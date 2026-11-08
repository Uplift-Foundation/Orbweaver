using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Orbweaver;
using static Orbweaver.RelayCrypto;

// CB-314: the desktop relay client against a real relay, end to end.
//
//   dotnet run --project tools/relay-probe -- [https://orbweaver-relay.uplifttech.org] [--flips N]
//
// The desktop half is the shipped RelayClient over a scratch RelayIdentity
// (plain secret store in a temp directory, never the real Keychain). The phone
// half is scripted here from RelayCrypto's primitives, the way the iPhone does
// it with CryptoKit. Each step prints PASS or FAIL with what it measured:
// register both, pair by offer and check code, roster on link, deltas timed
// from OnScanned to the phone's socket, then Remove: revoke, the relay's
// unlinked, and a frame from the removed phone refused by the relay.
//
// It registers two throwaway devices on the relay each run; on the shared dev
// relay, tell surface A when a clean slate is wanted.

var baseUrl = new Uri(args.FirstOrDefault(a => a.StartsWith("http")) ?? "https://orbweaver-relay.uplifttech.org");
var flips = args.SkipWhile(a => a != "--flips").Skip(1).Select(int.Parse).FirstOrDefault(20);
var failures = 0;
void Report(bool ok, string step, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {step}{(detail.Length > 0 ? "  — " + detail : "")}");
}

var dir = Path.Combine(Path.GetTempPath(), "relay-probe-" + Guid.NewGuid().ToString("N")[..8]);
var identity = new RelayIdentity(dir, PlainSecretStore.Instance);
var client = new RelayClient(identity, new RelayClient.Options(baseUrl, "mac", "probe", "Relay Probe Mac"), new WebSocketRelayTransport());
MirrorLog.Say("relay-probe", "start");

// ---- desktop up ---------------------------------------------------------
client.Start();
var sw = Stopwatch.StartNew();
while (client.State != RelayClient.Status.Connected && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
Report(client.State == RelayClient.Status.Connected, "desktop registers and is welcomed", $"{identity.DeviceId} in {sw.ElapsedMilliseconds} ms");
if (client.State != RelayClient.Status.Connected) { Console.WriteLine(client.LastError); return 1; }

// ---- phone registers -------------------------------------------------------
var phId = GenerateKeyPair();
var phKa = GenerateKeyPair();
using var http = new HttpClient { BaseAddress = baseUrl };
var body = new Dictionary<string, string> { ["platform"] = "ios", ["app_version"] = "probe", ["name"] = "Probe iPhone", ["id_pub"] = Hex(phId.Public), ["ka_pub"] = Hex(phKa.Public) };
var first = await (await http.PostAsJsonAsync("/v1/register", body)).Content.ReadFromJsonAsync<JsonElement>();
var challenge = first.GetProperty("challenge").GetString()!;
body["challenge"] = challenge;
body["sig"] = Base64Url(Sign(phId, RegisterMessage(FromBase64Url(challenge)!)));
var second = await (await http.PostAsJsonAsync("/v1/register", body)).Content.ReadFromJsonAsync<JsonElement>();
var phoneId = second.GetProperty("device_id").GetString()!;
var phoneToken = second.GetProperty("token").GetString()!;
Report(phoneId.StartsWith("ph_"), "phone registers with a signed raw challenge", phoneId);

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new UriBuilder(baseUrl) { Scheme = baseUrl.Scheme == "http" ? "ws" : "wss", Path = "/v1/socket" }.Uri, default);
var inbox = System.Threading.Channels.Channel.CreateUnbounded<(JsonElement Frame, long At)>();
_ = Task.Run(async () =>
{
    var buf = new byte[256 * 1024];
    while (ws.State == WebSocketState.Open)
    {
        var len = 0;
        ValueWebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf.AsMemory(len), default); len += r.Count; } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
        if (r.MessageType == WebSocketMessageType.Close) break;
        await inbox.Writer.WriteAsync((JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, len)).RootElement.Clone(), Stopwatch.GetTimestamp()));
    }
});
Task Send(object frame) => ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, default);
async Task<(JsonElement Frame, long At)?> Next(Func<JsonElement, bool> match, int seconds = 10)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    try
    {
        while (true)
        {
            var item = await inbox.Reader.ReadAsync(cts.Token);
            if (item.Frame.TryGetProperty("t", out var t) && t.GetString() == "ping") { await Send(new { t = "pong" }); continue; }
            if (match(item.Frame)) return item;
        }
    }
    catch (OperationCanceledException) { return null; }
}
static bool T(JsonElement e, string t) => e.TryGetProperty("t", out var v) && v.GetString() == t;
static bool Env(JsonElement e, string cls) => e.TryGetProperty("v", out _) && e.GetProperty("class").GetString() == cls;

