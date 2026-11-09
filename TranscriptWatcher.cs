namespace Orbweaver
{
    // One phone's watch on one session's transcript (CB-316). Holds what was
    // last sent — the parsed-line count and the first line — and turns each
    // poll into the windows TranscriptWatch says are owed: nothing, a tail, or
    // a reset backfill.
    //
    // Polled rather than event-driven: a FileSystemWatcher fires on writes the
    // parser cannot read yet and differs between macOS and Windows, while a
    // stat every tick is cheap and identical on both. The debounce is the
    // contract's: a change is parsed once the file has been still for 250 ms,
    // so rows appended within 250 ms of each other become one window. The file,
    // the parser and the clock come in as functions, so every case is a unit
    // test and the integration suite runs the real file and parsers through it.
    internal sealed class TranscriptWatcher
    {
        internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

        // What a poll can see of the file without reading it.
        internal readonly record struct Stamp(string Path, long Length, DateTime Written);

        internal sealed record Seams(
            Func<string?> PathOf,
            Func<string, Stamp?> StampOf,
            Func<string, IReadOnlyList<TranscriptWatch.WireLine>?> Parse);

        private readonly Seams _seams;
        private Stamp? _stamp;
        private DateTimeOffset? _changedAt;
        private int _held;
        private TranscriptWatch.WireLine? _first;
        private TranscriptWatch.WireLine? _last;
        private bool _backfilled;

        internal TranscriptWatcher(string phoneId, string sessionId, Seams seams)
        {
            PhoneId = phoneId;
            SessionId = sessionId;
            _seams = seams;
        }

        internal string PhoneId { get; }
        internal string SessionId { get; }

        // The parsed-line count the last window was built from.
        internal int Total => _held;

        // The subscription's first answer: the total for `ok watching`, then
        // the backfill (none when there is no transcript yet, or it is empty —
        // the windows start when the hook names a file or its first row lands).
        internal (int Total, IReadOnlyList<string> Windows) Start()
        {
            var lines = Current();
            _stamp = Stamped();
            if (lines is null) return (0, []);
            return (lines.Count, Hold(lines, TranscriptWatch.Backfill(SessionId, lines)));
        }

        // A page of older lines for `watch {before}`: no change to what is held.
        // An empty transcript has no page: no window is sent with no lines.
        internal IReadOnlyList<string> Page(long before) =>
            Current() is { Count: > 0 } lines ? TranscriptWatch.Page(SessionId, lines, before) : [];

        // One tick. A changed stamp starts (or restarts) the quiet period; once
        // it has lasted 250 ms the file is re-parsed and diffed.
        internal IReadOnlyList<string> Poll(DateTimeOffset now)
        {
            var stamp = Stamped();
            if (stamp != _stamp)
            {
                _stamp = stamp;
                _changedAt = now;
                return [];
            }
            if (_changedAt is not { } at || now - at < Debounce) return [];
            _changedAt = null;

            if (Current() is not { } lines) return [];
            if (!_backfilled) return Hold(lines, TranscriptWatch.Backfill(SessionId, lines));
            return TranscriptWatch.Diff(_held, _first, lines, _last) switch
            {
                TranscriptWatch.Change.Tail => Hold(lines, TranscriptWatch.Tail(SessionId, lines, _held)),
                TranscriptWatch.Change.Restate => Hold(lines, TranscriptWatch.Tail(SessionId, lines, _held - 1)),
                TranscriptWatch.Change.Reset => Hold(lines, TranscriptWatch.Backfill(SessionId, lines, reset: true)),
                _ => [],
            };
        }

        // ---- the real file ----------------------------------------------------

        // The file's length and last write, or null when it is not there.
        internal static Stamp? FileStamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? new Stamp(path, info.Length, info.LastWriteTimeUtc) : null;
        }

        // The whole file through the session's own parser — a full re-parse per
        // change, which the contract chooses for correctness first (numbering
        // from the start of the file needs every row; TranscriptReader's
        // 256 KB tail cannot number). Shared read, because the CLI is writing.
        internal static IReadOnlyList<TranscriptWatch.WireLine>? ParseFile(string path, SessionSource source)
        {
            if (!File.Exists(path)) return null;
            var lines = new List<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                while (reader.ReadLine() is { } line) lines.Add(line);
            var rows = source switch
            {
                SessionSource.Codex => CodexTranscript.Map(lines),
                SessionSource.Grok => GrokTranscript.Map(lines),
                _ => ChatTranscript.Map(lines),
            };
            return rows.Select(r => TranscriptWatch.From(r.Turn)).ToList();
        }

        internal static Seams For(SessionStatus status) =>
            new(() => string.IsNullOrEmpty(status.TranscriptPath) ? null : status.TranscriptPath,
                FileStamp,
                path => ParseFile(path, status.Source));

        private IReadOnlyList<string> Hold(IReadOnlyList<TranscriptWatch.WireLine> lines, IReadOnlyList<string> windows)
        {
            if (lines.Count == 0) return [];
            _held = lines.Count;
            _first = lines[0];
            _last = lines[^1];
            _backfilled = true;
            return windows;
        }

        private Stamp? Stamped() => _seams.PathOf() is { } path ? _seams.StampOf(path) : null;

        private IReadOnlyList<TranscriptWatch.WireLine>? Current() =>
            _seams.PathOf() is { } path ? _seams.Parse(path) : null;
    }
}
