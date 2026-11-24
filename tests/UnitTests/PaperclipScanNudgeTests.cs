using Xunit;
using static Orbweaver.Tests.PaperclipFx;

namespace Orbweaver.Tests;

// CB-421: a Paperclip change should be on screen about one poll plus a short
// debounce later. ScanNudge is the decision (pure, so no timer and no sleeping);
// PaperclipSessions.Publish is what raises it, and only for a change in what the
// orbs draw.
public class ScanNudgeTests
{
    [Fact]
    public void ARequest_StartsTheDebounce_AndItsTickStartsExactlyOneScan()
    {
        var n = new ScanNudge();
        Assert.True(n.Request(scanInFlight: false));
        Assert.True(n.OnDebounceTick(scanInFlight: false));
        Assert.False(n.OnScanFinished());           // nothing queued behind it
    }

    [Fact]
    public void ABurstOfRequests_IsOneScan()
    {
        var n = new ScanNudge();
        // Each request restarts the same debounce, so only the last tick fires.
        Assert.True(n.Request(false)); Assert.True(n.Request(false)); Assert.True(n.Request(false));
        Assert.True(n.OnDebounceTick(false));
        Assert.False(n.OnScanFinished());
    }

    [Fact]
    public void ARequestDuringAScan_IsNotDropped_OneFollowUpRunsAfterIt()
    {
        var n = new ScanNudge();
        Assert.False(n.Request(scanInFlight: true));   // nothing to start now
        Assert.True(n.OnScanFinished());                // but one more is wanted
        Assert.True(n.OnDebounceTick(false));           // and it runs
        Assert.False(n.OnScanFinished());               // exactly one
    }

    [Fact]
    public void SeveralRequestsDuringAScan_AreOneFollowUp()
    {
        var n = new ScanNudge();
        n.Request(true); n.Request(true); n.Request(true);
        Assert.True(n.OnScanFinished());
        Assert.False(n.OnScanFinished());
    }

    [Fact]
    public void ADebounceTickThatLandsMidScan_QueuesTheNudgeInsteadOfLosingIt()
    {
        var n = new ScanNudge();
        Assert.True(n.Request(false));                  // debounce started, no scan yet
        Assert.False(n.OnDebounceTick(scanInFlight: true));   // a scan began meanwhile
        Assert.True(n.OnScanFinished());                // the nudge survives it
    }

    [Fact]
    public void TheControl_ATickNobodyNudged_IsTheWatchersAndBehavesAsBefore()
    {
        var n = new ScanNudge();
        Assert.True(n.OnDebounceTick(scanInFlight: false));
        Assert.True(n.OnDebounceTick(scanInFlight: true));   // ScheduleScan's own guard decides, as it always did
        Assert.False(n.OnScanFinished());                    // and it queues nothing
    }
}

// Serialised with the other tests that read PaperclipSessions' static snapshot, so a
// publish here cannot land under an assertion there.
[Collection("Settings")]
public class PaperclipPublishTests : IDisposable
{
    private int _changes;
    private readonly Action _count;

    public PaperclipPublishTests()
    {
        _count = () => Interlocked.Increment(ref _changes);
        PaperclipSessions.Changed += _count;
        PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");
        _changes = 0;
    }

    public void Dispose()
    {
        PaperclipSessions.Changed -= _count;
        PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");
    }

    private static PaperclipAgent A(string id = "a", string state = "idle", PaperclipMark mark = PaperclipMark.None,
        int? budget = null, string? activity = null, DateTime? started = null, string? last = null, DateTime? finished = null,
        IReadOnlyList<PaperclipNeed>? needs = null, bool sleeping = true, string? task = null) =>
        new(id, id, "r", "t", "idle", null, "paperclip_runner", state, mark, null, needs ?? [], task, activity, started, last,
            budget, Now, "http://x/" + id, sleeping, null, null, 5000, budget ?? 0, finished);

    [Fact]
    public void AChangedPublish_RaisesExactlyOnce_EvenWhenPublishedTwice()
    {
        PaperclipSessions.Publish([A()], PaperclipLink.Connected, "Connected");
        Assert.Equal(1, _changes);
        PaperclipSessions.Publish([A()], PaperclipLink.Connected, "Connected");
        Assert.Equal(1, _changes);   // the same again: nothing
    }

    [Fact]
    public void AnUnchangedSnapshot_RaisesNothing_SoAHealthyIdleCompanyAddsNoScans()
    {
        PaperclipSessions.Publish([A()], PaperclipLink.Connected, "Connected");
        _changes = 0;
        for (var i = 0; i < 20; i++) PaperclipSessions.Publish([A()], PaperclipLink.Connected, "Connected");
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void TimeAlone_NeverRaises_TheRunningForAndAgoLinesMoveOnTheirOwn()
    {
        PaperclipSessions.Publish([A(state: "generating", sleeping: false, started: Now, last: "failed", finished: Now)],
            PaperclipLink.Connected, "Connected");
        _changes = 0;
        PaperclipSessions.Publish([A(state: "generating", sleeping: false, started: Now.AddMinutes(5), last: "failed", finished: Now.AddHours(1))],
            PaperclipLink.Connected, "Connected");
        Assert.Equal(0, _changes);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("mark")]
    [InlineData("ring")]
    [InlineData("activity")]
    [InlineData("task")]
    [InlineData("result")]
    [InlineData("need")]
    [InlineData("asleep")]
    public void EachThingTheOrbsDraw_Raises(string what)
    {
        PaperclipSessions.Publish([A()], PaperclipLink.Connected, "Connected");
        _changes = 0;
        var changed = what switch
        {
            "state" => A(state: "generating", sleeping: false),
            "mark" => A(mark: PaperclipMark.Broke, sleeping: false),
            "ring" => A(budget: 30),
            "activity" => A(activity: "Bash"),
            "task" => A(task: "Fix it"),
            "result" => A(last: "failed"),
            "need" => A(needs: [new PaperclipNeed("approval", "p", "Hire", "u")]),
            _ => A(sleeping: false),
        };
        PaperclipSessions.Publish([changed], PaperclipLink.Connected, "Connected");
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void TheLinkAndItsWords_AndAnAgentComingOrGoing_Raise()
    {
        PaperclipSessions.Publish([A("a"), A("b")], PaperclipLink.Connected, "Connected");
        _changes = 0;
        PaperclipSessions.Publish([A("a"), A("b")], PaperclipLink.NeedsReconnect, "Connected");
        Assert.Equal(1, _changes);
        PaperclipSessions.Publish([A("a"), A("b")], PaperclipLink.NeedsReconnect, "Reconnect to Paperclip");
        Assert.Equal(2, _changes);
        PaperclipSessions.Publish([A("a")], PaperclipLink.NeedsReconnect, "Reconnect to Paperclip");
        Assert.Equal(3, _changes);
    }

    [Fact]
    public void PublishSetsWhatTheScanReads()
    {
        var agents = new[] { A("a") };
        PaperclipSessions.Publish(agents, PaperclipLink.Connected, "Connected to x.");
        Assert.Same(agents, PaperclipSessions.Snapshot());
        Assert.Equal((PaperclipLink.Connected, "Connected to x."), (PaperclipSessions.Link, PaperclipSessions.StatusText));
    }

    [Fact]
    public void TheTestSeamsDoNotRaise_SoSuitesThatSetASnapshotDoNotNudgeAScan()
    {
        PaperclipSessions.SetSnapshotForTests([A("z")]);
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "x");
        Assert.Equal(0, _changes);
    }
}
