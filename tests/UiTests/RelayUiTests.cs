using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-314's visible surfaces, headless: the pairing window through every stage
// with a fake client, Settings › Paired phones over a real RelayClient that is
// never started, the tray item, and RelayService's wiring. No socket, no
// Keychain, no real window shown — PairPhoneWindow.Toggle runs with
// PresentForTests, and windows are never closed (the FontManager rule
// SettingsWindowSmokeTest records).
internal sealed class FakePairingClient : IPairingClient
{
    public Func<CancellationToken, Task<RelayLink.PairOffer>> Begin = _ =>
        Task.FromResult(new RelayLink.PairOffer("off_1", DateTimeOffset.UtcNow.AddMinutes(5), new byte[32], "orbweaver://pair?r=relay&d=dsk_1&o=off_1&k=AA&s=BB"));
    public bool AllowResult = true;
    public readonly List<string> Calls = new();

    public Task<RelayLink.PairOffer> BeginPairingAsync(CancellationToken token = default) { Calls.Add("begin"); return Begin(token); }
    public bool Allow(RelayLink.PairRequest request) { Calls.Add("allow:" + request.PhoneId); return AllowResult; }
    public void Deny(RelayLink.PairRequest request) => Calls.Add("deny:" + request.PhoneId);
    public void CancelPairing() => Calls.Add("cancel");
    public event Action<RelayLink.PairRequest> PairRequested = delegate { };
    public void Raise(RelayLink.PairRequest r) => PairRequested(r);
}

public class PairPhoneWindowTests
{
    private static DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static RelayLink.PairRequest Request(string offer = "off_1") =>
        new(offer, "ph_1", "Warren's iPhone", [], [], [], 4213);

    private static (PairPhoneWindow Window, FakePairingClient Client) New(FakePairingClient? client = null)
    {
        client ??= new FakePairingClient();
        var offer = new RelayLink.PairOffer("off_1", _now.AddMinutes(5), new byte[32], "orbweaver://pair?r=relay&d=dsk_1&o=off_1&k=AA&s=BB");
        client.Begin = _ => Task.FromResult(offer);
        return (new PairPhoneWindow(client, () => _now), client);
    }

    [AvaloniaFact]
    public async Task Showing_draws_a_qr_and_counts_down()
    {
        var (w, client) = New();
        Assert.Equal(PairPhoneWindow.Stage.Loading, w.Current);
        Assert.Equal("Contacting the relay…", w.Detail.Text);
        Assert.True(w.CloseButton.IsVisible);

        await w.BeginAsync();
        Assert.Equal(PairPhoneWindow.Stage.Showing, w.Current);
        Assert.True(w.Qr.IsVisible);
        Assert.NotNull(w.Qr.Modules);
        Assert.Equal("Expires in 5:00", w.Countdown.Text);
        Assert.False(w.AllowButton.IsVisible);
        Assert.Equal(["begin"], client.Calls);

        _now = _now.AddSeconds(75);
        w.Tick();
        Assert.Equal("Expires in 3:45", w.Countdown.Text);
        _now = _now.AddSeconds(-75);
    }

    [AvaloniaFact]
    public async Task A_request_asks_with_the_code_and_allow_pairs()
    {
        var (w, client) = New();
        await w.BeginAsync();
        client.Raise(Request());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(PairPhoneWindow.Stage.Asking, w.Current);
        Assert.Equal("Pair Warren's iPhone?", w.Heading.Text);
        Assert.Equal("004 213", w.Code.Text);
        Assert.True(w.AllowButton.IsVisible && w.DenyButton.IsVisible);
        Assert.False(w.Qr.IsVisible);

        w.AllowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(PairPhoneWindow.Stage.Paired, w.Current);
        Assert.Equal("Paired with Warren's iPhone", w.Heading.Text);
        Assert.Contains("allow:ph_1", client.Calls);
        w.Tick();                                                   // nothing to count once paired
    }

    [AvaloniaFact]
    public async Task Allow_that_could_not_be_sent_stays_on_the_question()
    {
        var (w, client) = New(new FakePairingClient { AllowResult = false });
        await w.BeginAsync();
        w.Ask(Request());
        w.OnAllow();
        Assert.Equal(PairPhoneWindow.Stage.Asking, w.Current);
        Assert.Contains("Lost the connection", w.Detail.Text);
    }

