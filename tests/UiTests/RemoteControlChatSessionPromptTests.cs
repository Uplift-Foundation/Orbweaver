using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using static Orbweaver.RemoteControlChatSession;

namespace Orbweaver.Tests;

// CB-307 addendum item 3: a peer's question form. The session asks the far side
// for it only while the panel is open and the far session is waiting, answers
// with the fingerprint it was shown, and never presses anything the far side did
// not just read off the screen. Driven with the transport seams and a clock the
// test ticks by hand, so the rules are decided without a socket or a sleep.
[Collection("Settings")]
public class RemoteControlChatSessionPromptTests
{
    private sealed class Harness
    {
        public DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        public bool Waiting = true;
        public int Asks;
        public readonly Queue<PromptReply> Replies = new();
        public readonly List<(string Key, string? Fp, string? Text)> Answers = new();
        public AnswerReply NextAnswer = new(AnswerKind.Done);
        public bool AskThrows;
        public bool AnswerThrows;
        public int PromptChanges;
        private readonly Queue<TaskCompletionSource> _ticks = new();

        public RemoteControlChatSession Session { get; }

        public Harness()
        {
            Session = new RemoteControlChatSession("rc:.claude:evidence", ".claude", "evidence")
            {
                WaitingNow = () => Waiting,
                Delay = (_, ct) =>
                {
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => tcs.TrySetCanceled());
                    lock (_ticks) _ticks.Enqueue(tcs);
                    return tcs.Task;
                },
            };

            Session.Ask = _ =>
            {
                Asks++;
                if (AskThrows) throw new InvalidOperationException("boom");
                return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : new PromptReply(PromptReplyKind.NoPrompt));
            };

            Session.Answer = (key, fp, text) =>
            {
                Answers.Add((key, fp, text));
                if (AnswerThrows) throw new InvalidOperationException("boom");
                return Task.FromResult(NextAnswer);
            };

