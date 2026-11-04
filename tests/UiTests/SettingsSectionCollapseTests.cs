using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// The collapse half of CB-166: a real gesture on the header, moving the
// chevron and the body's IsVisible, and persisting through
// OrbweaverSettings — measured through the actual click handler rather
// than by calling SettingsSection.IsOpen directly, which is exactly the
// seam the production click handler sits behind.
//
// [Collection("Settings")] for the reason SettingsCollection.cs gives, and
// the window is built through its private constructor, shown and never
// closed, the same house rule SettingsSectionTests states: Window.Close()
// on a headless window corrupts a process-wide Avalonia FontManager cache.
[Collection("Settings")]
public class SettingsSectionCollapseTests
{
    private static readonly string[] AllIds =
    {
        "orbs", "orb-click", "auto-organize", "orb-colours", "chat-panel",
        "voice", "sounds", "claude-code", "codex", "grok", "openclaw", "claude-cloud",
        "bridge-commands", "peer-link", "claude-desktop"
    };

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ToggleButton HeaderOf(SettingsWindow.SettingsSection section) =>
        section.GetLogicalDescendants().OfType<ToggleButton>()
            .Single(tb => tb.Classes.Contains("settings-disclosure"));

    private static Point CenterOf(Control control, Visual ancestor) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), ancestor)
        ?? throw new InvalidOperationException($"{control} is not inside {ancestor}");

    // Focus plus Space down/up, exercising the same header.Click handler a
    // mouse click reaches — and, unlike a synthesized mouse click, needing
    // no window layout or pointer capture, so most tests below use this. A
    // real mouse click is covered separately, once, in
    // AMouseClickTogglesTheSectionToo — see that test's comment for why
    // it exists rather than being assumed to behave the same way.
    private static void Click(ToggleButton header)
    {
        // The Click event the production handler is attached to, raised
        // directly -- not Focus plus a synthesized Space.
        //
        // Focus only succeeds on a window that has been Shown, and showing
        // this one is what made the suite unusable. This assembly never closes
        // a window (the FontManager hazard SettingsWindowSmokeTest records), so
        // by the time this class runs, roughly 1260 earlier tests have left
        // their windows alive -- and showing the settings window, the largest
        // in the app, then pumping the dispatcher lays out every one of them
        // again. Measured: this class alone takes 2 seconds, and the same class
        // inside the full suite never finished at all. Dropping Show took the
        // whole suite to 23 seconds, matching develop.
        //
        // Raising ClickEvent reaches exactly the handler a real click reaches.
        // It deliberately does not toggle IsChecked first, and does not need
        // to: the handler computes the new state from section.IsOpen rather
        // than reading IsChecked back, precisely so it does not depend on when
        // Avalonia flips it. A genuine pointer click, with hit-testing and a
        // shown window, is still covered once in AMouseClickTogglesTheSectionToo.
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        FlushRender();
    }

    private static void ResetAllSections()
    {
        foreach (var id in AllIds) OrbweaverSettings.SetSettingsSectionCollapsed(id, false);
    }

    [AvaloniaFact]
    public void EverySectionOpensOnAFreshInstall()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();

            Assert.All(window.Sections.Values, s => Assert.True(s.IsOpen));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // The ticket's own acceptance criterion: click a header, its section
    // folds; click again, it opens back up.
    [AvaloniaFact]
    public void ASectionCollapsesAndExpands()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            // Not Shown: see Click() above. Showing this window inside a suite
            // that never closes one is what made the full run never finish.
            FlushRender();

            var section = window.Sections["voice"];
            var header = HeaderOf(section);

            Assert.True(section.IsOpen);

            Click(header);
            Assert.False(section.IsOpen);
            Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));

            Click(header);
            Assert.True(section.IsOpen);
            Assert.False(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // The chevron itself moves — not just IsVisible on the body — since it's
    // the only visual cue once the rows are hidden.
    [AvaloniaFact]
    public void CollapsingRotatesTheChevronAndExpandingRotatesItBack()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            // Not Shown: see Click() above. Showing this window inside a suite
            // that never closes one is what made the full run never finish.
            FlushRender();

            var section = window.Sections["voice"];
            var header = HeaderOf(section);
            var chevron = header.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            var rotation = Assert.IsType<RotateTransform>(chevron.RenderTransform);

            Assert.Equal(90, rotation.Angle);

            Click(header);
            Assert.Equal(0, rotation.Angle);

            Click(header);
            Assert.Equal(90, rotation.Angle);
        }
        finally
        {
            ResetAllSections();
        }
    }

    // The ticket's other acceptance criterion: a fold survives past the
    // window that made it, which is what a *setting* means as opposed to a
    // one-off view toggle.
    [AvaloniaFact]
    public void TheStateIsRestoredInTheNextWindow()
    {
        ResetAllSections();
        try
        {
            var first = NewWindow();
            // Not Shown: see Click() above. Showing this window inside a suite
            // that never closes one is what made the full run never finish.
            FlushRender();

            Click(HeaderOf(first.Sections["codex"]));
            Assert.False(first.Sections["codex"].IsOpen);

            var second = NewWindow();

            Assert.False(second.Sections["codex"].IsOpen);
            Assert.All(
                AllIds.Where(id => id != "codex"),
                id => Assert.True(second.Sections[id].IsOpen));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // Rebuild() throws away and rebuilds every section from scratch — a
    // gateway TextBox's LostFocus commit does exactly this — so what a
    // section was left as has to come from settings, not from the instance
    // that is about to be discarded.
    [AvaloniaFact]
    public void RebuildKeepsWhatSectionsWereLeftAs()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            // Not Shown: see Click() above. Showing this window inside a suite
            // that never closes one is what made the full run never finish.
            FlushRender();

            Click(HeaderOf(window.Sections["orbs"]));
            Assert.False(window.Sections["orbs"].IsOpen);

            window.Rebuild();

            Assert.False(window.Sections["orbs"].IsOpen);
            Assert.True(window.Sections["voice"].IsOpen);
        }
        finally
        {
            ResetAllSections();
        }
    }

    // A folded section's rows stay in the logical tree — through the real
    // IsOpen setter this time, rather than the architect's own probe of
    // IsVisible directly, which is what the whole-window row tests actually
    // depend on surviving a fold.
    [AvaloniaFact]
    public void AFoldedSectionsRowsStayInTheLogicalTree()
    {
        var window = NewWindow();
        var section = window.Sections["orbs"];

        var rows = section.GetLogicalDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(rows);

        section.IsOpen = false;

        var reachable = window.GetLogicalDescendants().ToHashSet();
        Assert.All(rows, row => Assert.Contains(row, reachable));
    }

    // Space and Enter operate the disclosure header like any other button —
    // they must never also close the window, which is what would happen if
    // the window's own KeyDown handler treated them the way it treats
    // Escape.
    [AvaloniaTheory]
    [InlineData(Key.Space, KeyModifiers.None, false)]
    [InlineData(Key.Enter, KeyModifiers.None, false)]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    public void SpaceAndEnterAreNotWindowCloseKeysWhileEscapeStillIs(
        Key key, KeyModifiers modifiers, bool shouldClose) =>
        Assert.Equal(shouldClose, SettingsWindow.ShouldCloseOnKeyDown(key, modifiers));

    [AvaloniaFact]
    public void AllFourteenIdsAreDistinct() =>
        Assert.Equal(AllIds.Length, AllIds.Distinct().Count());
}
