using Avalonia;
using Xunit;
using static Orbweaver.OrbArrangement;

namespace Orbweaver.Tests
{
    // CB-268: ComputeGroups' own promises, the ones a sweep of invariants
    // cannot state — which route was taken, what Landed means, that a group's
    // picture is its own and moves by exactly the delta a drag gave it, and
    // that a member follows its root lead's slot whatever its own entry says.
    // The sweep (ArrangementSweep.GroupCases) proves nothing overlaps and
    // nothing leaves the screen across 112896 cases; this file proves the
    // arrangement is the right one.
    public class OrbArrangementGroupAnchorTests
    {
        private static readonly PixelRect Work = new(0, 0, 1920, 1080);

        private static Layout LayoutAt(double scale = 1.0, double spacing = 0.85, PixelRect? work = null) =>
            new(work ?? Work, scale, "circle", spacing);

        private static GroupLayout[] Table(params (string Shape, PixelPoint? At)[] groups) =>
            groups.Select(g => new GroupLayout(g.Shape, g.At)).ToArray();

        private static int[] NoLeads(int n) => Enumerable.Repeat(-1, n).ToArray();

        private static int[] Dealt(int n, int groups) => Enumerable.Range(0, n).Select(i => i % groups).ToArray();

        private static bool Overlap(PixelRect a, PixelRect b) =>
            a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

        // --- routes ---

        [Fact]
        public void NoOrbsIsNoPositionsAndNoGroups()
        {
            var (positions, groups) = ComputeGroups(
                0, Array.Empty<int>(), Array.Empty<int>(), Table(("circle", null)), LayoutAt());

            Assert.Empty(positions);
            Assert.Empty(groups);
        }

        [Fact]
        public void WithNoAnchorsEachGroupIsAskedForTheMiddleOfItsBand()
        {
            var table = Table(("circle", null), ("circle", null), ("circle", null));
            var of = Dealt(9, 3);

            var (_, groups, path) = Run(9, NoLeads(9), of, table);

            Assert.Equal(GroupPath.Separated, path);

            // The strip of screen Bands cuts for each: left to right in slot
            // order, and each group landed in its own third.
            var bands = Bands(Work, new[] { 3, 3, 3 }, 56);
            for (var g = 0; g < 3; g++)
                Assert.True(
                    groups[g].Landed.X >= bands[g].X - 60 && groups[g].Landed.X <= bands[g].Right + 60,
                    $"slot {g} landed at {groups[g].Landed} outside its band {bands[g]}");
        }

        [Fact]
        public void TheFirstDrawIsAFunctionOfTheAnchorsSoFeedingLandedBackReproducesItOrbForOrb()
        {
            var leads = new[] { -1, 0, -1, -1, 3, -1, -1, -1, 6 };
            var of = new[] { 0, 1, 1, 2, 0, 2, 3, 3, 0 };

            // None anchored, then one anchored and the rest not, then all.
            foreach (var anchors in new[]
            {
                new PixelPoint?[] { null, null, null, null },
                new PixelPoint?[] { null, new PixelPoint(1500, 300), null, null },
                new PixelPoint?[] { new PixelPoint(400, 300), new PixelPoint(900, 700), new PixelPoint(1500, 300), new PixelPoint(5000, 5000) },
            })
            {
                var shapes = new[] { "heart", "star", "line", "heart" };
                var (first, groups, path) = Run(9, leads, of, Table(shapes.Select((sh, i) => (sh, anchors[i])).ToArray()));

                var fed = shapes.Select((sh, g) => (sh, (PixelPoint?)groups.FirstOrDefault(p => p.Slot == g).Landed)).ToArray();
                var (second, groups2, path2) = Run(9, leads, of, Table(fed));

                Assert.Equal(path, path2);
                Assert.Equal(first, second);
                Assert.Equal(groups.Select(g => g.Landed), groups2.Select(g => g.Landed));
            }
        }

