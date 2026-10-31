using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests
{
    // CB-270 Stage 3: the orb's working state across the captured native runs,
    // replayed through OnEvent at the capture's own spacing. Nothing new in the
    // classifier — the open-panel half of CB-169's gap closes because the
    // events now arrive at all — so this pins it: working from the first
    // assistant delta, idle on the run's own end, reopened by the next run.
    [Collection("Settings")]
    public class OpenClawNativeRunStateTests
    {
        private const string FixtureKey = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        // The capture with its session key replaced by a unique one, since what is
        // running is recorded process-wide and two cases sharing a key would
        // answer each other's question.
        private static List<(string Name, JsonElement Payload, DateTime At)> Rows(string key) =>
            OpenClawNativeRunFixture.Events
                .Replace(FixtureKey, key)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(Json)
                .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("payload"),
                    DateTime.Parse(e.GetProperty("at").GetString()!, null,
                        System.Globalization.DateTimeStyles.RoundtripKind)))
                .ToList();

        private static string State(string key, DateTime now) => OpenClawSessions.StateFor(key, now);

        [Fact]
        public void TheOrbWorksFromTheFirstDeltaAndIdlesOnTheRunsOwnEnd()
        {
            var key = $"agent:main:dashboard:{Guid.NewGuid()}";
            var rows = Rows(key);

            var first = rows.First(r => r.Name == "agent");
            OpenClawSessions.OnEvent(first.Name, first.Payload, first.At);
            Assert.Equal("generating", State(key, first.At.AddMilliseconds(50)));

            var finalAt = rows.FindIndex(r => r.Name == "chat"
                && r.Payload.GetProperty("state").GetString() == "final");
            var final = rows[finalAt];
            foreach (var r in rows.Take(finalAt).Skip(1))
            {
                OpenClawSessions.OnEvent(r.Name, r.Payload, r.At);
            }

            OpenClawSessions.OnEvent(final.Name, final.Payload, final.At);
            Assert.Equal("idle", State(key, final.At.AddMilliseconds(50)));
        }

        [Fact]
        public void TheNextRunReopensIt()
        {
            var key = $"agent:main:dashboard:{Guid.NewGuid()}";

            foreach (var r in Rows(key)) OpenClawSessions.OnEvent(r.Name, r.Payload, r.At);

            // The whole capture has been delivered, ending on the second run's own
            // end, so the orb is idle again; and a start from a third run reopens it.
            var last = Rows(key).Last().At;
            Assert.Equal("idle", State(key, last.AddMilliseconds(50)));

            OpenClawSessions.OnEvent("agent",
                Json("{\"runId\":\"run-3\",\"stream\":\"lifecycle\",\"sessionKey\":\"" + key + "\",\"data\":{\"phase\":\"start\"}}"),
                last.AddSeconds(1));
            Assert.Equal("generating", State(key, last.AddSeconds(1.05)));
        }

        // A native session's events carry its plain key and a cron session's carry
        // it run-scoped; both are the same session.
        [Fact]
        public void APlainKeyAndARunScopedKeyReachTheSameSession()
        {
            var plain = $"agent:main:dashboard:{Guid.NewGuid()}";
            var scoped = $"agent:main:dashboard:{Guid.NewGuid()}";
            var now = DateTime.UtcNow;
            var body = ",\"stream\":\"assistant\",\"data\":{\"delta\":\"x\"}}";

            OpenClawSessions.OnEvent("agent", Json("{\"sessionKey\":\"" + plain + "\"" + body), now);
            OpenClawSessions.OnEvent("agent", Json("{\"sessionKey\":\"" + scoped + ":run:r1\"" + body), now);

            Assert.Equal("generating", State(plain, now.AddMilliseconds(50)));
            Assert.Equal("generating", State(scoped, now.AddMilliseconds(50)));
        }
    }
}
