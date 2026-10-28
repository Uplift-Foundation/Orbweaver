using System;
using System.Collections.Generic;
using Avalonia;

namespace Orbweaver
{
    // CB-268: what releasing a dragged orb (or team) should do about groups.
    //
    // Pure on purpose, like OrbArrangement and OrbGlyph: no window, no settings,
    // everything in DIP. The drag handler converts from physical pixels once
    // (dividing by the screen's render scaling) and hands the numbers over, so a
    // rule about distances can be tested with a list of points.
    internal abstract record SnapOutcome
    {
        internal sealed record NoChange : SnapOutcome;
        internal sealed record Join(string GroupId) : SnapOutcome;
        internal sealed record Leave : SnapOutcome;

        // Free mode only (F3). Pair: the dragged team dropped within join
        // distance of another loose team; `OtherId` is that team's candidate
        // id (its root session id). LeaveToFree: the dragged team left a user
        // group and floats where it was dropped.
        internal sealed record Pair(string OtherId) : SnapOutcome;
        internal sealed record LeaveToFree : SnapOutcome;
    }

    /// <summary>
    /// One group the dragged orb could be near. <paramref name="Outline"/> may be
    /// empty (outlines off, or not computed yet), in which case only the discs
    /// count. <paramref name="Discs"/> exclude the dragged orb and its team.
    /// <paramref name="Loose"/> marks a free-mode target that is one ungrouped
    /// team rather than a group: snapping to it is a Pair, not a Join.
    /// </summary>
    internal readonly record struct SnapCandidate(
        string GroupId,
        Point[] Outline,
        IReadOnlyList<(double X, double Y, double R)> Discs,
        bool Loose = false);

    internal static class OrbSnap
    {
        internal const double JoinDip = 24;
        internal const double LeaveDip = 72;

        /// <summary>
        /// The visible radius, in DIP, of an orb at size 1.0. Both thresholds are
        /// stated for that size and scale with the dragged orb's, because the
        /// gap S2 leaves between two separated groups scales with orb size too:
        /// a fixed 24 DIP join distance is wider than that gap on a small orb, and
        /// releasing an edge orb would join the neighbouring group it was only
        /// arranged next to.
        /// </summary>
        internal const double BaseRadiusDip = 18;

        /// <summary>Single-disc form of <see cref="Decide(IReadOnlyList{ValueTuple{double,double,double}}, string, IReadOnlyList{SnapCandidate}, double, double)"/>.</summary>
        internal static SnapOutcome Decide(
            Point centre, double radius, string currentGroupId,
            IReadOnlyList<SnapCandidate> candidates,
            double joinDip = JoinDip, double leaveDip = LeaveDip) =>
            Decide(new[] { (centre.X, centre.Y, radius) }, currentGroupId, candidates, joinDip, leaveDip);

        internal static SnapOutcome Decide(
            IReadOnlyList<(double X, double Y, double R)> moving, string currentGroupId,
            IReadOnlyList<SnapCandidate> candidates,
            double joinDip = JoinDip, double leaveDip = LeaveDip) =>
            Decide(moving, currentGroupId, candidates, free: false, joinDip, leaveDip);

        // `moving` is every disc being dragged — the pressed orb and its whole
        // team (T2). A team is measured as one thing: the gap to a group is the
        // smallest gap from any of its discs, so a member fanned out towards a
        // neighbouring group is what snaps the team, and a team does not "leave"
        // just because its lead is far from the rest of the group.
        //
        //   Join(id): the nearest group other than currentGroupId whose gap is
        //             <= joinDip. Ties go to the ordinal-smallest id. Join beats
        //             Leave.
        //   Leave:    no Join, the current group has at least one other disc, and
        //             every one of them is > leaveDip away edge to edge.
        //   NoChange: otherwise, including when the moving team is its group's
        //             only occupant. The 24..72 band between the two thresholds
        //             is the hysteresis: neither fires inside it.
        //
        // Free mode (F3) changes two things. A loose candidate is Paired with
        // rather than Joined, and leaving a user group is LeaveToFree, not
        // Leave: the team is simply let go, there is no singleton group to
        // make. The caller leaves the predefined groups out of the candidates
        // in free mode, and a team that is itself loose has no group to leave.
        internal static SnapOutcome Decide(
            IReadOnlyList<(double X, double Y, double R)> moving, string currentGroupId,
            IReadOnlyList<SnapCandidate> candidates, bool free,
            double joinDip = JoinDip, double leaveDip = LeaveDip)
        {
            if (moving.Count == 0) return new SnapOutcome.NoChange();

            string? bestId = null;
            var bestLoose = false;
            var bestGap = double.PositiveInfinity;
            SnapCandidate? own = null;

            foreach (var candidate in candidates)
            {
                // A loose target exists only in free mode. Arranged mode keeps
                // today's rule, so a host that offered one anyway cannot make
                // an arranged drop pair.
                if (candidate.Loose && !free) continue;

                if (string.Equals(candidate.GroupId, currentGroupId, StringComparison.Ordinal))
                {
                    own ??= candidate;
                    continue;
                }

                var gap = GapTo(moving, candidate);
                if (gap > joinDip) continue;

                if (gap < bestGap
                    || (gap == bestGap && string.CompareOrdinal(candidate.GroupId, bestId) < 0))
                {
                    bestGap = gap;
                    bestId = candidate.GroupId;
                    bestLoose = candidate.Loose;
                }
            }

            if (bestId is not null)
                return bestLoose ? new SnapOutcome.Pair(bestId) : new SnapOutcome.Join(bestId);

            if (own is { } mine && mine.Discs.Count > 0 && DiscGap(moving, mine.Discs) > leaveDip)
                return free ? new SnapOutcome.LeaveToFree() : new SnapOutcome.Leave();

            return new SnapOutcome.NoChange();
        }

        // The smaller of: the edge-to-outline distance from outside, and the
        // edge-to-edge distance to the nearest disc.
        internal static double GapTo(
            IReadOnlyList<(double X, double Y, double R)> moving, SnapCandidate candidate)
        {
            var gap = DiscGap(moving, candidate.Discs);

            if (candidate.Outline.Length >= 3)
            {
                foreach (var (x, y, r) in moving)
                    gap = Math.Min(gap, OutlineGap(new Point(x, y), r, candidate.Outline));
            }

            return gap;
        }

        // Smallest edge-to-edge distance between any moving disc and any of
        // `others`. Overlapping discs are 0, not negative: a gap is a distance.
        internal static double DiscGap(
            IReadOnlyList<(double X, double Y, double R)> moving,
            IReadOnlyList<(double X, double Y, double R)> others)
        {
            var best = double.PositiveInfinity;
            foreach (var (x, y, r) in moving)
            {
                foreach (var (ox, oy, or) in others)
                {
                    var d = Math.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy)) - r - or;
                    if (d < best) best = d;
                }
            }

            return Math.Max(0, best);
        }

        // Distance from the orb's edge to the polygon, from outside; 0 once the
        // orb's centre is inside it.
        internal static double OutlineGap(Point centre, double radius, Point[] polygon)
        {
            var signed = GroupOutline.SignedDistanceInside(centre, polygon);
            return signed >= 0 ? 0 : Math.Max(0, -signed - radius);
        }
    }
}
