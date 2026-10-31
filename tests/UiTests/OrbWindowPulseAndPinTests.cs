using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// The shared pulse tick, the pin, and the two click actions that were previously
// only reachable through something unsafe.
//
// The pulse ticker is process-wide and accumulates every orb ever shown for the
// life of the test process, because nothing closes a window to remove one. So it
// is driven directly rather than waited on — a test that waited passed in
// isolation and failed once the rest of the suite loaded the machine, even with a
// ten-second budget.
[Collection("Settings")]
public class OrbWindowPulseAndPinTests
{
    private static OrbWindow Orb(string id = "pulse-1")
    {
        var orb = new OrbWindow(id);
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = $"{Path.DirectorySeparatorChar}Users{Path.DirectorySeparatorChar}w"
                + $"{Path.DirectorySeparatorChar}Source{Path.DirectorySeparatorChar}Thing",
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });
        return orb;
    }

    // ---- the pulse ---------------------------------------------------------

    // With nothing pulsing the tick is a no-op and stops the timer. That arm is
    // the one that matters: without it the ticker spins at 30fps for the life of
    // the app after the last orb goes quiet.
    [AvaloniaFact]
    public void TickingWithNothingPulsingIsHarmless()
    {
        OrbWindow.TickAllPulses();
        OrbWindow.TickAllPulses();
    }

    // ---- the pin -----------------------------------------------------------

    // PinAt puts an orb where an earlier run left it, WITHOUT treating that as a
    // fresh drag — there is nothing to write back, since the position came from
    // settings in the first place.
    //
    // Worth having a direct test: arranging no longer pins anything (CB-268
    // retired that; see
    // OrbArrangementAnimationTests.ArrangingAndCompletingTheGlideLeavesEveryOrbUnpinned),
    // so PinAt is reached by a drag outside an arrangement and by
    // RestoreFromPattern putting a hand-placed orb back.
    [AvaloniaFact]
    public void PinningPutsTheOrbWhereItIsToldAndMarksItPinned()
    {
        var orb = Orb();

        orb.PinAt(new PixelPoint(321, 654));

        Assert.Equal(new PixelPoint(321, 654), orb.Position);
        Assert.True(orb.IsPinned);
    }

    [AvaloniaFact]
    public void UnpinningClearsThePinWithoutMovingTheOrb()
    {
        var orb = Orb();
        orb.PinAt(new PixelPoint(321, 654));

        orb.Unpin();

        Assert.False(orb.IsPinned);
        Assert.Equal(new PixelPoint(321, 654), orb.Position);
    }

    // ---- the click actions -------------------------------------------------

    // Both of these are safe to run precisely because of what is missing:
    // SessionManager.Instance is null, so the chat action finds no session and
    // returns; and this orb has no transcript, so the speak action finds nothing
    // to say and returns before reaching the speech engine. Running them proves
    // the switch dispatches, which is the part that can be wrong.
    [AvaloniaFact]
    public void TheChatActionIsHarmlessWithNoSessionManager()
    {
        OrbweaverSettings.ReloadForTests();
        var orb = Orb();

        orb.RunClickAction(ActionClicks("chat"));
    }

    [AvaloniaFact]
    public void TheSpeakActionIsHarmlessWithNothingToSay()
    {
        OrbweaverSettings.ReloadForTests();
        var orb = Orb();

        Assert.Null(orb.FindSpeakableText());

        orb.RunClickAction(ActionClicks("speak"));
    }

    // ---- the cwd fallback --------------------------------------------------

    // A session whose own transcript path is empty falls back to the newest
    // transcript for its working directory. That is how an orb can still speak
    // after a /clear, and it is reachable here only because FindSpeakableText
    // takes a home to search — otherwise it would depend on what happens to be in
    // the developer's real ~/.claude.
    [AvaloniaFact]
    public void WithNoTranscriptPathTheCwdFallbackIsUsed()
    {
        OrbweaverSettings.ReloadForTests();

        // Built from the platform's own separator, not written as a POSIX literal.
        // EncodeCwd replaces Path.DirectorySeparatorChar with '-', so on Windows a
        // "/Users/w/Source/Thing" literal keeps every slash — and Path.Combine then
        // turns the "encoded" name into a nest of directories rather than the one
        // directory the search looks for. That is what made this test pass on macOS
        // and fail on Windows, and it is the third time on this branch that
        // hardcoding what an OS-dependent function produces has asserted the
        // platform instead of the rule.
        var sep = Path.DirectorySeparatorChar;
        var cwd = $"{sep}Users{sep}w{sep}Source{sep}Thing";
        var home = Path.Combine(Path.GetTempPath(), "cb-orbhome-" + Guid.NewGuid());
        var project = Path.Combine(
            home, ".claude", "projects", TranscriptReader.EncodeCwd(cwd));
        Directory.CreateDirectory(project);

        File.WriteAllText(Path.Combine(project, "s.jsonl"),
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"from the fallback"}]}}"""
            + "\n");

        var orb = new OrbWindow("fallback-1");
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = cwd,
            TranscriptPath = null,
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });

        try
        {
            Assert.Equal("from the fallback", orb.FindSpeakableText(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // And a cwd with nothing under it returns null rather than reaching for
    // whatever else is in that home.
    [AvaloniaFact]
    public void ACwdWithNoTranscriptAnywhereSaysNothing()
    {
        OrbweaverSettings.ReloadForTests();

        var home = Path.Combine(Path.GetTempPath(), "cb-orbhome-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(home);

        var orb = new OrbWindow("fallback-2");
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = $"{Path.DirectorySeparatorChar}Users{Path.DirectorySeparatorChar}w"
                + $"{Path.DirectorySeparatorChar}Source{Path.DirectorySeparatorChar}Nothing",
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });

        try
        {
            Assert.Null(orb.FindSpeakableText(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // Finds however many clicks the settings currently map to the wanted action,
    // so these tests do not break when someone reorders the click preferences.
    private static int ActionClicks(string wanted)
    {
        for (var clicks = 1; clicks <= 3; clicks++)
        {
            if (OrbWindow.ActionFor(clicks) == wanted) return clicks;
        }

        // Nothing maps to it under the current settings; RunClickAction's "none"
        // arm is covered elsewhere, so a count that resolves to nothing is fine.
        return 1;
    }

    // The flyout's arrange button, which is safe for the same reason the tray's
    // is: SessionManager.Instance is null outside the running app, so this is a
    // no-op. A version that dereferenced instead of using ?. would throw here,
    // and nothing else would have caught it.
    [AvaloniaFact]
    public void ArrangingWithNoSessionManagerIsANoOp()
    {
        OrbWindow.ArrangeAllOrbs();
    }

    // A transcript that exists for the cwd but has nothing the assistant said
    // in it — a session cleared and then abandoned, which is exactly the shape
    // the fallback exists for and exactly the shape it cannot help with. The
    // answer is nothing, not the newest thing anybody said.
    [AvaloniaFact]
    public void AFallbackTranscriptWithNothingSaidInItStillSaysNothing()
    {
        OrbweaverSettings.ReloadForTests();

        var sep = Path.DirectorySeparatorChar;
        var cwd = $"{sep}Users{sep}w{sep}Source{sep}Quiet";
        var home = Path.Combine(Path.GetTempPath(), "cb-orbhome-quiet-" + Guid.NewGuid());
        var project = Path.Combine(
            home, ".claude", "projects", TranscriptReader.EncodeCwd(cwd));
        Directory.CreateDirectory(project);

        // A real row, and not an assistant one. The fallback finds this file —
        // which is the point, and what separates this case from
        // ACwdWithNoTranscriptAnywhereSaysNothing above — and then finds
        // nothing in it.
        File.WriteAllText(Path.Combine(project, "s.jsonl"),
            """{"type":"user","message":{"role":"user","content":"anyone there?"}}"""
            + "\n");

        var orb = new OrbWindow("fallback-3");
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = cwd,
            TranscriptPath = null,
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
        });

        try
        {
            Assert.Null(orb.FindSpeakableText(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ---- the guard in front of Quit ----------------------------------------

    // Both Quit paths in this app — the tray item and the orb's own menu — ask
    // the same question before ending the process, and under this suite's
    // headless lifetime the answer is no. That is what makes calling either of
    // them from a test harmless, so it is asserted rather than relied on.
    [AvaloniaFact]
    public void AHeadlessHostIsNotADesktopAppAndIsNotQuit()
    {
        Assert.False(OrbWindow.IsDesktopLifetime(Application.Current?.ApplicationLifetime));

        // Which is why these are safe to call at all. Neither should do
        // anything, and neither should throw.
        Orb().Exit_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        TrayController.QuitApp();
    }

    [AvaloniaFact]
    public void NoLifetimeAtAllIsNotADesktopAppEither()
    {
        Assert.False(OrbWindow.IsDesktopLifetime(null));
    }
}
