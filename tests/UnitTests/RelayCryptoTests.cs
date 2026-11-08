using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-314: RelayCrypto against the contract vectors it generated, which live in
/// Orbweaver-Mobile <c>contracts/</c> and were cross-checked there by an
/// independent OpenSSL implementation. The iPhone's CryptoKit tests pin the
/// same bytes, so a change here that moves any of them fails on both sides
/// rather than as a phone that silently shows nothing.
/// </summary>
public class RelayCryptoVectorTests
{
    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", name))).RootElement;

    private static readonly JsonElement P = Load("pairing-vectors.json");
    private static readonly JsonElement S = Load("session-vectors.json");

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static byte[] Bytes(JsonElement e, string name) => Convert.FromHexString(Str(e, name));
    private static KeyPair Keys(JsonElement e, string name) =>
        new(Bytes(e.GetProperty(name), "private"), Bytes(e.GetProperty(name), "public"));

    private static readonly JsonElement In = P.GetProperty("inputs");
    private static readonly string DesktopId = Str(In, "desktop_id");
    private static readonly string PhoneId = Str(In, "phone_id");
    private static readonly string OfferId = Str(In, "offer_id");
    private static readonly KeyPair DskId = Keys(In, "desktop_identity");
    private static readonly KeyPair DskKa = Keys(In, "desktop_agreement");
    private static readonly KeyPair PhId = Keys(In, "phone_identity");
    private static readonly KeyPair PhKa = Keys(In, "phone_agreement");

    [Theory]
    [InlineData("desktop_identity")]
    [InlineData("desktop_agreement")]
    [InlineData("phone_identity")]
    [InlineData("phone_agreement")]
    public void Public_key_is_derived_from_the_fixed_scalar(string name)
    {
        var k = Keys(In, name);
        Assert.Equal(Hex(k.Public), Hex(KeyPairFromPrivate(k.Private).Public));
    }

    [Fact]
    public void Qr_text_matches()
    {
        var qr = PairQrText(Str(In, "relay_host"), DesktopId, OfferId, DskId.Public, DskKa.Public, Bytes(In, "pair_secret"));
        Assert.Equal(Str(P, "qr_text"), qr);
    }

    [Fact]
    public void Pair_key_matches() =>
        Assert.Equal(Str(P, "pair_key"), Hex(PairKey(Bytes(In, "pair_secret"), OfferId, DesktopId, PhoneId)));

    [Fact]
    public void Check_code_matches_in_all_three_forms()
    {
        var code = CheckCode(DskId.Public, DskKa.Public, PhId.Public, PhKa.Public);
        var v = P.GetProperty("check_code");
        Assert.Equal(v.GetProperty("int").GetInt32(), code);
        Assert.Equal(Str(v, "digits"), CheckCodeDigits(code));
        Assert.Equal(Str(v, "display"), CheckCodeDisplay(code));
    }

    [Fact]
    public void Check_code_keeps_leading_zeros() => Assert.Equal("004 213", CheckCodeDisplay(4213));

    [Fact]
    public void Register_message_matches()
    {
        var r = P.GetProperty("register");
        Assert.Equal(Str(r, "message"), Hex(RegisterMessage(Str(r, "challenge"))));
    }

    [Fact]
    public void Pair_request_seals_to_the_vector_and_opens()
    {
        var r = P.GetProperty("pair_request");
        var key = Bytes(P, "pair_key");
        var aad = Aad(Str(r, "from"), Str(r, "to"), Str(r, "class"));
        Assert.Equal(Str(r, "aad"), Hex(aad));
        Assert.Equal(Str(r, "nonce"), Hex(Nonce(PhoneToDesktop, 0, 0)));
        Assert.Equal(Str(r, "ct"), Seal(key, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(Str(r, "plaintext")), aad));
        Assert.Equal(Str(r, "plaintext"), Encoding.UTF8.GetString(Open(key, Str(r, "ct"), aad)!));
    }

    [Fact]
    public void Pair_accept_signature_message_and_box_match()
    {
        var a = P.GetProperty("pair_accept");
        var msg = AcceptMessage(DesktopId, PhoneId, DskId.Public, DskKa.Public, PhId.Public, PhKa.Public);
        Assert.Equal(Str(a, "sig_message"), Hex(msg));
        Assert.True(Verify(DskId.Public, msg, FromBase64Url(Str(a, "sig"))));
        var aad = Aad(DesktopId, PhoneId, "pair");
        Assert.Equal(Str(a, "aad"), Hex(aad));
        Assert.Equal(Str(a, "nonce"), Hex(Nonce(DesktopToPhone, 0, 0)));
        Assert.Equal(Str(a, "plaintext"), Encoding.UTF8.GetString(Open(Bytes(P, "pair_key"), Str(a, "ct"), aad)!));
    }

