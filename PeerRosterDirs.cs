using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Orbweaver
{
    // Which Claude Code config dirs the peer link reads `claude agents` from
    // (CB-300).
    //
    // **The link read a settings key nothing could write.** CB-61 made the peer
    // link answer for every configured account by reading
    // RemoteControlProfileDirs, a list the relay's settings card was the only
    // writer of. The relay was deleted the same day, so the list froze at
    // whatever it held, and a profile added afterwards in Settings → Claude Code
    // (ClaudeCodeProfileDirs) was never offered. Nothing failed: the roster was
    // simply empty for every session under that profile.
    //
    // The answer is a union rather than a swap, so nothing shared today drops
    // out: the old list first and in its own order (so the first entry, which
    // is the label both halves of the link carry, and MergeRosters' first-claim
    // tie-break, stay put on every existing install), then the default, then
    // the profiles the user configured.
    internal static class PeerRosterDirs
    {
        internal static IReadOnlyList<string> For(
            string home,
            IReadOnlyList<string> remoteControlProfileDirs,
            IReadOnlyList<string> claudeCodeProfileDirs)
        {
            var dirs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? dir)
            {
                // The half-finished settings edit ClaudeConfigRoots.All guards
                // against as well: Path.Combine would answer $HOME for it.
                if (string.IsNullOrWhiteSpace(dir)) return;

                var trimmed = dir.Trim();

                // By resolved path, never by string: ".claude", "./.claude",
                // ".claude/" and "<home>/.claude" are one directory, and a
                // string comparison would run `claude agents` twice for it —
                // the second time without CB-42's unset-for-default rule. The
                // first spelling seen is kept, so the default stays spelled as
                // the default and CLAUDE_CONFIG_DIR stays unset for it.
                if (seen.Add(ClaudeProfile.Resolve(home, trimmed))) dirs.Add(trimmed);
            }

            foreach (var dir in remoteControlProfileDirs) Add(dir);
            Add(OrbweaverSettings.DefaultRemoteControlProfileDir);
            foreach (var dir in claudeCodeProfileDirs) Add(dir);

            return dirs;
        }

        // The real answer, for the app and tools/peer-probe alike so the two
        // cannot disagree. Excluded from coverage because it reads the live
        // settings; the rule is For above, and the settings seam is covered by
        // the integration test that calls this with a scratch home.
        [ExcludeFromCodeCoverage]
        internal static IReadOnlyList<string> Configured(string? home = null) =>
            For(
                home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                OrbweaverSettings.RemoteControlProfileDirs,
                OrbweaverSettings.ClaudeCodeProfileDirs);
    }
}
