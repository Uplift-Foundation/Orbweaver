using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Orbweaver
{
    // "Stop this session", asked once, by hand, from an orb's context menu.
    //
    // Structured like ProcessLiveness next door — one question, one libc call on
    // Unix, a Windows arm that does the same thing a different way — and for the
    // same reason: the decision about *whether* to ask belongs somewhere pure
    // and testable (SessionPresence.CanEndSession), so all that is left here is
    // the syscall.
    //
    // SIGTERM, not SIGKILL. The pid is a `claude` process, which handles TERM by
    // finishing its own shutdown — flushing the transcript, running SessionEnd,
    // which deletes the status file and takes the orb with it. SIGKILL would
    // leave both behind and land the cleanup on the sweep instead.
    //
    // That reasoning is the Unix arm's alone, and the Windows arm cannot have
    // it: Kill() is TerminateProcess, which is unconditional and runs no
    // shutdown, so there is no SessionEnd and no `rm -f`. On Windows the orb
    // still goes on the next scan — the pid stops answering, which is all
    // ProcessGone needs — and the file waits for the sweep's grace to expire.
    // Nothing is lost either way; the difference is ten minutes of a file
    // nobody is looking at, and .NET offers no portable "ask it to stop".
    //
    // Only ever the session's own pid. Not TermPid, which is the terminal the
    // user is typing in; not the daemon, which hosts every other background job
    // on the machine. One worker hosts exactly one session — each row of
    // `claude agents --json` names a distinct pid — so this ends exactly the
    // process the menu item named.
    //
    // The process, not necessarily the job (CB-264). A background job whose
    // worker is signalled while it still has work in front of it is resumed by
    // the daemon on a fresh pid, so for a job this would end nothing at all.
    // SessionManager.EndSession therefore asks the daemon first, through
    // BackgroundJobs.Stop, and only reaches here for a session that is not a
    // job or a daemon that could not be asked.
    //
    // **That last paragraph is false for exactly one shape, and this is what
    // CB-26 added.** For the husk a backgrounded turn leaves behind, the
    // session's own pid is simultaneously the terminal the user is typing in
    // *and* the ancestor of the daemon: both exclusions are violated by the
    // same pid, so neither of them is doing the work the comment credits it
    // with. The Unix arm signalled one pid and killed the user's only window
    // onto a running job; the Windows arm would have taken the whole tree,
    // daemon included, with no SessionEnd and no transcript flush.
    //
    // So a pid is now asked what is underneath it before it is signalled at
    // all, and the answer travels with it rather than being looked up here —
    // SessionDependents.Of reads the process table, SessionDependents.Inspect
    // decides, and the same verdict that disabled the menu row is the one
    // handed to this call. A required parameter rather than a lookup inside,
    // because a caller cannot forget an argument the compiler demands, and
    // because looking it up here would ask the machine twice for one gesture
    // and could get two different answers.
    internal static class SessionTerminator
    {
        private const int SIGTERM = 15;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        // Excluded from coverage: this is a real signal to a real process, and
        // there is no version of testing it that does not end something on the
        // machine running the suite. The decision it carries out is
        // SessionPresence.CanEndSession, which is pure and covered per case; the
        // platform split is the same one ProcessLiveness.IsRunningWindows
        // documents — coverage comes from one platform's run, so the two arms
        // can never both be measured.
        //
        // Failures are swallowed, and a pid that had already exited counts as
        // success: the user asked for the session to be over, and it is. There
        // is nowhere to report a failure to in any case — this app has no dialog
        // vocabulary — and the orb going away on the next scan is the only
        // feedback the gesture has ever needed.
        [ExcludeFromCodeCoverage]
        public static void Terminate(int pid, SessionDependents.Verdict dependents)
        {
            // Belt to CanEndSession's braces. Signalling 0 on Unix means "every
            // process in my process group", which is this app and every helper
            // it has spawned — the one mistake here that could not be walked
            // back, so it is refused twice.
            if (pid <= 0) return;

            // And the same belt for the new rule. SessionManager.EndSession has
            // already refused this, and the menu row the click came from was
            // disabled — this is the third refusal, and it is here for the same
            // reason the pid check above is: the cost of one redundant branch is
            // nothing, and the cost of the one path that reaches here without
            // having asked is a daemon and every job under it.
            if (SessionDependents.BlocksTermination(dependents)) return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // The whole tree, because a Claude Code session owns its MCP
                    // servers as children and they do not exit on their own.
                    //
                    // The adjacent claim used to be that this is safe "in the
                    // direction that matters: the terminal is this process's
                    // *parent*, never a descendant". That is true of an ordinary
                    // session and exactly backwards for a husk, whose
                    // descendants are the daemon and every background job on the
                    // machine. Nothing about the tree kill is narrowed here —
                    // narrowing it would mean deciding which of a session's own
                    // children deserve to survive, which nothing can answer — so
                    // the guard is instead that such a pid never reaches this
                    // line. Inferred from the code and a macOS process tree,
                    // **not reproduced on Windows**; see SessionDependents.
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    ProcessTreeKill.Kill(process);
                    return;
                }

                kill(pid, SIGTERM);
            }
            catch
            {
                // Gone already, or not ours to signal. Either way there is
                // nothing further to do and nothing to say.
            }
        }
    }
}
