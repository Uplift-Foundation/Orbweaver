using Xunit;
using static Orbweaver.PaperclipSessions;

namespace Orbweaver.Tests;

// A scripted transport: answers by exact path, records every call.
internal sealed class FakePaperclip : IPaperclipApi
{
    internal readonly List<string> Calls = [];
    internal readonly Dictionary<string, PaperclipResponse> Gets = new();
    internal readonly Dictionary<string, PaperclipResponse> Posts = new();
    internal readonly Dictionary<string, Queue<PaperclipResponse>> Sequences = new();
    internal string? LastPostBody;

    internal static PaperclipResponse Ok(string body) => new(PaperclipHttpKind.Ok, 200, body);
    internal static PaperclipResponse Status(PaperclipHttpKind k, int code = 0, string? detail = null) => new(k, code, "", detail);

    public Task<PaperclipResponse> GetAsync(string path, CancellationToken ct)
    {
        Calls.Add("GET " + path);
        if (Sequences.TryGetValue(path, out var q) && q.Count > 0) return Task.FromResult(q.Dequeue());
        return Task.FromResult(Gets.GetValueOrDefault(path) ?? Status(PaperclipHttpKind.NotFound, 404));
    }

    public Task<PaperclipResponse> PostAsync(string path, string jsonBody, CancellationToken ct)
    {
        Calls.Add("POST " + path);
        LastPostBody = jsonBody;
        return Task.FromResult(Posts.GetValueOrDefault(path) ?? Status(PaperclipHttpKind.NotFound, 404));
    }
}

public class PaperclipExplainTests
{
    [Fact]
    public void TheInnermostReasonIsPrintedOnce_WithNoAddress_TheCallersSentenceHasIt()
    {
        var socket = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
        var outer = new HttpRequestException(socket.Message + " (127.0.0.1:3100)", socket);
        Assert.Equal(socket.Message, PaperclipHttp.Explain(outer, onMacOS: false));
    }

    [Fact]
    public void AnExceptionWithNoInner_IsItsOwnReason() =>
        Assert.Equal("boom", PaperclipHttp.Explain(new Exception("boom"), false));

    [Fact]
    public void ANoRouteOnAMac_GetsTheLocalNetworkHint_ElsewhereItDoesNot()
    {
        var socket = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);
        var ex = new HttpRequestException("x", socket);
        Assert.Contains("Local Network", PaperclipHttp.Explain(ex, onMacOS: true));
        Assert.DoesNotContain("Local Network", PaperclipHttp.Explain(ex, onMacOS: false));
    }
}

public class PaperclipDiscoveryTests
{
    private static IEnumerable<string> Folders(string root) => ["/h/.paperclip/instances/default", "/h/.paperclip/instances/prod"];

    [Fact]
    public void RealRuntimeInfo_GivesLoopbackOn3100()
    {
        var c = PaperclipDiscovery.Candidates(r => ["/h/.paperclip/instances/default"],
            f => f.EndsWith("runtime-info.json") ? PaperclipFx.Read("native/runtime-info.json") : null, "/h/.paperclip/instances");
        var one = Assert.Single(c);
        Assert.Equal(("http://127.0.0.1:3100", "default"), (one.BaseUrl, one.InstanceName));
    }

    [Fact]
    public void ConfigOnly_FallsBackToItsServerPort()
    {
        var c = PaperclipDiscovery.Candidates(r => ["/h/.paperclip/instances/default"],
            f => f.EndsWith("config.json") ? PaperclipFx.Read("native/config-server-section.json") : null, "/r");
        Assert.Equal("http://127.0.0.1:3100", Assert.Single(c).BaseUrl);
    }

    [Fact]
    public void TwoInstances_BothListed_MalformedOneSkipped_DuplicatePortsCollapse()
    {
        var c = PaperclipDiscovery.Candidates(Folders, f => f.Contains("default") ? "{\"port\":3100}" : "{\"port\":3101}", "/r");
        Assert.Equal(new[] { "http://127.0.0.1:3100", "http://127.0.0.1:3101" }, c.Select(x => x.BaseUrl).ToArray());

        var bad = PaperclipDiscovery.Candidates(Folders, f => f.Contains("default") ? "not json" : "{\"port\":3101}", "/r");
        Assert.Equal("http://127.0.0.1:3101", Assert.Single(bad).BaseUrl);

        var dup = PaperclipDiscovery.Candidates(Folders, f => "{\"port\":3100}", "/r");
        Assert.Single(dup);
    }

