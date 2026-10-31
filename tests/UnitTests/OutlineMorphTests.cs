using System;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 M3/M1: the pure maths of the outline morph and the highlight's breathing.
public class OutlineMorphTests
{
    private static Point[] Square(double half, Point c) =>
        new[] { new Point(c.X - half, c.Y - half), new Point(c.X + half, c.Y - half), new Point(c.X + half, c.Y + half), new Point(c.X - half, c.Y + half) };

    private static Point[] Circle(double r, Point c, int n = 360) =>
        Enumerable.Range(0, n).Select(i => new Point(c.X + r * Math.Sin(2 * Math.PI * i / n), c.Y - r * Math.Cos(2 * Math.PI * i / n))).ToArray();

    // A real heart outline, the shape the morph has to cope with.
    private static Point[] Heart()
    {
        var curve = new GroupOutline.PlacedCurve(new Point(500, 400), 8);
        var discs = Enumerable.Range(0, 10).Select(i =>
        {
            var (x, y) = OrbArrangement.HeartAt(2 * Math.PI * (i + 0.3) / 10);
            return (500 + 8 * x, 400 + 8 * y, 18.0);
        }).ToList();
        return GroupOutline.Silhouette("heart", discs, 10, curve);
    }

    // ---- centroid and area -------------------------------------------------------

    [Fact]
    public void TheCentroidOfAPolygonIsItsAreaCentroid()
    {
        var c = OutlineMorph.Centroid(Square(10, new Point(100, 50)));
        Assert.Equal(100, c.X, 9);
        Assert.Equal(50, c.Y, 9);

        var tri = OutlineMorph.Centroid(new[] { new Point(0, 0), new Point(30, 0), new Point(0, 30) });
        Assert.Equal(10, tri.X, 9);
        Assert.Equal(10, tri.Y, 9);
    }

    [Fact]
    public void AZeroAreaPolygonHasItsVertexMeanForACentre()
    {
        var c = OutlineMorph.Centroid(new[] { new Point(0, 0), new Point(10, 0), new Point(20, 0) });
        Assert.Equal(10, c.X, 9);
        Assert.Equal(0, c.Y, 9);
    }

    [Fact]
    public void AreaIsTheShoelaceAreaAndGrowingComparesIt()
    {
        Assert.Equal(400, OutlineMorph.Area(Square(10, default)), 9);
        Assert.True(OutlineMorph.IsGrowing(Square(10, default), Square(20, default)));
        Assert.False(OutlineMorph.IsGrowing(Square(20, default), Square(10, default)));
        Assert.True(OutlineMorph.IsGrowing(Square(10, default), Square(10, new Point(5, 5))));   // equal counts as a swell
    }

    // ---- resampling ------------------------------------------------------------------

    [Fact]
    public void ACircleResamplesToEqualRadiiAboutItsCentre()
    {
        var p = OutlineMorph.Resample(Circle(50, new Point(200, 100)));

        Assert.Equal(OutlineMorph.Samples, p.Radii.Length);
        Assert.Equal(200, p.Centre.X, 6);
        Assert.Equal(100, p.Centre.Y, 6);
        Assert.All(p.Radii, r => Assert.InRange(r, 49.9, 50.0001));   // a 360-gon sags a hair inside
    }

    [Fact]
    public void ASquareResamplesToItsFlatsAndItsCorners()
    {
        var p = OutlineMorph.Resample(Square(10, default));

        Assert.Equal(10, p.Radii[0], 6);                                    // straight up: the middle of the top edge
        Assert.Equal(10, p.Radii[OutlineMorph.Samples / 4], 6);             // right
        Assert.Equal(10 * Math.Sqrt(2), p.Radii[OutlineMorph.Samples / 8], 6);   // the corner at 45 degrees
    }

