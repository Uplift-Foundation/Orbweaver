using System;
using System.Globalization;
using System.Text;

namespace Orbweaver
{
    // A file a chat message points at, read out of a Markdown link destination
    // (CB-363).
    //
    // Chat text is untrusted, so this is the one place a destination becomes a
    // path, and it is strict by construction: it accepts the handful of shapes
    // an agent really emits and returns null for everything else, because a
    // null here is a plain label on screen and a wrong "yes" is a file opened
    // that nobody chose. Pure — no disk, no OS — so each refusal is a unit
    // case. Whether the file may be opened is FileLinkPolicy's question, and
    // whether it exists is the caller's; this only says what was named.
    //
    // Path is decoded exactly once, and is in the form of its own platform:
    // forward slashes for a rooted POSIX path, backslashes for a drive path.
    internal readonly record struct FileLink(string Path, bool IsWindowsPath)
    {
        // The forms, all of which also take a trailing ":line" or ":line:col"
        // (dropped — an editor's cursor position, meaningless to a viewer):
        //
        //   vscode://file/<abs>     vscode://file//<abs>   (both are seen in one message)
        //   file:///<abs>           file://localhost/<abs>
        //   /abs                    C:\abs                  C:/abs
        //
        // Either path syntax is accepted whichever OS is running; whether a
        // drive path means anything here is the policy's call.
        //
        // The order matters and is the point of the ticket's wording: the
        // line suffix comes off first, while ':' still means what it looks
        // like, and the percent-decode runs once after — so "%2520" is a
        // literal "%20" in a file name, not a space, and an encoded ':' or
        // '/' can never be what carries a stream name or a UNC prefix past
        // the checks, because those run on the decoded text again.
        internal static FileLink? TryParse(string? dest)
        {
            if (string.IsNullOrEmpty(dest)) return null;

            // A raw '?' or '#' is a URL query or fragment, which a file path
            // has none of — "file:///a.pdf?x" is something other than a.pdf.
            // An encoded one (%3F, %23) is a character of the name and decodes.
            foreach (var c in dest)
            {
                if (c is '?' or '#') return null;
            }

            var rest = Rest(dest);
            if (rest is null) return null;

            rest = StripLineSuffix(rest);

            var decoded = DecodeOnce(rest);
            return decoded is null ? null : FromDecodedPath(decoded);
        }

        // Whether a destination is shaped like one of the forms at all, which
        // is a weaker question than TryParse's. ChatMarkdown asks it to decide
        // whether spaces belong to the destination ("/a/My File.pdf") or start
        // a title, and that has to be settled before it is known whether the
        // whole thing is acceptable.
        internal static bool LooksLikeFileForm(string dest) =>
            Rest(dest) is not null;

        // What follows the scheme, as a still-encoded path, or null when the
        // destination is none of the forms.
        private static string? Rest(string dest)
        {
            string rest;

            if (dest.StartsWith("vscode://file", StringComparison.OrdinalIgnoreCase))
            {
                rest = dest["vscode://file".Length..];
                // "vscode://file/Users/x" and "vscode://file//Users/x" are the
                // same file: one slash belongs to the scheme, so the doubled
                // form leaves a leading "//" that is not UNC.
                if (rest.StartsWith("//", StringComparison.Ordinal)) rest = rest[1..];
                if (!rest.StartsWith('/')) return null;
                return DropSlashBeforeDrive(rest);
            }

            if (dest.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                rest = dest["file://".Length..];

                // The only authority a file URL may name is this machine.
                // file://host/share is somebody else's, and file:// plus a
                // path with no authority at all is a one-slash form nobody
                // writes: both are refused rather than guessed at.
                if (rest.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase)) rest = rest["localhost".Length..];
                if (!rest.StartsWith('/')) return null;
                return DropSlashBeforeDrive(rest);
            }

            // A bare rooted path, which may be "//x" and so UNC — kept whole
            // so the decoded checks refuse it.
            if (dest.StartsWith('/')) return dest;

            return HasDrivePrefix(dest, 0) ? dest : null;
        }

        // "/C:/x" (what a file URL says for a drive path) is "C:/x".
        private static string DropSlashBeforeDrive(string rest) =>
            rest.Length >= 3 && rest[0] == '/' && HasDrivePrefix(rest, 1) ? rest[1..] : rest;

