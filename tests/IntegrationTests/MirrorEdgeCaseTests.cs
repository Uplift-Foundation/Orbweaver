using Xunit;

namespace Orbweaver.Tests;

// The paths a mirror takes when something is missing, stale, refused or asked
// for twice — the ones a happy round trip never reaches.
//
// Split from MirrorRoundTripTests so that file stays a readable statement of
// what the feature does, and this one carries the accumulated "and what if"
// list without burying it.
// In the Settings collection, which by now is really "tests that touch
// process-global Buddy state" — this class calls
// RemoteControlSessions.ResetForTests(), which clears the installed mirror
// client and the MirrorChanged subscribers out from under anything else using
// them. IntegrationTests does not disable parallelisation
// the way UiTests does, so without this these classes really do run at once.
// Costs nothing in an ordinary run: the live tests skip in milliseconds.
[Collection("Settings")]
public class MirrorEdgeCaseTests : IDisposable
{
    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";

    private readonly string _dir;
    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();
    private readonly List<string> _toClient = new();

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;

    private readonly List<RemoteMirrorClient.MirrorRows> _windows = new();
    private readonly List<RemoteMirrorClient.MirrorRows> _deltas = new();
    private readonly List<(string Name, string Why)> _failures = new();

    private bool _dropEverything;
    private int _refuseAfter = -1;
    private int _sent;
    private string _path = "";

