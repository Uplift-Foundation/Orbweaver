using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-353: PaneColumns.StripSidePanel, one case per arm of the divider rule, then every
/// fixture. Synthetic screens here are drawn by Row(): chat text padded to a cell column,
/// a divider, the panel's text.
/// </summary>
public class PaneColumnsTests
{
    private const int Col = 40;

    private static string Row(string chat, string panel, int col = Col, char bar = '│')
    {
        var cells = chat.EnumerateRunes().Sum(PaneColumns.Cells);
        return chat + new string(' ', col - cells) + bar + panel;
    }

    // A panel of `rows` rows starting at `top`, chat text on every row.
    private static string[] Screen(int rows, int top = 0, int total = 12, string panel = "board text")
    {
        var lines = new string[total];
        for (var n = 0; n < total; n++)
            lines[n] = n >= top && n < top + rows ? Row($"chat row {n}", panel) : $"chat row {n}";
        return lines;
    }

    [Fact]
    public void A_panel_is_cut_off_every_row_and_the_row_trimmed()
    {
        var cut = PaneColumns.StripSidePanel(Screen(rows: 12));

        Assert.All(cut.Select((l, n) => (l, n)), x => Assert.Equal($"chat row {x.n}", x.l));
    }

    [Fact]
    public void A_screen_without_a_bar_is_returned_as_the_same_array()
    {
        var lines = new[] { "one", "two" };

        Assert.Same(lines, PaneColumns.StripSidePanel(lines));
    }

    [Fact]
    public void A_run_shorter_than_eight_rows_is_not_a_panel()
    {
        var lines = Screen(rows: 7);

        Assert.Same(lines, PaneColumns.StripSidePanel(lines));
    }

    [Fact]
    public void A_run_of_exactly_eight_rows_is_a_panel() =>
        Assert.Equal("chat row 0", PaneColumns.StripSidePanel(Screen(rows: 8))[0]);

    [Fact]
    public void A_run_that_starts_below_row_one_is_not_a_panel()
    {
        var lines = Screen(rows: 9, top: 2, total: 12);

        Assert.Same(lines, PaneColumns.StripSidePanel(lines));
    }

    [Fact]
    public void A_run_starting_on_row_one_is_a_panel() =>
        Assert.Equal("chat row 1", PaneColumns.StripSidePanel(Screen(rows: 9, top: 1, total: 12))[1]);

    [Fact]
    public void A_run_with_more_than_a_tenth_of_its_rows_missing_is_not_a_panel()
    {
        var lines = Screen(rows: 12, total: 12);
        lines[3] = "no bar here";
        lines[6] = "no bar here either";

        Assert.Same(lines, PaneColumns.StripSidePanel(lines));
    }

    [Fact]
    public void A_run_with_a_row_missing_inside_the_allowance_is_still_a_panel_and_that_row_is_left_alone()
    {
        var lines = Enumerable.Range(0, 20).Select(n => Row($"chat {n}", "board")).ToArray();
        lines[5] = "a row the width table drew wrong";

        var cut = PaneColumns.StripSidePanel(lines);

        Assert.Equal("chat 4", cut[4]);
        Assert.Equal("a row the width table drew wrong", cut[5]);
    }

    [Fact]
    public void A_box_edge_with_nothing_to_its_right_is_not_a_panel()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", "")).ToArray();

