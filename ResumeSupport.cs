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
        // `-r, --resume [value]` (Claude) and `--resume <ID>` (Grok) are flags:
        // the whole token, so --resume-session-at or --no-resume never count.
        private static readonly Regex ResumeFlag = new(@"(?<![\w-])--resume(?![\w-])", RegexOptions.CultureInvariant);

        // Codex lists it as a row of its Commands: table, the name then two or
        // more spaces before its description:
        // "  resume      Resume a previous interactive session". Only rows inside
        // that block count, so a wrapped description line that happens to start
        // with "resume" is not a subcommand.
        private static readonly Regex ResumeCommandRow = new(@"^\s+resume\s{2,}\S", RegexOptions.CultureInvariant);

        internal static bool HasCodexResumeCommand(string helpText)
        {
            var inCommands = false;
            foreach (var raw in helpText.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim() == "Commands:") { inCommands = true; continue; }
                // The block ends at the next unindented heading.
                if (inCommands && line.Length > 0 && !char.IsWhiteSpace(line[0])) inCommands = false;
                if (inCommands && ResumeCommandRow.IsMatch(line)) return true;
            }
            return false;
        }

        // Pure: does this help text offer resume-by-id for this CLI?
        // version is only for the message; null leaves it out.
        internal static ResumeCheck Check(NewChatCli cli, string? helpText, string? version = null)
        {
            var text = helpText ?? "";
            var supported = cli == NewChatCli.Codex ? HasCodexResumeCommand(text) : ResumeFlag.IsMatch(text);
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

        // The refusal when the help text could not be read at all — the probe
        // timed out, failed to start, or threw. That says nothing about the
        // version, so it is never worded as "this version can't", never cached,
        // and the next click asks again.
        internal static ResumeCheck Unchecked(NewChatCli cli) =>
            new(false, "Couldn't check whether " + NewChatLauncher.DisplayName(cli) + " can resume. Try again.");

        // Tests: a fixed answer per CLI instead of running a binary.
        internal static Func<NewChatCli, string, ResumeCheck>? ProbeForTests;

        // Blocking — up to 10 s for --help and --version together. Only ever
        // called from NewChatLauncher.Resume, which the dialog runs off the UI
        // thread.
        internal static ResumeCheck Probe(NewChatCli cli, string binary) =>
            ProbeForTests?.Invoke(cli, binary) ?? Shared.Probe(cli, binary);

        [ExcludeFromCodeCoverage]
        internal static ResumeProbe Shared { get; } = new(Run, StampOf);

        [ExcludeFromCodeCoverage]
        private static DateTime StampOf(string binary)
        {
            try { return File.GetLastWriteTimeUtc(binary); }
            catch { return DateTime.MinValue; }
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
                    try { ProcessTreeKill.Kill(process); } catch { }
                    return null;
                }
                return stdout.Result + "\n" + stderr.Result;
            }
            catch { return null; }
        }
    }

    // The probe and its cache, with the process run and the file stamp as
    // seams. Cached by binary path and write time, so the dialog does not run
    // --help on every click and a reinstall is asked again.
    //
    // Only an answer that came from real help text is cached (QA, CB-361): a
    // first run on the MacBook returned nothing, once, and caching that as
    // "unsupported" refused every resume of that CLI until the app restarted,
    // while telling the user their version was too old.
    internal sealed class ResumeProbe(Func<string, string, string?> run, Func<string, DateTime> stamp)
    {
        private readonly ConcurrentDictionary<(string, DateTime), ResumeCheck> _probed = new();

        internal ResumeCheck Probe(NewChatCli cli, string binary)
        {
            var key = (binary, stamp(binary));
            if (_probed.TryGetValue(key, out var known)) return known;

            // Both at once, so a slow binary costs one 10 s bound, not two.
            var help = Task.Run(() => run(binary, "--help"));
            var version = Task.Run(() => run(binary, "--version"));
            Task.WaitAll(help, version);

            if (help.Result is null) return ResumeSupport.Unchecked(cli);

            var check = ResumeSupport.Check(cli, help.Result, ResumeSupport.VersionFrom(version.Result));
            _probed[key] = check;
            return check;
        }
    }
}
