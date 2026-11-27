using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using ArrowPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Platform;

namespace Orbweaver
{
    // Draws the arrow from an agent-team member's orb to its team lead's.
    //
    // A team member is a separate claude process — its own session id, its own
    // status file, its own orb — so a team of four looks exactly like four
    // unrelated sessions that happen to have appeared at the same time. The
    // arrow is the only thing that says otherwise. Which orb points where comes
    // from the member's `lead` field; see SessionStatus.Lead.
    //
    // Each arrow is its own transparent click-through window sized to the
    // bounding box of the two orbs it joins, rather than one screen-sized
    // overlay. A full-screen window would have to be re-rendered whenever any
    // orb moved, would cover other Spaces awkwardly, and on macOS would sit in
    // front of every other app as one large surface — the same reasoning that
    // keeps each orb in its own 56x56 window.
    internal static class TeamLinks
    {
        // Rendered in DIPs, so these are the shape's own dimensions rather than
        // anything screen-dependent. The shaft is tapered — thin where it
        // leaves the member, full width where it meets the head — which reads
        // as direction even before you register the arrowhead.
        // All three live in TeamLinkGeometry, with HeadHalfWidth below. HeadLength
        // in particular used to be declared here *as well*, with the same value —
        // so shortening an arrow to leave room for its head and drawing the head
        // were reading two different constants, and changing one of them would
        // have moved the head off the end of the shaft.
        private const double ShaftAtMember = TeamLinkGeometry.ShaftAtMember;
        private const double ShaftAtHead = TeamLinkGeometry.ShaftAtHead;
        private const double HeadLength = TeamLinkGeometry.HeadLength;

        // Aliased rather than duplicated, the same way the gaps below are: Place
        // sizes the window from this and ArrowGeometry draws the head with it, so
        // two copies would be two chances to disagree about whether the head fits
        // inside its own window.
        private const double HeadHalfWidth = TeamLinkGeometry.HeadHalfWidth;

        // The room an arrow needs lives in TeamLinkGeometry, shared with
        // whatever places the orbs — see the note there about the two drifting
        // apart and every arrow silently vanishing.
        private const double MemberGap = TeamLinkGeometry.MemberGap;
        private const double LeadGap = TeamLinkGeometry.LeadGap;
        private const double MinimumLength = TeamLinkGeometry.MinimumLength;

        // Below full opacity the arrows sit behind the orbs visually without
        // needing to sit behind them in the window stack (they can't — every
        // orb is topmost).
        private const double ArrowOpacity = 0.55;

        // The silk: spokes a little stronger than the threads between them, which
        // is how a real one catches the light.
        private const double SpokeOpacity = 0.5;
        private const double RingOpacity = 0.35;

        private static readonly List<(OrbWindow Member, OrbWindow Lead)> Pairs = new();
        private static readonly List<LinkWindow> Windows = new();
        private static readonly HashSet<OrbWindow> WebLeads = new();
        private static readonly List<WebWindow> WebWindows = new();
        private static readonly Stack<WebWindow> WebParked = new();

        // Parked and reused, never closed — the same rule as
        // ClaudeDesktopOverlay, and for the same reason: tearing an Avalonia
        // window down while its render target is in flight crashes the app, and
        // a team's arrows appear and disappear constantly as members are
        // spawned and finish.
        private static readonly Stack<LinkWindow> Parked = new();
        private const int MaxParked = 8;

        private static bool _visible = true;

        // Whether links are being drawn at all. Read by tests that switch them
        // off for their own duration and have to put back what they found.
        internal static bool IsVisible => _visible;

        // Replaces the whole set. Callers rebuild the pair list on every scan
        // rather than diffing it: it's a handful of pairs, and the alternative
        // is tracking membership changes in three places.
        // Excluded from coverage: creates and shows real transparent arrow windows
        // over live orb windows.
        [ExcludeFromCodeCoverage]
        public static void Update(
            IEnumerable<(OrbWindow Member, OrbWindow Lead)> pairs, IEnumerable<OrbWindow>? webLeads = null)
        {
            Pairs.Clear();
            Pairs.AddRange(pairs);

            // The leads whose team is on the web shape. Only those get the web
            // drawn; on any other shape a link is a link, as it always was.
            WebLeads.Clear();
            if (webLeads is not null) WebLeads.UnionWith(webLeads);

            Refresh();
        }

