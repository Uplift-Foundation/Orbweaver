using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbweaver;
using static Orbweaver.RelayCrypto;

// CB-315: a stand-in phone that answers amber prompts, for QA's gate controls.
//
//   dotnet run --project tools/relay-probe -- [relay] --answer-phone --qr <qr text | @file>
//
// Pairs with whatever desktop made the QR (the installed app under
// ORBWEAVER_RELAY_TEST_HOOKS=1), then prints every action frame the desktop
// sends — prompt (with its e opened), resolved, err — and reads commands on
// stdin to answer the LAST prompt seen, signing exactly as the iPhone does:
// ECDSA P-256 over UTF-8("orbweaver-answer-v1") || the canonical body, by this
// phone's identity key. Every field can be forged, one per control:
//
//   answer <action> [scope=…] [note=…]       a correctly signed answer to the
//          [prompt:N]                        latest prompt, or to the Nth seen
//          [set:<field>=<value>]...          change a field AFTER signing
//                                            (request_id session_id action scope
//                                            note nonce expires_at screen_hash sig)
//          [signbody:<field>=<value>]...     sign a body with this field changed,
//                                            send the real one (a bad signature)
//          [sign:other]                      sign with a different key
//          [sign:watch]                      sign with the watch key and mark
//                                            it key:"watch" (CB-323); key=<v>
//                                            forges the marker on its own
//   watch add [at=N] [sign:watch]            CB-323: publish this phone's watch
//                                            key (key.add, signed by the
//                                            identity key; sign:watch is the
//                                            "key vouches for itself" control)
//   watch remove [at=N] [sign:id]            withdraw it (key.remove, signed by
//                                            the watch key; sign:id is a control)
//   watch new                                make a fresh watch key (not sent)
//   hold / answer … held                     snapshot the latest prompt now, answer it later
//   replay                                   resend the last answer, same nonce
//   wait <seconds>                           sleep before the next command
//   unlink                                   unlink from the phone side and exit
//   quit                                     exit without unlinking
//
// For tell and other, scope defaults to the SHA-256 hex of note, as the phone computes it.
internal static class AnswerPhone
{
    private static string Now() => DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");

