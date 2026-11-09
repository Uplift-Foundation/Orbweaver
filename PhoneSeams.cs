using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Orbweaver
{
    // The real world behind PhoneService: the scan's sessions, transcript
    // files on disk, the typing gate and the terminal. Excluded from coverage
    // like the other live seams — a test must never type into a terminal or
    // read the developer's transcripts; PhoneService's suite drives fakes.
    [ExcludeFromCodeCoverage]
    internal sealed class PhoneSeams
    {
        private readonly object _gate = new();
        private Dictionary<string, PhoneService.Session> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Length, DateTime Written, List<PhoneFrames.Line> Lines)> _parsed = new();

        // Fed by every scan: the book a watch or an input is resolved against.
        internal void OnScan(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var next = new Dictionary<string, PhoneService.Session>(StringComparer.Ordinal);
            foreach (var (id, status) in snapshot)
                if (status.IsLocalCli) next[id] = new PhoneService.Session(id, status, RelayRoster.MapState(status.State));
            lock (_gate) _sessions = next;
        }

        internal PhoneService.Session? Find(string id)
        {
            lock (_gate) return _sessions.GetValueOrDefault(id);
        }

        // The whole transcript, parsed by the CLI's own parser into the rows
        // the desktop's chat panel shows. Re-read only when the file's length
        // or time changed, so a 250 ms tick over a quiet file costs one stat.
        internal IReadOnlyList<PhoneFrames.Line>? Load(PhoneService.Session session)
        {
            var path = session.Status.TranscriptPath;
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                lock (_gate)
                {
                    if (_parsed.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
                        return hit.Lines;
                }
                var lines = new List<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                    while (reader.ReadLine() is { } line) lines.Add(line);
                var rows = CliChatFormat.For(session.Status.Source).Map(lines).Select(r => PhoneFrames.FromTurn(r.Turn)).ToList();
                lock (_gate)
                {
                    if (_parsed.Count > 16) _parsed.Clear();
                    _parsed[path] = (info.Length, info.LastWriteTimeUtc, rows);
                }
                return rows;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // ---- launch ------------------------------------------------------------

        private HashSet<string> _before = new(StringComparer.Ordinal);

        internal static string? Locate(NewChatCli cli) => cli switch
        {
            NewChatCli.Codex => CodexBinary.Locate(),
            NewChatCli.Grok => GrokBinary.Locate(),
            _ => ClaudeBinary.Locate(),
        };

        // Starts the CLI where it can be typed into: a conhost window on Windows
        // (reached later by AttachConsole on the CLI's pid), tmux on a Mac or
        // Linux box (a window in the user's attached session when there is one,
        // else a detached session named orbweaver) — never Terminal.app.
        internal (string? Code, string? Why) Start(LaunchPlan plan, string binary)
        {
            lock (_gate) _before = [.. _sessions.Keys];
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var info = LaunchCommand.Windows(plan, binary, configDir: null);
                    if (info is null) return ("launch_failed", "that CLI is a script shim, which cannot carry a message safely");
                    using var started = Process.Start(info);
                    return started is null ? ("launch_failed", "the console did not start") : (null, null);
                }

                var tmux = TerminalLauncher.ResolveTmux();
                if (tmux is null) return ("no_terminal", "tmux is not installed");
                var command = LaunchCommand.Shell(plan, binary, configDir: null);
                if (!string.IsNullOrEmpty(TerminalLauncher.PlaceInOwnTmuxWindow(command, plan.Cwd))) return (null, null);

                // No attached tmux client: a detached session named orbweaver, and
                // later launches add windows to it.
                var hasSession = RunTmux(tmux, "has-session", "-t", "orbweaver");
                var ok = hasSession
                    ? RunTmux(tmux, "new-window", "-d", "-t", "orbweaver:", "-c", plan.Cwd, command)
                    : RunTmux(tmux, "new-session", "-d", "-s", "orbweaver", "-c", plan.Cwd, command);
                return ok ? (null, null) : ("launch_failed", "tmux refused to start the session");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return ("launch_failed", e.GetType().Name);
            }
        }

        private static bool RunTmux(string tmux, params string[] args)
        {
            var info = new ProcessStartInfo(tmux) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p is null) return false;
            p.WaitForExit(10000);
            return p.HasExited && p.ExitCode == 0;
        }

        // A session of this cli in this folder that was not in the book when the
        // launch began. cwd is compared the way the OS does: ordinal on a Mac,
        // case-blind on Windows.
        internal PhoneService.Session? FindNew(NewChatCli cli, string cwd, DateTimeOffset since)
        {
            var source = cli switch { NewChatCli.Codex => SessionSource.Codex, NewChatCli.Grok => SessionSource.Grok, _ => SessionSource.ClaudeCode };
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            lock (_gate)
                return _sessions.Values.FirstOrDefault(s => !_before.Contains(s.Id) && s.Status.Source == source
                    && string.Equals(s.Status.Cwd.TrimEnd('/', '\\'), cwd.TrimEnd('/', '\\'), comparison));
        }

        internal static bool CanType(SessionStatus status) =>
            status.IsLocalCli && !status.IsDesktopHosted && TerminalFocuser.CanSendQuietly(status);

        // TypingGate first (the record, then the screen), with the settle rule
        // from PR #227 generalised to typing: two screen reads 150 ms apart
        // must be identical, and both must read as a prompt, before any key goes
        // in. A screen that moved between the reads is not a prompt to type into.
        internal static async Task<(bool Typed, string? Refusal)> TypeAsync(PhoneService.Session session, string text)
        {
            var status = session.Status;
            var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
                status, text,
                TerminalFocuser.SendTextAndSubmit,
                s => s.SessionPid > 0 ? SessionPark.RecordStatus(s.SessionPid, session.Id) : null,
                SettledCapture,
                TerminalFocuser.CanReadPane,
                shadow: null, log: null, who: "phone").ConfigureAwait(false);
            return (typed, refusal);
        }

        private static async Task<string?> SettledCapture(SessionStatus status)
        {
            var first = await TerminalFocuser.CapturePane(status).ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false);
            var second = await TerminalFocuser.CapturePane(status).ConfigureAwait(false);
            return first is not null && first == second ? second : null;
        }
    }
}
