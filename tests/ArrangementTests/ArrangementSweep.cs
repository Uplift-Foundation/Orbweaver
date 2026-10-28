using Avalonia;
using Orbweaver;

namespace Orbweaver.Tests
{
    // The orb-geometry sweep, as a class rather than as a script.
    //
    // The sweep itself is unchanged and predates this file: it walks every
    // shape, at every end of the spacing slider, across the orb counts and team
    // shapes that actually occur, and asserts the things that kept going wrong —
    // orbs off the screen, orbs drawn on top of each other, teams fanned so far
    // from their lead that the arrows stopped being drawn, and arrangements that
    // threw rather than drew. Then it walks the whole thing again with a saved
    // anchor, the input that says "draw the shape here" instead of in the middle
    // of the screen.
    //
    // **Why it moved out of Program.cs.** tests/ArrangementTests is a plain
    // console exe, not a test-SDK project, so it contributes nothing to
    // tools/coverage.sh — OrbArrangement.cs read 0.0% while being the most
    // exhaustively verified file in the repo, and CLAUDE.md called that out as a
    // number to distrust rather than a gap to fill. Splitting the cases and the
    // invariants away from the printing lets tests/UnitTests compile this file
    // in and run the same sweep as a real test, which puts those lines in the
    // denominator where they belong.
    //
    // Two consumers, deliberately: `dotnet run --project tests/ArrangementTests`
    // still prints the grouped failure report that makes a regression readable
    // by hand, and the xUnit suite asserts the same list is empty. Neither owns
    // a private copy of the matrix, which is the point — a case added here is
    // added to both at once.
    internal static class ArrangementSweep
    {
        internal readonly record struct Screen(string Name, PixelRect Work, double Scale);

        // A team shape is a function of the orb count rather than a fixed array,
        // because the same shape ("one team of 3") has to mean something at 1
        // orb and at 30.
        internal readonly record struct TeamShape(string Name, Func<int, int[]> Build);

        internal readonly record struct Anchor(string Name, PixelPoint At);

        // How the orbs are split across the shapes, when there is more than one
        // shape. A function of the count for the same reason a TeamShape is:
        // "chats and heartbeats" has to mean something at 2 orbs and at 30.
        //
        // null on a Case means the ungrouped entry point — the two-argument
        // Compute, which is still what runs whenever every orb is in the same
        // group and is the only thing the first two sweeps ever exercised.
        internal readonly record struct Split(string Name, Func<int, int[]> Build);

        // One user size per orb (CB-198), when the orbs are not all the same
        // size. A function of the count for the same reason as the two above.
        internal readonly record struct SizeMix(string Name, Func<int, double[]> Build);

        // Size is every orb's user size when Mix is null, and is ignored when it
        // is not. 1.0 with no Mix is the arrangement as it was before sizes
        // existed, and is run through Compute with no sizes at all — the call
        // every caller made until CB-198, which has to keep meaning the same.
        internal readonly record struct Case(
            Screen Screen,
            string Shape,
            double Spacing,
            int Count,
            TeamShape Team,
            Anchor? Anchor,
            Split? Split = null,
            double Size = 1.0,
            SizeMix? Mix = null)
        {
            public override string ToString()
                => $"{Screen.Name} / {Shape} / spacing {Spacing:0.00} / {Count} orbs / {Team.Name}"
                 + (Anchor is null ? "" : $" / anchor {Anchor.Value.Name}")
                 + (Split is null ? "" : $" / groups {Split.Value.Name}")
                 + (Mix is { } mix ? $" / sizes {mix.Name}" : Size == 1.0 ? "" : $" / size {Size:0.00}");

            // What every orb in this case is drawn at, and what Compute is
            // handed — null for the plain 1.0 case, deliberately, per the note
            // on the record.
            internal double[] SizesOf(int count) =>
                Mix?.Build(count) ?? Enumerable.Repeat(Size, count).ToArray();

            internal double[]? SizeArgument(int count) =>
                Mix is null && Size == 1.0 ? null : SizesOf(count);
        }

        internal static readonly string[] Shapes =
            { "heart", "circle", "diamond", "star", "grid", "line", "vline" };

        internal static readonly double[] Spacings = { 0.3, 0.85, 2.0 };

        internal static readonly int[] Counts = { 1, 2, 3, 5, 8, 13, 20, 30 };

        // Both ends of the orb-size slider and the size orbs always were.
        // Taken from OrbSizing rather than written out, so the slider's range
        // and the range verified here cannot drift apart.
        //
        // Both platforms' floors, on whichever runner this is — not
        // OrbSizing.Min, which is the current platform's. The matrix has to be
        // the same on both CI legs, or a Windows-only geometry failure at 0.6
        // could never be seen and a macOS one at 0.7 would never be looked for;
        // and the geometry is platform-free, so there is no reason for either
        // leg to verify less than both.
        internal static readonly double[] Sizes =
            new[] { OrbSizing.MinMacOS, OrbSizing.MinWindows, OrbSizing.Default, OrbSizing.Max }
                .Distinct()
                .OrderBy(s => s)
                .ToArray();

        // The smallest orb any platform can draw, and the largest.
        internal static readonly double MinSize = Math.Min(OrbSizing.MinMacOS, OrbSizing.MinWindows);
        internal const double MaxSize = OrbSizing.Max;

