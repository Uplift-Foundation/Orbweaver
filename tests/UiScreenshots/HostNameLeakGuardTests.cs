using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Sdk;

namespace Orbweaver.Tests;

// CB-374: ScreenshotHelper.Check fails any capture that shows this machine's name.
// A guard that nothing ever trips reads exactly like one that works, so these
// show it trips - on every spelling the app derives from the host - and that the
// suite-wide pin is what keeps the real captures clear of it.
public class HostNameLeakGuardTests
{
    [AvaloniaFact]
    public void ACaptureShowingTheHostNameIsRefused()
    {
        foreach (var spelling in ScreenshotHelper.HostNameSpellings())
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "job-hunter" });
            panel.Children.Add(new TextBlock { Text = spelling });

            var refusal = Assert.Throws<XunitException>(
                () => ScreenshotHelper.AssertNoHostNameIsShown(panel, "leak.png"));

            Assert.Contains(spelling, refusal.Message);
        }
    }

    [AvaloniaFact]
    public void ACaptureShowingOnlyTheFixtureNameIsAccepted()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = MachineNames.Readable(ChatPanelTestAccess.SuiteMachineName) });

        ScreenshotHelper.AssertNoHostNameIsShown(panel, "clean.png");
    }

    [AvaloniaFact]
    public void TheSuitePinIsInPlaceBeforeAnyPanelAsksForTheHost()
    {
        // Read straight off the field ChatPanel caches the host name in, so this
        // does not depend on any panel being open. Every WithMachineName in the
        // suite puts back what it found, which is the pin.
        var held = (string?)typeof(ChatPanel)
            .GetField("_thisMachine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);

        Assert.Equal(ChatPanelTestAccess.SuiteMachineName, held);
    }
}