    public MirrorEdgeCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Build();
    }

    public void Dispose()
    {
        RemoteControlSessions.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- asking about nothing --------------------------------------------------

    // A HELLO naming nothing means "what have you got?", and is answered with a
    // roster rather than refused.
    //
    // **This assertion is the reverse of what it used to be, and the reversal is
    // deliberate.** The old rule — a nameless handshake is malformed — was right
    // for the relay and wrong in general. Over the relay the asker already had a
    // list of sessions from ListAgents, so a hello naming none of them really
    // was a question asked wrong; and answering it would have described sessions
    // the far peer list could not see, which is a visibility rule the mirror had
    // no business undoing.
    //
    // A direct link has neither property. There is no prior list, so a nameless
    // hello is the *only* possible first question — without an answer there is
    // nothing to put an orb on and no name to ask about next. And the asker is
    // not any process sharing an account: it presented a certificate pinned when
    // a person typed a pairing code.
    //
    // Nothing new becomes visible either way: the same IsLocalCli filter still
    // applies, so the answer covers only sessions this machine would show.
    [Fact]
    public async Task AHandshakeThatNamesNoSessionsIsAnsweredWithEverything()
    {
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Hello, "abcd1234"));

        Assert.DoesNotContain(_toClient, line => line.Contains(";t=" + MirrorProtocol.Err));
        Assert.Contains(_toClient, line => line.Contains(";t=" + MirrorProtocol.Chunk));
    }

    [Fact]
    public async Task AFetchWithNoNameIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Fetch, "abcd1234"));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoSession));
    }

    [Fact]
    public async Task AFetchForASessionThatIsNotHereIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Fetch, "abcd1234",
            ("n", MirrorProtocol.Encode("someone-else")), ("w", "tail")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoSession));
    }

    // Registered, running, and Buddy has no transcript path for it — a session
    // whose hook has not written one yet.
    [Fact]
    public async Task AFetchForASessionWithNoTranscriptSaysSoRatherThanFailingSilently()
    {
        AddSession(transcriptPath: "");

        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Fetch, "abcd1234",
            ("n", MirrorProtocol.Encode(Name)), ("w", "tail")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoTranscript));
    }

    // The path is there and the file is not — a session whose transcript was
    // deleted under it.
    [Fact]
    public async Task AFetchForATranscriptThatIsNotOnDiskIsRefused()
    {
        AddSession(transcriptPath: Path.Combine(_dir, "gone.jsonl"));

        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Fetch, "abcd1234",
            ("n", MirrorProtocol.Encode(Name)), ("w", "tail")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoTranscript));
    }

    [Fact]
    public async Task AWatchOnASessionThatIsNotHereIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Watch, "abcd1234", ("n", MirrorProtocol.Encode("someone-else"))));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrNoSession));
    }

    [Fact]
    public async Task AWatchWithNoNameIsDroppedRatherThanRegistered()
    {
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Watch, "abcd1234"));

        Assert.False(_server.Busy);
    }

    [Fact]
    public async Task AResendForATransferNobodyRemembersIsRefused()
    {
        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Resend, "abcd1234", ("seq", "0")));

        Assert.Contains(_toClient, line => line.Contains("code=" + MirrorProtocol.ErrBadHash));
    }

    [Fact]
    public async Task AnInputWhoseTextDidNotSurviveIsRefusedBeforeAnythingIsResolved()
    {
        AddSession();

        // A payload whose digest does not match: exactly what a courier that
        // altered the text in flight produces. Typing this into somebody's
        // terminal is the worst thing that could happen in this file.
        var line = "CB-MIRROR:v1;t=" + MirrorProtocol.Input + ";id=abcd1234;n="
                   + MirrorProtocol.Encode(Name)
                   + ";p=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("rm -rf /"))
                   + ";h=" + new string('0', 64);

        await _server.HandleAsync(NearRelay, MirrorProtocol.TryParseFrame(line)!);

        Assert.Contains(_toClient, l => l.Contains("code=" + MirrorProtocol.ErrBadHash));
        Assert.Empty(_typed);
    }

    [Fact]
    public async Task AnInputWithNoNameIsRefused()
    {
        var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Input, "abcd1234", null,
            System.Text.Encoding.UTF8.GetBytes("hello")))!;

        await _server.HandleAsync(NearRelay, frame);

        Assert.Contains(_toClient, l => l.Contains("code=" + MirrorProtocol.ErrNoSession));
        Assert.Empty(_typed);
    }

    // The typing itself failing — tmux gone, pane closed between the check and
    // the send. Reported rather than swallowed, since the person is waiting to
    // see their message appear.
    [Fact]
    public async Task ATypeThatFailsAtTheLastMomentIsReportedNotSwallowed()
    {
        AddSession();
        _typeSucceeds = false;

        await Handshake();

        // ErrTypeFailed, not ErrNoPane: a route was found and the delivery
        // threw. Reporting "there is nowhere to type" for that is a statement
        // about the session's terminal, and it is the wrong thing for a user
        // to act on — the fix for a throw is consent or a reopened window,
        // not a different terminal.
        Assert.Equal(MirrorProtocol.ErrTypeFailed, await _client.SendInputAsync(Name, "hello"));
    }

    [Fact]
    public async Task ATypeThatThrowsIsReportedRatherThanCrashingTheRelay()
    {
        AddSession();
        _typeThrows = true;

        await Handshake();

        Assert.Equal(MirrorProtocol.ErrTypeFailed, await _client.SendInputAsync(Name, "hello"));
    }

    [Fact]
    public async Task SendingToASessionNoBuddyHasClaimedIsRefusedWithoutAskingAnyone()
    {
        Assert.Equal(MirrorProtocol.ErrNoSession, await _client.SendInputAsync("never-heard-of-it", "hi"));
        Assert.Empty(_toClient);
    }

    // --- the client's side -------------------------------------------------------

    [Fact]
    public async Task ADiscoveryThatAsksAboutNothingSendsNothing()
    {
        await _client.DiscoverAsync(Peers, Array.Empty<string>());

        Assert.Empty(_toClient);
    }

    // Silence is not "no". A relay that was busy, or whose model did not call
    // the tool, is asked again next poll — only a real answer settles a name as
    // having no live view.
    [Fact]
    public async Task ARelayThatDoesNotAnswerLeavesTheQuestionOpen()
    {
        AddSession();
        _dropEverything = true;

        // The one test here that waits out a timeout on purpose, so it is the
        // one that shortens it.
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unknown,
            _client.StateFor(Name).Availability);
    }

    // Asked once and answered, the roster is not asked for again — every
    // handshake is a model turn on both relays.
    [Fact]
    public async Task ASessionAlreadyAnsweredForIsNotAskedAboutAgain()
    {
        AddSession();

        await Handshake();
        var after = _toClient.Count;

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.Equal(after, _toClient.Count);
    }

    [Fact]
    public async Task OpeningASessionNoBuddyHasClaimedDoesNothing()
    {
        Assert.False(await _client.OpenAsync("never-heard-of-it"));
        Assert.Empty(_toClient);
    }

    [Fact]
    public async Task ThereIsNoBacklogForASessionThatWasNeverOpened() =>
        await Task.Run(() =>
        {
            Assert.False(_client.HasMore("never-heard-of-it"));
            Assert.Null(_client.LoadOlderAsync("never-heard-of-it").Result);
        });

    // Rebinding a panel to the session it is already showing should cost a
    // renewal, not another full read of the tail — the panel is a singleton and
    // clicking between two orbs rebinds constantly.
    [Fact]
    public async Task ReopeningAPanelAlreadyOpenDoesNotReReadTheWholeTail()
    {
        AddSession(rows: 20);
        await Handshake();

        await _client.OpenAsync(Name);
        Assert.Single(_windows);

        await _client.ReopenAsync(Name);

        Assert.Single(_windows);
    }

    [Fact]
    public async Task ReopeningAPanelThatWasClosedReadsItAgain()
    {
        AddSession(rows: 20);
        await Handshake();

        await _client.OpenAsync(Name);
        await _client.CloseAsync(Name);
        await _client.ReopenAsync(Name);

        Assert.Equal(2, _windows.Count);
    }

    // Closing tells the far side to stop pushing rather than letting the watch
    // lapse: a relay kept awake by a panel nobody is looking at is somebody's
    // quota.
    [Fact]
    public async Task ClosingAPanelStopsTheFarSideServingIt()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        Assert.True(_server.Busy);

        await _client.CloseAsync(Name);

        Assert.False(_server.Busy);
        Assert.False(_client.Busy);
    }

    [Fact]
    public async Task ClosingSomethingThatWasNeverOpenIsHarmless()
    {
        await _client.CloseAsync("never-heard-of-it");

        Assert.Empty(_toClient);
    }

    // A watch that is never renewed lapses, so a Buddy that quit without saying
    // goodbye stops being served.
    [Fact]
    public async Task AWatchNobodyRenewsLapsesOnItsOwn()
    {
        AddSession(rows: 20);
        await Handshake();

        await _server.HandleAsync(NearRelay, Frame(
            MirrorProtocol.Watch, "abcd1234",
            ("n", MirrorProtocol.Encode(Name)), ("ttl", "0")));

        Assert.True(_server.Busy);

        // TTL 0 is out of range and is clamped to the maximum rather than taken
        // literally, so this is still live...
        await _server.TickAsync();
        Assert.True(_server.Busy);

        // ...whereas an explicit unwatch is not.
        await _server.HandleAsync(NearRelay, Frame(MirrorProtocol.Unwatch, "abcd1234"));
        Assert.False(_server.Busy);
    }

    [Fact]
    public async Task RenewingAWatchUpdatesTheOneAlreadyThereRatherThanStackingAnother()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        // A panel left open for an afternoon renews every ninety seconds;
        // thirty stacked subscriptions would send every update thirty times.
        for (var i = 0; i < 5; i++) await _client.TickAsync();

        File.AppendAllText(_path, UserRow("late", "one more") + "\n");
        await _server.TickAsync();

        Assert.Single(_deltas);
    }

    [Fact]
    public async Task TickingWithNothingOpenDoesNothing()
    {
        await _client.TickAsync();
        await _server.TickAsync();

        Assert.Empty(_toClient);
    }

    // A session that goes away while it is being watched stops producing
    // updates instead of throwing on every tick.
    [Fact]
    public async Task AWatchedSessionThatDisappearsJustStopsUpdating()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        _agents.Clear();
        _sessions.Clear();

        File.AppendAllText(_path, UserRow("late", "nobody will see this") + "\n");
        await _server.TickAsync();

        Assert.Empty(_deltas);
    }

    // A far Buddy's roster carries the command list, read off its own disk, so
    // it wins over anything a model recited — and it includes built-ins, which
    // now genuinely run.
    [Fact]
    public async Task ARosterCommandListBeatsOneAModelRecited()
    {
        AddSession(rows: 4);
        await Handshake();

        RemoteControlSessions.UseMirrorClientForTests(Account, _client);

        var commands = RemoteControlSessions.CommandsFor(Account, Name);

        Assert.Contains(commands, c => c.Name == "/color");
    }

    [Fact]
    public void WithNoMirrorAtAllTheStateIsUnknownRatherThanAThrow()
    {
        var state = RemoteControlSessions.MirrorStateFor("no-such-account", "nobody");

        Assert.Equal(RemoteMirrorClient.MirrorAvailability.Unknown, state.Availability);
        Assert.Null(state.Entry);
        Assert.Null(RemoteControlSessions.MirrorClientFor("no-such-account"));
        Assert.Empty(RemoteControlSessions.CommandsFor("no-such-account", "nobody"));
    }

    // --- the last few branches ---------------------------------------------------

    // A session the registry knows and Buddy's own status files do not yet —
    // its hook has fired for the registry but not for the scan. Matching on pid
    // as well costs nothing and covers the gap.
    [Fact]
    public async Task ASessionWhoseIdBuddyDoesNotKnowIsStillFoundByItsPid()
    {
        _path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(_path, UserRow("u0", "hello") + "\n");

        // The registry's session id and Buddy's disagree; only the pid lines up.
        _agents.Add(new AgentRoster.Entry(Name, "an-id-buddy-has-never-seen", 4242));
        _sessions.Add(("a-different-id", new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242
        }));

        await Handshake();

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            _client.StateFor(Name).Availability);
    }

    // A watch nobody renews lapses, so a Buddy that quit without saying goodbye
    // stops being served rather than being pushed to forever.
    [Fact]
    public async Task AWatchThatIsNeverRenewedLapses()
    {
        AddSession(rows: 20);

        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        _server.Now = () => now;

        await Handshake();
        await _client.OpenAsync(Name);

        Assert.True(_server.Busy);

        now = now.AddSeconds(MirrorProtocol.WatchTtlSeconds + 1);
        await _server.TickAsync();

        Assert.False(_server.Busy);
    }

    // The transcript going away under a live watch. The next tick tries again
    // rather than the whole relay falling over.
    [Fact]
    public async Task ATranscriptDeletedUnderALiveWatchDoesNotTakeTheRelayWithIt()
    {
        AddSession(rows: 20);
        await Handshake();
        await _client.OpenAsync(Name);

        File.Delete(_path);

        await _server.TickAsync();

        Assert.Empty(_deltas);
        Assert.True(_server.Busy);
    }

    // A relay that refuses mid-transfer. Stopping is right: the client times the
    // transfer out and can ask again, and pushing the rest would spend turns
    // filling in something nobody can complete.
    [Fact]
    public async Task AFarSideThatStopsAcceptingDoesNotLeaveThePanelWaiting()
    {
        // **There is no longer a middle to stop in, which is why this is no
        // longer named for one.** A transfer was dozens of frames, so a far
        // side that stopped accepting halfway left one genuinely half-pushed.
        // A message goes whole, so the only thing left to test — and the thing
        // a user would actually feel — is that a send which cannot be delivered
        // ends the wait rather than hanging the panel.
        AddSession(IncompressibleTranscript());

        await Handshake();

        // Failure here arrives as a timeout — the far side simply stops
        // sending — so this is the other test that shortens it.
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);

        _refuseAfter = 0;
        _windows.Clear();

        Assert.False(await _client.OpenAsync(Name));
        Assert.Empty(_windows);
    }

    // --- CB-46: one window request per session, however many callers ---------

    // The measured bug: four distinct FETCHes for one session inside 78 seconds,
    // against a three-minute timeout, so none of them were retries. Each one
    // makes the far side build and queue another whole window, so a relay
    // already minutes deep in the first acquires more behind it and the queue
    // grows faster than a model turn can drain it.
    //
    // Asserted on task identity rather than on a frame count, because that is
    // the actual contract: a second caller is handed the first answer, not a
    // second conversation about it.
    [Fact]
    public async Task ASecondAskForAWindowJoinsTheFirstRatherThanStartingAnother()
    {
        AddSession(rows: 20);
        await Handshake();

        _holdFetch = new TaskCompletionSource();

        var first = _client.OpenAsync(Name);
        var second = _client.OpenAsync(Name);

        Assert.Same(first, second);

        _holdFetch.SetResult();

        Assert.True(await first);
        Assert.True(await second);

        // One window, not two.
        Assert.Single(_windows);
    }

    // The specific path that defeated the old guard, and the reason it was not
    // obvious: Loading lived on the Feed, and CloseAsync *removes* the Feed. A
    // panel being rebound — which clicking between two orbs does constantly —
    // threw away the only record that a fetch was running, so the next open
    // started another.
    [Fact]
    public async Task ClosingAPanelMidFetchDoesNotLetTheNextOpenStartASecondOne()
    {
        AddSession(rows: 20);
        await Handshake();

        _holdFetch = new TaskCompletionSource();

        var first = _client.OpenAsync(Name);

        // The panel closes while the window is still crossing the wire.
        await _client.CloseAsync(Name);

        var second = _client.OpenAsync(Name);

        Assert.Same(first, second);

        _holdFetch.SetResult();
        await first;

        Assert.Single(_windows);
    }

    // A refusal is not a running request and must not be remembered as one: a
    // session with no roster entry answers no immediately, and caching that
    // would answer every later caller with the same stale no — including the
    // one that asks after the roster finally arrives.
    [Fact]
    public async Task ARefusalIsNotRememberedAsAnOutstandingRequest()
    {
        AddSession(rows: 20);

        Assert.False(await _client.OpenAsync("nobody-here"));

        await Handshake();

        Assert.True(await _client.OpenAsync(Name));
    }

    // A range fetch that names no upper bound means "to the end of the file".
    //
    // Reachable only by building the frame by hand: LoadOlderAsync always knows
    // where its page stops, so the client never omits `to`. It is still part of
    // the protocol another machine's Buddy speaks, and a version of it that
    // guessed zero would answer an empty window rather than the tail.
    [Fact]
    public async Task ARangeFetchWithNoEndReadsToTheEndOfTheFile()
    {
        AddSession(rows: 20);
        await Handshake();

        _windows.Clear();

        var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Fetch, "range-1",
            new Dictionary<string, string>
            {
                ["n"] = MirrorProtocol.Encode(Name),
                ["w"] = "range",
                ["from"] = "0"
            }));

        await _server.HandleAsync(NearRelay, frame!);

        // The whole conversation came back, which is what "no end" has to mean:
        // an empty window would be the same answer as a broken one.
        var sent = string.Join("\n", _toClient);
        Assert.Contains("t=CHUNK", sent);
        Assert.Contains("range-1", sent);
    }

    // The case that will actually be live: a machine running this code asking a
    // machine that is not.
    //
    // The window sizing is the *server's* half, so until the far Buddy is
    // updated it keeps building the old oversized window and sending it over
    // several chunks. The client half still has to make that arrive, and this is
    // what does it: the wait is reset by each verified chunk, so a transfer that
    // takes far longer than one timeout completes as long as it keeps making
    // progress. Under the old flat deadline the request expired mid-transfer and
    // the remaining chunks arrived as replies to nothing — which is precisely
    // how a four-minute window died against a three-minute timeout.
    //
    // **Asserted on the resets rather than on the clock, and that is a
    // correction.** The first version of this ran the transfer slower than one
    // interval and checked it had outlasted it, which reads as the obvious test
    // and is a wall-clock claim CI cannot honour: it went red on the macOS leg —
    // "a transfer that keeps delivering verified chunks must not be timed out" —
    // while passing on Windows, because a loaded runner stalled longer between
    // two chunks than the deliberately short timeout allowed. Widening the
    // window would only have moved the failure, and a sleep would be the same
    // mistake this repository has already fixed four times.
    //
    // The deadline is pushed out once per intermediate chunk, unconditionally,
    // so the count is a property of the transfer's *shape* and not of how fast
    // the machine was. That makes the mechanism assertable with no clock in it:
    // a flat deadline scores zero resets, which is the regression worth
    // catching, and the completion assertion below carries the rest.
        // **This asserted deadline-extension across a multi-chunk transfer, and a
    // multi-chunk transfer is no longer a thing that happens.**
    //
    // The mechanism was a real fix for a real fault: a transfer was dozens of
    // chunks, each one a far model retyping ~8KB of base64 at roughly two
    // minutes a turn, so a flat deadline killed transfers that were making
    // perfectly good progress. RequestAsync pushed its deadline out on every
    // intermediate chunk to answer that.
    //
    // A message now goes whole over a socket, so there are no intermediate
    // chunks and nothing to extend. Rewritten rather than deleted, because the
    // surviving question is the one that matters to a user: does an
    // incompressible transcript — the case that used to need the most chunks —
    // still arrive, and arrive in one piece?
    [Fact]
    public async Task AnIncompressibleTranscriptStillArrivesAndArrivesWhole()
    {
        AddSession(IncompressibleTranscript());
        await Handshake();

        _windows.Clear();

        Assert.True(await _client.OpenAsync(Name));

        var delivered = Assert.Single(_windows);
        Assert.NotEmpty(delivered.Turns);
    }

    // The complement, and the case that was actually failing in the field.
    //
    // A single-chunk transfer has no intermediate chunk, so there is nothing to
    // reset the wait on: the deadline it starts with is the whole of the wait it
    // ever gets. That makes CB-46's shrink-until-it-fits and the extension above
    // interact in a way neither one predicts on its own — shrinking a window to
    // one chunk removes the very signal that was keeping long transfers alive,
    // so the *smallest* transfers became the ones most likely to be abandoned.
    //
    // Measured on 29 Aug 2026: a one-chunk window off the mini took `7m 15s` to
    // emit, arrived complete and verified, and was dropped because the fetch had
    // been given three minutes. Nothing on the wire was wrong. The deadline was
    // just shorter than the answer, and no renewal was ever going to come.
    //
    // So this asserts the absence deliberately, rather than leaving it implied:
    // if a future change ever does start extending single-chunk waits, the
    // reasoning behind MirrorProtocol.FetchTimeoutSeconds stops being load-
    // bearing and should be revisited rather than silently kept.
    [Fact]
    public async Task ASingleChunkTransferGetsNoExtensionSoItsFirstDeadlineIsAllItHas()
    {
        AddSession();                       // small enough to fit one chunk
        await Handshake();

        var before = _client.TimeoutExtensionsForTests;
        _windows.Clear();

        Assert.True(await _client.OpenAsync(Name));
        Assert.Single(_windows);

        Assert.Equal(before, _client.TimeoutExtensionsForTests);
    }

    // Discovery runs off the poll, and the poll does not wait for the last one.
    //
    // A name enters the roster only when a reply lands, so while a far relay is
    // slow every tick used to send another HELLO for the same name — every 20
    // seconds, or every 5 while anything was working. Measured on the mini as a
    // backlog of **166 roster requests** queued ahead of the window that had
    // actually been asked for, each costing a model turn to answer: the wait for
    // an answer manufactured the queue that stopped the answer arriving, and the
    // busier the relay got the harder it was asked.
    //
    // Held at the door rather than raced, so "in flight" is a fact the test
    // arranges instead of a guess about scheduling.
    [Fact]
    public async Task ASecondPollDoesNotAskAgainForAnAnswerAlreadyOnTheWire()
    {
        AddSession();

        _holdHello = new TaskCompletionSource();

        var first = _client.DiscoverAsync(Peers, new[] { Name });

        // The first HELLO is now parked mid-send. A poll landing here is the
        // case that used to pile on.
        var second = _client.DiscoverAsync(Peers, new[] { Name });

        // Counted rather than awaited, and that is deliberate. Awaiting the
        // second call asserts the same thing, but a regression then *hangs* it
        // against the same door instead of failing — and a suite that stops
        // dead is a worse way to learn this than one that says which number was
        // wrong. The frame count rises synchronously on the way to the gate, so
        // reading it here is a fact rather than a race.
        Assert.Equal(1, _hellos);

        _holdHello.SetResult();
        await first;
        await second;

        Assert.Equal(1, _hellos);
    }

    // And the suppression has to lift, or it trades a flood for a silence.
    //
    // Silence is still not "no": a relay that was busy or timed out is asked
    // again on the next poll, and only a real answer settles a name. A name left
    // marked as being asked about would never be asked again, which is the worse
    // of the two failures — the flood was at least still trying.
    [Fact]
    public async Task AskingAgainIsAllowedOnceTheFirstAnswerHasSettled()
    {
        AddSession();

        await _client.DiscoverAsync(Peers, new[] { Name });
        Assert.Equal(1, _hellos);

        // Answered, so the name is in the roster and there is nothing to ask.
        await _client.DiscoverAsync(Peers, new[] { Name });
        Assert.Equal(1, _hellos);

        // A name that is not known is asked about, proving the gate released
        // rather than the roster merely covering for it.
        await _client.DiscoverAsync(Peers, new[] { "some-other-session" });
        Assert.Equal(2, _hellos);
    }

    // What the poll asks before it takes the relay's only turn.
    //
    // A relay carries one frame per model turn, and Buddy polls the same relay
    // for ListAgents every tick. At the fast cadence the poll comes round again
    // before the last answer is finished, so a FETCH somebody is waiting on can
    // sit unsent behind an unbroken run of polls. Measured on 30 Aug 2026: a
    // FETCH not typed for eight minutes, by which time its own deadline had
    // gone — the far machine then answered correctly and was ignored for being
    // late. Identical ending to CB-54, completely different cause, which is
    // precisely why it read as CB-54 not being fixed.
    //
    // RemoteControlSessions.PollAsync reads this and skips ListAgents while it
    // is true. Asserted here rather than there because the poll itself types
    // into a live relay and is excluded from coverage; this is the decision it
    // is built on, and it is testable with no relay at all.
    [Fact]
    public async Task TheClientSaysItIsWaitingOnlyWhileARequestIsInFlight()
    {
        AddSession();
        await Handshake();

        Assert.False(_client.Waiting);

        _holdFetch = new TaskCompletionSource();

        var fetching = _client.OpenAsync(Name);

        // Parked mid-send, which is the whole window the poll must stay out of.
        Assert.True(_client.Waiting);

        _holdFetch.SetResult();
        await fetching;

        // And it has to clear, or one stuck request silences the peer list for
        // good — trading a starved fetch for a frozen roster.
        Assert.False(_client.Waiting);
    }

    // What the idle timer asks before it retires a relay.
    //
    // Remote Control shuts its relays down after RemoteControlIdleMinutes
    // without use, and "use" meant Touch(), which is called on send and nowhere
    // else. Watching sends nothing, so an open panel streaming a far machine's
    // conversation was idle by that definition and had the relay pulled out from
    // under it — measured overnight as 27 deltas and then nothing, with the
    // panel still showing 1 a.m. at 8 a.m.
    //
    // The window has to match the panel exactly: a feed exists between OpenAsync
    // and CloseAsync, and CloseAsync is what PanelClosed calls. True for as long
    // as somebody is looking, and no longer — a client that stayed "watching"
    // after the panel closed would keep a live Claude Code session alive on
    // another machine for nothing, which is the cost the setting exists to
    // avoid.
    [Fact]
    public async Task WatchingIsTrueForExactlyAsLongAsAPanelIsOpen()
    {
        AddSession();
        await Handshake();

        Assert.False(_client.Watching);

        Assert.True(await _client.OpenAsync(Name));
        Assert.True(_client.Watching);

        await _client.CloseAsync(Name);
        Assert.False(_client.Watching);
    }

    // And silence still ends it. Extending on progress would be worthless if it
    // also extended on nothing — a far side that stops halfway has to become a
    // failure the panel can report rather than a wait nobody ever leaves.
    [Fact]
    public async Task ATransferThatGoesQuietStillTimesOut()
    {
        AddSession(IncompressibleTranscript());
        await Handshake();

        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

        // **Nothing gets through, where this used to let two frames past.** A
        // transfer was dozens of chunks, so stopping after two left one
        // genuinely half-delivered — there was a middle to stop in. A message
        // goes whole now, so "goes quiet" can only mean the answer never comes,
        // and letting the first frame through would let the transfer *finish*.
        //
        // The question this asks is unchanged and still the important one: a
        // wait that no answer will ever end has to end by itself.
        _refuseAfter = 0;
        _windows.Clear();

        Assert.False(await _client.OpenAsync(Name));
        Assert.Empty(_windows);
    }

    // The real wiring, constructed. Nothing here runs a process — the point is
    // that every delegate is present and the two that read settings read the
    // right ones, since a null in this record would be a crash the first time
    // another machine asked anything.
    [Fact]
    public void TheRealSeamsAreWiredToTheRealThings()
    {
        var seams = RemoteMirrorServer.RealSeams(
            ".claude-board",
            (_, _) => Task.FromResult(true),
            () => Array.Empty<(string, SessionStatus)>());

        Assert.NotNull(seams.SendFrame);
        Assert.NotNull(seams.LocalSessions);
        Assert.NotNull(seams.Agents);
        Assert.NotNull(seams.TypeInto);

        // The setting a local panel obeys is the one a remote request obeys —
        // asserted by *reading* both, not by flipping one.
        //
        // Flipping it is what the first version did, and CI caught it on the
        // Windows leg: OrbweaverSettings is process-global, its setters write
        // settings.json through a debounced save, and SettingsRoundTripTests
        // next door asserts a pending write is *not* on disk yet. That whole
        // class is [Collection("Settings")] precisely so no two settings tests
        // run at once; this one is not in it, so it raced and the deferred-save
        // test saw a file it had not written. Reading has no such hazard, and
        // still says the only thing worth saying here — that this delegate is
        // wired to the same source CliChatFormat reads rather than to a
        // constant.
        Assert.Equal(
            CliChatFormat.For(SessionSource.ClaudeCode).ReplyEnabled(),
            seams.ReplyEnabled(SessionSource.ClaudeCode));

        Assert.Equal(
            CliChatFormat.For(SessionSource.Codex).ReplyEnabled(),
            seams.ReplyEnabled(SessionSource.Codex));

        // A session with no pane cannot be typed into, which is answered without
        // running tmux at all.
        Assert.False(seams.CanType(new SessionStatus { Source = SessionSource.ClaudeCode }));
    }

    // --- wiring ----------------------------------------------------------------------

    private readonly List<(string Name, string Text)> _typed = new();
    private bool _typeSucceeds = true;
    private bool _typeThrows;

    // The machines to ask, by name.
    //
    // **This used to be a list of BridgeProtocol.RemoteAgent that the client
    // filtered down to relays** — IsOwnRelay, IsOffline, IsRemoteControl — which
    // is why the second entry below was the session itself and was expected to
    // be dropped. A direct link has no such list and needs no such filter: it
    // knows the machines it is connected to, which is the answer that filtering
    // was working towards.
    private static IReadOnlyList<string> Peers => new[] { FarRelay };
    private Task Handshake() => _client.DiscoverAsync(Peers, new[] { Name });

    // A transcript whose turns cannot be squeezed into one chunk, however small
    // a window the server chooses.
    //
    // Two properties, and both are needed. The text is incompressible, so gzip
    // cannot rescue it — and each single row is larger than a chunk on its own,
    // so *no* window size makes it fit. That second part is what makes the
    // fixture hold: since CB-46 the server shrinks a tail until it fits, so
    // merely being a big file is not enough — it would just send a smaller
    // window and arrive in one frame again, leaving this test no middle to stop
    // in. A row bigger than a chunk is the one shape shrinking cannot answer,
    // which is exactly why the server sends it oversized and the client's
    // timeout extends while pieces keep coming.
    //
    // Deterministically pseudo-random rather than actually random, so a failure
    // reproduces: the seed is fixed and the same bytes come out every run.
    private string IncompressibleTranscript()
    {
        var random = new Random(20260830);
        var alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rows = new List<string>();

        for (var i = 0; i < 8; i++)
        {
            // **A fixed size, not a multiple of ChunkBytes.** This was
            // `8 * ChunkBytes`, which was 48KB a row when a chunk was 6KB and
            // is two gigabytes now that a chunk is the whole 32MB message —
            // twenty-six seconds of random character generation per test, for
            // a property that needs only enough bytes to be worth compressing.
            //
            // What this fixture is *for* is content gzip cannot shrink, which
            // is a fact about the bytes and not about their number.
            var noise = new char[64 * 1024];
            for (var c = 0; c < noise.Length; c++) noise[c] = alphabet[random.Next(alphabet.Length)];

            rows.Add(UserRow($"u{i}", new string(noise)));
        }

        _path = Path.Combine(_dir, "incompressible.jsonl");
        File.WriteAllText(_path, string.Join("\n", rows) + "\n");

        return _path;
    }

    private void AddSession(string? transcriptPath = null, int rows = 2)
    {
        if (transcriptPath is null)
        {
            _path = Path.Combine(_dir, "session.jsonl");
            File.WriteAllText(_path, string.Join("\n",
                Enumerable.Range(0, rows).Select(i => UserRow($"u{i}", $"line {i}"))) + "\n");
            transcriptPath = _path;
        }

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            TmuxPane = "%1",
            SessionPid = 4242
        }));
    }

    private void Build()
    {
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => true,
            _ => true,
            (status, text) =>
            {
                if (_typeThrows) throw new InvalidOperationException("tmux went away");

                if (_typeSucceeds) _typed.Add((status.Title, text));
                return Task.FromResult(_typeSucceeds);
            },
            // **Who may ask, which is now a decision the caller has to make.**
            // The server used to fall back to a name test — anything called
            // `claude-buddy-rc-…` was another Buddy's relay — and that was a
            // guess dressed as a check: a name is not a credential and anyone on
            // the account could pick one. It defaults to refusing now, and the
            // real transport answers properly, because a peer has completed a
            // TLS handshake with a certificate somebody pinned by typing a code.
            //
            // A harness has no handshake to point at, so it says yes explicitly.
            // That is the honest shape: this file is testing what the server
            // does once a request is allowed, not who is allowed to make one.
            PeerAllowed: _ => true,
            // CB-252: never the developer's real persona files.
            UserConfigDirs: () => Array.Empty<string>()));

        _client = new RemoteMirrorClient(Account, new RemoteMirrorClient.Seams(SendToServerAsync))
        {
            // The real waits are minutes long, which is right for a relay whose
            // model may be mid-turn and far too long for a test.
            //
            // Generous rather than tight, and that is a correction: this was
            // 250ms, which is plenty for a loopback reply until the machine is
            // busy — and then a test that expected an *answer* saw a timeout
            // instead and failed once in a run. A timeout is the slowest thing
            // in this file, so the only test that should ever hit one is the one
            // deliberately dropping replies, which shortens this for itself.
            TimeoutOverrideForTests = TimeSpan.FromSeconds(10)
        };

        _client.Delivered += rows =>
        {
            if (rows.Mode == RemoteMirrorClient.MirrorDelivery.Window) _windows.Add(rows);
            else _deltas.Add(rows);
        };

        _client.Failed += (name, why) => _failures.Add((name, why));
    }

    private static MirrorProtocol.MirrorFrame Frame(
        string type, string id, params (string Key, string Value)[] fields) =>
        MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            type, id, fields.ToDictionary(f => f.Key, f => f.Value)))!;

    // Holds FETCH frames at the door until a test lets them through, so a
    // request can be genuinely in flight while something else asks for the same
    // thing. Without it "concurrent" is a guess about scheduling.
    private TaskCompletionSource? _holdFetch;

    // The same door, for HELLO, and counted on the way through. Discovery's
    // duplicate suppression is about what is *not* sent, so the test needs to
    // see every frame that reached the wire rather than only the replies.
    private TaskCompletionSource? _holdHello;
    private int _hellos;

    private async Task<bool> SendToServerAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        if (frame.Type == MirrorProtocol.Hello) _hellos++;

        if (_holdHello is { } helloGate && frame.Type == MirrorProtocol.Hello)
            await helloGate.Task;

        if (_holdFetch is { } gate && frame.Type == MirrorProtocol.Fetch)
            await gate.Task;

        await _server.HandleAsync(NearRelay, frame);
        return true;
    }

    private async Task<bool> SendToClientAsync(string peer, string line)
    {
        _toClient.Add(line);
        if (_dropEverything) return true;

        // A relay that stops accepting part-way through a transfer.
        if (_refuseAfter >= 0 && ++_sent > _refuseAfter) return false;

        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        await _client.OnFrameAsync(FarRelay, frame);
        return true;
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";
}
