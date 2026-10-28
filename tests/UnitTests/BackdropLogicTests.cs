using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 S5: the decisions behind the group backdrop windows. The native calls
// that act on them are not testable headless; these are.
public class BackdropLogicTests
{
    private static readonly Point[] Square =
        { new(10, 10), new(90, 10), new(90, 90), new(10, 90) };

    private static readonly Rect Bounds = new(0, 0, 100, 100);
    private static readonly (double X, double Y, double R)[] NoDiscs = Array.Empty<(double, double, double)>();

    // --- ShouldCatch ---------------------------------------------------------

    [Fact]
    public void InsideTheSilhouetteCatchesAndOutsideDoesNot()
    {
        Assert.True(BackdropHitTest.ShouldCatch(new Point(50, 50), Bounds, Square, NoDiscs, false, null));
        Assert.False(BackdropHitTest.ShouldCatch(new Point(5, 5), Bounds, Square, NoDiscs, false, null));
    }

    [Fact]
    public void TheCornersOfTheBoundingBoxPassThroughEvenWhenInsideTheRectangle()
    {
        var circle = Enumerable.Range(0, 360)
            .Select(i => new Point(50 + 45 * Math.Cos(i * Math.PI / 180), 50 + 45 * Math.Sin(i * Math.PI / 180)))
            .ToArray();

        Assert.False(BackdropHitTest.ShouldCatch(new Point(8, 8), Bounds, circle, NoDiscs, false, null));
        Assert.True(BackdropHitTest.ShouldCatch(new Point(50, 50), Bounds, circle, NoDiscs, false, null));
    }

    [Fact]
    public void OutsideTheBoundsIsDecidedWithoutTheSilhouette()
    {
        Assert.False(BackdropHitTest.ShouldCatch(new Point(500, 500), Bounds, Square, NoDiscs, false, null));
    }

    [Fact]
    public void NothingChangesWhileAButtonIsDown()
    {
        Assert.Null(BackdropHitTest.ShouldCatch(new Point(50, 50), Bounds, Square, NoDiscs, true, null));
        Assert.Null(BackdropHitTest.ShouldCatch(new Point(5, 5), Bounds, Square, NoDiscs, true, true));
    }

    [Fact]
    public void ANativeCallIsOnlyAskedForOnAChange()
    {
        Assert.Null(BackdropHitTest.ShouldCatch(new Point(50, 50), Bounds, Square, NoDiscs, false, true));
        Assert.Null(BackdropHitTest.ShouldCatch(new Point(5, 5), Bounds, Square, NoDiscs, false, false));
        Assert.False(BackdropHitTest.ShouldCatch(new Point(5, 5), Bounds, Square, NoDiscs, false, true));
        Assert.True(BackdropHitTest.ShouldCatch(new Point(50, 50), Bounds, Square, NoDiscs, false, false));
    }

    [Fact]
    public void AnOrbsDiscIsNeverCaughtEvenInsideTheSilhouette()
    {
        var discs = new (double X, double Y, double R)[] { (50, 50, 10), (20, 20, 5) };

        Assert.False(BackdropHitTest.ShouldCatch(new Point(55, 50), Bounds, Square, discs, false, null));   // inside disc 1
        Assert.False(BackdropHitTest.ShouldCatch(new Point(22, 20), Bounds, Square, discs, false, null));   // inside disc 2
        Assert.True(BackdropHitTest.ShouldCatch(new Point(70, 70), Bounds, Square, discs, false, null));    // clear of both
        Assert.True(BackdropHitTest.InsideAnyDisc(new Point(60, 50), discs));                               // on the edge
        Assert.False(BackdropHitTest.InsideAnyDisc(new Point(61, 50), discs));
        Assert.False(BackdropHitTest.InsideAnyDisc(new Point(0, 0), NoDiscs));
    }

    [Fact]
    public void LocalDiscsAreRelativeToTheWindowAndRegionHolesAreDevicePixels()
    {
        var local = BackdropGeometry.LocalDiscs(new (double, double, double)[] { (150, 260, 18.2) }, new Point(100, 200));
        Assert.Equal(new (double, double, double)[] { (50, 60, 18.2) }, local);

        Assert.Equal(new[] { (100, 120, 37) }, BackdropGeometry.RegionHoles(local, 2));
        Assert.Empty(BackdropGeometry.RegionHoles(NoDiscs, 1));
    }

    [Fact]
    public void ThePolygonEdgeCountsAsInside()
    {
        Assert.True(BackdropHitTest.InsideSilhouette(new Point(10, 50), Bounds, Square));
    }

