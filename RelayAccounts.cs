using System.Text;

namespace Orbweaver
{
    // The accounts a phone may start a session under (additive to frames-v4, see contracts/frames-v8.md in Orbweaver-Mobile):
    // the desktop's own New Chat picker, per CLI. A phone with no choice gets the account
    // last used in that dialog, as the dialog itself would open on.
    internal static class RelayAccounts
    {
        // "default" names the CLI's own default directory; any other id is the
        // profile directory exactly as the desktop's settings spell it.
        internal const string Default = "default";

        internal sealed record Account(string Cli, string Id, string Label);

        internal static IReadOnlyList<Account> From(
            Func<NewChatCli, IReadOnlyList<NewChatAccounts.Choice>> choices)
        {
            var all = new List<Account>();
            foreach (var cli in new[] { NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok })
                foreach (var c in choices(cli))
                    all.Add(new Account(CliName(cli), c.ProfileDir ?? Default, c.Label));
            return all;
        }

        // The wire name of a CLI, as a launch's cli field spells it.
        internal static string CliName(NewChatCli cli) => cli switch { NewChatCli.Codex => "codex", NewChatCli.Grok => "grok", _ => "claude" };

        // The desktop's own New Chat account picker, per CLI.
        internal static IReadOnlyList<NewChatAccounts.Choice> Choices(NewChatCli cli) =>
            NewChatAccounts.Choices(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cli switch
            {
                NewChatCli.Codex => OrbweaverSettings.CodexHomes,
                NewChatCli.Grok => OrbweaverSettings.GrokHomes,
                _ => OrbweaverSettings.ClaudeCodeProfileDirs,
            });

        // The desktop's own summariser, asked for the few sentences it would speak when a turn finishes.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static Task<string?> Summarize(SessionStatus status, string reply) =>
            SpeechSummary.SummarizeOrSayWhyAsync(reply, SpeechSummaryKind.TurnFinished,
                SpeechSummary.AccountDirFor(status.TranscriptPath)).ContinueWith(t => (string?)t.Result);

        // summary (desktop to phone, class state): t, session_id, text, at (Unix seconds, when it was
        // made, so a phone can refuse to read out a stale one).
        internal static string SummaryFrame(string sessionId, string text, long at) =>
            "{\"t\":\"summary\",\"session_id\":" + Str(sessionId) + ",\"text\":" + Str(text) + ",\"at\":" + at.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

        private static string Str(string s) => "\"" + AnswerSigning.Escape(s) + "\"";

        // t, accounts[{cli, id, label}] in that order; nothing to offer is still a frame,
        // so a phone drops what it had.
        internal static string Frame(IReadOnlyList<Account> accounts) =>
            "{\"t\":\"accounts\",\"accounts\":[" + string.Join(",", accounts.Select(a =>
                "{\"cli\":" + Str(a.Cli) + ",\"id\":" + Str(a.Id) + ",\"label\":" + Str(a.Label) + "}")) + "]}";

        // The profile directory a launch runs under: what the phone chose, else the account
        // the user designated for phone launches, else the CLI's default. Null is
        // "the default directory". Returns false for an account the desktop does not list.
        internal static bool TryResolve(NewChatCli cli, string asked, IReadOnlyList<NewChatAccounts.Choice> choices,
            string? designated, string? lastUsed, out string? profileDir)
        {
            profileDir = null;
            if (asked.Length == 0) asked = designated ?? lastUsed ?? Default;
            if (asked == Default) return true;
            var hit = choices.FirstOrDefault(c => c.ProfileDir is not null && string.Equals(c.ProfileDir, asked, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return false;
            profileDir = hit.ProfileDir;
            return true;
        }
    }
}
