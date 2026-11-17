using System;

namespace Orbweaver
{
    // CB-330: kept out of RelayRow.cs because OrbweaverSettings.cs needs the
    // enum and its two words and tools/openclaw-probe compiles that file in by
    // hand; RelayRow pulls in the chat panel's types, which the probe has not
    // got. Same reason as HiddenOrb.cs.

    // The three answers the setting can give. Collapsed is the default: a lead
    // with a team gets an idle notification per teammate per turn, and a full
    // bubble for each is exactly the noise this ticket removes.
    public enum RelayedMessageDisplay { Collapsed, Expanded, Hidden }

    internal static class RelayedMessageWords
    {
        // The JSON spellings, and the only ones: a hand-edited or newer value
        // is read as Collapsed rather than guessed at (see Parse).
        internal static string Name(RelayedMessageDisplay display) => display switch
        {
            RelayedMessageDisplay.Expanded => "expanded",
            RelayedMessageDisplay.Hidden => "hidden",
            _ => "collapsed"
        };

        // Unknown, empty and missing all read as Collapsed. A settings file is
        // editable by hand and shared with versions that know more words than
        // this one, and the cost of misreading one must be "the default", never
        // "every teammate message vanishes".
        internal static RelayedMessageDisplay Parse(string? value) =>
            string.Equals(value, "expanded", StringComparison.OrdinalIgnoreCase) ? RelayedMessageDisplay.Expanded
            : string.Equals(value, "hidden", StringComparison.OrdinalIgnoreCase) ? RelayedMessageDisplay.Hidden
            : RelayedMessageDisplay.Collapsed;
    }
}
