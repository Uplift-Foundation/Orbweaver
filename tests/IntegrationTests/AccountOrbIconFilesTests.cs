using SkiaSharp;
using Xunit;

namespace Orbweaver.Tests;

// CB-287: icon files on disk, driven through the same write path the UI uses.
[Collection("Settings")]
public class AccountOrbIconFilesTests
{
    private const string Key = "account:~";

    private static string Setup(out string settingsDir)
    {
        settingsDir = Path.Combine(Path.GetTempPath(), "orb287f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", settingsDir);
        OrbweaverSettings.ReloadForTests();
        return settingsDir;
    }

    private static string Picture(string dir, string name, SKColor c)
    {
        using var bmp = new SKBitmap(16, 16);
        bmp.Erase(c);
        using var img = SKImage.FromBitmap(bmp);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        return p;
    }

    [Fact]
    public void IconsDir_IsUnderSettingsDir()
    {
        var dir = Setup(out _);
        Assert.Equal(Path.Combine(dir, "account-icons"), AccountOrbIcons.IconsDirectory);
    }

    [Fact]
    public void CopiedIcon_SurvivesSourceDeletion_AndIsRecordedInSettings()
    {
        var dir = Setup(out _);
        var src = Picture(dir, "src.png", SKColors.Red);
        var r = AccountOrbCustomization.SetIcon(Key, src);
        Assert.Null(r.Error);

        File.Delete(src);
        OrbweaverSettings.ReloadForTests();

        var name = AccountOrbCustomization.Current(Key)!.IconFile;
        Assert.Equal(r.FileName, name);
        Assert.Equal(IconLoadOutcome.Loaded, AccountOrbIcons.Load(AccountOrbIcons.IconsDirectory, name).Outcome);
    }

    [Fact]
    public void ReplaceIcon_DeletesOld()
    {
        var dir = Setup(out _);
        var first = AccountOrbCustomization.SetIcon(Key, Picture(dir, "a.png", SKColors.Red)).FileName!;
        var second = AccountOrbCustomization.SetIcon(Key, Picture(dir, "b.png", SKColors.Blue)).FileName!;
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(Path.Combine(AccountOrbIcons.IconsDirectory, first)));
        Assert.True(File.Exists(Path.Combine(AccountOrbIcons.IconsDirectory, second)));
        Assert.Equal(second, AccountOrbCustomization.Current(Key)!.IconFile);
    }

    [Fact]
    public void ReimportingSamePicture_KeepsTheFile()
    {
        var dir = Setup(out _);
        var src = Picture(dir, "a.png", SKColors.Red);
        var first = AccountOrbCustomization.SetIcon(Key, src).FileName!;
        var again = AccountOrbCustomization.SetIcon(Key, src).FileName!;
        Assert.Equal(first, again);
        Assert.True(File.Exists(Path.Combine(AccountOrbIcons.IconsDirectory, first)));
    }

    [Fact]
    public void FailedImport_LeavesPreviousIconAndSetting_AndRaisesNothing()
    {
        var dir = Setup(out _);
        var good = AccountOrbCustomization.SetIcon(Key, Picture(dir, "a.png", SKColors.Red)).FileName!;

        var raised = 0;
        void On(string _) => raised++;
        AccountOrbCustomization.Changed += On;
        try
        {
            var bad = Path.Combine(dir, "bad.png");
            File.WriteAllText(bad, "nope");
            Assert.Equal(IconImportError.NotAnImage, AccountOrbCustomization.SetIcon(Key, bad).Error);
            Assert.Equal(IconImportError.Unreadable, AccountOrbCustomization.SetIcon("", bad).Error);
        }
        finally { AccountOrbCustomization.Changed -= On; }

        Assert.Equal(0, raised);
        Assert.Equal(good, AccountOrbCustomization.Current(Key)!.IconFile);
        Assert.True(File.Exists(Path.Combine(AccountOrbIcons.IconsDirectory, good)));
    }

    [Fact]
    public void RemoveIcon_AndReset_DeleteTheFile()
    {
        var dir = Setup(out _);
        var a = AccountOrbCustomization.SetIcon(Key, Picture(dir, "a.png", SKColors.Red)).FileName!;
        AccountOrbCustomization.SetName(Key, "Acme");
        AccountOrbCustomization.RemoveIcon(Key);
        Assert.False(File.Exists(Path.Combine(AccountOrbIcons.IconsDirectory, a)));
        Assert.Equal("Acme", AccountOrbCustomization.Current(Key)!.Name);

        var b = AccountOrbCustomization.SetIcon(Key, Picture(dir, "b.png", SKColors.Blue)).FileName!;
        AccountOrbCustomization.Reset(Key);
        Assert.False(File.Exists(Path.Combine(AccountOrbIcons.IconsDirectory, b)));
        Assert.Null(AccountOrbCustomization.Current(Key));
    }
}
