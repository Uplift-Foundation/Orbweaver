namespace Orbweaver
{
    // One event this scan noticed, already reduced to what the policy needs
    // to act on it: which kind of signal, which key its override (if any) is
    // filed under, and which session gets the summary if that is what gets
    // decided. SessionManager builds these from TurnSignalTracker.Observe;
    // TurnSoundPolicy never sees a SessionStatus.
    internal readonly record struct TurnSoundEvent(TurnSignal Signal, string SoundKey, string SessionId);

    // The two things this feature can actually make happen, and Silent,
    // which is the answer on most scans since most scans have nothing to
    // say.
    internal enum SoundActionKind { Silent, Chime, Summary }

    // PlayAt is what QA's fix for the drop-not-defer bug lives in: null
    // means "play this now," and a value means "the rate limit is still
    // closed — this is what plays once it opens." Kind still says WHAT will
    // eventually play; PlayAt only ever says WHEN. Keeping the same Kind
    // for both cases (rather than a fourth enum member) is deliberate — a
    // deferred chime is still, fundamentally, a chime, and every caller that
    // only cares "is this audible" reads Kind exactly as before.
    internal sealed record SoundAction(
        SoundActionKind Kind, string? Path = null, string? SessionId = null, DateTime? PlayAt = null)
    {
        internal static readonly SoundAction Silent = new(SoundActionKind.Silent);

        // QA (CB-167): a Chime action now carries the session id that
        // resolved to it too, not just Path — a pending Chime needs one to
        // be cancellable the same way a pending Summary already was.
        // TurnSounds is what actually reads it; Decide never inspects its
        // own output's SessionId for anything.
        internal static SoundAction Chime(string sessionId, string path, DateTime? playAt = null) =>
            new(SoundActionKind.Chime, Path: path, SessionId: sessionId, PlayAt: playAt);

        internal static SoundAction Summary(string sessionId, DateTime? playAt = null) =>
            new(SoundActionKind.Summary, SessionId: sessionId, PlayAt: playAt);

        internal bool IsDeferred => PlayAt is not null;
    }

    // Everything this decision needs to know about what the user has asked
    // for, gathered up front by the caller so this stays a function of its
    // arguments rather than a reader of OrbweaverSettings. The two resolver
    // delegates carry the filesystem lookup and the platform default name
    // together, because "null means the platform default" is a fact about
    // *which trigger* the setting belongs to (Ping for attention, Glass for
    // finished on macOS) and this record is what closes over that without
    // TurnSoundPolicy itself having to know either name.
    internal sealed record SoundSettingsSnapshot(
        bool MasterEnabled,
        string? DefaultFinishedSetting,
        string? DefaultAttentionSetting,
        Func<string, OrbweaverSettings.OrbTurnSound?> OverrideFor,
        Func<string?, string?> ResolveFinishedSound,
        Func<string?, string?> ResolveAttentionSound,
        // CB-298. Trailing with a default so every existing construction
        // keeps compiling. Checked beside MasterEnabled, and in ResolveOne as
        // well, because a deferred chime is resolved again when it fires and a
        // mute that lands during the deferral has to silence it too.
        bool Muted = false);

    // What actually happens for one scan's worth of signals: no process, no
    // settings file, no clock but the one it is handed. Every rule the plan
    // lists — coalescing, the rate limit, an override beating the default,
    // the busy-speech fallback — is a branch here and nowhere else, which is
    // what makes each of them a one-line assertion instead of something only
    // provable by listening to the app for two minutes.
    internal static class TurnSoundPolicy
    {
        // At least this long between two sounds this feature plays. Not a
        // debounce on any one session — TurnSignalTracker already stops a
        // session repeating itself — but a floor on how often the *app*
        // makes a noise, so four orbs finishing across two adjacent two-
        // second scans still reads as one sound rather than a rattle.
        internal static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2);

        private const string Off = "off";
        private const string SummarySetting = "summary";

        internal static SoundAction Decide(
            IReadOnlyList<TurnSoundEvent> signals,
            SoundSettingsSnapshot settings,
            DateTime now,
            DateTime lastPlayed,
            bool speechBusy)
        {
            if (signals.Count == 0) return SoundAction.Silent;
            if (!settings.MasterEnabled || settings.Muted) return SoundAction.Silent;

            // QA (CB-167) found the bug in doing this the other way around:
            // an earlier version picked the highest-ranked *signal* first
            // and only afterwards asked whether it resolved to anything
            // audible. That let a muted orb's NeedsAttention "win" the
            // coalescing and then evaporate against its own "off" override,
            // silencing a second orb's perfectly audible Finished chime in
            // the very same scan — the muted orb was never a candidate to
            // win, but nothing checked that before crowning it. So every
            // signal is resolved first — off, missing file, a real chime, or
            // a summary — and only a signal that resolves to something
            // audible is eligible to be the winner at all.
            SoundAction? best = null;
            var bestIsAttention = false;

            foreach (var signal in signals)
            {
                // Defensive rather than load-bearing: SessionManager only
                // ever adds a signal that isn't None, but Decide is reasoned
                // about as a pure function on its own terms, and a None
                // signal must never accidentally resolve as if it were a
                // Finished one just because it shares that branch's "not
                // attention" shape below.
                if (signal.Signal == TurnSignal.None) continue;

                var resolved = ResolveOne(signal, settings, speechBusy);
                if (resolved.Kind == SoundActionKind.Silent) continue;

                var isAttention = signal.Signal == TurnSignal.NeedsAttention;

                // Attention outranks Finished regardless of scan order;
                // within one rank, the first audible signal observed keeps
                // winning over a later one of the same rank.
                if (best is null || (isAttention && !bestIsAttention))
                {
                    best = resolved;
                    bestIsAttention = isAttention;
                }
            }

            if (best is not { } chosen) return SoundAction.Silent;

            // Inside the rate limit: defer rather than drop. The earlier
            // version returned Silent here, and because TurnSignalTracker
            // never re-raises an unchanged state, a signal born inside the
            // gap had no later scan that would ever produce it again — an
            // orb that started waiting 1.5s after another orb's chime simply
            // never got its Ping, for as long as it sat there. Holding the
            // winner until the gap opens, and letting a later scan's winner
            // replace it, is what "coalesced" is supposed to mean — not just
            // within one scan, but across the whole time the app is quiet.
            if (now - lastPlayed < MinimumGap)
            {
                var playAt = lastPlayed + MinimumGap;
                return chosen with { PlayAt = playAt };
            }

            return chosen;
        }

        // What one signal resolves to on its own, with no knowledge of any
        // other signal in the scan: override or default, "off", "summary"
        // (turn-finished only, and only when speech isn't already busy), or
        // a real path. This is exactly what the pre-QA version of Decide did
        // to its single already-chosen winner — now run per candidate,
        // before any winner is chosen, which is the fix.
        //
        // Internal rather than private as of QA round 2: TurnSounds.
        // FirePending re-runs this at fire time, over whichever pending
        // events survive Settle/Prune/no-longer-waiting filtering, with a
        // freshly-read speechBusy — the same logic, asked again rather than
        // trusted from when it was first deferred.
        internal static SoundAction ResolveOne(TurnSoundEvent ev, SoundSettingsSnapshot settings, bool speechBusy)
        {
            if (settings.Muted) return SoundAction.Silent;

            var isAttention = ev.Signal == TurnSignal.NeedsAttention;
            var over = settings.OverrideFor(ev.SoundKey);
            var setting = isAttention
                ? over?.Attention ?? settings.DefaultAttentionSetting
                : over?.Finished ?? settings.DefaultFinishedSetting;

            if (IsOff(setting)) return SoundAction.Silent;

            // Vibe summary is a turn-finished thing only — see the settings
            // comment on needsAttentionSound for why the attention side of
            // this record has no equivalent value to check for.
            if (!isAttention && IsSummary(setting))
            {
                // A summary never interrupts speech already in progress. It
                // falls back to the ordinary finished chime rather than to
                // silence, because the alternative is a turn ending with no
                // feedback at all whenever the user happened to be listening
                // to something else — which is worse than the chime someone
                // who never asked for speech gets every time.
                return speechBusy
                    ? ChimeOrSilent(ev.SessionId, settings.ResolveFinishedSound(null))
                    : SoundAction.Summary(ev.SessionId);
            }

            var resolve = isAttention ? settings.ResolveAttentionSound : settings.ResolveFinishedSound;
            return ChimeOrSilent(ev.SessionId, resolve(setting));
        }

        private static SoundAction ChimeOrSilent(string sessionId, string? path) =>
            path is null ? SoundAction.Silent : SoundAction.Chime(sessionId, path);

        private static bool IsOff(string? setting) =>
            setting is not null && string.Equals(setting, Off, StringComparison.OrdinalIgnoreCase);

        private static bool IsSummary(string? setting) =>
            setting is not null && string.Equals(setting, SummarySetting, StringComparison.OrdinalIgnoreCase);
    }
}
