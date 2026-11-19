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
// clock and the Grok refresh. The headless timer is set never to fire, and
// HeadlessUsage.Tick is called by hand, except in the one case that checks the
// timer itself is wired.
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

    public HeadlessUsageTests()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", Path.Combine(_dir, "settings"));
        OrbweaverSettings.ReloadForTests();

        RelayService.HeadlessScan = () => [];
        RelayService.HeadlessEvery = Timeout.InfiniteTimeSpan;
        RelayService.Factory = () => _client = Client();

        HeadlessUsage.Source = () => _source;
        HeadlessUsage.OffThread = work => work();
        HeadlessUsage.Now = () => _now;
        HeadlessUsage.Every = Timeout.InfiniteTimeSpan;
        HeadlessUsage.GrokRefresh = () => throw new InvalidOperationException("no test may start Grok");

        MirrorLog.Heard += OnHeard;
    }

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
        Assert.True(HeadlessUsage.Started);
        HeadlessUsage.Tick();

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
        HeadlessUsage.Tick();

        Assert.True(HeadlessUsage.Started);
        Assert.Equal(0, _source.Reads);
        Assert.Equal(0, Heard("usage accounts"));
    }

    // Negative control: the relay off, usage on. Before the unlock the phone
    // is the only consumer, so nothing starts.
    [AvaloniaFact]
    public void ServeEarly_with_the_relay_off_polls_no_usage()
    {
        OrbweaverSettings.RelayEnabled = false;
        UsageOn();

        RelayService.ServeEarly();
        HeadlessUsage.Tick();

        Assert.False(HeadlessUsage.Started);
        Assert.Equal(0, _source.Reads);
    }

    // The timer is really wired: with a short period and no hand-driven tick,
    // a read still happens — on the production runner and clock, which every
    // other case here replaces. Only the source and the Grok refresh stay fake.
    [AvaloniaFact]
    public void The_headless_usage_timer_ticks_on_its_own()
    {
        HeadlessUsage.ResetForTests();
        HeadlessUsage.Source = () => _source;
        HeadlessUsage.GrokRefresh = () => throw new InvalidOperationException("no test may start Grok");
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        HeadlessUsage.Every = TimeSpan.FromMilliseconds(20);

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

        HeadlessUsage.Tick();
        Assert.Equal(UsagePollCadence.Fast, HeadlessUsage.PumpForTests!.Interval);
        Assert.Equal(1, _client!.UsageFramesSent);

        _now = _now.AddSeconds(1);                                  // before the floor: no read
        HeadlessUsage.Tick();
        Assert.Equal(1, _source.Reads);

        _now = T0 + UsagePollCadence.Fast;                          // same numbers again
        HeadlessUsage.Tick();
        Assert.Equal(2, _source.Reads);
        Assert.Equal(TimeSpan.FromSeconds(120), HeadlessUsage.PumpForTests!.Interval);
        Assert.Equal(1, _client.UsageFramesSent);

        _now += TimeSpan.FromSeconds(120);                          // a number moved
        _source.Readings = [Usage("/acct/a", session: 25)];
        HeadlessUsage.Tick();
        Assert.Equal(3, _source.Reads);
        Assert.Equal(UsagePollCadence.Fast, HeadlessUsage.PumpForTests!.Interval);
        Assert.Equal(2, _client.UsageFramesSent);
    }

    // An icon bundle finishing is the first moment its hash can reach the
    // phone, so the readings already held are projected and sent again. With
    // nothing held, or after the handover, there is nothing to send from here.
    [AvaloniaFact]
    public void Headless_usage_resends_when_an_icon_bundle_is_built()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        var sent = new List<IReadOnlyList<RelayUsage.Account>>();
        HeadlessUsage.Send = sent.Add;
        RelayService.ServeEarly();

        HeadlessUsage.IconBuiltForTests();                         // nothing held yet
        Assert.Empty(sent);

        _source.Readings = [Usage("/acct/a")];
        HeadlessUsage.Tick();
        Assert.Single(sent);

        HeadlessUsage.IconBuiltForTests();
        Assert.Equal(2, sent.Count);

        Assert.NotNull(HeadlessUsage.Handoff());
        HeadlessUsage.IconBuiltForTests();
        Assert.Equal(2, sent.Count);
    }

    // Every path off the timer swallows and logs: a Timer discards what it
    // throws, and a silent timer is undiagnosable.
    [AvaloniaFact]
    public void Headless_usage_tick_survives_a_throwing_sink_clock_and_icon_resend()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        HeadlessUsage.Send = _ => throw new IOException("relay down");
        RelayService.ServeEarly();

        HeadlessUsage.Tick();                                      // the sink throws
        HeadlessUsage.IconBuiltForTests();                         // the resend throws
        HeadlessUsage.Now = () => throw new InvalidOperationException("clock");
        HeadlessUsage.Tick();                                      // the tick throws

        Assert.Equal(2, Heard("headless usage failed: IOException"));
        Assert.Equal(1, Heard("headless usage failed: InvalidOperationException"));
        Assert.False(HeadlessUsage.PumpForTests!.Polling);
    }

    // Grok's forced refresh runs before the unlock too, gated by the same two
    // switches as after it — and the control: one switch alone starts nothing.
    [AvaloniaFact]
    public void Grok_refresh_runs_headless_only_with_both_switches()
    {
        var refreshes = 0;
        void Run(bool usage, bool auto)
        {
            RelayService.ResetHeadlessForTests();
            RelayService.HeadlessScan = () => [];
            RelayService.HeadlessEvery = Timeout.InfiniteTimeSpan;
            HeadlessUsage.Source = () => _source;
            HeadlessUsage.OffThread = work => work();
            HeadlessUsage.Now = () => _now;
            HeadlessUsage.Every = Timeout.InfiniteTimeSpan;
            HeadlessUsage.GrokRefresh = () => refreshes++;
            OrbweaverSettings.RelayEnabled = true;
            UsageOn(claude: true, grok: usage);
            OrbweaverSettings.GrokAutoRefreshEnabled = auto;
            RelayService.ServeEarly();
            HeadlessUsage.Tick();
        }

        Run(usage: true, auto: false);
        Assert.Equal(0, refreshes);
        Run(usage: false, auto: true);
        Assert.Equal(0, refreshes);
        Run(usage: true, auto: true);
        Assert.Equal(1, refreshes);

        _now = _now.AddMinutes(1);                                 // inside the 20-minute floor
        HeadlessUsage.Tick();
        Assert.Equal(1, refreshes);
    }

    [AvaloniaFact]
    public void Tick_before_Start_and_after_Handoff_does_nothing_and_Start_after_Handoff_is_refused()
    {
        UsageOn();
        HeadlessUsage.Tick();                                      // never started
        Assert.Equal(0, _source.Reads);
        Assert.Null(HeadlessUsage.Handoff());                      // nothing to hand over

        HeadlessUsage.Start();                                     // the handover already happened
        Assert.False(HeadlessUsage.Started);
        HeadlessUsage.Tick();
        Assert.Equal(0, _source.Reads);
        Assert.Null(HeadlessUsage.Handoff());                      // and only once
    }

    // ---- the unlock ------------------------------------------------------------

    // The orbs draw what was already read, at once, without reading again.
    [AvaloniaFact]
    public void Attach_hands_the_readings_to_the_orbs()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        HeadlessUsage.Tick();
        var pump = HeadlessUsage.PumpForTests;

        var manager = Manager();
        RelayService.Attach(manager);

        var orbs = manager.AccountOrbsForTests;
        Assert.True(orbs.Orbs.ContainsKey("/acct/a"));
        Assert.Same(pump, orbs.PumpForTests);
        Assert.Equal(1, _source.Reads);
        Assert.False(HeadlessUsage.Started);
    }

    // The Grok schedule carries too, so its 20-minute floor is not reset by
    // an unlock.
    [AvaloniaFact]
    public void Attach_hands_the_Grok_schedule_to_the_session_manager()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        RelayService.ServeEarly();
        var manager = Manager();
        var before = manager.GrokRefreshForTests;

        RelayService.Attach(manager);

        Assert.NotSame(before, manager.GrokRefreshForTests);
    }

    // One pump, one floor: a tick of the adopted orbs before the floor reads
    // nothing; at it, exactly once.
    [AvaloniaFact]
    public void Attach_does_not_double_poll()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        HeadlessUsage.Tick();

        var manager = Manager();
        RelayService.Attach(manager);
        var orbs = manager.AccountOrbsForTests;

        orbs.Tick(T0.AddSeconds(30));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, _source.Reads);

        orbs.Tick(T0 + UsagePollCadence.Fast);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, _source.Reads);

        HeadlessUsage.Tick();                                      // the headless side is done
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
        HeadlessUsage.Tick();
        Assert.Equal(1, _client!.UsageFramesSent);

        var manager = Manager();
        RelayService.Attach(manager);
        Assert.Equal(1, _client.UsageFramesSent);

        manager.AccountOrbsForTests.Apply([Usage("/acct/a", session: 10)], T0.AddMinutes(1));
        Assert.Equal(1, _client.UsageFramesSent);

        manager.AccountOrbsForTests.Apply([Usage("/acct/a", session: 30)], T0.AddMinutes(2));
        Assert.Equal(2, _client.UsageFramesSent);
    }

    // A read started headless and finishing after the unlock reaches the orbs
    // through the dispatcher, once, and is not sent from the headless side.
    [AvaloniaFact]
    public void A_read_in_flight_at_Attach_reaches_the_orbs_once()
    {
        var held = new Held();
        HeadlessUsage.OffThread = held.Run;
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        HeadlessUsage.Tick();
        Assert.True(HeadlessUsage.PumpForTests!.Polling);

        var manager = Manager();
        RelayService.Attach(manager);
        var orbs = manager.AccountOrbsForTests;
        orbs.Tick(T0.AddHours(1));                                 // in flight: no second read
        Assert.Single(held.Pending);
        Assert.Empty(orbs.Orbs);

        held.Release();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, _source.Reads);
        Assert.True(orbs.Orbs.ContainsKey("/acct/a"));
        Assert.Empty(HeadlessUsage.ReadingsForTests);              // never merged headless
        Assert.Equal(1, _client!.UsageFramesSent);                 // the orbs' Apply, once
    }

    // The narrow gap: the read finishes after Handoff but before the orbs
    // have pointed the pump at themselves. It is held, then drawn on adoption.
    [AvaloniaFact]
    public void A_read_finishing_between_Handoff_and_Adopt_is_held_then_drawn()
    {
        var held = new Held();
        HeadlessUsage.OffThread = held.Run;
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        _source.Readings = [Usage("/acct/a")];
        RelayService.ServeEarly();
        HeadlessUsage.Tick();

        var handoff = HeadlessUsage.Handoff();
        held.Release();                                            // lands in the gap
        Assert.Empty(HeadlessUsage.ReadingsForTests);

        var manager = Manager();
        manager.AdoptUsage(handoff);
        Dispatcher.UIThread.RunJobs();

        Assert.True(manager.AccountOrbsForTests.Orbs.ContainsKey("/acct/a"));
        Assert.Equal(1, _source.Reads);
    }

    // The null arm: no feed (relay off) leaves the orbs with their own pump.
    [AvaloniaFact]
    public void Attach_without_a_headless_feed_keeps_the_orbs_own_pump()
    {
        OrbweaverSettings.RelayEnabled = false;
        var manager = Manager();
        var own = manager.AccountOrbsForTests.PumpForTests;

        RelayService.Attach(manager);

        Assert.Same(own, manager.AccountOrbsForTests.PumpForTests);
    }

    // Started but nothing read yet: the pump is adopted, nothing is drawn.
    [AvaloniaFact]
    public void Attach_before_any_headless_read_adopts_the_pump_and_draws_nothing()
    {
        OrbweaverSettings.RelayEnabled = true;
        UsageOn();
        RelayService.ServeEarly();
        var pump = HeadlessUsage.PumpForTests;

        var manager = Manager();
        RelayService.Attach(manager);

        Assert.Same(pump, manager.AccountOrbsForTests.PumpForTests);
        Assert.Empty(manager.AccountOrbsForTests.Orbs);
    }

    // Adopt without a late-readings holder (the parameter's default).
    [AvaloniaFact]
    public void Adopt_without_late_readings_draws_what_it_was_given()
    {
        UsageOn();
        var manager = Manager();
        var pump = new UsagePump(_source, work => work());

        manager.AccountOrbsForTests.Adopt(pump,
            new Dictionary<string, AccountUsage> { ["/acct/a"] = Usage("/acct/a") });

        Assert.True(manager.AccountOrbsForTests.Orbs.ContainsKey("/acct/a"));
        Assert.Same(pump, manager.AccountOrbsForTests.PumpForTests);
    }
}
