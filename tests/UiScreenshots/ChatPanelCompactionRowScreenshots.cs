using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-356: the row a Claude Code compaction becomes. Whether it reads as a quiet
// note of the same family as a relayed message rather than a bubble, and whether
// the opened summary's headings, list and code block look right on both
// renderers, are judgements no assertion can make.
//
// Two captures of one panel (a user turn, an assistant turn, an automatic
// compaction, an assistant turn after it): collapsed, and clicked open. A third
// holds a manual row, an orphan "Conversation compacted" row and a relay row
// together, to show they read as one family.
[Collection("Settings")]
public class ChatPanelCompactionRowScreenshots : IDisposable
{
    private readonly List<string> _panelsToClean = new();

    public void Dispose()
    {
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    // The shape of the summary in tests/TranscriptTests/Captures/cb356/auto-adjacent.
    private const string Summary =
        "## Primary request and intent\n\nShip the compaction row, so a compaction is one collapsed line instead of a giant user bubble.\n\n" +
        "## Key decisions\n\n- Reuse the relay row's template\n- Never governed by the relay setting\n- No setting of its own\n\n" +
        "## Code\n\n```csharp\npublic bool IsCompactRow => IsRelay || IsCompaction;\n```";

    private static ChatTurn Compaction(CompactionNote note, int minute) => new()
    {
        Role = ChatRole.System, Text = note.Line, IsComplete = true, At = T0.AddMinutes(minute), Compaction = note
    };

    private static ChatTurn Say(ChatRole role, string text, int minute) =>
        new() { Role = role, Text = text, IsComplete = true, At = T0.AddMinutes(minute) };

    private ChatPanel Open(string name, IEnumerable<ChatTurn> history)
    {
        var id = "screenshot-compaction-" + name + "-" + Guid.NewGuid();
        _panelsToClean.Add(id);
        var fake = new FakeChatSession(history) { SessionId = id, DisplayName = "Lead" };
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 420;
        panel.Height = 560;
        ScreenshotHelper.Flush();
        return panel;
    }

    private static IEnumerable<ChatTurn> InContext() => new[]
    {
        Say(ChatRole.User, "Keep going on the compaction row.", 0),
        Say(ChatRole.Assistant, "The row is in; running the targeted tests now.", 1),
        Compaction(new CompactionNote("auto", 582_000, 9_000, 41_000, Summary, HasBoundary: true), 2),
        Say(ChatRole.Assistant, "Picking up from the summary: the tests are green.", 3),
    };

    private static List<Border> CompactionRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "RelayRow" && b.IsVisible
                && b.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "CompactionIcon").IsVisible)
            .ToList();

    private static void ClickHeader(ChatPanel panel, Border row)
    {
        var header = row.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RelayHeader");
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        header.RaiseEvent(new PointerPressedEventArgs(
            header, pointer, panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        ScreenshotHelper.Flush();
    }

    [AvaloniaFact]
    public void CompactionRowCollapsedInContext()
    {
        var panel = Open("collapsed", InContext());

        Assert.Single(CompactionRows(panel));
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-compaction-collapsed.png");
    }

    [AvaloniaFact]
    public void CompactionRowExpandedToItsSummary()
    {
        var panel = Open("expanded", InContext());

        ClickHeader(panel, Assert.Single(CompactionRows(panel)));

        var body = Assert.Single(CompactionRows(panel)).GetVisualDescendants().OfType<ContentControl>()
            .Single(c => c.Name == "RelayBodyHost");
        Assert.True(body.IsVisible);
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-compaction-expanded.png");
    }

    [AvaloniaFact]
    public void CompactionRowsBesideARelayRowReadAsOneFamily()
    {
        var relay = new RelayedMessage(RelayKind.Teammate, "hana-moriyama", "green", "idle_notification",
            "finished", "Routine, no report.", "Routine, no report.");
        var panel = Open("family", new[]
        {
            Compaction(new CompactionNote("manual", 120_000, 6_000, null, Summary, HasBoundary: true), 0),
            Compaction(new CompactionNote(null, null, null, null, Summary, HasBoundary: false), 1),
            new ChatTurn { Role = ChatRole.System, Text = relay.Line, IsComplete = true, At = T0.AddMinutes(2), Relay = relay },
        });

        Assert.Equal(2, CompactionRows(panel).Count);
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-compaction-family.png");
    }
}
