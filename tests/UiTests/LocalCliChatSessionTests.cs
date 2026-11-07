using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// A local CLI session as the chat panel talks to it: a transcript file on disk,
// tailed.
//
// In UiTests because the loads finish on the Avalonia dispatcher — the read
// happens on a worker and the result is posted back — so a test has to pump the
// loop to see it. That is the only reason this is not a unit test; nothing here
// starts a CLI, and the sending half (which goes through tmux) is not touched.
//
// The thing worth understanding before reading any of it, quoting the source:
// there is only one conversation and this is not a copy of it. The file *is* the
// conversation, so what these tests assert is that the panel reads the same thing
// the terminal is writing — including the parts of that file it must not show.
[Collection("Settings")]
public class LocalCliChatSessionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-localcli-" + Guid.NewGuid().ToString("N"));

    public LocalCliChatSessionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Real Claude Code row shapes, trimmed of the fields none of this reads.
    private static string User(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + Json(text) + "}}";

    private static string Assistant(string uuid, string text) =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:01Z\","
        + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":"
        + Json(text) + "}]}}";

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private string Transcript(params string[] rows)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', rows) + "\n");
        return path;
    }

    private static LocalCliChatSession Session(string transcriptPath, string state = "idle") =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            State = state,
            Title = "",
        });

    // --- the attach affordance ----------------------------------------------

    // Whether the panel offers to put this session in a terminal. The rule is
    // ClickRouting's and is covered per case there; what this pins is that the
    // session asks it, with its *own* status — which is the wiring that would
    // fail silently, since a session that always answered false would simply
    // show no button and look like a session that could be typed into.
    [AvaloniaFact]
    public void ABackgroundSessionOffersAnAttachAndSaysWhatItWants()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
            SessionPid = Environment.ProcessId,
            State = "idle",
        });

        Assert.True(session.CanOpenElsewhere);
        Assert.Equal("Needs input — attach to reply", session.ComposerHint);
    }

    // An ordinary session in a tmux pane: nothing to attach, and the box says
    // what it has always said. A button on every panel would be a mark that
    // distinguishes nothing, which is the argument the orb's badges are held to.
    [AvaloniaFact]
    public void AnOrdinarySessionOffersNothingElsewhere()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Shape = LocalSessionShape.Terminal,
            SessionPid = Environment.ProcessId,
            TmuxPane = "%7",
            TmuxSocket = "/tmp/tmux-501/default",
            State = "idle",
        });

        Assert.False(session.CanOpenElsewhere);
    }

    // The read runs on a worker and posts its result back, so the loop has to be
    // pumped until it lands. Bounded rather than a bare spin: a test that hangs
    // tells you far less than one that fails.
    private static void PumpUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static LocalCliChatSession Started(string transcriptPath, string state = "idle")
    {
        var session = Session(transcriptPath, state);
        session.Start();
        PumpUntil(() => session.History.Count > 0 || session.State == RemoteChatState.Connected,
            "the initial load");
        return session;
    }

    // --- reflection into the private plumbing ---
    //
    // Same reasoning as SessionScanTests' OrbIds/DisplayOrder: Watch, Nudge,
    // Pump and Add are private because nothing outside this class should call
    // them directly in production, but the debounce-and-tail machinery they
    // make up is the largest untested surface in this file and none of it
    // needs a dispatcher, a watcher or a CLI to exercise — only the private
    // entry points a real watcher event or timer tick would otherwise reach.
    private static void Invoke(LocalCliChatSession session, string method, params object?[] args) =>
        typeof(LocalCliChatSession)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, args);

    private static void SetField(LocalCliChatSession session, string field, object? value) =>
        typeof(LocalCliChatSession)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session, value);

    private static T GetField<T>(LocalCliChatSession session, string field) =>
        (T)typeof(LocalCliChatSession)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    // --- Start ---

    [AvaloniaFact]
    public void ATranscriptIsReadIntoHistory()
    {
        var session = Started(Transcript(
            User("u1", "fix the arrangement test"),
            Assistant("a1", "Fixed the nested-team case.")));

        Assert.Equal(2, session.History.Count);
        Assert.Equal(ChatRole.User, session.History[0].Role);
        Assert.Equal("fix the arrangement test", session.History[0].Text);
        Assert.Equal(ChatRole.Assistant, session.History[1].Role);
    }

    [AvaloniaFact]
    public void LoadingAnnouncesThatTheWholeTranscriptChanged()
    {
        var session = Session(Transcript(User("u1", "hello")));
        var replaced = 0;
        session.HistoryReplaced += () => replaced++;

        session.Start();
        PumpUntil(() => replaced > 0, "HistoryReplaced");

        Assert.Equal(1, replaced);
    }

    [AvaloniaFact]
    public void ASessionWithATranscriptReportsItselfConnected()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.Equal(RemoteChatState.Connected, session.State);
    }

    // The transcript path is the one field the hook can record later than the
    // rest, so a session whose first status file predates its first message has
    // none — and Start has to be a no-op rather than an error.
    [AvaloniaFact]
    public void ASessionWithNoTranscriptStaysQuiet()
    {
        var session = Session("");

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.History);
        Assert.False(session.HasMore);
        Assert.Equal(RemoteChatState.Connecting, session.State);
    }

    [AvaloniaFact]
    public void APathThatDoesNotExistIsAlsoQuiet()
    {
        var session = Session(Path.Combine(_root, "never-written.jsonl"));

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.History);
    }

    // Idempotent, because it is called from both construction-time binding and
    // every status update. A second Start must not re-read the file and double
    // the history.
    [AvaloniaFact]
    public void StartingTwiceReadsOnce()
    {
        var path = Transcript(User("u1", "one"), Assistant("a1", "two"));
        var session = Started(path);

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, session.History.Count);
    }

    // Rows the panel must not show. These are in every real transcript and each
    // one has its own reason — a tool result is not conversation, a sidechain is
    // a subagent's own transcript, a system reminder is not something anybody
    // said. The parsing is tested exhaustively elsewhere; what this asserts is
    // that the session hands the file to that parser rather than showing raw
    // rows.
    [AvaloniaFact]
    public void RowsThatAreNotConversationDoNotAppear()
    {
        var session = Started(Transcript(
            User("u1", "fix it"),
            "{\"type\":\"user\",\"uuid\":\"m1\",\"isMeta\":true,\"timestamp\":\"2026-08-24T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"hook output\"}}",
            "{\"type\":\"user\",\"uuid\":\"s1\",\"isSidechain\":true,\"timestamp\":\"2026-08-24T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"subagent progress\"}}",
            Assistant("a1", "done")));

        Assert.Equal(2, session.History.Count);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("hook output"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("subagent progress"));
    }

    // A row repeated in the file — which happens, because the tail window can be
    // re-read — is one turn. The uuid is what makes that possible.
    [AvaloniaFact]
    public void ARepeatedRowBecomesOneTurn()
    {
        var row = User("u1", "said once");
        var session = Started(Transcript(row, row, row));

        Assert.Single(session.History);
    }

    // --- paging backwards ---

    [AvaloniaFact]
    public void AShortTranscriptHasNothingOlderToLoad()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.False(session.HasMore);
    }

    [AvaloniaFact]
    public async Task AskingForOlderTurnsWhenThereAreNoneAnswersFalse()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.False(await session.LoadOlderAsync(CancellationToken.None));
    }

    // A transcript longer than the opening window opens on its tail, and paging
    // back reaches the rest. The rows are padded so the file genuinely exceeds
    // the 512KB the session opens with, because the whole point of the window is
    // that a real transcript is far larger than what it shows.
    [AvaloniaFact]
    public async Task ALongTranscriptOpensOnItsTailAndPagesBack()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200)
            .Select(i => User("u" + i, $"message {i} {padding}"))
            .ToArray();

        var session = Started(Transcript(rows));

        var opened = session.History.Count;
        Assert.True(opened > 0, "the tail should hold something");
        Assert.True(opened < rows.Length, $"the whole file should not fit: {opened} of {rows.Length}");
        Assert.True(session.HasMore, "there should be more to page back to");

        var prepended = 0;
        session.HistoryPrepended += n => prepended = n;

        Assert.True(await session.LoadOlderAsync(CancellationToken.None));

        Assert.True(prepended > 0, "paging back should report how many arrived");
        Assert.True(session.History.Count > opened, "paging back should add turns");

        // ...and the oldest turn on screen is older than it was, which is the
        // property a user actually notices.
        Assert.StartsWith("message ", session.History[0].Text);
    }

    // Paging back repeatedly reaches the beginning and then stops claiming there
    // is more — otherwise the panel offers a "load older" that never ends.
    [AvaloniaFact]
    public async Task PagingBackEventuallyReachesTheBeginning()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200)
            .Select(i => User("u" + i, $"message {i} {padding}"))
            .ToArray();

        var session = Started(Transcript(rows));

        for (var i = 0; i < 40 && session.HasMore; i++)
        {
            await session.LoadOlderAsync(CancellationToken.None);
        }

        Assert.False(session.HasMore);
        Assert.Contains(session.History, t => t.Text.StartsWith("message 0 ", StringComparison.Ordinal));
    }

    // --- status updates ---

    // The title can improve after the panel opened: Claude Code writes an
    // ai-title for a conversation that did not have one yet, and a panel opened
    // before that would otherwise keep the folder name in its header for as long
    // as the app runs.
    [AvaloniaFact]
    public void ALaterTitleImprovesTheDisplayName()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = path,
            Title = "Fixing the arrangement",
        });

        Assert.Equal("Fixing the arrangement", session.DisplayName);
    }

    // ...and a status that has lost its title does not blank the one already
    // shown. An empty title is "not known yet", not "called nothing".
    [AvaloniaFact]
    public void AStatusWithNoTitleDoesNotBlankTheOneAlreadyKnown()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, Title = "Named",
        });
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, Title = "",
        });

        Assert.Equal("Named", session.DisplayName);
    }

    // A transcript path arriving late is the case Start is idempotent for: the
    // session was bound before the hook had written one, and the next status
    // update is what gets it reading.
    [AvaloniaFact]
    public void ATranscriptPathArrivingLateStartsTheSession()
    {
        var session = Session("");
        session.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(session.History);

        var path = Transcript(User("u1", "arrived late"));
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path,
        });

        PumpUntil(() => session.History.Count > 0, "the late load");

        Assert.Single(session.History);
    }

    // --- the composer ---

    // "No terminal to type into" wins over the reply setting, and the precedence is
    // the interesting half: a session with nowhere to send has to say *that*
    // rather than "Replying is off", because the two have different answers —
    // one is a setting the user can change and the other is not.
    //
    // The reply-setting branch below it is deliberately not asserted here. It is
    // only reachable once the session can send quietly, which needs a real tmux
    // binary on the machine — so a test of it would pass on a developer's Mac and
    // do nothing on a runner without tmux, which is the same test passing for two
    // different reasons. The setting's own behaviour is covered where it is
    // decided, in the settings suite.
    [AvaloniaFact]
    public void ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting()
    {
        var session = Session(Transcript(User("u1", "hello")));

        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
        Assert.Equal("No terminal to type into", session.ComposerHint);

        OrbweaverSettings.ClaudeCodeReplyEnabled = false;
        Assert.Equal("No terminal to type into", session.ComposerHint);
    }

    // Scanned once per session rather than per keystroke, since the commands a
    // running CLI understands do not change while it runs — so asking twice must
    // give the same list rather than re-reading the disk.
    [AvaloniaFact]
    public void SlashCommandsAreScannedOnce()
    {
        var session = Session(Transcript(User("u1", "hello")));

        var first = session.SlashCommands;

        Assert.Same(first, session.SlashCommands);
    }

    [AvaloniaFact]
    public void DisposingASessionThatNeverStartedIsHarmless()
    {
        var session = Session("");

        session.Dispose();

        Assert.Empty(session.History);
    }

    [AvaloniaFact]
    public void DisposingAStartedSessionStopsItCleanly()
    {
        var session = Started(Transcript(User("u1", "hello")));

        session.Dispose();
        Dispatcher.UIThread.RunJobs();

        // The history it already read stays readable; disposing stops the
        // watcher, it does not empty the panel.
        Assert.Single(session.History);
    }

    // --- the permission prompt ---
    //
    // A prompt is the panel offering buttons that send keystrokes into a live
    // session, so a stale one is not a cosmetic problem: it is a button that
    // presses something for a dialog that has already been answered. Finding a
    // prompt means capturing a tmux pane and is excluded; the transitions around
    // one are decided here and are not.

    // Leaving "waiting" clears the prompt. Without this the buttons stay on
    // screen after the dialog is gone.
    [AvaloniaFact]
    public void LeavingTheWaitingStateClearsThePrompt()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        session.SetPrompt(new ChatPrompt("Do you want to proceed?", new[]
        {
            new ChatPromptOption("1", "Yes"),
            new ChatPromptOption("2", "No"),
        }));
        Assert.NotNull(session.Prompt);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
        });

        Assert.Null(session.Prompt);
    }

    // ...and says so, because the panel has to take the buttons down rather than
    // wait for something else to happen.
    [AvaloniaFact]
    public void ClearingThePromptIsAnnounced()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        var changes = 0;
        session.PromptChanged += () => changes++;

        session.SetPrompt(new ChatPrompt("Proceed?", Array.Empty<ChatPromptOption>()));
        Dispatcher.UIThread.RunJobs();
        var afterSetting = changes;

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
        });
        Dispatcher.UIThread.RunJobs();

        Assert.True(changes > afterSetting, "clearing a prompt has to be announced");
    }

    // A status update that was not waiting and still is not raises nothing. The
    // scan runs a couple of times a second, so an update per tick would be an
    // event per tick for a panel with nothing to change.
    [AvaloniaFact]
    public void StayingNotWaitingRaisesNothing()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        Dispatcher.UIThread.RunJobs();
        var changes = 0;
        session.PromptChanged += () => changes++;

        for (var i = 0; i < 3; i++)
        {
            session.UpdateStatus(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
            });
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, changes);
    }

    // A prompt already on screen survives another "waiting" update. Claude Code
    // commonly asks two or three permissions in a row and the state never leaves
    // "waiting" between them, so an update that cleared or re-read on every tick
    // would flicker the buttons under the pointer.
    [AvaloniaFact]
    public void APromptSurvivesAnotherWaitingUpdate()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        var prompt = new ChatPrompt("Do you want to proceed?", new[]
        {
            new ChatPromptOption("1", "Yes"),
        });
        session.SetPrompt(prompt);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "waiting",
        });

        Assert.Same(prompt, session.Prompt);
    }

    // Answering is refused when replying is off, and refused *out loud* — the
    // panel says so in the transcript rather than a button doing nothing. A send
    // that silently fails is the worst outcome a chat window can produce.
    [AvaloniaFact]
    public async Task AnsweringWithReplyingOffSaysSoInsteadOfDoingNothing()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        OrbweaverSettings.ClaudeCodeReplyEnabled = false;

        var before = session.History.Count;
        await session.AnswerAsync(new ChatPromptOption("1", "Yes"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(session.History.Count > before, "a refusal has to be visible");
        Assert.Contains(
            session.History,
            turn => turn.Text.Contains("Replying is off", StringComparison.OrdinalIgnoreCase));
    }

    // --- watching and the live tail ---
    //
    // Nudge, OnDebounce and Pump are private because nothing outside this class
    // should call them directly — a real FileSystemWatcher event or the 2s poll
    // timer is what calls them in production — but the debounce-and-tail
    // machinery they make up needs none of that to exercise: it is bytes on
    // disk and a dispatcher, same as LoadInitialAsync above. Invoked here
    // through reflection for the same reason OrbIds and DisplayOrder are
    // reflected into in SessionScanTests.

    // Watch() is only ever reached from Start() with a path already confirmed
    // to exist, so provoking the FileSystemWatcher constructor to throw for
    // real needs a lower-level nudge than Start offers: point _transcriptPath
    // at a directory that was never created and call the private method
    // directly. "A watcher is an optimisation over the poll below, not a
    // requirement" is the claim the source comment makes; this proves the
    // catch does not also take the poll timer down with it.
    [AvaloniaFact]
    public void AWatcherThatCannotBeCreatedStillLeavesThePollRunning()
    {
        var session = Session("");
        SetField(session, "_transcriptPath", Path.Combine(_root, "never-created", "gone.jsonl"));

        Invoke(session, "Watch");

        Assert.NotNull(GetField<object?>(session, "_poll"));
    }

    // --- CB-234: turning the watcher on must not hold anything up ---------------
    //
    // On macOS StartRaisingEvents is a native FSEvents stream, and under load it
    // has been seen never to return (a UiTests host at 0% CPU for hours, three
    // times in a day). Watch() runs on the UI thread, so the claim worth pinning
    // is not "the watcher starts" but "nothing waits for it".

    private sealed class RaisableWatcher : FileSystemWatcher
    {
        public RaisableWatcher(string dir, string name) : base(dir, name) { }
        public void RaiseChanged() =>
            OnChanged(new FileSystemEventArgs(WatcherChangeTypes.Changed, Path, "x"));
    }

    private static LocalCliChatSession SessionWithWatcher(
        string transcriptPath, Func<string, string, FileSystemWatcher> startWatcher) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            State = "idle",
            Title = "",
        }, startWatcher: startWatcher);

    // The negative control is built in. A start that never returns is forced
    // through the seam; a watchdog releases it after ten seconds and records that
    // it had to. Before the fix Start() sat inside that call, so it came back
    // only when the watchdog let it, and the assertion below fails with the
    // reason. After it, Start() returns at once and the watchdog never fires.
    // The ten seconds detects a hang -- it is not a tolerance anything waits on.
    [AvaloniaFact]
    public void AWatcherThatNeverStartsDoesNotHoldUpStart()
    {
        var never = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var watchdogFired = false;
        using var watchdog = new Timer(_ => { watchdogFired = true; never.Set(); }, null,
            TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        var session = SessionWithWatcher(Transcript(User("u1", "hello")), (dir, name) =>
        {
            entered.Set();
            never.Wait();
            throw new IOException("released by the test");
        });

        session.Start();

        Assert.False(watchdogFired, "Start() waited on the watcher's start instead of returning.");
        // The poll -- the real backstop -- is already running, with the watcher still stuck.
        var poll = GetField<DispatcherTimer?>(session, "_poll");
        Assert.NotNull(poll);
        Assert.True(poll!.IsEnabled);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the watcher start was never attempted");

        never.Set();
        session.WatcherStarter!.Join();
        session.Dispose();
    }

    // A start that fails outright: the poll is untouched and nothing is held.
    [AvaloniaFact]
    public void AWatcherStartThatThrowsLeavesThePollAndNoWatcher()
    {
        var session = SessionWithWatcher(Transcript(User("u1", "hello")),
            (dir, name) => throw new IOException("no fsevents today"));

        session.Start();
        session.WatcherStarter!.Join();

        Assert.True(GetField<DispatcherTimer?>(session, "_poll")!.IsEnabled);
        Assert.Null(session.Watcher);
        session.Dispose();
    }

    // A start that succeeds is kept, and its Changed event reaches Nudge on the UI
    // thread -- the wiring the old inline code had, now made on the other thread.
    [AvaloniaFact]
    public void ASuccessfulWatcherIsKeptAndItsEventsNudgeTheDebounce()
    {
        RaisableWatcher? made = null;
        var session = SessionWithWatcher(Transcript(User("u1", "hello")), (dir, name) =>
            made = new RaisableWatcher(dir, name));

        session.Start();
        session.WatcherStarter!.Join();

        Assert.Same(made, session.Watcher);
        made!.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.True(GetField<DispatcherTimer?>(session, "_debounce")!.IsEnabled);
        session.Dispose();
    }

    // Disposed while the start was still in flight: whoever finishes it finds no
    // owner, so it disposes the watcher rather than leaking a live FSEvents stream.
    [AvaloniaFact]
    public void AWatcherThatFinishesStartingAfterDisposeIsDisposed()
    {
        var release = new ManualResetEventSlim(false);
        RaisableWatcher? made = null;
        var session = SessionWithWatcher(Transcript(User("u1", "hello")), (dir, name) =>
        {
            release.Wait();
            return made = new RaisableWatcher(dir, name);
        });

        session.Start();
        var starter = session.WatcherStarter!;
        session.Dispose();
        release.Set();
        starter.Join();

        Assert.Null(session.Watcher);
        Assert.Throws<ObjectDisposedException>(() => made!.EnableRaisingEvents = true);
    }

    // Read before Start() has run there is no watcher and no thread to join: the
    // accessors are null-safe rather than assuming Watch() got there first.
    [AvaloniaFact]
    public void ASessionThatHasNotStartedHasNoWatcherAndNoStarter()
    {
        var session = Session(Transcript(User("u1", "hello")));

        Assert.Null(session.WatcherStarter);
        Assert.Null(session.Watcher);
        session.Dispose();
    }

    // The half of the real start that is safe to call from a test. Turning the
    // watcher on is deliberately not called here: on macOS that runs the
    // kernel's global sync(2) and can take minutes on a loaded machine, which
    // is the dependence on the OS's timing this ticket removes. It runs on the
    // background thread in every test that takes the default seam.
    [AvaloniaFact]
    public void TheRealWatcherIsBuiltForTheFileAndNotYetOn()
    {
        using var watcher = LocalCliChatSession.CreateWatcher(_root, "x.jsonl");

        Assert.False(watcher.EnableRaisingEvents);
        Assert.Equal("x.jsonl", watcher.Filter);
        Assert.Equal(NotifyFilters.LastWrite | NotifyFilters.Size, watcher.NotifyFilter);
    }

    // A real watcher's Changed event does exactly this: post Nudge to the UI
    // thread, which (re)starts a 150ms debounce that calls Pump once it
    // fires. The real DispatcherTimer that (re)start drives is not something
    // a headless test can wait out reliably — there is no virtual clock to
    // advance — so Nudge and the timer's own Tick handler are proved
    // separately: this covers what Nudge itself does (create, or reuse, a
    // stopped-then-restarted timer), and OnDebounce below covers what firing
    // it does.
    [AvaloniaFact]
    public void NudgeStartsADebounceTimerReadyToFire()
    {
        var session = Session(Transcript(User("u1", "hello")));

        Invoke(session, "Nudge");

        var debounce = GetField<DispatcherTimer?>(session, "_debounce");
        Assert.NotNull(debounce);
        Assert.True(debounce!.IsEnabled);
    }

    // Nudged again before it fires, the same timer is reused rather than a
    // second one being created — `_debounce ??=` is the whole of that — and
    // it stays enabled rather than compounding into several pending fires.
    [AvaloniaFact]
    public void RepeatedNudgesReuseTheSameTimerInstance()
    {
        var session = Session(Transcript(User("u1", "hello")));

        Invoke(session, "Nudge");
        var first = GetField<DispatcherTimer?>(session, "_debounce");

        Invoke(session, "Nudge");
        Invoke(session, "Nudge");
        var second = GetField<DispatcherTimer?>(session, "_debounce");

        Assert.Same(first, second);
        Assert.True(second!.IsEnabled);
    }

    // OnDebounce is the timer's own Tick handler — invoked here exactly as a
    // real fire would call it, with the sender and event args a DispatcherTimer
    // hands its subscribers — and what it does is stop the timer and pump
    // whatever Nudge was raised for.
    [AvaloniaFact]
    public void OnDebounceStopsTheTimerAndPumpsWhatWasAppended()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        File.AppendAllText(path, Assistant("a2", "appended live") + "\n");

        Invoke(session, "Nudge");
        Invoke(session, "OnDebounce", null, EventArgs.Empty);

        Assert.False(GetField<DispatcherTimer?>(session, "_debounce")!.IsEnabled);

        PumpUntil(() => session.History.Count > 1, "the pump OnDebounce kicked off");
        Assert.Equal("appended live", session.History[^1].Text);
    }

    // The file replaced case: /clear starts a new transcript, which can be
    // shorter than wherever the tail had already read to. Carrying the old
    // offset would read from the middle of an unrelated row forever, so Pump
    // notices the file is now shorter than its offset and starts over from
    // zero.
    [AvaloniaFact]
    public void ATranscriptShorterThanTheLastOffsetIsReadFromTheStart()
    {
        var path = Transcript(Assistant("u1", "before clear " + new string('y', 200)));
        var session = Started(path);

        File.WriteAllText(path, User("u2", "after clear") + "\n");

        Invoke(session, "Pump");
        PumpUntil(() => session.History.Any(t => t.Text == "after clear"), "the restarted read");
    }

    // Mid-write or gone: Pump's own catch, provoked here by holding the file
    // open with no sharing at all so the FileStream it tries to open throws.
    // The offset must stay exactly where it was — "the poll comes back in two
    // seconds" is the design, and moving the offset on a failed read would
    // skip whatever that read was supposed to cover.
    [AvaloniaFact]
    public void APumpThatCannotOpenTheFileLeavesTheOffsetUntouched()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);
        var offsetBefore = GetField<long>(session, "_offset");

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Invoke(session, "Pump");
            PumpUntil(() => !GetField<bool>(session, "_pumping"), "the failed pump to finish");
        }

        Assert.Equal(offsetBefore, GetField<long>(session, "_offset"));
        Assert.Single(session.History);
    }

    // LoadInitialAsync's own catch: the transcript exists but cannot be
    // opened, which reports a session with nothing in it rather than leaving
    // the panel stuck on "Connecting…" forever.
    [AvaloniaFact]
    public void ATranscriptThatCannotBeOpenedForTheInitialReadStaysConnectedWithNoHistory()
    {
        var path = Transcript(User("u1", "hello"));

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var session = Session(path);
            session.Start();
            PumpUntil(() => session.State == RemoteChatState.Connected, "the failed initial load");

            Assert.Empty(session.History);
            Assert.False(session.HasMore);
        }
    }

    // LoadOlderAsync's own catch, the other side of the same guard: a page
    // that cannot be read reports no progress rather than throwing, so the
    // panel's "load older" can be tried again instead of crashing the click.
    [AvaloniaFact]
    public async Task PagingOlderWhenTheFileCannotBeOpenedReportsNoProgress()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200).Select(i => User("u" + i, $"message {i} {padding}")).ToArray();
        var path = Transcript(rows);
        var session = Started(path);
        Assert.True(session.HasMore);

        var before = session.History.Count;

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await session.LoadOlderAsync(CancellationToken.None));
        }

        Assert.Equal(before, session.History.Count);
    }

    // --- reconciling a sent message against the row it produces ---
    //
    // SendCoreAsync itself needs a real tmux pane to reach — CanSendQuietly is
    // false without one, which is the same environment dependency
    // ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting declines to test for
    // above. Reconcile's own branches do not: they only need _pending and its
    // two candidate texts set, which is what a successful send would have left
    // behind, set here directly.

    private static void SetPending(
        LocalCliChatSession session, ChatTurn? turn, string raw, string caption, DateTimeOffset at)
    {
        SetField(session, "_pending", turn);
        SetField(session, "_pendingRaw", raw);
        SetField(session, "_pendingCaption", caption);
        SetField(session, "_pendingAt", at);
    }

    [AvaloniaFact]
    public void APendingSendOlderThanTwoMinutesIsNotReconciledAgainst()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "typed this", "typed this", DateTimeOffset.Now - TimeSpan.FromMinutes(3));

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", incoming);

        // Neither turn is discarded: a stale pending send must not swallow an
        // identical message sent again later.
        Assert.Equal(2, session.History.Count(t => t.Text == "typed this"));
        Assert.Null(GetField<ChatTurn?>(session, "_pending"));
    }

    // The pending turn is itself passed through Add on the way in for a plain
    // send, and must not reconcile against itself — "SendAsync orders things
    // so this cannot happen" is the claim in the source comment; this is the
    // belt to that braces, tested by breaking the ordering on purpose.
    [AvaloniaFact]
    public void APendingTurnDoesNotReconcileAgainstItself()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var turn = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        SetPending(session, turn, "typed this", "typed this", DateTimeOffset.Now);

        Invoke(session, "Add", turn);

        Assert.Single(session.History);
        Assert.NotNull(GetField<ChatTurn?>(session, "_pending"));
    }

    [AvaloniaFact]
    public void AnAssistantTurnNeverSettlesAUsersPendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "typed this", "typed this", DateTimeOffset.Now);

        var incoming = new ChatTurn { Role = ChatRole.Assistant, Text = "typed this" };
        Invoke(session, "Add", incoming);

        Assert.NotNull(GetField<ChatTurn?>(session, "_pending"));
        Assert.Contains(session.History, t => ReferenceEquals(t, incoming));
    }

    [AvaloniaFact]
    public void AUserTurnWithDifferentTextDoesNotSettleThePendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "typed this", "typed this", DateTimeOffset.Now);

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "something else entirely" };
        Invoke(session, "Add", incoming);

        Assert.NotNull(GetField<ChatTurn?>(session, "_pending"));
        Assert.Contains(session.History, t => ReferenceEquals(t, incoming));
    }

    // The row that comes back adopts the turn already on screen rather than
    // adding a second — matched here on the raw typed text, which is what
    // comes back verbatim when the CLI never noticed a pasted image path.
    [AvaloniaFact]
    public void ARowMatchingTheRawTypedTextSettlesThePendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "caption only" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "caption only /tmp/pic.png", "caption only", DateTimeOffset.Now);

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "caption only /tmp/pic.png" };
        Invoke(session, "Add", incoming);

        Assert.Equal(1, updated);
        Assert.Equal("caption only /tmp/pic.png", pending.Text);
        Assert.DoesNotContain(session.History, t => ReferenceEquals(t, incoming));
        Assert.Null(GetField<ChatTurn?>(session, "_pending"));
    }

    // ...and a row matching the caption alone settles it too — the other of
    // the two shapes the transcript can hand back, when the CLI did notice the
    // path and swapped it for a real picture.
    [AvaloniaFact]
    public void ARowMatchingTheCaptionAloneAlsoSettlesThePendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "caption only" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "caption only /tmp/pic.png", "caption only", DateTimeOffset.Now);

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "caption only" };
        Invoke(session, "Add", incoming);

        Assert.Equal(1, updated);
        Assert.DoesNotContain(session.History, t => ReferenceEquals(t, incoming));
        Assert.Null(GetField<ChatTurn?>(session, "_pending"));
    }

    // --- sending: the two early refusals that need no tmux at all ---
    //
    // The success path below them needs a real tmux pane, for the same
    // environment-dependency reason ComposerHint's "Message…" branch is left
    // untested — see the comment on ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting.

    [AvaloniaFact]
    public async Task SendingWhileReplyingIsOffLeavesANoteInsteadOfTyping()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi")));
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;

            var outcome = await session.SendAsync("hello there");

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));

            // CB-35: this is the return value ChatPanel.Send() reads to
            // decide whether to keep the typed text in the composer.
            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    [AvaloniaFact]
    public async Task SendingWithNoTmuxPaneExplainsThereIsNowhereToTypeQuietly()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi"))); // TmuxPane empty by construction
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;

            var outcome = await session.SendAsync("hello there");

            Assert.Contains(
                session.History,
                t => t.Text.Contains(TerminalTyping.CantTypePhrase, StringComparison.Ordinal));

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // SendWithImagesAsync with no images at all is just SendAsync — the same
    // refusal, reached through the other entry point.
    [AvaloniaFact]
    public async Task SendingWithNoImagesFallsBackToThePlainSend()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi")));
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;

            var outcome = await session.SendWithImagesAsync("hello", Array.Empty<string>());

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));

            // CB-35: the image-send path reports the same outcome type as
            // the plain one, which is what lets ChatPanel.Send() treat both
            // entry points the same way when deciding what to do with the
            // composer.
            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // With an image path, the caption is what SendCoreAsync sees as the
    // display text even though replying is off and it never gets past the
    // first check — proving the caption/path assembly happens before that
    // gate is asked, not that anything reaches tmux.
    [AvaloniaFact]
    public async Task SendingWithAnImagePathStillRefusesQuietlyWhenReplyingIsOff()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var imagePath = Path.Combine(_root, "pic.png");
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });

            var session = Session(Transcript(User("u1", "hi")));
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;

            await session.SendWithImagesAsync("a caption", new[] { imagePath });

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // A picture that vanished between being dropped and being sent — deleted,
    // or never really there — must not fail the send. "The file is still on
    // disk and the terminal still gets its path" is the claim for the normal
    // case; this is the one where that is not even true, and the send still
    // has to proceed rather than throwing out of SendWithImagesAsync.
    [AvaloniaFact]
    public async Task AMissingImageFileDoesNotStopTheSendFromProceeding()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var missingPath = Path.Combine(_root, "never-written.png");

            var session = Session(Transcript(User("u1", "hi")));
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;

            await session.SendWithImagesAsync("a caption", new[] { missingPath });

            // Still reaches SendCoreAsync and reports the same refusal a plain
            // send would — the missing thumbnail cost nothing but itself.
            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // --- cancelling and stepping away to the terminal ---

    [AvaloniaFact]
    public void CancellingDoesNothingWhenReplyingIsOff()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi")));
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;

            session.Cancel(); // must not throw, and must not touch TerminalFocuser at all
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    [AvaloniaFact]
    public void CancellingWithReplyingOnButNoPaneSendsNothing()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi"))); // TmuxPane empty
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;

            // SendPaneKey short-circuits on CanSendQuietly before it would
            // otherwise touch tmux, exactly like the composer's own checks.
            session.Cancel();
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // Answering with nowhere to send the keystroke reports the failure and,
    // since the session is still waiting, asks the terminal again for what is
    // on screen — which, with no tmux pane to capture, comes back as an honest
    // "something is waiting and I can't say what" rather than silence.
    [AvaloniaFact]
    public async Task AnsweringWithNoPaneExplainsAndAsksAgainWhileStillWaiting()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            var path = Transcript(User("u1", "hello"));
            var session = Started(path, state: "waiting");
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;

            session.SetPrompt(new ChatPrompt("Proceed?", new[] { new ChatPromptOption("1", "Yes") }));

            await session.AnswerAsync(new ChatPromptOption("1", "Yes"));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Couldn't answer that in the terminal", StringComparison.Ordinal));
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // AnswerElsewhere hands off to TerminalFocuser.Focus, which is out of
    // bounds for a headless test to actually reach — so this uses a
    // non-local-CLI status, which makes Focus's own first check return before
    // anything resembling tmux, ps or osascript runs. What this proves is that
    // AnswerElsewhere wires the call through at all, not what Focus then does.
    [AvaloniaFact]
    public void AnswerElsewhereCallsFocusWithoutReachingTerminalMachinery()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus { Source = SessionSource.OpenClaw });

        session.AnswerElsewhere();
    }

    // --- a recorded transcript path that is wrong, not just late ---------------

    // The daemon respawns a finished job's worker from the job's original
    // directory, so the hook records a transcript_path computed from that
    // directory while the conversation lives in the projects directory keyed by
    // where the session actually ran. Job b0633b77 on a real machine: 3.6MB of
    // transcript, and a panel that opened blank forever, because Start treated
    // "recorded but missing" as final and never hunted.
    [AvaloniaFact]
    public void AMissingRecordedPathFallsBackToTheHunt()
    {
        var real = Transcript(User("u1", "why is this orb chat blank?"),
                              Assistant("a1", "Found it — the recorded path was wrong."));
        var recorded = Path.Combine(_root, "never-written-here.jsonl");

        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = recorded,
            State = "idle",
            Title = "",
        }, findTranscript: id => id == "s1" ? real : null);

        session.Start();
        PumpUntil(() => session.History.Count > 0, "the hunted transcript to load");

        Assert.Contains(session.History,
            t => t.Text.Contains("the recorded path was wrong", StringComparison.Ordinal));
    }

    // The other direction: a path that exists is never second-guessed. A hunt
    // that ran anyway could shadow the hook's own record with a stale sibling's
    // file, which is a worse wrong than the one being fixed.
    [AvaloniaFact]
    public void ARecordedPathThatExistsIsNeverHuntedPast()
    {
        var real = Transcript(User("u1", "hello"), Assistant("a1", "hi"));
        var hunted = false;

        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = real,
            State = "idle",
            Title = "",
        }, findTranscript: _ => { hunted = true; return null; });

        session.Start();
        PumpUntil(() => session.History.Count > 0, "the recorded transcript to load");

        Assert.False(hunted);
    }

    // And when the hunt finds nothing either, Start stays unstarted rather than
    // wedging: the next status update asks again, which is how a transcript
    // that appears late has always been picked up.
    [AvaloniaFact]
    public void AHuntThatFindsNothingLeavesStartRetryable()
    {
        var recorded = Path.Combine(_root, "never-written-here.jsonl");
        var hunts = 0;

        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = recorded,
            State = "idle",
            Title = "",
        }, findTranscript: _ => { hunts++; return null; });

        session.Start();
        session.Start();

        Assert.Equal(2, hunts);
        Assert.Empty(session.History);
    }

    // --- CB-105: delivering to a session with no pane, over its own registry socket ---
    //
    // SendCoreAsync's tmux path needs a real tmux binary and a real pane and
    // cannot be faked — see the comment on TypeIntoTerminalAsync. The
    // messenger path is different by design: both the SessionMessenger and
    // the registry lookup are seams on the constructor, so the whole path can
    // be driven for real against a fake Seams, the same pattern
    // SessionMessengerTests already uses for the messenger alone.
    //
    // findRegistryEntry and messengerEntries are deliberately separate rather
    // than one list handed to both: CanDeliver's own lookup (findRegistry) and
    // DeliverAsync's (the messenger's own Seams.Registry) are two different
    // reads of the same real registry in production, taken a moment apart —
    // the far session can exit, or stop speaking a protocol this build knows,
    // between the composer offering to deliver and the send actually landing.
    // Letting the two diverge in a test is what makes NoRegistryEntry and
    // UnsupportedProtocol reachable at all: both need CanDeliver to have said
    // yes a moment ago, and the messenger's own read to disagree now.
    private static SessionRegistry.Entry RegistryEntry(
        string sessionId, int peerProtocol = 1, string? status = "idle") =>
        SessionRegistry.Parse(
            $$"""
            {"pid":4242,"sessionId":"{{sessionId}}","messagingSocketPath":"/tmp/a.sock",
             "peerProtocol":{{peerProtocol}},"status":{{(status is null ? "null" : $"\"{status}\"")}}}
            """, keyPath: null)!.Value;

    private static (SessionMessenger Messenger, Func<string, SessionRegistry.Entry?> FindRegistry) FakeMessaging(
        SessionRegistry.Entry? findRegistryEntry,
        SessionRegistry.Entry[]? messengerEntries = null,
        bool write = true)
    {
        var forMessenger = messengerEntries
            ?? (findRegistryEntry is { } e ? new[] { e } : Array.Empty<SessionRegistry.Entry>());

        var messenger = new SessionMessenger(new SessionMessenger.Seams(
            Registry: () => forMessenger,
            PidAlive: _ => true,
            ReadKey: _ => null,
            Write: (_, _, _) => Task.FromResult(write)));

        Func<string, SessionRegistry.Entry?> find = id =>
            findRegistryEntry is { } found && found.SessionId == id ? found : null;

        return (messenger, find);
    }

    private static LocalCliChatSession BackgroundSession(
        string transcriptPath, SessionMessenger messenger, Func<string, SessionRegistry.Entry?> findRegistry,
        OrbPresence presence = OrbPresence.NeedsInput) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            Shape = LocalSessionShape.Background,
            Presence = presence,
            State = "idle",
            Title = "job-hunter",
        }, messenger: messenger, findRegistry: findRegistry);

    // The composer hint before anything is typed: a `claude bg-spare` worker
    // with a live, speaking registry entry gets the new wording rather than
    // the daemon-attach one, because there is now somewhere for a message to
    // go even though there is still no pane.
    [AvaloniaFact]
    public void ADeliverableBackgroundSessionOffersToBeMessaged()
    {
        var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
        var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

        Assert.Equal("Message it — it reads this at its next turn", session.ComposerHint);
    }

    // Slash commands don't survive a delivered message — see
    // LocalSlashChannel — so on a messaging channel with no bridge the session
    // still *knows* the whole catalogue (so a typed one is refused, not sent as
    // prose) but every command is Unavailable and none is offered.
    [AvaloniaFact]
    public void NoSlashCommandIsOfferedWhenTheChannelIsMessaging()
    {
        var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
        var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

        Assert.NotEmpty(session.SlashCommands);
        Assert.All(session.SlashCommands, c => Assert.Equal(SlashRouteKind.Unavailable, c.Route));
        Assert.Empty(ChatPanel.OfferedCommands(session.SlashCommands));
    }

    // Sending it for real: the note left afterwards is DeliveryNote's own
    // Accepted wording, naming the session by its display name.
    //
    // Replying has to be turned on for any of the send tests below — the
    // first check SendCoreAsync makes, ahead of CanSendQuietly or CanDeliver —
    // and is restored afterwards, the same pattern every other send test in
    // this file already follows.
    [AvaloniaFact]
    public async Task SendingToADeliverableBackgroundSessionNotesItWasHandedOver()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            var outcome = await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("Handed to job-hunter", StringComparison.Ordinal));

            // CB-35: Accepted is the one receipt that means the socket took
            // the bytes — see DeliverViaMessengerAsync's own comment — and
            // it is the only one of the four delivery tests in this file
            // that reports Sent rather than Failed.
            Assert.Equal(ChatSendOutcome.Sent, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The row Claude Code eventually writes back is the whole
    // <cross-session-message> tag it was handed, not the bare text — so this
    // proves the send settles against that row rather than leaving the
    // message pending forever and showing it a second time when the row
    // arrives.
    [AvaloniaFact]
    public async Task ADeliveredMessageSettlesWhenTheWrappedRowArrivesInTheTranscript()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            await session.SendAsync("check the deploy");
            var beforeEcho = session.History.Count;

            var updated = 0;
            session.TurnUpdated += _ => updated++;

            var wrapped = "<cross-session-message from=\"Claude Buddy on mini\" from-mode=\"prompting\">\n"
                          + "check the deploy\n</cross-session-message>";
            var incoming = new ChatTurn { Role = ChatRole.User, Text = wrapped };
            Invoke(session, "Add", incoming);

            Assert.Equal(1, updated);
            Assert.Equal(beforeEcho, session.History.Count);
            Assert.DoesNotContain(session.History, t => ReferenceEquals(t, incoming));
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The far session is no longer in the registry at all by the time the
    // send actually runs — it may have exited a moment after the composer
    // offered to deliver — and the note points at the one thing that still
    // works: attaching it.
    [AvaloniaFact]
    public async Task SendingWhenTheFarSessionHasLeftTheRegistrySaysSo()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(
                findRegistryEntry: RegistryEntry("s1"), messengerEntries: Array.Empty<SessionRegistry.Entry>());
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            var outcome = await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("isn't registered", StringComparison.Ordinal));

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The far session now speaks a peer protocol this build doesn't
    // recognize — newer than what CanDeliver saw a moment ago.
    [AvaloniaFact]
    public async Task SendingWhenTheFarSessionSpeaksAnUnsupportedProtocolSaysSo()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(
                findRegistryEntry: RegistryEntry("s1"),
                messengerEntries: new[] { RegistryEntry("s1", peerProtocol: 2) });
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            var outcome = await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("peer protocol", StringComparison.Ordinal));

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The socket itself refuses the connection — nothing session-specific
    // learned, so the note is the generic one rather than naming the session.
    [AvaloniaFact]
    public async Task SendingWhenTheSocketRefusesSaysNothingWasSent()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(RegistryEntry("s1"), write: false);
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            var outcome = await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("refused the connection", StringComparison.Ordinal));

            Assert.Equal(ChatSendOutcome.Failed, outcome);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // --- CB-307 addendum: never type into a dialog -------------------------------

    // ShadowOn is pinned off unless a test says otherwise, so these never depend
    // on a marker file or variable left on the machine running them.
    private static LocalCliChatSession Gated(
        Func<SessionStatus, Task<TypingVerdict>> gate, bool shadow = false) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "",
            TmuxPane = "%42",
        })
        { Gate = gate, ShadowOn = () => shadow };

    [AvaloniaFact]
    public async Task ASendIsRefusedWithTheReasonAndNothingIsTyped()
    {
        SessionStatus? asked = null;
        var session = Gated(status =>
        {
            asked = status;
            return Task.FromResult(new TypingVerdict(false, TypingGate.DialogWhy("Permissions")));
        });

        var outcome = await session.RefuseIfNotAtPromptAsync();

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.NotNull(asked);

        // The note names the dialog and says nothing was typed, under the
        // composer, where the user's own text is still sitting.
        var note = Assert.Single(session.History).Text;
        Assert.Contains("isn't at its prompt", note, StringComparison.Ordinal);
        Assert.Contains("it is showing a dialog: Permissions", note, StringComparison.Ordinal);
        Assert.Contains("Nothing was typed", note, StringComparison.Ordinal);
    }

    // The control: at a prompt the gate says yes, nothing is noted, and the
    // caller goes on to type.
    [AvaloniaFact]
    public async Task ASendAtAPromptIsNotRefusedAndLeavesNoNote()
    {
        var calls = 0;
        var session = Gated(_ => { calls++; return Task.FromResult(TypingVerdict.Allow); });

        Assert.Null(await session.RefuseIfNotAtPromptAsync());
        Assert.Equal(1, calls);
        Assert.Empty(session.History);
    }

    [AvaloniaFact]
    public async Task ARefusalWithNoReasonStillExplainsItself()
    {
        var session = Gated(_ => Task.FromResult(new TypingVerdict(false, null)));

        Assert.Equal(ChatSendOutcome.Failed, await session.RefuseIfNotAtPromptAsync());
        Assert.Equal(
            "This terminal isn't at its prompt. Nothing was typed; answer that screen in the terminal and send again.",
            Assert.Single(session.History).Text);
    }

    [Fact]
    public void TheRefusalNoteJoinsTheReasonWithADash() =>
        Assert.Equal(
            "This terminal isn't at its prompt — Claude Code reports it is waiting on a dialog. "
            + "Nothing was typed; answer that screen in the terminal and send again.",
            LocalCliChatSession.NotAtPromptNote(TypingGate.WaitingWhy));

    // The log line that tells a real refusal's cause apart afterwards. Never any
    // screen text: a dialog's title is reduced to the word "dialog".
    [Theory]
    [InlineData(TypingGate.WaitingWhy, null, "session=s1 kind=record-waiting diag=none")]
    [InlineData(TypingGate.UnreadableWhy, "capture=null", "session=s1 kind=unreadable diag=capture=null")]
    [InlineData(TypingGate.UnreadableWhy, "capture=threw", "session=s1 kind=unreadable diag=capture=threw")]
    [InlineData("it is showing a dialog: Permissions", null, "session=s1 kind=dialog diag=none")]
    [InlineData("something new", null, "session=s1 kind=other diag=none")]
    [InlineData(null, null, "session=s1 kind=allowed diag=none")]
    public void ARefusalIsLoggedWithItsCauseAndNeverTheScreen(string? why, string? diagnostic, string expected)
    {
        var verdict = new TypingVerdict(false, why) { Diagnostic = diagnostic };

        var line = LocalCliChatSession.RefusalLogDetail("s1", verdict);

        Assert.Equal(expected, line);
        Assert.DoesNotContain("Permissions", line, StringComparison.Ordinal);
    }

    // --- shadow mode: work out the verdict, log it, type anyway ------------------

    [AvaloniaFact]
    public async Task InShadowModeARefusalIsLoggedButTheSendGoesAhead()
    {
        var calls = 0;
        var session = Gated(_ =>
        {
            calls++;
            return Task.FromResult(new TypingVerdict(false, TypingGate.UnreadableWhy) { Diagnostic = "capture=null" });
        }, shadow: true);

        // Null is "go ahead and type", and nothing is noted under the composer.
        Assert.Null(await session.RefuseIfNotAtPromptAsync());
        Assert.Equal(1, calls);
        Assert.Empty(session.History);
    }

    [AvaloniaFact]
    public async Task InShadowModeAnAllowedSendIsAlsoLeftAlone()
    {
        var session = Gated(_ => Task.FromResult(TypingVerdict.Allow), shadow: true);

        Assert.Null(await session.RefuseIfNotAtPromptAsync());
        Assert.Empty(session.History);
    }

    // Control: the same refusal with shadow off still refuses.
    [AvaloniaFact]
    public async Task WithShadowOffTheSameRefusalStillRefuses()
    {
        var session = Gated(
            _ => Task.FromResult(new TypingVerdict(false, TypingGate.UnreadableWhy)), shadow: false);

        Assert.Equal(ChatSendOutcome.Failed, await session.RefuseIfNotAtPromptAsync());
        Assert.Single(session.History);
    }

    // What the gate saw, reported as presence only: the line carries no path, no
    // pane id and no screen text.
    [Fact]
    public void TheLogSaysWhichStatusFieldsTheGateSawAndNoValues()
    {
        var status = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", SessionPid = 4321,
            TmuxSocket = "/private/tmp/tmux-501/default", TmuxPane = "%42", TmuxBin = "",
        };

        var line = LocalCliChatSession.RefusalLogDetail(
            "s1", new TypingVerdict(false, TypingGate.UnreadableWhy) { Diagnostic = "capture=null" },
            status, canReadPane: true, shadow: true);

        Assert.Equal(
            "session=s1 kind=unreadable diag=capture=null shadow=1 state=idle source=ClaudeCode pid=set "
            + "tmux_socket=set tmux_pane=set tmux_bin=empty can_read_pane=true",
            line);
        Assert.DoesNotContain("/private", line, StringComparison.Ordinal);
        Assert.DoesNotContain("%42", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionWithNoPidOrTmuxFieldsSaysSoPlainly()
    {
        var line = LocalCliChatSession.RefusalLogDetail(
            "s1", TypingVerdict.Allow, new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle" });

        Assert.Equal(
            "session=s1 kind=allowed diag=none state=idle source=ClaudeCode pid=none "
            + "tmux_socket=empty tmux_pane=empty tmux_bin=empty can_read_pane=false",
            line);
    }

    // --- CB-307 item 3: answering a question form from the local panel ----------

    // A pane that moves as a real one does: a screen, and every key pressed and
    // text typed recorded. Handed to the session through its capture, press and
    // type seams, so the shared procedure (FormAnswer) runs for real over it.
    private sealed class Pane(string screen)
    {
        public string Screen = screen;
        public List<string> Pressed { get; } = new();
        public List<string> Typed { get; } = new();
        public Func<string, string?> AfterPress = _ => null;
        public Func<string, string?> AfterType = _ => null;

        // When set, a capture waits for it and returns what it is given: a read
        // that began earlier and comes back later, as a slow one does.
        public TaskCompletionSource<string?>? Gate;

        public Task<string?> Capture(SessionStatus _) => Gate?.Task ?? Task.FromResult<string?>(Screen);

        public Task<bool> Press(SessionStatus _, string key)
        {
            Pressed.Add(key);
            if (AfterPress(key) is { } next) Screen = next;
            return Task.FromResult(true);
        }

        public Task<bool> Type(SessionStatus _, string text)
        {
            Typed.Add(text);
            if (AfterType(text) is { } next) Screen = next;
            return Task.FromResult(true);
        }
    }

    // A session waiting on the pane's screen, with the form read and on show.
    private static async Task<LocalCliChatSession> WaitingOn(Pane pane)
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", Title = "", TmuxPane = "%42",
        })
        {
            Capture = pane.Capture,
            PressKey = pane.Press,
            TypeLiteral = pane.Type,
        };

        // The scan's own update is what reads the prompt, once, off the pane.
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
        });

        for (var i = 0; i < 200 && session.Prompt is null; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.NotNull(session.Prompt);
        return session;
    }

    private static async Task WithReplyOn(Func<Task> work)
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;
            await work();
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    [AvaloniaFact]
    public async Task AFormIsShownWithItsTypeSomethingOptionAsAButton()
    {
        await WithReplyOn(async () =>
        {
            var session = await WaitingOn(new Pane(PaneFixtures.QaFormQ1));

            Assert.Equal("3", Assert.Single(session.Prompt!.Options, o => o.FreeText).Key);
        });
    }

    [AvaloniaFact]
    public async Task ADigitIsPressedWhenTheFormIsStillTheOneShown()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            await session.AnswerAsync(session.Prompt!.Options[1]);

            Assert.Equal(new[] { "2" }, pane.Pressed);
            Assert.Null(session.Prompt);
        });
    }

    // The screen moved on between the form being shown and the click: nothing is
    // pressed, and the user is told, and shown what is there now.
    [AvaloniaFact]
    public async Task AnAnswerToAFormThatMovedOnPressesNothingAndSaysSo()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            pane.Screen = PaneFixtures.MacModelSwitchDialog;
            await session.AnswerAsync(session.Prompt!.Options[1]);

            Assert.Empty(pane.Pressed);
            Assert.Contains(session.History, t => t.Text.Contains("had changed, so nothing was answered", StringComparison.Ordinal));
        });
    }

    [AvaloniaFact]
    public async Task TheMultiSelectSubmitButtonPressesRightAndNotADigit()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(AskFormFixtures.MultiSelectTwoChecked);
            var session = await WaitingOn(pane);

            await session.AnswerAsync(session.Prompt!.Options[^1]);

            Assert.Equal(new[] { "Right" }, pane.Pressed);
        });
    }

    // On a single-select question that digit selects the row empty, which
    // declines the whole form: it is never pressed, and the user is told where
    // the answer goes.
    [AvaloniaFact]
    public async Task TheFreeTextOptionIsNeverPressedByItsDigit()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            await session.AnswerAsync(session.Prompt!.Options.Single(o => o.FreeText));

            Assert.Empty(pane.Pressed);
            Assert.Contains(session.History, t => t.Text.Contains("in the box, then send it", StringComparison.Ordinal));
        });
    }

    // The typed answer: cursor onto the row, the text typed and seen, and only
    // then Enter — the shared procedure, run over a pane that moves like QA's.
    [AvaloniaFact]
    public async Task TypedTextGoesCursorThenTextThenEnter()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var downs = 0;
            pane.AfterPress = k => k == "Down" && ++downs == 2 ? PaneFixtures.QaCursorOnFreeText : null;
            pane.AfterType = _ => PaneFixtures.QaFreeTextTyped;
            var session = await WaitingOn(pane);

            await session.AnswerFreeTextAsync(session.Prompt!.Options.Single(o => o.FreeText), "my own answer");

            Assert.Equal(new[] { "Down", "Down", "Enter" }, pane.Pressed);
            Assert.Equal(new[] { "my own answer" }, pane.Typed);
        });
    }

    // Blank text is refused with nothing pressed or typed: Enter on the empty
    // row would decline the whole form.
    [AvaloniaFact]
    public async Task BlankTextIsRefusedAndNothingIsPressedOrTyped()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            await session.AnswerFreeTextAsync(session.Prompt!.Options.Single(o => o.FreeText), "   ");

            Assert.Empty(pane.Pressed);
            Assert.Empty(pane.Typed);
            Assert.Contains(session.History, t => t.Text.Contains("That can't be answered that way. Nothing was pressed.", StringComparison.Ordinal));
        });
    }

    // Only a form's own free-text option takes text, and only a form on show.
    [AvaloniaFact]
    public async Task TextForAnythingButTheFreeTextOptionOfAFormDoesNothing()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            await session.AnswerFreeTextAsync(session.Prompt!.Options[0], "Red please");

            Assert.Empty(pane.Pressed);
            Assert.Empty(pane.Typed);

            var plain = new LocalCliChatSession("s2", new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle" });
            await plain.AnswerFreeTextAsync(new ChatPromptOption("1", "x", FreeText: true), "text");
            Assert.Empty(plain.History);
        });
    }

    [AvaloniaFact]
    public async Task WithReplyingOffNothingIsAnsweredAndTheUserIsTold()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = false;
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            await session.AnswerAsync(session.Prompt!.Options[0]);
            await session.AnswerFreeTextAsync(session.Prompt.Options.Single(o => o.FreeText), "x");

            Assert.Empty(pane.Pressed);
            Assert.Equal(2, session.History.Count(t => t.Text.StartsWith("Replying is off", StringComparison.Ordinal)));
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // An ordinary dialog (a permission bar) still answers by its key, exactly as
    // it always did: only a question form goes through the shared procedure.
    [AvaloniaFact]
    public async Task AnOrdinaryDialogStillAnswersByItsKey()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.MiniExitDialog);
            var session = await WaitingOn(pane);

            await session.AnswerAsync(session.Prompt!.Options[0]);

            Assert.Equal(new[] { "1" }, pane.Pressed);
        });
    }

    [Fact]
    public void EveryRefusalCodeSaysWhatHappened()
    {
        Assert.Contains("had changed", LocalCliChatSession.FormRefusalNote(MirrorProtocol.ErrPromptChanged));
        Assert.Equal("That question was already answered.", LocalCliChatSession.FormRefusalNote(MirrorProtocol.ErrNoPrompt));
        Assert.Contains("Nothing was pressed", LocalCliChatSession.FormRefusalNote(MirrorProtocol.ErrBadKey));
        Assert.Contains("Answer it in the terminal", LocalCliChatSession.FormRefusalNote(MirrorProtocol.ErrUnsupported));
        Assert.Contains("Couldn't answer", LocalCliChatSession.FormRefusalNote(MirrorProtocol.ErrTypeFailed));
    }

    [Fact]
    public void ALocalMultiSelectOffersItsBoxesChatAndSubmit()
    {
        var prompt = LocalCliChatSession.ReadPrompt(AskFormFixtures.MultiSelectQ2)!;

        Assert.Equal(new[] { "1", "2", "3", "5", AskFormView.CommitKey }, prompt.Options.Select(o => o.Key));
    }

    // --- a second click on a form that was just answered (local) ----------------

    private static async Task<(LocalCliChatSession Session, Pane Pane, Func<DateTime> Now, Action<TimeSpan> Advance)> ClockedForm()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var pane = new Pane(PaneFixtures.QaFormQ1);
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", Title = "", TmuxPane = "%42",
        })
        {
            Capture = pane.Capture, PressKey = pane.Press, TypeLiteral = pane.Type,
            Clock = () => now,
        };

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
        });
        for (var i = 0; i < 200 && session.Prompt is null; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        return (session, pane, () => now, d => now += d);
    }

    private static async Task Rescan(LocalCliChatSession session)
    {
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
        });
        for (var i = 0; i < 40; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    // The terminal has not advanced yet, so the next scan reads the same form
    // again: it must not put live buttons back on a question already answered.
    [AvaloniaFact]
    public async Task TheFormJustAnsweredIsNotShownAgainBeforeTheTerminalAdvances()
    {
        await WithReplyOn(async () =>
        {
            var (session, pane, _, advance) = await ClockedForm();

            await session.AnswerAsync(session.Prompt!.Options[1]);
            Assert.Null(session.Prompt);

            await Rescan(session);
            Assert.Null(session.Prompt);
            Assert.Equal(new[] { "2" }, pane.Pressed);

            // Still the same form once the settle window has passed: the answer
            // did not take, so the buttons come back.
            advance(LocalCliChatSession.SettleAfterAnswer + TimeSpan.FromSeconds(1));
            await Rescan(session);
            Assert.NotNull(session.Prompt);
        });
    }

    [AvaloniaFact]
    public async Task TheNextQuestionIsShownAtOnceWhenTheTerminalHasAdvanced()
    {
        await WithReplyOn(async () =>
        {
            var (session, pane, _, _) = await ClockedForm();

            await session.AnswerAsync(session.Prompt!.Options[1]);
            pane.Screen = PaneFixtures.MacModelSwitchDialog;

            await Rescan(session);

            Assert.NotNull(session.Prompt);
            Assert.StartsWith("Opus 5.5's safeguards flagged this session.", session.Prompt!.Title);
        });
    }

    // A refusal presses nothing, so the same form comes straight back.
    [AvaloniaFact]
    public async Task ARefusedAnswerBringsTheSameFormStraightBack()
    {
        await WithReplyOn(async () =>
        {
            var (session, pane, _, _) = await ClockedForm();
            var option = session.Prompt!.Options.Single(o => o.FreeText);

            await session.AnswerFreeTextAsync(option, "   ");
            await Rescan(session);

            Assert.NotNull(session.Prompt);
            Assert.Empty(pane.Pressed);
        });
    }

    // --- a stale read must never redraw an answered form (local) ---------------

    // A capture already in flight when an answer goes out saw the screen as it
    // was before the key. It returns afterwards, showing the old form, and must
    // be dropped rather than drawn as current.
    [AvaloniaFact]
    public async Task ACaptureThatReturnsAfterAnAnswerIsDropped()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);
            var option = session.Prompt!.Options[1];

            // The first answer lands and the terminal moves on.
            pane.AfterPress = _ => PaneFixtures.MacModelSwitchDialog;
            await session.AnswerAsync(option);
            Assert.Null(session.Prompt);

            // A scan reads the pane again, and that read is slow...
            pane.Gate = new TaskCompletionSource<string?>();
            session.UpdateStatus(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
            });
            await Task.Delay(30);

            // ...an answer goes out meanwhile...
            await session.AnswerAsync(option);

            // ...and the slow read comes back with an OLD form — a different one
            // from the one answered, so the hold-back on the answered form does
            // not catch it and only the generation guard can.
            pane.Gate.SetResult(AskFormFixtures.MultiSelectQ2);
            for (var i = 0; i < 40; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }

            Assert.Null(session.Prompt);
        });
    }

    // The control: the same slow read with no answer in between is drawn.
    [AvaloniaFact]
    public async Task TheSameSlowReadIsDrawnWhenNoAnswerWentOutMeanwhile()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);
            pane.AfterPress = _ => PaneFixtures.MacModelSwitchDialog;
            await session.AnswerAsync(session.Prompt!.Options[1]);

            pane.Gate = new TaskCompletionSource<string?>();
            session.UpdateStatus(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
            });
            await Task.Delay(30);
            pane.Gate.SetResult(PaneFixtures.MacModelSwitchDialog);
            for (var i = 0; i < 40; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }

            Assert.NotNull(session.Prompt);
        });
    }

    // Nothing is waiting any more: a second click on a form that is already gone
    // clears quietly and is never reported as a failure.
    [AvaloniaFact]
    public async Task ANoPromptReplyToAnAnswerIsNotAnError()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            pane.Screen = PaneFixtures.MacPromptAfterEsc;
            await session.AnswerAsync(session.Prompt!.Options[0]);

            Assert.Contains(session.History, t => t.Text == "That question was already answered.");
            Assert.DoesNotContain(session.History, t => t.Text.Contains("Couldn't answer", StringComparison.Ordinal));
            Assert.Empty(pane.Pressed);
        });
    }

    // --- the default seams, and the arms only they reach ------------------------

    // The real gate and the real shadow switch, on a session with no pane and no
    // record: nothing can be read, so nothing is refused, and nothing spawns.
    [AvaloniaFact]
    public async Task TheDefaultGateOnASessionWithNoPaneAllowsTheSend()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", Title = "",
        });

        Assert.Null(await session.RefuseIfNotAtPromptAsync());
        Assert.Empty(session.History);
    }

    [AvaloniaFact]
    public async Task TheDefaultShadowSwitchIsOffUnlessAskedFor()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", Title = "",
        })
        {
            Gate = _ => Task.FromResult(new TypingVerdict(false, TypingGate.UnreadableWhy)),
        };

        // Off by default, so the refusal stands.
        Assert.Equal(ChatSendOutcome.Failed, await session.RefuseIfNotAtPromptAsync());
    }

    // A pane that cannot be captured: the form could not be read, so the honest
    // prompt is the one with nothing to click and the terminal link.
    [AvaloniaFact]
    public async Task AnUnreadablePaneShowsTheWaitingPromptWithNoOptions()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", Title = "", TmuxPane = "%42",
        })
        {
            Capture = _ => Task.FromResult<string?>(null),
        };

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%42",
        });
        for (var i = 0; i < 200 && session.Prompt is null; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal("Waiting for input", session.Prompt!.Title);
        Assert.Empty(session.Prompt.Options);
    }

    // The real clock, not a test's: a form re-read straight after an answer is
    // held back with the default clock too.
    [AvaloniaFact]
    public async Task TheFormJustAnsweredIsHeldBackOnTheRealClockToo()
    {
        await WithReplyOn(async () =>
        {
            var pane = new Pane(PaneFixtures.QaFormQ1);
            var session = await WaitingOn(pane);

            await session.AnswerAsync(session.Prompt!.Options[1]);
            await Rescan(session);

            Assert.Null(session.Prompt);
        });
    }
}
