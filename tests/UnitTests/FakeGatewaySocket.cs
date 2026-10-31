using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver.Tests;

// A WebSocket that answers from a script instead of from a gateway.
//
// The same idea as tests/UiTests' FakeChatSession, one layer lower down:
// OpenClawGateway is protocol on top of a socket, and WebSocket is already the
// abstraction the BCL hands it, so standing in here needs no new interface in
// the app — only the connector seam that decides which socket it gets.
//
// Frames pushed in are delivered to the receive loop in order. Frames sent out
// are recorded, and a handler can answer them synchronously, which is what makes
// a whole handshake a straight-line test rather than a race between two tasks.
internal sealed class FakeGatewaySocket : WebSocket
{
    private readonly ConcurrentQueue<Frame> _inbound = new();
    private readonly SemaphoreSlim _arrived = new(0);

    private sealed record Frame(
        byte[] Data,
        WebSocketMessageType Type = WebSocketMessageType.Text,
        bool EndOfMessage = true,
        WebSocketCloseStatus? Close = null,
        string? CloseDescription = null);

    public sealed record Request(string Id, string Method, JsonElement Params);

    // Every request the gateway sent, in order.
    public List<Request> Requests { get; } = new();

    // What to answer a request with, as an object to serialize. Null means "say
    // nothing", which is how the request-timeout path is reached.
    public Func<Request, object?>? OnRequest { get; set; }

    // Set to make the next SendAsync record the frame and then never
    // complete on its own — only the caller's own cancellation token can end
    // it. This exists for the cancelled-mid-handshake test: with the send
    // itself the thing left hanging, ConnectAsync's cancellation observes it
    // through RequestAsync's `await _socket!.SendAsync(...)`, the same
    // un-shared await SendAsync's caller is already sitting on, rather than
    // through the receive loop noticing the same token and racing
    // RequestAsync's own per-request timeout registration to the punch.
    //
    // That race is real, not theoretical: parking the *receive* side instead
    // (answering nothing, as the test used to) leaves a window between "the
    // request was recorded" and "RequestAsync has registered its own
    // cancellation handling" during which the receive loop's reaction to the
    // same cancelled token can get there first and fail the pending call
    // with "gateway connection closed" rather than the caller's own
    // "abandoned" message — CB-175 reproduced this reliably (500/500) by
    // injecting a few milliseconds of delay at exactly that point, standing
    // in for the scheduling jitter a loaded CI runner already provides for
    // free. Parking the send instead removes the shared state race
    // entirely: the only writer of the outcome is the one await this flag
    // controls.
    public bool ParkSendUntilCancelled { get; set; }

    // Set to make every send record its frame and then complete however this says.
    public Func<Task>? SendCompletes { get; set; }

    public override WebSocketState State { get; } = WebSocketState.Open;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    // ---- what the test makes the gateway see ----

    public void PushJson(string json) => Push(new Frame(Encoding.UTF8.GetBytes(json)));

    public void PushEvent(string name, object payload) =>
        PushJson(JsonSerializer.Serialize(new { @event = name, payload }));

    public void PushBytes(byte[] data, bool endOfMessage = true) =>
        Push(new Frame(data, EndOfMessage: endOfMessage));

    public void PushClose(WebSocketCloseStatus status, string? description) =>
        Push(new Frame(Array.Empty<byte>(), WebSocketMessageType.Close, true, status, description));

    private void Push(Frame frame)
    {
        _inbound.Enqueue(frame);
        _arrived.Release();
    }

    // ---- the WebSocket surface ----

    // Set to make the next receive fail the way a socket that has gone away
    // does — not cancelled, not closed cleanly, just broken.
    public Exception? BreakOnReceive { get; set; }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (BreakOnReceive is { } broken) throw broken;

        _inbound.TryDequeue(out var frame);

        if (frame!.Type == WebSocketMessageType.Close)
        {
            return new WebSocketReceiveResult(
                0, WebSocketMessageType.Close, true, frame.Close, frame.CloseDescription);
        }

        frame.Data.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(frame.Data.Length, frame.Type, frame.EndOfMessage);
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        var json = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var request = new Request(
            root.GetProperty("id").GetString()!,
            root.GetProperty("method").GetString()!,
            root.GetProperty("params").Clone());

        Requests.Add(request);

        // The send is on the wire and does not finish until the test says how it
        // ends: what a real socket does when the connection is torn down under a
        // request that was already being written.
        if (SendCompletes is { } completes) return completes();

        if (ParkSendUntilCancelled)
        {
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => parked.TrySetCanceled(cancellationToken));
            return parked.Task;
        }

        var answer = OnRequest?.Invoke(request);
        if (answer is not null) PushJson(JsonSerializer.Serialize(answer));

        return Task.CompletedTask;
    }

    // A success frame in the gateway's own shape, so a test says what it means
    // rather than assembling an envelope every time.
    public static object Ok(string id, object payload) => new { type = "res", id, ok = true, payload };

    public static object Error(string id, string code, string message, string? detail) => new
    {
        type = "res",
        id,
        ok = false,
        error = new { code, message, details = new { code = detail } }
    };

    public override void Abort() { }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) =>
        Task.CompletedTask;

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) =>
        Task.CompletedTask;

    public override void Dispose() => _arrived.Dispose();
}

// A socket whose send always fails, for the one path that has to leave nothing
// behind: a request that never left the machine must not sit in the pending
// table pretending to be in flight.
internal sealed class FailingSendSocket : WebSocket
{
    public override WebSocketState State => WebSocketState.Open;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct).ContinueWith(
            _ => new WebSocketReceiveResult(0, WebSocketMessageType.Close, true),
            TaskScheduler.Default);

    public override Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType type, bool eom, CancellationToken ct) =>
        throw new WebSocketException("the socket is gone");

    public override void Abort() { }

    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) =>
        Task.CompletedTask;

    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) =>
        Task.CompletedTask;

    public override void Dispose() { }
}
