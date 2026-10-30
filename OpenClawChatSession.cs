using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia.Threading;

namespace Orbweaver
{
    // One turn as the history parser hands it over: what TurnsFromHistory reads
    // out of a page of chat.history, and what SetHistory and PrependHistory put
    // into a transcript.
    //
    // A record rather than the seven-field tuple this was. The tuple was
    // tolerable while it was three fields and stopped being so at seven — every
    // producer and consumer restated the whole shape in its own signature, so
    // the shape appeared eight times in two files plus once in each test's
    // helper, and adding a field meant editing all of them before anything
    // compiled again. Named members also read at the call site: `turn.Speaker`
    // says what `t.Item6` does not.
    //
    // A struct because it is a value and is treated as one — a page is a few
    // hundred of these, each copied into a ChatTurn immediately, and nothing
    // ever holds on to one.
    internal readonly record struct HistoryTurn(
        ChatRole Role,
        string Text,
        string? ImageUrl,
        string ImageAlt,
        DateTimeOffset At,
        string? Speaker,
        string? SpeakerColor,

        // Whether the person at this keyboard said it — see ChatTurn.Mine, whose
        // value this becomes. Defaulted, because only the OpenClaw history
        // parser is in a position to answer it and every other producer of a
        // HistoryTurn would otherwise have to write `false` to say nothing.
        bool Mine = false,

        // The picture itself, when the block carried it inline rather than
        // naming somewhere to fetch it from. This is the shape this gateway
        // actually emits — `{type:"image", data:"<base64>", mimeType:...}`
        // with no url at all (CB-91) — so a turn generally has *either* this
        // or ImageUrl, never both. Defaulted for the same reason Mine is:
        // only TurnsFromHistory is in a position to fill it in.
        byte[]? ImageBytes = null,

        // The clean path behind ImageUrl, when ImageUrl is one of the
        // gateway's own assistant-media requests (CB-109). ImageUrl is the
        // finished, identity-bearing url and is fetched verbatim; this is the
        // human-readable file it names, carried so the panel can put it in the
        // tooltip on a refusal without unescaping it back out of a query
        // string. Same category of value ImageAlt already holds — a basename
        // derived from this very path — only whole rather than lossy.
        //
        // Null for a picture that did not come from that route at all: an
        // image block carrying its own url, or one carrying inline bytes.
        // Defaulted, and trailing, for the same reason Mine and ImageBytes
        // are — only the arms of TurnsFromHistory that build such a request
        // are in a position to fill it in. A tenth field on a record struct
        // that became a record precisely so a tenth field would not mean
        // rewriting every producer; this is that cost being paid rather than
        // worked around.
        string? ImageSourcePath = null,

        // CB-115: which cron job might be able to recover this turn's
        // picture, when the message named `openclawAutomation` with kind
        // "cron" and neither arm above resolved anything — the delivered-
        // mirror and named-path arms both `continue` before this field is
        // ever set, so a turn only ever carries it alongside no picture,
        // never instead of one it already has. Whether the message's text is
        // actually worth a lookup (the trigger's second conjunct — an
        // image-shaped trailing token, not just any cron reply) is decided
        // later, in OpenClawSessions.FetchHistoryPageAsync, against this same
        // Text — kept out of TurnsFromHistory so that method stays a pure
        // read of one message with no opinion on what counts as
        // image-shaped. See OpenClawCronRecovery's header for the full
        // trigger and why it is gated this way.
        OpenClawAutomation? Automation = null,

        // CB-116: whether a *failed* fetch for this turn's picture is worth
        // explaining with a visible note — see MediaConfidence, next to
        // ChatRole in RemoteChat.cs, for the full reasoning. Decided by
        // TurnsFromHistory (and, for a CB-115 recovery, by
        // FetchHistoryPageAsync) at the point each arm already knows the
        // candidate's provenance; nothing downstream re-derives it from the
        // text. Defaults High so an arm with no opinion — an inline image
        // block, a turn built outside this parser — keeps today's behaviour:
        // a failure explains itself unless something here says not to.
        MediaConfidence Confidence = MediaConfidence.High,

        // CB-98's cross-arm case, second instance: which of TurnsFromHistory's
        // picture-drawing arms produced this turn. The named-path arm already
        // has its own collapse against a delivery-mirror, keyed on the two
        // arms resolving to the identical *path* -- see the merge at the end
        // of TurnsFromHistory. An inline image block carries no path at all
        // (CB-91: bare base64, no filename anywhere in the block), so it has
        // nothing to key that merge on, and stayed unmerged.
        //
        // What the two arms *can* share, once both are fetched, is identical
        // bytes -- but bytes alone proved to be the wrong signal the first
        // time this ticket was worked: two genuinely separate mirror
        // deliveries of the same file are just as byte-identical as one
        // delivery seen twice, and collapsing on bytes alone would silently
        // eat the second, real one (see TwoMirrorsOfOneFileStayTwoTurns).
        // Arm is what tells them apart -- pairing bytes only across two
        // *different* arms is safe precisely because a mirror is never
        // compared against another mirror.
        //
        // Set here, at parse time, by the two arms that can produce a
        // cross-arm duplicate; left at the default for every other turn. Read
        // later by OpenClawSessions.CrossArmDuplicateMirrorIndices, once the
        // mirror side's bytes exist to compare -- see that method's header
        // for why the comparison cannot happen inside this pure parser.
        MediaSourceArm Arm = MediaSourceArm.None,

        // CB-274 / CB-275: the attachment block this turn was drawn from, when
        // the gateway's display projection moved the file out of the text and
        // into a video / audio / attachment / attachment_error block. Carried
        // by every site that copies a history turn into a live one, because a
        // copy that dropped it would send a clip or a pdf to the bitmap
        // decoder, which does not throw on rubbish.
        ChatAttachment? Attachment = null,

        // CB-270: whether this is a tool line whose result reported an error,
        // so the panel can tint it. Only a `toolcall` turn from
        // TurnsFromHistory ever sets it; defaulted and trailing for the same
        // reason every field above it is.
        bool IsError = false,

        // CB-270: this turn was drawn from a `cli` rollup message, which
        // repeats the text of the run's steps. Needed only so a scroll-back
        // can tell, when an older page ends in the steps of the run whose
        // rollup is the head of the transcript, that the rollup is the double
        // and must go (see PrependHistory).
        bool FromRollup = false,

        // CB-270 stage 3b, both set by TurnsFromHistory from the page's raw
        // messages (a user message that renders to no turn is still a boundary
        // there, which the turns alone would lose). OpenTail: a text step of the
        // page's trailing run, with no rollup or user message after it, so the run
        // an older page ends in. SeamRollup: the kept text of the first rollup with
        // no user message before it, on a page an older one may precede; the rollup
        // that closes that run. The page boundary compares exactly these two.
        bool OpenTail = false,
        bool SeamRollup = false,

        // CB-270 stage 3b, set by TurnsFromHistory from the raw messages like the two
        // above. RunTail / RunHead: any turn (a picture too) of the page's trailing
        // run, with no rollup or user message after it / of its head run, before any
        // user message or rollup closes it, on a page an older one may precede. The
        // page boundary compares pictures across them by path. Blanked: the caption
        // was blanked because its words are shown by the other half of a covered pair
        // and the picture kept; with a delivery mirror (Arm) it is one of the only two
        // kinds of turn the boundary may drop as a duplicate picture.
        bool RunTail = false,
        bool RunHead = false,
        bool Blanked = false,

        // CB-270 stage 3b: a named-path turn whose page did not pair it with a
        // delivery-mirror, so it can still account for one on the page next to it.
        // The collapse of a mirror into the message naming its file is page-wide and
        // one named turn cancels one mirror, so across a page boundary only unpaired
        // named turns count.
        bool FreeNamed = false);

    // See HistoryTurn.Arm. Internal and small on purpose: this exists only to
    // let the post-parse dedup pass in OpenClawSessions tell an inline image
    // block from a delivery-mirror record apart, not to describe every way a
    // turn can carry a picture -- the named-path arm does not need a value
    // here, because its own collapse already keys on the resolved path rather
    // than on bytes, and giving it one anyway would be a distinction nothing
    // reads.
    internal enum MediaSourceArm { None, Inline, Mirror }

    // One OpenClaw session, as something the chat panel can talk to.
    //
    // Reading always works. Sending works when "Allow replying to agents" is on,
    // which re-pairs this device with `operator.write` — a deliberate act a
    // person approves on the gateway, never something opening a window does on
    // their behalf. Without it, sending says so rather than failing quietly.
    //
    // A send is routed by OpenClawSendPolicy: a conversation on a chat surface
    // is delivered back to that surface with the typed message mirrored into
    // it, and a native session, which has no surface, is answered in place.
    internal sealed class OpenClawChatSession : IRemoteChatSession, IRemoteChatBacklog, IRemoteChatComposer, IRemoteChatGatewayKey, IRemoteChatRunOptions, IRemoteChatImages, IRemoteChatPrompts
    {
        private readonly List<ChatTurn> _history = new();

        // The turn currently being streamed, if any. Held so an `agent` event
        // can update it in place rather than appending a row per delta.
        private ChatTurn? _streaming;

        // Which stream the turn above belongs to. An agent emits "thinking"
        // and then "assistant", each as its own growing snapshot, so they have
        // to become two turns — appending one to the other would produce a
        // paragraph that says the same thing twice in different voices.
        private string? _streamingKind;

