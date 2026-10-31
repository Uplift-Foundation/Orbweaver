using Xunit;

namespace Orbweaver.Tests
{
    // What kind of conversation a gateway session is, and which room it is in.
    //
    // Both classes under test say in their own comments why they were kept pure:
    // so the rule could be tested without a gateway. Until now nothing did. That
    // is worth more here than in most files, because the mistake this code can
    // make is silent *and* directional — a channel mislabelled as a direct
    // message tells the user a room other people can read is private, and
    // nothing else on screen would contradict it.
    //
    // Every "not observed" and "observed on a real gateway" note in the cases
    // below is quoting the source's own comments rather than guessing at what a
    // gateway does.
    public class OpenClawSessionKindTests
    {
        // --- From: the key is structural and is consulted first ---

        // "agent:x:cron:<uuid>" cannot be anything but a scheduled job, whatever
        // chatType claims — which is the reason the key wins over origin.
        [Theory]
        [InlineData("agent:main:cron:2f6c", null)]
        [InlineData("agent:main:cron:2f6c", "channel")]
        [InlineData("agent:main:CRON:2f6c", "direct")]
        public void ACronKeyIsACronJobWhateverTheChatTypeSays(string key, string? chatType)
        {
            Assert.Equal(SessionKind.Cron, OpenClawSessionKind.From(key, chatType));
        }

        // Main is its own kind rather than a direct message: every agent has one,
        // it is reached through the TUI rather than a chat surface, and badging
        // almost every orb would distinguish nothing.
        [Theory]
        [InlineData("agent:main:main", null)]
        [InlineData("agent:alexis:main", "direct")]
        [InlineData("agent:alexis:MAIN", "channel")]
        public void AMainKeyIsMainWhateverTheChatTypeSays(string key, string? chatType)
        {
            Assert.Equal(SessionKind.Main, OpenClawSessionKind.From(key, chatType));
        }

        // CB-270: a New Chat's session is the agent's own, reached through no
        // surface, so it is Main too. The key is structural, so a chatType on the
        // row (the gateway sends "direct" on these) must not override it.
        [Theory]
        [InlineData("agent:main:dashboard:9f1c2a3b-4d5e-4f60-8a7b-1c2d3e4f5a6b", null)]
        [InlineData("agent:alexis:dashboard:9f1c2a3b", "direct")]
        [InlineData("agent:alexis:DASHBOARD:9f1c2a3b", "channel")]
        public void ADashboardKeyIsMainWhateverTheChatTypeSays(string key, string? chatType)
        {
            Assert.Equal(SessionKind.Main, OpenClawSessionKind.From(key, chatType));
        }

        // A dashboard session is not a room member, whatever else it carries.
        [Fact]
        public void ADashboardKeyBelongsToNoRoom()
        {
            Assert.Null(OpenClawSessionKind.RoomOf("agent:main:dashboard:9f1c2a3b-4d5e-4f60-8a7b-1c2d3e4f5a6b"));
        }

        // --- From: chatType decides where the key is uninformative ---

        [Theory]
        [InlineData("direct")]
        [InlineData("dm")]
        [InlineData("im")]
        [InlineData("DIRECT")]
        public void TheGatewaysWordsForAPrivateMessageAllMeanDirect(string chatType)
        {
            Assert.Equal(
                SessionKind.Direct,
                OpenClawSessionKind.From("agent:main:discord:x:1", chatType));
        }

        [Theory]
        [InlineData("channel")]
        [InlineData("group")]
        [InlineData("guild")]
        [InlineData("Channel")]
        public void TheGatewaysWordsForARoomAllMeanChannel(string chatType)
        {
            Assert.Equal(
                SessionKind.Channel,
                OpenClawSessionKind.From("agent:main:discord:x:1", chatType));
        }

