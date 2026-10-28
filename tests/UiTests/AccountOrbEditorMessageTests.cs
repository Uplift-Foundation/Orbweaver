using Xunit;

namespace Orbweaver.Tests;

// The wording for each import failure. Pure functions, so deliberately NOT in
// AccountOrbEditorPanelTests: that class's constructor resets an account
// through AccountOrbCustomization, which raises a static event, and a plain
// [Fact] runs that constructor on a pool thread. Under a full Debug run any
// AccountOrbs a neighbouring test left subscribed would then redraw its orbs
// off the UI thread and throw. These tests need no fixture, so they get none.
public class AccountOrbEditorMessageTests
{
    // Every failure has its own words; none falls through to a generic line.
    [Fact]
    public void EveryImportErrorHasItsOwnMessage()
    {
        var messages = Enum.GetValues<IconImportError>()
            .Select(e => AccountOrbEditorPanel.MessageFor(e))
            .ToList();

        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m)));
        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    // A value added to the enum without words fails loudly here rather than
    // quietly showing a generic line.
    [Fact]
    public void UnknownImportError_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountOrbEditorPanel.MessageFor((IconImportError)999));

    // Two different "too large"s, each saying which limit was hit: the byte cap
    // talks about the file, the pixel cap about megapixels.
    [Fact]
    public void TooLargeMessages_NameTheirOwnLimit()
    {
        var bytes = AccountOrbEditorPanel.MessageFor(IconImportError.TooLarge);
        var pixels = AccountOrbEditorPanel.MessageFor(IconImportError.ImageDimensionsTooLarge);

        Assert.Contains("16 MB", bytes);
        Assert.Contains("file", bytes);
        Assert.Contains("50 megapixels", pixels);
        Assert.DoesNotContain("MB", pixels);
    }
}
