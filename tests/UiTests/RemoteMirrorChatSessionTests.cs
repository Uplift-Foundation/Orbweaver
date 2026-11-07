using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// RemoteControlChatSession in **live view** — the mode where the panel shows the
// far session's own transcript rather than a reply a model wrote about it.
//
// Separate from RemoteControlChatSessionTests, which covers the messaging
// fallback, because the two modes are genuinely different behaviours sharing a
// class and mixing them would make each file's setup lie about the other's.
//
// The far Buddy here is the real RemoteMirrorServer reading real files in a temp
// directory, reached through the real RemoteMirrorClient and the real protocol.
// Only the relay is faked, and only because the real one is a live Claude Code
// session on somebody's account. Same seam as MirrorRoundTripTests, and for the
// same reason.
//
// [Collection("Settings")] for the same reason every other ChatPanel-touching
// suite carries it (see SettingsCollection.cs and ChatPanelStaticApiTests'
// header): this class opens real ChatPanels bound to a remote-mirror session,
// and ChatPanel.Panels is a process-wide static that OpenFor reuses rather
// than replacing. Missing this attribute let the class run in a separate
// parallel group from the rest of the ChatPanel* suites, which was one of two
// files responsible for a flaky
// ChatPanelPinTests.IsOpenForFindsAPinnedPanelToo failure that only showed up
// in the full suite, in Release, never standalone (CB-168).
[Collection("Settings")]
public class RemoteMirrorChatSessionTests : IDisposable
{
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";

    private readonly string _dir;
    private readonly List<(string Name, string Text)> _typed = new();

    // Swallow FETCH frames, so a panel upgrades to a live view and then waits
    // for a window that never comes. That is not a contrived state: on the wire
    // this runs over, a window costs a model turn per chunk, so the interval
    // between "mirroring" and "painted" is real and was measured in minutes.
    private bool _stallFetch;

    // CB-306: answer from a pool thread, as the real transport does. Every
    // other test here answers inline on the UI thread, which is the one
    // arrangement in which an off-thread UI touch cannot be seen.
    private bool _relayOffThread;

    // CB-306: an INPUT that is never answered, never reaches the relay, or a
    // FETCH answered with a bare OK carrying no window.
    private bool _stallInput;
    private bool _unreachableInput;
    private bool _bareOkForFetch;
    private readonly List<string> _toServer = new();

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;
    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();

    private bool _replyEnabled = true;
    private bool _canType = true;

    // CB-307 addendum: the far pane's screen for the typing gate. Null leaves
    // the gate unwired, which is every test written before it.
    private string? _farScreen;

    // CB-307 item 3: the keys the far side pressed to answer a form, and the
    // state its session reports (the roster's `waiting` is what makes a panel
    // ask for a form at all). Null state leaves the status as it always was.
    private readonly List<string> _pressed = new();
    private string? _farState;
    private bool _typeResult = true;
    private bool _mangle;
    private bool _mangleInput;

    // CB-105's messaging fallback. Defaults keep every test written before it
    // seeing exactly what it saw before: _canDeliver false means
    // `_seams.CanDeliver?.Invoke(status) == true` is false, which falls
    // straight to ErrNoPane the same as it always has.
    private bool _canDeliver;
    private DeliveryResult _deliverResult = DeliveryResult.Accepted;
    private string? _deliverAgentStatus;
    private bool _deliverThrows;
    private readonly List<(string Name, string Text)> _delivered = new();

    // CB-306. Wired only when a test sets _wireSlash before Wire(), so every
    // other test keeps the null seams a server built before the runner has.
    private bool _wireSlash;
    private IReadOnlyList<SlashCommand> _slashCatalogue = Array.Empty<SlashCommand>();
    private RemoteMirrorServer.SlashOutcome _slashOutcome = new(MirrorProtocol.ViaControl, null);
    private readonly List<string> _slashRan = new();

    // A far Buddy from before route kinds: its roster arrives without them.
    private bool _oldFarRoster;

    private readonly bool _remoteWasEnabled;

