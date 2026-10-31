namespace Orbweaver
{
    // What kind of conversation a gateway session is.
    //
    // Only meaningful for OpenClaw: a local session is always someone at a
    // terminal, so it is Unknown and draws no badge. For a gateway session the
    // distinction is the difference between a room other people can read, a
    // private message, and a scheduled job with nobody on the other end — which
    // the title alone cannot carry, since "Nova — general" and "Nova — wtvamp"
    // are the same shape.
    public enum SessionKind
    {
        Unknown,

        // The agent's own session, reached through the TUI. Every agent has
        // one, so it is deliberately not badged — a mark on almost every orb
        // distinguishes nothing.
        Main,

        Direct,
        Channel,
        Cron,

        // A Claude Code session on another machine, reached over Remote Control.
        // Badged, unlike Main, because it is the exception rather than the rule:
        // almost every orb on screen is local, so the few that aren't are worth
        // marking — clicking one opens a chat instead of jumping to a terminal,
        // and knowing that before you click is the difference between the app
        // feeling consistent and feeling broken.
        Remote,

        // A Claude Code background job: no terminal of its own, run inside a
        // pooled worker the daemon keeps alive between turns, reached with
        // `claude attach` rather than by jumping to a window.
        //
        // In this enum despite having nothing to do with a gateway, because
        // Remote already broke that reading and the badge is drawn from one
        // switch (OrbWindow.BadgeFor). A second kind-of-thing enum with a second
        // badge switch beside it is how one orb ends up wearing two marks, or
        // none — this is the channel that answers "what kind of thing is this",
        // and a background job is an answer to that question.
        //
        // Badged whether the job is working or parked. The badge says what a
        // session *is*, which does not change; whether anything is happening in
        // it rides the orb's opacity instead. See SessionStatus.Parked.
        Background,

        // A Claude Code session running in Anthropic's cloud, listed by the
        // account API (see ClaudeCloudSessions). Here for the reason Background
        // gives above: this is the one channel that answers "what kind of thing
        // is this", and a second enum beside it is how an orb ends up wearing
        // two marks or none.
        //
        // It wears no CLI mark. The cloud badge already says the session is not
        // local, and a Claude spark beside it would read as a local session from
        // across the room — the same argument CliMark makes for Remote.
        Cloud
    }

    // Working out what kind of conversation a gateway session is, from the two
    // things the gateway says about it.
    //
    // Pure and string-valued rather than reaching into JSON, so it can be tested
    // — the same reason OrbArrangement and ChatTranscript are. This one earns it
    // for a reason those don't: the mistake it can make is **silent and
    // directional**. A channel mislabelled as a direct message says a room other
    // people can read is private, and nothing on screen would contradict it.
    public static class OpenClawSessionKind
    {
        // key is "agent:<name>:<surface>[:<type>:<id>]" and chatType is
        // origin.chatType, either of which may be missing.
        //
        // The key is consulted first because it is structural: an
        // "agent:x:cron:<uuid>" cannot be anything but a scheduled job, whatever
        // else is attached to it. chatType is the gateway's own word for a
        // conversation and is the only thing separating a DM from a channel, so
        // it decides wherever the key is uninformative — which it usually is,
        // since "agent:main:discord:…" says only which surface.
        public static SessionKind From(string? key, string? chatType)
        {
            var parts = (key ?? "").Split(':');

            if (parts.Length >= 3 && parts[0] == "agent")
            {
                if (Is(parts[2], "cron")) return SessionKind.Cron;

                // Every agent has one of these and it is reached through the
                // TUI rather than any chat surface, so it is its own kind rather
                // than a direct message.
                if (Is(parts[2], "main")) return SessionKind.Main;

                // CB-270: a New Chat's session, "agent:<id>:dashboard:<uuid>", is
                // the same thing by the same description — the agent's own session,
                // reached through no chat surface. Structural, so a chatType on the
                // row cannot turn it into a direct message. Nothing visible changes:
                // Main and Unknown both wear no badge and both cluster as Chats.
                // What changes is that the kind is now true.
                if (Is(parts[2], "dashboard")) return SessionKind.Main;
            }

            // The key's fourth segment carries the same word when origin is
            // absent: "agent:main:discord:direct:1000…".
            var type = string.IsNullOrWhiteSpace(chatType)
                ? parts.Length >= 4 ? parts[3] : null
                : chatType;

            if (type is null) return SessionKind.Unknown;

            // Unrecognised is Unknown rather than a guess at the commoner of the
            // two. An unbadged orb says "I don't know"; a wrong badge says
            // something false about who can read the conversation.
            if (Is(type, "direct") || Is(type, "dm") || Is(type, "im")) return SessionKind.Direct;
            if (Is(type, "channel") || Is(type, "group") || Is(type, "guild")) return SessionKind.Channel;

            return SessionKind.Unknown;
        }

        // CB-270: the same answer, with the gateway's own classification as a
        // second opinion on the one mistake that matters.
        //
        // A WhatsApp group was measured carrying chatType "direct", top-level and
        // in origin, while its classification, kind and peerKind all said group.
        // chatType decides wherever the key is uninformative, so that row came out
        // Direct, and Direct wears the "direct message" badge: a room other people
        // read, labelled private.
        //
        // Overridden in that one direction only. A Direct answer where the
        // classification says group or channel becomes Channel; nothing else
        // changes, and in particular a classification of "direct" never turns a
        // Channel into Direct. Calling a DM a room is the safe error and calling a
        // room private is the dangerous one, which is the whole reason this does
        // not simply trust classification both ways. A null or unrecognised
        // classification gives exactly the two-argument answer.
        public static SessionKind From(string? key, string? chatType, string? classification)
        {
            var kind = From(key, chatType);

            return kind == SessionKind.Direct
                   && (Is(classification ?? "", "group") || Is(classification ?? "", "channel"))
                ? SessionKind.Channel
                : kind;
        }

        // Which room a channel session is in, as a key stable across restarts
        // and shared by every agent in it: "<surface>:<channel id>".
        //
        // Taken from the session key rather than from origin.label, which is
        // written for a log and varies — the id at the end of
        // "agent:main:discord:channel:1900000000000000001" is the room, and the
        // agent in front of it is who is standing in it.
        //
        // Null for anything that is not a channel, including a DM: two people
        // messaging privately is not a room other agents can join.
        public static string? RoomOf(string? key)
        {
            var parts = (key ?? "").Split(':');

            // agent : <name> : <surface> : <type> : <id>
            if (parts.Length < 5 || parts[0] != "agent") return null;
            if (From(key, null) != SessionKind.Channel) return null;

            var surface = parts[2];
            var id = string.Join(":", parts.Skip(4));

            if (string.IsNullOrWhiteSpace(id)) return null;

            // A key whose channel id is *another session key*. Observed on a
            // real gateway:
            //
            //   agent:main:discord:channel:agent:ea-hope:discord:channel:15389…
            //
            // It is a genuine session and the gateway reports it as a group, but
            // the thing after "channel:" is not a channel — it carries no
            // groupChannel, and treating it as one split #arch into two rooms:
            // the real one, and a second named after the raw id because there
            // was no name to find. Splitting a room in half is worse than not
            // grouping at all, which is the whole point of grouping.
            if (id.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)) return null;

            return $"{surface}:{id}";
        }

        private static bool Is(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
