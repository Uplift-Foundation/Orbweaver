using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// CB-276's rules, with no window, no settings file and no scan behind them.
//
// The scan asks HiddenOrbs one question per pass and believes the answer, so
// every outcome of that answer is pinned here: who is hidden, through which
// lead, what Settings will say about them, and — the one that fails quietly —
// when an entry is forgotten. A prune that fires on a source that merely went
// quiet un-hides an orb the user asked never to see again, and nothing on
// screen says why; so DuePrunes' negative controls are the cases that matter
// most below.
public class HiddenOrbsTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    // --- Effective -----------------------------------------------------------

    private static HashSet<string> Effective(string[] explicitIds, params (string Id, string? Lead)[] sessions) =>
        HiddenOrbs.Effective(explicitIds, sessions.Select(s => (s.Id, s.Lead)));

    [Fact]
    public void NoEntriesHidesNothingWhateverTheTeamsLookLike()
    {
        Assert.Empty(Effective(Array.Empty<string>(), ("lead", ""), ("member", "lead")));
    }

    [Fact]
    public void AnExplicitEntryIsHiddenEvenWhenTheScanDidNotFindIt()
    {
        // A hide for a session not on screen this pass is still a hide.
        Assert.Equal(new[] { "gone" }, Effective(new[] { "gone" }).ToArray());
    }

    [Fact]
    public void HidingALeadHidesItsMembersAndTheirMembers()
    {
        var hidden = Effective(new[] { "lead" },
            ("lead", ""), ("member", "lead"), ("grand", "member"), ("stranger", ""));

        Assert.Equal(new[] { "grand", "lead", "member" }, hidden.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void HidingAMemberLeavesItsLeadAndSiblingsAlone()
    {
        var hidden = Effective(new[] { "member" },
            ("lead", ""), ("member", "lead"), ("sibling", "lead"), ("under", "member"));

        // Its own member goes with it; nothing above or beside it does.
        Assert.Equal(new[] { "member", "under" }, hidden.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ALeadCycleEndsInsteadOfLooping()
    {
        var hidden = Effective(new[] { "x" }, ("a", "b"), ("b", "a"), ("c", "a"));

        Assert.Equal(new[] { "x" }, hidden.ToArray());
    }

    [Fact]
    public void ALeadCycleThroughAHiddenSessionHidesTheCycle()
    {
        var hidden = Effective(new[] { "a" }, ("a", "b"), ("b", "a"));

        Assert.Equal(new[] { "a", "b" }, hidden.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ALeadThatIsNotInTheScanEndsTheWalk()
    {
        Assert.Equal(new[] { "x" }, Effective(new[] { "x" }, ("member", "absent")).ToArray());
    }

    [Fact]
    public void IdsAreCaseInsensitive()
    {
        var hidden = Effective(new[] { "LEAD" }, ("lead", ""), ("member", "Lead"));

        Assert.Contains("member", hidden);
        Assert.Contains("lead", hidden);
    }

    [Fact]
    public void NoSessionsReturnsTheExplicitEntries()
    {
        Assert.Equal(new[] { "a" }, Effective(new[] { "a" }).ToArray());
    }

    // --- PropagatingLead -----------------------------------------------------

    [Theory]
    [InlineData(SessionSource.ClaudeCode, "lead")]
    [InlineData(SessionSource.RemoteControl, "lead")]
    [InlineData(SessionSource.Codex, "")]
    [InlineData(SessionSource.Grok, "")]
    [InlineData(SessionSource.OpenClaw, "")]      // the lead field is the room
    [InlineData(SessionSource.ClaudeCloud, "")]
    [InlineData(SessionSource.Paperclip, "")]     // CB-421: a company's reporting line is not a team to hide
    public void OnlyAgentTeamsPropagateAHide(SessionSource source, string expected)
    {
        Assert.Equal(expected, HiddenOrbs.PropagatingLead(source, "lead"));
    }

    [Fact]
    public void EverySourceIsCoveredByThePropagationCases()
    {
        // Fails when a source is added without deciding whether it has teams.
        Assert.Equal(7, Enum.GetValues<SessionSource>().Length);
    }

    [Fact]
    public void ANullLeadPropagatesNothing()
    {
        Assert.Equal("", HiddenOrbs.PropagatingLead(SessionSource.ClaudeCode, null));
    }

    // --- Describe and SourceLabel --------------------------------------------

    [Fact]
    public void ALocalSessionWithATitleIsDescribedByTitleAndFolder()
    {
        var orb = HiddenOrbs.Describe("id-1",
            new SessionStatus { Title = "  fix the tray ", Cwd = "/Users/me/Source/Orbweaver" }, Now, null);

        Assert.Equal(new HiddenOrb("id-1", Now, "fix the tray", "Claude Code", "/Users/me/Source/Orbweaver"), orb);
    }

    [Fact]
    public void AnUntitledSessionIsNamedForItsFolder()
    {
        var orb = HiddenOrbs.Describe("id-1", new SessionStatus { Cwd = @"C:\work\repo\" }, Now, null);

        Assert.Equal("repo", orb.Title);
    }

    [Fact]
    public void ASessionWithNeitherTitleNorFolderIsNamedForItsId()
    {
        var orb = HiddenOrbs.Describe("cloud:abc", new SessionStatus { Source = SessionSource.ClaudeCloud }, Now, null);

        Assert.Equal("cloud:abc", orb.Title);
        Assert.Equal("Claude cloud", orb.Source);
        Assert.Equal("", orb.Detail);
        Assert.Null(orb.Peer);
    }

    [Fact]
    public void ATeamMemberIsToldApartByItsAgentName()
    {
        var orb = HiddenOrbs.Describe("m",
            new SessionStatus { Title = "lead's title", Agent = "researcher", Cwd = "/x" }, Now, null);

        Assert.Equal("researcher", orb.Detail);
    }

    [Fact]
    public void ARemoteSessionCarriesItsPeerForPruningAndShowsItWithNothingBetter()
    {
        var orb = HiddenOrbs.Describe("rc:work:abc",
            new SessionStatus { Source = SessionSource.RemoteControl, Title = "far" }, Now, "mini");

        Assert.Equal("Other machine", orb.Source);
        Assert.Equal("mini", orb.Detail);
        Assert.Equal("mini", orb.Peer);
    }

    [Fact]
    public void ARemoteSessionWithNoPeerHasAnEmptyDetail()
    {
        var orb = HiddenOrbs.Describe("rc:work:abc",
            new SessionStatus { Source = SessionSource.RemoteControl, Title = "far" }, Now, null);

        Assert.Equal("", orb.Detail);
        Assert.Null(orb.Peer);
    }

    [Fact]
    public void APeerIsNotRecordedForALocalSession()
    {
        var orb = HiddenOrbs.Describe("id", new SessionStatus { Title = "t" }, Now, "mini");

        Assert.Null(orb.Peer);
        Assert.Equal("", orb.Detail);
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode, false, false, "Claude Code")]
    [InlineData(SessionSource.ClaudeCode, true, false, "Background job")]
    [InlineData(SessionSource.Codex, false, false, "Codex")]
    [InlineData(SessionSource.Grok, false, false, "Grok")]
    [InlineData(SessionSource.OpenClaw, false, false, "OpenClaw")]
    [InlineData(SessionSource.OpenClaw, false, true, "OpenClaw room")]
    [InlineData(SessionSource.RemoteControl, false, false, "Other machine")]
    [InlineData(SessionSource.ClaudeCloud, false, false, "Claude cloud")]
    public void EachSourceHasItsOwnBadge(SessionSource source, bool background, bool room, string expected)
    {
        var status = new SessionStatus
        {
            Source = source,
            IsRoom = room,
            Shape = background ? LocalSessionShape.Background : LocalSessionShape.Terminal
        };

        Assert.Equal(expected, HiddenOrbs.SourceLabel(status));
    }

    [Fact]
    public void AnUnknownSourceValueFallsBackToItsName()
    {
        Assert.Equal("99", HiddenOrbs.SourceLabel(new SessionStatus { Source = (SessionSource)99 }));
    }

    // --- WhenText and MenuHeader ---------------------------------------------

    [Theory]
    [InlineData(0, "hidden just now")]
    [InlineData(59, "hidden just now")]
    [InlineData(60, "hidden 1 min ago")]
    [InlineData(59 * 60, "hidden 59 min ago")]
    [InlineData(2 * 3600 + 30, "hidden 2 h ago")]
    [InlineData(86400, "hidden 1 day ago")]
    [InlineData(3 * 86400 + 5, "hidden 3 days ago")]
    [InlineData(-3600, "hidden just now")]   // a clock that moved back
    public void WhenTextSaysHowLongAgo(int secondsAgo, string expected)
    {
        Assert.Equal(expected, HiddenOrbs.WhenText(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void AnUnknownTimeSaysNothing()
    {
        Assert.Equal("", HiddenOrbs.WhenText(DateTime.MinValue, Now));
    }

    [Fact]
    public void ALeadsMenuSaysItsTeamGoesToo()
    {
        Assert.Equal("Hide this orb and its team", HiddenOrbs.MenuHeader(true));
        Assert.Equal("Hide this orb", HiddenOrbs.MenuHeader(false));
    }

    // --- DuePrunes -----------------------------------------------------------

    private static IReadOnlyList<string> Prune(
        Dictionary<string, DateTime> clock, DateTime at, bool? answer, params string[] ids) =>
        HiddenOrbs.DuePrunes(ids, _ => answer, clock, at, HiddenOrbs.PruneGrace);

    [Fact]
    public void ASourceThatCannotSayPrunesNothingForAnHour()
    {
        // The negative control: off, disconnected, halted. An hour of it,
        // every two seconds, and the hide survives.
        var clock = new Dictionary<string, DateTime>();
        for (var t = 0; t <= 3600; t += 2)
        {
            Assert.Empty(Prune(clock, Now.AddSeconds(t), null, "a"));
        }

        Assert.Empty(clock);
    }

    [Fact]
    public void ASourceThatListsTheSessionPrunesNothingAndClearsTheClock()
    {
        var clock = new Dictionary<string, DateTime> { ["a"] = Now.AddHours(-1) };

        Assert.Empty(Prune(clock, Now, true, "a"));
        Assert.Empty(clock);
    }

    [Fact]
    public void AbsenceStartsTheClockAndPrunesAtTheGraceNotATickBefore()
    {
        var clock = new Dictionary<string, DateTime>();

        Assert.Empty(Prune(clock, Now, false, "a"));
        Assert.Equal(Now, clock["a"]);

        Assert.Empty(Prune(clock, Now + HiddenOrbs.PruneGrace - TimeSpan.FromSeconds(1), false, "a"));
        Assert.Equal(new[] { "a" }, Prune(clock, Now + HiddenOrbs.PruneGrace, false, "a"));
        Assert.Empty(clock);
    }

    [Fact]
    public void AnAnswerInTheMiddleRestartsTheClock()
    {
        var clock = new Dictionary<string, DateTime>();
        Prune(clock, Now, false, "a");
        Prune(clock, Now.AddSeconds(60), null, "a");
        Prune(clock, Now.AddSeconds(62), false, "a");

        Assert.Empty(Prune(clock, Now + HiddenOrbs.PruneGrace, false, "a"));
        Assert.Equal(new[] { "a" }, Prune(clock, Now.AddSeconds(62) + HiddenOrbs.PruneGrace, false, "a"));
    }

    [Fact]
    public void AnIdNoLongerHiddenIsForgottenFromTheClock()
    {
        var clock = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["restored"] = Now.AddHours(-1),
            ["kept"] = Now
        };

        Prune(clock, Now, false, "KEPT");

        Assert.Equal(new[] { "kept" }, clock.Keys.ToArray());
    }

    [Fact]
    public void EachIdIsJudgedByItsOwnAnswer()
    {
        var clock = new Dictionary<string, DateTime>();
        var answers = new Dictionary<string, bool?> { ["gone"] = false, ["here"] = true, ["unknown"] = null };
        HiddenOrbs.DuePrunes(answers.Keys, id => answers[id], clock, Now, HiddenOrbs.PruneGrace);

        var due = HiddenOrbs.DuePrunes(answers.Keys, id => answers[id], clock, Now + HiddenOrbs.PruneGrace, HiddenOrbs.PruneGrace);

        Assert.Equal(new[] { "gone" }, due);
    }

    // --- Listed --------------------------------------------------------------

    private static readonly IReadOnlySet<string> Local = new HashSet<string> { "local-1" };
    private static readonly IReadOnlySet<string> Gateway = new HashSet<string>
    {
        "agent:main:main",
        "agent:main:discord:channel:1474"
    };
    private static readonly IReadOnlySet<string> Cloud = new HashSet<string> { "session_1" };
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Remote =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["mini"] = new HashSet<string> { "rc:work:abc" },
            ["quiet"] = new HashSet<string>()
        };

    private static bool? Listed(string id, string? peer = null) =>
        HiddenOrbs.Listed(id, peer, Local, Gateway, Cloud, Remote);

    [Theory]
    [InlineData("local-1", true)]
    [InlineData("local-2", false)]
    [InlineData("openclaw:agent:main:main", true)]
    [InlineData("openclaw:agent:main:gone", false)]
    [InlineData("openclaw:room:discord:1474", true)]
    [InlineData("openclaw:room:discord:9999", false)]
    [InlineData("cloud:session_1", true)]
    [InlineData("cloud:session_2", false)]
    public void EachSourceAnswersForItsOwnIds(string id, bool expected)
    {
        Assert.Equal(expected, Listed(id));
    }

    [Fact]
    public void ARemoteSessionIsJudgedByThePeerItRanOn()
    {
        Assert.True(Listed("rc:work:abc", "mini"));
        Assert.False(Listed("rc:work:ended", "mini"));
    }

    [Theory]
    [InlineData(null)]        // a hand-edited entry with no peer
    [InlineData("quiet")]     // the peer lists nothing: off, asleep, disconnected
    [InlineData("unknown")]   // the peer is not answering at all
    public void ARemoteSessionWhosePeerCannotSayIsNotJudged(string? peer)
    {
        Assert.Null(Listed("rc:work:abc", peer));
    }

    [Fact]
    public void AnUnknownPrefixIsNeverJudged()
    {
        Assert.Null(Listed("future:thing"));
    }

    [Fact]
    public void EverySourceThatCannotSayAnswersNull()
    {
        Assert.Null(HiddenOrbs.Listed("local-1", null, null, Gateway, Cloud, Remote));
        Assert.Null(HiddenOrbs.Listed("openclaw:agent:main:main", null, Local, null, Cloud, Remote));
        Assert.Null(HiddenOrbs.Listed("openclaw:room:discord:1474", null, Local, null, Cloud, Remote));
        Assert.Null(HiddenOrbs.Listed("cloud:session_1", null, Local, Gateway, null, Remote));
        Assert.Null(HiddenOrbs.Listed("rc:work:abc", "mini", Local, Gateway, Cloud, null));
    }

    // --- the tray's half -----------------------------------------------------

    private static TrayController.SessionEntry Entry(string state, bool hidden) =>
        new("6a6fcb43-fa28", new SessionStatus { Title = "t", State = state }, hidden);

    [Fact]
    public void AHiddenTrayEntryWearsTheMarkerAfterItsLiveState()
    {
        Assert.Equal("t — needs you (hidden)", TrayController.SessionLabel(Entry("waiting", true), false));
        Assert.Equal("t — needs you", TrayController.SessionLabel(Entry("waiting", false), false));
    }

    [Fact]
    public void AHiddenSessionDoesNotCountTowardsTheIcon()
    {
        var counts = TrayController.IconCounts(new[]
        {
            Entry("waiting", true),
            Entry("generating", true),
            Entry("idle", false)
        });

        // Exactly what one visible idle session alone gives.
        Assert.Equal((1, 0, 0), counts);
        Assert.Equal((2, 1, 1), TrayController.IconCounts(new[]
        {
            Entry("waiting", false), Entry("generating", false)
        }));
    }
}
