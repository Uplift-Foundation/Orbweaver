using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-274 / CB-275: AttachmentOf and the parser arm that draws what it finds.
// Fixtures and their provenance (real row vs. from the gateway's source) are
// in OpenClawAttachmentFixtures.
[Collection("Settings")]
public class OpenClawAttachmentOfTests
{
    private static JsonElement Content(string messageJson) =>
        JsonDocument.Parse(messageJson).RootElement.GetProperty("content").Clone();

    private static JsonElement Wrapped(string contentJson) =>
        JsonDocument.Parse("{\"content\":" + contentJson + "}").RootElement.GetProperty("content").Clone();

    private static System.Collections.Generic.List<HistoryTurn> Turns(params string[] messages) =>
        OpenClawSessions.TurnsFromHistory(
            JsonDocument.Parse("[" + string.Join(",", messages) + "]").RootElement.Clone(), null);

    // ---- AttachmentOf ---------------------------------------------------

    [Fact]
    public void TheRealSeqNineSevenSevenIsAFailedVideo()
    {
        var a = OpenClawSessions.AttachmentOf(Content(OpenClawAttachmentFixtures.SeqNineSevenSeven));

        Assert.NotNull(a);
        Assert.Equal(AttachmentKind.Video, a!.Value.Kind);
        Assert.Equal(OpenClawAttachmentFixtures.ClipName, a.Value.Label);
        Assert.Equal("video/mp4", a.Value.MimeType);
        Assert.Equal("delivery-failed", a.Value.FailureCode);
        Assert.True(a.Value.Failed);
        Assert.False(a.Value.Fetchable);
        Assert.Null(a.Value.Url);
    }

    [Theory]
    [InlineData("sample_pink_afternoon_video_2pm_1234567890.mp4")]
    [InlineData("sample_dawn_video_6am_1111111111.mp4")]
    [InlineData("sample_evening_video_6pm_2222222222.mp4")]
    [InlineData("sample_noon_video_2pm_3333333333.mp4")]
    [InlineData("sample_morning_video_10am_4444444444.mp4")]
    public void EveryRealFailedClipHasTheSameShapeAndOnlyTheLabelDiffers(string label)
    {
        var json = "{\"content\":[{\"type\":\"attachment_error\",\"attachment\":"
                   + "{\"code\":\"delivery-failed\",\"kind\":\"video\",\"label\":\"" + label
                   + "\",\"mimeType\":\"video/mp4\"}}]}";

        var a = OpenClawSessions.AttachmentOf(Content(json))!.Value;

        Assert.Equal(label, a.Label);
        Assert.Equal(AttachmentKind.Video, a.Kind);
        Assert.Equal("delivery-failed", a.FailureCode);
    }

    [Fact]
    public void AVideoSuccessCarriesItsUrlAndEveryOptionalField()
    {
        var a = OpenClawSessions.AttachmentOf(Content(OpenClawAttachmentFixtures.VideoSuccess))!.Value;

        Assert.Equal(AttachmentKind.Video, a.Kind);
        Assert.Equal(OpenClawAttachmentFixtures.OutgoingUrl, a.Url);
        Assert.Equal("sample_clip.mp4", a.Label);
        Assert.Equal("video/mp4", a.MimeType);
        Assert.Equal(1042813, a.SizeBytes);
        Assert.Equal(12750, a.DurationMs);
        Assert.Equal(544, a.Width);
        Assert.Equal(800, a.Height);
        Assert.Null(a.FailureCode);
        Assert.True(a.Fetchable);
        Assert.False(a.Failed);
        Assert.Equal("0:13", a.Duration);
    }

