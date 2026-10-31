using System;
using System.IO;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-287: usage orbs wearing custom initials and a custom icon, drawn through
// real Skia. The last case is the acceptance proof for "the rings stay legible
// over the icon": a pixel diff, not an eyeball.
[Collection("Settings")]
public class AccountOrbOverrideScreenshots : IDisposable
{
    public void Dispose() => AccountOrbWindow.ClearBreathingForTests();

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static AccountUsage Usage(DateTimeOffset? readAt = null) =>
        new(null, "board", true, "team",
            new UsageWindow(40, Now.AddHours(3)), new UsageWindow(70, Now.AddDays(3)),
            new ExtraUsage(true, 400, 2000, "USD", 2, null),
            readAt ?? Now, AccountUsageSource.Codex);

    // An icon built to be adversarial: ring-calm green and white in a hard
    // checker, edge to edge, so any bleed past the disc would be unmistakable.
    private static string MintLoud()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orb287s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var bmp = new SKBitmap(64, 64);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                bmp.SetPixel(x, y, ((x / 8 + y / 8) % 2 == 0) ? new SKColor(0x5F, 0xD7, 0x9B) : SKColors.White);
        using var img = SKImage.FromBitmap(bmp);
        var src = Path.Combine(dir, "loud.png");
        File.WriteAllBytes(src, img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        var r = AccountOrbIcons.Import(src, "account:~", AccountOrbIcons.IconsDirectory);
        Assert.Null(r.Error);
        return r.FileName!;
    }

    private static AccountOrbWindow Orb(AccountUsage usage, AccountOrbLook look)
    {
        var orb = new AccountOrbWindow("k");
        orb.UpdateFrom(usage, look, Now);
        return orb;
    }

    // Pixels of the window as drawn, plus the device scale, so a DIP radius
    // from the orb's centre (36,36) can be turned into a pixel test.
    private static (SKBitmap Bitmap, double Scale) Render(AccountOrbWindow orb)
    {
        orb.Show();
        ScreenshotHelper.Flush();
        var size = new Avalonia.PixelSize(
            Math.Max(1, (int)Math.Ceiling(orb.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(orb.Bounds.Height)));
        using var target = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        ScreenshotHelper.Render(orb, target);
        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;
        return (SKBitmap.Decode(stream), size.Width / orb.Bounds.Width);
    }

    [AvaloniaFact]
    public void AnAccountWithCustomInitials()
    {
        var orb = Orb(Usage(), new AccountOrbLook("Ac", "Acme", null));
        ScreenshotHelper.Capture(orb, "account-orb-custom-initials.png");
        orb.Close();
    }

    [AvaloniaFact]
    public void AnAccountWithAnIcon()
    {
        var orb = Orb(Usage(), new AccountOrbLook("Ac", "Acme", MintLoud()));
        Assert.True(orb.IconVisible);
        ScreenshotHelper.Capture(orb, "account-orb-icon.png");
        orb.Close();
    }

    [AvaloniaFact]
    public void AnAccountWithAnIconStale()
    {
        var orb = Orb(Usage(Now - AccountUsage.StaleAfter), new AccountOrbLook("Ac", "Acme", MintLoud()));
        Assert.True(orb.IsDimmed);
        ScreenshotHelper.Capture(orb, "account-orb-icon-stale.png");
        orb.Close();
    }

    [AvaloniaFact]
    public void AnIconNeverTouchesTheRings()
    {
        var icon = MintLoud();
        var plain = Orb(Usage(), new AccountOrbLook("Ac", "Acme", null));
        var (without, scale) = Render(plain);
        plain.Close();

        var iconed = Orb(Usage(), new AccountOrbLook("Ac", "Acme", icon));
        var (with, _) = Render(iconed);
        ScreenshotHelper.CaptureAlreadyShown(iconed, "account-orb-icon-ring-diff.png");
        iconed.Close();

        Assert.True(iconed.IconVisible);
        Assert.Equal(without.Width, with.Width);

        int outsideDiffer = 0, outsideCount = 0, insideDiffer = 0;
        for (var y = 0; y < without.Height; y++)
        {
            for (var x = 0; x < without.Width; x++)
            {
                var dx = (x + 0.5) / scale - 36;
                var dy = (y + 0.5) / scale - 36;
                var r = Math.Sqrt(dx * dx + dy * dy);
                var differs = without.GetPixel(x, y) != with.GetPixel(x, y);

                if (r >= 15) { outsideCount++; if (differs) outsideDiffer++; }
                else if (r <= 12 && differs) insideDiffer++;
            }
        }

        // (a) Everything from the innermost ring outward — rings, tracks, CLI
        // badge — is pixel-identical with and without the icon.
        Assert.True(outsideCount > 1000);
        Assert.Equal(0, outsideDiffer);

        // (b) The paired negative control: the same render *does* change inside
        // the disc. Without this, (a) also passes when the icon silently failed
        // to draw at all.
        Assert.True(insideDiffer > 100, $"only {insideDiffer} pixels changed inside the disc");
    }
}
