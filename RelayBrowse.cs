using System.Text;

namespace Orbweaver
{
    // CB-339 / frames-v11: the folder picker's desk half. Everything that decides what a phone may
    // see of this machine's folders lives here, as a pure function of (request, settings, platform,
    // filesystem), the way LaunchGate in RelayLaunch.cs decides whether a launch may run: no window,
    // no static settings, no disk in the tests that replay the vectors. BrowseFileSystem is the one
    // implementation that touches a real disk.
    //
    // The order is the contract's resolve.steps and it is the point: canonicalise, THEN check, so a
    // `..`, another capitalisation, an 8.3 short name, a symbolic link or a junction can only arrive
    // at a folder that is itself allowed (the CB-89 lesson: a lexical check before resolution is a
    // check of the wrong string). Everything outside the roots is forbidden_path whether or not it
    // exists, so a refusal cannot be used to map the disk.

    internal enum BrowseKind { Dir, File, Link }

    // One thing a folder holds, as the filesystem spells it. Target is a link's raw target, absolute
    // or relative to the folder that holds the link.
    internal readonly record struct BrowseEntry(string Name, BrowseKind Kind, string? Target = null);

    internal interface IBrowseFileSystem
    {
        string Home { get; }
        bool IsWindows { get; }
        bool CaseInsensitive { get; }

        // scope.system_folders with ~, <SystemRoot> and <drive> filled in for this machine.
        IReadOnlyList<string> DeniedExact { get; }
        IReadOnlyList<string> DeniedTrees { get; }

        // The entry called `name` in `parent`, in the filesystem's own spelling of it: its real
        // case, and on Windows the long name for an 8.3 short name. Null when there is none, or
        // when `parent` cannot be read.
        BrowseEntry? Lookup(string parent, string name);

        // Whether a folder exists at exactly this canonical path.
        bool DirectoryExists(string path);

        // The first `cap` entries of a folder, and whether there were more. Null when it cannot be
        // read (permission, or it vanished).
        (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap);

        // Windows' Hidden attribute on a child. Always false elsewhere: a dot-folder is hidden by
        // name, which the rules decide.
        bool HiddenAttribute(string folder, string name);

        // Whether the desk's persona resolution finds a persona named by a file at this folder's
        // own level.
        bool PersonaAt(string folder);
    }

    // Both settings are desk-side and neither is on the wire. Roots is what the desk has resolved to
    // use (the user's list, or the automatic one); Home is the "whole home folder" toggle.
    internal sealed record BrowseSettings(IReadOnlyList<string> Roots, bool Home);

    internal sealed record BrowseItem(
        string Name, string? Path = null, string? Drive = null, bool Home = false,
        bool Git = false, bool Claude = false, bool Persona = false, bool Hidden = false);

    internal sealed record BrowseListing(
        string Path, string Parent, string Root, IReadOnlyList<BrowseItem> Items, bool Truncated);

    // Either one err code or a listing. Canonical is what step 6 arrived at, null where the request
    // never got that far; it is returned so the vectors can pin it.
    internal sealed record BrowseOutcome(string? Err, BrowseListing? Listing, string? Canonical = null);

    internal static class BrowseLimits
    {
        // frames-v11 limits.max_dir_plaintext_bytes (contract 00c391e): 40,000, not v4's 48,000 window,
        // so a `dir` chunk still fits the relay's 65,536-byte frame even if its base64 `ct` were
        // written with the default escaper.
        internal const int MaxDirPlaintextBytes = 40000;
        internal const int MaxEntries = 500;
        internal const int MaxPathBytes = 4096;
        internal const int MaxRoots = 64;
        internal const int RateBurst = 8;
        internal const int RateIntervalMs = 250;
        internal const int MaxLinkReplacements = 40;

        // frames-v11 desktop_rules.serve: the most directory entries read from one folder.
        internal const int MaxScan = 10_000;
    }

    internal static class BrowseRules
    {
        // ---- the system folders -----------------------------------------------------------

