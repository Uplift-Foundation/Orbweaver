using Xunit;

namespace Orbweaver.Tests
{
    // OrbClusters: which shape an orb joins, and whether it exists at all.
    //
    // Worth a case per outcome rather than a smoke test, because both mistakes
    // it can make are silent. Answering Hidden where the user said WithChats
    // takes an orb off the screen with nothing to say about it; answering group
    // 1 where the scan answered Hidden puts an orb in a shape that then has no
    // orb in it. Nothing on screen contradicts either.
    public class OrbClustersTests
    {
        // --- which cluster ---

        [Theory]
        [InlineData(SessionKind.Unknown)]
        [InlineData(SessionKind.Main)]
        [InlineData(SessionKind.Direct)]
        [InlineData(SessionKind.Channel)]
        [InlineData(SessionKind.Remote)]
        public void EverySessionKindThatIsNotACronIsAChat(SessionKind kind)
            => Assert.Equal(OrbCluster.Chats, OrbClusters.Of(heartbeat: false, kind, native: false));

        [Fact]
        public void ACronIsACron()
            => Assert.Equal(OrbCluster.Crons, OrbClusters.Of(heartbeat: false, SessionKind.Cron, native: false));

        // Main in particular, because that is the kind a heartbeat session
        // actually has — OpenClawHeartbeat's whole rule is "the heartbeat lands
        // in the agent's own main session". A classifier that read only the kind
        // would put every heartbeat in with the chats.
        [Fact]
        public void AHeartbeatIsAHeartbeatAndNotItsUnderlyingKind()
        {
            Assert.Equal(OrbCluster.Heartbeats, OrbClusters.Of(heartbeat: true, SessionKind.Main, native: false));
            Assert.Equal(OrbCluster.Heartbeats, OrbClusters.Of(heartbeat: true, SessionKind.Unknown, native: false));
        }

        // The real payload behind the precedence rule: the gateway's own
        // heartbeat job is a cron, listed by `openclaw cron list --all` as
        // "Heartbeat (<agent-id>)", so both detectors say yes at once. It has to
        // count as one thing, and the heartbeat is the more specific answer.
        [Fact]
        public void ASessionThatIsBothACronAndTheHeartbeatCountsAsAHeartbeat()
            => Assert.Equal(OrbCluster.Heartbeats, OrbClusters.Of(heartbeat: true, SessionKind.Cron, native: false));

        // Read straight off the two detectors rather than through hand-made
        // booleans, so the precedence above is asserted against the strings a
        // gateway really sends rather than against this test's idea of them.
        [Fact]
        public void ThePrecedenceHoldsForTheKeysAGatewayActuallySends()
        {
            const string cronKey = "agent:main:cron:8f2c1e4a";
            const string mainKey = "agent:alexis:main";
            const string channelKey = "agent:main:discord:channel:1900000000000000001";

            OrbCluster Classify(string key, string? label = null)
                => OrbClusters.Of(
                    OpenClawHeartbeat.Is(key, label),
                    OpenClawSessionKind.From(key, null),
                    native: false);

            Assert.Equal(OrbCluster.Crons, Classify(cronKey));
            Assert.Equal(OrbCluster.Heartbeats, Classify(mainKey));
            Assert.Equal(OrbCluster.Chats, Classify(channelKey));

            // The overlap, as the gateway labels it.
            Assert.Equal(OrbCluster.Heartbeats, Classify(cronKey, "Cron: Heartbeat (main)"));

            // And a job somebody wrote that merely mentions the word is still a
            // cron — the prefix rule in OpenClawHeartbeat, seen from here.
            Assert.Equal(OrbCluster.Crons, Classify(cronKey, "Cron: nightly-report"));
        }

        // --- CB-270: native sessions ---

        [Fact]
        public void ANativeSessionIsNative()
            => Assert.Equal(OrbCluster.Native, OrbClusters.Of(heartbeat: false, SessionKind.Main, native: true));

