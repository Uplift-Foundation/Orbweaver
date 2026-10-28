using Avalonia;
using Xunit;
using static Orbweaver.OrbArrangement;

namespace Orbweaver.Tests;

// CB-268 S4 x S2: the join distance must be smaller than the gap ComputeGroups
// actually leaves between two groups, at every orb size the app allows —
// otherwise releasing an edge orb right after an arrange joins the neighbour it
// was only drawn beside (QA found this at size <= 0.75 with a fixed 24 DIP).
//
// Reads the real constants and functions: ComputeGroups lays the groups out,
// OrbDragCommit.Preview (the code a release runs) decides. Nothing restates the
// gap formula, so changing GroupGapWindows or the thresholds moves this test.
// The negative control moves the neighbour closer and expects the opposite.
public class OrbSnapGroupGapTests
{
    private static readonly PixelRect Work = new(0, 0, 3840, 2160);

    private sealed class Layouts : IOrbDragHost
    {
        public Dictionary<string, (double X, double Y, double R)> Discs { get; } = new();
        public Dictionary<string, string> Groups { get; } = new();

        public bool IsArranged => true;
        public IReadOnlyList<string> TeamIdsOf(string id) => new[] { id };
        public IReadOnlyList<string> OrbIdsInGroup(string g) => Groups.Where(x => x.Value == g).Select(x => x.Key).ToList();
        public OrbWindow? WindowOf(string id) => null;
        public string GroupIdOf(string id) => Groups[id];
        public string PositionKeyOf(string id) => id;
        public (double X, double Y, double R) DiscOf(string id) => Discs[id];

        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excluded) =>
            Groups.GroupBy(g => g.Value)
                .Select(g => new SnapCandidate(
                    g.Key, Array.Empty<Point>(),
                    g.Where(o => !excluded.Contains(o.Key)).Select(o => Discs[o.Key]).ToList()))
                .ToList();

