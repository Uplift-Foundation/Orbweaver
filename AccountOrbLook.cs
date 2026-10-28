namespace Orbweaver
{
    // What one usage orb actually wears once overrides are applied: the letters,
    // the name for the tooltip and card, and an icon file name when one is set
    // and well-formed. Whether the icon file still exists and decodes is decided
    // by AccountOrbIcons.Load at draw time; this only carries the name.
    internal sealed record AccountOrbLook(string Glyph, string DisplayName, string? IconFile);

    // Override precedence, pure so it can be asserted without a window or a
    // settings file. The override is applied at draw time and never written
    // back into AccountUsage, so the pollers' parsers and the "did the drawn
    // value change" comparison are untouched.
    internal static class AccountOrbLooks
    {
        internal static AccountOrbLook Resolve(string derivedLabel, AccountOrbOverride? over, bool twoLetter)
        {
            var o = over?.Normalise();

            // Initials win verbatim (clipped, never re-derived). A name with no
            // initials re-derives the letters from the name — "the letters are
            // usually derived from the name". Otherwise the poller's label.
            string glyph;
            var clipped = OrbGlyph.Clip(o?.Initials, twoLetter);
            if (clipped.Length > 0) glyph = clipped;
            else if (o?.Name is { } name) glyph = OrbGlyph.For(name, twoLetter);
            else glyph = OrbGlyph.For(derivedLabel, twoLetter);

            var icon = AccountOrbIcons.IsSafeFileName(o?.IconFile) ? o!.IconFile : null;
            return new AccountOrbLook(glyph, o?.Name ?? derivedLabel, icon);
        }

        internal static AccountOrbLook For(AccountUsage usage) =>
            Resolve(
                usage.Label,
                OrbweaverSettings.AccountOrbOverrideFor(AccountOrbs.PositionKey(usage.ConfigDir)),
                OrbweaverSettings.TwoLetterGlyphs);
    }
}