        // Re-runs the geometry without touching the set — what a drag calls, on
        // every pointer move, so it does no allocation beyond the geometry it
        // has to rebuild.
        // Excluded from coverage: measures live orb windows and repositions real
        // arrow windows; the arithmetic between the two is TeamLinkGeometry.Place,
        // which is tested.
        [ExcludeFromCodeCoverage]
        public static void Refresh()
        {
            if (!_visible)
            {
                HideAll();
                return;
            }

            // What the links look like is the thread style's to say: silk, or the
            // arrows the app has always drawn. Silk on a web-shaped team is the
            // whole web — spokes and the threads round them — and a lead needs
            // enough members for that to be a web at all; anywhere else silk is a
            // thread per link and arrows are arrows.
            var silk = OrbweaverSettings.ThreadStyle == OrbweaverSettings.ThreadStyleSilk;

            // Every member's state, so a tug starts when its session does something.
            // Only silk tugs; arrows never move, so with them nothing is tracked and
            // no frames are asked for. Members that have left are forgotten first, so
            // a stale one cannot keep the timer going.
            if (silk)
            {
                ThreadTug.Retain(Pairs.Select(p => (object)p.Member));
                foreach (var (member, _) in Pairs) ThreadTug.Observe(member, member.State);
            }
            else
            {
                ThreadTug.Reset();
            }

            var webs = silk
                ? Pairs
                    .GroupBy(p => p.Lead)
                    .Where(g => WebLeads.Contains(g.Key)
                        && g.Count(p => p.Member.IsVisible) >= OrbArrangement.WebMinimum)
                    .ToList()
                : new List<IGrouping<OrbWindow, (OrbWindow Member, OrbWindow Lead)>>();
            var webbed = new HashSet<OrbWindow>(webs.SelectMany(g => g.Select(p => p.Member)));
            var arrows = Pairs.Where(p => !webbed.Contains(p.Member)).ToList();

            while (Windows.Count < arrows.Count)
            {
                Windows.Add(Parked.Count > 0 ? Parked.Pop() : new LinkWindow());
            }

            for (var i = 0; i < arrows.Count; i++)
            {
                var (member, lead) = arrows[i];
                Windows[i].Apply(member, lead, silk);
            }

            // Spare windows are parked rather than dropped, so a member that
            // comes and goes reuses the same window each time.
            for (var i = Windows.Count - 1; i >= arrows.Count; i--)
            {
                Park(Windows[i]);
                Windows.RemoveAt(i);
            }

            while (WebWindows.Count < webs.Count)
            {
                WebWindows.Add(WebParked.Count > 0 ? WebParked.Pop() : new WebWindow());
            }

            for (var i = 0; i < webs.Count; i++)
            {
                WebWindows[i].Apply(webs[i].Key, webs[i].Select(p => p.Member).ToList());
            }

            for (var i = WebWindows.Count - 1; i >= webs.Count; i--)
            {
                WebWindows[i].Park();
                if (WebParked.Count < MaxParked) WebParked.Push(WebWindows[i]);
                WebWindows.RemoveAt(i);
            }

            // Frames keep coming while a thread is moving, and stop when none is.
            ThreadTug.Drive(Refresh);
        }

        // Orbs hidden means arrows hidden: an arrow between two invisible orbs
        // is a line from nowhere to nowhere.
        // Excluded from coverage: shows or hides real arrow windows.
        [ExcludeFromCodeCoverage]
        public static void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;

            if (visible) Refresh();
            else HideAll();
        }

        // Excluded from coverage: hides real arrow windows.
        [ExcludeFromCodeCoverage]
        private static void HideAll()
        {
            foreach (var window in Windows) window.Park();
            foreach (var window in WebWindows) window.Park();
        }

        // Excluded from coverage: hides a real arrow window and returns it to the
        // pool.
        [ExcludeFromCodeCoverage]
        private static void Park(LinkWindow window)
        {
            window.Park();
            if (Parked.Count < MaxParked) Parked.Push(window);
        }

        // Excluded from coverage, as a class: what is left in here after the
        // outline maths moved to TeamLinkGeometry is nothing but a native window.
        // The constructor builds a transparent, undecorated, non-activating one;
        // Apply() reads PointToScreen off two real orbs and calls Show(); Park()
        // calls Hide(); MakeClickThrough() sends setIgnoresMouseEvents: to an
        // NSWindow through objc_msgSend on macOS and rewrites GWL_EXSTYLE through
        // user32 on Windows. Under the headless platform there is no window for
        // any of it to act on.
        //
        // Apply()'s catch is part of that: PointToScreen throws when an orb has no
        // platform window yet, which is a real race during startup and not
        // something a test can arrange without a window either.
        [ExcludeFromCodeCoverage]
        private sealed class LinkWindow : Window
        {
            private readonly ArrowPath _arrow;
            private readonly ArrowPath _thread;
            private readonly SolidColorBrush _brush = new(Colors.White, ArrowOpacity);
            private readonly SolidColorBrush _threadBrush = new(Colors.White, ArrowOpacity);
            private bool _shown;