    [Fact]
    public void Pair_revoke_messages_match_both_directions()
    {
        var bd = P.GetProperty("pair_revoke").GetProperty("by_desktop");
        var bp = P.GetProperty("pair_revoke").GetProperty("by_phone");
        Assert.Equal(Str(bd, "sig_message"), Hex(RevokeMessage(DesktopId, PhoneId)));
        Assert.True(Verify(DskId.Public, RevokeMessage(DesktopId, PhoneId), FromBase64Url(Str(bd, "sig"))));
        Assert.Equal(Str(bp, "sig_message"), Hex(RevokeMessage(PhoneId, DesktopId)));
    }

    [Fact]
    public void Every_pairing_negative_is_refused()
    {
        var code = CheckCode(DskId.Public, DskKa.Public, PhId.Public, PhKa.Public);
        var seen = 0;
        foreach (var n in P.GetProperty("negatives").EnumerateArray())
        {
            seen++;
            switch (Str(n, "expect"))
            {
                case "open_fails":
                    Assert.Null(Open(Bytes(n, "key"), Str(n, "ct"), Bytes(n, "aad")));
                    break;
                case "verify_fails":
                    Assert.False(Verify(Bytes(n, "public"), Bytes(n, "sig_message"), FromBase64Url(Str(n, "sig"))));
                    break;
                case "differs_from_check_code":
                    var swapped = CheckCode(DskId.Public, DskKa.Public, PhKa.Public, PhId.Public);
                    Assert.Equal(n.GetProperty("check_code_int").GetInt32(), swapped);
                    Assert.NotEqual(code, swapped);
                    break;
                default:
                    Assert.Fail("unknown expectation " + Str(n, "expect"));
                    break;
            }
        }
        Assert.Equal(8, seen);
    }

    // ---- session ---------------------------------------------------------

    private static byte[] Base() => Bytes(S, "base");

    [Fact]
    public void Shared_secret_and_base_match_from_both_sides()
    {
        var i = S.GetProperty("inputs");
        var dka = Keys(i, "desktop_agreement");
        var pka = Keys(i, "phone_agreement");
        Assert.Equal(Str(S, "shared_secret"), Hex(SharedSecret(dka, pka.Public)));
        Assert.Equal(Str(S, "shared_secret"), Hex(SharedSecret(pka, dka.Public)));
        Assert.Equal(Str(S, "base"), Hex(SessionBase(Bytes(S, "shared_secret"), Str(i, "desktop_id"), Str(i, "phone_id"))));
    }

    [Fact]
    public void Epoch_keys_match()
    {
        foreach (var e in S.GetProperty("epoch_keys").EnumerateArray())
            Assert.Equal(Str(e, "key"), Hex(EpochKey(Base(), (ushort)e.GetProperty("epoch").GetInt32())));
    }

    [Fact]
    public void Nonces_match_and_parse_back()
    {
        foreach (var n in S.GetProperty("nonces").EnumerateArray())
        {
            var dir = n.GetProperty("direction").GetUInt32();
            var epoch = (ushort)n.GetProperty("epoch").GetInt32();
            var counter = n.GetProperty("counter").GetUInt64();
            var nonce = Nonce(dir, epoch, counter);
            Assert.Equal(Str(n, "nonce"), Hex(nonce));
            Assert.Equal(new NonceParts(dir, epoch, counter), ParseNonce(nonce));
        }
    }

    [Fact]
    public void Ck_matches_and_is_epoch_independent()
    {
        Assert.Equal(Str(S, "ck_key"), Hex(CoalesceKey(Base())));
        foreach (var c in S.GetProperty("ck").EnumerateArray())
            Assert.Equal(Str(c, "ck"), Ck(Bytes(S, "ck_key"), Str(c, "session_id")));
    }

    [Fact]
    public void Seal_vectors_reproduce_byte_for_byte_and_open()
    {
        foreach (var s in S.GetProperty("seal").EnumerateArray())
        {
            var key = EpochKey(Base(), (ushort)s.GetProperty("epoch").GetInt32());
            Assert.Equal(Str(s, "key"), Hex(key));
            var nonce = Nonce(s.GetProperty("direction").GetUInt32(), (ushort)s.GetProperty("epoch").GetInt32(), s.GetProperty("counter").GetUInt64());
            var aad = Aad(Str(s, "from"), Str(s, "to"), Str(s, "class"));
            Assert.Equal(Str(s, "aad"), Hex(aad));
            Assert.Equal(Str(s, "ct"), Seal(key, nonce, Encoding.UTF8.GetBytes(Str(s, "plaintext")), aad));
            Assert.Equal(Str(s, "plaintext"), Encoding.UTF8.GetString(Open(key, Str(s, "ct"), aad)!));
            Assert.Equal(ParseNonce(nonce), PeekNonce(Str(s, "ct")));
        }
    }

