using System;
using System.Linq;
using Avalonia;
using Xunit;
using Xunit.Abstractions;

namespace Orbweaver.UnitTests;

// CB-268 QA, free-mode obstacles (S2). Contract (docs/cb268-contracts.md): with obstacles there is NO
// Bands+Compact fallback; unseparable groups stay at their best-effort SeparateGroups position.
public class GroupObstaclesQaTests
{
    private readonly ITestOutputHelper _out;
    public GroupObstaclesQaTests(ITestOutputHelper o) => _out = o;

    private static readonly PixelRect Work = new(0, 0, 1920, 1080);

    [Fact]
    public void AnUnseparableGroupKeepsItsAnchorNeighbourhoodInsteadOfBeingRebanded()
    {
        var obstacles = new[] { new PixelRect(0, 0, 910, 1080), new PixelRect(1010, 0, 910, 1080) };  // 100 px strip left
        var asked = new PixelPoint(200, 300);
        var groups = new[]
        {
            new OrbArrangement.GroupLayout("circle", null),     // main: empty
            new OrbArrangement.GroupLayout("circle", null),
            new OrbArrangement.GroupLayout("circle", null),
            new OrbArrangement.GroupLayout("circle", asked),    // a user group
        };
        var n = 6;
        var (at, placed) = OrbArrangement.ComputeGroups(
            n, Enumerable.Repeat(-1, n).ToArray(), Enumerable.Repeat(3, n).ToArray(), groups,
            new OrbArrangement.Layout(Work, 1.0, "circle", 1.0), null, OrbArrangement.GroupLayoutMode.Free, obstacles, out var path);

        _out.WriteLine($"path={path} landed={placed[0].Landed} asked={asked} orbs x {at.Min(p => p.X)}..{at.Max(p => p.X)} y {at.Min(p => p.Y)}..{at.Max(p => p.Y)}");
        var landed = placed[0].Landed;
        var dist = Math.Sqrt(Math.Pow(landed.X - asked.X, 2) + Math.Pow(landed.Y - asked.Y, 2));
        Assert.Equal(OrbArrangement.GroupPath.Fallback, path);
        // The DRAWN orbs, not just Landed, must stay in the anchor's neighbourhood (best effort), never jump to the screen middle.
        var cx = (at.Min(p => p.X) + at.Max(p => p.X)) / 2.0; var cy = (at.Min(p => p.Y) + at.Max(p => p.Y)) / 2.0;
        var drawnDist = Math.Sqrt(Math.Pow(cx - asked.X, 2) + Math.Pow(cy - asked.Y, 2));
        Assert.True(drawnDist < 900, $"orbs drawn around ({cx:F0},{cy:F0}), {drawnDist:F0}px from the saved anchor {asked}");
        Assert.True(dist < 900, $"Landed {landed} is {dist:F0}px from the saved anchor {asked}: the group was rebanded, not left at best effort");
    }

    [Fact]
    public void ControlWithoutObstaclesAnAnchoredGroupLandsAtItsAnchor()
    {
        var asked = new PixelPoint(600, 500);
        var groups = new[] { new OrbArrangement.GroupLayout("circle", asked) };
        var n = 6;
        var (_, placed) = OrbArrangement.ComputeGroups(
            n, Enumerable.Repeat(-1, n).ToArray(), new int[n], groups,
            new OrbArrangement.Layout(Work, 1.0, "circle", 1.0), null, OrbArrangement.GroupLayoutMode.Free, Array.Empty<PixelRect>(), out var path);
        Assert.Equal(OrbArrangement.GroupPath.Separated, path);
        Assert.True(Math.Abs(placed[0].Landed.X - asked.X) < 5 && Math.Abs(placed[0].Landed.Y - asked.Y) < 5);
    }

