using Xunit;

namespace Orbweaver.Tests;

// CB-415: building the mirror roster asked RemoteMirrorServer.RegistryEntryFor
// once per session, and each ask walked the whole registry on disk, on the
// peer pump. Walks are counted through SessionRegistry.Walk and the clock is
// moved by hand: no sleeps, no wall-clock assertions.
[Collection("SessionRegistryScanCache")]
public class SessionRegistryScanCacheTests : IDisposable
{
    private DateTime _now = new(2026, 11, 25, 18, 0, 0, DateTimeKind.Utc);
    private int _walks;
    private List<SessionRegistry.Entry> _disk = new();
    private readonly int _pid = Environment.ProcessId;

    public SessionRegistryScanCacheTests()
    {
        SessionRegistry.ForgetScan();
        SessionRegistry.Now = () => _now;
        SessionRegistry.Walk = _ => { _walks++; return _disk.ToList(); };
    }

    public void Dispose()
    {
        SessionRegistry.Now = () => DateTime.UtcNow;
        SessionRegistry.Walk = SessionRegistry.Scan;
        SessionRegistry.ForgetScan();
    }

    private SessionRegistry.Entry Row(int pid, string id) =>
        new(pid, id, null, "bg", 1, "/tmp/" + id + ".sock", "idle", null);

    [Fact]
    public void ABuildThatAsksOncePerSessionWalksTheRegistryOnce()
    {
        _disk.Add(Row(_pid, "one"));

        for (var i = 0; i < 8; i++)
            Assert.NotNull(RemoteMirrorServer.RegistryEntryFor(new SessionStatus { SessionPid = _pid }));

        Assert.Equal(1, _walks);
    }

    [Fact]
    public void AScanAfterTheHoldExpiresSeesASessionThatRegisteredMeanwhile()
    {
        Assert.Empty(SessionRegistry.ScanCached(new[] { "/root" }));
        _disk.Add(Row(1, "new"));

        _now += SessionRegistry.ScanHeldFor - TimeSpan.FromTicks(1);
        Assert.Empty(SessionRegistry.ScanCached(new[] { "/root" }));
        Assert.Equal(1, _walks);

        _now += TimeSpan.FromTicks(1);
        Assert.Single(SessionRegistry.ScanCached(new[] { "/root" }));
        Assert.Equal(2, _walks);
    }

    [Fact]
    public void ADifferentSetOfRootsIsNotAnsweredFromAnotherSetsWalk()
    {
        SessionRegistry.ScanCached(new[] { "/a" });
        SessionRegistry.ScanCached(new[] { "/b" });

        Assert.Equal(2, _walks);
    }
}
