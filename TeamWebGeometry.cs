using Avalonia;

namespace Orbweaver
{
    // The silk of a team drawn as a spiderweb: the spokes that run from the lead
    // out past its members, and the threads that join neighbouring points around
    // each ring.
    //
    // Members alone make a plus sign, or a hexagon of orbs, and neither reads as
    // a web — what makes a web is the frame between the points. So this works out
    // the frame from where the members actually are, rather than from how the
    // arrangement meant to put them: a member the user dragged somewhere else
    // still gets a spoke, and a lead against a screen edge, whose web is only
    // part of a circle, gets part of a frame.
    //
    // Pure, in DIPs relative to the lead's centre: no windows, no screen. The
    // window that draws it is in TeamLinks, for the same reason the arrow's
    // outline is in TeamLinkGeometry.
    internal static class TeamWebGeometry
    {
        // The widest angle between neighbouring spokes before the frame adds
        // some of its own. Four members at the compass points are four spokes
        // ninety degrees apart, which is a plus sign; one spoke in each gap makes
        // it eight, which is a web.
        internal const double MaxSpokeGap = 70 * Math.PI / 180;

        // A gap wider than this is the open side of a partial web — the way a
        // lead against an edge faces away from it — and is left open. Three
        // members on a full web are 120 degrees apart, which is under it, so
        // that web closes.
        internal const double OpenGap = 2.4;

        // Members within this fraction of one another's distance from the lead
        // are on one ring.
        internal const double RingTolerance = 0.12;

        // A ring needs this many members before a thread is drawn round it; a
        // lone member out on a spoke of its own is not a ring.
        internal const int MinRingMembers = 2;

        // How far a thread between two points is pulled in towards the lead.
        // Silk between two anchors hangs; a straight chord reads as a polygon.
        internal const double Sag = 0.78;

        // Clearance from the lead's rim before a spoke starts.
        internal const double SpokeStart = 3;

        internal readonly record struct Strand(Point From, Point Control, Point To);

        // `SpokeMembers[i]` is which of the input members sit on `Spokes[i]` —
        // indices into the list Build was given, empty for a spoke the frame added
        // to fill a gap. What lets a tug on one member shiver its own thread.
        internal sealed record Frame(
            IReadOnlyList<(Point From, Point To)> Spokes,
            IReadOnlyList<Strand> Rings,
            double Reach,
            IReadOnlyList<IReadOnlyList<int>> SpokeMembers);

        // Which of `members` lie along direction `angle`, as indices.
        private static IReadOnlyList<int> OnSpoke(IReadOnlyList<Point> members, double angle)
        {
            var on = new List<int>();

            for (var i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (Math.Sqrt(m.X * m.X + m.Y * m.Y) <= 1e-6) continue;

                var turn = Math.Atan2(m.Y, m.X) - angle;
                if (Math.Abs(Math.Atan2(Math.Sin(turn), Math.Cos(turn))) <= 0.05) on.Add(i);
            }

            return on;
        }

        // The frame for members at `members` (offsets from the lead's centre), or
        // null when there are none to draw round. `hubRadius` is the lead's own
        // radius, which is where the spokes begin.
        internal static Frame? Build(IReadOnlyList<Point> members, double hubRadius)
        {
            if (members.Count == 0) return null;

            var polar = members
                .Select(m => (Angle: Math.Atan2(m.Y, m.X), Radius: Math.Sqrt(m.X * m.X + m.Y * m.Y)))
                .Where(m => m.Radius > 1e-6)
                .ToList();
            if (polar.Count == 0) return null;

            // One spoke per distinct direction, in turn order.
            var angles = new List<double>();
            foreach (var a in polar.Select(p => p.Angle).OrderBy(a => a))
            {
                if (angles.Count == 0 || a - angles[^1] > 0.05) angles.Add(a);
            }

            if (angles.Count > 1 && angles[0] + Math.PI * 2 - angles[^1] <= 0.05) angles.RemoveAt(angles.Count - 1);

            // Fill the gaps, and note which neighbours are joined: every pair
            // except across an open side.
            var spokeAngles = new List<double>();
            var joinedToNext = new List<bool>();

            for (var i = 0; i < angles.Count; i++)
            {
                var next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + Math.PI * 2;
                var gap = next - angles[i];

                spokeAngles.Add(angles[i]);

                if (angles.Count == 1 || gap > OpenGap)
                {
                    joinedToNext.Add(false);
                    continue;
                }

                var extra = Math.Max(0, (int)Math.Ceiling(gap / MaxSpokeGap) - 1);

                for (var k = 1; k <= extra; k++)
                {
                    joinedToNext.Add(true);
                    spokeAngles.Add(angles[i] + gap * k / (extra + 1));
                }

                joinedToNext.Add(true);
            }

            // Rings: members at one distance from the lead, to within a tolerance.
            var radii = polar.Select(p => p.Radius).OrderBy(r => r).ToList();
            var clusters = new List<List<double>>();
            foreach (var r in radii)
            {
                if (clusters.Count > 0 && r - clusters[^1][0] <= RingTolerance * clusters[^1][0])
                    clusters[^1].Add(r);
                else
                    clusters.Add(new List<double> { r });
            }

            var ringRadii = clusters.Where(c => c.Count >= MinRingMembers).Select(c => c.Average()).ToList();

            var rings = new List<Strand>();
            var n = spokeAngles.Count;

            foreach (var r in ringRadii)
            {
                for (var i = 0; i < n; i++)
                {
                    if (!joinedToNext[i]) continue;

                    var a = spokeAngles[i];
                    var b = spokeAngles[(i + 1) % n];
                    if (n == 1) continue;

                    var from = new Point(r * Math.Cos(a), r * Math.Sin(a));
                    var to = new Point(r * Math.Cos(b), r * Math.Sin(b));
                    var middle = new Point((from.X + to.X) / 2 * Sag, (from.Y + to.Y) / 2 * Sag);

                    rings.Add(new Strand(from, middle, to));
                }
            }

            // Spokes: out to the outermost ring, or to a member that is further
            // out than that, so none stops short of the thing it leads to.
            var outer = ringRadii.Count > 0 ? ringRadii.Max() : 0;
            var spokes = new List<(Point, Point)>();
            var spokeMembers = new List<IReadOnlyList<int>>();
            var reach = outer;

            foreach (var a in spokeAngles)
            {
                var own = polar
                    .Where(p => Math.Abs(Math.Atan2(Math.Sin(p.Angle - a), Math.Cos(p.Angle - a))) <= 0.05)
                    .Select(p => p.Radius)
                    .DefaultIfEmpty(0)
                    .Max();
                var end = Math.Max(outer, own);

                reach = Math.Max(reach, end);
                if (end <= hubRadius + SpokeStart) continue;

                var start = hubRadius + SpokeStart;
                spokes.Add((
                    new Point(start * Math.Cos(a), start * Math.Sin(a)),
                    new Point(end * Math.Cos(a), end * Math.Sin(a))));
                spokeMembers.Add(OnSpoke(members, a));
            }

            return new Frame(spokes, rings, reach, spokeMembers);
        }
    }
}
