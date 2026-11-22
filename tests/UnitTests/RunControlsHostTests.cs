using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-404: RunControlsHost with RunControls' real resolver and fakes at the
/// edges — the pane, the bridge, the files. Every route that sends something
/// has its refusal beside it, and nothing is pending that was not sent.
/// </summary>
public class RunControlsHostTests
{
    private sealed class Desk
    {
        public bool CanType = true;
        public string? Bridge;
        public bool BridgeCommands = true;
        public RunReading? Reading;
        public RunCatalogue? Catalogue = new([
            new RunModel("opus", "Opus 5.5", "claude-opus-5-5", ["low", "medium", "high", "xhigh", "max"]),
            new RunModel("sonnet", "Sonnet 5.5", "claude-sonnet-5-5", ["low", "medium", "high"]),
            new RunModel("haiku", "Haiku 4.5", "claude-haiku-4-5", null),
        ]);
        public Version? CodexVersion;
        public bool Types = true;
        public string? Record;
        public MirrorSlashHost.ControlVerdict Verdict = MirrorSlashHost.ControlVerdict.Done;
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public readonly List<string> Typed = [];
        public readonly List<(string Subtype, string Json)> Posted = [];
        public readonly List<string> Changed = [];
        public TaskCompletionSource<bool>? TypeGate;
        public TaskCompletionSource<MirrorSlashHost.ControlVerdict>? PostGate;

        // The two clocks a set is bounded by, each released by the test.
        public readonly TaskCompletionSource ControlTimer = new(), SetTimer = new(), StepTimer = new();
        public RunControlsHost Host = null!;

        public Desk()
        {
            Host = new RunControlsHost(new RunControlsHost.Seams(
                _ => CanType,
                _ => Bridge,
                () => BridgeCommands,
                _ => Reading,
                _ => Catalogue,
                _ => CodexVersion,
                async (_, text) =>
                {
                    Typed.Add(text);
                    if (TypeGate is { } gate) await gate.Task;
                    return Types;
                },
                (_, subtype, request) =>
                {
                    Posted.Add((subtype, request.ToJsonString()));
                    return PostGate is { } gate ? gate.Task : Task.FromResult(Verdict);
                },
                () => Now,
                RecordStatus: _ => Record,
                Log: (_, _) => { },
                Delay: d => d == RunControlsHost.ControlBudget ? ControlTimer.Task
                    : d == RunControlsHost.SetBudget ? SetTimer.Task
                    : d == RunControlsHost.StepBudget ? StepTimer.Task
                    : Task.CompletedTask));
            Host.Changed += Changed.Add;
        }
    }

    private static SessionStatus Claude(bool desktop = false) =>
        new() { SessionId = "s1", Source = SessionSource.ClaudeCode, Entrypoint = desktop ? SessionStatus.DesktopEntrypoint : "", Cwd = "/w" };

    private static SessionStatus Of(SessionSource source) => new() { SessionId = "s1", Source = source, Cwd = "/w" };

    private static RunReading At(string? model, string? effort, DateTimeOffset at) =>
        new(model, effort, at, RunReadingSource.Transcript);

