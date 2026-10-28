using System.Diagnostics.CodeAnalysis;
using Avalonia;

namespace Orbweaver
{
    // Where every orb goes when you arrange them into a shape.
    //
    // Pulled out of SessionManager and made pure — no windows, no screens, no
    // settings — because it could not be tested where it was, and it needed to
    // be. A run of bugs here were all of a kind that a person notices only by
    // looking at the screen and squinting: patterns half off the edge, orbs
    // drawn on top of each other, a team fanned so far from its lead that the
    // arrows stopped being drawn at all. Each fix broke a case the last one had
    // fixed, because nothing checked the other cases.
    //
    // Everything it needs arrives as arguments, and everything it decides comes
    // back as an array. tests/ArrangementTests walks every shape at every
    // spacing across a range of counts and team shapes and asserts the things
    // that were going wrong.
    //
    // Positions are window top-left corners in physical pixels, which is what
    // OrbWindow.Position takes. The circle a person sees is 36 DIP inside a
    // 56 DIP window, and a team member's is 72% of that again — those three
    // numbers, mixed up, caused most of the bugs.
    internal static class OrbArrangement
    {
        internal const double WindowDip = 56;
        internal const double CircleDip = 36;
        internal const double MemberScale = 0.72;

        // Center, when given, is where the shape is drawn instead of the
        // middle of Work — the saved anchor that keeps a shape from
        // recentering every time an orb joins or leaves. Work still bounds
        // where Fit/Slide are allowed to put it.
        internal readonly record struct Layout(
            PixelRect Work,
            double Scale,
            string Shape,
            double Spacing,
            PixelPoint? Center = null);

        // CB-268: one group's own shape and anchor, as handed to ComputeGroups.
        // A group list is indexed by *slot*, and the slot order comes from the
        // group table rather than from which sessions exist: main, heartbeats,
        // crons, then user groups ordered by id (see OrbClusters.GroupOf for
        // why a dense index was rejected). Center is the group's saved anchor
        // in physical pixels, null if it has never been drawn.
        //
        // groupOf[i] is a slot index into `groups` (see ComputeGroups below),
        // with Compute's rules: out of range or missing reads as slot 0, and
        // only a root lead's slot is consulted — a member goes wherever its
        // lead is. layout.Work, Scale and Spacing apply to every group;
        // layout.Shape and layout.Center are ignored in favour of each
        // group's own. docs/cb268-contracts.md has the fallback rules.
        /// <summary>One group's shape and saved anchor (physical px), indexed by slot.</summary>
        internal readonly record struct GroupLayout(string Shape, PixelPoint? Center);

        // What ComputeGroups decided for one group that has orbs on screen —
        // one entry per non-empty slot, in ascending slot order; an empty slot
        // gets none. Landed is the anchor the group actually ended up at (the
        // asked-for one plus its total integer translation, CB-211's rule
        // applied per group), which the caller saves back when it differs.
        // SlotBounds is the union of the group's slot boxes in physical
        // pixels, the box SeparateGroups pushed. DrawnShape is the shape
        // really drawn — "grid" when a squeezed group fell back to one — and
        // is what GroupOutline is asked to draw. Orbs are the orb indices in
        // this group, members included, ascending.
        //
        // Orbs is an array, so record equality compares it by reference;
        // compare Orbs with SequenceEqual in tests.
        //
        // CurveCentre and CurveScale describe the curve the group's anchor orbs
        // (not their fanned team members) were placed on, in physical pixels,
        // after separation and translation: orb i's centre is
        // CurveCentre + CurveScale * Unit(DrawnShape, anchors)[i], to within the
        // pixel rounding and the nudges that keep neighbours apart. It is what
        // GroupOutline offsets outward to draw the group's own shape, instead of
        // re-fitting a curve to the discs. Found as the least-squares fit of the
        // placed centres to the shape's unit curve, which is exact for the
        // curve the placement started from and absorbs the rounding. A group of
        // one orb has no curve: CurveScale is 0 and CurveCentre is that orb's
        // centre. So has a line, a column or a grid (asked for or fallen back
        // to): CurveScale is 0 and CurveCentre is the middle of the anchors.
        // The fit is made from the final positions, so it moves by exactly the
        // integer delta the orbs do.
        /// <summary>Where one non-empty group landed, and what was drawn for it.</summary>
        internal readonly record struct GroupPlacement(
            int Slot,
            PixelPoint Landed,
            PixelRect SlotBounds,
            string DrawnShape,
            int[] Orbs,
            Point CurveCentre = default,
            double CurveScale = 0);

        // leadOf[i] is the index of orb i's team lead, or -1 if it leads itself.
        //
        // One shape for everything, which is what the app did before groups
        // existed and what it still does whenever every orb belongs to the same
        // one. Kept as its own entry point rather than made a special case at the
        // call site: the sweep in tests/ArrangementTests is written against it,
        // and every case it walks has to keep meaning the same thing.
        internal static PixelPoint[] Compute(
            int count, int[] leadOf, Layout layout, double[]? sizeOf = null)
            => count <= 0
                ? Array.Empty<PixelPoint>()
                : Compute(count, leadOf, new int[count], new[] { layout.Shape }, layout, sizeOf);

        // The same, with the orbs split across up to `shapes.Count` shapes drawn
        // side by side — see OrbClusters for what the groups mean and
        // Bands below for where each one is drawn.
        //
        // groupOf[i] is which shape orb i joins, an index into `shapes`; out of
        // range, or an array shorter than the orb count, reads as group 0. Only
        // an *anchor's* group is consulted. A team member is drawn hanging off
        // its lead, so the shape it belongs to is whichever one its lead is
        // standing in — asking its own group instead would let a lead in one
        // band fan its members into another, which is neither what the group
        // setting means nor something the arrows could survive.
        //
        // sizeOf[i] is orb i's user size (CB-198) — the slider in Settings or
        // that orb's own override, 1.0 being the size every orb always was.
        // It is a fourth number, and deliberately kept apart from the three
        // above and from Layout.Scale: Scale is the display's pixels per DIP,
        // MemberScale is what a team member is drawn at relative to its lead,
        // and a user size multiplies both without being either. Null, or an
        // array shorter than the orb count, reads as 1.0 for whatever it does
        // not cover. Every position returned is still a window top-left, for
        // *that orb's own* window, so a caller sets OrbWindow.Position to it
        // exactly as before.
        //
        // **The lattice is laid out for the largest orb present, and every orb
        // is centred in its slot.** Everything below works in slots one
        // largest-window wide — the shape, the fit, the bands, the fan-outs and
        // the separation pass — and only at the very end does each orb trade
        // the slot's top-left for its own, half its own window short of the
        // slot's centre. A smaller orb therefore sits wholly inside the
        // footprint a largest orb would have had, which makes the two things
        // the sweep cares most about true by construction rather than by
        // tuning: a slot on the screen holds an orb on the screen, and two
        // circles no bigger than the ones that were separated cannot overlap
        // more than those did.
        //
        // The price, stated so nobody mistakes it for a bug: small orbs sit as
        // far apart as the biggest one would. One orb at 200% among a dozen at
        // 60% spreads all of them out as though every one were at 200%. Packing
        // mixed sizes tightly means a separation pass that reasons about pairs
        // of different radii, fan-outs whose arrow distance depends on which
        // member it is, and bands sized by the orbs inside them — every one of
        // them a place the geometry has broken before when it was changed by
        // eye. Loose and provably right was preferred to tight and
        // probably right.
        internal static PixelPoint[] Compute(
            int count, int[] leadOf, int[] groupOf, IReadOnlyList<string> shapes, Layout layout,
            double[]? sizeOf = null)
            => ComputeCore(count, leadOf, groupOf, shapes, layout, sizeOf, out _, out _);

        // Compute's body, with one extra answer: whether the grid retry below
        // was taken *and kept*. ComputeGroups needs it to report the shape it
        // really drew (CB-268); Compute itself does not, and is this with the
        // answer thrown away, so every arrangement it returns is unchanged.
        private static PixelPoint[] ComputeCore(
            int count, int[] leadOf, int[] groupOf, IReadOnlyList<string> shapes, Layout layout,
            double[]? sizeOf, out bool gridded, out bool[] griddedSlots)
        {
            gridded = false;
            griddedSlots = new bool[Math.Max(1, shapes.Count)];
            if (count <= 0) return Array.Empty<PixelPoint>();

            var anchors = new List<int>();
            var members = new Dictionary<int, List<int>>();

            Classify(count, leadOf, anchors, members);

            var sizes = SizesFor(count, sizeOf);
            var largest = sizes.Max();

            var window = WindowFor(layout.Scale, largest);
            var circle = CircleDip * layout.Scale * largest;

            var placed = Arrange(
                count, leadOf, groupOf, shapes, layout, anchors, members, window, circle,
                gridWhenSqueezed: false, out var squeezed, out _);

            // Orbs the separation pass could not get apart, in an arrangement
            // the screen had to squeeze, are drawn again with every squeezed
            // shape as a grid — and the grid kept only if it is actually better.
            //
            // It took orb sizes to find this. At 200% a circle is 72 DIP, and
            // thirty of them in a row need about 2220 DIP of width that not one
            // test screen has; a column of thirteen needs more height than a
            // small laptop has; a ring of thirty is wider than that laptop is
            // tall. Fit squeezed each outline down to the screen regardless,
            // Separate pushed against the screen edges, and the sweep found
            // orbs up to 60px on top of each other. No spacing, turn or nudge
            // makes a row of thirty fit a width that holds twenty-six — only a
            // different shape does. A grid is the one that packs a rectangle,
            // and it is already one of the shapes, which is why it rather than
            // an invented wrap: Line's comment makes the same argument about a
            // wrapped column.
            //
            // Tried, measured and kept-if-better rather than predicted, and two
            // drafts are the reason. The first swapped to a grid whenever an
            // outline's neighbours were squeezed closer than Separate accepts,
            // and redrew 184 cases in the sweep at plain 1.0 size — thirty-orb
            // stars and hearts on a small laptop, too tight on paper, that
            // Separate had always resolved perfectly well. The second asked the
            // same question only after measuring an overlap, and missed a column
            // of ten 200% orbs on an 800px laptop: its outline *just* holds them,
            // at 76px a step against 74 needed, and the lead's fanned team has
            // nowhere left to go. Predicting capacity means modelling the fans,
            // the separation pass and the edges together; measuring the result
            // needs none of it. And an arrangement with nothing overlapping is
            // returned exactly as it would have been before this existed — by
            // construction, not by a threshold tuned until the sweep went quiet.
            if (squeezed && WorstGap(placed, leadOf, circle) < -Slack(circle, layout.Spacing))
            {
                var retried = Arrange(
                    count, leadOf, groupOf, shapes, layout, anchors, members, window, circle,
                    gridWhenSqueezed: true, out _, out var regridded);

                if (WorstGap(retried, leadOf, circle) > WorstGap(placed, leadOf, circle))
                {
                    placed = retried;
                    gridded = true;
                    griddedSlots = regridded;
                }
            }

            // From slots to orbs: each one centred in the slot it was given, at
            // its own size. Integer halving, so the offset is the same whole
            // number of pixels however the slot moves — a dragged anchor moves
            // a small orb by exactly the delta it moves a big one.
            for (var i = 0; i < count; i++)
            {
                var inset = (window - WindowFor(layout.Scale, sizes[i])) / 2;
                placed[i] = new PixelPoint(placed[i].X + inset, placed[i].Y + inset);
            }

            return placed;
        }

        // Which of ComputeGroups' two routes produced an answer. Reported so a
        // test can say what it is asserting about; callers can ignore it.
        internal enum GroupPath
        {
            // Every group fitted on its own, then SeparateGroups got them apart.
            Separated,

