using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-443 section 8.4 of the plan, run by itself against a REAL Paperclip, headless: nobody opens the panel by hand.
//
// Off unless ORBWEAVER_PAPERCLIP_LIVE=1 (a developer's machine, and CI, skip it with the reason). Everything it needs comes
// from the environment, never from a file in the repo, and nothing it prints contains a key:
//
//   ORBWEAVER_PAPERCLIP_LIVE=1
//   ORBWEAVER_PAPERCLIP_LIVE_URL           e.g. http://127.0.0.1:3100   (no trailing path)
//   ORBWEAVER_PAPERCLIP_LIVE_COMPANY       the company id
//   ORBWEAVER_PAPERCLIP_LIVE_AGENT         the agent's id (the uuid, without "paperclip:")
//   ORBWEAVER_PAPERCLIP_LIVE_KEY           a board key from the app's connect flow
//   ORBWEAVER_PAPERCLIP_LIVE_REVOKED_KEY  a key that has been revoked (the control)
//   ORBWEAVER_PAPERCLIP_LIVE_TRANSCRIPT   optional: where the transcript is written
//                                          (default: TestResults/paperclip-live-transcript.txt in the repo root)
//
//   dotnet test tests/UiTests -c Release -- --filter-class Orbweaver.Tests.PaperclipChatLiveProofTests
//
// WHERE IT STARTS. The real poll (PaperclipSessions.Restart, over PaperclipHttp, reading the company) fills the snapshot; a
// real SessionManager scans it into a real OrbWindow; then the two calls OrbWindow.TryOpenRemoteChat makes when a Paperclip
// orb is clicked: SessionManager.RemoteChatFor(id), which builds the real PaperclipChatSession, and ChatPanel.OpenFor(orb,
// chat). From there everything is the real thing: PaperclipChatFeed, PaperclipActions, the session and the panel's own text
// box and send path (a key press on the box, as the panel tests do).
//
// WHAT IT CANNOT INCLUDE. The pointer click on the orb window and OrbWindow.GoToSession: GoToSession ends in
// TryOpenRemoteChat only because SessionManager.Instance is set by Start(), which creates the tray icon and timers, so it is
// replicated by calling its two lines directly. TerminalFocuser is NOT on this route for a Paperclip orb: GoToSession returns
// at TryOpenNeedsPanel or TryOpenRemoteChat before it (read from source, and RemoteChatFor never returns null for one). And
// no pixels on a real display: the headless renderer draws, a screen does not.
[Collection("Settings")]
public class PaperclipChatLiveProofTests : IDisposable
{
    private const string Prefix = "ORBWEAVER_PAPERCLIP_LIVE";
    private static string? Env(string suffix) => Environment.GetEnvironmentVariable(Prefix + suffix) is { Length: > 0 } v ? v : null;

    // AvaloniaFactAttribute is sealed, so the gate is a dynamic skip at the top of the test.
    private static readonly string SkipReason =
        $"a live proof against a real Paperclip; set {Prefix}=1 and the {Prefix}_URL, _COMPANY, _AGENT, _KEY and _REVOKED_KEY variables";

    private const string Pineapple = "Reply with the single word pineapple.";

    // The measured wake: a run started 2.0 s after the comment and finished 12.2 s after it. Three times that.
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(37);

    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly Func<Func<ChatTarget>, IPaperclipChatFeed> _feeds = SessionManager.PaperclipFeedFactory;
    private readonly Func<PaperclipSessions.PaperclipConfig> _config = PaperclipSessions.ConfigSource;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb443-live-" + Guid.NewGuid());
    private readonly List<string> _transcript = new();
    private readonly List<string> _requests = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string? _panelId;

    public PaperclipChatLiveProofTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        if (_panelId is not null) ChatPanel.HideFor(_panelId);
        SessionManager.PaperclipFeedFactory = _feeds;
        PaperclipSessions.Actions = null;
        PaperclipSessions.ConfigSource = _config;
        OrbweaverSettings.PaperclipEnabled = _enabled;
        PaperclipSessions.Restart();   // the real poll stops: the config is back to what it was
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private void Say(string line)
    {
        var text = $"[{_clock.Elapsed.TotalSeconds,6:0.0}s] {line}";
        lock (_transcript) _transcript.Add(text);
        Console.WriteLine(text);
    }

    private static async Task Until(string what, TimeSpan within, Func<bool> done)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            await Task.Delay(50);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), $"within {within.TotalSeconds:0} s: {what}");
    }

    // GET reads of one kind the feed made (a post of a comment is a POST and is not counted). The request log is path,
    // status, size and time only: never a body, a key or a query.
    private int Requests(string pathPart)
    {
        lock (_requests) return _requests.Count(r => r.StartsWith("GET ", StringComparison.Ordinal) && r.Contains(pathPart, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task OpenSendAndReply_ThenARevokedKeyIsRefusedAndTheTextStays_ThenClosingStopsTheReads()
    {
        Assert.SkipUnless(Env("") == "1", SkipReason);
        var url = Env("_URL") ?? throw new InvalidOperationException(Prefix + "_URL is not set");
        var company = Env("_COMPANY") ?? throw new InvalidOperationException(Prefix + "_COMPANY is not set");
        var agent = Env("_AGENT") ?? throw new InvalidOperationException(Prefix + "_AGENT is not set");
        var key = Env("_KEY") ?? throw new InvalidOperationException(Prefix + "_KEY is not set");
        var revoked = Env("_REVOKED_KEY") ?? throw new InvalidOperationException(Prefix + "_REVOKED_KEY is not set");
        var sessionId = PaperclipScan.KeyFor(agent);
        _panelId = sessionId;

        // ---- the real pieces ---------------------------------------------------------------------------------------
        // The real poll, over the real client, for this company. The key lives only in this closure and in the client.
        OrbweaverSettings.PaperclipEnabled = true;
        PaperclipSessions.ConfigSource = () => new PaperclipSessions.PaperclipConfig(true, url, company, key, null, "fold");

        // The real feed, over a real PaperclipHttp that records each request's method-less path and status (never a body,
        // a key or a query), so the run shows the reads start on open and stop on close.
        SessionManager.PaperclipFeedFactory = target =>
            new LiveChatFeed(new PaperclipChatFeed(
                new PaperclipHttp(url, key, log: line => { lock (_requests) _requests.Add(line); }, logSuccess: true), company, target));

        PaperclipSessions.Restart();
        await Until("the poll connected and listed the agent", TimeSpan.FromSeconds(40),
            () => PaperclipSessions.Link == PaperclipLink.Connected && PaperclipSessions.Snapshot().Any(a => a.Id == agent));
        var row = PaperclipSessions.Snapshot().Single(a => a.Id == agent);
        Say($"poll connected; agent '{row.Name}' status={row.Status} state={row.State}");

        // ---- where the click would start ---------------------------------------------------------------------------
        var manager = (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { _scratch });
        manager.ScanAndUpdate();
        var orbs = (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        Assert.True(orbs.TryGetValue(sessionId, out var orb), "the scan made an orb for the agent");

        var chat = Assert.IsType<PaperclipChatSession>(manager.RemoteChatFor(sessionId));   // what TryOpenRemoteChat asks for
        ChatPanel.OpenFor(orb!, chat);                                                       // and what it does with it
        var panel = ChatPanelTestAccess.Instance!;
        Say($"panel opened for {chat.DisplayName}; header link '{(chat.ReplyUrl is null ? "none" : "Open in Paperclip")}'");

        // ---- 1. the thread, then a real message ----------------------------------------------------------------------
        await Until("the thread's task row arrived", TimeSpan.FromSeconds(30), () => chat.History.Count > 0);
        var taskLine = chat.History[0].Text;
        Say($"task line: {taskLine}");
        Assert.NotEqual("No task yet.", taskLine);                   // the agent has a task: the line is its real one
        Assert.Equal(ChatRole.System, chat.History[0].Role);
        foreach (var t in chat.History.Skip(1)) Say($"  row [{t.Role}{(t.IsError ? ", red" : "")}] {Trim(t.Text)}");

        var input = panel.FindControl<TextBox>("Input")!;
        var before = chat.History.Count;
        input.Text = Pineapple;
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Say($"sent through the panel: \"{Pineapple}\"");

        await Until("the user's bubble appeared (Paperclip accepted the comment)", TimeSpan.FromSeconds(15),
            () => chat.History.Skip(before).Any(t => t.Role == ChatRole.User && t.Text == Pineapple));
        Say("user bubble up");
        Assert.True(string.IsNullOrEmpty(input.Text), "the box was cleared once the send was accepted");
        Assert.Equal($"Waking {chat.DisplayName}…", input.Watermark);
        Say($"box says: {input.Watermark}");

        var seen = chat.History.Count;
        await Until("the agent's reply arrived", ReplyWindow,
            () => chat.History.Skip(before).Any(t => t.Role == ChatRole.Assistant));
        var reply = chat.History.Skip(before).First(t => t.Role == ChatRole.Assistant);
        Say($"agent replied: {Trim(reply.Text)}");
        foreach (var t in chat.History.Skip(before).Where(t => t != reply && t.Role != ChatRole.User)) Say($"  row [{t.Role}{(t.IsError ? ", red" : "")}] {Trim(t.Text)}");
        Assert.Contains("pineapple", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(seen >= before + 1);
        Assert.True(Requests("/comments") > 0, "the feed read the comments while the panel was open");
        Say($"reads while open: comments {Requests("/comments")}, run events {Requests("/events")}");

        // ---- 2. the control: a revoked key, on the send only ------------------------------------------------------------
        // The poll and the feed keep the good key (so the thread stays readable); only the send goes out with the revoked
        // one, through the same seam the panel sends through. A refusal must leave the text in the box, say why, and add
        // no bubble: it is what would have failed if the first send had gone through anywhere but the app's key.
        PaperclipSessions.Actions = new PaperclipActions(
            new PaperclipHttp(url, revoked, timeout: PaperclipSessions.ActionTimeout, logSuccess: false), company);
        var bubbles = chat.History.Count(t => t.Role == ChatRole.User);
        input.Text = "This must not arrive.";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until("the refusal's red line appeared", TimeSpan.FromSeconds(30),
            () => chat.History[^1] is { IsError: true });
        var red = chat.History[^1].Text;
        Say($"revoked-key send refused; red line: {red}");
        Assert.Equal($"Reconnect to Paperclip to message {chat.DisplayName}.", red);
        Assert.Equal("This must not arrive.", input.Text);                                  // the text stays in the box
        Assert.Equal(bubbles, chat.History.Count(t => t.Role == ChatRole.User));            // and no new bubble
        PaperclipSessions.Actions = null;

        // ---- 3. closing the panel stops the reads -----------------------------------------------------------------------
        var open = Requests("/comments");
        ChatPanel.HideFor(sessionId);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(500);                                                                // a read already on the wire may land
        var atClose = Requests("/comments");
        Say($"panel closed; comment reads so far: {atClose}");
        await Task.Delay(TimeSpan.FromSeconds(13));                                           // three times the comments cadence (4 s)
        Say($"13 s after close; comment reads: {Requests("/comments")}");
        Assert.Equal(atClose, Requests("/comments"));
        Assert.True(open > 0);

        Flush();
    }

    private static string Trim(string s) => s.Length <= 120 ? s : s[..117] + "…";

    private void Flush()
    {
        var path = Env("_TRANSCRIPT") ?? Path.Combine(ScreenshotRoot(), "TestResults", "paperclip-live-transcript.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_transcript) File.WriteAllLines(path, _transcript, Encoding.UTF8);
    }

    private static string ScreenshotRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? Path.GetTempPath();
    }
}
