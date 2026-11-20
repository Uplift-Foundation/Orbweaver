using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-330: a relayed message crossing the mirror link. The new field is optional
// on MirrorTurn, so a frame with no relay must be byte for byte what it was, a
// frame with one must survive the round trip, and a frame from an older
// Orbweaver (raw wrapper as a user turn, no relay) must still be compacted here.
public class MirrorRelayTests
{
    private static readonly RelayedMessage Idle =
        new(RelayKind.Teammate, "hana", "green", "idle_notification", "finished", "Done.", "Done.");

    private const string Provenance =
        "\n\nThis came from another Claude session — not typed by your user, but very likely working on their behalf.";

    private static string Wrapped(params string[] ids) =>
        ChatTranscript.PeerPrefix
        + string.Join("\n\n", ids.Select(i => $"<teammate-message teammate_id=\"{i}\">\nhello {i}\n</teammate-message>"))
        + Provenance;

    private static string UserRow(string content, string uuid = "u1") =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-11-16T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":"
        + JsonSerializer.Serialize(content) + "}}";

    [Fact]
    public void ARelayTurnRoundTripsThroughEncodeAndDecode()
    {
        var turns = MirrorProtocol.TurnsFrom(new[] { UserRow(Wrapped("a")) }, MirrorProtocol.CliClaudeCode);
        var back = MirrorProtocol.DecodeTurns(MirrorProtocol.EncodeTurns(turns))!;

        Assert.Equal(turns, back);
        var turn = Assert.Single(back);
        Assert.Equal("s", turn.Role);
        Assert.Equal("Teammate @a sent a message: hello a", turn.Text);
        Assert.Equal("a", turn.Relay!.Name);
    }

    [Fact]
    public void ARelayFrameIsAdoptedAsASystemTurnWithTheRelay()
    {
        var sent = new MirrorProtocol.MirrorTurn("s", Idle.Line, "x", 1_700_000_000, MirrorProtocol.MirrorRelay.From(Idle));

        var adopted = Assert.Single(MirrorProtocol.AdoptTurns(new[] { sent }));

        Assert.Equal("x", adopted.Key);
        Assert.Equal(ChatRole.System, adopted.Turn.Role);
        Assert.Equal(Idle, adopted.Turn.Relay);
        Assert.Equal(Idle.Line, adopted.Turn.Text);
        Assert.True(adopted.Turn.IsComplete);
    }

    [Fact]
    public void ATurnWithNoRelayLeavesTheFrameJsonUnchanged()
    {
        var turns = MirrorProtocol.TurnsFrom(
            new[] { UserRow("fix it") }, MirrorProtocol.CliClaudeCode);

        var json = Encoding.UTF8.GetString(Gunzip(MirrorProtocol.EncodeTurns(turns)));

        Assert.DoesNotContain("\"k\"", json);
        Assert.Contains("\"r\":\"u\"", json);
    }

    [Fact]
    public void AFrameWithNoKDecodesWithANullRelay()
    {
        var old = Encoding.UTF8.GetBytes("[{\"r\":\"u\",\"t\":\"hi\",\"u\":\"1\",\"a\":5}]");

        var back = Assert.Single(MirrorProtocol.DecodeTurns(Gzip(old))!);

        Assert.Null(back.Relay);
        Assert.Equal("hi", back.Text);
    }

    [Fact]
    public void AnOlderReceiverIgnoresTheNewPropertyAndKeepsTheText()
    {
        // The old record shape: no Relay member. System.Text.Json skips "k".
        var turns = MirrorProtocol.TurnsFrom(new[] { UserRow(Wrapped("a")) }, MirrorProtocol.CliClaudeCode);
        var json = Gunzip(MirrorProtocol.EncodeTurns(turns));

        var old = JsonSerializer.Deserialize<OldTurn[]>(json)!;

        Assert.Equal("s", Assert.Single(old).R);
        Assert.StartsWith("Teammate @a", old[0].T);
    }

