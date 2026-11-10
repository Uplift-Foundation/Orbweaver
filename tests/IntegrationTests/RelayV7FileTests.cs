using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323: the activity frame over a real loopback socket and the gate's
/// activity log as a real file. A real RelayClient against RelayStub: the
/// snapshot is the first thing it sends after hello, every frame it sends and
/// every reply it gets is one line in the file, a change inside the debounce
/// goes when the interval ends through the client's own timer, a stop while
/// that timer waits sends nothing, and an old relay's err unexpected costs
/// nothing but a log line.
/// </summary>
public class RelayV7FileTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb323-" + Guid.NewGuid().ToString("N")[..8]);
    private RelayStub _stub = null!;
    private RelayClient? _client;

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    private readonly MemoryStore _store = new();

    // A clock the debounce's wait moves, so the 2 s interval passes without
    // the test waiting 2 s of wall time. Every other wait — the five-minute
    // ping — is a real one, cancelled at stop.
    private DateTimeOffset _clock = DateTimeOffset.FromUnixTimeSeconds(1794111792);
    private readonly object _clockGate = new();
    private DateTimeOffset Now() { lock (_clockGate) return _clock; }
    private readonly List<TimeSpan> _shortWaits = new();
    private Func<TimeSpan, CancellationToken, Task>? _shortWait;

    private Task Delay(TimeSpan t, CancellationToken ct)
    {
        if (t > TimeSpan.FromSeconds(2)) return Task.Delay(t, ct);
        lock (_clockGate) _shortWaits.Add(t);
        if (_shortWait is { } custom) return custom(t, ct);
        lock (_clockGate) _clock += t;
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _stub = new RelayStub();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.StopAsync();
        _client?.Dispose();
        await _stub.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LogPath => Path.Combine(_dir, "activity.jsonl");

    private RelayClient Client(string? log = null)
    {
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            PingInterval = TimeSpan.FromMinutes(5),
            Silence = TimeSpan.FromMinutes(5),
            MinBackoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(100),
            ActivityLog = log is null ? null : new RelayActivityLog(log),
        };
        _client = new RelayClient(new RelayIdentity(Path.Combine(_dir, "id"), _store), options, new WebSocketRelayTransport(), now: Now, delay: Delay);
        return _client;
    }

    private static RelaySession Row(string id, string state) => new(id, "claude", "orbweaver", "/c", state, null, null);

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }

    // Read the way the gate's checker does — sharing the file with the app
    // writing it — never File.ReadAllLines, which on Windows locks the writer out.
    private static string[] Lines(string path)
    {
        if (!File.Exists(path)) return [];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task The_snapshot_follows_hello_and_the_log_has_one_line_per_frame_and_per_reply()
    {
        var client = Client(LogPath);
        client.OnScanned([Row("s1", "waiting"), Row("s2", "generating")]);    // in the book before any socket
        client.Start();
        await _stub.NextT("hello");
        var first = await _stub.Next(_ => true);
        Assert.Equal("activity", first.GetProperty("t").GetString());
        Assert.Equal((1, 1, 0, "Test Mac"), (first.GetProperty("amber").GetInt32(), first.GetProperty("working").GetInt32(),
            first.GetProperty("idle").GetInt32(), first.GetProperty("machine").GetString()));
        var reference = first.GetProperty("ref").GetString()!;
        Assert.Matches("^act-[0-9a-f]{16}-1$", reference);

        await _stub.Send($$"""{"t":"ok","ref":"{{reference}}","code":"activity"}""");
        await Until(() => Lines(LogPath).Length == 2);
        var lines = Lines(LogPath);
        Assert.Equal(first.GetRawText()[..^1] + ",\"at_ms\":" + Now().ToUnixTimeMilliseconds() + "}", lines[0]);
        Assert.Equal($$"""{"ref":"{{reference}}","code":"activity","at_ms":{{Now().ToUnixTimeMilliseconds()}}}""", lines[1]);
    }

    [Fact]
    public async Task A_change_inside_the_interval_goes_when_it_ends_with_the_state_then()
    {
        var client = Client(LogPath);
        client.Start();
        await _stub.NextT("activity");
        await Until(() => client.State == RelayClient.Status.Connected);

        client.OnScanned([Row("s1", "generating")]);    // inside 2 s of the welcome's send: deferred, flushed by the timer
        var flushed = await _stub.NextT("activity");
        Assert.Equal((2L, 1), (flushed.GetProperty("seq").GetInt64(), flushed.GetProperty("working").GetInt32()));
        Assert.Contains(TimeSpan.FromSeconds(2), _shortWaits);
        await Until(() => Lines(LogPath).Length == 2);              // both sends, no replies from the stub
        Assert.All(Lines(LogPath), l => Assert.StartsWith("{\"t\":\"activity\",", l));
    }

    [Fact]
    public async Task A_stop_while_the_debounce_waits_sends_nothing_more()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shortWait = async (t, ct) => { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var client = Client();
        client.Start();
        await _stub.NextT("activity");
        await Until(() => client.State == RelayClient.Status.Connected);
        client.OnScanned([Row("s1", "waiting")]);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.StopAsync();
        _client = null;
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _stub.NextT("activity", seconds: 1));
    }

    [Fact]
    public async Task An_old_relays_err_unexpected_costs_one_log_line_and_nothing_else()
    {
        var client = Client(LogPath);
        var logged = new List<string>();
        client.Logged += l => { lock (logged) logged.Add(l); };
        client.Start();
        var first = await _stub.NextT("activity");
        await _stub.Send($$"""{"t":"err","ref":"{{first.GetProperty("ref").GetString()}}","code":"unexpected","message":"activity"}""");
        await Until(() => { lock (logged) return logged.Any(l => l.Contains("err unexpected")); });
        Assert.Equal(RelayClient.Status.Connected, client.State);

        client.OnScanned([Row("s1", "waiting")]);
        Assert.Equal(1, (await _stub.NextT("activity")).GetProperty("amber").GetInt32());   // still sent, still connected
        await Until(() => Lines(LogPath).Count(l => l.Contains("\"code\":\"unexpected\"")) == 1);
    }

    [Fact]
    public async Task A_rate_limited_snapshot_goes_again_through_the_clients_timer_with_no_change_at_the_desk()
    {
        var client = Client(LogPath);
        client.OnScanned([Row("s1", "waiting")]);
        client.Start();
        var first = await _stub.NextT("activity");
        await _stub.Send($$"""{"t":"err","ref":"{{first.GetProperty("ref").GetString()}}","code":"rate_limited"}""");
        var again = await _stub.NextT("activity");                  // nothing scanned in between
        Assert.Equal((first.GetProperty("boot").GetString(), 2L, 1),
            (again.GetProperty("boot").GetString(), again.GetProperty("seq").GetInt64(), again.GetProperty("amber").GetInt32()));
        await Until(() => Lines(LogPath).Count(l => l.Contains("\"code\":\"rate_limited\"")) == 1);
    }

    // ---- the log on its own -------------------------------------------------------

    [Fact]
    public void The_log_is_on_only_when_the_variable_names_a_path()
    {
        Assert.Null(RelayActivityLog.From(_ => null));
        Assert.Null(RelayActivityLog.From(_ => ""));
        Assert.Equal(LogPath, RelayActivityLog.From(name => name == RelayActivityLog.EnvVar ? LogPath : null)!.Path);
    }

    [Fact]
    public void Only_an_activity_frame_is_an_activity_frame()
    {
        Assert.True(RelayActivityLog.IsActivityFrame("""{"t":"activity","ref":"act-x-1"}"""));
        Assert.False(RelayActivityLog.IsActivityFrame("""{"t":"ping"}"""));
        Assert.False(RelayActivityLog.IsActivityFrame("""{"v":2,"t":"activity"}"""));
    }

    [Fact]
    public void A_log_that_cannot_be_written_says_so_once()
    {
        var log = new RelayActivityLog(_dir);                        // a directory, not a file
        var failures = new List<string>();
        log.Failed += failures.Add;
        log.Sent("""{"t":"activity","seq":1}""", DateTimeOffset.UnixEpoch);
        log.Reply("act-x-1", "activity", DateTimeOffset.UnixEpoch);
        var line = Assert.Single(failures);
        Assert.StartsWith("activity log not written: ", line);
    }

    [Fact]
    public void Lines_append_to_a_real_file_in_order()
    {
        var log = new RelayActivityLog(LogPath);
        log.Sent("""{"t":"activity","seq":1}""", DateTimeOffset.FromUnixTimeMilliseconds(1794111792123));
        log.Reply("act-x-1", "bad_frame", DateTimeOffset.FromUnixTimeMilliseconds(1794111792200));
        Assert.Equal(
            [
                """{"t":"activity","seq":1,"at_ms":1794111792123}""",
                """{"ref":"act-x-1","code":"bad_frame","at_ms":1794111792200}""",
            ],
            Lines(LogPath));
        foreach (var l in Lines(LogPath)) JsonDocument.Parse(l);
    }

    // A stand-in for Windows' sharing violation, so the waiting path runs on
    // every OS: ERROR_SHARING_VIOLATION in HResult, as .NET reports it.
    private static IOException Sharing() => new("in use by another process", unchecked((int)0x80070020));

    private sealed class Blockable
    {
        internal bool Blocked = true;
        internal int Opens;
        internal readonly List<Action> Retries = new();
        internal Stream Open(string path)
        {
            Opens++;
            if (Blocked) throw Sharing();
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    [Fact]
    public void Lines_refused_for_sharing_wait_in_order_and_go_on_the_one_retry()
    {
        var b = new Blockable();
        var log = new RelayActivityLog(LogPath, b.Open, b.Retries.Add);
        var failures = new List<string>();
        log.Failed += failures.Add;
        log.Sent("""{"t":"activity","seq":1}""", DateTimeOffset.FromUnixTimeMilliseconds(1));
        log.Reply("act-x-1", "activity", DateTimeOffset.FromUnixTimeMilliseconds(2));
        Assert.Equal((2, 1), (log.PendingCount, b.Retries.Count));      // one retry, not one per line
        Assert.False(File.Exists(LogPath));

        b.Retries[0]();                                                  // still held: waits again
        Assert.Equal((2, 2), (log.PendingCount, b.Retries.Count));

        b.Blocked = false;
        b.Retries[1]();
        Assert.Equal(0, log.PendingCount);
        Assert.Equal(["""{"t":"activity","seq":1,"at_ms":1}""", """{"ref":"act-x-1","code":"activity","at_ms":2}"""], Lines(LogPath));
        Assert.Empty(failures);
    }

    [Fact]
    public void A_waiting_line_goes_with_the_next_one_that_gets_through_and_the_retry_then_has_nothing_to_do()
    {
        var b = new Blockable();
        var log = new RelayActivityLog(LogPath, b.Open, b.Retries.Add);
        log.Reply("act-x-1", "activity", DateTimeOffset.FromUnixTimeMilliseconds(1));
        b.Blocked = false;
        log.Reply("act-x-2", "activity", DateTimeOffset.FromUnixTimeMilliseconds(2));
        Assert.Equal(2, Lines(LogPath).Length);
        var opens = b.Opens;
        b.Retries.Single()();
        Assert.Equal(opens, b.Opens);                                   // nothing pending: no open at all
        Assert.Equal(2, Lines(LogPath).Length);
    }

    // The real lock, on the real file system: a reader that shares only for
    // reading. On Windows the line waits until the reader is gone and then
    // goes; elsewhere there is no mandatory lock and it goes at once. Either
    // way nothing is lost, and nothing here depends on how long anything takes.
    [Fact]
    public void A_reader_that_shares_only_for_reading_costs_no_line()
    {
        var retries = new List<Action>();
        var log = new RelayActivityLog(LogPath, later: retries.Add);
        var failures = new List<string>();
        log.Failed += failures.Add;
        log.Sent("""{"t":"activity","seq":1}""", DateTimeOffset.UnixEpoch);
        using (new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            log.Reply("act-x-1", "activity", DateTimeOffset.UnixEpoch);
            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, log.PendingCount);
        }
        foreach (var retry in retries.ToList()) retry();
        Assert.Equal(0, log.PendingCount);
        Assert.Equal(2, Lines(LogPath).Length);
        Assert.Empty(failures);
    }

    // The default retry is real: it fires on its own, off the caller's thread,
    // and the waiting line lands. Bounded by a deadline that fails a hang, not
    // a slow pass.
    [Fact]
    public async Task The_default_retry_fires_by_itself_and_the_line_lands()
    {
        var b = new Blockable();
        var log = new RelayActivityLog(LogPath, b.Open);
        log.Reply("act-x-1", "activity", DateTimeOffset.UnixEpoch);
        Assert.Equal(1, log.PendingCount);
        b.Blocked = false;
        await Until(() => log.PendingCount == 0);
        Assert.Single(Lines(LogPath));
    }

    [Fact]
    public void A_reader_that_never_lets_go_costs_the_oldest_lines_past_the_cap_and_says_so_once()
    {
        var b = new Blockable();
        var log = new RelayActivityLog(LogPath, b.Open, b.Retries.Add) { MaxPending = 3 };
        var failures = new List<string>();
        log.Failed += failures.Add;
        for (var i = 1; i <= 5; i++) log.Reply($"act-x-{i}", "activity", DateTimeOffset.FromUnixTimeMilliseconds(i));
        Assert.Equal(3, log.PendingCount);
        Assert.StartsWith("activity log behind a reader", Assert.Single(failures));
        b.Blocked = false;
        b.Retries[0]();
        Assert.Equal(["act-x-3", "act-x-4", "act-x-5"], Lines(LogPath).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("ref").GetString()));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]     // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021), true)]     // ERROR_LOCK_VIOLATION
    [InlineData(unchecked((int)0x80070050), false)]    // ERROR_FILE_EXISTS
    [InlineData(unchecked((int)0x80131620), false)]    // a plain COR_E_IO
    public void Only_a_sharing_or_lock_violation_is_worth_waiting_for(int hresult, bool expected) =>
        Assert.Equal(expected, RelayActivityLog.IsSharingViolation(new IOException("x", hresult)));

    [Fact]
    public void A_log_in_a_missing_directory_fails_at_once_without_retrying()
    {
        var retries = new List<Action>();
        var log = new RelayActivityLog(Path.Combine(_dir, "no-such-dir", "a.jsonl"), later: retries.Add);
        var failures = new List<string>();
        log.Failed += failures.Add;
        log.Reply("act-x-1", "activity", DateTimeOffset.UnixEpoch);
        Assert.Equal("activity log not written: DirectoryNotFoundException", Assert.Single(failures));
        Assert.Empty(retries);
        Assert.Equal(0, log.PendingCount);
    }

    // An IOException that is not about sharing — a file name no file system
    // takes — will not clear by waiting: the line goes, said once, and the
    // next line is tried again.
    [Fact]
    public void An_io_failure_that_is_not_sharing_is_said_once_and_not_waited_on()
    {
        var log = new RelayActivityLog(Path.Combine(_dir, new string('n', 300) + ".jsonl"), later: _ => Assert.Fail("not a sharing violation: nothing to wait for"));
        var failures = new List<string>();
        log.Failed += failures.Add;
        log.Reply("act-x-1", "activity", DateTimeOffset.UnixEpoch);
        log.Reply("act-x-2", "activity", DateTimeOffset.UnixEpoch);
        Assert.StartsWith("activity log not written: ", Assert.Single(failures));
    }
}
