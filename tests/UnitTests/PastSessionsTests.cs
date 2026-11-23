using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-361: the three readers and the list behind New Chat's Resume mode. The
// rows are the minimal shapes each Map accepts (the same builders as
// ChatTranscriptEdgeTests, CodexTranscriptItemTests and GrokTranscriptTests);
// the architect's real captures in tests/TranscriptTests/Captures/cb361 pin
// the full shapes.
public class PastSessionsTests
{
    private const string Id = "11111111-2222-4333-8444-555555555555";
    private static readonly DateTimeOffset At = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static bool Exists(string _) => true;
    private static bool Missing(string _) => false;

    // ---- Claude ----

    private static string ClaudeUser(string text, string? cwd = "/work/app", string? sessionId = Id, bool sidechain = false) =>
        "{\"type\":\"user\",\"uuid\":\"u" + text.Length + "\",\"timestamp\":\"2026-11-20T10:00:00Z\""
        + (sidechain ? ",\"isSidechain\":true" : "")
        + (cwd is null ? "" : ",\"cwd\":\"" + cwd + "\"")
        + (sessionId is null ? "" : ",\"sessionId\":\"" + sessionId + "\"")
        + ",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    private static string ClaudeAssistant(string text) =>
        "{\"type\":\"assistant\",\"uuid\":\"a1\",\"timestamp\":\"2026-11-20T10:00:01Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\""
        + text + "\"}]}}";

    private static PastSession? Claude(string path, Func<string, bool>? exists = null, params string[] lines) =>
        PastSessions.FromClaude(lines, lines, null, path, At, exists ?? Exists);

    [Fact]
    public void ClaudeReadsCwdIdFirstPromptAndCount()
    {
        var s = Claude("/c/projects/-work-app/" + Id + ".jsonl", null, ClaudeUser("fix the build"), ClaudeAssistant("done"))!;

        Assert.Equal(NewChatCli.ClaudeCode, s.Cli);
        Assert.Equal(Id, s.Id);
        Assert.Equal("/work/app", s.Cwd);
        Assert.Equal("fix the build", s.Title);
        Assert.Null(s.MessageCount);
        Assert.Equal(At, s.LastActivity);
        Assert.False(s.FolderMissing);
    }

    [Fact]
    public void ClaudeCustomTitleOutranksAiTitleAndThePrompt()
    {
        var s = Claude("/x/" + Id + ".jsonl", null,
            ClaudeUser("first prompt"),
            "{\"type\":\"ai-title\",\"aiTitle\":\"Generated\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"Named by hand\"}")!;
        Assert.Equal("Named by hand", s.Title);

        var ai = Claude("/x/" + Id + ".jsonl", null, ClaudeUser("first prompt"), "{\"type\":\"ai-title\",\"aiTitle\":\"Generated\"}")!;
        Assert.Equal("Generated", ai.Title);
    }

    [Fact]
    public void ClaudeSidechainRowsNeverSupplyTheCwdOrId()
    {
        var s = Claude("/x/" + Id + ".jsonl", null,
            ClaudeUser("sub", cwd: "/elsewhere", sessionId: "99999999-0000-4000-8000-000000000000", sidechain: true),
            "not json",
            "[1,2]",
            "{broken",
            ClaudeUser("real"))!;

        Assert.Equal("/work/app", s.Cwd);
        Assert.Equal(Id, s.Id);
        Assert.Equal("real", s.Title);
    }

    [Fact]
    public void ClaudeFallsBackToTheFileNameForTheIdAndHasNoCwdWithoutOne()
    {
        var s = Claude("/x/" + Id + ".jsonl", null, ClaudeUser("hi", cwd: null, sessionId: null))!;

        Assert.Equal(Id, s.Id);
        Assert.Null(s.Cwd);
        Assert.True(s.FolderMissing);
    }

    [Fact]
    public void ClaudeWithNoUserTurnOrAnUnsafeIdIsSkipped()
    {
        Assert.Null(Claude("/x/" + Id + ".jsonl", null, ClaudeAssistant("only me")));
        Assert.Null(Claude("/x/a;rm -rf.jsonl", null, ClaudeUser("hi", sessionId: null)));
    }

    [Fact]
    public void AFolderThatIsGoneMarksTheRowMissing() =>
        Assert.True(Claude("/x/" + Id + ".jsonl", Missing, ClaudeUser("hi"))!.FolderMissing);

    // ---- Codex ----

