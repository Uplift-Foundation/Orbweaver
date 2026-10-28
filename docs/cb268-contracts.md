# CB-268 contracts

The shared surface every CB-268 stream builds against. The approved plan's decisions are final, and this file only fixes the names, the types, the units and the edge cases the plan leaves open, so that streams built in parallel compile together and agree on what a value means. If a stream needs to change a signature here, say so to the architect and the other affected owner before you push. Don't change it silently.

Already in code (`OrbGroups.cs`, `OrbArrangement.cs`, first pushed at `ac81f062`): `OrbGroup`, `GroupStyle`, `OrbGroups.Main/Heartbeats/Crons`, `OrbArrangement.GroupLayout`, `OrbArrangement.GroupPlacement`. Everything else below is a signature to implement.

## Units, once

- **Screen units** for anything about where orbs or groups sit on screen: orb window top-lefts, `GroupLayout.Center`, `GroupPlacement.Landed/SlotBounds`, `OrbGroup.Anchor`, the `discs` handed to `GroupOutline`, and `PointToScreen` results. This is the space `OrbArrangement`, `OrbWindow.Position` and `PointToScreen` already use. The rest of this file and the code comments say "physical px" for these, but they are **physical pixels on Windows and points on macOS**: on a Retina Mac, Avalonia's screen coordinates are already points (see `LinkWindow.Apply`'s comment in `TeamLinks.cs`).
- **DIP** only for the snap/drag thresholds and outline padding (`OrbSnap`, `DragPlan`, `GroupBackdrops`). To convert screen units to DIP, divide by the **measured** screen-units-per-DIP, taken the way `LinkWindow.Apply` takes it (`(PointToScreen(new Point(64, 0)).X - PointToScreen(new Point(0, 0)).X) / 64`). That is 1 on macOS and the display scaling on Windows. **Never use `RenderScaling` for this**: it is 2 on a Retina Mac while `PointToScreen` is already in points, so dividing by it would halve every distance and double the threshold. (This bullet said "render scaling" until the CB-268 team-orb revision, and that was wrong.)
- **Opacity** is 0..1 everywhere in code and settings. The Settings slider shows 0–100%.
- **Colours** are `#RRGGBB` strings, the form settings.json already stores. Any alpha in the string is ignored, because the opacity fields are the alpha.

## Agent-team orbs (T1–T7, final)

The rules Warren added for agent teams, as they land on these contracts:

- **T1 — one root, one group.** A team's group is resolved from its **root lead**, and its members never diverge from it. In `ComputeGroups`, every member inherits its root lead's slot, whatever its own `groupOf` entry says. In `SessionManager`, the group of every orb in a team is the root's `GroupIdFor`.
- **T2 — a team moves as one.** A plain drag on a lead **or** on any member, at any depth, moves the whole team (root plus every descendant, nested included), and snaps or unsnaps it as one. `OrbSnap` measures from **all** of the team's discs. Shift-drag or a backdrop drag moves the whole group, teams included.
- **T3 — membership on every key.** Membership is written under every team key, including members that arrive later; S7 writes those the first time it sees them. A member that outlives its lead then keeps the group.
- **T4 — the root survives its lead.** When a lead ends, the root becomes its **nearest surviving ancestor**: walk the `Lead` chain past orbs that are no longer on screen to the first one that is. If none is, the orb is its own root. S7 builds `leadOf` this way, so `ComputeGroups`, `TeamIdsOf` and the arrows all see the same tree. A lead cycle (no root) resolves the way `OrbArrangement.Classify` already resolves one: the cycle's first orb by index becomes the anchor.
- **T5 — the outline encloses fanned members.** This is `GroupOutline`'s guarantee, applied to members' discs as well (S3 covers it).
- **T6 — arrows above, orbs never covered.** Team arrows stay above the backdrop and stay click-through. The backdrop's hit area never covers an orb: on Windows its region is the silhouette **minus every orb's disc**, and on macOS `ShouldCatch` is false inside any orb's disc. This does not rely on z-order alone. See the spike doc.
- **T7 — tests.**
  - The sweep crosses `TeamShapes` with group splits: nested teams, a lead in A whose members' `groupOf` says B, a missing lead, and cycles.
  - Team cases in `OrbSnap` and `DragPlan`.
  - A UI test that drags a member onto another group and checks that the whole team moved.
  - A screenshot of a group containing a team.

