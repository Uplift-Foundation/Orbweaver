namespace Orbweaver
{
    // CB-434: this desk answering "which past sessions could I resume here?"
    // for somebody else — another desktop over the peer link, or the phone
    // over the relay. One answer for both, so the phone and a remote New Chat
    // never disagree about what this machine would resume.
    //
    // The rows are PastSessions.List's own, unfiltered: the asker filters and
    // searches them as it does a local list. Pure apart from its seams.
    internal sealed class PastSessionServe
    {
        internal sealed record Seams(
            // The same switch launch-info and launch honour. Titles and
            // folders are what a person typed and where they work, so they
            // leave this machine only once it has opted in to being launched on.
            Func<bool> Enabled,
            RelayRemote.AccountSeams Accounts,
            // PastSessionIndex.Load: cli, the account's profile dir (null for
            // the default), showOlder, now. Blocking.
            Func<NewChatCli, string?, bool, DateTimeOffset, IReadOnlyList<PastSession>> Load,
            // Session ids an orb is running right now on this desk.
            Func<IReadOnlySet<string>> Live,
            Func<DateTimeOffset> Now);

        // A ceiling on one answer. 30 days of interactive Claude sessions on
        // the mini was under 500; "older" can be every session ever, and an
        // asker that needs the 2001st is better served by its search box than
        // by a bigger frame.
        internal const int MaxRows = 2000;

        internal sealed record Answer(string Code, IReadOnlyList<PastSessionWire>? Rows, bool Truncated)
        {
            internal bool Ok => Code == Listed;
        }

        internal const string Listed = "listed";

        private readonly Seams _seams;

        internal PastSessionServe(Seams seams) => _seams = seams;

        // In order: the switch, the CLI, the account, then the read. A desk
        // that has not opted in answers before looking at anything else.
        internal Answer List(string cli, string account, bool showOlder)
        {
            if (!_seams.Enabled()) return new(PeerLaunchCodes.PeerLaunchOff, null, false);
            if (Array.IndexOf(LaunchGate.Clis, cli) < 0) return new(RelayReplies.UnknownCli, null, false);

            var which = LaunchGate.CliFor(cli);
            var accounts = _seams.Accounts;
            if (!RelayAccounts.TryResolve(which, account, accounts.Choices(which), accounts.Designated(which), null, out var profileDir))
                return new(RelayReplies.UnknownAccount, null, false);

            var now = _seams.Now();
            IReadOnlyList<PastSession> sessions;
            try { sessions = _seams.Load(which, profileDir, showOlder, now); }
            catch (Exception) { return new(RelayReplies.Unreadable, null, false); }

            var rows = PastSessions.List(sessions, _seams.Live(), null, null, now, showOlder)
                .Select(r => ToWire(cli, r))
                .OfType<PastSessionWire>()
                .ToList();
            return new(Listed, rows.Take(MaxRows).ToList(), rows.Count > MaxRows);
        }

        // Null for a row the asker's parser would refuse — and one refused row
        // refuses the whole answer there, so it is dropped here instead. A
        // title is cleaned rather than dropped (a pasted prompt can carry a
        // stray escape); an id or folder is never rewritten, because a resume
        // has to find exactly that one.
        internal static PastSessionWire? ToWire(string cli, PastSessionRow row)
        {
            var s = row.Session;
            if (!PastSessions.IsSafeId(s.Id) || s.Id.Length > 256) return null;
            if (s.Cwd is { } cwd && (cwd.Length > PeerLaunchWire.MaxText || cwd.Any(char.IsControl))) return null;
            var title = new string(s.Title.Where(c => !char.IsControl(c)).ToArray());
            return new(cli, s.Id, s.Cwd, title, Math.Max(0, s.LastActivity.ToUnixTimeSeconds()), s.FolderMissing, row.IsLive);
        }

        // The asker's half: a wire row back into the row New Chat draws. No
        // transcript path and no count — this side never reads a far file.
        internal static PastSessionRow FromWire(PastSessionWire w) =>
            new(new PastSession(LaunchGate.CliFor(w.Cli), w.Id, w.Cwd, w.Title,
                DateTimeOffset.FromUnixTimeSeconds(w.LastActivity), null, "", w.FolderMissing), w.Live);

        // The real desk. Excluded: each seam is one line into settings, the
        // account list, the shared index or the session snapshot.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static PastSessionServe ForThisDesk() => new(new Seams(
            () => OrbweaverSettings.PeerLaunchInboundEnabled,
            new RelayRemote.AccountSeams(RelayAccounts.Choices, _ => null),
            PastSessionIndex.Load,
            () => RemoteControlSessions.LocalSessions()
                .Where(s => s.Status.IsLocalCli)
                .Select(s => s.SessionId)
                .ToHashSet(StringComparer.Ordinal),
            () => DateTimeOffset.UtcNow));
    }
}
