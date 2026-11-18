using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-356: a Claude Code compaction drawn as one compact collapsible row, in
// the family CB-330 made for relayed messages. The turns are built by hand (a
// compaction turn is a System turn carrying a CompactionNote), so this is the
// panel's half only: which row, what it says, what a click does, and what the
// relay setting, speech and scrolling make of it.
[Collection("Settings")]
public class ChatPanelCompactionRowTests : IDisposable
{
    private readonly ScopedSettingsDir _dir = new("compactionrow");
    private readonly List<string> _toClean = new();

    public ChatPanelCompactionRowTests()
    {
        SpeechRequest.UtteranceForTests = null;
    }

    public void Dispose()
    {
        SpeechRequest.UtteranceForTests = null;
        TextToSpeech.SilenceForTests = true;
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        _dir.Dispose();
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private const string SummaryText =
        "## Primary request\n\nShip the compaction row.\n\n- first point\n- second point";

    private static CompactionNote Auto(string summary = SummaryText) =>
        new("auto", 582_000, 9_000, 1_000, summary, HasBoundary: true);

    private static ChatTurn CompactionTurn(CompactionNote note, int minute = 1) => new()
    {
        Role = ChatRole.System,
        Text = note.Line,
        IsComplete = true,
        At = T0.AddMinutes(minute),
        Compaction = note
    };

    private static ChatTurn User(string text, int minute = 0) =>
        new() { Role = ChatRole.User, Text = text, IsComplete = true, At = T0.AddMinutes(minute) };

    private static ChatTurn Assistant(string text, int minute = 0) =>
        new() { Role = ChatRole.Assistant, Text = text, IsComplete = true, At = T0.AddMinutes(minute) };

    private static ChatTurn Relay(int minute = 1)
    {
        var relay = new RelayedMessage(RelayKind.Teammate, "hana-moriyama", "green",
            "idle_notification", "finished", "done", "done, in full");
        return new() { Role = ChatRole.System, Text = relay.Line, IsComplete = true, At = T0.AddMinutes(minute), Relay = relay };
    }

    private sealed class CompactionSession : IRemoteChatSession
    {
        private readonly List<ChatTurn> _history = new();

        public string SessionId { get; } = "compaction-" + Guid.NewGuid();
        public string DisplayName => "Lead";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;

        public CompactionSession(IEnumerable<ChatTurn> seed) => _history.AddRange(seed);

        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);
        public void Cancel() { }

        public void Add(ChatTurn turn)
        {
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
        }

        public void Updated(ChatTurn turn) => TurnUpdated?.Invoke(turn);

        public void ReplaceWith(IEnumerable<ChatTurn> turns)
        {
            _history.Clear();
            _history.AddRange(turns);
            HistoryReplaced?.Invoke();
        }

        // Present because the interface asks; never raised.
        private void Unused() => StateChanged?.Invoke(State);
    }

    private ChatPanel Open(IRemoteChatSession session)
    {
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        var panel = ChatPanelTestAccess.Instance!;
        Flush();
        return panel;
    }

    private ChatPanel OpenWith(params ChatTurn[] turns) => Open(new CompactionSession(turns));

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> Named<T>(ChatPanel panel, string name) where T : Control =>
        panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<T>().Where(c => c.Name == name);

    // Relay rows and compaction rows share one template, so the rows drawn
    // are the visible ones; CompactionIcon tells the two apart.
    private static List<Border> Rows(ChatPanel panel) =>
        Named<Border>(panel, "RelayRow").Where(b => b.IsVisible).ToList();

    private static List<Border> CompactionRows(ChatPanel panel) =>
        Rows(panel).Where(r => Inside<Control>(r, "CompactionIcon").IsVisible).ToList();

    private static Border OnlyCompactionRow(ChatPanel panel) => Assert.Single(CompactionRows(panel));

    private static T Inside<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static string[] RunsOf(Border row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Inlines is { Count: > 0 })
            .Inlines!.OfType<Run>().Select(r => r.Text ?? "").ToArray();

    private static string LineOf(Border row) => string.Concat(RunsOf(row));

    private static bool BodyShown(Border row) => Inside<ContentControl>(row, "RelayBodyHost").IsVisible;

