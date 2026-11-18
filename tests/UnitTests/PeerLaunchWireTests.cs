using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-357: the codec for the four peer launch messages. Every parser is fed the
// shapes a confused or hostile peer could send, because each answers null
// rather than a half-read body.
public class PeerLaunchWireTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly PeerLaunchInfo Full = new(
        true, "macos", "/Users/a", true,
        [new PeerLaunchCli("claude", true), new PeerLaunchCli("codex", false)],
        [new PeerLaunchAccount("claude", "default", "Default")],
        ["/Users/a/src/x"]);

    [Fact]
    public void AnAllowedInfoRoundTripsExactly()
    {
        var back = PeerLaunchWire.ParseInfo(PeerLaunchWire.EncodeInfo(Full))!;

        Assert.True(back.Allowed);
        Assert.Equal("macos", back.Os);
        Assert.Equal("/Users/a", back.Home);
        Assert.True(back.Terminal);
        Assert.Equal(Full.Clis, back.Clis);
        Assert.Equal(Full.Accounts, back.Accounts);
        Assert.Equal(Full.Recents, back.Recents);
    }

    [Fact]
    public void AnInfoWithTheSwitchOffSendsNothingButTheOs()
    {
        // Encoded from a fully-built info: the encoder itself strips it.
        var body = PeerLaunchWire.EncodeInfo(Full with { Allowed = false });

        Assert.Equal(["allowed", "os"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void ARefusingDesksExtraFieldsAreNotBelievedOnTheWayIn()
    {
        var back = PeerLaunchWire.ParseInfo(J(
            """{"allowed":false,"os":"windows","home":"C:\\x","recents":["C:\\x"]}"""))!;

        Assert.False(back.Allowed);
        Assert.Equal("windows", back.Os);
        Assert.Null(back.Home);
        Assert.Null(back.Recents);
    }

    [Fact]
    public void AnAllowedInfoWithNoListsGetsEmptyOnes()
    {
        var back = PeerLaunchWire.ParseInfo(J("""{"allowed":true,"os":"macos"}"""))!;

        Assert.Empty(back.Clis!);
        Assert.Empty(back.Accounts!);
        Assert.Empty(back.Recents!);
    }

    public static TheoryData<string> BadInfo => new()
    {
        "[]", "\"x\"", "null", "7",
        """{"allowed":"yes","os":"macos"}""",
        """{"allowed":true}""",
        """{"allowed":true,"os":""}""",
        """{"allowed":true,"os":"mac\u0001os"}""",
        """{"allowed":true,"os":"macos","home":"a\nb"}""",
        """{"allowed":true,"os":"macos","clis":[null]}""",
        """{"allowed":true,"os":"macos","clis":[{"cli":"","installed":true}]}""",
        """{"allowed":true,"os":"macos","accounts":[null]}""",
        """{"allowed":true,"os":"macos","accounts":[{"cli":"claude","id":"","label":"x"}]}""",
        """{"allowed":true,"os":"macos","accounts":[{"cli":"claude","id":"a","label":""}]}""",
        """{"allowed":true,"os":"macos","recents":[""]}""",
        """{"allowed":true,"os":"macos","recents":[null]}""",
    };

    [Theory]
    [MemberData(nameof(BadInfo))]
    public void AMalformedInfoIsNoAnswer(string json) =>
        Assert.Null(PeerLaunchWire.ParseInfo(J(json)));

    [Fact]
    public void AnUndefinedBodyIsNoAnswerForEveryParser()
    {
        Assert.Null(PeerLaunchWire.ParseInfo(default));
        Assert.Null(PeerLaunchWire.ParseLaunch(default));
        Assert.Null(PeerLaunchWire.ParseResult(default));
    }

    [Fact]
    public void InfoListsAreBounded()
    {
        string Clis(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"cli":"c{{i}}","installed":true}"""));
        string Accounts(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"cli":"claude","id":"i{{i}}","label":"l"}"""));
        string Recents(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"\"/r{i}\""));

        Assert.NotNull(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","clis":[{{Clis(PeerLaunchWire.MaxClis)}}]}""")));
        Assert.Null(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","clis":[{{Clis(PeerLaunchWire.MaxClis + 1)}}]}""")));
        Assert.NotNull(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","accounts":[{{Accounts(PeerLaunchWire.MaxAccounts)}}]}""")));
        Assert.Null(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","accounts":[{{Accounts(PeerLaunchWire.MaxAccounts + 1)}}]}""")));
        Assert.NotNull(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","recents":[{{Recents(PeerLaunchWire.MaxRecents)}}]}""")));
        Assert.Null(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","recents":[{{Recents(PeerLaunchWire.MaxRecents + 1)}}]}""")));
    }

    [Fact]
    public void AStringOverTheBoundIsRefused()
    {
        var long_ = new string('a', PeerLaunchWire.MaxText + 1);

        Assert.Null(PeerLaunchWire.ParseInfo(J($$"""{"allowed":true,"os":"m","home":"{{long_}}"}""")));
    }

    // --- launch ---------------------------------------------------------------

    private static readonly PeerLaunchWire.LaunchRequest Req =
        new("/w", "claude", "", "", "default", "", "", "nonce", 1_800_000_000, "sig", "cert");

    [Fact]
    public void ALaunchRoundTripsAndUsesTheWireNames()
    {
        var body = PeerLaunchWire.EncodeLaunch(Req);

        Assert.Equal(
            ["account", "cert", "cli", "cwd", "expires_at", "first_message", "model", "nonce",
             "parent_session_id", "persona", "sig"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Req, PeerLaunchWire.ParseLaunch(body));
    }

    [Fact]
    public void ALaunchWithTheOptionalFieldsAbsentGetsEmptyOnes()
    {
        var back = PeerLaunchWire.ParseLaunch(J(
            """{"cwd":"/w","cli":"claude","account":"default","nonce":"n","expires_at":5,"sig":"s","cert":"c"}"""))!;

        Assert.Equal("", back.Persona);
        Assert.Equal("", back.Model);
        Assert.Equal("", back.ParentSessionId);
        Assert.Equal("", back.FirstMessage);
    }

    [Fact]
    public void AFirstMessageMayHoldNewlinesButNotRunOnForever()
    {
        Assert.NotNull(PeerLaunchWire.ParseLaunch(PeerLaunchWire.EncodeLaunch(Req with { FirstMessage = "a\nb" })));
        Assert.Null(PeerLaunchWire.ParseLaunch(
            PeerLaunchWire.EncodeLaunch(Req with { FirstMessage = new string('x', 16 * 1024 + 1) })));
    }

    public static TheoryData<string> BadLaunch => new()
    {
        "[]", "null", "\"x\"",
        """{"cwd":"/w","cli":"claude","account":"default","nonce":"n","expires_at":"soon","sig":"s","cert":"c"}""",
        """{"cwd":"/w","cli":"claude","account":"default","nonce":"n","expires_at":0,"sig":"s","cert":"c"}""",
        """{"cli":"claude","account":"default","nonce":"n","expires_at":5,"sig":"s","cert":"c"}""",
        """{"cwd":"/w","account":"default","nonce":"n","expires_at":5,"sig":"s","cert":"c"}""",
        """{"cwd":"/w","cli":"claude","nonce":"n","expires_at":5,"sig":"s","cert":"c"}""",
        """{"cwd":"/w","cli":"claude","account":"default","expires_at":5,"sig":"s","cert":"c"}""",
        """{"cwd":"/w","cli":"claude","account":"default","nonce":"n","expires_at":5,"cert":"c"}""",
        """{"cwd":"/w","cli":"claude","account":"default","nonce":"n","expires_at":5,"sig":"s"}""",
        """{"cwd":"/w\u0000x","cli":"claude","account":"default","nonce":"n","expires_at":5,"sig":"s","cert":"c"}""",
    };

    [Theory]
    [MemberData(nameof(BadLaunch))]
    public void AnIncompleteLaunchIsNull(string json) =>
        Assert.Null(PeerLaunchWire.ParseLaunch(J(json)));

    // --- launch-result --------------------------------------------------------

    [Fact]
    public void AResultRoundTripsWithAndWithoutItsOptionalFields()
    {
        var ok = new PeerLaunchResult(true, "launched", "sid-1", null);
        var refused = new PeerLaunchResult(false, "no_terminal", null, "tmux is not installed");

        Assert.Equal(ok, PeerLaunchWire.ParseResult(PeerLaunchWire.EncodeResult(ok)));
        Assert.Equal(refused, PeerLaunchWire.ParseResult(PeerLaunchWire.EncodeResult(refused)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"code":""}""")]
    [InlineData("""{"ok":"yes","code":"launched"}""")]
    [InlineData("""{"ok":true,"code":"launched","session_id":"a\nb"}""")]
    [InlineData("""{"ok":false,"code":"x","why":"a\u0007"}""")]
    public void AMalformedResultIsNull(string json) =>
        Assert.Null(PeerLaunchWire.ParseResult(J(json)));
}