            // The groups could not all be got apart (more of them, or bigger,
            // than the screen holds), so the whole set was drawn through
            // Bands + Compact, which always puts every orb somewhere.
            Fallback
        }

        // What kind of layout a ComputeGroups call is; see ComputeGroups.
        internal enum GroupLayoutMode
        {
            // The groups are being arranged: separated by SeparateGroups, with
            // the Bands + Compact fallback.
            Arranged,

            // User groups beside loose orbs: placed clear one at a time, best
            // effort near the anchor, never rebanded.
            Free
        }

        // The air SeparateGroups leaves between two groups' slot boxes, as a
        // fraction of the largest window. A box is already a window wider than
        // the discs it holds, and an outline stands off the discs by about the
        // same again, so boxes that merely touch would draw outlines that kiss.
        internal const double GroupGapWindows = 0.15;

        // CB-268: every group drawn in its own shape around its own anchor.
        //
        // **Independent first, separated after.** Each group is laid out by
        // exactly the machinery a lone shape has always used — ComputeCore on
        // that group's orbs alone, against the whole work area, with the
        // squeeze-then-grid retry — so a group's picture depends on nothing but
        // its own orbs, sizes, shape and anchor. That is what lets a drag move
        // one group without redrawing the others, and it is why this does not
        // share Bands: bands cut the screen by orb count, which makes every
        // group's shape depend on every other group's size.
        //
        // The price is that two groups can be asked for the same patch of
        // screen, which is SeparateGroups' job. When it cannot get them apart
        // (more groups than the screen holds at these sizes) the whole set is
        // drawn again through Bands + Compact, which has never failed to put
        // orbs somewhere, and the path out says so.
        //
        // **One path, whatever has been anchored.** A group with no anchor is
        // asked for the middle of its band — the strip of screen Bands would
        // give it, which is where it would have been drawn before groups had
        // anchors — and from there it is the same as one that has been. The
        // layout is a pure function of the anchors, so the first draw and every
        // redraw go through the same code and feeding Landed back in
        // reproduces the layout, orb for orb. (An earlier draft drew a first
        // draw through Bands + Compact and read Landed off it; the groups were
        // band-fitted then and screen-fitted on the next arrange, which resized
        // every shape once, and a heart's anchor was not the middle of its box.)
        //
        // The one exception is the fallback, which is Bands + Compact and does
        // not use anchors at all. It is a function of the same inputs too, so
        // its Landed is simply what was asked for: feeding that back asks the
        // same question and gets the same fallback.
        //
        // Landed is the asked-for anchor plus the group's *whole* integer
        // translation: what it took to bring the group's box inside the work
        // area (CB-211, per group) plus what SeparateGroups moved it by. No
        // second Compute is needed to find it, because the translation is the
        // only thing that ever moves a finished group.
        internal static (PixelPoint[] Positions, GroupPlacement[] Groups) ComputeGroups(
            int count, int[] leadOf, int[] groupOf,
            IReadOnlyList<GroupLayout> groups, Layout layout, double[]? sizeOf = null)
            => ComputeGroups(
                count, leadOf, groupOf, groups, layout, sizeOf,
                GroupLayoutMode.Arranged, Array.Empty<PixelRect>(), null, out _);

        internal static (PixelPoint[] Positions, GroupPlacement[] Groups) ComputeGroups(
            int count, int[] leadOf, int[] groupOf,
            IReadOnlyList<GroupLayout> groups, Layout layout, double[]? sizeOf,
            GroupLayoutMode mode, IReadOnlyList<PixelRect> obstacles,
            IReadOnlyList<(double Dx, double Dy)?>? memberOffsetOf = null)
            => ComputeGroups(count, leadOf, groupOf, groups, layout, sizeOf, mode, obstacles, memberOffsetOf, out _);

        internal static (PixelPoint[] Positions, GroupPlacement[] Groups) ComputeGroups(
            int count, int[] leadOf, int[] groupOf,
            IReadOnlyList<GroupLayout> groups, Layout layout, double[]? sizeOf, out GroupPath path)
            => ComputeGroups(
                count, leadOf, groupOf, groups, layout, sizeOf,
                GroupLayoutMode.Arranged, Array.Empty<PixelRect>(), null, out path);

        internal static (PixelPoint[] Positions, GroupPlacement[] Groups) ComputeGroups(
            int count, int[] leadOf, int[] groupOf,
            IReadOnlyList<GroupLayout> groups, Layout layout, double[]? sizeOf,
            GroupLayoutMode mode, IReadOnlyList<PixelRect> obstacles, out GroupPath path)
            => ComputeGroups(count, leadOf, groupOf, groups, layout, sizeOf, mode, obstacles, null, out path);

        // The same, saying which route it took.
        //
        // `mode` says what kind of layout this is, and it is a parameter rather
        // than something inferred from the obstacle list because both modes
        // take a list, and an empty one means different things in each.
        //
        //   Arranged  The groups are being arranged (the predefined ones, or
        //             user groups while arranging). They are separated from each
        //             other and from `obstacles` by SeparateGroups; if that
        //             cannot get them apart, the whole set is drawn through
        //             Bands + Compact, which always puts every orb somewhere and
        //             cannot see obstacles, so a group may then overlap one. An
        //             empty list is the arrangement exactly as it was before
        //             obstacles existed, bit for bit.
        //   Free      User groups beside orbs that are not in any group (the
        //             free-mode stack column, free-pinned orbs). Groups are
        //             placed by PlaceClear, in slot order, each at the nearest
        //             spot clear of the obstacles and of the groups before it,
        //             or — when there is none — where it was asked to be,
        //             overlapping, with the route reported as Fallback. There
        //             is no Bands + Compact then: it cannot see obstacles and
        //             would throw user groups across the screen and onto the
        //             stack column (CB-268 F2, F4). Landed is where the group
        //             was drawn, and feeding it back reproduces the layout
        //             exactly, because a group that found a clear spot is still
        //             clear there and one that found none stays where it is
        //             asked; SeparateGroups' pushing cannot promise that, since
        //             a second run starts from its own best effort and need not
        //             stop where the first did.
        //
        // `obstacles` are fixed rectangles in physical pixels, never null,
        // often empty.
        //
        // `memberOffsetOf[i]` is a team member's hand-placed offset from its
        // lead, in DIP, centre to centre (CB-268 T2). Null, a missing entry,
        // a non-finite one, or a root's, is ignored. A member with one is not
        // part of the layout above: the group is laid out, separated and
        // reported exactly as if nobody had moved anybody, so a member drag can
        // never shift its group, its box, its Landed or its curve. Only then is
        // the member put at its lead's final centre plus the offset times
        // layout.Scale — its own team following it rigidly — kept on the
        // screen, and, if that is on top of an orb, an obstacle or another
        // group's box, moved to the nearest spot that is not. Nothing else is
        // moved to make room: the lead above all. (Laying it out inside the
        // group instead was the first draft, and it is wrong twice: the group
        // is drawn about the middle of the screen, where an offset reaching
        // across the screen from a lead near one edge is clamped short; and a
        // member dragged to an edge pulled its whole group by the amount it
        // stuck out.)
        internal static (PixelPoint[] Positions, GroupPlacement[] Groups) ComputeGroups(
            int count, int[] leadOf, int[] groupOf,
            IReadOnlyList<GroupLayout> groups, Layout layout, double[]? sizeOf,
            GroupLayoutMode mode, IReadOnlyList<PixelRect> obstacles,
            IReadOnlyList<(double Dx, double Dy)?>? memberOffsetOf, out GroupPath path)
        {
            path = GroupPath.Separated;

            if (count <= 0) return (Array.Empty<PixelPoint>(), Array.Empty<GroupPlacement>());

            var slots = Math.Max(1, groups.Count);
            var shapes = new string[slots];
            for (var g = 0; g < slots; g++)
                shapes[g] = ShapeAt(groups.Select(x => x.Shape).ToList(), g, layout.Shape);

            // Members take their ROOT lead's slot whatever their own entry in
            // groupOf says (CB-268 T1) — a team is the unit of grouping.
            var slotOf = SlotsFor(count, leadOf, groupOf, slots);
            var sizes = SizesFor(count, sizeOf);

            var work = layout.Work;
            var middle = new PixelPoint(work.X + work.Width / 2, work.Y + work.Height / 2);

            // Where each slot is asked to be: its anchor, or else the middle of
            // the band Bands would cut for it (the same anchors-per-slot count
            // Arrange gives it, so a slot with a team is not counted per member).
            var anchorsIn = new int[slots];
            for (var i = 0; i < count; i++)
                if (!Resolves(i, leadOf, count)) anchorsIn[slotOf[i]]++;

            var bands = Bands(work, anchorsIn, WindowFor(layout.Scale, sizes.Max()));
            var askedOf = new PixelPoint[slots];
            for (var g = 0; g < slots; g++)
            {
                askedOf[g] = g < groups.Count && groups[g].Center is { } c
                    ? c
                    : new PixelPoint(bands[g].X + bands[g].Width / 2, bands[g].Y + bands[g].Height / 2);
            }

            var members = new List<int[]>();   // orb indices per used slot
            var usedSlots = new List<int>();
            for (var g = 0; g < slots; g++)
            {
                var orbs = Enumerable.Range(0, count).Where(i => slotOf[i] == g).ToArray();
                if (orbs.Length == 0) continue;
                members.Add(orbs);
                usedSlots.Add(g);
            }

            var positions = new PixelPoint[count];
            var boxes = new PixelRect[usedSlots.Count];
            var canonical = new PixelRect[usedSlots.Count];
            var drawn = new string[usedSlots.Count];
            var localOf = new int[count];

            for (var k = 0; k < usedSlots.Count; k++)
            {
                var g = usedSlots[k];
                var orbs = members[k];

                // Always laid out about the middle of the work area, whatever
                // the group's anchor is; the anchor is applied below as one
                // integer translation of the finished group. A shape drawn
                // *at* its anchor depends on it through rounding, Slide and
                // the fans turning away from edges, so a drag could change a
                // group's picture, and the anchor Landed reports could not be
                // exact. A rigid body moved by whole pixels has neither
                // problem: dragging moves it by exactly the delta, and feeding
                // Landed back reproduces it to the pixel.
                var asked = askedOf[g];

                var (placed, box, gridded) = LayGroup(orbs, leadOf, count, sizes, shapes[g], layout, middle, localOf);

                drawn[k] = gridded ? "grid" : shapes[g];

                for (var li = 0; li < orbs.Length; li++) positions[orbs[li]] = placed[li];

                canonical[k] = box;
                boxes[k] = new PixelRect(box.X + asked.X - middle.X, box.Y + asked.Y - middle.Y, box.Width, box.Height);
            }

            var gap = Math.Max(1, (int)Math.Round(WindowFor(layout.Scale, sizes.Max()) * GroupGapWindows));
            var free = mode == GroupLayoutMode.Free;
            var (moved, separated) = free
                ? PlaceClear(boxes, work, gap, obstacles)
                : SeparateGroups(boxes, work, gap, obstacles);

            if (!separated && !free)
            {
                path = GroupPath.Fallback;

                var fallback = ComputeCore(
                    count, leadOf, slotOf, shapes, layout with { Center = null }, sizes, out _, out var fallbackGrid);

                var described = Describe(fallback, leadOf, slotOf, shapes, sizes, layout.Scale, askedOf, fallbackGrid);

                PlaceByHand(fallback, leadOf, sizes, layout, memberOffsetOf, obstacles, described.Select(d => d.SlotBounds).ToArray(), described.Select(d => d.Orbs).ToArray());

                return (fallback, described);
            }

            path = separated ? GroupPath.Separated : GroupPath.Fallback;

            var placements = new GroupPlacement[usedSlots.Count];

            for (var k = 0; k < usedSlots.Count; k++)
            {
                // From where the group was drawn to where it ended up: the
                // anchor's own offset, plus whatever it took to get inside the
                // work area and clear of the groups before it.
                var dx = moved[k].X - canonical[k].X;
                var dy = moved[k].Y - canonical[k].Y;

                foreach (var i in members[k])
                    positions[i] = new PixelPoint(positions[i].X + dx, positions[i].Y + dy);

                var (curveAt, curveScale) = FitCurve(
                    drawn[k], members[k], positions, sizes, layout.Scale, leadOf, count);

                placements[k] = new GroupPlacement(
                    usedSlots[k],
                    new PixelPoint(middle.X + dx, middle.Y + dy),
                    moved[k],
                    drawn[k],
                    members[k],
                    curveAt,
                    curveScale);
            }

            PlaceByHand(positions, leadOf, sizes, layout, memberOffsetOf, obstacles, moved, members.ToArray());

            return (positions, placements);
        }

