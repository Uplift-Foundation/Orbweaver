using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-306, OpenClaw arm, through a real panel: a real OpenClawChatSession over an
// in-memory gateway, the real catalogue shape, and the keys a person would press.
// Autocomplete lists the gateway's commands and not /reset (this device was not
// paired with operator.admin), accepting one and sending it reaches chat.send as
// the typed text, and /reset typed in full is refused with a note in the panel
// and nothing sent.
[Collection("Settings")]
public class ChatPanelOpenClawSlashTests : IDisposable
{
    private const string SessionKey = "agent:main:dashboard:slash";
    private static readonly string[] Write = { "operator.read", "operator.write" };

    private static void Flush() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private readonly string _sessionId = "openclaw:" + SessionKey;

    public void Dispose()
    {
        ChatPanel.HideFor(_sessionId);
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.SetCommandsForTests(OpenClawSessions.CommandCatalogue.None);
        OrbweaverSettings.OpenClawReplyEnabled = false;
    }

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "2026.10.10" },
                    auth = new { scopes = Write },
                    features = new { methods = new[] { "chat.send", "commands.list", "send" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576L }
                })
                : FakeGatewaySocket.Ok(request.Id, new { runId = "r1", status = "started" });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);
        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private (OpenClawChatSession Chat, ChatPanel Panel) OpenPanel(OpenClawSessions.Delivery? delivery = null)
    {
        OrbweaverSettings.OpenClawReplyEnabled = true;
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(
            OpenClawCommandCatalog.Parse(JsonDocument.Parse(OpenClawSlashFixtures.CommandsList).RootElement), Write));

        var chat = new OpenClawChatSession(_sessionId, SessionKey, "Agent") { Delivery = delivery };
        ChatPanel.OpenFor(NewOrb(), chat);
        Flush();
        return (chat, ChatPanelTestAccess.Instance!);
    }

    private static void Press(TextBox input, Key key)
    {
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Flush();
    }

    private static void Type(TextBox input, string text)
    {
        input.Focus();
        Flush();
        input.Text = "";
        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        Flush();
    }

    private static List<string?> Offered(ChatPanel panel) =>
        ((System.Collections.IEnumerable)panel.FindControl<ItemsControl>("SlashList")!.ItemsSource!)
        .Cast<object>()
        .Select(v => v.GetType().GetProperty("Name")!.GetValue(v) as string)
        .ToList();

    // Fails, naming what it was waiting for, when the condition does not come
    // true. It used to return quietly after the five seconds and let the test
    // assert on whatever state it was in, which is how CB-376 read as a
    // collections mismatch rather than "the second send never arrived".
    private static void WaitFor(Func<bool> done,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string? what = null)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until)
        {
            Flush();
            Thread.Sleep(10);
        }
        Flush();
        Assert.True(done(), "timed out waiting for: " + what);
    }

    [AvaloniaFact]
    public void TheSlashBoxListsTheGatewaysCommandsAndNotReset()
    {
        var (_, panel) = OpenPanel();
        var input = panel.FindControl<TextBox>("Input")!;

        Type(input, "/");

        var names = Offered(panel);
        Assert.Contains("/status", names);
        Assert.Contains("/model", names);
        Assert.Contains("/add_model_provider", names);
        Assert.DoesNotContain("/reset", names);

        // Typing its own name offers nothing: /reset is the only command in the
        // list that starts "/res", so with it filtered the box closes rather than
        // advertising a command the panel would refuse.
        Type(input, "/res");
        Assert.False(panel.FindControl<Control>("SlashBox")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task AcceptingACommandAndSendingItReachesChatSendAsTheTypedText()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (_, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;

            Type(input, "/sta");
            Press(input, Key.Tab);
            Assert.StartsWith("/status", input.Text);

            Press(input, Key.Enter);

            WaitFor(() => socket.Requests.Any(r => r.Method == "chat.send"));
            var send = Assert.Single(socket.Requests, r => r.Method == "chat.send");
            Assert.Equal("/status", send.Params.GetProperty("message").GetString());
            WaitFor(() => input.Text == "");
            Assert.Equal("", input.Text);
        }
    }

    [AvaloniaFact]
    public async Task ResetTypedInFullIsRefusedWithANoteAndNothingIsSent()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (chat, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;

            Type(input, "/reset");
            Press(input, Key.Enter);
            WaitFor(() => chat.History.Any(t => t.Role == ChatRole.System));

            Assert.DoesNotContain(socket.Requests, r => r.Method == "chat.send");
            Assert.Equal("/reset", input.Text);
            Assert.Contains("operator.admin", chat.History.Single(t => t.Role == ChatRole.System).Text);
        }
    }

    // An alias completes to the command's own name; typing the whole alias closes
    // the box, so Enter sends it rather than accepting it into something else.
    [AvaloniaFact]
    public void AnAliasCompletesToTheCommandAndATypedAliasClosesTheBox()
    {
        var (_, panel) = OpenPanel();
        var input = panel.FindControl<TextBox>("Input")!;

        Type(input, "/thinki");
        Assert.Equal(new List<string?> { "/think" }, Offered(panel));
        Press(input, Key.Tab);
        Assert.StartsWith("/think ", input.Text);

        Type(input, "/thinking");
        Assert.False(panel.FindControl<Control>("SlashBox")!.IsVisible);
    }

    // The panel's known-name gate counts a command's aliases, so /t typed in full
    // is routed as the command it is; a path is a message and the runner never
    // sees it (no note, and the text goes out as typed).
    //
    // What tells the two routes apart on the wire is the mirror: on a
    // conversation that lives in a chat channel, a message is first posted there
    // ("send") and then asked of the agent ("chat.send"), while a recognised
    // command is only ever the second (CB-306). So the session here has a
    // delivery address, and the assertion is the order of the calls: a line
    // routed the wrong way changes it. Both lines reaching chat.send, which is
    // all this used to check, is true whichever way they were routed.
    [AvaloniaFact]
    public async Task ATypedAliasIsRoutedAsACommandAndAPathAsAMessage()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (chat, panel) = OpenPanel(new OpenClawSessions.Delivery("discord", "user:2000000000000000001", "quillbot"));
            var input = panel.FindControl<TextBox>("Input")!;
            List<string> Wire() => socket.Requests
                .Where(r => r.Method is "send" or "chat.send").Select(r => r.Method).ToList();

            Type(input, "/t high");
            Press(input, Key.Enter);
            WaitFor(() => Wire().Count == 1);
            // The send's own clearing of the box comes after the request is
            // recorded; typing before it ran is the race CB-376 was.
            WaitFor(() => input.Text == "");
            Assert.Equal(new[] { "chat.send" }, Wire());   // the alias: a command, not mirrored

            Type(input, "/tmp/build.log has the error");
            Press(input, Key.Enter);
            WaitFor(() => Wire().Count == 3);

            Assert.Equal(new[] { "chat.send", "send", "chat.send" }, Wire());   // the path: mirrored, then sent
            Assert.Equal(new[] { "/t high", "/tmp/build.log has the error" },
                socket.Requests.Where(r => r.Method == "chat.send")
                    .Select(r => r.Params.GetProperty("message").GetString()));
            Assert.DoesNotContain(chat.History, t => t.Role == ChatRole.System);
        }
    }

    // CB-376: the reply to a send can arrive after the person has started the
    // next message. The gateway's reply is held here, the next line typed, and
    // only then let through.
    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway, Action ReleaseReply)> HeldReplyAsync()
    {
        var (socket, gateway) = await ConnectedAsync();
        var original = socket.OnRequest!;
        var hold = true;
        socket.OnRequest = r => r.Method == "chat.send" && hold ? null : original(r);
        return (socket, gateway, () =>
        {
            hold = false;
            socket.PushJson(JsonSerializer.Serialize(FakeGatewaySocket.Ok(
                socket.Requests.Last(r => r.Method == "chat.send").Id, new { runId = "r1", status = "started" })));
        });
    }

    [AvaloniaFact]
    public async Task ALateReplyDoesNotWipeTheNextMessageTypedWhileItWasInFlight()
    {
        var (socket, gateway, release) = await HeldReplyAsync();
        using (gateway)
        {
            var (_, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;

            // The panel is reused between tests, so its count does not start at 0.
            var done = panel.CompletedSendsForTests;
            Type(input, "/t high");
            Press(input, Key.Enter);
            WaitFor(() => socket.Requests.Count(r => r.Method == "chat.send") == 1);

            Type(input, "/tmp/build.log has the error");
            release();
            WaitFor(() => panel.CompletedSendsForTests == done + 1);
            Assert.Equal("/tmp/build.log has the error", input.Text);

            Press(input, Key.Enter);
            WaitFor(() => socket.Requests.Count(r => r.Method == "chat.send") == 2);
        }
    }

    // CB-387: the reply to a send can land after the person has gone to another
    // conversation. Back in the first, the box must not offer the sent text as
    // unsent (a duplicate send) when the send went, and must keep it when the
    // send failed (CB-35). The other conversation's draft is not touched.
    private async Task<(string InA, string InB)> SwitchAwayBeforeReplyAsync(bool replySucceeds)
    {
        var (socket, gateway, release) = await HeldReplyAsync();
        using (gateway)
        {
            var (chat, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;
            var orb = NewOrb();

            var done = panel.CompletedSendsForTests;
            Type(input, "please run the build");
            Press(input, Key.Enter);
            WaitFor(() => socket.Requests.Count(r => r.Method == "chat.send") == 1);

            var other = new OpenClawChatSession("openclaw:" + SessionKey + ":b", SessionKey + ":b", "Other");
            ChatPanel.OpenFor(orb, other);
            Flush();
            Type(input, "a draft for the other one");

            if (replySucceeds) release();
            else
            {
                var id = socket.Requests.Last(r => r.Method == "chat.send").Id;
                socket.PushJson(JsonSerializer.Serialize(
                    FakeGatewaySocket.Error(id, "UNAVAILABLE", "gateway down", null)));
            }
            WaitFor(() => panel.CompletedSendsForTests == done + 1);
            Assert.Equal("a draft for the other one", input.Text);

            ChatPanel.OpenFor(orb, chat);
            Flush();
            var inA = input.Text ?? "";
            ChatPanel.OpenFor(orb, other);
            Flush();
            return (inA, input.Text ?? "");
        }
    }

    [AvaloniaFact]
    public async Task ASendThatWentLeavesNoDraftBehindWhenTheReplyLandsAfterASwitch()
    {
        var (inA, inB) = await SwitchAwayBeforeReplyAsync(replySucceeds: true);

        Assert.Equal("", inA);
        Assert.Equal("a draft for the other one", inB);
    }

    [AvaloniaFact]
    public async Task ASendThatFailedKeepsItsTextWhenTheReplyLandsAfterASwitch()
    {
        var (inA, inB) = await SwitchAwayBeforeReplyAsync(replySucceeds: false);

        Assert.Equal("please run the build", inA);
        Assert.Equal("a draft for the other one", inB);
    }

    [AvaloniaFact]
    public async Task AnEditThatRestatesTheSentTextStillSurvivesTheReply()
    {
        var (socket, gateway, release) = await HeldReplyAsync();
        using (gateway)
        {
            var (_, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;

            var done = panel.CompletedSendsForTests;
            Type(input, "/t high");
            Press(input, Key.Enter);
            WaitFor(() => socket.Requests.Count(r => r.Method == "chat.send") == 1);

            // The box holds "/t high" already, and setting a property to the value
            // it has raises no change. This is an edit only because Type empties
            // the box first and then enters the text (two real changes); if Type
            // ever stops doing that, this stops testing the restated text.
            Type(input, "/t high");
            release();
            WaitFor(() => panel.CompletedSendsForTests == done + 1);
            Assert.Equal("/t high", input.Text);
        }
    }

    [AvaloniaFact]
    public async Task WithNoEditTheReplyClearsTheBox()
    {
        var (socket, gateway, release) = await HeldReplyAsync();
        using (gateway)
        {
            var (_, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;

            Type(input, "/t high");
            Press(input, Key.Enter);
            WaitFor(() => socket.Requests.Count(r => r.Method == "chat.send") == 1);
            Assert.Equal("/t high", input.Text);

            release();
            WaitFor(() => input.Text == "");
        }
    }

    // Avalonia raises TextBox.TextChanged after the fact (it is posted), while
    // the property change is synchronous. The count of edits must follow the
    // property: text set and sent before the event has run is the text that was
    // sent, not an edit made during the send, and the box has to clear.
    [AvaloniaFact]
    public async Task TextSentBeforeItsTextChangedEventHasRunStillClearsTheBox()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (_, panel) = OpenPanel();
            var input = panel.FindControl<TextBox>("Input")!;
            input.Focus();
            Flush();

            input.Text = "/tmp/build.log has the error";   // no Flush: TextChanged is still queued
            Press(input, Key.Enter);

            WaitFor(() => socket.Requests.Count(r => r.Method == "chat.send") == 1);
            WaitFor(() => input.Text == "");
        }
    }

    // The panel's own gate, alone: what it hands to the runner. A gate that
    // accepted a path would be absorbed by the runner (which re-checks the
    // catalogue), so nothing else notices. The runner and the rule are pinned on
    // their own by OpenClawSlashCommandsTests and SlashRouteTests.
    [AvaloniaFact]
    public void TheGateKnowsAnAliasAndNotAPathAndNothingWithoutACatalogue()
    {
        var (_, panel) = OpenPanel();

        Assert.True(panel.IsKnownSlashCommand("/t high"));
        Assert.False(panel.IsKnownSlashCommand("/tmp/build.log has the error"));

        OpenClawSessions.SetCommandsForTests(OpenClawSessions.CommandCatalogue.None);

        Assert.False(panel.IsKnownSlashCommand("/t high"));
        Assert.False(panel.IsKnownSlashCommand("/tmp/build.log has the error"));
    }

    [AvaloniaTheory]
    [InlineData(ChatSendOutcome.Sent, false, true)]
    [InlineData(ChatSendOutcome.Sent, true, false)]
    [InlineData(ChatSendOutcome.Failed, false, false)]
    [InlineData(ChatSendOutcome.Failed, true, false)]
    public void TheBoxIsClearedOnlyForASendThatWentAndWasNotEdited(ChatSendOutcome outcome, bool edited, bool clears) =>
        Assert.Equal(clears, ChatPanel.ShouldClearComposerAfterSend(outcome, edited));
}
