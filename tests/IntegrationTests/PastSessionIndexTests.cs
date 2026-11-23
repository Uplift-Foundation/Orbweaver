using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// CB-361: the walk and the stamp cache behind Resume, over a real temp tree
// laid out the way each CLI lays out its home. The real seams are used, so
// this is the same disk access the dialog makes.
public class PastSessionIndexTests : IDisposable
{
    private const string A = "11111111-2222-4333-8444-555555555555";
    private const string B = "22222222-2222-4333-8444-555555555555";
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("cb361-").FullName;
    private readonly string _work;

    public PastSessionIndexTests()
    {
        _work = Path.Combine(_root, "work dir");
        Directory.CreateDirectory(_work);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static string Esc(string s) => s.Replace("\\", "\\\\");

    private string Write(string path, DateTimeOffset at, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        return path;
    }

    private string ClaudeUser(string id, string text, string? cwd = null) =>
        "{\"type\":\"user\",\"uuid\":\"u\",\"timestamp\":\"2026-11-20T10:00:00Z\",\"cwd\":\"" + Esc(cwd ?? _work)
        + "\",\"sessionId\":\"" + id + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    private static IReadOnlyList<PastSession> Read(PastSessionIndex index, NewChatCli cli, string home, bool older = false) =>
        index.Read(cli, home, older, Now);

    [Fact]
    public void ClaudeListsTopLevelTranscriptsOnlyAndCutsAtTheWindow()
    {
        var home = Path.Combine(_root, ".claude");
        Write(Path.Combine(home, "projects", "-work", A + ".jsonl"), Now.AddDays(-1), ClaudeUser(A, "recent"));
        Write(Path.Combine(home, "projects", "-work", B + ".jsonl"), Now.AddDays(-45), ClaudeUser(B, "old"));
        Write(Path.Combine(home, "projects", "-work", A, "subagents", "agent-1.jsonl"), Now, ClaudeUser("33333333-0000-4000-8000-000000000000", "sub"));
        Write(Path.Combine(home, "projects", "-work", "empty.jsonl"), Now, "{\"type\":\"summary\"}");

        var index = new PastSessionIndex(PastSessionIndex.Real);

        var recent = Assert.Single(Read(index, NewChatCli.ClaudeCode, home));
        Assert.Equal(A, recent.Id);
        Assert.Equal(_work, recent.Cwd);
        Assert.False(recent.FolderMissing);

        Assert.Equal(new[] { A, B }, Read(index, NewChatCli.ClaudeCode, home, older: true).Select(s => s.Id).OrderBy(x => x));
    }

    [Fact]
    public void AnUnchangedFileIsNotReparsedButAChangedOneIs()
    {
        var home = Path.Combine(_root, ".claude");
        var file = Write(Path.Combine(home, "projects", "-work", A + ".jsonl"), Now.AddDays(-1), ClaudeUser(A, "one"));
        int heads = 0, counts = 0;
        var seams = PastSessionIndex.Real with
        {
            Head = (p, n) => { heads++; return PastSessionIndex.Real.Head(p, n); },
            Lines = p => { counts++; return PastSessionIndex.Real.Lines(p); },
        };
        var index = new PastSessionIndex(seams);

        Read(index, NewChatCli.ClaudeCode, home);
        Read(index, NewChatCli.ClaudeCode, home);
        Assert.Equal(1, heads);
        Assert.Equal(1, index.Count(NewChatCli.ClaudeCode, file));
        Assert.Equal(1, index.Count(NewChatCli.ClaudeCode, file));
        Assert.Equal(1, counts);

        Write(file, Now.AddDays(-1).AddMinutes(1), ClaudeUser(A, "one"), ClaudeUser(A, "two"));
        Assert.Null(Assert.Single(Read(index, NewChatCli.ClaudeCode, home)).MessageCount);
        Assert.Equal(2, heads);
        Assert.Equal(2, index.Count(NewChatCli.ClaudeCode, file));
        Assert.Equal(2, counts);
        Assert.Null(index.Count(NewChatCli.ClaudeCode, Path.Combine(_root, "gone.jsonl")));
    }

    [Fact]
    public void AFolderDeletedAfterCachingIsMarkedMissingOnTheNextRead()
    {
        var home = Path.Combine(_root, ".claude");
        var gone = Path.Combine(_root, "gone");
        Directory.CreateDirectory(gone);
        Write(Path.Combine(home, "projects", "-gone", A + ".jsonl"), Now.AddDays(-1), ClaudeUser(A, "x", gone));
        var index = new PastSessionIndex(PastSessionIndex.Real);

        Assert.False(Assert.Single(Read(index, NewChatCli.ClaudeCode, home)).FolderMissing);
        Directory.Delete(gone);
        Assert.True(Assert.Single(Read(index, NewChatCli.ClaudeCode, home)).FolderMissing);
    }

    // A resume appends to the ORIGINAL rollout, so a session started long ago
    // and resumed yesterday sits in an old day directory with a fresh mtime.
    // The window is the file's mtime, never the directory's date.
    [Fact]
    public void CodexFiltersOnFileMtimeNotTheDayDirectory()
    {
        var home = Path.Combine(_root, ".codex");
        string Meta(string id) => "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"" + Esc(_work) + "\",\"originator\":\"codex-tui\"}}";
        const string User = "{\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}}";
        // Started in January, resumed yesterday.
        Write(Path.Combine(home, "sessions", "2026", "01", "02", "rollout-2026-01-02T10-00-00-" + A + ".jsonl"), Now.AddDays(-1), Meta(A), User);
        // Recent directory, but untouched for 45 days.
        Write(Path.Combine(home, "sessions", "2026", "11", "19", "rollout-2026-11-19T10-00-00-" + B + ".jsonl"), Now.AddDays(-45), Meta(B), User);

        var index = new PastSessionIndex(PastSessionIndex.Real);

        Assert.Equal(new[] { A }, Read(index, NewChatCli.Codex, home).Select(s => s.Id));
        Assert.Equal(2, Read(index, NewChatCli.Codex, home, older: true).Count);
    }

    [Fact]
    public void ACodexRolloutWhoseFirstRowRunsPastTheCapIsSkipped()
    {
        var home = Path.Combine(_root, ".codex");
        var file = Write(Path.Combine(home, "sessions", "2026", "11", "19", "rollout-2026-11-19T10-00-00-" + A + ".jsonl"), Now.AddDays(-1),
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + A + "\",\"x\":\"" + new string('x', 200_000) + "\"}}");
        var seams = PastSessionIndex.Real with { CodexHead = p => TranscriptReader.HeadRows(p, 100_000, TranscriptReader.HeadBytes) };
        Assert.Empty(new PastSessionIndex(seams).Read(NewChatCli.Codex, home, false, Now));
        Assert.Single(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.Codex, home, false, Now));
        Assert.True(File.Exists(file));
    }

