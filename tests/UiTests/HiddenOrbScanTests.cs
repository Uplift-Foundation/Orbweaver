using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-276 QA: "Hide this orb" through a real scan, for every source the
// feature promises, plus teams, rooms, sound and the tray's counting.
// HiddenOrbCoreScanTests covers local sessions and pruning; this covers the
// rest and tries to break the rules where they meet.
[Collection("Settings")]
public class HiddenOrbScanTests : IDisposable
{
    private static readonly int LivePid = Environment.ProcessId;
    private readonly string _statusDir = Path.Combine(Path.GetTempPath(), "cb-hiddenqa-" + Guid.NewGuid());
    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), "cb-hiddenqa-settings-" + Guid.NewGuid());
    private readonly List<string> _played = new();
    private readonly object _lock = new();

    public HiddenOrbScanTests()
    {
        Directory.CreateDirectory(_statusDir);
        Directory.CreateDirectory(_settingsDir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _settingsDir);
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        TurnSounds.ResetForTests();
        ChimePlayer.PlayForTests = path => { lock (_lock) _played.Add(path); };
        SpeechRequest.UtteranceForTests = (_, _, _) => { };
    }

    public void Dispose()
    {
        ChimePlayer.PlayForTests = null;
        SpeechRequest.UtteranceForTests = null;
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        RemoteControlSessions.SetSnapshotForTests(Array.Empty<RemoteControlSessions.Remote>());
        ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());
        OrbweaverSettings.ClaudeCloudEnabled = false;
        OpenClawSessions.SetListingCurrentForTests(false);
        RemoteControlSessions.SetListedByPeerForTests(new Dictionary<string, IReadOnlySet<string>>());
        try { Directory.Delete(_statusDir, recursive: true); } catch { }
        try { Directory.Delete(_settingsDir, recursive: true); } catch { }
    }

    private SessionManager NewManager()
    {
        var ctor = typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;
        return (SessionManager)ctor.Invoke(new object[] { _statusDir });
    }

    private static T Field<T>(SessionManager m, string name) =>
        (T)typeof(SessionManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static List<string> Orbs(SessionManager m) =>
        Field<System.Collections.IDictionary>(m, "_windows").Keys.Cast<string>().OrderBy(x => x).ToList();

    private static List<string> Hidden(SessionManager m) =>
        Field<List<(string SessionId, SessionStatus Status)>>(m, "_hidden").Select(h => h.SessionId).OrderBy(x => x).ToList();

    private void WriteLocal(string id, string cli, string state = "idle")
    {
        File.WriteAllText(Path.Combine(_statusDir, id + ".txt"), JsonSerializer.Serialize(new SessionStatus
        {
            State = state, Cli = cli, Cwd = "/Users/user/project", SessionPid = LivePid,
            TermProgram = "iTerm.app", Tty = "/dev/ttys004",
        }));
    }

    // ---- OpenClaw ---------------------------------------------------------

    private static string JustNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    private static void PublishGateway(string sessionsJson)
    {
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        var (sessions, _) = OpenClawSessions.Parse(JsonDocument.Parse(sessionsJson).RootElement, DateTime.UtcNow);
        OpenClawSessions.SetSnapshotForTests(sessions);
    }

    private static string TwoAgentRoom() => $$"""
        {"sessions":[
          {"key":"agent:nova:discord:channel:1474","chatType":"channel","groupChannel":"#general","lastActivityAt":{{JustNow}}},
          {"key":"agent:annabel:discord:channel:1474","chatType":"channel","groupChannel":"#general","lastActivityAt":{{JustNow}}}
        ]}
        """;

    private const string NovaId = "openclaw:agent:nova:discord:channel:1474";
    private const string AnnabelId = "openclaw:agent:annabel:discord:channel:1474";
    private static readonly string RoomId = SessionManager.RoomId("discord:1474");

    [AvaloniaFact]
    public void AnOpenClawSessionHidesStaysHiddenAcrossARestartAndRestores()
    {
        PublishGateway($$"""{"sessions":[{"key":"agent:aurora:discord:direct:2467","chatType":"direct","lastActivityAt":{{JustNow}}}]}""");
        var id = "openclaw:agent:aurora:discord:direct:2467";
        var m = NewManager();
        m.ScanAndUpdate();
        Assert.Contains(id, Orbs(m));

        m.HideSession(id);
        m.ScanAndUpdate();
        m.ScanAndUpdate();
        Assert.DoesNotContain(id, Orbs(m));
        Assert.Contains(id, Hidden(m));
        Assert.Equal("OpenClaw", OrbweaverSettings.HiddenOrbList().Single().Source);

        // a restart: a fresh manager on the same settings file
        OrbweaverSettings.ReloadForTests();
        var restarted = NewManager();
        restarted.ScanAndUpdate();
        Assert.DoesNotContain(id, Orbs(restarted));

        restarted.RestoreHiddenOrb(id);
        restarted.ScanAndUpdate();
        Assert.Contains(id, Orbs(restarted));
    }

    [AvaloniaFact]
    public void HidingARoomLeavesItsMembersStandingAloneWithNoLead()
    {
        PublishGateway(TwoAgentRoom());
        var m = NewManager();
        m.ScanAndUpdate();
        Assert.Contains(RoomId, Orbs(m));
        Assert.Equal(RoomId, m.StatusFor(NovaId)!.Lead);
        Assert.False(m.LeadsATeam(RoomId)); // a room is not a team lead for hiding

        m.HideSession(RoomId);
        m.ScanAndUpdate();

        Assert.DoesNotContain(RoomId, Orbs(m));
        Assert.Contains(NovaId, Orbs(m));
        Assert.Contains(AnnabelId, Orbs(m));
        Assert.Equal("", m.StatusFor(NovaId)!.Lead);
        Assert.Equal("", m.StatusFor(AnnabelId)!.Lead);
        Assert.Contains(RoomId, Hidden(m));
        Assert.Equal("OpenClaw room", OrbweaverSettings.HiddenOrbList().Single().Source);
    }

    [AvaloniaFact]
    public void RestoringARoomGivesItsMembersTheirLeadBack()
    {
        PublishGateway(TwoAgentRoom());
        var m = NewManager();
        m.ScanAndUpdate();
        m.HideSession(RoomId);
        m.ScanAndUpdate();
        m.RestoreHiddenOrb(RoomId);
        m.ScanAndUpdate();
        Assert.Contains(RoomId, Orbs(m));
        Assert.Equal(RoomId, m.StatusFor(NovaId)!.Lead);
    }

    [AvaloniaFact]
    public void HidingARoomMemberLeavesTheRoomAndTheOtherMemberUntouched()
    {
        PublishGateway(TwoAgentRoom());
        var m = NewManager();
        m.ScanAndUpdate();

        m.HideSession(NovaId);
        m.ScanAndUpdate();

        Assert.DoesNotContain(NovaId, Orbs(m));
        Assert.Contains(RoomId, Orbs(m));
        Assert.Contains(AnnabelId, Orbs(m));
        Assert.Equal(RoomId, m.StatusFor(AnnabelId)!.Lead);
    }

    [AvaloniaFact]
    public void ARoomMemberIsNeverHiddenByItsRoomsHideThroughPropagation()
    {
        // Decision 4, from the other side: the room id is explicit, a member's
        // Lead is the room id, and the member must still be drawn.
        PublishGateway(TwoAgentRoom());
        var m = NewManager();
        m.ScanAndUpdate();
        OrbweaverSettings.HideOrb(new HiddenOrb(RoomId, DateTime.UtcNow, "r", "OpenClaw room", ""));
        m.ScanAndUpdate();
        Assert.Contains(NovaId, Orbs(m));
        Assert.Contains(AnnabelId, Orbs(m));
    }

    // ---- Cloud ------------------------------------------------------------

    private static ClaudeCloudSessions.Session CloudSession(string id) => new(
        Id: id, Title: "Refactor", State: "idle", LastActivity: DateTime.UtcNow.AddSeconds(-5),
        Url: "https://claude.ai/code/" + id, StatusBucket: "idle", NeedsAction: false, Model: "claude-opus-5",
        ContextPercent: null, StatusDetail: null, RecentAction: null, OwnerRoot: null);

    [AvaloniaFact]
    public void ACloudSessionHidesAndRestores()
    {
        OrbweaverSettings.ClaudeCloudEnabled = true;
        ClaudeCloudSessions.SetSnapshotForTests(new[] { CloudSession("session_01a"), CloudSession("session_01b") });
        var m = NewManager();
        m.ScanAndUpdate();
        Assert.Equal(new[] { "cloud:session_01a", "cloud:session_01b" }, Orbs(m));

        m.HideSession("cloud:session_01a");
        m.ScanAndUpdate();
        Assert.Equal(new[] { "cloud:session_01b" }, Orbs(m));
        Assert.Equal("Claude cloud", OrbweaverSettings.HiddenOrbList().Single().Source);

        m.RestoreAllHiddenOrbs();
        m.ScanAndUpdate();
        Assert.Equal(2, Orbs(m).Count);
    }

    // ---- Remote Control ---------------------------------------------------

    private static RemoteControlSessions.Remote Far(string route, string? lead = null, string? agent = null) =>
        new("team title", "far-mac", "idle", DateTime.UtcNow, ".claude", Route: route, LeadRoute: lead, Agent: agent);

    private const string RcLead = "rc:.claude:sid:lead", RcA = "rc:.claude:sid:a", RcB = "rc:.claude:sid:b";

    private static void PublishFarTeam()
    {
        OrbweaverSettings.PeerLinkEnabled = true;
        RemoteControlSessions.SetSnapshotForTests(new[]
        {
            Far("sid:lead"), Far("sid:a", "sid:lead", "wren-asare"), Far("sid:b", "sid:lead", "hana-moriyama"),
        });
    }

    [AvaloniaFact]
    public void HidingAFarLeadHidesItsMembersAndRestoringBringsThemAll()
    {
        PublishFarTeam();
        var m = NewManager();
        m.ScanAndUpdate();
        Assert.Equal(new[] { RcA, RcB, RcLead }, Orbs(m));
        Assert.True(m.LeadsATeam(RcLead));
        Assert.False(m.LeadsATeam(RcA));

        m.HideSession(RcLead);
        m.ScanAndUpdate();
        Assert.Empty(Orbs(m));
        Assert.Equal(new[] { RcA, RcB, RcLead }, Hidden(m));
        Assert.Equal("Other machine", OrbweaverSettings.HiddenOrbList().Single().Source);
        // the members have no entry of their own
        Assert.Single(OrbweaverSettings.HiddenOrbIds());

        m.RestoreHiddenOrb(RcLead);
        m.ScanAndUpdate();
        Assert.Equal(new[] { RcA, RcB, RcLead }, Orbs(m));
    }

    [AvaloniaFact]
    public void HidingAFarMemberLeavesItsLeadAndSiblingAndSurvivesTheLeadsRestore()
    {
        PublishFarTeam();
        var m = NewManager();
        m.ScanAndUpdate();

        m.HideSession(RcA);
        m.ScanAndUpdate();
        Assert.Equal(new[] { RcB, RcLead }, Orbs(m));

        // hide the lead too, then restore only the lead: the member's own hide holds
        m.HideSession(RcLead);
        m.ScanAndUpdate();
        Assert.Empty(Orbs(m));
        m.RestoreHiddenOrb(RcLead);
        m.ScanAndUpdate();
        Assert.Equal(new[] { RcB, RcLead }, Orbs(m));
    }

    [AvaloniaFact]
    public void AFarHideRecordsTheMachineThatListsIt()
    {
        PublishFarTeam();
        var m = NewManager();
        m.ScanAndUpdate();
        m.HideSession(RcA);
        var entry = OrbweaverSettings.HiddenOrbList().Single();
        Assert.Equal("far-mac", entry.Peer);
        Assert.Equal("wren-asare", entry.Detail);
    }

    // ---- Local ------------------------------------------------------------

    [AvaloniaFact]
    public void AHiddenSessionWithNoFileLeftIsNotListedAnywhere()
    {
        WriteLocal("a", "");
        var m = NewManager();
        m.ScanAndUpdate();
        m.HideSession("a");
        m.ScanAndUpdate();
        File.Delete(Path.Combine(_statusDir, "a.txt"));
        m.ScanAndUpdate();
        // the tray/phone list only what the scan found this pass
        Assert.Empty(Hidden(m));
    }

    [AvaloniaFact]
    public void AHiddenSessionsLiveStateReachesTheTrayAndPhoneList()
    {
        WriteLocal("a", "", "idle");
        var m = NewManager();
        m.ScanAndUpdate();
        m.HideSession("a");
        m.ScanAndUpdate();

        WriteLocal("a", "", "waiting");
        m.ScanAndUpdate();

        var hidden = Field<List<(string SessionId, SessionStatus Status)>>(m, "_hidden").Single();
        Assert.Equal("waiting", hidden.Status.State);
        Assert.Equal("project — needs you (hidden)",
            TrayController.SessionLabel(new TrayController.SessionEntry(hidden.SessionId, hidden.Status, Hidden: true), false));
    }

    // ---- Sound ------------------------------------------------------------

    [AvaloniaFact]
    public async Task AHiddenSessionFinishingATurnMakesNoSoundWhileAVisibleOneBesideItDoes()
    {
        WriteLocal("a", "", "generating");
        WriteLocal("b", "codex", "generating");
        var m = NewManager();
        m.ScanAndUpdate();
        m.HideSession("a");
        m.ScanAndUpdate();

        WriteLocal("a", "", "idle");
        m.ScanAndUpdate();
        await TurnSounds.ChimesEnqueuedSoFar();
        Assert.Empty(_played);

        WriteLocal("b", "codex", "idle");
        m.ScanAndUpdate();
        for (var i = 0; i < 100 && _played.Count == 0; i++) await Task.Delay(50);
        Assert.Single(_played);   // the control: the same scan chimes for a visible orb
    }

    [AvaloniaFact]
    public async Task RestoringASessionThatFinishedWhileHiddenDoesNotChime()
    {
        WriteLocal("a", "", "generating");
        var m = NewManager();
        m.ScanAndUpdate();
        m.HideSession("a");
        m.ScanAndUpdate();
        WriteLocal("a", "", "idle");
        m.ScanAndUpdate();

        m.RestoreHiddenOrb("a");
        m.ScanAndUpdate();
        await TurnSounds.ChimesEnqueuedSoFar();
        await Task.Delay(300);
        Assert.Empty(_played);
    }

    [AvaloniaFact]
    public async Task RestoringASessionStillWaitingDoesNotChimeEither()
    {
        // The plan's "Not read": what Observe returns the first time it meets
        // a session already in waiting. Pinned here as a measurement.
        WriteLocal("a", "", "waiting");
        var m = NewManager();
        m.ScanAndUpdate();
        await TurnSounds.ChimesEnqueuedSoFar();
        _played.Clear();
        m.HideSession("a");
        m.ScanAndUpdate();
        m.RestoreHiddenOrb("a");
        m.ScanAndUpdate();
        await TurnSounds.ChimesEnqueuedSoFar();
        await Task.Delay(300);
        Assert.Empty(_played);
    }

    // ---- Pruning through the real seams ------------------------------------

    private const string AuroraKey = "agent:aurora:discord:direct:2467";
    private const string AuroraId = "openclaw:" + AuroraKey;

    private static string Aurora(string key = AuroraKey) =>
        $$"""{"sessions":[{"key":"{{key}}","chatType":"direct","lastActivityAt":{{JustNow}}}]}""";

    private SessionManager HiddenAuroraManager()
    {
        PublishGateway(Aurora());
        OpenClawSessions.SetListingCurrentForTests(true);
        var m = NewManager();
        m.HiddenPruneGrace = TimeSpan.Zero;
        m.ScanAndUpdate();
        m.HideSession(AuroraId);
        m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
        return m;
    }

    [AvaloniaFact]
    public void AGatewayListingWithoutTheKeyPrunesItAfterTheGrace()
    {
        var m = HiddenAuroraManager();
        PublishGateway(Aurora("agent:other:discord:direct:1"));
        OpenClawSessions.SetListingCurrentForTests(true);
        m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());   // the first absent pass only starts the clock
        m.ScanAndUpdate();
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void ADisconnectedGatewayPrunesNothingHoweverLongItStaysDown()
    {
        var m = HiddenAuroraManager();
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        OpenClawSessions.SetListingCurrentForTests(false);
        for (var i = 0; i < 6; i++) m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void AHiddenConversationThatWentQuietIsNotTakenForEnded()
    {
        // Parse drops a session quiet for longer than "active within"; the
        // hide must survive that, or it un-hides on the next message.
        var m = HiddenAuroraManager();
        var old = DateTimeOffset.UtcNow.AddHours(-5).ToUnixTimeMilliseconds();
        OrbweaverSettings.OpenClawActiveWithinMinutes = 1;
        var (sessions, _) = OpenClawSessions.Parse(JsonDocument.Parse(
            $$"""{"sessions":[{"key":"{{AuroraKey}}","chatType":"direct","lastActivityAt":{{old}}}]}""").RootElement,
            DateTime.UtcNow);
        Assert.Empty(sessions);   // the control: the snapshot really no longer carries it
        OpenClawSessions.SetSnapshotForTests(sessions);
        OpenClawSessions.SetListingCurrentForTests(true);
        for (var i = 0; i < 5; i++) m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
    }

    private static void PublishListedByPeer(params (string Peer, string[] Keys)[] peers) =>
        RemoteControlSessions.SetListedByPeerForTests(peers.ToDictionary(
            p => p.Peer, p => (IReadOnlySet<string>)p.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase));

    private SessionManager HiddenFarMember()
    {
        PublishFarTeam();
        PublishListedByPeer(("far-mac", new[] { RcLead, RcA, RcB }));
        var m = NewManager();
        m.HiddenPruneGrace = TimeSpan.Zero;
        m.ScanAndUpdate();
        m.HideSession(RcA);
        m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
        return m;
    }

    [AvaloniaFact]
    public void AFarMemberTheMachineNoLongerListsIsPrunedButOnlyWhileTheMachineIsAnswering()
    {
        var m = HiddenFarMember();
        PublishListedByPeer(("far-mac", new[] { RcLead, RcB }));   // answering, without RcA
        m.ScanAndUpdate();
        m.ScanAndUpdate();
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void AFarMachineThatListsNothingPrunesNothing()
    {
        var m = HiddenFarMember();
        PublishListedByPeer();                                     // off, asleep or reconnecting
        for (var i = 0; i < 6; i++) m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void AnotherMachinesRosterNeverPrunesThisMachinesHide()
    {
        var m = HiddenFarMember();
        PublishListedByPeer(("other-mac", new[] { "rc:.claude:sid:x" }));
        for (var i = 0; i < 6; i++) m.ScanAndUpdate();
        Assert.Single(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void AFarHideKeepsItsPeerAcrossARestartSoPruningStillWorks()
    {
        var m = HiddenFarMember();
        OrbweaverSettings.ReloadForTests();
        var restarted = NewManager();
        restarted.HiddenPruneGrace = TimeSpan.Zero;
        PublishListedByPeer(("far-mac", new[] { RcLead, RcB }));
        restarted.ScanAndUpdate();
        restarted.ScanAndUpdate();
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    // ---- Tray counting ----------------------------------------------------

    [Fact]
    public void AHiddenWaitingSessionLeavesTheIconWhereAVisibleIdleOneWould()
    {
        var idle = new SessionStatus { State = "idle" };
        var waiting = new SessionStatus { State = "waiting" };
        var gen = new SessionStatus { State = "generating" };
        var counts = TrayController.IconCounts(new[]
        {
            new TrayController.SessionEntry("v", idle),
            new TrayController.SessionEntry("h1", waiting, Hidden: true),
            new TrayController.SessionEntry("h2", gen, Hidden: true),
        });
        Assert.Equal((1, 0, 0), counts);
    }

    [Fact]
    public void AVisibleWaitingSessionStillCountsAlongsideHiddenOnes()
    {
        var counts = TrayController.IconCounts(new[]
        {
            new TrayController.SessionEntry("v", new SessionStatus { State = "waiting" }),
            new TrayController.SessionEntry("h", new SessionStatus { State = "waiting" }, Hidden: true),
        });
        Assert.Equal((1, 1, 0), counts);
    }

    [Fact]
    public void OnlyTheHiddenEntryCarriesTheMarker()
    {
        var s = new SessionStatus { State = "idle", Cwd = "/a/proj" };
        Assert.DoesNotContain("(hidden)", TrayController.SessionLabel(new TrayController.SessionEntry("v", s), false));
        Assert.EndsWith("(hidden)", TrayController.SessionLabel(new TrayController.SessionEntry("h", s, Hidden: true), false));
    }
}
