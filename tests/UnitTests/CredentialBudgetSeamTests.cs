using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orbweaver.UnitTests;
using Xunit;

namespace Orbweaver.Tests;

// The injectable clock on a credential read's budget (CB-269).
//
// Every test here decides the race by what the *source* did or by a clock the
// test holds, never by how quickly the thread pool ran: that is the property the
// seam exists for, and a test that depended on pool latency would be the thing
// it was written to remove.
public class CredentialBudgetSeamTests
{
    private sealed class Answering : ICloudCredentialSource
    {
        public string? Stamp() => "s";

        public CredentialRead Read() =>
            new(CredentialOutcome.Found, "token", null, "a credential is present");
    }

    private sealed class NeverAnswering : ICloudCredentialSource, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public string? Stamp() => "s";

        public CredentialRead Read()
        {
            _gate.Wait();
            return new CredentialRead(CredentialOutcome.Found, "late", null, "too late");
        }

        // Frees the parked pool thread; the gate is not disposed under it.
        public void Dispose() => _gate.Set();
    }

    // A gated source that records what it was told, and whether the gate was
    // free again afterwards.
    private sealed class Gated : IGatedCredentialSource
    {
        private readonly ICloudCredentialSource _inner;

        internal Gated(ICloudCredentialSource inner) => _inner = inner;

        public SemaphoreSlim Gate { get; } = new(1, 1);
        internal CredentialRead? Reported { get; private set; }
        public ICloudCredentialSource Choose() => _inner;
        public void Report(CredentialRead read) => Reported = read;
        public string? Stamp() => "s";
        public CredentialRead Read() => _inner.Read();
    }

    [Fact]
    public async Task AnAnsweringSourceIsFoundAndItsTimerIsCancelled()
    {
        CancellationToken seen = default;
        var budget = new CredentialBudget(TimeSpan.FromSeconds(5), (_, ct) =>
        {
            seen = ct;
            return Task.Delay(Timeout.Infinite, ct);
        });

        var read = await ClaudeCliCredentials.ReadWithinAsync(new Answering(), budget, CancellationToken.None);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Equal("token", read.AccessToken);

        // No orphaned timer: the clock handed to the budget is cancelled once the
        // read has won, as the real Task.Delay was.
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task ASourceThatNeverAnswersIsNoAnswerOnceTheClockExpires()
    {
        using var source = new NeverAnswering();

        var read = await ClaudeCliCredentials.ReadWithinAsync(
            source, TestBudgets.ExpiresAtOnce, CancellationToken.None);

        Assert.Equal(CredentialOutcome.NoAnswer, read.Outcome);
        Assert.Null(read.AccessToken);
        Assert.Contains("did not answer", read.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABudgetExpiredByHandDecidesTheRaceEitherWay()
    {
        // Read first: the clock is never expired, so the answer stands.
        var (notExpired, _) = TestBudgets.Manual();
        var found = await ClaudeCliCredentials.ReadWithinAsync(
            new Answering(), notExpired, CancellationToken.None);
        Assert.Equal(CredentialOutcome.Found, found.Outcome);

        // Clock first: the read is parked, and the test is the one that ends it.
        using var parked = new NeverAnswering();
        var (budget, expire) = TestBudgets.Manual();
        var pending = ClaudeCliCredentials.ReadWithinAsync(parked, budget, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        expire.SetResult();

        Assert.Equal(CredentialOutcome.NoAnswer, (await pending).Outcome);
    }

    [Fact]
    public async Task TheClockIsHandedTheBudgetsLength()
    {
        TimeSpan? asked = null;
        var budget = new CredentialBudget(TimeSpan.FromSeconds(7), (length, ct) =>
        {
            asked = length;
            return Task.Delay(Timeout.Infinite, ct);
        });

        await ClaudeCliCredentials.ReadWithinAsync(new Answering(), budget, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(7), asked);
    }

    // The production default: the real Task.Delay. A zero length is already up,
    // so a parked source is abandoned at once with no wall-clock wait.
    [Fact]
    public async Task TheDefaultClockIsARealDelay()
    {
        using var source = new NeverAnswering();

        var read = await ClaudeCliCredentials.ReadWithinAsync(
            source, CredentialBudget.Of(TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(CredentialOutcome.NoAnswer, read.Outcome);
    }

    [Fact]
    public void TheDefaultBudgetIsTheUnmeasuredPlaceholder()
    {
        Assert.Equal(ClaudeCliCredentials.UnmeasuredReadBudget, CredentialBudget.Unmeasured.Length);
        Assert.Equal(TimeSpan.FromSeconds(3), CredentialBudget.Of(TimeSpan.FromSeconds(3)).Length);
    }

    [Fact]
    public async Task TheTimeSpanFormDelegatesToTheDefaultClock()
    {
        using var parked = new NeverAnswering();

        var abandoned = await ClaudeCliCredentials.ReadWithinAsync(
            parked, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(CredentialOutcome.NoAnswer, abandoned.Outcome);

        // And an answer with a generous length comes straight back: the control.
        // Nothing here waits on the five minutes, because the read finishes.
        var answered = await ClaudeCliCredentials.ReadWithinAsync(
            new Answering(), TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(CredentialOutcome.Found, answered.Outcome);
    }

    [Fact]
    public async Task AGatedSourceReportsAndReleasesTheGateOnBothOutcomes()
    {
        var found = new Gated(new Answering());
        await ClaudeCliCredentials.ReadWithinAsync(found, TestBudgets.AlwaysAnswers, CancellationToken.None);
        Assert.Equal(CredentialOutcome.Found, found.Reported!.Outcome);
        Assert.Equal(1, found.Gate.CurrentCount);

        using var parked = new NeverAnswering();
        var unanswered = new Gated(parked);
        await ClaudeCliCredentials.ReadWithinAsync(unanswered, TestBudgets.ExpiresAtOnce, CancellationToken.None);
        Assert.Equal(CredentialOutcome.NoAnswer, unanswered.Reported!.Outcome);
        Assert.Equal(1, unanswered.Gate.CurrentCount);
    }

    // Cancelling is not a timeout, and it is cancellation however the clock is
    // supplied: the clock's own task is cancelled with the caller's token, which
    // is how the wait ends, and it is reported as cancellation.
    [Fact]
    public async Task CancellingWhileTheClockIsRunningThrowsRatherThanReportingNoAnswer()
    {
        using var parked = new NeverAnswering();
        using var cts = new CancellationTokenSource();
        var (budget, _) = TestBudgets.Manual();

        var pending = ClaudeCliCredentials.ReadWithinAsync(parked, budget, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // The test budgets are themselves part of what the rest of the suite stands
    // on, so the one that must never fire is pinned not to.
    [Fact]
    public async Task TheAlwaysAnswersClockNeverFiresAndEndsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var clock = TestBudgets.AlwaysAnswers.Expiry(TimeSpan.Zero, cts.Token);

        Assert.False(clock.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clock);
    }

    // --- the isolation this ticket relies on, pinned ------------------------------
    //
    // A PARTIAL guard, and it should be read as one. It pins the two memberships
    // that the races turned on, so removing either attribute fails here. It cannot
    // see a *new* class that starts writing ClaudeDesktopManager's snapshot or
    // pinning the pool: that needs somebody to know the rule, which is why the
    // reasons are written next to each class as well.

    // Read off the attribute's constructor argument: the name is not exposed as a
    // property on every xunit version this repository has used.
    private static string? CollectionOf(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static (string? Name, bool DisableParallelization) DefinitionOf(Type type)
    {
        var data = type.GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(CollectionDefinitionAttribute));
        var disable = data.NamedArguments
            .FirstOrDefault(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization));
        return (data.ConstructorArguments.FirstOrDefault().Value as string,
            disable.TypedValue.Value is true);
    }

    [Fact]
    public void ThePoolSaturationTestRunsAloneAfterTheParallelCollections()
    {
        Assert.Equal("ThreadPoolCeiling", CollectionOf(typeof(OpenClawPoolSaturationTests)));

        var (name, disabled) = DefinitionOf(typeof(ThreadPoolCeilingCollection));

        Assert.Equal("ThreadPoolCeiling", name);
        Assert.True(disabled);
    }

    [Fact]
    public void NothingElseInTheGatewayClassRewritesThePoolCeiling()
    {
        Assert.Equal("Settings", CollectionOf(typeof(OpenClawGatewayTests)));

        foreach (var method in typeof(OpenClawGatewayTests).GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                     | BindingFlags.DeclaredOnly))
        {
            Assert.DoesNotContain("Saturated", method.Name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheClassesThatWriteTheDesktopSnapshotShareOneCollection()
    {
        Assert.Equal("Settings", CollectionOf(typeof(OrphanedInstanceTests)));
        Assert.Equal("Settings", CollectionOf(typeof(ClaudeDesktopManagerTests)));
    }
}
