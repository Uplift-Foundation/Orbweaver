using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Orbweaver
{
    // Whether a session is a conversation somebody is having, or a terminal
    // somebody walked away from.
    //
    // **Every cheap way of asking this returns the wrong answer.** On the Mac
    // mini, a `job-hunter-mac-mini` session abandoned at a prompt on the 29th
    // was still drawing an orb a day later, beside an identically-named live
    // one — and each of the obvious checks agreed it was alive:
    //
    // - its **process** was running, because nobody had closed the window;
    // - `claude agents --json` **listed** it, for the same reason;
    // - its transcript's **mtime** was six minutes old.
    //
    // That last one is the trap worth naming, because it is the check anyone
    // would reach for first. The file really had just been written — with a
    // `bridge-session` row carrying no timestamp at all. Remote Control's
    // bridge keeps poking the transcript of a session it is attached to, so
    // mtime measures the bridge's health and not the conversation's.
    //
    // What does distinguish them is the newest row that represents somebody
    // saying something: `user` or `assistant`, both of which carry a
    // timestamp. On that measure the dead session last spoke 23 hours ago and
    // the live one 8 seconds ago, which is not a close call.
    //
    // **This is deliberately not the rule CB-74 removed.** That one keyed off
    // the status file's heartbeat, which stops updating for a session that is
    // merely idle — so it hid sessions that were alive and waiting, which is
    // exactly the mistake being avoided here. A session reported `idle` this
    // instant is kept, provided the conversation itself is recent.
    //
    // **And a quiet session is parked, not hidden (CB-307).** The first
    // version of this rule dropped such a session from the roster outright,
    // and that turned out to be its own bug: a long-lived session nobody has
    // typed into since yesterday is the normal way these sessions are used,
    // not an abandoned one — `lawyer-search` on the mini had gone twenty hours
    // without a turn and was very much wanted. So a quiet session now stays on
    // the board, drawn dimmed, on the host and on every peer, and clears the
    // moment somebody speaks to it. The case that started all this — a quiet
    // copy sitting beside a live copy of the same name — is a separate, much
    // narrower rule (SuppressedDuplicates), because the name collision was
    // what made it confusing, not the quiet.
    internal static class SessionLiveness
    {
        // How long after its last turn a conversation is drawn as parked.
        //
        // Long enough to survive a meal, a meeting or a school run — walking
        // away from a session for an hour is not leaving it, and an orb that
        // dims over lunch says something untrue. Short enough that yesterday's
        // quiet terminal reads as quiet today.
        //
        // Nothing subtler is warranted: the cases observed on real machines
        // were 8 seconds, 20 hours and 23 hours, and no plausible boundary
        // between them separates those differently. Since CB-307 a wrong
        // answer costs a dimmed orb that brightens on the next turn rather
        // than a missing one, so there is no pressure to move it either way.
        internal static readonly TimeSpan ParkedAfter = TimeSpan.FromHours(2);

        // The rows that mean somebody said something.
        //
        // Everything else a transcript holds is bookkeeping — `mode`,
        // `agent-name`, `queue-operation`, `file-history-snapshot`,
        // `bridge-session` — written by tooling rather than by a turn, and
        // several of them are written to a session nobody is using.
        private static bool IsATurn(string? type) =>
            type is "user" or "assistant";

        // Whether this session is parked: alive, but nobody has said anything
        // to it for at least the window.
        //
        // Pure, and takes `now` rather than reading a clock, so the boundary
        // can be asserted from both sides without waiting two hours. Exactly
        // at the window is parked, which is the boundary the hiding rule this
        // replaced had (it showed only strictly inside the window).
        internal static bool IsParked(
            string? state, DateTime? lastTurnUtc, DateTime nowUtc, TimeSpan window)
        {
            // A session that is generating or waiting is being used right now,
            // whatever its transcript last recorded. A long tool call writes
            // nothing for minutes, and a permission prompt can sit unanswered
            // far longer than that — neither is parked, and both are the
            // moments a user most wants the orb at full brightness.
            if (state is "generating" or "waiting") return false;

            // No turn found at all. That is a transcript this process could
            // not read, or one holding nothing but bookkeeping — and a session
            // whose quiet cannot be established is drawn as present. Parking on
            // a failed read would make an unreadable file look exactly like a
            // quiet session, which is the confusion this class exists to end.
            if (lastTurnUtc is not { } last) return false;

            return nowUtc - last >= window;
        }

        internal static bool IsParked(string? state, DateTime? lastTurnUtc, DateTime nowUtc) =>
            IsParked(state, lastTurnUtc, nowUtc, ParkedAfter);

        // Which sessions the duplicate rule is allowed to look at.
        //
        // One predicate shared by the host's scan and the mirror server, so the
        // two cannot drift into suppressing different things. Team members are
        // out because every member inherits its lead's title (CB-223's
        // "fourteen identically-titled orbs"): a quiet member beside a live one
        // is a team, not a duplicate, and must not vanish. Codex, Grok and the
        // rest are out because the collision this rule exists for — a second
        // `claude --remote-control <name>` beside a forgotten first — is a
        // Claude Code shape.
        internal static bool EligibleForDuplicateRule(SessionStatus s) =>
            s.Source == SessionSource.ClaudeCode && string.IsNullOrEmpty(s.Lead);

        // The keys of quiet copies that a live copy of the same name makes
        // redundant.
        //
        // The one case where hiding is still right: two sessions answering to
        // one name, one of them in use and the other parked. Drawing both puts
        // two identical labels on the board, and the dim one is the one nobody
        // means — that is the exact `job-hunter-mac-mini` picture this class
        // was first written for. Everything else is drawn: two live copies are
        // both real (the roster already qualifies them with a route suffix),
        // and two parked copies give nothing to prefer one over the other, so
        // both stay, dimmed.
        //
        // Names compare case-insensitively, as the peer list resolves them; a
        // row with no name (or only whitespace) is never a duplicate of anything.
        internal static HashSet<string> SuppressedDuplicates(
            IEnumerable<(string Key, string Name, bool Parked)> rows)
        {
            var suppressed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var group in rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                         .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!group.Any(r => !r.Parked)) continue;

                foreach (var row in group)
                    if (row.Parked) suppressed.Add(row.Key);
            }

            return suppressed;
        }

        // The newest turn in these lines, or nothing.
        //
        // Takes lines rather than a path so the rule is testable without a
        // disk — the interesting inputs here are files a test machine does not
        // happen to have, most of all one whose only recent rows are
        // untimestamped bridge housekeeping.
        //
        // Newest-anywhere rather than last-row: rows are appended in order in
        // practice, but a transcript ends with whatever tooling wrote last, so
        // scanning from the end for the first turn would work while scanning
        // only the final row would not. Taking the maximum costs one pass and
        // does not depend on either assumption.
        internal static DateTime? LastTurnAt(IEnumerable<string> lines)
        {
            DateTime? newest = null;

            foreach (var line in lines)
            {
                var at = TurnTime(line);
                if (at is null) continue;

                if (newest is null || at > newest) newest = at;
            }

            return newest;
        }

        // One line's turn time, if it is a turn and carries one.
        //
        // Deliberately tolerant. This reads a format nobody here controls, on
        // a machine that may be running a different version of the CLI, and
        // the cost of misreading a line is an orb that lingers — so anything
        // unrecognised is "not a turn" rather than an exception.
        internal static DateTime? TurnTime(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            try
            {
                using var row = JsonDocument.Parse(line);

                if (row.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (!row.RootElement.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !IsATurn(type.GetString()))
                    return null;

                if (!row.RootElement.TryGetProperty("timestamp", out var stamp)
                    || stamp.ValueKind != JsonValueKind.String)
                    return null;

                var text = stamp.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;

                // Round-trip: the CLI writes `2026-08-31T03:07:07.499Z`, and
                // AdjustToUniversal keeps a transcript written in another zone
                // comparable with this machine's clock.
                if (!DateTime.TryParse(
                        text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var when))
                    return null;

                return when;
            }
            catch (JsonException)
            {
                // A truncated line, which is the ordinary case at the start of
                // a tail read rather than a fault.
                return null;
            }
        }
    }
}
