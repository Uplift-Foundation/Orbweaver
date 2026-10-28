# CB-268 contracts

The shared surface every CB-268 stream builds against. The approved plan's decisions are final, and this file only fixes the names, the types, the units and the edge cases the plan leaves open, so that streams built in parallel compile together and agree on what a value means. If a stream needs to change a signature here, say so to the architect and the other affected owner before you push. Don't change it silently.

Already in code (commit `OrbGroups.cs`, `OrbArrangement.cs`): `OrbGroup`, `GroupStyle`, `OrbGroups.Main/Heartbeats/Crons`, `OrbArrangement.GroupLayout`, `OrbArrangement.GroupPlacement`. Everything else below is a signature to implement.

## Units, once

- **Physical pixels** for anything about where orbs or groups sit on screen: orb window top-lefts, `GroupLayout.Center`, `GroupPlacement.Landed/SlotBounds`, `OrbGroup.Anchor`, and the `discs` handed to `GroupOutline`. This is the space `OrbArrangement` and `OrbWindow.Position` already use.
- **DIP** only for the snap/drag thresholds (`OrbSnap`, `DragPlan`). The drag handler divides by the screen's render scaling once, before calling them.
- **Opacity** is 0..1 everywhere in code and settings. The Settings slider shows 0–100%.
- **Colours** are `#RRGGBB` strings, the form settings.json already stores. Any alpha in the string is ignored, because the opacity fields are the alpha.

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

- `groupOf[i]` is a slot index into `groups`. Out of range, or missing, reads as slot 0. Only an anchor's slot counts: a team member goes wherever its lead is.
- `layout.Work`, `Scale` and `Spacing` apply to every group. `layout.Shape` and `layout.Center` are ignored.
- **No group has a `Center`** → use today's Bands+Compact path, so a first draw looks the way it does today. The `Landed` values come back from that layout and are what the caller saves.
- **Otherwise** each group is fit on its own against all of `Work`, with the squeezed → grid retry. Then `SeparateGroups` runs. If boxes still overlap afterwards, fall back to Bands+Compact.
- `Groups` has one entry per non-empty slot, in ascending slot order. `Landed` = the asked-for centre + the group's total integer translation. `DrawnShape` is `"grid"` when the group fell back to a grid.
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

`Point` and `Rect` are `Avalonia.Point` and `Avalonia.Rect`. Use `OrbArrangement.Unit`, which is already `internal`, for the curves. If you need a per-curve helper (`HeartAt` …), promote it from `private` to `internal` in its place in the file. Don't copy it.

## S4 — `OrbSnap` and `DragPlan` (gesture engineer)

```csharp
internal abstract record SnapOutcome
{
    internal sealed record NoChange : SnapOutcome;
    internal sealed record Join(string GroupId) : SnapOutcome;
    internal sealed record Leave : SnapOutcome;
}

// One group the dragged orb could be near. Outline may be empty (outlines off and
// not computed yet), in which case only the discs count.
// Discs exclude the dragged orb and its team.
internal readonly record struct SnapCandidate(
    string GroupId, Point[] Outline, IReadOnlyList<(double X, double Y, double R)> Discs);

internal static class OrbSnap
{
    // All in DIP. The gap to a group is the smaller of: the distance from the dragged
    // orb's edge to the candidate's outline (from outside), or its edge-to-edge distance
    // to the nearest disc.
    //   Join(id): the nearest group other than currentGroupId with gap <= joinDip.
    //             Ties go to the ordinal-smallest id. Join beats Leave.
    //   Leave:    no Join, the current group has at least one other disc, and every
    //             one of them is > leaveDip away edge-to-edge.
    //   NoChange: otherwise, including when the dragged orb is its group's only orb.
    SnapOutcome Decide(Point centre, double radius, string currentGroupId,
        IReadOnlyList<SnapCandidate> candidates, double joinDip = 24, double leaveDip = 72);
}

internal enum DragSource { Orb, Backdrop }

internal static class DragPlan
{
    internal const double ThresholdDip = 6;   // the inline 6 px from OrbWindow.OnPointerPressed

    // teamIds: the pressed orb plus its team (lead and members); groupIds: every orb in its group.
    // Orb, !shift   → (false, teamIds)   — snap/unsnap is decided on release
    // Orb, shift    → (true,  groupIds)
    // Backdrop, any → (true,  groupIds)
    (bool WholeGroup, IReadOnlyList<string> Ids) Followers(
        DragSource source, bool shift, IReadOnlyList<string> teamIds, IReadOnlyList<string> groupIds);
}
```

Ids are session ids (`SessionStatus.SessionId`), the identity `OrbWindow` already carries. When the drag is released:

- **Join**: write the membership for the lead and every member, then call `ReapplyArrangement()`.
- **Leave**: make a new one-orb group (`NewUserGroupId`, "Group N"), anchored at the drop point.
- A menu "Leave group" instead sends the orb back to `Main`.

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

A disc for `GroupOutline` is the orb's visible circle, not its window: the circle's centre and radius in physical px. If `OrbWindow` doesn't already expose that, S5 adds `internal (double X, double Y, double R) ScreenDisc` to `OrbWindow`. A team member's circle is `MemberScale` of its lead's. Padding is `TeamLinkGeometry`-independent: 10 DIP × scaling, as a named constant in `GroupBackdrops`.

The window ordering and the hit-test mechanism follow `docs/cb268-backdrop-spike.md` (S0).

## S7 — integration (architect)

`SessionManager.ComputeClusteredPositions` builds `GroupLayout`s from `OrbGroups.Ordered` and calls `ComputeGroups`. It saves each group's `Landed` anchor when it changed. `ShiftArrangementAnchor(int dx, int dy, string groupId = OrbGroups.Main)`.
