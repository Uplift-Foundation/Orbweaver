using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-302: the rules that decide whether a thought bubble or a flyout opens or
// stays open, given Avalonia's IsPointerOver (which goes stale when an orb
// window misses its leave) and the real cursor (which can be unavailable).
// A 72x72 orb at 100,200, so its last pixel is 171,271.
public class RealHoverTests
{
    private static readonly PixelRect Orb = new(100, 200, 72, 72);
    private static readonly PixelRect Flyout = new(60, 230, 160, 120);

    // --- ShouldOpen -----------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void WithNoCursorOpeningTrustsPointerOver(bool isPointerOver, bool expected) =>
        Assert.Equal(expected, RealHover.ShouldOpen(isPointerOver, null, Orb));

    [Fact]
    public void OpensWhenAvaloniaAndTheCursorBothSayOver() =>
        Assert.True(RealHover.ShouldOpen(true, new PixelPoint(130, 230), Orb));

    // The stale window: Avalonia still says over, the cursor is elsewhere.
    [Theory]
    [InlineData(99, 230)]    // left
    [InlineData(172, 230)]   // right
    [InlineData(130, 199)]   // above
    [InlineData(130, 272)]   // below
    public void AStaleWindowDoesNotOpenWithTheCursorOutside(int x, int y) =>
        Assert.False(RealHover.ShouldOpen(true, new PixelPoint(x, y), Orb));

    // A cursor inside is not enough on its own: nothing told this window the
    // pointer arrived, so the delay that leads here was not started by a hover.
    [Fact]
    public void ACursorInsideWithoutPointerOverDoesNotOpen() =>
        Assert.False(RealHover.ShouldOpen(false, new PixelPoint(130, 230), Orb));

    [Theory]
    [InlineData(100, 230, true)]   // left edge is in
    [InlineData(130, 200, true)]   // top edge is in
    [InlineData(171, 271, true)]   // last pixel is in
    [InlineData(172, 230, false)]  // right edge is out
    [InlineData(130, 272, false)]  // bottom edge is out
    public void EdgesAreInclusiveLeftAndTopExclusiveRightAndBottom(int x, int y, bool expected) =>
        Assert.Equal(expected, RealHover.ShouldOpen(true, new PixelPoint(x, y), Orb));

    // --- ShouldStayOpen ---------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void WithNoCursorStayingOpenTrustsPointerOver(bool isPointerOver, bool expected) =>
        Assert.Equal(expected, RealHover.ShouldStayOpen(isPointerOver, null, Orb));

    // The case the whole ticket is about: IsPointerOver stuck true, cursor gone.
    [Theory]
    [InlineData(99, 230)]
    [InlineData(172, 230)]
    [InlineData(130, 199)]
    [InlineData(130, 272)]
    public void AStaleFlagDoesNotKeepItOpenWithTheCursorOutside(int x, int y) =>
        Assert.False(RealHover.ShouldStayOpen(true, new PixelPoint(x, y), Orb));

    // And the other direction: a leave reported early does not close something
    // the cursor is still on.
    [Fact]
    public void ACursorInsideKeepsItOpenEvenWithPointerOverFalse() =>
        Assert.True(RealHover.ShouldStayOpen(false, new PixelPoint(130, 230), Orb));

    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(171, 271, true)]
    [InlineData(172, 271, false)]
    [InlineData(171, 272, false)]
    public void StayingOpenUsesTheSameEdges(int x, int y, bool expected) =>
        Assert.Equal(expected, RealHover.ShouldStayOpen(true, new PixelPoint(x, y), Orb));

    // --- the flyout: two frames, either one keeps it ----------------------------

    [Theory]
    [InlineData(110, 210, true)]   // on the orb only
    [InlineData(70, 340, true)]    // on the flyout only
    [InlineData(130, 250, true)]   // where they overlap
    [InlineData(40, 100, false)]   // on neither
    [InlineData(500, 500, false)]  // across the screen, the screenshot's case
    public void TheFlyoutStaysOpenWhileTheCursorIsOnEitherWindow(int x, int y, bool expected) =>
        Assert.Equal(expected, RealHover.ShouldStayOpen(true, new PixelPoint(x, y), Orb, Flyout));

    [Fact]
    public void NoFramesAtAllMeansNotOver() =>
        Assert.False(RealHover.ShouldStayOpen(true, new PixelPoint(130, 230)));

    // --- Inside, directly ---------------------------------------------------------

    [Fact]
    public void AnEmptyFrameContainsNothing() =>
        Assert.False(RealHover.Inside(new PixelPoint(5, 5), new PixelRect(5, 5, 0, 0)));

    // --- FrameOf --------------------------------------------------------------------

    // macOS: Position and DIPs are both points.
    [Fact]
    public void AFrameAtOneUnitPerDipIsPositionPlusSize() =>
        Assert.Equal(new PixelRect(100, 200, 72, 72), RealHover.FrameOf(new PixelPoint(100, 200), new Size(72, 72), 1));

    // Windows at 150%: Position is physical pixels, so the size scales with it.
    [Fact]
    public void AFrameAtOneAndAHalfUnitsPerDipScalesTheSizeNotThePosition() =>
        Assert.Equal(new PixelRect(100, 200, 108, 108), RealHover.FrameOf(new PixelPoint(100, 200), new Size(72, 72), 1.5));
}