    private static string CodexMeta(string? id = Id, string? cwd = "/work/api") =>
        "{\"timestamp\":\"2026-11-20T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{"
        + string.Join(",", new[] { id is null ? null : "\"id\":\"" + id + "\"", cwd is null ? null : "\"cwd\":\"" + cwd + "\"", "\"cli_version\":\"0.154.0\"" }.Where(x => x is not null))
        + "}}";

    private static string CodexUser(string text) =>
        "{\"timestamp\":\"2026-11-20T10:00:01Z\",\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":"
        + "{\"type\":\"UserMessage\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}}";

    private const string CodexFile = "/h/sessions/2026/11/20/rollout-2026-11-20T10-00-00-" + Id + ".jsonl";

    private static PastSession? Codex(string path, params string[] lines) =>
        PastSessions.FromCodex(lines, path, At, Exists);

    [Fact]
    public void CodexReadsSessionMetaAndTheFirstPrompt()
    {
        var s = Codex(CodexFile, "{\"type\":\"other\"}", "{\"type\":\"session_meta\",\"payload\":3}", CodexMeta(), CodexUser("add a route"))!;

        Assert.Equal(NewChatCli.Codex, s.Cli);
        Assert.Equal(Id, s.Id);
        Assert.Equal("/work/api", s.Cwd);
        Assert.Equal("add a route", s.Title);
        Assert.Null(s.MessageCount);
    }

    [Fact]
    public void CodexFallsBackToTheFileNameForTheId()
    {
        var s = Codex(CodexFile, CodexMeta(id: null), CodexUser("x"))!;
        Assert.Equal(Id, s.Id);
    }

    // A real codex-tui rollout can be session_meta alone; it is listed, and
    // its title is the folder's name.
    [Fact]
    public void CodexWithNoPromptIsListedUnderItsFolderName()
    {
        Assert.Equal("api", Codex(CodexFile, CodexMeta(cwd: "/work/api/"))!.Title);
        Assert.Equal("(untitled)", Codex(CodexFile, CodexMeta(cwd: null))!.Title);
    }

    [Fact]
    public void CodexWithNoUsableIdIsSkipped() =>
        Assert.Null(Codex("/h/rollout.jsonl", CodexMeta(id: null), CodexUser("x")));

    // ---- ruling A: only sessions a person started ----

    [Theory]
    [InlineData("cli", true)]
    [InlineData(null, true)]
    [InlineData("sdk-cli", false)]
    [InlineData("claude-desktop", false)]
    [InlineData("sdk-ts", false)]
    public void ClaudeListsOnlyTheCliEntrypoint(string? entrypoint, bool listed)
    {
        var opening = "{\"type\":\"mode\",\"sessionId\":\"" + Id + "\"}";
        var user = ClaudeUser("hi");
        if (entrypoint is not null) user = user.Replace("\"type\":\"user\",", "\"type\":\"user\",\"entrypoint\":\"" + entrypoint + "\",");
        Assert.Equal(listed, Claude("/x/" + Id + ".jsonl", null, opening, user) is not null);
    }

    [Theory]
    [InlineData("codex-tui", true)]
    [InlineData(null, true)]
    [InlineData("openclaw", false)]
    [InlineData("codex_exec", false)]
    public void CodexListsOnlyTheTuiOriginator(string? originator, bool listed)
    {
        var meta = CodexMeta();
        if (originator is not null) meta = meta.Replace("\"cli_version\"", "\"originator\":\"" + originator + "\",\"cli_version\"");
        Assert.Equal(listed, Codex(CodexFile, meta, CodexUser("go")) is not null);
    }

    [Theory]
    [InlineData("{\"session_kind\":\"headless\"}", false)]
    [InlineData("{\"session_kind\":\"interactive\"}", true)]
    [InlineData("{}", true)]
    [InlineData(null, true)]
    public void GrokSkipsHeadlessSessions(string? summary, bool listed) =>
        Assert.Equal(listed, PastSessions.FromGrok(summary, new[] { GrokUser("hi") }, GrokFile, At, Exists) is not null);

    [Fact]
    public void GrokPrefersTheSummarysCwd() =>
        Assert.Equal("/real/cwd", PastSessions.FromGrok("{\"info\":{\"cwd\":\"/real/cwd\"}}", new[] { GrokUser("hi") }, GrokFile, At, Exists)!.Cwd);

    [Theory]
    [InlineData("{\"info\":\"flat\"}")]
    [InlineData("{\"info\":{}}")]
    [InlineData("{\"info\":{\"cwd\":\"\"}}")]
    public void AnUnusableSummaryCwdFallsBackToTheDirectoryName(string summary) =>
        Assert.Equal("/work/my site", PastSessions.FromGrok(summary, new[] { GrokUser("hi") }, GrokFile, At, Exists)!.Cwd);

