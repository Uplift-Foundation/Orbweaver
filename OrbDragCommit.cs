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

        /// <summary>Free mode: the orbs are not arranged. User groups are live and the predefined ones are not snap targets (F1, F3).</summary>
        bool IsFree => !IsArranged;

        /// <summary>True when the orb is its team's root (a solo orb is its own root); false for a member.</summary>
        bool IsRoot(string sessionId) => TeamIdsOf(sessionId)[0] == sessionId;

        /// <summary>The orb's root's group is laid out in this mode: a user group, or any group while arranged.</summary>
        bool IsLaidOut(string sessionId) => IsArranged || !OrbGroups.IsPredefined(GroupIdOf(sessionId));

        /// <summary>Saves a dragged member's offset from its lead (member centre minus effective lead centre, in DIP, keyed by SoundKey). A no-op until the roster implements it.</summary>
        void SaveMemberOffset(string memberId) { }

        /// <summary>Provisional outline around a dragged team and the loose team it would pair with; null, null clears it. A no-op until the roster implements it.</summary>
        void PreviewPair(IReadOnlyList<string>? draggedTeamIds, string? targetRootId) { }

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

        /// <summary>Re-lays out whatever the current mode lays out: the user groups always, the predefined groups while arranged.</summary>
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
        // During the move: the outcome if the user let go now. A Join highlights
        // the group; a Pair previews the provisional outline around both teams;
        // anything else clears both.
        internal static SnapOutcome Preview(IOrbDragHost host, string pressedId, double unitsPerDip)
        {
            var outcome = Decide(host, pressedId, unitsPerDip);
            host.HighlightGroup(outcome is SnapOutcome.Join join ? join.GroupId : null);
            if (outcome is SnapOutcome.Pair pair) host.PreviewPair(host.TeamIdsOf(pressedId), pair.RootId);
            else host.PreviewPair(null, null);
            return outcome;
        }

        // On release of a drag that moved (dx, dy) physical pixels.
        //
        // Whole-group drags change no membership: the group moved, so its saved
        // anchor moves with it. A member drag (T2 revised) moves only the member
        // and never touches membership: in a laid-out group its offset from its
        // lead is saved so the next layout puts it there, and otherwise it was
        // simply dropped and stays pinned. A lead drag moves the team and then
        // snaps or unsnaps it as one — membership is written under the lead's
        // key and every member's (T3), so the team is never split across groups.
        // Every path ends by reapplying, because a team dropped inside its own
        // group's shape would otherwise stay wherever the pointer left it.
        internal static SnapOutcome Release(
            IOrbDragHost host, string pressedId, bool wholeGroup, int dx, int dy, double unitsPerDip)
        {
            host.HighlightGroup(null);
            host.PreviewPair(null, null);

            if (wholeGroup)
            {
                host.ShiftGroupAnchor(host.GroupIdOf(pressedId), dx, dy);
                host.ReapplyArrangement();
                return new SnapOutcome.NoChange();
            }

            if (!host.IsRoot(pressedId))
            {
                if (host.IsLaidOut(pressedId))
                {
                    host.SaveMemberOffset(pressedId);
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
            IReadOnlyList<string> otherTeam = Array.Empty<string>();
            if (outcome is SnapOutcome.Pair pair) otherTeam = host.TeamIdsOf(pair.RootId);
            var otherKeys = otherTeam.Select(host.PositionKeyOf).ToList();

            if (outcome is not SnapOutcome.NoChange && keys.All(string.IsNullOrEmpty))
                outcome = new SnapOutcome.NoChange();
            // A pair needs a key on both sides, or the group would hold only one.
            if (outcome is SnapOutcome.Pair && otherKeys.All(string.IsNullOrEmpty))
                outcome = new SnapOutcome.NoChange();

            switch (outcome)
            {
                case SnapOutcome.Join join:
                    OrbweaverSettings.AssignTeamToGroup(keys[0], keys.Skip(1), join.GroupId);
                    break;

                case SnapOutcome.Pair:
                    // A new user group holding both whole teams, anchored at the
                    // midpoint of the two teams' disc centroids.
                    var paired = OrbweaverSettings.CreateOrbGroup();
                    OrbweaverSettings.AssignTeamToGroup(keys[0], keys.Skip(1).Concat(otherKeys), paired.Id);
                    var mine = Centroid(team.Select(host.DiscOf).ToList());
                    var theirs = Centroid(otherTeam.Select(host.DiscOf).ToList());
                    OrbweaverSettings.SetOrbGroupAnchor(
                        paired.Id,
                        new OrbweaverSettings.OrbPlacement(
                            (int)Math.Round((mine.X + theirs.X) / 2), (int)Math.Round((mine.Y + theirs.Y) / 2)));
                    break;

                case SnapOutcome.LeaveToFree:
                    foreach (var key in keys) OrbweaverSettings.UnassignOrbGroup(key);
                    break;

                case SnapOutcome.Leave:
                    var made = OrbweaverSettings.CreateOrbGroup();
                    OrbweaverSettings.AssignTeamToGroup(keys[0], keys.Skip(1), made.Id);

                    // Anchored at the middle of the team's discs, not at the
                    // pressed orb: a group's anchor is its shape's centre, and a
                    // member fanned out from its lead is not where that is.
                    OrbweaverSettings.SetOrbGroupAnchor(
                        made.Id, BoundsCentre(team.Select(host.DiscOf).ToList()));
                    break;
            }

            host.ReapplyArrangement();
            return outcome;
        }

        // A member's offset from its lead as it is stored: centre to centre, so
        // it does not depend on either orb's size (a member is drawn smaller
        // than its lead, and the two windows' top-lefts differ by more than their
        // centres do), in DIP so it survives a display-scale change. Discs are in
        // the host's screen units; `unitsPerDip` is OrbWindow.UnitsPerDip.
        // SessionManager.SaveMemberOffset hands the result to
        // OrbweaverSettings.SetOrbMemberOffset(soundKey, ...).
        internal static (double Dx, double Dy) MemberOffsetDip(
            (double X, double Y, double R) member, (double X, double Y, double R) lead, double unitsPerDip)
        {
            var s = unitsPerDip > 0 ? unitsPerDip : 1;
            return ((member.X - lead.X) / s, (member.Y - lead.Y) / s);
        }

        private static (double X, double Y) Centroid(IReadOnlyList<(double X, double Y, double R)> discs) =>
            (discs.Average(d => d.X), discs.Average(d => d.Y));

        private static OrbweaverSettings.OrbPlacement BoundsCentre(
            IReadOnlyList<(double X, double Y, double R)> discs)
        {
            var x = (discs.Min(d => d.X - d.R) + discs.Max(d => d.X + d.R)) / 2;
            var y = (discs.Min(d => d.Y - d.R) + discs.Max(d => d.Y + d.R)) / 2;
            return new OrbweaverSettings.OrbPlacement((int)Math.Round(x), (int)Math.Round(y));
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
                moving, host.GroupIdOf(pressedId), candidates, host.IsFree,
                OrbSnap.JoinDip * size, OrbSnap.LeaveDip * size);
        }
    }
}