        // The key's fourth segment carries the same word when origin is absent.
        [Theory]
        [InlineData("agent:main:discord:direct:2467", SessionKind.Direct)]
        [InlineData("agent:main:discord:channel:1474", SessionKind.Channel)]
        [InlineData("agent:main:discord:group:1474", SessionKind.Channel)]
        public void TheFourthSegmentStandsInForAMissingChatType(string key, SessionKind want)
        {
            Assert.Equal(want, OpenClawSessionKind.From(key, null));
            Assert.Equal(want, OpenClawSessionKind.From(key, "   "));
        }

        // A supplied chatType beats the key's fourth segment — origin is the
        // gateway's own current word for the conversation.
        [Fact]
        public void AGivenChatTypeWinsOverTheKeysFourthSegment()
        {
            Assert.Equal(
                SessionKind.Channel,
                OpenClawSessionKind.From("agent:main:discord:direct:2467", "channel"));
        }

        // The safe direction, stated as a test: an unrecognised word is Unknown
        // rather than a guess at the commoner of the two. An unbadged orb says "I
        // don't know"; a wrong badge says something false about who can read the
        // conversation.
        [Theory]
        [InlineData("thread")]
        [InlineData("forum")]
        [InlineData("")]
        [InlineData(null)]
        public void AnUnrecognisedChatTypeIsNeverGuessedAt(string? chatType)
        {
            Assert.Equal(
                SessionKind.Unknown,
                OpenClawSessionKind.From("agent:main:discord", chatType));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-key")]
        [InlineData("agent:main")]
        [InlineData("notagent:main:main")]
        public void AKeyThatIsNotAGatewaySessionKeyIsUnknown(string? key)
        {
            Assert.Equal(SessionKind.Unknown, OpenClawSessionKind.From(key, null));
        }

        // --- RoomOf: a key stable across restarts and shared by every agent ---

        [Fact]
        public void ARoomIsTheSurfaceAndTheChannelId()
        {
            Assert.Equal(
                "discord:1900000000000000001",
                OpenClawSessionKind.RoomOf("agent:main:discord:channel:1900000000000000001"));
        }

        // Two agents in the same room must produce the same room key — that is
        // the entire purpose, and the agent segment is deliberately not part of
        // the answer.
        [Fact]
        public void TwoAgentsInOneRoomAgreeOnTheRoom()
        {
            Assert.Equal(
                OpenClawSessionKind.RoomOf("agent:main:discord:channel:1474"),
                OpenClawSessionKind.RoomOf("agent:ea-hope:discord:channel:1474"));
        }

        // A channel id containing colons keeps all of them: the id is everything
        // after the type segment, not just the next one.
        [Fact]
        public void AChannelIdMayContainColons()
        {
            Assert.Equal(
                "slack:T024:C7GB",
                OpenClawSessionKind.RoomOf("agent:main:slack:channel:T024:C7GB"));
        }

        // Null for anything that is not a channel, a DM included: two people
        // messaging privately is not a room other agents can join.
        [Theory]
        [InlineData("agent:main:discord:direct:2467")]
        [InlineData("agent:main:cron:2f6c")]
        [InlineData("agent:main:main")]
        [InlineData("agent:main:discord")]
        [InlineData("")]
        [InlineData(null)]
        public void AnythingThatIsNotAChannelHasNoRoom(string? key)
        {
            Assert.Null(OpenClawSessionKind.RoomOf(key));
        }

        // A real payload, quoted from the source's comment: a channel key whose
        // id is *another session key*. The gateway reports it as a group, but the
        // thing after "channel:" is not a channel — it carries no groupChannel,
        // and treating it as one split #arch into two rooms, the real one and a
        // second named after the raw id. Splitting a room in half is worse than
        // not grouping at all, which is the whole point of grouping.
        [Fact]
        public void AChannelKeyWhoseIdIsAnotherSessionKeyIsNotARoom()
        {
            Assert.Null(OpenClawSessionKind.RoomOf(
                "agent:main:discord:channel:agent:ea-hope:discord:channel:15389"));
        }

        [Fact]
        public void AChannelWithNoIdIsNotARoom()
        {
            Assert.Null(OpenClawSessionKind.RoomOf("agent:main:discord:channel:"));
            Assert.Null(OpenClawSessionKind.RoomOf("agent:main:discord:channel:   "));
        }
    }

