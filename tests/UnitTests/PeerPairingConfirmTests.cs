using Xunit;

namespace Orbweaver.UnitTests;

// CB-346: a pairing dial adopts nothing and delivers nothing until the far
// side's `ok` proves it holds the code for the certificate this connection is
// actually on, and the code itself is never sent.
//
// The dial accepts whatever certificate answers, because there is no pin yet to
// compare it with, and it Adopts before any answer. The proof is a keyed hash
// over both sides' nonces and both pins, so an answerer that sees both pins and
// both nonces but does not hold the code cannot make it. The control is the
// legitimate pairing: a gate that refused everything would pass the negative
// cases.
[Collection("Settings")]
public class PeerPairingConfirmTests
{
    private const string Code = "123456";
    private const string OurPin = "aa11";
    private const string PresentedPin = "bb22";
    private const string OtherPin = "cc33";
    private const string ClientNonce = "c0c0";
    private const string ServerNonce = "5e5e";

    private static PeerLink Link(List<(string Machine, PeerProtocol.PeerMessage Message)> got) =>
        new(new PeerLink.Seams(
            Deliver: (machine, message) =>
            {
                lock (got) got.Add((machine, message));
                return Task.CompletedTask;
            },
            KnownPeer: _ => null,
            OwnCertificate: _ => throw new InvalidOperationException("no socket in this test")));

    // Reads what the far side "sent" and keeps what this side wrote.
    private sealed class Duplex(byte[] incoming) : Stream
    {
        private readonly MemoryStream _in = new(incoming);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    private static async Task<byte[]> BytesOf(params PeerProtocol.PeerMessage[] messages)
    {
        using var stream = new MemoryStream();
        foreach (var m in messages) await PeerProtocol.WriteAsync(stream, m);
        return stream.ToArray();
    }

    private static PeerProtocol.PeerMessage Ok(string serverPin, string code = Code) =>
        PeerProtocol.Message(PeerProtocol.Ok, "h", name: "mini", nonce: ServerNonce,
            proof: PeerIdentity.PairingProof(code, PeerIdentity.ServerRole, ClientNonce, ServerNonce, serverPin, OurPin));

