using System.Text;

namespace Orbweaver
{
    // CB-339: the real disk behind BrowseRules. The rules decide; this only answers questions about
    // what is there, in the filesystem's own spelling, and refuses to guess: a name it cannot find is
    // null, a folder it cannot read is null, and nothing here ever throws into the rules.
    //
    // Two things it is careful about because the rules lean on them. A lookup must return the
    // STORED spelling (a different capitalisation, or on Windows an 8.3 short name, is how a lexical
    // check gets walked around) and must not go through an API that normalises the name first, so
    // a Windows `name.` or `name ` is never quietly the folder `name`. And hidden and system entries
    // must be seen (AttributesToSkip is 0), because the rules, not the enumerator, decide what is
    // hidden and what is a system folder.
    internal sealed class BrowseFileSystem : IBrowseFileSystem
    {
        internal BrowseFileSystem(string? home = null, IReadOnlyList<string>? deniedExact = null,
            IReadOnlyList<string>? deniedTrees = null)
        {
            Home = Path.TrimEndingDirectorySeparator(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (deniedExact is null || deniedTrees is null)
            {
                var (exact, trees) = BrowseRules.SystemFolders(IsWindows, Home, WindowsSystemRoot(), Drives(),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf ? pf : @"C:\Program Files",
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) is { Length: > 0 } pf86 ? pf86 : @"C:\Program Files (x86)",
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } pd ? pd : @"C:\ProgramData");
                deniedExact ??= exact;
                deniedTrees ??= trees;
            }
            DeniedExact = deniedExact;
            DeniedTrees = deniedTrees;
        }

        public string Home { get; }
        public bool IsWindows => OperatingSystem.IsWindows();

        // Windows and macOS volumes are case-insensitive by default, which is what the rules compare
        // under. (A case-sensitive APFS volume would make this over-deny, never under-deny.)
        public bool CaseInsensitive => true;
        public IReadOnlyList<string> DeniedExact { get; }
        public IReadOnlyList<string> DeniedTrees { get; }

        private static string WindowsSystemRoot() =>
            Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : @"C:\Windows";

        private static IEnumerable<string> Drives() =>
            OperatingSystem.IsWindows() ? DriveInfo.GetDrives().Select(d => d.Name) : [];

        // `*` and `?` are wildcards to an enumeration pattern, and on Windows so are < > and ". A name
        // holding one is compared by hand across the whole folder instead of being handed over as a
        // pattern.
        private static bool Wild(string name) => name.IndexOfAny(['*', '?', '<', '>', '"']) >= 0;

        private static readonly EnumerationOptions Everything = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            MatchType = MatchType.Simple,
        };

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder buffer, uint size);

        // Windows only: the long name the OS gives an 8.3 alias in `parent`, or null. Enumeration by
        // name does not map an alias on every runtime (measured: .NET 10 on the Windows box does not),
        // so the OS is asked outright.
        private static string? LongNameOf(string parent, string name)
        {
            if (!OperatingSystem.IsWindows() || name.IndexOf('~') < 0) return null;
            var buffer = new StringBuilder(32768);
            var n = GetLongPathNameW(BrowseRules.Join(parent, name, true), buffer, (uint)buffer.Capacity);
            if (n == 0 || n >= buffer.Capacity) return null;
            var resolved = buffer.ToString();
            var folder = Path.GetDirectoryName(resolved);
            if (folder is null || !string.Equals(folder.TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            var longName = Path.GetFileName(resolved);
            return string.Equals(longName, name, StringComparison.OrdinalIgnoreCase) ? null : longName;
        }

        public BrowseEntry? Lookup(string parent, string name) => Lookup(parent, name, true);

        private BrowseEntry? Lookup(string parent, string name, bool tryAlias)
        {
            try
            {
                var wild = Wild(name);
                var options = new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    // Win32 matching is what lets FindFirstFile answer to an 8.3 short name too.
                    MatchType = IsWindows && !wild ? MatchType.Win32 : MatchType.Simple,
                };
                FileSystemInfo? best = null;
                foreach (var info in new DirectoryInfo(parent).EnumerateFileSystemInfos(wild ? "*" : name, options))
                {
                    if (wild && !string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (info.Name == name) return Entry(info);
                    best ??= info;
                }
                if (best is null && tryAlias && LongNameOf(parent, name) is { } longName) return Lookup(parent, longName, false);
                return best is null ? null : Entry(best);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        // A link or junction (LinkTarget answers for both) is a link; any other reparse point, a cloud
        // placeholder for one, is just the folder or file it presents itself as.
        private static BrowseEntry Entry(FileSystemInfo info)
        {
            string? target = null;
            try { target = info.LinkTarget; }
            catch (IOException) { }
            if (target is not null) return new BrowseEntry(info.Name, BrowseKind.Link, StripDevicePrefix(target));
            return new BrowseEntry(info.Name, info.Attributes.HasFlag(FileAttributes.Directory) ? BrowseKind.Dir : BrowseKind.File);
        }

        // A junction's target can come back as \??\C:\x or \\?\C:\x: the same absolute path.
        private static string StripDevicePrefix(string target) =>
            target.StartsWith(@"\??\", StringComparison.Ordinal) || target.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? target[4..]
                : target;

        // Through Lookup rather than Directory.Exists, which on Windows would read `name.` as `name`.
        // A drive root has no parent to look in.
        public bool DirectoryExists(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null) return Directory.Exists(path);
            return Lookup(parent, Path.GetFileName(path)) is { Kind: BrowseKind.Dir };
        }

        public (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap)
        {
            try
            {
                var entries = new List<BrowseEntry>();
                foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Everything))
                {
                    if (entries.Count >= cap) return (entries, true);
                    entries.Add(Entry(info));
                }
                return (entries, false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public bool HiddenAttribute(string folder, string name)
        {
            if (!IsWindows) return false;
            try { return File.GetAttributes(BrowseRules.Join(folder, name, true)).HasFlag(FileAttributes.Hidden); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }

        public bool PersonaAt(string folder) => BrowsePersona.At(folder);
    }

    // What the desk browses under right now, read from the settings and the live sessions: the
    // user's own list when they have made one, else the automatic roots. Read on each request and
    // on each Settings redraw, so editing the list, or a new session changing the automatic ones,
    // takes effect on the next listing without a restart.
    internal static class BrowseSettingsSource
    {
        internal static BrowseSettings Current(IBrowseFileSystem fs) => BrowseRules.Effective(
            OrbweaverSettings.PhoneBrowseRoots, OrbweaverSettings.PhoneBrowseHome, OrbweaverSettings.NewChatRecentFolders,
            LiveFolders(SessionManager.Instance?.AllStatuses.Values ?? Enumerable.Empty<SessionStatus>()), fs);

        // The working folders of the local CLI sessions on this desk: never a peer's, a cloud
        // session's or an OpenClaw conversation's, and never a blank one.
        internal static IEnumerable<string> LiveFolders(IEnumerable<SessionStatus> sessions) =>
            sessions.Where(s => s.IsLocalCli && !string.IsNullOrWhiteSpace(s.Cwd)).Select(s => s.Cwd!);
    }
}
