using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-363: what a file link looks like in a bubble and what sits under it while
// it is being fetched or after it was refused. Whether the link reads as a link
// and the note reads as a note of the bubble's own, on both renderers, is a
// judgement no assertion can make.
//
// One peer panel with three bubbles, one link each: left idle, held mid-fetch,
// and refused. A second capture is a local session where a link to a folder was
// clicked, which is shown rather than opened. Each state is reached by a real
// click on the link's own text, with the opener replaced by a no-op recorder.
[Collection("Settings")]
public class ChatPanelFileLinkScreenshots : IDisposable
{
    private readonly List<string> _panelsToClean = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb363-shot-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, bool> _previousOpener = ChatLinks.OpenFile;
    private readonly Func<string, bool> _previousReveal = ChatLinks.RevealFile;

    public ChatPanelFileLinkScreenshots()
    {
        Directory.CreateDirectory(_dir);
        ChatLinks.OpenFile = _ => true;
        ChatLinks.RevealFile = _ => true;
    }

    public void Dispose()
    {
        ChatLinks.OpenFile = _previousOpener;
        ChatLinks.RevealFile = _previousReveal;
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static ChatTurn Say(string text, int minute) =>
        new() { Role = ChatRole.Assistant, Text = text, IsComplete = true, At = T0.AddMinutes(minute) };

    private ChatPanel Open(string name, Func<string, FakeChatSession> build, out FakeChatSession fake)
    {
        var id = "screenshot-filelink-" + name + "-" + Guid.NewGuid();
        _panelsToClean.Add(id);
        fake = build(id);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();

        var panel = ChatPanelTestAccess.Instance!;
        panel.Width = 460;
        panel.Height = 560;
        ScreenshotHelper.Flush();
        return panel;
    }

    private static string Rendered(TextBlock block) =>
        !string.IsNullOrEmpty(block.Text)
            ? block.Text!
            : string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []);

    private static void ClickLink(ChatPanel panel, string label)
    {
        var blocks = panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        var block = blocks.FirstOrDefault(b => Rendered(b).Contains(label, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("no block says " + label + "; blocks: " + string.Join(" | ", blocks.Select(Rendered)));
        var index = Rendered(block).IndexOf(label, StringComparison.Ordinal) + 2;
        var rect = block.TextLayout.HitTestTextRange(index, 1).First();
        var local = new Point(block.Padding.Left + rect.X + rect.Width / 2, block.Padding.Top + rect.Y + rect.Height / 2);
        var point = block.TranslatePoint(local, panel)!.Value;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        block.RaiseEvent(new PointerPressedEventArgs(
            block, pointer, panel, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        ScreenshotHelper.Flush();
        block.RaiseEvent(new PointerReleasedEventArgs(
            block, pointer, panel, point, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        ScreenshotHelper.Flush();
    }

    private static bool NoteShown(ChatPanel panel, string text) =>
        panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<TextBlock>()
            .Any(b => b.IsVisible && b.Text == text);

    [AvaloniaFact]
    public void APeersBubblesIdleFetchingAndRefused()
    {
        var gated = new TaskCompletionSource<bool>();
        var panel = Open("peer", id => new FakeChatSession(new[]
        {
            Say("Here are the files:\n\n- [Response](vscode://file//Users/warren/Documents/Evidence/1_Response.pdf)", 0),
            Say("And the motion: [Stoel's motion](vscode://file/Users/warren/Documents/Evidence/Motion%20for%20Extension%20(env14901649).pdf)", 1),
            Say("Plus a private file: [vault.pdf](file:///Users/warren/Library/Secrets/vault.pdf)", 2),
        })
        {
            SessionId = id,
            DisplayName = "evidence-5d",
            FileHost = ChatFileHost.Peer,
            FileHostName = "Mac mini",
            FileAnswer = path => path.Contains("Secrets")
                ? ChatFileResult.Refused("forbidden_path")
                : ChatFileResult.Refused("failed"),
        }, out var fake);

        // The refused one first, while nothing is gated.
        ClickLink(panel, "vault.pdf");
        Assert.True(NoteShown(panel, "Mac mini doesn't share that folder."));

        // Then the fetch that is held open at 42%.
        fake.FileGate = gated;
        fake.FileProgress = (42, 100);
        ClickLink(panel, "Stoel's motion");
        Assert.True(NoteShown(panel, "Fetching Motion for Extension (env14901649).pdf from Mac mini… 42%"));

        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-file-links-peer.png");
        gated.SetResult(true);
    }

    [AvaloniaFact]
    public void ALocalFolderLinkIsShownNotOpened()
    {
        var panel = Open("local", id => new FakeChatSession(new[]
        {
            Say($"The scans are in [this folder]({new Uri(_dir).AbsoluteUri}).", 0),
        })
        {
            SessionId = id,
            DisplayName = "evidence-5d",
        }, out _);

        ClickLink(panel, "this folder");

        Assert.True(NoteShown(panel, "Folders are shown, not opened."));
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-file-links-local.png");
    }
}
