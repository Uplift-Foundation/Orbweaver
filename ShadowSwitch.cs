using System;
using System.IO;

namespace Orbweaver
{
    // Whether the typing gate only logs (CB-307): it still works out its
    // verdict and logs it, but the send goes ahead whatever the answer. The one
    // switch both gates read — the local panel's and the mirror's remote typing
    // paths (TypingGate.GuardedTypeAsync) — on the machine doing the typing.
    //
    // **Log-only is the default.** The lead's decision, told to Warren: the
    // guard ships watching, not blocking, on every machine, and enforcing is a
    // follow-up once real logs show it makes no wrong calls. A real refusal
    // with no way around it is what started this ticket's last two rounds.
    //
    // How each machine says otherwise, strongest first:
    //
    //  1. The environment variable (ORBWEAVER_LOCAL_GATE_SHADOW, through
    //     BrandEnv). "0" enforces; any other non-blank value is log-only. It
    //     wins over both marker files, so it can be left in a profile and
    //     flipped without touching the settings folder.
    //  2. The `local-gate-shadow` marker in the settings folder: log-only. It
    //     meant that before the default flipped, and still does, so a machine
    //     that has one installed is unchanged. With both markers present it
    //     wins — when the two disagree, the one that cannot block anyone does.
    //  3. The `local-gate-enforce` marker in the settings folder: enforce.
    //     Opt-in, for a terminal-less machine (the mini under launchd) where
    //     there is no profile to set a variable in.
    //  4. Nothing set: log-only.
    //
    // Pure, with the directory, the variable and the file check handed in, so
    // the rule is asserted without touching a disk. A file check that throws
    // counts as the marker not being there.
    internal static class ShadowSwitch
    {
        internal const string MarkerFileName = "local-gate-shadow";
        internal const string EnforceMarkerFileName = "local-gate-enforce";

        internal static bool IsOn(string? settingsDir, string? envValue, Func<string, bool> fileExists)
        {
            if (!string.IsNullOrWhiteSpace(envValue)) return envValue.Trim() != "0";

            if (string.IsNullOrEmpty(settingsDir)) return true;

            if (Exists(fileExists, Path.Combine(settingsDir, MarkerFileName))) return true;
            if (Exists(fileExists, Path.Combine(settingsDir, EnforceMarkerFileName))) return false;

            return true;
        }

        private static bool Exists(Func<string, bool> fileExists, string path)
        {
            try { return fileExists(path); }
            catch { return false; }
        }
    }
}
