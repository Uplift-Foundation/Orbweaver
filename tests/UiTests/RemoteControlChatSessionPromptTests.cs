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
            Session.PromptChanged += () => PromptChanges++;
            Session.AssumeMirroringForTests();
        }

        public void Open() => Session.PanelOpened();

        // One poll interval elapsing.
        public void Tick()
        {
            TaskCompletionSource? next = null;
            lock (_ticks) if (_ticks.Count > 0) next = _ticks.Dequeue();
            next?.TrySetResult();
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

        Assert.Equal("Answer on evidence", h.Session.Prompt!.ElsewhereLabel);

        h.Session.AnswerElsewhere();
        Assert.Contains(h.Session.History, t => t.Text.Contains("evidence's terminal", StringComparison.Ordinal));
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
            (MirrorProtocol.ErrNoPrompt, AnswerKind.Changed),
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
}
