using System.Diagnostics.CodeAnalysis;
using System.Reflection;

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
            For(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), log, CreateMacOS, CreateWindows);

        // The choice, with the construction passed in, so each arm is a case
        // and neither real installer is built by a test.
        internal static IUpdateInstaller? For(
            bool isMac, bool isWindows, Action<string> log,
            Func<Action<string>, IUpdateInstaller> mac, Func<Action<string>, IUpdateInstaller?> windows) =>
            isMac ? mac(log) : isWindows ? windows(log) : null;

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

        // Excluded from coverage: the real downloader, the real settings folder
        // and the real desktop lifetime. The shutdown is posted to the UI thread
        // because the installer calls it from the notice's async continuation.
        [ExcludeFromCodeCoverage]
        private static IUpdateInstaller CreateWindows(Action<string> log)
        {
            var http = new HttpUpdateHttp();
            var current = UpdateChecker.ResolveCurrentVersion(
                BrandEnv.Get(BrandEnv.UpdateCurrentVersion, Environment.GetEnvironmentVariable),
                typeof(UpdateChecker).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                out _);
            return UpdateInstallWindows.CreateReal(
                (release, directory, progress, ct) =>
                    UpdateDownloader.DownloadVerifiedAsync(release, UpdateAssets.CurrentRid(), directory, http, progress, ct),
                current, log,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    (Avalonia.Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown()));
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
