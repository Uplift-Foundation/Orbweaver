using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Threading;

namespace Orbweaver
{
    // Excluded from coverage: the process entry point. Main waits on the real
    // screen-lock state and then hands control to
    // StartWithClassicDesktopLifetime, which owns the process for its lifetime;
    // BuildAvaloniaApp configures the one AppBuilder a process gets, and the
    // headless suites build their own (see tests/UiTests' TestAppBuilder). There
    // is no way to run either without ending the test run.
    [ExcludeFromCodeCoverage]
    internal static class Program
    {
        // How long to wait before starting anyway *when the lock state cannot
        // be read at all* — a daemon or background-job context, where
        // CGSessionCopyCurrentDictionary returns null and there is no window
        // server session to ask. Long enough to cover coming back to the
        // machine after a while, short enough that an unknowable state can't
        // keep the app off the menu bar for a whole session.
        //
        // This is no longer the cap on a screen the window server has *told*
        // us is locked — that one has no cap at all (see below), and starting when this
        // shorter one expired is what the 2026-09-22 -6661 crash was.
        private static readonly TimeSpan LockWait = TimeSpan.FromHours(2);

        // There is no cap on a screen the window server reports locked: startup
        // waits, serving peers, until it unlocks (CB-215). See
        // ScreenLockWait.ShouldStartNow for why the twelve-hour cap it used to
        // have was removed.

        private static readonly TimeSpan LockPoll = TimeSpan.FromSeconds(2);

        // Held for the process's lifetime once we own it, so it is not
        // finalized out from under us — a local variable would be eligible
        // for GC (and, with it, the finalizer that releases the mutex) the
        // moment Main stops referencing it, which is right away since
        // everything after this point runs through Startup.Run's delegates.
        // A list since CB-255: the legacy name and the new one are both held,
        // for the whole of the process, so an old build started later finds
        // its own name taken (see SingleInstance.ClaimNames).
        private static IReadOnlyList<Mutex> _singleInstanceMutexes = [];

