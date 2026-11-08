using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // The desktop's half of the relay protocol as a pure state machine (CB-314
    // plan §2.1–2.4): frames in, frames out, events raised — no socket, no
    // HTTP, no timer, and the clock injected. RelayClient owns the socket and
    // feeds this; tests feed it directly, which is how every ordering a
    // network can produce (presence before welcome, a resync for a phone that
    // was just removed, a pair.request after the offer expired) gets a case
    // instead of a hope.
    //
    // Not thread-safe. RelayClient calls it under one lock.
    //
    // What it decides:
    //  * hello / welcome / unauthorized / version — whether the socket is up.
    //  * presence — which phones are online. Frames go only to online phones,
    //    and a phone coming online is sent a fresh roster. That is
    //    deliberate: the relay coalesces queued state frames by ck, which
    //    leaves seq gaps, and a phone would only resync out of them anyway.
    //  * scans — the book's roster/delta decision, numbered per phone.
    //  * pairing — an offer's pair_secret, opening pair.request, the check
    //    code, and on Allow the signed pair.accept plus the relay's link.
    //  * revoke — signed pair.revoke plus the relay-visible unlink, in either
    //    direction, and unlinks owed from while the socket was down.
    //  * every sealed frame's nonce, through RelaySendCounter, with the epoch
    //    persisted before it is used.
    internal sealed class RelayLink
    {
        internal const int Proto = 2;
        internal const int OfferAttempts = 5;

        internal sealed record PairOffer(string OfferId, DateTimeOffset ExpiresAt, byte[] PairSecret, string QrText)
        {
            internal int AttemptsLeft { get; set; } = OfferAttempts;
        }

        internal sealed record PairRequest(
            string OfferId, string PhoneId, string Name, byte[] IdPublic, byte[] KaPublic, byte[] PairKey, int CheckCode)
        {
            internal string CheckCodeDisplay => RelayCrypto.CheckCodeDisplay(CheckCode);
        }

        private sealed class Channel
        {
            internal required RelayIdentity.Phone Phone;
            internal required byte[] Base;
            internal required byte[] CkKey;
            internal required RelaySendCounter Counter;
            internal required RelayReceiveWindow Window;
            internal readonly RelayPhoneStream Stream = new();
            internal readonly Dictionary<ushort, byte[]> EpochKeys = new();
            internal bool Online;
            internal bool EpochPersisted;
            internal DateTimeOffset? LastSeen;

            internal byte[] KeyFor(ushort epoch)
            {
                if (!EpochKeys.TryGetValue(epoch, out var key))
                {
                    if (EpochKeys.Count > 8) EpochKeys.Clear();
                    EpochKeys[epoch] = key = RelayCrypto.EpochKey(Base, epoch);
                }
                return key;
            }
        }

        private readonly RelayIdentity _identity;
        private readonly RelaySessionBook _book;
        private readonly Func<DateTimeOffset> _now;
        private readonly string _platform;
        private readonly string _appVersion;
        private readonly string _relayHost;
        private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
        private readonly List<string> _pendingUnlinks = new();

        internal RelayLink(RelayIdentity identity, RelaySessionBook book, Func<DateTimeOffset> now,
            string platform, string appVersion, string relayHost)
        {
            _identity = identity;
            _book = book;
            _now = now;
            _platform = platform;
            _appVersion = appVersion;
            _relayHost = relayHost;
        }

        // ---- state anyone may read -------------------------------------------

        internal bool Welcomed { get; private set; }
        internal PairOffer? Offer { get; private set; }
        internal PairRequest? Pending { get; private set; }

        internal bool IsOnline(string phoneId) => _channels.TryGetValue(phoneId, out var c) && c.Online;
        internal DateTimeOffset? LastSeen(string phoneId) => _channels.TryGetValue(phoneId, out var c) ? c.LastSeen : null;
        internal IReadOnlyCollection<string> PendingUnlinks => _pendingUnlinks;

        // ---- events -----------------------------------------------------------

        internal event Action WelcomeReceived = delegate { };
        internal event Action Unauthorized = delegate { };
        internal event Action<string> Refused = delegate { };                    // version, or any hello error but unauthorized
        internal event Action<PairRequest> PairRequested = delegate { };
        internal event Action<RelayIdentity.Phone> PhonePaired = delegate { };
        internal event Action<string> PhoneRemoved = delegate { };               // by the phone, or the relay said unlinked
        internal event Action PresenceChanged = delegate { };
        internal event Action<string> Note = delegate { };                       // one log line, metadata only

        // ---- connection -------------------------------------------------------

        internal string Hello() => Frame(w =>
        {
            w.WriteString("t", "hello");
            w.WriteString("device_id", _identity.DeviceId);
            w.WriteString("token", _identity.Token);
            w.WriteString("platform", _platform);
            w.WriteString("app_version", _appVersion);
            w.WriteNumber("proto", Proto);
        });

        internal static string Ping() => PingFrame;
        private static readonly string PingFrame = Frame(w => w.WriteString("t", "ping"));

        // The socket closed. Every phone is offline until presence says so
        // again. Channels — and their send counters — are kept: the epoch moves
        // once per process start, not per reconnect, because a network that
        // drops every minute would otherwise spend a pairing's 65,536 epochs in
        // about six weeks. A phone coming back online gets a roster, which
        // restarts its numbering.
        internal void Disconnected()
        {
            Welcomed = false;
            foreach (var c in _channels.Values) c.Online = false;
        }

        internal IReadOnlyList<string> OnFrame(string text)
        {
            JsonElement f;
            try { f = JsonDocument.Parse(text).RootElement; }
            catch (JsonException) { Note("frame: not json"); return []; }
            if (f.ValueKind != JsonValueKind.Object) return [];

            if (Str(f, "t") is { } t) return OnControl(t, f);
            if (f.TryGetProperty("v", out _)) return OnEnvelope(f);
            Note("frame: no t and no v");
            return [];
        }

        private IReadOnlyList<string> OnControl(string t, JsonElement f)
        {
            switch (t)
            {
                case "ok" when Str(f, "ref") == "hello":
                    return OnWelcome();
                case "err" when Str(f, "ref") == "hello":
                    Welcomed = false;
                    if (Str(f, "code") == "unauthorized") Unauthorized();
                    else Refused(Str(f, "code") ?? "unknown");
                    return [];
                case "err" when Str(f, "code") == "unlinked":
                    // peer_id on current relays; the older image carried the
                    // id only in `message` (hana, 8 Nov), so both are read.
                    var gone = Str(f, "peer_id") ?? Str(f, "message");
                    if (gone is not null) DropPhone(gone, byPhone: true);
                    return [];
                case "err":
                    Note($"relay err ref={Str(f, "ref")} code={Str(f, "code")}");
                    return [];
                case "presence":
                    return OnPresence(f);
                case "ping":
                    return [Frame(w => w.WriteString("t", "pong"))];
                case "pong":
                    return [];
                case "resync":
                    return Str(f, "peer_id") is { } peer && _channels.TryGetValue(peer, out var c) && c.Online
                        ? Collect(RosterFor(c))
                        : [];
                default:
                    Note($"relay frame t={t} ignored");
                    return [];
            }
        }

        private IReadOnlyList<string> OnWelcome()
        {
            Welcomed = true;
            var phones = _identity.Phones;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var phone in phones) keep.Add(phone.PhoneId);
            foreach (var id in _channels.Keys.ToList())
                if (!keep.Contains(id)) _channels.Remove(id);
            foreach (var phone in phones)
                if (!_channels.ContainsKey(phone.PhoneId)) OpenChannel(phone, fresh: false);
            WelcomeReceived();

            // Unlinks owed from while the socket was down: Remove on the PC
            // must reach the relay (acceptance 4), so it is retried here.
            var frames = _pendingUnlinks.Select(UnlinkFrame).ToList();
            _pendingUnlinks.Clear();
            return frames;
        }

        private IReadOnlyList<string> OnPresence(JsonElement f)
        {
            if (Str(f, "device_id") is not { } id || !_channels.TryGetValue(id, out var c)) return [];
            var online = f.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
            // TryGetInt64 throws on a non-number rather than returning false,
            // so the kind is checked first: a relay that ever sends last_seen as
            // a string must not take the read loop down with it.
            if (f.TryGetProperty("last_seen", out var ls) && ls.ValueKind == JsonValueKind.Number && ls.TryGetInt64(out var secs))
                c.LastSeen = DateTimeOffset.FromUnixTimeSeconds(secs);
            var cameOnline = online && !c.Online;
            c.Online = online;
            PresenceChanged();
            return cameOnline ? Collect(RosterFor(c)) : [];
        }

        // ---- envelopes in -----------------------------------------------------

        private IReadOnlyList<string> OnEnvelope(JsonElement f)
        {
            var from = Str(f, "from");
            var to = Str(f, "to");
            var cls = Str(f, "class");
            var ct = Str(f, "ct");
            // A '|' in from or class could never name a real device or class,
            // and Aad refuses it — so it is dropped here, before anything
            // tries to open the box, rather than thrown out of the read loop.
            if (from is null || cls is null || ct is null || to != _identity.DeviceId || from.Contains('|') || cls.Contains('|'))
            {
                Note("envelope: misaddressed or incomplete");
                return [];
            }

            if (!_channels.TryGetValue(from, out var channel))
            {
                if (cls == "pair") OnPairRequest(from, Str(f, "offer"), ct);
                else Note($"envelope from unpaired {from} class={cls}");
                return [];
            }

            if (OpenFromPhone(channel, cls, ct) is not { } plain) return [];

            if (cls == "pair" && Json(plain) is { } body && Str(body, "t") == "pair.revoke")
            {
                var sig = RelayCrypto.FromBase64Url(Str(body, "sig"));
                if (RelayCrypto.Verify(channel.Phone.IdPublic, RelayCrypto.RevokeMessage(from, _identity.DeviceId!), sig))
                    DropPhone(from, byPhone: true);
                else
                    Note("pair.revoke with a bad signature ignored");
            }
            // Milestone 2 has nothing else a phone sends sealed; later
            // milestones' prompts and answers dispatch here.
            return [];
        }

        private byte[]? OpenFromPhone(Channel c, string cls, string ct)
        {
            if (RelayCrypto.PeekNonce(ct) is not { } n || n.Direction != RelayCrypto.PhoneToDesktop
                || !c.Window.WouldAccept(n.Epoch, n.Counter))
            {
                Note("envelope refused: wrong direction, replayed or reordered");
                return null;
            }
            var plain = RelayCrypto.Open(c.KeyFor(n.Epoch), ct, RelayCrypto.Aad(c.Phone.PhoneId, _identity.DeviceId!, cls), RelayCrypto.PhoneToDesktop);
            if (plain is null) { Note("envelope refused: did not open"); return null; }
            c.Window.Accept(n.Epoch, n.Counter);
            _identity.SaveReceive(c.Phone.PhoneId, n.Epoch, n.Counter);
            return plain;
        }

        // ---- pairing ----------------------------------------------------------

        // A fresh offer from POST /v1/offers. The pair_secret is made here and
        // never leaves this machine except inside the QR code.
        internal PairOffer BeginPairing(string offerId, DateTimeOffset expiresAt)
        {
            var (id, ka) = _identity.Keys();
            var secret = RandomNumberGenerator.GetBytes(32);
            Offer = new PairOffer(offerId, expiresAt, secret,
                RelayCrypto.PairQrText(_relayHost, _identity.DeviceId!, offerId, id.Public, ka.Public, secret));
            Pending = null;
            return Offer;
        }

        internal void CancelPairing()
        {
            Offer = null;
            Pending = null;
        }

        private void OnPairRequest(string from, string? offerId, string ct)
        {
            var offer = Offer;
            if (offer is null || offerId != offer.OfferId || _now() >= offer.ExpiresAt || offer.AttemptsLeft <= 0)
            {
                Note("pair.request for no live offer");
                return;
            }
            offer.AttemptsLeft--;

            var pairKey = RelayCrypto.PairKey(offer.PairSecret, offer.OfferId, _identity.DeviceId!, from);
            if (RelayCrypto.PeekNonce(ct) is not { Direction: RelayCrypto.PhoneToDesktop }
                || RelayCrypto.Open(pairKey, ct, RelayCrypto.Aad(from, _identity.DeviceId!, "pair"), RelayCrypto.PhoneToDesktop) is not { } plain
                || Json(plain) is not { } body
                || Str(body, "t") != "pair.request" || Str(body, "phone_id") != from)
            {
                Note("pair.request did not open");
                return;
            }

            var idPub = RelayCrypto.FromHex(Str(body, "id_pub"));
            var kaPub = RelayCrypto.FromHex(Str(body, "ka_pub"));
            if (!RelayCrypto.IsValidPublicKey(idPub) || !RelayCrypto.IsValidPublicKey(kaPub))
            {
                Note("pair.request with unusable keys");
                return;
            }

            var (dId, dKa) = _identity.Keys();
            var name = Str(body, "name") is { Length: > 0 } n ? n : "iPhone";
            Pending = new PairRequest(offer.OfferId, from, name, idPub!, kaPub!, pairKey,
                RelayCrypto.CheckCode(dId.Public, dKa.Public, idPub!, kaPub!));
            PairRequested(Pending);
        }

        // The owner clicked Allow on the request now pending. Sends the signed
        // pair.accept (sealed under the pair key, so only the phone that knew
        // the QR's secret opens it), then the relay-visible link, then the
        // phone's first roster. Nothing if the request is no longer current —
        // and nothing, with the request left pending, while the socket is
        // down: the accept and the link would have nowhere to go, and a phone
        // recorded as paired whose link the relay never heard of is worse than
        // a second click once the connection is back.
        internal IReadOnlyList<string> Allow(PairRequest request, string desktopName)
        {
            // Pending is cleared whenever the offer changes, so a request that
            // is still the pending one is always for the current offer.
            if (!ReferenceEquals(request, Pending) || _now() >= Offer!.ExpiresAt || !Welcomed) return [];
            var offer = Offer;

            var desktopId = _identity.DeviceId!;
            var (dId, dKa) = _identity.Keys();
            var sig = RelayCrypto.Sign(dId, RelayCrypto.AcceptMessage(desktopId, request.PhoneId,
                dId.Public, dKa.Public, request.IdPublic, request.KaPublic));
            var accept = Frame(w =>
            {
                w.WriteString("t", "pair.accept");
                w.WriteString("desktop_id", desktopId);
                w.WriteString("name", desktopName);
                w.WriteString("sig", RelayCrypto.Base64Url(sig));
            });
            var ct = RelayCrypto.Seal(request.PairKey, RelayCrypto.Nonce(RelayCrypto.DesktopToPhone, 0, 0),
                Encoding.UTF8.GetBytes(accept), RelayCrypto.Aad(desktopId, request.PhoneId, "pair"));

            var phone = _identity.AddPhone(request.PhoneId, request.Name, request.IdPublic, request.KaPublic);
            var frames = new List<string>
            {
                Envelope(desktopId, request.PhoneId, "pair", "", ct, offer.OfferId),
                Frame(w => { w.WriteString("t", "link"); w.WriteString("offer_id", offer.OfferId); w.WriteString("phone_id", request.PhoneId); }),
            };
            Offer = null;
            Pending = null;

            var c = OpenChannel(phone, fresh: true);
            c.Online = true;   // it is talking to us right now
            frames.AddRange(Collect(RosterFor(c)));
            PhonePaired(phone);
            return frames;
        }

        internal void Deny(PairRequest request)
        {
            if (ReferenceEquals(request, Pending)) Pending = null;
        }

        // Settings › Paired phones › Remove. The signed pair.revoke is for the
        // phone's own record and goes only if the phone has a channel; the
        // unlink is what the relay acts on, and is owed until it is sent.
        internal IReadOnlyList<string> Remove(string phoneId)
        {
            var frames = new List<string>();
            if (Welcomed && _channels.TryGetValue(phoneId, out var c))
            {
                var desktopId = _identity.DeviceId!;
                var sig = RelayCrypto.Sign(_identity.Keys().Identity, RelayCrypto.RevokeMessage(desktopId, phoneId));
                var revoke = Frame(w =>
                {
                    w.WriteString("t", "pair.revoke");
                    w.WriteString("by", desktopId);
                    w.WriteString("sig", RelayCrypto.Base64Url(sig));
                });
                if (SealTo(c, "pair", "", Encoding.UTF8.GetBytes(revoke)) is { } frame) frames.Add(frame);
            }

            if (Welcomed) frames.Add(UnlinkFrame(phoneId));
            else if (!_pendingUnlinks.Contains(phoneId)) _pendingUnlinks.Add(phoneId);

            _channels.Remove(phoneId);
            _identity.RemovePhone(phoneId);
            return frames;
        }

        private void DropPhone(string phoneId, bool byPhone)
        {
            var known = _identity.RemovePhone(phoneId) | _channels.Remove(phoneId);
            if (known && byPhone) PhoneRemoved(phoneId);
        }

        // ---- scans ------------------------------------------------------------

        internal IReadOnlyList<string> OnScan(IReadOnlyList<RelaySession> rows)
        {
            var update = _book.Update(rows, _now());
            if (update.IsEmpty || !Welcomed) return [];

            var frames = new List<string>();
            foreach (var c in _channels.Values.Where(c => c.Online))
            {
                if (update.NeedsRoster)
                {
                    frames.AddRange(Collect(RosterFor(c)));
                    continue;
                }
                // An online phone has always been sent a roster (presence,
                // Allow and resync all send one), so its stream is numbered.
                foreach (var d in update.Deltas)
                    if (SealTo(c, "state", RelayCrypto.Ck(c.CkKey, d.SessionId), c.Stream.Delta(d)!) is { } frame)
                        frames.Add(frame);
            }
            return frames;
        }

        private string? RosterFor(Channel c) =>
            SealTo(c, "state", RelayCrypto.Ck(c.CkKey, RosterCkSubject), c.Stream.Roster(_book.Snapshot()));

        // The ck a roster is coalesced under: not a session id, so it never
        // collides with one, and opaque to the relay like every other ck.
        internal const string RosterCkSubject = "\u0000roster";

        // ---- sealing out ------------------------------------------------------

        private Channel OpenChannel(RelayIdentity.Phone phone, bool fresh)
        {
            var (_, ka) = _identity.Keys();
            var desktopId = _identity.DeviceId!;
            var basis = RelayCrypto.SessionBase(RelayCrypto.SharedSecret(ka, phone.KaPublic), desktopId, phone.PhoneId);
            var now = _now();
            var counter = fresh
                ? RelaySendCounter.Fresh(now)
                : new RelaySendCounter(phone.SendEpoch, 0, now, phone.SendExhausted);
            if (!fresh) counter.Start(now);   // a new process: a new epoch, whatever the file said

            var c = new Channel
            {
                Phone = phone,
                Base = basis,
                CkKey = RelayCrypto.CoalesceKey(basis),
                Counter = counter,
                Window = phone.Received is { } r ? new RelayReceiveWindow(r.Epoch, r.Counter) : new RelayReceiveWindow(),
                // A fresh pairing's epoch 0 is already on disk (AddPhone).
                EpochPersisted = fresh || _identity.SaveSendEpoch(phone.PhoneId, counter.Epoch, counter.Exhausted),
            };
            _channels[phone.PhoneId] = c;
            return c;
        }

        // Null — and nothing sent — when the epoch cannot be persisted or the
        // pairing has used its last epoch. Either way sending would risk a
        // reused nonce, which is the one failure this design exists to rule out.
        private string? SealTo(Channel c, string cls, string ck, byte[] plaintext)
        {
            if (!c.EpochPersisted)
            {
                c.EpochPersisted = _identity.SaveSendEpoch(c.Phone.PhoneId, c.Counter.Epoch, c.Counter.Exhausted);
                if (!c.EpochPersisted) { Note("send epoch not persisted; holding frames"); return null; }
            }
            if (!c.Counter.TryNext(_now(), out var epoch, out var counter, out var rotated))
            {
                Note($"pairing with {c.Phone.PhoneId} has used every epoch; pair again");
                return null;
            }
            if (rotated && !_identity.SaveSendEpoch(c.Phone.PhoneId, epoch, c.Counter.Exhausted))
            {
                // The counter has moved on in memory; the next send retries the
                // write first and the epoch is never used unpersisted.
                c.EpochPersisted = false;
                Note("send epoch not persisted; holding frames");
                return null;
            }

            var desktopId = _identity.DeviceId!;
            var ct = RelayCrypto.Seal(c.KeyFor(epoch), RelayCrypto.Nonce(RelayCrypto.DesktopToPhone, epoch, counter),
                plaintext, RelayCrypto.Aad(desktopId, c.Phone.PhoneId, cls));
            return Envelope(desktopId, c.Phone.PhoneId, cls, ck, ct, null);
        }

        private string Envelope(string from, string to, string cls, string ck, string ct, string? offer) => Frame(w =>
        {
            w.WriteNumber("v", Proto);
            w.WriteString("id", Ulid(_now()));
            w.WriteString("from", from);
            w.WriteString("to", to);
            w.WriteNumber("ts", _now().ToUnixTimeSeconds());
            w.WriteString("class", cls);
            w.WriteString("push", "none");
            w.WriteString("ck", ck);
            w.WriteString("ct", ct);
            if (offer is not null) w.WriteString("offer", offer);
        });

        private static string UnlinkFrame(string peer) => Frame(w => { w.WriteString("t", "unlink"); w.WriteString("peer_id", peer); });

        // ---- helpers ----------------------------------------------------------

        private static IReadOnlyList<string> Collect(string? frame) => frame is null ? [] : [frame];

        private static string Frame(Action<Utf8JsonWriter> body)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static JsonElement? Json(byte[] utf8)
        {
            try
            {
                var e = JsonDocument.Parse(utf8).RootElement;
                return e.ValueKind == JsonValueKind.Object ? e : null;
            }
            catch (JsonException) { return null; }
        }

        // A ULID, as the spec's example envelope id: 48-bit millisecond time
        // then 80 random bits, Crockford base32. Unique and time-sortable,
        // which is all the relay needs of an id.
        internal static string Ulid(DateTimeOffset t)
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            Span<byte> bytes = stackalloc byte[16];
            var ms = (ulong)t.ToUnixTimeMilliseconds();
            for (var i = 5; i >= 0; i--) { bytes[i] = (byte)ms; ms >>= 8; }
            RandomNumberGenerator.Fill(bytes[6..]);

            Span<char> chars = stackalloc char[26];
            // 128 bits as 26 five-bit groups, the first group holding 3 bits.
            var hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
            var lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            var value = (UInt128)hi << 64 | lo;
            for (var i = 25; i >= 0; i--) { chars[i] = alphabet[(int)(value & 31)]; value >>= 5; }
            return new string(chars);
        }
    }
}
