using Xunit;

namespace Orbweaver.Tests;

// CB-287: override precedence, pure — no window, no settings.
public class AccountOrbLooksTests
{
    private const string Icon = "0123456789ab-0123456789abcdef.png";

    [Fact]
    public void NoOverride_DerivesFromLabel()
    {
        var look = AccountOrbLooks.Resolve("claude-buddy", null, true);
        Assert.Equal("Cb", look.Glyph);
        Assert.Equal("claude-buddy", look.DisplayName);
        Assert.Null(look.IconFile);
    }

    [Fact]
    public void Initials_Win_Verbatim()
    {
        var look = AccountOrbLooks.Resolve("claude-buddy", new(" xYz ", null, null), true);
        Assert.Equal("xY", look.Glyph);
    }

    [Fact]
    public void NameOnly_LettersFromName_AndNameIsDisplayName()
    {
        var look = AccountOrbLooks.Resolve("claude-buddy", new(null, "Acme Corp", null), true);
        Assert.Equal("Ac", look.Glyph);
        Assert.Equal("Acme Corp", look.DisplayName);
    }

    [Fact]
    public void NameAndInitials_InitialsWin()
    {
        var look = AccountOrbLooks.Resolve("x", new("QQ", "Acme Corp", null), true);
        Assert.Equal("QQ", look.Glyph);
        Assert.Equal("Acme Corp", look.DisplayName);
    }

    [Fact]
    public void BlankInitials_FallBack()
    {
        Assert.Equal("Cb", AccountOrbLooks.Resolve("claude-buddy", new("  ", null, null), true).Glyph);
    }

    [Fact]
    public void TwoLetterOff_ClipsOverrideToOne()
    {
        Assert.Equal("x", AccountOrbLooks.Resolve("a", new("xy", null, null), false).Glyph);
    }

    [Fact]
    public void MintedIconFile_Kept()
    {
        Assert.Equal(Icon, AccountOrbLooks.Resolve("a", new(null, null, Icon), true).IconFile);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/abs/path.png")]
    [InlineData("a\\b.png")]
    [InlineData("0123456789AB-0123456789abcdef.png")]
    [InlineData("0123456789ab-0123456789abcdef.jpg")]
    public void UnsafeIconFile_Dropped(string name)
    {
        Assert.Null(AccountOrbLooks.Resolve("a", new(null, null, name), true).IconFile);
    }

    [Fact]
    public void Normalise_TrimsAndNullsBlanks()
    {
        var n = new AccountOrbOverride("  ", " n ", "").Normalise();
        Assert.Equal(new AccountOrbOverride(null, "n", null), n);
        Assert.False(n.IsEmpty);
    }

    [Fact]
    public void AllBlank_IsEmpty()
    {
        Assert.True(new AccountOrbOverride(" ", null, "").IsEmpty);
    }

    [Fact]
    public void IsSafeFileName_NullIsUnsafe()
    {
        Assert.False(AccountOrbIcons.IsSafeFileName(null));
    }
}

[Collection("Settings")]
public class AccountOrbLooksForTests
{
    [Fact]
    public void For_ReadsSettingsByPositionKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orb287-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();

        OrbweaverSettings.TwoLetterGlyphs = true;
        var usage = new AccountUsage("/home/a/.claude", "work", true, null, null, null, null, DateTimeOffset.UtcNow);
        Assert.Equal("Wo", AccountOrbLooks.For(usage).Glyph);

        OrbweaverSettings.SetAccountOrbOverride(
            AccountOrbs.PositionKey("/home/a/.claude"), new("ZZ", "Zed", null));
        var look = AccountOrbLooks.For(usage);
        Assert.Equal("ZZ", look.Glyph);
        Assert.Equal("Zed", look.DisplayName);
    }
}