    private static void Click(Control control, ChatPanel panel)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Flush();
    }

    // --- collapsed, and not a bubble ---

    [AvaloniaFact]
    public void ACompactionIsOneCollapsedRowWithItsLabelAndADimDetail()
    {
        var panel = OpenWith(User("hello"), CompactionTurn(Auto()));

        var row = OnlyCompactionRow(panel);
        Assert.Equal("Conversation compacted automatically  582k → 9k tokens", LineOf(row));
        Assert.False(BodyShown(row));
        Assert.True(Inside<Control>(row, "RelayChevron").IsVisible);
        Assert.True(Inside<Control>(row, "CompactionIcon").IsVisible);

        var runs = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Inlines is { Count: > 0 })
            .Inlines!.OfType<Run>().ToList();
        Assert.NotNull(runs[1].Foreground);
        Assert.NotEqual(runs[0].Foreground, runs[1].Foreground);
    }

    [AvaloniaFact]
    public void ACompactionIsNeverTheUserBubbleItsIconIsDrawnAndItHasNoDotOrFace()
    {
        var panel = OpenWith(User("hello"), Assistant("hi back"), CompactionTurn(Auto()));

        // Three bubble templates, one per turn; the compaction's is the hidden one.
        var bubbles = Named<Border>(panel, "BubbleRow").ToList();
        Assert.Equal(3, bubbles.Count);
        Assert.Equal(2, bubbles.Count(b => b.IsVisible));

        var row = OnlyCompactionRow(panel);
        Assert.Null(row.Background);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, row.HorizontalAlignment);
        Assert.False(Inside<Control>(row, "RelayDotMark").IsVisible);
        Assert.False(Inside<Control>(row, "RelayAvatarMark").IsVisible);
        Assert.IsAssignableFrom<Avalonia.Controls.Shapes.Path>(Inside<Control>(row, "CompactionIcon"));
    }

    [AvaloniaFact]
    public void ARelayRowBesideACompactionKeepsItsDotAndHidesTheIcon()
    {
        var panel = OpenWith(CompactionTurn(Auto(), 1), Relay(2));

        var rows = Rows(panel);
        Assert.Equal(2, rows.Count);
        Assert.Single(CompactionRows(panel));
        var relay = rows.Single(r => !Inside<Control>(r, "CompactionIcon").IsVisible);
        Assert.True(Inside<Control>(relay, "RelayDotMark").IsVisible);
        Assert.Contains("Teammate", LineOf(relay));
    }

    [AvaloniaFact]
    public void AnOrphanBoundaryHasNoChevronAndAClickDoesNothing()
    {
        var note = new CompactionNote("manual", 100_000, 5_000, null, "", HasBoundary: true);
        var panel = OpenWith(CompactionTurn(note));

        var row = OnlyCompactionRow(panel);
        Assert.Equal("Conversation compacted manually  100k → 5k tokens", LineOf(row));
        Assert.False(Inside<Control>(row, "RelayChevron").IsVisible);

        Click(Inside<Control>(row, "RelayHeader"), panel);

        Assert.False(BodyShown(OnlyCompactionRow(panel)));
    }

    [AvaloniaFact]
    public void AnOrphanSummaryShowsThePlainLabelAndExpands()
    {
        var note = new CompactionNote(null, null, null, null, SummaryText, HasBoundary: false);
        var panel = OpenWith(CompactionTurn(note));

        var row = OnlyCompactionRow(panel);
        Assert.Equal("Conversation compacted", LineOf(row));
        Assert.True(Inside<Control>(row, "RelayChevron").IsVisible);

        Click(Inside<Control>(row, "RelayHeader"), panel);

        Assert.True(BodyShown(OnlyCompactionRow(panel)));
    }

    // --- click to expand ---

    private static List<string> BodyTexts(Border row) =>
        Inside<ContentControl>(row, "RelayBodyHost").GetVisualDescendants()
            .OfType<SelectableTextBlock>().Select(b => b.Text ?? "").ToList();

    [AvaloniaFact]
    public void AClickExpandsToTheSummaryAsMarkdownAndASecondClickCollapses()
    {
        var panel = OpenWith(CompactionTurn(Auto()));
        var row = OnlyCompactionRow(panel);

        Click(Inside<Control>(row, "RelayHeader"), panel);

        row = OnlyCompactionRow(panel);
        Assert.True(BodyShown(row));
        var texts = BodyTexts(row);
        Assert.Contains(texts, t => t.Contains("Primary request"));
        Assert.Contains(texts, t => t.Contains("first point"));
        Assert.Contains(texts, t => t.Contains("second point"));

        // The summary's own words, not the app's note: upright.
        Assert.All(Inside<ContentControl>(row, "RelayBodyHost").GetVisualDescendants().OfType<SelectableTextBlock>(),
            b => Assert.NotEqual(FontStyle.Italic, b.FontStyle));
        Assert.Equal(90, Assert.IsType<RotateTransform>(Inside<Control>(row, "RelayChevron").RenderTransform).Angle);

        Click(Inside<Control>(row, "RelayHeader"), panel);

        row = OnlyCompactionRow(panel);
        Assert.False(BodyShown(row));
        Assert.Equal(0, Assert.IsType<RotateTransform>(Inside<Control>(row, "RelayChevron").RenderTransform).Angle);
    }

    [AvaloniaFact]
    public void ClickingOneCompactionLeavesAnotherCollapsed()
    {
        var other = Auto("## Other\n\nsecond summary");
        var panel = OpenWith(CompactionTurn(Auto(), 1), CompactionTurn(other, 2));

        Click(Inside<Control>(CompactionRows(panel)[0], "RelayHeader"), panel);

        var rows = CompactionRows(panel);
        Assert.True(BodyShown(rows[0]));
        Assert.False(BodyShown(rows[1]));
    }

    [AvaloniaFact]
    public void TheExpandedStateSurvivesAHistoryReloadInTheSamePanelAndIsGoneInANewOne()
    {
        var note = Auto();
        var session = new CompactionSession(new[] { CompactionTurn(note) });
        var panel = Open(session);

        Click(Inside<Control>(OnlyCompactionRow(panel), "RelayHeader"), panel);
        Assert.True(BodyShown(OnlyCompactionRow(panel)));

        // The same compaction, re-mapped into a brand-new ChatTurn, as a
        // session does on reload: the key is (when, what), not the object.
        session.ReplaceWith(new[] { CompactionTurn(note) });
        Flush();
        Assert.True(BodyShown(OnlyCompactionRow(panel)));

        ChatPanel.HideFor(session.SessionId);
        var fresh = OpenWith(CompactionTurn(note));
        Assert.False(BodyShown(OnlyCompactionRow(fresh)));
    }

    // --- not governed by the relay setting ---

    [AvaloniaFact]
    public void HiddenRemovesRelayRowsButNeverACompaction()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        var panel = OpenWith(User("before"), CompactionTurn(Auto(), 1), Relay(2), Assistant("after", 3));

        Assert.Single(CompactionRows(panel));
        Assert.Single(Rows(panel));

        // And switching to Hidden under an open panel leaves it alone too.
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Collapsed;
        ChatPanel.ApplyRelayDisplay();
        Flush();
        Assert.Equal(2, Rows(panel).Count);
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        ChatPanel.ApplyRelayDisplay();
        Flush();
        Assert.Single(Rows(panel));
        Assert.Single(CompactionRows(panel));
    }

    [AvaloniaFact]
    public void ExpandedDoesNotOpenACompactionAndTheSettingChangeDoesNotEither()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Expanded;
        var panel = OpenWith(CompactionTurn(Auto(), 1), Relay(2));

        Assert.False(BodyShown(OnlyCompactionRow(panel)));

        ChatPanel.ApplyRelayDisplay();
        Flush();
        Assert.False(BodyShown(OnlyCompactionRow(panel)));
    }

    // --- a live merge ---

    [AvaloniaFact]
    public void ALiveMergeGivesTheSameRowItsSummaryAndAChevron()
    {
        var boundaryOnly = new CompactionNote("auto", 246_000, 21_000, null, "", HasBoundary: true);
        var turn = CompactionTurn(boundaryOnly);
        var session = new CompactionSession(new[] { turn });
        var panel = Open(session);

        var row = OnlyCompactionRow(panel);
        Assert.False(Inside<Control>(row, "RelayChevron").IsVisible);

        var merged = boundaryOnly with { Summary = SummaryText };
        turn.Compaction = merged;
        turn.Text = merged.Line;
        session.Updated(turn);
        Flush();

        row = OnlyCompactionRow(panel);
        Assert.True(Inside<Control>(row, "RelayChevron").IsVisible);
        Click(Inside<Control>(row, "RelayHeader"), panel);
        Assert.Contains(BodyTexts(OnlyCompactionRow(panel)), t => t.Contains("second point"));
    }

    [AvaloniaFact]
    public void ACompactionAddedLiveDrawsARow()
    {
        var session = new CompactionSession(new[] { User("hi") });
        var panel = Open(session);
        Assert.Empty(CompactionRows(panel));

        session.Add(CompactionTurn(Auto()));
        Flush();

        Assert.Single(CompactionRows(panel));
    }

    // --- speech ---

    [AvaloniaFact]
    public void SpeakLatestSpeaksTheAssistantTurnBeforeATrailingCompactionNeverTheSummary()
    {
        var uttered = new List<string>();
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        OrbweaverSettings.SpeakScope = SpeakScope.Full;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);

        var panel = OpenWith(User("q"), Assistant("The assistant's own answer."),
            CompactionTurn(Auto("SECRET summary text")));
        panel.SpeakLatest();
        Flush();

        Assert.Single(uttered);
        Assert.Contains("The assistant's own answer.", uttered[0]);
        Assert.DoesNotContain("SECRET", uttered[0]);
        Assert.DoesNotContain("compacted", uttered[0], StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void SpeakLatestWithOnlyACompactionSaysNothing()
    {
        var uttered = new List<string>();
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);

        var panel = OpenWith(CompactionTurn(Auto()));
        panel.SpeakLatest();
        Flush();

        Assert.Empty(uttered);
    }

    // --- scroll ---

    // Only a User turn drags the view to the bottom; a compaction arriving
    // while the reader is scrolled up leaves them where they are.
    [AvaloniaFact]
    public void ACompactionArrivingWhileScrolledUpDoesNotMoveTheView()
    {
        var turns = Enumerable.Range(0, 40)
            .Select(i => Assistant(string.Join("\n\n", Enumerable.Repeat("filler line " + i, 4)), i))
            .ToArray();
        var session = new CompactionSession(turns);
        var panel = Open(session);
        var scroll = panel.FindControl<ScrollViewer>("Scroll")!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "needs something to scroll");

        scroll.Offset = new Vector(0, 0);
        Flush();

        session.Add(CompactionTurn(Auto(), 50));
        Flush();

        Assert.Equal(0, scroll.Offset.Y);
        Assert.Single(CompactionRows(panel));
    }

    // --- controls: everything else is styled as before ---

    [AvaloniaFact]
    public void AUserTurnAndAPlainSystemNoteBesideACompactionAreStyledAsBefore()
    {
        var note = new ChatTurn { Role = ChatRole.System, Text = "a plain note", IsComplete = true, At = T0 };
        var panel = OpenWith(User("hello"), note, CompactionTurn(Auto(), 2));

        var visible = Named<Border>(panel, "BubbleRow").Where(b => b.IsVisible).ToList();
        Assert.Equal(2, visible.Count);
        var user = visible.Single(b => b.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right);
        Assert.NotNull(user.Background);

        var system = visible.Single(b => b != user);
        Assert.Contains(system.GetVisualDescendants().OfType<SelectableTextBlock>(),
            t => t.FontStyle == FontStyle.Italic);
        Assert.Single(CompactionRows(panel));
    }

    [AvaloniaFact]
    public void TheIconAndLineGrowWithTheChatTextScale()
    {
        var panel = OpenWith(CompactionTurn(Auto()));

        var scaleBefore = OrbweaverSettings.ChatTextScale;
        OrbweaverSettings.ChatTextScale = ChatZoom.Default;
        ChatPanel.ReapplyTextScale();
        Flush();
        var row = OnlyCompactionRow(panel);
        var iconBefore = Inside<Control>(row, "CompactionIcon").Width;
        var sizeBefore = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Inlines is { Count: > 0 }).FontSize;
        try
        {
            OrbweaverSettings.ChatTextScale = ChatZoom.Max;
            ChatPanel.ReapplyTextScale();
            Flush();

            row = OnlyCompactionRow(panel);
            Assert.True(Inside<Control>(row, "CompactionIcon").Width > iconBefore);
            Assert.True(row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Inlines is { Count: > 0 }).FontSize > sizeBefore);
        }
        finally
        {
            OrbweaverSettings.ChatTextScale = scaleBefore;
            ChatPanel.ReapplyTextScale();
        }
    }
}