            public LinkWindow()
            {
                WindowDecorations = WindowDecorations.None;
                Background = Brushes.Transparent;
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                CanResize = false;
                SizeToContent = SizeToContent.Manual;

                _arrow = new ArrowPath
                {
                    Fill = _brush,
                    Stretch = Stretch.None,      // geometry is already in window coordinates
                    IsHitTestVisible = false
                };

                // The silk style's link: a stroke and not a filled outline, so it
                // is its own shape. Only one of the two has geometry at a time.
                _thread = new ArrowPath
                {
                    Stroke = _threadBrush,
                    StrokeThickness = 1.2,
                    StrokeLineCap = PenLineCap.Round,
                    Stretch = Stretch.None,
                    IsHitTestVisible = false
                };

                var layers = new Grid();
                layers.Children.Add(_arrow);
                layers.Children.Add(_thread);
                Content = layers;

                // Follows its orbs across Spaces, since they do.
                Opened += (_, _) =>
                {
                    this.ShowOnAllSpaces();
                    MakeClickThrough();
                };
            }

            public void Park()
            {
                if (!_shown) return;
                Hide();
                _shown = false;
            }

            public void Apply(OrbWindow member, OrbWindow lead, bool silk = false)
            {
                // Nothing to join yet, or nothing to see: an orb with no
                // platform window can't be measured, and a hidden one has no
                // position worth pointing at.
                if (!member.IsVisible || !lead.IsVisible)
                {
                    Park();
                    return;
                }

                // How many units of Window.Position there are to a DIP —
                // measured rather than assumed, because the answer is 1 on
                // macOS (Avalonia hands out screen coordinates in points, which
                // are already the DIPs a 56x56 window is laid out in) and the
                // display scaling on Windows. Getting this from Scaling instead
                // put the arrows half an orb off on a Retina Mac.
                //
                // Both orbs are treated as being at the member's scale: an
                // arrow spanning two monitors of different densities is a few
                // pixels out, which doesn't show on a decoration and is much
                // cheaper than resolving each orb's screen every drag frame.
                double scale;
                Point from, to;
                try
                {
                    var origin = member.PointToScreen(new Point(0, 0));
                    scale = (member.PointToScreen(new Point(64, 0)).X - origin.X) / 64.0;
                    if (scale <= 0) scale = 1;

                    from = Centre(member);
                    to = Centre(lead);
                }
                catch
                {
                    // An orb torn down mid-scan. The next refresh has it.
                    Park();
                    return;
                }

                // Everything from here to the assignments below used to be
                // inline. It is arithmetic on two measured points and two radii,
                // and it is the part that decides whether there is an arrow at
                // all, so it lives in TeamLinkGeometry next to the clearance rule
                // it has to agree with — see the note there about the two
                // drifting apart and every arrow silently vanishing.
                if (TeamLinkGeometry.Place(
                        from, to, member.OrbRadius, lead.OrbRadius, scale,
                        silk ? ThreadRoom : 0)
                    is not { } placement)
                {
                    Park();
                    return;
                }

                Position = placement.Position;
                Width = placement.Width;
                Height = placement.Height;

                if (silk)
                {
                    // A fine thread that sags a little between its ends and
                    // shivers when its member is working. In the member's colour,
                    // since without arrowheads that is what says whose it is.
                    var (offset, glow) = ThreadTug.Now(member);
                    _arrow.Data = null;
                    _thread.Data = ThreadGeometry(placement.Start, placement.End, offset);
                    _threadBrush.Color = member.LinkColor;
                    _threadBrush.Opacity = Math.Min(1, ArrowOpacity + glow * 0.4);
                }
                else
                {
                    _thread.Data = null;
                    _arrow.Data = ArrowGeometry(
                        placement.Start, placement.End, placement.Ux, placement.Uy);
                }

                // The member's colour, not the lead's: several members pointing
                // at one lead stay distinguishable, and an arrow is the member's
                // statement about where it reports.
                if (_brush.Color != member.LinkColor) _brush.Color = member.LinkColor;

                if (_shown) return;
                _shown = true;
                Show();
            }

