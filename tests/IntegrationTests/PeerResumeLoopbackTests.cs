using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-434: list and resume a past session across two real hosts over loopback
// TLS, through the production wiring (PeerSessions.ServeLaunchOn), the real
// signer, the real gate and the real runner. Three things are pinned and
// nothing else: the far desk's index (PastSessionIndex.LoadForTests), its
// CLI's resume support (ResumeSupport.ProbeForTests) and the start itself
// (LaunchRunner.StartForTests), which records the command it was handed and
// refuses it — so a resume that passes every check reaches the terminal seam
// with the exact command line, and no terminal is ever opened.
[Collection("Settings")]
public class PeerResumeLoopbackTests : IDisposable
{
    private const string Id = "0f5b1c2e-6a8d-4c11-9d3e-2b7a5f8e1c40";

    private readonly List<PeerMirrorHost> _hosts = new();
    private readonly bool _saved = OrbweaverSettings.PeerLaunchInboundEnabled;
    private readonly string _folder = Directory.CreateTempSubdirectory("cb434-").FullName;
    private readonly List<string> _starts = new();
    private readonly List<(NewChatCli Cli, string? Profile, bool Older)> _loads = new();
    private ResumeCheck _support = new(true, "");

    private static string Claude => OperatingSystem.IsWindows() ? "C:\\c\\claude.exe" : "/bin/claude";

    public PeerResumeLoopbackTests()
    {
        NewChatLauncher.LocateFreshForTests = cli => cli == NewChatCli.ClaudeCode ? Claude : null;
        ResumeSupport.ProbeForTests = (_, _) => _support;
        PastSessionIndex.LoadForTests = (cli, profile, older, now) =>
        {
            lock (_loads) _loads.Add((cli, profile, older));
            return cli != NewChatCli.ClaudeCode ? [] :
            [
                new PastSession(cli, Id, _folder, "Fix the feed", now.AddHours(-1), null, "/far/secret/" + Id + ".jsonl", false),
                new PastSession(cli, "11111111-2222-3333-4444-555555555555", _folder + "-gone", "Gone", now.AddHours(-2), null, "/far/x.jsonl", true),
            ];
        };
        LaunchRunner.StartForTests = new LaunchRunner.StartOverride(
            (command, _) => { lock (_starts) _starts.Add(command); return (null, "refused by the test"); },
            psi => { lock (_starts) _starts.Add(string.Join(" ", psi.ArgumentList)); return null; });
    }

