using Avalonia.Media;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 S5: GroupStyle.Resolve — per-field inheritance from the global outline
// style, with the fallbacks the contract names.
public class GroupStyleResolveTests
{
    private static readonly GroupStyle Globals = new("#336699", 0.5, "#FF8800", 0.25);

    [Fact]
    public void NoOverrideTakesEveryGlobal()
    {
        var (stroke, fill) = GroupStyle.Resolve(null, Globals);

        Assert.Equal(Color.FromArgb(128, 0x33, 0x66, 0x99), stroke);
        Assert.Equal(Color.FromArgb(64, 0xFF, 0x88, 0x00), fill);
    }

    [Fact]
    public void AnAllNullOverrideIsTheSameAsNone()
    {
        Assert.Equal(GroupStyle.Resolve(null, Globals), GroupStyle.Resolve(new GroupStyle(null, null, null, null), Globals));
    }

    [Fact]
    public void EachFieldOverridesIndependently()
    {
        var (stroke, fill) = GroupStyle.Resolve(new GroupStyle(null, null, "#00FF00", 1.0), Globals);

        Assert.Equal(Color.FromArgb(128, 0x33, 0x66, 0x99), stroke);   // inherited
        Assert.Equal(Color.FromArgb(255, 0, 255, 0), fill);           // overridden

        var (stroke2, _) = GroupStyle.Resolve(new GroupStyle("#112233", null, null, null), Globals);
        Assert.Equal(Color.FromArgb(128, 0x11, 0x22, 0x33), stroke2);  // colour only, opacity inherited

        var (stroke3, _) = GroupStyle.Resolve(new GroupStyle(null, 0.0, null, null), Globals);
        Assert.Equal(Color.FromArgb(0, 0x33, 0x66, 0x99), stroke3);    // opacity only, colour inherited
    }

    [Fact]
    public void OpacityBecomesTheAlphaByteReplacingTheColoursOwnAlpha()
    {
        var (stroke, _) = GroupStyle.Resolve(new GroupStyle("#80112233", 1.0, null, null), Globals);
        Assert.Equal(Color.FromArgb(255, 0x11, 0x22, 0x33), stroke);
    }

    [Theory]
    [InlineData(-3.0, 0)]
    [InlineData(7.0, 255)]
    [InlineData(0.35, 89)]
    public void OpacityIsClampedAndRounded(double opacity, int alpha)
    {
        var (stroke, _) = GroupStyle.Resolve(new GroupStyle(null, opacity, null, null), Globals);
        Assert.Equal(alpha, stroke.A);
    }

    [Fact]
    public void AnUnparsableColourFallsBackToTheGlobal()
    {
        var (stroke, fill) = GroupStyle.Resolve(new GroupStyle("not-a-colour", null, "#zzz", null), Globals);

        Assert.Equal(Color.FromArgb(128, 0x33, 0x66, 0x99), stroke);
        Assert.Equal(Color.FromArgb(64, 0xFF, 0x88, 0x00), fill);
    }

    [Fact]
    public void WhenTheGlobalAlsoFailsStrokeIsWhiteAndFillIsBlack()
    {
        var broken = new GroupStyle("nope", 1.0, "nope", 1.0);
        var (stroke, fill) = GroupStyle.Resolve(null, broken);

        Assert.Equal(Colors.White, stroke);
        Assert.Equal(Colors.Black, fill);

        var (s2, f2) = GroupStyle.Resolve(new GroupStyle("also bad", null, "bad", null), broken);
        Assert.Equal(Colors.White, s2);
        Assert.Equal(Colors.Black, f2);
    }

    [Fact]
    public void ANullGlobalColourIsTreatedLikeAnUnparsableOne()
    {
        var (stroke, fill) = GroupStyle.Resolve(null, new GroupStyle(null, 1.0, null, 1.0));
        Assert.Equal(Colors.White, stroke);
        Assert.Equal(Colors.Black, fill);
    }

    [Fact]
    public void ANonFiniteOpacityIsTreatedAsMissingAndFallsThroughToTheGlobalThenTheDefault()
    {
        var (stroke, _) = GroupStyle.Resolve(new GroupStyle(null, double.NaN, null, null), Globals);
        Assert.Equal(128, stroke.A);   // global

        var noOpacity = new GroupStyle("#FFFFFF", double.PositiveInfinity, "#000000", null);
        var (s2, f2) = GroupStyle.Resolve(null, noOpacity);
        Assert.Equal((byte)System.Math.Round(OrbweaverSettings.DefaultGroupOutlineStrokeOpacity * 255), s2.A);
        Assert.Equal((byte)System.Math.Round(OrbweaverSettings.DefaultGroupOutlineFillOpacity * 255), f2.A);
    }
}
