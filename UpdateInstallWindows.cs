using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // The Windows IUpdateInstaller (CB-308): preflight, download and verify the
    // setup exe, save pending settings, start Setup silently, and exit so Setup
    // can replace the files. Setup's own [Code] stops any stray copy and its
    // /RELAUNCH=1 [Run] entry starts the new build into the same desktop
    // session, so this process never has to relaunch anything.
    //
    // Everything with a side effect is a constructor argument so a test drives
    // the whole sequence against a scratch folder; setup.exe is never run for
    // real in a suite. CreateReal is the one place wired to the real thing.
    internal sealed class UpdateInstallWindows : IUpdateInstaller
    {
        // Downloads the installer and the checksum list, verifies the checksum
        // and returns the verified setup exe's path. The core owns that rule.
        internal delegate Task<string> DownloadVerified(
            UpdateRelease release, string directory, IProgress<UpdateProgress> progress, CancellationToken ct);

        private readonly DownloadVerified _download;
        private readonly string? _processPath;
        private readonly string _updatesRoot;
        private readonly string _currentVersion;
        private readonly Action _flushSettings;
        private readonly Func<ProcessStartInfo, int> _start;
        private readonly Action _shutdown;
        private readonly Action<string> _log;

        internal UpdateInstallWindows(
            DownloadVerified download, string? processPath, string updatesRoot, string currentVersion,
            Action flushSettings, Func<ProcessStartInfo, int> start, Action shutdown, Action<string> log)
        {
            _download = download;
            _processPath = processPath;
            _updatesRoot = updatesRoot;
            _currentVersion = currentVersion;
            _flushSettings = flushSettings;
            _start = start;
            _shutdown = shutdown;
            _log = log;
        }

        public async Task<InstallOutcome> InstallAsync(
            UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            var refusal = UpdateInstallPlan.WinPreflight(_processPath);
            if (refusal is not null)
            {
                _log($"refused: {refusal}");
                return new InstallOutcome(InstallStatus.Refused, refusal);
            }

            var directory = Path.Combine(_updatesRoot, release.Version);
            Directory.CreateDirectory(directory);
            string setup;
            try
            {
                setup = await _download(release, directory, progress, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _log($"download failed: {e.Message}");
                return new InstallOutcome(InstallStatus.Failed, $"The download did not complete: {e.Message}");
            }

            var logPath = Path.Combine(directory, UpdateInstallPlan.WinSetupLogFile);
            var markerPath = Path.Combine(_updatesRoot, UpdateInstallPlan.WinPendingFile);
            try
            {
                File.WriteAllText(markerPath, UpdateInstallPlan.WinPendingJson(
                    new UpdateInstallPlan.WinPendingUpdate(_currentVersion, release.Version, logPath)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"could not record the pending update: {e.Message}");
                return new InstallOutcome(InstallStatus.Failed,
                    $"The update could not be started because its working folder is not writable: {e.Message}");
            }

            progress.Report(new UpdateProgress(UpdateStage.Installing, 0, 0));
            // Settings are saved on a debounce; Setup's taskkill/Restart Manager
            // ends this process without a goodbye, so write anything pending now.
            _flushSettings();

            try
            {
                var pid = _start(UpdateInstallPlan.WinSetupStartInfo(setup, logPath));
                _log($"started {Path.GetFileName(setup)} for {release.Version} as pid {pid}; log {logPath}");
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                DeleteQuietly(markerPath);
                _log($"could not start setup: {e.Message}");
                return new InstallOutcome(InstallStatus.Failed,
                    $"The installer could not be started: {e.Message}. Nothing was changed.");
            }

            progress.Report(new UpdateProgress(UpdateStage.Restarting, 0, 0));
            _shutdown();
            return new InstallOutcome(InstallStatus.HandedOff,
                $"Installing {release.Version}; Orbweaver will reopen when it finishes.");
        }

        // Called once at launch (before the post-install notice). Returns the
        // pending update iff its installer did not land, so the UI can say "the
        // update did not complete" with an Open log action; null when there was
        // nothing pending or the update took. Clears the marker either way, so
        // the notice shows once, and removes the downloaded installer once it
        // has landed. A failed run keeps its folder, because the log is in it.
        internal static UpdateInstallPlan.WinPendingUpdate? ReconcileAtLaunch(
            string updatesRoot, string currentVersion, Action<string> log,
            Func<string, string>? readFile = null, Action<string>? deleteFile = null, Action<string>? deleteDirectory = null)
        {
            // The file operations are arguments so a test can make each one
            // throw: a locked or read-only file is not something two operating
            // systems can both be made to produce on demand.
            readFile ??= File.ReadAllText;
            deleteFile ??= File.Delete;
            deleteDirectory ??= d => Directory.Delete(d, recursive: true);
            var markerPath = Path.Combine(updatesRoot, UpdateInstallPlan.WinPendingFile);
            string? json = null;
            try { json = File.Exists(markerPath) ? readFile(markerPath) : null; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var aftermath = UpdateInstallPlan.WinDecideAftermath(UpdateInstallPlan.WinParsePending(json), currentVersion);
            if (aftermath.Kind == UpdateInstallPlan.WinAftermathKind.Nothing)
            {
                // An unreadable or empty marker is debris, not a pending update.
                if (json is not null) DeleteQuietly(markerPath, deleteFile);
                return null;
            }

            DeleteQuietly(markerPath, deleteFile);
            var pending = aftermath.Pending!;
            // The marker is a user-writable file, so its log path is not trusted:
            // it is only ever used, deleted or offered to the UI when it lies
            // strictly inside the updates root. Anything else is dropped and the
            // notice shows without an Open log action.
            var folder = FolderInside(updatesRoot, pending.LogPath);
            if (folder is null)
            {
                log($"ignoring the log path in {UpdateInstallPlan.WinPendingFile}: it is not inside {updatesRoot}");
                pending = pending with { LogPath = "" };
            }

            if (aftermath.Kind == UpdateInstallPlan.WinAftermathKind.Completed)
            {
                log($"update to {pending.To} completed (running {currentVersion})");
                if (folder is not null)
                {
                    try { deleteDirectory(folder); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                return null;
            }

            log($"update to {pending.To} did not complete (still running {currentVersion}); setup log {(pending.LogPath.Length > 0 ? pending.LogPath : "unavailable")}");
            return pending;
        }

        // The directory holding `filePath`, when that directory is a strict
        // descendant of `root` once both are made absolute; null otherwise (a
        // relative path, the root itself, a sibling or anything outside it).
        internal static string? FolderInside(string root, string filePath)
        {
            if (!Path.IsPathRooted(filePath)) return null;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (string.IsNullOrEmpty(dir)) return null;
                var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var prefix = rootFull + Path.DirectorySeparatorChar;
                return dir.StartsWith(prefix, StringComparison.Ordinal) ? dir : null;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static void DeleteQuietly(string path, Action<string>? delete = null)
        {
            try { (delete ?? File.Delete)(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // Excluded from coverage: starts the real setup exe, which no test may.
        // Every test drives the constructor with a fake start function.
        [ExcludeFromCodeCoverage]
        internal static UpdateInstallWindows CreateReal(
            DownloadVerified download, string currentVersion, Action<string> log, Action shutdown) =>
            new(download, Environment.ProcessPath,
                Path.Combine(OrbweaverSettings.Directory, "updates"),
                currentVersion, OrbweaverSettings.FlushPendingSave,
                info => Process.Start(info)?.Id ?? throw new InvalidOperationException("Process.Start returned no process"),
                shutdown, log);
    }
}