        // Pushes later boxes out of earlier ones and back inside `work`.
        //
        // `obstacles` are fixed rectangles — the stack column, orbs pinned
        // where the user left them — that outrank every box and never move;
        // boxes are pushed out of them like out of an earlier box, and
        // `Separated` is false if any is left within `gap` of one. They are
        // not in the returned list.
        //
        // Index order is priority: box 0 only ever moves to get inside the work
        // area, never for another box (or, with obstacles, for one of them
        // either — it is then pushed out of those). A box in the way is moved along the axis
        // where it has to go least — right, left, down or up, in that order on a
        // tie, so the answer is deterministic — but only to a place that is
        // still inside `work` when there is one: pushing a box off the screen
        // and sliding it back lands it on top of the box it was pushed from,
        // which is the failure a naive "push then clamp" loop never leaves.
        //
        // Fifty passes is a ceiling, not a target; the loop leaves the moment a
        // pass moves nothing. `Separated` is read off the final boxes, not off
        // whether the loop converged.
        internal static (PixelRect[] Boxes, bool Separated) SeparateGroups(
            IReadOnlyList<PixelRect> boxes, PixelRect work, int gap,
            IReadOnlyList<PixelRect>? obstacles = null)
        {
            // Obstacles go first, as extra boxes that outrank every group and
            // never move — not even to get inside the work area, which is where
            // they already are. Groups are pushed out of them exactly as they
            // are pushed out of earlier groups, with the same gap, so the whole
            // pass needs no second rule. `start` is the first box allowed to
            // move: with no obstacles that is box 1 (box 0 only slides into the
            // work area), with them it is the first group.
            var m = obstacles?.Count ?? 0;
            var n = m + boxes.Count;
            var start = m > 0 ? m : 1;
            var x = new int[n];
            var y = new int[n];
            var w = new int[n];
            var h = new int[n];

            for (var i = 0; i < n; i++)
            {
                var fixedBox = i < m;
                var box = fixedBox ? obstacles![i] : boxes[i - m];

                w[i] = box.Width;
                h[i] = box.Height;
                x[i] = fixedBox ? box.X : Within(box.X, w[i], work.X, work.Right);
                y[i] = fixedBox ? box.Y : Within(box.Y, h[i], work.Y, work.Bottom);
            }

            bool Close(int a, int b) =>
                x[a] < x[b] + w[b] + gap && x[b] < x[a] + w[a] + gap
                && y[a] < y[b] + h[b] + gap && y[b] < y[a] + h[a] + gap;

            for (var pass = 0; pass < 50; pass++)
            {
                var moved = false;

                for (var j = start; j < n; j++)
                {
                    var first = -1;
                    for (var i = 0; i < j && first < 0; i++)
                        if (Close(i, j)) first = i;

                    if (first < 0) continue;

                    // Every place j could stand flush against an earlier box:
                    // right, left, below, above each of them. Judged against
                    // *all* of them, not just the one it was pushed from — a
                    // box pushed clear of one neighbour and onto the next was
                    // the stalemate the first draft lived in, five boxes asked
                    // for one spot ending in a row with the last still on top.
                    var options = new List<(int X, int Y)>();
                    for (var i = 0; i < j; i++)
                    {
                        options.Add((x[i] + w[i] + gap, y[j]));
                        options.Add((x[i] - w[j] - gap, y[j]));
                        options.Add((x[j], y[i] + h[i] + gap));
                        options.Add((x[j], y[i] - h[j] - gap));
                    }

                    bool Fits((int X, int Y) o) =>
                        o.X >= work.X && o.X + w[j] <= work.Right && o.Y >= work.Y && o.Y + h[j] <= work.Bottom;

                    bool ClearOfEarlier((int X, int Y) o)
                    {
                        for (var i = 0; i < j; i++)
                        {
                            if (o.X < x[i] + w[i] + gap && x[i] < o.X + w[j] + gap
                                && o.Y < y[i] + h[i] + gap && y[i] < o.Y + h[j] + gap) return false;
                        }

                        return true;
                    }

                    int Cost((int X, int Y) o) => Math.Abs(o.X - x[j]) + Math.Abs(o.Y - y[j]);

                    // Nearest first by displacement; the generation order
                    // above (earlier box, then right, left, down, up) breaks a
                    // tie, so the answer is the same every time.
                    var best = -1;
                    for (var o = 0; o < options.Count; o++)
                    {
                        if (!Fits(options[o]) || !ClearOfEarlier(options[o])) continue;
                        if (best < 0 || Cost(options[o]) < Cost(options[best])) best = o;
                    }

                    // Nowhere inside the work area is clear of every earlier
                    // box: take the nearest way out of the first one in the
                    // way, inside the work area if it can be, and clamp.
                    if (best < 0)
                    {
                        for (var o = 0; o < 4; o++)
                        {
                            var at = first * 4 + o;
                            if (!Fits(options[at])) continue;
                            if (best < 0 || Cost(options[at]) < Cost(options[best])) best = at;
                        }

                        if (best < 0)
                        {
                            best = first * 4;
                            for (var o = 1; o < 4; o++)
                                if (Cost(options[first * 4 + o]) < Cost(options[best])) best = first * 4 + o;
                        }
                    }

                    x[j] = Within(options[best].X, w[j], work.X, work.Right);
                    y[j] = Within(options[best].Y, h[j], work.Y, work.Bottom);
                    moved = true;
                }

                if (!moved) break;
            }

            var separated = true;
            for (var j = start; j < n && separated; j++)
            for (var i = 0; i < j; i++)
            {
                if (Close(i, j)) { separated = false; break; }
            }

            return (Enumerable.Range(m, n - m).Select(i => new PixelRect(x[i], y[i], w[i], h[i])).ToArray(), separated);
        }

        // Free mode's placement: in order, each box goes to the nearest spot
        // inside `work` that is `gap` clear of every obstacle and every box
        // before it, or stays where it was asked (clamped into `work`) if there
        // is none. The second return is whether every box found a clear spot.
        //
        // The candidates are the flush positions — right of or left of each
        // rectangle it must avoid, below or above it, and the edges of `work` —
        // taken as every combination of an x and a y, with the asked position
        // among them. A box that fits anywhere clear fits at one of them: slide
        // it left and up until something stops it. Nearest means the least
        // total movement, and a tie goes to the earlier candidate.
        //
        // One pass, no iteration, and that is the point: the answer for a box
        // depends on where it was asked only through "nearest", so a box put at
        // a clear spot and asked again from there stays, and one that found no
        // spot stays too. Feeding the result back as the asked positions
        // reproduces it exactly, which a loop that pushes boxes around until it
        // gives up cannot say.
        internal static (PixelRect[] Boxes, bool Clear) PlaceClear(
            IReadOnlyList<PixelRect> boxes, PixelRect work, int gap, IReadOnlyList<PixelRect> obstacles)
        {
            var placed = new List<PixelRect>(obstacles);
            var result = new PixelRect[boxes.Count];
            var allClear = true;

            for (var b = 0; b < boxes.Count; b++)
            {
                var w = boxes[b].Width;
                var h = boxes[b].Height;
                var stayX = Within(boxes[b].X, w, work.X, work.Right);
                var stayY = Within(boxes[b].Y, h, work.Y, work.Bottom);

                var xs = new List<int> { stayX, work.X, Math.Max(work.X, work.Right - w) };
                var ys = new List<int> { stayY, work.Y, Math.Max(work.Y, work.Bottom - h) };

                foreach (var r in placed)
                {
                    xs.Add(Within(r.Right + gap, w, work.X, work.Right));
                    xs.Add(Within(r.X - w - gap, w, work.X, work.Right));
                    ys.Add(Within(r.Bottom + gap, h, work.Y, work.Bottom));
                    ys.Add(Within(r.Y - h - gap, h, work.Y, work.Bottom));
                }

                bool ClearAt(int x, int y)
                {
                    foreach (var r in placed)
                    {
                        if (x < r.Right + gap && r.X < x + w + gap && y < r.Bottom + gap && r.Y < y + h + gap)
                            return false;
                    }

                    return true;
                }

                var bestX = stayX;
                var bestY = stayY;
                var bestCost = int.MaxValue;

                foreach (var x in xs)
                foreach (var y in ys)
                {
                    var cost = Math.Abs(x - stayX) + Math.Abs(y - stayY);
                    if (cost >= bestCost || !ClearAt(x, y)) continue;

                    bestCost = cost;
                    bestX = x;
                    bestY = y;
                }

                if (bestCost == int.MaxValue) allClear = false;

                result[b] = new PixelRect(bestX, bestY, w, h);
                placed.Add(result[b]);
            }

            return (result, allClear);
        }

        // `pos` clamped so [pos, pos+size) sits in [lo, hi); the low edge wins
        // when the box is wider than the room.
        private static int Within(int pos, int size, int lo, int hi) => Math.Max(lo, Math.Min(pos, hi - size));

        // The slot a root lead's whole team shares: the root's own groupOf
        // entry, read as Compute reads one. Walks only while Resolves says the
        // orb really hangs off somebody, so a cycle or a dangling lead stops at
        // the orb Classify makes an anchor.
        private static int[] SlotsFor(int count, int[] leadOf, int[] groupOf, int slots)
        {
            var result = new int[count];

            for (var i = 0; i < count; i++)
            {
                var root = i;
                while (Resolves(root, leadOf, count)) root = leadOf[root];
                result[i] = SlotOf(root, groupOf, slots);
            }

            return result;
        }

        // The union of a group's slot boxes: each orb's window, outset back to
        // the slot it was centred in (the group's largest window).
        private static PixelRect SlotBox(PixelPoint[] placed, double[] sizes, double scale, int window)
        {
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

            for (var i = 0; i < placed.Length; i++)
            {
                var inset = (window - WindowFor(scale, sizes[i])) / 2;
                left = Math.Min(left, placed[i].X - inset);
                top = Math.Min(top, placed[i].Y - inset);
                right = Math.Max(right, placed[i].X - inset + window);
                bottom = Math.Max(bottom, placed[i].Y - inset + window);
            }

            return new PixelRect(left, top, right - left, bottom - top);
        }

