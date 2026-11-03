namespace Orbweaver
{
    // Which OpenClaw sessions are exempt from "Keep orbs for" and the active-
    // within window (CB-270).
    //
    // Those two settings are clocks measuring silence, and CB-182's argument
    // about cloud sessions applies to a gateway conversation that is a durable
    // server-side resource rather than a process that may have exited: a clock
    // there measures the wrong thing. The conversation's lifecycle is the
    // gateway's own `archived` flag (End archives it), not how long it has been
    // quiet.
    //
    // Two things qualify:
    //   * a session the gateway says is pinned. That is the user's explicit,
    //     gateway-held signal "keep this", set from any client, and it is
    //     honoured for every session, a Discord DM pinned in the Control UI
    //     included;
    //   * a dashboard session (a New Chat). Not every native one: native mains
    //     are heartbeat targets the gateway keeps forever, so exempting them
    //     would be permanent orbs nobody asked for.
    //
    // An archived session is still dropped: Parse's archived skip sits above the
    // recency rule this feeds.
    internal static class OpenClawRetention
    {
        internal static bool KeptRegardlessOfAge(bool gatewayPinned, OpenClawNativeKind native) =>
            gatewayPinned || native == OpenClawNativeKind.Dashboard;
    }
}