        // Mixed sizes, each chosen for the way it could break the "lay out for
        // the largest" rule: a ramp that puts every size next to every other
        // across the shape and the teams; one orb at the top of the slider among
        // a crowd at the bottom, which is the case that spreads them out most
        // and is orb 0 — the lead, in every team shape that has one; and the
        // reverse, one small orb in a crowd of big ones, at the end where team
        // shapes put members.
        internal static readonly SizeMix[] Mixes =
        {
            new("ramp, smallest to largest", n =>
                Enumerable.Range(0, n).Select(i => MinSize + (MaxSize - MinSize) * (i % 5) / 4.0).ToArray()),

            new("one big lead among small orbs", n =>
                Enumerable.Range(0, n).Select(i => i == 0 ? MaxSize : MinSize).ToArray()),

            new("one small orb among big ones", n =>
                Enumerable.Range(0, n).Select(i => i == n - 1 ? MinSize : MaxSize).ToArray()),
        };

        internal static readonly int[] MixedCounts = { 2, 5, 13, 30 };

        internal static readonly Screen[] Screens =
        {
            new("14in retina", new PixelRect(0, 0, 3024, 1890), 2.0),
            new("1080p", new PixelRect(0, 0, 1920, 1080), 1.0),
            new("small laptop", new PixelRect(0, 0, 1280, 800), 1.0),
        };

        internal static readonly TeamShape[] TeamShapes =
        {
            new("no teams", n => Enumerable.Repeat(-1, n).ToArray()),

            new("one team of 3", n =>
            {
                var leads = Enumerable.Repeat(-1, n).ToArray();
                for (var i = 1; i < Math.Min(4, n); i++) leads[i] = 0;
                return leads;
            }),

            new("two teams of 6", n =>
            {
                var leads = Enumerable.Repeat(-1, n).ToArray();
                for (var i = 2; i < Math.Min(8, n); i++) leads[i] = 0;
                for (var i = 8; i < Math.Min(14, n); i++) leads[i] = 1;
                return leads;
            }),

            new("nested: A leads B, B leads C and D", n =>
            {
                var leads = Enumerable.Repeat(-1, n).ToArray();
                if (n > 1) leads[1] = 0;
                if (n > 2) leads[2] = 1;
                if (n > 3) leads[3] = 1;
                return leads;
            }),

            new("everything in one team", n =>
            {
                var leads = Enumerable.Repeat(0, n).ToArray();
                if (n > 0) leads[0] = -1;
                return leads;
            }),

            new("lead cycle: A leads B, B leads A", n =>
            {
                var leads = Enumerable.Repeat(-1, n).ToArray();
                if (n > 1) { leads[0] = 1; leads[1] = 0; }
                return leads;
            }),

            new("lead points at itself", n =>
            {
                var leads = Enumerable.Repeat(-1, n).ToArray();
                if (n > 0) leads[0] = 0;
                return leads;
            }),

            new("lead points at an orb that isn't there", n =>
            {
                var leads = Enumerable.Repeat(-1, n).ToArray();
                if (n > 0) leads[0] = n + 5;
                return leads;
            }),
        };

        // Every way the three groups can be occupied that is not "all of them in
        // one", plus two that are — the second of those being the case worth
        // having deliberately: one group in use, but not group 0, so the band
        // code has to notice how many slots are *occupied* rather than how many
        // exist.
        //
        // The last two are hostile and are the reason SlotOf exists: a group
        // index nothing has a shape for, and a groupOf array shorter than the
        // orb count, both of which a caller can produce by getting the settings
        // and the shapes array out of step. Neither may lose an orb.
        internal static readonly Split[] Splits =
        {
            new("all in the chats", n => new int[n]),
            new("all in the crons", n => Enumerable.Repeat(2, n).ToArray()),

            new("chats + heartbeats", n =>
                Enumerable.Range(0, n).Select(i => i % 3 == 1 ? 1 : 0).ToArray()),

            new("chats + crons", n =>
                Enumerable.Range(0, n).Select(i => i % 3 == 2 ? 2 : 0).ToArray()),

            new("all three, evenly", n =>
                Enumerable.Range(0, n).Select(i => i % 3).ToArray()),

            new("all three, one orb each in 1 and 2", n =>
                Enumerable.Range(0, n).Select(i => i < 2 ? i + 1 : 0).ToArray()),

            new("a group index with no shape", n =>
                Enumerable.Range(0, n).Select(i => i % 2 == 0 ? 0 : 7).ToArray()),

            new("a groupOf array shorter than the orbs", n =>
                Enumerable.Repeat(1, Math.Max(0, n - 2)).ToArray()),
        };

        // One shape per group, rotated off the case's own shape so that across
        // the sweep every shape appears in every slot — and no two slots ever
        // hold the same one, which is what makes a shape drawn in the wrong band
        // visible rather than a coincidence.
        //
        // Six slots since CB-268, up from three: the user-group sweep below runs
        // up to six groups, and seven shapes means six rotations are still all
        // different. The Bands-era cases only ever index the first three (or a
        // slot nothing has a shape for), so widening this changes nothing they
        // draw.
        internal static string[] ShapesFor(string shape)
        {
            var at = Array.IndexOf(Shapes, shape);
            if (at < 0) at = 0;

            return Enumerable.Range(0, 6).Select(k => Shapes[(at + k) % Shapes.Length]).ToArray();
        }

