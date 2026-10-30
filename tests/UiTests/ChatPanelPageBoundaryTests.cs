using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 plan §2.1a, the panel half: when a scroll-back prepends the steps of a
// run whose rollup is already on screen at the top of the transcript, the model
// drops the rollup and the panel has to drop its row, or the reply is drawn
// twice and the row count drifts from the model's.
[Collection("Settings")]
public class ChatPanelPageBoundaryTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        OpenClawSessions.SetGatewayForTests(null);
    }

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static HistoryTurn H(ChatRole role, string text, bool fromRollup = false) =>
        new(role, text, null, "", new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), null, null,
            FromRollup: fromRollup);

    private static string[] RowTexts(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children
            .Select(r => string.Join("", r.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)))
            .ToArray();

    private ChatPanel Open(OpenClawChatSession session)
    {
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        FlushRender();
        return ChatPanelTestAccess.Instance!;
    }

    private static OpenClawChatSession Session()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        return new OpenClawChatSession($"openclaw:agent:main:dashboard:{id}", $"agent:main:dashboard:{id}", "main")
        {
            HasMore = false
        };
    }

    private static HistoryTurn Seam(string text) =>
        new(ChatRole.Assistant, text, null, "", new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero),
            null, null, FromRollup: true, SeamRollup: true);

    private static HistoryTurn Tail(string text, string? imageUrl = null) =>
        new(ChatRole.Assistant, text, imageUrl, imageUrl is null ? "" : "p.png",
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), null, null, OpenTail: true);

    // Stage 3b: the older page's trailing-run steps the head rollup contains are
    // left out before they become rows, so the panel only ever inserts what the
    // model inserted and its rows stay equal to the model's, with the words once.
    [AvaloniaFact]
    public void ScrollingBackLeavesOutTheOlderStepsTheHeadRollupContainsAndKeepsRowsInStep()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Seam("step1\n\nstep3\n\nand the answer"), H(ChatRole.User, "next") });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), Tail("step1"), H(ChatRole.System, "· exec  ls"), Tail("step3")
        });
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(session.History.Count, rows.Length);
        Assert.Equal(4, rows.Length);
        Assert.Equal(1, rows.Count(r => r.Contains("step3")));
        Assert.DoesNotContain(rows, r => r == "step1" || r == "step3");
        Assert.Contains("next", rows[^1]);
    }

    // An older step that carries a picture keeps its row and loses only its caption.
    [AvaloniaFact]
    public void ScrollingBackKeepsTheRowOfACoveredOlderPictureStepAndBlanksItsCaption()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Seam("look at this\n\nmore") });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), Tail("look at this", "/route") });
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(session.History.Count, rows.Length);
        Assert.Equal(3, rows.Length);
        Assert.Equal(1, rows.Count(r => r.Contains("look at this")));
    }

    // Control: nothing marked, nothing left out.
    [AvaloniaFact]
    public void ScrollingBackWhenNothingIsCoveredAddsEveryRow()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { Seam("the real answer") });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go"), Tail("let me look") });
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(session.History.Count, rows.Length);
        Assert.Equal(3, rows.Length);
        Assert.Contains("let me look", rows[1]);
    }
}