    // Where the heartbeat lands, and what the heart looks like while it beats.
    //
    // The detection rule is deliberately narrow because the gateway does not
    // report heartbeats at all — the source's comment records that
    // `sessions.list` was read off a live gateway (84 sessions, 8 agents) with no
    // field naming one. So these cases pin "where does the heartbeat land",
    // which is documented and stable, and deliberately do not pretend to test
    // "was this turn a heartbeat", which nothing can answer.
    public class OpenClawHeartbeatTests
    {
        [Theory]
        [InlineData("agent:main:main")]
        [InlineData("agent:alexis:main")]
        [InlineData("agent:alexis:MAIN")]
        public void TheAgentsOwnMainSessionIsWhereTheHeartbeatLands(string key)
        {
            Assert.True(OpenClawHeartbeat.Is(key));
        }

        // Matched although it was never observed, because a gateway that ever
        // keys a heartbeat this way would otherwise draw an ordinary orb with no
        // hint of what keeps waking it.
        [Fact]
        public void ASurfaceSegmentThatSaysHeartbeatCounts()
        {
            Assert.True(OpenClawHeartbeat.Is("agent:main:heartbeat"));
        }

        // The documented under-mark, asserted so it stays a decision: a
        // heartbeat retargeted at a channel with the job's `session` override is
        // not marked, because it looks exactly like a channel session — which is
        // what it is.
        [Theory]
        [InlineData("agent:main:discord:channel:1474")]
        [InlineData("agent:main:discord:direct:2467")]
        [InlineData("agent:main:cron:2f6c")]
        [InlineData("")]
        [InlineData(null)]
        public void AnOrdinarySessionIsNotAHeartbeat(string? key)
        {
            Assert.False(OpenClawHeartbeat.Is(key));
        }

        // The gateway's own name for the job — `openclaw cron list --all` shows
        // it as "Heartbeat (<agent-id>)". Untested against a real payload by the
        // author's own admission; pinned here so the intent survives.
        [Theory]
        [InlineData("Heartbeat (main)")]
        [InlineData("heartbeat")]
        [InlineData("Cron: Heartbeat (main)")]
        [InlineData("  Cron:   heartbeat (alexis)  ")]
        public void ALabelNamingTheHeartbeatCounts(string label)
        {
            Assert.True(OpenClawHeartbeat.Is("agent:main:discord:channel:1", label));
        }

        // Prefix rather than substring, and this is the case that distinguishes
        // them: a job somebody wrote that merely mentions the word is not the
        // heartbeat.
        [Theory]
        [InlineData("Cron: nightly-heartbeat-followup")]
        [InlineData("Cron: sweep")]
        [InlineData("")]
        public void ALabelThatMerelyMentionsTheWordDoesNotCount(string label)
        {
            Assert.False(OpenClawHeartbeat.Is("agent:main:discord:channel:1", label));
        }

        // --- Beat: the shape of the pulse ---

        // Lub-dub: a big arch, a smaller one just after it, then flat for the
        // rest of the cycle. The rest is the point — the orb is already
        // breathing on a cosine, and a second smooth swell beside it would read
        // as one thing wobbling.
        [Fact]
        public void TheHeartIsAtRestForTheSecondHalfOfItsCycle()
        {
            foreach (var phase in new[] { 0.5, 0.6, 0.75, 0.9, 0.999 })
                Assert.Equal(0, OpenClawHeartbeat.Beat(phase), 6);
        }

        [Fact]
        public void TheFirstBeatIsTheStrongerOfTheTwo()
        {
            var first = OpenClawHeartbeat.Beat(0.11);    // top of the first arch
            var second = OpenClawHeartbeat.Beat(0.37);   // top of the second

            Assert.Equal(1.0, first, 6);
            Assert.Equal(0.62, second, 6);
            Assert.True(second < first);
        }

        [Fact]
        public void TheBeatStaysWithinZeroAndOne()
        {
            for (var i = 0; i <= 1000; i++)
            {
                var beat = OpenClawHeartbeat.Beat(i / 1000.0);
                Assert.InRange(beat, 0.0, 1.0);
            }
        }

