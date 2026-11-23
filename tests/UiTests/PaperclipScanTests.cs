using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// A scan with Paperclip agents in it, and what it does around them (CB-421).
//
// The same harness as CloudScanTests: SessionManager's internal constructor
// takes a scratch status directory, Start() is never called, and the snapshot
// arrives through PaperclipSessions.SetSnapshotForTests, because the only thing
// that publishes one in production is the poll loop, which needs a real
// Paperclip and is excluded from coverage for that reason.
//
// Nothing here calls a Paperclip or opens a browser. No clicks either: an orb
// click reaches TerminalFocuser, which runs real tmux/ps/osascript processes.
[Collection("Settings")]
public class PaperclipScanTests : IDisposable
{
    private static DateTime Now => DateTime.UtcNow;

    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly string _sleeping = OrbweaverSettings.PaperclipSleeping;
    private readonly bool _claudeCode = OrbweaverSettings.ClaudeCodeEnabled;
    private readonly int _lifetime = OrbweaverSettings.OrbLifetimeMinutes;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb421-scan-" + Guid.NewGuid());

    public PaperclipScanTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        PaperclipSessions.SetSnapshotForTests([]);
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
        OrbweaverSettings.PaperclipEnabled = _enabled;
        OrbweaverSettings.PaperclipSleeping = _sleeping;
        OrbweaverSettings.ClaudeCodeEnabled = _claudeCode;
        OrbweaverSettings.OrbLifetimeMinutes = _lifetime;
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static Dictionary<string, OrbWindow> Orbs(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private SessionManager Manager() =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { _scratch });

    private static PaperclipAgent A(string id, string? lead = null, string state = "idle", bool? sleeping = null,
        PaperclipMark mark = PaperclipMark.None, DateTime? activity = null, int? pid = null, string? session = null,
        IReadOnlyList<PaperclipNeed>? needs = null) =>
        new(id, "Agent " + id, "r", "t", "idle", lead, "paperclip_runner", state, mark, null, needs ?? [], null, null, null, null, null,
            activity ?? Now.AddSeconds(-5), "http://127.0.0.1:3100/EX/agents/" + id,
            sleeping ?? (state == "idle" && mark == PaperclipMark.None && (needs?.Count ?? 0) == 0), pid, session);

    private static void Publish(PaperclipLink link = PaperclipLink.Connected, params PaperclipAgent[] agents)
    {
        OrbweaverSettings.PaperclipEnabled = true;
        PaperclipSessions.SetLinkForTests(link, link == PaperclipLink.NeedsReconnect ? "Reconnect to Paperclip" : "Connected");
        PaperclipSessions.SetSnapshotForTests(agents);
    }

    private static PaperclipAgent[] Team() => [A("lead"), A("x", "lead"), A("y", "lead", state: "generating")];

