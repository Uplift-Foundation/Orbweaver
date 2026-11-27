using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-359: an MCP tool approval ("Allow Claude to use <tool> (<server>)?" with a
// "What Claude sent" payload, Deny / Allow once) never reached the phone or the
// panel: no amber orb, no prompt card. CB-315 requires every session waiting on
// an approval to go amber and show a card, or "Open on desk".
//
// The first two tests read a REAL capture (Fixtures/mcp-approval, see README
// there): a default-mode session stopped on "Do you want to proceed?" (Yes /
// Yes, and don't ask again / No). The negative control needs no capture.
public class McpToolApprovalCaptureTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "McpApprovalFixtures");

    private static string Read(string name)
    {
        var path = Path.Combine(Dir, name);
        Assert.True(File.Exists(path), $"CB-359 capture missing: {name} (see Fixtures/mcp-approval/README.md)");
        return File.ReadAllText(path);
    }

    private static string RepoFile(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Orbweaver.sln")) &&
               !Directory.Exists(Path.Combine(d.FullName, "tools"))) d = d.Parent;
        return Path.Combine(new[] { d!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void The_captured_mcp_approval_pane_reads_as_a_dialog_the_pipeline_would_mint()
    {
        var screen = Read("pane.txt");

        var dialog = ChatTranscript.ParseDialog(screen);

        Assert.NotNull(dialog);
        Assert.Equal(new[] { "1", "2", "3" }, dialog!.Options.Select(o => o.Key.ToString()));
        Assert.Equal("Yes", dialog.Options[0].Label);
        Assert.StartsWith("Yes, and don't ask again", dialog.Options[1].Label);
        Assert.Equal("No", dialog.Options[2].Label);
        Assert.NotNull(PromptFingerprint.Of(screen, AskFormTranscript.CallFor(screen, null)));
    }

    [Fact]
    public void The_notification_the_captured_session_sent_is_one_the_hook_installers_map_to_waiting()
    {
        using var doc = JsonDocument.Parse(Read("notification.json"));
        var type = doc.RootElement.GetProperty("notification_type").GetString();
        Assert.False(string.IsNullOrEmpty(type));

        foreach (var installer in new[] { "install-macos-hooks.sh", "install-windows-hooks.ps1" })
        {
            var text = File.ReadAllText(RepoFile("tools", installer));
            Assert.Matches($@"(Notification|event: 'Notification').*{System.Text.RegularExpressions.Regex.Escape(type!)}.*waiting", text);
        }
    }

    // Negative control: a tool-use line with no approval is not a prompt, so it
    // must not go amber whatever the fix does to widen what counts as one.
    [Fact]
    public void An_ordinary_mcp_tool_use_line_with_no_approval_is_not_a_prompt()
    {
        const string screen =
            "● atlassian-uplift - getJiraIssue (MCP)(issueIdOrKey: \"CB-359\")\n" +
            "  ⎿  {\"key\":\"CB-359\",\"fields\":{\"summary\":\"show an mcp tool approval\"}}\n" +
            "\n" +
            "✻ Thinking… (3s · esc to interrupt)\n" +
            "\n" +
            "> \n";

        Assert.Null(ChatTranscript.ParseDialog(screen));
        Assert.Null(PromptFingerprint.Of(screen, AskFormTranscript.CallFor(screen, null)));
    }
}
