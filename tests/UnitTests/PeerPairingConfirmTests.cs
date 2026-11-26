using Xunit;

namespace Orbweaver.UnitTests;

// CB-346: a pairing dial adopts nothing and delivers nothing until the far
// side's `ok` proves it holds the code for the certificate this connection is
// actually on.
//
// The dial accepts whatever certificate answers, because there is no pin yet to
// compare it with, and it Adopts before any answer. So anything that answered
// was a mirror session until the pairing ended. The control is the legitimate
// pairing: a gate that refused everything would pass the negative cases.
[Collection("Settings")]
public class PeerPairingConfirmTests
{
    private const string Code = "123456";
    private const string OurPin = "aa11";
    private const string PresentedPin = "bb22";
    private const string OtherPin = "cc33";

    private static PeerLink Link(List<(string Machine, PeerProtocol.PeerMessage Message)> got) =>
        new(new PeerLink.Seams(
            Deliver: (machine, message) =>
            {
                lock (got) got.Add((machine, message));
                return Task.CompletedTask;
            },
            KnownPeer: _ => null,
            OwnCertificate: _ => throw new InvalidOperationException("no socket in this test")));

    private static async Task<byte[]> BytesOf(params PeerProtocol.PeerMessage[] messages)
    {
        using var stream = new MemoryStream();
        foreach (var m in messages) await PeerProtocol.WriteAsync(stream, m);
        return stream.ToArray();
    }

    private static PeerProtocol.PeerMessage Ok(string serverPin) =>
        PeerProtocol.Message(PeerProtocol.Ok, "h", name: "mini",
            proof: PeerIdentity.PairingProof(Code, serverPin, OurPin));

    // Runs a pairing dial's connection to its end, with the connection adopted
    // on the certificate TLS presented (PresentedPin).
    private static async Task<(bool Connected, List<string> Announced)> RunAsync(
        PeerLink link, params PeerProtocol.PeerMessage[] frames)
    {
        var announced = new List<string>();
        link.PeerConnected += name => { lock (announced) announced.Add(name); };

        link.BeginPairing("mini", Code, OurPin);
        link.Adopt("mini", new MemoryStream(await BytesOf(frames)), PresentedPin, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (link.ConnectedMachines().Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection was never forgotten");
            await Task.Delay(10);
        }

        return (link.IsConnected("mini"), announced);
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

            var (_, announced) = await RunAsync(link,
                PeerProtocol.Message(PeerProtocol.Window, "early"),
                Ok(OtherPin),
                PeerProtocol.Message(PeerProtocol.Window, "late"));

            Assert.Empty(got);
            Assert.Empty(announced);
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

            var (_, announced) = await RunAsync(link,
                PeerProtocol.Message(PeerProtocol.Ok, "h", name: "mini"),
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

            var (_, announced) = await RunAsync(link,
                PeerProtocol.Message(PeerProtocol.Window, "early"),
                Ok(PresentedPin),
                PeerProtocol.Message(PeerProtocol.Window, "late"));

            Assert.Equal(new[] { "late" }, got.Select(g => g.Message.Id));
            Assert.Equal(new[] { "mini" }, announced);

            // The pin bound is the certificate that was presented.
            Assert.Equal(PresentedPin, PeerIdentity.PeerFor("mini")?.Pin);
        });
    }

    [Fact]
    public void TheProofBindsCodeAndBothCertificates()
    {
        var proof = PeerIdentity.PairingProof(Code, PresentedPin, OurPin);

        Assert.Equal(proof, PeerIdentity.PairingProof(Code, PresentedPin.ToUpperInvariant(), OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof("654321", PresentedPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, OtherPin, OurPin));
        Assert.NotEqual(proof, PeerIdentity.PairingProof(Code, PresentedPin, OtherPin));
    }
}
