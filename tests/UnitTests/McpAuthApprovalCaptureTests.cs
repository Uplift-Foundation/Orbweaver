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
    public void The_captured_pane_is_the_complete_authentication_approval_and_reads_as_a_dialog_with_its_options()
    {
        var screen = Read("pane.txt");

        Assert.Matches(new Regex("complete_authentication"), screen);
        var dialog = ChatTranscript.ParseDialog(screen);

        Assert.NotNull(dialog);
        Assert.True(dialog!.Options.Count >= 2);
        Assert.Equal("1", dialog.Options[0].Key.ToString());
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
    public void The_captured_status_file_reached_waiting_for_the_same_session()
    {
        using var status = JsonDocument.Parse(Read("status.json"));

        Assert.Equal("waiting", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("cli", status.RootElement.GetProperty("entrypoint").GetString());
        Assert.Contains("waiting", Read("hook.log"));
        Assert.All(Notifications(), d => Assert.False(string.IsNullOrEmpty(d.RootElement.GetProperty("session_id").GetString())));
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
