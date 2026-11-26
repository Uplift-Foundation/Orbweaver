using System;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// The team-as-spiderweb layout: a lead at the hub, its members on radial spokes,
// a new ring out each time the spokes fill.
//
// WebOffsets is the geometry and is pure; the arrangement tests below go through
// Compute to pin that FanOut actually uses it, and falls back to the one-sided
// fan where a full web does not fit.
public class OrbWebLayoutTests
{
    private const double Radius = 60;
    private const double MemberCircle = 26;

    [Fact]
    public void NoMembersIsNoOffsets()
    {
        Assert.Empty(OrbArrangement.WebOffsets(0, Radius, MemberCircle, 0));
        Assert.Empty(OrbArrangement.WebOffsets(-3, Radius, MemberCircle, 0));
    }

    [Fact]
    public void FirstSpokePointsStraightUp()
    {
        var (x, y) = OrbArrangement.WebOffsets(5, Radius, MemberCircle, 0)[0];

        Assert.Equal(0, x, 6);
        Assert.Equal(-Radius, y, 6);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void MembersWithinOneRingAreEvenlySpacedAtTheArrowRadius(int count)
    {
        var offsets = OrbArrangement.WebOffsets(count, Radius, MemberCircle, 0);

        Assert.All(offsets, o => Assert.Equal(Radius, Math.Sqrt(o.X * o.X + o.Y * o.Y), 6));

        var angles = offsets.Select(o => Math.Atan2(o.Y, o.X)).ToArray();

        for (var i = 1; i < count; i++)
        {
            var step = angles[i] - angles[i - 1];
            if (step < 0) step += Math.PI * 2;
            Assert.Equal(Math.PI * 2 / count, step, 6);
        }
    }

    [Fact]
    public void NeverMoreThanEightSpokes()
    {
        var offsets = OrbArrangement.WebOffsets(20, Radius, MemberCircle, 0);
        var distinct = offsets.Select(o => Math.Round(Math.Atan2(o.Y, o.X), 4)).Distinct().Count();

        Assert.Equal(OrbArrangement.WebSpokes, distinct);
    }

    [Fact]
    public void AFullRingPushesTheNextMemberOntoTheSameSpokeOneRingOut()
    {
        var offsets = OrbArrangement.WebOffsets(OrbArrangement.WebSpokes + 1, Radius, MemberCircle, 0);
        var inner = offsets[0];
        var outer = offsets[OrbArrangement.WebSpokes];

        // Same thread: same direction, further out by one ring step.
        Assert.Equal(Math.Atan2(inner.Y, inner.X), Math.Atan2(outer.Y, outer.X), 6);
        Assert.Equal(Radius + MemberCircle * 1.5, Math.Sqrt(outer.X * outer.X + outer.Y * outer.Y), 6);
    }

    [Fact]
    public void PhaseTurnsTheWholeWeb()
    {
        var plain = OrbArrangement.WebOffsets(4, Radius, MemberCircle, 0)[0];
        var quarter = OrbArrangement.WebOffsets(4, Radius, MemberCircle, Math.PI / 2)[0];

        // Up turned a quarter clockwise is right.
        Assert.Equal(Radius, quarter.X, 6);
        Assert.Equal(0, quarter.Y, 6);
        Assert.NotEqual(plain.X, quarter.X);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(30)]
    public void NoTwoMembersOfAWebTouch(int count)
    {
        var offsets = OrbArrangement.WebOffsets(count, Radius, MemberCircle, 0);

        for (var i = 0; i < count; i++)
        for (var j = i + 1; j < count; j++)
        {
            var d = Math.Sqrt(Math.Pow(offsets[i].X - offsets[j].X, 2) + Math.Pow(offsets[i].Y - offsets[j].Y, 2));
            Assert.True(d > MemberCircle, $"members {i} and {j} are {d:0.0} apart at {count} members");
        }
    }

    private static PixelPoint[] Team(int members, PixelRect work, PixelPoint? centre = null, string shape = "web")
    {
        var count = members + 1;
        var leadOf = new int[count];
        leadOf[0] = -1;
        for (var i = 1; i < count; i++) leadOf[i] = 0;

        return OrbArrangement.Compute(
            count, leadOf, new OrbArrangement.Layout(work, 1.0, shape, 1.0, centre));
    }

    private static double Distance(PixelPoint a, PixelPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    [Fact]
    public void ATeamOfFiveSurroundsItsLead()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var at = Team(5, work, new PixelPoint(960, 540));

        // Members all around the lead, not hanging off one side of it: the lead
        // lies inside the box the members span, on both axes.
        var members = at.Skip(1).ToArray();

        Assert.True(members.Min(p => p.X) < at[0].X && at[0].X < members.Max(p => p.X));
        Assert.True(members.Min(p => p.Y) < at[0].Y && at[0].Y < members.Max(p => p.Y));
    }

    [Fact]
    public void AWebsMembersAreOneArrowLengthFromTheirLead()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var at = Team(5, work, new PixelPoint(960, 540));
        var distances = at.Skip(1).Select(p => Distance(p, at[0])).ToArray();

        // Evenly on one ring, give or take Separate nudging an orb a pixel or two.
        Assert.True(distances.Max() - distances.Min() < 6, string.Join(", ", distances.Select(d => d.ToString("0"))));
    }

