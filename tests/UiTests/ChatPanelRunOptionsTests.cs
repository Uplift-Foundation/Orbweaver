using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 Stage 4: the run-controls chip and flyout in a real panel, driven
// through FakeChatSession, which implements IRemoteChatRunOptions so one fake
// can show and hide the controls and record what the panel asked for.
[Collection("Settings")]
public class ChatPanelRunOptionsTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static RemoteRunOptions Options(
        string chip = "Claude Opus 4.6 · adaptive",
        bool canReset = true, bool locked = false, bool fast = false, bool fastIsSet = false,
        string? thinkingId = null, bool noThinking = false) =>
        new(
            "claude-opus-4-6",
            new[] { new RemoteRunChoice("claude-opus-4-6", "Claude Opus 4.6"), new RemoteRunChoice("qwen/q", "Qwen") },
            thinkingId,
            "adaptive",
            noThinking
                ? Array.Empty<RemoteRunChoice>()
                // The id is sent and the label shown, and they differ here as they do for one model.
                : new[] { new RemoteRunChoice("off", "off"), new RemoteRunChoice("low", "on"), new RemoteRunChoice("adaptive", "adaptive") },
            fast, fastIsSet, locked, canReset, chip);

    private FakeChatSession Fake(RemoteRunOptions? options)
    {
        var id = "run-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession { SessionId = id, DisplayName = "Native", RunOptions = options };
    }

    private ChatPanel Open(FakeChatSession session)
    {
        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();
        return ChatPanelTestAccess.Instance!;
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

    private static Border Chip(ChatPanel panel) => panel.FindControl<Border>("RunOptionsChip")!;
    private static string? ChipText(ChatPanel panel) => panel.FindControl<TextBlock>("RunOptionsChipText")!.Text;

    // A session that implements none of the optional interfaces.
    private sealed class PlainSession : IRemoteChatSession
    {
        public string SessionId { get; } = "plain-" + Guid.NewGuid();
        public string DisplayName => "Plain";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>();
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }
        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);
        public void Cancel() { }
    }

    // --- the chip ---

    [AvaloniaFact]
    public void TheChipIsHiddenWhenTheSessionOffersNoOptions()
    {
        var panel = Open(Fake(null));

        Assert.False(Chip(panel).IsVisible);
    }

    [AvaloniaFact]
    public void TheChipIsHiddenForASessionThatDoesNotImplementTheInterface()
    {
        var plain = new PlainSession();
        _toClean.Add(plain.SessionId);
        ChatPanel.OpenFor(NewOrb(), plain);
        FlushRender();

        Assert.False(Chip(ChatPanelTestAccess.Instance!).IsVisible);
    }

    [AvaloniaFact]
    public void TheChipShowsTheModelThinkingAndFastTextAndTheTooltip()
    {
        var panel = Open(Fake(Options("Claude Opus 4.6 · high · fast")));

        Assert.True(Chip(panel).IsVisible);
        Assert.Equal("Claude Opus 4.6 · high · fast", ChipText(panel));
        Assert.Contains("reminders", (string)ToolTip.GetTip(Chip(panel))!);
    }

    [AvaloniaFact]
    public void ARunOptionsChangeRedrawsTheChip()
    {
        var session = Fake(Options("Claude Opus 4.6 · adaptive"));
        var panel = Open(session);

        session.RaiseRunOptionsChanged(Options("Qwen · on"));
        Assert.Equal("Qwen · on", ChipText(panel));

        session.RaiseRunOptionsChanged(null);
        Assert.False(Chip(panel).IsVisible);

        session.RaiseRunOptionsChanged(Options("Back again"));
        Assert.True(Chip(panel).IsVisible);
        Assert.Equal("Back again", ChipText(panel));
    }

    // The panel is reused across orbs: a session left subscribed would go on
    // redrawing the chip of whichever conversation is open next.
    [AvaloniaFact]
    public void ReBindingUnsubscribesTheOldSession()
    {
        var first = Fake(Options("first"));
        var panel = Open(first);

        var second = Fake(Options("second"));
        ChatPanel.OpenFor(NewOrb(), second);
        FlushRender();

        first.RaiseRunOptionsChanged(Options("stale redraw"));

        Assert.Equal("second", ChipText(panel));
    }

    // --- the flyout ---

    [AvaloniaFact]
    public void TheFlyoutOffersTheModelsTheLevelsAndTheTimeLimits()
    {
        var panel = Open(Fake(Options()));

        panel.OpenRunOptions();

        // "Default" first on each, since a reset is possible.
        Assert.Equal(new[] { "Default", "Claude Opus 4.6", "Qwen" },
            panel.RunModelBox!.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!));
        Assert.Equal(new[] { "Default (adaptive)", "off", "on", "adaptive" },
            panel.RunThinkingBox!.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!));
        Assert.Equal(new[] { "Default", "2 minutes", "10 minutes", "30 minutes", "60 minutes" },
            panel.RunLimitBox!.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!));
    }

    [AvaloniaFact]
    public async Task ChoosingAModelAsksForItsIdNotItsName()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();

        panel.RunModelBox!.SelectedIndex = 2;
        await SettleAsync(() => session.ModelRequests.Count > 0);

        Assert.Equal(new[] { "qwen/q" }, session.ModelRequests);
    }

    [AvaloniaFact]
    public async Task ChoosingDefaultForTheModelAsksForNull()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();
        panel.RunModelBox!.SelectedIndex = 2;
        await SettleAsync(() => session.ModelRequests.Count > 0);
        panel.OpenRunOptions();

        panel.RunModelBox!.SelectedIndex = 0;
        await SettleAsync(() => session.ModelRequests.Count > 1);

        Assert.Equal(new string?[] { "qwen/q", null }, session.ModelRequests);
    }

    // The qwen case: the id "low" is shown as "on", and "low" is what is sent.
    [AvaloniaFact]
    public async Task ChoosingAThinkingLevelSendsItsIdNotItsLabel()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();

        panel.RunThinkingBox!.SelectedIndex = 2;
        await SettleAsync(() => session.ThinkingRequests.Count > 0);

        Assert.Equal(new string?[] { "low" }, session.ThinkingRequests);
    }

    [AvaloniaFact]
    public async Task ChoosingDefaultForThinkingAsksForNull()
    {
        var session = Fake(Options(thinkingId: "low"));
        var panel = Open(session);
        panel.OpenRunOptions();
        Assert.Equal(2, panel.RunThinkingBox!.SelectedIndex);

        panel.RunThinkingBox!.SelectedIndex = 0;
        await SettleAsync(() => session.ThinkingRequests.Count > 0);

        Assert.Equal(new string?[] { null }, session.ThinkingRequests);
    }

    [AvaloniaFact]
    public async Task TheFastSwitchTogglesAndItsDefaultButtonOnlyShowsOnceItIsSet()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();
        Assert.False(panel.RunFastDefault!.IsVisible);

        panel.RunFastSwitch!.IsChecked = true;
        await SettleAsync(() => session.FastRequests.Count > 0);
        Assert.Equal(new bool?[] { true }, session.FastRequests);

        var set = Fake(Options(fast: true, fastIsSet: true));
        var panel2 = Open(set);
        panel2.OpenRunOptions();
        Assert.True(panel2.RunFastSwitch!.IsChecked);
        Assert.True(panel2.RunFastDefault!.IsVisible);

        panel2.RunFastDefault.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(() => set.FastRequests.Count > 0);
        Assert.Equal(new bool?[] { null }, set.FastRequests);
    }

    // A refusal is said in the flyout, the control goes back to what is true, and
    // the control is usable again.
    [AvaloniaFact]
    public async Task ARefusalShowsInTheStatusLineAndTheControlIsUsableAgain()
    {
        var session = Fake(Options());
        session.ReplyToRunChange = "Couldn't change the model: model not allowed";
        var panel = Open(session);
        panel.OpenRunOptions();

        panel.RunModelBox!.SelectedIndex = 2;
        await SettleAsync(() => session.ModelRequests.Count > 0);

        Assert.True(panel.RunStatus!.IsVisible);
        Assert.Equal("Couldn't change the model: model not allowed", panel.RunStatus.Text);
        Assert.True(panel.RunModelBox!.IsEnabled);
        // Back on what the session says is true: the model it is running on.
        Assert.Equal(1, panel.RunModelBox.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task ASuccessClearsAnEarlierRefusal()
    {
        var session = Fake(Options());
        session.ReplyToRunChange = "nope";
        var panel = Open(session);
        panel.OpenRunOptions();
        panel.RunFastSwitch!.IsChecked = true;
        await SettleAsync(() => session.FastRequests.Count > 0);
        Assert.True(panel.RunStatus!.IsVisible);

        session.ReplyToRunChange = null;
        panel.RunFastSwitch!.IsChecked = true;
        await SettleAsync(() => session.FastRequests.Count > 1);

        Assert.False(panel.RunStatus!.IsVisible);
    }

    [AvaloniaFact]
    public void ThePickedTimeLimitIsHeldOnTheSession()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();

        panel.RunLimitBox!.SelectedIndex = 2;
        Assert.Equal(10, session.TimeoutMinutes);

        panel.RunLimitBox!.SelectedIndex = 0;
        Assert.Null(session.TimeoutMinutes);
    }

    // The limit already chosen is what the box shows when the flyout reopens.
    [AvaloniaFact]
    public void AnEarlierTimeLimitIsShownWhenTheFlyoutReopens()
    {
        var session = Fake(Options());
        session.TimeoutMinutes = 30;
        var panel = Open(session);

        panel.OpenRunOptions();

        Assert.Equal(3, panel.RunLimitBox!.SelectedIndex);
    }

    // No Default entries and no reset button when the gateway cannot reset.
    [AvaloniaFact]
    public void WithoutAResetThereAreNoDefaultChoices()
    {
        var panel = Open(Fake(Options(canReset: false, fast: true, fastIsSet: true)));

        panel.OpenRunOptions();

        Assert.DoesNotContain(panel.RunModelBox!.Items.OfType<ComboBoxItem>(), i => (string)i.Content! == "Default");
        Assert.Equal(new[] { "off", "on", "adaptive" },
            panel.RunThinkingBox!.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!));
        Assert.False(panel.RunFastDefault!.IsVisible);
    }

    // The default level with no label to name it is just "Default".
    [AvaloniaFact]
    public void ADefaultThinkingLevelWithNoLabelIsJustDefault()
    {
        var options = Options() with { ThinkingDefaultLabel = null };
        var panel = Open(Fake(options));

        panel.OpenRunOptions();

        Assert.Equal("Default", (string)((ComboBoxItem)panel.RunThinkingBox!.Items[0]!).Content!);
    }

    [AvaloniaFact]
    public void ALockedModelCannotBeChangedAndSaysWhy()
    {
        var panel = Open(Fake(Options(locked: true)));

        panel.OpenRunOptions();

        Assert.False(panel.RunModelBox!.IsEnabled);
        Assert.Equal(OpenClawRunText.LockedModelTip, ToolTip.GetTip(panel.RunModelBox));
        Assert.True(panel.RunThinkingBox!.IsEnabled);
        Assert.True(panel.RunFastSwitch!.IsEnabled);
    }

    // A model with no thinking levels has no thinking row at all.
    [AvaloniaFact]
    public void NoThinkingChoicesMeansNoThinkingBox()
    {
        var panel = Open(Fake(Options(noThinking: true)));

        panel.OpenRunOptions();

        Assert.Null(panel.RunThinkingBox);
    }

    // Setting the controls up from the options is not a choice: rebuilding the
    // flyout while it is open asks for nothing.
    [AvaloniaFact]
    public void RebuildingTheFlyoutAsksForNothing()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();

        session.RaiseRunOptionsChanged(Options(thinkingId: "low", fast: true, fastIsSet: true));
        FlushRender();

        Assert.Empty(session.ModelRequests);
        Assert.Empty(session.ThinkingRequests);
        Assert.Empty(session.FastRequests);
        Assert.Equal(2, panel.RunThinkingBox!.SelectedIndex);
    }

    // The options going away under an open flyout closes it and hides the chip.
    [AvaloniaFact]
    public void OptionsVanishingUnderAnOpenFlyoutHidesTheChip()
    {
        var session = Fake(Options());
        var panel = Open(session);
        panel.OpenRunOptions();

        session.RaiseRunOptionsChanged(null);

        Assert.False(Chip(panel).IsVisible);
    }

    // Opening with nothing on offer is a no-op, not a crash.
    [AvaloniaFact]
    public void OpeningTheFlyoutWithNoOptionsDoesNothing()
    {
        var panel = Open(Fake(null));
        var before = panel.RunModelBox;

        panel.OpenRunOptions();

        // The panel is one window shared across tests, so what it held before is
        // what it holds after: nothing was built.
        Assert.Same(before, panel.RunModelBox);
    }

    // --- pictures: only where they can go ---

    private static async Task<bool> PastePictureAsync(ChatPanel panel)
    {
        var input = panel.FindControl<TextBox>("Input")!;
        var attachments = panel.FindControl<ItemsControl>("Attachments")!;

        input.Focus();
        FlushRender();

        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96));
        await panel.Clipboard!.SetBitmapAsync(bitmap);

        var gesture = TextBox.PasteGesture!;
        input.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers
        });
        await SettleAsync(() => attachments.IsVisible);

        return attachments.IsVisible;
    }

    // A session that has the interface and cannot send a picture right now (a
    // surface chat) leaves a paste as the ordinary paste it would have been.
    [AvaloniaFact]
    public async Task APasteIsNotTakenAsAPictureWhenThePanelsSessionCannotSendOne()
    {
        var session = Fake(null);
        session.CanSendImages = false;

        Assert.False(await PastePictureAsync(Open(session)));
    }

    [AvaloniaFact]
    public async Task APasteIsTakenAsAPictureWhenThePanelsSessionCan()
    {
        var session = Fake(null);

        Assert.True(await PastePictureAsync(Open(session)));
    }
}

