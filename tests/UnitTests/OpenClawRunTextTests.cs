using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4: the chip's wording and the time-limit choices.
public class OpenClawRunTextTests
{
    [Fact]
    public void TheChipIsModelThenThinkingThenFast()
    {
        Assert.Equal("Claude Opus 4.6 · adaptive · fast", OpenClawRunText.Chip("Claude Opus 4.6", "adaptive", true));
        Assert.Equal("Claude Opus 4.6 · adaptive", OpenClawRunText.Chip("Claude Opus 4.6", "adaptive", false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ANoThinkingLabelIsLeftOut(string? label)
    {
        Assert.Equal("Local", OpenClawRunText.Chip("Local", label, false));
        Assert.Equal("Local · fast", OpenClawRunText.Chip("Local", label, true));
    }

    // The choices have no zero and no "no limit": the gateway reads 0 as none.
    [Fact]
    public void TheTimeLimitChoicesAreTwoTenThirtySixtyAndNeverZero()
    {
        Assert.Equal(new[] { 2, 10, 30, 60 }, OpenClawRunText.TimeLimitMinutes);
        Assert.All(OpenClawRunText.TimeLimitMinutes, m => Assert.True(m > 0));
    }

    [Fact]
    public void TheTimeLimitReadsInMinutesOrAsDefault()
    {
        Assert.Equal("10 minutes", OpenClawRunText.TimeLimitText(10));
        Assert.Equal("Default", OpenClawRunText.TimeLimitText(null));
    }

    // The tooltip says what a person would otherwise find out afterwards.
    [Fact]
    public void TheChipTipNamesTheRemindersAndHeartbeats()
    {
        Assert.Contains("reminders", OpenClawRunText.ChipTip);
        Assert.Contains("heartbeats", OpenClawRunText.ChipTip);
    }
}