        // Two anchors well inside the work area, one hard against a corner, and
        // two that cannot be honoured at all. The hostile ones are deliberate:
        // honouring an anchor must never win over keeping orbs on the screen.
        internal static Anchor[] AnchorsFor(Screen screen) => new Anchor[]
        {
            new("upper left quarter", new PixelPoint(
                screen.Work.X + screen.Work.Width / 4, screen.Work.Y + screen.Work.Height / 4)),
            new("lower right quarter", new PixelPoint(
                screen.Work.X + screen.Work.Width * 3 / 4, screen.Work.Y + screen.Work.Height * 3 / 4)),
            new("hard against the corner", new PixelPoint(screen.Work.X, screen.Work.Y)),
            new("off the right edge", new PixelPoint(screen.Work.Right + 800, screen.Work.Y + 40)),
            new("negative", new PixelPoint(-4000, -4000)),
        };

        // Every case, anchorless sweep first and then the anchored one. The
        // anchored sweep exists because the anchor shipped with no coverage at
        // all and the shape went on recentering itself on every orb that joined
        // or left — the exact class of bug the first sweep was written for,
        // reintroduced by a new argument nothing checked.
        internal static IEnumerable<Case> Cases()
        {
            foreach (var screen in Screens)
            foreach (var shape in Shapes)
            foreach (var spacing in Spacings)
            foreach (var count in Counts)
            foreach (var team in TeamShapes)
            foreach (var size in Sizes)
                yield return new Case(screen, shape, spacing, count, team, null, Size: size);

            foreach (var screen in Screens)
            foreach (var anchor in AnchorsFor(screen))
            foreach (var shape in Shapes)
            foreach (var spacing in Spacings)
            foreach (var count in Counts)
            foreach (var team in TeamShapes)
                yield return new Case(screen, shape, spacing, count, team, anchor);

            // And once more with the orbs split across up to three shapes.
            //
            // A subset of the team shapes and of the anchors rather than the full
            // cross product, and that is a real limit worth stating rather than
            // leaving for someone to infer: three groups multiply an already
            // large matrix, and the expensive part of Compute is the separation
            // pass, which is quadratic in the orb count and runs up to 150 times.
            //
            // The three team shapes kept are the ones that stress a fan-out
            // inside a band — none, a small team, and every orb in one team,
            // which is the case where a whole group is a single fan. Nested teams
            // and the four malformed lead tables are exercised at full breadth by
            // the two sweeps above, and grouping does not touch the code that
            // resolves a lead: only an *anchor's* group is ever consulted, and a
            // member is placed relative to its lead exactly as before.
            var groupedTeams = TeamShapes
                .Where(t => t.Name is "no teams" or "one team of 3" or "everything in one team")
                .ToArray();

            foreach (var screen in Screens)
            foreach (var anchor in AnchorsFor(screen).Take(3).Select(a => (Anchor?)a).Append(null))
            foreach (var shape in Shapes)
            foreach (var spacing in Spacings)
            foreach (var count in Counts)
            foreach (var team in groupedTeams)
            foreach (var split in Splits)
                yield return new Case(screen, shape, spacing, count, team, anchor, split);

            // And with orbs of different sizes side by side (CB-198). Every
            // shape, spacing, screen and team shape, since a member drawn at a
            // different size from its lead is exactly what could break a fan;
            // four counts rather than eight, spanning a pair to a crowd, and both
            // entry points — ungrouped, and grouped across all three bands — each
            // unanchored and anchored into a corner, the anchor that has broken
            // things most often.
            var mixedSplits = new Split?[] { null, Splits.First(s => s.Name == "all three, evenly") };

            foreach (var screen in Screens)
            foreach (var anchor in new Anchor?[] { null, AnchorsFor(screen)[2] })
            foreach (var split in mixedSplits)
            foreach (var shape in Shapes)
            foreach (var spacing in Spacings)
            foreach (var count in MixedCounts)
            foreach (var team in TeamShapes)
            foreach (var mix in Mixes)
                yield return new Case(screen, shape, spacing, count, team, anchor, split, Mix: mix);
        }

