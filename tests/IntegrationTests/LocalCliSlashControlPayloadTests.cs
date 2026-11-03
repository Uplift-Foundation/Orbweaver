using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// A slash command typed into a Remote Control-bridged Claude Desktop chat
// (CB-306), end to end through the seam: the session resolves the route, builds
// the control_request body, and an ICloudApi answers with a status and body read
// back through the real OutcomeFor. The body shapes are the ones measured on the
// bridge on 2026-11-03 (docs/remote-control-findings.md).
[Collection("Settings")]
public class LocalCliSlashControlPayloadTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string Bridge = "session_01FixtureOnly";

    private sealed class Api : ICloudApi
    {
        internal int Status = 200;
        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            using var request = CloudRequest.Build(context.AccessToken, context.Path, context.Method, context.Body);
            Assert.Equal(System.Net.Http.HttpMethod.Post, request.Method);

            var outcome = CloudOutcomes.OutcomeFor(Status, Status == 200 ? "{}" : "<html></html>");
            return Task.FromResult(new CloudApiResult(outcome, null));
        }
    }

    private sealed class Credentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "present");
    }

    private static LocalCliChatSession Session(Api api) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Entrypoint = SessionStatus.DesktopEntrypoint,
            BridgeSessionId = Bridge,
            State = "idle",
            Title = "",
        }, cloudApi: api, credentialsFor: _ => new Credentials(), cloudEnabled: () => true)
        {
            ReadBudget = CredentialBudget.Unmeasured,
            Delay = (_, _) => Task.CompletedTask,
        };

    [Theory]
    [InlineData("/color pink", """{"subtype":"set_color","color":"pink"}""")]
    [InlineData("/rename Weekly triage", """{"subtype":"rename_session","title":"Weekly triage"}""")]
    [InlineData("/model opus", """{"subtype":"set_model","model":"opus"}""")]
    [InlineData("/permissions plan", """{"subtype":"set_permission_mode","mode":"plan"}""")]
    [InlineData("/think 8000", """{"subtype":"set_max_thinking_tokens","max_thinking_tokens":8000}""")]
    [InlineData("/think default", """{"subtype":"set_max_thinking_tokens","max_thinking_tokens":null}""")]
    public async Task EachControlCommandPostsTheMeasuredRequestToTheBridgeEventsPath(string line, string expected)
    {
        var api = new Api();

        Assert.Equal(ChatSendOutcome.Sent, await Session(api).RunSlashAsync(line));

        var request = Assert.Single(api.Requests);
        Assert.Equal($"/v1/code/sessions/{Bridge}/events", request.Path);

        using var doc = JsonDocument.Parse(request.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        Assert.True(Guid.TryParse(payload.GetProperty("request_id").GetString(), out _));
        Assert.Equal(
            System.Text.Json.Nodes.JsonNode.Parse(expected)!.ToJsonString(),
            System.Text.Json.Nodes.JsonNode.Parse(payload.GetProperty("request").GetRawText())!.ToJsonString());
        Assert.DoesNotContain(Token, request.Body!, StringComparison.Ordinal);
    }

    // The negative control: a built-in the bridge cannot run produces no request
    // at all, so the model never sees it as prose either.
    [Fact]
    public async Task ABuiltInWithNoControlEquivalentPostsNothing()
    {
        var api = new Api();
        var session = Session(api);

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/compact"));

        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task AnEdgeRefusalIsReportedAndIsNotCountedAsSent()
    {
        var api = new Api { Status = 403 };
        var session = Session(api);

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/color pink"));

        Assert.Contains(session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
    }
}
