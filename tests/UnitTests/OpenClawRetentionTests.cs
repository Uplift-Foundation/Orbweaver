using Xunit;

namespace Orbweaver.Tests
{
    // Which gateway sessions the age rules never apply to (CB-270).
    //
    // All six combinations of pinned x native kind, because the rule is an OR and
    // the dangerous mistake is one arm too wide: an unpinned Main is the control,
    // since native mains are heartbeat targets the gateway keeps forever and
    // exempting them would be three permanent orbs nobody asked for.
    public class OpenClawRetentionTests
    {
        [Theory]
        [InlineData(false, OpenClawNativeKind.None, false)]
        [InlineData(false, OpenClawNativeKind.Main, false)]
        [InlineData(false, OpenClawNativeKind.Dashboard, true)]
        [InlineData(true, OpenClawNativeKind.None, true)]
        [InlineData(true, OpenClawNativeKind.Main, true)]
        [InlineData(true, OpenClawNativeKind.Dashboard, true)]
        public void PinnedOrDashboardIsKeptAndNothingElseIs(
            bool pinned, OpenClawNativeKind native, bool expected) =>
            Assert.Equal(expected, OpenClawRetention.KeptRegardlessOfAge(pinned, native));

        // The computed property on Session is the same answer, so the scan and the
        // parse cannot disagree about it.
        [Fact]
        public void ASessionReportsTheSameAnswer()
        {
            OpenClawSessions.Session Build(OpenClawNativeKind native, bool pinned) => new(
                "k", "t", "c", "idle", DateTime.UtcNow, null, SessionKind.Main, false,
                Native: native, GatewayPinned: pinned);

            Assert.True(Build(OpenClawNativeKind.Dashboard, false).KeptRegardlessOfAge);
            Assert.True(Build(OpenClawNativeKind.None, true).KeptRegardlessOfAge);
            Assert.False(Build(OpenClawNativeKind.Main, false).KeptRegardlessOfAge);
        }
    }
}
