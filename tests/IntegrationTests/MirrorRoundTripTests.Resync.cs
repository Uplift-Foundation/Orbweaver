using Xunit;

namespace Orbweaver.Tests;

// CB-402: the rows the scan draws catch up with what the client knows within one
// dial tick, even when no roster reply registered a change.
//
// Drives the client straight in rather than installing it on PeerSessions:
// MirrorEdgeCaseTests runs in another collection, in parallel, and installs and
// clears its own client there.
public partial class MirrorRoundTripTests
{
    [Fact]
    public async Task StaleDrawnRowsCatchUpWithTheClientOnTheNextTick()
    {
        var harness = new Harness(_dir);
        harness.AddSession("team title", WriteTranscript("lead.jsonl", Conversation(2)));
        harness.AddSession("other", WriteTranscript("o.jsonl", Conversation(2)));
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        Assert.Equal(2, harness.Client.Known().Count);

        try
        {
            // The drawn rows hold nothing although the client knows two: the
            // stale state no later "nothing changed" reply would ever repair.
            RemoteControlSessions.SetPeerRowsForTests(Array.Empty<RemoteControlSessions.Remote>());

            RemoteControlSessions.RepublishIfOutOfStep(harness.Client);
            var caughtUp = RemoteControlSessions.SnapshotForTests;
            Assert.Equal(2, caughtUp.Count);

            // In step: left exactly as it was, so Seen and the lifetime rule
            // that reads it are untouched.
            RemoteControlSessions.RepublishIfOutOfStep(harness.Client);
            Assert.Same(caughtUp, RemoteControlSessions.SnapshotForTests);

            // No client: nothing to compare, nothing published.
            RemoteControlSessions.SetPeerRowsForTests(Array.Empty<RemoteControlSessions.Remote>());
            RemoteControlSessions.RepublishIfOutOfStep(null);
            Assert.Empty(RemoteControlSessions.SnapshotForTests);
        }
        finally
        {
            RemoteControlSessions.SetPeerRowsForTests(Array.Empty<RemoteControlSessions.Remote>());
        }
    }
}
