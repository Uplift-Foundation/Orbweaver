using System;
using System.IO;

namespace Orbweaver
{
    // What is known about a path on disk, gathered by the caller so that the
    // decision below stays a pure function of it.
    internal readonly record struct FileFacts(bool Exists, bool IsDirectory, bool Executable)
    {
        // The facts for a real path on this machine. The one place the policy
        // touches the disk, kept out of Decide so Decide needs no temp tree.
        //
        // Executable is the POSIX execute bit on any of user/group/other,
        // read through File.GetUnixFileMode (which follows a symlink, so a
        // "report.pdf" that is a link to /bin/ls is judged as /bin/ls). It is
        // always false on Windows, where there is no such bit and the
        // extension list is the gate.
        internal static FileFacts Probe(string path)
        {
            try
            {
                if (Directory.Exists(path)) return new FileFacts(true, true, false);
                if (!File.Exists(path)) return new FileFacts(false, false, false);

                var executable = false;
                if (!OperatingSystem.IsWindows())
                {
                    const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                    executable = (File.GetUnixFileMode(path) & anyExecute) != 0;
                }

                return new FileFacts(true, false, executable);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Unreadable is treated as missing: the verdict is a refusal
                // either way, and nothing here may throw into a click handler.
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
    internal enum FileLinkAction { Open, Reveal, Refuse }

    internal readonly record struct FileLinkVerdict(FileLinkAction Action, string Reason = "")
    {
        internal static FileLinkVerdict Open => new(FileLinkAction.Open);
        internal static FileLinkVerdict Reveal(string reason) => new(FileLinkAction.Reveal, reason);
        internal static FileLinkVerdict Refuse(string reason) => new(FileLinkAction.Refuse, reason);
    }

    internal static class FileLinkPolicy
    {
        // The ticket's list plus the architect's additions. Compared without
        // case, on the last extension only: "x.pdf.exe" is an .exe and
        // "x.exe.pdf" is a pdf, which is how the OS reads them too.
        private static readonly string[] DeniedExtensions =
        [
            ".app", ".command", ".sh", ".exe", ".bat", ".cmd", ".ps1", ".lnk", ".scpt", ".pkg", ".msi",
            ".jar", ".js", ".vbs", ".wsf", ".hta", ".scr", ".com", ".pif", ".reg", ".cpl", ".dll", ".url",
            ".terminal", ".tool", ".workflow", ".action", ".inetloc", ".fileloc", ".webloc", ".dmg", ".desktop"
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

            if (!facts.Exists) return FileLinkVerdict.Refuse("That file does not exist.");

            if (facts.IsDirectory) return FileLinkVerdict.Reveal("Folders are shown, not opened.");

            if (facts.Executable) return FileLinkVerdict.Reveal("Executable files are shown, not run.");

            if (IsDeniedExtension(link.Path)) return FileLinkVerdict.Reveal("Programs and scripts are shown, not run.");

            return FileLinkVerdict.Open;
        }

        internal static bool IsDeniedExtension(string path)
        {
            var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot < 0) return false;

            var extension = name[dot..];
            foreach (var denied in DeniedExtensions)
            {
                if (string.Equals(extension, denied, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool IsNetworkPath(string path) =>
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

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
