using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-93: when the gateway refuses `MEDIA:<path>`, `&meta=1` on the same route
// says why — this covers turning that answer into the line and tooltip the
// panel shows, and the two guards that decide whether to ask at all.
public class OpenClawMediaRefusalTests
{
    // ---- ShouldAskWhy: the happy-path guard -----------------------------

    [Fact]
    public void NonEmptyBytesNeverAsksWhy()
    {
        Assert.False(OpenClawMediaRefusal.ShouldAskWhy(
            new byte[] { 1 }, OpenClawSessions.AssistantMediaRoute + "x"));
    }

    [Fact]
    public void EmptyBytesAgainstTheAssistantMediaRouteAsksWhy()
    {
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(
            Array.Empty<byte>(), OpenClawSessions.AssistantMediaRoute + "x"));
    }

    [Fact]
    public void NullBytesAgainstAnOrdinaryAttachmentUrlNeverAsksWhy()
    {
        // An ordinary [media attached: ...] url has no &meta=1 variant, so
        // asking would be a second wasted request against a route that was
        // never going to explain itself.
        Assert.False(OpenClawMediaRefusal.ShouldAskWhy(
            null, "/__openclaw__/inbound?source=x"));
    }

    [Fact]
    public void NullBytesAgainstTheAssistantMediaRouteAsksWhy()
    {
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(
            null, OpenClawSessions.AssistantMediaRoute + "x"));
    }

    [Fact]
    public void ANullUrlNeverAsksWhy()
    {
        Assert.False(OpenClawMediaRefusal.ShouldAskWhy(null, null));
    }

    // The one that matters after CB-109, and the reason this guard was moved
    // off AssistantMediaRoute onto AssistantMediaPathPrefix.
    //
    // A real request now carries a second parameter. The guard has to
    // recognise it, and has to keep doing so if the query is ever reordered —
    // because the failure mode is not a broken picture or a red test, it is
    // every refusal note in the app silently ceasing to appear. One line here
    // catches that whole class.
    [Fact]
    public void AFullyFormedRequestCarryingASessionStillAsksWhy()
    {
        var url = new OpenClawMediaSource(
            "/Users/w/.openclaw/workspace-sample-agent/outputs/sample_40.png",
            "agent:comfyui:discord:direct:100000000000000001").Route;

        Assert.Contains("&sessionKey=", url, StringComparison.Ordinal);
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(Array.Empty<byte>(), url));
    }

    // And the meta url built from it is still recognised as one of these
    // routes, since it is the same string with a flag on the end.
    [Fact]
    public void TheMetaUrlOfAFullyFormedRequestIsStillRecognised()
    {
        var source = new OpenClawMediaSource("/a/b.png", "agent:quill:discord:channel:9");

        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(null, source.MetaRoute));
    }

    // ---- Explain ------------------------------------------------------------

    [Fact]
    public void OutsideAllowedFoldersGetsTheActionableRemedy()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"outside-allowed-folders\",\"reason\":\"Outside allowed folders\"}");

        Assert.Contains("~/.openclaw/media/", line, StringComparison.Ordinal);
        Assert.Contains("won't serve files from that folder", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyOtherCodeWithAReasonReportsTheReason()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"some-other-code\",\"reason\":\"not on this host\"}");

        // CB-108: an unmapped code no longer says "refused" — that word
        // asserts a permission decision the gateway may not actually have
        // made, and this code isn't in the table this file's Explain
        // consults first.
        Assert.Equal("Picture not shown — the gateway wouldn't serve it: not on this host", line);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeWithNoReasonNamesTheCode()
    {
        var line = OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"nope\"}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it (nope).", line);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherCodeNorReasonIsTheGenericRefusal()
    {
        var line = OpenClawMediaRefusal.Explain("{\"available\":false}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it.", line);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableTrueSaysTheFetchDidNotFinish()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":true,\"mediaTicket\":\"v1.abc\",\"mediaTicketExpiresAt\":\"later\"}");

        Assert.Equal("Picture not shown — the gateway has the file but the fetch didn't finish.", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void AnUnusableAnswerSaysItCouldNotAsk(string? json)
    {
        Assert.Equal("Picture not shown — couldn't ask the gateway why.", OpenClawMediaRefusal.Explain(json));
    }

    [Fact]
    public void CodePresentWithAvailableMissingIsTreatedAsRefused()
    {
        var line = OpenClawMediaRefusal.Explain("{\"code\":\"nope\"}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it (nope).", line);
    }

    [Fact]
    public void AWhitespaceOnlyReasonFallsBackToTheCode()
    {
        var line = OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"nope\",\"reason\":\"   \"}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it (nope).", line);
    }

    [Fact]
    public void ACodeThatIsNotAStringIsTreatedAsMissing()
    {
        // The gateway's own answer is always a string code, but a value of
        // the wrong JSON kind should read as "no code" rather than throw.
        var line = OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":123}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it.", line);
    }

    [Fact]
    public void AnOverLongReasonIsTruncatedAt200Characters()
    {
        var reason = new string('x', 500);
        var line = OpenClawMediaRefusal.Explain(
            $"{{\"available\":false,\"code\":\"nope\",\"reason\":\"{reason}\"}}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it: " + new string('x', 200), line);
    }

    [Fact]
    public void UnknownExtraFieldsAreIgnored()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"nope\",\"somethingElse\":123,\"nested\":{\"a\":1}}");

        Assert.Equal("Picture not shown — the gateway wouldn't serve it (nope).", line);
    }

    // ---- CB-108: per-code sentences, one row per table entry -----------------
    //
    // "outside-allowed-folders" and "available:true" already have cases above
    // (OutsideAllowedFoldersGetsTheActionableRemedy, AvailableTrueSaysTheFetch-
    // DidNotFinish) and are untouched by this ticket — it only widens the set
    // of codes with their own sentence and neutralizes the fallback. Every
    // code below was measured against a real gateway; a code that hasn't been
    // seen live is deliberately left off the table and falls through to the
    // neutral generic arm instead of a guessed sentence (covered above by
    // AnyOtherCodeWithAReasonReportsTheReason and its neighbors). The negative
    // "refused" assertion is the regression guard: the bug this ticket fixes
    // was a fall-through to a "refused" sentence for codes that are not
    // permission decisions, so a test that only checked the new string would
    // still pass against the broken build.

    [Fact]
    public void FileNotFoundGetsItsOwnSentence()
    {
        // Measured: OpenClawSessions.ResolveLocalMediaPath's bare-filename
        // guess (~/.openclaw/media/<basename>) is allowlisted, so every
        // wrong guess lands here — this was the most common wrong wording.
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"file-not-found\",\"reason\":\"File not found\"}");

        Assert.Equal("Picture not shown — the gateway couldn't find that file.", line);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NotAFileGetsItsOwnSentence()
    {
        // Measured.
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"not-a-file\",\"reason\":\"Not a file\"}");

        Assert.Equal("Picture not shown — that path isn't a file.", line);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedMediaTypeGetsItsOwnSentence()
    {
        // Measured — this is the gateway's own body, verbatim, for
        // /etc/passwd with a session supplied: it clears the folder check
        // and is turned away for not being an image. Note the reason is
        // "Not an image" and not a restatement of the code, which is why
        // the sentence here comes from the table rather than from `reason`.
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"unsupported-media-type\",\"reason\":\"Not an image\"}");

        Assert.Equal("Picture not shown — that file isn't a picture this app can show.", line);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
    }

    // ---- Detail (tooltip) ----------------------------------------------------

    [Fact]
    public void DetailPairsThePathWithTheCode()
    {
        var detail = OpenClawMediaRefusal.Detail(
            "{\"available\":false,\"code\":\"outside-allowed-folders\"}", "/a/b.png");

        Assert.Equal("/a/b.png — outside-allowed-folders", detail);
    }

    [Fact]
    public void DetailIsThePathAloneWithNoCode()
    {
        Assert.Equal("/a/b.png", OpenClawMediaRefusal.Detail("{\"available\":false}", "/a/b.png"));
    }

    [Fact]
    public void DetailIsThePathAloneWhenTheAnswerCouldNotBeAsked()
    {
        Assert.Equal("/a/b.png", OpenClawMediaRefusal.Detail(null, "/a/b.png"));
    }

    // CB-277: which session the refused route was built with.
    [Fact]
    public void DetailNamesTheSessionTheRefusedRouteWasAskedUnder()
    {
        var url = new OpenClawMediaSource("/a/b.mp4", "agent:comfyui:discord:direct:1").Route;

        Assert.Equal(
            "/a/b.mp4 — unsupported-media-type — asked under agent:comfyui:discord:direct:1",
            OpenClawMediaRefusal.Detail("{\"available\":false,\"code\":\"unsupported-media-type\"}", "/a/b.mp4", url, "agent:main:discord:direct:1"));
        Assert.Equal(
            "/a/b.mp4 — asked under agent:comfyui:discord:direct:1",
            OpenClawMediaRefusal.Detail("{\"available\":false}", "/a/b.mp4", url, "agent:main:discord:direct:1"));
        Assert.Equal(
            "/a/b.mp4 — asked under agent:comfyui:discord:direct:1",
            OpenClawMediaRefusal.Detail(null, "/a/b.mp4", url, "agent:main:discord:direct:1"));
    }

    [Fact]
    public void DetailAddsNothingWhenTheRouteIsTheConversationsOwnKey()
    {
        const string own = "agent:comfyui:discord:direct:1";
        var url = new OpenClawMediaSource("/a/b.mp4", own).Route;

        Assert.Equal("/a/b.mp4 — x", OpenClawMediaRefusal.Detail("{\"code\":\"x\"}", "/a/b.mp4", url, own));
    }

    [Fact]
    public void DetailAddsNothingWhenTheAskingKeyIsNotKnown()
    {
        var url = new OpenClawMediaSource("/a/b.mp4", "agent:comfyui:discord:direct:1").Route;

        Assert.Equal("/a/b.mp4", OpenClawMediaRefusal.Detail(null, "/a/b.mp4", url, null));
    }

    [Fact]
    public void DetailAddsNothingWhenTheRouteNamesNoSession()
    {
        Assert.Equal("/a/b.mp4", OpenClawMediaRefusal.Detail(null, "/a/b.mp4", new OpenClawMediaSource("/a/b.mp4", null).Route));
        Assert.Equal("/a/b.mp4", OpenClawMediaRefusal.Detail(null, "/a/b.mp4", null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("/no/query", null)]
    [InlineData("/r?source=%2Fa", null)]
    [InlineData("/r?source=%2Fa&sessionKey=", null)]
    [InlineData("/r?source=%2Fa&sessionKey=agent%3Ax%3Amain", "agent:x:main")]
    [InlineData("/r?source=%2Fa&sessionKey=agent%3Ax%3Amain&meta=1", "agent:x:main")]
    [InlineData("/r?source=%2Fa&flag=1&sessionKey=agent%3Ax%3Amain", "agent:x:main")]
    public void SessionKeyOfReadsTheKeyBackOutOfTheQuery(string? route, string? expected)
    {
        Assert.Equal(expected, OpenClawMediaSource.SessionKeyOf(route));
    }

    // ---- The HTTP half: a JSON body survives ReadResponseAsync intact -------
    //
    // Reusing OpenClawSocket.ReadResponseAsync over a MemoryStream, the same
    // seam OpenClawSocketTests already drives it through, rather than a fake
    // socket of this file's own: the interesting question is whether a meta
    // answer's bytes come back unmangled, and that is the exact thing this
    // method already proves for a picture's bytes.
    [Fact]
    public async Task AMetaJsonBodySurvivesTheHttpReadIntact()
    {
        const string json = "{\"available\":false,\"code\":\"outside-allowed-folders\",\"reason\":\"Outside allowed folders\"}";
        var body = Encoding.UTF8.GetBytes(json);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");

        using var stream = new MemoryStream(headers.Length + body.Length);
        stream.Write(headers);
        stream.Write(body);
        stream.Position = 0;

        var bytes = await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal(json, Encoding.UTF8.GetString(bytes!));
    }
}