    [Fact]
    public void CursorMovedIsTrueForTheFirstSampleAndForAnyChange()
    {
        Assert.True(BackdropHitTest.CursorMoved(null, new Point(1, 1)));
        Assert.True(BackdropHitTest.CursorMoved(new Point(1, 1), new Point(1, 2)));
        Assert.False(BackdropHitTest.CursorMoved(new Point(1, 1), new Point(1, 1)));
    }

    // --- LowestOf ------------------------------------------------------------

    private static IntPtr P(int n) => new(n);

    [Fact]
    public void LowestOfIsTheRearmostWindowOfOursThatIsNotABackdrop()
    {
        // front to back: orb, arrow, [backdrop], someone else's, orb2
        var z = new[] { P(1), P(2), P(9), P(5), P(3) };
        var ours = new HashSet<IntPtr> { P(1), P(2), P(3), P(9) };
        var backdrops = new HashSet<IntPtr> { P(9) };

        Assert.Equal(P(3), BackdropOrder.LowestOf(z, ours, backdrops));
    }

    [Fact]
    public void LowestOfSkipsBackdropsEvenWhenTheyAreRearmost()
    {
        var z = new[] { P(1), P(2), P(9) };
        Assert.Equal(P(2), BackdropOrder.LowestOf(z, new HashSet<IntPtr> { P(1), P(2), P(9) }, new HashSet<IntPtr> { P(9) }));
    }

    [Fact]
    public void LowestOfIsNullWhenNoneOfOursIsOnScreen()
    {
        Assert.Null(BackdropOrder.LowestOf(new[] { P(5), P(6) }, new HashSet<IntPtr> { P(1) }, new HashSet<IntPtr>()));
        Assert.Null(BackdropOrder.LowestOf(Array.Empty<IntPtr>(), new HashSet<IntPtr>(), new HashSet<IntPtr>()));
    }

    // --- geometry ------------------------------------------------------------

    [Fact]
    public void CocoaCoordinatesAreFlippedAboutTheMainDisplayHeight()
    {
        Assert.Equal(new Point(100, 700), BackdropGeometry.CocoaToTopLeft(100, 200, 900));
    }

    [Fact]
    public void RegionPointsAreWindowRelativeAndScaledToDevicePixels()
    {
        var pts = BackdropGeometry.RegionPoints(new[] { new Point(110, 220), new Point(130.4, 250.6) }, new Point(100, 200), 2);
        Assert.Equal(new[] { (20, 40), (61, 101) }, pts);
    }

    [Fact]
    public void RegionPointsAtScaleOneAreJustTheOffset()
    {
        Assert.Equal(new[] { (5, 7) }, BackdropGeometry.RegionPoints(new[] { new Point(15, 27) }, new Point(10, 20), 1));
    }

    [Fact]
    public void ATranslatedDiscSetIsRecognisedWithItsOffset()
    {
        var a = new List<(double X, double Y, double R)> { (0, 0, 10), (50, 20, 7) };
        var b = a.Select(d => (d.X + 30, d.Y - 12, d.R)).ToList();

        Assert.Equal((30.0, -12.0), BackdropGeometry.TryTranslation(a, b));
    }

    [Fact]
    public void ADiscSetThatChangedShapeIsNotATranslation()
    {
        var a = new List<(double X, double Y, double R)> { (0, 0, 10), (50, 20, 7) };

        Assert.Null(BackdropGeometry.TryTranslation(a, new List<(double, double, double)> { (5, 5, 10), (50, 20, 7) }));   // one moved
        Assert.Null(BackdropGeometry.TryTranslation(a, new List<(double, double, double)> { (5, 5, 10), (55, 25, 8) }));   // one resized
        Assert.Null(BackdropGeometry.TryTranslation(a, new List<(double, double, double)> { (5, 5, 10) }));                // one fewer
        Assert.Null(BackdropGeometry.TryTranslation(a, new List<(double, double, double)> { (5, 5, 10), (55, 99, 7) }));   // y differs
        Assert.Null(BackdropGeometry.TryTranslation(
            Array.Empty<(double, double, double)>(), Array.Empty<(double, double, double)>()));
    }

