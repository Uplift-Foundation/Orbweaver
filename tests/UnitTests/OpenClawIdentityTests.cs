using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// This machine's identity to an OpenClaw gateway.
//
// Worth testing rather than trusting, and the reason is in the source's own
// comments: every mistake this file can make is refused by the gateway with the
// single word "unauthorized". A payload field in the wrong order, a base64
// alphabet with the wrong two characters, a key wrapped in an envelope, or
// Ed25519ph instead of Ed25519 all verify as a forgery and all report the same
// thing — so there is nothing to debug from and the only defence is pinning the
// shape here, where a difference of one character is visible.
//
// Nothing here talks to a gateway. Every value below is either derived from a
// fixed key or read back from a file this test wrote, which is the whole of what
// this file does.
// The shared "Settings" collection rather than a private one: this file's own
// comment gives the reason — its files live at OrbweaverSettings.Directory,
// one directory for the whole assembly — and that is exactly what every other
// settings-touching class in here contends for too. Serialising against all of
// them is strictly stronger than serialising against itself.
[Collection("Settings")]
public class OpenClawIdentityTests
{
    // OpenClawIdentity caches the keypair and the token table for the process,
    // and its files live at OrbweaverSettings.Directory — one directory for
    // the whole assembly. Two of these running at once would read each other's
    // identity file, so they share a collection and xUnit runs them one at a
    // time. ResetForTests exists for the same reason; see its comment.

    // A fresh settings directory for one test, with the caches cleared either
    // side of it. Restoring the variable matters: everything else in this
    // assembly reads settings through the same property.
    private static void InIsolation(Action<string> body)
    {
        var previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var scratch = Path.Combine(Path.GetTempPath(), "cb-identity-" + Guid.NewGuid());

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", scratch);
        OpenClawIdentity.ResetForTests();

        try
        {
            body(scratch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", previous);
            OpenClawIdentity.ResetForTests();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    private static string IdentityFile(string dir) => Path.Combine(dir, "openclaw-identity.json");

    private static string DeviceFile(string dir) => Path.Combine(dir, "openclaw-devices.json");

    // The gateway derives the device id by hashing the *raw* public key, so an
    // envelope of any kind produces an id for a device it has never heard of.
    // Pinned against a hash computed here from the same 32 bytes rather than
    // against a literal, so this says "sha256 of exactly these bytes" instead of
    // "whatever it said last time".
    [Fact]
    public void DeviceIdIsTheSha256OfTheRawPublicKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)i;

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(key)),
            OpenClawIdentity.DeviceIdOf(key));

