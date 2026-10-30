using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6, the wording of the approval prompt and each way it ends. Pure,
// so each sentence and each bound is one call.
public class OpenClawApprovalTextTests
{
    // The options are the client's, fixed: exactly deny then allow-once, each bound
    // to the approval it was drawn for, so a gateway cannot name an option this app
    // never meant to send, and allow-always is not among them.
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
        Assert.Equal("Nova wants to run: true\non box", OpenClawApprovalText.Prompt("ap-1", "Nova", "true", "", "box").Title);
    }

    [Fact]
    public void ABlankAgentOrCommandIsSaidPlainly() =>
        Assert.Equal("An agent wants to run: (no command shown)", OpenClawApprovalText.Prompt("ap-1", "", "", null, null).Title);

    // An approval prompt is deliberate: its options are armed, not instant.
    [Fact]
    public void AnApprovalPromptIsDeliberate() =>
        Assert.True(OpenClawApprovalText.Prompt("ap-1", "Nova", "true", null, null).ArmedActivation);

    // The command is the one thing that must not be reworded for display, so text
    // that cannot be shown as it is is not shown at all.
    [Theory]
    [InlineData("echo hi\r&& rm")]          // Cc lone carriage return
    [InlineData("echo\u000Bhi")]            // Cc vertical tab
    [InlineData("echo hi\u0007")]           // Cc bell
    [InlineData("echo hi\u202E esab")]      // Cf bidi override
    [InlineData("echo\u200Bhi")]            // Cf zero-width space
    [InlineData("echo\u2028hi")]            // Zl
    [InlineData("echo\u2029hi")]            // Zp
    public void ACommandWithAnUnprintableCharacterIsNotShownAndOnlyDenyIsOffered(string command)
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", command, null, null);

        Assert.StartsWith("A request couldn't be shown safely; answer it on the gateway\nRequest id: 'ap-1'", prompt.Title);
        Assert.DoesNotContain("echo", prompt.Title);
        Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));
        Assert.True(prompt.ArmedActivation);
    }

    // Every displayed field is held to it, not only the command.
    [Fact]
    public void AnUnprintableAgentWorkingDirectoryOrHostMakesTheRequestUnsafeToo()
    {
        foreach (var (agent, cwd, host) in new[]
                 {
                     ("No\u0007va", "/tmp", "box"), ("Nova", "/t\nmp", "box"), ("Nova", "/tmp", "bo\u202Ex")
                 })
        {
            var prompt = OpenClawApprovalText.Prompt("ap-1", agent, "true", cwd, host);

            Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));
        }
    }

    // The parser's own judgement is honoured too.
    [Fact]
    public void ARequestTheParserFlaggedUnsafeIsNotShownWhateverItsFieldsSay()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "rm -rf /", "/etc", "box", unsafeToShow: true);

        Assert.StartsWith("A request couldn't be shown safely", prompt.Title);
        Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));

        // Nothing of the request is on the bar: not the command, the place or the agent.
        Assert.DoesNotContain("rm -rf", prompt.Title);
        Assert.DoesNotContain("/etc", prompt.Title);
        Assert.DoesNotContain("Nova", prompt.Title);
        Assert.DoesNotContain("box", prompt.Title);
    }

    [Fact]
    public void OrdinaryPunctuationAndNonLatinTextAreShownAsTheyAre()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "echo 'héllo' | wc -c; ls \"日本語\"", null, null);

        Assert.Equal("Nova wants to run: echo 'héllo' | wc -c; ls \"日本語\"", prompt.Title);
        Assert.Equal(2, prompt.Options.Count);
    }

    // The command is shown whole up to the bound, with both options, and its last
    // character is on the bar.
    [Theory]
    [InlineData(3999)]
    [InlineData(4000)]
    public void ACommandUpToTheBoundIsShownWholeWithBothOptions(int length)
    {
        var command = new string('x', length - 1) + "Z";

        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", command, null, null);

        Assert.Equal("Nova wants to run: " + command, prompt.Title);
        Assert.EndsWith("Z", prompt.Title);
        Assert.Equal(new[] { "deny", "allow-once" }, prompt.Options.Select(o => o.Key));
    }

    // One character past it, the end cannot be read here, so it can only be denied.
    [Fact]
    public void ACommandPastTheBoundIsCutCountedAndOnlyDenyIsOffered()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", new string('x', 4000) + "Z", null, null);

        Assert.Equal(
            "Nova wants to run: " + new string('x', 4000) + "… (1 more characters)\n" + OpenClawApprovalText.TooLongToAllow,
            prompt.Title);
        Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));
        Assert.Equal("ap-1", prompt.Options[0].ApprovalId);
        Assert.True(prompt.ArmedActivation);
    }

    // The parser may already have cut it: its flag is honoured, and the length it
    // saw is what the count is taken from.
    [Fact]
    public void ACommandTheParserTruncatedIsDenyOnlyAndCountedFromItsLength()
    {
        var prompt = OpenClawApprovalText.Prompt(
            "ap-1", "Nova", new string('x', 4000), null, null, commandTruncated: true, commandLength: 9000);

        Assert.Contains("… (5000 more characters)", prompt.Title);
        Assert.Equal(new[] { "deny" }, prompt.Options.Select(o => o.Key));

        // With no length known there is no number to give.
        var unknown = OpenClawApprovalText.Prompt("ap-1", "Nova", "ls", null, null, commandTruncated: true);
        Assert.Contains("ls…\n", unknown.Title.Replace("\r", ""));
        Assert.Equal(new[] { "deny" }, unknown.Options.Select(o => o.Key));
    }

    // The count is of the command as the gateway sent it, measured before any
    // bound: a 4,500-character command the parser cut to 4,000 says 500 are missing,
    // never none.
    [Fact]
    public void TheCountIsFromTheLengthTheGatewaySentNotTheTextHandedOver()
    {
        var prompt = OpenClawApprovalText.Prompt(
            "ap-1", "Nova", new string('x', 4000), null, null, commandTruncated: true, commandLength: 4500);

        Assert.Contains("… (500 more characters)", prompt.Title);
        Assert.DoesNotContain("(0 more", prompt.Title);
    }

    // The other fields are not what is being approved: they keep the small cap.
    [Fact]
    public void TheAgentWorkingDirectoryAndHostKeepTheSmallCap()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "true", new string('d', 300), new string('h', 300));

        Assert.Contains(new string('d', 240) + "… (60 more characters)", prompt.Title);
        Assert.Contains(new string('h', 240) + "… (60 more characters)", prompt.Title);
        Assert.Equal(2, prompt.Options.Count);
    }

    // Not through the middle of a surrogate pair.
    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        var text = new string('a', 239) + "😀" + "bbb";

        Assert.Equal(new string('a', 239) + "… (5 more characters)", OpenClawApprovalText.Cap(text, 240));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  a   b  ", "  a   b  ")]
    public void CapLeavesTextAloneThatFits(string? input, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.Cap(input, 50));

    [Fact]
    public void CapCountsWhatItCut()
    {
        Assert.Equal("aaaaa… (99995 more characters)", OpenClawApprovalText.Cap(new string('a', 100_000), 5));
        Assert.Equal("aaaaa… (1 more characters)", OpenClawApprovalText.Cap("aaaaab", 5));
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
        Assert.Equal("Already decided elsewhere: Denied by another reviewer", OpenClawApprovalText.AlreadyDecided("denied"));
        Assert.Equal("Already decided elsewhere: unknown", OpenClawApprovalText.AlreadyDecided(null));
    }

    [Fact]
    public void ACouldntAnswerCarriesTheReasonOrSaysThereIsNone()
    {
        Assert.Equal("Couldn't answer: not connected", OpenClawApprovalText.CouldntAnswer("not connected"));
        Assert.Equal("Couldn't answer: no reason given", OpenClawApprovalText.CouldntAnswer(""));
    }

    // Gateway text with an unprintable character in it is not repeated back.
    [Fact]
    public void GatewayTextWithAnUnprintableCharacterIsNotRepeated()
    {
        Assert.Equal("Couldn't answer: no reason given", OpenClawApprovalText.CouldntAnswer("bad\u0007"));
        Assert.Equal("Already decided elsewhere: unknown", OpenClawApprovalText.AlreadyDecided("de\nnied"));
        Assert.Equal(OpenClawApprovalText.NoLongerPending, OpenClawApprovalText.Ended("pending", "x\u202Ey"));
        Assert.Equal(" is waiting for an approval", OpenClawApprovalText.RoomLine("Qu\u0007ill"));
    }

    // The gateway declares status and reason as pairs, so the line comes from the
    // status first and the reason only refines it; with no status the reason is read
    // alone, and with neither the request is simply no longer pending.
    [Theory]
    [InlineData("denied", "user", "Denied by another reviewer")]
    [InlineData("allowed", "user", "Allowed by another reviewer")]
    [InlineData("expired", "timeout", "Expired unanswered")]
    [InlineData("expired", null, "Expired unanswered")]
    [InlineData("cancelled", "run-aborted", "The run was stopped")]
    [InlineData("cancelled", "gateway-restart", "Dropped by the gateway (gateway-restart)")]
    [InlineData("cancelled", null, "Cancelled")]
    [InlineData("denied", "no-route", "Dropped by the gateway (no-route)")]
    [InlineData("denied", "storage-corrupt", "Dropped by the gateway (storage-corrupt)")]
    [InlineData("denied", "malformed-verdict", "Dropped by the gateway (malformed-verdict)")]
    [InlineData("denied", "timeout", "Denied by another reviewer")]      // status wins over a mismatched reason
    [InlineData(null, "timeout", "Expired unanswered")]
    [InlineData(null, "run-aborted", "The run was stopped")]
    [InlineData(null, "no-route", "Dropped by the gateway (no-route)")]
    [InlineData("pending", "something-new", "This request is no longer pending")]
    [InlineData(null, null, "This request is no longer pending")]
    public void AnEndWeDidNotCauseReadsAsWhatHappened(string? status, string? reason, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.Ended(status, reason));

    // allow-always is named only as something to do on the gateway, never offered,
    // and the id is shown as data beside the verb, never composed into a line.
    [Fact]
    public void TheElsewhereNoteNamesTheCommandAndQuotesTheId()
    {
        var note = OpenClawApprovalText.Elsewhere("ap-1");

        Assert.Contains("approvals resolve", note);
        Assert.Contains("--deny", note);
        Assert.Contains("--allow-once", note);
        Assert.Contains("--allow-always", note);
        Assert.Contains("'ap-1'", note);
        Assert.DoesNotContain("resolve ap-1", note);
    }

    // A hostile id: shell syntax inside an id is shown escaped and visibly
    // verbatim, not as a pasteable line.
    [Fact]
    public void AHostileIdIsQuotedAndEscapedNotComposed()
    {
        var note = OpenClawApprovalText.Elsewhere("x'; rm -rf ~'");

        Assert.Contains("'x'\\''; rm -rf ~'\\'''", note);
        Assert.DoesNotContain("resolve x", note);
    }

    [Fact]
    public void QuoteIdWritesOutUnprintableCharactersAndKeepsTheRest()
    {
        Assert.Equal("'a\\u{A}b\\u{202E}c😀'", OpenClawApprovalText.QuoteId("a\nb\u202Ec😀"));
        Assert.Equal("''", OpenClawApprovalText.QuoteId(null));
    }

    [Fact]
    public void TheRoomLineNamesTheMember() =>
        Assert.Equal("Quill is waiting for an approval", OpenClawApprovalText.RoomLine("Quill"));

    [Fact]
    public void TheOffSwitchNoteSaysWhatToTurnOn() =>
        Assert.Contains("Allow approving agent actions", OpenClawApprovalText.ApprovingOff);

    // A real command is written over lines and with tabs: those two, in the command
    // only, are shown as they are; CRLF reads as one break.
    [Fact]
    public void ACommandMayCarryLineBreaksAndTabsAndKeepsBothOptions()
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "cat <<EOF\r\n\tone\ntwo\nEOF", null, null);

        Assert.Equal("Nova wants to run: cat <<EOF\n\tone\ntwo\nEOF", prompt.Title);
        Assert.Equal(2, prompt.Options.Count);
    }

    // Other fields get no such allowance.
    [Theory]
    [InlineData("a\nb", "/tmp", "box", null)]
    [InlineData("Nova", "/t\tmp", "box", null)]
    [InlineData("Nova", "/tmp", "bo\nx", null)]
    [InlineData("Nova", "/tmp", "box", "careful\nnow")]
    public void NoOtherFieldMayCarryLineBreaksOrTabs(string agent, string cwd, string host, string? warning)
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", agent, "true", cwd, host, warning: warning);

        Assert.StartsWith(OpenClawApprovalText.UnsafeRequest, prompt.Title);
        Assert.Equal("deny", Assert.Single(prompt.Options).Key);
    }

    // Allow once is offered only where the gateway lists it, or said nothing.
    [Theory]
    [InlineData(null, 2)]
    [InlineData(new[] { "deny", "allow-once" }, 2)]
    [InlineData(new[] { "deny" }, 1)]
    [InlineData(new string[0], 1)]
    public void AllowOnceIsOfferedOnlyWhenTheGatewayAllowsIt(string[]? allowed, int options)
    {
        var prompt = OpenClawApprovalText.Prompt("ap-1", "Nova", "true", null, null, allowedDecisions: allowed);

        Assert.Equal(options, prompt.Options.Count);
        Assert.Equal("deny", prompt.Options[0].Key);
        Assert.DoesNotContain(prompt.Options, o => o.Key == "allow-always");
    }

    [Fact]
    public void TheGatewaysWarningIsShownAsPlainText() =>
        Assert.Equal("Nova wants to run: true\nin /tmp\nruns as root",
            OpenClawApprovalText.Prompt("ap-1", "Nova", "true", "/tmp", null, warning: "runs as root").Title);

    // allow-always by another reviewer is named as such; the lines are reused for
    // an answer that lost the race.
    [Fact]
    public void AnAllowAlwaysBySomeoneElseIsNamedAndTheLinesAreReusedForALostRace()
    {
        Assert.Equal("Allowed always, by another reviewer", OpenClawApprovalText.Ended("allowed", "user", "allow-always"));
        Assert.Equal("Allowed by another reviewer", OpenClawApprovalText.Ended("allowed", "user", "allow-once"));
        Assert.Equal("Already decided elsewhere: Denied by another reviewer", OpenClawApprovalText.AlreadyDecided("denied", "user"));
        Assert.Equal("Already decided elsewhere: Expired unanswered", OpenClawApprovalText.AlreadyDecided("expired", "timeout"));
        Assert.Equal("Already decided elsewhere: unknown", OpenClawApprovalText.AlreadyDecided(null));
    }

    // Optional lines of the prompt: each of cwd, host and the gateway's warning is
    // drawn when it has text and left out when it is null or empty. (A field cannot
    // be capped down to nothing: the caps are far above zero, so a cut field always
    // keeps its first characters.)
    [Theory]
    [InlineData(null, null, null, "Nova wants to run: true")]
    [InlineData("", "", "", "Nova wants to run: true")]
    [InlineData("/tmp", null, null, "Nova wants to run: true\nin /tmp")]
    [InlineData(null, "box", null, "Nova wants to run: true\non box")]
    [InlineData(null, null, "careful", "Nova wants to run: true\ncareful")]
    [InlineData("/tmp", "box", "careful", "Nova wants to run: true\nin /tmp on box\ncareful")]
    public void EachOptionalLineIsDrawnOnlyWhenItHasText(string? cwd, string? host, string? warning, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.Prompt("ap-1", "Nova", "true", cwd, host, warning: warning).Title);

    [Fact]
    public void ALongWorkingDirectoryHostAndWarningAreEachCappedWithACount()
    {
        var title = OpenClawApprovalText.Prompt(
            "ap-1", "Nova", "true", new string('d', 300), new string('h', 300), warning: new string('w', 300)).Title;

        Assert.Equal(3, title.Split('\n').Length);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(title, @"\(\d+ more characters\)").Count);
    }

    // What the gateway said decided an answer that lost the race, for every status
    // it declares, and for one it does not (shown if clean, "unknown" if not).
    [Theory]
    [InlineData("allowed", "user", "Already decided elsewhere: Allowed by another reviewer")]
    [InlineData("denied", "user", "Already decided elsewhere: Denied by another reviewer")]
    [InlineData("denied", "no-route", "Already decided elsewhere: Dropped by the gateway (no-route)")]
    [InlineData("expired", "timeout", "Already decided elsewhere: Expired unanswered")]
    [InlineData("cancelled", "run-aborted", "Already decided elsewhere: The run was stopped")]
    [InlineData("cancelled", null, "Already decided elsewhere: Cancelled")]
    [InlineData("pending", null, "Already decided elsewhere: pending")]
    [InlineData("", null, "Already decided elsewhere: unknown")]
    [InlineData("sta\ntus", null, "Already decided elsewhere: unknown")]
    [InlineData(null, null, "Already decided elsewhere: unknown")]
    public void ALostRaceReusesTheStatusLine(string? status, string? reason, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.AlreadyDecided(status, reason));

    [Theory]
    [InlineData("", "Couldn't answer: no reason given")]
    [InlineData("bad\u0007", "Couldn't answer: no reason given")]
    [InlineData("not connected", "Couldn't answer: not connected")]
    public void ACouldntAnswerNoteSaysWhyOrThatThereIsNoReason(string message, string expected) =>
        Assert.Equal(expected, OpenClawApprovalText.CouldntAnswer(message));
}
