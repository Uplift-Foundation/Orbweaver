using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-314: the phone relay's one hook into the scan. A real SessionManager over
// hand-written status files, with every subprocess seam handed over (the same
// shape as ScanProbeScanTests), so this asserts what the relay client will
// actually be given: every pass raises SessionsScanned once, at the end, with
// what the screen now shows, and RelayRoster.Project turns that into the row a
// phone would receive.
[Collection("Settings")]
public class RelaySessionsScannedTests
{
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-relay-scan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(string sessionId, string state, string title) =>
            File.WriteAllText(Path.Combine(Dir, sessionId + ".txt"), System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = state,
                Cli = "",
                Title = title,
                Cwd = "/Users/someone/Source/proj",
                SessionPid = Environment.ProcessId,
                // A terminal, as ScanProbeScanTests' visible session has: a live
                // pid with no terminal reads as a background husk and is not drawn.
                TermProgram = "iTerm.app",
                Tty = "/dev/ttys004",
            }));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Manager(Scratch scratch)
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        return new SessionManager(
            scratch.Dir,
            () => new Dictionary<string, string>(StringComparer.Ordinal),
            () => new HashSet<string>(StringComparer.Ordinal),
            dependents: _ => SessionDependents.Nothing,
            paneOwners: claims => claims.ToDictionary(TmuxPaneKey.Of, _ => (string?)null),
            agentViewer: _ => null,
            teams: pids => pids.ToDictionary(p => p, _ => AgentTeam.None),
            fileTeams: _ => new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public void Every_scan_raises_SessionsScanned_once_with_what_it_shows()
    {
        using var scratch = new Scratch();
        scratch.Write("relay-a", "generating", "First chat");
        var manager = Manager(scratch);

        var raised = new List<IReadOnlyList<(string SessionId, SessionStatus Status)>>();
        manager.SessionsScanned += snapshot => raised.Add(snapshot);

        manager.ScanAndUpdate();
        var first = Assert.Single(raised);
        var (id, status) = Assert.Single(first);
        Assert.Equal("relay-a", id);
        Assert.Equal("generating", status.State);

        var row = Assert.Single(RelayRoster.Project(first));
        Assert.Equal(new RelaySession("relay-a", "claude", "First chat", "/Users/someone/Source/proj", "generating", null, null), row);

        // A second pass, after the session changed on disk, raises again with
        // the new state, as a new list rather than the one handed out before.
        // (One session only: every fixture shares this process's pid, and the
        // scan rightly keeps only the newest status file per pid.)
        scratch.Write("relay-a", "waiting", "Renamed chat");
        manager.ScanAndUpdate();
        Assert.Equal(2, raised.Count);
        Assert.NotSame(raised[0], raised[1]);
        var second = Assert.Single(raised[1]);
        Assert.Equal("waiting", second.Status.State);
        Assert.Equal("Renamed chat", RelayRoster.Project(raised[1]).Single().Title);
    }
}
