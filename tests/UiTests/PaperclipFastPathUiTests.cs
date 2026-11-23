using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-427 / CB-425 wiring through SessionManager, with a fake clock and no timer:
// an answered need leaves its orb at once (and the next poll can put it back), and
// every Paperclip state change the scan draws writes one line.
[Collection("Settings")]
public class PaperclipFastPathUiTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb425-" + Guid.NewGuid());
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;

    public PaperclipFastPathUiTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        OrbweaverSettings.PaperclipEnabled = _enabled;
        PaperclipSessions.SetSnapshotForTests([]);
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private SessionManager Manager() => (SessionManager)typeof(SessionManager).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { _scratch });

    private static PaperclipAgent Agent(string id, string state, params PaperclipNeed[] needs) =>
        new(id, "Atlas", "ceo", "", "idle", null, "x", state, PaperclipMark.None, null, needs, null, null, null, null, null,
            DateTime.UtcNow, "http://127.0.0.1:3100/CB/agents/" + id, false);

    private static readonly PaperclipNeed Hire = new("approval", "p1", "Hire", "http://127.0.0.1:3100/CB/approvals/p1");
    private static readonly PaperclipNeed Tool = new("action", "p2", "Deploy", "http://127.0.0.1:3100/CB/inbox/requests");

    private static SessionStatus S(SessionManager m, string id) => m.StatusFor("paperclip:" + id)!;

    private async Task<SessionManager> WithAmberOrb(params PaperclipNeed[] needs)
    {
        OrbweaverSettings.PaperclipEnabled = true;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "t");
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "waiting", needs)]);
        PaperclipSessions.TakeChangeSeen();
        var m = Manager();
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
        return m;
    }

    // ---- the orb the user just answered leaves amber at once ---------------------------

    [AvaloniaFact]
    public async Task Answering_the_last_need_clears_the_orb_now_and_a_poll_that_still_lists_it_puts_it_back()
    {
        var m = await WithAmberOrb(Hire);
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        Assert.Equal((OrbPresence.Present, "idle"), (S(m, "a1").Presence, S(m, "a1").State));
        Assert.Equal("http://127.0.0.1:3100/CB/agents/a1", S(m, "a1").Url);

        // the poll has not caught up and still says waiting: it stays the source of truth
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);

        // the poll catches up: the orb stays clear
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
    }

    [AvaloniaFact]
    public async Task Answering_one_of_two_leaves_the_orb_amber_and_an_unknown_answer_does_nothing()
    {
        var m = await WithAmberOrb(Hire, Tool);
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
        Assert.Equal([Tool], S(m, "a1").Needs);

        Assert.False(m.ClearPaperclipNeed("a1", "nope"));       // not this orb's need
        Assert.False(m.ClearPaperclipNeed("ghost", "p2"));      // no such orb
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
    }

    // ---- one line per Paperclip state change the scan draws ----------------------------

    [AvaloniaFact]
    public async Task A_state_change_the_scan_draws_writes_one_line_with_its_wait_and_a_quiet_scan_writes_none()
    {
        var m = await WithAmberOrb(Hire);
        var seen = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();
        m.PaperclipClock = () => seen.AddMilliseconds(3300);
        m.PaperclipLatencySink = lines.Add;

        // a scan with nothing published and nothing moved
        await m.ScheduleScan();
        Assert.Empty(lines);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        PaperclipSessions.MarkChanged(seen);
        await m.ScheduleScan();
        Assert.Equal(["paperclip: a1 waiting -> idle reached the orb in 3300 ms"], lines);

        // the same state drawn again, even with a mark pending: no second line
        PaperclipSessions.MarkChanged(seen);
        await m.ScheduleScan();
        Assert.Single(lines);
    }

    [AvaloniaFact]
    public async Task A_fast_change_writes_its_line_too_and_a_change_with_no_published_mark_writes_none()
    {
        var m = await WithAmberOrb(Hire);
        var seen = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();
        m.PaperclipClock = () => seen.AddMilliseconds(120);
        m.PaperclipLatencySink = lines.Add;

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        PaperclipSessions.MarkChanged(seen);
        await m.ScheduleScan();
        Assert.Equal(["paperclip: a1 waiting -> idle reached the orb in 120 ms"], lines);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "waiting", Hire)]);   // moved, but nothing was published
        await m.ScheduleScan();
        Assert.Single(lines);
    }

    // ---- the fast path, back on a copy (CB-425) ---------------------------------------------

    // Freezes a scan's worker thread just after it has read the status files, by
    // blocking on the clock the read phase is timed with.
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
    public async Task A_change_reaches_its_orb_while_a_slow_pass_is_still_in_flight_and_the_pass_cannot_draw_the_amber_back()
    {
        var m = await WithAmberOrb(Hire);
        var seen = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();
        m.PaperclipClock = () => seen.AddMilliseconds(3300);
        m.PaperclipLatencySink = lines.Add;
        var worker = new FrozenWorker();
        m.ScanClock = worker.Clock;

        var running = m.ScheduleScan();                  // a slow local pass, held after its read
        await worker.ReadDone.Task;
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        PaperclipSessions.MarkChanged(seen);
        Assert.True(m.ApplyPaperclipNow());              // the change does not wait for the pass
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
        Assert.Equal(["paperclip: a1 waiting -> idle reached the orb in 3300 ms"], lines);

        worker.Release.Set();                            // the pass now applies: the apply reads the snapshot itself
        await running;
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
        Assert.Single(lines);                            // one line per change, whichever path drew it
    }

    [AvaloniaFact]
    public async Task The_fast_path_and_an_answer_clear_publish_a_new_object_and_never_touch_the_one_the_relay_holds()
    {
        var m = await WithAmberOrb(Hire, Tool);
        var held = S(m, "a1");                           // what the last scan handed the relay
        var before = (held.State, held.Presence, held.Needs.Count);

        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        Assert.NotSame(held, S(m, "a1"));
        Assert.Equal(before, (held.State, held.Presence, held.Needs.Count));

        var afterClear = S(m, "a1");
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        Assert.True(m.ApplyPaperclipNow());
        Assert.NotSame(afterClear, S(m, "a1"));
        Assert.Equal(OrbPresence.NeedsInput, afterClear.Presence);     // the copy handed out before is unchanged too
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
    }

    [AvaloniaFact]
    public async Task A_change_that_alters_the_set_of_orbs_falls_back_and_a_published_change_still_asks_for_the_scan()
    {
        var m = await WithAmberOrb(Hire);
        var debounce = (Avalonia.Threading.DispatcherTimer)typeof(SessionManager)
            .GetField("_debounce", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        Assert.False(debounce.IsEnabled);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle"), Agent("a2", "idle")]);
        Assert.False(m.ApplyPaperclipNow());             // a new orb: the full pass owns it
        PaperclipSessions.SetSnapshotForTests([]);
        Assert.False(m.ApplyPaperclipNow());             // nothing to apply

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        m.OnPaperclipChanged();
        Assert.True(debounce.IsEnabled);
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
    }
}
