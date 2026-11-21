using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-404: frames-v13's desk side — set_run's shape rules, the run state frame's
/// bytes and caps, and run_options in caps. The cross-app byte vectors are
/// RelayV13VectorTests; these are the rules the vectors cannot enumerate.
/// </summary>
public class RelayV13FrameTests
{
    private static RelayRequest? Parse(string json) => RelayRequests.Parse(JsonDocument.Parse(json).RootElement, "ref");

    [Fact]
    public void A_well_formed_set_run_parses_with_its_field_and_value()
    {
        var r = Assert.IsType<SetRunRequest>(Parse("""{"t":"set_run","session_id":"s","field":"effort","value":"xhigh"}"""));
        Assert.Equal(("ref", "s", "effort", "xhigh"), (r.Ref, r.SessionId, r.Field, r.Value));
    }

    [Fact]
    public void A_set_run_with_no_session_is_bad_frame()
    {
        Assert.IsType<BadFrameRequest>(Parse("""{"t":"set_run","field":"model","value":"opus"}"""));
    }

    [Theory]
    [InlineData("""{"t":"set_run","session_id":"s","value":"opus"}""")]
    [InlineData("""{"t":"set_run","session_id":"s","field":"fast","value":"opus"}""")]
    [InlineData("""{"t":"set_run","session_id":"s","field":1,"value":"opus"}""")]
    public void A_field_that_is_missing_or_unknown_is_carried_as_null(string json)
    {
        var r = Assert.IsType<SetRunRequest>(Parse(json));
        Assert.Null(r.Field);
        Assert.Equal("opus", r.Value);
    }

    [Theory]
    [InlineData("""{"t":"set_run","session_id":"s","field":"model"}""")]
    [InlineData("""{"t":"set_run","session_id":"s","field":"model","value":""}""")]
    [InlineData("""{"t":"set_run","session_id":"s","field":"model","value":"-rf"}""")]
    [InlineData("""{"t":"set_run","session_id":"s","field":"model","value":"a b"}""")]
    [InlineData("""{"t":"set_run","session_id":"s","field":"model","value":7}""")]
    public void A_value_outside_launch_models_grammar_is_carried_as_null(string json)
    {
        var r = Assert.IsType<SetRunRequest>(Parse(json));
        Assert.Equal("model", r.Field);
        Assert.Null(r.Value);
    }

    [Fact]
    public void Value_grammar_is_launch_models_without_empty_and_capped()
    {
        Assert.True(RunFrames.ValueOk("claude-opus-5-5"));
        Assert.True(RunFrames.ValueOk("gpt-6.2-sol"));
        Assert.True(RunFrames.ValueOk(new string('m', RunFrames.MaxValueLength)));
        Assert.False(RunFrames.ValueOk(new string('m', RunFrames.MaxValueLength + 1)));
        Assert.False(RunFrames.ValueOk(null));
        Assert.False(RunFrames.ValueOk(""));
    }

    [Fact]
    public void The_state_frame_is_canonical_and_omits_what_is_unknown_or_unset()
    {
        var state = new RunState(
            new RunFieldState("opus", "Opus 5.5", [new("opus", "Opus 5.5"), new("sonnet", "Sonnet 5.5")], null, true),
            new RunFieldState(null, null, [], "this model has no effort setting", false));

        Assert.Equal(
            """{"t":"run","session_id":"s1","model":{"id":"opus","label":"Opus 5.5","choices":[{"id":"opus","label":"Opus 5.5"},{"id":"sonnet","label":"Sonnet 5.5"}],"pending":true},"effort":{"choices":[],"off":"this model has no effort setting"}}""",
            RunFrames.State("s1", state));
    }

    [Fact]
    public void The_coalescing_key_is_per_session_and_not_the_rosters()
    {
        Assert.Equal("run:abc", RunFrames.CkFor("abc"));
    }

    [Fact]
    public void Caps_advertise_run_options_beside_list_dir()
    {
        var caps = JsonDocument.Parse(BrowseFrames.Caps).RootElement.GetProperty("caps");
        Assert.Equal(["list_dir", "run_options"], caps.EnumerateArray().Select(c => c.GetString()).ToArray());
    }

    private static List<RemoteRunChoice> Many(int n, int labelLength = 4) =>
        Enumerable.Range(0, n).Select(i => new RemoteRunChoice("m" + i, new string('x', labelLength))).ToList();

    [Fact]
    public void Choices_under_both_caps_are_kept_whole_and_in_order()
    {
        var all = Many(5);
        Assert.Equal(all, RunFrames.Capped(all, "m2"));
        Assert.Equal(all, RunFrames.Capped(all, null));
    }

    [Fact]
    public void Choices_past_the_count_cap_are_cut_but_the_running_one_is_kept()
    {
        var all = Many(RunFrames.MaxChoices + 5);
        var kept = RunFrames.Capped(all, "m" + (RunFrames.MaxChoices + 3));
        Assert.Equal(RunFrames.MaxChoices, kept.Count);
        Assert.Equal("m" + (RunFrames.MaxChoices + 3), kept[^1].Id);
        Assert.Equal("m0", kept[0].Id);

        // A running value the catalogue does not hold is not invented here.
        Assert.Equal(RunFrames.MaxChoices, RunFrames.Capped(all, "nowhere").Count);
    }

    [Fact]
    public void Choices_past_the_byte_cap_are_cut_and_the_running_one_appended()
    {
        var all = Many(20, labelLength: 400);
        var kept = RunFrames.Capped(all, "m19");
        Assert.True(kept.Count < 20);
        Assert.Equal("m19", kept[^1].Id);
        Assert.Equal(kept.Count - 1, kept.Take(kept.Count - 1).Select((c, i) => c.Id == "m" + i).Count(x => x));
    }
}
