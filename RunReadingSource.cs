namespace Orbweaver
{
    // CB-404: where a run reading came from, so the chip can tell a reading from
    // a guess. Its own file because RemoteRunOptions (RemoteChat.cs) carries it,
    // and the TranscriptTests console suite compiles RemoteChat.cs by hand: this
    // keeps that suite from having to pull in the whole run-controls core.
    public enum RunReadingSource { None, Transcript, PaneFooter, Rollout, GrokSummary, CloudEvents, CloudRoster, Gateway, LastSet }
}