    [AvaloniaFact]
    public void ALeadAndTwoReports_ThreeOrbs_WithTheRightKeysAndLeads()
    {
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();

        var orbs = Orbs(m);
        Assert.Equal(new[] { "paperclip:lead", "paperclip:x", "paperclip:y" }, orbs.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("", orbs["paperclip:lead"].LastStatus!.Lead);
        Assert.Equal("paperclip:lead", orbs["paperclip:x"].LastStatus!.Lead);
        Assert.Equal("paperclip:lead", orbs["paperclip:y"].LastStatus!.Lead);
        Assert.All(orbs.Values, o => Assert.Equal(SessionSource.Paperclip, o.LastStatus!.Source));
    }

    [AvaloniaFact]
    public void StateChangesInPlace_TheSameOrbUpdates()
    {
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();
        var before = Orbs(m)["paperclip:x"];
        Assert.Equal("idle", before.LastStatus!.State);

        Publish(PaperclipLink.Connected, A("lead"), A("x", "lead", state: "generating"), A("y", "lead", state: "generating"));
        m.ScanAndUpdate();

        Assert.Same(before, Orbs(m)["paperclip:x"]);
        Assert.Equal("generating", Orbs(m)["paperclip:x"].LastStatus!.State);
    }

    [AvaloniaFact]
    public void ATerminatedAgentsOrbGoes_WhenItLeavesTheListing()
    {
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();
        Assert.Equal(3, Orbs(m).Count);

        Publish(PaperclipLink.Connected, A("lead"), A("y", "lead", state: "generating"));
        m.ScanAndUpdate();
        Assert.DoesNotContain("paperclip:x", Orbs(m).Keys);
    }

    [AvaloniaFact]
    public void SwitchingOff_RemovesEveryOrb()
    {
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();
        Assert.Equal(3, Orbs(m).Count);

        // Switching off clears the snapshot (Restart does it in production).
        PaperclipSessions.SetSnapshotForTests([]);
        OrbweaverSettings.PaperclipEnabled = false;
        m.ScanAndUpdate();
        Assert.Empty(Orbs(m));
    }

    // The stale timer is a rule about local sessions whose process is probably
    // gone. An agent three hours asleep is the normal state of a company.
    [AvaloniaFact]
    public void AnAgentThreeHoursAsleep_SurvivesTheStaleTimer()
    {
        OrbweaverSettings.OrbLifetimeMinutes = 1;
        Publish(PaperclipLink.Connected, A("lead", activity: Now.AddHours(-3)), A("x", "lead", activity: Now.AddHours(-3)));
        var m = Manager();
        m.ScanAndUpdate();
        Assert.Equal(2, Orbs(m).Count);
    }

    [AvaloniaTheory]
    [InlineData("NeedsReconnect")]
    [InlineData("Unreachable")]
    public void ALinkThatIsGone_DimsAndKeepsEveryOrb_AndSaysWhy(string link)
    {
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();

        Publish(Enum.Parse<PaperclipLink>(link), Team());
        m.ScanAndUpdate();

        Assert.Equal(3, Orbs(m).Count);
        Assert.All(Orbs(m).Values, o =>
        {
            Assert.Equal(OrbPresence.Parked, o.LastStatus!.Presence);
            Assert.Equal("idle", o.LastStatus.State);
            Assert.NotEmpty(o.LastStatus.HoverLines);
        });
        Assert.Equal(link == "NeedsReconnect" ? "Reconnect to Paperclip" : "Connected", Assert.Single(Orbs(m)["paperclip:x"].LastStatus!.HoverLines));
    }

    [AvaloniaFact]
    public void TenAgents_Fold_TheCountSitsOnTheLead()
    {
        var agents = new List<PaperclipAgent> { A("lead") };
        for (var i = 1; i < 10; i++) agents.Add(A("m" + i, "lead"));
        Publish(PaperclipLink.Connected, agents.ToArray());
        var m = Manager();
        m.ScanAndUpdate();

        var orb = Assert.Single(Orbs(m)).Value;
        Assert.Equal(9, orb.LastStatus!.FoldedCount);

        OrbweaverSettings.PaperclipSleeping = "show";
        m.ScanAndUpdate();
        Assert.Equal(10, Orbs(m).Count);
    }

    [AvaloniaFact]
    public void AnAmberAgent_IsNeedsInput_AndItsUrlIsThePageThatAnswersIt()
    {
        var need = new PaperclipNeed("approval", "p1", "Hire QA", "http://127.0.0.1:3100/EX/approvals/p1");
        Publish(PaperclipLink.Connected, A("lead"), A("x", "lead", state: "waiting", needs: [need]));
        var m = Manager();
        m.ScanAndUpdate();

        var s = Orbs(m)["paperclip:x"].LastStatus!;
        Assert.Equal(OrbPresence.NeedsInput, s.Presence);
        Assert.Equal("http://127.0.0.1:3100/EX/approvals/p1", s.Url);
        Assert.True(ClickRouting.OpensInABrowser(s));
        Assert.Equal(new[] { "Hire QA" }, s.HoverLines.ToArray());
    }

    [AvaloniaFact]
    public void APaperclipOrbNeverBuildsAChatSession()
    {
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();
        Assert.Null(m.RemoteChatFor("paperclip:x"));
    }

    // ---- one orb per agent ----

    private string LocalRow(string name, string entrypoint, string cwd, int pid, string sessionId = "")
    {
        var path = Path.Combine(_scratch, name + ".txt");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
        {
            State = "idle", Entrypoint = entrypoint, Cwd = cwd, SessionPid = pid, SessionId = sessionId,
            TermProgram = "iTerm.app", Tty = "/dev/ttys004",
        }));
        return name;
    }

    private const string RunCwd = "/Users/u/.paperclip/instances/default/isolated-workspaces/co/uuid";

    [AvaloniaFact]
    public void AMatchingSdkRow_IsNotDrawnWhileConnected_AndIsDrawnWithPaperclipOff()
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        LocalRow("run-row", "sdk-ts", RunCwd, Environment.ProcessId);
        Publish(PaperclipLink.Connected, Team());

        var m = Manager();
        m.ScanAndUpdate();
        Assert.DoesNotContain("run-row", Orbs(m).Keys);
        Assert.Contains("paperclip:x", Orbs(m).Keys);

        // The paired control: the same row, the same cwd, Paperclip off.
        PaperclipSessions.SetSnapshotForTests([]);
        OrbweaverSettings.PaperclipEnabled = false;
        var off = Manager();
        off.ScanAndUpdate();
        Assert.Contains("run-row", Orbs(off).Keys);
    }

    // Two rows with one pid would be the scan's own supersede rule at work and
    // prove nothing about Paperclip, so each is its own scan.
    [AvaloniaTheory]
    [InlineData("cli", "/Users/u/src/repo")]        // a person's terminal
    [InlineData("sdk-cli", "/Users/u/src/repo")]    // headless Claude that is not Paperclip's
    [InlineData("cli", RunCwd)]                      // a person's terminal inside a workspace
    public void AnOrdinarySession_IsStillDrawn_BesideTheCompany(string entrypoint, string cwd)
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        LocalRow("mine", entrypoint, cwd, Environment.ProcessId);
        Publish(PaperclipLink.Connected, Team());
        var m = Manager();
        m.ScanAndUpdate();
        Assert.Contains("mine", Orbs(m).Keys);
        Assert.Contains("paperclip:x", Orbs(m).Keys);
    }

    [AvaloniaFact]
    public void FoldAway_IsDecidedByTheSettingAndTheSnapshot_NotByTheRowAlone()
    {
        var row = new SessionStatus { Source = SessionSource.ClaudeCode, Entrypoint = "sdk-cli", Cwd = "/repo", SessionPid = 4242, SessionId = "s1" };

        OrbweaverSettings.PaperclipEnabled = false;
        PaperclipSessions.SetSnapshotForTests([A("a", pid: 4242)]);
        Assert.False(SessionManager.PaperclipFoldAway(row));                    // off

        OrbweaverSettings.PaperclipEnabled = true;
        PaperclipSessions.SetSnapshotForTests([]);
        Assert.False(SessionManager.PaperclipFoldAway(row));                    // nothing drawn

        PaperclipSessions.SetSnapshotForTests([A("a", pid: 4242)]);
        Assert.True(SessionManager.PaperclipFoldAway(row));                     // a known run pid

        PaperclipSessions.SetSnapshotForTests([A("a", session: "s1")]);
        Assert.True(SessionManager.PaperclipFoldAway(row));                     // a known run session

        PaperclipSessions.SetSnapshotForTests([A("a")]);
        Assert.False(SessionManager.PaperclipFoldAway(row));                    // no match anywhere

        Assert.False(SessionManager.PaperclipFoldAway(new SessionStatus
        { Source = SessionSource.Codex, Entrypoint = "sdk-cli", Cwd = RunCwd }));   // not a Claude Code row
    }
}
