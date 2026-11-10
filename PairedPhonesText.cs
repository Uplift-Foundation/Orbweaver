namespace Orbweaver
{
    // The words Settings › Paired phones uses (CB-314), pure so each one is a
    // unit test rather than something read off a screenshot.
    internal static class PairedPhonesText
    {
        internal static string Relay(RelayClient.Status state, string? lastError) => state switch
        {
            RelayClient.Status.Connected => "Connected to the relay.",
            RelayClient.Status.Registering => "Registering this computer with the relay…",
            RelayClient.Status.Refused => $"The relay refused this version of {Brand.DisplayName}. Update to pair phones.",
            RelayClient.Status.Connecting when lastError is not null => $"Reconnecting to the relay — last attempt: {lastError}",
            RelayClient.Status.Connecting => "Connecting to the relay…",
            _ => "Not connected to the relay.",
        };

        // "Online", or how long ago the relay last heard from it. Rounded to
        // what a person would say — nobody needs "last seen 437 seconds ago".
        internal static string Phone(bool online, DateTimeOffset? lastSeen, DateTimeOffset now)
        {
            if (online) return "Online";
            if (lastSeen is not { } seen) return "Offline";
            var ago = now - seen;
            if (ago < TimeSpan.FromMinutes(1)) return "Last seen just now";
            if (ago < TimeSpan.FromHours(1)) return $"Last seen {(int)ago.TotalMinutes} min ago";
            if (ago < TimeSpan.FromDays(1)) return $"Last seen {(int)ago.TotalHours} h ago";
            return $"Last seen {seen.ToLocalTime():d MMM}";
        }
    }
}