    [AvaloniaFact]
    public async Task Deny_goes_back_to_the_code_and_tells_the_client()
    {
        var (w, client) = New();
        await w.BeginAsync();
        w.Ask(Request());
        w.DenyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(PairPhoneWindow.Stage.Showing, w.Current);
        Assert.Contains("deny:ph_1", client.Calls);
        w.OnAllow();                                                // nothing pending: nothing happens
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("allow"));
        w.OnDeny();                                                 // nothing pending: no second deny
        Assert.Single(client.Calls, c => c.StartsWith("deny"));
    }

    [AvaloniaFact]
    public async Task Requests_for_another_offer_or_outside_the_showing_stages_are_ignored()
    {
        var (w, _) = New();
        w.Ask(Request());                                           // still loading
        Assert.Equal(PairPhoneWindow.Stage.Loading, w.Current);
        await w.BeginAsync();
        w.Ask(Request("off_other"));
        Assert.Equal(PairPhoneWindow.Stage.Showing, w.Current);
    }

    [AvaloniaFact]
    public async Task The_code_expires_and_try_again_asks_for_a_new_one()
    {
        var (w, client) = New();
        await w.BeginAsync();
        _now = _now.AddMinutes(5);
        try
        {
            w.Tick();
            Assert.Equal(PairPhoneWindow.Stage.Expired, w.Current);
            Assert.Contains("cancel", client.Calls);
            Assert.True(w.RetryButton.IsVisible);
            w.Tick();                                               // already expired: no second cancel
            Assert.Single(client.Calls, c => c == "cancel");
        }
        finally { _now = _now.AddMinutes(-5); }

        w.RetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, client.Calls.Count(c => c == "begin"));
    }

    [AvaloniaTheory]
    [InlineData("http")]
    [InlineData("relay")]
    [InlineData("timeout")]
    [InlineData("keychain")]
    public async Task A_failed_offer_is_a_stage_with_the_reason(string kind)
    {
        var client = new FakePairingClient();
        var w = new PairPhoneWindow(client, () => _now);
        client.Begin = _ => kind switch
        {
            "http" => Task.FromException<RelayLink.PairOffer>(new HttpRequestException("no route to host")),
            "relay" => Task.FromException<RelayLink.PairOffer>(new RelayHttpException("the relay answered 503")),
            "keychain" => Task.FromException<RelayLink.PairOffer>(new RelaySecretUnavailableException("the Keychain refused the relay keys (OSStatus -25293)")),
            _ => Task.FromException<RelayLink.PairOffer>(new TaskCanceledException()),
        };
        await w.BeginAsync();
        Assert.Equal(PairPhoneWindow.Stage.Failed, w.Current);
        Assert.Equal("Couldn't start pairing", w.Heading.Text);
        Assert.Equal(kind switch
        {
            "http" => "no route to host",
            "relay" => "the relay answered 503",
            "keychain" => "the Keychain refused the relay keys (OSStatus -25293)",
            _ => "The relay did not answer.",
        }, w.Detail.Text);
    }

    [AvaloniaFact]
    public async Task A_superseded_attempt_never_overwrites_the_newer_one()
    {
        var client = new FakePairingClient();
        var w = new PairPhoneWindow(client, () => _now);
        var slow = new TaskCompletionSource<RelayLink.PairOffer>();
        client.Begin = _ => slow.Task;
        var first = w.BeginAsync();
        client.Begin = _ => Task.FromResult(new RelayLink.PairOffer("off_2", _now.AddMinutes(5), new byte[32], "orbweaver://pair?o=off_2"));
        await w.BeginAsync();
        slow.SetResult(new RelayLink.PairOffer("off_1", _now.AddMinutes(5), new byte[32], "orbweaver://pair?o=off_1"));
        await first;
        w.Ask(Request("off_2"));
        Assert.Equal(PairPhoneWindow.Stage.Asking, w.Current);       // off_2 is the live one

        var failing = new TaskCompletionSource<RelayLink.PairOffer>();
        client.Begin = _ => failing.Task;
        var third = w.BeginAsync();
        client.Begin = _ => Task.FromResult(new RelayLink.PairOffer("off_3", _now.AddMinutes(5), new byte[32], "x"));
        await w.BeginAsync();
        failing.SetException(new HttpRequestException("late"));
        await third;
        Assert.Equal(PairPhoneWindow.Stage.Showing, w.Current);      // the late failure was ignored
    }

    [AvaloniaFact]
    public async Task Forget_cancels_a_live_offer_and_stops_listening()
    {
        var (w, client) = New();
        await w.BeginAsync();
        w.Forget();
        Assert.Contains("cancel", client.Calls);
        client.Raise(Request());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PairPhoneWindow.Stage.Showing, w.Current);

        var (done, doneClient) = New();
        await done.BeginAsync();
        done.Ask(Request());
        done.OnAllow();
        done.Forget();                                              // paired: nothing to cancel
        Assert.DoesNotContain("cancel", doneClient.Calls);
    }

    [AvaloniaFact]
    public void Toggle_opens_once_and_brings_the_open_one_forward()
    {
        var presented = new List<PairPhoneWindow>();
        PairPhoneWindow.PresentForTests = presented.Add;
        PairPhoneWindow.OpenForTests = null;
        try
        {
            var client = new FakePairingClient();
            PairPhoneWindow.Toggle(client);
            PairPhoneWindow.Toggle(client);
            Assert.Equal(2, presented.Count);
            Assert.Same(presented[0], presented[1]);
            Assert.Same(presented[0], PairPhoneWindow.OpenForTests);
            presented[0].Forget();
            Assert.Null(PairPhoneWindow.OpenForTests);
            presented[0].Forget();                                  // not the open one any more: harmless
        }
        finally
        {
            PairPhoneWindow.PresentForTests = null;
            PairPhoneWindow.OpenForTests = null;
        }
    }

    [AvaloniaFact]
    public void The_qr_matrix_is_square_and_renders()
    {
        var m = QrView.Encode(new string('a', 230));
        Assert.Equal(m.GetLength(0), m.GetLength(1));
        Assert.True(m.GetLength(0) > 40);
        var view = new QrView { Width = 200, Height = 200 };
        var host = new Window { Content = view, Width = 220, Height = 220 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();   // renders empty, before a matrix
        Dispatcher.UIThread.RunJobs();
        view.Modules = m;                                           // renders with a matrix
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(m, view.Modules);
    }
}

