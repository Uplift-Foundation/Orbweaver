namespace Orbweaver
{
    // CB-287: what the user has chosen for one usage orb, as stored. Every field
    // is optional and null means "no override" — the derived label, letters and
    // colour still apply for whatever is null. IconFile is a bare file name
    // minted by AccountOrbIcons, never a path: settings.json travels (the data
    // dir is migrated, tests redirect it), and a stored path would not.
    internal sealed record AccountOrbOverride(string? Initials, string? Name, string? IconFile)
    {
        // Trims every field and turns blank into null. Applied on the way in
        // and on load, so a hand-edited file and a UI write reach the same
        // shape and "nothing set" is spelled exactly one way.
        internal AccountOrbOverride Normalise() =>
            new(Empty(OrbGlyph.Clip(Initials, twoLetter: true)), Empty(OrbGlyph.ClipText(Blank(Name), MaxNameElements).Trim()), Blank(IconFile));

        // The display name feeds the tooltip and the card, so a hand-edited
        // 5000-character value is cut here, on every path in (UI write and
        // file load alike), by the same text-element rule the initials use.
        internal const int MaxNameElements = 64;

        private static string? Empty(string s) => s.Length == 0 ? null : s;

        // True when this overrides nothing — the entry is removed rather than
        // stored, the same rule SetOrbTurnSound applies.
        internal bool IsEmpty
        {
            get
            {
                var n = Normalise();
                return n.Initials is null && n.Name is null && n.IconFile is null;
            }
        }

        private static string? Blank(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    // Which account an orb draws, as the UI surfaces (the editor, the Settings
    // rows) need to name it: the key overrides are stored under, the CLI it
    // belongs to, and the label the poller derived for it. The derived label is
    // here so an editor can show "was: foo@bar.com" beside an empty field.
    internal sealed record AccountOrbIdentity(
        string AccountKey,
        string PositionKey,
        AccountUsageSource Source,
        string DerivedLabel);
}