        // CB-270 Stage 3: a run is rebuilt from the gateway's deltas, because on
        // 2026.10.5 only the first and last assistant events of a run carry
        // data.text and the rest carry data.delta alone (96 of 102 measured).
        // _runText is the run so far; _closed is the part of it already drawn as
        // finished bubbles, which a tool starting sets to everything so far. The
        // bubble on screen is what follows it, so the text after a tool is a
        // bubble of its own. That is what makes the live view equal history's
        // [step, tool line, step] for the same run.
        private string? _runId;
        private string _runText = "";
        private string _closed = "";

        // Whether this session saw the run from its first event, which carries
        // the text so far. A panel opened partway through sees a delta first, and
        // then the final snapshot (the whole rollup, earlier steps included) would
        // put steps the panel never drew, or history already holds, back into the
        // last bubble. So a run joined late is built from its deltas alone.
        private bool _sawRunStart = true;

        // Tool lines awaiting their result, by toolCallId, so the result can mark
        // the line it belongs to as an error.
        private readonly Dictionary<string, ChatTurn> _tools = new();

        // Turns already asked about, or being asked about — so a streaming
        // snapshot that still carries the marker on its next delta doesn't
        // fire a second gateway round trip for the same picture.
        private readonly HashSet<ChatTurn> _pendingImageChecks = new();

        public OpenClawChatSession(string sessionId, string gatewayKey, string displayName)
        {
            SessionId = sessionId;
            GatewayKey = gatewayKey;
            DisplayName = displayName;
        }

        public string SessionId { get; }

        // The gateway's own key, without the "openclaw:" prefix the app adds to
        // keep session ids in one namespace.
        public string GatewayKey { get; }

        // How many panels are bound to this conversation right now, and when
        // the last one let go — CB-92. A count rather than a flag because a
        // pinned panel and the transient can both be on the same session for a
        // moment while one is being rebound to the other, and a flag would have
        // the second close claim nobody is looking when somebody is.
        private int _panels;

        // Seeded at construction rather than left default so a session nobody
        // ever opens — the orb's speak button makes one, and so does a room
        // merging its members — ages on the same clock as one whose panel
        // closed. The alternative, treating "never had a panel" as never idle,
        // is precisely the case that accumulated silently.
        internal DateTime IdleSince { get; private set; } = DateTime.UtcNow;

        internal bool HasOpenPanel => _panels > 0;

        // The message subscription follows the count: held from the first panel
        // to the last, asked for by this chat itself so that a room's members,
        // which the room fans PanelOpened out to, each hold their own.
        internal void PanelOpened()
        {
            // What ended while no panel was watching is written up on opening.
            RefreshApprovals();

            if (_panels++ == 0) OpenClawSessions.MessagesWanted(this, true);
        }

        internal void PanelClosed()
        {
            var held = _panels > 0;
            if (held) _panels--;
            if (_panels == 0)
            {
                IdleSince = DateTime.UtcNow;

                // Only the real 1 to 0 transition: a stray extra close has
                // nothing to release.
                if (held) OpenClawSessions.MessagesWanted(this, false);
            }
        }

        // A subscribe the gateway refused is a fact about this one conversation,
        // not about the connection, so it is said here and not through the
        // settings pane's connection line.
        internal void NoteLiveUnavailable(string reason) =>
            Note("Live updates aren't available for this conversation: " + reason);

        // What this transcript's decoded pictures are costing.
        internal long ResidentImageBytes => OpenClawChatMemory.ResidentBytes(_history);

        // Give the pictures back, keep the words. Returns what was freed, which
        // is what makes "this actually released memory" something a test can
        // assert rather than something a comment claims.
        //
        // Safe to do behind a closed panel because the gateway is the source of
        // truth for a transcript: opening this conversation again runs
        // LoadHistoryAsync, which replaces the history wholesale through
        // SetHistory, pictures and all. Nothing here is the only copy of
        // anything — which is exactly why the bytes were worth holding onto
        // until now and not a moment longer.
        internal long ReleaseImages()
        {
            long freed = 0;

            foreach (var turn in _history)
            {
                if (turn.ImageBytes is not { Length: > 0 } bytes) continue;

                freed += bytes.Length;

                // Through the property, so the setter's change notification
                // fires. Inert for anything on screen — TurnView only listens
                // for bytes *arriving* late (see its PropertyChanged handler),
                // and a turn whose picture is already decoded keeps the Bitmap
                // it drew — and this only ever runs for a session with no panel
                // bound anyway.
                turn.ImageBytes = null;
            }

            return freed;
        }

        // Settable, because the name can improve after the session was created:
        // agents.list arrives moments after the connection does, so a panel
        // opened in that window would otherwise keep the raw id ("main") in its
        // header for as long as the app runs.
        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        // Where this conversation lives, when it lives somewhere — a Discord DM,
        // a channel. Null for a session with no channel behind it, which is the
        // signal not to mirror anything anywhere.
        public OpenClawSessions.Delivery? Delivery { get; set; }

        // What the gateway last said this conversation is, and whether it has
        // ever said it is a chat-surface one. Null Native is "never listed",
        // which is not the same as listed-and-not-native. SeenAsSurface is
        // sticky on purpose: a session that was a Discord DM a poll ago does not
        // become native on a transient classification. See OpenClawSendPolicy.
        internal OpenClawNativeKind? Native { get; private set; }
        internal bool SeenAsSurface { get; private set; }

        internal void Observe(OpenClawNativeKind? native)
        {
            if (native is null) return;

            Native = native;
            SeenAsSurface |= native == OpenClawNativeKind.None;
        }

        internal OpenClawSendRoute Route =>
            OpenClawSendPolicy.RouteFor(Native, SeenAsSurface, Delivery);

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public Task<ChatSendOutcome> SendAsync(string text) => SendCoreAsync(text, null, null);

        // CB-270 Stage 4: a native conversation takes pictures with its message.
        // The panel hands over files it already saved to disk; only the images
        // among them are sent, by name and content, and the first one is drawn on
        // the message's own bubble so it is not text-only until the gateway's
        // copy comes back.
        public async Task<ChatSendOutcome> SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths)
        {
            var images = new List<OpenClawOutgoingImage>();
            byte[]? thumbnail = null;

            foreach (var path in imagePaths)
            {
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(path); }
                catch { continue; }

                if (OpenClawOutgoingImage.From(path, bytes) is not { } image) continue;

                images.Add(image);
                thumbnail ??= bytes;
            }

