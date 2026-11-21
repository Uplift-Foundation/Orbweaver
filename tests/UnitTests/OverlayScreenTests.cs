using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-400: the pure reader for a screen only Esc closes, run on real
/// `tmux capture-pane` output of `/mobile` (PaneFixtures.MobileOverlay*) and on
/// the negatives that would make it fire on something else.
/// </summary>
public class OverlayScreenTests
{
    private static string Qr(Overlay o) => string.Join("\n", o.Body.Where(r => r.Any(c => c is '█' or '▀' or '▄')));

    [Fact]
    public void The_primary_capture_reads_as_title_caption_and_the_whole_screen_as_body()
    {
        var o = OverlayScreen.Read(PaneFixtures.MobileOverlay80x50)!;

        Assert.Equal("Get the Claude app on your phone", o.Title);
        Assert.Equal(
            ["Get the Claude app on your phone", "Scan the QR code, or visit claude.ai/mobile", "Find this session in the Code tab of the Claude app"],
            o.Caption);
        Assert.Equal(15, o.Body.Count(r => r.Any(c => c is '█' or '▀' or '▄')));
        Assert.DoesNotContain(o.Body, r => r.Contains("Esc to close"));
        // The common indent (the title's two columns) is stripped; the QR keeps its own two more.
        Assert.Equal("Get the Claude app on your phone", o.Body[0]);
        Assert.StartsWith("  █▀▀▀▀▀█", o.Body.First(r => r.Contains('█')));
        Assert.True(o.Body.Count <= 60);
    }

    [Fact]
    public void The_narrow_capture_keeps_the_qr_intact_and_wraps_the_caption()
    {
        var o = OverlayScreen.Read(PaneFixtures.MobileOverlay40x50)!;
        Assert.Equal(["Get the Claude app on your phone", "Scan the QR code, or visit", "claude.ai/mobile",
            "Find this session in the Code tab of", "the Claude app"], o.Caption);
        Assert.Equal(Qr(OverlayScreen.Read(PaneFixtures.MobileOverlay80x50)!), Qr(o));
    }

    [Fact]
    public void A_pane_too_narrow_for_the_qr_is_still_an_overlay()
    {
        // The title wraps at 28 columns, so the title is its first row only.
        var o = OverlayScreen.Read(PaneFixtures.MobileOverlay28x50)!;
        Assert.Equal("Get the Claude app on", o.Title);
    }

    [Fact]
    public void A_clipped_short_pane_is_still_an_overlay()
    {
        var o = OverlayScreen.Read(PaneFixtures.MobileOverlay80x14)!;
        Assert.Equal("Get the Claude app on your phone", o.Title);
        Assert.EndsWith("↓", o.Body[^1]);
    }

    [Fact]
    public void Typing_into_the_overlay_changes_nothing()
    {
        Assert.Equal(PaneFixtures.MobileOverlay80x50, PaneFixtures.MobileOverlayAfterTyping);
        Assert.NotNull(OverlayScreen.Read(PaneFixtures.MobileOverlayAfterTyping));
    }

    [Fact]
    public void The_screen_after_esc_is_not_an_overlay()
    {
        Assert.Null(OverlayScreen.Read(PaneFixtures.MobileAfterEsc));
        Assert.Null(OverlayScreen.Read(""));
        Assert.Null(OverlayScreen.Read(null));
    }

    // Prose that quotes the footer, with the real input box below it: a live
    // overlay has no box under it.
    [Fact]
    public void The_footer_quoted_in_prose_with_the_input_box_below_is_not_an_overlay()
    {
        var quoted = PaneFixtures.MobileOverlay80x50 + "\n" + PaneFixtures.MobileAfterEsc.Split('\n').Skip(4).Aggregate((a, b) => a + "\n" + b);
        Assert.Null(OverlayScreen.Read(quoted));
    }

