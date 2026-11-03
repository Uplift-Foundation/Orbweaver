using Xunit;

namespace Orbweaver.UnitTests;

// Who said a user-role message the gateway handed us.
//
// Worth its own suite for the reason ChatSpeaker and OrbGlyph have one: the
// answer is drawn as an assertion about a person — your colour, your side of
// the panel — and the only other way to check it is to open a room and look at
// bubbles. It also reads an undocumented block on a format nobody here
// controls, so the cases below are the record of what was actually measured.
//
// Every id, name and sentence here is invented. The shapes are real; the
// contents are not, and this repository is public where the gateway is not.
public class OpenClawSenderTests
{
    private const string Prefix = OpenClawSender.MirrorPrefix;

    // --- the operator, three ways ------------------------------------------

    // Typed in Discord. The gateway states this outright, which makes it the
    // only one of the three that is measured rather than inferred.
    [Fact]
    public void TheGatewaySayingTheOwnerSentItIsEnough()
    {
        var sender = OpenClawSender.Classify(true, "quillfeather", null, "what is the status");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
        Assert.Equal("what is the status", sender.Text);
    }

    // Typed here. No sender fields at all — the gateway does not attribute a
    // message it accepted from this client — and a top-level idempotency key
    // whose ":user" suffix is the whole signal.
    [Fact]
    public void AMessageThisClientSentIsRecognisedByItsIdempotencyKey()
    {
        var sender = OpenClawSender.Classify(
            null, null, "8f2a41c0-1d3e-4b77-9a05-6c1e2f0b8d34:user", "any thoughts?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // The same block on __openclaw rather than on the message, since both carry
    // it and the caller falls back from one to the other.
    [Fact]
    public void TheSameKeyIsRecognisedWhereverItWasRead()
    {
        var sender = OpenClawSender.Classify(
            null, null, "0b9d1a55-77c4-4f10-bb2e-3d8a90e6c412:user", "still there?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // The agent's own reply carries an idempotency key too, and it must not
    // match. It is assistant-role anyway, so this is belt and braces — but the
    // suffix test is a string test, and "cli-assistant:<guid>" is exactly the
    // shape that would be caught by a looser one.
    [Fact]
    public void AnAgentsOwnReplyKeyIsNotMistakenForYours()
    {
        var sender = OpenClawSender.Classify(
            null, null, "cli-assistant:5c7e0f81-2b46-4d93-8a1f-70e6bb43d205", "done");

        Assert.NotEqual(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // --- our own mirror, coming back ---------------------------------------

    // The prefix beats the name, and this is the case that made the ordering
    // matter. A message typed here is posted to the channel by whichever bot
    // account carried it, so the copies that reach the *other* agents arrive
    // attributed to that bot — trusting senderName would draw your own words as
    // an agent's.
    [Fact]
    public void OurOwnMirrorIsYoursEvenThoughItArrivesUnderTheCarriersName()
    {
        var sender = OpenClawSender.Classify(
            false, "Quillbot", null, Prefix + "everyone still awake?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
        Assert.Null(sender.Name);
    }

    // And the prefix comes off. It is addressing, not content: leaving it on
    // would show you a sentence you did not type, and — because the carrier's
    // own transcript holds the copy without it — would stop the two matching,
    // which is how a successful room send came to be drawn twice.
    [Fact]
    public void TheMirrorPrefixIsStrippedFromWhatIsShown()
    {
        var sender = OpenClawSender.Classify(
            false, "Quillbot", null, Prefix + "everyone still awake?");

        Assert.Equal("everyone still awake?", sender.Text);
    }

    // The same, for history an older build mirrored - and for an older build on
    // another machine still mirroring today. Room history never expires, so
    // this stays true forever.
    [Fact]
    public void TheLegacyMirrorPrefixIsStrippedAndStillMine()
    {
        var sender = OpenClawSender.Classify(
            false, "Quillbot", null, OpenClawSender.LegacyMirrorPrefix + "everyone still awake?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
        Assert.Null(sender.Name);
        Assert.Equal("everyone still awake?", sender.Text);
    }

    [Fact]
    public void TheCurrentMirrorPrefixIsMineToo()
    {
        var sender = OpenClawSender.Classify(
            false, "Quillbot", null, Prefix + "everyone still awake?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // The two literals, pinned: the first is what new builds write, the second
    // what every old transcript holds.
    [Fact]
    public void TheTwoPrefixesAreTheBrandedStrings()
    {
        Assert.Equal("**(via Orbweaver)** ", OpenClawSender.MirrorPrefix);
        Assert.Equal("**(via Claude Buddy)** ", OpenClawSender.LegacyMirrorPrefix);
    }

    // Only at the front. Somebody writing the words in the middle of a sentence
    // is talking *about* this app, not through it, and their message is theirs.
    [Fact]
    public void TheWordsInTheMiddleOfASentenceAreNotAMirror()
    {
        var sender = OpenClawSender.Classify(
            false, "Thistle", null, "I sent that one via Claude Buddy earlier");

        Assert.Equal(OpenClawSender.SenderKind.Named, sender.Kind);
        Assert.Equal("Thistle", sender.Name);
        Assert.Equal("I sent that one via Claude Buddy earlier", sender.Text);
    }

    // --- somebody else ------------------------------------------------------

    // An agent relayed through the channel. Richer than expected — the relay
    // carries the bot's Discord display name — so a relayed agent turn is named
    // rather than anonymous.
    [Fact]
    public void ARelayedMessageIsAttributedToWhoeverTheGatewayNames()
    {
        var sender = OpenClawSender.Classify(false, "Thistle", null, "nodes are loaded");

        Assert.Equal(OpenClawSender.SenderKind.Named, sender.Kind);
        Assert.Equal("Thistle", sender.Name);
    }

    // The name is trimmed but not otherwise touched — it goes on a chip beside
    // the bubble, and leading space would show as a gap inside it.
    [Fact]
    public void TheNameIsTrimmed()
    {
        var sender = OpenClawSender.Classify(false, "  Thistle  ", null, "nodes are loaded");

        Assert.Equal("Thistle", sender.Name);
    }

    // A name that is only whitespace says nothing, so it is not a name.
    [Fact]
    public void AnEmptyNameIsNotANameAtAll()
    {
        var sender = OpenClawSender.Classify(false, "   ", null, "nodes are loaded");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
    }

    // --- nothing said -------------------------------------------------------

    // The safety net, and the reason the whole rule is ordered the way it is:
    // with no metadata at all the answer is exactly what this app did before any
    // of it existed — an anonymous turn, drawn as the room's own voice. A
    // gateway that stops sending `__openclaw` degrades to the old transcript
    // rather than to a wrong one.
    [Fact]
    public void WithNoMetadataAtAllNobodyIsNamedAndNothingIsClaimed()
    {
        var sender = OpenClawSender.Classify(null, null, null, "morning");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
        Assert.Null(sender.Name);
        Assert.Equal("morning", sender.Text);
    }

    // senderIsOwner false with no name is the same answer. The gateway has said
    // it wasn't you and has not said who, which is less than it usually offers
    // and still not grounds to guess.
    [Fact]
    public void NotYoursAndUnnamedIsStillUnknown()
    {
        var sender = OpenClawSender.Classify(false, null, null, "morning");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
    }

    // An inter-session message arrives here after Readable has already stripped
    // its machine header and identified the agent behind it. It carries no
    // sender fields, so it falls through to Unknown — which is what leaves
    // Readable's better answer in place at the call site.
    [Fact]
    public void AnInterSessionMessageFallsThroughAndLeavesReadablesAnswerAlone()
    {
        var sender = OpenClawSender.Classify(null, null, null, "can you take the build?");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
    }

    // --- CB-270: a turn the gateway wrote itself is not a person ---------------

    private const string Suffix = "8f2a41c0-1d3e-4b77-9a05-6c1e2f0b8d34:user";

    // The measured case: an exec notice on a WhatsApp-routed main carrying the
    // ":user" suffix with senderIsOwner false. Checked after the suffix it was the
    // operator's own words; the provenance has to be asked first.
    [Fact]
    public void AnInternalSystemTurnIsAMachineEvenWithTheUserSuffix()
    {
        var sender = OpenClawSender.Classify(
            false, null, Suffix, "Exec completed (calm-otter, code 0)", "internal_system");

        Assert.Equal(OpenClawSender.SenderKind.Machine, sender.Kind);
        Assert.Null(sender.Name);
        Assert.Equal("Exec completed (calm-otter, code 0)", sender.Text);
    }

    // The order pins: every other signal, each one separately, loses to it.
    [Fact]
    public void ProvenanceOutranksTheOwnerFlagTheMirrorPrefixAndASenderName()
    {
        Assert.Equal(OpenClawSender.SenderKind.Machine,
            OpenClawSender.Classify(true, null, null, "x", "internal_system").Kind);
        Assert.Equal(OpenClawSender.SenderKind.Machine,
            OpenClawSender.Classify(null, null, null, Prefix + "x", "internal_system").Kind);
        Assert.Equal(OpenClawSender.SenderKind.Machine,
            OpenClawSender.Classify(false, "quillfeather", null, "x", "internal_system").Kind);

        // The prefix is not stripped from a machine turn: it was never ours.
        Assert.Equal(Prefix + "x",
            OpenClawSender.Classify(null, null, null, Prefix + "x", "internal_system").Text);
    }

    // The controls: no provenance, or a provenance that is not internal_system,
    // changes nothing. S2's own sends carry the suffix and no provenance.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user_input")]
    [InlineData("INTERNAL_SYSTEM")]
    public void AnythingButInternalSystemLeavesTheOldRuleInForce(string? provenance)
    {
        var sender = OpenClawSender.Classify(null, null, Suffix, "any thoughts?", provenance);

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // --- IsGatewayInjected ---

    [Theory]
    [InlineData("A scheduled reminder has been triggered. The reminder content is: stretch")]
    [InlineData("System: [2026-10-29 09:15:02] Node connected")]
    [InlineData("System: [2026-10-29 09:15:02 PDT] Node connected")]
    [InlineData("System: [2026-10-29 09:15:02 UTC]")]
    public void TheMeasuredInjectionsAreRecognised(string text) =>
        Assert.True(OpenClawSender.IsGatewayInjected(text));

    // Anchored and case-sensitive: a person typing the sentence is not swallowed.
    [Theory]
    [InlineData("System: hello")]
    [InlineData("System: [not a timestamp] hello")]
    [InlineData("System: [2026-10-29 09:15] too short")]
    [InlineData("System: [2026-10-29 09:15:02 pacific] zone too long")]
    [InlineData("fyi: A scheduled reminder has been triggered")]
    [InlineData("a scheduled reminder has been triggered")]
    [InlineData("")]
    [InlineData("what time is the standup?")]
    public void ANearMissIsNotInjected(string text) =>
        Assert.False(OpenClawSender.IsGatewayInjected(text));
}
