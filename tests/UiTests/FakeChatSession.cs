namespace Orbweaver.Tests;

// An in-memory IRemoteChatSession, exactly the shape RemoteChat.cs's own
// header comment says the interface was designed to be driven by "before any
// real gateway exists". Honours the four rules stated there:
//
//  1. Every event fires on the UI thread — trivially true in a headless test,
//     since everything here runs on the one dispatcher thread already.
//  2. TurnUpdated would carry the whole mutated turn, not a delta (no test
//     below needs to raise it, so there is nothing to get wrong yet).
//  3. SendAsync raises TurnAdded for the user's own turn itself — the panel
//     never inserts it optimistically.
//  4. History is pre-bounded and already ordered oldest to newest — callers
//     of this fake are expected to build it that way; it does no trimming or
//     sorting of its own.
internal sealed class FakeChatSession :
    IRemoteChatSession, IRemoteChatImages, IRemoteChatSlashCommands,
    IRemoteChatComposer, IRemoteChatElsewhere, IRemoteChatFetchWait, IRemoteChatRoom,
    IRemoteChatReadOnly, IRemoteChatInterrupt, IRemoteChatPeerInbound, IRemoteChatGatewayKey,
    IRemoteChatRunOptions
{
    public string SessionId { get; init; } = "fake-session";
    public string DisplayName { get; init; } = "Fake Session";

    // CB-277: the conversation's own gateway key; null makes no claim.
    public string? GatewayKey { get; init; }
    public RemoteChatState State { get; set; } = RemoteChatState.Connected;

    // False by default, the same as any ordinary one-to-one session that
    // doesn't implement IRemoteChatRoom at all — this fake implements it
    // unconditionally only so a test can flip the property, per CB-36's own
    // comment on why it is a property rather than a bare marker.
    public bool IsRoom { get; set; }

    // Empty by default, the same as a session with nothing to say about
    // IRemoteChatSlashCommands. Settable rather than init-only, and after
    // OpenFor as well as before: a session on another machine has to be asked
    // what it can run, so its list arrives well after the panel has bound to
    // it, and a fake that could only be set up front could not express the
    // case that mattered.
    public IReadOnlyList<SlashCommand> SlashCommands { get; set; } = Array.Empty<SlashCommand>();

    // What the composer's box says, and whether the panel offers to attach this
    // session somewhere it can be typed into. Both default to what an ordinary
    // typeable session answers, so every existing test of this fake is unchanged
    // by their arrival: an ordinary hint, and no button.
    public string ComposerHint { get; set; } = "Message…";

    public bool CanOpenElsewhere { get; set; }

    // False by default, so every test that predates CB-164 still gets an
    // ordinary panel with a composer in it. Implemented unconditionally and
    // settable for the reason IsRoom above is: one fake has to be able to drive
    // both sides of the panel's decision, which a bare marker interface could
    // not express.
    public bool IsReadOnly { get; set; }

    // Null by default, which is the "read-only with nowhere to go" case — the
    // panel then shows the sentence and no link. A test that wants the link
    // sets it.
    public string? ReplyUrl { get; set; }

    // CB-199: IsReadOnly can flip after bind now, so a test calls
    // RaiseReadOnlyChanged — the same two steps a real session takes when a send
    // is refused: change the property, then say so.
    public event Action? ReadOnlyChanged;

    public void RaiseReadOnlyChanged(bool isReadOnly)
    {
        IsReadOnly = isReadOnly;
        ReadOnlyChanged?.Invoke();
    }

    // IRemoteChatInterrupt. False by default, which is what every session that
    // predates CB-199 answered by not implementing it at all.
    public bool CanInterrupt { get; set; }

    // CB-270 Stage 4: run controls. Null by default, which is a session with none
    // to show; a test sets it, then raises RunOptionsChanged the way a real session
    // does once the gateway has said something new. Each Set records what the panel
    // asked for and answers with ReplyToRunChange, null meaning success.
    public RemoteRunOptions? RunOptions { get; set; }

    public event Action? RunOptionsChanged;

    public void RaiseRunOptionsChanged(RemoteRunOptions? options)
    {
        RunOptions = options;
        RunOptionsChanged?.Invoke();
    }

    public List<string?> ModelRequests { get; } = new();
    public List<string?> ThinkingRequests { get; } = new();
    public List<bool?> FastRequests { get; } = new();
    public string? ReplyToRunChange { get; set; }
    public int? TimeoutMinutes { get; set; }

    // When set, a change waits for it before answering, so a test can rebind the
    // panel or take the options away while the request is out.
    public TaskCompletionSource<string?>? RunChangeGate { get; set; }

    public Task<string?> SetModelAsync(string? id)
    {
        ModelRequests.Add(id);
        return RunChangeGate?.Task ?? Task.FromResult(ReplyToRunChange);
    }

    public Task<string?> SetThinkingAsync(string? id)
    {
        ThinkingRequests.Add(id);
        return Task.FromResult(ReplyToRunChange);
    }

    public Task<string?> SetFastModeAsync(bool? on)
    {
        FastRequests.Add(on);
        return Task.FromResult(ReplyToRunChange);
    }

    public event Action? InterruptChanged;

    public void RaiseInterruptChanged(bool canInterrupt)
    {
        CanInterrupt = canInterrupt;
        InterruptChanged?.Invoke();
    }

    // Counted, like OpenElsewhere below, so a test of the Stop control can
    // assert that the click reached the session.
    public int CancelCalls { get; private set; }

    // Counted rather than performed. The real one opens or focuses a real
    // window, which is the half this suite must never execute — what is being
    // tested is that a click on the button reaches the session at all.
    public int OpenElsewhereCalls { get; private set; }

    public void OpenElsewhere() => OpenElsewhereCalls++;

    private readonly List<ChatTurn> _history;
    public IReadOnlyList<ChatTurn> History => _history;

    public event Action<ChatTurn>? TurnAdded;
    public event Action<ChatTurn>? TurnUpdated;
    public event Action<RemoteChatState>? StateChanged;

    // What SendAsync was actually called with, in order — so a test can
    // assert on what the panel sent without the fake having to simulate a
    // reply.
    public List<string> SentTexts { get; } = new();

    // What SendAsync (and SendWithImagesAsync below) hand back — CB-35's
    // whole reason for existing. Settable per test rather than a
    // constructor argument: most of this suite predates the ticket and
    // wants the ordinary "it went through" answer, and defaulting to Sent
    // is what keeps every one of those tests passing unchanged. A test of
    // the retain-on-failure behaviour sets this to Failed before calling
    // Send() on the panel, the same way ComposerHint is set for a
    // can't-type panel.
    public ChatSendOutcome SendOutcome { get; set; } = ChatSendOutcome.Sent;

    public FakeChatSession(IEnumerable<ChatTurn>? seedHistory = null)
    {
        _history = seedHistory?.ToList() ?? new List<ChatTurn>();
    }

    public Task<ChatSendOutcome> SendAsync(string text)
    {
        SentTexts.Add(text);

        var turn = new ChatTurn { Role = ChatRole.User, Text = text };
        _history.Add(turn);
        TurnAdded?.Invoke(turn);

        return Task.FromResult(SendOutcome);
    }

    public void Cancel() => CancelCalls++;

    // What SendWithImagesAsync was actually called with — the panel's paste
    // path takes this route instead of SendAsync whenever it is holding at
    // least one pending picture (see IRemoteChatImages).
    public List<(string Text, List<string> ImagePaths)> SentWithImages { get; } = new();

    // CB-270 Stage 4: whether a paste of a picture is taken as one. True by
    // default, which is what an implementation of the interface meant before it
    // had the question; a test sets it false to be a session that has the
    // interface and no use for it right now.
    public bool CanSendImages { get; set; } = true;

    public Task<ChatSendOutcome> SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths)
    {
        SentWithImages.Add((text, imagePaths.ToList()));

        var turn = new ChatTurn { Role = ChatRole.User, Text = text };
        _history.Add(turn);
        TurnAdded?.Invoke(turn);

        return Task.FromResult(SendOutcome);
    }

    // Test helpers, not part of the interface: raise the two events the
    // panel subscribes to but that SendAsync never needs to for these tests.
    public void RaiseTurnAdded(ChatTurn turn)
    {
        _history.Add(turn);
        TurnAdded?.Invoke(turn);
    }

    public void RaiseTurnUpdated(ChatTurn turn) => TurnUpdated?.Invoke(turn);

    public void RaiseStateChanged(RemoteChatState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    // IRemoteChatFetchWait. The panel draws a wait for any session that offers
    // one, so the fake offers one it can drive: Waiting(seconds) puts the start
    // in the past, which is how a test asserts a counter without waiting for a
    // clock.
    public DateTimeOffset? WaitingSince { get; private set; }

    public string WaitingFor { get; set; } = "its conversation";

    public event Action? WaitChanged;

    public void BeginWaiting(TimeSpan alreadyElapsed = default)
    {
        WaitingSince = DateTimeOffset.Now - alreadyElapsed;
        WaitChanged?.Invoke();
    }

    public void EndWaiting()
    {
        WaitingSince = null;
        WaitChanged?.Invoke();
    }

    // IRemoteChatPeerInbound (CB-267). No offer by default, which is what every
    // session that is not running without permission prompts answers — so every
    // test that predates the ticket gets no box. Settable, and RaiseInboundChanged
    // says so, the same two steps RaiseReadOnlyChanged takes.
    public PeerInboundOffer? InboundOffer { get; set; }

    public bool HasHeldMessage { get; set; }

    public event Action? InboundChanged;

    public void RaiseInboundChanged() => InboundChanged?.Invoke();

    // How many handlers are attached, so a rebind test can prove the panel
    // let go of the previous session.
    public int InboundSubscribers => InboundChanged?.GetInvocationList().Length ?? 0;

    // Counted, and answered from EnableGate when a test wants to hold the write
    // open (a second click mid-write, a rebind mid-write); EnableResult otherwise.
    public int EnableCalls { get; private set; }

    public bool EnableResult { get; set; } = true;

    public TaskCompletionSource<bool>? EnableGate { get; set; }

    public Task<bool> EnableInboundAsync()
    {
        EnableCalls++;
        return EnableGate?.Task ?? Task.FromResult(EnableResult);
    }

    // Counted. A resend that goes in clears the held flag and says so, which is
    // what the real session does once the second attempt is not held.
    public int ResendCalls { get; private set; }

    public Task<ChatSendOutcome> ResendHeldAsync()
    {
        ResendCalls++;
        HasHeldMessage = false;
        InboundChanged?.Invoke();
        return Task.FromResult(ChatSendOutcome.Sent);
    }
}
