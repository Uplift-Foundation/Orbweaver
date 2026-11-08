using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-313 / CB-315: the terminal x CLI table from docs/screen-read-spike-findings.md,
// in executable form. Every fixture under tests/UnitTests/Fixtures/screens is a real
// capture of a live terminal (the reader that took it is in manifest.json), and this
// runs the shipped parser over each one and pins what it says.
//
// CB-313 recorded what the parser said THEN (Grok `unknown` on every host, long
// labels cut at 80 columns, conhost's idle screen unread). CB-315 taught it:
// Grok reads on all seven hosts (3 options on the Mac, 5 on Windows), a wrapped
// label is joined onto its option, conhost's > is the prompt mark, and the options
// that switch the session's permission mode are flagged. The manifest rows moved
// with it, on purpose; this file is where a later change to any of that fails.
public class ScreenReadFixtureTests
{
    public sealed record Row(
        string Fixture, string Terminal, string Cli, string Kind,
        string Expected, string ExpectedCheck, int ExpectedOptions,
        string? ExpectedTitle, bool LabelsComplete, string[]? ModeKeys);

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static List<Row> AllRows() =>
        JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(Path.Combine(Dir, "manifest.json")), Json)!;

    private static string Screen(string fixture) => File.ReadAllText(Path.Combine(Dir, fixture + ".txt"));

    public static IEnumerable<object[]> Rows() => AllRows().Select(r => new object[] { r });

    // Whitespace collapsed, so a label that wrapped across two lines can be found whole.
    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ");

    // The same, with a line broken at a hyphen ("cb313-" / "probe") put back as the one word it was.
    private static string FlatJoiningHyphens(string s) => Flat(Regex.Replace(s, @"(?<=[^\s]-)[ ]*\n\s*", ""));

    [Theory]
    [MemberData(nameof(Rows))]
    public void Parser_says_what_the_manifest_records(Row row)
    {
        var screen = File.ReadAllText(Path.Combine(Dir, row.Fixture));

        // The fixtures are LF whatever the checkout (.gitattributes); a CR here
        // would be the PR #219 failure again, not a property of the screen.
        Assert.DoesNotContain('\r', screen);

        var (reading, check) = PaneScreen.Read(screen);
        Assert.Equal(row.Expected, reading.ToString().ToLowerInvariant());
        Assert.Equal(row.ExpectedCheck, check);

        var dialog = ChatTranscript.ParseDialog(screen);
        if (row.Expected != "dialog")
        {
            Assert.Null(dialog);
            return;
        }

        Assert.NotNull(dialog);
        Assert.Equal(row.ExpectedOptions, dialog!.Options.Count);
        Assert.Equal(row.ExpectedTitle, dialog.Title);

        // Keys 1..n, and every label is the WHOLE of what is drawn: the screen
        // flattened to single spaces contains the label, so a label the terminal
        // wrapped is joined, not cut at the line break.
        var flat = Flat(screen);
        var flatHyphens = FlatJoiningHyphens(screen);
        for (var i = 0; i < dialog.Options.Count; i++)
        {
            var o = dialog.Options[i];
            Assert.Equal((i + 1).ToString(), o.Key);
            Assert.True(flat.Contains(Flat(o.Label)) || flatHyphens.Contains(Flat(o.Label)), $"option {o.Key} is drawn whole: {o.Label}");
            Assert.False(o.FreeText);
        }

        // The options that switch the permission mode are exactly the ones the
        // manifest names, and only those.
        Assert.Equal(row.ModeKeys ?? Array.Empty<string>(),
            dialog.Options.Where(o => o.Mode).Select(o => o.Key).ToArray());
    }

    // The per-cell negative control: a screen with no prompt on it never parses as a dialog.
    [Fact]
    public void Every_idle_capture_is_not_a_dialog()
    {
        var idle = AllRows().Where(r => r.Kind.StartsWith("none", StringComparison.Ordinal)).ToList();
        Assert.True(idle.Count >= 25, "expected an idle capture per cell");
        foreach (var r in idle)
        {
            var screen = File.ReadAllText(Path.Combine(Dir, r.Fixture));
            Assert.Null(ChatTranscript.ParseDialog(screen));
            Assert.NotEqual(PaneReading.Dialog, PaneScreen.Read(screen).Reading);
        }
    }

    // CB-315 point 1: Grok reads on every host. 3 options on the Mac's five terminals, 5 on Windows,
    // none of them left `unknown` as CB-313 found them.
    [Fact]
    public void Grok_prompt_parses_on_all_seven_hosts()
    {
        var grok = AllRows().Where(r => r.Cli == "grok" && r.Kind == "prompt").ToList();
        Assert.Equal(7, grok.Count);
        foreach (var r in grok)
        {
            var dialog = ChatTranscript.ParseDialog(Screen(Path.GetFileNameWithoutExtension(r.Fixture)));
            Assert.NotNull(dialog);
            Assert.Equal(r.Terminal is "wt" or "conhost" ? 5 : 3, dialog!.Options.Count);
            Assert.Equal(PaneReading.Dialog, PaneScreen.Read(Screen(Path.GetFileNameWithoutExtension(r.Fixture))).Reading);
        }
    }

    [Fact]
    public void Grok_mac_dialog_has_its_three_options_and_the_command_as_its_title()
    {
        var d = ChatTranscript.ParseDialog(Screen("tmux-grok-prompt"))!;
        Assert.Equal("touch $HOME/cb313-probe", d.Title);
        Assert.Equal(new[] { "Yes, and don't ask again for anything (always-approve mode)", "Yes, proceed", "No, reject (type to add feedback)" },
            d.Options.Select(o => o.Label).ToArray());
        Assert.Equal(new[] { "1", "2", "3" }, d.Options.Select(o => o.Key).ToArray());
    }

    [Fact]
    public void Grok_windows_auto_review_dialog_has_its_five_options_under_both_hosts()
    {
        foreach (var f in new[] { "wt-grok-prompt", "conhost-grok-prompt" })
        {
            var d = ChatTranscript.ParseDialog(Screen(f))!;
            Assert.Equal("Remove-Item C:/Users/xxxxx/cb313-does-not-exist -ErrorAction SilentlyContinue", d.Title);
            Assert.Equal(5, d.Options.Count);
            Assert.StartsWith("Always allow: Remove-Item", d.Options[1].Label);
            Assert.Equal("Yes, proceed", d.Options[2].Label);
            Assert.Equal("No, reject (type to add feedback)", d.Options[3].Label);
            Assert.StartsWith("Never allow: Remove-Item", d.Options[4].Label);
        }
    }

    // CB-315 point 2, the one that must never be pressable: Grok's always-approve option, and Claude Code's
    // "switch to auto mode", are mode switches. Both are parsed (the dialog draws them) and flagged.
    [Fact]
    public void Mode_options_are_flagged_and_no_answer_option_is()
    {
        var grok = ChatTranscript.ParseDialog(Screen("tmux-grok-prompt"))!;
        Assert.True(grok.Options[0].Mode, "always-approve mode");
        Assert.False(grok.Options[1].Mode); Assert.False(grok.Options[2].Mode);

        var claude = ChatTranscript.ParseDialog(Screen("tmux-claude-prompt"))!;
        Assert.Equal("Yes, and switch to auto mode · auto mode handles these prompts for you", claude.Options[2].Label);
        Assert.True(claude.Options[2].Mode);
        Assert.False(claude.Options[0].Mode); Assert.False(claude.Options[1].Mode); Assert.False(claude.Options[3].Mode);

        var codex = ChatTranscript.ParseDialog(Screen("tmux-codex-prompt"))!;
        Assert.DoesNotContain(codex.Options, o => o.Mode);

        Assert.True(OptionModes.IsMode("Yes, and don't ask again for anything (always-approve mode)"));
        Assert.True(OptionModes.IsMode("YES, AND SWITCH TO AUTO MODE"));
        Assert.False(OptionModes.IsMode("Yes, and always allow access to /tmp from this project"));
        Assert.False(OptionModes.IsMode("Always allow: Remove-Item C:/x"));
        Assert.False(OptionModes.IsMode("No, reject (type to add feedback)"));
    }

    // CB-315 point 3: wrapped labels are joined. The 80-column iTerm2 and WezTerm captures, and tmux with a
    // long path, were `yes*` in CB-313 (the parser kept only the first line); now each label is whole.
    [Theory]
    [InlineData("iterm2-claude-prompt", 1, "Yes, and always allow access to /private/tmp/c3/iterm-claude from this project")]
    [InlineData("wezterm-claude-prompt", 1, "Yes, and always allow access to /private/tmp/c3/wez-claude from this project")]
    [InlineData("iterm2-codex-prompt", 1, "Yes, and don't ask again for commands that start with `touch $HOME/cb313-probe` (p)")]
    [InlineData("wezterm-codex-prompt", 1, "Yes, and don't ask again for commands that start with `touch $HOME/cb313-probe` (p)")]
    public void Wrapped_labels_are_joined_into_one(string fixture, int index, string expected)
    {
        Assert.Equal(expected, ChatTranscript.ParseDialog(Screen(fixture))!.Options[index].Label);
    }

    [Fact]
    public void A_long_label_in_a_narrow_terminal_is_whole_and_its_neighbours_are_not_swallowed()
    {
        var d = ChatTranscript.ParseDialog(Screen("tmux-claude-prompt-wrapped"))!;
        Assert.Equal(4, d.Options.Count);
        Assert.StartsWith("Yes, and always allow access to /private/tmp/claude-501/", d.Options[1].Label);
        Assert.EndsWith("scratchpad/cb313/tmux from this project", d.Options[1].Label);
        Assert.Equal("Yes, and switch to auto mode · auto mode handles these prompts for you", d.Options[2].Label);
        Assert.Equal("No", d.Options[3].Label);
    }

    [Fact]
    public void A_dialog_that_does_not_wrap_is_left_exactly_as_it_was()
    {
        foreach (var f in new[] { "tmux-claude-prompt", "kitty-claude-prompt", "iterm2-claude-prompt-120", "wezterm-codex-prompt-120" })
        {
            var d = ChatTranscript.ParseDialog(Screen(f))!;
            Assert.All(d.Options, o => Assert.DoesNotContain("\n", o.Label));
        }
        Assert.Equal("Yes, and always allow access to /private/tmp/c3/iterm120-claude from this project",
            ChatTranscript.ParseDialog(Screen("iterm2-claude-prompt-120"))!.Options[1].Label);
    }

    // CB-315 point 4: conhost draws the idle prompt mark as > (with a no-break space after it), so its idle
    // screen read Unknown/no-prompt-mark where every other terminal said Prompt.
    [Fact]
    public void Conhost_idle_prompt_mark_is_recognised()
    {
        Assert.Equal((PaneReading.Prompt, "prompt"), PaneScreen.Read(Screen("conhost-claude-none")));
        // ... and the other terminals' idle prompt still is
        Assert.Equal((PaneReading.Prompt, "prompt"), PaneScreen.Read(Screen("tmux-claude-none")));
    }

    [Fact]
    public void A_greater_than_sign_that_is_not_in_the_input_box_is_not_a_prompt()
    {
        // a quote line in prose, with no box around it
        var quote = "Here is what the file says:\n> Try this instead\n\nAnd that is all.\n";
        Assert.Equal(PaneReading.Unknown, PaneScreen.Read(quote).Reading);
        // a shell prompt line alone
        Assert.Equal(PaneReading.Unknown, PaneScreen.Read("user@host ~ % ls\n> \n").Reading);
    }

    // CB-315 point 5: the first-run dialogs are not permission prompts. Recognised by their wording, on every
    // CLI, whether or not they happen to parse as a numbered dialog (the Codex ones do).
    [Fact]
    public void First_run_dialogs_are_recognised_and_permission_prompts_are_not()
    {
        var firstRun = AllRows().Where(r => r.Kind is "trustdialog" or "importdialog" or "hooksdialog" or "updatedialog").ToList();
        Assert.True(firstRun.Count >= 7, "every first-run capture from CB-313");
        foreach (var r in firstRun)
            Assert.True(FirstRunDialogs.Is(File.ReadAllText(Path.Combine(Dir, r.Fixture))), r.Fixture);

        foreach (var r in AllRows().Where(r => r.Kind.StartsWith("prompt", StringComparison.Ordinal)
                                               || r.Kind.StartsWith("none", StringComparison.Ordinal) || r.Kind is "ranwithoutprompt" or "signin"))
            Assert.False(FirstRunDialogs.Is(File.ReadAllText(Path.Combine(Dir, r.Fixture))), r.Fixture);

        Assert.False(FirstRunDialogs.Is(null)); Assert.False(FirstRunDialogs.Is("   "));
    }

    // The first-run dialogs that parse as numbered dialogs today still do; FirstRunDialogs is what tells
    // them from a permission prompt (they are not parsed away, the panel keeps working as it did).
    [Fact]
    public void Some_first_run_dialogs_parse_as_numbered_dialogs_which_is_why_they_need_a_name()
    {
        Assert.NotNull(ChatTranscript.ParseDialog(Screen("wt-codex-trustdialog")));
        Assert.NotNull(ChatTranscript.ParseDialog(Screen("wt-codex-hooksdialog")));
        Assert.NotNull(ChatTranscript.ParseDialog(Screen("conhost-codex-updatedialog")));
        Assert.Null(ChatTranscript.ParseDialog(Screen("wt-claude-importdialog")));
        Assert.Null(ChatTranscript.ParseDialog(Screen("tmux-grok-trustdialog")));
    }
}