        // One copy of the invariants, run for every case in both sweeps. An
        // anchor is not allowed to buy an exemption from any of them, which is
        // why there is one Check and not two.
        //
        // Returns what went wrong, already prefixed with the case, so a caller
        // can print it or assert it is empty without knowing anything about the
        // geometry.
        internal static IReadOnlyList<string> Check(Case test)
        {
            var screen = test.Screen;
            var count = test.Count;
            var failures = new List<string>();
            var where = test.ToString();

            void Fail(string what) => failures.Add($"{where}: {what}");

            var leads = test.Team.Build(count);
            var layout = new OrbArrangement.Layout(
                screen.Work, screen.Scale, test.Shape, test.Spacing, test.Anchor?.At);

            // Which entry point this case is about. The two-argument one is not
            // just the grouped one with every orb in group 0 — it is the one the
            // app still calls whenever nobody has asked for a separate shape, so
            // it has to keep being exercised as itself.
            var groups = test.Split?.Build(count);
            var shapes = ShapesFor(test.Shape);

            var sizes = test.SizesOf(count);
            var sizeArgument = test.SizeArgument(count);

            PixelPoint[] Arrange(int[] withLeads, OrbArrangement.Layout with) =>
                groups is null
                    ? OrbArrangement.Compute(count, withLeads, with, sizeArgument)
                    : OrbArrangement.Compute(count, withLeads, groups, shapes, with, sizeArgument);

            PixelPoint[] pts;

            try
            {
                pts = Arrange(leads, layout);
            }
            catch (Exception ex)
            {
                Fail($"threw {ex.GetType().Name}: {ex.Message}");
                return failures;
            }

            if (pts.Length != count)
            {
                Fail($"returned {pts.Length} positions for {count} orbs");
                return failures;
            }

            // Every orb measured at its own size. Written out here rather than
            // asked of OrbArrangement.WindowFor, so the check is not the code it
            // is checking: a window is 56 DIP, times the display's scale, times
            // the orb's own user size; a circle is 36 DIP by the same two, and a
            // team member's is 72% of that again.
            var windowOf = sizes.Select(s => (int)Math.Round(OrbArrangement.WindowDip * screen.Scale * s)).ToArray();
            var window = windowOf.Max();
            var circleOf = sizes.Select(s => OrbArrangement.CircleDip * screen.Scale * s).ToArray();

            // Centres, not corners: two orbs of different sizes at the same
            // top-left are not at the same place.
            double CentreX(PixelPoint[] p, int i) => p[i].X + windowOf[i] / 2.0;
            double CentreY(PixelPoint[] p, int i) => p[i].Y + windowOf[i] / 2.0;

            // 1. Every orb fully on screen. An orb you cannot see is worse than
            //    an arrangement you do not like.
            foreach (var (p, i) in pts.Select((p, i) => (p, i)))
            {
                if (p.X < screen.Work.X || p.Y < screen.Work.Y
                    || p.X + windowOf[i] > screen.Work.Right || p.Y + windowOf[i] > screen.Work.Bottom)
                {
                    Fail($"orb {i} at ({p.X},{p.Y}) is outside the work area");
                    break;
                }
            }

            // 2. Nothing sitting on top of anything else. Measured on the
            //    circles that are drawn, at their own sizes — a member's is
            //    smaller than a lead's, and a 60% orb's smaller than a 200% one's.
            double RadiusOf(int i) =>
                (leads[i] >= 0 && leads[i] < count ? circleOf[i] * OrbArrangement.MemberScale : circleOf[i]) / 2;

            // At the very bottom of the slider the shape is meant to be a tight
            // cluster, so a little overlap there is the setting doing its job.
            // Anywhere else, circles must not touch.
            //
            // The allowance is a fraction of a lead's circle, and with mixed
            // sizes it is the larger of the pair's two — the strictest reading
            // that is still the same rule, since the arrangement's own slack is
            // cut from the largest orb present. With every orb the same size it
            // is the number this check always used.
            double AllowedOverlap(int i, int j) =>
                test.Spacing <= 0.3 ? -Math.Max(circleOf[i], circleOf[j]) * 0.45 : 0;

            var worst = double.MaxValue;
            var worstGap = 0.0;
            var worstAllowed = 0.0;
            var worstPair = (-1, -1);

            for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
            {
                var dx = CentreX(pts, i) - CentreX(pts, j);
                var dy = CentreY(pts, i) - CentreY(pts, j);
                var gap = Math.Sqrt(dx * dx + dy * dy) - RadiusOf(i) - RadiusOf(j);
                var allowed = AllowedOverlap(i, j);

                if (gap - allowed < worst)
                {
                    worst = gap - allowed;
                    worstGap = gap;
                    worstAllowed = allowed;
                    worstPair = (i, j);
                }
            }

            if (count > 1 && worst < 0)
            {
                Fail($"orbs {worstPair.Item1} and {worstPair.Item2} overlap by {-worstGap:0}px "
                   + $"(allowed {-worstAllowed:0})");
            }

            // 3. Every team member close enough to its lead that TeamLinks will
            //    draw the arrow. Without the arrow there is nothing saying they
            //    are a team.
            for (var i = 0; i < count; i++)
            {
                var lead = leads[i];
                if (lead < 0 || lead >= count || lead == i) continue;

                // Cycles are broken by the arrangement, so only check pairs it
                // still treats as a team: a member sits within a sane distance
                // of its lead.
                var dx = CentreX(pts, i) - CentreX(pts, lead);
                var dy = CentreY(pts, i) - CentreY(pts, lead);
                var apart = Math.Sqrt(dx * dx + dy * dy);

                if (apart > screen.Work.Width * 0.6)
                {
                    Fail($"member {i} is {apart:0}px from lead {lead} — too far to read as a team");
                    break;
                }
            }

            // 4. Deterministic. An arrangement that moved on its own would make
            //    every other check meaningless.
            var again = Arrange(test.Team.Build(count), layout);
            if (!pts.SequenceEqual(again)) Fail("not deterministic — two runs disagreed");

            // 5. Moving the anchor moves the arrangement with it, by the same
            //    amount.
            //
            //    This is the invariant the shipped bug actually broke, and it
            //    took three tries to state. Comparing the arrangement's middle
            //    against the anchor looks like the obvious check and is not one:
            //    a shape has size, so an anchor in a screen corner is a centre
            //    nothing can have; "line" spans the full width however it is
            //    anchored; and with two orbs a heart fills two of its points,
            //    whose middle is nowhere near the heart's. All three are the
            //    arrangement behaving correctly and the assertion being wrong.
            //
            //    What the user does is drag the shape, which nudges the saved
            //    anchor by the same delta (SessionManager.ShiftArrangementAnchor)
            //    and must nudge the orbs by that delta next time round. Before
            //    the anchor existed the shape was recomputed around the middle
            //    of the screen every time, so the drag was thrown away the
            //    moment an orb joined or left.
            //
            //    Scoped to teamless arrangements, where every orb is a point of
            //    the shape. A team member is placed at a fixed distance from its
            //    lead in whichever direction fits on the screen, so near an edge
            //    a nudge can legitimately turn the fan instead of moving it, and
            //    no delta is owed. The other checks still run for every team
            //    shape; this one cannot.
            if (test.Anchor is { } a && leads.All(l => l < 0))
            {
                // Not equal, so swapping the axes cannot pass, and both
                // **even**, which is what makes the assertion below exact rather
                // than approximate. Points are laid out from the anchor in
                // floating point and rounded to whole pixels twice — once
                // building the shape, once scaling it to fit — and Math.Round
                // sends an exact .5 to the even side, so an odd shift flips
                // which side that is and the delta lands a pixel out. An even
                // shift leaves the integer part's parity alone, so every
                // rounding falls the same way and the whole arrangement moves by
                // exactly the delta. (With an odd delta the error is not even
                // bounded at one pixel: Fit scales the shape about its middle
                // after rounding it, so at the top of the spacing slider a
                // one-pixel rounding comes out multiplied.)
                const int dx = 38, dy = 24;

                var moved = Arrange(
                    test.Team.Build(count),
                    layout with { Center = new PixelPoint(a.At.X + dx, a.At.Y + dy) });

                // Only where neither arrangement was pushed back onto the
                // screen. Fit scales the shape about its own middle, which does
                // not depend on the anchor, and Slide then translates whatever
                // hangs off the edge back inside — so against an edge the anchor
                // is *supposed* to stop being honoured, and demanding the delta
                // there would be demanding orbs walk off the screen. "Slid" is
                // read off the result rather than asked of Slide: a margin on
                // all four sides means there was nothing to push.
                //
                // With mixed sizes the margin is measured on each orb's *slot*
                // — the largest window, centred where the orb is — because the
                // arrangement is laid out in slots and that is what gets pushed.
                // A 60% orb sits inset in its slot, so a slot jammed against the
                // edge still shows the small orb a margin away from it, and
                // reading that margin as "nothing was pushed" demanded the delta
                // from an arrangement that had correctly stopped honouring the
                // anchor. With every orb one size the slot is the orb's own
                // window and this is the check it always was.
                bool Clear(PixelPoint[] p)
                    => Enumerable.Range(0, count).All(i =>
                        CentreX(p, i) - window / 2.0 > screen.Work.X
                        && CentreY(p, i) - window / 2.0 > screen.Work.Y
                        && CentreX(p, i) + window / 2.0 < screen.Work.Right
                        && CentreY(p, i) + window / 2.0 < screen.Work.Bottom);

                if (moved.Length == count && Clear(pts) && Clear(moved))
                {
                    for (var i = 0; i < count; i++)
                    {
                        var gotX = moved[i].X - pts[i].X;
                        var gotY = moved[i].Y - pts[i].Y;

                        if (gotX == dx && gotY == dy) continue;

                        Fail($"moving the anchor by ({dx},{dy}) moved orb {i} by ({gotX},{gotY}) instead");
                        break;
                    }
                }
            }

            // 6. Each shape drawn where its band is: left to right in group
            //    order, and never collapsed onto the group beside it.
            //
            //    The point of bands is that two shapes cannot start on top of
            //    each other, and check 2 above already forbids any two *orbs*
            //    overlapping — but it would pass just as happily with three
            //    shapes interleaved into one indistinguishable cloud, which is
            //    the failure that actually matters here and the one the whole
            //    band mechanism exists to prevent.
            //
            //    Measured on the group centroids, and only over anchors: a team
            //    member is drawn hanging off its lead and belongs to whichever
            //    band its lead is standing in, whatever its own entry in groupOf
            //    says. Resolves is the same question Compute asks to tell the
            //    two apart.
            //
            //    Non-strict, with a window's slack — the largest orb's, which is
            //    what the lattice is laid out in. The separation pass runs
            //    after the bands are cut and is allowed to push a crowded orb
            //    across a boundary — that is it doing its job, and thirty orbs
            //    in three groups on a 1280-wide screen genuinely have nowhere
            //    tidier to be. A *swap*, or two centroids on the same point,
            //    still fails, which is what this is watching for.
            if (groups is not null && count > 1)
            {
                var sumX = new double[shapes.Length];
                var anchors = new int[shapes.Length];

                for (var i = 0; i < count; i++)
                {
                    if (OrbArrangement.Resolves(i, leads, count)) continue;   // a member, not an anchor

                    var g = i < groups.Length ? groups[i] : 0;
                    if (g < 0 || g >= shapes.Length) g = 0;

                    sumX[g] += CentreX(pts, i);
                    anchors[g]++;
                }

                var previous = double.MinValue;
                var previousGroup = -1;

                for (var g = 0; g < shapes.Length; g++)
                {
                    if (anchors[g] == 0) continue;

                    var centroid = sumX[g] / anchors[g];

                    if (previousGroup >= 0 && centroid + window < previous)
                    {
                        Fail($"group {g} is drawn left of group {previousGroup} "
                           + $"({centroid:0} against {previous:0})");
                        break;
                    }

                    previous = centroid;
                    previousGroup = g;
                }
            }

            return failures;
        }

