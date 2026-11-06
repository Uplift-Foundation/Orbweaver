using System.Text.Json;

namespace Orbweaver
{
    // One OS command, as data: the file to run and its arguments, never a
    // shell string. Every argv the macOS updater runs is built here and
    // asserted literally in tests, so nothing is ever quoted and a path with a
    // space or a quote in it cannot become a second command (CB-308).
    internal sealed record MacCommand(string File, IReadOnlyList<string> Args);

    // The handoff the app writes and the finisher reads. Plain data in a file
    // rather than argv, so a path with spaces needs no quoting at all.
    //
    // **This file is a contract between two different versions**: version N
    // writes it and version N+1's finisher reads it, so Schema says which
    // shape it is. A finisher that does not recognise the number must refuse
    // without touching anything (UpdateInstallMacOS.FinishAsync), and the
    // number only ever changes together with a reader that handles both.
    internal sealed record MacHandoff(
        int Schema,
        string Installed,
        string Staged,
        int Pid,
        string? KeepAlivePlist,
        string Version,
        string MarkerDir);

    // The macOS half of the updater's rules: what to refuse, what to run, and
    // how to read what comes back. Pure by design — no process, no disk — so
    // each decision has a case per outcome and the real hdiutil, codesign,
    // spctl, open and launchctl are only ever named here, never run (CB-308).
    // The Windows half is UpdateInstallPlan.Windows.cs.
    internal static partial class UpdateInstallPlan
    {
        // The Developer ID team that signs Orbweaver. A staged bundle that is
        // not signed by exactly this team is refused: an HttpClient download
        // carries no quarantine attribute, so Gatekeeper never assesses the
        // swapped-in bundle on its own and this is one of the only gates.
        internal const string MacTeamId = "5AQ4ULRG3Z";

        internal const string MacFinishFlag = "--finish-update";

        // The only handoff shape this build writes and understands.
        internal const int MacHandoffSchema = 1;

        // The first release whose bundle answers --finish-update. Every earlier
        // build ignores the flag, starts as a second instance, hits the
        // single-instance mutex and exits, so staging one would only ever end
        // in a 60 second wait and "did not start". The next release after
        // 1.0.3 is the first with the finisher (patch bump). This also
        // refuses a downgrade for free, which is out of scope.
        internal const string MacFirstFinisherVersion = "1.0.4";

        // Null when `releaseVersion` can finish its own update; otherwise why
        // not, in words for the person reading it. Unparseable counts as no.
        internal static string? MacFinisherRefusal(string releaseVersion)
        {
            if (UpdateVersion.TryParse(releaseVersion, out var v)
                && UpdateVersion.TryParse(MacFirstFinisherVersion, out var first)
                && UpdateVersion.Compare(v, first) >= 0)
                return null;
            return $"Version {releaseVersion} is older than the first release that can update itself ({MacFirstFinisherVersion}), so it can't be installed from here. Download it from the Orbweaver releases page instead.";
        }

        // The two files the finisher leaves for the old app to read on its
        // next launch (or for the waiting app to poll for).
        internal const string MacFinishedMarker = "finished";
        internal const string MacFailedMarker = "failed";

        // The `.app` the running executable lives in, or null when it is not
        // inside one (`dotnet run`, a loose binary). The outermost-first walk
        // would be wrong for a bundle nested in another .app; the innermost
        // .app ancestor is the one that owns this executable.
        internal static string? MacAppBundleFor(string? processPath)
        {
            if (string.IsNullOrEmpty(processPath)) return null;
            var path = processPath;
            while (!string.IsNullOrEmpty(path) && path != "/")
            {
                if (path.EndsWith(".app", StringComparison.Ordinal)) return path;
                path = Path.GetDirectoryName(path);
            }
            return null;
        }

        // Refuse before touching anything. `parentWritable` is injected so the
        // decision is testable without a read-only volume. A refusal carries
        // instructions rather than an error code, because the person reading
        // it has to do something about it: there is no privilege escalation.
        internal static (string? Target, string? Refusal) Preflight(string? processPath, Func<string, bool> parentWritable)
        {
            var app = MacAppBundleFor(processPath);
            if (app is null)
                return (null, "This copy of Orbweaver is not running from an app bundle (a loose binary or a developer build), so it can't replace itself. Install the release from the Orbweaver downloads page instead.");
            if (app.Contains("/AppTranslocation/", StringComparison.Ordinal))
                return (null, "macOS is running Orbweaver from a temporary, read-only location. Drag Orbweaver.app into your Applications folder, open it from there, and check for updates again.");
            var parent = Path.GetDirectoryName(app)!;
            if (!parentWritable(parent))
                return (null, $"Your account can't write to {parent}, so Orbweaver can't replace itself there. Ask an administrator to update it, or move Orbweaver.app to a folder you own.");
            return (app, null);
        }

        // ---- argv builders -------------------------------------------------

        internal static MacCommand MacHdiutilAttach(string dmg, string mountpoint) =>
            new("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mountpoint, dmg]);

        internal static MacCommand MacHdiutilDetach(string mountpoint, bool force) =>
            new("/usr/bin/hdiutil", force ? ["detach", "-force", mountpoint] : ["detach", mountpoint]);

        internal static MacCommand MacCodesignVerify(string app) =>
            new("/usr/bin/codesign", ["--verify", "--deep", "--strict", app]);

        // -dv writes to stderr; the exec seam merges the two streams.
        internal static MacCommand MacCodesignDisplay(string app) =>
            new("/usr/bin/codesign", ["-dv", "--verbose=4", app]);

        internal static MacCommand MacSpctl(string app) =>
            new("/usr/sbin/spctl", ["--assess", "--type", "execute", app]);

        internal static MacCommand MacPlistRead(string app, string key) =>
            new("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", System.IO.Path.Combine(app, "Contents", "Info.plist")]);

