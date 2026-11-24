using Xunit;
using Desk = Orbweaver.Tests.LaunchRunnerTests.Desk;

namespace Orbweaver.Tests;

/// <summary>
/// CB-434: LaunchRunner's resume arm, in the contract's order (11539 section
/// 3 as amended by 11541 and 11543): already running (CLI and id, not
/// account), account, the phone's browse scope, the index and its folder, the
/// CLI's own version gate, the start with resume arguments, and the check-in
/// for that exact id. Every refusal also asserts nothing was started.
/// </summary>
public class LaunchRunnerResumeTests
{
    private const string Id = "0f5b1c2e-6a8d-4c11-9d3e-2b7a5f8e1c40";

    private sealed class Index
    {
        public PastSession? Found = new(NewChatCli.ClaudeCode, Id, "/w", "Fix the feed", DateTimeOffset.UnixEpoch, null, "/t.jsonl", false);
        public ResumeCheck Support = new(true, "");
        public Func<string, bool>? InScope;
        public readonly List<(NewChatCli Cli, string? Profile, string Id)> Finds = [];
        public readonly List<(NewChatCli Cli, string Binary)> Probes = [];

        public LaunchRunner.ResumeSeams Seams() => new(
            (cli, profile, id) => { Finds.Add((cli, profile, id)); return Found; },
            (cli, binary) => { Probes.Add((cli, binary)); return Support; })
        { InScope = InScope };
    }

    private static LaunchRunner Runner(Desk d, LaunchRunner.ResumeSeams? resume) =>
        new(new LaunchRunner.Seams(d.Launch(), d.Accounts(), () => d.Sessions, () => d.Now, d.Delay) { Resume = resume });

    private static LaunchFields R(string cli = "claude", string cwd = "/w", string id = Id, string account = "", string message = "") =>
        new(cwd, cli, "", "", "", message, "n", 0, account, id);

    private static void NothingStarted(Desk d)
    {
        Assert.Empty(d.MacStarts);
        Assert.Empty(d.WindowsStarts);
    }

