using Xunit;

namespace Orbweaver.Tests;

// CB-402: a peer's team (lead and member, one shared title) is left out of one
// roster and is back in the next. Driven through the real client and server,
// then the republish rows, so the state a hand-built snapshot cannot model
// (_roster, _servedBy, _answeredNo, the held roster hash) is in play.
public partial class MirrorRoundTripTests
{
    [Fact]
    public async Task ATeamLeftOutOfOneRosterIsKnownAgainAfterTheNext()
    {
        var harness = new Harness(_dir);
        harness.AddSession("team title", WriteTranscript("lead.jsonl", Conversation(2)));
        var lead = harness.SessionIdOf("team title");
        var member = harness.AddTeamMember("team title", lead, "wren", "blue", WriteTranscript("m.jsonl", Conversation(2)));
        harness.AddSession("other", WriteTranscript("o.jsonl", Conversation(2)));
        var leadRoute = RemoteMirrorServer.RouteFor(lead);
        var memberRoute = RemoteMirrorServer.RouteFor(member);

        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        Assert.Equal(3, harness.Client.Known().Count);

        harness.Paneless.Add("team title");
        harness.Reshape("team title", st => st.TranscriptPath = "");
        File.Delete(Path.Combine(_dir, "m.jsonl"));
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        Assert.Single(harness.Client.Known());

        // Unchanged asks while it is out, as the mini's log showed.
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);

        harness.Paneless.Remove("team title");
        harness.Reshape("team title", st => st.TranscriptPath = WriteTranscript("lead.jsonl", Conversation(2)));
        WriteTranscript("m.jsonl", Conversation(2));
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);

        var routes = harness.Client.Known().Select(k => k.Entry.Route).ToList();
        Assert.Contains(leadRoute, routes);
        Assert.Contains(memberRoute, routes);

        var rows = RemoteControlSessions.RemotesFromRoster("acct", harness.Client.Known(), DateTime.UtcNow);
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, RemoteControlSessions.OnePerSession(rows).Count);
    }
}
