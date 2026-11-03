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
                    features = new { methods = new[] { "chat.send", "commands.list" } },
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

    private (OpenClawChatSession Chat, ChatPanel Panel) OpenPanel()
    {
        OrbweaverSettings.OpenClawReplyEnabled = true;
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(
            OpenClawCommandCatalog.Parse(JsonDocument.Parse(OpenClawSlashFixtures.CommandsList).RootElement), Write));

        var chat = new OpenClawChatSession(_sessionId, SessionKey, "Agent");
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

    private static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until)
        {
            Flush();
            Thread.Sleep(10);
        }
        Flush();
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
}
