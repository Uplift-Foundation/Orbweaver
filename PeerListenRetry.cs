namespace Orbweaver
{
    // Keeps trying to open the peer link's listening socket until it opens.
    //
    // **CB-306 (reopened): an install left the new Orbweaver with no peer link at
    // all.** The old instance still held the port while the new one started, the
    // bind threw, PeerSessions.Start logged it once and carried on — and nothing
    // ever tried again, so the instance ran for as long as it lived with nothing
    // listening on 7677. The log line that said so was also invisible, because
    // the mirror log had stopped at its 1 MB ceiling days earlier.
    //
    // Pure policy plus a loop that takes its clock and its socket as arguments,
    // so the rule is a test and the held-port case is an integration test rather
    // than a thing seen once on a MacBook.
    internal static class PeerListenRetry
    {
        internal static readonly TimeSpan First = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan Longest = TimeSpan.FromSeconds(30);

        // Doubling from one second to a ceiling. An installer's old instance
        // takes a few seconds to exit, which the early attempts catch quickly; a
        // port held by something unrelated settles at one attempt per thirty
        // seconds, which is cheap and still self-heals when it is released.
        internal static TimeSpan Delay(int failures)
        {
            if (failures < 1) failures = 1;

            // 2^5 seconds already exceeds the ceiling, and capping the shift
            // keeps a very long outage from overflowing it.
            var seconds = First.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6));
            return TimeSpan.FromSeconds(Math.Min(seconds, Longest.TotalSeconds));
        }

        // Runs `listen` until it returns without throwing, or `ct` is cancelled.
        // Returns how many attempts failed first. `onFailure` is told the attempt
        // number, the delay before the next one, and the exception; it must not
        // throw (a logger).
        internal static async Task<int> RunAsync(
            Action listen,
            Action<int, TimeSpan, Exception> onFailure,
            Func<TimeSpan, CancellationToken, Task> wait,
            CancellationToken ct)
        {
            var failures = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    listen();
                    return failures;
                }
                catch (Exception ex)
                {
                    failures++;
                    var next = Delay(failures);
                    onFailure(failures, next, ex);

                    try { await wait(next, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            return failures;
        }
    }
}
