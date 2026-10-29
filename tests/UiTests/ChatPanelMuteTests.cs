using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-298: the chat panel's mute button. The decisions (which glyph, what the
// tooltip says) are pinned in SymbolMarksMuteTests; this is the wiring — a click
// reaches AudioMute, the header follows the flag from any source, and a closed
// panel stops following it.
//
// [Collection("Settings")] because AudioMute.Set writes OrbweaverSettings.Muted,
// which is process-wide. Mute is restored in Dispose so no later class inherits
// a silenced app.
[Collection("Settings")]
public class ChatPanelMuteTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public ChatPanelMuteTests()
    {
        AudioMute.Set(false);
    }

    public void Dispose()
    {
        AudioMute.Set(false);
        foreach (var id in _toClean) ChatPanel.CloseFor(id);
        Flush();
    }

    private FakeChatSession NewFake()
    {
        var id = "mute-" + Guid.NewGuid();
        _toClean.Add(id);

        return new FakeChatSession(null) { SessionId = id, DisplayName = "Fake Session" };
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private ChatPanel Open()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        Flush();

        return ChatPanel.PanelFor(fake.SessionId)!;
    }

    // A single PointerPressed raised on the control itself, the pattern
    // ChatPanelPinTests uses: it reaches the handler without a real mouse and
    // never touches TerminalFocuser.
    private static void Press(Control control, Control root)
    {
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, root, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static Control Button(ChatPanel p) => p.FindControl<Control>("MuteButton")!;
    private static string Glyph(ChatPanel p) => p.FindControl<TextBlock>("MuteGlyph")!.Text!;
    private static IBrush? Fill(ChatPanel p) => p.FindControl<Avalonia.Controls.Shapes.Ellipse>("MuteFill")!.Fill;

    private static string? Tip(ChatPanel p) => ToolTip.GetTip(Button(p)) as string;

    [AvaloniaFact]
    public void ANewPanelStartsUnmuted()
    {
        var panel = Open();

        Assert.Equal(SymbolMarks.MuteLook(false), Glyph(panel));
        Assert.StartsWith("Mute", Tip(panel));
    }

    [AvaloniaFact]
    public void ANewPanelOpenedWhileMutedStartsMuted()
    {
        AudioMute.Set(true);

        var panel = Open();

        Assert.Equal(SymbolMarks.MuteLook(true), Glyph(panel));
        Assert.StartsWith("Unmute", Tip(panel));
    }

    [AvaloniaFact]
    public void PressingTheButtonMutesAndAgainUnmutes()
    {
        var panel = Open();
        var idleFill = Fill(panel);

        Press(Button(panel), panel);

        Assert.True(AudioMute.IsMuted);
        Assert.Equal("\U0001F507", Glyph(panel));
        Assert.NotSame(idleFill, Fill(panel));
        Assert.StartsWith("Unmute", Tip(panel));

        Press(Button(panel), panel);

        Assert.False(AudioMute.IsMuted);
        Assert.Equal("\U0001F50A", Glyph(panel));
        Assert.Same(idleFill, Fill(panel));
        Assert.StartsWith("Mute", Tip(panel));
    }

    [AvaloniaFact]
    public void TheTooltipNamesTheBoundChord()
    {
        var panel = Open();
        var bound = HotkeyActions.Bound(HotkeyAction.ToggleMute);

        Assert.Equal(SymbolMarks.MuteTip(false, bound), Tip(panel));
        Assert.NotNull(bound);
        Assert.Contains(HotkeyRegistry.Format(bound!.Value), Tip(panel));
    }

    [AvaloniaFact]
    public void OnePressUpdatesEveryOpenPanel()
    {
        var a = Open();
        var b = Open();

        Press(Button(a), a);

        Assert.Equal("\U0001F507", Glyph(a));
        Assert.Equal("\U0001F507", Glyph(b));
    }

    [AvaloniaFact]
    public void AnExternalSetUpdatesThePanelsTheWayTheTrayAndHotkeyDo()
    {
        var panel = Open();

        AudioMute.Set(true);
        Assert.Equal("\U0001F507", Glyph(panel));

        AudioMute.Set(false);
        Assert.Equal("\U0001F50A", Glyph(panel));
    }

    [AvaloniaFact]
    public async Task ASetFromAnotherThreadStillReachesThePanel()
    {
        var panel = Open();

        await Task.Run(() => AudioMute.Set(true));
        Flush();

        Assert.Equal("\U0001F507", Glyph(panel));
    }

    [AvaloniaFact]
    public void AClosedPanelNoLongerFollowsTheFlag()
    {
        var panel = Open();
        var before = Glyph(panel);

        panel.Close();
        Flush();
        AudioMute.Set(true);

        Assert.Equal(before, Glyph(panel));
    }

    [AvaloniaFact]
    public void ADissolvedPanelNoLongerFollowsTheFlag()
    {
        var panel = Open();
        var before = Glyph(panel);

        // Dissolve is private; it is what an unpinned panel does on focus loss.
        typeof(ChatPanel).GetMethod("Dissolve",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(panel, null);
        Flush();
        AudioMute.Set(true);

        Assert.Equal(before, Glyph(panel));
    }

    [AvaloniaFact]
    public void TheMicButtonIsUntouchedByMute()
    {
        var panel = Open();
        var mic = panel.FindControl<Control>("MicButton")!;
        var visible = mic.IsVisible;

        AudioMute.Set(true);

        Assert.Equal(visible, mic.IsVisible);
        Assert.Equal(OrbweaverSettings.VoiceInputEnabled, mic.IsVisible);
    }

    [AvaloniaFact]
    public void TheHeaderOrderIsPinMuteReadAloudClose()
    {
        var panel = Open();
        int Col(string name) => Grid.GetColumn(panel.FindControl<Control>(name)!);

        Assert.True(Col("PinButton") < Col("MuteButton"));
        Assert.True(Col("MuteButton") < Col("SpeakButton"));
        Assert.True(Col("SpeakButton") < Col("CloseButton"));
    }
}
