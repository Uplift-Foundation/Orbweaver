using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: the model and effort of a session on another machine, from a real
// panel over the real mirror client and server. Warren's hand check found a
// peer panel with no chip at all: the wire carried `run`, and nothing on the
// near side read it.
public partial class RemoteMirrorChatSessionTests
{
    private FarRunControls? _farRun;

    private sealed class FarRunControls : IRunControls
    {
        public RunState State = new(
            new RunFieldState("opus", null, [new("opus", "Opus 5.5"), new("sonnet", "Sonnet 5.5")], null, null),
            new RunFieldState("high", null, [new("low", "low"), new("high", "high")], null, null),
            false);
        public RunSetResult Result = RunSetResult.Sent;
        public readonly List<(string Field, string Value)> Sets = [];

        public RunState? Read(SessionStatus status) => State;
        public bool IsRunning(SessionStatus status) => false;

        public Task<RunSetResult> SetAsync(SessionStatus status, string field, string value)
        {
            Sets.Add((field, value));
            return Task.FromResult(Result);
        }
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ChatPanel PanelFor(RemoteControlChatSession session)
    {
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        return ChatPanelTestAccess.Instance!;
    }

    private static (bool Visible, string? Text) Chip(ChatPanel panel) =>
        (panel.FindControl<Border>("RunOptionsChip")!.IsVisible, panel.FindControl<TextBlock>("RunOptionsChipText")!.Text);

    [AvaloniaFact]
    public async Task A_peer_panel_shows_the_far_sessions_model_and_effort_on_its_chip()
    {
        _farRun = new FarRunControls();
        Wire("a", "b");
        var session = await OpenAsync();
        var panel = PanelFor(session);
        try
        {
            Assert.Equal((true, "Opus 5.5 · high"), Chip(panel));
            var options = session.RunOptions!;
            Assert.Equal(("opus", "high", null, null), (options.ModelId, options.ThinkingId, options.ModelDisabledReason, options.EffortDisabledReason));
            Assert.False(options.HasFast);
        }
        finally { ChatPanel.HideFor(session.SessionId); }
    }

    [AvaloniaFact]
    public async Task A_set_goes_to_the_far_side_shows_pending_and_a_later_roster_replaces_it()
    {
        _farRun = new FarRunControls();
        Wire("a", "b");
        var session = await OpenAsync();
        var changed = 0;
        session.RunOptionsChanged += () => changed++;

        Assert.Null(await session.SetModelAsync("sonnet"));

        Assert.Equal(("model", "sonnet"), Assert.Single(_farRun.Sets));
        Assert.Empty(_typed);                                    // never typed as text
        var pending = session.RunOptions!;
        Assert.Equal(("sonnet", true), (pending.ModelId, pending.ModelPending));
        Assert.Equal(1, changed);

        // The far side confirms on its next roster, which replaces the near guess.
        _farRun.State = _farRun.State with { Model = _farRun.State.Model with { Id = "sonnet" } };
        await _client.AskWhatTheyHaveAsync(Peers);            // the periodic full roster, as PeerSessions asks it
        Flush();

        var confirmed = session.RunOptions!;
        Assert.Equal(("sonnet", false, "sonnet"), (confirmed.ModelId, confirmed.ModelPending, confirmed.ConfirmedModelId));
        Assert.Equal(2, changed);

        // A roster that changed nothing about the run does not redraw the chip.
        await _client.AskWhatTheyHaveAsync(Peers);            // the periodic full roster, as PeerSessions asks it
        Flush();
        Assert.Equal(2, changed);
        session.Dispose();
    }

    [AvaloniaFact]
    public async Task An_effort_set_the_far_side_refuses_says_why_and_shows_its_failure()
    {
        _farRun = new FarRunControls { Result = new RunSetResult(RunSetKind.Failed, "It is showing a dialog: Bash command") };
        Wire("a", "b");
        var session = await OpenAsync();

        Assert.Equal("It is showing a dialog: Bash command", await session.SetThinkingAsync("low"));
        Assert.False(session.RunOptions!.EffortPending);

        // A failure the far side reports in its roster reaches the panel's record.
        _farRun.State = _farRun.State with { Effort = _farRun.State.Effort with { Failed = "low was not applied." } };
        await _client.AskWhatTheyHaveAsync(Peers);            // the periodic full roster, as PeerSessions asks it
        Assert.Equal("low was not applied.", session.RunOptions!.EffortFailure);
    }

    [AvaloniaFact]
    public async Task A_far_orbweaver_too_old_to_say_shows_the_chip_disabled_with_why()
    {
        _farRun = null;                                          // the far server sends no `run`
        Wire("a", "b");
        var session = await OpenAsync();
        var panel = PanelFor(session);
        try
        {
            var options = session.RunOptions!;
            var why = MirrorRun.TooOldReason(session.FarMachine);
            Assert.Equal((why, why), (options.ModelDisabledReason, options.EffortDisabledReason));
            Assert.True(Chip(panel).Visible);                    // disabled, never hidden
            Assert.Equal(why, await session.SetModelAsync("sonnet"));
            Assert.Empty(_typed);
        }
        finally { ChatPanel.HideFor(session.SessionId); }
    }

    [AvaloniaFact]
    public async Task Before_any_roster_the_controls_are_off_and_nothing_is_sent()
    {
        WireClientOnly();
        var session = NewSession();

        var options = session.RunOptions!;
        Assert.Equal(RemoteControlChatSession.RosterNotYet, options.ModelDisabledReason);
        Assert.Equal(RemoteControlChatSession.RosterNotYet, await session.SetThinkingAsync("high"));
        Assert.Equal("name a model", await session.SetModelAsync(null));
        Assert.Equal("name an effort level", await session.SetThinkingAsync(null));
        Assert.Equal(RunText.NoRoute, await session.SetFastModeAsync(true));
        session.TimeoutMinutes = 5;
        Assert.Null(session.TimeoutMinutes);
    }

    [AvaloniaFact]
    public async Task With_the_peer_link_off_a_set_says_so_and_sends_nothing()
    {
        _farRun = new FarRunControls();
        Wire("a", "b");
        var session = await OpenAsync();
        OrbweaverSettings.PeerLinkEnabled = false;
        try
        {
            Assert.Equal(RemoteControlChatSession.RemoteControlOffNote, await session.SetModelAsync("sonnet"));
            Assert.Empty(_farRun.Sets);
        }
        finally { OrbweaverSettings.PeerLinkEnabled = true; }
    }

    [AvaloniaFact]
    public async Task A_model_with_no_effort_setting_draws_the_model_alone_while_pending()
    {
        _farRun = new FarRunControls();
        _farRun.State = _farRun.State with
        {
            Model = _farRun.State.Model with { Choices = [new("haiku", "Haiku 4.5"), new("opus", "Opus 5.5")], Id = "haiku" },
            Effort = new RunFieldState(null, null, [], RunText.NoEffortSetting, null),
        };
        Wire("a", "b");
        var session = await OpenAsync();

        Assert.Null(await session.SetModelAsync("opus"));
        Assert.Equal("Opus 5.5", session.RunOptions!.ChipText);

        // The far side applied opus, which does have an effort setting.
        _farRun.State = _farRun.State with
        {
            Model = _farRun.State.Model with { Id = "opus" },
            Effort = new RunFieldState("low", null, [new("low", "Low"), new("high", "High")], null, null),
        };
        await _client.AskWhatTheyHaveAsync(Peers);            // the periodic full roster, as PeerSessions asks it
        Assert.Null(await session.SetThinkingAsync("high"));
        Assert.Equal("Opus 5.5 · High", session.RunOptions!.ChipText);
    }
}
