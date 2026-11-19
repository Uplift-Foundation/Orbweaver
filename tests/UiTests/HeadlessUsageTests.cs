using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-391: a Mac that launches locked sends the phone its usage before anyone
// unlocks it, and hands the poll to the orbs when someone does.
//
// Driven through the real RelayService.ServeEarly and Attach, a real
// RelayClient (over a transport that never connects) whose tracker decides
// what is a frame, the real SessionManager and AccountOrbs, and a settings
// file of its own. What is faked is the usage source, the runner a read goes
// to (inline, or held so "in flight" is a state rather than a race), the
// clock and the Grok refresh. The usage timer is set never to fire, and the
// feed's Tick is called by hand, except where a case checks the timer itself.
[Collection("Settings")]
public class HeadlessUsageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("cb391-ui-").FullName;
    private readonly string? _settingsDir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
    private readonly List<string> _heard = new();
    private readonly List<SessionManager> _managers = new();
    private readonly FakeSource _source = new();
    private RelayClient? _client;
    private DateTimeOffset _now = T0;

    private Action<Action> _run = work => work();
    private int _refreshes;
    private Action<IReadOnlyList<RelayUsage.Account>>? _send;
    private Func<DateTimeOffset>? _clock;

    public HeadlessUsageTests()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", Path.Combine(_dir, "settings"));
        OrbweaverSettings.ReloadForTests();

        RelayService.HeadlessScan = () => [];
        RelayService.HeadlessEvery = Timeout.InfiniteTimeSpan;
        RelayService.Factory = () => _client = Client();

        // The production wiring around a fake source: the real settings
        // switches, the real projection, the real RelayService.OnUsage and the
        // real MirrorLog. Only the source, the runner, the clock and the Grok
        // refresh are the test's.
        RelayService.UsageFactory = () => new HeadlessUsage(HeadlessUsage.Production(_source) with
        {
            OffThread = work => _run(work),
            Now = () => (_clock ?? (() => _now))(),
            RefreshGrok = () => _refreshes++,
            Send = a => (_send ?? RelayService.OnUsage)(a),
        });
        RelayService.UsageFirstTick = Timeout.InfiniteTimeSpan;
        RelayService.UsageEvery = Timeout.InfiniteTimeSpan;

        MirrorLog.Heard += OnHeard;
    }

    private static HeadlessUsage Feed() => RelayService.UsageFeed!;

    public void Dispose()
    {
        MirrorLog.Heard -= OnHeard;
        foreach (var m in _managers)
        {
            m.AccountOrbsForTests.CloseAll();
            m.SessionsScanned -= RelayService.OnScanned;
        }
        AccountOrbs.UsageApplied -= RelayService.OnUsage;
        RelayService.ResetHeadlessForTests();
        RelayService.UseForTests(null);
        RelayService.Factory = RelayService.RealClient;
        _client?.Dispose();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _settingsDir);
        OrbweaverSettings.ReloadForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void OnHeard(string what, string detail)
    {
        lock (_heard) _heard.Add(what + " " + detail);
    }

    private int Heard(string text)
    {
        lock (_heard) return _heard.Count(l => l.Contains(text, StringComparison.Ordinal));
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeSource : IUsageSource
    {
        private int _reads;
        internal List<AccountUsage> Readings { get; set; } = new();
        internal int Reads => Volatile.Read(ref _reads);

        public IReadOnlyList<AccountUsage> Read()
        {
            Interlocked.Increment(ref _reads);
            return Readings;
        }
    }

    private sealed class Held
    {
        internal readonly List<Action> Pending = new();
        internal void Run(Action work) { lock (Pending) Pending.Add(work); }
        internal void Release()
        {
            Action[] work;
            lock (Pending) { work = Pending.ToArray(); Pending.Clear(); }
            foreach (var w in work) w();
        }
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) => _items.TryGetValue(reference[4..], out var v) ? v : null;
        public void Forget(string reference) { }
    }

    private sealed class NeverConnects : IRelayTransport
    {
        public Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token).ContinueWith<IRelaySocket>(_ => throw new OperationCanceledException(), TaskScheduler.Default);
    }

    private sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") });
    }

    private RelayClient Client()
    {
        var identity = new RelayIdentity(Path.Combine(_dir, "relay"), new MemoryStore());
        identity.Keys();
        identity.SetRegistration("dsk_391", "tok");
        identity.AddPhone("ph_0", "Phone", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
        return new RelayClient(identity, new RelayClient.Options(new Uri("https://relay.example"), "mac", "1", "Locked Mac"), new NeverConnects(), new Refuses());
    }

    private SessionManager Manager()
    {
        var dir = Path.Combine(_dir, "status-" + _managers.Count);
        Directory.CreateDirectory(dir);
        var manager = new SessionManager(dir,
            () => new Dictionary<string, string>(StringComparer.Ordinal),
            () => new HashSet<string>(StringComparer.Ordinal),
            dependents: _ => SessionDependents.Nothing,
            paneOwners: claims => claims.ToDictionary(TmuxPaneKey.Of, _ => (string?)null),
            agentViewer: _ => null,
            teams: pids => pids.ToDictionary(p => p, _ => AgentTeam.None),
            fileTeams: _ => new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));
        _managers.Add(manager);
        return manager;
    }

    private static AccountUsage Usage(string? dir, double session = 10, string label = "board",
        AccountUsageSource source = AccountUsageSource.ClaudeCode) =>
        new(dir, label, true, "team", new UsageWindow(session, T0.AddHours(3)),
            new UsageWindow(40, T0.AddDays(3)), null, T0, source);

    private static void UsageOn(bool claude = true, bool grok = false, bool codex = false)
    {
        OrbweaverSettings.AccountUsageEnabled = claude;
        OrbweaverSettings.GrokAccountUsageEnabled = grok;
        OrbweaverSettings.CodexAccountUsageEnabled = codex;
    }

    // ---- before the unlock ---------------------------------------------------

    // The bug itself: with the relay and Claude usage on, a parked Mac reads
    // usage and the phone's frame holds it, with the line the mini's log never
    // had.
    [AvaloniaFact]
    public void ServeEarly_polls_usage_and_sends_it_before_Attach()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a", label: "parked-board")];

        RelayService.ServeEarly();
        Feed().Tick();

        Assert.Equal(1, _source.Reads);
        Assert.NotNull(_client);
        Assert.Contains("parked-board", _client!.LatestUsageFrame);
        Assert.Equal(1, _client.UsageFramesSent);
        Assert.Equal(1, Heard("relay usage accounts"));
    }

    // Negative control: the feed starts with the relay, but with every usage
    // source off it never asks — no `claude -p` on a locked Mac for nothing.
    [AvaloniaFact]
    public void ServeEarly_with_every_usage_source_off_polls_nothing()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn(claude: false);

        RelayService.ServeEarly();
        Feed().Tick();

        Assert.Equal(0, _source.Reads);
        Assert.Equal(0, Heard("usage accounts"));
    }

    // Negative control: the relay off, usage and Grok refresh on. Before the
    // unlock the phone is the only consumer, so nothing starts at all.
    [AvaloniaFact]
    public void ServeEarly_with_the_relay_off_polls_no_usage()
    {
        var made = 0;
        RelayService.UsageFactory = () => { made++; return null!; };
        OrbweaverSettings.RelayEnabled = false;
        UsageOn(grok: true);
        OrbweaverSettings.GrokAutoRefreshEnabled = true;

        RelayService.ServeEarly();

        Assert.Null(RelayService.UsageFeed);
        Assert.Equal(0, made);
        Assert.Equal(0, _source.Reads);
        Assert.Equal(0, _refreshes);
    }

    // A second ServeEarly does not start a second feed.
    [AvaloniaFact]
    public void ServeEarly_twice_starts_one_feed()
    {
        OrbweaverSettings.RelayEnabled = true;
        RelayService.ServeEarly();
        var first = RelayService.UsageFeed;

        RelayService.ServeEarly();

        Assert.Same(first, RelayService.UsageFeed);
    }

    // Production's first wait and period, which the rest of this class
    // replaces: 5 s, then the UI tick's 2 s (plan 11427 §3).
    [AvaloniaFact]
    public void The_usage_timer_defaults_to_a_5s_first_tick_then_2s()
    {
        RelayService.ResetHeadlessForTests();
        Assert.Equal(TimeSpan.FromSeconds(5), RelayService.UsageFirstTick);
        Assert.Equal(TimeSpan.FromSeconds(2), RelayService.UsageEvery);
    }

    // The timer is really wired, on the production runner and clock that every
    // other case here replaces. Only the source and the Grok refresh are fake.
    [AvaloniaFact]
    public void The_headless_usage_timer_ticks_on_its_own()
    {
        RelayService.UsageFactory = () => new HeadlessUsage(HeadlessUsage.Production(_source) with
        {
            RefreshGrok = () => _refreshes++,
        });
        RelayService.UsageFirstTick = TimeSpan.FromMilliseconds(20);
        RelayService.UsageEvery = TimeSpan.FromMilliseconds(20);
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();

        RelayService.ServeEarly();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_source.Reads == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(_source.Reads > 0, "the headless usage timer never read");
    }

    // The cadence is the orbs' rule: a reading that moved nothing backs off
    // and sends no frame; one that moved goes back to Fast and sends one.
    [AvaloniaFact]
    public void Headless_usage_follows_the_cadence()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a", session: 10)];
        RelayService.ServeEarly();

        Feed().Tick();
        Assert.Equal(UsagePollCadence.Fast, Feed().Pump.Interval);
        Assert.Equal(1, _client!.UsageFramesSent);

        _now = T0 + UsagePollCadence.Fast;                          // same numbers again
        Feed().Tick();
        Assert.Equal(2, _source.Reads);
        Assert.Equal(TimeSpan.FromSeconds(120), Feed().Pump.Interval);
        Assert.Equal(1, _client.UsageFramesSent);

        _now += TimeSpan.FromSeconds(120);                          // a number moved
        _source.Readings = [Usage("/acct/a", session: 25)];
        Feed().Tick();
        Assert.Equal(UsagePollCadence.Fast, Feed().Pump.Interval);
        Assert.Equal(2, _client.UsageFramesSent);
    }

    // The real icon-bundle event reaches the feed: a hash first offerable now
    // goes to the phone without waiting for the next poll.
    [AvaloniaFact]
    public void Headless_usage_resends_when_an_icon_bundle_is_built()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        var sent = new List<IReadOnlyList<RelayUsage.Account>>();
        _send = sent.Add;
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        Feed().Tick();
        Assert.Single(sent);

        RaiseAccountIconBuilt();

        Assert.Equal(2, sent.Count);
    }

    private static void RaiseAccountIconBuilt()
    {
        var field = typeof(PersonaImageStore).GetField("Built",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((Action?)field.GetValue(PersonaImageStore.Accounts))?.Invoke();
    }

    // Grok's forced refresh runs before the unlock too, through the real
    // settings switches — and the control: one switch alone starts nothing.
    [AvaloniaFact]
    public void Grok_refresh_runs_headless_only_with_both_switches()
    {
        void Run(bool usage, bool auto)
        {
            RelayService.ResetHeadlessForTests();
            RelayService.HeadlessScan = () => [];
            RelayService.HeadlessEvery = Timeout.InfiniteTimeSpan;
            RelayService.UsageFactory = () => new HeadlessUsage(HeadlessUsage.Production(_source) with
            {
                OffThread = work => work(), Now = () => _now, RefreshGrok = () => _refreshes++,
            });
            RelayService.UsageFirstTick = Timeout.InfiniteTimeSpan;
            OrbweaverSettings.RelayEnabled = true;
            UsageOn(claude: true, grok: usage);
            OrbweaverSettings.GrokAutoRefreshEnabled = auto;
            RelayService.ServeEarly();
            Feed().Tick();
        }

        Run(usage: true, auto: false);
        Assert.Equal(0, _refreshes);
        Run(usage: false, auto: true);
        Assert.Equal(0, _refreshes);
        Run(usage: true, auto: true);
        Assert.Equal(1, _refreshes);
    }

    // The log line a failure leaves, through the real MirrorLog.
    [AvaloniaFact]
    public void A_headless_usage_failure_is_said_in_the_mirror_log()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _clock = () => throw new InvalidOperationException("clock");
        RelayService.ServeEarly();

        Feed().Tick();

        Assert.Equal(1, Heard("relay headless usage failed: InvalidOperationException"));
    }

    // ---- the unlock ------------------------------------------------------------

    // The orbs draw what was already read, at once, without reading again or
    // telling the phone anything; the feed is gone; and both halves project
    // the same readings to the same shape.
    [AvaloniaFact]
    public void Attach_hands_the_readings_to_the_orbs()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        Feed().Tick();
        var pump = Feed().Pump;
        var applied = 0;
        void OnApplied(IReadOnlyList<RelayUsage.Account> _) => applied++;
        AccountOrbs.UsageApplied += OnApplied;
        try
        {
            var manager = Manager();
            RelayService.Attach(manager);

            var orbs = manager.AccountOrbsForTests;
            Assert.True(orbs.Orbs.ContainsKey("/acct/a"));
            Assert.Same(pump, orbs.PumpForTests);
            Assert.Equal(1, _source.Reads);
            Assert.Equal(0, applied);
            Assert.Null(RelayService.UsageFeed);
            Assert.Equal(
                AccountOrbs.RelayAccountsOf(new Dictionary<string, AccountUsage> { ["/acct/a"] = Usage("/acct/a") }).Select(a => a.Shape),
                orbs.RelayAccounts().Select(a => a.Shape));
        }
        finally { AccountOrbs.UsageApplied -= OnApplied; }
    }

    // The Grok schedule carries, last refresh and all: the UI side does not
    // refresh again inside the floor — and does at it, which shows the
    // handover did not simply switch refresh off.
    [AvaloniaFact]
    public void Attach_hands_the_Grok_last_refresh_to_the_session_manager()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn(grok: true);
        OrbweaverSettings.GrokAutoRefreshEnabled = true;
        RelayService.ServeEarly();
        Feed().Tick();
        Assert.Equal(1, _refreshes);

        var manager = Manager();
        RelayService.Attach(manager);

        Assert.False(manager.GrokRefreshForTests.Tick(T0.AddMinutes(5), true, true));
        Assert.True(manager.GrokRefreshForTests.Tick(T0 + GrokUsageRefresher.MinimumInterval, true, true));
    }

    // One pump, one floor: a tick of the adopted orbs before the floor reads
    // nothing; at it, exactly once. The old feed's tick does nothing.
    [AvaloniaFact]
    public void Attach_does_not_double_poll()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        var feed = Feed();
        feed.Tick();

        var manager = Manager();
        RelayService.Attach(manager);
        var orbs = manager.AccountOrbsForTests;

        orbs.Tick(T0.AddSeconds(30));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, _source.Reads);

        orbs.Tick(T0 + UsagePollCadence.Fast);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, _source.Reads);

        _now = T0.AddHours(1);
        feed.Tick();
        Assert.Equal(2, _source.Reads);
    }

    // The tracker lives on the one early client, so the orbs' first Apply of
    // the same numbers is no change to the phone — and a moved number is
    // exactly one frame, the positive control.
    [AvaloniaFact]
    public void Attach_sends_no_duplicate_frame()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a", session: 10)];
        RelayService.ServeEarly();
        Feed().Tick();
        Assert.Equal(1, _client!.UsageFramesSent);

        var manager = Manager();
        RelayService.Attach(manager);
        Assert.Equal(1, _client.UsageFramesSent);

        manager.AccountOrbsForTests.Apply([Usage("/acct/a", session: 10)], T0.AddMinutes(1));
        Assert.Equal(1, _client.UsageFramesSent);

        manager.AccountOrbsForTests.Apply([Usage("/acct/a", session: 30)], T0.AddMinutes(2));
        Assert.Equal(2, _client.UsageFramesSent);
    }

    // A read started headless and finishing after the unlock goes to Deliver,
    // which posts: it lands after Attach has returned, on top of the readings
    // already adopted, once, and is never sent from the headless side.
    [AvaloniaFact]
    public void A_read_in_flight_at_Attach_reaches_the_orbs_once_after_the_adopted_readings()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        var feed = Feed();
        feed.Tick();                                               // /acct/a, read and sent headless

        var held = new Held();
        _run = held.Run;
        _now = T0 + UsagePollCadence.Fast;
        _source.Readings = [Usage("/acct/a"), Usage("/acct/b")];
        feed.Tick();                                               // in flight at the unlock
        Assert.True(feed.Pump.Polling);
        Assert.Equal(1, _client!.UsageFramesSent);

        var manager = Manager();
        RelayService.Attach(manager);
        var orbs = manager.AccountOrbsForTests;
        orbs.Tick(T0.AddHours(1));                                 // in flight: no second read
        Assert.Single(held.Pending);
        Assert.Equal(["/acct/a"], orbs.Orbs.Keys);

        held.Release();
        Assert.Equal(["/acct/a"], orbs.Orbs.Keys);                 // posted, not yet run
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, _source.Reads);
        Assert.Equal(["/acct/a", "/acct/b"], orbs.Orbs.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.False(feed.ReadingsForTests.ContainsKey("/acct/b")); // never merged headless
        Assert.Equal(2, _client.UsageFramesSent);                  // the orbs' Apply, once
    }

    // The null arm: no feed (relay off) leaves the orbs with their own pump.
    [AvaloniaFact]
    public void Attach_without_a_headless_feed_keeps_the_orbs_own_pump()
    {
        OrbweaverSettings.RelayEnabled = false;
        var manager = Manager();
        var own = manager.AccountOrbsForTests.PumpForTests;
        var grok = manager.GrokRefreshForTests;

        RelayService.Attach(manager);

        Assert.Same(own, manager.AccountOrbsForTests.PumpForTests);
        Assert.Same(grok, manager.GrokRefreshForTests);
    }

    // Started but nothing read yet — the ordinary unlocked launch, which the
    // 5 s first tick exists for: the pump is adopted, nothing is drawn.
    [AvaloniaFact]
    public void Attach_before_any_headless_read_adopts_the_pump_and_draws_nothing()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        RelayService.ServeEarly();
        var pump = Feed().Pump;

        var manager = Manager();
        RelayService.Attach(manager);

        Assert.Same(pump, manager.AccountOrbsForTests.PumpForTests);
        Assert.Empty(manager.AccountOrbsForTests.Orbs);
    }

    // The test reset disposes the feed, so a timer one test started cannot
    // tick into the next.
    [AvaloniaFact]
    public void ResetHeadlessForTests_disposes_the_usage_feed()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        RelayService.UsageFirstTick = TimeSpan.FromMilliseconds(100);
        RelayService.UsageEvery = TimeSpan.FromMilliseconds(20);
        RelayService.ServeEarly();
        Assert.NotNull(RelayService.UsageFeed);

        RelayService.ResetHeadlessForTests();
        Thread.Sleep(300);

        Assert.Null(RelayService.UsageFeed);
        Assert.Equal(0, _source.Reads);
    }
}