## S1 — `OrbGroups` (nadia-okafor)

```csharp
internal static partial class OrbGroups
{
    // Explicit membership wins. Otherwise the CB-14 classification decides:
    // OrbClusters.GroupOf(cluster, hb, cron) 1 → Heartbeats, 2 → Crons, else Main.
    // A membership naming a group that no longer exists reads as no membership.
    string GroupIdFor(string? membership, OrbCluster cluster, ClusterMode hb, ClusterMode cron);

    // Slot order: Main, Heartbeats, Crons, then user groups by Id (ordinal).
    // Predefined groups are always present, even when empty, so slots 0–2 never move.
    IReadOnlyList<OrbGroup> Ordered(IReadOnlyDictionary<string, OrbGroup> groups);

    string NewUserGroupId();   // "g-" + 8 lowercase hex
}
```

Settings keys and accessors are per the plan §A. Membership is keyed by `PositionKey`, `OrdinalIgnoreCase`. When a team is snapped, the membership is written under the lead's key and every member's key. Membership must be carried across a `PositionKey` change.

## S2 — `ComputeGroups` and `SeparateGroups` (arrangement engineer)

```csharp
internal static (PixelPoint[] Positions, GroupPlacement[] Groups) ComputeGroups(
    int count, int[] leadOf, int[] groupOf,
    IReadOnlyList<GroupLayout> groups, Layout layout, double[]? sizeOf = null);
```

- `groupOf[i]` is a slot index into `groups`. Out of range, or missing, reads as slot 0.
- **T1: only a root's slot counts.** A member at any depth goes to the slot of the root it resolves to through `leadOf`, even when its own `groupOf` names another slot. A cycle's root is whichever orb `Classify` makes the anchor. `GroupPlacement.Orbs` therefore lists the whole team under the root's slot.
- `layout.Work`, `Scale` and `Spacing` apply to every group. `layout.Shape` and `layout.Center` are ignored.
- **No group has a `Center`** → use today's Bands+Compact path, so a first draw looks the way it does today. The `Landed` values come back from that layout and are what the caller saves.
- **Otherwise** each group is fit on its own against all of `Work`, with the squeezed → grid retry. Then `SeparateGroups` runs. If boxes still overlap afterwards, fall back to Bands+Compact.
- `Groups` has one entry per non-empty slot, in ascending slot order. `Landed` = the asked-for centre + the group's total integer translation. `DrawnShape` is `"grid"` when the group fell back to a grid.
- **`CurveCentre` and `CurveScale` (added by decision, final; in code at `2d5790e7`): the curve the anchors were actually placed on.** They are trailing optional fields, `Point CurveCentre = default` and `double CurveScale = 0`, so older constructions still compile.
  - The formula: the centre of anchor orb k is `CurveCentre + CurveScale × OrbArrangement.Unit(DrawnShape, anchorCount)[k]`, in screen units, after separation and translation.
    - "Anchor" means a non-member orb (a team root or a solo orb), and k runs over the anchors in index order.
  - It is a **least-squares fit** to where the anchors actually landed, not a value carried through the layout. It is within a few px of the real placement; the worst measured was a star at about 7.5 px. So an outline built from it still has to enclose the real discs (T5) and must not assume the anchors sit exactly on the curve.
  - **"No curve" is `CurveScale == 0`; the fields are never null** (final at `c179b432`, fallback at `9839db6f`). It covers a grid, a line, a column, a group the fallback redrew as a grid (its `DrawnShape` is then `"grid"`), and a one-orb group. `CurveCentre` is still filled: the anchors' mean centre, or a lone orb's centre.
  - **One owner:** `SessionManager.CurveOf(placement)` is the single place a placement becomes `BackdropSpec.Curve`, and it returns null exactly when `CurveScale == 0`. `GroupOutline` tolerates a zero, non-finite or negative scale by falling back to its scaled fit, but that is a safety net, not a second decision: nothing in the app hands it one.
  - For `grid`, `line` and `vline`, `GroupOutline` keeps its rounded rectangle or stadium around the discs, and a one-orb group keeps its circle. The curve drives the outline only for heart, circle, diamond and star.
