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

        // How many times a frame the relay refused with rate_limited is sent
        // again before giving up on this connection. An unlink given up on is
        // still owed, and goes again on the next welcome.
        internal const int MaxRetries = 5;

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
        // Unlinks sent on this connection and not yet answered, in send order:
        // the relay handles a socket's frames in order, so its ok/err for
        // unlinks arrive in this order too.
        private readonly Queue<string> _unlinksInFlight = new();

        // Frames worth sending again if the relay refuses them for rate: the
        // pending link ("link") and the pair.accept envelope ("env:<id>").
        // State frames are not here — a phone that misses one resyncs.
        private readonly Dictionary<string, string> _retryable = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _retryAttempts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _revokeKeys = new(StringComparer.Ordinal);   // phone → its revoke's retry key

        // Keys with a retry scheduled and not yet fired. A flood beyond the
        // relay's raw ceiling returns dozens of unnamed rate_limited errors in
        // a millisecond, and each used to count as an attempt: a revoke was
        // given up on before one backoff had elapsed (measured on the live
        // relay). A key already waiting is not scheduled again, so attempts
        // count resends, not errors.
        private readonly HashSet<string> _retryPending = new(StringComparer.Ordinal);

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
        internal IReadOnlyList<string> PendingUnlinks => _identity.PendingUnlinks;

        // ---- events -----------------------------------------------------------

        internal event Action WelcomeReceived = delegate { };
        internal event Action Unauthorized = delegate { };
        internal event Action<string> Refused = delegate { };                    // version, or any hello error but unauthorized
        internal event Action<PairRequest> PairRequested = delegate { };
        internal event Action<RelayIdentity.Phone> PhonePaired = delegate { };
        internal event Action<string> PhoneRemoved = delegate { };               // by the phone, or the relay said unlinked
        internal event Action PresenceChanged = delegate { };
        internal event Action<string> Note = delegate { };                       // one log line, metadata only
        internal event Action<string, int> RetryDue = delegate { };              // (key, attempt): call Retry(key) after a backoff
        // CB-315: a sealed answer from a paired phone, with that phone's
        // pairing id_pub for the gate's rule 1. Only ever raised for an action
        // envelope that opened under the pair's session key and carried
        // t:"answer" — a /v1/ack push-ack (class control, plaintext ct) cannot
        // open, and nothing else is dispatched here.
        internal event Action<string, byte[], PhoneAnswer> AnswerReceived = delegate { };
        // CB-315 gate (a): the relay's ok pushed / push_throttled /
        // push_no_token for an amber, with APNs's round trip when it gave one.
        internal event Action<string, string, int?> PushReplied = delegate { };
        // CB-315 gate (a): the phone's notification extension acknowledging an
        // amber over /v1/ack (phone id, request id, the phone's at_ms). The one
        // unsealed ct in the protocol (frames-v3 http.ack), so it is read only
        // here, only on class control, only in its exact shape, and it reaches
        // nothing but this timing event — never the answer gate.
        internal event Action<string, string, long> PushAcked = delegate { };
        // CB-316: a phone's watch / unwatch / input / launch, from an action
        // envelope that opened under the pair's key (phone id, its pairing
        // id_pub for the launch signature, the request). A v4 type with the
        // wrong shape arrives as BadFrameRequest, to be answered bad_frame.
        internal event Action<string, byte[], RelayRequest> RequestReceived = delegate { };
        // CB-316: a phone's presence went from online to offline. Its watches
        // die with it, so the relay's queue never fills with windows for a
        // phone that is not reading them.
        internal event Action<string> PhoneWentOffline = delegate { };
        // CB-323: a scan's activity frame was deferred by the 2 s debounce;
        // call FlushActivity at this time. Raised once per deferral.
        internal event Action<DateTimeOffset> ActivityDue = delegate { };
        // CB-323: the relay's reply to an activity frame (ref, t, code), for
        // the activity log. Every reply, ok and err alike.
        internal event Action<string, string, string> ActivityReplied = delegate { };

        // This desktop's name, for the done push's label (the amber's rule:
        // "<title> on <machine>"). Set by RelayClient from its options.
        internal string Machine { get; init; } = "";

        // CB-323: this process's activity numbering (frames-v7 `activity`).
        // Null sends none — the protocol tests that predate v7 run without it;
        // RelayClient always sets one.
        internal RelayActivity.Tracker? Activity { get; init; }

        // CB-323: the minted prompt behind a waiting session, for the activity
        // frame's oldest amber (PromptPipeline.ActivityFor). Set by RelayClient
        // once the pipeline exists.
        internal Func<string, ActivityPrompt?> ActivityPrompts { get; set; } = _ => null;

        // One "this relay predates activity" line per connection, not one per
        // frame every two seconds.
        private bool _activityUnexpectedNoted;

        // The usage frame a phone coming online is sent after its roster:
        // whatever SendUsage last sent, or nothing before the first reading.
        private string? _latestUsage;

        // CB-321: the personas frame a phone coming online is sent after its
        // roster and usage, and what SendPersonas compares against so an
        // unchanged frame is not sent again every scan.
        private string? _latestPersonas;
        // CB-321 addendum 2: the default persona that frame named, for limit pushes.
        private PushPersona? _defaultPersona;

        // The accounts frame a phone coming online is sent after the personas frame,
        // and what SendAccounts compares against so an unchanged one is not resent.
        private string? _latestAccounts;

        // A session this desktop saw go generating -> idle, by id.
        internal event Action<string> SessionFinished = delegate { };

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
            _unlinksInFlight.Clear();
            _retryable.Clear();
            _retryAttempts.Clear();
            _revokeKeys.Clear();
            _retryPending.Clear();
            Activity?.Reset();
            _activityUnexpectedNoted = false;
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
                // Before the ref-based cases: a rate-limited unlink or link is a
                // frame to resend, not a refusal to settle.
                case "err" when Str(f, "code") == "rate_limited":
                    OnRateLimited(Str(f, "ref"));
                    return [];
                // CB-323: the relay's answer to an activity frame. After the
                // rate_limited case, which resends it.
                case "ok" or "err" when RelayActivity.IsActivityRef(Str(f, "ref")):
                    OnActivityReply(t, Str(f, "ref")!, Str(f, "code") ?? "");
                    return [];
                case "err" when Str(f, "ref") == "unlink":
                    // not_linked: already gone, which is what was wanted. Any
                    // other refusal leaves it owed, retried on the next welcome.
                    if (Str(f, "code") == "not_linked") SettleUnlink();
                    else if (_unlinksInFlight.Count > 0) _unlinksInFlight.Dequeue();
                    return [];
                case "err" when Str(f, "code") == "unlinked":
                    // peer_id on current relays; the older image carried the
                    // id only in `message` (hana, 8 Nov), so both are read.
                    var gone = Str(f, "peer_id") ?? Str(f, "message");
                    if (gone is not null) DropPhone(gone, byPhone: true);
                    return [];
                case "err":
                    Note($"relay err ref={Safe(Str(f, "ref"))} code={Safe(Str(f, "code"))}");
                    return [];
                case "presence":
                    return OnPresence(f);
                case "ok" when Str(f, "ref") == "unlink":
                    SettleUnlink();
                    return [];
                case "ok" when Str(f, "ref") == "link":
                    // The link landed, so the accept that preceded it did too
                    // (the relay answers a socket's frames in order).
                    _retryable.Clear();
                    _retryAttempts.Remove("link");
                    return [];
                case "ok" when Str(f, "code") is "pushed" or "push_throttled" or "push_no_token" && Str(f, "ref") is { } pushed:
                    PushReplied(pushed, Str(f, "code")!, f.TryGetProperty("apns_ms", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt32(out var n) ? n : null);
                    return [];
                case "ok":
                    return [];
                case "ping":
                    return [Frame(w => w.WriteString("t", "pong"))];
                case "pong":
                    return [];
                case "resync":
                    if (Str(f, "peer_id") is { } peer && _channels.TryGetValue(peer, out var c) && c.Online)
                    {
                        Note($"resync from {peer}: fresh roster");
                        return Collect(RosterFor(c));
                    }
                    // Said, not silent: a phone stuck waiting on a roster
                    // that never comes is otherwise undiagnosable.
                    var who = Str(f, "peer_id");
                    Note($"resync for {Safe(who)} ignored: not an online paired phone ("
                         + (who is not null && _channels.ContainsKey(who) ? "paired, offline" : "not paired") + ")");
                    return [];
                default:
                    Note($"relay frame t={Safe(t)} ignored");
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

            // Unlinks owed — from while the socket was down, from a previous
            // run, or sent and never answered: Remove on the PC must reach the
            // relay (acceptance 4), so every one is sent again here.
            _unlinksInFlight.Clear();
            // CB-323: the activity snapshot first, on every welcome — before
            // anything else this connection sends, rosters included.
            var frames = ActivityFrames(RelayActivity.Reason.Welcome);
            frames.AddRange(_identity.PendingUnlinks.Select(SendUnlink));
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
            var wentOffline = !online && c.Online;
            c.Online = online;
            PresenceChanged();
            if (wentOffline) PhoneWentOffline(id);
            return cameOnline ? RosterAndUsage(c) : [];
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
                else Note($"envelope from unpaired {Safe(from)} class={Known(cls, Classes)}");
                return [];
            }

            if (cls == "control")
            {
                if (ParsePushAck(ct) is { } ack) PushAcked(from, ack.RequestId, ack.AtMs);
                else Note($"control envelope from {Safe(from)} not a push-ack; ignored");
                return [];
            }

            if (OpenFromPhone(channel, cls, ct) is not { } plain) return [];

            if (cls == "pair" && Json(plain) is { } body && Str(body, "t") == "pair.revoke")
            {
                var sig = RelayCrypto.FromBase64Url(Str(body, "sig"));
                if (RelayCrypto.Verify(channel.Phone.IdPublic, RelayCrypto.RevokeMessage(from, _identity.DeviceId!), sig))
                {
                    // Said positively, so a gate can confirm the signature check
                    // ran and passed rather than infer it from a missing refusal.
                    Note($"pair.revoke from {from} verified; phone removed (its own unlink settles the link at the relay)");
                    DropPhone(from, byPhone: true);
                }
                else
                    Note("pair.revoke with a bad signature ignored");
            }
            // CB-323: the phone's watch key, added or removed. Class pair only,
            // out of a box that opened under this pair's key, as pair.revoke.
            else if (cls == "pair" && Json(plain) is { } keyFrame && RelayWatchKey.IsKeyFrame(Str(keyFrame, "t")))
                return OnWatchKey(channel, from, keyFrame);
            // CB-315: the phone's answer to an amber prompt. Class action only,
            // so nothing that arrived on another class — and nothing that did
            // not open under this pair's key — can reach the gate.
            else if (cls == "action" && Json(plain) is { } act && Str(act, "t") == "answer")
            {
                if (ParseAnswer(act) is { } answer) AnswerReceived(from, channel.Phone.IdPublic, answer);
                else Note($"answer from {from} malformed; ignored");
            }
            // CB-316: watch / unwatch / input / launch. Class action only, and
            // only out of a box that opened under this pair's key, exactly as
            // the answer above. The envelope id is the ref every reply echoes.
            else if (cls == "action" && Json(plain) is { } req
                     && RelayRequests.Parse(req, Str(f, "id") ?? "") is { } request)
            {
                Note($"{Str(req, "t")} from {from} ref={Safe(request.Ref)}");
                RequestReceived(from, channel.Phone.IdPublic, request);
            }
            // A sealed action that opened but is neither: a frame that does not
            // parse or has no t is answered bad_frame (frames-v4 canonical rule);
            // an unknown t from a newer phone is left alone.
            else if (cls == "action" && (Json(plain) is not { } other || Str(other, "t") is null))
            {
                Note($"action from {from} with no t; bad_frame");
                RequestReceived(from, channel.Phone.IdPublic, new BadFrameRequest(Str(f, "id") ?? "", null));
            }
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
            var acceptId = Ulid(_now());
            var acceptFrame = Envelope(desktopId, request.PhoneId, "pair", "", ct, offer.OfferId, acceptId);
            var linkFrame = Frame(w => { w.WriteString("t", "link"); w.WriteString("offer_id", offer.OfferId); w.WriteString("phone_id", request.PhoneId); });
            _retryable["env:" + acceptId] = acceptFrame;
            _retryable["link"] = linkFrame;
            var frames = new List<string> { acceptFrame, linkFrame };
            Offer = null;
            Pending = null;

            var c = OpenChannel(phone, fresh: true);
            c.Online = true;   // it is talking to us right now
            frames.AddRange(RosterAndUsage(c));
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
                // Retryable by its id if the relay refuses it for rate: the
                // phone's own record of the removal. Dropped once the unlink
                // settles, after which the relay would refuse it anyway.
                var revokeId = Ulid(_now());
                if (SealTo(c, "pair", "", Encoding.UTF8.GetBytes(revoke), revokeId) is { } frame)
                {
                    frames.Add(frame);
                    _retryable["env:" + revokeId] = frame;
                    _revokeKeys[phoneId] = "env:" + revokeId;
                }
            }

            // Owed until the relay says ok, persisted first so a quit between
            // here and the answer cannot lose it.
            _identity.AddPendingUnlink(phoneId);
            if (Welcomed) frames.Add(SendUnlink(phoneId));
            Note($"removed {phoneId}: " + (Welcomed ? "signed revoke and unlink sent" : "unlink owed until the next welcome"));

            _channels.Remove(phoneId);
            _identity.RemovePhone(phoneId);
            return frames;
        }

        // key.add / key.remove: decided against what is stored for this phone,
        // persisted before key.ok goes, and answered sealed, class pair, to the
        // phone that sent it. A decision that could not be persisted is not
        // answered at all — the phone resends, and a repeat of an applied add
        // or remove is confirmed without changing anything.
        private IReadOnlyList<string> OnWatchKey(Channel channel, string from, JsonElement frame)
        {
            var phone = _identity.PhoneById(from);
            if (phone is null) return [];
            var before = new WatchKeyState(phone.WatchPublic, phone.WatchAt);
            var d = RelayWatchKey.Decide(frame, from, phone.IdPublic, before);
            if (d.Accepted && !d.After.Same(before) && !_identity.SaveWatchKey(from, d.After.Public, d.After.At!.Value))
            {
                Note($"key.{d.Op} from {from}: accepted but not persisted; no reply");
                return [];
            }
            Note($"key.{d.Op} from {from}: {d.Code ?? "ok"}");
            return Collect(SealTo(channel, "pair", "", Encoding.UTF8.GetBytes(d.Reply())));
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

            // Once per finished session, whoever is paired: the client turns it into
            // the phone's spoken-style summary (watched sessions only).
            foreach (var d in update.Deltas.Where(d => d.Finished)) SessionFinished(d.SessionId);

            // CB-323: the activity snapshot leads the scan's frames. A scan that
            // sends a done push sends a changed snapshot at once, so the relay's
            // counts on that alert already include it; any other scan is
            // debounced.
            var pushes = _channels.Count > 0 && update.Deltas.Any(d => d.Finished);
            var frames = ActivityFrames(pushes ? RelayActivity.Reason.Push : RelayActivity.Reason.Scan);
            foreach (var c in _channels.Values)
            {
                // CB-316: a session that went generating -> idle rides its own
                // delta with push done, to every paired phone — online or not,
                // because the push is what reaches a backgrounded phone. Sent
                // even when this scan also needs a roster, which would
                // otherwise swallow the transition.
                foreach (var d in update.Deltas.Where(d => d.Finished))
                    frames.AddRange(Collect(DoneFor(c, d)));
                if (!c.Online) continue;
                if (update.NeedsRoster)
                {
                    frames.AddRange(Collect(RosterFor(c)));
                    continue;
                }
                // An online phone has always been sent a roster (presence,
                // Allow and resync all send one), so its stream is numbered.
                foreach (var d in update.Deltas.Where(d => !d.Finished))
                    if (SealTo(c, "state", RelayCrypto.Ck(c.CkKey, d.SessionId), c.Stream.Delta(d)!) is { } frame)
                        frames.Add(frame);
            }
            return frames;
        }

        // The done delta (frames-v4 sealed_plaintext.done): the v2 delta itself,
        // push done, and an e sealed to this phone's agreement key naming the
        // session the amber's way — "<title> on <machine>", or the machine
        // alone when the session has no title.
        //
        // CB-321 addendum 2: the e also names the session's roster persona, so
        // the phone can show it as the sender (frames-v6 done_e).
        private string? DoneFor(Channel c, RelayDelta d)
        {
            // The book has just stamped this row (a finished delta is never a
            // removal), so it is there to name the session.
            var row = _book.Find(d.SessionId)!;
            var persona = PushPersona.Of(row.Persona);
            var e = PushE(c, "done", persona, p => DoneE(d.SessionId, row.Title, Machine, p));
            return SealTo(c, "state", RelayCrypto.Ck(c.CkKey, d.SessionId), c.Stream.DeltaForPush(d), push: "done", e: e);
        }

        // A push e sealed to this phone, or none when it could not fit the
        // relay's 3600-character cap (a very long title): the alert then shows
        // the generic text, which beats the relay refusing the envelope.
        // frames-v6: with a persona, the e is tried with it first and then
        // without it before giving up, so the persona goes before e does. The
        // log line says which, by the persona's id prefix only.
        private string? PushE(Channel c, string kind, PushPersona? persona, Func<PushPersona?, string> plaintext)
        {
            var (bytes, withPersona) = PushPlaintext(persona, plaintext);
            Note($"push {kind} e={(bytes is null ? "none" : "yes")} {PushPersona.LogTagFor(persona, withPersona)}");
            return bytes is null ? null : PushSeal.Seal(c.Phone.KaPublic, bytes);
        }

        // The done/limit fitting rule (frames-v6 done_e, limit_e), pure so the
        // vectors and the tests run the one the app does: the plaintext with
        // the persona if its e fits, else without it if that fits, else none.
        internal static (byte[]? Bytes, bool WithPersona) PushPlaintext(PushPersona? persona, Func<PushPersona?, string> plaintext)
        {
            if (persona is not null && Fits(plaintext(persona)) is { } with) return (with, true);
            return (Fits(plaintext(null)), false);

            static byte[]? Fits(string p)
            {
                var bytes = Encoding.UTF8.GetBytes(p);
                return PushSeal.SealedLength(bytes.Length) <= PushSeal.MaxE ? bytes : null;
            }
        }

        // CB-321 addendum 2: the persona a session's amber speaks as — its
        // roster row's, as last projected. Null for a session the book does not
        // hold or one with no persona.
        internal PushPersona? PersonaFor(string sessionId) => PushPersona.Of(_book.Find(sessionId)?.Persona);

        internal static string DoneLabel(string title, string machine) =>
            string.IsNullOrWhiteSpace(title) ? machine : $"{title} on {machine}";

        internal static string DoneE(string sessionId, string title, string machine, PushPersona? persona = null)
        {
            var j = new CanonicalJson().Num("v", 1).Str("t", "done").Str("session_id", sessionId)
                .Str("label", DoneLabel(title, machine)).Str("title", title);
            if (persona is not null) j.Raw("persona", persona.Canonical());
            return j.ToString();
        }

        // ---- CB-316 replies, windows and usage -------------------------------------

        internal const string UsageCk = "usage";

        // A sealed ok/err to the phone that asked. ck is the session's when the
        // reply names one (so the phone can group it without opening it), else
        // empty — frames-v4 ok.direction.
        internal IReadOnlyList<string> SendReply(string phoneId, string? sessionId, string plaintext)
        {
            if (!Welcomed || !_channels.TryGetValue(phoneId, out var c)) return [];
            return Collect(SealTo(c, "action", sessionId is null ? "" : RelayCrypto.Ck(c.CkKey, sessionId), Encoding.UTF8.GetBytes(plaintext)));
        }

        // One window to the phone watching. Online only: a watch dies with the
        // phone's presence, so a window for an offline phone is a bug upstream,
        // and is dropped here rather than filling the relay's queue.
        internal IReadOnlyList<string> SendWindow(string phoneId, string sessionId, string plaintext)
        {
            if (!Welcomed || !_channels.TryGetValue(phoneId, out var c) || !c.Online) return [];
            return Collect(SealTo(c, "chat", RelayCrypto.Ck(c.CkKey, sessionId), Encoding.UTF8.GetBytes(plaintext)));
        }

        // The usage frame to every online phone — or, carrying a limit crossing,
        // to every paired phone with push limit and that phone's own e, since
        // the alert has to reach a phone that is not open. Remembered either
        // way, for the next phone that comes online.
        //
        // CB-321 addendum 2: a crossing's e speaks as the PC's default persona
        // (the personas frame's default:true row, as last sent), not a
        // session's — a limit belongs to an account (frames-v6 limit_e).
        internal IReadOnlyList<string> SendUsage(string plaintext, RelayUsage.Account? crossing = null)
        {
            _latestUsage = plaintext;
            if (!Welcomed) return [];
            var frames = new List<string>();
            foreach (var c in _channels.Values)
            {
                if (crossing is null && !c.Online) continue;
                var e = crossing is null ? null : PushE(c, "limit", _defaultPersona, p => RelayUsage.LimitE(crossing, p));
                frames.AddRange(Collect(SealTo(c, "state", RelayCrypto.Ck(c.CkKey, UsageCk), Encoding.UTF8.GetBytes(plaintext),
                    push: crossing is null ? "none" : "limit", e: e)));
            }
            return frames;
        }

        // CB-321: the personas frame (frames-v5 personas), to every online phone
        // when its canonical bytes changed since the last one, and remembered for
        // the next phone that comes online. class state, ck Ck("personas"), so
        // the relay coalesces it for an offline phone as it does usage.
        //
        // CB-321 addendum 2: and the default row, kept as the persona a limit
        // push speaks as. Taken even when the frame's bytes did not change, so
        // it is always the one the phones were last told about.
        internal IReadOnlyList<string> SendPersonas(string plaintext, PushPersona? defaultPersona = null)
        {
            _defaultPersona = defaultPersona;
            if (plaintext == _latestPersonas) return [];
            _latestPersonas = plaintext;
            if (!Welcomed) return [];
            var frames = new List<string>();
            foreach (var c in _channels.Values.Where(c => c.Online))
                frames.AddRange(Collect(PersonasFor(c, plaintext)));
            return frames;
        }

        // The accounts a launch may name (additive to frames-v4): class state, ck Ck("accounts"),
        // to every online phone when its bytes changed, and remembered for the next phone online.
        internal IReadOnlyList<string> SendAccounts(string plaintext)
        {
            if (plaintext == _latestAccounts) return [];
            _latestAccounts = plaintext;
            if (!Welcomed) return [];
            var frames = new List<string>();
            foreach (var c in _channels.Values.Where(c => c.Online))
                frames.AddRange(Collect(AccountsFor(c, plaintext)));
            return frames;
        }

        private string? AccountsFor(Channel c, string plaintext) =>
            SealTo(c, "state", RelayCrypto.Ck(c.CkKey, "accounts"), Encoding.UTF8.GetBytes(plaintext));

        // What a session just did, in a few spoken-style sentences (additive to frames-v4): class
        // state, ck Ck("summary:" + session_id) so the relay keeps only the newest per session.
        // To the phones named, online only: a summary is for a phone that is watching.
        internal IReadOnlyList<string> SendSummary(string sessionId, string plaintext, IReadOnlyCollection<string> phones)
        {
            if (!Welcomed) return [];
            var frames = new List<string>();
            foreach (var c in _channels.Values.Where(c => c.Online && phones.Contains(c.Phone.PhoneId)))
                frames.AddRange(Collect(SealTo(c, "state", RelayCrypto.Ck(c.CkKey, "summary:" + sessionId), Encoding.UTF8.GetBytes(plaintext))));
            return frames;
        }

        private string? PersonasFor(Channel c, string plaintext) =>
            SealTo(c, "state", RelayCrypto.Ck(c.CkKey, PersonaFrames.PersonasCk), Encoding.UTF8.GetBytes(plaintext));

        // One persona_image window to the phone that asked, class chat, ck
        // Ck("img:" + hash). Online only, as transcript windows are: a picture
        // in flight to a phone that went away is abandoned upstream, and a
        // window that still arrives here for it is dropped rather than queued.
        internal IReadOnlyList<string> SendImageWindow(string phoneId, string hash, string plaintext)
        {
            if (!Welcomed || !_channels.TryGetValue(phoneId, out var c) || !c.Online) return [];
            return Collect(SealTo(c, "chat", RelayCrypto.Ck(c.CkKey, PersonaFrames.ImageCk(hash)), Encoding.UTF8.GetBytes(plaintext)));
        }

        // A roster's ck is the empty string (CB-314 addendum 6): ck names a
        // session for coalescing, and a roster is every session at once.
        // Always the whole book, unconditionally — a resync asks for the book,
        // not the diff — and it restarts the phone's numbering at 1.
        private string? RosterFor(Channel c) =>
            SealTo(c, "state", "", c.Stream.Roster(_book.Snapshot()));

        // CB-316: a phone coming online (or just paired) gets its roster, then
        // the latest usage frame (frames-v4: "sent on connect, after the
        // roster"), when there has been a reading at all.
        private List<string> RosterAndUsage(Channel c)
        {
            var frames = new List<string>(Collect(RosterFor(c)));
            if (_latestUsage is { } usage)
                frames.AddRange(Collect(SealTo(c, "state", RelayCrypto.Ck(c.CkKey, UsageCk), Encoding.UTF8.GetBytes(usage))));
            // CB-321: then the personas frame (frames-v5: after the roster and
            // the usage frame).
            if (_latestPersonas is { } personas) frames.AddRange(Collect(PersonasFor(c, personas)));
            if (_latestAccounts is { } accounts) frames.AddRange(Collect(AccountsFor(c, accounts)));
            return frames;
        }

        // ---- CB-315 prompts ------------------------------------------------------

        private static readonly System.Text.RegularExpressions.Regex UlidShape = new("^[0-9A-HJKMNP-TV-Z]{26}$");

        // A push-ack's plaintext ct: base64 of {"t":"push-ack","request_id":<ULID>,"at_ms":N}
        // and exactly those three keys, the relay's own rule for the one unsealed
        // ct. Null for anything else, including the right three plus another.
        internal static (string RequestId, long AtMs)? ParsePushAck(string ct)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(ct); }
            catch (FormatException) { return null; }
            if (Json(raw) is not { } j || j.ValueKind != JsonValueKind.Object || j.EnumerateObject().Count() != 3
                || Str(j, "t") != "push-ack" || Str(j, "request_id") is not { } rid || !UlidShape.IsMatch(rid)
                || !j.TryGetProperty("at_ms", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var ms))
                return null;
            return (rid, ms);
        }

        // The answer frame's fields, or null when any is missing or the wrong
        // type: a frame the gate could not even read is not an answer.
        internal static PhoneAnswer? ParseAnswer(JsonElement f)
        {
            string? S(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("request_id") is not { } rid || S("session_id") is not { } sid || S("action") is not { } action
                || S("scope") is not { } scope || S("note") is not { } note || S("nonce") is not { } nonce
                || S("screen_hash") is not { } hash || S("sig") is not { } sig
                || !f.TryGetProperty("expires_at", out var e) || e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var exp))
                return null;
            // CB-323: `key` picks the verifying key. A non-string is no name
            // the gate knows, so it reads as "" and fails rule 1.
            var key = f.TryGetProperty("key", out var k) ? (k.ValueKind == JsonValueKind.String ? k.GetString() : "") : null;
            return new PhoneAnswer(rid, sid, action, scope, note, nonce, exp, hash, sig, key);
        }

        // One amber to every paired phone, online or not — action frames are
        // queued by the relay, and the push is what reaches a locked phone.
        // The envelope id is the request_id (frames-v3), and each phone gets
        // its own e, sealed to its own agreement key; the e is left off when
        // the plaintext cannot fit the relay's 3600-character cap.
        internal IReadOnlyList<string> SendPrompt(string requestId, string sessionId, string prompt, string? pushPlaintext)
        {
            if (!Welcomed) return [];
            var frames = new List<string>();
            foreach (var c in _channels.Values)
            {
                var e = pushPlaintext is null ? null : PushSeal.Seal(c.Phone.KaPublic, System.Text.Encoding.UTF8.GetBytes(pushPlaintext));
                if (SealTo(c, "action", RelayCrypto.Ck(c.CkKey, sessionId), System.Text.Encoding.UTF8.GetBytes(prompt), requestId, "amber", e) is { } frame)
                    frames.Add(frame);
            }
            return WithActivity(frames);
        }

        // resolved to every paired phone, so a sheet open on any of them closes:
        // push resolve, envelope id = the request_id it resolves, and ck the
        // amber's own (the same session's Ck) so the passive "Answered at the
        // desk" alert replaces the amber in its thread. e, when the pipeline
        // gives a plaintext, is the sealed resolve, per phone, for that alert.
        internal IReadOnlyList<string> SendResolved(string requestId, string sessionId, string by, string? pushPlaintext = null)
        {
            if (!Welcomed) return [];
            var plain = System.Text.Encoding.UTF8.GetBytes(PromptFrames.Resolved(requestId, sessionId, by));
            var frames = new List<string>();
            foreach (var c in _channels.Values)
            {
                var e = pushPlaintext is null ? null : PushSeal.Seal(c.Phone.KaPublic, System.Text.Encoding.UTF8.GetBytes(pushPlaintext));
                if (SealTo(c, "action", RelayCrypto.Ck(c.CkKey, sessionId), plain, requestId, "resolve", e) is { } frame)
                    frames.Add(frame);
            }
            return WithActivity(frames);
        }

        // A refusal goes only to the phone that answered.
        internal IReadOnlyList<string> SendRefusal(string phoneId, string requestId, string sessionId, string code)
        {
            if (!Welcomed || !_channels.TryGetValue(phoneId, out var c)) return [];
            return Collect(SealTo(c, "action", RelayCrypto.Ck(c.CkKey, sessionId),
                System.Text.Encoding.UTF8.GetBytes(PromptFrames.Refusal(requestId, sessionId, code))));
        }

        // ---- CB-323 activity -------------------------------------------------------

        // The snapshot as of now, as zero or one frames to send now.
        private List<string> ActivityFrames(RelayActivity.Reason reason)
        {
            if (Activity is null) return [];
            var frame = Activity.Offer(Snapshot(), _now(), reason, out var becameDue);
            if (becameDue) ActivityDue(Activity.DueAt!.Value);
            return frame is null ? [] : [frame];
        }

        private ActivitySnapshot Snapshot() => RelayActivity.Snapshot(_book.Snapshot(), ActivityPrompts, Machine);

        // A push-bearing send: the snapshot goes first, and only when the push
        // itself went somewhere.
        private List<string> WithActivity(List<string> pushFrames)
        {
            if (pushFrames.Count == 0) return pushFrames;
            var frames = ActivityFrames(RelayActivity.Reason.Push);
            frames.AddRange(pushFrames);
            return frames;
        }

        // The relay did not take a snapshot (rate_limited): the latest one goes
        // again, same boot, next seq, once the interval has passed — whatever
        // the desk does meanwhile, so a quiet desk is not left wrong at the
        // relay. Refused again, it is re-armed again, until one is taken.
        private void RetryActivity()
        {
            if (Activity?.Retry(_now()) is true) ActivityDue(Activity.DueAt!.Value);
        }

        // The deferred send ActivityDue asked for, when it is time.
        internal IReadOnlyList<string> FlushActivity()
        {
            if (Activity is null || !Welcomed) return [];
            return Collect(Activity.Flush(Snapshot(), _now()));
        }

        private void OnActivityReply(string t, string reference, string code)
        {
            ActivityReplied(reference, t, code);
            if (t == "ok" && code == "activity") return;
            if (t == "err" && code == "unexpected")
            {
                // frames-v7 back_compat: a relay that predates activity. Said
                // once per connection; everything else carries on.
                if (_activityUnexpectedNoted) return;
                _activityUnexpectedNoted = true;
                Note("relay does not know activity frames (err unexpected): an older relay; the island will not count this desktop");
                return;
            }
            Note($"activity {Safe(reference)} answered {Known(t, ControlKinds)} code={Safe(code)}");
        }

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
        private string? SealTo(Channel c, string cls, string ck, byte[] plaintext, string? id = null, string push = "none", string? e = null)
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
            var envelope = Envelope(desktopId, c.Phone.PhoneId, cls, ck, ct, null, id, push, e);
            // The send boundary: a frame the relay would close the socket over is
            // never sent. Said in the log, by size and class only; the counter it
            // used is simply skipped, which the phone's window allows.
            var bytes = Encoding.UTF8.GetByteCount(envelope);
            if (bytes > MaxFrameBytes)
            {
                Note($"envelope {Known(cls, Classes)} of {bytes} bytes is over the relay's {MaxFrameBytes}; not sent");
                return null;
            }
            return envelope;
        }

        private string Envelope(string from, string to, string cls, string ck, string ct, string? offer, string? id = null,
            string push = "none", string? e = null) => Frame(w =>
        {
            w.WriteNumber("v", Proto);
            w.WriteString("id", id ?? Ulid(_now()));
            w.WriteString("from", from);
            w.WriteString("to", to);
            w.WriteNumber("ts", _now().ToUnixTimeSeconds());
            w.WriteString("class", cls);
            w.WriteString("push", push);
            w.WriteString("ck", ck);
            w.WriteString("ct", ct);
            if (e is not null) w.WriteString("e", e);
            if (offer is not null) w.WriteString("offer", offer);
        });

        private static string UnlinkFrame(string peer) => Frame(w => { w.WriteString("t", "unlink"); w.WriteString("peer_id", peer); });

        private string SendUnlink(string peer)
        {
            _unlinksInFlight.Enqueue(peer);
            return UnlinkFrame(peer);
        }

        // The oldest unanswered unlink is done.
        private void SettleUnlink()
        {
            if (_unlinksInFlight.Count == 0) return;
            var peer = _unlinksInFlight.Dequeue();
            _retryAttempts.Remove("unlink:" + peer);
            if (_revokeKeys.Remove(peer, out var revokeKey)) _retryable.Remove(revokeKey);
            _identity.RemovePendingUnlink(peer);
        }

        // The relay refused a frame for rate and did not process it (frames-v2,
        // hana's PR #9): resend it, after a backoff the client schedules. ref
        // names the envelope id or the control frame; with no ref (the raw
        // ceiling, refused before parsing) every unanswered frame worth
        // resending goes again. A refused state frame is left alone — the
        // phone resyncs from the gap.
        private void OnRateLimited(string? reference)
        {
            Note($"relay err code=rate_limited ref={Safe(reference)}");
            switch (reference)
            {
                case "unlink":
                    if (_unlinksInFlight.Count > 0) ScheduleRetry("unlink:" + _unlinksInFlight.Dequeue());
                    break;
                case "link":
                    if (_retryable.ContainsKey("link")) ScheduleRetry("link");
                    break;
                case null or "":
                    var unlinks = _unlinksInFlight.Distinct().ToList();
                    _unlinksInFlight.Clear();
                    foreach (var peer in unlinks) ScheduleRetry("unlink:" + peer);
                    foreach (var key in _retryable.Keys.ToList()) ScheduleRetry(key);
                    // CB-323: refused before parsing, so the last snapshot may be
                    // among what the relay never took. Sent again, as of then.
                    RetryActivity();
                    break;
                case var r when RelayActivity.IsActivityRef(r):
                    // Not stored at the relay: the next snapshot goes whatever
                    // it carries, after the debounce.
                    ActivityReplied(r!, "err", "rate_limited");
                    RetryActivity();
                    break;
                default:
                    if (_retryable.ContainsKey("env:" + reference)) ScheduleRetry("env:" + reference);
                    break;
            }
        }

        private void ScheduleRetry(string key)
        {
            if (!_retryPending.Add(key)) return;
            var attempt = _retryAttempts.GetValueOrDefault(key) + 1;
            if (attempt > MaxRetries)
            {
                Note($"giving up on {key} after {MaxRetries} rate-limited attempts");
                _retryPending.Remove(key);
                _retryAttempts.Remove(key);
                _retryable.Remove(key);
                return;
            }
            _retryAttempts[key] = attempt;
            RetryDue(key, attempt);
        }

        // The frame for a retry the client's backoff has now waited out —
        // nothing if the socket went down meanwhile (a welcome resends owed
        // unlinks anyway) or the frame no longer needs sending.
        internal IReadOnlyList<string> Retry(string key)
        {
            _retryPending.Remove(key);
            if (!Welcomed) return [];
            if (key.StartsWith("unlink:", StringComparison.Ordinal))
            {
                var peer = key["unlink:".Length..];
                return _identity.PendingUnlinks.Contains(peer) ? [SendUnlink(peer)] : [];
            }
            return _retryable.TryGetValue(key, out var frame) ? [frame] : [];
        }

        // ---- helpers ----------------------------------------------------------

        // Anything the relay sends that ends up in a log line goes through one
        // of these two (nadia, PR #225). A hostile or buggy relay can put
        // anything in t, an id, a code or a class — a newline that forges a
        // second log line, twenty thousand characters, a payload — and "the
        // log never shows a payload" must not depend on the sender's manners.
        internal static readonly string[] ControlKinds = ["ok", "err", "presence", "resync", "ping", "pong", "link", "unlink", "hello"];
        internal static readonly string[] Classes = ["state", "action", "chat", "pair", "control"];
        internal const int MaxLoggedLength = 64;

        // A value from a fixed vocabulary, or "other".
        internal static string Known(string? value, string[] allowed) =>
            value is not null && Array.IndexOf(allowed, value) >= 0 ? value : "other";

        // An id, ref or code: only the characters those ever use (the relay's
        // own envelope-id rule, letters, digits and _ . : -), at most 64 of
        // them, with "…" saying something was cut. Empty for none.
        internal static string Safe(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var kept = new StringBuilder(Math.Min(value.Length, MaxLoggedLength));
            foreach (var ch in value)
            {
                if (kept.Length == MaxLoggedLength) break;
                if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or ':' or '-') kept.Append(ch);
            }
            return kept.Length == value.Length ? value : kept.Append('…').ToString();
        }

        private static IReadOnlyList<string> Collect(string? frame) => frame is null ? [] : [frame];

        // Relaxed escaping (CB-316). The default encoder writes every `+` as
        // \u002B, and `+` is about one base64 character in 64: a full 48,000-byte
        // window sealed to a 64,000-character ct, and the envelope came out at
        // 69,176 bytes — over the relay's 65,536-byte frame cap, so the relay
        // closed the desk with MessageTooBig on every large backfill, the desk
        // reconnected, the phone re-watched, and the link flapped about once a
        // second (measured on a real 6.7 MB transcript). Relaxed it is 64,181.
        // Everything these frames carry is ASCII ids, base64 and JSON the
        // relay parses, so nothing needs the HTML-safe escapes.
        private static readonly JsonWriterOptions FrameOptions =
            new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // What the relay will take in one frame (relay Limits.MaxFrameBytes).
        internal const int MaxFrameBytes = 64 * 1024;

        private static string Frame(Action<Utf8JsonWriter> body)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, FrameOptions))
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
