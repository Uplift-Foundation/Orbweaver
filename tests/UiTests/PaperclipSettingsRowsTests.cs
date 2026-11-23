using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// The Paperclip group of Settings, driven the way CloudSettingsRowsTests drives
// the cloud one: call the production row builder, never walk for a control whose
// type depends on which theme template loaded. The connector is a fake, so
// nothing here opens a browser, calls a Paperclip, or touches the real key
// store; a Connect click reaches the fake and stops there.
[Collection("Settings")]
public class PaperclipSettingsRowsTests : IDisposable
{
    private sealed class FakeConnector : IPaperclipConnector
    {
        public PaperclipPhase Phase { get; set; }
        public string Message { get; set; } = "";
        public IReadOnlyList<PaperclipDiscovery.Found> Found { get; set; } = [];
        public IReadOnlyList<PaperclipApi.Company> Companies { get; set; } = [];
        public readonly List<string> Calls = [];

        public Task DiscoverAsync(CancellationToken ct) { Calls.Add("discover"); return Task.CompletedTask; }
        public Task LoadCompaniesAsync(CancellationToken ct) { Calls.Add("companies"); return Task.CompletedTask; }
        public Task ConnectAsync(string baseUrl, CancellationToken ct) { Calls.Add("connect " + baseUrl); return Task.CompletedTask; }
        public void CancelConnect() => Calls.Add("cancel");
        public Task DisconnectAsync(CancellationToken ct) { Calls.Add("disconnect"); return Task.CompletedTask; }
        public void ChooseCompany(string companyId) => Calls.Add("choose " + companyId);
    }

    private readonly FakeConnector _fake = new();
    private readonly IPaperclipConnector _was = PaperclipConnector.Current;
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly string _url = OrbweaverSettings.PaperclipUrl;
    private readonly string _company = OrbweaverSettings.PaperclipCompanyId;
    private readonly string _sleeping = OrbweaverSettings.PaperclipSleeping;

    public PaperclipSettingsRowsTests()
    {
        PaperclipConnector.Current = _fake;
        OrbweaverSettings.PaperclipEnabled = false;
        OrbweaverSettings.PaperclipUrl = "";
        OrbweaverSettings.PaperclipCompanyId = "";
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
    }