    internal static async Task<int> RunAsync(Uri baseUrl, string qrText)
    {
        var q = qrText[(qrText.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
        var k = FromBase64Url(q["k"])!;
        var (dIdPub, dKaPub, desktopId) = (k[..65], k[65..], q["d"]);
        var phId = GenerateKeyPair();
        var phKa = GenerateKeyPair();
        var other = GenerateKeyPair();
        var watch = GenerateKeyPair();
        using var http = new HttpClient { BaseAddress = baseUrl };
        var body = new Dictionary<string, string> { ["platform"] = "ios", ["app_version"] = "probe", ["name"] = "Probe iPhone", ["id_pub"] = Hex(phId.Public), ["ka_pub"] = Hex(phKa.Public) };
        var first = await (await http.PostAsJsonAsync("/v1/register", body)).Content.ReadFromJsonAsync<JsonElement>();
        body["challenge"] = first.GetProperty("challenge").GetString()!;
        body["sig"] = Base64Url(Sign(phId, RegisterMessage(FromBase64Url(body["challenge"])!)));
        var reg = await (await http.PostAsJsonAsync("/v1/register", body)).Content.ReadFromJsonAsync<JsonElement>();
        var phoneId = reg.GetProperty("device_id").GetString()!;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new UriBuilder(baseUrl) { Scheme = baseUrl.Scheme == "http" ? "ws" : "wss", Path = "/v1/socket" }.Uri, default);
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(object f)
        {
            await sendLock.WaitAsync();
            try { await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(f), WebSocketMessageType.Text, true, default); }
            finally { sendLock.Release(); }
        }

        var buf = new byte[256 * 1024];
        async Task<JsonElement?> Receive(int seconds)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                var len = 0;
                ValueWebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf.AsMemory(len), cts.Token); len += r.Count; } while (!r.EndOfMessage);
                return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, len)).RootElement.Clone();
            }
            catch (OperationCanceledException) { return null; }
        }

        await Send(new { t = "hello", device_id = phoneId, token = reg.GetProperty("token").GetString(), platform = "ios", app_version = "probe", proto = 2 });
        await Receive(10);
        var pairKey = PairKey(FromBase64Url(q["s"])!, q["o"], desktopId, phoneId);
        var req = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phoneId}\",\"name\":\"Probe iPhone\",\"id_pub\":\"{Hex(phId.Public)}\",\"ka_pub\":\"{Hex(phKa.Public)}\"}}";
        await Send(new { v = 2, id = RelayLink.Ulid(DateTimeOffset.UtcNow), from = phoneId, to = desktopId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), @class = "pair", push = "none", ck = "",
            ct = Seal(pairKey, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(req), Aad(phoneId, desktopId, "pair")), offer = q["o"] });
        Console.WriteLine($"{Now()} phone {phoneId} sent pair.request; check code {CheckCodeDisplay(CheckCode(dIdPub, dKaPub, phId.Public, phKa.Public))}");

        JsonElement? accept = null;
        for (var i = 0; i < 200 && accept is null; i++)
        {
            var e = await Receive(120);
            if (e is null) break;
            if (e.Value.TryGetProperty("v", out _) && e.Value.GetProperty("class").GetString() == "pair") accept = e;
        }
        var acc = accept is null ? null : Open(pairKey, accept.Value.GetProperty("ct").GetString(), Aad(desktopId, phoneId, "pair"), DesktopToPhone);
        if (acc is null || !Verify(dIdPub, AcceptMessage(desktopId, phoneId, dIdPub, dKaPub, phId.Public, phKa.Public),
                FromBase64Url(JsonDocument.Parse(acc).RootElement.GetProperty("sig").GetString())))
        {
            Console.WriteLine($"{Now()} FAIL  no verified pair.accept");
            return 1;
        }
        Console.WriteLine($"{Now()} paired with {desktopId}; waiting for prompts (commands on stdin)");

        var basis = SessionBase(SharedSecret(phKa, dKaPub), desktopId, phoneId);
        ulong sent = 0;
        JsonElement? lastPrompt = null;
        var prompts = new List<JsonElement>();   // every prompt, in arrival order, for prompt:N
        JsonElement? held = null;                 // `hold` snapshots the latest prompt; `answer … held` answers it
        object? lastAnswer = null;

        Task SendSealed(string plaintext, string cls = "action") =>
            Send(new { v = 2, id = RelayLink.Ulid(DateTimeOffset.UtcNow), from = phoneId, to = desktopId, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                @class = cls, push = "none", ck = "",
                ct = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, sent++), Encoding.UTF8.GetBytes(plaintext), Aad(phoneId, desktopId, cls)) });

        // Reader: print every frame; remember the last prompt.
        var done = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                var e = await Receive(3600);
                if (e is null) continue;
                var f = e.Value;
                if (f.TryGetProperty("t", out var tt))
                {
                    if (tt.GetString() == "ping") { await Send(new { t = "pong" }); continue; }
                    Console.WriteLine($"{Now()} relay {f.GetRawText()}");
                    continue;
                }
                var cls = f.GetProperty("class").GetString()!;
                var ct = f.GetProperty("ct").GetString()!;
                var plain = Open(EpochKey(basis, PeekNonce(ct)!.Value.Epoch), ct, Aad(desktopId, phoneId, cls), DesktopToPhone);
                if (plain is null) { Console.WriteLine($"{Now()} FAIL  a {cls} frame did not open"); continue; }
                var msg = JsonDocument.Parse(plain).RootElement.Clone();
                // CB-323: the desktop's key.ok / key.err for a watch key frame.
                if (cls == "pair" && msg.TryGetProperty("t", out var kt) && kt.GetString()!.StartsWith("key.", StringComparison.Ordinal))
                {
                    Console.WriteLine($"{Now()} pair {Encoding.UTF8.GetString(plain)}");
                    continue;
                }
                if (cls != "action") continue;   // rosters and deltas: not this tool's business
                var push = f.TryGetProperty("push", out var p) ? p.GetString() : "?";
                Console.WriteLine($"{Now()} {cls} push={push} id={f.GetProperty("id").GetString()} {Encoding.UTF8.GetString(plain)}");
                if (f.TryGetProperty("e", out var ee))
                {
                    var pt = PushSeal.Open(phKa, ee.GetString());
                    Console.WriteLine($"{Now()}   e ({ee.GetString()!.Length} chars) {(pt is null ? "DID NOT OPEN" : Encoding.UTF8.GetString(pt))}");
                }
                if (msg.GetProperty("t").GetString() == "prompt")
                {
                    lock (prompts) prompts.Add(msg);
                    lastPrompt = msg;
                    Console.WriteLine($"{Now()}   (prompt #{prompts.Count})");
                }
            }
        });

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            var words = Tokens(line);
            if (words.Count == 0) continue;
            switch (words[0])
            {
                case "quit":
                    done.Cancel();
                    return 0;
                case "unlink":
                    await Send(new { t = "unlink", peer_id = desktopId });
                    await Task.Delay(1000);
                    done.Cancel();
                    return 0;
                case "wait":
                    await Task.Delay(TimeSpan.FromSeconds(double.Parse(words[1], System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                case "hold":
                    held = lastPrompt;
                    Console.WriteLine($"{Now()} held {(held is { } h ? h.GetProperty("request_id").GetString() : "nothing")}");
                    break;
                case "watch":
                    if (words.Count > 1 && words[1] == "new")
                    {
                        watch = GenerateKeyPair();
                        Console.WriteLine($"{Now()} new watch key {Hex(watch.Public)[..16]}…");
                        break;
                    }
                    var op = words.Count > 1 && words[1] == "remove" ? "remove" : "add";
                    var at = words.FirstOrDefault(w => w.StartsWith("at=")) is { } atw ? long.Parse(atw[3..]) : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var keySigner = op == "add" ? (words.Contains("sign:watch") ? watch : phId) : (words.Contains("sign:id") ? phId : watch);
                    var keySig = Sign(keySigner, RelayWatchKey.Message(RelayWatchKey.Body(op, "watch", Hex(watch.Public), phoneId, at)));
                    var keyText = JsonSerializer.Serialize(new { t = "key." + op, kind = "watch", pub = Hex(watch.Public), at, sig = Base64Url(keySig) });
                    await SendSealed(keyText, "pair");
                    Console.WriteLine($"{Now()} sent {keyText}");
                    break;
                case "replay":
                    if (lastAnswer is null) { Console.WriteLine("no answer to replay"); break; }
                    await SendSealed(JsonSerializer.Serialize(lastAnswer));
                    Console.WriteLine($"{Now()} sent replay");
                    break;
                case "answer":
                    // prompt:N answers the Nth prompt seen (1-based) instead of the
                    // latest — what a rule-6 control needs, since the desktop
                    // retires a redrawn dialog's prompt and sends a new one.
                    var pick = words.FirstOrDefault(w => w.StartsWith("prompt:"));
                    JsonElement? chosen;
                    lock (prompts) chosen = words.Contains("held") ? held
                        : pick is null ? lastPrompt : int.TryParse(pick[7..], out var nth) && nth >= 1 && nth <= prompts.Count ? prompts[nth - 1] : null;
                    words = words.Where(w => w != "held").ToList();
                    if (chosen is not { } pr) { Console.WriteLine("no such prompt"); break; }
                    words = words.Where(w => !w.StartsWith("prompt:")).ToList();
                    var f = new Dictionary<string, object>
                    {
                        ["t"] = "answer",
                        ["request_id"] = pr.GetProperty("request_id").GetString()!,
                        ["session_id"] = pr.GetProperty("session_id").GetString()!,
                        ["action"] = words[1],
                        ["scope"] = "",
                        ["note"] = "",
                        ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
                        ["expires_at"] = pr.GetProperty("expires_at").GetInt64(),
                        ["screen_hash"] = pr.GetProperty("screen_hash").GetString()!,
                    };
                    var sets = new List<(string, string)>();
                    var signBody = new Dictionary<string, object>();
                    var signer = phId;
                    foreach (var w in words.Skip(2))
                    {
                        if (w == "sign:other") signer = other;
                        else if (w == "sign:watch") { signer = watch; f["key"] = "watch"; }
                        else if (w.StartsWith("set:")) sets.Add(Split(w[4..]));
                        else if (w.StartsWith("signbody:")) { var (a, b) = Split(w[9..]); signBody[a] = Typed(a, b); }
                        else { var (a, b) = Split(w); f[a] = b; }
                    }
                    // tell and (CB-323) other bind their note through scope.
                    if ((string)f["action"] is "tell" or "other" && (string)f["scope"] == "") f["scope"] = AnswerSigning.TellScope((string)f["note"]);
                    var s = new Dictionary<string, object>(f);
                    foreach (var (a, b) in signBody) s[a] = b;
                    var bodyText = AnswerSigning.Body((string)s["request_id"], (string)s["session_id"], (string)s["action"], (string)s["scope"], (string)s["nonce"], (long)s["expires_at"]);
                    f["sig"] = Base64Url(Sign(signer, AnswerSigning.Message(bodyText)));
                    foreach (var (a, b) in sets) f[a] = Typed(a, b);
                    lastAnswer = f;
                    var text = JsonSerializer.Serialize(f);
                    await SendSealed(text);
                    Console.WriteLine($"{Now()} sent {text}");
                    break;
                default:
                    Console.WriteLine($"unknown command {words[0]}");
                    break;
            }
        }
        done.Cancel();
        await Task.WhenAny(reader, Task.Delay(100));
        return 0;
    }

    private static object Typed(string field, string value) => field == "expires_at" ? long.Parse(value) : value;

    private static (string, string) Split(string kv)
    {
        var i = kv.IndexOf('=');
        return (kv[..i], kv[(i + 1)..]);
    }

    // Words separated by spaces; a value may be "double quoted" to hold spaces.
    private static List<string> Tokens(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