- **`GroupOutline` (S3) draws the outward offset of that curve,** `CurveCentre + CurveScale × curve(t)` for heart, circle, diamond and star, offset outward by the smallest distance that still encloses every disc of the group plus padding (T5, fanned members included). It does not fit a fresh, freely scaled shape around the discs: that read 2–3× too big on a heart in the screenshots.
- S7 hands each `GroupPlacement` through to `GroupBackdrops` whole, `DrawnShape`, `CurveCentre` and `CurveScale` included.
- Pure and deterministic: the same inputs give identical output.

```csharp
// Pushes later boxes out of earlier ones along the axis of least penetration,
// keeping `gap` px between them, then slides each one back inside `work`.
// At most 50 passes. Index order is priority: box 0 never moves for another box.
// Returns the moved boxes (same order) and whether every pair ended at least `gap` apart.
internal static (PixelRect[] Boxes, bool Separated) SeparateGroups(
    IReadOnlyList<PixelRect> boxes, PixelRect work, int gap);
```

Per-box translation = `Boxes[i].Position - boxes[i].Position`.

## S3 — `GroupOutline` (celeste-ngata)

```csharp
internal static class GroupOutline
{
    // A closed polygon (first point not repeated) of the group's outline.
    // shape: a GroupPlacement.DrawnShape. heart/circle/diamond/star follow
    // OrbArrangement.Unit's curve, sampled at 360 points. line/vline → a stadium,
    // grid → a rounded rectangle. A single disc → a circle, whatever the shape.
    // No discs → empty array.
    // Guarantee: for every disc, SignedDistanceInside((X,Y), result) >= R + padding,
    // up to 0.5 px of polygon-sampling error.
    // Same units in as out, normally physical px.
    Point[] Silhouette(string shape, IReadOnlyList<(double X, double Y, double R)> discs, double padding);

    // Axis-aligned bounds of the polygon. Rect.Empty-equivalent (0,0,0,0) for an empty one.
    Rect Bounds(Point[] polygon);

    // Distance from p to the nearest edge: positive inside, negative outside, 0 on it.
    // Orientation-agnostic (CW or CCW input). Empty polygon → double.NegativeInfinity.
    double SignedDistanceInside(Point p, Point[] polygon);
}
```

**Revised by decision (final):** `Silhouette` also takes the placement's curve, `CurveCentre` and `CurveScale` (see S2), and draws the outward offset of that curve instead of fitting its own. The exact parameter list is celeste-ngata's revision; until it lands, don't code against the signature above.

`Point` and `Rect` are `Avalonia.Point` and `Avalonia.Rect`. Use `OrbArrangement.Unit`, which is already `internal`, for the curves. If you need a per-curve helper (`HeartAt` …), promote it from `private` to `internal` in its place in the file. Don't copy it.

## S4 — `OrbSnap` and `DragPlan` (gesture engineer)

