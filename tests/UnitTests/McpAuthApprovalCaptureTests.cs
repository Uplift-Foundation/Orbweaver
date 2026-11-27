using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-359, the remote-OAuth form: the approval prompt for an authentication tool
// Claude Code offers for a remote MCP server that needs sign-in ("Allow Claude to
// use complete_authentication?") never reached Orbweaver. These tests read a REAL
// capture of it (Fixtures/mcp-auth-approval, see the README there), made from a
// default-mode terminal session whose approval was left pending. They fail until
// that capture is committed. The stdio form is McpToolApprovalCaptureTests.
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

    [Fact]
    public void The_captured_auth_approval_pane_reads_as_a_dialog_with_its_options()
    {
        var screen = Read("pane.txt");

        Assert.Matches(new Regex("authenticat", RegexOptions.IgnoreCase), screen);
        var dialog = ChatTranscript.ParseDialog(screen);

        Assert.NotNull(dialog);
        Assert.True(dialog!.Options.Count >= 2);
        Assert.Equal("1", dialog.Options[0].Key.ToString());
        Assert.NotNull(PromptFingerprint.Of(screen, AskFormTranscript.CallFor(screen, null)));
    }

    [Fact]
    public void The_notification_the_captured_session_sent_is_one_the_hook_installers_map_to_waiting()
    {
        using var doc = JsonDocument.Parse(Read("notification.json"));
        Assert.Equal("Notification", doc.RootElement.GetProperty("hook_event_name").GetString());
        var type = doc.RootElement.GetProperty("notification_type").GetString();
        Assert.False(string.IsNullOrEmpty(type));

        foreach (var installer in new[] { "install-macos-hooks.sh", "install-windows-hooks.ps1" })
        {
            var text = File.ReadAllText(RepoFile("tools", installer));
            Assert.Matches($@"(Notification|event: 'Notification').*{Regex.Escape(type!)}.*waiting", text);
        }
    }

    [Fact]
    public void The_captured_status_file_reached_waiting_for_the_same_session()
    {
        using var status = JsonDocument.Parse(Read("status.json"));
        using var note = JsonDocument.Parse(Read("notification.json"));

        Assert.Equal("waiting", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("cli", status.RootElement.GetProperty("entrypoint").GetString());
        Assert.False(string.IsNullOrEmpty(note.RootElement.GetProperty("session_id").GetString()));
    }
}