[Collection("Settings")]
public class RelaySettingsAndServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-ui-" + Guid.NewGuid());

    public void Dispose()
    {
        RelayService.UseForTests(null);
        RelayService.Factory = RelayService.RealClient;
        RelayService.PersonaName = RelayService.CachedPersonaName;
        RelayService.UserConfigDirs = RelayService.DefaultUserConfigDirs;
        OrbweaverSettings.RelayEnabled = false;
        try { Directory.Delete(_dir, recursive: true); } catch { }
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

    // No test here may reach the network: any HTTP the client attempts gets a
    // canned 503 from this handler instead of a DNS lookup for relay.example.
    private sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") });
    }

    private RelayClient Client(params string[] phoneNames)
    {
        var identity = new RelayIdentity(_dir, new MemoryStore());
        identity.Keys();
        identity.SetRegistration("dsk_ui", "tok");
        for (var i = 0; i < phoneNames.Length; i++)
            identity.AddPhone($"ph_{i}", phoneNames[i], RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
        return new RelayClient(identity, new RelayClient.Options(new Uri("https://relay.example"), "mac", "1", "UI Mac"), new NeverConnects(), new Refuses());
    }

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    private static IEnumerable<T> All<T>(Control[] rows) where T : Control =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<T>().Concat(r is T t ? new[] { t } : Array.Empty<T>()));

    [AvaloniaFact]
    public void Without_a_client_settings_offers_pairing_and_lists_nothing()
    {
        RelayService.UseForTests(null);
        var rows = NewWindow().PairedPhonesRows();
        Assert.Contains(All<Button>(rows), b => b.Name == "PairPhoneButton");
        Assert.Contains(All<TextBlock>(rows), t => t.Text == "No phones paired.");
        Assert.True(NewWindow().Sections.ContainsKey("paired-phones"));
    }

    [AvaloniaFact]
    public void With_a_client_each_phone_has_a_status_and_remove()
    {
        var client = Client("Warren's iPhone", "Work iPhone");
        RelayService.UseForTests(client);
        var window = NewWindow();
        var rows = window.PairedPhonesRows();

        Assert.Equal("Not connected to the relay.", window.RelayStatusLabel!.Text);
        Assert.Contains(All<TextBlock>(rows), t => t.Text == "Warren's iPhone");
        Assert.Equal(2, All<TextBlock>(rows).Count(t => t.Text == "Offline"));
        Assert.DoesNotContain(All<TextBlock>(rows), t => t.Text == "No phones paired.");

        window.OnStatusTick(null, EventArgs.Empty);                 // nothing changed: labels stay
        var label = All<TextBlock>(rows).First(t => t.Text == "Offline");
        label.Text = "stale";
        window.OnStatusTick(null, EventArgs.Empty);                 // a changed line is rewritten
        Assert.Equal("Offline", label.Text);

        var remove = All<Button>(rows).Single(b => b.Name == "RemovePhone:ph_0");
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["ph_1"], client.Phones.Select(p => p.PhoneId));
    }

    [AvaloniaFact]
    public void The_status_tick_follows_the_client()
    {
        var client = Client("Phone");
        RelayService.UseForTests(client);
        var window = NewWindow();
        window.PairedPhonesRows();
        typeof(RelayClient).GetProperty("State", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, RelayClient.Status.Connecting);
        typeof(RelayClient).GetProperty("LastError", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, "timed out");
        window.OnStatusTick(null, EventArgs.Empty);
        Assert.Equal("Reconnecting to the relay — last attempt: timed out", window.RelayStatusLabel!.Text);

        RelayService.UseForTests(null);                             // client gone: the tick leaves it alone
        window.OnStatusTick(null, EventArgs.Empty);
        Assert.Equal("Reconnecting to the relay — last attempt: timed out", window.RelayStatusLabel!.Text);
    }

    [AvaloniaFact]
    public async Task Enable_is_idempotent_persists_and_disable_undoes_it()
    {
        var made = 0;
        RelayService.Factory = () => { made++; return Client(); };
        var changed = 0;
        void OnChanged() => changed++;
        RelayService.ClientChanged += OnChanged;
        try
        {
            var a = RelayService.Enable();
            var b = RelayService.Enable();
            Assert.Same(a, b);
            Assert.Equal(1, made);
            Assert.True(OrbweaverSettings.RelayEnabled);
            Assert.Same(a, RelayService.Client);

            await RelayService.DisableAsync();
            Assert.Null(RelayService.Client);
            Assert.False(OrbweaverSettings.RelayEnabled);
            await RelayService.DisableAsync();                      // nothing to stop
            Assert.Equal(3, changed);
        }
        finally { RelayService.ClientChanged -= OnChanged; }
    }

    [AvaloniaFact]
    public async Task Attach_listens_to_the_scan_and_connects_only_if_paired_before()
    {
        RelayService.Factory = () => Client("Phone");
        var dir = Path.Combine(_dir, "status");
        Directory.CreateDirectory(dir);
        var manager = new SessionManager(dir,
            () => new Dictionary<string, string>(StringComparer.Ordinal),
            () => new HashSet<string>(StringComparer.Ordinal),
            dependents: _ => SessionDependents.Nothing,
            paneOwners: claims => claims.ToDictionary(TmuxPaneKey.Of, _ => (string?)null),
            agentViewer: _ => null,
            teams: pids => pids.ToDictionary(p => p, _ => AgentTeam.None),
            fileTeams: _ => new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));

        OrbweaverSettings.RelayEnabled = false;
        RelayService.Attach(manager);
        Assert.Null(RelayService.Client);
        manager.ScanAndUpdate();                                    // no client: the handler is a no-op

        OrbweaverSettings.RelayEnabled = true;
        RelayService.UserConfigDirs = () => [];
        var asked = new List<string>();
        RelayService.PersonaName = (status, _) => { asked.Add(status.Cwd); return "Jennifer Voss"; };
        RelayService.Attach(manager);
        Assert.NotNull(RelayService.Client);
        File.WriteAllText(Path.Combine(dir, "s.txt"), System.Text.Json.JsonSerializer.Serialize(new SessionStatus
        {
            State = "generating", Cwd = "/Users/someone/proj", SessionPid = Environment.ProcessId, TermProgram = "iTerm.app", Tty = "/dev/ttys004",
        }));
        manager.ScanAndUpdate();                                    // projected, persona asked, handed to the client
        Assert.Contains("/Users/someone/proj", asked);
        await RelayService.DisableAsync();
    }

    // Addendum 4: the colour names a phone may be sent are exactly the names
    // an orb itself draws. RelayRoster keeps its own copy (it is pure and
    // must not touch a window class), so this pins the two together: a name
    // added to OrbWindow's accent table without the relay list fails here.
    [AvaloniaFact]
    public void Persona_colours_are_exactly_the_orb_accent_names()
    {
        var table = (System.Collections.IDictionary)typeof(OrbWindow)
            .GetField("AgentColors", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var accents = table.Keys.Cast<string>().Select(k => k.ToLowerInvariant()).Order().ToList();
        Assert.Equal(accents, RelayRoster.PersonaColors.Order().ToList());
    }

    [AvaloniaFact]
    public async Task Attach_runs_the_test_hooks_only_when_they_are_switched_on()
    {
        RelayService.Factory = () => Client();
        var ran = new List<IPairingClient>();
        RelayService.HooksRunner = c => { ran.Add(c); return Task.CompletedTask; };
        var dir = Path.Combine(_dir, "status-hooks");
        Directory.CreateDirectory(dir);
        var manager = new SessionManager(dir,
            () => new Dictionary<string, string>(StringComparer.Ordinal),
            () => new HashSet<string>(StringComparer.Ordinal),
            dependents: _ => SessionDependents.Nothing,
            paneOwners: claims => claims.ToDictionary(TmuxPaneKey.Of, _ => (string?)null),
            agentViewer: _ => null,
            teams: pids => pids.ToDictionary(p => p, _ => AgentTeam.None),
            fileTeams: _ => new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));
        try
        {
            OrbweaverSettings.RelayEnabled = false;
            RelayService.HooksEnabled = () => false;
            RelayService.Attach(manager);
            Assert.Empty(ran);
            Assert.Null(RelayService.Client);

            RelayService.HooksEnabled = () => true;
            RelayService.Attach(manager);
            Assert.Same(RelayService.Client, Assert.Single(ran));
            Assert.False(RelayTestHooks.Enabled(_ => null));                   // and the real default is off
        }
        finally
        {
            RelayService.HooksEnabled = () => RelayTestHooks.Enabled(Environment.GetEnvironmentVariable);
            RelayService.HooksRunner = RelayService.RunRealHooks;
            await RelayService.DisableAsync();
        }
    }

    [AvaloniaFact]
    public void Defaults_name_this_platform_and_version()
    {
        var o = RelayService.DefaultOptions();
        Assert.Equal(OperatingSystem.IsWindows() ? "win" : "mac", o.Platform);
        Assert.Equal(("win", "mac"), (RelayService.PlatformName(true), RelayService.PlatformName(false)));
        Assert.Equal("orbweaver-relay.uplifttech.org", o.RelayHost);
        Assert.DoesNotContain("+", RelayService.AppVersion());
        Assert.NotEmpty(RelayService.AppVersion());
    }

    [AvaloniaFact]
    public void The_tray_pair_item_enables_the_relay_and_opens_the_window()
    {
        RelayService.Factory = () => Client();
        var presented = new List<PairPhoneWindow>();
        PairPhoneWindow.PresentForTests = presented.Add;
        PairPhoneWindow.OpenForTests = null;
        try
        {
            TrayController.OpenPairPhone();
            Assert.Single(presented);
            Assert.NotNull(RelayService.Client);
            Assert.True(OrbweaverSettings.RelayEnabled);
        }
        finally
        {
            presented.ForEach(w => w.Forget());
            PairPhoneWindow.PresentForTests = null;
            PairPhoneWindow.OpenForTests = null;
            RelayService.DisableAsync().GetAwaiter().GetResult();
        }
    }
}
