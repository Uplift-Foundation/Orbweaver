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
    public void TheCatalogueKeepsEveryNameWithItsCloudRoute()
    {
        var catalogue = CloudSlashCatalog.Catalogue(
            new[] { "compact", "rename", "model", "color", "permissions", "think", "rename", "my-skill" },
            new[] { "color", "PERMISSIONS", "doctor" });

        var routes = catalogue.ToDictionary(c => c.Name, c => c.Route, StringComparer.OrdinalIgnoreCase);

        // Nothing dropped, a name listed twice kept once, terminal-only names
        // (case-insensitively, unless the table has a route) Unavailable, and
        // Claude Code's built-in names present whether init listed them or not.
        Assert.Equal(catalogue.Count, catalogue.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SlashCommandCatalog.ClaudeCodeBuiltins, b => Assert.Contains(b.Name, routes.Keys));
        Assert.Equal(SlashRouteKind.Control, routes["/rename"]);
        Assert.Equal(SlashRouteKind.Control, routes["/model"]);
        Assert.Equal(SlashRouteKind.Control, routes["/think"]);
        // A terminal-only listing does not take away a command the table runs.
        Assert.Equal(SlashRouteKind.Control, routes["/permissions"]);
        Assert.Equal(SlashRouteKind.Unavailable, routes["/color"]);
        Assert.Equal(SlashRouteKind.Unavailable, routes["/doctor"]);
        Assert.Equal(SlashRouteKind.Unavailable, routes["/compact"]);
        Assert.Equal(SlashRouteKind.Unavailable, routes["/my-skill"]);
        Assert.NotEqual("", catalogue.Single(c => c.Name == "/rename").Description);
        Assert.Equal("", catalogue.Single(c => c.Name == "/compact").Description);
    }

    [Fact]
    public void TheFourControlCommandsAreOfferedEvenWhenInitDoesNotListThem()
    {
        var catalogue = CloudSlashCatalog.Catalogue(new[] { "compact", "my-skill" }, new[] { "color" });

        Assert.Equal(new[] { "/rename", "/model", "/permissions", "/think" },
            ChatPanel.OfferedCommands(catalogue).Select(c => c.Name));
        Assert.Contains(catalogue, c => c.Name == "/compact" && c.Route == SlashRouteKind.Unavailable);
    }

    [Fact]
    public void ThePanelOffersOnlyTheControlCommandsOfTheCatalogue()
    {
        var catalogue = CloudSlashCatalog.Catalogue(
            new[] { "compact", "rename", "model", "color", "permissions", "think", "my-skill" },
            new[] { "color" });

        Assert.Equal(new[] { "/rename", "/model", "/permissions", "/think" },
            ChatPanel.OfferedCommands(catalogue).Select(c => c.Name));
    }

    // --- init arrives on the stream ---------------------------------------

    private static CloudStreamEvent Init(long seq, string row) =>
        new(CloudStreamEventKind.Durable, "client_event", seq, "{\"payload\":" + row + "}",
            PayloadType: "system", Subtype: "init", RowJson: row);

    private static bool Knows(ClaudeCloudChatSession chat, string name) =>
        chat.SlashCommands.Any(c => c.Name == name);

    [Fact]
    public async Task TheLatestInitsListIsKeptAndAListlessInitLeavesIt()
    {
        var (chat, _, stream, _) = Streaming();
        // Before any init: the built-ins are known (so /compact is refused, not
        // sent as prose) and the four the routing table runs are offered.
        Assert.True(Knows(chat, "/compact"));
        Assert.Equal(new[] { "/rename", "/model", "/permissions", "/think" },
            ChatPanel.OfferedCommands(chat.SlashCommands).Select(c => c.Name));
        Assert.False(Knows(chat, "/first-skill"));

        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Init(42, InitRow("[\"first-skill\"]", "[]")));
        await Until(() => Knows(chat, "/first-skill"));

        // Re-published with a different list: the newest wins.
        events.TryWrite(Init(43, InitRow("[\"second-skill\"]")));
        await Until(() => Knows(chat, "/second-skill"));
        Assert.False(Knows(chat, "/first-skill"));

        // An init with no list at all, and a status that is not an init, change nothing.
        events.TryWrite(Init(44, "{\"type\":\"system\",\"subtype\":\"init\"}"));
        events.TryWrite(Durable(45, "system", "status", "requesting"));
        events.TryWrite(Durable(46, "assistant", payload: AssistantRow("a1", "marker")));
        await Until(() => chat.History.Count == 1);
        Assert.True(Knows(chat, "/second-skill"));

        await Close(chat);
    }

    // The empty-catalogue case: a panel typed into before init has arrived.
    [Fact]
    public async Task BeforeAnyInitACompactIsRefusedAndARenameStillRuns()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/compact"));
        Assert.Empty(api.Posts);
        Assert.StartsWith("Not sent: /compact can't run from here", Assert.Single(Notes(chat)).Text);

        Assert.Equal(ChatSendOutcome.Sent, await chat.RunSlashAsync("/rename foo"));
        Assert.Equal("rename_session", RequestOf(Assert.Single(api.Posts)).GetProperty("subtype").GetString());
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

        Assert.Equal(ChatSendOutcome.Sent, await chat.RunSlashAsync("/think"));

        Assert.Equal("set_max_thinking_tokens", RequestOf(Assert.Single(api.Posts)).GetProperty("subtype").GetString());
        Assert.Equal("Sent /think.", Assert.Single(Notes(chat)).Text);
    }

    // Teaches the session its command list by way of a real init on the stream.
    private static async Task<(ClaudeCloudChatSession Chat, RoutingApi Api)> WithInit(string commands, string terminal)
    {
        var (chat, api, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();
        events.TryWrite(Init(42, InitRow(commands.Replace("]", ",\"init-marker\"]"), terminal)));
        await Until(() => Knows(chat, "/init-marker"));
        return (chat, api);
    }

    [Theory]
    [InlineData("/compact")]
    [InlineData("/doctor")]
    [InlineData("/some-skill do it")]
    public async Task AKnownButUnavailableCommandIsRefusedWithItsReasonAndNothingIsSent(string line)
    {
        var (chat, api) = await WithInit("[\"compact\",\"doctor\",\"some-skill\",\"rename\"]", "[\"doctor\"]");

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync(line));

        Assert.Empty(api.Posts);
        var name = line.Split(' ')[0];
        var expected = SlashRouting.RefusalNote(name,
            SlashRouting.Resolve(SlashChannel.Cloud, name, "").Reason!);
        Assert.Equal(expected, Assert.Single(Notes(chat)).Text);
        Assert.Empty(chat.History.Where(t => t.Role == ChatRole.User));
        await Close(chat);
    }

    // Before any init has arrived the routing table's own commands still count
    // as commands: /color is refused for a cloud session even then.
    [Fact]
    public async Task ARoutedCommandIsRecognisedBeforeAnyInitHasArrived()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/color pink"));

        Assert.Empty(api.Posts);
        Assert.Equal(SlashRouting.RefusalNote("/color", SlashRouting.CloudNoColorReason),
            Assert.Single(Notes(chat)).Text);
    }

    [Theory]
    [InlineData("/Users/me/notes.txt please read")]
    [InlineData("/not-a-command")]
    public async Task ASlashWordTheCliDoesNotKnowIsAnOrdinaryMessage(string line)
    {
        var (chat, api) = await WithInit("[\"compact\",\"rename\"]", "[]");

        Assert.Equal(ChatSendOutcome.Sent, await chat.RunSlashAsync(line));

        var post = Assert.Single(api.Posts);
        using var doc = JsonDocument.Parse(post.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("user", payload.GetProperty("type").GetString());
        Assert.Equal(line, payload.GetProperty("message").GetProperty("content").GetString());
        await Close(chat);
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

    [Fact]
    public async Task ARenameWithNoNameIsRefusedBeforeAnyPost()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/rename"));

        Assert.Empty(api.Requests);
        Assert.StartsWith("Not sent: /rename — give the session a name", Assert.Single(Notes(chat)).Text);
    }

    // The interrupt posts through the shared poster with its own request object;
    // this pins its wire body to ClaudeCloudSend.InterruptBody's, which the probe
    // and the send tests use, so the two cannot drift apart.
    [Fact]
    public async Task TheInterruptsWireBodyMatchesInterruptBodyExceptItsIds()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, session: BusyRow());

        chat.Cancel();
        await chat.InterruptTask!;

        static JsonElement Request(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("events")[0].GetProperty("payload")
                .GetProperty("request").Clone();
        }

        var posted = Request(Assert.Single(api.Posts).Body!);
        var reference = Request(ClaudeCloudSend.InterruptBody("r", "u"));
        Assert.Equal(reference.GetRawText(), posted.GetRawText());
    }
}
