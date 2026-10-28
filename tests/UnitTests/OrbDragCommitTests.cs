using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S4: what releasing a drag does — preview highlight, membership for the
// lead and every member, a new one-orb group on Leave, anchors for a whole-group
// move — against a fake roster. [Collection("Settings")] because the writes go
// through OrbweaverSettings' process-wide model.
[Collection("Settings")]
public class OrbDragCommitTests
{
    private sealed class FakeHost : IOrbDragHost
    {
        public bool IsArranged { get; set; } = true;
        public bool IsFree => !IsArranged;
        public List<string> OffsetsSaved { get; } = new();
        public List<(IReadOnlyList<string>? Team, string? Target)> PairPreviews { get; } = new();
        public bool LaidOutOverride { get; set; }
        public bool IsRoot(string id) => TeamIdsOf(id)[0] == id;
        public bool IsLaidOut(string id) => LaidOutOverride || IsArranged;
        public void SaveMemberOffset(string id) => OffsetsSaved.Add(id);
        public void PreviewPair(IReadOnlyList<string>? team, string? target) => PairPreviews.Add((team, target));
        public Dictionary<string, string[]> Teams { get; } = new();
        public Dictionary<string, string> Groups { get; } = new();
        public Dictionary<string, (double X, double Y, double R)> Discs { get; } = new();
        public List<SnapCandidate> Candidates { get; } = new();
        public List<string?> Highlights { get; } = new();
        public List<(string Group, int Dx, int Dy)> Shifts { get; } = new();
        public int Reapplied { get; private set; }
        public IReadOnlyCollection<string>? ExcludedAsked { get; private set; }

        public IReadOnlyList<string> TeamIdsOf(string id) =>
            Teams.Values.FirstOrDefault(t => t.Contains(id)) ?? new[] { id };
        public IReadOnlyList<string> OrbIdsInGroup(string groupId) =>
            Groups.Where(g => g.Value == groupId).Select(g => g.Key).ToList();
        public OrbWindow? WindowOf(string id) => null;
        public string GroupIdOf(string id) => Groups.GetValueOrDefault(id) ?? OrbGroups.Main;
        public HashSet<string> KeylessIds { get; } = new();
        public string PositionKeyOf(string id) => KeylessIds.Contains(id) ? "" : "key:" + id;
        public (double X, double Y, double R) DiscOf(string id) => Discs[id];

        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excludedIds)
        {
            ExcludedAsked = excludedIds;
            return Candidates;
        }

