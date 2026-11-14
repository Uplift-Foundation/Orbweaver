using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // This machine's identity to another copy of this app, and its memory of the
    // ones it has been paired with.
    //
    // Three things, and each answers a different question:
    //
    //  * **A self-signed certificate** — "is this connection private?" Both ends
    //    of a peer link are this app, so there is no certificate authority in the
    //    picture and nothing to buy. TLS is here for confidentiality and
    //    integrity, not for a name.
    //
    //  * **A SHA-256 pin of that certificate** — "is this the machine I paired
    //    with?" Identity is the fingerprint, exactly as OpenClawSocket already
    //    treats the gateway's self-signed cert, and for the same reason: a
    //    generated certificate has no meaningful subject to validate. Trust on
    //    first use, remembered from then on, and a *change* is refused rather
    //    than shrugged at, because that is the one shape an interception takes.
    //
    //  * **A pairing code** — "did a person agree to this?" Shown on the far
    //    machine and typed on this one, once. Without it, anything that can
    //    reach the port could ask for a transcript.
    //
    // The private key never leaves this file and the file never leaves this
    // machine, which is the same promise OpenClawIdentity makes about its
    // keypair — and this deliberately mirrors that file's layout so there is one
    // shape to learn rather than two.
    internal static class PeerIdentity
    {
        // Alongside settings.json rather than in the temp directory. An identity
        // that vanished on reboot would ask the user to re-pair every machine
        // every time, which is the one part of this that cannot be automatic.
        private static string Path_ =>
            System.IO.Path.Combine(OrbweaverSettings.Directory, "peer-identity.json");

        private static readonly object Gate = new();
        private static Stored? _cached;

        // The certificate is stored as PKCS#12 rather than a keypair, because
        // unlike OpenClaw's raw Ed25519 keys this one has to be handed to
        // SslStream, which wants an X509Certificate2 with a private key attached.
        internal sealed record Stored(
            [property: JsonPropertyName("pkcs12")] string Pkcs12Base64,
            [property: JsonPropertyName("peers")] Dictionary<string, Peer> Peers);

        // A machine we have paired with. `Pin` is the identity; `Machine` is only
        // ever shown to a person.
        internal sealed record Peer(
            [property: JsonPropertyName("pin")] string Pin,
            [property: JsonPropertyName("machine")] string Machine,
            [property: JsonPropertyName("address")] string? Address = null);

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // --- this machine --------------------------------------------------------

        // **Loaded once, never under Gate, and never waited on forever (CB-301).**
        //
        // This used to re-import the PKCS#12 on every call, inside `lock (Gate)`
        // — on every announce and every handshake. On macOS an import with a
        // private key is a keychain operation: .NET creates a fresh temporary
        // keychain per import (measured, ~130 ms and one `<guid>.keychain` file
        // in the temp directory each, none of them in the login keychain), and
        // `EphemeralKeySet`, which would avoid that, throws
        // PlatformNotSupportedException there. So one import that waited on a
        // SecurityAgent prompt held Gate indefinitely, and Peers(), PeerFor()
        // and the dial loop all queued behind it: the whole peer link froze,
        // with no log line, on the MacBook on 2026-10-30.
        //
        // Now: one load per process, started outside Gate through `Loader`,
        // shared by every caller as a single task. A caller waits at most
        // `wait`; a stall is logged and that caller gets a TimeoutException,
        // which both handshakes already catch and log. A stall does *not* start
        // a second load — that would only queue another keychain operation
        // behind the stuck one. A load that fails is logged and forgotten, so
        // the next caller tries again.
        //
        // The instance is never disposed: it is handed to every SslStream for
        // the life of the process, and disposing it would also delete the temp
        // keychain its key lives in.
        internal static Func<byte[], X509Certificate2> Loader = DefaultLoader;

        private static X509Certificate2 DefaultLoader(byte[] pkcs12) =>
            X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.Exportable);

        // Guards only the swap of `_load`. Never held across a load, and never
        // taken while holding Gate.
        private static readonly object LoadGate = new();
        private static Task<X509Certificate2>? _load;
        private static string? _pin;

        internal static async Task<X509Certificate2> CertificateAsync(
            TimeSpan wait, CancellationToken ct = default)
        {
            try
            {
                return await CurrentLoad().WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A fixed detail, so SayOnce collapses a long stall into one
                // line restated every few minutes rather than one per tick.
                MirrorLog.SayOnce("peer-identity-stalled", $"waited={wait.TotalSeconds:0.#}s still loading");
                throw;
            }
        }

        // Starts the load at launch, so it is done (or visibly stalled) before
        // the first handshake needs it and `peer-identity-loaded` lands in the
        // mirror-log beside `peer-listening`.
        internal static void Warm() =>
            _ = CertificateAsync(WarmWait).ContinueWith(
                t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        internal static readonly TimeSpan WarmWait = TimeSpan.FromSeconds(30);

        // What a handshake waits for the certificate before giving up on this
        // attempt. Long enough for a cold import (~130 ms measured, JIT
        // included); short enough that a stuck one costs a dial tick, not the
        // link. The load itself carries on, so the next tick may find it done.
        internal static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(10);

        internal static Task<X509Certificate2> HandshakeCertificateAsync(CancellationToken ct) =>
            CertificateAsync(HandshakeWait, ct);

        private static Task<X509Certificate2> CurrentLoad()
        {
            lock (LoadGate)
            {
                if (_load is not null) return _load;

                var loader = Loader;
                var load = Task.Run(() => LoadNow(loader));
                _load = load;

                // Forgotten on failure so the next caller retries. Compared by
                // identity because ForgetForTests may already have replaced it.
                _ = load.ContinueWith(
                    failed => { lock (LoadGate) if (ReferenceEquals(_load, failed)) _load = null; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

                return load;
            }
        }

        private static X509Certificate2 LoadNow(Func<byte[], X509Certificate2> loader)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var certificate = loader(StoredPkcs12());
                MirrorLog.Say("peer-identity-loaded", $"ms={started.ElapsedMilliseconds}");
                return certificate;
            }
            catch (Exception ex)
            {
                MirrorLog.SayOnce("peer-identity-load-failed", $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        // The stored PKCS#12, creating the identity on first use. File I/O and
        // (once ever) RSA keygen under Gate, as before — neither touches a
        // keychain. Only the import moved out.
        private static byte[] StoredPkcs12()
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                return Convert.FromBase64String(_cached.Pkcs12Base64);
            }
        }

        // What the far side will pin us by.
        //
        // Read straight out of the PKCS#12 bytes rather than through a loaded
        // certificate: the pin is a hash of the public certificate and needs
        // no private key, so it has no business importing one. It used to
        // import one every announce, which is how the announce loop came to be
        // one of the two threads found holding the keychain.
        internal static string OwnPin()
        {
            if (Volatile.Read(ref _pin) is { } pin) return pin;

            pin = PinOfDer(CertificateDerOf(StoredPkcs12()));
            Volatile.Write(ref _pin, pin);
            return pin;
        }

        // The DER of the certificate that carries the private key in a PKCS#12,
        // parsed by BouncyCastle so that no OS key store is involved at all.
        // Byte-identical to X509Certificate2.RawData for the same blob, which is
        // what keeps the pin the same pin it always was.
        internal static byte[] CertificateDerOf(byte[] pkcs12)
        {
            var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();

            using (var stream = new MemoryStream(pkcs12))
                store.Load(stream, Array.Empty<char>());

            foreach (var alias in store.Aliases)
                if (store.IsKeyEntry(alias)) return store.GetCertificate(alias).Certificate.GetEncoded();

            throw new CryptographicException("the stored identity has no certificate with a private key");
        }

        // The fingerprint of a certificate, as lowercase hex of the SHA-256 over
        // its DER bytes.
        //
        // The same computation OpenClawSocket.PinnedAuthentication performs on
        // the gateway's leaf, written the same way so the two are recognisably
        // one idea. Public so a test can assert a pin without a connection.
        internal static string PinOf(X509Certificate2 certificate) => PinOfDer(certificate.RawData);

        private static string PinOfDer(byte[] der) => Convert.ToHexStringLower(SHA256.HashData(der));

        // --- the machines we have paired with -------------------------------------

        internal static IReadOnlyDictionary<string, Peer> Peers()
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                return new Dictionary<string, Peer>(_cached.Peers, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static Peer? PeerFor(string machine)
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                return _cached.Peers.TryGetValue(machine, out var peer) ? peer : null;
            }
        }

        internal static void Remember(Peer peer)
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                _cached.Peers[peer.Machine] = peer;
                Save(_cached);
            }
        }

        internal static void Forget(string machine)
        {
            lock (Gate)
            {
                _cached ??= Load() ?? Create();
                if (_cached.Peers.Remove(machine)) Save(_cached);
            }
        }

        // Whether a certificate offered by `machine` is the one we paired with.
        //
        // Pure, and the reason it is: this is the security decision of the whole
        // feature, and it should be assertable without a socket, a certificate
        // store or a second machine. The three arms are the whole of it — never
        // seen it (refuse; pairing is a deliberate act), seen it and it matches
        // (accept), seen it and it does not (refuse, loudly).
        internal static bool Trusts(Peer? known, string offeredPin) =>
            known is not null
            && !string.IsNullOrWhiteSpace(offeredPin)
            && string.Equals(known.Pin, offeredPin, StringComparison.OrdinalIgnoreCase);

        // --- pairing --------------------------------------------------------------

        // A short code a person reads off one screen and types into another.
        //
        // Six digits, from a cryptographic source rather than Random: it is
        // short-lived and low-entropy by design, so the entropy it does have
        // should at least be real. Digits only because it is read aloud across a
        // room as often as it is copied.
        internal static string NewPairingCode()
        {
            Span<byte> bytes = stackalloc byte[4];
            RandomNumberGenerator.Fill(bytes);

            return (BitConverter.ToUInt32(bytes) % 1_000_000).ToString("D6");
        }

        // Compared without leaking how much of it matched.
        //
        // A six-digit code guessed one digit at a time is a thousand tries
        // rather than a million, and an ordinary string compare returns as soon
        // as it disagrees. Fixed-time is cheap here and the alternative is a
        // real weakening of the only thing standing between a stranger on the
        // network and a transcript.
        internal static bool CodeMatches(string? expected, string? offered)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(offered)) return false;
            if (expected.Length != offered.Length) return false;

            var difference = 0;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ offered[i];

            return difference == 0;
        }

        // --- disk -----------------------------------------------------------------

        // A test seam, and the only one this file needs: everything else is
        // deterministic given a certificate and a directory, and the directory
        // already moves with ORBWEAVER_SETTINGS_DIR.
        //
        // Also forgets the loaded certificate and the pin, which are derived
        // from `_cached` and would otherwise outlive it. The old certificate is
        // not disposed: a parallel test may still hold it in a live SslStream.
        internal static void ForgetForTests()
        {
            lock (Gate) _cached = null;
            lock (LoadGate) _load = null;
            Volatile.Write(ref _pin, null);
        }

        // Swaps the loader for one test, and puts it back — and forgets any
        // load made with either — when the returned handle is disposed.
        internal static IDisposable UseLoaderForTests(Func<byte[], X509Certificate2> loader)
        {
            var previous = Loader;
            Loader = loader;
            lock (LoadGate) _load = null;

            return new Restore(() =>
            {
                Loader = previous;
                lock (LoadGate) _load = null;
            });
        }

        private sealed class Restore(Action undo) : IDisposable
        {
            public void Dispose() => undo();
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static Stored? Load()
        {
            try
            {
                if (!File.Exists(Path_)) return null;

                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(Path_), Json);

                return stored is null || string.IsNullOrWhiteSpace(stored.Pkcs12Base64)
                    ? null
                    : stored;
            }
            catch
            {
                // A corrupt identity is replaced rather than fatal. The cost is
                // re-pairing; the alternative is an app that will not start.
                return null;
            }
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static Stored Create()
        {
            using var key = RSA.Create(2048);

            var request = new CertificateRequest(
                $"CN={MachineNames.Mine()}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // Ten years, because this certificate is not trusted for being
            // unexpired — it is trusted for being the one that was pinned. An
            // expiry would only mean re-pairing every machine on a timer.
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            var stored = new Stored(
                Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12)),
                new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase));

            Save(stored);
            return stored;
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static void Save(Stored stored)
        {
            try
            {
                Directory.CreateDirectory(OrbweaverSettings.Directory);
                File.WriteAllText(Path_, JsonSerializer.Serialize(stored, Json));

                // Owner-only, the same as openclaw-devices.json. A no-op concept
                // on Windows, where the call is simply not made rather than
                // branched on behaviour — the file lives in the user's own
                // AppData and inherits its protection from there.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // An identity that cannot be written still works for this run.
                // The user re-pairs next launch, which is better than refusing
                // to run at all.
            }
        }
    }
}