        // ---------------------------------------------------------------------
        // CB-268: user groups, each with its own shape and its own anchor.
        //
        // A separate matrix rather than one more dimension on Case, because the
        // question is different. Case asks "is this one arrangement sane";
        // these ask it of several arrangements that must not get in each
        // other's way, and the inputs that matter — how many groups, where each
        // is anchored, how the teams straddle them — do not exist on Case.
        // ---------------------------------------------------------------------

        // Where each of N groups is anchored. A function of the screen and N
        // for the same reason a Split is a function of the count.
        //   none       nobody anchored: the first-draw route (Bands + Compact)
        //   partial    only group 0, so the rest are asked for the screen's middle
        //   spread     evenly across the middle, each honoured as asked
        //   colliding  all on the same point, which SeparateGroups must untangle
        //   off-screen alternately past the right edge and past the bottom
        //   negative   all far above and left of the screen
        internal static readonly string[] AnchorKinds =
            { "none", "partial", "spread", "colliding", "off-screen", "negative" };

        internal static PixelPoint?[] AnchorsOf(string kind, int groups, Screen screen)
        {
            var work = screen.Work;
            var at = new PixelPoint?[groups];

            for (var g = 0; g < groups; g++)
            {
                at[g] = kind switch
                {
                    "none" => null,
                    "partial" => g == 0 ? new PixelPoint(work.X + work.Width / 4, work.Y + work.Height / 4) : null,
                    "spread" => new PixelPoint(work.X + work.Width * (g + 1) / (groups + 1), work.Y + work.Height / 2),
                    "colliding" => new PixelPoint(work.X + work.Width / 2, work.Y + work.Height / 2),
                    "off-screen" => g % 2 == 0
                        ? new PixelPoint(work.Right + 800, work.Y + 40)
                        : new PixelPoint(work.X + 40, work.Bottom + 800),
                    _ => new PixelPoint(-4000, -4000),
                };
            }

            return at;
        }

