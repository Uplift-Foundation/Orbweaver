using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6: how an approval looks in the panel. Real prompts built by the
// same OpenClawApprovalText the session uses, on a fake session, so the picture is
// what the production wording and bounds produce. The record shapes behind them are
// declared, not measured.
public class ChatPanelApprovalScreenshots : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private FakeChatSession Fake(ChatPrompt? prompt, IEnumerable<ChatTurn>? history = null)
    {
        var id = "screenshot-approval-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Nova", Prompt = prompt };
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static IEnumerable<ChatTurn> Context() => new[]
    {
        new ChatTurn { Role = ChatRole.User, Mine = true, Text = "Count the files in the workspace." },
        new ChatTurn { Role = ChatRole.Assistant, Text = "I need to run a command for that." },
    };

    private static void Capture(ChatPrompt prompt, string name, Action<ChatPanel>? after = null)
    {
        var fake = new ChatPanelApprovalScreenshots().Fake(prompt, Context());
        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        after?.Invoke(ChatPanelTestAccess.Instance!);
        ScreenshotHelper.Flush();
        ChatPanelTestAccess.Instance!.UpdateLayout();
        ScreenshotHelper.Flush();
        ChatPanelTestAccess.Instance!.FindControl<TextBlock>("PromptTitle")!.InvalidateMeasure();
        ChatPanelTestAccess.Instance!.UpdateLayout();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, name);
        ChatPanel.HideFor(fake.SessionId);
    }

    // An ordinary request: what it wants to run, where, and the two answers with
    // Deny first.
    [AvaloniaFact]
    public void AnApprovalPromptWithBothOptions() =>
        Capture(
            OpenClawApprovalText.Prompt("ap-1", "Nova", "ls /home/op/workspace | wc -l", "/home/op/workspace", "gateway"),
            "chat-panel-approval-prompt.png");

    // A request that could not be shown safely: nothing of it is drawn, and it can
    // only be denied.
    [AvaloniaFact]
    public void AnUnsafeRequestIsNotShownAndOffersOnlyDeny() =>
        Capture(
            OpenClawApprovalText.Prompt("ap-2", "Nova", "echo hi\nrm -rf /", "/", "gateway"),
            "chat-panel-approval-unsafe.png");

    // A field that is not what is being approved, cut with the count of what was
    // left out.
    [AvaloniaFact]
    public void ALongWorkingDirectoryIsCappedWithTheCount() =>
        Capture(
            OpenClawApprovalText.Prompt("ap-3", "Nova", "ls", "/home/op/" + new string('d', 300), "gateway"),
            "chat-panel-approval-capped-field.png");

    // A long command, shown whole in a block that scrolls, scrolled to its end.
    [AvaloniaFact]
    public void ALongCommandScrollsInsideThePrompt() =>
        Capture(
            OpenClawApprovalText.Prompt("ap-4", "Nova", string.Join(" && ", Enumerable.Range(1, 300).Select(n => $"step{n}")), "/tmp", "gateway"),
            "chat-panel-approval-long-command.png",
            panel => panel.FindControl<ScrollViewer>("PromptTitleScroll")!.ScrollToEnd());

    // Past the bound the end cannot be read here, so it can only be denied.
    [AvaloniaFact]
    public void ACommandPastTheBoundOffersOnlyDeny() =>
        Capture(
            OpenClawApprovalText.Prompt("ap-5", "Nova", new string('x', 4100), "/tmp", "gateway"),
            "chat-panel-approval-too-long.png",
            panel => panel.FindControl<ScrollViewer>("PromptTitleScroll")!.ScrollToEnd());

    // Each way an answer, or an approval, ends, as the lines the transcript gets.
    [AvaloniaFact]
    public void EachWayAnApprovalEnds()
    {
        var lines = new[]
        {
            OpenClawApprovalText.Answered("allow-once"),
            OpenClawApprovalText.Answered("deny"),
            OpenClawApprovalText.AlreadyDecided("allowed"),
            OpenClawApprovalText.NoLongerPending,
            OpenClawApprovalText.CouldntAnswer("not connected to the gateway"),
            OpenClawApprovalText.Ended("denied", "user"),
            OpenClawApprovalText.Ended("allowed", "user"),
            OpenClawApprovalText.Ended("denied", "timeout"),
            OpenClawApprovalText.Ended("denied", "run-aborted"),
            OpenClawApprovalText.Ended("denied", "no-route"),
        };

        var fake = Fake(null, lines.Select(l => new ChatTurn { Role = ChatRole.System, IsComplete = true, Text = l }));
        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, "chat-panel-approval-outcomes.png");
    }
}
