using Xunit;

namespace Orbweaver.Tests;

// CB-281: the account reader. The clock and the file read are injected, so nothing here
// touches a disk, and "held" is observable as a count of reads.
public class AutoGroupAccountsTests
{
    private const string Home = "/Users/ann";

    private static string Claude(string email) =>
        "{\"oauthAccount\":{\"emailAddress\":\"" + email + "\"}}";

    private sealed class Disk
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = new();
        public string? Read(string path) { Reads.Add(path); return Files.GetValueOrDefault(path); }
    }

    // ---- HomeOf ---------------------------------------------------------------

    [Theory]
    [InlineData("/Users/ann/.claude/projects/-x/abc.jsonl", "/Users/ann/.claude")]
    [InlineData("/Users/ann/.claude-board/projects/-x/abc.jsonl", "/Users/ann/.claude-board")]
    public void AClaudeHomeIsTheConfigRootOfItsTranscript(string transcript, string home) =>
        Assert.Equal(home, AutoGroupAccounts.HomeOf(SessionSource.ClaudeCode, transcript));

    [Fact]
    public void ACodexHomeIsFourFoldersAboveItsRollout() =>
        Assert.Equal("/Users/ann/.codex", AutoGroupAccounts.HomeOf(SessionSource.Codex,
            "/Users/ann/.codex/sessions/2026/11/14/rollout-1-x.jsonl"));

    [Fact]
    public void AGrokHomeIsFourFoldersAboveItsUpdatesFile() =>
        Assert.Equal("/Users/ann/.grok", AutoGroupAccounts.HomeOf(SessionSource.Grok,
            "/Users/ann/.grok/sessions/%2FUsers%2Fann%2Frepo/01a0/updates.jsonl"));

    [Fact]
    public void AWindowsRolloutPathKeepsItsBackslashes() =>
        Assert.Equal(@"C:\Users\ann\.codex", AutoGroupAccounts.HomeOf(SessionSource.Codex,
            @"C:\Users\ann\.codex\sessions\2026\11\14\rollout-1-x.jsonl"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Users/ann/.codex/elsewhere/2026/11/14/rollout.jsonl")]
    [InlineData("/sessions/a/b/c/d")]
    [InlineData("rollout.jsonl")]
    public void ACodexPathOfAnyOtherShapeHasNoHome(string? transcript) =>
        Assert.Null(AutoGroupAccounts.HomeOf(SessionSource.Codex, transcript));

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    [InlineData(SessionSource.ClaudeCloud)]
    public void ACliWithNoLocalHomeHasNone(SessionSource source) =>
        Assert.Null(AutoGroupAccounts.HomeOf(source, "/Users/ann/.claude/projects/-x/a.jsonl"));

    // ---- Of -------------------------------------------------------------------

    [Fact]
    public void TheDefaultClaudeAccountReadsTheSiblingIdentityFile()
    {
        var disk = new Disk();
        disk.Files["/Users/ann/.claude.json"] = Claude("Ann@Example.org");
        var accounts = new AutoGroupAccounts(Home, read: disk.Read);

        var account = accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude");

        Assert.Equal(("claude\nann@example.org", "Ann"), account);
        Assert.Equal(new[] { "/Users/ann/.claude.json" }, disk.Reads);
    }

    [Fact]
    public void AnotherClaudeProfileReadsItsOwnIdentityFile()
    {
        var disk = new Disk();
        disk.Files["/Users/ann/.claude-board/.claude.json"] = Claude("board@example.org");
        var accounts = new AutoGroupAccounts(Home, read: disk.Read);

        Assert.Equal(("claude\nboard@example.org", "board"),
            accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude-board"));
    }

    [Fact]
    public void AClaudeProfileThatNeverLoggedInIsUnknown()
    {
        var accounts = new AutoGroupAccounts(Home, read: _ => null);
        Assert.Null(accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude"));
    }

    [Fact]
    public void CodexAndGrokKeyOnTheLabelTheirAuthFileGives()
    {
        var disk = new Disk();
        disk.Files["/Users/ann/.codex/auth.json"] = "{\"email\":\"Cody@example.org\"}";
        disk.Files["/Users/ann/.grok/auth.json"] = "{\"x\":{\"email\":\"grace@example.org\"}}";
        var accounts = new AutoGroupAccounts(Home, read: disk.Read);

        Assert.Equal(("codex\ncody", "Cody"), accounts.Of(SessionSource.Codex, "/Users/ann/.codex"));
        Assert.Equal(("grok\ngrace", "grace"), accounts.Of(SessionSource.Grok, "/Users/ann/.grok"));
    }

    [Fact]
    public void CodexAndGrokWithNoAuthFileAreUnknown()
    {
        var accounts = new AutoGroupAccounts(Home, read: _ => null);
        Assert.Null(accounts.Of(SessionSource.Codex, "/Users/ann/.codex"));
        Assert.Null(accounts.Of(SessionSource.Grok, "/Users/ann/.grok"));
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    [InlineData(SessionSource.ClaudeCloud)]
    public void ACliWithNoAccountOfItsOwnIsUnknown(SessionSource source) =>
        Assert.Null(new AutoGroupAccounts(Home, read: _ => "{}").Of(source, "/Users/ann/.claude"));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void NoHomeIsUnknown(string? home) =>
        Assert.Null(new AutoGroupAccounts(Home, read: _ => "{}").Of(SessionSource.ClaudeCode, home));

    [Fact]
    public void AnAnswerIsHeldThenReadAgainAfterItExpires()
    {
        var disk = new Disk();
        disk.Files["/Users/ann/.claude.json"] = Claude("a@example.org");
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var accounts = new AutoGroupAccounts(Home, () => clock, disk.Read);

        accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude");
        accounts.Of(SessionSource.ClaudeCode, "/USERS/ann/.claude");
        Assert.Single(disk.Reads);

        disk.Files["/Users/ann/.claude.json"] = Claude("b@example.org");
        clock += AutoGroupAccounts.HeldFor + TimeSpan.FromSeconds(1);
        Assert.Equal("claude\nb@example.org", accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude")!.Value.Key);
        Assert.Equal(2, disk.Reads.Count);
    }

    [Fact]
    public void AnUnknownAnswerIsHeldToo()
    {
        var disk = new Disk();
        var accounts = new AutoGroupAccounts(Home, read: disk.Read);
        accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude");
        accounts.Of(SessionSource.ClaudeCode, "/Users/ann/.claude");
        Assert.Single(disk.Reads);
    }

    [Fact]
    public void TheRealReaderReadsAFileAndShrugsOffAMissingOrUnreadableOne()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb281-acct-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(home, ".claude-x"));
        try
        {
            var accounts = new AutoGroupAccounts(home);
            Assert.Null(accounts.Of(SessionSource.ClaudeCode, Path.Combine(home, ".claude-x")));

            File.WriteAllText(Path.Combine(home, ".claude-y.json"), "x");
            Directory.CreateDirectory(Path.Combine(home, ".claude-y"));
            File.WriteAllText(Path.Combine(home, ".claude-y", ".claude.json"), Claude("y@example.org"));
            Assert.Equal("y", new AutoGroupAccounts(home).Of(SessionSource.ClaudeCode, Path.Combine(home, ".claude-y"))!.Value.Label);

            // A directory where the file should be: File.Exists is false, still null, no throw.
            Directory.CreateDirectory(Path.Combine(home, ".claude-z", ".claude.json"));
            Assert.Null(new AutoGroupAccounts(home).Of(SessionSource.ClaudeCode, Path.Combine(home, ".claude-z")));
        }
        finally { Directory.Delete(home, true); }
    }
}