    // ---- Claude titles ----

    [Fact]
    public void TheCustomTitleSidecarWinsOverEveryRow()
    {
        var lines = new[] { ClaudeUser("first"), "{\"type\":\"custom-title\",\"customTitle\":\"Row title\"}" };
        Assert.Equal("Sidecar title", PastSessions.FromClaude(lines, lines, "{\"customTitle\":\" Sidecar title \"}", "/x/" + Id + ".jsonl", At, Exists)!.Title);
        Assert.Equal("Row title", PastSessions.FromClaude(lines, lines, "{\"customTitle\":\"\"}", "/x/" + Id + ".jsonl", At, Exists)!.Title);
        Assert.Equal("Row title", PastSessions.FromClaude(lines, lines, "nonsense", "/x/" + Id + ".jsonl", At, Exists)!.Title);
    }

    // Slash commands and teammate relays are not what a person typed; the
    // title falls through them to the first real prompt.
    [Fact]
    public void TheFirstPromptSkipsCommandAndTeammateRows()
    {
        var s = Claude("/x/" + Id + ".jsonl", null,
            ClaudeUser("<command-name>/clear</command-name>"),
            ClaudeUser("<command-message>clear</command-message>"),
            ClaudeUser("<teammate-message teammate_id=\\\"lead\\\">do it</teammate-message>"),
            ClaudeUser("the real prompt"))!;
        Assert.Equal("the real prompt", s.Title);
    }

    [Theory]
    [InlineData("/h/rollout-2026-11-20T10-00-00-" + Id + ".jsonl", Id)]
    [InlineData("/h/short.jsonl", null)]
    [InlineData("/h/x" + Id + ".jsonl", null)]
    public void CodexIdFromFileName(string path, string? expected) =>
        Assert.Equal(expected, PastSessions.CodexIdFromFileName(path));

    // ---- Grok ----

    private static string GrokUser(string text) =>
        "{\"timestamp\":1788198313,\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":"
        + "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"" + text + "\"}}}}";

    private static readonly string GrokFile = System.IO.Path.Combine("/g", "sessions", "%2Fwork%2Fmy%20site", Id, "updates.jsonl");

