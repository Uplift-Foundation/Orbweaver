using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-443: the panel a Paperclip orb opens. The capture holds what a person reads:
// the task line, their own bubble, a tool line that ran and one that failed, the
// agent's answer, "Open in Paperclip" above the box, and the box saying
// "Waking Nova…" right after a message. Whether the red tool line reads as a
// failure, and whether the link sits where a thumb would look for it, are
// judgements no assertion makes.
[Collection("Settings")]
public class PaperclipChatScreenshots : IDisposable
{
    private readonly List<string> _panelsToClean = new();
    public void Dispose() { foreach (var id in _panelsToClean) ChatPanel.HideFor(id); }

    private sealed class Feed : IPaperclipChatFeed
    {
        public event Action<ChatSnapshot>? Updated;
        public void Opened() { }
        public void Closed() { }
        public void Nudge() { }
        public void Push(ChatSnapshot s) => Updated?.Invoke(s);
    }

    private sealed class Actions : IPaperclipActions
    {
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct) =>
            Task.FromResult(new PaperclipSent(new(PaperclipActionResult.Done), "c9", issueId));
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly DateTime T0 = new(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void AnAgentsThreadWithAToolRowItsReplyAndTheBoxWaking()
    {
        var id = "paperclip:screenshot-" + Guid.NewGuid();
        var feed = new Feed();
        var session = new PaperclipChatSession("shot", "Nova", "https://paperclip.example/EX/agents/shot", () => feed,
            () => new Actions(), () => [], () => new(Array.Empty<PaperclipApi.RawLiveRun>(), Array.Empty<PaperclipApi.RawRun>(),
                Array.Empty<PaperclipApi.RawIssue>()), null, () => PaperclipLink.Connected, () => T0.AddSeconds(100), a => a());
        _panelsToClean.Add(session.SessionId);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        ScreenshotHelper.Flush();
        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 420;
        panel.Height = 560;

        feed.Push(new ChatSnapshot(
            new("i1", "CB-12", "Triage the login bug", "in_progress"),
            new[]
            {
                new PaperclipChat.Comment("1", "Can you look at why login fails on Safari?", null, "u1", null, T0.AddSeconds(5)),
            },
            PaperclipChat.Activity.Empty("r1") with
            {
                Rows =
                [
                    new("r:r1:t:a", ChatRole.System, "Bash: git log --oneline -5 -- login.ts", T0.AddSeconds(20)),
                    new("r:r1:t:b", ChatRole.System, "Bash: npm test -- login", T0.AddSeconds(30), true),
                    new("r:r1:answer", ChatRole.Assistant, "The session cookie is set without SameSite=None, so Safari drops it on the redirect. I have a fix ready; the test run above failed on an unrelated snapshot.", T0.AddSeconds(40)),
                ]
            },
            false));
        ScreenshotHelper.Flush();

        // A message just sent: the bubble goes up and the box says Waking.
        var input = panel.FindControl<Avalonia.Controls.TextBox>("Input")!;
        input.Text = "Great, please open the PR.";
        input.RaiseEvent(new Avalonia.Input.KeyEventArgs
        { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        ScreenshotHelper.Flush();

        Assert.Equal("Waking Nova…", input.Watermark);
        Assert.Equal("Open in Paperclip", panel.FindControl<Avalonia.Controls.TextBlock>("SessionLink")!.Text);

        ScreenshotHelper.CaptureAlreadyShown(panel, "paperclip-chat-panel.png");
    }
}
