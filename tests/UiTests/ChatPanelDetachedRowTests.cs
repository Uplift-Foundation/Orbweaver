using System;
using System.Collections.Generic;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 gate: a row's PropertyChanged is raised from its turn's own change
// notification, and a row whose panel has dropped it (the transcript was
// replaced, so the list cleared and unbound it) has no subscriber, while the
// old turn still holds the row's handler. Changing that turn must not throw.
// Covers the no-subscriber side of the invoke that the live panel tests (always
// bound) never take.
[Collection("Settings")]
public class ChatPanelDetachedRowTests : IDisposable
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

    private static HistoryTurn Tool() =>
        new(ChatRole.System, "· exec  false", null, "",
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), null, null);

    [AvaloniaFact]
    public void AToolLineWhoseRowWasDroppedByAReplacedTranscriptCanStillBeMarkedFailed()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var session = new OpenClawChatSession($"openclaw:agent:main:dashboard:{id}", $"agent:main:dashboard:{id}", "main")
        {
            HasMore = false
        };
        session.SetHistory(new List<HistoryTurn> { Tool() });
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        FlushRender();

        var old = session.History[0];

        // The transcript is replaced: the panel clears its rows, so the old
        // turn's row has nothing bound to it any more.
        session.SetHistory(new List<HistoryTurn> { Tool() });
        FlushRender();

        old.IsError = true;
        FlushRender();

        Assert.True(old.IsError);
        Assert.False(session.History[0].IsError);
    }
}
