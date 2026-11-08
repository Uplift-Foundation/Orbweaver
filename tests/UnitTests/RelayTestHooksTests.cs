using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-314: the installed app's scripted pairing for an end-to-end harness —
/// off without the explicit opt-in, and when on: QR text to a file, the first
/// request Allowed with its check code logged, an unanswered offer replaced.
/// </summary>
public class RelayTestHooksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-hooks-" + Guid.NewGuid());
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Fake : IPairingClient
    {
        public int Offers;
        public bool AllowResult = true;
        public Func<int, Task<RelayLink.PairOffer>>? Begin;
        public readonly List<string> Allowed = new();
        public Task<RelayLink.PairOffer> BeginPairingAsync(CancellationToken token = default)
        {
            Offers++;
            return Begin?.Invoke(Offers) ?? Task.FromResult(new RelayLink.PairOffer($"off_{Offers}", T0.AddMinutes(5), new byte[32], $"orbweaver://pair?o=off_{Offers}"));
        }
        public bool Allow(RelayLink.PairRequest request) { Allowed.Add(request.PhoneId); return AllowResult; }
        public void Deny(RelayLink.PairRequest request) { }
        public void CancelPairing() { }
        public event Action<RelayLink.PairRequest> PairRequested = delegate { };
        public void Raise(string phone) => PairRequested(new RelayLink.PairRequest($"off_{Offers}", phone, "QA iPhone", [], [], [], 4213));
    }

    private string Events() => File.ReadAllText(Path.Combine(_dir, RelayTestHooks.EventsFile));

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void Only_an_explicit_1_turns_them_on(string? value, bool expected) =>
        Assert.Equal(expected, RelayTestHooks.Enabled(name => name == BrandEnv.Name(BrandEnv.RelayTestHooks) ? value : null));

    [Fact]
    public async Task The_qr_is_written_the_first_request_is_allowed_and_the_code_logged()
    {
        var fake = new Fake();
        var gate = new TaskCompletionSource();
        var run = RelayTestHooks.RunAsync(fake, _dir, () => T0, (_, ct) => gate.Task.WaitAsync(ct), default);
        Assert.Equal("orbweaver://pair?o=off_1", File.ReadAllText(Path.Combine(_dir, RelayTestHooks.QrFile)));

        fake.Raise("ph_1");
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["ph_1"], fake.Allowed);
        var events = Events();
        Assert.Contains("test hooks on", events);
        Assert.Contains("offer off_1 expires 2026-11-08T12:05:00Z", events);
        Assert.Contains("request ph_1 \"QA iPhone\" check code 004 213", events);
        Assert.Contains("allowed ph_1", events);
        Assert.Contains("paired; test hooks done", events);
        Assert.StartsWith("2026-11-08T12:00:00.000Z ", events);
    }

    [Fact]
    public async Task An_allow_that_could_not_be_sent_keeps_waiting_and_an_expired_offer_is_replaced()
    {
        var fake = new Fake { AllowResult = false };
        var expiries = new List<TaskCompletionSource>();
        var run = RelayTestHooks.RunAsync(fake, _dir, () => T0.AddMinutes(10),            // every offer already expired
            (t, ct) => { var tcs = new TaskCompletionSource(); lock (expiries) expiries.Add(tcs); return tcs.Task.WaitAsync(ct); }, default);
        fake.Raise("ph_1");
        Assert.Contains("allow not sent for ph_1", Events());
        Assert.False(run.IsCompleted);

        lock (expiries) expiries[0].SetResult();                                          // the offer runs out
        await Task.Run(async () => { while (fake.Offers < 2) await Task.Delay(10); }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("orbweaver://pair?o=off_2", File.ReadAllText(Path.Combine(_dir, RelayTestHooks.QrFile)));

        fake.AllowResult = true;
        fake.Raise("ph_1");
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("paired; test hooks done", Events());
    }

    [Fact]
    public async Task A_failed_offer_is_logged_and_retried()
    {
        var fake = new Fake();
        fake.Begin = n => n == 1
            ? Task.FromException<RelayLink.PairOffer>(new RelayHttpException("the relay answered 503"))
            : Task.FromResult(new RelayLink.PairOffer("off_2", T0.AddMinutes(5), new byte[32], "qr-2"));
        var gate = new TaskCompletionSource();
        var waits = new List<TimeSpan>();
        var run = RelayTestHooks.RunAsync(fake, _dir, () => T0, (t, ct) => { waits.Add(t); return waits.Count == 1 ? Task.CompletedTask : gate.Task.WaitAsync(ct); }, default);
        await Task.Run(async () => { while (!File.Exists(Path.Combine(_dir, RelayTestHooks.QrFile))) await Task.Delay(10); }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("offer failed: the relay answered 503", Events());
        Assert.Equal(TimeSpan.FromSeconds(5), waits[0]);
        fake.Raise("ph_9");
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_stops_it_and_says_so()
    {
        using var cts = new CancellationTokenSource();
        var fake = new Fake();
        var run = RelayTestHooks.RunAsync(fake, _dir, () => T0, (t, ct) => Task.Delay(Timeout.Infinite, ct), cts.Token);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("test hooks stopped", Events());
        fake.Raise("ph_late");                                                            // unsubscribed: ignored
        Assert.Empty(fake.Allowed);
    }
}
