using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbweaver;
using static Orbweaver.RelayCrypto;

// Writes the phone relay's contract vectors (CB-314 plan §1, §2.2, §2.3):
//
//   dotnet run --project tools/relay-vectors -- <out-dir> [<orbweaver-sha>]
//
// produces <out-dir>/pairing-vectors.json, session-vectors.json and
// roster-fixtures.json.
// They go to Orbweaver-Mobile `contracts/`, where the iPhone's CryptoKit tests
// consume them, and a copy goes to tests/UnitTests/Fixtures/relay/, where
// RelayCryptoVectorTests recomputes every value from RelayCrypto. A byte that
// drifts on either side then fails a build instead of a pairing.
//
// Every input is fixed — private scalars are SHA-256 of a label — so a rerun
// reproduces every byte except the ECDSA signatures, which are randomized by
// design. Signature vectors therefore give the exact message bytes and one
// valid signature to verify; nobody should byte-compare a signature.

var outDir = args.Length > 0 ? args[0] : ".";
var sha = args.Length > 1 ? args[1] : "unknown";
Directory.CreateDirectory(outDir);

static byte[] Label(string label) => SHA256.HashData(Encoding.UTF8.GetBytes("orbweaver-vector:" + label));
static JsonObject Pair(KeyPair k) => new() { ["private"] = Hex(k.Private), ["public"] = Hex(k.Public) };
static byte[] Flip(byte[] b, int at) { var c = (byte[])b.Clone(); c[at] ^= 0x01; return c; }
static string FlipCt(string ct) { var b = Convert.FromBase64String(ct); b[^1] ^= 0x01; return Convert.ToBase64String(b); }

const string relayHost = "orbweaver-relay.uplifttech.org";
const string desktopId = "dsk_5f3c9a0e1b7d2468";
const string phoneId = "ph_a1b2c3d4e5f60718";
const string offerId = "off_0f1e2d3c4b5a6978";
const string desktopName = "MacBook Pro";
const string phoneName = "Warren's iPhone";

var dskId = KeyPairFromPrivate(Label("desktop-identity"));
var dskKa = KeyPairFromPrivate(Label("desktop-agreement"));
var phId = KeyPairFromPrivate(Label("phone-identity"));
var phKa = KeyPairFromPrivate(Label("phone-agreement"));
var pairSecret = Label("pair-secret");
var challenge = Label("register-challenge");

var source = new JsonObject
{
    ["generated_by"] = "Uplift-Foundation/Orbweaver tools/relay-vectors (RelayCrypto.cs)",
    ["orbweaver_sha"] = sha,
    ["spec"] = "CB-314 architect plan rev 1, sections 2.2 and 2.3",
    ["encoding"] = "hex is lowercase; ct is standard base64 with padding of nonce(12) || ciphertext || tag(16); " +
                   "QR k/s, sig fields inside plaintexts, and ck are base64url without padding",
};

var inputs = new JsonObject
{
    ["relay_host"] = relayHost,
    ["desktop_id"] = desktopId,
    ["phone_id"] = phoneId,
    ["offer_id"] = offerId,
    ["desktop_name"] = desktopName,
    ["phone_name"] = phoneName,
    ["pair_secret"] = Hex(pairSecret),
    ["desktop_identity"] = Pair(dskId),
    ["desktop_agreement"] = Pair(dskKa),
    ["phone_identity"] = Pair(phId),
    ["phone_agreement"] = Pair(phKa),
};

// ---- pairing ------------------------------------------------------------

var pairKey = PairKey(pairSecret, offerId, desktopId, phoneId);
var code = CheckCode(dskId.Public, dskKa.Public, phId.Public, phKa.Public);

var reqPlain = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phoneId}\",\"name\":\"{phoneName}\"," +
               $"\"id_pub\":\"{Hex(phId.Public)}\",\"ka_pub\":\"{Hex(phKa.Public)}\"}}";
