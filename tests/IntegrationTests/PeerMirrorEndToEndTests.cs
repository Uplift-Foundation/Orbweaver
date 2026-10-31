using Xunit;

namespace Orbweaver.IntegrationTests;

// The whole mirror, over a real TLS socket, with nothing faked between the two
// halves but the machines themselves.
//
// **This is the test the plan turns on.** Every other assertion about the peer
// link is about a part of it — framing, trust, the read loop. This one takes the
// unchanged RemoteMirrorClient and RemoteMirrorServer, puts a PeerLink between
// them instead of a hidden Claude Code session, and asks whether a transcript
// still arrives. If it does, the transport swap altered no behaviour, which is
// the only claim worth making at this step.
//
// What it replaces is worth restating: the same exchange over the relay cost a
// model turn per frame and was measured at 222 to 247 seconds for a single 6KB
// chunk, with at least one chunk arriving corrupted.
[Collection("Settings")]
public class PeerMirrorEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "peer-mirror-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<PeerMirrorHost> _hosts = new();

    public PeerMirrorEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var host in _hosts) host.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PeerMirrorHost NewHost(PeerMirrorHost.OpenClawIdentitySeams? identity = null)
    {
        var host = new PeerMirrorHost(identity);
        _hosts.Add(host);
        return host;
    }

    // The far machine: a Buddy with one Claude Code session whose transcript is
    // on its disk, serving whatever is asked of it.
    private (PeerMirrorHost Host, RemoteMirrorServer Server) Serving(params string[] lines)
    {
        var path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        var sessionId = Guid.NewGuid().ToString();

        var sessions = new List<(string, SessionStatus)>
        {
            (sessionId, new SessionStatus
            {
                Title = "job-hunter",
                Cwd = _dir,
                Source = SessionSource.ClaudeCode,
                TranscriptPath = path,
                TmuxPane = "%1",
                SessionPid = 4242
            })
        };

        var agents = new List<AgentRoster.Entry> { new("job-hunter", sessionId, 4242) };

        var host = NewHost();

        var server = new RemoteMirrorServer("acct", new RemoteMirrorServer.Seams(
            SendFrame: host.SendFrameAsync,
            LocalSessions: () => sessions,
            Agents: () => agents,
            ReplyEnabled: _ => true,
            CanType: _ => true,
            TypeInto: (_, _) => Task.FromResult(true),
            PeerAllowed: host.MayAsk,
            UserConfigDirs: () => Array.Empty<string>()));

        host.Bind(new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(host.SendFrameAsync)), server);

        return (host, server);
    }

    // This machine: a Buddy with a panel open on the far session.
    private (PeerMirrorHost Host, RemoteMirrorClient Client) Watching()
    {
        var host = NewHost();
        var client = new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(host.SendFrameAsync));

        var server = new RemoteMirrorServer("acct", new RemoteMirrorServer.Seams(
            SendFrame: host.SendFrameAsync,
            LocalSessions: () => new List<(string, SessionStatus)>(),
            Agents: () => new List<AgentRoster.Entry>(),
            ReplyEnabled: _ => true,
            CanType: _ => false,
            TypeInto: (_, _) => Task.FromResult(false),
            PeerAllowed: host.MayAsk,
            UserConfigDirs: () => Array.Empty<string>()));

        host.Bind(client, server);
        return (host, client);
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";

    private static string AssistantRow(string uuid, string text) =>
        $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";


    [Fact]
    public async Task ATranscriptCrossesTheLinkAndArrivesAsTurns()
    {
        var far = Serving(
            UserRow("u1", "what is it working on?"),
            AssistantRow("a1", "Batch 33 launched (RUN_ID 410)."),
            UserRow("u2", "thanks"));

        var near = Watching();

        // What a person does once, in Settings: agree that this machine and that
        // one may talk. Both ends run in this process and so share one identity
        // file, which means one certificate and therefore one pin — the same
        // situation as two machines that have paired.
        //
        // **Both directions, because pairing is mutual and the greeting made
        // that visible.** The dialling side has to trust the certificate it is
        // offered; the accepting side has to trust the machine that greets it,
        // and a greeting names the *real* machine — MachineNames.Mine() —
        // rather than the label the dial used. Remembering only "far" left the
        // far end refusing a connection it had already completed a TLS
        // handshake on, which arrived here as a session that was never
        // Available and no explanation anywhere.
        //
        // MachineNames.Mine() rather than Environment.MachineName, and the two
        // are not the same on macOS: the greeting says what the app calls this
        // machine, which is its LocalHostName and not its gethostname. Seeding
        // the wrong one leaves the far end refusing a connection it has already
        // completed a TLS handshake on.
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "far"));
        PeerIdentity.Remember(
            new PeerIdentity.Peer(PeerIdentity.OwnPin(), MachineNames.Mine()));

        far.Host.Link.Listen(0);
        var port = far.Host.Link.BoundPort;

        Assert.True(
            await near.Host.Link.ConnectAsync("far", "127.0.0.1", port,
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token),
            "the two Buddies could not connect");

        var painted = new TaskCompletionSource<RemoteMirrorClient.MirrorRows>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        near.Client.Delivered += rows => painted.TrySetResult(rows);

        // Told who to ask directly, which is what a direct link knows — the
        // relay-shaped peer list this used to require is the coupling that came
        // out for this to be possible.
        await near.Client.DiscoverAsync(new[] { "far" }, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            near.Client.StateFor("job-hunter").Availability);

        Assert.True(await near.Client.OpenAsync("job-hunter"));

        var window = await painted.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The far machine's actual conversation, read off its disk and parsed by
        // the same ChatTranscript a local panel uses.
        Assert.Equal(3, window.Turns.Count);
        Assert.Contains(window.Turns, t => t.Text.Contains("Batch 33 launched"));
        Assert.Contains(window.Turns, t => t.Text.Contains("what is it working on?"));
    }

    // A peer that never connected is refused by the server's own gate, which on
    // this transport asks "is there a connection?" rather than "does the name
    // look right?" — the difference between a boundary and a guard.
    [Fact]
    public void AMachineWithNoConnectionMayNotAsk()
    {
        var far = Serving(UserRow("u1", "hello"));

        Assert.False(far.Host.MayAsk("someone-else"));
    }

    // Two PeerMirrorHosts and a real TLS socket are deliberately involved here:
    // injecting the receiving cache would prove its lookup and leave the new
    // request/response routing untested. The source seam models only the local
    // workspace resolver on the other machine; the receiving seam calls the
    // production apply path after the response has crossed the link.
    [Fact]
    public async Task APairedHostRoutesOnlySamePinnedKnownAgentVoicesToTheReceiver()
    {
        var savedPin = OrbweaverSettings.OpenClawFingerprint;
        const string gatewayPin = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var option = new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.Neural,
            "af_bella", "af_bella (Kokoro)");
        TaskCompletionSource<IReadOnlyList<OpenClawPeerIdentity.Row>>? applied = null;
        try
        {
            // Do not let the connection callback send an automatic request: the
            // three explicit requests below are the observations this test makes.
            OrbweaverSettings.OpenClawFingerprint = "";
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["main"] = new("Gateway Main", null, null),
            });

            var source = NewHost(new PeerMirrorHost.OpenClawIdentitySeams(
                Resolve: (pin, ids) => pin == gatewayPin && ids.SequenceEqual(new[] { "main" })
                    ? new[] { new OpenClawPeerIdentity.Row("main", "af_bella", 1.3) }
                    : Array.Empty<OpenClawPeerIdentity.Row>(),
                Apply: (_, _, _) => { }));
            var receiver = NewHost(new PeerMirrorHost.OpenClawIdentitySeams(
                Resolve: (_, _) => Array.Empty<OpenClawPeerIdentity.Row>(),
                Apply: (peer, pin, rows) =>
                {
                    OpenClawSessions.ApplyPeerProfileVoices(peer, pin, rows);
                    applied?.TrySetResult(rows);
                }));

            PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "profile-source"));
            PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), MachineNames.Mine()));
            source.Link.Listen(0);
            Assert.True(await receiver.Link.ConnectAsync("profile-source", "127.0.0.1",
                source.Link.BoundPort, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));

            OrbweaverSettings.OpenClawFingerprint = gatewayPin;

            async Task<IReadOnlyList<OpenClawPeerIdentity.Row>> Ask(string pin, params string[] ids)
            {
                applied = new TaskCompletionSource<IReadOnlyList<OpenClawPeerIdentity.Row>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                await receiver.RequestOpenClawProfileVoicesAsync(pin, ids);
                return await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            // A different gateway and an id which the source did not resolve
            // each produce an empty routed response, leaving global fallback.
            Assert.Empty(await Ask(new string('b', 64), "main"));
            Assert.Null(OpenClawSessions.VoiceForSession("openclaw:agent:main:room", new[] { option }));
            Assert.Empty(await Ask(gatewayPin, "unknown"));
            Assert.Null(OpenClawSessions.VoiceForSession("openclaw:agent:main:room", new[] { option }));

            Assert.Equal(new[] { new OpenClawPeerIdentity.Row("main", "af_bella", 1.3) },
                await Ask(gatewayPin, "main"));
            Assert.Equal(option, OpenClawSessions.VoiceForSession("openclaw:agent:main:room", new[] { option }));
            Assert.Equal(1.3, OpenClawSessions.RateForSession("openclaw:agent:main:room"));
        }
        finally
        {
            OrbweaverSettings.OpenClawFingerprint = savedPin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    [Fact]
    public async Task AnUnconnectedMachineCannotInvokeEitherProfileRoutingDirection()
    {
        var resolved = 0;
        var applied = 0;
        var host = NewHost(new PeerMirrorHost.OpenClawIdentitySeams(
            Resolve: (_, _) => { resolved++; return Array.Empty<OpenClawPeerIdentity.Row>(); },
            Apply: (_, _, _) => applied++));
        var pin = new string('a', 64);

        await host.DeliverAsync("not-paired", PeerProtocol.Message(PeerProtocol.OpenClawIdentityGet,
            "request", body: PeerProtocol.BodyOf(new OpenClawPeerIdentity.Request(pin, new[] { "main" }))));
        await host.DeliverAsync("not-paired", PeerProtocol.Message(PeerProtocol.OpenClawIdentity,
            "response", body: PeerProtocol.BodyOf(new OpenClawPeerIdentity.Response(pin,
                new[] { new OpenClawPeerIdentity.Row("main", "af_bella") }))));

        Assert.Equal(0, resolved);
        Assert.Equal(0, applied);
    }
}
