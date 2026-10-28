namespace Orbweaver
{
    /// <summary>
    /// What an orb's Group submenu needs to know about the orb: which group it
    /// is in, and the keys to write when its team moves. <paramref name="LeadKey"/>
    /// is the team root's position key and <paramref name="MemberKeys"/> every
    /// other team orb's, the exact shape
    /// <see cref="OrbweaverSettings.AssignTeamToGroup"/> takes.
    /// </summary>
    internal sealed record GroupMenuContext(
        string GroupId, string? LeadKey, IReadOnlyList<string> MemberKeys);

    // The decisions behind the Group submenu, kept out of OrbWindow so they can
    // be tested with a dictionary of statuses and no window.
    internal static class OrbGroupMenu
    {
        // The orb a team hangs from, by the one rule everything else uses
        // (OrbGroups.RootOf, which defers to OrbArrangement): follow Lead until
        // it is empty or names a session that is not on screen, and every orb in
        // a lead cycle is its own root. Orb indices are the statuses in ordinal
        // id order, so the answer does not depend on dictionary order.
        internal static string RootOf(string sessionId, IReadOnlyDictionary<string, SessionStatus> statuses)
        {
            var (ids, leadOf) = Indexed(statuses);
            var at = Array.IndexOf(ids, sessionId);

            // Not on screen: its own root, with nothing to follow.
            return at < 0 ? sessionId : ids[OrbGroups.RootOf(at, leadOf, ids.Length)];
        }

        private static (string[] Ids, int[] LeadOf) Indexed(IReadOnlyDictionary<string, SessionStatus> statuses)
        {
            var ids = statuses.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < ids.Length; i++) index[ids[i]] = i;

            var leadOf = new int[ids.Length];
            for (var i = 0; i < ids.Length; i++)
            {
                var lead = statuses[ids[i]].Lead;
                leadOf[i] = !string.IsNullOrEmpty(lead) && index.TryGetValue(lead, out var at) ? at : -1;
            }

            return (ids, leadOf);
        }

        // The group the orb's *team* is in (CB-268 T1: membership resolves from
        // the root lead), plus the keys a move must write (T3: every team key).
        internal static GroupMenuContext Resolve(
            string sessionId,
            IReadOnlyDictionary<string, SessionStatus> statuses,
            ClusterMode heartbeats,
            ClusterMode crons)
        {
            var rootId = RootOf(sessionId, statuses);
            statuses.TryGetValue(rootId, out var root);

            string rootKey = "";
            string? fallback = null;
            var cluster = OrbCluster.Chats;
            if (root is not null)
            {
                rootKey = SessionManager.PositionKeyFor(root, rootId);
                // Claude Code only, as SessionManager does it: a Codex or Grok orb
                // must not take a Claude Code orb's place by sharing its directory.
                var directory = root.Source == SessionSource.ClaudeCode ? SessionManager.DirectoryKeyFor(root) : "";
                if (directory.Length > 0 && directory != rootKey) fallback = directory;
                cluster = OrbClusters.Of(root.Heartbeat, root.Kind);
            }

            var membership = string.IsNullOrEmpty(rootKey)
                ? null
                : OrbweaverSettings.OrbGroupMembershipFor(rootKey, fallback);
            var groupId = OrbGroups.GroupIdFor(membership, cluster, heartbeats, crons);

            var (ids, leadOf) = Indexed(statuses);
            var members = new List<string>();
            for (var i = 0; i < ids.Length; i++)
            {
                var id = ids[i];
                var status = statuses[id];
                if (id == rootId || ids[OrbGroups.RootOf(i, leadOf, ids.Length)] != rootId) continue;

                var key = SessionManager.PositionKeyFor(status, id);
                if (key.Length > 0 && key != rootKey && !members.Contains(key)) members.Add(key);
            }

            return new GroupMenuContext(groupId, rootKey.Length == 0 ? null : rootKey, members);
        }
    }
}
