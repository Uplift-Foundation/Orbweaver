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

// Never the real settings directory: anything this probe logs or saves goes to
// a scratch one unless the caller chose a directory. (An earlier version wrote
// its start line into the installed app's own mirror log.)
if (string.IsNullOrEmpty(BrandEnv.Get(BrandEnv.SettingsDir)))
    Environment.SetEnvironmentVariable(BrandEnv.Name(BrandEnv.SettingsDir), Path.Combine(Path.GetTempPath(), "relay-probe-settings"));

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
// --no-pacer: the burst test of the relay's own rate limiter (hana's PR #9),
// with the client's pacing switched off so the relay sees the raw burst.
var client = new RelayClient(identity, new RelayClient.Options(baseUrl, "mac", "probe", "Relay Probe Mac") { PaceFrames = !args.Contains("--no-pacer") }, new WebSocketRelayTransport());
MirrorLog.Say("relay-probe", "start");

// ---- phone-only mode ---------------------------------------------------------
// The other half: a scripted phone pairing with whatever desktop made the QR —
// the installed app under ORBWEAVER_RELAY_TEST_HOOKS=1, for one. Pass the QR
// text, or @path to read it from a file. It prints the check code, waits for
// the accept, then reports every roster and delta with its arrival time
// (UTC), forces one gap and a resync after --deltas N deltas, and finally
// unlinks from the phone side so the desktop forgets the pairing.
if (args.Contains("--phone-only"))
{
    var qrArg = args.SkipWhile(a => a != "--qr").Skip(1).First();
    var qrText = qrArg.StartsWith('@') ? File.ReadAllText(qrArg[1..]).Trim() : qrArg;
    var wantDeltas = args.SkipWhile(a => a != "--deltas").Skip(1).Select(int.Parse).FirstOrDefault(4);
    return await PhoneOnly(baseUrl, qrText, wantDeltas);
}

// ---- answer-phone mode (CB-315) ---------------------------------------------
// A stand-in phone that answers amber prompts with any field forged; see
// AnswerPhone.cs for the stdin commands.
if (args.Contains("--answer-phone"))
{
    var qrArg = args.SkipWhile(a => a != "--qr").Skip(1).First();
    return await AnswerPhone.RunAsync(baseUrl, qrArg.StartsWith('@') ? File.ReadAllText(qrArg[1..]).Trim() : qrArg);
}

// ---- prompt-desktop mode (CB-315) -------------------------------------------
// The shipped RelayClient and PromptPipeline, with the REAL tmux seams, aimed at
// one pane the caller owns: --pane %N [--socket <tmux socket path>]
// [--cli claude|codex|grok]. It prints a QR, Allows the first request, then
// reports that pane as a waiting session every second, so whatever dialog the
// pane shows goes out as an amber prompt and an answer presses into that pane.
// The pipeline's own log lines (prompt / refuse / press / press-no-effect /
// resolved, each with the request id) print to stdout. Scratch identity, no
// settings touched, no installed app involved.
if (args.Contains("--prompt-desktop"))
{
    string Arg(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
    var pane = Arg("--pane", "");
    if (pane.Length == 0) { Console.WriteLine("--pane %N (or --pane - with --term-program/--term-id) is required"); return 2; }
    var source = Arg("--cli", "claude") switch { "codex" => SessionSource.Codex, "grok" => SessionSource.Grok, _ => SessionSource.ClaudeCode };
    // Or a non-tmux terminal: --term-program iTerm.app|WezTerm|kitty|Apple_Terminal
    // --term-id <session GUID / pane id / window id> [--tty /dev/ttysNNN]
    // [--pid N] (Windows console: the CLI's own pid).
    var status = new SessionStatus
    {
        State = "waiting", Source = source, Title = "relay probe", TmuxPane = pane == "-" ? "" : pane, TmuxSocket = Arg("--socket", ""),
        TermProgram = Arg("--term-program", ""), TermId = Arg("--term-id", ""), Tty = Arg("--tty", ""),
        SessionPid = int.Parse(Arg("--pid", "0")),
    };
    Console.WriteLine($"route {TerminalFocuser.RouteFor(status)}; presses silently: {TerminalFocuser.CanPressSilently(status)}");
    var seams = new PromptPipeline.Seams(
        s => ScreenReaders.For(s)?.ReadAsync(s) ?? Task.FromResult<string?>(null), TerminalFocuser.PressKey, TerminalFocuser.TypeQuietly,
        () => DateTimeOffset.UtcNow, Task.Delay, l => Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {l}"),
        TerminalFocuser.CanPressSilently, TerminalFocuser.CaptureAndPress);
    var pd = new RelayClient(identity, new RelayClient.Options(baseUrl, "mac", "probe", "Relay Probe Mac"), new WebSocketRelayTransport(), promptSeams: seams);
    // Say every connection change: a probe that goes quiet after the relay
    // closed its socket must show whether it reconnected.
    pd.StateChanged += () => Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} relay {pd.State}{(pd.LastError is { } le ? " (" + le + ")" : "")}");
    pd.Start();
    while (pd.State != RelayClient.Status.Connected) await Task.Delay(50);
    var pdOffer = await pd.BeginPairingAsync();
    Console.WriteLine($"desktop {identity.DeviceId} connected to {baseUrl}");
    Console.WriteLine($"QR {pdOffer.QrText}");
    var pdAsked = new TaskCompletionSource<RelayLink.PairRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    pd.PairRequested += r => pdAsked.TrySetResult(r);
    var pr = await pdAsked.Task.WaitAsync(pdOffer.ExpiresAt - DateTimeOffset.UtcNow);
    Console.WriteLine($"request from {pr.PhoneId} check code {pr.CheckCodeDisplay}: {(pd.Allow(pr) ? "allowed" : "allow not sent")}");
    using var quit = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
    while (!quit.IsCancellationRequested)
    {
        await pd.OnStatuses([("probe-session", status)]);
        await Task.Delay(1000, quit.Token).ContinueWith(_ => { });
    }
    await pd.StopAsync();
    pd.Dispose();
    return 0;
}