        // How the orbs are dealt to the groups. Round robin deliberately puts
        // a team's members in different groups from their lead (T1: the
        // members must follow the lead anyway); blocks keeps neighbours
        // together, so most teams are whole already.
        internal static readonly string[] GroupSplits = { "round robin", "blocks" };

        internal static int[] DealTo(string split, int count, int groups) =>
            Enumerable.Range(0, count)
                .Select(i => split == "round robin" ? i % groups : Math.Min(groups - 1, i * groups / count))
                .ToArray();

        internal readonly record struct GroupCase(
            Screen Screen,
            string Shape,
            double Spacing,
            int Count,
            TeamShape Team,
            int Groups,
            string Anchors,
            string Split,
            SizeMix? Mix = null)
        {
            public override string ToString()
                => $"{Screen.Name} / {Shape} / spacing {Spacing:0.00} / {Count} orbs / {Team.Name}"
                 + $" / {Groups} groups, anchors {Anchors}, dealt {Split}"
                 + (Mix is { } mix ? $" / sizes {mix.Name}" : "");
        }

        internal static readonly int[] GroupCounts = { 3, 8, 20 };

        internal static readonly int[] GroupMixedCounts = { 5, 13 };

        // The size mixes above, plus every orb at the top of the slider: the
        // case that fills a screen with a few groups and is where the groups
        // stop fitting side by side and the whole set has to be drawn through
        // Bands + Compact instead.
        internal static readonly SizeMix[] GroupMixes = Mixes
            .Append(new SizeMix("everything at the top of the slider", n => Enumerable.Repeat(MaxSize, n).ToArray()))
            .ToArray();

        // 1–6 groups across every screen, shape, spacing and team shape, at
        // three orb counts (fewer orbs than groups, so slots go empty, up to a
        // crowd) and six anchor arrangements; then a smaller sweep with orbs of
        // different sizes. Slot s draws ShapesFor(shape)[s], so every shape is
        // exercised in every slot.
        internal static IEnumerable<GroupCase> GroupCases()
        {
            foreach (var screen in Screens)
            foreach (var groups in Enumerable.Range(1, 6))
            foreach (var kind in AnchorKinds)
            foreach (var shape in Shapes)
            foreach (var spacing in Spacings)
            foreach (var count in GroupCounts)
            foreach (var team in TeamShapes)
            foreach (var split in GroupSplits)
                yield return new GroupCase(screen, shape, spacing, count, team, groups, kind, split);

            foreach (var screen in Screens)
            foreach (var groups in new[] { 2, 4, 6 })
            foreach (var kind in new[] { "spread", "colliding" })
            foreach (var shape in Shapes)
            foreach (var count in GroupMixedCounts)
            foreach (var team in TeamShapes)
            foreach (var mix in GroupMixes)
                yield return new GroupCase(screen, shape, 0.85, count, team, groups, kind, "round robin", mix);
        }