        [Fact]
        public void AnEmptyGroupTableIsOneGroupDrawnInTheLayoutsShape()
        {
            var (positions, groups) = ComputeGroups(
                4, NoLeads(4), new int[4], Array.Empty<GroupLayout>(), LayoutAt());

            Assert.Equal(4, positions.Length);
            var only = Assert.Single(groups);
            Assert.Equal(0, only.Slot);
            Assert.Equal("circle", only.DrawnShape);
        }

        [Fact]
        public void ABlankShapeInTheTableFallsBackToTheLayoutsShape()
        {
            var (_, groups) = ComputeGroups(
                4, NoLeads(4), new int[4], Table(("", new PixelPoint(900, 500))), LayoutAt());

            Assert.Equal("circle", Assert.Single(groups).DrawnShape);
        }

        [Fact]
        public void OneAnchorAmongNoneTakesTheAnchoredRoute()
        {
            var (_, groups, path) = Run(
                6, NoLeads(6), Dealt(6, 2), Table(("circle", new PixelPoint(500, 300)), ("heart", null)));

            Assert.Equal(GroupPath.Separated, path);
            Assert.Equal(2, groups.Length);
        }

        private static (PixelPoint[] Positions, GroupPlacement[] Groups, GroupPath Path) Run(
            int count, int[] leads, int[] of, GroupLayout[] table, Layout? layout = null, double[]? sizes = null)
        {
            var (positions, groups) = ComputeGroups(count, leads, of, table, layout ?? LayoutAt(), sizes, out var path);
            return (positions, groups, path);
        }

        // A heart's anchor is not the middle of its box: an early draft read the
        // first draw's Landed off the box middle and the redraw from the saved
        // anchors put the heart 162 px lower (found by S7).
        [Theory]
        [InlineData("heart")]
        [InlineData("circle")]
        [InlineData("star")]
        [InlineData("diamond")]
        public void TheFirstDrawsLandedFedBackReproducesIt(string mainShape)
        {
            var table = Table((mainShape, null), ("circle", null), ("line", null), ("heart", null));
            var of = new[] { 0, 0, 3 };

            var (first, groups, _) = Run(3, NoLeads(3), of, table);

            var fed = groups.ToDictionary(g => g.Slot, g => (PixelPoint?)g.Landed);
            var again = Table((mainShape, fed[0]), ("circle", null), ("line", null), ("heart", fed[3]));

            var (second, _, _) = Run(3, NoLeads(3), of, again);

            Assert.Equal(first, second);
        }

        // --- anchors and Landed ---

        [Fact]
        public void AGroupThatFitsIsDrawnAroundItsAnchorAndLandsExactlyThere()
        {
            var asked = new PixelPoint(700, 500);

            var (positions, groups, path) = Run(5, NoLeads(5), new int[5], Table(("circle", asked)));

            Assert.Equal(GroupPath.Separated, path);
            Assert.Equal(asked, Assert.Single(groups).Landed);

            // And drawn about it: the box is centred within a window or so.
            var box = groups[0].SlotBounds;
            Assert.InRange(box.X + box.Width / 2.0, asked.X - 60, asked.X + 60);
            Assert.InRange(box.Y + box.Height / 2.0, asked.Y - 60, asked.Y + 60);
        }

        [Fact]
        public void AnAnchorThatCannotBeHonouredLandsWhereTheGroupActuallyIs()
        {
            var table = Table(("circle", new PixelPoint(-4000, -4000)));

            var (positions, groups, _) = Run(8, NoLeads(8), new int[8], table);

            var landed = groups[0].Landed;
            var box = groups[0].SlotBounds;

            // Flush against the top-left of the work area, and the saved anchor
            // is the one that put it there — not the -4000 it was asked for.
            Assert.Equal(Work.X, box.X);
            Assert.Equal(Work.Y, box.Y);
            Assert.InRange(landed.X, 0, Work.Width);
            Assert.InRange(landed.Y, 0, Work.Height);

            // Feeding it back is exact (CB-211, per group).
            var (again, _, _) = Run(8, NoLeads(8), new int[8], Table(("circle", landed)));
            Assert.Equal(positions, again);
        }

