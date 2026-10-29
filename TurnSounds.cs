using System.Threading;

namespace Orbweaver
{
    // The one call SessionManager makes at the end of a scan to turn whatever
    // TurnSignalTracker noticed into an actual sound.
    //
    // Everything that decides *whether* to make a noise for one scan's worth
    // of signals lives in TurnSoundPolicy, pure and covered by its own tests
    // with no clock but the one it is handed. This class is the seam between
    // that decision and the world: it reads the settings the decision needs,
    // remembers when this app last actually made a sound, holds whichever
    // signals are still genuinely waiting on the rate limit, and carries out
    // whichever of Silent/Chime/Summary the survivors resolve to once the
    // gap opens. Kept this thin on purpose — a bug in "should this play"
    // belongs in a pure function with a name, not folded into the one place
    // that also touches a settings file, a timer and a speaker.
    internal static class TurnSounds
    {
        private static readonly object Gate = new();

        // When this app last actually delivered a sound — a chime played, or
        // a summary was handed off to speak. Process-wide rather than
        // per-session, because the rate limit the plan asks for is on the
        // app making noise at all, not on any one orb repeating itself
        // (TurnSignalTracker already owns that half).
        private static DateTime _lastPlayed = DateTime.MinValue;

        // QA round 2: every deferred event still genuinely waiting on the
        // gap, not just the single latest one. One slot was never enough —
        // it let a later, lower-ranked decision silently erase an earlier,
        // still-valid one (finding 3: a pending attention replaced by a
        // later finished), and it had nowhere to keep a *second* attention
        // from a different session once one was already pending, which is
        // exactly the shape "two prompts land inside one gap and coalesce
        // into a single Ping" (round 3c) needs: if the session this slot
        // currently remembers gets Settled or Pruned, the other one must
        // still be there to fall back to. A session's own newer signal
        // replaces its own older entry; it never displaces a different
        // session's.
        private static readonly List<TurnSoundEvent> _pendingEvents = new();
        private static Func<string, Task<bool>>? _pendingSpeak;

        // QA round 2 (finding 5): asked again at fire time, not trusted from
        // whenever each event was first deferred — a session id in, its
        // current tracked state out, or null if the tracker no longer holds
        // it at all. SessionManager supplies this from _statuses; null
        // (the default) means "don't ask," which is what every case that
        // doesn't care about this validation, including most of this
        // file's own tests, gets for free.
        private static Func<string, string?>? _pendingCurrentStateFor;

        private static Timer? _pendingTimer;

        // When the armed timer is due, and whether a live sound has already
        // pushed this pending sound back once (CB-240). Without the second, a
        // live scan that keeps beating the re-armed timer could push a pending
        // sound back every gap for as long as sounds keep arriving. Pushed back
        // once, it goes first: see Deliver.
        private static DateTime _pendingPlayAt;
        private static bool _pendingPushedBack;

        // QA (CB-167): playback is chained onto this rather than fired
        // independently per decision. The 2 s rate limit only spaces out
        // when a new sound is *decided*; it says nothing about how long the
        // previous one takes to actually finish, and a user's own chosen
        // file can run the full 5 s cap ChimePlayer allows. Two decisions
        // landing 2.1 s apart — perfectly legal under the rate limit — could
        // otherwise start a second afplay/PlaySync while the first was still
        // running. Chaining makes "the next chime waits for the previous one
        // to actually finish" true regardless of how close together two
        // decisions land; TaskScheduler.Default keeps each link off
        // whatever thread scheduled it.
        private static Task _chimeChain = Task.CompletedTask;

        // Bumped only by ResetForTests. Replacing _chimeChain and disposing
        // the timer there does not stop work that is already queued: a
        // continuation on the old chain, a timer callback already running,
        // or a summary fallback still awaiting its answer carries on, and
        // reaches ChimePlayer.Play (and so whichever test's PlayForTests
        // seam is installed by then) after the reset. CI caught exactly
        // that: CancelPendingForADifferentSessionLeavesTheRealPendingSignalAlone
        // saw two Glass chimes where it had caused one. Every such piece of
        // work captures the generation it was started under and drops
        // itself if a reset has happened since. Production never resets, so
        // this never changes what a user hears.
        private static int _generation;

        // Test seam: a scan-level test wants a clean rate-limit clock and no
        // timer left armed from a previous case, without sleeping two real
        // seconds to clear either — the same reason
        // OrbweaverSettings.ReloadForTests exists.
        // The tail of the chime chain as it stands, so a test can await "everything
        // enqueued so far has played" instead of sleeping.
        internal static Task ChimesEnqueuedSoFar()
        {
            lock (Gate) return _chimeChain;
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _lastPlayed = DateTime.MinValue;
                ClearPendingLocked();
                _chimeChain = Task.CompletedTask;
                _generation++;
            }
        }

