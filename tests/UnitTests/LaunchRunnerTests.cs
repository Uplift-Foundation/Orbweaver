using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-357: LaunchRunner, driven directly — what a launch does on this desk once
/// LaunchGate has passed it, shared by the phone path (RelayRemoteTests covers
/// it through RelayRemote, unchanged) and the peer path (PeerLaunchServerTests).
/// </summary>
public class LaunchRunnerTests
{
    internal sealed class Desk
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
        public bool Windows;
        public string? Binary = "/bin/claude";
        public bool DirExists = true;
        public (string? Pane, string? Why) MacPlace = ("%1", null);
        public int? WindowsPid = 42;
        public readonly List<(string Command, string Cwd)> MacStarts = [];
        public readonly List<ProcessStartInfo> WindowsStarts = [];
        public readonly List<TimeSpan> Delays = [];
        public Action? OnDelay;
        public IReadOnlyList<(string SessionId, SessionStatus Status)> Sessions = [];
        public List<NewChatAccounts.Choice> Choices = [new("Default", null), new("~/.claude-b", ".claude-b"), new("~/.claude%x", ".claude%x")];
        public string? Designated;
        public bool HasAccounts = true;
        // Absolute on both rids, so the expected values are the same rule on each.
        public string Home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cb397-home"));

        public RelayRemote.LaunchSeams Launch() => new(
            Windows,
            _ => Binary,
            _ => DirExists,
            () => "/bin/zsh",
            (command, cwd) => { MacStarts.Add((command, cwd)); return MacPlace; },
            psi => { WindowsStarts.Add(psi); return WindowsPid; },
            () => Home);

        public RelayRemote.AccountSeams Accounts() => new(_ => Choices, _ => Designated);

        public Task Delay(TimeSpan d)
        {
            Delays.Add(d);
            Now += d;
            OnDelay?.Invoke();
            return Task.CompletedTask;
        }

        public LaunchRunner Runner() => new(new LaunchRunner.Seams(
            Launch(), HasAccounts ? Accounts() : null, () => Sessions, () => Now, Delay));

