using System;
using System.Collections.Generic;
using Xunit;

namespace Orbweaver.Tests;

// CB-391: the two rules AccountOrbs.Apply used to hold inline, now static so
// HeadlessUsage runs them rather than a paraphrase of them. Both halves of an
// unlock projecting the same shape is what lets the relay's tracker see the
// handover as "no change", so the rules get a case per outcome here.
[Collection("Settings")]
public class AccountOrbsMergeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly bool _claude = OrbweaverSettings.AccountUsageEnabled;
    private readonly bool _grok = OrbweaverSettings.GrokAccountUsageEnabled;
    private readonly bool _codex = OrbweaverSettings.CodexAccountUsageEnabled;

    public void Dispose()
    {
        OrbweaverSettings.AccountUsageEnabled = _claude;
        OrbweaverSettings.GrokAccountUsageEnabled = _grok;
        OrbweaverSettings.CodexAccountUsageEnabled = _codex;
    }

    private static AccountUsage Usage(string? dir, double weekly = 40,
        AccountUsageSource source = AccountUsageSource.ClaudeCode, string label = "label") =>
        new(dir, label, true, "team", new UsageWindow(10, Now.AddHours(3)),
            new UsageWindow(weekly, Now.AddDays(3)), null, Now, source);

    [Fact]
    public void A_new_account_is_a_change_and_is_keyed_by_its_config_dir()
    {
        var held = new Dictionary<string, AccountUsage>(StringComparer.Ordinal);

        Assert.True(AccountOrbs.MergeReadings(held, [Usage("/a"), Usage(null)]));
        Assert.Equal(["", "/a"], new SortedSet<string>(held.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void The_same_numbers_again_are_no_change()
    {
        var held = new Dictionary<string, AccountUsage>(StringComparer.Ordinal);
        AccountOrbs.MergeReadings(held, [Usage("/a", weekly: 40)]);

        Assert.False(AccountOrbs.MergeReadings(held, [Usage("/a", weekly: 40)]));
    }

    [Fact]
    public void A_moved_number_is_a_change_and_overwrites_the_old_reading()
    {
        var held = new Dictionary<string, AccountUsage>(StringComparer.Ordinal);
        AccountOrbs.MergeReadings(held, [Usage("/a", weekly: 40)]);

        Assert.True(AccountOrbs.MergeReadings(held, [Usage("/a", weekly: 55)]));
        Assert.Equal(55, held["/a"].Weekly!.Percent);
    }

    // A failed poll is not news: an account that answered nothing keeps the
    // reading it had.
    [Fact]
    public void An_empty_read_changes_nothing_and_keeps_what_was_held()
    {
        var held = new Dictionary<string, AccountUsage>(StringComparer.Ordinal);
        AccountOrbs.MergeReadings(held, [Usage("/a")]);

        Assert.False(AccountOrbs.MergeReadings(held, []));
        Assert.True(held.ContainsKey("/a"));
    }

    [Fact]
    public void RelayAccountsOf_drops_a_source_that_is_switched_off()
    {
        OrbweaverSettings.AccountUsageEnabled = true;
        OrbweaverSettings.GrokAccountUsageEnabled = false;
        var held = new Dictionary<string, AccountUsage>(StringComparer.Ordinal)
        {
            ["/a"] = Usage("/a", label: "claude-a"),
            ["/g"] = Usage("/g", source: AccountUsageSource.Grok, label: "grok-g"),
        };

        var accounts = AccountOrbs.RelayAccountsOf(held);

        Assert.Single(accounts);
        Assert.Equal("claude-a", accounts[0].Name);
    }

    [Fact]
    public void RelayAccountsOf_names_by_email_when_one_is_known_and_by_label_otherwise()
    {
        OrbweaverSettings.AccountUsageEnabled = true;
        var held = new Dictionary<string, AccountUsage>(StringComparer.Ordinal)
        {
            ["/a"] = Usage("/a", label: "claude-a"),
        };

        Assert.Equal("claude-a", AccountOrbs.RelayAccountsOf(held)[0].Name);
        Assert.Equal("a@example.com", AccountOrbs.RelayAccountsOf(held,
            new Dictionary<string, string?> { ["/a"] = "a@example.com" })[0].Name);
    }
}
