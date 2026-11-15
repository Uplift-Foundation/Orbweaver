using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-344, over a real TLS socket: a client whose certificate nobody pinned gets
// nothing delivered to the mirror, whatever it sends before its hello.
//
// The unit tests (PeerPreHelloTests) pin the rule over an in-memory stream. This
// is the seam they cannot reach: an inbound handshake that accepts any
// certificate, which is what made the rule necessary. Before the fix this
// client's first frame reached Deliver under an "(inbound …)" name.
public class PeerPreHelloLoopbackTests
{
    private static X509Certificate2 NewCert(string cn)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public async Task AnUnpinnedClientThatSkipsHelloGetsNothingDelivered()
    {
        var serverCert = NewCert("server");
        var stranger = NewCert("stranger");

        var delivered = new List<(string Machine, string Type)>();

        using var server = new PeerLink(new PeerLink.Seams(
            Deliver: (machine, message) =>
            {
                lock (delivered) delivered.Add((machine, message.Type));
                return Task.CompletedTask;
            },
            KnownPeer: _ => null,                       // nobody is paired
            OwnCertificate: _ => Task.FromResult(serverCert)));

        server.Listen(0);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "server",
            ClientCertificates = new X509Certificate2Collection(stranger),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        });

        // A mirror request with no greeting in front of it, then a greeting the
        // server must refuse. The refusal is the end signal: the server answers
        // err and drops the connection, so the client's read reaches it or EOF.
        // Drop runs before the pump has drained its delivery queue, which is why
        // this is enough: with the fix nothing is ever queued, and without it the
        // frame is queued before the hello is even read, so its synchronous
        // delivery beats the err across TLS. It failed that way on origin/main.
        await PeerProtocol.WriteAsync(tls, PeerProtocol.Message(
            PeerProtocol.Window, PeerProtocol.NewId(),
            body: PeerProtocol.BodyOf("CB-MIRROR: v1 FETCH (test)")));
        await PeerProtocol.WriteAsync(tls, PeerProtocol.Message(
            PeerProtocol.Hello, PeerProtocol.NewId(), name: "stranger"));

        var answer = await ReadUntilEndOrErrAsync(tls).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(answer is null || answer.Type == PeerProtocol.Err,
            $"expected a refusal or a hangup, got {answer?.Type}");

        lock (delivered)
            Assert.Empty(delivered);
    }

    private static async Task<PeerProtocol.PeerMessage?> ReadUntilEndOrErrAsync(Stream stream)
    {
        try
        {
            while (await PeerProtocol.ReadAsync(stream, CancellationToken.None) is { } message)
                if (message.Type == PeerProtocol.Err) return message;
        }
        catch (IOException)
        {
            // The server closing under a pending read is a hangup too.
        }

        return null;
    }
}
