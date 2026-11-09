using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>CB-316: the phone's watch / unwatch / input requests, every refusal and the tail, against fakes.</summary>
public class PhoneServiceTests
{
    private sealed class Rig
    {
        internal readonly List<(string Phone, string Cls, string? Ck, string Plain)> Sent = new();
        internal readonly Dictionary<string, PhoneService.Session> Book = new();
        internal List<PhoneFrames.Line>? Rows = [];
        internal bool CanType = true;
        internal (bool Typed, string? Refusal) Typing = (true, null);
        internal bool Throw;
        internal readonly List<string> Typed = new();
        internal PhoneService Service;

        internal Rig()
        {
            Book["s1"] = new("s1", new SessionStatus(), "idle");
            Service = new PhoneService(new PhoneService.Seams(
                id => Book.GetValueOrDefault(id),
                _ => Rows,
                _ => CanType,
                (_, text) => { if (Throw) throw new InvalidOperationException("x"); Typed.Add(text); return Task.FromResult(Typing); },
                (phone, cls, ck, plain) => { Sent.Add((phone, cls, ck, plain)); return true; },
                _ => { }));
        }

        internal Task Ask(string json, string id = "req1") => Service.OnRequestAsync("ph_1", id, JsonDocument.Parse(json).RootElement);
        internal string[] Plains => Sent.Select(s => s.Plain).ToArray();
    }

    private static List<PhoneFrames.Line> Rows(int n) =>
        Enumerable.Range(0, n).Select(i => new PhoneFrames.Line(i % 2 == 0 ? "user" : "assistant", $"row {i}", 100 + i)).ToList();

    [Fact]
    public async Task WatchAnswersOkThenTheLastTwoHundredLines()
    {
        var r = new Rig { Rows = Rows(230) };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        Assert.Equal("""{"t":"ok","ref":"req1","code":"watching","session_id":"s1","total":230}""", r.Plains[0]);
        Assert.Equal("action", r.Sent[0].Cls);
        Assert.All(r.Sent.Skip(1), s => { Assert.Equal("chat", s.Cls); Assert.Equal("s1", s.Ck); });
        Assert.StartsWith("""{"t":"window","session_id":"s1","from_line":30,"total":230,"live":false,"lines":[""", r.Plains[1]);
        Assert.Equal(1, r.Service.WatchCount);
    }

    [Fact]
    public async Task WatchWithNoTranscriptYetSaysTotalZeroAndSendsNoWindow()
    {
        var r = new Rig { Rows = null };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        Assert.Equal(["""{"t":"ok","ref":"req1","code":"watching","session_id":"s1","total":0}"""], r.Plains);
    }

    [Fact]
    public async Task APageSendsOnlyWindowsAndLeavesTheSubscriptionAlone()
    {
        var r = new Rig { Rows = Rows(230) };
        await r.Ask("""{"t":"watch","session_id":"s1","before":30}""");
        Assert.Single(r.Sent);
        Assert.StartsWith("""{"t":"window","session_id":"s1","from_line":0,"total":230,"live":false""", r.Plains[0]);
        Assert.Equal(0, r.Service.WatchCount);
    }

    [Fact]
    public async Task APagePastTheEndIsClamped()
    {
        var r = new Rig { Rows = Rows(5) };
        await r.Ask("""{"t":"watch","session_id":"s1","before":999}""");
        Assert.StartsWith("""{"t":"window","session_id":"s1","from_line":0,"total":5,"live":false""", r.Plains[0]);
    }

    [Theory]
    [InlineData("""{"t":"watch","session_id":"s1","before":0}""")]
    [InlineData("""{"t":"watch","session_id":"s1","before":"x"}""")]
    [InlineData("""{"t":"watch"}""")]
    [InlineData("""{"t":"watch","session_id":""}""")]
    public async Task ABadWatchIsBadFrame(string frame)
    {
        var r = new Rig();
        await r.Ask(frame);
        Assert.Contains("\"code\":\"bad_frame\"", Assert.Single(r.Plains));
        Assert.Equal(0, r.Service.WatchCount);
    }

    [Fact]
    public async Task AnUnknownSessionIsRefusedByWatchUnwatchAndInput()
    {
        var r = new Rig();
        foreach (var t in new[] { "watch", "unwatch", "input" })
            await r.Ask($$"""{"t":"{{t}}","session_id":"gone","text":"hi"}""");
        Assert.All(r.Plains, p => Assert.Contains("\"code\":\"unknown_session\",\"session_id\":\"gone\"", p));
        Assert.Equal(3, r.Plains.Length);
    }

    [Fact]
    public async Task UnwatchStopsTheStreamAndIsIdempotent()
    {
        var r = new Rig { Rows = Rows(3) };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        r.Sent.Clear();
        await r.Ask("""{"t":"unwatch","session_id":"s1"}""", "req2");
        await r.Ask("""{"t":"unwatch","session_id":"s1"}""", "req3");
        Assert.Equal(["""{"t":"ok","ref":"req2","code":"unwatched","session_id":"s1"}""", """{"t":"ok","ref":"req3","code":"unwatched","session_id":"s1"}"""], r.Plains);
        Assert.Equal(0, r.Service.WatchCount);
        r.Rows!.Add(new("user", "more", 1));
        r.Service.Tick();
        Assert.Equal(2, r.Sent.Count);
    }