await Send(new { t = "hello", device_id = phoneId, token = phoneToken, platform = "ios", app_version = "probe", proto = 2 });
Report(await Next(e => T(e, "ok")) is not null, "phone is welcomed");

// ---- pairing --------------------------------------------------------------
var offer = await client.BeginPairingAsync();
var q = offer.QrText[(offer.QrText.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
Report(q["r"] == baseUrl.Host && q["d"] == identity.DeviceId, "offer from POST /v1/offers, QR names this relay and desktop", offer.OfferId);
var k = FromBase64Url(q["k"])!;
var (dIdPub, dKaPub) = (k[..65], k[65..]);
var pairKey = PairKey(FromBase64Url(q["s"])!, q["o"], q["d"], phoneId);

RelayLink.PairRequest? asked = null;
client.PairRequested += r => asked = r;
var req = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phoneId}\",\"name\":\"Probe iPhone\",\"id_pub\":\"{Hex(phId.Public)}\",\"ka_pub\":\"{Hex(phKa.Public)}\"}}";
await Send(new { v = 2, id = RelayLink.Ulid(DateTimeOffset.UtcNow), from = phoneId, to = q["d"], ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), @class = "pair", push = "none", ck = "",
    ct = Seal(pairKey, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(req), Aad(phoneId, q["d"], "pair")), offer = q["o"] });
sw.Restart();
while (asked is null && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
var phoneCode = CheckCode(dIdPub, dKaPub, phId.Public, phKa.Public);
Report(asked is not null && asked.CheckCode == phoneCode, "pair.request reaches the desktop; check codes match",
    asked is null ? "no request" : $"desktop {asked.CheckCodeDisplay}, phone {CheckCodeDisplay(phoneCode)}");
if (asked is null) return 1;

// Allow is required: until the owner clicks it, the phone receives nothing —
// no accept, no roster.
var early = await Next(e => e.TryGetProperty("v", out _), 2);
Report(early is null, "nothing reaches the phone before Allow", early is null ? "2 s of silence" : early.Value.Frame.GetRawText());
Report(client.Allow(asked), "Allow sends");
var accept = await Next(e => Env(e, "pair"));
var acceptBody = accept is null ? null : Open(pairKey, accept.Value.Frame.GetProperty("ct").GetString(), Aad(q["d"], phoneId, "pair"), DesktopToPhone);
var acceptJson = acceptBody is null ? default : JsonDocument.Parse(acceptBody).RootElement;
Report(acceptBody is not null && Verify(dIdPub, AcceptMessage(q["d"], phoneId, dIdPub, dKaPub, phId.Public, phKa.Public), FromBase64Url(acceptJson.GetProperty("sig").GetString())),
    "pair.accept opens under the pair key and its signature verifies under the scanned key");

var basis = SessionBase(SharedSecret(phKa, dKaPub), q["d"], phoneId);
JsonElement? OpenState(JsonElement env)
{
    var ct = env.GetProperty("ct").GetString()!;
    var n = PeekNonce(ct)!.Value;
    var plain = Open(EpochKey(basis, n.Epoch), ct, Aad(q["d"], phoneId, env.GetProperty("class").GetString()!), DesktopToPhone);
    return plain is null ? null : JsonDocument.Parse(plain).RootElement;
}
var rosterEnv = await Next(e => Env(e, "state"));
var roster = rosterEnv is null ? null : OpenState(rosterEnv.Value.Frame);
Report(roster is { } rr && rr.GetProperty("t").GetString() == "roster" && rr.GetProperty("seq").GetInt64() == 1, "the first roster arrives after link and opens under the session key");

// ---- deltas, timed ----------------------------------------------------------
var rows = new[] { new RelaySession("probe-session", "claude", "probe", "/tmp/probe", "idle", null, null) };
client.OnScanned(rows);                                                   // a new session: a roster
await Next(e => Env(e, "state"));
var latencies = new List<double>();
var seqOk = true;
long expect = 2;
for (var i = 0; i < flips; i++)
{
    var state = i % 2 == 0 ? "generating" : "idle";
    var start = Stopwatch.GetTimestamp();
    client.OnScanned([rows[0] with { State = state }]);
    var got = await Next(e => Env(e, "state"));
    if (got is null) { seqOk = false; break; }
    var d = OpenState(got.Value.Frame);
    latencies.Add(Stopwatch.GetElapsedTime(start, got.Value.At).TotalMilliseconds);
    if (d is not { } dd || dd.GetProperty("t").GetString() != "delta" || dd.GetProperty("seq").GetInt64() != expect++ || dd.GetProperty("state").GetString() != state)
    {
        if (seqOk) Console.WriteLine($"      first mismatch at flip {i}: expected delta seq {expect - 1} state {state}, got {(d is { } x ? x.GetRawText() : "a box that did not open")}");
        seqOk = false;
    }
}
latencies.Sort();
double P(double p) => latencies.Count == 0 ? double.NaN : latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(p * latencies.Count) - 1)];
Report(seqOk && latencies.Count == flips, $"{flips} state flips arrive as numbered deltas", $"OnScanned→phone socket p50 {P(0.5):F0} ms, p95 {P(0.95):F0} ms, max {latencies.LastOrDefault():F0} ms");

