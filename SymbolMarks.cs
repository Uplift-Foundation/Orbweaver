namespace Orbweaver
{
    // The small monochrome marks this app draws rather than types, as SVG path
    // data in a 16x16 box — the shape CliMark's marks take, stretched uniformly
    // into whatever square the control gives them.
    //
    // Why drawn at all (CB-171, CB-173): every one of these has a Unicode
    // character, and every one of those characters reaches the screen through
    // font fallback, which picks a different face per platform. Measured with
    // the same SKFontManager.MatchCharacter call Avalonia's TryMatchCharacter
    // makes, on the real Windows box and on a Mac: U+23F1, U+2699, U+2601,
    // U+23F9 and U+2328 all land on Segoe UI Emoji on Windows, a colour font,
    // so the glyph arrives at its own size and in its own colours and ignores
    // the Foreground the control asked for. macOS gives the same characters a
    // monochrome face (STIX Two Math, Menlo, Hiragino Sans). U+21C4 went to
    // Segoe UI Symbol on the box and to Segoe UI Emoji on the CI runner, which
    // is the other reason not to trust a character: which face wins varies by
    // machine as well as by platform. A path is drawn in the Fill it is given,
    // at the size it is given, on both.
    //
    // "The character resolves" was never the test — CB-171's cloud resolved
    // fine on both platforms and still drew nothing a person would read as a
    // cloud. The test is what reaches the screen at 13px, in the colour asked
    // for, on both rids; tests/UiScreenshots captures each of these so the two
    // can be compared side by side.
    //
    // Every mark is fat on purpose. At 13px one unit of this box is about 0.8
    // of a pixel, so a stroke under two units is a hairline that antialiasing
    // turns to grey, and a gap under one unit closes up. They are designed at
    // that size rather than shrunk to it.
    //
    // Pure strings and pure functions of an enum, so the unit suite can hold
    // them without a render interface. Parsing needs one, which is why the parse-and-bounds
    // check lives in tests/UiTests.
    internal static class SymbolMarks
    {
        // Three lobes and a flat base. At 13px a cloud with fine edges is the
        // white lump this replaced (CB-171), so the lobes are large enough to
        // survive two or three pixels each.
        internal const string Cloud =
            "M4.6,13 C2.3,13 0.5,11.3 0.5,9.2 C0.5,7.4 1.8,5.9 3.5,5.5 "
            + "C4.0,3.5 5.9,2 8.2,2 C10.4,2 12.2,3.4 12.8,5.3 "
            + "C14.3,5.7 15.5,7.1 15.5,8.8 C15.5,11.1 13.7,13 11.4,13 Z";

        // A stopwatch: a thick ring, the crown on top, a side button at
        // forty-five degrees, and one hand pointing at twelve. One hand rather
        // than two because a second one at this size merges with the first
        // into a blob in the middle of the dial, and the crown is what makes it
        // a stopwatch rather than a clock at all.
        //
        // Nonzero fill (the F1 prefix), with the dial's inner edge wound the
        // other way to punch the hole: the crown overlaps the ring and the hand
        // sits inside the hole, and under even-odd both overlaps would cut
        // holes of their own instead of filling.
        internal const string Stopwatch =
            "F1 "
            // ring, outer edge clockwise
            + "M8,3 A6.5,6.5 0 1 1 8,16 A6.5,6.5 0 1 1 8,3 Z "
            // ring, inner edge counter-clockwise
            + "M8,5.2 A4.3,4.3 0 1 0 8,13.8 A4.3,4.3 0 1 0 8,5.2 Z "
            // stem and cap of the crown
            + "M6.9,1.4 L9.1,1.4 L9.1,3.6 L6.9,3.6 Z "
            + "M5.4,0 L10.6,0 L10.6,1.8 L5.4,1.8 Z "
            // side button
            + "M11.9,4.0 L13.5,2.4 L15.1,4.0 L13.5,5.6 Z "
            // the hand, from the centre up to twelve
            + "M7.1,6.4 L8.9,6.4 L8.9,10.3 L7.1,10.3 Z";

        // A gear: eight chunky teeth on a disc with a hole through the hub.
        // Eight because six reads as a nut and ten closes the gaps between
        // teeth at 13px; the teeth taper slightly so they read as teeth rather
        // than as a stop sign. Even-odd, so the hub circle is the hole.
        //
        // The same mark on the background-job badge, the flyout's settings
        // button and the chat panel's attach button — the attach button's own
        // comment says the gear is there to match the badge, which only holds
        // if they are the same drawing.
        internal const string Gear =
            "M13.25,6.74 L15.49,6.69 L15.49,9.31 L13.25,9.26 L12.60,10.82 "
            + "L14.22,12.37 L12.37,14.22 L10.82,12.60 L9.26,13.25 L9.31,15.49 "
            + "L6.69,15.49 L6.74,13.25 L5.18,12.60 L3.63,14.22 L1.78,12.37 "
            + "L3.40,10.82 L2.75,9.26 L0.51,9.31 L0.51,6.69 L2.75,6.74 "
            + "L3.40,5.18 L1.78,3.63 L3.63,1.78 L5.18,3.40 L6.74,2.75 "
            + "L6.69,0.51 L9.31,0.51 L9.26,2.75 L10.82,3.40 L12.37,1.78 "
            + "L14.22,3.63 L12.60,5.18 Z "
            + "M10.5,8 A2.5,2.5 0 1 0 5.5,8 A2.5,2.5 0 1 0 10.5,8 Z";

        // Two arrows going opposite ways, the top one right and the bottom one
        // left — U+21C4's own arrangement, and the "it is somewhere else and it
        // answers" every sync icon means. Shafts two units thick and heads six
        // tall, with a unit of clear space between the two heads so they do not
        // merge into one bar at 13px.
        internal const string Arrows =
            "M0.5,3.4 L9.5,3.4 L9.5,1.5 L15.5,4.5 L9.5,7.5 L9.5,5.6 L0.5,5.6 Z "
            + "M15.5,10.4 L6.5,10.4 L6.5,8.5 L0.5,11.5 L6.5,14.5 L6.5,12.6 L15.5,12.6 Z";

        // A keyboard, for the flyout's "open the chat" button: a rounded body
        // with a frame, a row of three keys and a space bar. Even-odd, so the
        // body's inner edge is a hole and the keys inside it fill again. At
        // least a unit and a half of clear space around every key and a space
        // bar two units tall, because the first draft had less of both and at
        // 14px its top row fused onto the frame while the space bar vanished.
        internal const string Keyboard =
            "M1.5,2 L14.5,2 A1,1 0 0 1 15.5,3 L15.5,13 A1,1 0 0 1 14.5,14 "
            + "L1.5,14 A1,1 0 0 1 0.5,13 L0.5,3 A1,1 0 0 1 1.5,2 Z "
            + "M2,3.5 L14,3.5 L14,12.5 L2,12.5 Z "
            + "M3.5,5 L5.5,5 L5.5,7 L3.5,7 Z "
            + "M7,5 L9,5 L9,7 L7,7 Z "
            + "M10.5,5 L12.5,5 L12.5,7 L10.5,7 Z "
            + "M4.5,8.7 L11.5,8.7 L11.5,10.7 L4.5,10.7 Z";

        // The speak button's "press again to stop" square. A square is the one
        // shape here a font could hardly get wrong in outline, and U+23F9 still
        // lands on Segoe UI Emoji on Windows, so it is the emoji face's colours
        // and size rather than the white mark the button asks for.
        internal const string Stop =
            "M2.5,2.5 L13.5,2.5 L13.5,13.5 L2.5,13.5 Z";

        // The heartbeat heart, on the orb's badge and in the chat panel's chip.
        // U+2665 is no colour emoji on Windows — it resolves to plain Segoe UI
        // and takes the pink it is given — but Segoe UI's heart is a much
        // smaller glyph than the Hiragino Sans one macOS draws at the same
        // point size, so the win-x64 capture showed a heart about two thirds
        // the size of the osx-arm64 one in an identical badge. "The colour and
        // the size the badge specifies" is the bar, so it is drawn too.
        //
        // Two lobes and a point, with a shallow notch: a deep notch closes up
        // at 11px and the heart reads as a blob, which is the failure the
        // cloud's lumps were designed against too.
        internal const string Heart =
            "M8,14.5 C8,14.5 0.8,10.1 0.8,5.4 C0.8,3.1 2.6,1.4 4.8,1.4 "
            + "C6.2,1.4 7.4,2.2 8,3.3 C8.6,2.2 9.8,1.4 11.2,1.4 "
            + "C13.4,1.4 15.2,3.1 15.2,5.4 C15.2,10.1 8,14.5 8,14.5 Z";

        // What the speak button wears in each state: a character, or a drawn
        // mark, never both. Shared by OrbFlyout and ChatPanel, which used to
        // carry the same switch twice.
        //
        // Only the stop square is drawn. The speaker and the hourglass are
        // emoji-presentation characters that come out in colour on *both*
        // platforms (Apple Color Emoji, Segoe UI Emoji), which is the look
        // they were chosen for, so they agree with each other already. U+23F9
        // was the odd one out: a monochrome STIX glyph on macOS and a colour
        // emoji on Windows.
        internal static (string? Glyph, string? Mark) SpeakLook(TextToSpeech.SpeakState state) => state switch
        {
            TextToSpeech.SpeakState.Speaking => (null, Stop),
            TextToSpeech.SpeakState.Preparing => ("\u23F3", null),
            _ => ("\U0001F508", null)
        };

        // What the mute button wears (CB-298): the speaker with a stroke
        // through it when everything is muted, the plain loud speaker when
        // it is not. Both are colour emoji on both platforms, the same family
        // SpeakLook chose its speaker from, so no drawn mark is needed and the
        // two buttons sit side by side without disagreeing about style.
        internal static string MuteLook(bool muted) => muted ? "\U0001F507" : "\U0001F50A";

        // The mute button's tooltip. The combo is whatever is really bound
        // (HotkeyActions.Bound), or null when nothing registered, so the
        // tooltip never names a chord that would do nothing.
        internal static string MuteTip(bool muted, HotkeyCombo? combo)
        {
            var verb = muted ? "Unmute" : "Mute";
            return combo is { } c ? $"{verb} \u2014 {HotkeyRegistry.Format(c)}" : verb;
        }
    }
}
