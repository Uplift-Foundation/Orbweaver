using System;
using System.IO;

namespace Orbweaver
{
    // What is known about a path on disk, gathered by the caller so that the
    // decision below stays a pure function of it.
    internal readonly record struct FileFacts(bool Exists, bool IsDirectory, bool Executable, string? Target = null, bool Special = false)
    {
        // Special is true for something that exists, is not a directory and is
        // not a regular file: a FIFO, a socket, a character or block device.
        // .NET reports all of them as an ordinary file, and "open" on a FIFO
        // hands a viewer a path whose read blocks for ever. Only ever true on
        // POSIX, where those kinds exist.
        // Target is the final destination when the path is itself a symlink,
        // and null when it is not. A link named x.pdf that points at y.command
        // is a command, so the extension rule applies to both names and the
        // launcher is handed the target, so swapping the link after the
        // decision cannot redirect what is opened.
        // The facts for a real path on this machine. The one place the policy
        // touches the disk, kept out of Decide so Decide needs no temp tree.
        //
        // Executable is the POSIX execute bit on any of user/group/other,
        // read through File.GetUnixFileMode (which follows a symlink, so a
        // "report.pdf" that is a link to /bin/ls is judged as /bin/ls). It is
        // always false on Windows, where there is no such bit and the
        // extension list is the gate.
        // Whether the path, with symlinks followed, is a regular file. The
        // type is not readable through .NET (FileAttributes is Normal for a
        // FIFO), so this asks the system's own `test -f`, argv-only with a
        // bound on how long it may take: the same answer as stat's S_IFREG
        // without a per-platform struct layout to get wrong. Always true on
        // Windows, which has no such kinds. Anything that cannot be asked is
        // treated as not regular, which is a reveal, never an open.
        internal static bool IsRegularFile(string path)
        {
            if (OperatingSystem.IsWindows()) return true;

            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("/bin/test") { UseShellExecute = false };
                info.ArgumentList.Add("-f");
                info.ArgumentList.Add(path);

                using var process = System.Diagnostics.Process.Start(info);
                if (process is null) return false;

                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        internal static FileFacts Probe(string path)
        {
            try
            {
                var isDirectory = Directory.Exists(path);
                if (!isDirectory && !File.Exists(path)) return new FileFacts(false, false, false);

                // The final target of a link, or null for a plain path. A
                // dangling or looping link never reaches here: both make
                // Exists false (a loop throws, and lands in the catch).
                var target = isDirectory
                    ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                    : File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;

                if (isDirectory) return new FileFacts(true, true, false, target);

                var executable = false;
                if (!OperatingSystem.IsWindows())
                {
                    const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                    executable = (File.GetUnixFileMode(path) & anyExecute) != 0;
                }

                return new FileFacts(true, false, executable, target, Special: !IsRegularFile(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Unreadable (or a link loop) is treated as missing: the
                // verdict is a refusal either way, and nothing here may throw
                // into a click handler.
                return new FileFacts(false, false, false);
            }
        }
    }

    // The decision every file-link launch goes through, the way
    // CloudSessionLink.IsOpenable gates browser launches (CB-363).
    //
    // Chat text is untrusted, and "open" on a file is "run it" for a surprising
    // number of kinds. So the default for anything that could execute, or be
    // asked to by a double-click, is Reveal: show it in Finder or Explorer and
    // let a person decide. Open is what is left once a file is none of those.
    // FileLinkAction itself is declared beside its first consumer in
    // ChatFileLinks.cs; the three members are the contract between them.

    internal readonly record struct FileLinkVerdict(FileLinkAction Action, string Reason = "")
    {
        internal static FileLinkVerdict Open => new(FileLinkAction.Open);
        internal static FileLinkVerdict Reveal(string reason) => new(FileLinkAction.Reveal, reason);
        internal static FileLinkVerdict Refuse(string reason) => new(FileLinkAction.Refuse, reason);
    }

    internal static class FileLinkPolicy
    {
        // An allowlist, not a deny list (PM decision, CB-363): a deny list is
        // a bet that nobody has registered a handler that runs something it
        // forgot — .html and .svg run script in a browser, .py runs under the
        // default handler on Windows, .docm carries macros, .iso mounts — and
        // every such omission is a file opened that nobody chose. Only document,
        // image, audio and video kinds that cannot execute on open are here;
        // everything else is revealed, which costs one more click and nothing
        // else. Compared without case, on the last extension only: "x.pdf.exe"
        // is an .exe (revealed) and "x.exe.pdf" is a pdf (opened), which is how
        // the OS reads them too.
        private static readonly string[] AllowedExtensions =
        [
            ".pdf", ".txt", ".md", ".markdown", ".rtf", ".csv", ".tsv", ".json", ".log", ".xml", ".yaml", ".yml",
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".bmp", ".tiff", ".tif",
            ".mp3", ".m4a", ".wav", ".mp4", ".mov", ".m4v",
            ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".pages", ".numbers", ".key", ".eml"
        ];

        internal static FileLinkVerdict Decide(FileLink link, FileFacts facts, bool isWindows)
        {
            // A drive path on a Mac, or a rooted POSIX path on Windows, names
            // nothing on this machine; guessing a translation would open
            // whatever the guess landed on.
            if (link.IsWindowsPath != isWindows)
                return FileLinkVerdict.Refuse("That path is not valid on this computer.");

            if (FileLink.FromDecodedPath(link.Path) is null || IsNetworkPath(link.Path))
                return FileLinkVerdict.Refuse("Network and device paths are not opened from chat.");

            // Windows drops a trailing dot or space from a name when it opens
            // it, so "x.exe." is x.exe — and would sail past an extension
            // check on the name as written. Refused outright, any segment.
            if (isWindows && HasTrailingDotOrSpace(link.Path))
                return FileLinkVerdict.Refuse("Names ending in a dot or space are not opened from chat.");

            // CON.pdf and nul.txt are not files on Windows: the name before the
            // first dot is a device, whatever follows, and opening one reads
            // the console or blocks on a port.
            if (isWindows && IsReservedDeviceName(link.Path))
                return FileLinkVerdict.Refuse("That name is a Windows device, not a file.");

            if (!facts.Exists) return FileLinkVerdict.Refuse("That file does not exist.");

            if (facts.IsDirectory) return FileLinkVerdict.Reveal("Folders are shown, not opened.");

            if (facts.Special) return FileLinkVerdict.Reveal("Pipes, sockets and devices are shown, not opened.");

            if (facts.Executable) return FileLinkVerdict.Reveal("Executable files are shown, not run.");

            // Both names: the one in the message and, for a symlink, where it
            // really goes. Either one off the list is shown, not opened.
            if (!IsAllowedExtension(link.Path) || (facts.Target is not null && !IsAllowedExtension(facts.Target))) return FileLinkVerdict.Reveal("Only documents, images, audio and video are opened from chat; other files are shown.");

            return FileLinkVerdict.Open;
        }

        internal static bool IsAllowedExtension(string path)
        {
            var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot < 0) return false;

            var extension = name[dot..];
            foreach (var allowed in AllowedExtensions)
            {
                if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool IsNetworkPath(string path) =>
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

        private static readonly string[] ReservedStems = ["CON", "PRN", "AUX", "NUL"];

        // The final segment's stem — before its first dot, trailing spaces
        // trimmed, compared without case — is CON, PRN, AUX, NUL, COM0-9 or
        // LPT0-9, including the superscript digits Windows also reserves
        // (COM¹ COM² COM³, LPT¹ LPT² LPT³). "CONSOLE" and "con1" are names.
        internal static bool IsReservedDeviceName(string path)
        {
            var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            var dot = name.IndexOf('.');
            var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');

            foreach (var reserved in ReservedStems)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return true;
            }

            if (stem.Length != 4) return false;

            var prefix = stem[..3];
            if (!prefix.Equals("COM", StringComparison.OrdinalIgnoreCase)
                && !prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase)) return false;

            var digit = stem[3];
            return (digit >= '0' && digit <= '9') || digit is '\u00B9' or '\u00B2' or '\u00B3';
        }

        internal static bool HasTrailingDotOrSpace(string path)
        {
            foreach (var segment in path.Split('\\', '/'))
            {
                if (segment.Length > 0 && (segment[^1] == '.' || segment[^1] == ' ')) return true;
            }

            return false;
        }
    }
}
