using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Orbweaver.RelayCrypto;

// Writes the phone relay's contract vectors (CB-314 plan §1, §2.2, §2.3):
//
//   dotnet run --project tools/relay-vectors -- <out-dir> [<orbweaver-sha>]
//
// produces <out-dir>/pairing-vectors.json and <out-dir>/session-vectors.json.
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
    ["check_code"] = new JsonObject
    {
        ["int"] = code,
        ["digits"] = CheckCodeDigits(code),
        ["display"] = CheckCodeDisplay(code),
    },
    ["register"] = new JsonObject
    {
        ["challenge"] = "Zm9yLXZlY3RvcnMtb25seQ",
        ["message"] = Hex(RegisterMessage("Zm9yLXZlY3RvcnMtb25seQ")),
        ["_note"] = "message = UTF-8(\"orbweaver-register-v1\") || UTF-8(challenge string as received); sig is ECDSA-P256-SHA256 raw r||s by the identity key",
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
        ["by_phone"] = new JsonObject { ["by"] = phoneId, ["peer_id"] = desktopId, ["sig_message"] = Hex(revokeByPhone) },
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
    "\"cli\":\"claude\",\"title\":\"orbweaver\",\"cwd\":\"/Users/someone/Source/Orbweaver\",\"state\":\"generating\",\"since\":\"2026-11-08T08:00:00Z\"}]}";
const string deltaPlain = "{\"t\":\"delta\",\"seq\":2,\"session_id\":\"8a6f0c1e-2b3d-4e5f-9a8b-7c6d5e4f3a2b\",\"state\":\"idle\",\"since\":\"2026-11-08T08:00:05Z\"}";
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
    ["ck_key"] = Hex(ckKey),
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

var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(outDir, "pairing-vectors.json"), pairing.ToJsonString(options) + "\n");
File.WriteAllText(Path.Combine(outDir, "session-vectors.json"), session.ToJsonString(options) + "\n");
Console.WriteLine($"wrote pairing-vectors.json and session-vectors.json to {Path.GetFullPath(outDir)} (check code {CheckCodeDisplay(code)})");
