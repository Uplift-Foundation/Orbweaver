using Xunit;

namespace Orbweaver.UnitTests;

// The rules that decide whether anything remote is drawn at all, and what the
// panel says when it cannot send. (Which client a panel gets used to be a rule
// here too, link over relay; the relay is gone — CB-238 — so there is only the
// link's.)
//
// Every one of them used to be an implicit "the relay is the only transport",
// which is why they are worth pinning: none announced itself as a decision, and
// each would fail in a way that reads as the link not working rather than as a
// switch being consulted about the wrong thing.
public class PanelOverTheLinkTests
{

    // --- which slash commands a remote panel offers -------------------------

    // The roster is the only source now (CB-238 removed the relay's CB-INFO
    // answers). Every arm, because an entry with no list is a real case: an
    // older far Buddy sends no `commands` field at all.
    private static MirrorProtocol.MirrorRosterEntry Entry(IReadOnlyList<string>? commands) =>
        new("job-hunter", "claude", HasTranscript: true, HasPane: true, Commands: commands);

    [Fact]
    public void ARosterCommandListBecomesTheOffer()
    {
        var offered = RemoteControlSessions.CommandsFrom(Entry(new[] { "/color", "/compact" }));

        Assert.Equal(new[] { "/color", "/compact" }, offered.Select(c => c.Name));
    }

    [Fact]
    public void AnEntryWithNoCommandListOffersNothing() =>
        Assert.Empty(RemoteControlSessions.CommandsFrom(Entry(null)));

    [Fact]
    public void AnEmptyCommandListOffersNothing() =>
        Assert.Empty(RemoteControlSessions.CommandsFrom(Entry(Array.Empty<string>())));

    [Fact]
    public void NoEntryAtAllOffersNothing() =>
        Assert.Empty(RemoteControlSessions.CommandsFrom(null));

    // --- CB-306: route kinds in the roster ------------------------------------------

    private static MirrorProtocol.MirrorRosterEntry Routed(
        IReadOnlyList<string> commands, IReadOnlyList<string>? routes, bool hasPane = false) =>
        new("job-hunter", "claude", HasTranscript: true, HasPane: hasPane, Commands: commands, CommandRoutes: routes);

    [Fact]
    public void UnavailableCommandsAreKeptWithTheirKindsForThePanelToFilter()
    {
        var offered = RemoteControlSessions.CommandsFrom(Routed(
            new[] { "/color", "/compact", "/model", "/mystery" },
            new[] { "control", "unavailable", "control", "from-the-future" }));

        // Kept, not dropped: the panel hides them from autocomplete but needs
        // the names, so "/compact" typed in full is recognised as a command and
        // refused instead of being sent to the far model as prose. An unknown
        // word from a newer far Buddy reads as unavailable.
        Assert.Equal(new[] { "/color", "/compact", "/model", "/mystery" }, offered.Select(c => c.Name));
        Assert.Equal(
            new[] { SlashRouteKind.Control, SlashRouteKind.Unavailable, SlashRouteKind.Control, SlashRouteKind.Unavailable },
            offered.Select(c => c.Route));
        Assert.Equal(new[] { "/color", "/model" },
            ChatPanel.OfferedCommands(offered).Select(c => c.Name));
    }

    [Fact]
    public void EveryRouteWordRoundTrips()
    {
        foreach (var kind in new[] { SlashRouteKind.Typed, SlashRouteKind.PassThrough, SlashRouteKind.Control,
                     SlashRouteKind.Native, SlashRouteKind.Unavailable })
            Assert.Equal(kind, MirrorProtocol.RouteFromWord(MirrorProtocol.RouteWord(kind)));

        Assert.Equal(SlashRouteKind.Unavailable, MirrorProtocol.RouteFromWord(null));
    }

    [Fact]
    public void RoutesThatDoNotLineUpWithTheCommandsAreTreatedAsAbsentEverywhere()
    {
        var noPane = Routed(new[] { "/color", "/model" }, new[] { "control" }, hasPane: false);
        var pane = Routed(new[] { "/color", "/model" }, new[] { "control" }, hasPane: true);

        // One definition, so the panel cannot offer typed commands while the
        // session sends the slash flag.
        Assert.False(RemoteControlSessions.ValidRoutes(pane));
        Assert.False(RemoteControlSessions.SpeaksSlash(pane));
        Assert.False(RemoteControlSessions.SpeaksSlash(noPane));

        var typed = RemoteControlSessions.CommandsFrom(pane);
        Assert.Equal(new[] { "/color", "/model" }, typed.Select(c => c.Name));
        Assert.All(typed, c => Assert.Equal(SlashRouteKind.Typed, c.Route));

        // Known, not offered: nothing here would run.
        var known = RemoteControlSessions.CommandsFrom(noPane);
        Assert.Equal(new[] { "/color", "/model" }, known.Select(c => c.Name));
        Assert.All(known, c => Assert.Equal(SlashRouteKind.Unavailable, c.Route));
        Assert.Empty(ChatPanel.OfferedCommands(known));
    }

