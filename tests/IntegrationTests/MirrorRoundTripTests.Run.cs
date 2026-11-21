using Xunit;

namespace Orbweaver.Tests;

// CB-404 (plan 4.6): a session's model and effort over the linked-desktop
// mirror — the roster's `run` field and the `run` INPUT — through the real
// client and server with a fake RunControls on the far side.
public partial class MirrorRoundTripTests
{
    private sealed class FarRun : IRunControls
    {
        public RunState? State = new(
            new RunFieldState("opus", null, [new("opus", "Opus 5.5"), new("sonnet", "Sonnet 5.5")], null, null),
            new RunFieldState("high", "low", [new("low", "low"), new("high", "high")], null, "didn't apply"),
            true);
        public RunSetResult Result = RunSetResult.Sent;
        public bool ReadThrows, SetThrows, Running;

        public bool IsRunning(SessionStatus status) => Running;
        public readonly List<(string Field, string Value)> Sets = [];

        public RunState? Read(SessionStatus status) =>
            ReadThrows ? throw new IOException("transcript locked") : State;

        public Task<RunSetResult> SetAsync(SessionStatus status, string field, string value)
        {
            if (SetThrows) throw new InvalidOperationException("bridge went away");
            Sets.Add((field, value));
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task TheRosterCarriesTheFarSidesRunState()
    {
        var harness = new Harness(_dir, run: new FarRun());
        harness.AddSession("job-hunter", WriteTranscript("run1.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var run = harness.Client.StateFor("job-hunter").Entry!.Run!;
        Assert.Equal(("opus", null), (run.Model!.Id, run.Model.Pending));
        Assert.Equal(2, run.Model.Choices!.Count);
        Assert.Equal(("high", "low", "didn't apply"), (run.Effort!.Id, run.Effort.Pending, run.Effort.Failed));
        Assert.True(run.SavesDefault);

        // The near panel draws the chosen value as pending, with the confirmed one beside it.
        var options = MirrorRun.Options(run, "mini", "claude");
        Assert.Equal(("opus", "low", true, "high", "Opus 5.5 · low"),
            (options.ModelId, options.ThinkingId, options.EffortPending, options.ConfirmedEffortId, options.ChipText));
        Assert.Equal(RunText.ClaudeTypedDefault, options.DefaultWarning);
        Assert.Equal(RunText.CodexPickerDefault, MirrorRun.Options(run, "mini", MirrorProtocol.CliFor(SessionSource.Codex)).DefaultWarning);
        Assert.Null(options.ModelDisabledReason);
    }

    [Fact]
    public async Task AFarSideWithNoRunControlsSendsNoRunAndTheNearPanelSaysItIsTooOld()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("run2.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var entry = harness.Client.StateFor("job-hunter").Entry!;
        Assert.Null(entry.Run);
        // And the bytes never mention it, so an ordinary roster keeps its CB-216 hash.
        Assert.DoesNotContain("\"run\"", System.Text.Encoding.UTF8.GetString(MirrorProtocol.RosterBytes([entry])));

        var options = MirrorRun.Options(entry.Run, "mini");
        Assert.Equal(MirrorRun.TooOldReason("mini"), options.ModelDisabledReason);
        Assert.Equal(MirrorRun.TooOldReason("mini"), options.EffortDisabledReason);
        Assert.False(options.HasFast);
    }

    [Fact]
    public async Task AFarHostThatThrowsCostsTheSessionItsRunFieldNotTheRoster()
    {
        var harness = new Harness(_dir, run: new FarRun { ReadThrows = true });
        harness.AddSession("job-hunter", WriteTranscript("run3.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var entry = harness.Client.StateFor("job-hunter").Entry!;
        Assert.NotNull(entry);
        Assert.Null(entry.Run);
    }

    [Fact]
    public async Task ARunChangeReachesTheFarHostAndComesBackSet()
    {
        var far = new FarRun();
        var harness = new Harness(_dir, run: far);
        harness.AddSession("job-hunter", WriteTranscript("run4.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var setting = harness.Client.SetRunAsync("job-hunter", RunField.Effort, "low");
        await harness.Server.RunInFlight;
        var result = await setting;

        Assert.Equal(RunSetResult.Sent, result);
        Assert.Equal(("effort", "low"), Assert.Single(far.Sets));
        // A run change is never typed as text, and never run as a slash line.
        Assert.Empty(harness.Typed);
        Assert.Empty(harness.SlashRan);
    }

    [Fact]
    public async Task AModelWithNoEffortSettingDrawsTheModelAloneAndUnknownsSaySo()
    {
        var options = MirrorRun.Options(new PeerRun(
            new PeerRunField(Choices: [new("haiku", "Haiku 4.5")]),
            new PeerRunField(Off: RunText.NoEffortSetting)), "mini");
        Assert.Equal((RunText.ModelUnknown, null, false), (options.ChipText, options.DefaultWarning, options.ModelPending));
        options = MirrorRun.Options(new PeerRun(new PeerRunField("haiku", Choices: [new("haiku", "Haiku 4.5")]), null), "mini");
        Assert.Equal("Haiku 4.5 · " + RunText.EffortUnknown, options.ChipText);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ARunChangeWhileAnotherIsRunningOrOutsideTheChoicesIsRefusedBeforeTheHost()
    {
        var far = new FarRun();
        var harness = new Harness(_dir, run: far);
        harness.AddSession("job-hunter", WriteTranscript("run10.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var notAChoice = harness.Client.SetRunAsync("job-hunter", RunField.Model, "haiku");
        await harness.Server.RunInFlight;
        Assert.Equal(RunSetKind.BadValue, (await notAChoice).Kind);

        far.Running = true;
        var busy = harness.Client.SetRunAsync("job-hunter", RunField.Model, "sonnet");
        await harness.Server.RunInFlight;
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunFrames.BusyWhy), await busy);
        Assert.Empty(far.Sets);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ARefusedRunChangeKeepsItsKindAndWordsAcrossTheLink(int k)
    {
        var kind = (RunSetKind)k;
        var far = new FarRun { Result = new RunSetResult(kind, "Model 'x' not found, with \"quotes\" and ünicode") };
        var harness = new Harness(_dir, run: far);
        harness.AddSession("job-hunter", WriteTranscript("run5-" + kind + ".jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var setting = harness.Client.SetRunAsync("job-hunter", RunField.Model, "sonnet");
        await harness.Server.RunInFlight;

        // A bad_value from the route says so with the desk's own sentence for it.
        Assert.Equal(kind == RunSetKind.BadValue
                ? new RunSetResult(kind, "that isn't one of this session's choices")
                : new RunSetResult(kind, "Model 'x' not found, with \"quotes\" and ünicode"),
            await setting);
    }

    [Fact]
    public async Task AFarHostThatThrowsOnASetIsFailedNotADroppedLink()
    {
        var harness = new Harness(_dir, run: new FarRun { SetThrows = true });
        harness.AddSession("job-hunter", WriteTranscript("run6.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var setting = harness.Client.SetRunAsync("job-hunter", RunField.Model, "sonnet");
        await harness.Server.RunInFlight;

        Assert.Equal(new RunSetResult(RunSetKind.Failed, "that machine failed while changing it"), await setting);
    }

    [Fact]
    public async Task AFarSideWithNoRunControlsRefusesARunInputAsUnavailableRatherThanTypingIt()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("run7.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var setting = harness.Client.SetRunAsync("job-hunter", RunField.Model, "sonnet");
        await harness.Server.RunInFlight;

        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RelayRemote.NoRunControlsWhy), await setting);
        Assert.Empty(harness.Typed);
    }

    [Theory]
    [InlineData("fast", "on")]
    [InlineData("model", "-rf")]
    public async Task ARunInputOfTheWrongShapeIsBadValueAndNeverReachesTheHost(string field, string value)
    {
        var far = new FarRun();
        var harness = new Harness(_dir, run: far);
        harness.AddSession("job-hunter", WriteTranscript("run8-" + field + ".jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var outcome = harness.Client.SendInputDetailedAsync("job-hunter", value, run: field);
        await harness.Server.RunInFlight;
        var result = MirrorRun.ResultOf(await outcome);

        Assert.Equal(RunSetKind.BadValue, result.Kind);
        Assert.Empty(far.Sets);
    }

    [Fact]
    public async Task ARunChangeForASessionTheFarSideDoesNotHaveIsFailedWithTheCode()
    {
        var harness = new Harness(_dir, run: new FarRun());
        harness.AddSession("job-hunter", WriteTranscript("run9.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var result = await harness.Client.SetRunAsync("nobody", RunField.Model, "sonnet");

        Assert.Equal(new RunSetResult(RunSetKind.Failed, MirrorProtocol.ErrNoSession), result);
    }
}
