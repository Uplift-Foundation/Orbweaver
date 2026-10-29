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

    [AvaloniaFact]
    public void ScrollingBackOverASplitRunDrawsTheReplyOnceAndKeepsRowsInStepWithTheModel()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "step1\n\nstep3", fromRollup: true), H(ChatRole.User, "next")
        });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1"), H(ChatRole.System, "· exec  ls"), H(ChatRole.Assistant, "step3")
        }, olderEndsInOpenTextStep: true);
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(session.History.Count, rows.Length);
        Assert.Equal(5, rows.Length);
        Assert.DoesNotContain(rows, r => r.Contains("step1\n\nstep3"));
        Assert.Equal(1, rows.Count(r => r.Contains("step3")));
        Assert.Contains("next", rows[^1]);
    }

    // The dropped rollup is not the first row: a later step sits above it.
    [AvaloniaFact]
    public void ScrollingBackRemovesTheRightRowWhenTheRollupIsBehindALaterStep()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "step3"), H(ChatRole.Assistant, "step1\n\nstep3", fromRollup: true), H(ChatRole.User, "next")
        });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "step1"), H(ChatRole.System, "· exec  ls")
        }, olderEndsInOpenTextStep: true);
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(session.History.Count, rows.Length);
        Assert.Equal(5, rows.Length);
        Assert.DoesNotContain(rows, r => r.Contains("step1\n\nstep3"));
        Assert.Contains("step3", rows[3]);
        Assert.Contains("next", rows[4]);
    }

    // A rollup that was two rows (picture, then text) loses both when dropped.
    [AvaloniaFact]
    public void ScrollingBackRemovesEveryRowOfAMultiTurnRollup()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn>
        {
            H(ChatRole.Assistant, "", fromRollup: true), H(ChatRole.Assistant, "look", fromRollup: true), H(ChatRole.User, "next")
        });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn>
        {
            H(ChatRole.User, "go"), H(ChatRole.Assistant, "look"), H(ChatRole.System, "· exec  ls")
        }, olderEndsInOpenTextStep: true);
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(session.History.Count, rows.Length);
        Assert.Equal(4, rows.Length);
        Assert.Contains("next", rows[^1]);
    }

    // Control: nothing dropped, so no row is removed and rows still match.
    [AvaloniaFact]
    public void ScrollingBackWhenTheOlderPageEndsInAUserTurnRemovesNoRow()
    {
        var session = Session();
        session.SetHistory(new List<HistoryTurn> { H(ChatRole.Assistant, "reply", fromRollup: true) });
        var panel = Open(session);

        session.PrependHistory(new List<HistoryTurn> { H(ChatRole.User, "go") }, olderEndsInOpenTextStep: false);
        FlushRender();

        var rows = RowTexts(panel);

        Assert.Equal(2, rows.Length);
        Assert.Contains("go", rows[0]);
        Assert.Contains("reply", rows[1]);
    }
}
