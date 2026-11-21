using Xunit;

namespace Orbweaver.Tests;

// ShadowSwitch.IsOn: whether the typing gate only logs. CB-403 flipped the
// default: nothing set enforces, and log-only is the explicit opt-out — the
// variable set non-"0", or the `local-gate-shadow` marker. The enforce marker
// is redundant and harmless.
public class ShadowSwitchTests
{
    private const string Dir = "/settings";
    private static readonly string Shadow = System.IO.Path.Combine(Dir, ShadowSwitch.MarkerFileName);
    private static readonly string Enforce = System.IO.Path.Combine(Dir, ShadowSwitch.EnforceMarkerFileName);

    private static Func<string, bool> Markers(bool shadow, bool enforce) =>
        path => (shadow && path == Shadow) || (enforce && path == Enforce);

    // env: null = unset. Expected: true = log-only, false = enforce.
    [Theory]
    [InlineData(null, false, false, false)]  // nothing set: enforce
    [InlineData(null, true, false, true)]    // shadow marker: log-only
    [InlineData(null, false, true, false)]   // enforce marker: redundant, still enforce
    [InlineData(null, true, true, true)]     // both markers: the shadow marker wins
    [InlineData("0", false, false, false)]
    [InlineData("0", true, false, false)]    // env=0 beats the shadow marker
    [InlineData("0", true, true, false)]
    [InlineData("1", false, false, true)]    // env=1: log-only
    [InlineData("1", false, true, true)]     // env=1 beats the enforce marker
    [InlineData("1", true, true, true)]
    public void EveryCombination(string? env, bool shadowMarker, bool enforceMarker, bool logOnly) =>
        Assert.Equal(logOnly, ShadowSwitch.IsOn(Dir, env, Markers(shadowMarker, enforceMarker)));

    [Theory]
    [InlineData("true")]
    [InlineData(" yes ")]
    public void AnyNonZeroVariableIsLogOnly(string value) =>
        Assert.True(ShadowSwitch.IsOn(Dir, value, Markers(false, true)));

    [Fact]
    public void ZeroWithSpacesStillEnforces() =>
        Assert.False(ShadowSwitch.IsOn(Dir, " 0 ", Markers(true, false)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankVariableFallsThroughToTheMarkers(string? value)
    {
        Assert.True(ShadowSwitch.IsOn(Dir, value, Markers(true, false)));
        Assert.False(ShadowSwitch.IsOn(Dir, value, Markers(false, false)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoSettingsFolderEnforcesWithoutAsking(string? dir) =>
        Assert.False(ShadowSwitch.IsOn(dir, null, _ => throw new System.InvalidOperationException("asked")));

    // A file check that throws is a marker that is not there: the default,
    // enforce, stands.
    [Fact]
    public void AFileCheckThatThrowsLeavesTheDefault() =>
        Assert.False(ShadowSwitch.IsOn(Dir, null, _ => throw new System.UnauthorizedAccessException()));
}
