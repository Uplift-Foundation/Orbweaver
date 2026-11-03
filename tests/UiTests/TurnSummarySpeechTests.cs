using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-167's "vibe summary" turn-finished sound, from SpeechRequest.
// SpeakTurnSummary down through OrbWindow.SpeakTurnSummary — the layer
// SpeakScopeUiTests already proves the ordinary speak button through, now
// exercised for the path TurnSounds.Deliver reaches instead of a button
// click.
//
// Same boundary SpeakScopeUiTests and OrbWindowSpeakTests both keep: every
// case here stops at SpeechRequest.UtteranceForTests or SpeechSummary.
// SummarizerForTests, never at a real /usr/bin/say. See either file's header
// for why that is not optional.
[Collection("Settings")]
public class TurnSummarySpeechTests : IDisposable
{
    private readonly SpeakScope _scopeWas = OrbweaverSettings.SpeakScope;

    public void Dispose()
    {
        OrbweaverSettings.SpeakScope = _scopeWas;
        SpeechSummary.SummarizerForTests = null;
        SpeechSummary.AccountSummarizerForTests = null;
        SpeechRequest.UtteranceForTests = null;
        SpeechRequest.TranscriptPathForTests = null;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
    }

    // Long enough that SpeechPlan.For routes it through the summary leg
    // rather than speaking it whole — built from the threshold so it cannot
    // silently drift out of sync with the rule it exists to cross.
    private static string LongReply() =>
        string.Join(" ", Enumerable.Repeat("the assistant did a great deal", 60))
            .PadRight(SpeechPlan.ShortEnoughChars + 1, '.');

    // SpeakTurnSummaryAsync's local branch now posts the utterance onto the
    // UI thread rather than calling it inline (QA, CB-167 — the transcript
    // walk that used to happen synchronously here moved off-thread, and
    // SpeechRequest.SpeakTurnSummary has to stay on the UI thread regardless
    // of which thread the continuation resumes on). A case that awaits the
    // method and then wants to assert what got spoken has to pump the
    // dispatcher once afterwards to let that posted job actually run.
    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // --- SpeechRequest.SpeakTurnSummary --------------------------------------

