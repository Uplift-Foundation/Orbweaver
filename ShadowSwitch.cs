using System;
using System.IO;

namespace Orbweaver
{
    // Whether the typing gate only logs (CB-307): it still works out its
    // verdict and logs it, but the send goes ahead whatever the answer. The one
    // switch both gates read — the local panel's and the mirror's remote typing
    // paths (TypingGate.GuardedTypeAsync) — on the machine doing the typing.
    //
    // **Enforcing is the default (CB-403).** It shipped log-only, and the log
    // it produced was the evidence against that: on the mini a refusal
    // (`check=no-box-top shadow=1`) was followed by `-> ok via=typed`, twice —
    // text typed into a pane that was not at its prompt, reported as sent, the
    // sender's panel showing nothing wrong. A guard that logs and types anyway
    // protects nobody, so with nothing set the gate refuses.
    //
    // How a machine says otherwise, strongest first:
    //
    //  1. The environment variable (ORBWEAVER_LOCAL_GATE_SHADOW, through
    //     BrandEnv). "0" enforces; any other non-blank value is log-only. It
    //     wins over the marker file, so it can be left in a profile and
    //     flipped without touching the settings folder.
    //  2. The `local-gate-shadow` marker in the settings folder: log-only, the
    //     explicit opt-out for a machine with no profile to set a variable in.
    //  3. Nothing set: enforce. The `local-gate-enforce` marker is what
    //     opting in used to take; it is redundant now and harmless, kept so a
    //     machine that has one is unchanged.
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

            if (string.IsNullOrEmpty(settingsDir)) return false;

            // The enforce marker is not read: it and "nothing set" are the same
            // answer now, and shadow wins if both files are present.
            return Exists(fileExists, Path.Combine(settingsDir, MarkerFileName));
        }

        private static bool Exists(Func<string, bool> fileExists, string path)
        {
            try { return fileExists(path); }
            catch { return false; }
        }
    }
}
