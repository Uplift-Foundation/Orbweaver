using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// What a local session offers and does with a typed slash command, per channel
// (CB-306): a Desktop chat or a headless job with a Remote Control bridge runs
// the five control commands and refuses everything else with nothing sent; a
// headless job on its peer socket alone runs nothing; a Desktop chat with no
// bridge says how to switch Remote Control on.
//
// Everything crosses fakes: no credential is read, no socket opened.
[Collection("Settings")]
public class LocalCliChatSessionSlashTests : IDisposable
{
    private const string Bridge = "session_01FixtureOnly";
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private readonly bool _replyBefore = OrbweaverSettings.ClaudeCodeReplyEnabled;

    public LocalCliChatSessionSlashTests() => OrbweaverSettings.ClaudeCodeReplyEnabled = true;

    public void Dispose()
    {
        OrbweaverSettings.ClaudeCodeReplyEnabled = _replyBefore;
        foreach (var dir in _cleanup)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class Api : ICloudApi
    {
        internal int Status { get; set; } = 200;
        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            var outcome = CloudOutcomes.OutcomeFor(Status, Status == 200 ? "{}" : "<html></html>");
            return Task.FromResult(new CloudApiResult(outcome, null));
        }
    }

    private sealed class Credentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } = new(CredentialOutcome.Found, Token, null, "present");
        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private static SessionRegistry.Entry Row(bool bridge, int protocol = 1) =>
        SessionRegistry.Parse(
            "{\"pid\":4242,\"sessionId\":\"s1\",\"messagingSocketPath\":\"/tmp/a.sock\",\"peerProtocol\":"
            + protocol + (bridge ? $",\"bridgeSessionId\":\"{Bridge}\"" : "") + "}", null)!.Value;

    private static LocalCliChatSession Headless(Api api, Credentials credentials,
        SessionRegistry.Entry? entry, bool cloudOn = true, bool write = true, string cwd = "")
    {
        var messenger = new SessionMessenger(new SessionMessenger.Seams(
            Registry: () => entry is { } e ? new[] { e } : Array.Empty<SessionRegistry.Entry>(),
            PidAlive: _ => true, ReadKey: _ => null, Write: (_, _, _) => Task.FromResult(write)));

        return new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Shape = LocalSessionShape.Background,
            State = "idle",
            Title = "job-hunter",
            Cwd = cwd,
        }, messenger: messenger, findRegistry: id => entry is { } f && f.SessionId == id ? f : null,
        cloudApi: api, credentialsFor: _ => credentials, cloudEnabled: () => cloudOn)
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Delay = (_, _) => Task.CompletedTask,
        };
    }

    private static LocalCliChatSession Desktop(Api api, Credentials credentials, string bridge, bool cloudOn = true, string cwd = "") =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Entrypoint = SessionStatus.DesktopEntrypoint,
            BridgeSessionId = bridge,
            State = "idle",
            Title = "",
            Cwd = cwd,
        }, cloudApi: api, credentialsFor: _ => credentials, cloudEnabled: () => cloudOn)
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Delay = (_, _) => Task.CompletedTask,
        };

    private static JsonElement ControlRequestOf(CloudRequestContext request)
    {
        using var doc = JsonDocument.Parse(request.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        return payload.GetProperty("request").Clone();
    }

    private static IEnumerable<string> Runnable(LocalCliChatSession s) =>
        s.SlashCommands.Where(c => c.Route != SlashRouteKind.Unavailable).Select(c => c.Name);

    // A project with one custom command in .claude/commands, so "known to the
    // CLI" includes something no fixed list could name.
    private string ProjectWithCustomCommand()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb306-slash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cwd, ".claude", "commands"));
        File.WriteAllText(Path.Combine(cwd, ".claude", "commands", "ship-it.md"), "Ship it.");
        _cleanup.Add(cwd);
        return cwd;
    }

    private readonly List<string> _cleanup = new();

    private static readonly string[] Five = { "/color", "/rename", "/model", "/permissions", "/think" };

    // --- what is offered ---

    [AvaloniaFact]
    public void ABridgedHeadlessJobOffersTheFiveControlCommands()
    {
        var session = Headless(new Api(), new Credentials(), Row(bridge: true));

        Assert.Equal(SlashChannel.Bridge, session.SlashChannelNow);
        Assert.Equal(Five.Order(), Runnable(session).Order());
        // The rest of what the CLI knows is listed but unavailable, for the
        // panel to hide and the known-name check to use.
        Assert.Equal(SlashRouteKind.Unavailable, session.SlashCommands.Single(c => c.Name == "/compact").Route);
        Assert.Equal(Bridge, session.BridgeId);
    }

    // Replaces SlashCommandsAreEmptyWhenTheChannelIsMessaging for the bridge-less
    // half: a socket alone still offers nothing.
    [AvaloniaFact]
    public void AHeadlessJobOnItsSocketAloneOffersNothing()
    {
        var session = Headless(new Api(), new Credentials(), Row(bridge: false));

        Assert.Equal(SlashChannel.PeerSocket, session.SlashChannelNow);
        Assert.Empty(Runnable(session));
        Assert.Contains(session.SlashCommands, c => c.Name == "/compact");
        Assert.Null(session.BridgeId);
    }

    [AvaloniaFact]
    public void ABridgedDesktopChatOffersTheFiveAndAnUnbridgedOneNothing()
    {
        Assert.Equal(Five.Order(), Runnable(Desktop(new Api(), new Credentials(), Bridge)).Order());
        Assert.Empty(Runnable(Desktop(new Api(), new Credentials(), "")));
    }

    // --- running ---

    [AvaloniaTheory]
    [InlineData("/color pink", "set_color", "color", "pink")]
    [InlineData("/rename my session", "rename_session", "title", "my session")]
    [InlineData("/model opus", "set_model", "model", "opus")]
    [InlineData("/permissions plan", "set_permission_mode", "mode", "plan")]
    public async Task AControlCommandIsPostedToTheBridgeSessionAndNothingElseIsSent(
        string line, string subtype, string field, string value)
    {
        var api = new Api();
        foreach (var session in new[]
                 {
                     Headless(api, new Credentials(), Row(bridge: true)),
                     Desktop(api, new Credentials(), Bridge),
                 })
        {
            api.Requests.Clear();

            Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync(line));

            var request = Assert.Single(api.Requests);
            Assert.Equal($"/v1/code/sessions/{Bridge}/events", request.Path);
            Assert.Equal(Token, request.AccessToken);
            var body = ControlRequestOf(request);
            Assert.Equal(subtype, body.GetProperty("subtype").GetString());
            Assert.Equal(value, body.GetProperty(field).GetString());
            Assert.Contains(session.History, t => t.Text == $"Sent {line} over Remote Control.");
            Assert.DoesNotContain(Token, request.Body!, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task ThinkTakesANumericBudgetAndABareModelMeansDefault()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: true));

        await session.RunSlashAsync("/think 8000");
        await session.RunSlashAsync("/model");

        Assert.Equal(8000, ControlRequestOf(api.Requests[0]).GetProperty("max_thinking_tokens").GetInt32());
        Assert.Equal("default", ControlRequestOf(api.Requests[1]).GetProperty("model").GetString());
        Assert.Contains(session.History, t => t.Text == "Sent /model over Remote Control.");
    }

    // The negative control: /compact is not a control command, so it is refused
    // with the reason and nothing is delivered anywhere.
    [AvaloniaFact]
    public async Task CompactIsRefusedOverTheBridgeWithNothingDelivered()
    {
        var api = new Api();
        foreach (var session in new[]
                 {
                     Headless(api, new Credentials(), Row(bridge: true)),
                     Desktop(api, new Credentials(), Bridge),
                 })
        {
            Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/compact"));
            Assert.Contains(session.History, t => t.Text ==
                SlashRouting.RefusalNote("/compact", SlashRouting.PeerMessageReason));
        }

        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public async Task APeerSocketJobRefusesEveryCommandWithTheMessagingReason()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: false));

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/color pink"));

        Assert.Contains(session.History, t => t.Text ==
            SlashRouting.RefusalNote("/color", SlashRouting.PeerSocketReason));
        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public async Task ADesktopChatWithNoBridgeSaysHowToSwitchRemoteControlOn()
    {
        var api = new Api();
        var session = Desktop(api, new Credentials(), "");

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/rename x"));

        Assert.Contains(session.History, t => t.Text.Contains(LocalCliChatSession.DesktopNoBridgeHint));
        Assert.Empty(api.Requests);
    }

    [AvaloniaTheory]
    [InlineData("/color")]
    [InlineData("/rename")]
    [InlineData("/permissions")]
    [InlineData("/think soon")]
    public async Task AControlCommandWithAnArgumentItCannotUseIsRefusedBeforeAnyPost(string line)
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: true));

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync(line));

        Assert.Contains(session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public async Task WithCloudSessionsOffTheCommandIsRefusedAndTheReasonIsTheSetting()
    {
        var api = new Api();
        var headless = Headless(api, new Credentials(), Row(bridge: true), cloudOn: false);
        var desktop = Desktop(api, new Credentials(), Bridge, cloudOn: false);

        Assert.Equal(ChatSendOutcome.Failed, await headless.RunSlashAsync("/color pink"));
        Assert.Equal(ChatSendOutcome.Failed, await desktop.RunSlashAsync("/color pink"));

        Assert.Contains(headless.History, t => t.Text == "Not sent: " + LocalCliChatSession.CloudOffControlHint);
        Assert.Contains(desktop.History, t => t.Text == "Not sent: " + LocalCliChatSession.DesktopCloudOffHint);
        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public async Task AMissingLoginIsReportedAndNothingIsPosted()
    {
        var api = new Api();
        var credentials = new Credentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "no credential"),
        };
        var session = Headless(api, credentials, Row(bridge: true));

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/color pink"));

        Assert.Contains(session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public async Task ARefusedPostLeavesTheApisReasonAndFails()
    {
        var api = new Api { Status = 500 };
        var session = Headless(api, new Credentials(), Row(bridge: true));

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/color pink"));

        Assert.Single(api.Requests);
        Assert.Contains(session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
        Assert.DoesNotContain(session.History, t => t.Text.StartsWith("Sent /color", StringComparison.Ordinal));
    }

    // A session with no pane, no socket and no bridge keeps the Terminal answer
    // it always had: the typed route goes through the ordinary send, which fails
    // on its own terms rather than becoming a control request.
    [AvaloniaFact]
    public async Task ATypedRouteGoesThroughTheOrdinarySendAndNeverTheBridge()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), entry: null);

        Assert.Equal(SlashChannel.Terminal, session.SlashChannelNow);
        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/compact"));

        Assert.Empty(api.Requests);
    }

    // "/" alone is not a command: it is an ordinary line, sent as one.
    [AvaloniaFact]
    public async Task ALoneSlashIsAnOrdinarySend()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: false));

        Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync("/"));

        Assert.Empty(api.Requests);
        Assert.Contains(session.History, t => t.Text.Contains("Handed to job-hunter", StringComparison.Ordinal));
    }

    // The known-name rule: a path or an unknown /word is prose, delivered as an
    // ordinary message (not refused, not a control request).
    [AvaloniaTheory]
    [InlineData("/Users/me/notes.txt please read")]
    [InlineData("/definitely-not-a-command")]
    public async Task AnUnknownSlashWordIsDeliveredAsAnOrdinaryMessage(string line)
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: true));

        Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync(line));

        Assert.Empty(api.Requests);
        Assert.Contains(session.History, t => t.Text.Contains("Handed to job-hunter", StringComparison.Ordinal));
    }

    // Known to the CLI but not runnable on the channel: refused, nothing sent —
    // a built-in and a project's own custom command, on every channel.
    [AvaloniaFact]
    public async Task AKnownButUnavailableCommandIsRefusedOnEveryChannelWithNothingSent()
    {
        var cwd = ProjectWithCustomCommand();
        var api = new Api();

        foreach (var (session, reason) in new[]
                 {
                     (Headless(api, new Credentials(), Row(bridge: true), cwd: cwd), SlashRouting.PeerMessageReason),
                     (Desktop(api, new Credentials(), Bridge, cwd: cwd), SlashRouting.PeerMessageReason),
                     (Headless(api, new Credentials(), Row(bridge: false), cwd: cwd), SlashRouting.PeerSocketReason),
                 })
        {
            Assert.Contains(session.SlashCommands, c => c.Name == "/ship-it");

            foreach (var name in new[] { "/compact", "/ship-it" })
            {
                Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync(name + " now"));
                Assert.Contains(session.History, t => t.Text == SlashRouting.RefusalNote(name, reason));
            }
        }

        Assert.Empty(api.Requests);
    }

    // The same two channels that refuse a known command deliver a path, a bare
    // "/" and an unknown word as the ordinary messages they are.
    [AvaloniaTheory]
    [InlineData("/tmp/x.log boom")]
    [InlineData("/")]
    [InlineData("/nonsense")]
    public async Task PathsAndUnknownWordsAreDeliveredOnBridgeAndPeerSocketAlike(string line)
    {
        var api = new Api();

        foreach (var session in new[]
                 {
                     Headless(api, new Credentials(), Row(bridge: true)),
                     Headless(api, new Credentials(), Row(bridge: false)),
                 })
        {
            Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync(line));
            Assert.Contains(session.History, t => t.Text.Contains("Handed to job-hunter", StringComparison.Ordinal));
            Assert.DoesNotContain(session.History, t => t.Text.StartsWith("Not sent", StringComparison.Ordinal));
        }

        Assert.Empty(api.Requests);
    }

    [AvaloniaFact]
    public async Task APathToABridgedDesktopChatGoesThroughTheBridgeAsAMessage()
    {
        var api = new Api();
        var session = Desktop(api, new Credentials(), Bridge);

        Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync("/tmp/x.log boom"));

        var request = Assert.Single(api.Requests);
        using var doc = JsonDocument.Parse(request.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("user", payload.GetProperty("type").GetString());
        Assert.Equal("/tmp/x.log boom", payload.GetProperty("message").GetProperty("content").GetString());
    }

    // The one case that still lists commands it cannot send: no pane, no socket,
    // no bridge. Its typed send must fail with a note a person can read.
    [AvaloniaFact]
    public async Task ASessionWithNowhereToSendFailsVisiblyNotSilently()
    {
        var session = Headless(new Api(), new Credentials(), entry: null);

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/compact"));

        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text.Length > 0);
    }
}
