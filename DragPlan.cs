using System;
using System.Collections.Generic;

namespace Orbweaver
{
    internal enum DragSource { Orb, Backdrop }

    // CB-268: who moves when something is dragged. Pure, so the gesture table
    // (orb vs Shift vs backdrop, solo vs lead vs member vs nested team) is a
    // list of cases rather than something to be driven through a pointer.
    internal static class DragPlan
    {
        /// <summary>
        /// How far, in DIP, a pressed pointer must travel before the press is a
        /// drag rather than a click. It used to be an inline 6 compared against
        /// screen units, which is half the distance on a Windows display at 200%.
        /// </summary>
        internal const double ThresholdDip = 6;

        /// <summary>
        /// True once the pointer has moved far enough to be a drag. (dx, dy) are
        /// PointToScreen deltas and <paramref name="unitsPerDip"/> is the measured
        /// number of those units in one DIP (<see cref="OrbWindow.UnitsPerDip"/>).
        /// </summary>
        internal static bool Exceeded(double dxPx, double dyPx, double unitsPerDip)
        {
            var s = unitsPerDip > 0 ? unitsPerDip : 1;
            return Math.Abs(dxPx) / s >= ThresholdDip || Math.Abs(dyPx) / s >= ThresholdDip;
        }

        // teamIds: the pressed orb plus its whole team (root lead, members,
        // nested members). groupIds: every orb in its group.
        //   Orb, !shift, root   -> (false, teamIds)    snap/unsnap is decided on release
        //   Orb, !shift, member -> (false, [pressed])  only that member moves (T2 revised)
        //   Orb, shift          -> (true,  groupIds)
        //   Backdrop, any       -> (true,  groupIds)
        internal static (bool WholeGroup, IReadOnlyList<string> Ids) Followers(
            DragSource source, bool shift, bool isRoot, string pressedId,
            IReadOnlyList<string> teamIds, IReadOnlyList<string> groupIds) =>
            source == DragSource.Backdrop || shift
                ? (true, groupIds)
                : isRoot ? (false, teamIds) : (false, new[] { pressedId });
    }
}
