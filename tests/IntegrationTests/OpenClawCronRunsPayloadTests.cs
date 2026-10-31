using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-115: cron.runs, end to end against a fixture shaped like a real
// gateway's answer — the seam CLAUDE.md says to cover twice, the same reason
// OpenClawGatewayPayloadTests exists for sessions.list and chat.history. The
// unit suite (tests/UnitTests/OpenClawCronRecoveryTests.cs) checks the
// matching rules a case at a time against small, hand-built entries; this
// checks the whole exchange — a real cron.runs page, carrying every field a
// live gateway actually sent and not merely the two this parser reads, into
// a resolved MEDIA: path.
//
// **Every value in this fixture is invented.** The structure is real — field
// by field from a page captured against a live gateway
// (.cb115-evidence/cron-runs-live-capture.json, kept out of source control
// rather than checked in verbatim) — and the jobId, filenames, captions,
// timestamps and session ids are not, for the same reason
// OpenClawGatewayPayloadTests' fixtures invent theirs: this repository is
// public, the gateway a real cron job posting to a private Discord server,
// and only the shape is what a test needs.
//
// Serialised on the settings collection for the same reason
// OpenClawGatewayPayloadTests is: the one test that calls TurnsFromHistory
// resolves speaker names and colours through the process-wide identity
// table.
[Collection("Settings")]
public class OpenClawCronRunsPayloadTests
{
    // One page, five runs, matching the real envelope's field list —
    // action, completionStatus, delivered, delivery, deliveryStatus,
    // durationMs, jobId, jobName, model, nextRunAtMs, provider, runAtMs,
    // sessionId, sessionKey, status, summary, ts, usage — even though
    // RunsFrom only reads two of them. A parser that broke the moment an
    // unread field appeared would be a worse test of "does this survive a
    // real payload" than one that never declared which fields it needs.
    private const string CronRunsPage = """
        {
          "entries": [
            {
              "ts": 1700000500000,
              "jobId": "job-aaaa1111",
              "action": "finished",
              "status": "ok",
              "completionStatus": "succeeded",
              "summary": "texting you back with a wink 📱 astra_bedphone_texting_1.png\nMEDIA:/home/agent/outputs/astra/astra_bedphone_texting_1.png",
              "runAtMs": 1700000440000,
              "durationMs": 55787,
              "nextRunAtMs": 1700001940000,
              "model": "claude-sonnet-4-6",
              "provider": "claude-cli-example",
              "usage": { "input_tokens": 2, "output_tokens": 1, "total_tokens": 88677 },
              "delivered": true,
              "deliveryStatus": "delivered",
              "delivery": {
                "intended": { "channel": "discord", "to": "user:1", "accountId": "default", "source": "explicit" },
                "resolved": { "ok": true, "channel": "discord", "to": "user:1", "accountId": "default", "source": "explicit" },
                "fallbackUsed": true,
                "delivered": true
              },
              "sessionId": "00000000-0000-4000-8000-000000000101",
              "sessionKey": "agent:main:cron:job-aaaa1111:run:00000000-0000-4000-8000-000000000101",
              "jobName": "astra-drops"
            },
            {
              "ts": 1700002500000,
              "jobId": "job-aaaa1111",
              "action": "finished",
              "status": "ok",
              "completionStatus": "succeeded",
              "summary": "cozy on the couch tonight 🧡 astra_scenesweater_couch_2.png\nMEDIA:/home/agent/outputs/astra/astra_scenesweater_couch_2.png",
              "runAtMs": 1700002440000,
              "durationMs": 57784,
              "nextRunAtMs": 1700003940000,
              "model": "claude-sonnet-4-6",
              "provider": "claude-cli-example",
              "usage": { "input_tokens": 2, "output_tokens": 1, "total_tokens": 92896 },
              "delivered": true,
              "deliveryStatus": "delivered",
              "delivery": {
                "intended": { "channel": "discord", "to": "user:1", "accountId": "default", "source": "explicit" },
                "resolved": { "ok": true, "channel": "discord", "to": "user:1", "accountId": "default", "source": "explicit" },
                "fallbackUsed": true,
                "delivered": true
              },
              "sessionId": "00000000-0000-4000-8000-000000000102",
              "sessionKey": "agent:main:cron:job-aaaa1111:run:00000000-0000-4000-8000-000000000102",
              "jobName": "astra-drops"
            },
            {
              "ts": 1700004500000,
              "jobId": "job-aaaa1111",
              "action": "finished",
              "status": "ok",
              "completionStatus": "succeeded",
              "summary": "just checking in, nothing to share this round",
              "runAtMs": 1700004440000,
              "durationMs": 4021,
              "nextRunAtMs": 1700005940000,
              "model": "claude-sonnet-4-6",
              "provider": "claude-cli-example",
              "usage": { "input_tokens": 2, "output_tokens": 1, "total_tokens": 512 },
              "delivered": false,
              "deliveryStatus": "skipped",
              "sessionId": "00000000-0000-4000-8000-000000000103",
              "sessionKey": "agent:main:cron:job-aaaa1111:run:00000000-0000-4000-8000-000000000103",
              "jobName": "astra-drops"
            },
            {
              "ts": 1700006500000,
              "jobId": "job-bbbb2222",
              "action": "finished",
              "status": "ok",
              "completionStatus": "succeeded",
              "summary": "MEDIA:/home/agent/outputs/other/astra_bedphone_texting_1.png",
              "runAtMs": 1700006440000,
              "durationMs": 12345,
              "model": "claude-sonnet-4-6",
              "provider": "claude-cli-example",
              "sessionId": "00000000-0000-4000-8000-000000000201",
              "sessionKey": "agent:other:cron:job-bbbb2222:run:00000000-0000-4000-8000-000000000201",
              "jobName": "other-job"
            },
            {
              "ts": 1700008500000,
              "jobId": "job-aaaa1111",
              "action": "finished",
              "status": "error",
              "completionStatus": "failed",
              "summary": "the render failed, no picture this time",
              "runAtMs": 1700008440000,
              "durationMs": 900,
              "model": "claude-sonnet-4-6",
              "provider": "claude-cli-example",
              "delivered": false,
              "deliveryStatus": "failed",
              "sessionId": "00000000-0000-4000-8000-000000000104",
              "sessionKey": "agent:main:cron:job-aaaa1111:run:00000000-0000-4000-8000-000000000104",
              "jobName": "astra-drops"
            }
          ],
          "total": 5,
          "offset": 0,
          "limit": 60,
          "hasMore": false,
          "nextOffset": 5
        }
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void APageOfCronRunsIsReadEntryByEntry()
    {
        var runs = OpenClawCronRecovery.RunsFrom(Json(CronRunsPage));

        Assert.Equal(5, runs.Count);
        Assert.Equal("job-aaaa1111", runs[0].JobId);
        Assert.Contains("MEDIA:", runs[0].Summary);

        // Nothing about the fields this parser does not read (delivery,
        // usage, jobName, …) stops the ones it does from coming through.
        Assert.Equal("job-bbbb2222", runs[3].JobId);
    }

    [Fact]
    public void TheEnvelopesPagingFieldsAreReadFaithfully()
    {
        var page = Json(CronRunsPage);

        Assert.False(OpenClawCronRecovery.HasMore(page));
        Assert.Equal(5, OpenClawCronRecovery.NextOffset(page));
    }

    // The real match this ticket exists for: two different runs of the same
    // job, each carrying a caption plus a MEDIA: path, resolved by basename.
    [Fact]
    public void EachRunsBasenameResolvesToItsOwnJobsPath()
    {
        var runs = OpenClawCronRecovery.RunsFrom(Json(CronRunsPage));

        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-aaaa1111");

        Assert.Equal(
            "/home/agent/outputs/astra/astra_bedphone_texting_1.png",
            map["astra_bedphone_texting_1.png"]);
        Assert.Equal(
            "/home/agent/outputs/astra/astra_scenesweater_couch_2.png",
            map["astra_scenesweater_couch_2.png"]);
    }

    // CB-272: a cron run whose summary ends on a clip is recoverable the same
    // way a picture is. Invented page, real envelope shape: only the fields
    // RunsFrom reads matter to the result, the rest ride along as they do live.
    [Fact]
    public void ARunSummaryNamingAClipIsRecoverableByBasename()
    {
        var page = """
        {"entries":[{"ts":1700000900000,"jobId":"job-clip0001","action":"finished",
          "status":"ok","completionStatus":"succeeded","delivered":true,
          "deliveryStatus":"delivered","runAtMs":1700000840000,"durationMs":41000,
          "summary":"a short one for you\nMEDIA:/home/agent/outputs/astra/clip_123.mp4"}]}
        """;

        var runs = OpenClawCronRecovery.RunsFrom(Json(page));
        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-clip0001");

        Assert.Equal("/home/agent/outputs/astra/clip_123.mp4", map["clip_123.mp4"]);
        Assert.Equal("clip_123.mp4",
            OpenClawCronRecovery.CandidateBasenameFrom("a short one for you\nclip_123.mp4"));
    }

    // CB-274 / CB-275: a failed attachment block names its file by label, and
    // the run that produced it names the real path. A clip, a voice note and
    // a document (which no picture/clip/audio predicate nominates) are all
    // recoverable by basename; an unsafe path is not.
    [Fact]
    public void ARunSummaryNamingAClipAVoiceNoteOrADocumentIsRecoverableByLabel()
    {
        var page = """
        {"entries":[
          {"jobId":"job-att0001","summary":"x\nMEDIA:/home/agent/outputs/astra/sample_clip.mp4"},
          {"jobId":"job-att0001","summary":"x\nMEDIA:/home/agent/outputs/astra/note.m4a"},
          {"jobId":"job-att0001","summary":"x\nMEDIA:/home/agent/outputs/astra/report.pdf"},
          {"jobId":"job-att0001","summary":"x\nMEDIA:/home/agent/../etc/passwd"},
          {"jobId":"job-other","summary":"x\nMEDIA:/home/agent/outputs/astra/other.pdf"}]}
        """;

        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(
            OpenClawCronRecovery.RunsFrom(Json(page)), "job-att0001");

        Assert.Equal("/home/agent/outputs/astra/sample_clip.mp4", map["sample_clip.mp4"]);
        Assert.Equal("/home/agent/outputs/astra/note.m4a", map["note.m4a"]);
        Assert.Equal("/home/agent/outputs/astra/report.pdf", map["report.pdf"]);
        Assert.DoesNotContain("passwd", map.Keys);
        Assert.DoesNotContain("other.pdf", map.Keys);
    }

    // A run with no MEDIA: line at all (a status-only or a failed run)
    // contributes nothing — it is not a third kind of entry to special-case,
    // it simply never enters the basename map.
    [Fact]
    public void RunsWithNoPictureContributeNothingToTheMap()
    {
        var runs = OpenClawCronRecovery.RunsFrom(Json(CronRunsPage));
        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-aaaa1111");

        // Four entries for this job in the fixture (two pictures, one
        // status-only, one failed run with no summary picture) — only the
        // two that actually carry a MEDIA: line make it into the map.
        Assert.Equal(2, map.Count);
    }

    // Two different jobs can each produce a run whose MEDIA: path happens to
    // share a basename — this is not the same-job collision this ticket
    // refuses on, and each job's own map answers for its own file only.
    [Fact]
    public void TheSameBasenameInADifferentJobDoesNotCollideAcrossJobs()
    {
        var runs = OpenClawCronRecovery.RunsFrom(Json(CronRunsPage));

        var mainMap = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-aaaa1111");
        var otherMap = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, "job-bbbb2222");

        Assert.Equal(
            "/home/agent/outputs/astra/astra_bedphone_texting_1.png",
            mainMap["astra_bedphone_texting_1.png"]);
        Assert.Equal(
            "/home/agent/outputs/other/astra_bedphone_texting_1.png",
            otherMap["astra_bedphone_texting_1.png"]);
    }

    // The full end-to-end shape, without a socket: a chat.history-style
    // message whose delivery route stripped the directory off its own
    // caption, resolved by joining TurnsFromHistory's tagging with this
    // page's map — the same two calls
    // OpenClawSessions.FetchHistoryPageAsync makes, minus the RPC itself.
    [Fact]
    public void ATranscriptMessageResolvesAgainstThisPageEndToEnd()
    {
        var message = Json("""
            {
              "role": "assistant",
              "content": "cozy on the couch tonight 🧡\nastra_scenesweater_couch_2.png",
              "openclawAutomation": { "kind": "cron", "jobId": "job-aaaa1111", "runId": "cron:job-aaaa1111:1700002500123" }
            }
            """);

        var messages = Json($"[{message.GetRawText()}]");
        var turn = Assert.Single(OpenClawSessions.TurnsFromHistory(messages, "agent:main:main"));

        Assert.NotNull(turn.Automation);

        var basename = OpenClawCronRecovery.CandidateBasenameFrom(turn.Text);
        Assert.Equal("astra_scenesweater_couch_2.png", basename);

        var runs = OpenClawCronRecovery.RunsFrom(Json(CronRunsPage));
        var map = OpenClawCronRecovery.MediaPathsByBasenameForJob(runs, turn.Automation!.Value.JobId);

        Assert.Equal("/home/agent/outputs/astra/astra_scenesweater_couch_2.png", map[basename!]);
    }
}
