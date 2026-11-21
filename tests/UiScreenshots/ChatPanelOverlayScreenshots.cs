using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-400: how the /mobile overlay looks in the panel. The prompt is not hand-built:
// it is what the real parser makes of the real capture (PaneFixtures.MobileOverlay80x50,
// a tmux capture-pane of Claude Code 2.1.330), so the picture is what production draws.
// This suite draws through real Skia rather than the null renderer, which is the only
// place the QR's half-block glyphs get a real font — whether the code is a code is a
// question about these pixels, and a QR decoder is the instrument that answers it.
public class ChatPanelOverlayScreenshots : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static IEnumerable<ChatTurn> Context() => new[]
    {
        new ChatTurn { Role = ChatRole.User, Mine = true, Text = "Can you get that on my phone?" },
        new ChatTurn { Role = ChatRole.Assistant, Text = "Run /mobile and it shows a code to scan." },
    };

    private static ChatPrompt Overlay() => ChatTranscript.ParseDialog(PaneFixtures.MobileOverlay80x50)!;

    // tall: the overlay's card fills the default-height panel and pushes the transcript
    // out of view, which would hide the very note this picture is of, so that case
    // is drawn on a taller panel and the shared window is put back afterwards.
    private void Capture(string name, IEnumerable<ChatTurn> history, bool tall = false)
    {
        var id = "screenshot-overlay-" + Guid.NewGuid();
        _toClean.Add(id);
        var fake = new FakeChatSession(history) { SessionId = id, DisplayName = "claude-buddy", Prompt = Overlay() };

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var height = panel.Height;
        try
        {
            if (tall)
            {
                panel.Height = 680;
                ScreenshotHelper.Flush();
            }

            ScreenshotHelper.CaptureAlreadyShown(panel, name);
        }
        finally
        {
            panel.Height = height;
            ChatPanel.HideFor(id);
        }
    }

    // The overlay as the panel shows it: its caption, the code on a white card, and Close.
    [AvaloniaFact]
    public void TheOverlayWithItsQrCodeAndCloseButton() =>
        Capture("chat-panel-overlay-qr.png", Context());

    // Tall real screens, each read through the real reader (title from under the top edge, body
    // from there down, the button labelled by the footer's own verb), bounded, with the button
    // still on screen under them.
    [AvaloniaFact]
    public void ATallScreenIsBoundedAndKeepsCloseVisible() =>
        CaptureTall(PaneFixtures.McpList, "chat-panel-overlay-tall.png");

    [AvaloniaFact]
    public void AHelpScreenIsBoundedAndKeepsCloseVisible() =>
        CaptureTall(PaneFixtures.OtherHelpScreen, "chat-panel-overlay-tall-help.png");

    // The second level of /mcp: its footer says Esc to go back, and so does the button.
    [AvaloniaFact]
    public void TheMcpDetailPageOffersGoBack() =>
        CaptureTall(PaneFixtures.McpDetail, "chat-panel-overlay-mcp-detail.png");

    private void CaptureTall(string screen, string name)
    {
        var id = "screenshot-overlay-tall-" + Guid.NewGuid();
        _toClean.Add(id);
        var prompt = ChatTranscript.ParseDialog(screen)!;
        var fake = new FakeChatSession(Context()) { SessionId = id, DisplayName = "claude-buddy", Prompt = prompt };

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, name);
        ChatPanel.HideFor(id);
    }

    // The same, after a send was refused because of it: the note says the message was
    // not sent and is still in the box, in the sentence the panel really writes.
    [AvaloniaFact]
    public void TheOverlayWithTheNotSentNoteUnderTheComposer()
    {
        var note = new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = LocalCliChatSession.NotAtPromptNote(TypingGate.OverlayWhy(Overlay().Title)),
        };

        Capture("chat-panel-overlay-not-sent.png", Context().Append(note), tall: true);
    }
}
