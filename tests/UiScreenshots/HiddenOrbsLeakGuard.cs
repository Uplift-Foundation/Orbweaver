using System;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.v3;

[assembly: Orbweaver.UiScreenshots.HiddenOrbsLeakGuard]

namespace Orbweaver.UiScreenshots;

// CB-276: every scan in this assembly reads one process-wide hidden set, so a
// hide one test leaves behind silently takes an orb away from whichever test
// scans that id next. That is exactly how PR #250 went red on both CI legs: a
// HiddenOrbCoreScanTests case ended with session "a" hidden, and
// OrbDropSticksTests and RemoteScanTests — which scan their own "a" and never
// reload settings — lost it. Local runs passed only because their order put the
// leak somewhere harmless.
//
// So no test is trusted to clean up, the same reasoning ResetOpenClawApprovals
// records. Before each test in the serialised "Settings" collection the hidden
// set must be empty. If it is not, it is cleared — so this test is protected —
// and this test fails with a message naming the test that ran last, which is
// the one that left it.
//
// The check is in Before rather than After on purpose: xunit runs a test
// class's Dispose *after* After, and a class that cleans up in Dispose would
// otherwise be failed for a leak it was about to fix. The cost is that the red
// test is the one after the leaker; the message says which test that was.
//
// What it does not cover: a hide that is live *during* another test. That
// cannot happen here only because TestBootstrap disables test parallelization
// for the whole assembly. Turn parallelism back on and a hide test running
// beside a scan of the same id would race, and this guard would not see it.
// The hide tests use short ids like "a" on the strength of that setting.
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class HiddenOrbsLeakGuardAttribute : BeforeAfterTestAttribute
{
    private static string _previous = "(none — first test in the run)";

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!InSettings(methodUnderTest)) return;

        var leaked = OrbweaverSettings.HiddenOrbIds();
        if (leaked.Count == 0) return;

        OrbweaverSettings.RestoreAllHiddenOrbs();
        throw new InvalidOperationException(
            $"Hidden orbs [{string.Join(", ", leaked.OrderBy(x => x))}] were left behind by "
            + $"{_previous}. Cleared before this test; fix that test's cleanup "
            + "(SettingsDirScope, or RestoreAllHiddenOrbs in finally/Dispose).");
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (InSettings(methodUnderTest)) _previous = test.TestDisplayName;
    }

    private static bool InSettings(MethodInfo method) =>
        method.DeclaringType!.GetCustomAttributesData().Any(d =>
            d.AttributeType.Name == "CollectionAttribute"
            && d.ConstructorArguments.Count > 0
            && d.ConstructorArguments[0].Value as string == "Settings");
}
