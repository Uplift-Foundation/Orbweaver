namespace Orbweaver.Tests;

// Same fake as tests/UiTests/FakeChatSession.cs, copied rather than shared
// across projects — this project deliberately doesn't reference that one
// (see TestAppBuilder's own comment on why the two stay isolated), and the
// fake itself needs nothing internal, so a plain copy costs nothing. See
// the original for the four IRemoteChatSession rules this honours.
internal sealed class FakeChatSession :
    IRemoteChatSession, IRemoteChatComposer, IRemoteChatElsewhere, IRemoteChatRoom,
    IRemoteChatReadOnly, IRemoteChatInterrupt,
    IRemoteChatMachine, IRemoteChatPeerInbound, IRemoteChatRunOptions, IRemoteChatPrompts
{
    // CB-270 Stage 6: a prompt posed for a capture; answering does nothing.
    public ChatPrompt? Prompt { get; set; }

    public event Action? PromptChanged { add { } remove { } }

    public Task AnswerAsync(ChatPromptOption option) => Task.CompletedTask;

    public void AnswerElsewhere() { }

    // CB-270 Stage 4: run controls, posed for a capture. See the UiTests copy.
    public RemoteRunOptions? RunOptions { get; set; }

    public event Action? RunOptionsChanged { add { } remove { } }

    public int? TimeoutMinutes { get; set; }

    public Task<string?> SetModelAsync(string? id) => Task.FromResult<string?>(null);

    public Task<string?> SetThinkingAsync(string? id) => Task.FromResult<string?>(null);

    public Task<string?> SetFastModeAsync(bool? on) => Task.FromResult<string?>(null);

    // Both default to what an ordinary typeable session answers — an ordinary
    // hint and no button — so every capture that predates them is unchanged.
    public string ComposerHint { get; set; } = "Message…";

    public bool CanOpenElsewhere { get; set; }

    // False by default, so every capture that predates CB-164 still shows a
    // panel with a composer in it. Settable for the reason the UiTests fake's
    // copy is: one class has to be able to pose for both sides of the picture.
    public bool IsReadOnly { get; set; }

    // Null by default, which is the "read-only with nowhere to go" case — the
    // panel then shows the sentence and no link. A test that wants the link
    // sets it.
    public string? ReplyUrl { get; set; }

    // CB-199: see the UiTests copy. A capture sets the property and raises.
    public event Action? ReadOnlyChanged;

    public void RaiseReadOnlyChanged(bool isReadOnly)
    {
        IsReadOnly = isReadOnly;
        ReadOnlyChanged?.Invoke();
    }

    // IRemoteChatInterrupt, false by default like a session without it.
    public bool CanInterrupt { get; set; }

    public event Action? InterruptChanged;

    public void RaiseInterruptChanged(bool canInterrupt)
    {
        CanInterrupt = canInterrupt;
        InterruptChanged?.Invoke();
    }

    // Null by default, which is what every capture before CB-164 showed: a
    // session that names no machine is read as being on this one. Set for a
    // cloud capture, because a picture of a cloud session with the reviewer's
    // own laptop named in its header is a picture of the bug
    // ClaudeCloudChatSession.MachineName exists to prevent — and a screenshot
    // that contradicts the app is worse than no screenshot.
    public string? MachineName { get; set; }

    public event Action? MachineChanged
    {
        add { }
        remove { }
    }

    // False by default, same reasoning as tests/UiTests/FakeChatSession.cs:
    // an ordinary capture is a one-to-one conversation, and only the
    // room-attribution scenario (CB-36) sets this.
    public bool IsRoom { get; set; }

    // Never called from a capture, and it would open a window if it were.
    public void OpenElsewhere()
    {
    }

    public string SessionId { get; init; } = "fake-session";
    public string DisplayName { get; init; } = "Fake Session";
    public RemoteChatState State { get; set; } = RemoteChatState.Connected;

    private readonly List<ChatTurn> _history;
    public IReadOnlyList<ChatTurn> History => _history;

    public event Action<ChatTurn>? TurnAdded;
    public event Action<ChatTurn>? TurnUpdated;
    public event Action<RemoteChatState>? StateChanged;

    public List<string> SentTexts { get; } = new();

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

        return Task.FromResult(ChatSendOutcome.Sent);
    }

    public void Cancel()
    {
        // No-op: nothing is ever in flight in this fake.
    }

    // IRemoteChatPeerInbound (CB-267): enough to pose the box in each of its
    // states. No offer by default, so every other capture is unchanged.
    public PeerInboundOffer? InboundOffer { get; set; }

    public bool HasHeldMessage { get; set; }

    public event Action? InboundChanged
    {
        add { }
        remove { }
    }

    public Task<bool> EnableInboundAsync() => Task.FromResult(true);

    public Task<ChatSendOutcome> ResendHeldAsync() => Task.FromResult(ChatSendOutcome.Sent);
}
