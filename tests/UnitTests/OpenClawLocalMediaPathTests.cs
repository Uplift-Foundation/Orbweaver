using System.Collections.Generic;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-88: an agent's own generated picture, named by its own path on the
// gateway host rather than a fetchable URL. Two real shapes, both taken from
// a live gateway via tools/openclaw-probe rather than assumed — see
// OpenClawSessions.LocalMediaPathFrom's own comment.
//
// The *shapes* below are the real ones; the message text, paths and filenames
// are invented stand-ins. Fidelity is owed to the structure — how many
// paragraphs sit above the MEDIA: line, where the marker falls, how deep the
// path runs — never to the wording, so do not paste real captured output back
// in here on the theory that the values matter.
public class OpenClawLocalMediaPathTests
{
    // The captured shape: two paragraphs of in-character reply, then the
    // MEDIA: line last — not the first line of the message.
    [Fact]
    public void AMediaLineAfterOtherParagraphsIsFound()
    {
        var text = "got the path — queuing the render 🌸\n\n"
                  + "here it is, straight off the batch 💛✨\n\n"
                  + "MEDIA:/Users/sample/.openclaw/workspace-render-quill/outputs/"
                  + "gallery/sample_drop_100200300_400500600_00001_.png";

        Assert.Equal(
            "/Users/sample/.openclaw/workspace-render-quill/outputs/"
            + "gallery/sample_drop_100200300_400500600_00001_.png",
            OpenClawSessions.LocalMediaPathFrom(text)?.Path);
    }

    [Fact]
    public void AMediaLineAsTheWholeMessageIsFound()
    {
        Assert.Equal("/tmp/pic.png", OpenClawSessions.LocalMediaPathFrom("MEDIA:/tmp/pic.png")?.Path);
    }

    [Fact]
    public void WhitespaceAroundTheMediaLineIsTrimmed()
    {
        Assert.Equal("/tmp/pic.png", OpenClawSessions.LocalMediaPathFrom("MEDIA:  /tmp/pic.png  ")?.Path);
    }

