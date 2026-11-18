using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-356: CompactionNote, one case per rule in docs/cb-356-plan.md section 2.
// The real rows are covered by Cb356Captures (through TranscriptSuiteTests);
// these are the edges no real row has been seen at.
public class CompactionNoteTests
{
    private static CompactionNote Boundary(string? trigger = "auto", long? pre = 245582, long? post = 20628) =>
        new(trigger, pre, post, 12751, "", HasBoundary: true);

    private static CompactionNote SummaryNote(string summary = "## Body") =>
        new(null, null, null, null, summary, HasBoundary: false);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- Tokens ----------------------------------------------------------

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(850L, "850")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1k")]
    [InlineData(1499L, "1k")]
    [InlineData(1500L, "2k")]
    [InlineData(20628L, "21k")]
    [InlineData(245582L, "246k")]
    [InlineData(978379L, "978k")]
    [InlineData(999499L, "999k")]
    [InlineData(999500L, "1.0M")]
    [InlineData(999600L, "1.0M")]
    [InlineData(1234567L, "1.2M")]
    public void Tokens_reads_as_the_plan_says(long n, string expected) =>
        Assert.Equal(expected, CompactionNote.Tokens(n));

    [Fact]
    public void Tokens_ignores_the_current_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.2M", CompactionNote.Tokens(1234567));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    // ---- Label, Detail, Line ---------------------------------------------

    [Theory]
    [InlineData("manual", "Conversation compacted manually")]
    [InlineData("auto", "Conversation compacted automatically")]
    [InlineData("AUTO", "Conversation compacted automatically")]
    [InlineData("idle", "Conversation compacted while idle")]
    [InlineData("scheduled", "Conversation compacted")]
    [InlineData(null, "Conversation compacted")]
    public void Label_by_trigger(string? trigger, string expected) =>
        Assert.Equal(expected, Boundary(trigger).Label);

    [Fact]
    public void Detail_with_both_counts_before_only_after_only_and_neither()
    {
        Assert.Equal("246k → 21k tokens", Boundary(pre: 245582, post: 20628).Detail);
        Assert.Equal("246k tokens", Boundary(pre: 245582, post: null).Detail);
        Assert.Equal("", Boundary(pre: null, post: 20628).Detail);
        Assert.Equal("", Boundary(pre: null, post: null).Detail);
    }

    [Fact]
    public void Line_is_label_then_detail_or_label_alone()
    {
        Assert.Equal("Conversation compacted automatically · 246k → 21k tokens", Boundary().Line);
        Assert.Equal("Conversation compacted", SummaryNote().Line);
    }

    [Fact]
    public void HasSummary_follows_the_summary_text()
    {
        Assert.False(Boundary().HasSummary);
        Assert.True(SummaryNote().HasSummary);
    }

    // ---- FromBoundary ----------------------------------------------------

    [Fact]
    public void FromBoundary_reads_camelCase_metadata()
    {
        var n = CompactionNote.FromBoundary(Json(
            """{"type":"system","subtype":"compact_boundary","compactMetadata":{"trigger":"manual","preTokens":116684,"postTokens":22786,"durationMs":28501}}"""));
        Assert.Equal(new CompactionNote("manual", 116684, 22786, 28501, "", true), n);
    }

    [Fact]
    public void FromBoundary_reads_snake_case_metadata_as_the_fallback()
    {
        var n = CompactionNote.FromBoundary(Json(
            """{"type":"system","compact_metadata":{"trigger":"auto","pre_tokens":245582,"post_tokens":20628,"duration_ms":12751}}"""));
        Assert.Equal(new CompactionNote("auto", 245582, 20628, 12751, "", true), n);
    }

    [Fact]
    public void FromBoundary_prefers_camelCase_when_both_are_present()
    {
        var n = CompactionNote.FromBoundary(Json(
            """{"compact_metadata":{"trigger":"auto","pre_tokens":1},"compactMetadata":{"trigger":"manual","preTokens":2000}}"""));
        Assert.Equal("manual", n.Trigger);
        Assert.Equal(2000, n.PreTokens);
    }

    [Theory]
    [InlineData("""{"type":"system"}""")]
    [InlineData("""{"compactMetadata":[1,2]}""")]
    [InlineData("""{"compactMetadata":"auto","compact_metadata":null}""")]
    [InlineData("""[1,2]""")]
    public void FromBoundary_without_usable_metadata_is_still_a_plain_boundary(string json)
    {
        var n = CompactionNote.FromBoundary(Json(json));
        Assert.Equal(new CompactionNote(null, null, null, null, "", true), n);
        Assert.Equal("Conversation compacted", n.Line);
    }

    [Fact]
    public void FromBoundary_drops_a_count_that_is_not_a_whole_non_negative_number()
    {
        var n = CompactionNote.FromBoundary(Json(
            """{"compactMetadata":{"trigger":7,"preTokens":"245582","postTokens":-5,"durationMs":1.5}}"""));
        Assert.Equal(new CompactionNote(null, null, null, null, "", true), n);
    }

