using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // The phone's launch signature (frames-v4.json launch): ECDSA P-256 over
    // "orbweaver-launch-v1" followed by a body the desktop rebuilds from the
    // frame's own fields, so any other byte layout fails as bad_signature. Its
    // own domain prefix means a launch can never verify as an answer, or the
    // reverse.
    internal static class LaunchSigning
    {
        internal const string Domain = "orbweaver-launch-v1";

        // account is signed only when present (between model and parent_session_id), so a
        // request that does not name one is byte-for-byte the v4 body the vectors pin.
        internal static string Body(string cwd, string cli, string persona, string model, string parentSessionId,
            string firstMessage, string nonce, long expiresAt, string account = "") =>
            "{\"cwd\":\"" + AnswerSigning.Escape(cwd) + "\",\"cli\":\"" + AnswerSigning.Escape(cli)
            + "\",\"persona\":\"" + AnswerSigning.Escape(persona) + "\",\"model\":\"" + AnswerSigning.Escape(model)
            + (account.Length > 0 ? "\",\"account\":\"" + AnswerSigning.Escape(account) : "")
            + "\",\"parent_session_id\":\"" + AnswerSigning.Escape(parentSessionId)
            + "\",\"first_message_sha256\":\"" + RelayCrypto.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(firstMessage)))
            + "\",\"nonce\":\"" + AnswerSigning.Escape(nonce) + "\",\"expires_at\":"
            + expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

        internal static bool Verify(byte[] phoneIdPublic, string body, string? sigBase64Url) =>
            RelayCrypto.Verify(phoneIdPublic, [.. Encoding.UTF8.GetBytes(Domain), .. Encoding.UTF8.GetBytes(body)],
                RelayCrypto.FromBase64Url(sigBase64Url));
    }

    // What a launch has been checked down to.
    internal sealed record LaunchPlan(NewChatCli Cli, string Cwd, string Model, string? ParentSessionId, string FirstMessage, string? ProfileDir = null);

    // The command a launch starts, pure: no process is spawned here, so every
    // quoting and flag decision has a case.
    internal static class LaunchCommand
    {
        internal static readonly Regex PercentName = new("%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.Compiled);
        internal static readonly Regex ModelShape = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.Compiled);

        internal static string CliName(NewChatCli cli) => cli switch { NewChatCli.Codex => "codex", NewChatCli.Grok => "grok", _ => "claude" };

        internal static NewChatCli? ParseCli(string cli) => cli switch
        {
            "claude" => NewChatCli.ClaudeCode,
            "codex" => NewChatCli.Codex,
            "grok" => NewChatCli.Grok,
            _ => null,
        };

        // claude --model M --parent-session-id P (the message is typed, once the
        // session is at its prompt); codex -m M -- MESSAGE and grok -m M -- MESSAGE
        // (the message is the CLI's positional prompt, after `--` so a message
        // starting with a dash or naming a subcommand is never a flag).
        internal static List<string> Words(LaunchPlan p)
        {
            var words = new List<string>();
            if (p.Cli == NewChatCli.ClaudeCode)
            {
                if (p.Model.Length > 0) { words.Add("--model"); words.Add(p.Model); }
                if (p.ParentSessionId is { Length: > 0 } parent) { words.Add("--parent-session-id"); words.Add(parent); }
                return words;
            }
            if (p.Model.Length > 0) { words.Add("-m"); words.Add(p.Model); }
            if (p.FirstMessage.Length > 0) { words.Add("--"); words.Add(p.FirstMessage); }
            return words;
        }

        // The tmux window's command on a Mac: the CLI exec'd with its words,
        // each shell-quoted.
        internal static string Shell(LaunchPlan p, string binary, string? configDir) =>
            string.Join(" ", [NewChatCommand.ExecLine(p.Cli, binary, configDir), .. Words(p).Select(TerminalScripts.ShellQuote)]);

        // Windows Terminal first, the terminal the user lives in: a new tab in it,
        // `wt.exe -d <folder> cmd.exe /k <cli> <words>` for claude and
        // `wt.exe -d <folder> <cli> <words>` for codex and grok, whose argv is
        // built word by word because cmd would re-parse & | > % in a message.
        // conhost.exe, named outright, is the fallback when wt.exe is not there:
        // a bare cmd.exe is handed to Windows Terminal by the OS default-terminal
        // setting and then owns no console window the hook can see. Null when a
        // codex/grok that is a script shim has a message to carry (no argv to
        // put it in safely).
        internal static ProcessStartInfo? Windows(LaunchPlan p, string binary, string? configDir, bool windowsTerminal = true)
        {
            var start = new ProcessStartInfo(windowsTerminal ? "wt.exe" : "conhost.exe") { UseShellExecute = false, WorkingDirectory = p.Cwd };
            if (windowsTerminal) { start.ArgumentList.Add("-d"); start.ArgumentList.Add(p.Cwd); }
            if (p.Cli == NewChatCli.ClaudeCode)
            {
                start.ArgumentList.Add("cmd.exe");
                start.ArgumentList.Add("/k");
            }
            else if (p.FirstMessage.Length > 0 && !binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            start.ArgumentList.Add(binary);
            foreach (var w in Words(p)) start.ArgumentList.Add(w);
            if (configDir is not null) start.Environment[NewChatAccountHome.For(p.Cli).EnvVar] = configDir;
            return start;
        }
    }

    // CB-316 launch: a signed request to start a CLI on a folder, and the two
    // replies it earns (launched, then typed or why not).
    internal sealed class PhoneLaunch
    {
        internal const int LaunchTtlSeconds = 120;
        internal const int MaxFutureSeconds = 300;
        internal static readonly TimeSpan CheckInWait = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan PromptWait = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

        internal sealed record Seams(
            Func<DateTimeOffset> Now,
            Func<TimeSpan, Task> Delay,
            Func<string, bool> DirectoryExists,
            // The CLI's binary, located fresh, or null.
            Func<NewChatCli, string?> Locate,
            // A session in this desktop's roster, for a parent.
            Func<string, PhoneService.Session?> Find,
            // Starts the terminal. Null code is started; else no_terminal or
            // launch_failed, with the desk's own sentence.
            Func<LaunchPlan, string, (string? Code, string? Why)> Start,
            // A local session of this cli in this cwd that appeared after the
            // launch began.
            Func<NewChatCli, string, DateTimeOffset, PhoneService.Session?> FindNew,
            // The guarded typer of PhoneService (gate, settle, keys).
            Func<PhoneService.Session, string, Task<(bool Typed, string? Refusal)>> Type,
            Func<string, string, string?, string, bool> Send,
            Func<bool> IsWindows,
            Action<string> Log,
            // The desktop's account choices for a CLI, and the one last used in its New Chat
            // dialog. Null seams mean no accounts: a request naming one is unknown_account.
            Func<NewChatCli, IReadOnlyList<NewChatAccounts.Choice>>? Choices = null,
            Func<NewChatCli, string?>? LastUsed = null,
            Func<NewChatCli, string?>? Designated = null);

        private readonly Seams _s;
        private readonly NonceWindow _nonces;

        internal PhoneLaunch(Seams seams, NonceWindow nonces) { _s = seams; _nonces = nonces; }

        internal async Task HandleAsync(string phone, string reference, byte[] phoneIdPublic, JsonElement f)
        {
            string? S(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("cwd") is not { } cwd || S("cli") is not { } cliName || S("persona") is not { } persona || S("model") is not { } model
                || S("first_message") is not { } message || S("nonce") is not { } nonce || S("sig") is not { } sig
                || !f.TryGetProperty("expires_at", out var e) || e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var expires)
                || (f.TryGetProperty("parent_session_id", out var p) && p.ValueKind != JsonValueKind.String))
            { Err(phone, reference, "bad_frame"); return; }
            var parent = S("parent_session_id") ?? "";
            if (f.TryGetProperty("account", out var a) && a.ValueKind != JsonValueKind.String) { Err(phone, reference, "bad_frame"); return; }
            var account = S("account") ?? "";

            var body = LaunchSigning.Body(cwd, cliName, persona, model, parent, message, nonce, expires, account);
            if (!LaunchSigning.Verify(phoneIdPublic, body, sig)) { Err(phone, reference, "bad_signature"); return; }

            var now = _s.Now();
            var unix = now.ToUnixTimeSeconds();
            if (unix >= expires || expires > unix + MaxFutureSeconds) { Err(phone, reference, "expired"); return; }
            if (_nonces.Seen(phone, nonce, now)) { Err(phone, reference, "replayed"); return; }
            _nonces.Add(phone, nonce, now);

            if (LaunchCommand.ParseCli(cliName) is not { } cli || _s.Locate(cli) is not { } binary) { Err(phone, reference, "unknown_cli"); return; }
            if (!IsAbsolute(cwd) || !_s.DirectoryExists(cwd)) { Err(phone, reference, "unknown_cwd"); return; }
            if (model.Length > 0 && !LaunchCommand.ModelShape.IsMatch(model)) { Err(phone, reference, "bad_model"); return; }
            string? profileDir = null;
            if (!PhoneAccounts.TryResolve(cli, account, _s.Choices?.Invoke(cli) ?? [], _s.Designated?.Invoke(cli), _s.LastUsed?.Invoke(cli), out profileDir))
            { Err(phone, reference, "unknown_account"); return; }
            if (persona.Length > 0) { Err(phone, reference, "unsupported_persona"); return; }
            if (parent.Length > 0)
            {
                if (_s.Find(parent) is null) { Err(phone, reference, "unknown_parent"); return; }
                if (cli != NewChatCli.ClaudeCode) { Err(phone, reference, "unsupported_parent"); return; }
            }
            if (!PhoneService.ValidTypedText(message, allowEmpty: true)
                || (_s.IsWindows() && cli != NewChatCli.ClaudeCode && LaunchCommand.PercentName.IsMatch(message)))
            { Err(phone, reference, "bad_input"); return; }

            var plan = new LaunchPlan(cli, cwd, model, parent.Length > 0 ? parent : null, message, profileDir);
            var began = now;
            var (code, why) = _s.Start(plan, binary);
            if (code is not null) { Err(phone, reference, code, why: why); return; }

            var session = await WaitForAsync(() => _s.FindNew(cli, cwd, began), CheckInWait).ConfigureAwait(false);
            if (session is null) { Err(phone, reference, "no_session"); return; }
            Send(phone, "action", session.Id, PhoneFrames.Ok(reference, "launched", session.Id));
            if (message.Length == 0) return;

            // codex and grok took the message in argv: checking in is delivery.
            if (cli != NewChatCli.ClaudeCode) { Send(phone, "action", session.Id, PhoneFrames.Ok(reference, "typed", session.Id)); return; }

            // claude: wait for its prompt (a first-run trust dialog is the user's
            // to clear at the desk), then type through the same guarded route as input.
            var deadline = _s.Now() + PromptWait;
            string? refusal = null;
            while (true)
            {
                var live = _s.Find(session.Id) ?? session;
                (bool Typed, string? Refusal) result;
                try { result = await _s.Type(live, message).ConfigureAwait(false); }
                catch (Exception ex) { _s.Log($"launch typer threw {ex.GetType().Name}"); result = (false, null); }
                if (result.Typed) { Send(phone, "action", session.Id, PhoneFrames.Ok(reference, "typed", session.Id)); return; }
                if (result.Refusal is null)
                {
                    Send(phone, "action", session.Id, PhoneFrames.Err(reference, "typing_failed", session.Id, "the terminal refused the text"));
                    return;
                }
                refusal = result.Refusal;
                if (_s.Now() >= deadline) break;
                await _s.Delay(Poll).ConfigureAwait(false);
            }
            Send(phone, "action", session.Id, PhoneFrames.Err(reference, "not_at_prompt", session.Id, refusal));
        }

        private async Task<PhoneService.Session?> WaitForAsync(Func<PhoneService.Session?> look, TimeSpan limit)
        {
            var deadline = _s.Now() + limit;
            while (true)
            {
                if (look() is { } found) return found;
                if (_s.Now() >= deadline) return null;
                await _s.Delay(Poll).ConfigureAwait(false);
            }
        }

        // A Mac path starts with /; a Windows one is a drive letter and a separator.
        internal static bool IsAbsolute(string path) =>
            path.StartsWith('/') || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

        private void Err(string phone, string reference, string code, string? why = null) =>
            Send(phone, "action", null, PhoneFrames.Err(reference, code, null, why));

        private void Send(string phone, string cls, string? ckSession, string plaintext) => _s.Send(phone, cls, ckSession, plaintext);
    }
}
