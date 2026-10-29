using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// The pure half of CB-270's send policy: which route a reply takes, and the
// exact shape of the two requests. No gateway, no window.
public class OpenClawSendPolicyTests
{
    private static readonly OpenClawSessions.Delivery Address =
        new("discord", "user:2000000000000000001", "quillbot");

    // The only way to Native: listed native, never seen as a surface, no address.
    [Theory]
    [InlineData(OpenClawNativeKind.Main)]
    [InlineData(OpenClawNativeKind.Dashboard)]
    public void ANativeSessionNeverSeenAsASurfaceWithNoAddressIsNative(OpenClawNativeKind kind) =>
        Assert.Equal(OpenClawSendRoute.Native, OpenClawSendPolicy.RouteFor(kind, false, null));

    // Each guard on its own pushes a native session back to Surface.
    [Theory]
    [InlineData(OpenClawNativeKind.Main)]
    [InlineData(OpenClawNativeKind.Dashboard)]
    public void AnyOneGuardFailingMeansSurface(OpenClawNativeKind kind)
    {
        Assert.Equal(OpenClawSendRoute.Surface, OpenClawSendPolicy.RouteFor(kind, true, null));
        Assert.Equal(OpenClawSendRoute.Surface, OpenClawSendPolicy.RouteFor(kind, false, Address));
        Assert.Equal(OpenClawSendRoute.Surface, OpenClawSendPolicy.RouteFor(kind, true, Address));
    }

    // Not listed, and listed as not native, are Surface whatever else is true.
    [Theory]
    [InlineData(null)]
    [InlineData(OpenClawNativeKind.None)]
    public void UnknownOrNonNativeIsAlwaysSurface(OpenClawNativeKind? kind)
    {
        foreach (var seen in new[] { false, true })
        {
            foreach (var delivery in new OpenClawSessions.Delivery?[] { null, Address })
            {
                Assert.Equal(OpenClawSendRoute.Surface,
                    OpenClawSendPolicy.RouteFor(kind, seen, delivery));
            }
        }
    }

    [Fact]
    public void TheMirrorHasTodaysKeysInTodaysOrder()
    {
        var mirror = OpenClawSendPolicy.MirrorRequest(Address, "hi", "k1");

        Assert.Equal(new[] { "to", "message", "channel", "idempotencyKey", "accountId" },
            mirror.Keys.ToArray());
        Assert.Equal("user:2000000000000000001", mirror["to"]);
        Assert.Equal("discord", mirror["channel"]);
        Assert.Equal("k1", mirror["idempotencyKey"]);
        Assert.Equal("quillbot", mirror["accountId"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankAccountLeavesAccountIdOut(string? account)
    {
        var mirror = OpenClawSendPolicy.MirrorRequest(
            new OpenClawSessions.Delivery("whatsapp", "+15550000001", account), "hi", "k1");

        Assert.Equal(new[] { "to", "message", "channel", "idempotencyKey" }, mirror.Keys.ToArray());
    }

    // The round trip through the real recognizer: what the mirror writes is
    // what Classify reads back as ours.
    [Fact]
    public void TheMirrorMessageIsRecognisedAsOurs()
    {
        var message = (string)OpenClawSendPolicy.MirrorRequest(Address, "hi", "k1")["message"];

        Assert.StartsWith(OpenClawSender.MirrorPrefix, message);
        Assert.Equal("**(via Orbweaver)** hi", message);
        var sender = OpenClawSender.Classify(null, null, null, message);

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
        Assert.Equal("hi", sender.Text);
    }

    [Fact]
    public void ASurfaceChatSendDelivers()
    {
        var request = OpenClawSendPolicy.ChatSendRequest("agent:a:b", "hi", OpenClawSendRoute.Surface, "k1");

        Assert.Equal(new[] { "sessionKey", "message", "deliver", "idempotencyKey" }, request.Keys.ToArray());
        Assert.Equal("agent:a:b", request["sessionKey"]);
        Assert.Equal("hi", request["message"]);
        Assert.Equal(true, request["deliver"]);
        Assert.Equal("k1", request["idempotencyKey"]);
    }

    [Fact]
    public void ANativeChatSendDoesNotDeliver() =>
        Assert.Equal(false,
            OpenClawSendPolicy.ChatSendRequest("agent:a:b", "hi", OpenClawSendRoute.Native, "k1")["deliver"]);

    // --- the chat session's route (CB-270) ---

    private static OpenClawChatSession Session() =>
        new("openclaw:agent:a:dashboard:x", "agent:a:dashboard:x", "A");

    [Fact]
    public void ObservingNothingLeavesTheRouteOnTheSurface()
    {
        var chat = Session();
        chat.Observe(null);

        Assert.Null(chat.Native);
        Assert.Equal(OpenClawSendRoute.Surface, chat.Route);
    }

    [Fact]
    public void ObservingADashboardMakesTheRouteNative()
    {
        var chat = Session();
        chat.Observe(OpenClawNativeKind.Dashboard);

        Assert.Equal(OpenClawSendRoute.Native, chat.Route);
    }

    [Fact]
    public void ASurfaceObservationIsSticky()
    {
        var chat = Session();
        chat.Observe(OpenClawNativeKind.None);
        chat.Observe(OpenClawNativeKind.Dashboard);

        Assert.True(chat.SeenAsSurface);
        Assert.Equal(OpenClawSendRoute.Surface, chat.Route);
    }

    [Fact]
    public void ADeliveryAddressKeepsANativeSessionOnTheSurface()
    {
        var chat = Session();
        chat.Delivery = Address;
        chat.Observe(OpenClawNativeKind.Dashboard);

        Assert.Equal(OpenClawSendRoute.Surface, chat.Route);
    }

    // --- timeoutMs (Stage 4) ---

    [Fact]
    public void ATimeoutIsAppendedAfterTheIdempotencyKey()
    {
        var request = OpenClawSendPolicy.ChatSendRequest("agent:a:b", "hi", OpenClawSendRoute.Native, "k1", 600_000);

        Assert.Equal(new[] { "sessionKey", "message", "deliver", "idempotencyKey", "timeoutMs" }, request.Keys.ToArray());
        Assert.Equal(600_000, request["timeoutMs"]);
    }

    // With none chosen the request is exactly what it always was.
    [Fact]
    public void WithNoTimeoutTheRequestIsUnchanged()
    {
        var plain = OpenClawSendPolicy.ChatSendRequest("agent:a:b", "hi", OpenClawSendRoute.Native, "k1");
        var explicitNull = OpenClawSendPolicy.ChatSendRequest("agent:a:b", "hi", OpenClawSendRoute.Native, "k1", null);

        Assert.Equal(new[] { "sessionKey", "message", "deliver", "idempotencyKey" }, plain.Keys.ToArray());
        Assert.Equal(plain.Keys.ToArray(), explicitNull.Keys.ToArray());
    }

    // The gateway reads 0 as no timeout at all and a negative value as the
    // default; neither may be asked for.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveTimeoutIsRefused(int ms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenClawSendPolicy.ChatSendRequest("agent:a:b", "hi", OpenClawSendRoute.Native, "k1", ms));
}

