using Xunit;

namespace Orbweaver.Tests;

// CB-300: a session under a Claude Code profile added after the relay was
// deleted was never offered over the peer link, and left no trace.
//
// Real settings.json on disk, the real RemoteMirrorServer, the real client and
// the real protocol between them; the only fakes are the `claude agents`
// reader (a subprocess) and the status files' liveness, kept out by giving the
// sessions no transcript on this disk.
[Collection("Settings")]
public class PeerRosterProfilesTests : IDisposable
{
    private const string Far = "mini";
    private const string Near = "laptop";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb300-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousSettingsDir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    private string Home => Path.Combine(_root, "home");

    public PeerRosterProfilesTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", Path.Combine(_root, "settings"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _previousSettingsDir);
        OrbweaverSettings.ReloadForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteSettings(string json)
    {
        File.WriteAllText(Path.Combine(_root, "settings", "settings.json"), json);
        OrbweaverSettings.ReloadForTests();
    }

    // --- the settings seam ----------------------------------------------------

    // The mini's literal two keys on 2026-10-30.
    private const string MiniSettings =
        "{\"claudeCodeProfileDirs\":[\".claude\",\".claude-board\",\".claude-loginid\"],"
        + "\"remoteControlProfileDirs\":[\".claude\",\".claude-board\"]}";

    [Fact]
    public void TheMinisSettingsFileNamesTheLoginidProfile()
    {
        WriteSettings(MiniSettings);

        Assert.Equal(
            new[] { ".claude", ".claude-board", ".claude-loginid" },
            PeerRosterDirs.Configured(Home));
    }

    [Fact]
    public void TheSameFileWithoutTheProfilesKeyDoesNot()
    {
        WriteSettings("{\"remoteControlProfileDirs\":[\".claude\",\".claude-board\"]}");

        Assert.DoesNotContain(".claude-loginid", PeerRosterDirs.Configured(Home));
        Assert.Equal(new[] { ".claude", ".claude-board" }, PeerRosterDirs.Configured(Home));
    }

    // --- a hello through the real wiring ----------------------------------------

    private sealed class Wire
    {
        public RemoteMirrorServer Server { get; }
        public RemoteMirrorClient Client { get; }

        public Wire(string home, Func<IReadOnlyList<string>> dirs, IReadOnlyList<(string, SessionStatus)> sessions)
        {
            // The agent roster knows only the sessions of the one dir that holds
            // them, which is what a real `claude agents` does per config dir.
            AgentRoster.ReadResult Read(string dir) =>
                new(dir == ".claude-loginid"
                    ? sessions.Select(s => new AgentRoster.Entry("job-" + s.Item1, s.Item1, 0)).ToList()
                    : Array.Empty<AgentRoster.Entry>(), null);

            // Only Agents and RosterSnapshot are the real wiring under test; every
            // other seam is replaced so nothing here reaches tmux or a socket.
            var seams = RemoteMirrorServer.AllAccountSeams(
                dirs,
                (_, line) => Deliver(line),
                () => sessions,
                Read,
                () => new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                new RosterReadLog((_, _) => { }))
                with
                {
                    ReplyEnabled = _ => false,
                    CanType = _ => false,
                    TypeInto = (_, _) => Task.FromResult(false),
                    CanDeliver = null,
                    Deliver = null,
                    PeerAllowed = _ => true,
                    UserConfigDirs = () => Array.Empty<string>()
                };

            Server = new RemoteMirrorServer("acct", seams) { Home = home };
            Server.Now = () => new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            Client = new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(
                async (_, line) =>
                {
                    var frame = MirrorProtocol.TryParseFrame(line);
                    if (frame is null) return false;
                    await Server.HandleAsync(Near, frame);
                    return true;
                }));
        }

        private Task<bool> Deliver(string line)
        {
            var frame = MirrorProtocol.TryParseFrame(line);
            return frame is null ? Task.FromResult(false) : DeliverAsync(frame);
        }

        private async Task<bool> DeliverAsync(MirrorProtocol.MirrorFrame frame)
        {
            await Client.OnFrameAsync(Far, frame);
            return true;
        }

