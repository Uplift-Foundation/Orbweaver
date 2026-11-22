using System.Text;

namespace Orbweaver
{
    // CB-404 (frames-v13, Orbweaver-Mobile contracts/frames-v13.json): what
    // the run controls say about one session, in the shape both the phone relay
    // and the linked-desktop mirror carry.
    //
    // Deliberately not RemoteRunOptions. That record is what the desktop panel
    // draws, and it carries things a wire has no business with (the chip's own
    // sentence, OpenClaw's fast mode and time limit). This is the part a far
    // reader can act on. RunControlsHost.Wire is the one place the two meet, so
    // the panel's record can grow without the contract moving.
    //
    // Id is the confirmed value, null when nothing has been read. Pending is the
    // value being applied, until a reading confirms or refutes it. Off is the
    // sentence a disabled control shows (null when enabled), and Failed is the
    // sentence while the desk reports that its last set of this field was not
    // confirmed.
    internal sealed record RunFieldState(
        string? Id,
        string? Pending,
        IReadOnlyList<RemoteRunChoice> Choices,
        string? Off,
        string? Failed);

    // SavesDefault: the route in use also changes the default for new sessions
    // (decision F1: Claude Code's typed route, a Codex before 0.164).
    internal sealed record RunState(RunFieldState Model, RunFieldState Effort, bool SavesDefault);

    // A phone asking to change one field of a session's run (frames-v13
    // set_run). Only a well-formed one is made: every shape fault is bad_frame.
    internal sealed record SetRunRequest(string Ref, string SessionId, string Field, string Value) : RelayRequest(Ref);

    internal static class RunFrames
    {
        // caps: a phone shows the controls only for a desk that advertises this.
        internal const string Cap = "run_options";

        internal const string FieldModel = "model";
        internal const string FieldEffort = "effort";

        internal const string SetOk = "set";
        internal const string BadValue = "bad_value", Unavailable = "unavailable", SetFailed = "set_failed";

        // frames-v13 limits.
        internal const int MaxChoices = 50;
        internal const int MaxIdChars = 64;
        internal const int MaxLabel = 64;
        internal const int MaxReason = 200;

        // The desk's own sentence while the host is running another change to
        // the same session: two typed commands interleaved in one pane would
        // reach the CLI as one garbled line. A change that only waits for a
        // reading does not block (frames-v13 at a407b9a): Claude and Grok confirm
        // effort only with the next turn, and the list must not freeze that long.
        internal const string BusyWhy = "Another change to this session is being made right now.";

        // The coalescing key's input: the relay keeps the newest per session,
        // and the roster's key is untouched.
        internal static string CkFor(string sessionId) => "run:" + sessionId;

        // An ID: 1 to 64 printable ASCII characters, no space, no quote and no
        // backslash, so it is the same bytes escaped or not and nothing a typed
        // route could break out of. Not launch.model's grammar: a Claude alias
        // such as `opus[1m]` is an ID.
        internal static bool IdOk(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdChars) return false;
            foreach (var c in id)
                if (c < '!' || c > '~' || c == '"' || c == '\\') return false;
            return true;
        }

        internal static bool FieldOk(string? field) => field is FieldModel or FieldEffort;

        // C0 controls and DEL become spaces, then the frames-v6 Cut.
        internal static string Clean(string text, int max)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text) sb.Append(c < ' ' || c == '\u007f' ? ' ' : c);
            return PromptFrames.Cut(sb.ToString(), max);
        }

        // A blank label (or one of nothing but controls) is the id.
        // The blank test comes before the cut, as the contract orders it, so a
        // label of nothing but blanks can never come out as blanks and "…".
        internal static string Label(string id, string label)
        {
            var spaced = Clean(label, int.MaxValue);
            return string.IsNullOrWhiteSpace(spaced) ? id : PromptFrames.Cut(spaced, MaxLabel);
        }

        internal static string? Reason(string? text) => text is null ? null : Clean(text, MaxReason);

        // What a field says on the wire. Choices that are not IDs are left out,
        // and so is a repeated id after its first. Past the cap, the confirmed and
        // pending values are kept and the earliest others fill the rest, all in
        // the desk's own order, which for effort is the dial's. An id or pending
        // value that is not among what is left is not written.
        internal static RunFieldState Sanitize(RunFieldState f)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var valid = new List<RemoteRunChoice>();
            foreach (var c in f.Choices)
                if (IdOk(c.Id) && seen.Add(c.Id)) valid.Add(new RemoteRunChoice(c.Id, Label(c.Id, c.Label)));

            var kept = valid;
            if (valid.Count > MaxChoices)
            {
                bool Named(RemoteRunChoice c) => c.Id == f.Id || c.Id == f.Pending;
                var room = MaxChoices - valid.Count(Named);
                kept = valid.Where(c => Named(c) || room-- > 0).ToList();
            }

            string? Offered(string? id) => id is not null && kept.Any(c => c.Id == id) ? id : null;
            return new RunFieldState(Offered(f.Id), Offered(f.Pending), kept, Reason(f.Off), Reason(f.Failed));
        }

        internal static RunState Sanitize(RunState s) => new(Sanitize(s.Model), Sanitize(s.Effort), s.SavesDefault);

        internal static string State(string sessionId, RunState s)
        {
            var clean = Sanitize(s);
            var j = new CanonicalJson().Str("t", "run").Str("session_id", sessionId)
                .Raw(FieldModel, Field(clean.Model)).Raw(FieldEffort, Field(clean.Effort));
            return j.Flag("saves_default", clean.SavesDefault).ToString();
        }

        private static string Field(RunFieldState f)
        {
            var j = new CanonicalJson();
            if (f.Id is not null) j.Str("id", f.Id);
            if (f.Pending is not null) j.Str("pending", f.Pending);
            j.Array("choices", f.Choices.Select(c => new CanonicalJson().Str("id", c.Id).Str("label", c.Label).ToString()));
            if (f.Off is not null) j.Str("off", f.Off);
            if (f.Failed is not null) j.Str("failed", f.Failed);
            return j.ToString();
        }

        // set_run's checks after the session is known (frames-v13 check order):
        // the field is off, or the host is running another change to this
        // session (unavailable, with why), then the value is not one of the
        // field's choices as written (bad_value). Null means the route may run.
        // Read against the sanitised state, so a choice the cap left out is not
        // offered here either.
        internal static (string Code, string? Why)? Refusal(RunState state, string field, string value, bool running)
        {
            var clean = Sanitize(state);
            var f = field == FieldEffort ? clean.Effort : clean.Model;
            if (f.Off is { } off) return (Unavailable, off);
            if (running) return (Unavailable, BusyWhy);
            if (f.Choices.All(c => c.Id != value)) return (BadValue, null);
            return null;
        }

        // A route's own answer as the reply's code and why.
        internal static (string Code, string? Why) Answer(RunSetResult result) => result.Kind switch
        {
            RunSetKind.BadValue => (BadValue, null),
            RunSetKind.Unavailable => (Unavailable, Reason(result.Why ?? RunText.NoRoute)),
            _ => (SetFailed, Reason(result.Why ?? "it did not change")),
        };
    }
}
