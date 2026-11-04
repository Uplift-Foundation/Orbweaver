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
        internal List<CloudRequestContext> Reads { get; } = new();
        internal string? AnswerError { get; set; }
        internal bool AnswerOk { get; set; }

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            // CB-306's product-readiness round: the GET that reads a control
            // request's answer. Kept out of Requests ("what was posted"). With
            // AnswerError set, the page answers the last posted request with
            // that CLI error; otherwise it holds no answer.
            if (context.Method is null)
            {
                Reads.Add(context);
                var body = "{\"data\":[]}";
                if ((AnswerError is not null || AnswerOk) && Requests.Count > 0)
                {
                    using var posted = JsonDocument.Parse(Requests[^1].Body!);
                    var rid = posted.RootElement.GetProperty("events")[0].GetProperty("payload")
                        .GetProperty("request_id").GetString();
                    body = AnswerOk
                        ? JsonSerializer.Serialize(new
                        {
                            data = new[] { new { payload = new { type = "control_response", response = new { subtype = "success", request_id = rid } } } },
                        })
                        : JsonSerializer.Serialize(new
                        {
                            data = new[] { new { payload = new { type = "control_response", response = new { subtype = "error", request_id = rid, error = AnswerError } } } },
                        });
                }
                return Task.FromResult(new CloudApiResult(new CloudOutcome(CloudOutcomeKind.Ok, 200), body));
            }

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

    private static SessionRegistry.Entry Row(bool bridge, int protocol = 1, string? version = null) =>
        SessionRegistry.Parse(
            "{\"pid\":4242,\"sessionId\":\"s1\",\"messagingSocketPath\":\"/tmp/a.sock\",\"peerProtocol\":"
            + protocol + (bridge ? $",\"bridgeSessionId\":\"{Bridge}\"" : "")
            + (version is null ? "" : $",\"version\":\"{version}\"") + "}", null)!.Value;

    private static LocalCliChatSession Headless(Api api, Credentials credentials,
        SessionRegistry.Entry? entry, bool cloudOn = true, bool write = true, string cwd = "",
        bool commandsOn = true)
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
        cloudApi: api, credentialsFor: _ => credentials, cloudEnabled: () => cloudOn,
        bridgeCommandsEnabled: () => commandsOn)
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Delay = (_, _) => Task.CompletedTask,
        };
    }

    private static LocalCliChatSession Desktop(Api api, Credentials credentials, string bridge, bool cloudOn = true,
        string cwd = "", bool commandsOn = true) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Entrypoint = SessionStatus.DesktopEntrypoint,
            BridgeSessionId = bridge,
            State = "idle",
            Title = "",
            Cwd = cwd,
        }, cloudApi: api, credentialsFor: _ => credentials, cloudEnabled: () => cloudOn,
        bridgeCommandsEnabled: () => commandsOn)
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
            Assert.Contains(session.History, t => t.Text == $"Sent {line} over Remote Control — no confirmation yet.");
            Assert.DoesNotContain(Token, request.Body!, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task ThinkTakesANumericBudget()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: true));

        await session.RunSlashAsync("/think 8000");

        Assert.Equal(8000, ControlRequestOf(Assert.Single(api.Requests)).GetProperty("max_thinking_tokens").GetInt32());
        Assert.Contains(session.History, t => t.Text == "Sent /think 8000 over Remote Control — no confirmation yet.");
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

    // CB-306's product-readiness round: a job whose registry row names no
    // bridge is told so plainly, with the two ways out. A plain-text line to
    // the same job is not a command and still goes over the socket.
    [AvaloniaFact]
    public async Task AJobWithNoBridgeSaysItHasNoRemoteControlAndPlainTextStillDelivers()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: false));

        await session.RunSlashAsync("/model opus");

        var note = Assert.Single(session.History, t => t.Text.StartsWith("Not sent: /model", StringComparison.Ordinal)).Text;
        Assert.Contains("no Remote Control connection", note);
        Assert.Contains("--remote-control", note);
        Assert.Contains("terminal pane", note);
        Assert.DoesNotContain("socket", note);

        Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync("please carry on"));
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
    [InlineData("/model")]
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

    // CB-306's product-readiness round: the bridge-commands setting, not
    // "Show cloud sessions", is the gate. Off, both kinds of session refuse
    // with a note naming the toggle and post nothing.
    [AvaloniaFact]
    public async Task WithBridgeCommandsOffTheCommandIsRefusedAndTheNoteNamesTheToggle()
    {
        var api = new Api();
        var headless = Headless(api, new Credentials(), Row(bridge: true), commandsOn: false);
        var desktop = Desktop(api, new Credentials(), Bridge, commandsOn: false);

        Assert.Equal(ChatSendOutcome.Failed, await headless.RunSlashAsync("/color pink"));
        Assert.Equal(ChatSendOutcome.Failed, await desktop.RunSlashAsync("/color pink"));

        var note = "Not sent: " + LocalCliChatSession.BridgeCommandsOffHint;
        Assert.Contains(headless.History, t => t.Text == note);
        Assert.Contains(desktop.History, t => t.Text == note);
        Assert.Contains(LocalCliChatSession.BridgeCommandsToggleLabel, note);
        Assert.Empty(api.Requests);
    }

    // CB-306's product-readiness round: the CLI answering "error" is a
    // rejection, not a send, and the note carries the CLI's sentence beside
    // this session's Claude Code version and the one the requests were
    // measured on. Nothing is said about versions the row does not name.
    [AvaloniaTheory]
    [InlineData("2.1.318", true)]
    [InlineData(null, false)]
    public async Task ARejectionFromTheCliNamesTheVersions(string? version, bool named)
    {
        var api = new Api { AnswerError = "Unknown color \"teal\". Available: red, blue" };
        var session = Headless(api, new Credentials(), Row(bridge: true, version: version));

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/color teal"));

        var note = Assert.Single(session.History, t => t.Text.StartsWith("Not run: ", StringComparison.Ordinal)).Text;
        Assert.Contains("Claude Code refused it: Unknown color \"teal\". Available: red, blue", note);
        Assert.Equal(named, note.Contains("This session runs Claude Code 2.1.318; Orbweaver's commands were measured on 2.1.317."));
        Assert.Equal(named, note.Contains("2.1.317"));
        Assert.DoesNotContain(session.History, t => t.Text.StartsWith("Sent ", StringComparison.Ordinal));
        Assert.Single(api.Requests);
        Assert.Single(api.Reads);
    }

    // No answer within the budget is said as exactly that — never a plain
    // "Sent", which reads as success — and the CLI's "success" is "Done".
    [AvaloniaFact]
    public async Task NoAnswerFromTheCliSaysNoConfirmationYet()
    {
        var api = new Api();
        var session = Headless(api, new Credentials(), Row(bridge: true, version: "2.1.318"));

        Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync("/color pink"));

        Assert.Contains(session.History, t => t.Text == "Sent /color pink over Remote Control — no confirmation yet.");
        Assert.DoesNotContain(session.History, t => t.Text.StartsWith("Done", StringComparison.Ordinal));
        Assert.Equal(CloudTurnSender.AnswerPolls, api.Reads.Count);
    }

    [AvaloniaFact]
    public async Task ASuccessAnswerFromTheCliSaysDone()
    {
        var api = new Api { AnswerOk = true };
        var session = Headless(api, new Credentials(), Row(bridge: true));

        Assert.Equal(ChatSendOutcome.Sent, await session.RunSlashAsync("/rename tidy"));

        Assert.Contains(session.History, t => t.Text == "Done: /rename tidy.");
        Assert.Single(api.Reads);
    }

    // ...and with cloud off but commands on — a fresh install — both post.
    [AvaloniaFact]
    public async Task WithCloudSessionsOffButCommandsOnBothKindsPostTheCommand()
    {
        var api = new Api();
        var headless = Headless(api, new Credentials(), Row(bridge: true), cloudOn: false);
        var desktop = Desktop(api, new Credentials(), Bridge, cloudOn: false);

        Assert.Equal(ChatSendOutcome.Sent, await headless.RunSlashAsync("/color pink"));
        Assert.Equal(ChatSendOutcome.Sent, await desktop.RunSlashAsync("/rename tidy"));

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("set_color", ControlRequestOf(api.Requests[0]).GetProperty("subtype").GetString());
        Assert.Equal("rename_session", ControlRequestOf(api.Requests[1]).GetProperty("subtype").GetString());
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

    // /think is not in the CLI's built-in catalogue, but it is ours to refuse:
    // off a bridge it is a known command, never prose for the model.
    [AvaloniaFact]
    public async Task ThinkIsRefusedNotDeliveredWhereNoBridgeRunsIt()
    {
        var api = new Api();

        foreach (var session in new[]
                 {
                     Headless(api, new Credentials(), Row(bridge: false)),
                     Desktop(api, new Credentials(), ""),
                 })
        {
            Assert.Contains(session.SlashCommands, c => c.Name == "/think" && c.Route == SlashRouteKind.Unavailable);

            Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/think 8000"));

            Assert.Contains(session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
            Assert.DoesNotContain(session.History, t => t.Text.Contains("Handed to", StringComparison.Ordinal));
        }

        Assert.Empty(api.Requests);
    }
}