        public async Task<int> AskAsync()
        {
            await Client.AskWhatTheyHaveAsync(new[] { Far });
            return Client.Known().Count;
        }
    }

    private List<(string, SessionStatus)> LoginidSessions(int n) =>
        Enumerable.Range(0, n).Select(i =>
        {
            var id = $"s{i}";
            var transcript = Path.Combine(Home, ".claude-loginid", "projects", "slug", id + ".jsonl");

            // A real file, because the client only keeps a roster entry whose
            // transcript exists on the far disk, and a turn a minute before
            // the server's pinned clock so the session reads as live.
            Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
            File.WriteAllText(transcript,
                "{\"type\":\"user\",\"uuid\":\"u" + i + "\",\"timestamp\":\"2026-10-30T11:59:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");

            return (id, new SessionStatus
            {
                Title = "job-" + id,
                Cwd = Home,
                Source = SessionSource.ClaudeCode,
                TmuxPane = "%1",
                SessionPid = 5000 + i,
                TranscriptPath = transcript
            });
        }).ToList();

    [Fact]
    public async Task ASessionUnderAProfileAddedInSettingsReachesThePeerWithNoHandEdit()
    {
        WriteSettings(MiniSettings);
        var wire = new Wire(Home, () => PeerRosterDirs.Configured(Home), LoginidSessions(7));

        Assert.Equal(7, await wire.AskAsync());
        Assert.Empty(wire.Server.LastRosterGaps);
    }

    // The reproduction as a test: the same fixture with the old RC-only list.
    [Fact]
    public async Task TheOldListGivesAnEmptyRosterAndAGapThatSaysWhy()
    {
        var wire = new Wire(Home, () => new[] { ".claude", ".claude-board" }, LoginidSessions(7));

        Assert.Equal(0, await wire.AskAsync());

        var gap = Assert.Single(wire.Server.LastRosterGaps);
        Assert.Equal("~/.claude-loginid", gap.Root);
        Assert.Equal(7, gap.Sessions);
        Assert.False(gap.Asked);
        Assert.True(gap.Actionable);
    }

    // No new Serve, no restart: the next hello sees the profile.
    [Fact]
    public async Task AProfileAddedWhileRunningIsOfferedByTheNextHello()
    {
        WriteSettings("{\"remoteControlProfileDirs\":[\".claude\",\".claude-board\"]}");
        var wire = new Wire(Home, () => PeerRosterDirs.Configured(Home), LoginidSessions(4));

        Assert.Equal(0, await wire.AskAsync());
        Assert.False(Assert.Single(wire.Server.LastRosterGaps).Asked);

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-loginid");

        Assert.Equal(4, await wire.AskAsync());
        Assert.Empty(wire.Server.LastRosterGaps);
    }

    // A root outside home is logged (non-actionable gaps are log-only) but by
    // kind, never by a path that could carry a user name.
    [Fact]
    public async Task AGapOutsideHomeIsLoggedWithoutItsPath()
    {
        var wsl = (Guid.NewGuid().ToString("N"), new SessionStatus
        {
            Title = "w", Cwd = Home, Source = SessionSource.ClaudeCode, TmuxPane = "%1",
            TranscriptPath = @"\\wsl.localhost\Ubuntu\home\bob\.claude\projects\s\x.jsonl"
        });
        var other = (Guid.NewGuid().ToString("N"), new SessionStatus
        {
            Title = "o", Cwd = Home, Source = SessionSource.ClaudeCode, TmuxPane = "%1",
            TranscriptPath = @"C:\other\bob\.claude\projects\s\y.jsonl"
        });

        var wire = new Wire(Home, () => new[] { ".claude" }, new List<(string, SessionStatus)> { wsl, other });
        var said = new List<string>();
        wire.Server.GapLog = (what, detail) => said.Add(what + " " + detail);

        await wire.AskAsync();

        var line = Assert.Single(said);
        Assert.Equal("roster-gap:(outside home) root=(outside home) sessions=2 asked=no", line);
        Assert.DoesNotContain("bob", line);
        Assert.DoesNotContain(Home, line);
        Assert.False(Assert.Single(wire.Server.LastRosterGaps.Where(g => g.Root.Contains("wsl"))).Actionable);
    }

    // The log line for the CB-300 case itself is home-relative.
    [Fact]
    public async Task TheActionableGapIsLoggedHomeRelative()
    {
        var wire = new Wire(Home, () => new[] { ".claude" }, LoginidSessions(3));
        var said = new List<string>();
        wire.Server.GapLog = (what, detail) => said.Add(what + " " + detail);

        await wire.AskAsync();

        Assert.Equal(new[] { "roster-gap:~/.claude-loginid root=~/.claude-loginid sessions=3 asked=no" }, said);
        Assert.DoesNotContain(Home, said[0]);
    }
}