    // ---- Strip -----------------------------------------------------------

    private const string Tail =
        "If you need specific details from before compaction (like exact code snippets), read the full transcript at: /x.jsonl\n"
        + "Continue the conversation from where it left off.";

    [Fact]
    public void Strip_removes_every_reminder_block_across_lines()
    {
        var s = CompactionNote.Strip("one\n<system-reminder>\na\n</system-reminder>\ntwo\n<system-reminder>b</system-reminder>three");
        Assert.Equal("one\n\ntwo\nthree", s);
    }

    [Fact]
    public void Strip_leaves_an_unclosed_reminder_alone()
    {
        Assert.Equal("body\n<system-reminder>\nnever closed", CompactionNote.Strip("body\n<system-reminder>\nnever closed"));
    }

    [Fact]
    public void Strip_removes_the_artifact_prelude_through_its_sentence()
    {
        var s = CompactionNote.Strip(
            CompactionNote.ArtifactPrelude + "\nA sentence about artifacts.\n" + CompactionNote.Opening + "\n\nbody");
        Assert.Equal("body", s);
    }

    [Fact]
    public void Strip_keeps_an_artifact_prelude_the_opening_does_not_follow()
    {
        var text = CompactionNote.ArtifactPrelude + "\nSomething unfamiliar.\n\nbody";
        Assert.Equal(text, CompactionNote.Strip(text));
    }

    [Fact]
    public void Strip_removes_the_opening_only_at_the_start()
    {
        Assert.Equal("body", CompactionNote.Strip(CompactionNote.Opening + "\n\nbody"));
        Assert.Equal("intro " + CompactionNote.Opening, CompactionNote.Strip("intro " + CompactionNote.Opening));
    }

    [Fact]
    public void Strip_cuts_from_the_last_tail_paragraph_only()
    {
        var text = "A\n\n" + Tail + "\n\nB\n\n" + Tail;
        Assert.Equal("A\n\n" + Tail + "\n\nB", CompactionNote.Strip(text));
    }

    [Fact]
    public void Strip_does_not_cut_at_a_tail_phrase_inside_a_line()
    {
        var text = "A\n" + Tail + " and then If you need specific details from before compaction again";
        Assert.Equal("A", CompactionNote.Strip(text));

        var inline = "Body that says If you need specific details from before compaction mid-line.";
        Assert.Equal(inline, CompactionNote.Strip(inline));
    }

    [Fact]
    public void Strip_cuts_a_tail_at_the_very_start_and_falls_back_to_the_original()
    {
        Assert.Equal(Tail, CompactionNote.Strip(Tail));
    }

    [Fact]
    public void Strip_removes_a_leading_summary_line()
    {
        Assert.Equal("## Heading", CompactionNote.Strip("Summary:\n## Heading"));
        Assert.Equal("## Heading", CompactionNote.Strip("Summary:  \r\n## Heading"));
    }

    [Fact]
    public void Strip_keeps_a_summary_line_that_carries_text()
    {
        Assert.Equal("Summary: inline\nbody", CompactionNote.Strip("Summary: inline\nbody"));
        Assert.Equal("Summary:x", CompactionNote.Strip("Summary:x"));
    }

    [Fact]
    public void Strip_never_returns_nothing()
    {
        Assert.Equal("Summary:", CompactionNote.Strip("  Summary:  "));
        Assert.Equal(CompactionNote.Opening, CompactionNote.Strip(CompactionNote.Opening));
        Assert.Equal("<system-reminder>x</system-reminder>", CompactionNote.Strip("<system-reminder>x</system-reminder>"));
        Assert.Equal("", CompactionNote.Strip("   "));
    }

    [Fact]
    public void Strip_runs_every_step_in_order_on_the_real_shape()
    {
        var real = CompactionNote.Opening + "\n\n<system-reminder>\n<tools-used-before-summary>Bash</tools-used-before-summary>\n</system-reminder>\n"
                   + "Summary:\n## 1. Primary Request\n\nText.\n\n" + Tail + "\n\nThe messages after this summary are verbatim.";
        Assert.Equal("## 1. Primary Request\n\nText.", CompactionNote.Strip(real));
    }

    // ---- FromSummary, WithSummaryFrom, equality ---------------------------

    [Fact]
    public void FromSummary_is_an_orphan_with_the_stripped_text()
    {
        var n = CompactionNote.FromSummary(CompactionNote.Opening + "\n\nSummary:\nbody");
        Assert.Equal(new CompactionNote(null, null, null, null, "body", false), n);
    }

    [Fact]
    public void WithSummaryFrom_keeps_the_boundary_and_takes_the_summary()
    {
        var merged = Boundary().WithSummaryFrom(SummaryNote("## Kept"));
        Assert.Equal(new CompactionNote("auto", 245582, 20628, 12751, "## Kept", true), merged);
    }

