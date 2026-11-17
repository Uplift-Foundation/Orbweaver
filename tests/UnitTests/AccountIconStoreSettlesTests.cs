using System.Text;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-340 review (architect and QA, both on 8d8de0aa): AccountOrbs re-offers every account's icon each
/// time a bundle finishes building (so the phone is told the moment a hash exists), which loops for
/// ever if the store can evict one icon to rebuild another. These model that loop at the store, with a
/// synchronous worker and the same re-offer on Built, and assert the number of builds settles.
/// </summary>
public class AccountIconStoreSettlesTests
{
    private sealed class World
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<Action> Workers = [];
        public int Builds;
        public readonly PersonaImageStore Store;
        public readonly List<string> Paths = [];
        public int Rounds;

        public World(bool forAccounts)
        {
            Func<string, (long, DateTime)?> stat = p => Files.TryGetValue(p, out var f) ? (f.Length, DateTime.UnixEpoch) : null;
            Func<string, byte[]?> read = p => Files.GetValueOrDefault(p);
            Func<byte[], PersonaImageBundle?> build = b =>
            {
                Builds++;
                return new PersonaImageBundle(PersonaImageBundle.HashOf(b), 24, 24, [new PersonaFrames.Frame(0, new byte[10])]);
            };
            Store = forAccounts
                ? PersonaImageStore.ForAccounts(stat, read, build, Workers.Add, _ => { })
                : new PersonaImageStore(stat, read, build, Workers.Add, _ => { });
            // AccountOrbs.OnIconBundleBuilt: a finished bundle re-offers every account's icon.
            Store.Built += OfferAll;
        }

        public void OfferAll() { foreach (var p in Paths) Store.Offer(p); }

        public void AddIcons(int n)
        {
            for (var i = 0; i < n; i++)
            {
                var path = $"/icons/{i}.png";
                Files[path] = Encoding.UTF8.GetBytes("icon" + i);
                Paths.Add(path);
            }
        }

        // Runs workers until none are left, or the budget runs out — and says which.
        public bool RunUntilSettled(int budget = 2000)
        {
            while (Workers.Count > 0 && Rounds < budget)
            {
                var w = Workers.ToList();
                Workers.Clear();
                w.ForEach(a => a());
                Rounds++;
            }
            return Workers.Count == 0;
        }

        public int Offered() => Paths.Count(p => Store.Offer(p) is not null);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(PersonaImageStore.AccountCapacity)]
    public void Up_to_the_capacity_every_icon_is_built_exactly_once_and_offered(int n)
    {
        var w = new World(forAccounts: true);
        w.AddIcons(n);
        w.OfferAll();

        Assert.True(w.RunUntilSettled());
        Assert.Equal(n, w.Builds);                    // not one rebuild
        Assert.Equal(n, w.Offered());
        Assert.Empty(w.Workers);                      // and nothing left to build, so no further frame
        Assert.Equal(n, w.Store.Count);
    }

    [Fact]
    public void Past_the_capacity_it_still_settles_and_the_icons_that_do_not_fit_are_simply_not_offered()
    {
        var w = new World(forAccounts: true);
        var n = PersonaImageStore.AccountCapacity + 1;
        w.AddIcons(n);
        w.OfferAll();

        Assert.True(w.RunUntilSettled());
        Assert.Equal(n, w.Builds);                    // each built once; the evicted one is NOT rebuilt
        Assert.Equal(PersonaImageStore.AccountCapacity, w.Store.Count);
        Assert.Equal(PersonaImageStore.AccountCapacity, w.Offered());
        Assert.Empty(w.Workers);

        // Offering again, as the next poll does, changes nothing.
        w.OfferAll();
        Assert.Empty(w.Workers);
        Assert.Equal(n, w.Builds);
    }

    [Fact]
    public void An_evicted_icon_is_rebuilt_once_its_file_changes()
    {
        var w = new World(forAccounts: true);
        w.AddIcons(PersonaImageStore.AccountCapacity + 1);
        w.OfferAll();
        Assert.True(w.RunUntilSettled());
        var evicted = w.Paths.First(p => w.Store.Offer(p) is null);

        w.Files[evicted] = Encoding.UTF8.GetBytes("a different picture entirely");   // length differs: a changed file
        w.Store.Offer(evicted);
        Assert.Single(w.Workers);
    }

    // The control that makes the above mean something: the persona store's own rule (rebuild an evicted
    // bundle on the next offer) driven the same way does not settle — which is what both reviews saw.
    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public void The_persona_stores_rule_would_loop_here_which_is_why_accounts_do_not_use_it(int n)
    {
        var w = new World(forAccounts: false);
        w.AddIcons(n);
        w.OfferAll();

        Assert.False(w.RunUntilSettled(budget: 300));
        Assert.True(w.Builds > n * 5);
    }
}
