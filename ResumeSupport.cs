using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // Whether the installed CLI can resume by id (CB-361). The flags were read
    // off each CLI's --help at Claude Code 2.1.330, Codex 0.154.0 and Grok
    // 1.0.13; an older install without them would otherwise start a fresh
    // session, or Codex's picker, while the dialog said "resumed" — so the
    // help text is asked first and a launch it does not support is refused
    // with nothing started.
    internal sealed record ResumeCheck(bool Supported, string Message);

    internal static class ResumeSupport
    {
        // `-r, --resume [value]` (Claude) and `--resume <ID>` (Grok) are flags.
        private static readonly Regex ResumeFlag = new(@"(^|[\s,])--resume\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // Codex lists it under Commands: as an indented `resume` word at the
        // start of a line, e.g. "  resume      Resume a previous interactive session".
        private static readonly Regex ResumeCommand = new(@"^\s+resume\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // Pure: does this help text offer resume-by-id for this CLI?
        // version is only for the message; null leaves it out.
        internal static ResumeCheck Check(NewChatCli cli, string? helpText, string? version = null)
        {
            var text = helpText ?? "";
            var supported = cli == NewChatCli.Codex ? ResumeCommand.IsMatch(text) : ResumeFlag.IsMatch(text);
            var name = NewChatLauncher.DisplayName(cli);
            return supported
                ? new ResumeCheck(true, name + " can resume sessions.")
                : new ResumeCheck(false,
                    "This version of " + name + " can't resume from here"
                    + (string.IsNullOrWhiteSpace(version) ? "" : " (" + version.Trim() + ")") + ".");
        }

        // The first version-looking token in a `--version` answer, e.g.
        // "2.1.330 (Claude Code)" or "codex-cli 0.154.0".
        internal static string? VersionFrom(string? versionText) =>
            versionText is null ? null
            : Regex.Match(versionText, @"\d+(\.\d+)+") is { Success: true } m ? m.Value : null;

        // Tests: a fixed help text per CLI instead of running a binary.
        internal static Func<NewChatCli, string, ResumeCheck>? ProbeForTests;

        // The check for a real binary, cached by path and write time so the
        // dialog does not run `--help` on every click, and a reinstall (new
        // mtime) is asked again.
        internal static ResumeCheck Probe(NewChatCli cli, string binary) =>
            ProbeForTests?.Invoke(cli, binary) ?? ProbeReal(cli, binary);

        private static readonly ConcurrentDictionary<(string, DateTime), ResumeCheck> Probed = new();

        [ExcludeFromCodeCoverage]
        private static ResumeCheck ProbeReal(NewChatCli cli, string binary)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(binary); }
            catch { stamp = DateTime.MinValue; }

            return Probed.GetOrAdd((binary, stamp), _ =>
                Check(cli, Run(binary, "--help"), VersionFrom(Run(binary, "--version"))));
        }

        // Bounded at 10 s: a CLI that hangs on --help (a login prompt, an
        // update check) is treated as one that said nothing, which refuses.
        [ExcludeFromCodeCoverage]
        private static string? Run(string binary, string arg)
        {
            try
            {
                var start = new ProcessStartInfo(binary, arg)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(start);
                if (process is null) return null;
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
                return stdout.Result + "\n" + stderr.Result;
            }
            catch { return null; }
        }
    }
}
