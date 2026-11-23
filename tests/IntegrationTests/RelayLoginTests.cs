using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-412 slice C over a real socket against RelayStub: /login's screens as
/// prompts the phone can open, a `code` answer sealed and signed like any other,
/// and the shape an old phone is left with. The captures are real tmux reads.
/// </summary>
public class RelayLoginTests : IAsyncLifetime
{
    private const string Canary = "QA-CANARY-7h2m9qlstate";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-login-" + Guid.NewGuid());
    private RelayStub _stub = null!;
    private RelayClient? _client;

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
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

    private static string Fixture(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tests", "TranscriptTests", "Captures", "cb412", name + ".txt");
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException(name);
    }

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

    private async Task<(RelayClient Client, RelayIdentity Identity, Phone Phone, List<string> Typed, List<string> Keys, Func<string, string> SetScreen)> Start(string screen)
    {
        var identity = new RelayIdentity(_dir, new MemoryStore());
        identity.Keys();
        var phone = new Phone("ph_00000000000000c1", GenerateKeyPair(), GenerateKeyPair());
        identity.AddPhone(phone.Id, "Test iPhone", phone.IdKey.Public, phone.KaKey.Public);
        var typed = new List<string>();
        var keys = new List<string>();
        var current = screen;
        var seams = new PromptPipeline.Seams(
            _ => { lock (typed) return Task.FromResult<string?>(current); },
            (_, k) => { lock (keys) keys.Add(k); lock (typed) current = "> \n"; return Task.FromResult(true); },
            (_, t) => { lock (typed) { typed.Add(t); current = Fixture("login-code-typed-w100"); } return Task.FromResult(true); },
            () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { });
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            PingInterval = TimeSpan.FromMinutes(5), Silence = TimeSpan.FromMinutes(5),
            MinBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(100),
        };
        _client = new RelayClient(identity, options, new WebSocketRelayTransport(), promptSeams: seams);
        _client.Start();
        await _stub.NextT("hello");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_client.State != RelayClient.Status.Connected)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("not connected");
            await Task.Delay(20);
        }
        await _client.OnStatuses([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, Title = "proj" })]);
        return (_client, identity, phone, typed, keys, s => { lock (typed) return current = s; });
    }

    private Task SendAnswer(RelayIdentity identity, Phone phone, JsonElement prompt, string desktopId, string action, string note, ulong counter, KeyPair signer)
    {
        var rid = prompt.GetProperty("request_id").GetString()!;
        var exp = prompt.GetProperty("expires_at").GetInt64();
        var scope = note.Length > 0 ? AnswerSigning.TellScope(note) : "";
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var sig = Base64Url(Sign(signer, AnswerSigning.Message(AnswerSigning.Body(rid, "s1", action, scope, nonce, exp))));
        var answer = JsonSerializer.Serialize(new
        {
            t = "answer", request_id = rid, session_id = "s1", action, scope, note, nonce,
            expires_at = exp, screen_hash = prompt.GetProperty("screen_hash").GetString(), sig,
        });
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        var ct = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, counter), Encoding.UTF8.GetBytes(answer), Aad(phone.Id, desktopId, "action"));
        return _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "a" + counter, from = phone.Id, to = desktopId, ts = 0, @class = "action", push = "none", ck = "", ct }));
    }

    [Fact]
    public async Task The_picker_reaches_a_phone_quietly_and_a_digit_answers_it()
    {
        var (_, identity, phone, _, keys, _) = await Start(Fixture("login-picker-w100"));
        var env = await _stub.Next(IsEnvelope);
        Assert.Equal(("action", "none"), (env.GetProperty("class").GetString(), env.GetProperty("push").GetString()));
        Assert.False(env.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.String);   // nothing about /login is in a push
        var prompt = OpenAsPhone(env, phone, identity);
        Assert.Equal(("question", "Login", true), (prompt.GetProperty("kind").GetString(), prompt.GetProperty("tool").GetString(), prompt.GetProperty("quiet").GetBoolean()));
        Assert.Equal("picker", prompt.GetProperty("login").GetProperty("step").GetString());

        await SendAnswer(identity, phone, prompt, env.GetProperty("from").GetString()!, "option:1", "", 0, phone.IdKey);
        var resolved = await _stub.Next(IsEnvelope);
        Assert.Equal("none", resolved.GetProperty("push").GetString());
        Assert.Equal(("resolved", "phone:" + phone.Id), (OpenAsPhone(resolved, phone, identity).GetProperty("t").GetString(), OpenAsPhone(resolved, phone, identity).GetProperty("by").GetString()));
        lock (keys) Assert.Equal(["1"], keys);
    }

    [Fact]
    public async Task A_sealed_code_is_typed_and_entered_and_appears_nowhere_on_the_wire()
    {
        var (_, identity, phone, typed, keys, _) = await Start(Fixture("login-url-w100"));
        var env = await _stub.Next(IsEnvelope);
        var prompt = OpenAsPhone(env, phone, identity);
        // What an old phone sees of the URL step is an ordinary question with no
        // options (drawn as "Open on desk"); what a new one adds is login.
        Assert.Equal("question", prompt.GetProperty("kind").GetString());
        Assert.Empty(prompt.GetProperty("options").EnumerateArray());
        var login = prompt.GetProperty("login");
        Assert.Equal(("url", true), (login.GetProperty("step").GetString(), login.GetProperty("code").GetBoolean()));
        Assert.DoesNotContain("oauth", prompt.GetProperty("preview").GetString());

        var wire = new StringBuilder(env.GetRawText());
        await SendAnswer(identity, phone, prompt, env.GetProperty("from").GetString()!, "code", Canary, 0, phone.IdKey);
        var resolved = await _stub.Next(IsEnvelope);
        wire.Append(resolved.GetRawText());
        Assert.Equal("phone:" + phone.Id, OpenAsPhone(resolved, phone, identity).GetProperty("by").GetString());
        lock (typed) Assert.Equal([Canary], typed);          // the control: it did reach the session
        lock (keys) Assert.Equal(["Enter"], keys);
        Assert.DoesNotContain(Canary, wire.ToString());
        Assert.DoesNotContain(Canary, string.Join("\n", _stub.Requests));
    }

    [Fact]
    public async Task A_code_signed_by_another_key_is_refused_and_never_typed()
    {
        var (_, identity, phone, typed, keys, _) = await Start(Fixture("login-url-w100"));
        var env = await _stub.Next(IsEnvelope);
        var prompt = OpenAsPhone(env, phone, identity);
        await SendAnswer(identity, phone, prompt, env.GetProperty("from").GetString()!, "code", Canary, 0, GenerateKeyPair());
        var refusal = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal(("err", "bad_signature"), (refusal.GetProperty("t").GetString(), refusal.GetProperty("code").GetString()));
        lock (typed) Assert.Empty(typed);
        lock (keys) Assert.Empty(keys);
    }
}
