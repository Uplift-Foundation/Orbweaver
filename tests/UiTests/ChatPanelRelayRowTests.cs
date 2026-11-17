using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-330: a message a teammate or another session relayed, drawn as one compact
// collapsible row instead of a bubble. The turns are built by hand (a relay
// turn is a System turn carrying a RelayedMessage), so nothing here depends on
// the parser: this is the panel's half — which row, what it says, what a click
// does, what the setting does, and what speech and scrolling make of it.
[Collection("Settings")]
public class ChatPanelRelayRowTests : IDisposable
{
    private readonly ScopedSettingsDir _dir = new("relayrow");
    private readonly List<string> _toClean = new();

    public ChatPanelRelayRowTests()
    {
        SpeechRequest.UtteranceForTests = null;
    }

    public void Dispose()
    {
        SpeechRequest.UtteranceForTests = null;
        ChatPanel.RelayAvatarForTests = null;
        TextToSpeech.SilenceForTests = true;
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        _dir.Dispose();
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static RelayedMessage Finished(string name = "hana-moriyama", string? color = "green",
        string full = "Routine, no report.") =>
        new(RelayKind.Teammate, name, color, "idle_notification", "finished", full.Split('\n')[0], full);

    private static ChatTurn RelayTurn(RelayedMessage relay, int minute = 1) => new()
    {
        Role = ChatRole.System,
        Text = relay.Line,
        IsComplete = true,
        At = T0.AddMinutes(minute),
        Relay = relay
    };

    private static ChatTurn User(string text, int minute = 0) =>
        new() { Role = ChatRole.User, Text = text, IsComplete = true, At = T0.AddMinutes(minute) };

    private static ChatTurn Assistant(string text, int minute = 0) =>
        new() { Role = ChatRole.Assistant, Text = text, IsComplete = true, At = T0.AddMinutes(minute) };

    // A session that can have its whole history replaced and older turns
    // prepended, the two paths that rebuild rows.
    private sealed class RelaySession : IRemoteChatSession, IRemoteChatBacklog
    {
        private readonly List<ChatTurn> _history = new();

        public string SessionId { get; } = "relay-" + Guid.NewGuid();
        public string DisplayName => "Lead";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? HistoryReplaced;
        public event Action<int>? HistoryPrepended;

        public bool HasMore => false;

        public RelaySession(IEnumerable<ChatTurn> seed) => _history.AddRange(seed);

        public Task<bool> LoadOlderAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);
        public void Cancel() { }

        public void Add(ChatTurn turn)
        {
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
        }

        public void ReplaceWith(IEnumerable<ChatTurn> turns)
        {
            _history.Clear();
            _history.AddRange(turns);
            HistoryReplaced?.Invoke();
        }

        public void Prepend(IEnumerable<ChatTurn> turns)
        {
            var list = turns.ToList();
            _history.InsertRange(0, list);
            HistoryPrepended?.Invoke(list.Count);
        }

        // Present because the interface asks; never raised.
        private void Unused() { TurnUpdated?.Invoke(_history[0]); StateChanged?.Invoke(State); }
    }

    private ChatPanel Open(IRemoteChatSession session, Func<string, Bitmap?>? avatar = null)
    {
        _toClean.Add(session.SessionId);
        ChatPanel.RelayAvatarForTests = avatar;
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        var panel = ChatPanelTestAccess.Instance!;
        Flush();
        return panel;
    }

    private ChatPanel OpenWith(params ChatTurn[] turns) =>
        Open(new RelaySession(turns));

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> Named<T>(ChatPanel panel, string name) where T : Control =>
        panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<T>().Where(c => c.Name == name);

    // Every row carries a relay template and a bubble template and shows one,
    // so the rows that are *drawn* are the visible ones.

    private static List<Border> RelayRows(ChatPanel panel) => Named<Border>(panel, "RelayRow").Where(b => b.IsVisible).ToList();

    private static Border OnlyRelayRow(ChatPanel panel) => Assert.Single(RelayRows(panel));