var reqAad = Aad(phoneId, desktopId, "pair");
var reqNonce = Nonce(PhoneToDesktop, 0, 0);
var reqCt = Seal(pairKey, reqNonce, Encoding.UTF8.GetBytes(reqPlain), reqAad);

var acceptMsg = AcceptMessage(desktopId, phoneId, dskId.Public, dskKa.Public, phId.Public, phKa.Public);
var acceptSig = Sign(dskId, acceptMsg);
var accPlain = $"{{\"t\":\"pair.accept\",\"desktop_id\":\"{desktopId}\",\"name\":\"{desktopName}\",\"sig\":\"{Base64Url(acceptSig)}\"}}";
var accAad = Aad(desktopId, phoneId, "pair");
var accNonce = Nonce(DesktopToPhone, 0, 0);
var accCt = Seal(pairKey, accNonce, Encoding.UTF8.GetBytes(accPlain), accAad);

var revokeByDesktop = RevokeMessage(desktopId, phoneId);
var revokeByPhone = RevokeMessage(phoneId, desktopId);
var revokeSig = Sign(dskId, revokeByDesktop);

var wrongSecretKey = PairKey(Flip(pairSecret, 0), offerId, desktopId, phoneId);
var wrongOfferKey = PairKey(pairSecret, "off_0000000000000000", desktopId, phoneId);
var swappedCode = CheckCode(dskId.Public, dskKa.Public, phKa.Public, phId.Public);