            // An orb sits at DIP CentreDip in its own window regardless of how
            // big the team role draws it, so this point is the same for a
            // member and a lead — (28,28) at the default size, scaled with the
            // user's orb size since CB-198. Asked of the window rather than derived from
            // Position, so it is right on both platforms' coordinate systems.
            //
            // (28,28) is the centre of OrbWindow's Root, which is pinned to
            // 56x56 and anchored top-left precisely so this stays true where
            // the OS won't let the window itself be 56x56 — see the comment on
            // Root in OrbWindow.axaml. Arrows were landing off-centre on
            // Windows for exactly that reason before it was pinned.
            private static Point Centre(OrbWindow orb)
            {
                var centre = orb.PointToScreen(new Point(orb.CentreDip, orb.CentreDip));
                return new Point(centre.X, centre.Y);
            }

            // Room a silk link needs beyond a straight arrow's: its sag, plus the
            // widest a tug swings it.
            private const double ThreadRoom = 10;

            // A single quadratic curve from the member to the lead, bowed a little
            // to one side the way silk hangs, plus whatever a tug has added.
            private static StreamGeometry ThreadGeometry(Point start, Point end, double tug)
            {
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                var nx = length > 0 ? -dy / length : 0;
                var ny = length > 0 ? dx / length : 0;

                // The control point is twice as far out as the curve's middle.
                var bow = (length * 0.03 + tug) * 2;
                var control = new Point(
                    (start.X + end.X) / 2 + nx * bow, (start.Y + end.Y) / 2 + ny * bow);

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(start, false);
                    context.QuadraticBezierTo(control, end);
                    context.EndFigure(false);
                }

                return geometry;
            }

            // Streams TeamLinkGeometry.ArrowOutline into the one Avalonia type
            // that has to be built here. The shape itself is decided there, so
            // that it can be asserted on without a window — this method is only
            // the part that cannot be.
            private static StreamGeometry ArrowGeometry(Point start, Point end, double ux, double uy)
            {
                var outline = TeamLinkGeometry.ArrowOutline(start, end, ux, uy);

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(outline[0], true);
                    for (var i = 1; i < outline.Length; i++) context.LineTo(outline[i]);
                    context.EndFigure(true);
                }

