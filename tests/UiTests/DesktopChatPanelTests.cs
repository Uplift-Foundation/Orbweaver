using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// The chat panel bound to a *real* LocalCliChatSession for a session Claude
// Desktop hosts (CB-265), rather than a fake told to be read-only. The fake
// would pass whatever the production class decided; the question here is
// whether LocalCliChatSession itself says "read-only" and what the panel then
// draws — no composer, the hint where it was, no link, and no attach button
// offered for a conversation `claude attach` could not find. CB-266 adds the
// other half: a chat on Remote Control is sendable, and the panel follows the
// flip live, fed by fakes for the API and the login so no network is touched.
[Collection("Settings")]
public class DesktopChatPanelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-desktop-chat-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _toClean = new();

    // "Replying" is a per-CLI setting that is off until a user turns it on; the
    // bridged rows only mean anything with it on, so the tests turn it on and put
    // it back.
    private readonly bool _replyBefore = OrbweaverSettings.ClaudeCodeReplyEnabled;

    public DesktopChatPanelTests()
    {
        Directory.CreateDirectory(_root);
        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
    }

    public void Dispose()
    {
        OrbweaverSettings.ClaudeCodeReplyEnabled = _replyBefore;
        foreach (var id in _toClean) ChatPanel.CloseFor(id);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private string Transcript()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path,
            "{\"type\":\"user\",\"uuid\":\"u1\",\"timestamp\":\"2026-10-14T12:00:00Z\","
            + "\"message\":{\"role\":\"user\",\"content\":\"summarise the open PRs\"}}\n"
            + "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-10-14T12:00:01Z\","
            + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\","
            + "\"text\":\"Three are ready to merge; one is waiting on review.\"}]}}\n");
        return path;
    }

    private static SessionStatus DesktopStatus(string transcript, string entrypoint = SessionStatus.DesktopEntrypoint) => new()
    {
        Source = SessionSource.ClaudeCode,
        Cli = "claude",
        Entrypoint = entrypoint,
        TranscriptPath = transcript,
        SessionPid = Environment.ProcessId,
        State = "idle",
    };

    private LocalCliChatSession Open(SessionStatus status)
    {
        var id = "desktop-" + Guid.NewGuid();
        _toClean.Add(id);
        var session = new LocalCliChatSession(id, status) { ReadBudget = TestBudgets.AlwaysAnswers };
        session.Start();

        for (var i = 0; i < 400 && session.History.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        // Fails here, by name, rather than carrying on into a panel with nothing in
        // it and failing three assertions later about something else (CB-269).
        Assert.True(session.History.Count > 0, "the session never loaded its transcript.");

        ChatPanel.OpenFor(NewOrb(), session);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return session;
    }

    private static T Find<T>(string name) where T : Control =>
        ChatPanelTestAccess.Instance!.FindControl<T>(name)!;

    // --- the session itself ---------------------------------------------------

    [AvaloniaFact]
    public void ADesktopSessionIsReadOnlyWithNowhereToLinkTo()
    {
        var session = new LocalCliChatSession("s1", DesktopStatus(Transcript())) { ReadBudget = TestBudgets.AlwaysAnswers };

        Assert.True(session.IsReadOnly);
        Assert.Null(session.ReplyUrl);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, session.ComposerHint);
        Assert.False(session.CanOpenElsewhere);

        // Subscribing and unsubscribing is harmless; the raising is covered below.
        void Handler() { }
        session.ReadOnlyChanged += Handler;
        session.ReadOnlyChanged -= Handler;
    }

    // Negative controls: the same transcript under any other entrypoint is the
    // ordinary writable session it always was.
    [AvaloniaTheory]
    [InlineData("cli")]
    [InlineData("sdk-cli")]
    [InlineData("")]
    public void TheSameSessionUnderAnyOtherEntrypointIsNotReadOnly(string entrypoint)
    {
        var session = new LocalCliChatSession("s1", DesktopStatus(Transcript(), entrypoint)) { ReadBudget = TestBudgets.AlwaysAnswers };

        Assert.False(session.IsReadOnly);
        Assert.NotEqual(LocalCliChatSession.DesktopNoBridgeHint, session.ComposerHint);
    }

    // --- the panel ---------------------------------------------------------------

    [AvaloniaFact]
    public void ThePanelHidesTheComposerAndSaysWhereTypingGoes()
    {
        Open(DesktopStatus(Transcript()));

        Assert.False(Find<Grid>("ComposerRow").IsVisible);
        Assert.True(Find<Control>("ReadOnlyBox").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, Find<TextBlock>("ReadOnlyNote").Text);
        Assert.False(Find<TextBlock>("ReadOnlyLink").IsVisible);
        Assert.False(Find<TextBlock>("SessionLink").IsVisible);
        Assert.False(Find<Control>("AttachButton").IsVisible);
    }

    // The control for the panel half: an ordinary session bound the same way
    // keeps its composer, so the assertions above are about the entrypoint and
    // not about something every local session does.
    [AvaloniaFact]
    public void AnOrdinarySessionKeepsItsComposerAndHasNoReadOnlyBox()
    {
        Open(DesktopStatus(Transcript(), entrypoint: "cli"));

        Assert.False(Find<Control>("ReadOnlyBox").IsVisible);
    }

    // --- CB-266: a chat on Remote Control can be sent into -----------------------

    private const string Bridge = "session_01DesktopBridgeAbc123";

    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<CloudApiResult> _answer;

        internal FakeApi(Func<CloudApiResult> answer) => _answer = answer;

        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(_answer());
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";

        public CredentialRead Read() =>
            new(CredentialOutcome.Found, Token, null, "a credential is present");
    }

    private static FakeApi Accepting() => new(() => new CloudApiResult(CloudOutcomes.OutcomeFor(200, "{}"), "{}"));

    private static FakeApi Refusing(int status) => new(() => new CloudApiResult(CloudOutcomes.OutcomeFor(status, "{}"), null));

    private static SessionStatus Bridged(string transcript, string bridge = Bridge)
    {
        var status = DesktopStatus(transcript);
        status.BridgeSessionId = bridge;
        return status;
    }

    private LocalCliChatSession OpenWith(SessionStatus status, ICloudApi? api, Func<bool> cloudOn)
    {
        var id = "desktop-" + Guid.NewGuid();
        _toClean.Add(id);
        var session = new LocalCliChatSession(id, status,
            cloudApi: api,
            credentialsFor: _ => new FakeCredentials(),
            cloudEnabled: cloudOn)
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Delay = (_, _) => Task.CompletedTask,
        };
        session.Start();

        for (var i = 0; i < 400 && session.History.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(session.History.Count > 0, "the session never loaded its transcript.");

        ChatPanel.OpenFor(NewOrb(), session);
        Redraw();
        return session;
    }

    private static void Redraw()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitFor(Func<bool> done,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(done))] string what = "")
    {
        for (var i = 0; i < 600 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Redraw();

        // Names the condition that never came true. It used to fall through and
        // let the next assertion fail instead, about whatever that happened to be.
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static void TypeAndEnter(string text)
    {
        var input = Find<TextBox>("Input");
        input.Text = text;
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
    }

    [AvaloniaFact]
    public void ABridgedChatWithCloudOnShowsTheComposerWithTheTeammateWatermarkAndTheLink()
    {
        var session = OpenWith(Bridged(Transcript()), Accepting(), () => true);

        Assert.True(Find<Grid>("ComposerRow").IsVisible);
        Assert.False(Find<Control>("ReadOnlyBox").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopBridgedHint, Find<TextBox>("Input").Watermark);
        Assert.Contains("teammate", Find<TextBox>("Input").Watermark!);

        var link = Find<TextBlock>("SessionLink");
        Assert.True(link.IsVisible);
        Assert.Equal("https://claude.ai/code/" + Bridge, session.ReplyUrl);
    }

    [AvaloniaFact]
    public void ABridgedChatWithCloudOffIsReadOnlySaysSoAndKeepsTheLink()
    {
        OpenWith(Bridged(Transcript()), Accepting(), () => false);

        Assert.False(Find<Grid>("ComposerRow").IsVisible);
        Assert.True(Find<Control>("ReadOnlyBox").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopCloudOffHint, Find<TextBlock>("ReadOnlyNote").Text);
        Assert.True(Find<TextBlock>("ReadOnlyLink").IsVisible);
        Assert.False(Find<TextBlock>("SessionLink").IsVisible);
    }

    // The flip in both directions, on the one bound panel: no rebinding, only
    // UpdateStatus, which is what the scan calls every tick.
    [AvaloniaFact]
    public void TheComposerAppearsAndGoesAwayLiveAsTheBridgeComesAndGoes()
    {
        var transcript = Transcript();
        var session = OpenWith(DesktopStatus(transcript), Accepting(), () => true);
        var raised = 0;
        session.ReadOnlyChanged += () => raised++;

        Assert.False(Find<Grid>("ComposerRow").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, Find<TextBlock>("ReadOnlyNote").Text);

        session.UpdateStatus(Bridged(transcript));
        Redraw();

        Assert.Equal(1, raised);
        Assert.True(Find<Grid>("ComposerRow").IsVisible);
        Assert.False(Find<Control>("ReadOnlyBox").IsVisible);
        Assert.True(Find<TextBlock>("SessionLink").IsVisible);

        // The same status again changes nothing and says nothing.
        session.UpdateStatus(Bridged(transcript));
        Assert.Equal(1, raised);

        session.UpdateStatus(DesktopStatus(transcript));
        Redraw();

        Assert.Equal(2, raised);
        Assert.False(Find<Grid>("ComposerRow").IsVisible);
        Assert.True(Find<Control>("ReadOnlyBox").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, Find<TextBlock>("ReadOnlyNote").Text);
        Assert.False(Find<TextBlock>("SessionLink").IsVisible);
    }

    [AvaloniaFact]
    public void ASendThroughTheApiDrawsOneBubbleAndClearsTheBox()
    {
        var api = Accepting();
        var session = OpenWith(Bridged(Transcript()), api, () => true);
        var before = session.History.Count;

        TypeAndEnter("please rebase the third PR");
        WaitFor(() => session.History.Count > before);

        Assert.Single(api.Requests);
        Assert.Equal(before + 1, session.History.Count);
        Assert.Equal(ChatRole.User, session.History[^1].Role);
        Assert.Equal("please rebase the third PR", session.History[^1].Text);
        WaitFor(() => string.IsNullOrEmpty(Find<TextBox>("Input").Text));
        Assert.True(string.IsNullOrEmpty(Find<TextBox>("Input").Text));
    }

    // A refusal keeps the sentence where it was typed, draws no bubble, and
    // takes the composer away with the reason — the panel already re-reads on
    // ReadOnlyChanged, so this is the whole chain from API answer to pixels.
    [AvaloniaFact]
    public void ARefusedSendKeepsTheTextAndFlipsThePanelReadOnly()
    {
        var api = Refusing(409);
        var session = OpenWith(Bridged(Transcript()), api, () => true);
        var before = session.History.Count;

        TypeAndEnter("this will be refused");
        WaitFor(() => Find<Control>("ReadOnlyBox").IsVisible);

        Assert.Single(api.Requests);
        Assert.Equal("this will be refused", Find<TextBox>("Input").Text);
        Assert.False(Find<Grid>("ComposerRow").IsVisible);
        Assert.Equal(LocalCliChatSession.DesktopBridgeGoneHint, Find<TextBlock>("ReadOnlyNote").Text);
        Assert.True(Find<TextBlock>("ReadOnlyLink").IsVisible);
        Assert.DoesNotContain(session.History.Skip(before), t => t.Text == "this will be refused");
    }
}
