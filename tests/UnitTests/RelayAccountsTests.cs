using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// The launch's optional account and the accounts frame (additive to frames-v4): what a phone may
/// name, how it is signed, parsed and resolved, and the console and tmux lines that carry it.
/// </summary>
public class RelayAccountsTests
{
    private static readonly IReadOnlyList<NewChatAccounts.Choice> Claude =
        [new("Default (~/.claude)", null), new("~/.claude-uplift", ".claude-uplift")];

    [Fact]
    public void The_accounts_frame_lists_each_clis_choices_and_nothing_is_still_a_frame()
    {
        var choices = new Dictionary<NewChatCli, IReadOnlyList<NewChatAccounts.Choice>>
        {
            [NewChatCli.ClaudeCode] = Claude,
            [NewChatCli.Codex] = [new("Default (~/.codex)", null)],
            [NewChatCli.Grok] = [],
        };
        var frame = RelayAccounts.Frame(RelayAccounts.From(c => choices[c]));
        Assert.Equal("""{"t":"accounts","accounts":[{"cli":"claude","id":"default","label":"Default (~/.claude)"},{"cli":"claude","id":".claude-uplift","label":"~/.claude-uplift"},{"cli":"codex","id":"default","label":"Default (~/.codex)"}]}""", frame);
        Assert.Equal("""{"t":"accounts","accounts":[]}""", RelayAccounts.Frame([]));
    }

    [Theory]
    [InlineData("ClaudeCode", "claude")]
    [InlineData("Codex", "codex")]
    [InlineData("Grok", "grok")]
    public void A_clis_wire_name_is_the_one_a_launch_spells(string cliName, string name)
    {
        var cli = Enum.Parse<NewChatCli>(cliName);
        Assert.Equal(name, RelayAccounts.CliName(cli));
        Assert.Equal(cli, LaunchGate.CliFor(name));
    }