```csharp
internal abstract record SnapOutcome
{
    internal sealed record NoChange : SnapOutcome;
    internal sealed record Join(string GroupId) : SnapOutcome;
    internal sealed record Leave : SnapOutcome;
}

// One group the dragged team could be near. Outline may be empty (outlines off and
// not computed yet), in which case only the discs count.
// Discs exclude every orb of the dragged team (T2).
internal readonly record struct SnapCandidate(
    string GroupId, Point[] Outline, IReadOnlyList<(double X, double Y, double R)> Discs);

internal static class OrbSnap
{
    // All in DIP. `team` is every disc of the dragged team: the root and all its
    // descendants, or just the orb if it has no team (T2).
    // The gap to a group is the minimum, over the team's discs, of: the distance from
    // that disc's edge to the candidate's outline (from outside), or its edge-to-edge
    // distance to the nearest candidate disc.
    //   Join(id): the nearest group other than currentGroupId with gap <= joinDip.
    //             Ties go to the ordinal-smallest id. Join beats Leave.
    //   Leave:    no Join, the current group has at least one disc outside the team,
    //             and every team disc is > leaveDip from every one of them, edge-to-edge.
    //   NoChange: otherwise, including when the team is the whole of its group.
    SnapOutcome Decide(IReadOnlyList<(double X, double Y, double R)> team, string currentGroupId,
        IReadOnlyList<SnapCandidate> candidates, double joinDip = 24, double leaveDip = 72);

    // Convenience overload for an orb with no team; it delegates to the one above.
    // It must be covered like any other line.
    SnapOutcome Decide(Point centre, double radius, string currentGroupId,
        IReadOnlyList<SnapCandidate> candidates, double joinDip = 24, double leaveDip = 72);
}

internal enum DragSource { Orb, Backdrop }

internal static class DragPlan
{
    // The inline 6 from OrbWindow.OnPointerMoved, which compared PointToScreen deltas:
    // 6 points on macOS and 6 physical px on Windows. Converting with the measured
    // units-per-DIP (see Units) keeps it at exactly that on both platforms.
    internal const double ThresholdDip = 6;

    // teamIds: the whole team of the pressed orb (root lead + every descendant, nested
    // included), the same list whichever team orb was pressed (T2). Just the orb if it
    // has no team. groupIds: every orb in its group, teams included.
    // Orb, !shift   → (false, teamIds)   — snap/unsnap is decided on release
    // Orb, shift    → (true,  groupIds)
    // Backdrop, any → (true,  groupIds)
    (bool WholeGroup, IReadOnlyList<string> Ids) Followers(
        DragSource source, bool shift, IReadOnlyList<string> teamIds, IReadOnlyList<string> groupIds);
}
```

Ids are session ids (`SessionStatus.SessionId`), the identity `OrbWindow` already carries. When the drag is released (`OrbDragCommit`, S4):

- **Join**: `OrbweaverSettings.AssignTeamToGroup(rootKey, memberKeys, id)`, where the keys are the `PositionKey` of every team orb. Then `ReapplyArrangement()`.
- **Leave**: `CreateOrbGroup`, then `AssignTeamToGroup` for the whole team, then `SetOrbGroupAnchor(new, drop point)`. The team becomes a new group, anchored where it was dropped.
- A whole-group drag: `ShiftGroupAnchor(sourceGroup, dx, dy)`, then `ReapplyArrangement()`.
- A menu "Leave group" instead sends the orb's team back to `Main`.

### The S4 ↔ S7 seam: `IOrbDragHost` (juno-abernathy proposes, architect implements)

```csharp
// OrbDragHost.cs (S4). SessionManager implements it in S7 and assigns OrbDragHost.Current
// at startup. Until then, Current is null and OrbWindow keeps today's drag behaviour,
// so S4 compiles and tests (against a fake host) without touching SessionManager.
internal interface IOrbDragHost
{
    bool IsArranged { get; }
    IReadOnlyList<string> TeamIdsOf(string sessionId);    // T2/T4: root + all descendants; includes sessionId
    IReadOnlyList<string> GroupIdsOf(string sessionId);   // every visible orb in that orb's group, teams included
    string GroupIdOf(string sessionId);                   // T1: resolved from the root
    OrbWindow? WindowOf(string sessionId);
    IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excludedIds); // discs in screen units; S4 converts to DIP
    void HighlightGroup(string? groupId);                 // → GroupBackdrops.Highlight
    void ShiftGroupAnchor(string groupId, int dx, int dy);
    void ReapplyArrangement();
    void RefreshBackdrops();                              // → GroupBackdrops.Refresh, beside TeamLinks.Refresh
}

internal static class OrbDragHost { internal static IOrbDragHost? Current { get; set; } }
```

