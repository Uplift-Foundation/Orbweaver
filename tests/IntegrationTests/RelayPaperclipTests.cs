using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-428 over a real socket against RelayStub: a Paperclip agent's approval becomes a prompt a phone can
/// answer (the push amber and its sealed e on the wire), the answer is verified and carried out through the
/// actions seam, the resolve goes back; a break rides push broke; and a relay that refuses push broke costs
/// the phone the alert and never the delta.
/// </summary>
public class RelayPaperclipTests : IAsyncLifetime
{
    private const string AgentId = "3f2b8c1e-7a44-4d0e-9b61-5c2d8e0f1a77";
    private const string Sid = "paperclip:" + AgentId;
    private const string ApprovalId = "a1f0c9d2-0b3e-4f55-8d6a-9e7c1b2a3d44";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-paperclip-" + Guid.NewGuid());
    private RelayStub _stub = null!;
    private RelayClient? _client;

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    private sealed class Actions : IPaperclipActions
    {
        public readonly List<string> Calls = new();
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"{(approve ? "approve" : "decline")} {need.Kind} {need.Id}");
            return Task.FromResult(new PaperclipOutcome(PaperclipActionResult.Done));
        }
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct) => throw new NotSupportedException();
    }

    public Task InitializeAsync() { _stub = new RelayStub(); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        try { if (_client is not null) await RelayClientTests.StopBounded(_client); }
        finally { _client?.Dispose(); await _stub.DisposeAsync(); }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record Phone(string Id, KeyPair IdKey, KeyPair KaKey);
    private static bool IsEnvelope(JsonElement e) => e.TryGetProperty("v", out _);

    private static JsonElement OpenAsPhone(JsonElement envelope, Phone phone, RelayIdentity identity)
    {
        var ct = envelope.GetProperty("ct").GetString()!;
        var nonce = PeekNonce(ct)!.Value;
        var desktopId = envelope.GetProperty("from").GetString()!;
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        var plain = Open(EpochKey(basis, nonce.Epoch), ct, Aad(desktopId, phone.Id, envelope.GetProperty("class").GetString()!), DesktopToPhone);
        Assert.NotNull(plain);
        return JsonDocument.Parse(Encoding.UTF8.GetString(plain!)).RootElement;
    }

    private static PaperclipAgent Agent(params PaperclipNeed[] needs) =>
        new(AgentId, "Ledger bot", "accountant", "", "active", null, "claude_local", needs.Length > 0 ? "waiting" : "idle", PaperclipMark.None, null, needs,
            null, null, null, null, null, DateTime.UtcNow, "http://pc/agents/" + AgentId, false);

    private static RelaySession Row(string state = "idle", string? broke = null) => new(Sid, "paperclip", "Ledger bot", "", state, null, null, broke);

    private static (string, SessionStatus)[] Snapshot() => [(Sid, new SessionStatus { Source = SessionSource.Paperclip, State = "waiting", Title = "Ledger bot" })];

    private IReadOnlyList<PaperclipAgent> _agents = [];
    private readonly Actions _actions = new();

    private async Task<(RelayClient Client, RelayIdentity Identity, Phone Phone)> Start()
    {
        var identity = new RelayIdentity(_dir, new MemoryStore());
        identity.Keys();
        var phone = new Phone("ph_00000000000000d1", GenerateKeyPair(), GenerateKeyPair());
        identity.AddPhone(phone.Id, "Test iPhone", phone.IdKey.Public, phone.KaKey.Public);
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            PingInterval = TimeSpan.FromMinutes(5), Silence = TimeSpan.FromMinutes(5),
            MinBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(100),
        };
        _client = new RelayClient(identity, options, new WebSocketRelayTransport(), paperclipActions: () => _actions, paperclipAgents: () => _agents);
        _client.Start();
        await _stub.NextT("hello");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_client.State != RelayClient.Status.Connected)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("not connected");
            await Task.Delay(20);
        }
        return (_client, identity, phone);
    }

    private Task SendAnswer(RelayIdentity identity, Phone phone, JsonElement prompt, string desktopId, string action, ulong counter)
    {
        var rid = prompt.GetProperty("request_id").GetString()!;
        var exp = prompt.GetProperty("expires_at").GetInt64();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var sig = Base64Url(Sign(phone.IdKey, AnswerSigning.Message(AnswerSigning.Body(rid, Sid, action, "", nonce, exp))));
        var answer = JsonSerializer.Serialize(new
        {
            t = "answer", request_id = rid, session_id = Sid, action, scope = "", note = "", nonce,
            expires_at = exp, screen_hash = prompt.GetProperty("screen_hash").GetString(), sig,
        });
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        var ct = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, counter), Encoding.UTF8.GetBytes(answer), Aad(phone.Id, desktopId, "action"));
        return _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "a" + counter, from = phone.Id, to = desktopId, ts = 0, @class = "action", push = "none", ck = "", ct }));
    }

    [Fact]
    public async Task An_approval_reaches_a_phone_as_an_amber_it_answers_and_the_answer_is_carried_out_in_Paperclip()
    {
        var (client, identity, phone) = await Start();
        client.OnScanned([Row("waiting")]);
        _agents = [Agent(new PaperclipNeed("approval", ApprovalId, "Hire a research agent", "http://pc/approvals/" + ApprovalId, Tool: "Hire"))];
        await client.OnStatuses(Snapshot());

        var env = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "amber");
        Assert.Equal("action", env.GetProperty("class").GetString());
        Assert.True(env.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.String);
        var prompt = OpenAsPhone(env, phone, identity);
        Assert.Equal(("paperclip", "Hire", Sid, "permission"), (prompt.GetProperty("cli").GetString(), prompt.GetProperty("tool").GetString(), prompt.GetProperty("session_id").GetString(), prompt.GetProperty("kind").GetString()));
        Assert.Equal(PaperclipPrompts.ScreenHash("approval", ApprovalId), prompt.GetProperty("screen_hash").GetString());

        await SendAnswer(identity, phone, prompt, env.GetProperty("from").GetString()!, "approve", 0);
        var resolved = await _stub.Next(x => IsEnvelope(x) && OpenAsPhone(x, phone, identity).TryGetProperty("t", out var t) && t.GetString() == "resolved");
        Assert.Equal("phone:" + phone.Id, OpenAsPhone(resolved, phone, identity).GetProperty("by").GetString());
        lock (_actions.Calls) Assert.Equal([$"approve approval {ApprovalId}"], _actions.Calls);
    }

    [Fact]
    public async Task An_answer_naming_a_prompt_the_desk_does_not_hold_is_refused_not_pending_and_nothing_is_carried_out()
    {
        var (client, identity, phone) = await Start();
        client.OnScanned([Row("waiting")]);
        _agents = [Agent(new PaperclipNeed("approval", ApprovalId, "Hire", "u", Tool: "Hire"))];
        await client.OnStatuses(Snapshot());
        var env = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "amber");
        // The unknown answer: a prompt id the desk does not hold is refused and cleared, never pressed.
        var prompt = OpenAsPhone(env, phone, identity);
        var bogus = JsonDocument.Parse(prompt.GetRawText().Replace(prompt.GetProperty("request_id").GetString()!, "01JNOSUCHREQUEST00000000000")).RootElement;
        await SendAnswer(identity, phone, bogus, env.GetProperty("from").GetString()!, "approve", 0);
        var refusal = await _stub.Next(x => IsEnvelope(x) && OpenAsPhone(x, phone, identity).TryGetProperty("code", out _));
        Assert.Equal("not_pending", OpenAsPhone(refusal, phone, identity).GetProperty("code").GetString());
        lock (_actions.Calls) Assert.Empty(_actions.Calls);
    }

    private Task SendPrefs(RelayIdentity identity, Phone phone, string desktopId, string body, ulong counter)
    {
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        var ct = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, counter), Encoding.UTF8.GetBytes(body), Aad(phone.Id, desktopId, "pair"));
        return _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "p" + counter, from = phone.Id, to = desktopId, ts = 0, @class = "pair", push = "none", ck = "", ct }));
    }

    [Fact]
    public async Task A_break_rides_push_broke_to_a_phone_that_asked_and_stops_when_an_older_relay_refuses_it()
    {
        var (client, identity, phone) = await Start();
        client.OnScanned([Row()]);                                       // the baseline
        client.OnScanned([Row(broke: "error")]);                         // the phone never sent broke_alerts: no push, and an offline phone gets no plain delta either
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _stub.Next(e => IsEnvelope(e) && e.GetProperty("class").GetString() == "state", seconds: 1));

        var desktopId = _stub.DeviceId;
        await SendPrefs(identity, phone, desktopId, """{"t":"prefs","done_alerts":"all","broke_alerts":true}""", 0);
        var reply = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("class").GetString() == "pair");
        Assert.Equal("prefs.ok", OpenAsPhone(reply, phone, identity).GetProperty("t").GetString());

        client.OnScanned([Row()]);
        client.OnScanned([Row(broke: "timed_out")]);
        var env = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "broke");
        Assert.Equal("state", env.GetProperty("class").GetString());
        var delta = OpenAsPhone(env, phone, identity);
        Assert.Equal(("delta", Sid), (delta.GetProperty("t").GetString(), delta.GetProperty("session_id").GetString()));
        var e = Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, env.GetProperty("e").GetString()!)!);
        Assert.Contains("\"why\":\"timed_out\"", e);

        // An older relay answers bad_envelope: no more broke pushes on this connection, the next break is a plain delta.
        var latched = new TaskCompletionSource();
        client.Logged += note => { if (note.Contains("does not know push broke")) latched.TrySetResult(); };
        await _stub.Send($$"""{"t":"err","ref":"{{env.GetProperty("id").GetString()}}","code":"bad_envelope","message":"push must be none, amber, resolve, done or limit"}""");
        await latched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.OnScanned([Row()]);
        client.OnScanned([Row(broke: "error")]);
        JsonElement? stray = null;
        try { stray = await _stub.Next(x => IsEnvelope(x) && x.GetProperty("class").GetString() == "state", seconds: 1); } catch (OperationCanceledException) { }
        Assert.True(stray is null, "unexpected frame: " + stray?.GetRawText().Substring(0, 220));
    }

    // CB-431: the roster a parked host serves is RelayService.RelayScan; a prompt must reach a phone from
    // exactly that feed (rows from the headless scan, needs from the poll loop's own snapshot), with no UI.
    [Fact]
    public async Task With_the_UI_parked_the_headless_roster_carries_the_agent_and_its_approval_reaches_the_phone_as_an_amber()
    {
        var (client, identity, phone) = await Start();
        var dir = Path.Combine(Path.GetTempPath(), "cb431-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            _agents = [Agent(new PaperclipNeed("approval", ApprovalId, "Hire a research agent", "http://pc/a", Tool: "Hire"))];
            PaperclipSessions.SetSnapshotForTests(_agents);        // the poll loop's snapshot feeds the headless scan
            PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected");
            var scan = RelayService.RelayScan(dir, () => new Dictionary<string, string>(), _ => true, DateTime.UtcNow.AddDays(3), onWindows: false);
            Assert.Contains(scan, r => r.SessionId == Sid);
            client.OnScanned(RelayService.Project(scan).Rows);
            await client.OnStatuses(scan);
            var env = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "amber");
            Assert.Equal(Sid, OpenAsPhone(env, phone, identity).GetProperty("session_id").GetString());
        }
        finally
        {
            PaperclipSessions.SetSnapshotForTests([]);
            Directory.Delete(dir, recursive: true);
        }
    }

    // CB-431: the broke push, from the same headless feed (rows from RelayScan, marks from the poll loop's snapshot).
    [Fact]
    public async Task With_the_UI_parked_an_agent_whose_mark_becomes_Broke_pushes_broke_to_a_phone_that_asked_and_an_outage_does_not_repeat_it()
    {
        var (client, identity, phone) = await Start();
        var dir = Path.Combine(Path.GetTempPath(), "cb431-broke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await SendPrefs(identity, phone, _stub.DeviceId, """{"t":"prefs","done_alerts":"all","broke_alerts":true}""", 0);
        await _stub.Next(e => IsEnvelope(e) && e.GetProperty("class").GetString() == "pair");
        async Task Tick(PaperclipAgent a, PaperclipLink link)
        {
            PaperclipSessions.SetSnapshotForTests([a]);
            PaperclipSessions.SetLinkForTests(link, link.ToString());
            var scan = RelayService.RelayScan(dir, () => new Dictionary<string, string>(), _ => true, DateTime.UtcNow.AddDays(3), onWindows: false);
            client.OnScanned(RelayService.Project(scan).Rows);
            await client.OnStatuses(scan);
        }
        try
        {
            var ok = Agent();
            var broke = ok with { Mark = PaperclipMark.Broke, MarkReason = "Last run failed" };
            await Tick(ok, PaperclipLink.Connected);                       // the baseline
            await Tick(broke, PaperclipLink.Connected);
            var env = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "broke");
            Assert.Contains("\"why\":\"run_failed\"", Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, env.GetProperty("e").GetString()!)!));

            await Tick(broke, PaperclipLink.NeedsReconnect);               // the key is refused: every row parks
            await Tick(broke, PaperclipLink.Connected);                    // and it comes back, still broke
            JsonElement? again = null;
            try { again = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "broke", seconds: 1); } catch (OperationCanceledException) { }
            Assert.Null(again);
        }
        finally
        {
            PaperclipSessions.SetSnapshotForTests([]);
            PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "");
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_desktops_default_agents_are_the_pollers_snapshot()
    {
        using var client = new RelayClient(new RelayIdentity(_dir, new MemoryStore()), new RelayClient.Options(new Uri("http://127.0.0.1:1"), "mac", "9.9.9", "Test Mac"), new WebSocketRelayTransport());
        Assert.NotNull(client.Paperclip);
        Assert.Equal(0, client.Paperclip.PendingCount);
    }
}
