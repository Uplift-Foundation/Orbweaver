using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-276 QA: the cases HiddenOrbsTests is least likely to think of. Pure
// functions only, no settings, no windows.
public class HiddenOrbsAdversarialTests
{
    private static (string, string?) S(string id, string? lead = null) => (id, lead);

    [Fact]
    public void EmptyLeadStringsDoNotLinkEverythingTogether()
    {
        // "" means "no lead". If it were treated as an id, hiding a session
        // with an empty id would hide every lead-less session.
        var r = HiddenOrbs.Effective(new[] { "" }, new[] { S("a", ""), S("b", null) });
        Assert.DoesNotContain("a", r);
        Assert.DoesNotContain("b", r);
    }

    [Fact]
    public void ASessionThatLeadsItselfTerminatesAndIsNotHiddenByItself()
    {
        var r = HiddenOrbs.Effective(new[] { "x" }, new[] { S("a", "a") });
        Assert.DoesNotContain("a", r);
    }

    [Fact]
    public void ATwoCycleWithNoExplicitEntryHidesNothing()
    {
        var r = HiddenOrbs.Effective(new[] { "unrelated" }, new[] { S("a", "b"), S("b", "a") });
        Assert.Equal(new[] { "unrelated" }, r.ToArray());
    }

    [Fact]
    public void ACycleReachedFromAHiddenLeadHidesTheWholeCycle()
    {
        // top is hidden; a and b lead each other but b answers to top.
        var r = HiddenOrbs.Effective(new[] { "top" },
            new[] { S("a", "b"), S("b", "top"), S("c", "a") });
        Assert.Contains("a", r);
        Assert.Contains("b", r);
        Assert.Contains("c", r);
    }

    [Fact]
    public void ALongChainHidesEveryDescendantAndNoSibling()
    {
        var sessions = new List<(string, string?)> { S("l0") };
        for (var i = 1; i <= 200; i++) sessions.Add(S("l" + i, "l" + (i - 1)));
        sessions.Add(S("sibling", null));
        var r = HiddenOrbs.Effective(new[] { "l0" }, sessions);
        Assert.Equal(201, r.Count);
        Assert.DoesNotContain("sibling", r);
    }

    [Fact]
    public void HidingAMiddleMemberHidesItsSubtreeButNotItsLeadOrSiblings()
    {
        var r = HiddenOrbs.Effective(new[] { "mid" },
            new[] { S("lead"), S("mid", "lead"), S("sib", "lead"), S("leaf", "mid") });
        Assert.Contains("mid", r);
        Assert.Contains("leaf", r);
        Assert.DoesNotContain("lead", r);
        Assert.DoesNotContain("sib", r);
    }

    [Fact]
    public void LeadMatchingIsCaseInsensitiveBothWays()
    {
        var r = HiddenOrbs.Effective(new[] { "LEAD" }, new[] { S("m", "lead") });
        Assert.Contains("m", r);
        var r2 = HiddenOrbs.Effective(new[] { "lead" }, new[] { S("M", "LEAD") });
        Assert.Contains("m", r2);
    }

    [Fact]
    public void AnExplicitIdForAnAbsentSessionIsStillReturned()
    {
        var r = HiddenOrbs.Effective(new[] { "gone" }, Array.Empty<(string, string?)>());
        Assert.Contains("gone", r);
    }