    [Fact]
    public void NoFolder_OrAnUnreadableOne_GivesTheDefaultCandidate()
    {
        Assert.Equal(PaperclipDiscovery.DefaultAddress, Assert.Single(PaperclipDiscovery.Candidates(r => [], f => null, "/r")).BaseUrl);
        Assert.Equal(PaperclipDiscovery.DefaultAddress, Assert.Single(PaperclipDiscovery.Candidates(r => throw new IOException(), f => null, "/r")).BaseUrl);
        Assert.Equal(PaperclipDiscovery.DefaultAddress,
            Assert.Single(PaperclipDiscovery.Candidates(r => ["/x/y"], f => throw new IOException(), "/r")).BaseUrl);
    }

    [Fact]
    public void OnlyRuntimeInfoAndConfigAreEverRead()
    {
        var read = new List<string>();
        PaperclipDiscovery.Candidates(r => ["/h/.paperclip/instances/default"], f => { read.Add(Path.GetFileName(f)); return null; }, "/r");
        Assert.Equal(new[] { "runtime-info.json", "config.json" }, read.ToArray());
    }

    [Fact]
    public async Task OnlyACandidateWhoseHealthIsOk_IsFound()
    {
        var good = new FakePaperclip(); good.Gets["/api/health"] = FakePaperclip.Ok(PaperclipFx.Read("health.json"));
        var notOk = new FakePaperclip(); notOk.Gets["/api/health"] = FakePaperclip.Ok("{\"status\":\"starting\"}");
        var down = new FakePaperclip();
        var garbage = new FakePaperclip(); garbage.Gets["/api/health"] = FakePaperclip.Ok("<html>");
        var apis = new Dictionary<string, FakePaperclip> { ["u1"] = good, ["u2"] = notOk, ["u3"] = down, ["u4"] = garbage };
        var found = await PaperclipDiscovery.FindAsync(
            apis.Keys.Select(k => new PaperclipDiscovery.Candidate(k, k)).ToList(), u => apis[u], default);
        var f = Assert.Single(found);
        Assert.Equal(("u1", "authenticated"), (f.BaseUrl, f.DeploymentMode));
    }

    [Fact]
    public void InstancesRoot_IsUnderDotPaperclip() =>
        Assert.Equal(Path.Combine("/h", ".paperclip", "instances"), PaperclipDiscovery.InstancesRoot("/h"));
}

public class PaperclipConnectTests
{
    private const string Health = "{\"status\":\"ok\",\"deploymentMode\":\"authenticated\"}";

    // The real shape, measured on the live instance: pollPath has no /api prefix
    // and no query; approvalPath is a UI route carrying the token.
    private static string ChallengeJson(string url = "http://127.0.0.1:3100/cli-auth/c1?token=tok", string? expires = null,
        string pollPath = "/cli-auth/challenges/c1", string approvalPath = "/cli-auth/c1?token=tok") =>
        "{\"id\":\"c1\",\"token\":\"tok\",\"boardApiToken\":\"KEY\",\"approvalUrl\":\"" + url + "\"," +
        "\"approvalPath\":\"" + approvalPath + "\",\"pollPath\":\"" + pollPath + "\",\"suggestedPollIntervalMs\":1000" +
        (expires == null ? "" : ",\"expiresAt\":\"" + expires + "\"") + "}";

    private sealed class World
    {
        internal readonly FakePaperclip Anon = new();
        internal readonly FakePaperclip Keyed = new();
        internal readonly List<string> Opened = [];
        internal DateTime Now = PaperclipFx.Now;
        internal bool BrowserWorks = true;
        internal readonly List<string> Progress = [];
        internal string? KeyUsed;

        internal World(string mode = "authenticated")
        {
            Anon.Gets["/api/health"] = FakePaperclip.Ok("{\"status\":\"ok\",\"deploymentMode\":\"" + mode + "\"}");
            Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson());
            Keyed.Gets["/api/cli-auth/me"] = FakePaperclip.Ok("{\"source\":\"board_key\"}");
        }

        internal void Statuses(params string[] s) =>
            Anon.Sequences["/api/cli-auth/challenges/c1?token=tok"] = new Queue<PaperclipResponse>(
                s.Select(x => FakePaperclip.Ok("{\"status\":\"" + x + "\"}")));