    [Fact]
    public async Task TheTailSendsAppendedRowsLiveAndNothingWhenQuiet()
    {
        var r = new Rig { Rows = Rows(3) };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        r.Sent.Clear();
        r.Service.Tick();
        Assert.Empty(r.Sent);
        r.Rows!.AddRange([new("user", "four", 9), new("assistant", "five", 10)]);
        r.Service.Tick();
        Assert.Equal("""{"t":"window","session_id":"s1","from_line":3,"total":5,"live":true,"lines":[{"role":"user","text":"four","at":9},{"role":"assistant","text":"five","at":10}]}""", Assert.Single(r.Plains));
        r.Service.Tick();
        Assert.Single(r.Sent);
    }

    [Fact]
    public async Task AShrunkTranscriptIsAResetBackfill()
    {
        var r = new Rig { Rows = Rows(5) };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        r.Sent.Clear();
        r.Rows = Rows(2);
        r.Service.Tick();
        Assert.StartsWith("""{"t":"window","session_id":"s1","from_line":0,"total":2,"live":false,"reset":true""", Assert.Single(r.Plains));
        r.Rows = [];
        r.Sent.Clear();
        r.Service.Tick();
        Assert.Equal("""{"t":"window","session_id":"s1","from_line":0,"total":0,"live":false,"reset":true,"lines":[]}""", Assert.Single(r.Plains));
    }

    [Fact]
    public async Task ASessionLeavingTheBookEndsItsWatchSilently()
    {
        var r = new Rig { Rows = Rows(2) };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        r.Sent.Clear();
        r.Book.Remove("s1");
        r.Service.Tick();
        Assert.Empty(r.Sent);
        Assert.Equal(0, r.Service.WatchCount);
    }

    [Fact]
    public async Task APhoneGoingOfflineEndsItsWatches()
    {
        var r = new Rig { Rows = Rows(2) };
        await r.Ask("""{"t":"watch","session_id":"s1"}""");
        r.Service.PhoneOffline("ph_other");
        Assert.Equal(1, r.Service.WatchCount);
        r.Service.PhoneOffline("ph_1");
        Assert.Equal(0, r.Service.WatchCount);
    }

    [Fact]
    public async Task InputIsTypedAndAnswersTyped()
    {
        var r = new Rig();
        await r.Ask("""{"t":"input","session_id":"s1","text":"Please rerun\nthe tests\tnow"}""");
        Assert.Equal(["Please rerun\nthe tests\tnow"], r.Typed);
        Assert.Equal("""{"t":"ok","ref":"req1","code":"typed","session_id":"s1"}""", Assert.Single(r.Plains));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\u001bb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u009bb")]
    [InlineData("a\rb")]
    public async Task InputWithAControlCharacterOrNothingIsBadInput(string text)
    {
        var r = new Rig();
        await r.Ask(JsonSerializer.Serialize(new { t = "input", session_id = "s1", text }));
        Assert.Contains("\"code\":\"bad_input\"", Assert.Single(r.Plains));
        Assert.Empty(r.Typed);
    }

    [Fact]
    public async Task InputOverTheCapIsBadInputAndAtTheCapIsTyped()
    {
        var r = new Rig();
        await r.Ask(JsonSerializer.Serialize(new { t = "input", session_id = "s1", text = new string('a', 8001) }));
        Assert.Contains("bad_input", r.Plains[0]);
        await r.Ask(JsonSerializer.Serialize(new { t = "input", session_id = "s1", text = new string('a', 8000) }));
        Assert.Contains("\"code\":\"typed\"", r.Plains[1]);
    }

    [Fact]
    public async Task InputWithoutTextIsBadFrame()
    {
        var r = new Rig();
        await r.Ask("""{"t":"input","session_id":"s1"}""");
        Assert.Contains("bad_frame", Assert.Single(r.Plains));
    }

    [Fact]
    public async Task InputIntoAWaitingSessionIsAlwaysNotAtPrompt()
    {
        var r = new Rig();
        r.Book["s1"] = new("s1", new SessionStatus(), RelayRoster.Waiting);
        await r.Ask("""{"t":"input","session_id":"s1","text":"hi"}""");
        Assert.Equal($$"""{"t":"err","ref":"req1","code":"not_at_prompt","session_id":"s1","why":"{{TypingGate.WaitingWhy}}"}""", Assert.Single(r.Plains));
        Assert.Empty(r.Typed);
    }

    [Fact]
    public async Task InputIntoATerminalThatCannotBeTypedIntoIsUntypeable()
    {
        var r = new Rig { CanType = false };
        await r.Ask("""{"t":"input","session_id":"s1","text":"hi"}""");
        Assert.Contains("\"code\":\"untypeable\"", Assert.Single(r.Plains));
    }

    [Fact]
    public async Task TheGatesRefusalIsCarriedAsWhy()
    {
        var r = new Rig { Typing = (false, "it is showing a dialog: Bash command") };
        await r.Ask("""{"t":"input","session_id":"s1","text":"hi"}""");
        Assert.Equal("""{"t":"err","ref":"req1","code":"not_at_prompt","session_id":"s1","why":"it is showing a dialog: Bash command"}""", Assert.Single(r.Plains));
    }

    [Fact]
    public async Task ATyperThatReturnsFalseOrThrowsIsTypingFailed()
    {
        var r = new Rig { Typing = (false, null) };
        await r.Ask("""{"t":"input","session_id":"s1","text":"hi"}""");
        Assert.Contains("\"code\":\"typing_failed\"", r.Plains[0]);
        r.Throw = true;
        await r.Ask("""{"t":"input","session_id":"s1","text":"hi"}""");
        Assert.Contains("\"code\":\"typing_failed\"", r.Plains[1]);
    }

    [Fact]
    public async Task AnUnknownRequestTypeDoesNothing()
    {
        var r = new Rig();
        await r.Ask("""{"t":"launch"}""");
        Assert.Empty(r.Sent);
    }
}
