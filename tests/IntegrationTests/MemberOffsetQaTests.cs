using Xunit;

namespace Orbweaver.Tests;

// CB-268 QA probe: hand-edited orbMemberOffsets values (DIP doubles, contract T2).
[Collection("Settings")]
public class MemberOffsetQaTests
{
    private static void Stage(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb268-off-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void AbsurdOrNonFiniteStoredOffsetsAreDroppedAndAGoodOneSurvives()
    {
        Stage("""{"orbMemberOffsets":{"a":{"dx":1e300,"dy":-1e300},"b":{"dx":5.5,"dy":6},"c":{"dx":"x","dy":1}}}""");

        var a = OrbweaverSettings.OrbMemberOffsetFor("a");
        Console.WriteLine($"a => {a}");
        Assert.True(a is null || (Math.Abs(a.Value.Dx) <= 1e6 && Math.Abs(a.Value.Dy) <= 1e6), $"a => {a}");
        Assert.Equal((5.5, 6.0), OrbweaverSettings.OrbMemberOffsetFor("b"));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("c"));
    }

    [Fact]
    public void SetRoundTripsFractionsAndNullClearsAndSurvivesAReload()
    {
        Stage("{}");
        OrbweaverSettings.SetOrbMemberOffset("k", (0.25, -3.5));
        Assert.Equal((0.25, -3.5), OrbweaverSettings.OrbMemberOffsetFor("K"));            // case-insensitive
        OrbweaverSettings.FlushPendingSave();
        var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;
        OrbweaverSettings.ReloadForTests();
        Assert.Equal((0.25, -3.5), OrbweaverSettings.OrbMemberOffsetFor("k"));
        OrbweaverSettings.SetOrbMemberOffset("k", null);
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("k"));
        OrbweaverSettings.SetOrbMemberOffset("", (1, 1));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(""));
        OrbweaverSettings.SetOrbMemberOffset("n", (double.NaN, 1));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("n"));
        Assert.True(Directory.Exists(dir));
    }
}