        public void HighlightGroup(string? groupId) { }
        public void ShiftGroupAnchor(string groupId, int dx, int dy) { }
        public void ReapplyArrangement() { }
    }

    // Two anchored groups of `perGroup` orbs whose anchors are 30 px apart, so
    // they overlap and SeparateGroups has to push them to exactly its minimum
    // gap (anchors far apart would never be pushed and prove nothing). Side by side, sizes sa and sb,
    // laid out by ComputeGroups itself. Returns discs by orb and the host.
    private static (Layouts Host, string EdgeOrb) Arrange(double sa, double sb, int perGroup = 4, double scale = 1.0)
    {
        var count = perGroup * 2;
        var groupOf = Enumerable.Range(0, count).Select(i => i < perGroup ? 0 : 1).ToArray();
        var sizes = Enumerable.Range(0, count).Select(i => i < perGroup ? sa : sb).ToArray();
        var table = new[]
        {
            new GroupLayout("grid", new PixelPoint(1900, 1080)),
            new GroupLayout("grid", new PixelPoint(1930, 1080)),
        };

        var (positions, groups) = ComputeGroups(
            count, Enumerable.Repeat(-1, count).ToArray(), groupOf, table,
            new Layout(Work, scale, "grid", 0.85), sizes, out var path);

        // Only the separated route has a gap to speak of; a fall-back to bands
        // would make this test assert about something else.
        Assert.Equal(GroupPath.Separated, path);
        Assert.Equal(2, groups.Length);

        var host = new Layouts();
        for (var i = 0; i < count; i++)
        {
            var id = "o" + i;
            var window = WindowFor(scale, sizes[i]);
            host.Discs[id] = (positions[i].X + window / 2.0, positions[i].Y + window / 2.0, CircleDip * scale * sizes[i] / 2);
            host.Groups[id] = i < perGroup ? "A" : "B";
        }

        // The edge orb: the group-A orb nearest any group-B disc.
        var edge = host.Groups.Where(g => g.Value == "A")
            .OrderBy(g => host.Groups.Where(o => o.Value == "B").Min(o => EdgeGap(host.Discs[g.Key], host.Discs[o.Key])))
            .First().Key;

        return (host, edge);
    }

    private static double EdgeGap((double X, double Y, double R) a, (double X, double Y, double R) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) - a.R - b.R;

    // Every size the app allows: macOS's floor to the slider's top, a step apart.
    public static TheoryData<double> Sizes()
    {
        var data = new TheoryData<double>();
        for (var s = OrbSizing.MinMacOS; s <= OrbSizing.Max + 1e-9; s += OrbSizing.Step)
            data.Add(Math.Round(s, 2));
        return data;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AnEdgeOrbOfAFreshlyArrangedGroupDoesNotJoinItsNeighbour(double size)
    {
        var (host, edge) = Arrange(size, size);

        var outcome = OrbDragCommit.Preview(host, edge, 1);

        Assert.IsNotType<SnapOutcome.Join>(outcome);
    }

    [Theory]
    [InlineData(0.6, 2.0)]
    [InlineData(2.0, 0.6)]
    [InlineData(0.7, 1.0)]
    [InlineData(1.0, 0.7)]
    [InlineData(0.6, 0.6)]
    [InlineData(2.0, 2.0)]
    public void MixedSizesAcrossTheTwoGroupsKeepTheSameGuarantee(double sa, double sb)
    {
        var (host, edge) = Arrange(sa, sb);

        Assert.IsNotType<SnapOutcome.Join>(OrbDragCommit.Preview(host, edge, 1));

        // And the other way round: the neighbour's own edge orb.
        var other = host.Groups.Where(g => g.Value == "B")
            .OrderBy(g => host.Groups.Where(o => o.Value == "A").Min(o => EdgeGap(host.Discs[g.Key], host.Discs[o.Key])))
            .First().Key;
        Assert.IsNotType<SnapOutcome.Join>(OrbDragCommit.Preview(host, other, 1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheGuaranteeHoldsAtAHigherDisplayScale(double scale)
    {
        // Physical pixels per DIP; the orbs' discs and windows scale with it, and
        // so does the dragged orb's radius that sizes the thresholds.
        var (host, edge) = Arrange(1.0, 1.0, scale: scale);

        Assert.IsNotType<SnapOutcome.Join>(OrbDragCommit.Preview(host, edge, scale));
    }

    // The negative control: the same layout with the neighbouring group dragged
    // to half the join distance must join. If the assertions above held only
    // because the decision was blind to distance, this is what would catch it.
    [Theory]
    [MemberData(nameof(Sizes))]
    public void ShrinkingTheGapBelowTheJoinDistanceDoesJoin(double size)
    {
        var (host, edge) = Arrange(size, size);
        var join = OrbSnap.JoinDip * (host.Discs[edge].R / OrbSnap.BaseRadiusDip);
        var gap = host.Groups.Where(g => g.Value == "B").Min(g => EdgeGap(host.Discs[edge], host.Discs[g.Key]));
        Assert.True(gap > join, $"the real gap {gap:0.0} must exceed the join distance {join:0.0} before it can be shrunk");

        // Pull every group-B disc towards the edge orb, along the line joining
        // the nearest one, until the gap is half the join distance.
        var nearest = host.Groups.Where(g => g.Value == "B").OrderBy(g => EdgeGap(host.Discs[edge], host.Discs[g.Key])).First().Key;
        var from = host.Discs[nearest];
        var to = host.Discs[edge];
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var pull = gap - join / 2;
        foreach (var id in host.Groups.Where(g => g.Value == "B").Select(g => g.Key).ToList())
        {
            var d = host.Discs[id];
            host.Discs[id] = (d.X + dx / length * pull, d.Y + dy / length * pull, d.R);
        }

        Assert.IsType<SnapOutcome.Join>(OrbDragCommit.Preview(host, edge, 1));
    }
}