    public void Dispose()
    {
        PaperclipConnector.Current = _was;
        OrbweaverSettings.PaperclipEnabled = _enabled;
        OrbweaverSettings.PaperclipUrl = _url;
        OrbweaverSettings.PaperclipCompanyId = _company;
        OrbweaverSettings.PaperclipSleeping = _sleeping;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
    }

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)(typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()")).Invoke(null);

    private static string Text(Control[] rows) =>
        string.Join("\n", rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)));

    private static Button Button(Control[] rows, string label) =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<Button>()).Single(b => (string?)b.Content == label);

    private static IEnumerable<Button> Buttons(Control[] rows) =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<Button>());

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    private static readonly PaperclipApi.Company One = new("c1", "One", "A");
    private static readonly PaperclipApi.Company Two = new("c2", "Two", "B");

    // ---- not connected ----

    [AvaloniaFact]
    public void NothingFound_ExplainsItself_AndOffersAnAddressBoxAndConnect()
    {
        var rows = NewWindow().PaperclipRows();
        Assert.Contains("Connecting is one approval in your browser", Text(rows));
        Assert.Contains("Nothing is installed in your Paperclip", Text(rows));
        Assert.Single(Buttons(rows));
        Assert.Equal("Connect", Button(rows, "Connect").Content);
    }

    [AvaloniaFact]
    public void NoControlEverHoldsTheKey_TheOnlyTextBoxIsTheAddress()
    {
        var rows = NewWindow().PaperclipRows();
        var box = Assert.Single(rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBox>()));
        Assert.Equal(SettingsWindow.AddressExample, box.Watermark);

        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = "http://127.0.0.1:3100";
        Assert.Empty(NewWindow().PaperclipRows().SelectMany(r => r.GetLogicalDescendants().OfType<TextBox>()));
    }

    [AvaloniaFact]
    public void AFoundInstance_IsOffered_AndItsAddressPrefilled_AndConnectUsesIt()
    {
        _fake.Found = [new("http://127.0.0.1:3100", "default", "authenticated")];
        var rows = NewWindow().PaperclipRows();
        Assert.Contains("Paperclip found on this machine. Show your company?", Text(rows));
        var box = rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBox>()).Single();
        Assert.Equal("http://127.0.0.1:3100", box.Text);
        Assert.True(Button(rows, "Connect").IsEnabled);                       // a discovered address is enough
        Assert.DoesNotContain("Enter your Paperclip address.",
            string.Join("\n", rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text))));

        Click(Button(rows, "Connect"));
        Assert.Contains("connect http://127.0.0.1:3100", _fake.Calls);
    }

    private static TextBox AddressBox(Control[] rows) =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBox>()).Single();

    [AvaloniaFact]
    public void NothingDiscovered_TheBoxIsEmpty_ShowsAnExampleNotLocalhost_AndConnectIsOff()
    {
        var rows = NewWindow().PaperclipRows();
        var box = AddressBox(rows);
        Assert.True(string.IsNullOrEmpty(box.Text));
        Assert.Equal("http://192.0.2.10:3100", SettingsWindow.AddressExample);
        Assert.Equal(SettingsWindow.AddressExample, box.Watermark);
        Assert.DoesNotContain("127.0.0.1", box.Watermark);
        Assert.DoesNotContain("localhost", box.Watermark);
        Assert.False(Button(rows, "Connect").IsEnabled);
        Assert.Contains("Enter your Paperclip address.", Text(rows));
    }

    [AvaloniaFact]
    public void ClickingAnOffConnect_DoesNothing_NotEvenAGuessAtLocalhost()
    {
        var rows = NewWindow().PaperclipRows();
        Click(Button(rows, "Connect"));
        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("connect"));
    }

    [AvaloniaFact]
    public void TypingAnAddress_TurnsConnectOn_AndHidesThePrompt_AndClearingItTurnsItOffAgain()
    {
        var rows = NewWindow().PaperclipRows();
        var box = AddressBox(rows);
        var prompt = rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>())
            .Single(t => t.Text == "Enter your Paperclip address.");
        Assert.True(prompt.IsVisible);

        box.Text = "http://192.0.2.10:3100";
        Assert.True(Button(rows, "Connect").IsEnabled);
        Assert.False(prompt.IsVisible);

        Click(Button(rows, "Connect"));
        Assert.Contains("connect http://192.0.2.10:3100", _fake.Calls);

        box.Text = "   ";                       // whitespace is empty
        Assert.False(Button(rows, "Connect").IsEnabled);
        Assert.True(prompt.IsVisible);
        box.Text = "";
        Assert.False(Button(rows, "Connect").IsEnabled);
    }

    [AvaloniaFact]
    public void TheUnencryptedNote_FollowsTheTypedAddress()
    {
        var rows = NewWindow().PaperclipRows();
        var note = rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>())
            .Single(t => t.Text == "The key is sent unencrypted on this network.");
        Assert.False(note.IsVisible);

        var box = rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBox>()).Single();
        box.Text = "http://192.0.2.10:3100";
        Assert.True(note.IsVisible);
        box.Text = "http://127.0.0.1:3100";
        Assert.False(note.IsVisible);
        box.Text = "https://paperclip.example";
        Assert.False(note.IsVisible);
    }

    [AvaloniaFact]
    public void Approving_ShowsTheWaitAndCancel_AndNoConnectButton()
    {
        _fake.Phase = PaperclipPhase.Approving;
        _fake.Message = "Approve Orbweaver in your browser…";
        var rows = NewWindow().PaperclipRows();
        Assert.Contains("Approve Orbweaver in your browser…", Text(rows));
        Assert.Equal("Cancel", Assert.Single(Buttons(rows)).Content);
        Assert.Empty(rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBox>()));

        Click(Button(rows, "Cancel"));
        Assert.Contains("cancel", _fake.Calls);
    }

    [AvaloniaFact]
    public void AFailedConnect_SaysWhy()
    {
        _fake.Phase = PaperclipPhase.Failed;
        _fake.Message = "The approval timed out. Try again.";
        Assert.Contains("The approval timed out. Try again.", Text(NewWindow().PaperclipRows()));
    }

    // ---- connected ----

    private static void Connected(string url = "http://127.0.0.1:3100", PaperclipLink link = PaperclipLink.Connected, string status = "Connected to 127.0.0.1:3100. 4 agents.")
    {
        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = url;
        OrbweaverSettings.PaperclipCompanyId = "c1";
        PaperclipSessions.SetLinkForTests(link, status);
    }

    [AvaloniaFact]
    public void Connected_ShowsTheStatusLine_TheSleepingChoice_AndDisconnect_ButNoPickerOrReconnect()
    {
        Connected();
        _fake.Companies = [One];
        var w = NewWindow();
        var rows = w.PaperclipRows();
        Assert.Equal("Connected to 127.0.0.1:3100. 4 agents.", w.PaperclipStatusText);
        Assert.Contains("When agents are asleep", Text(rows));
        Assert.Equal(new[] { "Disconnect" }, Buttons(rows).Select(b => (string?)b.Content).ToArray());
        Assert.Empty(rows.SelectMany(r => r.GetLogicalDescendants().OfType<ComboBox>()).Skip(1));   // only the sleeping choice
        Assert.DoesNotContain("unencrypted", Text(rows));
    }

    [AvaloniaFact]
    public void ThePicker_AppearsOnlyWithSeveralCompanies_AndChoosingCallsTheConnector()
    {
        Connected();
        _fake.Companies = [One, Two];
        var rows = NewWindow().PaperclipRows();
        Assert.Contains("Company", Text(rows));
        var combos = rows.SelectMany(r => r.GetLogicalDescendants().OfType<ComboBox>()).ToList();
        Assert.Equal(2, combos.Count);
        var picker = combos.Single(c => ((IEnumerable<string>)c.ItemsSource!).Contains("Two"));
        Assert.Equal(0, picker.SelectedIndex);   // c1 is chosen

        picker.SelectedIndex = 1;
        Assert.Contains("choose c2", _fake.Calls);
        picker.SelectedIndex = 0;                // back to the stored one: no call
        Assert.DoesNotContain("choose c1", _fake.Calls);
    }

    [AvaloniaFact]
    public void TheSleepingChoice_WritesTheSetting_AndShowsTheCurrentOne()
    {
        Connected();
        OrbweaverSettings.PaperclipSleeping = "hide";
        var combo = NewWindow().SleepingPicker() as ComboBox;
        Assert.Equal(2, combo!.SelectedIndex);
        combo.SelectedIndex = 1;
        Assert.Equal("show", OrbweaverSettings.PaperclipSleeping);
        combo.SelectedIndex = 0;
        Assert.Equal("fold", OrbweaverSettings.PaperclipSleeping);
        Assert.Equal(new[] { "fold", "show", "hide" }, SettingsWindow.SleepingChoices.Select(c => c.Value).ToArray());
    }

    [AvaloniaFact]
    public void Reconnect_IsOnlyOffered_WhenTheKeyNeedsReplacing_AndRunsTheSameApproval()
    {
        Connected(link: PaperclipLink.NeedsReconnect, status: "Reconnect to Paperclip");
        var rows = NewWindow().PaperclipRows();
        Assert.Equal(new[] { "Reconnect", "Disconnect" }, Buttons(rows).Select(b => (string?)b.Content).ToArray());
        // Reconnect is the primary action and Disconnect stays plain.
        Assert.Contains("accent", Button(rows, "Reconnect").Classes);
        Assert.DoesNotContain("accent", Button(rows, "Disconnect").Classes);
        Click(Button(rows, "Reconnect"));
        Assert.Contains("connect http://127.0.0.1:3100", _fake.Calls);
    }

    [AvaloniaFact]
    public void Disconnect_CallsTheConnector()
    {
        Connected();
        Click(Button(NewWindow().PaperclipRows(), "Disconnect"));
        Assert.Contains("disconnect", _fake.Calls);
    }

    [AvaloniaFact]
    public void AnHttpAddressOffThisMachine_GetsTheUnencryptedNote_LoopbackAndHttpsDoNot()
    {
        Connected("http://192.0.2.10:3100");
        Assert.Contains("The key is sent unencrypted on this network.", Text(NewWindow().PaperclipRows()));
        Connected("http://localhost:3100");
        Assert.DoesNotContain("unencrypted", Text(NewWindow().PaperclipRows()));
        Connected("https://paperclip.example");
        Assert.DoesNotContain("unencrypted", Text(NewWindow().PaperclipRows()));
    }

    [AvaloniaFact]
    public void AMessageFromTheConnector_IsShownWhileConnected()
    {
        Connected();
        _fake.Message = "Disconnected here, but the key may still be listed in Paperclip.";
        Assert.Contains("key may still be listed", Text(NewWindow().PaperclipRows()));
    }

    // ---- the tick ----

    [AvaloniaFact]
    public void TheTick_RefreshesTheStatusLine_AndRebuildsWhenThePhaseMoves()
    {
        Connected();
        var w = NewWindow();
        w.PaperclipRows();
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected. The key expires in 2 days.");
        w.OnStatusTick(null, EventArgs.Empty);
        Assert.Equal("Connected. The key expires in 2 days.", w.PaperclipStatusText);

        // A link change is a different set of rows (Reconnect appears): the tick rebuilds.
        PaperclipSessions.SetLinkForTests(PaperclipLink.NeedsReconnect, "Reconnect to Paperclip");
        w.OnStatusTick(null, EventArgs.Empty);
        Assert.Equal("Reconnect to Paperclip", w.PaperclipStatusText);
    }

    [AvaloniaFact]
    public async Task OpeningTheWindow_RunsDiscovery_AndLoadsCompaniesOnlyWhenConnected()
    {
        var w = NewWindow();
        await w.DiscoverPaperclipAsync();
        Assert.Equal(new[] { "discover" }, _fake.Calls.ToArray());

        Connected();
        _fake.Calls.Clear();
        await w.DiscoverPaperclipAsync();
        Assert.Equal(new[] { "discover", "companies" }, _fake.Calls.ToArray());
    }

    [AvaloniaFact]
    public async Task ConnectAndDisconnect_RebuildTheRowsAroundTheCall()
    {
        var w = NewWindow();
        await w.OnPaperclipConnectAsync("http://127.0.0.1:3100");
        Assert.Contains("connect http://127.0.0.1:3100", _fake.Calls);
        await w.OnPaperclipDisconnectAsync();
        Assert.Contains("disconnect", _fake.Calls);
    }

    // ---- pure helpers ----

    [AvaloniaTheory]
    [InlineData("http://192.0.2.10:3100", true)]
    [InlineData("http://paperclip.lan", true)]
    [InlineData("http://127.0.0.1:3100", false)]
    [InlineData("http://localhost:3100", false)]
    [InlineData("http://[::1]:3100", false)]
    [InlineData("https://paperclip.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void SendsKeyUnencrypted_OnlyForHttpOffThisMachine(string? address, bool expected) =>
        Assert.Equal(expected, SettingsWindow.SendsKeyUnencrypted(address));

    [AvaloniaFact]
    public void SuggestedAddress_FoundThenStoredThenEmpty()
    {
        PaperclipDiscovery.Found f = new("http://127.0.0.1:3100", "d", "authenticated");
        Assert.Equal("http://127.0.0.1:3100", SettingsWindow.PaperclipSuggestedAddress([f], "http://x"));
        Assert.Equal("http://x", SettingsWindow.PaperclipSuggestedAddress([], "http://x"));
        Assert.Equal("", SettingsWindow.PaperclipSuggestedAddress([], ""));
    }
}
