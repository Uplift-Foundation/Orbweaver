using System.Collections;
using System.Diagnostics;

namespace Orbweaver
{
    // CB-413: the environment Orbweaver hands to a session it launches.
    //
    // The app may itself have been started from inside an agent's shell (an
    // `--install` relaunch is exactly that), so its environment can carry the
    // identity of whichever Claude Code session did the starting. Anything it
    // launched then inherited that identity: a new `claude` registered itself as
    // the *other* session's background job and made that session vanish, and a
    // `tmux new-session` honoured the inherited TMUX and created the pane inside
    // the other agent's swarm server instead of the default one.
    //
    // The decision is pure: names in, names to drop out. Callers apply it to a
    // ProcessStartInfo, to the app's own environment at startup, or turn it into
    // `env -u` arguments for a command a tmux server will run.
    //
    // A denylist, not an allowlist: a launched CLI legitimately needs PATH, HOME,
    // proxies, provider keys, CLAUDE_CONFIG_DIR for a named account and
    // CLAUDE_CODE_USE_BEDROCK-style switches, none of which can be enumerated
    // here. What cannot be allowed to pass is *per-session identity*, which is a
    // closed, recognisable set: the exact names and prefixes below.
    internal static class AgentEnvironment
    {
        // Names that identify one running session or one tmux client.
        internal static readonly string[] ExactNames =
        [
            "TMUX", "TMUX_PANE",
            "CLAUDECODE", "CLAUDE_PID", "CLAUDE_JOB_DIR", "CLAUDE_EFFORT",
            "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH",
            // Which project the launching session bills and attributes its work
            // to. Measured in a live teammate's environment on the mini, and in
            // the exec-time environment of an app `--install` relaunched from
            // one. They are the launching session's, not the user's: a session
            // the app starts picks its own project from its own cwd.
            "CLAUDE_PROJECT_UUID", "CLAUDE_CODE_SPEND_PROJECT", "CLAUDE_CODE_ATTRIBUTION_PROJECT",
        ];

        // Families of per-session variables: session id/kind/attended, the
        // messaging socket and token, the bridge session, child-session markers,
        // the sessions that launched this one, delegated-session markers.
        internal static readonly string[] Prefixes =
        [
            "CLAUDE_CODE_SESSION_", "CLAUDE_CODE_MESSAGING_", "CLAUDE_CODE_BRIDGE_",
            "CLAUDE_CODE_CHILD_", "CLAUDE_CODE_LAUNCHED_", "CLAUDE_CODE_PARENT_",
            "CLAUDE_CODE_USER_DELEGATED_SESSION",
        ];

        // Names the user configured on purpose. They match no rule above today;
        // the list exists so a future broader prefix cannot swallow them silently.
        internal static readonly string[] Kept = ["CLAUDE_CONFIG_DIR"];

