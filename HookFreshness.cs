using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Keeping the installed Claude Code hook script as new as the app (CB-265).
    //
    // Neither platform refreshes it on upgrade. On macOS the script is copied to
    // ~/.claude/orbweaver/ by install-macos-hooks.sh, which only runs when
    // somebody re-wires; on Windows it is copied to %LOCALAPPDATA%\Orbweaver\ by
    // install-windows-hooks.ps1, which the installer runs only when the
    // "wirehooks" task is ticked. A change to the hook therefore reaches new
    // installs and nobody else, which is how a hook that records a new field
    // can ship and be silently absent from every upgraded machine.
    //
    // What this does about it is deliberately small: at startup, if a copy of the
    // script is already installed and differs from the one bundled with this
    // build, overwrite it. It never creates a missing copy — a first install is
    // the user's choice and a machine that never wired hooks must stay that way —
    // and it never touches settings.json or any Codex or Grok configuration. The
    // script file is the only thing it writes.
    //
    // Which copy: every candidate that exists. settings.json invokes whichever
    // path the last installer wrote, and an upgraded machine can still be
    // pointing at the pre-rename ClaudeBuddyHook.* (confirmed on a real Mac: its
    // settings.json invokes ~/.claude/claude-buddy/ClaudeBuddyHook.sh and
    // ~/.claude/orbweaver does not exist). Refreshing a path nothing invokes is
    // harmless; failing to refresh the one that is invoked defeats the point. The
    // bundled script has the same content under either name — it does not read
    // its own filename — so the legacy path receives the same bytes.
    internal static class HookFreshness
    {
        internal enum Verdict { Skip, Reapply }

        // The decision, with no filesystem behind it. Reapply only when there is
        // an installed copy to refresh, a bundled script to refresh it from, and
        // the two actually differ; every other shape is Skip, in particular a
        // missing installed copy (never create) and a missing bundled script
        // (nothing to copy, and an empty file must not replace a working one).
        internal static Verdict Decide(bool installedExists, byte[]? installed, byte[]? bundled)
        {
            if (!installedExists || bundled is null || installed is null) return Verdict.Skip;
            return installed.AsSpan().SequenceEqual(bundled) ? Verdict.Skip : Verdict.Reapply;
        }

        // The installed copies, new first and legacy second — the same paths
        // NewChatHookState.HookCopyCandidates names for Claude Code, reused so the
        // two cannot disagree about where the installers put things.
        // Codex's copies too (CB-316). The Codex hook is the same script, and
        // its daemon fix only helps a machine whose copy is refreshed: the
        // MacBook's ~/.codex copy was found three weeks stale. Overwriting the
        // script never touches hooks.json, which is what Codex hashes for its
        // trust review, so a refresh cannot silently disable Codex's hooks.
        internal static IReadOnlyList<string> InstalledCandidates(
            bool onWindows, string userProfile, string localAppData, string? codexHome = null)
        {
            // The only variable either lookup asks for is Codex's CODEX_HOME.
            Func<string, string?> env = _ => codexHome;
            return NewChatHookState.HookCopyCandidates(NewChatCli.ClaudeCode, onWindows, userProfile, localAppData, env)
                .Concat(NewChatHookState.HookCopyCandidates(NewChatCli.Codex, onWindows, userProfile, localAppData, env))
                .ToList();
        }

        // Where the bundled script sits in each layout the app runs from: inside
        // the .app, Contents/Resources beside the installers (BaseDirectory is
        // Contents/MacOS); on Windows next to the exe, per Orbweaver.iss's
        // {app}; from a source build, the repo root, found by walking up. Not
        // HookInstaller.Resolve, which looks for installers under tools/ and so
        // never finds a script that sits beside the exe.
        internal static string? BundledScript(string name, string baseDirectory)
        {
            var resources = Path.Combine(baseDirectory, "..", "Resources", name);
            if (File.Exists(resources)) return Path.GetFullPath(resources);

            // Beside the exe and nowhere further up. A source build used to be
            // found by walking up to the repo root, which meant any `dotnet run`
            // from a feature worktree overwrote the user's real installed hook
            // with that branch's copy — on a machine where a dozen agents build
            // a dozen branches, the installed hook would be whichever ran last.
            // The build does not copy the script into bin/, so a source build
            // finds nothing here and refreshes nothing, which is the point:
            // only an installed app speaks for what the installed hook should be.
            var beside = Path.Combine(baseDirectory, name);
            return File.Exists(beside) ? beside : null;
        }

        // What happened to one installed copy, for the log.
        internal sealed record Refreshed(string Path, bool Overwrote, string? Error = null);

        // Refresh every installed copy that differs from the bundled script.
        // Never throws: a startup chore that can take the app down over a locked
        // file is worse than a stale hook. Returns what it did so a test can
        // assert on it; nothing is written for a copy that is absent or current.
        internal static IReadOnlyList<Refreshed> Refresh(
            string? bundledPath, IEnumerable<string> installedCandidates, Action<string>? log = null)
        {
            var done = new List<Refreshed>();
            if (bundledPath is null) return done;

            byte[] bundled;
            try { bundled = File.ReadAllBytes(bundledPath); }
            catch (Exception ex)
            {
                log?.Invoke($"could not read bundled hook {bundledPath}: {ex.Message}");
                return done;
            }

            foreach (var path in installedCandidates)
            {
                try
                {
                    var exists = File.Exists(path);
                    var installed = exists ? File.ReadAllBytes(path) : null;
                    if (Decide(exists, installed, bundled) != Verdict.Reapply) continue;

                    Overwrite(path, bundled);
                    done.Add(new Refreshed(path, true));
                    log?.Invoke($"refreshed {path} from {bundledPath}");
                }
                catch (Exception ex)
                {
                    done.Add(new Refreshed(path, false, ex.Message));
                    log?.Invoke($"could not refresh {path}: {ex.Message}");
                }
            }

            return done;
        }

        // Write beside the target and rename over it. A hook can fire at any
        // moment, and bash reads a script as it goes, so an in-place rewrite
        // could hand a running hook half of each version; a rename leaves a
        // hook that already opened the old file reading the old inode. The
        // existing file's permissions are carried across so the script stays
        // executable on macOS.
        private static void Overwrite(string path, byte[] bytes)
        {
            var temp = path + ".refresh-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temp, bytes);
                CopyUnixMode(path, temp);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        // Excluded from coverage: the Windows arm of an OS check (Windows has no
        // Unix mode); the macOS arm is covered by an integration test.
        [ExcludeFromCodeCoverage(Justification = "Windows arm of the Unix-mode check")]
        private static void CopyUnixMode(string from, string to)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }

        // The startup entry: pool thread, real paths, real log. Excluded from
        // coverage because it only binds the tested pieces to the real process
        // (AppContext, the user's home, the log file); Refresh is where the
        // behaviour lives and is tested against scratch directories. The outer
        // catch guards the path lookups, since Refresh itself covers the rest.
        [ExcludeFromCodeCoverage(Justification = "Binds Refresh to the real home, base directory and log")]
        internal static void StartInBackground() =>
            _ = Task.Run(() =>
            {
                try
                {
                    var onWindows = OperatingSystem.IsWindows();
                    var bundled = BundledScript(
                        onWindows ? Brand.HookScriptPowerShell : Brand.HookScriptShell,
                        AppContext.BaseDirectory);
                    Refresh(
                        bundled,
                        InstalledCandidates(
                            onWindows,
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            Environment.GetEnvironmentVariable("CODEX_HOME")),
                        message => HookInstallerLog.Record("hook-freshness",
                            new HookInstallResult(HookInstallOutcome.Ok, "hook-freshness", Output: message)));
                }
                catch
                {
                }
            });
    }
}
