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
//
// An OrbWindow's PositionKey defaults to empty, and a panel opened for an
// empty key ignores its saved size — so Orb() below sets it, or the sizes
// these cases depend on would silently never apply.
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

    [AvaloniaFact]
    public void HiddenReopen_IsGatedAndThenRevealed()
    {
        var a = Orb(600, 640);
        var b = Orb(300, 260);
        var fakeA = Fake();

        ChatPanel.OpenFor(a, fakeA);
        Pump(ChatPanelReveal.MaxFrames);
        var panel = ChatPanelTestAccess.Instance!;
        ChatPanel.HideFor(fakeA.SessionId);
        Pump(ChatPanelReveal.MaxFrames);
        Assert.False(panel.IsRevealed);

        ChatPanel.OpenFor(b, Fake());

        // Whether Show() lays out before returning is recorded, not asserted
        // either way: it is the headless answer, not a guarantee about the
        // native window (see the PR body).
        Assert.False(panel.IsRevealed);
        Pump(ChatPanelReveal.MaxFrames);
        Assert.True(panel.IsRevealed);
        Assert.Equal(new Size(300, 260), panel.Bounds.Size);
    }

    [AvaloniaFact]
    public void HideThenReopenSameSession_Reveals()
    {
        var fake = Fake();
        var orb = Orb(400, 300);
        ChatPanel.OpenFor(orb, fake);
        ChatPanel.HideFor(fake.SessionId);
        Pump(ChatPanelReveal.MaxFrames);
        var panel = ChatPanelTestAccess.Instance!;
        Assert.False(panel.IsRevealed);

        ChatPanel.OpenFor(orb, fake);
        Pump(ChatPanelReveal.MaxFrames);
        Assert.True(panel.IsRevealed);
    }

    [AvaloniaFact]
    public void RapidAbaRebinds_EndRevealedAtA()
    {
        var a = Orb(560, 600);
        var b = Orb(300, 260);
        ChatPanel.OpenFor(a, Fake());
        ChatPanel.OpenFor(b, Fake());
        Pump(1);
        ChatPanel.OpenFor(a, Fake());
        var panel = ChatPanelTestAccess.Instance!;
        Assert.False(panel.IsRevealed);

        Pump(ChatPanelReveal.MaxFrames);
        Assert.True(panel.IsRevealed);
        Assert.Equal(new Size(560, 600), panel.Bounds.Size);
    }

    [AvaloniaFact]
    public void CloseForMidGate_DoesNotRevealThePanel()
    {
        var fake = Fake();
        ChatPanel.OpenFor(Orb(500, 500), fake);
        var panel = ChatPanelTestAccess.Instance!;

        ChatPanel.CloseFor(fake.SessionId);
        Pump(ChatPanelReveal.MaxFrames);

        Assert.False(panel.IsRevealed);
    }

    [AvaloniaFact]
    public void RestorePinned_RevealsWithoutActivation()
    {
        var orb = Orb(420, 360);
        var fake = Fake();
        ChatPanel.RestorePinned(orb, fake, new PixelPoint(40, 40));
        var panel = ChatPanelTestAccess.PanelFor(fake.SessionId)!;
        Assert.False(panel.IsRevealed);

        Pump(ChatPanelReveal.MaxFrames);
        Assert.True(panel.IsRevealed);
    }

    [AvaloniaFact]
    public void PinAndUnpin_DoNotChangeReveal()
    {
        var fake = Fake();
        ChatPanel.OpenFor(Orb(420, 360), fake);
        Pump(ChatPanelReveal.MaxFrames);
        var panel = ChatPanelTestAccess.PanelFor(fake.SessionId)!;
        Assert.True(panel.IsRevealed);

        panel.TogglePin();
        Pump(2);
        Assert.True(panel.IsRevealed);
        panel.TogglePin();
        Pump(2);
        Assert.True(panel.IsRevealed);
    }

    [AvaloniaFact]
    public void RepositionAndTextScale_DoNotHideARevealedPanel()
    {
        var orb = Orb(420, 360);
        var fake = Fake();
        ChatPanel.OpenFor(orb, fake);
        Pump(ChatPanelReveal.MaxFrames);
        var panel = ChatPanelTestAccess.Instance!;

        ChatPanel.RepositionFor(orb);
        ChatPanel.ReapplyTextScale();
        panel.Width = 450;
        Pump(2);

        Assert.True(panel.IsRevealed);
    }
}