        // QA (CB-167), reworked in round 2 for the event-list model: called
        // from a session's own removal — pruned from the scan (a husk,
        // backgrounded or genuinely gone) or Settled by a manual reset — so
        // a deferred signal for that exact session can never fire late for
        // an orb that has already stopped meaning anything. Removes only
        // that session's own entries; a different session's pending event,
        // coalesced into the same wait, survives untouched (round 3c) — the
        // timer is only actually cancelled once nothing valid is left.
        internal static void CancelPendingFor(string sessionId)
        {
            lock (Gate)
            {
                _pendingEvents.RemoveAll(e => e.SessionId == sessionId);
                if (_pendingEvents.Count == 0) ClearPendingLocked();
            }
        }

        // The Prune-shaped version of the same guard: whatever the scan no
        // longer sees this pass is exactly what Prune is about to drop from
        // the tracker, and a pending event for any of them is stale for the
        // identical reason. Called with the same `seen` set
        // ScanAndUpdateCore already built, so this costs nothing extra to
        // compute.
        internal static void CancelPendingUnlessSeen(IReadOnlySet<string> seen)
        {
            lock (Gate)
            {
                _pendingEvents.RemoveAll(e => !seen.Contains(e.SessionId));
                if (_pendingEvents.Count == 0) ClearPendingLocked();
            }
        }

        // `trySpeakTurnSummary` both speaks (when it can) and reports
        // whether it did, entirely off whatever thread called Deliver —
        // SessionManager's real callback resolves to
        // OrbWindow.SpeakTurnSummaryAsync, which is where "found no text" or
        // "not an orb kind with a transcript" actually gets decided.
        // TurnSoundPolicy never sees any of this; it only ever names the
        // winning session id.
        //
        // `currentStateFor` is QA round 2's fire-time re-validation hook —
        // optional, and null (the default) for any caller that doesn't need
        // FirePending to re-check a pending attention's session against its
        // live tracked state before playing it.
        //
        // `now` keeps its position as the third, positional argument on
        // purpose — every existing call site (production and test) already
        // passes it that way, and `currentStateFor` is the newer, optional
        // addition, so it goes last rather than forcing every one of those
        // call sites to switch to named arguments just to keep compiling.
        internal static void Deliver(
            IReadOnlyList<TurnSoundEvent> events,
            Func<string, Task<bool>> trySpeakTurnSummary,
            DateTime? now = null,
            Func<string, string?>? currentStateFor = null)
        {
            if (events.Count == 0) return;

            var moment = now ?? DateTime.UtcNow;
            SoundAction decision;
            int generation;

            // QA round 2 (finding 6): Decide reads _lastPlayed to ask
            // whether the gap is still closed, and FirePending — running on
            // the timer's own thread — can be resolving and stamping at the
            // same real moment. Reading a decision and then, separately,
            // acting on it is a classic check-then-act race once a second
            // thread can also write the thing that was checked; deciding
            // and (for a live Chime, which is certain to play) stamping now
            // both happen under the one lock below, so nothing else can
            // write _lastPlayed in between.
            lock (Gate)
            {
                generation = _generation;
                decision = TurnSoundPolicy.Decide(events, Snapshot(), moment, _lastPlayed, TextToSpeech.IsSpeaking);

                if (decision.Kind == SoundActionKind.Silent) return;

                // A pending sound that a live one has already pushed back once
                // does not yield again: this scan joins it instead of playing
                // ahead of it, and FirePending ranks the lot as it does for any
                // scan that lands inside a gap (attention over finished, one
                // sound per gap). So a pending sound waits at most one extra gap
                // however many live scans keep arriving (CB-240).
                if (!decision.IsDeferred && _pendingPushedBack && _pendingEvents.Count > 0)
                {
                    SchedulePendingLocked(events, _pendingPlayAt, trySpeakTurnSummary, currentStateFor);
                    return;
                }

                if (decision.IsDeferred)
                {
                    // QA round 3, finding 1: every non-None event from this
                    // scan is pended, not just the one Decide picked as the
                    // winner. Pending only the winner lost the others for
                    // good — two prompts landing in the same scan inside
                    // the gap coalesced down to Decide's single choice, so
                    // if that one got answered before its timer fired, the
                    // other had no record anywhere to fall back to and
                    // never Pinged; the same held for a finish sharing a
                    // scan with a prompt that outranked it. FirePending
                    // already re-ranks whatever is still valid at fire
                    // time, so pending the whole scan and trusting that
                    // re-rank is what actually lets a coalesced sibling
                    // survive the winner being answered.
                    SchedulePendingLocked(events, decision.PlayAt!.Value, trySpeakTurnSummary, currentStateFor);
                    return;
                }

                // QA round 2 (finding 3): a live decision never touches
                // _pendingEvents — the old ClearPending() call here used to
                // wipe out a still-genuinely-pending attention the moment
                // any unrelated live decision arrived. A live decision and
                // whatever is still waiting on its own gap are unrelated;
                // both get to happen.
                //
                // A live Chime is certain to play, so it is stamped right
                // here. A live Summary is not — whether anything is
                // actually said depends on trySpeakTurnSummary's answer,
                // not known synchronously — so it keeps stamping later, in
                // SpeakSummaryOrFallbackAsync, once that answer is in.
                if (decision.Kind == SoundActionKind.Chime) _lastPlayed = moment;
            }

            Execute(decision, moment, trySpeakTurnSummary, generation);
        }

