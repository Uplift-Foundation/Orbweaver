using Avalonia.Threading;

namespace Orbweaver
{
    // The one switch behind "mute" (CB-298): the tray's Mute item, the chat
    // panel's speaker button and the Ctrl+Alt+M hotkey all land here, and
    // nothing else reads the setting directly. Every sound this app makes
    // starts a child process in TextToSpeech or ChimePlayer, and both check
    // IsMuted at the line that builds it — that, not any caller's good
    // manners, is what makes the mute hold.
    internal static class AudioMute
    {
        // The rule, pure, so the decision layers can be tested against it.
        internal static bool Allows(bool muted) => !muted;

        internal static bool IsMuted => OrbweaverSettings.Muted;

        // Raised on the UI thread, with the new state. Subscribers: the tray
        // and every open chat panel.
        internal static event Action<bool>? Changed;

        internal static void Toggle() => Set(!IsMuted);

        internal static void Set(bool muted)
        {
            if (OrbweaverSettings.Muted == muted) return;
            // Persisted first, so a crash in the middle of the stop still
            // comes back up muted rather than half-silenced.
            OrbweaverSettings.Muted = muted;
            if (muted) StopEverythingPlaying();
            Raise(muted);
        }

        // Muting has to cut what is already sounding, not only what comes
        // next. TextToSpeech.Cancel kills the speaking process tree (say,
        // SAPI, the Kokoro side-car or a custom command all assign it) and
        // bumps StopGeneration, which drops a summary still in flight.
        // ChimePlayer.StopPlaying is the non-sticky chime kill.
        internal static void StopEverythingPlaying()
        {
            TextToSpeech.Cancel();
            ChimePlayer.StopPlaying();
        }

        private static void Raise(bool muted)
        {
            if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke(muted);
            else Dispatcher.UIThread.Post(() => Changed?.Invoke(muted));
        }
    }
}
