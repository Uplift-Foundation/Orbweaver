using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // CB-363: where a file fetched from another machine over the peer link is
    // kept, so a second click on the same link opens what is already here
    // instead of pulling it again.
    //
    // Layout: <root>/<key>/<mtimeMs>-<size>/<SafeName>, where key is the first
    // sixteen hex digits of sha256("<machine>|<path>"). The far path never
    // becomes a local path — only its hash and a sanitised last component do —
    // so nothing a chat message says can steer where a file lands here.
    //
    // A cached copy is offered back to the far side as `have` (its newest
    // entry's stamp); the far side answers `same` when the file there still has
    // that stamp, and only then is the copy here reused.
    //
    // Every file is written as a temp file and renamed into place, so a
    // half-written file is never what a click opens; mode 0600, so it is never
    // executable and never readable by another user; and marked as downloaded
    // (IDownloadMarker) before the rename, so there is no instant in which the
    // final path exists unmarked.
    internal sealed class PeerFileCache
    {
        internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        internal const long MaxTotalBytes = 500L * 1024 * 1024;

        // A temp file this old is from a write that never finished.
        internal static readonly TimeSpan StaleTemp = TimeSpan.FromHours(1);

        private const string TempSuffix = ".part";

        // One cached copy.
        internal sealed record CachedFile(string LocalPath, long MtimeMs, long Size)
        {
            public string Have => MirrorProtocol.FileStamp(MtimeMs, Size);
        }

        private readonly string _root;
        private readonly Func<DateTime> _utcNow;
        private readonly IDownloadMarker _marker;
        private readonly object _gate = new();

        internal PeerFileCache(string root, Func<DateTime>? utcNow = null, IDownloadMarker? marker = null)
        {
            _root = root;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _marker = marker ?? OsDownloadMarker.Instance;
        }

        internal string Root => _root;

        // The app's own cache, under the same support directory settings.json
        // lives in, and moved by the same ORBWEAVER_SETTINGS_DIR a test sets.
        internal static PeerFileCache Shared { get; } = new(DefaultRoot());

        internal static string DefaultRoot() =>
            Path.Combine(
                BrandEnv.Get(BrandEnv.SettingsDir) is { Length: > 0 } scratch
                    ? scratch
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Brand.DataDirName),
                "peer-files");

        // --- naming ------------------------------------------------------------

        internal static string Key(string machine, string path) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine + "|" + path)))[..16].ToLowerInvariant();

        private static readonly string[] WindowsDeviceNames =
            ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
             "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

        // The far path's last component, made safe to be a file name on either
        // OS: separators and the characters Windows refuses become `_`,
        // control and format characters are dropped, trailing dots and spaces
        // (which Windows strips) go, a device name gets a leading `_`, and
        // a name longer than 120 characters keeps its extension. The extension
        // is what decides which app opens it, so it is kept wherever it can be.
        internal static string SafeName(string path)
        {
            var cut = path.LastIndexOfAny(['/', '\\']);
            var name = cut >= 0 ? path[(cut + 1)..] : path;

            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
                sb.Append(c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\' ? '_' : c);
            }

            var safe = sb.ToString().TrimEnd('.', ' ').TrimStart(' ');
            if (safe.Length == 0 || safe == "." || safe == "..") safe = "file";

            var stem = safe.Split('.')[0];
            if (WindowsDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;

            if (safe.Length > 120)
            {
                var ext = Path.GetExtension(safe);
                if (ext.Length > 20) ext = "";
                safe = safe[..(120 - ext.Length)] + ext;
            }

            // A leading dot would make it hidden, and the sweep's temp files
            // start with one.
            if (safe[0] == '.') safe = "_" + safe;
            return safe;
        }

        private string KeyDir(string machine, string path) => Path.Combine(_root, Key(machine, path));

        // --- reading -------------------------------------------------------------

        // The newest complete copy of this machine's file, or null. Newest by
        // the far file's mtime: the stamp that is worth offering as `have`. An
        // entry whose file is missing or the wrong length is not a copy.
        internal CachedFile? Newest(string machine, string path)
        {
            var dir = KeyDir(machine, path);
            var name = SafeName(path);
            CachedFile? best = null;

            lock (_gate)
            {
                if (!Directory.Exists(dir)) return null;

                foreach (var entry in SafeEnumerate(dir))
                {
                    if (MirrorProtocol.ParseFileStamp(Path.GetFileName(entry)) is not { } stamp) continue;
                    var file = Path.Combine(entry, name);
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length != stamp.Size) continue;
                    if (best is null || stamp.MtimeMs > best.MtimeMs) best = new CachedFile(file, stamp.MtimeMs, stamp.Size);
                }
            }

            return best;
        }

        // Records that a copy was just used, so the age sweep counts from its
        // last use rather than its first fetch.
        internal void Touch(CachedFile file)
        {
            lock (_gate)
            {
                var entry = Path.GetDirectoryName(file.LocalPath)!;
                try { Directory.SetLastWriteTimeUtc(entry, _utcNow()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        // --- writing -------------------------------------------------------------

        // Keeps these bytes as the copy of `path` on `machine` with this stamp,
        // and returns where. Null when it could not be written or marked — a
        // file that cannot carry the downloaded mark is not handed to an
        // opener.
        internal CachedFile? Store(string machine, string path, long mtimeMs, byte[] bytes, string origin)
        {
            var entry = Path.Combine(KeyDir(machine, path), MirrorProtocol.FileStamp(mtimeMs, bytes.LongLength));
            var final = Path.Combine(entry, SafeName(path));
            var temp = Path.Combine(entry, "." + Guid.NewGuid().ToString("N") + TempSuffix);

            lock (_gate)
            {
                try
                {
                    CreatePrivateDirectory(_root);
                    CreatePrivateDirectory(Path.GetDirectoryName(entry)!);
                    CreatePrivateDirectory(entry);

                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                    };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                    using (var stream = new FileStream(temp, options))
                        stream.Write(bytes, 0, bytes.Length);

                    _marker.Mark(temp, origin);
                    File.Move(temp, final, overwrite: true);
                    Directory.SetLastWriteTimeUtc(entry, _utcNow());
                    return new CachedFile(final, mtimeMs, bytes.LongLength);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    TryDelete(temp);
                    MirrorLog.Say("file-cache", $"store failed: {e.GetType().Name}");
                    return null;
                }
            }
        }

        private static void CreatePrivateDirectory(string dir)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // --- sweeping ------------------------------------------------------------

        // Drops every copy not used for MaxAge, every temp file older than
        // StaleTemp, and then the least recently used copies until what is left
        // is no more than MaxTotalBytes. Returns how many copies went.
        internal int Sweep()
        {
            var now = _utcNow();
            var removed = 0;

            lock (_gate)
            {
                if (!Directory.Exists(_root)) return 0;

                var live = new List<(string Dir, DateTime Used, long Bytes)>();

                foreach (var keyDir in SafeEnumerate(_root))
                {
                    foreach (var entry in SafeEnumerate(keyDir))
                    {
                        foreach (var temp in Directory.EnumerateFiles(entry, "*" + TempSuffix))
                            if (now - File.GetLastWriteTimeUtc(temp) > StaleTemp) TryDelete(temp);

                        var used = Directory.GetLastWriteTimeUtc(entry);
                        if (now - used > MaxAge)
                        {
                            if (TryDeleteDir(entry)) removed++;
                            continue;
                        }

                        var bytes = Directory.EnumerateFiles(entry).Sum(f => new FileInfo(f).Length);
                        live.Add((entry, used, bytes));
                    }
                }

                var total = live.Sum(l => l.Bytes);
                foreach (var (dir, _, bytes) in live.OrderBy(l => l.Used))
                {
                    if (total <= MaxTotalBytes) break;
                    if (TryDeleteDir(dir)) { removed++; total -= bytes; }
                }

                foreach (var keyDir in SafeEnumerate(_root))
                    if (!Directory.EnumerateFileSystemEntries(keyDir).Any()) TryDeleteDir(keyDir);
            }

            return removed;
        }

        private static IEnumerable<string> SafeEnumerate(string dir) =>
            Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];

        private static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private static bool TryDeleteDir(string dir)
        {
            try { Directory.Delete(dir, recursive: true); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    // Marks a file as having come from somewhere else, the way a browser marks
    // a download, so the OS treats opening it with the caution it gives any
    // download: com.apple.quarantine on macOS, a Zone.Identifier stream on
    // Windows. Throws IOException when the mark could not be written.
    internal interface IDownloadMarker
    {
        void Mark(string path, string origin);
    }

    internal sealed class OsDownloadMarker : IDownloadMarker
    {
        internal static readonly OsDownloadMarker Instance = new();

        // The quarantine value: flags, the time in hex seconds, the agent that
        // fetched it. 0081 is "quarantined, downloaded by an app", which is
        // what Safari and Mail write for a file they saved.
        internal static string QuarantineValue(DateTime utcNow, string agent) =>
            $"0081;{((DateTimeOffset)utcNow).ToUnixTimeSeconds():x8};{agent};";

        internal const string QuarantineName = "com.apple.quarantine";

        // ZoneId 3 is the Internet zone, what Edge writes for a download.
        internal static string ZoneIdentifier() =>
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n";

        public void Mark(string path, string origin)
        {
            if (OperatingSystem.IsMacOS()) MarkMac(path);
            else if (OperatingSystem.IsWindows()) File.WriteAllText(path + ":Zone.Identifier", ZoneIdentifier());
        }

        private static void MarkMac(string path)
        {
            var value = Encoding.UTF8.GetBytes(QuarantineValue(DateTime.UtcNow, Brand.DataDirName));
            if (setxattr(path, QuarantineName, value, value.Length, 0, 0) != 0)
                throw new IOException($"setxattr failed: errno {Marshal.GetLastPInvokeError()}");
        }

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        [SuppressMessage("Globalization", "CA2101", Justification = "UTF-8 on every Unix runtime.")]
        private static extern int setxattr(string path, string name, byte[] value, nint size, uint position, int options);

        // Read back, for the tests that prove the mark is really there.
        internal static string? ReadQuarantine(string path)
        {
            if (!OperatingSystem.IsMacOS()) return null;
            var buffer = new byte[512];
            var n = getxattr(path, QuarantineName, buffer, buffer.Length, 0, 0);
            return n < 0 ? null : Encoding.UTF8.GetString(buffer, 0, (int)n);
        }

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        [SuppressMessage("Globalization", "CA2101", Justification = "UTF-8 on every Unix runtime.")]
        private static extern nint getxattr(string path, string name, byte[] value, nint size, uint position, int options);
    }
}
