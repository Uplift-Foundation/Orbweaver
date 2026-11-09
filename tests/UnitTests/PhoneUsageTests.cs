using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>CB-316: the usage frame, its rounding and the limit edge, against frames-v4-vectors.json (usage section).</summary>
public class PhoneUsageTests
{
    private static readonly JsonElement U = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v4-vectors.json"))).RootElement.GetProperty("usage");

    [Fact]
    public void TheFrameMatchesTheVector()
    {
        var accounts = new List<PhoneUsage.Account>
        {
            new("warren@uplifttech.org", "claude", "max", 62, 1794117192, 31, 1794543792, 1794111792),
            new("codex (default)", "codex", "", 17, 1794120792, null, null, 1794111732),
            new("grok", "grok", "", null, null, null, null, 1794111672),
        };
        Assert.Equal(U.GetProperty("frame").GetProperty("plaintext").GetString(), PhoneUsage.Frame(accounts));
    }

    [Fact]
    public void RoundingIsHalfAwayFromZeroAndClamped()
    {
        foreach (var r in U.GetProperty("_rounding").EnumerateArray())
            Assert.Equal(r.GetProperty("pct").GetInt32(), PhoneUsage.Round(r.GetProperty("percent").GetDouble()));
    }

    [Fact]
    public void TheLimitEdgeFiresOnTheVectorsSequence()
    {
        var rule = U.GetProperty("limit_rule");
        var watch = new UsageLimitWatch();
        foreach (var r in rule.GetProperty("readings").EnumerateArray())
        {
            var a = new PhoneUsage.Account("n", "claude", "", r.GetProperty("pct").GetInt32(), r.GetProperty("resets_at").GetInt64(), null, null, 0);
            Assert.Equal(r.GetProperty("fires").GetBoolean(), watch.Crossings([a]).Count == 1);
        }
        Assert.True(rule.GetProperty("first_reading_at_82_fires").GetBoolean());
        Assert.Single(new UsageLimitWatch().Crossings([new("n", "claude", "", 82, 1, null, null, 0)]));
    }

    [Fact]
    public void AnAccountThatLosesItsWindowIsForgottenSoItRearms()
    {
        var watch = new UsageLimitWatch();
        Assert.Single(watch.Crossings([new("n", "claude", "", 90, 1, null, null, 0)]));
        Assert.Empty(watch.Crossings([new("n", "claude", "", null, null, null, null, 0)]));
        Assert.Single(watch.Crossings([new("n", "claude", "", 90, 1, null, null, 0)]));
    }

    [Fact]
    public void TheEsMatchTheVectorsPlaintext()
    {
        Assert.Equal(U.GetProperty("limit_e").GetProperty("plaintext").GetString(),
            PhoneUsage.LimitE(new("warren@uplifttech.org", "claude", "max", 81, 1794117192, 31, 1794543792, 1)));
        Assert.Equal("""{"v":1,"t":"done","session_id":"0f6c4f2a","label":"orbweaver on MacBook Pro","title":"orbweaver"}""",
            PhoneUsage.DoneE("0f6c4f2a", PhoneUsage.DoneLabel("orbweaver", "MacBook Pro"), "orbweaver"));
        Assert.Equal("MacBook Pro", PhoneUsage.DoneLabel(" ", "MacBook Pro"));
    }

    [Fact]
    public void ReadingsBecomeAccountsInStableOrderWithLiveWindowsOnly()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
        var readings = new[]
        {
            new AccountUsage(null, "b@x.org", true, "Max", new UsageWindow(61.5, now.AddHours(1)), new UsageWindow(10, now.AddDays(-1)), null, now),
            new AccountUsage(null, "grok", false, null, null, null, null, now, AccountUsageSource.Grok),
            new AccountUsage(null, "a@x.org", true, null, new UsageWindow(5, now.AddHours(1)), null, null, now.AddMinutes(-3), AccountUsageSource.Codex, now.AddMinutes(-9)),
        };
        var accounts = PhoneUsage.From(readings, now);
        Assert.Equal(["b@x.org", "a@x.org", "grok"], accounts.Select(a => a.Name));
        Assert.Equal(["claude", "codex", "grok"], accounts.Select(a => a.Cli));
        Assert.Equal(62, accounts[0].Pct5h);
        Assert.Equal("max", accounts[0].Plan);
        Assert.Null(accounts[0].WeeklyPct);                 // its window had already reset
        Assert.Null(accounts[2].Pct5h);                     // unavailable: no windows
        Assert.Equal(now.AddMinutes(-9).ToUnixTimeSeconds(), accounts[1].AsOf);   // the CLI's own observation time
        Assert.Equal(PhoneUsage.Signature(accounts), PhoneUsage.Signature(accounts.Select(a => a with { AsOf = 5 }).ToList()));
    }

    [Fact]
    public void TheCliNames()
    {
        Assert.Equal("claude", PhoneUsage.CliOf(AccountUsageSource.ClaudeCode));
        Assert.Equal("codex", PhoneUsage.CliOf(AccountUsageSource.Codex));
        Assert.Equal("grok", PhoneUsage.CliOf(AccountUsageSource.Grok));
    }
}
