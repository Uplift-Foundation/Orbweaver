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

    // ---- no flash of amber after an answer, and the relay's object is never written to ----------

    private static readonly PaperclipNeed Hire2 = new("approval", "p1", "Hire", "http://127.0.0.1:3100/CB/approvals/p1");

    [AvaloniaFact]
    public async Task An_answered_orb_stays_clear_through_a_scan_on_the_old_snapshot_and_a_newer_one_that_still_lists_it_brings_it_back()
    {
        var m = await WithAmberOrb(Hire);
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);

        // a pass applies before the nudged poll has come back: the snapshot is the old one
        await m.ScheduleScan();
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
        Assert.Empty(S(m, "a1").Needs);

        // a newer snapshot that still lists the need is the truth: amber returns
        PaperclipSessions.SetSnapshotForTests([Agent("a1", "waiting", Hire)]);
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
        Assert.Equal([Hire], S(m, "a1").Needs);

        // and the memory is gone: a later pass on that snapshot keeps it amber
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
    }

    [AvaloniaFact]
    public async Task A_newer_snapshot_that_no_longer_lists_the_need_leaves_the_orb_clear_and_one_of_two_answers_leaves_the_other_asking()
    {
        var m = await WithAmberOrb(Hire, Tool);
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        await m.ScheduleScan();
        Assert.Equal([Tool], S(m, "a1").Needs);
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "idle")]);
        await m.ScheduleScan();
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
    }

    [AvaloniaFact]
    public async Task An_answer_while_a_pass_is_in_flight_is_not_drawn_back_by_that_pass()
    {
        var m = await WithAmberOrb(Hire);
        var worker = new FrozenWorker();
        m.ScanClock = worker.Clock;
        var running = m.ScheduleScan();                  // its worker has read the amber snapshot
        await worker.ReadDone.Task;
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        worker.Release.Set();                            // it now applies, on the same snapshot
        await running;
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
    }

    [AvaloniaFact]
    public async Task Clearing_an_answered_need_publishes_a_copy_and_never_touches_the_object_the_relay_holds()
    {
        var m = await WithAmberOrb(Hire, Tool);
        var held = S(m, "a1");                           // what the last scan handed the relay
        var before = (held.State, held.Presence, held.Needs.Count, held.Url);

        Assert.True(m.ClearPaperclipNeed("a1", "p1"));
        Assert.NotSame(held, S(m, "a1"));
        Assert.Equal(before, (held.State, held.Presence, held.Needs.Count, held.Url));
    }

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

    // ---- the fast path (CB-425), on a copy ----------------------------------------------------

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

    [AvaloniaFact]
    public async Task The_fast_path_also_leaves_an_answered_need_out_until_a_newer_snapshot_says_otherwise()
    {
        var m = await WithAmberOrb(Hire);
        Assert.True(m.ClearPaperclipNeed("a1", "p1"));

        Assert.True(m.ApplyPaperclipNow());              // a change published on the SAME snapshot object
        Assert.Equal(OrbPresence.Present, S(m, "a1").Presence);
        Assert.Empty(S(m, "a1").Needs);

        PaperclipSessions.SetSnapshotForTests([Agent("a1", "waiting", Hire)]);   // a newer one that still lists it
        Assert.True(m.ApplyPaperclipNow());
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
        Assert.Equal([Hire], S(m, "a1").Needs);

        Assert.True(m.ApplyPaperclipNow());              // and the memory is gone
        Assert.Equal(OrbPresence.NeedsInput, S(m, "a1").Presence);
    }
}
