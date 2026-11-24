using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-443: what a click on a Paperclip orb gets from the manager. No orb is clicked.
[Collection("Settings")]
public class PaperclipChatWiringTests : IDisposable
{
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly Func<Func<ChatTarget>, IPaperclipChatFeed> _factory = SessionManager.PaperclipFeedFactory;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb443-wire-" + Guid.NewGuid());

    public PaperclipChatWiringTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        SessionManager.PaperclipFeedFactory = _factory;
        PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");
        PaperclipSessions.SetSnapshotForTests([]);
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
        OrbweaverSettings.PaperclipEnabled = _enabled;
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private SessionManager Manager() =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { _scratch });

    private static Dictionary<string, OrbWindow> Orbs(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static PaperclipAgent A(string id) =>
        new(id, "Agent " + id, "r", "t", "idle", null, "paperclip_runner", "idle", PaperclipMark.None, null, [], null, null, null, null, null,
            DateTime.UtcNow.AddSeconds(-5), "https://p/EX/agents/" + id, true);

    [AvaloniaFact]
    public void APaperclipOrbGetsOneCachedSession_BuiltWithItsAgentIdAndTitle_AndDroppedWithTheOrb()
    {
        var feeds = new List<PaperclipChatPanelTests.FakeFeed>();
        SessionManager.PaperclipFeedFactory = _ => { var f = new PaperclipChatPanelTests.FakeFeed(); feeds.Add(f); return f; };
        OrbweaverSettings.PaperclipEnabled = true;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected");
        PaperclipSessions.SetSnapshotForTests([A("x")]);
        var m = Manager();
        m.ScanAndUpdate();

        var chat = m.RemoteChatFor("paperclip:x");
        var session = Assert.IsType<PaperclipChatSession>(chat);
        Assert.Same(chat, m.RemoteChatFor("paperclip:x"));
        Assert.Single(feeds);
        Assert.Equal("paperclip:x", session.SessionId);
        Assert.Equal("https://p/EX/agents/x", session.ReplyUrl);
        Assert.Contains("Agent x", session.ComposerHint);

        // The panel's own words come from the live roster and actions seams.
        session.PanelOpened();
        Assert.Equal(new[] { "opened" }, feeds[0].Calls);

        PaperclipSessions.SetSnapshotForTests([]);
        m.ScanAndUpdate();
        Assert.False(Orbs(m).ContainsKey("paperclip:x"));
        Assert.Equal(new[] { "opened", "closed" }, feeds[0].Calls);
    }

    [AvaloniaFact]
    public void ACloudOrbStillGetsACloudSession_AndANonPaperclipSourceNeverGetsThisOne()
    {
        Assert.False(ClickRouting.OpensInABrowser(new SessionStatus { Source = SessionSource.Paperclip }));
        Assert.True(ClickRouting.OpensInABrowser(new SessionStatus { Source = SessionSource.ClaudeCloud }));
    }

    [AvaloniaFact]
    public void TheLiveFeedNeedsAConnectedCompany_AndIsIdleWithout()
    {
        var before = PaperclipSessions.ConfigSource;
        try
        {
            PaperclipSessions.ConfigSource = () => PaperclipSessions.PaperclipConfig.Off;
            Assert.Null(PaperclipSessions.LiveChatApi());
            var idle = SessionManager.PaperclipFeedFactory(() => new ChatTarget("a", null, null, false));
            Assert.IsType<IdleChatFeed>(idle);
            idle.Updated += _ => { }; idle.Updated -= _ => { };
            idle.Opened(); idle.Nudge(); idle.Closed();

            PaperclipSessions.PaperclipConfig On(string key, string url, string company) =>
                new(true, url, company, key, null, "fold");
            PaperclipSessions.ConfigSource = () => On("k1", "http://127.0.0.1:1", "co1");
            var first = PaperclipSessions.LiveChatApi();
            Assert.NotNull(first);
            Assert.Equal("co1", first!.Value.CompanyId);
            Assert.Same(first.Value.Api, PaperclipSessions.LiveChatApi()!.Value.Api);

            // A new key rebuilds the client.
            PaperclipSessions.ConfigSource = () => On("k2", "http://127.0.0.1:1", "co1");
            Assert.NotSame(first.Value.Api, PaperclipSessions.LiveChatApi()!.Value.Api);
        }
        finally { PaperclipSessions.ConfigSource = before; }
    }

    [AvaloniaFact]
    public void ThePollsRunsAndBlockedTasksReachTheChatView()
    {
        PaperclipSessions.SetChatViewForTests(new([], [], []));
        var live = new PaperclipApi.RawLiveRun("r1", "a", "running", null, "i1", null, null);
        var blocked = new PaperclipApi.RawIssue("i2", "CB-2", "t", "blocked", "a");
        var st = PaperclipSessions.PollState.Initial with { LiveRuns = [live], Blocked = [blocked] };
        PaperclipSessions.PublishStep(st, PaperclipSessions.PaperclipConfig.Off, [], new NullRosterStore(), DateTime.UtcNow);
        Assert.Equal("r1", Assert.Single(PaperclipSessions.CurrentChatView.Live).Id);
        Assert.Equal("i2", Assert.Single(PaperclipSessions.CurrentChatView.Blocked).Id);
        Assert.Empty(PaperclipSessions.CurrentChatView.Runs);
        PaperclipSessions.SetChatViewForTests(new([], [], []));
    }
}
