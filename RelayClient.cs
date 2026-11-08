using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Orbweaver
{
    // The desktop's one outbound connection to the phone relay (CB-314 plan
    // §4): registration over HTTPS, then a single WebSocket that RelayLink
    // speaks through. Everything this file decides is about the connection —
    // when to register, when to reconnect and how long to wait, when a socket
    // has gone silent, which frames go out on which socket. Everything about
    // the protocol is RelayLink's, and is tested there without a socket.
    //
    // Threads: the scan calls OnScanned on the UI thread, the read loop and
    // the ping run on the pool, and the pairing UI calls Allow/Deny/Remove on
    // the UI thread. All of them touch RelayLink under one lock, and none of
    // them sends directly: frames go into the current connection's outbox and
    // one writer per connection sends them in order, so a socket never sees
    // two concurrent sends. Events this raises are raised outside that lock,
    // on whatever thread produced them — the UI marshals.
    internal sealed class RelayClient : IDisposable, IPairingClient
    {
        internal const string DefaultHost = "orbweaver-relay.uplifttech.org";

        internal enum Status { Off, Registering, Connecting, Connected, Refused }

        internal sealed record Options(
            Uri Base,
            string Platform,
            string AppVersion,
            string DesktopName)
        {
            internal TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);
            internal TimeSpan Silence { get; init; } = TimeSpan.FromSeconds(60);
            internal TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);
            internal TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

            internal Uri SocketUri => new UriBuilder(Base)
            {
                Scheme = Base.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
                Path = "/v1/socket",
            }.Uri;

            internal string RelayHost => Base.IsDefaultPort ? Base.Host : $"{Base.Host}:{Base.Port}";
        }

        // Where the relay is. The override exists so a test, or QA's stub
        // relay, can point a real build somewhere it controls — the same
        // shape as the updater's feed override.
        internal static Uri BaseFrom(string? overrideUrl) =>
            Uri.TryCreate(overrideUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
                ? u
                : new Uri("https://" + DefaultHost);

        // Doubling from the floor to the ceiling, so a relay that is down is
        // asked about once a minute rather than hammered.
        internal static TimeSpan Backoff(int attempt, TimeSpan min, TimeSpan max)
        {
            var ms = min.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 16));
            return TimeSpan.FromMilliseconds(Math.Min(ms, max.TotalMilliseconds));
        }

        private readonly RelayIdentity _identity;
        private readonly Options _options;
        private readonly IRelayTransport _transport;
        private readonly HttpClient _http;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly RelayLink _link;
        private readonly object _gate = new();
        private readonly List<Action> _deferred = new();
        private Channel<string>? _outbox;
        private CancellationTokenSource? _run;
        private CancellationTokenSource? _connection;
        private Task? _loop;
        private bool _unauthorized;
        private string? _refusedCode;

        internal RelayClient(RelayIdentity identity, Options options, IRelayTransport transport,
            HttpMessageHandler? http = null, Func<DateTimeOffset>? now = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _identity = identity;
            _options = options;
            _transport = transport;
            _http = new HttpClient(http ?? new SocketsHttpHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
            _delay = delay ?? Task.Delay;
            _link = new RelayLink(identity, new RelaySessionBook(), now ?? (() => DateTimeOffset.UtcNow),
                options.Platform, options.AppVersion, options.RelayHost);

            // Raised inside RelayLink, under the lock: anything that reaches
            // outside this class is deferred until the lock is released.
            _link.WelcomeReceived += () => Defer(() => { SetStatus(Status.Connected); PhonesChanged(); });
            // Both arrive only from the read loop, under the lock, while a
            // connection is current — so _connection is set.
            _link.Unauthorized += () => { _unauthorized = true; _connection!.Cancel(); };
            _link.Refused += code => { _refusedCode = code; _connection!.Cancel(); };
            _link.PairRequested += r => Defer(() => PairRequested(r));
            _link.PhonePaired += _ => Defer(() => PhonesChanged());
            _link.PhoneRemoved += _ => Defer(() => PhonesChanged());
            _link.PresenceChanged += () => Defer(() => PhonesChanged());
            _link.Note += note => MirrorLog.Say("relay", note);
        }

        // ---- what the UI sees ------------------------------------------------

        internal Status State { get; private set; } = Status.Off;
        internal string? LastError { get; private set; }

        internal event Action StateChanged = delegate { };
        internal event Action PhonesChanged = delegate { };
        public event Action<RelayLink.PairRequest> PairRequested = delegate { };

        internal IReadOnlyList<RelayIdentity.Phone> Phones => _identity.Phones;

        internal bool IsOnline(string phoneId) { lock (_gate) return _link.IsOnline(phoneId); }
        internal DateTimeOffset? LastSeen(string phoneId) { lock (_gate) return _link.LastSeen(phoneId); }

        // ---- lifecycle ---------------------------------------------------------

        internal void Start()
        {
            lock (_gate)
            {
                if (_loop is not null) return;
                _refusedCode = null;
                _run = new CancellationTokenSource();
                var token = _run.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }

        internal async Task StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                loop = _loop;
                _run?.Cancel();
                _loop = null;
            }
            // RunAsync returns on cancellation rather than throwing.
            if (loop is not null) await loop.ConfigureAwait(false);
            SetStatus(Status.Off);
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _http.Dispose();
        }

        private async Task RunAsync(CancellationToken stop)
        {
            var attempt = 0;
            while (true)
            {
                try
                {
                    if (_identity.Token is null)
                    {
                        SetStatus(Status.Registering);
                        await RegisterAsync(stop).ConfigureAwait(false);
                    }
                    SetStatus(Status.Connecting);
                    await ConnectOnceAsync(stop, () => attempt = 0).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or HttpRequestException
                                           or RelayHttpException or OperationCanceledException or JsonException)
                {
                    LastError = ex.Message;
                    MirrorLog.Say("relay", $"connection ended: {ex.GetType().Name}: {ex.Message}");
                }

                if (_refusedCode is { } code)
                {
                    LastError = $"the relay refused this version ({code})";
                    SetStatus(Status.Refused);
                    return;
                }
                if (_unauthorized)
                {
                    // Re-register with the same identity key straight away: the
                    // relay answers with the same device id and a fresh token.
                    _unauthorized = false;
                    _identity.ClearToken();
                    continue;
                }

                SetStatus(Status.Connecting);
                try { await _delay(Backoff(attempt++, _options.MinBackoff, _options.MaxBackoff), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task ConnectOnceAsync(CancellationToken stop, Action welcomed)
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var token = connection.Token;
            await using var socket = await _transport.ConnectAsync(_options.SocketUri, token).ConfigureAwait(false);

            var outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            lock (_gate)
            {
                _connection = connection;
                _outbox = outbox;
                outbox.Writer.TryWrite(_link.Hello());
            }

            var writer = WriteLoopAsync(socket, outbox.Reader, token);
            var pinger = PingLoopAsync(outbox.Writer, token);
            try
            {
                while (true)
                {
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(token);
                    silence.CancelAfter(_options.Silence);
                    var text = await socket.ReceiveAsync(silence.Token).ConfigureAwait(false);
                    if (text is null) break;   // the relay closed the socket
                    Dispatch(() => _link.OnFrame(text));
                    if (State == Status.Connected) welcomed();
                }
            }
            finally
            {
                lock (_gate)
                {
                    _link.Disconnected();
                    _outbox = null;
                    _connection = null;
                }
                outbox.Writer.TryComplete();
                connection.Cancel();
                await Task.WhenAll(Quietly(writer), Quietly(pinger)).ConfigureAwait(false);
                RunDeferred();
                PhonesChanged();
            }
        }

        private static async Task Quietly(Task t)
        {
            try { await t.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ChannelClosedException) { }
        }

        private static async Task WriteLoopAsync(IRelaySocket socket, ChannelReader<string> outbox, CancellationToken token)
        {
            await foreach (var frame in outbox.ReadAllAsync(token).ConfigureAwait(false))
                await socket.SendAsync(frame, token).ConfigureAwait(false);
        }

        private async Task PingLoopAsync(ChannelWriter<string> outbox, CancellationToken token)
        {
            // Ends by cancellation, which the delay throws.
            while (true)
            {
                await _delay(_options.PingInterval, token).ConfigureAwait(false);
                outbox.TryWrite(RelayLink.Ping());
            }
        }

        // ---- the scan and the UI -------------------------------------------------

        internal void OnScanned(IReadOnlyList<RelaySession> rows) => Dispatch(() => _link.OnScan(rows));

        public async Task<RelayLink.PairOffer> BeginPairingAsync(CancellationToken token = default)
        {
            if (_identity.Token is null) await RegisterAsync(token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.Base, "/v1/offers"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _identity.Token);
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) _identity.ClearToken();
            var body = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var offerId = Str(body, "offer_id") ?? throw new RelayHttpException("the relay's offer had no id");
            var expires = ExpiresAt(body) ?? throw new RelayHttpException("the relay's offer had no expiry");
            lock (_gate) return _link.BeginPairing(offerId, expires);
        }

        public void CancelPairing() { lock (_gate) _link.CancelPairing(); }

        // False when nothing was sent — the request is stale, or the socket
        // is down and the request stays pending for another try.
        public bool Allow(RelayLink.PairRequest request) => Dispatch(() => _link.Allow(request, _options.DesktopName));

        public void Deny(RelayLink.PairRequest request) { lock (_gate) _link.Deny(request); }

        internal void Remove(string phoneId)
        {
            Dispatch(() => _link.Remove(phoneId));
            PhonesChanged();
        }

        // Runs one RelayLink call under the lock, queues what it produced on
        // the live connection, then raises events outside. RelayLink produces
        // frames only while welcomed, and welcome and the outbox are set and
        // cleared together under this lock, so there is always an outbox for
        // them. Returns whether anything was produced.
        private bool Dispatch(Func<IReadOnlyList<string>> call)
        {
            int count;
            lock (_gate)
            {
                var frames = call();
                count = frames.Count;
                foreach (var frame in frames) _outbox!.Writer.TryWrite(frame);
            }
            RunDeferred();
            return count > 0;
        }

        private void Defer(Action action) { lock (_gate) _deferred.Add(action); }

        private void RunDeferred()
        {
            List<Action> now;
            lock (_gate)
            {
                if (_deferred.Count == 0) return;
                now = new List<Action>(_deferred);
                _deferred.Clear();
            }
            foreach (var action in now) action();
        }

        private void SetStatus(Status status)
        {
            if (State == status) return;
            State = status;
            StateChanged();
        }

        // ---- registration (§2.1) -------------------------------------------------

        private async Task RegisterAsync(CancellationToken token)
        {
            var (id, ka) = _identity.Keys();
            var fields = new Dictionary<string, string>
            {
                ["platform"] = _options.Platform,
                ["app_version"] = _options.AppVersion,
                ["name"] = _options.DesktopName,
                ["id_pub"] = RelayCrypto.Hex(id.Public),
                ["ka_pub"] = RelayCrypto.Hex(ka.Public),
            };
            var first = await PostAsync("/v1/register", fields, token).ConfigureAwait(false);
            var challenge = Str(first, "challenge") ?? throw new RelayHttpException("the relay sent no challenge");

            fields["challenge"] = challenge;
            fields["sig"] = RelayCrypto.Base64Url(RelayCrypto.Sign(id, RelayCrypto.RegisterMessage(challenge)));
            var second = await PostAsync("/v1/register", fields, token).ConfigureAwait(false);
            var deviceId = Str(second, "device_id") ?? throw new RelayHttpException("the relay sent no device id");
            var issued = Str(second, "token") ?? throw new RelayHttpException("the relay sent no token");
            _identity.SetRegistration(deviceId, issued);
        }

        private async Task<JsonElement> PostAsync(string path, Dictionary<string, string> fields, CancellationToken token)
        {
            using var content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri(_options.Base, path), content, token).ConfigureAwait(false);
            return await ReadJsonAsync(response, token).ConfigureAwait(false);
        }

        private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (!response.IsSuccessStatusCode)
                throw new RelayHttpException($"the relay answered {(int)response.StatusCode}");
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var json = JsonDocument.Parse(text).RootElement;
            return json.ValueKind == JsonValueKind.Object ? json : throw new RelayHttpException("the relay's answer was not an object");
        }

        // Unix seconds or ISO-8601: the plan does not say which, so both.
        internal static DateTimeOffset? ExpiresAt(JsonElement body)
        {
            if (!body.TryGetProperty("expires_at", out var e)) return null;
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var secs)) return DateTimeOffset.FromUnixTimeSeconds(secs);
            if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) return t;
            return null;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    internal sealed class RelayHttpException(string message) : Exception(message);

    // The socket, as far as RelayClient cares: text frames in and out.
    internal interface IRelayTransport
    {
        Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token);
    }

    internal interface IRelaySocket : IAsyncDisposable
    {
        Task SendAsync(string frame, CancellationToken token);

        // The next text frame, or null once the far side has closed.
        Task<string?> ReceiveAsync(CancellationToken token);
    }

    // ClientWebSocket, with the spec's 64 KB frame ceiling enforced on the way
    // in: a larger message closes the socket rather than growing a buffer to
    // whatever a misbehaving relay sends.
    internal sealed class WebSocketRelayTransport : IRelayTransport
    {
        internal const int MaxFrame = 64 * 1024;

        public async Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token)
        {
            var ws = new ClientWebSocket();
            // The relay's ping is JSON (plan §2.1), so keep-alive at the
            // WebSocket layer would only be a second heartbeat.
            ws.Options.KeepAliveInterval = TimeSpan.Zero;
            try
            {
                await ws.ConnectAsync(uri, token).ConfigureAwait(false);
                return new Socket(ws);
            }
            catch
            {
                ws.Dispose();
                throw;
            }
        }

        private sealed class Socket(ClientWebSocket ws) : IRelaySocket
        {
            public Task SendAsync(string frame, CancellationToken token) =>
                ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, token);

            public async Task<string?> ReceiveAsync(CancellationToken token)
            {
                var buffer = new byte[MaxFrame];
                var length = 0;
                while (true)
                {
                    if (length == buffer.Length)
                    {
                        await CloseQuietly(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                        throw new WebSocketException("a relay frame exceeded 64 KB");
                    }
                    var result = await ws.ReceiveAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    length += result.Count;
                    if (result.EndOfMessage)
                        return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, length) : "";
                }
            }

            // The catch is for a socket that died between the state check and
            // the close — a race no test can schedule, so it is excluded.
            [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
            private async Task CloseQuietly(WebSocketCloseStatus status)
            {
                try { await ws.CloseOutputAsync(status, null, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
            }

            public async ValueTask DisposeAsync()
            {
                // CloseReceived too: when the relay closed first, answering its
                // Close completes the handshake instead of leaving it an EOF.
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await CloseQuietly(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                ws.Dispose();
            }
        }
    }
}