        // The invariants for one user-group case. Returns what went wrong,
        // prefixed with the case, as Check does.
        internal static IReadOnlyList<string> CheckGroups(GroupCase test)
        {
            var screen = test.Screen;
            var count = test.Count;
            var failures = new List<string>();
            var where = test.ToString();

            void Fail(string what) => failures.Add($"{where}: {what}");

            var leads = test.Team.Build(count);
            var groupOf = DealTo(test.Split, count, test.Groups);
            var shapes = ShapesFor(test.Shape).Take(test.Groups).ToArray();
            var centres = AnchorsOf(test.Anchors, test.Groups, screen);

            var sizes = test.Mix?.Build(count) ?? Enumerable.Repeat(1.0, count).ToArray();
            double[]? sizeArgument = test.Mix is null ? null : sizes;

            var layout = new OrbArrangement.Layout(screen.Work, screen.Scale, shapes[0], test.Spacing);

            List<OrbArrangement.GroupLayout> Table(PixelPoint?[] at) =>
                shapes.Select((sh, g) => new OrbArrangement.GroupLayout(sh, at[g])).ToList();

            (PixelPoint[] Positions, OrbArrangement.GroupPlacement[] Groups) result;
            OrbArrangement.GroupPath path;

            try
            {
                result = OrbArrangement.ComputeGroups(
                    count, leads, groupOf, Table(centres), layout, sizeArgument, out path);
            }
            catch (Exception ex)
            {
                Fail($"threw {ex.GetType().Name}: {ex.Message}");
                return failures;
            }

            var (pts, placed) = result;

            if (pts.Length != count)
            {
                Fail($"returned {pts.Length} positions for {count} orbs");
                return failures;
            }

            var windowOf = sizes.Select(sz => (int)Math.Round(OrbArrangement.WindowDip * screen.Scale * sz)).ToArray();
            var circleOf = sizes.Select(sz => OrbArrangement.CircleDip * screen.Scale * sz).ToArray();

            // Written out, not asked of OrbArrangement: the expected slot of an
            // orb is its root lead's own groupOf entry, where the root is the
            // last orb on the lead chain, and an orb on a cycle or pointing at
            // nothing is its own root.
            int RootOf(int i)
            {
                var at = i;
                for (var hops = 0; hops <= count; hops++)
                {
                    var lead = leads[at];
                    if (lead < 0 || lead >= count || lead == at) return at;
                    if (OrbArrangement.Resolves(at, leads, count) is false) return at;
                    at = lead;
                }

                return at;
            }

            int ExpectedSlot(int i)
            {
                var g = groupOf[RootOf(i)];
                return g >= 0 && g < test.Groups ? g : 0;
            }

            // 1. The table is right: one entry per non-empty slot, ascending,
            //    each orb in exactly one, orbs ascending within a group.
            var seen = new int[count];
            var lastSlot = -1;

            foreach (var group in placed)
            {
                if (group.Slot <= lastSlot) Fail($"slot {group.Slot} listed after slot {lastSlot}");
                lastSlot = group.Slot;

                if (group.Orbs.Length == 0) Fail($"slot {group.Slot} is listed but empty");
                if (!group.Orbs.SequenceEqual(group.Orbs.OrderBy(o => o))) Fail($"slot {group.Slot}'s orbs are not ascending");

                foreach (var orb in group.Orbs) seen[orb]++;
            }

            if (seen.Any(c => c != 1))
            {
                Fail($"orbs not in exactly one group: {string.Join(",", seen.Select((c, i) => (c, i)).Where(t => t.c != 1).Select(t => t.i))}");
                return failures;
            }

            var expectedSlots = Enumerable.Range(0, count).Select(ExpectedSlot).Distinct().OrderBy(g => g).ToArray();
            if (!placed.Select(g => g.Slot).SequenceEqual(expectedSlots))
                Fail($"groups listed are [{string.Join(",", placed.Select(g => g.Slot))}], expected [{string.Join(",", expectedSlots)}]");

            // 2. A member's slot is its root lead's, whatever its own groupOf says.
            foreach (var group in placed)
            foreach (var orb in group.Orbs)
            {
                if (ExpectedSlot(orb) == group.Slot) continue;
                Fail($"orb {orb} is in slot {group.Slot}, its root lead {RootOf(orb)} puts it in {ExpectedSlot(orb)}");
                break;
            }

            // 3. Every orb fully on screen.
            for (var i = 0; i < count; i++)
            {
                if (pts[i].X < screen.Work.X || pts[i].Y < screen.Work.Y
                    || pts[i].X + windowOf[i] > screen.Work.Right || pts[i].Y + windowOf[i] > screen.Work.Bottom)
                {
                    Fail($"orb {i} at ({pts[i].X},{pts[i].Y}) is outside the work area");
                    break;
                }
            }

            // 4. No two circles on top of each other — across groups too, which
            //    is the point of separating them. Same radii and the same
            //    allowance at the bottom of the spacing slider as Check.
            double RadiusOf(int i) =>
                (leads[i] >= 0 && leads[i] < count ? circleOf[i] * OrbArrangement.MemberScale : circleOf[i]) / 2;

            double CentreX(int i) => pts[i].X + windowOf[i] / 2.0;
            double CentreY(int i) => pts[i].Y + windowOf[i] / 2.0;

            for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
            {
                var dx = CentreX(i) - CentreX(j);
                var dy = CentreY(i) - CentreY(j);
                var gap = Math.Sqrt(dx * dx + dy * dy) - RadiusOf(i) - RadiusOf(j);
                var allowed = test.Spacing <= 0.3 ? -Math.Max(circleOf[i], circleOf[j]) * 0.45 : 0;

                if (gap - allowed >= 0) continue;

                Fail($"orbs {i} and {j} overlap by {-gap:0}px (allowed {-allowed:0})");
                i = count;
                break;
            }

            // 5. Every group's slot box holds the windows of the orbs in it.
            foreach (var group in placed)
            foreach (var orb in group.Orbs)
            {
                var box = group.SlotBounds;
                if (pts[orb].X >= box.X && pts[orb].Y >= box.Y
                    && pts[orb].X + windowOf[orb] <= box.Right && pts[orb].Y + windowOf[orb] <= box.Bottom) continue;

                Fail($"orb {orb} sticks out of slot {group.Slot}'s box {box}");
                break;
            }

            // 6. On the separated route, no two groups' boxes overlap and each
            //    sits inside the work area — unless it is wider than the work
            //    area, which no placement can cure. The other routes make no such
            //    promise: Compact lays shapes out before their teams fan, and a
            //    fan can reach past its box.
            if (path == OrbArrangement.GroupPath.Separated)
            {
                for (var a = 0; a < placed.Length; a++)
                {
                    var box = placed[a].SlotBounds;

                    var fits = box.Width <= screen.Work.Width && box.Height <= screen.Work.Height;
                    if (fits && (box.X < screen.Work.X || box.Y < screen.Work.Y
                        || box.Right > screen.Work.Right || box.Bottom > screen.Work.Bottom))
                        Fail($"slot {placed[a].Slot}'s box {box} is outside the work area");

                    for (var b = a + 1; b < placed.Length; b++)
                    {
                        var other = placed[b].SlotBounds;
                        if (box.X < other.Right && other.X < box.Right && box.Y < other.Bottom && other.Y < box.Bottom)
                        {
                            Fail($"slot {placed[a].Slot}'s box {box} overlaps slot {placed[b].Slot}'s {other}");
                            a = placed.Length;
                            break;
                        }
                    }
                }
            }

            // 7. DrawnShape is the shape asked for, or a grid.
            foreach (var group in placed)
            {
                if (group.DrawnShape == shapes[group.Slot] || group.DrawnShape == "grid") continue;
                Fail($"slot {group.Slot} reports drawing {group.DrawnShape}, asked for {shapes[group.Slot]}");
                break;
            }

            // 8. Deterministic.
            var again = OrbArrangement.ComputeGroups(
                count, leads, groupOf, Table(centres), layout, sizeArgument, out var againPath);

            if (againPath != path || !pts.SequenceEqual(again.Positions) || !SamePlacements(placed, again.Groups))
                Fail("not deterministic — two runs disagreed");

            // 9. Feeding each group's Landed back as its anchor reproduces the
            //    layout exactly: what was reported is what was drawn, so saving
            //    it neither moves a group nor leaves an overshoot hiding in the
            //    settings (CB-211, per group). Only on the separated route; the
            //    others are read off a layout that had no per-group anchors, so
            //    the second draw is a different route by design.
            if (path == OrbArrangement.GroupPath.Separated)
            {
                var landed = Enumerable.Range(0, test.Groups)
                    .Select(g => placed.Where(p => p.Slot == g).Select(p => (PixelPoint?)p.Landed).FirstOrDefault() ?? centres[g])
                    .ToArray();

                var round = OrbArrangement.ComputeGroups(
                    count, leads, groupOf, Table(landed), layout, sizeArgument, out var roundPath);

                if (roundPath != OrbArrangement.GroupPath.Separated)
                    Fail($"feeding Landed back took the {roundPath} route");
                else if (!pts.SequenceEqual(round.Positions))
                {
                    var i = Enumerable.Range(0, count).First(k => pts[k] != round.Positions[k]);
                    Fail($"feeding Landed back moved orb {i} from ({pts[i].X},{pts[i].Y}) to ({round.Positions[i].X},{round.Positions[i].Y})");
                }
            }

            return failures;
        }

