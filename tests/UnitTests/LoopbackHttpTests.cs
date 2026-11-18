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

        // ...and the holder of the skipped port still answers its own request.
        _ = Task.Run(async () =>
        {
            var ctx = await holder.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });
        using var held = await http.GetAsync($"http://127.0.0.1:{heldPort}/y");
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
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
    public async Task RefusingPortRefusesWhileHeld()
    {
        // Not "immediately": a loopback refusal takes ~2s on Windows. What holds
        // on both is Refused rather than TimedOut, well inside the 75s timeout.
        using var refusing = LoopbackHttp.RefusingPort();
        var outcome = await LoopbackHttp.ProbeAsync(refusing.Port, TimeSpan.FromSeconds(10));
        Assert.Equal(LoopbackHttp.ConnectOutcome.Refused, outcome);
    }

    // Negative controls: a connect that is silently dropped, or that succeeds,
    // must not classify as Refused — otherwise the test above proves nothing.
    [Fact]
    public async Task ADroppedConnectIsATimeoutNotARefusal()
    {
        var outcome = await LoopbackHttp.ClassifyAsync(ct => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(100));
        Assert.Equal(LoopbackHttp.ConnectOutcome.TimedOut, outcome);
    }

    [Fact]
    public async Task ARefusedSocketErrorClassifiesAsRefusedAndOthersPropagate()
    {
        Assert.Equal(LoopbackHttp.ConnectOutcome.Refused, await LoopbackHttp.ClassifyAsync(
            _ => throw new SocketException((int)SocketError.ConnectionRefused), TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<SocketException>(() => LoopbackHttp.ClassifyAsync(
            _ => throw new SocketException((int)SocketError.HostUnreachable), TimeSpan.FromSeconds(1)));
        Assert.Equal(LoopbackHttp.ConnectOutcome.Connected, await LoopbackHttp.ClassifyAsync(
            _ => Task.CompletedTask, TimeSpan.FromSeconds(1)));
    }

    // The real-socket control: the pre-CB-365 shape. On macOS a bound-only port
    // drops the SYN, and the probe must call that a timeout. Skipped elsewhere:
    // Windows refuses a bound-only port (after ~2s), so the claim is macOS's.
    [MacDropsBoundOnlyFact]
    public async Task OnMacAPortBoundButNotListeningDropsInsteadOfRefusing()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port;
        Assert.Equal(LoopbackHttp.ConnectOutcome.TimedOut, await LoopbackHttp.ProbeAsync(port, TimeSpan.FromSeconds(1)));
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

internal sealed class MacDropsBoundOnlyFactAttribute : FactAttribute
{
    public MacDropsBoundOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "only macOS silently drops the SYN on a bound-only port; Windows refuses it";
    }
}
