using System.Text;

namespace Orbweaver
{
    // CB-409: the colour a group wears when "Random per group" is on. Pure — no
    // settings, no windows — like OrbGlyph, so the one rule that matters
    // (the same group gets the same colour every time) is asserted directly.
    //
    // The colour is a function of the group id, the global seed and the group's
    // own salt, so it survives rescans, restarts and orbs joining or leaving.
    // The seed is what Shuffle re-rolls for every group at once; the salt is
    // what "New random colour" re-rolls for one.
    internal static class GroupColour
    {
        // Twelve hues, mid-saturation so they do not fight the orb rings from
        // OrbColors and mid-lightness so a 0.35-alpha stroke reads on a light
        // desktop as well as a dark one. Neighbours are spread around the hue
        // wheel, so two groups that land on adjacent slots still look different.
        internal static readonly string[] Palette =
        {
            "#E5484D", // red
            "#2F9E8F", // teal
            "#F5A524", // amber
            "#5B6CF0", // indigo
            "#76B947", // green
            "#D6409F", // magenta
            "#17A2D4", // sky
            "#E8743B", // orange
            "#8E5CD9", // violet
            "#3FB57A", // emerald
            "#D9C22B", // yellow
            "#C2557D", // rose
        };

        // FNV-1a, 32 bit, over the id's UTF-8 bytes and then the seed and salt as
        // four little-endian bytes each. Not string.GetHashCode(): .NET
        // randomises that per process, which would quietly turn "stable across
        // restarts" into "different every launch".
        internal static string For(string groupId, int seed, int salt)
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(groupId ?? string.Empty)) hash = Mix(hash, b);
            hash = MixInt(hash, seed);
            hash = MixInt(hash, salt);
            return Palette[(int)(hash % (uint)Palette.Length)];
        }

        private static uint Mix(uint hash, byte b) => (hash ^ b) * 16777619u;

        private static uint MixInt(uint hash, int value)
        {
            for (var i = 0; i < 4; i++) hash = Mix(hash, (byte)(value >> (8 * i)));
            return hash;
        }
    }
}