    [Fact]
    public void AnOlderFarBuddyKnowsItsCommandsAndOffersThemOnlyWhereThereIsAPane()
    {
        var noPane = RemoteControlSessions.CommandsFrom(Routed(new[] { "/color" }, null, hasPane: false));
        Assert.Equal(SlashRouteKind.Unavailable, Assert.Single(noPane).Route);
        Assert.Empty(ChatPanel.OfferedCommands(noPane));

        var pane = RemoteControlSessions.CommandsFrom(Routed(new[] { "/color" }, null, hasPane: true));
        Assert.Single(ChatPanel.OfferedCommands(pane));
    }

    [Fact]
    public void ANewerFarBuddyIsRecognisedByItsRoutesEvenWhenThereAreNone()
    {
        Assert.False(RemoteControlSessions.SpeaksSlash(null));
        Assert.False(RemoteControlSessions.SpeaksSlash(Routed(new[] { "/color" }, null)));
        Assert.True(RemoteControlSessions.SpeaksSlash(Routed(Array.Empty<string>(), Array.Empty<string>())));
        Assert.False(RemoteControlSessions.SpeaksSlash(Routed(new[] { "/a", "/b" }, new[] { "typed" })));
        Assert.False(RemoteControlSessions.SpeaksSlash(Routed(Array.Empty<string>(), new[] { "typed" })));
        Assert.True(RemoteControlSessions.SpeaksSlash(Routed(new[] { "/color" }, new[] { "control" })));
    }

    [Fact]
    public void RoutesSurviveTheRosterEncoding_AndAbsenceSurvivesToo()
    {
        var withRoutes = Routed(new[] { "/color" }, new[] { "control" });
        var without = Routed(new[] { "/color" }, null);
        var empty = Routed(Array.Empty<string>(), Array.Empty<string>());

        var back = MirrorProtocol.DecodeRoster(MirrorProtocol.EncodeRoster(new[] { withRoutes, without, empty }))!;

        Assert.Equal(new[] { "control" }, back[0].CommandRoutes);
        Assert.Null(back[1].CommandRoutes);
        Assert.NotNull(back[2].CommandRoutes);
        Assert.Empty(back[2].CommandRoutes!);
    }

    [Fact]
    public void TheSlashFlagIsOnTheFrameOnlyWhenAsked()
    {
        var plain = RemoteMirrorClient.SlashFields("job-hunter", slash: false);
        var slash = RemoteMirrorClient.SlashFields("job-hunter", slash: true);

        Assert.False(plain.ContainsKey(MirrorProtocol.SlashField));
        Assert.Equal("1", slash[MirrorProtocol.SlashField]);
        Assert.Equal(plain["n"], slash["n"]);
    }

    // --- whether remote orbs are drawn at all --------------------------------

    private static readonly IReadOnlyList<RemoteControlSessions.Remote> OneRow =
        new[] { new RemoteControlSessions.Remote("jh", "mini", "idle", DateTime.UnixEpoch, "acct") };

    // **One switch again, and a different one.** This gate asked about the relay
    // while a relay was the only way a remote row could exist, then briefly
    // about both, and now about the link alone. The middle version is the one
    // worth remembering: it drew nothing on exactly the machine most likely to
    // have rows, because it insisted on a switch users had been told to turn off.
    [Fact]
    public void TheLinkOnShowsRemoteOrbs()
    {
        Assert.Same(OneRow, RemoteControlSessions.Visible(OneRow, linkOn: true));
    }

    [Fact]
    public void TheLinkOffShowsNothing()
    {
        // The scan never has to know *why* the list is empty, which is the whole
        // reason this is a gate rather than a filter further down.
        Assert.Empty(RemoteControlSessions.Visible(OneRow, linkOn: false));
    }

    // --- whether a send is even attempted ------------------------------------

