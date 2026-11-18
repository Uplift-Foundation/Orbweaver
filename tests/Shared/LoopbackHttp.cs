using System.Net;
using System.Net.Sockets;

namespace Orbweaver.Tests;

// A loopback HttpListener on a port nobody else holds, and a port nobody is
// listening on — without the probe-and-release race.
//
// The old pattern (CB-314, found by QA's full IntegrationTests run: seven
// "Address already in use" failures) bound a TcpListener to port 0, read the
// port, released it, then bound the HttpListener to it. Between the release
// and the bind, any test running in parallel could take the same port. A
// TcpListener can bind 0 and keep it, but HttpListener cannot bind 0 at all,
// so instead the listener binds a random port itself and retries on a
// collision: the port it ends up with is one it actually holds, from the
// first moment anyone learns its number.
internal static class LoopbackHttp
{
    internal static (HttpListener Listener, string Root) Start() =>
        Start(() => Random.Shared.Next(20000, 60000));

    // The port source is a seam so the retry can be driven deterministically:
    // a test hands it a port it already holds and then a free one, instead of
    // hoping a collision happens. Production callers use the overload above.
    internal static (HttpListener Listener, string Root) Start(Func<int> nextPort)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = nextPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://127.0.0.1:{port}");
            }
            catch (HttpListenerException)
            {
                // Close on the final failure too, so giving up leaks nothing.
                listener.Close();
                if (attempt >= 50) throw;
            }
        }
    }

    internal enum ConnectOutcome { Refused, Connected, TimedOut }

    // Classifies one connect attempt, bounded by `timeout`, so a SYN that is
    // silently dropped cannot be mistaken for a refusal. Only an RST
    // (ConnectionRefused) is Refused; any other socket error propagates.
    internal static async Task<ConnectOutcome> ClassifyAsync(Func<CancellationToken, Task> connect, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await connect(cts.Token).ConfigureAwait(false);
            return ConnectOutcome.Connected;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ConnectOutcome.Refused;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return ConnectOutcome.TimedOut;
        }
    }

    internal static async Task<ConnectOutcome> ProbeAsync(int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        return await ClassifyAsync(ct => client.ConnectAsync(IPAddress.Loopback, port, ct).AsTask(), timeout)
            .ConfigureAwait(false);
    }

    // A loopback port on which connecting is refused with an RST, immediately.
    //
    // CB-365: this used to bind a socket and never listen, which refuses on
    // Windows but on macOS drops the SYN — measured on osx-arm64, a connect to
    // a bound-only port hangs until the ~75s TCP connect timeout, while a
    // released port answers ECONNREFUSED in 0.0s. A listening socket accepts
    // instead (even shut down or with a full backlog), so there is no way to
    // *hold* a refusing port on macOS. Instead the port is probed and released,
    // from 10000-19999: below Start()'s 20000-60000 range and below every OS's
    // ephemeral range, so nothing in this test process can bind it meanwhile.
    // Each candidate is verified with a bounded connect, so a platform that
    // drops rather than refuses is skipped rather than hung on.
    internal static int RefusingPort()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var port = Random.Shared.Next(10000, 20000);
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException) { continue; }

            if (ProbeAsync(port, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult() == ConnectOutcome.Refused)
                return port;
        }
        throw new InvalidOperationException("no loopback port in 10000-19999 refused a connection");
    }
}
