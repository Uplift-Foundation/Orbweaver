namespace Orbweaver
{
    // Keeps the desktop under the relay's per-device frame rate (CB-314,
    // frames-v2.json: 50 frames/s). Measured against the live relay with
    // tools/relay-probe: a hundred back-to-back state flips drew
    // `err rate_limited`, and the relay dropped the frame — a lost delta, which
    // the phone resyncs from, but in one run also, by every sign, the unlink
    // that Remove depends on, which nothing resends. So the client paces
    // itself: a token bucket below the relay's limit, and a frame over budget
    // waits its turn rather than being sent to be dropped.
    //
    // Pure: the caller supplies the time and does the waiting, so the bucket
    // is a unit test, not a timing test.
    internal sealed class RelayPacer
    {
        // A full burst plus a second of refill is the most any one second can
        // carry, so burst + rate must stay under the relay's 50 — 45 here,
        // with margin because the relay's limiter shape is not ours to know.
        // (40 + 20 was the first choice; the window test caught that it
        // allows 60 in the first second.)
        internal const double DefaultRate = 35;      // frames per second
        internal const double DefaultBurst = 10;

        private readonly double _rate;
        private readonly double _burst;
        private double _tokens;
        private DateTimeOffset _last;

        internal RelayPacer(DateTimeOffset now, double rate = DefaultRate, double burst = DefaultBurst)
        {
            _rate = rate;
            _burst = burst;
            _tokens = burst;
            _last = now;
        }

        // Takes one frame's token and says how long to wait before sending
        // it: zero while the burst lasts, then one rate interval per frame.
        internal TimeSpan Reserve(DateTimeOffset now)
        {
            var elapsed = Math.Max(0, (now - _last).TotalSeconds);
            _tokens = Math.Min(_burst, _tokens + elapsed * _rate);
            _last = now;
            _tokens -= 1;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }
}
