using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // This desktop's identity at the phone relay, and its memory of the phones
    // paired with it (CB-314 plan §4). Kept in relay-identity.json beside
    // peer-identity.json and settings.json, with the private keys and the
    // token held by an IRelaySecretStore — the Keychain on macOS, DPAPI on
    // Windows — and only a reference to each in the file.
    //
    // What it remembers about each phone is exactly what the session needs and
    // nothing the phone did not send: its id, the name it gave, its two public
    // keys, when it paired, and this desktop's send epoch toward it. The epoch
    // is the nonce-reuse guard (see RelaySendCounter): it is written before any
    // frame goes out under a new one, which is why it lives here rather than
    // in memory.
    //
    // Every method takes the one lock; nothing here does network IO. Keychain
    // calls can in principle wait on a consent prompt, so callers keep this off
    // the UI thread — RelayClient loads it on its own task.
    internal sealed class RelayIdentity
    {
        internal const string FileName = "relay-identity.json";

        internal sealed record Phone(
            string PhoneId,
            string Name,
            byte[] IdPublic,
            byte[] KaPublic,
            DateTimeOffset PairedAt,
            ushort SendEpoch,
            bool SendExhausted,
            (ushort Epoch, ulong Counter)? Received);

        private sealed class StoredPhone
        {
            [JsonPropertyName("phone_id")] public string PhoneId { get; set; } = "";
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("id_pub")] public string IdPublic { get; set; } = "";
            [JsonPropertyName("ka_pub")] public string KaPublic { get; set; } = "";
            [JsonPropertyName("paired_at")] public DateTimeOffset PairedAt { get; set; }
            [JsonPropertyName("send_epoch")] public ushort SendEpoch { get; set; }
            [JsonPropertyName("send_exhausted")] public bool SendExhausted { get; set; }
            [JsonPropertyName("recv_epoch")] public ushort? ReceiveEpoch { get; set; }
            [JsonPropertyName("recv_counter")] public ulong? ReceiveCounter { get; set; }
        }

        private sealed class Stored
        {
            [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
            [JsonPropertyName("token")] public string? Token { get; set; }
            [JsonPropertyName("id_pub")] public string? IdPublic { get; set; }
            [JsonPropertyName("id_priv")] public string? IdPrivate { get; set; }
            [JsonPropertyName("ka_pub")] public string? KaPublic { get; set; }
            [JsonPropertyName("ka_priv")] public string? KaPrivate { get; set; }
            [JsonPropertyName("phones")] public List<StoredPhone> Phones { get; set; } = new();
        }

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly Lazy<RelayIdentity> SharedInstance =
            new(() => new RelayIdentity(OrbweaverSettings.Directory, RelaySecrets.ForThisPlatform()));

        internal static RelayIdentity Shared => SharedInstance.Value;

        private readonly object _gate = new();
        private readonly string _directory;
        private readonly IRelaySecretStore _store;
        private readonly Func<DateTimeOffset> _now;
        private Stored _stored = new();
        private RelayCrypto.KeyPair? _identity;
        private RelayCrypto.KeyPair? _agreement;
        private string? _token;
        private bool _loaded;

        internal RelayIdentity(string directory, IRelaySecretStore store, Func<DateTimeOffset>? now = null)
        {
            _directory = directory;
            _store = store;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        internal string FilePath => Path.Combine(_directory, FileName);

        // ---- this desktop ------------------------------------------------------

        // The identity and agreement key pairs, created on first use. If the
        // stored private keys cannot be opened — the Keychain item was deleted,
        // the file was copied from another user — new ones are made, and the
        // registration and every pairing go with them: a phone checks this
        // desktop by the keys it scanned, so pairings to keys that no longer
        // exist cannot be kept honestly. Logged, because the user will have to
        // pair their phone again.
        internal (RelayCrypto.KeyPair Identity, RelayCrypto.KeyPair Agreement) Keys()
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (_identity is not null && _agreement is not null) return (_identity, _agreement);

                var hadKeys = _stored.IdPrivate is not null || _stored.KaPrivate is not null;
                if (hadKeys)
                    MirrorLog.Say("relay-identity", "stored keys could not be opened; making new ones and forgetting pairings");

                ForgetSecret(_stored.IdPrivate);
                ForgetSecret(_stored.KaPrivate);
                ForgetSecret(_stored.Token);

                _identity = RelayCrypto.GenerateKeyPair();
                _agreement = RelayCrypto.GenerateKeyPair();
                _stored = new Stored
                {
                    IdPublic = RelayCrypto.Hex(_identity.Public),
                    IdPrivate = Protect("identity", _identity.Private),
                    KaPublic = RelayCrypto.Hex(_agreement.Public),
                    KaPrivate = Protect("agreement", _agreement.Private),
                };
                _token = null;
                Save();
                return (_identity, _agreement);
            }
        }

        internal string? DeviceId
        {
            get { lock (_gate) { EnsureLoaded(); return _stored.DeviceId; } }
        }

        internal string? Token
        {
            get { lock (_gate) { EnsureLoaded(); return _token; } }
        }

        // What the relay handed back from /v1/register.
        internal void SetRegistration(string deviceId, string token)
        {
            lock (_gate)
            {
                EnsureLoaded();
                ForgetSecret(_stored.Token);
                _stored.DeviceId = deviceId;
                _stored.Token = Protect("token", System.Text.Encoding.UTF8.GetBytes(token));
                _token = token;
                Save();
            }
        }

        // The relay refused the token (unauthorized): forget it so the next
        // connect re-registers with the same identity key, which the relay
        // answers with the same device id and a fresh token.
        internal void ClearToken()
        {
            lock (_gate)
            {
                EnsureLoaded();
                ForgetSecret(_stored.Token);
                _stored.Token = null;
                _token = null;
                Save();
            }
        }

        // ---- phones ------------------------------------------------------------

        internal IReadOnlyList<Phone> Phones
        {
            get { lock (_gate) { EnsureLoaded(); return _stored.Phones.Select(ToPhone).ToList(); } }
        }

        internal Phone? PhoneById(string phoneId)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var p = _stored.Phones.FirstOrDefault(x => x.PhoneId == phoneId);
                return p is null ? null : ToPhone(p);
            }
        }

        // A phone the owner just allowed. Pairing the same phone again replaces
        // its record and starts its epoch over — new keys, new session base, so
        // no earlier nonce can collide.
        internal Phone AddPhone(string phoneId, string name, byte[] idPublic, byte[] kaPublic)
        {
            lock (_gate)
            {
                EnsureLoaded();
                _stored.Phones.RemoveAll(p => p.PhoneId == phoneId);
                var stored = new StoredPhone
                {
                    PhoneId = phoneId,
                    Name = name,
                    IdPublic = RelayCrypto.Hex(idPublic),
                    KaPublic = RelayCrypto.Hex(kaPublic),
                    PairedAt = _now(),
                };
                _stored.Phones.Add(stored);
                Save();
                return ToPhone(stored);
            }
        }

        internal bool RemovePhone(string phoneId)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var removed = _stored.Phones.RemoveAll(p => p.PhoneId == phoneId) > 0;
                if (removed) Save();
                return removed;
            }
        }

        // Written before sending under a new epoch. Returns false if the file
        // could not be written — the caller must not send, because a restart
        // would then reuse the epoch it is about to use.
        internal bool SaveSendEpoch(string phoneId, ushort epoch, bool exhausted)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var p = _stored.Phones.FirstOrDefault(x => x.PhoneId == phoneId);
                if (p is null) return false;
                p.SendEpoch = epoch;
                p.SendExhausted = exhausted;
                return Save();
            }
        }

        internal void SaveReceive(string phoneId, ushort epoch, ulong counter)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var p = _stored.Phones.FirstOrDefault(x => x.PhoneId == phoneId);
                if (p is null) return;
                p.ReceiveEpoch = epoch;
                p.ReceiveCounter = counter;
                Save();
            }
        }

        // ---- storage -----------------------------------------------------------

        private static Phone ToPhone(StoredPhone p) => new(
            p.PhoneId, p.Name,
            // Always hex this class wrote, and EnsureLoaded drops any phone
            // whose keys do not parse, so neither can be null here.
            RelayCrypto.FromHex(p.IdPublic)!, RelayCrypto.FromHex(p.KaPublic)!,
            p.PairedAt, p.SendEpoch, p.SendExhausted,
            // Both or nothing: a hand-edited file with only one half has no
            // position anyone can trust, so it reads as none.
            p.ReceiveEpoch is { } e && p.ReceiveCounter is { } c ? (e, c) : null);

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            Stored? stored = null;
            try
            {
                if (File.Exists(FilePath))
                    stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A corrupt identity is replaced rather than fatal, as
                // PeerIdentity does. The cost is pairing the phone again.
                MirrorLog.Say("relay-identity", $"could not read {FileName}: {ex.GetType().Name}");
            }
            _stored = stored ?? new Stored();
            _stored.Phones ??= new();
            // A phone whose keys do not parse cannot be talked to; drop it
            // rather than carry a record that fails every frame.
            _stored.Phones.RemoveAll(p => !RelayCrypto.IsValidPublicKey(RelayCrypto.FromHex(p.IdPublic))
                                          || !RelayCrypto.IsValidPublicKey(RelayCrypto.FromHex(p.KaPublic)));

            _identity = OpenPair(_stored.IdPrivate, _stored.IdPublic);
            _agreement = OpenPair(_stored.KaPrivate, _stored.KaPublic);
            if (_identity is not null && _agreement is not null && _stored.Token is { } token
                && Unprotect(token) is { } bytes)
                _token = System.Text.Encoding.UTF8.GetString(bytes);
        }

        private RelayCrypto.KeyPair? OpenPair(string? privateRef, string? publicHex)
        {
            if (privateRef is null || RelayCrypto.FromHex(publicHex) is not { } pub) return null;
            if (Unprotect(privateRef) is not { } priv) return null;
            // The private key must be the one the public key in the file says;
            // a mismatch is a copied or half-written file.
            try
            {
                return RelayCrypto.KeyPairFromPrivate(priv).Public.AsSpan().SequenceEqual(pub)
                    ? new RelayCrypto.KeyPair(priv, pub)
                    : null;
            }
            // A scalar that is not a valid key: CryptographicException on
            // macOS, PlatformNotSupportedException from CNG on Windows.
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        // The platform store first, then the plain file — said in the log, so
        // a degraded install is a line someone can find rather than a silence.
        private string Protect(string name, byte[] secret)
        {
            if (_store.Protect(name, secret) is { } reference) return reference;
            MirrorLog.Say("relay-identity", $"secret store refused {name}; keeping it in {FileName}, owner-only (degraded)");
            return PlainSecretStore.Instance.Protect(name, secret)!;
        }

        private byte[]? Unprotect(string reference) =>
            reference.StartsWith(RelaySecrets.PlainPrefix, StringComparison.Ordinal)
                ? PlainSecretStore.Instance.Unprotect(reference)
                : _store.Unprotect(reference);

        private void ForgetSecret(string? reference)
        {
            if (reference is not null) _store.Forget(reference);
        }

        private bool Save()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_stored, Json));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MirrorLog.Say("relay-identity", $"could not write {FileName}: {ex.GetType().Name}");
                return false;
            }
        }
    }
}
