using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-363: what a click on a file link does, driven through ChatFileLinks' injected seams so no
/// disk is read and no process started. Every Reveal/Refuse/failure case has a paired control
/// asserting the opener was NOT called, because the property that matters is that nothing launches
/// on the way to a refusal.
/// </summary>
public class ChatFileLinksTests
{
    // Progress is delivered through the context the click started on. xUnit installs its own for
    // an async test body after the constructor runs, so each test that observes ordering clears it
    // again where it starts (Click does it), making delivery inline without a dispatcher.

    private sealed class Files : IRemoteChatFiles
    {
        public ChatFileHost FileHost { get; set; } = ChatFileHost.ThisMachine;
        public string? FileHostName { get; set; }
        public List<string> Fetched { get; } = new();
        public Func<string, ChatFileResult> Answer { get; set; } = ChatFileResult.Local;
        public Action<IProgress<(long Done, long Total)>?>? OnFetch { get; set; }
        public TaskCompletionSource<bool>? Gate { get; set; }
        public Exception? Throw { get; set; }
        public IProgress<(long Done, long Total)>? Captured { get; private set; }

        public async Task<ChatFileResult> FetchFileAsync(
            string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
        {
            Fetched.Add(path);
            Captured = progress;
            OnFetch?.Invoke(progress);
            if (Gate is not null) await Gate.Task;
            ct.ThrowIfCancellationRequested();
            if (Throw is not null) throw Throw;
            return Answer(path);
        }
    }

    private sealed class Harness
    {
        public Files Files { get; } = new();
        public List<string?> Notes { get; } = new();
        public List<(string Path, FileLinkAction Action)> Opened { get; } = new();
        public List<(string Path, bool Exec)> Decided { get; } = new();
        public FileLinkAction Verdict { get; set; } = FileLinkAction.Open;
        public string? VerdictRefusal { get; set; }
        public bool OpenResult { get; set; } = true;

        public ChatFileLinks Links => new(
            Files,
            (p, x) => { Decided.Add((p, x)); return new ChatFileLinks.Decision(Verdict, VerdictRefusal); },
            (p, a) => { Opened.Add((p, a)); return OpenResult; });

        public ChatFileLinks Make(ChatFileLinks? links = null) => links ?? Links;
        public Task Click(ChatFileLinks links, string path = "/a/x.pdf", CancellationToken ct = default)
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return links.ClickAsync(path, Notes.Add, ct);
        }
    }

    [Fact]
    public async Task ALocalClickOpensThePathAndLeavesNoNote()
    {
        var h = new Harness();
        await h.Click(h.Links);
        Assert.Equal(new[] { "/a/x.pdf" }, h.Files.Fetched);
        Assert.Equal(new[] { ("/a/x.pdf", FileLinkAction.Open) }, h.Opened);
        Assert.Equal(new string?[] { null }, h.Notes);
    }

