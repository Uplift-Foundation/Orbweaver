using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Everything the macOS updater does that is not a decision (CB-308). The
    // decisions are UpdateInstallPlan.MacOS.cs; this runs them, and every OS
    // call goes through the `exec` Func so no suite ever runs a real hdiutil,
    // codesign, spctl, open or launchctl.
    internal delegate (int ExitCode, string Output) MacExec(MacCommand command, TimeSpan timeout);

    internal static class UpdateInstallMacOS
    {
        private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

        // ------------------------------------------------------------------
        // Stage: the new bundle, verified, copied out of the mounted image.
        // ------------------------------------------------------------------

        // Returns the path of the verified copy, or the reason it is not
        // trustworthy. The image is detached in a `finally` whatever happens,
        // and nothing is installed: a refusal here leaves the installed
        // bundle exactly as it was, which the integration test pins by hash.
        internal static (string? StagedApp, string? Error) Stage(
            string dmg, string stagingDir, string expectedVersion, MacExec exec, Action<string> log)
        {
            var mount = Path.Combine(stagingDir, "mnt");
            Directory.CreateDirectory(mount);
            var attach = exec(UpdateInstallPlan.MacHdiutilAttach(dmg, mount), ToolTimeout);
            if (attach.ExitCode != 0)
                return (null, $"Couldn't open the update image (hdiutil exited {attach.ExitCode}).");
            try
            {
                var entries = new DirectoryInfo(mount).EnumerateFileSystemInfos()
                    .Select(e => (e.Name, e is DirectoryInfo, e.LinkTarget is not null));
                var (appName, findError) = UpdateInstallPlan.MacFindSingleApp(entries);
                if (appName is null) return (null, findError);
                var mounted = Path.Combine(mount, appName);

                var gate = Verify(mounted, expectedVersion, exec, log);
                if (gate is not null) return (null, gate);

                var staged = Path.Combine(stagingDir, appName);
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                var copy = exec(UpdateInstallPlan.MacCopyBundle(mounted, staged), ToolTimeout);
                if (copy.ExitCode != 0)
                    return (null, $"Couldn't copy the new app out of the update image (ditto exited {copy.ExitCode}).");

                // The copy is what gets installed, so it is what is verified
                // last: a copy that no longer verifies is not an update.
                var recheck = exec(UpdateInstallPlan.MacCodesignVerify(staged), ToolTimeout);
                if (recheck.ExitCode != 0)
                {
                    try { Directory.Delete(staged, true); } catch { }
                    return (null, "The copied app failed its signature check.");
                }
                log($"staged {staged}");
                return (staged, null);
            }
            finally
            {
                if (exec(UpdateInstallPlan.MacHdiutilDetach(mount, force: false), ToolTimeout).ExitCode != 0)
                    exec(UpdateInstallPlan.MacHdiutilDetach(mount, force: true), ToolTimeout);
            }
        }

        // The gate: signature valid, signed by our team, Gatekeeper accepts
        // it, and it is the bundle and version we expect. Each failing check
        // is its own message because "refused" with no reason is not
        // something a person can act on. Null means every check passed.
        private static string? Verify(string app, string expectedVersion, MacExec exec, Action<string> log)
        {
            var verify = exec(UpdateInstallPlan.MacCodesignVerify(app), ToolTimeout);
            if (verify.ExitCode != 0) return $"The new app's signature is not valid (codesign: {Tail(verify.Output)}).";

            var display = exec(UpdateInstallPlan.MacCodesignDisplay(app), ToolTimeout);
            var team = UpdateInstallPlan.MacParseTeamId(display.Output);
            if (team != UpdateInstallPlan.MacTeamId)
                return $"The new app is not signed by Orbweaver's Developer ID team (found {team ?? "none"}).";

            var spctl = exec(UpdateInstallPlan.MacSpctl(app), ToolTimeout);
            if (spctl.ExitCode != 0) return $"macOS Gatekeeper would not accept the new app (spctl: {Tail(spctl.Output)}).";

            var id = exec(UpdateInstallPlan.MacPlistRead(app, "CFBundleIdentifier"), ToolTimeout);
            if (id.ExitCode != 0 || id.Output.Trim() != Brand.MacBundleId)
                return $"The new app has the wrong bundle identifier ({id.Output.Trim()}).";

            var version = exec(UpdateInstallPlan.MacPlistRead(app, "CFBundleShortVersionString"), ToolTimeout);
            if (version.ExitCode != 0 || version.Output.Trim() != expectedVersion)
                return $"The new app reports version {version.Output.Trim()}, not the {expectedVersion} that was offered.";

            log($"verified {app}: team {team}, version {expectedVersion}");
            return null;
        }

        private static string Tail(string output)
        {
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return string.IsNullOrEmpty(line) ? "no output" : line;
        }

        // ------------------------------------------------------------------
        // Launch: start the finisher and wait for its marker.
        // ------------------------------------------------------------------

        // Writes the handoff, runs `open -n`, then polls for the marker the
        // finisher leaves. `pollDelay` is injected so a test is instant; the
        // real one waits half a second, so 120 polls is the 60 seconds the
        // plan allows before "the helper never started".
        internal static async Task<InstallOutcome> LaunchAsync(
            MacHandoff handoff, MacExec exec, Func<Task> pollDelay, Action<string> log, int maxPolls = 120)
        {
            Directory.CreateDirectory(handoff.MarkerDir);
            foreach (var stale in new[] { UpdateInstallPlan.MacFinishedMarker, UpdateInstallPlan.MacFailedMarker })
            {
                var path = Path.Combine(handoff.MarkerDir, stale);
                if (File.Exists(path)) File.Delete(path);
            }
            var handoffPath = Path.Combine(handoff.MarkerDir, "handoff.json");
            File.WriteAllText(handoffPath, UpdateInstallPlan.MacSerializeHandoff(handoff));

            var open = exec(UpdateInstallPlan.MacOpenFinisher(handoff.Staged, handoffPath), ToolTimeout);
            if (open.ExitCode != 0)
            {
                log($"open -n exited {open.ExitCode}: {Tail(open.Output)}");
                return new InstallOutcome(InstallStatus.Failed, "The update helper could not be started. Nothing was changed.");
            }

            for (var i = 0; i < maxPolls; i++)
            {
                var failed = Path.Combine(handoff.MarkerDir, UpdateInstallPlan.MacFailedMarker);
                if (File.Exists(failed))
                    return new InstallOutcome(InstallStatus.Failed, File.ReadAllText(failed).Trim());
                if (File.Exists(Path.Combine(handoff.MarkerDir, UpdateInstallPlan.MacFinishedMarker)))
                    return new InstallOutcome(InstallStatus.HandedOff, $"Updated to {handoff.Version}.");
                await pollDelay();
            }
            return new InstallOutcome(InstallStatus.Failed, "The update helper did not start, so nothing was changed.");
        }

        // ------------------------------------------------------------------
        // Finish: runs as the staged NEW bundle, with the old app still up.
        // ------------------------------------------------------------------

        internal sealed record FinishDeps(
            MacExec Exec,
            Action<string> Log,
            Func<DateTimeOffset> Now,
            Action<int, bool> Signal,      // (pid, force)
            Func<int, bool> IsAlive,
            Func<TimeSpan, Task> Delay,
            Action<string, string> Move,   // Directory.Move, injected so EXDEV is testable
            string TrashDir,
            int Uid,
            int SelfPid);

        // Never throws: any exception becomes a `failed` marker, because the
        // old app reads the marker and a finisher that died silently would
        // leave "did it update?" unanswerable.
        internal static async Task<bool> FinishAsync(MacHandoff h, FinishDeps d)
        {
            var oldPath = UpdateInstallPlan.MacOldPath(h.Installed, d.Now());
            var swapped = false;
            try
            {
                // Before anything is stopped, unloaded or moved: a handoff from
                // a version this build does not understand is refused with the
                // old app still running and the installed bundle untouched.
                if (h.Schema != UpdateInstallPlan.MacHandoffSchema)
                    return Fail(h, d, $"The update was prepared by a version this one can't finish (handoff schema {h.Schema}, expected {UpdateInstallPlan.MacHandoffSchema}), so nothing was changed.");

                d.Log($"finish: {h.Staged} -> {h.Installed} (old pid {h.Pid})");

                // 1. This bundle must still verify, as itself.
                if (d.Exec(UpdateInstallPlan.MacCodesignVerify(h.Staged), ToolTimeout).ExitCode != 0)
                    return Fail(h, d, "The staged update failed its signature check, so nothing was changed.");

                // 2. launchd would otherwise restart the old app the moment we stop it.
                if (h.KeepAlivePlist is not null && File.Exists(h.KeepAlivePlist))
                    d.Exec(UpdateInstallPlan.MacLaunchctlUnload(h.KeepAlivePlist), ToolTimeout);

                // 3. Stop the old app, and anything else running the installed executable.
                var exe = Path.Combine(h.Installed, "Contents", "MacOS", Brand.AssemblyName);
                var others = UpdateInstallPlan.MacParsePs(d.Exec(UpdateInstallPlan.MacPsList(), ToolTimeout).Output, exe, d.SelfPid, d.Uid);
                var targets = others.Append(h.Pid).Where(p => p != d.SelfPid).Distinct().ToList();
                await StopAsync(targets, d);

                // 4. Swap.
                if (Directory.Exists(h.Installed)) d.Move(h.Installed, oldPath);
                try
                {
                    MoveBundle(h, d);
                    swapped = true;
                }
                catch
                {
                    // A restore that fails must not replace the swap's own
                    // error as the one that is reported.
                    try { RestoreOld(h, oldPath, d); }
                    catch (Exception restore) { d.Log($"finish: restore failed: {restore.Message}"); }
                    throw;
                }

                // 5. The keep-alive plist names the executable, so reconcile it
                // from the NEW bundle's own script.
                var repair = d.Exec(UpdateInstallPlan.MacKeepAliveRepair(h.Installed), ToolTimeout);
                if (repair.ExitCode != 0) d.Log($"keep-alive reconcile exited {repair.ExitCode}: {Tail(repair.Output)}");

                // 6. Wait for the new process; if launchd did not start one, start it.
                if (!await RunningAsync(exe, d, 10))
                {
                    d.Exec(UpdateInstallPlan.MacOpenInstalled(h.Installed), ToolTimeout);
                    if (!await RunningAsync(exe, d, 20))
                    {
                        RestoreAndRelaunch(h, oldPath, d);
                        return Fail(h, d, "The new version was installed but did not start, so the previous version was put back.");
                    }
                }

                // 7. Success: the old bundle to the Trash, the image out of the way.
                TrashOld(oldPath, d);
                foreach (var dmg in Directory.Exists(h.MarkerDir) ? Directory.GetFiles(h.MarkerDir, "*.dmg") : [])
                    try { File.Delete(dmg); } catch { }
                File.WriteAllText(Path.Combine(h.MarkerDir, UpdateInstallPlan.MacFinishedMarker), h.Version);
                d.Log($"finish: updated to {h.Version}");
                return true;
            }
            catch (Exception e)
            {
                d.Log($"finish: failed: {e.Message}");
                if (swapped) RestoreAndRelaunch(h, oldPath, d);
                else d.Exec(UpdateInstallPlan.MacOpenInstalled(h.Installed), ToolTimeout);
                return Fail(h, d, $"The update did not complete ({e.Message}). The previous version was restored.");
            }
        }

        // staged -> installed. A cross-volume rename throws IOException, so it
        // falls back to copying beside the target and renaming that, which
        // keeps the final step an atomic rename on the target's own volume.
        private static void MoveBundle(MacHandoff h, FinishDeps d)
        {
            try { d.Move(h.Staged, h.Installed); }
            catch (IOException)
            {
                var staging = UpdateInstallPlan.MacNewPath(h.Installed);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                var copy = d.Exec(UpdateInstallPlan.MacCopyBundle(h.Staged, staging), ToolTimeout);
                if (copy.ExitCode != 0) throw new IOException($"copy into place failed (ditto exited {copy.ExitCode})");
                d.Move(staging, h.Installed);
            }
        }

        private static async Task StopAsync(IReadOnlyList<int> pids, FinishDeps d)
        {
            foreach (var pid in pids) d.Signal(pid, false);
            for (var i = 0; i < 20 && pids.Any(d.IsAlive); i++) await d.Delay(TimeSpan.FromMilliseconds(500));
            foreach (var pid in pids.Where(d.IsAlive))
            {
                d.Log($"finish: pid {pid} ignored SIGTERM, sending SIGKILL");
                d.Signal(pid, true);
            }
        }

        private static async Task<bool> RunningAsync(string exe, FinishDeps d, int polls)
        {
            for (var i = 0; i < polls; i++)
            {
                if (UpdateInstallPlan.MacParsePs(d.Exec(UpdateInstallPlan.MacPsList(), ToolTimeout).Output, exe, d.SelfPid, d.Uid).Count > 0)
                    return true;
                await d.Delay(TimeSpan.FromMilliseconds(500));
            }
            return false;
        }

        private static void RestoreOld(MacHandoff h, string oldPath, FinishDeps d)
        {
            if (!Directory.Exists(oldPath)) return;
            if (Directory.Exists(h.Installed)) Directory.Delete(h.Installed, true);
            d.Move(oldPath, h.Installed);
        }

        private static void RestoreAndRelaunch(MacHandoff h, string oldPath, FinishDeps d)
        {
            try { RestoreOld(h, oldPath, d); } catch (Exception e) { d.Log($"finish: restore failed: {e.Message}"); }
            d.Exec(UpdateInstallPlan.MacKeepAliveRepair(h.Installed), ToolTimeout);
            d.Exec(UpdateInstallPlan.MacOpenInstalled(h.Installed), ToolTimeout);
        }

        private static void TrashOld(string oldPath, FinishDeps d)
        {
            if (!Directory.Exists(oldPath)) return;
            try
            {
                Directory.CreateDirectory(d.TrashDir);
                d.Move(oldPath, Path.Combine(d.TrashDir, Path.GetFileName(oldPath)));
            }
            catch (Exception e) { d.Log($"finish: left {oldPath} in place, could not trash it: {e.Message}"); }
        }

        private static bool Fail(MacHandoff h, FinishDeps d, string reason)
        {
            try
            {
                Directory.CreateDirectory(h.MarkerDir);
                File.WriteAllText(Path.Combine(h.MarkerDir, UpdateInstallPlan.MacFailedMarker), reason);
            }
            catch (Exception e) { d.Log($"finish: could not write the failed marker: {e.Message}"); }
            d.Log($"finish: {reason}");
            return false;
        }

        // ------------------------------------------------------------------
        // The real-process bindings. Excluded from coverage: they start real
        // processes and signal real pids, which no suite may do. Each is a
        // few lines over an API whose behaviour the injected seams already pin.
        // ------------------------------------------------------------------

        [ExcludeFromCodeCoverage]
        internal static (int ExitCode, string Output) RealExec(MacCommand command, TimeSpan timeout)
        {
            var start = new ProcessStartInfo(command.File)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in command.Args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(true); } catch { }
                return (-1, "timed out");
            }
            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        // The finisher's entry point from Startup: true when this launch was
        // a --finish-update one (handled, so Startup returns), false for an
        // ordinary launch.
        [ExcludeFromCodeCoverage]
        internal static bool TryRunFinisher(string[] args)
        {
            var at = Array.IndexOf(args, UpdateInstallPlan.MacFinishFlag);
            if (at < 0) return false;
            var logDir = CrashLog.Directory;
            void Log(string line) => DataDirMigration.AppendLog(logDir, "update.log", line);
            var handoff = at + 1 < args.Length && File.Exists(args[at + 1])
                ? UpdateInstallPlan.MacParseHandoff(File.ReadAllText(args[at + 1])) : null;
            if (handoff is null) { Log("finish: no readable handoff; nothing done"); return true; }
            var deps = new FinishDeps(
                RealExec, Log, () => DateTimeOffset.UtcNow,
                (pid, force) => kill(pid, force ? 9 : 15),
                pid => { try { return !Process.GetProcessById(pid).HasExited; } catch { return false; } },
                Task.Delay,
                (from, to) => Directory.Move(from, to),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"),
                (int)geteuid(), Environment.ProcessId);
            FinishAsync(handoff, deps).GetAwaiter().GetResult();
            return true;
        }

        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern uint geteuid();

        // SIGTERM (15) then SIGKILL (9): Process.Kill is always SIGKILL, and
        // the old app should get the chance to flush its settings first.
        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern int kill(int pid, int signal);
    }
}