var pairing = new JsonObject
{
    ["_source"] = source.DeepClone(),
    ["inputs"] = inputs.DeepClone(),
    ["qr_text"] = PairQrText(relayHost, desktopId, offerId, dskId.Public, dskKa.Public, pairSecret),
    ["pair_key"] = Hex(pairKey),
    ["pair_key_hkdf"] = new JsonObject
    {
        ["ikm"] = Hex(pairSecret),
        ["salt"] = Hex(Encoding.UTF8.GetBytes(offerId)),
        ["info"] = Hex(Encoding.UTF8.GetBytes("orbweaver-pair-v1" + desktopId + phoneId)),
        ["length"] = 32,
    },
    ["check_code"] = new JsonObject
    {
        ["sha256_input"] = Hex(Encoding.UTF8.GetBytes("orbweaver-check-v1").Concat(dskId.Public).Concat(dskKa.Public).Concat(phId.Public).Concat(phKa.Public).ToArray()),
        ["int"] = code,
        ["digits"] = CheckCodeDigits(code),
        ["display"] = CheckCodeDisplay(code),
    },
    ["register"] = new JsonObject
    {
        ["challenge"] = Base64Url(challenge),
        ["challenge_raw"] = Hex(challenge),
        ["message"] = Hex(RegisterMessage(challenge)),
        ["sig"] = Base64Url(Sign(dskId, RegisterMessage(challenge))),
        ["_note"] = "message = UTF-8(\"orbweaver-register-v1\") || the RAW 32 challenge bytes (base64url-decoded, CB-314 addendum 1); sig is ECDSA-P256-SHA256 raw r||s by the identity key, verifies under inputs.desktop_identity.public",
    },
    ["pair_request"] = new JsonObject
    {
        ["from"] = phoneId, ["to"] = desktopId, ["class"] = "pair",
        ["plaintext"] = reqPlain,
        ["aad"] = Hex(reqAad),
        ["nonce"] = Hex(reqNonce),
        ["ct"] = reqCt,
    },
    ["pair_accept"] = new JsonObject
    {
        ["from"] = desktopId, ["to"] = phoneId, ["class"] = "pair",
        ["sig_message"] = Hex(acceptMsg),
        ["sig"] = Base64Url(acceptSig),
        ["plaintext"] = accPlain,
        ["aad"] = Hex(accAad),
        ["nonce"] = Hex(accNonce),
        ["ct"] = accCt,
    },
    ["pair_revoke"] = new JsonObject
    {
        ["by_desktop"] = new JsonObject { ["by"] = desktopId, ["peer_id"] = phoneId, ["sig_message"] = Hex(revokeByDesktop), ["sig"] = Base64Url(revokeSig) },
        ["by_phone"] = new JsonObject
        {
            ["by"] = phoneId, ["peer_id"] = desktopId, ["sig_message"] = Hex(revokeByPhone),
            ["sig"] = Base64Url(Sign(phId, revokeByPhone)),
            ["_note"] = "signed by the fixed SOFTWARE key inputs.phone_identity, standing in for the phone's Secure Enclave approval key (addendum 2), which cannot be put in a vector. A real SE signature is covered by surface C's test that has the .NET verifier check it.",
        },
    },
    ["negatives"] = new JsonArray
    {
        new JsonObject { ["name"] = "request_wrong_pair_secret", ["key"] = Hex(wrongSecretKey), ["ct"] = reqCt, ["aad"] = Hex(reqAad), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "request_wrong_offer_salt", ["key"] = Hex(wrongOfferKey), ["ct"] = reqCt, ["aad"] = Hex(reqAad), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "request_readdressed_aad", ["key"] = Hex(pairKey), ["ct"] = reqCt, ["aad"] = Hex(Aad(phoneId, "dsk_ffffffffffffffff", "pair")), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "request_tampered_tag", ["key"] = Hex(pairKey), ["ct"] = FlipCt(reqCt), ["aad"] = Hex(reqAad), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "accept_sig_tampered", ["public"] = Hex(dskId.Public), ["sig_message"] = Hex(acceptMsg), ["sig"] = Base64Url(Flip(acceptSig, 10)), ["expect"] = "verify_fails" },
        new JsonObject { ["name"] = "accept_sig_other_phone", ["public"] = Hex(dskId.Public), ["sig_message"] = Hex(AcceptMessage(desktopId, "ph_0000000000000000", dskId.Public, dskKa.Public, phId.Public, phKa.Public)), ["sig"] = Base64Url(acceptSig), ["expect"] = "verify_fails" },
        new JsonObject { ["name"] = "accept_sig_wrong_key", ["public"] = Hex(phId.Public), ["sig_message"] = Hex(acceptMsg), ["sig"] = Base64Url(acceptSig), ["expect"] = "verify_fails" },
        new JsonObject { ["name"] = "check_code_phone_keys_swapped", ["check_code_int"] = swappedCode, ["expect"] = "differs_from_check_code" },
        new JsonObject { ["name"] = "revoke_by_phone_sig_tampered", ["public"] = Hex(phId.Public), ["sig_message"] = Hex(revokeByPhone), ["sig"] = Base64Url(Flip(Sign(phId, revokeByPhone), 20)), ["expect"] = "verify_fails" },
        new JsonObject { ["name"] = "register_sig_over_challenge_text", ["public"] = Hex(dskId.Public), ["sig_message"] = Hex(RegisterMessage(challenge)), ["sig"] = Base64Url(Sign(dskId, Encoding.UTF8.GetBytes("orbweaver-register-v1" + Base64Url(challenge)))), ["expect"] = "verify_fails" },
    },
};

// ---- session ------------------------------------------------------------

var ssDesktop = SharedSecret(dskKa, phKa.Public);
var ssPhone = SharedSecret(phKa, dskKa.Public);
if (!ssDesktop.AsSpan().SequenceEqual(ssPhone)) throw new InvalidOperationException("ECDH disagrees with itself");
var sessionBase = SessionBase(ssDesktop, desktopId, phoneId);
var ckKey = CoalesceKey(sessionBase);

var epochs = new JsonArray();
foreach (ushort e in new ushort[] { 0, 1, 2, 65535 })
    epochs.Add(new JsonObject { ["epoch"] = e, ["key"] = Hex(EpochKey(sessionBase, e)) });

