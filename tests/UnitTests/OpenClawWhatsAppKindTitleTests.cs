using System.Text.RegularExpressions;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-270 S5: what a WhatsApp conversation is called and what kind it wears.
    //
    // Re-measured read-only against OpenClaw 2026.10.5. The rows below keep the
    // real field structure and nothing else: every number is +1555000000N, every
    // group id is 12000000000000000N@g.us, and every name and subject is
    // invented, because this repository is public and the gateway is somebody's
    // real WhatsApp. By category, what the live WhatsApp rows carried:
    //   * a DM: classification, kind, chatType and peerKind all "direct";
    //     origin.label is the raw phone number; no subject, no groupChannel;
    //   * a group: classification, kind and peerKind "group", subject holds the
    //     group's name, origin.label is the raw <id>@g.us;
    //   * and one group row, of three, whose top-level chatType and origin
    //     chatType both say "direct" while classification, kind and peerKind
    //     all say group. That row is badged "direct message" by a rule that
    //     prefers chatType, which tells the user a room other people read is
    //     private.
    // These tests go through Parse, so they run against whatever KindFor and
    // TitleFor do today, and the first of them fails on develop.
    [Collection("Settings")]
    public class OpenClawWhatsAppKindTitleTests
    {
        private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        private static long JustNow => new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();

        private const string Dm = "agent:alpha:whatsapp:direct:+15550000001";
        private const string GroupA = "agent:alpha:whatsapp:group:120000000000000001@g.us";
        private const string GroupB = "agent:beta:whatsapp:group:120000000000000001@g.us";
        private const string Contradicting = "agent:gamma:whatsapp:group:120000000000000002@g.us";

        private static string Page() => $$"""
            {"sessions":[
              {"key":"{{Dm}}","classification":"direct","kind":"direct","chatType":"direct","peerKind":"direct",
               "origin":{"provider":"whatsapp","chatType":"direct","label":"+15550000001","from":"+15550000001",
                         "to":"+15550000001","surface":"whatsapp","nativeDirectUserId":"+15550000001"},
               "deliveryContext":{"channel":"whatsapp","to":"+15550000001"},"lastChannel":"whatsapp","lastTo":"+15550000001",
               "lastActivityAt":{{JustNow}}},
              {"key":"{{GroupA}}","classification":"group","kind":"group","chatType":"group","peerKind":"group",
               "subject":"Studio Crew","displayName":"Studio Crew",
               "origin":{"provider":"whatsapp","chatType":"group","label":"120000000000000001@g.us",
                         "from":"120000000000000001@g.us","to":"120000000000000001@g.us","surface":"whatsapp",
                         "accountId":"default"},
               "deliveryContext":{"channel":"whatsapp","to":"120000000000000001@g.us","accountId":"default"},
               "lastChannel":"whatsapp","lastTo":"120000000000000001@g.us","lastAccountId":"default",
               "lastActivityAt":{{JustNow}}},
              {"key":"{{GroupB}}","classification":"group","kind":"group","chatType":"group","peerKind":"group",
               "subject":"Studio Crew","displayName":"Studio Crew",
               "origin":{"provider":"whatsapp","chatType":"group","label":"120000000000000001@g.us",
                         "from":"120000000000000001@g.us","to":"120000000000000001@g.us","surface":"whatsapp",
                         "accountId":"default"},
               "deliveryContext":{"channel":"whatsapp","to":"120000000000000001@g.us","accountId":"default"},
               "lastChannel":"whatsapp","lastTo":"120000000000000001@g.us","lastAccountId":"default",
               "lastActivityAt":{{JustNow}}},
              {"key":"{{Contradicting}}","classification":"group","kind":"group","chatType":"direct","peerKind":"group",
               "subject":"Night Shift","displayName":"Night Shift",
               "origin":{"provider":"whatsapp","chatType":"direct","label":"120000000000000002@g.us",
                         "from":"120000000000000002@g.us","to":"120000000000000002@g.us","surface":"whatsapp",
                         "accountId":"default"},
               "deliveryContext":{"channel":"whatsapp","to":"120000000000000002@g.us","accountId":"default"},
               "lastChannel":"whatsapp","lastTo":"120000000000000002@g.us","lastAccountId":"default",
               "lastActivityAt":{{JustNow}}}
            ]}
            """;

        private static IReadOnlyList<OpenClawSessions.Session> Parse(string json)
        {
            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            OrbweaverSettings.OpenClawCronMode = ClusterMode.WithChats;
            OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
            OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;

            return OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, Now).Sessions;
        }

        private static OpenClawSessions.Session Row(string key) =>
            Parse(Page()).Single(s => s.Key == key);

        // --- kind ---

        // The bug. The gateway's own classification says group and the row's
        // chatType says direct; the badge must follow the safe answer.
        [Fact]
        public void AWhatsAppGroupWhoseChatTypeSaysDirectIsStillAChannel() =>
            Assert.Equal(SessionKind.Channel, Row(Contradicting).Kind);

        // The control: a group that agrees with itself, and a DM that does.
        [Fact]
        public void AnAgreeingGroupIsAChannelAndADmStaysDirect()
        {
            Assert.Equal(SessionKind.Channel, Row(GroupA).Kind);
            Assert.Equal(SessionKind.Direct, Row(Dm).Kind);
        }

        // --- title ---

        // The group's name is what the gateway calls it; the raw id is not.
        [Fact]
        public void AWhatsAppGroupIsTitledByItsSubject()
        {
            Assert.Equal("alpha — Studio Crew", Row(GroupA).Title);
            Assert.Equal("gamma — Night Shift", Row(Contradicting).Title);
        }

        // A DM has no name to show, and its origin.label is the peer's number,
        // so the title falls back to the surface word.
        [Fact]
        public void AWhatsAppDmIsTitledByTheSurfaceNotThePhoneNumber() =>
            Assert.Equal("alpha — whatsapp", Row(Dm).Title);

        // The privacy pin. A number or a group id in an orb title lands in every
        // screenshot and every screen share. No title built from any WhatsApp
        // row, DM or group, may carry five digits in a row.
        [Fact]
        public void NoWhatsAppTitleContainsARunOfFiveOrMoreDigits()
        {
            foreach (var session in Parse(Page()))
            {
                Assert.False(
                    Regex.IsMatch(session.Title, "[0-9]{5,}"),
                    $"a title carries an identifier: {session.Title}");
            }
        }

        // --- controls: Discord is untouched ---

        [Fact]
        public void ADiscordChannelWithAGroupChannelAndADiscordDmKeepTheirTitles()
        {
            var json = $$"""
                {"sessions":[
                  {"key":"agent:alpha:discord:channel:1900000000000000001","chatType":"channel",
                   "groupChannel":"#general","origin":{"provider":"discord","chatType":"channel",
                   "label":"#general channel id:1900000000000000001"},"lastActivityAt":{{JustNow}}},
                  {"key":"agent:alpha:discord:direct:1900000000000000002","chatType":"direct",
                   "origin":{"provider":"discord","chatType":"direct",
                   "label":"riverbend user id:1900000000000000002"},"lastActivityAt":{{JustNow}}}
                ]}
                """;

            var sessions = Parse(json);

            Assert.Equal("alpha — #general", sessions.Single(s => s.Key.Contains(":channel:")).Title);
            Assert.Equal("alpha — riverbend", sessions.Single(s => s.Key.Contains(":direct:")).Title);
        }

        // A label that merely contains a number is a name, not an identifier.
        [Fact]
        public void ALabelThatIsANumberPlusWordsIsNotTreatedAsAnIdentifier()
        {
            var json = $$"""
                {"sessions":[{"key":"agent:alpha:whatsapp:direct:+15550000003","chatType":"direct",
                  "origin":{"provider":"whatsapp","chatType":"direct","label":"Room 12345 Standup"},
                  "lastActivityAt":{{JustNow}}}]}
                """;

            Assert.Equal("alpha — Room 12345 Standup", Assert.Single(Parse(json)).Title);
        }

        // --- the room ---

        // Two agents in one WhatsApp group are one room: the room key is the
        // surface and the group id, with nothing in it that varies by agent.
        // A DM is nobody's room. These pass on develop; they pin it.
        [Fact]
        public void TwoAgentsInOneWhatsAppGroupShareARoomAndADmHasNone()
        {
            const string room = "whatsapp:120000000000000001@g.us";

            Assert.Equal(room, OpenClawSessionKind.RoomOf(GroupA));
            Assert.Equal(room, OpenClawSessionKind.RoomOf(GroupB));
            Assert.Null(OpenClawSessionKind.RoomOf(Dm));
        }
    
        // What counts as a bare identifier, and what does not.
        [Theory]
        [InlineData("+15550000001", true)]
        [InlineData("15550000001", true)]
        [InlineData(" +15550000001 ", true)]
        [InlineData("12345", true)]
        [InlineData("120000000000000001@g.us", true)]
        [InlineData("1234", false)]
        [InlineData("+1234", false)]
        [InlineData("Room 12345 Standup", false)]
        [InlineData("riverbend", false)]
        [InlineData("120000000000000001@example.com", false)]
        [InlineData("my@g.us", false)]
        [InlineData("", false)]
        public void OnlyANumberOrAGroupIdIsABareIdentifier(string label, bool expected) =>
            Assert.Equal(expected, OpenClawSessions.IsBareIdentifier(label));

        // A subject is a name only when it says something.
        [Theory]
        [InlineData("{\"subject\":\"Studio Crew\"}", "Studio Crew")]
        [InlineData("{\"subject\":\"  Studio Crew \"}", "Studio Crew")]
        [InlineData("{\"subject\":\"   \"}", null)]
        [InlineData("{\"subject\":7}", null)]
        [InlineData("{}", null)]
        [InlineData("{\"groupChannel\":\"#general\",\"subject\":\"Studio Crew\"}", "#general")]
        public void AGroupIsNamedByItsGroupChannelThenItsSubject(string row, string? expected) =>
            Assert.Equal(expected, OpenClawSessions.Group(JsonDocument.Parse(row).RootElement));

        // The identifier rule is not WhatsApp's alone: a Discord row whose
        // origin.label is only a bare id (live: slash-command and a channel row on
        // a few agents) had the digit run in its title, and now shows the surface
        // word, the same fallback a Discord row with no label already got. A
        // label with words in it is kept. Discord kinds, badges and attribution
        // are untouched; only titles that were a bare id change.
        [Fact]
        public void ADiscordRowWhoseLabelIsABareIdShowsTheSurfaceWordAndALabelWithWordsIsKept()
        {
            var json = $$"""
                {"sessions":[
                  {"key":"agent:alpha:discord:slash:1900000000000000001","chatType":"direct",
                   "origin":{"provider":"discord","chatType":"direct","label":"1900000000000000001"},
                   "lastActivityAt":{{JustNow}}},
                  {"key":"agent:alpha:discord:channel:1900000000000000002","chatType":"channel",
                   "origin":{"provider":"discord","chatType":"channel","label":"1900000000000000002"},
                   "lastActivityAt":{{JustNow}}},
                  {"key":"agent:alpha:discord:direct:1900000000000000003","chatType":"direct",
                   "origin":{"provider":"discord","chatType":"direct","label":"riverbend user id:1900000000000000003"},
                   "lastActivityAt":{{JustNow}}}
                ]}
                """;

            var sessions = Parse(json);

            Assert.Equal("alpha — discord", sessions.Single(s => s.Key.Contains(":slash:")).Title);
            Assert.Equal("alpha — discord", sessions.Single(s => s.Key.Contains(":channel:")).Title);
            Assert.Equal("alpha — riverbend", sessions.Single(s => s.Key.Contains(":direct:")).Title);

            // Kinds are exactly what they were.
            Assert.Equal(SessionKind.Channel, sessions.Single(s => s.Key.Contains(":channel:")).Kind);
            Assert.Equal(SessionKind.Direct, sessions.Single(s => s.Key.Contains(":direct:")).Kind);
        }
}
}
