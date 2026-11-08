using System;
using System.Collections.Generic;
using System.IO;

namespace Orbweaver
{
    // When a transcript last recorded somebody saying something, read once per
    // change rather than once per ask (CB-307).
    //
    // Both sides of the parked rule need this answer: the mirror server for
    // every session it offers on every HELLO, and the host's own scan for
    // every idle Claude Code session every two seconds. The answer lives a
    // megabyte deep in the worst case (see TailBytes), and re-reading that on
    // every ask would make parking the most expensive thing either loop does.
    //
    // So the answer is cached against the file's length, the same shape as
    // SessionManager.IdentityFor's cache. A new turn always lengthens the
    // file, so a stale answer cannot outlive the turn that would correct it.
    // The bridge's housekeeping rows lengthen it too, which costs a re-read
    // every few minutes per attached session and is the price of not having to
    // trust mtime (SessionLiveness's header has why mtime is a liar here).
    //
    // Never pruned, deliberately: an entry is a path and two small values,
    // and the number of distinct transcripts one process ever sees is the
    // number of sessions started while it ran — hundreds over weeks, a few
    // tens of kilobytes. Pruning would need to know which sessions are gone,
    // which is the scan's knowledge, not this class's.
    //
    // Both arms are injected so the caching rule is testable without a disk;
    // the parameterless constructor wires them to the real file system.
    // Thread-safe, because the server answers HELLOs concurrently and the scan
    // runs its disk half off the UI thread.
    internal sealed class LastTurnCache
    {
        // How much of a transcript to read looking for the last turn.
        //
        // Larger than the window used to *render* a conversation, because this
        // is asking a different question: a session that has been sitting at a
        // prompt accumulates bookkeeping rows — `bridge-session`,
        // `queue-operation`, `mode` — after its final turn, and the answer is
        // wrong if the last turn has been pushed out of the window by them.
        // A megabyte reaches past several hundred such rows.
        internal const int TailBytes = 1024 * 1024;

        private readonly Func<string, long?> _lengthOf;
        private readonly Func<string, IEnumerable<string>> _tailLines;
        private readonly object _gate = new();
        private readonly Dictionary<string, (long Length, DateTime? LastTurn)> _cache =
            new(StringComparer.Ordinal);

        internal LastTurnCache()
            : this(RealLengthOf, path => TranscriptReader.TailLines(path, TailBytes))
        {
        }

        internal LastTurnCache(Func<string, long?> lengthOf, Func<string, IEnumerable<string>> tailLines)
        {
            _lengthOf = lengthOf;
            _tailLines = tailLines;
        }

        // Null when the file is missing or unreadable, or holds no turn — all
        // three of which SessionLiveness.IsParked reads as "not parked".
        //
        // A failed read is not cached: the next ask tries again, so a
        // transcript that appears a moment after its status file is picked up
        // as soon as it exists rather than after its length next changes.
        internal DateTime? LastTurnFor(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            long length;
            try
            {
                if (_lengthOf(path) is not { } known) return null;
                length = known;
            }
            catch
            {
                return null;
            }

            lock (_gate)
            {
                if (_cache.TryGetValue(path, out var cached) && cached.Length == length)
                    return cached.LastTurn;
            }

            DateTime? lastTurn;
            try
            {
                lastTurn = SessionLiveness.LastTurnAt(_tailLines(path));
            }
            catch
            {
                return null;
            }

            lock (_gate) _cache[path] = (length, lastTurn);
            return lastTurn;
        }

        // The real length arm: null for a file that is not there. Excluded
        // from nothing — the integration suite drives it against real files.
        internal static long? RealLengthOf(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
    }
}