    [Fact]
    public void GrokReadsTheEncodedFolderTheIdAndTheSummaryTitle()
    {
        var s = PastSessions.FromGrok("{\"generated_title\":\"  Site work \"}", new[] { GrokUser("hello") }, GrokFile, At, Exists)!;

        Assert.Equal(NewChatCli.Grok, s.Cli);
        Assert.Equal(Id, s.Id);
        Assert.Equal("/work/my site", s.Cwd);
        Assert.Equal("Site work", s.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("not json")]
    [InlineData("{\"generated_title\":\"\"}")]
    [InlineData("{\"other\":1}")]
    public void GrokFallsBackToTheFirstPrompt(string? summary)
    {
        var s = PastSessions.FromGrok(summary, new[] { GrokUser("hello") }, GrokFile, At, Exists)!;
        Assert.Equal("hello", s.Title);
    }

    [Fact]
    public void GrokWithNoUserTurnOrAnUnsafeIdIsSkipped()
    {
        Assert.Null(PastSessions.FromGrok(null, new string[0], GrokFile, At, Exists));
        var bad = System.IO.Path.Combine("/g", "sessions", "x", "a b", "updates.jsonl");
        Assert.Null(PastSessions.FromGrok(null, new[] { GrokUser("hi") }, bad, At, Exists));
        Assert.Null(PastSessions.FromGrok(null, new[] { GrokUser("hi") }, "updates.jsonl", At, Exists));
    }

    [Fact]
    public void GrokAtTheRootHasNoFolder()
    {
        var s = PastSessions.FromGrok(null, new[] { GrokUser("hi") }, System.IO.Path.Combine(Id, "updates.jsonl"), At, Exists)!;
        Assert.Null(s.Cwd);
        Assert.True(s.FolderMissing);
    }

    // ---- shared ----

    [Fact]
    public void TitlesAreOneLineAndClipped()
    {
        Assert.Null(PastSessions.Clip(null));
        Assert.Null(PastSessions.Clip("  \n "));
        Assert.Equal("a b c", PastSessions.Clip(" a\n b\t c "));
        var clipped = PastSessions.Clip(new string('x', 500))!;
        Assert.Equal(PastSessions.TitleLength, clipped.Length);
        Assert.EndsWith("…", clipped);
    }

    [Theory]
    [InlineData(Id, true)]
    [InlineData("abc-DEF-123", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    [InlineData("a'b", false)]
    public void SafeIds(string? id, bool safe) => Assert.Equal(safe, PastSessions.IsSafeId(id));

    // ---- QA round 1 edge cases ----

    [Fact]
    public void ClaudeCwdOnlyPastTheHeadWindowListsAsFolderMissing()
    {
        // The reader is handed the head window; a cwd beyond it is never seen.
        var head = new[] { ClaudeUser("hi", cwd: null) };
        var s = PastSessions.FromClaude(head, head, null, "/x/" + Id + ".jsonl", At, Exists)!;
        Assert.Null(s.Cwd);
        Assert.True(s.FolderMissing);
    }

    [Fact]
    public void CrlfRowsReadTheSameAsLf()
    {
        var lines = new[] { ClaudeUser("crlf prompt") + "\r", ClaudeAssistant("ok") + "\r" };
        var s = Claude("/x/" + Id + ".jsonl", null, lines)!;
        Assert.Equal("/work/app", s.Cwd);
        Assert.Equal("crlf prompt", s.Title);
        Assert.Equal(2, PastSessions.CountLines(NewChatCli.ClaudeCode, lines));
    }

    [Fact]
    public void ATornLastRowIsIgnored()
    {
        var s = Claude("/x/" + Id + ".jsonl", null, ClaudeUser("whole"), "{\"type\":\"user\",\"message\":{\"role\":\"us");
        Assert.Equal("whole", s!.Title);
    }

    // A picture pasted with nothing typed is a user turn with no text, and
    // the only thing in the head: the row has no title to show.
    [Fact]
    public void AnImageOnlyFirstPromptIsUntitled()
    {
        var image = "{\"type\":\"user\",\"uuid\":\"u1\",\"timestamp\":\"2026-11-20T10:00:00Z\",\"cwd\":\"/w\",\"sessionId\":\"" + Id
            + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"iVBORw0KGgo=\"}}]}}";
        Assert.Equal("(untitled)", Claude("/x/" + Id + ".jsonl", null, image)!.Title);
    }

    // ---- QA coverage batch ----

    // Empty rows, rows whose fields are the wrong JSON type, and a
    // session_meta with no payload are all passed over, not misread.
    [Fact]
    public void WrongTypedAndEmptyRowsAreSkipped()
    {
        var s = Claude("/x/" + Id + ".jsonl", null,
            "",
            "{\"type\":\"mode\",\"cwd\":5,\"sessionId\":7}",
            ClaudeUser("hi"))!;
        Assert.Equal("/work/app", s.Cwd);
        Assert.Equal(Id, s.Id);

        var codex = Codex(CodexFile, "{\"type\":\"session_meta\"}", CodexMeta(), CodexUser("x"))!;
        Assert.Equal("/work/api", codex.Cwd);
    }

    [Fact]
    public void AGrokPathWithNoDirectoryIsSkipped() =>
        Assert.Null(PastSessions.FromGrok(null, new[] { GrokUser("hi") }, System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath())!, At, Exists));

    // ---- counting ----

    [Fact]
    public void ClaudeAndCodexCountOneTurnPerTurnRow()
    {
        Assert.Equal(3, PastSessions.CountLines(NewChatCli.ClaudeCode,
            new[] { ClaudeUser("a"), "{\"type\":\"summary\"}", ClaudeAssistant("b"), "garbage", ClaudeUser("c") }));
        Assert.Equal(2, PastSessions.CountLines(NewChatCli.Codex,
            new[] { CodexMeta(), CodexUser("a"), CodexUser("b"), "{\"type\":\"turn_context\"}" }));
    }

    private static string GrokUpdate(string kind, string extra = "") =>
        "{\"timestamp\":1,\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"" + kind + "\"" + extra + "}}}";

    [Fact]
    public void GrokCountsRunsOfOneChunkKindAndEachToolCall()
    {
        var lines = new[]
        {
            GrokUpdate("user_message_chunk"), GrokUpdate("user_message_chunk"),
            GrokUpdate("agent_thought_chunk"),
            GrokUpdate("agent_message_chunk"), GrokUpdate("agent_message_chunk"),
            GrokUpdate("tool_call"), GrokUpdate("tool_call"),
            GrokUpdate("tool_call_update"),
            GrokUpdate("agent_message_chunk"),
            "{\"other\":1}",
        };
        Assert.Equal(6, PastSessions.CountLines(NewChatCli.Grok, lines));
    }

    [Fact]
    public void CountMessagesGoesThroughItsSeam()
    {
        try
        {
            PastSessions.CountForTests = (cli, path) => cli == NewChatCli.Grok && path == "/p" ? 7 : null;
            Assert.Equal(7, PastSessions.CountMessages(NewChatCli.Grok, "/p"));
        }
        finally { PastSessions.CountForTests = null; }
    }

    // ---- List ----

    private static PastSession S(string id, int daysAgo, string title = "t", string? cwd = "/w/a", NewChatCli cli = NewChatCli.ClaudeCode) =>
        new(cli, id, cwd, title, At.AddDays(-daysAgo), 1, "/p/" + id, false);

    private static readonly IReadOnlySet<string> NoneLive = new HashSet<string>();

    [Fact]
    public void ListIsNewestFirstAndCutsAtThirtyDaysUnlessShowingOlder()
    {
        var all = new[] { S("a", 40), S("b", 1), S("c", 10), S("d", 30) };

        Assert.Equal(new[] { "b", "c", "d" }, PastSessions.List(all, NoneLive, null, null, At, false).Select(r => r.Session.Id));
        Assert.Equal(new[] { "b", "c", "d", "a" }, PastSessions.List(all, NoneLive, null, null, At, true).Select(r => r.Session.Id));
    }

    [Fact]
    public void ListFiltersOnTitleAndFolderCaseInsensitively()
    {
        var all = new[] { S("a", 1, "Fix Login"), S("b", 2, "other", "/w/LOGIN-svc"), S("c", 3, "unrelated"), S("d", 4, "x", null) };

        Assert.Equal(new[] { "a", "b" }, PastSessions.List(all, NoneLive, "  login ", null, At, false).Select(r => r.Session.Id));
        Assert.Equal(4, PastSessions.List(all, NoneLive, "", null, At, false).Count);
    }

    [Fact]
    public void ListFiltersByFolderIgnoringATrailingSlash()
    {
        var all = new[] { S("a", 1, cwd: "/w/a/"), S("b", 2, cwd: "/w/b"), S("c", 3, cwd: null) };

        Assert.Equal(new[] { "a" }, PastSessions.List(all, NoneLive, null, "/w/a", At, false).Select(r => r.Session.Id));
    }

    [Fact]
    public void ListMarksLiveRowsAndDedupesOnCliAndId()
    {
        var all = new[] { S("a", 3), S("a", 1, "newer"), S("a", 2, cli: NewChatCli.Codex), S("b", 4) };
        var rows = PastSessions.List(all, new HashSet<string> { "a" }, null, null, At, false);

        Assert.Equal(3, rows.Count);
        Assert.Equal("newer", rows[0].Session.Title);
        Assert.True(rows[0].IsLive);
        Assert.False(rows[2].IsLive);
    }

    [Fact]
    public void TheThirtyDayBoundaryIsInclusive()
    {
        var exactly = S("edge", 0) with { LastActivity = At - PastSessions.RecentWindow };
        var justOver = S("over", 0) with { LastActivity = At - PastSessions.RecentWindow - TimeSpan.FromSeconds(1) };
        Assert.Equal(new[] { "edge" }, PastSessions.List(new[] { exactly, justOver }, NoneLive, null, null, At, false).Select(r => r.Session.Id));
    }

    // Live is keyed on the id alone. Every id these CLIs write is a UUID, so a
    // shared id under two CLIs does not happen by chance; if it ever did, both
    // rows read live and choosing either only focuses an orb, never launches —
    // the safe direction to be wrong in.
    [Fact]
    public void ASharedIdUnderTwoClisReadsLiveOnBoth()
    {
        var rows = PastSessions.List(new[] { S("a", 1), S("a", 2, cli: NewChatCli.Grok) }, new HashSet<string> { "a" }, null, null, At, false);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsLive));
    }

    [Fact]
    public void ListBreaksTiesOnId()
    {
        var all = new[] { S("b", 1), S("a", 1) };
        Assert.Equal(new[] { "a", "b" }, PastSessions.List(all, NoneLive, null, null, At, false).Select(r => r.Session.Id));
    }

    [Fact]
    public void FoldersAreDistinctMostRecentFirst()
    {
        var all = new[] { S("a", 5, cwd: "/w/old"), S("b", 1, cwd: "/w/new/"), S("c", 3, cwd: "/w/new"), S("d", 2, cwd: null), S("e", 2, cwd: "/") };

        Assert.Equal(new[] { "/w/new", "/", "/w/old" }, PastSessions.Folders(all));
    }
}
