using Avalonia;

namespace Orbweaver
{
    // CB-282's group name caption: where it sits, and how big the backdrop window
    // has to be to hold it. Pure, in the units of whatever outline it is handed
    // (physical pixels in the app), so the placement is tested for every shape
    // without a window. GroupBackdrops measures the text and draws it.
    //
    // The caption is centred over the outline's bounding box and sits entirely
    // above its top edge. Nothing in a group is above its own outline, so it can
    // never cover one of the group's orbs. It is drawn in a window of its own, a
    // click-through one: the backdrop's native region is the silhouette, and on
    // Windows a region clips drawing too, so a caption outside the silhouette in
    // the backdrop's own window would never appear.
    //
    // Placement takes bounds, not a polygon, so an animated outline can ask on
    // every frame with the bounds of whatever polygon it is drawing and the
    // caption rides it.
    internal static class GroupCaption
    {
        internal const double FontSizeDip = 12;

        // Between the caption's bottom and the outline's top, in DIP.
        internal const double GapDip = 4;

        // The text shown: the group's name, or "Group N" when it has none. N is
        // the group's place among user groups, so two nameless groups differ.
        internal static string TextFor(string? name, int ordinal) =>
            string.IsNullOrWhiteSpace(name)
                ? "Group " + Math.Max(1, ordinal).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : name.Trim();

        // The caption's rectangle: `text` wide and tall, centred on the outline's
        // bounds and `gap` above its top.
        internal static Rect Place(Rect outlineBounds, Size text, double gap) =>
            new(outlineBounds.Center.X - text.Width / 2, outlineBounds.Top - gap - text.Height, text.Width, text.Height);
    }
}
