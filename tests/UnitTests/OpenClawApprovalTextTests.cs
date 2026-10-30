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
    public void TheOptionsAreExactlyDenyThenAllowOnceEachBoundToItsApproval()
    {
        var options = OpenClawApprovalText.Options("ap-1");

        Assert.Equal(new[] { "deny", "allow-once" }, options.Select(o => o.Key));
        Assert.Equal(new[] { "Deny", "Allow once" }, options.Select(o => o.Label));
        Assert.All(options, o => Assert.Equal("ap-1", o.ApprovalId));
        Assert.DoesNotContain(options, o => o.Key.Contains("always"));
    }

    // A request that could not be shown safely can only be refused.
    [Fact]
    public void ADenyOnlyPromptOffersJustDeny()
    {
        var options = OpenClawApprovalText.Options("ap-1", denyOnly: true);

        Assert.Equal(new[] { "deny" }, options.Select(o => o.Key));
        Assert.Equal("ap-1", options[0].ApprovalId);
    }

    [Fact]
    public void AnUnsafeRequestShowsNothingOfItAndOffersDenyOnly()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "rm -rf /", "/", "box", unsafeToShow: true);

        Assert.Equal("A request couldn't be shown safely; answer it on the gateway", prompt.Title);
        Assert.DoesNotContain("rm", prompt.Title);
        Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));
        Assert.True(prompt.Deliberate);
    }

    // An approval prompt is deliberate: its options are armed, not instant.
    [Fact]
    public void AnApprovalPromptIsDeliberate() =>
        Assert.True(OpenClawApprovalText.Prompt("ap-1", "Nova", "true", null, null).Deliberate);

    [Fact]
    public void ThePromptNamesTheAgentTheCommandAndWhere()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "ls /home/op/workspace | wc -l", "/home/op/workspace", "gateway");

        Assert.Equal("Nova wants to run: ls /home/op/workspace | wc -l\nin /home/op/workspace on gateway", prompt.Title);
        Assert.Equal(new[] { "deny", "allow-once" }, prompt.Options.Select(o => o.Key));
    }

    [Fact]
    public void ThePromptWithNoLocationIsOneLine()
    {
        Assert.Equal("Nova wants to run: true", OpenClawApprovalText.Prompt("ap-1", "Nova", "true", null, null).Title);
        Assert.Equal("Nova wants to run: true\nin /tmp", OpenClawApprovalText.Prompt("ap-1", "Nova", "true", "/tmp", "").Title);
        Assert.Equal("Nova wants to run: true\non box", OpenClawApprovalText.Prompt("ap-1", "Nova", "true", " ", "box").Title);
    }

    [Fact]
    public void ABlankAgentOrCommandIsSaidPlainly()
    {
        Assert.Equal("An agent wants to run: (no command shown)", OpenClawApprovalText.Prompt("ap-1", "", "  ", null, null).Title);
    }

    // The command is the thing a person is deciding on, so it is shown on one line,
    // with nothing in it able to fake a second or push the buttons away.
    [Fact]
    public void ACommandSprawlingOverLinesIsShownOnOneAndControlCharactersAreGone()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "echo hi\n\n  && rm -rf /\u0007\tnow", null, null);

        Assert.Equal("Nova wants to run: echo hi && rm -rf / now", prompt.Title);
        Assert.DoesNotContain('\u0007', prompt.Title);
    }

    // What is cut is counted, so a long command cannot look shorter than it is.
    [Fact]
    public void AnOverlongCommandIsCutAndSaysHowMuchWasLeftOut()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", new string('x', 1000), null, null);

        Assert.Equal(
            "Nova wants to run: " + new string('x', 400) + "… (600 more characters)", prompt.Title);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  a   b  ", "a b")]
    [InlineData("tab\tsep", "tab sep")]
    public void CleanSingleSpacesAndStripsControls(string? input, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.Clean(input, 50));

    [Fact]
    public void CleanCountsWhatItCutAndDoesNotCountTrailingSpace()
    {
        Assert.Equal("aaaaa… (99995 more characters)", OpenClawApprovalText.Clean(new string('a', 100_000), 5));
        Assert.Equal("aaaaa", OpenClawApprovalText.Clean("aaaaa   ", 5));
        Assert.Equal("aaaaa… (1 more characters)", OpenClawApprovalText.Clean("aaaaab", 5));
    }

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