        // Callable only while already holding Gate. `events` is the whole
        // scan's worth, not just Decide's winner — a session's own newer
        // signal still replaces its own older entry (a scan never reports
        // two signals for the same session, so this only ever matters
        // across separate Deliver calls), and every other session's own
        // entry is left alone, same as before.
        private static void SchedulePendingLocked(
            IReadOnlyList<TurnSoundEvent> events, DateTime playAt,
            Func<string, Task<bool>> trySpeakTurnSummary, Func<string, string?>? currentStateFor)
        {
            foreach (var ev in events)
            {
                if (ev.Signal == TurnSignal.None) continue;
                _pendingEvents.RemoveAll(e => e.SessionId == ev.SessionId);
                _pendingEvents.Add(ev);
            }

            _pendingSpeak = trySpeakTurnSummary;
            _pendingCurrentStateFor = currentStateFor;
            _pendingPlayAt = playAt;

            var delay = playAt - DateTime.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            // Re-armed on every call rather than left alone when one is
            // already running — harmless, since every event contributing to
            // one pending window shares the same PlayAt (they were all
            // deferred against the same _lastPlayed), and simpler than
            // reasoning about when it would be safe to skip.
            _pendingTimer?.Dispose();
            _pendingTimer = new Timer(_ => FirePending(), null, delay, Timeout.InfiniteTimeSpan);
        }

        // Callable only while already holding Gate.
        private static void ClearPendingLocked()
        {
            _pendingEvents.Clear();
            _pendingSpeak = null;
            _pendingCurrentStateFor = null;
            _pendingTimer?.Dispose();
            _pendingTimer = null;
            _pendingPushedBack = false;
        }

