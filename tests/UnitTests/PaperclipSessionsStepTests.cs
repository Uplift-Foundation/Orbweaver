using Xunit;
using static Orbweaver.PaperclipSessions;

namespace Orbweaver.Tests;

[Collection("Settings")]
public class PaperclipSessionsStepTests
{
    private const string Co = "co1";
    private static readonly DateTime T0 = PaperclipFx.Now;

    private static PaperclipConfig Cfg(bool enabled = true, DateTime? expires = null, string company = Co) =>
        new(enabled, PaperclipFx.Base, company, "KEY", expires, "fold");

    private static string P(string tail) => $"/api/companies/{Co}/{tail}";

    private static FakePaperclip Company(string live = "[]", string runs = "[]")
    {
        var f = new FakePaperclip();
        f.Gets["/api/companies"] = FakePaperclip.Ok("[{\"id\":\"co1\",\"name\":\"C\",\"issuePrefix\":\"EX\"}]");
        f.Gets[P("agents")] = FakePaperclip.Ok(PaperclipFx.Read("native/agents.json"));
        f.Gets[P("live-runs")] = FakePaperclip.Ok(live);
        f.Gets[P("approvals?status=pending")] = FakePaperclip.Ok("[]");
        f.Gets[P("tools/action-requests")] = FakePaperclip.Ok("{\"actionRequests\":[]}");
        f.Gets[P("issues?status=blocked")] = FakePaperclip.Ok("[]");
        f.Gets[P("heartbeat-runs?limit=20")] = FakePaperclip.Ok(runs);
        f.Gets[P("issues?status=in_progress")] = FakePaperclip.Ok("[]");
        return f;
    }

    private static Task<StepResult> Step(FakePaperclip f, PollState st, DateTime now, PaperclipConfig? cfg = null) =>
        StepAsync(f, cfg ?? Cfg(), st, now, default);

    [Fact]
    public async Task Disabled_MakesNoCall_AndSaysSwitchedOff()
    {
        var f = Company();
        var r = await Step(f, PollState.Initial, T0, Cfg(enabled: false));
        Assert.Empty(f.Calls);
        Assert.Equal((PaperclipLink.Off, "Paperclip is switched off."), (r.Next.Link, r.Next.Status));
    }

    [Fact]
    public async Task NoCompanyChosen_AsksForOne_MakesNoCall()
    {
        var f = Company();
        var r = await Step(f, PollState.Initial, T0, Cfg(company: ""));
        Assert.Empty(f.Calls);
        Assert.Equal("Choose a company in Settings.", r.Next.Status);
    }

    [Fact]
    public async Task FirstTick_CallsEverything_AndPublishesTheCompany()
    {
        var f = Company();
        var r = await Step(f, PollState.Initial, T0);
        Assert.Equal(new[]
        {
            "GET /api/companies", "GET " + P("agents"), "GET " + P("live-runs"),
            "GET " + P("approvals?status=pending"), "GET " + P("tools/action-requests"),
            "GET " + P("issues?status=blocked"), "GET " + P("heartbeat-runs?limit=20"),
        }, f.Calls.ToArray());
        Assert.Equal(4, r.Next.Published!.Count);
        Assert.Equal(PaperclipLink.Connected, r.Next.Link);
        Assert.Equal("Connected to 127.0.0.1:3100. 4 agents.", r.Next.Status);
        Assert.Equal(FastEvery, r.Wait);
        Assert.StartsWith("http://127.0.0.1:3100/EX/agents/", r.Next.Published![0].Url);
    }

    [Fact]
    public async Task Cadence_FastEvery2s_SlowEvery4s_RunsOnlyWhenSomethingMoved()
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;

        f.Calls.Clear();
        s = (await Step(f, s, T0.AddSeconds(2))).Next;            // fast only; nothing moved
        Assert.Equal(new[] { "GET " + P("agents"), "GET " + P("live-runs") }, f.Calls.ToArray());

        f.Calls.Clear();
        s = (await Step(f, s, T0.AddSeconds(4))).Next;            // both due
        Assert.Equal(5, f.Calls.Count);
        Assert.DoesNotContain("GET " + P("heartbeat-runs?limit=20"), f.Calls);

