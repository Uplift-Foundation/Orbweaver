using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// Quitting while requests are in flight. crash.log held 471 unobserved
// ObjectDisposedExceptions on a SemaphoreSlim across twelve processes, all at
// shutdown. The gateway's send gate is the one that can be reached without a TLS
// peer: a request holding the gate while its frame is on the wire, the gateway
// disposed under it, and the send then ending the way a torn-down connection ends
// it. The request's `finally` released a semaphore Dispose had already disposed,
// and that ObjectDisposedException replaced the real failure — and, for a caller
// that fires a request and does not await it, was never observed by anyone.
[Collection("Settings")]
public class OpenClawGatewayShutdownTests
{
    private static bool IsDisposedSemaphore(Exception e) => e switch
    {
        ObjectDisposedException ode => ode.ObjectName == "System.Threading.SemaphoreSlim",
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(IsDisposedSemaphore),
        _ => false
    };

    private static async Task<(OpenClawGateway Gateway, FakeGatewaySocket Socket, TaskCompletionSource Send)> ConnectedWithASendOnTheWireAsync()
    {
        var socket = OpenClawGatewayTests.Accepting();
        var gateway = OpenClawGatewayTests.Gateway(socket);
        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.SendCompletes = () => send.Task;
        return (gateway, socket, send);
    }

    private static async Task WaitForSendsAsync(FakeGatewaySocket socket, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (socket.Requests.Count < count + 1 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(socket.Requests.Count >= count + 1, "the request never reached the socket");
    }

    // Started and forgotten, in a frame of its own so nothing here keeps the task
    // reachable: the collection below has to be able to finalize it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FireAndForget(OpenClawGateway gateway, int count)
    {
        for (var i = 0; i < count; i++) _ = gateway.RequestAsync("sessions.list", null, CancellationToken.None);
    }

    private static List<Exception> CollectUnobserved(Action run)
    {
        var seen = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (seen) seen.Add(e.Exception);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            run();

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (seen) return seen.ToList();
    }

    [Fact]
    public async Task ARequestWhoseSendFailsAfterDisposeReportsTheSendsFailureNotADisposedSemaphore()
    {
        var (gateway, socket, send) = await ConnectedWithASendOnTheWireAsync();
        var request = gateway.RequestAsync("sessions.list", null, CancellationToken.None);
        await WaitForSendsAsync(socket, 1);

        gateway.Dispose();
        send.SetException(new IOException("connection aborted"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => request);
        Assert.False(IsDisposedSemaphore(failure), "the gate was disposed under the request that held it: " + failure);
        Assert.IsType<IOException>(failure);
    }

    [Fact]
    public async Task AFireAndForgetRequestCaughtByShutdownLeavesNoUnobservedDisposedSemaphore()
    {
        var (gateway, socket, send) = await ConnectedWithASendOnTheWireAsync();

        var unobserved = CollectUnobserved(() =>
        {
            FireAndForget(gateway, 1);
            WaitForSendsAsync(socket, 1).GetAwaiter().GetResult();

            gateway.Dispose();
            send.SetException(new IOException("connection aborted"));
            Thread.Sleep(100);
        });

        Assert.DoesNotContain(unobserved, IsDisposedSemaphore);
    }

    // Many requests queued behind the one on the wire, all fired and forgotten, and
    // the gateway disposed under the lot: nothing that was waiting on the gate may
    // be left waiting on a semaphore that no longer exists.
    [Fact]
    public async Task QuittingUnderLoadLeavesNoUnobservedDisposedSemaphoreAndNoRequestHanging()
    {
        var (gateway, socket, send) = await ConnectedWithASendOnTheWireAsync();
        var held = new List<Task>();
        for (var i = 0; i < 40; i++) held.Add(gateway.RequestAsync("sessions.list", null, CancellationToken.None));
        await WaitForSendsAsync(socket, 1);

        var unobserved = CollectUnobserved(() =>
        {
            gateway.Dispose();
            send.SetException(new IOException("connection aborted"));

            var finished = Task.WhenAll(held.Select(t => t.ContinueWith(_ => { }))).Wait(TimeSpan.FromSeconds(10));
            Assert.True(finished, "a request was left waiting after the gateway was disposed");

            held.Clear();
        });

        Assert.DoesNotContain(unobserved, IsDisposedSemaphore);
    }

    // A request made after the gateway is gone fails the way every other request on a
    // dead connection does, rather than on whatever the disposed gate throws.
    [Fact]
    public async Task ARequestAfterDisposeFailsAsAClosedConnection()
    {
        var socket = OpenClawGatewayTests.Accepting();
        var gateway = OpenClawGatewayTests.Gateway(socket);
        await gateway.ConnectAsync(null, CancellationToken.None);

        gateway.Dispose();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => gateway.RequestAsync("sessions.list", null, CancellationToken.None));
        Assert.IsType<IOException>(failure);
        Assert.Contains("closed", failure.Message);
    }
}
