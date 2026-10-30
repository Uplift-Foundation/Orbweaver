using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 stage 3b: a picture is not drawn twice in one run, and that includes a
// run the page boundary cuts. A delivery-mirror (the gateway's record of having
// delivered a file) collapses into the agent's own message naming the same file
// when both are on one page; across two pages the collapse could not happen, so
// the mirror survived beside it. The seam now drops a mirror, or a covered turn
// whose caption was blanked, when a turn of the other half of the run shows the
// same file, in both directions. It never drops a turn the agent sent, and never
// the last copy.
[Collection("Settings")]
public class OpenClawSeamPictureTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private const string Dir = "/Users/sample/.openclaw/workspace-main/.openclaw-cli-images/";
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.png";

    private const string User = """{"role":"user","content":"go"}""";
    private const string Next = """{"role":"user","content":"next"}""";
    private const string Between = """{"role":"user","content":"between"}""";

    private static string Named(string file) =>
        // The agent's own message ends with the file's path, the shape the real capture has.
        "{\"role\":\"assistant\",\"api\":\"anthropic-messages\",\"content\":[{\"type\":\"text\",\"text\":\"here it is\\n\\n" + Dir + file + "\"}]}";

    private static string Mirror(string file) =>
        "{\"role\":\"assistant\",\"api\":\"openclaw-transcript\",\"provider\":\"openclaw\",\"model\":\"delivery-mirror\",\"content\":[{\"type\":\"text\",\"text\":\"" + file + "\"}]}";

    private static JsonElement Page(params string[] messages) =>
        JsonDocument.Parse("[" + string.Join(",", messages) + "]").RootElement;

    private static OpenClawChatSession Session() => new("openclaw:" + Key, Key, "main");

    // As the panel loads them: the newer page first (an older one may precede it),
    // then the older page.
    private static (OpenClawChatSession Session, List<(int From, int Count)> Removed) Load(JsonElement older, JsonElement newer)
    {
        var session = Session();
        session.SetHistory(OpenClawSessions.TurnsFromHistory(newer, Key, startsMidRun: true));

        var removed = new List<(int, int)>();
        session.TurnsRemoved += (f, c) => removed.Add((f, c));

        session.PrependHistory(OpenClawSessions.TurnsFromHistory(older, Key));
        return (session, removed);
    }

    private static string Show(OpenClawChatSession s) =>
        string.Join(" | ", s.History.Select(t => t.Role.ToString()[0] + ":" + (t.ImageUrl is null ? "" : "[pic " + t.ImageAlt + "]") + t.Text.Replace("\n", "/")));

    private static int Pictures(OpenClawChatSession s, string file) =>
        s.History.Count(t => t.ImageUrl is not null && t.ImageAlt == file);

    // ---- the premise: on one page the mirror collapses; on two pages it did not ------

    [Fact]
    public void OnOnePageTheMirrorCollapsesIntoTheNamedMessage()
    {
        var turns = OpenClawSessions.TurnsFromHistory(Page(User, Named(A), Mirror(A)), Key);

        Assert.True(turns.Count(t => t.ImageUrl is not null && t.ImageAlt == A) == 1,
            string.Join(" | ", turns.Select(t => t.Role + ":" + t.Arm + ":" + (t.ImageUrl is null ? "-" : "pic") + ":" + t.ImageAlt + ":" + t.Text.Replace("\n", "/"))));
    }

    // ---- both directions ---------------------------------------------------------------

    [Fact]
    public void ANamedMessageOnTheOlderPageRemovesTheMirrorRowOnTheNewerPage()
    {
        var (session, removed) = Load(Page(User, Named(A)), Page(Mirror(A), Next));

        Assert.Equal(1, Pictures(session, A));
        Assert.Equal(new[] { (0, 1) }, removed.ToArray());
        Assert.Contains("here it is", session.History.Single(t => t.ImageAlt == A).Text);
        Assert.Equal("next", session.History[^1].Text);
    }

    [Fact]
    public void AMirrorOnTheOlderPageIsLeftOutWhenTheNewerPageNamesTheFile()
    {
        var (session, removed) = Load(Page(User, Mirror(A)), Page(Named(A), Next));

        Assert.Equal(1, Pictures(session, A));
        Assert.Empty(removed);
        Assert.Contains("here it is", session.History.Single(t => t.ImageAlt == A).Text);
    }

    // ---- controls --------------------------------------------------------------------------

    [Fact]
    public void AMirrorThatIsTheOnlyCopyStays()
    {
        var (session, removed) = Load(Page(User, Mirror(A)), Page(Next));

        Assert.Equal(1, Pictures(session, A));
        Assert.Empty(removed);
    }

    [Fact]
    public void AMirrorOfADifferentFileStaysBesideANamedMessage()
    {
        var (session, removed) = Load(Page(User, Named(A)), Page(Mirror(B), Next));

        Assert.Equal(1, Pictures(session, A));
        Assert.Equal(1, Pictures(session, B));
        Assert.Empty(removed);
    }

    // The collapse is page-wide, not bound to a run: a user message between the
    // older page's mirror and the next page's named message does not stop the named
    // message cancelling it, as it would not on one page.
    [Fact]
    public void AMirrorOnTheOlderPageIsCancelledByANamedMessageOnTheNextPageEvenAfterAUserMessage()
    {
        var (session, _) = Load(Page(User, Mirror(A)), Page(Next, Named(A)));

        Assert.Equal(1, Pictures(session, A));
    }

    // A message the agent actually sent that names the same file is never dropped,
    // on either side, and the mirror beside it is the one that goes.
    [Fact]
    public void ARealReplyNamingTheSameFileOnBothSidesIsNeverDropped()
    {
        var (session, removed) = Load(Page(User, Named(A)), Page(Named(A), Next));

        Assert.Equal(2, Pictures(session, A));
        Assert.Empty(removed);
    }

    // ---- the collapse is page-wide and budgeted (CB-270, Ines's raw pages) ----------------

    // Two delivery records of one file, one on each page, with nothing named: two
    // genuine deliveries, as the whole page keeps them.
    [Fact]
    public void TwoMirrorRecordsOfOneFileAcrossTheSeamBothStay()
    {
        var (session, removed) = Load(Page(User, Mirror(A)), Page(Mirror(A), Next));

        Assert.Equal(2, Pictures(session, A));
        Assert.Empty(removed);
    }

    // The named message already paired with a mirror on its own page, so a second
    // record on the next page has nothing left to be cancelled by.
    [Fact]
    public void ANamedMessageThatAlreadyPairedWithAMirrorCannotCancelASecondOneOnTheNextPage()
    {
        var (session, removed) = Load(Page(User, Named(A), Mirror(A)), Page(Mirror(A), Next));

        Assert.Equal(2, Pictures(session, A));
        Assert.Empty(removed);
    }

    // A user message between the named message and its mirror does not stop the
    // collapse on one page, so it does not across the seam either: the shape of the
    // real capture, where an inter-session message sits between them.
    [Fact]
    public void AUserMessageBetweenTheNamedMessageAndItsMirrorDoesNotStopTheCollapseAcrossTheSeam()
    {
        var (session, removed) = Load(Page(User, Named(A), Next), Page(Between, Mirror(A)));

        Assert.Equal(1, Pictures(session, A));
        Assert.Single(removed);
    }

    // One unpaired named message cancels one mirror, not every mirror of the file.
    [Fact]
    public void OneNamedMessageCancelsOnlyOneOfTwoMirrorsOnTheNextPage()
    {
        var (session, removed) = Load(Page(User, Named(A)), Page(Mirror(A), Mirror(A), Next));

        Assert.Equal(2, Pictures(session, A));
        Assert.Single(removed);
    }

    // ---- covered turns across the seam -----------------------------------------------------

    private static HistoryTurn Tail(string text, string file, bool blanked = false, bool mirror = false) =>
        new(ChatRole.Assistant, text, "/route/" + file, file, DateTimeOffset.Now, null, null,
            ImageSourcePath: Dir + file, RunTail: true, Blanked: blanked, FreeNamed: !blanked && !mirror,
            Arm: mirror ? MediaSourceArm.Mirror : MediaSourceArm.None);

    private static HistoryTurn HeadPic(string text, string file, bool blanked = false, bool mirror = false) =>
        new(ChatRole.Assistant, text, "/route/" + file, file, DateTimeOffset.Now, null, null,
            ImageSourcePath: Dir + file, RunHead: true, Blanked: blanked, FreeNamed: !blanked && !mirror,
            Arm: mirror ? MediaSourceArm.Mirror : MediaSourceArm.None);

    [Fact]
    public void ABlankedOlderStepIsLeftOutWhenAHeadTurnShowsItsPath()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { HeadPic("caption", A) });

        session.PrependHistory(new List<HistoryTurn> { Tail("", A, blanked: true) });

        Assert.Single(session.History);
    }

    [Fact]
    public void ABlankedHeadTurnIsRemovedWhenAnOlderTurnShowsItsPath()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { HeadPic("", A, blanked: true), new(ChatRole.User, "next", null, "", DateTimeOffset.Now, null, null) });
        var removed = new List<(int, int)>();
        session.TurnsRemoved += (f, c) => removed.Add((f, c));

        session.PrependHistory(new List<HistoryTurn> { Tail("caption", A) });

        Assert.Equal(new[] { (0, 1) }, removed.ToArray());
        Assert.Equal(new[] { "caption", "next" }, session.History.Select(t => t.Text).ToArray());
    }

    // Both sides blanked: the older copy goes and the head's stays, so one remains.
    [Fact]
    public void WhenBothSidesAreBlankedExactlyOneCopyRemains()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { HeadPic("", A, blanked: true) });
        var removed = new List<(int, int)>();
        session.TurnsRemoved += (f, c) => removed.Add((f, c));

        session.PrependHistory(new List<HistoryTurn> { Tail("", A, blanked: true) });

        Assert.Single(session.History);
        Assert.Empty(removed);
    }

    // A caption-less head turn that is not a mirror or blanked (a genuine picture)
    // is never removed.
    [Fact]
    public void AGenuineHeadPictureIsNeverRemovedEvenWhenAnOlderTurnShowsTheSameFile()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { HeadPic("", A) });

        session.PrependHistory(new List<HistoryTurn> { Tail("caption", A) });

        Assert.Equal(2, session.History.Count);
    }

    // ---- removal bookkeeping: highest first, one event per contiguous run, page-last --------

    private static ChatTurn? PageLast(OpenClawChatSession s) =>
        (ChatTurn?)typeof(OpenClawChatSession).GetField("_pageLast", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s);

    [Fact]
    public void RemovalsAreHighestFirstOneEventPerContiguousRun()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            HeadPic("", A, mirror: true),
            new(ChatRole.System, "· tool", null, "", DateTimeOffset.Now, null, null, RunHead: true),
            HeadPic("", B, mirror: true),
            HeadPic("", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc.png", mirror: true),
            new(ChatRole.User, "next", null, "", DateTimeOffset.Now, null, null)
        });
        var removed = new List<(int, int)>();
        session.TurnsRemoved += (f, c) => removed.Add((f, c));

        session.PrependHistory(new List<HistoryTurn>
        {
            Tail("one", A), Tail("two", B), Tail("three", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc.png")
        });

        Assert.Equal(new[] { (2, 2), (0, 1) }, removed.ToArray());
        Assert.Equal(new[] { "one", "two", "three", "· tool", "next" }, session.History.Select(t => t.Text).ToArray());
    }

    [Fact]
    public void RemovingThePagesLastRowMovesItToTheRowBeforeIt()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            new(ChatRole.System, "· tool", null, "", DateTimeOffset.Now, null, null, RunHead: true),
            HeadPic("", A, mirror: true)
        });
        var tool = session.History[0];
        Assert.Same(session.History[1], PageLast(session));

        session.PrependHistory(new List<HistoryTurn> { Tail("caption", A) });

        Assert.Same(tool, PageLast(session));
    }

    [Fact]
    public void RemovingThePagesOnlyRowLeavesNoPageLast()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { HeadPic("", A, mirror: true) });

        session.PrependHistory(new List<HistoryTurn> { Tail("caption", A) });

        Assert.Null(PageLast(session));
    }

    [Fact]
    public void ARowThatIsNoLongerInTheTranscriptIsNotComparedAtTheSeam()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { HeadPic("", A, mirror: true) });
        var stale = new List<ChatTurn> { new() { ImageUrl = "/route", ImageAlt = A, IsMirror = true } };
        foreach (var field in new[] { "_headRun", "_headPage" })
            typeof(OpenClawChatSession).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, stale);

        session.PrependHistory(new List<HistoryTurn> { Tail("caption", A) });

        Assert.Equal(2, session.History.Count);
    }

    // A mirror parsed on its own guesses its route (the shared media directory); the
    // whole page resolves it against the directory the agent's message names. The
    // copy that survives the seam takes that route from the named turn beside it, in
    // both directions, and the split matches the whole page.
    [Fact]
    public void ASurvivingMirrorOnTheNewerPageTakesTheRouteTheNamedTurnOnTheOlderPageHas()
    {
        var whole = OpenClawSessions.TurnsFromHistory(Page(User, Named(A), Mirror(A), Mirror(A), Next), Key);
        var (session, _) = Load(Page(User, Named(A)), Page(Mirror(A), Mirror(A), Next));

        Assert.Equal(whole.Count(t => t.ImageAlt == A && t.ImageUrl is not null), Pictures(session, A));
        Assert.All(session.History.Where(t => t.ImageUrl is not null), t => Assert.Equal(Dir + A, t.ImageSourcePath));
    }

    [Fact]
    public void ASurvivingMirrorOnTheOlderPageTakesTheRouteTheNamedTurnOnTheNewerPageHas()
    {
        var whole = OpenClawSessions.TurnsFromHistory(Page(User, Mirror(A), Mirror(A), Named(A), Next), Key);
        var (session, _) = Load(Page(User, Mirror(A), Mirror(A)), Page(Named(A), Next));

        Assert.Equal(whole.Count(t => t.ImageAlt == A && t.ImageUrl is not null), Pictures(session, A));
        Assert.All(session.History.Where(t => t.ImageUrl is not null), t => Assert.Equal(Dir + A, t.ImageSourcePath));
    }

    [Fact]
    public void AMirrorParsedAloneGuessesTheSharedMediaDirectory()
    {
        var turn = OpenClawSessions.TurnsFromHistory(Page(User, Mirror(A)), Key).Single(t => t.ImageUrl is not null);

        Assert.StartsWith(OpenClawSessions.SharedMediaDir, turn.ImageSourcePath);
    }
}
