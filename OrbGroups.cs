namespace Orbweaver
{
    // CB-268's groups: named sets of orbs, each drawn as its own shape around
    // its own anchor, optionally with that shape outlined behind them.
    //
    // This file starts as contracts only — the two records and the predefined
    // ids — so the streams building the store (S1), the arrangement (S2), the
    // outline (S3), the gesture (S4) and the backdrops (S5) can all compile
    // against the same names from day one. docs/cb268-contracts.md is the
    // agreed surface for everything else; the store, precedence and migration
    // land here in S1.
    //
    // The three predefined groups are the shapes the app already drew before
    // groups existed: `main` is the one ArrangeShape/ArrangeAnchor described,
    // and `heartbeats` and `crons` are CB-14's OpenClaw clusters, which were
    // slots 1 and 2 of OrbClusters.GroupOf. Keeping those slot numbers is
    // deliberate — see the comment on GroupOf about why a slot is fixed by the
    // group table and never by which sessions happen to exist.
    internal static partial class OrbGroups
    {
        /// <summary>The group every orb is in unless something says otherwise. Slot 0.</summary>
        internal const string Main = "main";

        /// <summary>CB-14's heartbeat cluster, folded in as a predefined group. Slot 1.</summary>
        internal const string Heartbeats = "heartbeats";

        /// <summary>CB-14's cron cluster, folded in as a predefined group. Slot 2.</summary>
        internal const string Crons = "crons";
    }

    /// <summary>
    /// One group of orbs. <paramref name="Id"/> is <see cref="OrbGroups.Main"/>,
    /// <see cref="OrbGroups.Heartbeats"/>, <see cref="OrbGroups.Crons"/>, or
    /// <c>g-&lt;8 hex&gt;</c> for a user group. <paramref name="Shape"/> is one of
    /// the arrangement shape names (<c>heart</c>, <c>circle</c>, <c>diamond</c>,
    /// <c>star</c>, <c>grid</c>, <c>line</c>, <c>vline</c>). <paramref name="Anchor"/>
    /// is the shape's centre in physical pixels, null until the group has been
    /// drawn once; <paramref name="Style"/> is a per-group override, null meaning
    /// "use the global outline style" outright.
    /// </summary>
    internal sealed record OrbGroup(
        string Id,
        string Name,
        string Shape,
        OrbweaverSettings.OrbPlacement? Anchor,
        GroupStyle? Style);

    /// <summary>
    /// A group's outline style. Every field is an override: null inherits the
    /// matching global setting (<c>groupOutlineStroke</c>, …), field by field,
    /// so a group can change only its fill. Colours are <c>#RRGGBB</c> or
    /// <c>#AARRGGBB</c> strings as the settings file already stores colours;
    /// opacities are 0..1 (the Settings slider shows 0–100%). Partial so S5 can
    /// add the pure <c>Resolve</c> next to the code that draws with it, without
    /// editing this file.
    /// </summary>
    internal sealed partial record GroupStyle(
        string? Stroke,
        double? StrokeOpacity,
        string? Fill,
        double? FillOpacity);
}
