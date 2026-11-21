using Xunit;

namespace Orbweaver.Tests;

// CB-404: the client's half of a run frame, through a real RelayClient. Its
// remote sender hands a watch's run frame to the link; nothing connects here,
// so what is proven is the hand-off, not a delivery (RelayLinkTests seals it).
public class RelayClientRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cb404-client-").FullName;
    private RelayClient? _client;

    public void Dispose()
    {
        _client?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) => _items.TryGetValue(reference[4..], out var v) ? v : null;
        public void Forget(string reference) { }
    }

    private sealed class Run : IRunControls
    {
        public int Reads;
        public RunState? Read(SessionStatus status)
        {
            Reads++;
            return new(new RunFieldState("opus", null, [new("opus", "Opus")], null, null),
                new RunFieldState(null, null, [], null, null), false);
        }
        public bool IsRunning(SessionStatus status) => false;
        public Task<RunSetResult> SetAsync(SessionStatus status, string field, string value) => Task.FromResult(RunSetResult.Sent);
    }

    [Fact]
    public async Task A_watch_hands_its_run_frame_to_the_link_through_the_clients_sender()
    {
        var run = new Run();
        var seams = new RelayRemote.Seams(
            () => DateTimeOffset.UnixEpoch, _ => Task.CompletedTask,
            _ => new TranscriptWatcher.Seams(() => null, _ => null, _ => null), null!, null!, _ => { })
        {
            RunControls = run,
        };
        _client = new RelayClient(new RelayIdentity(Path.Combine(_dir, "id"), new MemoryStore()),
            new RelayClient.Options(new Uri("http://127.0.0.1:9"), "mac", "9.9.9", "Test Mac"),
            new WebSocketRelayTransport(), remoteSeams: seams);

        await _client.OnStatuses([("s1", new SessionStatus { SessionId = "s1", Source = SessionSource.ClaudeCode, Cwd = "/w" })]);
        await _client.Remote.HandleAsync("ph", [], new WatchRequest("r1", "s1", null));

        Assert.Equal(1, run.Reads);
        Assert.True(_client.Remote.IsWatching("ph", "s1"));
    }
}
