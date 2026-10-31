namespace Orbweaver
{
    // What the tray icon should be, as pure functions of the session counts and
    // the mute (CB-298) — so the rule is a line a test can point at rather than
    // an expression inside UpdateIcon, which needs a TrayIcon to run.
    internal static class TrayIconLook
    {
        // "idle" | "generating" | "waiting". The rule UpdateIcon used to carry
        // inline: someone waiting on you outranks work in progress.
        internal static string State(int waiting, int generating) =>
            waiting > 0 ? "waiting" : generating > 0 ? "generating" : "idle";

        // The asset for a state. The muted variant is the same single-colour
        // alpha mask with a slash through it, so the runtime re-tint keeps
        // working on it and the state's colour still says what the sessions
        // are doing.
        internal static string AssetName(string state, bool muted) =>
            muted ? $"tray-{state}-muted" : $"tray-{state}";
    }
}
