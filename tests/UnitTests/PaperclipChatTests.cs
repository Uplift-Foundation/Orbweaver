using System.Text;
using System.Text.Json;
using Orbweaver;
using Xunit;
using static Orbweaver.PaperclipChat;

namespace Orbweaver.Tests;

// CB-443: the pure half of the Paperclip chat. Fixtures under native/chat are the lead's real bodies
// (comments newest first; one run's events) reduced and scrubbed. A body built in this file is edited or
// invented, and named so: Edited_ / Synthetic_.
public class PaperclipChatTests
{
    private static string Fx(string name) => PaperclipFx.Read("native/chat/" + name);
    private static readonly DateTime T0 = new(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc);
    private const string Agent = "agent-1";

    // ---- ParseComments ------------------------------------------------------

    [Fact]
    public void The_real_comment_list_comes_back_oldest_first_with_each_author_kind()
    {
        var c = ParseComments(Fx("comments-user-then-agent.json"));
        Assert.Equal(3, c.Count);
        Assert.True(c[0].At < c[1].At && c[1].At < c[2].At);
        Assert.Equal("user-1", c[1].AuthorUserId);
        Assert.Null(c[1].AuthorAgentId);
        Assert.Equal("agent-1", c[2].AuthorAgentId);
        Assert.Equal("17 times 3 is 51.", c[2].Body);
        Assert.NotNull(c[2].RunId);
    }

    [Fact]
    public void Synthetic_comments_missing_fields_deleted_and_wrong_types_are_skipped_or_defaulted()
    {
        var json = """
            [ {"id":"a","body":"kept","createdAt":"not a date"},
              {"id":"b"},
              {"body":"no id"},
              {"id":"d","body":"gone","deletedAt":"2026-11-24T03:00:00Z"},
              5,
              {"id":"e","body":"","authorAgentId":"  "} ]
            """;
        var c = ParseComments(json);
        Assert.Equal(["e", "a"], c.Select(x => x.Id));
        Assert.Null(c.First(x => x.Id == "a").At);
        Assert.Null(c.First(x => x.Id == "e").AuthorAgentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"x\":1}")]
    [InlineData("\"a string\"")]
    public void Comments_that_are_not_an_array_parse_to_nothing(string? body) => Assert.Empty(ParseComments(body));

    [Fact]
    public void An_object_that_wraps_the_array_is_read_too() =>
        Assert.Single(ParseComments("{\"comments\":[{\"id\":\"a\",\"body\":\"x\"}]}"));

    // ---- Fold ---------------------------------------------------------------

    [Fact]
    public void The_real_run_shows_one_answer_and_no_tool_row()
    {
        var a = Fold(Activity.Empty("run-1"), Fx("run-events.json"));
        // The only tool is paperclip_finish, the runner's own bookkeeping; the non-speech item kinds are skipped;
        // deltas are ignored; the summary, the final message and (later) the comment are one answer.
        var row = Assert.Single(a.Rows);
        Assert.Equal(AnswerKey("run-1"), row.Key);
        Assert.Equal(ChatRole.Assistant, row.Role);
        Assert.Equal("17 times 3 is 51.", row.Text);
        Assert.Equal(87, a.LastSeq);   // the highest seq in the body, not the last speech
    }

    [Fact]
    public void The_same_body_fed_in_three_afterSeq_slices_gives_the_rows_of_the_whole()
    {
        var whole = Fold(Activity.Empty("run-1"), Fx("run-events.json"));
        var all = JsonDocument.Parse(Fx("run-events.json")).RootElement.EnumerateArray().ToList();
        var soFar = Activity.Empty("run-1");
        foreach (var slice in new[] { all.Where(e => e.GetProperty("seq").GetInt64() <= 47 ),
                                      all.Where(e => e.GetProperty("seq").GetInt64() is > 47 and <= 60),
                                      all.Where(e => e.GetProperty("seq").GetInt64() > 60) })
            soFar = Fold(soFar, "[" + string.Join(",", slice.Select(e => e.GetRawText())) + "]");
        Assert.Equal(whole.Rows, soFar.Rows);
        Assert.Equal(whole.LastSeq, soFar.LastSeq);
    }

    [Fact]
    public void Feeding_the_same_slice_twice_changes_nothing()
    {
        var once = Fold(Activity.Empty("run-1"), Fx("run-events.json"));
        var twice = Fold(once, Fx("run-events.json"));
        Assert.Equal(once.Rows, twice.Rows);
        Assert.Equal(once.LastSeq, twice.LastSeq);
    }

    // One event in the shape the real body has (everything under payload.prpEvent.payload).
    private static string Ev(long seq, string type, object? payload, string? itemId = "i" ) =>
        JsonSerializer.Serialize(new
        {
            seq, eventType = type, createdAt = T0.AddSeconds(seq).ToString("o"),
            payload = payload == null ? null : new { prpEvent = new { itemId, payload } },
        });
    private static string Body(params string[] events) => "[" + string.Join(",", events) + "]";

