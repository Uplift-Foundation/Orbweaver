using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6, the panel side: what an OpenClaw chat session offers when a
// pending approval is held for it, and every way an answer ends. Over a real
// OpenClawGateway on an in-memory socket, with the gateway's record shapes as
// the gateway side's own fixtures declare them (declared, not measured; see
// OpenClawApprovalsTests).
//
// The property under test throughout is the one the stage exists for: nothing
// answers an approval except a click on an option this session offered for the
// request it is showing, and a stale or repeated click answers nothing.
[Collection("Settings")]
public class OpenClawChatApprovalTests : IDisposable
{
    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly string _key = "agent:alpha:dashboard:" + Guid.NewGuid();

    public OpenClawChatApprovalTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        Reset();
    }

    public void Dispose()
    {
        Reset();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OpenClawSessions.SetGatewayForTests(null);
    }

    private static void Reset()
    {
        OrbweaverSettings.OpenClawEnabled = false;
        OpenClawSessions.Restart();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private string Row(string id, string command = "ls -la /tmp", int minutes = 30, int createdOffset = 0) =>
        "{\"id\":\"" + id + "\",\"approvalKind\":\"exec\",\"request\":{\"command\":" + JsonSerializer.Serialize(command)
        + ",\"cwd\":\"/tmp\",\"host\":\"gateway\",\"agentId\":\"alpha\",\"sessionKey\":\"" + _key + "\"},"
        + "\"createdAtMs\":" + Ms(Now.AddSeconds(createdOffset)) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(minutes)) + "}";

    private void Request(string id, string command = "ls -la /tmp", int createdOffset = 0) =>
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id, command, createdOffset: createdOffset)), Now);

    private static void Resolve(string id, string decision = "deny", string? reason = null) =>
        OpenClawSessions.OnEvent("exec.approval.resolved", Json(
            "{\"id\":\"" + id + "\",\"decision\":\"" + decision + "\""
            + (reason is null ? "" : ",\"reason\":\"" + reason + "\"") + "}"), Now);

    private OpenClawChatSession Chat() => new("openclaw:" + _key, _key, "Nova");

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object?>? answer = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.approvals" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : answer?.Invoke(request) ?? FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private static List<FakeGatewaySocket.Request> Resolves(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method == "approval.resolve").ToList();

    private static object Applied(FakeGatewaySocket.Request r, string status = "denied") =>
        FakeGatewaySocket.Ok(r.Id, new { applied = true, approval = new { status, reason = "user" } });

    private static ChatPromptOption Option(OpenClawChatSession chat, string key) =>
        chat.Prompt!.Options.Single(o => o.Key == key);

    private static string[] Texts(OpenClawChatSession chat) => chat.History.Select(t => t.Text).ToArray();

    // --- the prompt ---

    [Fact]
    public void NothingPendingIsNoPrompt() => Assert.Null(Chat().Prompt);

    [Fact]
    public void ThePromptIsTheFirstToArriveWithDenyThenAllowOnceBoundToIt()
    {
        Request("a1", "first", createdOffset: 0);
        Request("a2", "second", createdOffset: -50);   // arrived later, claims to be older: still second

        var prompt = Chat().Prompt!;

        Assert.StartsWith("Nova wants to run: first", prompt.Title);
        Assert.Equal(new[] { "deny", "allow-once" }, prompt.Options.Select(o => o.Key));
        Assert.All(prompt.Options, o => Assert.Equal("a1", o.ApprovalId));
        Assert.True(prompt.ArmedActivation);
    }

    // The gateway says which answers it accepts (declared in its schemas, not
    // measured): a record without allow-once in the list is Deny only, and the
    // click for allow-once is refused with nothing sent.
    [Fact]
    public void ARecordWithoutAllowOnceInItsAllowedDecisionsShowsDenyOnly()
    {
        OpenClawSessions.OnEvent("exec.approval.requested", Json(
            Row("a1").Replace("\"request\":{", "\"request\":{\"allowedDecisions\":[\"deny\"],")), Now);

        var prompt = Chat().Prompt!;

        Assert.Equal("deny", Assert.Single(prompt.Options).Key);
    }

    // A record the parser judged unsafe has its command blanked and is Deny only:
    // the prompt says so and shows nothing of the request but its id.
    [Fact]
    public void AnUnsafeRecordShowsOnlyDenyAndNoneOfItsCommand()
    {
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("a1", "echo \u0007EVIL")), Now);

        var prompt = Chat().Prompt!;

        Assert.StartsWith(OpenClawApprovalText.UnsafeRequest, prompt.Title);
        Assert.DoesNotContain("EVIL", prompt.Title);
        Assert.Equal("deny", Assert.Single(prompt.Options).Key);
        Assert.Equal("a1", prompt.Options[0].ApprovalId);
    }

    // A command cut by the parser says how much was left, from the length the
    // gateway sent.
    [Fact]
    public void ACutCommandSaysHowManyCharactersTheGatewaySentBeyondWhatIsShown()
    {
        Request("a1", new string('x', 4500));

        var prompt = Chat().Prompt!;

        Assert.Contains("(500 more characters)", prompt.Title);
        Assert.Equal("deny", Assert.Single(prompt.Options).Key);
    }

    // The queue: losing one shows the next.
    [Fact]
    public void ResolvingTheFirstShowsTheNext()
    {
        var chat = Chat();
        Request("a1", "first");
        Request("a2", "second", createdOffset: 5);

        Resolve("a1");

        Assert.StartsWith("Nova wants to run: second", chat.Prompt!.Title);
        Assert.All(chat.Prompt!.Options, o => Assert.Equal("a2", o.ApprovalId));
    }

    [Fact]
    public void ACommandTooLongToAllowOffersOnlyDenyAndSaysWhy()
    {
        Request("a1", new string('x', 4001));

        var prompt = Chat().Prompt!;

        Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));
        Assert.Contains(OpenClawApprovalText.TooLongToAllow, prompt.Title);
    }

    // --- changes reach the panel ---

    [Fact]
    public void ARequestOrAResolveRaisesPromptChangedOnlyWhenTheBarWouldDiffer()
    {
        var chat = Chat();
        var raised = 0;
        chat.PromptChanged += () => raised++;

        Request("a1");
        chat.OnApprovalsChanged();
        Assert.Equal(1, raised);

        chat.OnApprovalsChanged(); // nothing changed
        Assert.Equal(1, raised);

        Resolve("a1");
        chat.OnApprovalsChanged();
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ApprovalsChangedStillFiresForWhoeverListens()
    {
        var chat = Chat();
        var told = 0;
        chat.ApprovalsChanged += () => told++;

        chat.OnApprovalsChanged();

        Assert.Equal(1, told);
    }

    // --- an answer ---

    [Fact]
    public async Task AClickSendsExactlyOneResolveFrameForTheRequestAndNotesTheAnswer()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve" ? Applied(r) : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(Option(chat, "deny"));

            var frame = Assert.Single(Resolves(socket));
            Assert.Equal(new[] { "id", "kind", "decision" }, frame.Params.EnumerateObject().Select(p => p.Name));
            Assert.Equal("a1", frame.Params.GetProperty("id").GetString());
            Assert.Equal("exec", frame.Params.GetProperty("kind").GetString());
            Assert.Equal("deny", frame.Params.GetProperty("decision").GetString());
            Assert.Contains("Denied", Texts(chat));
            Assert.Null(chat.Prompt);
        }
    }

    [Fact]
    public async Task AllowOnceIsSentAsAllowOnceAndReadsAsAllowedOnce()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve" ? Applied(r, "allowed") : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(Option(chat, "allow-once"));

            Assert.Equal("allow-once", Assert.Single(Resolves(socket)).Params.GetProperty("decision").GetString());
            Assert.Contains("Allowed once", Texts(chat));
        }
    }

    // Render A; A is resolved elsewhere and B arrives; the click lands on A's old
    // option: nothing is sent, the person is told to look again, and B's own
    // option then sends exactly one frame, for B.
    [Fact]
    public async Task AClickOnAnOptionForARequestThatHasGoneSendsNothingAndTheNewOnesOptionSendsOne()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve" ? Applied(r) : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1", "echo A");
            var oldOption = Option(chat, "allow-once");

            Resolve("a1");
            Request("b1", "echo B", createdOffset: 5);

            await chat.AnswerAsync(oldOption);

            Assert.Empty(Resolves(socket));
            Assert.Contains(OpenClawApprovalText.RequestChanged, Texts(chat));

            await chat.AnswerAsync(Option(chat, "allow-once"));

            var frame = Assert.Single(Resolves(socket));
            Assert.Equal("b1", frame.Params.GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task AClickWithNothingPendingSendsNothingAndSaysTheRequestChanged()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat();

            await chat.AnswerAsync(new ChatPromptOption("allow-once", "Allow once", "gone"));

            Assert.Empty(Resolves(socket));
            Assert.Contains(OpenClawApprovalText.RequestChanged, Texts(chat));
        }
    }

    // Two clicks racing: the first marks the request answered, the second has
    // nothing to land on and says nothing.
    [Fact]
    public async Task ASecondClickWhileTheFirstIsInFlightSendsNothing()
    {
        var gate = new TaskCompletionSource();
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve" ? Applied(r) : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");
            var option = Option(chat, "deny");

            await Task.WhenAll(chat.AnswerAsync(option), chat.AnswerAsync(option));

            Assert.Single(Resolves(socket));
            Assert.DoesNotContain(OpenClawApprovalText.RequestChanged, Texts(chat));
            gate.TrySetResult();
        }
    }

    // The two answers are the client's: a key the payload could have named is not
    // sent, nor allow-once on a request that can only be denied.
    [Theory]
    [InlineData("allow-always")]
    [InlineData("approve")]
    [InlineData("")]
    public async Task AnOptionOutsideTheOfferedPairSendsNothing(string key)
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(new ChatPromptOption(key, "x", "a1"));

            Assert.Empty(Resolves(socket));
        }
    }

    [Fact]
    public async Task AllowOnceOnARequestThatCanOnlyBeDeniedSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat();
            Request("a1", new string('x', 4001));

            await chat.AnswerAsync(new ChatPromptOption("allow-once", "Allow once", "a1"));

            Assert.Empty(Resolves(socket));
        }
    }

    [Fact]
    public async Task WithApprovingOffTheClickSaysSoAndSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat();
            Request("a1");
            var option = Option(chat, "deny");
            OrbweaverSettings.OpenClawApproveEnabled = false;

            await chat.AnswerAsync(option);

            Assert.Empty(Resolves(socket));
            Assert.Contains(OpenClawApprovalText.ApprovingOff, Texts(chat));
        }
    }

    // --- the other ends of an answer ---

    [Fact]
    public async Task AnotherReviewerGettingThereFirstSaysWhatTheyDecided()
    {
        var (_, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve"
            ? FakeGatewaySocket.Ok(r.Id, new { applied = false, approval = new { status = "allowed", reason = "user" } })
            : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(Option(chat, "deny"));

            Assert.Contains("Already decided elsewhere: Allowed by another reviewer", Texts(chat));
        }
    }

    [Fact]
    public async Task ARequestTheGatewayNoLongerKnowsIsNoLongerPending()
    {
        var (_, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve"
            ? FakeGatewaySocket.Error(r.Id, "INVALID_REQUEST", "approval expired or not found", null)
            : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(Option(chat, "deny"));

            Assert.Contains(OpenClawApprovalText.NoLongerPending, Texts(chat));
            Assert.Null(chat.Prompt);
        }
    }

    // A failure keeps the request and the bar, and the same click can be tried again.
    [Fact]
    public async Task AFailureKeepsTheRequestAndRestoresTheBarForAnotherTry()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve"
            ? FakeGatewaySocket.Error(r.Id, "UNAVAILABLE", "the gateway is busy", null)
            : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");
            var raised = 0;
            chat.PromptChanged += () => raised++;

            await chat.AnswerAsync(Option(chat, "deny"));

            Assert.Contains(Texts(chat), t => t.StartsWith("Couldn't answer: ") && t.Contains("busy"));
            Assert.NotNull(chat.Prompt);
            Assert.True(raised >= 2); // hidden at the click, back after the failure

            await chat.AnswerAsync(Option(chat, "deny"));
            Assert.Equal(2, Resolves(socket).Count);
        }
    }

    [Fact]
    public async Task NotConnectedIsSaidAndKeepsTheBar()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var chat = Chat();
        Request("a1");

        await chat.AnswerAsync(Option(chat, "deny"));

        Assert.Contains("Couldn't answer: not connected to the gateway", Texts(chat));
        Assert.NotNull(chat.Prompt);
    }

    // --- what ended without us ---

    [Theory]
    [InlineData("deny", null, "Denied by another reviewer")]
    [InlineData("allow-once", null, "Allowed by another reviewer")]
    [InlineData("allow-always", null, "Allowed always, by another reviewer")]
    [InlineData("deny", "run-aborted", "Denied by another reviewer")]   // status first, reason only refines
    [InlineData("mystery", null, "This request is no longer pending")]
    public void AnApprovalThatEndedElsewhereIsWrittenUpOnce(string decision, string? reason, string expected)
    {
        var chat = Chat();
        Request("a1");

        Resolve("a1", decision, reason);
        chat.OnApprovalsChanged();
        chat.OnApprovalsChanged();

        Assert.Equal(1, Texts(chat).Count(t => t == expected));
    }

    // An option that names no approval at all is not bound to anything, so it is
    // not the click for the request on the bar: nothing is sent.
    [Fact]
    public async Task AnOptionBoundToNoApprovalSendsNothingAndSaysTheRequestChanged()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve" ? Applied(r) : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(new ChatPromptOption("deny", "Deny"));

            Assert.Empty(Resolves(socket));
            Assert.Contains(OpenClawApprovalText.RequestChanged, Texts(chat));
        }
    }

    // A gateway refusal with no message still says that there is no reason.
    [Fact]
    public async Task AFailureWithAnEmptyMessageSaysThereIsNoReason()
    {
        var (_, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve"
            ? FakeGatewaySocket.Error(r.Id, "UNAVAILABLE", "", null)
            : null);
        using (gateway)
        {
            var chat = Chat();
            Request("a1");

            await chat.AnswerAsync(Option(chat, "deny"));

            Assert.Contains("Couldn't answer: no reason given", Texts(chat));
        }
    }

    // More waiting than the store keeps (8 per conversation): the oldest drops out
    // of view and the panel says so, once.
    [Fact]
    public void ARequestEvictedByALaterOneIsWrittenUpAsDroppedFromView()
    {
        var chat = Chat();
        for (var i = 1; i <= 9; i++) Request("e" + i, "echo " + i, createdOffset: i);

        chat.OnApprovalsChanged();
        chat.OnApprovalsChanged();

        Assert.Equal(1, Texts(chat).Count(t => t == OpenClawApprovalText.Evicted));
    }

    // A record carrying no command length (the default, 0) draws and cannot count
    // what was cut; the parser always stamps one, and a record built without it is
    // handed in directly.
    [Fact]
    public void ARecordWithNoCommandLengthStillDrawsAndOneWithItCountsWhatWasCut()
    {
        var chat = Chat();
        var bare = new OpenClawApproval("n1", "exec", _key, "alpha", "echo bare", "/tmp", "gateway",
            new DateTimeOffset(Now), new DateTimeOffset(Now.AddMinutes(30)));
        OpenClawSessions.ReplacePending("exec", new[] { bare }, new DateTimeOffset(Now));
        Assert.StartsWith("Nova wants to run: echo bare", chat.Prompt!.Title);

        OpenClawSessions.Restart();
        Request("n2", new string('x', 4300));
        Assert.Contains("(300 more characters)", Chat().Prompt!.Title);
    }

    // The two ways an ending can arrive with nothing declared and no decision we
    // know: the local clock passing the expiry reads as expired, and anything else
    // (the poll no longer listing it, or a decision we do not recognise) reads as no
    // longer pending.
    [Fact]
    public void AnEndingWithNoDeclaredStatusReadsAsExpiredOnlyWhenTheClockSaidSo()
    {
        var chat = Chat();
        Request("x1", "echo 1");
        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now.AddMinutes(31)));
        chat.OnApprovalsChanged();
        Assert.Contains("Expired unanswered", Texts(chat));

        Request("x2", "echo 2");
        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));
        chat.OnApprovalsChanged();
        Assert.Contains(OpenClawApprovalText.NoLongerPending, Texts(chat));

        Request("x3", "echo 3");
        Resolve("x3", "mystery");
        chat.OnApprovalsChanged();
        Assert.Equal(2, Texts(chat).Count(t => t == OpenClawApprovalText.NoLongerPending));
    }

    // A record that reached the panel with no command text (however the feed made
    // it) never offers Allow once, and a click for it sends nothing.
    [Fact]
    public async Task ARecordWithNoCommandTextOffersOnlyDenyAndAllowOnceSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "approval.resolve" ? Applied(r) : null);
        using (gateway)
        {
            var chat = Chat();
            var bare = new OpenClawApproval("c1", "exec", _key, "alpha", "", "/tmp", "gateway",
                new DateTimeOffset(Now), new DateTimeOffset(Now.AddMinutes(30)));
            OpenClawSessions.ReplacePending("exec", new[] { bare }, new DateTimeOffset(Now));

            var prompt = chat.Prompt!;
            Assert.StartsWith(OpenClawApprovalText.UnsafeRequest, prompt.Title);
            Assert.Equal("deny", Assert.Single(prompt.Options).Key);

            await chat.AnswerAsync(new ChatPromptOption("allow-once", "Allow once", "c1"));
            Assert.Empty(Resolves(socket));
        }
    }

    // The poll finding it gone: before its expiry nothing says why, after it, it
    // expired.
    [Fact]
    public void ARequestThePollFoundGoneOrExpiredIsWrittenUp()
    {
        var chat = Chat();
        Request("a1");
        Request("a2");

        OpenClawSessions.ReplacePending("exec", new[] { OpenClawApprovals.ParseRecord(Json(Row("a2", minutes: 30)), "exec", new DateTimeOffset(Now))! }, new DateTimeOffset(Now));
        chat.OnApprovalsChanged();
        Assert.Contains(OpenClawApprovalText.NoLongerPending, Texts(chat));

        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now.AddMinutes(31)));
        chat.OnApprovalsChanged();
        Assert.Contains("Expired unanswered", Texts(chat));
    }

    // Opening a panel writes up what ended while nothing was watching.
    [Fact]
    public void OpeningAPanelWritesUpWhatEndedWhileNobodyWasWatching()
    {
        var chat = Chat();
        Request("a1");
        Resolve("a1", "deny");

        chat.PanelOpened();

        Assert.Contains("Denied by another reviewer", Texts(chat));
        chat.PanelClosed();
    }

    // --- elsewhere ---

    [Fact]
    public void AnsweringElsewhereGivesTheIdAsQuotedData()
    {
        var chat = Chat();
        Request("x'; rm -rf ~'");

        chat.AnswerElsewhere();

        var note = Assert.Single(Texts(chat), t => t.StartsWith("To answer this yourself"));
        Assert.Contains("'x'\\''; rm -rf ~'\\'''", note);
        Assert.DoesNotContain("resolve x", note);
    }

    [Fact]
    public void AnsweringElsewhereWithNothingPendingSaysNothing()
    {
        var chat = Chat();

        chat.AnswerElsewhere();

        Assert.Empty(chat.History);
    }
}