        [STAThread]
        public static void Main(string[] args)
        {
            // The order these run in is Startup.Run's, and the first two
            // are the fixes for CB-44 and CB-178 respectively — see
            // Startup.Run's own comment for why claimSingleInstance sits where
            // it does. What each step is *for* is here, next to the thing it
            // calls.
            Startup.Run(
                // Write an unhandled exception down before anything can throw
                // one. Buddy aborted twice on the mini on 28 Aug with nothing on
                // disk to say why, and the .ips reports could not name the
                // exception; CrashLog exists so the next one costs a `cat`
                // rather than a probe (CB-44).
                installCrashLog: CrashLog.Install,

                // Whether this is the one Buddy that gets to run. See
                // SingleInstance.cs for the enum and the reasoning; this is
                // just the glue that keeps the acquired mutexes alive for the
                // rest of the process. When we are the duplicate, ClaimAll
                // has already given back and closed everything it took, so
                // there is nothing here to dispose.
                claimSingleInstance: () =>
                {
                    var (proceed, held) = SingleInstance.ClaimAll(SingleInstance.ClaimNames);
                    if (!proceed) return false;

                    _singleInstanceMutexes = held;
                    return true;
                },

                // Move the user's data and logs from the legacy folder name to
                // the new one (CB-255). Only the instance that holds both
                // claims gets here, and nothing has read settings yet.
                migrateUserData: DataDirMigration.Run,

                // Claim Avalonia's UI thread for this thread while it is
                // certain to be free, which is the only moment it is:
                // everything after this line either starts something that
                // posts to the dispatcher from the thread pool, or holds this
                // thread for hours while it does.
                claimUiThread: Startup.ClaimUiThread,

                // Retire what the rename left behind (CB-255): legacy hook
                // folders the installers have marked superseded long enough
                // ago, and on macOS a verified-ours Claude Buddy.app beside the
                // new bundle. Both are no-throw; holding both mutex names is
                // what proves no old build is running while they act.
                retireLegacy: () =>
                {
                    LegacyHookCleanup.Run();
                    MacOSLegacyBundle.Run();
                },

                // On macOS, re-point the crash keep-alive LaunchAgent when the
                // program it names is gone — a drag-install over a phase-2
                // bundle, whose executable was ClaudeBuddy (CB-256). Runs the
                // bundle's own install-hooks.sh --keepalive-only; no-throw, and
                // a no-op whenever the plist's program exists.
                repairKeepAlive: KeepAliveRepair.Run,

                // Overwrite an installed hook script that differs from the
                // bundled one (CB-265), here rather than from App because
                // App is behind the screen-lock wait below and the mini's
                // screen never unlocks — so the fix in 5343d162 sat in the
                // bundle on that machine while every session kept running
                // the 26 Oct script (CB-306). Files only, on a pool thread;
                // never creates a copy, never touches settings.json.
                refreshHooks: HookFreshness.StartInBackground,

                // The serve path before the screen-lock wait below, because it
                // needs nothing that wait exists to protect: a relay is tmux,
                // files and subprocesses, with no display anywhere in it. A
                // machine that serves its sessions to other Buddies unattended
                // is exactly a machine whose screen may never unlock —
                // headless, in a cupboard — and parking the relay behind the
                // wait meant it never served at all (CB-24). Does nothing
                // unless remoteControlServeOnLaunch is on; and if this early
                // start fails, SessionManager.Start makes the same call again
                // once the UI is up, because EnsureStarted retries a failed
                // relay.
                serveOnLaunch: () =>
                {


                    // The peer link starts here for exactly the reasons above,
                    // and rather more sharply. It is a socket and a UDP
                    // announcement — no display anywhere in it — and the
                    // machine it matters most on is the one whose screen never
                    // unlocks. Parking it behind the screen-lock wait would
                    // reproduce CB-24 on a transport that has no excuse for it.
                    //
                    // Does nothing unless peerLinkEnabled is on.
                    PeerSessions.Start();

                    // OpenClawSessions.Restart() used to wait behind the
                    // screen-lock check too, in SessionManager.Start() — and a
                    // machine kept permanently locked (a headless server Buddy,
                    // paired to hand another machine its resolved agent
                    // voices) never restarted it in any practical timeframe: it
                    // restarts on every relaunch, so the cap this file's
                    // waitForUnlock imposes never actually elapses.
                    // Opening the gateway connection is a WebSocket client and
                    // a background poll loop, the same shape as PeerSessions
                    // above rather than anything that touches a window, so it
                    // belongs here for the identical reason (CB-130). Does
                    // nothing unless openclawEnabled is on.
                    OpenClawSessions.Restart();

                    // And Claude Code's own cloud sessions, here for the
                    // identical reason and with the identical shape: an HTTPS
                    // poll and nothing that touches a window. The headless
                    // machine CB-130 was about is exactly the one likeliest to
                    // be watching cloud sessions rather than local ones, so
                    // this is the last place it should be parked behind a
                    // screen-lock wait. Does nothing unless claudeCloudEnabled
                    // is on.
                    ClaudeCloudSessions.Restart();
                },

                // Avalonia's macOS render timer is a CVDisplayLink, and
                // creating one fails with -6661 (kCVReturnInvalidArgument —
                // see MacOSScreenLock for why this was called
                // kCVReturnInvalidDisplay, which is -6670, and why the
                // conclusion holds anyway) while the screen is locked, which
                // killed startup outright. A
                // Login Item starts before you type your password, so every
                // reboot hit this and the app was simply missing afterwards
                // with no visible reason.
                //
                // Nothing is lost by waiting: a menu-bar icon on a locked
                // screen is invisible either way, and sessions that start while
                // locked still get picked up, because the hook writes status
                // files to disk and SessionManager reads them on its first
                // scan.
                //
                // This step used to hand back a bool saying "the cap expired
                // and the screen is still locked", and this lambda's
                // conversion to Startup.Run's `Action` threw it away — so the
                // UI started on a screen the app had just confirmed was
                // locked, which is exactly the -6661 above. It returns void
                // now and the decision lives in ScreenLockWait, one arm per
                // state, so there is nothing here left to discard.
                waitForUnlock: () =>
                    MacOSScreenLock.WaitForUnlock(LockWait, LockPoll),

                startUi: () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args));
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The orbs are the whole UI; no Dock icon needed on macOS.
                .With(new MacOSPlatformOptions { ShowInDock = false })
                // The earliest moment the platform's dispatcher exists, and
                // before the app has started any timers of its own. See
                // DispatcherClock for why every timer was late without it.
                .AfterPlatformServicesSetup(_ => DispatcherClock.Align(Dispatcher.UIThread))
                .LogToTrace();
    }
}