    [Fact]
    public void ATeamOfTwoKeepsTheOneSidedFan()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var at = Team(2, work, new PixelPoint(960, 540));

        // Two spokes would be a line through the lead; the fan keeps both on
        // the same side.
        var a = Math.Atan2(at[1].Y - at[0].Y, at[1].X - at[0].X);
        var b = Math.Atan2(at[2].Y - at[0].Y, at[2].X - at[0].X);
        var between = Math.Abs(Math.Atan2(Math.Sin(a - b), Math.Cos(a - b)));

        Assert.True(between < Math.PI / 2, $"members are {between:0.00} rad apart");
    }

    [Fact]
    public void AWebThatCannotFitFallsBackToTheFanAndStaysOnScreen()
    {
        // A strip too short to hold a full circle of radius one arrow.
        var work = new PixelRect(0, 0, 1920, 150);
        var at = Team(5, work, new PixelPoint(960, 40));

        Assert.All(at, p =>
        {
            Assert.InRange(p.X, work.X, work.Right);
            Assert.InRange(p.Y, work.Y, work.Bottom);
        });
    }
    [Fact]
    public void APartialWebStaysInsideItsSpanAndFacesThePhase()
    {
        // A half turn facing left (a quarter turn anticlockwise from up).
        var offsets = OrbArrangement.WebOffsets(4, Radius, MemberCircle, -Math.PI / 2, Math.PI);

        Assert.All(offsets, o => Assert.True(o.X < 0, $"({o.X:0.0}, {o.Y:0.0}) is not on the left"));
    }

    [Fact]
    public void APartialWebUsesFewerSpokesWhenTheyWouldCrowd()
    {
        // A quarter turn holds fewer than four neighbours at this spacing, so
        // the rest go on a second ring along the same spokes.
        var offsets = OrbArrangement.WebOffsets(8, Radius, MemberCircle, 0, Math.PI / 2);
        var rings = offsets.Select(o => Math.Round(Math.Sqrt(o.X * o.X + o.Y * o.Y))).Distinct().Count();

        Assert.True(rings >= 2, "expected the members to spill onto an outer ring");
    }

    [Fact]
    public void APartialWebAlwaysHasAtLeastOneSpoke()
    {
        // A span narrower than one neighbour gap still places everyone.
        var offsets = OrbArrangement.WebOffsets(3, Radius, MemberCircle, 0, 0.01);

        Assert.Equal(3, offsets.Length);
        Assert.All(offsets, o => Assert.False(double.IsNaN(o.X) || double.IsNaN(o.Y)));
    }

    [Fact]
    public void ReachCoversTheOutermostMember()
    {
        foreach (var count in new[] { 1, 3, 8, 9, 20 })
        {
            var reach = OrbArrangement.WebReach(count, Radius, MemberCircle);
            var farthest = OrbArrangement.WebOffsets(count, Radius, MemberCircle, 0)
                .Max(o => Math.Sqrt(o.X * o.X + o.Y * o.Y));

            Assert.True(reach >= farthest + MemberCircle / 2 - 1e-6, $"{count}: reach {reach:0.0} < {farthest:0.0}");
        }

        Assert.Equal(0, OrbArrangement.WebReach(0, Radius, MemberCircle));
    }

    [Fact]
    public void ALeadOnAColumnAtTheRightEdgeGetsAWebMostlyToItsLeft()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        const int window = 56;
        var lead = new PixelPoint(work.Right - window - 24, 400);

        var at = OrbArrangement.WebAround(
            lead, (lead.X + window / 2.0 + 100000, lead.Y + window / 2.0), 4, OrbArrangement.CircleDip, work, window);

        Assert.Equal(4, at.Length);
        // The web may wrap a little past the lead's own column — a three quarter
        // turn still fits — but most of it faces into the screen.
        Assert.True(at.Count(p => p.X < lead.X) >= 2, "most of the web should be left of the lead");
        Assert.All(at, p => Assert.InRange(p.X, work.X, work.Right - window));

        // Members spread over a range of heights: a fan, not a column.
        Assert.True(at.Max(p => p.Y) - at.Min(p => p.Y) > window);
    }

    [Fact]
    public void ALeadInTheTopRightCornerStillGetsAWebNotALine()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        const int window = 56;
        var lead = new PixelPoint(work.Right - window - 24, 24);

        var at = OrbArrangement.WebAround(
            lead, (lead.X + window / 2.0 + 100000, lead.Y + window / 2.0), 4, OrbArrangement.CircleDip, work, window);

        Assert.All(at, p =>
        {
            Assert.InRange(p.X, work.X, work.Right - window);
            Assert.InRange(p.Y, work.Y, work.Bottom - window);
        });

        // Spread across more than one row and more than one column.
        Assert.True(at.Select(p => p.X).Distinct().Count() > 1);
        Assert.True(at.Select(p => p.Y).Distinct().Count() > 1);
    }

    // ---- the web is the web shape's alone ------------------------------------

    [Theory]
    [InlineData("heart")]
    [InlineData("circle")]
    [InlineData("diamond")]
    [InlineData("star")]
    [InlineData("grid")]
    [InlineData("line")]
    [InlineData("vline")]
    public void EveryOtherShapeKeepsTheOneSidedFan(string shape)
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var at = Team(5, work, new PixelPoint(960, 540), shape);
        var members = at.Skip(1).Select(p => (X: (double)p.X - at[0].X, Y: (double)p.Y - at[0].Y)).ToArray();

        // A fan hangs off one side of its lead: some direction has every member
        // in front of it. A web surrounds the lead and has none.
        var oneSided = Enumerable.Range(0, 72).Any(i =>
        {
            var a = i * Math.PI / 36;
            return members.All(m => m.X * Math.Cos(a) + m.Y * Math.Sin(a) > 0);
        });

        Assert.True(oneSided, $"{shape}: members surround the lead, which only the web shape does");
    }

    [Fact]
    public void TheWebShapeSurroundsItsLeadWhereTheCircleShapeDoesNot()
    {
        var work = new PixelRect(0, 0, 1920, 1080);

        bool Surrounds(string shape)
        {
            var at = Team(5, work, new PixelPoint(960, 540), shape);
            var members = at.Skip(1).Select(p => (X: (double)p.X - at[0].X, Y: (double)p.Y - at[0].Y)).ToArray();

            return !Enumerable.Range(0, 72).Any(i =>
            {
                var a = i * Math.PI / 36;
                return members.All(m => m.X * Math.Cos(a) + m.Y * Math.Sin(a) > 0);
            });
        }

        Assert.True(Surrounds("web"));
        Assert.False(Surrounds("circle"));
    }

    [Fact]
    public void TheWebShapeGathersItsRootsOnTheSameRingAsTheCircle()
    {
        var web = OrbArrangement.Unit("web", 9);
        var circle = OrbArrangement.Unit("circle", 9);

        Assert.Equal(circle, web);
    }

    [Fact]
    public void OnlyTheWebShapeIsAWeb()
    {
        Assert.True(OrbArrangement.IsWeb("web"));
        Assert.False(OrbArrangement.IsWeb("circle"));
        Assert.False(OrbArrangement.IsWeb(null));
        Assert.False(OrbArrangement.IsWeb("Web"));
    }
}