    [Fact]
    public void Notes_are_equal_by_value()
    {
        Assert.Equal(Boundary(), Boundary());
        Assert.NotEqual(Boundary(), Boundary("manual"));
    }

    // ---- TryAbsorb, TryAdopt, TryRefresh ---------------------------------

    private static readonly RelayedMessage Relay = new(RelayKind.Peer, null, null, "message", "", "p", "f");

    private static ChatTurn Turn(string kind) => kind switch
    {
        "boundary" => new ChatTurn { Role = ChatRole.System, Text = Boundary().Line, IsComplete = true, Compaction = Boundary() },
        "summary" => new ChatTurn { Role = ChatRole.System, Text = SummaryNote().Line, IsComplete = true, Compaction = SummaryNote() },
        "merged" => new ChatTurn
        {
            Role = ChatRole.System, Text = Boundary().Line, IsComplete = true,
            Compaction = Boundary().WithSummaryFrom(SummaryNote())
        },
        "empty-summary" => new ChatTurn { Role = ChatRole.System, Text = "Conversation compacted", Compaction = SummaryNote("") },
        "relay" => new ChatTurn { Role = ChatRole.System, Text = Relay.Line, IsComplete = true, Relay = Relay },
        "system" => new ChatTurn { Role = ChatRole.System, Text = "note", IsComplete = true },
        _ => new ChatTurn { Role = ChatRole.User, Text = "hello", IsComplete = true },
    };

    private static readonly string[] Kinds = { "boundary", "summary", "merged", "empty-summary", "relay", "system", "user" };

    public static IEnumerable<object[]> Pairs() =>
        from a in Kinds from b in Kinds select new object[] { a, b };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TryAbsorb_merges_only_a_lone_summary_into_a_lone_boundary(string open, string incoming)
    {
        var a = Turn(open);
        var b = Turn(incoming);
        var (aNote, aText, bNote, bText) = (a.Compaction, a.Text, b.Compaction, b.Text);

        var absorbed = CompactionNote.TryAbsorb(a, b);

        Assert.Equal(open == "boundary" && incoming == "summary", absorbed);
        if (absorbed)
        {
            Assert.Equal(Boundary().WithSummaryFrom(SummaryNote()), a.Compaction);
            Assert.Equal(a.Compaction!.Line, a.Text);
        }
        else
        {
            Assert.Equal(aNote, a.Compaction);
            Assert.Equal(aText, a.Text);
        }

        Assert.Equal(bNote, b.Compaction);
        Assert.Equal(bText, b.Text);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TryAdopt_moves_a_lone_boundary_onto_the_shown_lone_summary(string older, string shown)
    {
        var a = Turn(older);
        var b = Turn(shown);
        var (aNote, bNote, bText) = (a.Compaction, b.Compaction, b.Text);

        var adopted = CompactionNote.TryAdopt(a, b);

        Assert.Equal(older == "boundary" && shown == "summary", adopted);
        Assert.Equal(aNote, a.Compaction);
        if (adopted)
        {
            Assert.Equal(Boundary().WithSummaryFrom(SummaryNote()), b.Compaction);
            Assert.Equal("Conversation compacted automatically · 246k → 21k tokens", b.Text);
        }
        else
        {
            Assert.Equal(bNote, b.Compaction);
            Assert.Equal(bText, b.Text);
        }
    }

    [Theory]
    [InlineData("boundary", "summary", true)]
    [InlineData("summary", "boundary", true)]
    [InlineData("boundary", "merged", true)]
    [InlineData("summary", "merged", true)]
    [InlineData("merged", "summary", false)]
    [InlineData("merged", "boundary", false)]
    [InlineData("merged", "merged", false)]
    [InlineData("boundary", "boundary", false)]
    [InlineData("summary", "summary", false)]
    [InlineData("boundary", "relay", false)]
    [InlineData("relay", "summary", false)]
    [InlineData("user", "merged", false)]
    public void TryRefresh_keeps_what_the_shown_turn_knew_and_adds_what_the_read_knows(
        string shown, string reread, bool changes)
    {
        var a = Turn(shown);
        var before = a.Compaction;

        Assert.Equal(changes, CompactionNote.TryRefresh(a, Turn(reread)));

        if (changes)
        {
            Assert.Equal(Boundary().WithSummaryFrom(SummaryNote()), a.Compaction);
            Assert.Equal(a.Compaction!.Line, a.Text);
        }
        else
        {
            Assert.Equal(before, a.Compaction);
        }
    }

    // ---- ChatTurn.Compaction ---------------------------------------------

    [Fact]
    public void ChatTurn_raises_Compaction_when_it_changes_and_not_when_set_to_an_equal_note()
    {
        var turn = new ChatTurn { Role = ChatRole.System };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)turn).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        turn.Compaction = Boundary();
        turn.Compaction = Boundary();
        turn.Compaction = null;

        Assert.Equal(new[] { "Compaction", "Compaction" }, raised);
    }
}
