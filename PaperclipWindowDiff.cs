using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // frames-v18's tail rule for a Paperclip row, pure. frames-v4 compares role and text and restates at most the
    // last row, which is wrong for a thread: a tool line turning red, or the task line moving from in progress to
    // done, is a change to a row that is not the last. Here each row's role, text and error are compared (never
    // `at`, which is not stable across two builds of one thread) and applied in order:
    //  1. the thread was replaced (the agent's task changed), shrank, or was at the row cap and its first
    //     difference is row 1 (the cap dropped the oldest row after the task row, so every later row moved up):
    //     a reset backfill;
    //  2. otherwise the rows from the first difference (or from the old count when only rows were appended)
    //     through the end, as a live tail the phone replaces by index;
    //  3. nothing differs and nothing was appended: nothing.
    internal static class PaperclipWindowDiff
    {
        internal enum Change { None, Tail, Reset }

        internal static (Change Kind, int From) Diff(
            IReadOnlyList<TranscriptWatch.WireLine> previous, string? previousTask,
            IReadOnlyList<TranscriptWatch.WireLine> now, string? nowTask)
        {
            // A move from no task to the one a message just opened is the thread growing, not a different
            // thread: the sent line must survive it. Task to another task, or to none, is a replacement.
            var replaced = previousTask != null && nowTask != previousTask;
            if (replaced || now.Count < previous.Count) return (Change.Reset, 0);

            var first = -1;
            for (var i = 0; i < previous.Count; i++)
                if (!Same(previous[i], now[i])) { first = i; break; }

            if (first == 1 && previous.Count >= PaperclipChat.RowCap) return (Change.Reset, 0);
            if (first >= 0) return (Change.Tail, first);
            return now.Count > previous.Count ? (Change.Tail, previous.Count) : (Change.None, 0);
        }

        internal static bool Same(TranscriptWatch.WireLine a, TranscriptWatch.WireLine b) =>
            a.Role == b.Role && a.Text == b.Text && a.Error == b.Error;

        // The window run for a change: a backfill (reset) or the live tail from the first changed row.
        internal static IReadOnlyList<string> Windows(string sessionId, IReadOnlyList<TranscriptWatch.WireLine> now,
            (Change Kind, int From) change) => change.Kind switch
        {
            Change.Reset => TranscriptWatch.Backfill(sessionId, now, reset: true),
            Change.Tail => TranscriptWatch.Tail(sessionId, now, change.From),
            _ => Array.Empty<string>(),
        };

        // A row as the wire draws it: the task row (it has no time of its own) carries at 0.
        internal static TranscriptWatch.WireLine Line(PaperclipChat.Row row) =>
            TranscriptWatch.Line(TranscriptWatch.RoleOf(row.Role), row.Text,
                row.At == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(row.At, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                row.Error);
    }
}
