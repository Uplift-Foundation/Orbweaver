using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-340: an override edit on this PC reaches the phone's usage frame at once. The phone hears a
// rename, a new picture or a Reset from AccountOrbs.UsageApplied, which used to fire only on a poll
// (up to five minutes later); these drive the real orbs, the real settings file and the real
// customization write path, with the clock held by the test, and assert on what the relay would
// have been handed.
[Collection("Settings")]
public class AccountOrbIdentityRelayTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Id = "ac7d4a85766f600f0bbb94dfc5321dc3";

    private sealed class FakeUsageSource : IUsageSource
    {
        public IReadOnlyList<AccountUsage> Read() => Array.Empty<AccountUsage>();
    }

    private readonly Func<DateTimeOffset> _clock = AccountOrbCustomization.Clock;
    private readonly List<AccountOrbs> _made = new();
    private readonly List<IReadOnlyList<RelayUsage.Account>> _seen = new();

    // Its own settings directory, so the overrides these write never reach the file the rest of the
    // suite shares (a leftover "Q✦" on the default account made a neighbour's glyph assertion fail).
    private readonly string? _settingsDir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public AccountOrbIdentityRelayTests()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR",
            Directory.CreateTempSubdirectory("orb340-ui-").FullName);
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = true;
        AccountOrbCustomization.Clock = () => DateTimeOffset.FromUnixTimeSeconds(1794100000);
        AccountOrbs.UsageApplied += Seen;
    }

    private void Seen(IReadOnlyList<RelayUsage.Account> a) => _seen.Add(a);

    public void Dispose()
    {
        AccountOrbs.UsageApplied -= Seen;
        foreach (var orbs in _made) orbs.CloseAll();
        AccountOrbCustomization.Clock = _clock;
        AccountOrbWindow.ClearBreathingForTests();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _settingsDir);
        OrbweaverSettings.ReloadForTests();
    }

    private AccountOrbs Started()
    {
        var orbs = new AccountOrbs(new FakeUsageSource());
        _made.Add(orbs);
        orbs.SetEmail(string.Empty, "river.quill@example.com");
        orbs.Apply([new AccountUsage(null, "river.quill", true, "max",
            new UsageWindow(62, Now.AddHours(3)), new UsageWindow(31, Now.AddDays(3)), null, Now,
            AccountUsageSource.ClaudeCode, null, Id)], Now);
        _seen.Clear();
        return orbs;
    }

    private RelayUsage.Account Latest() => Assert.Single(_seen.Last());

    [AvaloniaFact]
    public void ARenameReachesTheRelayAtOnceWithTheNewKeysAndTheAccountsId()
    {
        Started();
        AccountOrbCustomization.SetName(AccountOrbs.PositionKey(null), "Quillwork Studio");

        var sent = Latest();
        Assert.Equal((Id, "Quillwork Studio", "Qs", (long?)1794100000), (sent.AccountId, sent.DisplayName, sent.Initials, sent.OverrideUpdatedAt));
        Assert.Equal("river.quill@example.com", sent.Name);   // the account's own name is unchanged: the override travels beside it
        Assert.Null(sent.IconHash);

        // And it is a frame the tracker sends now, without waiting for a poll's numbers to move.
        var tracker = new RelayUsageTracker();
        Assert.Single(tracker.Next([sent with { DisplayName = null, OverrideUpdatedAt = null }]));
        Assert.Single(tracker.Next([sent]));
    }

    [AvaloniaFact]
    public void ATypedInitialsOverrideIsWhatThePhoneIsToldTheOrbWears()
    {
        Started();
        AccountOrbCustomization.SetInitials(AccountOrbs.PositionKey(null), "Q✦");
        Assert.Equal("Q✦", Latest().Initials);
    }

    [AvaloniaFact]
    public void AResetTakesTheNamesAndTheStampBackOffTheFrame()
    {
        Started();
        var key = AccountOrbs.PositionKey(null);
        AccountOrbCustomization.SetName(key, "Quillwork Studio");
        AccountOrbCustomization.Reset(key);

        var sent = Latest();
        Assert.Null(sent.DisplayName);
        Assert.Null(sent.OverrideUpdatedAt);
        Assert.Equal("Ri", sent.Initials);   // the derived glyph is still sent: the phone shows one for an account nobody renamed
        Assert.Equal(Id, sent.AccountId);
    }

    [AvaloniaFact]
    public void AnIconIsOfferedOnlyOnceItsBundleIsBuiltAndThePhoneIsToldWhenItIs()
    {
        Started();
        var key = AccountOrbs.PositionKey(null);

        var dir = Path.Combine(Path.GetTempPath(), "orb340-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var bmp = new SKBitmap(32, 32))
        {
            bmp.Erase(new SKColor(200, 30, 90));
            using var img = SKImage.FromBitmap(bmp);
            File.WriteAllBytes(Path.Combine(dir, "i.png"), img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        Assert.Null(AccountOrbCustomization.SetIcon(key, Path.Combine(dir, "i.png")).Error);

        // Straight after the edit the bundle is still building, so no hash is offered yet.
        Assert.Null(Latest().IconHash);

        // The worker finishes and raises Built; the orbs re-offer without anybody polling.
        var clock = Stopwatch.StartNew();
        while (_seen.Last().Single().IconHash is null && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(10);
        }

        var hash = Assert.Single(_seen.Last()).IconHash;
        Assert.NotNull(hash);
        var stored = AccountOrbCustomization.Current(key)!.IconFile!;
        Assert.Equal(PersonaImageBundle.HashOf(File.ReadAllBytes(Path.Combine(AccountOrbIcons.IconsDirectory, stored))), hash);
        Assert.NotNull(PersonaImageStore.Accounts.ById(hash!));        // and the phone's persona_image request finds it here
        Assert.Null(PersonaImageStore.Shared.ById(hash!));              // ...from the account store, not the persona one
    }

    [AvaloniaFact]
    public void ALookWrittenWhileNoOrbExistsDoesNothingAndAClosedOrbStopsListening()
    {
        var orbs = Started();
        orbs.CloseAll();
        _seen.Clear();
        AccountOrbCustomization.SetName(AccountOrbs.PositionKey(null), "Nobody is listening");
        Assert.Empty(_seen);
    }
}
