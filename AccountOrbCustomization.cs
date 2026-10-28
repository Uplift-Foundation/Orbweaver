using System;

namespace Orbweaver
{
    // CB-287: the one write path for usage-orb overrides. The orb's right-click
    // editor and the Settings rows both call this and nothing else, so they
    // cannot disagree about clipping, trimming or what "clear" means, and
    // Changed is raised from one place.
    internal static class AccountOrbCustomization
    {
        // Static and shared by the whole process: a test that subscribes must
        // unsubscribe. Argument is the position key that was written.
        internal static event Action<string>? Changed;

        internal static AccountOrbOverride? Current(string positionKey) =>
            string.IsNullOrEmpty(positionKey) ? null : OrbweaverSettings.AccountOrbOverrideFor(positionKey);

        // Stored clipped to what the setting allows (two graphemes, always — the
        // two-letter *display* setting is applied at draw time so flipping it
        // does not lose what was typed). Blank clears.
        internal static void SetInitials(string positionKey, string? initials)
        {
            var clipped = OrbGlyph.Clip(initials, twoLetter: true);
            Write(positionKey, o => o with { Initials = clipped.Length == 0 ? null : clipped });
        }

        internal static void SetName(string positionKey, string? name) =>
            Write(positionKey, o => o with { Name = name });

        // Import, swap and delete-the-old-one arrive with AccountOrbIcons.Import.
        internal static IconImport SetIcon(string positionKey, string sourcePath) =>
            new(null, IconImportError.UnsupportedFormat);

        internal static void RemoveIcon(string positionKey) =>
            Write(positionKey, o => o with { IconFile = null });

        internal static void Reset(string positionKey)
        {
            if (string.IsNullOrEmpty(positionKey)) return;
            OrbweaverSettings.SetAccountOrbOverride(positionKey, null);
            Changed?.Invoke(positionKey);
        }

        internal static AccountOrbLook Preview(AccountOrbIdentity account) =>
            AccountOrbLooks.Resolve(
                account.DerivedLabel, Current(account.PositionKey), OrbweaverSettings.TwoLetterGlyphs);

        private static void Write(string positionKey, Func<AccountOrbOverride, AccountOrbOverride> edit)
        {
            if (string.IsNullOrEmpty(positionKey)) return;
            var next = edit(Current(positionKey) ?? new AccountOrbOverride(null, null, null));
            OrbweaverSettings.SetAccountOrbOverride(positionKey, next);
            Changed?.Invoke(positionKey);
        }
    }

    internal enum IconImportError { Unreadable, Empty, TooLarge, NotAnImage, UnsupportedFormat }

    internal sealed record IconImport(string? FileName, IconImportError? Error);
}
