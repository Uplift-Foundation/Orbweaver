using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

namespace Orbweaver.UiTests;

// Adversarial probes of Stage 6's panel, written in QA review: no keyboard route to
// an answer, and the pointer sequences that must not answer. Fake gateway only; the
// record shapes are declared, not measured.
[Collection("Settings")]
public class ChatPanelApprovalAdversarialTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly List<string> _toClean = new();
    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
    private readonly Func<DateTime> _previousClock = ChatPanel.PromptClock;
    private DateTime _now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _key = "agent:alpha:dashboard:" + Guid.NewGuid();
    private static void Reset() { OrbweaverSettings.OpenClawEnabled = false; OpenClawSessions.Restart(); }
    public ChatPanelApprovalAdversarialTests(ITestOutputHelper o) { _o = o; Reset(); OrbweaverSettings.OpenClawApproveEnabled = true; OrbweaverSettings.OpenClawEnabled = true; ChatPanel.PromptClock = () => _now; }
    public void Dispose() { Reset(); OpenClawSessions.ForgetApprovalsForTests(); ChatPanel.PromptClock = _previousClock; OrbweaverSettings.OpenClawApproveEnabled = _approve; OrbweaverSettings.OpenClawEnabled = _enabled; OpenClawSessions.SetGatewayForTests(null); foreach (var id in _toClean) ChatPanel.HideFor(id); }
    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static JsonElement Json(string t) => JsonDocument.Parse(t).RootElement;
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private string Row(string id, string command) => "{\"id\":\"" + id + "\",\"approvalKind\":\"exec\",\"request\":{\"command\":" + JsonSerializer.Serialize(command) + ",\"cwd\":\"/tmp\",\"host\":\"gateway\",\"agentId\":\"alpha\",\"sessionKey\":\"" + _key + "\"},\"createdAtMs\":" + Ms(DateTime.UtcNow) + ",\"expiresAtMs\":" + Ms(DateTime.UtcNow.AddMinutes(30)) + "}";
    private void Request(string id, string command = "ls -la /tmp") => OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id, command)), DateTime.UtcNow);
    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = r => r.Method == "connect"
            ? FakeGatewaySocket.Ok(r.Id, new { protocol = 4, server = new { version = "1.2.3" }, auth = new { scopes = new[] { "operator.read", "operator.write", "operator.approvals" } }, policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 } })
            : r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, new { applied = true, approval = new { status = "denied", reason = "user" } }) : FakeGatewaySocket.Ok(r.Id, new { });
        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token", (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        await gateway.ConnectAsync(null, CancellationToken.None);
        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }
    private (ChatPanel Panel, OpenClawChatSession Chat) Open()
    {
        var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + _key, "Alpha")!;
        _toClean.Add(chat.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), chat);
        Flush();
        return (ChatPanelTestAccess.Instance!, chat);
    }
    private static Border Option(ChatPanel panel, int index) => ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[index]).GetVisualDescendants().OfType<Border>().First();
    private static Pointer NewPointer() => new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
    private static void Press(ChatPanel panel, Border option) { var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2); option.RaiseEvent(new PointerPressedEventArgs(option, NewPointer(), panel, at, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1)); }
    private static void Release(ChatPanel panel, Border option) { var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2); option.RaiseEvent(new PointerReleasedEventArgs(option, NewPointer(), panel, at, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left)); }
    private static async Task Settle() { for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Flush(); }
    private static int Resolves(FakeGatewaySocket s) => s.Requests.Count(r => r.Method.Contains("resolve", StringComparison.Ordinal));

    [AvaloniaFact]
    public async Task KeyboardNeverAnswers()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1"); Flush(); _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            var keys = new (PhysicalKey Key, string? Text)[] { (PhysicalKey.Enter, null), (PhysicalKey.Space, " "), (PhysicalKey.Y, "y"), (PhysicalKey.N, "n"), (PhysicalKey.Digit1, "1"), (PhysicalKey.Digit2, "2"), (PhysicalKey.NumPad1, "1"), (PhysicalKey.NumPad2, "2"), (PhysicalKey.Tab, null), (PhysicalKey.A, "a"), (PhysicalKey.D, "d"), (PhysicalKey.Escape, null) };
            var input = panel.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            foreach (var focusInput in new[] { false, true })
            {
                if (focusInput && input is not null) input.Focus(); else panel.Focus();
                Flush();
                foreach (var (key, text) in keys)
                {
                    panel.KeyPressQwerty(key, RawInputModifiers.None);
                    if (text is not null) panel.KeyTextInput(text);
                    panel.KeyReleaseQwerty(key, RawInputModifiers.None);
                    // and with the modifier a send gesture uses
                    panel.KeyPressQwerty(key, RawInputModifiers.Control);
                    panel.KeyReleaseQwerty(key, RawInputModifiers.Control);
                }
                await Settle();
            }
            var stillShown = panel.FindControl<Border>("PromptBox")!.IsVisible;
            Assert.True(Resolves(socket) == 0, $"KEYBOARD: {keys.Length * 2} key sequences x 2 focus targets: resolve frames={Resolves(socket)}");
        }
    }

    [AvaloniaFact]
    public async Task PressDuringArmingReleaseAfter()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1"); Flush();
            Press(panel, Option(panel, 1));                 // pressed 0 ms after the prompt appears: inside the arming window
            _now += ChatPanel.PromptArmDelay + TimeSpan.FromMilliseconds(100);
            Release(panel, Option(panel, 1));               // released after the window
            await Settle();
            Assert.True(Resolves(socket) == 0, $"PRESS-IN-ARMING-WINDOW-RELEASE-AFTER: resolve frames={Resolves(socket)}");
        }
    }

    [AvaloniaFact]
    public async Task PressOnOneOptionReleaseOnAnother()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1"); Flush(); _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            Press(panel, Option(panel, 0)); Release(panel, Option(panel, 1));
            await Settle();
            Assert.True(Resolves(socket) == 0, $"PRESS-DENY-RELEASE-ALLOW: resolve frames={Resolves(socket)}");
        }
    }

    [AvaloniaFact]
    public async Task SwitchOffWhileShowing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1"); Flush(); _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            OrbweaverSettings.OpenClawApproveEnabled = false;
            Press(panel, Option(panel, 1)); Release(panel, Option(panel, 1));
            await Settle();
            Assert.True(Resolves(socket) == 0, $"SWITCH-OFF-THEN-CLICK: resolve frames={Resolves(socket)}");
        }
    }

    [AvaloniaFact]
    public async Task ReplyingOffStillAnswers()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var prev = OrbweaverSettings.OpenClawReplyEnabled;
            OrbweaverSettings.OpenClawReplyEnabled = false;
            try
            {
                var (panel, _) = Open();
                Request("a1"); Flush(); _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
                Press(panel, Option(panel, 0)); Release(panel, Option(panel, 0));
                await Settle();
                Assert.True(Resolves(socket) == 1, $"REPLYING-OFF-APPROVING-ON: expected 1 resolve frame, got {Resolves(socket)}");
            }
            finally { OrbweaverSettings.OpenClawReplyEnabled = prev; }
        }
    }

    // Pressed 300 ms after the prompt appeared and released at 700 ms: the press
    // itself was inside the window, so the release answers nothing.
    [AvaloniaFact]
    public async Task APressAt300MsReleasedAt700MsAnswersNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1"); Flush();
            _now += TimeSpan.FromMilliseconds(300);
            Press(panel, Option(panel, 1));
            _now += TimeSpan.FromMilliseconds(400);
            Release(panel, Option(panel, 1));
            await Settle();
            Assert.Equal(0, Resolves(socket));
        }
    }

    // A press on A, held while B replaces A, released on the same control after the
    // delay: B's own window restarted, so nothing is answered.
    [AvaloniaFact]
    public async Task APressOnOneRequestHeldAcrossItsReplacementAnswersNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1", "echo A"); Flush();
            _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            var allow = Option(panel, 1);
            Press(panel, allow);

            OpenClawSessions.OnEvent("exec.approval.resolved",
                Json("{\"id\":\"a1\",\"decision\":\"deny\"}"), DateTime.UtcNow);
            Request("b1", "echo B"); Flush();
            _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);

            Release(panel, allow);
            await Settle();
            Assert.Equal(0, Resolves(socket));
        }
    }

    // The lead's attack: the SAME id, different content, while a press is held. Resolve + re-request lets a
    // hostile gateway swap the command under one id, so the id-bound click cannot tell them apart.
    private async Task<(bool reused, int frames, string title)> SwapUnderHeldPress(bool resolveInBetweenFlush)
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1", "echo harmless"); Flush(); _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            var oldAllow = Option(panel, 1);
            Press(panel, oldAllow);                                        // armed press on the harmless command, then held
            OpenClawSessions.OnEvent("exec.approval.resolved", Json("{\"id\":\"a1\",\"decision\":\"deny\"}"), DateTime.UtcNow);
            if (resolveInBetweenFlush) Flush();                            // the UI sees the empty gap
            Request("a1", "echo DANGEROUS"); Flush();                      // same id, another command on the bar
            _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            var newAllow = Option(panel, 1);
            var reused = ReferenceEquals(oldAllow, newAllow);
            Release(panel, newAllow);                                      // the release lands on whatever is under the pointer now
            await Settle();
            return (reused, Resolves(socket), panel.FindControl<TextBlock>("PromptTitle")!.Text ?? "");
        }
    }

    [AvaloniaFact]
    public async Task SameIdSwapNoGapSeenByTheUi()
    {
        var r = await SwapUnderHeldPress(false);
        Assert.True(r.frames == 0 && !r.reused, $"SAME-ID-SWAP (UI never sees an empty bar): controlsReused={r.reused}; resolve frames={r.frames}; title now: {r.title}");
    }

    [AvaloniaFact]
    public async Task SameIdSwapWithTheEmptyGapSeenByTheUi()
    {
        var r = await SwapUnderHeldPress(true);
        Assert.True(r.frames == 0 && !r.reused, $"SAME-ID-SWAP (UI sees the empty gap): controlsReused={r.reused}; resolve frames={r.frames}; title now: {r.title}");
    }

    // Pins the clearing of the remembered press on a changed request by itself: release on the OLD control,
    // the one path where the same-control comparison cannot help, so only the clear stands in the way.
    [AvaloniaFact]
    public async Task ReleaseOnTheOldControlAfterTheRequestChanged()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1", "echo harmless"); Flush(); _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            var oldAllow = Option(panel, 1);
            Press(panel, oldAllow);
            OpenClawSessions.OnEvent("exec.approval.resolved", Json("{\"id\":\"a1\",\"decision\":\"deny\"}"), DateTime.UtcNow);
            Request("a1", "echo DANGEROUS"); Flush();
            _now += ChatPanel.PromptArmDelay + TimeSpan.FromSeconds(1);
            Release(panel, oldAllow);
            await Settle();
            Assert.True(Resolves(socket) == 0, $"RELEASE-ON-OLD-CONTROL: resolve frames={Resolves(socket)}; title now: {panel.FindControl<TextBlock>("PromptTitle")!.Text}");
        }
    }
}