        // scope.system_folders for this machine. `systemRoot` is normally C:\Windows, `drives` every
        // drive root (C:\, D:\), `programFiles`/`programFilesX86`/`programData` the machine's own.
        internal static (string[] Exact, string[] Trees) SystemFolders(
            bool windows, string home, string systemRoot = @"C:\Windows",
            IEnumerable<string>? drives = null, string programFiles = @"C:\Program Files",
            string programFilesX86 = @"C:\Program Files (x86)", string programData = @"C:\ProgramData")
        {
            if (!windows)
            {
                return (["/", "/Users", "/Volumes"],
                [
                    "/System", "/Library", "/Applications", "/private", "/etc", "/var", "/tmp", "/usr", "/bin",
                    "/sbin", "/dev", "/opt", "/cores",
                    Join(home, "Library", false), Join(home, ".ssh", false), Join(home, ".gnupg", false),
                    Join(home, ".aws", false), Join(home, ".Trash", false),
                ]);
            }

            var drive = systemRoot.Length >= 2 ? systemRoot[..2] : "C:";
            var trees = new List<string> { systemRoot, programFiles, programFilesX86, programData };
            foreach (var d in drives ?? [])
            {
                trees.Add(Join(d, "$Recycle.Bin", true));
                trees.Add(Join(d, "System Volume Information", true));
                trees.Add(Join(d, "Recovery", true));
            }
            foreach (var name in new[] { "AppData", ".ssh", ".gnupg", ".aws" }) trees.Add(Join(home, name, true));
            return ([drive + "\\Users"], trees.ToArray());
        }

        // ---- paths ------------------------------------------------------------------------

        private static bool IsSeparator(char c, bool windows) => c == '/' || (windows && c == '\\');

        internal static char Separator(bool windows) => windows ? '\\' : '/';

        internal static string Join(string folder, string name, bool windows)
        {
            var sep = Separator(windows);
            return folder.Length > 0 && folder[^1] == sep ? folder + name : folder + sep + name;
        }

        // The path's drive root ("C:\" or "/") when it is an absolute path in this platform's sense
        // (step 5), else null. Windows: a drive letter, a colon, a separator, and no other colon, so a
        // UNC path, a \\?\ or \\.\ device path, a drive-relative C:name and an alternate data stream
        // are none of them absolute here.
        private static string? AbsoluteRoot(string path, bool windows)
        {
            if (!windows) return path.StartsWith('/') ? "/" : null;
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !IsSeparator(path[2], true)) return null;
            if (path.IndexOf(':', 2) >= 0) return null;
            return char.ToUpperInvariant(path[0]) + ":\\";
        }

        // A Windows component the OS would read as something else: it strips trailing dots and
        // spaces, so `C:\root\.. ` is `C:\root\..` to every API but this one's string comparison.
        // Hardening beyond the contract's text: nothing in the vectors has such a name, and the only
        // effect is forbidden_path.
        private static bool Slippery(string component, bool windows)
        {
            if (!windows || component.Length == 0) return false;
            if (component[^1] is '.' or ' ') return true;
            foreach (var c in component) if (c is '<' or '>' or '"' or '|' or '?' or '*' or ':') return true;
            return false;
        }