    [Fact]
    public void AnObstacleOnTheAnchorPushesTheGroupClearAndDoesNotMoveTheObstacle()
    {
        var anchor = new PixelPoint(960, 540);
        var obstacle = new PixelRect(900, 480, 120, 120);   // right on the anchor
        var n = 4;
        var (at, placed) = OrbArrangement.ComputeGroups(
            n, Enumerable.Repeat(-1, n).ToArray(), new int[n], new[] { new OrbArrangement.GroupLayout("circle", anchor) },
            new OrbArrangement.Layout(Work, 1.0, "circle", 1.0), null, OrbArrangement.GroupLayoutMode.Free, new[] { obstacle }, out var path);

        Assert.Equal(OrbArrangement.GroupPath.Separated, path);
        var box = placed[0].SlotBounds;
        Assert.False(box.Intersects(obstacle), $"box {box} overlaps obstacle {obstacle}");
    }

    [Fact]
    public void AnObstacleHangingOutsideTheWorkAreaIsStillRespectedAndTheGroupStaysInside()
    {
        var obstacle = new PixelRect(1700, -200, 600, 1500);   // overhangs right, top and bottom
        var n = 5;
        var (_, placed) = OrbArrangement.ComputeGroups(
            n, Enumerable.Repeat(-1, n).ToArray(), new int[n], new[] { new OrbArrangement.GroupLayout("heart", new PixelPoint(1800, 540)) },
            new OrbArrangement.Layout(Work, 1.0, "heart", 1.0), null, OrbArrangement.GroupLayoutMode.Free, new[] { obstacle }, out var path);

        var box = placed[0].SlotBounds;
        Assert.Equal(OrbArrangement.GroupPath.Separated, path);
        Assert.False(box.Intersects(obstacle));
        Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= 1920 && box.Bottom <= 1080, $"box {box} left the work area");
    }

    [Fact]
    public void ObstaclesAreDeterministicAndOrderOfObstaclesDoesNotChangeTheAnswer()
    {
        var a = new PixelRect(1500, 0, 420, 1080);
        var b = new PixelRect(100, 100, 150, 150);
        var n = 8;
        (PixelPoint[], OrbArrangement.GroupPlacement[]) Run(PixelRect[] obs) => OrbArrangement.ComputeGroups(
            n, Enumerable.Repeat(-1, n).ToArray(), Enumerable.Range(0, n).Select(i => i % 2).ToArray(),
            new[] { new OrbArrangement.GroupLayout("circle", new PixelPoint(1600, 500)), new OrbArrangement.GroupLayout("star", new PixelPoint(200, 200)) },
            new OrbArrangement.Layout(Work, 1.0, "circle", 1.0), null, OrbArrangement.GroupLayoutMode.Free, obs);
        var one = Run(new[] { a, b });
        var two = Run(new[] { b, a });
        Assert.Equal(one.Item1, two.Item1);
        Assert.Equal(one.Item1, Run(new[] { a, b }).Item1);
    }

    // Hana's contract: Landed is where the group was drawn, so feeding it back reproduces the layout to the pixel -
    // including on the no-room route, and with hand-placed members applied.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FeedingLandedBackReproducesTheLayoutWithObstaclesAndHandPlacedMembers(bool cramped, bool offsets)
    {
        var obstacles = cramped
            ? new[] { new PixelRect(0, 0, 910, 1080), new PixelRect(1010, 0, 910, 1080) }
            : new[] { new PixelRect(1500, 0, 420, 1080), new PixelRect(300, 300, 150, 150) };
        var n = 6;
        var leadOf = new[] { -1, 0, 0, -1, 3, 3 };
        var groupOf = new[] { 0, 0, 0, 1, 1, 1 };
        var off = offsets ? new (double, double)?[] { null, (180, 90), null, null, (-150, 60), null } : null;
        PixelPoint? Ask(int g) => g == 0 ? new PixelPoint(300, 600) : new PixelPoint(1300, 300);

        (PixelPoint[] At, OrbArrangement.GroupPlacement[] G, OrbArrangement.GroupPath P) Go(Func<int, PixelPoint?> centre)
        {
            var (at, g) = OrbArrangement.ComputeGroups(n, leadOf, groupOf,
                new[] { new OrbArrangement.GroupLayout("circle", centre(0)), new OrbArrangement.GroupLayout("heart", centre(1)) },
                new OrbArrangement.Layout(Work, 1.0, "circle", 1.0), null, OrbArrangement.GroupLayoutMode.Free, obstacles, off, out var path);
            return (at, g, path);
        }

        var first = Go(Ask);
        var landed = first.G.ToDictionary(x => x.Slot, x => x.Landed);
        var second = Go(g => landed[g]);
        Assert.Equal(first.At, second.At);
        // Third pass too: a fixed point, not a one-off.
        var landed2 = second.G.ToDictionary(x => x.Slot, x => x.Landed);
        Assert.Equal(landed, landed2);
    }
}