        // Warren's ruling (it replaced CB-270's first one): the heartbeat setting
        // wins. A native main is a heartbeat target by key, and the gateway's
        // heartbeat job wakes it every few minutes, so it follows "Heartbeat
        // sessions". Putting native first made a hidden heartbeat's own main
        // session keep coming back...
        [Fact]
        public void ANativeHeartbeatMainIsGovernedByTheHeartbeatSetting()
            => Assert.Equal(OrbCluster.Heartbeats, OrbClusters.Of(heartbeat: true, SessionKind.Main, native: true));

        // ...and "Agents' own sessions" governs the native sessions that are not
        // heartbeats, in practice a New Chat.
        [Fact]
        public void ANativeSessionThatIsNotAHeartbeatIsGovernedByTheNativeSetting()
        {
            Assert.Equal(OrbCluster.Native, OrbClusters.Of(heartbeat: false, SessionKind.Main, native: true));
            Assert.Equal(ClusterMode.OwnShape, OrbClusters.ModeOf(
                OrbCluster.Native, ClusterMode.Hidden, ClusterMode.Hidden, ClusterMode.OwnShape));
        }

        // A main with WhatsApp origin or delivery is not native
        // (OpenClawNative says None for it) and is a heartbeat as it always was.
        [Fact]
        public void AMainWithASurfaceStaysAHeartbeat()
        {
            var native = OpenClawNative.Of("main", "whatsapp", true, "whatsapp") != OpenClawNativeKind.None;

            Assert.Equal(OrbCluster.Heartbeats, OrbClusters.Of(heartbeat: true, SessionKind.Main, native));
        }

        [Fact]
        public void EachClusterReadsItsOwnModeIncludingNative()
        {
            Assert.Equal(ClusterMode.Hidden, OrbClusters.ModeOf(
                OrbCluster.Native, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.Hidden));
            Assert.False(OrbClusters.Visible(
                OrbCluster.Native, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.Hidden));
            Assert.True(OrbClusters.Visible(
                OrbCluster.Heartbeats, ClusterMode.WithChats, ClusterMode.Hidden, ClusterMode.Hidden));
        }

        [Fact]
        public void NativeIsSlotThreeUnderOwnShapeAndTheChatsOtherwise()
        {
            const ClusterMode own = ClusterMode.OwnShape;

            Assert.Equal(3, OrbClusters.GroupOf(OrbCluster.Native, own, own, own));
            Assert.Equal(0, OrbClusters.GroupOf(OrbCluster.Native, own, own, ClusterMode.WithChats));
            Assert.Equal(0, OrbClusters.GroupOf(OrbCluster.Native, own, own, ClusterMode.Hidden));

            // The existing slots do not move.
            Assert.Equal(1, OrbClusters.GroupOf(OrbCluster.Heartbeats, own, own, own));
            Assert.Equal(2, OrbClusters.GroupOf(OrbCluster.Crons, own, own, own));
        }

        // --- the mode, and whether an orb exists ---

        [Theory]
        [InlineData(ClusterMode.Hidden)]
        [InlineData(ClusterMode.WithChats)]
        [InlineData(ClusterMode.OwnShape)]
        public void ChatsAreNeverHiddenAndNeverGetTheirOwnMode(ClusterMode both)
        {
            // Whatever the two timer settings say, a conversation is a
            // conversation: there is no setting that hides every orb, and the
            // shape chats join is the main ArrangeShape rather than one of these.
            Assert.Equal(ClusterMode.WithChats, OrbClusters.ModeOf(OrbCluster.Chats, both, both, both));
            Assert.True(OrbClusters.Visible(OrbCluster.Chats, both, both, both));
            Assert.Equal(0, OrbClusters.GroupOf(OrbCluster.Chats, both, both, both));
        }

        [Fact]
        public void EachTimerClusterReadsItsOwnSettingAndNotTheOther()
        {
            const ClusterMode hb = ClusterMode.Hidden;
            const ClusterMode cron = ClusterMode.OwnShape;

            Assert.Equal(hb, OrbClusters.ModeOf(OrbCluster.Heartbeats, hb, cron, ClusterMode.WithChats));
            Assert.Equal(cron, OrbClusters.ModeOf(OrbCluster.Crons, hb, cron, ClusterMode.WithChats));

            Assert.False(OrbClusters.Visible(OrbCluster.Heartbeats, hb, cron, ClusterMode.WithChats));
            Assert.True(OrbClusters.Visible(OrbCluster.Crons, hb, cron, ClusterMode.WithChats));
        }

