using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// What the chat panel does with a session it can read and not write to.
//
// Two questions, and they are not the same one. The first is what the panel
// does with IRemoteChatReadOnly — there is no composer, and there is a sentence
// where it was. The second is what a *failed read* looks like, which is the
// thing this file exists for: an unreadable conversation and an empty one are
// pixel-identical unless something insists otherwise, and the panel's only way
// of insisting is the connection dot.
//
// The real ClaudeCloudChatSession is used for the second half rather than a
// fake, against a fake ICloudApi. A fake session could be told to report Error
// and the test would pass without the production code ever having decided
// anything — the whole question is whether a refused endpoint *ends up* in
// Error rather than in an empty Connected.
[Collection("Settings")]
public class CloudChatPanelTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Controls.Controls RenderedRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    private static Color ColourOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    private static Control ComposerRow(ChatPanel panel) =>
        panel.FindControl<Grid>("ComposerRow")!;

    private static Control ReadOnlyBox(ChatPanel panel) =>
        panel.FindControl<Control>("ReadOnlyBox")!;

    private static TextBlock ReadOnlyNote(ChatPanel panel) =>
        panel.FindControl<TextBlock>("ReadOnlyNote")!;

    private static TextBlock ReadOnlyLink(ChatPanel panel) =>
        panel.FindControl<TextBlock>("ReadOnlyLink")!;

    private static TextBlock SessionLink(ChatPanel panel) =>
        panel.FindControl<TextBlock>("SessionLink")!;

    private static Control StopButton(ChatPanel panel) =>
        panel.FindControl<Grid>("StopButton")!;

    private static TextBox Input(ChatPanel panel) =>
        panel.FindControl<TextBox>("Input")!;

    // A real routed PointerPressed on the control, which is what the panel's own
    // handler is attached to — the same helper shape ChatPanelInteractionTests
    // uses, so the production click path runs rather than a method called round
    // it.
    private static void Click(Control control, Control root)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, root, new Avalonia.Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static void PressEnter(TextBox input) =>
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    // --- a fake API, shaped after the one in tests/UnitTests ---

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
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private const string UserTemplate =
        """{"type":"user","uuid":"UUID","timestamp":"2026-09-19T10:00:00Z","message":{"role":"user","content":[{"type":"text","text":"TEXT"}]}}""";

    private const string AssistantTemplate =
        """{"type":"assistant","uuid":"UUID","timestamp":"2026-09-19T10:00:05Z","message":{"role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":10,"output_tokens":20},"stop_reason":"end_turn"}}""";

    private static string Row(string template, string uuid, string text) =>
        template.Replace("UUID", uuid, StringComparison.Ordinal)
            .Replace("TEXT", text, StringComparison.Ordinal);

    private static string Envelope(params string[] rows) =>
        "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":false,\"last_id\":null}";

    private static ClaudeCloudSessions.Session Session(string id) =>
        new(id, "a cloud session", "idle",
            new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // The post hook runs actions inline, so a test can await LoadAsync and then
    // assert without chasing the dispatcher — the same seam and the same reason
    // the unit suite uses it.
    private static ClaudeCloudChatSession Chat(string id, ICloudApi api,
        ICloudCredentialSource? creds = null) =>
        new(Session(id), api, creds ?? new FakeCredentials(), action => action())
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
        };

    // --- the composer ---

    // The whole of CB-164's composer decision, driven through the fake so both
    // sides of it come from one class.
    [AvaloniaFact]
    public void AReadOnlySessionGetsNoComposerAtAll()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-readonly-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "this cloud session can be read here and replied to at claude.ai/code",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.False(ComposerRow(panel).IsVisible);
    }

    // Hidden, not merely emptied — and the hint is kept, because hiding the box
    // and explaining nothing leaves a panel that looks truncated rather than
    // one that has said something.
    [AvaloniaFact]
    public void TheHintTakesTheComposersPlaceRatherThanVanishingWithIt()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-hint-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "this cloud session can be read here and replied to at claude.ai/code",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ReadOnlyBox(panel).IsVisible);
        Assert.Equal(fake.ComposerHint, ReadOnlyNote(panel).Text);
    }

    // The link is the half that matters, and it is a link rather than the
    // address in prose: an orb click goes to the session, and a panel that can
    // only *name* where the session lives is asking the user to go and find it.
    [AvaloniaFact]
    public void AReadOnlySessionOffersALinkToWhereItCanBeRepliedTo()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-link-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var link = ReadOnlyLink(ChatPanelTestAccess.Instance!);

        Assert.True(link.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(link.Text));

        // The label is a label and not the address. A per-session URL is long
        // enough to swamp the sentence beside it, and what the click does is the
        // part worth reading.
        Assert.DoesNotContain("https://", link.Text!);
    }

    // The link looks like one under the pointer.
    //
    // Small, and it is the only thing that says the sentence is clickable — the
    // text is a plain TextBlock in the panel's own foreground, so without the
    // underline a user has no way of knowing there is anything to press. Driven
    // with raised routed events rather than a real pointer, the way
    // SettingsWindow's own link rows are: a synthesized mouse on a machine
    // someone is using interleaves with their real input.
    [AvaloniaFact]
    public void TheReadOnlyLinkUnderlinesUnderThePointerAndClearsWhenItLeaves()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-hover-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var link = ReadOnlyLink(ChatPanelTestAccess.Instance!);

        // Nothing drawn until the pointer is over it, which is the half a test
        // that only hovered would not catch.
        Assert.Null(link.TextDecorations);

        Hover(link, InputElement.PointerEnteredEvent);
        Assert.Equal(TextDecorations.Underline, link.TextDecorations);

        Hover(link, InputElement.PointerExitedEvent);
        Assert.Null(link.TextDecorations);
    }

    private static void Hover(Control target, RoutedEvent<PointerEventArgs> which) =>
        target.RaiseEvent(new PointerEventArgs(
            which, target,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true),
            target, default, 0, new PointerPointProperties(), KeyModifiers.None));

    // No address, no link — rather than a link that opens nothing. Both halves
    // asserted because a link drawn unconditionally would pass the case above.
    [AvaloniaFact]
    public void AReadOnlySessionWithNowhereToGoDrawsNoLink()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-nolink-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = null,
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ReadOnlyBox(panel).IsVisible);
        Assert.False(ReadOnlyLink(panel).IsVisible);
    }

    // A real cloud session carries the address the roster gave it, rather than
    // one rebuilt at the panel — which is the point of Session.Url existing.
    [AvaloniaFact]
    public void ACloudSessionsLinkIsTheRostersOwnAddress()
    {
        var id = "session_link_" + Guid.NewGuid().ToString("N");
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""), Envelope()));

        var chat = Chat(id, api);

        Assert.Equal("https://claude.ai/code/" + id, ((IRemoteChatReadOnly)chat).ReplyUrl);
    }

    // The negative control for the pair above: an ordinary session is untouched
    // by any of this. Without it, a bug that hid every composer would pass both
    // of the assertions above.
    [AvaloniaFact]
    public void AnOrdinarySessionKeepsItsComposer()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-ordinary-" + Guid.NewGuid(),
            IsReadOnly = false,
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);
    }

    // ...and the panel is reused across orbs, so a read-only session must not
    // leave the next conversation without a box. That is the failure this shape
    // of panel has had before — see the class comment on ChatPanel about the
    // transient being rebound rather than recreated.
    [AvaloniaFact]
    public void TheComposerComesBackForTheNextSession()
    {
        var readOnly = new FakeChatSession(null)
        {
            SessionId = "cloud-first-" + Guid.NewGuid(),
            IsReadOnly = true,
        };
        var ordinary = new FakeChatSession(null)
        {
            SessionId = "cloud-second-" + Guid.NewGuid(),
        };
        _toClean.Add(readOnly.SessionId);
        _toClean.Add(ordinary.SessionId);

        var orb = NewOrb();
        ChatPanel.OpenFor(orb, readOnly);
        FlushRender();
        Assert.False(ComposerRow(ChatPanelTestAccess.Instance!).IsVisible);

        ChatPanel.OpenFor(orb, ordinary);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);
    }

    // --- sending (CB-199) ---

    // A cloud session that may be written to looks like any other: the box, and
    // no sentence in its place. Stop is not there either, because nothing is
    // running — hidden rather than greyed, per CB-59.
    private FakeChatSession SendableCloud(string prefix, bool canInterrupt = false)
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = prefix + "-" + Guid.NewGuid(),
            IsReadOnly = false,
            CanInterrupt = canInterrupt,
            ComposerHint = "Message\u2026",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);
        return fake;
    }

    [AvaloniaFact]
    public void ASendableCloudSessionGetsTheComposerAndNoReadOnlyBox()
    {
        var fake = SendableCloud("cloud-sendable");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);
        Assert.False(StopButton(panel).IsVisible);
    }

    // Enter sends what was typed, and the box is cleared once the session says
    // it went. The session raises the user's own turn itself (the interface's
    // rule 3), so the bubble comes from there rather than from the panel.
    [AvaloniaFact]
    public void TypingAndEnterSendsIntoACloudSessionAndClearsTheBox()
    {
        var fake = SendableCloud("cloud-send");

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = Input(panel);

        input.Text = "run the tests again";
        PressEnter(input);
        Flush();

        Assert.Equal(new[] { "run the tests again" }, fake.SentTexts);
        Assert.Equal("", input.Text ?? "");
        Assert.Single(fake.History);
    }

    // ...and a refused send keeps the text. The note explaining the refusal is
    // the session's to write; what the panel owes is not losing the paragraph.
    [AvaloniaFact]
    public void ARefusedCloudSendKeepsTheTextInTheBox()
    {
        var fake = SendableCloud("cloud-refused");
        fake.SendOutcome = ChatSendOutcome.Failed;

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var input = Input(ChatPanelTestAccess.Instance!);

        input.Text = "this will be refused";
        PressEnter(input);
        Flush();

        Assert.Equal(new[] { "this will be refused" }, fake.SentTexts);
        Assert.Equal("this will be refused", input.Text);
    }

    // **The reason ReadOnlyChanged exists.** A session that turns read-only while
    // the panel is open — the server has said it ended — must lose its box then,
    // not at the next bind, or the next paragraph typed into it is lost. And it
    // comes back if the session changes its mind, so the handler is a re-read
    // rather than a one-way latch.
    [AvaloniaFact]
    public void TurningReadOnlyMidPanelSwapsTheComposerForTheNoteAndBack()
    {
        var fake = SendableCloud("cloud-flip");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(ComposerRow(panel).IsVisible);

        fake.ComposerHint = "This session has ended, so it can\u2019t be replied to.";
        fake.RaiseReadOnlyChanged(true);
        Flush();

        Assert.False(ComposerRow(panel).IsVisible);
        Assert.True(ReadOnlyBox(panel).IsVisible);
        Assert.Equal(fake.ComposerHint, ReadOnlyNote(panel).Text);

        // The link comes with the flip, since it is re-read with the rest.
        Assert.True(ReadOnlyLink(panel).IsVisible);

        fake.ComposerHint = "Message\u2026";
        fake.RaiseReadOnlyChanged(false);
        Flush();

        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);
    }

    // Stop is there exactly while the session says a turn can be interrupted, a
    // click reaches the session once, and it goes away when the session says
    // there is nothing left to stop.
    [AvaloniaFact]
    public void StopShowsWhileATurnCanBeInterruptedAndAClickCancelsIt()
    {
        var fake = SendableCloud("cloud-stop");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var stop = StopButton(panel);
        Assert.False(stop.IsVisible);

        fake.RaiseInterruptChanged(true);
        Flush();
        Assert.True(stop.IsVisible);

        Click(stop, panel);
        Flush();
        Assert.Equal(1, fake.CancelCalls);

        fake.RaiseInterruptChanged(false);
        Flush();
        Assert.False(stop.IsVisible);
    }

    // A press that lands after the session said there is nothing to stop — the
    // race between a turn ending and a click — reaches no Cancel. The button is
    // hidden by then, so the press is synthesized directly.
    [AvaloniaFact]
    public void AStopPressWithNothingToStopReachesNoCancel()
    {
        var fake = SendableCloud("cloud-stop-late");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Click(StopButton(panel), panel);
        Flush();

        Assert.Equal(0, fake.CancelCalls);
    }

    // Already interruptible when the panel opens — a reply was running before
    // anybody clicked the orb — shows Stop from the first frame rather than
    // waiting for a change that may never come.
    [AvaloniaFact]
    public void StopIsShownAtBindForASessionAlreadyMidTurn()
    {
        var fake = SendableCloud("cloud-stop-bind", canInterrupt: true);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        Assert.True(StopButton(ChatPanelTestAccess.Instance!).IsVisible);
    }

    // Never over a read-only session, even one that (wrongly) still claims an
    // interruptible turn: there is no composer for it to belong to, and the
    // button's own visibility says so rather than leaning on its parent row.
    [AvaloniaFact]
    public void StopIsHiddenForAReadOnlySession()
    {
        var fake = SendableCloud("cloud-stop-readonly", canInterrupt: true);
        fake.IsReadOnly = true;

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.False(StopButton(panel).IsVisible);

        // ...and turning read-only mid-turn takes it away too.
        fake.RaiseReadOnlyChanged(false);
        Flush();
        Assert.True(StopButton(panel).IsVisible);

        fake.RaiseReadOnlyChanged(true);
        Flush();
        Assert.False(StopButton(panel).IsVisible);
    }

    // A long reason wraps, and the link stays on the panel. The box was a
    // horizontal StackPanel, which measures with infinite width: the sentence
    // never wrapped and pushed the link off the right edge, which only the
    // ended-session capture showed. Asserted on layout bounds rather than
    // pixels so it runs here, under the null renderer.
    [AvaloniaFact]
    public void ALongReadOnlyReasonWrapsAndKeepsTheLinkInsideThePanel()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-long-reason-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This session has ended, so it can\u2019t be replied to here or "
                           + "anywhere else, and this sentence is long enough to need two lines.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var box = ReadOnlyBox(panel);
        var link = ReadOnlyLink(panel);

        Assert.True(box.Bounds.Width > 0);
        Assert.True(link.Bounds.Right <= box.Bounds.Width,
            $"link ends at {link.Bounds.Right}, box is {box.Bounds.Width} wide");
        Assert.True(ReadOnlyNote(panel).Bounds.Right <= link.Bounds.Left);
    }

    // The panel is reused across orbs, so a session it has left must not reach
    // it any more. Raised after the move, each event would otherwise redraw the
    // *new* conversation's composer from the old one's answers — a refusal in
    // one cloud session taking the box away from an unrelated one.
    [AvaloniaFact]
    public void ASessionThePanelHasLeftNoLongerChangesIt()
    {
        var first = SendableCloud("cloud-left");
        var second = SendableCloud("cloud-next");

        var orb = NewOrb();
        ChatPanel.OpenFor(orb, first);
        FlushRender();

        ChatPanel.OpenFor(orb, second);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        // The handlers re-read the *bound* session, so a lingering subscription
        // is only visible if the bound one would redraw differently. So each
        // check first puts `second` in such a state, then has `first` announce.
        second.CanInterrupt = true;
        first.RaiseInterruptChanged(true);
        Flush();
        Assert.False(StopButton(panel).IsVisible);

        second.IsReadOnly = true;
        first.RaiseReadOnlyChanged(true);
        Flush();
        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(ReadOnlyBox(panel).IsVisible);

        // The negative control: the same mutation announced by the session that
        // *is* bound does redraw, so the assertions above are about the
        // subscription and not about a handler that never runs.
        second.RaiseReadOnlyChanged(true);
        Flush();
        Assert.False(ComposerRow(panel).IsVisible);
    }

    // The panel tells a real cloud session when it is being looked at, and when
    // it stops being looked at (PanelOpened / PanelClosed, by concrete type).
    //
    // The state is read off the session's PanelOpen, and that is deliberate.
    // What the flag licenses — one transcript read when a turn finishes — goes
    // through Task.Run and a single-flight guard, so a request count can show
    // the open half (awaited below) but can never show the closed half: a read
    // the closed panel wrongly started and a read started afterwards collapse
    // into the same one request. The flag is the only thing that can tell those
    // two apart.
    private static bool PanelOpenFlag(ClaudeCloudChatSession chat) => chat.PanelOpen;

    [AvaloniaFact]
    public async Task BindAndUnbindTellACloudSessionWhetherAPanelIsOpen()
    {
        var id = "session_panel_" + Guid.NewGuid().ToString("N");
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi(() =>
        {
            firstRequest.TrySetResult();
            return new CloudApiResult(
                CloudOutcomes.OutcomeFor(200, ""), Envelope(Row(UserTemplate, "u1", "hello")));
        });
        var chat = Chat(id, api);
        _toClean.Add(chat.SessionId);

        Assert.False(PanelOpenFlag(chat));

        var orb = NewOrb();
        ChatPanel.OpenFor(orb, chat);
        FlushRender();

        Assert.True(PanelOpenFlag(chat));

        // ...and that open panel is what gets a finished turn read. Awaited on
        // the request itself rather than a sleep, and bounded so a regression
        // fails instead of hanging the suite.
        chat.UpdateStatus(Session(id) with { State = "generating", StatusBucket = "working" });
        chat.UpdateStatus(Session(id));
        var reached = await Task.WhenAny(firstRequest.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(firstRequest.Task, reached);

        // Moving the panel to another session is the Unbind.
        var other = new FakeChatSession(null) { SessionId = "cloud-after-" + Guid.NewGuid() };
        _toClean.Add(other.SessionId);
        ChatPanel.OpenFor(orb, other);
        FlushRender();

        Assert.False(PanelOpenFlag(chat));
    }

    // --- a read that worked ---

    [AvaloniaFact]
    public async Task ACloudTranscriptRendersItsTurns()
    {
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""),
            Envelope(
                Row(UserTemplate, "u1", "run the tests"),
                Row(AssistantTemplate, "a1", "running them now"))));

        var chat = Chat("session_ok_" + Guid.NewGuid().ToString("N"), api);
        _toClean.Add(chat.SessionId);

        Assert.True(await chat.LoadAsync(CancellationToken.None));

        ChatPanel.OpenFor(NewOrb(), chat);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(2, RenderedRows(panel).Count);

        // CB-199: a cloud session is writable until the server refuses it, so a
        // transcript that read cleanly comes with a box under it.
        Assert.True(ComposerRow(panel).IsVisible);
    }

    // --- a read that did not ---

    // **The point of this file.** A refused endpoint must not leave the panel
    // showing a conversation with nothing in it, because that is exactly what a
    // genuinely empty session looks like and the user has no way to tell them
    // apart. LoadAsync answers false and leaves itself in Error; the panel's
    // connection dot is what carries that onto the screen.
    [AvaloniaFact]
    public async Task AnUnreadableCloudSessionDoesNotRenderAsAnEmptyConversation()
    {
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(500, "{}"), null));

        var chat = Chat("session_bad_" + Guid.NewGuid().ToString("N"), api);
        _toClean.Add(chat.SessionId);

        Assert.False(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Empty(chat.History);

        ChatPanel.OpenFor(NewOrb(), chat);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        // Nothing in the transcript, which is expected — and the dot is the
        // thing that says why. Compared against the colour a *successfully*
        // empty conversation would wear rather than against a literal hex, so
        // this keeps meaning the same thing if the palette is ever retuned.
        var errored = ColourOf(panel.StateDot.Fill);

        var emptyButFine = new FakeChatSession(null)
        {
            SessionId = "cloud-empty-" + Guid.NewGuid(),
            State = RemoteChatState.Connected,
        };
        _toClean.Add(emptyButFine.SessionId);

        ChatPanel.OpenFor(NewOrb(), emptyButFine);
        FlushRender();

        var fine = ColourOf(ChatPanelTestAccess.Instance!.StateDot.Fill);

        Assert.NotEqual(fine, errored);
    }

    // --- where the header says this session lives ---

    // A cloud session names its machine, and the name is not this machine.
    //
    // ChatHeaderMeta.MachineFor reads silence as "on this one", which was a
    // sound rule while only the mirror implemented IRemoteChatMachine. A cloud
    // session is the first thing that is neither local nor on a paired machine,
    // and left silent the panel would have printed the user's own laptop's name
    // in the one line that exists to answer "where is this".
    [AvaloniaFact]
    public void ACloudSessionDoesNotClaimToBeOnThisMachine()
    {
        var api = new FakeApi(() => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""),
            Envelope(Row(UserTemplate, "u1", "hello"))));

        var chat = Chat("session_where_" + Guid.NewGuid().ToString("N"), api);

        var named = (IRemoteChatMachine)chat;

        Assert.False(string.IsNullOrWhiteSpace(named.MachineName));

        // This machine's name is a literal rather than MachineNames.Mine(),
        // which shells out to scutil — a real subprocess, which this suite must
        // never fire. MachineFor takes the name as an argument for exactly that
        // reason, and its own comment says so: the interesting case is a far
        // session reporting the *same* name as this machine, which cannot be
        // arranged on a real one at all.
        var (name, isLocal) = ChatHeaderMeta.MachineFor(named.MachineName, "some-laptop");

        Assert.False(isLocal);
        Assert.Equal(named.MachineName, name);
    }

    // The negative control, and the one that would have caught this: a session
    // saying nothing is still read as local, which is correct for every other
    // transport and is precisely why a cloud session has to speak up.
    [AvaloniaFact]
    public void ASilentSessionIsStillReadAsLocal()
    {
        var (name, isLocal) = ChatHeaderMeta.MachineFor(null, "some-laptop");

        Assert.True(isLocal);
        Assert.Equal("some-laptop", name);
    }

    // A missing credential is the other way in to the same state, and it is the
    // common one: the user declined the Keychain prompt. It must not read as an
    // empty conversation either.
    [AvaloniaFact]
    public async Task ADeclinedCredentialLeavesTheSessionInError()
    {
        var api = new FakeApi(() => throw new InvalidOperationException(
            "the endpoint must not be reached without a credential"));

        var creds = new FakeCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "denied"),
        };

        var chat = Chat("session_denied_" + Guid.NewGuid().ToString("N"), api, creds);
        _toClean.Add(chat.SessionId);

        Assert.False(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Empty(chat.History);
    }

    // --- a streamed reply (CB-199) ---

    // One connection, handed to the test as a channel to write the turn into.
    private sealed class PanelStream : ICloudEventStream
    {
        private readonly Channel<Channel<CloudStreamEvent>> _opened = Channel.CreateUnbounded<Channel<CloudStreamEvent>>();

        public async IAsyncEnumerable<CloudStreamEvent> OpenAsync(string accessToken, string sessionId,
            long? fromSequenceNum, [EnumeratorCancellation] CancellationToken ct)
        {
            var events = Channel.CreateUnbounded<CloudStreamEvent>();
            _opened.Writer.TryWrite(events);
            await foreach (var e in events.Reader.ReadAllAsync(ct)) yield return e;
        }

        internal async Task<ChannelWriter<CloudStreamEvent>> NextOpenAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return (await _opened.Reader.ReadAsync(timeout.Token)).Writer;
        }
    }

    // Answers the stream's starting-point read with a sequence number, and
    // everything else with an empty transcript.
    private sealed class NewestApi : ICloudApi
    {
        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token) =>
            Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
                context.Path.EndsWith("?limit=1&sort_order=desc", StringComparison.Ordinal)
                    ? "{\"data\":[{\"sequence_num\":\"7\"}]}"
                    : Envelope()));
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            FlushRender();
            await Task.Delay(5);
        }

        FlushRender();
        Assert.True(condition(), "the panel did not reach the expected state within five seconds");
    }

    private static string ShownText(ChatPanel panel) =>
        string.Concat(panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<TextBlock>()
            .Select(tb => !string.IsNullOrEmpty(tb.Text)
                ? tb.Text
                : string.Concat((tb.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
                    .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text))));

    // What the real-machine report asked for: the reply builds up in one row
    // while it is written, Stop is up the whole time, and both settle when the
    // turn ends. Driven through the real session and the real panel, with the
    // session posting to the real dispatcher.
    [AvaloniaFact]
    public async Task AStreamedReplyGrowsInOneRowWithStopShowing()
    {
        var id = "session_stream_" + Guid.NewGuid().ToString("N");
        _toClean.Add(id);
        var stream = new PanelStream();
        var chat = new ClaudeCloudChatSession(Session(id), new NewestApi(), new FakeCredentials(),
            action => Dispatcher.UIThread.Post(action))
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Stream = stream,
            Enabled = () => true,
        };

        ChatPanel.OpenFor(NewOrb(), chat);
        FlushRender();
        var panel = ChatPanelTestAccess.Instance!;
        var events = await stream.NextOpenAsync();

        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Durable, "client_event", 8, "{}",
            PayloadType: "system", Subtype: "init"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "stream_event", TextDelta: "Once upon ", InnerType: "content_block_delta"));
        await PumpUntil(() => RenderedRows(panel).Count == 1 && ShownText(panel).Contains("Once upon"));
        Assert.True(StopButton(panel).IsVisible);

        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "stream_event", TextDelta: "a time", InnerType: "content_block_delta"));
        await PumpUntil(() => ShownText(panel).Contains("Once upon a time"));
        Assert.Single(RenderedRows(panel));
        Assert.True(StopButton(panel).IsVisible);

        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Durable, "client_event", 9,
            "{\"payload\":" + Row(AssistantTemplate, "a1", "Once upon a time, the end.") + "}",
            PayloadType: "assistant"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Durable, "client_event", 10, "{}",
            PayloadType: "result", Subtype: "success"));
        await PumpUntil(() => !StopButton(panel).IsVisible && ShownText(panel).Contains("the end."));
        Assert.Single(RenderedRows(panel));

        ChatPanel.HideFor(id);
        FlushRender();
        await chat.StreamTask!;
    }

    // The bug this block is for: CB-199 made a live cloud session keep its
    // composer, and the only "Open in your browser" link lived in the read-only
    // box, so the link vanished for exactly the sessions people have open.
    [AvaloniaFact]
    public void ALiveCloudSessionOffersALinkToTheSessionAboveItsComposer()
    {
        var fake = SendableCloud("cloud-live-link");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        var link = SessionLink(panel);

        Assert.True(ComposerRow(panel).IsVisible);
        Assert.True(link.IsVisible);
        Assert.Equal("Open in your browser", link.Text);
        Assert.DoesNotContain("https://", link.Text!);

        // One link at a time: the read-only box's own stays away while this one
        // is up.
        Assert.False(ReadOnlyLink(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AReadOnlyCloudSessionShowsExactlyOneLinkAndItIsTheReadOnlyBoxs()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-one-link-" + Guid.NewGuid(),
            IsReadOnly = true,
            ComposerHint = "This session has ended.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(ReadOnlyLink(panel).IsVisible);
        Assert.False(SessionLink(panel).IsVisible);
    }

    // Negative controls for the two above: a link drawn unconditionally would
    // pass both of them.
    [AvaloniaFact]
    public void ASessionWithNoAddressShowsNoLinkWhetherOrNotItCanBeTypedInto()
    {
        var live = SendableCloud("cloud-live-nourl");
        live.ReplyUrl = null;

        ChatPanel.OpenFor(NewOrb(), live);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(ComposerRow(panel).IsVisible);
        Assert.False(SessionLink(panel).IsVisible);
        Assert.False(ReadOnlyLink(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AnOrdinarySessionShowsNoSessionLink()
    {
        var fake = new FakeChatSession(null)
        {
            SessionId = "cloud-ordinary-nolink-" + Guid.NewGuid(),
            IsReadOnly = false,
        };
        _toClean.Add(fake.SessionId);

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        Assert.False(SessionLink(ChatPanelTestAccess.Instance!).IsVisible);
    }

    // The flip hands the link from one place to the other and back, never two
    // and never none — the session ending must not leave a live-session link
    // over a box that has gone.
    [AvaloniaFact]
    public void TheLinkFollowsTheReadOnlyFlipWithExactlyOneVisibleAtATime()
    {
        var fake = SendableCloud("cloud-link-flip");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(SessionLink(panel).IsVisible);
        Assert.False(ReadOnlyLink(panel).IsVisible);

        fake.RaiseReadOnlyChanged(true);
        Flush();

        Assert.False(SessionLink(panel).IsVisible);
        Assert.True(ReadOnlyLink(panel).IsVisible);

        fake.RaiseReadOnlyChanged(false);
        Flush();

        Assert.True(SessionLink(panel).IsVisible);
        Assert.False(ReadOnlyLink(panel).IsVisible);
    }

    [AvaloniaFact]
    public void TheSessionLinkUnderlinesUnderThePointerAndClearsWhenItLeaves()
    {
        var fake = SendableCloud("cloud-link-hover");

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();

        var link = SessionLink(ChatPanelTestAccess.Instance!);

        Assert.Null(link.TextDecorations);

        Hover(link, InputElement.PointerEnteredEvent);
        Assert.Equal(TextDecorations.Underline, link.TextDecorations);

        Hover(link, InputElement.PointerExitedEvent);
        Assert.Null(link.TextDecorations);
    }
}
