using Xunit;

namespace Orbweaver.Tests;

// ShadowSwitch.IsOn: whether the local typing gate only logs (CB-307). Pure,
// with the directory, the variable and the file check handed in.
public class ShadowSwitchTests
{
    private const string Dir = "/settings";
    private static readonly string Marker = System.IO.Path.Combine(Dir, ShadowSwitch.MarkerFileName);

    [Fact]
    public void TheMarkerFileTurnsItOn() =>
        Assert.True(ShadowSwitch.IsOn(Dir, null, path => path == Marker));

    [Fact]
    public void NoMarkerAndNoVariableIsOff() =>
        Assert.False(ShadowSwitch.IsOn(Dir, null, _ => false));

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(" yes ")]
    public void AnyNonZeroVariableTurnsItOn(string value) =>
        Assert.True(ShadowSwitch.IsOn(Dir, value, _ => false));

    // Zero is the one off value, and it wins over a marker file: the variable
    // can be left in a profile and switched off without being unset.
    [Theory]
    [InlineData("0")]
    [InlineData(" 0 ")]
    public void ZeroTurnsItOffEvenWithAMarkerFile(string value) =>
        Assert.False(ShadowSwitch.IsOn(Dir, value, _ => true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankVariableFallsThroughToTheMarkerFile(string? value)
    {
        Assert.True(ShadowSwitch.IsOn(Dir, value, _ => true));
        Assert.False(ShadowSwitch.IsOn(Dir, value, _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoSettingsFolderMeansOffRatherThanAnException(string? dir) =>
        Assert.False(ShadowSwitch.IsOn(dir, null, _ => throw new System.InvalidOperationException("asked")));

    [Fact]
    public void AFileCheckThatThrowsIsOff() =>
        Assert.False(ShadowSwitch.IsOn(Dir, null, _ => throw new System.UnauthorizedAccessException()));
}
