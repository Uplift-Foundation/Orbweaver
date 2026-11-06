using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // The tray's "Check for updates…" / "Update to <v> available…" item, split
    // out of TrayController so its label and its choice of action are plain
    // functions a test can call (CB-308). TrayController owns only the
    // NativeMenuItem and the signature gate that makes the menu rebuild when
    // PendingVersion changes — without that term in Apply()'s signature the
    // label would never move off "Check for updates…".
    internal static class UpdateMenu
    {
        // Test seams: the real answers come from UpdateChecker, which a headless
        // run has no instance of.
        internal static Func<UpdateRelease?>? PendingForTests;
        internal static Action? CheckForTests;
        internal static Action<UpdateRelease>? OfferForTests;

        internal static UpdateRelease? Pending() =>
            PendingForTests is { } seam ? seam() : UpdateServices.Checker?.Pending;

        internal static string? PendingVersion() => Pending()?.Version;

        internal static string Label(string? pendingVersion) =>
            pendingVersion is null ? "Check for updates…" : $"Update to {pendingVersion} available…";

        // One click, one of two things: open the notice for what is waiting, or
        // go and look. Which one is decided at click time rather than when the
        // item was built, because a check that finished since the menu was
        // drawn should win over a label that has not caught up yet.
        internal static void Activate()
        {
            var pending = Pending();
            if (pending is not null)
            {
                (OfferForTests ?? Offer)(pending);
                return;
            }

            (CheckForTests ?? CheckNow)();
        }

        [ExcludeFromCodeCoverage]
        private static void Offer(UpdateRelease release) =>
            UpdateServices.Offer(release);

        // Excluded: reaches GitHub. The manual check always reports, including
        // "up to date" and a failure, which is UpdateChecker's own contract and
        // covered there; what this adds is only the call.
        [ExcludeFromCodeCoverage]
        private static void CheckNow() => _ = UpdateServices.CheckNowAsync();
    }
}