    [Fact]
    public void A_footer_with_no_rule_above_it_is_not_an_overlay()
    {
        var noEdge = string.Join("\n", PaneFixtures.MobileOverlay80x50.Split('\n').Where(l => !l.StartsWith('▔')));
        Assert.Null(OverlayScreen.Read(noEdge));
    }

    // The edge is any one repeated symbol, not the glyph the Mac happened to draw.
    [Fact]
    public void A_footer_among_other_hints_is_found_by_its_segment()
    {
        Assert.NotNull(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  A title\n  r to retry · Esc to close\n"));
        Assert.Null(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  A title\n  Esc to close the door\n"));
    }

    [Fact]
    public void An_existing_Esc_to_cancel_dialog_is_not_an_overlay_and_still_parses_as_before()
    {
        Assert.Null(OverlayScreen.Read(PaneFixtures.MiniExitDialog));
        var dialog = ChatTranscript.ParseDialog(PaneFixtures.MiniExitDialog)!;
        Assert.Null(dialog.Body);
        Assert.True(dialog.Options.Count >= 2);
    }

    [Fact]
    public void A_numbered_dialog_that_shows_the_footer_is_the_dialog()
    {
        var both = PaneFixtures.MacModelSwitchDialog + "\n  Esc to close\n";
        Assert.Null(OverlayScreen.Read(both));
        var parsed = ChatTranscript.ParseDialog(both)!;
        Assert.Null(parsed.Body);
        Assert.True(parsed.Options.Count >= 2);
    }

    [Fact]
    public void ParseDialog_reads_the_overlay_as_one_Close_option_with_its_body()
    {
        var p = ChatTranscript.ParseDialog(PaneFixtures.MobileOverlay80x50)!;
        Assert.Equal("Get the Claude app on your phone", p.Title);
        var only = Assert.Single(p.Options);
        Assert.Equal((OverlayScreen.CloseKey, OverlayScreen.CloseLabel), (only.Key, only.Label));
        Assert.Equal(OverlayScreen.Read(PaneFixtures.MobileOverlay80x50)!.Body, p.Body);
        Assert.Null(ChatTranscript.ParseDialog(PaneFixtures.MobileAfterEsc));
    }

    [Fact]
    public void A_screen_whose_only_content_is_the_edge_and_footer_has_no_overlay()
    {
        Assert.Null(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  Esc to close\n"));
    }

    [Fact]
    public void A_body_with_no_letters_takes_its_first_row_as_the_title_and_has_no_caption()
    {
        var o = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  ██▀▀\n  Esc to close\n")!;
        Assert.Equal("██▀▀", o.Title);
        Assert.Empty(o.Caption);
    }

    [Fact]
    public void PaneScreen_names_an_overlay_by_its_own_word_and_the_prompt_after_esc_as_a_prompt()
    {
        Assert.Equal((PaneReading.Dialog, "overlay"), PaneScreen.Read(PaneFixtures.MobileOverlay80x50));
        Assert.Equal((PaneReading.Dialog, "dialog"), PaneScreen.Read(PaneFixtures.MacModelSwitchDialog));
        Assert.Equal(PaneReading.Prompt, PaneScreen.Read(PaneFixtures.MobileAfterEsc).Reading);
    }

    [Fact]
    public void The_Windows_console_capture_reads_the_same_and_its_control_does_not()
    {
        var o = OverlayScreen.Read(PaneFixtures.WinMobileOverlay)!;
        Assert.Equal("Get the Claude app on your phone", o.Title);
        Assert.Equal(3, o.Caption.Count);
        Assert.Null(OverlayScreen.Read(PaneFixtures.WinAfterEsc));
    }

    // The top edge is a run of ▔ (measured on the Mac and the Windows console), and
    // the nearest one above the footer is it, however far up: /help puts it 47 rows up.
    [Fact]
    public void The_nearest_top_edge_is_used_however_far_above_the_footer()
    {
        var far = "▔▔▔▔▔▔▔▔▔▔▔▔\n  Title\n" + string.Join("\n", Enumerable.Repeat("  some prose here", 70)) + "\n  Esc to close\n";
        var o = OverlayScreen.Read(far)!;
        Assert.Equal("Title", o.Title);
        Assert.Equal(61, o.Body.Count);                    // 60 rows and the cut marker
        var two = "▔▔▔▔▔▔▔▔▔▔▔▔\n  Old\n▔▔▔▔▔▔▔▔▔▔▔▔\n  New\n  Esc to close\n";
        Assert.Equal("New", OverlayScreen.Read(two)!.Title);
    }

    [Theory]
    [InlineData("────────────")]
    [InlineData("============")]
    [InlineData("ABABABABABAB")]
    [InlineData("▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔ x")]
    [InlineData("▔▔▔▔▔▔▔▔▔")]
    public void Only_a_run_of_at_least_ten_upper_one_eighth_blocks_is_an_edge(string notRule) =>
        Assert.Null(OverlayScreen.Read($"{notRule}\n  A title\n  Esc to close\n"));

    [Fact]
    public void A_wide_body_is_bounded()
    {
        var rows = string.Join("\n", Enumerable.Range(0, 3).Select(i => "  r" + new string('x', 250)));
        Assert.All(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  T\n" + rows + "\n  Esc to close\n")!.Body, r => Assert.True(r.Length <= 200));
    }

    // Every other Claude Code screen QA captured that draws `Esc to close` reads as an
    // overlay with its own title; /resume wraps its footer over two lines and still
    // matches because `Esc to close` is the last segment of the last line.
    // The older /permissions tab captures (`▔` edge, footer `Esc to cancel`) are panes
    // Esc leaves, so they read as Cancel; the Recently-blocked tab with blocked items ends
    // `Esc to approve 1 blocked`, which approves rather than leaves, so it reads null.
    [Theory]
    [InlineData(nameof(PaneFixtures.PermissionsAllow))]
    [InlineData(nameof(PaneFixtures.PermissionsRecentlyBlockedEmpty))]
    public void An_older_permissions_tab_is_a_pane_labelled_Cancel(string name)
    {
        var o = OverlayScreen.Read(Fixture(name))!;
        Assert.Equal(("Permissions", "Cancel"), (o.Title, o.Label));
    }

    [Fact]
    public void The_recently_blocked_tab_that_approves_is_not_a_pane() =>
        Assert.Null(OverlayScreen.Read(PaneFixtures.MiniRecentlyBlockedPopulatedStripped));

    [Fact]
    public void Two_looks_at_one_overlay_with_different_content_hash_the_same()
    {
        var a = PromptFingerprint.Of(PaneFixtures.OtherResumeScreen)!;
        var b = PromptFingerprint.Of(PaneFixtures.OtherResumeScreen.Replace("minute", "hour"))!;
        Assert.NotEqual(a.Preview, b.Preview);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(PromptFingerprint.CanonicalText([OverlayScreen.Read(PaneFixtures.OtherResumeScreen)!.Title], a.Prompt.Options), a.Canonical);
    }

    [Fact]
    public void A_phones_preview_is_the_caption_cut_at_twelve_lines_with_an_ellipsis()
    {
        var short3 = PromptPipeline.OverlayPreview(["a", "b"]);
        Assert.Equal("a\nb", short3);
        var twelve = Enumerable.Range(0, 12).Select(i => "l" + i).ToList();
        Assert.Equal(string.Join("\n", twelve), PromptPipeline.OverlayPreview(twelve));
        var cut = PromptPipeline.OverlayPreview(twelve.Append("l12").ToList()).Split('\n');
        Assert.Equal(13, cut.Length);
        Assert.Equal(("l11", "…"), (cut[11], cut[12]));
    }

    private static string Fixture(string name) =>
        (string)typeof(PaneFixtures).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    // ---- addendum 3: every pane Esc leaves, labelled by its footer's verb -------

    // The architect's table of what each real capture yields: the button's label, the
    // title, and how many body and caption rows. Same-length scrubbing of personal
    // values leaves every count as captured.
    [Theory]
    [InlineData(nameof(PaneFixtures.MobileOverlay80x50), "Close", "Get the Claude app on your phone", 22, 3)]
    [InlineData(nameof(PaneFixtures.WinMobileOverlay), "Close", "Get the Claude app on your phone", 22, 3)]
    [InlineData(nameof(PaneFixtures.MobileOverlay40x50), "Close", "Get the Claude app on your phone", 24, 5)]
    [InlineData(nameof(PaneFixtures.MobileOverlay28x50), "Close", "Get the Claude app on", 43, 7)]
    [InlineData(nameof(PaneFixtures.MobileOverlay80x14), "Close", "Get the Claude app on your phone", 10, 2)]
    [InlineData(nameof(PaneFixtures.McpList), "Close", "MCP servers", 45, 42)]
    [InlineData(nameof(PaneFixtures.McpAfterEsc1), "Close", "MCP servers", 45, 42)]
    [InlineData(nameof(PaneFixtures.McpDetail), "Go back", null, 10, 8)]
    [InlineData(nameof(PaneFixtures.OtherHelpScreen), "Close", "Help", 19, 14)]
    [InlineData(nameof(PaneFixtures.OtherStatusScreen), "Close", "Settings", 18, 16)]
    [InlineData(nameof(PaneFixtures.OtherUsageScreen), "Close", "Settings", 35, 26)]
    [InlineData(nameof(PaneFixtures.OtherStatsScreen), "Close", "Settings", 26, 14)]
    [InlineData(nameof(PaneFixtures.OtherConfigScreen), "Go back", "Settings", 45, 42)]
    [InlineData(nameof(PaneFixtures.ConfigAfterEsc1), "Close", "Settings", 45, 42)]
    [InlineData(nameof(PaneFixtures.OtherReleaseNotesScreen), "Close", "Release notes", 45, 44)]
    [InlineData(nameof(PaneFixtures.OtherPermissionsScreen), "Close", "Permissions", 17, 13)]
    [InlineData(nameof(PaneFixtures.OtherHooksScreen), "Close", "Hooks", 45, 35)]
    [InlineData(nameof(PaneFixtures.OtherPluginScreen), "Close", "Plugins", 44, 38)]
    [InlineData(nameof(PaneFixtures.OtherMemoryScreen), "Close", "Memory", 9, 6)]
    [InlineData(nameof(PaneFixtures.OtherResumeScreen), "Close", "Resume session", 8, 5)]
    [InlineData(nameof(PaneFixtures.OtherIdeScreen), "Close", "Connect to an IDE", 4, 3)]
    [InlineData(nameof(PaneFixtures.OtherExportScreen), "Cancel", "Export conversation", 4, 3)]
    [InlineData(nameof(PaneFixtures.OtherRewindScreen), "Cancel", "Rewind", 11, 7)]
    [InlineData(nameof(PaneFixtures.OtherThemeScreen), "Cancel", "Theme", 20, 15)]
    public void Every_pane_Esc_leaves_reads_with_the_label_of_its_footer(string name, string label, string? title, int body, int caption)
    {
        var o = OverlayScreen.Read(Fixture(name))!;
        Assert.Equal(label, o.Label);
        if (title is not null) Assert.Equal(title, o.Title);
        Assert.Equal((body, caption), (o.Body.Count, o.Caption.Count));
        Assert.Equal(label, Assert.Single(ChatTranscript.ParseDialog(Fixture(name))!.Options).Label);
        Assert.Equal("overlay", PaneScreen.Read(Fixture(name)).Check);
    }

    [Theory]
    [InlineData(nameof(PaneFixtures.MobileAfterEsc))]
    [InlineData(nameof(PaneFixtures.WinAfterEsc))]
    [InlineData(nameof(PaneFixtures.McpAfterEsc2))]
    public void The_idle_prompt_after_leaving_is_not_a_pane(string name) => Assert.Null(OverlayScreen.Read(Fixture(name)));

    // /model is numbered, so ParseDialogCore reads it first and answers by digit.
    [Fact]
    public void A_numbered_model_picker_is_the_dialog_not_an_overlay() =>
        Assert.Null(OverlayScreen.Read(PaneFixtures.OtherModelScreen));

    // Esc on a permission prompt denies it and Esc on a question form abandons it, so
    // these must never read as a pane: none has the `▔` edge.
    [Theory]
    [InlineData(nameof(PaneFixtures.RealPermissionPrompt2330))]
    [InlineData(nameof(PaneFixtures.RealQuestionForm2330))]
    [InlineData(nameof(PaneFixtures.MiniAskForm))]
    [InlineData(nameof(PaneFixtures.MacModelSwitchDialog))]
    [InlineData(nameof(PaneFixtures.MiniExitDialog))]
    public void An_answerable_dialog_or_form_is_never_a_pane(string name) => Assert.Null(OverlayScreen.Read(Fixture(name)));

    [Fact]
    public void A_pane_whose_footer_names_another_verb_is_not_a_pane()
    {
        foreach (var verb in new[] { "Esc to approve 1 blocked", "Esc to clear", "esc to interrupt", "Esc to" })
            Assert.Null(OverlayScreen.Read($"▔▔▔▔▔▔▔▔▔▔▔▔\n  Recently blocked\n  Enter to approve · r to retry · {verb}\n"));
        Assert.Equal("Cancel", OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  A title\n  ↑/↓ · Esc to cancel\n")!.Label);
    }

    // The title is cut at the first run of two spaces, so a tab bar gives its first tab.
    [Fact]
    public void A_tab_bar_title_is_cut_at_its_first_gap_and_the_caption_collapses_gaps()
    {
        var o = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  Settings   Status    Config\n  a     b\n  Esc to close\n")!;
        Assert.Equal("Settings", o.Title);
        Assert.Equal(["Settings Status Config", "a b"], o.Caption);
    }

    // A footer that wrapped over two lines: the wrapped first half (it holds a
    // separator) is not part of the body, and the last segment of the last line decides.
    [Fact]
    public void A_wrapped_footer_is_not_body()
    {
        var o = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  A title\n  content row\n\n  ←/→ to switch · d to show day ·\n  w to show week · Esc to close\n")!;
        Assert.Equal(["A title", "content row"], o.Body.Where(r => r.Length > 0));
    }

    [Fact]
    public void A_body_cut_at_either_bound_ends_with_the_marker_and_a_whole_one_does_not()
    {
        var tall = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  T\n" + string.Join("\n", Enumerable.Range(0, 80).Select(i => "  line " + i)) + "\n  Esc to close\n")!;
        Assert.Equal(61, tall.Body.Count);
        Assert.Equal(OverlayScreen.CutMarker, tall.Body[^1]);
        var wide = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  T\n  r" + new string('x', 250) + "\n  Esc to close\n")!;
        Assert.Equal(OverlayScreen.CutMarker, wide.Body[^1]);
        Assert.Equal(200, wide.Body[1].Length);
        Assert.DoesNotContain(OverlayScreen.CutMarker, OverlayScreen.Read(PaneFixtures.OtherHooksScreen)!.Body);
    }

    [Fact]
    public void A_screen_that_goes_from_Go_back_to_Close_is_a_different_prompt()
    {
        Assert.NotEqual(PromptFingerprint.Of(PaneFixtures.OtherConfigScreen)!.Hash, PromptFingerprint.Of(PaneFixtures.ConfigAfterEsc1)!.Hash);
        Assert.EndsWith("\nEscape. Go back", PromptFingerprint.Of(PaneFixtures.OtherConfigScreen)!.Canonical);
        Assert.NotEqual(PromptFingerprint.Of(PaneFixtures.McpList)!.Hash, PromptFingerprint.Of(PaneFixtures.McpDetail)!.Hash);
    }
}
