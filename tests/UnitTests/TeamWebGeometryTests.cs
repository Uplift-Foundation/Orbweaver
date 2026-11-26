using System;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// TeamWebGeometry: the silk drawn between a lead and its members.
//
// Pure arithmetic on offsets from the lead, so every rule — which gaps get a spoke
// of their own, which side of a partial web stays open, which members count as one
// ring — can be asserted without a window.
public class TeamWebGeometryTests
{
    private const double Hub = 18;

    private static Point Polar(double degrees, double radius)
    {
        var a = degrees * Math.PI / 180;
        return new Point(radius * Math.Cos(a), radius * Math.Sin(a));
    }

    private static double Length(Point p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);

    [Fact]
    public void NoMembersIsNoWeb()
    {
        Assert.Null(TeamWebGeometry.Build(Array.Empty<Point>(), Hub));
    }

    [Fact]
    public void AMemberOnTheLeadsCentreHasNoDirectionAndIsIgnored()
    {
        Assert.Null(TeamWebGeometry.Build(new[] { new Point(0, 0) }, Hub));
    }

    [Fact]
    public void FourMembersAtTheCompassPointsBecomeEightSpokes()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(90, 60), Polar(180, 60), Polar(270, 60) }, Hub)!;

        // A plus sign is four spokes; a spoke in every gap makes it a web.
        Assert.Equal(8, frame.Spokes.Count);
    }

    [Fact]
    public void AFullWebsRingIsClosed()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(90, 60), Polar(180, 60), Polar(270, 60) }, Hub)!;

        // Eight spokes round one ring: eight threads, the last back to the first.
        Assert.Equal(8, frame.Rings.Count);

        var ends = frame.Rings.Select(r => r.To).ToList();
        foreach (var strand in frame.Rings) Assert.Contains(ends, e => Length(new Point(e.X - strand.From.X, e.Y - strand.From.Y)) < 1e-6);
    }

    [Fact]
    public void ThreadsSagTowardsTheLead()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(120, 60), Polar(240, 60) }, Hub)!;

        Assert.All(frame.Rings, r =>
        {
            var chordMiddle = new Point((r.From.X + r.To.X) / 2, (r.From.Y + r.To.Y) / 2);
            Assert.True(Length(r.Control) < Length(chordMiddle), "control point should be pulled in");
        });
    }

    [Fact]
    public void ThreeMembersOnAFullWebCloseWithoutExtraSpokes()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(120, 60), Polar(240, 60) }, Hub)!;

        // 120 degrees is wider than a spoke gap, so one spoke is added in each.
        Assert.Equal(6, frame.Spokes.Count);
        Assert.Equal(6, frame.Rings.Count);
    }

    [Fact]
    public void APartialWebLeavesItsOpenSideOpen()
    {
        // Four members spread over 135 degrees on the left: the rest is open.
        var frame = TeamWebGeometry.Build(
            new[] { Polar(112.5, 60), Polar(157.5, 60), Polar(202.5, 60), Polar(247.5, 60) }, Hub)!;

        // Neighbours are 45 degrees apart, so no spokes are added, and the three
        // gaps between them are joined; the wide open gap is not.
        Assert.Equal(4, frame.Spokes.Count);
        Assert.Equal(3, frame.Rings.Count);
    }

    [Fact]
    public void MembersAtNearlyTheSameDistanceAreOneRing()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(90, 63), Polar(180, 58), Polar(270, 61) }, Hub)!;

        // One ring's worth of threads, not four rings of their own.
        Assert.Equal(8, frame.Rings.Count);
    }

    [Fact]
    public void ALoneMemberOnAnOuterRingGetsASpokeButNoThread()
    {
        var members = Enumerable.Range(0, 8).Select(i => Polar(i * 45, 60)).Append(Polar(0, 100)).ToArray();
        var frame = TeamWebGeometry.Build(members, Hub)!;

        // The ninth is alone out at 100: the ring at 60 is threaded, and 100 is not.
        Assert.Equal(8, frame.Rings.Count);

        // Its spoke runs out to it rather than stopping at the ring.
        Assert.Contains(frame.Spokes, s => Math.Abs(Length(s.To) - 100) < 1e-6);
        Assert.Equal(100, frame.Reach, 6);
    }

    [Fact]
    public void SpokesStartAtTheLeadsRimAndRunToTheOutermostRing()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(90, 60), Polar(180, 60), Polar(270, 60) }, Hub)!;

        Assert.All(frame.Spokes, s =>
        {
            Assert.Equal(Hub + TeamWebGeometry.SpokeStart, Length(s.From), 6);
            Assert.Equal(60, Length(s.To), 6);
        });
    }

    [Fact]
    public void ReachIsTheFarthestPointDrawn()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(90, 60), Polar(180, 60), Polar(270, 60) }, Hub)!;

        Assert.Equal(60, frame.Reach, 6);
    }

    [Fact]
    public void SeveralMembersOnOneSpokeAreOneSpoke()
    {
        var frame = TeamWebGeometry.Build(
            new[] { Polar(0, 60), Polar(0, 100), Polar(0, 140) }, Hub)!;

        Assert.Single(frame.Spokes);
        Assert.Empty(frame.Rings);
        Assert.Equal(140, Length(frame.Spokes[0].To), 6);
    }

    [Fact]
    public void AMemberJustPastTheSeamIsNotCountedTwice()
    {
        // 179.99 and -179.99 degrees are the same direction either side of the
        // atan2 seam.
        var frame = TeamWebGeometry.Build(new[] { Polar(179.99, 60), Polar(-179.99, 60) }, Hub)!;

        Assert.Single(frame.Spokes);
    }

    [Fact]
    public void AMemberInsideTheLeadsRimDrawsNoSpoke()
    {
        var frame = TeamWebGeometry.Build(new[] { Polar(0, Hub) }, Hub)!;

        Assert.Empty(frame.Spokes);
    }

    [Fact]
    public void EachSpokeSaysWhichMembersSitOnIt()
    {
        var members = new[] { Polar(0, 60), Polar(90, 60), Polar(180, 60), Polar(270, 60), Polar(0, 100) };
        var frame = TeamWebGeometry.Build(members, Hub)!;

        Assert.Equal(frame.Spokes.Count, frame.SpokeMembers.Count);

        // The spoke along 0 degrees carries members 0 and 4; the four spokes the
        // frame added between them carry nobody.
        Assert.Contains(frame.SpokeMembers, m => m.SequenceEqual(new[] { 0, 4 }));
        Assert.Equal(4, frame.SpokeMembers.Count(m => m.Count == 0));
        Assert.Equal(members.Length, frame.SpokeMembers.SelectMany(m => m).Distinct().Count());
    }
}