        // Lexical normalisation (step 6, first half): `.` dropped, `..` removing the component before
        // it and stopping at the root, repeated separators collapsed, a drive letter upper-cased.
        // Null when a component is one this platform reads as something else.
        private static (string Root, List<string> Components)? Normalise(string absolute, bool windows)
        {
            if (AbsoluteRoot(absolute, windows) is not { } root) return null;
            var parts = new List<string>();
            var start = windows ? 3 : 1;
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length == 0) return;
                var part = sb.ToString();
                sb.Clear();
                if (part == ".") return;
                if (part == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    return;
                }
                parts.Add(part);
            }
            for (var i = start; i < absolute.Length; i++)
            {
                if (IsSeparator(absolute[i], windows)) Flush();
                else sb.Append(absolute[i]);
            }
            Flush();
            foreach (var p in parts) if (Slippery(p, windows)) return null;
            return (root, parts);
        }

        // Step 6: the canonical form. Each component is replaced by the filesystem's own spelling of
        // it, and a link or junction by its target, itself normalised and resolved the same way.
        // More than 40 link replacements, or a component the platform would read as something else,
        // is null (forbidden_path). A component that does not exist keeps its written form, and so
        // does everything after it.
        internal static string? Canonical(IBrowseFileSystem fs, string absolute)
        {
            var windows = fs.IsWindows;
            if (Normalise(absolute, windows) is not { } n) return null;
            var current = n.Root;
            var pending = new LinkedList<string>(n.Components);
            var links = 0;
            while (pending.First is { } node)
            {
                pending.RemoveFirst();
                var component = node.Value;
                if (fs.Lookup(current, component) is not { } entry)
                {
                    // A Windows 8.3 short name the filesystem could not resolve to its long name is
                    // refused rather than kept as written: kept, `C:\PROGRA~1` matches neither the
                    // denied `C:\Program Files` nor anything else the checks name, and the OS would
                    // still open it.
                    if (windows && LooksLikeShortName(component)) return null;
                    // Gone: it and everything after it stays as written.
                    current = Join(current, component, windows);
                    foreach (var rest in pending) current = Join(current, rest, windows);
                    return current;
                }

                if (entry.Kind != BrowseKind.Link)
                {
                    current = Join(current, entry.Name, windows);
                    continue;
                }

                if (++links > BrowseLimits.MaxLinkReplacements) return null;
                var target = entry.Target ?? "";
                var absoluteTarget = AbsoluteRoot(target, windows) is not null ? target : RelativeTarget(current, target, windows);
                if (absoluteTarget is null || Normalise(absoluteTarget, windows) is not { } t) return null;
                current = t.Root;
                for (var i = t.Components.Count - 1; i >= 0; i--) pending.AddFirst(t.Components[i]);
            }
            return current;
        }

        // `NAME~1`, `PROGRA~1.TXT`: a tilde and a digit, which is what an 8.3 alias always has.
        private static bool LooksLikeShortName(string component)
        {
            for (var i = component.IndexOf('~'); i >= 0 && i + 1 < component.Length; i = component.IndexOf('~', i + 1))
                if (char.IsAsciiDigit(component[i + 1])) return true;
            return false;
        }

        // A link's relative target, against the folder that holds the link. A Windows target that
        // names a drive-relative, rooted-without-a-drive or device path is not one this resolves.
        private static string? RelativeTarget(string folder, string target, bool windows)
        {
            if (target.Length == 0) return null;
            if (windows && (IsSeparator(target[0], true) || target.Contains(':'))) return null;
            return Join(folder, target, windows);
        }

        private static bool Equal(string a, string b, IBrowseFileSystem fs) =>
            string.Equals(a, b, fs.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        // Equal to or inside, by whole components.
        internal static bool Within(string path, string folder, IBrowseFileSystem fs)
        {
            if (Equal(path, folder, fs)) return true;
            var prefix = folder.Length > 0 && IsSeparator(folder[^1], fs.IsWindows) ? folder : folder + Separator(fs.IsWindows);
            return path.StartsWith(prefix, fs.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static bool Denied(IBrowseFileSystem fs, string canonical)
        {
            foreach (var exact in fs.DeniedExact) if (Equal(canonical, exact, fs)) return true;
            foreach (var tree in fs.DeniedTrees) if (Within(canonical, tree, fs)) return true;
            return false;
        }

        private static bool IsFilesystemRoot(string path, IBrowseFileSystem fs) =>
            fs.IsWindows ? path.Length == 3 && path[1] == ':' && path[2] == '\\' : path == "/";

        // A path's last component, or the whole path for a filesystem or drive root.
        private static string LastComponent(string path, IBrowseFileSystem fs)
        {
            if (IsFilesystemRoot(path, fs)) return path;
            var i = path.LastIndexOf(Separator(fs.IsWindows));
            return path[(i + 1)..];
        }

        private static string ParentOf(string path, IBrowseFileSystem fs)
        {
            var i = path.LastIndexOf(Separator(fs.IsWindows));
            return i <= (fs.IsWindows ? 2 : 0) ? path[..(i + 1)] : path[..i];
        }

        // ---- roots ------------------------------------------------------------------------

        // scope.default_roots: the folders a desk recently ran sessions in (most recent first) to the
        // automatic project roots. Lexical: callers pass folders as the sessions reported them and
        // Resolve canonicalises each root afterwards. Each folder offers its PARENT, except that
        // when the parent is the home folder, a folder above it, a filesystem or drive root or one of
        // the exact system folders, the folder offers ITSELF — which is what keeps a session started
        // in ~/notes from making the home folder browsable and defeating the toggle.
        internal static List<string> DefaultRoots(IEnumerable<string> recent, IBrowseFileSystem fs)
        {
            var candidates = new List<string>();
            foreach (var folder in recent)
            {
                if (AbsoluteRoot(folder, fs.IsWindows) is null || Normalise(folder, fs.IsWindows) is not { } n) continue;
                var path = n.Components.Count == 0 ? n.Root : Join(n.Root, string.Join(Separator(fs.IsWindows), n.Components), fs.IsWindows);
                var parent = ParentOf(path, fs);
                var exact = fs.DeniedExact.Any(e => Equal(parent, e, fs));
                var offer = Within(fs.Home, parent, fs) || IsFilesystemRoot(parent, fs) || exact ? path : parent;
                if (Within(fs.Home, offer, fs) || IsFilesystemRoot(offer, fs) || Denied(fs, offer)) continue;
                if (!candidates.Any(c => Equal(c, offer, fs))) candidates.Add(offer);
            }
            var kept = candidates.Where(c => !candidates.Any(o => !ReferenceEquals(o, c) && !Equal(o, c, fs) && Within(c, o, fs))).ToList();
            return kept.Take(BrowseLimits.MaxRoots).ToList();
        }

        // What the desk browses under right now: the user's own list when they have made one, else the
        // automatic roots from the folders sessions recently ran in (the New chat dialog's recent
        // folders, then the live local CLI sessions' working folders).
        internal static BrowseSettings Effective(
            IReadOnlyList<string>? explicitRoots, bool home, IEnumerable<string> savedRecent,
            IEnumerable<string> liveFolders, IBrowseFileSystem fs) =>
            new(explicitRoots ?? DefaultRoots(savedRecent.Concat(liveFolders), fs), home);

        // The sentence Settings gives for a folder it will not add as a project root, or null when it
        // may be. Stricter than the contract in one place on purpose: a folder that IS the home folder
        // or holds it (a Windows C:\\) would hand the phone all of home with the toggle off, so it is
        // refused and the switch is named instead.
        internal static string? Refusal(string path, IReadOnlyList<string> existing, IBrowseFileSystem fs)
        {
            var expanded = ExpandHome(path.Trim(), fs);
            if (AbsoluteRoot(expanded, fs.IsWindows) is null || Canonical(fs, expanded) is not { } canonical)
                return "That isn't a folder path this computer can use.";
            if (!fs.DirectoryExists(canonical)) return "That folder doesn't exist.";
            if (Within(Canonical(fs, fs.Home) ?? fs.Home, canonical, fs))
                return "That's your home folder, or holds it. Use the switch below to let the phone browse all of home.";
            if (Denied(fs, canonical)) return "That's a system folder, which a phone can never browse.";
            if (existing.Any(e => Canonical(fs, ExpandHome(e, fs)) is { } c && Equal(c, canonical, fs)))
                return "That folder is already in the list.";
            if (existing.Count >= BrowseLimits.MaxRoots) return $"The list holds at most {BrowseLimits.MaxRoots} folders.";
            return null;
        }

        // The canonical form of a root as Settings should store it: what Refusal checked.
        internal static string StoredRoot(string path, IBrowseFileSystem fs) =>
            Canonical(fs, ExpandHome(path.Trim(), fs)) ?? path.Trim();

        private sealed record Root(string Path, bool Home);

        // scope.roots: the home folder first when the toggle is on, then the project roots. Each is
        // canonicalised; one that does not exist, is not a folder or is a system folder is not a
        // root; duplicates are one; a project root equal to the home folder while the toggle is on is
        // the home entry.
        private static List<Root> Roots(BrowseSettings settings, IBrowseFileSystem fs)
        {
            var roots = new List<Root>();
            bool Usable(string? canonical) => canonical is not null && fs.DirectoryExists(canonical) && !Denied(fs, canonical);

            string? home = Usable(Canonical(fs, fs.Home)) ? Canonical(fs, fs.Home) : null;
            if (settings.Home && home is not null) roots.Add(new Root(home, true));

            var projects = 0;
            foreach (var written in settings.Roots)
            {
                if (projects >= BrowseLimits.MaxRoots) break;
                var expanded = ExpandHome(written, fs);
                if (AbsoluteRoot(expanded, fs.IsWindows) is null) continue;
                var canonical = Canonical(fs, expanded);
                if (!Usable(canonical)) continue;
                if (roots.Any(r => Equal(r.Path, canonical!, fs))) continue;
                roots.Add(new Root(canonical!, false));
                projects++;
            }
            return roots;
        }

        // Step 4: `~` alone, or `~/` (on Windows also `~\`), is the home folder. Any other use of `~`
        // is an ordinary character.
        private static string ExpandHome(string path, IBrowseFileSystem fs)
        {
            if (path == "~") return fs.Home;
            if (path.Length > 1 && path[0] == '~' && IsSeparator(path[1], fs.IsWindows)) return fs.Home + path[1..];
            return path;
        }

        private static bool Browsable(IBrowseFileSystem fs, List<Root> roots, string canonical) =>
            roots.Any(r => Within(canonical, r.Path, fs)) && !Denied(fs, canonical);

        // ---- sorting ----------------------------------------------------------------------

        private static readonly UTF8Encoding Strict = new(false, true);

        // The UTF-8 bytes of a name, or null when it cannot be written as a JSON string (an unpaired
        // surrogate, which Windows allows in a name).
        internal static byte[]? Utf8(string name)
        {
            try { return Strict.GetBytes(name); }
            catch (EncoderFallbackException) { return null; }
        }

        // listing.sort: the names' UTF-8 bytes after folding ASCII A-Z to a-z and nothing else; names
        // equal under the fold compare by their unfolded bytes. No locale, no Unicode case mapping, no
        // numeric ordering: the same order in C#, Swift and Kotlin.
        internal static int Compare(string a, string b)
        {
            var x = Utf8(a) ?? Encoding.UTF8.GetBytes(a);
            var y = Utf8(b) ?? Encoding.UTF8.GetBytes(b);
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                int p = Fold(x[i]), q = Fold(y[i]);
                if (p != q) return p < q ? -1 : 1;
            }
            if (x.Length != y.Length) return x.Length < y.Length ? -1 : 1;
            for (var i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return 0;
        }

        private static byte Fold(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

        // ---- the request ------------------------------------------------------------------

        // Steps 3 to 10 of resolve.steps; the rate limit and the request's shape (steps 1 and 2) are
        // answered before this is reached.
        internal static BrowseOutcome Resolve(string path, bool hidden, BrowseSettings settings, IBrowseFileSystem fs)
        {
            var roots = Roots(settings, fs);

            // 3. top.
            if (path.Length == 0) return new BrowseOutcome(null, Top(roots, fs), "");

            // 4. home, 5. absolute.
            var expanded = ExpandHome(path, fs);
            if (AbsoluteRoot(expanded, fs.IsWindows) is null) return Refused("forbidden_path", null);

            // 6. canonical.
            if (Canonical(fs, expanded) is not { } canonical) return Refused("forbidden_path", null);

            // 7. scope: on the canonical path and on nothing else, whether or not it exists.
            if (!Browsable(fs, roots, canonical)) return Refused("forbidden_path", canonical);

            // 8. exists.
            if (!fs.DirectoryExists(canonical)) return Refused("unknown_path", canonical);

            // 9. read.
            if (fs.Enumerate(canonical, BrowseLimits.MaxScan) is not { } read) return Refused("unreadable", canonical);

            // 10. list.
            var outer = roots.Where(r => Within(canonical, r.Path, fs)).OrderBy(r => r.Path.Length).First();
            var candidates = new List<(string Name, string Canonical, bool Hidden)>();
            foreach (var entry in read.Entries)
            {
                if (entry.Kind == BrowseKind.File) continue;
                if (Utf8(entry.Name) is null) continue;
                var childCanonical = Join(canonical, entry.Name, fs.IsWindows);
                if (entry.Kind == BrowseKind.Link)
                {
                    if (Canonical(fs, childCanonical) is not { } target || !fs.DirectoryExists(target)) continue;
                    childCanonical = target;
                }
                if (!Browsable(fs, roots, childCanonical)) continue;
                var isHidden = entry.Name.StartsWith('.') || fs.HiddenAttribute(canonical, entry.Name);
                if (isHidden && !hidden) continue;
                candidates.Add((entry.Name, childCanonical, isHidden));
            }
            candidates.Sort((a, b) => Compare(a.Name, b.Name));
            var truncated = read.More || candidates.Count > BrowseLimits.MaxEntries;
            var items = candidates.Take(BrowseLimits.MaxEntries)
                .Select(c => Markers(fs, roots, c.Canonical, new BrowseItem(c.Name, Hidden: c.Hidden))).ToList();
            var parent = Equal(canonical, outer.Path, fs) ? "" : ParentOf(canonical, fs);
            return new BrowseOutcome(null, new BrowseListing(canonical, parent, outer.Path, items, truncated), canonical);
        }

        private static BrowseOutcome Refused(string code, string? canonical) => new(code, null, canonical);

        // ---- for CB-363's FILE frame ------------------------------------------------------
        //
        // The peer file server (PeerFileRules) answers "may this file be read" with exactly the
        // rules that answer "may this folder be listed", rather than a second copy of them. These
        // expose the three pieces it needs and change nothing about how Resolve uses them.

        // Whether a path is absolute in this filesystem's sense (step 5): no `~`, no relative, no
        // UNC or device path, no drive-relative or ADS form.
        internal static bool IsAbsolute(string path, IBrowseFileSystem fs) => AbsoluteRoot(path, fs.IsWindows) is not null;

        // Whether a canonical path is inside one of the settings' roots and outside every system
        // folder (step 7).
        internal static bool InScope(BrowseSettings settings, IBrowseFileSystem fs, string canonical) =>
            Browsable(fs, Roots(settings, fs), canonical);

        // Whether a canonical path is, or is inside, one of the system folders.
        internal static bool IsDenied(IBrowseFileSystem fs, string canonical) => Denied(fs, canonical);

        // Equal under the filesystem's own case rule.
        internal static bool SamePath(string a, string b, IBrowseFileSystem fs) => Equal(a, b, fs);

        // listing.top: the roots that exist, the home folder first, then the project roots sorted by
        // path. A Windows desk's every entry carries its drive, which is how a phone groups them.
        private static BrowseListing Top(List<Root> roots, IBrowseFileSystem fs)
        {
            var ordered = roots.Where(r => r.Home)
                .Concat(roots.Where(r => !r.Home).OrderBy(r => r.Path, Comparer<string>.Create(Compare))).ToList();
            var items = ordered.Select(r => new BrowseItem(
                LastComponent(r.Path, fs), r.Path, fs.IsWindows ? r.Path[..2] : null, r.Home))
                .Select(i => Markers(fs, roots, i.Path!, i)).ToList();
            return new BrowseListing("", "", "", items, false);
        }

        // The three markers, read from the folder itself. A folder the desk cannot read has none, and a
        // folder behind a macOS privacy prompt is not read at all (ProtectedFolders).
        private static BrowseItem Markers(IBrowseFileSystem fs, List<Root> roots, string folder, BrowseItem item) =>
            ProtectedFolders.IsProtected(fs, folder, roots.Select(r => r.Path)) ? item : item with
        {
            Git = fs.Lookup(folder, ".git") is not null,
            Claude = fs.Lookup(folder, "CLAUDE.md") is { Kind: not BrowseKind.Dir },
            Persona = fs.PersonaAt(folder),
        };

        // ---- shape ------------------------------------------------------------------------

        // frames-v11 list_dir.rules: a string of at most 4,096 UTF-8 bytes with no C0 control and no
        // DEL. The empty string is the top level.
        internal static bool PathShapeOk(string path)
        {
            if (Encoding.UTF8.GetByteCount(path) > BrowseLimits.MaxPathBytes) return false;
            foreach (var c in path) if (c < 0x20 || c == 0x7F) return false;
            return true;
        }
    }

    // The per-phone request bucket (frames-v11 limits_rules), in integers: the phone holds a credit in
    // milliseconds that starts at burst x interval, grows by the milliseconds elapsed since its last
    // request up to that same ceiling, and a request is accepted when the credit is at least one
    // interval and then costs one. A refused request costs nothing.
    internal sealed class BrowseRateLimiter
    {
        private const long Ceiling = (long)BrowseLimits.RateBurst * BrowseLimits.RateIntervalMs;
        private readonly Dictionary<string, (long Credit, long At)> _phones = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        internal bool TryAccept(string phoneId, long nowMs)
        {
            lock (_gate)
            {
                var (credit, at) = _phones.TryGetValue(phoneId, out var s) ? s : (Ceiling, nowMs);
                credit = Math.Min(Ceiling, credit + Math.Max(0, nowMs - at));
                var accepted = credit >= BrowseLimits.RateIntervalMs;
                if (accepted) credit -= BrowseLimits.RateIntervalMs;
                _phones[phoneId] = (credit, nowMs);
                return accepted;
            }
        }

    }

    // The v11 frames this desk writes, in frames-v4's canonical form.
    internal static class BrowseFrames
    {
        internal const string CapsCk = "caps";

        internal static readonly string Caps =
            new CanonicalJson().Str("t", "caps").Array("caps", [CanonicalJson.Quote("list_dir"), CanonicalJson.Quote(RunFrames.Cap)]).ToString();

        private static string Entry(BrowseItem e)
        {
            var j = new CanonicalJson().Str("name", e.Name);
            if (e.Path is not null) j.Str("path", e.Path);
            if (e.Drive is not null) j.Str("drive", e.Drive);
            return j.Flag("home", e.Home).Flag("git", e.Git).Flag("claude", e.Claude)
                .Flag("persona", e.Persona).Flag("hidden", e.Hidden).ToString();
        }

        private static string Frame(string reference, BrowseListing l, int from, int total, IEnumerable<string> entries) =>
            new CanonicalJson().Str("t", "dir").Str("ref", reference).Str("path", l.Path).Str("parent", l.Parent)
                .Str("root", l.Root).Num("entries_total", total).Num("from_entry", from)
                .Array("entries", entries).Flag("truncated", l.Truncated).ToString();

        // dir.rules, chunking: the listing's entries split over one or more frames with the same ref,
        // greedily, so each canonical frame is at most 40,000 bytes and adding its next entry would
        // exceed that. An empty listing is one frame with entries_total 0.
        internal static List<string> Dir(string reference, BrowseListing listing)
        {
            var entries = listing.Items.Select(Entry).ToList();
            var frames = new List<string>();
            var from = 0;
            do
            {
                var count = 0;
                var size = Encoding.UTF8.GetByteCount(Frame(reference, listing, from, entries.Count, []));
                while (from + count < entries.Count)
                {
                    var add = Encoding.UTF8.GetByteCount(entries[from + count]) + (count > 0 ? 1 : 0);
                    if (count > 0 && size + add > BrowseLimits.MaxDirPlaintextBytes) break;
                    size += add;
                    count++;
                }
                frames.Add(Frame(reference, listing, from, entries.Count, entries.Skip(from).Take(count)));
                from += count;
            } while (from < entries.Count);
            return frames;
        }
    }

    // The persona marker's own-level call. LocalPersona walks up from a folder; a ceiling at the
    // folder stops it at that folder's own four files, so a persona in a parent does not mark a child
    // and neither does one in the user's config (CB-339 plan, section 3A: asserted by a test, not
    // taken on trust).
    internal static class BrowsePersona
    {
        internal static bool At(string folder)
        {
            var candidates = LocalPersona.CandidatesBelow(folder, [], SessionSource.ClaudeCode, "", folder);
            return !LocalPersona.ResolveFrom(candidates, folder).IsEmpty;
        }
    }
}
