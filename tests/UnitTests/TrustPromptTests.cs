using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-397: TrustPrompt over real captures (Claude Code 2.1.330, the mini's
/// tmux): plain and hooks-warning variants, marker on No and on Yes, at 100, 80
/// and 32 columns, plus controls where Claude showed its input box and asked
/// nothing. The other CLIs' dialogs are CB-313's.
/// </summary>
public class TrustPromptTests
{
    private static string Screens(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures", name + ".txt"));

    private static TrustPrompt.Reading Read(string? screen, string cwd = TrustScreens.Folder, NewChatCli cli = NewChatCli.ClaudeCode, string? resolved = null) =>
        TrustPrompt.Read(screen, cwd, cli, resolved);

    public static TheoryData<string, string, string, bool> Real() => new()
    {
        { "plain-w100-no", TrustScreens.Folder, "No", false },
        { "plain-w100-yes", TrustScreens.Folder, "Yes", false },
        { "plain-w80-no", TrustScreens.Folder, "No", false },
        { "plain-w80-yes", TrustScreens.Folder, "Yes", false },
        { "plain-w32-no", TrustScreens.Folder, "No", false },
        { "hooks-w100-no", TrustScreens.HooksFolder, "No", true },
        { "hooks-w100-yes", TrustScreens.HooksFolder, "Yes", true },
        { "hooks-w80-no", TrustScreens.HooksFolder, "No", true },
        { "hooks-w80-yes", TrustScreens.HooksFolder, "Yes", true },
        { "hooks-w32-no", TrustScreens.HooksFolder, "No", true },
    };

    [Theory]
    [MemberData(nameof(Real))]
    public void The_real_prompt_is_answerable_with_its_marker_and_whether_it_warns_of_hooks(string fixture, string folder, string marker, bool hooks)
    {
        Assert.Equal(new TrustPrompt.Reading(TrustPrompt.Kind.Answerable, Enum.Parse<TrustPrompt.Option>(marker), hooks), Read(TrustScreens.Read(fixture), folder));
    }

    // Claude showed its input box and asked nothing: a fresh folder that is not a git repository.
    [Theory]
    [InlineData("nongit-file-w80-no")]
    [InlineData("nongit-hooks-w80-no")]
    public void A_folder_that_did_not_ask_reads_nothing(string fixture)
    {
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(TrustScreens.Read(fixture), "/private/tmp/cb397-w/nongit-file"));
    }

    // CB-397 addendum 2: asked as /tmp/x, printed as /private/tmp/x.
    [Fact]
    public void A_folder_asked_through_a_link_binds_on_its_resolved_spelling_and_on_neither_alone()
    {
        var screen = TrustScreens.Read("tmpfolder-w100-no");
        const string asked = "/tmp/cb397-lnk";
        const string physical = "/private/tmp/cb397-lnk";

        Assert.Equal(TrustPrompt.Kind.Answerable, Read(screen, asked, resolved: physical).Kind);
        Assert.Equal(TrustPrompt.Kind.Answerable, Read(screen, physical, resolved: asked).Kind);
        Assert.Equal(TrustPrompt.Kind.Answerable, Read(screen, physical).Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(screen, asked).Kind);
        // A third folder is refused whichever spellings are offered.
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(screen, "/tmp/other", resolved: "/private/tmp/other").Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(screen, asked, resolved: "").Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(screen, asked, resolved: null).Kind);
    }

    [Fact]
    public void Another_folder_does_not_bind_and_is_never_answered()
    {
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(TrustScreens.OnNo, "/private/tmp/cb397-w/other").Kind);
        // "/tmp/..." is inside "/private/tmp/...": containment must not bind.
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(TrustScreens.OnNo, "/tmp/cb397-w/git-plain").Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(TrustScreens.OnNo, "").Kind);
        Assert.Equal(TrustPrompt.Kind.Answerable, Read(TrustScreens.OnNo, TrustScreens.Folder + "/").Kind);
    }

    [Fact]
    public void Only_claude_code_is_answered()
    {
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(TrustScreens.OnNo, cli: NewChatCli.Codex).Kind);
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(TrustScreens.OnNo, cli: NewChatCli.Grok).Kind);
    }

    [Theory]
    [InlineData("tmux-grok-trustdialog")]
    [InlineData("wt-codex-trustdialog")]
    public void Grok_and_codex_trust_dialogs_are_reported_never_answered(string fixture)
    {
        Assert.Equal(new TrustPrompt.Reading(TrustPrompt.Kind.Unanswerable), Read(Screens(fixture), cli: NewChatCli.Grok));
    }

    [Theory]
    [InlineData("tmux-claude-importdialog")]
    [InlineData("conhost-claude-none")]
    [InlineData("conhost-claude-prompt")]
    [InlineData("conhost-codex-updatedialog")]
    public void Other_screens_read_nothing(string fixture)
    {
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(Screens(fixture)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void An_unreadable_pane_reads_nothing(string? screen)
    {
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(screen));
    }

    [Fact]
    public void The_wording_followed_by_anything_but_the_footer_reads_nothing()
    {
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(TrustScreens.OnNo.TrimEnd() + "\n\n$ ls\n"));
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(TrustScreens.OnNo.Replace("Is this a project you created or one you trust?", "Is this fine?")));
    }

    [Fact]
    public void Option_lines_must_be_exactly_two_with_exactly_one_marker()
    {
        var both = TrustScreens.OnNo.Replace("   Yes, I trust this folder", " ❯ Yes, I trust this folder");
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(both));
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(TrustScreens.OnNo.Replace("❯ No, exit", "  No, exit")));
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(TrustScreens.OnNo.Replace("   Yes, I trust this folder\n", "")));
        Assert.Equal(TrustPrompt.Reading.Nothing, Read(TrustScreens.OnNo.Replace(" Security guide", " ❯ No, exit\n Security guide")));
    }

    [Fact]
    public void A_screen_with_the_heading_but_no_folder_line_does_not_bind()
    {
        Assert.Equal(TrustPrompt.Kind.Unanswerable, Read(TrustScreens.OnNo.Replace("Quick safety check:", "Checking:")).Kind);
    }

    [Theory]
    [InlineData("plain-w100-no")]
    [InlineData("plain-w32-no")]
    [InlineData("hooks-w100-no")]
    [InlineData("hooks-w32-no")]
    public void The_claude_trust_screen_is_a_first_run_dialog(string fixture)
    {
        Assert.True(FirstRunDialogs.Is(TrustScreens.Read(fixture)));
    }
}
