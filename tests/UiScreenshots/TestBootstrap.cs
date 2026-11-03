using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

// Same reasoning as tests/UiTests/TestBootstrap.cs's identical line: every
// [AvaloniaFact] here shares one Avalonia application and one headless
// dispatcher, and xUnit's default of running different test classes as
// parallel collections means real concurrent Window construction against
// that one shared dispatcher — a plausible contributor to the FontManager
// race TestAppBuilder's warm-up also guards against. One piece of several,
// not a fix by itself; costs nothing given how fast this suite runs.
//
// Pinned to xunit.v3 3.2.2 (see the csproj's own comment on why — Avalonia.
// Headless.XUnit 12.1.1 depends on xunit.v3.extensibility.core 3.2.2
// specifically, and a newer 4.0.0 causes a binary-incompatible
// MissingMethodException at test discovery). DisableTestParallelization is
// only obsolete starting in xunit.v3 4.0.0's Xunit.v3.ParallelizationAttribute
// — not available yet at 3.2.2 — so this stays on the older spelling.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Orbweaver.Tests;

// Same seam as tests/UiTests/TestBootstrap.cs, for the same reason: OrbWindow
// and OrbFlyout read settings-backed colors the moment they're constructed
// (OrbWindow's _orbBrush field initializer, OrbColors.Idle), so this points
// ORBWEAVER_SETTINGS_DIR at a private scratch directory before that can
// happen, or this suite reads and writes the developer's real settings.json.
internal static class TestBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cb-uiscreenshots-" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", scratch);

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
        // TextToSpeech.SilenceForTests and ChimePlayer.SilenceForTests.
        TextToSpeech.SilenceForTests = true;
        ChimePlayer.SilenceForTests = true;

        // CB-302: no capture reads the developer's real mouse — see CursorProbe
        // and the same line in tests/UiTests/TestBootstrap.cs.
        CursorProbe.Read = () => null;

        OrbweaverSettings.ReloadForTests();
    }
}