    [Fact]
    public void TheInputCollectionIsNotMutated()
    {
        var explicitIds = new List<string> { "lead" };
        HiddenOrbs.Effective(explicitIds, new[] { S("m", "lead") });
        Assert.Equal(new[] { "lead" }, explicitIds);
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode, "lead", "lead")]
    [InlineData(SessionSource.RemoteControl, "lead", "lead")]
    [InlineData(SessionSource.Codex, "lead", "")]
    [InlineData(SessionSource.Grok, "lead", "")]
    [InlineData(SessionSource.OpenClaw, "room", "")]
    [InlineData(SessionSource.ClaudeCloud, "lead", "")]
    [InlineData(SessionSource.ClaudeCode, null, "")]
    public void OnlyAgentTeamSourcesPropagate(SessionSource source, string? lead, string expected) =>
        Assert.Equal(expected, HiddenOrbs.PropagatingLead(source, lead));

    [Fact]
    public void EverySessionSourceIsDecidedExplicitly()
    {
        // A new source added later must make somebody choose; this fails
        // loudly if the enum grows without PropagatingLead being revisited.
        var known = new[]
        {
            SessionSource.ClaudeCode, SessionSource.Codex, SessionSource.Grok,
            SessionSource.OpenClaw, SessionSource.RemoteControl, SessionSource.ClaudeCloud
        };
        Assert.Equal(known.OrderBy(x => x), Enum.GetValues<SessionSource>().OrderBy(x => x));
    }

    // ---- DuePrunes: the negative controls -------------------------------

    private static readonly DateTime T0 = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASourceThatCannotSayForAnHourPrunesNothing()
    {
        var since = new Dictionary<string, DateTime>();
        for (var m = 0; m <= 60; m++)
        {
            var due = HiddenOrbs.DuePrunes(new[] { "a" }, _ => null, since,
                T0.AddMinutes(m), HiddenOrbs.PruneGrace);
            Assert.Empty(due);
        }
        Assert.Empty(since);
    }

    [Fact]
    public void FalseHeldForGraceMinusATickDoesNotPruneAndAtGraceDoes()
    {
        var since = new Dictionary<string, DateTime>();
        Assert.Empty(HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since, T0, HiddenOrbs.PruneGrace));
        Assert.Empty(HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since,
            T0 + HiddenOrbs.PruneGrace - TimeSpan.FromMilliseconds(1), HiddenOrbs.PruneGrace));
        var due = HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since,
            T0 + HiddenOrbs.PruneGrace, HiddenOrbs.PruneGrace);
        Assert.Equal(new[] { "a" }, due);
        Assert.Empty(since);
    }

    [Fact]
    public void ASingleNullInTheMiddleRestartsTheClock()
    {
        var since = new Dictionary<string, DateTime>();
        bool? answer = false;
        HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0, HiddenOrbs.PruneGrace);
        answer = null;
        HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0.AddSeconds(90), HiddenOrbs.PruneGrace);
        answer = false;
        // 130 s after the first false but only 40 s after the restart.
        Assert.Empty(HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0.AddSeconds(130), HiddenOrbs.PruneGrace));
        Assert.Empty(HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0.AddSeconds(249), HiddenOrbs.PruneGrace));
        Assert.Single(HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0.AddSeconds(250), HiddenOrbs.PruneGrace));
    }

    [Fact]
    public void ATrueResetsTheClockToo()
    {
        var since = new Dictionary<string, DateTime>();
        bool? answer = false;
        HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0, HiddenOrbs.PruneGrace);
        answer = true;
        HiddenOrbs.DuePrunes(new[] { "a" }, _ => answer, since, T0.AddSeconds(60), HiddenOrbs.PruneGrace);
        Assert.Empty(since);
    }

    [Fact]
    public void OneIdsAbsenceNeverPrunesAnotherAndDuplicatesCountOnce()
    {
        var since = new Dictionary<string, DateTime>();
        var ids = new[] { "a", "A", "b" };
        Func<string, bool?> listed = id => id.Equals("a", StringComparison.OrdinalIgnoreCase) ? false : true;
        HiddenOrbs.DuePrunes(ids, listed, since, T0, HiddenOrbs.PruneGrace);
        var due = HiddenOrbs.DuePrunes(ids, listed, since, T0 + HiddenOrbs.PruneGrace, HiddenOrbs.PruneGrace);
        Assert.Single(due);
        Assert.Equal("a", due[0], ignoreCase: true);
    }

    [Fact]
    public void ARestoredIdsClockIsForgottenSoAReHideStartsFresh()
    {
        var since = new Dictionary<string, DateTime>();
        HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since, T0, HiddenOrbs.PruneGrace);
        HiddenOrbs.DuePrunes(Array.Empty<string>(), _ => false, since, T0.AddSeconds(30), HiddenOrbs.PruneGrace);
        Assert.Empty(since);
        // Re-hidden at +10 min: must not be due on the strength of the old clock.
        Assert.Empty(HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since, T0.AddMinutes(10), HiddenOrbs.PruneGrace));
    }

    [Fact]
    public void AClockThatMovesBackwardNeverPrunes()
    {
        var since = new Dictionary<string, DateTime>();
        HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since, T0, HiddenOrbs.PruneGrace);
        Assert.Empty(HiddenOrbs.DuePrunes(new[] { "a" }, _ => false, since, T0.AddHours(-1), HiddenOrbs.PruneGrace));
    }

    [Fact]
    public void TheListedCallbackIsAskedOncePerDistinctId()
    {
        var asked = new List<string>();
        HiddenOrbs.DuePrunes(new[] { "a", "A", "a" }, id => { asked.Add(id); return true; },
            new Dictionary<string, DateTime>(), T0, HiddenOrbs.PruneGrace);
        Assert.Single(asked);
    }

    // ---- Describe / WhenText -------------------------------------------

    [Fact]
    public void ATeamMembersRowIsToldApartByItsAgentNotItsInheritedTitle()
    {
        var a = HiddenOrbs.Describe("m1", new SessionStatus { Title = "ship it", Agent = "tester", Cwd = "/x/y" }, T0, null);
        var b = HiddenOrbs.Describe("m2", new SessionStatus { Title = "ship it", Agent = "builder", Cwd = "/x/y" }, T0, null);
        Assert.Equal(a.Title, b.Title);
        Assert.NotEqual(a.Detail, b.Detail);
    }

    [Fact]
    public void AWindowsPathWithATrailingSeparatorStillNamesItsFolder()
    {
        var d = HiddenOrbs.Describe("id", new SessionStatus { Cwd = "C:\\Users\\me\\proj\\" }, T0, null);
        Assert.Equal("proj", d.Title);
    }

    [Fact]
    public void NothingToNameItByFallsBackToTheSessionId()
    {
        var d = HiddenOrbs.Describe("abc123", new SessionStatus(), T0, null);
        Assert.Equal("abc123", d.Title);
        Assert.Equal("", d.Detail);
    }

    [Fact]
    public void AWhitespaceTitleIsNotUsed()
    {
        var d = HiddenOrbs.Describe("id", new SessionStatus { Title = "   ", Cwd = "/a/proj" }, T0, null);
        Assert.Equal("proj", d.Title);
    }

    [Fact]
    public void OnlyARemoteControlSessionKeepsItsPeer()
    {
        var rc = HiddenOrbs.Describe("rc:a:b", new SessionStatus { Source = SessionSource.RemoteControl }, T0, "mini");
        Assert.Equal("mini", rc.Peer);
        Assert.Equal("mini", rc.Detail);
        var local = HiddenOrbs.Describe("id", new SessionStatus(), T0, "mini");
        Assert.Null(local.Peer);
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode, false, "Claude Code")]
    [InlineData(SessionSource.Codex, false, "Codex")]
    [InlineData(SessionSource.Grok, false, "Grok")]
    [InlineData(SessionSource.OpenClaw, false, "OpenClaw")]
    [InlineData(SessionSource.OpenClaw, true, "OpenClaw room")]
    [InlineData(SessionSource.RemoteControl, false, "Other machine")]
    [InlineData(SessionSource.ClaudeCloud, false, "Claude cloud")]
    public void EverySourceHasABadge(SessionSource source, bool room, string expected) =>
        Assert.Equal(expected, HiddenOrbs.SourceLabel(new SessionStatus { Source = source, IsRoom = room }));

    [Fact]
    public void ABackgroundJobIsLabelledAsOne()
    {
        Assert.Equal("Background job", HiddenOrbs.SourceLabel(
            new SessionStatus { Shape = LocalSessionShape.Background }));
    }

    [Theory]
    [InlineData(0, "hidden just now")]
    [InlineData(59, "hidden just now")]
    [InlineData(60, "hidden 1 min ago")]
    [InlineData(3599, "hidden 59 min ago")]
    [InlineData(3600, "hidden 1 h ago")]
    [InlineData(86399, "hidden 23 h ago")]
    [InlineData(86400, "hidden 1 day ago")]
    [InlineData(172800, "hidden 2 days ago")]
    [InlineData(-3600, "hidden just now")]
    public void WhenTextBoundaries(int ageSeconds, string expected) =>
        Assert.Equal(expected, HiddenOrbs.WhenText(T0.AddSeconds(-ageSeconds), T0));

    [Fact]
    public void AnUnknownTimeSaysNothingAtAll() =>
        Assert.Equal("", HiddenOrbs.WhenText(DateTime.MinValue, T0));

    [Fact]
    public void MenuHeaderBothArms()
    {
        Assert.Equal("Hide this orb", HiddenOrbs.MenuHeader(false));
        Assert.Equal("Hide this orb and its team", HiddenOrbs.MenuHeader(true));
    }
}
