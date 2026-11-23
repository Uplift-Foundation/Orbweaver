using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: a cloud session's model and effort set from the flyout, end to end
// through the seam: the session builds the control_request, posts it to the
// session's events path, and the answer is read back through the real
// OutcomeFor. set_model is the shape measured on a 2.1.317 worker;
// apply_flag_settings on a worker is M2 and unmeasured.
public class ClaudeCloudRunPayloadTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private sealed class Api : ICloudApi
    {
        internal int Status = 200;
        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            using var request = CloudRequest.Build(context.AccessToken, context.Path, context.Method, context.Body);
            var outcome = CloudOutcomes.OutcomeFor(Status, Status == 200 ? "{}" : "<html></html>");
            return Task.FromResult(new CloudApiResult(outcome, null));
        }
    }

    private sealed class Credentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "present");
    }

    private static ClaudeCloudChatSession Chat(Api api) =>
        new(new ClaudeCloudSessions.Session("session_01FixtureOnly", "t", "idle", new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc),
                "https://claude.ai/code/session_01FixtureOnly", "idle", false, "claude-opus-5-5", null, null, null),
            api, new Credentials(), action => action())
        {
            ReadBudget = CredentialBudget.Unmeasured,
            Enabled = () => true,
            RunCatalogue = () => RunSources.ClaudeFallback,
        };

    [Theory]
    [InlineData("model", "sonnet", """{"subtype":"set_model","model":"sonnet"}""")]
    [InlineData("effort", "xhigh", """{"subtype":"apply_flag_settings","settings":{"effortLevel":"xhigh"}}""")]
    public async Task ASetPostsTheControlRequestToTheSessionsEventsPath(string field, string value, string expected)
    {
        var api = new Api();
        var chat = Chat(api);

        Assert.Null(field == "model" ? await chat.SetModelAsync(value) : await chat.SetThinkingAsync(value));

        var request = Assert.Single(api.Requests);
        Assert.Equal("/v1/code/sessions/session_01FixtureOnly/events", request.Path);
        using var doc = JsonDocument.Parse(request.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        Assert.Equal(JsonNode.Parse(expected)!.ToJsonString(), JsonNode.Parse(payload.GetProperty("request").GetRawText())!.ToJsonString());
        Assert.DoesNotContain(Token, request.Body!, StringComparison.Ordinal);
    }

    // The paired refusal: an edge that refuses leaves nothing pending.
    [Fact]
    public async Task AnEdgeRefusalIsReturnedAndNothingIsPending()
    {
        var api = new Api { Status = 403 };
        var chat = Chat(api);

        Assert.NotNull(await chat.SetModelAsync("sonnet"));
        Assert.False(chat.RunOptions!.ModelPending);
    }
}
