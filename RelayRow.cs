using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Orbweaver
{
    // CB-330: how the chat panel shows a message another session or a team
    // member relayed into this one — Teammate @name finished, From another
    // session. Everything that is a decision rather than a drawing lives here,
    // pure, so it can be tested with no window and no settings behind it.
    //
    internal static class RelayRow
    {
        // The reader's own click wins over the setting, in both directions: a
        // row they opened stays open under Collapsed, and one they closed stays
        // closed under Expanded. Hidden never reaches here — the row is not
        // built — but a row that somehow is reads as collapsed.
        internal static bool IsExpanded(RelayedMessageDisplay setting, bool? userChoice) =>
            userChoice ?? (setting == RelayedMessageDisplay.Expanded);

        internal static bool IsShown(RelayedMessageDisplay setting) =>
            setting != RelayedMessageDisplay.Hidden;

        // The eight colours Claude Code assigns a team member, drawn from the
        // table the orbs already use so a row's dot and the member's orb are the
        // same colour rather than two palettes that drift apart. Null for a
        // colour this does not know, and for none.
        internal static Color? DotColor(string? payloadColor) =>
            !string.IsNullOrEmpty(payloadColor) && OrbWindow.AgentColors.TryGetValue(payloadColor, out var c)
                ? c
                : null;

        // The persona a member is an instance of, for CB-288's reason: a crashed
        // team respawned under its old names comes back as `name-2`, and
        // PersonaPool.NamesFor is the one place that knows what is a collision
        // suffix (digits only, so `name-v2` is left whole).
        internal static string PersonaNameFor(string name)
        {
            var names = PersonaPool.NamesFor(name);
            return names[^1];
        }

        // Which live session is this relayed name? A member of this panel's own
        // session whose agent name equals the relay's name, else its persona's
        // name — and only when that is exactly one session. Two matches are two
        // different people who happen to share a name, and the wrong face on a
        // message is worse than a dot (OpenClawSessions.AvatarForAgentName takes
        // the same position). Never a session that is not led by this one: the
        // lead's own face on a member's message is the CB-288 bug in new clothes.
        internal static string? MemberSessionId(
            string? name, string? leadSessionId, IEnumerable<KeyValuePair<string, SessionStatus>> statuses)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(leadSessionId)) return null;

            var members = statuses
                .Where(kv => kv.Value.Lead == leadSessionId && kv.Key != leadSessionId)
                .ToList();

            foreach (var candidate in PersonaPool.NamesFor(name))
            {
                var hits = members.Where(kv => kv.Value.Agent == candidate).ToList();
                if (hits.Count == 1) return hits[0].Key;
                if (hits.Count > 1) return null;
            }

            return null;
        }
    }
}
