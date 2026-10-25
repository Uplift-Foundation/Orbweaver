using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// The gateway connection, driven over an in-memory socket.
//
// What is worth pinning here is not "does a WebSocket work" — the BCL's does —
// but the handshake and the correlation, both of which fail in ways that are
// almost impossible to diagnose from the other end. The gateway answers a
// mis-signed payload with "unauthorized", a frame it did not expect with
// nothing, and a rejected connect by closing with the reason in the close frame
// rather than in the response. Each of those is a case below.
//
// Nothing here opens a socket. OpenClawGateway takes its connector as a
// constructor argument for exactly this reason — see its comment.
[Collection("Settings")]
public class OpenClawGatewayTests
{
    // Shares the settings collection because ConnectAsync signs with
    // OpenClawIdentity.Current(), which is a process-wide cache over a file in
    // the settings directory. Running alongside a test that moves that directory
    // would sign with one key and assert against another.

    internal static OpenClawGateway Gateway(
        WebSocket socket,
        string gatewayToken = "gw-token",
        string fingerprint = "fp-abc",
        TimeSpan? challengeTimeout = null,
        TimeSpan? requestTimeout = null) =>
        new("gw.local", 4443, gatewayToken,
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, fingerprint)),
            // No deadline at all, which is CB-23's answer to a number that has
            // now been raised twice and flaked at both values.
            //
            // The history is worth keeping because it is what rules the middle
            // ground out. Two seconds was missable when the machine was busy and
            // this class failed intermittently four times in one session that
            // way; raising it to thirty helped measurably, and then CI failed
            // here again at *exactly* thirty, which was read as a genuine hang
            // on the grounds that a busy machine does not lose half a minute.
            //
            // A busy machine does not, but a starved *thread pool* does. Under
            // a pool held by blocked work items — min threads forced to one,
            // every worker parked — the handshake over this very fake took
            // **25.5 seconds** on an idle-CPU Mac, against a thirty-second wall.
            // So thirty is not a wall a hang has to climb; it is four seconds of
            // headroom over a scheduling delay that is already measurable, and a
            // failure at thirty tells you nothing that a failure at two did not.
            // (That measurement is of a deliberately pathological pool, not of
            // a CI runner — it bounds what scheduling *can* cost, not what CI
            // actually costs. Either way it is not a number to sit just above.)
            //
            // Nothing here talks to a network. The socket is a fake answering
            // from memory, so a correct implementation replies immediately and
            // an expired deadline never means "the gateway is slow" — it only
            // ever means "this process did not get scheduled". That is not a
            // fact about the gateway and it has no business failing an
            // assertion about one. Removing the deadline makes the class say
            // exactly one thing: a *failure* is a protocol bug, and a *hang* is
            // a lost frame or a deadlock, reported by the job timeout with the
            // test's name on it.
            //
            // The three tests that are genuinely about a timeout pass their own
            // small explicit values and are unaffected.
            challengeTimeout ?? Timeout.InfiniteTimeSpan,
            requestTimeout ?? Timeout.InfiniteTimeSpan);

    // Connect, and say what happened when it didn't.
    //
    // CB-23 exists because this was `await gateway.ConnectAsync(...)` with the
    // result dropped on the floor. Every failure path leaves GrantedScopes at
    // its empty default, so a handshake that timed out surfaced three lines
    // later as `Assert.Equal() Failure: Expected ["operator.read",
    // "operator.write"], Actual: []` — a message that names neither the timeout
    // nor the outcome nor the gateway's own detail, and reads for all the world
    // like the scopes being parsed wrong.
    //
    // Discarding the result is the bug, not the timeout: the value that says
    // what went wrong was returned and thrown away.
    private static async Task<OpenClawGateway.ConnectResult> ConnectOrExplainAsync(
        OpenClawGateway gateway)
    {
        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.True(
            result.Outcome == OpenClawGateway.Outcome.Connected,
            $"the handshake did not connect: {result.Outcome} — {result.Detail ?? "no detail"}");

        return result;
    }

    // A gateway that accepts the connect and grants what was asked for.
    internal static FakeGatewaySocket Accepting(
        IEnumerable<string>? scopes = null,
        string nonce = "nonce-1",
        object? policy = null,
        object? server = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce });

        socket.OnRequest = request => request.Method == "connect"
            ? FakeGatewaySocket.Ok(request.Id, new
            {
                protocol = 4,
                server = server ?? new { version = "1.2.3" },
                auth = new { scopes = scopes ?? new[] { "operator.read" } },
                policy = policy ?? new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
            })
            : FakeGatewaySocket.Ok(request.Id, new { });

        return socket;
    }

    // The whole handshake, and the fields the gateway rebuilds the signature
    // from. Asserted together because they have to agree to the character: the
    // gateway recomputes the payload out of what it was sent, so a `client.mode`
    // that disagrees with the signed `clientMode` verifies as a forgery and is
    // reported as "unauthorized" with nothing else to go on.
    [Fact]
    public async Task ConnectSendsASignedV3HandshakeTheGatewayCanRebuild()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        var connect = Assert.Single(socket.Requests);
        Assert.Equal("connect", connect.Method);

        var p = connect.Params;
        Assert.Equal(4, p.GetProperty("minProtocol").GetInt32());
        Assert.Equal(4, p.GetProperty("maxProtocol").GetInt32());
        Assert.Equal("operator", p.GetProperty("role").GetString());

        var client = p.GetProperty("client");
        Assert.Equal("gateway-client", client.GetProperty("id").GetString());
        Assert.Equal("ui", client.GetProperty("mode").GetString());

        // The one field that used to be hardcoded "macos" regardless of the
        // machine. It is reported against the paired device record and read at
        // the approval prompt, so it says what this machine actually is.
        var platform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux" : "unknown";
        Assert.Equal(platform, client.GetProperty("platform").GetString());

        // The signature, verified the way the gateway does it: rebuild the
        // payload from the fields that were sent, and check it against the
        // public key in the same frame.
        var device = p.GetProperty("device");
        var identity = OpenClawIdentity.Current();

        Assert.Equal(identity.DeviceId, device.GetProperty("id").GetString());
        Assert.Equal(OpenClawIdentity.Base64Url(identity.PublicKey),
            device.GetProperty("publicKey").GetString());
        Assert.Equal("nonce-1", device.GetProperty("nonce").GetString());

        var rebuilt = OpenClawIdentity.AuthPayload(
            device.GetProperty("id").GetString()!,
            client.GetProperty("id").GetString()!,
            client.GetProperty("mode").GetString()!,
            p.GetProperty("role").GetString()!,
            p.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!),
            device.GetProperty("signedAt").GetInt64(),

            // Which token is signed is not obvious and not documented: the
            // gateway takes `auth.token ?? auth.deviceToken`, so once a gateway
            // token is being sent it is the gateway token that gets signed.
            p.GetProperty("auth").GetProperty("token").GetString(),
            device.GetProperty("nonce").GetString()!,
            client.GetProperty("platform").GetString()!,
            "");

        Assert.Equal(
            OpenClawIdentity.Sign(identity, rebuilt),
            device.GetProperty("signature").GetString());
    }

    // The challenge is already on the socket when ConnectAsync starts — that is
    // how every fake here is written, and how a gateway that writes it the
    // instant the upgrade completes looks. This is the case that failed on
    // windows-latest when the receive loop consumed the frame before
    // WaitForChallengeAsync subscribed.
    //
    // It used to make its point with a two-second deadline — "a lost event does
    // not get faster if you wait", so passing quickly was the assertion. CB-23
    // took that deadline away, because it could not tell a lost event from a
    // slow scheduler and the second is measurable on a starved pool. What a
    // regression looks like now is a hang rather than a failure: a challenge
    // nobody is listening for is waited on forever, and the job timeout names
    // this test. Uglier to read in a CI log, and unambiguous, which the two
    // seconds was not.
    [Fact]
    public async Task AChallengeAlreadyOnTheSocketIsNotLost()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        Assert.Equal("nonce-1", socket.Requests[0].Params.GetProperty("device")
            .GetProperty("nonce").GetString());
    }

    // What the handshake's answer configures. All four are read back out of the
    // response rather than defaulted, and the payload ceiling in particular is
    // load-bearing: the receive loop bounds a frame by it, so a wrong value
    // either truncates real traffic or removes the bound.
    [Fact]
    public async Task TheHandshakeResponseSetsTheScopesVersionAndPolicy()
    {
        var socket = Accepting(new[] { "operator.read", "operator.write" });
        using var gateway = Gateway(socket, fingerprint: "fp-observed");

        await ConnectOrExplainAsync(gateway);

        Assert.Equal(new[] { "operator.read", "operator.write" }, gateway.GrantedScopes);
        Assert.Equal("1.2.3", gateway.ServerVersion);
        Assert.Equal(15_000, gateway.TickIntervalMs);
        Assert.Equal(1_048_576, gateway.MaxPayload);

        // Reported so the settings window can show the user the value they are
        // being asked to trust.
        Assert.Equal("fp-observed", gateway.ObservedFingerprint);
    }

    // A gateway that answers `protocol` but no server version. The protocol
    // number stands in, because a blank version reads as a broken connection.
    [Fact]
    public async Task WithNoServerVersionTheProtocolNumberStandsIn()
    {
        var socket = Accepting(server: new { });
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        Assert.Equal("4", gateway.ServerVersion);
    }

    // A policy block with neither field leaves the defaults alone rather than
    // zeroing them — a maxPayload of 0 would drop every frame.
    [Fact]
    public async Task AnEmptyPolicyLeavesTheDefaultsInPlace()
    {
        var socket = Accepting(policy: new { });
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        Assert.Equal(30_000, gateway.TickIntervalMs);
        Assert.Equal(26_214_400, gateway.MaxPayload);
    }

    // Connected with nothing granted. This is the shape the original spike hit
    // with only a gateway token: every read method then refuses individually,
    // which reads like a broken feature rather than an unfinished pairing — so
    // it is named here instead.
    [Fact]
    public async Task ConnectedWithNoScopesIsPairingPendingRatherThanSuccess()
    {
        var socket = Accepting(Array.Empty<string>());
        using var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.PairingPending, result.Outcome);
        Assert.Contains("approve this device", result.Detail);
    }

    // The gateway speaks first, so there is nothing to send until the challenge
    // arrives. A challenge with no nonce leaves nothing to sign — the same dead
    // end as no challenge, and until CB-23 reported with the same words, which
    // sent anyone reading it looking for a gateway that was not answering when
    // the gateway had answered.
    [Fact]
    public async Task AChallengeWithoutANonceIsNotAConnection()
    {
        var socket = new FakeGatewaySocket();

        // An unrelated event first, which the challenge wait has to ignore
        // rather than mistake for its own.
        socket.PushEvent("tick", new { });
        socket.PushEvent("connect.challenge", new { });

        using var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
        Assert.Equal("the gateway sent connect.challenge with no nonce", result.Detail);
        Assert.Empty(socket.Requests);
    }

    // A gateway that connects and then says nothing. Unreachable rather than
    // rejected, so the supervisor keeps trying — a machine that is asleep is the
    // most likely reason a challenge never arrives.
    //
    // The deadline is named in the detail rather than left implicit. This is
    // one of the three tests that is genuinely about a timeout, so it keeps an
    // explicit one, and 50ms is short enough that no amount of scheduling delay
    // makes it mean something else.
    [Fact]
    public async Task AGatewayThatNeverChallengesIsUnreachable()
    {
        var socket = new FakeGatewaySocket();
        using var gateway = Gateway(socket, challengeTimeout: TimeSpan.FromMilliseconds(50));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
        Assert.Equal("no connect.challenge within 0.05s", result.Detail);
    }

    // The transport failing, and the reason the exception chain is flattened:
    // the outermost message is almost always "Unable to connect to the remote
    // server", which says nothing a person can act on.
    [Fact]
    public async Task AFailedTransportIsUnreachableWithTheWholeExceptionChain()
    {
        var gateway = new OpenClawGateway("gw.local", 4443, "tok",
            (_, _, _, _) => throw new IOException(
                "Unable to connect to the remote server",
                new SocketishException("connection refused")));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
        Assert.Contains("Unable to connect", result.Detail);
        Assert.Contains("connection refused", result.Detail);
    }

    private sealed class SocketishException : Exception
    {
        public SocketishException(string message) : base(message) { }
    }

    // A pin mismatch, which BouncyCastle reports as a fatal bad_certificate
    // alert — indistinguishable by type from any other handshake failure, so the
    // text is what separates them. Classified separately because the settings
    // window has to *offer something* for this one; anything else is a dead end
    // with no way through but editing settings.json.
    [Fact]
    public async Task ABadCertificateAlertIsClassifiedAsAMismatch()
    {
        var gateway = new OpenClawGateway("gw.local", 4443, "tok",
            (_, _, _, _) => throw new IOException("fatal alert: bad_certificate"));

        var result = await gateway.ConnectAsync("some-old-pin", CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.CertificateMismatch, result.Outcome);
    }

    // "certificate" alone only counts as a mismatch when a pin was in play. With
    // no pin there is nothing for a mismatch to be against, and calling it one
    // would offer the user a button that clears a pin that does not exist.
    [Fact]
    public async Task ACertificateComplaintWithNoPinIsMerelyUnreachable()
    {
        var gateway = new OpenClawGateway("gw.local", 4443, "tok",
            (_, _, _, _) => throw new IOException("certificate has expired"));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
    }

    // Only the gateway saying no is a reason to stop asking. Terminal outcomes
    // stop the supervisor for good, so the mapping from detail code to outcome
    // decides whether a transient problem becomes a permanent outage — which is
    // why an unknown code is retryable rather than fatal.
    //
    // The expected outcome is named rather than passed as the enum: Outcome is
    // internal and a public [Theory] method cannot take an internal parameter
    // type, so the name is what crosses the signature.
    [Theory]
    [InlineData("PAIRING_REQUIRED", "PairingPending")]
    [InlineData("DEVICE_IDENTITY_REQUIRED", "PairingPending")]
    [InlineData("AUTH_UNAUTHORIZED", "AuthRejected")]
    [InlineData("AUTH_TOKEN_MISSING", "AuthRejected")]
    [InlineData("AUTH_TOKEN_MISMATCH", "AuthRejected")]
    [InlineData("AUTH_TOKEN_NOT_CONFIGURED", "AuthRejected")]
    [InlineData("AUTH_PASSWORD_MISMATCH", "AuthRejected")]
    [InlineData("AUTH_DEVICE_TOKEN_MISMATCH", "AuthRejected")]
    [InlineData("AUTH_SCOPE_MISMATCH", "AuthRejected")]
    [InlineData("DEVICE_AUTH_INVALID", "AuthRejected")]
    [InlineData("DEVICE_AUTH_SIGNATURE_INVALID", "AuthRejected")]
    [InlineData("DEVICE_AUTH_PUBLIC_KEY_INVALID", "AuthRejected")]
    [InlineData("DEVICE_AUTH_DEVICE_ID_MISMATCH", "AuthRejected")]
    [InlineData("PROTOCOL_MISMATCH", "AuthRejected")]
    [InlineData("RATE_LIMITED", "Unreachable")]
    [InlineData("SOMETHING_A_FUTURE_GATEWAY_INVENTS", "Unreachable")]
    public void ADetailCodeDecidesWhetherToKeepTrying(string code, string expected)
    {
        var result = OpenClawGateway.Classify(
            new OpenClawRequestException("refused", code));

        Assert.Equal(expected, result.Outcome.ToString());

        // The code is carried into the detail, because the prose alone is not
        // enough to tell "approve this device" from "your signature is wrong".
        Assert.Equal($"{code}: refused", result.Detail);
    }

    // No structured code at all — an older gateway, or a failure it has no
    // vocabulary for. The message stands on its own rather than being prefixed
    // with a colon and nothing.
    [Fact]
    public void AnErrorWithNoDetailCodeReportsItsMessageAlone()
    {
        var result = OpenClawGateway.Classify(new OpenClawRequestException("request failed", null));

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
        Assert.Equal("request failed", result.Detail);
    }

    // A structured refusal reaching ConnectAsync goes through Classify, so a
    // pairing that has not been approved is reported as pending rather than as a
    // credential problem.
    [Fact]
    public async Task ARefusedConnectIsClassifiedRatherThanReportedRaw()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "n" });
        socket.OnRequest = r => FakeGatewaySocket.Error(
            r.Id, "unauthorized", "device is not approved", "PAIRING_REQUIRED");

        using var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.PairingPending, result.Outcome);
        Assert.Contains("PAIRING_REQUIRED", result.Detail);
    }

    // The socket dropping mid-handshake used to be reported as AuthRejected,
    // which is terminal — so a gateway restarted between the upgrade and its
    // answer said "refused these credentials" and never tried again for the life
    // of the app. Everything that is not the gateway saying no is transport.
    //
    // The detail is asserted as well as the outcome, because this is the path
    // CB-23 was actually on and the words were the whole problem: the gateway
    // answered the challenge, took the connect and then said nothing, and the
    // app reported "A task was canceled." — which names no method, no deadline
    // and no gateway.
    [Fact]
    public async Task ASocketThatDiesMidHandshakeIsUnreachableAndNotRejected()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "n" });

        // No answer at all, and the request times out.
        using var gateway = Gateway(socket, requestTimeout: TimeSpan.FromMilliseconds(50));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
        Assert.Equal(
            "the gateway accepted the socket but did not answer connect within 0.05s",
            result.Detail);
    }

    // The caller giving up, which arrives as the same OperationCanceledException
    // as the timeout above and means the opposite thing. Nobody needs telling
    // that a connection they cancelled did not finish; they very much need
    // telling that one they did not cancel ran out of patience.
    // Seeing the connect request land in socket.Requests proves the send was
    // issued; it does not hold anything there, because the fake's SendAsync
    // returns synchronously by default. Left that way, there is a real
    // window — between the request being recorded and RequestAsync
    // registering its own per-request cancellation handling — in which the
    // receive loop's own reaction to the same cancelled token can race
    // RequestAsync to the pending entry and fail it with "gateway connection
    // closed" instead. See ParkSendUntilCancelled's comment in
    // FakeGatewaySocket for how CB-175 reproduced that reliably.
    //
    // ParkSendUntilCancelled closes the window rather than narrowing it: the
    // send itself is what stays outstanding, so ConnectAsync's cancellation
    // is observed through the one await RequestAsync is actually sitting on
    // — no deadline of its own, and nothing for the receive loop to race,
    // because RequestAsync never reaches the point where it would register
    // anything for the loop to beat.
    [Fact]
    public async Task ACancelledHandshakeSaysItWasAbandonedRatherThanTimedOut()
    {
        var socket = new FakeGatewaySocket { ParkSendUntilCancelled = true };
        socket.PushEvent("connect.challenge", new { nonce = "n" });

        using var gateway = Gateway(socket);
        using var caller = new CancellationTokenSource();

        var connecting = gateway.ConnectAsync(null, caller.Token);

        while (socket.Requests.Count == 0) await Task.Delay(5);
        caller.Cancel();

        var result = await connecting;

        Assert.Equal(OpenClawGateway.Outcome.Unreachable, result.Outcome);
        Assert.Equal(
            "the connection attempt was abandoned before the gateway answered connect",
            result.Detail);
    }

    // The three arms of the handshake's own failure vocabulary, exercised
    // directly. Two of them are reachable through ConnectAsync above; the third
    // is the one that only shows up when something that is not a cancellation
    // comes back, and it has to keep flattening the chain rather than inventing
    // a sentence of its own.
    [Fact]
    public void AHandshakeFailureThatIsNotACancellationKeepsItsExceptionChain()
    {
        var ex = new IOException("outer", new IOException("the socket went away"));

        Assert.Equal(
            "outer — the socket went away",
            OpenClawGateway.DescribeHandshakeFailure(ex, abandoned: false, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void ALapsedHandshakeNamesTheMethodAndTheDeadline()
    {
        Assert.Equal(
            "the gateway accepted the socket but did not answer connect within 20s",
            OpenClawGateway.DescribeHandshakeFailure(
                new OperationCanceledException(), abandoned: false, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void AnAbandonedHandshakeSaysSoRatherThanBlamingTheGateway()
    {
        Assert.Equal(
            "the connection attempt was abandoned before the gateway answered connect",
            OpenClawGateway.DescribeHandshakeFailure(
                new TaskCanceledException(), abandoned: true, TimeSpan.FromSeconds(20)));
    }

    // Silence and a malformed challenge are different faults and now read as
    // different faults. They shared one sentence until CB-23, and the shared
    // sentence described the one the gateway was not guilty of.
    [Fact]
    public void AChallengeThatNeverCameAndOneWithNoNonceAreDescribedApart()
    {
        Assert.Equal(
            "no connect.challenge within 10s",
            OpenClawGateway.DescribeMissingChallenge(arrived: false, TimeSpan.FromSeconds(10)));

        Assert.Equal(
            "the gateway sent connect.challenge with no nonce",
            OpenClawGateway.DescribeMissingChallenge(arrived: true, TimeSpan.FromSeconds(10)));
    }

    // Timeout.InfiniteTimeSpan is minus one millisecond, and a deadline that was
    // deliberately removed must not print as a negative one. Reachable only by
    // calling this directly — a wait that never expires cannot report on itself
    // — which is why it is asserted here rather than left to the two callers.
    [Fact]
    public void AWaitWithNoDeadlineIsDescribedAsHavingNone()
    {
        Assert.Equal("no limit", OpenClawGateway.DescribeWait(Timeout.InfiniteTimeSpan));
        Assert.Equal("0.05s", OpenClawGateway.DescribeWait(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("20s", OpenClawGateway.DescribeWait(TimeSpan.FromSeconds(20)));
    }

    // Flatten's own contract: the chain, in order, without repeating a message
    // that appears twice.
    [Fact]
    public void FlattenJoinsTheChainAndDropsRepeats()
    {
        var ex = new Exception("outer", new Exception("middle", new Exception("outer")));

        Assert.Equal("outer — middle", OpenClawGateway.Flatten(ex));
    }

    // ---- requests, once connected -------------------------------------------

    // Connected under the default timeout, and only then given the one asked
    // for. The handshake is a request too, so building the gateway with a 50ms
    // timeout gave the handshake 50ms as well — and on a loaded CI runner that
    // failed ARequestThatIsNeverAnsweredIsCancelled at the connect, with
    // "did not answer connect within 0.05s", before its own request existed.
    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        TimeSpan? requestTimeout = null)
    {
        var socket = Accepting();
        var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);
        if (requestTimeout is { } timeout) gateway.RequestTimeout = timeout;

        return (socket, gateway);
    }

    [Fact]
    public async Task ARequestCarriesItsMethodAndParametersAndGetsThePayloadBack()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            socket.OnRequest = r => FakeGatewaySocket.Ok(r.Id, new { sessions = new[] { "one" } });

            var response = await gateway.RequestAsync(
                "sessions.list", new Dictionary<string, object> { ["limit"] = 40 },
                CancellationToken.None);

            var sent = socket.Requests.Last();
            Assert.Equal("sessions.list", sent.Method);
            Assert.Equal(40, sent.Params.GetProperty("limit").GetInt32());

            Assert.Equal("one", response.GetProperty("sessions")[0].GetString());
        }
    }

    // Ids are unique per request, which is the whole basis of correlation over
    // one socket: two requests sharing an id means one of them gets the other's
    // answer.
    [Fact]
    public async Task EveryRequestGetsItsOwnId()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            socket.OnRequest = r => FakeGatewaySocket.Ok(r.Id, new { });

            for (var i = 0; i < 5; i++)
            {
                await gateway.RequestAsync("ping", null, CancellationToken.None);
            }

            var ids = socket.Requests.Select(r => r.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    // Two requests in flight at once, answered out of order. The pending table
    // is the only thing that keeps them apart, and getting this wrong hands one
    // caller the other's answer — which is worse than an error, because it looks
    // like success.
    [Fact]
    public async Task AnswersAreMatchedToTheirRequestEvenOutOfOrder()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var held = new List<FakeGatewaySocket.Request>();
            socket.OnRequest = r => { held.Add(r); return null; };

            var first = gateway.RequestAsync("first", null, CancellationToken.None);
            var second = gateway.RequestAsync("second", null, CancellationToken.None);

            while (held.Count < 2) await Task.Delay(5);

            // Answered in reverse.
            socket.PushJson(JsonSerializer.Serialize(
                FakeGatewaySocket.Ok(held[1].Id, new { which = "second" })));
            socket.PushJson(JsonSerializer.Serialize(
                FakeGatewaySocket.Ok(held[0].Id, new { which = "first" })));

            Assert.Equal("first", (await first).GetProperty("which").GetString());
            Assert.Equal("second", (await second).GetProperty("which").GetString());
        }
    }

    // A gateway that stops answering. The caller gets a cancellation rather than
    // hanging forever — which is what the supervisor treats as a dead socket and
    // reconnects over.
    [Fact]
    public async Task ARequestThatIsNeverAnsweredIsCancelled()
    {
        var (socket, gateway) = await ConnectedAsync(TimeSpan.FromMilliseconds(50));
        using (gateway)
        {
            socket.OnRequest = _ => null;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => gateway.RequestAsync("sessions.list", null, CancellationToken.None));
        }
    }

    // A request that never left leaves nothing behind. Without this its slot sat
    // in the pending table until the socket died, which meant the table was not
    // the list of things actually in flight — the only thing it is for.
    [Fact]
    public async Task ARequestThatCouldNotBeSentDoesNotStayPending()
    {
        using var gateway = Gateway(new FailingSendSocket());

        // Connect fails the same way, for the same reason, so the send failure
        // is provoked directly.
        await Assert.ThrowsAnyAsync<Exception>(
            () => gateway.RequestAsync("sessions.list", null, CancellationToken.None));

        // The proof that nothing is left over: the socket dies next, and a
        // pending slot would be failed with the close reason instead of the send
        // error above having been the whole story.
        //
        // Asserted by re-requesting rather than by reading a private field: a
        // second attempt behaves identically, which it would not if the first
        // had left an id behind that the response matcher would now find first.
        await Assert.ThrowsAnyAsync<Exception>(
            () => gateway.RequestAsync("sessions.list", null, CancellationToken.None));
    }

    // ---- the receive loop ---------------------------------------------------

    // Events are raised by name with their payload, which is how the session
    // list learns that a session is mid-run — the list itself never says so.
    [Fact]
    public async Task EventsAreRaisedByNameWithTheirPayload()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        var seen = new List<(string Name, string? Key)>();
        gateway.EventReceived += (name, payload) => seen.Add((
            name,
            payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("sessionKey", out var k) ? k.GetString() : null));

        await ConnectOrExplainAsync(gateway);

        socket.PushEvent("agent", new { sessionKey = "agent:nova:discord" });
        await WaitFor(() => seen.Any(s => s.Name == "agent"));

        Assert.Contains(("agent", "agent:nova:discord"), seen);
    }

    // An event with no payload property at all. The gateway sends these — `tick`
    // carries nothing — and reaching for a payload that isn't there must not
    // take the loop down.
    [Fact]
    public async Task AnEventWithNoPayloadIsStillRaised()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        var names = new List<string>();
        gateway.EventReceived += (name, _) => names.Add(name);

        await ConnectOrExplainAsync(gateway);

        socket.PushJson("{\"event\":\"tick\"}");
        await WaitFor(() => names.Contains("tick"));
    }

    // One frame the parser didn't expect used to take the whole loop down and
    // cost a full TLS 1.3 reconnect. A frame we can't read is a frame we skip —
    // proved by sending rubbish and then something real.
    [Fact]
    public async Task ABadFrameIsSkippedRatherThanKillingTheConnection()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        socket.PushJson("this is not json at all");
        socket.PushJson("[\"an array, not an object\"]");
        socket.PushJson("{\"no\":\"id and no event\"}");

        // A subscriber that throws is the other half of the same problem.
        gateway.EventReceived += (_, _) => throw new InvalidOperationException("boom");

        socket.PushEvent("health", new { });

        socket.OnRequest = r => FakeGatewaySocket.Ok(r.Id, new { alive = true });

        var response = await gateway.RequestAsync("ping", null, CancellationToken.None);

        Assert.True(response.GetProperty("alive").GetBoolean());
    }

    // A response whose id came back as a number rather than a string. Both
    // shapes are tolerated because the id is ours and the gateway is free to
    // echo it however its serializer feels — dropping it would hang the caller.
    [Fact]
    public async Task AnIdEchoedAsANumberStillMatches()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        socket.OnRequest = r => new
        {
            type = "res",
            id = int.Parse(r.Id),
            ok = true,
            payload = new { matched = true }
        };

        var response = await gateway.RequestAsync("ping", null, CancellationToken.None);

        Assert.True(response.GetProperty("matched").GetBoolean());
    }

    // A response with no payload hands back the whole frame rather than nothing.
    // Some methods answer with their fields at the top level.
    [Fact]
    public async Task AResponseWithNoPayloadHandsBackTheWholeFrame()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        socket.OnRequest = r => new { type = "res", id = r.Id, ok = true, count = 7 };

        var response = await gateway.RequestAsync("sessions.count", null, CancellationToken.None);

        Assert.Equal(7, response.GetProperty("count").GetInt32());
    }

    // An error frame becomes an exception carrying the structured detail code,
    // which is the only way to tell "approve this device" from "your signature
    // is wrong" without matching on prose.
    [Fact]
    public async Task AnErrorFrameBecomesAnExceptionCarryingTheDetailCode()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        socket.OnRequest = r => FakeGatewaySocket.Error(
            r.Id, "forbidden", "missing scope: operator.write", "AUTH_SCOPE_MISMATCH");

        var ex = await Assert.ThrowsAsync<OpenClawRequestException>(
            () => gateway.RequestAsync("chat.send", null, CancellationToken.None));

        Assert.Equal("missing scope: operator.write", ex.Message);
        Assert.Equal("AUTH_SCOPE_MISMATCH", ex.DetailCode);
    }

    // An error frame with nothing in it at all. Still an exception, and still
    // says something rather than surfacing as a null reference.
    [Fact]
    public async Task AnErrorFrameWithNoDetailStillFails()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        socket.OnRequest = r => new { type = "res", id = r.Id, ok = false };

        var ex = await Assert.ThrowsAsync<OpenClawRequestException>(
            () => gateway.RequestAsync("chat.send", null, CancellationToken.None));

        Assert.Equal("request failed", ex.Message);
        Assert.Null(ex.DetailCode);
    }

    // A frame that arrives in pieces. The gateway's own limit is 26MB, so this
    // is the ordinary case for anything large, and treating a partial frame as a
    // whole one produces a parse error per chunk.
    [Fact]
    public async Task AFrameSplitAcrossReadsIsReassembled()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        var pending = new List<FakeGatewaySocket.Request>();
        socket.OnRequest = r => { pending.Add(r); return null; };

        var request = gateway.RequestAsync("sessions.list", null, CancellationToken.None);
        while (pending.Count == 0) await Task.Delay(5);

        var frame = JsonSerializer.Serialize(
            FakeGatewaySocket.Ok(pending[0].Id, new { half = "yes" }));
        var bytes = Encoding.UTF8.GetBytes(frame);
        var split = bytes.Length / 2;

        socket.PushBytes(bytes[..split], endOfMessage: false);
        socket.PushBytes(bytes[split..], endOfMessage: true);

        Assert.Equal("yes", (await request).GetProperty("half").GetString());
    }

    // The gateway hanging up. The close frame's description is the only place it
    // explains a rejected connect — it answers, then closes with 1008 and the
    // reason as text — so throwing that away and reporting "connection closed"
    // would discard the entire diagnosis.
    [Fact]
    public async Task ACloseFramesReasonReachesTheWaitingCaller()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        var pending = new List<FakeGatewaySocket.Request>();
        socket.OnRequest = r => { pending.Add(r); return null; };

        var request = gateway.RequestAsync("sessions.list", null, CancellationToken.None);
        while (pending.Count == 0) await Task.Delay(5);

        socket.PushClose(WebSocketCloseStatus.PolicyViolation, "scope operator.write not granted");

        var ex = await Assert.ThrowsAsync<IOException>(() => request);

        Assert.Contains("scope operator.write not granted", ex.Message);
    }

    // A close with no description falls back to the status, and a caller waiting
    // on the dead socket is failed immediately rather than being left to time
    // out on its own — they all failed for the same reason at the same moment.
    [Fact]
    public async Task ACloseWithNoReasonStillFailsEverythingInFlight()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        var pending = new List<FakeGatewaySocket.Request>();
        socket.OnRequest = r => { pending.Add(r); return null; };

        var first = gateway.RequestAsync("one", null, CancellationToken.None);
        var second = gateway.RequestAsync("two", null, CancellationToken.None);
        while (pending.Count < 2) await Task.Delay(5);

        socket.PushClose(WebSocketCloseStatus.NormalClosure, null);

        // Both, and because the close swept them rather than because a deadline
        // expired: these requests have no deadline at all, so a test that only
        // passed by waiting one out would never finish.
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => second);
    }

    // A frame past the server's own advertised limit is a frame it never sent,
    // so the loop stops rather than buffering it. Bounded by the gateway's
    // number rather than one of ours, which is what the handshake reads it for.
    [Fact]
    public async Task AFrameLargerThanTheAdvertisedLimitEndsTheLoop()
    {
        var socket = Accepting(policy: new { maxPayload = 64 });
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);
        Assert.Equal(64, gateway.MaxPayload);

        var pending = new List<FakeGatewaySocket.Request>();
        socket.OnRequest = r => { pending.Add(r); return null; };

        var request = gateway.RequestAsync("sessions.list", null, CancellationToken.None);
        while (pending.Count == 0) await Task.Delay(5);

        socket.PushBytes(Encoding.UTF8.GetBytes(new string('x', 128)), endOfMessage: false);

        // Treated as the connection being over, which fails the waiting request
        // rather than leaving it to its own timeout.
        await Assert.ThrowsAsync<IOException>(() => request);
    }

    // The socket breaking rather than closing. A dead socket must not leave
    // callers waiting out their own timeouts one by one — they all failed for
    // the same reason at the same moment, and the reason is worth carrying.
    [Fact]
    public async Task ASocketThatBreaksMidStreamFailsWhatWasInFlightWithTheReason()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        await ConnectOrExplainAsync(gateway);

        var pending = new List<FakeGatewaySocket.Request>();
        socket.OnRequest = r => { pending.Add(r); return null; };

        var request = gateway.RequestAsync("sessions.list", null, CancellationToken.None);
        while (pending.Count == 0) await Task.Delay(5);

        socket.BreakOnReceive = new WebSocketException("the network went away");
        socket.PushJson("{}");   // wakes the loop, which then throws

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => request);
        Assert.Contains("network went away", ex.Message);
    }

    // Disposing twice, and disposing something that never connected, are both
    // ordinary — the supervisor's finally block runs on every path through the
    // loop including the ones that failed before a socket existed.
    [Fact]
    public void DisposingIsSafeWhateverStateTheConnectionIsIn()
    {
        var gateway = new OpenClawGateway("gw.local", 4443, "tok",
            (_, _, _, _) => throw new IOException("never connected"));

        gateway.Dispose();
        gateway.Dispose();
    }

    // The shape the app actually constructs, which is the same one with the real
    // connector behind it. Worth one line: constructing it must not touch a
    // socket — the supervisor builds one per attempt and would otherwise be
    // opening a connection before it had decided to.
    [Fact]
    public void TheProductionConstructorOpensNothingByItself()
    {
        using var gateway = new OpenClawGateway("gw.local", 4443, "gateway-token");

        // The advertised defaults, until a handshake replaces them.
        Assert.Equal(26_214_400, gateway.MaxPayload);
        Assert.Equal(30_000, gateway.TickIntervalMs);
        Assert.Empty(gateway.GrantedScopes);
        Assert.Null(gateway.ObservedFingerprint);
    }

    // The same problem as the handshake deadline, one layer up: this used to
    // give up after two hundred five-millisecond turns — one second — and a
    // second is well inside what a starved scheduler costs, so a frame that
    // arrived late failed as a frame that never arrived. The budget is now
    // large enough that expiring it means something is genuinely wrong, and the
    // message says how long it waited so nobody has to count the loop.
    private static async Task WaitFor(Func<bool> condition)
    {
        var waited = TimeSpan.Zero;
        var turn = TimeSpan.FromMilliseconds(5);
        var budget = TimeSpan.FromSeconds(60);

        while (!condition() && waited < budget)
        {
            await Task.Delay(turn);
            waited += turn;
        }

        Assert.True(
            condition(),
            $"the condition never became true within {budget.TotalSeconds:0}s");
    }

    // ---- CB-68: the receive loop's own thread ---------------------------------

    // The ordinary case, with the pool otherwise idle: the receive loop still
    // runs on a thread the CLR grew specifically for it, not a borrowed pool
    // worker. Cheap to assert and worth asserting anyway — a future edit that
    // quietly changes this back to Task.Run would compile, pass every other
    // test here, and reintroduce exactly the dependency CB-68 removed.
    [Fact]
    public async Task TheReceiveLoopRunsOnADedicatedThreadNotAPooledOne()
    {
        var socket = Accepting();
        using var gateway = Gateway(socket);

        var result = await ConnectOrExplainAsync(gateway);

        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);
        Assert.False(gateway.ReceiveLoopStartedOnAPooledThread);
    }
}
