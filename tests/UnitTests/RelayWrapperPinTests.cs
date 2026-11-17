using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-330: CodexTranscript and GrokTranscript are deliberately untouched. Measured
// on one Mac, 18 Codex rollouts and 1,140 Grok updates.jsonl carried the relay
// wrapper in no user message, so neither parser classifies it. These pin the
// choice so it is visible rather than accidental: a user message that *is* the
// wrapper stays an ordinary user turn there, and if someone later wires Classify
// in, these fail by name and the decision gets made on purpose.
public class RelayWrapperPinTests
{
    private static readonly string Wrapper =
        ChatTranscript.PeerPrefix
        + "<teammate-message teammate_id=\"a\">\n{\"type\":\"shutdown_approved\"}\n</teammate-message>"
        + "\n\nThis came from another Claude session — not typed by your user.";

    [Fact]
    public void ControlTheWrapperReallyClassifiesWhereItIsRecognised() =>
        Assert.NotNull(RelayedMessage.Classify(Wrapper));

    [Fact]
    public void ACodexUserMessageThatIsTheWrapperStaysAUserTurn()
    {
        var row = "{\"timestamp\":\"2026-08-24T12:00:00.000Z\",\"ordinal\":1,\"type\":\"event_msg\","
            + "\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"content\":"
            + "[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(Wrapper) + "}]}}}";

        var turn = Assert.Single(CodexTranscript.Map(new[] { row })).Turn;

        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Null(turn.Relay);
        Assert.StartsWith("Another Claude session", turn.Text);
    }

    [Fact]
    public void AGrokUserMessageThatIsTheWrapperStaysAUserTurn()
    {
        var update = "{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":"
            + JsonSerializer.Serialize(Wrapper) + "}}";
        var line = "{\"timestamp\":1788198313,\"method\":\"session/update\","
            + "\"params\":{\"sessionId\":\"s1\",\"update\":" + update + "}}";

        var turn = Assert.Single(GrokTranscript.Map(new[] { line })).Turn;

        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Null(turn.Relay);
        Assert.StartsWith("Another Claude session", turn.Text);
    }

    // The phone's role mapping needs no change: a relay turn is a system turn.
    [Fact]
    public void ARelayTurnReachesThePhoneAsASystemLine()
    {
        var relay = RelayedMessage.Classify(Wrapper)!.Relays.Single();
        var turn = new ChatTurn { Role = ChatRole.System, Text = relay.Line, Relay = relay, IsComplete = true };

        var wire = TranscriptWatch.From(turn);

        Assert.Equal(("system", "Teammate @a shut down"), (wire.Role, wire.Text));
    }
}
