namespace Orbweaver
{
    // CB-272: the half of OpenClawVideo that decides whether a path names a
    // clip, split into its own file because it has no dependencies at all.
    // ChatTurn.IsVideo (RemoteChat.cs) reads it, and tests/TranscriptTests
    // compiles RemoteChat.cs in by hand with <Compile Include>; the rest of
    // OpenClawVideo reaches OpenClawSessions, the socket and OpenClawMedia
    // through its seams, which would pull most of the app into a console
    // suite that exists to stay small. Same class, partial, so every caller
    // still says OpenClawVideo.IsVideoPath.
    internal static partial class OpenClawVideo
    {
        internal static readonly string[] Extensions = { ".mp4", ".mov", ".webm", ".m4v" };

        internal static bool HasVideoExtension(string text) =>
            Array.Exists(Extensions, ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // Whether a path names a video, by extension alone. This is what
        // ChatTurn.IsVideo reads off ImageSourcePath, rather than a flag every
        // producer and every one of the turn-copying sites would have to carry
        // beside the path: the path is already set with the route by each
        // producer, so the kind cannot drift from it.
        internal static bool IsVideoPath(string? path) =>
            !string.IsNullOrEmpty(path) && HasVideoExtension(path!);
    }
}
