using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Orbweaver
{
    // The part of SessionManager a drag needs and nothing more, so the release
    // outcome below can be tested against a fake and OrbWindow's pointer handlers
    // (excluded from coverage) can stay a few lines each. Positions and discs are
    // physical pixels, the space OrbWindow.Position and OrbArrangement use.
    internal interface IOrbDragHost
    {
        bool IsArranged { get; }

        /// <summary>The pressed orb's whole team: root lead first, then every member, nested ones included. Always contains <paramref name="sessionId"/>; a solo orb gives just itself.</summary>
        IReadOnlyList<string> TeamIdsOf(string sessionId);

        /// <summary>Every on-screen orb in a group, teams included.</summary>
        IReadOnlyList<string> OrbIdsInGroup(string groupId);

        OrbWindow? WindowOf(string sessionId);

        string GroupIdOf(string sessionId);

        /// <summary>Position key (membership key) of an orb; empty when it has none.</summary>
        string PositionKeyOf(string sessionId);

        /// <summary>The orb's visible circle in physical pixels.</summary>
        (double X, double Y, double R) DiscOf(string sessionId);

        /// <summary>Every group except the orbs in <paramref name="excludedIds"/>, discs in physical pixels. Outline empty when none is computed.</summary>
        IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excludedIds);

        void HighlightGroup(string? groupId);

        /// <summary>Moves a group's saved anchor by the drag's delta (physical pixels).</summary>
        void ShiftGroupAnchor(string groupId, int dx, int dy);

        void ReapplyArrangement();
    }

    // Where the drag handlers find the roster. Null until SessionManager sets it
    // (S7), and OrbWindow keeps today's drag behaviour while it is.
    internal static class OrbDragHost
    {
        internal static IOrbDragHost? Current { get; set; }
    }

    // CB-268: what a drag does to groups, as plain calls. OrbWindow's handlers
    // only gather what the pointer did and hand it here.
    internal static class OrbDragCommit
    {
        // During the move: the outcome if the user let go now, with the target
        // group highlighted (and nothing highlighted for any other outcome).
        internal static SnapOutcome Preview(IOrbDragHost host, string pressedId, double unitsPerDip)
        {
            var outcome = Decide(host, pressedId, unitsPerDip);
            host.HighlightGroup(outcome is SnapOutcome.Join join ? join.GroupId : null);
            return outcome;
        }

        // On release of a drag that moved (dx, dy) physical pixels.
        //
        // Whole-group drags change no membership: the group moved, so its saved
        // anchor moves with it. A plain orb drag moves the team and then snaps
        // or unsnaps it as one — membership is written under the lead's key and
        // every member's (T3), so the team is never split across groups. Leaving
        // makes a new one-orb group anchored at the drop point, which is how a
        // user builds a group: pull two orbs out and drop them together.
        // The arrangement is reapplied last in every case, because a team that
        // was dropped inside its own group's shape would otherwise stay wherever
        // the pointer left it.
        internal static SnapOutcome Release(
            IOrbDragHost host, string pressedId, bool wholeGroup, int dx, int dy, double unitsPerDip)
        {
            host.HighlightGroup(null);

            if (wholeGroup)
            {
                if (host.IsArranged)
                {
                    host.ShiftGroupAnchor(host.GroupIdOf(pressedId), dx, dy);
                    host.ReapplyArrangement();
                }

                return new SnapOutcome.NoChange();
            }

            var outcome = Decide(host, pressedId, unitsPerDip);
            var team = host.TeamIdsOf(pressedId);
            var keys = team.Select(host.PositionKeyOf).ToList();

            // Membership is keyed by position key, and an orb with none (a CLI
            // with no cwd) has no stable identity to remember. If nothing can be
            // written there is nothing to join or leave: making the group first
            // would leave an empty one, with an anchor, in settings.json for
            // good.
            if (outcome is not SnapOutcome.NoChange && keys.All(string.IsNullOrEmpty))
                outcome = new SnapOutcome.NoChange();

            switch (outcome)
            {
                case SnapOutcome.Join join:
                    OrbweaverSettings.AssignTeamToGroup(keys[0], keys.Skip(1), join.GroupId);
                    break;

                case SnapOutcome.Leave:
                    var made = OrbweaverSettings.CreateOrbGroup();
                    OrbweaverSettings.AssignTeamToGroup(keys[0], keys.Skip(1), made.Id);

                    // Anchored at the middle of the team's discs, not at the
                    // pressed orb: a group's anchor is its shape's centre, and a
                    // member fanned out from its lead is not where that is.
                    var discs = team.Select(host.DiscOf).ToList();
                    var x = (discs.Min(d => d.X - d.R) + discs.Max(d => d.X + d.R)) / 2;
                    var y = (discs.Min(d => d.Y - d.R) + discs.Max(d => d.Y + d.R)) / 2;
                    OrbweaverSettings.SetOrbGroupAnchor(
                        made.Id,
                        new OrbweaverSettings.OrbPlacement((int)Math.Round(x), (int)Math.Round(y)));
                    break;
            }

            if (host.IsArranged) host.ReapplyArrangement();
            return outcome;
        }

        private static SnapOutcome Decide(IOrbDragHost host, string pressedId, double unitsPerDip)
        {
            var s = unitsPerDip > 0 ? unitsPerDip : 1;
            var team = host.TeamIdsOf(pressedId);
            var moving = team
                .Select(host.DiscOf)
                .Select(d => (X: d.X / s, Y: d.Y / s, R: d.R / s))
                .ToList();

            var candidates = host.CandidatesFor(new HashSet<string>(team))
                .Select(c => c with
                {
                    Outline = c.Outline.Select(p => new Point(p.X / s, p.Y / s)).ToArray(),
                    Discs = c.Discs.Select(d => (d.X / s, d.Y / s, d.R / s)).ToList(),
                })
                .ToList();

            // The lead is the biggest disc of a team (members are drawn smaller),
            // so the largest radius is the orb size the thresholds scale with.
            var radius = moving.Count == 0 ? 0 : moving.Max(d => d.R);
            var size = radius > 0 ? radius / OrbSnap.BaseRadiusDip : 1;

            return OrbSnap.Decide(
                moving, host.GroupIdOf(pressedId), candidates,
                OrbSnap.JoinDip * size, OrbSnap.LeaveDip * size);
        }
    }
}
