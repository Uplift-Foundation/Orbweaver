using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // What to tell someone whose last update did not finish (CB-308).
    //
    // On macOS the finisher is a separate process that kills the old app, swaps
    // the bundle, and relaunches. If the swap fails it restores the old bundle and
    // relaunches that, then leaves a `failed` marker holding the reason — because
    // by then the process that started the update is gone and cannot say anything.
    // This is the half that reads the marker on the next launch and says it, once.
    //
    // A marker the notice window already showed inline (the failure happened while
    // the old app was still alive to read it) is cleared at that point, so it is
    // not announced a second time at the next start.
    internal static class UpdateFailureNotice
    {
        internal sealed record Failure(string Version, string Reason, string MarkerPath);

        internal static string UpdatesRoot() => Path.Combine(OrbweaverSettings.Directory, "updates");

        // Pure over the filesystem it is handed: every <root>/<version>/failed.
        internal static IReadOnlyList<Failure> Find(string updatesRoot)
        {
            var found = new List<Failure>();
            if (!Directory.Exists(updatesRoot)) return found;

            foreach (var dir in Directory.GetDirectories(updatesRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var marker = Path.Combine(dir, UpdateInstallPlan.MacFailedMarker);
                if (!File.Exists(marker)) continue;

                string reason;
                try { reason = File.ReadAllText(marker).Trim(); }
                catch (IOException) { continue; }

                if (reason.Length == 0) reason = "The installer did not say why.";
                found.Add(new Failure(Path.GetFileName(dir), reason, marker));
            }

            return found;
        }

        internal static string Message(Failure failure) =>
            $"The update to {failure.Version} did not complete, so {Brand.DisplayName} is still on the version you had. "
            + failure.Reason;

        internal static void Clear(string updatesRoot, string version)
        {
            var marker = Path.Combine(updatesRoot, version, UpdateInstallPlan.MacFailedMarker);
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Reads, shows, then deletes — in that order, so a marker that cannot be
        // deleted is shown again rather than silently kept from view.
        internal static int Run(string updatesRoot, Action<string, string> show)
        {
            var failures = Find(updatesRoot);
            foreach (var failure in failures)
            {
                show("Update did not complete", Message(failure));
                Clear(updatesRoot, failure.Version);
            }

            return failures.Count;
        }

        // Windows: tamsin's ReconcileAtLaunch says whether the silent setup landed.
        // Its LogPath comes out of a marker the user's account can write, so it is
        // only offered as an "Open log" action when it is an absolute path to an
        // existing .log file; anything else shows the notice without the button.
        internal static string? TrustedLog(string? logPath) =>
            !string.IsNullOrEmpty(logPath)
            && Path.IsPathRooted(logPath)
            && logPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && File.Exists(logPath)
                ? logPath
                : null;

        internal static string WindowsMessage(UpdateInstallPlan.WinPendingUpdate pending) =>
            $"The update to {pending.To} did not complete, so {Brand.DisplayName} is still on {pending.From}. "
            + "Nothing was lost; you can try again from the tray menu or download the installer yourself.";

        // Returns whether a notice was shown. `show` gets the message and the
        // vetted log path (null = no button).
        internal static bool RunWindows(
            UpdateInstallPlan.WinPendingUpdate? pending, Action<string, string, string?> show)
        {
            if (pending is null) return false;
            show("Update did not complete", WindowsMessage(pending), TrustedLog(pending.LogPath));
            return true;
        }

        [ExcludeFromCodeCoverage]
        internal static void RunWindowsAtLaunch(string currentVersion)
        {
            if (!OperatingSystem.IsWindows()) return;
            var pending = UpdateInstallWindows.ReconcileAtLaunch(UpdatesRoot(), currentVersion, UpdateServices.Log);
            RunWindows(pending, (t, m, log) => UpdateMessageWindow.Toggle(t, m, false, log));
        }

        [ExcludeFromCodeCoverage]
        internal static void Run() =>
            Run(UpdatesRoot(), (title, message) => UpdateMessageWindow.Toggle(title, message, offerLocalNetwork: false));
    }
}
