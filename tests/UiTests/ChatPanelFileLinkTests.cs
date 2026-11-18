using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// Clicking a file link in a chat bubble (CB-363).
//
// Same method as ChatPanelLinkTests: pointer events raised on the bubble block,
// the point worked out from its own text layout. The opener is the recorder
// swapped into ChatLinks.OpenFile, so nothing is ever started; the files are
// real temp files, because the policy decides on what is on disk and a test
// that faked that would prove the fake. What is NOT asserted: that a real
// application or Finder window opens (the launcher is excluded from coverage
// for that reason, and the real run belongs to the E2E), or that the tooltip
// popup draws.
[Collection("Settings")]
public class ChatPanelFileLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb363-ui-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _toClean = new();
    private readonly List<string> _opened = new();
    private readonly List<string> _web = new();
    private readonly List<string> _revealed = new();
    private readonly Func<string, bool> _previousReveal = ChatLinks.RevealFile;
    private readonly Func<string, bool> _previousFile;
    private readonly Func<string, bool> _previousWeb;

    public ChatPanelFileLinkTests()
    {
        Directory.CreateDirectory(_dir);
        _previousFile = ChatLinks.OpenFile;
        _previousWeb = ChatLinks.Open;
        ChatLinks.OpenFile = path => { _opened.Add(path); return true; };
        ChatLinks.Open = url => { _web.Add(url); return true; };
        ChatLinks.RevealFile = path => { _revealed.Add(path); return true; };
    }

    public void Dispose()
    {
        ChatLinks.OpenFile = _previousFile;
        ChatLinks.RevealFile = _previousReveal;
        ChatLinks.Open = _previousWeb;
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string File(string name = "report.pdf")
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, "%PDF");
        return path;
    }

    // file:///… for either OS's absolute path — the form that survives Windows
    // backslashes and drive letters without a Markdown escape.
    private static string Url(string path) => new Uri(path).AbsoluteUri;

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private (ChatPanel Panel, FakeChatSession Fake, ChatTurn Turn) Open(
        string text, Action<FakeChatSession>? arrange = null)
    {
        var id = "filelinks-" + Guid.NewGuid();
        _toClean.Add(id);
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = text, IsComplete = true };
        var fake = new FakeChatSession(new[] { turn }) { SessionId = id, DisplayName = "File Session" };
        arrange?.Invoke(fake);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        Flush();
        return (ChatPanelTestAccess.Instance!, fake, turn);
    }

    private static string Rendered(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text!;
        if (block.Inlines is null) return "";
        return string.Concat(block.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }

    private static SelectableTextBlock BlockSaying(ChatPanel panel, string text) =>
        panel.FindControl<ItemsControl>("Turns")!
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(b => Rendered(b).Contains(text, StringComparison.Ordinal));

    private static Point At(ChatPanel panel, SelectableTextBlock block, string text, int offset = 1)
    {
        var index = Rendered(block).IndexOf(text, StringComparison.Ordinal) + offset;
        var rect = block.TextLayout.HitTestTextRange(index, 1).First();
        var local = new Point(
            block.Padding.Left + rect.X + rect.Width * 0.5,
            block.Padding.Top + rect.Y + rect.Height / 2);
        return block.TranslatePoint(local, panel)!.Value;
    }

    private sealed class Mouse(ChatPanel panel, SelectableTextBlock block)
    {
        private readonly Pointer _pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        public void Click(Point p)
        {
            block.RaiseEvent(new PointerPressedEventArgs(
                block, _pointer, panel, p, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            Dispatcher.UIThread.RunJobs();
            block.RaiseEvent(new PointerReleasedEventArgs(
                block, _pointer, panel, p, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
            Dispatcher.UIThread.RunJobs();
        }

        public void Hover(Point p)
        {
            block.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, block, _pointer, panel, p, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static TextBlock? NoteRow(ChatPanel panel, string text) =>
        panel.FindControl<ItemsControl>("Turns")!
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(b => b.Text == text && b.IsVisible);

    // --- local ---

    [AvaloniaFact]
    public void AClickOnALocalFileLinkOpensItWithoutTouchingTheBrowser()
    {
        var path = File();
        var (panel, fake, turn) = Open($"here is [the report]({Url(path)}) for you");
        var block = BlockSaying(panel, "the report");

        new Mouse(panel, block).Click(At(panel, block, "the report", 3));

        Assert.Equal(new[] { path }, _opened);
        Assert.Equal(new[] { path }, fake.FetchedFiles);
        Assert.Empty(_web);
        Assert.Null(turn.FileNote);
    }

    [AvaloniaFact]
    public void AClickOnAWebLinkStillGoesToTheBrowserAndNeverToTheFileOpener_Control()
    {
        var (panel, fake, _) = Open("see [docs](https://example.com/a) and [file](" + Url(File()) + ")");
        var block = BlockSaying(panel, "docs");

        new Mouse(panel, block).Click(At(panel, block, "docs", 1));

        Assert.Equal(new[] { "https://example.com/a" }, _web);
        Assert.Empty(_opened);
        Assert.Empty(fake.FetchedFiles);
    }

    [AvaloniaFact]
    public void AMissingLocalFileSaysSoAndOpensNothing()
    {
        var gone = Path.Combine(_dir, "gone.pdf");
        var (panel, _, turn) = Open($"[lost]({Url(gone)})");
        var block = BlockSaying(panel, "lost");

        new Mouse(panel, block).Click(At(panel, block, "lost", 1));
        Flush();

        Assert.Empty(_opened);
        Assert.Equal("That file does not exist.", turn.FileNote);
        Assert.NotNull(NoteRow(panel, "That file does not exist."));
    }

    [AvaloniaFact]
    public void AFolderLinkIsShownNotOpenedAndSaysWhy()
    {
        var (panel, _, turn) = Open($"[the folder]({Url(_dir)})");
        var block = BlockSaying(panel, "the folder");

        new Mouse(panel, block).Click(At(panel, block, "the folder", 2));

        Assert.Equal(new[] { _dir }, _revealed);
        Assert.Empty(_opened);
        Assert.Equal("Folders are shown, not opened.", turn.FileNote);
    }

    // --- remote ---

    [AvaloniaFact]
    public async Task AClickOnAPeersFileShowsFetchingThenOpensTheCachedCopy()
    {
        var cached = File("cached.pdf");
        var gate = new TaskCompletionSource<bool>();
        var (panel, fake, turn) = Open("[the brief](/Users/a/Docs/brief.pdf)", f =>
        {
            f.FileHost = ChatFileHost.Peer;
            f.FileHostName = "Mac Mini";
            f.FileProgress = (42, 100);
            f.FileGate = gate;
            f.FileAnswer = _ => ChatFileResult.Cached(cached, executable: false);
        });
        var block = BlockSaying(panel, "the brief");

        new Mouse(panel, block).Click(At(panel, block, "the brief", 2));
        await Until(() => turn.FileNote == "Fetching brief.pdf from Mac Mini… 42%");

        Assert.Equal("Fetching brief.pdf from Mac Mini… 42%", turn.FileNote);
        Assert.NotNull(NoteRow(panel, "Fetching brief.pdf from Mac Mini… 42%"));
        Assert.Empty(_opened);

        gate.SetResult(true);
        await Until(() => _opened.Count == 1);

        Assert.Equal(new[] { cached }, _opened);
        Assert.Equal(new[] { "/Users/a/Docs/brief.pdf" }, fake.FetchedFiles);
        await Until(() => turn.FileNote is null);
        Assert.Null(turn.FileNote);
    }

    [AvaloniaTheory]
    [InlineData("forbidden_path", "Mac Mini doesn't share that folder.")]
    [InlineData("update", "Update Orbweaver on Mac Mini to open files from it.")]
    [InlineData("too_large", "That file is too large to open from here (50 MB limit).")]
    public void APeersRefusalIsShownUnderTheBubbleAndNothingOpens(string code, string expected)
    {
        var (panel, _, turn) = Open("[the brief](/Users/a/Docs/brief.pdf)", f =>
        {
            f.FileHost = ChatFileHost.Peer;
            f.FileHostName = "Mac Mini";
            f.FileAnswer = _ => ChatFileResult.Refused(code);
        });
        var block = BlockSaying(panel, "the brief");

        new Mouse(panel, block).Click(At(panel, block, "the brief", 2));
        Flush();

        Assert.Empty(_opened);
        Assert.Equal(expected, turn.FileNote);
        Assert.NotNull(NoteRow(panel, expected));
    }

    [AvaloniaFact]
    public async Task ASecondClickWhileTheFirstIsFetchingStartsNoSecondFetch()
    {
        var cached = File("cached.pdf");
        var gate = new TaskCompletionSource<bool>();
        var (panel, fake, _) = Open("[the brief](/Users/a/Docs/brief.pdf)", f =>
        {
            f.FileHost = ChatFileHost.Peer;
            f.FileHostName = "Mac Mini";
            f.FileGate = gate;
            f.FileAnswer = _ => ChatFileResult.Cached(cached, false);
        });
        var block = BlockSaying(panel, "the brief");
        var mouse = new Mouse(panel, block);
        var point = At(panel, block, "the brief", 2);

        mouse.Click(point);
        mouse.Click(point);
        Assert.Single(fake.FetchedFiles);

        gate.SetResult(true);
        await Until(() => _opened.Count == 1);
        Assert.Single(_opened);
    }

    [AvaloniaFact]
    public void APeersExecutableFileIsRevealedNotOpenedAndSaysWhy()
    {
        var cached = File("tool.pdf");
        var (panel, _, turn) = Open("[the tool](/Users/a/Docs/tool.pdf)", f =>
        {
            f.FileHost = ChatFileHost.Peer;
            f.FileHostName = "Mac Mini";
            f.FileAnswer = _ => ChatFileResult.Cached(cached, executable: true);
        });
        var block = BlockSaying(panel, "the tool");

        new Mouse(panel, block).Click(At(panel, block, "the tool", 2));
        Flush();

        Assert.Equal(new[] { cached }, _revealed);
        Assert.Empty(_opened);
        Assert.Equal("Executable files are shown, not run.", turn.FileNote);
    }

    // --- tooltip ---

    [AvaloniaFact]
    public void ALocalFileLinkSaysItsPathAndThatItIsOnThisComputer()
    {
        var path = File();
        var (panel, _, _) = Open($"[the report]({Url(path)})");
        var block = BlockSaying(panel, "the report");

        new Mouse(panel, block).Hover(At(panel, block, "the report", 2));

        Assert.Equal(path + "\non this computer", ToolTip.GetTip(block));
    }

    [AvaloniaFact]
    public void APeersFileLinkNamesTheMachine()
    {
        var (panel, _, _) = Open("[the brief](/Users/a/Docs/brief.pdf)", f =>
        {
            f.FileHost = ChatFileHost.Peer;
            f.FileHostName = "Mac Mini";
        });
        var block = BlockSaying(panel, "the brief");

        new Mouse(panel, block).Hover(At(panel, block, "the brief", 2));

        Assert.Equal("/Users/a/Docs/brief.pdf\non Mac Mini", ToolTip.GetTip(block));
    }

    [AvaloniaFact]
    public void AWebLinkKeepsItsDestinationAsTheTooltip_Control()
    {
        var (panel, _, _) = Open("[docs](https://example.com/a)");
        var block = BlockSaying(panel, "docs");

        new Mouse(panel, block).Hover(At(panel, block, "docs", 1));

        Assert.Equal("https://example.com/a", ToolTip.GetTip(block));
    }

    // --- a session with no files ---

    private sealed class NoFilesSession : IRemoteChatSession
    {
        public string SessionId { get; } = "nofiles-" + Guid.NewGuid();
        public string DisplayName => "Cloud-ish";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>
        {
            new() { Role = ChatRole.Assistant, Text = "open [the report](/Users/a/report.pdf) now", IsComplete = true }
        };

        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }

        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);

        public void Cancel() { }
    }

    [AvaloniaFact]
    public void ASessionWithNoFilesFlattensAFileLinkToItsLabel()
    {
        var session = new NoFilesSession();
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        var panel = ChatPanelTestAccess.Instance!;
        var block = BlockSaying(panel, "the report");

        Assert.Null(block.Tag);
        Assert.Contains("open the report now", Rendered(block));

        new Mouse(panel, block).Click(At(panel, block, "the report", 2));
        Assert.Empty(_opened);
        Assert.Empty(_web);
    }

    [AvaloniaFact]
    public void TheSameTextInASessionWithFilesIsALink_Control()
    {
        var (panel, _, _) = Open("open [the report](/Users/a/report.pdf) now");
        var block = BlockSaying(panel, "the report");

        Assert.NotNull(block.Tag);
    }
}
