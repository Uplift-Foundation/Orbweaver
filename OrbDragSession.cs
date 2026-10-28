using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Orbweaver
{
    // CB-268: one drag, from the first move to the release, whoever started it.
    // An orb press and a backdrop press differ only in who they move, which is
    // DragPlan's answer, so both build one of these and the handlers shrink to
    // "forward the pointer". Not excluded from coverage: it takes the host as a
    // parameter and moves windows that do not need to be on screen.
    internal sealed class OrbDragSession
    {
        private readonly IOrbDragHost _host;
        private readonly string _anchorId;
        private readonly double _unitsPerDip;
        private readonly List<(OrbWindow Orb, PixelPoint Start)> _moving = new();
        private readonly bool _leadDrag;
        private int _dx, _dy;

        internal bool WholeGroup { get; }

        // `pressedId` is the orb the pointer went down on, or for a backdrop any
        // orb of the group (the group's id is what counts then). Captured once,
        // on press: membership can change mid-drag, and an orb that joins the
        // team while you are moving it should not jump.
        internal OrbDragSession(
            IOrbDragHost host, string pressedId, DragSource source, bool shift, double unitsPerDip)
        {
            _host = host;
            _anchorId = pressedId;
            _unitsPerDip = unitsPerDip;

            var team = host.TeamIdsOf(pressedId);
            var groupId = host.GroupIdOf(pressedId);

            // In free mode the predefined groups are not groups to the user:
            // their orbs are loose, so Shift on one is a plain drag of its own
            // team rather than a move of every loose orb on the screen (F3).
            if (host.IsFree && OrbGroups.IsPredefined(groupId)) shift = false;

            _leadDrag = source == DragSource.Orb && host.IsRoot(pressedId);
            var (whole, ids) = DragPlan.Followers(
                source, shift, host.IsRoot(pressedId), pressedId, team, host.OrbIdsInGroup(groupId));
            WholeGroup = whole;

            foreach (var id in ids)
            {
                if (host.WindowOf(id) is { } window) _moving.Add((window, window.Position));
            }
        }

        // Called with the pointer's travel since the press, in physical pixels.
        // The caller has already decided the press is a drag (DragPlan.Exceeded).
        internal void Move(int dx, int dy)
        {
            _dx = dx;
            _dy = dy;

            foreach (var (orb, start) in _moving)
                orb.Position = new PixelPoint(start.X + dx, start.Y + dy);

            // Only a lead drag snaps, so only a lead drag previews one.
            if (!WholeGroup && _leadDrag) OrbDragCommit.Preview(_host, _anchorId, _unitsPerDip);

            TeamLinks.Refresh();
            GroupBackdrops.Refresh();
        }

        internal SnapOutcome End()
        {
            var outcome = OrbDragCommit.Release(_host, _anchorId, WholeGroup, _dx, _dy, _unitsPerDip);

            // Whatever is not in a user group after the release is loose and
            // stays where it was dropped: pinned, unless something is arranging
            // it. A user group's orbs are laid out by the group (F2), so neither
            // a pin nor a remembered spot belongs to them.
            //
            // Positions are keyed by directory and a team usually runs in one,
            // so writing every member's spot would overwrite the lead's with an
            // offset copy of itself. First orb with a key wins.
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (orb, _) in _moving)
            {
                if (!OrbGroups.IsPredefined(_host.GroupIdOf(orb.SessionId))) continue;

                if (!_host.IsArranged) orb.PinAt(orb.Position);
                if (!string.IsNullOrEmpty(orb.PositionKey) && seen.Add(orb.PositionKey))
                    SessionManager.Instance?.RememberOrbPosition(orb);
            }

            return outcome;
        }
    }
}
