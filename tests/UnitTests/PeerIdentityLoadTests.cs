using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.UnitTests;

[CollectionDefinition("PeerIdentityLoad", DisableParallelization = true)]
public sealed class PeerIdentityLoadCollection { }

// CB-301: this machine's peer certificate is loaded once, outside every lock,
// and a load that stalls or fails is logged rather than freezing the link.
//
// The defect was a PKCS#12 import — on macOS a keychain operation — run under
// PeerIdentity's Gate on every announce and every handshake. One import that
// waited on a SecurityAgent prompt held Gate forever, and Peers(), PeerFor()
// and the dial loop queued behind it with no log line. A real prompt cannot be
// summoned in a test, so the loader is a seam and these tests stand a blocked
// one in for it: blocked on an event, observed through an "entered" event,
// always released in a finally so a regression fails the suite rather than
// hanging it.
//
// Non-parallel because PeerIdentity is process-wide state.
[Collection("PeerIdentityLoad")]
public sealed class PeerIdentityLoadTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    private readonly ConcurrentQueue<(string What, string Detail)> _heard = new();

    public PeerIdentityLoadTests()
    {
        PeerIdentity.ForgetForTests();
        MirrorLog.Heard += Hear;
    }

    public void Dispose()
    {
        MirrorLog.Heard -= Hear;
        PeerIdentity.ForgetForTests();
    }

    private void Hear(string what, string detail) => _heard.Enqueue((what, detail));

    private IEnumerable<string> Said(string what) =>
        _heard.Where(l => l.What == what).Select(l => l.Detail);

    // A loader that waits for permission, announces that it has been entered,
    // and counts how often it ran.
    private sealed class BlockedLoader : IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public int Calls;

        public X509Certificate2 Load(byte[] pkcs12)
        {
            Interlocked.Increment(ref Calls);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable);
        }

        public void Dispose() => Release.Set();
    }

    private static Task Within(Action action) =>
        Task.Run(action).WaitAsync(Bound);

    // --- the pin ---------------------------------------------------------------

    // The pin is a hash of the public certificate. Taking it must never import
    // a private key — it used to, on every announce.
    [Fact]
    public async Task ThePinNeverCallsTheLoader()
    {
        var calls = 0;
        using var _ = PeerIdentity.UseLoaderForTests(b =>
        {
            Interlocked.Increment(ref calls);
            return X509CertificateLoader.LoadPkcs12(b, null, X509KeyStorageFlags.Exportable);
        });

        var pin = PeerIdentity.OwnPin();
        for (var i = 0; i < 50; i++) Assert.Equal(pin, PeerIdentity.OwnPin());
        Assert.Equal(0, calls);

        // And it is the same pin the loaded certificate has always pinned by.
        var cert = await PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PeerIdentity.PinOf(cert), pin);
        Assert.Equal(1, calls);
    }

    // The parse the pin relies on, against the exact recipe Create() uses.
    [Fact]
    public void TheDerReadFromAPkcs12IsTheCertificatesRawData()
    {
        using var key = RSA.Create(2048);
        using var made = new CertificateRequest("CN=der", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        Assert.Equal(made.RawData, PeerIdentity.CertificateDerOf(made.Export(X509ContentType.Pkcs12)));
    }

    // A PKCS#12 holding only a certificate has no identity in it to pin; it
    // is refused rather than pinned by whatever certificate happens to be
    // inside.
    [Fact]
    public void APkcs12WithNoPrivateKeyIsRefused()
    {
        using var key = RSA.Create(2048);
        using var made = new CertificateRequest("CN=nokey", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(made.RawData);

        Assert.Throws<CryptographicException>(
            () => PeerIdentity.CertificateDerOf(publicOnly.Export(X509ContentType.Pkcs12)));
    }

    [Fact]
    public void GarbageIsNotAPkcs12() =>
        Assert.ThrowsAny<Exception>(() => PeerIdentity.CertificateDerOf([1, 2, 3, 4]));

    // --- a load that will not finish -------------------------------------------

    // The defect itself. While the import is stuck, everything else that
    // touches peer identity carries on.
    [Fact]
    public async Task AStuckLoadBlocksNothingElse()
    {
        using var loader = new BlockedLoader();
        using var _ = PeerIdentity.UseLoaderForTests(loader.Load);
        var pending = PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30));

        try
        {
            Assert.True(loader.Entered.Wait(Bound), "the loader was never entered");

            await Within(() => PeerIdentity.Peers());
            await Within(() => PeerIdentity.PeerFor("nobody"));
            await Within(() => PeerIdentity.Remember(new PeerIdentity.Peer("ab", "cb301-stuck")));
            await Within(() => PeerIdentity.Forget("cb301-stuck"));
            await Within(() => PeerIdentity.OwnPin());

            Assert.False(pending.IsCompleted);
        }
        finally
        {
            loader.Release.Set();
        }

        // Finished here rather than left running into the next test, where its
        // "peer-identity-loaded" line would be counted as that test's.
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(30))).HasPrivateKey);
    }

    // Twenty-five handshakes arriving during one stuck import wait on the same
    // import, rather than each starting another keychain operation behind it.
    [Fact]
    public async Task ConcurrentCallersShareOneLoad()
    {
        using var loader = new BlockedLoader();
        using var _ = PeerIdentity.UseLoaderForTests(loader.Load);

        Task<X509Certificate2>[] waits;
        try
        {
            waits = Enumerable.Range(0, 25)
                .Select(_ => Task.Run(() => PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30))))
                .ToArray();

            Assert.True(loader.Entered.Wait(Bound), "the loader was never entered");
        }
        finally
        {
            loader.Release.Set();
        }

        var certs = await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, loader.Calls);
        Assert.All(certs, c => Assert.Same(certs[0], c));

        // And later callers reuse it with no further load.
        Assert.Same(certs[0], await PeerIdentity.CertificateAsync(Bound));
        Assert.Equal(1, loader.Calls);
        Assert.Single(Said("peer-identity-loaded"));
    }

    // A caller that runs out of patience is told so, the log says so once, and
    // the stuck load is left to finish rather than joined by another.
    [Fact]
    public async Task AStallTimesOutTheCallerLogsOnceAndStartsNoSecondLoad()
    {
        using var loader = new BlockedLoader();
        using var _ = PeerIdentity.UseLoaderForTests(loader.Load);
        var wait = TimeSpan.FromMilliseconds(173);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => PeerIdentity.CertificateAsync(wait));
            await Assert.ThrowsAsync<TimeoutException>(() => PeerIdentity.CertificateAsync(wait));

            Assert.Equal(1, loader.Calls);
            Assert.Single(Said("peer-identity-stalled"));
            Assert.Contains("still loading", Said("peer-identity-stalled").Single());
        }
        finally
        {
            loader.Release.Set();
        }

        var cert = await PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30));
        Assert.True(cert.HasPrivateKey);
        Assert.Equal(1, loader.Calls);
    }

    // A failed load is logged and forgotten, so the next handshake retries
    // instead of inheriting the failure for the life of the process.
    [Fact]
    public async Task AFailedLoadIsLoggedAndRetried()
    {
        var calls = 0;
        using var _ = PeerIdentity.UseLoaderForTests(b =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new CryptographicException("cb301 first import refused");
            return X509CertificateLoader.LoadPkcs12(b, null, X509KeyStorageFlags.Exportable);
        });

        await Assert.ThrowsAsync<CryptographicException>(
            () => PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains(Said("peer-identity-load-failed"),
            d => d.Contains("cb301 first import refused"));

        // The forgetting is a continuation, so give it the moment it needs —
        // bounded, never a bare sleep.
        X509Certificate2? cert = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (cert is null && DateTime.UtcNow < deadline)
        {
            try { cert = await PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30)); }
            catch (CryptographicException) { await Task.Yield(); }
        }

        Assert.NotNull(cert);
        Assert.Equal(2, calls);
    }

    // The handshake seam and the warm-up both route through the one load.
    [Fact]
    public async Task WarmingStartsTheLoadTheHandshakeThenReuses()
    {
        var calls = 0;
        using var _ = PeerIdentity.UseLoaderForTests(b =>
        {
            Interlocked.Increment(ref calls);
            return X509CertificateLoader.LoadPkcs12(b, null, X509KeyStorageFlags.Exportable);
        });

        PeerIdentity.Warm();
        var cert = await PeerIdentity.HandshakeCertificateAsync(CancellationToken.None);

        Assert.True(cert.HasPrivateKey);
        Assert.Equal(1, calls);
        Assert.True(PeerIdentity.HandshakeWait > TimeSpan.Zero && PeerIdentity.HandshakeWait < PeerIdentity.WarmWait);
    }

    // A warm-up that fails must not surface as an unobserved task exception.
    [Fact]
    public async Task AWarmUpThatFailsIsObserved()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = PeerIdentity.UseLoaderForTests(b =>
        {
            failed.TrySetResult();
            throw new CryptographicException("cb301 warm-up refused");
        });

        PeerIdentity.Warm();
        await failed.Task.WaitAsync(Bound);

        // Reached the log, and nothing escaped to fail the test host.
        var deadline = DateTime.UtcNow + Bound;
        while (!Said("peer-identity-load-failed").Any(d => d.Contains("cb301 warm-up refused"))
               && DateTime.UtcNow < deadline)
            await Task.Yield();

        Assert.Contains(Said("peer-identity-load-failed"), d => d.Contains("cb301 warm-up refused"));
    }

    // The seam puts the real loader back, and a test's load does not outlive it.
    [Fact]
    public void UsingATestLoaderRestoresTheRealOne()
    {
        var real = PeerIdentity.Loader;

        using (PeerIdentity.UseLoaderForTests(_ => throw new InvalidOperationException()))
            Assert.NotSame(real, PeerIdentity.Loader);

        Assert.Same(real, PeerIdentity.Loader);
    }
}