    // Exercises the same SpeakSummaryAsync(..., TurnFinished) overload the
    // section header above used to call directly with a hand-picked request
    // number — which is exactly the bug this version doesn't have.
    // _requestGeneration is one counter shared by the whole process for the
    // life of the test run, so a literal like `request: 1` is only ever
    // right by accident; going through SpeakTurnSummary lets it mint its own
    // NextRequest() the way production code does, which is also the only
    // way ShouldStillSpeak's check means anything.
    //
    // A fake summariser that resolves synchronously (Task.FromResult) lets
    // await complete without yielding, which is what makes the whole chain —
    // TurnFinished prompt in, spoken text out — observable right after this
    // call returns, with no dispatcher pump needed.
    [AvaloniaFact]
    public void ALongReplyIsSummarisedAndTheResultIsSpoken()
    {
        OrbweaverSettings.SpeakScope = SpeakScope.Full; // proves the forced override below
        var reply = LongReply();

        string? seenBySummariser = null;
        SpeechSummary.SummarizerForTests = text =>
        {
            seenBySummariser = text;
            return Task.FromResult<string?>("Fixed the bug. Next: write the tests.");
        };

        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

        SpeechRequest.SpeakTurnSummary(reply, "session-x");

        Assert.Equal(reply, seenBySummariser);
        Assert.Equal("Fixed the bug. Next: write the tests.", spoken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToSayNeverTouchesTheSummariserOrTheSpeaker(string? reply)
    {
        var summariserCalled = false;
        SpeechSummary.SummarizerForTests = _ => { summariserCalled = true; return Task.FromResult<string?>("x"); };
        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

        SpeechRequest.SpeakTurnSummary(reply, "session-x");

        Assert.False(summariserCalled);
        Assert.Null(spoken);
    }

    // Always Summary scope, regardless of what the user has the ordinary
    // speak button set to — choosing "Vibe summary" as an orb's finished
    // sound is itself the request for a summary. Full is picked deliberately
    // here to prove the global setting is not what decides this.
    [AvaloniaFact]
    public void AShortReplyIsSpokenInFullEvenWithTheGlobalScopeSetToFull()
    {
        OrbweaverSettings.SpeakScope = SpeakScope.Full;

        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;
        var summariserCalled = false;
        SpeechSummary.SummarizerForTests = _ => { summariserCalled = true; return Task.FromResult<string?>("x"); };

        SpeechRequest.SpeakTurnSummary("Fixed the nested-team case.", "session-x");

        Assert.Equal("Fixed the nested-team case.", spoken);
        Assert.False(summariserCalled); // under SpeechPlan's threshold — no round trip needed
    }

    // The long-reply arm starts the summary leg synchronously up to its first
    // await — TextToSpeech.Enter(Preparing) runs before anything is awaited,
    // the same "hourglass goes up before the round trip" guarantee
    // SpeakScopeUiTests pins for the ordinary speak button. Asserting it here
    // is what proves SpeakTurnSummary's NeedsSummary branch actually ran
    // rather than silently falling through to Utter.
    [AvaloniaFact]
    public void ALongReplyEntersPreparingSynchronouslyBeforeAnyRoundTrip()
    {
        OrbweaverSettings.SpeakScope = SpeakScope.Full;

        // Never lets the round trip finish within this test, so the state
        // this asserts is unambiguously the synchronous part of the call.
        var never = new TaskCompletionSource<string?>();
        SpeechSummary.SummarizerForTests = _ => never.Task;

        try
        {
            SpeechRequest.SpeakTurnSummary(LongReply(), "session-x");

            Assert.Equal(TextToSpeech.SpeakState.Preparing, TextToSpeech.State);
        }
        finally
        {
            never.SetResult("unused");
        }
    }

    // CB-298: muted, the turn-summary path utters nothing, asks the
    // summariser nothing and never raises the hourglass.
    [AvaloniaFact]
    public void WhileMutedTheTurnSummaryUttersNothingAndNeverEntersPreparing()
    {
        OrbweaverSettings.SpeakScope = SpeakScope.Full;
        var uttered = 0;
        var asked = 0;
        SpeechRequest.UtteranceForTests = (_, _, _) => uttered++;
        SpeechSummary.SummarizerForTests = _ => { asked++; return Task.FromResult<string?>("x"); };

        try
        {
            AudioMute.Set(true);
            SpeechRequest.SpeakTurnSummary(LongReply(), "session-x");
            SpeechRequest.SpeakTurnSummary("A short reply.", "session-x");

            Assert.Equal(0, uttered);
            Assert.Equal(0, asked);
            Assert.NotEqual(TextToSpeech.SpeakState.Preparing, TextToSpeech.State);
        }
        finally { AudioMute.Set(false); }
    }

    // --- OrbWindow.SpeakTurnSummary ------------------------------------------

    private static string WriteTranscript(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "cb-uitests-turnsummary-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    // QA (CB-167) named this arm specifically: an orb that has never had
    // UpdateFrom called (TurnSounds.Deliver's callback resolving a window
    // that scan just created, before the first UpdateFrom on the very
    // fastest path — theoretical today, but this is what makes both
    // SpeakTurnSummaryAsync and SpeakTurnSummaryRemoteAsync's null-status
    // arms real code paths and not merely lines that happen never to be
    // asked).
    [AvaloniaFact]
    public async Task ANeverInitialisedOrbReportsNothingSpokenRatherThanThrowing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        Assert.False(await orb.SpeakTurnSummaryAsync());
    }

    [AvaloniaFact]
    public async Task ASessionWithNoTranscriptAnywhereSpeaksNothing()
    {
        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "claude-buddy",
            Cwd = "",
            TranscriptPath = "",
        });

        var spoke = await orb.SpeakTurnSummaryAsync();
        Flush();

        Assert.False(spoke);
        Assert.Null(spoken);
    }

