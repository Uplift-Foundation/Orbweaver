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
        var reads = 0;
        var seams = PastSessionIndex.Real with { AllLines = p => { reads++; return PastSessionIndex.Real.AllLines(p); } };
        var index = new PastSessionIndex(seams);

        Read(index, NewChatCli.ClaudeCode, home);
        Read(index, NewChatCli.ClaudeCode, home);
        Assert.Equal(1, reads);

        Write(file, Now.AddDays(-1).AddMinutes(1), ClaudeUser(A, "one"), ClaudeUser(A, "two"));
        Assert.Equal(2, Assert.Single(Read(index, NewChatCli.ClaudeCode, home)).MessageCount);
        Assert.Equal(2, reads);
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

    [Fact]
    public void CodexWalksDayDirectoriesAndSkipsOnesOlderThanTheWindow()
    {
        var home = Path.Combine(_root, ".codex");
        string Meta(string id) => "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"" + Esc(_work) + "\"}}";
        const string User = "{\"ordinal\":1,\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}}";
        Write(Path.Combine(home, "sessions", "2026", "11", "19", "rollout-2026-11-19T10-00-00-" + A + ".jsonl"), Now.AddDays(-1), Meta(A), User);
        // Recently written, but in a day directory far outside the window: skipped without being read.
        Write(Path.Combine(home, "sessions", "2026", "01", "02", "rollout-2026-01-02T10-00-00-" + B + ".jsonl"), Now, Meta(B), User);
        // Not a date: walked anyway rather than guessed at.
        Write(Path.Combine(home, "sessions", "misc", "x", "y", "rollout-2026-11-19T10-00-00-33333333-0000-4000-8000-000000000000.jsonl"), Now, Meta("33333333-0000-4000-8000-000000000000"), User);

        var index = new PastSessionIndex(PastSessionIndex.Real);

        Assert.Equal(new[] { A, "33333333-0000-4000-8000-000000000000" }, Read(index, NewChatCli.Codex, home).Select(s => s.Id).OrderBy(x => x));
        Assert.Equal(3, Read(index, NewChatCli.Codex, home, older: true).Count);
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

    [Theory]
    [InlineData("2026", "11", "20", true)]
    [InlineData("2026", "02", "30", false)]
    [InlineData("2026", "13", "01", false)]
    [InlineData("0", "01", "01", false)]
    [InlineData("x", "01", "01", false)]
    [InlineData("2026", "x", "01", false)]
    [InlineData("2026", "01", "x", false)]
    public void DayOfParsesOnlyRealDates(string y, string m, string d, bool real) =>
        Assert.Equal(real, PastSessionIndex.DayOf("/s/" + y, "/s/" + y + "/" + m, "/s/" + y + "/" + m + "/" + d) is not null);

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
