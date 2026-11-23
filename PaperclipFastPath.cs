using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // A Paperclip change reaching its orbs without waiting behind the local scan
    // (CB-425). The full scan pass reads status files, lists background jobs and
    // applies every orb on the UI thread; on the Windows test PC a pass takes 1 to
    // 8 s, and a change published while one runs waited for it and then for a full
    // pass of its own, about the 3.3 s the ticket measured. Paperclip's rows are
    // a pure function of the poll's snapshot (PaperclipScan.Entries), so when
    // nothing about the SET of orbs changed they can be applied at once, to the
    // orbs that already exist, and the full pass converges the rest afterwards.
    //
    // What stays on the full pass, by falling back (Plan returns null): an orb
    // appearing or going, a lead or a title changing. Those move where an orb
    // sits or what it is called, which the pass's reflow and persona work own.
    internal static class PaperclipFastPath
    {
        internal sealed record Change(string Key, SessionStatus Existing, SessionStatus Fresh);

        // The updates to make, or null when the fast path must not be used. Only
        // Paperclip keys on either side are looked at.
        internal static IReadOnlyList<Change>? Plan(
            IReadOnlyList<SessionManager.ScanEntry> fresh, IEnumerable<KeyValuePair<string, SessionStatus>> current)
        {
            var existing = current.Where(kv => kv.Key.StartsWith(PaperclipScan.KeyPrefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (existing.Count != fresh.Count) return null;
            var changes = new List<Change>(fresh.Count);
            foreach (var e in fresh)
            {
                if (!existing.TryGetValue(e.SessionId, out var old)) return null;
                if (old.Lead != e.Status.Lead || old.Title != e.Status.Title) return null;
                changes.Add(new Change(e.SessionId, old, e.Status));
            }
            return changes;
        }

        // Copies what Paperclip owns onto the status the orb already holds, so
        // everything the pass added to it (persona, cluster, team data) is kept.
        internal static void CopyOwned(SessionStatus to, SessionStatus from)
        {
            to.State = from.State;
            to.Presence = from.Presence;
            to.Mark = from.Mark;
            to.MarkReason = from.MarkReason;
            to.BudgetPercent = from.BudgetPercent;
            to.FoldedCount = from.FoldedCount;
            to.FoldedNames = from.FoldedNames;
            to.HoverLines = from.HoverLines;
            to.StatusDetail = from.StatusDetail;
            to.Url = from.Url;
            to.Needs = from.Needs;
            to.PaperclipAgentId = from.PaperclipAgentId;
            to.PaperclipAgentStatus = from.PaperclipAgentStatus;
        }

        // One line for a state change that reached an orb: the agent's id prefix,
        // the states, the milliseconds from the poll that saw it. No title, no
        // token, no text from Paperclip. Null for a change that moved no state.
        internal static string? Line(string key, string oldState, string newState, TimeSpan elapsed)
        {
            if (oldState == newState) return null;
            var id = key.StartsWith(PaperclipScan.KeyPrefix, StringComparison.Ordinal) ? key[PaperclipScan.KeyPrefix.Length..] : key;
            return $"paperclip: {(id.Length > 8 ? id[..8] : id)} {oldState} -> {newState} reached the orb in {(long)Math.Max(0, elapsed.TotalMilliseconds)} ms";
        }
    }
}
