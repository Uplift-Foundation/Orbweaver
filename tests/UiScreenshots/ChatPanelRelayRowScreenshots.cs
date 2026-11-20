using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-330: the compact row a relayed teammate or peer message becomes. Whether it
// reads as a quiet note rather than a bubble, whether the bold name and the dim
// preview separate, whether the ellipsis and the chevron land where they should
// on both renderers — judgements no assertion can make.
//
// The first capture holds one of everything: a user turn and an assistant turn
// for contrast, a collapsed "finished" row with a colour dot, a row the reader
// has clicked open onto a Markdown list, a Team row, a "From another session"
// row, and a row with a persona's face. The second is the same history with the
// setting on Expanded.
[Collection("Settings")]
public class ChatPanelRelayRowScreenshots : IDisposable
{
    private readonly List<string> _panelsToClean = new();
    private readonly RelayedMessageDisplay _before = OrbweaverSettings.RelayedMessages;

    public void Dispose()
    {
        OrbweaverSettings.RelayedMessages = _before;
        ChatPanel.RelayAvatarForTests = null;
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static ChatTurn Relay(RelayedMessage relay, int minute) => new()
    {
        Role = ChatRole.System,
        Text = relay.Line,
        IsComplete = true,
        At = T0.AddMinutes(minute),
        Relay = relay
    };

    // The shape lead-idle-markdown-result has in tests/TranscriptTests/Captures/cb330.
    private const string MarkdownResult =
        "Merged CB-267 and checked the board.\n\n- PR 262 is on develop\n- Jira is Done\n- No follow-ups open\n\n**Nothing left for you.**";

    private static IEnumerable<ChatTurn> History() => new[]
    {
        new ChatTurn { Role = ChatRole.User, Text = "How is the team getting on?", IsComplete = true, At = T0 },
        new ChatTurn { Role = ChatRole.Assistant, Text = "Hana and Nadia are both reporting in now.", IsComplete = true, At = T0.AddMinutes(1) },
        Relay(new RelayedMessage(RelayKind.Teammate, "hana-moriyama", "green", "idle_notification", "finished",
            "Routine, no report.", "Routine, no report."), 2),
        Relay(new RelayedMessage(RelayKind.Teammate, "nadia-okafor", "orange", "idle_notification", "finished",
            "Merged CB-267 and checked the board.", MarkdownResult), 3),
        Relay(new RelayedMessage(RelayKind.Teammate, "celeste-ngata-2", "purple", "idle_notification", "finished",
            "Two jobs still running.", "Two jobs still running."), 4),
        Relay(new RelayedMessage(RelayKind.Teammate, null, null, "teammate_terminated", "",
            "juno-abernathy has shut down.", "juno-abernathy has shut down."), 5),
        Relay(new RelayedMessage(RelayKind.Peer, "cb-276", null, "message", "",
            "Can you re-run the Windows leg when you get a moment?", "Can you re-run the Windows leg when you get a moment?"), 6),
        Relay(new RelayedMessage(RelayKind.Teammate, "bryn-kowalczyk", "blue", "shutdown_approved", "shut down",
            "", ""), 7),
    };

    private static WriteableBitmap Face()
    {
        var bmp = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var offset = y * fb.RowBytes + x * 4;
                // A soft diagonal wash, so a crop that is wrong shows.
                System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, offset,
                    unchecked((int)(0xFF000000u | (uint)((60 + x * 4) << 16) | (uint)((110 + y * 3) << 8) | 0xC0u)));
            }
        return bmp;
    }

    private ChatPanel Open(string name)
    {
        var id = "screenshot-relay-" + name + "-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        // Only celeste's face resolves, so the capture shows a face and dots.
        ChatPanel.RelayAvatarForTests = who => who.StartsWith("celeste") ? Face() : null;

        var fake = new FakeChatSession(History()) { SessionId = id, DisplayName = "Lead" };
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 420;
        panel.Height = 560;
        ScreenshotHelper.Flush();
        return panel;
    }

    private static List<Border> Rows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "RelayRow" && b.IsVisible).ToList();

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
    public void CollapsedRowsWithOneOpenedByAClick()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Collapsed;
        var panel = Open("collapsed");

        Assert.Equal(6, Rows(panel).Count);
        ClickHeader(panel, Rows(panel)[1]);   // nadia's, the Markdown one

        var body = Rows(panel)[1].GetVisualDescendants().OfType<ContentControl>()
            .Single(c => c.Name == "RelayBodyHost");
        Assert.True(body.IsVisible);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-relay-rows-collapsed.png");
    }

    [AvaloniaFact]
    public void EveryRowOpenWhenTheSettingIsExpanded()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Expanded;
        var panel = Open("expanded");

        var rows = Rows(panel);
        Assert.Equal(6, rows.Count);

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-relay-rows-expanded.png");
    }
}
