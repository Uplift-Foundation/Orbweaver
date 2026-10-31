using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S4: the orb drag gesture end to end through the real pointer handlers —
// press, move, release on real OrbWindows — against a fake roster standing in
// for SessionManager. The point is the plan's T2/T7: dragging a team MEMBER onto
// another group moves and joins the whole team, Shift moves the whole group, and
// leaving builds a new group.
//
// Only drags are driven. A press-and-release without moving resolves a click,
// and a click goes to TerminalFocuser (real tmux/ps/osascript), which a test
// must not do — see the comment on OrbWindow.OnPointerPressed.
//
// [Collection("Settings")]: constructing an OrbWindow reads settings, and the
// release writes group membership through OrbweaverSettings.
[Collection("Settings")]
public class OrbWindowDragSnapTests
{
    private sealed class Roster : IOrbDragHost
    {
        public Dictionary<string, OrbWindow> Windows { get; } = new();
        public Dictionary<string, string[]> Teams { get; } = new();
        public Dictionary<string, string> Groups { get; } = new();
        public List<string?> Highlights { get; } = new();
        public List<(string, int, int)> Shifts { get; } = new();
        public int Reapplied { get; private set; }
        public bool IsArranged { get; set; }
        public bool LaidOut { get; set; }
        public List<string> OffsetsSaved { get; } = new();
        public bool IsLaidOut(string id) => LaidOut || IsArranged;
        public void SaveMemberOffset(string id) => OffsetsSaved.Add(id);

        public IReadOnlyList<string> TeamIdsOf(string id) => Teams.GetValueOrDefault(id) ?? new[] { id };
        public IReadOnlyList<string> OrbIdsInGroup(string g) => Groups.Where(x => x.Value == g).Select(x => x.Key).ToList();
        public OrbWindow? WindowOf(string id) => Windows.GetValueOrDefault(id);
        public string GroupIdOf(string id) => Groups.GetValueOrDefault(id) ?? OrbGroups.Main;
        public string PositionKeyOf(string id) => "key:" + id;

        // Orbs are 36 px discs at their window's top-left + 28.
        public (double X, double Y, double R) DiscOf(string id)
        {
            var p = Windows[id].Position;
            return (p.X + 28, p.Y + 28, 18);
        }

        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excluded) =>
            Groups.GroupBy(g => g.Value)
                .Select(g => new SnapCandidate(
                    g.Key, Array.Empty<Point>(),
                    g.Where(o => !excluded.Contains(o.Key)).Select(o => DiscOf(o.Key)).ToList()))
                .ToList();

        public void HighlightGroup(string? id) => Highlights.Add(id);
        public void ShiftGroupAnchor(string g, int dx, int dy) => Shifts.Add((g, dx, dy));
        public void ReapplyArrangement() => Reapplied++;

        public OrbWindow Add(string id, string group, int x, int y)
        {
            var w = new OrbWindow(id) { PositionKey = "key:" + id, Position = new PixelPoint(x, y) };
            Windows[id] = w;
            Groups[id] = group;
            return w;
        }
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    private static Pointer NewPointer() => new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    private static void Press(OrbWindow w, Pointer pointer, KeyModifiers mods = KeyModifiers.None) =>
        w.RaiseEvent(new PointerPressedEventArgs(
            w, pointer, w, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            mods, 1));