    // Ruling B: an account whose projects directory is a symlink to another's
    // lists the same transcripts under the same, real paths.
    [UnixFact]
    public void ASymlinkedProjectsDirectoryListsUnderItsRealPath()
    {
        var real = Path.Combine(_root, ".claude");
        var file = Write(Path.Combine(real, "projects", "-work", A + ".jsonl"), Now.AddDays(-1), ClaudeUser(A, "hi"));
        var other = Path.Combine(_root, ".claude-board");
        Directory.CreateDirectory(other);
        Directory.CreateSymbolicLink(Path.Combine(other, "projects"), Path.Combine(real, "projects"));

        var index = new PastSessionIndex(PastSessionIndex.Real);
        var viaLink = Assert.Single(Read(index, NewChatCli.ClaudeCode, other));
        var direct = Assert.Single(Read(index, NewChatCli.ClaudeCode, real));

        Assert.Equal(direct.TranscriptPath, viaLink.TranscriptPath);
        Assert.Equal(new FileInfo(file).FullName, new FileInfo(viaLink.TranscriptPath).FullName);
    }

    [Fact]
    public void AClaudeSidecarTitleIsReadFromBesideTheTranscript()
    {
        var home = Path.Combine(_root, ".claude");
        Write(Path.Combine(home, "projects", "-work", A + ".jsonl"), Now.AddDays(-1), ClaudeUser(A, "prompt"));
        Directory.CreateDirectory(Path.Combine(home, "projects", "-work", A));
        File.WriteAllText(Path.Combine(home, "projects", "-work", A, "custom-title.json"), "{\"customTitle\":\"disk cleanup\"}");

        Assert.Equal("disk cleanup", Assert.Single(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.ClaudeCode, home, false, Now)).Title);
    }

    [Fact]
    public void GrokReadsTheEncodedFolderAndTheSummaryBesideTheTranscript()
    {
        var home = Path.Combine(_root, ".grok");
        var dir = Path.Combine(home, "sessions", Uri.EscapeDataString(_work), A);
        const string User = "{\"timestamp\":1,\"method\":\"session/update\",\"params\":{\"sessionId\":\"s\",\"update\":{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hey\"}}}}";
        Write(Path.Combine(dir, "updates.jsonl"), Now.AddDays(-2), User);
        File.WriteAllText(Path.Combine(dir, "summary.json"), "{\"generated_title\":\"Grok title\"}");
        // A session directory with no transcript in it is not a session.
        Directory.CreateDirectory(Path.Combine(home, "sessions", Uri.EscapeDataString(_work), B));

        var s = Assert.Single(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.Grok, home, false, Now));
        Assert.Equal(A, s.Id);
        Assert.Equal(_work, s.Cwd);
        Assert.Equal("Grok title", s.Title);
    }

    [Fact]
    public void AMissingHomeListsNothing() =>
        Assert.Empty(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.Codex, Path.Combine(_root, "nope"), true, Now));

    // Row 0 of a real OpenClaw rollout reached 337 KB on the mini. A byte
    // window would cut it and lose the session; the streamed head keeps it.
    [Fact]
    public void ACodexSessionMetaLargerThanAnyWindowStillLists()
    {
        var home = Path.Combine(_root, ".codex");
        var meta = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + A + "\",\"cwd\":\"" + Esc(_work)
            + "\",\"base_instructions\":{\"text\":\"" + new string('i', 400_000) + "\"}}}";
        const string User = "{\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"content\":[{\"type\":\"text\",\"text\":\"after the big row\"}]}}}";
        Write(Path.Combine(home, "sessions", "2026", "11", "19", "rollout-2026-11-19T10-00-00-" + A + ".jsonl"), Now.AddDays(-1), meta, User);

        var s = Assert.Single(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.Codex, home, false, Now));
        Assert.Equal(_work, s.Cwd);
        Assert.Equal("after the big row", s.Title);
    }

    // ---- the architect's real captures (tests/TranscriptTests/Captures/cb361) ----

    private static string Captures(string cli)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "TranscriptTests", "Captures", "cb361")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "tests", "TranscriptTests", "Captures", "cb361", cli);
    }

    // The fixtures' folder does not exist here, so every row reads missing;
    // what is asserted is what the reader took from the real rows.
    private static PastSession One(NewChatCli cli, string home, string id) =>
        Assert.Single(new PastSessionIndex(PastSessionIndex.Real).Read(cli, home, true, Now), s => s.Id == id);

    // a6b87b1e is an sdk-cli (headless) session, so only the cli one lists —
    // and the sub-agent below it never does.
    [Fact]
    public void RealClaudeCapturesListTheCliSessionOnly()
    {
        var home = Captures("claude");
        var all = new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.ClaudeCode, home, true, Now);

        // A team member's session: its only user row is a <teammate-message>.
        var titled = Assert.Single(all);
        Assert.Equal("0d249aed-bfd5-40ae-8097-cb0ad9299fe5", titled.Id);
        Assert.Equal("/Users/user/Source/project", titled.Cwd);
        Assert.Equal("Scrubbed session title", titled.Title);
        Assert.True(new PastSessionIndex(PastSessionIndex.Real).Count(NewChatCli.ClaudeCode, titled.TranscriptPath) > 1);
    }

    [Fact]
    public void RealCodexCaptureSkipsTheContextBlocksForItsTitle()
    {
        var s = One(NewChatCli.Codex, Captures("codex"), "1c405cb1-d2d2-4fb8-8ef7-012d363500ee");
        Assert.Equal("/Users/user/Source/project", s.Cwd);
        // The UserMessage item, which is what the chat panel shows, not the
        // response_item copy or the <environment_context> block before it. The
        // scrubber wrote the two copies differently; this is the item's.
        Assert.Equal("scrubbed", s.Title);
    }

    // The captured Grok session is headless (grok -p), so it is not listed.
    // A copy with session_kind removed shows what the reader takes from it.
    [Fact]
    public void RealGrokCaptureIsHeadlessAndOtherwiseReadsItsFolderAndTitle()
    {
        const string id = "0570de7d-7092-4259-8d44-d356a921907c";
        Assert.Empty(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.Grok, Captures("grok"), true, Now));

        var source = Directory.GetDirectories(Path.Combine(Captures("grok"), "sessions")).Single();
        var copy = Path.Combine(_root, ".grok", "sessions", Path.GetFileName(source), id);
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(source, id, "updates.jsonl"), Path.Combine(copy, "updates.jsonl"));
        File.WriteAllText(Path.Combine(copy, "summary.json"),
            File.ReadAllText(Path.Combine(source, id, "summary.json")).Replace("\"headless\"", "\"x-interactive\""));
        File.SetLastWriteTimeUtc(Path.Combine(copy, "updates.jsonl"), Now.AddDays(-1).UtcDateTime);

        var s = Assert.Single(new PastSessionIndex(PastSessionIndex.Real).Read(NewChatCli.Grok, Path.Combine(_root, ".grok"), false, Now));
        Assert.Equal(id, s.Id);
        Assert.Equal("/Users/user/Source/project", s.Cwd);
        Assert.Equal("Scrubbed session title", s.Title);
        Assert.True(new PastSessionIndex(PastSessionIndex.Real).Count(NewChatCli.Grok, s.TranscriptPath) > 1);
    }

    [Fact]
    public void AFileThatVanishesBetweenListingAndStampIsSkipped()
    {
        var seams = PastSessionIndex.Real with
        {
            Directories = d => d.EndsWith("projects") ? new[] { "/p/x" } : Array.Empty<string>(),
            Files = (_, _) => new[] { "/p/x/" + A + ".jsonl" },
            Stamp = _ => null,
        };
        Assert.Empty(new PastSessionIndex(seams).Read(NewChatCli.ClaudeCode, "/h", false, Now));
    }

    // QA round 1: a transcript over 100 MB lists from its head and tail alone,
    // and its count streams it rather than holding it. The listing must touch
    // only the bounded reads; the count must see every turn row.
    [Fact]
    public void AHundredMegabyteTranscriptListsFromItsEndsAndCountsByStreaming()
    {
        var home = Path.Combine(_root, ".claude");
        var file = Path.Combine(home, "projects", "-big", A + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var filler = "{\"type\":\"progress\",\"data\":\"" + new string('x', 1000) + "\"}\n";
        using (var w = new StreamWriter(file))
        {
            w.Write(ClaudeUser(A, "the big one") + "\n");
            for (var i = 0; i < 105_000; i++) w.Write(filler);
            w.Write("{\"type\":\"custom-title\",\"customTitle\":\"Big session\"}\n");
            w.Write(ClaudeUser(A, "last") + "\n");
        }
        File.SetLastWriteTimeUtc(file, Now.AddHours(-1).UtcDateTime);
        Assert.True(new FileInfo(file).Length > 100L * 1024 * 1024);

        var lines = 0;
        var index = new PastSessionIndex(PastSessionIndex.Real with { Lines = p => { lines++; return PastSessionIndex.Real.Lines(p); } });

        var s = Assert.Single(index.Read(NewChatCli.ClaudeCode, home, false, Now));
        Assert.Equal("Big session", s.Title);
        Assert.Null(s.MessageCount);
        Assert.Equal(0, lines);

        Assert.Equal(2, index.Count(NewChatCli.ClaudeCode, file));
    }

    // ---- TranscriptReader.ReadHead ----

    [Fact]
    public void ReadHeadDropsTheTornLastLineOnlyWhenTheWindowStoppedShort()
    {
        var path = Write(Path.Combine(_root, "h.jsonl"), Now, "aaaa", "bbbb", "cccc");

        // 15 bytes: "aaaa\nbbbb\ncccc\n"; a 12-byte window ends inside "cccc".
        Assert.Equal(new[] { "aaaa", "bbbb" }, TranscriptReader.ReadHead(path, 12));
        Assert.Equal(new[] { "aaaa", "bbbb", "cccc" }, TranscriptReader.ReadHead(path, 1000));
        Assert.Empty(TranscriptReader.ReadHead(path, 3));
    }

    [Fact]
    public void ReadHeadRowsKeepsALongFirstRowWholeThenTheBudget()
    {
        var path = Path.Combine(_root, "c.jsonl");
        var first = new string('f', 150_000);
        File.WriteAllText(path, first + "\r\nab\ncd\nefgh\n");

        Assert.Equal(new[] { first, "ab", "cd" }, TranscriptReader.ReadHeadRows(path, 1_000_000, 7));
        Assert.Equal(new[] { first, "ab", "cd", "efgh" }, TranscriptReader.ReadHeadRows(path, 1_000_000, 1000));
        Assert.Equal(new[] { first }, TranscriptReader.ReadHeadRows(path, 1_000_000, 0));
        Assert.Null(TranscriptReader.ReadHeadRows(path, 100_000, 1000));
        Assert.Null(TranscriptReader.HeadRows(Path.Combine(_root, "missing.jsonl"), 10, 10));
    }

    [Fact]
    public void ReadHeadRowsTakesASingleUnterminatedRowUnderTheCap()
    {
        var path = Path.Combine(_root, "one.jsonl");
        File.WriteAllText(path, "only");
        Assert.Equal(new[] { "only" }, TranscriptReader.ReadHeadRows(path, 10, 10));
        File.WriteAllText(path, new string('z', 100));
        Assert.Null(TranscriptReader.ReadHeadRows(path, 10, 10));
    }

    [Fact]
    public void ReadHeadKeepsAFinalLineWithNoNewlineWhenTheWholeFileFits()
    {
        var path = Path.Combine(_root, "n.jsonl");
        File.WriteAllText(path, "one\r\ntwo\r\n\r\nthree");
        Assert.Equal(new[] { "one", "two", "three" }, TranscriptReader.ReadHead(path, 1000));
        Assert.Equal(new[] { "one", "two", "three" }, TranscriptReader.HeadLines(path));
        Assert.Empty(TranscriptReader.HeadLines(Path.Combine(_root, "missing.jsonl")));
    }

    [Theory]
    [InlineData("ClaudeCode", null, ".claude")]
    [InlineData("Codex", null, ".codex")]
    [InlineData("Grok", ".grok-work", ".grok-work")]
    public void HomeForIsTheChosenProfileElseTheDefault(string cliName, string? profile, string expected)
    {
        var cli = Enum.Parse<NewChatCli>(cliName);
        var home = Path.Combine(_root, "u");
        var got = PastSessionIndex.HomeFor(cli, home, profile is null ? null : Path.Combine(home, profile));
        Assert.Equal(Path.Combine(home, expected), got);
    }

    [Fact]
    public void LoadGoesThroughItsSeam()
    {
        try
        {
            var fixedList = new List<PastSession> { new(NewChatCli.Grok, A, "/w", "t", Now, 1, "/p", false) };
            PastSessionIndex.LoadForTests = (cli, profile, older, now) => cli == NewChatCli.Grok && older && profile == "/p" ? fixedList : new List<PastSession>();
            Assert.Same(fixedList, PastSessionIndex.Load(NewChatCli.Grok, "/p", true, Now));
        }
        finally { PastSessionIndex.LoadForTests = null; }
    }
}
