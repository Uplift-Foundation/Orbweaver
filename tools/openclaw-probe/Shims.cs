namespace Orbweaver
{
    // OrbweaverSettings is compiled in for its gateway fields, and reaches
    // for a default voice name from each speech engine on the way past. The
    // real ones pull in Whisper, PvRecorder and the platform audio stack, none
    // of which a probe that reads JSON off a socket has any use for.
    //
    // Stand-ins rather than compiling those files: the values are only ever
    // read as a fallback for a setting the probe never touches.
    internal static class TextToSpeech
    {
        public const string DefaultVoice = "";
    }

    internal static class NeuralSpeech
    {
        public const string DefaultVoiceName = "";
    }

    // Only PortToBind's default reaches for this, and the probe never binds a
    // peer listener. The real PeerLink is 800 lines with the whole peer
    // protocol behind it, so the constant is restated rather than dragged in.
    internal static class PeerLink
    {
        public const int DefaultPort = 7677;
    }

    // OrbweaverSettings.SpeakScope is typed against this enum (added by the
    // turn-sounds feature, CB-168 found the probe broken by it again). The
    // real type lives in SpeechSummary.cs, which also drags in ClaudeBinary
    // and InternalSessions — window/process-adjacent, and unrelated to what
    // the probe needs, which is only the two names the setting compares
    // against a string. Restated rather than compiled in, same reasoning as
    // PeerLink above.
    internal enum SpeakScope
    {
        Full,
        Summary,
    }

    // The new-chat settings are keyed by this enum's member names (CB-203).
    // The real one sits in NewChatCommand.cs beside the launcher, which
    // reaches the terminal scripts, the three CLI binaries and the profile
    // scanner — none of it anything a gateway probe calls. Restated, same
    // reasoning as SpeakScope; only the names matter, since that is all the
    // settings file parses.
    internal enum NewChatCli
    {
        ClaudeCode,
        Codex,
        Grok,
    }

    // The two volume settings default and clamp through this. The real class
    // is the platform audio stack (and the speech engine contract under it);
    // the probe reads neither setting, so the default and a clamp to the same
    // 0..1 range are restated rather than compiled in.
    internal static class AudioVolume
    {
        public const double Default = 1.0;

        public static double Clamp(double level) =>
            double.IsNaN(level) ? Default : Math.Clamp(level, 0.0, 1.0);
    }

    // OrbGroupTable (compiled in because OrbweaverSettings now holds a group
    // table) asks OrbArrangement whether a lead chain resolves. The real class
    // is 2,200 lines of Avalonia geometry. The probe never reads or writes an
    // orb group, so this throws rather than restating the rule: a copy that
    // answered would be a second implementation that could drift unseen.
    internal static class OrbArrangement
    {
        internal static bool Resolves(int start, int[] leadOf, int count) =>
            throw new System.NotSupportedException("The probe does not arrange orbs.");
    }
}