        internal Task<PaperclipConnectResult> Run(CancellationToken ct = default) =>
            PaperclipConnect.RunAsync("http://127.0.0.1:3100", k => { KeyUsed = k; return k == null ? Anon : Keyed; },
                u => { Opened.Add(u); return BrowserWorks; }, () => Now,
                (d, c) => { Now += d; return Task.CompletedTask; }, ct, Progress.Add);
    }

    [Fact]
    public async Task Approved_StoresNothingUntilMeIs200_ThenReturnsKeyAnd30DayExpiry()
    {
        var w = new World(); w.Statuses("pending", "pending", "approved");
        var r = await w.Run();
        Assert.Equal(PaperclipConnectOutcome.Connected, r.Outcome);
        Assert.Equal("KEY", r.Key);
        Assert.Equal(w.Now + PaperclipConnect.KeyLifetime, r.KeyExpires);
        Assert.Equal(new[] { "http://127.0.0.1:3100/cli-auth/c1?token=tok" }, w.Opened.ToArray());
        Assert.Contains("Approve Orbweaver in your browser…", w.Progress);
        Assert.Equal("{\"command\":\"Orbweaver\",\"clientName\":\"Orbweaver\",\"requestedAccess\":\"board\"}", w.Anon.LastPostBody);
        Assert.Contains("GET /api/cli-auth/me", w.Keyed.Calls);
    }

    [Fact]
    public async Task ApprovedButMeRefuses_IsAFailure_NoKey()
    {
        var w = new World(); w.Statuses("approved");
        w.Keyed.Gets["/api/cli-auth/me"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        var r = await w.Run();
        Assert.Equal(PaperclipConnectOutcome.Failed, r.Outcome);
        Assert.Null(r.Key);
    }

    [Theory]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("expired", "Expired")]
    public async Task CancelledOrExpiredByPaperclip_StoresNothing(string status, string expectedName)
    {
        var w = new World(); w.Statuses(status);
        var r = await w.Run();
        Assert.Equal(Enum.Parse<PaperclipConnectOutcome>(expectedName), r.Outcome);
        Assert.Null(r.Key);
        Assert.DoesNotContain("GET /api/cli-auth/me", w.Keyed.Calls);
    }

    [Fact]
    public async Task PollingPastTheChallengesOwnExpiry_TimesOut()
    {
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(expires: PaperclipFx.Now.AddSeconds(3).ToString("O")));
        w.Statuses("pending", "pending", "pending", "pending", "pending");
        Assert.Equal(PaperclipConnectOutcome.Expired, (await w.Run()).Outcome);
    }

    [Fact]
    public async Task WithNoExpiryGiven_TenMinutesIsTheBound()
    {
        var w = new World();
        w.Anon.Gets["/api/cli-auth/challenges/c1?token=tok"] = FakePaperclip.Ok("{\"status\":\"pending\"}");
        Assert.Equal(PaperclipConnectOutcome.Expired, (await w.Run()).Outcome);
        Assert.True(w.Now - PaperclipFx.Now >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task APollHiccup_IsRiddenOut()
    {
        var w = new World();
        w.Anon.Sequences["/api/cli-auth/challenges/c1?token=tok"] = new Queue<PaperclipResponse>(
            [FakePaperclip.Status(PaperclipHttpKind.Timeout), FakePaperclip.Ok("{\"status\":\"approved\"}")]);
        Assert.Equal(PaperclipConnectOutcome.Connected, (await w.Run()).Outcome);
    }

    [Fact]
    public async Task PollPathAlreadyCarryingAQuery_IsNotGivenASecondToken()
    {
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(pollPath: "/api/cli-auth/challenges/c1?token=tok"));
        w.Anon.Gets["/api/cli-auth/challenges/c1?token=tok"] = FakePaperclip.Ok("{\"status\":\"approved\"}");
        Assert.Equal(PaperclipConnectOutcome.Connected, (await w.Run()).Outcome);
    }

    [Fact]
    public async Task UserCancel_StopsPollingAndStoresNothing()
    {
        var w = new World(); w.Statuses("pending", "pending");
        using var cts = new CancellationTokenSource();
        var r = await PaperclipConnect.RunAsync("http://127.0.0.1:3100", k => k == null ? w.Anon : w.Keyed, u => true, () => w.Now,
            (d, c) => { cts.Cancel(); return Task.CompletedTask; }, cts.Token);
        Assert.Equal(PaperclipConnectOutcome.Cancelled, r.Outcome);
        Assert.Null(r.Key);
        Assert.DoesNotContain(w.Anon.Calls, c => c.Contains("challenges/c1?token"));
    }

    // ---- the server is told when an attempt is abandoned ----

    private const string CancelCall = "POST /api/cli-auth/challenges/c1/cancel";

    private static int Cancels(World w) => w.Anon.Calls.Count(c => c == CancelCall);

    [Fact]
    public async Task UserCancel_SendsExactlyOneCancel_WithTheChallengeToken_EvenThoughItsTokenIsCancelled()
    {
        var w = new World(); w.Statuses("pending", "pending");
        using var cts = new CancellationTokenSource();
        var r = await PaperclipConnect.RunAsync("http://127.0.0.1:3100", k => k == null ? w.Anon : w.Keyed, u => true, () => w.Now,
            (d, c) => { cts.Cancel(); return Task.CompletedTask; }, cts.Token);
        Assert.Equal(PaperclipConnectOutcome.Cancelled, r.Outcome);
        Assert.Equal(1, Cancels(w));
        Assert.Equal("{\"token\":\"tok\"}", w.Anon.LastPostBody);
    }

    [Fact]
    public async Task ExpiringLocally_SendsExactlyOneCancel()
    {
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(expires: PaperclipFx.Now.AddSeconds(3).ToString("O")));
        w.Anon.Gets["/api/cli-auth/challenges/c1?token=tok"] = FakePaperclip.Ok("{\"status\":\"pending\"}");
        Assert.Equal(PaperclipConnectOutcome.Expired, (await w.Run()).Outcome);
        Assert.Equal(1, Cancels(w));
    }

    [Fact]
    public async Task FailFast_SendsExactlyOneCancel()
    {
        var w = new World();
        Assert.Equal(PaperclipConnectOutcome.Failed, (await w.Run()).Outcome);
        Assert.Equal(1, Cancels(w));
    }

    [Fact]
    public async Task ARefusedApprovalUrl_AndABrowserThatWontOpen_EachCancelOnce()
    {
        var refused = new World();
        refused.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(url: "file:///x"));
        Assert.Equal(PaperclipConnectOutcome.Refused, (await refused.Run()).Outcome);
        Assert.Equal(1, Cancels(refused));

        var noBrowser = new World { BrowserWorks = false };
        Assert.Equal(PaperclipConnectOutcome.Failed, (await noBrowser.Run()).Outcome);
        Assert.Equal(1, Cancels(noBrowser));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("cancelled")]   // Paperclip already knows
    [InlineData("expired")]
    public async Task NoCancelIsSent_WhenTheServerSettledItAlready(string status)
    {
        var w = new World(); w.Statuses(status);
        await w.Run();
        Assert.Equal(0, Cancels(w));
    }

    [Fact]
    public async Task NoCancelIsSent_WhenNoChallengeWasEverCreated()
    {
        var w = new World();
        w.Anon.Posts.Remove("/api/cli-auth/challenges");
        Assert.Equal(PaperclipConnectOutcome.Failed, (await w.Run()).Outcome);
        Assert.Equal(0, Cancels(w));
    }

    [Fact]
    public async Task AFailingCancel_ChangesNoOutcome_AndShowsNoError()
    {
        // The server answers the cancel with an error.
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges/c1/cancel"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var r = await w.Run();
        Assert.Equal(PaperclipConnectOutcome.Failed, r.Outcome);
        Assert.Contains("stopped answering", r.Message);   // the original reason, not the cancel's

        // The transport throws on it.
        var inner = new World();
        var throwing = new CancelThrows(inner.Anon);
        var r2 = await PaperclipConnect.RunAsync("http://127.0.0.1:3100", k => k == null ? throwing : inner.Keyed, u => true,
            () => inner.Now, (d, c) => { inner.Now += d; return Task.CompletedTask; }, default);
        Assert.Equal(PaperclipConnectOutcome.Failed, r2.Outcome);
        Assert.Contains("stopped answering", r2.Message);
    }

    private sealed class CancelThrows(IPaperclipApi inner) : IPaperclipApi
    {
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken c) => inner.GetAsync(p, c);
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken c) =>
            p.EndsWith("/cancel") ? throw new InvalidOperationException("boom") : inner.PostAsync(p, b, c);
    }

    [Fact]
    public async Task AnApprovedAttempt_SendsNoCancel_ForAKeyThatDidNotWork()
    {
        var w = new World(); w.Statuses("approved");
        w.Keyed.Gets["/api/cli-auth/me"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        Assert.Equal(PaperclipConnectOutcome.Failed, (await w.Run()).Outcome);
        Assert.Equal(0, Cancels(w));
    }

    [Theory]
    [InlineData("/cli-auth/challenges/c1")]        // the real shape
    [InlineData("/api/cli-auth/challenges/c1")]    // already under /api
    public async Task PollPath_IsResolvedAgainstTheApiRoot(string pollPath)
    {
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(pollPath: pollPath));
        w.Statuses("approved");
        Assert.Equal(PaperclipConnectOutcome.Connected, (await w.Run()).Outcome);
        Assert.Contains("GET /api/cli-auth/challenges/c1?token=tok", w.Anon.Calls);
        Assert.DoesNotContain(w.Anon.Calls, c => c.StartsWith("GET /cli-auth"));
    }

    [Fact]
    public async Task APollThatNeverAnswersOk_FailsFastWithAReason_NotAfterTenMinutes()
    {
        var w = new World();   // every poll 404s: nothing is scripted for the poll path
        var r = await w.Run();
        Assert.Equal(PaperclipConnectOutcome.Failed, r.Outcome);
        Assert.Contains("stopped answering the approval check", r.Message);
        Assert.Contains("404", r.Message);
        Assert.Equal(PaperclipConnect.MaxFailedPolls, w.Anon.Calls.Count(c => c.Contains("challenges/c1?token")));
        Assert.True(w.Now - PaperclipFx.Now < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task FailedPollsThatAreNotConsecutive_AreRiddenOut()
    {
        var w = new World();
        w.Anon.Sequences["/api/cli-auth/challenges/c1?token=tok"] = new Queue<PaperclipResponse>(
        [
            FakePaperclip.Status(PaperclipHttpKind.Timeout), FakePaperclip.Status(PaperclipHttpKind.Timeout),
            FakePaperclip.Ok("{\"status\":\"pending\"}"),
            FakePaperclip.Status(PaperclipHttpKind.Timeout), FakePaperclip.Status(PaperclipHttpKind.Timeout),
            FakePaperclip.Ok("{\"status\":\"approved\"}"),
        ]);
        Assert.Equal(PaperclipConnectOutcome.Connected, (await w.Run()).Outcome);
    }

    [Fact]
    public async Task APollThatTimesOutThreeTimes_NamesTheKindWhenThereIsNoStatus()
    {
        var w = new World();
        w.Anon.Gets["/api/cli-auth/challenges/c1?token=tok"] = FakePaperclip.Status(PaperclipHttpKind.Timeout);
        Assert.Contains("(timeout)", (await w.Run()).Message);
    }

    [Fact]
    public void ApprovalUrl_PrefersTheAddressTheUserConnectedTo_WhenTheHostsDiffer()
    {
        PaperclipApi.Challenge Ch(string url, string? path) =>
            new("c", "t", "k", url, "/cli-auth/challenges/c", null, 1000, path);
        // same host: the returned absolute URL as it is
        Assert.Equal("http://127.0.0.1:3100/cli-auth/c?token=t",
            PaperclipConnect.ApprovalUrlFor("http://127.0.0.1:3100", Ch("http://127.0.0.1:3100/cli-auth/c?token=t", "/cli-auth/c?token=t")));
        // the server believes it is at another address: the connected one plus the path
        Assert.Equal("http://192.0.2.10:3100/cli-auth/c?token=t",
            PaperclipConnect.ApprovalUrlFor("http://192.0.2.10:3100/", Ch("http://0.0.0.0:3100/cli-auth/c?token=t", "/cli-auth/c?token=t")));
        // no path to build from, or a path that is not a path: the returned URL stands
        Assert.Equal("http://0.0.0.0:3100/x",
            PaperclipConnect.ApprovalUrlFor("http://192.0.2.10:3100", Ch("http://0.0.0.0:3100/x", null)));
        Assert.Equal("http://0.0.0.0:3100/x",
            PaperclipConnect.ApprovalUrlFor("http://192.0.2.10:3100", Ch("http://0.0.0.0:3100/x", "evil.example/y")));
        Assert.Equal("http://0.0.0.0:3100/x",
            PaperclipConnect.ApprovalUrlFor("not a url", Ch("http://0.0.0.0:3100/x", "/y")));
    }

    [Fact]
    public async Task ADifferentHostApprovalUrl_IsReplacedBeforeTheBrowserIsOpened()
    {
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(url: "http://0.0.0.0:3100/cli-auth/c1?token=tok"));
        w.Statuses("approved");
        await w.Run();
        Assert.Equal(new[] { "http://127.0.0.1:3100/cli-auth/c1?token=tok" }, w.Opened.ToArray());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x/y")]
    [InlineData("not a url")]
    public async Task ANonHttpApprovalUrl_IsRefused_AndNeverOpened(string url)
    {
        var w = new World();
        w.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(url));
        var r = await w.Run();
        Assert.Equal(PaperclipConnectOutcome.Refused, r.Outcome);
        Assert.Empty(w.Opened);
    }

    [Fact]
    public async Task LocalTrusted_ConnectsWithNoChallengeNoBrowserNoKey()
    {
        var w = new World("local_trusted");
        w.Anon.Gets["/api/companies"] = FakePaperclip.Ok("[]");
        var r = await w.Run();
        Assert.Equal(PaperclipConnectOutcome.ConnectedNoKey, r.Outcome);
        Assert.Null(r.Key);
        Assert.Empty(w.Opened);
        Assert.DoesNotContain("POST /api/cli-auth/challenges", w.Anon.Calls);
    }

    [Fact]
    public async Task LocalTrustedThatRefusesAnonymousCompanies_FallsThroughToApproval()
    {
        var w = new World("local_trusted"); w.Statuses("approved");
        w.Anon.Gets["/api/companies"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        Assert.Equal(PaperclipConnectOutcome.Connected, (await w.Run()).Outcome);
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public async Task EveryFailureNamesTheAddressTheUserTried_Once()
    {
        var results = new List<(string Name, string Message)>();

        var timeout = new World(); timeout.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.Timeout, 0, "Timed out");
        results.Add(("unreachable", (await timeout.Run()).Message!));

        var refused = new World(); refused.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.Unreachable, 0, "No connection could be made because the target machine actively refused it");
        results.Add(("refused", (await refused.Run()).Message!));

        var http500 = new World(); http500.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        results.Add(("health 500", (await http500.Run()).Message!));

        var noChallenge = new World(); noChallenge.Anon.Posts.Remove("/api/cli-auth/challenges");
        results.Add(("no challenge", (await noChallenge.Run()).Message!));

        var garbled = new World(); garbled.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok("{}");
        results.Add(("not understood", (await garbled.Run()).Message!));

        var badLink = new World(); badLink.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(ChallengeJson(url: "file:///x"));
        results.Add(("bad link", (await badLink.Run()).Message!));

        var noBrowser = new World { BrowserWorks = false };
        results.Add(("browser", (await noBrowser.Run()).Message!));

        var polls = new World();
        results.Add(("polls", (await polls.Run()).Message!));

        var badKey = new World(); badKey.Statuses("approved");
        badKey.Keyed.Gets["/api/cli-auth/me"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        results.Add(("key", (await badKey.Run()).Message!));

        foreach (var (name, message) in results)
            Assert.True(Count(message, "127.0.0.1:3100") == 1, $"{name}: \"{message}\" must name the address exactly once");
    }

    [Fact]
    public async Task TheReasonsRead_AfterTheAddress_InPlainWords()
    {
        var timeout = new World(); timeout.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.Timeout, 0, "Timed out");
        Assert.Equal("Can't reach Paperclip at 127.0.0.1:3100: timed out.", (await timeout.Run()).Message);

        var refused = new World(); refused.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.Unreachable, 0, "No connection could be made because the target machine actively refused it.");
        Assert.Equal("Can't reach Paperclip at 127.0.0.1:3100: no connection could be made because the target machine actively refused it.", (await refused.Run()).Message);

        var http500 = new World(); http500.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        Assert.Equal("Can't reach Paperclip at 127.0.0.1:3100: HTTP 500.", (await http500.Run()).Message);

        var noKind = new World(); noKind.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.Unreachable);
        Assert.Equal("Can't reach Paperclip at 127.0.0.1:3100: unreachable.", (await noKind.Run()).Message);

        var noChallenge = new World(); noChallenge.Anon.Posts.Remove("/api/cli-auth/challenges");
        Assert.Equal("Paperclip at 127.0.0.1:3100 would not start an approval: HTTP 404.", (await noChallenge.Run()).Message);

        var polls = new World();
        Assert.Equal("Paperclip at 127.0.0.1:3100 stopped answering the approval check (HTTP 404).", (await polls.Run()).Message);
    }

    [Fact]
    public async Task AFailedConnectWithAnAddressWithAPort_UsesAuthorityNotTheWholeUrl()
    {
        var w = new World(); w.Anon.Gets["/api/health"] = FakePaperclip.Status(PaperclipHttpKind.Timeout, 0, "Timed out");
        var r = await PaperclipConnect.RunAsync("http://192.168.0.250:3100/", k => k == null ? w.Anon : w.Keyed, u => true,
            () => w.Now, (d, c) => Task.CompletedTask, default);
        Assert.Equal("Can't reach Paperclip at 192.168.0.250:3100: timed out.", r.Message);
        var odd = await PaperclipConnect.RunAsync("nonsense", k => k == null ? w.Anon : w.Keyed, u => true,
            () => w.Now, (d, c) => Task.CompletedTask, default);
        Assert.Equal("Can't reach Paperclip at nonsense: timed out.", odd.Message);
    }

    [Fact]
    public void ChooseCompany_OneIsChosen_SeveralNeedAPick_StoredChoiceWins()
    {
        PaperclipApi.Company A = new("a", "A", "A"), B = new("b", "B", "B");
        Assert.Null(PaperclipConnect.ChooseCompany([], null));
        Assert.Equal(A, PaperclipConnect.ChooseCompany([A], null));
        Assert.Null(PaperclipConnect.ChooseCompany([A, B], null));
        Assert.Equal(B, PaperclipConnect.ChooseCompany([A, B], "b"));
        Assert.Null(PaperclipConnect.ChooseCompany([A, B], "gone"));
        Assert.Equal(A, PaperclipConnect.ChooseCompany([A], "gone"));
    }

    [Fact]
    public async Task ListCompanies_ParsesOk_EmptyOnFailure()
    {
        var f = new FakePaperclip(); f.Gets["/api/companies"] = FakePaperclip.Ok(PaperclipFx.Read("companies.json"));
        Assert.Single(await PaperclipConnect.ListCompaniesAsync(f, default));
        Assert.Empty(await PaperclipConnect.ListCompaniesAsync(new FakePaperclip(), default));
    }

    [Fact]
    public async Task Disconnect_RevokesThenDeletes_AndDeletesEvenWhenRevokeFails()
    {
        var order = new List<string>();
        var ok = new FakePaperclip(); ok.Posts["/api/cli-auth/revoke-current"] = FakePaperclip.Ok("{}");
        Assert.True(await PaperclipConnect.DisconnectAsync(ok, () => order.Add("deleted"), default));
        Assert.Equal(new[] { "POST /api/cli-auth/revoke-current" }, ok.Calls.ToArray());

        var failed = new FakePaperclip(); // 404 for the revoke
        Assert.False(await PaperclipConnect.DisconnectAsync(failed, () => order.Add("deleted2"), default));

        Assert.False(await PaperclipConnect.DisconnectAsync(null, () => order.Add("deleted3"), default));
        Assert.Equal(new[] { "deleted", "deleted2", "deleted3" }, order.ToArray());
    }

    private sealed class Throwing : IPaperclipApi
    {
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken c) => throw new InvalidOperationException();
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken c) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Disconnect_ATransportThatThrows_StillDeletesTheLocalKey()
    {
        var deleted = false;
        Assert.False(await PaperclipConnect.DisconnectAsync(new Throwing(), () => deleted = true, default));
        Assert.True(deleted);
    }

    [Fact]
    public async Task Disconnect_Cancelled_StillDeletesAndPropagates()
    {
        var deleted = false;
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelling = new CancelApi();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PaperclipConnect.DisconnectAsync(cancelling, () => deleted = true, cts.Token));
        Assert.True(deleted);
    }

    private sealed class CancelApi : IPaperclipApi
    {
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken c) => throw new OperationCanceledException();
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken c) => throw new OperationCanceledException();
    }
}

