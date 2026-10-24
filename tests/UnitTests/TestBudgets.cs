using System;
using System.Threading;
using System.Threading.Tasks;
using Orbweaver;

namespace Orbweaver.Tests;

// The credential-read budgets a test should reach for (CB-269).
//
// A test that handed ReadWithinAsync `TimeSpan.FromSeconds(5)` and an instant fake
// was racing a real clock against a real thread pool, so a pool another class
// had pinned turned a correct answer into NoAnswer after exactly five seconds.
// These take the clock out of the race rather than widening it: the outcome is
// decided by what the source did, not by how fast the scheduler ran.
internal static class TestBudgets
{
    // The clock never fires, and is cancelled when the read wins (which is what
    // the production code does with its own timer, so nothing is orphaned). For
    // every source that answers.
    internal static CredentialBudget AlwaysAnswers { get; } =
        new(TimeSpan.FromSeconds(5), (_, ct) => Task.Delay(Timeout.Infinite, ct));

    // The clock has already run out. Only for sources that never answer, so the
    // read is abandoned at once rather than after a guessed number of
    // milliseconds that a loaded runner can overshoot either way.
    internal static CredentialBudget ExpiresAtOnce { get; } =
        new(TimeSpan.FromMilliseconds(50), (_, _) => Task.CompletedTask);

    // Never fires, like AlwaysAnswers, and counts how often it was asked to
    // time a read. Compared with how many reads the source saw, it says whether
    // every read went through *this* budget and not a default one: a call site
    // that reached for CredentialBudget.Unmeasured instead of its instance's
    // budget would leave Calls short of Reads.
    internal static (CredentialBudget Budget, Func<int> Calls) Spy()
    {
        var calls = 0;
        return (new CredentialBudget(TimeSpan.FromSeconds(5), (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.Delay(Timeout.Infinite, ct);
        }), () => Volatile.Read(ref calls));
    }

    // A clock the test runs by hand: completing `Expire` is the moment the
    // budget runs out.
    internal static (CredentialBudget Budget, TaskCompletionSource Expire) Manual()
    {
        var expire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (new CredentialBudget(TimeSpan.FromSeconds(5),
            async (_, ct) => await expire.Task.WaitAsync(ct).ConfigureAwait(false)), expire);
    }
}
