namespace Orbweaver
{
    // The refuse-or-evict decision for tools/peer-probe (CB-306).
    //
    // The probe dials with this machine's own peer identity, and the far side's
    // PeerLink.Adopt replaces any connection already held under that machine
    // name. So running the probe beside a live Orbweaver knocks the app off the
    // link. Lives in the app assembly, not the tool, so the unit suite can reach
    // it; the tool only calls it.
    internal static class PeerProbeGuard
    {
        internal readonly record struct Verdict(bool Run, string? Message);

        // Pulls --evict out wherever it sits, leaving positional arguments.
        internal static (bool Evict, string[] Remaining) SplitEvict(string[] args)
        {
            var rest = args.Where(a => a != "--evict").ToArray();
            return (rest.Length != args.Length, rest);
        }

        internal static Verdict Decide(bool appRunning, bool evict) => (appRunning, evict) switch
        {
            (false, _) => new Verdict(true, null),
            (true, true) => new Verdict(true,
                "warning: Orbweaver is running. --evict given, so this probe will disconnect " +
                "the running app from that peer; pushes sent meanwhile are lost."),
            (true, false) => new Verdict(false,
                "refusing to run: Orbweaver is running, and dialling with this machine's " +
                "identity would replace its connection to that peer (the far side keeps one " +
                "link per machine name). Quit Orbweaver, or pass --evict to disconnect it " +
                "deliberately."),
        };

        // Whether another process holds the app's per-user single-instance claim.
        // Uses the app's own ClaimAll so the probe and the app can never disagree
        // about what "running" means, and gives the claim straight back when it
        // wins it — a probe must not itself block the app from starting.
        internal static bool AppIsRunning(
            IReadOnlyList<string>? names = null, bool? onWindows = null)
        {
            var (proceed, held) = SingleInstance.ClaimAll(names ?? SingleInstance.ClaimNames, onWindows);
            if (!proceed) return true;

            for (var i = held.Count - 1; i >= 0; i--)
            {
                held[i].ReleaseMutex();
                held[i].Dispose();
            }
            return false;
        }
    }
}
