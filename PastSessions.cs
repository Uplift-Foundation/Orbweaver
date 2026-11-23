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
    // MessageCount is null in the listing: counting needs the whole file,
    // and the list is built from a head and a tail read only (QA measured
    // 4962 Claude transcripts, 3.5 GB, in one 30-day window on the mini). It
    // is filled in per visible row by PastSessions.CountMessages.
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
        int? MessageCount,
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
        // never supply the cwd or id, even when one comes first. Only the head
        // window is searched: a file whose cwd first appears past it lists as
        // FolderMissing rather than being read to the end to find one. A file
        // whose head holds no user turn
        // is not a conversation anyone could want back — a hook-only stub, or
        // a session that was opened and closed — and is skipped.
        //
        // Only sessions a person started are listed (CB-361 ruling A): the
        // rows' `entrypoint` is "cli" for an interactive session. On the mini
        // 4484 of 4971 transcripts were "sdk-cli" (headless, queue-driven);
        // claude-desktop and sdk-ts were also seen. A transcript with no
        // entrypoint at all predates the field and is kept — the filter
        // excludes what it can name, it does not guess.
        //
        // The title is the custom-title.json sidecar Claude writes beside a
        // renamed session (projects/<slug>/<id>/custom-title.json,
        // {"customTitle":…}, 46 of them on the mini), else the newest
        // custom-title or ai-title row in the tail, else the first prompt.
        // Map already drops <command-name>/<command-message> rows as noise and
        // turns a <teammate-message> into a relayed row, so "the first prompt"
        // is the first thing a person actually typed.
        internal static PastSession? FromClaude(
            IReadOnlyList<string> head,
            IReadOnlyList<string> tail,
            string? sidecarJson,
            string path,
            DateTimeOffset lastActivity,
            Func<string, bool> folderExists)
        {
            var turns = ChatTranscript.Map(head);
            if (!HasUserTurn(turns)) return null;

            // The opening rows (mode, permission-mode, session-owner, …) carry
            // sessionId and no cwd; cwd and entrypoint arrive on the first user
            // or attachment row, at index 26 at worst across 4967 files.
            string? cwd = null, id = null, entrypoint = null;
            foreach (var line in head)
            {
                if (!TryObject(line, out var doc)) continue;
                using (doc)
                {
                    var row = doc.RootElement;
                    if (Bool(row, "isSidechain")) continue;
                    cwd ??= NonEmpty(Str(row, "cwd"));
                    id ??= NonEmpty(Str(row, "sessionId"));
                    entrypoint ??= NonEmpty(Str(row, "entrypoint"));
                    if (cwd is not null && id is not null && entrypoint is not null) break;
                }
            }

            if (entrypoint is not null && entrypoint != "cli") return null;

            id ??= Path.GetFileNameWithoutExtension(path);
            if (!IsSafeId(id)) return null;

            var title = Field(sidecarJson, "customTitle") ?? TranscriptIdentity.From(tail).Title ?? FirstUserText(turns);
            return Make(NewChatCli.ClaudeCode, id, cwd, title, lastActivity, path, folderExists);
        }

        // ---- Codex ----------------------------------------------------------------

        // $CODEX_HOME/sessions/yyyy/mm/dd/rollout-<iso>-<uuid>.jsonl. The first
        // line is session_meta, whose payload carries the id and the cwd; the
        // file name is the fallback for the id. state_N.sqlite is deliberately
        // not read — see docs/codex-findings.md on its WAL refusing a read-only
        // open while Codex is idle.
        //
        // Only payload.originator "codex-tui" is listed (ruling A): on the mini
        // 1642 of 1678 rollouts were "openclaw", which OpenClaw's own "Open
        // existing" reaches, and 22 "codex_exec". A rollout with no originator
        // is kept, as for Claude's entrypoint.
        //
        // Unlike Claude, a rollout with no prompt in its head is still listed —
        // a real codex-tui rollout can be session_meta alone, and resuming it
        // is legitimate. Its title is then the folder's name.
        internal static PastSession? FromCodex(
            IReadOnlyList<string> head,
            string path,
            DateTimeOffset lastActivity,
            Func<string, bool> folderExists)
        {
            var turns = CodexTranscript.Map(head);

            string? cwd = null, id = null, originator = null;
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
                    originator = NonEmpty(Str(p, "originator"));
                    break;
                }
            }

            if (originator is not null && originator != "codex-tui") return null;

            id ??= CodexIdFromFileName(path);
            if (!IsSafeId(id)) return null;

            var title = FirstUserText(turns) ?? (cwd is null ? null : Path.GetFileName(Trim(cwd)));
            return Make(NewChatCli.Codex, id!, cwd, title, lastActivity, path, folderExists);
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
        // summary.json's generated_title, else the first user turn
        // (generated_title is absent on a session seconds old). summary.json's
        // info.cwd is preferred to decoding the directory name when present.
        //
        // A session_kind of "headless" (`grok -p`) is not listed (ruling A).
        // Only that value is matched: what an interactive session's kind is
        // called was not observed, so nothing positive is required.
        internal static PastSession? FromGrok(
            string? summaryJson,
            IReadOnlyList<string> head,
            string path,
            DateTimeOffset lastActivity,
            Func<string, bool> folderExists)
        {
            var turns = GrokTranscript.Map(head);
            if (!HasUserTurn(turns)) return null;
            if (Field(summaryJson, "session_kind") == "headless") return null;

            var sessionDir = Path.GetDirectoryName(path);
            var id = sessionDir is null ? null : Path.GetFileName(sessionDir);
            if (!IsSafeId(id)) return null;

            var encoded = Path.GetFileName(Path.GetDirectoryName(sessionDir));
            var cwd = Field(summaryJson, "info", "cwd") ?? (string.IsNullOrEmpty(encoded) ? null : Uri.UnescapeDataString(encoded));

            var title = Field(summaryJson, "generated_title") ?? FirstUserText(turns);
            return Make(NewChatCli.Grok, id!, cwd, title, lastActivity, path, folderExists);
        }

        // One string field of a small JSON object (summary.json, the Claude
        // title sidecar), trimmed; null for anything unusable. A second name
        // reads one level down: Field(json, "info", "cwd").
        internal static string? Field(string? json, string name, string? inner = null)
        {
            if (string.IsNullOrWhiteSpace(json) || !TryObject(json.Trim(), out var doc)) return null;
            using (doc)
            {
                var e = doc.RootElement;
                if (inner is not null)
                {
                    if (!e.TryGetProperty(name, out e) || e.ValueKind != JsonValueKind.Object) return null;
                    name = inner;
                }
                return NonEmpty(Str(e, name)?.Trim());
            }
        }

        // ---- counting -------------------------------------------------------------

        // The UI's entry point for one row's count: streams the file, cached on
        // its stamp. Blocking — call it off the UI thread. Null when the file
        // could not be read.
        internal static Func<NewChatCli, string, int?>? CountForTests;

        internal static int? CountMessages(NewChatCli cli, string path) =>
            CountForTests is { } fake ? fake(cli, path) : PastSessionIndex.Shared.Count(cli, path);

        // The count over a stream of lines, holding one line at a time. Only
        // lines that pass the format's own IsInteresting prefilter are parsed.
        //
        // Claude and Codex rows each map on their own, so a row's turns are the
        // single-row Map. Grok streams a message as many chunk rows that its
        // Map joins into one turn, so a Grok turn is a run of one chunk kind,
        // and each tool call is a turn of its own — what the chat panel shows
        // without building it.
        internal static int CountLines(NewChatCli cli, IEnumerable<string> lines)
        {
            var count = 0;
            string? run = null;
            var one = new string[1];
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                switch (cli)
                {
                    case NewChatCli.ClaudeCode when ChatTranscript.IsInteresting(line):
                        one[0] = line;
                        count += ChatTranscript.Map(one).Count;
                        break;
                    case NewChatCli.Codex when CodexTranscript.IsInteresting(line):
                        one[0] = line;
                        count += CodexTranscript.Map(one).Count;
                        break;
                    case NewChatCli.Grok when GrokTranscript.IsInteresting(line):
                        var kind = GrokKind(line);
                        if (kind == "tool_call" || kind != run) count++;
                        run = kind;
                        break;
                }
            }
            return count;
        }

        private static string GrokKind(string line) =>
            line.Contains("user_message_chunk", StringComparison.Ordinal) ? "user"
            : line.Contains("agent_message_chunk", StringComparison.Ordinal) ? "assistant"
            : line.Contains("agent_thought_chunk", StringComparison.Ordinal) ? "thought"
            : "tool_call";

        // ---- shared ---------------------------------------------------------------

        private static PastSession Make(
            NewChatCli cli, string id, string? cwd, string? title, DateTimeOffset lastActivity,
            string path, Func<string, bool> folderExists) =>
            new(cli, id, cwd, Clip(title) ?? "(untitled)", lastActivity, MessageCount: null, path,
                FolderMissing: cwd is null || !folderExists(cwd));

        // A user turn, or a message relayed in from a teammate or another
        // session (CB-330) — an agent team member's whole conversation opens
        // with a <teammate-message>, which Map turns into a relayed System row,
        // and that session is as resumable as one a person typed into.
        private static bool HasUserTurn(IReadOnlyList<ChatTranscript.Row> turns) =>
            turns.Any(r => r.Turn.Role == ChatRole.User || r.Turn.Relay is not null);

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
            // A line that opens with '{' and parses at all is an object, so a
            // successful parse is the whole test.
            try
            {
                doc = JsonDocument.Parse(line);
                return true;
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
    //
    // A listing reads a bounded head of every transcript, and a bounded tail
    // of Claude's for its /rename title — never a whole file. The whole-file
    // count is a separate, per-row, streamed read (Count).
    internal sealed class PastSessionIndex
    {
        // Every touch of the disk, swappable so the walk and the cache can be
        // driven from a temp tree or from memory.
        internal sealed record Seams(
            Func<string, IEnumerable<string>> Directories,
            Func<string, string, IEnumerable<string>> Files,
            Func<string, (long Length, DateTime WriteUtc)?> Stamp,
            Func<string, int, IReadOnlyList<string>> Head,
            Func<string, IReadOnlyList<string>?> CodexHead,
            Func<string, IReadOnlyList<string>> Tail,
            Func<string, IEnumerable<string>> Lines,
            Func<string, string?> ReadText,
            Func<string, bool> DirectoryExists,
            Func<string, string> RealPath);

        [ExcludeFromCodeCoverage]
        internal static Seams Real { get; } = new(
            TranscriptReader.SafeDirectories,
            TranscriptReader.SafeFiles,
            StampOf,
            TranscriptReader.HeadLines,
            path => TranscriptReader.HeadRows(path, CodexFirstRowCap, TranscriptReader.HeadBytes),
            path => TranscriptReader.TailLines(path),
            LinesOf,
            TextOf,
            Directory.Exists,
            RealPathOf);

        internal static PastSessionIndex Shared { get; } = new(Real);

        private readonly Seams _seams;
        private readonly ConcurrentDictionary<string, ((long, DateTime) Stamp, PastSession? Value)> _cache = new();
        private readonly ConcurrentDictionary<string, ((long, DateTime) Stamp, int Value)> _counts = new();

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

            // In parallel: a cold first open is thousands of small bounded reads
            // (4626 Claude transcripts in one window on the mini), and the tail
            // read dominates — measured 8.2 s serial against 1.5 s without it.
            // Order does not matter; PastSessions.List sorts.
            return Transcripts(cli, home)
                .AsParallel()
                .Select(path =>
                {
                    // The mtime pre-filter: a file outside the window is never opened.
                    if (_seams.Stamp(path) is not { } stamp || stamp.WriteUtc < notBefore) return null;
                    if (!_cache.TryGetValue(path, out var hit) || hit.Stamp != stamp)
                    {
                        hit = (stamp, Parse(cli, path, stamp.WriteUtc));
                        _cache[path] = hit;
                    }
                    return hit.Value is { } session
                        ? session with { FolderMissing = session.Cwd is null || !_seams.DirectoryExists(session.Cwd) }
                        : null;
                })
                .Where(session => session is not null)
                .Select(session => session!)
                .ToList();
        }

        // Codex's row 0 is session_meta carrying the model's base instructions:
        // measured on the mini over 1678 rollouts, median 78 KB, p99 260 KB,
        // largest 337 KB. No fixed byte window holds it, and a window that cuts
        // it drops the id, the cwd and every prompt after it. So Codex's head
        // is row 0 read whole up to this cap, then the usual 64 KB of rows; a
        // row 0 over the cap means the file is skipped rather than read on.
        internal const int CodexFirstRowCap = 8 * 1024 * 1024;

        private PastSession? Parse(NewChatCli cli, string path, DateTime writeUtc)
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(writeUtc, DateTimeKind.Utc));
            switch (cli)
            {
                case NewChatCli.ClaudeCode:
                    // projects/<slug>/<id>.jsonl -> projects/<slug>/<id>/custom-title.json
                    var sidecar = Path.Join(Path.ChangeExtension(path, null), "custom-title.json");
                    return PastSessions.FromClaude(
                        _seams.Head(path, TranscriptReader.HeadBytes), _seams.Tail(path), _seams.ReadText(sidecar),
                        path, at, _seams.DirectoryExists);
                case NewChatCli.Codex:
                    return _seams.CodexHead(path) is { } head
                        ? PastSessions.FromCodex(head, path, at, _seams.DirectoryExists)
                        : null;
                default:
                    return PastSessions.FromGrok(
                        _seams.ReadText(Path.Join(Path.GetDirectoryName(path), "summary.json")),
                        _seams.Head(path, TranscriptReader.HeadBytes), path, at, _seams.DirectoryExists);
            }
        }

        // One transcript's message count, streamed a line at a time and cached
        // on its stamp. Null when the file is gone.
        internal int? Count(NewChatCli cli, string path)
        {
            if (_seams.Stamp(path) is not { } stamp) return null;
            if (_counts.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Value;
            var count = PastSessions.CountLines(cli, _seams.Lines(path));
            _counts[path] = (stamp, count);
            return count;
        }

        // ---- the walk -----------------------------------------------------------

        internal IEnumerable<string> Transcripts(NewChatCli cli, string home) => cli switch
        {
            NewChatCli.ClaudeCode => ClaudeTranscripts(home),
            NewChatCli.Codex => CodexTranscripts(home),
            _ => GrokTranscripts(home),
        };

        // Only files directly under projects/<slug>/ — a sub-agent's transcript
        // lives a level deeper (<id>/subagents/…) and is not a session of its own.
        //
        // The projects directory is resolved to its real path first (ruling B):
        // on the mini two account roots symlink `projects` to the default
        // root's, so without this the same transcript was cached and listed
        // under three paths. Every account there shows one list, accepted.
        private IEnumerable<string> ClaudeTranscripts(string home) =>
            _seams.Directories(_seams.RealPath(Path.Combine(home, "projects")))
                .SelectMany(dir => _seams.Files(dir, "*.jsonl"));

        // sessions/yyyy/mm/dd. Every day directory is walked and the window is
        // applied to each file's mtime, never to the directory's date: a resume
        // appends to the ORIGINAL rollout (measured, 9 rows to 18 in the same
        // file), so a session started 40 days ago and resumed yesterday lives
        // in a 40-day-old directory — exactly the session this feature makes.
        private IEnumerable<string> CodexTranscripts(string home) =>
            _seams.Directories(Path.Combine(home, "sessions"))
                .SelectMany(_seams.Directories)
                .SelectMany(_seams.Directories)
                .SelectMany(day => _seams.Files(day, "rollout-*.jsonl"));

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

        // One line at a time, never the whole file. A file that disappears or
        // errors part way simply ends the stream.
        [ExcludeFromCodeCoverage]
        private static IEnumerable<string> LinesOf(string path)
        {
            StreamReader? reader = null;
            try
            {
                reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            }
            catch { yield break; }

            using (reader)
            {
                while (true)
                {
                    string? line;
                    try { line = reader.ReadLine(); }
                    catch { yield break; }
                    if (line is null) yield break;
                    yield return line;
                }
            }
        }

        // A symlinked directory's final target; the path itself otherwise.
        [ExcludeFromCodeCoverage]
        private static string RealPathOf(string path)
        {
            try { return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
            catch { return path; }
        }

        [ExcludeFromCodeCoverage]
        private static string? TextOf(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }
    }
}