    // The send gate went the same way — one transport, one switch — so
    // CanReachRemotes is gone and SendAsync asks about the link directly. What
    // is worth keeping is the wording, because a refusal that does not name the
    // setting to turn on is a dead end for whoever reads it.
    [Fact]
    public void TheRefusalNamesTheSettingToTurnOn()
    {
        Assert.Contains(
            "Show sessions from other machines",
            RemoteControlChatSession.RemoteControlOffNote);
    }

    // And the other refusal, which is new: a session on screen with no live
    // view can no longer be sent to at all, because the messaging channel that
    // used to answer here went with the relay. Said plainly rather than left as
    // a composer that swallows what you type.
    [Fact]
    public void ASessionWithNoLiveViewSaysWhyItCannotBeWrittenTo()
    {
        var note = RemoteControlChatSession.NoWayToSendNote("job-hunter");

        Assert.Contains("job-hunter", note);
        Assert.Contains("tmux", note);
    }

    // --- roster rows become orb rows -----------------------------------------

    private static MirrorProtocol.MirrorRosterEntry Entry(
        string name, string? colour = null, string? status = null) =>
        new(name, MirrorProtocol.CliClaudeCode, true, true, colour, null, status);

    [Fact]
    public void ARosterEntryBecomesAnOrbRowOnTheMachineThatServedIt()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mac-mini", Entry("job-hunter")) }, now);

        var row = Assert.Single(rows);

        Assert.Equal("job-hunter", row.Name);
        Assert.Equal("mac-mini", row.Ref);
        Assert.Equal("acct", row.Account);
        Assert.Equal(now, row.Seen);
    }

    [Fact]
    public void ARosterRouteIsTheRemoteAddressWhileTheNameStaysTheLabel()
    {
        var entry = new MirrorProtocol.MirrorRosterEntry("same title", MirrorProtocol.CliGrok,
            true, false, Route: "sid:remote-grok-7");
        var row = Assert.Single(RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mini", entry) }, DateTime.UnixEpoch));

        Assert.Equal("same title", row.Name);
        Assert.Equal("sid:remote-grok-7", row.Route);
        Assert.Equal(MirrorProtocol.CliGrok, row.Cli);
        Assert.EndsWith(":sid:remote-grok-7", row.Key);
    }

    [Fact]
    public void AWorkingSessionDrawsAsWorking()
    {
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mini", Entry("jh", status: "working")) }, DateTime.UnixEpoch);

        Assert.True(Assert.Single(rows).Working);
    }

    [Fact]
    public void AnEntryWithNoStatusReadsAsIdleRatherThanAsNothing()
    {
        // An older Buddy on the far end answers without the field. It should
        // still get an orb — one that is wrong about its pulse, not absent.
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct", new[] { ("mini", Entry("jh")) }, DateTime.UnixEpoch);

        var row = Assert.Single(rows);

        Assert.Equal("idle", row.Status);
        Assert.False(row.Working);
    }

    [Fact]
    public void ColourCarriesAcrossAndBlankIsNotAColour()
    {
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct",
            new[] { ("mini", Entry("green-one", "green")), ("mini", Entry("blank-one", "  ")) },
            DateTime.UnixEpoch);

        Assert.Equal("green", rows.Single(r => r.Name == "green-one").Color);
        Assert.Null(rows.Single(r => r.Name == "blank-one").Color);
    }

    [Fact]
    public void AnEntryNobodyServesIsDroppedRatherThanDrawnWithNoMachine()
    {
        // A row with no machine is an orb the panel could not then ask anyone
        // about — worse than no orb, because it looks like a session that is
        // there and unreachable.
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct",
            new[] { ("", Entry("orphan")), ("mini", Entry("real")) },
            DateTime.UnixEpoch);

        Assert.Equal("real", Assert.Single(rows).Name);
    }

    [Fact]
    public void EveryMachineIsRepresented()
    {
        var rows = RemoteControlSessions.RemotesFromRoster(
            "acct",
            new[] { ("mini", Entry("a")), ("laptop", Entry("b")) },
            DateTime.UnixEpoch);

        Assert.Equal(new[] { "laptop", "mini" }, rows.Select(r => r.Ref).OrderBy(x => x));
    }

    [Fact]
    public void NothingKnownIsNoRows()
    {
        Assert.Empty(RemoteControlSessions.RemotesFromRoster(
            "acct",
            Array.Empty<(string, MirrorProtocol.MirrorRosterEntry)>(),
            DateTime.UnixEpoch));
    }
}