    private static T Inside<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    // The line as a reader sees it: every run of the one TextBlock, in order.
    private static string LineOf(Border row)
    {
        var line = row.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Inlines is { Count: > 0 });
        return string.Concat(line.Inlines!.OfType<Run>().Select(r => r.Text));
    }

    private static string[] RunsOf(Border row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Inlines is { Count: > 0 })
            .Inlines!.OfType<Run>().Select(r => r.Text ?? "").ToArray();

    private static void Click(Control control, ChatPanel panel)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, panel, new Point(1, 1), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Flush();
    }

    private static bool BodyShown(Border row) => Inside<ContentControl>(row, "RelayBodyHost").IsVisible;

    // --- collapsed, and not the user's bubble ---

    [AvaloniaFact]
    public void ARelayedMessageIsOneCollapsedRowWithItsLabelAndADimPreview()
    {
        var panel = OpenWith(User("hello"), RelayTurn(Finished()));

        var row = OnlyRelayRow(panel);
        Assert.True(row.IsVisible);
        Assert.Equal("Teammate @hana-moriyama finished  Routine, no report.", LineOf(row));
        Assert.False(BodyShown(row));
        Assert.True(Inside<Control>(row, "RelayChevron").IsVisible);
    }

    [AvaloniaFact]
    public void TheNameIsBoldAndThePreviewIsDimmerThanTheLabel()
    {
        var panel = OpenWith(RelayTurn(Finished()));

        var text = OnlyRelayRow(panel).GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Inlines is { Count: > 0 });
        var runs = text.Inlines!.OfType<Run>().ToList();

        Assert.Equal(new[] { "Teammate ", "@hana-moriyama", " finished", "  Routine, no report." },
            runs.Select(r => r.Text));
        Assert.Equal(FontWeight.Bold, runs[1].FontWeight);
        Assert.NotEqual(FontWeight.Bold, runs[0].FontWeight);
        Assert.NotNull(runs[3].Foreground);
        Assert.NotEqual(text.Foreground, runs[3].Foreground);
    }

    // The control and the point of the ticket: the same panel draws a user turn
    // as the blue right-hand bubble and the relay as none of that.
    [AvaloniaFact]
    public void ARelayRowIsNotTheUserBubbleAndTheUserBubbleIsUnchanged()
    {
        var panel = OpenWith(User("hello"), Assistant("hi back"), RelayTurn(Finished()));

        var bubbles = Named<Border>(panel, "BubbleRow").ToList();
        Assert.Equal(3, bubbles.Count);

        // Three bubble templates, one per turn; the relay's is the hidden one.
        Assert.Equal(2, bubbles.Count(b => b.IsVisible));
        var userBubble = bubbles.First(b => b.IsVisible && b.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right);
        Assert.IsAssignableFrom<IBrush>(userBubble.Background);
        Assert.NotNull(userBubble.Background);

        var row = OnlyRelayRow(panel);
        Assert.Null(row.Background);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, row.HorizontalAlignment);
    }

    [AvaloniaFact]
    public void APlainSystemNoteBesideARelayKeepsItsOwnTemplate()
    {
        var note = new ChatTurn { Role = ChatRole.System, Text = "a plain note", IsComplete = true, At = T0 };
        var panel = OpenWith(note, RelayTurn(Finished()));

        Assert.Single(RelayRows(panel));
        Assert.Equal(1, Named<Border>(panel, "BubbleRow").Count(b => b.IsVisible));
    }

    [AvaloniaFact]
    public void ARowWithNoBodyHasNoChevronAndAClickDoesNothing()
    {
        var relay = new RelayedMessage(RelayKind.Teammate, "hana-moriyama", "green",
            "shutdown_approved", "shut down", "", "");
        var panel = OpenWith(RelayTurn(relay));

        var row = OnlyRelayRow(panel);
        Assert.False(Inside<Control>(row, "RelayChevron").IsVisible);
        Assert.Equal("Teammate @hana-moriyama shut down", LineOf(row));

        Click(Inside<Control>(row, "RelayHeader"), panel);

        Assert.False(BodyShown(OnlyRelayRow(panel)));
    }

    [AvaloniaFact]
    public void ATeamRowAndAPeerRowHaveNoNameRunAndTheRightLabel()
    {
        var team = new RelayedMessage(RelayKind.Teammate, null, null, "teammate_terminated", "",
            "nadia-okafor has shut down.", "nadia-okafor has shut down.");
        var peer = new RelayedMessage(RelayKind.Peer, "cb-276", null, "message", "", "hello there", "hello there, in full");
        var panel = OpenWith(RelayTurn(team, 1), RelayTurn(peer, 2));

        var rows = RelayRows(panel);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "Team", "  nadia-okafor has shut down." }, RunsOf(rows[0]));
        Assert.Equal(new[] { "From another session", "  hello there" }, RunsOf(rows[1]));
    }

    [AvaloniaFact]
    public void ARowWithANameButNoVerbDrawsTheNameAlone()
    {
        var relay = new RelayedMessage(RelayKind.Teammate, "hana", null, "message", "", "", "body");
        var panel = OpenWith(RelayTurn(relay));

        Assert.Equal(new[] { "Teammate ", "@hana" }, RunsOf(OnlyRelayRow(panel)));
    }

    // --- click to expand, per message ---

    [AvaloniaFact]
    public void AClickExpandsToMarkdownAndASecondClickCollapses()
    {
        var relay = Finished(full: "Done:\n\n- first item\n- second item");
        var panel = OpenWith(RelayTurn(relay));
        var row = OnlyRelayRow(panel);

        Click(Inside<Control>(row, "RelayHeader"), panel);

        row = OnlyRelayRow(panel);
        Assert.True(BodyShown(row));
        var texts = Inside<ContentControl>(row, "RelayBodyHost").GetVisualDescendants()
            .OfType<SelectableTextBlock>().Select(b => b.Text ?? "").ToList();
        Assert.Contains(texts, t => t.Contains("first item"));
        Assert.Contains(texts, t => t.Contains("second item"));

        // Somebody's words, not the app's own note: upright, where a system
        // line is italic.
        Assert.All(Inside<ContentControl>(row, "RelayBodyHost").GetVisualDescendants().OfType<SelectableTextBlock>(),
            b => Assert.NotEqual(FontStyle.Italic, b.FontStyle));

        // Open, the preview is what the body already says.
        Assert.DoesNotContain("  Done:", LineOf(row));
        var chevron = Inside<Control>(row, "RelayChevron");
        Assert.Equal(90, Assert.IsType<RotateTransform>(chevron.RenderTransform).Angle);

        Click(Inside<Control>(row, "RelayHeader"), panel);

        row = OnlyRelayRow(panel);
        Assert.False(BodyShown(row));
        Assert.Equal(0, Assert.IsType<RotateTransform>(Inside<Control>(row, "RelayChevron").RenderTransform).Angle);
        Assert.Contains("  Done:", LineOf(row));
    }

    [AvaloniaFact]
    public void ClickingOneRowLeavesAnotherCollapsed()
    {
        var panel = OpenWith(
            RelayTurn(Finished("hana-moriyama", full: "alpha"), 1),
            RelayTurn(Finished("nadia-okafor", full: "bravo"), 2));

        var rows = RelayRows(panel);
        Click(Inside<Control>(rows[0], "RelayHeader"), panel);

        rows = RelayRows(panel);
        Assert.True(BodyShown(rows[0]));
        Assert.False(BodyShown(rows[1]));
    }

    [AvaloniaFact]
    public void TheExpandedStateSurvivesAHistoryReloadInTheSamePanelAndIsGoneInANewOne()
    {
        var relay = Finished(full: "kept open");
        var turn = RelayTurn(relay);
        var session = new RelaySession(new[] { turn });
        var panel = Open(session);

        Click(Inside<Control>(OnlyRelayRow(panel), "RelayHeader"), panel);
        Assert.True(BodyShown(OnlyRelayRow(panel)));

        // The same message, re-mapped into a brand-new ChatTurn, as a session
        // does when it reloads: the key is (when, what), not the object.
        session.ReplaceWith(new[] { RelayTurn(relay) });
        Flush();
        Assert.True(BodyShown(OnlyRelayRow(panel)));

        // A new panel for a new session starts collapsed.
        ChatPanel.HideFor(session.SessionId);
        var fresh = OpenWith(RelayTurn(relay));
        Assert.False(BodyShown(OnlyRelayRow(fresh)));
    }

    // --- the three modes ---

    [AvaloniaFact]
    public void ExpandedOpensEveryRowByDefaultAndAClickStillCollapsesOne()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Expanded;
        var panel = OpenWith(RelayTurn(Finished(full: "open by default")));

        var row = OnlyRelayRow(panel);
        Assert.True(BodyShown(row));

        Click(Inside<Control>(row, "RelayHeader"), panel);

        Assert.False(BodyShown(OnlyRelayRow(panel)));
    }

    [AvaloniaFact]
    public void ChangingTheSettingReEvaluatesRowsTheReaderHasNotTouchedAndKeepsTheOnesTheyHave()
    {
        var panel = OpenWith(
            RelayTurn(Finished("hana-moriyama", full: "alpha"), 1),
            RelayTurn(Finished("nadia-okafor", full: "bravo"), 2));

        // The reader opens the second by hand, then the setting goes to Expanded.
        Click(Inside<Control>(RelayRows(panel)[1], "RelayHeader"), panel);
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Expanded;
        ChatPanel.ApplyRelayDisplay();
        Flush();

        Assert.All(RelayRows(panel), r => Assert.True(BodyShown(r)));

        // Back to Collapsed: the untouched row closes, the hand-opened one stays.
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Collapsed;
        ChatPanel.ApplyRelayDisplay();
        Flush();

        var rows = RelayRows(panel);
        Assert.Equal(2, rows.Count);
        Assert.Contains("hana-moriyama", LineOf(rows[0]));
        Assert.False(BodyShown(rows[0]));
        Assert.True(BodyShown(rows[1]));
    }

    [AvaloniaFact]
    public void HiddenDrawsNoRelayRowAndLeavesTheTurnsAroundItIntact()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        var panel = OpenWith(User("before"), RelayTurn(Finished()), Assistant("after"));

        Assert.Empty(RelayRows(panel));
        Assert.Equal(2, Named<Border>(panel, "BubbleRow").Count());
        Assert.Equal(2, panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children.Count);
    }

    [AvaloniaFact]
    public void SwitchingToHiddenWithThePanelOpenRemovesTheRowsAndSwitchingBackRestoresThemInOrder()
    {
        var panel = OpenWith(User("before"), RelayTurn(Finished(full: "x"), 1), Assistant("after", 2));
        Assert.Single(RelayRows(panel));

        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        ChatPanel.ApplyRelayDisplay();
        Flush();
        Assert.Empty(RelayRows(panel));
        Assert.Equal(2, panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children.Count);

        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Collapsed;
        ChatPanel.ApplyRelayDisplay();
        Flush();

        var children = panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;
        Assert.Equal(3, children.Count);
        Assert.Single(RelayRows(panel));
        // The relay is the middle row, where history put it.
        Assert.Contains(children[1].GetVisualDescendants().OfType<Border>(), b => b.Name == "RelayRow" && b.IsVisible);
    }

    [AvaloniaFact]
    public void AHistoryWithNoRelaysSurvivesTheSettingMovingBackFromHidden()
    {
        var panel = OpenWith(User("only"));
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        ChatPanel.ApplyRelayDisplay();
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Collapsed;
        ChatPanel.ApplyRelayDisplay();
        Flush();

        Assert.Equal(1, panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children.Count);
    }

    [AvaloniaFact]
    public void ALiveRelayTurnUnderHiddenAddsNothingAndALaterTurnLandsInTheRightPlace()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        var session = new RelaySession(new[] { User("first") });
        var panel = Open(session);

        session.Add(RelayTurn(Finished(), 1));
        Flush();
        Assert.Equal(1, panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children.Count);

        session.Add(Assistant("then this", 2));
        Flush();

        var children = panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;
        Assert.Equal(2, children.Count);
        Assert.Empty(RelayRows(panel));
    }

    [AvaloniaFact]
    public void AnOlderPageWithARelayInItPrependsAtTheRightPositionUnderHiddenAndShown()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Hidden;
        var session = new RelaySession(new[] { User("newest", 10) });
        var panel = Open(session);

        // Older page: an assistant turn, a relay, a user turn. Hidden draws two.
        session.Prepend(new[] { Assistant("old a", 1), RelayTurn(Finished(), 2), User("old u", 3) });
        Flush();
        Assert.Equal(3, panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children.Count);
        Assert.Empty(RelayRows(panel));

        // The same shape with the relay shown: all rows, in history order.
        ChatPanel.HideFor(session.SessionId);
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Collapsed;
        var shown = new RelaySession(new[] { User("newest", 10) });
        var panel2 = Open(shown);
        shown.Prepend(new[] { Assistant("old a", 1), RelayTurn(Finished(), 2), User("old u", 3) });
        Flush();

        var children = panel2.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;
        Assert.Equal(4, children.Count);
        Assert.Contains(children[1].GetVisualDescendants().OfType<Border>(), b => b.Name == "RelayRow" && b.IsVisible);
    }

    [AvaloniaFact]
    public void ALiveRelayTurnUnderCollapsedAddsARowButDoesNotMoveTheView()
    {
        var session = new RelaySession(new[] { User("first") });
        var panel = Open(session);

        session.Add(RelayTurn(Finished(), 1));
        Flush();

        Assert.Single(RelayRows(panel));
    }

    // --- speech ---

    [AvaloniaFact]
    public void SpeakLatestSpeaksTheAssistantTurnBeforeATrailingRelayRowNeverTheRelayLine()
    {
        var uttered = new List<string>();
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        OrbweaverSettings.SpeakScope = SpeakScope.Full;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);

        var panel = OpenWith(
            User("question"),
            Assistant("The assistant's own answer."),
            RelayTurn(Finished(full: "SECRET relay text")));

        panel.SpeakLatest();
        Flush();

        Assert.Single(uttered);
        Assert.Contains("The assistant's own answer.", uttered[0]);
        Assert.DoesNotContain("relay", uttered[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Teammate", uttered[0]);
    }

    [AvaloniaFact]
    public void SpeakLatestWithOnlyARelayInTheTranscriptSaysNothing()
    {
        var uttered = new List<string>();
        SpeechRequest.UtteranceForTests = (t, _, _) => uttered.Add(t);
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);

        var panel = OpenWith(RelayTurn(Finished(full: "only a relay")));
        panel.SpeakLatest();
        Flush();

        Assert.Empty(uttered);
    }

    // --- the face ---

    private static WriteableBitmap Face()
    {
        var bmp = new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        return bmp;
    }

    [AvaloniaFact]
    public void AResolvedFaceReplacesTheDot()
    {
        var face = Face();
        string? asked = null;
        var session = new RelaySession(new[] { RelayTurn(Finished("celeste-ngata-2")) });
        var panel = Open(session, name => { asked = name; return face; });

        var row = OnlyRelayRow(panel);
        Assert.Equal("celeste-ngata-2", asked);
        Assert.True(Inside<Control>(row, "RelayAvatarMark").IsVisible);
        Assert.False(Inside<Control>(row, "RelayDotMark").IsVisible);
        Assert.Same(face, row.GetVisualDescendants().OfType<Image>().Single().Source);
    }

    [AvaloniaTheory]
    [InlineData("green", "#00AF5F")]
    [InlineData("purple", "#875FD7")]
    [InlineData(null, "#66FFFFFF")]
    [InlineData("chartreuse", "#66FFFFFF")]
    public void WithNoFaceTheDotIsThePayloadColourOrNeutral(string? color, string expected)
    {
        var panel = OpenWith(RelayTurn(Finished(color: color)));

        var row = OnlyRelayRow(panel);
        var dot = Inside<Avalonia.Controls.Shapes.Ellipse>(row, "RelayDotMark");
        Assert.True(dot.IsVisible);
        Assert.False(Inside<Control>(row, "RelayAvatarMark").IsVisible);
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
    }

    [AvaloniaFact]
    public void APeerRowNeverAsksForAFace()
    {
        var asked = false;
        var peer = new RelayedMessage(RelayKind.Peer, "cb-276", null, "message", "", "hi", "hi");
        var session = new RelaySession(new[] { RelayTurn(peer) });
        var panel = Open(session, _ => { asked = true; return Face(); });

        Assert.False(asked);
        Assert.True(Inside<Control>(OnlyRelayRow(panel), "RelayDotMark").IsVisible);
    }

    // The default resolver, with no live sessions at all, is a dot and no throw.
    [AvaloniaFact]
    public void TheDefaultFaceLookupWithNoLiveMemberFallsBackToTheDot()
    {
        var panel = OpenWith(RelayTurn(Finished()));

        Assert.True(Inside<Control>(OnlyRelayRow(panel), "RelayDotMark").IsVisible);
    }

    // --- the default face lookup, with a session table handed in ---

    private static string WritePng(string dir)
    {
        var info = new SkiaSharp.SKImageInfo(16, 16, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        bitmap.Erase(new SkiaSharp.SKColor(0x8A, 0x6F, 0xD4, 0xFF));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(dir, "portrait.png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public void AMemberOfThisLeadWithAPortraitGetsItsFirstFrameAndAnyoneElseGetsNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb330-face-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var memberId = "member-" + Guid.NewGuid();
        var strangerId = "stranger-" + Guid.NewGuid();
        try
        {
            var picture = WritePng(dir);
            LocalPersonas.Set(memberId, new LocalPersona.Persona(
                "Celeste", null, null, null, picture, Array.Empty<string>()));
            LocalPersonas.Set(strangerId, new LocalPersona.Persona(
                "Stranger", null, null, null, picture, Array.Empty<string>()));

            var statuses = new Dictionary<string, SessionStatus>
            {
                [memberId] = new SessionStatus { Agent = "celeste-ngata", Lead = "the-lead" },
                // Same agent name, but led by somebody else: never this lead's.
                [strangerId] = new SessionStatus { Agent = "other-agent", Lead = "someone-else" },
            };

            var face = ChatPanel.FaceFor("celeste-ngata-2", "the-lead", statuses);
            Assert.NotNull(face);

            // Control: the same table, asked for the other lead's member.
            Assert.Null(ChatPanel.FaceFor("other-agent", "the-lead", statuses));
            // A member with no persona picture is a dot, not a throw.
            statuses["bare-" + Guid.NewGuid()] = new SessionStatus { Agent = "no-portrait", Lead = "the-lead" };
            Assert.Null(ChatPanel.FaceFor("no-portrait", "the-lead", statuses));
        }
        finally
        {
            LocalPersonas.Forget(memberId);
            LocalPersonas.Forget(strangerId);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // --- scale ---

    [AvaloniaFact]
    public void TheRowGrowsWithTheChatTextScale()
    {
        var panel = OpenWith(RelayTurn(Finished()));
        var before = Inside<Avalonia.Controls.Shapes.Ellipse>(OnlyRelayRow(panel), "RelayDotMark").Width;
        var scaleBefore = OrbweaverSettings.ChatTextScale;
        try
        {
            OrbweaverSettings.ChatTextScale = ChatZoom.Max;
            ChatPanel.ReapplyTextScale();
            Flush();

            var after = Inside<Avalonia.Controls.Shapes.Ellipse>(OnlyRelayRow(panel), "RelayDotMark").Width;
            Assert.True(after > before, $"{after} should exceed {before}");
        }
        finally
        {
            OrbweaverSettings.ChatTextScale = scaleBefore;
            ChatPanel.ReapplyTextScale();
        }
    }

    // QA's requirement: the label and the name are plain runs, never Markdown.
    // A teammate_id decoded from an XML attribute can carry < and &, and a name
    // like a_b_c or **x** must not turn into emphasis either.
    [AvaloniaTheory]
    [InlineData("a<b&c")]
    [InlineData("**bold**_x_")]
    [InlineData("`code`[l](u)")]
    public void ANameWithMarkupCharactersIsDrawnLiterally(string name)
    {
        var relay = new RelayedMessage(RelayKind.Teammate, name, null, "idle_notification", "finished", "p", "full");
        var panel = OpenWith(RelayTurn(relay));

        var row = OnlyRelayRow(panel);
        Assert.Equal(new[] { "Teammate ", "@" + name, " finished", "  p" }, RunsOf(row));
        Assert.Equal("Teammate @" + name + " finished  p", LineOf(row));
    }

    // --- the settings window's row ---

    private static SettingsWindow NewSettingsWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            types: Type.EmptyTypes) ?? throw new MissingMethodException("SettingsWindow", ".ctor()");
        return (SettingsWindow)ctor.Invoke(null);
    }

    [AvaloniaFact]
    public void TheChatCardHasARowForTheSettingThatSaysWhatEachChoiceDoes()
    {
        var text = string.Join(" ", NewSettingsWindow().ChatRows()
            .SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>())
            .Select(t => t.Text ?? ""));

        Assert.Contains("Teammate and peer messages", text);
        Assert.Contains("Collapsed", text);
        Assert.Contains("Expanded", text);
        Assert.Contains("Hidden", text);
    }

    [AvaloniaTheory]
    [InlineData(RelayedMessageDisplay.Collapsed, 0)]
    [InlineData(RelayedMessageDisplay.Expanded, 1)]
    [InlineData(RelayedMessageDisplay.Hidden, 2)]
    public void ThePickerOpensOnTheSavedChoice(RelayedMessageDisplay saved, int index)
    {
        OrbweaverSettings.RelayedMessages = saved;

        var combo = Assert.IsType<ComboBox>(NewSettingsWindow().RelayedMessagesPicker());

        Assert.Equal(index, combo.SelectedIndex);
    }

    [AvaloniaFact]
    public void ChoosingEachItemWritesTheSettingAndReachesAnOpenPanel()
    {
        var panel = OpenWith(RelayTurn(Finished(full: "body")));
        var combo = Assert.IsType<ComboBox>(NewSettingsWindow().RelayedMessagesPicker());
        Assert.Equal(0, combo.SelectedIndex);

        combo.SelectedIndex = 1;
        Flush();
        Assert.Equal(RelayedMessageDisplay.Expanded, OrbweaverSettings.RelayedMessages);
        Assert.True(BodyShown(OnlyRelayRow(panel)));

        combo.SelectedIndex = 2;
        Flush();
        Assert.Equal(RelayedMessageDisplay.Hidden, OrbweaverSettings.RelayedMessages);
        Assert.Empty(RelayRows(panel));

        combo.SelectedIndex = 0;
        Flush();
        Assert.Equal(RelayedMessageDisplay.Collapsed, OrbweaverSettings.RelayedMessages);
        Assert.False(BodyShown(OnlyRelayRow(panel)));
    }

    // Clearing the selection is not a choice: nothing is written.
    [AvaloniaFact]
    public void AClearedSelectionChangesNothing()
    {
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Expanded;
        var combo = Assert.IsType<ComboBox>(NewSettingsWindow().RelayedMessagesPicker());

        combo.SelectedIndex = -1;

        Assert.Equal(RelayedMessageDisplay.Expanded, OrbweaverSettings.RelayedMessages);
    }
}
