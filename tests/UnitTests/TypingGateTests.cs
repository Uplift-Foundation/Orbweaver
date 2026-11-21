using Orbweaver;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-307 addendum: whether text may be typed into a session's terminal. The
// screens are real captures (PaneFixtures); the record and the capture are
// injected and counted, because the order of the two reads is part of the
// rule — the record is asked first and can refuse without touching tmux.
public class TypingGateTests
{
    private static readonly SessionStatus Status = new() { Title = "lawyer-search" };

    private sealed class Reads
    {
        public string? Record = "idle";
        public string? Screen = PaneFixtures.MacPromptAfterEsc;
        public bool Throw;
        public int Captures;

        public Task<TypingVerdict> Judge(bool readable) => TypingGate.JudgeAsync(
            Status,
            _ => Record,
            _ =>
            {
                Captures++;
                return Throw ? throw new InvalidOperationException("tmux went away") : Task.FromResult(Screen);
            },
            readable);
    }

    [Fact]
    public async Task ARecordSayingWaitingRefusesWithoutReadingTheScreen()
    {
        var reads = new Reads { Record = "waiting" };

        var verdict = await reads.Judge(readable: true);

        Assert.False(verdict.Allowed);
        Assert.Equal(TypingGate.WaitingWhy, verdict.Why);
        Assert.Equal(0, reads.Captures);
    }

    [Fact]
    public async Task APaneAtItsPromptIsTypedInto()
    {
        var reads = new Reads();

        Assert.True((await reads.Judge(readable: true)).Allowed);
        Assert.Equal(1, reads.Captures);
    }

    [Fact]
    public async Task ADialogRefusesAndNamesIt()
    {
        var verdict = await new Reads { Screen = PaneFixtures.MiniExitDialog }.Judge(readable: true);

        Assert.False(verdict.Allowed);
        Assert.StartsWith("it is showing a dialog: ", verdict.Why);
        Assert.Contains(ChatTranscript.ParseDialog(PaneFixtures.MiniExitDialog)!.Title, verdict.Why);
    }

    [Fact]
    public async Task TheMinisQuestionDialogRefusesAndNamesItsQuestion()
    {
        // The openclaw pane as it really was: a five-option question with the
        // session's titled rule below it. Refused, never read as a prompt.
        var reads = new Reads { Screen = PaneFixtures.MiniOpenclawQuestionDialog };

        var verdict = await reads.Judge(readable: true);

        Assert.False(verdict.Allowed);
        Assert.Equal(TypingGate.DialogWhy(ChatTranscript.ParseDialog(PaneFixtures.MiniOpenclawQuestionDialog)!.Title),
            verdict.Why);
        Assert.EndsWith("21 automations?", verdict.Why);
    }

    [Theory]
    [MemberData(nameof(PermissionsTabs))]
    public async Task EveryPermissionsTabRefuses(string screen)
    {
        // Whether the parser calls a tab a dialog or nothing at all, it is not
        // a prompt, and the gate refuses either way.
        var verdict = await new Reads { Screen = screen }.Judge(readable: true);

        Assert.False(verdict.Allowed);
        Assert.NotNull(verdict.Why);
    }

    public static TheoryData<string> PermissionsTabs => new()
    {
        PaneFixtures.PermissionsAllow, PaneFixtures.PermissionsAsk, PaneFixtures.PermissionsDeny,
        PaneFixtures.PermissionsAutoMode, PaneFixtures.PermissionsWorkspace,
        PaneFixtures.PermissionsRecentlyBlockedEmpty,
        PaneFixtures.MiniRecentlyBlockedPopulatedStripped,
    };

    // Each refusal says which check fired, so a report from a machine nobody
    // here can see names its cause. Never any of the screen's text.
    [Fact]
    public async Task AScreenThatIsNeitherRefusesAsUnreadableAndSaysWhy()
    {
        var verdict = await new Reads { Screen = "⏺ Working on it…\n✻ Cogitating" }.Judge(readable: true);

        Assert.Equal((false, TypingGate.UnreadableWhy), (verdict.Allowed, verdict.Why));
        Assert.Equal("lines=2 check=no-prompt-mark", verdict.Diagnostic);
    }

    [Fact]
    public async Task ACaptureThatReturnsNothingRefusesAndSaysSo()
    {
        var verdict = await new Reads { Screen = null }.Judge(readable: true);

        Assert.Equal((false, TypingGate.UnreadableWhy), (verdict.Allowed, verdict.Why));
        Assert.Equal("capture=null", verdict.Diagnostic);
    }

