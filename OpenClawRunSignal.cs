using System.Text.Json;

namespace Orbweaver
{
    // What one gateway event says about whether its session is generating.
    //
    // None:  nothing. The overwhelming majority of what arrives, and the
    //        answer for any event name this file has not been told about.
    // Work:  the session is producing something right now.
    // Open:  a run has started, and it will announce its own end. Until it
    //        does, silence is not evidence that it stopped (see RunCeiling).
    // End:   a run has finished. Only ends the run it names.
    internal enum RunSignal { None, Work, Open, End }

    internal readonly record struct RunEvent(RunSignal Signal, string? RunId);

    // What the tracker remembers about one session: when it last showed signs of
    // work, which run that was, and whether that run was opened by an explicit
    // start — the one case where silence is expected rather than suspicious.
    internal readonly record struct RunTrack(DateTime Last, string? RunId, bool Open);

    // CB-169: the positive-signal rule for an OpenClaw orb's glow.
    //
    // Before this, "working" was defined as *any event naming a session, except
    // a blocklist*. CB-149 and CB-152 each added to the blocklist to stop an orb
    // pinned on by housekeeping, and each addition was also a way to kill a live
    // one — the last of them, "sessions.changed", turned out to be the *only*
    // thing a run on a non-cron session sends this client (see
    // docs/openclaw-findings.md, "A run outside a cron session"). So the default
    // is inverted here: an event is evidence of work only if this file says so,
    // and an event name nobody has measured is None rather than Work. A new
    // gateway event can no longer pin an orb, and the price is that one can fail
    // to light it — which is the failure a person notices and reports, where a
    // pinned orb quietly defeats the recency filter for days.
    //
    // Every arm below was read off a live capture unless its comment says it
    // is assumed. Pure: no statics beyond two constants, no clock, no dispatcher.
    internal static class OpenClawRunSignal
    {
        // How long a session stays "working" after its last streaming event. A
        // cron turn emits continuously while it runs — thinking deltas, tool
        // phases — so this much silence means it stopped, whether or not a
        // terminal event arrived.
        internal static readonly TimeSpan RunIdle = TimeSpan.FromSeconds(20);

        // How long a run opened by an explicit start stays "working" with no
        // further events. It needs to be long, because a run on a non-cron
        // session sends *nothing* between its start and its end — two events in
        // total — so RunIdle would put out a reply's glow twenty seconds in.
        //
        // The number is read off the gateway rather than chosen. Its own
        // declared run timeout is no help (DEFAULT_AGENT_TIMEOUT_SECONDS is
        // 172800, two days), so the bound comes from what runs actually take:
        // the last-run runtimeMs of all 39 sessions on the live gateway that
        // report one (OpenClaw 2026.9.2, 25 Sep 2026) had a median of 11.2 s on
        // Discord sessions, 12 of 25 of them over RunIdle, three over five
        // minutes, and a longest of 1812 s. Ten minutes would have put that one
        // out two thirds of the way through. 45 minutes is the longest measured
        // run with half as much again on top.
        //
        // It is a safety net, not the mechanism: the run's own end clears it at
        // once, and a dropped connection or a Restart clears every run, since an
        // end sent while the socket was down is never seen. What it bounds is a
        // start whose end never arrives on a live connection — an orb glowing for up to 45 minutes
        // after that is the cost, paid only in a case never yet observed.
        internal static readonly TimeSpan RunCeiling = TimeSpan.FromMinutes(45);

        internal static RunEvent Classify(string name, JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object) return default;

            var runId = RunIdOf(payload);