    private sealed record OldTurn(
        [property: System.Text.Json.Serialization.JsonPropertyName("r")] string R,
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] string T);

    [Fact]
    public void ARawWrapperFromAnOlderSenderIsCompactedOnThisSide()
    {
        var oldFrame = new MirrorProtocol.MirrorTurn("u", Wrapped("a", "b"), "row", 1_700_000_000);

        var adopted = MirrorProtocol.AdoptTurns(new[] { oldFrame });

        Assert.Equal(new[] { "row", "row#1" }, adopted.Select(a => a.Key));
        Assert.All(adopted, a => Assert.Equal(ChatRole.System, a.Turn.Role));
        Assert.Equal(new[] { "a", "b" }, adopted.Select(a => a.Turn.Relay!.Name));
    }

    [Fact]
    public void OwnWordsFromAnOlderSendersWrapperStayAUserTurn()
    {
        // A bare wrapped message is a peer relay off the meta path, so it never
        // yields OwnText here; an own-app cross-session tag does.
        var text = ChatTranscript.PeerPrefix
            + "<cross-session-message from=\"x\" from-name=\"Orbweaver on mac\">\nship it\n</cross-session-message>"
            + Provenance;
        var adopted = MirrorProtocol.AdoptTurns(new[] { new MirrorProtocol.MirrorTurn("u", text, "own", 0) });

        var turn = Assert.Single(adopted).Turn;
        Assert.Equal((ChatRole.User, "ship it"), (turn.Role, turn.Text));
    }

    [Fact]
    public void OrdinaryTurnsAreAdoptedAsBefore()
    {
        var adopted = MirrorProtocol.AdoptTurns(new[]
        {
            new MirrorProtocol.MirrorTurn("u", "typed", "1", 0),
            new MirrorProtocol.MirrorTurn("a", "reply", null, 0),
            new MirrorProtocol.MirrorTurn("a", "again", null, 0),
        });

        Assert.Equal(new string?[] { "1", null, null }, adopted.Select(a => a.Key));
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Assistant }, adopted.Select(a => a.Turn.Role));
        Assert.All(adopted, a => Assert.Null(a.Turn.Relay));
    }

    [Fact]
    public void TurnsOfOneRowGetDistinctKeysSoNoneIsDroppedAsASeenUuid()
    {
        var adopted = MirrorProtocol.AdoptTurns(new[]
        {
            new MirrorProtocol.MirrorTurn("a", "one", "r", 0),
            new MirrorProtocol.MirrorTurn("a", "two", "r", 0),
            new MirrorProtocol.MirrorTurn("a", "three", "r", 0),
        });

        Assert.Equal(new[] { "r", "r#1", "r#2" }, adopted.Select(a => a.Key));
    }

    // The old behaviour, shown beside the new. The session dedupes on a key; keyed
    // by uuid alone (before CB-330) a row with three text blocks kept the first
    // turn and dropped two. Keyed by AdoptTurns it keeps all three.
    [Fact]
    public void ANonRelayRowWithSeveralTurnsKeepsAllOfThemWhereUuidAloneKeptOne()
    {
        var row = "{\"type\":\"assistant\",\"uuid\":\"m1\",\"timestamp\":\"2026-11-16T10:00:00Z\","
            + "\"message\":{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"one\"},{\"type\":\"text\",\"text\":\"two\"},{\"type\":\"text\",\"text\":\"three\"}]}}";
        var turns = MirrorProtocol.TurnsFrom(new[] { row }, MirrorProtocol.CliClaudeCode);
        Assert.Equal(3, turns.Count);
        Assert.All(turns, t => Assert.Null(t.Relay));

        var oldSeen = new System.Collections.Generic.HashSet<string>();
        var keptByUuid = turns.Where(t => oldSeen.Add(t.Uuid!)).Count();

        var newSeen = new System.Collections.Generic.HashSet<string>();
        var keptByKey = MirrorProtocol.AdoptTurns(turns).Where(a => newSeen.Add(a.Key!)).Select(a => a.Turn.Text).ToList();

        Assert.Equal(1, keptByUuid);
        Assert.Equal(new[] { "one", "two", "three" }, keptByKey);
    }

    [Fact]
    public void AnUnknownRelayKindIsReadAsPeer()
    {
        var r = new MirrorProtocol.MirrorRelay(99, null, null, "t", "v", "p", "f").ToRelayed();
        Assert.Equal(RelayKind.Peer, r.Kind);
    }

    [Fact]
    public void AFrameMissingRelayStringsStillBuildsARelay()
    {
        var json = Encoding.UTF8.GetBytes("[{\"r\":\"s\",\"t\":\"x\",\"k\":{\"d\":0}}]");

        var r = Assert.Single(MirrorProtocol.AdoptTurns(MirrorProtocol.DecodeTurns(Gzip(json))!)).Turn.Relay!;

        Assert.Equal((RelayKind.Teammate, "", "", ""), (r.Kind, r.PayloadType, r.Verb, r.Preview));
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
            gz.Write(raw);
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] gz)
    {
        using var input = new System.IO.MemoryStream(gz);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new System.IO.MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
