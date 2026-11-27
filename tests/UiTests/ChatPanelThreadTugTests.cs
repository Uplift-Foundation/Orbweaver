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

    public ChatPanelThreadTugTests()
    {
        ThreadTug.Reset();
        // CB-490: stop the tug's frame timer if an earlier test left it running.
        // Its tick is TeamLinks.Refresh, which keeps only team members in the
        // tug, so on the shared PerAssembly dispatcher a tick landing in this
        // test's first Flush forgot this orb: the send's acknowledge then had
        // nothing to silence, and the next Observe started a fresh tug, (0, 1)
        // at the second assert. With no members NeedsFrames is false, and Drive
        // stops the timer rather than starting it.
        ThreadTug.Drive(() => { });
        ThreadTug.Clock = () => _now;
        MotionPolicy.ReduceMotionProbe = () => false;
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
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