    [Fact]
    public void AMediaMarkerWithNoPathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:"));
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:   "));
    }

    [Fact]
    public void OrdinaryTextIsNeverMistakenForAMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("just an ordinary reply, nothing attached"));
    }

    // Mentioning "MEDIA:" mid-sentence, not as its own line, is not the
    // marker — only a line that starts with it counts.
    [Fact]
    public void MediaMentionedMidLineIsNotTheMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("the SOCIAL MEDIA:/path.png thing you mentioned"));
    }

    // QA (CB-88) found this real gap: an ordinary sentence that happens to
    // start a line with "MEDIA:" would otherwise have everything after the
    // colon extracted as a "path" and fired at the gateway as one. The text
    // after the prefix now has to pass the same shape check the bare-path
    // arm already required.
    [Fact]
    public void AnOrdinarySentenceStartingWithTheWordMediaIsNotAMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA: is a broad term for a lot of things"));
    }

    // The prefix alone doesn't make it a picture — a relative-looking path
    // after it is rejected the same way a bare relative path already is.
    [Fact]
    public void AMediaLineWithARelativePathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:outputs/gallery/pic.png"));
    }

    // The other real shape: the same automation's duplicate-post bug (before
    // it was fixed) left a bare path as an entire assistant turn, no MEDIA:
    // prefix at all.
    [Fact]
    public void ABarePathThatIsTheWholeMessageIsFound()
    {
        Assert.Equal(
            "/Users/sample/.openclaw/workspace-render-quill/outputs/gallery/sample_drop.png",
            OpenClawSessions.LocalMediaPathFrom(
                "/Users/sample/.openclaw/workspace-render-quill/outputs/gallery/sample_drop.png")?.Path);
    }

    [Theory]
    [InlineData("/tmp/pic.png")]
    [InlineData("/tmp/pic.PNG")]
    [InlineData("/tmp/pic.jpg")]
    [InlineData("/tmp/pic.jpeg")]
    [InlineData("/tmp/pic.gif")]
    [InlineData("/tmp/pic.webp")]
    public void EveryKnownImageExtensionIsRecognisedAsABarePath(string path)
    {
        Assert.Equal(path, OpenClawSessions.LocalMediaPathFrom(path)?.Path);
    }

    // A relative-looking path is not what this matches — every real example
    // observed was absolute, and a relative one is more likely a citation or
    // a filename mentioned in conversation than a picture to fetch.
    [Fact]
    public void ARelativeBarePathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("outputs/gallery/pic.png"));
    }

    // A sentence that happens to end in something that looks like a
    // filename is not a bare path — the whole trimmed message has to be
    // nothing else.
    [Fact]
    public void ASentenceEndingInAFilenameIsNotABarePath()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("saved it to /tmp/pic.png just now"));
    }

    [Fact]
    public void ABarePathToAnUnknownExtensionIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("/tmp/notes.txt"));
    }

    // CB-107: an agent's caption paired descriptive text with the file rather
    // than sending it alone, breaking both existing arms. Caught live in a
    // real OpenClaw orb — none of the reported messages drew a thumbnail.
    [Fact]
    public void ACaptionWithABarePathOnTheNextLineIsFound()
    {
        Assert.Equal(
            "/Users/w/.openclaw/workspace-example/outputs/agent/photo_275866713.png",
            OpenClawSessions.LocalMediaPathFrom(
                "here's the shot   /Users/w/.openclaw/"
                + "workspace-example/outputs/agent/photo_275866713.png")?.Path);
    }

    // A bare filename with no directory at all has nothing to fetch on its
    // own — ResolveLocalMediaPath is what turns this into something
    // fetchable, so this returns the raw name unresolved.
    [Fact]
    public void ACaptionWithABareFilenameOnTheNextLineIsFoundUnresolved()
    {
        Assert.Equal(
            "photo_773311913.png",
            OpenClawSessions.LocalMediaPathFrom(
                "here's this morning's shot\n"
                + "photo_773311913.png")?.Path);
    }

    // Video is a real shape in the same corpus. It was a deliberately separate
    // gap until CB-272: ImageExtensions has no video extensions, so this stayed
    // plain text rather than being silently treated as an image. It is now a
    // candidate of its own kind — a clip, never a picture: the image predicate
    // still refuses the name, and OpenClawVideo.IsVideoPath is what routes it
    // to the tile instead of the bitmap decoder.
    [Fact]
    public void ACaptionWithATrailingVideoFilenameIsAClipNotAPicture()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom(
            "Fixed the clip, here you go.\n"
            + "clip_v4_final.mp4");

        Assert.NotNull(candidate);
        Assert.Equal("clip_v4_final.mp4", candidate!.Value.Path);
        Assert.False(candidate.Value.Explicit);
        Assert.True(OpenClawVideo.IsVideoPath(candidate.Value.Path));
        Assert.False(OpenClawSessions.LooksLikeAnImagePath(candidate.Value.Path));
    }

    // A bare trailing word still has to look like a filename — the relative-
    // path and unknown-extension rules from the whole-message checks above
    // apply here too, not just "ends the message".
    [Fact]
    public void ATrailingWordThatIsNotFilenameShapedIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom(
            "just an ordinary reply that ends in a word"));
    }

    // ---- CB-116: which arm produced a candidate, carried on the result ----
    //
    // Explicit is true only for a "MEDIA:" line — an agent asserting a
    // picture — and false for both other arms, which are provenance the
    // parser itself has no access to (see LocalMediaCandidate's own header).
    // Pinned here, at the level that actually decides the flag, rather than
    // only inferred from the tiering tests downstream.

    [Fact]
    public void AMediaLineCandidateIsExplicit()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("MEDIA:/tmp/pic.png");

        Assert.NotNull(candidate);
        Assert.True(candidate!.Value.Explicit);
    }

    [Fact]
    public void ABarePathAsTheWholeMessageIsNotExplicit()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("/tmp/pic.png");

        Assert.NotNull(candidate);
        Assert.False(candidate!.Value.Explicit);
    }

    [Fact]
    public void ACaptionedTrailingTokenIsNotExplicit()
    {
        var candidate = OpenClawSessions.LocalMediaPathFrom("here's the shot\n/tmp/pic.png");

        Assert.NotNull(candidate);
        Assert.False(candidate!.Value.Explicit);
    }
}

// CB-107: turning what LocalMediaPathFrom found into something fetchable.
public class OpenClawResolveLocalMediaPathTests
{
    [Fact]
    public void ARootedPathIsReturnedUnchanged()
    {
        Assert.Equal("/tmp/pic.png",
            OpenClawSessions.ResolveLocalMediaPath("/tmp/pic.png", null));
    }

    [Fact]
    public void ATildePathIsReturnedUnchanged()
    {
        Assert.Equal("~/.openclaw/media/pic.png",
            OpenClawSessions.ResolveLocalMediaPath("~/.openclaw/media/pic.png", null));
    }

    [Fact]
    public void ABareFilenameKnownOnThePageResolvesToItsHarvestedDirectory()
    {
        var mediaPaths = new Dictionary<string, string>
        {
            ["pic.png"] = "/Users/w/.openclaw/workspace-example/outputs/agent/pic.png"
        };

        Assert.Equal(
            "/Users/w/.openclaw/workspace-example/outputs/agent/pic.png",
            OpenClawSessions.ResolveLocalMediaPath("pic.png", mediaPaths));
    }

    [Fact]
    public void ABareFilenameUnknownOnThePageFallsBackToTheSharedMediaDirectory()
    {
        Assert.Equal(
            OpenClawSessions.SharedMediaDir + "pic.png",
            OpenClawSessions.ResolveLocalMediaPath("pic.png", new Dictionary<string, string>()));
    }

    [Fact]
    public void ABareFilenameWithNoPageAtAllFallsBackToTheSharedMediaDirectory()
    {
        Assert.Equal(
            OpenClawSessions.SharedMediaDir + "pic.png",
            OpenClawSessions.ResolveLocalMediaPath("pic.png", null));
    }
}
