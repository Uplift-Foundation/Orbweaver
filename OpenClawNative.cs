namespace Orbweaver
{
    // What kind of *native* session a gateway row is, if it is one at all.
    //
    // A native session is one with no chat surface behind it: nothing to
    // deliver to, nobody on the other end but the user in a client (this app's
    // New Chat, the gateway's own Control UI) and the agent. CB-270 gives those
    // their own orb setting and their own send route, and both need the same
    // answer to "is this one native?", so it is computed once, here, and
    // recorded on the parsed session rather than re-derived by each caller.
    //
    // Measured on a live gateway (2026.10.5, see docs/openclaw-findings.md,
    // "CB-270"): of five `main` rows, three carried no origin.provider, no
    // deliveryContext and no lastChannel at all, and two were WhatsApp DMs
    // routed into the agent's main session. Both are `classification: "main"`,
    // so the classification alone cannot tell them apart. The surface evidence
    // can, and it is what decides here.
    //
    // **Every doubt resolves to None**, which is today's behaviour: the orb is
    // governed by the settings that already exist and a send goes out the way
    // it does now. A wrongly-native session would be pushed (or not pushed) to
    // a real Discord or WhatsApp conversation on the strength of a guess, and
    // that failure is silent and lands in front of other people. A wrongly-None
    // one costs a missing convenience. So any surface evidence at all, a
    // classification this code has not seen, or no classification, means None.
    public enum OpenClawNativeKind
    {
        // Not native, or not known to be. Also what a hand-built session is.
        None,

        // An agent's main session reached through no chat surface.
        Main,

        // A session started from a client's New Chat (`agent:<id>:dashboard:<uuid>`).
        Dashboard,
    }

    public static class OpenClawNative
    {
        public static OpenClawNativeKind Of(
            string? classification, string? originProvider, bool hasDeliveryContext, string? lastChannel)
        {
            if (!string.IsNullOrWhiteSpace(originProvider)
                || hasDeliveryContext
                || !string.IsNullOrWhiteSpace(lastChannel))
                return OpenClawNativeKind.None;

            if (string.Equals(classification, "dashboard", StringComparison.OrdinalIgnoreCase))
                return OpenClawNativeKind.Dashboard;

            if (string.Equals(classification, "main", StringComparison.OrdinalIgnoreCase))
                return OpenClawNativeKind.Main;

            return OpenClawNativeKind.None;
        }
    }
}
