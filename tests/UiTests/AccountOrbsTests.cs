using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// AccountOrbs driven by an in-memory source instead of the CLI.
//
// This is what IUsageSource exists for, and the same argument RemoteChat.cs
// makes for IRemoteChatSession: a surface fed by another process is untestable
// until the arrival is a seam. Nothing here starts a subprocess, so nothing
// here depends on which accounts the machine running the suite happens to have
// logged in.
//
// **What is deliberately not covered here is the hover timing.** The bridge is
// two DispatcherTimers and a confirmation that reads IsPointerOver on two
// separate top-level windows; headless can synthesize a click but cannot park a
// pointer over one window for 450ms and then move it to another. The decisions
// the timers guard — which card is open, what a pin does to it — are driven
// directly below; the delays themselves are named as uncovered in the PR.
[Collection("Settings")]
public class AccountOrbsTests : IDisposable
{
    // CB-287: AccountOrbs listens to the process-wide
    // AccountOrbCustomization.Changed while it holds orbs. A case that never
    // closed its orbs left an instance subscribed, and a later test's write
    // reached it from the wrong thread. Every instance made here is closed.
    private readonly List<AccountOrbs> _made = new();

    private AccountOrbs Make(IUsageSource source)
    {
        var orbs = new AccountOrbs(source);
        _made.Add(orbs);
        return orbs;
    }

    public void Dispose()
    {
        foreach (var orbs in _made) orbs.CloseAll();
    }

    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeUsageSource : IUsageSource
    {
        internal List<AccountUsage> Readings { get; } = new();

        internal int Reads { get; private set; }

        public IReadOnlyList<AccountUsage> Read()
        {
            Reads++;
            return Readings;
        }
    }

    private static AccountUsage Usage(
        string? configDir,
        string label,
        double weekly = 40,
        AccountUsageSource source = AccountUsageSource.ClaudeCode) =>
        new(
            ConfigDir: configDir,
            Label: label,
            Available: true,
            SubscriptionType: "team",
            Session: new UsageWindow(10, Now.AddHours(3)),
            Weekly: new UsageWindow(weekly, Now.AddDays(3)),
            Extra: null,
            ReadAt: Now,
            Source: source);

    // Namespaced the way the gateway and room ids are, so an account's saved
    // position can never collide with a session's — those are keyed by cwd, and
    // a config directory is a path too.
    [Fact]
    public void PositionKeysAreNamespacedAwayFromSessions()
    {
        Assert.Equal("account:~", AccountOrbs.PositionKey(null));
        Assert.Equal(
            "account:/Users/x/.claude-work",
            AccountOrbs.PositionKey("/Users/x/.claude-work"));
    }

