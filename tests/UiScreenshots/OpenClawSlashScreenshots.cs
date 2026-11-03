using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// CB-306: an OpenClaw panel's slash box, and the card the gateway answers a
// context-free command with. Both from the real structure measured on openclaw
// 2026.10.10 (OpenClawSlashFixtures), scrubbed of ids and account names.
//
// The first picture is autocomplete from the gateway's own catalogue on a device
// without operator.admin, so /reset is not in it. The second is /status already
// run: the user's line and the gateway's card as an ordinary assistant bubble.
[Collection("Settings")]
public class OpenClawSlashScreenshots : IDisposable
{
    private const string Key = "agent:main:dashboard:slash";
    private readonly string _sessionId = "openclaw:" + Key;

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        ChatPanel.HideFor(_sessionId);
        OpenClawSessions.SetCommandsForTests(OpenClawSessions.CommandCatalogue.None);
    }

    private OpenClawChatSession Chat(bool withHistory)
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(
            OpenClawCommandCatalog.Parse(JsonDocument.Parse(OpenClawSlashFixtures.CommandsList).RootElement),
            new[] { "operator.read", "operator.write" }));

        var chat = new OpenClawChatSession(_sessionId, Key, "Main") { HasMore = false };
        if (withHistory)
        {
            var messages = JsonDocument.Parse(OpenClawSlashFixtures.StatusCardHistory).RootElement.GetProperty("messages");
            chat.SetHistory(OpenClawSessions.TurnsFromHistory(messages, Key));
        }
        return chat;
    }

    [AvaloniaFact]
    public void TheSlashBoxOffersTheGatewaysCommandsWithoutReset()
    {
        ChatPanel.OpenFor(NewOrb(), Chat(withHistory: false));
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        input.Focus();
        ScreenshotHelper.Flush();
        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "/" });
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-openclaw-slash-box.png");
    }

    [AvaloniaFact]
    public void AStatusCommandsCardIsDrawnAsTheAgentsReply()
    {
        ChatPanel.OpenFor(NewOrb(), Chat(withHistory: true));
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, "chat-panel-openclaw-status-card.png");
    }
}