    public RemoteMirrorChatSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-mirror-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // A send checks this before doing anything, so with it off every test
        // below would assert against "remote sessions are switched off" instead
        // of the thing it is about. Put back in Dispose rather than left on: a
        // real setter that writes settings.json and leaks into every test after
        // it is exactly what bugfix/rc-tests-leak-remote-setting had to fix once
        // already.
        _remoteWasEnabled = OrbweaverSettings.RemoteControlEnabled;
        OrbweaverSettings.PeerLinkEnabled = true;
    }

    public void Dispose()
    {
        OrbweaverSettings.RemoteControlEnabled = _remoteWasEnabled;
        RemoteControlSessions.ResetForTests();

        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- the point of the whole thing -----------------------------------------

    // What the panel ends up holding is the far session's own conversation,
    // parsed by the same ChatTranscript a local panel uses — not a reply
    // composed about it.
    [AvaloniaFact]
    public async Task ALiveViewShowsTheFarSessionsOwnTranscript()
    {
        Wire("what did the build say?", "it passed on both runners");

        var session = await OpenAsync();

        Assert.True(session.IsMirroring);

        var said = Turns(session);

        Assert.Equal(2, said.Count);
        Assert.Equal(ChatRole.User, said[0].Role);
        Assert.Equal("what did the build say?", said[0].Text);
        Assert.Equal(ChatRole.Assistant, said[1].Role);
        Assert.Equal("it passed on both runners", said[1].Text);
    }

    // The banner replaces the "checking…" line rather than joining it, and says
    // plainly what the panel now is — including that typing goes into somebody
    // else's terminal, which is worth saying out loud.
    [AvaloniaFact]
    public async Task UpgradingReplacesTheOpeningLineWithOneThatSaysItIsLive()
    {
        Wire("hello", "hi");

        var replaced = 0;
        var session = await OpenAsync(s => ((IRemoteChatBacklog)s).HistoryReplaced += () => replaced++);

        Assert.Equal(1, replaced);

        var banner = session.History[0];

        Assert.Equal(ChatRole.System, banner.Role);
        Assert.Contains("Live view", banner.Text);
        Assert.Contains(Name, banner.Text);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Checking whether"));
    }

    // The box has to stop saying "message this session" once messages are being
    // typed into a terminal instead.
    [AvaloniaFact]
    public async Task TheComposerSaysTypingRatherThanMessaging()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains("terminal", session.ComposerHint);
        Assert.Contains(Name, session.ComposerHint);
    }

    // And it has to stop saying "terminal" when there is no terminal.
    //
    // **A live view is not a typable one, and keying the hint on mirroring alone
    // conflated them.** The roster has always carried HasPane — its own comment
    // says "a panel that offered a send it cannot deliver would be lying in the
    // one place it matters" — and the hint ignored it.
    //
    // Seen on a real pair: the composer read "Type into job-hunter-mac-mini's
    // terminal" with the line directly above it reading "Sent as a message
    // rather than typed … there is no input line to type into." Both on screen
    // at once, one of them false. On a headless machine, where sessions run in a
    // plain tty rather than under tmux, this is the ordinary case.
    [AvaloniaFact]
    public async Task TheComposerSaysMessagingWhenThereIsNoPaneToTypeInto()
    {
        _canType = false;

        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains("Message", session.ComposerHint);
        Assert.DoesNotContain("terminal", session.ComposerHint);
        Assert.Contains(Name, session.ComposerHint);
    }

    // CB-105's third arm: no pane, but this machine can hand the text to the
    // far session's own messaging socket instead — a live view that is real
    // and a composer that says something more useful than the plain
    // "Message…" a headless session got before this feature existed.
    [AvaloniaFact]
    public async Task TheComposerOffersDeliveryWhenThereIsNoPaneButThereIsASocket()
    {
        _canType = false;
        _canDeliver = true;

        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains(Name, session.ComposerHint);
        Assert.Contains("background job", session.ComposerHint);
        Assert.DoesNotContain("terminal", session.ComposerHint);
    }

    // A live view is a real transcript, so it can be paged back into — which is
    // exactly what the messaging channel could not do.
    [AvaloniaFact]
    public async Task ALiveViewCanBePagedBackInto()
    {
        // Comfortably more than one opening window — and since CB-46 that is no
        // longer the same as more than InitialBytes. The server grows the
        // opening window while the turns still fit one chunk, so a fixture sized
        // against the byte constant now arrives whole with nothing behind it.
        // Sized against the constraint that actually binds instead.
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < MirrorProtocol.InitialBytes * 8; i++)
        {
            var row = UserRow($"r{i}", $"message {i} " + new string('y', 300));
            rows.Add(row);
            bytes += row.Length + 1;
        }

        WireRows(rows);

        var session = await OpenAsync();
        var backlog = (IRemoteChatBacklog)session;

        Assert.True(backlog.HasMore);

        var prepended = 0;
        backlog.HistoryPrepended += n => prepended += n;

        Assert.True(await backlog.LoadOlderAsync(CancellationToken.None));
        Assert.True(prepended > 0);

        // The banner stays at the top: it describes the panel, not a moment in
        // the conversation, so older turns go in under it.
        Assert.Contains("Live view", session.History[0].Text);
    }

    // --- typing -----------------------------------------------------------------

    // The half of this bug that was about slash commands. The message reaches
    // the far session's own input line, which is the only place /color can
    // possibly work.
    [AvaloniaFact]
    public async Task AMessageIsTypedIntoTheFarTerminalRatherThanDescribedToAModel()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        var outcome = await session.SendAsync("/color green");

        var typed = Assert.Single(_typed);
        Assert.Equal(Name, typed.Name);
        Assert.Equal("/color green", typed.Text);

        // On screen once, after the transcript's own opening turn.
        Assert.Equal(
            new[] { "a", "/color green" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));

        // CB-35: a typed send with no error back from the far machine is the
        // one path that reports Sent — everything else in this file is a
        // reason the text never reached anywhere.
        Assert.Equal(ChatSendOutcome.Sent, outcome);
    }


    // --- CB-306: slash lines -------------------------------------------------------

    private static string NotesOf(RemoteControlChatSession session) =>
        string.Join("\n", session.History.Where(t => t.Role == ChatRole.System).Select(t => t.Text));

    [AvaloniaFact]
    public async Task ASlashLineInALiveViewIsTypedAndWaitsForItsEcho()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Equal("/color green", Assert.Single(_typed).Text);
        Assert.Equal(1, Turns(session).Count(t => t.Text == "/color green"));
    }

    // CB-306's product-readiness round: the far CLI's verdict crosses the
    // real link and the near panel says what it said. "done" is Done; no
    // answer in time, or no verdict at all (an older far Buddy), is
    // "no confirmation yet" — never a plain success.
    [AvaloniaTheory]
    [InlineData(MirrorProtocol.VerdictDone, true)]
    [InlineData(MirrorProtocol.VerdictUnconfirmed, false)]
    [InlineData(null, false)]
    public async Task AControlCommandRunsOnTheFarSideAndItsVerdictIsReported(string? verdict, bool done)
    {
        _wireSlash = true;
        _slashCatalogue = new[] { new SlashCommand("/color", "", SlashRouteKind.Control) };
        _slashOutcome = new(MirrorProtocol.ViaControl, null, verdict);
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Equal("/color green", Assert.Single(_slashRan));
        Assert.Empty(_typed);
        Assert.Contains(done
            ? "Done: /color green on " + Name + "."
            : "Sent /color green to " + Name + " over Remote Control — no confirmation yet.", NotesOf(session));
        Assert.DoesNotContain(done
            ? RemoteControlChatSession.SlashUnconfirmedNote("/color green", Name)
            : RemoteControlChatSession.SlashRanNote("/color green", Name), NotesOf(session));
    }

    [AvaloniaFact]
    public async Task ARefusedCommandSaysWhyAndKeepsTheBoxText()
    {
        _wireSlash = true;
        _slashCatalogue = new[] { new SlashCommand("/compact", "", SlashRouteKind.Unavailable) };
        _slashOutcome = new(MirrorProtocol.ViaRefused, "it needs the terminal");
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/compact now");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains(SlashRouting.RefusalNote("/compact", "it needs the terminal"), NotesOf(session));
        Assert.Empty(_typed);
    }

    [AvaloniaFact]
    public async Task ARefusalThatGaveNoReasonStillNamesTheCommand()
    {
        _wireSlash = true;
        _slashCatalogue = new[] { new SlashCommand("/compact", "", SlashRouteKind.Unavailable) };
        _slashOutcome = new(MirrorProtocol.ViaRefused, null);
        Wire("a", "b");
        var session = await OpenAsync();

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/compact"));
        Assert.Contains("Not sent: /compact", NotesOf(session));
    }

    // The known-name rule: a line is a command only if its first token is a
    // name the far session's CLI knows. Everything else is an ordinary message,
    // sent as it would be without a runner and never refused.
    [AvaloniaTheory]
    [InlineData("/tmp/build.log")]
    [InlineData("/")]
    [InlineData("/nonesuch run it")]
    public async Task ALineThatIsNotAKnownCommandGoesAsAnOrdinaryMessage(string line)
    {
        _wireSlash = true;
        _slashCatalogue = new[] { new SlashCommand("/color", "", SlashRouteKind.Control) };
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync(line);

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Empty(_slashRan);
        Assert.Equal(line, Assert.Single(_typed).Text);
    }

    [AvaloniaFact]
    public async Task AnUnknownSlashWordIsAMessageForAnOlderFarBuddyToo()
    {
        _oldFarRoster = true;
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/tmp/build.log");

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Equal("/tmp/build.log", Assert.Single(_typed).Text);
    }

    [AvaloniaFact]
    public async Task ACommandIsKnownWhateverItsCase()
    {
        _wireSlash = true;
        _slashCatalogue = new[] { new SlashCommand("/color", "", SlashRouteKind.Control) };
        Wire("a", "b");
        var session = await OpenAsync();

        await session.RunSlashAsync("/COLOR pink");

        Assert.Equal("/COLOR pink", Assert.Single(_slashRan));
    }

    [AvaloniaFact]
    public async Task ASlashLineNeedsNoPaneWhenTheFarBuddySpeaksSlash()
    {
        // A headless job reached over its bridge: a transcript to read but no
        // pane to type into.
        _wireSlash = true;
        _canType = false;
        _slashCatalogue = new[] { new SlashCommand("/color", "", SlashRouteKind.Control) };
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/color pink");

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Equal("/color pink", Assert.Single(_slashRan));
    }

    [AvaloniaFact]
    public async Task AFarMachineThatRefusesTheRequestOutrightIsReportedAsATypingRefusal()
    {
        _replyEnabled = false;
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains(RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrReplyOff, Name), NotesOf(session));
    }

    [AvaloniaFact]
    public async Task AnOlderFarBuddyInALiveViewKeepsTodaysTypedBehaviour()
    {
        _oldFarRoster = true;
        Wire("a", "b");
        var session = await OpenAsync();
        Assert.False(RemoteControlSessions.SpeaksSlash(RemoteControlSessions.MirrorFor(Account, Name)));

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Equal("/color green", Assert.Single(_typed).Text);
    }

    [AvaloniaFact]
    public async Task AnOlderFarBuddyWithNoPaneRefusesTheLineWithANote()
    {
        _oldFarRoster = true;
        _canType = false;
        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains(RemoteControlChatSession.OldFarBuddyNote(Name), NotesOf(session));
        Assert.Empty(_typed);
        Assert.Empty(ChatPanel.OfferedCommands(RemoteControlSessions.CommandsFor(Account, Name)));
    }

    // Nadia's case: a mirrored panel with no pane, where a known command that
    // cannot run must be refused with nothing sent, and a path must go as a
    // message.
    [AvaloniaFact]
    public async Task ARefusalNamesTheLineWhenItIsNotEvenSplittable()
    {
        Wire("a", "b");
        var session = await OpenAsync();
        var turn = new ChatTurn { Role = ChatRole.User, Text = "plain", IsComplete = true };

        var outcome = session.ApplySlashOutcome(turn, "plain",
            new RemoteMirrorClient.InputOutcome(null, MirrorProtocol.ViaRefused, null));

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains("Not sent: plain", NotesOf(session));
        Assert.Contains("can't run on that machine", NotesOf(session));
    }

    [AvaloniaFact]
    public async Task ASlashLineBeforeTheRosterHasArrivedIsLeftToTheOrdinarySend()
    {
        _wireSlash = true;
        Wire("a", "b");
        var session = NewSession();
        session.PanelOpened();
        // No discovery yet: a client exists and knows nothing about the session.

        var outcome = await session.RunSlashAsync("/color pink");

        Assert.Empty(_slashRan);
        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    [AvaloniaFact]
    public async Task AKnownButUnrunnableCommandIsRefusedAndAPathIsAMessage()
    {
        _wireSlash = true;
        _canType = false;
        _slashCatalogue = new[]
        {
            new SlashCommand("/color", "", SlashRouteKind.Control),
            new SlashCommand("/compact", "", SlashRouteKind.Unavailable),
        };
        _slashOutcome = new(MirrorProtocol.ViaRefused, SlashRouting.PeerMessageReason);
        Wire("a", "b");
        var session = await OpenAsync();

        Assert.Equal(ChatSendOutcome.Failed, await session.RunSlashAsync("/compact"));
        Assert.Equal("/compact", Assert.Single(_slashRan));
        Assert.Empty(_typed);
        Assert.Empty(_delivered);

        _slashRan.Clear();
        await session.RunSlashAsync("/tmp/x.log boom");
        Assert.Empty(_slashRan);
    }

    [AvaloniaFact]
    public async Task AnOlderFarBuddysKnownCommandIsRefusedWithoutAPaneAndNothingIsSent()
    {
        _oldFarRoster = true;
        _canType = false;
        Wire("a", "b");
        var session = await OpenAsync();

        // /compact is in its catalogue: known, so refused rather than sent on.
        var outcome = await session.RunSlashAsync("/compact");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains(RemoteControlChatSession.OldFarBuddyNote(Name), NotesOf(session));
        Assert.Empty(_typed);
        Assert.Empty(_delivered);
    }

    [AvaloniaFact]
    public async Task ASlashLineWithRemoteSessionsSwitchedOffIsRefusedButKept()
    {
        _wireSlash = true;
        // Known, so it reaches the runner rather than the ordinary send.
        _slashCatalogue = new[] { new SlashCommand("/color", "", SlashRouteKind.Control) };
        Wire("a", "b");
        var session = await OpenAsync();
        OrbweaverSettings.PeerLinkEnabled = false;

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains(RemoteControlChatSession.RemoteControlOffNote, NotesOf(session));
        Assert.Empty(_slashRan);
    }

    [AvaloniaFact]
    public async Task ASlashLineWithNoLinkClientIsLeftToTheOrdinarySend()
    {
        _wireSlash = true;
        Wire("a", "b");
        var session = await OpenAsync();
        RemoteControlSessions.UseMirrorClientForTests(Account, null);

        var outcome = await session.RunSlashAsync("/color green");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Contains(RemoteControlChatSession.NotConnectedNote, NotesOf(session));
        Assert.Empty(_slashRan);
    }

    [AvaloniaFact]
    public async Task ThePanelOffersOnlyWhatTheFarSideCanRun()
    {
        _wireSlash = true;
        _slashCatalogue = new[]
        {
            new SlashCommand("/color", "", SlashRouteKind.Control),
            new SlashCommand("/compact", "", SlashRouteKind.Unavailable),
        };
        Wire("a", "b");
        var session = await OpenAsync();

        // The session knows both; the panel offers the one that runs.
        Assert.Equal(new[] { "/color", "/compact" }, session.SlashCommands.Select(c => c.Name));
        Assert.Equal(new[] { "/color" }, ChatPanel.OfferedCommands(session.SlashCommands).Select(c => c.Name));
    }

    // The far transcript will produce the message back, because it went in
    // through the terminal — that is the whole design. So the row that returns
    // adopts the bubble already on screen instead of adding a second.
    [AvaloniaFact]
    public async Task TheEchoOfATypedMessageSettlesTheTurnRatherThanDuplicatingIt()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        await session.SendAsync("run the tests");

        // The far CLI reads it and writes it into its own transcript.
        Append(UserRow("echo", "run the tests"));
        await _server.TickAsync();

        // Once, not twice: the echo adopted the bubble that was already there.
        Assert.Equal(
            new[] { "a", "run the tests" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));

        Assert.Equal(1, updated);
    }

    // An unrelated message that happens to arrive after a send must not be
    // swallowed by the pending turn.
    [AvaloniaFact]
    public async Task ADifferentMessageArrivingAfterASendIsNotMistakenForTheEcho()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        await session.SendAsync("run the tests");

        Append(UserRow("other", "something else entirely"));
        await _server.TickAsync();

        Assert.Equal(
            new[] { "a", "run the tests", "something else entirely" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));
    }

    [AvaloniaFact]
    public async Task TheFarMachinesOwnReplySettingIsWhatDecidesWhetherAnythingIsTyped()
    {
        Wire("a", "b");
        _replyEnabled = false;

        var session = await OpenAsync();
        var outcome = await session.SendAsync("hello");

        Assert.Empty(_typed);

        var last = session.History[^1];
        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("switched off", last.Text);
        Assert.Contains("over there", last.Text);

        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    // CB-307 addendum: the far pane is on /permissions. Nothing is typed, the
    // send fails, and the note under it names the far screen and says what to
    // do — the sentence a person actually reads.
    [AvaloniaFact]
    public async Task ASendToAFarPaneOnADialogIsRefusedWithANoteThatSaysWhy()
    {
        _farScreen = PaneFixtures.PermissionsAllow;
        Wire("a", "b");

        var session = await OpenAsync();
        var outcome = await session.SendAsync("please carry on");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Empty(_typed);

        var last = session.History[^1];
        Assert.Equal(ChatRole.System, last.Role);
        Assert.StartsWith($"{Name}'s terminal isn't at its prompt — ", last.Text);
        Assert.EndsWith("answer that screen in the terminal and send again.", last.Text);
    }

    // ...and the control: the same wiring on a real prompt types.
    [AvaloniaFact]
    public async Task ASendToAFarPaneAtItsPromptIsTyped()
    {
        _farScreen = PaneFixtures.MacPromptAfterEsc;
        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Equal(ChatSendOutcome.Sent, await session.SendAsync("please carry on"));
        Assert.Single(_typed);
    }

    // --- CB-307 item 3: a question form on the far screen ---------------------

    // The whole round trip over the harness's real server and client: the far
    // roster says waiting, the panel asks for the form, shows it, and a click
    // presses exactly that key on the far side — nothing was typed.
    [AvaloniaFact]
    public async Task AFormOnTheFarScreenIsShownAndAnAnswerPressesItsKey()
    {
        _farScreen = PaneFixtures.MiniAskForm;
        _farState = "waiting";
        Wire("a", "b");

        var session = await OpenAsync();
        await PumpUntilAsync(() => session.Prompt is not null);

        // The screen cannot say which tab is showing, so no number is claimed.
        Assert.Null(session.Prompt!.Progress);
        Assert.StartsWith("xxxxx xx xxx xxxxx", session.Prompt.Title);

        // The link carries text now, so "Type something." is offered as the box.
        Assert.Equal("5", Assert.Single(session.Prompt.Options, o => o.FreeText).Key);

        await session.AnswerAsync(session.Prompt.Options[1]);

        Assert.Equal(new[] { "2" }, _pressed);
        Assert.Empty(_typed);
    }

    // A multiSelect over the real link: ticking is a digit, and the explicit
    // Submit button is Right on the far side — never a digit, never typed.
    [AvaloniaFact]
    public async Task AMultiSelectTicksWithADigitAndCommitsWithRight()
    {
        _farScreen = AskFormFixtures.MultiSelectQ2;
        _farState = "waiting";
        Wire("a", "b");

        var session = await OpenAsync();
        await PumpUntilAsync(() => session.Prompt is not null);

        Assert.Equal(new[] { "\u2610 Cheese", "\u2610 Ham", "\u2610 Olives" },
            session.Prompt!.Options.Take(3).Select(o => o.Label));
        Assert.Equal(AskFormView.CommitKey, session.Prompt.Options[^1].Key);

        await session.AnswerAsync(session.Prompt.Options[1]);
        Assert.Equal(new[] { "2" }, _pressed);

        // The same form again, as the next poll reads it; commit it.
        await PumpUntilAsync(() => session.Prompt is not null);
        await session.AnswerAsync(session.Prompt!.Options[^1]);

        Assert.Equal(new[] { "2", "Right" }, _pressed);
        Assert.Empty(_typed);
    }

    // The Submit tab over the real link: "Submit answers" presses its own key.
    [AvaloniaFact]
    public async Task TheReviewScreenOffersSubmitAnswersAndCancelAndPressesOnlyTheChosenOne()
    {
        _farScreen = AskFormFixtures.SubmitTab;
        _farState = "waiting";
        Wire("a", "b");

        var session = await OpenAsync();
        await PumpUntilAsync(() => session.Prompt is not null);

        Assert.True(session.Prompt!.ArmedActivation);
        Assert.Equal(new[] { "Submit answers", "Cancel" }, session.Prompt.Options.Select(o => o.Label));

        await session.AnswerAsync(session.Prompt.Options[0]);

        Assert.Equal(new[] { "1" }, _pressed);
    }

    // The screen moves on between the form being shown and the click: the far
    // side presses nothing, and the panel is shown what is there now with a note
    // saying so, which the user can answer.
    [AvaloniaFact]
    public async Task AnAnswerToAFormThatMovedOnPressesNothingAndShowsTheNewOne()
    {
        _farScreen = PaneFixtures.MiniAskForm;
        _farState = "waiting";
        Wire("a", "b");

        var session = await OpenAsync();
        await PumpUntilAsync(() => session.Prompt is not null);
        var shown = session.Prompt!;

        _farScreen = PaneFixtures.MacModelSwitchDialog;
        await session.AnswerAsync(shown.Options[1]);

        Assert.Empty(_pressed);
        Assert.NotEqual(shown.Title, session.Prompt?.Title);
        Assert.StartsWith("Opus 5.5's safeguards flagged this session.", session.Prompt!.Title);
        Assert.Contains(session.History, t => t.Text.Contains("had changed, so nothing was answered", StringComparison.Ordinal));
    }

    // The control: a far session that is not waiting is never asked, so no form
    // is fetched and nothing can be pressed.
    [AvaloniaFact]
    public async Task AFarSessionThatIsNotWaitingIsNeverAskedForAForm()
    {
        _farScreen = PaneFixtures.MiniAskForm;
        Wire("a", "b");

        var session = await OpenAsync();
        await PumpUntilAsync(() => false, attempts: 30);

        Assert.Null(session.Prompt);
        Assert.Empty(_pressed);
        Assert.DoesNotContain(MirrorProtocol.Prompt, _toServer);
    }

    private static async Task PumpUntilAsync(Func<bool> done, int attempts = 200)
    {
        for (var i = 0; i < attempts && !done(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    // --- CB-52: the integrity wording ------------------------------------------

    // A failed transfer says what it always said, byte for byte around the
    // reason. There used to be a second wording, for a relay stuck on a prompt;
    // the table it read was never filled once the relay went (937de9ec), so
    // this was already the only thing production could show, and CB-238 only
    // deleted the arm nobody could reach. Pinned whole so that stays true.
    [AvaloniaFact]
    public async Task AMirrorThatFailedForSomeOtherReasonStillSaysWhatItAlwaysDid()
    {
        Wire("a", "b");
        _mangle = true;

        var session = await OpenAsync(expectMirror: false);

        var last = session.History[^1];

        Assert.StartsWith($"Couldn't verify {Name}'s transcript — ", last.Text);
        Assert.EndsWith(
            ". Showing nothing rather than something altered; close and reopen the panel to try again.",
            last.Text);
        Assert.DoesNotContain("press Escape", last.Text);
    }

    // --- CB-46: upgraded, but nothing painted yet ------------------------------

    // The state the user was actually left in, and the reason his panel showed
    // nothing at all. The mirror had been agreed — the composer had already
    // switched to "type into its terminal" — but the first window had not
    // arrived, so the deltas had not started either, and OnInbound was throwing
    // his messages away on the strength of _mirroring alone. Three sources,
    // all silent, and a panel strictly worse than the messaging channel it
    // replaced.
    //
    // He sent "test"; the far session replied "Received — connectivity
    // confirmed."; it reached this machine and was discarded here.
    private async Task<RemoteControlChatSession> StalledMirrorAsync()
    {
        Wire("a", "b");
        _stallFetch = true;
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(50);

        var session = NewSession();
        session.PanelOpened();

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.True(session.IsMirroring, "the roster arrived, so the panel should have upgraded");
        Assert.DoesNotContain(Turns(session), t => t.Text == "a");

        return session;
    }

    // What the panel says while the transfer is running. It used to sit on the
    // opening "Checking whether a live view … is available" line for the whole
    // wait, which is the exact sentence that meant failure an hour earlier — the
    // user reported a working transfer as "no live view" twice on the strength
    // of it.
    [AvaloniaFact]
    public async Task ThePanelSaysItIsFetchingRatherThanStillChecking()
    {
        var session = await StalledMirrorAsync();

        Assert.Contains(session.History, t => t.Text.Contains("fetching its conversation"));
    }

    // And it does not outlive the wait: the window that ends it clears the
    // history outright, so the line goes with it rather than sitting above a
    // conversation it no longer describes.
    [AvaloniaFact]
    public async Task TheFetchingLineIsGoneOnceTheConversationArrives()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        Assert.DoesNotContain(session.History, t => t.Text.Contains("fetching its conversation"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Checking whether"));
    }

    // --- CB-43, and what became of it -------------------------------------------

    // **The capability these tested is gone, and it went deliberately with the
    // relay.** CB-43 fixed a real bug: a session running in a plain tty rather
    // than under tmux has a transcript to mirror but no input line to type
    // into, and the panel used to refuse — which made upgrading to a live view
    // strictly worse than staying on the messaging channel it had been using
    // happily. The fix was to fall back to that channel, which handed the text
    // to the far session's model as a cross-session message.
    //
    // That channel was the relay, and the relay is deleted. A direct link types
    // into the session's own input line and has nothing else to offer, so a
    // session with no pane can now be read and not written to.
    //
    // Rewritten rather than removed, because a capability that disappears
    // quietly is how a regression gets shipped. What follows asserts the new
    // behaviour *and* that the user is told plainly, which is the part that
    // keeps this from being the bug CB-43 was about.
    [AvaloniaFact]
    public async Task ASessionWithNoPaneIsRefusedAndToldWhy()
    {
        _canType = false;

        Wire("a", "b");
        var session = await OpenAsync();
        var before = session.History.Count;

        var outcome = await session.SendAsync("are you there?");

        var added = session.History.Skip(before).ToList();

        // The typed message stays on screen with the explanation under it. A
        // message that vanishes silently is the failure this panel has spent
        // six tickets learning not to produce.
        Assert.Contains(added, t => t.Role == ChatRole.User && t.Text == "are you there?");
        // Says "a terminal Buddy can type into" rather than naming tmux: since
        // CB-79 that is five different mechanisms, and the far machine's own
        // reason never crosses the wire — a code is all that does.
        Assert.Contains(added, t => t.Role == ChatRole.System
            && t.Text.Contains(TerminalTyping.CantTypePhrase, StringComparison.Ordinal));

        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    [AvaloniaFact]
    public async Task TheRefusalNamesTheSessionRatherThanBlamingTheNetwork()
    {
        // The fix is on the other machine, and this is the only place that will
        // ever say so.
        _canType = false;

        Wire("a", "b");
        var session = await OpenAsync();
        await session.SendAsync("hello?");

        Assert.Contains(
            session.History,
            t => t.Role == ChatRole.System && t.Text.Contains(Name));
    }

    // --- CB-105: delivering to a session with no pane at all -------------------

    // The mirrored transcript is what actually settles the pending turn — same
    // as a typed send — so this only asserts the note that appears while it is
    // still waiting for that echo, plus the fact that a delivery was actually
    // attempted rather than typed.
    [AvaloniaFact]
    public async Task SendingWithNoPaneButASocketHandsItOverAndSaysSo()
    {
        _canType = false;
        _canDeliver = true;
        _deliverAgentStatus = "working";

        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.SendAsync("still there?");

        Assert.Equal("still there?", Assert.Single(_delivered).Text);
        Assert.Empty(_typed);

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("Handed to") && t.Text.Contains("mid-turn"));

        // The typed message stays on screen, still pending — nothing here
        // settles it early. Once the far transcript's own next turn produces
        // the row, Echoes settles it exactly as it already does for a typed
        // send.
        Assert.Contains(session.History, t => t.Role == ChatRole.User && t.Text == "still there?");

        // CB-35: a hand-off with no wire error is Sent, the same as a typed
        // send — the socket took the bytes, which is as much as either path
        // ever knows at send time.
        Assert.Equal(ChatSendOutcome.Sent, outcome);
    }

    // The mirrored transcript settling the pending turn once the far session's
    // own next turn writes the row — the same mechanism a typed send already
    // relies on, exercised here for the messaging path.
    //
    // **This has to use the real wrapped row, not the bare text, or it proves
    // nothing.** A message delivered over CB-105's socket lands in the far
    // session's own transcript exactly the way one delivered over the old
    // relay fallback did — as a "user" row whose content is the whole
    // `<cross-session-message …>` tag SessionMessageFrame.Wrap builds, not
    // the plain text — so Echoes has to go through
    // BridgeProtocol.ParseInboundMessages to find it, not match on equality.
    // The first version of this test appended a bare UserRow("still there?")
    // and passed on the trivial exact-match branch of Echoes without ever
    // reaching the tag-parsing arm — precisely the mistake commit 904aa87
    // fixed for the relay-fallback case this one mirrors, and precisely why
    // both UserRowEncoded (properly JSON-escaped; the tag contains quotes)
    // and the assertion that the settled text still carries the tag are
    // here rather than a plain string match.
    [AvaloniaFact]
    public async Task ADeliveredMessageIsSettledByTheEchoOfItsOwnWrappedRow()
    {
        _canType = false;
        _canDeliver = true;

        Wire("a", "b");
        var session = await OpenAsync();

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        await session.SendAsync("still there?");

        Append(UserRowEncoded("echo", SessionMessageFrame.Wrap(
            SessionMessenger.FromName("the-other-machine"), "still there?")));

        await _server.TickAsync();

        Assert.Equal(1, updated);

        // Once, not twice: the wrapped echo adopted the bubble already on
        // screen rather than the typed turn being left pending forever, or a
        // second bubble appearing beside it.
        var settled = Assert.Single(Turns(session), t => t.Role == ChatRole.User && t.Text.Contains("still there?"));

        // And it genuinely arrived rather than being skipped — the settled
        // bubble carries the transcript's own wording, the wrapped tag and
        // all, which cannot be true unless the row was actually parsed
        // rather than matched by coincidence.
        Assert.Contains("cross-session-message", settled.Text);
    }

    [AvaloniaFact]
    public async Task ASessionTheRegistryNoLongerKnowsAboutSaysTheJobMayHaveStopped()
    {
        _canType = false;
        _canDeliver = true;
        _deliverResult = DeliveryResult.NoRegistryEntry;

        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.SendAsync("hello?");

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("the job may have stopped"));

        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    [AvaloniaFact]
    public async Task ASocketThatRefusesTheDeliverySaysNothingWasSent()
    {
        _canType = false;
        _canDeliver = true;
        _deliverResult = DeliveryResult.SocketRefused;

        Wire("a", "b");
        var session = await OpenAsync();

        var outcome = await session.SendAsync("hello?");

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("nothing was sent"));

        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    // --- keeping up ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task WhatTheFarSessionDoesNextArrivesWithoutBeingAskedFor()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = Turns(session).Count;

        Append(AssistantRow("new", "and then it deployed"));
        await _server.TickAsync();

        var said = Turns(session);

        Assert.Equal(before + 1, said.Count);
        Assert.Equal("and then it deployed", said[^1].Text);
    }

    // --- refusing what did not survive ------------------------------------------

    // The guarantee, at the panel. A mangled transfer produces an error and an
    // empty conversation — never a partial window, and never a quiet fallback to
    // the model-written version, which would substitute a summary at the exact
    // moment integrity failed.
    [AvaloniaFact]
    public async Task AMirrorThatFailsItsIntegrityCheckShowsNothingRatherThanSomethingElse()
    {
        Wire("what did the build say?", "it passed on both runners");
        _mangle = true;

        var session = await OpenAsync(expectMirror: false);

        var last = session.History[^1];

        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("Couldn't verify", last.Text);
        Assert.Contains("rather than something altered", last.Text);

        // Nothing from the far transcript reached the screen.
        Assert.DoesNotContain(session.History, t => t.Text.Contains("it passed on both runners"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("what did the build say?"));
    }

    // --- no live view --------------------------------------------------------------

    // A peer the far Buddy cannot show says so once, in so many words. It used
    // to promise a messaging channel whose replies "may summarise"; that
    // channel was the relay, gone since 937de9ec (CB-238).
    [AvaloniaFact]
    public async Task WithoutABuddyOverThereThePanelSaysWhyItIsNotALiveView()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        await _client.DiscoverAsync(
            Array.Empty<string>(), new[] { Name });

        Assert.False(session.IsMirroring);

        var last = session.History[^1];

        Assert.Equal(RemoteControlChatSession.NoLiveViewNote(Name), last.Text);
        Assert.Contains("Message", session.ComposerHint);
    }

    // The sequence a user actually hit, and the one that must be asserted rather
    // than reasoned about: the panel is OPEN and already showing "no live view",
    // and the roster arrives afterwards.
    //
    // The claim being pinned is that the stale line does not survive. It is a
    // statement about the *other machine* — "isn't running Remote Control" —
    // so a reader has no way to tell it has gone out of date, and leaving it
    // above a working live view would be its own bug. What removes it is the
    // Window delivery that follows the upgrade, which rebuilds the history from
    // the far transcript; this test exists so that stays true rather than being
    // an inference about code that could change.
    [AvaloniaFact]
    public async Task ARosterArrivingAfterThePanelGaveUpReplacesTheNoLiveViewLine()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        // No Buddy over there yet: the name settles as unavailable and says so.
        await _client.DiscoverAsync(
            Array.Empty<string>(), new[] { Name });

        Assert.False(session.IsMirroring);
        Assert.Contains(session.History, t => t.Text.Contains("No live view"));

        // Now the far Buddy shows up and can answer for the session. Wired by
        // hand rather than through WireRows, which would build a second client
        // and throw away the state this test is about.
        _path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(_path, UserRow("u1", "what did the build say?") + "\n"
                               + AssistantRow("a1", "it passed on both runners") + "\n");

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242
        }));

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.True(session.IsMirroring, "the panel should upgrade when the roster finally arrives");

        // The stale line is gone, not merely outvoted by a newer one.
        Assert.DoesNotContain(session.History, t => t.Text.Contains("No live view"));
        Assert.Contains(session.History, t => t.Text.Contains("Live view"));

        // And the far session's real conversation is what is on screen.
        Assert.Equal(
            new[] { "what did the build say?", "it passed on both runners" },
            Turns(session).Select(t => t.Text));
    }

    [AvaloniaFact]
    public async Task ThePanelOnlySaysItOnce()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        // Nobody to ask, twice. Over the relay this was a peer list with the
        // session but no Buddy on it; a link says the same thing by being
        // connected to no machines.
        var peers = Array.Empty<string>();

        await _client.DiscoverAsync(peers, new[] { Name });
        await _client.DiscoverAsync(peers, new[] { Name });

        Assert.Single(session.History.Where(t => t.Text.Contains("No live view")));
    }

    // --- the rest of what a panel can be told --------------------------------------

    // Every arm of the error switch, because each is a different thing to do
    // about it and a generic "couldn't send" would tell nobody which.
    [AvaloniaFact]
    public async Task ASessionTheFarBuddyHasForgottenSaysSo()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        _agents.Clear();
        _sessions.Clear();

        var outcome = await session.SendAsync("hello");

        Assert.Contains("no longer has a session", session.History[^1].Text);
        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    [AvaloniaFact]
    public async Task AMessageThatDidNotSurviveTheTripIsNotTypedAndSaysSo()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        // A courier that alters the message on its way to the terminal. Refused
        // rather than typed in a form the person did not write.
        _mangleInput = true;

        var outcome = await session.SendAsync("hello");

        Assert.Empty(_typed);
        Assert.Contains("didn't survive the trip", session.History[^1].Text);
        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    // Cancel is a no-op in both modes and must stay a quiet one: a Cancel that
    // looked like it worked and did nothing would be worse than none.
    [AvaloniaFact]
    public async Task CancellingDoesNothingAndSaysNothing()
    {
        Wire("a", "b");
        var session = await OpenAsync();
        var before = session.History.Count;

        session.Cancel();

        Assert.Equal(before, session.History.Count);
    }

    // Closing keeps the conversation and drops only the subscription, so a panel
    // reopened later still has what it had.
    [AvaloniaFact]
    public async Task ClosingThePanelKeepsTheConversationAndDropsTheSubscription()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        var before = session.History.Count;
        session.PanelClosed();

        Assert.Equal(before, session.History.Count);
        Assert.False(_server.Busy);

        session.PanelOpened();
        Assert.True(_server.Busy);
    }

    [AvaloniaFact]
    public void ClosingAPanelThatNeverBecameALiveViewIsHarmless()
    {
        var session = NewSession();
        var before = session.History.Count;

        session.PanelClosed();

        Assert.Equal(before, session.History.Count);
    }

    // Disposing is not part of the app's normal path — these sessions outlive
    // every panel that shows them — but it exists, and it must not throw or take
    // the conversation with it.
    [AvaloniaFact]
    public async Task DisposingUnsubscribesWithoutLosingTheConversation()
    {
        Wire("a", "b");
        var session = await OpenAsync();
        var before = session.History.Count;

        session.Dispose();
        session.Dispose();

        Assert.Equal(before, session.History.Count);
    }

    // Raised once per change, not per set, so a poll that re-asserts the same
    // state does not redraw anything.
    [AvaloniaFact]
    public void StateOnlyChangesWhenItChanges()
    {
        var session = NewSession();
        var changes = 0;
        session.StateChanged += _ => changes++;

        session.SetState(RemoteChatState.Connected);
        Assert.Equal(0, changes);

        session.SetState(RemoteChatState.Error);
        session.SetState(RemoteChatState.Error);

        Assert.Equal(1, changes);
        Assert.Equal(RemoteChatState.Error, session.State);
    }

    // A page that parsed to nothing but moved the offset is not the end — the
    // window can be entirely tool results — so paging must not stop at the first
    // quiet stretch.
    [AvaloniaFact]
    public async Task PagingPastAQuietStretchKeepsGoing()
    {
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < MirrorProtocol.InitialBytes + 200_000; i++)
        {
            var row = i % 8 == 0 ? UserRow($"u{i}", $"said {i}") : Snapshot(i);

            rows.Add(row);
            bytes += row.Length + 1;
        }

        WireRows(rows);

        var session = await OpenAsync();
        var backlog = (IRemoteChatBacklog)session;

        var pages = 0;
        while (backlog.HasMore && pages < 20)
        {
            await backlog.LoadOlderAsync(CancellationToken.None);
            pages++;
        }

        Assert.False(backlog.HasMore);
    }

    private static string Snapshot(int i) =>
        "{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
        + new string('x', 400) + "\"}";

    // --- CB-306: the fetch wait must not touch the UI off its thread ------------------

    private static async Task<bool> PumpUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        return done();
    }

    private bool WatchSent()
    {
        lock (_toServer) return _toServer.Contains(MirrorProtocol.Watch);
    }

    // The crash.log 2026-11-02 stack: Delivered arrives on a pool thread, the
    // session ended the wait inline, WaitChanged reached ChatPanel.HideWait and
    // FetchWaitBox.IsVisible threw. The throw escaped OpenOnceAsync, so the
    // window was never painted and the watch never renewed: "fetching" forever.
    [AvaloniaFact]
    public async Task ADeliveryFromAPoolThreadPaintsAndHidesTheWaitAndWatches()
    {
        _relayOffThread = true;
        Wire("what did the build say?", "it passed");

        var session = NewSession();
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        var box = ChatPanelTestAccess.Instance!.FindControl<Border>("FetchWaitBox")!;

        await _client.DiscoverAsync(Peers, new[] { Name });

        var painted = await PumpUntilAsync(() => session.History.Any(t => t.Text.Contains("Live view")));

        Assert.True(painted, "the window should have been painted");
        Assert.Contains(Turns(session), t => t.Text == "it passed");
        Assert.False(box.IsVisible, "the fetching indicator should be gone");
        Assert.Null(session.WaitingSince);
        Assert.True(await PumpUntilAsync(WatchSent), "the watch should have been sent after the window");
    }

    // Same shape, ending the other way: a transfer that fails its integrity
    // check, reported from a pool thread.
    [AvaloniaFact]
    public async Task AFailureFromAPoolThreadSaysSoAndHidesTheWait()
    {
        _relayOffThread = true;
        _mangle = true;
        Wire("a", "b");

        var session = NewSession();
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        var box = ChatPanelTestAccess.Instance!.FindControl<Border>("FetchWaitBox")!;

        await _client.DiscoverAsync(Peers, new[] { Name });

        var said = await PumpUntilAsync(() => session.History.Any(t => t.Text.Contains("Couldn't verify")));

        Assert.True(said, "the failure should have been put in the panel");
        Assert.False(box.IsVisible);
        Assert.Null(session.WaitingSince);
    }

    // An open that throws used to vanish: the task was dropped on the floor.
    [AvaloniaFact]
    public async Task AnOpenThatThrowsIsLogged()
    {
        Wire("a", "b");
        _client.Delivered += _ => throw new InvalidOperationException("boom");

        var logged = new List<(string What, string Detail)>();
        var session = NewSession();
        session.Log = (what, detail) => { lock (logged) logged.Add((what, detail)); };

        session.PanelOpened();
        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.True(await PumpUntilAsync(() => { lock (logged) return logged.Count > 0; }));

        lock (logged)
        {
            var line = Assert.Single(logged);
            Assert.Equal("open-threw", line.What);
            Assert.Contains($"n={Name}", line.Detail);
            Assert.Contains("InvalidOperationException", line.Detail);
            Assert.Contains("boom", line.Detail);
        }
    }

    // The send failures this logging exists for: no answer came back at all, so
    // there is no error code from the far side, and the line says unsupported
    // rather than nothing.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASendThatGetsNoAnswerIsLoggedWithAFallbackCode(bool stall)
    {
        Wire("a", "b");
        Capture();
        await OpenAsync();
        _stallInput = stall;
        _unreachableInput = !stall;
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(50);

        var outcome = await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Equal(MirrorProtocol.ErrUnsupported, outcome.Error);
        Assert.Contains($"input n={Name} -> err code={MirrorProtocol.ErrUnsupported}", _clientLog);
        AssertNoSecretLogged();
    }

    // An OK where a window should be: no payload and no reason either.
    [AvaloniaFact]
    public async Task AnOkWithNoWindowFailsTheOpenWithTheGenericReason()
    {
        _bareOkForFetch = true;
        Wire("a", "b");
        Capture();
        var failures = new List<string>();
        _client.Failed += (_, why) => failures.Add(why);

        await _client.DiscoverAsync(Peers, new[] { Name });
        Assert.False(await _client.OpenAsync(Name));

        Assert.Equal("the other machine didn't answer", Assert.Single(failures));
        Assert.Contains($"open n={Name} failed why=the other machine didn't answer", _clientLog);
    }

    // Nobody listening is an ordinary state (a client outlives its panels), and
    // must neither throw nor stop the line being written.
    [AvaloniaFact]
    public async Task AFailureWithNobodyListeningIsLoggedAndDoesNotThrow()
    {
        _bareOkForFetch = true;
        Wire("a", "b");
        Capture();

        await _client.DiscoverAsync(Peers, new[] { Name });
        Assert.False(await _client.OpenAsync(Name));

        Assert.Contains($"open n={Name} failed why=the other machine didn't answer", _clientLog);
    }

    [AvaloniaFact]
    public async Task ADeliveryWithNobodyListeningIsLoggedAndDoesNotThrow()
    {
        Wire("a", "b");
        Capture();

        await _client.DiscoverAsync(Peers, new[] { Name });
        Assert.True(await _client.OpenAsync(Name));

        Assert.Contains($"delivered n={Name} mode=window turns=2", _clientLog);
        Assert.Contains($"open n={Name} ok", _clientLog);
    }

    // The reopen path is fire-and-forget too: a panel closed and shown again
    // re-fetches, and a fault there used to vanish exactly as the first did.
    [AvaloniaFact]
    public async Task AReopenThatThrowsIsLoggedToo()
    {
        Wire("a", "b");
        _client.Delivered += _ => throw new InvalidOperationException("boom again");

        var logged = new List<string>();
        var session = NewSession();
        session.Log = (what, detail) => { lock (logged) logged.Add(what); };

        session.PanelOpened();
        await _client.DiscoverAsync(Peers, new[] { Name });
        Assert.True(await PumpUntilAsync(() => { lock (logged) return logged.Count == 1; }));

        // Close, then show again: the feed is gone, so this is a fresh open.
        session.PanelClosed();
        await Task.Delay(50);
        session.PanelOpened();

        Assert.True(await PumpUntilAsync(() => { lock (logged) return logged.Count == 2; }));
        lock (logged) Assert.All(logged, w => Assert.Equal("open-threw", w));
    }

    // The default sink is the real log, which is a no-op without a path.
    [AvaloniaFact]
    public void TheDefaultLogSinkIsCallable() => NewSession().Log("open-threw", "n=x");

    // --- CB-306: chat outcomes are logged on both sides, by name and never by text ----

    private const string Secret = "my password is hunter2";

    private readonly List<string> _serverLog = new();
    private readonly List<string> _clientLog = new();

    private void Capture()
    {
        _server.InputLog = (what, detail) => { lock (_serverLog) _serverLog.Add($"{what} {detail}"); };
        _client.Log = (what, detail) => { lock (_clientLog) _clientLog.Add($"{what} {detail}"); };
    }

    private void AssertNoSecretLogged()
    {
        lock (_serverLog) Assert.DoesNotContain(_serverLog, l => l.Contains("hunter2"));
        lock (_clientLog) Assert.DoesNotContain(_clientLog, l => l.Contains("hunter2"));
    }

    [AvaloniaFact]
    public async Task AnOpenAndATypedSendAreLoggedOnBothSides()
    {
        Wire("a", "b");
        Capture();
        var session = await OpenAsync();

        Assert.Null((await _client.SendInputDetailedAsync(Name, Secret)).Error);

        Assert.Contains($"open n={Name} ok", _clientLog);
        Assert.Contains($"delivered n={Name} mode=window turns=2", _clientLog);
        Assert.Contains($"input n={Name} -> ok via=typed", _clientLog);
        Assert.Contains($"input n={Name} -> ok via=typed", _serverLog);
        AssertNoSecretLogged();
        Assert.NotNull(session);
    }

    [AvaloniaFact]
    public async Task ADeltaIsLoggedWithItsMode()
    {
        Wire("a", "b");
        Capture();
        await OpenAsync();

        Append(AssistantRow("a2", "and then"));
        await _server.TickAsync();

        Assert.Contains($"delivered n={Name} mode=delta turns=1", _clientLog);
    }

    [AvaloniaFact]
    public async Task ARefusedTerminalIsLoggedAsATypeFailure()
    {
        _typeResult = false;
        Wire("a", "b");
        Capture();
        await OpenAsync();

        var outcome = await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Equal(MirrorProtocol.ErrTypeFailed, outcome.Error);
        Assert.Contains($"input n={Name} -> err code={MirrorProtocol.ErrTypeFailed}", _serverLog);
        Assert.Contains($"input n={Name} -> err code={MirrorProtocol.ErrTypeFailed}", _clientLog);
        AssertNoSecretLogged();
    }

    [AvaloniaTheory]
    [InlineData("Accepted", false, "ok via=msg")]
    [InlineData("NoRegistryEntry", false, "err code=not-registered")]
    [InlineData("SocketRefused", false, "err code=deliver-failed")]
    [InlineData("Accepted", true, "err code=deliver-failed")]
    public async Task AMessagingDeliveryIsLoggedByHowItEnded(string result, bool throws, string expected)
    {
        _canType = false;
        _canDeliver = true;
        _deliverResult = Enum.Parse<DeliveryResult>(result);
        _deliverThrows = throws;
        Wire("a", "b");
        Capture();
        await OpenAsync();

        await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Contains($"input n={Name} -> {expected}", _serverLog);
        Assert.Contains($"input n={Name} -> {expected}", _clientLog);
        AssertNoSecretLogged();
    }

    [AvaloniaFact]
    public async Task ASendWithNowhereToGoIsLoggedAsNoPane()
    {
        _canType = false;
        Wire("a", "b");
        Capture();
        await OpenAsync();

        await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Contains($"input n={Name} -> err code={MirrorProtocol.ErrNoPane}", _serverLog);
    }

    [AvaloniaFact]
    public async Task ASendWithRepliesSwitchedOffIsLogged()
    {
        Wire("a", "b");
        Capture();
        await OpenAsync();
        _replyEnabled = false;

        await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Contains($"input n={Name} -> err code={MirrorProtocol.ErrReplyOff}", _serverLog);
    }

    [AvaloniaFact]
    public async Task ASendForASessionTheFarSideNoLongerHasIsLogged()
    {
        Wire("a", "b");
        Capture();
        await OpenAsync();
        _sessions.Clear();
        _agents.Clear();

        await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Contains($"input n={Name} -> err code={MirrorProtocol.ErrNoSession}", _serverLog);
    }

    [AvaloniaFact]
    public async Task ASendTheNearSideCannotRouteIsLoggedThereOnly()
    {
        Wire("a", "b");
        Capture();
        await OpenAsync();

        var outcome = await _client.SendInputDetailedAsync("nobody-home", Secret);

        Assert.Equal(MirrorProtocol.ErrNoSession, outcome.Error);
        Assert.Contains($"input n=nobody-home -> err code={MirrorProtocol.ErrNoSession}", _clientLog);
        Assert.Empty(_serverLog);
    }

    [AvaloniaFact]
    public async Task AnInputThatFailedItsHashIsLoggedWithoutAName()
    {
        _mangleInput = true;
        Wire("a", "b");
        Capture();
        await OpenAsync();

        await _client.SendInputDetailedAsync(Name, Secret);

        Assert.Contains($"input n=? -> err code={MirrorProtocol.ErrBadHash}", _serverLog);
        AssertNoSecretLogged();
    }

    [AvaloniaFact]
    public async Task AnInputWithNoNameIsLoggedWithoutOne()
    {
        Wire("a", "b");
        Capture();

        var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Input, "id1", new Dictionary<string, string>(), Encoding.UTF8.GetBytes(Secret)))!;
        await _server.HandleAsync(NearRelay, frame);

        Assert.Contains($"input n=? -> err code={MirrorProtocol.ErrNoSession}", _serverLog);
        AssertNoSecretLogged();
    }

    [AvaloniaTheory]
    [InlineData(true, "ok via=typed")]
    [InlineData(false, "ok via=refused")]
    public async Task ASlashLineIsLoggedByHowItRan(bool canType, string expected)
    {
        _canType = canType;
        Wire("a", "b");
        Capture();
        await OpenAsync();

        await _client.SendInputDetailedAsync(Name, "/color " + Secret, slash: true);
        await _server.SlashInFlight;

        Assert.Contains($"input n={Name} -> {expected}", _serverLog);
        Assert.Contains($"input n={Name} -> {expected}", _clientLog);
        AssertNoSecretLogged();
    }

    [AvaloniaFact]
    public async Task AControlSlashIsLoggedAsControl()
    {
        _wireSlash = true;
        _slashCatalogue = new[] { new SlashCommand("/color", "", SlashRouteKind.Control) };
        Wire("a", "b");
        Capture();
        await OpenAsync();

        await _client.SendInputDetailedAsync(Name, "/color green", slash: true);
        await _server.SlashInFlight;

        Assert.Contains($"input n={Name} -> ok via={MirrorProtocol.ViaControl}", _serverLog);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOpenThatFailsIsLoggedWithWhy(bool mangle)
    {
        _stallFetch = !mangle;
        _mangle = mangle;
        Wire("a", "b");
        Capture();
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(50);

        var session = NewSession();
        session.PanelOpened();
        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.True(await PumpUntilAsync(() => { lock (_clientLog) return _clientLog.Any(l => l.StartsWith($"open n={Name} failed why=")); }));
        Assert.DoesNotContain(_clientLog, l => l == $"open n={Name} ok");
    }

    // --- through the panel itself ----------------------------------------------------

    // The panel is a singleton that outlives every session it shows, so what it
    // says about *where a message goes* has to follow the session it is bound
    // to — and follow it when that session changes underneath.
    [AvaloniaFact]
    public async Task ThePanelsInputBoxFollowsThePanelItHasBecome()
    {
        Wire("a", "b");

        var session = NewSession();
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        // Bound while it is still a messaging channel.
        ChatPanel.OpenFor(orb, session);

        var input = ChatPanelTestAccess.Instance!.FindControl<TextBox>("Input")!;
        Assert.Contains("Message", input.Watermark!);

        // ...and upgraded underneath it. Read once at bind, the box would go on
        // describing the panel it used to be.
        await _client.DiscoverAsync(Peers, new[] { Name });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("terminal", input.Watermark!);

        ChatPanel.HideFor(session.SessionId);
    }

    // Binding away from a live view has to stop the far side serving it: a relay
    // kept awake by a panel nobody is looking at is somebody's quota. Rebinding
    // to it starts again.
    [AvaloniaFact]
    public async Task BindingAwayFromALiveViewStopsItAndComingBackResumesIt()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        ChatPanel.OpenFor(orb, session);
        Assert.True(_server.Busy);

        // A different session takes the panel, which unbinds the first.
        var other = new FakeChatSession { SessionId = "other-" + Guid.NewGuid(), DisplayName = "Other" };
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), other);

        Assert.False(_server.Busy);

        ChatPanel.OpenFor(orb, session);
        Assert.True(_server.Busy);

        ChatPanel.HideFor(session.SessionId);
        ChatPanel.HideFor(other.SessionId);
    }

    // --- the frame door ------------------------------------------------------------

    // --- wiring ----------------------------------------------------------------------

    // --- a live view whose relay has gone -----------------------------------

    // Typing into a live view goes through the link's mirror client, and there
    // is a window where the panel is mirroring and the client has gone — the
    // link dropped under it. The message has to come back with something a
    // person can act on rather than disappearing, and since CB-238 that names
    // the link, not a relay that no longer exists.
    [AvaloniaFact]
    public async Task TypingWithNoClientLeftSaysTheLinkIsNotConnected()
    {
        Wire("what is left?", "one thing");
        var session = await OpenAsync();

        RemoteControlSessions.UseMirrorClientForTests(Account, null);

        var outcome = await session.SendAsync("still there?");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text == RemoteControlChatSession.NotConnectedNote);
        Assert.Equal(ChatSendOutcome.Failed, outcome);
    }

    // And the typed message stays on screen above that note rather than being
    // discarded, which is the same contract the messaging mode has.
    [AvaloniaFact]
    public async Task AMessageThatCouldNotBeTypedIsStillOnScreen()
    {
        Wire("what is left?", "one thing");
        var session = await OpenAsync();

        RemoteControlSessions.UseMirrorClientForTests(Account, null);

        await session.SendAsync("still there?");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(session.History,
            t => t.Role == ChatRole.User && t.Text == "still there?");
    }

    // --- scrolling back over a stretch nobody sees ---------------------------

    // A page that parsed to nothing but moved the offset is not the end of the
    // conversation. A window can be entirely file-history snapshots — on a real
    // transcript most of the bytes are — and treating "no turns in this page" as
    // "no more conversation" would stop the scroll dead in the middle of one.
    //
    // Same rule LocalCliChatSession follows for a local session: the answer is
    // whether there is more to *ask for*, not whether this page had anything in
    // it.
    [AvaloniaFact]
    public async Task APageOfNothingButSnapshotsIsNotTheTopOfTheConversation()
    {
        // A long stretch of rows no panel shows, with a page back landing
        // entirely inside it.
        //
        // **Three layers, and the middle one is the subject.** The opening
        // window grows until it has enough conversation to fill a panel, and a
        // stretch of snapshots yields no turns at all — so it grows straight
        // past them, which is exactly the point of growing.
        //
        // This used to end with a single turn, which was enough while growth
        // stopped at what fit one chunk. Growth stops at enough *turns* now, so
        // one turn at the end sends it hunting all the way through the
        // snapshots and into the conversation behind them, swallowing the whole
        // file and leaving nothing to page into.
        //
        // A real run of recent conversation stops the growth where a real
        // transcript would, which puts the snapshots back in the middle — the
        // only place they can be for this test to mean anything.
        var rows = new List<string>();

        for (var i = 0; i < 3000; i++)
            rows.Add(UserRow($"u{i}", $"the first thing said {i} " + new string('q', 200)));

        for (var i = 0; i < 4000; i++)
            rows.Add("{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
                     + new string('z', 600) + "\"}");

        for (var i = 0; i < 60; i++)
            rows.Add(AssistantRow($"a{i}", $"the last thing said {i}"));

        WireRows(rows);
        var session = await OpenAsync();

        // The first page back is all snapshots, so it yields no turns — and the
        // session must still report that there is more behind it.
        var more = await session.LoadOlderAsync(default);

        Assert.True(more, "a page of snapshots must not read as the top of the conversation");
    }

    private static RemoteControlChatSession NewSession() =>
        new($"rc:{Account}:{Name}", Account, Name);

    // Everything the far session said, with the panel's own banner left out.
    private static IReadOnlyList<ChatTurn> Turns(RemoteControlChatSession session) =>
        session.History.Where(t => t.Role != ChatRole.System).ToList();

    private async Task<RemoteControlChatSession> OpenAsync(
        Action<RemoteControlChatSession>? before = null, bool expectMirror = true)
    {
        var session = NewSession();
        before?.Invoke(session);

        session.PanelOpened();

        // Discovering is all it takes: the roster landing raises MirrorChanged,
        // the session upgrades itself and reads the tail. Opening the feed by
        // hand here would be a second window and would hide whether the session
        // does that for itself.
        await _client.DiscoverAsync(Peers, new[] { Name });

        if (expectMirror) Assert.True(session.IsMirroring, "the panel should have upgraded to a live view");

        return session;
    }

    // The machines to ask, by name.
    //
    // **This used to be a list of BridgeProtocol.RemoteAgent that the client
    // filtered down to relays** — IsOwnRelay, IsOffline, IsRemoteControl — which
    // is why the second entry below was the session itself and was expected to
    // be dropped. A direct link has no such list and needs no such filter: it
    // knows the machines it is connected to, which is the answer that filtering
    // was working towards.
    private static IReadOnlyList<string> Peers => new[] { FarRelay };
    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";

    private string _path = "";

    private void Wire(string question, string answer) =>
        WireRows(new List<string> { UserRow("u1", question), AssistantRow("a1", answer) });

    private void WireRows(List<string> rows)
    {
        _path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(_path, string.Join("\n", rows) + "\n");

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242,
            State = _farState ?? new SessionStatus().State
        }));

        BuildServer();
        BuildClient();
    }

    // No far Buddy at all: a client with nothing on the other end of it.
    private void WireClientOnly()
    {
        BuildServer();
        BuildClient();
    }

    private void Append(string row) => File.AppendAllText(_path, row + "\n");

    private void BuildServer() =>
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => _replyEnabled,
            _ => _canType,
            (status, text) =>
            {
                _typed.Add((status.Title, text));
                return Task.FromResult(_typeResult);
            },
            SlashCommandsFor: _wireSlash ? _ => _slashCatalogue : null,
            RunSlash: _wireSlash
                ? (_, line) =>
                {
                    _slashRan.Add(line);
                    return Task.FromResult(_slashOutcome);
                }
                : null,
            CanDeliver: status => _canDeliver,
            Deliver: async (status, text) =>
            {
                if (_deliverThrows) throw new InvalidOperationException("messaging socket went away");

                _delivered.Add((status.Title, text));
                return new DeliveryReceipt(_deliverResult, _deliverAgentStatus);
            },
            // Who may ask, which the server no longer guesses. It used to fall
            // back to a name test — anything called `claude-buddy-rc-…` was
            // taken for another Buddy's relay — and a name is not a credential.
            // It refuses by default now; the real transport answers properly,
            // because a peer has completed a TLS handshake with a certificate
            // somebody pinned by typing a code. A harness says yes explicitly.
            PeerAllowed: _ => true,
            // CB-252: never the developer's real persona files.
            UserConfigDirs: () => Array.Empty<string>(),
            CapturePane: _farScreen is null ? null : _ => Task.FromResult<string?>(_farScreen),
            CanReadPane: _farScreen is null ? null : _ => true,
            PressKey: _farScreen is null ? null : (_, key) => { _pressed.Add(key); return Task.FromResult(true); }));

    private void BuildClient()
    {
        _client = new RemoteMirrorClient(Account, new RemoteMirrorClient.Seams(SendToServerAsync));
        RemoteControlSessions.UseMirrorClientForTests(Account, _client);
    }

    private async Task<bool> SendToServerAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        lock (_toServer) _toServer.Add(frame.Type);

        if (_stallFetch && frame.Type == MirrorProtocol.Fetch) return true;
        if (_stallInput && frame.Type == MirrorProtocol.Input) return true;
        if (_unreachableInput && frame.Type == MirrorProtocol.Input) return false;

        if (_bareOkForFetch && frame.Type == MirrorProtocol.Fetch)
        {
            await _client.OnFrameAsync(FarRelay, MirrorProtocol.TryParseFrame(
                MirrorProtocol.BuildFrame(MirrorProtocol.Ok, frame.Id))!);
            return true;
        }

        // A courier that alters a message on its way to somebody's terminal.
        if (_mangleInput && frame.Type == MirrorProtocol.Input) frame = Mangle(line) ?? frame;

        await _server.HandleAsync(NearRelay, frame);
        return true;
    }

    private async Task<bool> SendToClientAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        // Only the transcript windows, not the roster: a handshake that failed
        // would leave the panel in messaging mode and never reach the code this
        // test is about. A window carries wfrom; a roster does not.
        if (_mangle && frame.Type == MirrorProtocol.Chunk && frame.Get("wfrom") is not null)
            frame = Mangle(line) ?? frame;

        // The roster as a far Buddy from before CB-306 would have sent it:
        // re-encoded without the route kinds, with its hashes recomputed so it
        // arrives as a perfectly valid roster that simply says less.
        if (_oldFarRoster && frame.Type == MirrorProtocol.Chunk && frame.Get("wfrom") is null
            && frame.Payload is not null && MirrorProtocol.DecodeRoster(frame.Payload) is { } entries)
        {
            var payload = MirrorProtocol.EncodeRoster(
                entries.Select(e => e with { CommandRoutes = null }).ToList());
            var fields = frame.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            fields["H"] = MirrorProtocol.Hash(payload);
            frame = MirrorProtocol.TryParseFrame(
                MirrorProtocol.BuildFrame(MirrorProtocol.Chunk, frame.Id, fields, payload))!;
        }

        if (_relayOffThread) await Task.Run(() => _client.OnFrameAsync(FarRelay, frame));
        else await _client.OnFrameAsync(FarRelay, frame);

        return true;
    }

    // A courier that quietly rewords what it was asked to relay.
    private static MirrorProtocol.MirrorFrame? Mangle(string line)
    {
        var start = line.IndexOf(";p=", StringComparison.Ordinal);
        var end = line.IndexOf(";h=", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;

        var swapped = Convert.ToBase64String(Encoding.UTF8.GetBytes("a tidier version of that"));
        return MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]);
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";

    // The same row with its content properly encoded.
    //
    // UserRow interpolates raw, which is fine for the one-word bodies every
    // other case uses and silently wrong for anything carrying a quote or a
    // newline: the row becomes invalid JSON split across several lines, the
    // parser skips it, and a test asserting that something did NOT appear twice
    // then passes because nothing appeared at all. That is exactly how the
    // cross-session echo case below first passed while covering none of the
    // code it was written for.
    private static string UserRowEncoded(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}}";

    private static string AssistantRow(string uuid, string text) =>
        $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";
}
