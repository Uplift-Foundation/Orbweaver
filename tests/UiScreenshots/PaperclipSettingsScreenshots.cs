using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// Captures for CB-421's Settings group in the two states a reviewer compares
// across rids: connected, and the dimmed "Reconnect to Paperclip" state (with
// the orb that state dims). Hand-written one per scenario, because adding a
// case to tests/UiTests does not generate its capture; this suite renders
// through real Skia.
//
// The connector is a fake, so nothing here calls a Paperclip, opens a browser
// or touches the real key store. No clicks.
[Collection("Settings")]
public class PaperclipSettingsScreenshots : IDisposable
{
    private sealed class FakeConnector : IPaperclipConnector
    {
        public PaperclipPhase Phase { get; set; }
        public string Message { get; set; } = "";
        public IReadOnlyList<PaperclipDiscovery.Found> Found { get; set; } = [];
        public IReadOnlyList<PaperclipApi.Company> Companies { get; set; } = [];
        public Task DiscoverAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LoadCompaniesAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ConnectAsync(string baseUrl, CancellationToken ct) => Task.CompletedTask;
        public void CancelConnect() { }
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public void ChooseCompany(string companyId) { }
    }

    private readonly IPaperclipConnector _was = PaperclipConnector.Current;
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly string _url = OrbweaverSettings.PaperclipUrl;
    private readonly string _company = OrbweaverSettings.PaperclipCompanyId;

    public void Dispose()
    {
        PaperclipConnector.Current = _was;
        OrbweaverSettings.PaperclipEnabled = _enabled;
        OrbweaverSettings.PaperclipUrl = _url;
        OrbweaverSettings.PaperclipCompanyId = _company;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
    }

    private static void Capture(string name)
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");
        var window = (Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        // Anchored on the group heading and climbed to the section type, the
        // structural anchor CloudSessionScreenshots explains at length.
        var title = window.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == "Paperclip agents");
        Assert.NotNull(title);
        var group = title!.GetLogicalAncestors().OfType<SettingsWindow.SettingsSection>().First();

        ScreenshotHelper.CaptureControl(group, name);
    }

    [AvaloniaFact]
    public void TheGroupWhenConnected()
    {
        PaperclipConnector.Current = new FakeConnector
        {
            Companies = [new("c1", "Example Org", "EX"), new("c2", "Another Org", "AO")],
        };
        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = "http://127.0.0.1:3100";
        OrbweaverSettings.PaperclipCompanyId = "c1";
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected. The key expires in 2 days.");

        Capture("settings-paperclip-connected.png");
    }

    [AvaloniaFact]
    public void TheGroupWhenItNeedsReconnecting()
    {
        PaperclipConnector.Current = new FakeConnector { Companies = [new("c1", "Example Org", "EX")] };
        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = "http://192.0.2.10:3100";
        OrbweaverSettings.PaperclipCompanyId = "c1";
        PaperclipSessions.SetLinkForTests(PaperclipLink.NeedsReconnect, "Reconnect to Paperclip");

        Capture("settings-paperclip-reconnect.png");
    }

    [AvaloniaFact]
    public void TheGroupWhenAnInstanceIsFound()
    {
        PaperclipConnector.Current = new FakeConnector { Found = [new("http://127.0.0.1:3100", "default", "authenticated")] };
        OrbweaverSettings.PaperclipEnabled = false;
        OrbweaverSettings.PaperclipUrl = "";

        Capture("settings-paperclip-found.png");
    }

    // The dimmed orb that "Reconnect to Paperclip" leaves on screen: parked,
    // idle, the explanation in its hover. What else it wears (the Paperclip
    // badge, marks, the ring) is the orb package's capture, not this one's.
    [AvaloniaFact]
    public void ADimmedOrbWhileTheKeyNeedsReplacing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.Paperclip,
            Kind = SessionKind.Unknown,
            State = "idle",
            Title = "Juno Abernathy",
            Cwd = "",
            Url = "http://127.0.0.1:3100/EX/agents/00000000-0000-4000-8000-000000000004",
            Presence = OrbPresence.Parked,
            HoverLines = new[] { "Reconnect to Paperclip" },
        });

        ScreenshotHelper.Capture(orb, "orb-window-paperclip-reconnect.png");
    }
}
