using System;
using Xunit;

namespace Orbweaver.Tests;

// Covers the exact shape of the request the cloud arm sends.
//
// **This file used to pin six headers against claude.ai and every one of those
// assertions was wrong.** They were the headers claude.ai's own web client
// sends, asserted twice over — once off a built request and once against the
// constants — which made a confidently wrong fact look doubly confirmed. The
// host was the error, not the headers: claude.ai answers a non-browser client
// with a Cloudflare challenge whatever it is sent, and against
// api.anthropic.com only Authorization and anthropic-version do anything. Two
// independent copies of a wrong measurement are still one wrong measurement,
// which is worth remembering before adding a third.
//
// The request builder is still the single copy of this shape, and the probe
// still references the app rather than building its own: a diagnostic that
// assembled the request itself could answer differently from the app and be
// believed.
public class ClaudeCloudRequestTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private static string? Header(System.Net.Http.HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public void TheAuthorizationHeaderIsABearerTokenAndNothingElse()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void TheVersionHeaderIsTheOtherHalfOfTheMeasuredSet()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Equal("2023-06-01", Header(request, "anthropic-version"));
        Assert.Equal("anthropic-version", CloudRequest.VersionHeader);
        Assert.Equal("2023-06-01", CloudRequest.VersionValue);
    }

    // The four that were carried over from claude.ai and are not sent.
    //
    // A test for absence rather than for presence, because the failure being
    // guarded against is somebody re-adding one from the old findings doc — at
    // which point a header set that reads fine goes back to describing a host
    // this arm does not talk to.
    [Theory]
    [InlineData("anthropic-beta")]
    [InlineData("anthropic-client-feature")]
    [InlineData("anthropic-client-platform")]
    [InlineData("x-organization-uuid")]
    public void TheClaudeAiHeadersAreNotSent(string name)
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Null(Header(request, name));
    }

    // **No cookie, ever.** Ours rides the CLI's OAuth token and nothing else.
    // Sending both would make it impossible to say which one was accepted.
    [Fact]
    public void NoCookieIsSent()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.False(request.Headers.Contains("Cookie"));
        Assert.DoesNotContain(request.Headers,
            h => string.Equals(h.Key, "cookie", StringComparison.OrdinalIgnoreCase));
    }

    // And no body. The empty ByteArrayContent that used to be attached was there
    // to reproduce a browser's GET byte for byte against the wrong host.
    [Fact]
    public void NoBodyRidesAGet()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Null(request.Content);
    }

    [Fact]
    public void ItIsAGetToTheAccountApiOverHttps()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.SessionsPath);

        Assert.Equal(System.Net.Http.HttpMethod.Get, request.Method);
        Assert.NotNull(request.RequestUri);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("api.anthropic.com", request.RequestUri.Host);
        Assert.Equal("/v2/ccr-sessions", request.RequestUri.AbsolutePath);
    }

    [Fact]
    public void TheHostConstantIsTheAccountApiAndNotClaudeAi()
    {
        Assert.Equal("https://api.anthropic.com", CloudRequest.Host);
        Assert.DoesNotContain("claude.ai", CloudRequest.Host, StringComparison.Ordinal);
        Assert.Equal("/v2/ccr-sessions", CloudRequest.SessionsPath);
    }

    // --- the paths -----------------------------------------------------------

    [Fact]
    public void TheListingPathCarriesALimitAndNothingElse()
    {
        Assert.Equal("/v2/ccr-sessions?limit=100", CloudRequest.ListPath(100, null));
        Assert.Equal("/v2/ccr-sessions?limit=100", CloudRequest.ListPath(100, "   "));
    }

    [Fact]
    public void TheListingPathFollowsACursorWhenGivenOne()
    {
        Assert.Equal("/v2/ccr-sessions?limit=100&after_id=session_abc",
            CloudRequest.ListPath(100, "session_abc"));
    }

    // **Measured: `limit=200` is refused** with "must be greater than or equal to
    // 0 and less than 101". So a caller asking for more gets the ceiling rather
    // than a 400, which is the difference between a walk that is slower than it
    // could be and one that returns nothing at all.
    [Theory]
    [InlineData(101, 100)]
    [InlineData(200, 100)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    public void TheLimitIsClampedToWhatTheEndpointAccepts(int asked, int sent)
    {
        Assert.Equal($"/v2/ccr-sessions?limit={sent}", CloudRequest.ListPath(asked, null));
    }

    [Fact]
    public void TheMaximumPageSizeIsTheMeasuredCeiling()
    {
        Assert.Equal(100, CloudRequest.MaxPageSize);
    }

    [Fact]
    public void ASingleSessionPathHangsOffTheCollection()
    {
        Assert.Equal("/v2/ccr-sessions/session_01ABC", CloudRequest.SessionPath("session_01ABC"));
    }

    [Fact]
    public void AnEventsPathPagesTheSameWay()
    {
        Assert.Equal("/v2/ccr-sessions/session_01ABC/events?limit=100",
            CloudRequest.EventsPath("session_01ABC", 100, null));

        Assert.Equal("/v2/ccr-sessions/session_01ABC/events?limit=100&after_id=evt_9",
            CloudRequest.EventsPath("session_01ABC", 100, "evt_9"));
    }

    // The list the panel's history reads: the write host's, newest first, paged
    // backwards by `cursor` (measured 2026-11-17). Capped and escaped like the
    // others, and refused outright for a malformed id, as the write paths are.
    [Fact]
    public void TheNewestEventsPathAsksForNewestFirstAndPagesBackwards()
    {
        Assert.Equal("/v1/code/sessions/session_01ABC/events?limit=100&sort_order=desc",
            CloudRequest.NewestEventsPath("session_01ABC", 100, null));

        Assert.Equal("/v1/code/sessions/session_01ABC/events?limit=100&sort_order=desc&cursor=2448",
            CloudRequest.NewestEventsPath("session_01ABC", 100, "2448"));

        Assert.Equal("/v1/code/sessions/session_01ABC/events?limit=100&sort_order=desc",
            CloudRequest.NewestEventsPath("session_01ABC", 100, "  "));

        Assert.EndsWith("&cursor=1%262",
            CloudRequest.NewestEventsPath("session_01ABC", 100, "1&2"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "limit=1&")]
    [InlineData(5000, "limit=100&")]
    public void TheNewestEventsPathCapsItsLimit(int asked, string sent)
    {
        Assert.Contains(sent, CloudRequest.NewestEventsPath("session_01ABC", asked, null), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestEventsPathRefusesAMalformedId()
    {
        Assert.Null(CloudRequest.NewestEventsPath("session_a/../admin", 100, null));
        Assert.Null(CloudRequest.NewestEventsPath(null, 100, null));
    }

    // An id arrives from a payload nobody here owns and ends up in a path. Escaped
    // rather than trusted — a path is the one place an external string turns into
    // a different request.
    [Fact]
    public void AnIdWithPathCharactersInItIsEscaped()
    {
        Assert.Equal("/v2/ccr-sessions/session_a%2F..%2Fadmin",
            CloudRequest.SessionPath("session_a/../admin"));

        Assert.Equal("/v2/ccr-sessions/session_a%3Fx%3D1/events?limit=100",
            CloudRequest.EventsPath("session_a?x=1", 100, null));
    }

    [Fact]
    public void ACursorWithQueryCharactersInItIsEscaped()
    {
        Assert.Contains("after_id=session_a%26limit%3D1",
            CloudRequest.ListPath(100, "session_a&limit=1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathIsWhateverTheCallerAsksFor()
    {
        using var request = CloudRequest.Build(Token, "/v2/ccr-sessions?limit=1");

        Assert.Equal("https://api.anthropic.com/v2/ccr-sessions?limit=1",
            request.RequestUri!.ToString());
    }

    // --- the cadences --------------------------------------------------------

    // Not cadences, placeholders. That they are positive and finite is all a test
    // can honestly say about numbers nobody has measured; the comment on the
    // constants names the three questions that would settle them.
    [Fact]
    public void BothIntervalsArePositiveUnmeasuredPlaceholders()
    {
        Assert.True(CloudRequest.UnmeasuredWalkInterval > TimeSpan.Zero);
        Assert.True(CloudRequest.UnmeasuredFirstPageInterval > TimeSpan.Zero);
    }

    // The deep walk is the expensive one and the first-page poll is the cheap one,
    // so the walk being the rarer of the two is the one relationship between them
    // that is a decision rather than a guess.
    [Fact]
    public void TheWalkIsRarerThanTheFirstPagePoll()
    {
        Assert.True(CloudRequest.UnmeasuredWalkInterval > CloudRequest.UnmeasuredFirstPageInterval);
    }

    [Fact]
    public void ThePageCapIsPositive()
    {
        Assert.True(CloudRequest.MaxPagesPerWalk > 0);
    }

    // The constant naming the credential store, kept beside the request tests
    // because it is the other measured-from-a-real-machine string in this arm.
    [Fact]
    public void TheKeychainServiceIsTheOneTheCliWritesUnder()
    {
        Assert.Equal("Claude Code-credentials", ClaudeCliCredentials.KeychainService);
    }

    // --- writes (CB-199) -------------------------------------------------------

    private const string SessionId = "session_01ABCdef";

    [Fact]
    public void AWritePathIsUnderV1CodeAndNotUnderTheRoster()
    {
        Assert.Equal("/v1/code/sessions", CloudRequest.CodeSessionsPath);
        Assert.Equal("/v1/code/sessions/session_01ABCdef/events",
            CloudRequest.CodeEventsPath(SessionId));
    }

    // A write refuses a malformed id outright rather than escaping it and
    // sending it anyway, which is what the read paths above do. Every one of
    // these is a request that never gets built.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("session_")]
    [InlineData("session_a/../admin")]
    [InlineData("session_a?x=1")]
    [InlineData("session_a%2F")]
    [InlineData("01ABC")]
    public void AMalformedIdGetsNoWritePath(string? id)
    {
        Assert.Null(CloudRequest.CodeEventsPath(id));
        Assert.Null(CloudRequest.CodeSessionPath(id));
    }

    // The live-status read (CB-199): the session's own record on the write host.
    [Fact]
    public void TheLiveStatusPathIsTheSessionItselfUnderV1Code()
    {
        Assert.Equal("/v1/code/sessions/session_01ABCdef", CloudRequest.CodeSessionPath(SessionId));
    }

    [Fact]
    public void APostCarriesItsBodyAsJson()
    {
        const string body = """{"events":[]}""";
        using var request = CloudRequest.Build(Token, CloudRequest.CodeEventsPath(SessionId)!,
            System.Net.Http.HttpMethod.Post, body);

        Assert.Equal(System.Net.Http.HttpMethod.Post, request.Method);
        Assert.Equal("https://api.anthropic.com/v1/code/sessions/session_01ABCdef/events",
            request.RequestUri!.ToString());
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(CloudRequest.JsonMediaType, request.Content.Headers.ContentType.MediaType);
        Assert.Equal(body, request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    // The measured set for a write is the same two headers as for a read, and
    // the two the claude.ai client sent are still absent — the gate ablated
    // both on this route and neither changed the answer.
    [Fact]
    public void APostCarriesTheSameTwoHeadersAsAGet()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.CodeEventsPath(SessionId)!,
            System.Net.Http.HttpMethod.Post, "{}");

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal("2023-06-01", Header(request, "anthropic-version"));
        Assert.Null(Header(request, "anthropic-beta"));
        Assert.Null(Header(request, "x-organization-uuid"));
        Assert.Equal(2, System.Linq.Enumerable.Count(request.Headers));
    }

    // Content-Type rides with a body and only with one: a method on its own
    // does not attach content.
    [Fact]
    public void AMethodWithoutABodyCarriesNoContent()
    {
        using var request = CloudRequest.Build(Token, CloudRequest.CodeEventsPath(SessionId)!,
            System.Net.Http.HttpMethod.Post);

        Assert.Null(request.Content);
    }

    // The read path is unchanged by the new parameters: omitting them and
    // passing their defaults build the same request.
    [Fact]
    public void AGetBuiltWithExplicitDefaultsIsTheSameAsOneWithout()
    {
        using var implicitly = CloudRequest.Build(Token, CloudRequest.SessionsPath);
        using var explicitly = CloudRequest.Build(Token, CloudRequest.SessionsPath, null, null);

        Assert.Equal(implicitly.Method, explicitly.Method);
        Assert.Equal(implicitly.RequestUri, explicitly.RequestUri);
        Assert.Null(explicitly.Content);
        Assert.Equal(implicitly.Headers.ToString(), explicitly.Headers.ToString());
    }

    // The canary: the token reaches the Authorization header and nothing else.
    // A body built by ClaudeCloudSend never contains it, and neither does
    // anything the request carries outside that one header.
    [Fact]
    public void TheTokenNeverReachesAWriteBody()
    {
        var body = ClaudeCloudSend.UserMessageBody(SessionId, "hello", "uuid-1");
        using var request = CloudRequest.Build(Token, CloudRequest.CodeEventsPath(SessionId)!,
            System.Net.Http.HttpMethod.Post, body);

        var sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(Token, sent, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ClaudeCloudSend.InterruptBody("r", "u"), StringComparison.Ordinal);
    }

    [Fact]
    public void AContextDefaultsToABodilessGet()
    {
        var context = new CloudRequestContext("token", CloudRequest.SessionsPath);

        Assert.Null(context.Method);
        Assert.Null(context.Body);

        var write = new CloudRequestContext("token", "/p", System.Net.Http.HttpMethod.Post, "{}");
        Assert.Equal(System.Net.Http.HttpMethod.Post, write.Method);
        Assert.Equal("{}", write.Body);
    }
}