    [Fact]
    public void Every_session_negative_is_refused()
    {
        var count = 0;
        foreach (var n in S.GetProperty("negatives").EnumerateArray())
        {
            count++;
            Assert.Null(Open(Bytes(n, "key"), Str(n, "ct"), Bytes(n, "aad")));
        }
        Assert.Equal(4, count);
    }

    [Fact]
    public void Replay_window_matches_every_case()
    {
        var last = S.GetProperty("replay").GetProperty("last");
        var window = new RelayReceiveWindow((ushort)last.GetProperty("epoch").GetInt32(), last.GetProperty("counter").GetUInt64());
        foreach (var c in S.GetProperty("replay").GetProperty("cases").EnumerateArray())
            Assert.Equal(c.GetProperty("accept").GetBoolean(),
                window.WouldAccept((ushort)c.GetProperty("epoch").GetInt32(), c.GetProperty("counter").GetUInt64()));
    }
}

/// <summary>
/// CB-314: the edges of RelayCrypto that the vectors do not reach — malformed
/// input off the network, the nonce bounds, and the send/receive counters
/// whose one job is never to reuse a nonce.
/// </summary>
public class RelayCryptoEdgeTests
{
    [Fact]
    public void Generated_key_pair_is_valid_and_agrees()
    {
        var a = GenerateKeyPair();
        var b = GenerateKeyPair();
        Assert.Equal(32, a.Private.Length);
        Assert.Equal(PublicKeyLength, a.Public.Length);
        Assert.True(IsValidPublicKey(a.Public));
        Assert.Equal(SharedSecret(a, b.Public), SharedSecret(b, a.Public));
    }

    [Fact]
    public void Invalid_public_keys_are_refused()
    {
        var good = GenerateKeyPair().Public;
        Assert.False(IsValidPublicKey(null));
        Assert.False(IsValidPublicKey(good[..64]));               // wrong length
        var compressed = (byte[])good.Clone(); compressed[0] = 0x02;
        Assert.False(IsValidPublicKey(compressed));                // not uncompressed
        var offCurve = new byte[PublicKeyLength]; offCurve[0] = 0x04;
        Assert.False(IsValidPublicKey(offCurve));                  // (0,0) is not on P-256
        Assert.Throws<CryptographicException>(() => SharedSecret(GenerateKeyPair(), good[..10]));
    }

    [Fact]
    public void Sign_verify_round_trip_and_refusals()
    {
        var k = GenerateKeyPair();
        var msg = "hello"u8.ToArray();
        var sig = Sign(k, msg);
        Assert.Equal(SignatureLength, sig.Length);
        Assert.True(Verify(k.Public, msg, sig));
        Assert.False(Verify(k.Public, "hellp"u8.ToArray(), sig));
        Assert.False(Verify(k.Public, msg, null));
        Assert.False(Verify(k.Public, msg, sig[..63]));
        Assert.False(Verify(new byte[PublicKeyLength], msg, sig));
    }

