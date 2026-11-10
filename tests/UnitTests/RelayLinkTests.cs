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
            var plain = Open(EpochKey(Base(), n.Epoch), ct, Aad(e.GetProperty("from").GetString()!, Id, e.GetProperty("class").GetString()!), DesktopToPhone);
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
    [InlineData("""{"t":"err","ref":"01M6X5","code":"bad_envelope"}""")]
    [InlineData("""{"t":"pong"}""")]
    [InlineData("""{"t":5}""")]
    [InlineData("""{"t":"ok","ref":"link","code":"linked"}""")]
    [InlineData("""{"t":"ok","ref":"resync"}""")]
    public void Junk_and_unknown_frames_produce_nothing(string frame) => Assert.Empty(New().Link.OnFrame(frame));

    [Theory]
    [InlineData(null, "")]
    [InlineData("ph_a1b2c3d4e5f60718", "ph_a1b2c3d4e5f60718")]
    [InlineData("01M6X5:unlink.v2-x", "01M6X5:unlink.v2-x")]
    [InlineData("ph_1\nFAKE line", "ph_1FAKEline…")]
    [InlineData("a/b+c=d", "abcd…")]
    public void Safe_keeps_only_id_characters_and_says_when_it_cut(string? value, string expected) =>
        Assert.Equal(expected, RelayLink.Safe(value));

    [Fact]
    public void Safe_caps_length_and_known_falls_back_to_unknown()
    {
        Assert.Equal(RelayLink.MaxLoggedLength + 1, RelayLink.Safe(new string('a', 500)).Length);
        var exact = new string('a', RelayLink.MaxLoggedLength);
        Assert.Same(exact, RelayLink.Safe(exact));                                   // at the cap, kept whole
        Assert.Equal(exact + "…", RelayLink.Safe(exact + "a"));                      // one past it, cut
        Assert.Equal("state", RelayLink.Known("state", RelayLink.Classes));
        Assert.Equal("other", RelayLink.Known("SECRET", RelayLink.Classes));
        Assert.Equal("other", RelayLink.Known(null, RelayLink.Classes));
    }

    [Fact]
    public void Notes_built_from_relay_values_cannot_carry_a_newline_or_a_flood()
    {
        var (link, _, notes) = New();
        link.OnFrame(Welcome());
        link.OnFrame(JsonSerializer.Serialize(new { t = "x\nFAKE relay line" }));
        link.OnFrame(JsonSerializer.Serialize(new { t = "err", @ref = "r\nFAKE", code = new string('c', 5_000) }));
        link.OnFrame(JsonSerializer.Serialize(new { t = "resync", peer_id = "ph_x\nFAKE" }));
        link.OnFrame(JsonSerializer.Serialize(new { t = "err", @ref = new string('r', 300), code = "rate_limited" }));
        link.OnFrame(JsonSerializer.Serialize(new { v = 2, from = "ph_x\nFAKE", to = DesktopId, @class = "SECRET", ct = "AAAA" }));
        Assert.Equal(5, notes.Count);
        Assert.All(notes, n => Assert.DoesNotContain("\n", n));
        Assert.All(notes, n => Assert.True(n.Length < 200, n.Length.ToString()));
        Assert.Contains("relay frame t=xFAKErelayline… ignored", notes);
        Assert.Contains("resync for ph_xFAKE… ignored: not an online paired phone (not paired)", notes);
        Assert.Contains("envelope from unpaired ph_xFAKE… class=other", notes);
    }

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
        var acceptBody = J(Encoding.UTF8.GetString(Open(phone.PairKey!, accept.GetProperty("ct").GetString()!, Aad(DesktopId, phone.Id, "pair"), DesktopToPhone)!));
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

    // Addendum 6's regression case, with the exact frame the relay forwards
    // (frames-v2.json "resync (forwarded to a desktop)"): a paired phone, the
    // snapshot unchanged since the last frame, and an inbound resync. The
    // next frame to the phone is the whole roster at seq 1 with ck "", and
    // the delta after it is seq 2.
    [Fact]
    public void A_forwarded_resync_with_nothing_changed_still_gets_the_whole_roster_then_seq_2()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnScan([Row("s1")]);
        link.OnScan([Row("s1", "waiting")]);                                  // phone now at seq 2
        Assert.Empty(link.OnScan([Row("s1", "waiting")]));                    // nothing changed: nothing sent

        var frame = link.OnFrame($$"""{"t":"resync","peer_id":"{{phone.Id}}"}""").Single();
        Assert.Equal("", J(frame).GetProperty("ck").GetString());
        var roster = phone.OpenFromDesktop(frame);
        Assert.Equal(("roster", 1L, "waiting"), (roster.GetProperty("t").GetString(), roster.GetProperty("seq").GetInt64(),
            roster.GetProperty("sessions")[0].GetProperty("state").GetString()));
        var next = phone.OpenFromDesktop(link.OnScan([Row("s1", "idle")]).Single());
        Assert.Equal(("delta", 2L), (next.GetProperty("t").GetString(), next.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public void Resync_sends_a_roster_only_to_a_known_online_phone()
    {
        var (link, _, phone, notes, _) = Paired();
        link.OnScan([Row("s1")]);
        link.OnScan([Row("s1", "waiting")]);
        var again = phone.OpenFromDesktop(link.OnFrame($$"""{"t":"resync","peer_id":"{{phone.Id}}"}""").Single());
        Assert.Equal(("roster", 1L), (again.GetProperty("t").GetString(), again.GetProperty("seq").GetInt64()));
        Assert.Empty(link.OnFrame("""{"t":"resync","peer_id":"ph_stranger"}"""));
        Assert.Empty(link.OnFrame("""{"t":"resync"}"""));
        Assert.Contains($"resync from {phone.Id}: fresh roster", notes);
        Assert.Contains("resync for ph_stranger ignored: not an online paired phone (not paired)", notes);
        link.OnFrame(Presence(phone.Id, false));
        link.OnFrame($$"""{"t":"resync","peer_id":"{{phone.Id}}"}""");
        Assert.Contains($"resync for {phone.Id} ignored: not an online paired phone (paired, offline)", notes);
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
        Assert.Contains($"pair.revoke from {phone.Id} verified; phone removed (its own unlink settles the link at the relay)", notes);
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
        link.OnFrame("""{"v":2,"from":"ph_x|dsk_00000000000000aa","to":"dsk_00000000000000aa","class":"pair","ct":"AAAA","offer":"off_1"}""");
        link.OnFrame("""{"v":2,"from":"ph_x","to":"dsk_00000000000000aa","class":"pa|ir","ct":"AAAA"}""");
        Assert.Equal(4, notes.Count(n => n == "envelope: misaddressed or incomplete"));   // a '|' never reaches Aad
        Assert.Contains("envelope from unpaired ph_x class=state", notes);
    }

    [Fact]
    public void The_relay_saying_unlinked_removes_the_phone_once()
    {
        var (link, identity, phone, _, _) = Paired();
        var removed = new List<string>();
        link.PhoneRemoved += removed.Add;
        link.OnFrame($$"""{"t":"err","code":"unlinked","peer_id":"{{phone.Id}}"}""");
        link.OnFrame($$"""{"t":"err","code":"unlinked","message":"{{phone.Id}}"}""");   // the older relay image's shape
        link.OnFrame("""{"t":"err","code":"unlinked"}""");
        Assert.Equal([phone.Id], removed);
        Assert.Empty(identity.Phones);
    }

    [Fact]
    public void Remove_while_connected_sends_a_signed_revoke_then_unlink()
    {
        var (link, identity, phone, notes, _) = Paired();
        var frames = link.Remove(phone.Id);
        Assert.Contains($"removed {phone.Id}: signed revoke and unlink sent", notes);
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
        var (link, identity, phone, notes, _) = Paired();
        link.Disconnected();
        Assert.Empty(link.Remove(phone.Id));
        Assert.Contains($"removed {phone.Id}: unlink owed until the next welcome", notes);
        Assert.Empty(link.Remove(phone.Id));                          // twice: owed once
        Assert.Equal([phone.Id], link.PendingUnlinks);
        Assert.Empty(identity.Phones);
        Assert.Equal([$$"""{"t":"unlink","peer_id":"{{phone.Id}}"}"""], link.OnFrame(Welcome()));
        Assert.Equal([phone.Id], link.PendingUnlinks);                // sent, but owed until the relay says ok
        link.OnFrame("""{"t":"ok","ref":"unlink","code":"unlinked"}""");
        Assert.Empty(link.PendingUnlinks);
        Assert.Empty(new RelayIdentity(_dir, _store).PendingUnlinks);  // and that is on disk
    }

    // Acceptance 4 across a quit: Remove while the socket is down, the
    // process ends, and the next run's first welcome sends the unlink.
    [Fact]
    public void An_owed_unlink_survives_a_restart_and_is_sent_on_the_first_welcome()
    {
        var (link, _, phone, _, _) = Paired();
        link.Disconnected();
        link.Remove(phone.Id);
        // ...the process quits here.

        var (restarted, identity, _) = New(new RelayIdentity(_dir, _store, () => _now));
        Assert.Equal([phone.Id], restarted.PendingUnlinks);
        Assert.Equal(["unlink"], restarted.OnFrame(Welcome()).Select(T));
        restarted.OnFrame("""{"t":"ok","ref":"unlink","code":"unlinked"}""");
        Assert.Empty(identity.PendingUnlinks);
    }

    [Fact]
    public void A_connected_remove_stays_owed_until_answered_and_not_linked_also_settles_it()
    {
        var (link, identity, phone, _, _) = Paired();
        link.Remove(phone.Id);
        Assert.Equal([phone.Id], identity.PendingUnlinks);
        link.OnFrame("""{"t":"err","ref":"unlink","code":"not_linked"}""");   // already gone: what was wanted
        Assert.Empty(identity.PendingUnlinks);
    }

    [Fact]
    public void Another_refusal_keeps_it_owed_for_the_next_welcome()
    {
        var (link, identity, phone, notes, _) = Paired();
        link.Remove(phone.Id);
        link.OnFrame("""{"t":"err","ref":"unlink","code":"bad_frame"}""");
        Assert.Equal([phone.Id], identity.PendingUnlinks);
        link.OnFrame("""{"t":"err","ref":"unlink","code":"bad_frame"}""");   // nothing in flight: harmless
        link.OnFrame("""{"t":"ok","ref":"unlink","code":"unlinked"}""");     // likewise
        Assert.Equal([phone.Id], identity.PendingUnlinks);
        link.Disconnected();
        Assert.Equal(["unlink"], link.OnFrame(Welcome()).Select(T));
    }

    // Hana's PR #9: a frame refused for rate comes back as err rate_limited
    // with a ref naming it, and was not processed — so it is resent, after a
    // backoff RelayClient schedules from RetryDue, at most MaxRetries times.
    private static List<(string Key, int Attempt)> Retries(RelayLink link)
    {
        var due = new List<(string, int)>();
        link.RetryDue += (k, a) => due.Add((k, a));
        return due;
    }

    [Fact]
    public void A_refused_unlink_is_retried_with_a_bounded_number_of_attempts()
    {
        var (link, identity, phone, notes, _) = Paired();
        var due = Retries(link);
        link.Remove(phone.Id);
        for (var attempt = 1; attempt <= RelayLink.MaxRetries; attempt++)
        {
            link.OnFrame("""{"t":"err","ref":"unlink","code":"rate_limited"}""");
            Assert.Equal(("unlink:" + phone.Id, attempt), due[^1]);
            Assert.Equal([$$"""{"t":"unlink","peer_id":"{{phone.Id}}"}"""], link.Retry("unlink:" + phone.Id));
        }
        link.OnFrame("""{"t":"err","ref":"unlink","code":"rate_limited"}""");          // the sixth refusal
        Assert.Equal(RelayLink.MaxRetries, due.Count);
        Assert.Contains(notes, n => n.StartsWith("giving up on unlink:"));
        Assert.Equal([phone.Id], identity.PendingUnlinks);                               // still owed for the next welcome
        link.OnFrame("""{"t":"err","ref":"unlink","code":"rate_limited"}""");          // nothing in flight: nothing scheduled
        Assert.Equal(RelayLink.MaxRetries, due.Count);
    }

    [Fact]
    public void An_unlink_that_lands_resets_its_attempts_and_is_not_retried()
    {
        var (link, identity, phone, _, _) = Paired();
        var due = Retries(link);
        link.Remove(phone.Id);
        link.OnFrame("""{"t":"err","ref":"unlink","code":"rate_limited"}""");
        link.Retry("unlink:" + phone.Id);
        link.OnFrame("""{"t":"ok","ref":"unlink","code":"unlinked"}""");
        Assert.Empty(identity.PendingUnlinks);
        Assert.Empty(link.Retry("unlink:" + phone.Id));                                  // nothing owed any more
        Assert.Single(due);
    }

    [Fact]
    public void A_refused_link_or_accept_is_retried_until_the_link_lands()
    {
        var (link, _, _) = New();
        var phone = new FakePhone();
        link.OnFrame(Welcome());
        var due = Retries(link);
        link.OnFrame(phone.Request(link.BeginPairing("off_1", _now.AddMinutes(5)).QrText));
        var frames = link.Allow(link.Pending!, "Mac");
        var acceptId = J(frames[0]).GetProperty("id").GetString()!;

        link.OnFrame($$"""{"t":"err","ref":"{{acceptId}}","code":"rate_limited"}""");
        link.OnFrame("""{"t":"err","ref":"link","code":"rate_limited"}""");
        Assert.Equal([("env:" + acceptId, 1), ("link", 1)], due);
        Assert.Equal([frames[0]], link.Retry("env:" + acceptId));
        Assert.Equal([frames[1]], link.Retry("link"));

        link.OnFrame("""{"t":"ok","ref":"link","code":"linked"}""");
        Assert.Empty(link.Retry("link"));
        Assert.Empty(link.Retry("env:" + acceptId));
        link.OnFrame("""{"t":"err","ref":"link","code":"rate_limited"}""");              // no link pending: nothing
        Assert.Equal(2, due.Count);
    }

    [Fact]
    public void A_refused_revoke_is_retried_until_its_unlink_settles()
    {
        var (link, _, phone, _, _) = Paired();
        var due = Retries(link);
        var frames = link.Remove(phone.Id);
        var revokeId = J(frames[0]).GetProperty("id").GetString()!;
        link.OnFrame($$"""{"t":"err","ref":"{{revokeId}}","code":"rate_limited"}""");
        Assert.Equal(("env:" + revokeId, 1), due.Single());
        Assert.Equal([frames[0]], link.Retry("env:" + revokeId));
        link.OnFrame("""{"t":"ok","ref":"unlink","code":"unlinked"}""");
        Assert.Empty(link.Retry("env:" + revokeId));                                     // the unlink landed: no more
    }

    // Measured on the live relay: 95 unnamed rate_limited errors in about two
    // milliseconds gave a revoke up after "5 attempts" with none of them sent.
    // While a retry is pending, more refusals schedule nothing more.
    [Fact]
    public void A_burst_of_refusals_schedules_each_retry_once_until_it_fires()
    {
        var (link, _, phone, notes, _) = Paired();
        link.OnFrame("""{"t":"ok","ref":"link","code":"linked"}""");                    // the pairing's frames are settled
        var due = Retries(link);
        var revokeId = J(link.Remove(phone.Id)[0]).GetProperty("id").GetString()!;
        for (var i = 0; i < 95; i++) link.OnFrame("""{"t":"err","code":"rate_limited"}""");
        Assert.Equal(2, due.Count);                                                       // the revoke and the unlink, once each
        Assert.All(due, d => Assert.Equal(1, d.Attempt));
        Assert.DoesNotContain(notes, n => n.StartsWith("giving up"));

        link.Retry("env:" + revokeId);                                                    // the backoff fired: a real resend
        link.OnFrame($$"""{"t":"err","ref":"{{revokeId}}","code":"rate_limited"}""");
        Assert.Equal(("env:" + revokeId, 2), due[^1]);                                     // now attempt 2
    }

    [Fact]
    public void A_refused_state_frame_is_left_for_the_phone_to_resync()
    {
        var (link, _, _, notes, _) = Paired();
        var due = Retries(link);
        link.OnFrame("""{"t":"err","ref":"01M6X5STATEFRAME00000000","code":"rate_limited"}""");
        Assert.Empty(due);
        Assert.Contains(notes, n => n.Contains("rate_limited ref=01M6X5STATEFRAME"));
    }

    [Fact]
    public void A_refusal_with_no_ref_retries_everything_unanswered()
    {
        var (link, _, _) = New();
        var phone = new FakePhone();
        link.OnFrame(Welcome());
        link.OnFrame(phone.Request(link.BeginPairing("off_1", _now.AddMinutes(5)).QrText));
        var frames = link.Allow(link.Pending!, "Mac");
        var acceptId = J(frames[0]).GetProperty("id").GetString()!;
        var revokeId = J(link.Remove(phone.Id)[0]).GetProperty("id").GetString()!;
        var due = Retries(link);
        link.OnFrame("""{"t":"err","code":"rate_limited"}""");                           // the raw ceiling: no ref
        Assert.Equal(new[] { "env:" + acceptId, "env:" + revokeId, "link", "unlink:" + phone.Id }.Order(StringComparer.Ordinal),
            due.Select(d => d.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Retries_are_dropped_with_the_connection()
    {
        var (link, _, _) = New();
        var phone = new FakePhone();
        link.OnFrame(Welcome());
        link.OnFrame(phone.Request(link.BeginPairing("off_1", _now.AddMinutes(5)).QrText));
        link.Allow(link.Pending!, "Mac");
        link.Disconnected();
        Assert.Empty(link.Retry("link"));                                                // not welcomed
        link.OnFrame(Welcome());
        Assert.Empty(link.Retry("link"));                                                // forgotten with the connection
        Assert.Empty(link.Retry("unlink:ph_never_removed"));
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
        // A t this desktop does not know, so the frame opens and is otherwise
        // left alone (since CB-316 a t-less action is answered bad_frame,
        // which would add a note of its own).
        var old = phone.Sealed("action", """{"t":"noop"}""", epoch: 3, counter: 9);
        link.OnFrame(old);
        link.Disconnected();

        var (restarted, _, notes) = New(new RelayIdentity(_dir, _store, () => _now));
        restarted.OnFrame(Welcome(phone.Id));
        restarted.OnFrame(old);                                                // a replay across the restart
        Assert.Contains("envelope refused: wrong direction, replayed or reordered", notes);
        restarted.OnFrame(phone.Sealed("action", """{"t":"noop"}""", epoch: 3, counter: 10));
        Assert.Single(notes);
    }

    // nadia's case: the process rotates to a new epoch, the write fails, the
    // frame is held — and then the process dies before the write ever
    // succeeds. The restart reads the old epoch from disk and moves one past
    // it, which is the very epoch the dead process held in memory but never
    // sent under. Every (epoch, counter) that actually left either process
    // must be distinct.
    [Fact]
    public void A_crash_after_rotation_but_before_the_persist_reuses_no_nonce()
    {
        var (link, _, phone, _, allowFrames) = Paired();
        var sent = allowFrames.Skip(2).Select(NonceOf).ToList();          // the roster after Allow
        sent.AddRange(link.OnScan([Row("s1")]).Select(NonceOf));
        sent.AddRange(link.OnScan([Row("s1", "waiting")]).Select(NonceOf));

        _now = _now.AddHours(24);                                         // rotation due
        var moved = Block();
        Assert.Empty(link.OnScan([Row("s1", "idle")]));                   // rotated in memory, not persisted, held
        Unblock(moved);
        // ...and the process dies here: `link` is never used again.

        var (restarted, _, _) = New(new RelayIdentity(_dir, _store, () => _now));
        restarted.OnFrame(Welcome(phone.Id));
        sent.AddRange(restarted.OnFrame(Presence(phone.Id, true)).Select(NonceOf));
        sent.AddRange(restarted.OnScan([Row("s1", "generating")]).Select(NonceOf));

        Assert.Equal(sent.Count, sent.Distinct().Count());
        Assert.Equal(1, sent[^1].Epoch);                                  // the restart's epoch
        Assert.All(sent.Take(3), n => Assert.Equal(0, n.Epoch));
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

    // ---- CB-315 prompts ----------------------------------------------------------

    private const string Rid = "01JC9R0A5N2Q7W8X3E4T6Y9ZBD";

    [Fact]
    public void A_prompt_goes_to_every_paired_phone_as_an_amber_with_its_own_e()
    {
        var (link, _, phone, _, _) = Paired();
        link.Disconnected();
        link.OnFrame(Welcome());   // welcomed, phone offline: action frames are queued by the relay, so it still gets one
        var frames = link.SendPrompt(Rid, "sess-1", "{\"t\":\"prompt\"}", "{\"v\":1}");
        var env = J(Assert.Single(frames));
        Assert.Equal((Rid, "action", "amber"), (env.GetProperty("id").GetString(), env.GetProperty("class").GetString(), env.GetProperty("push").GetString()));
        Assert.Equal("prompt", phone.OpenFromDesktop(frames[0]).GetProperty("t").GetString());
        Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, env.GetProperty("e").GetString())!));
        Assert.Equal(Ck(CoalesceKey(phone.Base()), "sess-1"), env.GetProperty("ck").GetString());
    }

    [Fact]
    public void A_prompt_whose_push_did_not_fit_carries_no_e()
    {
        var (link, _, _, _, _) = Paired();
        var env = J(Assert.Single(link.SendPrompt(Rid, "sess-1", "{}", null)));
        Assert.False(env.TryGetProperty("e", out _));
        Assert.Equal("amber", env.GetProperty("push").GetString());
    }

    [Fact]
    public void Resolved_is_push_resolve_with_the_request_id_and_no_e()
    {
        var (link, _, phone, _, _) = Paired();
        var frames = link.SendResolved(Rid, "sess-1", "desk");
        var env = J(Assert.Single(frames));
        Assert.Equal((Rid, "resolve"), (env.GetProperty("id").GetString(), env.GetProperty("push").GetString()));
        Assert.False(env.TryGetProperty("e", out _));
        var body = phone.OpenFromDesktop(frames[0]);
        Assert.Equal(("resolved", "desk"), (body.GetProperty("t").GetString(), body.GetProperty("by").GetString()));
    }

    [Fact]
    public void A_refusal_goes_only_to_the_phone_that_answered()
    {
        var (link, _, phone, _, _) = Paired();
        var frames = link.SendRefusal(phone.Id, Rid, "sess-1", AnswerGate.Replayed);
        var env = J(Assert.Single(frames));
        Assert.Equal("none", env.GetProperty("push").GetString());
        Assert.Equal("replayed", phone.OpenFromDesktop(frames[0]).GetProperty("code").GetString());
        Assert.Empty(link.SendRefusal("ph_unknown", Rid, "sess-1", AnswerGate.Replayed));
    }

    [Fact]
    public void Nothing_is_sent_while_not_welcomed()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnFrame("""{"t":"err","ref":"hello","code":"version"}""");
        Assert.Empty(link.SendPrompt(Rid, "s", "{}", null));
        Assert.Empty(link.SendResolved(Rid, "s", "desk"));
        Assert.Empty(link.SendRefusal(phone.Id, Rid, "s", AnswerGate.Expired));
    }

    private static string AnswerJson(string? drop = null, string? t = "answer")
    {
        var fields = new Dictionary<string, object?>
        {
            ["t"] = t, ["request_id"] = Rid, ["session_id"] = "sess-1", ["action"] = "approve", ["scope"] = "", ["note"] = "",
            ["nonce"] = "AAAAAAAAAAAAAAAAAAAAAA", ["expires_at"] = 1794113592L, ["screen_hash"] = "h", ["sig"] = "s",
        };
        if (drop is not null) fields.Remove(drop);
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public void An_action_answer_reaches_the_gate_with_the_phones_identity_key()
    {
        var (link, _, phone, _, _) = Paired();
        (string Phone, byte[] Key, PhoneAnswer Answer)? got = null;
        link.AnswerReceived += (p, k, a) => got = (p, k, a);
        link.OnFrame(phone.Sealed("action", AnswerJson()));
        Assert.NotNull(got);
        Assert.Equal(phone.Id, got.Value.Phone);
        Assert.Equal(phone.IdKey.Public, got.Value.Key);
        Assert.Equal(new PhoneAnswer(Rid, "sess-1", "approve", "", "", "AAAAAAAAAAAAAAAAAAAAAA", 1794113592, "h", "s"), got.Value.Answer);
    }

    [Theory]
    [InlineData("state", "answer", null)]      // an answer on any class but action
    [InlineData("control", "answer", null)]
    [InlineData("action", "push-ack", null)]   // a push-ack's shape, even sealed, is not an answer
    [InlineData("action", "answer", "sig")]    // malformed: a field missing
    [InlineData("action", "answer", "expires_at")]
    public void Nothing_but_a_complete_action_answer_reaches_the_gate(string cls, string t, string? drop)
    {
        var (link, _, phone, notes, _) = Paired();
        var raised = false;
        link.AnswerReceived += (_, _, _) => raised = true;
        link.OnFrame(phone.Sealed(cls, AnswerJson(drop, t)));
        Assert.False(raised);
        if (drop is not null) Assert.Contains(notes, n => n.Contains("malformed"));
    }

    [Fact]
    public void An_unsealed_push_ack_cannot_reach_the_gate()
    {
        var (link, _, phone, notes, _) = Paired();
        var raised = false;
        link.AnswerReceived += (_, _, _) => raised = true;
        var ack = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"t\":\"push-ack\",\"request_id\":\"" + Rid + "\",\"at_ms\":1}"));
        link.OnFrame(JsonSerializer.Serialize(new { v = 2, id = "z", from = phone.Id, to = DesktopId, ts = 0, @class = "control", push = "none", ck = "", ct = ack }));
        link.OnFrame(JsonSerializer.Serialize(new { v = 2, id = "z", from = phone.Id, to = DesktopId, ts = 0, @class = "action", push = "none", ck = "", ct = ack }));
        Assert.False(raised);
    }

    [Fact]
    public void ParseAnswer_refuses_a_wrongly_typed_field()
    {
        Assert.Null(RelayLink.ParseAnswer(J(AnswerJson().Replace("\"expires_at\":1794113592", "\"expires_at\":\"1794113592\""))));
        Assert.Null(RelayLink.ParseAnswer(J(AnswerJson().Replace("\"scope\":\"\"", "\"scope\":3"))));
        Assert.Null(RelayLink.ParseAnswer(J(AnswerJson().Replace("\"expires_at\":1794113592", "\"expires_at\":1.5"))));
    }

    [Fact]
    public void The_relays_push_replies_are_raised_with_apns_ms_when_given()
    {
        var (link, _, _) = New();
        var got = new List<(string, string, int?)>();
        link.PushReplied += (r, c, ms) => got.Add((r, c, ms));
        link.OnFrame($$"""{"t":"ok","ref":"{{Rid}}","code":"pushed","apns_ms":142}""");
        link.OnFrame($$"""{"t":"ok","ref":"{{Rid}}","code":"push_throttled"}""");
        link.OnFrame($$"""{"t":"ok","ref":"{{Rid}}","code":"push_no_token","apns_ms":"x"}""");
        link.OnFrame($$"""{"t":"ok","ref":"{{Rid}}","code":"pushed","apns_ms":1.5}""");
        link.OnFrame("""{"t":"ok","code":"pushed"}""");
        Assert.Equal([(Rid, "pushed", 142), (Rid, "push_throttled", null), (Rid, "push_no_token", null), (Rid, "pushed", null)], got);
    }

    [Fact]
    public void A_sealed_resolve_rides_as_e_with_the_ambers_ck()
    {
        var (link, _, phone, _, _) = Paired();
        var amber = J(Assert.Single(link.SendPrompt(Rid, "sess-1", "{}", null)));
        var resolve = J(Assert.Single(link.SendResolved(Rid, "sess-1", "desk", "{\"v\":1,\"t\":\"resolved\"}")));
        Assert.Equal(amber.GetProperty("ck").GetString(), resolve.GetProperty("ck").GetString());
        Assert.Equal("{\"v\":1,\"t\":\"resolved\"}", Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, resolve.GetProperty("e").GetString())!));
    }

    // ---- gate (a): the notification extension's push-ack ------------------------

    private static string AckEnvelope(string from, string ct) =>
        JsonSerializer.Serialize(new { v = 2, id = "k", from, to = DesktopId, ts = 0, @class = "control", push = "none", ck = "", ct });

    private static string AckCt(string body) => Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

    [Fact]
    public void A_push_ack_raises_its_request_id_and_time_and_nothing_else()
    {
        var (link, _, phone, _, _) = Paired();
        var acks = new List<(string, string, long)>();
        var answered = false;
        link.PushAcked += (p, r, t) => acks.Add((p, r, t));
        link.AnswerReceived += (_, _, _) => answered = true;
        Assert.Empty(link.OnFrame(AckEnvelope(phone.Id, AckCt($$"""{"t":"push-ack","request_id":"{{Rid}}","at_ms":1794111792500}"""))));
        Assert.Equal([(phone.Id, Rid, 1794111792500L)], acks);
        Assert.False(answered);
    }

    [Theory]
    [InlineData("""{"t":"answer","request_id":"01JC9R0A5N2Q7W8X3E4T6Y9ZBD","at_ms":1}""")]
    [InlineData("""{"t":"push-ack","request_id":"not-a-ulid","at_ms":1}""")]
    [InlineData("""{"t":"push-ack","request_id":"01JC9R0A5N2Q7W8X3E4T6Y9ZBD","at_ms":"1"}""")]
    [InlineData("""{"t":"push-ack","request_id":"01JC9R0A5N2Q7W8X3E4T6Y9ZBD"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"t":"push-ack","request_id":"01JC9R0A5N2Q7W8X3E4T6Y9ZBD","at_ms":1,"extra":true}""")]   // exactly three keys
    public void Anything_but_an_exact_push_ack_on_control_is_ignored(string body)
    {
        var (link, _, phone, notes, _) = Paired();
        var acks = 0;
        link.PushAcked += (_, _, _) => acks++;
        link.OnFrame(AckEnvelope(phone.Id, AckCt(body)));
        Assert.Equal(0, acks);
        Assert.Contains(notes, n => n.Contains("not a push-ack"));
    }

    [Fact]
    public void A_control_envelope_that_is_not_base64_is_ignored()
    {
        Assert.Null(RelayLink.ParsePushAck("@@@ not base64"));
        Assert.Null(RelayLink.ParsePushAck(Convert.ToBase64String([0xff, 0xfe])));
    }

    [Fact]
    public void A_push_ack_from_an_unpaired_device_is_not_raised()
    {
        var (link, _, _, _, _) = Paired();
        var acks = 0;
        link.PushAcked += (_, _, _) => acks++;
        link.OnFrame(AckEnvelope("ph_ffffffffffffffff", AckCt($$"""{"t":"push-ack","request_id":"{{Rid}}","at_ms":1}""")));
        Assert.Equal(0, acks);
    }

    // ---- CB-316: v4 requests, replies, windows, usage and done ----------------------

    private static string? Push(string frame) => J(frame).GetProperty("push").GetString();
    private static string CkOf(string frame) => J(frame).GetProperty("ck").GetString()!;
    private static bool HasE(string frame) => J(frame).TryGetProperty("e", out _);

    [Fact]
    public void A_v4_request_is_raised_with_the_envelope_id_as_its_ref()
    {
        var (link, _, phone, notes, _) = Paired();
        var got = new List<(string Phone, byte[] IdPub, RelayRequest Request)>();
        link.RequestReceived += (p, k, r) => got.Add((p, k, r));

        Assert.Empty(link.OnFrame(phone.Sealed("action", """{"t":"watch","session_id":"s1"}""")));
        var (from, idPub, request) = got.Single();
        Assert.Equal(phone.Id, from);
        Assert.Equal(phone.IdKey.Public, idPub);
        Assert.Equal(new WatchRequest("y", "s1", null), request);
        Assert.Contains(notes, n => n == $"watch from {phone.Id} ref=y");
    }

    [Fact]
    public void An_action_with_no_t_or_no_json_is_bad_frame_and_an_unknown_t_is_ignored()
    {
        var (link, _, phone, _, _) = Paired();
        var got = new List<RelayRequest>();
        link.RequestReceived += (_, _, r) => got.Add(r);

        link.OnFrame(phone.Sealed("action", """{"session_id":"s1"}"""));
        link.OnFrame(phone.Sealed("action", "not json"));
        link.OnFrame(phone.Sealed("action", """{"t":"teleport"}"""));       // a newer phone's type: left alone
        link.OnFrame(phone.Sealed("state", """{"t":"watch","session_id":"s1"}"""));   // wrong class: never dispatched
        Assert.Equal([new BadFrameRequest("y", null), new BadFrameRequest("y", null)], got);
    }

    [Fact]
    public void Going_offline_is_raised_once_and_coming_back_sends_roster_then_latest_usage()
    {
        var (link, _, phone, _, _) = Paired();
        var offline = new List<string>();
        link.PhoneWentOffline += offline.Add;

        link.OnFrame(Presence(phone.Id, false));
        link.OnFrame(Presence(phone.Id, false));
        Assert.Equal([phone.Id], offline);

        // No reading yet: the roster alone.
        Assert.Single(link.OnFrame(Presence(phone.Id, true)));
        link.OnFrame(Presence(phone.Id, false));

        var usage = """{"t":"usage","accounts":[]}""";
        Assert.Empty(link.SendUsage(usage));                                 // offline and no limit: not sent, but kept
        var back = link.OnFrame(Presence(phone.Id, true));
        Assert.Equal(2, back.Count);
        Assert.Equal("roster", phone.OpenFromDesktop(back[0]).GetProperty("t").GetString());
        Assert.Equal("usage", phone.OpenFromDesktop(back[1]).GetProperty("t").GetString());
        Assert.Equal(RelayCrypto.Ck(RelayCrypto.CoalesceKey(phone.Base()), RelayLink.UsageCk), CkOf(back[1]));
    }

    [Fact]
    public void A_limit_usage_goes_to_every_phone_with_push_and_e_and_a_plain_one_only_to_online_phones()
    {
        var (link, _, phone, _, _) = Paired();
        var plain = link.SendUsage("""{"t":"usage","accounts":[]}""").Single();
        Assert.Equal(("none", false), (Push(plain), HasE(plain)));
        Assert.Equal("state", J(plain).GetProperty("class").GetString());

        link.OnFrame(Presence(phone.Id, false));
        var limit = link.SendUsage("""{"t":"usage","accounts":[]}""", Crossing).Single();
        Assert.Equal(("limit", true), (Push(limit), HasE(limit)));
        Assert.Equal(RelayUsage.LimitE(Crossing), Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, J(limit).GetProperty("e").GetString())!));
    }

    private static readonly RelayUsage.Account Crossing = new("user@example.com", "claude", "max", 81, 1794117192, null, null, 1794111792, true);

    // ---- CB-321 addendum 2: the persona on done and limit pushes ---------------

    private static readonly PushPersona Aurora =
        PushPersona.From("3f7a9c1e5b2d8046", "Aurora", "6a3f8c2d1e0b9a7f4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3")!;

    private static RelaySession PersonaRow(string state, string title = "proj", RelayPersona? persona = null) =>
        new("s1", "claude", title, "/c", state, null, persona ?? new RelayPersona(Aurora.Name, "AU", null, Aurora.Id, Aurora.ImageHash));

    private static string FinishWith(RelayLink link, RelaySession generating, RelaySession idle)
    {
        link.OnScan([generating]);
        return link.OnScan([idle]).Single();
    }

    [Fact]
    public void A_done_push_names_the_session_s_roster_persona_last_and_logs_only_its_id_prefix()
    {
        var (link, _, phone, notes, _) = Paired();
        link.OnScan([PersonaRow("idle")]);
        var done = FinishWith(link, PersonaRow("generating"), PersonaRow("idle"));

        var e = Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, J(done).GetProperty("e").GetString())!);
        Assert.Equal(RelayLink.DoneE("s1", "proj", "", Aurora), e);
        Assert.EndsWith(""","persona":{"id":"3f7a9c1e5b2d8046","name":"Aurora","image_hash":"6a3f8c2d1e0b9a7f4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3"}}""", e);
        var note = Assert.Single(notes, n => n.StartsWith("push done", StringComparison.Ordinal));
        Assert.Equal("push done e=yes persona=3f7a9c avatar=yes", note);
        Assert.DoesNotContain(notes, n => n.Contains("Aurora", StringComparison.Ordinal) || n.Contains(Aurora.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void A_done_whose_persona_would_not_fit_drops_the_persona_before_the_e()
    {
        var (link, _, phone, notes, _) = Paired();
        // A title that fits only without the persona on this link's machine name
        // (frames-v6-vectors' done.persona_dropped is the same window for "MacBook Pro").
        var title = Enumerable.Range(1000, 400).Select(n => new string('t', n))
            .First(t => RelayLink.PushPlaintext(Aurora, p => RelayLink.DoneE("s1", t, "", p)) is { Bytes: not null, WithPersona: false });
        link.OnScan([PersonaRow("idle", title)]);
        var done = FinishWith(link, PersonaRow("generating", title), PersonaRow("idle", title));

        Assert.Equal(RelayLink.DoneE("s1", title, ""), Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, J(done).GetProperty("e").GetString())!));
        Assert.Contains("push done e=yes persona=dropped", notes);
    }

    [Fact]
    public void A_done_too_long_even_without_its_persona_goes_without_e_and_one_with_no_persona_says_none()
    {
        var (link, _, _, notes, _) = Paired();
        var title = new string('t', 3000);
        link.OnScan([PersonaRow("idle", title)]);
        Assert.False(HasE(FinishWith(link, PersonaRow("generating", title), PersonaRow("idle", title))));
        Assert.Contains("push done e=none persona=dropped", notes);

        // A session with no persona (or one from before v5 ids) is today's done.
        link.OnScan([Row("s2")]);
        var plain = FinishWith(link, Row("s2", "generating"), Row("s2", "idle"));
        Assert.True(HasE(plain));
        Assert.Contains("push done e=yes persona=none", notes);
    }

    [Fact]
    public void A_limit_speaks_as_the_default_persona_the_personas_frame_named_last()
    {
        var (link, _, phone, notes, _) = Paired();
        string LimitE() => Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey,
            J(link.SendUsage("""{"t":"usage","accounts":[]}""", Crossing).Single()).GetProperty("e").GetString())!);

        Assert.Equal(RelayUsage.LimitE(Crossing), LimitE());                 // before any personas frame: none
        Assert.Contains("push limit e=yes persona=none", notes);

        var frame = PersonaFrames.Personas([]);
        link.SendPersonas(frame, Aurora);
        Assert.Equal(RelayUsage.LimitE(Crossing, Aurora), LimitE());
        Assert.Contains("push limit e=yes persona=3f7a9c avatar=yes", notes);

        // The same bytes again still update the default: a default that went away goes away.
        Assert.Empty(link.SendPersonas(frame));
        Assert.Equal(RelayUsage.LimitE(Crossing), LimitE());
    }

    [Fact]
    public void The_persona_an_amber_speaks_as_is_the_session_s_roster_row_s()
    {
        var (link, _, _, _, _) = Paired();
        link.OnScan([PersonaRow("idle"), Row("s2"), new RelaySession("s3", "claude", "p", "/c", "idle", null, new RelayPersona("Old", "OL", null))]);
        Assert.Equal(Aurora, link.PersonaFor("s1"));
        Assert.Null(link.PersonaFor("s2"));                                  // no persona
        Assert.Null(link.PersonaFor("s3"));                                  // a persona with no v5 id cannot be named
        Assert.Null(link.PersonaFor("gone"));                                // not in the book
    }

    [Fact]
    public void Replies_and_windows_seal_to_the_asking_phone_with_the_session_ck()
    {
        var (link, _, phone, _, _) = Paired();
        var ck = RelayCrypto.Ck(RelayCrypto.CoalesceKey(phone.Base()), "s1");

        var reply = link.SendReply(phone.Id, "s1", RelayReplies.Ok("r", RelayReplies.Watching, "s1", 3)).Single();
        Assert.Equal(("action", ck), (J(reply).GetProperty("class").GetString(), CkOf(reply)));
        Assert.Equal("watching", phone.OpenFromDesktop(reply).GetProperty("code").GetString());
        Assert.Equal("", CkOf(link.SendReply(phone.Id, null, RelayReplies.Err("r", RelayReplies.BadSignature)).Single()));
        Assert.Empty(link.SendReply("ph_nobody", null, "{}"));

        var window = link.SendWindow(phone.Id, "s1", """{"t":"window"}""").Single();
        Assert.Equal(("chat", ck), (J(window).GetProperty("class").GetString(), CkOf(window)));
        Assert.Empty(link.SendWindow("ph_nobody", "s1", "{}"));
        link.OnFrame(Presence(phone.Id, false));
        Assert.Empty(link.SendWindow(phone.Id, "s1", "{}"));               // a watch dies with presence; never queued
    }

    [Fact]
    public void Nothing_v4_is_sent_before_welcome()
    {
        var (link, _, phone, _, _) = Paired();
        link.Disconnected();
        Assert.Empty(link.SendReply(phone.Id, null, "{}"));
        Assert.Empty(link.SendWindow(phone.Id, "s1", "{}"));
        Assert.Empty(link.SendUsage("{}"));
    }

    [Fact]
    public void Generating_to_idle_rides_its_delta_with_done_to_online_and_offline_phones()
    {
        var (link, _, phone, _, _) = Paired();
        var machine = new RelayLink(Identity(), new RelaySessionBook(), () => _now, "mac", "1", "h") { Machine = "MacBook Pro" };
        Assert.Equal("MacBook Pro", machine.Machine);

        link.OnScan([Row("s1")]);
        link.OnScan([Row("s1", "generating")]);
        var done = link.OnScan([Row("s1", "idle")]).Single();
        Assert.Equal(("done", true), (Push(done), HasE(done)));
        var delta = phone.OpenFromDesktop(done);
        Assert.Equal(("delta", "idle"), (delta.GetProperty("t").GetString(), delta.GetProperty("state").GetString()));
        var e = JsonDocument.Parse(PushSeal.Open(phone.KaKey, J(done).GetProperty("e").GetString())!).RootElement;
        Assert.Equal(("done", "s1", "proj", "proj on "), (e.GetProperty("t").GetString(), e.GetProperty("session_id").GetString(),
            e.GetProperty("title").GetString(), e.GetProperty("label").GetString()));

        // waiting -> idle is an answered prompt, not a finished turn.
        link.OnScan([Row("s1", "waiting")]);
        Assert.Equal("none", Push(link.OnScan([Row("s1", "idle")]).Single()));

        // Offline: the plain delta is not sent, the done one is.
        link.OnScan([Row("s1", "generating")]);
        link.OnFrame(Presence(phone.Id, false));
        Assert.Empty(link.OnScan([Row("s1", "waiting")]));
        link.OnScan([Row("s1", "generating")]);
        Assert.Equal("done", Push(link.OnScan([Row("s1", "idle")]).Single()));
    }

    [Fact]
    public void A_done_in_the_same_scan_as_a_new_session_is_still_sent_beside_the_roster()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnScan([Row("s1", "generating")]);
        var frames = link.OnScan([Row("s1", "idle"), Row("s2")]);
        Assert.Equal(["done", "none"], frames.Select(Push));
        Assert.Equal("roster", phone.OpenFromDesktop(frames[1]).GetProperty("t").GetString());
    }

    [Fact]
    public void A_done_e_too_long_for_the_relay_is_left_off()
    {
        var (link, _, _, _, _) = Paired();
        var longTitle = new string('t', 4000);
        link.OnScan([new RelaySession("s1", "claude", longTitle, "/c", "generating", null, null)]);
        var done = link.OnScan([new RelaySession("s1", "claude", longTitle, "/c", "idle", null, null)]).Single();
        Assert.Equal(("done", false), (Push(done), HasE(done)));
    }

    [Fact]
    public void The_done_label_is_the_ambers()
    {
        Assert.Equal("proj on Mac", RelayLink.DoneLabel("proj", "Mac"));
        Assert.Equal("Mac", RelayLink.DoneLabel(" ", "Mac"));
    }

    // CB-316: the envelope is written without HTML-safe escaping, so a base64
    // `+` stays one byte, and an envelope over the relay's frame cap is refused
    // at the send boundary — said in the log by size and class — never sent.
    [Fact]
    public void A_full_window_fits_the_relays_frame_and_an_oversize_one_is_never_sent()
    {
        var (link, _, phone, notes, _) = Paired();
        var full = new string('x', TranscriptWatch.MaxWindowBytes);
        var frame = link.SendWindow(phone.Id, "s1", full).Single();
        Assert.DoesNotContain("\\u002B", frame);
        Assert.Contains('+', J(frame).GetProperty("ct").GetString()!);
        Assert.True(Encoding.UTF8.GetByteCount(frame) <= RelayLink.MaxFrameBytes);

        Assert.Empty(link.SendWindow(phone.Id, "s1", new string('x', 50_000)));
        Assert.Contains(notes, n => n.StartsWith("envelope chat of ", StringComparison.Ordinal) && n.EndsWith("; not sent", StringComparison.Ordinal));

        // The next frame is unaffected.
        Assert.Single(link.SendWindow(phone.Id, "s1", "{}"));
    }

    // An action envelope with no id still dispatches: ref is then "", so the
    // phone's reply cannot be correlated, but nothing is dropped silently.
    [Fact]
    public void An_action_with_no_envelope_id_dispatches_with_an_empty_ref()
    {
        var (link, _, phone, _, _) = Paired();
        var got = new List<RelayRequest>();
        link.RequestReceived += (_, _, r) => got.Add(r);
        string NoId(string body)
        {
            var e = System.Text.Json.Nodes.JsonNode.Parse(phone.Sealed("action", body))!.AsObject();
            e.Remove("id");
            return e.ToJsonString();
        }
        link.OnFrame(NoId("""{"t":"unwatch","session_id":"s1"}"""));
        link.OnFrame(NoId("""{"session_id":"s1"}"""));
        Assert.Equal([new UnwatchRequest("", "s1"), new BadFrameRequest("", null)], got);
    }

    // ---- CB-321 personas frame and picture windows ----------------------------

    [Fact]
    public void A_phone_coming_online_gets_roster_then_usage_then_personas()
    {
        var (link, _, phone, _, _) = Paired();
        var personas = PersonaFrames.Personas([new PersonaFrames.Row("1cdab556cd43b1b2", "Vector", "VE", true, null)]);
        link.SendUsage("""{"t":"usage","accounts":[]}""");
        link.SendPersonas(personas);
        link.OnFrame(Presence(phone.Id, false));

        var back = link.OnFrame(Presence(phone.Id, true));
        Assert.Equal(["roster", "usage", "personas"], back.Select(f => phone.OpenFromDesktop(f).GetProperty("t").GetString()).ToArray());
        Assert.Equal(personas, phone.OpenFromDesktop(back[2]).GetRawText());
        Assert.Equal(("state", RelayCrypto.Ck(RelayCrypto.CoalesceKey(phone.Base()), PersonaFrames.PersonasCk)),
            (J(back[2]).GetProperty("class").GetString(), CkOf(back[2])));
    }

    [Fact]
    public void Personas_go_to_online_phones_only_when_their_bytes_change()
    {
        var (link, _, phone, _, _) = Paired();
        var a = PersonaFrames.Personas([]);
        var b = PersonaFrames.Personas([new PersonaFrames.Row("1cdab556cd43b1b2", "Vector", "VE", false, null)]);

        Assert.Single(link.SendPersonas(a));
        Assert.Empty(link.SendPersonas(a));                                  // unchanged: nothing
        link.OnFrame(Presence(phone.Id, false));
        Assert.Empty(link.SendPersonas(b));                                  // offline: kept, not sent
        var back = link.OnFrame(Presence(phone.Id, true));
        Assert.Equal(b, phone.OpenFromDesktop(back.Last()).GetRawText());   // the kept one, on connect

        // Before welcome nothing goes, and the frame is still remembered.
        link.Disconnected();
        Assert.Empty(link.SendPersonas(a));
        link.OnFrame(Welcome());
        Assert.Equal(a, phone.OpenFromDesktop(link.OnFrame(Presence(phone.Id, true)).Last()).GetRawText());
    }

    [Fact]
    public void No_personas_frame_is_sent_on_connect_before_there_is_one()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnFrame(Presence(phone.Id, false));
        Assert.Equal(["roster"], link.OnFrame(Presence(phone.Id, true)).Select(f => phone.OpenFromDesktop(f).GetProperty("t").GetString()).ToArray());
    }

    [Fact]
    public void Picture_windows_seal_to_the_asking_phone_as_chat_under_the_image_ck()
    {
        var (link, _, phone, _, _) = Paired();
        var hash = new string('a', 64);
        var window = link.SendImageWindow(phone.Id, hash, """{"t":"persona_image"}""").Single();
        Assert.Equal(("chat", RelayCrypto.Ck(RelayCrypto.CoalesceKey(phone.Base()), "img:" + hash)),
            (J(window).GetProperty("class").GetString(), CkOf(window)));
        Assert.Equal("persona_image", phone.OpenFromDesktop(window).GetProperty("t").GetString());

        Assert.Empty(link.SendImageWindow("ph_nobody", hash, "{}"));
        link.OnFrame(Presence(phone.Id, false));
        Assert.Empty(link.SendImageWindow(phone.Id, hash, "{}"));          // offline: dropped, never queued
        link.Disconnected();
        Assert.Empty(link.SendImageWindow(phone.Id, hash, "{}"));          // not welcomed
    }

    [Fact]
    public void A_persona_image_request_is_dispatched_and_a_bad_hash_is_bad_frame()
    {
        var (link, _, phone, _, _) = Paired();
        var got = new List<RelayRequest>();
        link.RequestReceived += (_, _, r) => got.Add(r);
        var hash = new string('b', 64);
        link.OnFrame(phone.Sealed("action", PersonaFrames.Request(hash)));
        link.OnFrame(phone.Sealed("action", """{"t":"persona_image","hash":"BB"}"""));
        Assert.Equal([new PersonaImageRequest("y", hash), new BadFrameRequest("y", null)], got);
    }
}
