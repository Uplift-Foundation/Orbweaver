using Avalonia;

namespace Orbweaver
{
    // How much room an arrow between two orbs needs, on its own so that both
    // the thing that draws arrows and the thing that positions orbs can agree
    // about it — and so the arrangement tests can ask without dragging a window
    // toolkit in behind them.
    //
    // The two used to disagree: orbs were fanned closer than an arrow can be
    // drawn, so TeamLinks silently parked every one and a team looked like
    // unrelated orbs sitting near each other.
    internal static class TeamLinkGeometry
    {
        // Clearance between an orb's visible edge and the arrow. The lead end
        // gets more so the arrowhead reads as pointing *at* the orb rather than
        // touching it.
        public const double MemberGap = 4;
        public const double LeadGap = 7;

        public const double HeadLength = 9;

        // An arrow shorter than its own head is a blob, not an arrow.
        public const double MinimumLength = HeadLength + 4;

        // Edge to edge, everything an arrow needs before it is worth drawing.
        public const double RequiredClearance = MemberGap + LeadGap + MinimumLength;

        // The same thing as a centre-to-centre distance, in whatever units the
        // radii are given in. Both radii and the answer are DIPs; multiply by
        // the display scale to compare against screen pixels.
        public static double MinimumCentreDistance(double memberRadius, double leadRadius)
            => memberRadius + leadRadius + RequiredClearance;

        // Room for the widest part of the head on either side of the line, plus
        // a pixel so nothing is clipped by rounding. Here rather than in
        // TeamLinks because Place below needs it and Place is what decides the
        // window's size.
        public const double HeadHalfWidth = 4.5;

        // How wide the shaft is, at each end. It tapers: narrow where it leaves
        // the member, wider where it meets the head, so the whole thing reads as
        // one arrow rather than a stick with a triangle balanced on it.
        //
        // Here rather than in TeamLinks because ArrowOutline below is what uses
        // them, and ArrowOutline is here so it can be tested without a window
        // toolkit — the same reason everything else in this file is.
        public const double ShaftAtMember = 0.7;
        public const double ShaftAtHead = 1.7;

        // The outline of one arrow, as the seven points of a single closed
        // figure, walking the member end's near edge up to the head, around the
        // point, and back down the far edge.
        //
        // One filled outline rather than a stroked line plus a separate polygon:
        // two shapes show a seam wherever their anti-aliased edges meet, and the
        // join is in the middle of the arrow where it is most visible.
        //
        // (ux, uy) is the unit vector from start towards end. It is passed in
        // rather than derived because the caller has already normalised it to
        // decide whether the arrow is long enough to draw at all, and computing
        // it twice invites the two answers differing for a zero-length link.
        public static Point[] ArrowOutline(Point start, Point end, double ux, double uy)
        {
            // Perpendicular, for offsetting each edge off the centre line.
            var nx = -uy;
            var ny = ux;

            // Where the head's flat base sits: back along the line from the tip.
            var baseX = end.X - ux * HeadLength;
            var baseY = end.Y - uy * HeadLength;

            return
            [
                new Point(start.X + nx * ShaftAtMember, start.Y + ny * ShaftAtMember),
                new Point(baseX + nx * ShaftAtHead, baseY + ny * ShaftAtHead),
                new Point(baseX + nx * HeadHalfWidth, baseY + ny * HeadHalfWidth),
                end,
                new Point(baseX - nx * HeadHalfWidth, baseY - ny * HeadHalfWidth),
                new Point(baseX - nx * ShaftAtHead, baseY - ny * ShaftAtHead),
                new Point(start.X - nx * ShaftAtMember, start.Y - ny * ShaftAtMember),
            ];
        }

        // Where an arrow's own window goes, and where the shaft runs inside it.
        //
        // Position is in the platform's window coordinates; everything else is in
        // DIPs relative to that position, which is what the geometry is drawn in.
        internal readonly record struct ArrowPlacement(
            PixelPoint Position, double Width, double Height,
            Point Start, Point End, double Ux, double Uy);

        // The arrow between two orb centres, or null for "do not draw one".
        //
        // Split out of TeamLinks.LinkWindow.Apply, which has to measure two live
        // windows before it can ask this and set a real window's position after.
        // This is the arithmetic in between, and it is the part that decides
        // whether an arrow exists at all — the null cases are the ones that made
        // a team look like unrelated orbs sitting near each other, which is the
        // failure this file was created to stop happening silently.
        //
        // `from` and `to` are orb centres in platform window coordinates, the
        // radii are in DIPs, and `scale` is how many units of Window.Position
        // there are to a DIP — 1 on macOS, the display scaling on Windows.
        internal static ArrowPlacement? Place(
            Point from, Point to, double memberRadius, double leadRadius, double scale,
            double extraPad = 0)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);

            // Stacked exactly, or as near as makes no difference: there is no
            // direction to point in.
            if (distance < 1) return null;

            var ux = dx / distance;
            var uy = dy / distance;

            var startGap = (memberRadius + MemberGap) * scale;
            var endGap = (leadRadius + LeadGap) * scale;

            // Overlapping, stacked, or dragged on top of each other. Drawing
            // here would produce a blob rather than an arrow.
            var span = distance - startGap - endGap;
            if (span < MinimumLength * scale) return null;

            var startX = from.X + ux * startGap;
            var startY = from.Y + uy * startGap;
            var endX = to.X - ux * endGap;
            var endY = to.Y - uy * endGap;

            // `extraPad` is room beyond the arrow's own, for a thread that bows out
            // from the straight line between its ends — a silk link sags, and
            // swings when it is tugged, and would be clipped by a window cut to
            // the line.
            var pad = (HeadHalfWidth + 1 + extraPad) * scale;

            var left = Math.Min(startX, endX) - pad;
            var top = Math.Min(startY, endY) - pad;
            var right = Math.Max(startX, endX) + pad;
            var bottom = Math.Max(startY, endY) + pad;

            var position = new PixelPoint((int)Math.Floor(left), (int)Math.Floor(top));

            return new ArrowPlacement(
                position,
                (right - left) / scale,
                (bottom - top) / scale,
                new Point((startX - position.X) / scale, (startY - position.Y) / scale),
                new Point((endX - position.X) / scale, (endY - position.Y) / scale),
                ux,
                uy);
        }
    }
}
