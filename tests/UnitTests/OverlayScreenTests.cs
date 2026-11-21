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

    [Fact]
    public void A_rule_further_than_twelve_lines_above_the_footer_is_not_its_edge()
    {
        var far = "▔▔▔▔▔▔▔▔▔▔▔▔\n" + string.Join("\n", Enumerable.Repeat("some prose here", 13)) + "\n  Esc to close\n";
        Assert.Null(OverlayScreen.Read(far));
        var near = "▔▔▔▔▔▔▔▔▔▔▔▔\n" + string.Join("\n", Enumerable.Repeat("some prose here", 11)) + "\n  Esc to close\n";
        Assert.NotNull(OverlayScreen.Read(near));
    }

    // The edge is any one repeated symbol, not the glyph the Mac happened to draw.
    [Theory]
    [InlineData("▔▔▔▔▔▔▔▔▔▔▔▔")]
    [InlineData("────────────")]
    [InlineData("============")]
    public void The_top_edge_is_a_repeated_symbol_whatever_it_is(string rule) =>
        Assert.NotNull(OverlayScreen.Read($"{rule}\n  A title\n  Esc to close\n"));

    [Theory]
    [InlineData("ABABABABABAB")]      // letters
    [InlineData("111111111111")]      // digits
    [InlineData("▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔ x")] // not one character
    [InlineData("▔▔▔▔▔▔▔▔▔")]         // too short
    public void Anything_else_is_not_an_edge(string notRule) =>
        Assert.Null(OverlayScreen.Read($"{notRule}\n  A title\n  Esc to close\n"));

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

    [Theory]
    [InlineData(nameof(PaneFixtures.PermissionsAllow))]
    [InlineData(nameof(PaneFixtures.PermissionsRecentlyBlockedEmpty))]
    public void A_permissions_list_is_not_an_overlay(string name)
    {
        var screen = (string)typeof(PaneFixtures).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        Assert.Null(OverlayScreen.Read(screen));
        // And with the footer added the navigated list still wins.
        Assert.Null(OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n" + screen + "\n  Esc to close\n"));
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
    public void A_body_is_bounded_in_lines_and_in_width()
    {
        var rows = string.Join("\n", Enumerable.Range(0, 80).Select(i => "  row " + i + new string('x', 250)));
        var o = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n" + rows + "\n  Esc to close\n");
        Assert.Null(o); // 80 lines of body put the footer more than twelve lines under the edge
        var many = "▔▔▔▔▔▔▔▔▔▔▔▔\n  T\n" + string.Join("\n", Enumerable.Range(0, 3).Select(i => "  r" + new string('x', 250))) + "\n  Esc to close\n";
        var wide = OverlayScreen.Read(many)!;
        Assert.All(wide.Body, r => Assert.True(r.Length <= 200));
        var tall = OverlayScreen.Read("▔▔▔▔▔▔▔▔▔▔▔▔\n  T\n  Esc to close\n" + string.Join("\n", Enumerable.Range(0, 80).Select(i => "  line " + i)));
        Assert.Equal(60, tall!.Body.Count);
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
}