    // Aligned by angle about each polygon's OWN centroid: moving a polygon moves the
    // centre and leaves the radii exactly as they were, so a join that also shifts
    // the centre morphs without twisting.
    [Fact]
    public void ResamplingIsAlignedByAngleAboutTheCentroidSoATranslationOnlyMovesTheCentre()
    {
        var heart = Heart();
        var moved = heart.Select(v => new Point(v.X + 123.5, v.Y - 77.25)).ToArray();

        var a = OutlineMorph.Resample(heart);
        var b = OutlineMorph.Resample(moved);

        Assert.Equal(a.Centre.X + 123.5, b.Centre.X, 6);
        Assert.Equal(a.Centre.Y - 77.25, b.Centre.Y, 6);
        for (var k = 0; k < OutlineMorph.Samples; k++) Assert.Equal(a.Radii[k], b.Radii[k], 6);
    }

    [Fact]
    public void EveryResampledVertexOfAHeartLiesOnItsOutline()
    {
        var heart = Heart();
        var frame = OutlineMorph.Frame(OutlineMorph.Resample(heart), OutlineMorph.Resample(heart), 1);

        Assert.All(frame, v => Assert.True(Math.Abs(GroupOutline.SignedDistanceInside(v, heart)) < 0.05,
            $"{GroupOutline.SignedDistanceInside(v, heart)}"));
    }

    // ---- frames ----------------------------------------------------------------------------

    [Fact]
    public void ProgressZeroIsTheFromPolygonAndOneIsTheTarget()
    {
        var from = OutlineMorph.Resample(Circle(40, new Point(100, 100)));
        var to = OutlineMorph.Resample(Circle(90, new Point(160, 130)));

        var start = OutlineMorph.Frame(from, to, 0);
        var end = OutlineMorph.Frame(from, to, 1);

        Assert.Equal(OutlineMorph.Samples, start.Length);
        for (var k = 0; k < start.Length; k += 13)
        {
            var ang = 2 * Math.PI * k / OutlineMorph.Samples;
            Assert.Equal(100 + from.Radii[k] * Math.Sin(ang), start[k].X, 9);
            Assert.Equal(160 + to.Radii[k] * Math.Sin(ang), end[k].X, 9);
            Assert.Equal(130 - to.Radii[k] * Math.Cos(ang), end[k].Y, 9);
        }
    }

    [Fact]
    public void AHalfwayFrameIsHalfwayInCentreAndEveryRadius()
    {
        var from = OutlineMorph.Resample(Circle(40, new Point(100, 100)));
        var to = OutlineMorph.Resample(Circle(80, new Point(200, 100)));
        var mid = OutlineMorph.Frame(from, to, 0.5);

        // The centre of the frame is the mean of the polygon's own vertices at a circle.
        Assert.Equal(150, mid.Average(v => v.X), 1);
        Assert.Equal(100, mid.Average(v => v.Y), 1);
        Assert.Equal(60, GroupOutline.Bounds(mid).Width / 2, 0);
    }

