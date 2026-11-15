using Xunit;

namespace Orbweaver.Tests;

// CB-281: the auto-group row's choices and search words are pure data, so they
// are pinned without a window.
public class SettingsFilterAutoGroupTests
{
    [Fact]
    public void TheChoicesCoverEveryAttributeOnceInDisplayOrder()
    {
        Assert.Equal(
            new[] { AutoGroupBy.Off, AutoGroupBy.Directory, AutoGroupBy.Cli, AutoGroupBy.Profile, AutoGroupBy.Account },
            SettingsFilter.AutoGroupChoices.Select(c => c.Value).ToArray());
        Assert.Equal(SettingsFilter.AutoGroupChoices.Length,
            SettingsFilter.AutoGroupChoices.Select(c => c.Label).Distinct().Count());
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("directory")]
    [InlineData("profile")]
    [InlineData("account")]
    [InlineData("CLI")]
    public void TheHelpIsWhatTheFilterSearches(string word) =>
        Assert.True(SettingsFilter.Matches(word, SettingsFilter.AutoGroupHelp));
}
