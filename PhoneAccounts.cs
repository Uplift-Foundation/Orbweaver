using System.Text;

namespace Orbweaver
{
    // The accounts a phone may start a session under (CB-316, additive to frames-v4):
    // the desktop's own New Chat picker, per CLI. A phone with no choice gets the account
    // last used in that dialog, as the dialog itself would open on.
    internal static class PhoneAccounts
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
                    all.Add(new Account(LaunchCommand.CliName(cli), c.ProfileDir ?? Default, c.Label));
            return all;
        }

        private static string Str(string s) => "\"" + AnswerSigning.Escape(s) + "\"";

        // t, accounts[{cli, id, label}] in that order; nothing to offer is still a frame,
        // so a phone drops what it had.
        internal static string Frame(IReadOnlyList<Account> accounts) =>
            "{\"t\":\"accounts\",\"accounts\":[" + string.Join(",", accounts.Select(a =>
                "{\"cli\":" + Str(a.Cli) + ",\"id\":" + Str(a.Id) + ",\"label\":" + Str(a.Label) + "}")) + "]}";

        // The profile directory a launch runs under: what the phone chose, else the last
        // account used in the desktop's New Chat dialog, else the CLI's default. Null is
        // "the default directory". Returns false for an account the desktop does not list.
        internal static bool TryResolve(NewChatCli cli, string asked, IReadOnlyList<NewChatAccounts.Choice> choices,
            string? lastUsed, out string? profileDir)
        {
            profileDir = null;
            if (asked.Length == 0) asked = lastUsed ?? Default;
            if (asked == Default) return true;
            var hit = choices.FirstOrDefault(c => c.ProfileDir is not null && string.Equals(c.ProfileDir, asked, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return false;
            profileDir = hit.ProfileDir;
            return true;
        }
    }
}
