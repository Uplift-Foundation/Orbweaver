using Xunit;

namespace Orbweaver.Tests;

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverCheckedWaitsOnlyTheStartupDelay() =>
        Assert.Equal(UpdateSchedule.StartupDelay, UpdateSchedule.DelayUntilNextCheck(null, Now));

    [Fact]
    public void ACheckAnHourAgoWaitsOutTheRestOfTheDay() =>
        Assert.Equal(TimeSpan.FromHours(23), UpdateSchedule.DelayUntilNextCheck(Now.AddHours(-1), Now));

    [Theory]
    [InlineData(24)]    // exactly due
    [InlineData(48)]    // laptop slept for two days
    public void OverdueStillWaitsTheStartupDelayNotZero(int hoursAgo) =>
        Assert.Equal(UpdateSchedule.StartupDelay, UpdateSchedule.DelayUntilNextCheck(Now.AddHours(-hoursAgo), Now));

    [Fact]
    public void ALastCheckInTheFutureWaitsOneFullIntervalFromNow() =>
        Assert.Equal(UpdateSchedule.Interval, UpdateSchedule.DelayUntilNextCheck(Now.AddDays(3), Now));

    [Fact]
    public void TheConstantsAreTheOnesTheDesignSaid()
    {
        Assert.Equal(TimeSpan.FromHours(24), UpdateSchedule.Interval);
        Assert.Equal(TimeSpan.FromHours(1), UpdateSchedule.FailureRetry);
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateSchedule.StartupDelay);
    }
}
