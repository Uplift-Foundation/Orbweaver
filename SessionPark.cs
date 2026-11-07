using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace Orbweaver
{
    // Whether Claude Code's own session record says this session has been moved
    // to the background — parked — and so should not be wearing an orb.
    //
    // The sibling of TranscriptHandoff, and the reason both exist is worth
    // stating together, because the second one only got written after the first
    // turned out to answer a narrower question than anyone thought.
    //
    // TranscriptHandoff reads the *transcript* for the row Claude Code appends
    // when a running turn is backgrounded. That row is real and that rule is
    // right, but it only covers a turn that was in flight. Opening agents mode
    // from an **idle** conversation forks it just the same, and writes no row at
    // all. The CLI's own decision, lifted from the 2.1.251 binary:
    //
    //     function p(e,t){ if(!e) return "idle-fork";
    //                      return t ? "defer-then-fork" : "abort-then-fork" }
    //
    // Only the two "then-fork" arms interrupt anything, and only they leave a
    // marker. `idle-fork` is silent — so a user who pressed nothing but the key
    // that opens the agents list was left looking at two orbs wearing one name,
    // with the conversation living in the fork and the husk answering to
    // nothing. Observed live: `makayla-case` as 746496c9 (pid 70580, tmux %41,
    // idle) beside e4f5c5e4 (pid 85810, `claude bg-spare`), sharing 1790 of
    // 1792 message uuids.
    //
    // What this reads instead is the fact itself. Claude Code keeps a record per
    // live session at <config-root>/sessions/<pid>.json, and parking writes
    // `parkedJobId` onto it naming the job that took the conversation. Its own
    // wording for the state, also lifted from the binary, is "session <id>,
    // moved to the background from this window". Coming back out of the agents
    // view clears the field again, so it is scoped to exactly the question an
    // orb is asking — is this window showing the conversation right now — and
    // needs no marker sniffing to answer it.
    //
    // Pure half and I/O half, separated the way TranscriptHandoff's and
    // TranscriptIdentity's are: the thing with a right answer takes text, so it
    // can be asserted against a record captured off a real machine rather than
    // against whatever this machine is doing.
    internal static class SessionPark
    {
        // In a holder for the reason TranscriptHandoff's is: it is only taken by
        // the I/O path, and a static field initializer does not run until some
        // static field is touched, so it reads as an uncovered line belonging to
        // code that is measured.
        [ExcludeFromCodeCoverage]
        private static class Cache
        {
            internal static readonly object Gate = new();
        }

        // Answer per record path, keyed on length and mtime so a record that has
        // not changed is never re-read — the same bargain TranscriptHandoff
        // strikes, and it matters more here: this is asked once per scan for
        // every Claude Code session on screen, and the scan runs every two
        // seconds.
        //
        // One read serves two questions (CB-307 addendum): whether the record
        // says parked, and what its `status` says. A record is parsed once per
        // change, not once per question.
        private static readonly Dictionary<string, (long Length, DateTime Written, bool Parked, string? Status)>
            _answers = new(StringComparer.Ordinal);

        // The I/O half: find the record, consult the cache, read only what has
        // changed.
        //
        // A record that cannot be found or read answers **false**, and the
        // direction is deliberate and the same one TranscriptHandoff documents:
        // a positive answer here *is* the hiding, so anything short of a record
        // that actually says "parked" must assert nothing and leave the orb
        // alone. A pid with no record at all is the ordinary case for a session
        // this rule knows nothing about, not evidence of anything.
        //
        // home is a parameter for the reason TranscriptReader's is: so the walk
        // can be pointed at a temp directory in a test instead of at the
        // machine's own accounts.
        internal static bool IsParked(int pid, string sessionId, string? home = null) =>
            Lookup(pid, sessionId, home)?.Parked ?? false;

        // What Claude Code's own record says its TUI is doing, or null when
        // there is no record, it is not this session's, or it names no status.
        //
        // **Only `waiting` is ever acted on, in either direction; `busy` and
        // `idle` are never trusted for anything.** Measured off a real machine
        // on 2026-11-06: the record's status is written when the TUI's modal
        // state changes and otherwise left alone, so `waiting` was right on both
        // occasions it was seen (a /permissions screen, and the exit dialog of a
        // session whose record had said so for four days) and cleared within
        // seconds of the dialog closing, while `busy` was wrong by two days on
        // a session sitting idle at its prompt. A positive `waiting` is
        // evidence; its absence, and every other word, is not.
        internal static string? RecordStatus(int pid, string sessionId, string? home = null) =>
            Lookup(pid, sessionId, home)?.Status;

        private static (bool Parked, string? Status)? Lookup(int pid, string sessionId, string? home)
        {
            if (pid <= 0 || string.IsNullOrEmpty(sessionId)) return null;

            // No record, or one that vanished between finding it and reading it:
            // the same answer, reached through one check rather than two, so
            // the second (a race no test can hold still) is not a branch of its
            // own.
            var found = RecordPath(pid, home);
            var stat = found is null ? null : Stat(found);
            if (stat is null) return null;

            // Non-null by construction: a stat only exists for a path that was found.
            var path = found!;

            lock (Cache.Gate)
            {
                if (_answers.TryGetValue(path, out var seen)
                    && seen.Length == stat.Value.Length
                    && seen.Written == stat.Value.Written)
                {
                    return (seen.Parked, seen.Status);
                }
            }

            var text = ReadAll(path);
            var parked = SaysParked(text, sessionId);
            var status = SaysStatus(text, sessionId);

            lock (Cache.Gate)
            {
                // Same cap and same reasoning as TranscriptHandoff's: pids come
                // and go and nothing ever unkeys them, and starting over costs
                // one extra read per surviving entry.
                if (_answers.Count >= 512) _answers.Clear();

                _answers[path] = (stat.Value.Length, stat.Value.Written, parked, status);
            }

            return (parked, status);
        }

        // The record's `status` string, only when the record is this session's.
        // The same pid-reuse guard SaysParked applies, for the same reason: a
        // recycled record must be able to fail to assert, never to assert about
        // the wrong session. Malformed JSON, a missing or non-string status all
        // answer null.
        internal static string? SaysStatus(string? json, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(sessionId)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (!document.RootElement.TryGetProperty("sessionId", out var owner)
                    || owner.ValueKind != JsonValueKind.String
                    || !string.Equals(owner.GetString(), sessionId, StringComparison.Ordinal))
                {
                    return null;
                }

                return document.RootElement.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The decision, over the record's text.
        //
        // Two conditions, and the second is the one that stops this being
        // dangerous. `parkedJobId` alone would be enough if a record could only
        // ever describe the session that is asking — but the record is keyed by
        // **pid**, and pids are reused. A parked session that exits leaves its
        // record behind for however long it takes something else to be given
        // that number and write over it, and in the window between, the record
        // names one session while a different one asks about it. Requiring the
        // record's own `sessionId` to match means a stale or recycled record can
        // only ever fail to hide an orb, never hide the wrong one.
        //
        // Malformed JSON, a missing field, a `parkedJobId` of the wrong type or
        // an empty string all answer false, for the reason the I/O half above
        // does: only an affirmative record may retire an orb.
        internal static bool SaysParked(string? json, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrEmpty(sessionId)) return false;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

                if (!document.RootElement.TryGetProperty("parkedJobId", out var parked)
                    || parked.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(parked.GetString()))
                {
                    return false;
                }

                return document.RootElement.TryGetProperty("sessionId", out var owner)
                    && owner.ValueKind == JsonValueKind.String
                    && string.Equals(owner.GetString(), sessionId, StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // Where the record for a pid lives, across every account on the machine.
        //
        // Searched rather than computed because the session doing the asking may
        // belong to any of them — the case this whole file exists for was a
        // session under ~/.claude-board, not the default root. First hit wins:
        // one pid is one process, so two roots naming it is not a state that can
        // occur outside a stale file, and a stale file is what the sessionId
        // check above is for.
        //
        // Excluded for the reason TranscriptHandoff.Stat is: it is a directory
        // walk over files belonging to sessions that may be ending, and the race
        // is not a state a test can hold still. What it finds is decided by
        // SaysParked, which is pure and covered.
        [ExcludeFromCodeCoverage]
        private static string? RecordPath(int pid, string? home)
        {
            var name = pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";

            foreach (var root in ClaudeConfigRoots.AllWithDiscovered(home))
            {
                try
                {
                    var candidate = Path.Combine(root, "sessions", name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // An unreadable root is skipped rather than fatal: the next
                    // one may hold the answer, and none of them holding it is
                    // already the fail-open case.
                }
            }

            return null;
        }

        [ExcludeFromCodeCoverage]
        private static (long Length, DateTime Written)? Stat(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch
            {
                return null;
            }
        }

        [ExcludeFromCodeCoverage]
        private static string? ReadAll(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        // For tests: the cache is process-wide and keyed on a path that a temp
        // directory can hand back to a later test, so a suite that writes two
        // different records to the same scratch path in the same run would
        // otherwise read the first one's answer out of the cache.
        internal static void ClearCacheForTests()
        {
            lock (Cache.Gate) _answers.Clear();
        }
    }
}
