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

    private void Write(int i, string cwd = "/Users/user/cb281auto") =>
        File.WriteAllText(Path.Combine(_dir, "s" + i + ".txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = Clis[i],
                Cwd = cwd,
                Title = "t" + i,
                SessionPid = Environment.ProcessId,
                TermProgram = "iTerm.app",
                Tty = "/dev/ttys004",
            }));

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
        // The drag still knows which group it would classify into.
        Assert.Equal(CliGroup(1), ((IOrbDragHost)m).AutoGroupIdOf("s1"));
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
}
