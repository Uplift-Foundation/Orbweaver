using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 S5: the gateway's own turns on screen. The parser's rule is covered a
// case at a time in tests/UnitTests; what only this can check is that a turn it
// demotes reaches the panel as a dim note and not as a right-aligned bubble in
// the operator's colour, which is how a measured exec notice was drawn before.
//
// The page is the real chat.history shape, parsed by the real TurnsFromHistory
// and handed to a real OpenClawChatSession, so the demotion is not something a
// fake was told. Every sentence and id is invented.
[Collection("Settings")]
public class ChatPanelMachineTurnTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _toClean = new();

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

    private static Avalonia.Controls.Controls RenderedRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    private static Border Bubble(Control row) =>
        row.GetVisualDescendants().OfType<Border>()
           .First(b => b.HorizontalAlignment != HorizontalAlignment.Stretch);

    private static string AllText(Control row) =>
        string.Concat(row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    // An exec notice wearing the operator's suffix, a reminder, and a message
    // the operator really typed, in that order.
    private const string Page = """
        [{"role":"user","content":"Exec completed (calm-otter, code 0) :: build finished",
          "timestamp":1787000000000,
          "idempotencyKey":"3f1c8ad2-59b7-4e06-9c31-8ab7205de164:user",
          "provenance":{"kind":"internal_system","sourceTool":"exec"},
          "__openclaw":{"senderIsOwner":false}},
         {"role":"user","content":"A scheduled reminder has been triggered. The reminder content is: stretch",
          "timestamp":1787000001000},
         {"role":"user","content":"are the reminders going out?",
          "timestamp":1787000002000,
          "idempotencyKey":"7a4d19e0-0c52-4b8e-a6d3-91f0c4ba7d58:user"}]
        """;

    private static System.Collections.Generic.List<HistoryTurn> Turns() =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(Page).RootElement, null);

    // The panel draws the notices as notes and the typed message as the
    // operator's own bubble, in one conversation.
    [AvaloniaFact]
    public void ExecNoticesAndRemindersAreNotesAndTheTypedMessageIsStillTheOperators()
    {
        var chat = new OpenClawChatSession(
            "openclaw:agent:alpha:main", "agent:alpha:main", "Alpha") { HasMore = false };
        chat.SetHistory(Turns());
        _toClean.Add(chat.SessionId);

        ChatPanel.OpenFor(NewOrb(), chat);
        FlushRender();

        var rows = RenderedRows(ChatPanelTestAccess.Instance!);
        Assert.Equal(3, rows.Count);

        var exec = Bubble(rows[0]);
        var reminder = Bubble(rows[1]);
        var typed = Bubble(rows[2]);

        // The operator's own bubble sits on the right; the notes do not.
        Assert.Equal(HorizontalAlignment.Right, typed.HorizontalAlignment);
        Assert.NotEqual(HorizontalAlignment.Right, exec.HorizontalAlignment);
        Assert.NotEqual(HorizontalAlignment.Right, reminder.HorizontalAlignment);

        // A note gets nearly the full width, which a bubble does not.
        Assert.True(exec.MaxWidth > typed.MaxWidth);
        Assert.True(reminder.MaxWidth > typed.MaxWidth);

        Assert.Contains("Exec completed", AllText(rows[0]));
    }

    // S5-Q5 in miniature: the room merge drops System turns, so a member's exec
    // notice, which used to be drawn as the operator's words in the room, is no
    // longer in the room transcript at all.
    [AvaloniaFact]
    public void AnExecNoticeNoLongerAppearsInARoomTranscript()
    {
        var alpha = new OpenClawChatSession(
            "openclaw:agent:alpha:whatsapp:group:120000000000000001@g.us",
            "agent:alpha:whatsapp:group:120000000000000001@g.us", "alpha") { HasMore = false };
        alpha.SetHistory(Turns());

        var room = new OpenClawRoomChatSession(
            "openclaw:room:whatsapp:120000000000000001@g.us:" + Guid.NewGuid().ToString("N")[..8],
            "Studio Crew");
        room.SetMembers(new[] { (alpha, "Alpha", "#7f7") }.ToList());
        room.Rebuild();
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        var texts = RenderedRows(ChatPanelTestAccess.Instance!).Select(AllText).ToList();

        Assert.DoesNotContain(texts, t => t.Contains("Exec completed"));
        Assert.DoesNotContain(texts, t => t.Contains("scheduled reminder"));
        Assert.Contains(texts, t => t.Contains("are the reminders going out?"));
    }
}
