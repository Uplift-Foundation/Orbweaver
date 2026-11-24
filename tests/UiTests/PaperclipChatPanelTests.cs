using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-443: the chat panel on a Paperclip agent. The real PaperclipChatSession is driven
// through a fake feed and fake actions, so what is asserted is what a person would see:
// the thread, the words in the box, the link above it, and what a failed send leaves.
// No orb is clicked (a click reaches TerminalFocuser), and nothing talks to Paperclip.
[Collection("Settings")]
public class PaperclipChatPanelTests : IDisposable
{
    private readonly List<string> _toClean = new();
    public void Dispose() { foreach (var id in _toClean) ChatPanel.HideFor(id); }

    internal sealed class FakeFeed : IPaperclipChatFeed
    {
        public event Action<ChatSnapshot>? Updated;
        public List<string> Calls { get; } = new();
        public void Opened() => Calls.Add("opened");
        public void Closed() => Calls.Add("closed");
        public void Nudge() => Calls.Add("nudge");
        public void Push(ChatSnapshot s) => Updated?.Invoke(s);
    }

    internal sealed class FakeActions : IPaperclipActions
    {
        public PaperclipSent Reply { get; set; } = new(new(PaperclipActionResult.Done), "c1", "i1");
        public List<string> Sent { get; } = new();
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct)
        { Sent.Add("comment:" + issueId + ":" + text); return Task.FromResult(Reply); }
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct)
        { Sent.Add("open:" + agentId + ":" + text); return Task.FromResult(Reply); }
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly DateTime T0 = new(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);
    private static PaperclipChat.TaskRef Task1 => new("i1", "CB-12", "Triage the login bug", "in_progress");

    private static ChatSnapshot Thread(bool refused = false) => new(
        Task1,
        new[]
        {
            new PaperclipChat.Comment("1", "Please look at the login bug", null, "u1", null, T0.AddSeconds(1)),
            new PaperclipChat.Comment("2", "Found it: the cookie is never set.", "ag1", null, null, T0.AddSeconds(9)),
        },
        PaperclipChat.Activity.Empty("r1") with
        {
            Rows =
            [
                new("r:r1:t:a", ChatRole.System, "Bash: git status", T0.AddSeconds(3)),
                new("r:r1:t:b", ChatRole.System, "Bash: dotnet test", T0.AddSeconds(4), true),
            ]
        },
        refused);

    private PaperclipSessions.ChatView _view = new(
        Array.Empty<PaperclipApi.RawLiveRun>(), Array.Empty<PaperclipApi.RawRun>(), Array.Empty<PaperclipApi.RawIssue>());
    private PaperclipAgent[] _roster = [];

    private (PaperclipChatSession Session, FakeFeed Feed, FakeActions Actions) Make(Func<DateTime>? now = null)
    {
        var feed = new FakeFeed();
        var actions = new FakeActions();
        var s = new PaperclipChatSession("ag1", "Nova", "https://p/EX/agents/ag1", () => feed, () => actions,
            () => _roster, () => _view, null, () => PaperclipLink.Connected, now, a => a());
        _toClean.Add(s.SessionId);
        return (s, feed, actions);
    }

    private static PaperclipAgent AgentWith(string status) =>
        new("ag1", "Nova", "r", "t", status, null, "paperclip_runner", "idle", PaperclipMark.None, null, [], null, null, null, null, null,
            DateTime.UtcNow, "https://p/EX/agents/ag1", false);

    private static void Flush() => Dispatcher.UIThread.RunJobs();
    private static void FlushRender() { Flush(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Flush(); }

    private static ChatPanel Open(PaperclipChatSession s)
    {
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), s);
        FlushRender();
        return ChatPanelTestAccess.Instance!;
    }

    private static TextBox InputOf(ChatPanel p) => p.FindControl<TextBox>("Input")!;

    private static void PressEnter(TextBox input)
    {
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();
    }

    [AvaloniaFact]
    public void OpeningThePanelStartsTheFeedAndTheThreadAppears()
    {
        var (s, feed, _) = Make();
        var panel = Open(s);

        Assert.Equal(new[] { "opened" }, feed.Calls);
        feed.Push(Thread());
        FlushRender();

        Assert.Equal(5, s.History.Count);
        Assert.Equal(5, panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children.Count);
        Assert.True(s.History[3].IsError);
        Assert.Equal(ChatRole.Assistant, s.History[4].Role);
    }

    [AvaloniaFact]
    public void ClosingThePanelStopsTheFeed()
    {
        var (s, feed, _) = Make();
        Open(s);
        ChatPanel.HideFor(s.SessionId);
        FlushRender();
        Assert.Contains("closed", feed.Calls);
    }

    [AvaloniaFact]
    public void OpenInPaperclipIsOneClickAboveTheBox()
    {
        var (s, feed, _) = Make();
        var panel = Open(s);
        feed.Push(Thread());
        FlushRender();

        var link = panel.FindControl<TextBlock>("SessionLink")!;
        Assert.True(link.IsVisible);
        Assert.Equal("Open in Paperclip", link.Text);
        Assert.True(panel.FindControl<Control>("ComposerRow")!.IsVisible);
    }

    [AvaloniaFact]
    public void TheBoxSaysItWakesTheAgent_AndOpensATaskWhenThereIsNone()
    {
        var (s, feed, _) = Make();
        var panel = Open(s);
        feed.Push(Thread());
        FlushRender();
        Assert.Equal("Message Nova. It wakes to read it.", InputOf(panel).Watermark);

        var (s2, feed2, _) = Make();
        var panel2 = Open(s2);
        feed2.Push(new ChatSnapshot(null, Array.Empty<PaperclipChat.Comment>(), null, false));
        FlushRender();
        Assert.Equal("Message Nova. This opens a new task.", InputOf(panel2).Watermark);
    }

    [AvaloniaFact]
    public void ASendPutsTheBubbleUp_ClearsTheBox_AndTheBoxSaysWaking()
    {
        var (s, feed, actions) = Make();
        var panel = Open(s);
        feed.Push(Thread());
        FlushRender();

        InputOf(panel).Text = "any update?";
        PressEnter(InputOf(panel));
        FlushRender();

        Assert.Equal(new[] { "comment:i1:any update?" }, actions.Sent);
        Assert.Equal("", InputOf(panel).Text ?? "");
        Assert.Equal(ChatRole.User, s.History[^1].Role);
        Assert.Equal("any update?", s.History[^1].Text);
        Assert.Equal("Waking Nova…", InputOf(panel).Watermark);
        Assert.Contains("nudge", feed.Calls);

        // The comment arriving on the next read is the same bubble, not a second one.
        feed.Push(Thread() with { Comments = Thread().Comments.Append(
            new PaperclipChat.Comment("c1", "any update?", null, "u1", null, T0.AddSeconds(20))).ToList() });
        FlushRender();
        Assert.Equal(6, s.History.Count);  // task, 2 comments, 2 tool lines, and the one sent
    }

    [AvaloniaFact]
    public void AFailedSendKeepsTheTextAndShowsOneRedLine()
    {
        var (s, feed, actions) = Make();
        actions.Reply = new(new(PaperclipActionResult.Unreachable, "Can't reach Paperclip"), null, null);
        var panel = Open(s);
        feed.Push(Thread());
        FlushRender();

        InputOf(panel).Text = "hello";
        PressEnter(InputOf(panel));
        FlushRender();

        Assert.Equal("hello", InputOf(panel).Text);
        Assert.True(s.History[^1].IsError);
        Assert.Equal("Can't reach Paperclip.", s.History[^1].Text);

        // A second failure reuses the line rather than stacking another.
        var count = s.History.Count;
        PressEnter(InputOf(panel));
        FlushRender();
        Assert.Equal(count, s.History.Count);
    }

    [AvaloniaFact]
    public void AKeyThatPaperclipRefusedHidesTheBoxAndOffersReconnect()
    {
        var (s, feed, _) = Make();
        var panel = Open(s);
        feed.Push(Thread(refused: true));
        FlushRender();

        Assert.False(panel.FindControl<Control>("ComposerRow")!.IsVisible);
        Assert.True(panel.FindControl<Control>("ReadOnlyBox")!.IsVisible);
        Assert.Equal("Reconnect to Paperclip", panel.FindControl<TextBlock>("ReadOnlyNote")!.Text);
        var link = panel.FindControl<TextBlock>("ReadOnlyLink")!;
        Assert.True(link.IsVisible);
        Assert.Equal("Open in Paperclip", link.Text);
    }

    [AvaloniaFact]
    public void AnAgentThatIsPausedOrWorkingSaysSoInTheBox()
    {
        var (s, feed, _) = Make();
        var panel = Open(s);
        feed.Push(Thread());
        FlushRender();

        _view = new([new("r1", "ag1", "running", T0, "i1", null, null)], [], []);
        feed.Push(Thread());
        FlushRender();
        Assert.Equal("Nova is working. Your message goes to this run.", InputOf(panel).Watermark);

        _view = new([], [], []);
        _roster = [AgentWith("paused")];
        feed.Push(Thread());
        FlushRender();
        Assert.Equal("Nova is paused in Paperclip. It reads this when resumed.", InputOf(panel).Watermark);
    }

    [AvaloniaFact]
    public void ThePanelTellsBothKindsOfSessionItOpenedAndClosed()
    {
        var (s, feed, _) = Make();
        var fake = new FakeChatSession(null) { SessionId = "other-" + Guid.NewGuid() };
        _toClean.Add(fake.SessionId);

        Open(s);
        Open(s); // rebinding to the same session does not start a second feed
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        FlushRender();

        Assert.Equal("closed", feed.Calls[^1]); // switching away closed the Paperclip one
        Assert.Equal("Open in your browser", ChatPanel.ReplyLabelFor(fake));
        Assert.Equal("Open in Paperclip", ChatPanel.ReplyLabelFor(s));
    }

    // The roster's own change (the agent was paused in Paperclip's page) reaches the box
    // without a feed tick: the session listens to PaperclipSessions.Changed while open.
    [AvaloniaFact]
    public void ARosterChangeRewordsTheBoxAtOnce_AndStopsListeningWhenClosed()
    {
        var (s, feed, _) = Make();
        var panel = Open(s);
        feed.Push(Thread());
        FlushRender();

        _roster = [AgentWith("paused")];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected");
        FlushRender();
        Assert.Equal("Nova is paused in Paperclip. It reads this when resumed.", InputOf(panel).Watermark);

        ChatPanel.HideFor(s.SessionId);
        FlushRender();
        _roster = [AgentWith("idle")];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected again");
        FlushRender();
        Assert.Equal("Nova is paused in Paperclip. It reads this when resumed.", s.ComposerHint);
        PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");
    }
}