    [Fact]
    public void OvershootExtrapolatesAndARadiusNeverGoesNegative()
    {
        var from = OutlineMorph.Resample(Circle(40, default));
        var to = OutlineMorph.Resample(Circle(80, default));

        Assert.True(GroupOutline.Bounds(OutlineMorph.Frame(from, to, 1.2)).Width / 2 > 80);   // swells past its target
        Assert.True(GroupOutline.Bounds(OutlineMorph.Frame(from, to, -0.5)).Width / 2 < 40);

        var collapsed = OutlineMorph.Frame(from, to, -5);                                      // 40 + 40 * -5 < 0
        Assert.All(collapsed, v => Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y)));
        Assert.Equal(0, GroupOutline.Bounds(collapsed).Width, 9);
    }

    // ---- the spring ----------------------------------------------------------------------------

    [Theory]
    [InlineData(BackdropMotion.JoinOmega, BackdropMotion.JoinDamping)]
    [InlineData(BackdropMotion.LeaveOmega, BackdropMotion.LeaveDamping)]
    public void EverySpringIsSettledByTheEndOfTheMorph(double omega, double damping)
    {
        Assert.Equal(0, OutlineMorph.SpringProgress(0, omega, damping));
        Assert.Equal(0, OutlineMorph.SpringProgress(-1, omega, damping));
        Assert.InRange(OutlineMorph.SpringProgress(BackdropMotion.MorphSeconds, omega, damping), 0.99, 1.01);
    }

    [Fact]
    public void AJoinSwellsPastItsTargetByAlittleAndALeaveWobbles()
    {
        double Peak(double w, double z) => Enumerable.Range(1, 300).Max(i => OutlineMorph.SpringProgress(i / 1000.0, w, z));
        double Trough(double w, double z)
        {
            // The lowest value after the first peak: the undershoot of a wobble.
            var samples = Enumerable.Range(1, 300).Select(i => OutlineMorph.SpringProgress(i / 1000.0, w, z)).ToList();
            var peak = samples.IndexOf(samples.Max());
            return samples.Skip(peak).Min();
        }

        var joinPeak = Peak(BackdropMotion.JoinOmega, BackdropMotion.JoinDamping);
        var leavePeak = Peak(BackdropMotion.LeaveOmega, BackdropMotion.LeaveDamping);

        Assert.InRange(joinPeak, 1.01, 1.08);        // a swell, not a bounce
        Assert.InRange(leavePeak, 1.08, 1.25);       // a wobble
        Assert.True(Trough(BackdropMotion.LeaveOmega, BackdropMotion.LeaveDamping) < 0.995);   // and it comes back under
    }

    [Fact]
    public void ADampingOutsideTheValidRangeStillGivesAFiniteProgress()
    {
        Assert.True(double.IsFinite(OutlineMorph.SpringProgress(0.1, 30, 5)));
        Assert.True(double.IsFinite(OutlineMorph.SpringProgress(0.1, 30, 0)));
    }

    // The spring is Spring.Step, so it is the same whether it is asked once or in
    // many steps, and it lands exactly on 1 rather than a hair short.
    [Fact]
    public void TheSpringAgreesWithSpringStepAndLandsExactlyOnOne()
    {
        var p = new SpringParams(BackdropMotion.JoinOmega, BackdropMotion.JoinDamping, 0.002, 0.05);
        var stepped = default(SpringState);
        for (var i = 0; i < 12; i++) stepped = Spring.Step(stepped, 1, 0.01, p);

        Assert.Equal(stepped.Value, OutlineMorph.SpringProgress(0.12, BackdropMotion.JoinOmega, BackdropMotion.JoinDamping), 9);

        Assert.False(OutlineMorph.AtRest(0.05, BackdropMotion.JoinOmega, BackdropMotion.JoinDamping));
        Assert.Equal(1.0, OutlineMorph.SpringProgress(2.0, BackdropMotion.JoinOmega, BackdropMotion.JoinDamping));
        Assert.True(OutlineMorph.AtRest(2.0, BackdropMotion.JoinOmega, BackdropMotion.JoinDamping));
    }

    // ---- breathing ------------------------------------------------------------------------------

    [Fact]
    public void BreathingStartsAtTrueSizeReachesFourPercentAndRepeats()
    {
        Assert.Equal(1.00, OutlineMorph.Breath(0), 9);
        Assert.Equal(1.04, OutlineMorph.Breath(OutlineMorph.BreathPeriodSeconds / 2), 9);
        Assert.Equal(1.00, OutlineMorph.Breath(OutlineMorph.BreathPeriodSeconds), 9);
        Assert.Equal(OutlineMorph.Breath(0.3), OutlineMorph.Breath(0.3 + 3 * OutlineMorph.BreathPeriodSeconds), 9);

        Assert.All(Enumerable.Range(0, 400), i =>
            Assert.InRange(OutlineMorph.Breath(i * 0.011), 1.00 - 1e-12, 1.04 + 1e-12));
    }
}
