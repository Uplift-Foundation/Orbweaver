using Xunit;

namespace Orbweaver.UnitTests;

// CB-357: a running session that has not been spoken to has a pane and no
// transcript. These are the three pure decisions that let it be drawn.
public class PeerEntryWithoutTranscriptTests
{
    private static MirrorProtocol.MirrorRosterEntry Entry(bool transcript, bool pane) =>
        new("s", "claude", transcript, pane);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)] // the negative control
    public void AnEntryIsShownWhenThereIsATranscriptOrAPane(bool transcript, bool pane, bool shown) =>
        Assert.Equal(shown, RemoteMirrorClient.Shows(Entry(transcript, pane)));

    [Fact]
    public void ItOpensEmptyOnlyWhenTheRosterSaidNoTranscriptAndTheFetchSaidNoTranscript() =>
        Assert.True(RemoteMirrorClient.OpensEmpty(false, false, MirrorProtocol.ErrNoTranscript));

    [Theory]
    [InlineData(true, false, "no-transcript")]  // roster promised one
    [InlineData(false, false, "no-pane")]       // some other refusal
    [InlineData(false, false, null)]            // no code at all
    [InlineData(false, true, null)]             // the fetch worked
    [InlineData(false, true, "no-transcript")]  // ok wins over any stray code
    public void ItDoesNotOpenEmptyForAnythingElse(bool rosterHasTranscript, bool ok, string? code) =>
        Assert.False(RemoteMirrorClient.OpensEmpty(rosterHasTranscript, ok, code));

    [Fact]
    public void TheEmptyNoteSaysNoMessagesYetAndWhereTheFirstOneGoes()
    {
        var note = RemoteControlChatSession.LiveViewNote("Mini work", true);
        Assert.Contains("No messages yet", note);
        Assert.Contains("Mini work", note);
        Assert.Contains("terminal", note);
        Assert.DoesNotContain("mirrors", note);
    }

    [Fact]
    public void TheNonEmptyNoteIsTheUsualLiveViewLine()
    {
        var note = RemoteControlChatSession.LiveViewNote("Mini work", false);
        Assert.StartsWith("Live view: this panel mirrors Mini work's own conversation", note);
    }
}
