namespace Orbweaver
{
    // CB-275: the half of the audio support that decides whether a path names
    // a voice note or a song. Dependency-free for the reason OpenClawVideoKind
    // is: ChatTurn.IsAudio (RemoteChat.cs) reads it, and tests/TranscriptTests
    // compiles RemoteChat.cs in by hand.
    internal static class OpenClawAudio
    {
        internal static readonly string[] Extensions =
            { ".mp3", ".m4a", ".aac", ".ogg", ".wav", ".flac", ".opus" };

        internal static bool HasAudioExtension(string text) =>
            Array.Exists(Extensions, ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // By extension alone, like OpenClawVideo.IsVideoPath: the path is set
        // with the route by every producer, so the kind cannot drift from it.
        internal static bool IsAudioPath(string? path) =>
            !string.IsNullOrEmpty(path) && HasAudioExtension(path!);
    }
}
