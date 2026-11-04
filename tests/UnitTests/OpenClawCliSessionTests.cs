using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-306: recognising a Claude Code session OpenClaw spawned to run one of its
// agents, and which gateway cluster it belongs to.
//
// FIXTURES: the shapes are the gateway's own, read off real transcripts on a
// real machine (the `[cron:<id> <name>]` prefix, the scheduled-reminder
// wrapper, the `Conversation info: ⟦openclaw:ctx⟧` block). Ids, names and
// prompt bodies are invented, because this repository is public.
//
// In the Settings collection because ShowHere and the end-to-end cases read
// OrbweaverSettings and the process-wide gateway workspace table.
[Collection("Settings")]
public class OpenClawCliSessionTests
{
    private const string Zara = "/Users/sample/.openclaw/workspace-sample-zara";

    private static string User(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = text }
        });

    private const string Heartbeat =
        "A scheduled reminder has been triggered. The reminder content is:\n\nHEARTBEAT: run the routine.";

    // --- Spawned ---------------------------------------------------------------

    [Theory]
    [InlineData("sdk-cli", Zara, true)]
    [InlineData("SDK-CLI", Zara + "/", true)]
    [InlineData("sdk-cli", "/Users/sample/.openclaw/workspace", true)]
    [InlineData("sdk-cli", @"C:\Users\sample\.openclaw\workspace-sample-zara", true)]
    // Negative controls: a terminal in the agent's folder is the person's own
    // session; -p anywhere else is somebody's script.
    [InlineData("cli", Zara, false)]
    [InlineData("", Zara, false)]
    [InlineData(null, Zara, false)]
    [InlineData("sdk-cli", "/Users/sample/Source/project", false)]
    [InlineData("sdk-cli", "/Users/sample/.openclaw/tmp", false)]
    [InlineData("sdk-cli", "/Users/sample/other/workspace-sample-zara", false)]
    [InlineData("sdk-cli", "/Users/sample/.openclaw/workspaces", false)]
    [InlineData("sdk-cli", "workspace", false)]
    [InlineData("sdk-cli", "", false)]
    [InlineData("sdk-cli", null, false)]
    public void SpawnedNeedsTheSdkEntrypointAndAnOpenClawWorkspace(string? entrypoint, string? cwd, bool spawned) =>
        Assert.Equal(spawned, OpenClawCliSession.Spawned(entrypoint, cwd));

    [Fact]
    public void AWorkspaceTheGatewayNamesCountsWhereverItLives()
    {
        const string elsewhere = "/srv/agents/zara";
        Assert.True(OpenClawCliSession.Spawned("sdk-cli", elsewhere, new[] { elsewhere + "/" }));
        Assert.False(OpenClawCliSession.Spawned("sdk-cli", elsewhere, new[] { "/srv/agents/other" }));
        Assert.False(OpenClawCliSession.Spawned("cli", elsewhere, new[] { elsewhere }));
    }

    // --- ClusterOfPrompt ---------------------------------------------------------

    [Theory]
    [InlineData("[cron:0000-aaaa sample-job] Read the sheet.", OrbCluster.Crons)]
    [InlineData("  [cron:0000-aaaa sample-job] HEARTBEAT mentioned in passing", OrbCluster.Crons)]
    [InlineData(Heartbeat, OrbCluster.Heartbeats)]
    [InlineData("Conversation info: ⟦openclaw:ctx⟧\n```json\n{}```", OrbCluster.Chats)]
    [InlineData("[Tue] <<<BEGIN_OPENCLAW_INTERNAL_CONTEXT>>>\n\n[Subagent Context] You are a subagent.", OrbCluster.Chats)]
    [InlineData("[Inter-session message] sourceSession=agent:sample:cron:1", OrbCluster.Chats)]
    public void APromptTheGatewayWroteMarksItsCluster(string prompt, OrbCluster cluster) =>
        Assert.Equal(cluster, OpenClawCliSession.ClusterOfPrompt(prompt));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("make sure nothing runs for five hours")]
    [InlineData("A scheduled reminder has been triggered. Water the plants.")]
    [InlineData("HEARTBEAT on its own is not the gateway's wrapper")]
    [InlineData("[OpenClaw: 12 earlier messages in this chat]")]
    public void AnyOtherPromptMarksNothing(string? prompt) =>
        Assert.Null(OpenClawCliSession.ClusterOfPrompt(prompt));

    // --- UserPromptOf --------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":\"hi\"}}")]
    [InlineData("{\"type\":7}")]
    [InlineData("{\"type\":\"user\"}")]
    [InlineData("{\"type\":\"user\",\"message\":\"flat\"}")]
    [InlineData("{\"type\":\"user\",\"message\":{}}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":\"\"}}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":5}}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"x\"}]}}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[1,{\"type\":5},{\"type\":\"text\"},{\"type\":\"text\",\"text\":3}]}}")]
    public void ARowThatIsNotAUserPromptReadsAsNone(string line) =>
        Assert.Null(OpenClawCliSession.UserPromptOf(line));

    [Fact]
    public void APromptInTextPartsIsJoined() =>
        Assert.Equal("[cron:1 a] go",
            OpenClawCliSession.UserPromptOf(
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[cron:1 a]\"},{\"type\":\"image\"},{\"type\":\"text\",\"text\":\" go\"}]}}"));

    // --- HeadOf / ClusterOfTranscript ---------------------------------------------

    [Fact]
    public void TheFirstMarkedTurnDecidesEvenWhenItIsNotTheFirstTurn()
    {
        // A resumed main session: the history preamble first, its heartbeat later.
        var lines = new[]
        {
            "{\"type\":\"queue-operation\"}",
            User("[OpenClaw: 12 earlier messages in this chat]"),
            "{\"type\":\"assistant\",\"message\":{\"content\":[]}}",
            User(Heartbeat)
        };

        Assert.Equal((OrbCluster.Heartbeats, true), OpenClawCliSession.HeadOf(lines));
        Assert.Equal(OrbCluster.Heartbeats, OpenClawCliSession.ClusterOfTranscript(lines));
    }

    [Fact]
    public void NoUserTurnYetIsNoAnswerAndUnsettled() =>
        Assert.Equal(((OrbCluster?)null, false), OpenClawCliSession.HeadOf(new[] { "{\"type\":\"queue-operation\"}" }));

    [Fact]
    public void UnmarkedTurnsAreNativeAndStillUnsettledInsideTheBounds() =>
        Assert.Equal((OrbCluster.Native, false), OpenClawCliSession.HeadOf(new[] { User("hello") }));

    [Fact]
    public void UnmarkedTurnsSettleAsNativeAtTheTurnBound()
    {
        var lines = Enumerable.Repeat(User("hello"), OpenClawCliSession.MaxUserTurns)
            .Append(User(Heartbeat));

        Assert.Equal((OrbCluster.Native, true), OpenClawCliSession.HeadOf(lines));
    }

    [Fact]
    public void TheCharacterBoundSettlesWhateverIsKnownSoFar()
    {
        var huge = new string('x', OpenClawCliSession.MaxChars + 1);

        Assert.Equal(((OrbCluster?)null, true), OpenClawCliSession.HeadOf(new[] { huge, User(Heartbeat) }));
        Assert.Equal((OrbCluster.Native, true), OpenClawCliSession.HeadOf(new[] { User("hello"), huge, User(Heartbeat) }));
    }

    // --- RepresentedByGateway and Show ---------------------------------------------

    [Fact]
    public void TheGatewayRepresentsAWorkspaceItNamesOnlyWhileTheIntegrationIsOn()
    {
        Assert.True(OpenClawCliSession.RepresentedByGateway(Zara + "/", true, new[] { Zara }));
        Assert.False(OpenClawCliSession.RepresentedByGateway(Zara, false, new[] { Zara }));
        Assert.False(OpenClawCliSession.RepresentedByGateway(Zara, true, new[] { "/Users/sample/.openclaw/workspace" }));
        Assert.False(OpenClawCliSession.RepresentedByGateway(null, true, new[] { Zara }));
        Assert.False(OpenClawCliSession.RepresentedByGateway("", true, new[] { Zara }));
    }

    [Theory]
    // Not OpenClaw's: always shown, even with everything hidden.
    [InlineData(null, false, ClusterMode.Hidden, ClusterMode.Hidden, ClusterMode.Hidden, true)]
    [InlineData(null, true, ClusterMode.Hidden, ClusterMode.Hidden, ClusterMode.Hidden, true)]
    // Each cluster follows its own setting, and only its own.
    [InlineData(OrbCluster.Heartbeats, false, ClusterMode.Hidden, ClusterMode.WithChats, ClusterMode.WithChats, false)]
    [InlineData(OrbCluster.Heartbeats, false, ClusterMode.WithChats, ClusterMode.Hidden, ClusterMode.Hidden, true)]
    [InlineData(OrbCluster.Crons, false, ClusterMode.WithChats, ClusterMode.Hidden, ClusterMode.WithChats, false)]
    [InlineData(OrbCluster.Crons, false, ClusterMode.Hidden, ClusterMode.OwnShape, ClusterMode.Hidden, true)]
    [InlineData(OrbCluster.Native, false, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.Hidden, false)]
    [InlineData(OrbCluster.Native, false, ClusterMode.Hidden, ClusterMode.Hidden, ClusterMode.WithChats, true)]
    [InlineData(OrbCluster.Chats, false, ClusterMode.Hidden, ClusterMode.Hidden, ClusterMode.Hidden, true)]
    // Drawn by the gateway already: never twice, whatever the settings.
    [InlineData(OrbCluster.Chats, true, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats, false)]
    [InlineData(OrbCluster.Heartbeats, true, ClusterMode.OwnShape, ClusterMode.WithChats, ClusterMode.WithChats, false)]
    public void ShowFollowsTheViewersClusterSettings(
        OrbCluster? cluster, bool represented, ClusterMode heartbeats, ClusterMode crons, ClusterMode native, bool shown) =>
        Assert.Equal(shown, OpenClawCliSession.Show(cluster, represented, heartbeats, crons, native));

    // --- wire words -------------------------------------------------------------------

    [Theory]
    [InlineData(OrbCluster.Heartbeats, "heartbeats")]
    [InlineData(OrbCluster.Crons, "crons")]
    [InlineData(OrbCluster.Native, "native")]
    [InlineData(OrbCluster.Chats, "chats")]
    public void EveryClusterRoundTripsItsWireWord(OrbCluster cluster, string word)
    {
        Assert.Equal(word, OpenClawCliSession.Name(cluster));
        Assert.Equal(cluster, OpenClawCliSession.Parse(word));
        Assert.Equal(cluster, OpenClawCliSession.Parse(" " + word.ToUpperInvariant() + " "));
    }

    [Fact]
    public void AnAbsentWordIsNotOpenClawsAndAnUnknownOneIsChats()
    {
        Assert.Null(OpenClawCliSession.Parse(null));
        Assert.Null(OpenClawCliSession.Parse(" "));
        Assert.Equal(OrbCluster.Chats, OpenClawCliSession.Parse("someday-a-new-cluster"));
    }

    // --- ReadCluster -------------------------------------------------------------------

    [Fact]
    public void ASettledAnswerIsReadOnceAndAnUnsettledOneAgainOnlyWhenTheFileGrows()
    {
        OpenClawCliSession.ForgetForTests();
        var reads = 0;
        long length = 10;
        var lines = new List<string> { User("hello") };
        IEnumerable<string> Read(string _) { reads++; return lines.ToList(); }

        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster("/t/a.jsonl", Read, _ => length));
        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster("/t/a.jsonl", Read, _ => length));
        Assert.Equal(1, reads);

        lines.Add(User("[cron:1 sample] go"));
        length = 20;
        Assert.Equal(OrbCluster.Crons, OpenClawCliSession.ReadCluster("/t/a.jsonl", Read, _ => length));
        Assert.Equal(2, reads);

        length = 30;
        Assert.Equal(OrbCluster.Crons, OpenClawCliSession.ReadCluster("/t/a.jsonl", Read, _ => length));
        Assert.Equal(2, reads);
        OpenClawCliSession.ForgetForTests();
    }

    [Fact]
    public void AnEmptyHeadIsNativeAndAskedAgain()
    {
        OpenClawCliSession.ForgetForTests();
        var reads = 0;
        IEnumerable<string> Read(string _) { reads++; return new[] { "{\"type\":\"queue-operation\"}" }; }

        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster("/t/b.jsonl", Read, _ => 5));
        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster("/t/b.jsonl", Read, _ => 5));
        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster("/t/b.jsonl", Read, _ => 6));
        Assert.Equal(2, reads);
        OpenClawCliSession.ForgetForTests();
    }

    [Fact]
    public void NoPathOrAnUnreadableFileIsNative()
    {
        OpenClawCliSession.ForgetForTests();
        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster(null));
        Assert.Equal(OrbCluster.Native, OpenClawCliSession.ReadCluster(""));
        Assert.Equal(OrbCluster.Native,
            OpenClawCliSession.ReadCluster(Path.Combine(Path.GetTempPath(), "cb306-missing-" + System.Guid.NewGuid() + ".jsonl")));
        Assert.Equal(OrbCluster.Native,
            OpenClawCliSession.ReadCluster("/t/c.jsonl", _ => throw new System.UnauthorizedAccessException(), _ => 1));
    }

    [Fact]
    public void ARealFileIsReadThroughTheDefaultReaders()
    {
        OpenClawCliSession.ForgetForTests();
        var path = Path.Combine(Path.GetTempPath(), "cb306-" + System.Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, User(Heartbeat) + "\n");
            Assert.Equal(OrbCluster.Heartbeats, OpenClawCliSession.ReadCluster(path));
        }
        finally
        {
            File.Delete(path);
            OpenClawCliSession.ForgetForTests();
        }
    }

    // --- ClusterOf and ShowHere, the status-holding callers -------------------------------

    private static (SessionStatus Status, string Path) Spawn(string cwd, string prompt, SessionSource source = SessionSource.ClaudeCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "cb306-" + System.Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, User(prompt) + "\n");
        return (new SessionStatus
        {
            Source = source,
            Entrypoint = "sdk-cli",
            Cwd = cwd,
            TranscriptPath = path
        }, path);
    }

    [Fact]
    public void ClusterOfAnswersOnlyForAClaudeCodeSpawn()
    {
        OpenClawCliSession.ForgetForTests();
        var spawn = Spawn(Zara, Heartbeat);
        var codex = Spawn(Zara, Heartbeat, SessionSource.Codex);
        var ordinary = Spawn("/Users/sample/Source/project", Heartbeat);
        try
        {
            Assert.Equal(OrbCluster.Heartbeats, OpenClawCliSession.ClusterOf(spawn.Status));
            Assert.Null(OpenClawCliSession.ClusterOf(codex.Status));
            Assert.Null(OpenClawCliSession.ClusterOf(ordinary.Status));
        }
        finally
        {
            File.Delete(spawn.Path);
            File.Delete(codex.Path);
            File.Delete(ordinary.Path);
            OpenClawCliSession.ForgetForTests();
        }
    }

    [Fact]
    public void ShowHereReadsTheViewersSettingsAndItsGateway()
    {
        OpenClawCliSession.ForgetForTests();
        var before = (OrbweaverSettings.OpenClawHeartbeatMode, OrbweaverSettings.OpenClawEnabled);
        var spawn = Spawn(Zara, Heartbeat);
        var ordinary = Spawn("/Users/sample/Source/project", Heartbeat);
        try
        {
            OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>());
            OrbweaverSettings.OpenClawEnabled = false;

            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.Hidden;
            Assert.False(OpenClawCliSession.ShowHere(spawn.Status));
            Assert.True(OpenClawCliSession.ShowHere(ordinary.Status));

            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
            Assert.True(OpenClawCliSession.ShowHere(spawn.Status));

            OrbweaverSettings.OpenClawEnabled = true;
            OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string> { ["zara"] = Zara });
            Assert.False(OpenClawCliSession.ShowHere(spawn.Status));
            Assert.True(OpenClawCliSession.ShowHere(ordinary.Status));
        }
        finally
        {
            OrbweaverSettings.OpenClawHeartbeatMode = before.OpenClawHeartbeatMode;
            OrbweaverSettings.OpenClawEnabled = before.OpenClawEnabled;
            OpenClawSessions.SetAgentWorkspacesForTests(new Dictionary<string, string>());
            File.Delete(spawn.Path);
            File.Delete(ordinary.Path);
            OpenClawCliSession.ForgetForTests();
        }
    }

    // --- the far side's stamp ----------------------------------------------------------------

    [Fact]
    public void TheRosterIsStampedOnlyForASpawnItOffersByRoute()
    {
        var spawn = new SessionStatus { Source = SessionSource.ClaudeCode, Entrypoint = "sdk-cli", Cwd = Zara + "/" };
        var ordinary = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/sample/Source/project" };
        var sessions = new List<(string, SessionStatus)> { ("s1", spawn), ("s2", ordinary) };
        var entries = new List<MirrorProtocol.MirrorRosterEntry>
        {
            new("spawn", "claude", true, false, Route: RemoteMirrorServer.RouteFor("s1")),
            new("ordinary", "claude", true, true, Route: RemoteMirrorServer.RouteFor("s2")),
            new("unrouted", "claude", true, true),
            new("unknown", "claude", true, true, Route: RemoteMirrorServer.RouteFor("gone"))
        };

        var stamped = RemoteMirrorServer.WithOpenClawFields(entries, sessions, new[] { "/elsewhere" },
            (status, known) =>
            {
                Assert.Equal(new[] { "/elsewhere" }, known);
                return ReferenceEquals(status, spawn) ? OrbCluster.Crons : null;
            });

        Assert.Equal("crons", stamped[0].OpenClawCluster);
        Assert.Equal(Zara, stamped[0].OpenClawWorkspace);
        Assert.All(stamped.Skip(1), e =>
        {
            Assert.Null(e.OpenClawCluster);
            Assert.Null(e.OpenClawWorkspace);
        });
    }

    [Fact]
    public void TheDefaultStampReadsTheTranscript()
    {
        OpenClawCliSession.ForgetForTests();
        var spawn = Spawn(Zara, "[cron:1 sample] go");
        try
        {
            var stamped = RemoteMirrorServer.WithOpenClawFields(
                new List<MirrorProtocol.MirrorRosterEntry> { new("spawn", "claude", true, false, Route: RemoteMirrorServer.RouteFor("s1")) },
                new List<(string, SessionStatus)> { ("s1", spawn.Status) },
                System.Array.Empty<string>());

            Assert.Equal("crons", Assert.Single(stamped).OpenClawCluster);
        }
        finally
        {
            File.Delete(spawn.Path);
            OpenClawCliSession.ForgetForTests();
        }
    }

    [Fact]
    public void AnUnstampedEntrySerialisesExactlyAsBefore()
    {
        var plain = new MirrorProtocol.MirrorRosterEntry("job", "claude", true, true);
        var json = System.Text.Encoding.UTF8.GetString(MirrorProtocol.RosterBytes(new[] { plain }));

        Assert.DoesNotContain("openclaw", json);
    }
}
