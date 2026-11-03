using Xunit;

namespace Orbweaver.Tests;

// CB-298: which tray asset, and what the hover text says, as pure functions.
public class TrayIconLookTests
{
    [Theory]
    [InlineData(0, 0, "idle")]
    [InlineData(0, 3, "generating")]
    [InlineData(1, 0, "waiting")]
    [InlineData(2, 5, "waiting")]   // someone waiting on you outranks work in progress
    public void StateFollowsTheSessionCounts(int waiting, int generating, string expected) =>
        Assert.Equal(expected, TrayIconLook.State(waiting, generating));

    [Theory]
    [InlineData("idle", false, "tray-idle")]
    [InlineData("generating", false, "tray-generating")]
    [InlineData("waiting", false, "tray-waiting")]
    [InlineData("idle", true, "tray-idle-muted")]
    [InlineData("generating", true, "tray-generating-muted")]
    [InlineData("waiting", true, "tray-waiting-muted")]
    public void AssetNameIsTheStateMutedOrNot(string state, bool muted, string expected) =>
        Assert.Equal(expected, TrayIconLook.AssetName(state, muted));

    [Fact]
    public void SummaryEndsInMutedWhenMutedAndIsUnchangedOtherwise()
    {
        Assert.Equal(TrayController.Summary(2, 1, 1), TrayController.Summary(2, 1, 1, muted: false));
        Assert.Equal(TrayController.Summary(2, 1, 1) + ", muted", TrayController.Summary(2, 1, 1, muted: true));
        Assert.Equal(TrayController.Summary(0, 0, 0) + ", muted", TrayController.Summary(0, 0, 0, muted: true));
    }
}
