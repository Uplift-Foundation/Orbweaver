using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Orbweaver.Tests;

// Runs once, before any test in this assembly, no matter which test class
// happens to run first. OrbweaverSettings.Directory reads
// ORBWEAVER_SETTINGS_DIR when it's set and falls back to the developer's
// real %APPDATA%/~/Library/... path otherwise — any test that touches a
// OrbweaverSettings property (even indirectly, e.g. via OrbColors or
// ClaudeDesktopColors, both of which read through to settings) would
// otherwise read and possibly overwrite Owner's real settings.json.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-unittests-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);

        // Where LocalPersona's walk up from a session's directory stops. The
        // scratch projects these tests build live under the temp directory,
        // which on Windows is inside the developer's home, so an unbounded walk
        // reaches the real home CLAUDE.md and the persona it imports — and
        // every test expecting "no persona", or its own fixture's, gets the
        // developer's instead. Ceilinged at the temp directory itself: the walk
        // still climbs through every scratch ancestor a test builds.
        Environment.SetEnvironmentVariable(
            "ORBWEAVER_PERSONA_WALK_CEILING", Path.GetTempPath());

        // No test in this assembly asks the OS for a credential. On macOS the
        // cloud arm's credential lives in the login Keychain, and reading it from
        // another application raises a consent dialog — which, headless, nobody
        // answers: the read waits out its forty-five-second budget, leaks the pool
        // thread parked inside Security.framework, and repeats for the next test
        // that gets there. It is invisible in CI, where no such Keychain item
        // exists and the query fails fast, and it only bites on a machine where
        // somebody has actually logged in. Set here with the settings seam above,
        // before any static constructor can run.
        Environment.SetEnvironmentVariable("ORBWEAVER_NO_CREDENTIAL_STORE", "1");

        // The floor under CrashLog.Directory (CB-285). Every ClaudeCloudChatSession
        // writes its decisions to cloud-chat.log there unless a test hands it its
        // own Log, and this assembly builds sessions that do not — so, left unset,
        // a run wrote into the developer's real ~/Library/Logs/Orbweaver. Only
        // IntegrationTests pointed it at scratch before; see its TestBootstrap for
        // why one assembly-wide directory rather than one per class.
        Environment.SetEnvironmentVariable(
            "ORBWEAVER_LOG_DIR",
            Path.Combine(Path.GetTempPath(), "cb-unittests-log-" + Guid.NewGuid()));

        // Where StatusDirectory.Path() puts settings-errors.log — left unset,
        // every suite run appends failure traces to the developer's real
        // $TMPDIR/orbweaver/settings-errors.log (CB-17).
        //
        // ORBWEAVER_STATUS_ROOT, not TMPDIR: moving TMPDIR from inside the
        // test process breaks the coverage collector's IPC and cost this repo
        // its added-line coverage rule for a while. See IntegrationTests'
        // TestBootstrap for the full reasoning (CB-172).
        var statusRoot = Path.Combine(
            Path.GetTempPath(), "cbt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(statusRoot);
        Environment.SetEnvironmentVariable("ORBWEAVER_STATUS_ROOT", statusRoot);

        // CB-168: no test in this assembly, including one nobody has
        // written yet, may reach a real speech engine or chime process — see
        // TextToSpeech.SilenceForTests and ChimePlayer.SilenceForTests for
        // why this has to be a process-wide guard rather than trusted to
        // every call site remembering its own seam.
        TextToSpeech.SilenceForTests = true;
        // CB-404: no run-controls probe (claude -p, codex app-server) ever starts in a suite.
        RunSourcesLive.ProbesOffForTests = true;
        ChimePlayer.SilenceForTests = true;

        OrbweaverSettings.ReloadForTests();
    }
}
