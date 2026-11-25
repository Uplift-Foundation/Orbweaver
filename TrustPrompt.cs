using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-397: reads a CLI's folder-trust prompt off a pane the desk has just
    // started, to decide whether the launching desktop may be asked
    // "Trust this folder on <machine>?". Pure: no window, no settings, no
    // process. LaunchRunner owns what is done with the answer.
    //
    // Three readings, and the default is the quiet one:
    //   * Answerable: Claude Code's prompt, for exactly the folder launched,
    //     drawn whole. Only Claude Code is answered in v1, because only its
    //     wording was captured on a tmux pane (CB-327) and its answer is
    //     arrows and Enter, which TerminalPress already presses.
    //   * Unanswerable: a trust prompt that is reported and never answered —
    //     Codex's and Grok's, or Claude's where the folder does not bind.
    //   * None: everything else, including an unreadable pane. The launch
    //     carries on to no_session exactly as before.
    //
    // Matched over the whole visible screen with whitespace collapsed, because
    // a narrow pane wraps the folder mid-token and the footer at a space
    // (tests/TranscriptTests/Captures/cb327/trust-w32.txt).
    internal static class TrustPrompt
    {
        internal enum Kind { None, Answerable, Unanswerable }

        internal enum Option { None, No, Yes }

        // Marker is which option the ❯ is on, meaningful only when Answerable.
        internal sealed record Reading(Kind Kind, Option Marker = Option.None, bool Hooks = false)
        {
            internal static readonly Reading Nothing = new(Kind.None);
        }

        private const string Heading = "Accessing workspace:";
        private const string Question = "Is this a project you created or one you trust?";
        private const string Footer = "Enter to confirm · Esc to cancel";
        private const string NoLine = "No, exit";
        private const string YesLine = "Yes, I trust this folder";
        private const string Marker = "❯";

        // CB-476: Claude Code in a plain conhost window (the console a Windows
        // launch opens, read on the PC in the logged-on session) draws its
        // marker as ">" and the hooks sign as "‼", its ASCII fallbacks; the
        // same screen read from a console with Unicode glyphs draws ❯. Either
        // marker counts, and only on the two exact option lines.
        private static readonly string[] Markers = { Marker, ">" };
        private const string HooksWarning = "This folder configures hooks that can run commands without asking";

        // The trust wording FirstRunDialogs already knows for the other CLIs
        // and the older Claude phrasing: detected, reported, never answered.
        private static readonly string[] OtherTrustWording =
        {
            "Trusting the directory allows",
            "Grok Build may run or modify contents in this directory",
            "Do you trust the contents of this directory",
            "Do you trust the files in this folder",
        };

        // `resolved` is the physical spelling of the launched folder, which is what
        // Claude prints for a folder asked for through a link; either binds.
        internal static Reading Read(string? screen, string cwd, NewChatCli cli, string? resolved = null)
        {
            if (string.IsNullOrWhiteSpace(screen)) return Reading.Nothing;

            var flat = Collapse(screen);

            if (!flat.Contains(Heading, StringComparison.Ordinal))
                return OtherTrustWording.Any(w => flat.Contains(w, StringComparison.Ordinal))
                    ? new Reading(Kind.Unanswerable)
                    : Reading.Nothing;

            // The wording followed by anything but its own footer is a screen
            // that has moved on, or one that was cut off: nothing to answer.
            if (!flat.Contains(Question, StringComparison.Ordinal) || !flat.EndsWith(Footer, StringComparison.Ordinal))
                return Reading.Nothing;

            var marker = MarkerOn(screen);
            if (marker is null) return Reading.Nothing;

            if (cli != NewChatCli.ClaudeCode || !FolderBinds(screen, cwd, resolved))
                return new Reading(Kind.Unanswerable);

            return new Reading(Kind.Answerable, marker.Value, flat.Contains(HooksWarning, StringComparison.Ordinal));
        }

        // Exactly the two option lines, exactly one carrying the marker.
        private static Option? MarkerOn(string screen)
        {
            var lines = screen.Split('\n').Select(l => l.Trim()).ToArray();
            var no = lines.Where(l => Strip(l) == NoLine).ToArray();
            var yes = lines.Where(l => Strip(l) == YesLine).ToArray();
            if (no.Length != 1 || yes.Length != 1) return null;
            var onNo = MarkerOf(no[0]) is not null;
            var onYes = MarkerOf(yes[0]) is not null;
            if (onNo == onYes) return null;
            return onNo ? Option.No : Option.Yes;
        }

        // The launched folder, compared whole: the text between the heading and
        // the question, whitespace removed, equals the folder with whitespace
        // removed. Not "contains", because "/tmp/x" is inside "/private/tmp/x"
        // and Claude prints a symlinked path resolved, which is a different
        // folder to trust.
        private static bool FolderBinds(string screen, string cwd, string? resolved)
        {
            var all = Regex.Replace(screen, @"\s+", "");
            var from = all.IndexOf(Heading.Replace(" ", ""), StringComparison.Ordinal);
            var to = all.IndexOf("Quicksafetycheck", StringComparison.Ordinal);
            if (from < 0 || to < from) return false;
            var shown = all[(from + Heading.Replace(" ", "").Length)..to];
            static string Trim(string p) => p.Length > 1 ? p.TrimEnd('/') : p;
            bool Is(string? folder)
            {
                var wanted = Regex.Replace(folder ?? "", @"\s+", "");
                if (wanted.Length == 0) return false;
                if (IsWindowsPath(shown) && IsWindowsPath(wanted))
                    return string.Equals(WindowsForm(shown), WindowsForm(wanted), StringComparison.OrdinalIgnoreCase);
                return Trim(shown) == Trim(wanted);
            }
            return Is(cwd) || Is(resolved);
        }

        // CB-476: a Windows folder is one folder however it is spelled: a
        // launch can name C:\Users\me as c:\users\ME, C:/Users/me or with a
        // trailing separator, and Windows' file system ignores case. So when
        // both the folder Claude printed and the folder asked for are Windows
        // paths (a drive letter, or a UNC \\server), they are compared with
        // either slash as \, no trailing one, ignoring case. Every other path
        // (macOS, Linux) is compared exactly as CB-397 did.
        private static bool IsWindowsPath(string p) => Regex.IsMatch(p, @"^([A-Za-z]:([\\/]|$)|\\\\)");

        private static string WindowsForm(string p)
        {
            var slashed = p.Replace('/', '\\');
            var trimmed = slashed.TrimEnd('\\');
            // A drive root keeps its separator, so C:\ and C: stay the same folder
            // and never become a match for C:\x.
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed.Length == 0 ? slashed : trimmed;
        }

        private static string? MarkerOf(string line) =>
            Markers.FirstOrDefault(m => line.StartsWith(m, StringComparison.Ordinal));

        private static string Strip(string line) =>
            (MarkerOf(line) is { } m ? line[m.Length..] : line).Trim();

        private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    }
}
