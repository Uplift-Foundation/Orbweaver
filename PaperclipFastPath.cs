using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // Two small pure rules about a Paperclip orb's status (CB-427, CB-425): what the
    // orb shows the moment a need is answered, and the one diagnostics line a
    // state change writes. (An earlier version also applied a published change
    // past the scan pass; the lead ruled that out: the slow pass on the Windows
    // test PC is every orb source's problem and belongs to CB-424.)
    internal static class PaperclipFastPath
    {
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
