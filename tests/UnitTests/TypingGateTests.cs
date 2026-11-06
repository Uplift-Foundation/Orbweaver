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

        Assert.Equal(TypingVerdict.Allow, await reads.Judge(readable: true));
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
        PaneFixtures.PermissionsRecentlyBlockedEmpty, PaneFixtures.PermissionsPermissionsTab,
        PaneFixtures.MiniRecentlyBlockedPopulatedStripped,
    };

    [Fact]
    public async Task AScreenThatIsNeitherRefusesAsUnreadable()
    {
        var verdict = await new Reads { Screen = "⏺ Working on it…\n✻ Cogitating" }.Judge(readable: true);

        Assert.Equal(new TypingVerdict(false, TypingGate.UnreadableWhy), verdict);
    }

    [Fact]
    public async Task ACaptureThatReturnsNothingRefuses() =>
        Assert.Equal(new TypingVerdict(false, TypingGate.UnreadableWhy),
            await new Reads { Screen = null }.Judge(readable: true));

    [Fact]
    public async Task ACaptureThatThrowsRefuses() =>
        Assert.Equal(new TypingVerdict(false, TypingGate.UnreadableWhy),
            await new Reads { Throw = true }.Judge(readable: true));

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

        Assert.Equal(TypingVerdict.Allow, await reads.Judge(readable: true));
        Assert.Equal(1, reads.Captures);
    }
}

// The one helper every typed path calls: TypingGate.GuardedTypeAsync.
public class GuardedTypeTests
{
    private static readonly SessionStatus Status = new() { Title = "openclaw" };

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

        Assert.Equal((true, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, null, null, null));
        Assert.Equal(new[] { "hi" }, pane.Typed);
        Assert.Equal(0, pane.Captures);
    }

    [Fact]
    public async Task ARefusalTypesNothingAndSaysWhy()
    {
        var pane = new Pane { Screen = PaneFixtures.MiniExitDialog };

        var (typed, refusal) = await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true);

        Assert.False(typed);
        Assert.StartsWith("it is showing a dialog: ", refusal);
        Assert.Empty(pane.Typed);
    }

    [Fact]
    public async Task APromptTypes()
    {
        var pane = new Pane();

        Assert.Equal((true, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", pane.Capture, _ => true));
        Assert.Equal(1, pane.Captures);
    }

    [Fact]
    public async Task ARecordAloneStillGuards()
    {
        // Only the record wired: there is no screen, so the route counts as
        // unreadable; waiting refuses and anything else types.
        var pane = new Pane();

        Assert.Equal((false, TypingGate.WaitingWhy),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "waiting", null, null));
        Assert.Equal((true, (string?)null),
            await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, _ => "idle", null, null));
    }

    [Fact]
    public async Task ACaptureAloneReadsTheScreenUnlessTheRouteSaysItCannot()
    {
        var pane = new Pane { Screen = PaneFixtures.PermissionsAllow };

        // canReadPane null: a capture was supplied, so it is believed.
        Assert.False((await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, null, pane.Capture, null)).Typed);
        Assert.Equal(1, pane.Captures);

        // The route says unreadable (iTerm2 and the rest): not captured, typed.
        Assert.True((await TypingGate.GuardedTypeAsync(Status, "hi", pane.Type, null, pane.Capture, _ => false)).Typed);
        Assert.Equal(1, pane.Captures);
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
