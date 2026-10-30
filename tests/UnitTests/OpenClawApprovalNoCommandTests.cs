using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// A person must never be offered Allow on something they cannot read. The installed-app
// check offered Allow once on a prompt that did not show the command. A request whose
// command text is missing, empty or only whitespace after parsing is Deny only, in the
// record, in the prompt and in the one method that answers.
[Collection("Settings")]
public class OpenClawApprovalNoCommandTests : IDisposable
{
    private const string Key = "agent:main:dashboard:00000000-0000-4000-8000-000000000003";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalNoCommandTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
    }

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
    }

    private static string Row(string id, string requestFields) =>
        "{\"approvalKind\":\"exec\",\"id\":\"" + id + "\",\"request\":{" + requestFields + "\"sessionKey\":\"" + Key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(10)) + "}";

    private static string Snapshot(string id, string presentationFields) =>
        "{\"id\":\"" + id + "\",\"status\":\"pending\",\"presentation\":{\"kind\":\"exec\"," + presentationFields +
        "\"allowedDecisions\":[\"allow-once\",\"allow-always\",\"deny\"]},\"urlPath\":\"/approve/" + id + "\"," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(10)) + "}";

    // Every way a request can arrive with nothing readable to decide on.
    public static TheoryData<string> BlankRequests() => new()
    {
        "",
        "\"command\":null,",
        "\"command\":\"\",",
        "\"command\":\"   \",",
        "\"command\":\"\\n\\t \",",
        "\"command\":5,",
        "\"commandArgv\":[],",
        "\"commandArgv\":[\"\",\"  \"],",
        "\"cwd\":\"/tmp\",\"host\":null,",
    };

    [Theory]
    [MemberData(nameof(BlankRequests))]
    public void ABlankLegacyRequestHasNoCommandToShow(string fields)
    {
        var approval = OpenClawApprovals.ParseRecord(Json(Row("a1", fields)), "exec", new DateTimeOffset(Now))!;

        Assert.True(approval.NoCommandToShow);
        Assert.Equal("", approval.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"commandText\":null,\"commandPreview\":null,")]
    [InlineData("\"commandText\":\"\",\"commandPreview\":\"  \",")]
    public void ABlankSnapshotHasNoCommandToShow(string fields)
    {
        var approval = OpenClawApprovals.ParseSnapshot(Json(Snapshot("a1", fields)), Key, new DateTimeOffset(Now))!;

        Assert.True(approval.NoCommandToShow);
        Assert.Equal("", approval.Command);
    }

    // Controls: anything readable, the preview as the fallback, and unsafe text
    // (which has its own refusal) are not "no command".
    [Fact]
    public void AReadableCommandIsNotNoCommand()
    {
        Assert.False(OpenClawApprovals.ParseRecord(Json(Row("a1", "\"command\":\"true\",")), "exec", new DateTimeOffset(Now))!.NoCommandToShow);
        Assert.False(OpenClawApprovals.ParseRecord(Json(Row("a2", "\"commandArgv\":[\"ls\",\"-la\"],")), "exec", new DateTimeOffset(Now))!.NoCommandToShow);
        Assert.False(OpenClawApprovals.ParseSnapshot(Json(Snapshot("a3", "\"commandText\":null,\"commandPreview\":\"rm -rf …\",")), Key, new DateTimeOffset(Now))!.NoCommandToShow);

        var unsafeRow = OpenClawApprovals.ParseRecord(Json(Row("a4", "\"command\":\"ls \\u202E\",")), "exec", new DateTimeOffset(Now))!;
        Assert.True(unsafeRow.Unsafe);
        Assert.False(unsafeRow.NoCommandToShow);
    }

    // The prompt: Deny only, says why, and says where to answer. Never a title that
    // reads as a request to run something.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void APromptWithNoCommandOffersDenyOnly(string command)
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Lilibeth", command, "/tmp", null);

        var only = Assert.Single(prompt.Options);
        Assert.Equal("deny", only.Key);
        Assert.StartsWith(OpenClawApprovalText.UnsafeRequest, prompt.Title);
        Assert.DoesNotContain("wants to run", prompt.Title);
        Assert.Equal(OpenClawApprovalText.ElsewhereLabel, prompt.ElsewhereLabel);
    }

    [Fact]
    public void APromptWithACommandStillOffersBoth()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Lilibeth", "true", "/tmp", null);

        Assert.Equal(new[] { "deny", "allow-once" }, prompt.Options.Select(o => o.Key));
    }

    private static async Task<FakeGatewaySocket> ConnectedAsync()
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
                : request.Method == "approval.resolve"
                    ? FakeGatewaySocket.Ok(request.Id, Json("{\"applied\":true}"))
                    : FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Assert.Equal(OpenClawGateway.Outcome.Connected, (await gateway.ConnectAsync(null, CancellationToken.None)).Outcome);
        OpenClawSessions.SetGatewayForTests(gateway);
        return socket;
    }

    // The one method that answers refuses an allow for such a request and sends
    // nothing, even if a stale or hostile caller asks; a deny is still sent.
    [Fact]
    public async Task AnAllowForARequestWithNoCommandIsRefusedAndNothingIsSent()
    {
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("a1", "\"command\":\"  \",")), Now);
        var socket = await ConnectedAsync();
        var chat = new OpenClawChatSession("openclaw:" + Key, Key, "Lilibeth");

        var refused = await OpenClawSessions.ResolveApprovalAsync(chat, "a1", "allow-once");

        Assert.Equal(OpenClawApprovalResult.Failed, refused.Result);
        Assert.Contains("only be denied", refused.Detail);
        Assert.Empty(socket.Requests.Where(r => r.Method == "approval.resolve"));

        var denied = await OpenClawSessions.ResolveApprovalAsync(chat, "a1", "deny");
        Assert.Equal(OpenClawApprovalResult.Answered, denied.Result);
        Assert.Single(socket.Requests.Where(r => r.Method == "approval.resolve"));
    }
}