public class PaperclipCredentialsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-cred-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void RoundTrip_PerBaseUrl_CaseAndSlashInsensitive_DeleteRemoves()
    {
        var c = new PaperclipCredentials(_dir);
        var exp = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        c.Set("http://H:3100/", "k1", exp);
        c.Set("http://other:3100", "k2", exp.AddDays(1));
        var again = new PaperclipCredentials(_dir);
        Assert.Equal(("k1", exp), (again.Get("http://h:3100")!.Key, again.Get("http://h:3100")!.Expires));
        Assert.Equal("k2", again.Get("http://other:3100/")!.Key);
        again.Delete("http://h:3100");
        Assert.Null(again.Get("http://h:3100"));
        Assert.NotNull(again.Get("http://other:3100"));
        again.Delete("http://never");
    }

    [UnixModeFact]
    public void FileIsOwnerOnly_OffWindows()
    {
        var c = new PaperclipCredentials(_dir);
        c.Set("http://h", "k", DateTime.UtcNow);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_dir, "paperclip-credentials.json")));
    }

    [UnixModeFact]
    public void TheKeyFileIsNeverWiderThan0600_NotEvenWhileItIsBeingWritten()
    {
        Directory.CreateDirectory(_dir);
        var c = new PaperclipCredentials(_dir);
        var wide = new List<string>();
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                foreach (var f in Directory.EnumerateFiles(_dir))
                {
                    try
                    {
                        var m = File.GetUnixFileMode(f);
                        if (m != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) lock (wide) wide.Add($"{Path.GetFileName(f)} {m}");
                    }
                    catch (IOException) { }   // renamed away between listing and stat
                }
        });
        for (var i = 0; i < 300; i++) c.Set("http://h", "SECRET" + i, DateTime.UtcNow);
        stop.Cancel();
        watcher.Wait();
        Assert.Empty(wide);
        Assert.Equal("SECRET299", c.Get("http://h")!.Key);
        Assert.Equal(new[] { "paperclip-credentials.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [UnixModeFact]
    public void AnExistingWiderFile_DoesNotLeakItsModeToTheReplacement()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "paperclip-credentials.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        new PaperclipCredentials(_dir).Set("http://h", "k", DateTime.UtcNow);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    // ---- Windows: an explicit owner-only ACL, inheritance off (read back) ----

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyAcl(string path)
    {
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance must be off");
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        var only = Assert.Single(rules);
        Assert.Equal(me, only.IdentityReference);
        Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, only.AccessControlType);
        Assert.Equal(System.Security.AccessControl.FileSystemRights.FullControl, only.FileSystemRights);
        Assert.False(only.IsInherited);
        Assert.Equal(me, security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)));
    }

    [WindowsAclFact]
    public void Windows_TheFileIsCreatedOwnerOnly_BeforeAByteIsWritten()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "created.tmp");
        using var fs = PaperclipCredentials.CreatePrivate(path);   // nothing written yet
        AssertOwnerOnlyAcl(path);
    }

    [WindowsAclFact]
    public void Windows_TheStoredFileIsOwnerOnly_InheritanceOff()
    {
        new PaperclipCredentials(_dir).Set("http://h", "k", DateTime.UtcNow);
        AssertOwnerOnlyAcl(Path.Combine(_dir, "paperclip-credentials.json"));
    }

    [WindowsAclFact]
    public void Windows_AnExistingWiderFile_DoesNotLeakItsAclToTheReplacement()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "paperclip-credentials.json");
        File.WriteAllText(path, "{}");
        // Make it clearly wider than owner-only: everyone may read it.
        var wide = new FileInfo(path).GetAccessControl();
        wide.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(wide);

        new PaperclipCredentials(_dir).Set("http://h", "k", DateTime.UtcNow);

        AssertOwnerOnlyAcl(path);
        Assert.Equal("k", new PaperclipCredentials(_dir).Get("http://h")!.Key);
    }

    [Fact]
    public void ACorruptFile_ReadsAsEmpty_AndIsOverwrittenOnNextSet()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "paperclip-credentials.json"), "{{{ nope");
        var c = new PaperclipCredentials(_dir);
        Assert.Null(c.Get("http://h"));
        c.Set("http://h", "k", DateTime.UtcNow);
        Assert.Equal("k", c.Get("http://h")!.Key);
    }

    [Fact]
    public void Default_LivesInTheSettingsDirectory()
    {
        Assert.NotNull(PaperclipCredentials.Default());
    }
}
