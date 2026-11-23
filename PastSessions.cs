using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // One past conversation a local CLI left on disk, as New Chat's Resume mode
    // lists it (CB-361). Everything here is read off the transcript itself:
    // the id the CLI resumes by, the folder it ran in, a title a person would
    // recognise, when it last moved and how long it is.
    //
    // FolderMissing is decided when the row is read rather than when it is
    // drawn, so the list and the launch agree about it: a session whose folder
    // is gone is shown greyed and is never resumed somewhere else instead,
    // which would put a conversation about one project in a different one.
    internal sealed record PastSession(
        NewChatCli Cli,
        string Id,
        string? Cwd,
        string Title,
        DateTimeOffset LastActivity,
        int MessageCount,
        string TranscriptPath,
        bool FolderMissing);

    // A row in the list: the session, and whether an orb is already running it.
    // A live row focuses that orb rather than starting a second process on the
    // same conversation.
    internal sealed record PastSessionRow(PastSession Session, bool IsLive);

    // The pure half of Resume: three readers, one per transcript format, and
    // the list's filter and order. No files, windows or settings — the IO is
    // in PastSessionIndex below and is kept as thin as it can be.
    internal static class PastSessions
    {
        // How far back the list goes until "Show older" is ticked.
        internal static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

        // An id is only ever put on a command line if it is this. Every id the
        // three CLIs write is a UUID, so nothing real is lost, and it also
        // passes NewChatCommand.CmdSafe for the Windows cmd /k line.
        private static readonly Regex SafeId = new("^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant);

        internal static bool IsSafeId(string? id) => !string.IsNullOrEmpty(id) && SafeId.IsMatch(id);

        // The longest a title is shown at. A first prompt can be a pasted page.
        internal const int TitleLength = 120;

        // ---- the list ---------------------------------------------------------

        // Newest first; the 30-day cut unless showOlder; a case-insensitive
        // match of filterText against title and folder; an exact folder filter;
        // each row marked live if its id is in `live`.
        internal static IReadOnlyList<PastSessionRow> List(
            IEnumerable<PastSession> sessions,
            IReadOnlySet<string> live,
            string? filterText,
            string? folder,
            DateTimeOffset now,
            bool showOlder)
        {
            var text = filterText?.Trim() ?? "";
            var cutoff = now - RecentWindow;

            return sessions
                .Where(s => showOlder || s.LastActivity >= cutoff)
                .Where(s => text.Length == 0
                    || s.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || (s.Cwd?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                .Where(s => string.IsNullOrEmpty(folder) || SameFolder(s.Cwd, folder))
                .GroupBy(s => (s.Cli, s.Id))
                .Select(g => g.OrderByDescending(s => s.LastActivity).First())
                .OrderByDescending(s => s.LastActivity)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new PastSessionRow(s, live.Contains(s.Id)))
                .ToList();
        }

        // The distinct folders the folder filter offers, most recently used first.
        internal static IReadOnlyList<string> Folders(IEnumerable<PastSession> sessions) =>
            sessions
                .Where(s => !string.IsNullOrEmpty(s.Cwd))
                .GroupBy(s => Trim(s.Cwd!), FolderComparer)
                .OrderByDescending(g => g.Max(s => s.LastActivity))
                .Select(g => g.Key)
                .ToList();

        private static StringComparer FolderComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static bool SameFolder(string? cwd, string folder) =>
            cwd is not null && FolderComparer.Equals(Trim(cwd), Trim(folder));

        private static string Trim(string path) =>
            path.Length > 1 ? path.TrimEnd('/', '\\') : path;

        // ---- Claude Code --------------------------------------------------------

        // <config>/projects/<slug>/<id>.jsonl. The slug is lossy (every
        // non-alphanumeric becomes '-'), so the folder comes from the `cwd`
        // field the rows carry, never from the directory name. The id is the
        // `sessionId` field, falling back to the file name.
        //
        // Sidechain rows (a sub-agent's turns written into the parent's file)
        // never supply the cwd or id. A file whose turns include no user turn
        // is not a conversation anyone could want back — a hook-only stub, or
        // a session that was opened and closed — and is skipped.
        internal static PastSession? FromClaude(
            IReadOnlyList<string> head,
            IReadOnlyList<string> tail,
            IReadOnlyList<ChatTranscript.Row> turns,
            string path,
            DateTimeOffset lastActivity,
            Func<string, bool> folderExists)
        {
            if (!HasUserTurn(turns)) return null;

            string? cwd = null, id = null;
            foreach (var line in head)
            {
                if (!TryObject(line, out var doc)) continue;
                using (doc)
                {
                    var row = doc.RootElement;
                    if (Bool(row, "isSidechain")) continue;
                    cwd ??= NonEmpty(Str(row, "cwd"));
                    id ??= NonEmpty(Str(row, "sessionId"));
                    if (cwd is not null && id is not null) break;
                }
            }

            id ??= Path.GetFileNameWithoutExtension(path);
            if (!IsSafeId(id)) return null;

            var title = TranscriptIdentity.From(tail).Title ?? FirstUserText(turns);
            return Make(NewChatCli.ClaudeCode, id, cwd, title, lastActivity, turns.Count, path, folderExists);
        }

        // ---- Codex ----------------------------------------------------------------

        // $CODEX_HOME/sessions/yyyy/mm/dd/rollout-<iso>-<uuid>.jsonl. The first
        // line is session_meta, whose payload carries the id and the cwd; the
        // file name is the fallback for the id. state_N.sqlite is deliberately
        // not read — see docs/codex-findings.md on its WAL refusing a read-only
        // open while Codex is idle.
        internal static PastSession? FromCodex(
            IReadOnlyList<string> head,
            IReadOnlyList<ChatTranscript.Row> turns,
            string path,
            DateTimeOffset lastActivity,
            Func<string, bool> folderExists)
        {
            if (!HasUserTurn(turns)) return null;

            string? cwd = null, id = null;
            foreach (var line in head)
            {
                if (!TryObject(line, out var doc)) continue;
                using (doc)
                {
                    var row = doc.RootElement;
                    if (Str(row, "type") != "session_meta") continue;
                    if (!row.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                    cwd = NonEmpty(Str(p, "cwd"));
                    id = NonEmpty(Str(p, "id"));
                    break;
                }
            }

            id ??= CodexIdFromFileName(path);
            if (!IsSafeId(id)) return null;

            return Make(NewChatCli.Codex, id!, cwd, FirstUserText(turns), lastActivity, turns.Count, path, folderExists);
        }

        // rollout-2026-11-20T10-11-12-<8-4-4-4-12 uuid>.jsonl: the last 36
        // characters of the name are the id.
        internal static string? CodexIdFromFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name.Length > 36 && name[^37] == '-' ? name[^36..] : null;
        }

        // ---- Grok -----------------------------------------------------------------

        // $GROK_HOME/sessions/<url-encoded cwd>/<id>/updates.jsonl, with a
        // summary.json beside it. The folder is the encoded directory name and
        // the id is the directory the transcript sits in; the title is
        // summary.json's generated_title, else the first user turn.
        internal static PastSession? FromGrok(
            string? summaryJson,
            IReadOnlyList<ChatTranscript.Row> turns,
            string path,
            DateTimeOffset lastActivity,
            Func<string, bool> folderExists)
        {
            if (!HasUserTurn(turns)) return null;

            var sessionDir = Path.GetDirectoryName(path);
            var id = sessionDir is null ? null : Path.GetFileName(sessionDir);
            if (!IsSafeId(id)) return null;

            var encoded = Path.GetFileName(Path.GetDirectoryName(sessionDir!) ?? "");
            var cwd = encoded.Length > 0 ? Uri.UnescapeDataString(encoded) : null;

            var title = GrokTitle(summaryJson) ?? FirstUserText(turns);
            return Make(NewChatCli.Grok, id!, cwd, title, lastActivity, turns.Count, path, folderExists);
        }

        internal static string? GrokTitle(string? summaryJson)
        {
            if (string.IsNullOrWhiteSpace(summaryJson) || !TryObject(summaryJson, out var doc)) return null;
            using (doc) return NonEmpty(Str(doc.RootElement, "generated_title")?.Trim());
        }

        // ---- shared ---------------------------------------------------------------

        private static PastSession Make(
            NewChatCli cli, string id, string? cwd, string? title, DateTimeOffset lastActivity,
            int count, string path, Func<string, bool> folderExists) =>
            new(cli, id, cwd, Clip(title) ?? "(untitled)", lastActivity, count, path,
                FolderMissing: cwd is null || !folderExists(cwd));

        private static bool HasUserTurn(IReadOnlyList<ChatTranscript.Row> turns) =>
            turns.Any(r => r.Turn.Role == ChatRole.User);

        private static string? FirstUserText(IReadOnlyList<ChatTranscript.Row> turns) =>
            turns.Where(r => r.Turn.Role == ChatRole.User)
                .Select(r => NonEmpty(r.Turn.Text.Trim()))
                .FirstOrDefault(t => t is not null);

        // One line, at most TitleLength characters.
        internal static string? Clip(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var flat = Regex.Replace(title.Trim(), @"\s+", " ");
            return flat.Length <= TitleLength ? flat : flat[..(TitleLength - 1)].TrimEnd() + "…";
        }

        private static bool TryObject(string line, [NotNullWhen(true)] out JsonDocument? doc)
        {
            doc = null;
            if (line.Length == 0 || line[0] != '{') return false;
            try
            {
                doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return true;
                doc.Dispose();
                doc = null;
                return false;
            }
            catch (JsonException) { return false; }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    // The IO half: which files to read for an account, read them through a
    // stamp cache, and hand each to its reader. One instance lives for the
    // app (Shared), so re-opening Resume re-reads only the transcripts whose
    // length or write time moved — the RunSources.Parsed pattern.
    internal sealed class PastSessionIndex
    {
        // Every touch of the disk, swappable so the walk and the cache can be
        // driven from a temp tree or from memory.
        internal sealed record Seams(
            Func<string, IEnumerable<string>> Directories,
            Func<string, string, IEnumerable<string>> Files,
            Func<string, (long Length, DateTime WriteUtc)?> Stamp,
            Func<string, IReadOnlyList<string>> AllLines,
            Func<string, string?> ReadText,
            Func<string, bool> DirectoryExists);

        [ExcludeFromCodeCoverage]
        internal static Seams Real { get; } = new(
            TranscriptReader.SafeDirectories,
            TranscriptReader.SafeFiles,
            StampOf,
            AllLinesOf,
            TextOf,
            Directory.Exists);

        internal static PastSessionIndex Shared { get; } = new(Real);

        private readonly Seams _seams;
        private readonly ConcurrentDictionary<string, ((long, DateTime) Stamp, PastSession? Value)> _cache = new();

        internal PastSessionIndex(Seams seams) => _seams = seams;

        // The UI's seam: a test hands Resume mode a fixed list without a disk.
        internal static Func<NewChatCli, string?, bool, DateTimeOffset, IReadOnlyList<PastSession>>? LoadForTests;

        // Every past session for one CLI and account. profileDir is what the
        // account picker chose (null for the default), resolved the same way a
        // launch resolves it, so the list shows exactly the sessions a resume
        // under that account can find. Blocking: call it off the UI thread.
        internal static IReadOnlyList<PastSession> Load(NewChatCli cli, string? profileDir, bool showOlder, DateTimeOffset now) =>
            LoadForTests?.Invoke(cli, profileDir, showOlder, now)
            ?? Shared.Read(cli, HomeFor(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), profileDir), showOlder, now);

        // The account's home: the chosen profile, else ~/<default dir>.
        internal static string HomeFor(NewChatCli cli, string userHome, string? profileDir) =>
            NewChatLauncher.ConfigDirFor(cli, userHome, profileDir)
            ?? Path.Combine(userHome, NewChatAccountHome.For(cli).DefaultDirName);

        internal IReadOnlyList<PastSession> Read(NewChatCli cli, string home, bool showOlder, DateTimeOffset now)
        {
            var notBefore = showOlder ? DateTime.MinValue : (now - PastSessions.RecentWindow).UtcDateTime;
            var list = new List<PastSession>();
            foreach (var path in Transcripts(cli, home, notBefore))
            {
                if (_seams.Stamp(path) is not { } stamp) continue;
                if (stamp.WriteUtc < notBefore) continue;
                if (!_cache.TryGetValue(path, out var hit) || hit.Stamp != stamp)
                {
                    hit = (stamp, Parse(cli, path, stamp.WriteUtc));
                    _cache[path] = hit;
                }
                if (hit.Value is { } session)
                    list.Add(session with { FolderMissing = session.Cwd is null || !_seams.DirectoryExists(session.Cwd) });
            }
            return list;
        }

        private PastSession? Parse(NewChatCli cli, string path, DateTime writeUtc)
        {
            var lines = _seams.AllLines(path);
            var at = new DateTimeOffset(DateTime.SpecifyKind(writeUtc, DateTimeKind.Utc));
            var head = lines.Take(HeadRows).ToList();
            switch (cli)
            {
                case NewChatCli.ClaudeCode:
                    return PastSessions.FromClaude(head, lines, ChatTranscript.Map(lines), path, at, _seams.DirectoryExists);
                case NewChatCli.Codex:
                    return PastSessions.FromCodex(head, CodexTranscript.Map(lines), path, at, _seams.DirectoryExists);
                default:
                    var summary = RunSources.GrokSummaryOf(path) is { } s ? _seams.ReadText(s) : null;
                    return PastSessions.FromGrok(summary, GrokTranscript.Map(lines), path, at, _seams.DirectoryExists);
            }
        }

        // The rows the cwd and id are looked for in. Claude can open with a
        // few summary or snapshot rows before the first one carrying a cwd.
        internal const int HeadRows = 50;

        // ---- the walk -----------------------------------------------------------

        internal IEnumerable<string> Transcripts(NewChatCli cli, string home, DateTime notBeforeUtc) => cli switch
        {
            NewChatCli.ClaudeCode => ClaudeTranscripts(home),
            NewChatCli.Codex => CodexTranscripts(home, notBeforeUtc),
            _ => GrokTranscripts(home),
        };

        // Only files directly under projects/<slug>/ — a sub-agent's transcript
        // lives a level deeper (<id>/subagents/…) and is not a session of its own.
        private IEnumerable<string> ClaudeTranscripts(string home) =>
            _seams.Directories(Path.Combine(home, "projects"))
                .SelectMany(dir => _seams.Files(dir, "*.jsonl"));

        // sessions/yyyy/mm/dd: a day directory older than the window is skipped
        // without listing it, so the 30-day view does not walk a year of days.
        private IEnumerable<string> CodexTranscripts(string home, DateTime notBeforeUtc)
        {
            var floor = notBeforeUtc == DateTime.MinValue ? DateOnly.MinValue : DateOnly.FromDateTime(notBeforeUtc).AddDays(-1);
            foreach (var year in _seams.Directories(Path.Combine(home, "sessions")))
            foreach (var month in _seams.Directories(year))
            foreach (var day in _seams.Directories(month))
            {
                if (DayOf(year, month, day) is { } date && date < floor) continue;
                foreach (var file in _seams.Files(day, "rollout-*.jsonl")) yield return file;
            }
        }

        internal static DateOnly? DayOf(string year, string month, string day) =>
            int.TryParse(Path.GetFileName(year), out var y)
            && int.TryParse(Path.GetFileName(month), out var m)
            && int.TryParse(Path.GetFileName(day), out var d)
            && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
                ? new DateOnly(y, m, d)
                : null;

        private IEnumerable<string> GrokTranscripts(string home) =>
            _seams.Directories(Path.Combine(home, "sessions"))
                .SelectMany(_seams.Directories)
                .Select(dir => Path.Combine(dir, "updates.jsonl"))
                .Where(file => _seams.Stamp(file) is not null);

        // ---- real IO ------------------------------------------------------------

        [ExcludeFromCodeCoverage]
        private static (long, DateTime)? StampOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch { return null; }
        }

        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<string> AllLinesOf(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return Array.Empty<string>(); }
        }

        [ExcludeFromCodeCoverage]
        private static string? TextOf(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }
    }
}