        [Fact]
        public void LandedOfEveryGroupRoundTripsEvenWhenSeveralWereMoved()
        {
            var table = Table(
                ("circle", new PixelPoint(900, 500)), ("heart", new PixelPoint(900, 500)), ("star", new PixelPoint(5000, 5000)));
            var of = Dealt(12, 3);

            var (positions, groups, path) = Run(12, NoLeads(12), of, table);
            Assert.Equal(GroupPath.Separated, path);

            var fed = Table(
                ("circle", groups[0].Landed), ("heart", groups[1].Landed), ("star", groups[2].Landed));
            var (again, again2, path2) = Run(12, NoLeads(12), of, fed);

            Assert.Equal(GroupPath.Separated, path2);
            Assert.Equal(positions, again);
            Assert.Equal(groups.Select(g => g.Landed), again2.Select(g => g.Landed));
        }

        [Fact]
        public void TheGroupWithoutAnAnchorIsAskedForItsBandsMiddleAndReportsWhereItLanded()
        {
            var table = Table(("circle", new PixelPoint(1500, 300)), ("grid", null));

            var (_, groups, _) = Run(8, NoLeads(8), Dealt(8, 2), table);

            Assert.False(Overlap(groups[0].SlotBounds, groups[1].SlotBounds));

            // Slot 1 was nobody's anchor: it is asked for the middle of its
            // band (the right half of two equal bands, 1440) and, being in the
            // way of slot 0 at (1500,300) or not, reports what it landed at.
            var band = Bands(Work, new[] { 4, 4 }, 56)[1];
            Assert.InRange(groups[1].Landed.X, band.X - 400, band.Right + 400);
        }

        [Fact]
        public void CollidingAnchorsAreSeparatedAndTheEarlierSlotKeepsItsPlace()
        {
            var at = new PixelPoint(960, 540);
            var table = Table(("circle", at), ("circle", at), ("circle", at));

            var (_, groups, path) = Run(9, NoLeads(9), Dealt(9, 3), table);

            Assert.Equal(GroupPath.Separated, path);
            Assert.Equal(at, groups[0].Landed);
            Assert.False(Overlap(groups[0].SlotBounds, groups[1].SlotBounds));
            Assert.False(Overlap(groups[0].SlotBounds, groups[2].SlotBounds));
            Assert.False(Overlap(groups[1].SlotBounds, groups[2].SlotBounds));
        }

        [Fact]
        public void DraggingOneGroupMovesItsOrbsByExactlyTheDeltaAndNobodyElse()
        {
            var one = new PixelPoint(500, 400);
            var two = new PixelPoint(1400, 600);
            var of = Dealt(10, 2);
            var leads = new[] { -1, -1, 0, 1, 2, 3, -1, -1, -1, -1 };

            var (before, _, _) = Run(10, leads, of, Table(("heart", one), ("star", two)));
            var (after, _, _) = Run(10, leads, of, Table(("heart", new PixelPoint(one.X + 38, one.Y + 24)), ("star", two)));

            for (var i = 0; i < 10; i++)
            {
                // Who moves is decided by the root: orbs 2 and 4 hang off
                // orb 0 (slot 0), 3 and 5 off orb 1 (slot 1).
                var inSlot0 = Root(i, leads, of) == 0;
                Assert.Equal(inSlot0 ? new PixelPoint(before[i].X + 38, before[i].Y + 24) : before[i], after[i]);
            }
        }

        private static int Root(int orb, int[] leads, int[] of)
        {
            var at = orb;
            while (leads[at] >= 0) at = leads[at];
            return of[at];
        }