// ---- a gap triggers a resync -------------------------------------------------
// The phone pretends it lost a delta: it sees the next one's seq jump, sends
// the relay-visible resync, and must get a fresh roster numbered from 1.
client.OnScanned([rows[0] with { State = "waiting" }]);
var skipped = await Next(e => Env(e, "state"));                          // "lost"
client.OnScanned([rows[0] with { State = "generating" }]);
var afterGap = await Next(e => Env(e, "state"));
var gapSeq = afterGap is null ? 0 : OpenState(afterGap.Value.Frame)?.GetProperty("seq").GetInt64() ?? 0;
await Send(new { t = "resync", peer_id = q["d"] });
var resynced = await Next(e => Env(e, "state"));
var fresh = resynced is null ? null : OpenState(resynced.Value.Frame);
Report(skipped is not null && gapSeq == expect + 1 && fresh is { } fr && fr.GetProperty("t").GetString() == "roster" && fr.GetProperty("seq").GetInt64() == 1
       && fr.GetProperty("sessions")[0].GetProperty("state").GetString() == "generating",
    "a seq gap and a resync bring a fresh roster from seq 1 with the current state", $"gap seen at seq {gapSeq}, expected {expect} first");

// ---- remove -----------------------------------------------------------------
client.Remove(phoneId);
var revoke = await Next(e => Env(e, "pair"));
var revokeBody = revoke is null ? null : OpenState(revoke.Value.Frame);
Report(revokeBody is { } rb && rb.GetProperty("t").GetString() == "pair.revoke" && Verify(dIdPub, RevokeMessage(q["d"], phoneId), FromBase64Url(rb.GetProperty("sig").GetString())),
    "Remove sends a signed pair.revoke the phone can verify");
// The relay sends both, in either order: presence offline for the desktop and
// err unlinked naming it. Both are consumed before the stray frame below, so
// its refusal cannot be confused with them.
var sawPresence = false;
string? unlinkedFrom = null;
while (!(sawPresence && unlinkedFrom is not null))
{
    var f = await Next(e => T(e, "err") || T(e, "presence"), 10);
    if (f is null) break;
    if (T(f.Value.Frame, "presence") && f.Value.Frame.GetProperty("device_id").GetString() == q["d"] && !f.Value.Frame.GetProperty("online").GetBoolean()) sawPresence = true;
    if (T(f.Value.Frame, "err") && f.Value.Frame.GetProperty("code").GetString() == "unlinked")
        unlinkedFrom = f.Value.Frame.TryGetProperty("peer_id", out var pid) ? pid.GetString() : f.Value.Frame.GetProperty("message").GetString();
}
Report(sawPresence && unlinkedFrom == q["d"], "the relay tells the phone the link is gone: presence offline and err unlinked naming the desktop", $"unlinked by {unlinkedFrom}");

var stray = Seal(EpochKey(basis, 1), Nonce(PhoneToDesktop, 1, 0), "{}"u8.ToArray(), Aad(phoneId, q["d"], "action"));
await Send(new { v = 2, id = RelayLink.Ulid(DateTimeOffset.UtcNow), from = phoneId, to = q["d"], ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), @class = "action", push = "none", ck = "", ct = stray });
var refused = await Next(e => T(e, "err"), 10);
Report(refused is { } rf && rf.Frame.GetRawText().Contains("not_linked"), "a frame from the removed phone is refused by the relay", refused?.Frame.GetRawText() ?? "nothing");

await client.StopAsync();
client.Dispose();
try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default); } catch { }
try { Directory.Delete(dir, true); } catch { }
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
Console.WriteLine($"devices registered: desktop {identity.DeviceId ?? "(cleared)"}, phone {phoneId}");
return failures == 0 ? 0 : 1;
