using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// The rows as the window builds them, rather than as a test wires them.
//
// SettingsWindowPickerTests drives ClickPicker with getters and setters of its
// own, which checks the picker. This file drives the rows the window actually
// creates, which is the only way the production lambdas run — and those lambdas
// are where the interesting mistake lives: three rows sharing one builder, each
// handed a different setting, is exactly the shape a copy-paste error hides in.
// Both switches still move on screen when two rows write the same setting.
//
// Buttons are never clicked. The gateway section holds one that trusts a new
// certificate and reconnects, and the host box reconnects when it loses focus —
// so that box is built and read but never given a focus change.
[Collection("Settings")]
public class SettingsWindowRowBuilderTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static List<ComboBox> CombosIn(IEnumerable<Control> rows) =>
        rows.SelectMany(row => row.GetLogicalDescendants().OfType<ComboBox>().Prepend(row as ComboBox))
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct()
            .ToList();

    private static int CountOf(ComboBox combo) => ((IList)combo.ItemsSource!).Count;

    // --- the three click rows, as the window builds them ---

    // One row per gesture, in the order the window lays them out, and each one
    // writes its own setting. The assertion that matters is the pair that did
    // *not* change: a row bound to the wrong getter/setter pair would still look
    // right on screen and would still move.
    [AvaloniaFact]
    public void EachClickRowWritesOnlyItsOwnSetting()
    {
        OrbweaverSettings.ClickAction = "none";
        OrbweaverSettings.DoubleClickAction = "none";
        OrbweaverSettings.TripleClickAction = "none";

        var window = NewWindow();
        var combos = CombosIn(window.ClickRows());

        Assert.Equal(3, combos.Count);

        // Index 0 is "Go to the session", which none of them starts on.
        combos[0].SelectedIndex = 0;
        Assert.Equal("terminal", OrbweaverSettings.ClickAction);
        Assert.Equal("none", OrbweaverSettings.DoubleClickAction);
        Assert.Equal("none", OrbweaverSettings.TripleClickAction);

        combos[1].SelectedIndex = 1;
        Assert.Equal("terminal", OrbweaverSettings.ClickAction);
        Assert.Equal("chat", OrbweaverSettings.DoubleClickAction);
        Assert.Equal("none", OrbweaverSettings.TripleClickAction);

        combos[2].SelectedIndex = 2;
        Assert.Equal("terminal", OrbweaverSettings.ClickAction);
        Assert.Equal("chat", OrbweaverSettings.DoubleClickAction);
        Assert.Equal("speak", OrbweaverSettings.TripleClickAction);
    }

    // Each row opens on its own saved value, which is the other half of the same
    // wiring: three rows reading one setting would all show the same thing.
    [AvaloniaFact]
    public void EachClickRowOpensOnItsOwnSavedValue()
    {
        OrbweaverSettings.ClickAction = "terminal";
        OrbweaverSettings.DoubleClickAction = "chat";
        OrbweaverSettings.TripleClickAction = "speak";

        var window = NewWindow();
        var combos = CombosIn(window.ClickRows());

        Assert.Equal(0, combos[0].SelectedIndex);
        Assert.Equal(1, combos[1].SelectedIndex);
        Assert.Equal(2, combos[2].SelectedIndex);
    }

    // --- how long an orb stays ---

    [AvaloniaFact]
    public void ChoosingALifetimeWritesIt()
    {
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 1;
            var window = NewWindow();

            var combo = (ComboBox)window.LifetimePicker();
            combo.SelectedIndex = 2;   // "15 minutes"

            Assert.Equal(15, OrbweaverSettings.OrbLifetimeMinutes);
        }
        finally { OrbweaverSettings.OrbLifetimeMinutes = before; }
    }

    // "Forever" is a real choice rather than a very large number, and it is the
    // last one on the list.
    [AvaloniaFact]
    public void ForeverIsTheLastChoiceAndMeansForever()
    {
        // Restored: "forever" left behind makes every later test that expects an
        // orb to expire keep it instead (CB-321 found SessionScanTests'
        // AWaitingSessionOutlivesKeepOrbsFor failing on exactly this).
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 1;
            var window = NewWindow();

            var combo = (ComboBox)window.LifetimePicker();
            combo.SelectedIndex = CountOf(combo) - 1;

            Assert.Equal(OrbweaverSettings.OrbLifetimeForever, OrbweaverSettings.OrbLifetimeMinutes);
        }
        finally { OrbweaverSettings.OrbLifetimeMinutes = before; }
    }

    // A number hand-written into settings.json shows as itself rather than being
    // silently rounded to whatever is on the list — opening this window must not
    // quietly change a setting the user typed.
    [AvaloniaFact]
    public void AHandWrittenLifetimeIsOfferedAsItself()
    {
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 7;
            var window = NewWindow();

            var combo = (ComboBox)window.LifetimePicker();

            Assert.True(combo.SelectedIndex >= 0, "a hand-written value must still be selected");
            Assert.Equal(7, OrbweaverSettings.OrbLifetimeMinutes);

            var labels = ((IList)combo.ItemsSource!).Cast<string>().ToList();
            Assert.Contains("7 minutes", labels);

            // Inserted before "Forever" rather than appended after it, so the list
            // still reads in order.
            Assert.True(
                labels.IndexOf("7 minutes") < labels.Count - 1,
                "a hand-written value belongs before Forever, not after it");
    
        }
        finally { OrbweaverSettings.OrbLifetimeMinutes = before; }
    }

    // --- which gateway sessions get an orb ---

    [AvaloniaFact]
    public void ChoosingAnActivityWindowWritesIt()
    {
        OrbweaverSettings.OpenClawActiveWithinMinutes = 60;
        var window = NewWindow();

        var combo = (ComboBox)window.ActiveWithinPicker();
        var before = OrbweaverSettings.OpenClawActiveWithinMinutes;

        // Pick something other than whatever it opened on, so the handler has a
        // change to make.
        combo.SelectedIndex = combo.SelectedIndex == 0 ? 1 : 0;

        Assert.NotEqual(before, OrbweaverSettings.OpenClawActiveWithinMinutes);
    }

    [AvaloniaFact]
    public void AHandWrittenActivityWindowIsOfferedAsItself()
    {
        OrbweaverSettings.OpenClawActiveWithinMinutes = 13;
        var window = NewWindow();

        var combo = (ComboBox)window.ActiveWithinPicker();

        Assert.True(combo.SelectedIndex >= 0);
        Assert.Contains("13 minutes", ((IList)combo.ItemsSource!).Cast<string>());
    }

    // Re-selecting what is already chosen writes nothing. Worth asserting because
    // the handler checks for it explicitly, and because every settings write hits
    // the disk.
    [AvaloniaFact]
    public void ReselectingTheSameActivityWindowIsANoOp()
    {
        OrbweaverSettings.OpenClawActiveWithinMinutes = 60;
        var window = NewWindow();

        var combo = (ComboBox)window.ActiveWithinPicker();
        var opened = combo.SelectedIndex;

        combo.SelectedIndex = opened;

        Assert.Equal(60, OrbweaverSettings.OpenClawActiveWithinMinutes);
    }

    // --- the gateway address ---

    // Built and read, never focused. Its handler reconnects the socket and clears
    // the pinned certificate, so raising a focus change here would open a real
    // connection — what is testable without that is that the box opens on the
    // saved address, which is what stops a user's gateway looking unconfigured
    // every time they open settings.
    [AvaloniaFact]
    public void TheGatewayBoxOpensOnTheSavedAddress()
    {
        OrbweaverSettings.OpenClawHost = "198.51.100.42";
        var window = NewWindow();

        var box = (TextBox)window.GatewayHostBox();

        Assert.Equal("198.51.100.42", box.Text);
    }

    [AvaloniaFact]
    public void TheGatewayBoxShowsAWatermarkWhenThereIsNoAddress()
    {
        OrbweaverSettings.OpenClawHost = "";
        var window = NewWindow();

        var box = (TextBox)window.GatewayHostBox();

        Assert.True(string.IsNullOrEmpty(box.Text));
        Assert.False(string.IsNullOrWhiteSpace(box.Watermark),
            "an empty address needs an example, or the field says nothing about what goes in it");
    }
}