        [Theory]
        [InlineData(ClusterMode.Hidden, false)]
        [InlineData(ClusterMode.WithChats, true)]
        [InlineData(ClusterMode.OwnShape, true)]
        public void OnlyHiddenTakesAnOrbOffTheScreen(ClusterMode mode, bool visible)
        {
            Assert.Equal(visible, OrbClusters.Visible(OrbCluster.Heartbeats, mode, mode, ClusterMode.WithChats));
            Assert.Equal(visible, OrbClusters.Visible(OrbCluster.Crons, mode, mode, ClusterMode.WithChats));
        }

        // --- which group ---

        [Fact]
        public void OnlyOwnShapeMovesAnOrbOutOfTheChatsGroup()
        {
            foreach (var mode in new[] { ClusterMode.Hidden, ClusterMode.WithChats })
            {
                Assert.Equal(0, OrbClusters.GroupOf(OrbCluster.Heartbeats, mode, mode, ClusterMode.WithChats));
                Assert.Equal(0, OrbClusters.GroupOf(OrbCluster.Crons, mode, mode, ClusterMode.WithChats));
            }

            const ClusterMode own = ClusterMode.OwnShape;
            Assert.Equal(1, OrbClusters.GroupOf(OrbCluster.Heartbeats, own, own, ClusterMode.WithChats));
            Assert.Equal(2, OrbClusters.GroupOf(OrbCluster.Crons, own, own, ClusterMode.WithChats));
        }

        // Fixed slots, not a dense count of the groups in use. The alternative
        // would make group 1 mean heartbeats on a screen that has some and crons
        // on one that does not — so the shape at index 1 would depend on which
        // sessions happened to be running rather than on the setting, and a user
        // who chose a star for their crons would sometimes get the heartbeats'
        // circle instead.
        [Fact]
        public void ACronsGroupIndexDoesNotDependOnWhetherHeartbeatsAreOnScreen()
        {
            Assert.Equal(
                OrbClusters.GroupOf(OrbCluster.Crons, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.WithChats),
                OrbClusters.GroupOf(OrbCluster.Crons, ClusterMode.Hidden, ClusterMode.OwnShape, ClusterMode.WithChats));

            Assert.Equal(
                OrbClusters.GroupOf(OrbCluster.Crons, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.WithChats),
                OrbClusters.GroupOf(OrbCluster.Crons, ClusterMode.WithChats, ClusterMode.OwnShape, ClusterMode.WithChats));
        }

        // --- the wire format ---

        [Theory]
        [InlineData(ClusterMode.Hidden, "hidden")]
        [InlineData(ClusterMode.WithChats, "chats")]
        [InlineData(ClusterMode.OwnShape, "own")]
        public void EveryModeRoundTripsThroughItsName(ClusterMode mode, string name)
        {
            Assert.Equal(name, OrbClusters.Name(mode));
            Assert.Equal(mode, OrbClusters.Parse(name));
        }

        [Theory]
        [InlineData("HIDDEN", ClusterMode.Hidden)]
        [InlineData("  Own  ", ClusterMode.OwnShape)]
        [InlineData("Chats", ClusterMode.WithChats)]
        public void ANameIsReadCaseInsensitivelyAndTrimmed(string text, ClusterMode expected)
            => Assert.Equal(expected, OrbClusters.Parse(text));

        // Unrecognised reads as the default and not as Hidden, which is the
        // difference between a typo costing nothing and a typo silently emptying
        // somebody's screen. A mode a *later* version invents arrives here the
        // same way.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("sideways")]
        [InlineData("true")]
        public void AnythingElseReadsAsTheDefaultRatherThanHidingOrbs(string? text)
        {
            Assert.Equal(ClusterMode.WithChats, OrbClusters.Parse(text));

            // And the caller can still say what "default" means, which is what
            // OrbweaverSettings' migration off the old boolean needs.
            Assert.Equal(ClusterMode.OwnShape, OrbClusters.Parse(text, ClusterMode.OwnShape));
        }
    }
}
