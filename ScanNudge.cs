namespace Orbweaver
{
    // The decision behind "a Paperclip change should be on screen about one poll
    // plus a short debounce later, not one poll plus up to two scan periods"
    // (CB-421). SessionManager's 2 s timer is the only thing that scans when
    // nothing writes a status file, so a snapshot the poll loop publishes just
    // after a scan began waits for the next one; PaperclipSessions now says when
    // what the orbs draw has changed, and this decides what to do about it.
    //
    // Pure, so each rule is a case with no timer and no sleeping:
    //  - a request starts (or restarts) the same 150 ms debounce the status-file
    //    watcher uses, so a burst is one scan;
    //  - a request while a scan is running is not dropped: one more scan runs
    //    after it, which is what the in-flight guard alone would have swallowed;
    //  - a debounce tick that nobody nudged (the watcher's) behaves exactly as
    //    it did before this class existed.
    internal sealed class ScanNudge
    {
        private bool _requested;
        private bool _followUp;

        // True when the caller should (re)start the debounce now.
        internal bool Request(bool scanInFlight)
        {
            if (scanInFlight)
            {
                _followUp = true;
                return false;
            }
            _requested = true;
            return true;
        }

        // The debounce fired. True when a scan should start now; ScheduleScan's own
        // in-flight guard still applies to ticks nobody nudged, as before.
        internal bool OnDebounceTick(bool scanInFlight)
        {
            var nudged = _requested;
            _requested = false;
            if (!nudged) return true;
            if (scanInFlight)
            {
                _followUp = true;
                return false;
            }
            return true;
        }

        // A scan finished. True when a request arrived while it ran, so one more
        // is wanted: the caller restarts the debounce.
        internal bool OnScanFinished()
        {
            if (!_followUp) return false;
            _followUp = false;
            _requested = true;
            return true;
        }
    }
}
