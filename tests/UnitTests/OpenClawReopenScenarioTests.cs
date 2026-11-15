using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-329: the "Open existing" picker against the real Parse, the real settings
    // store and a counting gateway, one case per row of QA's acceptance matrix
    // (and the G-numbered gaps from her review). OpenClawReopenListTests holds the
    // pure builder's own cases; everything here goes the way a poll does:
    // sessions.list JSON, then Parse, then ListedSnapshot, then Build.
    [Collection("Settings")]
    public class OpenClawReopenScenarioTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        private static long Ms(DateTime when) => new DateTimeOffset(when).ToUnixTimeMilliseconds();
        private static readonly long TwoDaysAgo = Ms(Now.AddDays(-2));

        private const ClusterMode W = ClusterMode.WithChats;
        private const ClusterMode H = ClusterMode.Hidden;
        private const ClusterMode O = ClusterMode.OwnShape;

        private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
        private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
        private readonly ClusterMode _hb = OrbweaverSettings.OpenClawHeartbeatMode;
        private readonly ClusterMode _cron = OrbweaverSettings.OpenClawCronMode;
        private readonly ClusterMode _native = OrbweaverSettings.OpenClawNativeMode;
        private readonly int _within = OrbweaverSettings.OpenClawActiveWithinMinutes;

        public OpenClawReopenScenarioTests()
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OpenClawSessions.ForgetLastSeenForTests();
            OpenClawSessions.ForgetApprovalsForTests();
        }

        public void Dispose()
        {
            OpenClawSessions.SetGatewayForTests(null);
            OpenClawSessions.ForgetLastSeenForTests();
            OpenClawSessions.ForgetApprovalsForTests();
            OpenClawSessions.SetListingCurrentForTests(false);
            OpenClawSessions.SetLastConnectFailureForTests(null);
            OrbweaverSettings.RestoreAllHiddenOrbs();
            OrbweaverSettings.OpenClawEnabled = _enabled;
            OrbweaverSettings.OpenClawApproveEnabled = _approve;
            OrbweaverSettings.OpenClawHeartbeatMode = _hb;
            OrbweaverSettings.OpenClawCronMode = _cron;
            OrbweaverSettings.OpenClawNativeMode = _native;
            OrbweaverSettings.OpenClawActiveWithinMinutes = _within;
        }

        private static string Wrap(string rows) => "{\"sessions\":[" + rows + "]}";

        // One poll: settings, Parse (which fills the listed snapshot), and the flag a
        // landed poll sets. Returns what the orbs would show and what the picker has.
        private static (IReadOnlyList<OpenClawSessions.Session> Shown, IReadOnlyList<OpenClawSessions.Session> Listed)
            Poll(string rows, int within = 60, ClusterMode hb = W, ClusterMode cron = W, ClusterMode native = W,
                DateTime? now = null)
        {
            OrbweaverSettings.OpenClawHeartbeatMode = hb;
            OrbweaverSettings.OpenClawCronMode = cron;
            OrbweaverSettings.OpenClawNativeMode = native;
            OrbweaverSettings.OpenClawActiveWithinMinutes = within;

            var (shown, _) = OpenClawSessions.Parse(JsonDocument.Parse(Wrap(rows)).RootElement, now ?? Now);
            OpenClawSessions.SetListingCurrentForTests(true);
            return (shown, OpenClawSessions.ListedSnapshot()!);
        }

        private static IReadOnlyList<OpenClawReopenRow> Build(
            IReadOnlyList<OpenClawSessions.Session> listed,
            ClusterMode hb = W, ClusterMode cron = W, ClusterMode native = W, bool approve = false) =>
            OpenClawReopenList.Build(listed, hb, cron, native, approve);

        private static string Direct(int n, long at, string extra = "") =>
            $$"""{"key":"agent:a:discord:direct:{{n}}","classification":"direct","chatType":"direct","lastActivityAt":{{at}}{{extra}}}""";

        // Rows shaped like OpenClawSessionsParseTests.NativeFixture (real shapes,
        // invented values): a native main, a WhatsApp main, a dashboard, a Discord
        // DM and a cron.
        private static string NativeRows(long when) => $$"""
            {"key":"agent:alpha:main","classification":"main","isMain":true,"pinned":false,"lastActivityAt":{{when}}},
            {"key":"agent:beta:main","classification":"main","isMain":true,"chatType":"direct",
             "origin":{"provider":"whatsapp","to":"+15550000001"},
             "deliveryContext":{"channel":"whatsapp","to":"+15550000001"},
             "lastChannel":"whatsapp","lastActivityAt":{{when}}},
            {"key":"agent:alpha:dashboard:11111111-2222-3333-4444-555555555555","classification":"dashboard","lastActivityAt":{{when}}},
            {"key":"agent:alpha:discord:direct:1900000000000000001","classification":"direct","chatType":"direct",
             "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:1900000000000000001"},
             "lastChannel":"discord","lastActivityAt":{{when}}},
            {"key":"agent:alpha:cron:job-1","classification":"cron","label":"Cron: sweep","lastActivityAt":{{when}}}
            """;

        // ---- Matrix 1: the recency filter ------------------------------------

        [Fact]
        public void TheListHoldsEveryRowTheRecencyFilterDropsAndTheOrbPathStillDropsThem()
        {
            var (shown, listed) = Poll(NativeRows(TwoDaysAgo), within: 60);

            // The orb path: only the dashboard row survives (retention keeps it).
            Assert.Equal(OpenClawNativeKind.Dashboard, Assert.Single(shown).Native);

            var rows = Build(listed);
            Assert.Equal(5, rows.Count);
            Assert.Equal(
                new[] { "Main", "Main", "Native", "DM", "Cron" }.OrderBy(x => x),
                rows.Select(OpenClawReopenList.Badge).OrderBy(x => x));
        }

        // The control: with the window off, the orb path keeps everything and the
        // picker lists the same five, so the first test's gap is the filter's doing.
        [Fact]
        public void WithTheWindowOffTheOrbPathAndThePickerAgree()
        {
            var (shown, listed) = Poll(NativeRows(TwoDaysAgo), within: OrbweaverSettings.OpenClawActiveWithinAll);

            Assert.Equal(5, shown.Count);
            Assert.Equal(5, Build(listed).Count);
        }

        // ---- Matrix 2: archived ----------------------------------------------

        [Theory]
        [InlineData(",\"archived\":true", false)]
        [InlineData(",\"archived\":false", true)]
        [InlineData("", true)]
        public void ArchivedRowsAreExcludedEvenWhenPinned(string flag, bool listed)
        {
            var (_, snapshot) = Poll(Direct(1, TwoDaysAgo, ",\"pinned\":true" + flag));

            Assert.Equal(listed, Build(snapshot).Any(r => r.OrbSessionId.EndsWith(":1")));
        }

        // ---- Matrix 3 and 4: the cluster rule, all three clusters ---------------

        private const string Plain = """{"key":"agent:a:cron:plain1","classification":"cron","label":"Cron: sweep","lastActivityAt":1}""";
        private const string HbCron = """{"key":"agent:a:cron:hb1","classification":"cron","label":"Cron: Heartbeat (main)","lastActivityAt":1}""";
        private const string Main = """{"key":"agent:beta:main","classification":"main","isMain":true,"lastActivityAt":1}""";
        private const string Dash = """{"key":"agent:a:dashboard:plain2","classification":"dashboard","lastActivityAt":1}""";
        private const string AllFour = Plain + "," + HbCron + "," + Main + "," + Dash;

        private static HashSet<string> Listed(ClusterMode hb, ClusterMode cron, ClusterMode native, bool approve = false)
        {
            var (_, snapshot) = Poll(AllFour, within: 60, hb, cron, native);
            return Build(snapshot, hb, cron, native, approve).Select(r => r.OrbSessionId["openclaw:agent:".Length..]).ToHashSet();
        }

        [Fact]
        public void HiddenHeartbeatsTakeTheMainAndTheHeartbeatLabelledCronAndNothingElse()
        {
            Assert.Equal(new[] { "a:cron:plain1", "a:dashboard:plain2" }.OrderBy(x => x), Listed(H, W, W).OrderBy(x => x));
        }

        [Fact]
        public void HiddenCronsTakeOnlyThePlainCronNotTheHeartbeatLabelledOne()
        {
            Assert.Equal(
                new[] { "a:cron:hb1", "a:dashboard:plain2", "beta:main" }.OrderBy(x => x), Listed(W, H, W).OrderBy(x => x));
        }

        [Fact]
        public void HiddenNativeTakesOnlyTheDashboard()
        {
            Assert.Equal(
                new[] { "a:cron:hb1", "a:cron:plain1", "beta:main" }.OrderBy(x => x), Listed(W, W, H).OrderBy(x => x));
        }

        [Theory]
        [InlineData(ClusterMode.WithChats)]
        [InlineData(ClusterMode.OwnShape)]
        public void WithChatsAndOwnShapeListEveryRowWithItsBadge(ClusterMode mode)
        {
            var (_, snapshot) = Poll(AllFour, within: 60, mode, mode, mode);
            var rows = Build(snapshot, mode, mode, mode);

            Assert.Equal(4, rows.Count);
            Assert.Equal("Native", OpenClawReopenList.Badge(rows.Single(r => r.OrbSessionId.Contains("dashboard"))));
            Assert.Equal("Cron", OpenClawReopenList.Badge(rows.Single(r => r.OrbSessionId.EndsWith("plain1"))));
            Assert.Equal("Heartbeat", OpenClawReopenList.Badge(rows.Single(r => r.OrbSessionId.EndsWith("hb1"))));
            Assert.Equal("Main", OpenClawReopenList.Badge(rows.Single(r => r.OrbSessionId.EndsWith("beta:main"))));
        }

        [Fact]
        public void APinnedCronIsListedUnderHiddenAndItsUnpinnedTwinIsNot()
        {
            var (_, snapshot) = Poll("""
                {"key":"agent:a:cron:pinned1","classification":"cron","pinned":true,"lastActivityAt":1},
                {"key":"agent:a:cron:twin1","classification":"cron","lastActivityAt":1}
                """, within: 60, W, H, W);

            var rows = Build(snapshot, W, H, W);
            Assert.Equal("openclaw:agent:a:cron:pinned1", Assert.Single(rows).OrbSessionId);
        }

        // Kept for an approval: listed while approving is on, with the unkept
        // twin and the same row with approving off as the controls.
        [Fact]
        public void ARowKeptForAnApprovalIsListedWhileApprovingIsOn()
        {
            var waiting = new OpenClawSessions.Session(
                "agent:a:main", "A", "", "waiting", Now, null, SessionKind.Main, true);
            var idle = waiting with { Key = "agent:b:main", State = "idle" };

            var rows = Build(new[] { waiting, idle }, H, W, W, approve: true);

            Assert.Equal("openclaw:agent:a:main", Assert.Single(rows).OrbSessionId);
            Assert.Empty(Build(new[] { waiting }, H, W, W, approve: false));
        }

        // The property behind "the picker never hides something that has an orb,
        // and never lists one that cannot get one": for every cluster, with pinned
        // and waiting, under every mode triple, Build keeps a row iff the real
        // Parse (window off) keeps it in its snapshot.
        [Fact]
        public void ClusterHidesAgreesWithParse()
        {
            var clusters = new (string Key, string Json)[]
            {
                ("agent:a:discord:direct:1", """{"key":"agent:a:discord:direct:1","classification":"direct","chatType":"direct","lastActivityAt":1"""),
                ("agent:a:main", """{"key":"agent:a:main","classification":"main","isMain":true,"lastActivityAt":1"""),
                ("agent:a:cron:job-1", """{"key":"agent:a:cron:job-1","classification":"cron","label":"Cron: sweep","lastActivityAt":1"""),
                ("agent:a:dashboard:u1", """{"key":"agent:a:dashboard:u1","classification":"dashboard","lastActivityAt":1"""),
            };
            var modes = new[] { H, W, O };
            var checkedCases = 0;

            foreach (var (key, json) in clusters)
            foreach (var pinned in new[] { false, true })
            foreach (var waiting in new[] { false, true })
            foreach (var approve in new[] { false, true })
            foreach (var hb in modes)
            foreach (var cron in modes)
            foreach (var native in modes)
            {
                OpenClawSessions.ForgetApprovalsForTests();
                OrbweaverSettings.OpenClawApproveEnabled = true;
                if (waiting) RequestApproval(key);
                OrbweaverSettings.OpenClawApproveEnabled = approve;

                var row = json + (pinned ? ",\"pinned\":true}" : "}");
                var (shown, listed) = Poll(row, OrbweaverSettings.OpenClawActiveWithinAll, hb, cron, native);

                var orbExists = shown.Any(s => s.Key == key);
                var picker = Build(listed, hb, cron, native, approve).Any(r => r.OrbSessionId == "openclaw:" + key);

                Assert.True(orbExists == picker,
                    $"{key} pinned={pinned} waiting={waiting} approve={approve} modes={hb}/{cron}/{native}: orb={orbExists} picker={picker}");
                checkedCases++;
            }

            Assert.Equal(4 * 2 * 2 * 2 * 27, checkedCases);
        }

        private static void RequestApproval(string key) =>
            OpenClawSessions.OnEvent("exec.approval.requested", JsonDocument.Parse(
                "{\"approvalKind\":\"exec\",\"id\":\"a1\",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + key + "\"},"
                + "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddHours(1)) + "}").RootElement, Now);

        // ---- Matrix 5 / G3: rooms through the real Parse ------------------------

        private static string Channel(string agent, string channel, long at = 1) =>
            $$"""{"key":"agent:{{agent}}:discord:channel:{{channel}}","classification":"group","chatType":"group","lastActivityAt":{{at}}}""";

        [Fact]
        public void TheTrapKeyIsItsOwnRowAndMakesNoPhantomRoom()
        {
            const string trap = "agent:main:discord:channel:agent:ea-hope:discord:channel:1538900000000000001";
            var (_, listed) = Poll($$"""
                {"key":"{{trap}}","classification":"group","chatType":"group","lastActivityAt":1},
                {{Channel("aurora", "900")}},
                {{Channel("nova", "900")}}
                """);

            var rows = Build(listed);
            Assert.Contains(rows, r => r.OrbSessionId == "openclaw:" + trap && r.Target == OpenClawReopenTarget.Session);
            Assert.Single(rows, r => r.Target == OpenClawReopenTarget.Room);
            Assert.DoesNotContain(rows, r => r.OrbSessionId.StartsWith("openclaw:room:agent:", StringComparison.Ordinal));
        }

        [Fact]
        public void TwoDmsOfOneAgentAreTwoRowsAndTwoChannelsOfTwoAgentsAreTwoRooms()
        {
            var (_, dms) = Poll(Direct(1, 1) + "," + Direct(2, 2));
            Assert.Equal(2, Build(dms).Count);

            var (_, channels) = Poll(string.Join(",",
                Channel("aurora", "900"), Channel("nova", "900"), Channel("aurora", "901"), Channel("nova", "901")));
            var rows = Build(channels);

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(OpenClawReopenTarget.Room, r.Target));
            Assert.Equal(2, rows.Select(r => r.OrbSessionId).Distinct().Count());
        }

        // Members the recency filter dropped still count: the room row is built
        // from the unfiltered list.
        [Fact]
        public void RoomMembersTheRecencyFilterDroppedStillCount()
        {
            var (shown, listed) = Poll(string.Join(",",
                Channel("aurora", "900", TwoDaysAgo), Channel("nova", "900", TwoDaysAgo), Channel("ember", "900", TwoDaysAgo)));

            Assert.Empty(shown);
            var room = Assert.Single(Build(listed));
            Assert.Equal(3, room.MemberKeys.Count);
            Assert.Equal(3, room.AgentIds.Count);
        }

        // WhatsApp group with chatType "direct" but classification "group": Channel.
        // The control is a plain direct row, which stays Direct.
        [Fact]
        public void AWhatsAppGroupWithADirectChatTypeIsStillAChannel()
        {
            const string group = "agent:alpha:whatsapp:group:120000000000000001@g.us";
            var (_, listed) = Poll($$"""
                {"key":"{{group}}","classification":"group","chatType":"direct","lastActivityAt":1},
                {{Direct(1, 1)}}
                """);

            var rows = Build(listed);
            var g = rows.Single(r => r.OrbSessionId == "openclaw:" + group);
            Assert.Equal(SessionKind.Channel, g.Kind);
            Assert.Equal("Channel", OpenClawReopenList.Badge(g));
            Assert.Equal("DM", OpenClawReopenList.Badge(rows.Single(r => r.OrbSessionId.EndsWith(":1"))));
        }

        // ---- G3d / G3e: Main versus Heartbeat ------------------------------------

        [Fact]
        public void ADashboardIsNativeAndEveryKindOfMainIsMain()
        {
            var (_, listed) = Poll("""
                {"key":"agent:x:dashboard:u9","classification":"dashboard","lastActivityAt":1},
                {"key":"agent:alpha:main","classification":"main","isMain":true,"lastActivityAt":1},
                {"key":"agent:beta:main","classification":"main","isMain":true,"chatType":"direct",
                 "origin":{"provider":"whatsapp","to":"+15550000001"},
                 "deliveryContext":{"channel":"whatsapp","to":"+15550000001"},
                 "lastChannel":"whatsapp","lastActivityAt":1},
                {"key":"agent:z:main","lastActivityAt":1}
                """);

            var badges = Build(listed).ToDictionary(r => r.OrbSessionId, OpenClawReopenList.Badge);
            Assert.Equal("Native", badges["openclaw:agent:x:dashboard:u9"]);
            Assert.Equal("Main", badges["openclaw:agent:alpha:main"]);
            Assert.Equal("Main", badges["openclaw:agent:beta:main"]);
            Assert.Equal("Main", badges["openclaw:agent:z:main"]);
        }

        [Fact]
        public void AnAgentMainIsAHeartbeatAndBadgedMainHiddenUnlessPinned()
        {
            const string nova = """{"key":"agent:nova:main","lastActivityAt":1""";
            var (_, listed) = Poll(nova + "}", within: 60, hb: H);
            var shownRow = listed.Single();
            Assert.True(shownRow.Heartbeat);
            Assert.Empty(Build(listed, H, W, W));

            var (_, pinned) = Poll(nova + ",\"pinned\":true}", within: 60, hb: H);
            var row = Assert.Single(Build(pinned, H, W, W));
            Assert.Equal("Main", OpenClawReopenList.Badge(row));

            var (_, label) = Poll("""{"key":"agent:a:cron:hb2","classification":"cron","label":"Cron: Heartbeat (main)","lastActivityAt":1}""");
            Assert.Equal("Heartbeat", OpenClawReopenList.Badge(Assert.Single(Build(label))));
        }

        // ---- Matrix 7 / G9: the real activity signal -------------------------------

        [Fact]
        public void ASortFollowsTheLaterOfLastActivityAndUpdatedAt()
        {
            // A is stale by lastActivityAt and fresh by updatedAt; Activity takes the max.
            var a = $$"""{"key":"agent:a:discord:direct:1","classification":"direct","chatType":"direct","lastActivityAt":{{TwoDaysAgo}},"updatedAt":{{Ms(Now.AddMinutes(-1))}}}""";
            var b = Direct(2, Ms(Now.AddMinutes(-30)));

            var (_, listed) = Poll(a + "," + b);
            var rows = Build(listed);
            Assert.Equal(new[] { "openclaw:agent:a:discord:direct:1", "openclaw:agent:a:discord:direct:2" },
                rows.Select(r => r.OrbSessionId));

            // Reversed input, identical output.
            var (_, reversed) = Poll(b + "," + a);
            Assert.Equal(rows.Select(r => r.OrbSessionId), Build(reversed).Select(r => r.OrbSessionId));
        }

        [Fact]
        public void MarkingTheOldestRowMakesItFirst()
        {
            var rows = Direct(1, TwoDaysAgo) + "," + Direct(2, Ms(Now.AddMinutes(-30))) + "," + Direct(3, Ms(Now.AddHours(-3)));
            Assert.Equal("openclaw:agent:a:discord:direct:2", Build(Poll(rows).Listed)[0].OrbSessionId);

            OpenClawSessions.MarkActive(new[] { "agent:a:discord:direct:1" }, Now);
            Assert.Equal("openclaw:agent:a:discord:direct:1", Build(Poll(rows).Listed)[0].OrbSessionId);
        }

        // ---- Matrix 9 / G4: the snapshot, and no second sessions.list ---------------

        [Fact]
        public void ASecondParseReplacesTheListedSnapshotWholesale()
        {
            Poll(Direct(1, 1));
            var (_, second) = Poll(Direct(2, 1));

            Assert.Equal("agent:a:discord:direct:2", Assert.Single(second).Key);
        }

        [Fact]
        public void ClearListingLeavesTheSnapshotNullAndTheOrbsEmpty()
        {
            Poll(Direct(1, 1));
            OpenClawSessions.SetSnapshotForTests(OpenClawSessions.ListedSnapshot()!);
            Assert.NotNull(OpenClawSessions.ListedSnapshot());

            OpenClawSessions.ClearListing();

            Assert.Null(OpenClawSessions.ListedSnapshot());
            Assert.Empty(OpenClawSessions.Snapshot());
        }

        [Fact]
        public void ListedSnapshotIsNullUntilAPollHasLanded()
        {
            Poll(Direct(1, 1));
            OpenClawSessions.SetListingCurrentForTests(false);

            Assert.Null(OpenClawSessions.ListedSnapshot());
        }

        // ---- G5: the in-place edits ---------------------------------------------------

        [Fact]
        public void PinningAHiddenHeartbeatListsItBeforeTheNextPoll()
        {
            var (_, listed) = Poll("""
                {"key":"agent:a:main","classification":"main","isMain":true,"lastActivityAt":1},
                {"key":"agent:b:main","classification":"main","isMain":true,"lastActivityAt":1}
                """, hb: H);
            OpenClawSessions.SetSnapshotForTests(listed);
            Assert.Empty(Build(listed, H, W, W));

            OpenClawSessions.ApplyPinned("agent:a:main", true);

            // Only the pinned key is listed; its unpinned neighbour is not.
            Assert.Equal("openclaw:agent:a:main", Assert.Single(Build(OpenClawSessions.ListedSnapshot()!, H, W, W)).OrbSessionId);
            Assert.True(OpenClawSessions.Snapshot().Single(s => s.Key == "agent:a:main").GatewayPinned);
            Assert.False(OpenClawSessions.Snapshot().Single(s => s.Key == "agent:b:main").GatewayPinned);
        }

        [Fact]
        public void EndingAConversationRemovesItFromTheListBeforeTheNextPoll()
        {
            var (_, listed) = Poll(Direct(1, 1) + "," + Direct(2, 1));
            OpenClawSessions.SetSnapshotForTests(listed);

            OpenClawSessions.Forget("agent:a:discord:direct:1");

            Assert.Equal("agent:a:discord:direct:2", Assert.Single(OpenClawSessions.ListedSnapshot()!).Key);
            Assert.Equal("agent:a:discord:direct:2", Assert.Single(OpenClawSessions.Snapshot()).Key);
        }

        // ---- G1: the load states and the connect failure ---------------------------------

        [Theory]
        [InlineData(false, false, 0, 0, "Loading conversations…")]
        [InlineData(false, false, 5, 5, "Loading conversations…")]
        [InlineData(false, true, 0, 0, "Can't reach the OpenClaw gateway")]
        [InlineData(false, true, 5, 5, "Can't reach the OpenClaw gateway")]
        [InlineData(true, false, 0, 0, "No conversations")]
        [InlineData(true, true, 0, 0, "No conversations")]
        [InlineData(true, false, 5, 0, "No conversations match")]
        [InlineData(true, true, 5, 2, null)]
        [InlineData(true, false, 1, 1, null)]
        public void EmptyTextStates(bool loaded, bool timedOut, int total, int shown, string? expected)
        {
            Assert.Equal(expected, OpenClawReopenList.EmptyText(loaded, timedOut, total, shown));
        }

        [Fact]
        public void TheUnreachableDetailCarriesTheRealLocalNetworkHint()
        {
            var text = OpenClawGateway.ExplainConnectFailure(
                new SocketException((int)SocketError.HostUnreachable), onMacOS: true);

            var detail = OpenClawReopenList.UnreachableDetail(text);

            Assert.Equal(text, detail);
            Assert.Contains(OpenClawGateway.LocalNetworkHint, detail);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void WithNoKnownFailureTheDetailPointsAtSettings(string? failure)
        {
            Assert.Equal("Check Settings → OpenClaw.", OpenClawReopenList.UnreachableDetail(failure));
        }

        // The only road the Local Network hint takes to the picker: a failure stores
        // its detail, a success never stores anything, and a listing that lands
        // clears it so the picker stops blaming a connection that works.
        [Fact]
        public void AFailureIsStoredAndALandedListingClearsIt()
        {
            OpenClawSessions.SetLastConnectFailureForTests(null);
            OpenClawSessions.RecordConnectFailure(new OpenClawGateway.ConnectResult(OpenClawGateway.Outcome.Connected));
            Assert.Null(OpenClawSessions.LastConnectFailure()); // control: success stores nothing

            var hint = OpenClawGateway.ExplainConnectFailure(
                new SocketException((int)SocketError.HostUnreachable), onMacOS: true);
            OpenClawSessions.RecordConnectFailure(new OpenClawGateway.ConnectResult(OpenClawGateway.Outcome.Unreachable, hint));
            Assert.Equal(hint, OpenClawSessions.LastConnectFailure());

            OpenClawSessions.SetListingCurrentForTests(false);
            OpenClawSessions.ListingLanded();

            Assert.Null(OpenClawSessions.LastConnectFailure());
            Assert.NotNull(OpenClawSessions.ListedSnapshot());
        }

        // The control: a listing landing with no failure ever recorded leaves it null.
        [Fact]
        public void ALandedListingWithNoFailureRecordedLeavesItNull()
        {
            OpenClawSessions.SetLastConnectFailureForTests(null);
            OpenClawSessions.ListingLanded();

            Assert.Null(OpenClawSessions.LastConnectFailure());
        }

        // A key with no agent segment (hand-built; the gateway always sends one)
        // still gets a row, named by the key.
        [Fact]
        public void AKeyWithNoAgentSegmentIsItsOwnAgent()
        {
            var odd = new OpenClawSessions.Session("loose-key", "Loose", "", "idle", Now, null, SessionKind.Unknown, false);
            var row = Assert.Single(Build(new[] { odd }));

            Assert.Equal(new[] { "loose-key" }, row.AgentIds);
        }

        [Fact]
        public void ConnectFailuresAreRecordedAndOnlyFailuresAre()
        {
            OpenClawSessions.SetLastConnectFailureForTests(null);

            OpenClawSessions.RecordConnectFailure(new OpenClawGateway.ConnectResult(OpenClawGateway.Outcome.Connected, "ignored"));
            Assert.Null(OpenClawSessions.LastConnectFailure());

            OpenClawSessions.RecordConnectFailure(new OpenClawGateway.ConnectResult(OpenClawGateway.Outcome.Unreachable, "no route"));
            Assert.Equal("no route", OpenClawSessions.LastConnectFailure());

            OrbweaverSettings.OpenClawEnabled = false;
            Assert.Null(OpenClawSessions.LastConnectFailure());
        }

        // ---- Matrix 12 / G6: what Reopen does and does not do ------------------------------

        private static string SettingsDirHash()
        {
            var dir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR")!;
            var lines = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Path.GetRelativePath(dir, f) + ":" + new FileInfo(f).Length + ":"
                             + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
            return string.Join("\n", lines);
        }

        [Fact]
        public void AReopenedSessionSurvivesOneWindowIsNotPinnedAndWritesNothing()
        {
            const string key = "agent:a:discord:direct:1";
            var rows = Direct(1, TwoDaysAgo) + "," + Direct(2, TwoDaysAgo);
            var (_, listed) = Poll(rows, within: 5, now: Now);
            var dmRow = Build(listed).Single(r => r.OrbSessionId == "openclaw:" + key);
            var roomListed = Poll(Channel("aurora", "900", TwoDaysAgo) + "," + Channel("nova", "900", TwoDaysAgo), within: 5).Listed;
            var roomRow = Assert.Single(Build(roomListed));

            OpenClawSessions.SetGatewayForTests(null);
            var before = SettingsDirHash();
            var mark = DateTime.UtcNow;

            Assert.NotNull(OpenClawSessions.Reopen(dmRow));
            Assert.NotNull(OpenClawSessions.Reopen(roomRow));
            var (shownA, afterA) = Poll(rows, within: 5, now: mark.AddMinutes(1));
            var (shownB, _) = Poll(rows, within: 5, now: mark.AddMinutes(5).AddSeconds(2));

            // A: survives the next Parse, and only the reopened one.
            Assert.Contains(shownA, s => s.Key == key);
            Assert.DoesNotContain(shownA, s => s.Key.EndsWith(":2"));
            // Not pinned, and not kept regardless of age.
            Assert.All(afterA, s => Assert.False(s.GatewayPinned || s.KeptRegardlessOfAge));
            // B: dropped once the window has lapsed.
            Assert.DoesNotContain(shownB, s => s.Key == key);
            // C: nothing written.
            Assert.Equal(before, SettingsDirHash());

            // Negative control: the hash check can fail, so C means something.
            OrbweaverSettings.OpenClawActiveWithinMinutes = 37;
            Assert.NotEqual(before, SettingsDirHash());
        }

        // A key that left the gateway between the poll and the click: measured, not
        // assumed. ChatFor builds a chat for any key; with no gateway there is
        // nowhere to load history from, so it is an empty panel and not a throw.
        [Fact]
        public void ReopeningAKeyTheGatewayNoLongerListsDoesNotThrow()
        {
            OpenClawSessions.SetGatewayForTests(null);
            var gone = new OpenClawReopenRow(OpenClawReopenTarget.Session, "openclaw:agent:ghost:discord:direct:404",
                "ghost", "Ghost — ghost", SessionKind.Direct, OpenClawNativeKind.None, false,
                new[] { "ghost" }, new[] { "agent:ghost:discord:direct:404" }, Now);

            var chat = Assert.IsType<OpenClawChatSession>(OpenClawSessions.Reopen(gone));

            Assert.Equal("openclaw:agent:ghost:discord:direct:404", chat.SessionId);
            Assert.Empty(chat.History);
        }

        // With OpenClaw off Reopen does nothing at all: a hidden row stays hidden
        // (no settings write) and nothing is marked, so the next Parse still drops
        // it. The toggle's own write is the control that the hash check can fail.
        [Fact]
        public void ReopenWithOpenClawOffReturnsNullAndTouchesNothing()
        {
            const string key = "agent:a:discord:direct:1";
            var rows = Direct(1, TwoDaysAgo);
            var (_, listed) = Poll(rows, within: 5);
            var row = Build(listed).Single();
            OrbweaverSettings.RestoreAllHiddenOrbs();
            OrbweaverSettings.HideOrb(new HiddenOrb("openclaw:" + key, Now, key, "OpenClaw", ""));
            var before = SettingsDirHash();

            OrbweaverSettings.OpenClawEnabled = false;
            var afterToggle = SettingsDirHash();
            Assert.NotEqual(before, afterToggle);

            Assert.Null(OpenClawSessions.Reopen(row));

            Assert.Equal(afterToggle, SettingsDirHash());
            Assert.Contains("openclaw:" + key, OrbweaverSettings.HiddenOrbIds());

            OrbweaverSettings.OpenClawEnabled = true;
            var (shown, _) = Poll(rows, within: 5, now: DateTime.UtcNow.AddMinutes(1));
            Assert.DoesNotContain(shown, s => s.Key == key); // nothing was marked
        }

        // ---- CB-276: un-hiding ------------------------------------------------------------

        [Fact]
        public void ReopenUnhidesAHiddenStaleDmAndNotItsHiddenTwin()
        {
            const string a = "agent:a:discord:direct:1", b = "agent:a:discord:direct:2";
            OrbweaverSettings.RestoreAllHiddenOrbs();
            var rows = Direct(1, TwoDaysAgo) + "," + Direct(2, TwoDaysAgo);
            var (_, listed) = Poll(rows, within: 5);
            foreach (var key in new[] { a, b })
                OrbweaverSettings.HideOrb(new HiddenOrb("openclaw:" + key, Now, key, "OpenClaw", ""));

            OpenClawSessions.Reopen(Build(listed).Single(r => r.OrbSessionId == "openclaw:" + a));
            var (shown, _) = Poll(rows, within: 5, now: DateTime.UtcNow.AddMinutes(1));

            var hidden = OrbweaverSettings.HiddenOrbIds();
            Assert.DoesNotContain("openclaw:" + a, hidden);
            Assert.Contains("openclaw:" + b, hidden);
            Assert.Contains(shown, s => s.Key == a);
            Assert.DoesNotContain(shown, s => s.Key == b);
        }

        // The room was hidden through its room orb alone. Un-hiding members only
        // would leave nothing on screen, because HiddenOrbs.Effective hides a
        // member whose lead is hidden; so Reopen restores the room id too. A
        // different hidden room is the control and stays hidden.
        [Fact]
        public void ReopenBringsBackARoomHiddenThroughItsRoomOrbAndLeavesAnotherRoomHidden()
        {
            OrbweaverSettings.RestoreAllHiddenOrbs();
            var rows = string.Join(",", Channel("aurora", "900", TwoDaysAgo), Channel("nova", "900", TwoDaysAgo),
                Channel("aurora", "901", TwoDaysAgo), Channel("nova", "901", TwoDaysAgo));
            var (_, listed) = Poll(rows, within: 5);
            var rooms = Build(listed);
            var mine = rooms.Single(r => r.OrbSessionId == "openclaw:room:discord:900");
            var other = rooms.Single(r => r.OrbSessionId == "openclaw:room:discord:901");

            // Only the room orbs are hidden, as "Hide this orb" on a room does.
            OrbweaverSettings.HideOrb(new HiddenOrb(mine.OrbSessionId, Now, "900", "OpenClaw", ""));
            OrbweaverSettings.HideOrb(new HiddenOrb(other.OrbSessionId, Now, "901", "OpenClaw", ""));

            var members = mine.MemberKeys.Select(k => ("openclaw:" + k, (string?)mine.OrbSessionId)).ToList();
            Assert.Contains("openclaw:" + mine.MemberKeys[0],
                HiddenOrbs.Effective(OrbweaverSettings.HiddenOrbIds(), members)); // hidden through the lead

            OpenClawSessions.Reopen(mine);

            var effective = HiddenOrbs.Effective(OrbweaverSettings.HiddenOrbIds(), members);
            Assert.DoesNotContain(mine.OrbSessionId, effective);
            Assert.All(mine.MemberKeys, k => Assert.DoesNotContain("openclaw:" + k, effective));
            Assert.Contains(other.OrbSessionId, OrbweaverSettings.HiddenOrbIds());
        }

        // The manager arm of the restore: SessionManager.RestoreHiddenOrb removes the
        // entry (and schedules a scan, which a scratch manager with no Start() never runs).
        [Fact]
        public void RestoreHiddenViaAManagerRemovesTheEntry()
        {
            OrbweaverSettings.RestoreAllHiddenOrbs();
            var dir = Path.Combine(Path.GetTempPath(), "cb329-mgr-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                var manager = new SessionManager(dir);
                OrbweaverSettings.HideOrb(new HiddenOrb("openclaw:agent:a:main", Now, "A", "OpenClaw", ""));

                OpenClawSessions.RestoreHiddenVia(manager, "openclaw:agent:a:main");

                Assert.DoesNotContain("openclaw:agent:a:main", OrbweaverSettings.HiddenOrbIds());
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ReopenHiddenIdsForARoomListTheRoomFirstThenEveryMember()
        {
            var (_, listed) = Poll(Channel("aurora", "900") + "," + Channel("nova", "900"));
            var room = Assert.Single(Build(listed));

            var ids = OpenClawSessions.ReopenHiddenIds(room);

            Assert.Equal(room.OrbSessionId, ids[0]);
            Assert.Equal(room.MemberKeys.Select(k => "openclaw:" + k), ids.Skip(1));
        }
    }
}
