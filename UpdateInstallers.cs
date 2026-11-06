using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // The one place the UI asks "what installs an update on this machine?"
    // (CB-308). The notice window calls ForCurrentPlatform and nothing else, so
    // it never touches a platform installer's internals, and each platform's
    // owner changes only its own arm.
    internal static class UpdateInstallers
    {
        // Null means this platform has no installer, and the caller should
        // offer "Download manually" instead of an Install button.
        internal static IUpdateInstaller? ForCurrentPlatform(Action<string> log) =>
            For(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(),
                () => CreateMacOS(log),
                // Windows arm: UpdateInstallWindows, owned by the Windows
                // engineer. Until it exists this answers "no installer".
                () => null);

        // The choice, with the construction passed in, so each arm is a case
        // and neither real installer is built by a test.
        internal static IUpdateInstaller? For(
            bool isMac, bool isWindows, Func<IUpdateInstaller> mac, Func<IUpdateInstaller?> windows) =>
            isMac ? mac() : isWindows ? windows() : null;

        // Excluded from coverage: composes the real downloader, the real
        // process runner and the real folders, none of which a suite may use.
        // Every piece of it is covered where it is defined; what is left here
        // is the choice of which real thing to hand each seam.
        [ExcludeFromCodeCoverage]
        private static IUpdateInstaller CreateMacOS(Action<string> log)
        {
            // Lives as long as the process, like the installer that owns it.
            var http = new HttpUpdateHttp();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new MacUpdateInstaller(
                (release, directory, progress, ct) =>
                    UpdateDownloader.DownloadVerifiedAsync(release, UpdateAssets.CurrentRid(), directory, http, progress, ct),
                Environment.ProcessPath,
                IsWritableFolder,
                Path.Combine(OrbweaverSettings.Directory, "updates"),
                Path.Combine(home, "Library", "LaunchAgents", Brand.MacBundleId + ".plist"),
                UpdateInstallMacOS.RealExec,
                () => Task.Delay(TimeSpan.FromMilliseconds(500)),
                log,
                Environment.ProcessId);
        }

        // A permission bit lies about ACLs and read-only volumes; creating a
        // file is the answer to "can this account replace something here".
        [ExcludeFromCodeCoverage]
        private static bool IsWritableFolder(string directory)
        {
            try
            {
                var probe = Path.Combine(directory, ".orbweaver-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
