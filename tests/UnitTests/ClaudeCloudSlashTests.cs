using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-306, cloud arm: the catalogue read off system/init and the runner's arms.
// A partial of ClaudeCloudEventsTests so the same fakes (RoutingApi, Sender,
// FakeStream) drive it.
public partial class ClaudeCloudEventsTests
{
    private static string InitRow(string commands, string? terminal = null) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"slash_commands\":" + commands
        + (terminal is null ? "" : ",\"terminal_slash_commands\":" + terminal) + "}";

    // --- CloudSlashCatalog -------------------------------------------------

    [Fact]
    public void AnInitsListsAreReadWithoutTheirSlashes()
    {
        var parsed = CloudSlashCatalog.ParseInit(
            InitRow("[\"compact\",\"/rename\",\"\",5]", "[\"color\"]"));

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "compact", "rename" }, parsed!.Value.Commands);
        Assert.Equal(new[] { "color" }, parsed.Value.Terminal);
    }

    [Fact]
    public void AnInitWithoutATerminalListHidesNothing()
    {
        var parsed = CloudSlashCatalog.ParseInit(InitRow("[\"model\"]"));

        Assert.Empty(parsed!.Value.Terminal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"system\"}")]
    [InlineData("{\"slash_commands\":\"rename\"}")]
    public void AnInitThatTellsUsNothingParsesToNull(string? row)
    {
        Assert.Null(CloudSlashCatalog.ParseInit(row));
    }

    [Fact]
    public void OnlyWhatACloudChannelCanRunIsOffered()
    {
        var offered = CloudSlashCatalog.Offered(
            new[] { "compact", "rename", "model", "color", "permissions", "think", "rename", "my-skill" },
            new[] { "color", "PERMISSIONS" });

        // /color is terminal-only (and refused for a cloud session anyway), /permissions is
        // hidden by the terminal list ignoring case, /compact and a skill have no control
        // request, and a name init lists twice is offered once.
        Assert.Equal(new[] { "/rename", "/model", "/think" }, offered.Select(c => c.Name));
        Assert.All(offered, c => Assert.Equal(SlashRouteKind.Control, c.Route));
        Assert.All(offered, c => Assert.NotEqual("", c.Description));
    }

    [Fact]
    public void ACommandOnlyTheRoutingTableKnowsStillGetsAnEmptyDescription()
    {
        // /permissions is in the table and described; this pins the fallback arm.
        var offered = CloudSlashCatalog.Offered(new[] { "permissions" }, Array.Empty<string>());
        Assert.Equal("Change the permission mode", Assert.Single(offered).Description);
        Assert.Empty(CloudSlashCatalog.Offered(new[] { "compact" }, Array.Empty<string>()));
    }

    // --- init arrives on the stream ---------------------------------------

    private static CloudStreamEvent Init(long seq, string row) =>
        new(CloudStreamEventKind.Durable, "client_event", seq, "{\"payload\":" + row + "}",
            PayloadType: "system", Subtype: "init", RowJson: row);

    [Fact]
    public async Task TheLatestInitsListIsKeptAndAListlessInitLeavesIt()
    {
        var (chat, _, stream, _) = Streaming();
        Assert.Empty(chat.SlashCommands);

        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Init(42, InitRow("[\"rename\",\"compact\"]", "[]")));
        await Until(() => chat.SlashCommands.Count == 1);
        Assert.Equal("/rename", chat.SlashCommands[0].Name);

        // Re-published with a different list: the newest wins.
        events.TryWrite(Init(43, InitRow("[\"model\",\"think\"]")));
        await Until(() => chat.SlashCommands.Count == 2);
        Assert.Equal(new[] { "/model", "/think" }, chat.SlashCommands.Select(c => c.Name));

        // An init with no list at all, and a status that is not an init, change nothing.
        events.TryWrite(Init(44, "{\"type\":\"system\",\"subtype\":\"init\"}"));
        events.TryWrite(Durable(45, "system", "status", "requesting"));
        events.TryWrite(Durable(46, "assistant", payload: AssistantRow("a1", "marker")));
        await Until(() => chat.History.Count == 1);
        Assert.Equal(new[] { "/model", "/think" }, chat.SlashCommands.Select(c => c.Name));

        await Close(chat);
    }

    // --- RunSlashAsync ------------------------------------------------------

    private static JsonElement RequestOf(CloudRequestContext post)
    {
        using var doc = JsonDocument.Parse(post.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        return payload.GetProperty("request").Clone();
    }

    [Fact]
    public async Task AControlCommandPostsItsRequestAndSaysSo()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Sent, await chat.RunSlashAsync("/rename  a better name "));

        var post = Assert.Single(api.Posts);
        Assert.Equal("/v1/code/sessions/session_a/events", post.Path);
        var request = RequestOf(post);
        Assert.Equal("rename_session", request.GetProperty("subtype").GetString());
        Assert.Equal("a better name", request.GetProperty("title").GetString());
        Assert.Equal("Sent /rename a better name.", Assert.Single(Notes(chat)).Text);
    }

    [Fact]
    public async Task ACommandWithNoArgumentsIsConfirmedWithoutATrailingSpace()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Sent, await chat.RunSlashAsync("/model"));

        Assert.Equal("default", RequestOf(Assert.Single(api.Posts)).GetProperty("model").GetString());
        Assert.Equal("Sent /model.", Assert.Single(Notes(chat)).Text);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/color pink")]
    [InlineData("/some-skill do it")]
    public async Task AnUnavailableCommandIsRefusedWithItsReasonAndNothingIsSent(string line)
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync(line));

        Assert.Empty(api.Requests);
        var name = line.Split(' ')[0];
        var expected = SlashRouting.RefusalNote(name,
            SlashRouting.Resolve(SlashChannel.Cloud, name, "").Reason!);
        Assert.Equal(expected, Assert.Single(Notes(chat)).Text);
        Assert.Empty(chat.History.Where(t => t.Role == ChatRole.User));
    }

    [Fact]
    public async Task AnArgumentTheRequestCannotBeBuiltFromIsRefusedBeforeAnyPost()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/think lots"));

        Assert.Empty(api.Requests);
        Assert.StartsWith("Not sent: /think — give a token budget", Assert.Single(Notes(chat)).Text);
    }

    [Fact]
    public async Task ARefusedControlRequestKeepsTheTextAndSaysWhy()
    {
        var api = new RoutingApi(Answer(409, "{\"error\":{\"type\":\"session_not_active\",\"message\":\"x\"}}"));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/rename x"));

        Assert.Single(api.Posts);
        var note = Assert.Single(Notes(chat)).Text;
        Assert.StartsWith("/rename did not reach the session: ", note);
        Assert.EndsWith(".", note);
    }

    [Fact]
    public async Task ASlashRunWithoutALoginSendsNothing()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…"),
        };
        var chat = Sender(api, creds: creds);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/model opus"));

        Assert.Empty(api.Requests);
        Assert.Equal("/model did not reach the session: " + ClaudeCliCredentials.Describe(CredentialOutcome.NotLoggedIn) + ".",
            Assert.Single(Notes(chat)).Text);
    }

    [Fact]
    public async Task AReadOnlyOrSwitchedOffSessionRunsNothing()
    {
        var offApi = new RoutingApi(Answer(200, Receipt));
        var off = Sender(offApi, enabled: false);
        Assert.Equal(ChatSendOutcome.Failed, await off.RunSlashAsync("/model opus"));
        Assert.Empty(offApi.Requests);
        Assert.Equal("Not sent: cloud sessions are switched off in Settings.", Assert.Single(Notes(off)).Text);

        var roApi = new RoutingApi(Answer(200, Receipt));
        var ended = Sender(roApi, session: Row(state: "archived", bucket: "archived"));
        Assert.True(ended.IsReadOnly);
        Assert.Equal(ChatSendOutcome.Failed, await ended.RunSlashAsync("/model opus"));
        Assert.Empty(roApi.Requests);
        Assert.Equal("Not sent: " + ended.ComposerHint, Assert.Single(Notes(ended)).Text);
    }

    [Fact]
    public async Task ABareSlashIsSentAsAMessage()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Sent, await chat.RunSlashAsync("/"));

        var post = Assert.Single(api.Posts);
        using var doc = JsonDocument.Parse(post.Body!);
        Assert.Equal("user", doc.RootElement.GetProperty("events")[0].GetProperty("payload")
            .GetProperty("type").GetString());
    }
}
