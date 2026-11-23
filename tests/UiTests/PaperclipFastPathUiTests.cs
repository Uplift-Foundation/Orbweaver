using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-425: a Paperclip change reaching its orb while a local scan pass is still in
// flight, and not being drawn back over by that pass's stale picture. The pure
// decisions are PaperclipFastPathTests; this is SessionManager's wiring, driven
// with a fake clock and no timer.
[Collection("Settings")]
public class PaperclipFastPathUiTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb425-" + Guid.NewGuid());
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private SessionManager? _m;

    public PaperclipFastPathUiTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        OrbweaverSettings.PaperclipEnabled = _enabled;
        PaperclipSessions.SetSnapshotForTests([]);
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private SessionManager Manager() => _m = (SessionManager)typeof(SessionManager).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { _scratch });

    private static PaperclipAgent Agent(string id, string state, params PaperclipNeed[] needs) =>
        new(id, "Atlas", "ceo", "", "idle", null, "x", state, PaperclipMark.None, null, needs, null, null, null, null, null,
            DateTime.UtcNow, "http://127.0.0.1:3100/CB/agents/" + id, false);

    private static readonly PaperclipNeed Hire = new("approval", "p1", "Hire", "http://127.0.0.1:3100/CB/approvals/p1");

    private static OrbPresence PresenceOf(SessionManager m, string id) => m.StatusFor("paperclip:" + id)!.Presence;

    private async Task<SessionManager> WithAmberOrb()
    {
        OrbweaverSettings.PaperclipEnabled = true;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "t");
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "waiting", Hire)]);
        var m = Manager();
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.NeedsInput, PresenceOf(m, "a1"));
        return m;
    }

    [AvaloniaFact]
    public async Task A_cleared_need_reaches_its_orb_while_a_scan_pass_is_still_in_flight_and_the_line_names_the_wait()
    {
        var m = await WithAmberOrb();
        var seen = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();
        m.PaperclipClock = () => seen.AddMilliseconds(3300);
        m.PaperclipLatencySink = lines.Add;

        var running = m.ScheduleScan();                  // a slow local pass, in flight and not awaited
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        PaperclipSessions.MarkChanged(seen);
        Assert.True(m.ApplyPaperclipNow());              // ...and the change does not wait for it

        Assert.Equal(OrbPresence.Present, PresenceOf(m, "a1"));
        Assert.Equal(["paperclip: a1 waiting -> idle reached the orb in 3300 ms"], lines);
        await running;
    }

    // Why this holds with no extra code: the apply reads the Paperclip snapshot itself,
    // on the UI thread, when it runs (ScanAndUpdateCore), not the worker at the start of
    // the pass. So a fast-path update cannot be overwritten by an older picture. The
    // test freezes the worker after its read to keep it that way.
    //
    // Freezes a scan's worker thread just after it has read the status files (and so
    // the Paperclip snapshot), by blocking on the clock the read phase is timed with.
    private sealed class FrozenWorker
    {
        private readonly int _ui = Environment.CurrentManagedThreadId;
        private int _workerCalls;
        internal readonly TaskCompletionSource ReadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        internal TimeSpan Clock()
        {
            // worker calls: 1 "waiting for a worker thread", 2 read phase start, 3 read phase end
            if (Environment.CurrentManagedThreadId != _ui && Interlocked.Increment(ref _workerCalls) == 3)
            {
                ReadDone.TrySetResult();
                Release.Wait(TimeSpan.FromSeconds(20));
            }
            return _sw.Elapsed;
        }
    }

    [AvaloniaFact]
    public async Task A_pass_whose_worker_read_before_the_change_cannot_draw_the_amber_back_over_the_fast_update()
    {
        var m = await WithAmberOrb();
        var worker = new FrozenWorker();
        m.ScanClock = worker.Clock;

        var running = m.ScheduleScan();
        await worker.ReadDone.Task;                       // the worker has read the amber snapshot and is held
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        Assert.True(m.ApplyPaperclipNow());
        Assert.Equal(OrbPresence.Present, PresenceOf(m, "a1"));

        worker.Release.Set();                              // the stale pass now applies on the UI thread
        await running;
        Assert.Equal(OrbPresence.Present, PresenceOf(m, "a1"));
    }

    [AvaloniaFact]
    public async Task No_line_when_the_state_did_not_move_or_nothing_was_pending_and_no_fast_path_when_the_set_of_orbs_changed()
    {
        var m = await WithAmberOrb();
        var lines = new List<string>();
        m.PaperclipLatencySink = lines.Add;
        PaperclipSessions.TakeChangeSeen();

        PaperclipSessions.MarkChanged(DateTime.UtcNow);
        Assert.True(m.ApplyPaperclipNow());              // same state: applied, but nothing to say
        Assert.Empty(lines);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        Assert.True(m.ApplyPaperclipNow());              // moved, but no pending mark: no line
        Assert.Empty(lines);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle"), Agent("a2", "idle")]);
        Assert.False(m.ApplyPaperclipNow());             // a new orb: the full pass owns it
        PaperclipSessions.SetSnapshotForTests([]);
        Assert.False(m.ApplyPaperclipNow());             // nothing to apply
    }

    [AvaloniaFact]
    public async Task A_published_change_asks_for_the_ordinary_scan_as_well()
    {
        var m = await WithAmberOrb();
        var debounce = (Avalonia.Threading.DispatcherTimer)typeof(SessionManager)
            .GetField("_debounce", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        Assert.False(debounce.IsEnabled);
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        m.OnPaperclipChanged();
        Assert.True(debounce.IsEnabled);
        Assert.Equal(OrbPresence.Present, PresenceOf(m, "a1"));
    }
}
