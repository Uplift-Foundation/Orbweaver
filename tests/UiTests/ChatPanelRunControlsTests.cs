using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orbweaver;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-404: the chip and flyout for a CLI session — the options come out of
// RunControlsResolver exactly as a real session's would, and the panel is
// asserted on what a person would see: enabled, disabled with a reason (tooltip
// and a line of text), pending, the typed-route warning, the dial, and no
// OpenClaw extras.
[Collection("Settings")]
public class ChatPanelRunControlsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Five = { "low", "medium", "high", "xhigh", "max" };

    private static readonly RunCatalogue Claude = new(new[]
    {
        new RunModel("opus", "Opus 5.5", "claude-opus-5-5", Five),
        new RunModel("sonnet", "Sonnet 5.5", "claude-sonnet-5-5", Five),
        new RunModel("haiku", "Haiku 4.5", "claude-haiku-4-5", null),
    });

    private static readonly RunReading Reading = new("claude-opus-5-5", "high", T0, RunReadingSource.Transcript);
    private static readonly RunRouteDecision Control = new(RunRoute.Control, null, false);

    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static RemoteRunOptions Resolve(RunRouteDecision? route = null, RunReading? reading = null, params RunPending[] pending) =>
        RunControlsResolver.Resolve(RunCli.Claude, route ?? Control, reading ?? Reading, Claude, pending);

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private ChatPanel Open(RemoteRunOptions options, out FakeChatSession session)
    {
        var id = "runctl-" + Guid.NewGuid();
        _toClean.Add(id);
        session = new FakeChatSession { SessionId = id, DisplayName = "Claude", RunOptions = options };
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        FlushRender();
        var panel = ChatPanelTestAccess.Instance!;
        panel.OpenRunOptions();
        return panel;
    }

    private static async Task SettleAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        FlushRender();
    }

    private static string? ChipText(ChatPanel panel) => panel.FindControl<TextBlock>("RunOptionsChipText")!.Text;

    [AvaloniaFact]
    public void AnEnabledSessionShowsModelAndEffortAndNoOpenClawExtras()
    {
        var panel = Open(Resolve(), out _);

        Assert.Equal("Opus 5.5 · high", ChipText(panel));
        Assert.Contains(RunText.ChipTip, (string)ToolTip.GetTip(panel.FindControl<Border>("RunOptionsChip")!)!);
        Assert.True(panel.RunModelBox!.IsEnabled);
        Assert.Equal(new[] { "Opus 5.5", "Sonnet 5.5", "Haiku 4.5" }, panel.RunModelBox.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!));
        Assert.Equal(0, panel.RunModelBox.SelectedIndex);
        Assert.Equal(Five, panel.RunEffortDial!.Levels);
        Assert.Equal(2, panel.RunEffortDial.SelectedIndex);
        Assert.True(panel.RunEffortDial.IsEnabled);
        Assert.False(panel.RunEffortDial.IsPending);
        Assert.Null(panel.RunEffortDefault);
        Assert.Null(panel.RunModelReason);
        Assert.Null(panel.RunEffortReason);
        Assert.Null(panel.RunDefaultWarning);
        Assert.Null(panel.RunFastSwitch);
        Assert.Null(panel.RunLimitBox);
    }

    [AvaloniaFact]
    public void ADisabledSessionKeepsBothControlsVisibleWithTheReasonAsTextAndTooltip()
    {
        var panel = Open(Resolve(RunRouteDecision.Unavailable(SlashRouting.PeerSocketReason)), out _);

        Assert.False(panel.RunModelBox!.IsEnabled);
        Assert.True(panel.RunModelBox.IsVisible);
        Assert.Equal(SlashRouting.PeerSocketReason, panel.RunModelReason!.Text);
        Assert.Equal(SlashRouting.PeerSocketReason, ToolTip.GetTip(panel.RunModelBox));
        Assert.True(ToolTip.GetShowOnDisabled(panel.RunModelBox));
        Assert.False(panel.RunEffortDial!.IsEnabled);
        Assert.Equal(SlashRouting.PeerSocketReason, panel.RunEffortReason!.Text);
        Assert.Equal(SlashRouting.PeerSocketReason, ToolTip.GetTip(panel.RunEffortDial));
    }

    [AvaloniaFact]
    public void AModelWithNoEffortSettingGreysTheDialAndSaysSo()
    {
        var panel = Open(Resolve(reading: new RunReading("haiku", null, T0, RunReadingSource.Transcript)), out _);

        Assert.True(panel.RunModelBox!.IsEnabled);
        Assert.False(panel.RunEffortDial!.IsEnabled);
        Assert.Empty(panel.RunEffortDial.Levels);
        Assert.Equal(RunText.NoEffortSetting, panel.RunEffortReason!.Text);
        Assert.Equal("Haiku 4.5", ChipText(panel));
    }

    [AvaloniaFact]
    public void APendingSetSaysApplyingOnTheChipTheLabelAndTheDial()
    {
        var options = Resolve(null, null, new RunPending(RunField.Effort, "max", T0.AddSeconds(1)), new RunPending(RunField.Model, "sonnet", T0.AddSeconds(1)));

        var panel = Open(options, out _);

        Assert.Equal($"Sonnet 5.5 · max · {RunText.Applying}", ChipText(panel));
        Assert.True(panel.RunEffortDial!.IsPending);
        Assert.Equal(4, panel.RunEffortDial.SelectedIndex);
        Assert.Equal(2, panel.RunEffortDial.ConfirmedIndex);
        Assert.Equal(1, panel.RunModelBox!.SelectedIndex);
        var label = ((StackPanel)panel.RunModelBox.Parent!).Children.OfType<TextBlock>().First().Text;
        Assert.Equal($"{OpenClawRunText.ModelLabel} · {RunText.Applying}", label);
    }

    [AvaloniaFact]
    public void TheTypedRouteWarnsBeforeAnythingIsChosen()
    {
        var panel = Open(Resolve(new RunRouteDecision(RunRoute.Typed, null, true)), out _);

        Assert.Equal(RunText.ClaudeTypedDefault, panel.RunDefaultWarning!.Text);
    }

    [AvaloniaFact]
    public async Task TurningTheDialSendsTheLevelId()
    {
        var panel = Open(Resolve(), out var session);

        panel.RunEffortDial!.Choose(3);
        await SettleAsync(() => session.ThinkingRequests.Count > 0);

        Assert.Equal(new string?[] { "xhigh" }, session.ThinkingRequests);
    }

    [AvaloniaFact]
    public async Task ARefusedLevelPutsTheDialBackAndSaysWhy()
    {
        var panel = Open(Resolve(), out var session);
        session.ReplyToRunChange = RunText.UnknownEffort("max", Five);

        panel.RunEffortDial!.Choose(4);
        await SettleAsync(() => session.ThinkingRequests.Count > 0);

        Assert.Equal(RunText.UnknownEffort("max", Five), panel.RunStatus!.Text);
        Assert.True(panel.RunStatus.IsVisible);
        Assert.Equal(2, panel.RunEffortDial!.SelectedIndex);
    }

    [AvaloniaFact]
    public void NothingKnownYetSaysSoOnTheChip()
    {
        var options = RunControlsResolver.Resolve(RunCli.Codex, new RunRouteDecision(RunRoute.Typed, null, false), RunReading.None, RunCatalogue.Empty, Array.Empty<RunPending>());

        var panel = Open(options, out _);

        Assert.Equal($"{RunText.ModelUnknown} · {RunText.EffortUnknown}", ChipText(panel));
        Assert.Equal(-1, panel.RunModelBox!.SelectedIndex);
        // No levels and no reason: nothing to draw a dial for.
        Assert.Null(panel.RunEffortDial);
    }

    [Fact]
    public void TheChipTextAddsApplyingOnlyWhilePending()
    {
        var options = Resolve();
        Assert.Equal("Opus 5.5 · high", ChatPanel.ChipTextFor(options));
        Assert.Equal($"Opus 5.5 · high · {RunText.Applying}", ChatPanel.ChipTextFor(options with { EffortPending = true }));
        Assert.Equal($"Opus 5.5 · high · {RunText.Applying}", ChatPanel.ChipTextFor(options with { ModelPending = true }));
    }
}
