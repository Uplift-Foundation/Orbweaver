using Xunit;

namespace Orbweaver.Tests;

// The connector joins PaperclipConnect's flow to its consequences: the key into
// credentials, the choices into settings, the loop restarted. Driven here with
// a fake transport, a scratch credentials directory and a recording browser, so
// nothing opens a browser, calls a Paperclip, or touches the real key store.
[Collection("Settings")]
public class PaperclipConnectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-conn-" + Guid.NewGuid().ToString("N"));
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly string _url = OrbweaverSettings.PaperclipUrl;
    private readonly string _company = OrbweaverSettings.PaperclipCompanyId;
    private readonly bool _hint = OrbweaverSettings.PaperclipHintShown;

    private readonly IRosterStore _rosterWas = PaperclipSessions.RosterStore;

    public void Dispose()
    {
        PaperclipSessions.RosterStore = _rosterWas;
        OrbweaverSettings.PaperclipEnabled = _enabled;
        OrbweaverSettings.PaperclipUrl = _url;
        OrbweaverSettings.PaperclipCompanyId = _company;
        OrbweaverSettings.PaperclipHintShown = _hint;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Base = "http://127.0.0.1:3100";
    private const string TwoCompanies = "[{\"id\":\"c1\",\"name\":\"One\",\"issuePrefix\":\"A\"},{\"id\":\"c2\",\"name\":\"Two\",\"issuePrefix\":\"B\"}]";

    private sealed class World
    {
        internal readonly FakePaperclip Anon = new();
        internal readonly FakePaperclip Keyed = new();
        internal readonly List<string> Opened = [];
        internal int Restarts;
        internal DateTime Now = PaperclipFx.Now;
        internal readonly PaperclipCredentials Creds;
        internal readonly PaperclipConnector Connector;
        internal IReadOnlyList<PaperclipDiscovery.Candidate> Candidates = [new(Base, "default")];

        internal World(string dir, string companies, string mode = "authenticated")
        {
            Creds = new PaperclipCredentials(dir);
            Anon.Gets["/api/health"] = FakePaperclip.Ok("{\"status\":\"ok\",\"deploymentMode\":\"" + mode + "\"}");
            Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(
                "{\"id\":\"c\",\"token\":\"t\",\"boardApiToken\":\"KEY\",\"approvalUrl\":\"http://127.0.0.1:3100/a\",\"pollPath\":\"/cli-auth/challenges/c\"}");
            Anon.Gets["/api/cli-auth/challenges/c?token=t"] = FakePaperclip.Ok("{\"status\":\"approved\"}");
            Anon.Gets["/api/companies"] = FakePaperclip.Ok(companies);
            Keyed.Gets["/api/cli-auth/me"] = FakePaperclip.Ok("{}");
            Keyed.Gets["/api/companies"] = FakePaperclip.Ok(companies);
            Keyed.Posts["/api/cli-auth/revoke-current"] = FakePaperclip.Ok("{}");
            Connector = new PaperclipConnector((u, k) => k == null ? Anon : Keyed, u => { Opened.Add(u); return true; },
                Creds, () => Now, (d, c) => { Now += d; return Task.CompletedTask; }, () => Restarts++, () => Candidates);
        }
    }

    [Fact]
    public async Task Connect_OneCompany_StoresKeyAndChoices_EnablesAndRestarts()
    {
        var w = new World(_dir, "[{\"id\":\"c1\",\"name\":\"One\",\"issuePrefix\":\"A\"}]");
        await w.Connector.ConnectAsync(Base + "/", default);
        Assert.Equal(("KEY", w.Now + PaperclipConnect.KeyLifetime), (w.Creds.Get(Base)!.Key, w.Creds.Get(Base)!.Expires));
        Assert.True(OrbweaverSettings.PaperclipEnabled);
        Assert.Equal((Base, "c1"), (OrbweaverSettings.PaperclipUrl, OrbweaverSettings.PaperclipCompanyId));
        Assert.True(OrbweaverSettings.PaperclipHintShown);
        Assert.Equal(1, w.Restarts);
        Assert.Equal((PaperclipPhase.Idle, ""), (w.Connector.Phase, w.Connector.Message));
        Assert.Equal(new[] { "http://127.0.0.1:3100/a" }, w.Opened.ToArray());
    }

    [Fact]
    public async Task Connect_SeveralCompanies_LeavesTheChoiceToSettings_ThenChooseCompanyRestarts()
    {
        OrbweaverSettings.PaperclipCompanyId = "";
        var w = new World(_dir, TwoCompanies);
        await w.Connector.ConnectAsync(Base, default);
        Assert.Equal("", OrbweaverSettings.PaperclipCompanyId);
        Assert.Equal(2, w.Connector.Companies.Count);
        Assert.True(OrbweaverSettings.PaperclipEnabled);

        w.Connector.ChooseCompany("c2");
        Assert.Equal("c2", OrbweaverSettings.PaperclipCompanyId);
        Assert.Equal(2, w.Restarts);
    }

    [Fact]
    public async Task Connect_KeepsTheStoredCompanyWhenItIsStillListed()
    {
        OrbweaverSettings.PaperclipCompanyId = "c2";
        var w = new World(_dir, TwoCompanies);
        await w.Connector.ConnectAsync(Base, default);
        Assert.Equal("c2", OrbweaverSettings.PaperclipCompanyId);
    }

    [Fact]
    public async Task Connect_LocalTrusted_StoresNoKey_AndClearsAStaleOne()
    {
        var w = new World(_dir, "[{\"id\":\"c1\",\"name\":\"One\",\"issuePrefix\":\"A\"}]", "local_trusted");
        w.Creds.Set(Base, "OLD", DateTime.UtcNow);
        await w.Connector.ConnectAsync(Base, default);
        Assert.Null(w.Creds.Get(Base));
        Assert.True(OrbweaverSettings.PaperclipEnabled);
        Assert.Empty(w.Opened);
    }

    [Fact]
    public async Task Connect_Cancelled_Expired_Failed_StoreNothingAndSayWhich()
    {
        OrbweaverSettings.PaperclipEnabled = false;
        var cancelled = new World(_dir, TwoCompanies);
        cancelled.Anon.Gets["/api/cli-auth/challenges/c?token=t"] = FakePaperclip.Ok("{\"status\":\"cancelled\"}");
        await cancelled.Connector.ConnectAsync(Base, default);
        Assert.Equal((PaperclipPhase.Idle, ""), (cancelled.Connector.Phase, cancelled.Connector.Message));

        var expired = new World(_dir, TwoCompanies);
        expired.Anon.Gets["/api/cli-auth/challenges/c?token=t"] = FakePaperclip.Ok("{\"status\":\"expired\"}");
        await expired.Connector.ConnectAsync(Base, default);
        Assert.Equal((PaperclipPhase.Failed, "The approval at 127.0.0.1:3100 timed out. Try again."), (expired.Connector.Phase, expired.Connector.Message));

        var failed = new World(_dir, TwoCompanies);
        failed.Anon.Gets.Remove("/api/health");
        await failed.Connector.ConnectAsync(Base, default);
        Assert.Equal(PaperclipPhase.Failed, failed.Connector.Phase);
        Assert.Contains("reach", failed.Connector.Message);

        var refused = new World(_dir, TwoCompanies);
        refused.Anon.Posts["/api/cli-auth/challenges"] = FakePaperclip.Ok(
            "{\"id\":\"c\",\"token\":\"t\",\"boardApiToken\":\"K\",\"approvalUrl\":\"file:///x\",\"pollPath\":\"/cli-auth/challenges/c\"}");
        await refused.Connector.ConnectAsync(Base, default);
        Assert.Equal(PaperclipPhase.Failed, refused.Connector.Phase);

        Assert.All(new[] { cancelled, expired, failed, refused }, w => Assert.Equal(0, w.Restarts));
        Assert.False(OrbweaverSettings.PaperclipEnabled);
        Assert.Null(cancelled.Creds.Get(Base));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://host")]
    public async Task Connect_AnAddressThatIsNotHttp_IsRefusedBeforeAnyCall(string address)
    {
        var w = new World(_dir, TwoCompanies);
        await w.Connector.ConnectAsync(address, default);
        Assert.Equal((PaperclipPhase.Failed, "Enter the address as http://host:port."), (w.Connector.Phase, w.Connector.Message));
        Assert.Empty(w.Anon.Calls);
    }

    [Fact]
    public async Task CancelConnect_StopsTheApprovalWait()
    {
        var w = new World(_dir, TwoCompanies);
        w.Anon.Gets["/api/cli-auth/challenges/c?token=t"] = FakePaperclip.Ok("{\"status\":\"pending\"}");
        var connector = new PaperclipConnector((u, k) => k == null ? w.Anon : w.Keyed, u => true, w.Creds, () => w.Now,
            (d, c) => { w.Now += d; return Task.CompletedTask; }, () => { }, () => []);
        PaperclipConnector? self = null;
        var polls = 0;
        var cancelling = new PaperclipConnector((u, k) => k == null ? w.Anon : w.Keyed, u => true, w.Creds, () => w.Now,
            (d, c) => { if (++polls == 2) self!.CancelConnect(); w.Now += d; return Task.CompletedTask; }, () => { }, () => []);
        self = cancelling;
        await cancelling.ConnectAsync(Base, default);
        Assert.Equal(PaperclipPhase.Idle, cancelling.Phase);
        Assert.Null(w.Creds.Get(Base));
        connector.CancelConnect();   // nothing running: harmless
    }

    [Fact]
    public async Task WhileApproving_PhaseAndMessageSayWhat()
    {
        var w = new World(_dir, TwoCompanies);
        var seen = new List<(PaperclipPhase, string)>();
        PaperclipConnector? c = null;
        c = new PaperclipConnector((u, k) => k == null ? w.Anon : w.Keyed, u => true, w.Creds, () => w.Now,
            (d, ct) => { seen.Add((c!.Phase, c.Message)); w.Now += d; return Task.CompletedTask; }, () => { }, () => []);
        await c.ConnectAsync(Base, default);
        Assert.Equal((PaperclipPhase.Approving, "Approve Orbweaver in your browser…"), seen[0]);
    }

    [Fact]
    public async Task Disconnect_RevokesDeletesTheKey_DisablesAndRestarts()
    {
        var w = new World(_dir, TwoCompanies);
        OrbweaverSettings.PaperclipUrl = Base; OrbweaverSettings.PaperclipEnabled = true; OrbweaverSettings.PaperclipCompanyId = "c1";
        w.Creds.Set(Base, "KEY", DateTime.UtcNow);
        await w.Connector.DisconnectAsync(default);
        Assert.Contains("POST /api/cli-auth/revoke-current", w.Keyed.Calls);
        Assert.Null(w.Creds.Get(Base));
        Assert.False(OrbweaverSettings.PaperclipEnabled);
        Assert.Equal("", OrbweaverSettings.PaperclipCompanyId);
        Assert.Equal("", w.Connector.Message);
        Assert.Equal(1, w.Restarts);
    }

    [Fact]
    public async Task Disconnect_AFailedRevoke_StillDeletesAndSaysTheKeyMayStillBeListed()
    {
        var w = new World(_dir, TwoCompanies);
        w.Keyed.Posts.Clear();
        OrbweaverSettings.PaperclipUrl = Base;
        w.Creds.Set(Base, "KEY", DateTime.UtcNow);
        await w.Connector.DisconnectAsync(default);
        Assert.Null(w.Creds.Get(Base));
        Assert.Equal("Disconnected here, but the key may still be listed in Paperclip.", w.Connector.Message);
    }

    [Fact]
    public async Task Disconnect_WithNoKeyOrUrl_JustSwitchesOff()
    {
        var w = new World(_dir, TwoCompanies);
        OrbweaverSettings.PaperclipUrl = "";
        OrbweaverSettings.PaperclipEnabled = true;
        await w.Connector.DisconnectAsync(default);
        Assert.False(OrbweaverSettings.PaperclipEnabled);
        Assert.Equal("", w.Connector.Message);
        Assert.Empty(w.Keyed.Calls);
    }

    [Fact]
    public async Task Discover_ListsOnlyAHealthyCandidate_LoadCompanies_NeedsAUrl()
    {
        var w = new World(_dir, TwoCompanies);
        await w.Connector.DiscoverAsync(default);
        Assert.Equal(Base, Assert.Single(w.Connector.Found).BaseUrl);

        OrbweaverSettings.PaperclipUrl = "";
        await w.Connector.LoadCompaniesAsync(default);
        Assert.Empty(w.Connector.Companies);

        OrbweaverSettings.PaperclipUrl = Base;
        w.Creds.Set(Base, "KEY", DateTime.UtcNow);
        await w.Connector.LoadCompaniesAsync(default);
        Assert.Equal(2, w.Connector.Companies.Count);
    }

    // ---- the poll loop's view of settings ----

    [Fact]
    public void ConfigSource_OffUntilEnabled_ThenCarriesUrlCompanyKeyAndExpiry()
    {
        var creds = new PaperclipCredentials(_dir);
        OrbweaverSettings.PaperclipEnabled = false;
        Assert.False(PaperclipConfigSource.Read(creds).Enabled);

        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = Base;
        OrbweaverSettings.PaperclipCompanyId = "c1";
        OrbweaverSettings.PaperclipSleeping = "hide";
        var exp = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        creds.Set(Base, "KEY", exp);
        Assert.Equal(new PaperclipSessions.PaperclipConfig(true, Base, "c1", "KEY", exp, "hide"), PaperclipConfigSource.Read(creds));

        creds.Delete(Base);
        Assert.Equal(("", (DateTime?)null), (PaperclipConfigSource.Read(creds).Key, PaperclipConfigSource.Read(creds).KeyExpires));

        OrbweaverSettings.PaperclipUrl = "";
        Assert.Equal("", PaperclipConfigSource.Read(creds).Key);
        Assert.NotNull(PaperclipConfigSource.Read());
    }

    [Fact]
    public void Sleeping_NormalisesAnythingUnknownToFold()
    {
        foreach (var (given, want) in new[] { ("show", "show"), ("hide", "hide"), ("fold", "fold"), ("banana", "fold"), ("", "fold") })
        {
            OrbweaverSettings.PaperclipSleeping = given;
            Assert.Equal(want, OrbweaverSettings.PaperclipSleeping);
        }
        Assert.Equal("fold", OrbweaverSettings.NormalisePaperclipSleeping(null));
        OrbweaverSettings.PaperclipSleeping = "fold";
    }

    // ---- the tray hint ----

    [Fact]
    public async Task Hint_ShowsOnlyWhenFound_NotEnabled_AndNotYetUsed()
    {
        var w = new World(_dir, TwoCompanies);
        OrbweaverSettings.PaperclipEnabled = false; OrbweaverSettings.PaperclipHintShown = false;
        PaperclipHint.SetFoundForTests([]);
        Assert.False(PaperclipHint.ShouldShow);

        await PaperclipHint.DiscoverAtLaunchAsync(w.Connector, default);
        Assert.True(PaperclipHint.ShouldShow);

        PaperclipHint.MarkUsed();
        Assert.False(PaperclipHint.ShouldShow);

        OrbweaverSettings.PaperclipHintShown = false; OrbweaverSettings.PaperclipEnabled = true;
        Assert.False(PaperclipHint.ShouldShow);
        PaperclipHint.SetFoundForTests([]);
    }

    [Fact]
    public async Task Hint_Discovery_IsSkippedWhenItDoesNotApply()
    {
        var w = new World(_dir, TwoCompanies);
        PaperclipHint.SetFoundForTests([]);
        OrbweaverSettings.PaperclipEnabled = true;
        await PaperclipHint.DiscoverAtLaunchAsync(w.Connector, default);
        OrbweaverSettings.PaperclipEnabled = false; OrbweaverSettings.PaperclipHintShown = true;
        await PaperclipHint.DiscoverAtLaunchAsync(w.Connector, default);
        Assert.Empty(w.Anon.Calls);
        Assert.Contains("found", PaperclipHint.Label);
        OrbweaverSettings.PaperclipHintShown = false;
    }

    // ---- the remembered roster is forgotten with the thing it describes ----

    private FileRosterStore RememberedRoster(string url = Base, string company = "c1")
    {
        var store = new FileRosterStore(Path.Combine(_dir, "roster"));
        store.Save(new RosterFile(url, company, "A", DateTime.UtcNow, [new("a", "A", "r", "t", null)]));
        PaperclipSessions.RosterStore = store;
        return store;
    }

    [Fact]
    public async Task Disconnect_RemovesTheRememberedRoster()
    {
        var w = new World(_dir, TwoCompanies);
        var store = RememberedRoster();
        OrbweaverSettings.PaperclipUrl = Base; OrbweaverSettings.PaperclipEnabled = true; OrbweaverSettings.PaperclipCompanyId = "c1";
        w.Creds.Set(Base, "KEY", DateTime.UtcNow);
        await w.Connector.DisconnectAsync(default);
        Assert.False(File.Exists(store.Path_));
    }

    [Fact]
    public void ChoosingAnotherCompany_RemovesIt_ChoosingTheSameOneKeepsIt()
    {
        var w = new World(_dir, TwoCompanies);
        var store = RememberedRoster();
        OrbweaverSettings.PaperclipCompanyId = "c1";
        w.Connector.ChooseCompany("c1");
        Assert.True(File.Exists(store.Path_));
        w.Connector.ChooseCompany("c2");
        Assert.False(File.Exists(store.Path_));
    }

    [Fact]
    public async Task ReconnectingToTheSameAddressAndCompany_KeepsIt_ANewAddressOrCompanyRemovesIt()
    {
        var one = "[{\"id\":\"c1\",\"name\":\"One\",\"issuePrefix\":\"A\"}]";

        var same = new World(_dir, one);
        var store = RememberedRoster();
        OrbweaverSettings.PaperclipUrl = Base; OrbweaverSettings.PaperclipCompanyId = "c1";
        await same.Connector.ConnectAsync(Base, default);
        Assert.True(File.Exists(store.Path_));                      // an expired key renewed: same picture

        OrbweaverSettings.PaperclipUrl = "http://elsewhere:3100";    // the user connected somewhere else before
        await same.Connector.ConnectAsync(Base, default);
        Assert.False(File.Exists(store.Path_));

        var store2 = RememberedRoster();
        OrbweaverSettings.PaperclipUrl = Base; OrbweaverSettings.PaperclipCompanyId = "previous-company";
        await same.Connector.ConnectAsync(Base, default);
        Assert.False(File.Exists(store2.Path_));
    }

    // ---- a discovery miss is not a failure ----

    private string? DiscoveryLog(Func<Task> act, out string dir)
    {
        dir = Path.Combine(_dir, "diag");
        using (CrashLog.ScopeForTests(dir)) act().GetAwaiter().GetResult();
        var path = DiagnosticsLog.PathIn(dir);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void ADiscoveryMiss_WithPaperclipOff_WritesNothing_ThisIsTheNormalCase()
    {
        OrbweaverSettings.PaperclipEnabled = false;
        var w = new World(_dir, TwoCompanies);
        w.Anon.Gets.Remove("/api/health");   // nothing answers
        var log = DiscoveryLog(() => w.Connector.DiscoverAsync(default), out _);
        Assert.Null(log);
        Assert.Empty(w.Connector.Found);
    }

    [Fact]
    public void ADiscoveryMiss_WithPaperclipOn_WritesOneLine_NamingWhatWasTried()
    {
        OrbweaverSettings.PaperclipEnabled = true;
        var w = new World(_dir, TwoCompanies);
        w.Anon.Gets.Remove("/api/health");
        var log = DiscoveryLog(() => w.Connector.DiscoverAsync(default), out _)!;
        Assert.Single(log.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("discovery found nothing at http://127.0.0.1:3100", log);
    }

    [Fact]
    public void AFind_WritesOneLine_WithTheAddress()
    {
        OrbweaverSettings.PaperclipEnabled = false;
        var w = new World(_dir, TwoCompanies);
        var log = DiscoveryLog(() => w.Connector.DiscoverAsync(default), out _)!;
        Assert.Single(log.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("paperclip: discovery found http://127.0.0.1:3100", log);
    }

    [Fact]
    public async Task DiscoveryUsesItsOwnClient_NotTheGeneralOne()
    {
        var probed = new List<string>();
        var general = new FakePaperclip(); general.Gets["/api/health"] = FakePaperclip.Ok("{\"status\":\"ok\",\"deploymentMode\":\"authenticated\"}");
        var probe = new FakePaperclip();
        var connector = new PaperclipConnector((u, k) => general, u => true, new PaperclipCredentials(_dir), () => DateTime.UtcNow,
            (d, c) => Task.CompletedTask, () => { }, () => [new(Base, "default")], u => { probed.Add(u); return probe; });
        OrbweaverSettings.PaperclipEnabled = false;
        await connector.DiscoverAsync(default);
        Assert.Equal(new[] { Base }, probed.ToArray());
        Assert.Empty(general.Calls);                  // the general client was never asked
        Assert.Equal(new[] { "GET /api/health" }, probe.Calls.ToArray());
        Assert.True(PaperclipConnector.DiscoveryTimeout < PaperclipHttp.RequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), PaperclipConnector.DiscoveryTimeout);
    }
}
