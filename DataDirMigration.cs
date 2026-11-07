using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Orbweaver
{
    // Moves the per-user data and log folders from Brand.Legacy.DataDirName to
    // Brand.DataDirName on the first launch after the rename (CB-255 §2).
    //
    // **The thing this must never do is lose settings.json.** Silently erasing
    // settings is the worst bug this app has shipped, and a migration is the
    // easiest place to ship it again: a move that half-completes, a second
    // process racing the first, a crash between two copies. So the shape is
    // chosen around that one file:
    //
    //   - A move where a move is possible. Directory.Move on one volume is a
    //     rename — all or nothing — and it is the only option that does not
    //     double ~130 MB of speech engine and every cloned .app bundle on disk.
    //   - A copy only when the move refuses (another volume, a file held open),
    //     and then settings.json is copied **last**, so its presence in the new
    //     folder is the commit record. A crash mid-copy leaves "new folder, no
    //     settings.json", which the next launch recognises and resumes; it can
    //     never leave a new settings.json beside a half-copied rest. Each file
    //     goes to a ".partial" name first and is renamed into place, so a crash
    //     mid-file cannot leave a torn file that the resume then skips as
    //     already present.
    //   - New wins whenever the new folder already has settings.json. That is
    //     both "a dev build ran before the upgrade" and "the second launch after
    //     a successful move", and the rule does not need to tell them apart.
    //   - After a move, the legacy folder is recreated holding copies of
    //     settings.json and peer-identity.json plus a note, so a downgraded
    //     build still finds its settings and its TLS identity (pairing
    //     survives). The legacy folder is never deleted on the copy path —
    //     it is left whole.
    //
    // It runs from Startup.Run's `migrateUserData` step: after both mutex names
    // are held (so no second instance, old or new, can be moving the same
    // folder) and before anything reads OrbweaverSettings, PeerIdentity or
    // NeuralSpeech. Checked rather than assumed for the two steps that run
    // before it: neither CrashLog.cs nor SingleInstance.cs refers to
    // OrbweaverSettings at all. CrashLog.Install does not create its
    // directory either — only a write does — but a crash or a persona refusal
    // could have created the new Logs folder before now, which is why the Logs
    // rule below is a merge rather than move-or-nothing.
    //
    // Does nothing at all while ORBWEAVER_SETTINGS_DIR, ORBWEAVER_LOG_DIR
    // or ORBWEAVER_BUNDLE_ROOT is set. Every test suite sets the first, and
    // a migration that ran under it would move a developer's real folder.
    internal static class DataDirMigration
    {
        internal enum RootKind
        {
            // settings.json, peer-identity.json, speech-engine/, voices/, and on
            // macOS bundles/. Its commit record is settings.json.
            Data,

            // crash.log and friends. Nothing in here is precious enough to need
            // a commit record: "the folder exists" stands in for it.
            Logs,
        }

        internal enum Operation
        {
            // Directory.Move, falling back to a copy (settings.json last) on
            // IOException or UnauthorizedAccessException.
            Move,

            // Recreate the legacy folder with the two state files and a note.
            // Only acts if the Move before it really moved — a copy leaves the
            // legacy folder whole, so there is nothing to snapshot.
            Snapshot,

            // Copy what the new folder is missing (settings.json last). For
            // Logs, a file in both is replaced by the legacy copy only when the
            // legacy one is newer.
            Merge,

            // New has settings.json: leave both folders exactly as they are.
            KeepNew,
        }

        internal sealed record Root(RootKind Kind, string Legacy, string New);

        internal const string SettingsFile = "settings.json";
        internal const string PeerIdentityFile = "peer-identity.json";
        internal const string MarkerFile = "MIGRATED-TO-ORBWEAVER.txt";
        internal const string PartialSuffix = ".orbweaver-partial";
        internal const string LogFile = "migration.log";

        // The bundle cache (macOS). Regenerable — ClaudeDesktopBundles clones
        // it again from /Applications/Claude.app — and made of whole .app
        // bundles full of symlinks that a file-by-file copy would flatten or
        // skip, leaving a clone that looks present and is broken. A move takes
        // it intact; a copy leaves it behind and lets the app re-clone.
        internal const string BundlesFolder = "bundles";

        // What the snapshot carries back into the legacy folder after a move:
        // exactly what a downgraded build needs to keep its settings and its
        // peer pairing. Everything else it can rebuild (speech engine, voices,
        // bundles) or does not need.
        internal static readonly IReadOnlyList<string> SnapshotFiles = [SettingsFile, PeerIdentityFile];

        // BrandEnv suffixes, so either spelling of any of the three counts.
        internal static readonly IReadOnlyList<string> OverrideVariables =
            [BrandEnv.SettingsDir, BrandEnv.LogDir, BrandEnv.BundleRoot];

        // ---- the rules ------------------------------------------------------

        // The whole decision for one root, with no filesystem behind it.
        internal static IReadOnlyList<Operation> Plan(
            RootKind kind, bool legacyExists, bool newExists, bool newHasSettings, bool overrideSet)
        {
            if (overrideSet || !legacyExists) return [];

            if (!newExists)
                return kind == RootKind.Data ? [Operation.Move, Operation.Snapshot] : [Operation.Move];

            if (kind == RootKind.Data && newHasSettings) return [Operation.KeepNew];

            return [Operation.Merge];
        }

        internal static bool OverrideSet(Func<string, string?> environment) =>
            OverrideVariables.Any(suffix => BrandEnv.Get(suffix, environment) is not null);

        // Where the two roots are on each platform, given the folders the OS
        // reports. Each new path is built exactly as its reader builds it —
        // OrbweaverSettings.Directory for the data root, CrashLog's default
        // directory for Logs — so the migration fills precisely the folder the
        // app is about to read.
        //
        // Logs first, deliberately: the migration's own log line is written
        // into CrashLog.Directory, and writing it before the Logs root had been
        // looked at would create the new Logs folder and turn a clean move into
        // a merge. (The lines are also buffered to the end of Run for the same
        // reason; the order makes that belt rather than the only brace.)
        //
        // On Windows only the Logs *subfolder* moves: %LOCALAPPDATA%\ClaudeBuddy
        // also holds the legacy hook script, which running Claude Code sessions
        // still call until LegacyHookCleanup retires the folder.
        internal static IReadOnlyList<Root> Roots(
            bool onWindows, string applicationData, string localApplicationData, string userProfile)
        {
            var logs = onWindows
                ? new Root(RootKind.Logs,
                    Path.Combine(localApplicationData, Brand.Legacy.DataDirName, "Logs"),
                    Path.Combine(localApplicationData, Brand.DataDirName, "Logs"))
                : new Root(RootKind.Logs,
                    Path.Combine(userProfile, "Library", "Logs", Brand.Legacy.DataDirName),
                    Path.Combine(userProfile, "Library", "Logs", Brand.DataDirName));

            var data = new Root(RootKind.Data,
                Path.Combine(applicationData, Brand.Legacy.DataDirName),
                Path.Combine(applicationData, Brand.DataDirName));

            return [logs, data];
        }

        // Whether one file from the legacy tree is copied into the new one.
        // A file the new folder lacks always is. One it already has is kept,
        // except in Logs, where the newer of the two survives — a crash.log
        // written by the old build last week beats one a dev build wrote last
        // month, and the reverse.
        internal static bool ShouldCopy(RootKind kind, bool existsInNew, bool legacyIsNewer) =>
            !existsInNew || (kind == RootKind.Logs && legacyIsNewer);

        // Paths (relative to the legacy root) a copy never carries: the
        // snapshot's own note, a ".partial" left by an interrupted copy, and on
        // the data root the bundle cache (see BundlesFolder).
        internal static bool Skipped(RootKind kind, string relative) =>
            relative == MarkerFile
            || relative.EndsWith(PartialSuffix, StringComparison.Ordinal)
            || (kind == RootKind.Data
                && (relative == BundlesFolder
                    || relative.StartsWith(BundlesFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)));

        // settings.json last — the commit record — and everything else in the
        // order it came.
        internal static IEnumerable<string> CopyOrder(IEnumerable<string> relative) =>
            relative.OrderBy(path => path == SettingsFile ? 1 : 0);

        internal static string MarkerText(string movedTo, DateTimeOffset when) =>
            $"""
            Orbweaver (formerly Claude Buddy) moved this folder to:

                {movedTo}

            on {when:yyyy-MM-dd HH:mm zzz}.

            What is left here is a snapshot of settings.json and peer-identity.json,
            so that an older Claude Buddy build, if you run one again, still finds
            your settings and its pairing with your other machines. Orbweaver itself
            never reads this folder again. It is safe to delete.

            """;

        // ---- the executor ---------------------------------------------------

        internal static void Run() => Run(OverrideSet(Environment.GetEnvironmentVariable), RealRoots);

        // The executor, with every effect it has on the world injectable so the
        // tests can drive the failure arms (a move that refuses, a copy that dies
        // on the Nth file) against temp folders. Never throws: the worst allowed
        // outcome is that nothing moved and the app starts on defaults this
        // once, with a line in migration.log saying why, and tries again next
        // launch.
        internal static void Run(
            bool overrideSet,
            Func<IReadOnlyList<Root>> roots,
            Action<string, string>? move = null,
            Action<string, string>? copy = null,
            Action<string>? log = null,
            Action? sweepChimes = null)
        {
            var lines = new List<string>();

            try
            {
                move ??= (from, to) => Directory.Move(from, to);
                copy ??= (from, to) => File.Copy(from, to, overwrite: true);

                foreach (var root in roots())
                    Migrate(root, overrideSet, move, copy, lines.Add);

                // The scaled-chime cache under the old name. Regenerable, never
                // read again, so it is deleted rather than migrated.
                if (!overrideSet) (sweepChimes ?? (Action)AudioVolume.SweepLegacyChimeCache)();
            }
            catch (Exception error)
            {
                lines.Add($"migration stopped: {error.GetType().Name}: {error.Message}");
            }

            // Written at the end, not as they happen, so the first line cannot
            // create the new Logs folder before the Logs root is decided.
            try
            {
                var sink = log ?? (line => AppendLog(CrashLog.Directory, line));
                foreach (var line in lines) sink(line);
            }
            catch
            {
                // A diagnostic that can break startup is worse than none.
            }
        }

        private static void Migrate(
            Root root, bool overrideSet, Action<string, string> move, Action<string, string> copy, Action<string> log)
        {
            try
            {
                var operations = Plan(
                    root.Kind,
                    legacyExists: Directory.Exists(root.Legacy),
                    newExists: Directory.Exists(root.New),
                    newHasSettings: File.Exists(Path.Combine(root.New, SettingsFile)),
                    overrideSet);

                var moved = false;

                foreach (var operation in operations)
                {
                    switch (operation)
                    {
                        case Operation.Move:
                            moved = MoveOrCopy(root, move, copy, log);
                            break;

                        case Operation.Snapshot when moved:
                            Snapshot(root, copy);
                            log($"left a snapshot of settings in {root.Legacy}");
                            break;

                        case Operation.Merge:
                            CopyTree(root, copy);
                            log($"merged {root.Legacy} into {root.New}");
                            break;

                        case Operation.KeepNew:
                            // Every launch after a successful move lands here,
                            // so it goes to the opt-in mirror log rather than
                            // migration.log, which would otherwise gain a line
                            // per launch forever.
                            MirrorLog.SayOnce("data-dir", $"{root.New} has settings.json; {root.Legacy} left alone");
                            break;
                    }
                }
            }
            catch (Exception error)
            {
                // Including a copy that died part-way: the new folder is left
                // without settings.json, so the next launch resumes the copy.
                log($"migrating {root.Legacy} failed: {error.GetType().Name}: {error.Message}");
            }
        }

        // True if the folder was moved, false if it had to be copied.
        private static bool MoveOrCopy(
            Root root, Action<string, string> move, Action<string, string> copy, Action<string> log)
        {
            // Directory.Move needs the destination's parent. On Windows the Logs
            // destination is %LOCALAPPDATA%\Orbweaver\Logs, whose parent is new.
            Directory.CreateDirectory(Path.GetDirectoryName(root.New)!);

            try
            {
                move(root.Legacy, root.New);
                log($"moved {root.Legacy} to {root.New}");
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"could not move {root.Legacy} ({error.GetType().Name}: {error.Message}); copying instead");
                CopyTree(root, copy);
                log($"copied {root.Legacy} to {root.New}");
                return false;
            }
        }

        // Copy everything the new folder needs from the legacy one, settings.json
        // last. Re-entrant: what is already there is skipped (or, for Logs,
        // replaced only by something newer), so a second run after a crash
        // finishes the job instead of starting it again.
        private static void CopyTree(Root root, Action<string, string> copy)
        {
            // Symlinks are skipped rather than followed or flattened; the only
            // place they are expected is inside cloned bundles, which a copy
            // leaves behind anyway.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            Directory.CreateDirectory(root.New);

            // Folders first, so an empty voices/ survives too.
            foreach (var folder in Directory.EnumerateDirectories(root.Legacy, "*", options))
            {
                var relative = Path.GetRelativePath(root.Legacy, folder);
                if (!Skipped(root.Kind, relative)) Directory.CreateDirectory(Path.Combine(root.New, relative));
            }

            var files = Directory.EnumerateFiles(root.Legacy, "*", options)
                .Select(file => Path.GetRelativePath(root.Legacy, file))
                .Where(relative => !Skipped(root.Kind, relative))
                .ToList();

            foreach (var relative in CopyOrder(files))
            {
                var from = Path.Combine(root.Legacy, relative);
                var to = Path.Combine(root.New, relative);
                var existsInNew = File.Exists(to);
                var legacyIsNewer = existsInNew && File.GetLastWriteTimeUtc(from) > File.GetLastWriteTimeUtc(to);

                if (!ShouldCopy(root.Kind, existsInNew, legacyIsNewer)) continue;

                var partial = to + PartialSuffix;
                copy(from, partial);
                File.Move(partial, to, overwrite: true);
            }
        }

        private static void Snapshot(Root root, Action<string, string> copy)
        {
            Directory.CreateDirectory(root.Legacy);

            foreach (var name in SnapshotFiles)
            {
                var from = Path.Combine(root.New, name);
                if (File.Exists(from)) copy(from, Path.Combine(root.Legacy, name));
            }

            File.WriteAllText(Path.Combine(root.Legacy, MarkerFile), MarkerText(root.New, DateTimeOffset.Now));
        }

        // One line per thing the migration did, next to the crash log — the
        // place a person looking for "where did my settings go" will look.
        internal static void AppendLog(string directory, string line) =>
            AppendLog(directory, LogFile, line);

        // The same writer for another log in the same folder. CB-308's updater
        // keeps its own update.log, because a person asking "why did my update
        // not take" should not have to read it out of a migration log.
        internal static void AppendLog(string directory, string fileName, string line)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, fileName),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
            }
            catch
            {
                // Same rule as CrashLog: never fail while writing it down.
            }
        }

        // Excluded from coverage: resolves the real user folders. Every test
        // drives Roots with temp folders instead, and every suite sets the
        // overrides that keep Run() from acting on these.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<Root> RealRoots() =>
            Roots(
                OperatingSystem.IsWindows(),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
