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

// CB-270 Stage 6, the panel side with a real chat session and a real gateway
// object on an in-memory socket: a pending approval draws as a prompt with Deny
// first, each option answers exactly once and sends one resolve frame with the
// decision equal to the option's key and never allow-always, and what ends
// without us is written up. The record shapes are declared, not measured.
[Collection("Settings")]
public class ChatPanelApprovalTests : IDisposable
{
    private readonly List<string> _toClean = new();
    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
    private readonly Func<DateTime> _previousClock = ChatPanel.PromptClock;
    private DateTime _now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _key = "agent:alpha:dashboard:" + Guid.NewGuid();

    // Pending records and answered ids are process-wide and an id stays answered:
    // each case starts from none, or "a1" answered in one would be refused in the next.
    private static void Reset()
    {
        OrbweaverSettings.OpenClawEnabled = false;
        OpenClawSessions.Restart();
    }

    public ChatPanelApprovalTests()
    {
        Reset();
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OrbweaverSettings.OpenClawEnabled = true;
        ChatPanel.PromptClock = () => _now;
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>(), new Dictionary<string, string> { ["alpha"] = "Alpha" });
    }

    public void Dispose()
    {
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>(), new Dictionary<string, string>());
        Reset();
        OpenClawSessions.ForgetApprovalsForTests();
        ChatPanel.PromptClock = _previousClock;
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OrbweaverSettings.OpenClawEnabled = _enabled;
        OpenClawSessions.SetGatewayForTests(null);
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private string Row(string id, string command) =>
        "{\"id\":\"" + id + "\",\"approvalKind\":\"exec\",\"request\":{\"command\":" + JsonSerializer.Serialize(command)
        + ",\"cwd\":\"/tmp\",\"host\":\"gateway\",\"agentId\":\"alpha\",\"sessionKey\":\"" + _key + "\"},"
        + "\"createdAtMs\":" + Ms(DateTime.UtcNow) + ",\"expiresAtMs\":" + Ms(DateTime.UtcNow.AddMinutes(30)) + "}";

    private void Request(string id, string command = "ls -la /tmp") =>
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id, command)), DateTime.UtcNow);

    private static void Resolved(string id, string decision = "deny") =>
        OpenClawSessions.OnEvent("exec.approval.resolved",
            Json("{\"id\":\"" + id + "\",\"decision\":\"" + decision + "\"}"), DateTime.UtcNow);

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = r =>
            r.Method == "connect"
                ? FakeGatewaySocket.Ok(r.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.approvals" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : r.Method == "approval.resolve"
                    ? FakeGatewaySocket.Ok(r.Id, new { applied = true, approval = new { status = "denied", reason = "user" } })
                    : FakeGatewaySocket.Ok(r.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        await gateway.ConnectAsync(null, CancellationToken.None);
        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private (ChatPanel Panel, OpenClawChatSession Chat) Open(string label = "Alpha")
    {
        var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + _key, label)!;
        _toClean.Add(chat.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), chat);
        Flush();
        return (ChatPanelTestAccess.Instance!, chat);
    }

    private static Border Option(ChatPanel panel, int index) =>
        ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[index])
            .GetVisualDescendants().OfType<Border>().First();

    private static Pointer NewPointer() => new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    // A real click: a press and a release over the same option.
    private static void Click(ChatPanel panel, Border option)
    {
        var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        option.RaiseEvent(new PointerPressedEventArgs(
            option, NewPointer(), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        option.RaiseEvent(new PointerReleasedEventArgs(
            option, NewPointer(), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private static async Task SettleAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Flush();
    }

    private static List<FakeGatewaySocket.Request> Resolves(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method == "approval.resolve").ToList();

    // --- what the panel draws ---

    [AvaloniaFact]
    public void APendingApprovalShowsAsAPromptWithDenyFirstAndAllowOnceSecond()
    {
        var (panel, _) = Open();

        Request("a1", "ls -la /tmp");
        Flush();

        Assert.True(panel.FindControl<Border>("PromptBox")!.IsVisible);
        Assert.Contains("Alpha wants to run: ls -la /tmp", panel.FindControl<TextBlock>("PromptTitle")!.Text);
        Assert.Equal(2, panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children.Count);
        Assert.Equal("deny", ((ChatPromptOption)Option(panel, 0).DataContext!).Key);
        Assert.Equal("allow-once", ((ChatPromptOption)Option(panel, 1).DataContext!).Key);
        Assert.Equal("How to answer on the gateway", panel.FindControl<TextBlock>("PromptElsewhere")!.Text);
    }

    // A heredoc keeps its line breaks and tabs in the prompt, and both options stay.
    [AvaloniaFact]
    public void AMultiLineCommandIsDrawnWithItsLineBreaksAndBothOptions()
    {
        var (panel, _) = Open();

        Request("a1", "cat <<EOF\n\tone\nEOF");
        Flush();

        Assert.Contains("wants to run: cat <<EOF\n\tone\nEOF", panel.FindControl<TextBlock>("PromptTitle")!.Text);
        Assert.Equal(2, panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children.Count);
    }

    // The last line of a prompt is where an unsafe request's id sits, and it was
    // laid out away when three lines of the font's own height rounded the box down
    // to a whole pixel. Every character of the title must be in the laid-out text.
    [AvaloniaFact]
    public void EveryLineOfAThreeLinePromptIsLaidOut()
    {
        var (panel, _) = Open();

        Request("a1", "echo hi");
        Flush();
        var title = panel.FindControl<TextBlock>("PromptTitle")!;
        title.Text = OpenClawApprovalText.UnsafeRequest + "\nRequest id: 'a1'";
        Flush();

        var laidOut = title.TextLayout.TextLines.Sum(l => l.Length);
        Assert.True(laidOut >= title.Text!.Length, $"laid out {laidOut} of {title.Text.Length}");
        Assert.Equal(0, title.LineHeight % 1);
    }

    // A request whose command carries any other control character is drawn as the
    // unsafe prompt: Deny only, none of the command on screen.
    [AvaloniaFact]
    public void ACommandWithABellIsDrawnAsTheUnsafePromptWithDenyOnly()
    {
        var (panel, _) = Open();

        Request("a1", "echo \u0007EVIL");
        Flush();

        var title = panel.FindControl<TextBlock>("PromptTitle")!.Text!;
        Assert.StartsWith(OpenClawApprovalText.UnsafeRequest, title);
        Assert.DoesNotContain("EVIL", title);
        Assert.Single(panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children);
    }

    // The prompt names the agent; the chat's long label is the header's to show and
    // never reads as the agent's name.
    [AvaloniaFact]
    public void TheChatLabelDoesNotLeakIntoThePromptTitle()
    {
        var (panel, _) = Open("Lilibeth \u2014 CB-270 approval recheck \u2014 please ignore");

        Request("a1", "true");
        Flush();

        var title = panel.FindControl<TextBlock>("PromptTitle")!.Text!;
        Assert.StartsWith("Alpha wants to run: true", title);
        Assert.DoesNotContain("recheck", title);
    }

    // --- one click, one frame, the decision the option names ---

    [AvaloniaTheory]
    [InlineData(0, "deny")]
    [InlineData(1, "allow-once")]
    public async Task ClickingEachOptionOnceSendsExactlyOneResolveFrameWithThatDecision(int index, string decision)
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1");
            Flush();
            _now += ChatPanel.PromptArmDelay;

            Click(panel, Option(panel, index));
            await SettleAsync(() => Resolves(socket).Count > 0);

            var frame = Assert.Single(Resolves(socket));
            Assert.Equal(decision, frame.Params.GetProperty("decision").GetString());
            Assert.NotEqual("allow-always", frame.Params.GetProperty("decision").GetString());
            Assert.Equal("a1", frame.Params.GetProperty("id").GetString());
            Assert.False(panel.FindControl<Border>("PromptBox")!.IsVisible);
        }
    }

    // Clicked again before the first lands, or after it has: still one frame.
    [AvaloniaFact]
    public async Task ClickingTheSameOptionTwiceStillSendsOneFrame()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1");
            Flush();
            _now += ChatPanel.PromptArmDelay;
            var option = Option(panel, 0);

            Click(panel, option);
            Click(panel, option);
            await SettleAsync(() => Resolves(socket).Count > 0);
            Click(panel, option);
            Flush();

            Assert.Single(Resolves(socket));
        }
    }

    // Too soon after the prompt appears, the same click sends nothing.
    [AvaloniaFact]
    public async Task AClickBeforeTheArmingDelaySendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1");
            Flush();

            Click(panel, Option(panel, 1));
            await SettleAsync(() => false);

            Assert.Empty(Resolves(socket));
        }
    }

    // A is resolved elsewhere and B takes its place under the pointer: a click on
    // the old option, even after the delay, sends nothing for B.
    [AvaloniaFact]
    public async Task AReplacementRequestRearmsTheBarAndAnOldClickAnswersNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (panel, _) = Open();
            Request("a1", "echo A");
            Flush();
            _now += ChatPanel.PromptArmDelay;
            var oldAllow = Option(panel, 1);

            Resolved("a1");
            Request("b1", "echo B");
            Flush();

            // Pressed on the bar as A left it, released on what B drew: still armed.
            Click(panel, oldAllow);
            await SettleAsync(() => false);
            Assert.Empty(Resolves(socket));

            _now += ChatPanel.PromptArmDelay;
            Click(panel, Option(panel, 1));
            await SettleAsync(() => Resolves(socket).Count > 0);

            var frame = Assert.Single(Resolves(socket));
            Assert.Equal("b1", frame.Params.GetProperty("id").GetString());
        }
    }

    // --- what ends without us ---

    [AvaloniaFact]
    public void AnApprovalResolvedElsewhereClearsTheBarAndSaysWhoDecided()
    {
        var (panel, chat) = Open();
        Request("a1");
        Flush();
        Assert.True(panel.FindControl<Border>("PromptBox")!.IsVisible);

        Resolved("a1", "deny");
        Flush();

        Assert.False(panel.FindControl<Border>("PromptBox")!.IsVisible);
        Assert.Contains(chat.History, t => t.Text == "Denied by another reviewer");
    }

    // The link that answers "where else": a note with the id as data, and the
    // panel stays open since the note is the thing to read.
    [AvaloniaFact]
    public void TheElsewhereLinkAddsTheGatewayNoteAndKeepsThePanelOpen()
    {
        var (panel, chat) = Open();
        Request("a1");
        Flush();
        var link = panel.FindControl<TextBlock>("PromptElsewhere")!;

        link.RaiseEvent(new PointerPressedEventArgs(
            link, NewPointer(), panel, new Point(2, 2), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Flush();

        Assert.Contains(chat.History, t => t.Text.Contains("'a1'") && t.Text.Contains("approvals resolve"));
        Assert.True(panel.IsVisible);
    }
}