        [Fact]
        public void AGroupsPictureDoesNotDependOnTheGroupsBesideIt()
        {
            var a = Table(("heart", new PixelPoint(600, 500)));
            var ab = Table(("heart", new PixelPoint(600, 500)), ("star", new PixelPoint(1500, 500)));

            var (solo, _, _) = Run(7, NoLeads(7), new int[7], a);

            // The same seven orbs in slot 0, plus six more in slot 1 far away.
            var (both, groups, _) = Run(13, NoLeads(13), Enumerable.Range(0, 13).Select(i => i < 7 ? 0 : 1).ToArray(), ab);

            Assert.Equal(solo, both.Take(7));
            Assert.Equal(2, groups.Length);
        }

        // --- the curve the orbs sit on ---

        private static double Residual(GroupPlacement group, PixelPoint[] positions, double scale, int[] leads)
        {
            var anchors = group.Orbs.Where(i => leads[i] < 0).ToArray();
            var unit = Unit(group.DrawnShape, anchors.Length);
            var worst = 0.0;

            for (var k = 0; k < anchors.Length; k++)
            {
                var window = WindowFor(scale, 1.0);
                var dx = positions[anchors[k]].X + window / 2.0 - (group.CurveCentre.X + group.CurveScale * unit[k].X);
                var dy = positions[anchors[k]].Y + window / 2.0 - (group.CurveCentre.Y + group.CurveScale * unit[k].Y);
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
            }

            return worst;
        }

        [Theory]
        [InlineData("heart", 5, 1.0)]
        [InlineData("heart", 13, 1.0)]
        [InlineData("heart", 13, 2.0)]
        [InlineData("circle", 8, 1.0)]
        [InlineData("diamond", 9, 1.0)]
        [InlineData("star", 10, 1.0)]
        [InlineData("star", 10, 2.0)]
        [InlineData("grid", 12, 1.0)]
        [InlineData("line", 7, 1.0)]
        [InlineData("vline", 7, 2.0)]
        public void EveryOrbOfAnUnfannedGroupLiesOnTheCurveItReports(string shape, int n, double scale)
        {
            var layout = LayoutAt(scale, work: scale > 1 ? new PixelRect(0, 0, 3024, 1890) : null);
            var table = Table((shape, new PixelPoint(900, 500)), ("circle", new PixelPoint(1600, 500)));
            var of = Enumerable.Range(0, n + 3).Select(i => i < n ? 0 : 1).ToArray();

            var (positions, groups, _) = Run(n + 3, NoLeads(n + 3), of, table, layout);

            var group = groups[0];
            Assert.Equal(shape, group.DrawnShape);
            Assert.True(group.CurveScale > 0);

            // Pixel rounding plus the nudges that keep neighbours apart: a few
            // pixels, nowhere near an orb's width. A star's valleys sit close
            // to its points and are nudged the most (7.5 px measured at size
            // 1), so it gets twice the allowance.
            var allowed = (shape == "star" ? 8 : 4) * scale;
            Assert.True(Residual(group, positions, scale, NoLeads(n + 3)) <= allowed,
                $"worst orb is {Residual(group, positions, scale, NoLeads(n + 3)):0.0}px off the curve");
        }

        [Fact]
        public void ALoneOrbHasNoCurveAndItsCentreIsTheOrbs()
        {
            var (positions, groups, _) = Run(1, NoLeads(1), new int[1], Table(("heart", new PixelPoint(700, 400))));

            var only = Assert.Single(groups);
            Assert.Equal(0, only.CurveScale);
            Assert.Equal(positions[0].X + 28.0, only.CurveCentre.X);
            Assert.Equal(positions[0].Y + 28.0, only.CurveCentre.Y);
        }

