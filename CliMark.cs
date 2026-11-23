namespace Orbweaver
{
    // The little CLI mark an orb wears so Claude, Codex, Grok and OpenClaw
    // read apart from across the room.
    //
    // A coloured disc with a white glyph, not a letter: the orb's own initials
    // already occupy the letter channel, and a "C" on a Claude orb would be
    // the session's name half the time. Colour is the long-range signal
    // (terracotta / green / black / lobster-red); the glyph is what you get
    // when you look closer. Bottom-left, because the other three corners are
    // already spoken for (presence, heart, kind).
    //
    // Hidden for remote-control orbs: those are Claude Code on another
    // machine, the kind badge already says they are not local, and a Claude
    // spark on a remote session would look like a local one from across the
    // room. OpenClaw keeps the lobster even when it also has a kind badge —
    // kind says cron/channel/direct, the lobster says it is OpenClaw.
    internal readonly record struct CliMarkStyle(string Name, string FillHex, string GlyphPath);

    internal static class CliMark
    {
        // 22 DIP, against the kind badge's 16. The kind glyphs are 8–9pt
        // punctuation and were sized for that; a logo at 16px is the size the
        // user asked us not to ship. 22 is big enough to read from the other
        // side of the desk and still leaves the centred initials alone.
        internal const double Size = 22;

        // The white Path inside the disc. Scaled with the disc on a team
        // member so it does not overflow the smaller badge.
        internal const double GlyphSize = 14;

        // Anthropic terracotta, a four-point spark. The spark is the mark
        // Claude Code's own UI uses; the colour is what you see first.
        internal static readonly CliMarkStyle Claude = new(
            "claude",
            "#D97757",
            "M8,1.2 L9.15,6.55 L14.8,8 L9.15,9.45 L8,14.8 L6.85,9.45 L1.2,8 L6.85,6.55 Z");

        // OpenAI green, a six-point star. Distinct from Claude's four-point
        // spark at a glance, and green-on-black is the pair Codex's own TUI
        // already sits on.
        internal static readonly CliMarkStyle Codex = new(
            "codex",
            "#10A37F",
            "M8,1.4 L8.95,6.2 L13.8,4.2 L10.1,8 L13.8,11.8 L8.95,9.8 L8,14.6 "
            + "L7.05,9.8 L2.2,11.8 L5.9,8 L2.2,4.2 L7.05,6.2 Z");

        // Near-black, a heavy X. Grok/xAI's mark is an X; at 22px a four-point
        // star rotated 45° is the same idea and does not collapse into Claude's
        // spark.
        internal static readonly CliMarkStyle Grok = new(
            "grok",
            "#1A1A1A",
            "M3.2,2.4 L8,7.2 L12.8,2.4 L13.6,3.2 L8.8,8 L13.6,12.8 L12.8,13.6 "
            + "L8,8.8 L3.2,13.6 L2.4,12.8 L7.2,8 L2.4,3.2 Z");

        // Cooked-lobster red, a top-down lobster: antennae, claws, body, tail.
        // Saturated enough that it does not collapse into Claude's terracotta
        // from across the room, which is the whole reason this disc exists.
        internal static readonly CliMarkStyle OpenClaw = new(
            "openclaw",
            "#E23A2B",
            "M7.2,3.8 L4.8,0.7 L5.6,0.5 L7.8,3.6 Z "
            + "M8.8,3.8 L11.2,0.7 L10.4,0.5 L8.2,3.6 Z "
            + "M4.4,5.0 C2.2,3.8 0.5,5.2 0.8,7.0 C1.1,8.4 2.8,8.8 4.4,7.8 "
            + "C3.6,6.8 3.8,5.8 4.4,5.0 Z "
            + "M11.6,5.0 C13.8,3.8 15.5,5.2 15.2,7.0 C14.9,8.4 13.2,8.8 11.6,7.8 "
            + "C12.4,6.8 12.2,5.8 11.6,5.0 Z "
            + "M8,4.2 C10.6,4.2 11.4,7.2 10.8,10.4 C10.2,12.2 8,13.0 8,13.0 "
            + "C8,13.0 5.8,12.2 5.2,10.4 C4.6,7.2 5.4,4.2 8,4.2 Z "
            + "M5.4,11.6 L3.8,14.4 L8,13.4 L12.2,14.4 L10.6,11.6 Z");

        // Paperclip indigo, a paperclip: the outline of one wire bent into an
        // outer loop and an inner one, drawn as a single filled path (the stroke
        // converted to its outline, 1.6 wide) so it scales with the disc like
        // every other glyph here. Worn by every Paperclip orb, asleep or not:
        // the mark says what the agent IS (a Paperclip agent, not a terminal
        // session) and the orb's dimming says whether it is running (CB-421).
        internal static readonly CliMarkStyle Paperclip = new(
            "paperclip",
            "#5B5BD6",
            "M11.2,10 V5 A3.2,3.2 0 0 0 4.8,5 V11.5 A1.7,1.7 0 0 0 8.2,11.5 V6 H9.8 "
            + "V11.5 A3.3,3.3 0 0 1 3.2,11.5 V5 A4.8,4.8 0 0 1 12.8,5 V10 Z");

        internal static CliMarkStyle? For(SessionSource source) => source switch
        {
            SessionSource.Paperclip => Paperclip,
            SessionSource.ClaudeCode => Claude,
            SessionSource.Codex => Codex,
            SessionSource.Grok => Grok,
            SessionSource.OpenClaw => OpenClaw,
            _ => null
        };

        // Every account source is a CLI, so this is never null — unlike
        // SessionSource, which still has remote-control orbs with no mark.
        internal static CliMarkStyle For(AccountUsageSource source) => source switch
        {
            AccountUsageSource.Codex => Codex,
            AccountUsageSource.Grok => Grok,
            _ => Claude
        };
    }
}
