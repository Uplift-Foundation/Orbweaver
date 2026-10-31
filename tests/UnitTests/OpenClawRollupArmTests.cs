using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270: FromRollup used to be set only by TurnsFromHistory's plain-text arm.
// A page-head rollup whose text names a picture takes the named-path arm, one
// with an attachment block takes the attachment arms, and one with an inline
// image block takes the image loop; none got the flag, so the page-boundary
// drop never fired for them and the reply doubled on scroll-back. A picture-
// delivering agent hits exactly that. One case per arm, each with a control
// where the identical message is not a rollup.
public class OpenClawRollupArmTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";
    private const string Path =
        "/Users/sample/.openclaw/workspace-main/.openclaw-cli-images/"
        + "3c1f90ab7d2e4658b0c9d31a5e7f8240c6b4a9e15d38f072a1bc6e9034d5f871.jpg";

    private static string Rollup(string content) =>
        """{"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r","content":""" + content + "}";

    private static string Plain(string content) => """{"role":"assistant","content":""" + content + "}";

    private static System.Collections.Generic.List<HistoryTurn> Turns(string message) =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse("[" + message + "]").RootElement, Key);

    private static string NamedContent =>
        "[{\"type\":\"text\",\"text\":\"here you go\\nMEDIA:" + Path + "\\nenjoy\"}]";

    private static string Video => OpenClawAttachmentFixtures.VideoSuccess.Trim();

    private static string VideoContent =>
        JsonDocument.Parse(Video).RootElement.GetProperty("content").GetRawText();

    private const string FailedAttachmentContent =
        """[{"type":"text","text":"a clip for you"},{"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"sample_clip.mp4","mimeType":"video/mp4"}}]""";

    private const string InlineImageContent =
        """[{"type":"text","text":"look"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]""";

    [Fact]
    public void ThePlainTextArmMarksARollup() =>
        Assert.All(Turns(Rollup("""[{"type":"text","text":"hello"}]""")), t => Assert.True(t.FromRollup));

    [Fact]
    public void TheNamedPathArmMarksARollup()
    {
        var turns = Turns(Rollup(NamedContent));

        Assert.NotEmpty(turns);
        Assert.NotNull(turns[0].ImageUrl);
        Assert.All(turns, t => Assert.True(t.FromRollup));
    }

    [Fact]
    public void TheDeliveredAttachmentArmMarksARollup()
    {
        var turns = Turns(Rollup(VideoContent));

        Assert.True(turns[0].Attachment is not null);
        Assert.All(turns, t => Assert.True(t.FromRollup));
    }

    [Fact]
    public void TheNotDeliveredAttachmentArmsMarkARollup()
    {
        var turns = Turns(Rollup(FailedAttachmentContent));

        Assert.NotEmpty(turns);
        Assert.All(turns, t => Assert.True(t.FromRollup));
    }

    [Fact]
    public void TheInlineImageLoopMarksARollup()
    {
        var turns = Turns(Rollup(InlineImageContent));

        Assert.Contains(turns, t => t.ImageBytes is not null);
        Assert.All(turns, t => Assert.True(t.FromRollup));
    }

    // Controls: the same messages, not rollups, are never marked.
    [Fact]
    public void TheSameMessagesWithoutTheRollupMarkersAreNotMarked()
    {
        foreach (var content in new[] { NamedContent, VideoContent, FailedAttachmentContent, InlineImageContent,
                     """[{"type":"text","text":"hello"}]""" })
        {
            var turns = Turns(Plain(content));

            Assert.NotEmpty(turns);
            Assert.All(turns, t => Assert.False(t.FromRollup));
        }
    }

    // The mark does not leak across messages: the turns of the message after a
    // rollup are not marked.
    [Fact]
    public void ARollupDoesNotMarkTheNextMessagesTurns()
    {
        var page = JsonDocument.Parse("[" + Rollup(NamedContent) + "," + Plain("""[{"type":"text","text":"next"}]""") + "]").RootElement;

        var turns = OpenClawSessions.TurnsFromHistory(page, Key);

        Assert.True(turns[0].FromRollup);
        Assert.False(turns[^1].FromRollup);
        Assert.Equal("next", turns[^1].Text);
    }

    // End to end: a picture-naming rollup at the page head, under an older page
    // ending in the step that named the same picture. The rollup is kept whole,
    // picture and caption. The older step names a picture too, so it keeps its
    // picture and loses only its caption: the words show once, the pictures are
    // never lost.
    [Fact]
    public void ANamedPathRollupAtThePageHeadStaysWholeAndTheOlderStepNamingTheSamePictureIsLeftOutOnScrollBack()
    {
        var newer = JsonDocument.Parse("[" + Rollup(NamedContent) + "]").RootElement;
        var headText = OpenClawSessions.TurnsFromHistory(newer, Key)[0].Text;

        var older = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"go\"},"
            + Plain("[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(headText) + "}]") + "]").RootElement;

        var session = new OpenClawChatSession("openclaw:" + Key, Key, "main") { HasMore = false };
        session.SetHistory(OpenClawSessions.TurnsFromHistory(newer, Key, startsMidRun: true));
        session.PrependHistory(OpenClawSessions.TurnsFromHistory(older, Key));

        // The head rollup shows the picture, with its caption; the older step's
        // caption-less copy of the same file is the duplicate and is left out.
        Assert.Equal(2, session.History.Count);
        var picture = session.History[1];
        Assert.True(picture.FromRollup);
        Assert.NotNull(picture.ImageUrl);
        Assert.Contains("here you go", picture.Text);
        Assert.Equal(1, session.History.Count(t => t.ImageUrl is not null));
    }

    // ---- a covered rollup's picture survives caption-less, on every arm ----
    // (CB-270 review: the text-only drop only guarded the plain-text arm, so a
    // covered rollup reaching a picture or attachment arm drew its words twice.)

    private static string Step(string text) =>
        "{\"role\":\"assistant\",\"api\":\"anthropic-messages\",\"content\":[{\"type\":\"text\",\"text\":"
        + JsonSerializer.Serialize(text) + "}]}";

    private static List<HistoryTurn> CoveredPage(string stepText, string rollupContent) =>
        OpenClawSessions.TurnsFromHistory(
            JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"go\"}," + Step(stepText) + "," + Rollup(rollupContent) + "]").RootElement,
            Key);

    // A covered named-path rollup and the step that covers it name the same file:
    // the picture shows once, on the step, which keeps its own caption. (The rollup's
    // caption-less copy is the duplicate; see OpenClawCoveredPictureTests.)
    [Fact]
    public void ACoveredNamedPathRollupShowsItsPictureOnceOnTheStepThatNamesItToo()
    {
        var raw = "here you go\nMEDIA:" + Path + "\nenjoy";
        var turns = CoveredPage(raw, "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(raw) + "}]");

        var picture = Assert.Single(turns, t => t.ImageUrl is not null);
        Assert.False(picture.FromRollup);
        Assert.Contains("here you go", picture.Text);
        Assert.Equal(1, turns.Count(t => t.Text.Contains("here you go")));
    }

    [Fact]
    public void ACoveredDeliveredAttachmentRollupKeepsItsAttachmentWithNoCaption()
    {
        var turns = CoveredPage("a clip for you", VideoContent);

        var rollupTurn = Assert.Single(turns, t => t.FromRollup);
        Assert.NotNull(rollupTurn.Attachment);
        Assert.Equal("", rollupTurn.Text);
        Assert.Equal(1, turns.Count(t => t.Text == "a clip for you"));
    }

    [Fact]
    public void ACoveredFailedAttachmentRollupKeepsItsAttachmentWithNoCaption()
    {
        var turns = CoveredPage("a clip for you", FailedAttachmentContent);

        var rollupTurn = Assert.Single(turns, t => t.FromRollup);
        Assert.NotNull(rollupTurn.Attachment);
        Assert.Equal("", rollupTurn.Text);
        Assert.Equal(1, turns.Count(t => t.Text == "a clip for you"));
    }
}
