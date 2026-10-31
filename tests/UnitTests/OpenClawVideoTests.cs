using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-272: a video an OpenClaw agent posts is a tile in the chat panel, not
// inline playback. This covers the decisions that sit under the tile, none of
// which need a window: which messages name a clip, what the gateway's meta
// answer says about it, how the tile is sized and labelled, and what a refused
// clip's note says.
//
// The turn text below keeps the shape of the real one that rendered as plain
// text — a caption, then a MEDIA: line last, naming an .mp4 under an agent's
// workspace outputs — with invented wording and paths, per this suite's habit
// (see OpenClawLocalMediaPathTests): fidelity is owed to the structure.
public class OpenClawVideoTests
{
    private const string ClipPath =
        "/Users/sample/.openclaw/workspace-sample/outputs/sample/sample_afternoon_video_1234567890.mp4";

    private const string RealTurnText =
        "Pink fairy lights and a little message just for you 🌸\nMEDIA:" + ClipPath;

    // The gateway's own `&meta=1` answers, in the two shapes the ticket
    // measured: a servable clip, and a refusal.
    private const string ServableMeta =
        "{\"available\":true,\"mimeType\":\"video/mp4\",\"sizeBytes\":1042813,\"playback\":\"native\","
        + "\"durationMs\":12750,\"width\":544,\"height\":800,\"mediaTicket\":\"v1.sample\"}";

    private const string RefusedMeta =
        "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}";