    [Fact]
    public async Task A_claude_resume_starts_with_resume_arguments_and_waits_for_that_exact_id()
    {
        var d = new Desk();
        var index = new Index();
        // Another new session in the same folder checks in first: it is somebody
        // else's and must not satisfy the wait.
        var polls = 0;
        d.OnDelay = () =>
        {
            polls++;
            d.Sessions = polls == 1
                ? [("other", LaunchRunnerTests.Status())]
                : [("other", LaunchRunnerTests.Status()), (Id, LaunchRunnerTests.Status(cwd: "/elsewhere"))];
        };

        var outcome = await Runner(d, index.Seams()).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, Id), outcome);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--resume", Id]), d.MacStarts.Single().Command);
        Assert.Equal("/w", d.MacStarts.Single().Cwd);
        Assert.Equal([(NewChatCli.ClaudeCode, (string?)null, Id)], index.Finds);
        Assert.Equal([(NewChatCli.ClaudeCode, "/bin/claude")], index.Probes);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task A_live_id_under_the_same_cli_is_already_running_and_nothing_is_looked_up_or_started()
    {
        var d = new Desk { Sessions = [(Id, LaunchRunnerTests.Status())] };
        var index = new Index();

        // Under another account than the one it is live under: still the same
        // conversation (amendment 2, section 4), so still already running.
        var outcome = await Runner(d, index.Seams()).RunAsync(R(account: "nobody-has-this"));

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.AlreadyRunning, null, Id), outcome);
        Assert.Empty(index.Finds);
        NothingStarted(d);
    }

    [Fact]
    public async Task A_live_id_under_another_cli_is_not_this_session_and_falls_through()
    {
        var d = new Desk { Sessions = [(Id, LaunchRunnerTests.Status(SessionSource.Codex))] };
        var index = new Index { Found = null };

        var outcome = await Runner(d, index.Seams()).RunAsync(R());

        Assert.Equal(RelayReplies.UnknownResume, outcome.Code);
        Assert.Single(index.Finds);
        NothingStarted(d);
    }

    [Fact]
    public async Task An_unknown_account_is_refused_before_the_index()
    {
        var d = new Desk();
        var index = new Index();

        var outcome = await Runner(d, index.Seams()).RunAsync(R(account: "nobody-has-this"));

        Assert.Equal(RelayReplies.UnknownAccount, outcome.Code);
        Assert.Empty(index.Finds);
        NothingStarted(d);
    }

    [Fact]
    public async Task The_index_is_read_under_the_accounts_resolved_profile_directory()
    {
        var d = new Desk();
        var index = new Index { Found = null };

        await Runner(d, index.Seams()).RunAsync(R(account: ".claude-b"));

        Assert.Equal(NewChatLauncher.ConfigDirFor(NewChatCli.ClaudeCode, d.Home, ".claude-b"), index.Finds.Single().Profile);
    }

    [Fact]
    public async Task A_desk_that_cannot_resume_says_so()
    {
        var d = new Desk();

        var outcome = await Runner(d, null).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.ResumeUnsupported, "This machine can't resume sessions.", null), outcome);
        NothingStarted(d);
    }

    [Fact]
    public async Task Outside_the_phones_scope_is_unknown_resume_before_the_index_is_read()
    {
        var d = new Desk();
        var asked = new List<string>();
        var index = new Index { InScope = cwd => { asked.Add(cwd); return false; } };

        var outcome = await Runner(d, index.Seams()).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.UnknownResume, null, null), outcome);
        Assert.Equal(["/w"], asked);
        Assert.Empty(index.Finds);
        NothingStarted(d);
    }

    // Amendment 3: a phone learns nothing about a session outside its roots,
    // not even that it is running.
    [Fact]
    public async Task Outside_the_phones_scope_wins_over_already_running()
    {
        var d = new Desk { Sessions = [(Id, LaunchRunnerTests.Status())] };
        var outcome = await Runner(d, new Index { InScope = _ => false }.Seams()).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.UnknownResume, null, null), outcome);
        // Control: in scope, the same live id is already running.
        Assert.Equal(RelayReplies.AlreadyRunning, (await Runner(d, new Index { InScope = _ => true }.Seams()).RunAsync(R())).Code);
    }

    [Fact]
    public async Task A_help_text_that_could_not_be_read_is_a_failed_launch_to_retry_not_an_old_cli()
    {
        var d = new Desk();
        var index = new Index { Support = ResumeSupport.Unchecked(NewChatCli.ClaudeCode) };

        var outcome = await Runner(d, index.Seams()).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, ResumeSupport.Unchecked(NewChatCli.ClaudeCode).Message, null), outcome);
        NothingStarted(d);
    }

    [Fact]
    public async Task A_cli_gone_since_the_gate_looked_is_unknown_cli_and_nothing_is_probed()
    {
        var d = new Desk { Binary = null };
        var index = new Index();

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.UnknownCli, null, null), await Runner(d, index.Seams()).RunAsync(R()));
        Assert.Empty(index.Probes);
        NothingStarted(d);
    }

    [Fact]
    public async Task Inside_the_scope_goes_on_to_the_index()
    {
        var d = new Desk();
        d.ChecksIn(Id, LaunchRunnerTests.Status());
        var index = new Index { InScope = _ => true };

        Assert.Equal(RelayReplies.Launched, (await Runner(d, index.Seams()).RunAsync(R())).Code);
    }

    [Fact]
    public async Task An_id_not_in_the_index_is_unknown_resume()
    {
        var d = new Desk();

        var outcome = await Runner(d, new Index { Found = null }.Seams()).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.UnknownResume, null, null), outcome);
        NothingStarted(d);
    }

    // The folder is signed: what runs must be what was asked for.
    [Theory]
    [InlineData("/elsewhere")]
    [InlineData(null)]
    public async Task An_index_entry_in_another_folder_or_none_is_unknown_resume(string? recorded)
    {
        var d = new Desk();
        var index = new Index();
        index.Found = index.Found! with { Cwd = recorded };

        Assert.Equal(RelayReplies.UnknownResume, (await Runner(d, index.Seams()).RunAsync(R())).Code);
        NothingStarted(d);
    }

    [Fact]
    public async Task A_cli_too_old_to_resume_is_refused_with_its_own_sentence()
    {
        var d = new Desk();
        var index = new Index { Support = new(false, "Claude Code 0.2 can't resume by id. Update it.") };

        var outcome = await Runner(d, index.Seams()).RunAsync(R());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.ResumeUnsupported, "Claude Code 0.2 can't resume by id. Update it.", null), outcome);
        NothingStarted(d);
    }

    [Fact]
    public async Task A_codex_resume_with_no_message_answers_resume_started_without_waiting()
    {
        var d = new Desk { Binary = "/bin/codex" };
        var index = new Index();
        index.Found = index.Found! with { Cli = NewChatCli.Codex };

        var outcome = await Runner(d, index.Seams()).RunAsync(R(cli: "codex"));

        Assert.Equal(new LaunchRunner.Outcome(PeerLaunchCodes.ResumeStarted, null, Id), outcome);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/codex", ["resume", Id]), d.MacStarts.Single().Command);
        Assert.Empty(d.Delays);
    }

    // With a message Codex's hook fires on that turn, so it waits like the others.
    [Fact]
    public async Task A_codex_resume_with_a_message_passes_it_after_the_separator_and_waits()
    {
        var d = new Desk { Binary = "/bin/codex" };
        d.ChecksIn(Id, LaunchRunnerTests.Status(SessionSource.Codex));

        var outcome = await Runner(d, new Index().Seams()).RunAsync(R(cli: "codex", message: "-carry on"));

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, Id), outcome);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/codex", ["resume", Id, "--", "-carry on"]), d.MacStarts.Single().Command);
    }

    [Fact]
    public async Task A_resume_that_never_checks_in_is_no_session()
    {
        var d = new Desk();

        Assert.Equal(RelayReplies.NoSession, (await Runner(d, new Index().Seams()).RunAsync(R())).Code);
    }

    [Fact]
    public async Task A_windows_resume_starts_a_console_with_resume_arguments()
    {
        var d = new Desk { Windows = true, Binary = @"C:\bin\claude.exe" };
        d.ChecksIn(Id, LaunchRunnerTests.Status(cwd: @"C:\w"));

        var outcome = await Runner(d, new Index { Found = new Index().Found! with { Cwd = @"c:\W" } }.Seams())
            .RunAsync(R(cwd: @"C:\w"));

        Assert.Equal(RelayReplies.Launched, outcome.Code);
        var start = d.WindowsStarts.Single();
        Assert.Contains("--resume", start.ArgumentList);
        Assert.Contains(Id, start.ArgumentList);
    }
}
