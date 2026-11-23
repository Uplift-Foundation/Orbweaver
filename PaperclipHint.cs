using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // The one-time "Paperclip found" tray row (plan 2.2). Discovery runs once at
    // launch, only while Paperclip is not enabled and the hint has not been
    // used: a folder read and one loopback GET, never on a timer. The row stays
    // until the user uses it (it opens Settings and marks the hint shown) or
    // connects.
    internal static class PaperclipHint
    {
        internal const string Label = "Paperclip found — show your company…";

        private static volatile IReadOnlyList<PaperclipDiscovery.Found> _found = Array.Empty<PaperclipDiscovery.Found>();

        internal static void SetFoundForTests(IReadOnlyList<PaperclipDiscovery.Found> found) => _found = found;

        // Whether the tray should carry the row right now.
        internal static bool ShouldShow =>
            !OrbweaverSettings.PaperclipEnabled && !OrbweaverSettings.PaperclipHintShown && _found.Count > 0;

        internal const string ReconnectLabel = "Reconnect to Paperclip\u2026";

        // The product promise for a refused key is "orbs dim and one line says
        // Reconnect to Paperclip, one click": this is the tray's half of it, so
        // the cue does not live only in Settings and an orb's hover.
        internal static bool ShouldShowReconnect =>
            OrbweaverSettings.PaperclipEnabled && PaperclipSessions.Link == PaperclipLink.NeedsReconnect;

        // Used: the user acted on it, so it does not come back.
        internal static void MarkUsed() => OrbweaverSettings.PaperclipHintShown = true;

        // Runs the discovery for the launch hint. A no-op when it does not apply,
        // so a machine with no Paperclip pays nothing after the first folder read.
        internal static async Task DiscoverAtLaunchAsync(IPaperclipConnector connector, CancellationToken ct)
        {
            if (OrbweaverSettings.PaperclipEnabled || OrbweaverSettings.PaperclipHintShown) return;
            await connector.DiscoverAsync(ct).ConfigureAwait(false);
            _found = connector.Found;
        }

        [ExcludeFromCodeCoverage] // launches the real connector from Program.cs
        internal static void StartAtLaunch() =>
            _ = Task.Run(() => DiscoverAtLaunchAsync(PaperclipConnector.Current, CancellationToken.None));
    }
}
