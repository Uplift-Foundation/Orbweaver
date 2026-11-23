using System.Linq;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-361: the resume arguments, the command lines that carry them, the
// --help gate and every reason a resume is refused before anything starts.
public class ResumeCommandTests
{
    private const string Id = "11111111-2222-4333-8444-555555555555";

    [Theory]
    [InlineData("ClaudeCode", "--resume")]
    [InlineData("Codex", "resume")]
    [InlineData("Grok", "--resume")]
    public void ResumeArgsUseEachCliSpelling(string cli, string verb) =>
        Assert.Equal(new[] { verb, Id }, NewChatCommand.ResumeArgs(System.Enum.Parse<NewChatCli>(cli), Id));

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x;reboot")]
    [InlineData("$(id)")]
    public void ResumeArgsRefuseAnUnsafeId(string id) =>
        Assert.Null(NewChatCommand.ResumeArgs(NewChatCli.ClaudeCode, id));

    [Fact]
    public void ExecLineWithArgsKeepsTheEnvFormAndQuotesEachArgument()
    {
        Assert.Equal("exec env CODEX_HOME='/h/.codex-work' '/bin/codex' 'resume' '" + Id + "'",
            NewChatCommand.ExecLine(NewChatCli.Codex, "/bin/codex", "/h/.codex-work", new[] { "resume", Id }));
        Assert.Equal("exec '/bin/claude' '--resume' 'it'\\''s'",
            NewChatCommand.ExecLine(NewChatCli.ClaudeCode, "/bin/claude", null, new[] { "--resume", "it's" }));
        Assert.Equal(NewChatCommand.ExecLine(NewChatCli.Grok, "/bin/grok"),
            NewChatCommand.ExecLine(NewChatCli.Grok, "/bin/grok", null, new string[0]));
    }

    [Fact]
    public void WindowsStartInfoCarriesTheResumeArgumentsAsSeparateWords()
    {
        var wt = NewChatCommand.WindowsProcessStartInfo(NewChatCli.Grok, @"C:\bin\grok.exe", @"C:\w x", true, @"C:\u\.grok-2", new[] { "--resume", Id })!;
        Assert.Equal("wt.exe", wt.FileName);
        Assert.Equal(new[] { "-d", @"C:\w x", "cmd.exe", "/k", @"C:\bin\grok.exe", "--resume", Id }, wt.ArgumentList.ToArray());
        Assert.False(wt.UseShellExecute);
        Assert.Equal(@"C:\u\.grok-2", wt.Environment["GROK_HOME"]);

        var cmd = NewChatCommand.WindowsProcessStartInfo(NewChatCli.Codex, @"C:\bin\codex.cmd", @"C:\w", false, null, new[] { "resume", Id })!;
        Assert.Equal(new[] { "/k", @"C:\bin\codex.cmd", "resume", Id }, cmd.ArgumentList.ToArray());
        Assert.True(cmd.UseShellExecute);
    }

    // ---- the --help gate ----

    [Theory]
    [InlineData("ClaudeCode", "  -r, --resume [value]   Resume a conversation", true)]
    [InlineData("ClaudeCode", "--resume", true)]
    [InlineData("ClaudeCode", "  -c, --continue", false)]
    [InlineData("ClaudeCode", "  --resume-session-at <id>", false)]
    [InlineData("ClaudeCode", "  --no-resume", false)]
    [InlineData("ClaudeCode", "  x--resume", false)]
    [InlineData("Grok", "--resume <ID>\n", true)]
    [InlineData("Grok", "  --fork-session", false)]
    [InlineData("Codex", "Commands:\n  exec        Run\n  resume      Resume a previous interactive session\n", true)]
    [InlineData("Codex", "Commands:\r\n  resume      Resume\r\n", true)]
    [InlineData("Codex", "Options:\n  --resume   (a flag is not the subcommand)\n", false)]
    [InlineData("Codex", "resume at column zero is prose", false)]
    [InlineData("Codex", "Commands:\n  exec        Run a thing and then\n              resume it later when done\n", false)]
    [InlineData("Codex", "Commands:\n  resume-foo  Not it\n  resumex     Nor this\n", false)]
    [InlineData("Codex", "Commands:\n  exec  Run\nOptions:\n  resume      outside the Commands block\n", false)]
    [InlineData("Codex", "  resume      before any Commands heading\n", false)]
    [InlineData("Codex", null, false)]
    public void CheckReadsTheHelpText(string cli, string? help, bool supported) =>
        Assert.Equal(supported, ResumeSupport.Check(System.Enum.Parse<NewChatCli>(cli), help).Supported);