        // Puts every hand-placed member (CB-268 T2) where the user left it,
        // on a finished layout: at its lead's centre plus the offset, with its
        // own team (everything that hangs off it) moved rigidly along, then on
        // the screen, then — if that is on an orb outside its own subtree, an
        // obstacle, or another group's box — at the nearest spot that is not,
        // found by walking outward in rings of fixed steps in a fixed order of
        // angles so the answer is the same every time. A member whose lead is
        // itself placed by hand is done after it. Only the member and its own
        // team move: the lead, the rest of the fan and every other group stay
        // exactly where the layout put them, which is why the group's box,
        // Landed and curve cannot depend on any of this.
        //
        // A spot that avoids orbs, obstacles and boxes is preferred; failing
        // that one that merely avoids orbs; failing that the clamped target,
        // which is the honest answer on a screen with no room left.
        private static void PlaceByHand(
            PixelPoint[] positions, int[] leadOf, double[] sizes, Layout layout,
            IReadOnlyList<(double Dx, double Dy)?>? offsets, IReadOnlyList<PixelRect>? obstacles,
            IReadOnlyList<PixelRect> boxes, int[][] groupOrbs)
        {
            if (offsets is null) return;

            var count = positions.Length;
            var work = layout.Work;

            bool ByHand(int i) =>
                i < offsets.Count && offsets[i] is { } o && double.IsFinite(o.Dx) && double.IsFinite(o.Dy)
                && Resolves(i, leadOf, count);

            var hand = Enumerable.Range(0, count).Where(ByHand).ToList();
            if (hand.Count == 0) return;

            int Depth(int i)
            {
                var d = 0;
                for (var at = i; Resolves(at, leadOf, count) && d <= count; at = leadOf[at]) d++;
                return d;
            }

            var children = new List<int>[count];
            for (var i = 0; i < count; i++)
                if (Resolves(i, leadOf, count)) (children[leadOf[i]] ??= new List<int>()).Add(i);

            var groupOfOrb = new int[count];
            for (var g = 0; g < groupOrbs.Length; g++) foreach (var o in groupOrbs[g]) groupOfOrb[o] = g;

            double Window(int i) => WindowFor(layout.Scale, sizes[i]);
            double Radius(int i) =>
                CircleDip * layout.Scale * sizes[i] / 2
                * (i < leadOf.Length && leadOf[i] >= 0 && leadOf[i] < count ? MemberScale : 1.0);

            foreach (var h in hand.OrderBy(Depth).ThenBy(i => i))
            {
                var subtree = new List<int> { h };
                for (var at = 0; at < subtree.Count; at++)
                    if (children[subtree[at]] is { } kids) subtree.AddRange(kids);

                var inside = new HashSet<int>(subtree);
                var lead = leadOf[h];
                var offset = offsets[h]!.Value;

                var wantX = positions[lead].X + Window(lead) / 2.0 + offset.Dx * layout.Scale - Window(h) / 2.0;
                var wantY = positions[lead].Y + Window(lead) / 2.0 + offset.Dy * layout.Scale - Window(h) / 2.0;

                var start = subtree.Select(i => new PixelPoint(
                    positions[i].X + (int)Math.Round(wantX) - positions[h].X,
                    positions[i].Y + (int)Math.Round(wantY) - positions[h].Y)).ToArray();

                // Onto the screen, as one piece, by the least that gets the
                // whole subtree's windows inside.
                int Shift(int low, int high, int lo, int hi) =>
                    low < lo ? lo - low : high > hi ? hi - high : 0;

                var sx = Shift(
                    subtree.Select((i, k) => start[k].X).Min(),
                    subtree.Select((i, k) => start[k].X + (int)Window(i)).Max(), work.X, work.Right);
                var sy = Shift(
                    subtree.Select((i, k) => start[k].Y).Min(),
                    subtree.Select((i, k) => start[k].Y + (int)Window(i)).Max(), work.Y, work.Bottom);

                // The subtree's windows at `at`, shifted by (dx, dy), and
                // whether that spot is acceptable at the given strictness.
                bool Acceptable(int dx, int dy, bool strict)
                {
                    for (var k = 0; k < subtree.Count; k++)
                    {
                        var i = subtree[k];
                        var w = (int)Window(i);
                        var x = start[k].X + sx + dx;
                        var y = start[k].Y + sy + dy;

                        if (x < work.X || y < work.Y || x + w > work.Right || y + w > work.Bottom) return false;

                        var cx = x + w / 2.0;
                        var cy = y + w / 2.0;
                        var r = Radius(i);

                        for (var other = 0; other < count; other++)
                        {
                            if (inside.Contains(other)) continue;

                            var ox = positions[other].X + Window(other) / 2.0;
                            var oy = positions[other].Y + Window(other) / 2.0;
                            var ddx = ox - cx;
                            var ddy = oy - cy;

                            if (Math.Sqrt(ddx * ddx + ddy * ddy) < r + Radius(other) + 2) return false;
                        }

                        if (!strict) continue;

                        bool Hits(PixelRect rect) =>
                            cx + r > rect.X && cx - r < rect.Right && cy + r > rect.Y && cy - r < rect.Bottom;

                        if (obstacles is not null && obstacles.Any(Hits)) return false;

                        for (var g = 0; g < boxes.Count; g++)
                            if (g != groupOfOrb[h] && Hits(boxes[g])) return false;
                    }

                    return true;
                }

                var moveX = 0;
                var moveY = 0;
                var found = false;

                var step = Math.Max(1.0, CircleDip * layout.Scale * sizes[h] / 6);

                foreach (var strict in new[] { true, false })
                {
                    if (found) break;

                    for (var ring = 0; ring <= 80 && !found; ring++)
                    for (var k = 0; k < (ring == 0 ? 1 : 24) && !found; k++)
                    {
                        var angle = k * Math.PI / 12;
                        var dx = (int)Math.Round(Math.Cos(angle) * step * ring);
                        var dy = (int)Math.Round(Math.Sin(angle) * step * ring);

                        if (!Acceptable(dx, dy, strict)) continue;

                        moveX = dx;
                        moveY = dy;
                        found = true;
                    }
                }

                // Nowhere is acceptable: the clamped target, overlap and all.
                for (var k = 0; k < subtree.Count; k++)
                    positions[subtree[k]] = new PixelPoint(start[k].X + sx + moveX, start[k].Y + sy + moveY);
            }
        }

        // One group laid out on its own, about `middle`: its orbs only, with
        // the lead links that stay inside it, against the whole work area, with
        // Compute's squeeze-then-grid retry. Returns the orbs' window top-lefts
        // (in `orbs` order), the group's slot box, and whether a grid replaced
        // the asked shape. `localOf` is scratch, one entry per orb.
        private static (PixelPoint[] Placed, PixelRect Box, bool Gridded) LayGroup(
            int[] orbs, int[] leadOf, int count, double[] sizes, string shape, Layout layout,
            PixelPoint middle, int[] localOf)
        {
            Array.Fill(localOf, -1);
            for (var li = 0; li < orbs.Length; li++) localOf[orbs[li]] = li;

            var subLead = new int[orbs.Length];
            var subSize = new double[orbs.Length];
            for (var li = 0; li < orbs.Length; li++)
            {
                var i = orbs[li];
                var lead = i < leadOf.Length ? leadOf[i] : -1;
                subLead[li] = lead >= 0 && lead < count ? localOf[lead] : -1;
                subSize[li] = sizes[i];
            }

            var placed = ComputeCore(
                orbs.Length, subLead, new int[orbs.Length], new[] { shape },
                layout with { Shape = shape, Center = middle }, subSize, out var gridded, out _);

            return (placed, SlotBox(placed, subSize, layout.Scale, WindowFor(layout.Scale, subSize.Max())), gridded);
        }

        // The curve a group's anchor orbs sit on: the centre and scale that
        // map the shape's unit curve onto their centres, by least squares.
        // Anchors only, in index order — that is the order the shape's points
        // were dealt to them, and a fanned team member is not on the curve.
        // With the unit points u and the placed centres q, S = Σ(Δu·Δq)/Σ(Δu·Δu)
        // about the means, and the centre is mean(q) − S·mean(u). Fit rather
        // than carried out of Fit and Slide, which would mean threading three
        // translations and a scale through two routes; it is exact for what
        // they produced up to the pixel rounding.
        private static (Point Centre, double Scale) FitCurve(
            string shape, int[] orbs, PixelPoint[] positions, double[] sizes, double scale,
            int[] leadOf, int count)
        {
            var anchors = orbs.Where(i => !Resolves(i, leadOf, count)).ToArray();

            // Lines, columns and grids have no curve to offset: their outline is
            // a stadium or a rounded rectangle round the discs. Scale 0 says so,
            // and the centre is the middle of the anchors.
            var unit = shape is "line" or "vline" or "grid"
                ? new (double X, double Y)[anchors.Length]
                : Unit(shape, anchors.Length);

            var qx = anchors.Select(i => positions[i].X + WindowFor(scale, sizes[i]) / 2.0).ToArray();
            var qy = anchors.Select(i => positions[i].Y + WindowFor(scale, sizes[i]) / 2.0).ToArray();

            double mqx = qx.Average(), mqy = qy.Average();
            double mux = unit.Average(u => u.X), muy = unit.Average(u => u.Y);

            double num = 0, den = 0;
            for (var i = 0; i < anchors.Length; i++)
            {
                var dux = unit[i].X - mux;
                var duy = unit[i].Y - muy;
                num += dux * (qx[i] - mqx) + duy * (qy[i] - mqy);
                den += dux * dux + duy * duy;
            }

            // One orb, or points that all coincide: no curve to scale.
            var s = den > 1e-9 ? num / den : 0;

            return (new Point(mqx - s * mux, mqy - s * muy), s);
        }

        // Placements read off the fallback's whole-set layout. Landed is what
        // was asked for, per the note on ComputeGroups: the fallback ignores
        // anchors, so what reproduces it is asking the same thing again.
        //
        private static GroupPlacement[] Describe(
            PixelPoint[] positions, int[] leadOf, int[] slotOf, string[] shapes, double[] sizes, double scale,
            PixelPoint[] askedOf, bool[] griddedSlots)
        {
            var window = WindowFor(scale, sizes.Max());
            var result = new List<GroupPlacement>();

            for (var g = 0; g < shapes.Length; g++)
            {
                var orbs = Enumerable.Range(0, positions.Length).Where(i => slotOf[i] == g).ToArray();
                if (orbs.Length == 0) continue;

                var box = SlotBox(
                    orbs.Select(i => positions[i]).ToArray(),
                    orbs.Select(i => sizes[i]).ToArray(),
                    scale, window);

                // Which slots the fallback redrew as a grid is known exactly (the
                // second pass of Compute's squeeze retry records it), so the
                // shape reported is the shape drawn.
                var drawn = griddedSlots[g] ? "grid" : shapes[g];
                var (curveAt, curveScale) = FitCurve(drawn, orbs, positions, sizes, scale, leadOf, positions.Length);

                result.Add(new GroupPlacement(g, askedOf[g], box, drawn, orbs, curveAt, curveScale));
            }

            return result.ToArray();
        }

