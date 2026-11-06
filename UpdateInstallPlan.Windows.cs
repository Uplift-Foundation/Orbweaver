using System.Diagnostics;
using System.Text.Json;

namespace Orbweaver
{
    // The Windows half of the updater's rules (CB-308): what to refuse, the
    // exact argv Setup is started with, and how to tell on the next launch
    // whether the update it started actually landed. Pure — no process and no
    // disk beyond the two small marker helpers at the bottom — so every
    // decision has a case per outcome and the real setup.exe is only ever
    // named here, never run. The macOS half is UpdateInstallPlan.MacOS.cs.
    internal static partial class UpdateInstallPlan
    {
        // The file name Setup installs the app as. A process running under any
        // other name is a host (`dotnet.exe`), not an installed copy.
        internal const string WinExeName = "Orbweaver.exe";

        // What the app passes Setup, in order. /RELAUNCH=1 is what tools\
        // Orbweaver.iss gates its silent relaunch entry on; without it a
        // /VERYSILENT run starts nothing afterwards. /CLOSEAPPLICATIONS is the
        // flag the manual-upgrade recipe has always used. The log path is its
        // own element and is never quoted by hand: ProcessStartInfo.ArgumentList
        // quotes an element containing a space, which Inno's parser accepts as
        // one token, so a profile folder like "C:\Users\Ada Lovelace" is safe.
        internal static IReadOnlyList<string> WinSetupArgs(string logPath) =>
        [
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/CLOSEAPPLICATIONS",
            "/RELAUNCH=1",
            "/LOG=" + logPath,
        ];

        // UseShellExecute=false is deliberate: ShellExecute would try to show a
        // UAC prompt or a SmartScreen dialog for an unsigned installer, while a
        // direct CreateProcess runs it as the current user (the installer is
        // per-user, PrivilegesRequired=lowest) and never asks.
        internal static ProcessStartInfo WinSetupStartInfo(string setupPath, string logPath)
        {
            var info = new ProcessStartInfo(setupPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? "",
            };
            foreach (var arg in WinSetupArgs(logPath)) info.ArgumentList.Add(arg);
            return info;
        }

        // Refuse before touching anything. A copy that is not the installed
        // Orbweaver.exe (a developer build run through `dotnet`, a renamed
        // binary) cannot be sure Setup will replace *it*, so it says so
        // instead of installing a second copy beside it.
        internal static string? WinPreflight(string? processPath)
        {
            // Split on both separators by hand: Path.GetFileName only knows the
            // running OS's, and this decision is unit-tested on macOS too.
            var name = processPath?[(processPath.LastIndexOfAny(['\\', '/']) + 1)..];
            if (string.IsNullOrWhiteSpace(processPath)
                || !string.Equals(name, WinExeName, StringComparison.OrdinalIgnoreCase))
                return "This copy of Orbweaver is not the installed app (a developer build or a renamed file), so it can't update itself. Download the installer from the Orbweaver releases page and run it instead.";
            return null;
        }

        // ---- the next-launch decision ------------------------------------------

        // Written just before Setup starts, read on the next launch. Judging by
        // "is the running version now the target" rather than by whether a log
        // exists: Setup writes its log on a successful run too, so "log exists
        // and the version is unchanged" cannot tell the two apart once the new
        // build has recorded itself as the last-run version.
        internal sealed record WinPendingUpdate(string From, string To, string LogPath);

        internal enum WinAftermathKind { Nothing, Completed, DidNotComplete }

        internal sealed record WinAftermath(WinAftermathKind Kind, WinPendingUpdate? Pending);

        internal static WinAftermath WinDecideAftermath(WinPendingUpdate? pending, string currentVersion)
        {
            if (pending is null) return new(WinAftermathKind.Nothing, null);
            // A target or current version that does not parse is treated as
            // "did not land": showing a reader a log they did not need is a
            // smaller error than telling them nothing happened was fine.
            var landed = UpdateVersion.TryParse(pending.To, out var to)
                         && UpdateVersion.TryParse(currentVersion, out var now)
                         && UpdateVersion.Compare(now, to) >= 0;
            return new(landed ? WinAftermathKind.Completed : WinAftermathKind.DidNotComplete, pending);
        }

        internal const string WinPendingFile = "pending.json";
        internal const string WinSetupLogFile = "setup.log";

        internal static string WinPendingJson(WinPendingUpdate pending) =>
            JsonSerializer.Serialize(pending);

        internal static WinPendingUpdate? WinParsePending(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var p = JsonSerializer.Deserialize<WinPendingUpdate>(json);
                return p is { From.Length: > 0, To.Length: > 0, LogPath.Length: > 0 } ? p : null;
            }
            catch (JsonException) { return null; }
        }
    }
}
