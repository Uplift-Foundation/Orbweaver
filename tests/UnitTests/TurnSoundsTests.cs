using System.Threading;
using Xunit;

namespace Orbweaver.Tests;

// TurnSounds.Deliver itself: the seam between TurnSoundPolicy's pure
// decision and the world. TurnSoundPolicyTests already covers what gets
// decided; this covers what Deliver actually *does* with a Chime, a
// Summary, or — the part QA's fix added — a deferred one: the pending
// timer that plays it once the rate-limit gap opens, and the two places a
// background failure is caught rather than left to crash an unobserved
// Task.
//
// No Avalonia needed here — Deliver has no dispatcher dependency of its
// own (that lives in the callback SessionManager hands it, exercised in
// tests/UiTests instead). [Collection("Settings")] because Snapshot()
// reads OrbweaverSettings, which every class in that collection can
// otherwise leave in a state this file did not choose.
[Collection("Settings")]
public class TurnSoundsTests : IDisposable
{
    // Deliberately years in the past rather than DateTime.UtcNow: a
    // deferred decision's PlayAt is computed from the `now` a test hands
    // Deliver, but the pending timer itself schedules against the *real*
    // wall clock (SchedulePending's own delay calculation). A PlayAt this
    // far in the past clamps that delay to zero, so the timer fires almost
    // immediately instead of a test having to wait out a real two-second
    // gap.
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Fires whatever is still pending at a logical moment past every gap these
    // tests open, then waits for the chime chain itself, instead of sleeping
    // three real seconds and hoping both the pending timer and the chain's
    // thread-pool continuation got scheduled in that time. On a loaded CI
    // runner they did not, once: Windows saw an empty list where A's live
    // Glass had been enqueued. Same shape the CB-240 tests below already use.
    // If the real timer got there first it took the pending events and this
    // fire finds nothing; either way every chime is enqueued before the await.
    private static async Task FireDueAndDrainAsync(DateTime t0)
    {
        TurnSounds.FirePending(t0 + TurnSoundPolicy.MinimumGap + TimeSpan.FromSeconds(1));
        await TurnSounds.ChimesEnqueuedSoFar();
    }

    public TurnSoundsTests() => TurnSounds.ResetForTests();

    public void Dispose()
    {
        ChimePlayer.PlayForTests = null;
        TurnSounds.ResetForTests();

        // QA round 2 finding 4's test drives TextToSpeech's own static state
        // directly (there is no other way to simulate "speech is already
        // playing" without a real speaker) — reset it here so a later test
        // in this class, or in another class sharing the process, never
        // finds speechBusy true for a reason that has nothing to do with it.
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
    }

    private static TurnSoundEvent Finished(string key, string sessionId) =>
        new(TurnSignal.Finished, key, sessionId);

    private static TurnSoundEvent NeedsAttention(string key, string sessionId) =>
        new(TurnSignal.NeedsAttention, key, sessionId);

    private static Func<string, Task<bool>> NoSummary => _ => Task.FromResult(false);

