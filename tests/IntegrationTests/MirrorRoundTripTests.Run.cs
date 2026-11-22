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

    // CB-404 hand check (the mini, 2026-11-22): every peer set was refused as busy
    // from the first. The first set's control post waited on a Keychain nobody
    // could answer, held the guard past the near side's ten seconds, and every
    // set behind it was refused. With a real host behind the far server: a post
    // that never answers falls back to typing, the set answers, and the next one
    // is not busy.
    [Fact]
    public async Task APeerSetToAnIdlePaneWhoseBridgePostHangsIsTypedAndTheNextSetIsNotBusy()
    {
        var typed = new List<string>();
        var posts = 0;
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, _ => "bridge-1", () => true,
            _ => new RunReading("claude-opus-5-5", "high", DateTimeOffset.UtcNow.AddMinutes(-5), RunReadingSource.Transcript),
            _ => RunSources.ClaudeFallback, _ => null,
            (_, text) => { lock (typed) typed.Add(text); return Task.FromResult(true); },
            (_, _, _) => { Interlocked.Increment(ref posts); return new TaskCompletionSource<MirrorSlashHost.ControlVerdict>().Task; },
            () => DateTimeOffset.UtcNow,
            Log: (_, _) => { },
            // The control budget runs out at once; the set's own budget never does.
            Delay: d => d == RunControlsHost.ControlBudget ? Task.CompletedTask : new TaskCompletionSource().Task));
        var harness = new Harness(_dir, run: host);
        var log = new List<string>();
        harness.Server.InputLog = (_, detail) => { lock (log) log.Add(detail); };
        harness.AddSession("job-hunter", WriteTranscript("run-hang.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var first = harness.Client.SetRunAsync("job-hunter", RunField.Model, "sonnet");
        await harness.Server.RunInFlight;
        Assert.Equal(RunSetResult.Sent, await first);

        var second = harness.Client.SetRunAsync("job-hunter", RunField.Effort, "low");
        await harness.Server.RunInFlight;
        Assert.Equal(RunSetResult.Sent, await second);

        Assert.Equal(["/model sonnet", "/effort low"], typed);
        Assert.Equal(2, posts);
        Assert.Contains(log, l => l.EndsWith("via=set run=model"));
    }

    [Fact]
    public async Task A_refused_run_logs_its_code_and_never_its_sentence()
    {
        var harness = new Harness(_dir, run: new FarRun { Running = true });
        var log = new List<string>();
        harness.Server.InputLog = (_, detail) => { lock (log) log.Add(detail); };
        harness.AddSession("job-hunter", WriteTranscript("run-log.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        var set = harness.Client.SetRunAsync("job-hunter", RunField.Model, "sonnet");
        await harness.Server.RunInFlight;
        Assert.Equal(new RunSetResult(RunSetKind.Unavailable, RunFrames.BusyWhy), await set);

        var line = Assert.Single(log, l => l.Contains("run=model"));
        Assert.EndsWith("via=refused run=model code=unavailable", line);
        Assert.DoesNotContain(RunFrames.BusyWhy, line);
    }

    // The same hand check from the mini's own panel, which nobody can open there:
    // both routes into RunControlsHost.SetAsync, the local panel
    // (LocalCliChatSession) and the peer INPUT, over ONE host, for an idle tmux
    // Claude session whose bridge post never answers. Both go through, typed, and
    // neither is refused as busy, so the guard was the host's and is fixed there.
    [Fact]
    public async Task BothRoutesIntoTheHostTypeAndNeitherIsBusyWhenTheBridgePostHangs()
    {
        var typed = new List<string>();
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, _ => "bridge-1", () => true,
            _ => new RunReading("claude-opus-5-5", "high", DateTimeOffset.UtcNow.AddMinutes(-5), RunReadingSource.Transcript),
            _ => RunSources.ClaudeFallback, _ => null,
            (_, text) => { lock (typed) typed.Add(text); return Task.FromResult(true); },
            (_, _, _) => new TaskCompletionSource<MirrorSlashHost.ControlVerdict>().Task,
            () => DateTimeOffset.UtcNow,
            Log: (_, _) => { },
            Delay: d => d == RunControlsHost.ControlBudget ? Task.CompletedTask : new TaskCompletionSource().Task));

        var harness = new Harness(_dir, run: host);
        var transcript = WriteTranscript("run-both.jsonl", Conversation(2));
        harness.AddSession("job-hunter", transcript);
        await harness.HandshakeAsync("job-hunter");

        // The local panel's route: the same session as the mini's own panel would hold it.
        var status = new SessionStatus
        {
            SessionId = "588fbf9a-local", Source = SessionSource.ClaudeCode, State = "idle", Title = "job-hunter",
            Cwd = _dir, TranscriptPath = transcript, TmuxPane = "%241",
        };
        using var local = new LocalCliChatSession(status.SessionId, status,
            findRegistry: _ => null, runControls: host);

        Assert.Null(await local.SetModelAsync("sonnet"));
        Assert.Null(await local.SetThinkingAsync("low"));

        // The peer's route, right after, into the same host.
        var peer = harness.Client.SetRunAsync("job-hunter", RunField.Model, "opus");
        await harness.Server.RunInFlight;
        Assert.Equal(RunSetResult.Sent, await peer);

        Assert.Equal(["/model sonnet", "/effort low", "/model opus"], typed);
        Assert.False(host.IsRunning(status));
    }
}
