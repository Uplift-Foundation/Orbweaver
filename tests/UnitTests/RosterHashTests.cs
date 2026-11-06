using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// CB-216: the rules that let an unchanged roster go unsent. A peer asks every
// ten seconds and each answer embedded every persona picture, about 17 MB per
// ask on the machine it was measured on. The round trip itself is covered in
// MirrorRoundTripTests; these are the decisions it rests on.
public class RosterHashTests
{
    private static readonly MirrorProtocol.MirrorRosterEntry[] Roster =
    {
        new("far-session", MirrorProtocol.CliClaudeCode, true, true,
            Persona: new MirrorProtocol.PeerPersona("Ava", null, null, new byte[] { 71, 73, 70, 56 })),
    };

    [Fact]
    public void TheWireRosterIsTheGzipOfTheBytesTheHashIsTakenOver()
    {
        var raw = MirrorProtocol.RosterBytes(Roster);

        Assert.Equal(raw, MirrorProtocol.Gunzip(MirrorProtocol.EncodeRoster(Roster)));
    }

    [Fact]
    public void TheSameRosterAlwaysHashesTheSame()
    {
        // The whole scheme depends on it: an answer is recognised as unchanged
        // only if building the same roster twice produces the same bytes.
        Assert.Equal(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(Roster)),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(Roster.ToArray())));
    }

    [Fact]
    public void AChangedPictureChangesTheHash()
    {
        var repainted = new[]
        {
            Roster[0] with { Persona = Roster[0].Persona! with { Avatar = new byte[] { 71, 73, 70, 57 } } },
        };

        Assert.NotEqual(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(Roster)),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(repainted)));
    }

    [Fact]
    public void TheSameEntriesInAnotherOrderHashTheSameOnceCanonical()
    {
        var a = new MirrorProtocol.MirrorRosterEntry("alpha", MirrorProtocol.CliClaudeCode, true, true, Route: "r1");
        var b = new MirrorProtocol.MirrorRosterEntry("beta", MirrorProtocol.CliClaudeCode, true, true, Route: "r2");

        // The QA finding: raw order leaks into the hash...
        Assert.NotEqual(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(new[] { a, b })),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(new[] { b, a })));

        // ...and the canonical order takes it out.
        Assert.Equal(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(MirrorProtocol.CanonicalRoster(new[] { a, b }))),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(MirrorProtocol.CanonicalRoster(new[] { b, a }))));
    }

    [Fact]
    public void CanonicalOrderIsRouteThenNameWithNoRouteFirst()
    {
        var entries = new[]
        {
            new MirrorProtocol.MirrorRosterEntry("b", MirrorProtocol.CliCodex, true, true, Route: "z"),
            new MirrorProtocol.MirrorRosterEntry("b", MirrorProtocol.CliClaudeCode, true, true),
            new MirrorProtocol.MirrorRosterEntry("a", MirrorProtocol.CliClaudeCode, true, true),
            new MirrorProtocol.MirrorRosterEntry("a", MirrorProtocol.CliGrok, true, true, Route: "Z"),
        };

        Assert.Equal(
            new[] { ("", "a"), ("", "b"), ("Z", "a"), ("z", "b") },
            MirrorProtocol.CanonicalRoster(entries).Select(e => (e.Route ?? "", e.Name)));
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("ABC", "abc", false)]   // hex from one function; no case games
    [InlineData(null, "abc", false)]    // every first ask, and every older Buddy's
    [InlineData("", "abc", false)]
    public void OnlyAHashTheAskerSentAndThatMatchesIsUnchanged(string? asker, string current, bool unchanged) =>
        Assert.Equal(unchanged, MirrorProtocol.RosterUnchanged(asker, current));

    // --- the gzip memo ------------------------------------------------------------

    [Fact]
    public void AnIdenticalRosterIsCompressedOnce()
    {
        var compressions = 0;
        var memo = new RosterGzipMemo(raw => { compressions++; return MirrorProtocol.Gzip(raw); });
        var raw = Encoding.UTF8.GetBytes("roster");

        var first = memo.For("h1", raw);
        var second = memo.For("h1", raw);

        Assert.Equal(1, compressions);
        Assert.Same(first, second);
    }

    [Fact]
    public void ADifferentRosterIsCompressedAndBothAreKept()
    {
        // Two rosters at once is the mixed-version case: one peer is sent
        // picture ids, another the pictures inline, and neither may evict the
        // other's compressed roster on every ask.
        var compressions = 0;
        var memo = new RosterGzipMemo(raw => { compressions++; return MirrorProtocol.Gzip(raw); });

        memo.For("h1", Encoding.UTF8.GetBytes("one"));
        var two = memo.For("h2", Encoding.UTF8.GetBytes("two"));
        memo.For("h1", Encoding.UTF8.GetBytes("one"));

        Assert.Equal(2, compressions);
        Assert.Equal("two", Encoding.UTF8.GetString(MirrorProtocol.Gunzip(two)));
    }

    [Fact]
    public void TheOldestRosterGoesFirstPastCapacity()
    {
        var compressions = 0;
        var memo = new RosterGzipMemo(raw => { compressions++; return MirrorProtocol.Gzip(raw); });

        for (var i = 0; i <= RosterGzipMemo.Capacity; i++) memo.For("h" + i, Encoding.UTF8.GetBytes("r" + i));
        Assert.Equal(RosterGzipMemo.Capacity + 1, compressions);

        memo.For("h" + RosterGzipMemo.Capacity, Encoding.UTF8.GetBytes("r"));   // newest: kept
        Assert.Equal(RosterGzipMemo.Capacity + 1, compressions);

        memo.For("h0", Encoding.UTF8.GetBytes("r0"));                           // oldest: gone
        Assert.Equal(RosterGzipMemo.Capacity + 2, compressions);
    }

    // --- picture ids ------------------------------------------------------------

    [Fact]
    public void APictureIdIsItsHashAndLength()
    {
        var bytes = new byte[] { 71, 73, 70, 56, 57, 97 };

        Assert.Equal(MirrorProtocol.Hash(bytes) + ":6", MirrorProtocol.AvatarIdOf(bytes));
    }

    [Fact]
    public void OnlyTheBytesAPictureIdNamesMatchIt()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var id = MirrorProtocol.AvatarIdOf(bytes);

        Assert.True(MirrorProtocol.AvatarMatches(id, bytes));
        Assert.False(MirrorProtocol.AvatarMatches(id, new byte[] { 1, 2, 4 }));
        Assert.False(MirrorProtocol.AvatarMatches(id, new byte[] { 1, 2, 3, 0 }));
        Assert.False(MirrorProtocol.AvatarMatches(id, null));
        Assert.False(MirrorProtocol.AvatarMatches(null, bytes));
        Assert.False(MirrorProtocol.AvatarMatches("", bytes));
    }

    [Fact]
    public void APersonaWithOnlyAPictureIdIsNotEmpty() =>
        Assert.False(new MirrorProtocol.PeerPersona(AvatarId: "abc:3").IsEmpty);

    [Fact]
    public void APictureIdSurvivesTheWireAndCarriesNoBytes()
    {
        var back = MirrorProtocol.DecodeRoster(MirrorProtocol.EncodeRoster(new[]
        {
            new MirrorProtocol.MirrorRosterEntry("far-session", MirrorProtocol.CliClaudeCode, true, true,
                Persona: new MirrorProtocol.PeerPersona("Ava", AvatarId: "abc:3")),
        }));

        var persona = Assert.Single(back!).Persona!;
        Assert.Equal("abc:3", persona.AvatarId);
        Assert.Null(persona.Avatar);
    }

    [Fact]
    public async Task PeersAskingAtOnceAllGetTheSameBytes()
    {
        var memo = new RosterGzipMemo(MirrorProtocol.Gzip);
        var raw = Encoding.UTF8.GetBytes(new string('x', 100_000));

        var answers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => memo.For("same", raw))));

        Assert.All(answers, answer => Assert.Equal(raw, MirrorProtocol.Gunzip(answer)));
    }

    // CB-307: the parked flag is how a peer learns a session has gone quiet or
    // come back, and it rides on the hash: unchanged when nothing is parked,
    // changed the moment something flips.
    [Fact]
    public void ARosterWithNothingParkedHashesAsItDidBeforeTheFieldExisted()
    {
        var before = "[{\"name\":\"far-session\",\"cli\":\"claude\",\"transcript\":true,\"pane\":true}]";
        var now = new MirrorProtocol.MirrorRosterEntry("far-session", MirrorProtocol.CliClaudeCode, true, true);

        Assert.Equal(before, Encoding.UTF8.GetString(MirrorProtocol.RosterBytes(new[] { now })));
        Assert.Equal(
            MirrorProtocol.Hash(Encoding.UTF8.GetBytes(before)),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(new[] { now })));
    }

    [Fact]
    public void ASessionGoingQuietChangesTheHash()
    {
        var live = new MirrorProtocol.MirrorRosterEntry("far-session", MirrorProtocol.CliClaudeCode, true, true);
        var parked = live with { Parked = true };

        Assert.NotEqual(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(new[] { live })),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(new[] { parked })));
    }
}
