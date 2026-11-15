using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Orbweaver.Tests;

// CB-292: pins what LoopbackHttp's header comment claims — that it survives a
// port somebody else holds — and shows the pattern it replaced does not.
public class LoopbackHttpTests
{
    private static (HttpListener Listener, int Port) Hold()
    {
        var (listener, root) = LoopbackHttp.Start();
        return (listener, new Uri(root).Port);
    }

    [Fact]
    public async Task StartSkipsAHeldPortAndReturnsAFreeOne()
    {
        var (holder, heldPort) = Hold();
        using var _h = holder;
        // Ports the source will offer after the held one: keep offering until one
        // binds, so the case never depends on a particular port being free.
        var offered = new List<int>();
        var rng = new Random(1234);
        int Next()
        {
            var p = offered.Count == 0 ? heldPort : rng.Next(20000, 60000);
            offered.Add(p);
            return p;
        }

        var (listener, root) = LoopbackHttp.Start(Next);
        using var _l = listener;

        Assert.Equal(heldPort, offered[0]);
        Assert.True(offered.Count >= 2, "the held port must have been tried and skipped");
        Assert.NotEqual($"http://127.0.0.1:{heldPort}", root);
        Assert.Equal($"http://127.0.0.1:{offered[^1]}", root);

        // The returned listener is the one answering at its root...
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
        });
        using var http = new HttpClient();
        using var res = await http.GetAsync(root + "/x");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.True(holder.IsListening);
    }

    [Fact]
    public void TheOldProbeAndReleasePatternThrowsOnAHeldPort()
    {
        // Negative control: the old sequence, with the probed port taken in the
        // window between release and bind (here: already held). This is the
        // failure LoopbackHttp.Start exists to avoid.
        var (holder, heldPort) = Hold();
        using var _h = holder;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{heldPort}/");
        Assert.Throws<HttpListenerException>(() => listener.Start());
    }

    [Fact]
    public void RefusingPortRefusesConnectionsWhileHeld()
    {
        var (holder, port) = LoopbackHttp.RefusingPort();
        using var _h = holder;
        using var client = new TcpClient();
        Assert.Throws<SocketException>(() => client.Connect(IPAddress.Loopback, port));
    }

    [Fact]
    public void StartGivesUpAfterFiftyCollisions()
    {
        var (holder, heldPort) = Hold();
        using var _h = holder;
        var calls = 0;
        Assert.Throws<HttpListenerException>(() => LoopbackHttp.Start(() => { calls++; return heldPort; }));
        Assert.Equal(51, calls);
    }
}