    [AvaloniaFact]
    public void OneOrbPerAccountThatAnswered()
    {
        var source = new FakeUsageSource();
        var orbs = Make(source);

        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.claude-board", "board")
        }, Now);

        Assert.Equal(2, orbs.Orbs.Count);
        Assert.True(orbs.Orbs.ContainsKey(string.Empty));
        Assert.True(orbs.Orbs.ContainsKey("/Users/x/.claude-board"));

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void AReadingUpdatesTheOrbItAlreadyHasRatherThanMakingASecond()
    {
        var orbs = Make(new FakeUsageSource());

        orbs.Apply(new[] { Usage(null, "wthompson", weekly: 10) }, Now);
        var first = orbs.Orbs[string.Empty];

        orbs.Apply(new[] { Usage(null, "wthompson", weekly: 95) }, Now);

        Assert.Single(orbs.Orbs);
        Assert.Same(first, orbs.Orbs[string.Empty]);
        Assert.Equal(AccountOrbWindow.DangerHex, first.WeeklyColour);

        orbs.CloseAll();
    }

    // A poll that failed is not news about usage. Removing the orb would make a
    // network blink look like an account being deleted, and an account orb that
    // comes and goes is one nobody can learn the position of.
    [AvaloniaFact]
    public void AnAccountThatAnswersNothingKeepsTheOrbItHad()
    {
        var orbs = Make(new FakeUsageSource());

        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);
        Assert.Single(orbs.Orbs);

        orbs.Apply(Array.Empty<AccountUsage>(), Now);

        Assert.Single(orbs.Orbs);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void PinningKeepsTheCardAndMarksTheOrb()
    {
        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);

        orbs.TogglePin(string.Empty);

        Assert.True(orbs.Orbs[string.Empty].IsPinned);

        orbs.TogglePin(string.Empty);

        Assert.False(orbs.Orbs[string.Empty].IsPinned);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void PinningSurvivesTheNextPoll()
    {
        // The poll redraws every orb from its reading. A pin that did not
        // survive that would come undone every five minutes on its own, which
        // is the one failure a pin cannot have.
        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);

        orbs.TogglePin(string.Empty);
        orbs.Apply(new[] { Usage(null, "wthompson", weekly: 88) }, Now.AddMinutes(5));

        Assert.True(orbs.Orbs[string.Empty].IsPinned);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void HidingTakesTheCardsWithTheOrbs()
    {
        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[] { Usage(null, "wthompson") }, Now);
        orbs.TogglePin(string.Empty);

        orbs.SetVisible(false);

        Assert.Empty(orbs.Cards);

        orbs.CloseAll();
    }

    [AvaloniaFact]
    public void ClosingEverythingLeavesNothingBehind()
    {
        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.claude-board", "board")
        }, Now);

        orbs.CloseAll();

        Assert.Empty(orbs.Orbs);
        Assert.Empty(orbs.Cards);
    }

    // Turning Grok usage on used to close every account orb, because
    // SyncToSettings' predecessor looked only at the Claude Code flag.
    [AvaloniaFact]
    public void TurningGrokUsageOnDoesNotCloseTheOrbs()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.GrokAccountUsageEnabled = true;

        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage("/Users/x/.grok", "user", source: AccountUsageSource.Grok)
        }, Now);

        orbs.SyncToSettings(visible: true);

        Assert.Single(orbs.Orbs);
        Assert.True(orbs.Orbs.ContainsKey("/Users/x/.grok"));

        orbs.CloseAll();
        OrbweaverSettings.ReloadForTests();
    }

    [AvaloniaFact]
    public void TurningClaudeUsageOffLeavesTheGrokOrb()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.GrokAccountUsageEnabled = true;

        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.grok", "user", source: AccountUsageSource.Grok)
        }, Now);

        orbs.SyncToSettings(visible: true);

        Assert.Single(orbs.Orbs);
        Assert.True(orbs.Orbs.ContainsKey("/Users/x/.grok"));
        Assert.False(orbs.Orbs.ContainsKey(string.Empty));

        orbs.CloseAll();
        OrbweaverSettings.ReloadForTests();
    }

    [AvaloniaFact]
    public void TurningGrokUsageOffLeavesTheClaudeOrb()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = true;
        OrbweaverSettings.GrokAccountUsageEnabled = false;

        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.grok", "user", source: AccountUsageSource.Grok)
        }, Now);

        orbs.SyncToSettings(visible: true);

        Assert.Single(orbs.Orbs);
        Assert.True(orbs.Orbs.ContainsKey(string.Empty));
        Assert.False(orbs.Orbs.ContainsKey("/Users/x/.grok"));

        orbs.CloseAll();
        OrbweaverSettings.ReloadForTests();
    }

    [AvaloniaFact]
    public void TurningBothOffClosesEverything()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.GrokAccountUsageEnabled = false;
        OrbweaverSettings.CodexAccountUsageEnabled = false;

        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.grok", "user", source: AccountUsageSource.Grok)
        }, Now);

        orbs.SyncToSettings(visible: true);

        Assert.Empty(orbs.Orbs);

        orbs.CloseAll();
        OrbweaverSettings.ReloadForTests();
    }

    [AvaloniaFact]
    public void TurningCodexUsageOnDoesNotCloseTheOrbs()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.GrokAccountUsageEnabled = false;
        OrbweaverSettings.CodexAccountUsageEnabled = true;

        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage("/Users/x/.codex", "codex", source: AccountUsageSource.Codex)
        }, Now);

        orbs.SyncToSettings(visible: true);

        Assert.Single(orbs.Orbs);
        Assert.True(orbs.Orbs.ContainsKey("/Users/x/.codex"));

        orbs.CloseAll();
        OrbweaverSettings.ReloadForTests();
    }

    [AvaloniaFact]
    public void TurningClaudeUsageOffLeavesTheCodexOrb()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = false;
        OrbweaverSettings.CodexAccountUsageEnabled = true;

        var orbs = Make(new FakeUsageSource());
        orbs.Apply(new[]
        {
            Usage(null, "wthompson"),
            Usage("/Users/x/.codex", "codex", source: AccountUsageSource.Codex)
        }, Now);

        orbs.SyncToSettings(visible: true);

        Assert.Single(orbs.Orbs);
        Assert.True(orbs.Orbs.ContainsKey("/Users/x/.codex"));
        Assert.False(orbs.Orbs.ContainsKey(string.Empty));

        orbs.CloseAll();
        OrbweaverSettings.ReloadForTests();
    }

    // The floor is the whole reason the poll is affordable: Claude Code caches
    // the underlying fetch for five minutes, so asking sooner spends a process
    // per account to be told the same thing.
    [AvaloniaFact]
    public void TheSettingBeingOffMeansNothingIsAsked()
    {
        var source = new FakeUsageSource();
        var orbs = Make(source);

        OrbweaverSettings.AccountUsageEnabled = false;
        orbs.Tick(Now);

        Assert.Equal(0, source.Reads);
    }

    // --- the poll cadence (CB-122) -----------------------------------------
    // AccountOrbs' half of the adaptive interval: the rule itself is
    // UsagePollCadence's and is covered per-outcome in tests/UnitTests, so what
    // these drive is the wiring — that Apply compares against the reading it is
    // about to overwrite, and that the answer reaches _interval.
    //
    // Driven through Apply rather than Tick on purpose. Tick would have to be
    // waited out in real seconds, and the thing worth asserting is the decision
    // rather than the sleeping.

    [AvaloniaFact]
    public void APollThatMovesNothingBacksTheCadenceOff()
    {
        var orbs = Make(new FakeUsageSource());
        var reading = Usage(null, "board", weekly: 40);

        // First reading of an account is always a change — there is nothing to
        // compare it to — so this lands on Fast rather than doubling.
        orbs.Apply(new[] { reading }, Now);
        Assert.Equal(UsagePollCadence.Fast, orbs.PollInterval);

        // The same numbers again, five minutes later. ReadAt has moved and the
        // record is therefore unequal; the rings have not, so this must back off.
        orbs.Apply(new[] { reading with { ReadAt = Now.AddMinutes(5) } }, Now.AddMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(120), orbs.PollInterval);

        orbs.Apply(new[] { reading with { ReadAt = Now.AddMinutes(10) } }, Now.AddMinutes(10));
        Assert.Equal(TimeSpan.FromSeconds(240), orbs.PollInterval);

        orbs.Apply(new[] { reading with { ReadAt = Now.AddMinutes(15) } }, Now.AddMinutes(15));
        Assert.Equal(UsagePollCadence.Slow, orbs.PollInterval);

        // ...and stays there rather than climbing past it.
        orbs.Apply(new[] { reading with { ReadAt = Now.AddMinutes(20) } }, Now.AddMinutes(20));
        Assert.Equal(UsagePollCadence.Slow, orbs.PollInterval);
    }

    [AvaloniaFact]
    public void APollThatMovesARingReturnsToTheFastCadence()
    {
        var orbs = Make(new FakeUsageSource());
        var reading = Usage(null, "board", weekly: 40);

        orbs.Apply(new[] { reading }, Now);
        for (var quiet = 0; quiet < 4; quiet++)
            orbs.Apply(new[] { reading with { ReadAt = Now.AddMinutes(quiet + 1) } }, Now);

        Assert.Equal(UsagePollCadence.Slow, orbs.PollInterval);

        // The burst: one point of weekly, which is 3.6 degrees of arc and the
        // smallest change the API can report.
        orbs.Apply(new[] { Usage(null, "board", weekly: 41) }, Now);

        Assert.Equal(UsagePollCadence.Fast, orbs.PollInterval);
    }

    // Any one account moving keeps the whole poll fast. The sources are read
    // together in a single CompositeUsageSource.Read(), so there is no such
    // thing as polling one of them harder than the others — and an account
    // sitting still must not be able to hold back one that is climbing.
    [AvaloniaFact]
    public void OneMovingAccountKeepsTheCadenceFastForAllOfThem()
    {
        var orbs = Make(new FakeUsageSource());
        var still = Usage(null, "board", weekly: 40);
        var moving = Usage("/Users/x/.claude-work", "work", weekly: 20);

        orbs.Apply(new[] { still, moving }, Now);
        orbs.Apply(new[] { still, moving }, Now);
        orbs.Apply(new[] { still, moving }, Now);
        orbs.Apply(new[] { still, moving }, Now);
        Assert.Equal(UsagePollCadence.Slow, orbs.PollInterval);

        orbs.Apply(new[] { still, Usage("/Users/x/.claude-work", "work", weekly: 21) }, Now);

        Assert.Equal(UsagePollCadence.Fast, orbs.PollInterval);
    }

    // An orb whose account answered nothing keeps the reading it had (the
    // existing keep-stale rule), and a poll where *nobody* answered is not news
    // about usage — so it must back off rather than hold the fast cadence on
    // the strength of an empty answer.
    [AvaloniaFact]
    public void APollThatAnsweredNothingBacksOff()
    {
        var orbs = Make(new FakeUsageSource());

        orbs.Apply(new[] { Usage(null, "board") }, Now);
        Assert.Equal(UsagePollCadence.Fast, orbs.PollInterval);

        orbs.Apply(Array.Empty<AccountUsage>(), Now);

        Assert.Equal(TimeSpan.FromSeconds(120), orbs.PollInterval);
    }

    // A freshly constructed AccountOrbs has not polled at all, and must not
    // assume the machine is quiet: starting at Slow would let a launch sleep
    // through the first five minutes of a burst.
    [AvaloniaFact]
    public void ANewInstanceStartsFast()
    {
        Assert.Equal(UsagePollCadence.Fast, Make(new FakeUsageSource()).PollInterval);
    }

    // ...and so does a switch that has just been turned on, for the same
    // reason the poll floor is cleared alongside it.
    //
    // The settings are pinned explicitly rather than inherited, and that is not
    // ceremony: SyncToSettings closes everything and returns early when no
    // source is enabled, so without this the test asserts on a line it never
    // reached. It passed in Debug and failed in Release on the first run,
    // because Release reorders a parallel suite and a sibling case had left
    // every usage flag off — the exact Debug/Release divergence CLAUDE.md
    // describes, fixed by making the test independent of what else ran rather
    // than by loosening it.
    [AvaloniaFact]
    public void SyncToSettingsReturnsToTheFastCadence()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = true;

        var orbs = Make(new FakeUsageSource());
        var reading = Usage(null, "board");

        orbs.Apply(new[] { reading }, Now);
        for (var quiet = 0; quiet < 4; quiet++) orbs.Apply(new[] { reading }, Now);
        Assert.Equal(UsagePollCadence.Slow, orbs.PollInterval);

        orbs.SyncToSettings(visible: false);

        Assert.Equal(UsagePollCadence.Fast, orbs.PollInterval);
    }
}
