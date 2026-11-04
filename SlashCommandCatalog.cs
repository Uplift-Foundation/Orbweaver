using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // What slash commands a local CLI session understands, so the chat panel's
    // Input box can offer the same "/" autocomplete the terminal itself would
    // show. Two kinds of source, merged:
    //
    //  * A fixed list of each CLI's own built-in commands. Neither CLI exposes
    //    these as a file or an API, so the only way to know them is to read
    //    the product's own docs — checked against code.claude.com/docs/en/commands
    //    and learn.chatgpt.com/docs/developer-commands, both as of 22 Aug 2026.
    //    It is a floor, not a mirror: both CLIs add commands between releases
    //    and this will drift behind them. That is an acceptable gap for an
    //    autocomplete aid — a command missing from the list still works when
    //    typed in full, it just isn't offered while you're typing it.
    //
    //  * Whatever the CLI would also discover from disk: Claude Code's
    //    ".claude/commands/" and ".claude/skills/", Codex's ".codex/prompts/".
    //    These are read live and can never go stale the way the built-in list
    //    can — which is the whole reason to bother with them at all.
    internal static class SlashCommandCatalog
    {
        public static IReadOnlyList<SlashCommand> For(SessionSource source, string cwd) =>
            source == SessionSource.Codex ? ForCodex()
            : source == SessionSource.Grok ? ForGrok()
            : ForClaudeCode(cwd);

        // Each CLI's fixed list alone, with no disk read at all: what a caller
        // that must never touch the filesystem offers until a scan has answered
        // (SlashCatalogueCache, CB-306).
        internal static IReadOnlyList<SlashCommand> BuiltinsFor(SessionSource source) =>
            source == SessionSource.Codex ? CodexBuiltins
            : source == SessionSource.Grok ? GrokBuiltins
            : ClaudeCodeBuiltins;


        // --- Claude Code ---

        private static IReadOnlyList<SlashCommand> ForClaudeCode(string cwd)
        {
            var byName = new Dictionary<string, SlashCommand>(StringComparer.OrdinalIgnoreCase);

            void Merge(IEnumerable<SlashCommand> commands)
            {
                foreach (var command in commands) byName[command.Name] = command;
            }

            Merge(ClaudeCodeBuiltins);

            // Lowest to highest precedence. Personal beats project, and a
            // skill beats a command of the same name regardless of level —
            // both rules Claude Code documents for resolving a name that
            // exists twice, so a skill merged last wins the way it would in
            // the terminal itself.
            if (!string.IsNullOrEmpty(cwd)) Merge(CustomCommands(Path.Combine(cwd, ".claude", "commands")));
            Merge(CustomCommands(Path.Combine(HomeDir, ".claude", "commands")));
            if (!string.IsNullOrEmpty(cwd)) Merge(Skills(ProjectSkillRoots(cwd)));
            Merge(Skills(new[] { Path.Combine(HomeDir, ".claude", "skills") }));

            return byName.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ".claude/skills/" in cwd and every parent directory up to the
        // filesystem root — the same reach Claude Code itself documents for
        // project skills ("in the directory where you start Claude Code and
        // in every parent directory up to the repository root"). Skills
        // nested below cwd are deliberately not included: Claude Code only
        // loads those once it has actually read or edited a file in that
        // subdirectory during the session, which this has no way to know
        // about from outside it.
        private static IEnumerable<string> ProjectSkillRoots(string cwd)
        {
            var dir = new DirectoryInfo(cwd);
            while (dir is not null)
            {
                yield return Path.Combine(dir.FullName, ".claude", "skills");
                dir = dir.Parent;
            }
        }

        // Every "*.md" file under root, named for its path relative to root
        // with each subdirectory joined by ":" — "frontend/component.md"
        // becomes "/frontend:component". A root that doesn't exist or can't
        // be read yields nothing rather than throwing: a session with no
        // custom commands is the ordinary case, not an error.
        private static IEnumerable<SlashCommand> CustomCommands(string root)
        {
            foreach (var file in SafeFiles(root, "*.md", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var withoutExt = relative[..^3];
                var name = "/" + withoutExt
                    .Replace(Path.DirectorySeparatorChar, ':')
                    .Replace(Path.AltDirectorySeparatorChar, ':');

                yield return new SlashCommand(name, DescriptionOf(file));
            }
        }

        // Each immediate subdirectory of a skills root that holds a SKILL.md
        // is one command, named for the directory rather than the file —
        // unlike a command file, a skill's file name (SKILL.md) never varies,
        // so the directory is the only thing that could distinguish it.
        private static IEnumerable<SlashCommand> Skills(IEnumerable<string> roots)
        {
            foreach (var root in roots)
            {
                foreach (var dir in SafeDirectories(root))
                {
                    var skillFile = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;

                    yield return new SlashCommand("/" + Path.GetFileName(dir), DescriptionOf(skillFile));
                }
            }
        }

        // --- Codex ---

        private static IReadOnlyList<SlashCommand> ForCodex() => ForCodex(HomeDir);

        // internal, and taking its home rather than reading it, so the prompts
        // directory can be a fixture. The alternative is a test that passes or
        // fails depending on whether the person running it happens to keep
        // prompts in ~/.codex — which is not a test of this code at all.
        internal static IReadOnlyList<SlashCommand> ForCodex(string home)
        {
            var byName = new Dictionary<string, SlashCommand>(StringComparer.OrdinalIgnoreCase);

            foreach (var command in CodexBuiltins) byName[command.Name] = command;

            // Top-level files only, no project-level directory: Codex's own
            // docs are explicit that "Codex scans only the top-level Markdown
            // files" under ~/.codex/prompts, and that a prompt is always
            // invoked as "/prompts:<name>" rather than a bare "/<name>".
            foreach (var file in SafeFiles(Path.Combine(home, ".codex", "prompts"), "*.md", SearchOption.TopDirectoryOnly))
            {
                var name = "/prompts:" + Path.GetFileNameWithoutExtension(file);
                byName[name] = new SlashCommand(name, DescriptionOf(file));
            }

            return byName.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // --- shared ---

        private static string HomeDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Materialized eagerly rather than left lazy: a lazy EnumerateFiles
        // can still throw partway through the first MoveNext, which would
        // have to be caught at every call site instead of once here.
        // Excluded from coverage: exists to be the try/catch. The Exists guard
        // above means both arms only fire on a real IO fault partway through an
        // enumeration — the case the comment above this pair describes, and not
        // something a test can stage.
        [ExcludeFromCodeCoverage]
        private static List<string> SafeFiles(string root, string pattern, SearchOption option)
        {
            if (!Directory.Exists(root)) return new List<string>();

            try { return Directory.EnumerateFiles(root, pattern, option).ToList(); }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        // Excluded from coverage: exists to be the try/catch. The Exists guard
        // above means both arms only fire on a real IO fault partway through an
        // enumeration — the case the comment above this pair describes, and not
        // something a test can stage.
        [ExcludeFromCodeCoverage]
        private static List<string> SafeDirectories(string root)
        {
            if (!Directory.Exists(root)) return new List<string>();

            try { return Directory.EnumerateDirectories(root).ToList(); }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        // A command's description, the way both CLIs show one in their own
        // popup: the "description" field of a YAML frontmatter block if the
        // file opens with one, else its first non-blank line.
        private static readonly Regex FrontmatterDescription =
            new(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static string DescriptionOf(string path) => DescriptionIn(SafeRead(path));

        // Excluded from coverage: exists to be the try/catch. Both arms are for a
        // file that passed Exists and then failed mid-read — a disk error, or a
        // path that turned out to be a directory — neither of which is arrangeable
        // from a test in a way that is the same on both platforms.
        [ExcludeFromCodeCoverage]
        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        // The parse, separated from the read so it can be exercised with strings
        // rather than fixture files — which is the whole of what is interesting
        // here, since both CLIs let a command describe itself two different ways.
        internal static string DescriptionIn(string text)
        {
            if (text.StartsWith("---", StringComparison.Ordinal))
            {
                var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end > 0)
                {
                    var match = FrontmatterDescription.Match(text[3..end]);
                    if (match.Success) return Truncate(match.Groups[1].Value.Trim().Trim('"'));
                }
            }

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && trimmed != "---") return Truncate(trimmed);
            }

            return "";
        }

        // A skill's description is written for the model and can run to a
        // full paragraph; this is a 340px-wide popup row, not a chat bubble.
        private static string Truncate(string text) =>
            text.Length <= 80 ? text : text[..77] + "...";

        // Checked against code.claude.com/docs/en/commands, 22 Aug 2026.
        internal static readonly SlashCommand[] ClaudeCodeBuiltins =
        {
            new("/add-dir", "Add a working directory for file access"),
            new("/advisor", "Enable or disable the advisor tool for a second opinion"),
            new("/agents", "Manage subagent configurations"),
            new("/artifacts", "List, attach, or open artifacts"),
            new("/auto-mode-setup", "Draft auto mode environment entries for this project"),
            new("/autocompact", "Set the auto-compact context window"),
            new("/autofix-pr", "Watch this branch's PR and push fixes automatically"),
            new("/background", "Detach this session to run as a background agent"),
            new("/batch", "Orchestrate large-scale changes across the codebase in parallel"),
            new("/branch", "Branch the conversation to try a different direction"),
            new("/btw", "Ask a side question without adding to the conversation"),
            new("/bug", "Report a bug or share this conversation"),
            new("/cd", "Move this session to a new working directory"),
            new("/chrome", "Configure Claude in Chrome settings"),
            new("/claude-api", "Load Claude API reference material and run upgrades"),
            new("/clear", "Start a new conversation with empty context"),
            new("/code-review", "Review a diff, PR, branch, or path for bugs and cleanup"),
            new("/color", "Set the prompt bar color for this session"),
            new("/compact", "Free up context by summarizing the conversation"),
            new("/config", "Open settings, or set one directly"),
            new("/context", "Visualize current context usage"),
            new("/copy", "Copy the last assistant response to the clipboard"),
            new("/cost", "Alias for /usage"),
            new("/dataviz", "Design guidance for charts and dashboards"),
            new("/debug", "Enable debug logging and troubleshoot issues"),
            new("/deep-research", "Fan out web searches and synthesize a cited report"),
            new("/design-login", "Authorize design-system access for /design-sync"),
            new("/design-sync", "Upload this repo's design system to Claude Design"),
            new("/desktop", "Continue this session in the Claude Code Desktop app"),
            new("/diff", "Open an interactive diff viewer for uncommitted changes"),
            new("/doctor", "Diagnose and fix installation issues"),
            new("/effort", "Set the reasoning effort level"),
            new("/exit", "Exit the CLI"),
            new("/export", "Export the conversation as plain text"),
            new("/fast", "Toggle fast mode"),
            new("/feedback", "Send product feedback about Claude Code"),
            new("/fewer-permission-prompts", "Add an allowlist from your transcripts to cut prompts"),
            new("/focus", "Toggle the focus view"),
            new("/fork", "Copy the conversation into a new background session"),
            new("/goal", "Keep working across turns until a condition is met"),
            new("/heapdump", "Write a heap snapshot for memory diagnosis"),
            new("/help", "Show help and available commands"),
            new("/hooks", "View hook configurations"),
            new("/ide", "Manage IDE integrations"),
            new("/import", "Bring configuration in from another coding agent"),
            new("/init", "Create a CLAUDE.md for this project"),
            new("/insights", "Analyze your recent sessions in an HTML report"),
            new("/install-github-app", "Install the Claude GitHub App"),
            new("/install-slack-app", "Install the Claude Slack app"),
            new("/keybindings", "Open your keyboard shortcuts file"),
            new("/list-agents", "List sessions and subagents you can message"),
            new("/login", "Sign in"),
            new("/logout", "Sign out"),
            new("/loop", "Run a prompt repeatedly while the session stays open"),
            new("/mcp", "Manage MCP server connections"),
            new("/memory", "Edit CLAUDE.md and manage auto memory"),
            new("/mobile", "Show a QR code for the Claude mobile app"),
            new("/model", "Switch the AI model"),
            new("/passes", "Share a free week of Claude Code"),
            new("/permissions", "Manage tool permission rules"),
            new("/plan", "Enter plan mode"),
            new("/plugin", "Manage Claude Code plugins"),
            new("/powerup", "Interactive lessons on Claude Code features"),
            new("/privacy-settings", "View and update privacy settings"),
            new("/radio", "Open Claude FM lo-fi radio"),
            new("/recap", "Summarize the current session"),
            new("/remote-control", "Continue this session from another device"),
            new("/rename", "Rename the current session"),
            new("/resume", "Return to an earlier conversation"),
            new("/review", "Alias for /code-review"),
            new("/rewind", "Roll code and conversation back to a checkpoint"),
            new("/security-review", "Check the diff for security vulnerabilities"),
            new("/simplify", "Suggest code simplifications"),
            new("/skills", "List, enable, or disable installed skills"),
            new("/status", "Show model, effort, and session stats"),
            new("/subtask", "Hand a side task to a subagent"),
            new("/tasks", "List background work and subagents"),
            new("/teleport", "Pull a web session into this terminal"),
            new("/theme", "Set the color theme"),
            new("/upgrade", "Upgrade Claude Code"),
            new("/usage", "Show API usage and costs"),
            new("/verify", "Run tests and checks to verify correctness"),
            new("/vim", "Toggle vim mode"),
            new("/web", "Continue this session on claude.ai/code"),
        };

        // Checked against learn.chatgpt.com/docs/developer-commands, 22 Aug 2026.
        private static readonly SlashCommand[] CodexBuiltins =
        {
            new("/permissions", "Set what Codex can do without asking first"),
            new("/ide", "Include open files and IDE context"),
            new("/keymap", "Remap TUI keyboard shortcuts"),
            new("/vim", "Toggle vim mode for the composer"),
            new("/agent", "Switch the active agent thread"),
            new("/subagents", "Switch the active agent thread"),
            new("/apps", "Browse and insert connector apps"),
            new("/plugins", "Browse installed and discoverable plugins"),
            new("/hooks", "View and manage lifecycle hooks"),
            new("/clear", "Clear the terminal and start a fresh chat"),
            new("/rename", "Rename the current chat"),
            new("/archive", "Archive the current session and exit"),
            new("/delete", "Permanently delete the current session and exit"),
            new("/compact", "Summarize the visible chat to free tokens"),
            new("/copy", "Copy the latest completed output"),
            new("/diff", "Show the git diff, including untracked files"),
            new("/exit", "Exit the CLI"),
            new("/quit", "Exit the CLI (same as /exit)"),
            new("/experimental", "Toggle experimental features"),
            new("/approve", "Approve one retry of a recent auto review denial"),
            new("/memories", "Configure memory use and generation"),
        };

        // Checked against ~/.grok/docs/user-guide/04-slash-commands.md as of
        // 31 Aug 2026. A floor, not a mirror: Grok adds commands between
        // releases the same way Claude Code does.
        private static IReadOnlyList<SlashCommand> ForGrok() => ForGrok(HomeDir);

        internal static IReadOnlyList<SlashCommand> ForGrok(string home)
        {
            var byName = new Dictionary<string, SlashCommand>(StringComparer.OrdinalIgnoreCase);

            foreach (var command in GrokBuiltins) byName[command.Name] = command;

            foreach (var dir in SafeDirectories(Path.Combine(home, ".grok", "skills")))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                byName["/" + Path.GetFileName(dir)] =
                    new SlashCommand("/" + Path.GetFileName(dir), DescriptionOf(skillFile));
            }

            return byName.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static readonly SlashCommand[] GrokBuiltins =
        {
            new("/new", "Start a new session"),
            new("/clear", "Start a new session (alias of /new)"),
            new("/resume", "Open the session picker"),
            new("/dashboard", "Open the agent dashboard"),
            new("/compact", "Compress conversation history"),
            new("/context", "Show context-window use"),
            new("/session-info", "Show session details"),
            new("/status", "Alias for /session-info"),
            new("/fork", "Branch this session into a peer agent"),
            new("/rewind", "Rewind to an earlier turn"),
            new("/rename", "Rename this session"),
            new("/title", "Alias for /rename"),
            new("/usage", "Show credit usage"),
            new("/cost", "Alias for /usage"),
            new("/model", "Switch models"),
            new("/effort", "Set reasoning effort"),
            new("/hooks", "View and manage hooks"),
            new("/skills", "List or inject skills"),
            new("/plugins", "Manage plugins"),
            new("/theme", "Switch the TUI theme"),
            new("/settings", "Open settings"),
            new("/copy", "Copy the last response"),
            new("/export", "Export the conversation"),
            new("/quit", "Exit Grok"),
            new("/exit", "Exit Grok (alias of /quit)"),
            new("/delete", "Delete this session"),
            new("/always-approve", "Toggle always-approve mode"),
        };
    }
}