        public void HighlightGroup(string? groupId) => Highlights.Add(groupId);
        public void ShiftGroupAnchor(string groupId, int dx, int dy) => Shifts.Add((groupId, dx, dy));
        public void ReapplyArrangement() => Reapplied++;
    }

    private static string UniqueId() => Guid.NewGuid().ToString("N")[..8];

    // A solo orb at the origin in main, one other main orb far away so there is
    // something to leave, and a user group right next to it to join.
    private static (FakeHost Host, string Orb, string Target) Scene(double targetX, double mainX = 120)
    {
        var orb = "orb-" + UniqueId();
        var target = OrbweaverSettings.CreateOrbGroup().Id;
        var host = new FakeHost();
        host.Discs[orb] = (0, 0, 18);
        host.Candidates.Add(new SnapCandidate(OrbGroups.Main, Array.Empty<Point>(), new[] { (mainX, 0.0, 18.0) }));
        host.Candidates.Add(new SnapCandidate(target, Array.Empty<Point>(), new[] { (targetX, 0.0, 18.0) }));
        return (host, orb, target);
    }

    [Fact]
    public void PreviewHighlightsTheGroupItWouldJoin()
    {
        var (host, orb, target) = Scene(50);

        var outcome = OrbDragCommit.Preview(host, orb, 1);

        Assert.Equal(new SnapOutcome.Join(target), outcome);
        Assert.Equal(new[] { target }, host.Highlights);
    }

    [Fact]
    public void PreviewClearsTheHighlightWhenNothingWouldJoin()
    {
        var (host, orb, _) = Scene(5000);

        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Preview(host, orb, 1));
        Assert.Equal(new string?[] { null }, host.Highlights);
    }

    [Fact]
    public void ReleaseJoiningWritesMembershipForTheLeadAndEveryMemberThenReapplies()
    {
        var (host, orb, target) = Scene(50);
        var lead = "lead-" + UniqueId();
        var member = "member-" + UniqueId();
        host.Teams[lead] = new[] { lead, orb, member };
        host.Discs[lead] = (0, 0, 18);
        host.Discs[member] = (0, 0, 10);

        // Pressing the team's root (lead) moves and snaps the whole team.
        var outcome = OrbDragCommit.Release(host, lead, false, 30, 40, 1);

        Assert.Equal(new SnapOutcome.Join(target), outcome);
        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + lead));
        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + member));
        Assert.Equal(1, host.Reapplied);
        Assert.Equal(new string?[] { null }, host.Highlights);
        Assert.Empty(host.Shifts);
        // The team is excluded from the candidates it is measured against.
        Assert.Equal(new[] { lead, orb, member }.OrderBy(x => x), host.ExcludedAsked!.OrderBy(x => x));
    }

    [Fact]
    public void ReleaseLeavingMakesANewOneOrbGroupAnchoredAtTheDropPoint()
    {
        var (host, orb, _) = Scene(5000);
        host.Discs[orb] = (310.4, 220.6, 18);
        var before = OrbweaverSettings.OrbGroupList().Select(g => g.Id).ToHashSet();

        var outcome = OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.Leave>(outcome);
        var madeId = OrbweaverSettings.OrbGroupMembershipFor("key:" + orb);
        Assert.NotNull(madeId);
        Assert.DoesNotContain(madeId, before);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(310, 221), OrbweaverSettings.OrbGroupFor(madeId)!.Anchor);
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void ReleaseLeavingAnchorsTheNewGroupAtTheMiddleOfTheWholeTeam()
    {
        // The pressed orb is a member fanned out to the right of its lead: the
        // group's shape is centred between them, not on the member.
        var (host, orb, _) = Scene(5000);
        var lead = "lead-" + UniqueId();
        host.Teams[lead] = new[] { lead, orb };
        host.Discs[lead] = (100, 200, 20);
        host.Discs[orb] = (180, 200, 10);

        var outcome = OrbDragCommit.Release(host, lead, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.Leave>(outcome);
        var made = OrbweaverSettings.OrbGroupMembershipFor("key:" + lead)!;
        // Bounds x 80..190 -> 135; y 180..220 -> 200.
        Assert.Equal(new OrbweaverSettings.OrbPlacement(135, 200), OrbweaverSettings.OrbGroupFor(made)!.Anchor);
    }

    [Fact]
    public void ALeaveOrJoinWithNothingToKeyTheMembershipByDoesNothingAtAll()
    {
        var before = OrbweaverSettings.OrbGroupList().Count;
        var (host, orb, target) = Scene(5000);   // Scene itself made one group
        before++;
        host.KeylessIds.Add(orb);

        var outcome = OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Equal(before, OrbweaverSettings.OrbGroupList().Count);

        // A join to a real group is refused the same way.
        host.Candidates[1] = new SnapCandidate(target, Array.Empty<Point>(), new[] { (50.0, 0.0, 18.0) });
        Assert.IsType<SnapOutcome.NoChange>(OrbDragCommit.Release(host, orb, false, 5, 5, 1));
        Assert.Equal(before, OrbweaverSettings.OrbGroupList().Count);
    }

    [Fact]
    public void ReleaseWithNoChangeWritesNothingButStillReapplies()
    {
        var (host, orb, _) = Scene(5000, mainX: 70);

        var outcome = OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void ReleaseReappliesInFreeModeToo()
    {
        var (host, orb, target) = Scene(50);
        host.IsArranged = false;

        OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void TheDecisionDoesNotDependOnHowManyScreenUnitsMakeADip()
    {
        // The thresholds scale with the orb's own drawn size, so the same scene
        // measured in screen units that are 1x or 2x of a DIP gives one answer:
        // 19 DIP gap against a 24 DIP limit (joins), 38 against 24 (does not).
        foreach (var (units, gapDip, expectJoin) in new[] { (1.0, 19.0, true), (2.0, 19.0, true), (1.0, 38.0, false), (2.0, 38.0, false) })
        {
            var (host, orb, target) = Scene(5000);
            host.Discs[orb] = (0, 0, 18 * units);
            host.Candidates[0] = new SnapCandidate(
                OrbGroups.Main, Array.Empty<Point>(), new[] { (-200 * units, 0.0, 18 * units) });
            host.Candidates[1] = new SnapCandidate(
                target, Array.Empty<Point>(),
                new[] { ((gapDip + 36) * units, 0.0, 18 * units) });

            var outcome = OrbDragCommit.Preview(host, orb, units);

            if (expectJoin) Assert.Equal(new SnapOutcome.Join(target), outcome);
            else Assert.IsType<SnapOutcome.Leave>(outcome);
        }

        // A nonsense factor reads as 1.
        var (h, o, t) = Scene(5000);
        h.Discs[o] = (0, 0, 18);
        h.Candidates[1] = new SnapCandidate(t, Array.Empty<Point>(), new[] { (55.0, 0.0, 18.0) });
        Assert.Equal(new SnapOutcome.Join(t), OrbDragCommit.Preview(h, o, 0));
    }

    [Fact]
    public void TheThresholdsScaleWithTheDraggedOrbsSize()
    {
        // A 14 DIP gap to the neighbouring group: inside the 24 DIP join
        // distance of a full-size orb (radius 18), outside the 12 DIP one of a
        // half-size orb (radius 9) — where S2 leaves a smaller gap too.
        var (host, orb, target) = Scene(5000);
        host.Discs[orb] = (0, 0, 18);
        host.Candidates[1] = new SnapCandidate(target, Array.Empty<Point>(), new[] { (50.0, 0.0, 18.0) });
        Assert.Equal(new SnapOutcome.Join(target), OrbDragCommit.Preview(host, orb, 1));

        host.Discs[orb] = (0, 0, 9);
        host.Candidates[1] = new SnapCandidate(target, Array.Empty<Point>(), new[] { (32.0, 0.0, 9.0) });
        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Preview(host, orb, 1));

        // Leave scales the same way: 50 DIP from its own group stays at full
        // size (limit 72) and leaves at half size (limit 36).
        host.Candidates[0] = new SnapCandidate(OrbGroups.Main, Array.Empty<Point>(), new[] { (68.0, 0.0, 9.0) });
        host.Candidates[1] = new SnapCandidate(target, Array.Empty<Point>(), new[] { (5000.0, 0.0, 9.0) });
        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Preview(host, orb, 1));
        host.Discs[orb] = (0, 0, 18);
        host.Candidates[0] = new SnapCandidate(OrbGroups.Main, Array.Empty<Point>(), new[] { (86.0, 0.0, 18.0) });
        Assert.IsType<SnapOutcome.NoChange>(OrbDragCommit.Preview(host, orb, 1));
    }

    [Fact]
    public void AnOrbWithNoRadiusIsMeasuredAtFullSize()
    {
        var (host, orb, target) = Scene(5000);
        host.Discs[orb] = (0, 0, 0);   // what an unknown orb reports
        host.Candidates[1] = new SnapCandidate(target, Array.Empty<Point>(), new[] { (20.0, 0.0, 0.0) });

        Assert.Equal(new SnapOutcome.Join(target), OrbDragCommit.Preview(host, orb, 1));
    }

    [Fact]
    public void AnOutlineIsScaledToDipToo()
    {
        var (host, orb, target) = Scene(5000);
        host.Discs[orb] = (-100, 200, 36);
        // 2x: outline 0..400 physical = 0..200 DIP; orb centre (-50,100) DIP, r 18: gap 32 DIP.
        host.Candidates[1] = new SnapCandidate(
            target,
            new[] { new Point(0, 0), new Point(400, 0), new Point(400, 400), new Point(0, 400) },
            Array.Empty<(double, double, double)>());
        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Preview(host, orb, 2));

        host.Discs[orb] = (-80, 200, 36);   // centre -40 DIP: gap 22
        Assert.Equal(new SnapOutcome.Join(target), OrbDragCommit.Preview(host, orb, 2));
    }

    [Fact]
    public void AWholeGroupDragShiftsTheGroupsAnchorAndChangesNoMembership()
    {
        var (host, orb, _) = Scene(50);
        host.Groups[orb] = "g-src";

        var outcome = OrbDragCommit.Release(host, orb, true, 12, -7, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Equal(new[] { ("g-src", 12, -7) }, host.Shifts);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(1, host.Reapplied);
    }

    // --- free mode (F3) ---

    // A free-mode scene: orb `a` at the origin, loose and in main. Candidates:
    // another loose team at `otherX` (id "loose-..."), and a user group at
    // `groupX`. Main is not a candidate.
    private static (FakeHost Host, string A, string Loose, string Group) FreeScene(double otherX, double groupX)
    {
        var a = "a-" + UniqueId();
        var loose = "loose-" + UniqueId();
        var group = OrbweaverSettings.CreateOrbGroup().Id;
        var host = new FakeHost { IsArranged = false };
        host.Discs[a] = (0, 0, 18);
        host.Discs[loose] = (otherX, 0, 18);
        host.Candidates.Add(new SnapCandidate(loose, Array.Empty<Point>(), new[] { (otherX, 0.0, 18.0) }, Loose: true));
        host.Candidates.Add(new SnapCandidate(group, Array.Empty<Point>(), new[] { (groupX, 0.0, 18.0) }));
        return (host, a, loose, group);
    }

    [Fact]
    public void DroppingALooseOrbBesideAnotherLooseOrbPairsThemIntoANewGroupAnchoredBetweenThem()
    {
        var (host, a, loose, group) = FreeScene(50, 5000);

        var outcome = OrbDragCommit.Release(host, a, false, 5, 5, 1);

        Assert.Equal(new SnapOutcome.Pair(loose), outcome);
        var made = OrbweaverSettings.OrbGroupMembershipFor("key:" + a)!;
        Assert.NotEqual(group, made);
        Assert.Equal(made, OrbweaverSettings.OrbGroupMembershipFor("key:" + loose));
        // Midpoint of the two centroids: (0 + 50) / 2 = 25.
        Assert.Equal(new OrbweaverSettings.OrbPlacement(25, 0), OrbweaverSettings.OrbGroupFor(made)!.Anchor);
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void PairingTakesBothWholeTeams()
    {
        var (host, a, loose, _) = FreeScene(50, 5000);
        var lead = "lead-" + UniqueId();
        var other = "other-" + UniqueId();
        host.Teams[lead] = new[] { lead, a };
        host.Discs[lead] = (-30, 0, 18);
        host.Teams[loose] = new[] { loose, other };
        host.Discs[other] = (120, 0, 10);

        OrbDragCommit.Release(host, lead, false, 5, 5, 1);

        var made = OrbweaverSettings.OrbGroupMembershipFor("key:" + lead)!;
        foreach (var id in new[] { lead, a, loose, other })
            Assert.Equal(made, OrbweaverSettings.OrbGroupMembershipFor("key:" + id));
        // Centroids: (-30 + 0) / 2 = -15 and (50 + 120) / 2 = 85; midpoint 35.
        Assert.Equal(new OrbweaverSettings.OrbPlacement(35, 0), OrbweaverSettings.OrbGroupFor(made)!.Anchor);
    }

    [Fact]
    public void ADropOntoAUserGroupInFreeModeStillJoinsIt()
    {
        var (host, a, _, group) = FreeScene(5000, 50);

        Assert.Equal(new SnapOutcome.Join(group), OrbDragCommit.Release(host, a, false, 5, 5, 1));
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor("key:" + a));
    }

    [Fact]
    public void ApairWithNoKeyOnEitherSideDoesNothing()
    {
        var (host, a, loose, _) = FreeScene(50, 5000);
        var groups = OrbweaverSettings.OrbGroupList().Count;

        host.KeylessIds.Add(loose);
        Assert.IsType<SnapOutcome.NoChange>(OrbDragCommit.Release(host, a, false, 5, 5, 1));

        host.KeylessIds.Clear();
        host.KeylessIds.Add(a);
        Assert.IsType<SnapOutcome.NoChange>(OrbDragCommit.Release(host, a, false, 5, 5, 1));
        Assert.Equal(groups, OrbweaverSettings.OrbGroupList().Count);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + loose));
    }

    [Fact]
    public void DraggingAGroupMemberOutInFreeModeUnassignsTheWholeTeam()
    {
        var (host, a, _, group) = FreeScene(5000, 5000);
        var lead = "lead-" + UniqueId();
        var mate = "mate-" + UniqueId();
        host.Teams[lead] = new[] { lead, a };
        host.Discs[lead] = (0, 0, 18);
        host.Groups[a] = group;
        host.Groups[lead] = group;
        host.Groups[mate] = group;
        host.Discs[mate] = (0, 0, 18);
        // The rest of the group is far away, so the team leaves.
        host.Candidates[1] = new SnapCandidate(group, Array.Empty<Point>(), new[] { (500.0, 0.0, 18.0) });
        host.Candidates.RemoveAt(0);
        OrbweaverSettings.AssignTeamToGroup("key:" + lead, new[] { "key:" + a, "key:" + mate }, group);

        var outcome = OrbDragCommit.Release(host, lead, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.LeaveToFree>(outcome);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + lead));
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + a));
        // Someone who stayed behind keeps the group.
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor("key:" + mate));
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void ALooseOrbFarFromEverythingInFreeModeJustStaysLoose()
    {
        var (host, a, _, group) = FreeScene(5000, 5000);

        var outcome = OrbDragCommit.Release(host, a, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + a));
        Assert.DoesNotContain(group, new[] { OrbweaverSettings.OrbGroupMembershipFor("key:" + a) });
    }

    [Fact]
    public void TheSameSceneInArrangedModeMakesASingletonGroupInsteadOfLeavingToFree()
    {
        var (host, a, _, group) = FreeScene(5000, 5000);
        host.IsArranged = true;
        host.Groups[a] = group;
        host.Candidates[1] = new SnapCandidate(group, Array.Empty<Point>(), new[] { (500.0, 0.0, 18.0) });
        host.Candidates.RemoveAt(0);

        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Release(host, a, false, 5, 5, 1));
        Assert.NotEqual(group, OrbweaverSettings.OrbGroupMembershipFor("key:" + a));
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void AWholeGroupDragShiftsTheAnchorAndReappliesInBothModes()
    {
        foreach (var arranged in new[] { false, true })
        {
            var (host, a, _, group) = FreeScene(5000, 5000);
            host.IsArranged = arranged;
            host.Groups[a] = group;

            OrbDragCommit.Release(host, a, true, 12, -7, 1);

            Assert.Equal(new[] { (group, 12, -7) }, host.Shifts);
            Assert.Equal(1, host.Reapplied);
        }
    }

    // --- member drags (T2 revised) ---

    private static (FakeHost Host, string Lead, string Member) TeamScene(bool laidOut)
    {
        var lead = "lead-" + UniqueId();
        var member = "mem-" + UniqueId();
        var host = new FakeHost { IsArranged = false, LaidOutOverride = laidOut };
        host.Teams[lead] = new[] { lead, member };
        host.Discs[lead] = (0, 0, 18);
        host.Discs[member] = (60, 0, 10);
        host.Candidates.Add(new SnapCandidate("g-near", Array.Empty<Point>(), new[] { (80.0, 0.0, 18.0) }));
        return (host, lead, member);
    }

    [Fact]
    public void ADraggedMemberInALaidOutGroupSavesItsOffsetAndReappliesWithoutAnySnap()
    {
        var (host, lead, member) = TeamScene(laidOut: true);

        // Right beside a group it would join if it were a lead: it is not.
        var outcome = OrbDragCommit.Release(host, member, false, 9, 9, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Equal(new[] { member }, host.OffsetsSaved);
        Assert.Equal(1, host.Reapplied);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + member));
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + lead));
        Assert.Null(host.ExcludedAsked);   // Decide never ran
    }

    [Fact]
    public void ADraggedMemberOfALooseTeamInFreeModeSavesNothingAndDoesNotReapply()
    {
        var (host, _, member) = TeamScene(laidOut: false);

        var outcome = OrbDragCommit.Release(host, member, false, 9, 9, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Empty(host.OffsetsSaved);
        Assert.Equal(0, host.Reapplied);
    }

    [Fact]
    public void ADraggedMemberWhileArrangedSavesItsOffsetToo()
    {
        var (host, _, member) = TeamScene(laidOut: false);
        host.IsArranged = true;

        OrbDragCommit.Release(host, member, false, 9, 9, 1);

        Assert.Equal(new[] { member }, host.OffsetsSaved);
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void PreviewInFreeModeHighlightsAUserGroupButNotALooseTargetThatHasNoGroupYet()
    {
        var (host, a, _, group) = FreeScene(50, 5000);
        Assert.IsType<SnapOutcome.Pair>(OrbDragCommit.Preview(host, a, 1));
        Assert.Equal(new string?[] { null }, host.Highlights);
        Assert.Equal(1, host.PairPreviews.Count);
        Assert.Equal(new[] { a }, host.PairPreviews[0].Team);
        Assert.Equal(host.Candidates[0].GroupId, host.PairPreviews[0].Target);

        host.Candidates[1] = new SnapCandidate(group, Array.Empty<Point>(), new[] { (40.0, 0.0, 18.0) });
        host.Candidates[0] = host.Candidates[0] with { Discs = new[] { (5000.0, 0.0, 18.0) } };
        Assert.Equal(new SnapOutcome.Join(group), OrbDragCommit.Preview(host, a, 1));
        Assert.Equal(group, host.Highlights[^1]);
        // A join previews no pair.
        Assert.Null(host.PairPreviews[^1].Team);
        Assert.Null(host.PairPreviews[^1].Target);
    }
}
