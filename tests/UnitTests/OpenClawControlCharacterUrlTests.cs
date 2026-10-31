using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-274 / CB-275 hardening: OpenClawSocket writes the request line by hand
// with the gateway bearer token already in the headers, so a CR or LF in a url
// taken from the gateway's own transcript is request/header injection. Covered
// at the socket, at both fetchers (before the cache and before the socket),
// and at each parser branch that produces a url.
[Collection("Settings")]
public class OpenClawControlCharacterUrlTests
{
    private const string Evil = "/x\r\nX-Evil: 1";

    [Theory]
    [InlineData("/ok", false)]
    [InlineData("/ok?a=b%0D%0A", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("/x\r\ny", true)]
    [InlineData("/x\ny", true)]
    [InlineData("/x\ry", true)]
    [InlineData("/x\0y", true)]
    [InlineData("/x\ty", true)]
    [InlineData("/x\u007fy", true)]
    public void OnlyAControlCharacterCountsAsOne(string? text, bool expected) =>
        Assert.Equal(expected, OpenClawSocket.HasControlCharacter(text));

    [Fact]
    public void TheRequestLineIsNeverBuiltFromAPathWithAControlCharacter()
    {
        Assert.Throws<System.ArgumentException>(() =>
            OpenClawSocket.GetRequest("h", 1, Evil, "token"));

        Assert.StartsWith("GET /ok HTTP/1.1\r\n", OpenClawSocket.GetRequest("h", 1, "/ok", "token"));
    }

    private static Dictionary<string, T> Cache<T>(string field) =>
        (Dictionary<string, T>)typeof(OpenClawSessions).GetField(
            field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    // The guard comes before the cache lookup, not after: a url that could end
    // the request line is not answered from memory either, so it can never be
    // "remembered" into looking fetched.
    [Fact]
    public async Task APictureFetchRefusesTheUrlBeforeTheCacheAndTheSocket()
    {
        var cache = Cache<byte[]?>("Media");
        cache[Evil] = new byte[] { 1, 2, 3 };

        try
        {
            Assert.Null(await OpenClawSessions.FetchMediaAsync(Evil, CancellationToken.None));
        }
        finally
        {
            cache.Remove(Evil);
        }
    }

    [Fact]
    public async Task AMetaFetchRefusesTheUrlBeforeTheCacheAndTheSocket()
    {
        var route = OpenClawMediaSource.MetaOf(Evil);
        var cache = Cache<string?>("MediaMeta");
        cache[route] = "{\"available\":true}";

        try
        {
            Assert.Null(await OpenClawSessions.FetchLocalMediaMetaAsync(Evil, CancellationToken.None));
        }
        finally
        {
            cache.Remove(route);
        }
    }

    private static List<HistoryTurn> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(json).RootElement.Clone(), null);

    // The existing url-form picture branch: a url with a control character is
    // no url, and with no inline bytes beside it there is no picture turn.
    [Fact]
    public void APictureBlockWhoseUrlCarriesAControlCharacterDrawsNothing()
    {
        Assert.Empty(Turns("[{\"role\":\"assistant\",\"content\":"
            + "[{\"type\":\"image\",\"url\":\"/x\\r\\nX-Evil: 1\"}]}]"));

        Assert.Single(Turns("[{\"role\":\"assistant\",\"content\":"
            + "[{\"type\":\"image\",\"url\":\"/x/full\"}]}]"));
    }

    // The new attachment arms: the block is kept (the agent did post a file)
    // but as omitted, with no url to fetch.
    [Fact]
    public void AnAttachmentBlockWhoseUrlCarriesAControlCharacterIsOmittedNotFetched()
    {
        var turn = Assert.Single(Turns("[{\"role\":\"assistant\",\"content\":"
            + "[{\"type\":\"video\",\"url\":\"/x\\r\\nX-Evil: 1\",\"fileName\":\"a.mp4\"}]}]"));

        Assert.True(turn.Attachment!.Value.Failed);
        Assert.Equal("omitted", turn.Attachment.Value.FailureCode);
        Assert.DoesNotContain("\r", turn.ImageUrl ?? "");
        Assert.DoesNotContain("\n", turn.ImageUrl ?? "");
    }
}