        f.Calls.Clear();
        await Step(f, s, T0.AddSeconds(5));                        // neither due
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task RunsAreFetched_WhenALiveRunExists_OrAHeartbeatMoved()
    {
        var live = "[{\"id\":\"L\",\"agentId\":\"a\",\"status\":\"running\"}]";
        var f = Company(live);
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Calls.Clear();
        await Step(f, s, T0.AddSeconds(2));
        Assert.Contains("GET " + P("heartbeat-runs?limit=20"), f.Calls);

        var g = Company();
        var s2 = (await Step(g, PollState.Initial, T0)).Next;
        var moved = PaperclipFx.Read("native/agents.json").Replace("2026-11-23T03:05:42.424Z", "2026-11-23T03:59:00.000Z");
        g.Gets[P("agents")] = FakePaperclip.Ok(moved);
        g.Calls.Clear();
        await Step(g, s2, T0.AddSeconds(2));
        Assert.Contains("GET " + P("heartbeat-runs?limit=20"), g.Calls);
    }

    [Fact]
    public async Task ATaskLine_IsLookedUpOnceForAnUnknownIssue()
    {
        var live = "[{\"id\":\"L\",\"agentId\":\"00000000-0000-4000-8000-000000000004\",\"status\":\"running\",\"issueId\":\"i9\"}]";
        var f = Company(live);
        f.Gets[P("issues?status=in_progress")] = FakePaperclip.Ok("[{\"id\":\"i9\",\"identifier\":\"EX-9\",\"title\":\"Ship it\",\"status\":\"in_progress\"}]");
        var r = await Step(f, PollState.Initial, T0);
        Assert.Equal("Ship it", r.Next.Published!.Single(a => a.Id.EndsWith("4")).Task);
        f.Calls.Clear();
        await Step(f, r.Next, T0.AddSeconds(2));
        Assert.DoesNotContain("GET " + P("issues?status=in_progress"), f.Calls);
    }

    [Fact]
    public async Task ATaskLookupThatFails_IsNotALinkFailure()
    {
        var live = "[{\"id\":\"L\",\"agentId\":\"00000000-0000-4000-8000-000000000004\",\"status\":\"running\",\"issueId\":\"i9\"}]";
        var f = Company(live);
        f.Gets.Remove(P("issues?status=in_progress"));
        var r = await Step(f, PollState.Initial, T0);
        Assert.Equal(PaperclipLink.Connected, r.Next.Link);
        Assert.Null(r.Next.Published!.Single(a => a.Id.EndsWith("4")).Task);
    }

    [Fact]
    public async Task NeedsComeFromTheSlowLists()
    {
        var f = Company();
        f.Gets[P("approvals?status=pending")] = FakePaperclip.Ok(
            "[{\"id\":\"p1\",\"type\":\"hire_agent\",\"status\":\"pending\",\"payload\":{\"title\":\"Hire QA\",\"agentId\":\"00000000-0000-4000-8000-000000000004\"}}," +
            "{\"id\":\"p2\",\"type\":\"hire_agent\",\"status\":\"approved\",\"payload\":{\"agentId\":\"00000000-0000-4000-8000-000000000004\"}}]");
        var r = await Step(f, PollState.Initial, T0);
        var a = r.Next.Published!.Single(x => x.Id.EndsWith("4"));
        Assert.Equal("waiting", a.State);
        Assert.Equal(new[] { "Hire QA" }, a.Needs.Select(n => n.Title).ToArray());
    }

    // ---- optional lists never dim the company ----

    public static IEnumerable<object[]> OptionalLists() => new[]
    {
        new object[] { "approvals?status=pending", "approvals" },
        new object[] { "tools/action-requests", "tool actions" },
        new object[] { "issues?status=blocked", "blocked tasks" },
    };

    [Theory]
    [MemberData(nameof(OptionalLists))]
    public async Task AnOptionalList_ThatIs404_IsNone_AndTheCompanyStaysConnected(string tail, string _)
    {
        var f = Company();
        f.Gets[P(tail)] = FakePaperclip.Status(PaperclipHttpKind.NotFound, 404);
        var s = await Step(f, PollState.Initial, T0);
        Assert.Equal(PaperclipLink.Connected, s.Next.Link);
        Assert.Equal("Connected to 127.0.0.1:3100. 4 agents.", s.Next.Status);
        Assert.Equal(4, s.Next.Published!.Count);
        Assert.Null(s.Next.NextAttempt);
    }

