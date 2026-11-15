using Xunit;

namespace Orbweaver.Tests;

// CB-281: where the auto group sits in an orb's group precedence. Explicit membership
// first, then an OwnShape cluster, then the auto group, then main — and a manual group
// is reachable only through the first step.
public class OrbGroupsAutoPrecedenceTests
{
    private const string Auto = "a-cli-0123456789ab";

    private static string Id(string? membership, OrbCluster cluster, ClusterMode hb, ClusterMode cron, ClusterMode native, string? auto) =>
        OrbGroups.GroupIdFor(membership, cluster, hb, cron, native, auto);

    private const ClusterMode Chats = ClusterMode.WithChats;
    private const ClusterMode Own = ClusterMode.OwnShape;

    [Fact]
    public void AnExplicitManualGroupBeatsAnAutoGroup() =>
        Assert.Equal("g-0a1b2c3d", Id("g-0a1b2c3d", OrbCluster.Chats, Chats, Chats, Chats, Auto));

    [Fact]
    public void AnExplicitMainBeatsAnAutoGroup() =>
        Assert.Equal(OrbGroups.Main, Id(OrbGroups.Main, OrbCluster.Chats, Chats, Chats, Chats, Auto));

    [Theory]
    [InlineData("Heartbeats", "heartbeats")]
    [InlineData("Crons", "crons")]
    [InlineData("Native", "native")]
    public void AnOwnShapeClusterBeatsAnAutoGroup(string cluster, string expected)
    {
        var which = Enum.Parse<OrbCluster>(cluster);
        Assert.Equal(expected, Id(null, which, Own, Own, Own, Auto));
    }

    [Theory]
    [InlineData("Heartbeats")]
    [InlineData("Crons")]
    [InlineData("Native")]
    public void AClusterShownWithTheChatsTakesTheAutoGroup(string cluster) =>
        Assert.Equal(Auto, Id(null, Enum.Parse<OrbCluster>(cluster), Chats, Chats, Chats, Auto));

    [Fact]
    public void AnOrdinaryOrbTakesTheAutoGroup() =>
        Assert.Equal(Auto, Id(null, OrbCluster.Chats, Own, Own, Own, Auto));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithNoAutoGroupItFallsToMain(string? auto) =>
        Assert.Equal(OrbGroups.Main, Id(null, OrbCluster.Chats, Chats, Chats, Chats, auto));

    [Fact]
    public void ABlankMembershipDoesNotBlockTheAutoGroup() =>
        Assert.Equal(Auto, Id("  ", OrbCluster.Chats, Chats, Chats, Chats, Auto));

    // Manual groups never gain members: even a buggy classifier handing back a manual or
    // predefined id cannot put an orb there.
    [Theory]
    [InlineData("g-0a1b2c3d")]
    [InlineData("heartbeats")]
    [InlineData("main")]
    public void ANonAutoIdFromTheClassifierIsIgnored(string id) =>
        Assert.Equal(OrbGroups.Main, Id(null, OrbCluster.Chats, Chats, Chats, Chats, id));
}