            return await SendCoreAsync(text, images, thumbnail);
        }

        // Pictures go only to a conversation answered in place, and only when
        // replying is allowed: anywhere else a pasted picture stays the ordinary
        // paste it would have been, rather than becoming a message that then fails.
        public bool CanSendImages
        {
            get
            {
                Observe(OpenClawSessions.NativeOf(GatewayKey));
                return Route == OpenClawSendRoute.Native && OrbweaverSettings.OpenClawReplyEnabled;
            }
        }

        private async Task<ChatSendOutcome> SendCoreAsync(
            string text, IReadOnlyList<OpenClawOutgoingImage>? images, byte[]? thumbnail)
        {
            // The user's own turn is added here rather than by the panel, so one
            // thing owns the transcript and a send that fails leaves a message
            // on screen with an explanation under it rather than a ghost.
            // Mine, said here rather than inferred: this is the one turn in the
            // app whose author is not in doubt, and marking it keeps it matching
            // the copy that comes back from the gateway a moment later — which
            // is what lets a room dedupe the two instead of drawing both.
            //
            // CB-35: added before the replying-off check below, not after. It
            // used to be after — reachable only once replying was already
            // known to be on — so a session with replying off showed nothing
            // at all for a send: no message, no note, just a cleared
            // composer, which was indistinguishable from the app silently
            // eating a keystroke. OpenClawRoomChatSession's no-address path
            // always added the message first, and the two disagreeing about
            // something neither transport actually decides — where the
            // user's own words go — was the shape CB-35 asks to fix. Now both
            // read the same: your message is on screen, and the note under it
            // says why nothing happened, exactly like a room whose channel
            // has nobody to carry a message to.
            Observe(OpenClawSessions.NativeOf(GatewayKey));
            _pendingLimitMinutes = Route == OpenClawSendRoute.Native ? TimeoutMinutes : null;
            var mine = new ChatTurn
            {
                Role = ChatRole.User, Text = text, IsComplete = true, Mine = true,
                ImageBytes = thumbnail
            };
            _sent.Add(mine);
            if (_liveRunActive) _typedMidRun.Add(mine);
            Add(mine);

            if (!OrbweaverSettings.OpenClawReplyEnabled)
            {
                // A System turn rather than an exception: the person has just
                // typed a sentence, and losing it behind a dialog would be a
                // poor answer to "why didn't that send".
                Note("Replying is off. Turn on \"Allow replying to agents\" in Settings — "
                   + "it asks the gateway for permission to write, which you approve there.");
                return ChatSendOutcome.Failed;
            }

            var failure = await SendOrFailureAsync(text, images);
            if (failure is not null)
            {
                Note("Couldn't send: " + failure);
                return ChatSendOutcome.Failed;
            }

            return ChatSendOutcome.Sent;
        }

        // The request, and the catch around it, moved behind a method that
        // returns the failure instead of throwing it.
        //
        // Excluded from coverage because it is the gateway call, but the shape is
        // what matters: an await that always faults never resumes, so the line
        // that awaited it is reported unhit even though the catch beside it runs.
        // Returning the message rather than throwing it means the caller's await
        // completes, and the decision that reads it — say so in the transcript —
        // is measured where it belongs.
        [ExcludeFromCodeCoverage]
        private async Task<string?> SendOrFailureAsync(string text, IReadOnlyList<OpenClawOutgoingImage>? images)
        {
            try
            {
                await OpenClawSessions.SendAsync(this, text, CancellationToken.None, images);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // The notes this session wrote itself, as against a tool line the run drew.
        // Both are System turns, and a reconciliation has to tell them apart: a
        // note about a send belongs to the message it follows and is kept with it.
        private readonly HashSet<ChatTurn> _notes = new();

        // The messages this session added for the person who typed them, so an
        // append can tell one a page has not shown yet from one it has.
        private readonly HashSet<ChatTurn> _sent = new();

        // Those of them typed while a run was already streaming. Such a message is
        // not what the run began after, wherever a page happens to place it, so a
        // reconciliation never takes it as the anchor: the run began before it.
        private readonly HashSet<ChatTurn> _typedMidRun = new();

        // Whether the stream says a run is going right now: any event of one since
        // the last end. Wider than "has drawn something", because a panel opened
        // partway through a run can be typed into before the next word draws, and
        // that message still comes after the run began.
        private bool _liveRunActive;

        private void Note(string text)
        {
            var note = new ChatTurn
            {
                Role = ChatRole.System,
                IsComplete = true,
                Text = text
            };

            _notes.Add(note);
            Add(note);
        }

        public void Cancel()
        {
            // Nothing to cancel while this is read-only. Stopping someone else's
            // run — one started from Discord or a cron schedule — is not
            // something a viewer should be able to do by accident anyway.
        }

        // Fed from OpenClawSessions' event stream. Everything here is already on
        // the UI thread, which is the contract IRemoteChatSession states and the
        // panel relies on.
        public void OnAgentEvent(string name, JsonElement payload)
        {
            switch (name)
            {
                case "agent" when Str(payload, "stream") == "lifecycle":
                    // The run's own end. Nothing else completes a native run's
                    // bubble: without this the next run's first words overwrote
                    // the previous reply in place.
                    if (payload.TryGetProperty("data", out var life)
                        && Str(life, "phase") is "end" or "error")
                    {
                        EndRun();
                    }
                    else
                    {
                        _liveRunActive = true;
                    }
                    break;

                case "agent":
                    _liveRunActive = true;
                    OnAgentText(payload);
                    break;

                case "session.tool":
                    _liveRunActive = true;
                    OnTool(payload);
                    break;

                case "cron" when Str(payload, "action") == "finished":
                case "task" when Str(payload, "action") == "upserted":
                    EndRun();
                    break;

                // CB-95: what the gateway calls a *delivery* rather than a
                // stream — a picture or a plain text message someone else
                // sent into this conversation, recorded as a chat.history row
                // (a "delivery-mirror" for media, an ordinary message for
                // text) rather than as an "agent" stream event. Neither event
                // name's payload is trusted for its own fields here: the
                // gateway's documented shape has already been wrong for other
                // events (see docs/openclaw-findings.md), and chat.history is
                // the one read this app has actually confirmed against a live
                // gateway. So this treats the event as a doorbell — something
                // landed, go and look — and re-reads the newest page the same
                // way TryResolveLiveImage already does below for pictures.
                //
                // AppendNewTail rather than SetHistory: a wholesale replace
                // while the user is mid-scrollback would throw them back to
                // the bottom for a delivery they may not even be reading yet
                // — the objection CB-95 itself raises against "re-read
                // history on a delivery signal". Appending only the turns the
                // fetched page adds past what this transcript already has
                // keeps a read position exactly where it was.
                //
                // A "chat" delta is not one of those doorbells (CB-270). Its text
                // has already arrived on the agent stream, and treating every one
                // as a reason to refetch history cost 25 round trips for one run.
                case "chat" when Str(payload, "state") is "final" or "error" or "aborted":
                    EndRun();
                    RefreshTailAsync();
                    break;

                // Any other state of a run (a delta, a status) says one is going.
                case "chat":
                    _liveRunActive = true;
                    break;

                case "session.message":
                    RefreshTailAsync();
                    break;
            }
        }

        // Fire-and-forget from OnAgentEvent, which is synchronous — events
        // arrive through OpenClawSessions.OnEvent's Dispatcher.Post and
        // nothing there awaits this. No explicit try/catch: FetchPageAsync
        // already swallows a gateway that will not answer and returns null,
        // the same contract TryResolveLiveImage already trusts without one of
        // its own. A delivery that could not be confirmed is not a reason to
        // disrupt the conversation already on screen — the next successful
        // delivery, or a reopened panel, catches up regardless.
        [ExcludeFromCodeCoverage]
        private async void RefreshTailAsync()
        {
            var page = await OpenClawSessions.FetchPageAsync(this, 0, CancellationToken.None);
            if (page is null) return;

            ApplyFetchedTail(page.Value.Turns);
        }

        // CB-270. Live text is a best-effort drawing of a run: a panel opened
        // partway through it holds a truncated first bubble and none of the tool
        // lines that came before it, and no amount of appending repairs that. So
        // a run the stream has drawn is replaced by what history says at its end,
        // which makes the two agree under whatever rule TurnsFromHistory applies.
        //
        // Set when a run's event draws something and cleared by the reconciliation
        // that follows its end. A run that has drawn nothing, a bare lifecycle
        // start, has nothing to defer for. While it is set and the run has not ended, a doorbell
        // does nothing: a page fetched mid-run holds a half-persisted run, and
        // appending it would only add to what the end is about to replace.
        // The deferral is bounded by the run state the app already trusts for the
        // orb (OpenClawSessions.StateFor): a run that state no longer calls
        // generating, because its end arrived on another signal, RunIdle passed
        // after its last streaming event, RunCeiling after a start, or the
        // connection dropped and cleared every run, is over for this purpose too.
        // A missed lifecycle end or chat final therefore cannot stop the panel
        // refreshing.
        private bool _runDirty;
        private bool _runEnded;

        // CB-270 Stage 4. A run this session drew is waiting to be told how it
        // ended, and the limit the last send asked for is what a timeout names.
        // The row's status arrives with the next poll, so the note is written then:
        // a timed-out run otherwise just stops, and the reply looks cut off for no
        // reason. Only a run this panel saw counts, so a conversation opened on an
        // old timeout does not announce it.
        private static readonly HashSet<string> TerminalStatuses =
            new(StringComparer.Ordinal) { "done", "failed", "error", "killed", "aborted", "timeout" };

        private bool _awaitingStatus;

        // The limit a send asked for belongs to the run that send starts, and to no
        // other: it is pending from the send until the next run begins drawing, and
        // that run keeps it. A run another client started has none, so its timeout
        // is announced without a number rather than with this send's.
        private int? _pendingLimitMinutes;
        private int? _runLimitMinutes;

        internal void OnRunStatus(string? status)
        {
            // Only a status that says the run is over settles it. One that says it is
            // still going ("running", read on the first poll after a send) leaves it
            // waiting, and so does anything not recognised.
            if (!_awaitingStatus || status is null || !TerminalStatuses.Contains(status)) return;

            // So a later timeout (another client's run, say) is not announced with
            // this send's limit.
            _awaitingStatus = false;
            if (status != "timeout") return;

            Note(_runLimitMinutes is { } minutes
                ? $"The time limit ({minutes} minutes) was reached, so the run was stopped."
                : "The run's time limit was reached, so the run was stopped.");
        }

        // The first row the live run drew in this transcript, by reference so a
        // prepend or the cap cannot shift it. The run's user turn is before it,
        // however much of the run was already on the page when the panel opened.
        // Null until something is drawn after a wholesale replace.
        private ChatTurn? _runFirst;

        // The last row of the page SetHistory installed, by reference. Everything
        // after it was added later, a message typed after the page landed among
        // it, and none of it can be the user turn a run began after.
        private ChatTurn? _pageLast;

        // CB-270 stage 3b: the transcript's head rollup that closes a run whose
        // earlier steps may be on an older page (see PrependHistory), or null.
        private ChatTurn? _seamRollup;

        // CB-270 stage 3b: the turns of the transcript's head run when an older page may
        // precede it, so the page boundary can compare their pictures with the older
        // page's trailing run.
        private List<ChatTurn> _headRun = new();

        // CB-270 stage 3b: every turn of the page next to the one about to be
        // prepended, for the page-wide, budgeted collapse of a delivery-mirror into the
        // message naming its file (see DedupePicturesAcrossTheSeam).
        private List<ChatTurn> _headPage = new();

        private void RunSeen()
        {
            // A run beginning: it takes the limit the last send asked for, if one
            // is waiting, and nothing otherwise.
            if (!_awaitingStatus)
            {
                _runLimitMinutes = _pendingLimitMinutes;
                _pendingLimitMinutes = null;
            }

            _awaitingStatus = true;
            if (!_runDirty) _runFirst = null;
            _runDirty = true;
            _runEnded = false;
        }

        private void EndRun()
        {
            _liveRunActive = false;
            _runEnded = true;
            Complete();
        }

        // Removed from the transcript: the index of the first, and how many. The
        // panel drops the same rows; what replaces them arrives as TurnAdded.
        internal event Action<int, int>? TurnsRemoved;

        // What to do with a freshly fetched newest page: nothing mid-run, a
        // reconciliation once a run the stream drew has ended, and the ordinary
        // append for a delivery that is not part of a run.
        internal void ApplyFetchedTail(IReadOnlyList<HistoryTurn> page)
        {
            if (_runDirty && !_runEnded
                && OpenClawSessions.StateFor(GatewayKey) == "generating") return;

            if (!_runDirty)
            {
                AppendNewTail(page);
                return;
            }

            _runDirty = false;
            ReconcileRun(page);
        }

        // The run is everything after the user turn that started it, and the live
        // tail is swapped for the page's after that same turn.
        //
        // That turn is the last user turn before the first row the run drew, not
        // simply the last user turn: a message typed while the reply streams is
        // added at once and would otherwise become the anchor, which a page may
        // not hold yet and which sits after a truncated first bubble that
        // reconciling from it would never reach. Candidates are tried latest
        // first and the first one the page also holds wins, so a message typed
        // here that the page does not show yet is passed over rather than
        // trusted. Not an index recorded when the run began, which a panel opened
        // midway through has no way to know.
        //
        // Your own messages in the replaced range are kept: one the page now
        // shows is the page's copy and is drawn once, one it does not show yet is
        // put back after the page's turns until a page does. If no candidate is on
        // the page (a run too long for it to reach back) the append is all that
        // can be said safely.
        private void ReconcileRun(IReadOnlyList<HistoryTurn> page)
        {
            var first = _runFirst is null ? -1 : _history.IndexOf(_runFirst);
            var afterPage = _pageLast is null ? -1 : _history.IndexOf(_pageLast) + 1;
            var before = first >= 0 ? first : afterPage > 0 ? afterPage : _history.Count;

            var user = -1;
            var at = -1;

            for (var u = before - 1; u >= 0 && at < 0; u--)
            {
                if (_history[u].Role != ChatRole.User || _typedMidRun.Contains(_history[u])) continue;

                for (var i = page.Count - 1; i >= 0; i--)
                {
                    if (page[i].Role == ChatRole.User
                        && string.Equals(page[i].Text, _history[u].Text, StringComparison.Ordinal))
                    {
                        user = u;
                        at = i;
                        break;
                    }
                }
            }

            if (at < 0)
            {
                AppendNewTail(page);
                return;
            }

            var from = user + 1;
            var count = _history.Count - from;

            // Sent here and not on the page yet: each page copy accounts for one.
            var shown = page.Skip(at + 1).Where(t => t.Role == ChatRole.User).Select(t => t.Text).ToList();
            // A note this session wrote straight after one of them, "Couldn't send"
            // or "Replying is off", goes with it: the reason belongs to the message
            // and the message is staying.
            var unconfirmed = new List<ChatTurn>();
            for (var i = from; i < _history.Count; i++)
            {
                var turn = _history[i];
                if (turn.Role != ChatRole.User || shown.Remove(turn.Text)) continue;

                unconfirmed.Add(turn);
                while (i + 1 < _history.Count && _notes.Contains(_history[i + 1]))
                {
                    unconfirmed.Add(_history[++i]);
                }
            }

            if (count > 0)
            {
                _history.RemoveRange(from, count);
                TurnsRemoved?.Invoke(from, count);
            }

            // On the normal end path Complete() has already let go of the bubble
            // in flight and the tool lines awaiting results. On the idle bound a
            // doorbell reconciles while the stream may still be going, so they
            // are let go of here: a delta would otherwise update a turn with no
            // row and a result would mark one, and the reply would be invisible
            // until the real end. _runText and _closed are left as they are, since
            // the step in flight is not persisted until it ends: the page cannot
            // hold it, and the next delta opens a fresh bubble from what follows
            // _closed.
            if (_streaming is not null && !_history.Contains(_streaming))
            {
                _streaming = null;
                _streamingKind = null;
            }

            foreach (var id in _tools.Where(t => !_history.Contains(t.Value)).Select(t => t.Key).ToList())
            {
                _tools.Remove(id);
            }

            AppendFrom(page, at + 1);
            foreach (var turn in unconfirmed) Add(turn);
        }

        // The actual live-update fix, and the half of it that is pure enough
        // to test without a gateway: given the newest page of chat.history,
        // work out which of its turns this transcript does not have yet and
        // add only those.
        //
        // A delivered message lands at the very end of the conversation, so
        // the freshly-fetched page's own tail either matches this
        // transcript's tail exactly (nothing new — most calls, since the
        // "chat"/"session.message" doorbell fires for reasons other than a
        // delivery too) or extends past it (a real delivery). The anchor is
        // this transcript's own last turn: its most recent matching
        // occurrence in the fetched page marks where "already known" ends and
        // "new" begins. Matching on role and text rather than on an id,
        // because chat.history hands back the same role/content shape
        // SetHistory and PrependHistory already build ChatTurns from —
        // nothing here carries a message id to match on instead.
        //
        // An empty transcript takes everything the page has; a transcript
        // whose anchor cannot be found on the page at all — more turns
        // arrived than one page holds, or a page boundary moved the anchor
        // off it — appends nothing rather than guessing, which costs exactly
        // the staleness this ticket already describes rather than risking a
        // duplicated or reordered transcript.
        internal void AppendNewTail(IReadOnlyList<HistoryTurn> freshTurns)
        {
            if (freshTurns.Count == 0) return;

            int startIndex;

            // Without this session's own notes, and the messages it sent that no
            // page shows yet: neither is on a page, so at the tail either would be
            // the anchor, match nothing, and stop every later delivery appearing (a
            // send that failed leaves both). Each copy a page does show accounts for
            // one sent message, which is then ordinary again. They stay in the
            // transcript; only the search for where the page joins looks past them.
            var shown = freshTurns.Where(t => t.Role == ChatRole.User).Select(t => t.Text).ToList();
            var live = new List<ChatTurn>();
            foreach (var turn in _history)
            {
                if (_notes.Contains(turn)) continue;
                if (_sent.Contains(turn) && !shown.Remove(turn.Text)) continue;
                live.Add(turn);
            }

            if (live.Count == 0)
            {
                startIndex = 0;
            }
            else
            {
                // The page position whose turns before it also match this
                // transcript's tail, best first and latest on a tie. Matching the
                // last turn alone is ambiguous whenever two turns read the same,
                // which two tool lines do once the summary's length cap has cut
                // them to the same text: the later twin would be taken for the
                // earlier and everything between them skipped.
                var anchorIndex = -1;
                var best = 0;

                for (var i = freshTurns.Count - 1; i >= 0; i--)
                {
                    var matched = 0;
                    while (matched <= i && matched < live.Count
                           && freshTurns[i - matched].Role == live[^(matched + 1)].Role
                           && string.Equals(freshTurns[i - matched].Text, live[^(matched + 1)].Text,
                               StringComparison.Ordinal))
                    {
                        matched++;
                    }

                    if (matched > best)
                    {
                        best = matched;
                        anchorIndex = i;
                    }
                }

                if (anchorIndex < 0) return;

                startIndex = anchorIndex + 1;
            }

            AppendFrom(freshTurns, startIndex);
        }

        private void AppendFrom(IReadOnlyList<HistoryTurn> freshTurns, int startIndex)
        {
            for (var i = startIndex; i < freshTurns.Count; i++)
            {
                var turn = freshTurns[i];

                // Add() rather than a batch insert: it is the one place that
                // both fires TurnAdded — what makes the panel actually draw
                // the new bubble without being reopened — and enforces the
                // 500-turn cap, so a burst of deliveries behaves exactly like
                // a burst of streamed replies would.
                Add(new ChatTurn
                {
                    Role = turn.Role,
                    Text = turn.Text,
                    ImageSourcePath = turn.ImageSourcePath,
                    Attachment = turn.Attachment,
                    Confidence = turn.Confidence,
                    ImageUrl = turn.ImageUrl,
                    ImageBytes = turn.ImageBytes,
                    ImageAlt = turn.ImageAlt,
                    At = turn.At,
                    Speaker = turn.Speaker,
                    SpeakerColor = turn.SpeakerColor,
                    Mine = turn.Mine,
                    IsError = turn.IsError,
                    FromRollup = turn.FromRollup,
                    IsComplete = true
                });
            }
        }

        private void OnAgentText(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;

            // Thinking is shown — watching an agent think is most of the value
            // of an orb that pulses — but kept as its own turn rather than
            // mixed into the reply it will eventually give.
            var kind = Str(payload, "stream") ?? "assistant";

            // The reply is rebuilt from deltas, with data.text taken as a full
            // snapshot whenever one arrives (the first and last events of a run).
            // A thinking event carries only progressTokens, never text, so it
            // keeps the old snapshot-only reading.
            var text = kind == "assistant" ? AssistantBubble(payload, data) : Str(data, "text");
            if (string.IsNullOrEmpty(text)) return;

            RunSeen();

            if (_streaming is null || _streaming.IsComplete || _streamingKind != kind)
            {
                _streaming = new ChatTurn
                {
                    Role = kind == "thinking" ? ChatRole.System : ChatRole.Assistant,
                    Text = text
                };
                _streamingKind = kind;
                Add(_streaming);
                _runFirst ??= _streaming;
            }
            else
            {
                _streaming.Text = text;
                TurnUpdated?.Invoke(_streaming);
            }

            // A live snapshot never carries the picture itself — only a
            // TurnsFromHistory read of chat.history does (see
            // OpenClawSessions.BestImageMatch) — so a reply that mentions an
            // attachment is worth asking the gateway about, once, rather than
            // leaving it as text-only until the panel happens to reload.
            if (text.Contains(OpenClawSessions.MediaAttachedMarker, StringComparison.Ordinal))
            {
                TryResolveLiveImage(_streaming);
                return;
            }

            // A picture the agent generated itself and named by its own path
            // on the gateway host — see LocalMediaPathFrom's own comment for
            // why this is a second, distinct convention from the marker
            // above rather than the same one.
            var localPath = OpenClawSessions.LocalMediaPathFrom(text);
            if (localPath is not null)
            {
                var candidate = localPath.Value;

                // No page to harvest a real directory from while streaming
                // (see ResolveLocalMediaPath's own comment) — a bare filename
                // gets the shared-media-directory guess, the same one the
                // history read falls back to when its own harvest misses.
                // CB-115: only when that resolved to a real path. A bare
                // filename resolves to the shared-media *guess*, and asking
                // for it here would be worse than useless — TryResolveLocalMedia
                // and TryResolveLiveImage share the one-shot _pendingImageChecks
                // guard, so a guess that is about to 404 would consume this
                // turn's single attempt and the run-record recovery below would
                // never run. That is exactly how this feature was inert for the
                // pictures it exists for.
                //
                // So the authoritative source goes first: a rooted path is
                // fetched directly, a bare filename falls through to the run
                // record, and nothing is lost either way — if the recovery
                // finds nothing, the refetched page's own named-path arm
                // applies the same guess this branch would have.
                var resolved = OpenClawSessions.ResolveLocalMediaPath(candidate.Path, null);
                if (resolved != OpenClawSessions.SharedMediaDir + candidate.Path)
                {
                    // CB-116: no openclawAutomation to consult here — a live
                    // "agent" event carries only the streamed text, not the
                    // message envelope TurnsFromHistory reads it off. So the
                    // only provenance available live is whether the agent
                    // wrote an explicit "MEDIA:" line; a trailing-token match
                    // with no automation to confirm it is tiered Low, the same
                    // conservative default a history read would give the
                    // identical text on a non-automation turn.
                    var confidence = candidate.Explicit
                        ? MediaConfidence.High
                        : MediaConfidence.Low;

                    TryResolveLocalMedia(_streaming, resolved, confidence);
                    return;
                }
            }

            // CB-115: a cron-delivered picture whose transcript has already
            // lost its directory — neither arm above could have resolved it,
            // since it either shares its line with a caption (failing
            // LocalMediaPathFrom's whole-trimmed-text rule) or carries no
            // MEDIA: prefix at all (the delivery route strips both). Reusing
            // TryResolveLiveImage rather than adding a fourth live-path arm:
            // the real recovery — reading openclawAutomation.kind off the
            // *actual* chat.history message, calling cron.runs, matching the
            // basename — happens identically whether this page is fetched
            // for the marker above or for this, inside
            // OpenClawSessions.FetchHistoryPageAsync's turn-by-turn pass (see
            // OpenClawCronRecovery's header for the full trigger). This check
            // only decides whether asking is worth it at all — the same job
            // the marker check above already does for its own case — so a
            // reply that merely ends in something image-shaped costs one
            // chat.history refetch and nothing more if it turns out not to be
            // a recoverable cron picture.
            if (OpenClawCronRecovery.CandidateBasenameFrom(text) is not null)
            {
                TryResolveLiveImage(_streaming);
            }
        }

        // Best-effort and one-shot per turn, the same shape as TurnView's own
        // LoadImage: a picture that doesn't resolve is a picture that doesn't
        // resolve, and the text beside it still reads. Fetching the newest
        // page rather than matching on anything in the event itself, because
        // nothing in a live "agent" event ties back to a chat.history message
        // — see BestImageMatch's own comment.
        //
        // No try/catch around the fetch: FetchPageAsync already swallows a
        // gateway that will not answer and returns null, the same contract
        // LoadOlderAsync already trusts without one of its own — adding a
        // second catch here would only ever guard against nothing.
        //
        // No explicit Dispatcher.Post after the await: this runs under the
        // app's own Avalonia SynchronizationContext, which already resumes an
        // await's continuation on the UI thread — the same reason TurnView's
        // LoadImage sets its own bound property directly.
        private async void TryResolveLiveImage(ChatTurn turn)
        {
            if (!_pendingImageChecks.Add(turn)) return;

            var page = await OpenClawSessions.FetchPageAsync(this, 0, CancellationToken.None);
            if (page is null) return;

            // Restricted to this turn's own role: a session's own
            // chat.history mixes the agent's replies with everyone else's
            // messages arriving as input to it (see OpenClawRoomChat's own
            // header comment), and a picture someone else posted moments
            // before or after the agent's reply is not the agent's picture —
            // matching across roles would occasionally attribute the wrong
            // one in a busy room.
            var match = OpenClawSessions.BestImageMatch(page.Value.Turns, turn.Role, turn.At);
            if (match is null) return;

            // Whichever form the matched turn actually carries. On this
            // gateway it is always the inline bytes (CB-91); the url arm is
            // kept for a deployment that sends one instead. Setting only
            // ImageUrl, as this did before, resolved to nothing at all here.
            //
            // No third arm for "neither": BestImageMatch only returns a turn
            // that has one or the other, so restating that here would be a
            // branch nothing could ever take.
            if (match.Value.ImageBytes is { Length: > 0 } matchedBytes) turn.ImageBytes = matchedBytes;
            else
            {
                // ImageSourcePath before ImageUrl, always. It is a plain
                // property and raises nothing; ImageUrl's setter is what wakes
                // the row up (see TurnView's PropertyChanged handler), so
                // setting the url first would send it fetching before the path
                // it needs to caption a refusal had arrived.
                //
                // Carried across rather than left null: the match came out of
                // a TurnsFromHistory page for *this* session, so both the url
                // and the path are already the right ones, and dropping the
                // path here would mean a live reply's picture lost the ability
                // to say why it did not load — which is what the history path
                // does say (CB-93).
                //
                // Confidence travels with it for the same reason (CB-116):
                // setting ImageUrl below is what wakes TurnView.LoadImage up
                // (see its PropertyChanged handler), and that is where this
                // value gets read. Leaving it at the turn's default here would
                // silently promote whatever tier the matched turn actually
                // carries to High.
                turn.ImageSourcePath = match.Value.ImageSourcePath;
                turn.Attachment = match.Value.Attachment;
                turn.Confidence = match.Value.Confidence;
                turn.ImageUrl = match.Value.ImageUrl;
            }

            turn.ImageAlt = match.Value.ImageAlt;

            // OpenClawRoomChat only rebuilds a room's merged view on this
            // event (see its chat.TurnUpdated subscription) — the
            // PropertyChanged the setters above already raised reaches a
            // direct (non-room) panel through TurnView's own subscription,
            // but a room's Rebuild() has to be asked separately.
            TurnUpdated?.Invoke(turn);
        }

        // CB-88: an agent's own generated picture, named by its own path on
        // the gateway host rather than fetchable by URL — fetched through the
        // gateway's own read-scoped media route (see
        // OpenClawSessions.FetchLocalMediaAsync), with LocalMediaPathFrom
        // deciding what counts as such a reference. Same one-shot-per-turn
        // guard as TryResolveLiveImage; the two never fire for the same turn
        // since OnAgentText only ever detects one marker or the other.
        //
        // confidence travels from OnAgentText's own read of the candidate
        // (CB-116) — carried in rather than re-derived from path, which by
        // this point is already resolved and has lost whether it came from an
        // explicit "MEDIA:" line or a bare trailing token.
        private async void TryResolveLocalMedia(
            ChatTurn turn, string path, MediaConfidence confidence)
        {
            if (!_pendingImageChecks.Add(turn)) return;

            // Set before the fetch, the same order LoadImage's twin uses for
            // ImageSourcePath: whoever knows the reason writes it down before
            // anything that might act on it, rather than the panel asking a
            // question this method already had the answer to.
            turn.Confidence = confidence;

            // The path *and* whose conversation named it (CB-109). This method
            // is on the session, so GatewayKey is simply in hand — the seam
            // was never missing here, only unused: the fetch below asked the
            // gateway about a file without saying which agent's media policy
            // to judge it against, and got a default agent's answer.
            var media = OpenClawSessions.MediaSourceFor(path, GatewayKey, OpenClawSessions.OwnerIndexSnapshot());

            // CB-272: a clip is never fetched here. The panel draws a tile
            // from the gateway's meta answer and fetches the bytes only when
            // it is clicked, so all this does is hand the row what it needs
            // to ask — the path and the finished route — and let TurnView
            // take it from there. ImageUrl last, since its setter is what
            // wakes the row (see TryResolveLiveImage's note on that order).
            // The bytes must never land in ImageBytes: that is the picture
            // path, which decodes whatever it is handed.
            if (OpenClawVideo.IsVideoPath(path))
            {
                turn.ImageSourcePath = media.Path;
                turn.ImageAlt = path[(path.LastIndexOf('/') + 1)..];
                turn.ImageUrl = media.Route;

                TurnUpdated?.Invoke(turn);
                return;
            }

            var bytes = await OpenClawSessions.FetchLocalMediaAsync(media, CancellationToken.None);
            if (bytes is { Length: > 0 })
            {
                turn.ImageBytes = bytes;

                // See TryResolveLiveImage's identical comment: OpenClawRoomChat
                // needs its own nudge to rebuild, beyond the PropertyChanged the
                // setter above already raised.
                TurnUpdated?.Invoke(turn);
                return;
            }

            // CB-116: a low-confidence candidate — ordinary prose that merely
            // ends in something filename-shaped, with nothing to say this
            // turn was a real delivery — stays silent on a failure rather
            // than asking the gateway why and showing a confident-sounding
            // note for a picture that was never real. See
            // MediaConfidence for the full reasoning.
            if (confidence != MediaConfidence.High) return;

            // CB-93: the fetch above came back empty, which used to leave the
            // turn as bare "MEDIA:<path>" text with no explanation. No
            // ShouldAskWhy guard needed here, unlike TurnView.LoadImage's
            // history-path twin: this method is only ever reached with a
            // path LocalMediaPathFrom already matched to the gateway's
            // read-scoped media route, so there is no ordinary attachment
            // url to protect against asking a meta question that has no
            // answer.
            // Asked against the same url the fetch above used, so the reason
            // describes the request that actually failed. See
            // FetchLocalMediaMetaAsync's own comment: an explanation asked
            // with a different identity than the fetch does not fail, it lies.
            var json = await OpenClawSessions.FetchLocalMediaMetaAsync(
                media.Route, CancellationToken.None);
            turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, media.Path);
            turn.ImageNote = OpenClawMediaRefusal.Explain(json);

            TurnUpdated?.Invoke(turn);
        }

        // The text of the bubble the run is in now, or null when there is none.
        private string? AssistantBubble(JsonElement payload, JsonElement data)
        {
            // A different run never continues the previous one's bubble. Today's
            // overwrite of run 1's reply by run 2's first words is what this is.
            var runId = Str(payload, "runId");
            if (!string.IsNullOrEmpty(runId) && runId != _runId)
            {
                Complete();
                _runId = runId;
            }

            var snapshot = Str(data, "text");
            var delta = Str(data, "delta");

            // A snapshot is trusted only where it agrees with what has already
            // been drawn as finished bubbles, and only for a run seen from its
            // start. One that does not agree (never measured) cannot say where the
            // current bubble starts, and guessing would either repeat a step or
            // cut one short, so it is ignored.
            if (!string.IsNullOrEmpty(snapshot))
            {
                if (_sawRunStart && snapshot.StartsWith(_closed, StringComparison.Ordinal)) _runText = snapshot;
            }
            else if (!string.IsNullOrEmpty(delta))
            {
                // A run's first event carries text; a delta with nothing before it
                // means this session joined partway.
                if (_runText.Length == 0) _sawRunStart = false;
                _runText += delta;
            }
            else return null;

            return _runText[_closed.Length..].Trim();
        }

        private void OnTool(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;

            var phase = Str(data, "phase");
            var tool = Str(data, "name");
            var id = Str(data, "toolCallId");

            if (phase == "result")
            {
                if (id is not null && _tools.Remove(id, out var line))
                {
                    // The flag, never the text: AppendNewTail anchors on text, and
                    // a line that reworded itself when its tool finished would not
                    // match its history twin.
                    if (Bool(data, "isError") == true)
                    {
                        line.IsError = true;
                        TurnUpdated?.Invoke(line);
                    }

                    return;
                }

                // A result for a start this client never saw. If it says what the
                // tool was, draw the line now rather than lose the call.
                if (string.IsNullOrEmpty(tool)) return;
                AddToolLine(tool, data, Bool(data, "isError") == true);
                return;
            }

            if (phase != "start" || string.IsNullOrEmpty(tool)) return;

            // The text so far is a step, and what comes after the tool is the
            // next: close the bubble here so the tool line lands between them.
            if (_streaming is { IsComplete: false } && _streamingKind == "assistant")
            {
                _streaming.IsComplete = true;
                TurnUpdated?.Invoke(_streaming);
                _streaming = null;
                _streamingKind = null;
                _closed = _runText;
            }

            var added = AddToolLine(tool, data, false);
            if (id is not null) _tools[id] = added;
        }

        // One line per tool call, in the transcript rather than in a status
        // area: what an agent reached for is part of what it said. The same
        // summariser history uses, fed the same arguments object, so the two
        // paths produce the identical line.
        private ChatTurn AddToolLine(string tool, JsonElement data, bool isError)
        {
            var args = data.TryGetProperty("args", out var a) ? a : default;
            var turn = new ChatTurn
            {
                Role = ChatRole.System,
                IsComplete = true,
                IsError = isError,
                Text = ChatTranscript.ToolSummary(tool, args)
            };

            RunSeen();
            Add(turn);
            _runFirst ??= turn;
            return turn;
        }

        private void Complete()
        {
            // A run is over whether or not a bubble was open for it.
            _tools.Clear();
            _runText = "";
            _closed = "";
            _runId = null;
            _sawRunStart = true;

            if (_streaming is null) return;

            _streaming.IsComplete = true;
            TurnUpdated?.Invoke(_streaming);
            _streaming = null;
            _streamingKind = null;
        }

        private void Add(ChatTurn turn)
        {
            _history.Add(turn);

            // Generous, because this is now the only thing that discards
            // anything: at 60 a busy conversation dropped its own beginning
            // while you were reading it, which is what "stuff disappears"
            // looked like. Scrolling back can load more than this, so the cap
            // is high enough that reaching it means a genuinely enormous
            // scrollback rather than an ordinary afternoon.
            const int Keep = 500;
            if (_history.Count > Keep) _history.RemoveRange(0, _history.Count - Keep);

            TurnAdded?.Invoke(turn);
        }

        // The backlog, once the gateway has told us what it is. Replaces
        // whatever is there rather than merging: this arrives moments after the
        // panel opens, and the alternative is reconciling two orderings of the
        // same conversation for the sake of a turn or two that might have
        // landed in between.
        //
        // Historical turns are marked complete so a live reply that arrives
        // next starts its own row instead of appending to the last thing
        // somebody said an hour ago.
        // How many of the gateway's own messages this transcript has consumed,
        // which is the offset the next page back starts at. Not the same as the
        // number of turns: one message can be text plus three pictures, and
        // some are dropped as scaffolding.
        public int LoadedMessages { get; set; }

        // False once the gateway answers a page with nothing left to give.
        public bool HasMore { get; set; } = true;

        // The fetch itself lives on OpenClawSessions, which owns the connection;
        // this is only the seam the panel reaches it through, so the panel does
        // not have to know that a page comes from a gateway rather than a file.
        public Task<bool> LoadOlderAsync(CancellationToken ct) =>
            OpenClawSessions.LoadOlderAsync(this, ct);

        // --- run controls (CB-270 Stage 4) -----------------------------------

        public event Action? RunOptionsChanged;

        public int? TimeoutMinutes { get; set; }

        // Called on the UI thread when the run state or the model catalogue moved.
        internal void OnRunOptionsChanged() => RunOptionsChanged?.Invoke();

        // CB-270 Stage 6: the pending approvals for this session changed (one
        // arrived, one was resolved, or the poll reconciled). Called on the UI
        // thread by OpenClawSessions, which has already updated PendingFor(key); the
        // panel side reads that and redraws its prompt, and drains TakeEnded for the
        // lines about approvals it did not answer itself.
        internal event Action? ApprovalsChanged;

        internal void OnApprovalsChanged()
        {
            RefreshApprovals();
            ApprovalsChanged?.Invoke();
        }

        // --- approvals (CB-270 Stage 6) ----------------------------------------
        //
        // The first unexpired pending approval to arrive for this conversation is
        // the prompt, in the order the store gives and never sorted by the gateway's
        // own createdAt (which a hostile gateway could set to jump the queue), read from OpenClawSessions every time it is asked, so a panel
        // that opens after the request arrived shows it too. The rest queue behind
        // it. The two answers are the client's, fixed, and every option is bound
        // to the approval it was drawn for.

        public event Action? PromptChanged;

        // Ids with an answer in flight from this panel: hidden from the bar at the
        // click so a second click has nothing to land on, and restored if the answer
        // failed and the request is still pending.
        private readonly HashSet<string> _answering = new(StringComparer.Ordinal);

        private string? _shownPromptKey;

        private OpenClawApproval? CurrentApproval() =>
            OpenClawSessions.PendingFor(GatewayKey).FirstOrDefault(a => !_answering.Contains(a.Id));

        private ChatPrompt PromptFor(OpenClawApproval approval) =>
            OpenClawApprovalText.Prompt(
                approval.Id, DisplayName, approval.Command, approval.Cwd, approval.Host,
                commandTruncated: approval.CommandTruncated,
                allowedDecisions: approval.AllowedDecisions, warning: approval.Warning,
                unsafeToShow: approval.Unsafe, commandLength: approval.CommandLength > 0 ? approval.CommandLength : null);

        // Remembers what it handed out, so PromptChanged is raised only when the bar
        // would differ from what the panel last drew.
        public ChatPrompt? Prompt
        {
            get
            {
                var approval = CurrentApproval();
                var prompt = approval is null ? null : PromptFor(approval);
                _shownPromptKey = approval is null ? null : approval.Id + "|" + prompt!.Title;
                return prompt;
            }
        }

        // Writes up what ended without this panel answering it, once each, and
        // redraws the bar if what it shows changed.
        private void RefreshApprovals()
        {
            foreach (var ended in OpenClawSessions.TakeEnded(GatewayKey)) Note(EndedLine(ended));

            var key = CurrentApproval() is { } approval ? approval.Id + "|" + PromptFor(approval).Title : null;
            if (key == _shownPromptKey) return;

            _shownPromptKey = key;
            PromptChanged?.Invoke();
        }

        private static string EndedLine(OpenClawApprovalEnded ended)
        {
            if (ended.Cause == "evicted") return OpenClawApprovalText.Evicted;

            // The status the gateway declared; failing that, what a resolved event's
            // decision says; failing that, the local clock's expiry.
            var declared = ended.Status is null or OpenClawApprovals.UnknownStatus ? null : ended.Status;
            var status = declared ?? ended.Decision switch
            {
                OpenClawApprovals.Deny => "denied",
                OpenClawApprovals.AllowOnce or "allow-always" => "allowed",
                _ => ended.Cause == "expired" ? "expired" : null
            };

            return OpenClawApprovalText.Ended(status, ended.Reason, ended.Decision);
        }

        // The one handler an answer comes through, reached only from a click on a
        // button the person can see. Sends nothing unless the option is one this
        // conversation's current prompt offered for the approval it shows now.
        public async Task AnswerAsync(ChatPromptOption option)
        {
            if (!OrbweaverSettings.OpenClawApproveEnabled)
            {
                Note(OpenClawApprovalText.ApprovingOff);
                return;
            }

            // A second click on a request already being answered has nothing to say.
            if (option.ApprovalId is { } already && _answering.Contains(already)) return;

            var approval = CurrentApproval();

            // The queue moved between drawing the option and the click: the option
            // is for a request that is no longer the one on the bar. Answering the
            // new one with the old click would put "Allow once" on a command the
            // person never saw.
            if (approval is null || option.ApprovalId != approval.Id)
            {
                Note(OpenClawApprovalText.RequestChanged);
                RefreshApprovals();
                return;
            }

            // Only an option the prompt offers for this request: never a key the
            // payload names, never allow-once on a request that can only be denied.
            if (!PromptFor(approval).Options.Any(o => o.Key == option.Key && o.ApprovalId == option.ApprovalId))
            {
                return;
            }

            // Marked the moment the click lands: a second click has nothing to land on.
            _answering.Add(approval.Id);
            RefreshApprovals();

            var outcome = await OpenClawSessions.ResolveApprovalAsync(this, approval.Id, option.Key);

            switch (outcome.Result)
            {
                case OpenClawApprovalResult.Answered:
                    Note(OpenClawApprovalText.Answered(option.Key));
                    break;
                case OpenClawApprovalResult.DecidedElsewhere:
                    Note(OpenClawApprovalText.AlreadyDecided(outcome.Status, outcome.Reason));
                    break;
                case OpenClawApprovalResult.NoLongerPending:
                    Note(OpenClawApprovalText.NoLongerPending);
                    break;
                case OpenClawApprovalResult.Failed:
                    Note(OpenClawApprovalText.CouldntAnswer(outcome.Detail ?? ""));
                    break;
                    // Duplicate: already being answered, so nothing more to say.
            }

            // Back on the bar if it did not land and is still pending.
            _answering.Remove(approval.Id);
            RefreshApprovals();
        }

        // There is no terminal to focus: say how to answer it on the gateway, with
        // the id as data.
        public void AnswerElsewhere()
        {
            if (CurrentApproval() is { } approval) Note(OpenClawApprovalText.Elsewhere(approval.Id));
        }

        // Null unless every one of these holds, so the panel hides the picker
        // rather than showing one that would be refused:
        //   - the conversation is answered in place. A surface chat's model is the
        //     channel's configuration, and patching it would change how the agent
        //     answers everyone else there. This is the same predicate that decides
        //     whether a reply is delivered, so the two cannot disagree.
        //   - replying is allowed, which is what grants the write scope;
        //   - the gateway granted write and lists sessions.patch (read through
        //     CapabilitiesFor, the one source), and has listed this conversation;
        //   - the gateway told us what it is running on, and which models it has.
        public RemoteRunOptions? RunOptions
        {
            get
            {
                // What the gateway last said this conversation is, read now: a panel
                // is opened before any send has asked, and Route is Surface until it
                // has been told.
                Observe(OpenClawSessions.NativeOf(GatewayKey));

                if (Route != OpenClawSendRoute.Native) return null;
                if (!OrbweaverSettings.OpenClawReplyEnabled) return null;

                var context = OpenClawSessions.CapabilitiesFor(SessionId);
                if (!context.Connected || context.SessionId is null
                    || !context.Methods.Contains(OpenClawOrbActions.PatchMethod)
                    || !context.Scopes.Contains(OpenClawOrbActions.WriteScope))
                {
                    return null;
                }

                if (OpenClawSessions.RunStateOf(GatewayKey) is not { Model: { } modelId } state) return null;

                var catalogue = OpenClawSessions.OfferedModels;
                if (catalogue.Count == 0) return null;

                var models = catalogue.Select(m => new RemoteRunChoice(m.Id, m.Name)).ToList();

                // The model in force may not be one that is offered (the gateway's
                // default can be a model it will not let a patch choose): it is still
                // what the chip must name, so it is listed rather than left out.
                if (models.All(m => m.Id != modelId)) models.Insert(0, new RemoteRunChoice(modelId, modelId));

                // The levels the gateway reports for this conversation's own model,
                // else the catalogue's entry for it. Never the refusal text.
                var levels = state.ThinkingLevels.Count > 0
                    ? state.ThinkingLevels
                    : catalogue.FirstOrDefault(m => m.Id == modelId)?.ThinkingLevels
                      ?? Array.Empty<OpenClawRunOption>();

                var modelName = models.First(m => m.Id == modelId).Label;
                var defaultLabel = state.ThinkingDefault is { } d
                    ? levels.FirstOrDefault(o => o.Id == d)?.Label ?? d
                    : null;

                return new RemoteRunOptions(
                    modelId,
                    models,
                    state.ThinkingLevel,
                    defaultLabel,
                    levels.Select(o => new RemoteRunChoice(o.Id, o.Label)).ToList(),
                    state.EffectiveFastMode,
                    state.FastMode is not null,
                    state.SelectionLocked,
                    // Measured accepted on all three fields.
                    true,
                    OpenClawRunText.Chip(modelName, state.EffectiveThinkingLabel, state.EffectiveFastMode));
            }
        }

        public async Task<string?> SetModelAsync(string? id) =>
            Describe(await OpenClawSessions.SetModelAsync(SessionId, id, CancellationToken.None), OpenClawAction.Model);

        public async Task<string?> SetThinkingAsync(string? id) =>
            Describe(await OpenClawSessions.SetThinkingAsync(SessionId, id, CancellationToken.None), OpenClawAction.Thinking);

        public async Task<string?> SetFastModeAsync(bool? on) =>
            Describe(await OpenClawSessions.SetFastModeAsync(SessionId, on, CancellationToken.None), OpenClawAction.Fast);

        // Null on success, otherwise the sentence the flyout shows.
        private static string? Describe((OpenClawActionOutcome Outcome, string? Detail) result, OpenClawAction action) =>
            result.Outcome == OpenClawActionOutcome.Done
                ? null
                : OpenClawActionText.For(result.Outcome, result.Detail, action);

        public string ComposerHint => OrbweaverSettings.OpenClawReplyEnabled
            ? "Message…"
            : "Replying is off";

        // Older turns, from scrolling back. Prepended rather than replacing, and
        // raising its own event, because the panel has to put the scroll
        // position back afterwards — content appearing above where you are
        // reading would otherwise throw you down the page.
        //
        // CB-270 stage 3b: a run's steps and its rollup can sit on two chat.history
        // pages. Each page drops, on its own, the steps a kept rollup on it
        // contains (TextsToDrop). The one thing a page cannot do is drop the steps
        // of *this* rollup that sit on an older page, so that is done here, when the
        // older page arrives: its trailing-run steps (OpenTail) that the
        // transcript's head rollup (SeamRollup) contains are left out of the turns
        // before they become rows, or keep their picture and lose their caption.
        //
        // Both sets come from the pages' raw messages, never from scanning turns,
        // because a user message can render to no turn and a boundary read off turns
        // would run past it into a different run. Nothing already on screen is
        // removed: where the whole run is an exact join across the seam, the page's
        // outcome stands (rollup kept, steps dropped) and the words are those of the
        // whole-page parse. HistoryPrepended reports the rows inserted.
        public void PrependHistory(IReadOnlyList<HistoryTurn> turns)
        {
            if (turns.Count == 0) return;

            turns = FilterOlderTailSteps(turns);
            turns = DedupePicturesAcrossTheSeam(turns);

            var older = new List<ChatTurn>(turns.Count);
            ChatTurn? seam = null;
            var headRun = new List<ChatTurn>();

            foreach (var t in turns)
            {
                var turn = new ChatTurn
                {
                    Role = t.Role,
                    Text = t.Text,

                    // Copied alongside ImageUrl, never instead of it: the url is
                    // what gets fetched, this is the file it names, and a turn
                    // that kept one without the other could no longer say why a
                    // picture did not load (CB-109).
                    ImageSourcePath = t.ImageSourcePath,
                    Attachment = t.Attachment,

                    // CB-116: carried the same way — the tier TurnsFromHistory
                    // already worked out for this turn, not re-guessed here from
                    // its text. See ChatTurn.Confidence's own header.
                    Confidence = t.Confidence,
                    ImageUrl = t.ImageUrl,
                    ImageBytes = t.ImageBytes,
                    ImageAlt = t.ImageAlt,
                    At = t.At,
                    Speaker = t.Speaker,
                    SpeakerColor = t.SpeakerColor,
                    Mine = t.Mine,
                    IsError = t.IsError,
                    FromRollup = t.FromRollup,
                    IsMirror = t.Arm == MediaSourceArm.Mirror,
                    Blanked = t.Blanked,
                    FreeNamed = t.FreeNamed,
                    IsComplete = true
                };

                older.Add(turn);
                if (t.SeamRollup) seam = turn;
                if (t.RunHead) headRun.Add(turn);
            }

            _history.InsertRange(0, older);

            // The older page is now the head. Its own cut first run, if it has one,
            // is the next seam; otherwise there is none.
            _seamRollup = seam;
            _headRun = headRun;
            _headPage = older;

            HistoryPrepended?.Invoke(older.Count);
        }

        // The older page's trailing-run steps the transcript's head rollup contains
        // are taken out; one that carries a picture or attachment keeps it, with an
        // empty caption (and is then Blanked, a candidate for the picture dedupe).
        private IReadOnlyList<HistoryTurn> FilterOlderTailSteps(IReadOnlyList<HistoryTurn> older)
        {
            var head = _seamRollup;
            if (head is null || !_history.Contains(head)) return older;

            var tail = new List<int>();
            for (var i = 0; i < older.Count; i++)
            {
                if (older[i].OpenTail) tail.Add(i);
            }

            var covered = OpenClawHistoryShape.StepsCoveredBy(head.Text, tail.Select(i => older[i].Text).ToList());
            if (covered.Count == 0) return older;

            var filtered = older.ToList();
            var gone = new HashSet<int>();

            foreach (var j in covered)
            {
                var at = tail[j];
                var step = older[at];

                if (step.ImageUrl is null && step.ImageBytes is null && step.Attachment is null)
                    gone.Add(at);
                else
                    filtered[at] = step with { Text = "", Blanked = true };
            }

            return filtered.Where((_, i) => !gone.Contains(i)).ToList();
        }

        // A picture is not drawn twice, and that includes across a page boundary. Only
        // two kinds of turn are ever dropped for it: a turn whose caption was blanked
        // (its words shown by the other half of a covered pair) and a delivery-mirror,
        // the gateway's record of having delivered a file. A turn the agent sent is
        // never dropped, even naming the same file, and the last copy always stays.
        //
        // A blanked turn goes when a turn of the other half of the *run* shows its
        // resolved path: the older page's trailing run is compared with the
        // transcript's head run (an older candidate is left out before it becomes a
        // row, a head candidate is a row on screen and is removed).
        //
        // A mirror is different, because the collapse it takes part in is page-wide
        // and budgeted: on one page each mirror is cancelled by one unpaired named
        // turn for its file, wherever it sits (a user message between them does not
        // matter), and a mirror with no unpaired named turn is a second genuine
        // delivery and stays. Across the seam the same: the older page's mirrors are
        // cancelled by the unpaired named turns of the page next to it, and that
        // page's mirrors by the older page's, one for one, matched by file name (the
        // page a mirror was parsed on may not hold the directory its picture sits in,
        // so its own path is a guess). Two mirrors of one file with nothing named stay
        // two.
        private IReadOnlyList<HistoryTurn> DedupePicturesAcrossTheSeam(IReadOnlyList<HistoryTurn> older)
        {
            var headRun = _headRun.Where(t => _history.Contains(t)).ToList();
            var headPage = _headPage.Where(t => _history.Contains(t)).ToList();

            static bool IsPicture(HistoryTurn t) => t.ImageUrl is not null;

            var gone = new HashSet<int>();

            for (var i = 0; i < older.Count; i++)
            {
                var c = older[i];
                if (!c.RunTail || !IsPicture(c) || !c.Blanked) continue;

                if (headRun.Any(h => h.ImageUrl is not null && !string.IsNullOrEmpty(c.ImageSourcePath)
                                     && string.Equals(c.ImageSourcePath, h.ImageSourcePath, StringComparison.Ordinal)))
                    gone.Add(i);
            }

            var rows = new SortedSet<int>();
            foreach (var h in headRun.Where(h => h.ImageUrl is not null && h.Blanked))
            {
                for (var i = 0; i < older.Count; i++)
                {
                    if (!older[i].RunTail || gone.Contains(i) || !IsPicture(older[i])) continue;

                    if (!string.IsNullOrEmpty(older[i].ImageSourcePath)
                        && string.Equals(older[i].ImageSourcePath, h.ImageSourcePath, StringComparison.Ordinal))
                    {
                        rows.Add(_history.IndexOf(h));
                        break;
                    }
                }
            }

            // The older page's mirrors against the next page's unpaired named turns.
            foreach (var file in older.Where(t => t.Arm == MediaSourceArm.Mirror && IsPicture(t) && !string.IsNullOrEmpty(t.ImageAlt))
                         .Select(t => t.ImageAlt).Distinct().ToList())
            {
                var budget = headPage.Count(h => h.FreeNamed && h.ImageUrl is not null && h.ImageAlt == file);
                for (var i = 0; i < older.Count && budget > 0; i++)
                {
                    if (older[i].Arm != MediaSourceArm.Mirror || !IsPicture(older[i]) || older[i].ImageAlt != file || gone.Contains(i)) continue;

                    gone.Add(i);
                    budget--;
                }
            }

            // The next page's mirrors against the older page's unpaired named turns.
            foreach (var file in headPage.Where(h => h.IsMirror && h.ImageUrl is not null && !string.IsNullOrEmpty(h.ImageAlt))
                         .Select(h => h.ImageAlt).Distinct().ToList())
            {
                var budget = older.Where((t, i) => !gone.Contains(i) && t.FreeNamed && IsPicture(t) && t.ImageAlt == file).Count();
                foreach (var h in headPage.Where(h => h.IsMirror && h.ImageUrl is not null && h.ImageAlt == file))
                {
                    if (budget == 0) break;

                    rows.Add(_history.IndexOf(h));
                    budget--;
                }
            }

            if (rows.Count > 0) RemoveRows(rows);

            var kept = gone.Count == 0 ? older.ToList() : older.Where((_, i) => !gone.Contains(i)).ToList();

            // The whole page resolves a mirror's route against the directories every
            // message on it names; parsed on its own, a mirror whose file is named on
            // the other page falls back to a guess. A mirror that survives is given the
            // route of the named turn for its file on the page next to it, so the copy
            // that stays fetches the file the whole page would.
            for (var i = 0; i < kept.Count; i++)
            {
                var m = kept[i];
                if (m.Arm != MediaSourceArm.Mirror || !IsGuess(m.ImageSourcePath)) continue;

                var named = headPage.FirstOrDefault(h => !h.IsMirror && h.ImageUrl is not null && h.ImageAlt == m.ImageAlt
                                                       && !string.IsNullOrEmpty(h.ImageSourcePath) && !IsGuess(h.ImageSourcePath));
                if (named is not null) kept[i] = m with { ImageUrl = named.ImageUrl, ImageSourcePath = named.ImageSourcePath };
            }

            foreach (var h in headPage.Where(h => h.IsMirror && h.ImageUrl is not null && IsGuess(h.ImageSourcePath)))
            {
                var named = kept.FirstOrDefault(o => o.Arm != MediaSourceArm.Mirror && o.ImageUrl is not null && o.ImageAlt == h.ImageAlt
                                                    && !string.IsNullOrEmpty(o.ImageSourcePath) && !IsGuess(o.ImageSourcePath));
                if (named.ImageUrl is null) continue;

                h.ImageUrl = named.ImageUrl;
                h.ImageSourcePath = named.ImageSourcePath;
            }

            return kept;
        }

        private static bool IsGuess(string? path) =>
            path is not null && path.StartsWith(OpenClawSessions.SharedMediaDir, StringComparison.Ordinal);

        // Removes rows already on screen, highest first and one TurnsRemoved per
        // contiguous run, so the indices are the transcript's as it stood. The page's
        // last row, held by reference (ReconcileRun anchors on it), moves to the row
        // before it if it is removed.
        private void RemoveRows(SortedSet<int> rows)
        {
            var pageLastAt = _pageLast is null ? -1 : _history.IndexOf(_pageLast);

            var ordered = rows.Reverse().ToList();
            var n = 0;
            while (n < ordered.Count)
            {
                var top = ordered[n];
                var run = 1;
                while (n + run < ordered.Count && ordered[n + run] == top - run) run++;

                var from = top - run + 1;
                _history.RemoveRange(from, run);
                TurnsRemoved?.Invoke(from, run);
                n += run;
            }

            if (pageLastAt >= 0 && rows.Contains(pageLastAt))
            {
                var before = pageLastAt - rows.Count(r => r < pageLastAt) - 1;
                _pageLast = before >= 0 ? _history[before] : null;
            }
        }

        public event Action<int>? HistoryPrepended;

        public void SetHistory(IReadOnlyList<HistoryTurn> turns)
        {
            if (turns.Count == 0) return;

            _history.Clear();
            _streaming = null;
            _streamingKind = null;
            _runFirst = null;

            // _runDirty is left alone: the page that just landed may be older than
            // the run (a slow first load) or hold only part of it, and the run's
            // end reconciles against a fresh page either way.

            // Whatever the run had streamed in before the page landed is in the
            // page or is not coming back by itself, so it is closed rather than
            // drawn a second time as the start of a fresh bubble. The refetch at
            // the run's end appends what the page did not hold.
            _closed = _runText;
            HasMore = true;

            _seamRollup = null;

            foreach (var turn in turns)
            {
                _history.Add(new ChatTurn
                {
                    Role = turn.Role,
                    Text = turn.Text,

                    // See PrependHistory's twin above for why this travels
                    // with ImageUrl rather than being recovered from it.
                    ImageSourcePath = turn.ImageSourcePath,
                    Attachment = turn.Attachment,
                    Confidence = turn.Confidence,
                    ImageUrl = turn.ImageUrl,
                    ImageBytes = turn.ImageBytes,
                    ImageAlt = turn.ImageAlt,
                    At = turn.At,
                    Speaker = turn.Speaker,
                    SpeakerColor = turn.SpeakerColor,
                    Mine = turn.Mine,
                    IsError = turn.IsError,
                    FromRollup = turn.FromRollup,
                    IsMirror = turn.Arm == MediaSourceArm.Mirror,
                    Blanked = turn.Blanked,
                    FreeNamed = turn.FreeNamed,
                    IsComplete = true
                });
            }

            // The page's own seam rollup, if it has one (a page an older one may
            // precede, whose first run ends in a kept rollup), and its head run.
            _headRun = new List<ChatTurn>();
            _headPage = _history.ToList();
            for (var i = 0; i < turns.Count; i++)
            {
                if (turns[i].SeamRollup) _seamRollup = _history[i];
                if (turns[i].RunHead) _headRun.Add(_history[i]);
            }

            _pageLast = _history[^1];
            HistoryReplaced?.Invoke();
        }

        // Raised when the whole transcript changes underneath the panel, which
        // TurnAdded can't express. Deliberately not on IRemoteChatSession: the
        // panel treats it as an optional extra, so an implementation that only
        // ever appends — the fake this was developed against — needs nothing.
        public event Action? HistoryReplaced;

        public void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

        private static bool? Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.SafeString()
                : null;
    }
}