    [Fact]
    public void ARefusalNamesTheCliAndItsVersion()
    {
        Assert.Equal("This version of Grok can't resume from here (0.9.1).", ResumeSupport.Check(NewChatCli.Grok, "", " 0.9.1 ").Message);
        Assert.Equal("This version of Codex can't resume from here.", ResumeSupport.Check(NewChatCli.Codex, "", "  ").Message);
        Assert.Equal("Claude Code can resume sessions.", ResumeSupport.Check(NewChatCli.ClaudeCode, "--resume").Message);
    }

    [Theory]
    [InlineData("2.1.330 (Claude Code)", "2.1.330")]
    [InlineData("codex-cli 0.154.0", "0.154.0")]
    [InlineData("no digits", null)]
    [InlineData("v7", null)]
    [InlineData(null, null)]
    public void VersionFrom(string? text, string? expected) =>
        Assert.Equal(expected, ResumeSupport.VersionFrom(text));

    // ---- refusals ----

    private static PastSession S(string id = Id, string? cwd = "/w") =>
        new(NewChatCli.Codex, id, cwd, "t", default, 1, "/p", false);

    private static readonly ResumeCheck Yes = new(true, "ok");

    [Fact]
    public void ResumeRefusalsComeInOrder()
    {
        Assert.Equal(LaunchOutcome.NotFound, NewChatLauncher.ResumeRefusal(S(), null, true, Yes)!.Outcome);
        Assert.Equal("That session's id can't be resumed from here.", NewChatLauncher.ResumeRefusal(S("a b"), "/bin/codex", true, Yes)!.Message);
        Assert.Equal("That session's folder is missing (/w).", NewChatLauncher.ResumeRefusal(S(), "/bin/codex", false, Yes)!.Message);
        Assert.Equal("That session's folder is missing.", NewChatLauncher.ResumeRefusal(S(cwd: null), "/bin/codex", true, Yes)!.Message);
        var no = new ResumeCheck(false, "too old");
        var refused = NewChatLauncher.ResumeRefusal(S(), "/bin/codex", true, no)!;
        Assert.Equal(LaunchOutcome.Unsupported, refused.Outcome);
        Assert.Equal("too old", refused.Message);
        Assert.Null(NewChatLauncher.ResumeRefusal(S(), "/bin/codex", true, Yes));
        Assert.Null(NewChatLauncher.ResumeRefusal(S(), "/bin/codex", true, null));
    }

    [Fact]
    public void ADecidedResumeSaysResumed() =>
        Assert.Equal("Codex resumed in /w.",
            NewChatLauncher.Decide("Codex", "/bin/codex", "/w", NewChatPlatform.MacOS, null, "%3", null, "resumed").Message);

    [Fact]
    public void ResumeGoesThroughItsSeam()
    {
        try
        {
            PastSession? seen = null;
            NewChatLauncher.ResumeForTests = (s, profile) => { seen = s; return new LaunchResult(LaunchOutcome.Launched, profile ?? "-"); };
            var result = NewChatLauncher.Resume(S(), "/h/.codex-2");
            Assert.Equal("/h/.codex-2", result.Message);
            Assert.Equal(Id, seen!.Id);
        }
        finally { NewChatLauncher.ResumeForTests = null; }
    }

    [Fact]
    public void ProbeGoesThroughItsSeam()
    {
        try
        {
            ResumeSupport.ProbeForTests = (cli, bin) => ResumeSupport.Check(cli, bin);
            Assert.True(ResumeSupport.Probe(NewChatCli.Grok, "--resume").Supported);
        }
        finally { ResumeSupport.ProbeForTests = null; }
    }
}