// ---- desktop-only mode -----------------------------------------------------
// For pairing a real phone client (surface C's LiveClient in a test harness)
// against the shipped desktop client: print the QR text, Allow the first
// request (printing both check codes' inputs so the harness can compare),
// then flip one session's state every --flip-every seconds. stdin takes
// "flip", "remove" and "quit".
if (args.Contains("--desktop-only"))
{
    var every = args.SkipWhile(a => a != "--flip-every").Skip(1).Select(int.Parse).FirstOrDefault(5);
    // RelayLink's notes on stdout, so a harness sees "resync from … fresh
    // roster" or "resync … ignored" as it happens (Celeste's request).
    client.Logged += note => Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} note {note}");
    client.Start();
    while (client.State != RelayClient.Status.Connected) await Task.Delay(50);
    Console.WriteLine($"desktop {identity.DeviceId} connected to {baseUrl}");
    var o = await client.BeginPairingAsync();
    Console.WriteLine($"QR {o.QrText}");
    Console.WriteLine($"offer {o.OfferId} expires {o.ExpiresAt:O}");
    // Continuations off the raising thread. (RelayClient no longer raises on
    // its read loop either, but a probe should not depend on that.)
    var paired = new TaskCompletionSource<RelayLink.PairRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PairRequested += r => paired.TrySetResult(r);
    RelayLink.PairRequest request;
    try { request = await paired.Task.WaitAsync(o.ExpiresAt - DateTimeOffset.UtcNow); }
    catch (TimeoutException) { Console.WriteLine("offer expired with no request"); await client.StopAsync(); client.Dispose(); return 1; }
    Console.WriteLine($"request from {request.PhoneId} \"{request.Name}\" check code {request.CheckCodeDisplay}");
    Console.WriteLine(client.Allow(request) ? "allowed" : "allow could not be sent");
    var row = new RelaySession("desktop-only-session", "claude", "relay probe", "/tmp/relay-probe", "idle", null, null);
    client.OnScanned([row]);
    using var stop = new CancellationTokenSource();
    var flipper = Task.Run(async () =>
    {
        var n = 0;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(every), stop.Token).ContinueWith(_ => { });
            if (stop.IsCancellationRequested) break;
            row = row with { State = ++n % 2 == 1 ? "generating" : "idle" };
            client.OnScanned([row]);
            Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} state -> {row.State}");
        }
    });
    string? line;
    while ((line = Console.ReadLine()) is not null && line != "quit")
    {
        if (line == "flip") { row = row with { State = row.State == "idle" ? "waiting" : "idle" }; client.OnScanned([row]); Console.WriteLine($"state -> {row.State}"); }
        if (line == "remove") { client.Remove(request.PhoneId); Console.WriteLine("removed"); }
    }
    stop.Cancel();
    await flipper;
    await client.StopAsync();
    client.Dispose();
    return 0;
}

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
// With --no-pacer, Remove happens in the middle of a 300-delta flood, which
// is the case hana's PR #9 fixes: the unlink must still land, and frames the
// relay refuses must come back as rate_limited with a ref (read the desktop's
// mirror log with ORBWEAVER_MIRROR_LOG=1 to count them).
if (args.Contains("--no-pacer"))
{
    for (var i = 0; i < 300; i++) client.OnScanned([rows[0] with { State = i % 2 == 0 ? "generating" : "idle" }]);
    Console.WriteLine("      queued 300 unpaced deltas; removing the phone during the flood");
}
client.Remove(phoneId);
// One pass collects the revoke, the presence and the unlinked, in whatever
// order they come: under a flood, a step waiting for only one of them would
// throw the others away.
JsonElement? revokeBody = null;
var sawPresence = false;
string? unlinkedFrom = null;
var collectUntil = DateTime.UtcNow.AddSeconds(15);
while (DateTime.UtcNow < collectUntil && !(revokeBody is not null && sawPresence && unlinkedFrom is not null))
{
    var f = await Next(e => Env(e, "pair") || T(e, "err") || T(e, "presence"), 15);
    if (f is null) break;
    var got = f.Value.Frame;
    if (Env(got, "pair")) revokeBody = OpenState(got);
    if (T(got, "presence") && got.GetProperty("device_id").GetString() == q["d"] && !got.GetProperty("online").GetBoolean()) sawPresence = true;
    if (T(got, "err") && got.GetProperty("code").GetString() == "unlinked")
        unlinkedFrom = got.TryGetProperty("peer_id", out var pid) ? pid.GetString() : got.GetProperty("message").GetString();
}
Report(revokeBody is { } rb && rb.GetProperty("t").GetString() == "pair.revoke" && Verify(dIdPub, RevokeMessage(q["d"], phoneId), FromBase64Url(rb.GetProperty("sig").GetString())),
    "Remove sends a signed pair.revoke the phone can verify");
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

