using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-276 acceptance tests for what a hidden session looks like to the two
// readers that still see it: the phone (through the real RelayService.Project,
// the call OnScanned makes) and the tray (which is built from _hidden).
//
// The hide used to leave the scan loop before ApplyPersona and before the
// ClaudeCode shape/kind/presence block, so both readers got a raw status: no
// persona on the phone, and a background job reading as a terminal session.
// Every case compares the hidden session with its own visible self, scanned by
// a fresh manager on the same files, so "the same" is a measurement.
[Collection("Settings")]
public class HiddenOrbsPhoneTrayTests : IDisposable
{
    private static readonly int LivePid = Environment.ProcessId;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-hiddenphone-" + Guid.NewGuid());
    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), "cb-hiddenphone-settings-" + Guid.NewGuid());
    private readonly List<string> _ids = new();

    public HiddenOrbsPhoneTrayTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_settingsDir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _settingsDir);
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.ClaudeCodeEnabled = true;
    }

    private readonly Func<SessionStatus, IReadOnlyList<string>, string?> _personaName = RelayService.PersonaName;

    public void Dispose()
    {
        RelayService.PersonaName = _personaName;
        foreach (var id in _ids) LocalPersonas.Forget(id);
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_settingsDir, recursive: true); } catch { }
    }

    // A job's file names no pid and no terminal, which is what makes the scan
    // worth asking the daemon about it (SessionPresence's gate); a terminal
    // session's names both, so nothing here ever shells out.
    private string WriteSession(string cwd, bool job = false)
    {
        var id = "hidden-phone-" + Guid.NewGuid();
        _ids.Add(id);
        File.WriteAllText(Path.Combine(_dir, id + ".txt"), JsonSerializer.Serialize(job
            ? new SessionStatus { State = "idle", Cwd = cwd }
            : new SessionStatus { State = "idle", Cwd = cwd, SessionPid = LivePid, TermProgram = "iTerm.app", Tty = "/dev/ttys004" }));
        return id;
    }

    // Built through the same internal seam ScanProbeScanTests uses, so nothing
    // here asks the real daemon, tmux or the machine's own agent teams.
    private SessionManager Manager(Dictionary<string, string>? jobs = null) =>
        new(_dir,
            () => jobs ?? new Dictionary<string, string>(StringComparer.Ordinal),
            attachClients: () => new HashSet<string>(StringComparer.Ordinal),
            userConfigDirs: () => Array.Empty<string>(),
            dependents: _ => SessionDependents.Nothing,
            paneOwners: claims => claims.ToDictionary(TmuxPaneKey.Of, _ => (string?)null),
            agentViewer: _ => null,
            teams: pids => pids.ToDictionary(p => p, _ => AgentTeam.None),
            fileTeams: _ => new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));

    private static IReadOnlyList<(string SessionId, SessionStatus Status)> Scan(SessionManager manager)
    {
        IReadOnlyList<(string SessionId, SessionStatus Status)>? heard = null;
        manager.SessionsScanned += list => heard = list;
        manager.ScanAndUpdate();
        Assert.NotNull(heard);
        return heard!;
    }

    private static List<string> OrbIds(SessionManager manager) =>
        ((System.Collections.IDictionary)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!)
            .Keys.Cast<string>().ToList();

    private static List<(string SessionId, SessionStatus Status)> HiddenEntries(SessionManager manager) =>
        (List<(string SessionId, SessionStatus Status)>)typeof(SessionManager)
            .GetField("_hidden", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;

    private RelaySession PhoneRow(IReadOnlyList<(string SessionId, SessionStatus Status)> scanned, string id) =>
        RelayService.Project(scanned).Rows.Single(r => r.SessionId == id);

    [AvaloniaFact]
    public void ASessionHiddenAtStartupReachesThePhoneWithTheSamePersonaAsItsVisibleSelf()
    {
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "# Notes\n\nHer name is Leota.\n");
        var id = WriteSession(project);

        // The phone's name comes from PeerPersonaCache.Peek, which nothing in a
        // scan warms. This answers what a warm cache would, from the registry
        // the scan fills, so the row can only name her if the scan resolved her
        // for this session, hidden or not. The id and picture come from the
        // registry in production too.
        RelayService.PersonaName = (status, _) => status.Cwd == project ? LocalPersonas.For(id)?.Name : null;

        // Visible: the control, so a persona-less row below cannot be this
        // fixture failing to produce one.
        var visible = Manager();
        var visibleRow = PhoneRow(Scan(visible), id);
        Assert.Equal("Leota", visibleRow.Persona?.Name);
        Assert.Contains(id, OrbIds(visible));

        // Hidden, then a restart: a fresh manager, an empty persona registry.
        OrbweaverSettings.HideOrb(new HiddenOrb(id, DateTime.UtcNow, "t", "Claude Code", ""));
        LocalPersonas.Forget(id);
        var restarted = Manager();
        var scanned = Scan(restarted);
        Assert.DoesNotContain(id, OrbIds(restarted));

        var hiddenRow = PhoneRow(scanned, id);
        Assert.Equal(visibleRow.Persona?.Name, hiddenRow.Persona?.Name);
        Assert.Equal(visibleRow.Persona?.Initials, hiddenRow.Persona?.Initials);
        Assert.Equal(visibleRow.Persona?.Id, hiddenRow.Persona?.Id);
        Assert.Equal(visibleRow.Title, hiddenRow.Title);
        Assert.Equal(visibleRow.Cwd, hiddenRow.Cwd);
    }

    [AvaloniaFact]
    public void AHiddenBackgroundJobKeepsItsKindAndShapeForThePhoneAndTheTray()
    {
        var id = WriteSession("/Users/user/project", job: true);
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal) { [id] = "working" };

        // Control: visible, the scan marks it a background job.
        var visible = Manager(jobs);
        var visibleScan = Scan(visible);
        var visibleStatus = visible.StatusFor(id)!;
        Assert.Equal(SessionKind.Background, visibleStatus.Kind);
        Assert.Equal(LocalSessionShape.Background, visibleStatus.Shape);

        // Hidden from the first scan of a fresh manager.
        OrbweaverSettings.HideOrb(new HiddenOrb(id, DateTime.UtcNow, "t", "Background job", ""));
        var restarted = Manager(jobs);
        var scanned = Scan(restarted);
        Assert.DoesNotContain(id, OrbIds(restarted));

        // The tray is built from _hidden: its entry must be the full status.
        var tray = HiddenEntries(restarted).Single(h => h.SessionId == id).Status;
        Assert.Equal(SessionKind.Background, tray.Kind);
        Assert.Equal(LocalSessionShape.Background, tray.Shape);
        Assert.Equal(visibleStatus.Presence, tray.Presence);

        // The phone is handed the same object, and still projects the row.
        var handed = scanned.Single(s => s.SessionId == id).Status;
        Assert.Equal(SessionKind.Background, handed.Kind);
        var hiddenRow = PhoneRow(scanned, id);
        var visibleRow = PhoneRow(visibleScan, id);
        Assert.Equal(visibleRow.State, hiddenRow.State);
        Assert.Equal(visibleRow.ParentSessionId, hiddenRow.ParentSessionId);

        // And the Describe the Settings row was written from agrees.
        Assert.Equal("Background job", HiddenOrbs.SourceLabel(tray));
    }
}