        Assert.Same(lines, PaneColumns.StripSidePanel(lines));
    }

    [Fact]
    public void Text_to_the_right_on_one_row_only_is_enough()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", n == 7 ? "x" : "")).ToArray();

        Assert.Equal("chat 3", PaneColumns.StripSidePanel(lines)[3]);
    }

    [Fact]
    public void A_divider_left_of_column_twenty_is_not_a_panel()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row("c", "board", col: 19)).ToArray();

        Assert.Same(lines, PaneColumns.StripSidePanel(lines));
    }

    [Fact]
    public void A_divider_on_column_twenty_is_a_panel()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row("c", "board", col: 20)).ToArray();

        Assert.Equal("c", PaneColumns.StripSidePanel(lines)[0]);
    }

    [Fact]
    public void A_column_that_is_not_a_panel_does_not_stop_the_search_for_one_further_right()
    {
        // A short box edge at column 25, then the real divider at column 60.
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", "board", col: 60)).ToArray();
        for (var n = 4; n < 7; n++) lines[n] = lines[n][..25] + "│" + lines[n][26..];

        var cut = PaneColumns.StripSidePanel(lines);

        Assert.Equal("chat 0", cut[0]);
        Assert.Equal("chat 5", cut[5][..6]);
    }

    // Display cells, not string indexes: the row below has CJK and emoji left of
    // the divider, so its bar is at string index 110 where the others' is at 119.
    [Fact]
    public void A_row_of_wide_characters_is_cut_at_the_same_display_column_as_its_neighbours()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", "board", col: 119)).ToArray();
        lines[4] = Row("日本語のテスト 🤖 emoji ✅ done", "board", col: 119);

        var cut = PaneColumns.StripSidePanel(lines);

        Assert.Equal("日本語のテスト 🤖 emoji ✅ done", cut[4]);
        Assert.True(lines[4].IndexOf('│') < 119);
        Assert.Equal("chat 5", cut[5]);
    }

    // A bar up to three cells off (the table is rough) is the divider; four is not.
    [Theory]
    [InlineData(1)]
    [InlineData(-3)]
    [InlineData(3)]
    public void A_bar_within_three_cells_of_the_column_is_cut_there(int offset)
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", "board")).ToArray();
        lines[5] = Row("chat 5", "board", col: Col + offset);

        Assert.Equal("chat 5", PaneColumns.StripSidePanel(lines)[5]);
    }

    [Fact]
    public void A_bar_four_cells_off_is_left_where_it_is()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", "board")).ToArray();
        lines[5] = Row("chat 5", "board", col: Col + 4);

        Assert.Equal(lines[5], PaneColumns.StripSidePanel(lines)[5]);
    }

    // Bars at cells 38 and 41, divider column 40: the nearer one, 41, is the divider.
    [Fact]
    public void Of_two_bars_within_reach_the_nearer_is_the_divider()
    {
        var lines = Enumerable.Range(0, 12).Select(n => Row($"chat {n}", "board")).ToArray();
        lines[5] = Row("chat 5", "", col: Col - 2) + "  │tail";

        Assert.Equal(Row("chat 5", "", col: Col - 2), PaneColumns.StripSidePanel(lines)[5].PadRight(Col - 1));
    }

    [Fact]
    public void The_input_box_under_the_panel_keeps_its_full_width()
    {
        var lines = Enumerable.Range(0, 12).Select(n => n < 8 ? Row($"chat {n}", "board") : $"{new string('─', 60)}").ToArray();

        Assert.Equal(new string('─', 60), PaneColumns.StripSidePanel(lines)[10]);
    }

    [Fact]
    public void The_input_is_not_modified()
    {
        var lines = Screen(rows: 12);
        var before = lines.ToArray();

        PaneColumns.StripSidePanel(lines);

        Assert.Equal(before, lines);
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("日", 2)]
    [InlineData("🤖", 2)]
    [InlineData("✅", 2)]
    [InlineData("́", 0)]
    [InlineData("️", 0)]
    [InlineData("​", 0)]
    [InlineData("│", 1)]
    public void Cells_counts_display_columns(string text, int expected) =>
        Assert.Equal(expected, PaneColumns.Cells(Rune.GetRuneAt(text, 0)));

    // --- the fixtures --------------------------------------------------------

    public static IEnumerable<object[]> Captures() => SidePanelFixtures.All.Select(f => new object[] { f.Name });

    private static string Of(string name) => SidePanelFixtures.All.First(f => f.Name == name).Screen;

    private static string Strip(string screen) => string.Join("\n", PaneColumns.StripSidePanel(screen.Split('\n')));

    // Equal, not "similar": the strip must give exactly what cutting each row by hand gives.
    [Theory]
    [MemberData(nameof(Captures))]
    public void Stripping_a_capture_equals_cutting_it_by_hand(string name)
    {
        var screen = Of(name);

        Assert.Equal(SidePanelFixtures.HandCut(screen), Strip(screen));
        Assert.NotEqual(screen, Strip(screen));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void A_stripped_capture_has_no_divider_in_its_chat_columns_to_the_right_of_twenty(string name) =>
        Assert.DoesNotContain(Strip(Of(name)).Split('\n'), l => l.Length > 118 && l.Contains('│', StringComparison.Ordinal) && l.LastIndexOf('│') >= 118);

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    [Theory]
    [MemberData(nameof(Captures))]
    public void Every_reader_gives_the_same_answer_for_a_capture_as_for_its_hand_cut_twin(string name)
    {
        var screen = Of(name);
        var twin = SidePanelFixtures.HandCut(screen);

        Assert.Equal(Json(ChatTranscript.ParseAskForm(twin)), Json(ChatTranscript.ParseAskForm(screen)));
        Assert.Equal(Json(ChatTranscript.ParseDialog(twin)), Json(ChatTranscript.ParseDialog(screen)));
        Assert.Equal(Json(MirrorProtocol.PromptFromScreen(twin)), Json(MirrorProtocol.PromptFromScreen(screen)));
        Assert.Equal(Json(PromptFingerprint.Of(twin)), Json(PromptFingerprint.Of(screen)));
        Assert.Equal(PaneScreen.Read(twin), PaneScreen.Read(screen));
        Assert.Equal(AskFormTranscript.ReadForm(twin, null) is null, AskFormTranscript.ReadForm(screen, null) is null);
    }

    // The failure being fixed: the board's rows used to land in the form.
    [Theory]
    [InlineData("FormTwoTabSingle")]
    [InlineData("FormTwoTabMulti")]
    [InlineData("FormFramedWrapped")]
    [InlineData("TypedFreeText")]
    public void A_form_beside_a_panel_has_tabs_and_labels_with_no_divider_in_them(string name)
    {
        var form = ChatTranscript.ParseAskForm(Of(name));

        Assert.NotNull(form);
        Assert.NotEmpty(form!.Tabs);
        Assert.DoesNotContain(form.Prompt.Options, o => o.Label.Contains('│', StringComparison.Ordinal) || o.Label.Length > 120);
    }

    [Fact]
    public void A_multi_select_beside_a_panel_has_its_submit_row() =>
        Assert.True(ChatTranscript.ParseAskForm(Of("FormTwoTabMulti"))!.HasSubmitRow);

    [Fact]
    public void The_free_text_capture_still_ticks_what_was_ticked() =>
        Assert.True(ChatTranscript.ParseAskForm(Of("TypedFreeText"))!.Checked!.Count >= 3);

    [Fact]
    public void An_idle_session_with_the_board_open_is_still_at_its_prompt()
    {
        Assert.Equal(PaneReading.Prompt, PaneScreen.Classify(Of("IdleBoard")));
        Assert.Equal(PaneReading.Prompt, PaneScreen.Classify(Of("WideBoard")));
    }

    [Fact]
    public void A_permission_dialog_beside_a_panel_is_a_dialog() =>
        Assert.Equal(PaneReading.Dialog, PaneScreen.Classify(Of("PermissionBoard")));

    // The fingerprint used to hash the board, so it moved whenever the board did.
    [Fact]
    public void The_fingerprint_does_not_move_when_only_the_board_changes()
    {
        var screen = Of("FormTwoTabMulti");
        var changed = string.Join("\n", screen.Split('\n').Select(l =>
        {
            var bar = l.LastIndexOf('│');
            return bar >= 20 ? l[..(bar + 1)] + new string('z', l.Length - bar - 1) : l;
        }));

        Assert.Equal(PromptFingerprint.Of(screen)!.Hash, PromptFingerprint.Of(changed)!.Hash);
    }

    // No fixture the repository already holds is changed: PaneFixtures, AskFormFixtures and the
    // real screens under Fixtures/screens. 26 of them hold a `│` at cell 20 or beyond, Grok's
    // boxes included, so this is the false-positive control for the rule.
    [Fact]
    public void Stripping_leaves_every_existing_fixture_untouched()
    {
        var swept = 0;
        foreach (var (name, screen) in ExistingScreens())
        {
            var lines = screen.Replace("\r", "").Split('\n');
            Assert.True(ReferenceEquals(lines, PaneColumns.StripSidePanel(lines)) || lines.SequenceEqual(PaneColumns.StripSidePanel(lines)), name);
            swept++;
        }
        Assert.True(swept >= 100, $"swept only {swept}");
    }

    private static IEnumerable<(string Name, string Screen)> ExistingScreens()
    {
        foreach (var type in new[] { typeof(PaneFixtures), typeof(AskFormFixtures) })
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                if (f.FieldType == typeof(string) && f.GetValue(null) is string s && s.Contains('\n', StringComparison.Ordinal))
                    yield return ($"{type.Name}.{f.Name}", s);

        var dir = Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures");
        foreach (var file in Directory.GetFiles(dir, "*.txt"))
            yield return (Path.GetFileName(file), File.ReadAllText(file));
    }
}
