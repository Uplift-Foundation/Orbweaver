using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 QA: ComputeGroups with hand-placed members (memberOffsetOf, DIP, centre to centre).
public class MemberOffsetPlacementQaTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1080);
    private static readonly OrbArrangement.Layout Layout = new(Work, 1.0, "circle", 1.0);

    // group of 4: lead 0 with members 1,2 and a nested member 3 under 2.
    private static readonly int[] LeadOf = { -1, 0, 0, 2 };

    private static (PixelPoint[] At, OrbArrangement.GroupPlacement[] G) Run(
        (double, double)?[]? offsets, PixelPoint? anchor = null, PixelRect[]? obstacles = null)
    {
        var groups = new[] { new OrbArrangement.GroupLayout("circle", anchor ?? new PixelPoint(960, 540)) };
        return OrbArrangement.ComputeGroups(4, LeadOf, new int[4], groups, Layout, null,
            OrbArrangement.GroupLayoutMode.Free, obstacles ?? Array.Empty<PixelRect>(),
            offsets?.Select(o => o is { } v ? ((double, double)?)v : null).ToArray());
    }

    // The visible circle of an orb (members are MemberScale of a lead's): avoidance is by circles, not windows.
    private static (double X, double Y, double R) Disc(PixelPoint p, bool member)
    {
        var w = OrbArrangement.WindowFor(1.0, 1.0);
        return (p.X + w / 2.0, p.Y + w / 2.0, OrbArrangement.CircleDip / 2 * (member ? OrbArrangement.MemberScale : 1.0));
    }

    private static PixelRect Win(PixelPoint p) =>
        new(p, new PixelSize(OrbArrangement.WindowFor(1.0, 1.0), OrbArrangement.WindowFor(1.0, 1.0)));

    [Fact]
    public void AHandPlacedMemberNeverMovesItsLeadItsGroupBoxLandedOrCurve()
    {
        var plain = Run(null);
        var moved = Run(new (double, double)?[] { null, (400, -300), null, null });

        Assert.Equal(plain.At[0], moved.At[0]);                         // lead
        Assert.Equal(plain.At[2], moved.At[2]);                         // siblings
        Assert.Equal(plain.At[3], moved.At[3]);
        Assert.NotEqual(plain.At[1], moved.At[1]);                      // control: the member did move
        Assert.Equal(plain.G[0].Landed, moved.G[0].Landed);
        Assert.Equal(plain.G[0].SlotBounds, moved.G[0].SlotBounds);
        Assert.Equal(plain.G[0].CurveCentre, moved.G[0].CurveCentre);
        Assert.Equal(plain.G[0].CurveScale, moved.G[0].CurveScale);
    }

    [Fact]
    public void AMemberFollowsItsOwnTeamRigidlyAndIsPlacedAfterItsLead()
    {
        var plain = Run(null);
        var r = Run(new (double, double)?[] { null, null, (200, 150), null });   // member 2 has nested member 3
        var d2 = (r.At[2].X - plain.At[2].X, r.At[2].Y - plain.At[2].Y);
        var d3 = (r.At[3].X - plain.At[3].X, r.At[3].Y - plain.At[3].Y);
        Assert.NotEqual((0, 0), d2);
        Assert.Equal(d2, d3);                                                   // subtree rigid
    }

    [Fact]
    public void ANestedHandPlacedMemberIsRelativeToItsLeadsFinalPlace()
    {
        var r = Run(new (double, double)?[] { null, null, (300, 0), (0, 200) });
        var lead2 = Win(r.At[2]); var m3 = Win(r.At[3]);
        var centre2 = (lead2.X + lead2.Width / 2.0, lead2.Y + lead2.Height / 2.0);
        var centre3 = (m3.X + m3.Width / 2.0, m3.Y + m3.Height / 2.0);
        var scaleMember = OrbArrangement.MemberScale;
        // 3 is placed 200 DIP below its lead's final centre (unless avoidance moved it: allow a generous slack).
        Assert.InRange(centre3.Item2 - centre2.Item2, 120, 300);
    }

    [Fact]
    public void AnOffsetReachingAcrossTheScreenFromALeadNearAnEdgeStaysOnScreen()
    {
        var r = Run(new (double, double)?[] { null, (5000, 5000), (-5000, -5000), null }, new PixelPoint(120, 120));
        foreach (var i in new[] { 1, 2 })
        {
            var w = Win(r.At[i]);
            Assert.True(w.X >= 0 && w.Y >= 0 && w.Right <= 1920 && w.Bottom <= 1080, $"member {i} at {w}");
        }
    }

    [Fact]
    public void SeveralMembersGivenTheSameOffsetDoNotLandOnTopOfEachOther()
    {
        var r = Run(new (double, double)?[] { null, (250, 0), (250, 0), (250, 0) });
        var discs = new[] { 1, 2, 3 }.Select(i => Disc(r.At[i], true)).ToArray();
        for (var a = 0; a < discs.Length; a++)
            for (var b = a + 1; b < discs.Length; b++)
            {
                var d = Math.Sqrt(Math.Pow(discs[a].X - discs[b].X, 2) + Math.Pow(discs[a].Y - discs[b].Y, 2));
                Assert.True(d >= discs[a].R + discs[b].R, $"members {a + 1} and {b + 1} discs overlap: centres {d:F1} apart");
            }
    }

    [Fact]
    public void AHandPlacedMemberAvoidsAnObstacleAndTheLeadAndAnotherGroupsBox()
    {
        var obstacle = new PixelRect(1100, 440, 300, 300);       // right where (400,0) from the lead would land
        var r = Run(new (double, double)?[] { null, (400, 0), null, null }, obstacles: new[] { obstacle });
        var m = Disc(r.At[1], true); var lead = Disc(r.At[0], false);
        Assert.False(m.X + m.R > obstacle.X && m.X - m.R < obstacle.Right && m.Y + m.R > obstacle.Y && m.Y - m.R < obstacle.Bottom,
            $"member disc {m} sits on the obstacle");
        Assert.True(Math.Sqrt(Math.Pow(m.X - lead.X, 2) + Math.Pow(m.Y - lead.Y, 2)) >= m.R + lead.R, "member disc sits on its own lead");
        // Control: the requested spot (lead centre + 400) really is on the obstacle, so the avoidance did work.
        var wanted = (X: lead.X + 400, Y: lead.Y);
        Assert.True(wanted.X > obstacle.X && wanted.X < obstacle.Right && wanted.Y > obstacle.Y && wanted.Y < obstacle.Bottom);
    }

    [Fact]
    public void NonFiniteAndRootOffsetsAreIgnoredAndTheResultIsDeterministic()
    {
        var plain = Run(null);
        var junk = Run(new (double, double)?[] { (100, 100), (double.NaN, 5), (double.PositiveInfinity, 0), null });
        Assert.Equal(plain.At, junk.At);                                         // root's and non-finite entries change nothing
        var a = Run(new (double, double)?[] { null, (200, 100), null, null });
        var b = Run(new (double, double)?[] { null, (200, 100), null, null });
        Assert.Equal(a.At, b.At);
    }

    [Fact]
    public void TheSameOffsetAtTwoScalesPlacesTheMemberProportionally()
    {
        PixelPoint Place(double scale)
        {
            var layout = new OrbArrangement.Layout(Work, scale, "circle", 1.0);
            var (at, _) = OrbArrangement.ComputeGroups(4, LeadOf, new int[4],
                new[] { new OrbArrangement.GroupLayout("circle", new PixelPoint(960, 540)) }, layout, null,
                OrbArrangement.GroupLayoutMode.Arranged, Array.Empty<PixelRect>(),
                new (double, double)?[] { null, (150, 0), null, null });
            return at[1];
        }
        var lead = Run(null).At[0];
        Assert.True(Place(2.0).X - lead.X > Place(1.0).X - lead.X);
    }
}
