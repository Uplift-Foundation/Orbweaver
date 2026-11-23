using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-412: /login in the panel, drawn through real Skia: the picker as buttons, the sign-in link with
// its code field, the outcome row under the time-limit turn, and the route that cannot be pressed.
public class ChatPanelLoginScreenshots : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static IEnumerable<ChatTurn> Context() => new[]
    {
        new ChatTurn { Role = ChatRole.User, Mine = true, Text = "/login" },
    };

    private void Capture(string name, ChatPrompt prompt, IEnumerable<ChatTurn>? history = null)
    {
        var id = "screenshot-login-" + Guid.NewGuid();
        _toClean.Add(id);
        var fake = new FakeChatSession(history ?? Context()) { SessionId = id, DisplayName = "orbweaver", Prompt = prompt };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();
        try { ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, name); }
        finally { ChatPanel.HideFor(id); }
    }

    private const string Url = "https://claude.ai/oauth/authorize?code=true&client_id=9d1c250a&response_type=code";

    [AvaloniaFact]
    public void ThePickerAsButtons() =>
        Capture("chat-panel-login-picker.png", new ChatPrompt("Select login method:", new[]
        {
            new ChatPromptOption("1", "Claude account with subscription"),
            new ChatPromptOption("2", "Anthropic Console account"),
            new ChatPromptOption("3", "3rd-party platform"),
        }));

    [AvaloniaFact]
    public void TheSignInLinkAndCodeField() =>
        Capture("chat-panel-login-url.png",
            new ChatPrompt("Sign in in your browser, then paste the code here", Array.Empty<ChatPromptOption>(),
                Link: Url, CodeEntry: true));

    [AvaloniaFact]
    public void TheOutcomeRowUnderTheTimeLimitTurn() =>
        Capture("chat-panel-login-outcome.png",
            new ChatPrompt("OAuth error: That code wasn't accepted", new[] { new ChatPromptOption("Enter", "Try again") }),
            new[]
            {
                new ChatTurn { Role = ChatRole.User, Mine = true, Text = "/login" },
                new ChatTurn { Role = ChatRole.System, Text = "Login reached its time limit" },
                new ChatTurn { Role = ChatRole.System, Text = "Login interrupted" },
            });

    // Terminal.app: readable, not pressable. The link and the terminal, no field and no buttons.
    [AvaloniaFact]
    public void AnUnpressableRouteShowsTheLinkAndTheTerminalOnly() =>
        Capture("chat-panel-login-fallback.png",
            new ChatPrompt("Sign in in your browser", Array.Empty<ChatPromptOption>(), Link: Url));
}
