using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Orbweaver
{
    // Re-points the crash keep-alive LaunchAgent when the program it names is
    // gone, by running the bundle's own install-hooks.sh --keepalive-only
    // (CB-256 §2, the design's risk #1).
    //
    // **Why this has to exist at all.** The keep-alive plist written by
    // install-hooks.sh names one executable path, and phase 3 renamed the
    // executable: a phase-2 install's plist says
    // Orbweaver.app/Contents/MacOS/ClaudeBuddy, and a phase-3 bundle has only
    // Contents/MacOS/Orbweaver. `build-macos-app.sh --install` and the DMG's
    // "Install Hooks.command" both rewrite the plist, but a user who drags the
    // new Orbweaver.app over the old one and never re-runs Install Hooks keeps
    // a LaunchAgent pointing at a file that no longer exists. launchd logs "No
    // such file", gives up, and the app runs only when someone starts it by
    // hand. The machine this hurts is exactly the one nobody is looking at —
    // the headless mini under launchd — and nobody reading release notes is
    // the user it hurts, by construction. Neither phase-2 mechanism notices:
    // MacOSLegacyBundle only ever looks at Claude Buddy.app.
    //
    // **It runs the script rather than rewriting the plist itself**, so there
    // is one implementation of reconcile_keepalive, not two to keep in step.
    // The bundled copy, Contents/Resources/install-hooks.sh, resolves the
    // bundle it sits in first, so the plist it writes names this executable;
    // it also re-reads "Serve on launch", so a stale plist for a setting since
    // turned off is removed rather than revived. Its output goes to
    // migration.log, beside what DataDirMigration and MacOSLegacyBundle say
    // they did.
    //
    // **The rule is narrow on purpose.** It acts only when the plist is ours
    // (its Label is Brand.MacBundleId), the program it names is missing, and
    // this process is running from a bundle — the last because there is no
    // bundled script to run otherwise, and a loose `dotnet run` binary is not
    // something launchd should be pointed at. A plist whose program exists is
    // left alone however it got there: the repair is for "dead", not for
    // "different". Once the script has rewritten it the program exists, so
    // this is a no-op on every launch after the one that repaired it.
    //
    // **What one repair does not do.** Loading the job makes launchd start a
    // copy straight away (SuccessfulExit=false implies RunAtLoad); that copy
    // finds this one holding the single-instance mutex and exits 0, which the
    // keep-alive takes at its word. So until the job is next loaded — the
    // next login — the running app is not launchd's child, and a crash of it
    // is not restarted. A repaired keep-alive, not an instantly armed one;
    // making it instant would mean this process stepping aside for launchd's,
    // which is not a thing a startup step should do on its own.
    //
    // Runs from Startup.Run's `repairKeepAlive` step, after `retireLegacy`: a
    // plist naming the legacy Claude Buddy.app is stale the moment
    // MacOSLegacyBundle trashes that bundle, and this catches it in the same
    // launch. Does nothing off macOS and nothing while any of
    // DataDirMigration.OverrideVariables is set, which every test suite does.
    // Never throws.
    internal static class KeepAliveRepair
    {
        // The bundled script and the flag that makes it touch only the
        // LaunchAgent — no hook wiring, no prompts.
        internal const string ScriptName = "install-hooks.sh";
        internal const string ScriptArgument = "--keepalive-only";

        // The script greps one file and calls launchctl twice, which takes a
        // fraction of a second; this only bounds a launchctl that hangs, so
        // startup cannot.
        internal static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

        internal enum Decision
        {
            // Our keep-alive names a program that is gone: run the script.
            Repair,

            // Anything else — no plist, not ours, its program is there, or no
            // bundle to repair from: do nothing.
            LeaveAlone,
        }

        // ---- the rules ------------------------------------------------------

        // The whole decision, with no filesystem behind it. `plistProgramPath`
        // is ReadProgram's answer, so null already means "no plist that is
        // provably ours"; `programExists` is only meaningful when it is not.
        internal static Decision Decide(string? plistProgramPath, string? ownProcessPath, bool programExists) =>
            plistProgramPath is { Length: > 0 } && !programExists && BundledScript(ownProcessPath) is not null
                ? Decision.Repair
                : Decision.LeaveAlone;

        // The program a LaunchAgent plist runs — the first ProgramArguments
        // string — but only when its Label is ours. Null for anything else: a
        // plist for some other job, a torn file, a binary plist, a missing or
        // empty array. Every one of those reads as "not provably ours", and
        // the repair must fail towards doing nothing. Managed rather than
        // `plutil`, for the reason MacOSLegacyBundle.ReadPlist gives.
        internal static string? ReadProgram(string xml)
        {
            XDocument document;
            try
            {
                // The DOCTYPE every plist carries is skipped, never fetched.
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                document = XDocument.Load(reader);
            }
            catch (XmlException)
            {
                return null;
            }

            // (A document that loaded always has a root.)
            var root = document.Root!;
            var dict = root.Name == "plist" ? root.Element("dict") : null;
            if (dict is null) return null;

            var label = ValueAfter(dict, "Label");
            if (label?.Name != "string" || label.Value != Brand.MacBundleId) return null;

            var arguments = ValueAfter(dict, "ProgramArguments");
            var program = arguments?.Name == "array" ? arguments.Elements().FirstOrDefault() : null;
            return program?.Name == "string" && program.Value.Length > 0 ? program.Value : null;
        }

        // The element straight after the top-level dict's <key>name</key>.
        private static XElement? ValueAfter(XElement dict, string key) =>
            dict.Elements("key").FirstOrDefault(element => element.Value == key)?.ElementsAfterSelf().FirstOrDefault();

        // The bundled install-hooks.sh beside an executable at
        // <name>.app/Contents/MacOS/<exe>, or null when the process is not
        // running from a bundle. Path arithmetic only; whether the script is
        // actually there is the executor's question.
        internal static string? BundledScript(string? processPath)
        {
            if (string.IsNullOrEmpty(processPath)) return null;

            var macOs = Path.GetDirectoryName(processPath);
            var contents = macOs is null ? null : Path.GetDirectoryName(macOs);
            var bundle = contents is null ? null : Path.GetDirectoryName(contents);
            if (bundle is null) return null;

            var inBundle = Path.GetFileName(macOs) == "MacOS"
                && Path.GetFileName(contents) == "Contents"
                && Path.GetFileName(bundle).EndsWith(".app", StringComparison.Ordinal);

            return inBundle ? Path.Combine(contents!, "Resources", ScriptName) : null;
        }

        // ---- the executor ---------------------------------------------------

        internal static void Run() =>
            Run(OperatingSystem.IsMacOS(),
                DataDirMigration.OverrideSet(Environment.GetEnvironmentVariable),
                RealPlistPath,
                () => Environment.ProcessPath);

        // Every effect injectable, so the tests drive it against temp folders
        // with a fake plist and a fake script on any platform. `plistPath` and
        // `processPath` are functions so that a no-op run never even resolves
        // the real ones.
        internal static void Run(
            bool onMac,
            bool overrideSet,
            Func<string> plistPath,
            Func<string?> processPath,
            Func<string, bool>? exists = null,
            Func<string, string, (int ExitCode, string Output)>? exec = null,
            Action<string>? log = null)
        {
            if (!onMac || overrideSet) return;

            var lines = new List<string>();

            try
            {
                exists ??= File.Exists;
                exec ??= (script, argument) => Exec(script, argument, ScriptTimeout);

                var plist = plistPath();
                var program = File.Exists(plist) ? ReadProgram(File.ReadAllText(plist)) : null;
                var own = processPath();

                if (Decide(program, own, program is not null && exists(program)) == Decision.Repair)
                {
                    // Repair means BundledScript answered.
                    var script = BundledScript(own)!;
                    lines.Add($"keep-alive {plist} names {program}, which is gone");
                    if (!exists(script))
                    {
                        lines.Add($"keep-alive not repaired: no {script} to run");
                    }
                    else
                    {
                        var (exitCode, output) = exec(script, ScriptArgument);
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            lines.Add("  " + line);
                        lines.Add(exitCode == 0
                            ? $"keep-alive repaired by {script} {ScriptArgument}"
                            : $"keep-alive repair: {script} {ScriptArgument} exited {exitCode}");
                    }
                }
            }
            catch (Exception error)
            {
                lines.Add($"keep-alive repair stopped: {error.GetType().Name}: {error.Message}");
            }

            try
            {
                var sink = log ?? (line => DataDirMigration.AppendLog(CrashLog.Directory, line));
                foreach (var line in lines) sink(line);
            }
            catch
            {
                // A diagnostic that can break startup is worse than none.
            }
        }

        // Runs `file argument` and hands back its exit code and everything it
        // printed, stdout then stderr. A run still going at `timeout` is killed
        // and reported as exit -1, so a hung launchctl costs startup a bounded
        // wait rather than the app. Throws if the file cannot be started at
        // all; Run turns that into a log line.
        internal static (int ExitCode, string Output) Exec(string file, string argument, TimeSpan timeout)
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                ProcessTreeKill.Kill(process);
                process.WaitForExit();
                var seconds = timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
                return (-1, $"{stdout.Result}{stderr.Result}timed out after {seconds} s");
            }

            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        // Excluded from coverage: the real LaunchAgents folder. Every test
        // drives Run with a temp plist, and every suite sets the overrides that
        // keep Run() from resolving this.
        [ExcludeFromCodeCoverage]
        private static string RealPlistPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", Brand.MacBundleId + ".plist");
    }
}
