using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-359, the remote-OAuth form: the approval prompt for an authentication tool
// Claude Code offers for a remote MCP server that needs sign-in ("Allow Claude to
// use complete_authentication?") never reached Orbweaver. These tests read a REAL
// capture of it (Fixtures/mcp-auth-approval, see the README there), made by
// tests/fixtures/mcp-auth/capture.sh from a default-mode terminal session whose
// approval was left pending. They fail until that capture is committed. The stdio form is McpToolApprovalCaptureTests.
public class McpAuthApprovalCaptureTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "McpAuthApprovalFixtures");

    private static string Read(string name)
    {
        var path = Path.Combine(Dir, name);
        Assert.True(File.Exists(path), $"CB-359 remote-auth capture missing: {name} (see Fixtures/mcp-auth-approval/README.md)");
        return File.ReadAllText(path);
    }

    private static string RepoFile(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Orbweaver.sln")) &&
               !Directory.Exists(Path.Combine(d.FullName, "tools"))) d = d.Parent;
        return Path.Combine(new[] { d!.FullName }.Concat(parts).ToArray());
    }

    private static IEnumerable<JsonDocument> Notifications() =>
        Read("notifications.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonDocument.Parse(l));

    private static bool InstallersMapToWaiting(string type) =>
        new[] { "install-macos-hooks.sh", "install-windows-hooks.ps1" }.All(installer =>
            Regex.IsMatch(File.ReadAllText(RepoFile("tools", installer)),
                $@"(Notification|event: 'Notification').*{Regex.Escape(type)}.*waiting"));

    [Fact]
    public void The_captured_pane_is_the_complete_authentication_approval()
    {
        var screen = Read("pane.txt");

        Assert.Matches(new Regex("complete_authentication|Complete Authentication Tool"), screen);
        Assert.Contains("Do you want to proceed?", screen);
        Assert.NotNull(PromptFingerprint.Of(screen, AskFormTranscript.CallFor(screen, null)));
    }

    [Fact]
    public void Every_notification_the_captured_session_sent_is_accounted_for_and_a_permission_one_maps_to_waiting()
    {
        var docs = Notifications().ToList();
        Assert.NotEmpty(docs);
        foreach (var d in docs)
            Assert.Equal("Notification", d.RootElement.GetProperty("hook_event_name").GetString());

        var types = docs.Select(d => d.RootElement.GetProperty("notification_type").GetString()!).ToList();
        // The dialog was on screen, so the hook has to have been told about it.
        Assert.True(types.Any(InstallersMapToWaiting),
            "no captured Notification maps to waiting; types were: " + string.Join(",", types));
    }

    [Fact]
    public void The_hook_log_reached_waiting_for_the_same_session()
    {
        // The capture holds the hook's run log, not the status file: capture.sh looked for
        // *.json and the hook writes <session-id>.txt (OrbweaverHook.sh:142).
        var sessions = Notifications().Select(d => d.RootElement.GetProperty("session_id").GetString()!).Distinct().ToList();
        Assert.Single(sessions);
        var log = Read("hook.log").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = log.Last(l => l.Contains(" start ") && l.Contains($"session={sessions[0]}"));
        Assert.Contains("state=waiting", last);
        Assert.Contains("agent=claude", last);
    }

    [Fact]
    public void The_captured_pane_has_unnumbered_options_so_it_is_not_a_numbered_dialog_and_reads_as_a_cancellable_prompt()
    {
        var screen = Read("pane.txt");

        // Real Claude Code draws "❯ No" / "Yes", no digits: the numbered reader cannot see it.
        Assert.Null(ChatTranscript.ParseDialogCore(screen, out _, out _));
        var fp = PromptFingerprint.Of(screen);
        Assert.NotNull(fp);
        Assert.True(fp!.Overlay);
        var only = Assert.Single(fp.Prompt.Options);
        Assert.Equal("Escape", only.Key);
        Assert.Contains(fp.Prompt.Body!, l => l.Contains("Complete Authentication Tool"));
    }

    [Fact]
    public void Control_the_same_pane_without_the_esc_to_cancel_footer_is_no_prompt()
    {
        var screen = Read("pane.txt").Replace("Esc to cancel · Tab to amend", "Tab to amend");
        Assert.Null(PromptFingerprint.Of(screen));
    }

    [Fact]
    public void Control_the_same_pane_without_the_selector_options_is_no_prompt()
    {
        var screen = string.Join('\n', Read("pane.txt").Split('\n').Where(l => !l.Contains("❯ No") && l.Trim() != "Yes"));
        Assert.Null(PromptFingerprint.Of(screen));
    }

    // Negative controls: the readers above are not so loose that anything passes.
    [Fact]
    public void Control_a_pane_with_no_numbered_options_is_not_a_dialog()
    {
        Assert.Null(ChatTranscript.ParseDialog("Authentication needed.\nOpen this link to sign in:\nhttp://127.0.0.1:1/authorize?x=1\n"));
    }

    [Theory]
    [InlineData("idle_prompt")]
    [InlineData("auth_success")]
    public void Control_a_notification_type_that_is_not_a_prompt_does_not_map_to_waiting(string type)
    {
        Assert.False(InstallersMapToWaiting(type));
    }
}
