using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-281 E2: auto groups through a real SessionManager and real scans of
// hand-written status files. Superseded keeps one orb per (pid, CLI) and every
// file here shares this process's pid, so a scan holds at most one session per
// CLI: three orbs, s0 Claude Code, s1 Codex, s2 Grok, which is exactly what
// grouping by CLI needs.
//
// The hard requirements each have a test: an explicit snap beats auto,
// predefined beats auto (GroupIdFor's precedence, pinned in OrbGroupsTests; the
// explicit-main arm is here end to end), a manual group never gains members,
// and every toggle, Off included, regroups live and leaves manual groups
// byte-for-byte alone.
[Collection("Settings")]
public class SessionManagerAutoGroupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb281-auto-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly AutoGroupBy _by = OrbweaverSettings.AutoGroupBy;
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;
    private readonly bool _showOrbs = OrbweaverSettings.ShowOrbs;

    public SessionManagerAutoGroupTests()
    {
        Directory.CreateDirectory(_dir);
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Off;
        OrbweaverSettings.ArrangeAnchor = null;
        ForgetAutoGroups();
        Reset();
    }

    public void Dispose()
    {
        Reset();
        OrbweaverSettings.AutoGroupBy = _by;
        OrbweaverSettings.ShowOrbs = _showOrbs;
        foreach (var key in _keys)
        {
            OrbweaverSettings.UnassignOrbGroup(key);
            OrbweaverSettings.ClearOrbPosition(key);
        }
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        ForgetAutoGroups();
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());
        OrbweaverSettings.ClaudeCloudEnabled = false;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Auto ids are a function of the value alone, so a group a previous test
    // made would otherwise still be in the table, anchor and all.
    private static void ForgetAutoGroups()
    {
        foreach (var g in OrbweaverSettings.OrbGroupList().Where(g => OrbAutoGroups.IsAuto(g.Id)).ToList())
            OrbweaverSettings.DeleteOrbGroup(g.Id);
    }

    private static void Reset()
    {
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.Highlight(null);
    }

    private static readonly string[] Clis = { "", "codex", "grok" };

    private static string Cli(int i) => i switch { 1 => "codex", 2 => "grok", _ => "claude" };

    private static string CliGroup(int i) => OrbAutoGroups.IdFor(AutoGroupBy.Cli, Cli(i));

    // `transcript` null writes `"transcript_path":null`, which the Codex hook
    // can send (OrbweaverHook.sh).
    private void Write(int i, string cwd = "/Users/user/cb281auto", string? transcript = "")
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SessionStatus
        {
            Cli = Clis[i],
            Cwd = cwd,
            Title = "t" + i,
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
            Tty = "/dev/ttys004",
            TranscriptPath = transcript ?? "",
        });
        if (transcript is null)
        {
            Assert.Contains("\"transcript_path\":\"\"", json);
            json = json.Replace("\"transcript_path\":\"\"", "\"transcript_path\":null");
        }
        File.WriteAllText(Path.Combine(_dir, "s" + i + ".txt"), json);
    }

    private SessionManager Scan(int count = 3)
    {
        for (var i = 0; i < count; i++) Write(i);
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var manager = new SessionManager(_dir) { AutoGroupRepoRootOf = _ => null };
        manager.ScanAndUpdate();
        Assert.True(manager.AllStatuses.Count == count && Windows(manager).Count == count,
            $"scan made {manager.AllStatuses.Count} statuses and {Windows(manager).Count} windows of {count}");
        foreach (var id in manager.AllStatuses.Keys) KeyOf(manager, id);
        return manager;
    }

    private string KeyOf(SessionManager m, string id)
    {
        var key = OrbGroupMenu.KeyOf(id, m.AllStatuses);
        if (!_keys.Contains(key)) _keys.Add(key);
        return key;
    }

    private OrbGroup Group(string shape = "circle")
    {
        var g = OrbweaverSettings.CreateOrbGroup(shape);
        _groups.Add(g.Id);
        return g;
    }

    private static string GroupOf(SessionManager m, string id) => ((IOrbDragHost)m).GroupIdOf(id);

    private static void Set(SessionManager m, AutoGroupBy by)
    {
        OrbweaverSettings.AutoGroupBy = by;
        m.ReapplyAutoGroups();
        Glide(m);
    }

    private static Dictionary<string, OrbWindow> Windows(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static IReadOnlyList<string> Placed(SessionManager m)
    {
        var placements = (System.Collections.IEnumerable)typeof(SessionManager)
            .GetField("_groupPlacements", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        var ids = new List<string>();
        foreach (var g in placements) ids.Add((string)g.GetType().GetProperty("GroupId")!.GetValue(g)!);
        return ids;
    }

    // Lands any glide in flight, the way the free-mode QA tests do.
    private static void Glide(SessionManager m)
    {
        if (typeof(SessionManager).GetField("_arrangeAnimTargets", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(m) is null) return;
        typeof(SessionManager).GetField("_arrangeAnimStart", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(m, Environment.TickCount64 - 5000);
        typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(m, new object?[] { null, EventArgs.Empty });
    }

    private static string Snapshot(OrbGroup? g) =>
        g is null ? "<none>" : System.Text.Json.JsonSerializer.Serialize(g);

    // --- the toggle, live ---------------------------------------------------

    [AvaloniaFact]
    public void OffMakesNoAutoGroupAndLeavesEveryOrbInMain()
    {
        var m = Scan();
        m.ReapplyAutoGroups();

        foreach (var i in new[] { 0, 1, 2 }) Assert.Equal(OrbGroups.Main, GroupOf(m, "s" + i));
        Assert.DoesNotContain(OrbweaverSettings.OrbGroupList(), g => OrbAutoGroups.IsAuto(g.Id));
        Assert.Empty(Placed(m));
    }

    [AvaloniaFact]
    public void TurningOnCliMakesOneGroupPerCliLiveInFreeModeWithNoRestart()
    {
        var m = Scan();
        Assert.False(m.IsArranged);

        Set(m, AutoGroupBy.Cli);

        foreach (var i in new[] { 0, 1, 2 })
        {
            Assert.Equal(CliGroup(i), GroupOf(m, "s" + i));
            Assert.NotNull(OrbweaverSettings.OrbGroupFor(CliGroup(i)));
        }
        Assert.Equal(new[] { "Claude", "Codex", "Grok" },
            new[] { 0, 1, 2 }.Select(i => OrbweaverSettings.OrbGroupFor(CliGroup(i))!.Name));
        // Free mode lays them out, singletons included: one placement each.
        Assert.Equal(new[] { 0, 1, 2 }.Select(CliGroup).OrderBy(x => x), Placed(m).OrderBy(x => x));
        // Nothing was written: classification is computed, never stored.
        foreach (var i in new[] { 0, 1, 2 })
            Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s" + i)));
    }

    [AvaloniaFact]
    public void SwitchingCliToDirectoryRegroupsEveryoneIntoTheOneDirectory()
    {
        var m = Scan();
        Set(m, AutoGroupBy.Cli);

        Set(m, AutoGroupBy.Directory);

        var dir = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/user/cb281auto");
        foreach (var i in new[] { 0, 1, 2 }) Assert.Equal(dir, GroupOf(m, "s" + i));
        Assert.Equal("cb281auto", OrbweaverSettings.OrbGroupFor(dir)!.Name);
        Assert.Equal(new[] { dir }, Placed(m));
        // The CLI groups stay in the table, dormant, so they come back where they were.
        Assert.NotNull(OrbweaverSettings.OrbGroupFor(CliGroup(1)));
    }

    [AvaloniaFact]
    public void TheScanAloneRegroupsWhenTheSettingChangesWithNobodyCallingReapply()
    {
        var m = Scan();
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Cli;

        // Nothing arrived or left: only the group tracker can notice.
        m.ScanAndUpdate();
        Glide(m);

        Assert.Equal(CliGroup(1), GroupOf(m, "s1"));
        Assert.Contains(CliGroup(1), Placed(m));
    }

    [AvaloniaFact]
    public void ReapplyWithNothingChangedMovesNothing()
    {
        var m = Scan();
        Set(m, AutoGroupBy.Cli);
        var before = Windows(m).ToDictionary(p => p.Key, p => p.Value.Position);

        m.ReapplyAutoGroups();

        Assert.Equal(before, Windows(m).ToDictionary(p => p.Key, p => p.Value.Position));
    }

    [AvaloniaFact]
    public void TurningOffGivesAPinnedOrbItsSavedSpotBackAndStacksTheRest()
    {
        var m = Scan();
        var w = Windows(m);
        var pin = new PixelPoint(w["s0"].Position.X - 300, w["s0"].Position.Y + 40);
        w["s0"].PinAt(pin);
        m.RememberOrbPosition(w["s0"]);
        m.ReapplyArrangement();
        var stacked = w["s1"].Position;

        Set(m, AutoGroupBy.Cli);
        Assert.NotEqual(pin, w["s0"].Position);              // pulled into its group

        Set(m, AutoGroupBy.Off);

        Assert.Equal(OrbGroups.Main, GroupOf(m, "s0"));
        Assert.True(w["s0"].IsPinned);
        Assert.Equal(pin, w["s0"].Position);                 // back where it was left by hand
        Assert.False(w["s1"].IsPinned);
        Assert.Equal(stacked, w["s1"].Position);             // back in the stack
        Assert.Empty(Placed(m));
    }

    [AvaloniaFact]
    public void AnAutoGroupsAnchorSurvivesOffAndOnAgain()
    {
        var m = Scan();
        Set(m, AutoGroupBy.Cli);
        var anchor = OrbweaverSettings.OrbGroupFor(CliGroup(1))!.Anchor;
        Assert.NotNull(anchor);

        Set(m, AutoGroupBy.Off);
        Set(m, AutoGroupBy.Cli);

        Assert.Equal(anchor, OrbweaverSettings.OrbGroupFor(CliGroup(1))!.Anchor);
    }

    [AvaloniaFact]
    public void ArrangedModeRegroupsLiveAndOffHandsOrbsBackToTheArrangement()
    {
        var m = Scan();
        m.ArrangeOrbsInPattern();
        Glide(m);
        Assert.True(m.IsArranged);

        Set(m, AutoGroupBy.Cli);
        foreach (var i in new[] { 0, 1, 2 }) Assert.Equal(CliGroup(i), GroupOf(m, "s" + i));

        Set(m, AutoGroupBy.Off);
        foreach (var i in new[] { 0, 1, 2 }) Assert.Equal(OrbGroups.Main, GroupOf(m, "s" + i));
        // Arranged, the arrangement owns positions: no orb was pinned by the release.
        Assert.All(Windows(m).Values, w => Assert.False(w.IsPinned));
    }

    // --- the precedence, end to end -----------------------------------------

    [AvaloniaFact]
    public void AManualGroupNeverGainsMembersAndIsUntouchedByEveryToggle()
    {
        var m = Scan();
        var manual = Group("star");
        OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s2"), manual.Id);
        m.ReapplyArrangement();
        var before = Snapshot(OrbweaverSettings.OrbGroupFor(manual.Id));

        foreach (var by in new[] { AutoGroupBy.Cli, AutoGroupBy.Directory, AutoGroupBy.Profile, AutoGroupBy.Account, AutoGroupBy.Off })
        {
            Set(m, by);

            Assert.Equal(manual.Id, GroupOf(m, "s2"));
            Assert.Equal(manual.Id, OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s2")));
            Assert.NotEqual(manual.Id, GroupOf(m, "s0"));
            Assert.NotEqual(manual.Id, GroupOf(m, "s1"));
            Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s0")));
            Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s1")));
        }

        // Its shape, anchor, name and style: the same entry, unchanged.
        Assert.Equal(before, Snapshot(OrbweaverSettings.OrbGroupFor(manual.Id)));
    }

    [AvaloniaFact]
    public void AManualGroupIsUntouchedByEveryToggleWhileArrangedToo()
    {
        var m = Scan();
        var manual = Group("star");
        OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s2"), manual.Id);
        m.ArrangeOrbsInPattern();
        Glide(m);
        Assert.True(m.IsArranged);
        var before = Snapshot(OrbweaverSettings.OrbGroupFor(manual.Id));

        foreach (var by in new[] { AutoGroupBy.Cli, AutoGroupBy.Directory, AutoGroupBy.Off })
        {
            Set(m, by);
            Assert.Equal(manual.Id, GroupOf(m, "s2"));
            Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s0")));
        }

        Assert.Equal(before, Snapshot(OrbweaverSettings.OrbGroupFor(manual.Id)));
    }

    [AvaloniaFact]
    public void ANewMatchingSessionLandsInTheAutoGroupNotTheManualOne()
    {
        var m = Scan(2);
        var manual = Group();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s0"), manual.Id);   // the Claude Code orb, by hand
        Set(m, AutoGroupBy.Directory);

        Write(2);                                                     // a Grok session in the same directory
        m.ScanAndUpdate();
        Glide(m);
        KeyOf(m, "s2");

        var dir = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/user/cb281auto");
        Assert.Equal(dir, GroupOf(m, "s2"));
        Assert.Equal(manual.Id, GroupOf(m, "s0"));
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s2")));
    }

    [AvaloniaFact]
    public void AnOrbExplicitlyInMainStaysOutOfItsAutoGroup()
    {
        var m = Scan();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s1"), OrbGroups.Main);

        Set(m, AutoGroupBy.Cli);

        Assert.Equal(OrbGroups.Main, GroupOf(m, "s1"));
        Assert.Equal(CliGroup(0), GroupOf(m, "s0"));
        // s1 is the only Codex orb and is held out, so no Codex group is made
        // for nobody: nothing would be drawn in it.
        Assert.Null(OrbweaverSettings.OrbGroupFor(CliGroup(1)));
        Assert.Null(((IOrbDragHost)m).AutoGroupIdOf("s1"));
    }

    [AvaloniaFact]
    public void AMidSessionCwdChangeRegroupsOnTheNextScan()
    {
        var m = Scan();
        Set(m, AutoGroupBy.Directory);
        var first = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/user/cb281auto");
        Assert.Equal(first, GroupOf(m, "s1"));

        Write(1, "/Users/user/elsewhere");
        m.ScanAndUpdate();
        Glide(m);
        KeyOf(m, "s1");

        var moved = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/user/elsewhere");
        Assert.Equal(moved, GroupOf(m, "s1"));
        Assert.Equal("elsewhere", OrbweaverSettings.OrbGroupFor(moved)!.Name);
        Assert.Equal(first, GroupOf(m, "s0"));
        Assert.Contains(moved, Placed(m));
    }

    [AvaloniaFact]
    public void DirectoryGroupsByTheRepoRootTheSeamResolves()
    {
        var m = Scan(2);
        Write(1, "/Users/user/cb281auto/.claude/worktrees/feature");
        m.AutoGroupRepoRootOf = cwd => cwd.StartsWith("/Users/user/cb281auto", StringComparison.Ordinal)
            ? "/Users/user/cb281auto"
            : null;
        m.ScanAndUpdate();
        KeyOf(m, "s1");

        Set(m, AutoGroupBy.Directory);

        Assert.Equal(GroupOf(m, "s0"), GroupOf(m, "s1"));
    }

    [AvaloniaFact]
    public void AnUnknownAccountIsUngrouped()
    {
        var m = Scan();
        m.AutoGroupAccountOf = status => status.Source == SessionSource.Codex ? ("codex\nme", "me") : null;

        Set(m, AutoGroupBy.Account);

        Assert.Equal(OrbAutoGroups.IdFor(AutoGroupBy.Account, "codex\nme"), GroupOf(m, "s1"));
        Assert.Equal("me", OrbweaverSettings.OrbGroupFor(GroupOf(m, "s1"))!.Name);
        Assert.Equal(OrbGroups.Main, GroupOf(m, "s0"));
        Assert.Equal(OrbGroups.Main, GroupOf(m, "s2"));
    }

    [AvaloniaFact]
    public void TheDragAsksOnlyAGroupThatExistsAndNothingWhileOff()
    {
        var m = Scan();
        var host = (IOrbDragHost)m;
        Assert.Null(host.AutoGroupIdOf("s1"));                       // Off

        // On, but no scan or reapply has made the group yet: a missing id would
        // read as main's slot in the layout, so it is not answered.
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Cli;
        Assert.Null(host.AutoGroupIdOf("s1"));
        Assert.Equal(OrbGroups.Main, GroupOf(m, "s1"));

        m.ReapplyAutoGroups();
        Assert.Equal(CliGroup(1), host.AutoGroupIdOf("s1"));
        Assert.Null(host.AutoGroupIdOf("not-a-session"));
    }

    [AvaloniaFact]
    public void TheGroupMenuSeesTheAutoGroup()
    {
        var m = Scan();
        Set(m, AutoGroupBy.Cli);

        Assert.Equal(CliGroup(2), m.GroupMenuContextFor("s2").GroupId);
        Assert.Contains(CliGroup(2), m.GroupsOnScreen());
    }

    [AvaloniaFact]
    public void ACloudSessionGroupsByTheProfileThatListedIt()
    {
        OrbweaverSettings.ClaudeCloudEnabled = true;
        ClaudeCloudSessions.SetSnapshotForTests(new[]
        {
            new ClaudeCloudSessions.Session(
                Id: "session_cb281", Title: "cloud", State: "idle", LastActivity: DateTime.UtcNow,
                Url: "https://claude.ai/code/session_cb281", StatusBucket: "idle", NeedsAction: false,
                Model: null, ContextPercent: null, StatusDetail: null, RecentAction: null,
                OwnerRoot: "/Users/user/.claude-board"),
        });
        var m = new SessionManager(_dir) { AutoGroupHome = "/Users/user" };
        m.ScanAndUpdate();
        var cloud = m.AllStatuses.Keys.Single(id => id.StartsWith("cloud:", StringComparison.Ordinal));
        Assert.Equal("/Users/user/.claude-board", m.AllStatuses[cloud].ConfigRoot);

        Set(m, AutoGroupBy.Profile);

        var id = OrbAutoGroups.IdFor(AutoGroupBy.Profile, "/users/user/.claude-board");
        Assert.Equal(id, GroupOf(m, cloud));
        Assert.Equal("board", OrbweaverSettings.OrbGroupFor(id)!.Name);
    }

    // --- the facts --------------------------------------------------------------

    [AvaloniaFact]
    public void FactsLookUpOnlyWhatTheAttributeNeeds()
    {
        var m = new SessionManager(_dir);
        var accountAsks = 0;
        var repoAsks = 0;
        m.AutoGroupAccountOf = _ => { accountAsks++; return ("k", "l"); };
        m.AutoGroupRepoRootOf = _ => { repoAsks++; return "/repo"; };
        var status = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, Cwd = "/repo/sub",
            TranscriptPath = "/Users/user/.claude-work/projects/slug/id.jsonl",
        };

        var cli = m.AutoGroupFactsFor(AutoGroupBy.Cli, status);
        Assert.Equal((0, 0), (accountAsks, repoAsks));
        Assert.Equal("/Users/user/.claude-work", cli.ConfigRoot);      // from the transcript
        Assert.Null(cli.AccountKey);
        Assert.Null(cli.RepoRoot);

        var dir = m.AutoGroupFactsFor(AutoGroupBy.Directory, status);
        Assert.Equal((0, 1), (accountAsks, repoAsks));
        Assert.Equal("/repo", dir.RepoRoot);

        var account = m.AutoGroupFactsFor(AutoGroupBy.Account, status);
        Assert.Equal((1, 1), (accountAsks, repoAsks));
        Assert.Equal(("k", "l"), (account.AccountKey, account.AccountLabel));

        // No cwd: the repo seam is not asked at all.
        m.AutoGroupFactsFor(AutoGroupBy.Directory, new SessionStatus { Source = SessionSource.OpenClaw });
        Assert.Equal(1, repoAsks);

        // A cloud row's own root wins; a non-Claude source has none.
        Assert.Equal("/owner", m.AutoGroupFactsFor(AutoGroupBy.Profile,
            new SessionStatus { Source = SessionSource.ClaudeCloud, ConfigRoot = "/owner" }).ConfigRoot);
        Assert.Null(m.AutoGroupFactsFor(AutoGroupBy.Profile,
            new SessionStatus { Source = SessionSource.Codex, TranscriptPath = status.TranscriptPath }).ConfigRoot);
    }

    [AvaloniaFact]
    public void AccountsReadLocalAndCloudClaudeSessionsTheSameWay()
    {
        var reads = new List<string>();
        var accounts = new AutoGroupAccounts("/Users/user", read: path =>
        {
            reads.Add(path);
            return path.Replace('\\', '/').EndsWith("/Users/user/.claude-board/.claude.json", StringComparison.Ordinal)
                ? "{\"oauthAccount\":{\"emailAddress\":\"Board@Example.org\"}}"
                : null;
        });

        var local = SessionManager.AccountOf(accounts, new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = "/Users/user/.claude-board/projects/slug/id.jsonl",
        });
        var cloud = SessionManager.AccountOf(accounts, new SessionStatus
        {
            Source = SessionSource.ClaudeCloud, ConfigRoot = "/Users/user/.claude-board",
        });

        Assert.NotNull(local);
        Assert.Equal(local, cloud);
        Assert.Null(SessionManager.AccountOf(accounts, new SessionStatus { Source = SessionSource.ClaudeCloud }));
        Assert.Null(SessionManager.AccountOf(accounts, new SessionStatus { Source = SessionSource.OpenClaw }));
        Assert.Single(reads);                                          // the second ask was held
    }

    [AvaloniaFact]
    public void TheDefaultSeamsAreTheRealCachedReaders()
    {
        var m = new SessionManager(_dir);

        // The account default reads through AutoGroupAccounts: an OpenClaw
        // session has no account and never touches a file.
        Assert.Null(m.AutoGroupAccountOf(new SessionStatus { Source = SessionSource.OpenClaw }));
        // The repo default is RepoRoots.Cached: a directory with no .git above it has none.
        Assert.Null(m.AutoGroupRepoRootOf(_dir));
    }

    // --- guards on giving a pin back (architect's review) -----------------------

    // Pins an orb at a spot, saves it, and turns grouping on so the orb is
    // drawn in its group somewhere else. Returns the saved spot.
    private PixelPoint SavedPinThenCli(SessionManager m, string id, AutoGroupBy by = AutoGroupBy.Cli)
    {
        var w = Windows(m)[id];
        var pin = new PixelPoint(w.Position.X - 300, w.Position.Y + 40);
        w.PinAt(pin);
        m.RememberOrbPosition(w);
        m.ReapplyArrangement();
        Set(m, by);
        Assert.NotEqual(pin, w.Position);
        return pin;
    }

    [AvaloniaFact]
    public void ADragOutOfAnAutoGroupStaysWhereItWasDroppedThroughTheNextScan()
    {
        // Directory: all three share one group, so there is a group to leave
        // (a lone orb dragged away is moving its own group, not leaving it).
        var m = Scan();
        var oldPin = SavedPinThenCli(m, "s1", AutoGroupBy.Directory);
        var w = Windows(m)["s1"];

        // Carry it far from everything and let go: LeaveToFree, which writes an
        // explicit main. `landed` pins it where it is, as OrbDragSession does,
        // but deliberately does not save the spot, so only the guard can keep
        // the next scan from sending it back to the old saved pin.
        w.Position = new PixelPoint(w.Position.X - 700, w.Position.Y + 300);
        var outcome = OrbDragCommit.Release(m, "s1", false, -700, 300, 1, landed: () => w.PinAt(w.Position));
        Glide(m);
        Assert.IsType<SnapOutcome.LeaveToFree>(outcome);
        Assert.Equal(OrbGroups.Main, OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s1")));
        var dropped = w.Position;

        m.ScanAndUpdate();
        Glide(m);

        Assert.Equal(dropped, w.Position);
        Assert.NotEqual(oldPin, w.Position);
        Assert.True(w.IsPinned);
    }

    [AvaloniaFact]
    public void TheMenusLeaveGroupFromAnAutoGroupDoesNotYankTheOrbToItsOldPin()
    {
        var m = Scan();
        var oldPin = SavedPinThenCli(m, "s1");
        var w = Windows(m)["s1"];
        w.CurrentManager = () => m;
        w.RebuildGroupSubmenu();

        Click(w.GroupMenuItem.Items.OfType<Avalonia.Controls.MenuItem>().Single(i => Equals(i.Header, "Leave group")));
        Glide(m);
        var left = w.Position;
        m.ScanAndUpdate();
        Glide(m);

        Assert.Equal(OrbGroups.Main, GroupOf(m, "s1"));
        Assert.Equal(left, w.Position);
        Assert.NotEqual(oldPin, w.Position);
    }

    [AvaloniaFact]
    public void OffWhileArrangedHandsUnarrangingTheSavedPinNotTheGroupsSpot()
    {
        var m = Scan();
        var pin = SavedPinThenCli(m, "s0");
        var w = Windows(m);
        m.ArrangeOrbsInPattern();
        Glide(m);
        Assert.True(m.IsArranged);

        Set(m, AutoGroupBy.Off);
        Assert.True(m.IsArranged);                         // nothing moved out of the arrangement

        m.ArrangeOrbsInPattern();                          // un-arrange
        Glide(m);

        Assert.False(m.IsArranged);
        Assert.True(w["s0"].IsPinned);
        Assert.Equal(pin, w["s0"].Position);
        Assert.False(w["s1"].IsPinned);                    // no saved spot: the stack
    }

    [AvaloniaFact]
    public void GivingAPinBackLeavesTheOrbsPositionKeyAlone()
    {
        var m = Scan();
        SavedPinThenCli(m, "s0");
        var w = Windows(m)["s0"];
        w.PositionKey = KeyOf(m, "s0") + "-as-made";       // what the orb was made with, if the key moved since
        OrbweaverSettings.SetOrbPosition(w.PositionKey, w.Position.X, w.Position.Y);

        Set(m, AutoGroupBy.Off);

        Assert.Equal(KeyOf(m, "s0") + "-as-made", w.PositionKey);
        OrbweaverSettings.ClearOrbPosition(w.PositionKey);
    }

    // --- the menu's Move to -----------------------------------------------------

    private static void Click(Avalonia.Controls.MenuItem item) =>
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));

    private static Avalonia.Controls.MenuItem MoveTo(OrbWindow w, string header) =>
        w.GroupMenuItem.Items.OfType<Avalonia.Controls.MenuItem>()
            .Single(i => Equals(i.Header, "Move to"))
            .Items.OfType<Avalonia.Controls.MenuItem>()
            .Single(i => Equals(i.Header, header));

    [AvaloniaFact]
    public void MovingAnOrbOntoItsOwnAutoGroupFromTheMenuMakesItAutomaticAgain()
    {
        // Move to offers only groups with an orb on screen, so the group s1
        // classifies into needs the other two in it: Directory.
        var m = Scan();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s1"), OrbGroups.Main);
        Set(m, AutoGroupBy.Directory);
        var dir = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/users/user/cb281auto");
        Assert.Equal(OrbGroups.Main, GroupOf(m, "s1"));
        var w = Windows(m)["s1"];
        w.CurrentManager = () => m;
        w.RebuildGroupSubmenu();

        Click(MoveTo(w, "cb281auto"));

        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s1")));
        Assert.Equal(dir, GroupOf(m, "s1"));
    }

    [AvaloniaFact]
    public void MovingAnOrbOntoAnotherAutoGroupFromTheMenuWritesIt()
    {
        var m = Scan();
        Set(m, AutoGroupBy.Cli);
        var w = Windows(m)["s1"];
        w.CurrentManager = () => m;
        w.RebuildGroupSubmenu();

        Click(MoveTo(w, "Claude"));

        Assert.Equal(CliGroup(0), OrbweaverSettings.OrbGroupMembershipFor(KeyOf(m, "s1")));
        Assert.Equal(CliGroup(0), GroupOf(m, "s1"));
    }

    // --- paths and accounts the lead and QA named ------------------------------

    [AvaloniaFact]
    public void AWindowsCwdGroupsWithTheSamePathInAnyCaseAndSeparator()
    {
        var m = Scan(2);
        Write(0, @"C:\Users\x\Repo\");
        Write(1, "c:/users/x/repo");
        m.ScanAndUpdate();
        KeyOf(m, "s0");
        KeyOf(m, "s1");

        Set(m, AutoGroupBy.Directory);

        Assert.True(OrbAutoGroups.IsAuto(GroupOf(m, "s0")));
        Assert.Equal(GroupOf(m, "s0"), GroupOf(m, "s1"));
    }

    [AvaloniaFact]
    public void CodexFindsItsAccountThroughItsRolloutAndANullTranscriptIsUnknown()
    {
        var accounts = new AutoGroupAccounts("/Users/user", read: path =>
            path.Replace('\\', '/') == "/Users/user/.codex/auth.json" ? "{\"email\":\"Cody@example.org\"}" : null);

        var codex = SessionManager.AccountOf(accounts, new SessionStatus
        {
            Source = SessionSource.Codex,
            TranscriptPath = "/Users/user/.codex/sessions/2026/11/14/rollout-1-x.jsonl",
        });
        Assert.NotNull(codex);
        Assert.StartsWith("codex\n", codex!.Value.Key);

        // OrbweaverHook.sh can write a null transcript_path for Codex.
        var none = new SessionStatus { Source = SessionSource.Codex, TranscriptPath = null! };
        Assert.Null(SessionManager.AccountOf(accounts, none));
        var m = new SessionManager(_dir) { AutoGroupAccountOf = s => SessionManager.AccountOf(accounts, s) };
        var facts = m.AutoGroupFactsFor(AutoGroupBy.Account, none);
        Assert.Null(facts.AccountKey);
        Assert.Null(OrbAutoGroups.Classify(AutoGroupBy.Account, facts, "/Users/user"));
        Assert.Null(m.AutoGroupFactsFor(AutoGroupBy.Profile,
            new SessionStatus { Source = SessionSource.ClaudeCode, TranscriptPath = null! }).ConfigRoot);
    }

    // --- round 3: precedence end to end, accounts that arrive late ---------------

    private static bool Gliding(SessionManager m) =>
        typeof(SessionManager).GetField("_arrangeAnimTargets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(m) is not null;

    private static long JustNow => DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds();

    // A heartbeat, a cron and an ordinary chat on the gateway, through the
    // parser a real poll uses.
    private static void PublishGateway(params string[] keys)
    {
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        var rows = string.Join(",", keys.Select(k => $$"""{"key":"{{k}}","lastActivityAt":{{JustNow}}}"""));
        var (sessions, _) = OpenClawSessions.Parse(
            System.Text.Json.JsonDocument.Parse("{\"sessions\":[" + rows + "]}").RootElement, DateTime.UtcNow);
        OpenClawSessions.SetSnapshotForTests(sessions);
    }

    private const string Heartbeat = "agent:main:main";
    private const string Cron = "agent:aurora:cron:5d1f0c2e-0000-4000-8000-000000000001";
    private const string Chat = "agent:aurora:discord:direct:2467";

    private void WithGateway(Action body)
    {
        var enabled = OrbweaverSettings.OpenClawEnabled;
        var within = OrbweaverSettings.OpenClawActiveWithinMinutes;
        var heartbeats = OrbweaverSettings.OpenClawHeartbeatMode;
        var crons = OrbweaverSettings.OpenClawCronMode;
        try { body(); }
        finally
        {
            OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
            OrbweaverSettings.OpenClawEnabled = enabled;
            OrbweaverSettings.OpenClawActiveWithinMinutes = within;
            OrbweaverSettings.OpenClawHeartbeatMode = heartbeats;
            OrbweaverSettings.OpenClawCronMode = crons;
        }
    }

    [AvaloniaFact]
    public void AHeartbeatAndACronInTheirOwnShapeStayInTheirClustersUnderCli() => WithGateway(() =>
    {
        PublishGateway(Heartbeat, Cron, Chat);
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.OwnShape;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.OwnShape;
        var m = new SessionManager(_dir);
        m.ScanAndUpdate();
        Assert.Contains("openclaw:" + Heartbeat, m.AllStatuses.Keys);
        Assert.Contains("openclaw:" + Cron, m.AllStatuses.Keys);

        Set(m, AutoGroupBy.Cli);

        var openclaw = OrbAutoGroups.IdFor(AutoGroupBy.Cli, "openclaw");
        Assert.Equal(OrbGroups.Heartbeats, GroupOf(m, "openclaw:" + Heartbeat));
        Assert.Equal(OrbGroups.Crons, GroupOf(m, "openclaw:" + Cron));
        // Control: the chat beside them is auto-grouped, so CLI really is on.
        Assert.Equal(openclaw, GroupOf(m, "openclaw:" + Chat));
    });

    [AvaloniaFact]
    public void AHeartbeatAndACronGatheredWithTheChatsAreAutoGroupedLikeAChat() => WithGateway(() =>
    {
        PublishGateway(Heartbeat, Cron, Chat);
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.WithChats;
        var m = new SessionManager(_dir);
        m.ScanAndUpdate();

        Set(m, AutoGroupBy.Cli);

        var openclaw = OrbAutoGroups.IdFor(AutoGroupBy.Cli, "openclaw");
        Assert.Equal(openclaw, GroupOf(m, "openclaw:" + Heartbeat));
        Assert.Equal(openclaw, GroupOf(m, "openclaw:" + Cron));
    });

    [AvaloniaFact]
    public void AHiddenHeartbeatHasNoOrbAndMakesNoGroup() => WithGateway(() =>
    {
        // Before the parse: Hidden drops the row there (OpenClawSessions.Parse).
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.Hidden;
        PublishGateway(Heartbeat);
        var m = new SessionManager(_dir);
        m.ScanAndUpdate();
        Assert.Empty(Windows(m));

        Set(m, AutoGroupBy.Cli);

        Assert.Null(OrbweaverSettings.OrbGroupFor(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "openclaw")));
        Assert.DoesNotContain(OrbweaverSettings.OrbGroupList(), g => OrbAutoGroups.IsAuto(g.Id));
    });

    // CB-306's fixture (SessionScanTests): a local Claude Code session that
    // OpenClaw spawned to run a heartbeat, beside an ordinary Codex session.
    // A different route to a heartbeat orb from the gateway's, and the one a
    // CLI classifier would otherwise call "claude".
    private static string SpawnTurn(string prompt) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "user", message = new { content = prompt } }) + "\n";

    private const string HeartbeatPrompt = "A scheduled reminder has been triggered. HEARTBEAT: run the routine.";

    // A null prompt writes an empty transcript: a spawn whose first prompt
    // has not landed yet.
    private SessionManager ScanHeartbeatSpawn(
        ClusterMode heartbeats, string? prompt = HeartbeatPrompt, ClusterMode crons = ClusterMode.WithChats)
    {
        OrbweaverSettings.OpenClawHeartbeatMode = heartbeats;
        OrbweaverSettings.OpenClawCronMode = crons;
        OrbweaverSettings.OpenClawEnabled = false;
        OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>());
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;

        var workspace = Path.Combine(_dir, ".openclaw", "workspace-sample-zara");
        var transcript = Path.Combine(_dir, "spawn.jsonl");
        File.WriteAllText(transcript, prompt is null ? "" : SpawnTurn(prompt));
        void Status(string id, string cli, string cwd, string path) =>
            File.WriteAllText(Path.Combine(_dir, id + ".txt"), System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = cli, Cwd = cwd, SessionPid = Environment.ProcessId, TermProgram = "iTerm.app",
                Tty = "/dev/ttys004", TranscriptPath = path, Entrypoint = "sdk-cli",
            }));
        Status("spawn", "", workspace, transcript);
        Status("ordinary", "codex", "/Users/user/Source/project", "");

        var m = new SessionManager(_dir) { AutoGroupRepoRootOf = _ => null };
        m.ScanAndUpdate();
        foreach (var id in m.AllStatuses.Keys) KeyOf(m, id);
        return m;
    }

    private void WithHeartbeatSpawn(Action body)
    {
        var before = (OrbweaverSettings.OpenClawHeartbeatMode, OrbweaverSettings.OpenClawEnabled,
            OrbweaverSettings.OpenClawCronMode);
        try { body(); }
        finally
        {
            OrbweaverSettings.OpenClawHeartbeatMode = before.OpenClawHeartbeatMode;
            OrbweaverSettings.OpenClawCronMode = before.OpenClawCronMode;
            OrbweaverSettings.OpenClawEnabled = before.OpenClawEnabled;
            OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>());
            OpenClawCliSession.ForgetForTests();
        }
    }

    // CB-306 gap fixed under CB-281: a spawned heartbeat used to be drawn as
    // a chat, in main, even with heartbeats in their own shape, because its
    // cluster decided only whether it was shown. The scan now marks it
    // (SessionManager.MarkSpawnCluster), so it joins the heartbeats cluster —
    // which auto-grouping yields to.
    [AvaloniaFact]
    public void ASpawnedHeartbeatInItsOwnShapeDrawsInHeartbeatsWithAutoGroupOffOrOn() => WithHeartbeatSpawn(() =>
    {
        var m = ScanHeartbeatSpawn(ClusterMode.OwnShape);
        Assert.True(m.AllStatuses["spawn"].Heartbeat);
        Assert.Equal(OrbGroups.Heartbeats, GroupOf(m, "spawn"));      // Off: the control

        Set(m, AutoGroupBy.Cli);

        Assert.Equal(OrbGroups.Heartbeats, GroupOf(m, "spawn"));
        Assert.Null(OrbweaverSettings.OrbGroupFor(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude")));
        // Control: the Codex session beside it is grouped, so CLI really is on.
        Assert.Equal(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "codex"), GroupOf(m, "ordinary"));
        Assert.False(m.AllStatuses["ordinary"].Heartbeat);
    });

    [AvaloniaFact]
    public void ASpawnedHeartbeatGatheredWithTheChatsIsStillDrawnAsAChat() => WithHeartbeatSpawn(() =>
    {
        var m = ScanHeartbeatSpawn(ClusterMode.WithChats);
        Assert.True(m.AllStatuses["spawn"].Heartbeat);                 // it wears the heart
        Assert.Equal(OrbGroups.Main, GroupOf(m, "spawn"));             // but sits with the chats

        Set(m, AutoGroupBy.Cli);

        Assert.Equal(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude"), GroupOf(m, "spawn"));
    });

    [AvaloniaFact]
    public void ASpawnWhoseHeartbeatPromptArrivesLateMovesToHeartbeatsExactlyOnce() => WithHeartbeatSpawn(() =>
    {
        // No user turn yet: unsettled, so it is drawn as a chat and read again
        // once the file grows (OpenClawCliSession.ReadCluster).
        var m = ScanHeartbeatSpawn(ClusterMode.OwnShape, prompt: null);
        Set(m, AutoGroupBy.Cli);
        Assert.False(m.AllStatuses["spawn"].Heartbeat);
        Assert.Equal(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude"), GroupOf(m, "spawn"));

        File.AppendAllText(Path.Combine(_dir, "spawn.jsonl"), SpawnTurn(HeartbeatPrompt));
        m.ScanAndUpdate();
        Assert.True(Gliding(m));                                   // the one regroup
        Glide(m);
        Assert.True(m.AllStatuses["spawn"].Heartbeat);
        Assert.Equal(OrbGroups.Heartbeats, GroupOf(m, "spawn"));
        var settled = Windows(m).ToDictionary(p => p.Key, p => p.Value.Position);

        for (var scan = 0; scan < 3; scan++)
        {
            m.ScanAndUpdate();
            Assert.False(Gliding(m));                              // nothing changed, nothing moves
            Assert.Equal(settled, Windows(m).ToDictionary(p => p.Key, p => p.Value.Position));
        }
        Assert.Equal(OrbGroups.Heartbeats, GroupOf(m, "spawn"));
    });

    [AvaloniaFact]
    public void ASpawnedCronInItsOwnShapeDrawsInCronsUnderCli() => WithHeartbeatSpawn(() =>
    {
        var m = ScanHeartbeatSpawn(ClusterMode.WithChats, "[cron:nightly] run the report", ClusterMode.OwnShape);
        Assert.Equal(SessionKind.Cron, m.AllStatuses["spawn"].Kind);
        Assert.False(m.AllStatuses["spawn"].Heartbeat);
        Assert.Equal(OrbGroups.Crons, GroupOf(m, "spawn"));

        Set(m, AutoGroupBy.Cli);

        Assert.Equal(OrbGroups.Crons, GroupOf(m, "spawn"));
        Assert.Null(OrbweaverSettings.OrbGroupFor(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude")));
    });

    [AvaloniaFact]
    public void AHiddenSpawnedHeartbeatLeavesNoPhantomAutoGroup() => WithHeartbeatSpawn(() =>
    {
        var m = ScanHeartbeatSpawn(ClusterMode.Hidden);
        Assert.DoesNotContain("spawn", Windows(m).Keys);

        Set(m, AutoGroupBy.Cli);

        Assert.Null(OrbweaverSettings.OrbGroupFor(OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude")));
        Assert.Equal(new[] { OrbAutoGroups.IdFor(AutoGroupBy.Cli, "codex") },
            OrbweaverSettings.OrbGroupList().Where(g => OrbAutoGroups.IsAuto(g.Id)).Select(g => g.Id));
    });

    [AvaloniaFact]
    public void AGrokAccountThatArrivesWithTheTranscriptMovesTheOrbExactlyOnce()
    {
        // Measured on a real Grok session: transcript_path is empty at
        // SessionStart and filled seconds later.
        var m = Scan();
        m.AutoGroupAccountOf = status =>
            status.Source == SessionSource.Grok && !string.IsNullOrEmpty(status.TranscriptPath)
                ? ("grok\nme@example.org", "me")
                : null;
        Set(m, AutoGroupBy.Account);
        Assert.Equal(OrbGroups.Main, GroupOf(m, "s2"));

        Write(2, transcript: "/Users/user/.grok/sessions/%2FUsers%2Fuser%2Fcb281auto/s2/updates.jsonl");
        m.ScanAndUpdate();
        Assert.True(Gliding(m));                                // the one regroup
        Glide(m);
        var account = OrbAutoGroups.IdFor(AutoGroupBy.Account, "grok\nme@example.org");
        Assert.Equal(account, GroupOf(m, "s2"));
        var settled = Windows(m).ToDictionary(p => p.Key, p => p.Value.Position);

        for (var scan = 0; scan < 3; scan++)
        {
            m.ScanAndUpdate();
            Assert.False(Gliding(m));                           // nothing changed, nothing moves
            Assert.Equal(settled, Windows(m).ToDictionary(p => p.Key, p => p.Value.Position));
        }
        Assert.Equal(account, GroupOf(m, "s2"));
    }

    [AvaloniaFact]
    public void ACodexStatusWithANullTranscriptPathScansAndStaysUngroupedUnderAccount()
    {
        var m = Scan();
        Write(1, transcript: null);
        m.ScanAndUpdate();
        Assert.True(string.IsNullOrEmpty(m.AllStatuses["s1"].TranscriptPath));

        // The real default seam, not an injected one: HomeOf answers null and
        // nothing is read.
        Set(m, AutoGroupBy.Account);

        Assert.Equal(OrbGroups.Main, GroupOf(m, "s1"));
        Assert.Null(((IOrbDragHost)m).AutoGroupIdOf("s1"));
    }

    [AvaloniaFact]
    public void ANamelessGroupsCaptionNumberIgnoresAutoGroups()
    {
        var m = Scan();
        Group();
        var unknown = "g-" + Guid.NewGuid().ToString("N")[..8];
        var before = GroupBackdrops.CaptionFor(unknown);

        Set(m, AutoGroupBy.Cli);
        Assert.Contains(OrbweaverSettings.OrbGroupList(), g => OrbAutoGroups.IsAuto(g.Id));

        Assert.Equal(before, GroupBackdrops.CaptionFor(unknown));
        Assert.Equal("Codex", GroupBackdrops.CaptionFor(CliGroup(1)));    // an auto group wears its name
    }
}
