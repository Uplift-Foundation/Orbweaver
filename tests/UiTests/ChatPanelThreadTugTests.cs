using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// Sending a message in an agent's chat stops its thread tugging.
//
// Opening the chat already does (ThreadTugInteractionTests), so this is the
// belt-and-braces case: the panel that was open before the agent started working,
// or opened by a path that did not acknowledge. These open the panel straight
// through ChatPanel.OpenFor, which does not, so the only thing that can silence
// the thread here is the send.
[Collection("Settings")]
public class ChatPanelThreadTugTests : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();
    private long _now;
    private readonly bool _linksVisible = TeamLinks.IsVisible;

    public ChatPanelThreadTugTests()
    {
        // CB-490: these tests put a lone orb in the tug, which the app never
        // does: TeamLinks.Refresh keeps only team members there and forgets
        // everyone else. Refresh is static and anything can call it, most
        // often a frame timer some earlier test left running on the shared
        // dispatcher (a SessionManager's arrangement glide was the one that
        // reached CI), so it lands inside this test's flushes. With links
        // hidden Refresh returns before it touches the tug, whoever calls it.
        TeamLinks.SetVisible(false);
        ThreadTug.Reset();
        // And stop the tug's own frame timer if an earlier test left it
        // running: with no members NeedsFrames is false, and Drive stops the
        // timer rather than starting it. Hidden links make its tick harmless
        // here; this is what stops it ticking at all.
        ThreadTug.Drive(() => { });
        ThreadTug.Clock = () => _now;
        MotionPolicy.ReduceMotionProbe = () => false;
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
        // Before the reset: showing links again refreshes them, and the reset
        // and the Drive below are what leave nothing behind from that.
        TeamLinks.SetVisible(_linksVisible);
        ThreadTug.Reset();
        ThreadTug.Drive(() => { });
        ThreadTug.Clock = null;
        MotionPolicy.ReduceMotionProbe = null;
    }

    private FakeChatSession NewFake()
    {
        var id = "fake-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(null) { SessionId = id, DisplayName = "Fake Session" };
    }

    // Never closed, for the reason ChatPanelTests gives.
    private static OrbWindow GeneratingOrb()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, State = "generating", Title = "Nova" });
        return orb;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void Type(TextBox input, string text)
    {
        input.Focus();
        Flush();
        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        Flush();
    }

    private static void PressEnter(TextBox input)
    {
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();
    }

    private (OrbWindow Orb, TextBox Input) OpenWithATugRinging()
    {
        var orb = GeneratingOrb();
        ThreadTug.Observe(orb, orb.State);
        _now = 100;
        Assert.True(ThreadTug.Now(orb).Glow > 0, "setup: the tug should be ringing");

        ChatPanel.OpenFor(orb, NewFake());
        Flush();
        // Still ringing after the panel opened and the dispatcher ran: if
        // anything forgot this orb, a failure says so here, not after the send.
        Assert.True(ThreadTug.Now(orb).Glow > 0, "setup: the tug should still be ringing once the panel is open");

        return (orb, ChatPanelTestAccess.Instance!.FindControl<TextBox>("Input")!);
    }

    [AvaloniaFact]
    public void SendingAMessageSilencesTheAgentsTug()
    {
        var (orb, input) = OpenWithATugRinging();

        Type(input, "hello");
        // The text landed, so a failure below is about the send, not focus.
        Assert.Equal("hello", input.Text);
        PressEnter(input);

        Assert.Equal((0, 0), ThreadTug.Now(orb));

        // And it stays quiet while the agent is still generating.
        _now = (long)(ThreadTug.Period * 1000) * 2;
        ThreadTug.Observe(orb, orb.State);
        Assert.Equal((0, 0), ThreadTug.Now(orb));
        Assert.False(ThreadTug.NeedsFrames());
    }

    // CB-490: what failed on the Windows runner, forced. An overlay refresh
    // arriving mid-test, from anyone, must not forget the orb: before the
    // constructor hid the links this dropped it, the send then acknowledged
    // nobody, and the next Observe started a fresh tug.
    [AvaloniaFact]
    public void AnOverlayRefreshArrivingMidTestDoesNotForgetTheOrb()
    {
        var (orb, input) = OpenWithATugRinging();

        TeamLinks.Refresh();
        Assert.True(ThreadTug.Now(orb).Glow > 0, "the tug should survive a refresh from elsewhere");

        Type(input, "hello");
        TeamLinks.Refresh();
        PressEnter(input);
        TeamLinks.Refresh();

        _now = (long)(ThreadTug.Period * 1000) * 2;
        ThreadTug.Observe(orb, orb.State);
        Assert.Equal((0, 0), ThreadTug.Now(orb));
    }

    // Controls: without these a pass could mean the tug was silenced some other
    // way — by opening the panel, or by pressing Enter at all.
    [AvaloniaFact]
    public void OpeningThePanelAloneDoesNotSilenceItHere()
    {
        var (orb, _) = OpenWithATugRinging();

        Assert.True(ThreadTug.Now(orb).Glow > 0);
    }

    [AvaloniaFact]
    public void PressingEnterOnAnEmptyBoxSendsNothingAndSilencesNothing()
    {
        var (orb, input) = OpenWithATugRinging();

        PressEnter(input);

        Assert.True(ThreadTug.Now(orb).Glow > 0);
    }
}
