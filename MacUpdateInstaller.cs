namespace Orbweaver
{
    // The macOS IUpdateInstaller (CB-308): preflight, download, stage, hand off
    // to the finisher, wait. Everything with a side effect is a constructor
    // argument so a test drives the whole sequence against a scratch folder;
    // the wiring to real processes and the real downloader is the one place
    // that is not (see App's composition, and UpdateInstallMacOS.RealExec).
    internal sealed class MacUpdateInstaller : IUpdateInstaller
    {
        // Downloads the dmg and the checksum list, verifies the checksum, and
        // returns the dmg's path. The core's downloader owns that rule; this
        // class never re-implements it.
        internal delegate Task<string> DownloadVerified(
            UpdateRelease release, string directory, IProgress<UpdateProgress> progress, CancellationToken ct);

        private readonly DownloadVerified _download;
        private readonly string? _processPath;
        private readonly Func<string, bool> _parentWritable;
        private readonly string _updatesRoot;
        private readonly string? _keepAlivePlist;
        private readonly MacExec _exec;
        private readonly Func<Task> _pollDelay;
        private readonly Action<string> _log;
        private readonly int _pid;

        internal MacUpdateInstaller(
            DownloadVerified download, string? processPath, Func<string, bool> parentWritable,
            string updatesRoot, string? keepAlivePlist, MacExec exec, Func<Task> pollDelay,
            Action<string> log, int pid)
        {
            _download = download;
            _processPath = processPath;
            _parentWritable = parentWritable;
            _updatesRoot = updatesRoot;
            _keepAlivePlist = keepAlivePlist;
            _exec = exec;
            _pollDelay = pollDelay;
            _log = log;
            _pid = pid;
        }

        public async Task<InstallOutcome> InstallAsync(
            UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            var (target, refusal) = UpdateInstallPlan.Preflight(_processPath, _parentWritable);
            if (target is null)
            {
                _log($"refused: {refusal}");
                return new InstallOutcome(InstallStatus.Refused, refusal!);
            }

            // Before a byte is downloaded: a release that predates the finisher
            // would ignore --finish-update and exit on the mutex.
            var tooOld = UpdateInstallPlan.MacFinisherRefusal(release.Version);
            if (tooOld is not null)
            {
                _log($"refused: {tooOld}");
                return new InstallOutcome(InstallStatus.Refused, tooOld);
            }

            var directory = UpdateInstallPlan.JoinPosix(_updatesRoot, release.Version);
            Directory.CreateDirectory(directory);
            string dmg;
            try
            {
                dmg = await _download(release, directory, progress, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _log($"download failed: {e.Message}");
                return new InstallOutcome(InstallStatus.Failed, $"The download did not complete: {e.Message}");
            }

            progress.Report(new UpdateProgress(UpdateStage.Staging, 0, 0));
            var (staged, error) = UpdateInstallMacOS.Stage(dmg, directory, release.Version, _exec, _log);
            if (staged is null)
            {
                _log($"stage refused: {error}");
                return new InstallOutcome(InstallStatus.Failed, error!);
            }

            progress.Report(new UpdateProgress(UpdateStage.Restarting, 0, 0));
            var handoff = new MacHandoff(UpdateInstallPlan.MacHandoffSchema, target, staged, _pid, _keepAlivePlist, release.Version, directory);
            return await UpdateInstallMacOS.LaunchAsync(handoff, _exec, _pollDelay, _log);
        }
    }
}
