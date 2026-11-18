using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-356 everywhere a compaction leaves the parser: ChatTranscript's own edges
// beyond the captures, the mirror's "c" field, and the phone's window line.
// The real rows are Captures/cb356, read through Cb356Captures.
public class CompactionWireTests
{
    private static readonly string[] Adjacent = Cb356Captures.Lines("auto-adjacent");
    private static string BoundaryRow => Adjacent.Single(l => l.Contains("\"compact_boundary\""));
    private static string SummaryRow => Adjacent.Single(l => l.Contains("\"isCompactSummary\":true"));

    private static CompactionNote MergedNote =>
        Cb356Captures.Map("auto-adjacent").Single(r => r.Turn.Compaction is not null).Turn.Compaction!;

    private static string SummaryWithContent(string contentJson) =>
        "{\"type\":\"user\",\"uuid\":\"s1\",\"isCompactSummary\":true,\"timestamp\":\"2026-10-29T02:16:53Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + contentJson + "}}";

    // ---- ChatTranscript ---------------------------------------------------

    [Fact]
    public void IsInteresting_passes_a_boundary_whatever_the_key_order()
    {
        Assert.True(ChatTranscript.IsInteresting(BoundaryRow));
        Assert.True(ChatTranscript.IsInteresting("{\"subtype\":\"compact_boundary\",\"type\":\"system\"}"));
        Assert.True(ChatTranscript.IsInteresting(Cb356Captures.Lines("manual").Single(l => l.Contains("compact_boundary"))));
    }

    [Fact]
    public void IsInteresting_still_rejects_other_system_rows()
    {
        Assert.False(ChatTranscript.IsInteresting("{\"type\":\"system\",\"subtype\":\"turn_duration\",\"durationMs\":5}"));
        Assert.False(ChatTranscript.IsInteresting("{\"type\":\"system\",\"subtype\":\"stop_hook_summary\"}"));
    }

    [Fact]
    public void A_pair_mapped_in_two_calls_and_absorbed_equals_the_one_call_result()
    {
        var one = Assert.Single(ChatTranscript.Map(new[] { BoundaryRow, SummaryRow }));

        var first = Assert.Single(ChatTranscript.Map(new[] { BoundaryRow }));
        var second = Assert.Single(ChatTranscript.Map(new[] { SummaryRow }));
        Assert.True(CompactionNote.TryAbsorb(first.Turn, second.Turn));

        Assert.Equal(one.Uuid, first.Uuid);
        Assert.Equal(one.Turn.Compaction, first.Turn.Compaction);
        Assert.Equal(one.Turn.Text, first.Turn.Text);
        Assert.Equal(one.Turn.Role, first.Turn.Role);
    }

    [Fact]
    public void A_summary_written_as_blocks_joins_its_text_blocks()
    {
        var row = SummaryWithContent(
            "[{\"type\":\"text\",\"text\":\"## One\"},{\"type\":\"image\",\"source\":{}},{\"type\":\"text\",\"text\":\"## Two\"}]");

        var turn = Assert.Single(ChatTranscript.Map(new[] { row })).Turn;

        Assert.Equal("## One\n\n## Two", turn.Compaction!.Summary);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[{\"type\":\"image\",\"source\":{}}]")]
    public void A_summary_with_no_text_is_a_plain_row_with_nothing_to_open(string content)
    {
        var turn = Assert.Single(ChatTranscript.Map(new[] { SummaryWithContent(content) })).Turn;

        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Equal(new CompactionNote(null, null, null, null, "", false), turn.Compaction);
        Assert.Equal("Conversation compacted", turn.Text);
    }

    [Fact]
    public void A_summary_marked_isMeta_is_still_a_compaction()
    {
        var meta = SummaryRow.Replace("\"isCompactSummary\":true", "\"isCompactSummary\":true,\"isMeta\":true");

        var turn = Assert.Single(ChatTranscript.Map(new[] { meta })).Turn;

        Assert.NotNull(turn.Compaction);
    }

    [Fact]
    public void A_summary_row_with_no_message_produces_nothing()
    {
        Assert.Empty(ChatTranscript.Map(new[] { "{\"type\":\"user\",\"uuid\":\"s1\",\"isCompactSummary\":true}" }));
    }

    // ---- the mirror -------------------------------------------------------

    [Fact]
    public void A_compaction_turn_round_trips_the_mirror()
    {
        var sent = MirrorProtocol.TurnsFrom(Adjacent, MirrorProtocol.CliClaudeCode);
        var back = MirrorProtocol.DecodeTurns(MirrorProtocol.EncodeTurns(sent))!;
        var adopted = MirrorProtocol.AdoptTurns(back);

        var row = Assert.Single(adopted, r => r.Turn.Compaction is not null);
        Assert.Equal(MergedNote, row.Turn.Compaction);
        Assert.Equal(MergedNote.Line, row.Turn.Text);
        Assert.Equal(ChatRole.System, row.Turn.Role);
        Assert.True(row.Turn.IsComplete);
        Assert.Equal(Cb356Captures.U(6), row.Key);
        Assert.Equal(3, adopted.Count);
    }

    [Fact]
    public void The_c_key_is_written_only_on_a_compaction_turn()
    {
        var sent = MirrorProtocol.TurnsFrom(Adjacent, MirrorProtocol.CliClaudeCode);
        var json = JsonSerializer.Serialize(sent, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        var turns = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();

        Assert.Equal(3, turns.Count);
        Assert.False(turns[0].TryGetProperty("c", out _));
        Assert.False(turns[2].TryGetProperty("c", out _));
        var c = turns[1].GetProperty("c");
        Assert.Equal("auto", c.GetProperty("g").GetString());
        Assert.Equal(245582, c.GetProperty("b").GetInt64());
        Assert.Equal(20628, c.GetProperty("o").GetInt64());
        Assert.True(c.GetProperty("h").GetBoolean());
    }

    [Fact]
    public void A_frame_with_no_c_decodes_as_before()
    {
        var back = Assert.Single(MirrorProtocol.DecodeTurns(MirrorProtocol.EncodeTurns(
            new[] { new MirrorProtocol.MirrorTurn("a", "hi", "1", 5) }))!);

        Assert.Null(back.Compaction);
        Assert.Null(Assert.Single(MirrorProtocol.AdoptTurns(new[] { back })).Turn.Compaction);
    }

    // An older sender sends the summary as a plain user turn. The receiver
    // cannot tell that from a user quoting the sentence, so it stays a bubble
    // until the far machine is upgraded — the plan's named gap.
    [Fact]
    public void An_older_senders_raw_summary_stays_a_user_turn()
    {
        var raw = JsonDocument.Parse(SummaryRow).RootElement.GetProperty("message").GetProperty("content").GetString()!;

        var turn = Assert.Single(MirrorProtocol.AdoptTurns(new[] { new MirrorProtocol.MirrorTurn("u", raw, "s1", 5) })).Turn;

        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Null(turn.Compaction);
        Assert.Equal(raw, turn.Text);
    }

    [Fact]
    public void The_receiver_rebuilds_the_line_and_refuses_counts_its_own_parser_would()
    {
        var frame = new MirrorProtocol.MirrorTurn("s", "anything the far side wrote", "b1", 5,
            Compaction: new MirrorProtocol.MirrorCompaction("manual", -1, 20628, -3, null, true));

        var turn = Assert.Single(MirrorProtocol.AdoptTurns(new[] { frame })).Turn;

        Assert.Equal(new CompactionNote("manual", null, 20628, null, "", true), turn.Compaction);
        Assert.Equal("Conversation compacted manually", turn.Text);
    }

    private sealed record OldTurn(
        [property: System.Text.Json.Serialization.JsonPropertyName("r")] string R,
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] string T);

    [Fact]
    public void An_older_receiver_ignores_c_and_draws_the_line_as_a_system_turn()
    {
        var sent = MirrorProtocol.TurnsFrom(Adjacent, MirrorProtocol.CliClaudeCode);
        var json = JsonSerializer.Serialize(sent);

        var old = JsonSerializer.Deserialize<OldTurn[]>(json)!;

        Assert.Equal("s", old[1].R);
        Assert.Equal("Conversation compacted automatically · 246k → 21k tokens", old[1].T);
    }

    // ---- the phone's window line -----------------------------------------

    private static ChatTurn Compaction(CompactionNote note) => new()
    {
        Role = ChatRole.System, Text = note.Line, IsComplete = true, Compaction = note,
        At = DateTimeOffset.FromUnixTimeSeconds(1794124800)
    };

    private static readonly CompactionNote BoundaryOnly = new("auto", 245582, 20628, 12751, "", true);

    [Fact]
    public void A_compaction_line_carries_kind_and_body_after_the_existing_keys()
    {
        var line = TranscriptWatch.From(Compaction(BoundaryOnly.WithSummaryFrom(CompactionNote.FromSummary("## 1. Primary"))));

        Assert.Equal(
            """{"role":"system","text":"Conversation compacted automatically · 246k → 21k tokens","at":1794124800,"kind":"compaction","body":"## 1. Primary"}""",
            TranscriptWatch.LineJson(line));
    }

    [Fact]
    public void An_orphan_boundary_line_has_a_kind_and_no_body()
    {
        var line = TranscriptWatch.From(Compaction(BoundaryOnly));

        Assert.Equal(("compaction", null, false), (line.Kind, line.Body, line.BodyCut));
        Assert.Equal(
            """{"role":"system","text":"Conversation compacted automatically · 246k → 21k tokens","at":1794124800,"kind":"compaction"}""",
            TranscriptWatch.LineJson(line));
    }

    [Fact]
    public void Any_other_line_has_none_of_the_new_keys()
    {
        var turn = new ChatTurn { Role = ChatRole.User, Text = "hello", At = DateTimeOffset.FromUnixTimeSeconds(5) };

        Assert.Equal("""{"role":"user","text":"hello","at":5}""", TranscriptWatch.LineJson(TranscriptWatch.From(turn)));
    }

    [Fact]
    public void A_long_summary_is_cut_to_the_line_cap_and_says_so()
    {
        var body = string.Concat(Enumerable.Repeat("é", 10_000));   // 20,000 UTF-8 bytes

        var line = TranscriptWatch.From(Compaction(BoundaryOnly with { Summary = body }));

        Assert.True(line.BodyCut);
        Assert.True(Encoding.UTF8.GetByteCount(line.Body!) <= TranscriptWatch.MaxLineBytes);
        Assert.EndsWith("…", line.Body);
        Assert.False(line.Cut);
        Assert.Contains("\"body_cut\":true", TranscriptWatch.LineJson(line));
    }

    [Fact]
    public void FitAlone_cuts_the_body_before_the_text()
    {
        var line = TranscriptWatch.From(Compaction(BoundaryOnly with { Summary = new string('\u0001', 8000) }));

        var fitted = TranscriptWatch.FitAlone(line, "s", 1, false, false);

        Assert.Equal(line.Text, fitted.Text);
        Assert.False(fitted.Cut);
        Assert.True(fitted.BodyCut);
        Assert.True(fitted.Body!.Length < 8000);
        Assert.True(Encoding.UTF8.GetByteCount(TranscriptWatch.Window("s", long.MaxValue, 1, false, false,
            new[] { TranscriptWatch.LineJson(fitted) })) <= TranscriptWatch.MaxWindowBytes);
    }

    [Fact]
    public void FitAlone_empties_the_body_and_then_cuts_the_text_when_the_text_alone_is_too_big()
    {
        var huge = new TranscriptWatch.WireLine("system", new string('\u0001', 8000), 1, false, false,
            "compaction", new string('\u0001', 8000), false);

        var fitted = TranscriptWatch.FitAlone(huge, "s", 1, false, false);

        Assert.Equal("…", fitted.Body);
        Assert.True(fitted.BodyCut);
        Assert.True(fitted.Cut);
        Assert.True(Encoding.UTF8.GetByteCount(TranscriptWatch.Window("s", long.MaxValue, 1, false, false,
            new[] { TranscriptWatch.LineJson(fitted) })) <= TranscriptWatch.MaxWindowBytes);
    }

    [Fact]
    public void FitAlone_leaves_a_line_that_fits_alone()
    {
        var line = TranscriptWatch.From(Compaction(MergedNote));

        Assert.Equal(line, TranscriptWatch.FitAlone(line, "s", 1, false, false));
    }

    [Fact]
    public void Same_compares_kind_and_body_too()
    {
        var boundary = TranscriptWatch.From(Compaction(BoundaryOnly));
        var merged = TranscriptWatch.From(Compaction(BoundaryOnly.WithSummaryFrom(CompactionNote.FromSummary("body"))));
        var plain = boundary with { Kind = null };

        Assert.Equal(boundary.Text, merged.Text);
        Assert.False(TranscriptWatch.Same(boundary, merged));
        Assert.False(TranscriptWatch.Same(boundary, plain));
        Assert.True(TranscriptWatch.Same(merged, merged with { At = 99 }));
    }

    [Fact]
    public void The_summary_arriving_later_restates_the_line()
    {
        var boundary = TranscriptWatch.From(Compaction(BoundaryOnly));
        var merged = TranscriptWatch.From(Compaction(BoundaryOnly.WithSummaryFrom(CompactionNote.FromSummary("body"))));
        var first = TranscriptWatch.Line("user", "hello", 1);

        var change = TranscriptWatch.Diff(2, first, new[] { first, merged }, boundary);

        Assert.Equal(TranscriptWatch.Change.Restate, change);
    }
}