        // Wrapped rather than clamped, so a caller can hand it elapsed time over
        // a period without doing the modulo itself. A negative phase wraps the
        // same way, which is what Math.Floor buys.
        [Theory]
        [InlineData(0.11, 1.11)]
        [InlineData(0.11, 7.11)]
        [InlineData(0.11, -0.89)]
        [InlineData(0.5, -2.5)]
        public void ThePhaseWrapsRatherThanClamping(double inCycle, double outside)
        {
            Assert.Equal(
                OpenClawHeartbeat.Beat(inCycle), OpenClawHeartbeat.Beat(outside), 6);
        }

        [Fact]
        public void TheRestingPulseIsAboutOneBeatASecond()
        {
            Assert.Equal(1100, OpenClawHeartbeat.PeriodMs);
        }
    
        // --- CB-270: the gateway's classification as a second opinion -----------

        private const string WhatsAppGroup = "agent:alpha:whatsapp:group:120000000000000001@g.us";
        private const string WhatsAppDm = "agent:alpha:whatsapp:direct:+15550000001";

        // The measured bug: chatType says direct on a row whose classification
        // says group.
        [Theory]
        [InlineData("group")]
        [InlineData("channel")]
        [InlineData("GROUP")]
        public void AClassificationOfGroupOrChannelOverridesAContradictingDirect(string classification) =>
            Assert.Equal(SessionKind.Channel, OpenClawSessionKind.From(WhatsAppGroup, "direct", classification));

        // The controls: a DM that agrees with itself stays Direct, and a group
        // that agrees with itself stays Channel.
        [Fact]
        public void AnAgreeingDmStaysDirectAndAnAgreeingGroupStaysChannel()
        {
            Assert.Equal(SessionKind.Direct, OpenClawSessionKind.From(WhatsAppDm, "direct", "direct"));
            Assert.Equal(SessionKind.Channel, OpenClawSessionKind.From(WhatsAppGroup, "group", "group"));
        }

        // The safe-direction pin: classification never turns a room private.
        [Theory]
        [InlineData("direct")]
        [InlineData("main")]
        [InlineData("dm")]
        public void AClassificationOfDirectNeverTurnsAChannelIntoDirect(string classification) =>
            Assert.Equal(SessionKind.Channel,
                OpenClawSessionKind.From(WhatsAppGroup, "channel", classification));

        // No classification, or one nobody has seen, is exactly the old answer.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("something-new")]
        public void NoUsableClassificationGivesTheTwoArgumentAnswer(string? classification)
        {
            Assert.Equal(OpenClawSessionKind.From(WhatsAppDm, "direct"),
                OpenClawSessionKind.From(WhatsAppDm, "direct", classification));
            Assert.Equal(OpenClawSessionKind.From(WhatsAppGroup, "direct"),
                OpenClawSessionKind.From(WhatsAppGroup, "direct", classification));
        }

        // A key that is structural is not overridden by anything: cron and main
        // are decided before chatType is consulted, so the third argument has no
        // Direct to correct.
        [Fact]
        public void AStructuralKeyIsNotTouchedByClassification()
        {
            Assert.Equal(SessionKind.Cron, OpenClawSessionKind.From("agent:a:cron:job1", "direct", "group"));
            Assert.Equal(SessionKind.Main, OpenClawSessionKind.From("agent:a:main", "direct", "group"));
        }

        // RoomOf: two agents in one WhatsApp group are one room, and a DM has none.
        [Fact]
        public void RoomOfAWhatsAppGroupIsTheSurfaceAndTheGroupIdForEveryAgent()
        {
            const string room = "whatsapp:120000000000000001@g.us";

            Assert.Equal(room, OpenClawSessionKind.RoomOf(WhatsAppGroup));
            Assert.Equal(room, OpenClawSessionKind.RoomOf("agent:beta:whatsapp:group:120000000000000001@g.us"));
            Assert.Null(OpenClawSessionKind.RoomOf(WhatsAppDm));
        }
}
}
