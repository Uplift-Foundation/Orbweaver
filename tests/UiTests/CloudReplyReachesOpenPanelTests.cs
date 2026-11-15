using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-285, as the user met it: a panel open on a cloud session, a message typed
// and sent with Enter, and the reply never appearing in that panel.
//
// Driven through the real ClaudeCloudChatSession and the real ChatPanel, the
// session posting to the real dispatcher, against a fake API whose status reads
// follow the shape measured on a cold session: idle *before* the turn starts.
// The loop's ticks are handed out one at a time, so the test can look at the
// panel between them. On develop the first idle read took Stop away and no
// later read ever happened, so the reply never reached the panel.
//
// The decision log is not asserted here (the unit cases do that); the
// TestBootstrap points it at scratch.
[Collection("Settings")]
public class CloudReplyReachesOpenPanelTests : IDisposable
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private const string Receipt =
        """{"results":[{"duplicate":false,"sequence_num":"20","event_id":"e"}]}""";

    private const string AssistantTemplate =
        """{"type":"assistant","uuid":"UUID","timestamp":"2026-11-14T10:00:05Z","message":{"role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":10,"output_tokens":20},"stop_reason":"end_turn"}}""";

    private const string ReplyText = "the reply reached the panel";

    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            FlushRender();
            await Task.Delay(5);
        }

        FlushRender();
        Assert.True(condition(), "the panel did not reach the expected state within five seconds");
    }

    private static Control StopButton(ChatPanel panel) => panel.FindControl<Grid>("StopButton")!;

    private static TextBox Input(ChatPanel panel) => panel.FindControl<TextBox>("Input")!;

    private static void PressEnter(TextBox input) =>
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private static string ShownText(ChatPanel panel) =>
        string.Concat(panel.FindControl<ItemsControl>("Turns")!.GetVisualDescendants().OfType<TextBlock>()
            .Select(tb => !string.IsNullOrEmpty(tb.Text)
                ? tb.Text
                : string.Concat((tb.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
                    .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text))));

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";

        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "a credential is present");
    }

    // A cloud session whose status reads answer `buckets` in order (the last
    // repeated), and whose transcript holds the sent message's echo and a reply
    // only once `ran` says the turn has happened. The stream's starting-point
    // read is answered too, for the stream case.
    private sealed class ColdSession : ICloudApi
    {
        private readonly string[] _buckets;
        private string? _sent;

        internal ColdSession(params string[] buckets) => _buckets = buckets;

        internal int StatusReads;
        internal int TranscriptReads;
        internal Func<bool> Ran { get; set; } = () => false;

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            string body;

            if (context.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(context.Body!);
                _sent = doc.RootElement.GetProperty("events")[0].GetProperty("payload").GetRawText();
                body = Receipt;
            }
            else if (context.Path.EndsWith("?limit=1&sort_order=desc", StringComparison.Ordinal))
            {
                body = "{\"data\":[{\"sequence_num\":\"7\"}]}";
            }
            else if (context.Path.StartsWith(CloudRequest.CodeSessionsPath + "/", StringComparison.Ordinal)
                     && !context.Path.Contains("/events", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref StatusReads);
                body = "{\"status_bucket\":\"" + _buckets[Math.Min(n, _buckets.Length) - 1] + "\"}";
            }
            else
            {
                Interlocked.Increment(ref TranscriptReads);
                var rows = Ran() && _sent is { } echo
                    ? new[] { echo, AssistantTemplate.Replace("UUID", "a1").Replace("TEXT", ReplyText) }
                    : Array.Empty<string>();
                body = "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":false,\"last_id\":null}";
            }

            return Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), body));
        }
    }

    // A connection that opens and then says nothing at all — the dead socket
    // HttpCloudEventStream has no read timeout to notice.
    private sealed class SilentStream : ICloudEventStream
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Opened => _opened.Task;

        public async IAsyncEnumerable<CloudStreamEvent> OpenAsync(string accessToken, string sessionId,
            long? fromSequenceNum, [EnumeratorCancellation] CancellationToken ct)
        {
            _opened.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    private ClaudeCloudChatSession Cold(string prefix, ColdSession api,
        Func<TimeSpan, CancellationToken, Task> delay, ICloudEventStream? stream = null)
    {
        var id = prefix + Guid.NewGuid().ToString("N");
        _toClean.Add(id);
        var row = new ClaudeCloudSessions.Session(id, "a cloud session", "idle",
            new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

        return new ClaudeCloudChatSession(row, api, new FakeCredentials(),
            action => Dispatcher.UIThread.Post(action))
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Delay = delay,
            Enabled = () => true,
            Stream = stream,
        };
    }

    private static async Task<ChatPanel> OpenAndSend(ClaudeCloudChatSession chat)
    {
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), chat);
        FlushRender();
        var panel = ChatPanelTestAccess.Instance!;

        var input = Input(panel);
        input.Text = "are you there?";
        PressEnter(input);
        await PumpUntil(() => StopButton(panel).IsVisible);
        return panel;
    }

    // Case 16. Idle, then the turn: the reply's bubble appears in the open panel
    // without rebinding, and Stop stays up until the turn is really over.
    [AvaloniaFact]
    public async Task AReplyAfterAnEarlyIdleReachesTheOpenPanel()
    {
        var api = new ColdSession("idle", "working", "working", "idle");
        api.Ran = () => Volatile.Read(ref api.StatusReads) >= 4;
        var ticks = new SemaphoreSlim(0);
        var chat = Cold("session_cold_", api, (_, ct) => ticks.WaitAsync(ct));

        var panel = await OpenAndSend(chat);

        // The early idle: the session has not started the turn, and Stop stays.
        ticks.Release();
        await PumpUntil(() => api.TranscriptReads >= 1 && Volatile.Read(ref api.StatusReads) == 1);
        FlushRender();
        Assert.True(StopButton(panel).IsVisible);
        Assert.DoesNotContain(ReplyText, ShownText(panel));

        // The turn runs...
        ticks.Release(2);
        await PumpUntil(() => Volatile.Read(ref api.StatusReads) == 3);
        Assert.True(StopButton(panel).IsVisible);

        // ...and ends, and its reply is in the panel.
        ticks.Release();
        await PumpUntil(() => ShownText(panel).Contains(ReplyText) && !StopButton(panel).IsVisible);

        ChatPanel.HideFor(chat.SessionId);
        FlushRender();
        await chat.LiveTask!;
    }

    // Case 17. The stream connects, is trusted, and says nothing about the send.
    // After the echo grace the panel polls beside it and the reply arrives.
    [AvaloniaFact]
    public async Task AReplyBehindASilentStreamReachesTheOpenPanel()
    {
        var api = new ColdSession("idle") { Ran = () => true };
        var grace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new SilentStream();
        var chat = Cold("session_silent_", api,
            (wait, ct) => wait == ClaudeCloudChatSession.UnmeasuredStreamEchoGrace
                ? grace.Task.WaitAsync(ct)
                : Task.CompletedTask,
            stream);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), chat);
        FlushRender();
        await stream.Opened.WaitAsync(TimeSpan.FromSeconds(10));
        var panel = ChatPanelTestAccess.Instance!;

        var input = Input(panel);
        input.Text = "are you there?";
        PressEnter(input);
        await PumpUntil(() => StopButton(panel).IsVisible);

        // Trusted and silent: nothing polls yet.
        Assert.Equal(0, Volatile.Read(ref api.StatusReads));

        grace.SetResult();
        await PumpUntil(() => ShownText(panel).Contains(ReplyText) && !StopButton(panel).IsVisible);
        Assert.True(Volatile.Read(ref api.StatusReads) >= 1);

        ChatPanel.HideFor(chat.SessionId);
        FlushRender();
        await chat.StreamTask!;
    }
}
