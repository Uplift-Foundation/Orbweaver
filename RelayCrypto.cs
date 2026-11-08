using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // The phone relay's cryptography, byte for byte as CB-314's architect plan
    // (§2.2 and §2.3) writes it down. Pure: no keys on disk, no sockets, no
    // settings — RelayIdentity owns where keys live and RelayClient owns when
    // they are used. That is what lets tests/UnitTests pin every function here
    // against the contract vectors in Orbweaver-Mobile `contracts/`, which this
    // file also generates (tools/relay-vectors). The iPhone's CryptoKit code
    // mirrors it function for function, and the vectors are the only thing
    // that keeps the two honest: a byte that differs here does not fail with an
    // error, it fails as "the phone shows nothing".
    //
    // Everything is P-256, HKDF-SHA256 and IETF ChaCha20-Poly1305 because those
    // are native on CryptoKit, the Secure Enclave and .NET on both desktops —
    // measured in milestone 1, which is why the spec dropped X25519/Ed25519.
    // No third-party crypto library is involved.
    //
    // The relay itself never calls any of this. It is blind by construction:
    // it carries `ct` and routes on the envelope.
    internal static class RelayCrypto
    {
        internal const int KeyLength = 32;
        internal const int PublicKeyLength = 65;   // X9.63 uncompressed: 0x04 ‖ X ‖ Y
        internal const int NonceLength = 12;
        internal const int TagLength = 16;
        internal const int SignatureLength = 64;   // raw r ‖ s, as CryptoKit's rawRepresentation

        // The nonce's 4-byte direction prefix. Two directions under one key
        // with independent counters would collide without it.
        internal const uint DesktopToPhone = 0;
        internal const uint PhoneToDesktop = 1;

        // The counter half of the nonce is 48 bits (§2.3: epoch ‖ counter).
        internal const ulong MaxCounter = (1UL << 48) - 1;

        // Message counts and age at which a sender moves to the next epoch —
        // the spec's "rotates every 24 hours, or after 10,000 messages".
        internal const ulong MessagesPerEpoch = 10_000;
        internal static readonly TimeSpan EpochLifetime = TimeSpan.FromHours(24);

        private static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;

        // ---- keys -------------------------------------------------------

        internal sealed record KeyPair(byte[] Private, byte[] Public);

        internal static KeyPair GenerateKeyPair()
        {
            using var key = ECDiffieHellman.Create(Curve);
            return ToPair(key.ExportParameters(includePrivateParameters: true));
        }

        // A key pair from a fixed 32-byte scalar. Only the vector generator and
        // tests use it — a real key always comes from GenerateKeyPair.
        internal static KeyPair KeyPairFromPrivate(byte[] scalar)
        {
            using var key = ECDiffieHellman.Create(new ECParameters { Curve = Curve, D = scalar });
            return ToPair(key.ExportParameters(includePrivateParameters: true));
        }

        private static KeyPair ToPair(ECParameters p) => new(p.D!, EncodePoint(p.Q));

        private static byte[] EncodePoint(ECPoint q)
        {
            var pub = new byte[PublicKeyLength];
            pub[0] = 0x04;
            q.X!.CopyTo(pub, 1);
            q.Y!.CopyTo(pub, 33);
            return pub;
        }

        private static ECPoint DecodePoint(byte[] pub)
        {
            if (pub.Length != PublicKeyLength || pub[0] != 0x04)
                throw new CryptographicException("not an uncompressed P-256 public key");
            return new ECPoint { X = pub[1..33], Y = pub[33..65] };
        }

        // True when `pub` is a well-formed point on P-256. A phone's keys come
        // off the network inside pair.request, so they are checked before
        // anything is stored or signed over.
        internal static bool IsValidPublicKey(byte[]? pub)
        {
            if (pub is null) return false;
            try
            {
                using var key = ECDiffieHellman.Create(new ECParameters { Curve = Curve, Q = DecodePoint(pub) });
                return true;
            }
            // Windows CNG refuses an off-curve point with PlatformNotSupported
            // ("curve not valid for this platform") where macOS throws
            // CryptographicException — measured on CI's windows leg.
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        // ECDH, raw x-coordinate — DeriveRawSecretAgreement on .NET,
        // sharedSecretFromKeyAgreement on CryptoKit (measured equal in M1).
        internal static byte[] SharedSecret(KeyPair mine, byte[] theirPublic)
        {
            using var me = ECDiffieHellman.Create(new ECParameters { Curve = Curve, D = mine.Private, Q = DecodePoint(mine.Public) });
            using var them = ECDiffieHellman.Create(new ECParameters { Curve = Curve, Q = DecodePoint(theirPublic) });
            return me.DeriveRawSecretAgreement(them.PublicKey);
        }

        internal static byte[] Sign(KeyPair signer, byte[] message)
        {
            using var key = ECDsa.Create(new ECParameters { Curve = Curve, D = signer.Private, Q = DecodePoint(signer.Public) });
            return key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        internal static bool Verify(byte[] publicKey, byte[] message, byte[]? signature)
        {
            if (signature is null || signature.Length != SignatureLength || !IsValidPublicKey(publicKey)) return false;
            using var key = ECDsa.Create(new ECParameters { Curve = Curve, Q = DecodePoint(publicKey) });
            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        // ---- encodings --------------------------------------------------

        internal static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static byte[]? FromBase64Url(string? text)
        {
            if (text is null) return null;
            var s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }

        internal static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

        internal static byte[]? FromHex(string? hex)
        {
            if (hex is null) return null;
            try { return Convert.FromHexString(hex); }
            catch (FormatException) { return null; }
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        private static byte[] Concat(params byte[][] parts)
        {
            var all = new byte[parts.Sum(p => p.Length)];
            var at = 0;
            foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
            return all;
        }

        // ---- registration (§2.1) ---------------------------------------

        // What the identity key signs to prove to the relay it holds it:
        // the prefix then the RAW 32 challenge bytes — the relay's base64url
        // string decoded, not its text (CB-314 addendum 1).
        internal static byte[] RegisterMessage(byte[] challenge) =>
            Concat(Utf8("orbweaver-register-v1"), challenge);

        // ---- pairing (§2.2) ---------------------------------------------

        internal static string PairQrText(string relayHost, string desktopId, string offerId,
            byte[] idPublic, byte[] kaPublic, byte[] pairSecret) =>
            $"orbweaver://pair?r={relayHost}&d={desktopId}&o={offerId}" +
            $"&k={Base64Url(Concat(idPublic, kaPublic))}&s={Base64Url(pairSecret)}";

        internal static byte[] PairKey(byte[] pairSecret, string offerId, string desktopId, string phoneId) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, pairSecret, KeyLength,
                salt: Utf8(offerId), info: Concat(Utf8("orbweaver-pair-v1"), Utf8(desktopId), Utf8(phoneId)));

        // The six digits both screens show. Derived from all four public keys,
        // so a hostile relay that swapped any key changes the number.
        internal static int CheckCode(byte[] desktopIdPublic, byte[] desktopKaPublic, byte[] phoneIdPublic, byte[] phoneKaPublic)
        {
            var digest = SHA256.HashData(Concat(Utf8("orbweaver-check-v1"),
                desktopIdPublic, desktopKaPublic, phoneIdPublic, phoneKaPublic));
            return (int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000);
        }

        internal static string CheckCodeDigits(int code) => code.ToString("D6");

        // "482 913": the spacing the pairing window shows, easier to compare
        // aloud than six run-together digits.
        internal static string CheckCodeDisplay(int code)
        {
            var d = CheckCodeDigits(code);
            return d[..3] + " " + d[3..];
        }

        internal static byte[] AcceptMessage(string desktopId, string phoneId,
            byte[] desktopIdPublic, byte[] desktopKaPublic, byte[] phoneIdPublic, byte[] phoneKaPublic) =>
            Concat(Utf8("orbweaver-pair-accept-v1"), Utf8(desktopId), Utf8(phoneId),
                desktopIdPublic, desktopKaPublic, phoneIdPublic, phoneKaPublic);

        internal static byte[] RevokeMessage(string by, string peerId) =>
            Concat(Utf8("orbweaver-pair-revoke-v1"), Utf8(by), Utf8(peerId));

        // ---- session keys (§2.3) ----------------------------------------

        internal static byte[] SessionBase(byte[] sharedSecret, string desktopId, string phoneId) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, KeyLength,
                salt: [], info: Concat(Utf8("orbweaver-session-v1"), Utf8(desktopId), Utf8(phoneId)));

        internal static byte[] EpochKey(byte[] sessionBase, ushort epoch)
        {
            var salt = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(salt, epoch);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionBase, KeyLength, salt: salt, info: Utf8("orbweaver-epoch-v1"));
        }

        internal static byte[] CoalesceKey(byte[] sessionBase) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionBase, KeyLength, salt: [], info: Utf8("orbweaver-ck-v1"));

        // The envelope's `ck`: lets the relay coalesce state frames for one
        // session without learning which session. Stable across epochs.
        internal static string Ck(byte[] coalesceKey, string sessionId) =>
            Base64Url(HMACSHA256.HashData(coalesceKey, Utf8(sessionId))[..16]);

        // ---- nonce, AAD, seal -------------------------------------------

        internal static byte[] Nonce(uint direction, ushort epoch, ulong counter)
        {
            if (counter > MaxCounter) throw new ArgumentOutOfRangeException(nameof(counter));
            var n = new byte[NonceLength];
            BinaryPrimitives.WriteUInt32BigEndian(n, direction);
            BinaryPrimitives.WriteUInt16BigEndian(n.AsSpan(4), epoch);
            // 48 bits: the low six bytes of a big-endian 64-bit write.
            Span<byte> c = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(c, counter);
            c[2..].CopyTo(n.AsSpan(6));
            return n;
        }

        internal readonly record struct NonceParts(uint Direction, ushort Epoch, ulong Counter);

        internal static NonceParts ParseNonce(ReadOnlySpan<byte> nonce)
        {
            if (nonce.Length != NonceLength) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
            Span<byte> c = stackalloc byte[8];
            nonce[6..].CopyTo(c[2..]);
            return new(BinaryPrimitives.ReadUInt32BigEndian(nonce),
                BinaryPrimitives.ReadUInt16BigEndian(nonce[4..]),
                BinaryPrimitives.ReadUInt64BigEndian(c));
        }

        // Binds the ciphertext to its routing: a frame re-addressed by a
        // hostile relay no longer opens. The separator is unescaped, so
        // ("a|b","c") and ("a","b|c") would give the same bytes; no device id
        // or class ever contains '|', so one that does is refused outright
        // rather than letting two different routings share an AAD. The bytes
        // for every legal input are unchanged, so the vectors stand.
        internal static byte[] Aad(string from, string to, string cls)
        {
            if (from.Contains('|') || to.Contains('|') || cls.Contains('|'))
                throw new ArgumentException("an AAD component contains '|'");
            return Utf8($"{from}|{to}|{cls}");
        }

        // base64(nonce ‖ ciphertext ‖ tag) — CryptoKit's SealedBox.combined.
        internal static string Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
        {
            var box = new byte[NonceLength + plaintext.Length + TagLength];
            nonce.CopyTo(box, 0);
            using var aead = new ChaCha20Poly1305(key);
            aead.Encrypt(nonce, plaintext,
                box.AsSpan(NonceLength, plaintext.Length), box.AsSpan(NonceLength + plaintext.Length), aad);
            return Convert.ToBase64String(box);
        }

        // The nonce of a sealed box without opening it — the receiver needs the
        // epoch out of it to know which key to open with. Null for anything
        // that is not a box at all.
        internal static NonceParts? PeekNonce(string? ct)
        {
            var box = DecodeBox(ct);
            return box is null ? null : ParseNonce(box.AsSpan(0, NonceLength));
        }

        // Null when `ct` is malformed, truncated, carries the other
        // direction's nonce prefix, or fails authentication — never an
        // exception, because every one of those arrives off the network and
        // none is the desktop's fault. The direction check is defence in
        // depth: a box sealed for the other direction under the same key could
        // otherwise be reflected back at its sender.
        internal static byte[]? Open(byte[] key, string? ct, byte[] aad, uint expectedDirection)
        {
            var box = DecodeBox(ct);
            if (box is null) return null;
            if (ParseNonce(box.AsSpan(0, NonceLength)).Direction != expectedDirection) return null;
            var length = box.Length - NonceLength - TagLength;
            var plaintext = new byte[length];
            try
            {
                using var aead = new ChaCha20Poly1305(key);
                aead.Decrypt(box.AsSpan(0, NonceLength), box.AsSpan(NonceLength, length),
                    box.AsSpan(NonceLength + length), plaintext, aad);
                return plaintext;
            }
            catch (AuthenticationTagMismatchException)
            {
                return null;
            }
        }

        private static byte[]? DecodeBox(string? ct)
        {
            if (ct is null) return null;
            byte[] box;
            try { box = Convert.FromBase64String(ct); }
            catch (FormatException) { return null; }
            return box.Length < NonceLength + TagLength ? null : box;
        }
    }

    // One direction's send position for one pairing: which epoch, which
    // counter. Persisted per phone by RelayIdentity, so it has to be cheap to
    // round-trip and must never hand out the same (epoch, counter) twice under
    // one key — that is a nonce reuse, and ChaCha20-Poly1305 does not survive
    // one.
    //
    // §2.3's rule: bump the epoch and restart the counter at 10,000 messages,
    // after 24 hours, and on every process start. The last is what makes a
    // lost counter harmless — the next run is in a fresh epoch whatever the
    // file said. The caller persists after Start and after every rotation,
    // before sending under the new epoch.
    internal sealed class RelaySendCounter
    {
        internal ushort Epoch { get; private set; }
        internal ulong Counter { get; private set; }
        internal DateTimeOffset EpochStarted { get; private set; }

        // True once this pairing has used its last epoch and must re-pair.
        internal bool Exhausted { get; private set; }

        internal RelaySendCounter(ushort epoch, ulong counter, DateTimeOffset epochStarted, bool exhausted = false)
        {
            Epoch = epoch;
            Counter = counter;
            EpochStarted = epochStarted;
            Exhausted = exhausted;
        }

        // A brand-new pairing: epoch 0, nothing sent.
        internal static RelaySendCounter Fresh(DateTimeOffset now) => new(0, 0, now);

        // A process has started with this counter loaded from disk.
        internal void Start(DateTimeOffset now) => Rotate(now);

        // The (epoch, counter) for the next frame, rotating first if due.
        // Returns false and sends nothing once the epochs have run out.
        // `rotated` says the caller must persist before sending.
        internal bool TryNext(DateTimeOffset now, out ushort epoch, out ulong counter, out bool rotated)
        {
            rotated = false;
            if (!Exhausted && (Counter >= RelayCrypto.MessagesPerEpoch || now - EpochStarted >= RelayCrypto.EpochLifetime))
            {
                Rotate(now);
                rotated = true;
            }
            epoch = Epoch;
            counter = Counter;
            if (Exhausted) return false;
            Counter++;
            return true;
        }

        private void Rotate(DateTimeOffset now)
        {
            if (Epoch == ushort.MaxValue) { Exhausted = true; return; }
            Epoch++;
            Counter = 0;
            EpochStarted = now;
        }
    }

    // The receiving side of one direction: the (epoch, counter) of the last
    // frame accepted. Anything not strictly greater is a replay or a
    // reorder and is refused (spec: "the receiver rejects a replayed or
    // reordered frame").
    internal sealed class RelayReceiveWindow
    {
        internal bool Any { get; private set; }
        internal ushort Epoch { get; private set; }
        internal ulong Counter { get; private set; }

        internal RelayReceiveWindow() { }

        internal RelayReceiveWindow(ushort epoch, ulong counter)
        {
            Any = true;
            Epoch = epoch;
            Counter = counter;
        }

        internal bool WouldAccept(ushort epoch, ulong counter) =>
            !Any || epoch > Epoch || (epoch == Epoch && counter > Counter);

        // Records a frame that has already opened. Call only after Open
        // succeeded — a forged nonce must never move the window.
        internal void Accept(ushort epoch, ulong counter)
        {
            Any = true;
            Epoch = epoch;
            Counter = counter;
        }
    }
}
