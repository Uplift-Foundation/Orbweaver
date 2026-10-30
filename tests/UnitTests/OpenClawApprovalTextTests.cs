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
    [InlineData("echo hi\n&& rm -rf /")]    // Cc newline
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
        Assert.Equal("Already decided elsewhere: denied", OpenClawApprovalText.AlreadyDecided("denied"));
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
        Assert.Equal("Dropped by the gateway", OpenClawApprovalText.Ended("pending", "x\u202Ey"));
        Assert.Equal(" is waiting for an approval", OpenClawApprovalText.RoomLine("Qu\u0007ill"));
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
}
