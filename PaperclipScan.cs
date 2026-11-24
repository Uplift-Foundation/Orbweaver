using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orbweaver
{
    // What the scan draws for Paperclip (plan 2.7 to 2.9), as pure functions of
    // the poll loop's snapshot. SessionManager only calls Entries; everything
    // that decides what an orb says lives here so it is a unit test.
    internal sealed class PaperclipScan
    {
        internal const string KeyPrefix = "paperclip:";

        private readonly object _gate = new();
        private IReadOnlyDictionary<string, DateTime> _stamps = new Dictionary<string, DateTime>();

        internal static string KeyFor(string agentId) => KeyPrefix + agentId;

        // The scan's rows for one pass. `sleeping` is the paperclipSleeping
        // setting; `link` and `linkText` are the poll loop's. A link that needs
        // reconnecting, or has been unreachable past the grace, parks every
        // row: states frozen to idle, no mark, no ring, and one line that says
        // why. Never a silent disappearance.
        internal IReadOnlyList<SessionManager.ScanEntry> Entries(
            IReadOnlyList<PaperclipAgent> snapshot, PaperclipLink link, string linkText,
            string sleeping, DateTime now, bool remembered = false)
        {
            if (snapshot.Count == 0) return Array.Empty<SessionManager.ScanEntry>();

            IReadOnlySet<string> awake;
            lock (_gate)
            {
                var (stamps, set) = PaperclipApi.TrackAwake(_stamps, snapshot, now);
                _stamps = stamps;
                awake = set;
            }

            var folded = PaperclipApi.Fold(snapshot, sleeping, PaperclipApi.DefaultFoldLimit, awake);
            var drawn = folded.Drawn.Select(a => a.Id).ToHashSet();
            // Remembered rows are a picture from the last launch, not a live read, so
            // they are parked exactly as a refused key parks the company (plan 2.4):
            // dimmed, no mark, no ring, one line saying why.
            var parked = remembered || link is PaperclipLink.NeedsReconnect or PaperclipLink.Unreachable;

            var rows = new List<SessionManager.ScanEntry>(folded.Drawn.Count);
            foreach (var a in folded.Drawn)
            {
                var waiting = !parked && a.State == "waiting";
                var count = 0; string? names = null;
                if (!parked && folded.FoldedInto.TryGetValue(a.Id, out var f))
                {
                    count = f.Count;
                    names = string.Join(", ", f.Names);
                }

                var status = new SessionStatus
                {
                    Source = SessionSource.Paperclip,
                    Kind = SessionKind.Unknown,
                    State = parked ? "idle" : a.State,
                    Title = a.Name,
                    Cwd = "",
                    Color = OpenClawSessions.ColourForAgent(a.Id),
                    Url = waiting && a.Needs.Count > 0 ? a.Needs[0].Url : a.Url,
                    Lead = a.ReportsTo != null && drawn.Contains(a.ReportsTo) ? KeyFor(a.ReportsTo) : "",
                    Presence = parked ? OrbPresence.Parked
                        : waiting ? OrbPresence.NeedsInput
                        : a.Sleeping ? OrbPresence.Parked
                        : OrbPresence.Present,
                    Mark = parked ? PaperclipMark.None : a.Mark,
                    MarkReason = parked ? null : a.MarkReason,
                    BudgetPercent = parked ? null : a.BudgetPercent,
                    PaperclipAgentId = a.Id,
                    PaperclipAgentUrl = a.Url,
                    PaperclipAvatarFile = a.AvatarFile,
                    PaperclipAgentStatus = parked ? "" : a.Status,
                    Needs = waiting ? a.Needs : Array.Empty<PaperclipNeed>(),
                    FoldedCount = count,
                    FoldedNames = names,
                    // The orb's own tooltip composes the folded-count line, so this
                    // is just the agent's lines; parked says only why.
                    HoverLines = parked ? new[] { linkText } : PaperclipHover.Lines(a, now),
                };

                // The agent's own last activity, never `now`: a scan entry's
                // stamp is a claim about when something last happened.
                status.StatusDetail = status.HoverLines.Count > 0 ? status.HoverLines[0] : null;
                rows.Add(new SessionManager.ScanEntry(KeyFor(a.Id), status, a.LastActivity));
            }
            return rows;
        }
    }

    // The tooltip's lines, in the plan's order, each omitted when empty (plan
    // 2.7): need titles; mark reason; task; activity; how long it has been
    // running; the last result and when; spend against the monthly cap.
    // Composed here rather than in the orb so the time-relative parts are
    // computed at scan time (every two seconds) and the order is one test.
    internal static class PaperclipHover
    {
        internal static IReadOnlyList<string> Lines(PaperclipAgent a, DateTime now)
        {
            var lines = new List<string>();
            foreach (var n in a.Needs) lines.Add(n.Title);
            if (a.Mark != PaperclipMark.None && !string.IsNullOrEmpty(a.MarkReason)) lines.Add(a.MarkReason!);
            if (!string.IsNullOrEmpty(a.Task)) lines.Add(a.Task!);
            if (!string.IsNullOrEmpty(a.Activity)) lines.Add(a.Activity!);
            if (a.State == "generating" && a.RunStarted is { } started)
                lines.Add("running for " + Span(now - started));
            // The time is the newest finished run's own finishedAt. LastActivity
            // includes a live run's start, so using it would print an in-flight
            // run's start as when the previous one ended. With no finishedAt the
            // result is printed with no time.
            if (!string.IsNullOrEmpty(a.LastResult) && a.State != "generating"
                && !(a.MarkReason?.StartsWith("Last run", StringComparison.Ordinal) ?? false))
                lines.Add(a.LastRunFinished is { } done
                    ? $"last run {a.LastResult} {Ago(now - done)}"
                    : $"last run {a.LastResult}");
            if (a.BudgetPercent != null)
                lines.Add($"{Money(a.SpentCents)} of {Money(a.BudgetCapCents)} this month");
            return lines;
        }

        // "45 s", "3 m", "2 h", "4 d": the largest whole unit, never fractional.
        internal static string Span(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} m";
            if (t.TotalDays < 1) return $"{(int)t.TotalHours} h";
            return $"{(int)t.TotalDays} d";
        }

        internal static string Ago(TimeSpan t) => t.TotalSeconds < 5 ? "just now" : Span(t) + " ago";

        internal static string Money(long cents) =>
            "$" + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    }
}
