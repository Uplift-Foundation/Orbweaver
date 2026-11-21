using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: a local CLI panel session shows and sets model and effort through
// the windowless RunControlsHost, and says when the answer moved — but only
// then, so the 2 s poll does not rebuild an open flyout.
[Collection("Settings")]
public class LocalCliChatSessionRunTests
{
    private sealed class Desk
    {
        public RunReading? Reading = new("claude-opus-5-5", "high", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), RunReadingSource.Transcript);
        public string? Bridge = "bridge-1";
        public MirrorSlashHost.ControlVerdict Verdict = MirrorSlashHost.ControlVerdict.Done;
        public readonly List<string> Posted = new();
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_010);
        public RunControlsHost Host = null!;

        public Desk()
        {
            Host = new RunControlsHost(new RunControlsHost.Seams(
                _ => false, st => st.BridgeSessionId, () => true, _ => Reading,
                _ => new RunCatalogue(new[]
                {
                    new RunModel("opus", "Opus 5.5", "claude-opus-5-5", new[] { "low", "medium", "high", "xhigh", "max" }),
                    new RunModel("sonnet", "Sonnet 5.5", "claude-sonnet-5-5", new[] { "low", "medium", "high" }),
                }),
                _ => null,
                (_, _) => Task.FromResult(true),
                (_, subtype, request) => { Posted.Add(request.ToJsonString()); return Task.FromResult(Verdict); },
                () => Now, Log: (_, _) => { }));
        }
    }

    private static LocalCliChatSession Session(Desk desk, string? bridge = null) =>
        new("run-s1", new SessionStatus
        {
            SessionId = "run-s1",
            Source = SessionSource.ClaudeCode,
            Entrypoint = SessionStatus.DesktopEntrypoint,
            BridgeSessionId = bridge ?? desk.Bridge,
            State = "idle",
            Title = "job",
            Cwd = "",
        }, findRegistry: _ => null, bridgeCommandsEnabled: () => true, runControls: desk.Host);

    [AvaloniaFact]
    public void TheOptionsAreTheHostsAnswer()
    {
        var desk = new Desk();
        using var s = Session(desk);

        var o = s.RunOptions!;

        Assert.Equal("Opus 5.5 · high", o.ChipText);
        Assert.Null(o.ModelDisabledReason);
        Assert.False(o.HasFast);
        Assert.Null(s.TimeoutMinutes);
        s.TimeoutMinutes = 5;
        Assert.Null(s.TimeoutMinutes);
    }

    [AvaloniaFact]
    public async Task SettingTheModelPostsSetModelAndTheChipSaysApplying()
    {
        var desk = new Desk();
        using var s = Session(desk);
        var changed = 0;
        s.RunOptionsChanged += () => changed++;

        Assert.Null(await s.SetModelAsync("sonnet"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("{\"subtype\":\"set_model\",\"model\":\"sonnet\"}", desk.Posted.Single());
        Assert.True(s.RunOptions!.ModelPending);
        Assert.True(changed >= 1);
    }

    [AvaloniaFact]
    public async Task SettingEffortPostsApplyFlagSettings()
    {
        var desk = new Desk();
        using var s = Session(desk);

        Assert.Null(await s.SetThinkingAsync("xhigh"));

        Assert.Equal("{\"subtype\":\"apply_flag_settings\",\"settings\":{\"effortLevel\":\"xhigh\"}}", desk.Posted.Single());
    }

    [AvaloniaFact]
    public async Task ABadLevelIsRefusedBeforeAnythingIsSent()
    {
        var desk = new Desk();
        using var s = Session(desk);

        Assert.NotNull(await s.SetThinkingAsync("bogus"));
        Assert.Empty(desk.Posted);
    }

    [AvaloniaFact]
    public async Task ANullChoiceAndFastModeAreNotOffered()
    {
        var desk = new Desk();
        using var s = Session(desk);

        Assert.Equal("name a model", await s.SetModelAsync(null));
        Assert.Equal("name an effort level", await s.SetThinkingAsync(null));
        Assert.Equal(RunText.NoRoute, await s.SetFastModeAsync(true));
        Assert.Empty(desk.Posted);
    }

    [AvaloniaFact]
    public void ANewReadingIsAnnouncedOnceAndAnUnchangedOneNotAtAll()
    {
        var desk = new Desk();
        using var s = Session(desk);
        var changed = 0;
        s.RunOptionsChanged += () => changed++;

        s.AnnounceRun();
        Assert.Equal(0, changed);

        desk.Reading = desk.Reading! with { Effort = "low" };
        s.AnnounceRun();
        s.AnnounceRun();
        Assert.Equal(1, changed);
    }

    [AvaloniaFact]
    public async Task TheHostsChangeForAnotherSessionIsIgnoredAndOffThreadIsPosted()
    {
        var desk = new Desk();
        using var s = Session(desk);
        using var other = new LocalCliChatSession("other", new SessionStatus { SessionId = "other", Source = SessionSource.ClaudeCode, Entrypoint = SessionStatus.DesktopEntrypoint, BridgeSessionId = "b2", Cwd = "" },
            findRegistry: _ => null, bridgeCommandsEnabled: () => true, runControls: desk.Host);
        var changed = 0;
        s.RunOptionsChanged += () => changed++;

        // A set on the other session makes it pending there, not here.
        await other.SetModelAsync("sonnet");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, changed);

        // A set made off the UI thread (the phone relay's path) reaches the panel.
        desk.Reading = desk.Reading! with { Model = "claude-sonnet-5-5" };
        await Task.Run(() => desk.Host.SetAsync(new SessionStatus { SessionId = "run-s1", Source = SessionSource.ClaudeCode, Entrypoint = SessionStatus.DesktopEntrypoint, BridgeSessionId = "bridge-1", Cwd = "" }, RunField.Effort, "low"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(changed >= 1);
    }

    [AvaloniaFact]
    public void UpdatingTheStatusReAsksTheHost()
    {
        var desk = new Desk();
        using var s = Session(desk);
        var changed = 0;
        s.RunOptionsChanged += () => changed++;

        s.UpdateStatus(new SessionStatus { SessionId = "run-s1", Source = SessionSource.ClaudeCode, Entrypoint = SessionStatus.DesktopEntrypoint, BridgeSessionId = null, State = "idle", Cwd = "" });

        Assert.Equal(1, changed);
        Assert.NotNull(s.RunOptions!.ModelDisabledReason);
    }

    [Fact]
    public void TheSignatureOfNoOptionsIsEmpty()
    {
        Assert.Equal("", LocalCliChatSession.RunSignature(null));
    }
}