        private static bool SamePlacements(
            IReadOnlyList<OrbArrangement.GroupPlacement> a, IReadOnlyList<OrbArrangement.GroupPlacement> b) =>
            a.Count == b.Count
            && Enumerable.Range(0, a.Count).All(i =>
                a[i].Slot == b[i].Slot && a[i].Landed == b[i].Landed && a[i].SlotBounds == b[i].SlotBounds
                && a[i].DrawnShape == b[i].DrawnShape && a[i].Orbs.SequenceEqual(b[i].Orbs));

        // The whole sweep, for a caller that just wants the verdict. The
        // user-group cases (CB-268) are counted in with the rest.
        internal static (int Cases, List<string> Failures) RunAll()
        {
            var cases = 0;
            var failures = new List<string>();

            foreach (var test in Cases())
            {
                cases++;
                failures.AddRange(Check(test));
            }

            foreach (var test in GroupCases())
            {
                cases++;
                failures.AddRange(CheckGroups(test));
            }

            return (cases, failures);
        }

        // Grouped, because one broken rule usually fails hundreds of cases and
        // the list is only useful if the shape of the problem is visible. Shared
        // so that a failing xUnit assertion reads the same way as the console
        // run rather than dumping thousands of lines.
        internal static string Report(IReadOnlyList<string> failures)
        {
            var text = new System.Text.StringBuilder();

            foreach (var group in failures
                .GroupBy(f => f[(f.IndexOf(": ", StringComparison.Ordinal) + 2)..])
                .OrderByDescending(g => g.Count()))
            {
                text.AppendLine($"  {group.Count(),5}x  {group.Key}");
                text.AppendLine($"         e.g. {group.First()}");
            }

            return text.ToString();
        }
    }
}