    [Theory]
    [MemberData(nameof(OptionalLists))]
    public async Task AnOptionalList_ThatErrors_KeepsItsLastAnswer_NamesItInTheStatus_AndNeverDims(string tail, string name)
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Gets[P(tail)] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        // several ticks past the 15 s grace: still connected, never unreachable
        for (var i = 1; i <= 6; i++)
        {
            var r = await Step(f, s, T0.AddSeconds(4 * i));
            s = r.Next;
            Assert.Equal(PaperclipLink.Connected, s.Link);
            Assert.Null(s.NextAttempt);
            Assert.Equal(4, s.Published!.Count);
            Assert.Equal($"Connected to 127.0.0.1:3100. 4 agents. Couldn't read {name}.", s.Status);
        }
        // and it recovers
        f.Gets[P(tail)] = FakePaperclip.Ok(tail.StartsWith("tools") ? "{\"actionRequests\":[]}" : "[]");
        Assert.Equal("Connected to 127.0.0.1:3100. 4 agents.", (await Step(f, s, T0.AddSeconds(40))).Next.Status);
    }

    [Fact]
    public async Task AnErroringApprovalsList_KeepsTheLastKnownNeed()
    {
        var f = Company();
        f.Gets[P("approvals?status=pending")] = FakePaperclip.Ok(
            "[{\"id\":\"p1\",\"type\":\"hire_agent\",\"status\":\"pending\",\"payload\":{\"title\":\"Hire QA\",\"agentId\":\"00000000-0000-4000-8000-000000000004\"}}]");
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Gets[P("approvals?status=pending")] = FakePaperclip.Status(PaperclipHttpKind.Timeout);
        s = (await Step(f, s, T0.AddSeconds(4))).Next;
        Assert.Equal("waiting", s.Published!.Single(a => a.Id.EndsWith("4")).State);
    }

    [Fact]
    public async Task TheDegradedNoteSurvivesTicksThatDoNotRetryTheList()
    {
        var f = Company();
        f.Gets[P("tools/action-requests")] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var s = (await Step(f, PollState.Initial, T0)).Next;
        var r = await Step(f, s, T0.AddSeconds(2));   // fast tick only
        Assert.Contains("Couldn't read tool actions.", r.Next.Status);
    }

    [Fact]
    public async Task RecentRuns_Failing_IsOptionalToo()
    {
        var f = Company();
        f.Gets[P("heartbeat-runs?limit=20")] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var s = await Step(f, PollState.Initial, T0);
        Assert.Equal(PaperclipLink.Connected, s.Next.Link);
        Assert.Contains("Couldn't read recent runs.", s.Next.Status);
    }

    [Theory]
    [InlineData("approvals?status=pending")]
    [InlineData("tools/action-requests")]
    [InlineData("issues?status=blocked")]
    public async Task ARefusedKeyOnAnOptionalList_StillMeansReconnect(string tail)
    {
        var f = Company();
        f.Gets[P(tail)] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        Assert.Equal(PaperclipLink.NeedsReconnect, (await Step(f, PollState.Initial, T0)).Next.Link);
    }

    [Fact]
    public async Task TheControl_AnAgentsReadThatFails_StillDimsAfter15Seconds()
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var now = T0.AddSeconds(2);
        PaperclipLink last = PaperclipLink.Connected;
        for (var i = 0; i < 6; i++)
        {
            var r = await Step(f, s, now);
            s = r.Next; last = s.Link; now = s.NextAttempt!.Value;
        }
        Assert.Equal(PaperclipLink.Unreachable, last);
    }

    // ---- failure ----

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Unauthorized_NeedsReconnect_KeepsTheSnapshot_AndDoesNotBackOff(int code)
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, code);
        var r = await Step(f, s, T0.AddSeconds(2));
        Assert.Equal((PaperclipLink.NeedsReconnect, "Reconnect to Paperclip"), (r.Next.Link, r.Next.Status));
        Assert.Equal(4, r.Next.Published!.Count);
        Assert.Null(r.Next.NextAttempt);
    }

    [Fact]
    public async Task Unauthorized_OnASlowCall_WinsOverAnEarlierTransportFailure()
    {
        var f = Company();
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.Timeout);
        f.Gets[P("approvals?status=pending")] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        var r = await Step(f, PollState.Initial, T0);
        Assert.Equal(PaperclipLink.NeedsReconnect, r.Next.Link);
    }

    [Fact]
    public async Task Unreachable_KeepsOrbsDuring15s_BacksOff2_4_8_16_30_30_ThenDims()
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.Unreachable, 0, "refused");

        var now = T0.AddSeconds(2);
        var waits = new List<TimeSpan>();
        var links = new List<PaperclipLink>();
        for (var i = 0; i < 6; i++)
        {
            var r = await Step(f, s, now);
            s = r.Next; waits.Add(r.Wait); links.Add(s.Link);
            Assert.Equal(4, s.Published!.Count);
            now = s.NextAttempt!.Value;
        }
        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30 }, waits.Select(w => (int)w.TotalSeconds).ToArray());
        // failing since T0+2: at +2,+4,+8,+16 -> within 15 s; +32 -> past the grace.
        Assert.Equal(new[] { PaperclipLink.Connected, PaperclipLink.Connected, PaperclipLink.Connected,
            PaperclipLink.Connected, PaperclipLink.Unreachable, PaperclipLink.Unreachable }, links.ToArray());
        Assert.Equal("Can't reach 127.0.0.1:3100.", s.Status);
    }

    [Fact]
    public async Task AttemptsBeforeTheBackoffHasElapsed_MakeNoCall()
    {
        var f = Company();
        f.Gets.Remove(P("agents"));
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Calls.Clear();
        var r = await Step(f, s, T0.AddSeconds(1));
        Assert.Empty(f.Calls);
        Assert.Equal(TimeSpan.FromSeconds(1), r.Wait);
    }

    [Fact]
    public async Task FirstTickFailure_SaysCantReach_AndMacHintIsKept()
    {
        var f = Company();
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.Unreachable, 0, "No route — Local Network access is off");
        var r = await Step(f, PollState.Initial, T0);
        Assert.Equal("Can't reach 127.0.0.1:3100. No route — Local Network access is off", r.Next.Status);
        Assert.Empty(r.Next.Published ?? []);
    }

    [Fact]
    public async Task Recovery_ClearsTheFailureAndReconnectsTheLink()
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Gets[P("agents")] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        s = (await Step(f, s, T0.AddSeconds(2))).Next;
        f.Gets[P("agents")] = FakePaperclip.Ok(PaperclipFx.Read("native/agents.json"));
        var r = await Step(f, s, T0.AddSeconds(4));
        Assert.Equal(PaperclipLink.Connected, r.Next.Link);
        Assert.Equal((0, null), (r.Next.Failures, r.Next.FailingSince));
    }

    [Fact]
    public async Task SwitchingCompany_StartsFresh()
    {
        var f = Company();
        var s = (await Step(f, PollState.Initial, T0)).Next;
        f.Calls.Clear();
        f.Gets["/api/companies/other/agents"] = FakePaperclip.Ok("[]");
        await StepAsync(f, Cfg(company: "other"), s, T0.AddSeconds(1), default);
        Assert.Contains("GET /api/companies/other/agents", f.Calls);
    }

    // ---- words ----

    [Fact]
    public void ConnectedText_SingularPlural_AndExpiryCountdown()
    {
        var now = T0;
        Assert.Equal("Connected to 127.0.0.1:3100. 1 agent.", ConnectedText(Cfg(), 1, now));
        Assert.Equal("Connected to 127.0.0.1:3100. 4 agents.", ConnectedText(Cfg(expires: now.AddDays(10)), 4, now));
        Assert.Equal("Connected. The key expires in 3 days.", ConnectedText(Cfg(expires: now.AddDays(3)), 4, now));
        Assert.Equal("Connected. The key expires in 1 day.", ConnectedText(Cfg(expires: now.AddHours(20)), 4, now));
        Assert.Equal("Connected. The key expires today.", ConnectedText(Cfg(expires: now.AddHours(-1)), 4, now));
    }

    [Fact]
    public void ANewClientIsBuilt_OnlyWhenThereIsNone_OrTheKeyOrAddressMoved()
    {
        var cfg = Cfg();
        Assert.True(NeedsNewClient(false, "KEY", PaperclipFx.Base, cfg));
        Assert.False(NeedsNewClient(true, "KEY", PaperclipFx.Base, cfg));
        Assert.True(NeedsNewClient(true, "OLD", PaperclipFx.Base, cfg));
        Assert.True(NeedsNewClient(true, "KEY", "http://elsewhere:1", cfg));
    }

    [Fact]
    public void HostOf_FallsBackToTheRawString() =>
        Assert.Equal("nonsense", HostOf("nonsense"));

    [Fact]
    public void TheShell_StartsOff_AndTheTestSeamsRoundTrip()
    {
        SetSnapshotForTests([]);
        SetLinkForTests(PaperclipLink.Connecting, "x");
        Assert.Equal(("x", PaperclipLink.Connecting), (StatusText, Link));
        Assert.Empty(Snapshot());
        SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
        Assert.False(PaperclipConfig.Off.Enabled);
    }
}
