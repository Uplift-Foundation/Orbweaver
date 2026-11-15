using Xunit;

namespace Orbweaver.Tests;

// OpenClawNewChat.AvailabilityFor/ReasonFor: whether the new-chat dialog's
// OpenClaw radio item is a live choice, and what it says under itself when
// it isn't. Pure and settings-free, so every branch is reachable with no
// gateway and no OrbweaverSettings singleton in the picture — see the
// header comment on OpenClawNewChatAvailability.cs for why that separation
// matters here specifically.
public class OpenClawNewChatAvailabilityTests
{
    [Fact]
    public void OffEntirelyIsNoGateway()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.NoGateway,
            OpenClawNewChat.AvailabilityFor(enabled: false, host: "avatar.internal", replyEnabled: true));
    }

    [Fact]
    public void EnabledButNoHostIsNoGateway()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.NoGateway,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "", replyEnabled: true));
    }

    // OrbweaverSettings.OpenClawHost reads back "" rather than null when
    // unset (OrbweaverSettings.cs:886) — this is the shape AvailabilityFor
    // actually receives, not a null, so it's exercised directly rather than
    // trusting IsNullOrWhiteSpace to cover both the same way.
    [Fact]
    public void EnabledButWhitespaceHostIsNoGateway()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.NoGateway,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "   ", replyEnabled: true));
    }

    [Fact]
    public void EnabledWithHostButReplyOffIsReplyDisabled()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.ReplyDisabled,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "avatar.internal", replyEnabled: false));
    }

    [Fact]
    public void EnabledWithHostAndReplyOnIsReady()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.Ready,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "avatar.internal", replyEnabled: true));
    }

    [Fact]
    public void ReadyHasNoReason()
    {
        Assert.Null(OpenClawNewChat.ReasonFor(OpenClawNewChatAvailability.Ready));
    }

    [Fact]
    public void NoGatewayNamesItself()
    {
        Assert.Equal("No gateway configured.", OpenClawNewChat.ReasonFor(OpenClawNewChatAvailability.NoGateway));
    }

    [Fact]
    public void ReplyDisabledPointsAtTheSetting()
    {
        var reason = OpenClawNewChat.ReasonFor(OpenClawNewChatAvailability.ReplyDisabled);
        Assert.Contains("Allow replying to agents", reason);
    }

    // Sentence case with a closing full stop, matching the three local
    // CLIs' own disabled reasons — CB-168's dialog states every reason
    // directly on screen now, not only in a tooltip, so the four rows need
    // to read consistently beside each other.
    [Theory]
    [InlineData(OpenClawNewChatAvailability.NoGateway)]
    [InlineData(OpenClawNewChatAvailability.ReplyDisabled)]
    public void EveryDisabledReasonEndsWithAFullStop(OpenClawNewChatAvailability availability)
    {
        var reason = OpenClawNewChat.ReasonFor(availability);
        Assert.EndsWith(".", reason);
    }

    // CB-329: reading a conversation needs only a gateway; starting one still
    // needs the reply switch. ReplyDisabled is the one row where they differ.
    [Theory]
    [InlineData(OpenClawNewChatAvailability.Ready, true, true)]
    [InlineData(OpenClawNewChatAvailability.ReplyDisabled, false, true)]
    [InlineData(OpenClawNewChatAvailability.NoGateway, false, false)]
    public void StartingNeedsReplyAndOpeningNeedsOnlyAGateway(
        OpenClawNewChatAvailability availability, bool canStart, bool canOpen)
    {
        Assert.Equal(canStart, OpenClawNewChat.CanStartNew(availability));
        Assert.Equal(canOpen, OpenClawNewChat.CanOpenExisting(availability));
    }
}
