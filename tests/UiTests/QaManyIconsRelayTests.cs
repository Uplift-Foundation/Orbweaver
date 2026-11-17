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

// QA probe (Ines): the REAL AccountOrbs path with more account icons than the old store held. Counts the usage events AccountOrbs raises
// and the icon hashes the relay would be told, and requires the event stream to go quiet (no event for a full second) at a bounded count.
[Collection("Settings")]
public class QaManyIconsRelayTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private sealed class FakeUsageSource : IUsageSource { public IReadOnlyList<AccountUsage> Read() => Array.Empty<AccountUsage>(); }
    private readonly string? _settingsDir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
    private readonly List<AccountOrbs> _made = new();
    private readonly List<IReadOnlyList<RelayUsage.Account>> _seen = new();
    private void Seen(IReadOnlyList<RelayUsage.Account> a) => _seen.Add(a);

    public void Dispose()
    {
        AccountOrbs.UsageApplied -= Seen;
        foreach (var o in _made) o.CloseAll();
        AccountOrbWindow.ClearBreathingForTests();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _settingsDir);
        OrbweaverSettings.ReloadForTests();
    }

    [AvaloniaTheory] [InlineData(9)] [InlineData(16)]
    public void ManyAccountIconsSettleToOneIconHashEach(int n)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", Directory.CreateTempSubdirectory("orb340-many-").FullName);
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AccountUsageEnabled = true;
        AccountOrbs.UsageApplied += Seen;

        var orbs = new AccountOrbs(new FakeUsageSource()); _made.Add(orbs);
        var readings = Enumerable.Range(0, n).Select(i => new AccountUsage($"/cfg/{i}", $"acct{i}", true, "max",
            new UsageWindow(10, Now.AddHours(3)), null, null, Now, AccountUsageSource.ClaudeCode, null,
            AccountIdentity.Derive("claude", $"uuid-{i}", "org"))).ToList();
        orbs.Apply(readings, Now);
        _seen.Clear();

        var dir = Path.Combine(Path.GetTempPath(), "orb340-many-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        for (var i = 0; i < n; i++)
        {
            using var bmp = new SKBitmap(32, 32); bmp.Erase(new SKColor((byte)(10 + i * 11), (byte)(200 - i * 9), (byte)(30 + i * 7)));
            using var img = SKImage.FromBitmap(bmp);
            var f = Path.Combine(dir, $"i{i}.png"); File.WriteAllBytes(f, img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
            Assert.Null(AccountOrbCustomization.SetIcon(AccountOrbs.PositionKey($"/cfg/{i}"), f).Error);
        }

        // Pump until no event has arrived for a full second, or give up at 40 s (unbounded loop).
        var total = Stopwatch.StartNew(); var quiet = Stopwatch.StartNew(); var lastCount = _seen.Count;
        while (total.Elapsed < TimeSpan.FromSeconds(40) && quiet.Elapsed < TimeSpan.FromSeconds(1))
        {
            Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(10);
            if (_seen.Count != lastCount) { lastCount = _seen.Count; quiet.Restart(); }
        }
        var final = _seen.Last();
        var hashes = final.Select(a => a.IconHash).ToList();
        output.WriteLine($"n={n} events={_seen.Count} elapsed={total.Elapsed.TotalSeconds:F1}s hashes={hashes.Count(h => h is not null)}/{n} distinct={hashes.Where(h => h is not null).Distinct().Count()}");
        Assert.True(quiet.Elapsed >= TimeSpan.FromSeconds(1), $"n={n}: events never went quiet ({_seen.Count} events in {total.Elapsed.TotalSeconds:F0}s)");
        Assert.True(_seen.Count <= 4 * n, $"n={n}: {_seen.Count} usage events");
        Assert.Equal(n, hashes.Count(h => h is not null));
        Assert.Equal(n, hashes.Distinct().Count());
    }
}