    // Runs a pairing dial's connection to its end, with the connection adopted
    // on the certificate TLS presented (PresentedPin).
    private static async Task<(bool Connected, List<string> Announced, string Sent)> RunAsync(
        PeerLink link, params PeerProtocol.PeerMessage[] frames)
    {
        var announced = new List<string>();
        link.PeerConnected += name => { lock (announced) announced.Add(name); };

        var stream = new Duplex(await BytesOf(frames));
        link.BeginPairing("mini", Code, OurPin, ClientNonce);
        link.Adopt("mini", stream, PresentedPin, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (link.ConnectedMachines().Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection was never forgotten");
            await Task.Delay(10);
        }

        return (link.IsConnected("mini"), announced, System.Text.Encoding.UTF8.GetString(stream.Written.ToArray()));
    }

    private static async Task InScratchAsync(Func<Task> body)
    {
        var previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var scratch = Path.Combine(Path.GetTempPath(), "cb-pair-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", scratch);
        PeerIdentity.ForgetForTests();

        try { await body(); }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", previous);
            PeerIdentity.ForgetForTests();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AnAnswerForAnotherCertificateDeliversNothingAndIsNotKept()
    {
        await InScratchAsync(async () =>
        {
            var got = new List<(string, PeerProtocol.PeerMessage)>();
            using var link = Link(got);

            var (_, announced, sent) = await RunAsync(link,
                PeerProtocol.Message(PeerProtocol.Window, "early"),
                Ok(OtherPin),
                PeerProtocol.Message(PeerProtocol.Window, "late"));

            Assert.Empty(got);
            Assert.Empty(announced);
            Assert.DoesNotContain(PeerProtocol.PairConfirm, sent);
            Assert.Null(PeerIdentity.PeerFor("mini"));
        });
    }

    // The substitute this ticket is about: it sees both certificates and both
    // nonces, because they are all on the wire, but was never told the code.
    [Fact]
    public async Task AnAnswererThatKnowsBothPinsButNotTheCodeCannotPass()
    {
        await InScratchAsync(async () =>
        {
            var got = new List<(string, PeerProtocol.PeerMessage)>();
            using var link = Link(got);

            var (_, announced, sent) = await RunAsync(link,
                Ok(PresentedPin, code: "000000"),
                PeerProtocol.Message(PeerProtocol.Window, "late"));

            Assert.Empty(got);
            Assert.Empty(announced);
            Assert.DoesNotContain(PeerProtocol.PairConfirm, sent);
            Assert.Null(PeerIdentity.PeerFor("mini"));
        });
    }

    [Fact]
    public async Task AnAnswerWithoutAProofIsNotAConfirmation()
    {
        await InScratchAsync(async () =>
        {
            var got = new List<(string, PeerProtocol.PeerMessage)>();
            using var link = Link(got);

            var (_, announced, _) = await RunAsync(link,
                PeerProtocol.Message(PeerProtocol.Ok, "h", name: "mini", nonce: ServerNonce),
                PeerProtocol.Message(PeerProtocol.Window, "late"));

            Assert.Empty(got);
            Assert.Empty(announced);
            Assert.Null(PeerIdentity.PeerFor("mini"));
        });
    }

    [Fact]
    public async Task FramesBeforeTheAnswerAreDiscardedAndLaterOnesArrive()
    {
        await InScratchAsync(async () =>
        {
            var got = new List<(string Machine, PeerProtocol.PeerMessage Message)>();
            using var link = Link(got);

            var (_, announced, sent) = await RunAsync(link,
                PeerProtocol.Message(PeerProtocol.Window, "early"),
                Ok(PresentedPin),
                PeerProtocol.Message(PeerProtocol.Window, "late"));

            Assert.Equal(new[] { "late" }, got.Select(g => g.Message.Id));
            Assert.Equal(new[] { "mini" }, announced);

            // The dialler proved in turn, and never put the code on the wire.
            Assert.Contains(PeerProtocol.PairConfirm, sent);
            Assert.DoesNotContain(Code, sent);

            // The pin bound is the certificate that was presented.
            Assert.Equal(PresentedPin, PeerIdentity.PeerFor("mini")?.Pin);
        });
    }

    [Fact]
    public void TheProofBindsCodeRoleNoncesAndBothCertificates()
    {
        var proof = PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, ClientNonce, ServerNonce, PresentedPin, OurPin);

        Assert.Equal(proof, PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, ClientNonce, ServerNonce, PresentedPin.ToUpperInvariant(), OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof("654321", PeerIdentity.ServerRole, ClientNonce, ServerNonce, PresentedPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, PeerIdentity.ClientRole, ClientNonce, ServerNonce, PresentedPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, "other", ServerNonce, PresentedPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, ClientNonce, "other", PresentedPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, ClientNonce, ServerNonce, OtherPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, ClientNonce, ServerNonce, PresentedPin, OtherPin));
    }

    // The accepting side: a dialler that has seen the accepting side's proof
    // and both pins but not the code cannot make the confirmation, and a
    // window that has closed since confirms nothing.
    [Fact]
    public void TheAcceptingSideOnlyConfirmsADiallerThatHoldsTheCode()
    {
        var pending = (Code, ClientNonce, ServerNonce, ServerPin: OurPin, ClientPin: PresentedPin);
        string Proof(string code) =>
            PeerIdentity.PairingProof(code, PeerIdentity.ClientRole, ClientNonce, ServerNonce, OurPin, PresentedPin);

        Assert.True(PeerLink.PairConfirmed(pending, Proof(Code), Code));
        Assert.False(PeerLink.PairConfirmed(pending, Proof("000000"), Code));
        Assert.False(PeerLink.PairConfirmed(pending, null, Code));
        Assert.False(PeerLink.PairConfirmed(pending, Proof(Code), null));
        Assert.False(PeerLink.PairConfirmed(pending,
            PeerIdentity.PairingProof(Code, PeerIdentity.ServerRole, ClientNonce, ServerNonce, OurPin, PresentedPin), Code));
    }
}
