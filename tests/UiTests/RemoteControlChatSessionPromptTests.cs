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
}
