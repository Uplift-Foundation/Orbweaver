using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // Pure rules about a Paperclip orb's status (CB-425, CB-427).
    //
    // A published change reaching its orbs without waiting behind the local scan:
    // the full pass reads status files, lists background jobs and applies every
    // orb on the UI thread, and on the Windows test PC a pass takes 1 to 8 s, so a
    // change published while one runs waited for it and then for a full pass of
    // its own. Paperclip's rows are a pure function of the poll's snapshot
    // (PaperclipScan.Entries), so when the SET of orbs is unchanged they can be
    // applied at once, to the orbs that already exist, and the full pass
    // converges the rest. Plan returns null, falling back to the full pass, when
    // an orb appears or goes or a lead or a title differs: those move where an
    // orb sits or what it is called, which the pass's reflow and persona work own.
    //
    // Never onto the object already published: the relay reads the statuses the
    // last scan handed it on its own threads, and every other path publishes a
    // fresh object and leaves it alone. So an update is made on a copy (Applied,
    // WithoutNeed's caller), and the copy replaces the old object in the table.
    //
    // What still lags on a slow machine, because it moves on the full pass: the
    // turn sound and the phone's roster. The orb changes first.
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

        // A copy of the orb's status carrying what Paperclip owns from the fresh
        // row, and everything the pass added to it (persona, cluster, team data)
        // kept. The original is not touched.
        internal static SessionStatus Applied(SessionStatus existing, SessionStatus fresh)
        {
            var to = existing.Copy();
            to.State = fresh.State;
            to.Presence = fresh.Presence;
            to.Mark = fresh.Mark;
            to.MarkReason = fresh.MarkReason;
            to.BudgetPercent = fresh.BudgetPercent;
            to.FoldedCount = fresh.FoldedCount;
            to.FoldedNames = fresh.FoldedNames;
            to.HoverLines = fresh.HoverLines;
            to.StatusDetail = fresh.StatusDetail;
            to.Url = fresh.Url;
            to.Needs = fresh.Needs;
            to.PaperclipAgentId = fresh.PaperclipAgentId;
            to.PaperclipAgentStatus = fresh.PaperclipAgentStatus;
            to.PaperclipAgentUrl = fresh.PaperclipAgentUrl;
            return to;
        }

        // What an orb looks like the moment its last need is answered, before the
        // poll has had its say: the need removed, and with none left the orb no
        // longer asking (idle, present) and pointing at its agent's page again.
        // Returns false when the status is not a Paperclip one or does not hold
        // that need, so nothing is changed for an answer that is not this orb's.
        internal static bool WithoutNeed(SessionStatus status, string needId)
        {
            var gone = status.Needs.FirstOrDefault(n => n.Id == needId);
            if (status.Source != SessionSource.Paperclip || gone == null) return false;
            status.Needs = status.Needs.Where(n => n.Id != needId).ToList();
            // The hover's first lines are the needs' titles (PaperclipHover): drop this one's.
            var lines = status.HoverLines.ToList();
            lines.Remove(gone.Title);
            status.HoverLines = lines;
            status.StatusDetail = lines.Count > 0 ? lines[0] : null;
            if (status.Needs.Count > 0)
            {
                status.Url = status.Needs[0].Url;
                return true;
            }
            status.State = "idle";
            status.Presence = OrbPresence.Present;
            if (!string.IsNullOrEmpty(status.PaperclipAgentUrl)) status.Url = status.PaperclipAgentUrl;
            return true;
        }

        // The snapshot with the answered needs left out, for the scan to draw while
        // the poll has not yet said what Paperclip now holds. An agent whose last
        // need was answered stops asking (idle); everything else about it stands.
        internal static IReadOnlyList<PaperclipAgent> WithoutAnswered(
            IReadOnlyList<PaperclipAgent> snapshot, IReadOnlySet<string> answered)
        {
            if (!snapshot.Any(a => a.Needs.Any(n => answered.Contains(n.Id)))) return snapshot;
            return snapshot.Select(a =>
            {
                var kept = a.Needs.Where(n => !answered.Contains(n.Id)).ToList();
                if (kept.Count == a.Needs.Count) return a;
                return a with { Needs = kept, State = kept.Count == 0 && a.State == "waiting" ? "idle" : a.State };
            }).ToList();
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