        [Fact]
        public void ATeamIsLeftOffTheCurveItsLeadsSitOn()
        {
            // Leads 0 and 3 are anchors; 1, 2 and 4 are fanned members.
            var leads = new[] { -1, 0, 0, -1, 3 };

            var (positions, groups, _) = Run(5, leads, new int[5], Table(("circle", new PixelPoint(900, 500))));

            var only = Assert.Single(groups);
            var unit = Unit("circle", 2);

            // Two anchors, two unit points: the leads fit, to the pixel rounding.
            for (var k = 0; k < 2; k++)
            {
                var lead = new[] { 0, 3 }[k];
                Assert.Equal(only.CurveCentre.X + only.CurveScale * unit[k].X, positions[lead].X + 28.0, 2.0);
                Assert.Equal(only.CurveCentre.Y + only.CurveScale * unit[k].Y, positions[lead].Y + 28.0, 2.0);
            }
        }

        [Fact]
        public void TheCurveMovesWithTheGroupByExactlyTheDrag()
        {
            var table = Table(("heart", new PixelPoint(700, 400)));
            var moved = Table(("heart", new PixelPoint(738, 424)));

            var (_, before, _) = Run(8, NoLeads(8), new int[8], table);
            var (_, after, _) = Run(8, NoLeads(8), new int[8], moved);

            Assert.Equal(before[0].CurveScale, after[0].CurveScale);
            Assert.Equal(before[0].CurveCentre.X + 38, after[0].CurveCentre.X, 1e-6);
            Assert.Equal(before[0].CurveCentre.Y + 24, after[0].CurveCentre.Y, 1e-6);
        }

        // --- membership ---

        [Fact]
        public void AMemberTakesItsRootLeadsSlotWhateverItsOwnEntrySays()
        {
            // Orb 0 leads 1, 1 leads 2: a chain. Lead in slot 1; the members'
            // own entries say 0 and 2.
            var leads = new[] { -1, 0, 1, -1 };
            var of = new[] { 1, 0, 2, 0 };
            var table = Table(("circle", new PixelPoint(500, 500)), ("circle", new PixelPoint(1300, 500)), ("circle", new PixelPoint(900, 200)));

            var (_, groups, _) = Run(4, leads, of, table);

            Assert.Equal(new[] { 0, 1 }, groups.Select(g => g.Slot));
            Assert.Equal(new[] { 3 }, groups[0].Orbs);
            Assert.Equal(new[] { 0, 1, 2 }, groups[1].Orbs);
        }

        [Fact]
        public void ACycleAndADanglingLeadMakeAnchorsThatKeepTheirOwnSlots()
        {
            // 0 and 1 lead each other (both anchors), 2 points at nothing.
            var leads = new[] { 1, 0, 40, -1 };
            var of = new[] { 0, 1, 2, 2 };
            var table = Table(("circle", new PixelPoint(500, 500)), ("circle", new PixelPoint(900, 500)), ("circle", new PixelPoint(1400, 500)));

            var (_, groups, _) = Run(4, leads, of, table);

            Assert.Equal(new[] { 0 }, groups[0].Orbs);
            Assert.Equal(new[] { 1 }, groups[1].Orbs);
            Assert.Equal(new[] { 2, 3 }, groups[2].Orbs);
        }

        [Fact]
        public void AnIndexNothingHasAShapeForReadsAsSlotZeroAndAShortArrayToo()
        {
            var table = Table(("circle", new PixelPoint(800, 500)), ("heart", new PixelPoint(1400, 500)));

            var (_, groups, _) = Run(4, NoLeads(4), new[] { 9, -3 }, table);

            var only = Assert.Single(groups);
            Assert.Equal(0, only.Slot);
            Assert.Equal(new[] { 0, 1, 2, 3 }, only.Orbs);
        }