        // The anchor that would have drawn `placed` where it landed (CB-211):
        // layout.Center itself whenever the shape went where it was asked, and
        // otherwise that centre moved along whichever axis Slide had to push the
        // shape back onto the screen. `placed` is what Compute returned for
        // these same arguments.
        //
        // A caller that saves an anchor has to save this one, not the one it
        // asked for. Slide is what keeps an anchor off past an edge from drawing
        // orbs off the screen, and it does that silently: an anchor 565px past
        // the last one the screen honours draws exactly the shape that last one
        // does. Saved as it was asked for, the overshoot sits in the settings
        // file where nothing can see it, and the next drag back toward the
        // screen has to spend it before the shape moves a pixel — which is how
        // a -300px drag on a real 2560px screen came back as no move at all.
        //
        // Per axis, and only along an axis where some slot touches the edge of
        // the work area. Slide leaves a shape flush against the edge it pushed
        // it to, so a shape clear of both edges on an axis was never pushed
        // along it, and its asked-for coordinate is already the landed one —
        // returned untouched, not recomputed, so an anchor the screen honours
        // is never nudged by so much as a rounding. That is also why the
        // real-world case keeps its vertical drag: pushed back in x, clear in y.
        //
        // Along an edge, measured rather than predicted. Working out Slide's
        // offset directly means modelling every shape, fan and band against
        // the edges, which is the arithmetic Compute already does; drawing the
        // same orbs once more around the middle of the screen, where nothing is
        // pushed, and comparing the two needs none of it. The middle is moved
        // by a pixel where needed so its distance from the asked-for anchor is
        // even, which is the shift the sweep proves exact — its check 5 says
        // why an odd one can round a pixel out. Averaged over the orbs rather
        // than read off one, because the per-orb clamp that is Slide's last
        // resort can move one member of a fan further than the rest; the
        // average still lands the saved anchor at the edge of the range the
        // screen honours, which is the property that matters.
        internal static PixelPoint LandedCenter(
            PixelPoint[] placed, int count, int[] leadOf, int[] groupOf, IReadOnlyList<string> shapes,
            Layout layout, double[]? sizeOf = null)
        {
            var work = layout.Work;
            var asked = layout.Center ?? new PixelPoint(work.X + work.Width / 2, work.Y + work.Height / 2);

            if (count <= 0 || placed.Length < count) return asked;

            // Slots, not orbs, for the same reason the sweep measures its margin
            // on slots: the arrangement is laid out and pushed in slots, and a
            // small orb sits inset in its slot a few pixels clear of an edge the
            // slot is jammed against.
            var sizes = SizesFor(count, sizeOf);
            var window = WindowFor(layout.Scale, sizes.Max());

            bool touchesX = false, touchesY = false;
            for (var i = 0; i < count; i++)
            {
                var inset = (window - WindowFor(layout.Scale, sizes[i])) / 2;
                var x = placed[i].X - inset;
                var y = placed[i].Y - inset;

                touchesX |= x <= work.X || x + window >= work.Right;
                touchesY |= y <= work.Y || y + window >= work.Bottom;
            }

            if (!touchesX && !touchesY) return asked;

            var middle = new PixelPoint(
                work.X + work.Width / 2 + ((asked.X - work.X - work.Width / 2) & 1),
                work.Y + work.Height / 2 + ((asked.Y - work.Y - work.Height / 2) & 1));

            var centred = Compute(count, leadOf, groupOf, shapes, layout with { Center = middle }, sizeOf);

            long dx = 0, dy = 0;
            for (var i = 0; i < count; i++)
            {
                dx += placed[i].X - centred[i].X;
                dy += placed[i].Y - centred[i].Y;
            }

            return new PixelPoint(
                touchesX ? middle.X + (int)Math.Round(dx / (double)count) : asked.X,
                touchesY ? middle.Y + (int)Math.Round(dy / (double)count) : asked.Y);
        }

        // Everything Compute does between knowing the slot size and handing
        // back slot top-lefts: shapes in bands, fans off leads, separation.
        // gridWhenSqueezed redraws as a grid any group the screen squeezed;
        // `squeezed` says whether any group was.
        private static PixelPoint[] Arrange(
            int count, int[] leadOf, int[] groupOf, IReadOnlyList<string> shapes, Layout layout,
            List<int> anchors, Dictionary<int, List<int>> members, int window, double circle,
            bool gridWhenSqueezed, out bool squeezed, out bool[] regridded)
        {
            squeezed = false;
            regridded = new bool[Math.Max(1, shapes.Count)];

            var minGap = MinGap(circle, layout.Spacing);

            var slots = Math.Max(1, shapes.Count);
            var byGroup = new List<int>[slots];
            for (var g = 0; g < slots; g++) byGroup[g] = new List<int>();

            foreach (var anchor in anchors) byGroup[SlotOf(anchor, groupOf, slots)].Add(anchor);

            var counts = new int[slots];
            for (var g = 0; g < slots; g++) counts[g] = byGroup[g].Count;

            var bands = Bands(layout.Work, counts, window);
            var single = counts.Count(c => c > 0) <= 1;

            var result = new PixelPoint[count];
            var placed = new bool[count];

            // Kept per group, because each is what a fan-out needs to know about
            // the shape its lead is standing in: which way is outward from the
            // middle of it, and how much room a member has before it lands on
            // the next orb along.
            var centreOf = new (double X, double Y)[slots];
            var nearestOf = new double[slots];
            var groupAt = new int[count];

            var drawn = new PixelPoint[slots][];

            for (var g = 0; g < slots; g++)
            {
                if (counts[g] == 0) continue;

                var band = bands[g];
                var at = layout with
                {
                    Work = band,
                    Shape = ShapeAt(shapes, g, layout.Shape),
                    Center = CentreFor(band, layout, single)
                };

                drawn[g] = Fit(ShapeFor(counts[g], window, at), band, window, minGap, out var tight);

                squeezed |= tight;

                if (tight && gridWhenSqueezed && at.Shape != "grid")
                {
                    drawn[g] = Fit(ShapeFor(counts[g], window, at with { Shape = "grid" }), band, window, minGap, out _);
                    regridded[g] = true;
                }
            }

            // With more than one shape in play, each has now been sized inside
            // its own band and is sitting in the middle of it — which spreads
            // three small shapes across the whole screen and leaves the saved
            // anchor doing nothing. Gather them instead.
            if (!single) Compact(drawn, layout, window, minGap);

            for (var g = 0; g < slots; g++)
            {
                if (drawn[g] is not { } shape) continue;

                for (var i = 0; i < counts[g]; i++)
                {
                    var orb = byGroup[g][i];
                    result[orb] = shape[i];
                    placed[orb] = true;
                    groupAt[orb] = g;
                }

                centreOf[g] = Centre(shape, window);
                nearestOf[g] = Nearest(shape);
            }

            // Breadth-first from the anchors, so a lead that is itself somebody's
            // member is positioned before its own members are hung off it.
            // Walking only the anchors left those grandchildren wherever they
            // happened to be, which read as orbs ignoring the arrangement.
            var queue = new Queue<int>(anchors);

            while (queue.Count > 0)
            {
                var lead = queue.Dequeue();
                if (!members.TryGetValue(lead, out var team)) continue;

                var g = groupAt[lead];

                // Bounded by the whole work area rather than by the lead's own
                // band. A fan needs its radius — that is the distance the arrow
                // is drawn at — and a band three orbs wide cannot hold one, so
                // bounding it there would turn every fan in a narrow band
                // instead of only the ones near a screen edge. The separation
                // pass afterwards is what keeps a fan that reached into the
                // next band from landing on anything in it.
                var positions = FanOut(
                    result[lead], centreOf[g], team.Count, circle, nearestOf[g], layout.Work, window);

                for (var i = 0; i < team.Count; i++)
                {
                    result[team[i]] = positions[i];
                    placed[team[i]] = true;
                    groupAt[team[i]] = g;
                    queue.Enqueue(team[i]);
                }
            }

            // Anything a cycle left unreachable still needs somewhere to be. The
            // first anchor, which is always placed: Classify guarantees a
            // non-empty set has an anchor, and every anchor was given a position
            // above. So this is a point on a real shape rather than an invented
            // one — and there is no "if we have somewhere" arm here that no
            // input could ever take.
            for (var i = 0; i < count; i++)
            {
                if (!placed[i]) result[i] = result[anchors[0]];
            }

            return Separate(result, leadOf, circle, layout.Work, window, layout.Spacing);
        }

        // The tightest clearance between any two circles, in pixels — negative
        // when they overlap. Slot top-lefts all share one window, so their
        // differences are centre differences. Radii as Separate counts them: a
        // member's is MemberScale of a lead's.
        internal static double WorstGap(PixelPoint[] pts, int[] leadOf, double circle)
        {
            double RadiusOf(int i) =>
                (i < leadOf.Length && leadOf[i] >= 0 && leadOf[i] < pts.Length ? circle * MemberScale : circle) / 2;

            var worst = double.MaxValue;

            for (var i = 0; i < pts.Length; i++)
            for (var j = i + 1; j < pts.Length; j++)
            {
                var dx = pts[i].X - pts[j].X;
                var dy = pts[i].Y - pts[j].Y;
                worst = Math.Min(worst, Math.Sqrt(dx * dx + dy * dy) - RadiusOf(i) - RadiusOf(j));
            }

            return worst;
        }

        // An orb's window in physical pixels at a given user size. Rounded the
        // same way the single-size code always rounded it, so that at 1.0 the
        // arrangement is bit-for-bit what it was before sizes existed.
        internal static int WindowFor(double scale, double size) =>
            (int)Math.Round(WindowDip * scale * size);

        // One size per orb, with anything the caller did not supply — or
        // supplied as nonsense — read as the size orbs have always been. A
        // zero, negative or non-finite size would make a window of no pixels
        // or of NaN pixels, and a NaN in the largest-orb maximum would put
        // every orb at NaN along with it; clamping to OrbSizing's real range is
        // the settings layer's job, and this only refuses what cannot be drawn.
        internal static double[] SizesFor(int count, double[]? sizeOf)
        {
            var sizes = new double[count];

            for (var i = 0; i < count; i++)
            {
                var size = sizeOf is not null && i < sizeOf.Length ? sizeOf[i] : 1.0;
                sizes[i] = size > 0 && double.IsFinite(size) ? size : 1.0;
            }

            return sizes;
        }

        // Which shape an orb joins, defended against a caller that disagrees
        // with itself. An index nothing has a shape for is group 0 rather than a
        // throw: the arrangement is what puts orbs on the screen, and a screen
        // with every orb in one shape is a far better answer to a bad index than
        // a screen with none.
        private static int SlotOf(int orb, int[] groupOf, int slots)
        {
            var g = orb < groupOf.Length ? groupOf[orb] : 0;
            return g >= 0 && g < slots ? g : 0;
        }

        private static string ShapeAt(IReadOnlyList<string> shapes, int g, string fallback)
        {
            var shape = g < shapes.Count ? shapes[g] : null;
            return string.IsNullOrWhiteSpace(shape) ? fallback : shape;
        }

        // Where a group's shape is drawn while it is being sized.
        //
        // With one group in play this is the saved anchor, untouched — the band
        // is the whole work area, so the arrangement is bit-for-bit what it was
        // before groups existed. That is the point of the `single` argument
        // rather than arithmetic that happens to come out the same: the 20736
        // cases in tests/ArrangementTests all take this branch, and they have to
        // keep meaning what they meant.
        //
        // With more than one, the middle of the band and *not* the anchor. The
        // anchor is applied once at the end, to all the shapes together — see
        // Compact. Applying it here as well was the first draft, and it was
        // wrong in a way worth recording: an anchor well off to one side pushes
        // a far band's shape clear outside that band, Fit's Slide then puts it
        // back against the band's edge, and from there the anchor can move as
        // far as it likes without that shape moving at all. Which is precisely
        // the bug the saved anchor exists to fix — a drag thrown away — except
        // now it applied to one shape and not the others. The sweep caught it
        // 842 times.
        private static PixelPoint? CentreFor(PixelRect band, Layout layout, bool single) =>
            single
                ? layout.Center
                : new PixelPoint(
                    (int)Math.Round(band.X + band.Width / 2.0),
                    (int)Math.Round(band.Y + band.Height / 2.0));