    [Fact]
    public void Synthetic_a_shell_tool_with_a_subject_reads_name_colon_subject()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "tool.execution.started", new { name = "Bash", executionId = "x1", status = "running" }),
            Ev(2, "tool.execution.progressed", new { name = "Bash", executionId = "x1", subject = "git status" }),
            Ev(3, "tool.execution.completed", new { name = "Bash", executionId = "x1", status = "completed", exitCode = 0 })));
        var row = Assert.Single(a.Rows);
        Assert.Equal(ToolKey("r", "x1"), row.Key);
        Assert.Equal("Bash: git status", row.Text);
        Assert.False(row.Error);
        Assert.Equal(ChatRole.System, row.Role);
    }

    [Fact]
    public void Synthetic_a_tool_that_only_starts_is_a_row_with_just_its_name()
    {
        var a = Fold(Activity.Empty("r"), Body(Ev(1, "tool.execution.started", new { name = "Read", executionId = "x1" })));
        Assert.Equal("Read", Assert.Single(a.Rows).Text);
    }

    [Fact]
    public void Edited_a_tool_that_failed_by_status_or_by_exit_code_is_red_and_a_clean_one_is_not()
    {
        string One(object done) => JsonSerializer.Serialize(
            Fold(Fold(Activity.Empty("r"), Body(Ev(1, "tool.execution.started", new { name = "Bash", executionId = "x" }))),
                Body(Ev(2, "tool.execution.completed", done))).Rows.Select(r => r.Error));
        Assert.Equal("[true]", One(new { name = "Bash", executionId = "x", status = "failed" }));
        Assert.Equal("[true]", One(new { name = "Bash", executionId = "x", status = "completed", exitCode = 2 }));
        Assert.Equal("[false]", One(new { name = "Bash", executionId = "x", status = "completed", exitCode = 0 }));
        Assert.Equal("[false]", One(new { name = "Bash", executionId = "x", status = "completed", exitCode = (string?)null }));
        Assert.Equal("[false]", One(new { name = "Bash", executionId = "x", status = "completed", exitCode = "oops" }));
        Assert.Equal("[false]", One(new { name = "Bash", executionId = "x", status = "completed", exitCode = 1.5 }));
    }

    [Fact]
    public void Synthetic_the_paperclip_namespace_is_hidden_beside_a_control_that_is_shown()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "tool.execution.started", new { name = "paperclip_finish", executionId = "p", @namespace = "paperclip" }),
            Ev(2, "tool.execution.started", new { name = "Bash", executionId = "b", @namespace = "shell" }),
            Ev(3, "tool.execution.completed", new { name = "paperclip_finish", executionId = "p", status = "completed" })));
        Assert.Equal(["Bash"], a.Rows.Select(r => r.Text));
    }

    [Fact]
    public void Synthetic_a_namespace_that_appears_only_on_a_later_event_removes_the_row_it_already_made()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "tool.execution.started", new { name = "x", executionId = "p" }),
            Ev(2, "tool.execution.progressed", new { name = "x", executionId = "p", @namespace = "paperclip" })));
        Assert.Empty(a.Rows);
    }

    [Fact]
    public void Synthetic_tool_events_with_no_id_or_no_known_name_make_no_row()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "tool.execution.started", new { name = "Bash" }),
            Ev(2, "tool.execution.completed", new { executionId = "never-started" })));
        Assert.Empty(a.Rows);
        Assert.Equal(2, a.LastSeq);
    }

    [Fact]
    public void Synthetic_a_progressed_event_without_a_subject_keeps_the_subject_it_had()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "tool.execution.started", new { name = "Bash", executionId = "x" }),
            Ev(2, "tool.execution.progressed", new { name = "Bash", executionId = "x", subject = "ls" }),
            Ev(3, "tool.execution.progressed", new { name = "Bash", executionId = "x" })));
        Assert.Equal("Bash: ls", Assert.Single(a.Rows).Text);
    }

    [Theory]
    [InlineData("thread_lineage")]
    [InlineData("model")]
    [InlineData("usage")]
    [InlineData("reasoning")]
    public void Synthetic_item_completed_of_another_kind_is_not_speech(string kind) =>
        Assert.Empty(Fold(Activity.Empty("r"), Body(Ev(1, "item.completed", new { kind, text = "x" }))).Rows);

    [Fact]
    public void Synthetic_a_progress_message_is_its_own_bubble_and_an_empty_one_is_skipped()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "item.completed", new { kind = "agentMessage", text = "Looking now.", channel = "progress" }, "m1"),
            Ev(2, "item.completed", new { kind = "agentMessage", text = "", channel = "progress" }, "m2"),
            Ev(3, "item.completed", new { kind = "agentMessage", text = "Done.", channel = "final" }, "m3")));
        Assert.Equal([SpeechKey("r", "m1"), AnswerKey("r")], a.Rows.Select(r => r.Key));
        Assert.All(a.Rows, r => Assert.Equal(ChatRole.Assistant, r.Role));
    }

    [Fact]
    public void Synthetic_a_message_with_no_item_id_still_gets_a_stable_key()
    {
        var a = Fold(Activity.Empty("r"), Body(Ev(7, "item.completed", new { kind = "agentMessage", text = "hi", channel = "progress" }, null)));
        Assert.Equal(SpeechKey("r", "seq7"), Assert.Single(a.Rows).Key);
    }

    [Fact]
    public void Synthetic_the_answer_is_the_summary_when_no_final_message_comes_and_the_final_wins_when_it_does()
    {
        var onlySummary = Fold(Activity.Empty("r"), Body(Ev(1, "run.result.proposed", new { summary = "From the result." })));
        Assert.Equal("From the result.", Assert.Single(onlySummary.Rows).Text);

        // the final message after a summary replaces the text in the same row
        var both = Fold(onlySummary, Body(Ev(2, "item.completed", new { kind = "agentMessage", text = "Final words.", channel = "final" })));
        var row = Assert.Single(both.Rows);
        Assert.Equal("Final words.", row.Text);
        Assert.Equal(onlySummary.Rows[0].At, row.At);

        // and a summary after the final does not override it
        var summaryLate = Fold(both, Body(Ev(3, "run.result.proposed", new { summary = "Later summary." })));
        Assert.Equal("Final words.", Assert.Single(summaryLate.Rows).Text);
        Assert.Equal("Later summary.", summaryLate.Summary);
    }

    [Fact]
    public void Synthetic_a_blank_summary_makes_no_answer()
    {
        Assert.Empty(Fold(Activity.Empty("r"), Body(Ev(1, "run.result.proposed", new { summary = "  " }))).Rows);
        Assert.Empty(Fold(Activity.Empty("r"), Body(Ev(1, "run.result.proposed", new { other = 1 }))).Rows);
    }

    [Fact]
    public void Synthetic_a_null_payload_an_unknown_event_and_a_payload_without_prpEvent_are_skipped()
    {
        var weird = JsonSerializer.Serialize(new { seq = 3, eventType = "tool.execution.started", payload = new { other = 1 } });
        var a = Fold(Activity.Empty("r"), Body(
            Ev(1, "lifecycle", null),
            Ev(2, "something.new", new { whatever = true }),
            weird,
            "{\"seq\":4,\"eventType\":\"item.completed\",\"payload\":{\"prpEvent\":5}}"));
        Assert.Empty(a.Rows);
        Assert.Equal(4, a.LastSeq);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    public void A_bad_events_body_returns_the_state_unchanged(string? body)
    {
        var soFar = Fold(Activity.Empty("r"), Body(Ev(1, "run.result.proposed", new { summary = "x" })));
        Assert.Same(soFar, Fold(soFar, body));
    }

    [Fact]
    public void Events_out_of_order_in_one_slice_are_applied_by_seq()
    {
        var a = Fold(Activity.Empty("r"), Body(
            Ev(2, "item.completed", new { kind = "agentMessage", text = "Final.", channel = "final" }),
            Ev(1, "run.result.proposed", new { summary = "Summary." })));
        Assert.Equal("Final.", Assert.Single(a.Rows).Text);
    }

    [Fact]
    public void CountEvents_counts_the_array() => Assert.Equal(2, CountEvents("[{},{}]"));

    // ---- CurrentTask ----------------------------------------------------------

    private static PaperclipApi.RawIssue Issue(string id, string status = "todo", string? assignee = Agent, string ident = "CB-1") =>
        new(id, ident, "Title " + id, status, assignee);
    private static PaperclipApi.RawLiveRun Live(string issue, string agent = Agent, DateTime? at = null) =>
        new("run-live", agent, "running", at ?? T0, issue, null, null);
    private static PaperclipApi.RawRun Done(string issue, string agent = Agent, DateTime? finished = null) =>
        new("run-done", agent, "succeeded", T0, finished ?? T0.AddMinutes(1), null, issue, null, null, false);
    private static AssignedIssue Assigned(string id, DateTime? at, string status = "done") =>
        new(new TaskRef(id, "CB-9", "Assigned " + id, status), at, status == "cancelled");

    private static readonly IReadOnlyList<PaperclipApi.RawLiveRun> NoLive = [];
    private static readonly IReadOnlyList<PaperclipApi.RawIssue> NoIssues = [];
    private static readonly IReadOnlyList<PaperclipApi.RawRun> NoRuns = [];

    [Fact]
    public void Rung_1_the_live_run_wins_over_everything_and_the_newest_live_run_wins_among_live()
    {
        var t = CurrentTask(Agent, [Live("i-old", at: T0), Live("i-new", at: T0.AddMinutes(5)), Live("other", "someone-else")],
            [Issue("b")], [Done("d")], [Assigned("a", T0)], [Issue("i-new", "in_progress")]);
        Assert.Equal(new TaskRef("i-new", "CB-1", "Title i-new", "in_progress"), t);
    }

    [Fact]
    public void Rung_2_the_blocked_task_of_this_agent_beats_a_finished_run()
    {
        var t = CurrentTask(Agent, NoLive, [Issue("other", assignee: "x"), Issue("b", "blocked")], [Done("d")], null, NoIssues);
        Assert.Equal("b", t!.Id);
        Assert.Equal("blocked", t.Status);
    }

    [Fact]
    public void Rung_3_the_newest_finished_run_with_a_task()
    {
        var older = new PaperclipApi.RawRun("r1", Agent, "succeeded", T0, T0.AddMinutes(1), null, "i-old", null, null, false);
        var newer = new PaperclipApi.RawRun("r2", Agent, "failed", T0, T0.AddMinutes(9), null, "i-new", null, null, false);
        var noTask = new PaperclipApi.RawRun("r3", Agent, "succeeded", T0, T0.AddMinutes(20), null, null, null, null, false);
        var t = CurrentTask(Agent, NoLive, NoIssues, [older, noTask, newer, Done("x", "someone-else")], null, [Issue("i-new", "done")]);
        Assert.Equal("i-new", t!.Id);
    }

    [Fact]
    public void A_task_id_nobody_holds_gives_a_ref_with_the_id_alone()
    {
        var t = CurrentTask(Agent, [Live("ghost")], NoIssues, NoRuns, null, NoIssues);
        Assert.Equal(new TaskRef("ghost", "", "", ""), t);
    }

    [Fact]
    public void A_task_known_only_from_the_assigned_read_is_resolved_from_it()
    {
        var t = CurrentTask(Agent, [Live("a1")], NoIssues, NoRuns, [Assigned("a1", T0, "in_progress")], NoIssues);
        Assert.Equal("Assigned a1", t!.Title);
    }

    [Fact]
    public void Rung_4_the_most_recent_not_cancelled_assigned_task_and_a_done_one_counts()
    {
        var t = CurrentTask(Agent, NoLive, NoIssues, NoRuns,
            [Assigned("old", T0), Assigned("new", T0.AddHours(1)), Assigned("newest-but-cancelled", T0.AddHours(2), "cancelled"), Assigned("undated", null)], NoIssues);
        Assert.Equal("new", t!.Id);
        Assert.Equal("done", t.Status);
    }

    [Fact]
    public void Rung_5_none_when_nothing_exists_or_the_assigned_read_was_not_made_or_all_are_cancelled()
    {
        Assert.Null(CurrentTask(Agent, NoLive, NoIssues, NoRuns, null, NoIssues));
        Assert.Null(CurrentTask(Agent, NoLive, NoIssues, NoRuns, [], NoIssues));
        Assert.Null(CurrentTask(Agent, NoLive, NoIssues, NoRuns, [Assigned("c", T0, "cancelled")], NoIssues));
    }

    [Fact]
    public void The_real_assigned_issues_parse_and_skip_nothing_with_an_id()
    {
        var json = """
            [ {"id":"a","identifier":"ORB-1","title":"T","status":"done","lastActivityAt":"2026-11-24T03:00:00Z"},
              {"id":"b","status":"cancelled","updatedAt":"2026-11-24T04:00:00Z"},
              {"id":"c","status":"todo","cancelledAt":"2026-11-24T04:00:00Z"},
              {"title":"no id"}, 3 ]
            """;
        var l = ParseAssignedIssues(json);
        Assert.Equal(["a", "b", "c"], l.Select(x => x.Task.Id));
        Assert.Equal(new DateTime(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc), l[0].UpdatedAt);
        Assert.Equal(new DateTime(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc), l[1].UpdatedAt);
        Assert.False(l[0].Cancelled);
        Assert.True(l[1].Cancelled);
        Assert.True(l[2].Cancelled);
        Assert.Empty(ParseAssignedIssues("nope"));
    }

    // ---- Build ----------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string> { ["agent-1"] = "Ines", ["agent-2"] = "Odile", ["agent-3"] = " " };

    private static Comment C(string id, string body, string? agent = null, string? user = null, string? run = null, int sec = 0) =>
        new(id, body, agent, user, run, T0.AddSeconds(sec));

    [Fact]
    public void Build_puts_the_task_first_then_comments_and_activity_by_time_with_each_author_kind()
    {
        var act = Fold(Activity.Empty("r"), Body(
            Ev(1, "tool.execution.started", new { name = "Bash", executionId = "x" })));   // created at T0+1s
        var rows = Build(new TaskRef("t", "CB-12", "Triage", "in_progress"), Agent, Names, [
            C("c3", "late", agent: Agent, sec: 30),
            C("c1", "hello", user: "user-1", sec: 0),
            C("c2", "side note", agent: "agent-2", sec: 2),
            C("c4", "odd", agent: "agent-3", sec: 3),
            C("c5", "from nowhere", sec: 4),
            C("c6", "unknown agent", agent: "agent-9", sec: 5)], act);
        Assert.Equal(["task", "c:c1", "r:r:t:x", "c:c2", "c:c4", "c:c5", "c:c6", "c:c3"], rows.Select(r => r.Key));
        Assert.Equal("CB-12 Triage · in progress", rows[0].Text);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.System, ChatRole.System, ChatRole.System, ChatRole.System, ChatRole.System, ChatRole.Assistant],
            rows.Select(r => r.Role));
        Assert.Equal("Odile: side note", rows[3].Text);
        Assert.Equal("Another agent: odd", rows[4].Text);
        Assert.Equal("from nowhere", rows[5].Text);
        Assert.Equal("Another agent: unknown agent", rows[6].Text);
    }

    [Fact]
    public void Build_with_no_task_says_so_and_with_nothing_else_is_just_that_row()
    {
        var rows = Build(null, Agent, Names, [], null);
        var r = Assert.Single(rows);
        Assert.Equal((TaskKey, "No task yet."), (r.Key, r.Text));
    }

    [Fact]
    public void The_task_line_degrades_with_what_is_known()
    {
        Assert.Equal("Task", TaskLine(new TaskRef("t", "", "", "")));
        Assert.Equal("CB-1", TaskLine(new TaskRef("t", "CB-1", "", "")));
        Assert.Equal("Only a title · done", TaskLine(new TaskRef("t", "", "Only a title", "done")));
    }

    [Fact]
    public void A_comment_that_carries_the_runs_answer_replaces_the_answer_row_and_the_real_pair_shows_one_bubble()
    {
        var act = Fold(Activity.Empty("run-1"), Fx("run-events.json"));
        var comments = ParseComments(Fx("comments-user-then-agent.json"));
        var rows = Build(new TaskRef("issue-1", "ORB-115", "Say hello", "done"), "agent-1", Names, comments, act);
        Assert.Equal(1, rows.Count(r => r.Text == "17 times 3 is 51."));
        var answer = rows.Single(r => r.Text == "17 times 3 is 51.");
        Assert.StartsWith("c:", answer.Key);
        Assert.Equal(AnswerKey("run-1"), answer.Replaces);
    }

    [Fact]
    public void A_comment_by_the_run_id_of_another_run_or_a_person_does_not_take_the_answer()
    {
        var act = Fold(Activity.Empty("r"), Body(Ev(1, "run.result.proposed", new { summary = "Mine." })));
        var rows = Build(null, Agent, Names, [C("c1", "Mine.", agent: Agent, run: "other-run"), C("c2", "Mine.", user: "u", run: "r")], act);
        Assert.Contains(rows, r => r.Key == AnswerKey("r"));
        Assert.All(rows.Where(r => r.Key.StartsWith("c:")), r => Assert.Null(r.Replaces));
    }

    [Fact]
    public void A_system_notice_and_an_unknown_author_type_are_quiet_system_lines_never_a_bubble()
    {
        // the shape of the measured notice: authorType "system", no agent, no user, no run
        var parsed = ParseComments("[{\"id\":\"s\",\"body\":\"Paperclip cannot safely continue\",\"authorType\":\"system\",\"createdAt\":\"2026-11-24T04:17:26Z\"},"
            + "{\"id\":\"x\",\"body\":\"from a new kind\",\"authorType\":\"bot\",\"authorUserId\":\"u\",\"createdAt\":\"2026-11-24T04:17:27Z\"}]");
        Assert.Equal("system", parsed[0].AuthorType);
        var rows = Build(null, Agent, Names, parsed, null);
        Assert.Equal([ChatRole.System, ChatRole.System, ChatRole.System], rows.Select(r => r.Role));
        Assert.Equal("Paperclip cannot safely continue", rows[1].Text);
        Assert.Equal("from a new kind", rows[2].Text);
    }

    [Fact]
    public void Measured_a_user_comment_mid_run_then_the_runs_one_final_comment_shows_exactly_one_answer_bubble()
    {
        // The B3 shape: the person's comment while the run was live, then the run's single final comment,
        // carrying the run id, which answers both. The run has ended.
        var ended = Fold(Fold(Activity.Empty("r"), Body(
                Ev(1, "item.completed", new { kind = "agentMessage", text = "It printed the date.\n\n2 plus 2 = 4.", channel = "final" }, "m"))),
            Body(Ev(2, "run.terminal", new { })));
        var rows = Build(null, Agent, Names, [
            C("u1", "While you are at it: what is 2 plus 2?", user: "user-1", sec: 5),
            C("a1", "It printed the date.\n\n2 plus 2 = 4.", agent: Agent, run: "r", sec: 20)], ended);
        Assert.Equal(1, rows.Count(r => r.Text.Contains("2 plus 2 = 4")));
        Assert.Equal(["task", "c:u1", "c:a1"], rows.Select(r => r.Key));
        Assert.DoesNotContain(rows, r => r.Key == AnswerKey("r"));
    }

    [Fact]
    public void The_comment_read_before_run_terminal_is_folded_still_shows_one_bubble_with_the_answer_text()
    {
        // QA F1. The comment lands ~1.6 s after run.terminal, but a comments read can arrive before the events
        // read that folds it. Order: answer row shown, comment (same sentence) read first, terminal second.
        var live = Fold(Activity.Empty("r"), Body(Ev(1, "item.completed", new { kind = "agentMessage", text = "It is 51.", channel = "final" }, "m")));
        var comment = C("a1", "It is 51.", agent: Agent, run: "r", sec: 20);

        var shown = Merge([], Build(null, Agent, Names, [], live));                    // answer row on screen
        // comment arrives, run not yet seen ended: it says the answer, so it takes the row at once
        shown = Merge(shown, Build(null, Agent, Names, [comment], live));
        Assert.Equal(1, shown.Count(r => r.Text == "It is 51."));
        var ended = Fold(live, Body(Ev(2, "run.terminal", new { })));
        shown = Merge(shown, Build(null, Agent, Names, [comment], ended));
        Assert.Equal(1, shown.Count(r => r.Text == "It is 51."));
        Assert.Equal(["task", "c:a1"], shown.Select(r => r.Key));
    }

    [Fact]
    public void The_other_order_terminal_folded_first_then_the_comment_ends_in_the_same_single_row()
    {
        var live = Fold(Activity.Empty("r"), Body(Ev(1, "item.completed", new { kind = "agentMessage", text = "It is 51.", channel = "final" }, "m")));
        var ended = Fold(live, Body(Ev(2, "run.terminal", new { })));
        var comment = C("a1", "It is 51.", agent: Agent, run: "r", sec: 20);
        var shown = Merge([], Build(null, Agent, Names, [], ended));                 // terminal first: answer row only
        shown = Merge(shown, Build(null, Agent, Names, [comment], ended));            // then the comment
        Assert.Equal(["task", "c:a1"], shown.Select(r => r.Key));
        Assert.Equal(1, shown.Count(r => r.Text == "It is 51."));
    }

    [Fact]
    public void A_comment_shown_plain_and_later_learned_to_carry_the_answer_drops_the_answer_row()
    {
        // Different wording (the comment is longer than the final message), so no early take: both show until the
        // end is seen, and then the answer row must go.
        var live = Fold(Activity.Empty("r"), Body(Ev(1, "item.completed", new { kind = "agentMessage", text = "Done.", channel = "final" }, "m")));
        var comment = C("a1", "Done. Details follow.", agent: Agent, run: "r", sec: 20);
        var shown = Merge(Merge([], Build(null, Agent, Names, [], live)), Build(null, Agent, Names, [comment], live));
        Assert.Contains(shown, r => r.Key == AnswerKey("r"));
        Assert.Contains(shown, r => r.Key == "c:a1");
        var ended = Fold(live, Body(Ev(2, "run.terminal", new { })));
        shown = Merge(shown, Build(null, Agent, Names, [comment], ended));
        Assert.Equal(["task", "c:a1"], shown.Select(r => r.Key));
        Assert.Equal("Done. Details follow.", shown[1].Text);
    }

    [Fact]
    public void A_comment_replaces_the_answer_only_once_the_run_has_ended_and_only_if_it_is_the_runs_last()
    {
        var spoke = Body(Ev(1, "run.result.proposed", new { summary = "Answer." }));
        var live = Fold(Activity.Empty("r"), spoke);
        var ended = Fold(live, Body(Ev(2, "run.terminal", new { })));
        Assert.False(live.Ended);
        Assert.True(ended.Ended);
        var last = C("last", "Answer.", agent: Agent, run: "r", sec: 20);
        // run not seen ended: a comment carrying its id that does NOT say the answer is not yet its answer
        var other = C("other", "Something else entirely.", agent: Agent, run: "r", sec: 20);
        Assert.Contains(Build(null, Agent, Names, [other], live), r => r.Key == AnswerKey("r"));
        // one that says the same sentence takes it at once (the comment can be read before run.terminal is folded)
        Assert.DoesNotContain(Build(null, Agent, Names, [last], live), r => r.Key == AnswerKey("r"));
        // ended: the last comment of the run takes the place
        var done = Build(null, Agent, Names, [last], ended);
        Assert.DoesNotContain(done, r => r.Key == AnswerKey("r"));
        Assert.Equal(AnswerKey("r"), done.Single(r => r.Key == "c:last").Replaces);
        // an earlier (interim) comment of the same run does not; the last one still does
        var interim = C("interim", "Working on it.", agent: Agent, run: "r", sec: 5);
        var both = Build(null, Agent, Names, [interim, last], ended);
        Assert.Null(both.Single(r => r.Key == "c:interim").Replaces);
        Assert.Equal(AnswerKey("r"), both.Single(r => r.Key == "c:last").Replaces);
    }

    [Fact]
    public void Every_text_is_cut_at_8000_bytes_on_a_character_boundary()
    {
        var big = new string('a', 7990) + "éééééé";       // 2-byte letters straddle the limit
        var rows = Build(null, Agent, Names, [C("c1", big, user: "u")], null);
        var cut = rows[1].Text;
        Assert.True(Encoding.UTF8.GetByteCount(cut) <= TextLimitBytes);
        Assert.EndsWith("…", cut);
        Assert.Equal("short", Cap("short"));
        // an emoji (a surrogate pair) is never split
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 3000));
        Assert.True(Encoding.UTF8.GetByteCount(Cap(emoji)) <= TextLimitBytes);
        Assert.DoesNotContain('�', Cap(emoji));
    }

    // ---- Merge ----------------------------------------------------------------

    private static Row R(string key, string text, int sec, ChatRole role = ChatRole.Assistant, bool err = false, string? replaces = null) =>
        new(key, role, text, T0.AddSeconds(sec), err, replaces);

    [Fact]
    public void Merge_appends_what_it_has_not_seen_in_time_order_among_the_new()
    {
        var shown = new[] { R("task", "t", 0), R("c:1", "a", 1) };
        var merged = Merge(shown, [R("task", "t", 0), R("c:1", "a", 1), R("c:3", "later", 9), R("c:2", "earlier", 5)]);
        Assert.Equal(["task", "c:1", "c:2", "c:3"], merged.Select(r => r.Key));
    }

    [Fact]
    public void Merge_updates_a_row_in_place_when_its_text_or_error_changed_and_keeps_its_place_and_time()
    {
        var shown = new[] { R("a", "one", 1), R("b", "two", 2) };
        var merged = Merge(shown, [R("a", "ONE", 99, err: true), R("b", "two", 2)]);
        Assert.Equal(["a", "b"], merged.Select(r => r.Key));
        Assert.Equal("ONE", merged[0].Text);
        Assert.True(merged[0].Error);
        Assert.Equal(shown[0].At, merged[0].At);
    }

    [Fact]
    public void Merge_a_late_row_with_an_early_time_still_appends_at_the_end()
    {
        var merged = Merge([R("a", "x", 10)], [R("a", "x", 10), R("early", "y", 1)]);
        Assert.Equal(["a", "early"], merged.Select(r => r.Key));
    }

    [Fact]
    public void Merge_the_comment_that_carries_a_runs_answer_takes_the_answer_rows_place_under_its_own_key()
    {
        var shown = new[] { R("task", "t", 0), R("r:1:answer", "51.", 5), R("r:1:t:x", "Bash", 6) };
        var merged = Merge(shown, [R("c:9", "51.", 5, replaces: "r:1:answer")]);
        Assert.Equal(["task", "c:9", "r:1:t:x"], merged.Select(r => r.Key));
        Assert.Null(merged[1].Replaces);
        // read again: the comment is now known by key, and nothing doubles
        Assert.Equal(merged.Select(r => r.Key), Merge(merged, [R("c:9", "51.", 5, replaces: "r:1:answer")]).Select(r => r.Key));
    }

    [Fact]
    public void Merge_a_replaces_naming_nothing_shown_appends_and_clears_the_marker()
    {
        var merged = Merge([R("a", "x", 1)], [R("c:1", "y", 2, replaces: "r:gone:answer")]);
        Assert.Equal(["a", "c:1"], merged.Select(r => r.Key));
        Assert.Null(merged[1].Replaces);
    }

    [Fact]
    public void Merge_adopts_a_local_user_bubble_by_text_and_leaves_other_users_text_alone()
    {
        var shown = new[] { R("u:1", "hello", 1, ChatRole.User), R("u:2", "other", 2, ChatRole.User) };
        var merged = Merge(shown, [R("c:7", "hello", 1, ChatRole.User)]);
        Assert.Equal(["c:7", "u:2"], merged.Select(r => r.Key));
        // an Assistant row with the same text never adopts a user bubble
        Assert.Equal(3, Merge(shown, [R("c:8", "other", 3, ChatRole.Assistant)]).Count);
    }

    [Fact]
    public void Merge_with_an_empty_side_is_the_other_side()
    {
        var rows = new[] { R("a", "x", 1) };
        Assert.Equal(rows, Merge(rows, []));
        Assert.Equal(rows, Merge([], rows));
        Assert.Empty(Merge([], []));
    }

    [Fact]
    public void Merge_holds_the_newest_200_and_always_keeps_the_task_row()
    {
        var shown = Enumerable.Range(0, 250).Select(i => R(i == 0 ? "task" : "c:" + i, "t" + i, i)).ToList();
        var merged = Merge([R("task", "t0", 0)], shown);
        Assert.Equal(PaperclipChat.RowCap, merged.Count);
        Assert.Equal("task", merged[0].Key);
        Assert.Equal("c:249", merged[^1].Key);
        Assert.Equal("c:51", merged[1].Key);
        // with no task row the newest 200 simply stand
        var noTask = Merge([], Enumerable.Range(1, 250).Select(i => R("c:" + i, "x", i)).ToList());
        Assert.Equal(PaperclipChat.RowCap, noTask.Count);
        Assert.Equal("c:51", noTask[0].Key);
    }

    // ---- Hint -----------------------------------------------------------------

    private static string H(string? status = "idle", bool hasTask = true, bool live = false, PaperclipLink link = PaperclipLink.Connected,
        DateTime? since = null, DateTime? now = null) => Hint("Ines", status, hasTask, live, link, since, now ?? T0);

    [Fact]
    public void Hint_one_case_per_state()
    {
        Assert.Equal("Message Ines. It wakes to read it.", H());
        Assert.Equal("Message Ines. This opens a new task.", H(hasTask: false));
        Assert.Equal("Ines is working. Your message goes to this run.", H(live: true));
        Assert.Equal("Ines is paused in Paperclip. It reads this when resumed.", H("paused"));
        Assert.Equal("Reconnect to Paperclip", H(link: PaperclipLink.NeedsReconnect));
        Assert.Equal("Waking Ines…", H(since: T0));
    }

    [Fact]
    public void Hint_the_wake_grace_edge_on_both_sides_and_a_live_run_ends_the_wait()
    {
        Assert.Equal("Waking Ines…", H(since: T0, now: T0.AddSeconds(29.9)));
        Assert.Equal("Ines hasn't woken yet. Wake it from the orb's menu.", H(since: T0, now: T0.AddSeconds(30)));
        Assert.StartsWith("Ines is working", H(since: T0, live: true, now: T0.AddSeconds(60)));
        // a refused key and a pause outrank a pending wait
        Assert.Equal("Reconnect to Paperclip", H(link: PaperclipLink.NeedsReconnect, since: T0));
        Assert.StartsWith("Ines is paused", H("paused", since: T0));
    }
}