    [Fact]
    public async Task ACaptureThatThrowsRefusesAndSaysSo()
    {
        var verdict = await new Reads { Throw = true }.Judge(readable: true);

        Assert.Equal((false, TypingGate.UnreadableWhy), (verdict.Allowed, verdict.Why));
        // The exception's type and never its message, which can carry a path.
        Assert.Equal("capture=threw:InvalidOperationException", verdict.Diagnostic);
    }

    [Fact]
    public async Task AnAllowedPromptStillSaysHowItRead() =>
        Assert.Equal("lines=45 check=prompt", (await new Reads().Judge(readable: true)).Diagnostic);

    [Fact]
    public async Task AnUnreadableRouteIsTypedIntoWhenTheRecordIsQuiet()
    {
        // iTerm2, Terminal.app, Kitty, WezTerm, the Windows console: no screen
        // to read, so only the record guards them. Nothing is captured.
        var reads = new Reads { Screen = PaneFixtures.PermissionsAllow };

        Assert.Equal(TypingVerdict.Allow, await reads.Judge(readable: false));
        Assert.Equal(0, reads.Captures);
    }

    [Fact]
    public async Task AnUnreadableRouteIsStillRefusedWhenTheRecordSaysWaiting() =>
        Assert.Equal(new TypingVerdict(false, TypingGate.WaitingWhy),
            await new Reads { Record = "waiting" }.Judge(readable: false));

    [Theory]
    [InlineData("busy")]
    [InlineData("idle")]
    [InlineData(null)]
    [InlineData("Waiting")]
    public async Task OnlyAnExactWaitingIsTrusted(string? record)
    {
        // The negative control from the mini: lawyer-search's record said
        // `busy` for two days while it sat at its prompt. Not `waiting`, so it
        // decides nothing, and the screen does.
        var reads = new Reads { Record = record };

        Assert.True((await reads.Judge(readable: true)).Allowed);
        Assert.Equal(1, reads.Captures);
    }

    // The two mini screens the installed build was reported refusing. Both
    // read as prompts here, so the report's cause was not these bytes; they
    // are pinned so it never becomes them.
    [Theory]
    [MemberData(nameof(MiniPrompts))]
    public async Task TheMinisRealPromptsAreTypedInto(string screen)
    {
        var verdict = await new Reads { Screen = screen }.Judge(readable: true);

        Assert.True(verdict.Allowed, verdict.Diagnostic);
        Assert.EndsWith("check=prompt", verdict.Diagnostic);
    }

    public static TheoryData<string> MiniPrompts => new()
    {
        PaneFixtures.MiniUpdateLinePrompt, PaneFixtures.MiniTeamListPrompt,
    };
}

// The one helper every typed path calls: TypingGate.GuardedTypeAsync.
public class GuardedTypeTests
{
    private static readonly SessionStatus Status = new() { Title = "openclaw" };

    private static Task NoWait(TimeSpan _) => Task.CompletedTask;

    private sealed class Pane
    {
        public List<string> Typed { get; } = new();
        public int Captures;
        public string? Screen = PaneFixtures.MacPromptAfterEsc;

        public Task<bool> Type(SessionStatus _, string text)
        {
            Typed.Add(text);
            return Task.FromResult(true);
        }

        public Task<string?> Capture(SessionStatus _)
        {
            Captures++;
            return Task.FromResult(Screen);
        }
    }

    [Fact]
    public async Task WithNeitherReadWiredItTypesAsItAlwaysDid()
    {
        // The back-compat contract: a harness built before the gate types.
        var pane = new Pane { Screen = PaneFixtures.PermissionsAllow };

        Assert.Equal((true, (string?)null, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, null, null, null));
        Assert.Equal(new[] { "hi" }, pane.Typed);
        Assert.Equal(0, pane.Captures);
    }