                return geometry;
            }

            // An arrow that ate clicks would be worse than no arrow: it lies
            // across the gap between two orbs, which is exactly where you drag
            // one to. See OverlayClickThrough for how the window server is told.
            private void MakeClickThrough() => OverlayClickThrough.Apply(this);
        }

        // A team's silk: its spokes and the threads round them, in one transparent
        // click-through window centred on the lead. Same rules as LinkWindow —
        // parked and reused, never closed — and excluded from coverage for the
        // same reason; the shape is TeamWebGeometry.Build, which is tested.
        [ExcludeFromCodeCoverage]
        private sealed class WebWindow : Window
        {
            private readonly ArrowPath _spokes;
            private readonly Grid _layers = new();
            private readonly List<ArrowPath> _pulses = new();
            private readonly ArrowPath _rings;
            private bool _shown;

            // Room past the outermost point for the stroke and the sag.
            private const double Pad = 6;

            public WebWindow()
            {
                WindowDecorations = WindowDecorations.None;
                Background = Brushes.Transparent;
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                Topmost = true;
                ShowInTaskbar = false;
                ShowActivated = false;
                CanResize = false;
                SizeToContent = SizeToContent.Manual;

                _spokes = new ArrowPath
                {
                    Stroke = new SolidColorBrush(Colors.White, SpokeOpacity),
                    StrokeThickness = 1.1,
                    StrokeLineCap = PenLineCap.Round,
                    Stretch = Stretch.None,
                    IsHitTestVisible = false
                };
                _rings = new ArrowPath
                {
                    Stroke = new SolidColorBrush(Colors.White, RingOpacity),
                    StrokeThickness = 0.8,
                    StrokeLineCap = PenLineCap.Round,
                    Stretch = Stretch.None,
                    IsHitTestVisible = false
                };

                // Spokes being tugged are drawn over the calm ones, each in its own
                // shape because each pulses in its own member's colour and a path
                // has one brush. They are made as they are needed and reused.
                _layers.Children.Add(_rings);
                _layers.Children.Add(_spokes);
                Content = _layers;

                Opened += (_, _) =>
                {
                    this.ShowOnAllSpaces();
                    OverlayClickThrough.Apply(this);
                };
            }

            public void Park()
            {
                if (!_shown) return;
                Hide();
                _shown = false;
            }

            public void Apply(OrbWindow lead, IReadOnlyList<OrbWindow> members)
            {
                if (!lead.IsVisible)
                {
                    Park();
                    return;
                }

                double scale;
                Point hub;
                var offsets = new List<Point>();
                var visible = new List<OrbWindow>();

                try
                {
                    var origin = lead.PointToScreen(new Point(0, 0));
                    scale = (lead.PointToScreen(new Point(64, 0)).X - origin.X) / 64.0;
                    if (scale <= 0) scale = 1;

                    var c = lead.PointToScreen(new Point(lead.CentreDip, lead.CentreDip));
                    hub = new Point(c.X, c.Y);

                    foreach (var member in members.Where(m => m.IsVisible))
                    {
                        var m = member.PointToScreen(new Point(member.CentreDip, member.CentreDip));
                        offsets.Add(new Point((m.X - hub.X) / scale, (m.Y - hub.Y) / scale));
                        visible.Add(member);
                    }
                }
                catch
                {
                    // An orb torn down mid-scan. The next refresh has it.
                    Park();
                    return;
                }

                if (TeamWebGeometry.Build(offsets, lead.OrbRadius) is not { } frame)
                {
                    Park();
                    return;
                }

                var half = frame.Reach + Pad;
                Position = new PixelPoint(
                    (int)Math.Floor(hub.X - half * scale), (int)Math.Floor(hub.Y - half * scale));
                Width = half * 2;
                Height = half * 2;

                Point At(Point p) => new(p.X + half, p.Y + half);

                var calm = new StreamGeometry();
                var pulses = new List<(StreamGeometry Shape, Color Colour, double Glow)>();

                using (var calmCtx = calm.Open())
                {
                    for (var i = 0; i < frame.Spokes.Count; i++)
                    {
                        var (from, to) = frame.Spokes[i];

                        // The spoke's tug is its loudest member's: several members
                        // out along one thread shake the one thread, and it pulses
                        // in that member's colour.
                        var offset = 0.0;
                        var glow = 0.0;
                        OrbWindow? loudest = null;
                        foreach (var index in frame.SpokeMembers[i])
                        {
                            var (o, g) = ThreadTug.Now(visible[index]);
                            if (g > glow || loudest is null && g > 0)
                            {
                                glow = g;
                                offset = o;
                                loudest = visible[index];
                            }
                        }

                        if (loudest is null)
                        {
                            calmCtx.BeginFigure(At(from), false);
                            calmCtx.LineTo(At(to));
                            calmCtx.EndFigure(false);
                            continue;
                        }

                        var shape = new StreamGeometry();
                        using (var ctx = shape.Open())
                        {
                            ctx.BeginFigure(At(from), false);

                            // Bowed sideways: the control point is twice as far
                            // out as the middle of the curve it draws.
                            var dx = to.X - from.X;
                            var dy = to.Y - from.Y;
                            var length = Math.Sqrt(dx * dx + dy * dy);
                            var nx = -dy / length;
                            var ny = dx / length;
                            var control = new Point(
                                (from.X + to.X) / 2 + nx * offset * 2, (from.Y + to.Y) / 2 + ny * offset * 2);

                            ctx.QuadraticBezierTo(At(control), At(to));
                            ctx.EndFigure(false);
                        }

                        pulses.Add((shape, loudest.LinkColor, glow));
                    }
                }

                // One shape per pulsing spoke, in the member's colour and as bright
                // as the tug is; any left over from last frame are emptied.
                while (_pulses.Count < pulses.Count)
                {
                    var path = new ArrowPath
                    {
                        Stroke = new SolidColorBrush(Colors.White, 1),
                        StrokeThickness = 1.8,
                        StrokeLineCap = PenLineCap.Round,
                        Stretch = Stretch.None,
                        IsHitTestVisible = false
                    };
                    _pulses.Add(path);
                    _layers.Children.Add(path);
                }

                for (var i = 0; i < _pulses.Count; i++)
                {
                    if (i >= pulses.Count)
                    {
                        _pulses[i].Data = null;
                        continue;
                    }

                    var (shape, colour, glow) = pulses[i];
                    var brush = (SolidColorBrush)_pulses[i].Stroke!;
                    brush.Color = colour;
                    brush.Opacity = Math.Min(1, SpokeOpacity + glow * 0.5);
                    _pulses[i].Data = shape;
                }

                var rings = new StreamGeometry();
                using (var ctx = rings.Open())
                {
                    foreach (var strand in frame.Rings)
                    {
                        ctx.BeginFigure(At(strand.From), false);
                        ctx.QuadraticBezierTo(At(strand.Control), At(strand.To));
                        ctx.EndFigure(false);
                    }
                }

                _spokes.Data = calm;
                _rings.Data = rings;

                if (_shown) return;
                _shown = true;
                Show();
            }
        }
    }
}
