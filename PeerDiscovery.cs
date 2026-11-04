using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // Finding the other machine without anybody typing an address.
    //
    // **Hand-rolled UDP multicast rather than mDNS, and that is a deliberate
    // choice about Windows.** .NET has no built-in mDNS. Bonjour is a macOS
    // system service with no dependable equivalent on Windows, so a package
    // would mean a new dependency that behaves differently on each platform —
    // and this app ships on both, where "a feature works on Windows and macOS
    // or it is not a feature". What is actually needed is very small: say who
    // and where I am, and listen for others doing the same. That is a hundred
    // lines of UDP that behaves identically everywhere.
    //
    // Announcing carries no secret. The name of a machine and a port number are
    // what a peer needs to *attempt* a connection, and attempting one proves
    // nothing: the certificate pin and the pairing code are what decide whether
    // it is allowed, and neither is on this wire. Discovery is an address book,
    // not a key.
    //
    // Manual add-by-address stays alongside this, and is not a lesser path — it
    // is what covers a VPN, where multicast usually does not cross the tunnel.
    internal sealed class PeerDiscovery : IDisposable
    {
        // Administratively-scoped IPv4 multicast: routable within an
        // organisation but never off it, which is exactly the reach wanted. Not
        // 224.0.0.251, which is mDNS's and would put our traffic in everybody
        // else's Bonjour listeners.
        public static readonly IPAddress Group = IPAddress.Parse("239.192.76.66");

        public const int GroupPort = 7678;

        // Often enough that a machine coming back is noticed while somebody is
        // still looking at the panel; rare enough that it is invisible on a
        // network. A peer is forgotten after three missed announcements, so this
        // also sets how long a machine that has gone stays listed.
        public static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(65);

        // What a machine says about itself. Deliberately the minimum: a name to
        // show, a port to try, and a version so a future format can be ignored
        // rather than misread.
        internal sealed record Announcement(
            [property: JsonPropertyName("v")] int Version,
            [property: JsonPropertyName("machine")] string Machine,
            [property: JsonPropertyName("port")] int Port,
            [property: JsonPropertyName("pin")] string Pin);

        // A machine we have heard from, and when.
        internal sealed record Seen(string Machine, string Address, int Port, string Pin, DateTime At);

        private static readonly JsonSerializerOptions Json = new();

        private readonly object _gate = new();
        private readonly Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);

        private UdpClient? _socket;
        private CancellationTokenSource? _stopping;

        internal event Action? Changed;

        // --- what is out there -------------------------------------------------------

        internal IReadOnlyList<Seen> Peers()
        {
            lock (_gate) return Forget(_seen, DateTime.UtcNow);
        }

        // Which announcements are still worth believing.
        //
        // Pure so the rule can be tested without waiting out a real minute — the
        // repository has fixed four flakes that were wall-clock claims, and a
        // "does it disappear after 65 seconds" test would have been a fifth.
        internal static List<Seen> Forget(Dictionary<string, Seen> seen, DateTime now)
        {
            var stale = seen
                .Where(p => now - p.Value.At > ForgetAfter)
                .Select(p => p.Key)
                .ToList();

            foreach (var machine in stale) seen.Remove(machine);

            return seen.Values.OrderBy(p => p.Machine, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // --- reading an announcement ---------------------------------------------------

        // Null for anything that is not a well-formed announcement from a
        // version we understand, or that claims to be us.
        //
        // Pure, and the arms matter: this parses a datagram that anybody on the
        // network can send. Nothing here trusts it — it decides only whether a
        // name appears in a list — but a malformed one must be dropped rather
        // than half-read.
        internal static Seen? Read(byte[] datagram, string fromAddress, string ownMachine, DateTime now)
        {
            Announcement? announced;

            try
            {
                announced = JsonSerializer.Deserialize<Announcement>(datagram, Json);
            }
            catch
            {
                return null;
            }

            if (announced is null) return null;
            if (announced.Version != PeerProtocol.Version) return null;
            if (string.IsNullOrWhiteSpace(announced.Machine)) return null;
            if (announced.Port is <= 0 or > 65535) return null;

            // Our own announcement, arriving back at us: multicast is delivered
            // to the sender too, and a machine listing itself as a peer would be
            // an orb pointing at this computer.
            if (announced.Machine.Equals(ownMachine, StringComparison.OrdinalIgnoreCase)) return null;

            return new Seen(announced.Machine, fromAddress, announced.Port, announced.Pin ?? "", now);
        }

        internal static byte[] Say(string machine, int port, string pin) =>
            JsonSerializer.SerializeToUtf8Bytes(
                new Announcement(PeerProtocol.Version, machine, port, pin), Json);

        // Records what was heard, and says whether it was news.
        //
        // Separate from the socket so the bookkeeping — first sighting, a
        // machine that moved address, a repeat that changes nothing — can be
        // asserted directly. Only news raises Changed, because the UI redraws on
        // it and an announcement every twenty seconds from every machine would
        // otherwise be a redraw every twenty seconds.
        internal bool Note(Seen peer)
        {
            lock (_gate)
            {
                var news = !_seen.TryGetValue(peer.Machine, out var had)
                           || had.Address != peer.Address
                           || had.Port != peer.Port
                           || had.Pin != peer.Pin;

                _seen[peer.Machine] = peer;
                return news;
            }
        }

        // --- the socket ------------------------------------------------------------------

        [ExcludeFromCodeCoverage]
        internal void Start(int listenPort)
        {
            lock (_gate)
            {
                if (_socket is not null) return;

                _stopping ??= new CancellationTokenSource();

                // Published to the field only once fully set up. A throw from
                // Bind or JoinMulticastGroup used to leave a half-made socket
                // here, so a retry (CB-306) returned at the guard above and
                // discovery never announced.
                var socket = new UdpClient(AddressFamily.InterNetwork);

                try
                {
                    socket.Client.SetSocketOption(
                        SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Client.Bind(new IPEndPoint(IPAddress.Any, GroupPort));
                    socket.JoinMulticastGroup(Group);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

                _socket = socket;
            }

            _ = ListenAsync(_stopping!.Token);
            _ = AnnounceLoopAsync(listenPort, _stopping!.Token);
        }

        [ExcludeFromCodeCoverage]
        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var got = await _socket!.ReceiveAsync(ct).ConfigureAwait(false);

                    var peer = Read(
                        got.Buffer, got.RemoteEndPoint.Address.ToString(),
                        MachineNames.Mine(), DateTime.UtcNow);

                    if (peer is null) continue;
                    if (Note(peer)) Changed?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    MirrorLog.Say("discovery-read-failed", $"{ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }
        }

        [ExcludeFromCodeCoverage]
        private async Task AnnounceLoopAsync(int listenPort, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var say = Say(MachineNames.Mine(), listenPort, PeerIdentity.OwnPin());
                    await _socket!.SendAsync(say, new IPEndPoint(Group, GroupPort), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // On macOS this is where a missing Local Network grant
                    // surfaces, and it looks like an ordinary network error.
                    // Announcing fails every interval on a machine with no
                    // Local Network grant — a headless Mac, most of all, where
                    // there is nobody to give it one. Said once and restated
                    // periodically rather than every tick.
                    MirrorLog.SayOnce("discovery-announce-failed",
                        OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS()));
                }

                try { await Task.Delay(AnnounceEvery, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        [ExcludeFromCodeCoverage]
        public void Dispose()
        {
            CancellationTokenSource? stopping;
            UdpClient? socket;

            lock (_gate)
            {
                stopping = _stopping;
                socket = _socket;
                _stopping = null;
                _socket = null;
            }

            try { stopping?.Cancel(); } catch { }
            try { socket?.Dispose(); } catch { }
            stopping?.Dispose();
        }
    }
}
