using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6, the wording of the approval prompt and each way it ends. Pure,
// so each sentence and each bound is one call.
public class OpenClawApprovalTextTests
{
    // The options are the client's, fixed: exactly deny then allow-once, so a
    // gateway cannot name an option this app never meant to send, and allow-always
    // is not among them.
    [Fact]
    public void TheOptionsAreExactlyDenyThenAllowOnce()
    {
        Assert.Equal(new[] { "deny", "allow-once" }, OpenClawApprovalText.Options.Select(o => o.Key));
        Assert.Equal(new[] { "Deny", "Allow once" }, OpenClawApprovalText.Options.Select(o => o.Label));
        Assert.DoesNotContain(OpenClawApprovalText.Options, o => o.Key.Contains("always"));
    }

    [Fact]
    public void ThePromptNamesTheAgentTheCommandAndWhere()
    {
        var prompt = OpenClawApprovalText.Prompt("Nova", "ls /home/op/workspace | wc -l", "/home/op/workspace", "gateway");

        Assert.Equal("Nova wants to run: ls /home/op/workspace | wc -l\nin /home/op/workspace on gateway", prompt.Title);
        Assert.Same(OpenClawApprovalText.Options, prompt.Options);
    }

    [Fact]
    public void ThePromptWithNoLocationIsOneLine()
    {
        Assert.Equal("Nova wants to run: true", OpenClawApprovalText.Prompt("Nova", "true", null, null).Title);
        Assert.Equal("Nova wants to run: true\nin /tmp", OpenClawApprovalText.Prompt("Nova", "true", "/tmp", "").Title);
        Assert.Equal("Nova wants to run: true\non box", OpenClawApprovalText.Prompt("Nova", "true", " ", "box").Title);
    }

    [Fact]
    public void ABlankAgentOrCommandIsSaidPlainly()
    {
        Assert.Equal("An agent wants to run: (no command shown)", OpenClawApprovalText.Prompt("", "  ", null, null).Title);
    }

    // The command is the thing a person is deciding on, so it is shown on one line,
    // with nothing in it able to fake a second or push the buttons away.
    [Fact]
    public void ACommandSprawlingOverLinesIsShownOnOneAndControlCharactersAreGone()
    {
        var prompt = OpenClawApprovalText.Prompt("Nova", "echo hi\n\n  && rm -rf /\u0007\tnow", null, null);

        Assert.Equal("Nova wants to run: echo hi && rm -rf / now", prompt.Title);
        Assert.DoesNotContain('\u0007', prompt.Title);
    }

    [Fact]
    public void AnOverlongCommandIsCutWithAnEllipsis()
    {
        var prompt = OpenClawApprovalText.Prompt("Nova", new string('x', 1000), null, null);

        Assert.Equal("Nova wants to run: ".Length + OpenClawApprovalText.MaxCommand + 1, prompt.Title.Length);
        Assert.EndsWith("…", prompt.Title);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  a   b  ", "a b")]
    [InlineData("tab\tsep", "tab sep")]
    public void CleanSingleSpacesAndStripsControls(string? input, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.Clean(input, 50));

    [Fact]
    public void CleanStopsAtTheBoundWithoutBuildingTheRest() =>
        Assert.Equal(new string('a', 5) + "…", OpenClawApprovalText.Clean(new string('a', 100_000), 5));

    [Fact]
    public void OurAnswersReadAsWhatWasDone()
    {
        Assert.Equal("Allowed once", OpenClawApprovalText.Answered("allow-once"));
        Assert.Equal("Denied", OpenClawApprovalText.Answered("deny"));
    }

    [Fact]
    public void AnotherReviewerGettingThereFirstSaysWhatTheyDecided()
    {
        Assert.Equal("Already decided elsewhere: denied", OpenClawApprovalText.AlreadyDecided("denied"));
        Assert.Equal("Already decided elsewhere: unknown", OpenClawApprovalText.AlreadyDecided(null));
    }

    [Fact]
    public void ACouldntAnswerCarriesTheReasonOrSaysThereIsNone()
    {
        Assert.Equal("Couldn't answer: not connected", OpenClawApprovalText.CouldntAnswer("not connected"));
        Assert.Equal("Couldn't answer: no reason given", OpenClawApprovalText.CouldntAnswer(" "));
    }

    // Every terminal reason the gateway has, and a decision by someone else.
    [Theory]
    [InlineData("denied", "user", "Denied by another reviewer")]
    [InlineData("allowed", "user", "Allowed by another reviewer")]
    [InlineData("denied", "timeout", "Expired unanswered")]
    [InlineData("denied", "run-aborted", "The run was stopped")]
    [InlineData("denied", "gateway-restart", "Dropped by the gateway (gateway-restart)")]
    [InlineData("denied", "no-route", "Dropped by the gateway (no-route)")]
    [InlineData("denied", "storage-corrupt", "Dropped by the gateway (storage-corrupt)")]
    [InlineData("denied", "malformed-verdict", "Dropped by the gateway (malformed-verdict)")]
    [InlineData("pending", "something-new", "Dropped by the gateway (something-new)")]
    [InlineData(null, null, "Dropped by the gateway")]
    public void AnEndWeDidNotCauseReadsAsWhatHappened(string? status, string? reason, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.Ended(status, reason));

    // allow-always is named only as something to do on the gateway, never offered.
    [Fact]
    public void TheElsewhereNoteGivesTheGatewaysOwnCommands()
    {
        var note = OpenClawApprovalText.Elsewhere("ap-1");

        Assert.Contains("openclaw approvals resolve ap-1 --deny", note);
        Assert.Contains("--allow-once", note);
        Assert.Contains("--allow-always", note);
    }

    [Fact]
    public void TheRoomLineNamesTheMember() =>
        Assert.Equal("Quill is waiting for an approval", OpenClawApprovalText.RoomLine("Quill"));

    [Fact]
    public void TheOffSwitchNoteSaysWhatToTurnOn() =>
        Assert.Contains("Allow approving agent actions", OpenClawApprovalText.ApprovingOff);
}
