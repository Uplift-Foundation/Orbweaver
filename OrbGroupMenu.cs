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
        // The orb a team hangs from: follow Lead until it is empty, points at
        // the orb itself, names a session that is not on screen, or loops. A
        // cycle ends at wherever the walk had got to, because "no root" would
        // leave the orb unable to move at all.
        internal static string RootOf(string sessionId, IReadOnlyDictionary<string, SessionStatus> statuses)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { sessionId };
            var current = sessionId;

            while (statuses.TryGetValue(current, out var status)
                   && !string.IsNullOrEmpty(status.Lead)
                   && statuses.ContainsKey(status.Lead)
                   && seen.Add(status.Lead))
            {
                current = status.Lead;
            }

            return current;
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
                var directory = root.IsLocalCli ? SessionManager.DirectoryKeyFor(root) : "";
                if (directory.Length > 0 && directory != rootKey) fallback = directory;
                cluster = OrbClusters.Of(root.Heartbeat, root.Kind);
            }

            var membership = string.IsNullOrEmpty(rootKey)
                ? null
                : OrbweaverSettings.OrbGroupMembershipFor(rootKey, fallback);
            var groupId = OrbGroups.GroupIdFor(membership, cluster, heartbeats, crons);

            var members = new List<string>();
            foreach (var (id, status) in statuses.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (id == rootId || RootOf(id, statuses) != rootId) continue;

                var key = SessionManager.PositionKeyFor(status, id);
                if (key.Length > 0 && key != rootKey && !members.Contains(key)) members.Add(key);
            }

            return new GroupMenuContext(groupId, rootKey.Length == 0 ? null : rootKey, members);
        }
    }
}
