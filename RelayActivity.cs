using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // CB-323 (frames-v7 `activity`): what this desktop tells the relay about
    // its own sessions so the relay can sum every PC into one Live Activity.
    // A plaintext control frame — the relay must read it to add it up, and the
    // phone is not there to decrypt — carrying counts and the one oldest amber,
    // with replace semantics: every frame is the whole truth about this desktop
    // as of now, never an increment, so a reconnect or a duplicate can never
    // count a PC twice.
    //
    // Pure, like RelayRoster: the snapshot is a function of the book's rows and
    // the pending prompts, and the debounce is a function of the clock it is
    // handed. RelayLink owns one Tracker and decides when to ask it.

    // The prompt the pipeline minted for a session, as much of it as the
    // island shows. Command is already the command line, not the whole detail.
    internal sealed record ActivityPrompt(string RequestId, string Tool, string Command);

    internal sealed record ActivityOldest(string SessionId, string RequestId, string Title, string Tool, string Command, long Since);

    internal sealed record ActivitySnapshot(string Machine, int Amber, int Working, int Idle, ActivityOldest? Oldest);

    internal static class RelayActivity
    {
        // frames-v7 limits.
        internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);
        internal const int CountsMax = 9999;
        internal const int MaxTitle = 64, MaxTool = 32, MaxMachine = 32, MaxCommand = 160;
        // The v7 addendum's cap on oldest.session_id. Every local CLI's id is
        // far under it (a UUID is 36); the cut is there so a CLI that one day
        // mints a longer one still sends a frame the relay takes.
        internal const int MaxSessionId = 64;
        internal const string RefPrefix = "act-";

        // The relay refuses an empty machine, and a desktop whose name could not
        // be read is still a desktop: one word the island can show.
        internal const string UnnamedMachine = "Desktop";

        // 16 lowercase hex, random per process start (frames-v7 `boot`): a
        // restarted desktop starts its seq at 1 again and the relay takes it,
        // because the boot changed.
        internal static string NewBoot() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        // The frame's ref: the envelope-id grammar, unique for the process, and
        // recognisable when the relay's reply comes back carrying it.
        internal static string Ref(string boot, long seq) => $"{RefPrefix}{boot}-{seq}";

        // The relay echoes a frame's ref only when it is a valid id, and names
        // the frame ("activity") otherwise (frames-v7 relay addendum). This
        // desktop's refs are always valid, but a reply naming the frame is
        // still a reply about it — a rate_limited one most of all.
        internal const string FrameName = "activity";

        internal static bool IsActivityRef(string? reference) =>
            reference is not null && (reference.StartsWith(RefPrefix, StringComparison.Ordinal) || reference == FrameName);

        // The book's rows (waiting, generating, idle — an ended session has
        // already left the book) and the pending prompts, as one snapshot.
        // oldest: the waiting row with the smallest since, ties by session id,
        // with its minted prompt when there is one and empty strings when not
        // ("Open on desk" on the island).
        internal static ActivitySnapshot Snapshot(IEnumerable<RelaySession> rows, Func<string, ActivityPrompt?> pending, string machine)
        {
            int amber = 0, working = 0, idle = 0;
            RelaySession? oldest = null;
            foreach (var row in rows)
            {
                switch (row.State)
                {
                    case RelayRoster.Waiting:
                        amber++;
                        if (oldest is null || Earlier(row, oldest)) oldest = row;
                        break;
                    case RelayRoster.Generating:
                        working++;
                        break;
                    case RelayRoster.Idle:
                        idle++;
                        break;
                }
            }

            ActivityOldest? o = null;
            if (oldest is not null)
            {
                var prompt = pending(oldest.SessionId);
                o = new ActivityOldest(
                    PromptFrames.Cut(oldest.SessionId, MaxSessionId),
                    prompt?.RequestId ?? "",
                    PromptFrames.Cut(oldest.Title, MaxTitle),
                    PromptFrames.Cut(prompt?.Tool ?? "", MaxTool),
                    PromptFrames.Cut(prompt?.Command ?? "", MaxCommand),
                    RelayRoster.Timestamp(oldest.Since));
            }
            return new ActivitySnapshot(MachineName(machine), Clamp(amber), Clamp(working), Clamp(idle), o);
        }

        private static bool Earlier(RelaySession a, RelaySession b)
        {
            var (sa, sb) = (RelayRoster.Timestamp(a.Since), RelayRoster.Timestamp(b.Since));
            return sa != sb ? sa < sb : string.CompareOrdinal(a.SessionId, b.SessionId) < 0;
        }

        private static int Clamp(int n) => Math.Min(n, CountsMax);

        internal static string MachineName(string machine) =>
            string.IsNullOrWhiteSpace(machine) ? UnnamedMachine : PromptFrames.Cut(machine, MaxMachine);

        // The command line out of a prompt's preview — what the island shows
        // under the tool, rather than the whole detail (which would put the
        // dialog's header and its question there too). One rule per CLI, read
        // off the real captures in tests/UnitTests/Fixtures/screens:
        //   * Codex: "Would you like to run the following command?", then
        //     Environment/Reason lines, then "$ <command>" — the $ line.
        //   * Grok: "› <what it is for>" then "│ <command>" — the framed line.
        //   * Claude Code: "<Tool> command" / "<Tool> file", then perhaps a
        //     "Tip:" that can wrap, a description, the command or path, and
        //     "Do you want …?" — the last line before that question.
        //   * Anything else (a trust, update or hooks screen, no header): its
        //     first line, which is what that screen is asking about.
        // Lines are trimmed and blank ones skipped; "" when nothing is left.
        internal static string CommandOf(IReadOnlyList<string> preview)
        {
            var lines = preview.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return "";
            if (lines[0].Contains("run the following command", StringComparison.OrdinalIgnoreCase))
                return lines.FirstOrDefault(l => l.StartsWith("$ ", StringComparison.Ordinal)) is { } dollar ? dollar[2..].Trim() : "";
            if (lines.FirstOrDefault(l => l.StartsWith('│')) is { } framed) return framed[1..].Trim();
            if (PromptPipeline.ToolOf([lines[0]]) != "")
            {
                var question = lines.FindIndex(1, l => l.StartsWith("Do you want", StringComparison.OrdinalIgnoreCase));
                var last = (question < 0 ? lines.Count : question) - 1;
                return last >= 1 ? lines[last] : "";
            }
            return lines[0];
        }

        // frames-v7 frames.activity, canonical, keys in the contract's order.
        internal static string Frame(string reference, string boot, long seq, ActivitySnapshot s)
        {
            var j = new CanonicalJson().Str("t", "activity").Str("ref", reference).Str("boot", boot).Num("seq", seq)
                .Str("machine", s.Machine).Num("amber", s.Amber).Num("working", s.Working).Num("idle", s.Idle);
            if (s.Oldest is not { } o) return j.Raw("oldest", "null").ToString();
            return j.Raw("oldest", new CanonicalJson().Str("session_id", o.SessionId).Str("request_id", o.RequestId)
                .Str("title", o.Title).Str("tool", o.Tool).Str("command", o.Command).Num("since", o.Since).ToString()).ToString();
        }

        // The 2 s trailing-edge debounce (frames-v7 activity_min_interval_s):
        // a change at least 2 s after the last send goes now; one inside the
        // interval is due when the interval ends, and goes then with the state
        // as it is then.
        internal static (bool SendNow, DateTimeOffset? DueAt) Decide(DateTimeOffset now, DateTimeOffset? lastSent, bool changed)
        {
            if (!changed) return (false, null);
            if (lastSent is not { } last || now - last >= MinInterval) return (true, null);
            return (false, last + MinInterval);
        }

        // Why a snapshot is being offered: a scan (debounced), a frame that
        // carries a push (sent at once when the snapshot changed, so the
        // relay's counts on that alert already include it), or a welcome
        // (always — the relay may have restarted and forgotten nothing, or
        // everything, and only a fresh frame tells it which).
        internal enum Reason { Scan, Push, Welcome }

        // The per-process state: boot, seq, what was last sent and when, and
        // whether a deferred send is due. Not thread-safe; RelayLink calls it
        // under RelayClient's lock.
        internal sealed class Tracker(string boot)
        {
            private ActivitySnapshot? _last;
            private DateTimeOffset? _lastAt;

            internal string Boot { get; } = boot;
            internal long Seq { get; private set; }
            internal DateTimeOffset? DueAt { get; private set; }

            // The frame to send now, or null. becameDue: this offer deferred a
            // send that was not already deferred — the caller schedules one
            // flush at DueAt, and only one.
            internal string? Offer(ActivitySnapshot s, DateTimeOffset now, Reason reason, out bool becameDue)
            {
                becameDue = false;
                var changed = s != _last;
                if (reason == Reason.Welcome || (reason == Reason.Push && changed)) return Send(s, now);
                // Already waiting: the flush sends the state as it is then — or
                // this offer does, if it arrives after the flush was due.
                if (DueAt is { } waiting) return now >= waiting ? Flush(s, now) : null;
                var (sendNow, due) = Decide(now, _lastAt, changed);
                if (sendNow) return Send(s, now);
                if (due is null) return null;
                DueAt = due;
                becameDue = true;
                return null;
            }

            // The deferred send, when its time has come: the snapshot as it is
            // now, or nothing if it is back where the last send left it.
            internal string? Flush(ActivitySnapshot s, DateTimeOffset now)
            {
                if (DueAt is not { } due || now < due) return null;
                DueAt = null;
                return s != _last ? Send(s, now) : null;
            }

            // The socket went: nothing deferred survives it, since the welcome
            // sends the snapshot regardless.
            internal void Reset() => DueAt = null;

            // The relay did not take the last frame (rate_limited): it is sent
            // again — the snapshot as of then — once the interval has passed.
            // True when that makes a send due that was not already.
            internal bool Retry(DateTimeOffset now)
            {
                _last = null;
                if (DueAt is not null) return false;
                DueAt = (_lastAt ?? now) + MinInterval;
                return true;
            }

            private string Send(ActivitySnapshot s, DateTimeOffset now)
            {
                Seq++;
                _last = s;
                _lastAt = now;
                DueAt = null;
                return Frame(Ref(Boot, Seq), Boot, Seq, s);
            }
        }
    }

    // CB-323 gate (frames-v7 gate.logs.desktop): ORBWEAVER_ACTIVITY_LOG=<path>
    // makes the desktop write one JSON line per activity frame it SENT — the
    // frame itself plus at_ms on this clock — and one per reply the relay
    // gave it (ref, code, at_ms). The hour gate's checker rebuilds every
    // desktop's snapshot sequence from these. Off unless the variable is set,
    // in every build, because the gate runs against the installed app. The
    // lines carry this desktop's own titles and commands, which were already
    // its own.
    internal sealed class RelayActivityLog(string path)
    {
        internal const string EnvVar = "ORBWEAVER_ACTIVITY_LOG";
        internal const int Attempts = 5;
        internal static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(20);
        private readonly object _gate = new();
        private bool _warned;

        internal string Path { get; } = path;

        internal static RelayActivityLog? From(Func<string, string?> env) =>
            env(EnvVar) is { Length: > 0 } p ? new RelayActivityLog(p) : null;

        internal static bool IsActivityFrame(string frame) => frame.StartsWith("{\"t\":\"activity\",", StringComparison.Ordinal);

        // Fired when the line could not be written, once: the gate then has a
        // hole in it, and a log that silently stopped is worse than none.
        internal event Action<string> Failed = delegate { };

        internal void Sent(string frame, DateTimeOffset at) =>
            Append(frame[..^1] + ",\"at_ms\":" + at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        // frames-v7 gate.logs.desktop: (ref, code, at_ms) and nothing else —
        // the code alone tells ok from err (activity, activity_stale against
        // bad_frame, forbidden, unexpected, rate_limited).
        internal void Reply(string reference, string code, DateTimeOffset at) =>
            Append(new CanonicalJson().Str("ref", reference).Str("code", code).Num("at_ms", at.ToUnixTimeMilliseconds()).ToString());

        private void Append(string line)
        {
            lock (_gate)
            {
                // Shared for reading and writing, and a few short retries: on
                // Windows a reader that opened the file without write sharing
                // (File.ReadAllLines does) makes the append fail while it reads,
                // and the gate's checker reads this file while the app writes
                // it. Measured on the Windows box: the plain AppendAllText lost
                // the reply line under a polling reader.
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        file.Write(bytes);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt < Attempts && ex is IOException and not DirectoryNotFoundException) { Thread.Sleep(RetryPause); continue; }
                        if (_warned) return;
                        _warned = true;
                        Failed($"activity log not written: {ex.GetType().Name}");
                        return;
                    }
                }
            }
        }
    }
}
