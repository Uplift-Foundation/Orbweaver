using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace Orbweaver
{
    // One local CLI session — Claude Code or Codex — as something the chat panel
    // can talk to.
    //
    // The thing worth understanding before reading any of this: **there is only
    // one conversation, and this is not a copy of it.** Both CLIs write every
    // session's transcript to a JSONL file and the hook already records where
    // (SessionStatus.TranscriptPath). That file is the conversation. This class
    // tails it, so anything typed in the terminal appears in the panel; and
    // sending goes through tmux into the terminal's own input line, so anything
    // sent from the panel appears in the terminal. Neither surface owns a copy
    // and there is nothing to reconcile — which is why there is no sync code
    // here, only a reader and a writer pointed at the same place.
    //
    // What differs between the two CLIs is small and lives in CliChatFormat: how
    // a line of their transcript maps to a turn, and which settings gate reading
    // and replying. Everything else here is *transcript-shaped-file* machinery
    // rather than Claude Code machinery — the byte offsets, the carry buffer so a
    // write landing mid-codepoint cannot leave a permanent replacement character,
    // the watcher-plus-poll pair because macOS FileSystemWatcher misses JSONL
    // appends, the window sizes measured across six real transcripts — and Codex
    // needs all of it unchanged, including the giant-row case: the largest single
    // row measured in a real rollout is 1,046,104 bytes.
    //
    // Two ways this differs from OpenClawChatSession, both consequences of the
    // transcript being a file rather than an event stream:
    //
    //  * Updates arrive per *block*, not per token. A row is appended when a
    //    thinking pass, a tool call or a paragraph completes, so the panel runs
    //    a few seconds behind the terminal's own streaming. Blocks are still
    //    fine-grained enough to watch a session work, which is most of the
    //    point; nothing here pretends to be faster than it is.
    //  * Nothing is ever mutated in place, so TurnUpdated is raised only when a
    //    message sent from the panel is reconciled against the transcript row it
    //    produced. The contract's "TurnUpdated carries the whole turn" holds
    //    trivially, since the whole turn is all there ever is.
    internal sealed class LocalCliChatSession :
        IRemoteChatSession, IRemoteChatBacklog, IRemoteChatComposer, IRemoteChatPrompts,
        IRemoteChatImages, IRemoteChatSlashCommands, IRemoteChatElsewhere, IRemoteChatReadOnly,
        IRemoteChatPeerInbound, IDisposable
    {
        // How much of the tail to read when the panel first opens.
        //
        // Sized by measurement, not by taste, because the answer is nothing like
        // what a count of turns suggests. Almost all of a transcript's bytes are
        // tool results and file-history snapshots, none of which is shown, so
        // the conversation is a thin seam through a very large file — and how
        // thin varies hugely with what the session was doing. Across six real
        // transcripts (0.6MB to 33MB), 64KB of tail yielded between **1 and 16**
        // displayable turns; 512KB yielded 14 to 86.
        //
        // So 64KB, which sounds generous for a panel showing a dozen rows, opens
        // some sessions on a single line. Half a megabyte is the point where
        // every transcript measured had more than a screenful, and it parses on
        // a worker thread in well under the time the window takes to appear.
        private const int InitialBytes = 512 * 1024;

        // Larger than the initial read for the same reason: in a tool-heavy
        // transcript a small page can step back through hundreds of kilobytes
        // and surface almost nothing, and doing that four times to fill a screen
        // is four round trips the reader can feel.
        private const int PageBytes = 1024 * 1024;

        // Same reasoning and same number as OpenClawChatSession.Add: high enough
        // that reaching it means a genuinely enormous scrollback rather than an
        // ordinary afternoon.
        private const int KeepTurns = 500;

        private readonly List<ChatTurn> _history = new();

        // Rows already turned into turns. Windows are disjoint by construction —
        // the backlog reads strictly below where the initial read started, and
        // the live tail strictly above where it ended — so this is a guard
        // rather than the mechanism, and cheap enough to keep as one.
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private SessionStatus _status;

        // Which CLI's transcript this is tailing. Set once from the status the
        // session was created with and never re-resolved: a session does not
        // change CLI, and re-reading it per pump would invite a format swap
        // halfway through a file.
        private readonly CliChatFormat _format;

        private string _transcriptPath = "";

        // Byte offsets into the transcript. _offset is where the live tail
        // resumes; _backlogFrom is the line-aligned start of the oldest window
        // read so far, and reaching zero is what ends paging.
        private long _offset;
        private long _backlogFrom;

        // Bytes of a trailing line the writer hadn't finished when we read. Kept
        // as bytes rather than a string on purpose: a write can land mid
        // codepoint, and decoding half of one produces a replacement character
        // that never heals.
        private readonly List<byte> _carry = new();

        private DeferredWatcher? _watcher;
        private DispatcherTimer? _poll;
        private DispatcherTimer? _debounce;
        private bool _pumping;
        private bool _started;

        // Set when the opening read has been applied. The live tail must not run
        // before that — see Pump.
        private bool _loaded;

        // findTranscript is a seam for the same reason SessionManager's
        // transcriptHunt is: the real one walks this machine's own projects
        // directories, and the decision worth testing is when Start falls back
        // to it, not what the walk finds.
        //
        // messenger and findRegistry are the same kind of seam for the
        // delivery path CB-105 adds: the real messenger writes to a live Unix
        // socket and the real registry lookup walks every Claude config root
        // on disk, and what is worth testing is what this class does with a
        // receipt or a found-or-not entry, not the socket or the walk
        // themselves. Both default to a real one, matching findTranscript's
        // own pattern — production never passes either.
        public LocalCliChatSession(string sessionId, SessionStatus status,
                                   Func<string, string?>? findTranscript = null,
                                   SessionMessenger? messenger = null,
                                   Func<string, SessionRegistry.Entry?>? findRegistry = null,
                                   Func<string, string, FileSystemWatcher>? startWatcher = null,
                                   ICloudApi? cloudApi = null,
                                   Func<string?, ICloudCredentialSource>? credentialsFor = null,
                                   Func<bool>? cloudEnabled = null,
                                   Func<SessionStatus, bool>? focusDesktop = null,
                                   Action<SessionStatus, string>? focusTerminal = null,
                                   Func<string, string?>? readSettings = null,
                                   Func<string, bool, PeerInboundWrite>? applySetting = null)
        {
            SessionId = sessionId;
            _status = status;
            _format = CliChatFormat.For(status.Source);
            DisplayName = status.Title ?? "";
            _findTranscript = findTranscript ?? (id => TranscriptReader.FindTranscriptFor(id));
            _messenger = messenger ?? new SessionMessenger(SessionMessenger.Live(ClaudeConfigRoots.All()));
            _findRegistry = findRegistry ?? (id => SessionRegistry.Find(
                SessionRegistry.Scan(ClaudeConfigRoots.All()), id, ProcessLiveness.IsRunning));
            _startWatcher = startWatcher ?? StartRealWatcher;
            _cloudApi = cloudApi;
            _credentialsFor = credentialsFor;
            _cloudEnabled = cloudEnabled ?? (() => OrbweaverSettings.ClaudeCloudEnabled);
            _focusDesktop = focusDesktop ?? FocusDesktopWindow;
            _focusTerminal = focusTerminal ?? FocusTerminalWindow;
            _readSettings = readSettings ?? PeerInboundSettingsFile.ReadText;
            _applySetting = applySetting ?? PeerInboundSettingsFile.Apply;
            _announced = DesktopView();
        }

        // CB-266: what sending into a Remote Control-bridged Claude Desktop chat
        // needs, passed in rather than built here so the app has one HttpClient
        // and one Keychain consent surface for every panel (SessionManager owns
        // them), and so a test drives the whole send through fakes. Either being
        // null means the bridge is not wired and the session behaves as if
        // "Show cloud sessions" were off.
        private readonly ICloudApi? _cloudApi;
        private readonly Func<string?, ICloudCredentialSource>? _credentialsFor;
        private readonly Func<bool> _cloudEnabled;
        private readonly Func<SessionStatus, bool> _focusDesktop;
        private readonly Action<SessionStatus, string> _focusTerminal;

        // Same two seams as the cloud panel's: a credential read is given a
        // budget, and the one retry waits on a clock a test can fake.
        internal CredentialBudget ReadBudget { get; init; } = CredentialBudget.Unmeasured;
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

        // The API's own refusal to take messages into this chat, learned from the
        // first one (there is no pre-flight). Cleared when the chat's bridge id
        // changes, because a refusal is a fact about one bridge session.
        private CloudSendability? _refusal;

        private readonly Func<string, string?> _findTranscript;
        private readonly SessionMessenger _messenger;
        private readonly Func<string, SessionRegistry.Entry?> _findRegistry;

        // startWatcher is the seam for the one call in this class that can
        // block its caller indefinitely (CB-234): turning a FileSystemWatcher
        // on. On macOS that is a native FSEvents stream, and under enough load
        // it never returns -- a UiTests host sat at 0% CPU for hours in it,
        // three times in one day. Production never passes one.
        private readonly Func<string, string, FileSystemWatcher> _startWatcher;

        // The thread that is turning the watcher on, kept so a test can join
        // it instead of sleeping. Null until Watch has run.
        internal Thread? WatcherStarter => _watcher?.Starter;

        // The watcher itself, once it has arrived. Null before, after a failed
        // start, and after Dispose.
        internal FileSystemWatcher? Watcher => _watcher?.Watcher;

        public string SessionId { get; }

        // Settable for the same reason OpenClawChatSession's is: the title can
        // improve after the panel opened, when Claude Code writes an ai-title
        // for a conversation that didn't have one yet.
        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connecting;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended;
        public event Action? PromptChanged;

        // --- lifecycle ---

        // Called on every scan, so the status this holds is never the one from
        // whenever the panel happened to open. Both things that follow from it
        // change while a panel is up: a transcript path can appear late, and the
        // waiting state is the entire permission-prompt mechanism.
        public void UpdateStatus(SessionStatus status)
        {
            // CB-266: whether the composer is offered can flip under an open
            // panel — Remote Control switched on in Desktop, the cloud setting
            // toggled, a refusal landing — so ReadOnlyChanged says so and the
            // panel redraws without being rebound.
            if (!string.Equals(_status.BridgeSessionId, status.BridgeSessionId, StringComparison.Ordinal))
                _refusal = null;

            _status = status;
            AnnounceView();
            if (!string.IsNullOrEmpty(status.Title)) DisplayName = status.Title;

            // CB-267: the scan is also when replying gets switched on or off, the
            // registry entry appears or goes, and settings.json is edited by hand
            // — every input to the offer except the transcript's own mode row.
            AnnounceInbound();

            Start();

            var waiting = string.Equals(status.State, "waiting", StringComparison.OrdinalIgnoreCase);

            if (!waiting)
            {
                if (!_waiting) return;

                _waiting = false;
                SetPrompt(null);
                return;
            }

            _waiting = true;

            // Not only on the transition into waiting. Claude Code commonly asks
            // two or three permissions in a row, and the state never leaves
            // "waiting" between them — so keying off the edge showed the first
            // dialog and then sat on a stale panel through every one after it.
            // Prompt going null is the signal that the last one was answered.
            //
            // This does not spin: a refresh always ends with a prompt set, even
            // when the screen could not be read, because "something is waiting
            // and I can't tell you what" is itself an answer.
            if (Prompt is null && !_refreshing) _ = RefreshPromptAsync();
        }

        // Idempotent, and called from both construction-time binding and every
        // status update, because the transcript path is the one field the hook
        // can record later than the rest — a session whose first status file
        // predates its first message has none.
        public void Start()
        {
            if (_started) return;

            // The hunt runs when the recorded path is missing as well as when
            // there is none. A recorded path can be wrong, not just late: a
            // respawned background worker's hook records a path computed from
            // the directory it relaunched in, while the conversation lives in
            // the projects directory keyed by where the session actually ran —
            // see SessionManager.WantsTranscriptRepair. Without the fallback,
            // File.Exists below said no on every status update, forever, and a
            // finished job's panel opened blank over a 3.6MB transcript.
            var path = _status.TranscriptPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = _findTranscript(SessionId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            _started = true;
            _transcriptPath = path;

            _ = LoadInitialAsync();
            Watch();
        }

        private void Watch()
        {
            var dir = Path.GetDirectoryName(_transcriptPath);
            var name = Path.GetFileName(_transcriptPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            // The backstop, and now first. FileSystemWatcher on macOS misses
            // writes to a file that is appended to without its metadata
            // changing the way the watcher expects, which is exactly what a
            // JSONL append is. It used to start after the watcher, so a watcher
            // that never finished starting took the poll down with it -- and
            // this method runs on the UI thread (a click opens the panel,
            // SessionManager creates the session and starts it), so it took the
            // thread down too.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _poll.Tick += (_, _) => Pump();
            _poll.Start();

            // The watcher is an optimisation over that poll, not a requirement,
            // so it is switched on where it cannot hold anything up -- see
            // DeferredWatcher for why that matters and what it cost to learn.
            _watcher = new DeferredWatcher(
                () => _startWatcher(dir, name),
                // Straight onto the UI thread and through the same 150ms
                // debounce SessionManager uses for status files, for the same
                // reason: one logical append can raise several events, and
                // parsing the tail three times to find the same two rows is
                // work on the thread that draws.
                watcher => watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Nudge),
                "LocalCliChatSession.Watcher");
        }

        // Split so the half a test can reach without waiting on the OS is
        // reachable: building the watcher is instant, turning it on is the call
        // that blocks. On macOS .NET's FSEvents start runs the kernel's global
        // sync(2) first, and that waits for every dirty buffer on the machine
        // to reach disk -- measured here at 118.7 s once, 0.4-1.8 s the next
        // four times, at a load average of 16-23 (CB-234).
        internal static FileSystemWatcher CreateWatcher(string dir, string name) =>
            new(dir, name) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };

        internal static FileSystemWatcher StartRealWatcher(string dir, string name)
        {
            var watcher = CreateWatcher(dir, name);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void Nudge()
        {
            _debounce?.Stop();
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounce.Tick -= OnDebounce;
            _debounce.Tick += OnDebounce;
            _debounce.Start();
        }

        private void OnDebounce(object? sender, EventArgs e)
        {
            _debounce?.Stop();
            Pump();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
            _poll?.Stop();
            _poll = null;
            _debounce?.Stop();
            _debounce = null;
        }

        // --- reading ---

        private async Task LoadInitialAsync()
        {
            var path = _transcriptPath;

            var window = await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var length = fs.Length;
                    var from = Math.Max(0, length - InitialBytes);
                    var (lines, alignedFrom) = ReadWindow(fs, from, length);
                    return (Turns: MapLines(lines), From: alignedFrom, To: length,
                            Mode: PeerInbound.LastPermissionMode(lines));
                }
                catch
                {
                    return (Turns: new List<Mapped>(), From: 0L, To: 0L, Mode: (string?)null);
                }
            });

            Dispatcher.UIThread.Post(() =>
            {
                _offset = window.To;
                _backlogFrom = window.From;
                _loaded = true;
                _permissionMode = window.Mode ?? _permissionMode;

                _history.Clear();
                _seen.Clear();

                foreach (var m in window.Turns)
                {
                    if (m.Uuid is not null && !_seen.Add(m.Uuid)) continue;
                    _history.Add(m.Turn);
                }

                Trim();
                SetState(RemoteChatState.Connected);
                HistoryReplaced?.Invoke();

                // A prompt may already be up when the panel opens — the session
                // has been sitting on it since before anyone clicked.
                if (string.Equals(_status.State, "waiting", StringComparison.OrdinalIgnoreCase))
                {
                    _waiting = true;
                    _ = RefreshPromptAsync();
                }

                AnnounceInbound();
            });
        }

        public bool HasMore => _started && _backlogFrom > 0;

        public async Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            if (!HasMore) return false;

            var path = _transcriptPath;
            var to = _backlogFrom;
            var from = Math.Max(0, to - PageBytes);

            var page = await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var (lines, alignedFrom) = ReadWindow(fs, from, to);
                    return (Turns: MapLines(lines), From: alignedFrom);
                }
                catch
                {
                    return (Turns: new List<Mapped>(), From: to);
                }
            }, ct);

            var added = 0;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _backlogFrom = page.From;

                var older = new List<ChatTurn>();
                foreach (var m in page.Turns)
                {
                    if (m.Uuid is not null && !_seen.Add(m.Uuid)) continue;
                    older.Add(m.Turn);
                }

                if (older.Count == 0) return;

                _history.InsertRange(0, older);
                added = older.Count;
                HistoryPrepended?.Invoke(added);
            });

            // A page that parsed to nothing but moved the offset is not the end
            // — the window can be entirely tool results and bookkeeping. Saying
            // false there would stop paging at the first quiet stretch, so the
            // answer is whether the offset moved, not whether rows came back.
            return added > 0 || page.From < to;
        }

        // The live tail. Everything appended since the last read, decoded as
        // whole lines only.
        private void Pump()
        {
            // Not before the opening read has landed. Watch() starts the poll
            // immediately after kicking off LoadInitialAsync, so without this a
            // tick that beat the initial read's post back to the UI thread would
            // see _offset still at zero and read the entire file — tens of
            // megabytes of it — as though it were new.
            if (!_started || !_loaded || _pumping) return;
            _pumping = true;

            var path = _transcriptPath;
            var from = _offset;

            _ = Task.Run(() =>
            {
                List<Mapped> mapped = new();
                string? mode = null;

                // Only moved once the bytes behind it have actually been mapped.
                // Assigning fs.Length up front and letting the catch below carry
                // it through would skip past whatever the failed read covered
                // and lose those rows for good.
                long to = from;

                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    // Shorter than where we were reading means the file was
                    // replaced under us — /clear starts a new transcript and
                    // Claude Code can rewrite one wholesale. Starting over is
                    // the only correct answer; carrying the old offset would
                    // read from the middle of an unrelated row forever.
                    if (fs.Length < from)
                    {
                        _carry.Clear();
                        from = 0;
                    }

                    var length = fs.Length;
                    if (length > from)
                    {
                        fs.Seek(from, SeekOrigin.Begin);
                        var buffer = new byte[length - from];
                        fs.ReadExactly(buffer);
                        var lines = TakeWholeLines(buffer);
                        mapped = MapLines(lines);
                        mode = PeerInbound.LastPermissionMode(lines);
                    }

                    to = length;
                }
                catch
                {
                    // Mid-write, or gone. The poll comes back in two seconds.
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _offset = to;
                    _pumping = false;

                    foreach (var m in mapped)
                    {
                        if (m.Uuid is not null && !_seen.Add(m.Uuid)) continue;
                        AddRow(m.Turn, m.Uuid);
                    }

                    // `/permissions` or shift-tab mid-session writes a new mode
                    // row, and that changes whether a message would be held.
                    if (mode is not null && mode != _permissionMode)
                    {
                        _permissionMode = mode;
                        AnnounceInbound();
                    }
                });
            });
        }

        // Appends `buffer` to whatever partial line was left over, and returns
        // the complete lines that make. The remainder goes back into the carry.
        // internal: the carry buffer is the thing standing between a write that
        // lands mid-codepoint and a permanent replacement character in the panel,
        // and it needs no dispatcher, no watcher and no CLI to exercise.
        internal List<string> TakeWholeLines(byte[] buffer)
        {
            _carry.AddRange(buffer);

            var last = _carry.LastIndexOf((byte)'\n');
            if (last < 0) return new List<string>();

            var complete = new byte[last + 1];
            _carry.CopyTo(0, complete, 0, last + 1);
            _carry.RemoveRange(0, last + 1);

            return Split(Encoding.UTF8.GetString(complete));
        }

        // A byte range of the file as whole lines. When `from` is not the start
        // of the file it almost certainly lands mid-row, so the first partial
        // line is dropped and the offset it was dropped to is returned — that
        // aligned offset is where the next page back has to stop, and using the
        // unaligned one would read the same row twice.
        // internal for the same reason: the alignment rule below decides whether
        // scrolling to the top of a long transcript makes progress or re-reads
        // the same megabyte forever.
        internal static (List<string> Lines, long From) ReadWindow(FileStream fs, long from, long to)
        {
            if (to <= from) return (new List<string>(), from);

            fs.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[to - from];
            fs.ReadExactly(buffer);

            var start = 0;
            if (from > 0)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n');

                // A whole window inside one row, which a megabyte-long
                // file-history snapshot manages. Reporting `to` would leave the
                // backlog offset exactly where it was, so every scroll to the
                // top would re-read the same megabyte and never get past it.
                // Reporting `from` steps over the window instead: the row is
                // unparseable from here anyway, and the page before it picks up
                // whatever came earlier.
                if (nl < 0) return (new List<string>(), from);

                start = nl + 1;
            }

            var text = Encoding.UTF8.GetString(buffer, start, buffer.Length - start);
            return (Split(text), from + start);
        }

        internal static List<string> Split(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        // --- mapping rows onto turns ---
        //
        // The parsing itself lives in ChatTranscript or CodexTranscript, both
        // pure and tested. What stays here is only the part that needs this
        // session: which bytes to read, whose format to read them as, and what
        // to do with the turns afterwards.

        private readonly record struct Mapped(string? Uuid, ChatTurn Turn);

        private List<Mapped> MapLines(List<string> lines) =>
            _format.Map(lines).Select(r => new Mapped(r.Uuid, r.Turn)).ToList();

        // --- slash commands ---

        // Scanned once per session rather than per keystroke: the commands a
        // running CLI understands don't change while it's running, so
        // rescanning ~/.claude on every character typed in the chat panel
        // would be disk I/O for something that never differs between reads.
        private IReadOnlyList<SlashCommand>? _slashCommands;

        // Empty once the channel is Messaging: a delivered message arrives as a
        // <cross-session-message>, not as keystrokes, so a built-in slash
        // command typed into the composer would be sent as plain text to a CLI
        // that never sees it as a command — see CB-43's messaging-mode note in
        // RemoteControlChatSession for the same rule over the wire.
        public IReadOnlyList<SlashCommand> SlashCommands =>
            ChannelIsMessaging
                ? Array.Empty<SlashCommand>()
                : (_slashCommands ??= SlashCommandCatalog.For(_status.Source, _status.Cwd));

        // --- sending ---

        // Whether CanSendQuietly has already said no and CanDeliver says yes —
        // asked from both the composer hint and SlashCommands, so it is one
        // answer rather than two copies that could drift.
        private bool ChannelIsMessaging =>
            !TerminalFocuser.CanSendQuietly(_status)
            && TerminalFocuser.CanDeliver(_status, SessionId, _findRegistry);

        public string ComposerHint => _status.IsDesktopHosted
            ? DesktopComposerHintFor(_status, _refusal, CloudOn, _format.ReplyEnabled(), IsBusy)!
            : ComposerHintFor(
                TerminalFocuser.CanSendQuietly(_status), ChannelIsMessaging, _format.ReplyEnabled(),
                _status.Shape, _status.Presence);

        // --- Claude Desktop (CB-265 read-only, CB-266 send over Remote Control) ---
        //
        // A session Claude Desktop hosts is read here and written there: the
        // transcript tails exactly like any other, but there is no terminal to
        // type into and no registry socket this app should message (CB-265). One
        // way in exists when the chat is on Remote Control: the same
        // POST /v1/code/sessions/<bridge>/events the cloud panel uses, with the
        // Claude Code login on this machine. It arrives in the session as a
        // *peer message* (measured, CB-266), not as the user's typing — Claude
        // treats it as a teammate's request and it cannot approve a permission
        // prompt — so the composer says so rather than pretending to be the
        // keyboard.
        //
        // Gated on "Show cloud sessions", the setting that promises no credential
        // read when off; no setting of its own.
        internal const string DesktopNoBridgeHint =
            "Typing goes through Claude Desktop — click the orb to open it. To send from here, turn on "
            + "Remote Control for this chat: type /rc in its Code tab, or switch on Settings › Claude Code › "
            + "Connect new sessions to Remote Control in Claude Desktop.";

        internal const string DesktopCloudOffHint =
            "This chat is on Remote Control. To send from here, turn on \"Show cloud sessions\" in "
            + "Orbweaver's Settings — sending uses the Claude Code login stored on this machine.";

        internal const string DesktopBridgedHint =
            "Message… (arrives as a teammate's request, via Remote Control)";

        internal const string DesktopNotPermittedHint =
            "The API refused messages to this chat from the Claude Code login on this machine. "
            + "Claude Desktop may be signed into a different account than the Claude Code CLI.";

        internal const string DesktopBridgeGoneHint =
            "This chat's Remote Control session has ended or was archived. "
            + "Type /rc in its Code tab to reconnect.";

        private bool CloudOn =>
            _cloudApi is not null && _credentialsFor is not null && _cloudEnabled();

        private bool IsBusy => string.Equals(_status.State, "generating", StringComparison.OrdinalIgnoreCase);

        // Whether the composer is taken away. Pure, so each row of the table is a
        // case: not bridged, cloud off, or a refusal the API has given.
        internal static bool DesktopReadOnly(SessionStatus status, CloudSendability? refusal, bool cloudEnabled) =>
            status.IsDesktopHosted && (!status.IsBridgedDesktop || !cloudEnabled || refusal is not null);

        // What the composer says. Null for a session that is not Desktop-hosted,
        // so a caller cannot mistake "not my case" for an empty sentence.
        internal static string? DesktopComposerHintFor(
            SessionStatus status, CloudSendability? refusal, bool cloudEnabled, bool replyEnabled,
            bool busy = false)
        {
            if (!status.IsDesktopHosted) return null;
            if (!status.IsBridgedDesktop) return DesktopNoBridgeHint;
            if (!cloudEnabled) return DesktopCloudOffHint;

            if (refusal is { } refused)
            {
                return refused == CloudSendability.NotPermitted ? DesktopNotPermittedHint : DesktopBridgeGoneHint;
            }

            if (!replyEnabled) return "Replying is off";
            return busy ? CloudChatSendability.BusyHint : DesktopBridgedHint;
        }

        public bool IsReadOnly => DesktopReadOnly(_status, _refusal, CloudOn);

        // The chat in a browser, once there is a bridge to point at — shown beside
        // the note and above the composer. Null with no bridge: Desktop's own
        // window is then the destination and the orb click is the way to it.
        public string? ReplyUrl => _status.IsBridgedDesktop ? ClaudeCloudRoster.UrlFor(_status.BridgeSessionId) : null;

        public event Action? ReadOnlyChanged;

        // The three things the panel draws from this class for a Desktop session,
        // compared across a status update. A non-Desktop session has none of them.
        private (bool ReadOnly, string? Url, string? Hint) DesktopView() =>
            _status.IsDesktopHosted ? (IsReadOnly, ReplyUrl, ComposerHint) : (false, null, null);

        // What the panel was last told. Compared against rather than read before
        // and after one call, because the cloud setting can move between two
        // scans without this class being involved at all.
        private (bool ReadOnly, string? Url, string? Hint) _announced;

        private void AnnounceView()
        {
            var now = DesktopView();
            if (_announced == now) return;

            _announced = now;
            ReadOnlyChanged?.Invoke();
        }

        // Both answers are reachable from a test this way, where they were not
        // before: whether there is a pane to type into depends on a real tmux and
        // a real session, so asking it here and deciding somewhere else is what
        // makes "no pane" something a test can state rather than something the
        // machine happens to be.
        //
        // shape and presence are here because "no pane" has two quite different
        // causes and only one of them has anything the user can do about it in
        // this panel. A terminal session outside tmux can be replied to in its own
        // terminal, which is what the old wording said — and for a background job
        // that wording was advice to go somewhere that does not exist, since a
        // daemon runs it precisely so that no terminal has to. The button beside
        // the box is the answer for that one, so the box names where it goes.
        //
        // The presence word is the daemon's own: it calls a parked job "needs
        // input", and several of them are literally holding a question. A box that
        // said only "no pane" was hiding the more interesting half of what was
        // true — and a job that has *finished* is a third thing again, which
        // nobody should be typing at, so it says so rather than inviting a reply.
        // canDeliver is asked before the no-pane branch below, because a
        // session CanDeliver says yes for has somewhere to go even though
        // CanSendQuietly said no — a `claude bg-spare` worker or an `--agent`
        // direct child with a live registry entry, see TerminalTyping.Channel.
        // A job that has already finished is the one exception: delivering to
        // it would land in a transcript nothing is going to read, so it keeps
        // the same "attach and read it" answer a background job with no
        // registry gets below, rather than inviting a message that goes
        // nowhere.
        internal static string ComposerHintFor(
            bool canSendQuietly, bool canDeliver, bool replyEnabled,
            LocalSessionShape shape, OrbPresence presence)
        {
            if (canDeliver && !canSendQuietly)
            {
                return presence == OrbPresence.Finished
                    ? "Finished — attach to read it"
                    : "Message it — it reads this at its next turn";
            }

            if (!canSendQuietly)
            {
                if (shape != LocalSessionShape.Background) return "No terminal to type into";

                return presence switch
                {
                    OrbPresence.NeedsInput => "Needs input — attach to reply",
                    OrbPresence.Finished => "Finished — attach to read it",
                    _ => "Attach to reply"
                };
            }

            return replyEnabled ? "Message…" : "Replying is off";
        }

        // --- attaching ---
        //
        // A background job has no pane, so the composer cannot reach it and no
        // amount of trying will change that where it is. What can change is
        // where it is: `claude attach` puts it in a terminal, and from there it
        // is an ordinary session with an ordinary pane.
        //
        // The rule is the click path's, asked rather than copied — see
        // ClickRouting.AttachWouldReach. A panel offering an attach for a session
        // a click would not attach is two answers to one question.
        //
        // What this deliberately is *not*: sending on the user's behalf by
        // attaching the session into a hidden tmux window, waiting for its prompt
        // and typing into that. It was considered and turned down here, on three
        // counts, and none of them is "it would be a lot of work":
        //
        // - It rests on an assumption nobody has checked: that `claude attach`
        //   fires hooks which record the new pane. If it does, this button is
        //   already the whole feature — the next scan gives the session a pane
        //   and the composer becomes an ordinary one with no new code. If it
        //   does not, a send-through-attach has to hold tmux state this app
        //   invented, which goes stale the moment the user closes that window.
        //   One live check settles which, and it has not been done.
        // - Knowing when the attached pane is ready to be typed into means
        //   reading Claude Code's TUI out of capture-pane and deciding it looks
        //   like a prompt. That is parsing a format nobody here controls, from a
        //   fixture written from imagination — the exact mistake this repo has
        //   already paid for once (see the dialog parser's note in CLAUDE.md,
        //   which failed on every real dialog).
        // - It fails in the wrong direction. Text typed into a TUI that is still
        //   booting is swallowed or mangled, and the panel would have shown the
        //   sentence as sent. A disabled-looking composer that says why, beside a
        //   button that visibly opens a terminal, fails where the user can see it.
        //
        // So: the honest affordance now, and the send when someone has watched an
        // attach happen on a real machine and knows which of the two worlds we
        // are in.
        public bool CanOpenElsewhere => ClickRouting.AttachWouldReach(_status, SessionId);

        // Excluded from coverage: one line, and it opens or focuses a real window.
        // What decides whether it is offered is CanOpenElsewhere above, which is
        // pure both sides of the call, and where it goes is ClickRouting's, which
        // is pure and covered per case.
        [ExcludeFromCodeCoverage]
        public void OpenElsewhere() => TerminalFocuser.Elsewhere(
            _status, SessionId, SessionManager.Instance?.PaneClaimsByOthers(SessionId),
            // Through the manager, because a chat session knows its id and not its
            // orb — and the button must acknowledge for the same reason the click
            // does, since the two share one destination.
            acknowledge: () => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => SessionManager.Instance?.AcknowledgeClickOn(SessionId)));

        // A message sent from the panel, waiting for the transcript row it will
        // produce. Held so the two can be reconciled instead of the same
        // sentence appearing twice a second apart.
        //
        // Two candidate texts rather than one, because an image-bearing send
        // can come back from the transcript in either of two shapes and
        // there is no way to know in advance which this CLI does. _pendingRaw
        // is what was actually typed — caption and path both — which is what
        // comes back verbatim if the CLI never noticed the path was a
        // picture. _pendingCaption is the caption alone, which is what comes
        // back if it did: see ChatTranscript's image handling, confirmed
        // against a real transcript row, for why the two diverge only then.
        // For a plain text send the two are identical, so nothing here
        // changes that path's behaviour.
        private ChatTurn? _pending;
        private string _pendingRaw = "";
        private string _pendingCaption = "";
        private DateTimeOffset _pendingAt;

        public Task<ChatSendOutcome> SendAsync(string text) =>
            _status.IsDesktopHosted
                ? SendViaBridgeAsync(text)
                : SendCoreAsync(typedText: text, displayText: text, imageBytes: null);

        // The uuid the bridge send went out with, held beside _pending so the
        // transcript row that comes back — Claude Code keeps the uuid the event
        // carried (measured, CB-266) — settles the bubble exactly, where matching
        // on text alone would fold two identical messages into one.
        private string? _pendingUuid;

        internal const string DesktopPicturesNote =
            "Pictures can't be sent to a Claude Desktop chat from here.";

        // Send into a Remote Control-bridged Claude Desktop chat (CB-266). Mirrors
        // the cloud panel's send through CloudTurnSender, with the two differences
        // that matter: the session is *local*, so the echo comes back through the
        // transcript this class already tails rather than a cloud history, and the
        // message arrives as a peer message, which ChatTranscript unwraps.
        //
        // **The bubble appears after the 2xx, never before**, for the cloud
        // panel's reason: a refusal would otherwise have to take a bubble back.
        // Not awaited with ConfigureAwait(false) — the continuation touches the
        // history and raises events, which belong on the UI thread this was
        // called from.
        internal async Task<ChatSendOutcome> SendViaBridgeAsync(string text)
        {
            if (!_format.ReplyEnabled())
            {
                Note(ReplyingOffNote);
                return ChatSendOutcome.Failed;
            }

            if (IsReadOnly)
            {
                Note("Not sent: " + ComposerHint);
                return ChatSendOutcome.Failed;
            }

            var source = _credentialsFor!(ClaudeConfigRoots.RootOfTranscript(_status.TranscriptPath));
            var attempt = await CloudTurnSender.SendUserTurnAsync(
                _cloudApi!, source, ReadBudget, _status.BridgeSessionId, text, Delay, CancellationToken.None);

            if (attempt.Result != CloudSendResult.Sent)
            {
                Note(attempt.Note!);

                if (attempt.Result == CloudSendResult.NotSent
                    && attempt.Outcome is { } outcome
                    && CloudChatSendability.RefusalFor(outcome) is { } refusal)
                {
                    _refusal ??= refusal;
                    AnnounceView();
                }

                return ChatSendOutcome.Failed;
            }

            // The row can beat the 2xx back to this thread: if the transcript
            // already delivered it, there is nothing to add or to wait for.
            if (_seen.Contains(attempt.Uuid)) return ChatSendOutcome.Sent;

            var mine = new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true };
            Add(mine);

            _pending = mine;
            _pendingUuid = attempt.Uuid;
            _pendingRaw = text.Trim();
            _pendingCaption = _pendingRaw;
            _pendingAt = DateTimeOffset.Now;
            return ChatSendOutcome.Sent;
        }

        // The picture is already a file by the time this is called — the
        // panel wrote it there before pasting its path in, the same way a
        // Finder drag-and-drop already puts a path in front of these two
        // CLIs rather than a picture. So the terminal gets the caption with
        // the paths appended as their own words, which is what a drop looks
        // like once it lands in the terminal's own input — but the bubble
        // shown locally gets the caption alone plus a thumbnail read
        // straight back from the same file, since there is no reason to make
        // this app's own echo wait on whether the CLI recognises the path.
        //
        // Only the first picture gets a thumbnail before the real transcript
        // row lands, matching the one-picture-per-turn a received image
        // already has; every path is still typed, so nothing beyond the
        // preview is limited to one.
        public async Task<ChatSendOutcome> SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths)
        {
            if (imagePaths.Count == 0) return await SendAsync(text);

            // A picture's path typed into a terminal is how the CLIs take one; a
            // peer message has no such channel, so refuse rather than send a path
            // the other side cannot open.
            if (_status.IsDesktopHosted)
            {
                Note(DesktopPicturesNote);
                return ChatSendOutcome.Failed;
            }

            var caption = text.Trim();
            var typed = imagePaths.Aggregate(caption, (line, path) => line.Length == 0 ? path : line + " " + path);

            byte[]? thumbnail = null;
            try { thumbnail = await File.ReadAllBytesAsync(imagePaths[0]); }
            catch
            {
                // No preview before the real row lands is not a reason to
                // fail the send — the file is still on disk and the
                // terminal still gets its path.
            }

            return await SendCoreAsync(typed, caption, thumbnail);
        }

        // Excluded from coverage for its last line, which no test may execute:
        // reaching it means CanSendQuietly has said yes, which needs a real tmux
        // binary and a real pane belonging to a live session — and it then types
        // into that pane for real, on the machine running the tests.
        //
        // The two refusals in front of it are the interesting part and they are
        // still measured, as the pure functions they now call: ReplyingOffNote
        // and NoPaneNote. Driving this method with replying off is also still
        // asserted; those assertions simply are not counted.
        [ExcludeFromCodeCoverage]
        private async Task<ChatSendOutcome> SendCoreAsync(string typedText, string displayText, byte[]? imageBytes)
        {
            if (!_format.ReplyEnabled())
            {
                Note(ReplyingOffNote);
                return ChatSendOutcome.Failed;
            }

            if (!TerminalFocuser.CanSendQuietly(_status))
            {
                if (TerminalFocuser.CanDeliver(_status, SessionId, _findRegistry))
                    return await DeliverViaMessengerAsync(typedText, displayText, imageBytes);

                Note(NoPaneNote(_status, _status.Shape, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows()));
                return ChatSendOutcome.Failed;
            }

            return await TypeIntoTerminalAsync(typedText, displayText, imageBytes);
        }

        // Not excluded from coverage, unlike TypeIntoTerminalAsync beside it:
        // that one is reachable only with a real tmux binary and a real pane,
        // which nothing here can fake, while this one needs only a fake
        // SessionMessenger and a fake registry lookup — both already seams on
        // the constructor — so a test can drive it for real rather than taking
        // its correctness on faith. See tests/UiTests/LocalCliChatSessionTests.cs.
        //
        // Ordering mirrors TypeIntoTerminalAsync exactly and for the same
        // reason: Add() runs every turn through Reconcile, so _pending has to
        // be set after the turn is on screen or the user's own message would
        // settle against itself before DeliverAsync is ever awaited.
        private async Task<ChatSendOutcome> DeliverViaMessengerAsync(
            string typedText, string displayText, byte[]? imageBytes)
        {
            var mine = new ChatTurn
            {
                Role = ChatRole.User,
                Text = displayText,
                IsComplete = true,
                ImageBytes = imageBytes
            };

            Add(mine);

            return await HandOverAsync(mine, typedText);
        }

        // The delivery itself, split out so a held message can be sent again
        // into the bubble it already has (CB-267): ResendHeldAsync hands this
        // the same turn, and _pending pointing at it is what makes the row
        // that eventually lands settle that bubble rather than add a second.
        private async Task<ChatSendOutcome> HandOverAsync(ChatTurn mine, string typedText)
        {
            _pending = mine;
            _pendingRaw = typedText.Trim();
            _pendingCaption = mine.Text.Trim();
            _pendingAt = DateTimeOffset.Now;

            var receipt = await _messenger.DeliverAsync(
                SessionId, SessionMessenger.FromName(MachineNames.Tag()), typedText, CancellationToken.None);

            // CB-35: Accepted is the only receipt that means the socket took
            // the bytes — DeliveryNote's own comment says the same thing
            // about wording ("never 'sent' or 'delivered' outright for
            // anything but Accepted"), and the composer's retain/clear
            // decision draws the line in the same place. Every other arm is
            // a reason nothing reached the far side, which is exactly what
            // "the text is still worth keeping in the box" means.
            var accepted = receipt.Result == DeliveryResult.Accepted;

            // CB-267: the socket taking the bytes is not the session reading
            // them. A session running without permission prompts holds a peer
            // message until someone approves it in that session, and nothing on
            // the wire says so — so the same rule the offer box uses decides
            // whether "Handed to X" would be a lie. An unreadable settings.json
            // (CanEdit false) is not claimed either way: the old note stands.
            var held = accepted && OfferNow() is { CanEdit: true };

            Note(held ? PeerInbound.HeldNote(DisplayName) : DeliveryNote(receipt, DisplayName));

            // Kept on a failed resend, so "Send it again" survives a socket
            // that refused once; dropped only when this very turn went in.
            if (held) _held = (mine, typedText);
            else if (accepted && ReferenceEquals(_held?.Turn, mine)) _held = null;

            AnnounceInbound();

            return accepted ? ChatSendOutcome.Sent : ChatSendOutcome.Failed;
        }

        // What the composer says once a delivery attempt has actually been
        // made — as opposed to ComposerHintFor above, which is what it says
        // beforehand. Never "sent" or "delivered" outright for anything but
        // Accepted: the socket accepting bytes is the only proof this protocol
        // ever gets, and the four other arms each name a specific reason
        // nothing reached the far side rather than a generic failure.
        //
        // The AgentStatus branch on Accepted exists because "handed to X" reads
        // as done when the far session is mid-turn — it is not done, Claude
        // Code queues it and folds it in once the running turn ends (see
        // BridgeProtocol's comment on absorbed rows for the same mechanism
        // over the wire), and a user watching the panel for a reply deserves
        // to know that before wondering why nothing came back.
        //
        // DeliveryResult.WriteFailed has no arm of its own here for the reason
        // its own doc comment gives: SessionMessenger never produces it today,
        // folding that case into SocketRefused instead, so the wildcard arm
        // covers both without a branch that can never be exercised.
        //
        // No protocol-version number in the UnsupportedProtocol sentence: an
        // earlier draft of this feature wanted one, but DeliveryReceipt as
        // actually built carries only AgentStatus, and adding a version field
        // to it purely to interpolate into a sentence was judged not worth
        // widening the foundation layer's own record for. The wire/remote
        // engineer building the mirror side of this feature reads
        // DeliveryReceipt unchanged.
        internal static string DeliveryNote(DeliveryReceipt receipt, string name) => receipt.Result switch
        {
            DeliveryResult.Accepted when string.Equals(receipt.AgentStatus, "working", StringComparison.Ordinal) =>
                $"Handed to {name}. It's mid-turn and will read this when that turn ends — "
                + "the message shows here once it has.",

            DeliveryResult.Accepted =>
                $"Handed to {name} for its next turn. It arrives as a message from {Brand.DisplayName}, "
                + "not keystrokes, so built-in slash commands won't run.",

            DeliveryResult.NoRegistryEntry =>
                $"{name} isn't registered with Claude Code any more — the job may have stopped. "
                + "Attach it with the gear button to answer it there.",

            DeliveryResult.UnsupportedProtocol =>
                $"{name} speaks a peer protocol {Brand.ShortName} doesn't recognize, so nothing was sent.",

            _ => "Claude Code's session socket refused the connection; nothing was sent.",
        };

        // A System turn rather than an exception, for the reason
        // OpenClawChatSession gives at the same point: the person has just typed
        // a sentence and losing it behind a dialog is a poor answer to "why
        // didn't that send".
        internal const string ReplyingOffNote =
            "Replying is off. Turn on \"Allow replying to sessions\" in Settings.";

        // Two different problems that both end in "nothing was typed", and the
        // note has to say which: a session outside tmux can still be replied to
        // in its own terminal, where a missing tmux binary cannot be worked
        // around at all. Telling someone to go to a terminal that isn't there is
        // the failure this distinction exists to avoid.
        //
        // Takes the whole status rather than a pane and a shape since CB-79:
        // the answer now depends on which terminal the session is in, and
        // `onMacOS` is a parameter rather than a call to
        // `OperatingSystem.IsMacOS()` so both arms run on both CI legs.
        internal static string NoPaneNote(
            SessionStatus status, LocalSessionShape shape, bool onMacOS, bool onWindows)
        {
            // Desktop-hosted: the panel is read-only, so this is only reached by
            // a caller that sent anyway. WhyNot already says where to reply, and
            // "Reply in the terminal instead" would name a window that is not there.
            if (status.IsDesktopHosted) return TerminalTyping.WhyNot(status, onMacOS, onWindows);

            if (!string.IsNullOrEmpty(status.TmuxPane)) return "Couldn't find tmux to type with.";

            // Three problems that all end in "nothing was typed", and the note
            // has to say which. The third is the one this branch added, and the
            // sentence it replaces was actively wrong: a background job is run by
            // a daemon so that no terminal has to hold it, so "reply in the
            // terminal instead" named a window that does not exist. The attach
            // button beside the box is what does exist, so the note points at it.
            if (shape == LocalSessionShape.Background)
            {
                // Names the button rather than typing U+2699 at it (CB-173):
                // in a chat bubble that character is a colour emoji on Windows,
                // so the sentence would point at a mark that looks nothing like
                // the drawn gear on the button itself.
                return "This is a background job with no terminal of its own. "
                    + "Attach it with the gear button beside the box to answer it there.";
            }

            // Locally the reason is knowable, so it is said. TerminalTyping
            // names the terminal it found and the ones it can address, which
            // is something a user can act on — unlike "isn't in a tmux pane",
            // which described a setting they did not want for a session that
            // was in an ordinary iTerm2 window.
            return TerminalTyping.WhyNot(status, onMacOS, onWindows)
                + " Reply in the terminal instead.";
        }

        // Excluded from coverage: only reachable once CanSendQuietly has said yes,
        // which needs a real tmux binary and a real pane belonging to a live
        // session — and it then types into that pane for real. Both of those are
        // the machine the tests are running on, not a fixture.
        //
        // The decisions in front of it are covered: whether there is anywhere to
        // type at all, and what to say when there is not — a session with no pane
        // gets a different sentence from a machine with no tmux, because they are
        // different problems with different answers.
        //
        // The ordering inside it is load-bearing and worth keeping written down.
        // Add() runs every turn through Reconcile, so marking _pending before
        // adding made the user's own message match itself: it was reconciled away
        // on the spot, never reached the history, and sending appeared to do
        // nothing.
        [ExcludeFromCodeCoverage]
        private async Task<ChatSendOutcome> TypeIntoTerminalAsync(
            string typedText, string displayText, byte[]? imageBytes)
        {
            var mine = new ChatTurn
            {
                Role = ChatRole.User,
                Text = displayText,
                IsComplete = true,
                ImageBytes = imageBytes
            };

            Add(mine);

            _pending = mine;
            _pendingRaw = typedText.Trim();
            _pendingCaption = displayText.Trim();
            _pendingAt = DateTimeOffset.Now;

            var sent = await TerminalFocuser.SendTextAndSubmit(_status, typedText);
            if (sent) return ChatSendOutcome.Sent;

            _pending = null;
            Note("Couldn't send that to the terminal.");
            return ChatSendOutcome.Failed;
        }

        // The body of a delivered message, unwrapped from the tag Claude Code's
        // own transcript holds it in.
        //
        // Not BridgeProtocol.ParseInboundMessages, which reads a shape this
        // isn't: that tag carries a from-name attribute so a multi-hop relay
        // reply can be correlated back to whichever peer it answers, and its
        // own guard drops anything without one ("without a sender there is
        // nothing to attribute the message to"). SessionMessageFrame.Wrap
        // never writes a from-name — a direct socket delivery already knows
        // which session it addressed, from the id it dialled rather than from
        // anything the row says back — so that parser would silently drop
        // every message this feature ever delivers. This reads the body alone,
        // which is all Reconcile below needs.
        private static readonly Regex DeliveredMessageBody = new(
            @"<cross-session-message\s+[^>]*>(?<body>.*?)</cross-session-message>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        internal static string? DeliveredBody(string rowText)
        {
            var m = DeliveredMessageBody.Match(rowText);
            return m.Success ? m.Groups["body"].Value.Trim() : null;
        }

        // The transcript will produce the message we just sent, because it went
        // through the terminal — that is the whole design. So the row that comes
        // back adopts the turn already on screen rather than adding a second.
        //
        // Matched on text and bounded by time: an identical message sent twice
        // an hour apart must not have the second one swallowed by a stale
        // pending turn that never arrived.
        private bool Reconcile(ChatTurn incoming, string? uuid)
        {
            if (_pending is null) return false;

            // The bridge send's own uuid is conclusive, and needs no time bound:
            // it is the id this message went out with, not a guess from its text.
            if (_pendingUuid is not null && string.Equals(uuid, _pendingUuid, StringComparison.Ordinal)
                && !ReferenceEquals(incoming, _pending))
            {
                return Settle(incoming);
            }

            if (DateTimeOffset.Now - _pendingAt > TimeSpan.FromMinutes(2))
            {
                _pending = null;
                return false;
            }

            // The pending turn is itself passed through Add on the way in, and
            // must not reconcile against itself. SendAsync orders things so this
            // cannot happen; the check stays because the failure it prevents —
            // a sent message silently never appearing — is invisible.
            if (ReferenceEquals(incoming, _pending)) return false;

            if (incoming.Role != ChatRole.User) return false;

            var incomingText = incoming.Text.Trim();

            // Either the CLI never noticed the path (the row comes back
            // exactly as typed) or it did and swapped it for a real picture
            // plus its own placeholder, which ChatTranscript has already
            // stripped down to the caption alone. Both are "this is the
            // message that was just sent" — see the two fields' own comment.
            //
            // A third shape only a messenger delivery produces: the row Claude
            // Code writes back is the whole <cross-session-message> tag it was
            // actually handed (see SessionMessageFrame.Wrap), not the bare
            // text — so an exact match against either candidate above always
            // misses it, and without this a delivered message would settle
            // nothing and appear a second time once the row arrived.
            if (!string.Equals(incomingText, _pendingRaw, StringComparison.Ordinal)
                && !string.Equals(incomingText, _pendingCaption, StringComparison.Ordinal))
            {
                var delivered = DeliveredBody(incoming.Text);

                if (delivered is null
                    || (!string.Equals(delivered, _pendingRaw, StringComparison.Ordinal)
                        && !string.Equals(delivered, _pendingCaption, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            // Keep the transcript's timestamp: it is when the session actually
            // received it, which for a message queued behind a long turn is
            // minutes after it was typed. The settled turn's own ImageBytes,
            // if any, is left alone rather than replaced from incoming — it
            // was already read straight from the file that was pasted, and
            // is the same picture either way this matched.
            return Settle(incoming);
        }

        private bool Settle(ChatTurn incoming)
        {
            var settled = _pending!;
            _pending = null;
            _pendingUuid = null;

            settled.Text = incoming.Text;
            TurnUpdated?.Invoke(settled);
            return true;
        }

        public void Cancel()
        {
            // Escape is what interrupts a run in the TUI, and this is the one
            // place the panel can offer it. Gated with everything else that
            // types: stopping someone's work is not something a viewer should be
            // able to do.
            if (!_format.ReplyEnabled()) return;

            _ = TerminalFocuser.SendPaneKey(_status, "Escape");
        }

        // --- permission prompts ---

        private bool _waiting;

        public ChatPrompt? Prompt { get; private set; }

        private bool _refreshing;

        private async Task RefreshPromptAsync()
        {
            _refreshing = true;

            try
            {
                var screen = await TerminalFocuser.CapturePane(_status);

                // Still waiting? The capture runs a process and the answer may
                // have been given in the terminal while it did.
                if (!_waiting) return;

                // A prompt with no options is the honest outcome when the dialog
                // could not be read: something is waiting, we cannot say what,
                // and the panel offers the terminal instead of a guess.
                var parsed = screen is null ? null : ChatTranscript.ParseDialog(screen);
                SetPrompt(parsed ?? new ChatPrompt("Waiting for input", Array.Empty<ChatPromptOption>()));
            }
            finally
            {
                _refreshing = false;
            }
        }

        // internal: the transitions around a prompt are what decide whether the
        // panel is still offering buttons for a dialog that has been answered,
        // and pressing one of those sends keystrokes into a live session. Reading
        // the pane to *find* a prompt needs tmux and is excluded; establishing one
        // does not.
        internal void SetPrompt(ChatPrompt? prompt)
        {
            Prompt = prompt;
            Dispatcher.UIThread.Post(() => PromptChanged?.Invoke());
        }

        public async Task AnswerAsync(ChatPromptOption option)
        {
            if (!_format.ReplyEnabled())
            {
                Note("Replying is off, so this can only be answered in the terminal.");
                return;
            }

            // Cleared first. The hook will report the session generating again
            // within a moment, but the buttons should stop being clickable the
            // instant one is clicked rather than staying live for a second
            // answer to the dialog that is already gone.
            SetPrompt(null);

            if (await TerminalFocuser.SendPaneKey(_status, option.Key)) return;

            Note("Couldn't answer that in the terminal.");

            // Put it back. Clearing optimistically is right when the keystroke
            // lands, and leaves the panel silent about a session that is still
            // stopped when it doesn't.
            if (_waiting) await RefreshPromptAsync();
        }

        // A Desktop session has no terminal for TerminalFocuser to find, so its
        // Focus returned early and this button did nothing (CB-265's gap). Its
        // window is Desktop's, which is where the orb click goes too.
        public void AnswerElsewhere()
        {
            if (_status.IsDesktopHosted) _focusDesktop(_status);
            else _focusTerminal(_status, SessionId);
        }

        // Excluded from coverage: each activates another application's window or
        // a terminal. Which one is chosen is AnswerElsewhere's decision above,
        // covered through the seams.
        [ExcludeFromCodeCoverage]
        private static bool FocusDesktopWindow(SessionStatus status) => DesktopSessionFocus.Focus(status, null);

        [ExcludeFromCodeCoverage]
        private static void FocusTerminalWindow(SessionStatus status, string sessionId) =>
            TerminalFocuser.Focus(status, null, sessionId);

        // --- CB-267: messages Claude Code would hold ---
        //
        // A session started with --permission-mode bypassPermissions holds every
        // peer message — this app's included — until someone approves it in that
        // session, unless its config root's settings.json says
        // "crossSessionInbound": "accept". The socket accepts the bytes either
        // way, so without this the panel said "Handed to X" over a message that
        // was sitting in a queue nobody was watching.
        //
        // What this class owns is *which* session, *which* settings.json and
        // *when* to ask; the rule itself (PeerInbound.WouldHold) and the file
        // edit (PeerInboundSettingsFile.Apply) are pure and IO respectively and
        // tested where they live. readSettings and applySetting are seams for
        // the same reason messenger is: the real ones read and write a file under
        // a real Claude config root, which no test may touch.
        private readonly Func<string, string?> _readSettings;
        private readonly Func<string, bool, PeerInboundWrite> _applySetting;

        // Off the last {"type":"permission-mode"} row the transcript carried.
        // Claude Code writes one near the start and again around every turn, and
        // the last one wins. Read from the opening window and the live tail only,
        // never from an older page: a page is *older*, and letting it overwrite
        // the current mode would wind the session back to whatever it was an hour
        // ago. Null until a row is seen, when settings.json's defaultMode stands
        // in for it.
        private string? _permissionMode;

        // The message the last delivery claimed was held, and exactly what was
        // typed for it, so "Send it again" re-sends the same bytes into the same
        // bubble.
        private (ChatTurn Turn, string Typed)? _held;

        private PeerInboundOffer? _inbound;

        // What the panel was last told, compared against for the same reason
        // _announced is: the inputs move between scans without this class being
        // involved.
        private (PeerInboundOffer? Offer, bool Held) _inboundAnnounced;

        // settings.json's text, kept against its write time and length so a
        // scan does not re-read a file nobody has touched. Re-read the moment
        // either moves, which is what lets a hand edit show up on the next scan.
        private (SettingsKey Key, SettingsRead Read)? _settingsRead;

        // What identifies one version of one settings.json. Compared with Equals
        // rather than field by field: a lifted comparison of nullable tuples
        // compiles to HasValue arms per side, which no session can drive one by
        // one and which only obscured what the cache is keyed on.
        private readonly record struct SettingsKey(string Path, bool Exists, DateTime Written, long Length);

        // Null text means two different things and only the stamp can tell them
        // apart: no settings.json at all (an empty object, which can be created)
        // or one that exists and could not be read — mode 000, a sharing
        // violation — which must be explained and left alone, never offered a
        // button whose write would fail on the same read (CB-267 QA).
        private readonly record struct SettingsRead(string? Text, bool Unreadable);

        public PeerInboundOffer? InboundOffer => _inbound;

        public bool HasHeldMessage => _held is not null;

        public event Action? InboundChanged;

        // Which config root this session belongs to: the transcript path names
        // it whenever the transcript lives where Claude Code puts it, and the
        // registry entry the messenger would use names it otherwise. Neither
        // means there is no settings.json this panel can honestly point at, and
        // then nothing is offered rather than a guess at ~/.claude.
        internal string? ConfigRootFor() =>
            ClaudeConfigRoots.RootOfTranscript(_status.TranscriptPath) ?? _findRegistry(SessionId)?.ConfigRoot;

        // A settings.json that is not JSON this app can safely rewrite. Said
        // rather than offered: a file we could not parse is never written to,
        // so the button would have nowhere to go.
        internal static string UnreadableText(string name) =>
            PeerInbound.OfferText(name) + " " + Brand.DisplayName
            + " couldn't read its settings file as JSON it can safely edit, so it has been left alone — add \"" + PeerInbound.SettingKey + "\": \""
            + PeerInbound.AcceptValue + "\" to it by hand to let them straight in.";

        internal static string EnableFailedNote(string displayPath, string? reason) =>
            $"Couldn't change {displayPath}, so nothing was turned on: {reason ?? "the write did not go through"}.";

        // The offer, asked fresh. Gated in the order that is cheapest to refuse:
        // a Desktop chat goes over Remote Control rather than the socket (CB-266,
        // unchanged here); replying off means nothing is being sent; a mode that
        // cannot hold ends it before the registry is scanned, which is most
        // sessions; and only a session reached over its socket — no pane — is
        // affected at all, since text typed into a pane is the user's own.
        private PeerInboundOffer? OfferNow()
        {
            if (_status.IsDesktopHosted || !_format.ReplyEnabled()) return null;
            if (_permissionMode is { } mode && !PeerInbound.HoldingModes.Contains(mode)) return null;
            if (!ChannelIsMessaging) return null;

            var root = ConfigRootFor();
            if (root is null) return null;

            var path = PeerInboundSettingsFile.PathFor(root);
            var (text, unreadable) = SettingsText(root, path);

            if (unreadable || PeerInbound.ReadSetting(text) == InboundSetting.Unreadable)
            {
                // Without the file the default mode is unknowable too, so only a
                // mode the transcript itself named can make this worth saying.
                return _permissionMode is null
                    ? null
                    : new PeerInboundOffer(UnreadableText(DisplayName), path, CanEdit: false);
            }

            return PeerInbound.WouldHold(_permissionMode, text)
                ? new PeerInboundOffer(PeerInbound.OfferText(DisplayName), path, CanEdit: true)
                : null;
        }

        private SettingsRead SettingsText(string root, string path)
        {
            var info = new FileInfo(path);
            var key = info.Exists
                ? new SettingsKey(path, true, info.LastWriteTimeUtc, info.Length)
                : new SettingsKey(path, false, default, 0);

            if (_settingsRead is { } cached && cached.Key.Equals(key)) return cached.Read;

            var text = _readSettings(root);
            var read = new SettingsRead(text, Unreadable: text is null && key.Exists);
            _settingsRead = (key, read);
            return read;
        }

        private void AnnounceInbound()
        {
            _inbound = OfferNow();

            var now = (_inbound, _held is not null);
            if (_inboundAnnounced == now) return;

            _inboundAnnounced = now;
            InboundChanged?.Invoke();
        }

        // The write runs off the UI thread — it is a read, a backup copy and an
        // atomic replace — and everything after it is back on the thread that
        // clicked. The cache is dropped rather than trusted: a write inside the
        // same filesystem tick as the read would leave the stamp unchanged.
        public async Task<bool> EnableInboundAsync()
        {
            var root = ConfigRootFor();
            if (root is null) return false;

            var write = await Task.Run(() => _applySetting(root, true));
            _settingsRead = null;

            var ok = write.Kind is PeerInboundWriteKind.Written or PeerInboundWriteKind.Unchanged;
            if (!ok)
            {
                Note(EnableFailedNote(
                    PeerInbound.DisplayPath(PeerInboundSettingsFile.PathFor(root),
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                    write.Reason));
            }

            AnnounceInbound();
            return ok;
        }

        // The held message, delivered again. Claude Code still has the first
        // copy queued; it is not withdrawn by this, which the panel's done text
        // leaves to the person who can see that session.
        public async Task<ChatSendOutcome> ResendHeldAsync()
        {
            if (_held is not { } held) return ChatSendOutcome.Failed;

            if (!_format.ReplyEnabled())
            {
                Note(ReplyingOffNote);
                return ChatSendOutcome.Failed;
            }

            return await HandOverAsync(held.Turn, held.Typed);
        }

        // --- plumbing ---

        private void Note(string text) => Add(new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = text
        });

        private void Add(ChatTurn turn) => AddRow(turn, null);

        // The row's uuid rides along so a bridge send's own echo can be matched
        // on it; Add stays one-argument because everything else has no uuid.
        private void AddRow(ChatTurn turn, string? uuid)
        {
            if (Reconcile(turn, uuid)) return;

            _history.Add(turn);
            Trim();
            TurnAdded?.Invoke(turn);
        }

        private void Trim()
        {
            if (_history.Count > KeepTurns) _history.RemoveRange(0, _history.Count - KeepTurns);
        }

        private void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

    }
}