    [Fact]
    public void AVideoWithNoOptionalFieldsStillParses()
    {
        var a = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"video","url":"/x/full"}]"""))!.Value;

        Assert.Equal("full", a.Label);
        Assert.Null(a.SizeBytes);
        Assert.Null(a.DurationMs);
        Assert.Null(a.Width);
        Assert.Null(a.Height);
        Assert.Null(a.Duration);
    }

    [Fact]
    public void ALabelFallsBackFromFileNameToAltToTheUrlToTheKind()
    {
        string Label(string block) =>
            OpenClawSessions.AttachmentOf(Wrapped("[" + block + "]"))!.Value.Label;

        Assert.Equal("a.mp4", Label("""{"type":"video","url":"/u/full","fileName":"a.mp4","alt":"b.mp4"}"""));
        Assert.Equal("b.mp4", Label("""{"type":"video","url":"/u/full","alt":"b.mp4"}"""));
        Assert.Equal("my clip.mp4", Label("""{"type":"video","url":"/u/my%20clip.mp4?x=1#y"}"""));
        // No invented name: a block naming nothing keeps an empty label.
        Assert.Equal("", Label("""{"type":"video"}"""));
        Assert.Equal("", Label("""{"type":"video","url":"/u/"}"""));
        Assert.Equal("", Label("""{"type":"audio"}"""));
    }

    [Fact]
    public void ABlockTheProjectionStrippedOfItsUrlIsOmittedNotDropped()
    {
        var a = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"video","fileName":"c.mp4","omitted":true}]"""))!.Value;

        Assert.Null(a.Url);
        Assert.Equal("omitted", a.FailureCode);
        Assert.Equal("c.mp4", a.Label);
    }

    [Fact]
    public void AWhitespaceUrlIsNoUrl()
    {
        var a = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"video","url":"   ","fileName":"c.mp4"}]"""))!.Value;

        Assert.Null(a.Url);
        Assert.Equal("omitted", a.FailureCode);
    }

    [Fact]
    public void AnAudioSuccessIsAudio()
    {
        var a = OpenClawSessions.AttachmentOf(Content(OpenClawAttachmentFixtures.AudioSuccess))!.Value;

        Assert.Equal(AttachmentKind.Audio, a.Kind);
        Assert.Equal("voice_note.m4a", a.Label);
        Assert.Equal("0:42", a.Duration);
        Assert.Null(a.Width);
    }

    [Fact]
    public void ADocumentAttachmentIsADocumentWithItsSize()
    {
        var a = OpenClawSessions.AttachmentOf(Content(OpenClawAttachmentFixtures.DocumentSuccess))!.Value;

        Assert.Equal(AttachmentKind.Document, a.Kind);
        Assert.Equal("quarterly-report.pdf", a.Label);
        Assert.Equal("application/pdf", a.MimeType);
        Assert.Equal(1258291, a.SizeBytes);
        Assert.True(a.Fetchable);
    }

    [Fact]
    public void AnAttachmentWithNoUrlOrLabelIsAnOmittedFile()
    {
        var a = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"attachment","attachment":{"kind":"document"}}]"""))!.Value;

        Assert.Equal("", a.Label);
        Assert.Equal("omitted", a.FailureCode);
    }

    [Fact]
    public void AnAttachmentLabelFallsBackToTheUrlsLastSegment()
    {
        var a = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"attachment","attachment":{"kind":"document","url":"/u/notes.txt"}}]"""))!.Value;

        Assert.Equal("notes.txt", a.Label);
        Assert.Null(a.FailureCode);
    }

    [Fact]
    public void AnAttachmentErrorWithNoCodeIsADeliveryFailure()
    {
        var a = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"attachment_error","attachment":{"kind":"audio","label":"n.m4a"}}]"""))!.Value;

        Assert.Equal("delivery-failed", a.FailureCode);
        Assert.Equal(AttachmentKind.Audio, a.Kind);

        var blank = OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"attachment_error","attachment":{"code":"  ","label":"n.m4a"}}]"""))!.Value;
        Assert.Equal("delivery-failed", blank.FailureCode);
    }

    [Fact]
    public void AnAttachmentErrorKeepsItsOwnCode()
    {
        var a = OpenClawSessions.AttachmentOf(Content(OpenClawAttachmentFixtures.DocumentError))!.Value;

        Assert.Equal("unsupported-format", a.FailureCode);
        Assert.Equal(AttachmentKind.Document, a.Kind);
    }

    [Fact]
    public void AWrapperThatIsNotAnObjectIsIgnored()
    {
        Assert.Null(OpenClawSessions.AttachmentOf(
            Wrapped("""[{"type":"attachment","attachment":"nope"},{"type":"attachment_error","attachment":3}]""")));
    }

    [Fact]
    public void ImageBlocksAreLeftToThePictureBranch()
    {
        Assert.Null(OpenClawSessions.AttachmentOf(Content(OpenClawAttachmentFixtures.SeqTenEighteen)));
    }

    [Fact]
    public void ContentThatIsNotABlockListHasNoAttachment()
    {
        Assert.Null(OpenClawSessions.AttachmentOf(JsonDocument.Parse("\"a string\"").RootElement));
        Assert.Null(OpenClawSessions.AttachmentOf(JsonDocument.Parse("{\"text\":\"x\"}").RootElement));
        Assert.Null(OpenClawSessions.AttachmentOf(Wrapped("""[{"type":"text","text":"just words"}]""")));
    }

    [Fact]
    public void TheFirstAttachmentBlockWins()
    {
        var a = OpenClawSessions.AttachmentOf(Wrapped(
            """[{"type":"video","url":"/a/full","fileName":"first.mp4"},{"type":"audio","url":"/b/full","fileName":"second.m4a"}]"""))!.Value;

        Assert.Equal("first.mp4", a.Label);
    }

    [Theory]
    [InlineData("image", null, "x", AttachmentKind.Image)]
    [InlineData("VIDEO", null, "x", AttachmentKind.Video)]
    [InlineData("audio", null, "x", AttachmentKind.Audio)]
    [InlineData("document", null, "x", AttachmentKind.Document)]
    [InlineData("media", "video/mp4", "x", AttachmentKind.Video)]
    [InlineData("media", "audio/mpeg", "x", AttachmentKind.Audio)]
    [InlineData(null, "image/png", "x", AttachmentKind.Image)]
    [InlineData(null, null, "clip.MOV", AttachmentKind.Video)]
    [InlineData(null, null, "pic.webp", AttachmentKind.Image)]
    [InlineData(null, "application/pdf", "report.pdf", AttachmentKind.Document)]
    [InlineData("media", null, "mystery", AttachmentKind.Document)]
    public void KindFallsBackFromTheKindToTheMimeToTheExtension(
        string? kind, string? mime, string label, AttachmentKind expected) =>
        Assert.Equal(expected, OpenClawSessions.KindFrom(kind, mime, label));

    [Theory]
    [InlineData("clip.mp4", "clip.mp4")]
    [InlineData("a/b/clip.mp4", "clip.mp4")]
    [InlineData("a\\b\\clip.mp4", "clip.mp4")]
    [InlineData("  clip.mp4 ", "clip.mp4")]
    [InlineData("", null)]
    [InlineData("dir/", null)]
    [InlineData("..", null)]
    [InlineData("a/..", null)]
    [InlineData(".", null)]
    [InlineData("a/..%2f..", "..%2f..")]
    [InlineData("my..clip.mp4", "my..clip.mp4")]
    [InlineData("a/b..c", "b..c")]
    public void ALabelIsReducedToABareNameBeforeItIsJoinedToADirectory(string label, string? expected) =>
        Assert.Equal(expected, OpenClawSessions.BareNameOf(label));

    // ---- the parser arm -------------------------------------------------

    [Fact]
    public void SeqNineSevenSevenIsOneCaptionTurnCarryingTheFailedClip()
    {
        var turn = Assert.Single(Turns(OpenClawAttachmentFixtures.SeqNineSevenSeven));

        // The caption exactly, never with the filename appended: AppendNewTail
        // anchors the live tail on (Role, Text).
        Assert.Equal(OpenClawAttachmentFixtures.Caption, turn.Text);
        Assert.Equal(OpenClawAttachmentFixtures.ClipName, turn.ImageAlt);
        Assert.EndsWith(OpenClawAttachmentFixtures.ClipName, turn.ImageSourcePath);
        Assert.NotNull(turn.ImageUrl);
        Assert.Equal(OpenClawAttachmentFixtures.JobId, turn.Automation!.Value.JobId);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
        Assert.Equal("delivery-failed", turn.Attachment!.Value.FailureCode);
        Assert.False(turn.Attachment.Value.Fetchable);
        Assert.Equal(MediaSourceArm.None, turn.Arm);
    }

    [Fact]
    public void TheSameMessageInRawFormIsStillOneTurnThroughTheNamedArm()
    {
        var turn = Assert.Single(Turns(OpenClawAttachmentFixtures.SeqNineSevenSevenRaw));

        Assert.Null(turn.Attachment);
        Assert.EndsWith(OpenClawAttachmentFixtures.ClipName, turn.ImageSourcePath);
        Assert.Equal(OpenClawAttachmentFixtures.JobId, turn.Automation!.Value.JobId);
    }

    [Fact]
    public void ADeliveredClipIsFetchedFromItsOutgoingUrlWithNoLocalPath()
    {
        var turn = Assert.Single(Turns(OpenClawAttachmentFixtures.VideoSuccess));

        Assert.Equal("a clip for you", turn.Text);
        Assert.Equal(OpenClawAttachmentFixtures.OutgoingUrl, turn.ImageUrl);
        Assert.Null(turn.ImageSourcePath);
        Assert.Equal("sample_clip.mp4", turn.ImageAlt);
        Assert.Equal(AttachmentKind.Video, turn.Attachment!.Value.Kind);

        var chat = new ChatTurn { Attachment = turn.Attachment, ImageSourcePath = turn.ImageSourcePath };
        Assert.True(chat.IsVideo);
    }

    [Fact]
    public void AMediaOnlyMessageStillDrawsItsAttachment()
    {
        var turn = Assert.Single(Turns(OpenClawAttachmentFixtures.AudioSuccess));

        Assert.Equal("", turn.Text);
        Assert.Equal(AttachmentKind.Audio, turn.Attachment!.Value.Kind);
        Assert.Equal("voice_note.m4a", turn.ImageAlt);
    }

    [Fact]
    public void AMessageWithNeitherTextNorAttachmentIsStillSkipped()
    {
        Assert.Empty(Turns("""{"role":"assistant","content":[{"type":"text","text":"  "}]}"""));
    }

    [Fact]
    public void AFailedAttachmentWithAnUnusableLabelDrawsTheCaptionOnly()
    {
        var turn = Assert.Single(Turns("""
        {"role":"assistant","content":[
          {"type":"text","text":"here"},
          {"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":".."}}]}
        """));

        Assert.Equal("here", turn.Text);
        Assert.Null(turn.ImageUrl);
        Assert.Null(turn.ImageSourcePath);
        Assert.Equal("..", turn.ImageAlt);
        Assert.True(turn.Attachment!.Value.Failed);
    }

    [Fact]
    public void AFailedPictureIsAPictureShapedTurnWithAnAssistantMediaRoute()
    {
        var turn = Assert.Single(Turns("""
        {"role":"assistant","content":[
          {"type":"attachment_error","attachment":{"code":"file-not-found","kind":"image","label":"sample.png"}}]}
        """));

        Assert.Equal(AttachmentKind.Image, turn.Attachment!.Value.Kind);
        Assert.StartsWith(OpenClawSessions.AssistantMediaPathPrefix, turn.ImageUrl);
        Assert.EndsWith("sample.png", turn.ImageSourcePath);
    }

    [Fact]
    public void AFailedAttachmentAndADeliveryMirrorOfTheSameFileAreOneBubble()
    {
        var turns = Turns(
            """
            {"role":"assistant","content":[
              {"type":"text","text":"clip"},
              {"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"dup.mp4"}}]}
            """,
            """{"role":"assistant","provider":"openclaw","model":"delivery-mirror","content":"dup.mp4"}""");

        var turn = Assert.Single(turns);
        Assert.Equal("clip", turn.Text);
        Assert.True(turn.Attachment!.Value.Failed);
    }

    [Fact]
    public void TheFullPageInTheGatewaysEnvelopeYieldsSixTurnsInOrder()
    {
        var turns = Turns(
            OpenClawAttachmentFixtures.SeqNineSevenSeven,
            OpenClawAttachmentFixtures.SeqTenEighteen,
            OpenClawAttachmentFixtures.VideoSuccess,
            OpenClawAttachmentFixtures.AudioSuccess,
            OpenClawAttachmentFixtures.DocumentSuccess,
            OpenClawAttachmentFixtures.DocumentError);

        // The picture row has no attachment: its url-form image block makes
        // the picture turn, as it does today.
        var picture = turns.First(t => t.ImageAlt == "sample_morning_still_10am_9876543210.png");
        Assert.Null(picture.Attachment);
        Assert.StartsWith("/api/chat/media/outgoing/", picture.ImageUrl);

        Assert.Equal(
            new[] { AttachmentKind.Video, AttachmentKind.Video, AttachmentKind.Audio,
                    AttachmentKind.Document, AttachmentKind.Document },
            turns.Where(t => t.Attachment is not null).Select(t => t.Attachment!.Value.Kind));
    }

    // "my..clip.mp4" is a legal filename; refusing it dropped the clip and its
    // note, which is the CB-274 symptom.
    [Fact]
    public void AFailedClipWhoseNameContainsTwoDotsIsStillResolved()
    {
        var turn = Assert.Single(Turns("""
        {"role":"assistant","content":[
          {"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"my..clip.mp4"}}]}
        """));

        Assert.EndsWith("my..clip.mp4", turn.ImageSourcePath);
        Assert.NotNull(turn.ImageUrl);
    }

    // A block that names nothing must not get a guessed filename: the gateway
    // would be asked about a file called "file".
    [Theory]
    [InlineData("""{"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"document"}}""")]
    [InlineData("""{"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video"}}""")]
    [InlineData("""{"type":"video"}""")]
    public void ANamelessFailedBlockGetsNoGuessedPathAndNoRoute(string block)
    {
        var turn = Assert.Single(Turns(
            "{\"role\":\"assistant\",\"openclawAutomation\":{\"kind\":\"cron\",\"jobId\":\"j\"},"
            + "\"content\":[{\"type\":\"text\",\"text\":\"hi\"}," + block + "]}"));

        Assert.Null(turn.ImageUrl);
        Assert.Null(turn.ImageSourcePath);
        Assert.Equal("", turn.Attachment!.Value.Label);
        Assert.Null(OpenClawSessions.RecoveryBasenameFor(turn));
    }

    // A url is spliced into a token-bearing request line; CR/LF or a space in
    // it would inject a header, so such a block draws as omitted.
    [Theory]
    [InlineData("/x\\r\\nX-Evil: 1")]
    [InlineData("/x y")]
    [InlineData("/x\\u0000")]
    [InlineData("/x\\u007f")]
    [InlineData("/x\\t")]
    public void AUrlThatCouldEndTheRequestLineIsNoUrl(string url)
    {
        var video = OpenClawSessions.AttachmentOf(
            Wrapped("[{\"type\":\"video\",\"url\":\"" + url + "\",\"fileName\":\"a.mp4\"}]"))!.Value;
        Assert.Null(video.Url);
        Assert.Equal("omitted", video.FailureCode);

        var doc = OpenClawSessions.AttachmentOf(Wrapped(
            "[{\"type\":\"attachment\",\"attachment\":{\"kind\":\"document\",\"label\":\"a.pdf\",\"url\":\"" + url + "\"}}]"))!.Value;
        Assert.Null(doc.Url);
        Assert.Equal("omitted", doc.FailureCode);
    }

    [Fact]
    public void AnOrdinaryEncodedUrlIsKept()
    {
        Assert.Equal("/api/chat/media/outgoing/k%3Aa/id/full?x=1", OpenClawSessions.CleanUrl("/api/chat/media/outgoing/k%3Aa/id/full?x=1"));
        Assert.Null(OpenClawSessions.CleanUrl(null));
        Assert.Null(OpenClawSessions.CleanUrl("  "));
    }
}
