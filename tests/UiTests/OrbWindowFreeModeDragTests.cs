using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S4, F3: the free-mode gestures through the real pointer handlers.
// Warren's hands-on test found snapping did nothing visible in free mode, so
// here nothing is arranged: two loose orbs dropped together pair into a user
// group, a third joins it, one drags out and floats, and a team MEMBER dropped
// beside a loose orb pairs both whole teams.
//
// The roster is a faithful stand-in for what SessionManager does in free mode:
// membership comes from the real settings (so a release's writes are what the
// next gesture sees), the candidates are the user groups plus one loose
// candidate per ungrouped team, and `main` is not a candidate.
//
// Only drags are driven, never a bare press and release, which would resolve a
// click and reach TerminalFocuser (see OrbWindowDragSnapTests).
[Collection("Settings")]
public class OrbWindowFreeModeDragTests
{
    private sealed class FreeRoster : IOrbDragHost
    {
        public Dictionary<string, OrbWindow> Windows { get; } = new();
        public Dictionary<string, string[]> Teams { get; } = new();
        public bool Arranged { get; set; }
        public int ArrangementReapplied { get; private set; }

        public bool IsArranged => Arranged;
        public bool IsFree => !Arranged;

        public IReadOnlyList<string> TeamIdsOf(string id) =>
            Teams.Values.FirstOrDefault(t => t.Contains(id)) ?? new[] { id };

        public string GroupIdOf(string id) =>
            OrbweaverSettings.OrbGroupMembershipFor("key:" + id) ?? OrbGroups.Main;

        public IReadOnlyList<string> OrbIdsInGroup(string g) =>
            Windows.Keys.Where(id => GroupIdOf(id) == g).ToList();

        public OrbWindow? WindowOf(string id) => Windows.GetValueOrDefault(id);
        public string PositionKeyOf(string id) => "key:" + id;

        // Orbs are 36 px discs at their window's top-left + 28.
        public (double X, double Y, double R) DiscOf(string id)
        {
            var p = Windows[id].Position;
            return (p.X + 28, p.Y + 28, 18);
        }

        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excluded)
        {
            var result = new List<SnapCandidate>();
            var rest = Windows.Keys.Where(id => !excluded.Contains(id)).ToList();

            foreach (var g in rest.Where(id => !OrbGroups.IsPredefined(GroupIdOf(id))).GroupBy(GroupIdOf))
                result.Add(new SnapCandidate(g.Key, Array.Empty<Point>(), g.Select(DiscOf).ToList()));

            foreach (var team in rest.Where(id => OrbGroups.IsPredefined(GroupIdOf(id))).GroupBy(id => TeamIdsOf(id)[0]))
                result.Add(new SnapCandidate(team.Key, Array.Empty<Point>(), team.Select(DiscOf).ToList(), Loose: true));

            return result;
        }

        public void HighlightGroup(string? groupId) { }
        public void ShiftGroupAnchor(string groupId, int dx, int dy) { }
        public void ReapplyArrangement() => ArrangementReapplied++;

