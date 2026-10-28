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
        private readonly double _scaling;
        private readonly List<(OrbWindow Orb, PixelPoint Start)> _moving = new();
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
            _scaling = unitsPerDip;

            var team = host.TeamIdsOf(pressedId);
            var group = host.OrbIdsInGroup(host.GroupIdOf(pressedId));
            var (whole, ids) = DragPlan.Followers(source, shift, team, group);
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

            if (!WholeGroup) OrbDragCommit.Preview(_host, _anchorId, _scaling);

            TeamLinks.Refresh();
        }

        internal SnapOutcome End()
        {
            // Pinned only when nothing is arranging them: an arranged group is
            // re-laid-out by the release below, and a pin would make that layout
            // treat the dragged team as an exception again.
            if (!_host.IsArranged)
            {
                foreach (var (orb, _) in _moving) orb.PinAt(orb.Position);
            }

            // Positions are keyed by directory and a team usually runs in one,
            // so writing every member's spot would overwrite the lead's with an
            // offset copy of itself. First orb with a key wins.
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (orb, _) in _moving)
            {
                if (!string.IsNullOrEmpty(orb.PositionKey) && seen.Add(orb.PositionKey))
                    SessionManager.Instance?.RememberOrbPosition(orb);
            }

            return OrbDragCommit.Release(_host, _anchorId, WholeGroup, _dx, _dy, _scaling);
        }
    }
}
