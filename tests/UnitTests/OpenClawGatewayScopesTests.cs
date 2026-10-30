using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6: what the connect frame asks for, from two independent switches.
//
// Read is always requested; write when replying is on; approvals when approving is
// on. Four sets, approving independent of replying, and a `caps` list that exists
// only when approving: the gateway sends an approval's requested broadcast to a
// non-admin client only if it declared a cap at connect, so a device that never
// asked to approve declares nothing.
//
// The cap NAME ("approvals") is declared, read from the gateway's own client-cap
// list, not measured against a live pairing; the probe can try the alternatives.
[Collection("Settings")]
public class OpenClawGatewayScopesTests : IDisposable
{
    private readonly bool _reply = OrbweaverSettings.OpenClawReplyEnabled;
    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public void Dispose()
    {
        OpenClawGateway.ScopesOverride = null;
        OpenClawGateway.CapsOverride = null;
        OrbweaverSettings.OpenClawReplyEnabled = _reply;
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
    }

    [Theory]
    [InlineData(false, false, new[] { "operator.read" })]
    [InlineData(true, false, new[] { "operator.read", "operator.write" })]
    [InlineData(false, true, new[] { "operator.read", "operator.approvals" })]
    [InlineData(true, true, new[] { "operator.read", "operator.write", "operator.approvals" })]
    public void TheFourSwitchCombinationsAskForExactlyTheirScopes(bool reply, bool approve, string[] expected) =>
        Assert.Equal(expected, OpenClawGateway.ScopesFor(reply, approve));

    // Approving does not need replying, and replying grants nothing about approving.
    [Fact]
    public void ApprovingAndReplyingAreIndependent()
    {
        Assert.DoesNotContain("operator.write", OpenClawGateway.ScopesFor(false, true));
        Assert.DoesNotContain("operator.approvals", OpenClawGateway.ScopesFor(true, false));
    }

    [Fact]
    public void OnlyApprovingDeclaresACap()
    {
        Assert.Equal(new[] { "approvals" }, OpenClawGateway.CapsFor(true));
        Assert.Empty(OpenClawGateway.CapsFor(false));
    }

    // The frame as sent. The settings are what decide it, with nothing overriding.
    private static async Task<JsonElement> ConnectFrameAsync()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : FakeGatewaySocket.Ok(request.Id, new { });

        using var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        await gateway.ConnectAsync(null, CancellationToken.None);

        return socket.Requests.Single(r => r.Method == "connect").Params;
    }

    private static string[] ScopesIn(JsonElement frame) =>
        frame.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheConnectFrameCarriesTheScopesAndTheCapOnlyWhenApproving(bool reply, bool approve)
    {
        OrbweaverSettings.OpenClawReplyEnabled = reply;
        OrbweaverSettings.OpenClawApproveEnabled = approve;

        var frame = await ConnectFrameAsync();

        Assert.Equal(OpenClawGateway.ScopesFor(reply, approve), ScopesIn(frame));
        Assert.Equal(approve, frame.TryGetProperty("caps", out var caps));
        if (approve) Assert.Equal("approvals", Assert.Single(caps.EnumerateArray()).GetString());
    }

    // The signature covers the scopes that were sent, whichever set that is: the
    // gateway rebuilds the payload from the frame and an approving device's
    // signature must verify against its own three scopes, not the old two.
    [Fact]
    public async Task TheSignatureCoversTheApprovingScopeSet()
    {
        OrbweaverSettings.OpenClawReplyEnabled = true;
        OrbweaverSettings.OpenClawApproveEnabled = true;

        var frame = await ConnectFrameAsync();
        var device = frame.GetProperty("device");
        var client = frame.GetProperty("client");
        var identity = OpenClawIdentity.Current();

        var rebuilt = OpenClawIdentity.AuthPayload(
            device.GetProperty("id").GetString()!,
            client.GetProperty("id").GetString()!,
            client.GetProperty("mode").GetString()!,
            frame.GetProperty("role").GetString()!,
            ScopesIn(frame),
            device.GetProperty("signedAt").GetInt64(),
            frame.GetProperty("auth").GetProperty("token").GetString(),
            device.GetProperty("nonce").GetString()!,
            client.GetProperty("platform").GetString()!,
            "");

        Assert.Equal(
            new[] { "operator.read", "operator.write", "operator.approvals" }, ScopesIn(frame));
        Assert.Equal(OpenClawIdentity.Sign(identity, rebuilt), device.GetProperty("signature").GetString());
    }

    // The probe's seam: an explicit set and cap list beat the settings, and
    // clearing them gives the settings back.
    [Fact]
    public async Task AnOverrideBeatsTheSettingsAndClearingItGivesThemBack()
    {
        OrbweaverSettings.OpenClawReplyEnabled = false;
        OrbweaverSettings.OpenClawApproveEnabled = false;
        OpenClawGateway.ScopesOverride = new[] { "operator.read", "operator.approvals" };
        OpenClawGateway.CapsOverride = new[] { "exec-approvals" };

        var overridden = await ConnectFrameAsync();

        Assert.Equal(new[] { "operator.read", "operator.approvals" }, ScopesIn(overridden));
        Assert.Equal("exec-approvals", overridden.GetProperty("caps")[0].GetString());

        OpenClawGateway.ScopesOverride = null;
        OpenClawGateway.CapsOverride = null;

        var plain = await ConnectFrameAsync();

        Assert.Equal(new[] { "operator.read" }, ScopesIn(plain));
        Assert.False(plain.TryGetProperty("caps", out _));
    }
}