    // ---- the parser: which messages name a clip -------------------------

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".webm")]
    [InlineData(".m4v")]
    [InlineData(".MP4")]
    public void AMediaLineNamingAClipIsACandidateForEveryVideoExtension(string ext)
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("here you go\nMEDIA:/tmp/out/clip" + ext);

        Assert.NotNull(candidate);
        Assert.Equal("/tmp/out/clip" + ext, candidate!.Value.Path);
        Assert.True(candidate.Value.Explicit);
    }

    [Fact]
    public void TheRealShapedTurnIsACandidateAndNamesTheClip()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom(RealTurnText);

        Assert.Equal(ClipPath, candidate!.Value.Path);
        Assert.True(candidate.Value.Explicit);
    }

    [Fact]
    public void AWholeMessageThatIsOnlyAClipPathIsACandidateButNotExplicit()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("  /tmp/out/clip.mov \n");

        Assert.Equal("/tmp/out/clip.mov", candidate!.Value.Path);
        Assert.False(candidate.Value.Explicit);
    }

    [Fact]
    public void ACaptionWithATrailingClipPathOnTheSameLineIsACandidate()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("made you a little thing ~/clips/hello.webm");

        Assert.Equal("~/clips/hello.webm", candidate!.Value.Path);
        Assert.False(candidate.Value.Explicit);
    }

    [Fact]
    public void ACaptionFollowedByABareClipFilenameIsACandidate()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("made you a little thing\nhello.m4v");

        Assert.Equal("hello.m4v", candidate!.Value.Path);
        Assert.False(candidate.Value.Explicit);
    }

    // The same negative control the picture arms already have: a clip's bare
    // filename with nothing around it is ambiguous prose, not a delivery.
    [Fact]
    public void ABareClipFilenameAloneStaysText()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("hello.mp4"));
    }

    [Theory]
    [InlineData("I converted that to .mp4 yesterday and it played fine")]
    [InlineData("the clip.mp4 is on my desktop, have a look")]
    [InlineData("MEDIA: is a broad term, like clip.mp4")]
    [InlineData("MEDIA:/tmp/../etc/clip.mp4")]
    [InlineData("MEDIA://host/clip.mp4")]
    [InlineData("MEDIA:/tmp/out/notes.txt")]
    public void ProseThatMerelyMentionsAClipIsNotACandidate(string text)
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom(text));
    }

    [Fact]
    public void ATraversalAsAWholeMessageIsRefusedForAClipToo()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("/tmp/../etc/clip.mp4"));
    }

    // The two kinds stay separate. The image predicate is OpenClawCronRecovery's
    // trigger for "is this trailing token a picture", so a clip must never
    // satisfy it, and a picture must never be taken for a clip.
    [Fact]
    public void TheImagePredicateStillRefusesAClipAndTheVideoPredicateStillRefusesAPicture()
    {
        Assert.False(OpenClawSessions.LooksLikeAnImagePath("/tmp/clip.mp4"));
        Assert.True(OpenClawSessions.LooksLikeAVideoPath("/tmp/clip.mp4"));

        Assert.True(OpenClawSessions.LooksLikeAnImagePath("/tmp/pic.png"));
        Assert.False(OpenClawSessions.LooksLikeAVideoPath("/tmp/pic.png"));

        Assert.True(OpenClawSessions.LooksLikeAMediaPath("/tmp/pic.png"));
        Assert.True(OpenClawSessions.LooksLikeAMediaPath("/tmp/clip.mp4"));
        Assert.False(OpenClawSessions.LooksLikeAMediaPath("/tmp/notes.txt"));
    }

    [Fact]
    public void APictureCandidateIsStillAPictureAndNotAClip()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("here\nMEDIA:/tmp/pic.png");

        Assert.False(OpenClawVideo.IsVideoPath(candidate!.Value.Path));
    }

    [Theory]
    [InlineData("/a/b.mp4", true)]
    [InlineData("/a/b.MOV", true)]
    [InlineData("/a/b.webm", true)]
    [InlineData("/a/b.m4v", true)]
    [InlineData("/a/b.png", false)]
    [InlineData("/a/b.mp4.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVideoPathReadsTheExtension(string? path, bool expected)
    {
        Assert.Equal(expected, OpenClawVideo.IsVideoPath(path));
    }

    // ---- a history read: the turn that carries the clip -----------------

    private static List<HistoryTurn> Turns(string text) =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(
            "[{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":"
            + JsonSerializer.Serialize(text) + "}]}]").RootElement, "agent:main:cron:sample");

    [Fact]
    public void AHistoryReadOfTheRealShapedTurnCarriesTheRouteAndTheCaption()
    {
        var turn = Assert.Single(Turns(RealTurnText));

        var media = new OpenClawMediaSource(ClipPath, "agent:main:cron:sample");
        Assert.Equal(media.Route, turn.ImageUrl);
        Assert.Equal(ClipPath, turn.ImageSourcePath);
        Assert.Equal("sample_afternoon_video_1234567890.mp4", turn.ImageAlt);
        Assert.Equal(MediaConfidence.High, turn.Confidence);

        // The caption stays; and nothing here ever carries the clip's bytes
        // into the picture path.
        Assert.Contains("Pink fairy lights", turn.Text);
        Assert.Null(turn.ImageBytes);
    }

    [Fact]
    public void AHistoryReadOfProseEndingInAClipPathIsLowConfidence()
    {
        var turn = Assert.Single(Turns("I deleted /Users/sample/old/clip.mp4"));

        Assert.Equal(MediaConfidence.Low, turn.Confidence);
    }

    [Fact]
    public void AHistoryReadOfProseThatMerelyMentionsMp4StaysText()
    {
        var turn = Assert.Single(Turns("render it as an .mp4 when you get a chance"));

        Assert.Null(turn.ImageUrl);
    }

    [Fact]
    public void ATurnIsAVideoOnlyWhenItsSourcePathNamesOne()
    {
        Assert.True(new ChatTurn { ImageSourcePath = ClipPath }.IsVideo);
        Assert.False(new ChatTurn { ImageSourcePath = "/tmp/pic.png" }.IsVideo);
        Assert.False(new ChatTurn().IsVideo);
    }

    // ---- the live path ---------------------------------------------------

    private static JsonElement AgentText(string text) =>
        JsonDocument.Parse($"{{\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}").RootElement;

    // The live arm hands the row a route to ask about and does not fetch.
    // TryResolveLocalMedia reaches this before its first await, so by the time
    // OnAgentEvent returns the turn already has everything — which is also
    // what proves nothing was fetched: a fetch would have been awaited.
    [Fact]
    public void ALiveClipTurnGetsARouteAndNoBytes()
    {
        var session = new OpenClawChatSession("openclaw:agent:main:main", "agent:main:main", "main");
        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        session.OnAgentEvent("agent", AgentText(RealTurnText));

        var turn = session.History[0];
        Assert.True(turn.IsVideo);
        Assert.Equal(new OpenClawMediaSource(ClipPath, "agent:main:main").Route, turn.ImageUrl);
        Assert.Equal(ClipPath, turn.ImageSourcePath);
        Assert.Equal("sample_afternoon_video_1234567890.mp4", turn.ImageAlt);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
        Assert.Null(turn.ImageBytes);
        Assert.Contains(turn, updated);
    }

    [Fact]
    public void ALiveClipNamedOnlyByATrailingTokenIsLowConfidence()
    {
        var session = new OpenClawChatSession("openclaw:agent:main:main", "agent:main:main", "main");

        session.OnAgentEvent("agent", AgentText("have a look /Users/sample/out/clip.mov"));

        Assert.Equal(MediaConfidence.Low, session.History[0].Confidence);
        Assert.True(session.History[0].IsVideo);
    }

    // ---- the gateway's meta answer ---------------------------------------

    [Fact]
    public void AServableClipsMetaIsParsed()
    {
        var meta = OpenClawVideo.ParseMeta(ServableMeta);

        Assert.Equal(new OpenClawVideo.Meta(true, 12750, 544, 800, 1042813), meta);
    }

    [Fact]
    public void ARefusedClipsMetaIsAnAnswerOfNotAvailable()
    {
        var meta = OpenClawVideo.ParseMeta(RefusedMeta);

        Assert.Equal(new OpenClawVideo.Meta(false, null, null, null, null), meta);
    }

    [Fact]
    public void AnAnswerWithNoAvailableFieldIsNotAvailable()
    {
        Assert.False(OpenClawVideo.ParseMeta("{\"durationMs\":1000}")!.Value.Available);
    }

    [Fact]
    public void AnAvailableThatIsNotTrueIsNotAvailable()
    {
        Assert.False(OpenClawVideo.ParseMeta("{\"available\":\"true\"}")!.Value.Available);
    }

    [Fact]
    public void FieldsTheGatewayLeavesOutOrMistypesComeBackNull()
    {
        var meta = OpenClawVideo.ParseMeta(
            "{\"available\":true,\"durationMs\":\"long\",\"width\":-5,\"height\":1.5,\"sizeBytes\":\"big\"}");

        Assert.Equal(new OpenClawVideo.Meta(true, null, null, null, null), meta);
    }

    // An unchecked int cast wrapped 4294967297 to 1 and drew a wrong tile.
    [Fact]
    public void ADimensionPastIntRangeIsNullNotWrapped()
    {
        var meta = OpenClawVideo.ParseMeta(
            "{\"available\":true,\"width\":4294967297,\"height\":800}");

        Assert.Equal(new OpenClawVideo.Meta(true, null, null, 800, null), meta);
        Assert.Equal(OpenClawVideo.TileWidth * 9 / 16, OpenClawVideo.TileHeight(meta!.Value.Width, meta.Value.Height));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void NoAnswerAtAllIsNull(string? json)
    {
        Assert.Null(OpenClawVideo.ParseMeta(json));
    }

    // ---- the tile's label and shape --------------------------------------

    [Theory]
    [InlineData(12750L, "0:13")]
    [InlineData(12400L, "0:12")]
    [InlineData(500L, "0:01")]
    [InlineData(59500L, "1:00")]
    [InlineData(61000L, "1:01")]
    [InlineData(600000L, "10:00")]
    [InlineData(3725000L, "62:05")]
    public void ADurationReadsAsMinutesAndSeconds(long ms, string expected)
    {
        Assert.Equal(expected, OpenClawVideo.FormatDuration(ms));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void AnUnknownOrEmptyDurationDrawsNoLabel(long? ms)
    {
        Assert.Null(OpenClawVideo.FormatDuration(ms));
    }

    [Fact]
    public void ATileIsAsWideAsAPicture()
    {
        Assert.Equal(228, OpenClawVideo.TileWidth);
    }

    [Theory]
    [InlineData(600, 600, 228.0)]
    [InlineData(1920, 1080, 128.25)]
    [InlineData(null, null, 128.25)]
    [InlineData(544, null, 128.25)]
    [InlineData(null, 800, 128.25)]
    [InlineData(0, 800, 128.25)]
    [InlineData(544, 0, 128.25)]
    public void ATileFollowsTheClipsAspectRatio(int? w, int? h, double expected)
    {
        Assert.Equal(expected, OpenClawVideo.TileHeight(w, h), 2);
    }

    [Fact]
    public void AnExtremeClipIsClampedSoTheTileStaysUsable()
    {
        // 544x800 would be 335 tall at this width; a banner would be a sliver.
        Assert.Equal(320, OpenClawVideo.TileHeight(544, 800));
        Assert.Equal(96, OpenClawVideo.TileHeight(1000, 100));
    }

    // ---- a refused clip's note -------------------------------------------

    [Fact]
    public void ARefusedClipSaysVideoAndKeepsTheGatewaysReason()
    {
        Assert.Equal(
            "Video not shown — the gateway wouldn't serve that file as a video (it said: Not an image).",
            OpenClawMediaRefusal.Explain(RefusedMeta, video: true));
    }

    [Fact]
    public void ARefusedClipWithNoReasonStillSaysSo()
    {
        Assert.Equal(
            "Video not shown — the gateway wouldn't serve that file as a video.",
            OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"unsupported-media-type\"}", video: true));
    }

    [Fact]
    public void AVeryLongReasonIsCappedForAClipToo()
    {
        var json = "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\""
                   + new string('x', 500) + "\"}";

        var note = OpenClawMediaRefusal.Explain(json, video: true);

        Assert.Equal("Video not shown — the gateway wouldn't serve that file as a video (it said: "
                     + new string('x', 200) + ").", note);
    }

    [Fact]
    public void TheSameRefusalForAPictureIsUnchanged()
    {
        Assert.Equal(
            "Picture not shown — that file isn't a picture this app can show.",
            OpenClawMediaRefusal.Explain(RefusedMeta));
    }

    [Fact]
    public void AClipOutsideTheAllowedFoldersGetsTheSameRemedyAsAPicture()
    {
        Assert.Equal(
            "Video not shown — the gateway won't serve files from that folder. Ask the agent to "
            + "write it to ~/.openclaw/media/, which is allowed for every agent.",
            OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"outside-allowed-folders\"}", video: true));
    }

    [Theory]
    [InlineData("file-not-found", "Video not shown — the gateway couldn't find that file.")]
    [InlineData("not-a-file", "Video not shown — that path isn't a file.")]
    public void TheOtherKnownCodesReadTheSameForAClip(string code, string expected)
    {
        Assert.Equal(expected,
            OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"" + code + "\"}", video: true));
    }

    [Fact]
    public void AnUnreachableGatewayIsAHonestDontKnowForAClip()
    {
        Assert.Equal("Video not shown — couldn't ask the gateway why.",
            OpenClawMediaRefusal.Explain(null, video: true));
    }

    [Fact]
    public void AnAvailableClipThatDidNotFinishSaysSo()
    {
        Assert.Equal("Video not shown — the gateway has the file but the fetch didn't finish.",
            OpenClawMediaRefusal.Explain(ServableMeta, video: true));
    }

    [Theory]
    [InlineData("{\"available\":false,\"code\":\"weird\",\"reason\":\"because\"}",
        "Video not shown — the gateway wouldn't serve it: because")]
    [InlineData("{\"available\":false,\"code\":\"weird\"}",
        "Video not shown — the gateway wouldn't serve it (weird).")]
    [InlineData("{\"available\":false}",
        "Video not shown — the gateway wouldn't serve it.")]
    public void AnUnmappedCodeFallsThroughToTheNeutralLinesForAClip(string json, string expected)
    {
        Assert.Equal(expected, OpenClawMediaRefusal.Explain(json, video: true));
    }

    // ---- the cross-arm dedup never fetches a clip ------------------------

    private static HistoryTurn Hist(MediaSourceArm arm, string? url, string? path) =>
        new(ChatRole.Assistant, "", url, "", DateTimeOffset.UnixEpoch, null, null,
            ImageSourcePath: path, Arm: arm);

    [Fact]
    public void WorthFetchingForCrossArmCompareSkipsAClipMirror()
    {
        Assert.True(OpenClawSessions.WorthFetchingForCrossArmCompare(
            Hist(MediaSourceArm.Mirror, "/r?x=1", "/a/pic.png")));
        Assert.False(OpenClawSessions.WorthFetchingForCrossArmCompare(
            Hist(MediaSourceArm.Mirror, "/r?x=1", "/a/clip.mp4")));
        Assert.False(OpenClawSessions.WorthFetchingForCrossArmCompare(
            Hist(MediaSourceArm.Inline, "/r?x=1", "/a/pic.png")));
        Assert.False(OpenClawSessions.WorthFetchingForCrossArmCompare(
            Hist(MediaSourceArm.Mirror, "", "/a/pic.png")));
        Assert.False(OpenClawSessions.WorthFetchingForCrossArmCompare(
            Hist(MediaSourceArm.Mirror, null, "/a/pic.png")));
    }

    [Fact]
    public void AMirrorRecordNamingAClipDrawsAVideoTurnWithMirrorArm()
    {
        var turns = OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(
            "[{\"role\":\"assistant\",\"provider\":\"openclaw\",\"model\":\"delivery-mirror\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"clip_123.mp4\"}]}]").RootElement,
            "agent:main:cron:sample");

        var turn = Assert.Single(turns);
        Assert.Equal(MediaSourceArm.Mirror, turn.Arm);
        Assert.Equal(OpenClawSessions.SharedMediaDir + "clip_123.mp4", turn.ImageSourcePath);
        Assert.True(OpenClawVideo.IsVideoPath(turn.ImageSourcePath));
        Assert.NotNull(turn.ImageUrl);
        Assert.Null(turn.ImageBytes);
    }

    // ---- the socket's body ceiling ---------------------------------------

    private static OpenClawVideo.Meta MetaOfSize(long? size) => new(true, null, null, null, size);

    [Fact]
    public void AClipOverTheSocketCeilingIsTooLargeToOpen()
    {
        Assert.True(OpenClawVideo.TooLargeToOpen(MetaOfSize(OpenClawVideo.MaxOpenBytes + 1)));
        Assert.False(OpenClawVideo.TooLargeToOpen(MetaOfSize(OpenClawVideo.MaxOpenBytes)));
        Assert.False(OpenClawVideo.TooLargeToOpen(MetaOfSize(null)));
        Assert.False(OpenClawVideo.TooLargeToOpen((OpenClawVideo.Meta?)null));

        // CB-274: the same guard for a gateway block, and for none.
        ChatAttachment? Block(long? size) =>
            new ChatAttachment(AttachmentKind.Video, "/u", "c.mp4", null, size, null, null, null, null);
        Assert.True(OpenClawVideo.TooLargeToOpen(Block(OpenClawVideo.MaxOpenBytes + 1)));
        Assert.False(OpenClawVideo.TooLargeToOpen(Block(OpenClawVideo.MaxOpenBytes)));
        Assert.False(OpenClawVideo.TooLargeToOpen(Block(null)));
        Assert.False(OpenClawVideo.TooLargeToOpen((ChatAttachment?)null));
    }

    [Fact]
    public void ABodyOverTheCeilingIsTruncated()
    {
        Assert.True(OpenClawVideo.IsTruncated((int)OpenClawVideo.MaxOpenBytes + 1));
        Assert.False(OpenClawVideo.IsTruncated((int)OpenClawVideo.MaxOpenBytes));
        Assert.False(OpenClawVideo.IsTruncated(0));
    }

    [Fact]
    public void TheCeilingIsTheSocketsOwn()
    {
        Assert.Equal(OpenClawSocket.MaxBodyBytes, OpenClawVideo.MaxOpenBytes);
        Assert.Equal(32L * 1024 * 1024, OpenClawVideo.MaxOpenBytes);
    }

    [Fact]
    public void TheTooLargeNoteSaysTheSizeInWholeMegabytes()
    {
        Assert.Equal(
            "Video not opened — the clip is 42 MB and this app fetches at most 32 MB. "
            + "Open it on the gateway machine instead.",
            OpenClawVideo.TooLargeNote(41L * 1024 * 1024 + 1));
        Assert.Contains("is 41 MB", OpenClawVideo.TooLargeNote(41L * 1024 * 1024));
    }

    [Fact]
    public void TheOtherClickTimeNotesAreFixedSentences()
    {
        Assert.Equal("Video not opened — the gateway didn't send the clip.", OpenClawVideo.NotSentNote);
        Assert.Equal("Video not opened — the clip came back incomplete (over 32 MB).", OpenClawVideo.TruncatedNote);
    }

    [Theory]
    [InlineData("holiday.webm", "/a/b/other.mov", "holiday.webm")]
    [InlineData("", "/a/b/clip.mov", "video.mov")]
    [InlineData(null, "/a/b/clip.WEBM", "video.WEBM")]
    [InlineData("", "", "video.mp4")]
    [InlineData(null, null, "video.mp4")]
    [InlineData("", "/a/b/noextension", "video.mp4")]
    public void OpenNameKeepsTheClipsOwnExtension(string? alt, string? path, string expected)
    {
        Assert.Equal(expected, OpenClawVideo.OpenName(alt, path));
    }

    [Fact]
    public void TheExtensionsAreExactlyTheFourTheTicketNames()
    {
        Assert.Equal(new[] { ".mp4", ".mov", ".webm", ".m4v" }, OpenClawVideo.Extensions);
    }

    // CB-274: the assistant-media meta answer becomes the one shape a tile is
    // drawn from.
    [Fact]
    public void AMetaAnswerConvertsToAUrlessVideoAttachment()
    {
        var meta = OpenClawVideo.ParseMeta(
            "{\"available\":true,\"sizeBytes\":1042813,\"durationMs\":12750,\"width\":544,\"height\":800}")!.Value;

        var a = OpenClawVideo.FromMeta(meta, "sample_clip.mp4");

        Assert.Equal(AttachmentKind.Video, a.Kind);
        Assert.Null(a.Url);
        Assert.Equal("sample_clip.mp4", a.Label);
        Assert.Equal(1042813, a.SizeBytes);
        Assert.Equal("0:13", a.Duration);
        Assert.Equal(320, OpenClawVideo.TileHeight(a.Width, a.Height));
        Assert.Equal("0:13", OpenClawVideo.FormatDuration(a.DurationMs));
    }
}
