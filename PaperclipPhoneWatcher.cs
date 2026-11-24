using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // One phone's watch on one Paperclip agent (CB-447): what it has been sent of the thread, and what it is owed
    // next. The TranscriptWatcher's counterpart for a row with no file: it asks the shared PaperclipPhoneThread
    // for its rows on every relay tick (a version compare, nothing is read), and PaperclipWindowDiff says what
    // changed. Holds the thread until Release(); the relay releases it when the watch ends.
    internal sealed class PaperclipPhoneWatcher
    {
        private readonly PaperclipPhoneThread _thread;
        private IReadOnlyList<TranscriptWatch.WireLine> _held = Array.Empty<TranscriptWatch.WireLine>();
        private string? _heldTask;
        private long _version = -1;
        private bool _backfilled;
        private bool _released;

        internal PaperclipPhoneWatcher(string phoneId, string sessionId, PaperclipPhoneThread thread)
        {
            PhoneId = phoneId;
            SessionId = sessionId;
            _thread = thread;
        }

        internal string PhoneId { get; }
        internal string SessionId { get; }
        internal PaperclipPhoneThread Thread => _thread;

        // The rows held, which is `total` for `ok watching`.
        internal int Total => _held.Count;

        // The subscription's first answer: the total, then the backfill (none while the thread has no rows yet;
        // the backfill then goes out, live false, when the first read lands).
        internal (int Total, IReadOnlyList<string> Windows) Start()
        {
            var snap = _thread.Current();
            _version = snap.Version;
            var lines = snap.Rows.Select(PaperclipWindowDiff.Line).ToList();
            if (lines.Count == 0) return (0, Array.Empty<string>());
            Hold(lines, snap.TaskId);
            return (lines.Count, TranscriptWatch.Backfill(SessionId, lines));
        }

        // One tick: nothing unless the thread moved.
        internal IReadOnlyList<string> Poll()
        {
            var snap = _thread.Current();
            if (snap.Version == _version) return Array.Empty<string>();
            _version = snap.Version;
            var lines = snap.Rows.Select(PaperclipWindowDiff.Line).ToList();
            if (lines.Count == 0) return Array.Empty<string>();

            if (!_backfilled)
            {
                Hold(lines, snap.TaskId);
                return TranscriptWatch.Backfill(SessionId, lines);
            }
            var change = PaperclipWindowDiff.Diff(_held, _heldTask, lines, snap.TaskId);
            var windows = PaperclipWindowDiff.Windows(SessionId, lines, change);
            Hold(lines, snap.TaskId);
            return windows;
        }

        internal void Release()
        {
            if (_released) return;
            _released = true;
            _thread.Release();
        }

        private void Hold(IReadOnlyList<TranscriptWatch.WireLine> lines, string? task)
        {
            _held = lines;
            _heldTask = task;
            _backfilled = true;
        }
    }
}
