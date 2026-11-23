namespace Orbweaver
{
    // CB-434: what New chat says while a far machine's Resume list loads, or
    // why it cannot. Pure, so the wording is pinned by tests and the window only
    // paints it.
    internal static class PeerResumeCopy
    {
        internal static string Loading(string machine) => "Asking " + machine + " for its sessions…";

        internal static string NeedsUpdate(string machine) =>
            machine + " needs an Orbweaver update before it can list or resume sessions.";

        internal static string UpdateToResume(string machine) => "Update Orbweaver on " + machine + " to resume sessions there.";

        internal static string Capped(string machine, int shown) =>
            "Showing the newest " + shown.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " sessions on " + machine + ".";

        internal static string Unreachable(string machine, string? why) =>
            "Couldn't reach " + machine + " for its sessions" + (string.IsNullOrWhiteSpace(why) ? "." : " (" + why + ").");

        internal static string Resuming(string machine) => "Resuming on " + machine + "…";

        internal static string Resumed(string machine) => "Resumed on " + machine + " — its orb should appear there.";

        // Codex fires no hook until the first prompt of a resumed session.
        internal static string ResumedCodex(string machine) =>
            "Resumed. The orb appears when you send your first message.";

        internal static string AlreadyRunning(string machine) => "Already running on " + machine + ".";

        internal static string AlreadyRunningFocused(string machine) => "Already running on " + machine + " — focused its orb.";

        internal static string Unsupported(string machine, string cli) =>
            "The " + cli + " on " + machine + " is too old to resume sessions.";

        internal static string Failed(string machine, string? message) =>
            string.IsNullOrWhiteSpace(message) ? "Couldn't resume that session on " + machine + ". Try again." : message!;
    }
}
