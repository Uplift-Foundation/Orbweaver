namespace Orbweaver.Tests;

/// <summary>
/// CB-397: the real Claude Code folder-trust captures (Claude Code 2.1.330 on the Mac
/// mini, tmux capture-pane -p, byte for byte; the yes files had one Down pressed and
/// Enter never sent). The folders in them are throwaway /private/tmp paths.
/// </summary>
internal static class TrustScreens
{
    internal const string Folder = "/private/tmp/cb397-w/git-plain";
    internal const string HooksFolder = "/private/tmp/cb397-w/git-hooks";

    internal static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrustFixtures", name + ".txt"));

    internal static string OnNo => Read("plain-w100-no");
    internal static string OnYes => Read("plain-w100-yes");
    internal static string HooksOnNo => Read("hooks-w100-no");
    internal static string HooksOnYes => Read("hooks-w100-yes");
}
