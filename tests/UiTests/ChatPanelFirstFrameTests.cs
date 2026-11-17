using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-286: opening a panel must not show a frame laid out for the previous
// conversation's size. Headless never presents an intermediate frame, so what
// is asserted is the state the gate exposes: hidden until Bounds and the
// per-row widths agree with the size the panel was bound at.
[Collection("Settings")]
public class ChatPanelFirstFrameTests : IDisposable
{
    private readonly List<string> _ids = new();

    private FakeChatSession Fake()
    {
        var id = "ff-" + Guid.NewGuid();
        _ids.Add(id);
        return new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "hello there", IsComplete = true },
            new ChatTurn { Role = ChatRole.Assistant, Text = "hi, how can I help today?", IsComplete = true },
        }) { SessionId = id, DisplayName = "FF" };
    }

    private static OrbWindow Orb(double w, double h)
    {
        var key = "ff-" + Guid.NewGuid();
        OrbweaverSettings.SetChatPanelSize(key, w, h);
        return new OrbWindow(key) { PositionKey = key };
    }

    private static void Pump(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public void Dispose()
    {
        foreach (var id in _ids) ChatPanel.CloseFor(id);
        Pump();
    }

    [AvaloniaFact]
    public void Rebind_HidesPanelUntilLaidOutAtTheNewSize()
    {
        var a = Orb(600, 640);
        var b = Orb(300, 260);

        ChatPanel.OpenFor(a, Fake());
        Pump(ChatPanelReveal.MaxFrames);
        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.IsRevealed);
        Assert.Equal(new Size(600, 640), panel.Bounds.Size);

        ChatPanel.OpenFor(b, Fake());

        // The window the user could otherwise have seen: still A's layout.
        Assert.False(panel.IsRevealed);
        Assert.Equal(new Size(600, 640), panel.Bounds.Size);
        Assert.All(panel.TurnAvailableWidths, w => Assert.True(w > 500));

        Pump(ChatPanelReveal.MaxFrames);

        Assert.True(panel.IsRevealed);
        Assert.Equal(new Size(300, 260), panel.Bounds.Size);
        Assert.All(panel.TurnAvailableWidths, w => Assert.True(w < 300));
    }

    [AvaloniaFact]
    public void FirstOpen_IsAlsoGatedAndThenShown()
    {
        var panel = OpenAndGet(Orb(420, 380));
        Assert.True(panel.IsRevealed);
        Assert.Equal(new Size(420, 380), panel.Bounds.Size);
    }

    private ChatPanel OpenAndGet(OrbWindow orb)
    {
        ChatPanel.OpenFor(orb, Fake());
        Assert.False(ChatPanelTestAccess.Instance!.IsRevealed);
        Pump(ChatPanelReveal.MaxFrames);
        return ChatPanelTestAccess.Instance!;
    }

    [AvaloniaFact]
    public void HidingWhilePending_DoesNotRevealLater()
    {
        var fake = Fake();
        ChatPanel.OpenFor(Orb(500, 500), fake);
        var panel = ChatPanelTestAccess.Instance!;
        Assert.False(panel.IsRevealed);

        ChatPanel.HideFor(fake.SessionId);
        Pump(ChatPanelReveal.MaxFrames);

        Assert.False(panel.IsRevealed);
    }
}
