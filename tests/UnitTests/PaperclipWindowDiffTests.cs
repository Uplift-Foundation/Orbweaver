using Xunit;

namespace Orbweaver.Tests;

// CB-447 (frames-v18 tail_rule): what a phone is owed when a Paperclip thread changes. Pure.
public class PaperclipWindowDiffTests
{
    private static TranscriptWatch.WireLine L(string role, string text, bool error = false, long at = 5) =>
        TranscriptWatch.Line(role, text, at, error);

    private static List<TranscriptWatch.WireLine> Rows(params string[] texts) =>
        texts.Select((t, i) => L(i == 0 ? "system" : "user", t)).ToList();

    private static (PaperclipWindowDiff.Change Kind, int From) Diff(List<TranscriptWatch.WireLine> a, string? ta, List<TranscriptWatch.WireLine> b, string? tb) =>
        PaperclipWindowDiff.Diff(a, ta, b, tb);

    [Fact]
    public void Nothing_differs_and_nothing_was_appended_sends_nothing_and_at_is_never_compared()
    {
        var a = Rows("task", "one", "two");
        var b = new List<TranscriptWatch.WireLine> { L("system", "task", at: 99), L("user", "one", at: 98), L("user", "two", at: 97) };
        Assert.Equal((PaperclipWindowDiff.Change.None, 0), Diff(a, "t1", b, "t1"));
    }

    [Fact]
    public void Rows_appended_are_a_tail_from_the_old_count()
    {
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 3), Diff(Rows("task", "a", "b"), "t1", Rows("task", "a", "b", "c", "d"), "t1"));
    }

    [Fact]
    public void A_changed_row_that_is_not_the_last_restates_everything_from_it_through_the_end()
    {
        var a = Rows("task", "tool", "answer");
        var b = new List<TranscriptWatch.WireLine> { L("system", "task"), L("user", "tool", error: true), L("user", "answer") };
        // One tool line turned red: error counts, which frames-v4's role-and-text rule would never see.
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 1), Diff(a, "t1", b, "t1"));

        // The task row moving from in progress to done is index 0: the whole thread restates.
        var done = new List<TranscriptWatch.WireLine> { L("system", "task - done"), L("user", "tool"), L("user", "answer") };
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 0), Diff(a, "t1", done, "t1"));

        // A changed row and a new one: from the changed one, through the new one.
        var both = new List<TranscriptWatch.WireLine> { L("system", "task"), L("user", "tool changed"), L("user", "answer"), L("user", "more") };
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 1), Diff(a, "t1", both, "t1"));

        // A changed role alone is a change.
        var role = new List<TranscriptWatch.WireLine> { L("system", "task"), L("system", "tool"), L("user", "answer") };
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 1), Diff(a, "t1", role, "t1"));
    }

    [Fact]
    public void A_shrunk_thread_or_a_replaced_task_is_a_reset()
    {
        Assert.Equal((PaperclipWindowDiff.Change.Reset, 0), Diff(Rows("task", "a", "b"), "t1", Rows("task", "a"), "t1"));
        Assert.Equal((PaperclipWindowDiff.Change.Reset, 0), Diff(Rows("task", "a"), "t1", Rows("task", "a", "b"), "t2"));
        Assert.Equal((PaperclipWindowDiff.Change.Reset, 0), Diff(Rows("task", "a"), "t1", Rows("No task"), null));   // task to none
    }

    [Fact]
    public void From_no_task_to_the_task_a_message_opened_is_growth_not_a_reset()
    {
        // The sent line is the task's description, not a comment; a reset would drop it.
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 0), Diff(Rows("No task yet.", "my message"), null, Rows("CB-1 t", "my message"), "t9"));
        Assert.Equal((PaperclipWindowDiff.Change.None, 0), Diff(Rows("No task yet."), null, Rows("No task yet."), null));
    }

    [Fact]
    public void At_the_row_cap_a_first_difference_at_row_one_is_the_oldest_row_dropping_so_a_reset()
    {
        var full = Enumerable.Range(0, PaperclipChat.RowCap).Select(i => L(i == 0 ? "system" : "user", i == 0 ? "task" : "m" + i)).ToList();
        var shifted = new List<TranscriptWatch.WireLine> { full[0] };
        shifted.AddRange(full.Skip(2));
        shifted.Add(L("user", "newest"));
        Assert.Equal((PaperclipWindowDiff.Change.Reset, 0), Diff(full, "t1", shifted, "t1"));

        // Below the cap the same first difference is just a tail from row 1, and at the cap a difference
        // elsewhere is a tail too.
        var below = full.Take(PaperclipChat.RowCap - 1).ToList();
        var belowShifted = new List<TranscriptWatch.WireLine> { below[0] };
        belowShifted.AddRange(below.Skip(2));
        belowShifted.Add(L("user", "newest"));
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 1), Diff(below, "t1", belowShifted, "t1"));
        var elsewhere = full.ToList();
        elsewhere[10] = L("user", "changed");
        Assert.Equal((PaperclipWindowDiff.Change.Tail, 10), Diff(full, "t1", elsewhere, "t1"));
    }

    [Fact]
    public void Windows_are_a_reset_backfill_or_a_live_tail_from_the_first_change_or_nothing()
    {
        var now = Rows("task", "a", "b", "c");
        var reset = PaperclipWindowDiff.Windows("s", now, (PaperclipWindowDiff.Change.Reset, 0));
        var tail = PaperclipWindowDiff.Windows("s", now, (PaperclipWindowDiff.Change.Tail, 2));
        Assert.Empty(PaperclipWindowDiff.Windows("s", now, (PaperclipWindowDiff.Change.None, 0)));

        var r = System.Text.Json.JsonDocument.Parse(Assert.Single(reset)).RootElement;
        Assert.Equal((0, 4, false, true), (r.GetProperty("from_line").GetInt32(), r.GetProperty("total").GetInt32(), r.GetProperty("live").GetBoolean(), r.GetProperty("reset").GetBoolean()));
        var t = System.Text.Json.JsonDocument.Parse(Assert.Single(tail)).RootElement;
        Assert.Equal((2, 4, true), (t.GetProperty("from_line").GetInt32(), t.GetProperty("total").GetInt32(), t.GetProperty("live").GetBoolean()));
        Assert.False(t.TryGetProperty("reset", out var rs) && rs.GetBoolean());
    }

    [Fact]
    public void A_row_is_a_wire_line_and_the_task_row_has_no_time_of_its_own()
    {
        var task = PaperclipWindowDiff.Line(new PaperclipChat.Row(PaperclipChat.TaskKey, ChatRole.System, "CB-1 t", DateTime.MinValue));
        Assert.Equal(("system", "CB-1 t", 0L, false), (task.Role, task.Text, task.At, task.Error));

        var at = new DateTime(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);
        var tool = PaperclipWindowDiff.Line(new PaperclipChat.Row("r:1:t:a", ChatRole.System, "Bash: ls", at, Error: true));
        Assert.Equal(("system", 1795492800L, true), (tool.Role, tool.At, tool.Error));
        Assert.Equal("user", PaperclipWindowDiff.Line(new PaperclipChat.Row("c:1", ChatRole.User, "hi", at)).Role);
        Assert.Equal("assistant", PaperclipWindowDiff.Line(new PaperclipChat.Row("c:2", ChatRole.Assistant, "yo", at)).Role);
    }
}
