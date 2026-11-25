using Xunit;

namespace Orbweaver.UnitTests;

// CB-408: one slow request served for a peer must not hold the frames behind it.
// The link hands messages to the host one at a time, so what the host awaits
// inline is what every later frame waits for; these drive DeliverAsync the way
// that queue does, with the slow serve gated on a TaskCompletionSource.
public class PeerServeOffQueueTests
{
    private const string Peer = "mini";
    private const string Name = "job-hunter";

    private sealed class Harness
    {
        public readonly TaskCompletionSource HoldPrompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource PromptReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<(string Type, string Id, string? Code)> Sent = [];
        public readonly PeerMirrorHost Host = new();
        public Func<string, bool> FailSend = _ => false;

        public Harness()
        {
            var agents = new List<AgentRoster.Entry> { new(Name, "s1", 4242) };
            var sessions = new List<(string, SessionStatus)>
            {
                ("s1", new SessionStatus { Title = Name, Cwd = "/tmp", Source = SessionSource.ClaudeCode, TmuxPane = "%1", SessionPid = 4242 }),
            };

            var server = new RemoteMirrorServer(".claude", new RemoteMirrorServer.Seams(
                (_, frame) =>
                {
                    var parsed = MirrorProtocol.TryParseFrame(frame)!;
                    if (FailSend(parsed.Id)) throw new InvalidOperationException("write failed");
                    lock (Sent) Sent.Add((parsed.Type, parsed.Id, parsed.Text("c")));
                    return Task.FromResult(true);
                },
                () => sessions,
                () => agents,
                _ => true,
                _ => true,
                (_, _) => Task.FromResult(true),
                PeerAllowed: _ => true,
                UserConfigDirs: () => Array.Empty<string>(),
                CapturePane: async _ =>
                {
                    PromptReading.TrySetResult();
                    await HoldPrompt.Task;
                    return null;
                },
                CanReadPane: _ => true));

            Host.Bind(new RemoteMirrorClient(".claude", new RemoteMirrorClient.Seams((_, _) => Task.FromResult(true))), server);
        }

        public Task Deliver(string type, string id, Dictionary<string, string>? fields = null) =>
            Host.DeliverAsync(Peer, PeerProtocol.Message(PeerProtocol.Fetch, PeerProtocol.NewId(),
                body: PeerProtocol.BodyOf(MirrorProtocol.BuildFrame(type, id, fields))));

        public async Task<bool> SentAsync(string id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (Sent) if (Sent.Any(s => s.Id == id)) return true;
                await Task.Delay(5);
            }

            return false;
        }

        public string[] SentIds { get { lock (Sent) return Sent.Select(s => s.Id).ToArray(); } }
    }

    // The link's queue: one message delivered after the other is finished with.
    private static Task Queue(Harness h, params Func<Task>[] deliveries) =>
        Task.Run(async () => { foreach (var d in deliveries) await d(); });

    private static readonly Dictionary<string, string> Named = new() { ["n"] = MirrorProtocol.Encode("sid:s1") };

    [Fact]
    public async Task ARosterRequestIsAnsweredWhileAPromptIsBeingServed()
    {
        var h = new Harness();
        var queue = Queue(h, () => h.Deliver(MirrorProtocol.Prompt, "p1", Named), () => h.Deliver(MirrorProtocol.Hello, "h1"));

        await h.PromptReading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var answered = await h.SentAsync("h1");

        Assert.False(h.HoldPrompt.Task.IsCompleted);
        Assert.True(answered, "the roster waited for the held PROMPT");
        Assert.DoesNotContain("p1", h.SentIds);

        h.HoldPrompt.SetResult();
        Assert.True(await h.SentAsync("p1"));
        await queue;
    }

    [Fact]
    public async Task ASecondPromptAndAnInputAreAnsweredWhileAPromptIsBeingServed()
    {
        var h = new Harness();
        var queue = Queue(h,
            () => h.Deliver(MirrorProtocol.Prompt, "p1", Named),
            () => h.Deliver(MirrorProtocol.Prompt, "p2", new() { ["n"] = MirrorProtocol.Encode("sid:none") }),
            // No payload: refused at once with bad-hash, which is a reply to wait for.
            () => h.Deliver(MirrorProtocol.Input, "i1", Named));

        Assert.True(await h.SentAsync("p2"), "the second PROMPT waited for the first");
        Assert.True(await h.SentAsync("i1"), "the INPUT waited for the held PROMPT");
        Assert.DoesNotContain("p1", h.SentIds);

        h.HoldPrompt.SetResult();
        await queue;
    }

    [Fact]
    public async Task InputsAndAnswersKeepTheirOrderAmongThemselves()
    {
        var h = new Harness();
        var ids = Enumerable.Range(0, 25).Select(i => (i % 2 == 0 ? "i" : "a") + i).ToArray();
        var queue = Queue(h, ids.Select<string, Func<Task>>(id => () =>
            h.Deliver(id[0] == 'i' ? MirrorProtocol.Input : MirrorProtocol.Answer, id, Named)).ToArray());

        await queue;
        Assert.True(await h.SentAsync(ids[^1]));
        Assert.Equal(ids, h.SentIds);
    }

    [Fact]
    public async Task AServeThatThrowsIsLoggedAndDoesNotStopTheNextOne()
    {
        var h = new Harness { FailSend = id => id is "bad-hello" or "bad-input" };

        await h.Deliver(MirrorProtocol.Hello, "bad-hello");
        await h.Deliver(MirrorProtocol.Input, "bad-input", Named);
        await h.Deliver(MirrorProtocol.Input, "good-input", Named);

        Assert.True(await h.SentAsync("good-input"));
        Assert.DoesNotContain("bad-hello", h.SentIds);
        Assert.DoesNotContain("bad-input", h.SentIds);
    }
}