    [Fact]
    public async Task A_session_this_host_does_not_serve_has_no_options_and_cannot_be_set()
    {
        var d = new Desk();
        var s = Of(SessionSource.OpenClaw);
        Assert.Null(d.Host.Options(s));
        Assert.Null(d.Host.Read(s));
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunText.NoRoute), await d.Host.SetAsync(s, RunField.Model, "opus"));
        Assert.Empty(d.Typed);
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode, RunCli.Claude)]
    [InlineData(SessionSource.Codex, RunCli.Codex)]
    [InlineData(SessionSource.Grok, RunCli.Grok)]
    public void The_three_local_clis_are_served(SessionSource source, RunCli cli) =>
        Assert.Equal(cli, RunControlsHost.CliFor(Of(source)));

    [Fact]
    public async Task A_claude_session_with_a_bridge_is_set_by_control_and_shows_pending_until_read_back()
    {
        var d = new Desk { Bridge = "bridge-1", Reading = At("claude-opus-5-5", "high", DateTimeOffset.FromUnixTimeSeconds(1_799_999_000)) };

        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Claude(), RunField.Model, "sonnet"));

        var (subtype, json) = d.Posted.Single();
        Assert.Equal("set_model", subtype);
        Assert.Equal("sonnet", JsonNode.Parse(json)!["model"]!.GetValue<string>());
        Assert.Empty(d.Typed);
        Assert.Equal(["s1"], d.Changed);

        var o = d.Host.Options(Claude())!;
        Assert.Equal(("sonnet", true, "opus"), (o.ModelId, o.ModelPending, o.ConfirmedModelId));

        // A reading after the set that agrees clears it, and says so.
        d.Reading = At("claude-sonnet-5-5", "high", d.Now.AddSeconds(5));
        o = d.Host.Options(Claude())!;
        Assert.False(o.ModelPending);
        Assert.Equal(["s1", "s1"], d.Changed);
        Assert.Null(d.Host.FailureFor(Claude(), RunField.Model));
    }

    [Fact]
    public async Task A_bridge_refusal_is_failed_with_the_clis_words_and_nothing_is_pending()
    {
        var d = new Desk { Bridge = "b", Verdict = MirrorSlashHost.ControlVerdict.Refused("Model 'x' not found") };

        Assert.Equal(new RunSetResult(RunSetKind.Failed, "Model 'x' not found"), await d.Host.SetAsync(Claude(), RunField.Model, "opus"));
        Assert.False(d.Host.Options(Claude())!.ModelPending);
        Assert.Empty(d.Changed);
    }

    [Fact]
    public async Task Effort_over_a_bridge_is_apply_flag_settings_with_the_level()
    {
        var d = new Desk { Bridge = "b", Reading = At("opus", "medium", d0) };

        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Claude(), RunField.Effort, "xhigh"));

        var (subtype, json) = d.Posted.Single();
        Assert.Equal("apply_flag_settings", subtype);
        Assert.Equal("xhigh", JsonNode.Parse(json)!["settings"]!["effortLevel"]!.GetValue<string>());
        Assert.True(d.Host.Options(Claude())!.EffortPending);
    }

    private static readonly DateTimeOffset d0 = DateTimeOffset.FromUnixTimeSeconds(1_799_000_000);

    [Fact]
    public async Task With_bridge_commands_off_a_claude_pane_is_typed_and_warns_about_the_default()
    {
        var d = new Desk { Bridge = "b", BridgeCommands = false };

        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Claude(), RunField.Model, "sonnet"));
        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Claude(), RunField.Effort, "low"));

        Assert.Equal(["/model sonnet", "/effort low"], d.Typed);
        Assert.Empty(d.Posted);
        Assert.Equal(RunText.ClaudeTypedDefault, d.Host.Options(Claude())!.DefaultWarning);
    }

    [Fact]
    public async Task A_typed_set_the_gate_refuses_or_the_pane_drops_is_failed_and_not_pending()
    {
        var d = new Desk { Record = "waiting" };
        var refused = await d.Host.SetAsync(Claude(), RunField.Model, "opus");
        Assert.Equal(RunSetKind.Failed, refused.Kind);
        Assert.Equal(TypingGate.NotAtPromptReason(TypingGate.WaitingWhy), refused.Why);
        Assert.Empty(d.Typed);

        d.Record = null;
        d.Types = false;
        Assert.Equal(new RunSetResult(RunSetKind.Failed, "the terminal refused the text"),
            await d.Host.SetAsync(Claude(), RunField.Model, "opus"));
        Assert.False(d.Host.Options(Claude())!.ModelPending);
    }

    [Fact]
    public async Task With_no_pane_and_no_bridge_both_controls_are_off_with_the_existing_sentence()
    {
        var d = new Desk { CanType = false };
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, SlashRouting.PeerSocketReason),
            await d.Host.SetAsync(Claude(), RunField.Model, "opus"));
        Assert.Equal(SlashRouting.PeerSocketReason, d.Host.Options(Claude())!.ModelDisabledReason);

        // A Desktop chat is told about itself, and is never typed into even with a pane reported.
        d.CanType = true;
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, SlashRouting.DesktopNoBridgeReason),
            await d.Host.SetAsync(Claude(desktop: true), RunField.Model, "opus"));
        Assert.Empty(d.Typed);
    }

    [Fact]
    public async Task A_value_the_catalogue_refuses_is_bad_value_and_nothing_is_sent()
    {
        var d = new Desk { Bridge = "b", Reading = At("haiku", null, d0) };
        Assert.Equal(new RunSetResult(RunSetKind.BadValue, RunText.UnknownModel("gpt")), await d.Host.SetAsync(Claude(), RunField.Model, "gpt"));
        Assert.Equal(new RunSetResult(RunSetKind.BadValue, RunText.NoEffortSetting), await d.Host.SetAsync(Claude(), RunField.Effort, "high"));
        Assert.Empty(d.Posted);
    }

    [Fact]
    public async Task A_level_the_catalogue_cannot_judge_is_still_refused_by_the_control_builder()
    {
        // No catalogue (the probe failed): Validate lets anything through, and the
        // control request's own check is the last word before the bridge.
        var d = new Desk { Bridge = "b", Catalogue = null };
        var r = await d.Host.SetAsync(Claude(), RunField.Effort, "bogus");
        Assert.Equal(RunSetKind.BadValue, r.Kind);
        Assert.NotNull(r.Why);
        Assert.Empty(d.Posted);
    }

    [Theory]
    [InlineData(SessionSource.Codex, RunField.Model, "gpt-6-luna", "/model gpt-6-luna")]
    [InlineData(SessionSource.Codex, RunField.Effort, "low", "/model --reasoning=low")]
    [InlineData(SessionSource.Grok, RunField.Model, "grok-4.7", "/model grok-4.7")]
    [InlineData(SessionSource.Grok, RunField.Effort, "high", "/effort high")]
    public async Task Codex_and_grok_are_typed_in_their_own_words(SessionSource source, RunField field, string value, string line)
    {
        var d = new Desk { Catalogue = null, CodexVersion = new Version(0, 167, 0) };
        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Of(source), field, value));
        Assert.Equal(line, d.Typed.Single());
    }

    [Fact]
    public async Task Until_the_codex_version_is_known_the_controls_are_off_and_nothing_is_typed()
    {
        var d = new Desk { Catalogue = null, CodexVersion = null };
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunControlsHost.CheckingCodexVersion),
            await d.Host.SetAsync(Of(SessionSource.Codex), RunField.Effort, "low"));
        Assert.Equal(RunControlsHost.CheckingCodexVersion, d.Host.Options(Of(SessionSource.Codex))!.ModelDisabledReason);
        Assert.Empty(d.Typed);
    }

    [Fact]
    public async Task A_second_change_while_one_is_running_is_unavailable_and_goes_ahead_once_it_returns()
    {
        var d = new Desk { Catalogue = null, TypeGate = new() };
        var first = d.Host.SetAsync(Of(SessionSource.Grok), RunField.Model, "grok-4.7");
        Assert.True(d.Host.IsRunning(Of(SessionSource.Grok)));

        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunFrames.BusyWhy),
            await d.Host.SetAsync(Of(SessionSource.Grok), RunField.Effort, "high"));
        Assert.Single(d.Typed);

        d.TypeGate.SetResult(true);
        Assert.Equal(RunSetResult.Sent, await first);
        Assert.False(d.Host.IsRunning(Of(SessionSource.Grok)));
        d.TypeGate = null;
        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Of(SessionSource.Grok), RunField.Effort, "high"));
    }

    [Fact]
    public async Task A_contradicted_set_stays_reported_for_its_field_until_that_field_is_set_again()
    {
        var d = new Desk { Bridge = "b" };
        await d.Host.SetAsync(Claude(), RunField.Model, "sonnet");
        d.Reading = At("claude-opus-5-5", null, d.Now.AddSeconds(1));

        Assert.False(d.Host.Options(Claude())!.ModelPending);
        var failed = RunText.Contradicted(RunField.Model, "sonnet", "claude-opus-5-5");
        Assert.Equal(failed, d.Host.FailureFor(Claude(), RunField.Model));
        Assert.Equal(failed, d.Host.Read(Claude())!.Model.Failed);
        Assert.Null(d.Host.FailureFor(Claude(), RunField.Effort));

        // A set of the other field leaves it; a set of this one clears it.
        await d.Host.SetAsync(Claude(), RunField.Effort, "low");
        Assert.Equal(failed, d.Host.FailureFor(Claude(), RunField.Model));
        await d.Host.SetAsync(Claude(), RunField.Model, "sonnet");
        Assert.Null(d.Host.FailureFor(Claude(), RunField.Model));
    }

    [Fact]
    public async Task A_pending_set_that_is_neither_confirmed_nor_timed_out_stays_without_a_change_event()
    {
        var d = new Desk { Bridge = "b", Reading = At("opus", "high", d0) };
        await d.Host.SetAsync(Claude(), RunField.Model, "sonnet");
        d.Changed.Clear();

        d.Now = d.Now.AddMinutes(1);
        Assert.True(d.Host.Options(Claude())!.ModelPending);
        Assert.Empty(d.Changed);
    }

    [Fact]
    public async Task The_wire_view_carries_the_confirmed_id_the_pending_one_and_saves_default()
    {
        var d = new Desk { Bridge = "b", Reading = At("claude-opus-5-5", "high", d0) };
        var w = d.Host.Read(Claude())!;
        Assert.Equal(("opus", null, null, null), (w.Model.Id, w.Model.Pending, w.Model.Off, w.Model.Failed));
        Assert.Equal(3, w.Model.Choices.Count);
        Assert.Equal("high", w.Effort.Id);
        Assert.False(w.SavesDefault);

        await d.Host.SetAsync(Claude(), RunField.Effort, "xhigh");
        w = d.Host.Read(Claude())!;
        Assert.Equal(("high", "xhigh"), (w.Effort.Id, w.Effort.Pending));

        // Nothing read: no id rather than a guess. The typed route saves a default.
        d.Reading = null;
        d.BridgeCommands = false;
        w = d.Host.Read(Claude())!;
        Assert.Equal((null, null), (w.Model.Id, w.Model.Pending));
        Assert.True(w.SavesDefault);
    }

    [Fact]
    public async Task The_wire_overload_maps_its_field_names()
    {
        var d = new Desk { Catalogue = null };
        await d.Host.SetAsync(Of(SessionSource.Grok), RunFrames.FieldEffort, "high");
        await d.Host.SetAsync(Of(SessionSource.Grok), RunFrames.FieldModel, "grok-4.7");
        Assert.Equal(["/effort high", "/model grok-4.7"], d.Typed);
    }

    // ---- CB-404 hand check: a set that never finished held the guard ---------

    [Fact]
    public async Task A_control_post_that_never_answers_falls_back_to_typing_into_the_pane()
    {
        // The mini: bridge on, a bridge id, and a Keychain nobody can answer.
        var d = new Desk { Bridge = "b", PostGate = new() };

        var set = d.Host.SetAsync(Claude(), RunField.Model, "sonnet");
        Assert.Single(d.Posted);
        Assert.Empty(d.Typed);
        d.ControlTimer.SetResult();

        Assert.Equal(RunSetResult.Sent, await set);
        Assert.Equal("/model sonnet", d.Typed.Single());
        Assert.False(d.Host.IsRunning(Claude()));
        Assert.True(d.Host.Options(Claude())!.ModelPending);
    }

    [Fact]
    public async Task A_post_refused_before_the_cli_types_where_there_is_a_pane_and_fails_where_there_is_none()
    {
        var d = new Desk { Bridge = "b", Verdict = MirrorSlashHost.ControlVerdict.Unreached("no usable credential") };
        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Claude(), RunField.Effort, "high"));
        Assert.Equal("/effort high", d.Typed.Single());

        var paneless = new Desk { Bridge = "b", CanType = false, Verdict = MirrorSlashHost.ControlVerdict.Unreached("no usable credential") };
        Assert.Equal(new RunSetResult(RunSetKind.Failed, "no usable credential"), await paneless.Host.SetAsync(Claude(), RunField.Effort, "high"));
        Assert.Empty(paneless.Typed);
    }

    [Fact]
    public async Task The_clis_own_refusal_is_final_and_nothing_is_typed()
    {
        var d = new Desk { Bridge = "b", Verdict = MirrorSlashHost.ControlVerdict.Refused("Model 'x' not found") };
        Assert.Equal(new RunSetResult(RunSetKind.Failed, "Model 'x' not found"), await d.Host.SetAsync(Claude(), RunField.Model, "sonnet"));
        Assert.Empty(d.Typed);
    }

    [Fact]
    public async Task A_set_past_its_budget_answers_but_keeps_the_guard_until_the_run_really_ends()
    {
        var d = new Desk { TypeGate = new() };                   // typing that is still going

        var set = d.Host.SetAsync(Claude(), RunField.Model, "sonnet");
        d.SetTimer.SetResult();
        Assert.Equal(new RunSetResult(RunSetKind.Failed, RunControlsHost.NoAnswerInTime), await set);

        // Still typing: a second set is the honest busy, and types nothing.
        Assert.True(d.Host.IsRunning(Claude()));
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunFrames.BusyWhy), await d.Host.SetAsync(Claude(), RunField.Effort, "low"));
        Assert.Single(d.Typed);

        d.TypeGate.SetResult(true);
        await Until(() => !d.Host.IsRunning(Claude()));
        d.TypeGate = null;
        Assert.Equal(RunSetResult.Sent, await d.Host.SetAsync(Claude(), RunField.Effort, "low"));
    }

    [Fact]
    public async Task A_pane_step_that_never_returns_is_given_up_at_its_own_budget()
    {
        var d = new Desk { TypeGate = new() };                   // a tmux call that hangs

        var set = d.Host.SetAsync(Claude(), RunField.Model, "sonnet");
        d.StepTimer.SetResult();

        Assert.Equal(new RunSetResult(RunSetKind.Failed, "the terminal refused the text"), await set);
        Assert.False(d.Host.IsRunning(Claude()));
        d.TypeGate.SetResult(true);
    }

    internal static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(5);
        Assert.True(done());
    }

}
