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

    // CB-476: the Windows PC's own console, captured by bryn-kowalczyk: Claude Code
    // started as a Windows launch starts it (conhost.exe cmd.exe /k, named account
    // .claude-sundaysky) in the logged-on session, buffer 120x9001, window 120x30,
    // read with AttachConsole + ReadConsoleOutputCharacterW over the visible window
    // as WindowsConsoleRead does. The yes files had one VK_DOWN written as console
    // input and Enter never sent. The only change from the captured files is
    // WindowsConsoleRead.Assemble's own shape: LF between rows, no trailing newline
    // (the capture script joined with CRLF). conhost draws the marker as ">" and
    // the hooks sign as "‼". "home" is the account's home folder, which asks every
    // session and declares hooks; "plain" is a fresh folder under it.
    internal const string WindowsHome = @"C:\Users\warre";
    internal const string WindowsPlain = @"C:\Users\warre\tmp-cb476";
    internal static string ConsoleOnNo => Read("conhost-home-w120-no");
    internal static string ConsoleOnYes => Read("conhost-home-w120-yes");
}