    [Fact]
    public async Task ARefusalTypesNothingAndSaysWhy()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };

        var (typed, refusal, diagnostic) = await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, delay: NoWait);

        Assert.False(typed);
        Assert.StartsWith("it is showing a dialog: ", refusal);
        Assert.StartsWith("lines=", diagnostic);
        Assert.Empty(pane.Typed);
    }

    [Fact]
    public async Task APromptTypes()
    {
        var pane = new Pane();

        Assert.Equal((true, (string?)null, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true));
        Assert.Equal(1, pane.Captures);
    }

    [Fact]
    public async Task ARecordAloneStillGuards()
    {
        // Only the record wired: there is no screen, so the route counts as
        // unreadable; waiting refuses and anything else types.
        var pane = new Pane();

        Assert.Equal((false, (string?)TypingGate.WaitingWhy, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "waiting", null, null));
        Assert.Equal((true, (string?)null, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", null, null));
    }

    [Fact]
    public async Task ACaptureAloneReadsTheScreenUnlessTheRouteSaysItCannot()
    {
        var pane = new Pane { Screen = PaneFixtures.PermissionsAllow };

        // canReadPane null: a capture was supplied, so it is believed.
        Assert.False((await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, null, pane.Capture, null, delay: NoWait)).Typed);
        Assert.Equal(9, pane.Captures); // first look + 8 re-judges

        // The route says unreadable (iTerm2 and the rest): not captured, typed.
        Assert.True((await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, null, pane.Capture, _ => false)).Typed);
        Assert.Equal(9, pane.Captures);
    }

    // CB-307 shadow mode: the verdict is logged and the text typed anyway;
    // with no sink wired it still types, and off it refuses as before.
    [Fact]
    public async Task ShadowModeTypesWhateverTheVerdictAndLogsIt()
    {
        var pane = new Pane { Screen = PaneFixtures.PermissionsAllow };
        var log = new List<string>();

        Assert.Equal((true, (string?)null, (string?)null), await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, () => true, (what, _) => log.Add(what), "s1", NoWait));
        Assert.Equal(new[] { "remote-typing-refused" }, log);

        Assert.True((await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, () => true, delay: NoWait)).Typed);

        Assert.False((await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, () => false, (what, _) => log.Add(what), delay: NoWait)).Typed);
        Assert.Single(log);
        Assert.Equal(new[] { "hi", "hi" }, pane.Typed);
    }

    [Fact]
    public async Task NoCaptureMeansNoScreenEvenOnAReadableRoute() =>
        Assert.True((await TypingGate.JudgeAsync(Status, _ => "idle", null, paneReadable: true)).Allowed);

    [Fact]
    public async Task ShadowModeLogsAnAllowedVerdictAsAllowed()
    {
        var pane = new Pane { Screen = PaneFixtures.MacPromptAfterEsc };
        var log = new List<string>();

        Assert.True((await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true,
            () => true, (what, _) => log.Add(what), "s1")).Typed);
        Assert.Equal(new[] { "remote-typing-allowed" }, log);
    }

    [Fact]
    public async Task AShadowLineWithNoSessionNameSaysSo()
    {
        var pane = new Pane { Screen = PaneFixtures.PermissionsAllow };
        var details = new List<string>();

        await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true,
            () => true, (_, detail) => details.Add(detail), delay: NoWait);

        Assert.StartsWith("session=? ", Assert.Single(details));
    }

    // CB-403: waiting for the prompt. A screen that is a dialog or unreadable
    // is re-judged every 250 ms for up to 2 s, on a fake clock here.
    [Fact]
    public async Task ADialogThatClearsWithinTheWindowTypesOnceItReadsAsAPrompt()
    {
        var pane = new Pane { Screen = PaneFixtures.PermissionsAllow };
        var waits = new List<TimeSpan>();

        Task Wait(TimeSpan t)
        {
            waits.Add(t);
            if (waits.Count == 3) pane.Screen = PaneFixtures.MacPromptAfterEsc;
            return Task.CompletedTask;
        }

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, delay: Wait);

        Assert.True(typed);
        Assert.Null(refusal);
        Assert.Equal(new[] { "hi" }, pane.Typed);
        Assert.Equal(3, waits.Count);
        Assert.All(waits, t => Assert.Equal(TimeSpan.FromMilliseconds(250), t));
        Assert.Equal(4, pane.Captures);
    }

    [Fact]
    public async Task AScreenThatNeverReadsAsAPromptRefusesAfterTwoSeconds()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };
        var waited = TimeSpan.Zero;

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, delay: t => { waited += t; return Task.CompletedTask; });

        Assert.False(typed);
        Assert.StartsWith("it is showing a dialog: ", refusal);
        Assert.Empty(pane.Typed);
        Assert.Equal(TimeSpan.FromSeconds(2), waited);
        Assert.Equal(9, pane.Captures);
    }

    [Fact]
    public async Task AnUnreadableScreenIsWaitedOnToo()
    {
        var pane = new Pane { Screen = null };
        var waits = 0;

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, delay: _ => { waits++; return Task.CompletedTask; });

        Assert.False(typed);
        Assert.Equal(TypingGate.UnreadableWhy, refusal);
        Assert.Equal(8, waits);
    }

    [Fact]
    public async Task AWaitingRecordRefusesAtOnceWithoutWaiting()
    {
        var pane = new Pane();
        var waits = 0;

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "waiting", pane.Capture, _ => true, delay: _ => { waits++; return Task.CompletedTask; });

        Assert.False(typed);
        Assert.Equal(TypingGate.WaitingWhy, refusal);
        Assert.Equal(0, waits);
        Assert.Equal(0, pane.Captures);
    }

    [Fact]
    public async Task APromptOnTheFirstLookTypesWithoutWaiting()
    {
        var pane = new Pane();
        var waits = 0;

        Assert.True((await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, delay: _ => { waits++; return Task.CompletedTask; })).Typed);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task TheDefaultClockIsRealTime()
    {
        // delay null falls back to Task.Delay; a prompt on the first look never
        // reaches it, and a refusing one is covered above on the fake clock.
        var pane = new Pane();
        Assert.True((await TypingGate.JudgeWithWaitAsync(Status, _ => "idle", pane.Capture, true)).Allowed);
    }

    // QA round 1 (CB-403). Prompt -> Dialog -> Prompt inside the window: the
    // first Prompt read types and the waiting stops there.
    [Fact]
    public async Task AScreenThatFlipsPromptDialogPromptTypesOnTheFirstPromptRead()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };
        var waits = 0;

        Task Wait(TimeSpan _)
        {
            waits++;
            // After the 1st wait the screen is a prompt; after the 2nd a dialog
            // again — which must never be read, because typing already happened.
            pane.Screen = waits == 1 ? PaneFixtures.MacPromptAfterEsc : PaneFixtures.MiniExitDialog;
            return Task.CompletedTask;
        }

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, delay: Wait);

        Assert.True(typed);
        Assert.Null(refusal);
        Assert.Equal(1, waits);
        Assert.Equal(2, pane.Captures);
        Assert.Equal(new[] { "hi" }, pane.Typed);
    }

    // Shadow on with a wait: retries do not log; the one verdict after the
    // full wait does, and the text is typed whatever it said.
    [Fact]
    public async Task ShadowOnLogsOneRefusalAfterTheFullWaitAndStillTypes()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };
        var log = new List<(string What, string Detail)>();
        var waits = 0;

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, () => true,
            (what, detail) => log.Add((what, detail)), "s1", _ => { waits++; return Task.CompletedTask; });

        Assert.True(typed);
        Assert.Null(refusal);
        Assert.Equal(8, waits);
        var line = Assert.Single(log);
        Assert.Equal("remote-typing-refused", line.What);
        Assert.Contains("shadow=1", line.Detail);
    }

    [Fact]
    public async Task ShadowOnLogsOneAllowedWhenADialogClearsNotOneLinePerRetry()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };
        var log = new List<string>();
        var waits = 0;

        Assert.True((await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true, () => true,
            (what, _) => log.Add(what), "s1",
            _ => { if (++waits == 3) pane.Screen = PaneFixtures.MacPromptAfterEsc; return Task.CompletedTask; })).Typed);

        Assert.Equal(new[] { "remote-typing-allowed" }, log);
    }

    // A `waiting` record that shows up on a retry refuses at once, with no
    // further delays.
    [Fact]
    public async Task AWaitingRecordThatAppearsOnARetryRefusesAtOnce()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };
        var reads = 0;
        var waits = 0;

        var (typed, refusal, _) = await TypingGate.GuardedTypeAsync(
            Status, "hi", pane.Type, _ => ++reads >= 2 ? "waiting" : "idle", pane.Capture, _ => true,
            delay: _ => { waits++; return Task.CompletedTask; });

        Assert.False(typed);
        Assert.Equal(TypingGate.WaitingWhy, refusal);
        Assert.Equal(1, waits);
        Assert.Empty(pane.Typed);
    }

    [Fact]
    public void TheSlashReasonWrapsTheGatesOwn() =>
        Assert.Equal("the terminal there isn't at its prompt (it is showing a dialog: X)",
            TypingGate.NotAtPromptReason(TypingGate.DialogWhy("X")));
}

public class CanReadPaneTests
{
    [Fact]
    public void ASessionWithNoPaneHasNoScreenToRead()
    {
        // The other arm needs a tmux binary on PATH, which a CI runner is not
        // promised; the route rule itself is TerminalTyping's and is tested there.
        Assert.False(TerminalFocuser.CanReadPane(new SessionStatus()));
        Assert.False(TerminalFocuser.CanReadPane(null));
    }
}
