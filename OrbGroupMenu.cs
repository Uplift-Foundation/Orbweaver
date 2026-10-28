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

            var (ids, leadOf) = Indexed(statuses);
            var team = new List<string> { rootId };
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] != rootId && ids[OrbGroups.RootOf(i, leadOf, ids.Length)] == rootId) team.Add(ids[i]);
            }

            return ContextFor(rootId, team, statuses, heartbeats, crons);
        }

        // The same answer for a root and team someone else already resolved.
        // SessionManager passes TeamTree's, which knows T4's nearest surviving
        // ancestor across an ended lead — something the statuses alone cannot
        // say, because the ended lead's own status is gone — so the menu, the
        // drag and the arrangement all agree on who the root is. `team` lists
        // the root first; keys are written in that order.
        internal static GroupMenuContext ContextFor(
            string rootId,
            IEnumerable<string> team,
            IReadOnlyDictionary<string, SessionStatus> statuses,
            ClusterMode heartbeats,
            ClusterMode crons)
        {
            statuses.TryGetValue(rootId, out var root);

            var rootKey = KeyOf(rootId, statuses);
            var cluster = root is null ? OrbCluster.Chats : OrbClusters.Of(root.Heartbeat, root.Kind);
            var groupId = OrbGroups.GroupIdFor(MembershipOf(rootId, statuses), cluster, heartbeats, crons);

            var members = new List<string>();
            foreach (var id in team)
            {
                if (id == rootId) continue;

                var key = KeyOf(id, statuses);
                if (key.Length > 0 && key != rootKey && !members.Contains(key)) members.Add(key);
            }

            return new GroupMenuContext(groupId, rootKey.Length == 0 ? null : rootKey, members);
        }

        // The groups an orb's "Move to" offers: every other group with an orb on
        // screen, in the table's order, and Main always (Leave group's target).
        // Without that filter the list kept every group ever made, including the
        // ones whose sessions had all ended, and only ever grew. A null
        // `onScreen` (no SessionManager behind the menu) filters nothing.
        internal static IReadOnlyList<OrbGroup> MoveTargets(
            IEnumerable<OrbGroup> groups, string currentGroupId, IReadOnlySet<string>? onScreen) =>
            groups.Where(g => g.Id != currentGroupId &&
                              (onScreen is null || g.Id == OrbGroups.Main || onScreen.Contains(g.Id)))
                  .ToList();

        // An orb's membership key, empty for an orb with no status or no key.
        internal static string KeyOf(string id, IReadOnlyDictionary<string, SessionStatus> statuses) =>
            statuses.TryGetValue(id, out var status) ? SessionManager.PositionKeyFor(status, id) : "";

        // The group an orb was explicitly put in, or null — looked up under its
        // key and, for Claude Code only as SessionManager does it, its bare
        // directory as a second chance: a Codex or Grok orb must not take a
        // Claude Code orb's group by sharing its directory.
        internal static string? MembershipOf(string id, IReadOnlyDictionary<string, SessionStatus> statuses)
        {
            if (!statuses.TryGetValue(id, out var status)) return null;

            var key = SessionManager.PositionKeyFor(status, id);
            if (key.Length == 0) return null;

            return OrbweaverSettings.OrbGroupMembershipFor(key, FallbackKeyOf(id, statuses));
        }

        // The bare-directory second chance for an orb's membership lookup, or
        // null when it has none: Claude Code only, as SessionManager does it for
        // positions, so one rule serves every caller (MembershipOf, T3's team
        // fill) and a Codex or Grok orb never takes a Claude Code orb's group.
        internal static string? FallbackKeyOf(string id, IReadOnlyDictionary<string, SessionStatus> statuses)
        {
            if (!statuses.TryGetValue(id, out var status)) return null;

            var key = SessionManager.PositionKeyFor(status, id);
            var directory = status.Source == SessionSource.ClaudeCode ? SessionManager.DirectoryKeyFor(status) : "";
            return directory.Length > 0 && directory != key ? directory : null;
        }
    }
}
