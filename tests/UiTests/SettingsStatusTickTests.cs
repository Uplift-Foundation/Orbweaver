using System;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// The two live status lines in the settings window, and the one timer that feeds
// them both.
//
// These exist because what they describe changes while you are looking at it: a
// relay takes a few seconds to start and stops itself when idle, and a gateway
// connects, gets refused, or waits to be approved. A line that was only true when
// the window opened would be worse than no line, because it would look current.
//
// The tick is driven directly. StartStatusTicker is excluded — it starts a real
// one-second Avalonia timer — and waiting on that timer is exactly the mistake
// this branch has now fixed five times.
[Collection("Settings")]
public class SettingsStatusTickTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    // Both labels are created only PAST their section's enabled-check, so the
    // feature has to be switched on before the rows are built, not after.
    private static SettingsWindow WithGateway()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.OpenClawEnabled = true;

        var window = NewWindow();
        _ = window.OpenClawRows();
        return window;
    }

    // Each label is null unless its own section has been built, which is the
    // point of the single timer: one ticker serving at most two labels rather
    // than a timer each.
    [AvaloniaFact]
    public void TickingWithNeitherSectionBuiltDoesNothing()
    {
        // Both switched OFF first, explicitly. The constructor calls Rebuild(),
        // so a section left enabled by an earlier test builds its label before
        // this test gets a look in — which is how this assertion failed the first
        // time I ran it, and is the same order-dependence this branch has spent
        // five commits removing.
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.OpenClawEnabled = false;
        OrbweaverSettings.RemoteControlEnabled = false;

        var window = NewWindow();

        window.OnStatusTick(null, EventArgs.Empty);

        Assert.Null(window.OpenClawStatusText);
    }

    [AvaloniaFact]
    public void TheGatewayLineIsFilledOnceItsSectionExists()
    {
        var window = WithGateway();

        window.OnStatusTick(null, EventArgs.Empty);

        Assert.Equal(OpenClawSessions.StatusText, window.OpenClawStatusText);
        Assert.False(string.IsNullOrWhiteSpace(window.OpenClawStatusText));
    }

    // Ticking again with nothing changed leaves the same text. The guard is an
    // `if (label.Text != value)` rather than an unconditional assignment, which
    // matters at one tick a second for as long as the window is open: assigning
    // Text invalidates layout even when the string is identical.
    [AvaloniaFact]
    public void TickingRepeatedlyWithNothingChangedIsStable()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.PeerLinkEnabled = true;

        var window = NewWindow();
        _ = window.OpenClawRows();
        _ = window.PeerLinkRows();

        window.OnStatusTick(null, EventArgs.Empty);
        var gateway = window.OpenClawStatusText;
        var link = window.PeerLinkStatusText;

        for (var i = 0; i < 5; i++) window.OnStatusTick(null, EventArgs.Empty);

        Assert.Equal(gateway, window.OpenClawStatusText);
        Assert.Equal(link, window.PeerLinkStatusText);
    }

    // Both sections built, both lines fed by the one tick.
    //
    // The second line used to be the relay's and is now the link's — same
    // ticker, same slot, and the reason for one timer rather than two is
    // unchanged: each label is null unless its own section has been built.
    [AvaloniaFact]
    public void OneTickServesBothLines()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.PeerLinkEnabled = true;

        var window = NewWindow();
        _ = window.OpenClawRows();
        _ = window.PeerLinkRows();

        window.OnStatusTick(null, EventArgs.Empty);

        Assert.NotNull(window.OpenClawStatusText);
        Assert.NotNull(window.PeerLinkStatusText);
    }

    // The sender and args are ignored — it is wired as a DispatcherTimer.Tick
    // handler but reads only the fields, which is what makes driving it directly
    // legitimate rather than a trick.
    [AvaloniaFact]
    public void TheTickIgnoresItsSenderAndArgs()
    {
        var window = WithGateway();

        window.OnStatusTick(null, EventArgs.Empty);
        var first = window.OpenClawStatusText;

        window.OnStatusTick(window, new EventArgs());

        Assert.Equal(first, window.OpenClawStatusText);
    }

    // CB-300: a live session under a Claude Code folder the link was never told
    // to read is otherwise an empty roster on the far machine with nothing to
    // say why. The line says so, and drops it again once there is nothing left.
    [AvaloniaFact]
    public void TheLinkLineNamesAFolderWhoseSessionsAreNotOffered()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.PeerLinkEnabled = true;

        try
        {
            var window = NewWindow();
            _ = window.PeerLinkRows();

            // The control: a gap that is normal (asked) or not the user's to
            // fix leaves the line exactly as it would be with no gap at all.
            PeerSessions.UseRosterGapsForTests(new[]
            {
                new RemoteMirrorServer.RosterGap("~/.claude-board", 5, Asked: true, Actionable: false),
                new RemoteMirrorServer.RosterGap("(outside home)", 2, Asked: false, Actionable: false)
            });
            window.OnStatusTick(null, EventArgs.Empty);
            var plain = PeerSessions.StatusText(true, true, 0, 0, 0);
            Assert.Equal(plain, window.PeerLinkStatusText);

            PeerSessions.UseRosterGapsForTests(new[]
            {
                new RemoteMirrorServer.RosterGap("~/.claude-loginid", 23, Asked: false, Actionable: true)
            });
            window.OnStatusTick(null, EventArgs.Empty);

            Assert.StartsWith(plain, window.PeerLinkStatusText);
            Assert.Contains("23 sessions under ~/.claude-loginid", window.PeerLinkStatusText);

            // Cleared, the sentence is gone.
            PeerSessions.UseRosterGapsForTests(Array.Empty<RemoteMirrorServer.RosterGap>());
            window.OnStatusTick(null, EventArgs.Empty);
            Assert.Equal(plain, window.PeerLinkStatusText);
        }
        finally
        {
            PeerSessions.UseRosterGapsForTests(null);
        }
    }
}
