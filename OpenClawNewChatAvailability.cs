namespace Orbweaver
{
    // Whether the OpenClaw slot in the new-chat dialog can be used at all, and
    // why not when it can't.
    //
    // Pure and settings-free on purpose, the same argument OrbArrangement and
    // OpenClawSessionKind already make: NewChatWindow's radio list reads this
    // to decide whether the OpenClaw entry is a live choice or a disabled one
    // with a reason under it, and a test of that decision should not need a
    // live settings singleton or a socket to exercise every branch.
    //
    // CB-168's probe (docs/openclaw-findings.md) confirmed sessions.create
    // needs the same operator.write scope this app already requests once
    // "Allow replying to agents" is on — so there is no third scope to ask
    // for, and this is exactly the same gate OpenClawChatSession.SendAsync
    // already applies before it will send a reply.
    public enum OpenClawNewChatAvailability
    {
        Ready,
        NoGateway,
        ReplyDisabled
    }

    public static class OpenClawNewChat
    {
        // host is OrbweaverSettings.OpenClawHost, which is "" rather than
        // null when unset — checked with IsNullOrWhiteSpace rather than a
        // null check for that reason.
        public static OpenClawNewChatAvailability AvailabilityFor(
            bool enabled, string? host, bool replyEnabled)
        {
            if (!enabled || string.IsNullOrWhiteSpace(host))
            {
                return OpenClawNewChatAvailability.NoGateway;
            }

            return replyEnabled
                ? OpenClawNewChatAvailability.Ready
                : OpenClawNewChatAvailability.ReplyDisabled;
        }

        // CB-329: starting a conversation needs the reply switch (sessions.create
        // is operator.write); reopening one only reads, so it needs a gateway and
        // nothing more. ReplyDisabled is the one state where the two differ.
        public static bool CanStartNew(OpenClawNewChatAvailability availability) =>
            availability == OpenClawNewChatAvailability.Ready;

        public static bool CanOpenExisting(OpenClawNewChatAvailability availability) =>
            availability != OpenClawNewChatAvailability.NoGateway;

        // What the disabled radio item's subtitle says. Null for Ready, since
        // a live entry has nothing to explain. Sentence case with a closing
        // full stop, the same as the three local CLIs' own disabled reasons
        // (NewChatAvailability.NotFoundReasonSuffix) — CB-168's dialog now
        // states every reason on screen, not only in a tooltip, so the four
        // rows' reasons need to read consistently next to each other.
        public static string? ReasonFor(OpenClawNewChatAvailability availability) =>
            availability switch
            {
                OpenClawNewChatAvailability.NoGateway => "No gateway configured.",
                OpenClawNewChatAvailability.ReplyDisabled =>
                    "Turn on \"Allow replying to agents\" in Settings.",
                _ => null
            };
    }
}