        private static bool HasDrivePrefix(string s, int at) =>
            s.Length >= at + 3
            && ((s[at] >= 'A' && s[at] <= 'Z') || (s[at] >= 'a' && s[at] <= 'z'))
            && s[at + 1] == ':'
            && (s[at + 2] == '\\' || s[at + 2] == '/');

        // A trailing ":12" or ":12:3". At most two numeric groups, so
        // "x.txt:12:3:4" keeps its extra colon and fails the stream check
        // instead of being quietly trimmed.
        private static string StripLineSuffix(string rest)
        {
            for (var groups = 0; groups < 2; groups++)
            {
                var colon = rest.LastIndexOf(':');
                if (colon <= 0 || colon == rest.Length - 1) break;

                var allDigits = true;
                for (var i = colon + 1; i < rest.Length; i++)
                {
                    if (rest[i] < '0' || rest[i] > '9')
                    {
                        allDigits = false;
                        break;
                    }
                }

                if (!allDigits) break;

                rest = rest[..colon];
            }

            return rest;
        }

        // Percent-decoding, once and strictly: a '%' not followed by two hex
        // digits is refused (not passed through), and the bytes must be valid
        // UTF-8 (not replaced with U+FFFD) — a lenient decoder is how two
        // spellings of one path get past a check that looked at only one.
        private static string? DecodeOnce(string s)
        {
            if (!s.Contains('%')) return s;

            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(s.Length)];
            var n = 0;

            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '%')
                {
                    if (i + 2 >= s.Length) return null;
                    if (!Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2])) return null;

                    bytes[n++] = (byte)(HexValue(s[i + 1]) * 16 + HexValue(s[i + 2]));
                    i += 2;
                }
                else
                {
                    // A literal character is encoded as it stands, a surrogate
                    // pair as one unit; a lone surrogate has no UTF-8 form and
                    // would silently become U+FFFD, so it is refused.
                    var width = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                    if (width == 1 && char.IsSurrogate(s[i])) return null;

                    n += Encoding.UTF8.GetBytes(s.AsSpan(i, width), bytes.AsSpan(n));
                    i += width - 1;
                }
            }

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, n);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static int HexValue(char c) =>
            c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a') + 10;

        // The checks that apply to a path whatever spelling it arrived in,
        // run on the decoded text. FileLinkLauncher runs them again on the
        // path it is handed, which is already decoded — hence a method of its
        // own rather than the tail of TryParse, since decoding twice is the
        // bug this file exists to avoid.
        internal static FileLink? FromDecodedPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var c in path)
            {
                if (char.IsControl(c)) return null;

                // U+202E and its relatives draw one name while the file
                // system is handed another; same rule as CloudSessionLink.
                if (char.GetUnicodeCategory(c) == UnicodeCategory.Format) return null;
            }

            // UNC and the device/extended prefixes: \\server\share, //server/x,
            // \\?\C:\x, \\.\pipe\x. A network path is a request to some other
            // machine, and the extended prefixes skip Windows' own path rules.
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return null;

            if (HasDrivePrefix(path, 0))
            {
                // A second colon is an alternate data stream ("a.txt:evil.exe"),
                // which opens something other than the file the name shows.
                if (path.IndexOf(':', 2) >= 0) return null;

                return new FileLink(Collapse(path.Replace('/', '\\'), '\\', 3), IsWindowsPath: true);
            }

            if (path[0] != '/') return null;

            // A backslash in a rooted path is ambiguous between a name
            // character and a separator, depending on which OS reads it; the
            // ambiguity is the attack, so it is refused.
            if (path.Contains('\\') || path.Contains(':')) return null;

            return new FileLink(Collapse(path, '/', 1), IsWindowsPath: false);
        }

        // "." and ".." segments resolved lexically, so the tooltip and the
        // launcher show and open the same path ("/a/../etc/x.pdf" is
        // "/etc/x.pdf"), and a ".." cannot hide behind a name that looks
        // harmless. Clamped at the root rather than refused. This is display
        // and launch hygiene only; it resolves no symlinks, and the serving
        // side canonicalises for itself and never trusts this.
        private static string Collapse(string path, char separator, int rootLength)
        {
            var segments = new System.Collections.Generic.List<string>();
            foreach (var segment in path[rootLength..].Split(separator))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            return path[..rootLength] + string.Join(separator, segments);
        }
    }
}
