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
    internal static (HttpListener Listener, string Root) Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://127.0.0.1:{port}");
            }
            catch (HttpListenerException) when (attempt < 50)
            {
                listener.Close();
            }
        }
    }

    // A port bound and held by this socket but never listened on, so a
    // connection to it is refused — and, unlike a probed-then-released port,
    // no parallel test can start listening there while the caller relies on
    // the refusal. Dispose the socket to free it.
    internal static (Socket Holder, int Port) RefusingPort()
    {
        var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (holder, ((IPEndPoint)holder.LocalEndPoint!).Port);
    }
}
