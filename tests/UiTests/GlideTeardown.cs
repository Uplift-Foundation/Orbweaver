using System.Reflection;
using Avalonia.Threading;

namespace Orbweaver.UiTests;

// Stops a SessionManager's arrangement glide when the test that made the
// manager is done with it (CB-490).
//
// The glide is driven by a real 60 Hz DispatcherTimer that stops itself when
// the manager's own clock says 600 ms have passed. A test that steps that
// clock by hand and ends mid-glide therefore leaves a timer that never stops:
// the clock is frozen, so the glide is never over. Under PerAssembly isolation
// the dispatcher is shared, so that timer goes on ticking inside every later
// test's RunJobs, and each tick refreshes the overlays, which are static.
// TeamLinks.Refresh keeps only team members in the thread tug, so a test that
// had put a lone orb there found it forgotten whenever a tick came due in one
// of its flushes. That was ChatPanelThreadTugTests on the Windows runner, with
// the timer left by ArrangementMotionTests.
//
// Stopped, not landed: landing runs the completion and can start a re-fit
// glide of its own, for orbs nobody is going to look at again.
internal static class GlideTeardown
{
    private static DispatcherTimer? Timer(SessionManager manager) =>
        (DispatcherTimer?)typeof(SessionManager)
            .GetField("_arrangeAnimTimer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager);

    internal static bool TimerRunning(SessionManager manager) => Timer(manager) is { IsEnabled: true };

    internal static void Stop(IEnumerable<SessionManager> managers)
    {
        foreach (var manager in managers) Timer(manager)?.Stop();
    }
}
