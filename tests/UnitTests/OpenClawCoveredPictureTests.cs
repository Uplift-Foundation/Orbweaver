using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 stage 3b: a covered message (a rollup its steps cover, or a step a kept
// rollup contains) loses its words but never its picture or attachment. These pin
// that for a covered *step* on each arm that draws one (delivered attachment,
// failed attachment, named path), and that a picture is never drawn twice in one
// run: when the covered side's caption-less picture shows a path another turn of
// the same run still shows, the duplicate goes, the last copy stays, and two
// different pictures both stay.
public class OpenClawCoveredPictureTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private const string P1 =
        "/Users/sample/.openclaw/workspace-main/.openclaw-cli-images/"
        + "1111111111111111111111111111111111111111111111111111111111111111.jpg";

    private const string P2 =
        "/Users/sample/.openclaw/workspace-main/.openclaw-cli-images/"
        + "2222222222222222222222222222222222222222222222222222222222222222.jpg";

    private const string User = """{"role":"user","content":"go"}""";

    private static string Msg(string api, string content, string? key = null) =>
        "{\"role\":\"assistant\",\"api\":\"" + api + "\""
        + (key is null ? "" : ",\"idempotencyKey\":\"" + key + "\"")
        + ",\"content\":" + content + "}";

    private static string Text(string text) =>
        "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]";

    private static string Step(string content) => Msg("anthropic-messages", content);

    private static string Rollup(string content) => Msg("cli", content, "cli-assistant:r");

    private static List<HistoryTurn> Parse(params string[] messages) =>
        OpenClawSessions.TurnsFromHistory(
            JsonDocument.Parse("[" + string.Join(",", messages) + "]").RootElement, Key);

    private static string Named(string label, string path) => label + "\nMEDIA:" + path + "\nend " + label;

    // ---- a covered step keeps its picture or attachment, caption-less ---------------

    private const string DeliveredVideo =
        """{"type":"video","artifactId":"artifact_1","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc-123/full","openUrl":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc-123/full","fileName":"sample_clip.mp4","mimeType":"video/mp4","playback":"native","sizeBytes":1042813,"durationMs":12750,"width":544,"height":800}""";

    private const string FailedVideo =
        """{"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"sample_clip.mp4","mimeType":"video/mp4"}}""";

    private static string WithBlock(string text, string block) =>
        "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}," + block + "]";

    [Fact]
    public void ACoveredStepWithADeliveredAttachmentKeepsTheAttachmentAndLosesItsCaption()
    {
        var turns = Parse(User, Step(WithBlock("a clip for you", DeliveredVideo)), Rollup(Text("a clip for you\n\nand a longer reply")));

        var clip = Assert.Single(turns, t => t.Attachment is not null);
        Assert.Equal("", clip.Text);
        Assert.False(clip.FromRollup);
        Assert.Equal(1, turns.Count(t => t.Text.Contains("a clip for you")));
    }

    [Fact]
    public void ACoveredStepWithAFailedAttachmentKeepsTheAttachmentAndLosesItsCaption()
    {
        var turns = Parse(User, Step(WithBlock("a clip for you", FailedVideo)), Rollup(Text("a clip for you\n\nand a longer reply")));

        var clip = Assert.Single(turns, t => t.Attachment is not null);
        Assert.Equal("", clip.Text);
        Assert.Equal(1, turns.Count(t => t.Text.Contains("a clip for you")));
    }

    // Control: a step the rollup does not contain keeps its caption.
    [Fact]
    public void AnUncoveredStepKeepsItsAttachmentAndItsCaption()
    {
        var turns = Parse(User, Step(WithBlock("a clip for you", DeliveredVideo)), Rollup(Text("something else entirely")));

        var clip = Assert.Single(turns, t => t.Attachment is not null);
        Assert.Equal("a clip for you", clip.Text);
    }

    // ---- no repeated picture in a run ---------------------------------------------

    // A kept rollup (it adds text) holds the step it contains: both name P1. The
    // step's blanked copy is the duplicate; the rollup's copy, with its caption,
    // stays.
    [Fact]
    public void AKeptRollupAndTheCoveredStepItContainsShowTheirSharedPictureOnce()
    {
        var step = Named("here", P1);
        var turns = Parse(User, Step(Text(step)), Rollup(Text(step + "\n\nand more")));

        var picture = Assert.Single(turns, t => t.ImageSourcePath == P1);
        Assert.True(picture.FromRollup);
        Assert.Contains("and more", picture.Text);
    }

    // An equal-join rollup is covered by its step: it loses its caption, and the
    // step, which stays with its own, is the copy that shows.
    [Fact]
    public void ACoveredEqualJoinRollupAndItsStepShowTheirSharedPictureOnce()
    {
        var step = Named("here", P1);
        var turns = Parse(User, Step(Text(step)), Rollup(Text(step)));

        var picture = Assert.Single(turns, t => t.ImageSourcePath == P1);
        Assert.False(picture.FromRollup);
        Assert.Contains("here", picture.Text);
    }

    // The last copy is never removed: two covered steps with the same picture and a
    // rollup that holds both leave exactly one copy.
    [Fact]
    public void TwoCoveredStepsAndTheirRollupLeaveOneCopyOfAPictureTheyAllName()
    {
        var step = Named("here", P1);
        var turns = Parse(User, Step(Text(step)), Step(Text(step)), Rollup(Text(step + "\n\n" + step + "\n\nand more")));

        Assert.Single(turns, t => t.ImageSourcePath == P1);
    }

    // A covered step whose picture nothing else in the run shows keeps it, caption-less:
    // there is no second copy to drop, so the in-page dedupe has nothing to remove.
    [Fact]
    public void ACoveredStepWhosePictureNothingElseShowsKeepsItCaptionLess()
    {
        var step = "here it is\n\n" + P1;
        var turns = Parse(User, Step(Text(step)), Rollup(Text(step + "\n\nand more")));

        var picture = Assert.Single(turns, t => t.ImageSourcePath == P1);
        Assert.Equal("", picture.Text);
        Assert.Equal(1, turns.Count(t => t.Text.Contains("here it is")));
    }

    // An open step that spoke before it acted renders a tool line and then its words
    // (the tool line first). Only the words are the open tail's; the tool line, a
    // System turn in the same message, is not marked.
    [Fact]
    public void AnOpenStepThatSpokeBeforeItActedMarksItsWordsAndNotItsToolLine()
    {
        var content = "[{\"type\":\"text\",\"text\":\"looking now\"},"
            + "{\"type\":\"toolcall\",\"id\":\"t1\",\"name\":\"exec\",\"arguments\":{\"command\":\"ls\"}}]";
        var turns = Parse(User, Step(content));

        var tool = Assert.Single(turns, t => t.Role == ChatRole.System);
        Assert.False(tool.OpenTail);
        var words = Assert.Single(turns, t => t.Role == ChatRole.Assistant);
        Assert.True(words.OpenTail);
        Assert.Equal("looking now", words.Text);
    }

    // Control: two different pictures both stay.
    [Fact]
    public void TwoDifferentPicturesInOneRunBothStay()
    {
        var one = Named("first", P1);
        var two = Named("second", P2);
        var turns = Parse(User, Step(Text(one)), Step(Text(two)), Rollup(Text(one + "\n\n" + two + "\n\nand more")));

        var paths = turns.Select(t => t.ImageSourcePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        Assert.Contains(P1, paths);
        Assert.Contains(P2, paths);
    }

    // Control: the same file named in a different run is a second picture, not a
    // duplicate, and is left alone.
    [Fact]
    public void ThePictureNamedInAnotherRunIsNotADuplicate()
    {
        var step = Named("here", P1);
        var turns = Parse(User, Step(Text(step)), Rollup(Text(step + "\n\nand more")),
            User, Step(Text(step)), Rollup(Text(step + "\n\nand more again")));

        Assert.Equal(2, turns.Count(t => t.ImageSourcePath == P1));
    }

    // ---- at the page boundary --------------------------------------------------------

    private static HistoryTurn Tail(string text, string path) =>
        new(ChatRole.Assistant, text, "/route", "p.jpg", System.DateTimeOffset.Now, null, null,
            ImageSourcePath: path, OpenTail: true, RunTail: true);

    // The older page's trailing step ends in the picture the head rollup also shows:
    // the head's copy stays, and the older step's caption-less copy is dropped.
    [Fact]
    public void AnOlderTailStepNamingThePictureTheHeadRollupShowsIsDroppedNotBlanked()
    {
        var session = new OpenClawChatSession("openclaw:" + Key, Key, "main");
        var head = new HistoryTurn(ChatRole.Assistant, "here\nend\n\nand more", "/route", "p.jpg",
            System.DateTimeOffset.Now, null, null, FromRollup: true, SeamRollup: true, RunHead: true, ImageSourcePath: P1);
        session.SetHistory(new List<HistoryTurn> { head });

        session.PrependHistory(new List<HistoryTurn>
        {
            new(ChatRole.User, "go", null, "", System.DateTimeOffset.Now, null, null),
            Tail("here\nend", P1)
        });

        Assert.Equal(2, session.History.Count);
        Assert.Single(session.History, t => t.ImageSourcePath == P1);
    }

    // A covered older step with a different picture keeps it, caption-less.
    [Fact]
    public void AnOlderTailStepWithADifferentPictureKeepsItCaptionLess()
    {
        var session = new OpenClawChatSession("openclaw:" + Key, Key, "main");
        var head = new HistoryTurn(ChatRole.Assistant, "here\n\nand more", "/route", "p.jpg",
            System.DateTimeOffset.Now, null, null, FromRollup: true, SeamRollup: true, RunHead: true, ImageSourcePath: P1);
        session.SetHistory(new List<HistoryTurn> { head });

        session.PrependHistory(new List<HistoryTurn> { Tail("here", P2) });

        Assert.Equal(2, session.History.Count);
        Assert.Equal("", session.History[0].Text);
        Assert.Equal(P2, session.History[0].ImageSourcePath);
    }
}
