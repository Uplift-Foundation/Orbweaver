using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 QA: GroupOutline.MinimumEnclosingCircle against an independent coarse-to-fine search.
public class MinimumCircleQaTests
{
    private static double Reach(IReadOnlyList<(double X, double Y, double R)> d, double x, double y) =>
        d.Max(c => Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y)) + c.R);

    private static double Reference(IReadOnlyList<(double X, double Y, double R)> d)
    {
        double cx = d.Average(c => c.X), cy = d.Average(c => c.Y);
        var span = Math.Max(d.Max(c => c.X) - d.Min(c => c.X), d.Max(c => c.Y) - d.Min(c => c.Y)) + 1;
        for (var it = 0; it < 120; it++)
        {
            double bx = cx, by = cy, best = Reach(d, cx, cy);
            for (var i = -6; i <= 6; i++)
                for (var j = -6; j <= 6; j++)
                {
                    var x = cx + i * span / 6; var y = cy + j * span / 6;
                    var r = Reach(d, x, y);
                    if (r < best) { best = r; bx = x; by = y; }
                }
            cx = bx; cy = by; span *= 0.7;
        }
        return Reach(d, cx, cy);
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 40).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EnclosesEveryDiscAndIsMinimalToWithinAThousandth(int seed)
    {
        var rng = new Random(seed);
        var n = 2 + rng.Next(14);
        var scale = new[] { 1.0, 50.0, 2000.0, 100000.0 }[seed % 4];
        var discs = Enumerable.Range(0, n)
            .Select(_ => (X: rng.NextDouble() * scale, Y: rng.NextDouble() * scale, R: 5 + rng.NextDouble() * 40))
            .ToList();

        var (c, r) = GroupOutline.MinimumEnclosingCircle(discs);

        foreach (var d in discs)
            Assert.True(Math.Sqrt((c.X - d.X) * (c.X - d.X) + (c.Y - d.Y) * (c.Y - d.Y)) + d.R <= r + 1e-6 * Math.Max(1, scale),
                $"seed {seed}: disc not enclosed");
        var reference = Reference(discs);
        Assert.True(r <= reference * 1.001 + 0.01, $"seed {seed} scale {scale}: r={r} reference={reference}");
    }

    [Fact]
    public void ControlADeliberatelyLargerCircleWouldFailTheMinimalityMeasure()
    {
        var discs = new List<(double X, double Y, double R)> { (0, 0, 20), (400, 0, 20) };
        var (_, r) = GroupOutline.MinimumEnclosingCircle(discs);
        Assert.True(r <= Reference(discs) * 1.001 + 0.01);
        Assert.False(r * 1.05 <= Reference(discs) * 1.001 + 0.01);
    }

    [Fact]
    public void NestedAndIdenticalAndCollinearAreHandled()
    {
        var nested = new List<(double X, double Y, double R)> { (100, 100, 200), (110, 100, 10), (90, 95, 5) };
        var (cn, rn) = GroupOutline.MinimumEnclosingCircle(nested);
        Assert.Equal(200, rn, 3);
        Assert.Equal(100, cn.X, 2);

        var same = new List<(double X, double Y, double R)> { (5, 5, 18), (5, 5, 18) };
        Assert.Equal(18, GroupOutline.MinimumEnclosingCircle(same).Radius, 6);

        var line = Enumerable.Range(0, 6).Select(i => (X: i * 100.0, Y: 0.0, R: 18.0)).ToList();
        var (cl, rl) = GroupOutline.MinimumEnclosingCircle(line);
        Assert.Equal(268, rl, 2);
        Assert.Equal(250, cl.X, 2);
    }

    [Fact]
    public void TheMinimumCircleShapeSilhouetteEnclosesEveryDiscWithPadding()
    {
        var rng = new Random(7);
        var discs = Enumerable.Range(0, 9).Select(_ => (X: rng.NextDouble() * 900, Y: rng.NextDouble() * 500, R: 13.0 + rng.Next(6))).ToList();
        var outline = GroupOutline.Silhouette(GroupOutline.MinimumCircleShape, discs, 8);
        foreach (var d in discs)
            Assert.True(GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), outline) >= d.R + 8 - 0.5);
    }
}