    [Fact]
    public void An_account_resolves_to_what_was_named_else_the_designated_one_else_the_clis_own()
    {
        string? dir;
        Assert.True(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, ".claude-uplift", Claude, null, null, out dir));
        Assert.Equal(".claude-uplift", dir);
        Assert.True(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, ".CLAUDE-UPLIFT", Claude, null, null, out dir));   // the desk's settings spelling wins
        Assert.Equal(".claude-uplift", dir);

        Assert.True(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, "", Claude, ".claude-uplift", null, out dir));
        Assert.Equal(".claude-uplift", dir);
        Assert.True(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, "", Claude, null, ".claude-uplift", out dir));
        Assert.Equal(".claude-uplift", dir);
        Assert.True(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, "", Claude, null, null, out dir));
        Assert.Null(dir);
        Assert.True(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, RelayAccounts.Default, Claude, ".claude-uplift", null, out dir));
        Assert.Null(dir);
    }

    [Fact]
    public void An_account_the_desktop_does_not_list_does_not_resolve_even_when_it_names_the_default_row()
    {
        Assert.False(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, ".claude-other", Claude, null, null, out var dir));
        Assert.Null(dir);
        Assert.False(RelayAccounts.TryResolve(NewChatCli.ClaudeCode, "", Claude, ".gone", null, out _));   // a designation that was since removed
    }

    [Fact]
    public void A_summary_frame_is_canonical_and_escapes_its_text()
    {
        Assert.Equal("""{"t":"summary","session_id":"s1","text":"it said \"ok\" & stopped","at":1794111792}""",
            RelayAccounts.SummaryFrame("s1", "it said \"ok\" & stopped", 1794111792).Replace("\\u0026", "&"));
        using var doc = JsonDocument.Parse(RelayAccounts.SummaryFrame("s1", "x", 1));
        Assert.Equal(["t", "session_id", "text", "at"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void The_desktops_own_choices_always_start_with_the_clis_default()
    {
        foreach (var cli in new[] { NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok })
            Assert.Null(RelayAccounts.Choices(cli)[0].ProfileDir);
    }

    // ---- the wire: parse and sign ---------------------------------------------------------

    private static LaunchFields Fields(string account = "") =>
        new("/x", "claude", "", "opus", "", "hi", "n", 1794111792, account);

    [Fact]
    public void An_account_is_signed_only_when_named_between_model_and_parent()
    {
        Assert.DoesNotContain("account", LaunchGate.Body(Fields()));
        Assert.Contains("\"model\":\"opus\",\"account\":\".claude-uplift\",\"parent_session_id\":\"\"", LaunchGate.Body(Fields(".claude-uplift")));
        // An unnamed account signs exactly the body every launch signed before.
        Assert.Equal(LaunchGate.Body(new LaunchFields("/x", "claude", "", "opus", "", "hi", "n", 1794111792)), LaunchGate.Body(Fields()));
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private const string Launch = """{"t":"launch","cwd":"/x","cli":"claude","persona":"","model":"","first_message":"","nonce":"n","expires_at":9,"sig":"s"%ACCOUNT%}""";

    [Fact]
    public void A_launch_account_is_optional_and_must_be_a_string()
    {
        Assert.Equal("", ((LaunchRequest)RelayRequests.Parse(J(Launch.Replace("%ACCOUNT%", "")), "r")!).Fields.Account);
        Assert.Equal(".claude-uplift", ((LaunchRequest)RelayRequests.Parse(J(Launch.Replace("%ACCOUNT%", ",\"account\":\".claude-uplift\"")), "r")!).Fields.Account);
        Assert.IsType<BadFrameRequest>(RelayRequests.Parse(J(Launch.Replace("%ACCOUNT%", ",\"account\":3")), "r"));
    }

    // ---- the lines that carry it ------------------------------------------------------------

    [Fact]
    public void A_mac_launch_line_carries_the_account_as_the_clis_profile_variable()
    {
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--model", "opus"]), NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--model", "opus"], null));
        var line = NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--model", "opus"], (NewChatCli.ClaudeCode, "/h/.claude-uplift"));
        // The whole inner command is itself quoted for `zsh -lc`, so the parts are checked in order.
        Assert.Matches(@"exec env CLAUDE_CONFIG_DIR=.*/h/\.claude-uplift.* .*/bin/claude.* .*--model.* .*opus", line);
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--model", "opus"]));
    }

    [Fact]
    public void A_windows_claude_account_rides_the_cmd_line_and_a_codex_one_rides_the_process()
    {
        var claude = NewChatCommand.ConsoleStartInfoForAccount(NewChatCli.ClaudeCode, @"C:\bin\claude.exe", @"C:\w", ["--model", "opus"], ".claude-uplift")!;
        Assert.Equal(["cmd.exe", "/k", "set \"CLAUDE_CONFIG_DIR=.claude-uplift\" && \"C:\\bin\\claude.exe\" --model opus"], claude.ArgumentList);
        Assert.True(claude.UseShellExecute);

        var codex = NewChatCommand.ConsoleStartInfoForAccount(NewChatCli.Codex, @"C:\bin\codex.exe", @"C:\w", ["--", "fix it"], @"C:\Users\me\.codex-b", viaCmd: false)!;
        Assert.Equal([@"C:\bin\codex.exe", "--", "fix it"], codex.ArgumentList);
        Assert.False(codex.UseShellExecute);
        Assert.Equal(@"C:\Users\me\.codex-b", codex.Environment[NewChatAccountHome.For(NewChatCli.Codex).EnvVar]);
    }

    [Theory]
    [InlineData(@"C:\a&b", @"C:\bin\claude.exe", "opus")]
    [InlineData(".claude-uplift", @"C:\bin\cl%aude.exe", "opus")]
    [InlineData(".claude-uplift", @"C:\bin\claude.exe", "op|us")]
    [InlineData("", @"C:\bin\claude.exe", "opus")]
    public void A_windows_value_cmd_would_read_as_syntax_is_not_carried_that_way(string configDir, string exe, string arg)
    {
        Assert.Null(NewChatCommand.ConsoleStartInfoForAccount(NewChatCli.ClaudeCode, exe, @"C:\w", [arg], configDir));
    }
}