    // QA (CB-167): the drop-not-defer fix's scan-side half. A signal that
    // lands inside the gap is held rather than thrown away, and the timer
    // that was armed for it is what actually plays it once the gap opens —
    // this is the part TurnSoundPolicyTests' pure Decide test cannot reach
    // on its own, since Decide only ever returns the deferred *action*, it
    // never schedules anything.
    [Fact]
    public async Task ADeferredDecisionEventuallyPlaysThroughThePendingTimer()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played)
            {
                played.Add(path);
            }

            signal.TrySetResult(true);
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null; // platform default (Glass on macOS)

        // First signal: nothing has played yet, so this plays immediately
        // and sets _lastPlayed to Past.
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);

        var first = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, first);
        Assert.Single(played);

        signal = new TaskCompletionSource<bool>();

        // Second signal: one second after the first, inside the 2s gap —
        // Decide returns it deferred rather than Silent, and the pending
        // timer is what has to carry it out.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, Past.AddSeconds(1));

        var second = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, second);
        Assert.Equal(2, played.Count);
    }

    // QA round 2, finding 3: this used to assert the opposite of what it
    // does now — that a live decision arriving after a deferred one clears
    // the deferred one, "rather than letting a stale timer double the sound
    // up." That was precisely the bug finding 3 reported: a live decision
    // for one session silently erased a still-genuinely-pending one for a
    // different session. B's own timer, armed when B was deferred, now
    // fires on its own schedule regardless of what C's later, unrelated
    // live decision does — C plays live, B plays once its own gap opens,
    // and neither erases the other.
    [Fact]
    public async Task ALiveDecisionDoesNotClearAnUnrelatedSessionsStillPendingOne()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played)
            {
                played.Add(path);
            }

            signal.TrySetResult(true);
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        // Anchored to the real clock rather than Past for this one case,
        // deliberately: SchedulePending's own delay is always computed
        // against real DateTime.UtcNow, so a decision built from a `now`
        // years in the past (as every other case here uses) gives that
        // timer a delay that clamps to zero — which fires it before this
        // test's own third Deliver call could ever race it fairly. Anchored
        // to "now," the pending timer gets a real ~2 s window, and the third
        // call's *logical* clock (not the real one) is what makes it live
        // rather than deferred — so it reliably arrives first.
        var start = DateTime.UtcNow;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        // Deferred — 0.1 s of logical time after the first, still inside
        // the 2 s gap. Real elapsed time so far is milliseconds, so the
        // pending timer this arms has close to a real two-second delay.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        // A third signal, logically 2.5 s after the first (so Decide calls
        // it live, not deferred) but issued in real time only milliseconds
        // after the second — well before B's own pending timer's real-world
        // deadline. Under the old (buggy) behaviour this used to wipe out
        // B's still-pending signal; under the fix it is unrelated and plays
        // independently.
        signal = new TaskCompletionSource<bool>();
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, start.AddSeconds(2.5));

        var third = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, third);
        var cPlayedAt = DateTime.UtcNow;

        // B's own timer fires about two real seconds after A and finds C's
        // live sound inside its gap (CB-240: FirePending holds itself to the
        // gap now, where it used to play on top of C). B is not erased, it
        // waits for its own gap again, which C's stamp puts a gap after C. So
        // this waits for B to actually play, bounded only so a lost B fails
        // instead of hanging, and asserts how far behind C it landed.
        signal = new TaskCompletionSource<bool>();
        var fourth = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Equal(signal.Task, fourth);
        var bPlayedAt = DateTime.UtcNow;

        Assert.Equal(3, played.Count); // A live, B's own deferred Ping, C live — none erased the others
        Assert.True(bPlayedAt - cPlayedAt >= TimeSpan.FromSeconds(1.9),
            $"B played {(bPlayedAt - cPlayedAt).TotalMilliseconds:F0} ms after C");
    }

    // QA (CB-167): the 2 s rate limit only spaces out when a sound is
    // *decided* — it says nothing about how long the previous one takes to
    // actually finish, and a user's own chosen file can run the full 5 s
    // cap. Two live decisions landing exactly 2 s apart (both legal, one
    // right after the gap the other closed) must still never have their
    // playback overlap if the first is still running. The seam simulates a
    // real duration with a short sleep — not a wait for a signal, an
    // honest stand-in for "afplay is still running" — so the test can
    // observe whether the second's start actually waited for the first's
    // finish rather than merely being decided after it.
    [Fact]
    public async Task TwoChimesDecidedTwoSecondsApartNeverOverlapInActualPlayback()
    {
        var events = new System.Collections.Generic.List<(string Path, DateTime Started, DateTime Finished)>();
        var done = new TaskCompletionSource<bool>();

        ChimePlayer.PlayForTests = path =>
        {
            var started = DateTime.UtcNow;
            Thread.Sleep(300); // stands in for a real, audible-length chime
            var finished = DateTime.UtcNow;
            lock (events)
            {
                events.Add((path, started, finished));
                if (events.Count == 2) done.TrySetResult(true);
            }
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddSeconds(2));

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Equal(2, events.Count);
        Assert.True(
            events[1].Started >= events[0].Finished,
            "the second chime started before the first had finished playing");
    }

    // QA (CB-167): a deferred signal must never fire late for a session
    // that has already been told to forget what it was doing — a husk
    // pruned from the scan, or a manual reset. CancelPendingFor and
    // CancelPendingUnlessSeen are SessionManager's two call sites for that;
    // both are anchored to the real clock for the same race-avoidance
    // reason ALiveDecisionAfterADeferredOneDoesNotAlsoPlayThePendingOne is.
    [Fact]
    public async Task CancelPendingForDropsAPendingSignalBeforeItsTimerFires()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            signal.TrySetResult(true);
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        // Deferred — real delay close to two seconds, plenty of window to
        // cancel it before it would fire on its own.
        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        TurnSounds.CancelPendingFor("session-b");

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(played); // still just the first — the second never played
    }

    // The negative control: cancelling a *different* session's pending slot
    // must not touch this one's — otherwise CancelPendingFor could not be
    // told apart from ClearPending, which is precisely the bug this
    // wouldn't catch if the session id check were ever dropped.
    [Fact]
    public async Task CancelPendingForADifferentSessionLeavesTheRealPendingSignalAlone()
    {
        var played = new System.Collections.Generic.List<string>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await TurnSounds.ChimesEnqueuedSoFar();
        lock (played) Assert.Single(played);

        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        TurnSounds.CancelPendingFor("some-other-session-entirely");

        // Fired by hand at a logical moment past the gap, as the tests above
        // do (8d5fbf26e), instead of waiting up to three real seconds for the
        // pending timer: on a loaded runner the timer was the thing that did
        // not arrive in time (CI, run 45114606958, one failure in 16346).
        await FireDueAndDrainAsync(start);

        lock (played) Assert.Equal(2, played.Count);
    }

    // A chime still queued on the chain when ResetForTests runs must not
    // play into whichever test installs its seam next. The first chime
    // holds the chain open inside the seam; the second, decided well past
    // the 2 s gap, queues behind it; a reset lands while it waits. Once the
    // first is released, the second has to drop itself rather than reach
    // ChimePlayer.Play. This is the leak CI hit on macOS, where
    // CancelPendingForADifferentSessionLeavesTheRealPendingSignalAlone
    // counted an earlier test's Glass as its own.
    [Fact]
    public async Task AChimeStillQueuedWhenTheTestStateIsResetNeverPlays()
    {
        var played = new System.Collections.Generic.List<string>();
        using var holdFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            if (firstStarted.TrySetResult(true)) holdFirst.Wait(TimeSpan.FromSeconds(5));
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);
        Assert.True(await Task.WhenAny(firstStarted.Task, Task.Delay(TimeSpan.FromSeconds(2))) == firstStarted.Task);

        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, Past.AddSeconds(10));
        TurnSounds.ResetForTests();
        holdFirst.Set();

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        lock (played) Assert.Single(played);
    }

    // The Prune-shaped guard: a session missing from `seen` (a husk the
    // scan just dropped) loses its pending signal the same way a Settled
    // one does.
    [Fact]
    public async Task CancelPendingUnlessSeenDropsAPendingSignalForASessionTheScanNoLongerSees()
    {
        var played = new System.Collections.Generic.List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            signal.TrySetResult(true);
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);
        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Single(played);

        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        // "session-b" is the husk this pass no longer sees.
        TurnSounds.CancelPendingUnlessSeen(new HashSet<string> { "session-a" });

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(played);
    }

    // Both background catches: a chime backend and a summary attempt can
    // each fail for reasons this app does not control (a missing binary, a
    // gateway timeout), and neither may crash the process by throwing out
    // of an unobserved Task. Coverage rather than a stronger assertion is
    // the point — the completion source is only proof this ran to the catch
    // and back out again without taking the test process down with it.
    [Fact]
    public async Task AChimePlayerThatThrowsIsCaughtRatherThanCrashingTheBackgroundTask()
    {
        var threw = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = _ =>
        {
            threw.TrySetResult(true);
            throw new InvalidOperationException("no audio device");
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);

        var winner = await Task.WhenAny(threw.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(threw.Task, winner);
    }

    [Fact]
    public async Task ATrySpeakTurnSummaryThatThrowsIsCaughtRatherThanCrashingTheBackgroundTask()
    {
        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = "summary";

        var threw = new TaskCompletionSource<bool>();
        Task<bool> ThrowingSpeak(string sessionId)
        {
            threw.TrySetResult(true);
            throw new InvalidOperationException("gateway timed out");
        }

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, ThrowingSpeak, Past);

        var winner = await Task.WhenAny(threw.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(threw.Task, winner);
    }

    // QA round 2, finding 3, folded in from Marguerite's demonstrating test
    // (Cb167QaRound2DeliverTests.APendingAttentionIsNotReplacedByALaterDeferredFinished,
    // worktree cb-wt-turn-sounds-qa2): A finishes and plays live, the gap
    // opens at t0. B hits a permission prompt 0.5 s later — deferred to
    // t0+2. C finishes 1 s later — also inside the gap, also deferred. On
    // 5370ac56, SchedulePending held one slot, so C's arrival silently
    // replaced B's still-pending attention; B's own state stays "waiting"
    // (unchanged, so it never signals again on its own) and its Ping was
    // gone for good. _pendingEvents is a list precisely so a second
    // session's still-valid entry is never displaced by a different
    // session's later one — only a session's own newer signal replaces its
    // own older entry.
    [Fact]
    public async Task APendingAttentionIsNotReplacedByALaterDeferredFinished()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;   // platform default (Glass)
        OrbweaverSettings.NeedsAttentionSound = null; // platform default (Ping)

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);
        TurnSounds.Deliver(new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5));
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, t0.AddSeconds(1));

        await FireDueAndDrainAsync(t0);

        lock (played)
        {
            // Both B's Ping and C's Glass survive the gap — coalesced into
            // the same wait rather than one erasing the other.
            Assert.Contains(SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultAttentionSoundName,
                SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions), played);
        }
    }

    // QA round 2, finding 4, folded in from Marguerite's demonstrating test
    // (Cb167QaRound2DeliverTests.ADeferredSummaryDoesNotStartWhileSpeechIsAlreadyPlaying).
    // A's turn finishes as a summary and speaks immediately. B's turn
    // finishes 0.5 s later, inside the gap — deferred. Before B's timer
    // fires, the user presses Speak on some unrelated orb, so
    // TextToSpeech.IsSpeaking goes true mid-gap. On 5370ac56, speechBusy was
    // sampled once, at Decide time, and carried in the already-resolved
    // SoundAction — so a deferred summary always spoke, even over speech
    // that started after it was scheduled. FirePending now re-resolves
    // through ResolveOne with a freshly-read IsSpeaking, so this falls back
    // to the ordinary chime instead of talking over the user.
    [Fact]
    public async Task ADeferredSummaryDoesNotStartWhileSpeechIsAlreadyPlaying()
    {
        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = "summary";
        ChimePlayer.PlayForTests = _ => { };

        var spokenFor = new List<string>();
        Task<bool> Speak(string sessionId)
        {
            lock (spokenFor) spokenFor.Add(sessionId);
            return Task.FromResult(true);
        }

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, Speak, t0);
        await Task.Delay(200); // let the first summary's _lastPlayed land

        TurnSounds.Deliver(new[] { Finished("key-b", "session-b") }, Speak, t0.AddSeconds(0.5));

        // The user presses Speak on some other orb inside the gap.
        TextToSpeech.Enter(TextToSpeech.SpeakState.Speaking);

        await Task.Delay(TimeSpan.FromSeconds(3));

        lock (spokenFor) Assert.DoesNotContain("session-b", spokenFor);
    }

    // Round 3(c): two prompts landing inside one gap coalesce into a single
    // Ping by design — one sound stands for both. The pending attention
    // survives as long as ANY contributing event is still valid; only
    // session-x is cancelled here (its own equivalent of a Settle or Prune,
    // the same CancelPendingFor a real scan calls), and session-y's own
    // still-pending attention is what plays.
    [Fact]
    public async Task ACoalescedAttentionSurvivesWhenOnlyOneOfItsTwoSessionsIsCancelled()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap
        TurnSounds.Deliver(new[] { NeedsAttention("key-x", "session-x") }, NoSummary, t0.AddSeconds(0.3));
        TurnSounds.Deliver(new[] { NeedsAttention("key-y", "session-y") }, NoSummary, t0.AddSeconds(0.6));

        TurnSounds.CancelPendingFor("session-x");

        await FireDueAndDrainAsync(t0);

        lock (played)
        {
            Assert.Contains(SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultAttentionSoundName,
                SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions), played);
        }
    }

    // The other half: once EVERY contributing event has been cancelled,
    // nothing is left to play at all — this is what tells "one still-valid
    // survivor is enough" apart from "cancelling anything at all silences
    // the whole coalesced wait", which a test that only ever cancels one of
    // two could not distinguish.
    [Fact]
    public async Task ACoalescedAttentionPlaysNothingWhenBothOfItsSessionsAreCancelled()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap
        TurnSounds.Deliver(new[] { NeedsAttention("key-x", "session-x") }, NoSummary, t0.AddSeconds(0.3));
        TurnSounds.Deliver(new[] { NeedsAttention("key-y", "session-y") }, NoSummary, t0.AddSeconds(0.6));

        TurnSounds.CancelPendingFor("session-x");
        TurnSounds.CancelPendingFor("session-y");

        await FireDueAndDrainAsync(t0);

        lock (played)
        {
            // Just A's live Glass — nothing from the fully-cancelled wait.
            Assert.Single(played);
        }
    }

    // The other arm of finding 5's fix, and the one nothing else in this
    // file reaches: not "the tracker no longer knows this session at all"
    // (a null currentStateFor return, covered by the scan-level husk/Settle
    // cases) and not "cancelled" (the reactive CancelPendingFor path, which
    // never even reaches FirePending's validation loop) but "the tracker
    // still knows this session, and it has moved off waiting some other
    // way" — an ordinary approval landing inside the gap, with nobody
    // clicking Reset and nothing pruned. Nothing reactive catches this;
    // only a fresh read of the session's state at fire time can.
    [Fact]
    public async Task ADeferredAttentionIsDroppedWhenItsSessionHasMovedOffWaitingWithoutAManualReset()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start); // opens the gap

        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100),
            currentStateFor: id => id == "session-b" ? "generating" : "idle");

        await FireDueAndDrainAsync(start);

        lock (played)
        {
            Assert.Single(played); // just A's live Glass — B's Ping never played
        }
    }

    // The negative control the test above needs: currentStateFor reporting
    // a session is genuinely still "waiting" at fire time must NOT drop it
    // — otherwise this whole mechanism could not be told apart from "any
    // currentStateFor at all silences a pending attention," which the test
    // above alone cannot rule out.
    [Fact]
    public async Task ADeferredAttentionStillWaitingAtFireTimeStillPlays()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start); // opens the gap

        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100),
            currentStateFor: _ => "waiting"); // still exactly where it was deferred from

        await FireDueAndDrainAsync(start);

        lock (played)
        {
            Assert.Equal(2, played.Count); // A's Glass, then B's Ping
        }
    }

    // currentStateFor's "no longer waiting" check is attention-only — a
    // deferred Finished event is never subject to it, since a turn that
    // finished doesn't have a "waiting" state to fall out of. Exercises the
    // isAttention == false arm of that check, which the attention-only
    // cases above never reach.
    [Fact]
    public async Task ADeferredFinishedIsNeverSubjectToTheWaitingCheckCurrentStateForAdds()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start); // opens the gap

        TurnSounds.Deliver(
            new[] { Finished("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100),
            currentStateFor: _ => "idle"); // known, but nowhere near "waiting" — irrelevant for Finished

        await FireDueAndDrainAsync(start);

        lock (played)
        {
            Assert.Equal(2, played.Count); // both plays — B was never a candidate to be dropped
        }
    }

    // Round 3(c)'s ranking, but with the order finding 3's own test never
    // exercises: a Finished deferred first, an Attention from a different
    // session deferred second into the same coalesced wait. FirePending's
    // ranking loop has to promote the later attention over the earlier
    // finished, the same as Decide would for a live scan — insertion order
    // must never be mistaken for rank.
    [Fact]
    public async Task ADeferredAttentionOutranksAnEarlierDeferredFinishedRegardlessOfInsertionOrder()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start); // opens the gap
        TurnSounds.Deliver(new[] { Finished("key-x", "session-x") }, NoSummary, start.AddSeconds(0.3));
        TurnSounds.Deliver(new[] { NeedsAttention("key-y", "session-y") }, NoSummary, start.AddSeconds(0.6));

        await FireDueAndDrainAsync(start);

        lock (played)
        {
            Assert.Equal(2, played.Count); // A's live Glass, then the Ping
            Assert.Equal(
                SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultAttentionSoundName,
                    SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions),
                played[1]); // the attention, not session-x's finished
        }
    }

    // FirePending re-resolves through Snapshot() fresh, not from whenever
    // the event was first deferred (the same principle finding 4's re-read
    // of speechBusy rests on) — so a setting changed mid-gap can turn an
    // event that WAS audible when it was deferred into one that resolves to
    // nothing by the time its timer fires. Covers "best is null" out of a
    // non-empty, fully-valid `valid` list — distinct from the round-3(c)
    // "nothing survived validation" case, which never reaches resolution
    // at all.
    [Fact]
    public async Task ASettingChangedAfterDeferralCanMakeAPendingAttentionResolveToNothing()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null; // audible at defer time

        var start = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start); // opens the gap
        TurnSounds.Deliver(new[] { NeedsAttention("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100));

        // Muted after B was already deferred, before its timer fires.
        OrbweaverSettings.NeedsAttentionSound = "off";

        await FireDueAndDrainAsync(start);

        lock (played)
        {
            Assert.Single(played); // just A's live Glass — B resolved to nothing at fire time
        }
    }

    // The other arm of `var moment = now ?? DateTime.UtcNow` — every other
    // case in this file supplies `now` explicitly so its logical clock can
    // be driven independently of the real one; this is the one that lets
    // the default do the job, the shape SessionManager's own production
    // call site would take if it were ever invoked without a `now`.
    [Fact]
    public async Task DeliverWithNoExplicitNowUsesTheRealClock()
    {
        var played = new List<string>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = path =>
        {
            lock (played) played.Add(path);
            signal.TrySetResult(true);
        };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary); // now omitted entirely

        var winner = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(signal.Task, winner);
        Assert.Single(played);
    }

    // The other arm of `if (decision.Kind == SoundActionKind.Silent) return;`
    // inside Deliver's own lock block — every other case in this file
    // arrives at an audible decision. Muting the only signal a scan
    // produced is the ordinary way a live (non-deferred) decision comes
    // back Silent.
    [Fact]
    public void ALiveDecisionThatResolvesToSilentPlaysNothing()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = "off";

        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, Past);

        lock (played) Assert.Empty(played);
    }

    // QA round 2, finding 6, the deadlock team-lead asked to be ruled out
    // before this pushed: FirePending runs on the timer thread and calls
    // currentStateFor, which (SessionManager's real implementation)
    // dispatches onto the UI thread; Deliver runs ON the UI thread and now
    // takes Gate to decide-and-stamp. If FirePending still held Gate while
    // blocked inside that dispatch, a concurrent Deliver on the UI thread
    // would hang behind Gate while FirePending hangs behind the UI thread —
    // each waiting on the other. FirePending's own lock block only covers
    // the snapshot-and-clear; currentStateFor is called after that block
    // has already exited, so Gate is free the entire time the callback can
    // possibly stall. This proves that property directly rather than
    // trusting the source read: currentStateFor is made to block until
    // released, and a live Deliver call made while it is genuinely stalled
    // inside that callback must still complete promptly — if Gate were
    // held across the callback, it would instead hang until the callback
    // was released.
    [Fact]
    public async Task AConcurrentDeliverIsNeverBlockedBehindAStalledCurrentStateForCallback()
    {
        var callbackEntered = new TaskCompletionSource<bool>();
        var releaseCallback = new TaskCompletionSource<bool>();

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;
        ChimePlayer.PlayForTests = _ => { };

        var start = DateTime.UtcNow;

        // Opens the gap.
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, start);

        // Deferred, with a currentStateFor that blocks until this test
        // releases it — standing in for a real Dispatcher.UIThread.Invoke
        // stalled because the UI thread is busy with something else.
        string? StalledCurrentStateFor(string id)
        {
            callbackEntered.TrySetResult(true);
            releaseCallback.Task.Wait(TimeSpan.FromSeconds(5)); // bounded even if this test's own premise is wrong
            return "waiting";
        }

        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, start.AddMilliseconds(100),
            currentStateFor: StalledCurrentStateFor);

        // Confirms FirePending's real timer actually fired and is genuinely
        // stuck inside the callback, not merely still waiting to.
        var entered = await Task.WhenAny(callbackEntered.Task, Task.Delay(TimeSpan.FromSeconds(4)));
        Assert.Equal(callbackEntered.Task, entered);

        try
        {
            // A second, unrelated Deliver call, made while the callback
            // above is genuinely mid-stall — this is the call that hangs
            // if Gate is ever held across a UI-thread dispatch.
            var concurrent = Task.Run(() =>
                TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, DateTime.UtcNow));

            var finished = await Task.WhenAny(concurrent, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Equal(concurrent, finished);
        }
        finally
        {
            releaseCallback.TrySetResult(true);
        }
    }

    // QA round 3, finding 1, folded in from Marguerite's demonstrating test
    // (Cb167QaRound3DeliverTests.TwoPromptsInOneScanInsideTheGapStillPingWhenTheWinnerIsAnswered,
    // worktree cb-wt-turn-sounds-qa3): two prompts land in the SAME scan,
    // both inside the gap — b and d. Decide picks one winner (b) to be the
    // decision's own Kind/Path/PlayAt, and on 5370ac56 that meant only b
    // itself ever got remembered in the pending list; d, coalesced into the
    // very same scan, was never recorded anywhere. b then gets answered
    // (its tracked state moves off "waiting") before the timer fires, and
    // with nothing else on record, nothing plays at all — d's own,
    // perfectly valid Ping is lost along with the winner's. The fix pends
    // every non-None event from the scan, not just Decide's winner, and
    // lets FirePending's own re-rank sort out who actually plays.
    [Fact]
    public async Task TwoPromptsInOneScanInsideTheGapStillPingWhenTheWinnerIsAnswered()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap

        // b and d arrive together, in one scan, both inside the gap.
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b"), NeedsAttention("key-d", "session-d") },
            NoSummary, t0.AddSeconds(0.5),
            currentStateFor: id => id == "session-b" ? "generating" : "waiting"); // b answered, d still waiting

        await FireDueAndDrainAsync(t0);

        lock (played)
        {
            Assert.Contains(
                SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultAttentionSoundName,
                    SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions),
                played); // d's Ping, even though b (the scan's own winner) was answered first
        }
    }

    // QA round 3, finding 1's other half, folded in from Marguerite's
    // demonstrating test (Cb167QaRound3DeliverTests.AFinishCoalescedBehind-
    // AnAnsweredPromptStillChimes): a finish (c) and a prompt (b) land in
    // the same scan, both inside the gap. Attention outranks Finished, so
    // Decide's winner is b — and on 5370ac56 that meant c's own Finished
    // signal, coalesced into the same scan, was never pended at all. b gets
    // answered before the timer fires, and c's chime — never recorded
    // anywhere — is gone along with it.
    [Fact]
    public async Task AFinishCoalescedBehindAnAnsweredPromptStillChimes()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap

        // c (finished) and b (attention) arrive together, in one scan, both
        // inside the gap; b outranks c as Decide's own winner.
        TurnSounds.Deliver(
            new[] { Finished("key-c", "session-c"), NeedsAttention("key-b", "session-b") },
            NoSummary, t0.AddSeconds(0.5),
            currentStateFor: id => id == "session-b" ? "generating" : "idle"); // b answered, c just known

        await FireDueAndDrainAsync(t0);

        var finishedSound = SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultFinishedSoundName,
            SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        lock (played)
        {
            // A's own live Glass, plus c's — the coalescing winner (b)
            // being answered must never have carried c down with it.
            Assert.Equal(2, played.Count(p => p == finishedSound));
        }
    }

    // Round 4, pre-built for round 5: pending the whole scan (finding 1's
    // fix, above) must never turn one scan into two sounds. FirePending's
    // ranking picks exactly one `best` and calls Execute once — this proves
    // that holds now that a scan's *coalesced siblings* are pended too, not
    // just its winner, since that is exactly the change that could have
    // made two valid survivors both play instead of one being chosen.
    [Fact]
    public async Task TwoValidCoalescedEventsFromOneScanStillProduceOnlyOneSound()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap

        // b and d, together in one scan, both still genuinely waiting at
        // fire time — neither answered, neither cancelled.
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b"), NeedsAttention("key-d", "session-d") },
            NoSummary, t0.AddSeconds(0.5), currentStateFor: _ => "waiting");

        await FireDueAndDrainAsync(t0);

        // A's live Glass, plus exactly one Ping — never two.
        lock (played) Assert.Equal(2, played.Count);
    }

    // The other half: a scan whose events are ALL answered prompts by fire
    // time must play nothing at all, not fall through to playing one of
    // them anyway. Distinct from ACoalescedAttentionPlaysNothingWhenBoth-
    // OfItsSessionsAreCancelled above, which drops both reactively via
    // CancelPendingFor before FirePending's validation loop ever runs this
    // scan's events through currentStateFor at all; this is the same
    // "every survivor invalid" outcome reached purely through the
    // currentStateFor arm, the shape an ordinary pair of approvals inside
    // one gap actually takes.
    [Fact]
    public async Task AScanWhoseEventsAreAllAnsweredPromptsPlaysNothing()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap

        // b and d, together in one scan, BOTH answered by fire time.
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b"), NeedsAttention("key-d", "session-d") },
            NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ => "generating"); // neither is "waiting" any more

        await FireDueAndDrainAsync(t0);

        // Just A's own live Glass — nothing from a scan with nothing left
        // valid in it.
        lock (played) Assert.Single(played);
    }

    // QA round 4, folded in from Marguerite's regression tests (worktree
    // cb-wt-turn-sounds-qa4, read-only reference, not written to): a
    // richer version of round 3's own coalescing check — four events in
    // ONE deferred scan (two finished, two attention), two of the four
    // answered by fire time. Exactly one sound plays, and it is the
    // survivor with the higher rank, not merely "a" survivor.
    [Fact]
    public async Task ADeferredScanOfSeveralEventsPlaysExactlyOneSoundAndItIsTheAttention()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap

        TurnSounds.Deliver(
            new[]
            {
                Finished("key-c", "session-c"), NeedsAttention("key-b", "session-b"),
                NeedsAttention("key-d", "session-d"), Finished("key-e", "session-e"),
            },
            NoSummary, t0.AddSeconds(0.5),
            currentStateFor: id => id is "session-b" or "session-d" ? "waiting" : "idle");

        await FireDueAndDrainAsync(t0);

        var attentionSound = SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultAttentionSoundName,
            SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        lock (played)
        {
            Assert.Equal(2, played.Count); // A's own live Glass, then exactly one for the deferred scan
            Assert.Equal(attentionSound, played[1]);
        }
    }

    // QA round 4, folded in from the same worktree: the same shape as
    // AScanWhoseEventsAreAllAnsweredPromptsPlaysNothing above, kept as its
    // own regression test since it is Marguerite's own literal repro
    // rather than a case I wrote independently.
    [Fact]
    public async Task ADeferredScanOfOnlyAnsweredPromptsPlaysNothing()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0); // opens the gap

        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b"), NeedsAttention("key-d", "session-d") },
            NoSummary, t0.AddSeconds(0.5), currentStateFor: _ => "generating");

        await FireDueAndDrainAsync(t0);

        lock (played) Assert.Single(played);
    }

    // QA round 4, folded in from the same worktree: a pending sound firing
    // and a live one from the very next regular scan — landing only
    // milliseconds after the pending timer, since a real scan runs on its
    // own cadence independent of when FirePending happens to fire — must
    // never land closer together than the rate limit itself. Proves
    // FirePending's own Execute call stamps _lastPlayed for real, so the
    // very next live Deliver sees a recent stamp and defers rather than
    // doubling up.
    [Fact]
    public async Task APendingSoundAndALiveOneAreNotPlayedInsideOneGap()
    {
        var played = new List<(DateTime At, string Path)>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add((DateTime.UtcNow, path)); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ => "waiting"); // pending, fires at roughly t0+2 (real time)

        await Task.Delay(TimeSpan.FromSeconds(2.05));
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, DateTime.UtcNow); // the next regular scan
        await Task.Delay(TimeSpan.FromSeconds(1));

        List<DateTime> starts;
        lock (played) starts = played.Select(p => p.At).OrderBy(t => t).ToList();
        for (var i = 1; i < starts.Count; i++)
        {
            Assert.True(starts[i] - starts[i - 1] >= TimeSpan.FromSeconds(1.9),
                $"sounds {i} and {i + 1} started {(starts[i] - starts[i - 1]).TotalMilliseconds:F0} ms apart");
        }
    }

    // CB-240. The gap is enforced for a live scan (Decide) but a deferred sound
    // that fires does not re-check it: its only protection is that its timer was
    // armed for lastPlayed + MinimumGap. A live scan that lands after the
    // pending sound is due but before the timer's callback runs -- thread-pool
    // latency, which loaded machines have plenty of -- sees the gap closed and
    // plays at once; the callback then plays the pending sound on top of it.
    //
    // Forced rather than waited for: the live scan is delivered first, then the
    // timer's callback is invoked by hand, which is exactly that ordering. No
    // sleeps, no dependence on how fast this machine is.
    [Fact]
    public async Task APendingSoundThatFiresLateDoesNotPlayOnTopOfALiveOneThatLandedFirst()
    {
        var played = new List<(DateTime At, string Path)>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add((DateTime.UtcNow, path)); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);   // plays, stamps t0
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ => "waiting");                                          // deferred to t0+2

        // A regular scan after the pending sound's PlayAt, before its timer ran.
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, t0.AddSeconds(2.05));

        TurnSounds.FirePending();                                                      // the timer, late
        await TurnSounds.ChimesEnqueuedSoFar();

        List<DateTime> starts;
        lock (played) starts = played.Select(p => p.At).OrderBy(t => t).ToList();
        Assert.Equal(2, starts.Count);   // key-a and key-c; key-b waits for its own gap
    }

    // CB-240, the second window. FirePending releases the lock after reading
    // what is pending and only stamps _lastPlayed once it has validated and
    // chosen; a live scan that lands in between finds the gap still open and
    // plays. currentStateFor runs in exactly that stretch, outside the lock, so
    // delivering the live scan from inside it forces the ordering with no timing.
    [Fact]
    public async Task ALiveScanThatLandsWhileAPendingSoundIsBeingValidatedDoesNotDoubleUp()
    {
        var played = new List<(DateTime At, string Path)>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add((DateTime.UtcNow, path)); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        var slippedIn = false;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);   // plays, stamps t0
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ =>
            {
                if (!slippedIn)
                {
                    slippedIn = true;
                    // A regular scan, after the pending sound's PlayAt, arriving mid-validation.
                    TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, NoSummary, t0.AddSeconds(2.05));
                }
                return "waiting";
            });

        TurnSounds.FirePending();
        await TurnSounds.ChimesEnqueuedSoFar();

        Assert.True(slippedIn);
        lock (played) Assert.Equal(2, played.Count);   // key-a and key-c; key-b waits for its own gap
    }

    // CB-240, starvation. Re-deferring alone would let a live scan that keeps
    // beating the re-armed timer push a pending sound back every gap. The rule:
    // pushed back once, it goes first, and a live scan arriving while it waits
    // joins it. So here, b is pushed back by c1, and plays at the next gap
    // instead of yielding to c2.
    [Fact]
    public async Task APendingSoundPushedBackOnceGoesFirstAtTheNextGap()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);   // plays
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ => "waiting");                                          // deferred to t0+2

        // Gap 1: a live scan lands first and plays; the late timer finds it and yields.
        TurnSounds.Deliver(new[] { Finished("key-c1", "session-c1") }, NoSummary, t0.AddSeconds(2.05));
        TurnSounds.FirePending(t0.AddSeconds(2.06));
        await TurnSounds.ChimesEnqueuedSoFar();
        lock (played) Assert.Equal(2, played.Count);   // a, c1 -- b has been pushed back once

        // Gap 2: another live scan arrives before the timer. It must not play
        // ahead of b a second time.
        TurnSounds.Deliver(
            new[] { Finished("key-c2", "session-c2") }, NoSummary, t0.AddSeconds(4.10),
            currentStateFor: _ => "waiting");
        await TurnSounds.ChimesEnqueuedSoFar();        // anything c2 enqueued has played by here
        lock (played) Assert.Equal(2, played.Count);   // c2 joined b rather than playing live

        TurnSounds.FirePending(t0.AddSeconds(4.11));
        await TurnSounds.ChimesEnqueuedSoFar();
        lock (played)
        {
            Assert.Equal(3, played.Count);
            Assert.NotEqual(played[0], played[2]);   // b's attention sound, not another finish: it ranks over c2
        }
    }

    // CB-240, the summary half, stated rather than argued. A chime's slot is
    // claimed the moment it is decided. A summary's is claimed when it is heard
    // (a summary that finds no text must not spend the rate limit on silence),
    // so a pending chime that falls due while a live summary is still being
    // prepared PLAYS: nothing has been heard yet to be inside a gap of. That is
    // also what a live chime scan does in the same state, and it is the stated
    // rule, not an accident; the "waits at most one extra gap" guarantee is
    // about being pushed back by a sound that has been claimed.
    [Fact]
    public async Task APendingChimeFiresWhileALiveSummaryIsStillBeingPrepared()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);   // plays, stamps t0
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ => "waiting");                                          // deferred to t0+2

        // A finished turn whose summary is still being prepared: decided live,
        // not yet heard, so not yet stamped.
        OrbweaverSettings.TurnFinishedSound = "summary";
        var preparing = new TaskCompletionSource<bool>();
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, _ => preparing.Task, t0.AddSeconds(2.05));

        TurnSounds.FirePending(t0.AddSeconds(2.06));   // the pending chime's timer
        await TurnSounds.ChimesEnqueuedSoFar();
        lock (played) Assert.Equal(2, played.Count);   // a, and b -- not held back behind an unheard summary

        preparing.SetResult(true);                     // the summary is heard; no extra chime for it
        await TurnSounds.ChimesEnqueuedSoFar();
        lock (played) Assert.Equal(2, played.Count);
    }

    // CB-240. The late summary stamp is written with the moment the summary was
    // DECIDED. If a chime stamped later in between, that is an older moment, and
    // writing it back would reopen a gap the chime had just closed: the next
    // scan, which should have deferred, plays. Forced with a scan 5 ms inside the
    // chime's gap but past the summary's.
    [Fact]
    public async Task ALateSummaryStampDoesNotReopenAGapAChimeJustClosed()
    {
        var played = new List<string>();
        ChimePlayer.PlayForTests = path => { lock (played) played.Add(path); };

        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = null;
        OrbweaverSettings.NeedsAttentionSound = null;

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, NoSummary, t0);
        TurnSounds.Deliver(
            new[] { NeedsAttention("key-b", "session-b") }, NoSummary, t0.AddSeconds(0.5),
            currentStateFor: _ => "waiting");

        OrbweaverSettings.TurnFinishedSound = "summary";
        var preparing = new TaskCompletionSource<bool>();
        TurnSounds.Deliver(new[] { Finished("key-c", "session-c") }, _ => preparing.Task, t0.AddSeconds(2.05));
        TurnSounds.FirePending(t0.AddSeconds(2.06));   // b plays and stamps t0+2.06
        await TurnSounds.ChimesEnqueuedSoFar();

        preparing.SetResult(true);                     // the summary's late stamp: t0+2.05, older than b's
        await TurnSounds.ChimesEnqueuedSoFar();

        // 5 ms short of b's gap closing (t0+4.06), but past t0+2.05's (t0+4.05).
        OrbweaverSettings.TurnFinishedSound = null;
        TurnSounds.Deliver(new[] { Finished("key-d", "session-d") }, NoSummary, t0.AddSeconds(4.055));
        await TurnSounds.ChimesEnqueuedSoFar();

        lock (played) Assert.Equal(2, played.Count);   // d is deferred, not played
    }

    // CB-240. FirePending reads its pending state under the lock, validates and
    // chooses outside it, then re-takes the lock to check the gap and claim the
    // slot. A reset in between (a generation bump) means what it chose belongs to
    // a world that no longer exists, so it plays nothing. A chime would be
    // dropped anyway by EnqueueChime's own generation check; a SUMMARY has no
    // such check on its speech call, so a summary is what shows the guard is
    // needed. currentStateFor runs in exactly that stretch, outside the lock, so
    // resetting from inside it forces the ordering.
    [Fact]
    public async Task APendingSummaryIsNotSpokenWhenAResetLandsBetweenChoosingAndClaiming()
    {
        ChimePlayer.PlayForTests = _ => { };
        OrbweaverSettings.TurnSoundsEnabled = true;
        OrbweaverSettings.TurnFinishedSound = "summary";

        var spokenFor = new List<string>();
        Task<bool> Speak(string sessionId)
        {
            lock (spokenFor) spokenFor.Add(sessionId);
            return Task.FromResult(true);
        }

        var t0 = DateTime.UtcNow;
        TurnSounds.Deliver(new[] { Finished("key-a", "session-a") }, Speak, t0);        // spoken, stamps t0
        TurnSounds.Deliver(
            new[] { Finished("key-b", "session-b") }, Speak, t0.AddSeconds(0.5),
            currentStateFor: _ => { TurnSounds.ResetForTests(); return "idle"; });     // deferred to t0+2
        lock (spokenFor) Assert.Equal(new[] { "session-a" }, spokenFor);

        TurnSounds.FirePending(t0.AddSeconds(2.06));
        await TurnSounds.ChimesEnqueuedSoFar();

        lock (spokenFor) Assert.Equal(new[] { "session-a" }, spokenFor);   // b was chosen, then the reset dropped it
    }
}
