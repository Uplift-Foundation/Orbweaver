using Xunit;

namespace Orbweaver.Tests;

// ShadowSwitch.IsOn: whether the typing gate only logs (CB-307). Log-only is
// the default; enforcing is opt-in. Every combination the lead listed — none,
// the shadow marker, the enforce marker, env=0, env=1 — and the precedence:
// env over both markers, the shadow marker over the enforce marker.
public class ShadowSwitchTests
{
    private const string Dir = "/settings";
    private static readonly string Shadow = System.IO.Path.Combine(Dir, ShadowSwitch.MarkerFileName);
    private static readonly string Enforce = System.IO.Path.Combine(Dir, ShadowSwitch.EnforceMarkerFileName);

    private static Func<string, bool> Markers(bool shadow, bool enforce) =>
        path => (shadow && path == Shadow) || (enforce && path == Enforce);

    // env: null = unset. Expected: true = log-only, false = enforce.
    [Theory]
    [InlineData(null, false, false, true)]   // nothing set: log-only
    [InlineData(null, true, false, true)]    // shadow marker: log-only (installed Macs unchanged)
    [InlineData(null, false, true, false)]   // enforce marker: enforce
    [InlineData(null, true, true, true)]     // both markers: the shadow marker wins
    [InlineData("0", false, false, false)]   // env=0: enforce
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

    [Theory]
    [InlineData(" 0 ")]
    public void ZeroWithSpacesStillEnforces(string value) =>
        Assert.False(ShadowSwitch.IsOn(Dir, value, Markers(true, false)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankVariableFallsThroughToTheMarkers(string? value)
    {
        Assert.False(ShadowSwitch.IsOn(Dir, value, Markers(false, true)));
        Assert.True(ShadowSwitch.IsOn(Dir, value, Markers(false, false)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoSettingsFolderIsLogOnlyWithoutAsking(string? dir) =>
        Assert.True(ShadowSwitch.IsOn(dir, null, _ => throw new System.InvalidOperationException("asked")));

    // A file check that throws is a marker that is not there: with nothing
    // readable, the default — log-only — stands.
    [Fact]
    public void AFileCheckThatThrowsLeavesTheDefault() =>
        Assert.True(ShadowSwitch.IsOn(Dir, null, _ => throw new System.UnauthorizedAccessException()));

    [Fact]
    public void AnEnforceMarkerStillCountsWhenTheShadowCheckThrows() =>
        Assert.False(ShadowSwitch.IsOn(Dir, null,
            path => path == Shadow ? throw new System.UnauthorizedAccessException() : path == Enforce));
}
