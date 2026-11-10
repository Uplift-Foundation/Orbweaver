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
    internal sealed class RelayClient : IDisposable, IPairingClient, IPromptSender
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
            // A WebSocket connect has no timeout of its own, so one that hangs
            // (a SYN into the void) would stall the loop with nothing logged.
            internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

            // On in every real build. Off only for tools/relay-probe's burst
            // test of the relay's own limiter (--no-pacer).
            internal bool PaceFrames { get; init; } = true;
            internal TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

            // CB-323: the gate's activity log (ORBWEAVER_ACTIVITY_LOG), or none.
            internal RelayActivityLog? ActivityLog { get; init; } = RelayActivityLog.From(Environment.GetEnvironmentVariable);

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
        private readonly Func<DateTimeOffset> _now;
        private readonly RelayLink _link;
        private readonly object _gate = new();
        private readonly List<Action> _deferred = new();

        // Every event this client raises goes through one serial queue, in
        // order, on its own task — never on the read loop's thread. A handler
        // that blocks (or awaits something whose continuation runs inline)
        // used to run on the read loop and stall it: inbound frames stopped
        // being processed while outbound deltas kept flowing. That is exactly
        // how a resync went unanswered in CB-314's two-process end to end.
        private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _eventPump;
        private Channel<string>? _outbox;
        private CancellationTokenSource? _run;
        private CancellationTokenSource? _connection;
        private Task? _loop;
        private bool _unauthorized;

        // One registration at a time. The relay invalidates a device's old
        // token the moment the same identity key registers again, so two
        // concurrent registrations — the connection loop and the first
        // "Pair phone…" both finding no token — leave one of them holding a
        // dead token. Measured on the installed app: the first offer failed
        // 401 and the socket was refused, until a retry.
        private readonly SemaphoreSlim _registering = new(1, 1);
        private string? _refusedCode;

        internal RelayClient(RelayIdentity identity, Options options, IRelayTransport transport,
            HttpMessageHandler? http = null, Func<DateTimeOffset>? now = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null, PromptPipeline.Seams? promptSeams = null,
            RelayRemote.Seams? remoteSeams = null)
        {
            _identity = identity;
            _options = options;
            _transport = transport;
            _http = new HttpClient(http ?? new SocketsHttpHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
            _delay = delay ?? Task.Delay;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _eventPump = Task.Run(PumpEventsAsync);
            _link = new RelayLink(identity, new RelaySessionBook(), _now,
                options.Platform, options.AppVersion, options.RelayHost)
            {
                Machine = options.DesktopName,
                // CB-323: one boot per process, so a restarted app's seq 1 is
                // taken by the relay rather than refused as stale.
                Activity = new RelayActivity.Tracker(RelayActivity.NewBoot()),
            };

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
            _link.Note += note => { MirrorLog.Say("relay", note); Raise(() => Logged(note)); };
            _link.RetryDue += (key, attempt) => Defer(() => _ = RetryLaterAsync(key, attempt));

            // CB-315: the amber loop. The pipeline awaits captures and presses,
            // so it runs off the event pump (fire and forget, serialized inside
            // the pipeline) and never under the lock.
            Prompts = new PromptPipeline(promptSeams ?? DefaultPromptSeams(_now), this, options.DesktopName);
            // CB-323: the activity frame's oldest amber names its minted prompt.
            _link.ActivityPrompts = Prompts.ActivityFor;
            _link.ActivityDue += at => Defer(() => _ = FlushActivityLaterAsync(at));
            if (options.ActivityLog is { } activityLog)
            {
                _link.ActivityReplied += (reference, _, code) => activityLog.Reply(reference, code, _now());
                activityLog.Failed += line => MirrorLog.Say("relay", line);
            }
            // Every pending prompt goes out again on each welcome (see
            // ResendPendingAsync for why every one, not only the unsent).
            _link.WelcomeReceived += () => Defer(() => _ = Prompts.ResendPendingAsync());
            // CB-323: with the phone's accepted watch key, read now, under the lock,
            // so the gate judges the answer against the key as of its arrival.
            _link.AnswerReceived += (phone, idPub, answer) =>
            {
                var watch = _identity.WatchPublicOf(phone);
                Defer(() => _ = Prompts.OnAnswerAsync(phone, idPub, answer, watch));
            };

            // CB-316: watch / unwatch / input / launch, carried out off the
            // event pump like an answer, never under the lock. Launches share
            // the answer gate's nonce table (one per phone, one hour).
            Remote = new RelayRemote(remoteSeams ?? DefaultRemoteSeams(_now), new RemoteSender(this), Prompts.Nonces);
            _link.RequestReceived += (phone, idPub, request) => Defer(() => _ = HandleQuietly(phone, idPub, request));
            _link.PhoneWentOffline += phone => Defer(() => Remote.PhoneOffline(phone));
            _link.SessionFinished += sessionId => Defer(() => _ = SummarizeAsync(sessionId));
            _link.PushAcked += (phone, id, atMs) =>
            {
                MirrorLog.Say("relay", $"push-ack {id} from {phone} at_ms={atMs}");
                Defer(() => PushAcked(phone, id, atMs));
            };
            _link.PushReplied += (id, code, ms) =>
            {
                MirrorLog.Say("relay", $"push {id} {code}{(ms is { } n ? $" apns_ms={n}" : "")}");
                Defer(() => PushReplied(id, code, ms));
            };
        }

        internal PromptPipeline Prompts { get; }

        internal RelayRemote Remote { get; }

        // A request's own failure is logged and costs that request only.
        private async Task HandleQuietly(string phone, byte[] idPub, RelayRequest request)
        {
            try { await Remote.HandleAsync(phone, idPub, request).ConfigureAwait(false); }
            catch (Exception ex) { MirrorLog.Say("relay", $"request {RelayLink.Safe(request.Ref)} threw {ex.GetType().Name}"); }
        }

        private sealed class RemoteSender(RelayClient client) : IRemoteSender
        {
            public void Reply(string phoneId, string? sessionId, string plaintext) =>
                client.Dispatch(() => client._link.SendReply(phoneId, sessionId, plaintext));

            public void Window(string phoneId, string sessionId, string plaintext) =>
                client.Dispatch(() => client._link.SendWindow(phoneId, sessionId, plaintext));

            public bool ImageWindow(string phoneId, string hash, string plaintext) =>
                client.Dispatch(() => client._link.SendImageWindow(phoneId, hash, plaintext));
        }

        // The real desk: the session's own transcript and parser, the
        // terminal routes and screen readers the amber loop already uses, the
        // fresh CLI locate New Chat uses, tmux on the Mac and conhost on
        // Windows. Excluded: each is one line into something that runs real
        // processes; RelayRemote, RelayInput and LaunchGate are tested.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static RelayRemote.Seams DefaultRemoteSeams(Func<DateTimeOffset> now) => new(
            now,
            Task.Delay,
            TranscriptWatcher.For,
            new RelayInput(new RelayInput.Seams(
                TerminalFocuser.RouteFor,
                status => ScreenReaders.For(status)?.ReadAsync(status) ?? Task.FromResult<string?>(null),
                TerminalFocuser.SendTextAndSubmit,
                Task.Delay,
                TerminalFocuser.TmuxPaneHolds)),
            new RelayRemote.LaunchSeams(
                OperatingSystem.IsWindows(),
                cli => NewChatLauncher.LocateFresh(LaunchGate.CliFor(cli)),
                Directory.Exists,
                () => Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/zsh",
                TerminalLauncher.PlaceForPhone,
                TerminalLauncher.StartConsole),
            line => MirrorLog.Say("relay", line))
        {
            // CB-321: pictures are answered only from a finished bundle.
            Images = hash => PersonaImageStore.Shared.ById(hash),
            Accounts = new RelayRemote.AccountSeams(RelayAccounts.Choices, OrbweaverSettings.PhoneLaunchProfileFor),
            Summarize = RelayAccounts.Summarize,
            Settle = () => Task.Delay(500),
        };

        // CB-316: the account readings the usage orbs just drew. Projected,
        // compared with what was last sent, and sent only when a number a phone
        // would draw changed — or once per account that crossed 80%, with the
        // limit push. Called on the UI thread after each poll.
        internal void OnUsage(IReadOnlyList<RelayUsage.Account> accounts)
        {
            IReadOnlyList<RelayUsageTracker.Send> sends;
            lock (_gate) sends = _usage.Next(accounts);
            foreach (var send in sends)
                Dispatch(() => _link.SendUsage(send.Plaintext, send.Crossing));
        }

        private readonly RelayUsageTracker _usage = new();

        // The relay's answer to each amber (request id, pushed / push_throttled /
        // push_no_token, APNs ms): gate (a) timing, and the proof a repeat on
        // reconnect was throttled rather than alerted twice.
        internal event Action<string, string, int?> PushReplied = delegate { };

        // Gate (a)'s far end: the phone's notification extension saw the amber.
        internal event Action<string, string, long> PushAcked = delegate { };

        // The real panes. Excluded: each is one line into TerminalFocuser,
        // whose tmux calls a headless runner must not make.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static PromptPipeline.Seams DefaultPromptSeams(Func<DateTimeOffset> now) => new(
            status => ScreenReaders.For(status)?.ReadAsync(status) ?? Task.FromResult<string?>(null),
            TerminalFocuser.PressKey, TerminalFocuser.TypeQuietly,
            now, Task.Delay, line => MirrorLog.Say("relay", line),
            // Only the routes that write without focus; Terminal.app and
            // anything else get "Open on desk".
            TerminalFocuser.CanPressSilently,
            TerminalFocuser.CaptureAndPress);

        // The whole scan, for the prompt watcher; the roster gets its
        // projection through OnScanned.
        // Nothing is captured for a desktop with no phone to send a prompt to.
        internal Task OnStatuses(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            Remote.OnStatuses(snapshot);
            return _identity.Phones.Count == 0 ? Task.CompletedTask : Prompts.OnScannedAsync(snapshot);
        }

        bool IPromptSender.Prompt(string requestId, string sessionId, string prompt, string? pushPlaintext) =>
            Dispatch(() => _link.SendPrompt(requestId, sessionId, prompt, pushPlaintext));

        // CB-321 addendum 2: the persona an amber for this session speaks as,
        // read off the link's book under the lock like everything else there.
        PushPersona? IPromptSender.PersonaFor(string sessionId) { lock (_gate) return _link.PersonaFor(sessionId); }

        void IPromptSender.Resolved(string requestId, string sessionId, string by, string? pushPlaintext) =>
            Dispatch(() => _link.SendResolved(requestId, sessionId, by, pushPlaintext));

        void IPromptSender.Refusal(string phoneId, string requestId, string sessionId, string code) =>
            Dispatch(() => _link.SendRefusal(phoneId, requestId, sessionId, code));

        // ---- what the UI sees ------------------------------------------------

        internal Status State { get; private set; } = Status.Off;
        internal string? LastError { get; private set; }

        internal event Action StateChanged = delegate { };
        internal event Action PhonesChanged = delegate { };
        public event Action<RelayLink.PairRequest> PairRequested = delegate { };

        // Every RelayLink note, on the event queue: what the mirror log gets,
        // for a tool that wants it on screen (relay-probe --desktop-only).
        internal event Action<string> Logged = delegate { };

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
                _ = Task.Run(() => Remote.RunAsync(token));
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
            _registering.Dispose();
            _events.Writer.TryComplete();
            _eventPump.Wait(TimeSpan.FromSeconds(5));
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
                        await EnsureRegisteredAsync(stop).ConfigureAwait(false);
                    }
                    SetStatus(Status.Connecting);
                    await ConnectOnceAsync(stop, () => attempt = 0).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or HttpRequestException
                                           or RelayHttpException or OperationCanceledException or JsonException
                                           or RelaySecretUnavailableException)
                {
                    LastError = ex.Message;
                    MirrorLog.Say("relay", $"connection ended: {ex.GetType().Name}: {ex.Message}");
                }
                // Anything else as well. An exception this loop did not list
                // used to escape it and end the client for good — no reconnect
                // and nothing logged, with the relay queueing answers for a
                // desktop that never came back. A bug thrown from a socket or a
                // frame handler costs one connection, not the client.
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    MirrorLog.Say("relay", $"connection ended unexpectedly: {ex.GetType().Name}: {RelayLink.Safe(ex.Message)}");
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
            IRelaySocket socket;
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connecting.CancelAfter(_options.ConnectTimeout);
                socket = await _transport.ConnectAsync(_options.SocketUri, connecting.Token).ConfigureAwait(false);
            }
            await using var _ = socket;

            var outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            lock (_gate)
            {
                _connection = connection;
                _outbox = outbox;
                outbox.Writer.TryWrite(_link.Hello());
            }

            var writer = WriteLoopAsync(socket, outbox.Reader, new RelayPacer(_now()), token);
            var pinger = PingLoopAsync(outbox.Writer, token);
            try
            {
                while (true)
                {
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(token);
                    silence.CancelAfter(_options.Silence);
                    var text = await socket.ReceiveAsync(silence.Token).ConfigureAwait(false);
                    if (text is null) break;   // the relay closed the socket
                    // What arrived, before anything decides what to do with it,
                    // so a frame lost after the read and one never read can be
                    // told apart from the log alone. Metadata only.
                    if (InboundKind(text) is { } kind) MirrorLog.Say("relay-in", kind);
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
                Remote.Disconnected();
                outbox.Writer.TryComplete();
                connection.Cancel();
                await Task.WhenAll(Quietly(writer), Quietly(pinger)).ConfigureAwait(false);
                RunDeferred();
                Raise(() => PhonesChanged());
            }
        }

        private static async Task Quietly(Task t)
        {
            try { await t.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ChannelClosedException) { }
        }

        // One writer per connection, in order, paced under the relay's frame
        // rate (RelayPacer says why). A frame over budget waits; none is dropped.
        private async Task WriteLoopAsync(IRelaySocket socket, ChannelReader<string> outbox, RelayPacer pacer, CancellationToken token)
        {
            await foreach (var frame in outbox.ReadAllAsync(token).ConfigureAwait(false))
            {
                var wait = _options.PaceFrames ? pacer.Reserve(_now()) : TimeSpan.Zero;
                if (wait > TimeSpan.Zero) await _delay(wait, token).ConfigureAwait(false);
                await socket.SendAsync(frame, token).ConfigureAwait(false);
                // CB-323: logged as sent once it is, not when it was queued.
                if (_options.ActivityLog is { } log && RelayActivityLog.IsActivityFrame(frame)) log.Sent(frame, _now());
            }
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

        // A frame the relay refused for rate goes again after a backoff that
        // doubles from a quarter second, plus up to a quarter second of
        // jitter so a crowd of refused frames does not return in step.
        internal static TimeSpan RetryDelay(int attempt, double jitter01) =>
            Backoff(attempt - 1, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(4)) + TimeSpan.FromMilliseconds(250 * jitter01);

        private async Task RetryLaterAsync(string key, int attempt)
        {
            // A retry only comes out of a frame a running client read, so the
            // run's token exists; StopAsync cancels it and the wait ends.
            var stop = _run!.Token;
            try { await _delay(RetryDelay(attempt, Random.Shared.NextDouble()), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Dispatch(() => _link.Retry(key));
        }

        // CB-323: a debounced activity frame, sent when its interval ends with
        // the snapshot as it is then. Raised only while connected, so the run
        // exists; a stop or a lost socket meanwhile sends nothing (the link
        // checks it is welcomed, and the next welcome sends a snapshot anyway).
        private async Task FlushActivityLaterAsync(DateTimeOffset at)
        {
            var wait = at - _now();
            if (wait > TimeSpan.Zero)
            {
                try { await _delay(wait, _run!.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            Dispatch(() => _link.FlushActivity());
        }

        // ---- the scan and the UI -------------------------------------------------

        internal void OnScanned(IReadOnlyList<RelaySession> rows) => Dispatch(() => _link.OnScan(rows));

        // CB-321: the personas frame's plaintext, from each scan. The link sends
        // it only when it changed. Addendum 2: and the default persona it names,
        // which the link keeps for limit pushes.
        internal void OnPersonas(string plaintext, PushPersona? defaultPersona = null) =>
            Dispatch(() => _link.SendPersonas(plaintext, defaultPersona));

        // The accounts a launch may name, from each scan; the link sends it only when it changed.
        internal void OnAccounts(string plaintext) => Dispatch(() => _link.SendAccounts(plaintext));

        // A session finished: its summary, to the phones watching it. Off the event pump, because
        // the summariser is a model call; a failure costs that summary only.
        private async Task SummarizeAsync(string sessionId)
        {
            try
            {
                if (await Remote.SummaryForAsync(sessionId).ConfigureAwait(false) is not { } summary) return;
                var frame = RelayAccounts.SummaryFrame(sessionId, summary.Text, _now().ToUnixTimeSeconds());
                Dispatch(() => _link.SendSummary(sessionId, frame, summary.Phones));
            }
            catch (Exception e) { MirrorLog.Say("relay", $"phone summary failed: {e.GetType().Name}"); }
        }

        public async Task<RelayLink.PairOffer> BeginPairingAsync(CancellationToken token = default)
        {
            await EnsureRegisteredAsync(token).ConfigureAwait(false);
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
            Raise(() => PhonesChanged());
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
            lock (_gate)
            {
                foreach (var action in _deferred) _events.Writer.TryWrite(action);
                _deferred.Clear();
            }
        }

        private void Raise(Action action) => _events.Writer.TryWrite(action);

        private async Task PumpEventsAsync()
        {
            await foreach (var action in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // A handler's exception is the handler's bug; it must not end
                // the pump and with it every later event.
                try { action(); }
                catch (Exception ex) { MirrorLog.Say("relay", $"event handler threw {ex.GetType().Name}"); }
            }
        }

        private void SetStatus(Status status)
        {
            if (State == status) return;
            State = status;
            Raise(() => StateChanged());
        }

        // ---- registration (§2.1) -------------------------------------------------

        // Registers unless someone else already did while this waited.
        private async Task EnsureRegisteredAsync(CancellationToken token)
        {
            await _registering.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_identity.Token is null) await RegisterAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _registering.Release();
            }
        }

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

            var raw = RelayCrypto.FromBase64Url(challenge) ?? throw new RelayHttpException("the relay's challenge was not base64url");
            fields["challenge"] = challenge;
            fields["sig"] = RelayCrypto.Base64Url(RelayCrypto.Sign(id, RelayCrypto.RegisterMessage(raw)));
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

        // A frame's kind for the inbound log: the control frame's t (with its
        // peer or device id when it has one), or an envelope's class and
        // sender — never ct, never a payload. Null for the heartbeat, which
        // would drown everything else, and for anything that is not JSON.
        internal static string? InboundKind(string text)
        {
            try
            {
                var e = JsonDocument.Parse(text).RootElement;
                if (e.ValueKind != JsonValueKind.Object) return null;
                // Everything echoed is filtered (RelayLink.Known/Safe): a relay
                // cannot forge a line, flood the log, or smuggle a payload in.
                if (Str(e, "t") is { } t)
                {
                    if (t is "ping" or "pong") return null;
                    var about = Str(e, "peer_id") ?? Str(e, "device_id");
                    var code = Str(e, "code");
                    return $"t={RelayLink.Known(t, RelayLink.ControlKinds)}"
                           + (about is null ? "" : $" peer={RelayLink.Safe(about)}")
                           + (code is null ? "" : $" code={RelayLink.Safe(code)}");
                }
                return e.TryGetProperty("v", out _)
                    ? $"envelope class={RelayLink.Known(Str(e, "class"), RelayLink.Classes)} from={RelayLink.Safe(Str(e, "from"))}"
                    : null;
            }
            catch (JsonException) { return null; }
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
