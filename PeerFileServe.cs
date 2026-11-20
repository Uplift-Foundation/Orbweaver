using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbweaver
{
    // CB-363: the serving half of MirrorProtocol's FILE frame — whether a peer
    // may have the bytes of one file on this machine, and reading them.
    //
    // The rules are the folder picker's (BrowseRules, CB-339), applied to a
    // file instead of a folder, so there is one answer on this machine to
    // "what may another device see": the configured browse roots, plus the
    // working folder of the session whose chat linked the file, minus every
    // system folder, and never the home folder or anything above it unless the
    // user switched home on. The near side's own reading of the path is never
    // trusted: the path is canonicalised HERE (case, `..`, 8.3 short names,
    // links and junctions all resolved by BrowseRules.Canonical) and only the
    // canonical path is checked, so a link out of a root arrives at the place
    // it really points to and is judged there.
    //
    // Everything outside the roots is forbidden_path whether or not it exists,
    // so a refusal cannot be used to map the disk; existence is only ever
    // reported for a path that was already in scope.

    internal enum PeerFileKind { Missing, Regular, Directory, Other }

    // What the disk says about one canonical path. Other is anything that is
    // neither a regular file nor a directory: a FIFO, a socket, a device.
    internal readonly record struct PeerFileStat(PeerFileKind Kind, long Size, long MtimeMs, bool Exec);

    internal interface IPeerFileSource
    {
        PeerFileStat Stat(string canonical);

        // Opened for reading, or null when it cannot be.
        Stream? Open(string canonical);
    }

    // Everything one FILE request is answered against, read fresh per request
    // so a settings change or a new session counts on the next click.
    internal sealed record PeerFileEnv(BrowseSettings Settings, IBrowseFileSystem Fs, IPeerFileSource Files);

    // The verdict before any byte is read. Err is a MirrorProtocol code; Same
    // means the asker's `have` still matches; otherwise Canonical and Stat say
    // what to read.
    internal sealed record PeerFileDecision(string? Err, bool Same, string? Canonical, PeerFileStat Stat);

    internal static class PeerFileRules
    {
        private static readonly UTF8Encoding Strict = new(false, true);

        // The path out of a FILE frame's payload, or null when it is not
        // strict UTF-8.
        internal static string? PathOf(byte[]? payload)
        {
            if (payload is null) return null;
            try { return Strict.GetString(payload); }
            catch (DecoderFallbackException) { return null; }
        }

        // bad_frame's shape test: not empty, the list_dir length and control
        // rules, and no format character (a right-to-left override, a
        // zero-width joiner) — the CB-274/275 lesson: a path that displays as
        // one thing and is another is refused rather than interpreted.
        internal static bool ShapeOk(string path)
        {
            if (path.Length == 0 || !BrowseRules.PathShapeOk(path)) return false;
            foreach (var c in path)
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) return false;
            return true;
        }

        // The settings plus the session's working folder as one more root —
        // unless that folder is not absolute, cannot be canonicalised, is a
        // system folder, or is the home folder or above it. A session started
        // in ~ does not make the whole home folder readable; only the home
        // switch does that.
        internal static BrowseSettings WithSessionRoot(BrowseSettings settings, string? cwd, IBrowseFileSystem fs)
        {
            if (string.IsNullOrWhiteSpace(cwd) || !BrowseRules.IsAbsolute(cwd, fs)) return settings;
            if (BrowseRules.Canonical(fs, cwd) is not { } canonical) return settings;
            if (BrowseRules.IsDenied(fs, canonical)) return settings;
            var home = BrowseRules.Canonical(fs, fs.Home) ?? fs.Home;
            if (BrowseRules.Within(home, canonical, fs)) return settings;
            return settings with { Roots = [canonical, .. settings.Roots] };
        }

        // forbidden_path, not_found, too_large and same, in that order.
        internal static PeerFileDecision Decide(string path, string? have, string? sessionCwd, PeerFileEnv env)
        {
            var fs = env.Fs;
            var settings = WithSessionRoot(env.Settings, sessionCwd, fs);

            if (!BrowseRules.IsAbsolute(path, fs) || NamesADevice(path, fs) || BrowseRules.Canonical(fs, path) is not { } canonical)
                return Refuse(MirrorProtocol.ErrForbiddenPath);

            if (!BrowseRules.InScope(settings, fs, canonical))
                return Refuse(MirrorProtocol.ErrForbiddenPath);

            var stat = env.Files.Stat(canonical);
            if (stat.Kind != PeerFileKind.Regular) return Refuse(MirrorProtocol.ErrNotFound);
            if (stat.Size > MirrorProtocol.MaxFileBytes) return Refuse(MirrorProtocol.ErrTooLarge);

            var same = MirrorProtocol.ParseFileStamp(have) is { } h && h.MtimeMs == stat.MtimeMs && h.Size == stat.Size;
            return new PeerFileDecision(null, same, canonical, stat);
        }

        private static PeerFileDecision Refuse(string code) => new(code, false, null, default);

        // On Windows, any component that is a reserved device name (NUL, CON,
        // aux.txt, COM1 …) is refused before the disk is asked anything: the
        // OS reads `<root>\NUL` as the device \\.\NUL whatever folder it is
        // written under, so it would pass the scope check as a path inside a
        // root and then open something that is not a file at all. The stem
        // rule is FileLinkPolicy's, the one the near side already uses.
        internal static bool NamesADevice(string path, IBrowseFileSystem fs)
        {
            if (!fs.IsWindows) return false;
            foreach (var component in path.Split('\\', '/'))
                if (component.Length > 0 && FileLinkPolicy.IsReservedDeviceName(component)) return true;
            return false;
        }

        // The bytes, or unreadable. After opening, the canonical path is
        // worked out again and must not have moved (a component swapped for a
        // link between the check and the open), and exactly the stat's length
        // must come back: a file that grew past the cap, or changed size, while
        // being read is not sent half-right.
        internal static (string? Err, byte[]? Bytes) Read(PeerFileDecision decision, PeerFileEnv env)
        {
            var canonical = decision.Canonical!;
            using var stream = env.Files.Open(canonical);
            if (stream is null) return (MirrorProtocol.ErrUnreadable, null);

            if (BrowseRules.Canonical(env.Fs, canonical) is not { } again || !BrowseRules.SamePath(again, canonical, env.Fs))
                return (MirrorProtocol.ErrUnreadable, null);

            var want = decision.Stat.Size;
            var buffer = new byte[want];
            var got = 0;
            try
            {
                while (got < want)
                {
                    var n = stream.Read(buffer, got, (int)(want - got));
                    if (n == 0) break;
                    got += n;
                }
                if (got != want || stream.ReadByte() != -1) return (MirrorProtocol.ErrUnreadable, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (MirrorProtocol.ErrUnreadable, null);
            }

            return (null, buffer);
        }
    }

    // The real disk. A regular file is told apart from a FIFO or a socket by
    // its st_mode on Unix, because .NET cannot: measured on macOS (CB-363), a
    // FIFO reports FileAttributes.Normal and length 0, exactly like an empty
    // file, and opening one blocks until somebody writes to it — a peer pump
    // thread lost for ever to one `mkfifo` inside a project folder.
    internal sealed class PeerFileSource : IPeerFileSource
    {
        internal static readonly PeerFileSource Instance = new();

        public PeerFileStat Stat(string canonical)
        {
            try
            {
                var info = new FileInfo(canonical);
                if (!info.Exists) return new PeerFileStat(Directory.Exists(canonical) ? PeerFileKind.Directory : PeerFileKind.Missing, 0, 0, false);

                var kind = OperatingSystem.IsWindows() ? WindowsKind(info.Attributes) : UnixKind(canonical);
                if (kind != PeerFileKind.Regular) return new PeerFileStat(kind, 0, 0, false);

                var exec = !OperatingSystem.IsWindows()
                    && (info.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                return new PeerFileStat(PeerFileKind.Regular, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), exec);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new PeerFileStat(PeerFileKind.Missing, 0, 0, false);
            }
        }

        // A Windows file reached through a drive path is a file; a device is
        // not one this serves.
        internal static PeerFileKind WindowsKind(FileAttributes attributes) =>
            attributes.HasFlag(FileAttributes.Device) ? PeerFileKind.Other : PeerFileKind.Regular;

        // S_IFMT out of .NET's own PAL stat (System.Native ships with every
        // runtime and its FileStatus starts Flags, Mode on every Unix it
        // supports), so there is no per-architecture struct stat to get right.
        internal static PeerFileKind UnixKind(string path)
        {
            var buffer = new byte[512];
            if (SystemNativeStat(path, buffer) != 0) return PeerFileKind.Missing;
            return KindOfMode(BitConverter.ToInt32(buffer, 4));
        }

        internal static PeerFileKind KindOfMode(int mode) => (mode & 0xF000) switch
        {
            0x8000 => PeerFileKind.Regular,
            0x4000 => PeerFileKind.Directory,
            _ => PeerFileKind.Other,
        };

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi, SetLastError = true)]
        [SuppressMessage("Globalization", "CA2101", Justification = "UTF-8 on every Unix runtime.")]
        private static extern int SystemNativeStat(string path, byte[] output);

        public Stream? Open(string canonical)
        {
            try
            {
                return new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    // Who may ask right now: a request bucket per peer (the folder picker's
    // own BrowseRateLimiter, a separate instance) and at most one file in
    // flight per peer, so one machine cannot have this one reading fifty
    // 50 MB files at once.
    internal sealed class PeerFileGate
    {
        private readonly BrowseRateLimiter _rate = new();
        private readonly Dictionary<string, PeerFileLease> _held = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        internal bool TryRate(string peer, long nowMs) => _rate.TryAccept(peer, nowMs);

        // The peer's one slot, or null when it is already held. What comes back is
        // the only way to let it go, so a release belongs to the request that took
        // it by construction (CB-384).
        internal PeerFileLease? TryBegin(string peer)
        {
            lock (_gate)
            {
                if (_held.ContainsKey(peer)) return null;

                var lease = new PeerFileLease(this, peer);
                _held[peer] = lease;
                return lease;
            }
        }

        private void Free(PeerFileLease lease)
        {
            lock (_gate)
            {
                // Only if the slot is still this lease's. A late release from a request
                // that already let go must never free a newer request's slot.
                if (_held.TryGetValue(lease.Peer, out var current) && ReferenceEquals(current, lease))
                    _held.Remove(lease.Peer);
            }
        }

        // One request's hold on its peer's slot. Releasing is idempotent and cannot
        // release anything but this lease's own hold.
        internal sealed class PeerFileLease
        {
            private readonly PeerFileGate _owner;
            private int _released;

            internal PeerFileLease(PeerFileGate owner, string peer)
            {
                _owner = owner;
                Peer = peer;
            }

            internal string Peer { get; }

            internal void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Free(this);
            }
        }
    }
}
