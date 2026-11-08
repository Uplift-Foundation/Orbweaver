using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-314: the desktop's relay protocol, driven frame by frame against a fake
/// phone that does its own half of the crypto. Each case is an event order a
/// real network can produce — including the ones nobody plans for: presence
/// before welcome, a request after the offer expired, a resync for a phone
/// already removed, a replayed revoke, a reconnect mid-session.
/// </summary>
public class RelayLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-link-" + Guid.NewGuid());
    private DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private const string DesktopId = "dsk_00000000000000aa";

    public void Dispose()
    {
        try { if (File.Exists(_dir)) File.Delete(_dir); else Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) => _items.TryGetValue(reference[4..], out var v) ? v : null;
        public void Forget(string reference) { }
    }

    private readonly MemoryStore _store = new();

    private RelayIdentity Identity()
    {
        var id = new RelayIdentity(_dir, _store, () => _now);
        id.Keys();
        if (id.DeviceId is null) id.SetRegistration(DesktopId, "token-1");
        return id;
    }

    private (RelayLink Link, RelayIdentity Identity, List<string> Notes) New(RelayIdentity? identity = null)
    {
        identity ??= Identity();
        var link = new RelayLink(identity, new RelaySessionBook(), () => _now, "mac", "1.0.4", "relay.example");
        var notes = new List<string>();
        link.Note += notes.Add;
        return (link, identity, notes);
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
    private static string? T(string frame) => J(frame).TryGetProperty("t", out var t) ? t.GetString() : null;
    private static string Welcome(params string[] links) =>
        JsonSerializer.Serialize(new { t = "ok", @ref = "hello", code = "welcome", links });
    private static string Presence(string id, bool online, long lastSeen = 1794111792) =>
        JsonSerializer.Serialize(new { t = "presence", device_id = id, online, last_seen = lastSeen });

    private static RelaySession Row(string id, string state = "idle") => new(id, "claude", "proj", "/c", state, null, null);

    // The phone's half, written against RelayCrypto's primitives the way the
    // iPhone does against CryptoKit's.
    private sealed class FakePhone(string phoneId = "ph_00000000000000b1")
    {
        public readonly string Id = phoneId;
        public readonly KeyPair IdKey = GenerateKeyPair();
        public readonly KeyPair KaKey = GenerateKeyPair();
        public byte[]? DesktopIdPub, DesktopKaPub, PairKey;
        public ulong Sent;
        public ushort Epoch = 1;

        public string Request(string qr, string? offerOverride = null, string? phoneIdInBody = null, bool tamper = false, string? idPubHex = null, string? name = "Warren's iPhone")
        {
            var q = qr[(qr.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
            var k = FromBase64Url(q["k"])!;
            DesktopIdPub = k[..65];
            DesktopKaPub = k[65..];
            PairKey = RelayCrypto.PairKey(FromBase64Url(q["s"])!, q["o"], q["d"], Id);
            var nameField = name is null ? "" : $"\"name\":\"{name}\",";
            var body = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phoneIdInBody ?? Id}\",{nameField}\"id_pub\":\"{idPubHex ?? Hex(IdKey.Public)}\",\"ka_pub\":\"{Hex(KaKey.Public)}\"}}";
            var ct = Seal(PairKey, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(body), Aad(Id, q["d"], "pair"));
            if (tamper) ct = ct[..^4] + (ct[^4] == 'A' ? "B" : "A") + ct[^3..];
            return JsonSerializer.Serialize(new { v = 2, id = "x", from = Id, to = q["d"], ts = 0, @class = "pair", push = "none", ck = "", ct, offer = offerOverride ?? q["o"] });
        }

        public byte[] Base() => SessionBase(SharedSecret(KaKey, DesktopKaPub!), DesktopId, Id);

        public JsonElement OpenFromDesktop(string frame)
        {
            var e = J(frame);
            var ct = e.GetProperty("ct").GetString()!;
            var n = PeekNonce(ct)!.Value;
            Assert.Equal(DesktopToPhone, n.Direction);
            var plain = Open(EpochKey(Base(), n.Epoch), ct, Aad(e.GetProperty("from").GetString()!, Id, e.GetProperty("class").GetString()!));
            Assert.NotNull(plain);
            return J(Encoding.UTF8.GetString(plain!));
        }

        public string Sealed(string cls, string body, ushort? epoch = null, ulong? counter = null, uint direction = PhoneToDesktop, string to = DesktopId)
        {
            var e = epoch ?? Epoch;
            var c = counter ?? Sent++;
            var ct = Seal(EpochKey(Base(), e), Nonce(direction, e, c), Encoding.UTF8.GetBytes(body), Aad(Id, to, cls));
            return JsonSerializer.Serialize(new { v = 2, id = "y", from = Id, to, ts = 0, @class = cls, push = "none", ck = "", ct });
        }

        public string Revoke(KeyPair? signer = null) =>
            Sealed("pair", $"{{\"t\":\"pair.revoke\",\"by\":\"{Id}\",\"sig\":\"{Base64Url(Sign(signer ?? IdKey, RevokeMessage(Id, DesktopId)))}\"}}");
    }

    // Welcome, offer, request, Allow: a phone paired and online.
    private (RelayLink Link, RelayIdentity Identity, FakePhone Phone, List<string> Notes, IReadOnlyList<string> AllowFrames) Paired()
    {
        var (link, identity, notes) = New();
        link.OnFrame(Welcome());
        var phone = new FakePhone();
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        RelayLink.PairRequest? asked = null;
        link.PairRequested += r => asked = r;
        link.OnFrame(phone.Request(offer.QrText));
        var frames = link.Allow(asked!, "MacBook Pro");
        return (link, identity, phone, notes, frames);
    }

    // ---- connection ------------------------------------------------------------

    [Fact]
    public void Hello_carries_identity_platform_and_proto_two()
    {
        var hello = J(New().Link.Hello());
        Assert.Equal(("hello", DesktopId, "token-1", "mac", "1.0.4", 2),
            (hello.GetProperty("t").GetString(), hello.GetProperty("device_id").GetString(), hello.GetProperty("token").GetString(),
             hello.GetProperty("platform").GetString(), hello.GetProperty("app_version").GetString(), hello.GetProperty("proto").GetInt32()));
        Assert.Equal("ping", T(RelayLink.Ping()));
        Assert.Same(RelayLink.Ping(), RelayLink.Ping());
    }

    [Fact]
    public void Welcome_raises_and_unauthorized_or_version_refuses()
    {
        var (link, _, _) = New();
        var events = new List<string>();
        link.WelcomeReceived += () => events.Add("welcome");
        link.Unauthorized += () => events.Add("unauthorized");
        link.Refused += code => events.Add("refused:" + code);

        Assert.Empty(link.OnFrame(Welcome()));
        Assert.True(link.Welcomed);
        link.OnFrame("""{"t":"err","ref":"hello","code":"unauthorized"}""");
        Assert.False(link.Welcomed);
        link.OnFrame("""{"t":"err","ref":"hello","code":"version"}""");
        link.OnFrame("""{"t":"err","ref":"hello"}""");
        Assert.Equal(["welcome", "unauthorized", "refused:version", "refused:unknown"], events);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"hello":1}""")]
    [InlineData("""{"t":"mystery"}""")]
    [InlineData("""{"t":"err","ref":"x","code":"rate_limited"}""")]
    [InlineData("""{"t":"pong"}""")]
    [InlineData("""{"t":5}""")]
    public void Junk_and_unknown_frames_produce_nothing(string frame) => Assert.Empty(New().Link.OnFrame(frame));

    [Fact]
    public void Ping_is_answered_with_pong() => Assert.Equal("pong", T(New().Link.OnFrame("""{"t":"ping"}""").Single()));

    // ---- pairing -----------------------------------------------------------------

    [Fact]
    public void Pairing_end_to_end_both_sides_agree_and_the_phone_can_verify()
    {
        var (link, identity, phone, _, frames) = Paired();
        var (dId, dKa) = identity.Keys();

        Assert.Equal(3, frames.Count);
        // 1. pair.accept, under the pair key, carrying the offer for the relay's routing.
        var accept = J(frames[0]);
        Assert.Equal(("pair", "off_1", phone.Id), (accept.GetProperty("class").GetString(), accept.GetProperty("offer").GetString(), accept.GetProperty("to").GetString()));
        var acceptBody = J(Encoding.UTF8.GetString(Open(phone.PairKey!, accept.GetProperty("ct").GetString()!, Aad(DesktopId, phone.Id, "pair"))!));
        Assert.Equal("pair.accept", acceptBody.GetProperty("t").GetString());
        Assert.Equal("MacBook Pro", acceptBody.GetProperty("name").GetString());
        Assert.True(Verify(phone.DesktopIdPub!, AcceptMessage(DesktopId, phone.Id, phone.DesktopIdPub!, phone.DesktopKaPub!, phone.IdKey.Public, phone.KaKey.Public),
            FromBase64Url(acceptBody.GetProperty("sig").GetString())));
        Assert.Equal(dId.Public, phone.DesktopIdPub);
        // 2. the relay-visible link.
        Assert.Equal("""{"t":"link","offer_id":"off_1","phone_id":"ph_00000000000000b1"}""", frames[1]);
        // 3. the first roster, under the session key, seq 1.
        var roster = phone.OpenFromDesktop(frames[2]);
        Assert.Equal(("roster", 1L), (roster.GetProperty("t").GetString(), roster.GetProperty("seq").GetInt64()));

        Assert.Equal(phone.KaKey.Public, identity.PhoneById(phone.Id)!.KaPublic);
        Assert.Null(link.Offer);
        Assert.Null(link.Pending);
        Assert.True(link.IsOnline(phone.Id));
        Assert.Equal(CheckCode(dId.Public, dKa.Public, phone.IdKey.Public, phone.KaKey.Public),
            CheckCode(phone.DesktopIdPub!, phone.DesktopKaPub!, phone.IdKey.Public, phone.KaKey.Public));
    }

    [Fact]
    public void The_pending_request_carries_the_check_code_and_name()
    {
        var (link, identity, _) = New();
        var phone = new FakePhone();
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        Assert.StartsWith("orbweaver://pair?r=relay.example&d=" + DesktopId + "&o=off_1&k=", offer.QrText);
        link.OnFrame(phone.Request(offer.QrText, name: ""));
        var (dId, dKa) = identity.Keys();
        Assert.Equal("iPhone", link.Pending!.Name);                 // empty name falls back
        Assert.Equal(CheckCodeDisplay(CheckCode(dId.Public, dKa.Public, phone.IdKey.Public, phone.KaKey.Public)), link.Pending.CheckCodeDisplay);
        link.OnFrame(new FakePhone("ph_00000000000000b2").Request(offer.QrText, name: null));
        Assert.Equal("iPhone", link.Pending!.Name);                 // and so does none at all
    }

    [Fact]
    public void Requests_that_do_not_match_a_live_offer_are_ignored()
    {
        var (link, _, notes) = New();
        var phone = new FakePhone();
        var raised = 0;
        link.PairRequested += _ => raised++;

        var qr = "orbweaver://pair?r=relay.example&d=" + DesktopId + "&o=off_1&k=" + Base64Url(new byte[130]) + "&s=" + Base64Url(new byte[32]);
        link.OnFrame(phone.Request(qr));                                      // no offer at all
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        link.OnFrame(phone.Request(offer.QrText, offerOverride: "off_other")); // another offer's id
        link.OnFrame(phone.Request(offer.QrText, tamper: true));               // does not open
        link.OnFrame(phone.Request(offer.QrText, phoneIdInBody: "ph_someone_else"));
        link.OnFrame(phone.Request(offer.QrText, idPubHex: "04" + new string('0', 128)));
        Assert.Equal(0, raised);
        Assert.Equal(2, offer.AttemptsLeft);                                  // three opened attempts spent

        _now = offer.ExpiresAt;                                               // expired
        link.OnFrame(phone.Request(offer.QrText));
        Assert.Equal(0, raised);
        Assert.Contains("pair.request for no live offer", notes);
    }

    [Fact]
    public void The_sixth_request_on_one_offer_is_refused()
    {
        var (link, _, _) = New();
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        var raised = 0;
        link.PairRequested += _ => raised++;
        for (var i = 0; i < 6; i++) link.OnFrame(new FakePhone().Request(offer.QrText));
        Assert.Equal(5, raised);
    }

    [Fact]
    public void A_wrong_direction_nonce_on_a_request_is_refused()
    {
        var (link, _, _) = New();
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        var phone = new FakePhone();
        phone.Request(offer.QrText);   // learn the pair key
        var ct = Seal(phone.PairKey!, Nonce(DesktopToPhone, 0, 0), "{\"t\":\"pair.request\"}"u8.ToArray(), Aad(phone.Id, DesktopId, "pair"));
        link.OnFrame(JsonSerializer.Serialize(new { v = 2, from = phone.Id, to = DesktopId, @class = "pair", ct, offer = "off_1" }));
        Assert.Null(link.Pending);
    }

    [Fact]
    public void A_request_whose_plaintext_is_not_a_pair_request_is_refused()
    {
        var (link, _, _) = New();
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        var phone = new FakePhone();
        phone.Request(offer.QrText);
        foreach (var body in new[] { "not json", "[1]", "{\"t\":\"pair.accept\"}" })
        {
            var ct = Seal(phone.PairKey!, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(body), Aad(phone.Id, DesktopId, "pair"));
            link.OnFrame(JsonSerializer.Serialize(new { v = 2, from = phone.Id, to = DesktopId, @class = "pair", ct, offer = "off_1" }));
        }
        Assert.Null(link.Pending);
    }

    [Fact]
    public void Allow_on_a_stale_or_expired_request_sends_nothing_and_deny_clears()
    {
        var (link, identity, _) = New();
        link.OnFrame(Welcome());
        var phone = new FakePhone();
        var offer = link.BeginPairing("off_1", _now.AddMinutes(5));
        link.OnFrame(phone.Request(offer.QrText));
        var first = link.Pending!;

        link.Deny(new RelayLink.PairRequest("x", "y", "z", [], [], [], 0));    // not the pending one: no-op
        Assert.Same(first, link.Pending);

        _now = offer.ExpiresAt;
        Assert.Empty(link.Allow(first, "Mac"));                                // expired
        _now = offer.ExpiresAt.AddMinutes(-1);

        var offer2 = link.BeginPairing("off_2", _now.AddMinutes(5));           // a new offer supersedes
        Assert.Empty(link.Allow(first, "Mac"));
        link.OnFrame(phone.Request(offer2.QrText));
        var second = link.Pending!;
        link.CancelPairing();
        Assert.Empty(link.Allow(second, "Mac"));                               // cancelled

        link.BeginPairing("off_3", _now.AddMinutes(5));
        link.OnFrame(phone.Request(link.Offer!.QrText));
        var third = link.Pending!;
        link.Deny(third);
        Assert.Null(link.Pending);
        Assert.Empty(link.Allow(third, "Mac"));                                // denied
        Assert.Empty(identity.Phones);
    }

    [Fact]
    public void Allow_while_disconnected_sends_nothing_and_keeps_the_request_for_another_try()
    {
        var (link, identity, _) = New();
        var phone = new FakePhone();
        link.OnFrame(phone.Request(link.BeginPairing("off_1", _now.AddMinutes(5)).QrText));
        var pending = link.Pending!;
        var paired = 0;
        link.PhonePaired += _ => paired++;

        Assert.Empty(link.Allow(pending, "Mac"));                   // no socket: nowhere to send the accept
        Assert.Same(pending, link.Pending);
        Assert.Empty(identity.Phones);
        Assert.Equal(0, paired);

        link.OnFrame(Welcome());
        var frames = link.Allow(pending, "Mac");                    // the second click, once connected
        Assert.Equal([null, "link", null], frames.Select(T));
        Assert.Equal("roster", phone.OpenFromDesktop(frames[2]).GetProperty("t").GetString());
        Assert.Equal(1, paired);
        Assert.Single(identity.Phones);
    }

    // ---- presence, scans, resync ---------------------------------------------------

    [Fact]
    public void Presence_drives_who_is_sent_frames()
    {
        var (link, _, phone, _, _) = Paired();
        var changed = 0;
        link.PresenceChanged += () => changed++;

        Assert.Empty(link.OnFrame(Presence("ph_stranger", true)));
        Assert.Empty(link.OnFrame("""{"t":"presence"}"""));
        Assert.Empty(link.OnFrame($$"""{"t":"presence","device_id":"{{phone.Id}}","last_seen":"yesterday"}"""));   // no online: offline
        Assert.False(link.IsOnline(phone.Id));
        Assert.Null(link.LastSeen(phone.Id));                                       // unparseable last_seen kept nothing
        Assert.Single(link.OnFrame($$"""{"t":"presence","device_id":"{{phone.Id}}","online":true}"""));             // back, no last_seen: roster
        Assert.Empty(link.OnFrame(Presence(phone.Id, true)));                       // already online
        Assert.Empty(link.OnFrame(Presence(phone.Id, false, lastSeen: 1794111000)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794111000), link.LastSeen(phone.Id));
        Assert.False(link.IsOnline(phone.Id));
        Assert.Empty(link.OnScan([Row("s1")]));                                     // offline: nothing

        var back = link.OnFrame(Presence(phone.Id, true));                          // back: fresh roster
        var roster = phone.OpenFromDesktop(back.Single());
        Assert.Equal("s1", roster.GetProperty("sessions")[0].GetProperty("session_id").GetString());
        Assert.Equal(5, changed);
        Assert.Null(link.LastSeen("ph_stranger"));
        Assert.False(link.IsOnline("ph_stranger"));
    }

    [Fact]
    public void Scans_become_deltas_or_rosters_numbered_per_phone()
    {
        var (link, _, phone, _, _) = Paired();
        var first = link.OnScan([Row("s1")]);                       // arrival → roster
        Assert.Equal(("roster", 1L), (phone.OpenFromDesktop(first.Single()).GetProperty("t").GetString(), phone.OpenFromDesktop(first.Single()).GetProperty("seq").GetInt64()));

        Assert.Empty(link.OnScan([Row("s1")]));                     // nothing changed

        var flip = link.OnScan([Row("s1", "generating")]);
        var d = phone.OpenFromDesktop(flip.Single());
        Assert.Equal(("delta", 2L, "generating"), (d.GetProperty("t").GetString(), d.GetProperty("seq").GetInt64(), d.GetProperty("state").GetString()));
        var env = J(flip.Single());
        Assert.Equal(("state", "none"), (env.GetProperty("class").GetString(), env.GetProperty("push").GetString()));
        Assert.Equal(Ck(CoalesceKey(phone.Base()), "s1"), env.GetProperty("ck").GetString());
        Assert.Equal(DesktopId, env.GetProperty("from").GetString());
        Assert.Equal(_now.ToUnixTimeSeconds(), env.GetProperty("ts").GetInt64());

        var gone = phone.OpenFromDesktop(link.OnScan([]).Single());
        Assert.Equal((3L, true), (gone.GetProperty("seq").GetInt64(), gone.GetProperty("removed").GetBoolean()));
    }

    [Fact]
    public void A_scan_before_welcome_updates_the_book_but_sends_nothing()
    {
        var (link, _, phone, _, _) = Paired();
        link.Disconnected();
        Assert.Empty(link.OnScan([Row("s1")]));
        link.OnFrame(Welcome(phone.Id));
        var roster = phone.OpenFromDesktop(link.OnFrame(Presence(phone.Id, true)).Single());
        Assert.Equal(1, roster.GetProperty("sessions").GetArrayLength());     // the book kept the scan
    }

    [Fact]
    public void Resync_sends_a_roster_only_to_a_known_online_phone()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnScan([Row("s1")]);
        link.OnScan([Row("s1", "waiting")]);
        var again = phone.OpenFromDesktop(link.OnFrame($$"""{"t":"resync","peer_id":"{{phone.Id}}"}""").Single());
        Assert.Equal(("roster", 1L), (again.GetProperty("t").GetString(), again.GetProperty("seq").GetInt64()));
        Assert.Empty(link.OnFrame("""{"t":"resync","peer_id":"ph_stranger"}"""));
        Assert.Empty(link.OnFrame("""{"t":"resync"}"""));
        link.OnFrame(Presence(phone.Id, false));
        Assert.Empty(link.OnFrame($$"""{"t":"resync","peer_id":"{{phone.Id}}"}"""));
    }

    // ---- revoke, both directions -----------------------------------------------------

    [Fact]
    public void A_phone_signed_revoke_removes_it_and_a_replay_or_forgery_does_not()
    {
        var (link, identity, phone, notes, _) = Paired();
        var removed = new List<string>();
        link.PhoneRemoved += removed.Add;

        Assert.Empty(link.OnFrame(phone.Revoke(signer: GenerateKeyPair())));      // forged signature
        Assert.Contains("pair.revoke with a bad signature ignored", notes);
        Assert.NotNull(identity.PhoneById(phone.Id));

        var good = phone.Revoke();
        link.OnFrame(phone.Sealed("action", "{\"t\":\"later\"}"));                // a later milestone's frame: opened, ignored
        Assert.Empty(link.OnFrame(good));                                          // counter 1 < 2 already seen: replay window
        Assert.Contains("envelope refused: wrong direction, replayed or reordered", notes);

        link.OnFrame(phone.Revoke());
        Assert.Equal([phone.Id], removed);
        Assert.Null(identity.PhoneById(phone.Id));
        Assert.Empty(link.OnFrame(phone.Revoke()));                                // now unpaired
    }

    [Fact]
    public void Sealed_frames_that_do_not_open_or_point_the_wrong_way_are_refused()
    {
        var (link, identity, phone, notes, _) = Paired();
        link.OnFrame(phone.Sealed("pair", "{}", direction: DesktopToPhone));
        link.OnFrame(phone.Sealed("pair", "{}", epoch: 1, counter: 50) .Replace("\"class\":\"pair\"", "\"class\":\"state\""));   // AAD class mismatch
        link.OnFrame(phone.Sealed("pair", "not json"));
        link.OnFrame(JsonSerializer.Serialize(new { v = 2, from = phone.Id, to = DesktopId, @class = "pair", ct = "AAAA" }));
        Assert.Contains("envelope refused: did not open", notes);
        Assert.Equal(2, notes.Count(n => n.StartsWith("envelope refused: wrong direction")));
        Assert.NotNull(identity.PhoneById(phone.Id));
        Assert.NotNull(identity.PhoneById(phone.Id)!.Received);   // the not-json one did open and was recorded
    }

    [Fact]
    public void Misaddressed_incomplete_or_unpaired_envelopes_are_dropped()
    {
        var (link, _, phone, notes, _) = Paired();
        link.OnFrame(phone.Sealed("state", "{}", to: "dsk_someone_else"));
        link.OnFrame("""{"v":2,"from":"ph_x","to":"dsk_00000000000000aa","class":"state"}""");
        link.OnFrame("""{"v":2,"from":"ph_x","to":"dsk_00000000000000aa","class":"state","ct":"AAAA"}""");
        Assert.Equal(2, notes.Count(n => n == "envelope: misaddressed or incomplete"));
        Assert.Contains("envelope from unpaired ph_x class=state", notes);
    }

    [Fact]
    public void The_relay_saying_unlinked_removes_the_phone_once()
    {
        var (link, identity, phone, _, _) = Paired();
        var removed = new List<string>();
        link.PhoneRemoved += removed.Add;
        link.OnFrame($$"""{"t":"err","code":"unlinked","peer_id":"{{phone.Id}}"}""");
        link.OnFrame($$"""{"t":"err","code":"unlinked","device_id":"{{phone.Id}}"}""");
        link.OnFrame("""{"t":"err","code":"unlinked"}""");
        Assert.Equal([phone.Id], removed);
        Assert.Empty(identity.Phones);
    }

    [Fact]
    public void Remove_while_connected_sends_a_signed_revoke_then_unlink()
    {
        var (link, identity, phone, _, _) = Paired();
        var frames = link.Remove(phone.Id);
        Assert.Equal(2, frames.Count);
        var revoke = phone.OpenFromDesktop(frames[0]);
        Assert.Equal(("pair.revoke", DesktopId), (revoke.GetProperty("t").GetString(), revoke.GetProperty("by").GetString()));
        Assert.True(Verify(phone.DesktopIdPub!, RevokeMessage(DesktopId, phone.Id), FromBase64Url(revoke.GetProperty("sig").GetString())));
        Assert.Equal($$"""{"t":"unlink","peer_id":"{{phone.Id}}"}""", frames[1]);
        Assert.Empty(identity.Phones);
        Assert.Empty(link.OnScan([Row("s9")]));
    }

    [Fact]
    public void Remove_while_disconnected_owes_the_unlink_until_the_next_welcome()
    {
        var (link, identity, phone, _, _) = Paired();
        link.Disconnected();
        Assert.Empty(link.Remove(phone.Id));
        Assert.Empty(link.Remove(phone.Id));                          // twice: owed once
        Assert.Equal([phone.Id], link.PendingUnlinks);
        Assert.Empty(identity.Phones);
        Assert.Equal([$$"""{"t":"unlink","peer_id":"{{phone.Id}}"}"""], link.OnFrame(Welcome()));
        Assert.Empty(link.PendingUnlinks);
    }

    // ---- nonces across reconnects and restarts -----------------------------------------

    private static RelayCrypto.NonceParts NonceOf(string frame) => PeekNonce(J(frame).GetProperty("ct").GetString())!.Value;

    [Fact]
    public void A_reconnect_keeps_the_epoch_and_a_restart_moves_it()
    {
        var (link, identity, phone, _, frames) = Paired();
        var before = NonceOf(frames[2]);
        Assert.Equal(0, before.Epoch);                                         // fresh pairing: epoch 0

        link.Disconnected();
        link.OnFrame(Welcome(phone.Id));
        var afterReconnect = NonceOf(link.OnFrame(Presence(phone.Id, true)).Single());
        Assert.Equal((0, before.Counter + 1), (afterReconnect.Epoch, afterReconnect.Counter));

        // A new process over the same files.
        var (restarted, _, _) = New(new RelayIdentity(_dir, _store, () => _now));
        restarted.OnFrame(Welcome(phone.Id));
        var afterRestart = NonceOf(restarted.OnFrame(Presence(phone.Id, true)).Single());
        Assert.Equal((1, 0UL), (afterRestart.Epoch, afterRestart.Counter));
        Assert.Equal(1, new RelayIdentity(_dir, _store).PhoneById(phone.Id)!.SendEpoch);
        Assert.NotNull(identity.PhoneById(phone.Id));
    }

    [Fact]
    public void A_welcome_forgets_channels_for_phones_removed_meanwhile()
    {
        var (link, identity, phone, _, _) = Paired();
        link.Disconnected();
        identity.RemovePhone(phone.Id);                                        // removed by another path
        link.OnFrame(Welcome());
        Assert.Empty(link.OnFrame(Presence(phone.Id, true)));
    }

    [Fact]
    public void An_exhausted_pairing_sends_nothing_and_says_so()
    {
        var identity = Identity();
        var phone = new FakePhone();
        identity.AddPhone(phone.Id, "P", phone.IdKey.Public, phone.KaKey.Public);
        identity.SaveSendEpoch(phone.Id, ushort.MaxValue, false);
        var (link, _, notes) = New(identity);
        link.OnFrame(Welcome(phone.Id));
        Assert.Empty(link.OnFrame(Presence(phone.Id, true)));
        Assert.Contains(notes, n => n.Contains("has used every epoch"));
    }

    // Writes to _dir fail while blocked: the directory is moved aside and a
    // file put in its place. Anything already loaded stays in memory.
    private string Block()
    {
        var moved = _dir + ".bak";
        Directory.Move(_dir, moved);
        File.WriteAllText(_dir, "in the way");
        return moved;
    }

    private void Unblock(string moved)
    {
        File.Delete(_dir);
        Directory.Move(moved, _dir);
    }

    [Fact]
    public void An_epoch_that_cannot_be_persisted_holds_frames_until_it_can()
    {
        var (link, _, phone, notes, _) = Paired();
        link.Disconnected();

        // A restart that loads its files and then cannot write them.
        var restartedIdentity = new RelayIdentity(_dir, _store, () => _now);
        Assert.Single(restartedIdentity.Phones);
        var moved = Block();
        var (restarted, _, restartedNotes) = New(restartedIdentity);
        restarted.OnFrame(Welcome(phone.Id));
        Assert.Empty(restarted.OnFrame(Presence(phone.Id, true)));
        Assert.Contains("send epoch not persisted; holding frames", restartedNotes);

        // The directory comes back; the next send writes the epoch first.
        Unblock(moved);
        var frame = restarted.OnFrame($$"""{"t":"resync","peer_id":"{{phone.Id}}"}""").Single();
        Assert.Equal("roster", phone.OpenFromDesktop(frame).GetProperty("t").GetString());
        Assert.Equal(1, NonceOf(frame).Epoch);
        Assert.Equal(1, new RelayIdentity(_dir, _store).PhoneById(phone.Id)!.SendEpoch);
        Assert.DoesNotContain(notes, n => n.Contains("holding"));
    }

    [Fact]
    public void A_rotation_whose_epoch_cannot_be_persisted_holds_the_frame()
    {
        var (link, _, phone, notes, _) = Paired();
        link.OnScan([Row("s1")]);
        _now = _now.AddHours(24);                                              // due to rotate
        var moved = Block();
        Assert.Empty(link.OnScan([Row("s1", "waiting")]));
        Assert.Contains("send epoch not persisted; holding frames", notes);

        Unblock(moved);
        var next = link.OnScan([Row("s1", "idle")]).Single();
        Assert.Equal(1, NonceOf(next).Epoch);                                  // the rotated epoch, now persisted
        Assert.Equal(1, new RelayIdentity(_dir, _store).PhoneById(phone.Id)!.SendEpoch);
    }

    [Fact]
    public void Remove_on_an_exhausted_pairing_still_unlinks()
    {
        var identity = Identity();
        var phone = new FakePhone();
        identity.AddPhone(phone.Id, "P", phone.IdKey.Public, phone.KaKey.Public);
        identity.SaveSendEpoch(phone.Id, ushort.MaxValue, false);
        var (link, _, _) = New(identity);
        link.OnFrame(Welcome(phone.Id));
        Assert.Equal(["unlink"], link.Remove(phone.Id).Select(T));
    }

    [Fact]
    public void Frames_across_many_epochs_still_open()
    {
        // A phone restarted a dozen times: each epoch's key is derived on
        // demand, and the cache of them stays small.
        var (link, identity, phone, notes, _) = Paired();
        for (ushort e = 1; e <= 12; e++)
            link.OnFrame(phone.Sealed("action", "{}", epoch: e, counter: 0));
        Assert.DoesNotContain(notes, n => n.StartsWith("envelope refused"));
        Assert.Equal(((ushort)12, 0UL), identity.PhoneById(phone.Id)!.Received);
    }

    [Fact]
    public void The_receive_window_survives_a_restart()
    {
        var (link, _, phone, _, _) = Paired();
        var old = phone.Sealed("action", "{}", epoch: 3, counter: 9);
        link.OnFrame(old);
        link.Disconnected();

        var (restarted, _, notes) = New(new RelayIdentity(_dir, _store, () => _now));
        restarted.OnFrame(Welcome(phone.Id));
        restarted.OnFrame(old);                                                // a replay across the restart
        Assert.Contains("envelope refused: wrong direction, replayed or reordered", notes);
        restarted.OnFrame(phone.Sealed("action", "{}", epoch: 3, counter: 10));
        Assert.Single(notes);
    }

    [Fact]
    public void Ulids_are_26_crockford_characters_and_sort_by_time()
    {
        var a = RelayLink.Ulid(_now);
        var b = RelayLink.Ulid(_now.AddMilliseconds(1));
        Assert.Equal(26, a.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", a);
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.Equal(a[..10], RelayLink.Ulid(_now)[..10]);
    }
}
