using Xunit;

namespace Orbweaver.Tests;

// CB-402: the rows the scan draws catch up with what the client knows within one
// dial tick, even when no roster reply registered a change.
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
            // Reset first: it also uninstalls a test client.
            RemoteControlSessions.ResetForTests();
            PeerSessions.UseClientForTests(harness.Client);
            Assert.Empty(RemoteControlSessions.SnapshotForTests);

            RemoteControlSessions.RepublishIfOutOfStep();
            var caughtUp = RemoteControlSessions.SnapshotForTests;
            Assert.Equal(2, caughtUp.Count);

            // In step: left exactly as it was, so Seen and the lifetime rule
            // that reads it are untouched.
            RemoteControlSessions.RepublishIfOutOfStep();
            Assert.Same(caughtUp, RemoteControlSessions.SnapshotForTests);
        }
        finally
        {
            PeerSessions.UseClientForTests(null);
            RemoteControlSessions.ResetForTests();
        }

        // No client at all: nothing to compare, nothing published.
        RemoteControlSessions.RepublishIfOutOfStep();
        Assert.Empty(RemoteControlSessions.SnapshotForTests);
    }
}
