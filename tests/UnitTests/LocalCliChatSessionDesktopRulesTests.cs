using Xunit;

namespace Orbweaver.Tests;

// The two decisions behind the Desktop composer (CB-266), as truth tables over
// pure functions: whether the box is taken away, and what it says. No window, no
// settings, no session behind either, so every row of the plan's table is a case.
public class LocalCliChatSessionDesktopRulesTests
{
    private const string Bridge = "session_01FixtureOnly";

    private static SessionStatus Status(bool desktop = true, string bridge = Bridge) => new()
    {
        Source = SessionSource.ClaudeCode,
        Entrypoint = desktop ? SessionStatus.DesktopEntrypoint : "cli",
        BridgeSessionId = bridge,
    };

    private static readonly CloudSendability?[] Refusals =
    {
        null, CloudSendability.Ended, CloudSendability.Gone, CloudSendability.NotPermitted,
    };

    // --- DesktopReadOnly ------------------------------------------------------

    [Fact]
    public void ASessionThatIsNotDesktopHostedIsNeverReadOnlyHere()
    {
        foreach (var cloud in new[] { false, true })
            foreach (var refusal in Refusals)
                Assert.False(LocalCliChatSession.DesktopReadOnly(Status(desktop: false), refusal, cloud));
    }

    [Fact]
    public void NoBridgeIsReadOnlyWhateverElseIsTrue()
    {
        foreach (var cloud in new[] { false, true })
            foreach (var refusal in Refusals)
                Assert.True(LocalCliChatSession.DesktopReadOnly(Status(bridge: ""), refusal, cloud));
    }

    [Fact]
    public void ABridgedChatWithCloudOffIsReadOnly() =>
        Assert.True(LocalCliChatSession.DesktopReadOnly(Status(), null, cloudEnabled: false));

    [Fact]
    public void ABridgedChatWithCloudOnAndNoRefusalIsSendable() =>
        Assert.False(LocalCliChatSession.DesktopReadOnly(Status(), null, cloudEnabled: true));

    // CB-306: commands on keeps the box with cloud off; a refusal still wins.
    [Fact]
    public void ABridgedChatWithCloudOffButCommandsOnKeepsTheBoxUnlessRefused()
    {
        Assert.False(LocalCliChatSession.DesktopReadOnly(Status(), null, cloudEnabled: false, bridgeCommands: true));
        Assert.True(LocalCliChatSession.DesktopReadOnly(Status(), CloudSendability.NotPermitted, cloudEnabled: false, bridgeCommands: true));
    }

    [Theory]
    [InlineData("Ended")]
    [InlineData("Gone")]
    [InlineData("NotPermitted")]
    public void ARefusalTakesTheBoxAwayEvenWithCloudOn(string refusalName) =>
        Assert.True(LocalCliChatSession.DesktopReadOnly(Status(), System.Enum.Parse<CloudSendability>(refusalName), cloudEnabled: true));

    // --- DesktopComposerHintFor ----------------------------------------------

    [Fact]
    public void ASessionThatIsNotDesktopHostedHasNoDesktopHint() =>
        Assert.Null(LocalCliChatSession.DesktopComposerHintFor(Status(desktop: false), null, true, true));

    [Fact]
    public void NoBridgeSaysHowToTurnRemoteControlOnAheadOfEveryOtherInput()
    {
        foreach (var cloud in new[] { false, true })
            foreach (var reply in new[] { false, true })
                foreach (var refusal in Refusals)
                    Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint,
                        LocalCliChatSession.DesktopComposerHintFor(Status(bridge: ""), refusal, cloud, reply, busy: true));

        Assert.Contains("/rc", LocalCliChatSession.DesktopNoBridgeHint);
        Assert.Contains("Connect new sessions to Remote Control", LocalCliChatSession.DesktopNoBridgeHint);
    }

    [Fact]
    public void CloudOffNamesTheSettingAndTheLoginItUses()
    {
        var hint = LocalCliChatSession.DesktopComposerHintFor(Status(), null, cloudEnabled: false, replyEnabled: true);

        Assert.Equal(LocalCliChatSession.DesktopCloudOffHint, hint);
        Assert.Contains("Show cloud sessions", hint);
        Assert.Contains("login stored on this machine", hint);
    }

    // CB-306's product-readiness round: with commands on, cloud off says the
    // box takes commands only and names what turning cloud on would add.
    [Fact]
    public void CloudOffWithCommandsOnSaysCommandsOnly()
    {
        var hint = LocalCliChatSession.DesktopComposerHintFor(
            Status(), null, cloudEnabled: false, replyEnabled: true, bridgeCommands: true);

        Assert.Equal(LocalCliChatSession.DesktopCommandsOnlyHint, hint);
        foreach (var name in new[] { "/color", "/rename", "/model", "/permissions", "/think", "Show cloud sessions" })
            Assert.Contains(name, hint);
    }

    [Fact]
    public void ARefusalOutranksReplyingBeingOff()
    {
        Assert.Equal(LocalCliChatSession.DesktopNotPermittedHint,
            LocalCliChatSession.DesktopComposerHintFor(Status(), CloudSendability.NotPermitted, true, replyEnabled: false));
        Assert.Contains("different account", LocalCliChatSession.DesktopNotPermittedHint);
    }

    [Theory]
    [InlineData("Ended")]
    [InlineData("Gone")]
    public void AnEndedOrGoneBridgeSaysToReconnect(string refusalName)
    {
        Assert.Equal(LocalCliChatSession.DesktopBridgeGoneHint,
            LocalCliChatSession.DesktopComposerHintFor(Status(), System.Enum.Parse<CloudSendability>(refusalName), true, true));
        Assert.Contains("/rc", LocalCliChatSession.DesktopBridgeGoneHint);
    }

    [Fact]
    public void ReplyingOffKeepsTheExistingWording() =>
        Assert.Equal("Replying is off",
            LocalCliChatSession.DesktopComposerHintFor(Status(), null, true, replyEnabled: false));

    [Fact]
    public void ASendableChatSaysItArrivesAsATeammatesRequestAndBusyHasItsOwnWatermark()
    {
        Assert.Equal(LocalCliChatSession.DesktopBridgedHint,
            LocalCliChatSession.DesktopComposerHintFor(Status(), null, true, true));
        Assert.Contains("teammate's request", LocalCliChatSession.DesktopBridgedHint);

        Assert.Equal(CloudChatSendability.BusyHint,
            LocalCliChatSession.DesktopComposerHintFor(Status(), null, true, true, busy: true));
    }
}