    [Theory]
    [InlineData(new byte[] { }, "")]
    [InlineData(new byte[] { 0xfb }, "-w")]              // length % 4 == 2, and '+' → '-'
    [InlineData(new byte[] { 0xff, 0xff }, "__8")]       // length % 4 == 3, and '/' → '_'
    [InlineData(new byte[] { 1, 2, 3 }, "AQID")]         // length % 4 == 0
    public void Base64url_round_trips(byte[] bytes, string text)
    {
        Assert.Equal(text, Base64Url(bytes));
        Assert.Equal(bytes, FromBase64Url(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A")]         // length % 4 == 1 can never be base64
    [InlineData("A*==")]      // not base64 at all
    public void Base64url_refuses_garbage(string? text) => Assert.Null(FromBase64Url(text));

    [Fact]
    public void Hex_round_trips_and_refuses_garbage()
    {
        Assert.Equal("00ff", Hex([0, 255]));
        Assert.Equal(new byte[] { 0, 255 }, FromHex("00FF"));
        Assert.Null(FromHex(null));
        Assert.Null(FromHex("zz"));
    }

    [Fact]
    public void Nonce_refuses_a_counter_past_48_bits_and_a_wrong_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Nonce(DesktopToPhone, 0, MaxCounter + 1));
        Assert.Throws<ArgumentException>(() => ParseNonce(new byte[11]));
        // A log line names the parts readably; also the record's generated members.
        Assert.Contains("Epoch = 4", new NonceParts(0, 4, 2).ToString());
    }

    [Fact]
    public void Open_and_peek_refuse_malformed_boxes_without_throwing()
    {
        var key = new byte[KeyLength];
        Assert.Null(Open(key, null, []));
        Assert.Null(Open(key, "not base64 !", []));
        Assert.Null(Open(key, Convert.ToBase64String(new byte[27]), []));
        Assert.Null(PeekNonce(null));
        Assert.Null(PeekNonce(Convert.ToBase64String(new byte[27])));
    }

    [Fact]
    public void Empty_plaintext_seals_and_opens()
    {
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        var ct = Seal(key, Nonce(DesktopToPhone, 4, 2), [], "a|b|c"u8.ToArray());
        Assert.Equal(NonceLength + TagLength, Convert.FromBase64String(ct).Length);
        Assert.Empty(Open(key, ct, "a|b|c"u8.ToArray())!);
    }
}

public class RelaySendCounterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fresh_pairing_counts_up_in_epoch_zero()
    {
        var c = RelaySendCounter.Fresh(T0);
        Assert.True(c.TryNext(T0, out var e, out var n, out var rotated));
        Assert.Equal((0, 0UL, false), (e, n, rotated));
        Assert.True(c.TryNext(T0, out e, out n, out rotated));
        Assert.Equal((0, 1UL, false), (e, n, rotated));
        Assert.Equal(2UL, c.Counter);
        Assert.Equal(T0, c.EpochStarted);
    }

    [Fact]
    public void Process_start_moves_to_a_fresh_epoch_whatever_the_counter_was()
    {
        var c = new RelaySendCounter(3, 17, T0);
        c.Start(T0.AddMinutes(1));
        Assert.Equal((4, 0UL), (c.Epoch, c.Counter));
        Assert.Equal(T0.AddMinutes(1), c.EpochStarted);
    }

    [Fact]
    public void Ten_thousand_messages_rotate_the_epoch()
    {
        var c = new RelaySendCounter(1, RelayCrypto.MessagesPerEpoch - 1, T0);
        Assert.True(c.TryNext(T0, out var e, out var n, out var rotated));
        Assert.Equal((1, 9999UL, false), (e, n, rotated));
        Assert.True(c.TryNext(T0, out e, out n, out rotated));
        Assert.Equal((2, 0UL, true), (e, n, rotated));
    }

    [Fact]
    public void Twenty_four_hours_rotate_the_epoch()
    {
        var c = new RelaySendCounter(1, 5, T0);
        Assert.True(c.TryNext(T0.AddHours(24), out var e, out var n, out var rotated));
        Assert.Equal((2, 0UL, true), (e, n, rotated));
    }

    [Fact]
    public void The_last_epoch_exhausts_instead_of_wrapping()
    {
        var c = new RelaySendCounter(ushort.MaxValue, RelayCrypto.MessagesPerEpoch, T0);
        Assert.False(c.TryNext(T0, out var e, out _, out var rotated));
        Assert.True(rotated);
        Assert.True(c.Exhausted);
        Assert.Equal(ushort.MaxValue, e);
        Assert.False(c.TryNext(T0, out _, out _, out rotated));
        Assert.False(rotated);                                  // already exhausted: no further rotation
    }

    [Fact]
    public void Starting_in_the_last_epoch_exhausts()
    {
        var c = new RelaySendCounter(ushort.MaxValue, 0, T0);
        c.Start(T0);
        Assert.True(c.Exhausted);
        Assert.False(c.TryNext(T0, out _, out _, out _));
        Assert.True(new RelaySendCounter(1, 0, T0, exhausted: true).Exhausted);
    }
}

public class RelayReceiveWindowTests
{
    [Fact]
    public void An_empty_window_accepts_anything_then_only_strictly_greater()
    {
        var w = new RelayReceiveWindow();
        Assert.False(w.Any);
        Assert.True(w.WouldAccept(0, 0));
        w.Accept(0, 0);
        Assert.True(w.Any);
        Assert.False(w.WouldAccept(0, 0));
        Assert.True(w.WouldAccept(0, 1));
        Assert.True(w.WouldAccept(1, 0));
        w.Accept(1, 0);
        Assert.Equal((1, 0UL), (w.Epoch, w.Counter));
        Assert.False(w.WouldAccept(0, 500));
    }
}
