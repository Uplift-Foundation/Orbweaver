using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-404: the arms of frames-v13's desk helpers the cross-app vectors
/// (RelayV13VectorTests) do not reach on their own.
/// </summary>
public class RelayV13FrameTests
{
    [Theory]
    [InlineData("opus", true)]
    [InlineData("claude-opus-4-1[1m]", true)]
    [InlineData("!~", true)]
    [InlineData("a b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a\\b", false)]
    [InlineData("é", false)]
    [InlineData("\u007f", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ids_are_printable_ascii_without_quote_or_backslash(string? id, bool ok) =>
        Assert.Equal(ok, RunFrames.IdOk(id));

    [Fact]
    public void An_id_is_at_most_64_characters()
    {
        Assert.True(RunFrames.IdOk(new string('x', 64)));
        Assert.False(RunFrames.IdOk(new string('x', 65)));
    }

    [Fact]
    public void Labels_and_reasons_lose_controls_and_are_cut()
    {
        Assert.Equal("a b", RunFrames.Clean("a\u0007b", 10));
        Assert.Equal("x x", RunFrames.Clean("x\u007fx", 10));
        Assert.Equal("ab…", RunFrames.Clean("abcdef", 3));
        Assert.Equal("id", RunFrames.Label("id", "\t\n"));
        Assert.Null(RunFrames.Reason(null));
    }

    private static RunState S(string? off = null) => new(
        new RunFieldState("opus", null, [new("opus", "Opus"), new("sonnet", "Sonnet")], off, null),
        new RunFieldState("high", null, [new("low", "low"), new("high", "high")], null, null),
        false);

    [Fact]
    public void Refusal_is_off_then_running_then_not_a_choice_then_none()
    {
        Assert.Equal((RunFrames.Unavailable, "no pane"), RunFrames.Refusal(S("no pane"), "model", "sonnet", running: true));
        Assert.Equal((RunFrames.Unavailable, RunFrames.BusyWhy), RunFrames.Refusal(S(), "model", "sonnet", running: true));
        Assert.Equal((RunFrames.BadValue, null), RunFrames.Refusal(S(), "effort", "sonnet", running: false));
        Assert.Null(RunFrames.Refusal(S(), "effort", "low", running: false));
    }

    [Fact]
    public void A_routes_answer_maps_to_its_code_with_a_sentence_where_one_was_missing()
    {
        Assert.Equal((RunFrames.BadValue, null), RunFrames.Answer(new RunSetResult(RunSetKind.BadValue, "x")));
        Assert.Equal((RunFrames.Unavailable, RunText.NoRoute), RunFrames.Answer(new RunSetResult(RunSetKind.Unavailable, null)));
        Assert.Equal((RunFrames.SetFailed, "it did not change"), RunFrames.Answer(new RunSetResult(RunSetKind.Failed, null)));
        Assert.Equal((RunFrames.SetFailed, "no"), RunFrames.Answer(new RunSetResult(RunSetKind.Failed, "no")));
    }

    [Fact]
    public void Caps_advertise_run_options_beside_list_dir_and_the_run_ck_is_per_session()
    {
        var caps = JsonDocument.Parse(BrowseFrames.Caps).RootElement.GetProperty("caps");
        Assert.Equal(["list_dir", "run_options"], caps.EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.Equal("run:abc", RunFrames.CkFor("abc"));
    }
}