        // ditto rather than cp -R: it carries the code signature's extended
        // attributes and resource forks, which is what keeps a copied bundle
        // verifying.
        internal static MacCommand MacCopyBundle(string from, string to) =>
            new("/usr/bin/ditto", [from, to]);

        // -n opens a second instance even though the staged copy shares the
        // installed one's bundle id; the handoff path is the argument after
        // the flag. Whether LaunchServices honours the staged path is measured
        // on the mini, not assumed (docs/update-findings.md).
        internal static MacCommand MacOpenFinisher(string stagedApp, string handoffJson) =>
            new("/usr/bin/open", ["-n", stagedApp, "--args", MacFinishFlag, handoffJson]);

        // The fallback if `open -n` misbehaves: launchd runs it in its own
        // job, so it is in no process group of the app's at all.
        internal static MacCommand MacLaunchctlSubmit(string label, string stagedExecutable, string handoffJson) =>
            new("/bin/launchctl", ["submit", "-l", label, "--", stagedExecutable, MacFinishFlag, handoffJson]);

        internal static MacCommand MacLaunchctlUnload(string plist) =>
            new("/bin/launchctl", ["unload", plist]);

        internal static MacCommand MacLaunchctlLoad(string plist) =>
            new("/bin/launchctl", ["load", plist]);

        // CLAUDE_CONFIG_DIR is stripped for the relaunch for the same reason
        // the install script strips it: a launch from a shell inside a Claude
        // session must not hand the new Buddy that session's config root.
        internal static MacCommand MacOpenInstalled(string app) =>
            new("/usr/bin/env", ["-u", "CLAUDE_CONFIG_DIR", "/usr/bin/open", "-a", app]);

        internal static MacCommand MacKeepAliveRepair(string installedApp) =>
            new(System.IO.Path.Combine(installedApp, "Contents", "Resources", "install-hooks.sh"), ["--keepalive-only"]);

        internal static MacCommand MacPsList() =>
            new("/bin/ps", ["-axo", "pid=,uid=,comm="]);

        // ---- parsers -------------------------------------------------------

        // "TeamIdentifier=5AQ4ULRG3Z" out of `codesign -dv`. An ad-hoc bundle
        // prints "TeamIdentifier=not set", which is not a team and comes back
        // as null, so it can never compare equal to ours.
        internal static string? MacParseTeamId(string codesignOutput)
        {
            foreach (var raw in codesignOutput.Split('\n'))
            {
                var line = raw.Trim();
                const string key = "TeamIdentifier=";
                if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
                var value = line[key.Length..].Trim();
                return value.Length == 0 || value == "not set" ? null : value;
            }
            return null;
        }

        // Exactly one .app directly at the image root. Symlinks (the DMG's
        // "Applications" drag target, or a .app symlink) never count: a
        // symlink into the real /Applications would otherwise be mistaken for
        // the payload. Anything but one real bundle is an error with a reason.
        internal static (string? App, string? Error) MacFindSingleApp(IEnumerable<(string Name, bool IsDirectory, bool IsSymlink)> entries)
        {
            var apps = entries
                .Where(e => e.IsDirectory && !e.IsSymlink && e.Name.EndsWith(".app", StringComparison.Ordinal))
                .Select(e => e.Name).ToList();
            return apps.Count switch
            {
                1 => (apps[0], null),
                0 => (null, "The update image contains no app bundle."),
                _ => (null, $"The update image contains {apps.Count} app bundles ({string.Join(", ", apps)}); expected exactly one."),
            };
        }

        // Every process whose executable is `exePath` and which the current
        // user owns, other than `selfPid`. `ps -axo pid=,uid=,comm=` gives
        // `comm` as the full path, which can contain spaces, so it is
        // everything after the second field rather than a third token.
        internal static IReadOnlyList<int> MacParsePs(string output, string exePath, int selfPid, int uid)
        {
            var pids = new List<int>();
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimStart();
                var first = line.IndexOf(' ');
                if (first < 0) continue;
                var rest = line[first..].TrimStart();
                var second = rest.IndexOf(' ');
                if (second < 0) continue;
                if (!int.TryParse(line[..first], out var pid)) continue;
                if (!int.TryParse(rest[..second], out var owner)) continue;
                var comm = rest[second..].Trim();
                if (pid != selfPid && owner == uid && comm == exePath) pids.Add(pid);
            }
            return pids;
        }

        // ---- handoff + marker files ---------------------------------------

        internal static string MacSerializeHandoff(MacHandoff handoff) =>
            JsonSerializer.Serialize(handoff);

        internal static MacHandoff? MacParseHandoff(string json)
        {
            try
            {
                var h = JsonSerializer.Deserialize<MacHandoff>(json);
                // Schema is deliberately not checked here: an unknown schema
                // still has to yield its MarkerDir so the finisher can leave a
                // `failed` marker the old app reads. It is the finisher's call.
                return h is null || string.IsNullOrEmpty(h.Installed) || string.IsNullOrEmpty(h.Staged)
                    || string.IsNullOrEmpty(h.MarkerDir) ? null : h;
            }
            catch (JsonException) { return null; }
        }

        // `Orbweaver.app` -> `Orbweaver.app.old-20261106T101500Z`, beside it,
        // so the rename is on the same volume and atomic.
        internal static string MacOldPath(string installed, DateTimeOffset now) =>
            installed + ".old-" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

        internal static string MacNewPath(string installed) => installed + ".new";
    }
}
