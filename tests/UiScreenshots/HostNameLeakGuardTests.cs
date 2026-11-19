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
        // On a host whose name is under four characters the guard has nothing to
        // look for, so every capture passes unchecked and this would loop zero
        // times and pass having tested nothing. Say so instead.
        Assert.SkipWhen(ScreenshotHelper.HostNameSpellings().Count == 0,
            "this host's name is under four characters, so the host-name guard is off here");

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

    // The kinds of text beyond a plain TextBlock that it does read.
    [AvaloniaFact]
    public void AHostNameInAnInlineRunOrATextBoxIsRefusedToo()
    {
        Assert.SkipWhen(ScreenshotHelper.HostNameSpellings().Count == 0,
            "this host's name is under four characters, so the host-name guard is off here");

        var host = ScreenshotHelper.HostNameSpellings()[0];

        var inline = new TextBlock();
        inline.Inlines!.Add(new Avalonia.Controls.Documents.Run(host));
        Assert.Throws<XunitException>(() => ScreenshotHelper.AssertNoHostNameIsShown(inline, "inline.png"));

        var box = new TextBox { Text = host };
        Assert.Throws<XunitException>(() => ScreenshotHelper.AssertNoHostNameIsShown(box, "box.png"));
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

    // The end of it, through a real panel: a freshly built panel on a local
    // session - one with no far machine, which is what every capture in the suite
    // except the mirrored ones is - names the fixture machine in its header, not
    // the host. The guard above catches a capture that leaks; this says why none
    // does.
    [AvaloniaFact]
    public void AFreshLocalPanelsHeaderNamesTheFixtureMachine()
    {
        var id = "host-leak-guard-" + Guid.NewGuid();
        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "hello" },
        })
        { SessionId = id, DisplayName = "Leota" };

        ChatPanel.OpenFor(new OrbWindow(id), fake);
        ScreenshotHelper.Flush();

        try
        {
            var panel = ChatPanelTestAccess.Instance!;
            var machine = panel.FindControl<TextBlock>("MetaMachineText")!.Text ?? "";

            Assert.Equal(MachineNames.Readable(ChatPanelTestAccess.SuiteMachineName), machine);
            Assert.DoesNotContain(
                ScreenshotHelper.HostNameSpellings(),
                host => machine.Contains(host, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ChatPanel.HideFor(id);
        }
    }
}
