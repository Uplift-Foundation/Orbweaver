namespace Orbweaver
{
    // CB-268: what a scan needs to remember to keep group membership attached
    // to the right key, carried out by SessionManager.SyncOrbGroups. The other
    // scan-time write, T3's late member, needs no planning here: it is
    // OrbweaverSettings.EnsureTeamMembership, which is idempotent and never
    // overwrites a member's own explicit choice.
    internal static class OrbGroupSync
    {
        // CB-10: an untitled session is keyed by its own id until Claude Code
        // names it, and then by its directory and title. A group chosen before
        // the title arrived is under the old key, so the membership is carried
        // to the new one — copied, never moved, by
        // OrbweaverSettings.CarryOrbGroupMembership, which is RefreshSoundKey's
        // rule for sounds and sizes.
        //
        // OrbWindow.PositionKey cannot tell anyone this happened: it is set once,
        // when the orb is made. So the tracker remembers the last key it saw for
        // each session and reports the ones that changed.
        internal sealed class KeyTracker
        {
            private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);

            // Every known session with its current key — SessionManager passes
            // all of its statuses, not only the orbs on screen. Returns (old,
            // new) for each whose key changed from one non-empty key to
            // another, and forgets a session only once it is no longer passed
            // in, which is when the session itself has ended.
            internal IReadOnlyList<(string Old, string New)> Observe(IEnumerable<(string Id, string Key)> current)
            {
                var carries = new List<(string, string)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (id, key) in current)
                {
                    seen.Add(id);
                    if (key.Length == 0) continue;

                    if (_last.TryGetValue(id, out var before)
                        && !string.Equals(before, key, StringComparison.OrdinalIgnoreCase))
                    {
                        carries.Add((before, key));
                    }

                    _last[id] = key;
                }

                foreach (var gone in _last.Keys.Where(id => !seen.Contains(id)).ToList())
                    _last.Remove(gone);

                return carries;
            }
        }
    }
}