    [Fact]
    public async Task APeerClickShowsFetchingThenClearsOnOpen()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.OnFetch = p => p!.Report((42, 100));
        h.Files.Answer = _ => ChatFileResult.Cached("/cache/x.pdf", false);
        await h.Click(h.Links);
        Assert.Equal(
            new string?[] { "Fetching x.pdf from Mac Mini…", "Fetching x.pdf from Mac Mini… 42%", null },
            h.Notes);
        Assert.Equal(new[] { ("/cache/x.pdf", FileLinkAction.Open) }, h.Opened);
    }

    [Fact]
    public async Task APeerExecutableFlagReachesThePolicyAndRevealsRatherThanOpens()
    {
        var h = new Harness { Verdict = FileLinkAction.Reveal };
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.Answer = _ => ChatFileResult.Cached("/cache/tool", executable: true);
        await h.Click(h.Links, "/a/tool");
        Assert.Equal(new[] { ("/cache/tool", true) }, h.Decided);
        Assert.Equal(new[] { ("/cache/tool", FileLinkAction.Reveal) }, h.Opened);
        Assert.Equal("tool isn't opened from chat — it's shown in your file manager instead.", h.Notes[^1]);
    }

    [Fact]
    public async Task ALocalCopyIsDecidedWithoutAnExecutableClaim()
    {
        var h = new Harness();
        await h.Click(h.Links);
        Assert.Equal(new[] { ("/a/x.pdf", false) }, h.Decided);
    }

    [Fact]
    public async Task ARefusedFetchSaysWhyAndNeverReachesThePolicyOrTheOpener()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Answer = _ => ChatFileResult.Refused("forbidden_path");
        await h.Click(h.Links);
        Assert.Equal("Mac Mini doesn't share that folder.", h.Notes[^1]);
        Assert.Empty(h.Decided);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task AnOldPeerGetsTheUpdateSentence()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Answer = _ => ChatFileResult.Refused("update");
        await h.Click(h.Links);
        Assert.Equal("Update Orbweaver on Mac Mini to open files from it.", h.Notes[^1]);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task ARefusedResultWithNoCodeIsAFailureNotASilentOpen()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.Answer = _ => new ChatFileResult();
        await h.Click(h.Links);
        Assert.Equal("Couldn't fetch the file from that machine.", h.Notes[^1]);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task ALocalFailureNeverNamesAMachine()
    {
        var h = new Harness();
        h.Files.Answer = _ => ChatFileResult.Refused("not_found");
        await h.Click(h.Links);
        Assert.Equal("That file no longer exists.", h.Notes[^1]);

        var h2 = new Harness { OpenResult = false };
        await h2.Click(h2.Links);
        Assert.Equal("Couldn't open that file.", h2.Notes[^1]);
        Assert.DoesNotContain("machine", h2.Notes[^1]);
    }

    [Fact]
    public async Task APolicyRefusalSaysSoAndNeverOpens()
    {
        var h = new Harness { Verdict = FileLinkAction.Refuse, VerdictRefusal = "forbidden_path" };
        await h.Click(h.Links);
        Assert.Equal("That file is outside the folders Orbweaver opens from chat.", h.Notes[^1]);
        Assert.Empty(h.Opened);

        var bare = new Harness { Verdict = FileLinkAction.Refuse };
        await bare.Click(bare.Links);
        Assert.Equal("That kind of link can't be opened.", bare.Notes[^1]);
        Assert.Empty(bare.Opened);
    }

    [Fact]
    public async Task ARevealIsOpenedAsAReveal_AndAnOpenIsNot()
    {
        var reveal = new Harness { Verdict = FileLinkAction.Reveal };
        await reveal.Click(reveal.Links, "/a/run.sh");
        Assert.Equal(FileLinkAction.Reveal, Assert.Single(reveal.Opened).Action);

        var open = new Harness();
        await open.Click(open.Links, "/a/x.pdf");
        Assert.Equal(FileLinkAction.Open, Assert.Single(open.Opened).Action);
    }

    [Fact]
    public async Task AnOpenerThatSaysNoIsAFailureLine()
    {
        var h = new Harness { OpenResult = false };
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Answer = _ => ChatFileResult.Cached("/c/x.pdf", false);
        await h.Click(h.Links);
        Assert.Equal("Couldn't fetch the file from Mac Mini.", h.Notes[^1]);
    }

    [Fact]
    public async Task AFetchThatThrowsIsAFailureLineNotACrash()
    {
        var h = new Harness();
        h.Files.Throw = new InvalidOperationException("boom");
        await h.Click(h.Links);
        Assert.Equal("Couldn't open that file.", h.Notes[^1]);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task ACancelledFetchClearsTheNoteQuietly()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await h.Click(h.Links, ct: cts.Token);
        Assert.Null(h.Notes[^1]);
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task AMissingPolicyFailsClosedNeverOpen()
    {
        // The default decide is the unwired placeholder: it throws, and the
        // click must come out as a refusal with nothing launched.
        var files = new Files();
        var notes = new List<string?>();
        var opened = new List<string>();
        var links = new ChatFileLinks(files, open: (p, _) => { opened.Add(p); return true; });
        await links.ClickAsync("/a/x.pdf", notes.Add);
        Assert.Empty(opened);
        Assert.Equal("Couldn't open that file.", notes[^1]);
    }

    [Fact]
    public async Task ASecondClickWhileOneIsInFlightStartsNoSecondFetch()
    {
        var h = new Harness();
        h.Files.Gate = new TaskCompletionSource<bool>();
        var links = h.Links;
        var first = h.Click(links);
        await h.Click(links); // returns immediately: guarded
        Assert.Single(h.Files.Fetched);

        h.Files.Gate.SetResult(true);
        await first;
        Assert.Single(h.Opened);

        // Control: once the first finished, the same link can be clicked again.
        await h.Click(links);
        Assert.Equal(2, h.Files.Fetched.Count);
    }

    [Fact]
    public async Task ADifferentPathIsNotBlockedByAnInFlightOne()
    {
        var h = new Harness();
        h.Files.Gate = new TaskCompletionSource<bool>();
        var links = h.Links;
        var first = h.Click(links, "/a/one.pdf");
        var second = h.Click(links, "/a/two.pdf");
        Assert.Equal(2, h.Files.Fetched.Count);
        h.Files.Gate.SetResult(true);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task AFailedClickReleasesTheGuard()
    {
        var h = new Harness();
        h.Files.Throw = new InvalidOperationException("boom");
        var links = h.Links;
        await h.Click(links);
        await h.Click(links);
        Assert.Equal(2, h.Files.Fetched.Count);
    }

    [Fact]
    public async Task ProgressReportedAfterTheFinalNoteNeverLeavesAStuckFetchingLine()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Answer = _ => ChatFileResult.Cached("/c/x.pdf", false);
        await h.Click(h.Links);
        Assert.Null(h.Notes[^1]);

        // The client reports once more after returning.
        h.Files.Captured!.Report((99, 100));
        Assert.Null(h.Notes[^1]);
        Assert.DoesNotContain(h.Notes, n => n is not null && n.Contains("99%"));
    }

    [Fact]
    public async Task ProgressReportedWhileInFlightIsShown_ControlForTheGate()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Gate = new TaskCompletionSource<bool>();
        h.Files.Answer = _ => ChatFileResult.Cached("/c/x.pdf", false);
        var task = h.Click(h.Links);
        h.Files.Captured!.Report((10, 100));
        Assert.Equal("Fetching x.pdf from Mac Mini… 10%", h.Notes[^1]);
        h.Files.Gate.SetResult(true);
        await task;
        Assert.Null(h.Notes[^1]);
    }

    // A context that queues Posts until pumped, standing in for the UI dispatcher.
    private sealed class QueueContext : SynchronizationContext
    {
        public Queue<(SendOrPostCallback Cb, object? State)> Queue { get; } = new();
        public override void Post(SendOrPostCallback d, object? state) => Queue.Enqueue((d, state));
        public void Pump() { while (Queue.Count > 0) { var (cb, st) = Queue.Dequeue(); cb(st); } }
    }

    [Fact]
    public async Task AProgressReportQueuedOnTheContextBehindTheFinalNoteIsDropped()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Answer = _ => ChatFileResult.Cached("/c/x.pdf", false);
        h.Files.OnFetch = p => p!.Report((99, 100)); // posted, not yet run
        var ctx = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        await h.Links.ClickAsync("/a/x.pdf", h.Notes.Add);
        Assert.Null(h.Notes[^1]);

        ctx.Pump(); // the late report finally runs
        Assert.Null(h.Notes[^1]);
        Assert.DoesNotContain(h.Notes, n => n is not null && n.Contains("99%"));
    }

    [Fact]
    public async Task AProgressReportQueuedOnTheContextIsShownWhileTheFetchIsStillOpen()
    {
        var h = new Harness();
        h.Files.FileHost = ChatFileHost.Peer;
        h.Files.FileHostName = "Mac Mini";
        h.Files.Gate = new TaskCompletionSource<bool>();
        var ctx = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        var task = h.Links.ClickAsync("/a/x.pdf", h.Notes.Add);
        h.Files.Captured!.Report((50, 100));
        ctx.Pump();
        Assert.Equal("Fetching x.pdf from Mac Mini… 50%", h.Notes[^1]);
        h.Files.Gate.SetResult(true);
        SynchronizationContext.SetSynchronizationContext(null);
        ctx.Pump();
        await task;
    }
}