        // The new session appears after the first poll.
        public void ChecksIn(string id, SessionStatus status) =>
            OnDelay = () => Sessions = [.. Sessions, (id, status)];
    }

    internal static SessionStatus Status(SessionSource source = SessionSource.ClaudeCode, string cwd = "/w") =>
        new() { State = "idle", Source = source, Cwd = cwd };

    private static LaunchFields F(string cli = "claude", string cwd = "/w", string account = "", string message = "", string model = "") =>
        new(cwd, cli, "", model, "", message, "n", 0, account);

    [Fact]
    public async Task A_mac_launch_starts_in_tmux_and_names_the_session_that_checks_in()
    {
        var d = new Desk { Sessions = [("old", Status())] };
        d.ChecksIn("new", Status(cwd: "/w/"));

        var outcome = await d.Runner().RunAsync(F(model: "opus"));

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.Launched, null, "new"), outcome);
        Assert.Equal("/w", d.MacStarts.Single().Cwd);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--model", "opus"]), d.MacStarts.Single().Command);
        Assert.Equal([RelayRemote.LaunchPoll], d.Delays);
    }

    // A session already in that folder before the start is not the new one.
    [Fact]
    public async Task A_session_that_was_already_there_never_counts_and_the_wait_ends_at_30_seconds()
    {
        var d = new Desk { Sessions = [("old", Status())] };
        d.OnDelay = () => d.Sessions = [("old", Status()), ("codex", Status(SessionSource.Codex)), ("elsewhere", Status(cwd: "/x"))];
        var start = d.Now;

        var outcome = await d.Runner().RunAsync(F());

        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.NoSession, null, null), outcome);
        Assert.Equal(RelayRemote.LaunchCheckIn, d.Now - start);
    }

    [Fact]
    public async Task Accounts_resolve_to_the_profile_or_refuse()
    {
        var named = new Desk();
        named.ChecksIn("new", Status());
        Assert.Equal("new", (await named.Runner().RunAsync(F(account: ".claude-b"))).SessionId);
        // CB-397: the exact absolute folder, not a spelling that merely contains the name.
        var expected = Path.Combine(named.Home, ".claude-b");
        Assert.True(Path.IsPathRooted(expected));
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", [], (NewChatCli.ClaudeCode, expected)), named.MacStarts.Single().Command);

        var dflt = new Desk();
        dflt.ChecksIn("new", Status());
        Assert.Equal("new", (await dflt.Runner().RunAsync(F(account: "default"))).SessionId);
        Assert.DoesNotContain(".claude-b", dflt.MacStarts.Single().Command);

        var unknown = new Desk();
        Assert.Equal(RelayReplies.UnknownAccount, (await unknown.Runner().RunAsync(F(account: "gone"))).Code);
        Assert.Empty(unknown.MacStarts);
    }

    // With no account seams only a launch that names none may proceed.
    [Fact]
    public async Task Without_accounts_a_named_account_is_unknown_and_none_is_fine()
    {
        var none = new Desk { HasAccounts = false };
        Assert.Equal(RelayReplies.UnknownAccount, (await none.Runner().RunAsync(F(account: "default"))).Code);
        Assert.Empty(none.MacStarts);

        none.ChecksIn("new", Status());
        Assert.Equal(RelayReplies.Launched, (await none.Runner().RunAsync(F())).Code);
    }

    public static TheoryData<string?, string, string> MacFailures() => new()
    {
        { TerminalScripts.NoTmuxWhy, RelayReplies.NoTerminal, TerminalScripts.NoTmuxWhy },
        { "tmux could not start a session", RelayReplies.LaunchFailed, "tmux could not start a session" },
        { null, RelayReplies.LaunchFailed, "Couldn't open a terminal for Claude Code." },
    };

    [Theory]
    [MemberData(nameof(MacFailures))]
    public async Task A_mac_launch_that_cannot_place_says_why(string? why, string code, string expected)
    {
        var d = new Desk { MacPlace = (null, why) };
        Assert.Equal(new LaunchRunner.Outcome(code, expected, null), await d.Runner().RunAsync(F()));
        Assert.Empty(d.Delays);
    }

    [Fact]
    public async Task A_windows_launch_is_conhost_with_or_without_an_account()
    {
        var plain = new Desk { Windows = true, Binary = "C:\\c\\claude.exe" };
        plain.ChecksIn("new", Status(cwd: "c:\\W"));
        Assert.Equal("new", (await plain.Runner().RunAsync(F(cwd: "C:\\w"))).SessionId);
        Assert.Equal("conhost.exe", plain.WindowsStarts.Single().FileName);
        Assert.Empty(plain.MacStarts);

        var account = new Desk { Windows = true, Binary = "C:\\c\\claude.exe" };
        account.ChecksIn("new", Status(cwd: "C:\\w"));
        Assert.Equal("new", (await account.Runner().RunAsync(F(cwd: "C:\\w", account: ".claude-b"))).SessionId);
        var expected = Path.Combine(account.Home, ".claude-b");
        Assert.Equal(
            NewChatCommand.ConsoleStartInfoForAccount(NewChatCli.ClaudeCode, "C:\\c\\claude.exe", "C:\\w", [], expected, viaCmd: true)!.ArgumentList,
            account.WindowsStarts.Single().ArgumentList);
        Assert.Contains(account.WindowsStarts.Single().ArgumentList, a => a.Contains(expected));
    }

    // CB-397: every CLI's variable carries the absolute folder, a settings entry
    // that is already absolute passes through, and naming the CLI's own default
    // folder sets no variable at all.
    public static TheoryData<string, string, string, string> HomeVariables() => new()
    {
        { "claude", ".claude-b", ".claude-b", "CLAUDE_CONFIG_DIR" },
        { "codex", ".codex-b", ".codex-b", "CODEX_HOME" },
        { "grok", ".grok-b", ".grok-b", "GROK_HOME" },
    };

    [Theory]
    [MemberData(nameof(HomeVariables))]
    public async Task A_relative_account_reaches_each_clis_variable_as_an_absolute_folder(string cli, string entry, string rel, string variable)
    {
        var d = new Desk { Choices = [new("Default", null), new("named", entry)] };
        d.ChecksIn("new", Status(cli == "codex" ? SessionSource.Codex : cli == "grok" ? SessionSource.Grok : SessionSource.ClaudeCode, "/w"));
        Assert.Equal("new", (await d.Runner().RunAsync(F(cli: cli, account: entry))).SessionId);

        var command = d.MacStarts.Single().Command;
        Assert.Contains("env " + variable + "=", command);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", NewChatCommand.LaunchArgs(LaunchGate.CliFor(cli), "", "", ""), (LaunchGate.CliFor(cli), Path.Combine(d.Home, rel))), command);
    }

    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("codex", ".codex")]
    [InlineData("grok", ".grok")]
    public async Task A_named_account_that_is_the_clis_default_folder_sets_no_variable(string cli, string entry)
    {
        var d = new Desk { Choices = [new("Default", null), new("named", entry)] };
        d.ChecksIn("new", Status(cwd: "/w"));
        await d.Runner().RunAsync(F(cli: cli, account: entry));
        Assert.DoesNotContain("env ", d.MacStarts.Single().Command);
        Assert.DoesNotContain("_CONFIG_DIR", d.MacStarts.Single().Command);
        Assert.DoesNotContain("_HOME=", d.MacStarts.Single().Command);
    }

    [Fact]
    public async Task An_absolute_settings_entry_passes_through_unchanged()
    {
        var d = new Desk();
        var abs = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere", ".claude-z"));
        d.Choices = [new("Default", null), new("abs", abs)];
        d.ChecksIn("new", Status(cwd: "/w"));
        await d.Runner().RunAsync(F(account: abs));
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", [], (NewChatCli.ClaudeCode, abs)), d.MacStarts.Single().Command);
    }

    // Without a Home seam the real user profile is what a relative entry resolves against.
    [Fact]
    public async Task Without_a_home_seam_the_user_profile_folder_is_used()
    {
        var d = new Desk();
        var seams = d.Launch() with { Home = null };
        d.ChecksIn("new", Status(cwd: "/w"));
        var runner = new LaunchRunner(new LaunchRunner.Seams(seams, d.Accounts(), () => d.Sessions, () => d.Now, d.Delay));
        await runner.RunAsync(F(account: ".claude-b"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", [], (NewChatCli.ClaudeCode, Path.Combine(home, ".claude-b"))), d.MacStarts.Single().Command);
        Assert.Equal(home, LaunchRunner.DefaultLaunchSeams().Home!());
    }

    [Fact]
    public async Task Windows_refusals_name_why()
    {
        // A profile folder cmd would re-parse.
        var unsafeDir = new Desk { Windows = true, Binary = "C:\\c\\claude.exe" };
        Assert.Equal(RelayReplies.LaunchFailed, (await unsafeDir.Runner().RunAsync(F(cwd: "C:\\w", account: ".claude%x"))).Code);
        Assert.Empty(unsafeDir.WindowsStarts);

        // A script shim handed a message.
        var shim = new Desk { Windows = true, Binary = "C:\\npm\\codex.cmd" };
        var refused = await shim.Runner().RunAsync(F(cli: "codex", cwd: "C:\\w", message: "hi"));
        Assert.Equal("Codex here is a script, which cannot be handed a message safely.", refused.Why);
        Assert.Empty(shim.WindowsStarts);

        // A console that would not open.
        var noPid = new Desk { Windows = true, Binary = "C:\\c\\claude.exe", WindowsPid = null };
        Assert.Equal(new LaunchRunner.Outcome(RelayReplies.LaunchFailed, "Couldn't open a console for Claude Code.", null),
            await noPid.Runner().RunAsync(F(cwd: "C:\\w")));
    }

    // The gate's view of this desk: a parent must be one of its own local
    // CLI sessions, never an OpenClaw conversation.
    [Fact]
    public void The_environment_reflects_the_desk()
    {
        var d = new Desk { Windows = true, DirExists = false, Binary = null,
            Sessions = [("lead", Status()), ("oc", new SessionStatus { Source = SessionSource.OpenClaw })] };
        var env = d.Runner().Environment();

        Assert.True(env.IsWindows);
        Assert.False(env.CliInstalled("claude"));
        Assert.False(env.DirectoryExists("C:\\w"));
        Assert.True(env.InRoster("lead"));
        Assert.False(env.InRoster("oc"));
        Assert.False(env.InRoster("gone"));
    }

    [Fact]
    public void Same_folder_rule()
    {
        Assert.True(LaunchRunner.SameFolder("/w/", "/w", false));
        Assert.False(LaunchRunner.SameFolder("/W", "/w", false));
        Assert.True(LaunchRunner.SameFolder("C:\\W\\", "c:\\w", true));
        Assert.False(LaunchRunner.SameFolder(null, "/w", false));
    }

    // The override that keeps the loopback suites from opening a terminal. It
    // is read at call time, so seams built before it was set (PeerLaunchServer.
    // Shared builds its once per process) still honour it, and nothing built
    // after it is cleared keeps a stale fake.
    [Fact]
    public void TheStartOverrideIsOffByDefaultAndIsReadWhenTheStartIsCalled()
    {
        Assert.Null(LaunchRunner.StartForTests);

        // Built while the override is null, as Shared's is.
        var seams = LaunchRunner.DefaultLaunchSeams();
        var placed = new List<(string, string)>();
        var started = 0;

        LaunchRunner.StartForTests = new LaunchRunner.StartOverride(
            (command, cwd) => { placed.Add((command, cwd)); return ("%9", null); },
            _ => { started++; return 77; });
        try
        {
            Assert.Equal(("%9", (string?)null), seams.PlaceMac("claude", "/w"));
            Assert.Equal(77, seams.StartWindows(new ProcessStartInfo("claude")));
            Assert.Equal([("claude", "/w")], placed);
            Assert.Equal(1, started);
        }
        finally
        {
            LaunchRunner.StartForTests = null;
        }
    }
}