## S5 — `GroupStyle.Resolve` and `GroupBackdrops` (backdrop engineer)

```csharp
internal sealed partial record GroupStyle
{
    // Each null field of `over` (or the whole of `over`, if it is null) takes the
    // matching field of `globals`. `globals` comes from settings and is fully populated.
    // A colour string that fails to parse falls back to the globals value; if that also
    // fails, stroke falls back to #FFFFFF and fill to #000000.
    // Opacity is clamped to 0..1 and becomes the alpha byte (Math.Round(o * 255)).
    internal static (Color Stroke, Color Fill) Resolve(GroupStyle? over, GroupStyle globals);
}

// One group with orbs on screen, as SessionManager hands it over on each scan or arrange.
internal readonly record struct BackdropSpec(
    string GroupId, string DrawnShape, IReadOnlyList<OrbWindow> Orbs, GroupStyle? Style);

internal static class GroupBackdrops
{
    void Update(IEnumerable<BackdropSpec> groups);   // replaces the set (TeamLinks.Update's shape)
    void Refresh();                                  // re-reads orb positions; call it wherever TeamLinks.Refresh() is called
    void Highlight(string? groupId);                 // full opacity and 2× stroke on one; null clears
    void ParkAll();                                  // hide and pool every backdrop (arrange off, app hidden)
    void SetOutlinesVisible(bool visible);           // groupOutlinesEnabled; backdrops still hit-test when false

    // Raised on a pointer press inside a silhouette. S4 wires it to a group drag with
    // DragSource.Backdrop. Arguments: group id, the press args, and the backdrop window
    // that received the press.
    event Action<string, PointerPressedEventArgs, Window>? BackdropPressed;
}
```

A disc for `GroupOutline` is the orb's visible circle, not its window: the circle's centre and radius in screen units. If `OrbWindow` doesn't already expose that, S5 adds `internal (double X, double Y, double R) ScreenDisc` to `OrbWindow`. A team member's circle is `MemberScale` of its lead's. Padding is `TeamLinkGeometry`-independent: 10 DIP × the measured units-per-DIP (see Units), as a named constant in `GroupBackdrops`.

**T6:** the hit area is the silhouette **minus every orb disc** in the group. On Windows that is `CombineRgn(…, RGN_DIFF)` with an elliptic region per disc. On macOS `BackdropHitTest.ShouldCatch` answers false inside any disc. Team arrows stay above the backdrop and stay click-through.

