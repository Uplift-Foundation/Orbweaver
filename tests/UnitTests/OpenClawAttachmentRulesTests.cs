using Xunit;

namespace Orbweaver.UnitTests;

// CB-275: the pure rules behind the audio tile and the file chip.
[Collection("Settings")]
public class OpenClawAttachmentRulesTests
{
    private static ChatAttachment Doc(string label, string? mime) =>
        new(AttachmentKind.Document, "/u", label, mime, null, null, null, null, null);

    private static ChatAttachment Audio(string label, string? mime = null) =>
        new(AttachmentKind.Audio, "/u", label, mime, null, null, null, null, null);

    // ---- FormatSize -----------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1L, null)]
    [InlineData(0L, "0 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(860_160L, "840 KB")]
    [InlineData(1_048_575L, "1.0 MB")]
    [InlineData(1_048_000L, "1023 KB")]
    [InlineData(1_073_741_823L, "1.0 GB")]
    [InlineData(1_073_000_000L, "1023.3 MB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(1_021_973L, "998 KB")]
    [InlineData(1_258_291L, "1.2 MB")]
    [InlineData(1_073_741_824L, "1.0 GB")]
    public void SizesReadTheWayAFileManagerSaysThem(long? size, string? expected) =>
        Assert.Equal(expected, OpenClawAttachment.FormatSize(size));

    // ---- the open name: safety ------------------------------------------

    [Theory]
    [InlineData("report.pdf", "application/pdf", "report.pdf")]
    [InlineData("evil.exe", "application/pdf", "evil.pdf")]
    [InlineData("thing.scr", "application/octet-stream", "thing.bin")]
    [InlineData("x.ps1", null, "x.bin")]
    [InlineData("a/b/c.txt", "text/plain", "c.txt")]
    [InlineData("a\\b\\c.bat", "text/plain; charset=utf-8", "c.txt")]
    [InlineData("noext", "application/json", "noext.json")]
    [InlineData("", "application/zip", "file.zip")]
    [InlineData("..", "application/zip", "file.zip")]
    [InlineData(".hidden", "application/yaml", "hidden.yaml")]
    [InlineData("a.tar.gz", "application/zip", "a.tar.zip")]
    public void ADocumentIsWrittenUnderAnExtensionTheMimeChoseNeverTheLabels(
        string label, string? mime, string expected) =>
        Assert.Equal(expected, OpenClawAttachment.OpenName(Doc(label, mime)));

    [Theory]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/json", ".json")]
    [InlineData("application/yaml", ".yaml")]
    [InlineData("application/x-yaml", ".yaml")]
    [InlineData("text/yaml", ".yaml")]
    [InlineData("text/csv", ".csv")]
    [InlineData("text/plain", ".txt")]
    [InlineData("text/markdown", ".md")]
    [InlineData("application/zip", ".zip")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx")]
    [InlineData("application/msword", ".doc")]
    [InlineData("application/vnd.ms-excel", ".xls")]
    [InlineData("application/vnd.ms-powerpoint", ".ppt")]
    [InlineData("audio/mp4", ".m4a")]
    [InlineData("audio/x-m4a", ".m4a")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("audio/ogg", ".ogg")]
    [InlineData("audio/wav", ".wav")]
    [InlineData("audio/x-wav", ".wav")]
    [InlineData("APPLICATION/PDF", ".pdf")]
    [InlineData("application/x-msdownload", ".bin")]
    [InlineData(null, ".bin")]
    public void EachMimeTypeHasItsOneExtension(string? mime, string expected) =>
        Assert.Equal(expected, OpenClawAttachment.ExtensionFor(mime));

    // ---- audio names ----------------------------------------------------

    [Theory]
    [InlineData("voice_note.m4a", "audio/mp4", "voice_note.m4a")]
    [InlineData("Song.MP3", null, "Song.mp3")]
    [InlineData("a/b/c.wav", null, "c.wav")]
    [InlineData("audio", "audio/mp4", "audio.m4a")]
    [InlineData("", "audio/mpeg", "audio.mp3")]
    [InlineData("memo.exe", "audio/ogg", "memo.ogg")]
    [InlineData("", null, "audio.bin")]
    public void AnAudioNoteKeepsItsAudioExtensionOrTakesTheMimes(string label, string? mime, string expected) =>
        Assert.Equal(expected, OpenClawAttachment.AudioOpenName(Audio(label, mime)));

    // ---- notes ----------------------------------------------------------

    [Fact]
    public void TheSizeNotesNameTheKind()
    {
        Assert.Equal(
            "Audio not opened — it is 33 MB and this app fetches at most 32 MB. Open it on the gateway machine instead.",
            OpenClawAttachment.TooLargeNote(AttachmentKind.Audio, 32L * 1_048_576 + 1));
        Assert.StartsWith("File not opened — it is", OpenClawAttachment.TooLargeNote(AttachmentKind.Document, 99_999_999));
        Assert.Equal("Audio not opened — the gateway didn't send it.", OpenClawAttachment.NotSentNote(AttachmentKind.Audio));
        Assert.Equal("File not opened — the gateway didn't send it.", OpenClawAttachment.NotSentNote(AttachmentKind.Document));
        Assert.Equal("Video not opened — the gateway didn't send it.", OpenClawAttachment.NotSentNote(AttachmentKind.Video));
        Assert.Equal("Picture not opened — the gateway didn't send it.", OpenClawAttachment.NotSentNote(AttachmentKind.Image));
        Assert.Equal("File not opened — it came back incomplete (over 32 MB).",
            OpenClawAttachment.TruncatedNote(AttachmentKind.Document));
    }

    [Fact]
    public void TooLargeIsOnlyPastTheCeiling()
    {
        var big = Audio("a.m4a") with { SizeBytes = OpenClawVideo.MaxOpenBytes + 1 };
        var edge = Audio("a.m4a") with { SizeBytes = OpenClawVideo.MaxOpenBytes };

        Assert.True(OpenClawAttachment.TooLargeToOpen(big));
        Assert.False(OpenClawAttachment.TooLargeToOpen(edge));
        Assert.False(OpenClawAttachment.TooLargeToOpen(null));
    }

    [Theory]
    [InlineData("{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}",
        "Audio not shown — the gateway wouldn't serve that file that way (it said: Not an image).")]
    [InlineData("{\"available\":false,\"code\":\"unsupported-media-type\"}",
        "Audio not shown — the gateway wouldn't serve that file that way.")]
    [InlineData("{\"available\":false,\"code\":\"file-not-found\"}",
        "Audio not shown — the gateway couldn't find that file.")]
    [InlineData(null, "Audio not shown — couldn't ask the gateway why.")]
    public void ARefusedAudioNoteNeverSaysPictureOrVideo(string? json, string expected) =>
        Assert.Equal(expected, OpenClawMediaRefusal.Explain(json, noun: "Audio"));

    // ---- audio paths ----------------------------------------------------

    [Theory]
    [InlineData("/a/b/note.m4a", true)]
    [InlineData("/a/b/NOTE.MP3", true)]
    [InlineData("x.opus", true)]
    [InlineData("x.flac", true)]
    [InlineData("x.png", false)]
    [InlineData("x.mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AnAudioPathIsRecognisedByExtension(string? path, bool expected)
    {
        Assert.Equal(expected, OpenClawAudio.IsAudioPath(path));
        Assert.Equal(expected, new ChatTurn { ImageSourcePath = path }.IsAudio);
    }

    [Fact]
    public void AStreamedAudioPathBecomesACandidateTheWayAClipDoes()
    {
        Assert.Equal("note.m4a", OpenClawSessions.LocalMediaPathFrom("here\nnote.m4a")!.Value.Path);
        Assert.Equal("/a/note.mp3", OpenClawSessions.LocalMediaPathFrom("MEDIA:/a/note.mp3")!.Value.Path);
        Assert.True(OpenClawSessions.LooksLikeAnAudioPath("/a/b.ogg"));
        Assert.False(OpenClawSessions.LooksLikeAnAudioPath("//a/b.ogg"));
        Assert.False(OpenClawSessions.LooksLikeAnAudioPath("b.ogg"));
        Assert.Equal("note.m4a", OpenClawCronRecovery.CandidateBasenameFrom("a voice note\nnote.m4a"));
        Assert.Equal("note.m4a", OpenClawCronRecovery.CandidateBasenameFrom("x /a/b/note.m4a"));
    }

    [Fact]
    public void ADeliveredAudioFileNameIsADeliveredPicture()
    {
        Assert.Equal("note.m4a", OpenClawSessions.DeliveredPictureName(
            "openclaw", "assistant", OpenClawSessions.DeliveryMirrorModel, "note.m4a"));
    }

    [Fact]
    public void AnAudioMirrorIsNeverFetchedForABytesCompare()
    {
        var mirror = new HistoryTurn(ChatRole.Assistant, "n.m4a", "/route", "n.m4a",
            System.DateTimeOffset.UnixEpoch, null, null, ImageSourcePath: "/a/n.m4a", Arm: MediaSourceArm.Mirror);

        Assert.False(OpenClawSessions.WorthFetchingForCrossArmCompare(mirror));
        Assert.True(OpenClawSessions.WorthFetchingForCrossArmCompare(
            mirror with { ImageSourcePath = "/a/n.png" }));
    }

    [Fact]
    public void ADocumentAttachmentIsADocumentAndNothingElse()
    {
        var turn = new ChatTurn { Attachment = Doc("r.pdf", "application/pdf") };

        Assert.True(turn.IsDocument);
        Assert.False(turn.IsAudio);
        Assert.False(turn.IsVideo);
        Assert.False(new ChatTurn().IsDocument);
    }

    // ---- a delivered clip's open name -------------------------------------

    private static ChatAttachment Clip(string label, string? mime) =>
        new(AttachmentKind.Video, "/u", label, mime, null, null, null, null, null);

    [Theory]
    [InlineData("evil.exe", "video/mp4", "evil.mp4")]
    [InlineData("x.app", "video/mp4", "x.mp4")]
    [InlineData("a.bat", "video/mp4", "a.mp4")]
    [InlineData("clip.mp4", "video/mp4", "clip.mp4")]
    [InlineData("clip.mp4", "video/quicktime", "clip.mov")]
    [InlineData("clip", "video/webm", "clip.webm")]
    [InlineData("clip", "video/x-m4v", "clip.m4v")]
    [InlineData("clip", "video/x-weird", "clip.mp4")]
    [InlineData("clip", "application/pdf", "clip.mp4")]
    [InlineData("clip", null, "clip.mp4")]
    [InlineData("", "video/mp4", "video.mp4")]
    public void ADeliveredClipTakesItsExtensionFromTheMimeNeverTheLabel(
        string label, string? mime, string expected) =>
        Assert.Equal(expected, OpenClawAttachment.VideoOpenName(Clip(label, mime)));

    [Theory]
    [InlineData("clip.mp4", null, "clip.mp4")]
    [InlineData("evil.exe", null, "evil.mp4")]
    [InlineData("evil", "/a/b.mov", "evil.mp4")]
    [InlineData("", "/a/b.mov", "video.mov")]
    [InlineData("", "/a/b.exe", "video.mp4")]
    [InlineData(null, null, "video.mp4")]
    public void ARecoveredClipNameIsKeptOnlyIfItAlreadyEndsInAClipExtension(
        string? alt, string? path, string expected) =>
        Assert.Equal(expected, OpenClawVideo.OpenName(alt, path));
}