    // Safe to press for real, the same reason SpeakScopeUiTests presses the
    // ordinary button for real: SpeechRequest's seam stands in for the one
    // excluded line, so this reaches SpeakTurnSummaryAsync's whole local
    // branch — the off-thread FindSpeakableText walk, SpeechPlan, and Utter
    // — without starting a process.
    [AvaloniaFact]
    public async Task ALocalSessionWithATranscriptSpeaksItsLastTurnThroughTheTurnSummaryPath()
    {
        const string AssistantSaid =
            """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";
        var path = WriteTranscript(AssistantSaid + "\n");

        try
        {
            var spoken = (string?)null;
            SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.ClaudeCode,
                State = "idle",
                Title = "claude-buddy",
                Cwd = "/tmp/does-not-matter",
                TranscriptPath = path,
            });

            var spoke = await orb.SpeakTurnSummaryAsync();
            Flush();   // runs the Dispatcher.UIThread.Post(...) this queued

            Assert.True(spoke);
            Assert.Equal("Fixed the nested-team case.", spoken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // RemoteControl and ClaudeCloud orbs, and anything else that isn't a
    // local CLI, have no transcript FindSpeakableText can read — the same
    // guard FindSpeakableText itself has, checked first here so TurnSounds
    // knows to fall back to a chime rather than trying and failing to find
    // text for an orb kind that never has any.
    [AvaloniaFact]
    public async Task ARemoteControlOrbReportsNothingSpokenRatherThanAttemptingATranscriptWalk()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.RemoteControl, State = "idle" });

        Assert.False(await orb.SpeakTurnSummaryAsync());
    }

    [AvaloniaFact]
    public async Task AGatewayOrbWithOpenClawDisabledFiresTheRemotePathWithoutThrowing()
    {
        var wasEnabled = OrbweaverSettings.OpenClawEnabled;
        try
        {
            OrbweaverSettings.OpenClawEnabled = false;

            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.OpenClaw, State = "idle", Title = "Nova"
            });

            // OpenClawEnabled = false means LastAssistantTextAsync's callee,
            // ChatFor, returns null immediately — the "nothing to say" half
            // of the remote branch.
            Assert.False(await orb.SpeakTurnSummaryAsync());
        }
        finally
        {
            OrbweaverSettings.OpenClawEnabled = wasEnabled;
        }
    }

    // QA round 2, finding 7: SpeakTurnSummaryRemoteAsync's own null-status
    // arm, the sibling of ANeverInitialisedOrbReportsNothingSpokenRatherThanThrowing
    // above but for the remote path specifically — `_lastStatus?.Title ?? ""`
    // has never seen `_lastStatus` itself be null, only a non-null status
    // with a real title. An orb SessionManager has created but not yet run
    // an UpdateFrom against is exactly the theoretical-today, real-tomorrow
    // shape the comment on that test already names.
    [AvaloniaFact]
    public async Task ANeverInitialisedOrbsRemotePathUsesAnEmptyTitleRatherThanThrowing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        Assert.False(await orb.SpeakTurnSummaryRemoteAsync());
    }

    // SpeakTurnSummaryRemoteAsync driven directly and awaited, the same
    // pattern OrbWindowSpeakTests uses for SpeakRemoteAsync: a real
    // (in-memory) history entry found synchronously, so
    // LastAssistantTextAsync returns without its own poll loop and the
    // Dispatcher.UIThread.Post(...) line actually runs and is then pumped
    // within this same test, so the posted SpeechRequest.SpeakTurnSummary
    // call executes here rather than sitting queued for whichever test
    // pumps the shared dispatcher next.
    //
    // CB-168: this test (and OrbWindowSpeakTests' identically-shaped one)
    // is what made Warren's speakers say "hello from the agent" — the
    // posted call ran, unseamed, on a later test's own dispatcher pump.
    // TextToSpeech.SilenceForTests now makes that impossible regardless of
    // when it runs; this test additionally pumps and seams it here so it
    // asserts what it actually scheduled.
    [AvaloniaFact]
    public async Task SpeakTurnSummaryRemoteAsyncFindsARealHistoryEntryAndSchedulesTheRead()
    {
        var wasEnabled = OrbweaverSettings.OpenClawEnabled;
        var agent = "nova" + Guid.NewGuid().ToString("N")[..8];
        var sessionId = $"openclaw:agent:{agent}:discord:channel:1";

        var uttered = new List<(string Text, TextToSpeech.VoiceOption? Voice, double? Rate)>();
        SpeechRequest.UtteranceForTests = (text, voice, rate) => uttered.Add((text, voice, rate));

        try
        {
            OrbweaverSettings.OpenClawEnabled = true;

            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor(sessionId, "Nova")!;
            chat.SetHistory(new[]
            {
                new HistoryTurn(ChatRole.Assistant, "hello from the agent", null, "",
                    DateTimeOffset.UtcNow, null, null),
            });

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.OpenClaw, State = "idle", Title = "Nova"
            });

            Assert.True(await orb.SpeakTurnSummaryRemoteAsync());

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Single(uttered);
            Assert.Equal("hello from the agent", uttered[0].Text);
        }
        finally
        {
            SpeechRequest.UtteranceForTests = null;
            OrbweaverSettings.OpenClawEnabled = wasEnabled;
        }
    }

    // --- whose account (CB-248) ----------------------------------------------

    // The user's own scenario: two terminal sessions, one on the default
    // account and one on a second, each finishing a turn. Each summary has to
    // run on its own session's account — before this, both ran on whichever
    // account Buddy itself had inherited. The two roots exist on disk because
    // AccountDirFor refuses one that does not; a session the scan does not
    // know is the third case, and keeps the old inheriting behaviour.
    [AvaloniaFact]
    public void EachSessionsSummaryRunsOnThatSessionsAccount()
    {
        var home = Directory.CreateTempSubdirectory("cb248-ui-").FullName;
        try
        {
            var accountA = Path.Combine(home, ".claude");
            var accountB = Path.Combine(home, ".claude-b");
            Directory.CreateDirectory(accountA);
            Directory.CreateDirectory(accountB);

            var transcripts = new Dictionary<string, string>
            {
                ["session-a"] = Path.Combine(accountA, "projects", "proj", "a.jsonl"),
                ["session-b"] = Path.Combine(accountB, "projects", "proj", "b.jsonl"),
            };
            SpeechRequest.TranscriptPathForTests =
                id => id is not null && transcripts.TryGetValue(id, out var path) ? path : null;

            var billed = new List<(string Reply, string? Account)>();
            SpeechSummary.AccountSummarizerForTests = (reply, account) =>
            {
                billed.Add((reply, account));
                return Task.FromResult<string?>("Done.");
            };
            SpeechRequest.UtteranceForTests = (_, _, _) => { };

            var replyA = "A: " + LongReply();
            var replyB = "B: " + LongReply();
            var replyUnknown = "?: " + LongReply();
            SpeechRequest.SpeakTurnSummary(replyA, "session-a");
            SpeechRequest.SpeakTurnSummary(replyB, "session-b");
            SpeechRequest.SpeakTurnSummary(replyUnknown, "session-not-scanned");

            Assert.Equal(
                new (string, string?)[] { (replyA, accountA), (replyB, accountB), (replyUnknown, null) },
                billed);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // With no seam, the lookup asks the live scan; a test host has none, so
    // the answer is "unknown" rather than a throw.
    [AvaloniaFact]
    public void WithNoScanRunningASessionsTranscriptIsUnknown() =>
        Assert.Null(SpeechRequest.TranscriptPathOf("session-a"));
}
