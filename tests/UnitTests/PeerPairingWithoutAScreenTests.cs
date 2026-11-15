using Xunit;

namespace Orbweaver.UnitTests;

// Pairing a machine nobody is sitting at, and the window lapsing on its own.
//
// The three rules here are the whole of the headless path's security. Every
// other way a pairing window opens has a person clicking a button in front of
// it; these do not, so what they accept is the entire guard.
public class PeerPairingWithoutAScreenTests
{
    private static PeerLink Link() => new(new PeerLink.Seams(
        Deliver: (_, _) => Task.CompletedTask,
        KnownPeer: _ => null,
        OwnCertificate: _ => throw new InvalidOperationException("no socket in this test")));

    // --- the window lapses ---------------------------------------------------

    private static readonly DateTime Opened = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AWindowIsOpenBeforeItsDeadline()
    {
        Assert.Equal("123456", PeerLink.StillOpen(
            "123456", Opened.AddMinutes(5), Opened.AddMinutes(4)));
    }

    [Fact]
    public void AWindowIsShutAtItsDeadline()
    {
        // Exactly at, not merely past: a boundary written as `<` and one written
        // as `<=` differ by a whole tick of validity, and only one of them was
        // meant.
        Assert.Null(PeerLink.StillOpen("123456", Opened.AddMinutes(5), Opened.AddMinutes(5)));
    }

    [Fact]
    public void AWindowIsShutAfterItsDeadline()
    {
        Assert.Null(PeerLink.StillOpen("123456", Opened.AddMinutes(5), Opened.AddHours(3)));
    }

    [Fact]
    public void NoCodeIsNoWindowHoweverEarlyItIs()
    {
        Assert.Null(PeerLink.StillOpen(null, Opened.AddMinutes(5), Opened));
    }

    [Fact]
    public void AShownCodeStopsWorkingOnItsOwn()
    {
        // The hole this closes: the first cut had no expiry and a comment
        // claiming the settings pane closed the window. Nothing anywhere called
        // ClosePairing, so a code shown once stayed valid until Buddy restarted.
        var now = Opened;

        using var link = Link();
        link.Now = () => now;

        link.OpenForPairing();
        Assert.True(link.PairingOpen);

        now = Opened + PeerLink.PairingWindowLife;
        Assert.False(link.PairingOpen);
    }

    [Fact]
    public void ClosingStillShutsItImmediately()
    {
        // The stronger statement, and the one a completed pairing uses: it does
        // not wait five minutes to be true.
        using var link = Link();

        link.OpenForPairing();
        link.ClosePairing();

        Assert.False(link.PairingOpen);
    }

    [Fact]
    public void AGivenCodeIsUsedRatherThanAFreshOne()
    {
        // The headless path hands in the code from the file. Generating one
        // instead would open a window with a code nobody can read.
        using var link = Link();

        Assert.Equal("246810", link.OpenForPairing("246810"));
    }

    // --- what the marker file may say ----------------------------------------

    [Fact]
    public void SixDigitsOpenAWindow()
    {
        Assert.Equal("123456", PeerSessions.CodeInFile("123456"));
    }

    [Fact]
    public void TheNewlineEchoAddsIsForgiven()
    {
        // `echo 123456 > pair-open` is how this will actually be written, every
        // time. Refusing the newline would make the documented command fail.
        Assert.Equal("123456", PeerSessions.CodeInFile("123456\n"));
        Assert.Equal("123456", PeerSessions.CodeInFile("  123456  \r\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("zsh: command not found")]
    [InlineData("123 456")]
    public void AnythingElseOpensNothing(string? contents)
    {
        // A file holding a stray shell error or half a line must open no window
        // at all. This is the only place in the app where a window opens with
        // nobody having clicked anything.
        Assert.Null(PeerSessions.CodeInFile(contents));
    }

    // --- an address typed by hand --------------------------------------------

    [Fact]
    public void ABareAddressGetsTheDefaultPort()
    {
        var address = PeerSessions.Address("198.51.100.10", PeerLink.DefaultPort);

        Assert.Equal(("198.51.100.10", PeerLink.DefaultPort), address);
    }

    [Fact]
    public void AHostNameWorksAsWellAsAnAddress()
    {
        Assert.Equal(("avatar.local", 7677), PeerSessions.Address("avatar.local", 7677));
    }

    [Fact]
    public void SurroundingSpaceIsForgiven()
    {
        Assert.Equal(("mini", 7677), PeerSessions.Address("  mini  ", 7677));
    }

    [Fact]
    public void AnExplicitPortWins()
    {
        Assert.Equal(("198.51.100.10", 9000), PeerSessions.Address("198.51.100.10:9000", 7677));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mini:")]
    [InlineData("mini:notaport")]
    [InlineData("mini:0")]
    [InlineData("mini:70000")]
    [InlineData("two words")]
    public void AnythingThatIsNotAnAddressIsRefusedBeforeItCostsAConnection(string? typed)
    {
        // A typo should say so in the window. Dialling it and coming back
        // "refused" reads as the pairing having failed rather than as never
        // having been attempted.
        Assert.Null(PeerSessions.Address(typed, PeerLink.DefaultPort));
    }

    [Fact]
    public void ABracketedIpv6AddressCanCarryAPort()
    {
        // The only way to write a v6 address with a port. It needs its own arm:
        // an earlier version claimed the last-colon rule covered this and it did
        // not.
        Assert.Equal(("::1", 9000), PeerSessions.Address("[::1]:9000", 7677));
    }

    [Fact]
    public void ABracketedIpv6AddressWithoutAPortLosesItsBrackets()
    {
        Assert.Equal(("::1", 7677), PeerSessions.Address("[::1]", 7677));
    }

    [Fact]
    public void AnIpv6LiteralKeepsItsColons()
    {
        // A v6 literal has several colons and no port, which is why the rule
        // counts them rather than reaching for the last one. Worth pinning: an
        // address silently truncated at its last colon would fail as an
        // unreachable host, which reads as a network problem rather than a
        // parse — and this is the fallback people reach for precisely when the
        // network is already suspect.
        var address = PeerSessions.Address("fe80::1c2e:aeff:fe12:3456", 7677);

        Assert.Equal(("fe80::1c2e:aeff:fe12:3456", 7677), address);
    }
}
