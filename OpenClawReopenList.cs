using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orbweaver
{
    // CB-329: the rows of New Chat's "Open existing" picker.
    //
    // Pure on purpose: no window, no settings read, no statics. The cluster modes
    // come in as arguments, and the one rule that decides whether a conversation
    // could ever get an orb is asked of OrbClusters itself rather than rewritten,
    // so the picker and the scan cannot drift apart.
    internal enum OpenClawReopenTarget { Session, Room }

    // What one row is. A channel with two or more agents in it is one Room row,
    // the way the scan draws one room orb; everything else is a Session row.
    internal sealed record OpenClawReopenRow(
        OpenClawReopenTarget Target,
        // "openclaw:<key>" or "openclaw:room:<roomKey>": the orb the row will get.
        string OrbSessionId,
        // What the row shows: "#general", "wtvamp", "Daily digest".
        string Title,
        // What ChatFor / RoomChatFor get as the display name.
        string PanelTitle,
        SessionKind Kind,
        OpenClawNativeKind Native,
        bool Heartbeat,
        // One for a session; two or more distinct for a room, most recent first.
        IReadOnlyList<string> AgentIds,
        // Gateway keys to mark active: [key], or every member of the room.
        IReadOnlyList<string> MemberKeys,
        DateTime LastActivity);

    internal static class OpenClawReopenList
    {
        private const string RoomPrefix = "openclaw:room:";

        // listed = OpenClawSessions.ListedSnapshot(). No recency check of any kind:
        // that is the whole point of the feature.
        internal static IReadOnlyList<OpenClawReopenRow> Build(
            IReadOnlyList<OpenClawSessions.Session> listed,
            ClusterMode heartbeats, ClusterMode crons, ClusterMode native,
            bool approveEnabled)
        {
            // The scan's own predicate, called (OpenClawSessions.ClusterHides is
            // what Parse asks too). A pinned row, or one kept for an approval, is
            // listed whatever its cluster says, exactly as Parse keeps it; a row
            // Parse would cluster-hide can never be given an orb, so listing it
            // would break the promise the picker makes.
            var eligible = listed
                .Where(s => !OpenClawSessions.ClusterHides(s, heartbeats, crons, native, approveEnabled))
                .OrderByDescending(s => s.LastActivity)
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .ToList();

            var rows = new List<OpenClawReopenRow>();
            var rooms = new Dictionary<string, List<OpenClawSessions.Session>>(StringComparer.Ordinal);
            var roomOrder = new List<string>();

            foreach (var s in eligible)
            {
                var roomKey = OpenClawSessionKind.RoomOf(s.Key);
                if (roomKey is null)
                {
                    rows.Add(SessionRow(s));
                    continue;
                }

                if (!rooms.TryGetValue(roomKey, out var members))
                {
                    rooms[roomKey] = members = new List<OpenClawSessions.Session>();
                    roomOrder.Add(roomKey);
                }

                members.Add(s);
            }

            foreach (var roomKey in roomOrder)
            {
                // Already most recent first, from the sort above.
                var members = rooms[roomKey];
                var agents = new List<string>();
                foreach (var m in members)
                {
                    // RoomOf only accepts "agent:<id>:…" keys, so AgentIdOf cannot be null
                    // for a room member (pinned by RoomOfOnlyAcceptsKeysWithAnAgent).
                    var agent = OpenClawSessions.AgentIdOf(m.Key)!;
                    if (agents.Contains(agent, StringComparer.OrdinalIgnoreCase)) continue;
                    agents.Add(agent);
                }

                // One agent in the room, however many sessions it holds there: the
                // scan draws no room orb for that, so neither does the picker.
                if (agents.Count < 2)
                {
                    rows.Add(SessionRow(members[0]));
                    continue;
                }

                var title = SessionManager.RoomTitle(members[0].Title);
                rows.Add(new OpenClawReopenRow(
                    OpenClawReopenTarget.Room,
                    RoomPrefix + roomKey,
                    title,
                    title,
                    SessionKind.Channel,
                    OpenClawNativeKind.None,
                    false,
                    agents,
                    members.Select(m => m.Key).ToList(),
                    members.Max(m => m.LastActivity)));
            }

            // Most recent first, then the id, so the order is the same twice
            // running — the reason Parse's Ordered breaks ties on the key too.
            return rows
                .OrderByDescending(r => r.LastActivity)
                .ThenBy(r => r.OrbSessionId, StringComparer.Ordinal)
                .ToList();
        }

        private static OpenClawReopenRow SessionRow(OpenClawSessions.Session s) =>
            new(
                OpenClawReopenTarget.Session,
                "openclaw:" + s.Key,
                SessionManager.RoomTitle(s.Title),
                s.Title,
                s.Kind,
                s.Native,
                s.Heartbeat,
                new[] { OpenClawSessions.AgentIdOf(s.Key) ?? s.Key },
                new[] { s.Key },
                s.LastActivity);

        // agentId null = all agents; search null or blank = no text filter. A room
        // matches an agent filter if any of its agents does. Search is a
        // case-insensitive substring over the titles, every agent's name and the
        // badge.
        internal static IReadOnlyList<OpenClawReopenRow> Filter(
            IReadOnlyList<OpenClawReopenRow> rows, string? agentId, string? search,
            Func<string, string> agentName)
        {
            var needle = search?.Trim();
            return rows
                .Where(r => agentId is null
                    || r.AgentIds.Contains(agentId, StringComparer.OrdinalIgnoreCase))
                .Where(r => string.IsNullOrEmpty(needle)
                    || Matches(r, needle, agentName))
                .ToList();
        }

        private static bool Matches(OpenClawReopenRow row, string needle, Func<string, string> agentName) =>
            Contains(row.Title, needle)
            || Contains(row.PanelTitle, needle)
            || Contains(Badge(row), needle)
            || row.AgentIds.Any(id => Contains(agentName(id), needle));

        private static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        // The badge says what kind of conversation a row is, not how it gets woken
        // (the Session record's own comment draws that line): every agent's main
        // session is a heartbeat target, so putting Heartbeat first would badge
        // every Main "Heartbeat" and the Main badge would never appear. The cluster
        // *filter* still uses Heartbeat as the scan does. A cron labelled as the
        // heartbeat is a Heartbeat, as OrbClusters.Of counts it.
        internal static string Badge(OpenClawReopenRow row)
        {
            if (row.Native == OpenClawNativeKind.Dashboard) return "Native";
            if (row.Kind == SessionKind.Main) return "Main";
            if (row.Heartbeat) return "Heartbeat";

            return row.Kind switch
            {
                SessionKind.Cron => "Cron",
                SessionKind.Direct => "DM",
                SessionKind.Channel => "Channel",
                _ when row.Native != OpenClawNativeKind.None => "Native",
                _ => ""
            };
        }

        // What replaces the list, or null when there are rows to show. Loading is
        // not empty: "No conversations" is only true after a poll completed and
        // listed nothing. timedOut is the load timer giving up on a gateway that
        // never answered; the poll keeps going, so the list still replaces it.
        internal static string? EmptyText(bool loaded, bool timedOut, int total, int shown)
        {
            if (!loaded) return timedOut ? "Can't reach the OpenClaw gateway" : "Loading conversations…";
            if (total == 0) return "No conversations";
            return shown == 0 ? "No conversations match" : null;
        }

        // The line under the unreachable headline: the connect failure's own text
        // when one is known (on a Mac, the Local Network hint), otherwise where to
        // look. The fallback lives here and nowhere else.
        internal static string UnreachableDetail(string? failure) =>
            string.IsNullOrWhiteSpace(failure) ? "Check Settings → OpenClaw." : failure;

        // Approximate, never exact: activity times are unreliable (see the
        // findings doc), so the picker does not pretend to a precision it lacks.
        internal static string Ago(DateTime activityUtc, DateTime nowUtc)
        {
            var age = nowUtc - activityUtc;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
            if (age < TimeSpan.FromDays(2)) return "yesterday";
            if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} days ago";

            return activityUtc.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
        }
    }
}