    public void Dispose()
    {
        NewChatLauncher.LocateFreshForTests = null;
        ResumeSupport.ProbeForTests = null;
        PastSessionIndex.LoadForTests = null;
        LaunchRunner.StartForTests = null;
        OrbweaverSettings.PeerLaunchInboundEnabled = _saved;
        foreach (var host in _hosts) host.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private async Task<PeerLaunchClient> Pair()
    {
        var receiver = new PeerMirrorHost();
        var sender = new PeerMirrorHost();
        _hosts.Add(receiver);
        _hosts.Add(sender);

        var client = new PeerLaunchClient(new PeerLaunchClient.Seams(
            sender.Link.SendAsync,
            () => [new PeerMachine("receiver", true)],
            PeerLaunchClient.SignerFor(m => PeerIdentity.PeerFor(m)?.Pin, message => PeerIdentity.SignAsync(message)),
            () => DateTimeOffset.UtcNow)
        {
            InfoWait = TimeSpan.FromSeconds(10),
            LaunchWait = TimeSpan.FromSeconds(20),
            ListWait = TimeSpan.FromSeconds(10),
        });

        PeerSessions.ServeLaunchOn(receiver, null);
        sender.ServeLaunch(null, null, client);

        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "receiver"));
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), MachineNames.Mine()));

        receiver.Link.Listen(0);
        Assert.True(await sender.Link.ConnectAsync("receiver", "127.0.0.1", receiver.Link.BoundPort,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        return client;
    }

    [Fact]
    public async Task The_far_desk_advertises_resume_and_lists_its_own_sessions_without_their_paths()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        var client = await Pair();

        Assert.Equal(PastSessionsRemote.ResumeVersion, (await client.InfoAsync("receiver", CancellationToken.None))!.Resume);

        var list = await client.ListAsync("receiver", NewChatCli.ClaudeCode, RelayAccounts.Default, false, CancellationToken.None);

        Assert.Equal(RemoteListOutcome.Ok, list.Outcome);
        Assert.Equal([Id, "11111111-2222-3333-4444-555555555555"], list.Rows.Select(r => r.Session.Id));
        Assert.Equal(2, list.Total);
        Assert.Equal(_folder, list.Rows[0].Session.Cwd);
        Assert.False(list.Rows[0].Session.FolderMissing);
        Assert.True(list.Rows[1].Session.FolderMissing);
        Assert.All(list.Rows, r => Assert.Equal("", r.Session.TranscriptPath));
        Assert.Contains((NewChatCli.ClaudeCode, (string?)null, false), _loads);
    }

    // The switch is the control for every other case: the same request, off,
    // reads nothing at all.
    [Fact]
    public async Task With_the_switch_off_nothing_is_read_and_the_window_is_told_why()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = false;
        var client = await Pair();

        var list = await client.ListAsync("receiver", NewChatCli.ClaudeCode, RelayAccounts.Default, false, CancellationToken.None);

        Assert.Equal(RemoteListOutcome.Unreachable, list.Outcome);
        Assert.Equal(PeerLaunchClient.ListWhy(PeerLaunchCodes.PeerLaunchOff), list.Why);
        Assert.Empty(_loads);
        Assert.Equal(0, (await client.InfoAsync("receiver", CancellationToken.None))!.Resume);
    }

    [Fact]
    public async Task A_resume_passes_the_real_gate_and_reaches_the_start_with_the_resume_command_in_its_folder()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        var client = await Pair();

        var result = await client.ResumeAsync("receiver", NewChatCli.ClaudeCode, Id, _folder, RelayAccounts.Default, CancellationToken.None);

        // The start seam refused, so the far desk reports a failed start: the
        // signature, the gate, the index, the folder and the version gate all
        // passed to get here.
        Assert.Equal(RemoteResumeOutcome.Failed, result.Outcome);
        var command = Assert.Single(_starts);
        Assert.Contains("--resume", command);
        Assert.Contains(Id, command);
    }

    [Fact]
    public async Task A_resume_of_an_id_the_far_desk_does_not_have_is_refused_and_starts_nothing()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        var client = await Pair();

        var result = await client.ResumeAsync("receiver", NewChatCli.ClaudeCode,
            "99999999-2222-3333-4444-555555555555", _folder, RelayAccounts.Default, CancellationToken.None);

        Assert.Equal(RemoteResumeOutcome.Failed, result.Outcome);
        Assert.Contains("no longer has that session", result.Message);
        Assert.Empty(_starts);
    }

    [Fact]
    public async Task A_resume_in_a_folder_other_than_the_one_recorded_is_refused_and_starts_nothing()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        var client = await Pair();
        var elsewhere = Directory.CreateTempSubdirectory("cb434-other-").FullName;
        try
        {
            var result = await client.ResumeAsync("receiver", NewChatCli.ClaudeCode, Id, elsewhere, RelayAccounts.Default, CancellationToken.None);

            Assert.Equal(RemoteResumeOutcome.Failed, result.Outcome);
            Assert.Empty(_starts);
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    [Fact]
    public async Task A_far_cli_too_old_to_resume_is_refused_with_its_own_sentence_and_starts_nothing()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        _support = new(false, "Claude Code 0.2 can't resume. Update it.");
        var client = await Pair();

        var result = await client.ResumeAsync("receiver", NewChatCli.ClaudeCode, Id, _folder, RelayAccounts.Default, CancellationToken.None);

        Assert.Equal(new RemoteResumeResult(RemoteResumeOutcome.Unsupported, "Claude Code 0.2 can't resume. Update it.", false), result);
        Assert.Empty(_starts);
    }
}
