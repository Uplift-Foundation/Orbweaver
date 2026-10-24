using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Orbweaver
{
    // This machine's identity to an OpenClaw gateway: an Ed25519 keypair, the
    // device id derived from it, and the signature the gateway demands on every
    // connect.
    //
    // Why a keypair at all, when the gateway also has a token: a bearer token
    // on its own gets you connected with *no operator scopes*, and every read
    // method then refuses with "missing scope: operator.read". Scopes belong to
    // a paired device, and a paired device proves itself by signing the nonce
    // from connect.challenge. Presenting a device token without a signature
    // answers NOT_PAIRED / DEVICE_IDENTITY_REQUIRED. Measured against a real
    // gateway — see docs/openclaw-findings.md, which has the full ladder of
    // attempts and what each one was refused with.
    //
    // The private key never leaves this file, and the file never leaves this
    // machine. Pairing it is a one-time act the user approves on the gateway;
    // after that the device token is what carries the scopes, and this key is
    // what proves we are the device it was issued to.
    internal static class OpenClawIdentity
    {
        // Alongside settings.json rather than in the temp directory: an identity
        // that vanished on reboot would ask the user to re-approve a pairing
        // every time, which is the one part of this feature that can't be made
        // automatic.
        private static string Path_ =>
            System.IO.Path.Combine(OrbweaverSettings.Directory, "openclaw-identity.json");

        private static readonly object Gate = new();
        private static Identity? _cached;

        // Raw 32-byte keys. The wire format is base64url of exactly these bytes,
        // not PEM and not SPKI — the gateway derives the device id by hashing
        // the raw public key, so any envelope around it produces a different id
        // and a device the gateway has never heard of.
        internal sealed record Identity(byte[] PrivateKey, byte[] PublicKey, string DeviceId);

        public static Identity Current()
        {
            lock (Gate)
            {
                if (_cached is not null) return _cached;

                _cached = Load() ?? Create();
                return _cached;
            }
        }

        // Forgets the keypair and the token table, so the next call reads
        // whatever is on disk now.
        //
        // A test seam, and the only one this file needs: everything else here is
        // deterministic given a key and a directory, and the directory already
        // moves with ORBWEAVER_SETTINGS_DIR. What is not is the process-wide
        // cache — the first test to ask for an identity would fix it for every
        // test after it, so "a truncated file falls back to a new key" would
        // pass or fail on the order the runner happened to pick. Narrow on
        // purpose: it clears state, it does not inject any.
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _cached = null;
                Tokens.Clear();
                _tokensLoaded = false;
            }
        }

        // Device tokens, keyed by the gateway that issued them. Kept beside the
        // keypair rather than in settings.json for two reasons: settings are
        // ordinary preferences a user might reasonably copy between machines,
        // and a device token copied to a second machine is a credential whose
        // signature no longer matches the key it was issued against — it would
        // fail in a way that looks like a gateway problem. Keyed by host
        // because one machine can face more than one gateway, and a token from
        // one is meaningless to another.
        //
        // Until CB-278 nothing wrote a device token here, so the table only
        // ever held the `gateway:` entry below. OpenClawGateway.ConnectAsync
        // now stores the one hello-ok issues, and the HTTP media fetches
        // present it (see OpenClawMediaCredential) instead of the shared
        // secret, which the gateway scores as admin.
        private static readonly Dictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase);
        private static bool _tokensLoaded;

        public static string? TokenFor(string host)
        {
            lock (Gate)
            {
                LoadTokens();
                return Tokens.GetValueOrDefault(host);
            }
        }

        // The gateway's own auth token — the one from its `gateway.auth.token`,
        // which every connect has to carry regardless of pairing. Kept here
        // rather than in settings.json because it is a credential and this file
        // is written 0600; settings.json is ordinary preferences and is not.
        // Prefixed so it cannot collide with a device token for a host that
        // happens to be called the same thing.
        public static string? GatewayTokenFor(string host)
        {
            lock (Gate)
            {
                LoadTokens();
                return Tokens.GetValueOrDefault("gateway:" + host);
            }
        }

        public static void SetGatewayTokenFor(string host, string token)
        {
            lock (Gate)
            {
                LoadTokens();

                if (string.IsNullOrEmpty(token)) Tokens.Remove("gateway:" + host);
                else Tokens["gateway:" + host] = token;

                SaveTokens();
            }
        }

        // Called from every successful connect (CB-278), because hello-ok hands
        // the device its token every time and the gateway's ensureDeviceToken
        // reuses the same one while the approved scopes hold. So nearly every
        // call is a no-op, and it is kept one: rewriting a 0600 credential file
        // on each reconnect would put a write, and a window in which the file
        // is half-written, on a path that has nothing new to say.
        public static void SetTokenFor(string host, string token)
        {
            lock (Gate)
            {
                LoadTokens();
                if (Tokens.TryGetValue(host, out var held) && string.Equals(held, token, StringComparison.Ordinal))
                    return;

                Tokens[host] = token;
                SaveTokens();
            }
        }

        private static string TokensPath =>
            System.IO.Path.Combine(OrbweaverSettings.Directory, "openclaw-devices.json");

        private static void LoadTokens()
        {
            if (_tokensLoaded) return;
            _tokensLoaded = true;

            try
            {
                if (!File.Exists(TokensPath)) return;

                var map = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(File.ReadAllText(TokensPath));
                if (map is null) return;

                foreach (var (host, token) in map) Tokens[host] = token;
            }
            catch
            {
                // Same reasoning as the identity file: an unreadable credential
                // store costs a re-approval, not a failure to start.
            }
        }

        private static void SaveTokens()
        {
            try
            {
                Directory.CreateDirectory(OrbweaverSettings.Directory);
                File.WriteAllText(TokensPath, System.Text.Json.JsonSerializer.Serialize(Tokens));

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(TokensPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
            }
        }

        // sha256 of the raw public key, hex. This is the gateway's own
        // derivation (dist/device-identity-*.js), and it's what appears as the
        // 64-character key in its devices table — so computing it the same way
        // is what lets a user match the row they're approving to this machine.
        public static string DeviceIdOf(byte[] publicKey) =>
            Convert.ToHexStringLower(SHA256.HashData(publicKey));

        // The gateway speaks base64url without padding everywhere: public keys,
        // signatures and device tokens all arrive and leave in this shape.
        public static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // The exact string the gateway rebuilds and verifies against, lifted
        // from its own buildDeviceAuthPayloadV3. Field order and the pipe
        // separator are load-bearing: a payload that differs by one character
        // verifies as a forgery, and the error says only "unauthorized", so
        // getting this wrong is expensive to diagnose. Scopes are comma-joined
        // in the order sent, and `token` is the empty string before pairing —
        // both are the gateway's convention, not ours.
        //
        // A v2 form exists (same fields, no platform/deviceFamily). We sign v3
        // because that's what the current gateway builds; if an older one ever
        // rejects it, v2 is a one-line variant rather than a redesign.
        public static string AuthPayload(
            string deviceId,
            string clientId,
            string clientMode,
            string role,
            IEnumerable<string> scopes,
            long signedAtMs,
            string? token,
            string nonce,
            string platform,
            string deviceFamily) =>
            string.Join('|', new[]
            {
                "v3",
                deviceId,
                clientId,
                clientMode,
                role,
                string.Join(',', scopes),
                signedAtMs.ToString(),
                token ?? "",
                nonce,
                platform,
                deviceFamily
            });

        // Ed25519ph (prehashed) is a different algorithm and verifies as a
        // forgery here; the gateway signs with node's crypto.sign(null, …),
        // which is plain Ed25519 over the whole message. Signer(0) is that.
        public static string Sign(Identity identity, string payload)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(identity.PrivateKey));

            var bytes = Encoding.UTF8.GetBytes(payload);
            signer.BlockUpdate(bytes, 0, bytes.Length);

            return Base64Url(signer.GenerateSignature());
        }

        private static Identity Create()
        {
            var random = new SecureRandom();
            var priv = new Ed25519PrivateKeyParameters(random);
            var pub = priv.GeneratePublicKey();

            var identity = new Identity(
                priv.GetEncoded(),
                pub.GetEncoded(),
                DeviceIdOf(pub.GetEncoded()));

            Save(identity);
            return identity;
        }

        private static Identity? Load()
        {
            try
            {
                if (!File.Exists(Path_)) return null;

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path_));
                var root = doc.RootElement;

                var priv = FromBase64Url(root.GetProperty("privateKey").GetString());
                var pub = FromBase64Url(root.GetProperty("publicKey").GetString());

                if (priv is null || pub is null || priv.Length != 32 || pub.Length != 32) return null;

                // Recomputed rather than read back: the file's own deviceId is a
                // convenience for anyone reading it, and trusting it would let a
                // hand-edited or half-written file point us at a device whose
                // key we don't hold.
                return new Identity(priv, pub, DeviceIdOf(pub));
            }
            catch
            {
                // Unreadable, truncated, or written by something else. Falling
                // through to Create() costs one re-approval, which is a better
                // outcome than refusing to start.
                return null;
            }
        }

        private static void Save(Identity identity)
        {
            try
            {
                Directory.CreateDirectory(OrbweaverSettings.Directory);

                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    privateKey = Base64Url(identity.PrivateKey),
                    publicKey = Base64Url(identity.PublicKey),
                    deviceId = identity.DeviceId
                });

                File.WriteAllText(Path_, json);

                // This is a private key. Settings live in the same directory and
                // are ordinary user data, so the directory's own permissions are
                // not enough of a statement — say it about this file explicitly.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // An identity that can't be persisted still works for this run;
                // the cost is re-approving the pairing next launch. Failing the
                // connection outright over it would be worse.
            }
        }

        private static byte[]? FromBase64Url(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);

            try { return Convert.FromBase64String(normalized); }
            catch { return null; }
        }
    }
}
