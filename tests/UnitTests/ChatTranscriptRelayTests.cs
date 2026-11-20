using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-330: the transcript paths the captured rows do not reach. The real rows are
// in Cb330Captures; no real row has had block-array content or a cloud envelope.
public class ChatTranscriptRelayTests
{
    private const string Provenance = "\n\nThis came from another Claude session — not typed by your user.";

    private static readonly string Wrapped =
        ChatTranscript.PeerPrefix
        + "<teammate-message teammate_id=\"a\" summary=\"S\">\nbody\n</teammate-message>" + Provenance;

    private static string Row(string contentJson, string extra = "") =>
        "{\"type\":\"user\"," + extra + "\"uuid\":\"r1\",\"timestamp\":\"2026-11-16T10:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + contentJson + "}}";

    [Fact]
    public void ATextBlockInAnArrayRowIsClassifiedToo()
    {
        var content = "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(Wrapped) + "},"
                      + "{\"type\":\"text\",\"text\":\"and my own words\"}]";

        var rows = ChatTranscript.Map(new[] { Row(content) });

        Assert.Equal(2, rows.Count);
        Assert.Equal((ChatRole.System, "Teammate @a sent a message: S"), (rows[0].Turn.Role, rows[0].Turn.Text));
        Assert.Equal("a", rows[0].Turn.Relay!.Name);
        Assert.Equal((ChatRole.User, "and my own words"), (rows[1].Turn.Role, rows[1].Turn.Text));
        Assert.All(rows, r => Assert.Equal("r1", r.Uuid));
    }

    [Fact]
    public void ATextBlockBesidePastedPictureStillCarriesThePictureAndIsNotClassified()
    {
        // The last text block carries the image; it goes through AddText as
        // before, so a picture is never lost to the relay path.
        var png = System.Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var content = "[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + png + "\"}},"
                      + "{\"type\":\"text\",\"text\":\"[Image #1] look\"}]";

        var turn = Assert.Single(ChatTranscript.Map(new[] { Row(content) })).Turn;

        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Null(turn.Relay);
        Assert.Equal("look", turn.Text);
    }

    [Fact]
    public void ANullTextBlockIsNotClassifiedAndIsDropped()
    {
        var content = "[{\"type\":\"text\"}]";
        Assert.Empty(ChatTranscript.Map(new[] { Row(content) }));
    }

    [Fact]
    public void ACloudPageCarryingTheWrapperYieldsARelayTurn()
    {
        var page = ClaudeCloudEvents.ParsePage(
            "{\"data\":[" + Row(JsonSerializer.Serialize(Wrapped)) + "],\"has_more\":false,\"last_id\":\"e1\"}");

        var turn = Assert.Single(page.Rows).Turn;

        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Equal("a", turn.Relay!.Name);
    }

    [Fact]
    public void AQueuedRowCarryingTheWrapperYieldsARelayTurn()
    {
        var queued = "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"uuid\":\"q1\","
                     + "\"timestamp\":\"2026-11-16T10:00:00Z\",\"content\":" + JsonSerializer.Serialize(Wrapped) + "}";

        var row = Assert.Single(ChatTranscript.Map(new[] { queued }));

        Assert.Equal(ChatRole.System, row.Turn.Role);
        Assert.Equal("S", row.Turn.Relay!.Preview);
    }

    [Fact]
    public void AQueuedRowOfOrdinaryTextStaysAUserTurn()
    {
        var queued = "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"uuid\":\"q1\","
                     + "\"timestamp\":\"2026-11-16T10:00:00Z\",\"content\":\"keep going\"}";

        var turn = Assert.Single(ChatTranscript.Map(new[] { queued })).Turn;

        Assert.Equal((ChatRole.User, "keep going"), (turn.Role, turn.Text));
        Assert.Null(turn.Relay);
    }

    [Fact]
    public void AMetaPeerRowWithArrayContentIsStillDropped()
    {
        var content = "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(Wrapped) + "}]";
        var meta = Row(content, "\"isMeta\":true,\"origin\":{\"kind\":\"peer\"},");

        Assert.Empty(ChatTranscript.Map(new[] { meta }));
    }

    [Fact]
    public void AJsonFirstLineThatIsNotJsonFallsBackToTheFencedBody()
    {
        var text = "<teammate-message teammate_id=\"a\">\n{bad\nmore\n</teammate-message>";

        var turn = Assert.Single(ChatTranscript.Map(new[] { Row(JsonSerializer.Serialize(text)) })).Turn;

        Assert.Equal("```\n{bad\nmore\n```", turn.Relay!.FullText);
    }
}
