using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397: TrustPrompt over real captures. The two Claude screens are CB-327's
/// (Claude Code 2.1.325, tmux capture-pane -p) at 100 and 32 columns; the rest
/// are CB-313's, so "answerable" is pinned against the dialogs that must stay
/// unanswered or unseen.
/// </summary>
public class TrustPromptTests
{
    private const string Folder = "/private/tmp/cb327-scratch/proj";

    private static string Trust(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrustFixtures", name + ".txt"));

    private static string Screen(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures", name + ".txt"));

    private static TrustPrompt.Reading Read(string? screen, string cwd = Folder, NewChatCli cli = NewChatCli.ClaudeCode) =>
        TrustPrompt.Read(screen, cwd, cli);

    [Theory]
    [InlineData("tmux-claude-trustdialog")]
    [InlineData("tmux-claude-trustdialog-w32")]
    public void The_real_claude_prompt_is_answerable_with_the_marker_on_no_at_any_width(string fixture)
    {
        Assert.Equal(new TrustPrompt.Reading(TrustPrompt.Kind.Answerable, TrustPrompt.Option.No), Read(Trust(fixture)));
    }

    // The same screen after one Down: only the marker moved.
    [Fact]
    public void A_marker_on_yes_is_read_as_yes()
    {
        var onYes = Trust("tmux-claude-trustdialog").Replace("❯ No, exit\n   Yes, I trust this folder", "  No, exit\n ❯ Yes, I trust this folder");
        Assert.NotEqual(Trust("tmux-claude-trustdialog"), onYes);
        Assert.Equal(new TrustPrompt.Reading(TrustPrompt.Kind.Answerable, TrustPrompt.Option.Yes), Read(onYes));
    }

    [Fact]
    public void Another_folder_does_not_bind_and_is_never_answered()
    {
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(Trust("tmux-claude-trustdialog"), "/private/tmp/cb327-scratch/other").Kind);
        // A symlinked path is printed resolved: "/tmp/x" is inside "/private/tmp/x" and must still not bind.
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(Trust("tmux-claude-trustdialog"), "/tmp/cb327-scratch/proj").Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(Trust("tmux-claude-trustdialog"), "").Kind);
        // A trailing slash on the launched folder is the same folder.
        Assert.Equal(TrustPrompt.Kind.Answerable, Read(Trust("tmux-claude-trustdialog"), Folder + "/").Kind);
    }

    [Fact]
    public void Only_claude_code_is_answered()
    {
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(Trust("tmux-claude-trustdialog"), cli: NewChatCli.Codex).Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(Trust("tmux-claude-trustdialog"), cli: NewChatCli.Grok).Kind);
    }

    [Theory]
    [InlineData("tmux-grok-trustdialog")]
    [InlineData("wt-codex-trustdialog")]
    public void Grok_and_codex_trust_dialogs_are_reported_never_answered(string fixture)
    {
        Assert.Equal(new TrustPrompt.Reading(TrustPrompt.Kind.Unanswerable), Read(Screen(fixture), cli: NewChatCli.Grok));
    }

    [Theory]
    [InlineData("tmux-claude-importdialog")]
    [InlineData("conhost-claude-none")]
    [InlineData("conhost-claude-prompt")]
    [InlineData("conhost-codex-updatedialog")]
    public void Other_screens_read_nothing(string fixture)
    {
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(Screen(fixture)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void An_unreadable_pane_reads_nothing(string? screen)
    {
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(screen));
    }

    // The wording, then anything but its own footer, is a screen that moved on.
    [Fact]
    public void The_wording_followed_by_anything_but_the_footer_reads_nothing()
    {
        var shell = Trust("tmux-claude-trustdialog").TrimEnd() + "\n\n$ ls\n";
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(shell));

        var noQuestion = Trust("tmux-claude-trustdialog").Replace("Is this a project you created or one you trust?", "Is this fine?");
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(noQuestion));
    }

    [Fact]
    public void Option_lines_must_be_exactly_two_with_exactly_one_marker()
    {
        var both = Trust("tmux-claude-trustdialog").Replace("   Yes, I trust this folder", " ❯ Yes, I trust this folder");
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(both));

        var neither = Trust("tmux-claude-trustdialog").Replace("❯ No, exit", "  No, exit");
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(neither));

        var missing = Trust("tmux-claude-trustdialog").Replace("   Yes, I trust this folder\n", "");
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(missing));

        var doubled = Trust("tmux-claude-trustdialog").Replace(" Security guide", " ❯ No, exit\n Security guide");
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(doubled));
    }

    [Fact]
    public void A_screen_with_the_heading_but_no_folder_line_does_not_bind()
    {
        var noQuick = Trust("tmux-claude-trustdialog").Replace("Quick safety check:", "Checking:");
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(noQuick).Kind);
    }

    // CB-397: the phone rule ("first-run dialogs are desk-only") names this wording.
    [Theory]
    [InlineData("tmux-claude-trustdialog")]
    [InlineData("tmux-claude-trustdialog-w32")]
    public void The_claude_trust_screen_is_a_first_run_dialog(string fixture)
    {
        Assert.True(FirstRunDialogs.Is(Trust(fixture)));
    }
}