            switch (name)
            {
                // A run's lifecycle as the session roster sees it. Measured: a
                // run on an agent's main session arrived as exactly two of
                // these, phase "start" and phase "end", each carrying the
                // runId, and *no* agent/chat/tool events at all. An aborted run
                // (CB-170, measured) sends phase "end" with status "killed" and
                // then, about a second later, a second row for the same runId
                // with phase "error". Either one ends the run: "error" is as
                // terminal as "end", and treating it as anything else would
                // either leave an orb lit when the "end" was missed or, worse,
                // light it again on the trailing row. The rows with
                // no phase — the CB-152 reconnect burst, reason "cron-binding"
                // or "placement", fired for the whole roster at once — carry no
                // runId either, and stay None. Phase "message" (a transcript row
                // appended) is a delivery, not generation.
                case "sessions.changed":
                    return (Str(payload, "phase"), runId) switch
                    {
                        ("start", not null) => new(RunSignal.Open, runId),
                        ("end" or "error", not null) => new(RunSignal.End, runId),
                        _ => default
                    };

                // Streaming output. Measured streams: "thinking", "assistant",
                // and "lifecycle" with data.phase "end" at the close of a cron
                // run. Lifecycle "start" and "error" are assumed by symmetry —
                // neither has been captured — and "start" is only Work, not
                // Open, so an assumption can hold an orb for RunIdle at most.
                case "agent":
                    return Str(payload, "stream") switch
                    {
                        "thinking" or "assistant" => new(RunSignal.Work, runId),
                        "lifecycle" => PhaseOf(payload) switch
                        {
                            "start" => new(RunSignal.Work, runId),
                            "end" or "error" => new(RunSignal.End, runId),
                            _ => default
                        },
                        _ => default
                    };

                // A tool starting or returning. Measured phases: "start" and
                // "result".
                case "session.tool":
                    return PhaseOf(payload) is "start" or "result"
                        ? new(RunSignal.Work, runId)
                        : default;

                // Measured: "delta" while text streams, "final" at the end.
                // "error" and "aborted" are assumed terminal states.
                case "chat":
                    return Str(payload, "state") switch
                    {
                        "delta" => new(RunSignal.Work, runId),
                        "final" or "error" or "aborted" => new(RunSignal.End, runId),
                        _ => default
                    };

                // Measured: the finish carries the run-scoped key but no runId
                // field, so the run comes off the key.
                case "cron" when Str(payload, "action") == "finished":
                    return new(RunSignal.End, runId);

                // Deliberately None, including "task"/"upserted". Measured: a
                // task event carries no top-level sessionKey at all — its
                // session is task.childSessionKey, and task.runId is the cron
                // scheduler's id, not the agent run's — so it cannot name the
                // run it would end, and the cron "finished" beside it already
                // ends that run. CB-149's housekeeping re-upserts are this same
                // event, and None is what stops them pinning an orb.
                default:
                    return default;
            }
        }

        // What an event does to a session's record. Null means "not running".
        //
        // An End clears only the run it names, because runs overlap on the
        // wire: in the capture a cron session's placement burst named two runs
        // at once. An End that names no run, or a record that never learned
        // one, has nothing to compare and clears — that is what a run-less
        // cron "finished" has always done.
        internal static RunTrack? Apply(RunTrack? track, RunEvent ev, DateTime now) => ev.Signal switch
        {
            RunSignal.Work => track is { } t
                ? t with { Last = now, RunId = t.RunId ?? ev.RunId }
                : new RunTrack(now, ev.RunId, false),
            RunSignal.Open => new RunTrack(now, ev.RunId, true),
            RunSignal.End when track is { } t
                && ev.RunId is not null && t.RunId is not null && t.RunId != ev.RunId => track,
            RunSignal.End => null,
            _ => track
        };

        internal static bool IsGenerating(RunTrack track, DateTime now) =>
            now - track.Last <= (track.Open ? RunCeiling : RunIdle);

        // The session an event belongs to: its key with any ":run:<runId>"
        // suffix trimmed, since event keys are run-scoped and list keys never
        // are.
        internal static string? SessionOf(JsonElement payload)
        {
            var key = payload.ValueKind == JsonValueKind.Object ? Str(payload, "sessionKey") : null;
            if (string.IsNullOrEmpty(key)) return null;

            var run = key.IndexOf(":run:", StringComparison.Ordinal);
            return run > 0 ? key[..run] : key;
        }

        // The runId field where there is one, else the key's ":run:" suffix.
        private static string? RunIdOf(JsonElement payload)
        {
            var id = Str(payload, "runId");
            if (!string.IsNullOrEmpty(id)) return id;

            var key = Str(payload, "sessionKey");
            var run = key?.IndexOf(":run:", StringComparison.Ordinal) ?? -1;
            return run > 0 && run + 5 < key!.Length ? key[(run + 5)..] : null;
        }

        private static string? PhaseOf(JsonElement payload) =>
            payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? Str(data, "phase")
                : null;

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.SafeString() : null;
    }
}
