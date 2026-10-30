using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 stage 3b: a run's steps and its rollup can sit on two chat.history pages.
// Each page drops, on its own, the steps a kept rollup on it contains. What it
// cannot do is drop the steps of that rollup that sit on an older page, so
// PrependHistory does it when the older page arrives: the older page's trailing-run
// steps (OpenTail) that the transcript's head rollup (SeamRollup) contains. Both
// sets come from the pages' raw messages. These are the cases at the turn level;
// OpenClawRealPageTests runs every split of every page.
[Collection("Settings")]
public class OpenClawPageBoundaryTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static OpenClawChatSession Session() => new("openclaw:" + Key, Key, "main");

    private static HistoryTurn H(ChatRole role, string text, bool fromRollup = false, bool openTail = false, bool seam = false) =>
        new(role, text, null, "", T0, null, null, FromRollup: fromRollup, OpenTail: openTail, SeamRollup: seam);

    private static HistoryTurn Picture(string text, bool openTail = false) =>
        new(ChatRole.Assistant, text, "/route", "p.png", T0, null, null, OpenTail: openTail);

    private static HistoryTurn Head(string text) => H(ChatRole.Assistant, text, fromRollup: true, seam: true);

    private static string[] Texts(OpenClawChatSession s) => s.History.Select(t => t.Text).ToArray();

    private static List<(int From, int Count)> Removals(OpenClawChatSession s)
    {
        var removed = new List<(int, int)>();
        s.TurnsRemoved += (f, c) => removed.Add((f, c));
        return removed;
    }

    private static void ForgetSeam(OpenClawChatSession s, ChatTurn? to) =>
        typeof(OpenClawChatSession).GetField("_seamRollup", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, to);

    // ---- the older page's trailing steps the head rollup contains go ---------------

    [Fact]
    public void OlderTailStepsTheHeadRollupContainsAreLeftOutAndNothingOnScreenMoves()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("step1\n\nstep3\n\nand the answer"), H(ChatRole.User, "next") });
        var removed = Removals(session);
        var prepended = -1;
        session.HistoryPrepended += n => prepended = n;

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1", openTail: true),
            H(ChatRole.System, "· exec"), H(ChatRole.Assistant, "step3", openTail: true)
        });

        Assert.Equal(new[] { "go", "· exec", "step1\n\nstep3\n\nand the answer", "next" }, Texts(session));
        Assert.Empty(removed);
        Assert.Equal(2, prepended);
    }

    // An exact join across the seam: the page that holds the rollup cannot know the
    // steps on the other page cover it, so the rollup stays and the steps go.
    [Fact]
    public void AnExactJoinAcrossTheSeamKeepsTheRollupAndDropsTheStepsOnTheOlderPage()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("step1\n\nstep3") });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1", openTail: true), H(ChatRole.Assistant, "step3", openTail: true)
        });

        Assert.Equal(new[] { "go", "step1\n\nstep3" }, Texts(session));
    }

    [Fact]
    public void AnOlderTailStepTheRollupDoesNotContainIsKept()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("step3 and more") });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1", openTail: true), H(ChatRole.Assistant, "step3", openTail: true)
        });

        Assert.Equal(new[] { "go", "step1", "step3 and more" }, Texts(session));
    }

    // A step that carries a picture keeps it, with an empty caption.
    [Fact]
    public void ACoveredOlderTailStepWithAPictureKeepsItsPictureAndLosesItsCaption()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("a\n\nmore") });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), Picture("a", openTail: true) });

        Assert.Equal(new[] { "go", "", "a\n\nmore" }, Texts(session));
        Assert.Equal("/route", session.History[1].ImageUrl);
    }

    // Only what the raw messages marked is compared: an older turn with the same
    // words that is not in the trailing run (an earlier run) is left alone.
    [Fact]
    public void AnOlderTurnNotMarkedAsTheTrailingRunIsLeftAloneEvenWithTheSameWords()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("a\n\nmore") });

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "a"), H(ChatRole.User, "go"), H(ChatRole.Assistant, "a", openTail: true)
        });

        Assert.Equal(new[] { "a", "go", "a\n\nmore" }, Texts(session));
    }

    // ---- nothing to compare -----------------------------------------------------

    [Fact]
    public void ATranscriptWithNoSeamRollupLeavesTheOlderPageAlone()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a\n\nmore", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), H(ChatRole.Assistant, "a", openTail: true) });

        Assert.Equal(new[] { "go", "a", "a\n\nmore" }, Texts(session));
    }

    [Fact]
    public void ASeamRollupThatIsNoLongerInTheTranscriptLeavesTheOlderPageAlone()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("a\n\nmore") });
        ForgetSeam(session, new ChatTurn { Role = ChatRole.Assistant, Text = "a\n\nmore" });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), H(ChatRole.Assistant, "a", openTail: true) });

        Assert.Equal(new[] { "go", "a", "a\n\nmore" }, Texts(session));
    }

    [Fact]
    public void AnEmptyTranscriptHasNoSeam()
    {
        var session = Session();

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1", openTail: true) });

        Assert.Single(session.History);
    }

    [Fact]
    public void SettingTheHistoryAgainForgetsTheOldSeam()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Head("a\n\nmore") });

        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "fresh") });
        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a", openTail: true) });

        Assert.Equal(new[] { "a", "fresh" }, Texts(session));
    }

    // ---- the seam moves with the head ---------------------------------------------

    [Fact]
    public void TheOlderPagesOwnCutRunBecomesTheNextSeamAndAPageWithoutOneEndsIt()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.User, "now") });

        // An older page whose first run is cut: its head rollup is the next seam.
        session.PrependHistory(new List<HistoryTurn>
        {
            Head("x\n\nmore"), H(ChatRole.User, "q")
        });

        // The page before that ends in the run's steps; the new seam covers them.
        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "first"), H(ChatRole.Assistant, "x", openTail: true) });
        Assert.Equal(new[] { "first", "x\n\nmore", "q", "now" }, Texts(session));

        // A page with no seam rollup of its own leaves nothing to compare against.
        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "plain") });
        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "x", openTail: true) });
        Assert.Equal(new[] { "x", "plain", "first", "x\n\nmore", "q", "now" }, Texts(session));
    }

    // ---- carried through the three copies -------------------------------------------------

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

    // ---- the boundary is the raw messages', not the turns' (CB-270 review) ---------
    // A user message can render to no turn at all (scaffolding). Spans read off turns
    // would run past it into a different run and judge an earlier step against a
    // later run's rollup, dropping a line the whole-page parse keeps.

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AUserMessageThatRendersNoTurnStillBoundsTheRunAtTheSeam()
    {
        var all = Json("[" +
            """{"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"alpha one stage passed"}]},""" +
            // A user message of tool output only: the parser draws no turn for it.
            """{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"Exec completed (done)"}]},""" +
            """{"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"beta two submitted"}]},""" +
            """{"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r","content":[{"type":"text","text":"alpha one stage passed\n\nbeta two submitted\n\nend of day summary"}]}""" +
            "]");

        string Slice(int from, int to) => "[" + string.Join(",", all.EnumerateArray().Skip(from).Take(to - from).Select(m => m.GetRawText())) + "]";

        var whole = OpenClawSessions.TurnsFromHistory(all, Key).Select(t => t.Text).ToArray();

        var older = Json(Slice(0, 3));
        var newer = Json(Slice(3, 4));

        // The premise: the noise message really renders to no turn.
        Assert.DoesNotContain(OpenClawSessions.TurnsFromHistory(older, Key), t => t.Role == ChatRole.User);

        var session = Session();
        session.SetHistory(OpenClawSessions.TurnsFromHistory(newer, Key, startsMidRun: true));
        session.PrependHistory(OpenClawSessions.TurnsFromHistory(older, Key));

        // The earlier run's first step survives (it is bounded off by the user
        // message), the second step is the one the rollup contains.
        Assert.Contains("alpha one stage passed", Texts(session));
        Assert.Equal(whole, Texts(session));
    }
}
