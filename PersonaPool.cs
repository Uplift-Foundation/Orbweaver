using System.Text.RegularExpressions;

namespace Orbweaver
{
    // Where a user's personas live when they are not in the project: the shared
    // pool, and the names an agent team member can be looked up under.
    //
    // A team member is its own `claude` process named by `--agent-name`, and
    // LocalPersona finds its persona by that name. Until this existed the only
    // places it looked were `profiles/<name>/<name>.md` in the session's working
    // directory and the directories above it. A pool kept anywhere else — the
    // account's own config directory, or one folder shared by every project —
    // was invisible to it, so a team of personas the user had made drew every
    // member in the main session's face (seen on a real WSL team: four teammate
    // orbs, one portrait). The lookup order is now project, then each account's
    // config directory, then the shared pool; the first place that names the
    // persona wins and the rest are not consulted for its fields.
    //
    // The shared pool is one directory the user chooses, because the right place
    // is a fact about their machines and not about a project. `ORBWEAVER_PERSONAS`
    // names it for anything started from a shell; `personaPoolDir` in
    // settings.json names it for the tray app, which is launched by launchd or
    // the Start menu and never sees a shell's environment. The variable wins when
    // both are set, the same precedence ORBWEAVER_SETTINGS_DIR has over where
    // settings live. With neither, it is `~/.orbweaver`.
    internal static class PersonaPool
    {
        internal const string EnvironmentVariable = BrandEnv.Prefix + BrandEnv.Personas;

        // The folder a project can keep its own personas, roles and teams in,
        // and the default name of the shared pool under the home directory.
        internal const string FolderName = ".orbweaver";

        // The pool's root, or null when none can be named (no variable, no
        // setting and no home directory to put the default in).
        //
        // `~` is expanded because the value is typed by a person into a shell
        // profile or a JSON file, where it is what they write, and a literal
        // `~` directory is never what they meant. A relative path is left
        // alone: it would resolve against whatever the process's working
        // directory happens to be, which is a different answer for every
        // caller, so it is refused rather than guessed at.
        internal static string? SharedRoot(string? environmentValue, string? settingValue, string? home)
        {
            var chosen = !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue
                : !string.IsNullOrWhiteSpace(settingValue) ? settingValue
                : null;

            if (chosen is null)
                return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, FolderName);

            chosen = chosen.Trim();
            if (chosen == "~" || chosen.StartsWith("~/", StringComparison.Ordinal)
                              || chosen.StartsWith("~\\", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(home)) return null;
                chosen = Path.Combine(home, chosen.Length > 2 ? chosen[2..] : "");
            }

            return Path.IsPathRooted(chosen) ? chosen : null;
        }

        // The pool as the app runs it: the environment, the setting, the home.
        internal static string? SharedRoot() => SharedRoot(
            BrandEnv.Get(BrandEnv.Personas),
            OrbweaverSettings.PersonaPoolDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // The second instance of a persona on a live team is named
        // `<persona>_2`, the third `<persona>_3`, because AgentRoster refuses to
        // match two live sessions that share a name — the same persona on two
        // teams at once would otherwise have no live view at all. The persona
        // is still the one it is an instance of, so the portrait and voice are
        // looked up under the name before the suffix too.
        private static readonly Regex InstanceSuffix = new(@"^(?<base>.+)_(?<n>[0-9]+)$", RegexOptions.Compiled);

        // The names a team member is looked up under, most specific first: its
        // own full name, then the persona it is an instance of. A persona whose
        // real name happens to end in `_<digits>` therefore still finds its own
        // file, and an instance finds its persona's when it has none of its own.
        internal static IReadOnlyList<string> NamesFor(string agentName)
        {
            var match = InstanceSuffix.Match(agentName);
            return match.Success ? new[] { agentName, match.Groups["base"].Value } : new[] { agentName };
        }
    }
}