static async Task<int> PhoneOnly(Uri baseUrl, string qrText, int wantDeltas)
{
    string Now() => DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");
    var q = qrText[(qrText.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
    var k = FromBase64Url(q["k"])!;
    var (dIdPub, dKaPub, desktopId) = (k[..65], k[65..], q["d"]);
    var phId = GenerateKeyPair();
    var phKa = GenerateKeyPair();
    using var http = new HttpClient { BaseAddress = baseUrl };
    var body = new Dictionary<string, string> { ["platform"] = "ios", ["app_version"] = "probe", ["name"] = "Probe iPhone", ["id_pub"] = Hex(phId.Public), ["ka_pub"] = Hex(phKa.Public) };
    var first = await (await http.PostAsJsonAsync("/v1/register", body)).Content.ReadFromJsonAsync<JsonElement>();
    body["challenge"] = first.GetProperty("challenge").GetString()!;
    body["sig"] = Base64Url(Sign(phId, RegisterMessage(FromBase64Url(body["challenge"])!)));
    var reg = await (await http.PostAsJsonAsync("/v1/register", body)).Content.ReadFromJsonAsync<JsonElement>();
    var phoneId = reg.GetProperty("device_id").GetString()!;

    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new UriBuilder(baseUrl) { Scheme = baseUrl.Scheme == "http" ? "ws" : "wss", Path = "/v1/socket" }.Uri, default);
    var buf = new byte[256 * 1024];
    async Task Send(object f) => await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(f), WebSocketMessageType.Text, true, default);
    async Task<JsonElement?> Receive(Func<JsonElement, bool> match, int seconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (true)
            {
                var len = 0;
                ValueWebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf.AsMemory(len), cts.Token); len += r.Count; } while (!r.EndOfMessage);
                var e = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, len)).RootElement.Clone();
                if (e.TryGetProperty("t", out var t) && t.GetString() == "ping") { await Send(new { t = "pong" }); continue; }
                if (match(e)) return e;
                if (!e.TryGetProperty("v", out _)) Console.WriteLine($"{Now()} (relay: {e.GetRawText()})");
            }
        }
        catch (OperationCanceledException) { return null; }
    }
    bool IsEnv(JsonElement e, string cls) => e.TryGetProperty("v", out _) && e.GetProperty("class").GetString() == cls;

    await Send(new { t = "hello", device_id = phoneId, token = reg.GetProperty("token").GetString(), platform = "ios", app_version = "probe", proto = 2 });
    await Receive(e => e.TryGetProperty("t", out var t) && t.GetString() == "ok", 10);
    var pairKey = PairKey(FromBase64Url(q["s"])!, q["o"], desktopId, phoneId);
    var req = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phoneId}\",\"name\":\"Probe iPhone\",\"id_pub\":\"{Hex(phId.Public)}\",\"ka_pub\":\"{Hex(phKa.Public)}\"}}";
    await Send(new { v = 2, id = RelayLink.Ulid(DateTimeOffset.UtcNow), from = phoneId, to = desktopId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), @class = "pair", push = "none", ck = "",
        ct = Seal(pairKey, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(req), Aad(phoneId, desktopId, "pair")), offer = q["o"] });
    Console.WriteLine($"{Now()} phone {phoneId} sent pair.request; phone check code {CheckCodeDisplay(CheckCode(dIdPub, dKaPub, phId.Public, phKa.Public))}");

    var accept = await Receive(e => IsEnv(e, "pair"), 120);
    var acc = accept is null ? null : Open(pairKey, accept.Value.GetProperty("ct").GetString(), Aad(desktopId, phoneId, "pair"), DesktopToPhone);
    var accJson = acc is null ? default : JsonDocument.Parse(acc).RootElement;
    var ok = acc is not null && Verify(dIdPub, AcceptMessage(desktopId, phoneId, dIdPub, dKaPub, phId.Public, phKa.Public), FromBase64Url(accJson.GetProperty("sig").GetString()));
    Console.WriteLine($"{Now()} {(ok ? "PASS" : "FAIL")}  pair.accept from \"{(acc is null ? "?" : accJson.GetProperty("name").GetString())}\" verifies under the scanned key");
    if (!ok) return 1;

    var basis = SessionBase(SharedSecret(phKa, dKaPub), desktopId, phoneId);
    JsonElement? OpenState(JsonElement env)
    {
        var ct = env.GetProperty("ct").GetString()!;
        var plain = Open(EpochKey(basis, PeekNonce(ct)!.Value.Epoch), ct, Aad(desktopId, phoneId, "state"), DesktopToPhone);
        return plain is null ? null : JsonDocument.Parse(plain).RootElement;
    }
    var failures = 0;
    var deltas = 0;
    long expect = 0;
    var gapDone = false;
    while (true)
    {
        var env = await Receive(e => IsEnv(e, "state"), 120);
        if (env is null) { Console.WriteLine($"{Now()} FAIL  no state frame within 120 s"); failures++; break; }
        var m = OpenState(env.Value);
        if (m is not { } msg) { Console.WriteLine($"{Now()} FAIL  a state frame did not open"); failures++; break; }
        var t = msg.GetProperty("t").GetString();
        var seq = msg.GetProperty("seq").GetInt64();
        if (t == "roster")
        {
            Console.WriteLine($"{Now()} roster seq {seq}, {msg.GetProperty("sessions").GetArrayLength()} sessions: " +
                string.Join(", ", msg.GetProperty("sessions").EnumerateArray().Select(x => $"{x.GetProperty("title").GetString()}={x.GetProperty("state").GetString()} since {x.GetProperty("since").GetInt64()}")));
            if (gapDone) { Console.WriteLine($"{Now()} PASS  resync brought a fresh roster from seq 1"); break; }
            expect = seq + 1;
            continue;
        }
        Console.WriteLine($"{Now()} delta seq {seq} {msg.GetProperty("session_id").GetString()} " +
            (msg.TryGetProperty("state", out var st) ? $"state={st.GetString()} " : "") + (msg.TryGetProperty("removed", out _) ? "removed" : ""));
        if (seq != expect) { Console.WriteLine($"{Now()} FAIL  expected seq {expect}"); failures++; }
        expect = seq + 1;
        if (++deltas == wantDeltas)
        {
            // Pretend the next delta is lost: wait for it, drop it, and on the
            // one after, see the gap and ask for a resync.
            var lost = await Receive(e => IsEnv(e, "state"), 120);
            var after = await Receive(e => IsEnv(e, "state"), 120);
            var afterSeq = after is null ? -1 : OpenState(after.Value)?.GetProperty("seq").GetInt64() ?? -1;
            Console.WriteLine($"{Now()} dropped seq {expect}, saw seq {afterSeq}: gap, sending resync");
            await Send(new { t = "resync", peer_id = desktopId });
            Console.WriteLine($"{Now()} sent resync peer_id={desktopId} from {phoneId}");
            gapDone = lost is not null && afterSeq == expect + 1;
            if (!gapDone) { failures++; break; }
        }
    }

    await Send(new { t = "unlink", peer_id = desktopId });
    var unl = await Receive(e => e.TryGetProperty("t", out var t) && t.GetString() == "ok" && e.TryGetProperty("ref", out var r) && r.GetString() == "unlink", 10);
    Console.WriteLine($"{Now()} {(unl is not null ? "PASS" : "FAIL")}  phone-side unlink acknowledged; the desktop is told err unlinked");
    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default); } catch { }
    return failures == 0 && unl is not null ? 0 : 1;
}