var nonces = new JsonArray();
foreach (var (dir, e, c) in new (uint, ushort, ulong)[] { (DesktopToPhone, 0, 0), (PhoneToDesktop, 0, 0), (DesktopToPhone, 1, 9999), (PhoneToDesktop, 258, 0x0102030405), (DesktopToPhone, 65535, MaxCounter) })
    nonces.Add(new JsonObject { ["direction"] = dir, ["epoch"] = e, ["counter"] = c, ["nonce"] = Hex(Nonce(dir, e, c)) });

var cks = new JsonArray();
foreach (var sid in new[] { "8a6f0c1e-2b3d-4e5f-9a8b-7c6d5e4f3a2b", "b2c4d6e8-0a1b-4c2d-8e3f-a4b5c6d7e8f9", "x" })
    cks.Add(new JsonObject { ["session_id"] = sid, ["ck"] = Ck(ckKey, sid) });

const string rosterPlain = "{\"t\":\"roster\",\"seq\":1,\"sessions\":[{\"session_id\":\"8a6f0c1e-2b3d-4e5f-9a8b-7c6d5e4f3a2b\"," +
    "\"cli\":\"claude\",\"title\":\"orbweaver\",\"cwd\":\"/Users/someone/Source/Orbweaver\",\"state\":\"generating\",\"since\":1794124800}]}";
const string deltaPlain = "{\"t\":\"delta\",\"seq\":2,\"session_id\":\"8a6f0c1e-2b3d-4e5f-9a8b-7c6d5e4f3a2b\",\"state\":\"idle\",\"since\":1794124805}";
const string phonePlain = "{\"t\":\"probe\",\"note\":\"milestone 2 sends nothing sealed phone-to-desktop on the socket; this vector pins the direction prefix\"}";

JsonObject SealVector(string name, uint dir, ushort e, ulong c, string from, string to, string cls, string plain)
{
    var nonce = Nonce(dir, e, c);
    var aad = Aad(from, to, cls);
    return new JsonObject
    {
        ["name"] = name, ["direction"] = dir, ["epoch"] = e, ["counter"] = c,
        ["from"] = from, ["to"] = to, ["class"] = cls,
        ["key"] = Hex(EpochKey(sessionBase, e)),
        ["nonce"] = Hex(nonce), ["aad"] = Hex(aad), ["plaintext"] = plain,
        ["aad_text"] = $"{from}|{to}|{cls}",
        ["ct"] = Seal(EpochKey(sessionBase, e), nonce, Encoding.UTF8.GetBytes(plain), aad),
    };
}

var rosterVec = SealVector("desktop_roster", DesktopToPhone, 1, 0, desktopId, phoneId, "state", rosterPlain);
var deltaVec = SealVector("desktop_delta", DesktopToPhone, 1, 1, desktopId, phoneId, "state", deltaPlain);
var phoneVec = SealVector("phone_generic", PhoneToDesktop, 3, 7, phoneId, desktopId, "control", phonePlain);