// Arranged mode with obstacles (S2 explicit mode parameter).
public class ArrangedModeQaTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1080);

    private static (PixelPoint[] At, OrbArrangement.GroupPlacement[] G, OrbArrangement.GroupPath P) Go(
        PixelRect[] obstacles, bool anchored = true)
    {
        var n = 9;
        var leadOf = new[] { -1, 0, 0, -1, 3, -1, -1, 6, 6 };
        var groupOf = new[] { 0, 0, 0, 1, 1, 2, 2, 2, 2 };
        PixelPoint? A(int x, int y) => anchored ? new PixelPoint(x, y) : null;
        var (at, g) = OrbArrangement.ComputeGroups(n, leadOf, groupOf,
            new[]
            {
                new OrbArrangement.GroupLayout("heart", A(500, 400)),
                new OrbArrangement.GroupLayout("circle", A(1300, 600)),
                new OrbArrangement.GroupLayout("star", A(900, 800)),
            },
            new OrbArrangement.Layout(Work, 1.0, "circle", 1.0), null,
            OrbArrangement.GroupLayoutMode.Arranged, obstacles, null, out var path);
        return (at, g, path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEmptyObstacleListAndAHarmlessOffScreenObstacleGiveTheSameArrangedOutput(bool anchored)
    {
        var empty = Go(Array.Empty<PixelRect>(), anchored);
        var harmless = Go(new[] { new PixelRect(-9000, -9000, 100, 100) }, anchored);
        Assert.Equal(empty.At, harmless.At);
        Assert.Equal(empty.G.Select(x => x.Landed), harmless.G.Select(x => x.Landed));
        Assert.Equal(empty.P, harmless.P);
        // Determinism.
        Assert.Equal(empty.At, Go(Array.Empty<PixelRect>(), anchored).At);
    }

    [Fact]
    public void ArrangedWithAnObstacleKeepsEveryGroupBoxClearOfItWhenSeparated()
    {
        var obstacle = new PixelRect(400, 300, 250, 250);        // on the first group's anchor
        var r = Go(new[] { obstacle });
        Assert.Equal(OrbArrangement.GroupPath.Separated, r.P);
        foreach (var g in r.G) Assert.False(g.SlotBounds.Intersects(obstacle), $"slot {g.Slot} box {g.SlotBounds} overlaps {obstacle}");
        // Control: without the obstacle the first group's box does sit on that rectangle.
        Assert.True(Go(Array.Empty<PixelRect>()).G.First(g => g.Slot == 0).SlotBounds.Intersects(obstacle));
    }

    [Fact]
    public void ArrangedWithNoRoomKeepsTheBandsFallbackAndLandedIsStillReportedForEveryGroup()
    {
        var wall = new[] { new PixelRect(0, 0, 940, 1080), new PixelRect(980, 0, 940, 1080) };    // a 40 px strip
        var r = Go(wall);
        Assert.Equal(OrbArrangement.GroupPath.Fallback, r.P);
        Assert.Equal(3, r.G.Length);
        Assert.All(r.At, p => Assert.True(p.X >= 0 && p.Y >= 0 && p.X <= 1920 && p.Y <= 1080));
    }
}