// CB-443: the two message calls against a scripted IPaperclipApi (the wire is PaperclipChatWireTests).
public class PaperclipMessageActionsTests
{
    private static (FakePaperclip Api, PaperclipActions A, Func<int> Polls) Rig()
    {
        var api = new FakePaperclip(); var polls = 0;
        return (api, new PaperclipActions(api, "co1", () => polls++), () => polls);
    }

    [Fact]
    public void The_title_is_the_first_line_cut_to_80_and_a_leading_blank_line_is_skipped()
    {
        Assert.Equal("Fix the bug", PaperclipActions.TitleFor("Fix the bug\nwith more detail"));
        Assert.Equal("Fix the bug", PaperclipActions.TitleFor("\n\n  Fix the bug  \nmore"));
        var title = PaperclipActions.TitleFor(new string('x', 120) + " tail");
        Assert.Equal(80, title.Length);
        Assert.Equal(new string('x', 80), title);
        Assert.Equal("ab", PaperclipActions.TitleFor("ab" + new string(' ', 200) + "cd")[..2]);
    }

    [Fact]
    public void An_emoji_at_the_title_limit_goes_whole_or_not_at_all()
    {
        var at79 = new string('x', 79) + "\U0001F600tail";              // the pair would straddle chars 79 and 80
        var t = PaperclipActions.TitleFor(at79);
        Assert.Equal(new string('x', 79), t);
        Assert.DoesNotContain(t, c => char.IsSurrogate(c));
        var at78 = new string('x', 78) + "\U0001F600tail";              // the pair fits exactly in 80
        Assert.Equal(new string('x', 78) + "\U0001F600", PaperclipActions.TitleFor(at78));
    }