        [Fact]
        public void EmptySlotsStayInTheTableSoTheOthersKeepTheirIds()
        {
            var table = Table(
                ("circle", new PixelPoint(400, 300)), ("heart", new PixelPoint(900, 300)),
                ("star", new PixelPoint(1400, 300)), ("diamond", new PixelPoint(1400, 800)));

            var (_, groups, _) = Run(4, NoLeads(4), new[] { 0, 3, 0, 3 }, table);

            Assert.Equal(new[] { 0, 3 }, groups.Select(g => g.Slot));
            Assert.Equal("diamond", groups[1].DrawnShape);
        }

        // --- hostile inputs and mixed sizes ---

        [Fact]
        public void EachGroupsSlotsAreSizedByItsOwnLargestOrb()
        {
            var sizes = new[] { 2.0, 2.0, 0.7, 0.7 };
            var table = Table(("circle", new PixelPoint(600, 500)), ("circle", new PixelPoint(1500, 500)));

            var (positions, groups, _) = Run(4, NoLeads(4), new[] { 0, 0, 1, 1 }, table, sizes: sizes);

            // Slots are 112 px at 200% and 39 px at 70%. Two orbs of the small
            // group sit a slot or two apart — nowhere near 112 — which they
            // would if the lattice were sized for the biggest orb on screen.
            Assert.True(groups[1].SlotBounds.Width < groups[0].SlotBounds.Width);
            Assert.True(positions[3].X - positions[2].X < 112 || positions[3].Y - positions[2].Y < 112);
        }

        [Fact]
        public void ASqueezedGroupReportsItDrewAGrid()
        {
            // Thirty 200% orbs in a line: wider than any screen, so the retry
            // draws a grid, and the placement says so.
            var sizes = Enumerable.Repeat(2.0, 30).ToArray();
            var small = new PixelRect(0, 0, 1280, 800);

            var (_, groups, _) = Run(
                30, NoLeads(30), new int[30], Table(("line", new PixelPoint(640, 400))), LayoutAt(work: small), sizes);

            Assert.Equal("grid", Assert.Single(groups).DrawnShape);
        }

        [Fact]
        public void TooManyBigGroupsFallBackAndStillPutEveryOrbOnTheScreen()
        {
            var small = new PixelRect(0, 0, 1280, 800);
            var at = new PixelPoint(640, 400);
            var table = Enumerable.Repeat(("grid", (PixelPoint?)at), 3).ToArray();
            var sizes = Enumerable.Repeat(2.0, 60).ToArray();

            var (positions, groups, path) = Run(60, NoLeads(60), Dealt(60, 3), Table(table), LayoutAt(work: small), sizes);

            Assert.Equal(GroupPath.Fallback, path);
            Assert.Equal(3, groups.Length);

            // Landed is what was asked for (the fallback ignores anchors), so
            // feeding it back asks the same question and gets the same layout.
            var fed = groups.Select(g => ("grid", (PixelPoint?)g.Landed)).ToArray();
            var (again, _, againPath) = Run(60, NoLeads(60), Dealt(60, 3), Table(fed), LayoutAt(work: small), sizes);
            Assert.Equal(GroupPath.Fallback, againPath);
            Assert.Equal(positions, again);
            Assert.All(positions, p =>
            {
                Assert.True(p.X >= 0 && p.Y >= 0 && p.X + 112 <= 1280 && p.Y + 112 <= 800);
            });
        }

        [Fact]
        public void TheSameInputsGiveTheSameAnswer()
        {
            var table = Table(("heart", new PixelPoint(300, 300)), ("star", new PixelPoint(320, 310)));
            var leads = new[] { -1, 0, -1, 2, -1, -1 };

            var a = Run(6, leads, Dealt(6, 2), table);
            var b = Run(6, leads, Dealt(6, 2), table);

            Assert.Equal(a.Positions, b.Positions);
            Assert.Equal(a.Path, b.Path);
            Assert.Equal(a.Groups.Select(g => (g.Slot, g.Landed, g.SlotBounds, g.DrawnShape)), b.Groups.Select(g => (g.Slot, g.Landed, g.SlotBounds, g.DrawnShape)));
        }
    }
}
