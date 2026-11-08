using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-307: RemoteMirrorServer.WithoutQuietDuplicates, the roster post-pass that
// keeps the one case where a quiet session is still left out — a parked copy
// beside a live copy of the same name. The rule itself is
// SessionLiveness.SuppressedDuplicates; these pin how the server maps a
// roster onto it.
public class QuietDuplicateRosterTests
{
    private static (string SessionId, SessionStatus Status) Session(
        string id, string title, SessionSource source = SessionSource.ClaudeCode, string lead = "") =>
        (id, new SessionStatus { Title = title, Source = source, Lead = lead });

    private static MirrorProtocol.MirrorRosterEntry Entry(string name, string id, bool parked) =>
        new(name, MirrorProtocol.CliClaudeCode, true, true,
            Route: RemoteMirrorServer.RouteFor(id), Parked: parked ? true : null);

    private static (List<MirrorProtocol.MirrorRosterEntry> Kept,
        List<(MirrorProtocol.MirrorRosterEntry Entry, string Name)> Suppressed) Run(
            IEnumerable<MirrorProtocol.MirrorRosterEntry> entries,
            params (string, SessionStatus)[] sessions) =>
        RemoteMirrorServer.WithoutQuietDuplicates(entries.ToList(), sessions);

    [Fact]
    public void AParkedCopyBesideALiveCopyIsDroppedAndNamedByItsTitle()
    {
        // Offer qualifies a shared registry name, so the entries' names differ;
        // the rule keys on the status title, which is what the host's orb wears.
        var (kept, gone) = Run(
            new[] { Entry("job-hunter#aaaa", "a", false), Entry("job-hunter#bbbb", "b", true) },
            Session("a", "job-hunter"), Session("b", "job-hunter"));

        Assert.Equal(RemoteMirrorServer.RouteFor("a"), Assert.Single(kept).Route);
        var (entry, name) = Assert.Single(gone);
        Assert.Equal(RemoteMirrorServer.RouteFor("b"), entry.Route);
        Assert.Equal("job-hunter", name);
    }

    [Fact]
    public void AnUntitledSessionFallsBackToTheEntryName()
    {
        var (kept, gone) = Run(
            new[] { Entry("job-hunter", "a", false), Entry("job-hunter", "b", true) },
            Session("a", ""), Session("b", ""));

        Assert.Single(kept);
        Assert.Equal("job-hunter", Assert.Single(gone).Name);
    }

    [Fact]
    public void BothParkedAreBothKept()
    {
        var (kept, gone) = Run(
            new[] { Entry("job-hunter", "a", true), Entry("job-hunter", "b", true) },
            Session("a", "job-hunter"), Session("b", "job-hunter"));

        Assert.Equal(2, kept.Count);
        Assert.Empty(gone);
    }

    [Fact]
    public void AMemberWearingItsLeadsTitleIsNeverDropped()
    {
        // Every member inherits its lead's title (CB-223).
        var (kept, gone) = Run(
            new[] { Entry("backlog", "lead", false), Entry("backlog", "m", true) },
            Session("lead", "backlog"), Session("m", "backlog", lead: "lead"));

        Assert.Equal(2, kept.Count);
        Assert.Empty(gone);
    }

    [Fact]
    public void ACodexSessionIsNeverDropped()
    {
        var (kept, gone) = Run(
            new[] { Entry("work", "a", false), Entry("work", "c", true) },
            Session("a", "work"), Session("c", "work", SessionSource.Codex));

        Assert.Equal(2, kept.Count);
        Assert.Empty(gone);
    }

    [Fact]
    public void AnEntryWithNoRouteOrNoSessionIsKeptUntouched()
    {
        var noRoute = new MirrorProtocol.MirrorRosterEntry("job-hunter", MirrorProtocol.CliClaudeCode,
            true, true, Parked: true);
        var orphan = Entry("job-hunter", "nobody", true);

        var (kept, gone) = Run(
            new[] { Entry("job-hunter", "a", false), noRoute, orphan },
            Session("a", "job-hunter"));

        Assert.Equal(3, kept.Count);
        Assert.Empty(gone);
    }

    [Fact]
    public void ASuppressedLeadCannotSponsorItsMembers()
    {
        // The server runs this pass before the member block, so the routes the
        // member block sees are the post-pass's. A member whose lead was
        // suppressed is then not offered.
        var sessions = new[]
        {
            Session("live", "job-hunter"),
            Session("quiet", "job-hunter"),
            Session("member", "job-hunter", lead: "quiet"),
        };

        var (kept, _) = Run(
            new[] { Entry("job-hunter#1", "live", false), Entry("job-hunter#2", "quiet", true) }, sessions);

        var members = RemoteMirrorServer.TeamMembersToOffer(
            kept.Select(e => e.Route!), sessions, _ => true);

        Assert.Empty(members);
    }
}
