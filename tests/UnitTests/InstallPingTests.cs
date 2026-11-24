using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-466: the anonymous install count's decisions and the sender around them, with
// every dependency faked. Nothing here opens a socket: the HTTP class is a fake and
// the real one is exercised against a loopback listener in IntegrationTests.
public class InstallPingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    // ---- Decide --------------------------------------------------------------

    [Fact]
    public void NeverPingedIsDue() =>
        Assert.True(InstallPing.Decide(true, null, null, null, Noon));

    [Fact]
    public void TheSettingOffIsNeverDue() =>
        Assert.False(InstallPing.Decide(false, null, null, null, Noon));

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("FALSE")]
    [InlineData(" Off ")]
    public void AnOffEnvValueDisables(string value) =>
        Assert.False(InstallPing.Decide(true, value, null, null, Noon));

    [Theory]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    public void ANonDisablingEnvValueDoesNot(string value) =>
        Assert.True(InstallPing.Decide(true, value, null, null, Noon));

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void DoNotTrackDisables(string value) =>
        Assert.False(InstallPing.Decide(true, null, value, null, Noon));

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public void DoNotTrackThatIsNotOnDoesNot(string value) =>
        Assert.True(InstallPing.Decide(true, null, value, null, Noon));

    [Fact]
    public void PingedEarlierTodaySkips() =>
        Assert.False(InstallPing.Decide(true, null, null, Noon.AddHours(-11), Noon));

    [Fact]
    public void PingedYesterdaySends() =>
        Assert.True(InstallPing.Decide(true, null, null, Noon.AddDays(-1), Noon));

    [Fact]
    public void TheUtcDateDecidesNotTheHoursElapsed()
    {
        var lastNight = new DateTimeOffset(2026, 11, 23, 23, 59, 0, TimeSpan.Zero);
        var justAfterMidnight = new DateTimeOffset(2026, 11, 24, 0, 1, 0, TimeSpan.Zero);
        Assert.True(InstallPing.Decide(true, null, null, lastNight, justAfterMidnight));
        Assert.False(InstallPing.Decide(true, null, null, justAfterMidnight, justAfterMidnight.AddHours(23)));
    }

    [Fact]
    public void AnOffsetStampIsComparedInUtc()
    {
        // 23:30 on the 23rd at UTC-5 is 04:30 on the 24th UTC: the same UTC day as Noon.
        var local = new DateTimeOffset(2026, 11, 23, 23, 30, 0, TimeSpan.FromHours(-5));
        Assert.False(InstallPing.Decide(true, null, null, local, Noon));
    }

    [Fact]
    public void AStampInTheFutureIsDueSoItGetsRewritten() =>
        Assert.True(InstallPing.Decide(true, null, null, Noon.AddDays(3), Noon));

    // ---- small pure helpers --------------------------------------------------

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("1", false, true)]
    [InlineData(" 1 ", false, true)]
    [InlineData("0", false, false)]
    [InlineData(null, true, true)]
    public void InternalFromEnvOrSetting(string? env, bool setting, bool expected) =>
        Assert.Equal(expected, InstallPing.IsInternal(env, setting));

    [Fact]
    public void EventNamesSeparateInternalFromPublic()
    {
        Assert.Equal("install-ping", InstallPing.EventName(false));
        Assert.Equal("install-ping-internal", InstallPing.EventName(true));
    }

    [Theory]
    [InlineData(true, false, "windows")]
    [InlineData(false, true, "macos")]
    [InlineData(false, false, "linux")]
    public void OsNames(bool win, bool mac, string expected) =>
        Assert.Equal(expected, InstallPing.OsName(win, mac));

    [Theory]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.X86, "x86")]
    public void ArchNames(Architecture arch, string expected) =>
        Assert.Equal(expected, InstallPing.ArchName(arch));

    [Theory]
    [InlineData(null, InstallPing.DefaultBaseUrl)]
    [InlineData("  ", InstallPing.DefaultBaseUrl)]
    [InlineData(" http://127.0.0.1:8765/ ", "http://127.0.0.1:8765")]
    public void BaseUrlOverride(string? given, string expected) =>
        Assert.Equal(expected, InstallPing.ResolveBaseUrl(given));

    [Fact]
    public void AnOverrideIsWarnedAboutAndNoOverrideIsNot()
    {
        Assert.Null(InstallPing.OverrideWarning(null));
        Assert.Null(InstallPing.OverrideWarning(" "));
        var warning = InstallPing.OverrideWarning(" http://127.0.0.1:1 ");
        Assert.StartsWith("WARNING:", warning);
        Assert.Contains("ORBWEAVER_INSTALL_PING_URL", warning);
        Assert.Contains("http://127.0.0.1:1", warning);
    }

    [Fact]
    public void TheUserAgentCarriesAPlatformSoUmamiDoesNotTakeItForABot() =>
        Assert.Equal("Orbweaver/1.0.7 (macos; arm64)", InstallPing.UserAgent("1.0.7", "macos", "arm64"));

    [Theory]
    [InlineData(200, "{\"cache\":\"x\",\"sessionId\":\"s\",\"visitId\":\"v\"}", true)]
    [InlineData(204, "", true)]
    [InlineData(200, null, true)]
    [InlineData(200, "{\"beep\":\"boop\"}", false)]
    [InlineData(200, "{\"BEEP\":1}", false)]
    [InlineData(400, "{}", false)]
    [InlineData(500, "", false)]
    [InlineData(199, "", false)]
    [InlineData(300, "", false)]
    public void WhatCountsAsAccepted(int status, string? body, bool expected) =>
        Assert.Equal(expected, InstallPing.Accepted(status, body));

    // ---- the body: the promise that nothing else is sent ----------------------

    private static readonly string[] PayloadKeys =
        ["data", "hostname", "id", "ip", "name", "url", "website"];
    private static readonly string[] DataKeys = ["arch", "iid", "os", "v"];

    [Fact]
    public void TheBodyHasExactlyTheDocumentedKeys()
    {
        var root = JsonNode.Parse(InstallPing.BuildBody("web-id", "abc123", "1.0.7", "macos", "arm64", false))!.AsObject();
        Assert.Equal(["payload", "type"], root.Select(p => p.Key).Order().ToArray());
        Assert.Equal("event", root["type"]!.GetValue<string>());

        var payload = root["payload"]!.AsObject();
        Assert.Equal(PayloadKeys, payload.Select(p => p.Key).Order().ToArray());
        Assert.Equal("web-id", payload["website"]!.GetValue<string>());
        Assert.Equal("install-ping", payload["name"]!.GetValue<string>());
        Assert.Equal("/install-ping", payload["url"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", payload["ip"]!.GetValue<string>());
        Assert.Equal("abc123", payload["id"]!.GetValue<string>());

        var data = payload["data"]!.AsObject();
        Assert.Equal(DataKeys, data.Select(p => p.Key).Order().ToArray());
        Assert.Equal("1.0.7", data["v"]!.GetValue<string>());
        Assert.Equal("macos", data["os"]!.GetValue<string>());
        Assert.Equal("arm64", data["arch"]!.GetValue<string>());
        Assert.Equal("abc123", data["iid"]!.GetValue<string>());
    }

    [Fact]
    public void AnInternalInstallUsesTheInternalEventName()
    {
        var payload = JsonNode.Parse(InstallPing.BuildBody("w", "i", "1", "windows", "x64", true))!["payload"]!;
        Assert.Equal("install-ping-internal", payload["name"]!.GetValue<string>());
        Assert.Equal("/install-ping-internal", payload["url"]!.GetValue<string>());
    }

    // The negative control for the key-set test: a copy with one extra field must fail
    // the same comparison, so the test above is known to be able to fail.
    [Fact]
    public void TheKeySetComparisonWouldCatchAnExtraField()
    {
        var payload = JsonNode.Parse(InstallPing.BuildBody("w", "i", "1", "macos", "arm64", false))!["payload"]!.AsObject();
        payload["data"]!.AsObject()["hostname"] = "leaked";
        Assert.NotEqual(DataKeys, payload["data"]!.AsObject().Select(p => p.Key).Order().ToArray());
    }

    // ---- the sender ----------------------------------------------------------

    private sealed class PingSettings : IInstallPingSettings
    {
        public bool Enabled { get; set; } = true;
        public string? InstallId { get; set; }
        public DateTimeOffset? LastPing { get; set; }
        public bool Internal { get; set; }
    }

    private sealed class PingHttp : IInstallPingHttp
    {
        public List<(string Url, string Json, string UserAgent)> Posts = [];
        public (int, string) Answer = (200, "{\"cache\":\"x\"}");
        public Exception? Throw;

        public Task<(int Status, string Body)> PostJsonAsync(string url, string json, string userAgent, CancellationToken ct)
        {
            Posts.Add((url, json, userAgent));
            if (Throw is not null) throw Throw;
            return Task.FromResult(Answer);
        }
    }

    private sealed class PingRig
    {
        public PingSettings S = new();
        public PingHttp H = new();
        public DateTimeOffset Now = Noon;
        public List<string> Log = [];
        public Dictionary<string, string?> Env = [];
        public InstallPingSender Sender;

        public PingRig() => Sender = new InstallPingSender(S, H, () => Now, Log.Add,
            k => Env.TryGetValue(k, out var v) ? v : null,
            "1.0.7", "macos", "arm64", "http://fixture");
    }

    [Fact]
    public async Task ADueInstallSendsOnceAndStampsTheDay()
    {
        var r = new PingRig();
        await r.Sender.TrySendAsync();

        var post = Assert.Single(r.H.Posts);
        Assert.Equal("http://fixture/api/send", post.Url);
        Assert.Equal("Orbweaver/1.0.7 (macos; arm64)", post.UserAgent);
        Assert.Equal(Noon, r.S.LastPing);
        Assert.Equal(32, r.S.InstallId!.Length);
        var sent = JsonNode.Parse(post.Json)!["payload"]!;
        Assert.Equal(r.S.InstallId, sent["id"]!.GetValue<string>());
        Assert.Equal("install-ping", sent["name"]!.GetValue<string>());
        Assert.Equal(InstallPing.WebsiteId, sent["website"]!.GetValue<string>());
        Assert.Empty(r.Log);
    }

    [Fact]
    public async Task TheSecondCheckTheSameDaySendsNothing()
    {
        var r = new PingRig();
        await r.Sender.TrySendAsync();
        r.Now = Noon.AddHours(5);
        await r.Sender.TrySendAsync();
        Assert.Single(r.H.Posts);

        r.Now = Noon.AddDays(1);
        await r.Sender.TrySendAsync();
        Assert.Equal(2, r.H.Posts.Count);
    }

    [Fact]
    public async Task TheInstallIdIsMintedOnceAndReused()
    {
        var r = new PingRig();
        await r.Sender.TrySendAsync();
        var first = r.S.InstallId;
        r.Now = Noon.AddDays(1);
        await r.Sender.TrySendAsync();
        Assert.Equal(first, r.S.InstallId);

        r.S.InstallId = "  ";
        r.Now = Noon.AddDays(2);
        await r.Sender.TrySendAsync();
        Assert.NotEqual(first, r.S.InstallId); // a blank one is replaced
    }

    [Fact]
    public async Task AnExistingIdIsSentAsIs()
    {
        var r = new PingRig();
        r.S.InstallId = "kept";
        await r.Sender.TrySendAsync();
        Assert.Equal("kept", JsonNode.Parse(r.H.Posts[0].Json)!["payload"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task EverySkipPathMakesZeroHttpCallsAndMintsNoId()
    {
        foreach (var arrange in new Action<PingRig>[]
        {
            r => r.S.Enabled = false,
            r => r.Env["ORBWEAVER_INSTALL_PING"] = "0",
            r => r.Env["CLAUDE_BUDDY_INSTALL_PING"] = "off",
            r => r.Env["DO_NOT_TRACK"] = "1",
            r => r.S.LastPing = Noon.AddHours(-1),
        })
        {
            var r = new PingRig();
            arrange(r);
            var before = r.S.InstallId;
            await r.Sender.TrySendAsync();
            Assert.Empty(r.H.Posts);
            Assert.Equal(before, r.S.InstallId);
        }
    }

    [Fact]
    public async Task ARefusedAnswerDoesNotStampTheDayAndIsLogged()
    {
        var r = new PingRig();
        r.H.Answer = (200, "{\"beep\":\"boop\"}");
        await r.Sender.TrySendAsync();
        Assert.Null(r.S.LastPing);
        Assert.Contains(r.Log, l => l.Contains("not accepted") && l.Contains("200"));

        r.H.Answer = (503, "");
        await r.Sender.TrySendAsync();
        Assert.Null(r.S.LastPing);
        Assert.Equal(2, r.H.Posts.Count); // not stamped, so it tries again
    }

    [Fact]
    public async Task AThrowingTransportIsSwallowedLoggedAndNotStamped()
    {
        var r = new PingRig();
        r.H.Throw = new HttpRequestException("no route");
        await r.Sender.TrySendAsync(); // must not throw
        Assert.Null(r.S.LastPing);
        Assert.Contains(r.Log, l => l.Contains("install ping failed") && l.Contains("no route"));
    }

    [Fact]
    public async Task InternalFromTheEnvironmentOrTheSettingUsesTheInternalName()
    {
        var viaEnv = new PingRig();
        viaEnv.Env["ORBWEAVER_INTERNAL"] = "1";
        await viaEnv.Sender.TrySendAsync();
        Assert.Equal("install-ping-internal", JsonNode.Parse(viaEnv.H.Posts[0].Json)!["payload"]!["name"]!.GetValue<string>());

        var viaSetting = new PingRig();
        viaSetting.S.Internal = true;
        await viaSetting.Sender.TrySendAsync();
        Assert.Equal("install-ping-internal", JsonNode.Parse(viaSetting.H.Posts[0].Json)!["payload"]!["name"]!.GetValue<string>());
    }

    private sealed class GatedHttp : IInstallPingHttp
    {
        public int Calls;
        public TaskCompletionSource Release = new();
        public Task<(int Status, string Body)> PostJsonAsync(string url, string json, string userAgent, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Release.Task.ContinueWith(_ => (200, "{}"));
        }
    }

    [Fact]
    public async Task TwoSendsAtOnceMakeOnePostAndALaterOneIsStillAllowed()
    {
        var s = new PingSettings();
        var http = new GatedHttp();
        var sender = new InstallPingSender(s, http, () => Noon, _ => { }, _ => null, "1", "macos", "arm64", "http://f");

        var first = sender.TrySendAsync();
        await sender.TrySendAsync(); // joins nothing: skipped while the first is in flight
        Assert.Equal(1, http.Calls);
        http.Release.SetResult();
        await first;
        Assert.Equal(Noon, s.LastPing);

        // The guard is released afterwards: next day sends again.
        var later = new InstallPingSender(s, http, () => Noon.AddDays(1), _ => { }, _ => null, "1", "macos", "arm64", "http://f");
        await later.TrySendAsync();
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task ATogglePulledDuringTheRequestIsNotStampedAfterwards()
    {
        var s = new PingSettings();
        var http = new GatedHttp();
        var sender = new InstallPingSender(s, http, () => Noon, _ => { }, _ => null, "1", "macos", "arm64", "http://f");

        var send = sender.TrySendAsync();
        s.Enabled = false; // the user switched it off mid-flight
        http.Release.SetResult();
        await send;

        Assert.Null(s.LastPing);
        Assert.Null(s.InstallId);
    }

    // ---- UpdateChecker's afterCheck hook -------------------------------------

    private sealed class PingFeed : IReleaseFeed
    {
        public Exception? Throw;
        public Task<IReadOnlyList<UpdateRelease>> FetchAsync(bool includePrerelease, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            IReadOnlyList<UpdateRelease> none = [];
            return Task.FromResult(none);
        }
    }

    private sealed class PingUpdSettings : IUpdateSettings
    {
        public bool AutoCheck { get; set; } = true;
        public bool IncludePrerelease { get; set; }
        public string? SkippedVersion { get; set; }
        public DateTimeOffset? LastCheck { get; set; }
    }

    private sealed class PingNoTimer : IDisposable { public void Dispose() { } }

    private static UpdateChecker Checker(PingFeed feed, Func<Task>? after, List<string> log) =>
        new(feed, new PingUpdSettings(), "1.0.3", () => Noon, log.Add, (_, _) => new PingNoTimer(), after);

    [Fact]
    public async Task TheHookRunsAfterAnUpToDateCheck()
    {
        var ran = new TaskCompletionSource();
        using var c = Checker(new PingFeed(), () => { ran.SetResult(); return Task.CompletedTask; }, []);
        await c.CheckNowAsync();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheHookRunsEvenWhenTheCheckFailed()
    {
        var ran = new TaskCompletionSource();
        var feed = new PingFeed { Throw = new InvalidOperationException("rate limited") };
        using var c = Checker(feed, () => { ran.SetResult(); return Task.CompletedTask; }, []);
        var result = await c.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AThrowingHookIsLoggedAndDoesNotChangeTheCheck()
    {
        var log = new List<string>();
        var logged = new TaskCompletionSource();
        Action<string> sink = l => { lock (log) log.Add(l); if (l.Contains("install ping hook threw")) logged.TrySetResult(); };
        using var c = new UpdateChecker(new PingFeed(), new PingUpdSettings(), "1.0.3", () => Noon, sink,
            (_, _) => new PingNoTimer(), () => throw new InvalidOperationException("boom"));
        var result = await c.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AFaultingHookTaskIsLoggedToo()
    {
        var logged = new TaskCompletionSource();
        using var c = new UpdateChecker(new PingFeed(), new PingUpdSettings(), "1.0.3", () => Noon,
            l => { if (l.Contains("faulted")) logged.TrySetResult(); },
            (_, _) => new PingNoTimer(), async () => { await Task.Yield(); throw new InvalidOperationException("faulted"); });
        await c.CheckNowAsync();
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AHookThatNeverFinishesDoesNotHoldTheCheck()
    {
        var never = new TaskCompletionSource();
        using var c = Checker(new PingFeed(), () => never.Task, []);
        var result = await c.CheckNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task NoHookIsANoOp()
    {
        using var c = Checker(new PingFeed(), null, []);
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await c.CheckNowAsync()).Outcome);
    }
}
