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
        Assert.DoesNotContain(o.Body, r => r.Contains(OverlayScreen.Footer));
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
    public void A_body_with_no_letters_has_an_empty_title()
    {
        var o = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  ██▀▀\n  Esc to close\n")!;
        Assert.Equal("", o.Title);
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
        Assert.Equal(60, o.Body.Count);
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
    [Theory]
    [InlineData(nameof(PaneFixtures.OtherHelpScreen))]
    [InlineData(nameof(PaneFixtures.OtherIdeScreen))]
    [InlineData(nameof(PaneFixtures.OtherResumeScreen))]
    [InlineData(nameof(PaneFixtures.OtherMemoryScreen))]
    public void Other_Esc_to_close_screens_read_as_overlays(string name)
    {
        Assert.False(string.IsNullOrWhiteSpace(OverlayScreen.Read(Fixture(name))!.Title));
        Assert.Equal((PaneReading.Dialog, "overlay"), PaneScreen.Read(Fixture(name)));
    }

    [Theory]
    [InlineData(nameof(PaneFixtures.OtherConfigScreen))]
    [InlineData(nameof(PaneFixtures.OtherModelScreen))]
    [InlineData(nameof(PaneFixtures.OtherThemeScreen))]
    [InlineData(nameof(PaneFixtures.OtherRewindScreen))]
    [InlineData(nameof(PaneFixtures.OtherExportScreen))]
    public void Screens_without_an_Esc_to_close_footer_are_not_overlays(string name) =>
        Assert.Null(OverlayScreen.Read(Fixture(name)));

    // A navigated list is not excluded any more: the footer decides.
    [Theory]
    [InlineData(nameof(PaneFixtures.PermissionsAllow))]
    [InlineData(nameof(PaneFixtures.PermissionsRecentlyBlockedEmpty))]
    [InlineData(nameof(PaneFixtures.MiniRecentlyBlockedPopulatedStripped))]
    public void A_permissions_tab_is_not_an_overlay(string name) => Assert.Null(OverlayScreen.Read(Fixture(name)));

    [Fact]
    public void A_navigated_list_that_does_say_Esc_to_close_is_an_overlay()
    {
        Assert.NotNull(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  Permissions  Recently blocked\n  ←/→ to switch\n  Esc to close\n"));
        Assert.Null(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  Recently blocked\n  Enter to approve · r to retry · Esc to approve 1 blocked\n"));
    }

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
}
