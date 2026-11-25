using System.Reflection;
using Xunit;

namespace Orbweaver.Tests;

// CB-347: a Connected written by the event pump for a connection that has
// since dropped must not overwrite the status the run loop moved on to.
public class RelayClientStaleConnectedTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb347-" + Guid.NewGuid());
    private RelayStub _stub = null!;
    private RelayClient? _client;

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    public Task InitializeAsync() { _stub = new RelayStub(); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        try { if (_client is not null) await RelayClientTests.StopBounded(_client); }
        finally
        {
            _client?.Dispose();
            await _stub.DisposeAsync();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_welcome_applied_after_its_connection_dropped_does_not_leave_the_status_on_connected()
    {
        var identity = new RelayIdentity(_dir, new MemoryStore());
        identity.Keys();
        identity.SetRegistration("dsk_stub000000000001", "tok");
        lock (_stub.ValidTokens) _stub.ValidTokens.Add("tok");

        var backoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac");
        _client = new RelayClient(identity, options, new WebSocketRelayTransport(),
            // The run loop parks in its backoff, so the status it left behind
            // (Connecting) is what stays until the stale write has run.
            delay: async (_, ct) => { backoff.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });

        // The pump is held inside the first state change, so the welcome's
        // deferred Connected queues behind it and runs only when released.
        using var hold = new ManualResetEventSlim();
        var phonesChanged = 0;
        _client.StateChanged += _ => hold.Wait(TimeSpan.FromSeconds(10));
        _client.PhonesChanged += () => Interlocked.Increment(ref phonesChanged);
        _client.Start();

        await _stub.NextT("hello");
        var link = (RelayLink)typeof(RelayClient).GetField("_link", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_client)!;
        await Until(() => link.Welcomed);                  // the welcome is in, its Connected still deferred

        await _stub.DropConnection();
        await backoff.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the run loop is past the drop
        Assert.Equal(RelayClient.Status.Connecting, _client.State);

        hold.Set();                                        // now the deferred Connected runs
        await Until(() => Volatile.Read(ref phonesChanged) >= 2);   // the welcome's, then the drop's: the pump is through both

        Assert.NotEqual(RelayClient.Status.Connected, _client.State);
    }
}
