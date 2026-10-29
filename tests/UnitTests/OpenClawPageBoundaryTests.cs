using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 plan §2.1a: RollupsToDrop judges one chat.history page, and pages are
// fetched newest first and parsed independently. A page boundary that falls
// between a run's last text step and its rollup leaves the rollup at the head
// of the newer page (kept, nothing before it) and the steps at the tail of the
// older one (kept, a run "in flight"), so a scroll-back drew the reply twice.
[Collection("Settings")]
public class OpenClawPageBoundaryTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Slice(JsonElement page, int from, int to) =>
        Json("[" + string.Join(",", page.EnumerateArray().Skip(from).Take(to - from).Select(m => m.GetRawText())) + "]");

    private static OpenClawChatSession Session() =>
        new("openclaw:" + Key, Key, "main");

    // Load the newer page first, then scroll back to the older one, the way the
    // panel does: SetHistory(newer), then PrependHistory(older, flag).
    private static OpenClawChatSession LoadSplit(JsonElement page, int split)
    {
        var older = Slice(page, 0, split);
        var newer = Slice(page, split, page.GetArrayLength());

        var session = Session();
        session.SetHistory(OpenClawSessions.TurnsFromHistory(newer, Key));
        session.PrependHistory(OpenClawSessions.TurnsFromHistory(older, Key),
            OpenClawHistoryShape.EndsInOpenTextStep(older));
        return session;
    }

    // ---- EndsInOpenTextStep --------------------------------------------

    private static bool Ends(params string[] messages) =>
        OpenClawHistoryShape.EndsInOpenTextStep(Json("[" + string.Join(",", messages) + "]"));

    private const string User = """{"role":"user","content":"go"}""";
    private const string Step = """{"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"step"}]}""";
    private const string ToolOnly = """{"role":"assistant","api":"anthropic-messages","content":[{"type":"toolcall","id":"a","name":"exec","arguments":{}}]}""";
    private const string Rollup = """{"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r","content":[{"type":"text","text":"step"}]}""";

    [Fact]
    public void APageEndingInATextStepEndsOpen() => Assert.True(Ends(User, Step));

    [Fact]
    public void APageEndingInAUserTurnDoesNotEndOpen() => Assert.False(Ends(User, Step, Rollup, User));

    [Fact]
    public void APageEndingInARollupDoesNotEndOpen() => Assert.False(Ends(User, Step, Rollup));

    [Fact]
    public void APageEndingInAToolOnlyStepDoesNotEndOpen() => Assert.False(Ends(User, ToolOnly));

    [Fact]
    public void AStepAfterAnEarlierRollupEndsOpen() => Assert.True(Ends(User, Step, Rollup, Step));

    [Fact]
    public void EmptyAndNonArrayPagesDoNotEndOpen()
    {
        Assert.False(OpenClawHistoryShape.EndsInOpenTextStep(Json("[]")));
        Assert.False(OpenClawHistoryShape.EndsInOpenTextStep(Json("{}")));
    }

    // ---- TurnsFromHistory marks the rollup's turn -----------------------

    [Fact]
    public void OnlyTheKeptRollupsTurnIsMarkedFromRollup()
    {
        var turns = OpenClawSessions.TurnsFromHistory(Json(OpenClawNativeRunFixtures.ToolRunPage), Key);

        // user, step, tool, step, user, lone rollup
        Assert.Equal(new[] { false, false, false, false, false, true }, turns.Select(t => t.FromRollup).ToArray());
    }

    // ---- the real fixture, split at every index -------------------------

    [Fact]
    public void SplitAtEveryIndexTheReplyAppearsOnceAndTheTranscriptMatchesTheUnsplitOne()
    {
        var page = Json(OpenClawNativeRunFixtures.ToolRunPage);
        var whole = OpenClawSessions.TurnsFromHistory(page, Key).Select(t => (t.Role, t.Text)).ToArray();

        for (var split = 1; split < page.GetArrayLength(); split++)
        {
            var session = LoadSplit(page, split);
            var got = session.History.Select(t => (t.Role, t.Text)).ToArray();

            Assert.True(got.SequenceEqual(whole), $"split at {split}: " + string.Join(" | ", got.Select(g => g.Text.Length > 20 ? g.Text[..20] : g.Text)));
            Assert.Equal(1, session.History.Count(t => t.Text == "probe ok, 18 files"));
        }
    }

    // ---- PrependHistory --------------------------------------------------

    private static HistoryTurn H(ChatRole role, string text, bool fromRollup = false) =>
        new(role, text, null, "", new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), null, null,
            FromRollup: fromRollup);

    [Fact]
    public void AMatchingHeadRollupIsRemovedWhenTheOlderPageEndsInTheRunsSteps()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1\n\nstep3", fromRollup: true), H(ChatRole.User, "next") });
        var reported = -1;
        session.HistoryPrepended += n => reported = n;

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1"), H(ChatRole.System, "· exec"), H(ChatRole.Assistant, "step3")
        }, olderEndsInOpenTextStep: true);

        Assert.Equal(new[] { "go", "step1", "· exec", "step3", "next" }, session.History.Select(t => t.Text).ToArray());
        Assert.Equal(4, reported);
        Assert.Equal(0, session.DroppedRollupIndex);
    }

    // Control: the older page ends in a user turn, so the head rollup is a run
    // of its own and nothing is removed.
    [Fact]
    public void NothingIsRemovedWhenTheOlderPageEndsInAUserTurn()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "reply", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go") }, olderEndsInOpenTextStep: false);

        Assert.Equal(new[] { "go", "reply" }, session.History.Select(t => t.Text).ToArray());
        Assert.Equal(-1, session.DroppedRollupIndex);
    }

    // The boundary can fall mid-run: the newer page holds a later step and the
    // rollup, which carries text from steps on the older page too. The rollup is
    // not the first turn, and is covered only by steps on both sides.
    [Fact]
    public void ARollupBehindALaterStepIsDroppedWhenBothPagesStepsCoverIt()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "step3"), H(ChatRole.Assistant, "step1\n\nstep3", fromRollup: true), H(ChatRole.User, "next")
        });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1"), H(ChatRole.System, "· exec")
        }, olderEndsInOpenTextStep: true);

        Assert.Equal(new[] { "go", "step1", "· exec", "step3", "next" }, session.History.Select(t => t.Text).ToArray());
        Assert.Equal(1, session.DroppedRollupIndex);
    }

    [Fact]
    public void ARollupBehindAUserTurnIsNotTheOlderRunsRollup()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "q"), H(ChatRole.Assistant, "step1", fromRollup: true)
        });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1") }, olderEndsInOpenTextStep: true);

        Assert.Equal(3, session.History.Count);
        Assert.Equal(-1, session.DroppedRollupIndex);
    }

    // The head rollup carries text the older page's steps do not: it is the
    // reply itself and must survive the prepend (CB-270 QA).
    [Fact]
    public void AHeadRollupCarryingTextTheStepsLackIsKeptEvenWhenTheOlderPageEndsOpen()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "the real final answer", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "let me look"), H(ChatRole.System, "· exec")
        }, olderEndsInOpenTextStep: true);

        Assert.Equal(new[] { "go", "let me look", "· exec", "the real final answer" },
            session.History.Select(t => t.Text).ToArray());
        Assert.Equal(-1, session.DroppedRollupIndex);
    }

    // The trailing walk stops at a user turn and at an earlier kept rollup, so
    // steps from before them cannot make a head rollup look covered.
    [Fact]
    public void TheTrailingStepWalkStopsAtAUserTurnAndAtAKeptRollup()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "alpha beta", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "alpha"), H(ChatRole.User, "q"),
            H(ChatRole.Assistant, "beta", fromRollup: true), H(ChatRole.Assistant, "gamma")
        }, olderEndsInOpenTextStep: true);

        Assert.Equal(5, session.History.Count);
        Assert.Equal(-1, session.DroppedRollupIndex);
    }

    // A rollup that yields a picture turn ahead of its text turn: only the text
    // turn is dropped (the steps cover its words); the picture stays, because
    // nothing else shows it. A picture drawn twice is acceptable, a lost one is
    // not.
    [Fact]
    public void AHeadRollupsTextTurnIsDroppedAndItsPictureTurnStays()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "", fromRollup: true), H(ChatRole.Assistant, "look", fromRollup: true), H(ChatRole.User, "next")
        });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "look"), H(ChatRole.System, "· exec")
        }, olderEndsInOpenTextStep: true);

        Assert.Equal(new[] { "go", "look", "· exec", "", "next" }, session.History.Select(t => t.Text).ToArray());
        Assert.Equal(1, session.DroppedRollupIndex);
    }

    // A rollup turn that carries a picture is never removed. When the steps cover
    // its caption it loses the caption, in place, and the picture stays.
    [Fact]
    public void ACoveredRollupTurnCarryingAPictureKeepsThePictureAndLosesItsCaption()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            new(ChatRole.Assistant, "look", "/route", "p.png",
                new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), null, null, FromRollup: true)
        });
        var changed = new List<string?>();
        session.History[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var updated = 0;
        session.TurnUpdated += _ => updated++;

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "look") }, olderEndsInOpenTextStep: true);

        Assert.Equal(2, session.History.Count);
        Assert.Equal(-1, session.DroppedRollupIndex);
        Assert.Equal("", session.History[1].Text);
        Assert.Equal("/route", session.History[1].ImageUrl);
        Assert.Contains("Text", changed);
        Assert.Equal(1, updated);
    }

    // The caption is only blanked when it is covered.
    [Fact]
    public void AnUncoveredRollupCaptionOnAPictureTurnIsLeftAlone()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            new(ChatRole.Assistant, "something new", "/route", "p.png",
                new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), null, null, FromRollup: true)
        });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "look") }, olderEndsInOpenTextStep: true);

        Assert.Equal("something new", session.History[1].Text);
    }

    [Fact]
    public void ANonRollupHeadIsKeptEvenWhenTheOlderPageEndsOpen()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step3") });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1") }, olderEndsInOpenTextStep: true);

        Assert.Equal(new[] { "step1", "step3" }, session.History.Select(t => t.Text).ToArray());
        Assert.Equal(-1, session.DroppedRollupIndex);
    }

    [Fact]
    public void AnEmptyTranscriptHasNoHeadToDrop()
    {
        var session = Session();

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1") }, olderEndsInOpenTextStep: true);

        Assert.Single(session.History);
        Assert.Equal(-1, session.DroppedRollupIndex);
    }

    [Fact]
    public void FromRollupIsCarriedBySetHistoryPrependAndAppend()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a", fromRollup: true) });
        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "b", fromRollup: true) });
        session.AppendNewTail(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "a", fromRollup: true), H(ChatRole.Assistant, "c", fromRollup: true)
        });

        Assert.All(session.History, t => Assert.True(t.FromRollup));
        Assert.Equal(3, session.History.Count);
    }
}
