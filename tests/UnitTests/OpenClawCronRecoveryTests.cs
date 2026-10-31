using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-115: recovering a cron-delivered picture's real path once OpenClaw's
// delivery route has stripped both the "MEDIA:" prefix and the directory out
// of the transcript. See OpenClawCronRecovery's own header for the full
// design and the two dead ends (runId/timestamp matching, and a presence
// rather than outcome trigger) it was built from.
public class OpenClawCronRecoveryTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- AutomationOf: the trigger's first conjunct -----------------------

    [Fact]
    public void AutomationOfReadsKindAndJobId()
    {
        var message = Parse("""{"openclawAutomation":{"kind":"cron","jobId":"job-1","runId":"cron:job-1:123"}}""");

        var automation = OpenClawCronRecovery.AutomationOf(message);

        Assert.NotNull(automation);
        Assert.Equal("cron", automation!.Value.Kind);
        Assert.Equal("job-1", automation.Value.JobId);
    }

    // Read faithfully rather than filtered to "cron" here — a caller decides
    // whether the kind it got back is one it cares about. A text-only
    // automation (a "task" kind, say) still parses; only OpenClawSessions'
    // wiring of it into a HistoryTurn narrows to "cron".
    [Fact]
    public void AutomationOfDoesNotFilterOnKind()
    {
        var message = Parse("""{"openclawAutomation":{"kind":"task","jobId":"job-1"}}""");

        var automation = OpenClawCronRecovery.AutomationOf(message);

        Assert.NotNull(automation);
        Assert.Equal("task", automation!.Value.Kind);
    }

    [Fact]
    public void AMessageWithNoOpenclawAutomationIsNull()
    {
        Assert.Null(OpenClawCronRecovery.AutomationOf(Parse("""{"role":"assistant"}""")));
    }

    [Fact]
    public void AnOpenclawAutomationThatIsNotAnObjectIsNull()
    {
        Assert.Null(OpenClawCronRecovery.AutomationOf(Parse("""{"openclawAutomation":"cron"}""")));
    }

    [Fact]
    public void AnOpenclawAutomationMissingKindIsNull()
    {
        Assert.Null(OpenClawCronRecovery.AutomationOf(Parse("""{"openclawAutomation":{"jobId":"job-1"}}""")));
    }

    [Fact]
    public void AnOpenclawAutomationMissingJobIdIsNull()
    {
        Assert.Null(OpenClawCronRecovery.AutomationOf(Parse("""{"openclawAutomation":{"kind":"cron"}}""")));
    }

    [Fact]
    public void ANonObjectCarrierIsNull()
    {
        Assert.Null(OpenClawCronRecovery.AutomationOf(Parse("\"just a string\"")));
    }

    // ---- CandidateBasenameFrom: the trigger's second conjunct --------------

    // CB-272: a cron run can end on a clip as readily as on a picture.
    [Fact]
    public void CandidateBasenameFromAcceptsAClipTrailingToken()
    {
        Assert.Equal("clip.mp4", OpenClawCronRecovery.CandidateBasenameFrom("caption\nclip.mp4"));
        Assert.Equal("clip.mp4", OpenClawCronRecovery.CandidateBasenameFrom("caption\n/x/clip.mp4"));
        Assert.Null(OpenClawCronRecovery.CandidateBasenameFrom("caption\nnotes.txt"));
        Assert.Null(OpenClawCronRecovery.CandidateBasenameFrom("caption\n/x/../clip.mp4"));
        Assert.Null(OpenClawCronRecovery.CandidateBasenameFrom("caption\nsub\\clip.mp4"));
    }


    // The real shape this ticket exists for: a caption, then the bare
    // filename the delivery route left behind once it stripped MEDIA: and
    // the directory.
    [Fact]
    public void ABareFilenameOnItsOwnTrailingLineIsTheCandidate()
    {
        var text = "sunset over the harbour today \U0001F307\n"
                 + "poster_harbour_sunset_10000001.png";

        Assert.Equal(
            "poster_harbour_sunset_10000001.png",
            OpenClawCronRecovery.CandidateBasenameFrom(text));
    }

    // The forward-compatible half of the invariant: a caption paired with a
    // ROOTED path (the shape CB-107 gives its own name) is just as
    // image-shaped as the bare case, via OpenClawSessions.LooksLikeAnImagePath
    // rather than a second predicate here. This is what keeps the trigger's
    // second conjunct evaluating identically whether or not CB-107's own
    // resolution arm exists in the build — what changes across that line is
    // only the third conjunct (whether the picture already resolved), never
    // this one.
    [Fact]
    public void ARootedPathOnItsOwnTrailingLineIsTheCandidateByItsBasename()
    {
        var text = "here you go\n"
                 + "/Users/sample/.openclaw/workspace-agent-two/outputs/"
                 + "poster/poster_barn_porch_10000002.png";

        Assert.Equal(
            "poster_barn_porch_10000002.png",
            OpenClawCronRecovery.CandidateBasenameFrom(text));
    }

    // A ~-prefixed path is the other rooted shape LooksLikeAnImagePath
    // recognises, and its basename comes back the same way.
    [Fact]
    public void ATildePathOnItsOwnTrailingLineIsTheCandidateByItsBasename()
    {
        Assert.Equal(
            "pic.png",
            OpenClawCronRecovery.CandidateBasenameFrom("caption\n~/.openclaw/media/pic.png"));
    }

    // A text-only cron reply — a heartbeat, a status post — must not look
    // image-shaped merely because some word in it happens to be a filename.
    // This is the population the second conjunct exists to exclude.
    [Fact]
    public void AnOrdinaryReplyWithNoTrailingFilenameIsNotACandidate()
    {
        Assert.Null(OpenClawCronRecovery.CandidateBasenameFrom("all systems nominal, nothing to report"));
    }

    // Whitespace-separated, not line-separated: a caption and a filename on
    // the same physical line (separated by a space rather than a newline)
    // still yields the trailing token.
    [Fact]
    public void TheTrailingWhitespaceTokenIsFoundEvenWithoutANewline()
    {
        Assert.Equal("pic.png", OpenClawCronRecovery.CandidateBasenameFrom("here it is pic.png"));
    }

    // Trailing blank lines and spaces are not themselves the candidate — the
    // last *non-blank* token is.
    [Fact]
    public void TrailingBlankLinesDoNotHideTheRealCandidate()
    {
        Assert.Equal(
            "pic.png",
            OpenClawCronRecovery.CandidateBasenameFrom("caption\n\n  pic.png   \n\n"));
    }

    [Fact]
    public void TheExtensionCheckIsCaseInsensitive()
    {
        Assert.Equal("PIC.PNG", OpenClawCronRecovery.CandidateBasenameFrom("caption\nPIC.PNG"));
    }

    // A trailing token with a slash that is not one of LooksLikeAnImagePath's
    // recognised rooted shapes (not "/" or "~/") is neither a bare filename
    // (it contains a separator) nor a recognised rooted path, so it is not a
    // candidate — this is the traversal-refusal behaviour riding along for
    // free rather than something this file re-implements.
    [Fact]
    public void AnUnrootedPathIsNotACandidate()
    {
        Assert.Null(OpenClawCronRecovery.CandidateBasenameFrom("caption\nsome/relative/pic.png"));
    }

    [Fact]
    public void ATrailingTokenWithNoExtensionAtAllIsNotACandidate()
    {
        Assert.Null(OpenClawCronRecovery.CandidateBasenameFrom("caption\nnot-a-picture"));
    }

    [Fact]
    public void BasenameOfStripsAnyDirectory()
    {
        Assert.Equal("pic.png", OpenClawCronRecovery.BasenameOf("/a/b/pic.png"));
        Assert.Equal("pic.png", OpenClawCronRecovery.BasenameOf("pic.png"));
    }

    // ---- RunsFrom / HasMore / NextOffset: reading a cron.runs page --------

    // Drawn from the real shape captured in
    // .cb115-evidence/cron-runs-live-capture.json, with invented ids,
    // filenames and captions — see OpenClawGatewayPayloadTests' own comment
    // for why a public repo's fixtures invent values while keeping a real
    // structure.
    private const string RealShapedPage = """
        {
          "entries": [
            {
              "jobId": "job-abc",
              "summary": "cozy evening 🌙\nMEDIA:/home/agent/outputs/pic-a.png"
            },
            {
              "jobId": "job-abc",
              "summary": "no picture in this one, just a status update"
            },
            {
              "jobId": "job-other",
              "summary": "MEDIA:/home/agent/outputs/pic-z.png"
            }
          ],
          "total": 3,
          "offset": 0,
          "limit": 60,
          "hasMore": false,
          "nextOffset": 3
        }
        """;

    [Fact]
    public void RunsFromReadsEveryEntrysJobIdAndSummary()
    {
        var runs = OpenClawCronRecovery.RunsFrom(Parse(RealShapedPage));

        Assert.Equal(3, runs.Count);
        Assert.Equal("job-abc", runs[0].JobId);
        Assert.Contains("MEDIA:/home/agent/outputs/pic-a.png", runs[0].Summary);
        Assert.Null(OpenClawSessions.LocalMediaPathFrom(runs[1].Summary!));
        Assert.Equal("job-other", runs[2].JobId);
    }

    [Fact]
    public void AnEntryWithNoJobIdIsSkipped()
    {
        var runs = OpenClawCronRecovery.RunsFrom(Parse("""{"entries":[{"summary":"MEDIA:/a.png"}]}"""));
        Assert.Empty(runs);
    }

    [Fact]
    public void AResponseWithNoEntriesArrayIsEmpty()
    {
        Assert.Empty(OpenClawCronRecovery.RunsFrom(Parse("{}")));
        Assert.Empty(OpenClawCronRecovery.RunsFrom(Parse("""{"entries":"not an array"}""")));
    }

    [Fact]
    public void HasMoreReadsTheBooleanFaithfully()
    {
        Assert.True(OpenClawCronRecovery.HasMore(Parse("""{"hasMore":true}""")));
        Assert.False(OpenClawCronRecovery.HasMore(Parse("""{"hasMore":false}""")));
        Assert.False(OpenClawCronRecovery.HasMore(Parse("{}")));
    }

    [Fact]
    public void NextOffsetReadsTheNumberOrNullsWhenAbsentOrWrongShaped()
    {
        Assert.Equal(5, OpenClawCronRecovery.NextOffset(Parse("""{"nextOffset":5}""")));
        Assert.Null(OpenClawCronRecovery.NextOffset(Parse("{}")));
        Assert.Null(OpenClawCronRecovery.NextOffset(Parse("""{"nextOffset":"five"}""")));
    }

    // ---- MediaPathsByBasenameForJob: the actual match ----------------------

    [Fact]
    public void AUniqueBasenameWithinTheJobResolvesToItsPath()
    {
        var runs = new List<OpenClawCronRun>
        {
            new("job-abc", "cozy evening\nMEDIA:/home/agent/outputs/pic-a.png"),
            new("job-abc", "no picture here"),
            new("job-other", "MEDIA:/home/agent/outputs/pic-a.png")
        };

        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-abc");

        Assert.Equal("/home/agent/outputs/pic-a.png", map["pic-a.png"]);
    }

    // Two different runs' full paths sharing one basename inside the SAME
    // job is refused — mapped explicitly to null rather than omitted, so a
    // caller (and this test) can tell "refused" apart from "never seen".
    [Fact]
    public void TwoDifferentPathsSharingABasenameInsideOneJobAreRefused()
    {
        var runs = new List<OpenClawCronRun>
        {
            new("job-abc", "MEDIA:/home/agent/outputs/posters/dup.png"),
            new("job-abc", "MEDIA:/home/agent/outputs/nova/dup.png")
        };

        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-abc");

        Assert.True(map.ContainsKey("dup.png"));
        Assert.Null(map["dup.png"]);
    }

    // The same full path delivered twice (a real second delivery, or a
    // duplicate record) is not a collision — one basename, one path, however
    // many runs said so.
    [Fact]
    public void TheSamePathDeliveredTwiceIsNotAnAmbiguity()
    {
        var runs = new List<OpenClawCronRun>
        {
            new("job-abc", "MEDIA:/home/agent/outputs/pic.png"),
            new("job-abc", "MEDIA:/home/agent/outputs/pic.png")
        };

        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-abc");

        Assert.Equal("/home/agent/outputs/pic.png", map["pic.png"]);
    }

    [Fact]
    public void ABasenameNeverSeenForTheJobIsAbsentFromTheMap()
    {
        var runs = new List<OpenClawCronRun> { new("job-abc", "MEDIA:/home/agent/outputs/pic-a.png") };

        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-abc");

        Assert.False(map.ContainsKey("never-seen.png"));
    }

    [Fact]
    public void ARunWithNoSummaryIsIgnored()
    {
        var runs = new List<OpenClawCronRun> { new("job-abc", null) };

        Assert.Empty(OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-abc"));
    }

    [Fact]
    public void ARunWhoseSummaryHasNoMediaLineContributesNothing()
    {
        var runs = new List<OpenClawCronRun> { new("job-abc", "just a status update, nothing delivered") };

        Assert.Empty(OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-abc"));
    }
}
