using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Orbweaver.Tests;

// QA probe (Ines): AccountOrbs re-offers every account's icon whenever a bundle is built. With more icons than the store holds, does it settle?
// Store-level model of that loop: Built => offer all N paths again, workers run until none are pending. `fixedStore` picks the shipped
// ForAccounts store; false builds the pre-fix configuration (capacity 8, rebuild on evict) as the negative control.
public class QaIconLoopProbeTests(ITestOutputHelper output)
{
    private static (int builds, int rounds, bool settled, int count, int hashFlips) Drive(int n, bool fixedStore, int maxRounds = 300)
    {
        var files = new Dictionary<string, byte[]>(); var workers = new List<Action>(); var builds = 0;
        Func<string, (long, DateTime)?> stat = p => files.TryGetValue(p, out var f) ? (f.Length, DateTime.UnixEpoch) : null;
        Func<string, byte[]?> read = p => files.GetValueOrDefault(p);
        Func<byte[], PersonaImageBundle?> build = b => { builds++; return new PersonaImageBundle(PersonaImageBundle.HashOf(b), 24, 24, [new PersonaFrames.Frame(0, new byte[10])]); };
        var store = fixedStore
            ? PersonaImageStore.ForAccounts(stat!, read, build, workers.Add, _ => { })
            : new PersonaImageStore(stat!, read, build, workers.Add, _ => { });
        var paths = Enumerable.Range(0, n).Select(i => $"/icons/{i}.png").ToList();
        foreach (var p in paths) files[p] = Encoding.UTF8.GetBytes("icon-" + p);
        var lastHash = new Dictionary<string, string?>(); var flips = 0;
        void OfferAll() { foreach (var p in paths) { var h = store.Offer(p); if (lastHash.TryGetValue(p, out var old) && old != h) flips++; lastHash[p] = h; } }   // a flip is a usage frame
        store.Built += OfferAll;
        OfferAll();
        var rounds = 0;
        while (workers.Count > 0 && rounds < maxRounds) { var w = workers.ToList(); workers.Clear(); w.ForEach(a => a()); rounds++; }
        return (builds, rounds, workers.Count == 0, store.Count, flips);
    }

    [Theory] [InlineData(1)] [InlineData(8)] [InlineData(9)] [InlineData(16)] [InlineData(64)] [InlineData(65)]
    public void ShippedAccountStoreSettlesToAtMostOneBuildPerIcon(int n)
    {
        var (builds, rounds, settled, count, flips) = Drive(n, fixedStore: true);
        output.WriteLine($"FIXED n={n} builds={builds} rounds={rounds} settled={settled} stored={count} hashFlips={flips}");
        Assert.True(settled, $"n={n}: still building after {rounds} rounds ({builds} builds)");
        Assert.Equal(n, builds);
    }

    [Theory] [InlineData(9)] [InlineData(16)]
    public void NegativeControl_TheOldConfigurationDoesNotSettle(int n)
    {
        var (builds, rounds, settled, _, _) = Drive(n, fixedStore: false);
        output.WriteLine($"OLD n={n} builds={builds} rounds={rounds} settled={settled}");
        Assert.False(settled);
    }
}
