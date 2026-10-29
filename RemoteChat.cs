using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Orbweaver
{
    // What the chat panel needs from a session it can talk to, and nothing more.
    //
    // An interface here rather than the panel reaching into OpenClawSessions
    // directly, for one practical reason: it lets the whole panel — layout,
    // keyboard, streaming, autoscroll, the mic — be built and watched against an
    // in-memory fake before the gateway can send a word. The transport is then
    // swapping one implementation for another rather than the moment everything
    // is first tried at once.
    //
    // Four requirements on any implementation, each of which the panel relies on
    // and none of which it can check:
    //
    //  1. Every event is raised on the UI thread. The implementation does its
    //     own Dispatcher.Post. The alternative is every consumer hopping threads
    //     by hand and a comment on each explaining why.
    //  2. TurnUpdated carries the whole turn, already mutated — not a delta. A
    //     dropped or coalesced event then costs nothing, because the panel
    //     re-reads Text. Deltas make the view desyncable with no way to notice.
    //     (The gateway does not oblige: on 2026.10.5 only the first and last
    //     assistant events of a run carry data.text, and the rest carry
    //     data.delta alone, so an implementation reassembles the deltas itself
    //     and hands the panel the whole turn.)
    //  3. SendAsync raises TurnAdded for the user's own turn. The panel never
    //     inserts optimistically, so exactly one thing owns the transcript and a
    //     failed send leaves no ghost behind.
    //  4. History is already bounded and ordered oldest to newest. The panel
    //     shows what it is given and never trims or pages.
    public enum ChatRole { User, Assistant, System }

    // CB-116: how much a candidate picture's own origin is worth trusting
    // when the fetch behind it fails — the fix for a bug that had nothing to
    // do with fetching. OpenClaw's LocalMediaPathFrom recognises any
    // caption-shaped prose ending in something extension-shaped (CB-107),
    // which is right for finding real pictures and wrong for deciding
    // whether a *failure* is worth a visible "picture not shown" note:
    // "I deleted photo.png" matches the same shape as a real delivery and,
    // unlike a real delivery, is an unbounded population — any message in any
    // conversation that happens to end in a filename, forever.
    //
    // The fetch itself is unaffected by this — every producer still tries,
    // cheaply and cached, because that is how real pictures named this way
    // are found at all. This only tiers whether a *failed* fetch gets to say
    // why. High for a candidate this app has independent reason to trust —
    // an explicit "MEDIA:" line, the gateway's own delivery-mirror record, a
    // path CB-115 recovered from the cron run that produced it, or a
    // trailing token on a turn the gateway tagged as an openclawAutomation
    // delivery. Low for everything else: ordinary prose that merely ends in
    // something filename-shaped, which is exactly comfyui-style narration
    // turns (accepted as a trade — see CB-116's PR body) and exactly
    // "I deleted photo.png".
    //
    // Deliberately not derived from the candidate's *shape* — a rooted path
    // in prose is exactly as untrustworthy as a bare one, and the real
    // corpus has genuine deliveries in both shapes. Provenance is metadata
    // about who produced the turn; shape is a property of the text, and
    // CB-107 already proved shape alone cannot tell a caption from a
    // delivery.
    //
    // A transport-neutral type living beside ChatRole rather than nested in
    // OpenClawSessions, even though every producer of it today is OpenClaw's:
    // ChatTurn.Confidence is a public member of a deliberately
    // transport-agnostic model, and a public member cannot expose a less
    // accessible — or more tightly coupled — type than itself.
    public enum MediaConfidence { High, Low }

    public enum RemoteChatState { Disconnected, Connecting, Connected, Error }

    // CB-35: what SendAsync (and SendWithImagesAsync beside it) actually
    // managed, so the one caller that matters — ChatPanel.Send — can decide
    // whether the composer is done with the text or still needs to hold it.
    //
    // Not a bool, even though every implementation below only ever needs the
    // two states: a result type says at the call site what a bool would make
    // the reader go and check the method for.
    //
    // The line between the two states is drawn the same way in every
    // implementation, and is worth writing down once here rather than four
    // times: **Sent** means the message is the transport's problem now — it
    // was handed to a gateway, typed into a terminal, or queued on a socket,
    // and whatever the transcript shows about it afterwards (a reconciled
    // turn, a "handed to X" note, even a later delivery failure reported
    // asynchronously) is a fact about that attempt, not about whether the
    // composer should keep a copy. **Failed** means the attempt never
    // happened — replying is switched off, there is nobody to address, there
    // is no pane and nowhere to deliver to — and in every one of those cases
    // nothing was queued anywhere, so the only copy of what was typed is the
    // one still sitting in the box.
    public enum ChatSendOutcome { Sent, Failed }

    // Mutable on purpose: a streaming reply updates Text in place and raises
    // TurnUpdated, so the list never recreates the item. Recreating it would
    // re-template the row, which is the one thing that could steal focus from
    // the input mid-sentence.
    public sealed class ChatTurn : INotifyPropertyChanged
    {
        private string _text = "";

        public ChatRole Role { get; init; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                Raise();
            }
        }

        public bool IsComplete { get; set; }

        // CB-270: drawn from a gateway rollup message; see HistoryTurn.FromRollup.
        public bool FromRollup { get; init; }

        private bool _isError;

        // CB-270: a tool line whose result reported an error. Mutable and
        // notifying, like Text, because a live tool line learns it failed only
        // when the result arrives, after the row is already on screen. It never
        // changes Text: AppendNewTail anchors on (Role, Text), and a line that
        // reworded itself on failure would no longer match its history twin.
        public bool IsError
        {
            get => _isError;
            set
            {
                if (_isError == value) return;
                _isError = value;
                Raise();
            }
        }

        // When this turn happened, in local time. The gateway records a
        // timestamp per message and the panel shows it, so a conversation that
        // has been going on across Discord and a terminal all day reads as
        // something with a shape rather than a flat wall.
        //
        // Defaulted to now, which is right for a turn created live and is
        // overwritten from the backlog for one that wasn't.
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        private string? _imageUrl;

        // A picture sent in the conversation, as a path on the gateway rather
        // than bytes: a transcript can hold a dozen of them and only the ones
        // actually scrolled to are worth a megabyte each. The panel resolves it.
        //
        // Settable rather than init-only: a live turn can start with no
        // picture and gain one once OpenClawChatSession resolves a
        // "[media attached: ...]" marker against the gateway's own history
        // (see TryResolveLiveImage) — the same reason Text is mutable rather
        // than the row being recreated.
        public string? ImageUrl
        {
            get => _imageUrl;
            set
            {
                if (_imageUrl == value) return;
                _imageUrl = value;
                Raise();
            }
        }

        public string ImageAlt { get; set; } = "";

        // The file behind ImageUrl, for the OpenClaw case (CB-109).
        //
        // Null on a turn with no picture at all, and on a picture that did not
        // come from the assistant-media route — an image block carrying its
        // own url, a local CLI's inline bytes. It is **never** null on a turn
        // whose ImageUrl *is* an assistant-media request: both producers set
        // the two together, and TurnView.LoadImage's null check is a refusal
        // to caption a note with a guess rather than a media case to handle.
        // Do not read this nullability as a picture scenario needing a
        // fallback; there isn't one.
        //
        // A plain string on purpose. This model is deliberately
        // transport-agnostic — it does not know what a gateway is, which is
        // what lets a Codex transcript and a room merge share it — so the
        // structured request lives in OpenClawMediaSource, is used where the
        // url is built, and is not stored here. What a turn needs afterwards
        // is not the request: it is the readable path for the tooltip on a
        // refusal, which is the same category of value ImageAlt above already
        // holds, only whole rather than reduced to a basename.
        //
        // Set by whoever built ImageUrl, which is the point: before CB-109 the
        // panel had nothing but the url and had to unescape the path back out
        // of it, and the one place that knew the answer had thrown it away.
        //
        // Plain rather than notifying on its own, like ImageNoteDetail below:
        // it is always set immediately *before* ImageUrl, whose Raise() is
        // what tells the row to look again.
        public string? ImageSourcePath { get; set; }

        // CB-272: whether ImageUrl names a video clip rather than a picture,
        // read off the extension of ImageSourcePath. Derived rather than
        // stored so it cannot disagree with the path, and so the several
        // sites that copy a history turn into a live one (all of which already
        // copy the path) need no new field to forget. A turn with no source
        // path — a local CLI's inline picture, a pasted one — is never one.
        //
        // CB-274: a gateway block that says it is a clip counts too. A success
        // block has no local path at all (the projection strips it) and its url
        // is not an assistant-media route, so the extension rule alone could
        // not keep it from the bitmap decoder.
        public bool IsVideo => Attachment is { Kind: AttachmentKind.Video }
                               || OpenClawVideo.IsVideoPath(ImageSourcePath);

        // CB-274 / CB-275: the attachment block behind this turn, if any.
        // Plain rather than notifying, like ImageSourcePath: set before
        // ImageUrl, whose Raise() is what tells the row to look again.
        public ChatAttachment? Attachment { get; set; }

        // CB-275: the other kinds, read the same way IsVideo is — off the
        // gateway's block, or (audio only) off the extension of a recovered
        // assistant-media path, so a streamed `MEDIA:/…/note.m4a` works too.
        public bool IsAudio => Attachment is { Kind: AttachmentKind.Audio }
                               || OpenClawAudio.IsAudioPath(ImageSourcePath);

        public bool IsDocument => Attachment is { Kind: AttachmentKind.Document };

        // CB-116: whether a *failed* fetch for ImageUrl is worth explaining
        // with the note below, rather than staying silent — see
        // MediaConfidence's own header for the full reasoning. Set by
        // whoever set ImageSourcePath, the same pairing that field already
        // has with ImageUrl; see OpenClawSessions.HistoryTurn.Confidence for
        // where the OpenClaw producers actually decide the value.
        //
        // Plain rather than notifying, for the same reason ImageSourcePath is:
        // it is always set before ImageUrl, whose own setter is what tells
        // the row to look again, so nothing here needs its own Raise().
        //
        // Defaults High: a turn built by anything other than the OpenClaw
        // producers — a local CLI transcript, a room merge — never had a
        // reason not to ask why, and keeps not having one.
        public MediaConfidence Confidence { get; set; } = MediaConfidence.High;

        private string? _imageNote;

        // Why a picture that should have shown didn't — CB-93. Set once a
        // resolution attempt comes back empty and the gateway's own &meta=1
        // answer explains the refusal (see OpenClawMediaRefusal). Null in the
        // ordinary case where nothing failed, which draws nothing.
        //
        // Mutable rather than init-only for the same reason ImageUrl and
        // ImageBytes are: a turn starts with neither a picture nor a reason,
        // and gains one once the async resolution — on the live path or the
        // history path — finishes.
        public string? ImageNote
        {
            get => _imageNote;
            set
            {
                if (_imageNote == value) return;
                _imageNote = value;
                Raise();
            }
        }

        // The tooltip for ImageNote: the path and the gateway's own code,
        // kept out of the line itself so the bubble doesn't grow past one
        // sentence. Plain rather than notifying on its own — always set
        // immediately before ImageNote, whose Raise() is what tells the view
        // to look again, the same pairing ImageAlt already has with
        // ImageBytes/ImageUrl above.
        public string? ImageNoteDetail { get; set; }

        private byte[]? _imageBytes;

        // A picture already decoded, for the two cases where there is no
        // gateway to resolve ImageUrl against: a local CLI's own transcript
        // carries an attached picture inline as base64 (see ChatTranscript's
        // image handling), and a picture the panel itself just pasted and
        // wrote to disk is read straight back rather than round-tripped
        // through a fetch it would only fail. Never both this and ImageUrl
        // on the same turn.
        public byte[]? ImageBytes
        {
            get => _imageBytes;
            set
            {
                if (_imageBytes == value) return;
                _imageBytes = value;
                Raise();
            }
        }

        // Who said this, when that is someone other than the two ends of the
        // conversation. In a channel an agent's transcript carries messages from
        // the other agents in the room, and "Nova" and "Aurora" arriving in
        // identical bubbles is a transcript you have to read twice to follow.
        //
        // Null for an ordinary two-party conversation, where the side of the
        // bubble already says who is talking and a name on every row would be
        // noise.
        public string? Speaker { get; init; }

        // Whether the person at this keyboard said it.
        //
        // Separate from the role, because the role cannot carry it. A message in
        // a channel arrives in every member agent's transcript in the *user*
        // role whoever sent it, so user-role means "not the agent this
        // transcript belongs to" and nothing more — which is why a room used to
        // draw everybody's messages as the room's own neutral voice rather than
        // asserting one of them was yours.
        //
        // The panel needs no rule of its own for this: a turn kept at
        // ChatRole.User with no Speaker is already what it draws blue and to the
        // right, so a Mine turn is one and a turn from somebody else carries the
        // Speaker that takes it back to the left. What this flag adds is
        // something the *transcript* can be built from — a room deciding which
        // of several identical-looking user turns to keep in your voice.
        //
        // Defaults false, which is the honest answer for every transport that
        // does not know: a local CLI's transcript, where user-role really does
        // mean you, sets it nowhere and loses nothing.
        public bool Mine { get; init; }

        // That speaker's colour, as "#RRGGBB" — the same one their orb's ring
        // is drawn in, so the two are recognisably the same agent. Carried
        // rather than looked up because the panel deliberately knows nothing
        // about agents or gateways.
        public string? SpeakerColor { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public interface IRemoteChatSession
    {
        string SessionId { get; }
        string DisplayName { get; }
        RemoteChatState State { get; }
        IReadOnlyList<ChatTurn> History { get; }

        event Action<ChatTurn>? TurnAdded;
        event Action<ChatTurn>? TurnUpdated;
        event Action<RemoteChatState>? StateChanged;

        Task<ChatSendOutcome> SendAsync(string text);

        // Stops the reply in flight. Separate from dismissing the panel: closing
        // a window should never cancel work someone asked for.
        void Cancel();
    }

    // Everything below is optional. The panel tests for each one and does
    // without when it isn't there, which is what keeps the in-memory fake — and
    // any future third transport — to the nine members above.
    //
    // They are separate interfaces rather than more members on
    // IRemoteChatSession because they are separate powers: a transport can page
    // backwards without being able to answer a dialog, and both were true of
    // exactly one implementation each when they were written.

    // Scrolling back past what the panel was given.
    //
    // This started life as four members on OpenClawChatSession that the panel
    // reached for by type test. Lifting it happened when the second transport
    // turned out to want the same thing for the same reason — a transcript is
    // thousands of rows and the panel is given forty.
    public interface IRemoteChatBacklog
    {
        // False once there is nothing older left to fetch. The panel asks before
        // every fetch, so an implementation that can't page says false forever.
        bool HasMore { get; }

        // True when this call actually prepended something. False is not a
        // failure — it is "that was the end" — and the panel uses it to avoid
        // measuring a scroll correction that isn't coming.
        Task<bool> LoadOlderAsync(CancellationToken ct);

        // The whole transcript changed underneath the panel, which TurnAdded
        // can't express. Raised when a backlog lands a moment after the panel
        // opened and replaces the little it had.
        event Action? HistoryReplaced;

        // Older turns went on the front. Carries how many, because the panel has
        // to put the scroll position back afterwards — content appearing above
        // where you are reading would otherwise throw you down the page.
        event Action<int>? HistoryPrepended;
    }

    // What the empty input box should say.
    //
    // Both transports can be in a state where typing is pointless — replying
    // switched off, or a session with no pane to type into — and both would
    // rather say so on the box than let you write a paragraph first. The box
    // stays enabled either way: SendAsync still explains itself in the
    // transcript, and a disabled box you can't paste a draft into is a worse
    // answer than one that tells you what will happen.
    public interface IRemoteChatComposer
    {
        string ComposerHint { get; }
    }

    // A session that can be read and, right now, not written to *at all*.
    //
    // **The panel hides the composer entirely while IsReadOnly is true**,
    // rather than showing a disabled box or the discouraging watermark
    // IRemoteChatComposer above argues for. That reads as a contradiction of the
    // paragraph directly overhead and is not one: that reasoning turns on typing
    // being *pointless*, where SendAsync can still explain itself in the
    // transcript afterwards. This is the case where the server has already said
    // the text has nowhere to go — a cloud session that has ended, been deleted,
    // or refused this login a write. A box that accepts a paragraph and only then
    // admits the session will not take it has already lost the paragraph, and the
    // person who typed it has no copy.
    //
    // This interface was written when a cloud session had no write path at all
    // (CB-164 found `/input`, `/messages`, `/turns` and `/conversation` on the
    // read host all 404). CB-199 found the one the Claude Code CLI uses —
    // `POST /v1/code/sessions/{id}/events`, measured — so a cloud session is now
    // writable until a refusal says otherwise, which is why IsReadOnly can change
    // and ReadOnlyChanged exists.
    //
    // ComposerHint is still read for one of these, and shown where the box was.
    // Hiding the box and explaining nothing leaves a panel that looks truncated;
    // the hint says why, and ReplyUrl where the session can still be opened,
    // which together are the useful half of the refusal.
    public interface IRemoteChatReadOnly
    {
        // A property rather than a bare marker interface, for the same reason
        // IRemoteChatRoom.IsRoom is one: a fake has to be able to flip it per
        // instance, so one test class can drive both sides of the panel's
        // decision.
        bool IsReadOnly { get; }

        // Where this conversation lives in a browser, as something a browser
        // will open — read whether or not the session is read-only (CB-226): a
        // live session offers it above its composer, a read-only one in the
        // box that replaces it. Null when there is nowhere, and a read-only
        // panel then says only that it cannot be replied to here.
        //
        // A link rather than an address printed into the hint. Telling somebody
        // the reply lives at claude.ai/code and leaving them to find the session
        // is a worse answer than the one this app gives everywhere else — an orb
        // click goes *to* the session, and the panel should too. The address is
        // also per-session and long, so as prose it is either truncated or it
        // swamps the sentence beside it.
        string? ReplyUrl { get; }

        // IsReadOnly changed after the panel read it.
        //
        // CB-199: it used to be fixed for a session's lifetime, and a panel that
        // read it once at bind was right. It no longer is — a cloud session is
        // writable until the server says otherwise (a 409 once it has ended, a
        // 404 once it has been deleted, a 403 for an account that may not write
        // to it), and the only way to learn that is to try. So the panel has to
        // hear about the flip rather than keep the box it drew at bind: a
        // composer left up over a session that has just refused a message would
        // take the next paragraph and lose it exactly the way the comment above
        // says a hidden box exists to prevent.
        //
        // Carries nothing. The handler re-reads IsReadOnly, ComposerHint and
        // ReplyUrl together, because a flip changes all three at once and an
        // event carrying one of them would invite reading the other two stale.
        // Raised on the UI thread, like every other event on these interfaces.
        event Action? ReadOnlyChanged;
    }

    // A session whose reply in flight this app can actually stop.
    //
    // Optional, and separate from IRemoteChatSession.Cancel, which every
    // transport has and which stays the verb: this interface says only whether
    // pressing it right now would do anything. For CB-59's reason the panel
    // hides a Stop control that would do nothing rather than drawing it
    // disabled — a greyed button over a session that is not working reads as
    // "stuck", and a Stop that silently does nothing reads as broken. A
    // transport that does not implement this keeps whatever the panel already
    // did for it.
    //
    // A property with a change event rather than something the panel infers
    // from the turns it has, because the thing that knows whether a turn is
    // running is the transport's own status (for a cloud session, the roster
    // row's worker state), not the transcript — a reply can be running long
    // before its first row arrives.
    public interface IRemoteChatInterrupt
    {
        // True while there is a turn in flight that Cancel would stop. Goes
        // false as soon as Cancel is called, optimistically, and stays false for
        // a session that is read-only: there is nothing to stop on a session
        // that can no longer be written to.
        bool CanInterrupt { get; }

        // CanInterrupt changed. Raised on the UI thread; carries nothing, and the
        // handler re-reads the property.
        event Action? InterruptChanged;
    }

    // A session that cannot be typed into where it is, but can be *opened*
    // somewhere it can be dealt with.
    //
    // One implementation, and the same reason the interfaces above are separate
    // from IRemoteChatSession rather than on it: a transport with no such answer
    // simply does not implement this, and the panel offers nothing. A gateway
    // session has nowhere to be attached to; a background job has exactly one
    // place, and until this existed the panel's advice for one was "reply in the
    // terminal instead" — a terminal that does not exist, which is the whole
    // reason a daemon runs the session.
    public interface IRemoteChatElsewhere
    {
        // Whether the panel should offer it. Decided by the session rather than
        // by the panel, because the rule is the click path's
        // (ClickRouting.AttachWouldReach) and the panel has no business holding
        // a second copy of it.
        bool CanOpenElsewhere { get; }

        // Fire and forget, like the click it shares its implementation with:
        // opening a window is not something the panel waits on, and what arrives
        // afterwards arrives through the ordinary scan.
        //
        // Deliberately the same destination as that click rather than a second
        // one of its own — for a background session, the `claude agents` roster.
        // One verb, one place: a panel that sent you somewhere else from where
        // the orb sends you would be two answers to "where is this session".
        void OpenElsewhere();
    }

    // A conversation several agents (or people) can be talking in at once.
    //
    // CB-36: what an unattributed assistant turn's chip needs to know before
    // it borrows the panel's sole-speaker name for itself. A one-to-one
    // session — a terminal, or a single gateway agent — has exactly one
    // speaker, so an assistant turn with no Speaker of its own genuinely is
    // that one speaker, and TurnView.SpeakerName is right to fall back to
    // it. A room is not one-to-one: OpenClawRoomChatSession.Rebuild stamps
    // every turn it can attribute and deliberately leaves the rest with no
    // Speaker, meaning "we do not know who said this" — and falling back to
    // the sole-speaker name there would answer with the panel's title, the
    // room itself, asserting a speaker the app does not know.
    //
    // A property rather than a bare marker so a fake can flip it per
    // instance in a test, the same way IRemoteChatMachine's MachineName is a
    // property rather than a second interface for "has a machine". A room
    // implementation always answers true; nothing here needs it to vary
    // after construction, so there is no change event to raise.
    public interface IRemoteChatRoom
    {
        bool IsRoom { get; }
    }

    // A conversation that is somewhere else, and can say where.
    //
    // The panel used to badge these "another machine", which is true and is not
    // an answer: somebody with two of them cannot act on it. The name is already
    // on the wire — a relay is called `claude-buddy-rc--claude-board-avatar` and
    // the tail is the machine — so this costs nothing to send and works against
    // a far Buddy of any version.
    //
    // Optional like the rest, and for the same reason: a local session's machine
    // is *this* one, which is not worth a chip, and a gateway conversation has
    // no machine at all.
    // CB-277: the gateway key of the conversation a panel is showing, for the
    // one row that needs to say a clip's route was re-keyed away from it. Null
    // or absent (a room, a local session) means no claim is made.
    public interface IRemoteChatGatewayKey
    {
        string? GatewayKey { get; }
    }

    public interface IRemoteChatMachine
    {
        // Null until the roster has answered, which a panel can open before. The
        // caller keeps the vaguer wording rather than guessing — naming the
        // wrong machine is worse than not naming one.
        string? MachineName { get; }

        // Raised when it becomes known, since the panel is usually open first.
        event Action? MachineChanged;
    }

    // A session that can be waiting on something slow enough to need saying so.
    //
    // Optional for the same reason as the interfaces above: a local CLI session
    // reads a file on this machine and has nothing to wait for, and a gateway
    // session is one websocket round trip away. Only the mirror has a wait worth
    // drawing — its opening window is carried by a model retyping base64 by
    // hand, measured on real machines at 222, 231, 234, 247 and 192 seconds.
    //
    // **What it deliberately does not offer is a percentage.** A window that
    // fits in one chunk is a single round trip: there is no intermediate signal
    // to report, so a bar that filled at a guessed rate would be inventing
    // progress rather than showing it, and would then sit at 99% for a minute —
    // which is worse than an honest spinner, because it makes a working transfer
    // look stuck. What is real is *elapsed*, and how long these usually take, so
    // that is what this carries. See CB-58.
    public interface IRemoteChatFetchWait
    {
        // When the current wait began, or null when nothing is in flight. A
        // clock rather than a bool so the panel can say how long it has been
        // without keeping its own copy of the answer.
        DateTimeOffset? WaitingSince { get; }

        // What is being waited for, in the panel's own words.
        string WaitingFor { get; }

        // Raised when either of the two above changes. Not a property-changed
        // event: this is the whole of the surface, and one signal for two
        // properties read together is less to get wrong than two signals.
        event Action? WaitChanged;
    }

    // A session where a pasted picture can actually go somewhere.
    //
    // Not on IRemoteChatSession itself, for the same reason the three
    // interfaces above aren't: only one implementation exists yet, and a
    // session that doesn't implement this simply leaves a paste of a
    // picture as the ordinary text paste it would otherwise have been —
    // which is the only honest answer for a transport with nowhere to put
    // a file. A local CLI session has somewhere, because its own reader is
    // on this machine; a gateway session's is on the other end of a
    // websocket, and handing it a path on this machine would name a file it
    // cannot open.
    public interface IRemoteChatImages
    {
        // Sends text together with pictures already saved to disk. Called
        // instead of SendAsync exactly when the panel is holding at least
        // one pasted picture; a message with none still goes through
        // SendAsync alone.
        Task<ChatSendOutcome> SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths);
    }

    // One option in a dialog the session is blocked on. Key is what gets sent —
    // a digit, for the numbered lists Claude Code puts up — and Label is the
    // dialog's own wording, read off the screen rather than guessed at.
    public sealed record ChatPromptOption(string Key, string Label);

    public sealed record ChatPrompt(string Title, IReadOnlyList<ChatPromptOption> Options);

    // One slash command a CLI understands, offered the same way typing "/" in
    // its own terminal would. Name includes the leading "/" so the panel can
    // compare it against the input box's text directly.
    public sealed record SlashCommand(string Name, string Description);

    // The slash commands a session's underlying CLI understands, so the panel
    // can offer autocomplete for a message that is about to be typed straight
    // into that CLI's own input line. Only a local CLI session has an answer:
    // an OpenClaw conversation isn't parsed by a command grammar on the far
    // end, so there is nothing to suggest.
    public interface IRemoteChatSlashCommands
    {
        IReadOnlyList<SlashCommand> SlashCommands { get; }
    }

    // A session that has stopped and is waiting to be answered.
    //
    // The case this exists for is a permission prompt: the session is doing
    // nothing, the transcript says nothing about why, and the panel would
    // otherwise show a conversation that had simply gone quiet. Answering is a
    // real power and is gated like sending is.
    public interface IRemoteChatPrompts
    {
        // Null when nothing is waiting. Non-null means the panel should show the
        // options instead of pretending the silence is normal.
        ChatPrompt? Prompt { get; }

        event Action? PromptChanged;

        // Take this option. Only ever called with an option out of Prompt, so an
        // implementation never has to invent a key it didn't publish.
        Task AnswerAsync(ChatPromptOption option);

        // "I'll deal with it myself" — go to wherever the dialog actually is.
        // The fall back for when the dialog could not be read, which is the only
        // honest answer at that point.
        void AnswerElsewhere();
    }
}

namespace Orbweaver
{
    // CB-267: what the panel shows when a peer message to this session would be
    // held by Claude Code until someone approves it in that session.
    public sealed record PeerInboundOffer(string Explanation, string SettingsPath, bool CanEdit);

    public interface IRemoteChatPeerInbound
    {
        // Null when there is nothing to offer.
        PeerInboundOffer? InboundOffer { get; }
        bool HasHeldMessage { get; }
        Task<bool> EnableInboundAsync();
        Task<ChatSendOutcome> ResendHeldAsync();
        // Raised on the UI thread.
        event Action? InboundChanged;
    }
}
