using Xunit;

namespace Orbweaver.Tests;

// The order the process starts in (CB-28, extended by CB-178).
//
// This is a test about a sequence of calls, which normally would not be
// worth writing down. It is worth writing down here because the bug was the
// sequence: claiming Avalonia's UI thread has to happen before anything that
// can post to the dispatcher from another thread, and on a machine whose screen
// never unlocks *everything* Buddy starts before the UI is up can. Nothing
// about `RemoteControlSessions.ServeOnLaunch()` looks like it competes for a
// static property in Avalonia, which is precisely why moving it back above the
// claim would be an easy and invisible thing to do.
//
// CB-178 added `claimSingleInstance` ahead of all of that, as a step that can
// answer "stop" and have `Run` return without touching anything after it —
// see the short-circuit tests near the bottom of this file, and
// Startup.Run's own comment for why a duplicate instance must never reach
// `claimUiThread` at all now.
//
// What cannot be asserted here is either claim itself — see
// UiThreadClaimTests for the UI-thread half, SingleInstanceTests for the
// single-instance decision, and tests/IntegrationTests' SingleInstanceTests
// for the real named mutex — and the CB-28 PR body for the measurement
// covering the rest.
public class StartupOrderTests
{
    [Fact]
    public void Claims_the_ui_thread_before_anything_else_starts()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => { },
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "log", "single", "claim", "serve", "wait", "ui" }, order);
    }

    [Fact]
    public void Claims_before_the_relay_that_would_otherwise_claim_it_from_the_pool()
    {
        // The pairing that actually matters, stated on its own so that a future
        // reordering fails on a test whose name says why it exists rather than
        // on a list of four strings.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => true,
            migrateUserData: () => { },
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal("claim", order[0]);
        Assert.Equal("serve", order[1]);
    }

    [Fact]
    public void Waits_for_the_screen_before_starting_the_ui_and_not_after()
    {
        // The CB-24 ordering, still true: the wait exists to keep Avalonia off a
        // locked screen, so a start that ran first would be the original bug
        // back again.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => true,
            migrateUserData: () => { },
            claimUiThread: () => { },
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => { },
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "wait", "ui" }, order);
    }

    [Fact]
    public void Installs_crash_logging_before_anything_that_could_crash()
    {
        // The ordering CB-44 is about. Both crashes it was filed for happened
        // in the last step, and left nothing on disk because nothing had
        // subscribed by then — so this is first, ahead of even the single-
        // instance claim and the UI-thread claim, both of which can throw.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => { },
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => order.Add("ui"));

        Assert.Equal("log", order[0]);
    }

    [Fact]
    public void Claims_single_instance_before_the_ui_thread_and_everything_after_it()
    {
        // CB-178's ordering. Placed straight after installCrashLog and ahead
        // of every other step, so a duplicate instance — which this test
        // arranges by having the claim answer "someone else holds it" — never
        // reaches claimUiThread, serveOnLaunch, waitForUnlock or startUi. See
        // the short-circuit tests below for that half; this one is only about
        // where the check sits when it says yes.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => { },
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal("single", order[0]);
        Assert.Equal("claim", order[1]);
    }

    [Fact]
    public void Runs_each_step_exactly_once()
    {
        var counts = new Dictionary<string, int>
        {
            ["log"] = 0, ["single"] = 0, ["migrate"] = 0, ["claim"] = 0, ["retire"] = 0, ["repair"] = 0,
            ["refresh"] = 0, ["serve"] = 0, ["wait"] = 0, ["ui"] = 0
        };

        Startup.Run(
            installCrashLog: () => counts["log"]++,
            claimSingleInstance: () => { counts["single"]++; return true; },
            migrateUserData: () => counts["migrate"]++,
            claimUiThread: () => counts["claim"]++,
            retireLegacy: () => counts["retire"]++,
            repairKeepAlive: () => counts["repair"]++,
            refreshHooks: () => counts["refresh"]++,
            serveOnLaunch: () => counts["serve"]++,
            waitForUnlock: () => counts["wait"]++,
            startUi: () => counts["ui"]++);

        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void Lets_a_failing_step_stop_the_ones_after_it()
    {
        // Deliberately not guarded, and asserted so that nobody adds a
        // try/catch here thinking it is kind. If ServeOnLaunch throws, the app
        // is in a state nobody has reasoned about; starting a UI on top of it
        // would hide the crash report that is the only evidence of what went
        // wrong. Same behaviour as the straight-line Main this replaced.
        var reached = false;

        Assert.Throws<InvalidOperationException>(() => Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => true,
            migrateUserData: () => { },
            claimUiThread: () => { },
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => throw new InvalidOperationException("relay"),
            waitForUnlock: () => reached = true,
            startUi: () => reached = true));

        Assert.False(reached);
    }

    // The CB-178 short-circuit itself: everything from claimUiThread onward
    // is a delegate specifically so this can be asserted without a real
    // dispatcher, a real relay or a real screen-lock wait. A duplicate
    // instance used to reach all of this and then some — see
    // App.axaml.cs's history — which is the bug.
    [Fact]
    public void Runs_nothing_after_the_single_instance_claim_when_it_says_stop()
    {
        var reached = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => false,
            migrateUserData: () => reached.Add("migrate"),
            claimUiThread: () => reached.Add("claim"),
            retireLegacy: () => reached.Add("retire"),
            repairKeepAlive: () => reached.Add("repair"),
            refreshHooks: () => reached.Add("refresh"),
            serveOnLaunch: () => reached.Add("serve"),
            waitForUnlock: () => reached.Add("wait"),
            startUi: () => reached.Add("ui"));

        Assert.Empty(reached);
    }

    [Fact]
    public void Still_installs_crash_logging_even_when_the_claim_says_stop()
    {
        // The one step that must run regardless: an unexpected throw out of
        // the claim itself is exactly the kind of failure CrashLog exists to
        // catch, so it has to be installed before the claim is even
        // attempted — independent of what the claim answers.
        var logged = false;

        Startup.Run(
            installCrashLog: () => logged = true,
            claimSingleInstance: () => false,
            migrateUserData: () => { },
            claimUiThread: () => { },
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => { },
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.True(logged);
    }

    [Fact]
    public void Runs_everything_in_order_when_the_claim_says_proceed()
    {
        // The complement of the two tests above, so "stop" and "proceed" are
        // both pinned down rather than one being inferred from the other.
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => order.Add("log"),
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => order.Add("migrate"),
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => order.Add("retire"),
            repairKeepAlive: () => order.Add("repair"),
            refreshHooks: () => order.Add("refresh"),
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(
            new[] { "log", "single", "migrate", "claim", "retire", "repair", "refresh", "serve", "wait", "ui" }, order);
    }

    // CB-255: the data-folder move sits between the two claims. After the
    // single-instance one, because two processes moving one folder is the race
    // that claim prevents; before the UI-thread claim and everything after it,
    // because serveOnLaunch is the first reader of settings, the peer identity
    // and the speech engine folder, and each must find the migrated folder.
    [Fact]
    public void Migrates_user_data_after_the_single_instance_claim_and_before_anything_reads_it()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => order.Add("migrate"),
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => { },
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal(new[] { "single", "migrate", "claim", "serve" }, order);
    }

    // CB-255: retiring legacy leftovers comes after the migration (the
    // Windows legacy hook folder also holds the legacy logs, which have to be
    // moved out first) and after the UI-thread claim, but before anything is
    // served or shown.
    [Fact]
    public void Retires_legacy_leftovers_after_migrating_and_before_serving()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => true,
            migrateUserData: () => order.Add("migrate"),
            claimUiThread: () => order.Add("claim"),
            retireLegacy: () => order.Add("retire"),
            repairKeepAlive: () => order.Add("repair"),
            refreshHooks: () => { },
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => { },
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "migrate", "claim", "retire", "repair", "serve", "ui" }, order);
    }

    // CB-256: the keep-alive repair runs straight after the retirement step —
    // a plist naming the legacy bundle goes stale when that step trashes it,
    // and running second catches it in the same launch — and still after the
    // single-instance claim (the script loads the job, and the copy launchd
    // starts must find this one holding the mutex) and before serving.
    [Fact]
    public void Repairs_the_keep_alive_after_retiring_legacy_and_before_serving()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => { },
            claimUiThread: () => { },
            retireLegacy: () => order.Add("retire"),
            repairKeepAlive: () => order.Add("repair"),
            refreshHooks: () => { },
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal(new[] { "single", "retire", "repair", "serve" }, order);
    }

    // CB-306: the installed-hook refresh (CB-265) ran from App, which is
    // behind the screen-lock wait, and on a Mac whose screen never unlocks
    // the UI never starts. The mini sat like that for a day with the fixed
    // hook in its bundle and the 26 Oct copy in ~/.claude/orbweaver, and
    // nothing in hook-installer.log — the one machine the fix was for. The
    // refresh is files only, so it runs with the other display-free steps,
    // ahead of the wait. This is the pairing that matters, on its own.
    [Fact]
    public void Refreshes_the_installed_hooks_before_the_screen_lock_wait()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => true,
            migrateUserData: () => { },
            claimUiThread: () => { },
            retireLegacy: () => { },
            repairKeepAlive: () => { },
            refreshHooks: () => order.Add("refresh"),
            serveOnLaunch: () => { },
            waitForUnlock: () => order.Add("wait"),
            startUi: () => order.Add("ui"));

        Assert.Equal(new[] { "refresh", "wait", "ui" }, order);
    }

    // And where among the display-free steps: after the single-instance
    // claim (two instances must never overwrite one script at once), after
    // the legacy retirement (never refresh a folder that step is retiring),
    // and before serving, so a session hooked from here on writes where the
    // app reads as early as possible.
    [Fact]
    public void Refreshes_hooks_after_the_claim_and_retirement_and_before_serving()
    {
        var order = new List<string>();

        Startup.Run(
            installCrashLog: () => { },
            claimSingleInstance: () => { order.Add("single"); return true; },
            migrateUserData: () => { },
            claimUiThread: () => { },
            retireLegacy: () => order.Add("retire"),
            repairKeepAlive: () => order.Add("repair"),
            refreshHooks: () => order.Add("refresh"),
            serveOnLaunch: () => order.Add("serve"),
            waitForUnlock: () => { },
            startUi: () => { });

        Assert.Equal(new[] { "single", "retire", "repair", "refresh", "serve" }, order);
    }

    // The interface units B, C and F build against (CB-255 §7): each step's
    // real body is static, takes nothing and never throws, and under the test
    // suites' environment overrides (TestBootstrap sets
    // ORBWEAVER_SETTINGS_DIR) it does nothing to a real folder. While those
    // files are still stubs this pins the shape; once each is real it keeps
    // pinning "no-throw under the test env".
    [Fact]
    public void The_migration_and_retirement_steps_are_no_throw_under_the_test_environment()
    {
        DataDirMigration.Run();
        LegacyHookCleanup.Run();
        MacOSLegacyBundle.Run();
        KeepAliveRepair.Run();
    }
}
