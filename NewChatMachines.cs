namespace Orbweaver
{
    // CB-357: which machine the New Chat window is aimed at, and what that
    // machine's answer allows the form to offer. Pure — no window, no settings,
    // no link — for the reason OrbArrangement is: the rules are the part worth
    // a case per outcome, and a window is the part that makes them hard to reach.
    internal static class NewChatMachines
    {
        // One item of the Machine combo. Name is null for this machine, which is
        // the only entry that is not a paired peer.
        internal sealed record Entry(string? Name, string Label, bool Online)
        {
            internal bool IsSelf => Name is null;

            // An offline peer is listed so a person can see it is paired, but
            // cannot be aimed at: there is nothing to ask.
            internal bool Selectable => Online;
        }

        internal const string OfflineSuffix = " — offline";

        // The Machine row's items, or an empty list when there is no row at
        // all: the peer link off or no peer paired leaves the window exactly as
        // it was before this ticket. This machine first, then online peers by
        // name, then offline ones by name.
        internal static IReadOnlyList<Entry> Build(bool peerLinkOn, IReadOnlyList<PeerMachine> peers, bool isWindows)
        {
            if (!peerLinkOn || peers.Count == 0) return [];

            var list = new List<Entry> { new(null, isWindows ? "This PC" : "This Mac", true) };
            foreach (var p in peers.Where(p => p.Online).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new Entry(p.Name, p.Name, true));
            foreach (var p in peers.Where(p => !p.Online).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new Entry(p.Name, p.Name + OfflineSuffix, false));
            return list;
        }

        internal enum InfoState { Loading, NoAnswer, NotAllowed, Ready }

        // What a remote machine's launch-info lets the form offer. Computed
        // once per answer; the window only paints it.
        internal sealed record Scope(
            InfoState State,
            PeerLaunchInfo? Info,
            string Machine)
        {
            // Ready alone is not enough: a machine with nowhere to start a CLI,
            // or none installed, answers fine and still cannot be launched on.
            internal bool StartEnabled => State == InfoState.Ready && AnyCli;

            internal bool AnyCli =>
                State == InfoState.Ready && Info is { Terminal: true, Clis: { } clis } && clis.Any(c => c.Installed);

            // The note line under the Machine row. Null when there is nothing
            // to say, in which case the line keeps its height and shows nothing.
            internal string? Note => State switch
            {
                InfoState.Loading => PeerLaunchCopy.Asking(Machine),
                InfoState.NoAnswer => PeerLaunchCopy.Explain(PeerLaunchCodes.NoAnswer, null, "claude", Machine),
                InfoState.NotAllowed => PeerLaunchCopy.Explain(PeerLaunchCodes.PeerLaunchOff, null, "claude", Machine),
                InfoState.Ready when Info is { Terminal: false } => PeerLaunchCopy.NoTerminal(Machine),
                InfoState.Ready when !AnyCli => PeerLaunchCopy.NoCli(Machine),
                _ => null,
            };

            // A CLI is offered only when the far machine has it installed and
            // somewhere to start it.
            internal bool CliEnabled(string cli) =>
                State == InfoState.Ready
                && Info is { Terminal: true, Clis: { } clis }
                && clis.Any(c => c.Cli == cli && c.Installed);

            // The far machine's recents, or its home folder when it has none,
            // so a machine that has never run a session can still be launched
            // on. Empty when the machine said nothing usable.
            internal IReadOnlyList<string> Folders()
            {
                if (State != InfoState.Ready || Info is null) return [];
                if (Info.Recents is { Count: > 0 } r) return r;
                return Info.Home is { Length: > 0 } h ? [h] : [];
            }

            internal IReadOnlyList<PeerLaunchAccount> Accounts(string cli) =>
                State == InfoState.Ready && Info?.Accounts is { } a ? a.Where(x => x.Cli == cli).ToList() : [];
        }

        internal static Scope Loading(string machine) => new(InfoState.Loading, null, machine);

        // A null info is no answer; allowed:false is the machine declining. The
        // two read differently because only one of them is the user's to fix
        // (by turning the switch on at the far end).
        internal static Scope From(string machine, PeerLaunchInfo? info) => info switch
        {
            null => new Scope(InfoState.NoAnswer, null, machine),
            { Allowed: false } => new Scope(InfoState.NotAllowed, info, machine),
            _ => new Scope(InfoState.Ready, info, machine),
        };
    }
}
