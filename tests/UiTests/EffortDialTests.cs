using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orbweaver;
using Xunit;

namespace Orbweaver.UiTests;

// CB-404: the round effort dial, driven headless — drag, tick click, every
// key, disabled and pending, and its automation peer.
public class EffortDialTests
{
    private static readonly string[] Five = { "low", "medium", "high", "xhigh", "max" };

    private static (Window Window, EffortDial Dial, List<int> Commits) Host(int selected = 1, bool enabled = true)
    {
        var dial = new EffortDial { Levels = Five, SelectedIndex = selected, IsEnabled = enabled };
        var window = new Window { Width = 200, Height = 200, Content = dial };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var commits = new List<int>();
        dial.Committed += (_, i) => commits.Add(i);
        return (window, dial, commits);
    }

    // A point at an angle on the dial's arc, in the window's coordinates.
    private static Point At(Window window, EffortDial dial, double angle)
    {
        var p = EffortDialMath.PointAt(dial.Centre, dial.Radius, angle);
        return dial.TranslatePoint(p, window)!.Value;
    }

    private static void Key(EffortDial dial, Key key)
    {
        dial.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = dial });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ClickingATickCommitsThatLevelOnRelease()
    {
        var (w, dial, commits) = Host();
        var tick = At(w, dial, EffortDialMath.AngleFor(3, 5)!.Value);

        w.MouseDown(tick, MouseButton.Left, RawInputModifiers.None);
        Assert.Empty(commits);
        Assert.Equal(3, dial.PreviewIndex);

        w.MouseUp(tick, MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { 3 }, commits);
        Assert.Equal(3, dial.SelectedIndex);
        Assert.Equal(-1, dial.PreviewIndex);
    }

    [AvaloniaFact]
    public void ADragIsOneCommitAtWhereItEnds()
    {
        var (w, dial, commits) = Host(selected: 0);

        w.MouseDown(At(w, dial, -135), MouseButton.Left, RawInputModifiers.None);
        w.MouseMove(At(w, dial, 0), RawInputModifiers.LeftMouseButton);
        Assert.Equal(2, dial.PreviewIndex);
        Assert.Equal("high", dial.ShownLevel);
        w.MouseMove(At(w, dial, 70), RawInputModifiers.LeftMouseButton);
        w.MouseUp(At(w, dial, 70), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { 3 }, commits);
    }

    [AvaloniaFact]
    public void ADragAcrossTheGapClampsInsteadOfWrapping()
    {
        var (w, dial, commits) = Host(selected: 3);

        w.MouseDown(At(w, dial, 135), MouseButton.Left, RawInputModifiers.None);
        w.MouseMove(At(w, dial, 175), RawInputModifiers.LeftMouseButton);
        w.MouseUp(At(w, dial, 175), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { 4 }, commits);
    }

    // nadia's review: a drag from the high end down through bottom centre used
    // to flip to the lowest level at 180 degrees.
    [AvaloniaFact]
    public void ADragPastBottomCentreStaysHighest()
    {
        var (w, dial, commits) = Host(selected: 2);

        w.MouseDown(At(w, dial, 100), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(3, dial.PreviewIndex);
        w.MouseMove(At(w, dial, 150), RawInputModifiers.LeftMouseButton);
        Assert.Equal(4, dial.PreviewIndex);
        w.MouseMove(At(w, dial, 179), RawInputModifiers.LeftMouseButton);
        w.MouseMove(At(w, dial, -170), RawInputModifiers.LeftMouseButton);
        Assert.Equal(4, dial.PreviewIndex);
        w.MouseUp(At(w, dial, -170), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(new[] { 4 }, commits);
    }

    [AvaloniaFact]
    public void ReleasingOnTheLevelItStartedAtCommitsNothing()
    {
        var (w, dial, commits) = Host(selected: 2);

        w.MouseDown(At(w, dial, 0), MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(At(w, dial, 0), MouseButton.Left, RawInputModifiers.None);

        Assert.Empty(commits);
    }

    [AvaloniaFact]
    public void AMoveWithoutAPressDoesNothing()
    {
        var (w, dial, commits) = Host();

        w.MouseMove(At(w, dial, 90), RawInputModifiers.None);
        w.MouseUp(At(w, dial, 90), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(-1, dial.PreviewIndex);
        Assert.Empty(commits);
    }

    [AvaloniaFact]
    public void ARightClickIsNotAGesture()
    {
        var (w, dial, commits) = Host();

        w.MouseDown(At(w, dial, 90), MouseButton.Right, RawInputModifiers.None);
        w.MouseUp(At(w, dial, 90), MouseButton.Right, RawInputModifiers.None);

        Assert.Empty(commits);
    }

    [AvaloniaFact]
    public void LosingTheCaptureMidDragCommitsNothing()
    {
        var (w, dial, commits) = Host();
        var tick = At(w, dial, EffortDialMath.AngleFor(3, 5)!.Value);

        w.MouseDown(tick, MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(3, dial.PreviewIndex);
        // Something else takes the pointer away: the dial leaves the tree.
        w.Content = new Border();
        Dispatcher.UIThread.RunJobs();
        w.MouseUp(tick, MouseButton.Left, RawInputModifiers.None);

        Assert.Empty(commits);
        Assert.Equal(-1, dial.PreviewIndex);
    }

    [AvaloniaFact]
    public void ADisabledDialIgnoresPointerAndKeys()
    {
        var (w, dial, commits) = Host(enabled: false);

        w.MouseDown(At(w, dial, 90), MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(At(w, dial, 90), MouseButton.Left, RawInputModifiers.None);
        Key(dial, Avalonia.Input.Key.Right);
        dial.Choose(4);

        Assert.Empty(commits);
        Assert.Equal(1, dial.SelectedIndex);
    }

    [AvaloniaFact]
    public void AnEmptyDialIgnoresInput()
    {
        var dial = new EffortDial();
        var w = new Window { Width = 200, Height = 200, Content = dial };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var commits = new List<int>();
        dial.Committed += (_, i) => commits.Add(i);

        w.MouseDown(new Point(100, 30), MouseButton.Left, RawInputModifiers.None);
        Key(dial, Avalonia.Input.Key.Right);

        Assert.Empty(commits);
        Assert.Null(dial.ShownLevel);
    }

    [AvaloniaTheory]
    [InlineData(Avalonia.Input.Key.Right, 2)]
    [InlineData(Avalonia.Input.Key.Up, 2)]
    [InlineData(Avalonia.Input.Key.Left, 0)]
    [InlineData(Avalonia.Input.Key.Down, 0)]
    [InlineData(Avalonia.Input.Key.Home, 0)]
    [InlineData(Avalonia.Input.Key.End, 4)]
    public void EachKeyMovesThenEnterCommits(Key key, int expected)
    {
        var (_, dial, commits) = Host(selected: 1);

        Key(dial, key);
        Assert.Equal(expected, dial.PreviewIndex);
        Assert.Empty(commits);

        Key(dial, Avalonia.Input.Key.Enter);

        Assert.Equal(new[] { expected }, commits);
    }

    [AvaloniaFact]
    public void SpaceCommitsAndEscapeAbandons()
    {
        var (_, dial, commits) = Host(selected: 1);

        Key(dial, Avalonia.Input.Key.Right);
        Key(dial, Avalonia.Input.Key.Escape);
        Assert.Equal(-1, dial.PreviewIndex);
        Key(dial, Avalonia.Input.Key.Enter);
        Assert.Empty(commits);

        Key(dial, Avalonia.Input.Key.Right);
        Key(dial, Avalonia.Input.Key.Right);
        Key(dial, Avalonia.Input.Key.Space);
        Assert.Equal(new[] { 3 }, commits);
    }

    [AvaloniaFact]
    public void OtherKeysAndAnIdleEscapeAreLeftAlone()
    {
        var (_, dial, commits) = Host();
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.A, Source = dial };

        dial.RaiseEvent(e);
        Assert.False(e.Handled);
        var esc = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape, Source = dial };
        dial.RaiseEvent(esc);
        Assert.False(esc.Handled);
        Assert.Empty(commits);
    }

    [AvaloniaFact]
    public void LeavingTheDialCommitsWhatTheKeysMovedTo()
    {
        var other = new Button();
        var dial = new EffortDial { Levels = Five, SelectedIndex = 1 };
        var w = new Window { Width = 200, Height = 300, Content = new StackPanel { Children = { dial, other } } };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var commits = new List<int>();
        dial.Committed += (_, i) => commits.Add(i);

        dial.Focus();
        Key(dial, Avalonia.Input.Key.End);
        other.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { 4 }, commits);
    }

    [AvaloniaFact]
    public void ItIsOneTabStop()
    {
        Assert.True(new EffortDial().Focusable);
    }

    [AvaloniaFact]
    public void ChooseOutOfRangeDoesNothing()
    {
        var (_, dial, commits) = Host();

        dial.Choose(-1);
        dial.Choose(5);

        Assert.Empty(commits);
    }

    private static void Draw(Control c)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(200, 200));
        bitmap.Render(c);
    }

    [AvaloniaFact]
    public void PendingAndFocusedStatesDrawWithoutThrowing()
    {
        var (w, dial, _) = Host(selected: 3);
        dial.ConfirmedIndex = 1;
        dial.IsPending = true;
        dial.Focus();
        Dispatcher.UIThread.RunJobs();

        // The null renderer still walks Render; a capture is in UiScreenshots.
        Draw(dial);
        Assert.Equal("xhigh", dial.ShownLevel);

        dial.Width = 10;
        dial.Height = 10;
        Dispatcher.UIThread.RunJobs();
        Draw(dial);
        Assert.Equal(0, dial.Radius);
        w.Close();
    }

    [AvaloniaFact]
    public void TheAutomationPeerNamesTheLevel()
    {
        var (_, dial, commits) = Host(selected: 2);
        var peer = ControlAutomationPeer.CreatePeerForElement(dial);

        var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer);
        var value = Assert.IsAssignableFrom<IValueProvider>(peer);
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        Assert.Equal(nameof(EffortDial), peer.GetClassName());
        Assert.Equal("high", value.Value);
        Assert.Equal(2, range.Value);
        Assert.Equal((0, 4, 1, 1), (range.Minimum, range.Maximum, range.SmallChange, range.LargeChange));
        Assert.False(range.IsReadOnly);

        range.SetValue(4);
        value.SetValue("LOW");
        value.SetValue("nonsense");

        Assert.Equal(new[] { 4, 0 }, commits);

        dial.SelectedIndex = -1;
        Assert.Null(value.Value);
        dial.IsEnabled = false;
        Assert.True(value.IsReadOnly);
    }
}