            // The transport can carry typed text here, so the "Type something."
            // option is offered; the tests of the withheld case turn it off.
            Session.CarriesText = true;
            Session.Clock = () => Now;
            Session.PromptChanged += () => PromptChanges++;
            Session.AssumeMirroringForTests();
        }

        public void Open() => Session.PanelOpened();

        // One poll interval elapsing. Every pending delay completes: one that was
        // overtaken by a wake-up is still queued and, in real time, would simply
        // have run out on its own, so completing the oldest alone would tick a
        // loop that had already moved on.
        public void Tick()
        {
            List<TaskCompletionSource> due;
            lock (_ticks)
            {
                due = _ticks.ToList();
                _ticks.Clear();
            }

            foreach (var tick in due) tick.TrySetResult();
        }

        public async Task Settle()
        {
            for (var i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(2);
            }
        }
    }

    private static ChatPrompt Form(string title = "Which exhibit?") => new(title, new[]
    {
        new ChatPromptOption("1", "Exhibit A"),
        new ChatPromptOption("2", "Exhibit B"),
        new ChatPromptOption("3", "Type something.", FreeText: true),
    }, Progress: "Question 1 of 2");

    private static PromptReply Shown(string fp, string title = "Which exhibit?") =>
        new(PromptReplyKind.Prompt, Form(title), fp);

    [AvaloniaFact]
    public async Task OpeningThePanelWhileWaitingAsksAndShowsTheForm()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));

        h.Open();
        await h.Settle();

        Assert.Equal(1, h.Asks);
        Assert.Equal("Which exhibit?", h.Session.Prompt!.Title);
        Assert.Equal("Question 1 of 2", h.Session.Prompt.Progress);
        Assert.Equal(1, h.PromptChanges);
    }

    // The far terminal is not here, so "answer elsewhere" must not dismiss the
    // panel; its label says where.
    [AvaloniaFact]
    public async Task TheElsewhereLinkNamesTheFarMachineSoThePanelStaysOpen()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        Assert.Equal("Answer on the other machine", h.Session.Prompt!.ElsewhereLabel);

        h.Session.AnswerElsewhere();
        Assert.Contains(h.Session.History, t => t.Text.Contains("terminal of evidence", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task ASessionThatIsNotWaitingIsNeverAsked()
    {
        var h = new Harness { Waiting = false };

        h.Open();
        await h.Settle();
        h.Tick();
        await h.Settle();

        Assert.Equal(0, h.Asks);
        Assert.Null(h.Session.Prompt);
    }

    [AvaloniaFact]
    public async Task AClosedPanelIsNeverAsked()
    {
        var h = new Harness();

        h.Session.EnsurePromptPolling();
        await h.Settle();

        Assert.Equal(0, h.Asks);
    }

    [AvaloniaFact]
    public async Task WithoutALiveViewNothingIsAsked()
    {
        var h = new Harness();
        var bare = new RemoteControlChatSession("rc:.claude:x", ".claude", "x")
        {
            WaitingNow = () => true,
            Ask = _ => { h.Asks++; return Task.FromResult(Shown("fp")); },
        };

        bare.PanelOpened();
        await h.Settle();

        Assert.Equal(0, h.Asks);
    }

    [AvaloniaFact]
    public async Task ItKeepsAskingEveryTickWhileWaitingAndStopsWhenItLeaves()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();
        Assert.Equal(1, h.Asks);

        h.Tick();
        await h.Settle();
        Assert.Equal(2, h.Asks);

        // The far session answers in its own terminal and leaves waiting.
        h.Waiting = false;
        h.Tick();
        await h.Settle();

        Assert.Equal(2, h.Asks);
        Assert.Null(h.Session.Prompt);

        h.Tick();
        await h.Settle();
        Assert.Equal(2, h.Asks);
    }

    // A re-read of the same form every few seconds is not a change: the panel
    // would restart its arming clock and close a half-typed text box.
    [AvaloniaFact]
    public async Task TheSameFormAgainDoesNotRaiseAChangeButANewOneDoes()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp2", "Which month?"));
        h.Open();
        await h.Settle();

        h.Tick(); await h.Settle();
        Assert.Equal(1, h.PromptChanges);

        h.Tick(); await h.Settle();
        Assert.Equal(2, h.PromptChanges);
        Assert.Equal("Which month?", h.Session.Prompt!.Title);
    }

    [AvaloniaFact]
    public async Task AnUnsupportedFarSideStopsTheAskingForGood()
    {
        var h = new Harness();
        h.Replies.Enqueue(new PromptReply(PromptReplyKind.Unsupported));
        h.Open();
        await h.Settle();

        Assert.Equal(1, h.Asks);
        Assert.Null(h.Session.Prompt);

        h.Tick(); await h.Settle();
        h.Session.PanelOpened(); await h.Settle();
        Assert.Equal(1, h.Asks);
    }

    [AvaloniaFact]
    public async Task NothingReadableClearsTheFormAndATransientFailureLeavesItAlone()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(new PromptReply(PromptReplyKind.Transient));
        h.Replies.Enqueue(new PromptReply(PromptReplyKind.NoPrompt));
        h.Open();
        await h.Settle();
        Assert.NotNull(h.Session.Prompt);

        h.Tick(); await h.Settle();
        Assert.NotNull(h.Session.Prompt);

        h.Tick(); await h.Settle();
        Assert.Null(h.Session.Prompt);
    }

    [AvaloniaFact]
    public async Task AnAskThatThrowsIsATransientFailureNotACrash()
    {
        var h = new Harness { AskThrows = true };
        h.Open();
        await h.Settle();
        h.Tick(); await h.Settle();

        Assert.Equal(2, h.Asks);
        Assert.Null(h.Session.Prompt);
    }

    [AvaloniaFact]
    public async Task ASessionWithNoTransportReadsAsUnsupported()
    {
        var session = new RemoteControlChatSession("rc:.claude:x", ".claude", "x") { WaitingNow = () => true };
        session.AssumeMirroringForTests();

        session.PanelOpened();
        await new Harness().Settle();

        Assert.Null(session.Prompt);
    }

    [AvaloniaFact]
    public async Task ClosingThePanelStopsAskingAndDropsTheForm()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();
        Assert.NotNull(h.Session.Prompt);

        h.Session.PanelClosed();
        await h.Settle();
        h.Tick(); await h.Settle();

        Assert.Null(h.Session.Prompt);
        Assert.Equal(1, h.Asks);

        // Reopening asks afresh.
        h.Replies.Enqueue(Shown("fp1"));
        h.Open(); await h.Settle();
        Assert.Equal(2, h.Asks);
        Assert.NotNull(h.Session.Prompt);
    }

    [AvaloniaFact]
    public async Task ADisposedSessionAsksNothing()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        h.Session.Dispose();
        h.Tick(); await h.Settle();

        Assert.Equal(1, h.Asks);
        Assert.Null(h.Session.Prompt);
    }

    // --- answering ------------------------------------------------------------

    [AvaloniaFact]
    public async Task AnAnswerSendsTheKeyAndTheFingerprintItWasShownAndClearsTheButtons()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[1]);

        var answer = Assert.Single(h.Answers);
        Assert.Equal(("2", "fp1", (string?)null), answer);
        Assert.Null(h.Session.Prompt);
    }

    // The next tab appears on the far screen straight away: asked now, not at
    // the next tick.
    [AvaloniaFact]
    public async Task ADoneAnswerAsksForTheNextQuestionWithoutWaitingForATick()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp2", "Which month?"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();

        Assert.Equal(2, h.Asks);
        Assert.Equal("Which month?", h.Session.Prompt!.Title);
    }

    [AvaloniaFact]
    public async Task AFreeTextAnswerCarriesTheText()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerFreeTextAsync(h.Session.Prompt!.Options[2], "lead with B");

        Assert.Equal(("3", "fp1", "lead with B"), Assert.Single(h.Answers));
    }

    [AvaloniaFact]
    public async Task WhenTheScreenMovedOnTheNewQuestionIsShownAndNothingWasAnswered()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.NextAnswer = new AnswerReply(AnswerKind.Changed, Form("Which month?"), "fp2");
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);

        Assert.Equal("Which month?", h.Session.Prompt!.Title);
        Assert.Contains(h.Session.History, t => t.Text.Contains("had changed, so nothing was answered", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task AnAnswerTheFarSideCannotTakeSaysSoAndStopsAsking()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.NextAnswer = new AnswerReply(AnswerKind.Unsupported);
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        h.Tick(); await h.Settle();

        Assert.Contains(h.Session.History, t => t.Text.Contains("Answer it in its terminal", StringComparison.Ordinal));
        Assert.Equal(1, h.Asks);
    }

    [AvaloniaFact]
    public async Task AFailedAnswerSaysSoAndAsksAgainSoTheButtonsComeBack()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.NextAnswer = new AnswerReply(AnswerKind.Failed);
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();

        Assert.Contains(h.Session.History, t => t.Text.Contains("Couldn't answer that on evidence", StringComparison.Ordinal));
        Assert.Equal(2, h.Asks);
        Assert.NotNull(h.Session.Prompt);
    }

    [AvaloniaFact]
    public async Task AnAnswerThatThrowsIsAFailedAnswer()
    {
        var h = new Harness { AnswerThrows = true };
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);

        Assert.Contains(h.Session.History, t => t.Text.Contains("Couldn't answer that on evidence", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task AnAnswerWithNoFormOrNoTransportDoesNothing()
    {
        var h = new Harness();
        await h.Session.AnswerAsync(new ChatPromptOption("1", "x"));
        Assert.Empty(h.Answers);

        var bare = new RemoteControlChatSession("rc:.claude:x", ".claude", "x");
        await bare.AnswerAsync(new ChatPromptOption("1", "x"));
    }

    // Until the transport can carry typed text, the "Type something." option is
    // withheld from a peer's form: pressing its key alone would leave the far
    // screen on a text-entry state nothing here can finish.
    [AvaloniaFact]
    public async Task TheTypeSomethingOptionIsWithheldWhenTheTransportCannotCarryText()
    {
        var h = new Harness();
        h.Session.CarriesText = false;
        h.Replies.Enqueue(Shown("fp1"));

        h.Open();
        await h.Settle();

        Assert.Equal(new[] { "1", "2" }, h.Session.Prompt!.Options.Select(o => o.Key));
    }

    // --- the wire, as the panel reads it --------------------------------------

    private static MirrorProtocol.WirePrompt Wire(
        string? freeText = "3", int current = 1, params (string Name, string State)[] tabs) =>
        new("Which exhibit?",
            new[]
            {
                new MirrorProtocol.WireOption("1", "Exhibit A"),
                new MirrorProtocol.WireOption("2", "Exhibit B"),
                new MirrorProtocol.WireOption("3", "Type something."),
            },
            "fp9",
            tabs.Select(t => new MirrorProtocol.WireTab(t.Name, t.State)).ToList(),
            current, freeText);

    [Fact]
    public void AFetchedFormBecomesAPromptWithProgressAndAFreeTextOption()
    {
        var reply = MapFetch(new RemoteMirrorClient.PromptOutcome(
            Wire(tabs: new[] { ("A", "answered"), ("B", "open"), ("C", "open"), ("Submit", "submit") }), null));

        Assert.Equal(PromptReplyKind.Prompt, reply.Kind);
        Assert.Equal("fp9", reply.Fingerprint);
        Assert.Equal("Question 2 of 3", reply.Prompt!.Progress);
        Assert.Equal("3", Assert.Single(reply.Prompt.Options, o => o.FreeText).Key);
    }

    [Fact]
    public void AnUnknownTabStateReadsAsOpenAndNeverInventsAProgress()
    {
        var reply = MapFetch(new RemoteMirrorClient.PromptOutcome(
            Wire(current: -1, tabs: new[] { ("A", "mystery"), ("B", "open") }), null));

        Assert.Null(reply.Prompt!.Progress);
    }

    // Looped rather than a [Theory]: the kinds are internal, and a public xUnit
    // method may not take an internal parameter type.
    [Fact]
    public void AFetchErrorMapsToWhatThisSideDoesAboutIt()
    {
        var cases = new (string? Code, PromptReplyKind Expected)[]
        {
            (MirrorProtocol.ErrNoPrompt, PromptReplyKind.NoPrompt),
            (MirrorProtocol.ErrUnsupported, PromptReplyKind.Unsupported),
            (MirrorProtocol.ErrNoPane, PromptReplyKind.Unsupported),
            (MirrorProtocol.ErrReplyOff, PromptReplyKind.Unsupported),
            (MirrorProtocol.ErrNoSession, PromptReplyKind.Transient),
            ("something-new", PromptReplyKind.Transient),
            (null, PromptReplyKind.Transient),
        };

        foreach (var (code, expected) in cases)
            Assert.Equal(expected, MapFetch(new RemoteMirrorClient.PromptOutcome(null, code)).Kind);
    }

    [Fact]
    public void AnAnswerCodeMapsToWhatThisSideDoesAboutIt()
    {
        var cases = new (string? Code, AnswerKind Expected)[]
        {
            (null, AnswerKind.Done),
            (MirrorProtocol.ErrPromptChanged, AnswerKind.Changed),
            (MirrorProtocol.ErrNoPrompt, AnswerKind.AlreadyAnswered),
            (MirrorProtocol.ErrBadKey, AnswerKind.Refused),
            (MirrorProtocol.ErrUnsupported, AnswerKind.Unsupported),
            (MirrorProtocol.ErrNoPane, AnswerKind.Unsupported),
            (MirrorProtocol.ErrReplyOff, AnswerKind.Unsupported),
            ("something-new", AnswerKind.Failed),
        };

        foreach (var (code, expected) in cases)
            Assert.Equal(expected, MapAnswerCode(code));
    }

    // The free-text option is never answered by its key: on the far screen that
    // digit would select it empty, which declines the whole form. Pressing it
    // without text sends nothing and says what to do.
    [AvaloniaFact]
    public async Task TheFreeTextOptionIsNeverSentWithoutText()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[2]);

        Assert.Empty(h.Answers);
        Assert.Contains(h.Session.History, t => t.Text.Contains("in the box, then send it", StringComparison.Ordinal));
        Assert.NotNull(h.Session.Prompt);
    }

    // The explicit Submit button's answer goes out under its own key, with the
    // fingerprint the user saw, and not as a digit.
    [AvaloniaFact]
    public async Task TheSubmitButtonIsSentUnderTheCommitKey()
    {
        var h = new Harness();
        h.Replies.Enqueue(new PromptReply(PromptReplyKind.Prompt, new ChatPrompt("Pick toppings?",
            new[]
            {
                new ChatPromptOption("1", "\u2610 Cheese"),
                new ChatPromptOption(AskFormView.CommitKey, "Submit"),
            }), "fp7"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[1]);

        Assert.Equal((AskFormView.CommitKey, "fp7", (string?)null), Assert.Single(h.Answers));
    }

    // The far side declined the request as asked, which is not the screen having
    // moved on: nothing was pressed, the buttons come back, and the note says so.
    [AvaloniaFact]
    public async Task ARefusedAnswerSaysNothingWasPressedAndBringsTheButtonsBack()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.NextAnswer = new AnswerReply(AnswerKind.Refused);
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();

        Assert.Contains(h.Session.History, t => t.Text.Contains("can't be answered that way on evidence. Nothing was pressed.", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Session.History, t => t.Text.Contains("had changed", StringComparison.Ordinal));
        Assert.Equal(2, h.Asks);
        Assert.NotNull(h.Session.Prompt);
    }

    // --- not yet known is not unsupported; and a skip says why ----------------

    // The far machine's "I answer PROMPT" rides on the roster reply, which may
    // land a moment after a panel starts asking. That is not "unsupported", which
    // is final: the next tick asks again and the form arrives.
    [AvaloniaFact]
    public async Task AFarSideNotYetKnownToAnswerPromptsIsAskedAgainAndNeverSettledAsUnsupported()
    {
        var h = new Harness();
        h.Replies.Enqueue(new PromptReply(PromptReplyKind.NotYet));
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();
        Assert.Null(h.Session.Prompt);
        Assert.Equal(1, h.Asks);

        h.Tick();
        await h.Settle();

        Assert.Equal(2, h.Asks);
        Assert.NotNull(h.Session.Prompt);
    }

    private static List<(string What, string Detail)> Logged(RemoteControlChatSession session)
    {
        var lines = new List<(string, string)>();
        session.PromptLog = (what, detail) => lines.Add((what, detail));
        return lines;
    }

    // A session that is not waiting says so, with what the roster did say, once.
    [AvaloniaFact]
    public async Task ASkipForNotWaitingNamesTheRosterStatusAndIsSaidOnce()
    {
        var h = new Harness { Waiting = false };
        var log = Logged(h.Session);

        h.Open();
        await h.Settle();
        h.Session.EnsurePromptPolling();
        h.Session.EnsurePromptPolling();

        var line = Assert.Single(log);
        Assert.Equal("prompt-skip", line.What);
        Assert.Contains("n=evidence not-waiting", line.Detail);
        Assert.Contains("account=.claude", line.Detail);
    }

    [AvaloniaFact]
    public async Task ASkipForANotEligiblePanelSaysWhichConditionFailed()
    {
        var h = new Harness();
        var log = Logged(h.Session);

        // Never opened.
        h.Session.EnsurePromptPolling();
        await h.Settle();

        var line = Assert.Single(log);
        Assert.Contains("not-eligible", line.Detail);
        Assert.Contains("panel-open=False", line.Detail);
        Assert.Contains("mirroring=True", line.Detail);
    }

    [AvaloniaFact]
    public async Task ASkipForAlreadyPollingSaysSo()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();
        var log = Logged(h.Session);

        h.Session.EnsurePromptPolling();

        Assert.Contains(log, l => l.Detail.Contains("already-polling"));
    }

    [AvaloniaFact]
    public async Task ANotYetReplySaysItWillAskAgain()
    {
        var h = new Harness();
        var log = Logged(h.Session);
        h.Replies.Enqueue(new PromptReply(PromptReplyKind.NotYet));

        h.Open();
        await h.Settle();

        Assert.Contains(log, l => l.Detail.Contains("far-side-not-known-to-answer-prompts"));
    }

    // A different reason is a different line, so a change is never swallowed.
    [AvaloniaFact]
    public async Task ADifferentSkipReasonIsSaidAgain()
    {
        var h = new Harness { Waiting = false };
        var log = Logged(h.Session);

        h.Open();
        await h.Settle();
        h.Session.PanelClosed();
        h.Session.EnsurePromptPolling();

        Assert.Equal(2, log.Count);
        Assert.Contains("not-waiting", log[0].Detail);
        Assert.Contains("not-eligible", log[1].Detail);
    }

    // --- a second click on a question that was just answered -------------------

    // The far screen takes a moment to advance after a key, so an immediate
    // re-ask reads the question that was just answered. Live buttons back on it
    // invite a second click that answers the NEXT question blind. It is held back.
    [AvaloniaFact]
    public async Task TheFormJustAnsweredIsNotShownAgainWhileTheScreenCatchesUp()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();

        Assert.Equal(2, h.Asks);
        Assert.Null(h.Session.Prompt);
    }

    [AvaloniaFact]
    public async Task TheNextQuestionIsShownTheMomentItArrives()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp2", "Which month?"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();
        Assert.Null(h.Session.Prompt);

        h.Tick();
        await h.Settle();

        Assert.Equal("Which month?", h.Session.Prompt!.Title);
    }

    // If the same form is still there after the settle window, the answer did not
    // take, and the buttons come back.
    [AvaloniaFact]
    public async Task AFormStillThereAfterTheSettleWindowIsShownAgain()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();
        Assert.Null(h.Session.Prompt);

        h.Now += RemoteControlChatSession.SettleAfterAnswer + TimeSpan.FromSeconds(1);
        h.Tick();
        await h.Settle();

        Assert.NotNull(h.Session.Prompt);
    }

    // Nothing was pressed when the far side refused or failed, so there is no
    // screen about to move on: the buttons come straight back.
    [AvaloniaFact]
    public async Task AnAnswerThatFailedBringsTheSameFormStraightBack()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.Replies.Enqueue(Shown("fp1"));
        h.NextAnswer = new AnswerReply(AnswerKind.Failed);
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();

        Assert.NotNull(h.Session.Prompt);
    }

    // --- a stale read must never redraw an answered form -------------------------

    // The real failure, in the order it happened: a poll is in flight when the
    // answer goes out, its reply is the screen as it was before the key landed,
    // and it arrives afterwards. It must be dropped, not drawn as current — the
    // final review screen showed twice, and the second Submit was a no-prompt
    // error, in every one of Warren's runs.
    [AvaloniaFact]
    public async Task APollReplyThatArrivesAfterAnAnswerIsDropped()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<PromptReply>();
        var asks = 0;
        h.Session.Ask = _ =>
        {
            asks++;
            return asks == 1 ? Task.FromResult(Shown("fp1")) : gate.Task;
        };
        h.Open();
        await h.Settle();
        Assert.NotNull(h.Session.Prompt);

        // A poll goes out and is still in flight...
        h.Tick();
        await h.Settle();
        Assert.Equal(2, asks);

        // ...the answer is sent...
        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        Assert.Null(h.Session.Prompt);

        // ...and only then does the old read come back, showing the old form. The
        // settle window is allowed to run out first, so what drops it is the
        // generation guard and not the hold-back that would also catch it.
        h.Now += RemoteControlChatSession.SettleAfterAnswer + TimeSpan.FromSeconds(1);
        gate.SetResult(Shown("fp1"));
        await h.Settle();

        Assert.Null(h.Session.Prompt);
    }

    // The control: the same reply arriving with no answer in between is drawn.
    [AvaloniaFact]
    public async Task TheSameReplyIsDrawnWhenNoAnswerWentOutMeanwhile()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<PromptReply>();
        var asks = 0;
        h.Session.Ask = _ =>
        {
            asks++;
            return asks == 1 ? Task.FromResult(new PromptReply(PromptReplyKind.NoPrompt)) : gate.Task;
        };
        h.Open();
        await h.Settle();
        h.Tick();
        await h.Settle();

        gate.SetResult(Shown("fp1"));
        await h.Settle();

        Assert.NotNull(h.Session.Prompt);
    }

    // No new poll goes out while an answer is in flight.
    [AvaloniaFact]
    public async Task NoPollGoesOutWhileAnAnswerIsInFlight()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        var release = new TaskCompletionSource<AnswerReply>();
        h.Session.Answer = (_, _, _) => release.Task;
        h.Open();
        await h.Settle();
        var before = h.Asks;

        var answering = h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        h.Tick();
        await h.Settle();

        Assert.Equal(before, h.Asks);

        release.SetResult(new AnswerReply(AnswerKind.Done));
        await answering;
    }

    // --- a no-prompt reply to an answer is not an error ----------------------------

    // The form was already answered — by this panel a moment ago, or in the
    // terminal. Cleared quietly: a dim note, never "Couldn't answer".
    [AvaloniaFact]
    public async Task ANoPromptReplyToAnAnswerClearsTheFormQuietly()
    {
        var h = new Harness();
        h.Replies.Enqueue(Shown("fp1"));
        h.NextAnswer = new AnswerReply(AnswerKind.AlreadyAnswered);
        h.Open();
        await h.Settle();

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);
        await h.Settle();

        Assert.Null(h.Session.Prompt);
        Assert.Contains(h.Session.History, t => t.Text == "That question was already answered.");
        Assert.DoesNotContain(h.Session.History, t => t.Text.Contains("Couldn't answer", StringComparison.Ordinal));
    }

    // --- nothing a person reads names the route ------------------------------------

    private static RemoteControlChatSession ByRoute() =>
        new("rc:.claude-board:sid:b5e36eef-9491-40d1-8604-3f31e9a467ae", ".claude-board",
            "sid:b5e36eef-9491-40d1-8604-3f31e9a467ae");

    [AvaloniaFact]
    public void ASessionAddressedByRouteIsNeverNamedByItInTheComposerOrTheHeader()
    {
        var session = ByRoute();

        Assert.DoesNotContain("sid:", session.ComposerHint);
        Assert.DoesNotContain("sid:", session.DisplayName);
        Assert.DoesNotContain("sid:", session.WaitingFor);
        Assert.Equal("that session", session.FarTitle);
    }

    private static async Task<RemoteControlChatSession> OpenedByRoute(AnswerKind answer)
    {
        var session = ByRoute();
        session.WaitingNow = () => true;
        session.AssumeMirroringForTests();
        session.Ask = _ => Task.FromResult(new PromptReply(PromptReplyKind.Prompt, Form(), "fp1"));
        session.Answer = (_, _, _) => Task.FromResult(new AnswerReply(answer));
        session.PanelOpened();
        await new Harness().Settle();
        return session;
    }

    // Every note a prompt can produce, for a session addressed by route.
    [AvaloniaFact]
    public async Task EveryPromptNoteNamesTheTitleNotTheRoute()
    {
        foreach (var kind in new[]
                 {
                     AnswerKind.Failed, AnswerKind.Refused, AnswerKind.Unsupported, AnswerKind.AlreadyAnswered,
                 })
        {
            var session = await OpenedByRoute(kind);
            await session.AnswerAsync(session.Prompt!.Options[0]);

            Assert.All(session.History, t => Assert.DoesNotContain("sid:", t.Text));
            Assert.All(session.History, t => Assert.DoesNotContain("b5e36eef", t.Text));
        }

        var other = await OpenedByRoute(AnswerKind.Done);
        other.AnswerElsewhere();
        await other.AnswerAsync(other.Prompt!.Options.First(o => o.FreeText));

        Assert.All(other.History, t => Assert.DoesNotContain("sid:", t.Text));
        Assert.Contains(other.History, t => t.Text.Contains("that session", StringComparison.Ordinal));
        Assert.Equal("Answer on the other machine", ByRouteLabel(other));
    }

    private static string? ByRouteLabel(RemoteControlChatSession session) => session.Prompt?.ElsewhereLabel;

    [Fact]
    public void ARouteIsRecognisedAsOneAndATitleIsNot()
    {
        Assert.True(RemoteControlChatSession.IsRoute("sid:b5e36eef"));
        Assert.True(RemoteControlChatSession.IsRoute("SID:abc"));
        Assert.True(RemoteControlChatSession.IsRoute(""));
        Assert.True(RemoteControlChatSession.IsRoute(null));
        Assert.False(RemoteControlChatSession.IsRoute("lawyer-search"));
    }

    // --- the remaining arms --------------------------------------------------------

    // A cancelled ask is the panel closing or the session going away: the loop
    // ends quietly and nothing is shown, and a later open can poll again.
    [AvaloniaFact]
    public async Task AnAskThatIsCancelledEndsThePollQuietly()
    {
        var h = new Harness();
        var asks = 0;
        h.Session.Ask = _ =>
        {
            asks++;
            if (asks == 1) throw new OperationCanceledException();
            return Task.FromResult(Shown("fp1"));
        };

        h.Open();
        await h.Settle();
        Assert.Null(h.Session.Prompt);
        Assert.Equal(1, asks);

        h.Session.PanelClosed();
        h.Open();
        await h.Settle();

        Assert.NotNull(h.Session.Prompt);
    }

    // A read that comes back after the panel was closed is not applied.
    [AvaloniaFact]
    public async Task AReadThatReturnsAfterThePanelClosedIsNotApplied()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<PromptReply>();
        h.Session.Ask = _ => gate.Task;
        h.Open();
        await h.Settle();

        h.Session.PanelClosed();
        gate.SetResult(Shown("fp1"));
        await h.Settle();

        Assert.Null(h.Session.Prompt);
    }

    // Every reply kind, applied directly.
    [AvaloniaFact]
    public void EveryReplyKindIsAppliedAsItShouldBe()
    {
        var h = new Harness();

        // A prompt reply that carries no prompt is not a form.
        h.Session.ApplyReply(new PromptReply(PromptReplyKind.Prompt, null, "fp"));
        Assert.Null(h.Session.Prompt);

        h.Session.ApplyReply(Shown("fp1"));
        Assert.NotNull(h.Session.Prompt);

        h.Session.ApplyReply(new PromptReply(PromptReplyKind.Transient));
        Assert.NotNull(h.Session.Prompt);

        h.Session.ApplyReply(new PromptReply(PromptReplyKind.NotYet));
        Assert.NotNull(h.Session.Prompt);

        h.Session.ApplyReply(new PromptReply(PromptReplyKind.NoPrompt));
        Assert.Null(h.Session.Prompt);

        h.Session.ApplyReply(Shown("fp1"));
        h.Session.ApplyReply(new PromptReply(PromptReplyKind.Unsupported));
        Assert.Null(h.Session.Prompt);
    }

    // A kind this side has never heard of (a newer far Buddy's) leaves what is on
    // show alone rather than guessing.
    [AvaloniaFact]
    public void AReplyKindNobodyKnowsLeavesTheFormAlone()
    {
        var h = new Harness();
        h.Session.ApplyReply(Shown("fp1"));

        h.Session.ApplyReply(new PromptReply((PromptReplyKind)99));

        Assert.NotNull(h.Session.Prompt);
    }

    // A label the far side already set is kept, not replaced by ours.
    [AvaloniaFact]
    public void AnElsewhereLabelAlreadySetIsKept()
    {
        var h = new Harness();

        h.Session.ApplyReply(new PromptReply(
            PromptReplyKind.Prompt, Form() with { ElsewhereLabel = "Answer over there" }, "fp1"));

        Assert.Equal("Answer over there", h.Session.Prompt!.ElsewhereLabel);
    }

    // With no loop running (nothing has woken it), an answer must still finish:
    // there is simply nobody to wake.
    [AvaloniaFact]
    public async Task AnAnswerWithNoPollLoopRunningStillFinishesForEveryOutcome()
    {
        foreach (var kind in new[]
                 {
                     AnswerKind.Done, AnswerKind.AlreadyAnswered, AnswerKind.Refused, AnswerKind.Failed,
                 })
        {
            var h = new Harness { Waiting = false };
            h.NextAnswer = new AnswerReply(kind);
            h.Session.ApplyReply(Shown("fp1"));

            await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);

            Assert.Single(h.Answers);
            Assert.Null(h.Session.Prompt);
        }
    }

    // The screen moved on but the far side gave no form to show: nothing to put
    // up, so the user is told it could not be answered.
    [AvaloniaFact]
    public async Task AChangedAnswerWithNoNewFormIsReportedAsNotAnswered()
    {
        var h = new Harness { Waiting = false };
        h.NextAnswer = new AnswerReply(AnswerKind.Changed, null);
        h.Session.ApplyReply(Shown("fp1"));

        await h.Session.AnswerAsync(h.Session.Prompt!.Options[0]);

        Assert.Contains(h.Session.History, t => t.Text.Contains("Couldn't answer that", StringComparison.Ordinal));
    }

    // The default transport with nothing to talk to: no link, so the answer is a
    // failure, said as one, rather than a crash or a silence.
    [AvaloniaFact]
    public async Task AnAnswerWithNoLinkAtAllIsAFailure()
    {
        PeerSessions.UseClientForTests(null);
        var session = new RemoteControlChatSession("rc:.claude:evidence", ".claude", "evidence")
        {
            WaitingNow = () => false,
        };
        session.ApplyReply(Shown("fp1"));

        await session.AnswerAsync(session.Prompt!.Options[0]);

        Assert.Contains(session.History, t => t.Text.Contains("Couldn't answer that", StringComparison.Ordinal));
    }

    // A refusal with no detail has no dash.
    [Fact]
    public void ANotAtPromptRefusalWithNoDetailHasNoDash()
    {
        var note = RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrNotAtPrompt, "evidence", null);

        Assert.Equal(
            "evidence's terminal isn't at its prompt. Nothing was typed; answer that screen in the terminal and send again.",
            note);
        Assert.Contains(" — it is on a dialog",
            RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrNotAtPrompt, "evidence", "it is on a dialog"));
    }
}
