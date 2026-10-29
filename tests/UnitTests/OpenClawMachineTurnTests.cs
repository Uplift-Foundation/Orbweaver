using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270 S5: turns the gateway's own machinery injects into a transcript in the
// user role, and the one turn that really is the operator's.
//
// Measured read-only against OpenClaw 2026.10.5 on the two main sessions a
// WhatsApp DM is routed into: every user turn the gateway returned was machine
// output, none a person's WhatsApp message. By category, what they were:
//   * "A scheduled reminder has been triggered…" injections, by far the most;
//   * "OpenClaw resumed this CLI session…" notices (already dropped by Readable);
//   * `System: [<timestamp>]` lines;
//   * tool_result blocks in the user role;
//   * "Exec completed (…)" notices, which carry
//     provenance {kind: "internal_system", sourceTool: "exec"}.
// And the finding that makes this a bug rather than a nicety: on one of the two,
// the exec notices carry the ":user" idempotency suffix with senderIsOwner
// false. Classify checks the suffix, so they are drawn today as the operator's
// own words: blue, right-aligned, "you said this".
//
// Every sentence, id and key here is invented; the field structure is real.
// Attributing a bare user turn to the WhatsApp peer is deliberately NOT done:
// no human WhatsApp turn was ever observed, so the shape of one is unknown.
[Collection("Settings")]
public class OpenClawMachineTurnTests
{
    private static System.Collections.Generic.List<HistoryTurn> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(json).RootElement, null);

    // The bug: a machine's notice that wears the operator's suffix.
    [Fact]
    public void AnExecNoticeCarryingTheUserSuffixIsNotTheOperatorsWords()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"user","content":"Exec completed (calm-otter, code 0) :: build finished",
          "timestamp":1787000000000,
          "idempotencyKey":"3f1c8ad2-59b7-4e06-9c31-8ab7205de164:user",
          "provenance":{"kind":"internal_system","sourceTool":"exec"},
          "__openclaw":{"senderIsOwner":false,"idempotencyKey":"3f1c8ad2-59b7-4e06-9c31-8ab7205de164:user"}}]
        """));

        Assert.False(turn.Mine);
        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Null(turn.Speaker);
    }

    [Fact]
    public void AScheduledReminderIsANoteNotAPersonsMessage()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"user","content":"A scheduled reminder has been triggered. The reminder content is: stand up and stretch",
          "timestamp":1787000001000}]
        """));

        Assert.Equal(ChatRole.System, turn.Role);
        Assert.False(turn.Mine);
    }

    [Fact]
    public void ATimestampedSystemLineIsANote()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"user","content":"System: [2026-10-29 09:15:02 PDT] Node connected",
          "timestamp":1787000002000}]
        """));

        Assert.Equal(ChatRole.System, turn.Role);
        Assert.False(turn.Mine);
    }

    // The controls: the operator's own typed message, and a named turn from a
    // WhatsApp group, are both unchanged.
    [Fact]
    public void ATypedMessageWithTheUserSuffixAndNoProvenanceIsStillMine()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"user","content":"are the reminders going out?",
          "timestamp":1787000003000,
          "idempotencyKey":"7a4d19e0-0c52-4b8e-a6d3-91f0c4ba7d58:user",
          "__openclaw":{"idempotencyKey":"7a4d19e0-0c52-4b8e-a6d3-91f0c4ba7d58:user"}}]
        """));

        Assert.True(turn.Mine);
        Assert.Equal(ChatRole.User, turn.Role);
    }

    [Fact]
    public void AWhatsAppGroupTurnWithASenderNameStaysNamed()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"user","content":"anyone seen the build?",
          "timestamp":1787000004000,
          "__openclaw":{"senderIsOwner":false,"senderName":"Marlowe Vance"}}]
        """));

        Assert.False(turn.Mine);
        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Equal("Marlowe Vance", turn.Speaker);
    }
}
