using System.Text;

namespace Orbweaver
{
    // CB-404 (frames-v13): what the run-controls chip says about one session,
    // in the shape both the phone relay and the linked-desktop mirror carry.
    //
    // Deliberately not RemoteRunOptions. That record is what the desktop panel
    // draws and it carries things a wire has no business with (the chip's own
    // sentence, OpenClaw's fast mode and time limit); this is the part a far
    // reader can act on — what is running, what may be chosen, and, when it
    // may not, why. RunStateWire.From is the one place the two meet, so the
    // panel's record can grow without the contract moving.
    //
    // Id null means the value is not known yet (no reading). Off null means
    // the control is enabled; otherwise it is the sentence a disabled control
    // shows. Pending means the app sent a change no reading has confirmed.
    internal sealed record RunFieldState(
        string? Id,
        string? Label,
        IReadOnlyList<RemoteRunChoice> Choices,
        string? Off,
        bool Pending);

    internal sealed record RunState(RunFieldState Model, RunFieldState Effort);

    // A phone asking to change one field of a session's run (frames-v13
    // set_run). Field and Value are null when the shape was wrong, so the
    // handler answers bad_field / bad_value rather than a generic bad_frame —
    // the contract separates the two because a phone with a newer field name
    // than this desk knows should be told which part was refused.
    internal sealed record SetRunRequest(string Ref, string SessionId, string? Field, string? Value) : RelayRequest(Ref);

    internal static class RunFrames
    {
        // caps: a phone shows the controls only for a desk that advertises this.
        internal const string Cap = "run_options";

        internal const string FieldModel = "model";
        internal const string FieldEffort = "effort";

        // The ok code a set answers with, and v13's err codes.
        internal const string SetOk = "set";
        internal const string BadField = "bad_field", BadValue = "bad_value", Unavailable = "unavailable", Failed = "failed";

        // frames-v13 limits: a catalogue is capped by count and by bytes so a
        // CLI that one day lists hundreds of models cannot push one state frame
        // past what the relay will carry. The running model is always kept,
        // even when the cap would have cut it.
        internal const int MaxChoices = 32;
        internal const int MaxChoiceBytes = 4000;
        internal const int MaxValueLength = 64;

        // The coalescing key's input: the relay keeps the newest per session,
        // and the roster's own key is untouched.
        internal static string CkFor(string sessionId) => "run:" + sessionId;

        // A value uses launch.model's grammar, but empty is not a choice here:
        // "back to the default" is not something the phone offers.
        internal static bool ValueOk(string? value) =>
            !string.IsNullOrEmpty(value) && value.Length <= MaxValueLength && LaunchGate.ModelOk(value);

        internal static bool FieldOk(string? field) => field is FieldModel or FieldEffort;

        internal static string State(string sessionId, RunState s) =>
            new CanonicalJson().Str("t", "run").Str("session_id", sessionId)
                .Raw(FieldModel, Field(s.Model)).Raw(FieldEffort, Field(s.Effort)).ToString();

        private static string Field(RunFieldState f)
        {
            var j = new CanonicalJson();
            if (f.Id is not null) j.Str("id", f.Id);
            if (f.Label is not null) j.Str("label", f.Label);
            j.Array("choices", Capped(f.Choices, f.Id).Select(Choice));
            if (f.Off is not null) j.Str("off", f.Off);
            return j.Flag("pending", f.Pending).ToString();
        }

        private static string Choice(RemoteRunChoice c) => new CanonicalJson().Str("id", c.Id).Str("label", c.Label).ToString();

        // In order, until either cap would be passed; the current value is
        // then put back at the end if it was cut, so a phone can always draw
        // what the session is running.
        internal static IReadOnlyList<RemoteRunChoice> Capped(IReadOnlyList<RemoteRunChoice> choices, string? current)
        {
            var kept = new List<RemoteRunChoice>();
            var bytes = 0;
            foreach (var c in choices)
            {
                var size = Encoding.UTF8.GetByteCount(Choice(c));
                if (kept.Count == MaxChoices || bytes + size > MaxChoiceBytes) break;
                kept.Add(c);
                bytes += size;
            }
            if (current is not null && kept.All(c => c.Id != current)
                && choices.FirstOrDefault(c => c.Id == current) is { } running)
            {
                if (kept.Count == MaxChoices) kept.RemoveAt(kept.Count - 1);
                kept.Add(running);
            }
            return kept;
        }
    }
}