    [Fact]
    public async Task The_comment_id_is_read_from_the_response_and_a_pollnow_follows_a_send_only()
    {
        var (api, a, polls) = Rig();
        api.Posts["/api/issues/i%201/comments"] = new(PaperclipHttpKind.Ok, 201, "{\"id\":\"cm-1\"}");
        var sent = await a.CommentAsync("i 1", "hello", default);
        Assert.Equal("cm-1", sent.CommentId);
        Assert.Equal("{\"body\":\"hello\"}", api.LastPostBody);
        Assert.Equal(1, polls());
        var gone = await a.CommentAsync("nope", "hello", default);      // nothing scripted: 404
        Assert.Equal((PaperclipActionResult.Refused, PaperclipActions.TaskGoneWhy), (gone.Outcome.Result, gone.Outcome.Why));
        Assert.Equal(1, polls());
    }

    [Fact]
    public async Task Opening_a_task_reads_the_issue_id_and_a_missing_company_agent_is_gone()
    {
        var (api, a, polls) = Rig();
        api.Posts["/api/companies/co1/issues"] = new(PaperclipHttpKind.Ok, 201, "{\"id\":\"is-5\"}");
        var sent = await a.OpenTaskAsync("ag-1", "Do the thing", default);
        Assert.Equal("is-5", sent.IssueId);
        Assert.Contains("\"assigneeAgentId\":\"ag-1\"", api.LastPostBody);
        Assert.Equal(1, polls());
        api.Posts.Clear();
        var gone = await a.OpenTaskAsync("ag-1", "Do the thing", default);
        Assert.Equal(PaperclipActions.AgentGoneWhy, gone.Outcome.Why);
        Assert.Equal(PaperclipActionResult.Refused, (await a.OpenTaskAsync("ag-1", " ", default)).Outcome.Result);
    }
}
