using Xunit;

namespace Orbweaver.Tests
{
    // OrbArrangement's geometry, as a test rather than as an exe you have to
    // remember to run.
    //
    // The sweep is not new and nothing here re-states it: the case matrix and
    // the invariants live in tests/ArrangementTests/ArrangementSweep.cs, which
    // this project compiles in, so `dotnet run --project tests/ArrangementTests`
    // and this test are running the identical cases against the identical rules.
    // What is new is that the lines now count. That project is a plain console
    // exe with no test SDK reference, so tools/coverage.sh never saw it, and the
    // most exhaustively verified file in the repository reported 0.0% — a number
    // CLAUDE.md warns about precisely because it reads like a gap and is not
    // one.
    //
    // Kept as one Fact rather than a Theory per case. A hundred thousand xUnit
    // test cases would cost more in discovery and reporting than the sweep costs
    // to run, and the sweep's own grouped report is a better failure message
    // than that many individual results: one broken rule fails hundreds of cases, and what you
    // need to see is the rule, not the roll call.
    public class OrbArrangementSweepTests
    {
        [Fact]
        public void EveryArrangementKeepsOrbsOnScreenAndApart()
        {
            var (cases, failures) = ArrangementSweep.RunAll();

            Assert.True(
                failures.Count == 0,
                $"{failures.Count} of {cases} arrangement cases failed:\n"
                    + ArrangementSweep.Report(failures));
        }

        // A floor, not an equality, and the asymmetry is the point: adding a
        // shape or a screen to the shared matrix should not fail a test, but
        // losing cases silently should. The sweep is shared between two projects
        // now, so a well-meant edit in one of them can shrink what the other
        // verifies with nothing to show for it — this is the assertion that
        // notices.
        [Fact]
        public void TheSweepStillCoversTheWholeMatrix()
        {
            var cases = ArrangementSweep.Cases().ToList();

            // 108864 since CB-198 added orb sizes: the first sweep at four
            // sizes (both platforms' floors, 1.0 and the top), and a mixed-size
            // sweep. It stood at 62208 before that,
            // which had itself gone stale — the matrix it guarded was already
            // 72576 — and a floor that low would not have noticed the size
            // dimension being dropped. Raised to the real count, not near it.
            Assert.True(
                cases.Count >= 108864,
                $"the sweep is down to {cases.Count} cases from 108864 — "
                    + "if that was deliberate, raise the floor in this test");

            // All three sweeps present. The anchored half was added after a
            // shipped bug where the shape recentered itself on every orb that
            // joined or left, and the grouped half after one where a shape drawn
            // in its own band stopped honouring the anchor at all — so an edit
            // that dropped either would remove the regression test for the
            // reason that half exists.
            Assert.Contains(cases, c => c.Anchor is null && c.Split is null);
            Assert.Contains(cases, c => c.Anchor is not null && c.Split is null);
            Assert.Contains(cases, c => c.Split is not null);

            // And the grouped half is grouped: a split that puts every orb in
            // group 0 exercises the same one-band path as an ungrouped case, so
            // its presence alone would not prove the bands are ever cut.
            Assert.Contains(cases, c =>
                c.Split is { } split && split.Build(6).Distinct().Count() > 1);

            // Both ends of the orb-size slider, uniformly, and orbs of
            // different sizes side by side (CB-198) — the last of which is the
            // only half that exercises "laid out for the largest orb".
            Assert.Contains(cases, c => c.Mix is null && c.Size == OrbSizing.MinMacOS);
            Assert.Contains(cases, c => c.Mix is null && c.Size == OrbSizing.MinWindows);
            Assert.Contains(cases, c => c.Mix is null && c.Size == OrbSizing.Max);
            Assert.Contains(cases, c => c.Mix is { } mix && mix.Build(5).Distinct().Count() > 1);
        }

        // The user-group half (CB-268), which is its own matrix rather than
        // more rows of Cases() — the floor above guards the first three sweeps
        // and this guards the fourth, at 116928: 108864 across 1–6 groups ×
        // six anchor arrangements × every shape, spacing, team shape and
        // screen at three orb counts and two ways of dealing the orbs, and
        // 8064 with mixed sizes. Raised to the real count, not near it.
        [Fact]
        public void TheGroupSweepStillCoversTheWholeMatrix()
        {
            var cases = ArrangementSweep.GroupCases().ToList();

            Assert.True(
                cases.Count >= 116928,
                $"the group sweep is down to {cases.Count} cases from 116928 — "
                    + "if that was deliberate, raise the floor in this test");

            // Every dimension that earns its keep: from one group to six, each
            // way of anchoring them (including none, which is the first-draw
            // route), both ways of dealing the orbs, and mixed sizes.
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, cases.Select(c => c.Groups).Distinct().OrderBy(g => g));
            Assert.Equal(
                ArrangementSweep.AnchorKinds.OrderBy(k => k),
                cases.Select(c => c.Anchors).Distinct().OrderBy(k => k));
            Assert.Equal(2, cases.Select(c => c.Split).Distinct().Count());
            Assert.Contains(cases, c => c.Mix is not null);

            // T1 is only proven if a team's members are dealt to a different
            // group from their lead somewhere in it.
            Assert.Contains(cases, c =>
            {
                var leads = c.Team.Build(c.Count);
                var of = ArrangementSweep.DealTo(c.Split, c.Count, c.Groups);
                return Enumerable.Range(0, c.Count).Any(i => leads[i] >= 0 && leads[i] < c.Count && of[i] != of[leads[i]]);
            });
        }
    }
}