        // Runs on the timer's own thread-pool callback thread — never the
        // Avalonia UI thread, so Execute below is free to do exactly what it
        // would from a live Deliver call.
        // `now` is the same kind of seam Deliver's is: the timer passes nothing and
        // gets the real clock, a test passes the logical one it delivered with.
        internal static void FirePending(DateTime? now = null)
        {
            List<TurnSoundEvent> events;
            Func<string, Task<bool>>? speak;
            Func<string, string?>? currentStateFor;
            int generation;
            lock (Gate)
            {
                // Read with the events it belongs to: a reset after this
                // point is caught by EnqueueChime's own check, and a reset
                // before it has already emptied _pendingEvents.
                generation = _generation;
                events = new List<TurnSoundEvent>(_pendingEvents);
                speak = _pendingSpeak;
                currentStateFor = _pendingCurrentStateFor;
                ClearPendingLocked();
            }

            // QA round 3: `speak is null` was never a real second condition
            // here — _pendingSpeak is set in SchedulePendingLocked and
            // cleared in ClearPendingLocked in the same breath as
            // _pendingEvents, so it is never null while events is non-empty.
            // The only genuine race this guard has to cover is a timer
            // callback already in flight losing a race to a concurrent
            // CancelPendingFor/CancelPendingUnlessSeen that empties the list
            // (and disposes this very timer) first.
            if (events.Count == 0) return;
            var trySpeak = speak!;

            // QA round 2 (finding 5, round 3c): re-validated now, against
            // live state, not trusted from whenever each event was first
            // deferred. A session the tracker no longer holds at all
            // (pruned, or the status file simply gone) drops out entirely.
            // An attention whose session has since moved off "waiting" by
            // some path other than the explicit Settle/Prune calls above —
            // the prompt was answered normally, mid-gap, before this timer
            // ever fired — drops out too; CancelPendingFor only catches a
            // *manual* reset, not an ordinary approval. Two attentions from
            // different sessions, coalesced into this one wait, are checked
            // independently: only once every contributing event has failed
            // this does nothing play.
            var valid = new List<TurnSoundEvent>(events.Count);
            foreach (var ev in events)
            {
                if (currentStateFor is not null)
                {
                    var state = currentStateFor(ev.SessionId);
                    if (state is null) continue;

                    if (ev.Signal == TurnSignal.NeedsAttention
                        && !string.Equals(state, "waiting", StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                valid.Add(ev);
            }

            if (valid.Count == 0) return;

            var settings = Snapshot();

            // QA round 2 (finding 4): sampled now, not carried over from
            // whatever it was when this was first deferred — the plan's own
            // rule is that a summary never interrupts speech already
            // playing, and speech that started *during* the gap is exactly
            // as "already playing" as speech that started before it.
            var speechBusy = TextToSpeech.IsSpeaking;

            // The same ranking Decide itself uses, re-run over whoever
            // survived the filter above: attention outranks finished, and
            // within one rank the first survivor (scan order, preserved by
            // the list) keeps winning.
            SoundAction? best = null;
            var bestIsAttention = false;

            foreach (var ev in valid)
            {
                var resolved = TurnSoundPolicy.ResolveOne(ev, settings, speechBusy);
                if (resolved.Kind == SoundActionKind.Silent) continue;

                var isAttention = ev.Signal == TurnSignal.NeedsAttention;
                if (best is null || (isAttention && !bestIsAttention))
                {
                    best = resolved;
                    bestIsAttention = isAttention;
                }
            }

            if (best is not { } chosen) return;

            // The gap, enforced here as well as in Decide (CB-240). Decide only
            // ever sees a live scan, so a deferred sound's sole protection was
            // that its timer was armed for lastPlayed + MinimumGap -- and a live
            // scan that landed after that moment but before this callback ran
            // (thread-pool latency, which load inflates) found the gap closed,
            // played at once and stamped, and then this played on top of it:
            // two sounds milliseconds apart, which is what the limit exists to
            // prevent. The same hole sat between the first lock above and the
            // stamp in PlayChimeInBackground, where a live Deliver could slip in.
            //
            // So the check and, for a chime, the claim on _lastPlayed happen
            // under one lock. A summary is not stamped here -- whether anything
            // is spoken is only known later, in SpeakSummaryOrFallbackAsync --
            // but it is still held to the gap.
            var firedAt = now ?? DateTime.UtcNow;
            lock (Gate)
            {
                if (generation != _generation) return;

                var earliest = _lastPlayed + TurnSoundPolicy.MinimumGap;
                if (firedAt < earliest)
                {
                    // Something else played first. What survived waits for its
                    // own gap again; a session that has a newer pending entry
                    // by now keeps that one rather than this older one.
                    var stillOwed = valid.Where(ev => !_pendingEvents.Any(p => p.SessionId == ev.SessionId)).ToList();
                    if (stillOwed.Count > 0)
                    {
                        SchedulePendingLocked(stillOwed, earliest, trySpeak, currentStateFor);
                        _pendingPushedBack = true;
                    }
                    return;
                }

                if (chosen.Kind == SoundActionKind.Chime) _lastPlayed = firedAt;
            }

            Execute(chosen, firedAt, trySpeak, generation);
        }

        // Carries out a decision that is ready to play right now — never
        // called with a still-deferred one. Fire-and-forget from here down:
        // this is reached from ScanAndUpdateCore, which CB-106 keeps on the
        // Avalonia UI thread, and the whole point of everything below is
        // that nothing may block it. Exceptions are caught and logged rather
        // than thrown, the same reason SpeechSummary.SummarizeOrSayWhyAsync
        // turns a throw into a sentence instead of letting it escape.
        private static void Execute(SoundAction decision, DateTime moment, Func<string, Task<bool>> trySpeakTurnSummary, int generation)
        {
            if (decision.Kind == SoundActionKind.Summary)
            {
                _ = SpeakSummaryOrFallbackAsync(decision.SessionId!, moment, trySpeakTurnSummary, generation);
            }
            else
            {
                PlayChimeInBackground(decision.Path!, moment, generation);
            }
        }

        private static void PlayChimeInBackground(string path, DateTime moment, int generation)
        {
            // Stamped here too, harmlessly redundant with Deliver's own
            // upfront stamp for the live path — FirePending's callers never
            // stamp beforehand, so this is the only place that call chain
            // gets one at all. Once a Chime decision is being carried out,
            // it is certain to play (ChimePlayer's own failure modes — a
            // process that won't start — were already true before this fix
            // and are not this fix's business). The summary path below is
            // the one that has to wait and see.
            lock (Gate) _lastPlayed = moment;

            EnqueueChime(path, generation);
        }

        // Every real chime — the direct path here and the vibe-summary
        // fallback below — goes through this one chain, which is what
        // guarantees the two can never overlap either: a fallback chime
        // starting while an unrelated orb's chime is still mid-playback
        // would be exactly the same rattle two ordinary chimes landing
        // close together would be.
        private static void EnqueueChime(string path, int generation)
        {
            lock (Gate)
            {
                _chimeChain = _chimeChain.ContinueWith(_ =>
                {
                    lock (Gate)
                    {
                        if (generation != _generation) return;
                    }

                    try
                    {
                        ChimePlayer.Play(path);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"{Brand.DisplayName}: couldn't play a turn sound: {ex.Message}");
                    }
                }, TaskScheduler.Default);
            }
        }

        // The two late stamps below are written seconds after the decision they
        // belong to, and a chime may have stamped in between (a pending one
        // falling due while the summary was being prepared, CB-240). Writing the
        // older moment over a newer stamp would reopen a gap that chime had
        // just closed, so a late stamp only ever moves _lastPlayed forward.
        private static void StampNoEarlierLocked(DateTime moment)
        {
            if (moment > _lastPlayed) _lastPlayed = moment;
        }

        // Off the UI thread for its whole life: trySpeakTurnSummary walks a
        // transcript file and, for a gateway session, awaits a round trip
        // over the wire — neither belongs on the thread ScanAndUpdateCore
        // runs on. _lastPlayed is only stamped once something is actually
        // going to be heard: a summary attempt that found no text must not
        // spend the rate limit on silence, which is why this waits for the
        // answer before touching it, unlike the chime path above, which
        // already knows it will play the moment it is called.
        private static async Task SpeakSummaryOrFallbackAsync(
            string sessionId, DateTime moment, Func<string, Task<bool>> trySpeakTurnSummary, int generation)
        {
            try
            {
                var spoke = await trySpeakTurnSummary(sessionId).ConfigureAwait(false);
                if (spoke)
                {
                    lock (Gate) StampNoEarlierLocked(moment);
                    return;
                }

                // No speakable text — a RemoteControl or ClaudeCloud orb, or
                // a local one whose transcript has no assistant turn in it
                // yet — is not "nothing happened." The turn still finished,
                // so this falls back to the ordinary chime rather than the
                // user hearing nothing at all for it.
                var fallback = Snapshot().ResolveFinishedSound(null);
                if (fallback is null) return;   // even the platform default is missing on this machine

                lock (Gate) StampNoEarlierLocked(moment);
                EnqueueChime(fallback, generation);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Brand.DisplayName}: couldn't speak a turn summary: {ex.Message}");
            }
        }

        // Read once per scan rather than once per event — the settings file
        // does not change mid-scan, and TurnSoundPolicy.Decide only ever
        // needs one snapshot regardless of how many sessions signalled.
        // FirePending calls this again at fire time rather than reusing
        // whatever Snapshot said when an event was first deferred, for the
        // identical reason it re-samples speechBusy: settings, like speech
        // state, can have moved on in the meantime.
        //
        // The two resolver closures are where "null means the platform
        // default" actually gets resolved into a name: SystemSoundCatalog
        // itself has no opinion on which trigger it is being asked about, so
        // that substitution happens here, once, rather than being duplicated
        // wherever a resolver gets called.
        private static SoundSettingsSnapshot Snapshot()
        {
            var directory = SystemSoundCatalog.DefaultDirectory;
            var extensions = SystemSoundCatalog.DefaultExtensions;

            return new SoundSettingsSnapshot(
                MasterEnabled: OrbweaverSettings.TurnSoundsEnabled,
                DefaultFinishedSetting: OrbweaverSettings.TurnFinishedSound,
                DefaultAttentionSetting: OrbweaverSettings.NeedsAttentionSound,
                OverrideFor: OrbweaverSettings.OrbTurnSoundFor,
                ResolveFinishedSound: setting => SystemSoundCatalog.Resolve(
                    setting ?? SystemSoundCatalog.DefaultFinishedSoundName, directory, extensions),
                ResolveAttentionSound: setting => SystemSoundCatalog.Resolve(
                    setting ?? SystemSoundCatalog.DefaultAttentionSoundName, directory, extensions),
                Muted: AudioMute.IsMuted);
        }
    }
}
