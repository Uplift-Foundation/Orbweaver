using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orbweaver.Tests;
using Xunit;
using static Orbweaver.RemoteControlChatSession;

namespace Orbweaver.UiTests;

// CB-307: a clicked button sends the key its OPTION carries, never one derived
// from where it sits in the drawn list. Warren could not say what he clicked on
// the second question of a form, and the mini logged key=4 ("Chat about this");
// on that form "Type something." is drawn as the field, so if any key came from a
// button's index, pulling that row out would shift every key after it.
//
// The shape is the real one: a single-select question of Small, Large, Type
// something., Chat about this — derived from QA's real capture by renaming its
// two options, so every other line is the terminal's own. Each button is clicked
// through the real panel, against both the peer session and the local one, and
// the exact key that reaches the far side (or the local pane) is asserted.
[Collection("Settings")]
public class AskFormButtonKeyTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static readonly string Q2 = AskFormFixtures.SingleSelectQ1
        .Replace("Pick a color", "Pick a size")
        .Replace("1. Red", "1. Small").Replace("A warm, bold color", "The small one")
        .Replace("2. Blue", "2. Large").Replace("A cool, calm color", "The large one");

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpUntil(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static Border Button(ChatPanel panel, int index) =>
        ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[index])
            .GetVisualDescendants().OfType<Border>().First();

    private static string LabelOf(ChatPanel panel, int index) =>
        ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[index])
            .GetVisualDescendants().OfType<TextBlock>().First().Text ?? "";

    private static void Click(ChatPanel panel, Border button)
    {
        var at = button.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        button.RaiseEvent(new PointerPressedEventArgs(
            button, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    // --- the peer's panel -----------------------------------------------------

    private async Task<(ChatPanel Panel, List<string> Sent, string Id)> PeerPanel()
    {
        var sent = new List<string>();
        var wire = MirrorProtocol.PromptFromScreen(Q2)!;
        var session = new RemoteControlChatSession("rc:.claude-board:sid:q2", ".claude-board", "sid:q2")
        {
            WaitingNow = () => true,
            Ask = _ => Task.FromResult(new PromptReply(
                PromptReplyKind.Prompt, AskFormView.FromWire(wire), wire.Fingerprint)),
            Answer = (key, _, _) =>
            {
                sent.Add(key);
                return Task.FromResult(new AnswerReply(AnswerKind.Done));
            },
        };
        session.AssumeMirroringForTests();
        _toClean.Add(session.SessionId);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        await PumpUntil(() => session.Prompt is not null);
        Flush();

        return (ChatPanelTestAccess.Instance!, sent, session.SessionId);
    }

    // The buttons, in the order drawn, with the keys each must send.
    private static readonly (string Label, string Key)[] Expected =
    {
        ("Small", "1"), ("Large", "2"), ("Chat about this", "4"),
    };

    [AvaloniaFact]
    public async Task EveryPeerButtonSendsTheKeyItsOptionCarriesNotItsPosition()
    {
        foreach (var (label, key) in Expected)
        {
            var (panel, sent, id) = await PeerPanel();

            var drawn = panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children.Count;
            var index = Enumerable.Range(0, drawn).First(i => LabelOf(panel, i) == label);
            Click(panel, Button(panel, index));
            await PumpUntil(() => sent.Count > 0);

            Assert.Equal(new[] { key }, sent);
            ChatPanel.HideFor(id);
        }
    }

    [AvaloniaFact]
    public async Task TheDrawnButtonsAreSmallLargeTypeSomethingAndChatWithTheirRealKeys()
    {
        var (panel, _, _) = await PeerPanel();

        var prompt = AskFormView.FromWire(MirrorProtocol.PromptFromScreen(Q2)!);

        Assert.Equal(
            new[] { ("1", "Small"), ("2", "Large"), ("3", "Type something."), ("4", "Chat about this") },
            prompt.Options.Select(o => (o.Key, o.Label)));

        // The free-text row is among the buttons, and is not the one that is
        // pressed: it opens the box. It never shifts the keys after it.
        Assert.Equal("Type something.", LabelOf(panel, 2));
        Assert.Equal("Chat about this", LabelOf(panel, 3));
    }

    // --- this machine's own panel ---------------------------------------------

    private sealed class Pane(string screen)
    {
        public string Screen = screen;
        public List<string> Pressed { get; } = new();

        public Task<string?> Capture(SessionStatus _) => Task.FromResult<string?>(Screen);

        public Task<bool> Press(SessionStatus _, string key)
        {
            Pressed.Add(key);
            return Task.FromResult(true);
        }
    }

    private async Task<(ChatPanel Panel, Pane Pane, string Id)> LocalPanel()
    {
        var pane = new Pane(Q2);
        var session = new LocalCliChatSession("local-q2", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "idle", Title = "", TmuxPane = "%1",
        })
        {
            Capture = pane.Capture,
            PressKey = pane.Press,
        };
        _toClean.Add(session.SessionId);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, State = "waiting", Title = "", TmuxPane = "%1",
        });
        await PumpUntil(() => session.Prompt is not null);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();

        return (ChatPanelTestAccess.Instance!, pane, session.SessionId);
    }

    [AvaloniaFact]
    public async Task EveryLocalButtonPressesTheKeyItsOptionCarriesNotItsPosition()
    {
        var before = OrbweaverSettings.ClaudeCodeReplyEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = true;

            foreach (var (label, key) in Expected)
            {
                var (panel, pane, id) = await LocalPanel();

                var drawn = panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children.Count;
                var index = Enumerable.Range(0, drawn).First(i => LabelOf(panel, i) == label);
                Click(panel, Button(panel, index));
                await PumpUntil(() => pane.Pressed.Count > 0);

                Assert.Equal(new[] { key }, pane.Pressed);
                ChatPanel.HideFor(id);
            }
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeReplyEnabled = before;
        }
    }
}
