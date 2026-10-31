namespace Orbweaver
{
    // Who said a user-role message the gateway handed us, and what of it is
    // worth showing.
    //
    // Pure and separate for the same reason ChatSpeaker and OpenClawSessionKind
    // are: the rule is small, it decides something a person will read as an
    // assertion about who was talking, and checking it any other way means
    // opening a panel and looking at bubbles.
    //
    // The problem it solves is that every message in a Discord channel arrives
    // in *every* member agent's transcript in the user role, whoever sent it,
    // and until now nothing here distinguished them. The room's answer was to
    // draw all of them as the room's own neutral voice — honest, and the
    // comment in OpenClawRoomChatSession.Rebuild explains why that beat the
    // alternative, but it means your own message comes back to you looking like
    // someone else's.
    //
    // It turns out the gateway does say, in an undocumented `__openclaw` block
    // on each message. Four shapes were measured on a live gateway (OpenClaw
    // 2026.7.1-2, protocol 4) across five agents' transcripts, durable across
    // history reloads back over a fortnight:
    //
    //   * The operator typing in Discord — senderIsOwner true, plus their
    //     Discord id, display name and username.
    //   * The operator typing here, in Orbweaver — no sender fields at all,
    //     and a top-level idempotencyKey of "<guid>:user". That suffix is what
    //     separates it from the agent's own reply, which carries
    //     "cli-assistant:<guid>" and is assistant-role anyway.
    //   * Another agent's message relayed through the channel — senderIsOwner
    //     false, plus the bot's Discord *display name*. Richer than expected:
    //     a relayed agent turn is named, not anonymous.
    //   * An inter-session message from sessions_send — no sender fields, and a
    //     "[Inter-session message]" header that Readable() already parses. It
    //     reaches Classify with that header gone, so it falls through to
    //     Unknown here and keeps the speaker Readable found.
    //
    // **A third person in the channel is assumed, not observed.** No human but
    // the operator appears anywhere in the retained history, so what someone
    // else's message looks like is an inference from the relay shape —
    // senderIsOwner false plus their name. The rendering chosen is the same
    // either way (a named turn, drawn as someone else's), which is why the gap
    // is tolerable rather than blocking; it is written down here because an
    // assumption nobody records is indistinguishable from a measurement.
    //
    // The whole rule degrades to what this app did before it when the metadata
    // is absent: no fields, no idempotency key and no prefix means Unknown,
    // which is the anonymous turn the room has always drawn. That is the safety
    // net if the gateway's internals move — this reads a shape nobody here
    // controls and nobody else documents.
    internal static class OpenClawSender
    {
        // What a message Orbweaver posts to a channel wears at the front.
        //
        // Fixed rather than composed, and it lives here rather than at the
        // composer, because it is read back as well as written: the copies that
        // reach the other agents in a room are the only trace that a message
        // came from us, and recognising one has to still work against a
        // transcript written by a build from six months ago. A prefix that
        // varied — by machine, by version, by who sent it — would be
        // unrecognisable exactly where it matters, in history.
        //
        // Both halves in one constant so the composer and the recognizer cannot
        // drift apart. They were two string literals in two files for one
        // release, which is how a successful room send came to be drawn twice:
        // once plain from the carrier's own transcript, once prefixed from
        // everybody else's, with nothing matching the two together.
        public const string MirrorPrefix = $"**(via {Brand.DisplayName})** ";

        // What builds before the Orbweaver rename wrote, and it must stay
        // recognised forever: a room's history never expires, so every message
        // an older build mirrored — here, or from another machine that has not
        // upgraded yet — still opens with this, and without it those would be
        // drawn as somebody else's words. Composed from Brand.Legacy so the
        // string is not a second literal to drift; only ever stripped, never
        // written. The cleanup after phase 3 must NOT delete this one.
        public const string LegacyMirrorPrefix = $"**(via {Brand.Legacy.DisplayName})** ";

        internal enum SenderKind
        {
            // Nothing said who. Drawn as the room's own voice, with no name and
            // not in your colour — "somebody said this", which is true, where
            // "you said this" would be a guess.
            Unknown,

            // The operator's own words, whichever way they got here.
            Mine,

            // Somebody with a name: another agent relayed through the channel,
            // or (assumed) another person in it.
            Named,

            // CB-270: not a person at all. The gateway's own machinery injected
            // this into the transcript in the user role: an exec notice, a
            // reminder, a system line. Drawn as a dim note, never as anybody's
            // words.
            Machine
        }

        internal readonly record struct Sender(SenderKind Kind, string? Name, string Text);

        // senderIsOwner and senderName come out of the message's `__openclaw`
        // block; idempotencyKey is read from the message itself first and from
        // `__openclaw` as a fallback, since both carry it. text is what the
        // panel would otherwise draw, already through Readable().
        //
        // The order below is precedence, and each step earns its place over the
        // one after it:
        //
        // provenanceKind is `message.provenance.kind`, which sits on the message
        // itself and not inside `__openclaw`. A trailing, defaulted parameter so
        // every caller that predates it is unchanged; what matters is the order
        // of the checks, and that one is first.
        public static Sender Classify(
            bool? senderIsOwner, string? senderName, string? idempotencyKey, string text,
            string? provenanceKind = null)
        {
            // CB-270, and first of all. The gateway labels its own injected turns
            // provenance.kind "internal_system", and it has to be asked before
            // anything else because the measured exec notices on a WhatsApp-routed
            // main carry the ":user" suffix with senderIsOwner false: checked
            // after the suffix, they were drawn as the operator's own words,
            // blue and right-aligned. A turn the gateway says it wrote itself is
            // not the operator's whatever else it wears, not even our mirror
            // prefix.
            if (string.Equals(provenanceKind, "internal_system", StringComparison.Ordinal))
            {
                return new Sender(SenderKind.Machine, null, text);
            }

            // Our own mirror, coming back. First, and ahead of senderName,
            // because the relayed copy is attributed to the *bot account* that
            // carried it — so a message you typed here reaches the other agents
            // wearing an agent's name, and trusting the name would draw your own
            // words as somebody else's.
            //
            // Nothing else is *expected* to write this prefix, and the only
            // thing Orbweaver mirrors is a message the person at the keyboard
            // just typed. Expected, not guaranteed: anyone in the channel can
            // type those characters, and if they do their words are drawn as the
            // operator's — blue and right-aligned — beating an explicit
            // senderIsOwner of false. That is accepted rather than unnoticed.
            //
            // Accepted because the alternative is worse and commoner. Trusting
            // senderName over the prefix would draw the operator's own mirrored
            // words as an agent's, which happens on every room send rather than
            // only when somebody sets out to spoof one; and the harm here is
            // display-only, inside the operator's own panel. A misattributed
            // bubble grants nothing, sends nothing, and moves no message.
            if (text.StartsWith(MirrorPrefix, StringComparison.Ordinal))
            {
                return new Sender(SenderKind.Mine, null, text[MirrorPrefix.Length..]);
            }

            if (text.StartsWith(LegacyMirrorPrefix, StringComparison.Ordinal))
            {
                return new Sender(SenderKind.Mine, null, text[LegacyMirrorPrefix.Length..]);
            }

            // The gateway's own word for it, and the only one of these that is
            // stated rather than inferred.
            if (senderIsOwner == true) return new Sender(SenderKind.Mine, null, text);

            // Typed here. The gateway stamps a message it accepted from this
            // client with "<guid>:user"; an agent's own reply is
            // "cli-assistant:<guid>", which does not end this way and is
            // assistant-role besides.
            if (idempotencyKey is not null
                && idempotencyKey.EndsWith(":user", StringComparison.Ordinal))
            {
                return new Sender(SenderKind.Mine, null, text);
            }

            // Somebody the gateway can name. Not resolved to an agent id on
            // purpose: this is a Discord display name, and the map from those
            // to the agent ids this app colours by is not something the gateway
            // offers. A name with no colour is the honest answer — see the
            // SpeakerColor left null where this is consumed.
            if (!string.IsNullOrWhiteSpace(senderName))
            {
                return new Sender(SenderKind.Named, senderName!.Trim(), text);
            }

            // Nothing said. Exactly the behaviour this app had before any of the
            // above existed, kept as the last resort rather than replaced, so a
            // gateway that stops sending `__openclaw` degrades to the old
            // transcript instead of to a wrong one.
            return new Sender(SenderKind.Unknown, null, text);
        }

        // CB-270: turns the gateway injects as plain user text with no provenance
        // to say so. Exact openings measured on 2026.10.5 on the two mains a
        // WhatsApp DM is routed into, in the same style as Readable's rule for
        // "OpenClaw resumed this CLI session":
        //   * "A scheduled reminder has been triggered" (by far the commonest);
        //   * a `System: [<timestamp>]` line, with or without a zone.
        // Anchored at the start, and case-sensitive, so a person who types the
        // sentence mid-message or in lower case is not swallowed. A different
        // gateway's wording falls through to Unknown, which is the old behaviour
        // and not a wrong one.
        public static bool IsGatewayInjected(string text) =>
            text.StartsWith("A scheduled reminder has been triggered", StringComparison.Ordinal)
            || SystemLine.IsMatch(text);

        private static readonly System.Text.RegularExpressions.Regex SystemLine = new(
            @"^System: \[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}( [A-Z]{2,5})?\]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
