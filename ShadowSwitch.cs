using System;
using System.IO;

namespace Orbweaver
{
    // Whether the local typing gate is in shadow mode (CB-307): it still works
    // out its verdict and logs it, but the send goes ahead whatever the answer.
    //
    // For reading why the gate refuses on a real machine without blocking the
    // person using it while the cause is read. Asked for the same two ways the
    // mirror log is, for the same reason: an environment variable for a
    // terminal, and a marker file named local-gate-shadow in the settings
    // folder for a machine where there is no terminal to set one in. Pure, with
    // the directory, the variable and the file check handed in, so the rule is
    // asserted without touching a disk.
    internal static class ShadowSwitch
    {
        internal const string MarkerFileName = "local-gate-shadow";

        // "0" is the one value that means off, so the variable can be left set
        // in a profile and switched back off without being unset.
        internal static bool IsOn(string? settingsDir, string? envValue, Func<string, bool> fileExists)
        {
            if (!string.IsNullOrWhiteSpace(envValue)) return envValue.Trim() != "0";

            if (string.IsNullOrEmpty(settingsDir)) return false;

            try { return fileExists(Path.Combine(settingsDir, MarkerFileName)); }
            catch { return false; }
        }
    }
}
