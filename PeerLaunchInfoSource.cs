namespace Orbweaver
{
    // Builds this desk's answer to `launch-info-get`.
    //
    // Every fact is a seam, so the one rule that matters — a desk with the
    // switch off discloses its OS and nothing else — is a unit test with no
    // settings file behind it. The real factory is the only line that touches
    // the settings, the disk and tmux, and is the named coverage exclusion.
    internal sealed class PeerLaunchInfoSource
    {
        internal sealed record Seams(
            Func<bool> Allowed,
            Func<string> Os,
            Func<string?> Home,
            Func<bool> Terminal,
            Func<NewChatCli, bool> CliInstalled,
            Func<IReadOnlyList<RelayAccounts.Account>> Accounts,
            Func<IReadOnlyList<string>> Recents);

        private readonly Seams _seams;

        internal PeerLaunchInfoSource(Seams seams) => _seams = seams;

        internal PeerLaunchInfo Build()
        {
            var os = _seams.Os();

            // Read first and alone: none of the other seams is even called
            // while the switch is off, so nothing is scanned for a peer that
            // was never allowed to see it.
            if (!_seams.Allowed()) return new PeerLaunchInfo(false, os);

            var clis = new[] { NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok }
                .Select(c => new PeerLaunchCli(RelayAccounts.CliName(c), _seams.CliInstalled(c)))
                .ToList();

            var accounts = _seams.Accounts()
                .Select(a => new PeerLaunchAccount(a.Cli, a.Id, a.Label))
                .ToList();

            // CB-434: this build lists and resumes past sessions, and says so
            // here so an asker knows before it waits on a request.
            return new PeerLaunchInfo(
                true, os, _seams.Home(), _seams.Terminal(), clis, accounts, _seams.Recents(), PastSessionsRemote.ResumeVersion);
        }

        // The real desk. Excluded: each lambda is one line into the settings,
        // the filesystem or a `which tmux`; Build above is what is tested.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static PeerLaunchInfoSource ForThisDesk(
            Func<bool> allowed, Func<IReadOnlyList<SessionStatus>> localSessions) => new(new Seams(
                allowed,
                () => OperatingSystem.IsWindows() ? "windows" : "macos",
                () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                () => OperatingSystem.IsWindows() || TerminalLauncher.ResolveTmux() is not null,
                cli => NewChatLauncher.LocateFresh(cli) is not null,
                () => RelayAccounts.From(RelayAccounts.Choices),
                () => RecentFolders.Merge(localSessions(), OrbweaverSettings.NewChatRecentFolders)));
    }
}