**Outline = offset of the placed curve (Jennifer's final decision, CB-268 hugging fix).** `GroupOutline.Silhouette`/`Fit` take an optional `PlacedCurve? placed` (`record struct PlacedCurve(Point Centre, double Scale)`): where unit (0,0) of `OrbArrangement.CircleAt/DiamondAt/StarAt/HeartAt` landed in screen units, and the screen units per curve unit. `ComputeGroups` is to report them as `GroupPlacement.CurveCentre` (Point) and `GroupPlacement.CurveScale` (double), null/absent for line, vline, grid and any group that fell back to a grid; S7 builds `PlacedCurve` from them and puts it in `BackdropSpec.Curve` (a trailing optional parameter, so old callers compile). With a curve the outline is that curve pushed outward by `d` with round joins, taking the outer boundary at the heart's cleft and star's notches (a rounded dip, not the fold). `d = max over discs of (R + padding - SignedDistanceInside(centre, curve))`, closed form, so fanned team members swell it by exactly what they need and un-fanned orbs on the curve give `maxR + padding`. The result has 240 vertices. Without a curve, or with a non-finite or non-positive one, the old scaled-copy fit is used. Cost: about 6 ms (heart) per fit in Release, only paid when the disc set changes shape; a group drag translates the window instead.

Decisions settled during S5 (Celeste):

- `GroupOutline.Silhouette` still **throws** on a non-finite disc. `GroupBackdrops` is the guard: it drops any orb whose measured centre or radius is not finite, so a refresh that reads a not-yet-positioned orb skips it instead of crashing; a group left with no finite orb has no backdrop until the next `Refresh`.
- `internal static Point[] OutlineOf(string groupId)` returns the silhouette last drawn for a group, in screen units, or empty.
- Orb discs are cut out of the hit area: on Windows the region is the polygon minus every orb's disc (`CombineRgn RGN_DIFF`); on macOS `BackdropHitTest.ShouldCatch(cursor, bounds, polygon, orbDiscs, buttonsDown, previous)` is false inside any disc.
- `Refresh()` puts a window that has just appeared behind the orbs (`Restack`) immediately, not only on `Update`. `Restack()` is also public to callers for drag end.
- `Highlight` shows the target (full stroke opacity, twice the width) even when outlines are off.

The window ordering and the hit-test mechanism follow `docs/cb268-backdrop-spike.md` (S0).

## S7 — integration (architect)

What calls what, as built.

**One team tree.** `TeamTree` (pure, `TeamTree.cs`) holds the one thing the single root resolver cannot know: who an ended lead's lead was.
- It is built from the on-screen ids in display order plus `SessionManager._knownLead`, the last `Lead` each session reported, kept after the session ends while something on screen still descends through it.
- `LeadOf` is T4's nearest surviving ancestor, and `LeadIndices` is the `leadOf` array built from it.
- `RootOf`/`TeamOf` (root first, nested members included) come from **`OrbGroups.RootOf`** (S1) over those indices. That is the single resolver, built on `OrbArrangement.Resolves`, so a cycle member is its own root, exactly as the arrangement draws it.
- Nothing walks `SessionStatus.Lead` on its own any more except the menu's standalone `OrbGroupMenu.Resolve`, which the app no longer calls.

**Every scan** (`ScanAndUpdateCore`), before `ReflowPositions`, runs `SyncOrbGroups()`:
1. Refresh `_knownLead` from the statuses and prune it with `TeamTree.Reachable`.
2. `OrbGroupSync.KeyTracker.Observe`, then `OrbweaverSettings.CarryOrbGroupMembership(old, new)` for each key that changed (CB-10).
   - This is needed because `OrbWindow.PositionKey` is set once at creation and never changes.
   - It observes **every session in `_statuses`**, not the orbs on screen, and forgets one only when its session ends. Hiding the orbs never dropped one, but a session known without an orb for a scan did, and its carry was lost.
3. For every team of two or more: `OrbweaverSettings.EnsureTeamMembership(rootKey, memberKeys)` (T3). It is idempotent, fills only member keys with no entry of their own, and does nothing for a root that is in its group by classification. A team *move* that should overwrite is `AssignTeamToGroup`.

None of the three moves an orb, so CB-161 is untouched.

**Group of an orb** is `GroupIdOf(tree, id)`: the root's group, through `OrbGroupMenu.ContextFor(root, …)`. That is explicit membership first (key, then bare-directory fallback), then `OrbGroups.GroupIdFor`'s classification.

**Arrangement:** `ComputeClusteredPositions` → `PlaceGroups` → **`OrbArrangement.ComputeGroups`** (S2).
- Inputs: `leadOf = tree.LeadIndices()`, `groupOf[i]` = slot of the root's group in `OrbweaverSettings.OrbGroupList()` order, `sizeOf`, and one `GroupLayout(Shape, Anchor)` per table entry (empty ones too, so slots are stable).
- Nothing is seeded before the layout. `ArrangementAnchor`, which put `main`'s anchor at the screen's middle first, is removed, so with no anchor anywhere `ComputeGroups` asks each group for its band's middle and takes the same single path as every later arrange.
- After it: `SetOrbGroupAnchor(id, Landed)` for every placed group whose `Landed` differs from the asked anchor (CB-211 per group, first draw included).
- `SessionManagerGroupsTests.TheFirstArrangeSavesEveryGroupsAnchorAndTheNextReproducesIt` pins that a first arrange saves every anchor and the next arrange reproduces it, every orb. It found an S2 defect (a heart's first-draw anchor redrew the group about 160 px away), which `ComputeGroups` becoming one path (`8ae815d4`) fixed.

**Pinning in arranged mode is retired.**
- `PinEveryOrbAtItsTarget` is deleted. It never ran, and a pin would make the next arrange treat the orb as an exception.
- A drag on an arranged orb changes membership or a group anchor and then calls `ReapplyArrangement`.
- Outside an arrangement, `OrbDragSession.End` still pins.

**Anchors:** `ShiftArrangementAnchor(dx, dy, groupId = main)` moves one group's saved anchor, and does nothing for a group with no anchor yet.

**A predefined group keeps its saved anchor after it empties (intended).** When `main`, `heartbeats` or `crons` empties and later refills, it comes back where it was, exactly as the single `ArrangeAnchor` always did. A user group never faces the question, because it is pruned when it empties.

**`IOrbDragHost`** is implemented explicitly on `SessionManager`, and `Start()` assigns `OrbDragHost.Current = this`.
- Every list is over visible orbs:
  - `TeamIdsOf` = `TeamOf`
  - `OrbIdsInGroup` and `GroupIdOf` through `GroupIdOf(tree, id)`
  - `PositionKeyOf` = `OrbGroupMenu.KeyOf` (fresh, not the stale `window.PositionKey`)
  - `DiscOf` = the visible circle in Position units: `Position + CentreDip × OrbWindow.UnitsPerDip`, radius `OrbRadius × UnitsPerDip`. That is the same factor `OrbDragCommit` divides out. Measured on a Retina MacBook: `UnitsPerDip` 1, `DesktopScaling` 1, `RenderScaling` 2.
  - `CandidatesFor` = every group with its discs, ordinal by id, and the outline its backdrop drew (empty for a group with no backdrop)
  - `HighlightGroup` → `GroupBackdrops.Highlight`
  - `ShiftGroupAnchor` → `ShiftArrangementAnchor`
- `MembersOf` and `ArrangedSiblings`, the pre-CB-268 drag helpers, are removed; nothing called them after S4.

**Group menu:** `GroupMenuContextFor(id)` → `OrbGroupMenu.ContextFor(tree.RootOf(id), tree.TeamOf(id), …)`. The menu, the drag and the arrangement therefore agree on the root, T4 included.

**Arrows:** `UpdateTeamLinks` pairs each on-screen orb with `tree.LeadOf(id)`. A member whose lead ended points at the ancestor it now fans off.

**Backdrops (S5), wired:**
- **Update** after every arrange: `ComputeClusteredPositions` turns `ComputeGroups`' placements into `BackdropSpec(GroupId, DrawnShape, Orbs, Style, Curve)`.
  - `Curve` is `SessionManager.CurveOf(placement)`, which is null wherever `ComputeGroups` reports `CurveScale == 0`: grid, line, vline, a squeezed group that fell back to a grid, and a lone orb.
  - `Style` is read fresh from the group table each time.
- **Refresh** beside every `TeamLinks.Refresh()`, through `RefreshOverlays()`; `OrbDragSession.Move` does the same.
- **Restore** clears the set: unarranged orbs have no backdrops.
- **Visibility:** `SetOrbsVisible(false)` → `ParkAll()`, and `true` → `Refresh()`.
- **Styles:** `ReapplyGroupStyles()` → `SetOutlinesVisible(GroupOutlinesEnabled)` + `Update`, so a style change and an arrange take one path.
- **Drag cues:** `HighlightGroup` → `GroupBackdrops.Highlight`. `CandidatesFor`' outline is `GroupBackdrops.OutlineOf`, so the padding lives only in `GroupBackdrops`.
- **Empty-space drag:** `Start()` subscribes `GroupBackdrops.BackdropPressed += OrbWindow.BeginBackdropDrag`.
- **Measurement:** `GroupBackdrops.Measure` defaults to `MeasureFromDisc(orb, SessionManager.DiscOf, OrbWindow.UnitsPerDip)`, the same circle the drag measures. Its guard catches `ArgumentException`, which `PointToScreen` throws for a visual outside a visual tree; the old `InvalidOperationException` catch caught nothing.