    private static void Move(OrbWindow w, Pointer pointer, int dx, int dy) =>
        w.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, w, pointer, w, new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));

    private static void Release(OrbWindow w, Pointer pointer, int dx, int dy) =>
        w.RaiseEvent(new PointerReleasedEventArgs(
            w, pointer, w, new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

    private static void Drag(OrbWindow w, int dx, int dy, KeyModifiers mods = KeyModifiers.None)
    {
        var pointer = NewPointer();
        Press(w, pointer, mods);
        Move(w, pointer, dx, dy);
        Release(w, pointer, dx, dy);
    }

    private sealed class HostScope : IDisposable
    {
        public HostScope(IOrbDragHost host) => OrbDragHost.Current = host;
        public void Dispose() => OrbDragHost.Current = null;
    }

    // A team (lead + member + nested member) in main, and a lone orb in a user
    // group a long way to the right.
    private static (Roster Roster, string Lead, string Member, string Nested, string Other, string Target)
        TeamScene()
    {
        var roster = new Roster();
        var id = Uid();
        var (lead, member, nested, other) = ("lead-" + id, "mem-" + id, "nest-" + id, "other-" + id);
        var target = OrbweaverSettings.CreateOrbGroup().Id;

        roster.Add(lead, OrbGroups.Main, 100, 100);
        roster.Add(member, OrbGroups.Main, 160, 100);
        roster.Add(nested, OrbGroups.Main, 160, 160);
        roster.Add(other, target, 600, 100);
        var team = new[] { lead, member, nested };
        foreach (var t in team) roster.Teams[t] = team;
        return (roster, lead, member, nested, other, target);
    }

    [AvaloniaFact]
    public void DraggingATeamLeadMovesTheWholeTeamAndJoinsItToTheGroupItLandedBy()
    {
        var (roster, lead, member, nested, other, target) = TeamScene();
        using var _ = new HostScope(roster);

        // Drag the team's LEAD so the team lands beside the other group's orb:
        // the member's disc ends at (598,128); the other disc is at (628,128).
        Drag(roster.Windows[lead], 410, 0);

        Assert.Equal(new PixelPoint(510, 100), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(570, 100), roster.Windows[member].Position);
        Assert.Equal(new PixelPoint(570, 160), roster.Windows[nested].Position);
        Assert.Equal(new PixelPoint(600, 100), roster.Windows[other].Position);

        foreach (var id in new[] { lead, member, nested })
            Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + id));
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + other));
        Assert.Contains(target, roster.Highlights);
        Assert.Null(roster.Highlights[^1]);
    }

    [AvaloniaFact]
    public void DraggingATeamMemberMovesOnlyThatMemberAndChangesNoMembership()
    {
        // T2 revised (Warren): a member can be dragged and arranged on its own.
        var (roster, lead, member, nested, other, target) = TeamScene();
        using var _ = new HostScope(roster);

        // Right up against the other group, which would join a lead.
        Drag(roster.Windows[member], 410, 0);

        Assert.Equal(new PixelPoint(100, 100), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(570, 100), roster.Windows[member].Position);
        Assert.Equal(new PixelPoint(160, 160), roster.Windows[nested].Position);
        foreach (var id in new[] { lead, member, nested, other })
            Assert.NotEqual(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + id) ?? "(none)");
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + member));
        // Nothing lays it out in this roster, so it is pinned where it was dropped.
        Assert.True(roster.Windows[member].IsPinned);
        Assert.False(roster.Windows[lead].IsPinned);
    }

    [AvaloniaFact]
    public void ADraggedMemberOfALaidOutGroupSavesItsOffsetInsteadOfPinning()
    {
        var (roster, lead, member, _, _, _) = TeamScene();
        roster.LaidOut = true;
        using var _ = new HostScope(roster);

        Drag(roster.Windows[member], 30, 40);

        Assert.Equal(new[] { member }, roster.OffsetsSaved);
        Assert.Equal(new PixelPoint(190, 140), roster.Windows[member].Position);
        Assert.Equal(1, roster.Reapplied);
    }

    [AvaloniaFact]
    public void DraggingALeadMovesItsMembersWithItToo()
    {
        var (roster, lead, member, nested, _, _) = TeamScene();
        using var _ = new HostScope(roster);

        Drag(roster.Windows[lead], 20, 30);

        Assert.Equal(new PixelPoint(120, 130), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(180, 130), roster.Windows[member].Position);
        Assert.Equal(new PixelPoint(180, 190), roster.Windows[nested].Position);
    }

    [AvaloniaFact]
    public void ADragThatStaysUnderTheThresholdIsNotADragAtAll()
    {
        var (roster, lead, _, _, _, _) = TeamScene();
        using var _ = new HostScope(roster);
        var pointer = NewPointer();

        Press(roster.Windows[lead], pointer);
        // Under 6 DIP: no movement and nothing committed. (The release is not
        // sent: it would resolve a click, which is TerminalFocuser.)
        Move(roster.Windows[lead], pointer, 5, 5);

        Assert.Equal(new PixelPoint(100, 100), roster.Windows[lead].Position);
        Assert.Empty(roster.Highlights);
    }

    [AvaloniaFact]
    public void ShiftDraggingAnOrbMovesTheWholeGroupAndShiftsItsAnchorWhenArranged()
    {
        var (roster, lead, member, nested, other, _) = TeamScene();
        // `other` in main too, so the group is the team plus it.
        roster.Groups[other] = OrbGroups.Main;
        roster.IsArranged = true;
        using var _ = new HostScope(roster);

        Drag(roster.Windows[member], 25, 15, KeyModifiers.Shift);

        Assert.Equal(new PixelPoint(125, 115), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(185, 115), roster.Windows[member].Position);
        Assert.Equal(new PixelPoint(185, 175), roster.Windows[nested].Position);
        Assert.Equal(new PixelPoint(625, 115), roster.Windows[other].Position);
        Assert.Equal(new[] { (OrbGroups.Main, 25, 15) }, roster.Shifts);
        Assert.Equal(1, roster.Reapplied);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + member));
    }

    [AvaloniaFact]
    public void DraggingATeamFarFromItsGroupLeavesIntoANewGroup()
    {
        var (roster, lead, member, nested, other, _) = TeamScene();
        // The team shares main with another orb; drag the team well away from it.
        roster.Groups[other] = OrbGroups.Main;
        roster.Windows[other].Position = new PixelPoint(100, 300);
        roster.IsArranged = true;
        using var _ = new HostScope(roster);

        Drag(roster.Windows[nested], 0, -90);   // up, away from `other` (400 px below the team's new spot... well over 72 DIP)
        Drag(roster.Windows[lead], 700, -90);

        var made = OrbweaverSettings.OrbGroupMembershipFor("key:" + lead);
        Assert.NotNull(made);
        Assert.NotEqual(OrbGroups.Main, made);
        Assert.Equal(made, OrbweaverSettings.OrbGroupMembershipFor("key:" + member));
        Assert.Equal(made, OrbweaverSettings.OrbGroupMembershipFor("key:" + nested));
        Assert.NotNull(OrbweaverSettings.OrbGroupFor(made)!.Anchor);
        Assert.True(roster.Reapplied >= 1);
    }

    [AvaloniaFact]
    public void ADraggedOrbIsPinnedUnlessTheArrangementIsInCharge()
    {
        var (roster, lead, _, _, _, _) = TeamScene();
        using var _ = new HostScope(roster);

        Drag(roster.Windows[lead], 10, 10);
        Assert.True(roster.Windows[lead].IsPinned);

        var (arranged, lead2, _, _, _, _) = TeamScene();
        arranged.IsArranged = true;
        OrbDragHost.Current = arranged;
        Drag(arranged.Windows[lead2], 10, 10);
        Assert.False(arranged.Windows[lead2].IsPinned);
    }

    [AvaloniaFact]
    public void OnlyTheFirstOrbOfASharedPositionKeyIsRemembered()
    {
        // A team usually runs in one directory, so its orbs share one key; an
        // orb with no key has nothing to remember. Neither may throw, and the
        // drag must still move every one of them.
        var roster = new Roster();
        var id = Uid();
        var (a, b, c) = ("a-" + id, "b-" + id, "c-" + id);
        roster.Add(a, OrbGroups.Main, 0, 0).PositionKey = "shared-" + id;
        roster.Add(b, OrbGroups.Main, 60, 0).PositionKey = "shared-" + id;
        roster.Add(c, OrbGroups.Main, 120, 0).PositionKey = "";
        var team = new[] { a, b, c };
        foreach (var t in team) roster.Teams[t] = team;
        using var _ = new HostScope(roster);

        Drag(roster.Windows[a], 10, 10);

        Assert.Equal(new PixelPoint(10, 10), roster.Windows[a].Position);
        Assert.Equal(new PixelPoint(70, 10), roster.Windows[b].Position);
        Assert.Equal(new PixelPoint(130, 10), roster.Windows[c].Position);
    }

    [AvaloniaFact]
    public void AReleaseRemembersTheDraggedOrbsSpotThroughTheLiveManager()
    {
        // SessionManager.Instance has a private setter because only Start()
        // assigns it, and Start() builds a tray icon and a gateway connection;
        // set by reflection for this one test and put back, the pattern
        // GroupLiveManagerTests uses for the same `Instance?.` arm.
        var property = typeof(SessionManager).GetProperty("Instance")!;
        var dir = Directory.CreateTempSubdirectory("drag-snap-").FullName;
        var roster = new Roster();
        var id = Uid();
        var window = roster.Add("remember-" + id, OrbGroups.Main, 200, 300);
        window.PositionKey = "remember-key-" + id;
        using var _ = new HostScope(roster);
        var before = property.GetValue(null);
        property.SetValue(null, new SessionManager(dir));
        try
        {
            Drag(window, 40, 20);

            var saved = OrbweaverSettings.OrbPositionFor("remember-key-" + id);
            Assert.NotNull(saved);
            Assert.Equal(240, saved!.X);
            Assert.Equal(320, saved.Y);
        }
        finally
        {
            property.SetValue(null, before);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void WithoutAHostADragStillMovesTheOrbItself()
    {
        var w = new OrbWindow("solo-" + Uid()) { Position = new PixelPoint(50, 50) };
        OrbDragHost.Current = null;

        Drag(w, 30, 40);

        Assert.Equal(new PixelPoint(80, 90), w.Position);
        Assert.True(w.IsPinned);
    }

    [AvaloniaFact]
    public void UnitsPerDipIsMeasuredFromPointToScreenNotFromRenderScaling()
    {
        var w = new OrbWindow("units-" + Uid());

        // Headless PointToScreen is 1:1, so one unit per DIP.
        Assert.Equal(1.0, OrbWindow.UnitsPerDip(w), 6);
    }

    // --- the backdrop's entry point ---

    [AvaloniaFact]
    public void ADragStartedOnABackdropMovesTheWholeGroup()
    {
        var (roster, lead, member, nested, other, target) = TeamScene();
        roster.IsArranged = true;
        using var _ = new HostScope(roster);
        var backdrop = new Avalonia.Controls.Window { Position = new PixelPoint(0, 0), Width = 300, Height = 300 };
        var pointer = NewPointer();

        OrbWindow.BeginBackdropDrag(
            OrbGroups.Main,
            new PointerPressedEventArgs(
                backdrop, pointer, backdrop, new Point(10, 10), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1),
            backdrop);
        backdrop.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, backdrop, pointer, backdrop, new Point(12, 12), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        Assert.Equal(new PixelPoint(100, 100), roster.Windows[lead].Position);   // under the threshold
        backdrop.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, backdrop, pointer, backdrop, new Point(40, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
        backdrop.RaiseEvent(new PointerReleasedEventArgs(
            backdrop, pointer, backdrop, new Point(40, 10), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

        Assert.Equal(new PixelPoint(130, 100), roster.Windows[lead].Position);
        Assert.Equal(new PixelPoint(190, 100), roster.Windows[member].Position);
        Assert.Equal(new PixelPoint(190, 160), roster.Windows[nested].Position);
        // Not in the group: stays put.
        Assert.Equal(new PixelPoint(600, 100), roster.Windows[other].Position);
        Assert.Equal(new[] { (OrbGroups.Main, 30, 0) }, roster.Shifts);
        Assert.Equal(1, roster.Reapplied);
    }

    [AvaloniaFact]
    public void ABackdropPressOnAnEmptyGroupOrWithoutAHostDoesNothing()
    {
        var (roster, _, _, _, _, _) = TeamScene();
        var backdrop = new Avalonia.Controls.Window();
        PointerPressedEventArgs Press() => new(
            backdrop, NewPointer(), backdrop, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1);

        using (new HostScope(roster))
            OrbWindow.BeginBackdropDrag("g-nobody", Press(), backdrop);

        OrbDragHost.Current = null;
        OrbWindow.BeginBackdropDrag(OrbGroups.Main, Press(), backdrop);
        Assert.Empty(roster.Shifts);

        // A press that is not the left button is ignored.
        using (new HostScope(roster))
            OrbWindow.BeginBackdropDrag(
                OrbGroups.Main,
                new PointerPressedEventArgs(
                    backdrop, NewPointer(), backdrop, new Point(1, 1), 0,
                    new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                    KeyModifiers.None, 1),
                backdrop);
        Assert.Empty(roster.Shifts);
    }
}
