using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Threading;

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
        private readonly OrbWindow? _pressedWindow;
        private int _dx, _dy;

        // M1: the magnetic pull, one spring per axis, in screen units. Its target
        // is whatever the last Move computed; Step eases the springs towards it
        // even when the pointer is still, and back to nothing once the drop would
        // no longer snap. The team is drawn at pointer + pull.
        private static readonly SpringParams PullSpring = new(Omega: 28, Zeta: 0.9, RestDelta: 0.1, RestVelocity: 0.1);
        private SpringState _pullX, _pullY;
        private double _pullTargetX, _pullTargetY;
        private long _pullAt;
        private DispatcherTimer? _timer;

        // M5: the pointer's travel in DIP, for the velocity the drop is thrown with.
        private readonly FlingTracker _fling = new();


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

            _pullAt = OrbWindow.MotionClock();
            _fling.Observe(0, 0, _pullAt);

            // The orb the pointer is on is the one that squashes; a backdrop
            // press has no orb under it.
            if (source == DragSource.Orb) _pressedWindow = host.WindowOf(pressedId);
        }

        // Called with the pointer's travel since the press, in physical pixels.
        // The caller has already decided the press is a drag (DragPlan.Exceeded).
        internal void Move(int dx, int dy)
        {
            var now = OrbWindow.MotionClock();
            _dx = dx;
            _dy = dy;

            // At the pointer, with no pull: what the snap decision below is
            // measured from, so a pull already applied is never counted twice.
            Place(0, 0);

            // Only a lead drag snaps, so only a lead drag previews one — and
            // only a lead drag is pulled towards what it would snap to (M1).
            if (!WholeGroup && _leadDrag)
            {
                var (_, pullDip) = OrbDragCommit.PreviewWithPull(_host, _anchorId, _unitsPerDip);
                _pullTargetX = pullDip.X * _unitsPerDip;
                _pullTargetY = pullDip.Y * _unitsPerDip;
                StepPull(now);
                Place(_pullX.Value, _pullY.Value);
                StartTimer();
            }

            _pressedWindow?.SquashObserve(dx / Units, dy / Units);
            _fling.Observe(dx / Units, dy / Units, now);

            Refresh();
        }

        private double Units => _unitsPerDip > 0 ? _unitsPerDip : 1;

        // Draws the team at the pointer plus the given pull.
        private void Place(double pullX, double pullY)
        {
            var px = (int)Math.Round(pullX);
            var py = (int)Math.Round(pullY);
            foreach (var (orb, start) in _moving)
                orb.Position = new PixelPoint(start.X + _dx + px, start.Y + _dy + py);
        }

        private void StepPull(long now)
        {
            var dt = Math.Max(0, (now - _pullAt) / 1000.0);
            _pullAt = now;

            _pullX = Spring.Step(_pullX, _pullTargetX, dt, PullSpring);
            _pullY = Spring.Step(_pullY, _pullTargetY, dt, PullSpring);
        }

        // One tick of the timer: the pull keeps easing while the pointer is
        // still, and the drag's squash keeps relaxing (its own timer).
        internal void Step(long now)
        {
            StepPull(now);
            Place(_pullX.Value, _pullY.Value);
            Refresh();
            if (Spring.AtRest(_pullX, _pullTargetX) && Spring.AtRest(_pullY, _pullTargetY)) _timer?.Stop();
        }

        private void StartTimer()
        {
            if (!MotionPolicy.AllowsMagneticPull) return;

            _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e) => Step(OrbWindow.MotionClock());

        private void Refresh()
        {
            TeamLinks.Refresh();
            GroupBackdrops.Refresh();
        }

        internal SnapOutcome End()
        {
            _timer?.Stop();
            _pressedWindow?.SquashRelease();

            // M5: everything this drag moved is thrown with the pointer's
            // release velocity; the settle springs it on from there.
            var (vx, vy) = _fling.VelocityAt(OrbWindow.MotionClock());
            var fling = new Fling(_moving.Select(m => m.Orb.SessionId).ToList(), vx, vy);

            var outcome = OrbDragCommit.Release(_host, _anchorId, WholeGroup, _dx, _dy, _unitsPerDip, fling);

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