    [Fact]
    public void ACurveFollowsTheDiscsOnlyWhenItMovedByTheSameOffsetAtTheSameScale()
    {
        var a = new GroupOutline.PlacedCurve(new Point(100, 200), 5);

        Assert.True(BackdropGeometry.CurveFollows(null, null, 3, 4));
        Assert.True(BackdropGeometry.CurveFollows(a, new GroupOutline.PlacedCurve(new Point(103, 204), 5), 3, 4));
        Assert.False(BackdropGeometry.CurveFollows(a, new GroupOutline.PlacedCurve(new Point(103, 204), 6), 3, 4));   // rescaled
        Assert.False(BackdropGeometry.CurveFollows(a, new GroupOutline.PlacedCurve(new Point(100, 204), 5), 3, 4));   // x stayed
        Assert.False(BackdropGeometry.CurveFollows(a, new GroupOutline.PlacedCurve(new Point(103, 200), 5), 3, 4));   // y stayed
        Assert.False(BackdropGeometry.CurveFollows(a, null, 3, 4));
        Assert.False(BackdropGeometry.CurveFollows(null, a, 3, 4));
    }

    // --- which groups get a backdrop ----------------------------------------------

    [Theory]
    [InlineData("g-1a2b3c4d", true, true)]
    [InlineData("g-1a2b3c4d", false, true)]    // user groups are live in free mode
    [InlineData("main", true, true)]
    [InlineData("main", false, false)]         // the predefined ones follow Arrange
    [InlineData("heartbeats", false, false)]
    [InlineData("crons", false, false)]
    [InlineData("heartbeats", true, true)]
    public void UserGroupsAlwaysHaveABackdropAndThePredefinedOnesOnlyWhileArranged(string id, bool arranged, bool shows)
    {
        Assert.Equal(shows, BackdropVisibility.Shows(id, arranged));
    }

    [Fact]
    public void OnlyTheGPrefixMakesAUserGroup()
    {
        Assert.True(BackdropVisibility.IsUserGroup("g-abcdef12"));
        Assert.False(BackdropVisibility.IsUserGroup("main"));
        Assert.False(BackdropVisibility.IsUserGroup("G-abcdef12"));
        Assert.False(BackdropVisibility.IsUserGroup(""));
    }

    // --- holes: the group's own orbs and any other orb reaching in -----------------

    [Fact]
    public void HolesAreTheGroupsOwnDiscsPlusEveryOtherOrbThatReachesIntoTheWindow()
    {
        var own = new (double X, double Y, double R)[] { (30, 30, 18) };
        var extra = new (double X, double Y, double R)[]
        {
            (500, 300, 18),    // window origin (400,200): local (100,100) — inside a 200x200 window
            (590, 300, 18),    // local (190,100): reaches in by its radius at the right edge
            (630, 300, 18),    // local (230,100): 230-18 = 212 > 200, clear of it
            (500, 150, 18),    // local (100,-50): wholly above the top edge
            (500, 175, 18),    // local (100,-25): -25 + 18 < 0, still wholly above it
            (380, 300, 18),    // local (-20,100): its right edge at -2, clear of the left edge
            (390, 300, 18),    // local (-10,100): its right edge at 8, reaches in
            (500, 430, 18),    // local (100,230): clear below
        };

        var holes = BackdropGeometry.HolesFor(own, extra, new Point(400, 200), 200, 200);

        Assert.Equal(new (double, double, double)[] { (30, 30, 18), (100, 100, 18), (190, 100, 18), (-10, 100, 18) }, holes);
    }

    [Fact]
    public void NoOtherOrbsLeavesJustTheGroupsOwnDiscs()
    {
        var own = new (double X, double Y, double R)[] { (30, 30, 18), (60, 60, 18) };
        Assert.Equal(own, BackdropGeometry.HolesFor(own, NoDiscs, default, 100, 100));
    }

    // --- drawing style -------------------------------------------------------

    private static readonly Color Stroke = Color.FromArgb(90, 255, 0, 0);
    private static readonly Color Fill = Color.FromArgb(15, 0, 0, 255);

    [Fact]
    public void OutlinesOnDrawTheResolvedStyle()
    {
        Assert.Equal((Stroke, Fill, BackdropStyle.StrokeDip), BackdropStyle.Compute(Stroke, Fill, false, true));
    }

    [Fact]
    public void OutlinesOffAreInvisible()
    {
        Assert.Equal((Colors.Transparent, Colors.Transparent, 0.0), BackdropStyle.Compute(Stroke, Fill, false, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HighlightIsFullStrokeOpacityAtTwiceTheWidthWhetherOrNotOutlinesAreOn(bool outlines)
    {
        var (stroke, fill, width) = BackdropStyle.Compute(Stroke, Fill, true, outlines);

        Assert.Equal(Color.FromArgb(255, 255, 0, 0), stroke);
        Assert.Equal(Fill, fill);
        Assert.Equal(BackdropStyle.StrokeDip * 2, width);
    }
}
