using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orbweaver
{
    // Kill a child process and everything under it, without ever stopping
    // anything (CB-306).
    //
    // .NET's `Process.Kill(entireProcessTree: true)` on Unix SIGSTOPs each
    // process, scans for its children, then SIGKILLs it, parents before
    // children. On macOS that is fatal to *us*: Orbweaver runs in launchd's
    // session as the leader of its own process group, so the group is not
    // orphaned only because launchd (its parent) sits in another group of the
    // same session (the group's job-control count is 1). SIGKILL a parent and
    // its children are reparented to launchd, not yet stopped; each of them is
    // then SIGSTOPped for its own child scan. If an orphaned sibling exits in
    // that window, the count drops from 1 to 0, XNU finds a stopped member of
    // the now-orphaned group and delivers SIGHUP+SIGCONT to all of it,
    // Orbweaver included. The default action for SIGHUP is to terminate.
    // docs/sighup-findings.md has the trials and what is and is not confirmed.
    //
    // So on macOS this takes a snapshot of the descendants first (libproc),
    // SIGKILLs the root and then each descendant, and never sends SIGSTOP.
    // Nothing is stopped, so there is nothing for the orphan rule to wake.
    //
    // **Known trade-off, deliberately accepted:** a child forked between the
    // snapshot and the kills is not in the snapshot and survives as an orphan
    // of launchd. That leak is harmless — it was never stopped, so it cannot
    // trigger the SIGHUP — and is the price of not freezing the tree to get a
    // stable view of it. Everywhere else (Windows) this is exactly
    // `Process.Kill(true)`, which has no process groups to disturb.
    internal static class ProcessTreeKill
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";
        private const int SigKill = 9;
        // pids per parent, not for the tree: a parent with more children than
        // this has the overflow left out of the snapshot, which is to say leaked.
        private const int MaxChildrenPerParent = 4096;

        [DllImport(LibSystem)]
        private static extern int proc_listchildpids(int ppid, int[] buffer, int buffersize);

        [DllImport(LibSystem)]
        private static extern int kill(int pid, int sig);

        internal static void Kill(Process process) => Kill(process, OperatingSystem.IsMacOS());

        // `useLibproc` is a parameter so that both arms can be exercised from
        // one machine: the Windows arm is just the framework call and works on
        // macOS too.
        internal static void Kill(Process process, bool useLibproc)
        {
            if (!useLibproc)
            {
                process.Kill(true);
                return;
            }

            if (process.HasExited) return;
            foreach (var pid in KillOrder(process.Id, Children)) kill(pid, SigKill);
        }

        // The order to kill in: the root first, then its descendants
        // breadth-first, each pid at most once. A `children` that reports a
        // pid already seen (a reparented process listed under two parents, or a
        // cycle from a recycled pid) cannot make this loop or kill twice.
        internal static IReadOnlyList<int> KillOrder(int root, Func<int, IReadOnlyList<int>> children)
        {
            var order = new List<int> { root };
            var seen = new HashSet<int> { root };
            for (var i = 0; i < order.Count; i++)
            {
                foreach (var child in children(order[i]))
                {
                    if (seen.Add(child)) order.Add(child);
                }
            }
            return order;
        }

        private static IReadOnlyList<int> Children(int ppid)
        {
            var buffer = new int[MaxChildrenPerParent];
            var returned = proc_listchildpids(ppid, buffer, buffer.Length * sizeof(int));
            // The return is a count on the macOS this was measured on; anything
            // that is not a positive pid (a zero-filled tail, or bytes read as
            // a count) is filtered rather than trusted.
            if (returned <= 0) return [];
            return buffer.Take(Math.Min(returned, buffer.Length)).Where(p => p > 0).ToArray();
        }
    }
}