var session = new JsonObject
{
    ["_source"] = source.DeepClone(),
    ["inputs"] = new JsonObject
    {
        ["desktop_id"] = desktopId,
        ["phone_id"] = phoneId,
        ["desktop_agreement"] = Pair(dskKa),
        ["phone_agreement"] = Pair(phKa),
    },
    ["shared_secret"] = Hex(ssDesktop),
    ["base"] = Hex(sessionBase),
    ["base_hkdf"] = new JsonObject { ["ikm"] = Hex(ssDesktop), ["salt"] = "", ["info"] = Hex(Encoding.UTF8.GetBytes("orbweaver-session-v1" + desktopId + phoneId)), ["length"] = 32 },
    ["epoch_hkdf"] = new JsonObject { ["ikm"] = "base", ["salt"] = "BE16(epoch), e.g. epoch 1 = 0001", ["info"] = Hex(Encoding.UTF8.GetBytes("orbweaver-epoch-v1")), ["length"] = 32 },
    ["ck_key"] = Hex(ckKey),
    ["ck_hkdf"] = new JsonObject { ["ikm"] = "base", ["salt"] = "", ["info"] = Hex(Encoding.UTF8.GetBytes("orbweaver-ck-v1")), ["length"] = 32 },
    ["epoch_keys"] = epochs,
    ["nonces"] = nonces,
    ["ck"] = cks,
    ["seal"] = new JsonArray { rosterVec, deltaVec, phoneVec },
    ["negatives"] = new JsonArray
    {
        new JsonObject { ["name"] = "wrong_epoch_key", ["key"] = Hex(EpochKey(sessionBase, 2)), ["ct"] = rosterVec["ct"]!.GetValue<string>(), ["aad"] = rosterVec["aad"]!.GetValue<string>(), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "wrong_direction_class_aad", ["key"] = rosterVec["key"]!.GetValue<string>(), ["ct"] = rosterVec["ct"]!.GetValue<string>(), ["aad"] = Hex(Aad(desktopId, phoneId, "action")), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "tampered_tag", ["key"] = rosterVec["key"]!.GetValue<string>(), ["ct"] = FlipCt(rosterVec["ct"]!.GetValue<string>()), ["aad"] = rosterVec["aad"]!.GetValue<string>(), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "truncated", ["key"] = rosterVec["key"]!.GetValue<string>(), ["ct"] = Convert.ToBase64String(new byte[NonceLength + TagLength - 1]), ["aad"] = rosterVec["aad"]!.GetValue<string>(), ["expect"] = "open_fails" },
        new JsonObject { ["name"] = "wrong_direction_prefix", ["key"] = rosterVec["key"]!.GetValue<string>(), ["ct"] = rosterVec["ct"]!.GetValue<string>(), ["aad"] = rosterVec["aad"]!.GetValue<string>(), ["expected_direction"] = PhoneToDesktop, ["expect"] = "open_fails", ["_note"] = "a valid desktop-to-phone box opened by a receiver expecting phone-to-desktop: refused on the nonce prefix before decryption" },
    },
    ["replay"] = new JsonObject
    {
        ["_note"] = "receiver last accepted (epoch, counter) = (1, 5) in this direction; accept only strictly greater, compared epoch first",
        ["last"] = new JsonObject { ["epoch"] = 1, ["counter"] = 5 },
        ["cases"] = new JsonArray
        {
            new JsonObject { ["epoch"] = 1, ["counter"] = 5, ["accept"] = false },
            new JsonObject { ["epoch"] = 1, ["counter"] = 4, ["accept"] = false },
            new JsonObject { ["epoch"] = 0, ["counter"] = 9999, ["accept"] = false },
            new JsonObject { ["epoch"] = 1, ["counter"] = 6, ["accept"] = true },
            new JsonObject { ["epoch"] = 2, ["counter"] = 0, ["accept"] = true },
        },
    },
};

// ---- roster fixtures (plan §2.4, addendum 3) -----------------------------
// The plaintexts a phone decrypts, produced by RelayRoster's own serializer
// so their field names and omissions are exactly what the desktop sends.

var t0 = new DateTimeOffset(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);
RelaySession S(string id, string cli, string title, string cwd, string state, int sinceSec, string? parent = null, RelayPersona? persona = null) =>
    new RelaySession(id, cli, title, cwd, state, parent, persona) { Since = t0.AddSeconds(sinceSec) };

