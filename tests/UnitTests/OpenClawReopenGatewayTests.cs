using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-329: what the picker costs the gateway, counted on a real OpenClawGateway
    // over the in-memory socket. G4a: opening the dialog and listing rows writes
    // nothing, so there is no second sessions.list. G7: a real session opened
    // through Reopen with replying off sends nothing when someone types, and
    // that is measured on the real session, not a fake.
    [Collection("Settings")]
    public class OpenClawReopenGatewayTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
        private readonly bool _reply = OrbweaverSettings.OpenClawReplyEnabled;
        private readonly int _within = OrbweaverSettings.OpenClawActiveWithinMinutes;

        public OpenClawReopenGatewayTests()
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OpenClawSessions.ForgetLastSeenForTests();
        }

        public void Dispose()
        {
            OpenClawSessions.SetGatewayForTests(null);
            OpenClawSessions.ForgetLastSeenForTests();
            OpenClawSessions.SetListingCurrentForTests(false);
            OrbweaverSettings.OpenClawEnabled = _enabled;
            OrbweaverSettings.OpenClawReplyEnabled = _reply;
            OrbweaverSettings.OpenClawActiveWithinMinutes = _within;
        }

        private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync()
        {
            var socket = new FakeGatewaySocket();
            socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
            socket.OnRequest = r => r.Method == "connect"
                ? FakeGatewaySocket.Ok(r.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.write" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : r.Method == "sessions.create"
                    ? FakeGatewaySocket.Ok(r.Id, new { key = "agent:a:dashboard:new1" })
                    : FakeGatewaySocket.Ok(r.Id, new { });
            var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
                (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            await gateway.ConnectAsync(null, CancellationToken.None);
            OpenClawSessions.SetGatewayForTests(gateway);
            return (socket, gateway);
        }

        private static (IReadOnlyList<OpenClawSessions.Session> Shown, IReadOnlyList<OpenClawReopenRow> Rows) Poll(string rows)
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = 60;
            var (shown, _) = OpenClawSessions.Parse(JsonDocument.Parse("{\"sessions\":[" + rows + "]}").RootElement, Now);
            OpenClawSessions.SetListingCurrentForTests(true);
            return (shown, OpenClawReopenList.Build(OpenClawSessions.ListedSnapshot()!,
                ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats, false));
        }

        private static long Stale => new DateTimeOffset(Now.AddDays(-2)).ToUnixTimeMilliseconds();

        private const string Dash = "agent:a:dashboard:u1";

        private string DashRow() =>
            $$"""{"key":"{{Dash}}","classification":"dashboard","label":"Scratch","lastActivityAt":{{Stale}}}""";

        private static string Chan(string agent) =>
            $$"""{"key":"agent:{{agent}}:discord:channel:900","classification":"group","chatType":"group","lastActivityAt":{{Stale}}}""";

        private static int Count(FakeGatewaySocket s, string method) =>
            s.Requests.Count(r => r.Method == method);

        private static bool AnySend(FakeGatewaySocket s) =>
            s.Requests.Any(r => r.Method.Contains("send", StringComparison.OrdinalIgnoreCase));

        [Fact]
        public async Task ListingAndBuildingWriteNoFramesAndOnlyAStartedConversationDoes()
        {
            var (socket, gateway) = await ConnectedAsync();
            using (gateway)
            {
                var (_, rows) = Poll(DashRow());
                var afterConnect = socket.Requests.Count;

                for (var i = 0; i < 5; i++)
                {
                    var listed = OpenClawSessions.ListedSnapshot();
                    Assert.NotNull(listed);
                    Assert.Single(OpenClawReopenList.Build(listed!, ClusterMode.WithChats, ClusterMode.WithChats,
                        ClusterMode.WithChats, false));
                }

                Assert.Equal(afterConnect, socket.Requests.Count);
                Assert.Equal(0, Count(socket, "sessions.list"));

                // Choosing a row asks for that conversation's history and nothing like a list.
                OpenClawSessions.Reopen(rows[0]);
                Assert.Equal(0, Count(socket, "sessions.list"));

                // The control: the counter does count a real call.
                var (session, failure) = await OpenClawSessions.StartConversationAsync("a", CancellationToken.None);
                Assert.Null(failure);
                Assert.NotNull(session);
                Assert.Equal(1, Count(socket, "sessions.create"));
            }
        }

        [Fact]
        public async Task ASessionOpenedWithReplyingOffSendsNothingAndWithItOnSendsOneFrame()
        {
            var (socket, gateway) = await ConnectedAsync();
            using (gateway)
            {
                var (_, rows) = Poll(DashRow());

                OrbweaverSettings.OpenClawReplyEnabled = false;
                var chat = Assert.IsType<OpenClawChatSession>(OpenClawSessions.Reopen(rows[0]));
                var outcome = await chat.SendAsync("hi");

                Assert.Equal(ChatSendOutcome.Failed, outcome);
                Assert.Contains(chat.History, t => t.Role == ChatRole.System && t.Text.Contains("Replying is off"));
                Assert.False(AnySend(socket), string.Join(",", socket.Requests.Select(r => r.Method)));

                // The control: the same session with replying on does send.
                OrbweaverSettings.OpenClawReplyEnabled = true;
                await chat.SendAsync("hi again");
                Assert.True(AnySend(socket), "replying on should have written a send frame: "
                    + string.Join(",", socket.Requests.Select(r => r.Method)));
            }
        }

        // A2 for rooms: the room session Reopen returns gates the same way. (If it
        // did not, that would be a pre-existing defect to report, not to fix here.)
        [Fact]
        public async Task ARoomOpenedWithReplyingOffSendsNothing()
        {
            var (socket, gateway) = await ConnectedAsync();
            using (gateway)
            {
                var (_, rows) = Poll(Chan("aurora") + "," + Chan("nova"));
                var room = Assert.IsType<OpenClawRoomChatSession>(OpenClawSessions.Reopen(Assert.Single(rows)));

                OrbweaverSettings.OpenClawReplyEnabled = false;
                var outcome = await room.SendAsync("hi");

                Assert.Equal(ChatSendOutcome.Failed, outcome);
                Assert.False(AnySend(socket), string.Join(",", socket.Requests.Select(r => r.Method)));
            }
        }
    }
}
