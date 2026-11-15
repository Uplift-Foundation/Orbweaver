namespace Orbweaver
{
    // Runs a body only when the previous run has finished; a call that arrives
    // while one is running is refused rather than queued.
    //
    // For a timer that discards its task (CB-301). PeerSessions dials every
    // ten seconds with `_ = DialAsync()`, and nothing stopped a new tick
    // starting while the last one was stuck — so when the identity import
    // froze, ticks piled up behind it by the dozen and all ran at once when it
    // let go. A tick that finds the previous one still running has nothing to
    // add: the next one after it finishes will see the same world.
    internal sealed class SingleFlight
    {
        private int _running;

        internal bool Running => Volatile.Read(ref _running) == 1;

        // True if the body ran, false if it was refused. Released however the
        // body ends, a throw included, or one fault would stop every later tick.
        internal async Task<bool> RunAsync(Func<Task> body)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

            try
            {
                await body().ConfigureAwait(false);
                return true;
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }
}
