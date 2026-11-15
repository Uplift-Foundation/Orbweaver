using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Xunit;

namespace Orbweaver.Tests;

// CB-289. A reply with a table wider than the bubble, drawn through real Skia:
// one capture per shape, and one with a wrapping code block beside a scrolling
// table so wrap-versus-scroll reads in a single image. Same singleton-cleanup
// rule as ChatPanelScreenshots: ChatPanel is one window shared by the process.
public class ChatTableScreenshots : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    private FakeChatSession NewFake(string text)
    {
        var id = "screenshot-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = text } })
        {
            SessionId = id,
            DisplayName = "Table Session"
        };
    }

    // Deliberately never closed — see ChatPanelScreenshots.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private void Capture(string text, string file)
    {
        ChatPanel.OpenFor(NewOrb(), NewFake(text));
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, file);
    }

    [AvaloniaFact]
    public void AWidePipeTableScrollsInsteadOfWrapping() =>
        Capture(
            "Here is the comparison:\n\n"
            + "| Machine | Role | Notes |\n|---|---|---|\n"
            + "| MacBook | signing and daily use | has the Developer ID identity installed |\n"
            + "| Mac mini | headless, unattended | runs under launchd with nobody looking |\n"
            + "| Windows PC | installer and Fluent theme | the only real Windows install |\n\n"
            + "That is all.",
            "chat-table-pipe-wide.png");

    [AvaloniaFact]
    public void AWideBoxTableScrollsInsteadOfWrapping() =>
        Capture(
            "```\n" + ChatTableFixtures.Join(ChatTableFixtures.BoxUpdateStatus) + "\n```",
            "chat-table-box-wide.png");

    [AvaloniaFact]
    public void AWrappingCodeBlockAndAScrollingTableSideBySide() =>
        Capture(
            "Code wraps:\n\n```csharp\nvar a = SomethingVeryLongIndeed(withSeveralArguments, andMoreArguments, andEvenMoreArguments);\n```\n\n"
            + "A table scrolls:\n\n" + ChatTableFixtures.Join(ChatTableFixtures.AsciiSqlite)
            + "\n\nand a wide one:\n\n" + ChatTableFixtures.Join(ChatTableFixtures.BoxUpdateStatus),
            "chat-table-wrap-vs-scroll.png");

    // Real Skia only, which is why this is here and not in UiTests: the null
    // renderer measures every glyph the same, so it cannot show a font stack
    // falling back to a proportional face for the box-drawing block. Menlo on
    // macOS and Consolas on Windows are expected to carry these glyphs at the
    // same advance as a letter; if one did not, the table would misalign in
    // exactly the way this ticket is about, whatever the parser does.
    [AvaloniaFact]
    public void BoxDrawingGlyphsAreTheSameWidthAsLettersInTheChatMonospaceStack()
    {
        double Width(string text) =>
            new TextLayout(text, new Typeface(ChatPanel.MonoFamily), 12, Brushes.Black)
                .WidthIncludingTrailingWhitespace;

        var box = Width("┌─┬┐│├┼┤└┴┘");
        var letters = Width("abcdefghijk");
        var dot = Width("·");
        var letter = Width("a");

        Assert.InRange(box, letters - 0.5, letters + 0.5);
        Assert.InRange(dot, letter - 0.5, letter + 0.5);
    }
}
