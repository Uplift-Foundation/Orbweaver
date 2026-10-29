using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Reading a reply out loud, from whichever button was pressed.
    //
    // There are two of those buttons — one on the orb's flyout, one in the chat
    // panel's header — and CB-165 shipped with each of them implementing half of
    // this. The orb resolved the session's persona voice and then spoke the raw
    // reply, ignoring the scope setting entirely. The panel honoured the scope
    // and then spoke it in the wrong voice, because its own resolver had no arm
    // for a local session. Every user pressing either button got exactly the
    // half the other one was missing.
    //
    // Two parallel fixes would have left that structure in place, which is what
    // produced the bug: nothing about two independent implementations of one
    // feature says which of them is authoritative, so they drift and nobody
    // notices until a user describes both halves in one sentence. So both
    // buttons now enter here and the drift has nowhere to happen.
    //
    // What it is *not* is a speech engine. The decisions — which text, whose
    // voice, what the button shows while waiting, what happens when the
    // summariser fails — all live here, in ordinary methods a headless test can
    // call. Only the utterance itself is excluded, and it is one line.
    internal static class SpeechRequest
    {
        // The seam the UI tests drive the whole path through.
        //
        // Not optional politeness: TextToSpeech.Speak starts a real speech
        // engine, and an earlier version of OrbWindowSpeakTests genuinely made
        // the machine running the suite talk out loud. With this set, a test can
        // press either button for real and assert the text, the voice and the
        // rate that would have been uttered — which is the thing no test did
        // before, and the reason this shipped broken while both halves were
        // covered.
        internal static Action<string, TextToSpeech.VoiceOption?, double?>? UtteranceForTests;

        // Likewise for the voice list: enumerating the real one asks Kokoro to
        // list itself and runs the user's own listing command.
        internal static IEnumerable<TextToSpeech.VoiceOption>? VoiceOptionsForTests;

        // And for the session table: the only thing that fills it in
        // production is SessionManager's scan, which needs a status file on
        // disk and a running timer.
        internal static Func<string?, string?>? TranscriptPathForTests;

        // Where a session's transcript is, which is what says whose account
        // its summary runs on. Null for anything the scan does not know.
        internal static string? TranscriptPathOf(string? sessionId)
        {
            var seam = TranscriptPathForTests;
            return seam is not null ? seam(sessionId) : SessionManager.Instance?.StatusFor(sessionId)?.TranscriptPath;
        }

        // How many speak requests have been made. Only ever read as "is the
        // request I started still the current one", never for its value.
        private static int _requestGeneration;

        private static int NextRequest() => Interlocked.Increment(ref _requestGeneration);

        // Whether a summary that has just come back should still be spoken.
        //
        // Pure, and separated out for the reason the rest of this file is: the
        // old version of this decision was one expression inside the method that
        // utters, so nothing could see it, and it was wrong for seven months of
        // machine-time before a user described the symptom.
        //
        // The two things that legitimately suppress a pending summary are a user
        // asking for silence, and a newer speak request having replaced this one.
        // Both are counted rather than inferred. What must *not* suppress it is
        // the shared speak state having moved for any other reason — that is what
        // the old `State != Preparing` check actually tested, and on a machine
        // with several orbs it is true constantly for reasons the user never
        // caused. See TextToSpeech.StopGeneration.
        internal static bool ShouldStillSpeak(
            int startedRequest, int currentRequest, int startedStop, int currentStop) =>
            startedRequest == currentRequest && startedStop == currentStop;

        // The entry point both buttons use. Everything below it is the same
        // sequence for both, which is the whole point of the file.
        //
        // The cancel branch stays with the callers rather than moving here: the
        // orb's is reached before it has gone looking for any text at all (a
        // gateway session's costs a round trip over the wire), so folding it in
        // would mean fetching a reply in order to discover it was not wanted.
        internal static void Speak(string? reply, string? sessionId)
        {
            // CB-298: before the plan, so a muted reply spends no summariser
            // round trip and raises no hourglass for speech nobody will hear.
            if (AudioMute.IsMuted) return;

            var plan = SpeechPlan.For(reply, OrbweaverSettings.SpeakScope);
            if (plan.Silent) return;

            // Claimed before either branch, so a full-text utterance supersedes a
            // summary still in flight exactly as a second summary would. Taking
            // it only on the summary path would let a pending summary speak over
            // the top of a reply the user asked for afterwards.
            var request = NextRequest();

            if (!plan.NeedsSummary)
            {
                Utter(plan.Text!, sessionId);
                return;
            }

            // Deliberately not awaited: the summariser takes seconds and the UI
            // thread is the one drawing the hourglass that says so.
            _ = SpeakSummaryAsync(plan.Text!, sessionId, request);
        }

        // CB-167's vibe-summary turn sound: OrbWindow.SpeakTurnSummary calls
        // this instead of Speak above. Always SpeakScope.Summary regardless
        // of what the user has the speak button's own scope set to —
        // choosing "Vibe summary" as an orb's finished sound is itself the
        // request for a summary, and the global preference for the whole
        // reply would otherwise silently turn every finished-turn sound back
        // into a five-minute reading for anyone who has that set to Full.
        // SpeechPlan.For's short-reply rule still applies underneath it: a
        // reply already under the threshold is spoken in full rather than
        // sent on a round trip that would buy nothing.
        internal static void SpeakTurnSummary(string? reply, string? sessionId)
        {
            // CB-298: as in Speak — decided before any work is spent.
            if (AudioMute.IsMuted) return;

            var plan = SpeechPlan.For(reply, SpeakScope.Summary);
            if (plan.Silent) return;

            var request = NextRequest();

            if (!plan.NeedsSummary)
            {
                Utter(plan.Text!, sessionId);
                return;
            }

            _ = SpeakSummaryAsync(plan.Text!, sessionId, request, SpeechSummaryKind.TurnFinished);
        }

        // The summary leg, which is the one with a wait in it.
        //
        // The hourglass goes up before the round trip rather than after, because
        // starting it is itself part of the wait being announced — the same
        // argument StartNeural's own comment makes about loading a model. The
        // measured round trip is several seconds, and a speaker that goes silent
        // for that long with no indication is the failure this ticket's
        // refinement notes predicted. The orb's flyout shows the same hourglass
        // off the same state change (SessionManager broadcasts it to every orb),
        // so this one line is what makes the two buttons look alike as well as
        // behave alike.
        internal static Task SpeakSummaryAsync(string reply, string? sessionId) =>
            SpeakSummaryAsync(reply, sessionId, NextRequest());

        internal static Task SpeakSummaryAsync(string reply, string? sessionId, int request) =>
            SpeakSummaryAsync(reply, sessionId, request, SpeechSummaryKind.Reply);

        internal static async Task SpeakSummaryAsync(
            string reply, string? sessionId, int request, SpeechSummaryKind kind)
        {
            var stop = TextToSpeech.StopGeneration;

            TextToSpeech.Enter(TextToSpeech.SpeakState.Preparing);

            // Billed to the session's own account, not this app's (CB-248).
            // Resolved here, before the await, because every caller is on the
            // UI thread and that is the thread SessionManager's table is kept on.
            var account = SpeechSummary.AccountDirFor(TranscriptPathOf(sessionId));

            var text = await SpeechSummary.SummarizeOrSayWhyAsync(reply, kind, account).ConfigureAwait(true);

            // Either the user asked for silence while the summariser was running,
            // or a newer speak request replaced this one. Both mean speaking now
            // would start audio nobody is waiting for. This is the only point at
            // which that can be honoured: the round trip is several seconds and
            // there is nothing else watching it.
            //
            // It used to read `State != Preparing`, which looks like the same
            // question and is not. The state is process-wide, so it also moved
            // whenever an unrelated utterance elsewhere finished and its Exited
            // handler called Enter(Idle) — and a summary that had been produced
            // perfectly well was then discarded in silence. The counters below
            // answer what was actually meant.
            if (!ShouldStillSpeak(request, _requestGeneration, stop, TextToSpeech.StopGeneration))
            {
                // Only ours to clear if nothing else has since claimed it;
                // otherwise the newer request owns the hourglass.
                if (request == _requestGeneration) TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
                return;
            }

            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
            Utter(text, sessionId);
        }

        // Whose voice, at what rate — asked here rather than by the callers, so
        // that a new caller cannot arrive answering it a third way.
        internal static void Utter(string text, string? sessionId)
        {
            var options = VoiceOptionsForTests;
            var voice = options is null
                ? SessionIdentity.VoiceFor(sessionId)
                : SessionIdentity.VoiceFor(sessionId, options);
            var rate = SessionIdentity.RateFor(sessionId);

            var seam = UtteranceForTests;
            if (seam is not null)
            {
                seam(text, voice, rate);
                return;
            }

            Say(text, voice, rate);
        }

        // Excluded from coverage: makes the machine make a noise. TextToSpeech.Speak
        // is itself already excluded for that, and scoping the exclusion to this one
        // call is what keeps every decision above it measured — which is the shape
        // OrbWindowSpeakTests' header argues for at length, and the shape whose
        // absence let CB-165 ship with the choice of voice untested on one path and
        // the choice of text untested on the other.
        //
        // A null voice is the user's own setting rather than silence, in both
        // arms of every resolver that can produce one.
        [ExcludeFromCodeCoverage]
        private static void Say(string text, TextToSpeech.VoiceOption? voice, double? rate)
        {
            if (voice is null) TextToSpeech.Speak(text, OrbweaverSettings.SpeakVoice);
            else TextToSpeech.Speak(text, voice, rate);
        }
    }
}
