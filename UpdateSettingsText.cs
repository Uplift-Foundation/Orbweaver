namespace Orbweaver
{
    // The sentences the Settings "Updates" group writes, as pure functions so
    // each arm is a case rather than something to read off a screenshot (CB-308).
    internal static class UpdateSettingsText
    {
        internal static string LastChecked(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc)
        {
            if (lastCheckUtc is not { } last) return "Not checked yet.";

            var ago = nowUtc - last;
            if (ago < TimeSpan.FromMinutes(1)) return "Last checked just now.";
            if (ago < TimeSpan.FromHours(1)) return $"Last checked {(int)ago.TotalMinutes} min ago.";
            if (ago < TimeSpan.FromDays(1)) return $"Last checked {(int)ago.TotalHours} h ago.";
            return $"Last checked {(int)ago.TotalDays} d ago.";
        }

        internal static string Result(UpdateCheckResult result) => result.Outcome switch
        {
            UpdateCheckOutcome.Available => $"Version {result.Release?.Version} is available — see the menu-bar item.",
            UpdateCheckOutcome.Failed => $"Could not check: {result.Error}",
            _ => $"{Brand.DisplayName} is up to date.",
        };
    }
}
