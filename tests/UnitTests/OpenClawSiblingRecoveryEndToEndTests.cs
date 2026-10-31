using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-277, slice 4: a clip whose directory is on neither its own conversation
// page nor its cron run record is recovered from the same conversation as the
// other agents kept it. The shape is the real one — a cron completion whose
// attachment_error names only the filename, a run record with a caption and
// no MEDIA: line, and a sibling agent's history that carries the absolute
// path — with invented ids, paths and names (public repository).
//
// Positive cases come first and each asserts a specific recovered path; the
// negatives (conflict, no siblings, already resolved) sit beside them so a
// mechanism that finds nothing at all cannot pass as "correctly refused".
[Collection("Settings")]
public class OpenClawSiblingRecoveryEndToEndTests : IDisposable
{
    private const string Ask = "agent:main:discord:direct:1";
    private const string Zara = "agent:comfyui:discord:direct:1";
    private const string Other = "agent:social:discord:direct:1";
    private const string Job = "job-sib-1";
    private const string Clip = "sample_midnight_clip_1.mp4";
    private const string Dir = "/Users/sample/.openclaw/workspace-sample-zara/outputs/sample";

    public OpenClawSiblingRecoveryEndToEndTests()
    {
        OpenClawSessions.ForgetSiblingRecoveriesForTests();
        OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>
        {
            ["main"] = "/Users/sample/.openclaw/workspace",
            ["comfyui"] = "/Users/sample/.openclaw/workspace-sample-zara",
        });
    }

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.SetSnapshotForTests(new List<OpenClawSessions.Session>());
        OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>());
        OpenClawSessions.ForgetSiblingRecoveriesForTests();
    }

    private static OpenClawSessions.Session Session(string key) =>
        new(key, "t", "", "idle", DateTime.UtcNow, null, SessionKind.Direct, false, "sid-" + key);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static object FailedClip(string job = Job) => new
    {
        role = "assistant",
        content = new object[]
        {
            new { type = "text", text = "a little clip" },
            new
            {
                type = "attachment_error",
                attachment = new { code = "delivery-failed", kind = "video", label = Clip, mimeType = "video/mp4" }
            }
        },
        provider = "openclaw",
        model = "automation-result",
        openclawAutomation = new { kind = "cron", jobId = job, runId = "r" }
    };

    private static object TextRow(string text) => new { role = "assistant", content = text };

    private static object CronRunsPage(params object[] entries) => new
    {
        entries, total = entries.Length, offset = 0, limit = 60, hasMore = false, nextOffset = (int?)null
    };

    // The run record that is caption-only: no MEDIA: line anywhere.
    private static readonly object CaptionOnlyRun = new { jobId = Job, summary = "a little clip, just for you" };

    private sealed record Rig(FakeGatewaySocket Socket, OpenClawChatSession Session)
    {
        public List<string> HistoryKeys() => Socket.Requests
            .Where(r => r.Method == "chat.history")
            .Select(r => r.Params.GetProperty("sessionKey").GetString()!)
            .ToList();
    }

    private static async Task<Rig> ConnectedAsync(
        Func<string, FakeGatewaySocket.Request, object> onHistory,
        object cronRuns,
        params string[] snapshotKeys)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request => request.Method switch
        {
            "connect" => FakeGatewaySocket.Ok(request.Id, new
            {
                protocol = 4,
                server = new { version = "1.2.3" },
                auth = new { scopes = new[] { "operator.read" } },
                policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
            }),
            "chat.history" => onHistory(request.Params.GetProperty("sessionKey").GetString()!, request),
            "cron.runs" => FakeGatewaySocket.Ok(request.Id, cronRuns),
            _ => FakeGatewaySocket.Ok(request.Id, new { })
        };

        var gateway = Gateway(socket);
        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);
        OpenClawSessions.SetSnapshotForTests(snapshotKeys.Select(Session).ToList());

        return new Rig(socket, new OpenClawChatSession("openclaw:" + Ask, Ask, "main"));
    }

    private static object Page(FakeGatewaySocket.Request r, params object[] messages) =>
        FakeGatewaySocket.Ok(r.Id, new { messages });

    // Asking page: the failed clip; the owner's page: the absolute path.
    private static object Histories(string key, FakeGatewaySocket.Request r) => key switch
    {
        Ask => Page(r, FailedClip()),
        Zara => Page(r, TextRow($"saved to {Dir}/{Clip} for delivery")),
        _ => Page(r, TextRow("nothing here"))
    };

    // ---- positive --------------------------------------------------------

    [Fact]
    public async Task ABareClipNameWithACaptionOnlyRunRecordIsRecoveredFromTheOwnersHistory()
    {
        var rig = await ConnectedAsync(Histories, CronRunsPage(CaptionOnlyRun), Ask, Zara, Other);

        var page = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);

        var turn = Assert.Single(page!.Value.Turns);
        Assert.Equal($"{Dir}/{Clip}", turn.ImageSourcePath);
        Assert.Equal(new OpenClawMediaSource($"{Dir}/{Clip}", Zara).Route, turn.ImageUrl);
        Assert.Equal(Clip, turn.ImageAlt);
        Assert.Equal(MediaConfidence.High, turn.Confidence);

        // One page for the conversation, then exactly one per sibling.
        Assert.Equal(new[] { Ask, Zara, Other }, rig.HistoryKeys());
    }

    [Fact]
    public async Task TheAnswerIsCachedSoASecondReadAsksNoSibling()
    {
        var rig = await ConnectedAsync(Histories, CronRunsPage(CaptionOnlyRun), Ask, Zara, Other);

        await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        var again = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);

        Assert.Equal($"{Dir}/{Clip}", Assert.Single(again!.Value.Turns).ImageSourcePath);
        Assert.Equal(new[] { Ask, Zara, Other, Ask }, rig.HistoryKeys());
    }

    // ---- negative controls beside them ----------------------------------

    [Fact]
    public async Task TwoSiblingsNamingDifferentDirectoriesAreRefusedAndTheGuessStays()
    {
        var rig = await ConnectedAsync((key, r) => key switch
        {
            Ask => Page(r, FailedClip()),
            Zara => Page(r, TextRow($"saved to {Dir}/{Clip} ok")),
            _ => Page(r, TextRow($"saved to /Users/sample/elsewhere/{Clip} ok"))
        }, CronRunsPage(CaptionOnlyRun), Ask, Zara, Other);

        var page = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);

        var turn = Assert.Single(page!.Value.Turns);
        Assert.Equal(OpenClawSessions.SharedMediaDir + Clip, turn.ImageSourcePath);
    }

    [Fact]
    public async Task WithNoSiblingInTheSnapshotNoSiblingIsAsked()
    {
        var rig = await ConnectedAsync(Histories, CronRunsPage(CaptionOnlyRun), Ask);

        var page = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);

        Assert.Equal(OpenClawSessions.SharedMediaDir + Clip, Assert.Single(page!.Value.Turns).ImageSourcePath);
        Assert.Equal(new[] { Ask }, rig.HistoryKeys());
    }

    // A page opened before the sessions poll landed has no siblings to ask.
    // That is not "never delivered": once the owner's conversation is in the
    // snapshot the same read must find the path.
    [Fact]
    public async Task AReadBeforeTheSiblingsAreKnownDoesNotHideThePathFromTheNextRead()
    {
        var rig = await ConnectedAsync(Histories, CronRunsPage(CaptionOnlyRun), Ask);

        var first = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        Assert.Equal(OpenClawSessions.SharedMediaDir + Clip, Assert.Single(first!.Value.Turns).ImageSourcePath);

        OpenClawSessions.SetSnapshotForTests(new[] { Ask, Zara, Other }.Select(Session).ToList());

        var second = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        Assert.Equal($"{Dir}/{Clip}", Assert.Single(second!.Value.Turns).ImageSourcePath);
    }

    [Fact]
    public async Task AClipTheCronRunRecordNamesIsNotLookedForInSiblings()
    {
        // Its own job id: the cron run map is cached per job process-wide.
        const string OwnJob = "job-sib-cron-path";
        var run = new { jobId = OwnJob, summary = $"a little clip\nMEDIA:{Dir}/{Clip}" };
        var rig = await ConnectedAsync((key, r) => key == Ask ? Page(r, FailedClip(OwnJob)) : Histories(key, r),
            CronRunsPage(run), Ask, Zara, Other);

        var page = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);

        Assert.Equal($"{Dir}/{Clip}", Assert.Single(page!.Value.Turns).ImageSourcePath);
        Assert.Equal(new[] { Ask }, rig.HistoryKeys());
    }

    [Fact]
    public async Task AClipThePageItselfAlreadyResolvedIsNotLookedForInSiblings()
    {
        var rig = await ConnectedAsync((key, r) => key == Ask
            ? Page(r, TextRow($"sent {Dir}/{Clip} to you"), FailedClip())
            : Histories(key, r), CronRunsPage(CaptionOnlyRun), Ask, Zara, Other);

        var page = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);

        Assert.Contains(page!.Value.Turns, t => t.ImageSourcePath == $"{Dir}/{Clip}");
        Assert.Equal(new[] { Ask }, rig.HistoryKeys());
    }

    // ---- a sibling that cannot answer is not a "never delivered" --------

    [Fact]
    public async Task ASiblingThatFailsToAnswerIsNotCachedAndTheNextReadTriesAgain()
    {
        var zaraFails = true;
        var rig = await ConnectedAsync((key, r) => key switch
        {
            Ask => Page(r, FailedClip()),
            Zara when zaraFails => FakeGatewaySocket.Error(r.Id, "UNAVAILABLE", "down", null),
            Zara => Page(r, TextRow($"saved to {Dir}/{Clip} ok")),
            _ => Page(r, TextRow("nothing here"))
        }, CronRunsPage(CaptionOnlyRun), Ask, Zara, Other);

        var first = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        Assert.Equal(OpenClawSessions.SharedMediaDir + Clip, Assert.Single(first!.Value.Turns).ImageSourcePath);

        zaraFails = false;
        var second = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        Assert.Equal($"{Dir}/{Clip}", Assert.Single(second!.Value.Turns).ImageSourcePath);
    }

    [Fact]
    public async Task ASiblingAnswerWithNoMessagesListIsNotCachedEither()
    {
        var malformed = true;
        var rig = await ConnectedAsync((key, r) => key switch
        {
            Ask => Page(r, FailedClip()),
            Zara when malformed => FakeGatewaySocket.Ok(r.Id, new { }),
            Zara => Page(r, TextRow($"saved to {Dir}/{Clip} ok")),
            _ => Page(r, TextRow("nothing here"))
        }, CronRunsPage(CaptionOnlyRun), Ask, Zara, Other);

        var first = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        Assert.Equal(OpenClawSessions.SharedMediaDir + Clip, Assert.Single(first!.Value.Turns).ImageSourcePath);

        malformed = false;
        var second = await OpenClawSessions.FetchPageAsync(rig.Session, 0, CancellationToken.None);
        Assert.Equal($"{Dir}/{Clip}", Assert.Single(second!.Value.Turns).ImageSourcePath);
    }

    [Fact]
    public async Task WithNoGatewayTheRecoveryAnswersNothingAndAsksNothing()
    {
        OpenClawSessions.SetGatewayForTests(null);

        Assert.Null(await OpenClawSessions.RecoverFromSiblingHistoryAsync(Ask, Clip, CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledReadAnswersNothingAndCachesNothing()
    {
        var rig = await ConnectedAsync(Histories, CronRunsPage(CaptionOnlyRun), Ask, Zara);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await OpenClawSessions.RecoverFromSiblingHistoryAsync(Ask, Clip, cts.Token));

        // Not remembered as a miss: a live read still finds it.
        Assert.Equal($"{Dir}/{Clip}",
            await OpenClawSessions.RecoverFromSiblingHistoryAsync(Ask, Clip, CancellationToken.None));
    }

    [Fact]
    public async Task TheCacheIsBoundedAndStillAnswersAfterItFills()
    {
        await ConnectedAsync(Histories, CronRunsPage(CaptionOnlyRun), Ask, Zara);

        for (var i = 0; i < 70; i++)
            await OpenClawSessions.RecoverFromSiblingHistoryAsync(Ask, $"missing_{i}.mp4", CancellationToken.None);

        Assert.Equal($"{Dir}/{Clip}",
            await OpenClawSessions.RecoverFromSiblingHistoryAsync(Ask, Clip, CancellationToken.None));
    }

    // ---- the pure halves -------------------------------------------------

    [Fact]
    public void SiblingConversationKeysAreTheOtherAgentsKeysOfTheSameConversation()
    {
        var keys = new[]
        {
            Ask, Zara, Other,
            "agent:comfyui:main",                      // another conversation
            "agent:comfyui:discord:direct:2",          // another conversation
            "agent:main:discord:direct:1",             // the asker itself, twice listed
            "not-an-agent-key",
        };

        Assert.Equal(new[] { Zara, Other }, OpenClawMediaOwner.SiblingConversationKeys(Ask, keys));
    }

    [Fact]
    public void ANonAgentAskingKeyHasNoSiblings()
    {
        Assert.Empty(OpenClawMediaOwner.SiblingConversationKeys("weird", new[] { Zara }));
        Assert.Empty(OpenClawMediaOwner.SiblingConversationKeys("agent:main", new[] { Zara }));
    }

    [Fact]
    public void AgreedPathIsTheOnePathEveryAnswerAgreesOn()
    {
        Assert.Equal("/a/x.mp4", OpenClawMediaOwner.AgreedPath(new string?[] { null, "/a/x.mp4", "/a/x.mp4" }));
        Assert.Null(OpenClawMediaOwner.AgreedPath(new string?[] { "/a/x.mp4", "/b/x.mp4" }));
        Assert.Null(OpenClawMediaOwner.AgreedPath(new string?[] { null, null }));
        Assert.Null(OpenClawMediaOwner.AgreedPath(Array.Empty<string?>()));
    }
}