        // Pull the shapes in from the middles of their bands until they sit
        // beside each other as one arrangement, then put that arrangement where
        // the anchor says.
        //
        // Two things this fixes, and they are the same thing seen from either
        // end. Left in their bands, three shapes of three orbs each are drawn a
        // thousand pixels apart on a wide screen — three lonely clusters rather
        // than one arrangement with three parts. And the saved anchor has nothing
        // to say, because each shape's position was decided by its band.
        //
        // Bands are still what does the work: they are where each shape's *size*
        // came from, and they are why the boxes laid out below cannot overlap —
        // a shape fitted inside a band is inside a rectangle that no other
        // shape's rectangle touches, so butting those rectangles together in
        // band order keeps them disjoint however oddly shaped their contents.
        //
        // Anchor-independent by construction, which the sweep checks: nothing
        // above this point consults layout.Center when there is more than one
        // group, and the offsets below are all relative. So moving the anchor
        // moves every orb by exactly the same delta, which is what a
        // whole-shape drag has to survive.
        private static void Compact(PixelPoint[][] drawn, Layout layout, int window, double minGap)
        {
            var used = new List<int>();
            for (var g = 0; g < drawn.Length; g++)
                if (drawn[g] is { Length: > 0 }) used.Add(g);

            if (used.Count <= 1) return;

            var left = new int[drawn.Length];
            var right = new int[drawn.Length];
            var top = new int[drawn.Length];
            var bottom = new int[drawn.Length];

            foreach (var g in used)
            {
                left[g] = drawn[g].Min(p => p.X);
                right[g] = drawn[g].Max(p => p.X) + window;
                top[g] = drawn[g].Min(p => p.Y);
                bottom[g] = drawn[g].Max(p => p.Y) + window;
            }

            // A whole window of air on top of the gap the orbs themselves are
            // asking for, so the join between two shapes reads as a gap between
            // arrangements rather than as one wide arrangement. Squeezed down,
            // never past nothing, if the shapes together are already as wide as
            // the screen — a gap is worth less than the shapes it separates.
            //
            // Rounded to a whole pixel here, and so is everything below it. That
            // is not tidiness, and it cost this change a bug worth naming: with
            // the gap left fractional, one group's translation came out at
            // exactly x.5, and `Math.Round` sends an exact .5 to the even side —
            // so a *one-ulp* difference in how that .5 was reached decided
            // whether the shape landed a pixel left or right. Which made the
            // whole arrangement move by 37 pixels when the anchor moved by 38,
            // for one group out of three, on 11 of the sweep's cases.
            //
            // Whole-pixel translations make that impossible rather than unlikely:
            // every shape has already been rounded to whole pixels by Fit, so
            // moving it by an integer preserves it exactly, and an integer shift
            // plus an integer anchor delta is an integer. Nothing is left for
            // floating point to decide.
            var boxes = used.Sum(g => right[g] - left[g]);
            var gap = (int)Math.Round(Math.Min(
                window + minGap,
                Math.Max(0, (layout.Work.Width - boxes) / (double)(used.Count - 1))));

            // Vertically centred on each other rather than each in its own band,
            // which is the same thing today — every band is the full height of
            // the work area — and stays right if bands are ever cut differently.
            var midY = (int)Math.Round(used.Average(g => (top[g] + bottom[g]) / 2.0));

            var offsetX = new int[drawn.Length];
            var offsetY = new int[drawn.Length];
            var x = 0;

            foreach (var g in used)
            {
                offsetX[g] = x - left[g];
                offsetY[g] = midY - (int)Math.Round((top[g] + bottom[g]) / 2.0);
                x += right[g] - left[g] + gap;
            }

            var width = x - gap;   // no trailing gap after the last shape

            var centreX = layout.Center?.X ?? layout.Work.X + layout.Work.Width / 2;
            var centreY = layout.Center?.Y ?? layout.Work.Y + layout.Work.Height / 2;

            var shiftX = centreX - (int)Math.Round(width / 2.0);
            var shiftY = centreY - midY;

            foreach (var g in used)
            {
                var dx = offsetX[g] + shiftX;
                var dy = offsetY[g] + shiftY;

                drawn[g] = drawn[g]
                    .Select(p => new PixelPoint(p.X + dx, p.Y + dy))
                    .ToArray();
            }

            // Back onto the screen as one piece, for the same reason Slide does
            // it for a single shape: sliding the whole arrangement keeps the gaps
            // inside it, where clamping each shape on its own would push them
            // together against the edge and undo the separation just arranged.
            var all = used.SelectMany(g => drawn[g]).ToArray();
            var slid = Slide(all, layout.Work, window);

            var at = 0;
            foreach (var g in used)
            {
                var next = new PixelPoint[drawn[g].Length];
                Array.Copy(slid, at, next, 0, next.Length);
                at += next.Length;
                drawn[g] = next;
            }
        }

        // The strip of screen each group's shape is drawn in: side by side, left
        // to right in group order, together covering the whole work area.
        //
        // Bands rather than three anchors the user places, and rather than three
        // shapes drawn on one centre and pulled apart afterwards. Both
        // alternatives were considered and both have the same failure: nothing
        // stops one shape being drawn on top of another. Separate() at the end of
        // Compute nudges *overlapping pairs* apart, which is the right tool for
        // two orbs and the wrong one for two whole shapes — from a standing pile
        // it produces a smear rather than three readable patterns. Disjoint
        // bands mean the shapes cannot start on top of each other in the first
        // place, and the fit inside each band keeps them there.
        //
        // Widths are proportional to how many orbs each group is holding,
        // because a band's height is the full work area either way, so its width
        // *is* its share of the room. Eight chats beside one cron get eight
        // times the width, which is what makes the chats' shape the size it
        // would have been on its own screen rather than a third of it.
        //
        // A floor under that, or a group of one would be handed a strip its
        // single orb does not fit inside — the fit pass would then shrink the
        // orb's shape to nothing and Separate would push it into the neighbour,
        // which looks exactly like the bug bands were added to prevent. The
        // floor is capped at an equal share so that the floors can never
        // between them ask for more width than there is.
        //
        // Vertical strips and not horizontal ones: every screen this runs on is
        // wider than it is tall, so cutting the long axis leaves each group a
        // band closer to square — and a shape fits a square better than a
        // letterbox, since Fit scales uniformly and the short side decides.
        //
        // Groups holding nothing get no band. Their entry in the returned array
        // is the whole work area, which nothing reads: Compute skips an empty
        // group before it asks for the band.
        internal static PixelRect[] Bands(PixelRect work, int[] counts, int window)
        {
            var bands = new PixelRect[counts.Length];
            for (var i = 0; i < counts.Length; i++) bands[i] = work;

            var occupied = counts.Count(c => c > 0);
            if (occupied <= 1) return bands;

            var total = counts.Where(c => c > 0).Sum();
            var floor = Math.Min(window * 2.0, work.Width / (double)occupied);

            var widths = new double[counts.Length];
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;
                widths[i] = Math.Max(floor, work.Width * counts[i] / (double)total);
            }

            // Raising the small groups to the floor asks for more width than the
            // screen has. Take the excess back off the groups that are above the
            // floor, in proportion to how far above it they are — so the group
            // with the most orbs gives up the most, and none is pushed back
            // under the floor it was just raised to.
            var slack = widths.Sum() - work.Width;

            if (slack > 0)
            {
                var above = widths.Sum(w => Math.Max(0, w - floor));

                // Provably redundant, and kept: the floors together can never
                // ask for more than the width, because the floor is capped at an
                // equal share — so an overshoot can only come from a group that
                // is *above* its floor, which means `above` is at least as big
                // as the overshoot whenever there is one. The false arm is
                // therefore unreachable and is named as such in the PR that
                // added it. Kept because what it prevents is a division by zero
                // that would put every orb at NaN, which is worse than a branch
                // nothing takes.
                if (above > 0.001)
                {
                    var take = Math.Min(slack, above);
                    for (var i = 0; i < widths.Length; i++)
                    {
                        if (widths[i] <= 0) continue;
                        widths[i] -= Math.Max(0, widths[i] - floor) / above * take;
                    }
                }
            }

            // Laid out from a running total rather than by rounding each width on
            // its own, so the rounding errors cannot accumulate into a gap
            // between two bands or a last band that overshoots the screen.
            var x = (double)work.X;

            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;

                var left = (int)Math.Round(x);
                x += widths[i];
                var right = (int)Math.Round(x);

                bands[i] = new PixelRect(left, work.Y, Math.Max(1, right - left), work.Height);
            }

