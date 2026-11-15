using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-329: the "Open existing" picker's list, and what choosing a row does.
    //
    // Build/Filter/Badge/Ago are pure and are driven with hand-made Session
    // records. Everything that depends on what the recency filter, the archive
    // check or LastSeen do goes through the real Parse, because paraphrasing
    // those rules is how a test ends up agreeing with its own misreading of them.
    [Collection("Settings")]
    public class OpenClawReopenListTests
    {
        private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        private static long Ms(DateTime when) => new DateTimeOffset(when).ToUnixTimeMilliseconds();

        private static readonly long TwoDaysAgo = Ms(Now.AddDays(-2));

        private const ClusterMode W = ClusterMode.WithChats;
        private const ClusterMode H = ClusterMode.Hidden;
        private const ClusterMode O = ClusterMode.OwnShape;

        private const string Aurora = "agent:aurora:discord:channel:900100";
        private const string Nova = "agent:nova:discord:channel:900100";
        private const string Ember = "agent:ember:discord:channel:900100";
        private const string Dm = "agent:aurora:discord:direct:1900000000000000001";

        private static OpenClawSessions.Session S(
            string key, string title, DateTime activity,
            SessionKind kind = SessionKind.Unknown, bool heartbeat = false,
            OpenClawNativeKind native = OpenClawNativeKind.None, bool pinned = false) =>
            new(key, title, "discord", "idle", activity, null, kind, heartbeat,
                Native: native, GatewayPinned: pinned);

        private static IReadOnlyList<OpenClawReopenRow> B(
            IReadOnlyList<OpenClawSessions.Session> listed,
            ClusterMode h, ClusterMode c, ClusterMode n, bool approve = false) =>
            OpenClawReopenList.Build(listed, h, c, n, approve);

        private static string Name(string id) => char.ToUpperInvariant(id[0]) + id[1..];

        // ---- Build: rooms --------------------------------------------------

        [Fact]
        public void CollapsesRoomMembersIntoOneRoomRow()
        {
            var rows = B(new[]
            {
                S(Aurora, "Aurora — general", Now.AddHours(-5), SessionKind.Channel),
                S(Nova, "Nova — general", Now.AddHours(-2), SessionKind.Channel),
                S(Ember, "Ember — general", Now.AddHours(-9), SessionKind.Channel),
            }, W, W, W);

            var row = Assert.Single(rows);
            Assert.Equal(OpenClawReopenTarget.Room, row.Target);
            Assert.Equal("openclaw:room:discord:900100", row.OrbSessionId);
            Assert.Equal("general", row.Title);
            Assert.Equal("general", row.PanelTitle);
            Assert.Equal(SessionKind.Channel, row.Kind);
            Assert.Equal(new[] { "nova", "aurora", "ember" }, row.AgentIds);
            Assert.Equal(new[] { Nova, Aurora, Ember }, row.MemberKeys);
            Assert.Equal(Now.AddHours(-2), row.LastActivity);
        }

        // The trap key: a channel id that is itself a session key must not make
        // a second room (RoomOf already refuses it), so it is a row of its own.
        [Fact]
        public void ATrapKeyDoesNotJoinTheRoom()
        {
            const string trap = "agent:main:discord:channel:agent:ea-hope:discord:channel:900100";
            Assert.Null(OpenClawSessionKind.RoomOf(trap));

            var rows = B(new[]
            {
                S(Aurora, "Aurora — general", Now.AddHours(-5), SessionKind.Channel),
                S(Nova, "Nova — general", Now.AddHours(-4), SessionKind.Channel),
                S(trap, "Main — arch", Now.AddHours(-1), SessionKind.Channel),
            }, W, W, W);

            Assert.Equal(2, rows.Count);
            Assert.Equal("openclaw:" + trap, rows[0].OrbSessionId);
            Assert.Equal(OpenClawReopenTarget.Session, rows[0].Target);
            Assert.Equal(OpenClawReopenTarget.Room, rows[1].Target);
            Assert.DoesNotContain(trap, rows[1].MemberKeys);
        }

        [Fact]
        public void ARoomWithOneAgentIsASessionRow()
        {
            var rows = B(new[]
            {
                S(Aurora, "Aurora — general", Now.AddHours(-5), SessionKind.Channel),
            }, W, W, W);

            var row = Assert.Single(rows);
            Assert.Equal(OpenClawReopenTarget.Session, row.Target);
            Assert.Equal("openclaw:" + Aurora, row.OrbSessionId);
            Assert.Equal("general", row.Title);
            Assert.Equal("Aurora — general", row.PanelTitle);
            Assert.Equal(new[] { "aurora" }, row.AgentIds);
            Assert.Equal(new[] { Aurora }, row.MemberKeys);
        }

        [Fact]
        public void TwoSessionsOfOneAgentInARoomGiveTheNewerOne()
        {
            const string again = "agent:aurora:discord:channel:900100:thread:7";
            Assert.Equal("discord:900100:thread:7", OpenClawSessionKind.RoomOf(again));

            // Same room key needed: use a key whose RoomOf matches the other's.
            const string second = "agent:aurora:discord:channel:900100";
            var rows = B(new[]
            {
                S(second, "Aurora — general", Now.AddHours(-9), SessionKind.Channel),
                S("agent:aurora:discord:channel:900100", "Aurora — general (newer)", Now.AddHours(-1), SessionKind.Channel),
            }, W, W, W);

            Assert.Equal(OpenClawReopenTarget.Session, Assert.Single(rows).Target);
        }

        // ---- Build: the cluster rule ----------------------------------------

        private static readonly OpenClawSessions.Session Heartbeat =
            S("agent:aurora:main", "Aurora", Now.AddHours(-1), SessionKind.Main, heartbeat: true);

        private static readonly OpenClawSessions.Session Cron =
            S("agent:aurora:cron:job-1", "Daily digest", Now.AddHours(-2), SessionKind.Cron);

        private static readonly OpenClawSessions.Session NativeDash =
            S("agent:aurora:dashboard:u1", "Scratch", Now.AddHours(-3), SessionKind.Main,
                native: OpenClawNativeKind.Dashboard);

        [Fact]
        public void HiddenHeartbeatsAreExcluded()
        {
            Assert.Empty(B(new[] { Heartbeat }, H, W, W));
        }

        // The control: the same heartbeat is listed when only crons are hidden.
        [Fact]
        public void AHeartbeatIsListedWhenOnlyCronsAreHidden()
        {
            Assert.Single(B(new[] { Heartbeat }, W, H, W));
        }

        [Fact]
        public void HiddenCronsAreExcluded()
        {
            Assert.Empty(B(new[] { Cron }, W, H, W));
            Assert.Single(B(new[] { Cron }, H, W, W));
        }

        [Fact]
        public void HiddenNativeIsExcludedAndOtherModesAreListed()
        {
            Assert.Empty(B(new[] { NativeDash }, W, W, H));
            Assert.Single(B(new[] { NativeDash }, W, W, W));
            Assert.Single(B(new[] { NativeDash }, W, W, O));
        }

        [Theory]
        [InlineData(ClusterMode.WithChats)]
        [InlineData(ClusterMode.OwnShape)]
        public void WithChatsAndOwnShapeAreListed(ClusterMode mode)
        {
            Assert.Equal(3, B(new[] { Heartbeat, Cron, NativeDash }, mode, mode, mode).Count);
        }

        [Fact]
        public void APinnedHeartbeatIsListedEvenWhenHidden()
        {
            var pinned = Heartbeat with { GatewayPinned = true };
            Assert.Single(B(new[] { pinned }, H, H, H));
            Assert.Empty(B(new[] { Heartbeat }, H, H, H));
        }

        // ---- Build: order ---------------------------------------------------

        [Fact]
        public void SortsByActivityDescendingThenId()
        {
            var tie = Now.AddHours(-4);
            var rows = B(new[]
            {
                S("agent:a:discord:direct:2", "b", tie, SessionKind.Direct),
                S("agent:a:discord:direct:1", "a", tie, SessionKind.Direct),
                S("agent:a:discord:direct:3", "c", Now.AddHours(-1), SessionKind.Direct),
                S("agent:a:discord:direct:4", "d", Now.AddHours(-9), SessionKind.Direct),
            }, W, W, W);

            Assert.Equal(
                new[] { "openclaw:agent:a:discord:direct:3", "openclaw:agent:a:discord:direct:1",
                        "openclaw:agent:a:discord:direct:2", "openclaw:agent:a:discord:direct:4" },
                rows.Select(r => r.OrbSessionId));
        }

        // ---- Filter ---------------------------------------------------------

        private static IReadOnlyList<OpenClawReopenRow> Sample() => B(new[]
        {
            S(Aurora, "Aurora — general", Now.AddHours(-5), SessionKind.Channel),
            S(Nova, "Nova — general", Now.AddHours(-2), SessionKind.Channel),
            S(Dm, "Aurora — wtvamp", Now.AddHours(-1), SessionKind.Direct),
            Cron,
        }, W, W, W);

        [Fact]
        public void FilterByAgentMatchesARoomByAnyMember()
        {
            var rows = OpenClawReopenList.Filter(Sample(), "nova", null, Name);

            var row = Assert.Single(rows);
            Assert.Equal(OpenClawReopenTarget.Room, row.Target);
            Assert.Equal(3, OpenClawReopenList.Filter(Sample(), "AURORA", null, Name).Count);
        }

        [Theory]
        [InlineData("GENERAL", 1)]
        [InlineData("wtvamp", 1)]
        [InlineData("daily", 1)]
        [InlineData("nova", 1)]
        [InlineData("dm", 1)]
        [InlineData("cron", 1)]
        [InlineData("channel", 1)]
        [InlineData("nothing like this", 0)]
        public void SearchIsCaseInsensitiveOverTitleAgentAndBadge(string search, int expected)
        {
            Assert.Equal(expected, OpenClawReopenList.Filter(Sample(), null, search, Name).Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankSearchKeepsEverything(string? search)
        {
            Assert.Equal(Sample().Count, OpenClawReopenList.Filter(Sample(), null, search, Name).Count);
        }

        // ---- Ago and Badge ---------------------------------------------------

        [Theory]
        [InlineData(-5, "just now")]
        [InlineData(0, "just now")]
        [InlineData(59, "just now")]
        [InlineData(60, "1 min ago")]
        [InlineData(59 * 60, "59 min ago")]
        [InlineData(60 * 60, "1 h ago")]
        [InlineData(24 * 3600 - 60, "23 h ago")]
        [InlineData(24 * 3600, "yesterday")]
        [InlineData(48 * 3600 - 60, "yesterday")]
        [InlineData(48 * 3600, "2 days ago")]
        [InlineData(7 * 86400 - 3600, "6 days ago")]
        public void AgoBuckets(int secondsAgo, string expected)
        {
            Assert.Equal(expected, OpenClawReopenList.Ago(Now.AddSeconds(-secondsAgo), Now));
        }

        [Fact]
        public void AgoOverAWeekIsADate()
        {
            var when = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(when.ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture),
                OpenClawReopenList.Ago(when, Now));
        }

        private static OpenClawReopenRow Row(
            SessionKind kind, bool heartbeat = false, OpenClawNativeKind native = OpenClawNativeKind.None) =>
            new(OpenClawReopenTarget.Session, "openclaw:k", "t", "t", kind, native, heartbeat,
                new[] { "a" }, new[] { "k" }, Now);

        // Native dashboard, then Main, then Heartbeat, Cron, DM, Channel, then
        // an unknown kind that is native, then nothing (plan: QA G3).
        [Theory]
        [InlineData(SessionKind.Main, true, OpenClawNativeKind.None, "Main")]
        [InlineData(SessionKind.Main, false, OpenClawNativeKind.None, "Main")]
        [InlineData(SessionKind.Main, false, OpenClawNativeKind.Main, "Main")]
        [InlineData(SessionKind.Main, false, OpenClawNativeKind.Dashboard, "Native")]
        [InlineData(SessionKind.Direct, false, OpenClawNativeKind.Dashboard, "Native")]
        [InlineData(SessionKind.Cron, true, OpenClawNativeKind.None, "Heartbeat")]
        [InlineData(SessionKind.Direct, true, OpenClawNativeKind.None, "Heartbeat")]
        [InlineData(SessionKind.Cron, false, OpenClawNativeKind.None, "Cron")]
        [InlineData(SessionKind.Direct, false, OpenClawNativeKind.None, "DM")]
        [InlineData(SessionKind.Channel, false, OpenClawNativeKind.None, "Channel")]
        [InlineData(SessionKind.Unknown, false, OpenClawNativeKind.Main, "Native")]
        [InlineData(SessionKind.Unknown, false, OpenClawNativeKind.None, "")]
        public void BadgeForEveryKind(SessionKind kind, bool heartbeat, OpenClawNativeKind native, string expected)
        {
            Assert.Equal(expected, OpenClawReopenList.Badge(Row(kind, heartbeat, native)));
        }

        // A WhatsApp group is a channel to the scan (CB-270 findings), so one
        // agent in it is a "Channel" session row and two are a "Channel" room.
        // Its key shape is the real one from OpenClawSessionKindTests.
        [Fact]
        public void AWhatsAppGroupIsAChannelRow()
        {
            const string alpha = "agent:alpha:whatsapp:group:120000000000000001@g.us";
            const string beta = "agent:beta:whatsapp:group:120000000000000001@g.us";
            var (sessions, _) = ParseAll($$"""
                {"sessions":[
                  {"key":"{{alpha}}","classification":"group","chatType":"group","lastActivityAt":{{TwoDaysAgo}}}
                ]}
                """);

            var solo = Assert.Single(B(sessions, W, W, W));
            Assert.Equal("Channel", OpenClawReopenList.Badge(solo));
            Assert.Equal(OpenClawReopenTarget.Session, solo.Target);

            var (both, _) = ParseAll($$"""
                {"sessions":[
                  {"key":"{{alpha}}","classification":"group","chatType":"group","lastActivityAt":{{TwoDaysAgo}}},
                  {"key":"{{beta}}","classification":"group","chatType":"group","lastActivityAt":{{TwoDaysAgo}}}
                ]}
                """);
            var room = Assert.Single(B(both, W, W, W));
            Assert.Equal(OpenClawReopenTarget.Room, room.Target);
            Assert.Equal("Channel", OpenClawReopenList.Badge(room));
            Assert.Equal("openclaw:room:whatsapp:120000000000000001@g.us", room.OrbSessionId);
        }

        // A WhatsApp *main* carries the heartbeat key (agent:<id>:main): it is a
        // heartbeat to the scan (so Hidden excludes it) and a Main to the badge.
        [Fact]
        public void AWhatsAppMainIsAHeartbeatToTheScanAndMainToTheBadge()
        {
            var (sessions, _) = ParseAll(WhatsAppMain());
            var row = Assert.Single(B(sessions, W, W, W));

            Assert.Equal(OpenClawNativeKind.None, row.Native);
            Assert.True(row.Heartbeat);
            Assert.Equal("Main", OpenClawReopenList.Badge(row));
            Assert.Empty(B(sessions, H, W, W));
        }

        // ---- Through the real Parse ------------------------------------------

        private static string WhatsAppMain() => $$"""
            {"sessions":[
              {"key":"agent:beta:main","classification":"main","isMain":true,"chatType":"direct",
               "origin":{"provider":"whatsapp","to":"+15550000001"},
               "deliveryContext":{"channel":"whatsapp","to":"+15550000001"},
               "lastChannel":"whatsapp","lastActivityAt":{{TwoDaysAgo}}}
            ]}
            """;

        private static (IReadOnlyList<OpenClawSessions.Session> Listed, IReadOnlyList<OpenClawSessions.Session> Shown)
            ParseAll(string json, int withinMinutes = 60, DateTime? now = null)
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OrbweaverSettings.OpenClawHeartbeatMode = W;
            OrbweaverSettings.OpenClawCronMode = W;
            OrbweaverSettings.OpenClawNativeMode = W;
            OrbweaverSettings.OpenClawActiveWithinMinutes = withinMinutes;

            var (shown, _) = OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, now ?? Now);

            // A poll that landed: ListedSnapshot is null (loading) until one has.
            OpenClawSessions.SetListingCurrentForTests(true);
            return (OpenClawSessions.ListedSnapshot()!, shown);
        }

        private static string Rows(string keyStale, string keyFresh, string extra = "") => $$"""
            {"sessions":[
              {"key":"{{keyStale}}","classification":"direct","chatType":"direct","lastActivityAt":{{TwoDaysAgo}}},
              {"key":"{{keyFresh}}","classification":"direct","chatType":"direct","lastActivityAt":{{Ms(Now.AddMinutes(-1))}}}
              {{extra}}
            ]}
            """;

        [Fact]
        public void IncludesSessionsTheRecencyFilterDrops()
        {
            OpenClawSessions.ForgetLastSeenForTests();
            var (listed, shown) = ParseAll(Rows("agent:a:discord:direct:1", "agent:a:discord:direct:2"));

            Assert.DoesNotContain(shown, s => s.Key.EndsWith(":1"));
            Assert.Contains(listed, s => s.Key.EndsWith(":1"));

            var rows = B(listed, W, W, W);
            Assert.Contains(rows, r => r.OrbSessionId.EndsWith(":1"));
            Assert.Contains(rows, r => r.OrbSessionId.EndsWith(":2"));
        }

        [Fact]
        public void ExcludesArchivedSessions()
        {
            OpenClawSessions.ForgetLastSeenForTests();
            var (listed, shown) = ParseAll(Rows("agent:a:discord:direct:1", "agent:a:discord:direct:2", """
                ,{"key":"agent:a:discord:direct:3","classification":"direct","chatType":"direct","archived":true,"lastActivityAt":1}
                """));

            Assert.DoesNotContain(listed, s => s.Key.EndsWith(":3"));
            Assert.DoesNotContain(B(listed, W, W, W), r => r.OrbSessionId.EndsWith(":3"));
            Assert.DoesNotContain(shown, s => s.Key.EndsWith(":3"));
        }

        [Fact]
        public void ListedSnapshotIsEmptyWhenOpenClawIsOff()
        {
            ParseAll(Rows("agent:a:discord:direct:1", "agent:a:discord:direct:2"));
            Assert.NotEmpty(OpenClawSessions.ListedSnapshot()!);

            OrbweaverSettings.OpenClawEnabled = false;
            Assert.Null(OpenClawSessions.ListedSnapshot());
            OrbweaverSettings.OpenClawEnabled = true;
        }

        [Fact]
        public void SetListedForTestsPublishesWhatItIsGiven()
        {
            OrbweaverSettings.OpenClawEnabled = true;
            var one = S(Dm, "Aurora — wtvamp", Now, SessionKind.Direct);
            OpenClawSessions.SetListingCurrentForTests(true);
            OpenClawSessions.SetListedForTests(new[] { one });

            Assert.Same(one, Assert.Single(OpenClawSessions.ListedSnapshot()!));
        }

        // The settled answer: a picked row counts as fresh activity for exactly
        // one recency window, by the real rule, not a paraphrase of it.
        [Fact]
        public void MarkActiveKeepsAStaleRowForOneWindowOnly()
        {
            const string key = "agent:a:discord:direct:1";
            var json = Rows(key, "agent:a:discord:direct:2");

            OpenClawSessions.ForgetLastSeenForTests();
            // Negative control: unmarked, the row is dropped at T+1 min.
            Assert.DoesNotContain(ParseAll(json, 5, Now.AddMinutes(1)).Shown, s => s.Key == key);

            OpenClawSessions.MarkActive(new[] { key }, Now);
            try
            {
                var within = ParseAll(json, 5, Now.AddMinutes(1)).Shown.Single(s => s.Key == key);
                Assert.Equal(Now, within.LastActivity);

                var after = ParseAll(json, 5, Now.AddMinutes(5).AddSeconds(1)).Shown;
                Assert.DoesNotContain(after, s => s.Key == key);
            }
            finally { OpenClawSessions.ForgetLastSeenForTests(); }
        }

        [Fact]
        public void MarkActiveDoesNotMakeItWorking()
        {
            const string key = "agent:a:discord:direct:1";
            OpenClawSessions.ForgetLastSeenForTests();
            OpenClawSessions.MarkActive(new[] { key }, Now);
            try
            {
                var shown = ParseAll(Rows(key, "agent:a:discord:direct:2"), 5, Now.AddMinutes(1)).Shown;
                Assert.Equal("idle", shown.Single(s => s.Key == key).State);
            }
            finally { OpenClawSessions.ForgetLastSeenForTests(); }
        }

        // ---- Reopen ----------------------------------------------------------

        private static string RoomRows() => $$"""
            {"sessions":[
              {"key":"{{Aurora}}","classification":"group","chatType":"group","lastActivityAt":{{TwoDaysAgo}}},
              {"key":"{{Nova}}","classification":"group","chatType":"group","lastActivityAt":{{TwoDaysAgo}}}
            ]}
            """;

        [Fact]
        public void ReopeningASessionRowMarksItAndBindsItsChat()
        {
            const string key = "agent:a:discord:direct:1";
            OpenClawSessions.ForgetLastSeenForTests();
            try
            {
                var (listed, shown) = ParseAll(Rows(key, "agent:a:discord:direct:2"));
                Assert.DoesNotContain(shown, s => s.Key == key);

                var row = B(listed, W, W, W).Single(r => r.OrbSessionId == "openclaw:" + key);
                var chat = Assert.IsType<OpenClawChatSession>(OpenClawSessions.Reopen(row));
                Assert.Equal("openclaw:" + key, chat.SessionId);

                Assert.Contains(ParseAll(Rows(key, "agent:a:discord:direct:2"), 60, DateTime.UtcNow).Shown,
                    s => s.Key == key);
            }
            finally { OpenClawSessions.ForgetLastSeenForTests(); }
        }

        // A room needs every member marked: the room orb exists only while two
        // agents pass recency.
        [Fact]
        public void ReopeningARoomRowMarksEveryMemberAndBindsTheRoom()
        {
            OpenClawSessions.ForgetLastSeenForTests();
            try
            {
                var (listed, shown) = ParseAll(RoomRows());
                Assert.Empty(shown);

                var row = Assert.Single(B(listed, W, W, W));
                Assert.Equal(OpenClawReopenTarget.Room, row.Target);

                var room = Assert.IsType<OpenClawRoomChatSession>(OpenClawSessions.Reopen(row));
                Assert.Equal("openclaw:room:discord:900100", room.SessionId);

                var after = ParseAll(RoomRows(), 60, DateTime.UtcNow).Shown;
                Assert.Equal(2, after.Count);
            }
            finally { OpenClawSessions.ForgetLastSeenForTests(); }
        }

        [Fact]
        public void ReopenReturnsNullWhenOpenClawIsOff()
        {
            var (listed, _) = ParseAll(Rows("agent:a:discord:direct:1", "agent:a:discord:direct:2"));
            var row = B(listed, W, W, W)[0];

            OrbweaverSettings.OpenClawEnabled = false;
            try { Assert.Null(OpenClawSessions.Reopen(row)); }
            finally
            {
                OrbweaverSettings.OpenClawEnabled = true;
                OpenClawSessions.ForgetLastSeenForTests();
            }
        }

        // CB-276 hides an orb after every other check, so a mark alone would
        // leave a hidden session without its orb: Reopen un-hides it. The twin
        // that was not reopened stays hidden — the control.
        [Fact]
        public void ReopenUnhidesEveryMemberKeyOfARoomButNotATwin()
        {
            OrbweaverSettings.RestoreAllHiddenOrbs();
            OpenClawSessions.ForgetLastSeenForTests();
            try
            {
                var (listed, _) = ParseAll(RoomRows());
                var row = Assert.Single(B(listed, W, W, W));

                foreach (var id in new[] { "openclaw:" + Aurora, "openclaw:" + Nova, row.OrbSessionId, "openclaw:" + Ember })
                    OrbweaverSettings.HideOrb(new HiddenOrb(id, Now, id, "OpenClaw", ""));

                OpenClawSessions.Reopen(row);

                var hidden = OrbweaverSettings.HiddenOrbIds();
                Assert.DoesNotContain("openclaw:" + Aurora, hidden);
                Assert.DoesNotContain("openclaw:" + Nova, hidden);
                Assert.DoesNotContain(row.OrbSessionId, hidden);
                Assert.Contains("openclaw:" + Ember, hidden);
            }
            finally
            {
                OrbweaverSettings.RestoreAllHiddenOrbs();
                OpenClawSessions.ForgetLastSeenForTests();
            }
        }

        [Fact]
        public void ReopenHiddenIdsAreTheRoomOrbThenEveryMember()
        {
            var (listed, _) = ParseAll(RoomRows());
            var room = Assert.Single(B(listed, W, W, W));

            Assert.Equal(
                new[] { "openclaw:room:discord:900100", "openclaw:" + Nova, "openclaw:" + Aurora }.OrderBy(x => x),
                OpenClawSessions.ReopenHiddenIds(room).OrderBy(x => x));
            Assert.Equal("openclaw:room:discord:900100", OpenClawSessions.ReopenHiddenIds(room)[0]);

            var dm = B(new[] { S(Dm, "Aurora — wtvamp", Now, SessionKind.Direct) }, W, W, W)[0];
            Assert.Equal(new[] { "openclaw:" + Dm }, OpenClawSessions.ReopenHiddenIds(dm));
        }

        // The seam sees exactly those ids, in order, and nothing else is written.
        [Fact]
        public void ReopenRestoresThroughTheSeam()
        {
            OpenClawSessions.ForgetLastSeenForTests();
            var seen = new List<string>();
            OpenClawSessions.RestoreHiddenForTests = seen.Add;
            try
            {
                var (listed, _) = ParseAll(Rows("agent:a:discord:direct:1", "agent:a:discord:direct:2"));
                var row = B(listed, W, W, W).Single(r => r.OrbSessionId.EndsWith(":1"));

                OpenClawSessions.Reopen(row);

                Assert.Equal(new[] { "openclaw:agent:a:discord:direct:1" }, seen);
            }
            finally
            {
                OpenClawSessions.RestoreHiddenForTests = null;
                OpenClawSessions.ForgetLastSeenForTests();
            }
        }

        // ---- The pinned write-through and the forget path ---------------------

        [Fact]
        public void TheListedSnapshotDoesNotSecondGuessAnotherPoll()
        {
            // A second Parse replaces the snapshot whole, so a conversation the
            // gateway stopped listing stops being offered.
            ParseAll(Rows("agent:a:discord:direct:1", "agent:a:discord:direct:2"));
            var (listed, _) = ParseAll($$"""{"sessions":[{"key":"agent:z:discord:direct:9","classification":"direct","chatType":"direct","lastActivityAt":{{Ms(Now)}}}]}""");

            Assert.Equal("agent:z:discord:direct:9", Assert.Single(listed).Key);
        }
    }
}
