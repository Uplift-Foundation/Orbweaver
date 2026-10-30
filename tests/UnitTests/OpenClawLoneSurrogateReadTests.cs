using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

// A gateway string holding a lone surrogate escape ("\ud800") parses as JSON but
// makes JsonElement.GetString() throw InvalidOperationException. Every string a
// gateway can send is therefore a way to throw out of whatever reads it, and a
// throw out of the sessions poll or the event pump stalls every orb at once. The
// approvals parser was fixed for this; these pin the other three reads a hostile
// or merely corrupt gateway reaches: a sessions.list page, a chat.history page
// and a live event.
//
// Each case poisons ONE string value at a time, in every position of a fixture
// shaped like a real reply, and asserts the read neither throws nor loses the
// healthy rows beside it. Fixture values are invented.
[Collection("Settings")]
public class OpenClawLoneSurrogateReadTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static readonly Regex StringValue =
        new("(?<=[:\\[,]\\s*)\"((?:[^\"\\\\]|\\\\.)*)\"(?=\\s*[,}\\]])", RegexOptions.Compiled);

    // Every string VALUE (not name) in the text, each replaced in turn by the escape.
    private static IEnumerable<(int Index, string Poisoned)> Poisoned(string text)
    {
        var matches = StringValue.Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            yield return (i, text[..m.Index] + "\"\\ud800\"" + text[(m.Index + m.Length)..]);
        }
    }

    public OpenClawLoneSurrogateReadTests()
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        OpenClawSessions.Restart();
    }

    public void Dispose() => OpenClawSessions.Restart();

    // --- sessions.list ---

    private static string ListPage()
    {
        var when = Ms(Now.AddSeconds(-5));
        return $$"""
            {"sessions":[
              {"key":"agent:alpha:main","classification":"main","isMain":true,"label":"Main","displayName":"Alpha","title":"t",
               "kind":"direct","chatType":"direct","channel":"webchat","lastChannel":"whatsapp","model":"m","modelProvider":"p",
               "origin":{"provider":"whatsapp","label":"l","to":"+15550000001"},
               "deliveryContext":{"channel":"whatsapp","to":"+15550000001","accountId":"a"},
               "lastActivityAt":{{when}}},
              {"key":"agent:alpha:discord:direct:1900000000000000001","classification":"direct","chatType":"direct",
               "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:1900000000000000001"},
               "lastChannel":"discord","lastActivityAt":{{when}}},
              {"key":"agent:alpha:cron:job-1","classification":"cron","label":"Cron: sweep","status":"running","lastActivityAt":{{when}}}
            ]}
            """;
    }

    [Fact]
    public void ASessionsListPageSurvivesALoneSurrogateInAnyStringValue()
    {
        var thrown = new List<string>();
        var count = 0;

        foreach (var (i, text) in Poisoned(ListPage()))
        {
            count++;
            try
            {
                var (sessions, _) = OpenClawSessions.Parse(Json(text), Now);
                // A poisoned field costs at most its own row.
                Assert.True(sessions.Count >= 2, "position " + i + " lost healthy rows");
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                thrown.Add(i + ": " + e.GetType().Name);
            }
        }

        Assert.True(count > 15);
        Assert.Empty(thrown);
    }

    // --- chat.history ---

    private static string HistoryPage() => """
        [{"role":"user","content":"hello there","timestamp":1790000000000},
         {"role":"user","content":[{"type":"text","text":"second"}],"timestamp":1790000001000},
         {"role":"assistant","content":[{"type":"text","text":"reply"},
            {"type":"toolCall","id":"c1","name":"read","arguments":{"path":"/tmp/a.txt"}}],"timestamp":1790000002000,
          "provider":"p","model":"m","stopReason":"toolUse"},
         {"role":"toolResult","toolCallId":"c1","toolName":"read","content":[{"type":"text","text":"file text"}],"isError":false},
         {"role":"assistant","content":[{"type":"image","data":"aGVsbG8=","mimeType":"image/png"}]},
         {"role":"assistant","content":[{"type":"text","text":"MEDIA:/tmp/pic.png"}],"senderLabel":"Alpha"},
         {"role":"user","content":"x","senderIsOwner":false,"senderLabel":"Someone","sender":{"id":"u1","name":"n"}}]
        """;

    [Fact]
    public void AChatHistoryPageSurvivesALoneSurrogateInAnyStringValue()
    {
        var thrown = new List<string>();
        var count = 0;

        foreach (var (i, text) in Poisoned(HistoryPage()))
        {
            count++;
            try
            {
                var turns = OpenClawSessions.TurnsFromHistory(Json(text), "agent:alpha:main");
                Assert.NotEmpty(turns);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                thrown.Add(i + ": " + e.GetType().Name);
            }
        }

        Assert.True(count > 20);
        Assert.Empty(thrown);
    }

    // --- live events ---

    // Keys no other class uses: an event stamps the process-wide LastSeen, which
    // survives Restart on purpose, and would otherwise make some other class's
    // rows look recently active.
    private static readonly (string Name, string Payload)[] Events =
    {
        ("agent", "{\"sessionKey\":\"agent:surrogate-qa:main\",\"runId\":\"r1\",\"stream\":\"lifecycle\",\"data\":{\"phase\":\"start\"}}"),
        ("agent", "{\"sessionKey\":\"agent:surrogate-qa:main\",\"runId\":\"r1\",\"stream\":\"assistant\",\"data\":{\"text\":\"hi\",\"delta\":\"h\"}}"),
        ("agent", "{\"sessionKey\":\"agent:surrogate-qa:main\",\"runId\":\"r1\",\"stream\":\"tool\",\"data\":{\"phase\":\"start\",\"name\":\"read\",\"toolCallId\":\"c1\"}}"),
        ("chat", "{\"sessionKey\":\"agent:surrogate-qa:main\",\"runId\":\"r1\",\"state\":\"final\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}"),
        ("cron", "{\"sessionKey\":\"agent:surrogate-qa:cron:job-1\",\"action\":\"finished\",\"jobId\":\"job-1\",\"status\":\"ok\"}"),
        ("sessions.changed", "{\"sessionKey\":\"agent:surrogate-qa:main\",\"reason\":\"patch\"}"),
    };

    [Fact]
    public void ALiveEventSurvivesALoneSurrogateInAnyStringValue()
    {
        var thrown = new List<string>();
        var count = 0;

        foreach (var (name, payload) in Events)
            foreach (var (i, text) in Poisoned(payload))
            {
                count++;
                try { OpenClawSessions.OnEvent(name, Json(text), Now); }
                catch (Exception e) { thrown.Add(name + " " + i + ": " + e.GetType().Name); }
            }

        Assert.True(count > 15);
        Assert.Empty(thrown);
    }

    // --- the reader itself, and the hello-ok list ---

    [Fact]
    public void TheSafeReaderReturnsTheStringOrNullWhenItCannotBeRead()
    {
        Assert.Equal("ok", Json("\"ok\"").SafeString());
        Assert.Null(Json("\"\\ud800\"").SafeString());
        Assert.Null(Json("null").SafeString());
    }

    [Fact]
    public void AHelloOkMethodNameWithALoneSurrogateIsSkippedAndItsNeighboursKept()
    {
        var hello = Json("{\"features\":{\"methods\":[\"sessions.list\",\"\\ud800\",\"\",\"chat.history\"]}}");

        var methods = OpenClawGateway.ParseFeatures(hello);

        Assert.Equal(new[] { "chat.history", "sessions.list" }, methods.OrderBy(m => m, StringComparer.Ordinal));
    }

    // --- the other ways to read a string out of an element ---
    //
    // Measured by probe, not assumed: ToString() on a String element and
    // JsonProperty.Name throw on a lone surrogate; GetRawText() and a property's
    // ToString() do not. The two that throw are the only other reads of gateway or
    // peer text in the OpenClaw files (grep: one ToString on a hello-ok protocol,
    // one property name check on peer bodies).

    [Fact]
    public void ToStringOnAStringElementAndAPropertyNameThrowWhereRawTextDoesNot()
    {
        var poison = Json("{\"\\ud800\":\"\\ud800\"}");

        Assert.Throws<InvalidOperationException>(() => Json("\"\\ud800\"").ToString());
        Assert.Throws<InvalidOperationException>(() => poison.EnumerateObject().First().Name);
        Assert.NotNull(poison.GetRawText());
        Assert.NotNull(poison.EnumerateObject().First().ToString());
    }

    [Fact]
    public void TheSafeTextAndNameReadersReturnTheValueOrNull()
    {
        Assert.Equal("ok", Json("\"ok\"").SafeText());
        Assert.Null(Json("\"\\ud800\"").SafeText());
        Assert.Equal("3", Json("3").SafeText());
        Assert.Equal("a", Json("{\"a\":1}").EnumerateObject().First().SafeName());
        Assert.Null(Json("{\"\\ud800\":1}").EnumerateObject().First().SafeName());
    }

    // A peer body whose property NAME cannot be read is refused, not thrown on.
    [Fact]
    public void APeerBodyWithAnUnreadablePropertyNameIsRefused()
    {
        var pin = new string('a', 64);

        Assert.Null(OpenClawPeerIdentity.RequestFrom(Json("{\"gatewayPin\":\"" + pin + "\",\"agentIds\":[],\"\\ud800\":1}")));
        Assert.Null(OpenClawPeerIdentity.ResponseFrom(Json("{\"gatewayPin\":\"" + pin + "\",\"voices\":[],\"\\ud800\":1}")));
        Assert.NotNull(OpenClawPeerIdentity.RequestFrom(Json("{\"gatewayPin\":\"" + pin + "\",\"agentIds\":[\"alpha\"]}")));
    }

    // --- a tool call's arguments (QA found this one after the sweep above) ---
    //
    // A gateway or agent that cuts an emoji in half writes exactly a lone high
    // surrogate escape, and a tool's arguments are where free text from a command
    // lives. ChatTranscript.ToolSummary read each with GetString() and threw, which
    // aborted a whole history page and, on the live path, threw inside a job posted
    // to the UI thread.

    [Theory]
    [InlineData("command")]
    [InlineData("path")]
    [InlineData("file_path")]
    [InlineData("url")]
    public void AToolSummaryWithAnUnreadableArgumentIsJustTheToolName(string key)
    {
        var args = Json("{\"" + key + "\":\"echo \\ud83d\"}");

        Assert.Equal("· exec", ChatTranscript.ToolSummary("exec", args));
        Assert.Equal("· exec  ok", ChatTranscript.ToolSummary("exec", Json("{\"" + key + "\":\"ok\"}")));
    }

    [Fact]
    public void AHistoryPageWithAPoisonedToolArgumentStillDrawsItsOtherTurns()
    {
        var page = Json("""
            [{"role":"user","content":"before","timestamp":1790000000000},
             {"role":"assistant","content":[{"type":"toolcall","id":"t1","name":"exec","arguments":{"command":"echo \ud83d"}}],"timestamp":1790000001000},
             {"role":"assistant","content":[{"type":"text","text":"after"}],"timestamp":1790000002000}]
            """);

        var turns = OpenClawSessions.TurnsFromHistory(page, "agent:alpha:main");

        Assert.Contains(turns, t => t.Text == "before");
        Assert.Contains(turns, t => t.Text == "· exec");
        Assert.Contains(turns, t => t.Text == "after");
    }

    [Fact]
    public void ALiveToolEventWithAPoisonedArgumentDrawsALineAndTheNextEventStillArrives()
    {
        var session = new OpenClawChatSession("openclaw:agent:main:main", "agent:main:main", "main");

        session.OnAgentEvent("session.tool", Json(
            "{\"data\":{\"phase\":\"start\",\"name\":\"exec\",\"args\":{\"command\":\"echo \\ud83d\"}}}"));
        session.OnAgentEvent("agent", Json("{\"data\":{\"text\":\"next\"}}"));

        Assert.Equal(new[] { "· exec", "next" }, session.History.Select(t => t.Text));
    }

    // Whatever else throws inside a delivery, it is that event's own failure: it is
    // recorded, nothing escapes into the UI thread, and the next job still runs.
    [Fact]
    public void AJobThatThrowsOnTheUiThreadIsRecordedAndDoesNotStopTheNext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbweaver-crash-" + Guid.NewGuid().ToString("N"));
        using (CrashLog.ScopeForTests(dir))
        {
            var ran = new List<string>();

            OpenClawSessions.RunUiJobGuarded(() => throw new InvalidOperationException("poisoned payload"));
            OpenClawSessions.RunUiJobGuarded(() => ran.Add("next"));

            Assert.Equal(new[] { "next" }, ran);
            Assert.Contains("poisoned payload", File.ReadAllText(CrashLog.Path_));
        }
    }
}