            return bands;
        }

        // Who is an anchor and who hangs off whom. A lead that cannot be
        // resolved — pointing at itself, at a missing orb, or round a cycle —
        // becomes an anchor, because a shape with nothing to draw is worse than
        // a team drawn as separate orbs.
        // internal for OrbGroups.RootOf's cross-check (CB-268): the group
        // resolver and the arrangement must agree on who a team's root is, and
        // the test that says so runs this rather than a paraphrase of it.
        internal static void Classify(
            int count, int[] leadOf, List<int> anchors, Dictionary<int, List<int>> members)
        {
            for (var i = 0; i < count; i++)
            {
                var lead = i < leadOf.Length ? leadOf[i] : -1;

                if (!Resolves(i, leadOf, count))
                {
                    anchors.Add(i);
                    continue;
                }

                if (!members.TryGetValue(lead, out var team)) members[lead] = team = new List<int>();
                team.Add(i);
            }

            // Every orb in a team means no shape at all. One of them leads.
            //
            // This has never been observed to run, and reasoning about Resolves
            // suggests it cannot: an orb is an anchor exactly when Resolves says
            // no, and Resolves only says yes after hopping to a lead that is out
            // of range — which makes the orb it landed on an anchor itself. So
            // any non-empty set has at least one. The 20736-case sweep in
            // tests/ArrangementTests has never produced anchors.Count == 0 either.
            //
            // Left in place rather than deleted: it is three lines of insurance
            // against a lead table this reasoning does not anticipate, and the
            // failure it prevents — every orb hidden, nothing drawn at all — is
            // far worse than three uncovered lines. Named here so the next person
            // reading a coverage report knows it is deliberate.
            if (anchors.Count == 0 && count > 0) MakeTheFirstOrbAnAnchor(anchors, members);
        }

        // Excluded from coverage: unreachable, for the reason stated above the
        // call — Resolves always lands on an orb that is its own anchor, so a
        // non-empty set always has one, and the 20736-case sweep in
        // tests/ArrangementTests has never produced anchors.Count == 0 either.
        //
        // Kept rather than deleted: it is insurance against a lead table that
        // reasoning does not anticipate, and the failure it prevents — every orb
        // hidden, nothing drawn at all — is far worse than three lines nothing
        // executes.
        [ExcludeFromCodeCoverage]
        private static void MakeTheFirstOrbAnAnchor(
            List<int> anchors, Dictionary<int, List<int>> members)
        {
            anchors.Add(0);
            foreach (var team in members.Values) team.Remove(0);
            members.Remove(0);
        }

        // internal so the shapes a full sweep never produces can be asked for
        // directly — see OrbArrangementResolvesTests. A lead chain that runs into
        // a cycle it is not part of is the case in point: the walk never returns
        // to where it began and never leaves the range, so only the hop budget
        // ends it.
        internal static bool Resolves(int start, int[] leadOf, int count)
        {
            var at = start;

            for (var hops = 0; hops <= count; hops++)
            {
                var lead = at < leadOf.Length ? leadOf[at] : -1;

                if (lead < 0 || lead >= count) return hops > 0;
                if (lead == at) return false;

                at = lead;
                if (at == start) return false;   // cycled back to where we began
            }

            return false;
        }

        // The gap the pattern aims for between neighbouring orbs. Measured on
        // the circle that is drawn rather than the window it sits in — the
        // window is more than half as wide again, and using it inflated every
        // arrangement by that margin.
        internal static double MinGap(double circle, double spacing) => circle * (0.35 + spacing);

        // How far a team member sits from its lead, at minimum: close enough to
        // read as a group, far enough that TeamLinks will still draw the arrow.
        // Asking TeamLinks rather than choosing a number, because a gap that
        // merely stops the two circles overlapping is well short of what an
        // arrow needs, and the two used to disagree.
        internal static double ArrowRadius(double circle, double scale)
        {
            var leadRadiusDip = CircleDip / 2;
            var memberRadiusDip = leadRadiusDip * MemberScale;

            return (TeamLinkGeometry.MinimumCentreDistance(memberRadiusDip, leadRadiusDip) + 3) * scale;
        }

        private static PixelPoint[] FanOut(
            PixelPoint lead, (double X, double Y) centre, int count,
            double circle, double nearest, PixelRect work, int window)
        {
            var memberCircle = circle * MemberScale;

            // A small team fans off one side of its lead; a large one has to go
            // most of the way round it. Capping the arc at about half a turn
            // meant nineteen members could not fit at the distance their arrows
            // need, however hard they were pushed apart — they were squeezed
            // into an arc too short to hold them, and the last pixel of overlap
            // simply could not be resolved.
            var spread = count <= 2
                ? Math.PI / 3
                : Math.Min(Math.PI * 2 * 0.92, Math.PI / 3 + (count - 2) * 0.35);

            var scale = circle / CircleDip;
            var radius = ArrowRadius(circle, scale);

            if (count > 1)
            {
                var step = spread / (count - 1);
                radius = Math.Max(radius, memberCircle * 1.15 / (2 * Math.Sin(step / 2)));
            }

            // Kept inside the lead's own share of the pattern where there is
            // room, but never closer than the arrow needs — a team with no
            // arrows is not readable as a team, which is the whole point of
            // drawing it as one.
            if (nearest > 0) radius = Math.Min(radius, Math.Max(ArrowRadius(circle, scale), nearest * 0.42));

            // Outward from the middle of the pattern, so a team hangs off the
            // outside of the shape rather than into it.
            var half = window / 2.0;
            var dx = lead.X + half - centre.X;
            var dy = lead.Y + half - centre.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            // A lead that *is* the middle of its shape — the only orb its group
            // holds — has no outward. Toward the middle of the screen instead,
            // which is where the room is, and only straight up when the lead is
            // standing on that too.
            //
            // It was always straight up, and for a while that was invisible for
            // the wrong reason: Centre added a DIP half-window to pixel
            // coordinates, so on a Retina screen the "middle" of a lone lead's
            // shape sat 28px up and left of the lead, and outward came out as
            // down and to the right — into the screen, by accident, from the one
            // corner the sweep anchors to. Fixing Centre turned that fan up into
            // the top edge, and nineteen members pressed into a corner that way
            // left 70 cases a pixel short of clearing each other.
            if (dist < 1)
            {
                dx = work.X + work.Width / 2.0 - (lead.X + half);
                dy = work.Y + work.Height / 2.0 - (lead.Y + half);
                dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 1) { dx = 0; dy = -1; dist = 1; }
            }

            var baseAngle = Math.Atan2(dy / dist, dx / dist);

            double Offset(int i) => count == 1
                ? 0
                : spread * (i - (count - 1) / 2.0) / Math.Max(count - 1, 1);

            PixelPoint At(double from, int i)
            {
                var angle = from + Offset(i);

                return new PixelPoint(
                    (int)Math.Round(lead.X + radius * Math.Cos(angle)),
                    (int)Math.Round(lead.Y + radius * Math.Sin(angle)));
            }

            // Which way the fan points, when pointing outward would hang it off
            // the screen. The radius is not negotiable — it is the distance the
            // arrow needs and the distance that keeps members off each other —
            // so direction is the only thing left to choose, and turning the fan
            // is strictly better than the clamp that used to happen here.
            //
            // Clamping each member back inside independently collapses any two
            // whose positions differed only along the clamped axis onto the same
            // edge pixel, so a lead against a corner had its whole team drawn as
            // a single orb. That went unseen because a lead only reaches a
            // corner once the shape has a saved anchor and the anchor is dragged
            // there; before that the shape was always centred and no lead ever
            // got close enough to an edge for the clamp to fire.
            //
            // Sixteenths of a turn, tried nearest-first so the fan stays as
            // close to outward as it can, and the least-bad angle kept if none
            // is clean — on a screen too small to hold the fan at all there is
            // no correct answer, only a least wrong one.
            var best = baseAngle;
            var fewestOutside = int.MaxValue;

            for (var step = 0; step < 16 && fewestOutside > 0; step++)
            {
                // 0, +1, -1, +2, -2 … so a small turn always beats a large one.
                var turn = (step + 1) / 2 * (step % 2 == 0 ? 1 : -1) * (Math.PI * 2 / 16);
                var candidate = baseAngle + turn;
                var outside = 0;

                for (var i = 0; i < count; i++)
                {
                    var p = At(candidate, i);

                    if (p.X < work.X || p.Y < work.Y
                        || p.X > work.Right - window || p.Y > work.Bottom - window)
                    {
                        outside++;
                    }
                }

                if (outside < fewestOutside) { fewestOutside = outside; best = candidate; }
            }

            var placed = new PixelPoint[count];

            for (var i = 0; i < count; i++)
            {
                var p = At(best, i);

                placed[i] = new PixelPoint(
                    Clamp(p.X, work.X, work.Right - window),
                    Clamp(p.Y, work.Y, work.Bottom - window));
            }

            return placed;
        }

        private static int Clamp(int value, int low, int high) =>
            high < low ? low : Math.Clamp(value, low, high);

        // Unit outlines, sampled so that consecutive points are equally far
        // apart *along the outline*. Stepping the parameter instead bunches
        // points wherever the curve moves fastest, and the spacing pass then
        // inflates the whole shape to separate the tightest pair.
        internal static (double X, double Y)[] Unit(string shape, int n)
        {
            if (n <= 0) return Array.Empty<(double, double)>();
            if (n == 1) return new[] { (0.0, 0.0) };

            return shape switch
            {
                "circle" => Sampled(CircleAt, n),
                "diamond" => Sampled(DiamondAt, n),
                "star" => Sampled(StarAt, n),
                "grid" => Grid(n),
                "line" => Line(n),
                "vline" => VLine(n),
                _ => Sampled(HeartAt, n)
            };
        }

        internal static (double X, double Y) CircleAt(double t) =>
            (10 * Math.Cos(t - Math.PI / 2), 10 * Math.Sin(t - Math.PI / 2));

        internal static (double X, double Y) DiamondAt(double t)
        {
            var a = t - Math.PI / 2;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            var r = 10 / Math.Max(Math.Abs(cos) + Math.Abs(sin), 0.01);
            return (r * cos, r * sin);
        }

        internal static (double X, double Y) StarAt(double t)
        {
            const int verts = 10;
            const double outer = 12, inner = outer * 0.4;

            var pos = t / (2 * Math.PI) * verts;
            var idx = (int)pos;
            var frac = pos - idx;

            (double X, double Y) Vertex(int v)
            {
                var angle = 2 * Math.PI * v / verts - Math.PI / 2;
                var r = v % 2 == 0 ? outer : inner;
                return (r * Math.Cos(angle), r * Math.Sin(angle));
            }

            var a = Vertex(idx % verts);
            var b = Vertex((idx + 1) % verts);

            return (a.X * (1 - frac) + b.X * frac, a.Y * (1 - frac) + b.Y * frac);
        }

        internal static (double X, double Y) HeartAt(double t)
        {
            var sin = Math.Sin(t);
            return (
                16 * sin * sin * sin,
                -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t)));
        }

        // Even spacing by arc length: walk the curve finely, total its length,
        // and take points at equal fractions of it.
        private static (double X, double Y)[] Sampled(Func<double, (double X, double Y)> curve, int n)
        {
            const int Steps = 2000;

            var walk = new (double X, double Y)[Steps + 1];
            var along = new double[Steps + 1];

            for (var i = 0; i <= Steps; i++) walk[i] = curve(2 * Math.PI * i / Steps);

            for (var i = 1; i <= Steps; i++)
            {
                var dx = walk[i].X - walk[i - 1].X;
                var dy = walk[i].Y - walk[i - 1].Y;
                along[i] = along[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }

            var total = along[Steps];
            var pts = new (double X, double Y)[n];
            var cursor = 0;

            for (var i = 0; i < n; i++)
            {
                var want = total * i / n;
                while (cursor < Steps && along[cursor + 1] < want) cursor++;
                pts[i] = walk[cursor];
            }

            return pts;
        }

        // A single row, centred on the middle of the work area like every other
        // shape. Fit does the rest: it scales the spacing up to whatever the
        // slider asks for and then down again to whatever the screen allows, so
        // a long row of orbs closes up rather than running off the edge.
        //
        // The plainest arrangement there is, and the one worth having for that
        // reason — a heart is a nice thing to look at and a row is a thing you
        // can read along.
        //
        // A row and not a column, though the column was written first and looked
        // like the obvious pair. The geometry sweep threw it out: a column has
        // only the screen's height to spend, and thirty orbs at 56 DIP need more
        // of it than any of the three test screens has, so orbs overlapped by up
        // to 21px on every one of them. A row spends the width instead, which is
        // the larger budget on every display anyone has, and passes the same
        // sweep at the same counts. Wrapping a column would have fixed it and
        // produced a grid, which already exists.
        private static (double X, double Y)[] Line(int n)
        {
            var pts = new (double X, double Y)[n];
            for (var i = 0; i < n; i++) pts[i] = (i - (n - 1) / 2.0, 0.0);
            return pts;
        }

        // Line, turned 90 degrees — but only up to MaxPerColumn orbs. Past that
        // it wraps into a second column, then a third, rather than staying a
        // single column that outgrows every screen's height: a column only has
        // the screen's height to spend, which runs out sooner than the width
        // Line spends, and Fit's job is to keep the *pattern* on screen, not to
        // shrink individual orbs — so a single unbounded column can be pushed
        // closer together than the orbs actually are, which is overlap, not
        // spacing. Wrapping early avoids ever asking Fit to do that.
        //
        // MaxPerColumn is picked well under what the shortest test screen can
        // hold (small laptop, 800 DIP tall, fits about 22 orbs at the smallest
        // circle size) so the margin survives the extra room a wide spacing
        // setting or a fanned-out team asks for. Below the cap this is a plain
        // single column, same as Line is a plain single row; above it, still
        // narrow — a handful of tall columns, not the square Grid already is.
        private static (double X, double Y)[] VLine(int n)
        {
            const int MaxPerColumn = 15;

            var cols = (int)Math.Ceiling(n / (double)MaxPerColumn);
            var rows = (int)Math.Ceiling(n / (double)cols);

            var pts = new (double X, double Y)[n];

            for (var i = 0; i < n; i++)
            {
                var col = i / rows;
                var row = i % rows;
                pts[i] = (col - (cols - 1) / 2.0, row - (rows - 1) / 2.0);
            }

            return pts;
        }

        private static (double X, double Y)[] Grid(int n)
        {
            var cols = (int)Math.Ceiling(Math.Sqrt(n));
            var rows = (int)Math.Ceiling((double)n / cols);

            var pts = new (double X, double Y)[n];

            for (var i = 0; i < n; i++)
            {
                pts[i] = (i % cols - (cols - 1) / 2.0, i / cols - (rows - 1) / 2.0);
            }

            return pts;
        }

        private static PixelPoint[] ShapeFor(int n, int window, Layout layout)
        {
            var unit = Unit(layout.Shape, n);

            // Any starting size will do — the fit below sets the real one from
            // the gap the spacing asks for. Starting from the screen keeps the
            // arithmetic away from both extremes.
            var s = layout.Work.Height / 40.0;

            var centerX = layout.Center is { } c1 ? c1.X : layout.Work.X + layout.Work.Width / 2.0;
            var centerY = layout.Center is { } c2 ? c2.Y : layout.Work.Y + layout.Work.Height / 2.0;

            var cx = centerX - window / 2.0;
            var cy = centerY - window / 2.0;

            return unit.Select(p => new PixelPoint(
                (int)Math.Round(cx + p.X * s),
                (int)Math.Round(cy + p.Y * s))).ToArray();
        }

        // Scale the pattern until neighbours clear each other, but never past
        // what the screen can hold, then slide it fully inside.
        //
        // `squeezed` says the screen won: the pattern was shrunk below the
        // size the spacing asked for to keep it on the screen.
        private static PixelPoint[] Fit(
            PixelPoint[] pts, PixelRect work, int window, double minGap, out bool squeezed)
        {
            squeezed = false;

            if (pts.Length == 0) return pts;
            if (pts.Length == 1) return new[] { Inside(pts[0], work, window) };

            var centre = Centre(pts, window);

            var closest = double.MaxValue;
            for (var i = 0; i < pts.Length; i++)
            {
                var next = pts[(i + 1) % pts.Length];
                var dx = pts[i].X - next.X;
                var dy = pts[i].Y - next.Y;
                closest = Math.Min(closest, Math.Sqrt(dx * dx + dy * dy));
            }

            var wanted = closest > 0.5 ? minGap / closest : 1.0;

            var halfW = pts.Max(p => Math.Abs(p.X + window / 2.0 - centre.X));
            var halfH = pts.Max(p => Math.Abs(p.Y + window / 2.0 - centre.Y));

            var roomW = Math.Max(1, (work.Width - window) / 2.0);
            var roomH = Math.Max(1, (work.Height - window) / 2.0);

            var fits = Math.Min(
                halfW > 1 ? roomW / halfW : double.MaxValue,
                halfH > 1 ? roomH / halfH : double.MaxValue);

            var factor = Math.Min(wanted, fits);

            // The screen, not the spacing slider, decided how big this is.
            squeezed = fits < wanted;

            var scaled = pts.Select(p => new PixelPoint(
                (int)Math.Round(centre.X + (p.X + window / 2.0 - centre.X) * factor - window / 2.0),
                (int)Math.Round(centre.Y + (p.Y + window / 2.0 - centre.Y) * factor - window / 2.0)))
                .ToArray();

            return Slide(scaled, work, window);
        }

        // How far two lead circles may overlap before Separate pushes them
        // apart: a little at the very bottom of the spacing slider, which is
        // meant to be a tight cluster, and none anywhere else.
        private static double Slack(double circle, double spacing) => spacing <= 0.3 ? circle * 0.4 : 0;

        // Centres, not corners: scaling has to happen about the middle of the
        // drawn shape, and the corner is half a window away from it.
        //
        // Half a window in *pixels*, the window every other line here uses.
        // This used to add WindowDip / 2 — 28, a DIP figure — to pixel
        // coordinates, which is right only at Scale 1.0. On a Retina screen the
        // window is 112px and the pivot sat 28px up and left of the shape's
        // real middle, so Fit scaled the shape away from its anchor by those
        // 28px times one less than Fit's own stretch factor. Measured before the fix: nine
        // teamless orbs in a grid, anchored in the middle of a 3024x1890 screen
        // at Scale 2 and the widest spacing, came out with their centroid
        // (73, 73) px off the anchor, against (0.0, 0.0) after. The sweep never
        // saw it because every check it makes is relative — a constant error
        // moves with the anchor — and CB-198 would have made it worse, since a
        // 200% orb at Scale 2 is a 224px window and an 84px pivot error.
        //
        // No empty-array guard, because no caller can pass one: Fit returns
        // before asking, and Compute only asks of a group holding orbs.
        private static (double X, double Y) Centre(PixelPoint[] pts, int window) =>
            (pts.Average(p => (double)p.X) + window / 2.0, pts.Average(p => (double)p.Y) + window / 2.0);

        private static PixelPoint[] Slide(PixelPoint[] pts, PixelRect work, int window)
        {
            var left = pts.Min(p => p.X);
            var right = pts.Max(p => p.X) + window;
            var top = pts.Min(p => p.Y);
            var bottom = pts.Max(p => p.Y) + window;

            var dx = 0;
            var dy = 0;

            if (left < work.X) dx = work.X - left;
            else if (right > work.Right) dx = work.Right - right;

            if (top < work.Y) dy = work.Y - top;
            else if (bottom > work.Bottom) dy = work.Bottom - bottom;

            if (dx == 0 && dy == 0) return pts;

            return pts.Select(p => Inside(new PixelPoint(p.X + dx, p.Y + dy), work, window)).ToArray();
        }

        private static PixelPoint Inside(PixelPoint p, PixelRect work, int window) =>
            new(Clamp(p.X, work.X, work.Right - window), Clamp(p.Y, work.Y, work.Bottom - window));

        // Nudge apart any two orbs whose circles overlap, and only those.
        //
        // The alternative — scaling the whole pattern until its worst pair
        // clears — is what made the smallest spacing setting fill the screen: a
        // heart's lobes nearly touch at the notch, a star's valleys sit close to
        // its neighbouring points, and one such pair anywhere dictated the size
        // of everything. Separating locally leaves the shape the size it was
        // asked to be and fixes the two orbs that are actually on top of each
        // other.
        //
        // Members are pulled back toward their lead afterwards, because being
        // pushed out of a collision must not take a team member beyond the
        // distance at which its arrow is drawn.
        private static PixelPoint[] Separate(
            PixelPoint[] pts, int[] leadOf, double circle, PixelRect work, int window, double spacing)
        {
            if (pts.Length < 2) return pts;

            var memberCircle = circle * MemberScale;
            var scale = circle / CircleDip;

            // The bottom of the slider is meant to be a tight cluster, so a
            // little overlap there is the setting doing its job rather than a
            // fault to correct.
            var slack = Slack(circle, spacing);

            // How many members each lead is holding, so the pull-back below can
            // ask whether they fit.
            var teamSize = new int[pts.Length];
            for (var i = 0; i < pts.Length; i++)
            {
                var lead = i < leadOf.Length ? leadOf[i] : -1;
                if (lead >= 0 && lead < pts.Length && lead != i) teamSize[lead]++;
            }

            var x = pts.Select(p => (double)p.X).ToArray();
            var y = pts.Select(p => (double)p.Y).ToArray();

            double RadiusOf(int i) =>
                (i < leadOf.Length && leadOf[i] >= 0 && leadOf[i] < pts.Length ? memberCircle : circle) / 2;

            // Fixed iterations, no randomness: the same input has to produce the
            // same arrangement every time or the orbs would wander between one
            // press of the button and the next.
            //
            // Was sixty, which was enough for as long as the shape was always
            // drawn in the middle of the screen. A shape with a saved anchor can
            // be dragged into a corner, and thirty orbs in one team pressed into
            // a corner are the slowest thing this loop has to untangle — every
            // pass has the pull toward the lead working against the push apart,
            // and from a standing pile it took just under a hundred. It costs
            // nothing in the ordinary case: the loop leaves the moment a pass
            // changes nothing, which is within a handful of passes for a shape
            // that was never crowded.
            for (var pass = 0; pass < 150; pass++)
            {
                var moved = false;

                // Pulled in first, then separated, so a pass ends having resolved
                // collisions rather than having just re-made one — the last two
                // pixels of overlap lived exactly there.
                // Back toward its lead, so separation can't strand a member
                // beyond the reach of its own arrow — but no tighter than the
                // team can physically fit. A ring only holds so many: twenty-nine
                // members each needing their own width around it need a radius
                // that follows from their number, and holding them closer than
                // that is asking for an overlap no amount of pushing can fix.
                var arrow = ArrowRadius(circle, scale) * 1.9;

                for (var i = 0; i < pts.Length; i++)
                {
                    var lead = i < leadOf.Length ? leadOf[i] : -1;
                    if (lead < 0 || lead >= pts.Length || lead == i) continue;

                    var dx = x[i] - x[lead];
                    var dy = y[i] - y[lead];
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    var team = teamSize[lead];
                    var ring = team > 1 ? team * (memberCircle + 2) / (2 * Math.PI) * 1.15 : 0;
                    var reach = Math.Max(arrow, ring);

                    if (dist <= reach || dist < 0.001) continue;

                    var pull = (dist - reach) / dist;
                    x[i] -= dx * pull;
                    y[i] -= dy * pull;
                    moved = true;
                }

                for (var i = 0; i < pts.Length; i++)
                for (var j = i + 1; j < pts.Length; j++)
                {
                    var want = RadiusOf(i) + RadiusOf(j) + 2 - slack;

                    var dx = x[j] - x[i];
                    var dy = y[j] - y[i];
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist >= want) continue;

                    // Exactly coincident: pick a direction rather than dividing
                    // by zero. Deterministic, so it stays put across runs.
                    if (dist < 0.001) { dx = 1 + i * 0.01; dy = j * 0.01; dist = Math.Sqrt(dx * dx + dy * dy); }

                    var push = (want - dist) / 2;
                    var ux = dx / dist * push;
                    var uy = dy / dist * push;

                    x[i] -= ux; y[i] -= uy;
                    x[j] += ux; y[j] += uy;
                    moved = true;
                }

                if (!moved) break;
            }

            // Back onto the screen once, as one piece — not every orb on every
            // pass, which is what this used to do and is why overlaps against
            // an edge were never resolved. Clamping inside the loop made the
            // clamp the strongest of the three forces in it: a member pushed
            // off the edge to clear its neighbour was put straight back on top
            // of it, the next pass pushed it off again, and sixty passes later
            // the pair was still overlapping with nothing left to try.
            //
            // Invisible until the shape got a saved anchor. Centred, the shape
            // never came near an edge and the clamp never fired; anchored into
            // a corner, a lead sits in the corner and its whole team is pressed
            // into it.
            //
            // Sliding the cluster keeps every gap the passes just opened, and
            // does it for the same reason Slide exists for the shape itself.
            // The per-orb clamp still follows as a last resort, for a cluster
            // genuinely wider than the screen — there the honest answer is that
            // there is no arrangement, only the least bad one.
            var lowX = x.Min();
            var lowY = y.Min();
            var shiftX = 0.0;
            var shiftY = 0.0;

            if (lowX < work.X) shiftX = work.X - lowX;
            else if (x.Max() + window > work.Right) shiftX = work.Right - (x.Max() + window);

            if (lowY < work.Y) shiftY = work.Y - lowY;
            else if (y.Max() + window > work.Bottom) shiftY = work.Bottom - (y.Max() + window);

            return Enumerable.Range(0, pts.Length)
                .Select(i => new PixelPoint(
                    (int)Math.Round(Math.Clamp(x[i] + shiftX, work.X, Math.Max(work.X, work.Right - window))),
                    (int)Math.Round(Math.Clamp(y[i] + shiftY, work.Y, Math.Max(work.Y, work.Bottom - window)))))
                .ToArray();
        }

        private static double Nearest(PixelPoint[] pts)
        {
            if (pts.Length < 2) return 0;

            var closest = double.MaxValue;

            for (var i = 0; i < pts.Length; i++)
            {
                for (var j = i + 1; j < pts.Length; j++)
                {
                    var dx = pts[i].X - pts[j].X;
                    var dy = pts[i].Y - pts[j].Y;
                    closest = Math.Min(closest, Math.Sqrt(dx * dx + dy * dy));
                }
            }

            return closest;
        }
    }
}