        public OrbWindow Add(string id, int x, int y)
        {
            var w = new OrbWindow(id) { PositionKey = "key:" + id, Position = new PixelPoint(x, y) };
            Windows[id] = w;
            return w;
        }
    }

    private sealed class HostScope : IDisposable
    {
        public HostScope(IOrbDragHost host) => OrbDragHost.Current = host;
        public void Dispose() => OrbDragHost.Current = null;
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    private static void Drag(OrbWindow w, int dx, int dy)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        w.RaiseEvent(new PointerPressedEventArgs(
            w, pointer, w, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        w.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, w, pointer, w, new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        w.RaiseEvent(new PointerReleasedEventArgs(
            w, pointer, w, new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private static string GroupOf(string id) => OrbweaverSettings.OrbGroupMembershipFor("key:" + id) ?? "(loose)";

    [AvaloniaFact]
    public void TwoLooseOrbsPairAThirdJoinsAndOneLeavesToFree()
    {
        var roster = new FreeRoster();
        var u = Uid();
        var (a, b, c) = ("a-" + u, "b-" + u, "c-" + u);
        roster.Add(a, 100, 100);
        roster.Add(b, 400, 100);
        roster.Add(c, 700, 400);
        using var _ = new HostScope(roster);

        // 1. Drop A beside B (disc gap 4): they pair into a new user group.
        Drag(roster.Windows[a], 260, 0);

        var group = GroupOf(a);
        Assert.StartsWith("g-", group);
        Assert.Equal(group, GroupOf(b));
        Assert.Equal("(loose)", GroupOf(c));
        Assert.NotNull(OrbweaverSettings.OrbGroupFor(group)!.Anchor);
        // The dragged orb belongs to a group now, so it is not pinned as a loose one.
        Assert.False(roster.Windows[a].IsPinned);
        Assert.Equal(1, roster.ArrangementReapplied);

        // 2. Drop C beside the pair (14 dip from B's disc): it joins that group.
        Drag(roster.Windows[c], -250, -300);

        Assert.Equal(group, GroupOf(c));

        // 3. Drag A far away from both: it leaves to free, unassigned and pinned
        // where it was dropped, while B and C keep the group.
        Drag(roster.Windows[a], 0, 400);

        Assert.Equal("(loose)", GroupOf(a));
        Assert.Equal(group, GroupOf(b));
        Assert.Equal(group, GroupOf(c));
        Assert.True(roster.Windows[a].IsPinned);
        Assert.Equal(new PixelPoint(360, 500), roster.Windows[a].Position);
    }

    [AvaloniaFact]
    public void ATeamDroppedBesideALooseOrbPairsBothWholeTeams()
    {
        var roster = new FreeRoster();
        var u = Uid();
        var (lead, member, loose) = ("lead-" + u, "mem-" + u, "loose-" + u);
        roster.Add(lead, 100, 100);
        roster.Add(member, 160, 100);
        roster.Add(loose, 600, 100);
        roster.Teams[lead] = new[] { lead, member };
        using var _ = new HostScope(roster);

        // Drag the LEAD: the team's member disc ends at (608,128), touching the
        // loose orb's at (628,128).
        Drag(roster.Windows[lead], 420, 0);

        var group = GroupOf(lead);
        Assert.StartsWith("g-", group);
        Assert.Equal(group, GroupOf(member));
        Assert.Equal(group, GroupOf(loose));
        // The whole team travelled with the dragged lead.
        Assert.Equal(new PixelPoint(520, 100), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(580, 100), roster.Windows[member].Position);
        Assert.False(roster.Windows[lead].IsPinned);
    }

    [AvaloniaFact]
    public void ATeamMemberDroppedBesideALooseOrbMovesAloneAndPairsNothing()
    {
        // T2 revised: a member drag never snaps; it is pinned where it lands.
        var roster = new FreeRoster();
        var u = Uid();
        var (lead, member, loose) = ("lead-" + u, "mem-" + u, "loose-" + u);
        roster.Add(lead, 100, 100);
        roster.Add(member, 160, 100);
        roster.Add(loose, 600, 100);
        roster.Teams[lead] = new[] { lead, member };
        using var _ = new HostScope(roster);

        Drag(roster.Windows[member], 410, 0);

        Assert.Equal("(loose)", GroupOf(member));
        Assert.Equal("(loose)", GroupOf(loose));
        Assert.Equal(new PixelPoint(100, 100), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(570, 100), roster.Windows[member].Position);
        Assert.True(roster.Windows[member].IsPinned);
    }

    [AvaloniaFact]
    public void ALooseOrbDroppedFarFromEverythingStaysLooseAndPinned()
    {
        var roster = new FreeRoster();
        var u = Uid();
        var (a, b) = ("a-" + u, "b-" + u);
        roster.Add(a, 100, 100);
        roster.Add(b, 900, 100);
        using var _ = new HostScope(roster);

        Drag(roster.Windows[a], 50, 50);

        Assert.Equal("(loose)", GroupOf(a));
        Assert.True(roster.Windows[a].IsPinned);
        Assert.Equal(new PixelPoint(150, 150), roster.Windows[a].Position);
    }

    [AvaloniaFact]
    public void ShiftOnALooseOrbMovesItsOwnTeamNotEveryLooseOrb()
    {
        var roster = new FreeRoster();
        var u = Uid();
        var (a, b) = ("a-" + u, "b-" + u);
        roster.Add(a, 100, 100);
        roster.Add(b, 900, 100);
        using var _ = new HostScope(roster);

        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var w = roster.Windows[a];
        w.RaiseEvent(new PointerPressedEventArgs(
            w, pointer, w, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.Shift, 1));
        w.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, w, pointer, w, new Point(40, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.Shift));
        w.RaiseEvent(new PointerReleasedEventArgs(
            w, pointer, w, new Point(40, 10), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.Shift, MouseButton.Left));

        Assert.Equal(new PixelPoint(130, 100), roster.Windows[a].Position);
        Assert.Equal(new PixelPoint(900, 100), roster.Windows[b].Position);
    }

    [AvaloniaFact]
    public void TheSameDropWhileArrangedDoesNotPair()
    {
        // Mode switching: arranged mode keeps today's rule, where `main` is a
        // group and two orbs in it are not individual targets.
        var roster = new FreeRoster { Arranged = true };
        var u = Uid();
        var (a, b) = ("a-" + u, "b-" + u);
        roster.Add(a, 100, 100);
        roster.Add(b, 400, 100);
        using var _ = new HostScope(roster);

        Drag(roster.Windows[a], 260, 0);

        Assert.Equal("(loose)", GroupOf(a));
        Assert.Equal("(loose)", GroupOf(b));
        Assert.Equal(1, roster.ArrangementReapplied);
    }
}