        internal static bool IsAgentVariable(string name)
        {
            if (Kept.Contains(name, StringComparer.Ordinal)) return false;
            if (ExactNames.Contains(name, StringComparer.Ordinal)) return true;
            foreach (var prefix in Prefixes)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        // The tmux client variables, which a pane's own command must keep (tmux
        // sets them for the pane itself) but the *client* we run must not.
        internal static bool IsTmuxClientVariable(string name) => name is "TMUX" or "TMUX_PANE";

        // Which of the inherited names to drop.
        internal static string[] Removals(IEnumerable<string> inheritedNames) =>
            inheritedNames.Where(IsAgentVariable).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // Names to drop from a command run *inside* a tmux pane: the identity
        // set without TMUX/TMUX_PANE, which tmux itself sets for that pane.
        // A tmux server also keeps its own global environment, so a pane inherits
        // the *server's* copies whatever the client's were; only the command can
        // remove those.
        internal static string[] PaneRemovals(IEnumerable<string> inheritedNames) =>
            Removals(inheritedNames).Where(n => !IsTmuxClientVariable(n)).ToArray();

        // Inside the pane, the prefix half of the rule at run time. The tmux
        // *server's* global environment reaches a new pane whatever ours is, and
        // this process has already scrubbed itself, so neither CurrentNames nor
        // a fixed list can know every CLAUDE_CODE_USER_DELEGATED_SESSION_* or
        // CLAUDE_CODE_PARENT_* the server holds. The shell enumerates what it
        // actually inherited and unsets each name a prefix matches, before the
        // line runs. POSIX sh and sed only; prefixes are [A-Z_], so the case
        // patterns need no quoting.
        internal static readonly string PanePreamble =
            "for n in $(env | sed -n 's/^\\([A-Za-z_][A-Za-z0-9_]*\\)=.*/\\1/p'); do case \"$n\" in "
            + string.Join("|", Prefixes.Select(p => p + "*"))
            + ") unset \"$n\";; esac; done; ";

        // `env -u A -u B sh -c '<line>'`, or the line untouched when there is
        // nothing to drop. sh -c rather than a bare prefix so a line of several
        // commands (`cd … || exit 1; claude`) is covered in full, and `env`
        // because it works whatever the user's default shell is.
        internal static string WrapPaneCommand(string line, IEnumerable<string> names)
        {
            var list = names.ToArray();
            if (list.Length == 0) return line;
            return "env " + string.Join(" ", list.Select(n => "-u " + n)) + " sh -c " + TerminalScripts.ShellQuote(line);
        }

        // The fixed set, for builders that run without a process in hand: every
        // exact name plus the variables this process itself carries.
        // The exact names go to `env -u`, the prefix families to PanePreamble.
        internal static string WrapPaneCommand(string line) =>
            WrapPaneCommand(PanePreamble + line, PaneRemovals(CurrentNames()).Union(KnownPaneNames).OrderBy(n => n, StringComparer.Ordinal));

        // Concrete names always dropped inside a pane, whatever this process
        // carries: the tmux *server* may hold them from whoever started it.
        // `env -u` of a name that is not set is a no-op.
        internal static readonly string[] KnownPaneNames =
        [
            "CLAUDECODE", "CLAUDE_PID", "CLAUDE_JOB_DIR", "CLAUDE_EFFORT",
            "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH",
            "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SESSION_KIND", "CLAUDE_CODE_SESSION_ATTENDED",
            "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
            "CLAUDE_CODE_BRIDGE_SESSION_ID",
            "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_CHILD_NOTES_DIR",
            "CLAUDE_CODE_LAUNCHED_BY_SESSION_IDS",
            "CLAUDE_PROJECT_UUID", "CLAUDE_CODE_SPEND_PROJECT", "CLAUDE_CODE_ATTRIBUTION_PROJECT",
        ];

        // The inverse of WrapPaneCommand, for callers that need the line itself.
        internal static string Unwrap(string wrapped)
        {
            const string marker = " sh -c '";
            if (!wrapped.StartsWith("env -u ", StringComparison.Ordinal)) return wrapped;
            var at = wrapped.IndexOf(marker, StringComparison.Ordinal);
            var line = wrapped[(at + marker.Length)..^1].Replace("'\\''", "'");
            return line.StartsWith(PanePreamble, StringComparison.Ordinal) ? line[PanePreamble.Length..] : line;
        }

        // `-u NAME` pairs for an `env` prefix in front of an app relaunch:
        // the known names, TMUX, and whatever this process carries.
        internal static string[] UnsetArgs() =>
            ExactNames.Union(KnownPaneNames).Union(Removals(CurrentNames()))
                .OrderBy(n => n, StringComparer.Ordinal).SelectMany(n => new[] { "-u", n }).ToArray();

        internal static string[] CurrentNames() =>
            Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => (string)k).ToArray();

        // Applies the scrub to a child's environment, in place.
        internal static void Scrub(IDictionary<string, string?> environment)
        {
            foreach (var name in Removals(environment.Keys.ToArray())) environment.Remove(name);
        }

        internal static ProcessStartInfo Scrubbed(ProcessStartInfo psi)
        {
            Scrub(psi.Environment);
            return psi;
        }

        // Drops the identity from this process, so nothing it launches later
        // can carry it. Returns the names removed, for the startup log.
        internal static string[] ScrubCurrentProcess()
        {
            var names = Removals(CurrentNames());
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            return names;
        }
    }
}
