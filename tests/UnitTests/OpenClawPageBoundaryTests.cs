using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 stage 3b: a run's steps and its rollup can sit on two chat.history pages,
// and a page whose first run is cut defers which steps a kept rollup repeats. The
// seam is where that is decided, in PrependHistory, over the combined span: the
// older page's trailing steps plus the transcript's lead steps against the
// transcript's first rollup, by the same rule a whole page uses. These are the
// cases at the turn level; OpenClawRealPageTests runs every split of every page.
[Collection("Settings")]
public class OpenClawPageBoundaryTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static OpenClawChatSession Session() => new("openclaw:" + Key, Key, "main");

    private static HistoryTurn H(ChatRole role, string text, bool fromRollup = false) =>
        new(role, text, null, "", T0, null, null, FromRollup: fromRollup);

    private static HistoryTurn WithPicture(ChatRole role, string text, bool fromRollup = false) =>
        new(role, text, "/route", "p.png", T0, null, null, FromRollup: fromRollup);

    private static string[] Texts(OpenClawChatSession s) => s.History.Select(t => t.Text).ToArray();

    private static List<(int From, int Count)> Removals(OpenClawChatSession s)
    {
        var removed = new List<(int, int)>();
        s.TurnsRemoved += (f, c) => removed.Add((f, c));
        return removed;
    }

    private static ChatTurn? PageLast(OpenClawChatSession s) =>
        (ChatTurn?)typeof(OpenClawChatSession).GetField("_pageLast", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s);

    // ---- the rollup is covered: it goes ---------------------------------------

    [Fact]
    public void AHeadRollupCoveredByTheOlderPagesTrailingStepsIsRemoved()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1\n\nstep3", fromRollup: true), H(ChatRole.User, "next") });
        var removed = Removals(session);
        var prepended = -1;
        session.HistoryPrepended += n => prepended = n;

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1"), H(ChatRole.System, "· exec"), H(ChatRole.Assistant, "step3")
        });

        Assert.Equal(new[] { "go", "step1", "· exec", "step3", "next" }, Texts(session));
        Assert.Equal(new[] { (0, 1) }, removed.ToArray());
        Assert.Equal(4, prepended);
    }

    [Fact]
    public void ARollupBehindALaterStepIsRemovedWhenStepsOnBothSidesCoverIt()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "step3"), H(ChatRole.Assistant, "step1\n\nstep3", fromRollup: true), H(ChatRole.User, "next")
        });
        var removed = Removals(session);

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1"), H(ChatRole.System, "· exec")
        });

        Assert.Equal(new[] { "go", "step1", "· exec", "step3", "next" }, Texts(session));
        Assert.Equal(new[] { (1, 1) }, removed.ToArray());
    }

    // The rollup yields a picture turn ahead of its text turn: the picture is not
    // compared and stays, the text turn is the one judged and removed.
    [Fact]
    public void ACoveredRollupsTextTurnIsRemovedAndItsPictureTurnStays()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "", fromRollup: true), H(ChatRole.Assistant, "look", fromRollup: true), H(ChatRole.User, "next")
        });
        var removed = Removals(session);

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "look"), H(ChatRole.System, "· exec")
        });

        Assert.Equal(new[] { "go", "look", "· exec", "", "next" }, Texts(session));
        Assert.Equal(new[] { (1, 1) }, removed.ToArray());
    }

    // A covered rollup turn that carries a picture keeps its row and loses only
    // its caption, in place, with TurnUpdated.
    [Fact]
    public void ACoveredRollupTurnCarryingAPictureKeepsThePictureAndLosesItsCaption()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { WithPicture(ChatRole.Assistant, "look", fromRollup: true) });
        var changed = new List<string?>();
        session.History[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var updated = 0;
        session.TurnUpdated += _ => updated++;
        var removed = Removals(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "look") });

        Assert.Equal(2, session.History.Count);
        Assert.Equal("", session.History[1].Text);
        Assert.Equal("/route", session.History[1].ImageUrl);
        Assert.Contains("Text", changed);
        Assert.Equal(1, updated);
        Assert.Empty(removed);
    }

    // ---- the rollup holds more: the steps it contains go ----------------------

    [Fact]
    public void AKeptRollupDropsTheOlderPagesTrailingStepsItContains()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a\n\nb\n\nand the answer", fromRollup: true) });
        var removed = Removals(session);
        var prepended = -1;
        session.HistoryPrepended += n => prepended = n;

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "a"), H(ChatRole.System, "· exec"), H(ChatRole.Assistant, "b")
        });

        // The tool line stays between the (now absent) steps; the rollup says it all once.
        Assert.Equal(new[] { "go", "· exec", "a\n\nb\n\nand the answer" }, Texts(session));
        Assert.Empty(removed);
        Assert.Equal(2, prepended);
    }

    [Fact]
    public void AKeptRollupDropsTheLeadStepsItContainsAndTheOlderOnesInOneSeam()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "c"), H(ChatRole.Assistant, "a\n\nc\n\nmore", fromRollup: true), H(ChatRole.User, "next")
        });
        var removed = Removals(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), H(ChatRole.Assistant, "a") });

        Assert.Equal(new[] { "go", "a\n\nc\n\nmore", "next" }, Texts(session));
        Assert.Equal(new[] { (0, 1) }, removed.ToArray());
    }

    [Fact]
    public void RemovalsAreHighestFirstOneEventPerContiguousRun()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "x"), H(ChatRole.System, "· tool"), H(ChatRole.Assistant, "y"), H(ChatRole.Assistant, "z"),
            H(ChatRole.Assistant, "x\n\ny\n\nz\n\nmore", fromRollup: true), H(ChatRole.User, "next")
        });
        var removed = Removals(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go") });

        Assert.Equal(new[] { (2, 2), (0, 1) }, removed.ToArray());
        Assert.Equal(new[] { "go", "· tool", "x\n\ny\n\nz\n\nmore", "next" }, Texts(session));
    }

    // A step that carries a picture is never removed: lead rows blank in place,
    // older turns keep their picture with an empty caption.
    [Fact]
    public void ACoveredLeadStepWithAPictureKeepsItsRowAndLosesItsCaption()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            WithPicture(ChatRole.Assistant, "c"), H(ChatRole.Assistant, "c\n\nmore", fromRollup: true)
        });
        var removed = Removals(session);
        var updated = 0;
        session.TurnUpdated += _ => updated++;

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go") });

        Assert.Equal(new[] { "go", "", "c\n\nmore" }, Texts(session));
        Assert.Equal(1, updated);
        Assert.Empty(removed);
    }

    [Fact]
    public void ACoveredOlderStepWithAPictureKeepsItsPictureAndLosesItsCaption()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a\n\nmore", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), WithPicture(ChatRole.Assistant, "a") });

        Assert.Equal(new[] { "go", "", "a\n\nmore" }, Texts(session));
        Assert.Equal("/route", session.History[1].ImageUrl);
    }

    // ---- nothing to decide ------------------------------------------------------

    [Fact]
    public void ARollupContainingNoStepChangesNothing()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "the real answer", fromRollup: true) });
        var removed = Removals(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), H(ChatRole.Assistant, "let me look"), H(ChatRole.System, "· exec") });

        Assert.Equal(new[] { "go", "let me look", "· exec", "the real answer" }, Texts(session));
        Assert.Empty(removed);
    }

    [Fact]
    public void ATranscriptWhoseHeadIsAUserTurnIsLeftAlone()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.User, "q"), H(ChatRole.Assistant, "a", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a") });

        Assert.Equal(new[] { "a", "q", "a" }, Texts(session));
    }

    [Fact]
    public void ATranscriptWithNoRollupIsLeftAlone()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step3") });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1") });

        Assert.Equal(new[] { "step1", "step3" }, Texts(session));
    }

    [Fact]
    public void AnEmptyTranscriptHasNoSeam()
    {
        var session = Session();

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "step1") });

        Assert.Single(session.History);
    }

    [Fact]
    public void ARollupWithNoStepsOnEitherSideIsKept()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "only", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go") });

        Assert.Equal(new[] { "go", "only" }, Texts(session));
    }

    // An uncovered picture caption is left alone.
    [Fact]
    public void AnUncoveredRollupCaptionOnAPictureTurnIsLeftAlone()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { WithPicture(ChatRole.Assistant, "something new", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "look") });

        Assert.Equal("something new", session.History[1].Text);
    }

    // ---- the page's last row is held by reference -------------------------------

    [Fact]
    public void RemovingThePagesLastRowMovesItToTheRowBeforeIt()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "tool-less lead"), H(ChatRole.Assistant, "a\n\ntool-less lead", fromRollup: true) });
        var lead = session.History[0];
        Assert.Same(session.History[1], PageLast(session));

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), H(ChatRole.Assistant, "a") });

        // Covered by both sides: the rollup (the page's last row) is gone.
        Assert.Same(lead, PageLast(session));
    }

    [Fact]
    public void RemovingThePagesOnlyRowLeavesNoPageLast()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "a\n\nb", fromRollup: true) });

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), H(ChatRole.Assistant, "a"), H(ChatRole.Assistant, "b") });

        Assert.Null(PageLast(session));
    }
}