var lead = "8a6f0c1e-2b3d-4e5f-9a8b-7c6d5e4f3a2b";
var macbook = new[]
{
    S(lead, "claude", "CB-314 relay milestone", "/Users/someone/Source/Orbweaver", "generating", 0, persona: new RelayPersona("Jennifer Voss", "JV", "purple")),
    S("b2c4d6e8-0a1b-4c2d-8e3f-a4b5c6d7e8f9", "claude", "tamsin-oyelaran", "/Users/someone/Source/Orbweaver-wt/tamsin-oyelaran", "waiting", 12, parent: lead, persona: new RelayPersona("Tamsin Oyelaran", "TO", "pink")),
    S("c3d5e7f9-1b2c-4d3e-9f4a-b5c6d7e8f9a0", "codex", "Orbweaver-Mobile", "/Users/someone/Source/Orbweaver-Mobile", "idle", 300),
};
var windows = new[]
{
    S("0d1e2f3a-4b5c-4d6e-8f7a-9b0c1d2e3f4a", "claude", "orbweaver", @"C:\Users\someone\Source\Orbweaver", "idle", 40),
    S("1e2f3a4b-5c6d-4e7f-9a8b-0c1d2e3f4a5b", "grok", "scratch", @"C:\Users\someone\scratch", "generating", 55, persona: new RelayPersona("Pixel", "PI", null)),
};
JsonNode Parse(byte[] utf8) => JsonNode.Parse(utf8)!;
var deltas = new JsonArray
{
    Parse(RelayRoster.DeltaJson(2, new RelayDelta(lead, "idle", null, t0.AddSeconds(90), false))),
    Parse(RelayRoster.DeltaJson(3, new RelayDelta("b2c4d6e8-0a1b-4c2d-8e3f-a4b5c6d7e8f9", "generating", null, t0.AddSeconds(95), false))),
    Parse(RelayRoster.DeltaJson(4, new RelayDelta("c3d5e7f9-1b2c-4d3e-9f4a-b5c6d7e8f9a0", null, "Orbweaver-Mobile app shell", null, false))),
    Parse(RelayRoster.DeltaJson(5, new RelayDelta("b2c4d6e8-0a1b-4c2d-8e3f-a4b5c6d7e8f9", "ended", null, t0.AddSeconds(120), true))),
};
var rosters = new JsonObject
{
    ["_source"] = source.DeepClone(),
    ["_rules"] = "CB-314 addendum 3: a roster replaces that PC's whole book and resets expected seq to 1; deltas carry only state/title/since and count up by 1; removed:true shows ended once then drops the row; a seq gap means send resync. State is idle|generating|waiting, plus ended only in a removal delta. since is an INTEGER of Unix seconds, the same type and unit as ts and last_seen (addendum 4). parent_session_id and persona are omitted when absent; persona.color is optional and, when present, one of the lowercase names in persona-colors.json (never hex) — render absent or unknown names neutral.",
    ["macbook_roster"] = Parse(RelayRoster.RosterJson(1, macbook)),
    ["windows_roster"] = Parse(RelayRoster.RosterJson(1, windows)),
    ["empty_roster"] = Parse(RelayRoster.RosterJson(1, [])),
    ["macbook_deltas_after_roster"] = deltas,
    ["gap_example"] = new JsonObject { ["_note"] = "after macbook_roster (seq 1), receiving this seq 3 delta with no seq 2 is a gap: send resync {t,peer_id} and expect a fresh roster", ["delta"] = Parse(RelayRoster.DeltaJson(3, new RelayDelta(lead, "waiting", null, t0.AddSeconds(200), false))) },
};

var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(outDir, "pairing-vectors.json"), pairing.ToJsonString(options) + "\n");
File.WriteAllText(Path.Combine(outDir, "session-vectors.json"), session.ToJsonString(options) + "\n");
File.WriteAllText(Path.Combine(outDir, "roster-fixtures.json"), rosters.ToJsonString(options) + "\n");
// Addendum 4: the colour names a phone may be sent, straight from the list
// RelayRoster filters with, one JSON array.
File.WriteAllText(Path.Combine(outDir, "persona-colors.json"),
    new JsonArray(RelayRoster.PersonaColors.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()).ToJsonString(options) + "\n");
Console.WriteLine($"wrote pairing-vectors.json, session-vectors.json, roster-fixtures.json and persona-colors.json to {Path.GetFullPath(outDir)} (check code {CheckCodeDisplay(code)})");