        // 64 hex characters is what appears in the gateway's devices table, and
        // matching the row being approved to this machine is the only way a user
        // can tell what they are trusting.
        Assert.Equal(64, OpenClawIdentity.DeviceIdOf(key).Length);
    }

    // base64url without padding, everywhere: keys, signatures and device tokens
    // all leave in this shape. The two substitutions are the entire difference
    // from base64 and are silent when wrong — a '+' where a '-' belongs decodes
    // to different bytes on the far end and verifies as a forgery.
    [Fact]
    public void Base64UrlUsesTheUrlAlphabetAndNoPadding()
    {
        // Chosen so ordinary base64 produces both a '+' and a '/', which is
        // otherwise easy to miss: most short inputs produce neither.
        var bytes = new byte[] { 0xFB, 0xEF, 0xBE, 0xFF, 0xFF };
        var plain = Convert.ToBase64String(bytes);

        Assert.Contains('+', plain);
        Assert.Contains('/', plain);

        var url = OpenClawIdentity.Base64Url(bytes);

        Assert.DoesNotContain('+', url);
        Assert.DoesNotContain('/', url);
        Assert.DoesNotContain('=', url);
        Assert.Equal(plain.TrimEnd('=').Replace('+', '-').Replace('/', '_'), url);
    }

    // The exact string the gateway rebuilds and verifies against. Field order
    // and the pipe separator are load-bearing, so this asserts the whole thing
    // as one string rather than checking that the parts are present somewhere.
    [Fact]
    public void AuthPayloadIsV3FieldsPipeJoinedInOrder()
    {
        var payload = OpenClawIdentity.AuthPayload(
            "deadbeef", "gateway-client", "ui", "operator",
            new[] { "operator.read", "operator.write" },
            1_700_000_000_000, "tok", "nonce-1", "macos", "");

        Assert.Equal(
            "v3|deadbeef|gateway-client|ui|operator|operator.read,operator.write"
            + "|1700000000000|tok|nonce-1|macos|",
            payload);
    }

    // `token` is the empty string before pairing rather than absent — the
    // gateway's convention, not ours, and a payload that omits the field
    // entirely has ten segments where it rebuilds eleven.
    [Fact]
    public void AuthPayloadKeepsAnEmptySegmentForAMissingToken()
    {
        var payload = OpenClawIdentity.AuthPayload(
            "id", "client", "ui", "operator", Array.Empty<string>(),
            1, null, "nonce", "windows", "family");

        Assert.Equal("v3|id|client|ui|operator||1||nonce|windows|family", payload);
        Assert.Equal(11, payload.Split('|').Length);
    }

    // Plain Ed25519 over the whole message, not Ed25519ph. The two are different
    // algorithms and the prehashed one verifies as a forgery here, which is
    // indistinguishable from a broken key from the gateway's side — so this
    // verifies the signature with an independent verifier rather than comparing
    // it to a recorded string.
    [Fact]
    public void SignProducesASignatureThePublicKeyVerifies()
    {
        InIsolation(_ =>
        {
            var identity = OpenClawIdentity.Current();
            const string payload = "v3|a|b|c|d||1||nonce|macos|";

            var signature = OpenClawIdentity.Sign(identity, payload);

            var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            verifier.Init(false, new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(
                identity.PublicKey));

            var bytes = Encoding.UTF8.GetBytes(payload);
            verifier.BlockUpdate(bytes, 0, bytes.Length);

            Assert.True(verifier.VerifySignature(FromBase64Url(signature)));

            // And it is base64url, like everything else on this wire.
            Assert.DoesNotContain('+', signature);
            Assert.DoesNotContain('=', signature);
        });
    }

    // One character different in the payload is a different signature. Stated
    // because the gateway cannot tell you this — it reports "unauthorized" for
    // both — so the property has to be pinned where it is visible.
    [Fact]
    public void ADifferentPayloadSignsDifferently()
    {
        InIsolation(_ =>
        {
            var identity = OpenClawIdentity.Current();

            Assert.NotEqual(
                OpenClawIdentity.Sign(identity, "v3|a|b|c|d||1||nonce|macos|"),
                OpenClawIdentity.Sign(identity, "v3|a|b|c|d||1||nonce|macos|x"));
        });
    }

    // The identity is written on first use and read back afterwards. An identity
    // that vanished between launches would ask the user to re-approve a pairing
    // every time, which is the one part of this feature that cannot be made
    // automatic.
    [Fact]
    public void AnIdentityIsCreatedOnceAndReadBackAfterwards()
    {
        InIsolation(dir =>
        {
            var first = OpenClawIdentity.Current();

            Assert.Equal(32, first.PrivateKey.Length);
            Assert.Equal(32, first.PublicKey.Length);
            Assert.True(File.Exists(IdentityFile(dir)));

            // Same process, cached.
            Assert.Same(first, OpenClawIdentity.Current());

            // A different process, reading the same file: the same keypair and
            // the same device id, which is what makes the pairing survive.
            OpenClawIdentity.ResetForTests();
            var reloaded = OpenClawIdentity.Current();

            Assert.NotSame(first, reloaded);
            Assert.Equal(first.PrivateKey, reloaded.PrivateKey);
            Assert.Equal(first.DeviceId, reloaded.DeviceId);
        });
    }

    // The private key is not group- or world-readable. Settings live in the same
    // directory and are ordinary user data, so the directory's own permissions
    // are not a statement about this file.
    [Fact]
    public void TheIdentityFileIsWrittenOwnerOnly()
    {
        // Read inside the isolation block and asserted outside it, and the
        // Windows check is next to the call rather than at the top of the test:
        // File.GetUnixFileMode is annotated unsupported on Windows and the
        // platform analyser only credits a guard it can see in the same body.
        //
        // Nothing is asserted on Windows because there is nothing to assert —
        // the app takes the same branch decision there and writes no mode.
        // %APPDATA% being per-user is what stands in for it.
        UnixFileMode? mode = null;

        InIsolation(dir =>
        {
            OpenClawIdentity.Current();
            if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(IdentityFile(dir));
        });

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    // The device id in the file is a convenience for anyone reading it, and
    // trusting it would let a hand-edited file point us at a device whose key we
    // do not hold — which the gateway would refuse in a way that reads as a
    // broken install.
    [Fact]
    public void AHandEditedDeviceIdIsIgnoredInFavourOfTheKey()
    {
        InIsolation(dir =>
        {
            var real = OpenClawIdentity.Current();
            OpenClawIdentity.ResetForTests();

            var json = File.ReadAllText(IdentityFile(dir))
                .Replace(real.DeviceId, new string('0', 64));
            File.WriteAllText(IdentityFile(dir), json);

            Assert.Equal(real.DeviceId, OpenClawIdentity.Current().DeviceId);
        });
    }

    // Unreadable, truncated, or written by something else. Falling through to a
    // new key costs one re-approval; refusing to start costs the feature.
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"privateKey\":\"\",\"publicKey\":\"\"}")]
    [InlineData("{\"privateKey\":\"!!!not base64!!!\",\"publicKey\":\"AAAA\"}")]
    [InlineData("{\"privateKey\":\"AAAA\",\"publicKey\":\"AAAA\"}")]
    public void AnUnusableIdentityFileIsReplacedRatherThanFatal(string contents)
    {
        InIsolation(dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(IdentityFile(dir), contents);

            var identity = OpenClawIdentity.Current();

            Assert.Equal(32, identity.PrivateKey.Length);
            Assert.Equal(OpenClawIdentity.DeviceIdOf(identity.PublicKey), identity.DeviceId);
        });
    }

    // An identity that cannot be persisted still works for this run. Provoked by
    // pointing the settings directory at a *file*, so Directory.CreateDirectory
    // throws — the only way to reach that catch without root.
    [Fact]
    public void AnIdentityThatCannotBePersistedStillWorksForThisRun()
    {
        var previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var blocker = Path.Combine(Path.GetTempPath(), "cb-identity-blocked-" + Guid.NewGuid());
        File.WriteAllText(blocker, "not a directory");

        try
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", blocker);
            OpenClawIdentity.ResetForTests();

            var identity = OpenClawIdentity.Current();
            Assert.Equal(32, identity.PublicKey.Length);

            // And a token set against the same unwritable directory is held in
            // memory rather than throwing at the caller.
            OpenClawIdentity.SetTokenFor("gw.local", "dev-token");
            Assert.Equal("dev-token", OpenClawIdentity.TokenFor("gw.local"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", previous);
            OpenClawIdentity.ResetForTests();
            try { File.Delete(blocker); } catch { }
        }
    }

    // Device tokens are keyed by host, because one machine can face more than
    // one gateway and a token from one is meaningless to another.
    [Fact]
    public void DeviceTokensAreKeptPerHostAndSurviveAReload()
    {
        InIsolation(dir =>
        {
            Assert.Null(OpenClawIdentity.TokenFor("one.local"));

            OpenClawIdentity.SetTokenFor("one.local", "token-one");
            OpenClawIdentity.SetTokenFor("two.local", "token-two");

            Assert.Equal("token-one", OpenClawIdentity.TokenFor("one.local"));
            Assert.Equal("token-two", OpenClawIdentity.TokenFor("two.local"));
            Assert.True(File.Exists(DeviceFile(dir)));

            OpenClawIdentity.ResetForTests();

            Assert.Equal("token-one", OpenClawIdentity.TokenFor("one.local"));
        });
    }

    // The gateway's own auth token is a different credential from the device
    // token, and both are required at once. Prefixed so a host called the same
    // thing as nothing in particular cannot collide with its device token —
    // which is the whole reason the prefix is there.
    [Fact]
    public void TheGatewayTokenIsHeldSeparatelyFromTheDeviceToken()
    {
        InIsolation(_ =>
        {
            OpenClawIdentity.SetTokenFor("gw.local", "device");
            OpenClawIdentity.SetGatewayTokenFor("gw.local", "gateway");

            Assert.Equal("device", OpenClawIdentity.TokenFor("gw.local"));
            Assert.Equal("gateway", OpenClawIdentity.GatewayTokenFor("gw.local"));

            // And neither is the other's, read through the other's accessor.
            Assert.Null(OpenClawIdentity.GatewayTokenFor("device"));
        });
    }

    // Setting an empty gateway token removes it rather than storing "". A stored
    // empty string is not the same thing as no token: the connect frame only
    // carries an `auth.token` when there is one, and an empty one would be sent
    // and refused.
    [Fact]
    public void ClearingTheGatewayTokenRemovesIt()
    {
        InIsolation(_ =>
        {
            OpenClawIdentity.SetGatewayTokenFor("gw.local", "gateway");
            OpenClawIdentity.SetGatewayTokenFor("gw.local", "");

            Assert.Null(OpenClawIdentity.GatewayTokenFor("gw.local"));
        });
    }

    // CB-278: every connect hands the device its token again, and nearly always
    // the same one, so setting an unchanged value must not rewrite the file.
    // Shown by planting a marker in the file after the first write: a rewrite
    // would serialise the in-memory table, which has never seen the marker, so
    // the marker surviving is the file not having been written at all. The
    // second half is the paired control: a changed value does rewrite, and
    // the marker goes.
    [Fact]
    public void SettingAnUnchangedDeviceTokenDoesNotRewriteTheStore()
    {
        InIsolation(dir =>
        {
            OpenClawIdentity.SetTokenFor("gw.local", "dev-one");

            const string planted = """{"gw.local":"dev-one","marker":"untouched"}""";
            File.WriteAllText(DeviceFile(dir), planted);

            OpenClawIdentity.SetTokenFor("gw.local", "dev-one");
            Assert.Equal(planted, File.ReadAllText(DeviceFile(dir)));

            // Ordinal, not case-insensitive: a token differing only in case is
            // a different token, and is written.
            OpenClawIdentity.SetTokenFor("gw.local", "DEV-ONE");
            Assert.DoesNotContain("marker", File.ReadAllText(DeviceFile(dir)));
            Assert.Equal("DEV-ONE", OpenClawIdentity.TokenFor("gw.local"));
        });
    }

    // Hosts are matched case-insensitively, because a hostname is.
    [Fact]
    public void HostsAreMatchedWithoutRegardToCase()
    {
        InIsolation(_ =>
        {
            OpenClawIdentity.SetTokenFor("Gateway.Local", "token");

            Assert.Equal("token", OpenClawIdentity.TokenFor("gateway.local"));
        });
    }

    // An unreadable credential store costs a re-approval, not a failure to
    // start — the same bargain the identity file makes.
    [Fact]
    public void AnUnreadableTokenStoreIsIgnored()
    {
        InIsolation(dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(DeviceFile(dir), "{ this is not json");

            Assert.Null(OpenClawIdentity.TokenFor("gw.local"));

            // And it recovers: a set after the bad read writes a good file.
            OpenClawIdentity.SetTokenFor("gw.local", "token");
            OpenClawIdentity.ResetForTests();

            Assert.Equal("token", OpenClawIdentity.TokenFor("gw.local"));
        });
    }

    // A token file that parses as JSON but not as a string map. Distinct from
    // the case above: Deserialize returns null rather than throwing, and the
    // early return for it is a separate line.
    [Fact]
    public void ATokenStoreThatIsJsonNullIsIgnored()
    {
        InIsolation(dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(DeviceFile(dir), "null");

            Assert.Null(OpenClawIdentity.TokenFor("gw.local"));
        });
    }

    // The device store holds credentials, so it is written owner-only for the
    // same reason the keypair is. Same platform note as the identity file above.
    [Fact]
    public void TheTokenStoreIsWrittenOwnerOnly()
    {
        UnixFileMode? mode = null;

        InIsolation(dir =>
        {
            OpenClawIdentity.SetTokenFor("gw.local", "token");
            if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(DeviceFile(dir));
        });

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
