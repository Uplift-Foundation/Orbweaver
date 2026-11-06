using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace Orbweaver
{
    // The seam between the updater's core and the app's windows (CB-308): owns the
    // one UpdateChecker, decides what each of its results should put on screen,
    // and builds the platform installer the notice calls.
    //
    // Split so the decision is testable and the wiring is not pretended to be:
    // Route and OnCompleted are pure over their inputs and seams, covered in
    // UpdateServicesTests; Start and the real installer composition are the
    // parts that reach GitHub, real processes and a real dispatcher, and are
    // excluded for exactly that reason.
    internal static class UpdateServices
    {
        internal static UpdateChecker? Checker { get; set; }

        // What a result should do. Kept as data so every arm is a case.
        internal enum Step { None, OpenNotice, ShowMessage }

        internal sealed record Routed(Step Do, string Title = "", string Message = "");

        // The decision. A manual check always answers, because the person asked and
        // silence reads as a dead menu item; an automatic one only ever surfaces an
        // available update (the checker already keeps its failures to the log).
        // `alreadyOffered` stops the same release opening the window again on every
        // daily check in one session, which would be nagging by schedule.
        internal static Routed Route(UpdateCheckResult result, string currentVersion, ISet<string> alreadyOffered)
        {
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.Available when result.Release is { } release:
                    if (!result.Manual && !alreadyOffered.Add(release.Tag)) return new Routed(Step.None);
                    alreadyOffered.Add(release.Tag);
                    return new Routed(Step.OpenNotice);

                case UpdateCheckOutcome.Failed when result.Manual:
                    return new Routed(Step.ShowMessage, "Couldn't check for updates",
                        result.Error ?? "The update check failed.");

                case UpdateCheckOutcome.UpToDate when result.Manual:
                    return new Routed(Step.ShowMessage, "You're up to date",
                        $"{Brand.DisplayName} {currentVersion} is the latest version.");

                default:
                    return new Routed(Step.None);
            }
        }

        private static readonly HashSet<string> Offered = new(StringComparer.Ordinal);

        // Test seams: a headless run shows no windows.
        internal static Action<UpdateRelease>? OfferForTests;
        internal static Action<string, string>? MessageForTests;

        internal static void OnCompleted(UpdateCheckResult result, string currentVersion)
        {
            var routed = Route(result, currentVersion, Offered);
            switch (routed.Do)
            {
                case Step.OpenNotice:
                    Offer(result.Release!);
                    break;
                case Step.ShowMessage:
                    (MessageForTests ?? ShowMessage)(routed.Title, routed.Message);
                    break;
            }
        }

        internal static void Offer(UpdateRelease release) =>
            (OfferForTests ?? OfferReal)(release);

        [ExcludeFromCodeCoverage]
        private static void OfferReal(UpdateRelease release) =>
            UpdateNoticeWindow.Toggle(release, Installer());

        [ExcludeFromCodeCoverage]
        private static void ShowMessage(string title, string message) =>
            UpdateMessageWindow.Toggle(title, message, offerLocalNetwork: false);

        // The Settings window's own "Check now". Awaited there so the answer can be
        // written beside the button; the same result also reaches OnCompleted via
        // the checker's event, which is what opens the notice if one is available.
        [ExcludeFromCodeCoverage]
        internal static async Task<UpdateCheckResult> CheckNowAsync() =>
            Checker is { } checker
                ? await checker.CheckNowAsync()
                : new UpdateCheckResult(UpdateCheckOutcome.Failed, null, "Updates are not available in this run.", true);

        internal static void Reschedule() => Checker?.Reschedule();
        internal static void ClearSkipped()
        {
            if (Checker is { } checker) checker.ClearSkipped();
            else OrbweaverSettings.SkippedUpdateVersion = null;
        }

        internal static void Skip(UpdateRelease release)
        {
            if (Checker is { } checker) checker.Skip(release);
            else OrbweaverSettings.SkippedUpdateVersion = release.Version;
        }

        // Called once from App startup. Everything the checker raises is on a pool
        // thread, so it is posted to the UI thread before it touches a window or
        // the tray.
        [ExcludeFromCodeCoverage]
        internal static void Start()
        {
            var checker = UpdateChecker.Create(Log);
            Checker = checker;

            checker.PendingChanged += () => Dispatcher.UIThread.Post(() => TrayController.Instance?.Refresh());
            checker.Completed += result =>
                Dispatcher.UIThread.Post(() => OnCompleted(result, checker.CurrentVersion));

            checker.Start();
        }

        [ExcludeFromCodeCoverage]
        internal static void Log(string line) =>
            DataDirMigration.AppendLog(CrashLog.Directory, "update.log", line);

        // The platform installer, composed from real parts. Excluded: this is the
        // one place the updater is wired to the real downloader, real processes and
        // the real filesystem, each of which is faked in the suites that cover the
        // installers themselves.
        [ExcludeFromCodeCoverage]
        internal static IUpdateInstaller Installer()
        {
            if (OperatingSystem.IsMacOS()) return MacInstaller();
            return WindowsInstaller();
        }

        [ExcludeFromCodeCoverage]
        private static IUpdateInstaller MacInstaller()
        {
            var plist = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", Brand.MacBundleId + ".plist");

            return new MacUpdateInstaller(
                (release, directory, progress, ct) => UpdateDownloader.DownloadVerifiedAsync(
                    release, UpdateAssets.CurrentRid(), directory, new HttpUpdateHttp(), progress, ct),
                Environment.ProcessPath,
                ParentWritable,
                Path.Combine(OrbweaverSettings.Directory, "updates"),
                File.Exists(plist) ? plist : null,
                UpdateInstallMacOS.RealExec,
                () => Task.Delay(500),
                Log,
                Environment.ProcessId);
        }

        // The Windows installer is composed by its own file (CB-308, Windows half).
        [ExcludeFromCodeCoverage]
        private static IUpdateInstaller WindowsInstaller() =>
            throw new PlatformNotSupportedException("The Windows installer is not wired yet.");

        [ExcludeFromCodeCoverage]
        private static bool ParentWritable(string directory)
        {
            try
            {
                var probe = Path.Combine(directory, $".orbweaver-write-test-{Guid.NewGuid():N}");
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
