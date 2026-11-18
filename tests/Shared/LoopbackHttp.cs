using System.Net;
using System.Net.Sockets;

namespace Orbweaver.Tests;

// A loopback HttpListener on a port nobody else holds, and a port nobody is
// listening on — without the probe-and-release race.
//
// The old pattern (CB-314, found by QA's full IntegrationTests run: seven
// "Address already in use" failures) bound a TcpListener to port 0, read the
// port, released it, then bound the HttpListener to it. Between the release
// and the bind, any test running in parallel could take the same port. A
// TcpListener can bind 0 and keep it, but HttpListener cannot bind 0 at all,
// so instead the listener binds a random port itself and retries on a
// collision: the port it ends up with is one it actually holds, from the
// first moment anyone learns its number.
internal static class LoopbackHttp
{
    internal static (HttpListener Listener, string Root) Start() =>
        Start(() => Random.Shared.Next(20000, 60000));

    // The port source is a seam so the retry can be driven deterministically:
    // a test hands it a port it already holds and then a free one, instead of
    // hoping a collision happens. Production callers use the overload above.
    internal static (HttpListener Listener, string Root) Start(Func<int> nextPort)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = nextPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://127.0.0.1:{port}");
            }
            catch (HttpListenerException)
            {
                // Close on the final failure too, so giving up leaks nothing.
                listener.Close();
                if (attempt >= 50) throw;
            }
        }
    }

    internal enum ConnectOutcome { Refused, Connected, TimedOut }

    // Classifies one connect attempt, bounded by `timeout`, so a SYN that is
    // silently dropped cannot be mistaken for a refusal. Only an RST
    // (ConnectionRefused) is Refused; any other socket error propagates.
    internal static async Task<ConnectOutcome> ClassifyAsync(Func<CancellationToken, Task> connect, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await connect(cts.Token).ConfigureAwait(false);
            return ConnectOutcome.Connected;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ConnectOutcome.Refused;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return ConnectOutcome.TimedOut;
        }
    }

    internal static async Task<ConnectOutcome> ProbeAsync(int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        return await ClassifyAsync(ct => client.ConnectAsync(IPAddress.Loopback, port, ct).AsTask(), timeout)
            .ConfigureAwait(false);
    }

    // A loopback port that refuses connections for as long as it is held, and
    // that nothing in this test process can take meanwhile.
    //
    // CB-365: the old shape bound a socket and never listened. Measured on
    // macOS 27 (osx-arm64), a connect to that port gets no answer until the
    // ~75s connect timeout, where a released port answers ECONNREFUSED in 0.0s.
    // Binding and then *connecting* the holder to a throwaway loopback listener
    // refuses on both macOS (0-45 ms) and Windows (~2s; every loopback refusal
    // on Windows takes about that long, which is why callers must bound the
    // wait generously and assert Refused rather than "fast").
    //
    // The port comes from 10000-19999: below Start()'s 20000-60000 and below
    // the ephemeral ranges, so no port-0 bind and no Start() in the suite can
    // land on it. Remaining gap: on macOS a second socket that names this exact
    // port can still bind it; nothing in the suite does, a foreign process could.
    internal sealed class RefusingPortHolder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Socket _holder;

        public int Port { get; }

        internal RefusingPortHolder()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            for (var attempt = 0; ; attempt++)
            {
                var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var port = Random.Shared.Next(10000, 20000);
                    holder.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    holder.Connect(_listener.LocalEndpoint);
                    _holder = holder;
                    Port = port;
                    return;
                }
                catch (SocketException) when (attempt < 50)
                {
                    holder.Dispose();
                }
                catch
                {
                    holder.Dispose();
                    _listener.Stop();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            _holder.Dispose();
            _listener.Stop();
        }
    }

    internal static RefusingPortHolder RefusingPort() => new();
}
