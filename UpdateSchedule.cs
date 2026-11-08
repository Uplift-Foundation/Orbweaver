namespace Orbweaver
{
    // When the next automatic check is due (CB-308). Pure: the clock and the last
    // check are arguments, so "what happens after a long sleep" or "the clock
    // went backwards" are cases rather than things to wait for.
    internal static class UpdateSchedule
    {
        // Once a day is plenty for a desktop app and sits far under GitHub's 60
        // unauthenticated requests an hour even with several machines behind one IP.
        internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        // Never check the instant the app starts: launch is already busy (hooks,
        // sessions, tray), and nobody is waiting on this.
        internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

        // After a failed automatic check, try again in an hour instead of waiting
        // out the whole day, but not in a tight loop.
        internal static readonly TimeSpan FailureRetry = TimeSpan.FromHours(1);

        internal static TimeSpan DelayUntilNextCheck(DateTimeOffset? lastCheck, DateTimeOffset now)
        {
            if (lastCheck is not { } last) return StartupDelay;

            var elapsed = now - last;
            // A last-check in the future means the clock moved back (or the file
            // was edited). Trusting it would silence checks for as long as the gap;
            // waiting one full interval from now is the bounded answer.
            if (elapsed < TimeSpan.Zero) return Interval;

            var remaining = Interval - elapsed;
            return remaining > StartupDelay ? remaining : StartupDelay;
        }
    }
}
