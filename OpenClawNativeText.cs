namespace Orbweaver
{
    // Every user-visible word the native-sessions setting uses, in one place so
    // a rename is one edit (CB-270).
    //
    // "Agents' own sessions" rather than "Direct sessions": SessionKind.Direct is
    // a Discord or WhatsApp DM and its badge says "direct message", so a user who
    // set "Direct sessions: Hidden" would expect their DMs to vanish and they
    // would not. The tip says plainly what is not covered, for the same reason.
    internal static class OpenClawNativeText
    {
        internal const string SettingLabel = "Agents' own sessions";

        internal const string ShapeLabel = "Agents' own sessions shape";

        internal const string GroupName = "Agents' own sessions";

        internal const string SettingTip =
            "An agent's main session and chats started from Orbweaver — not its Discord or WhatsApp conversations.";

        internal const string ShapeTip =
            "The shape these orbs gather into when they are drawn on their own. Discord and WhatsApp conversations, DMs included, are not affected.";
    }
}
