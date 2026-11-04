using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbweaver
{
    // Where the hooks leave a session's status file, and where Orbweaver
    // looks for it.
    //
    // **These have to be the same directory, and on a launchd-started Mac they
    // were not.** The hooks run from the user's shell, where `TMPDIR` is set to
    // the per-user temp directory macOS gives every login session. Orbweaver
    // asks .NET for `Path.GetTempPath()`, which on Unix reads `TMPDIR` and falls
    // back to `/tmp` when it is unset — and a launchd agent's environment does
    // not carry `TMPDIR` at all. `launchctl print` on the mini listed none, and
    // `/tmp/claude_buddy` did not exist.
    //
    // So a Buddy started by launchd read an empty directory, drew no orbs, and
    // answered every roster request from every other machine with "no sessions
    // here" — while two live Claude Code sessions sat in the real directory the
    // whole time. Nothing failed; the two halves were simply looking in
    // different places.
    //
    // That is the exact deployment the direct link exists for: a headless Mac,
    // always on, started by launchd, serving its sessions to the machine
    // somebody is actually sitting at.
    internal static class StatusDirectory
    {
        internal const string FolderName = Brand.StatusFolderName;

        // The folder the pre-Orbweaver hooks write, and the one the app goes
        // on watching beside FolderName (CB-255 §1). Claude Code and Codex
        // read their hooks at session start, so a session that was alive
        // across the upgrade keeps calling the old script, and the old script
        // keeps writing here until that session restarts. Not watching it
        // would be "Orbweaver shows no orbs" for every session that predates
        // the install — the canonical silent failure, on upgrade day.
        //
        // Same root as FolderName, always: the old hook and the new one agree
        // with the app about TMPDIR exactly as before, and only the last path
        // segment moved. Goes with the rest of Brand.Legacy in the cleanup
        // after phase 3, and not sooner than two releases after phase 2.
        internal const string LegacyFolderName = Brand.Legacy.StatusFolderName;

        // Both folders under one root, new first. Pure, so the "same root"
        // rule above is a test rather than a promise: Path() and LegacyPath()
        // are each this, applied to the real root.
        internal static (string Current, string Legacy) Under(string root) =>
            (System.IO.Path.Combine(root, FolderName), System.IO.Path.Combine(root, LegacyFolderName));

        // The name macOS gives the per-user temp directory, and what `TMPDIR`
        // holds in any ordinary shell. Asked of the C library rather than
        // guessed, because it contains a per-boot, per-user token.
        private const int DarwinUserTempDir = 65537;

        [DllImport("libc", SetLastError = true)]
        private static extern ulong confstr(int name, StringBuilder buf, ulong len);

        // Which temp root to use, given what the environment says and what the
        // platform can tell us.
        //
        // Pure so every arm is a test rather than a launchd job. The order is
        // the point.
        //
        // `statusRoot` is ORBWEAVER_STATUS_ROOT, and it exists so a test can
        // get its own status directory **without moving TMPDIR**, which is the
        // same seam ORBWEAVER_SETTINGS_DIR and ORBWEAVER_PROFILE_ROOT
        // already are for their own directories. Moving TMPDIR looks like the
        // cheaper trick and is not: it is process-wide, so it reaches every
        // other thing in the process that asks the OS for a temp path —
        // including tooling that was handed the old value before the test
        // assembly was ever loaded. CB-172 is what that cost. See the four
        // TestBootstrap.cs files for the failure it produced.
        //
        // The platform's per-user directory outranks TMPDIR, and that is the
        // CB-306 correction to the agreement with the hooks. TMPDIR used to
        // win "because that is where the hooks write", which held in a login
        // shell and not under OpenClaw: its gateway runs with
        // TMPDIR=~/.openclaw/tmp, every Claude Code session it spawns inherits
        // that — as does a tmux server started from one, and every session in
        // that tmux — so those hooks wrote to ~/.openclaw/tmp/orbweaver while
        // this process, with the login TMPDIR, read /var/folders/.../T/. The
        // mini served no sessions to any peer with five alive. The launchd
        // story above, from the other side: an environment variable any
        // parent can set is not a channel two processes with different
        // parents can agree through. confstr reads no environment at all,
        // which is what makes it one; the hook asks the same question through
        // `getconf DARWIN_USER_TEMP_DIR`. TMPDIR remains for a platform that
        // cannot answer, and the manual second-instance sandbox is
        // ORBWEAVER_STATUS_ROOT, which both halves honour first.
        //
        // The P/Invoke now runs on every call rather than only when TMPDIR is
        // unset. It is one confstr(3) into a caller-supplied buffer —
        // microseconds — and skipping it is exactly the bug.
        internal static string Root(
            string? statusRoot, string? tmpdir, Func<string?> perUser, string fallback) =>
            !string.IsNullOrWhiteSpace(statusRoot) ? statusRoot!
            : perUser() is { Length: > 0 } mine ? mine
            : !string.IsNullOrWhiteSpace(tmpdir) ? tmpdir!
            : fallback;

        // Excluded from coverage: reads the process environment and calls libc.
        // What it decides is Root, which is pure.
        [ExcludeFromCodeCoverage]
        internal static string Path0() => Root(
            BrandEnv.Get(BrandEnv.StatusRoot),
            Environment.GetEnvironmentVariable("TMPDIR"),
            PerUserTemp,
            System.IO.Path.GetTempPath());

        // Excluded from coverage: a P/Invoke whose answer differs per machine
        // and per boot.
        [ExcludeFromCodeCoverage]
        private static string? PerUserTemp()
        {
            if (!OperatingSystem.IsMacOS()) return null;

            try
            {
                var buffer = new StringBuilder(1024);
                var written = confstr(DarwinUserTempDir, buffer, 1024);

                return written is > 0 and <= 1024 ? buffer.ToString() : null;
            }
            catch
            {
                // Any failure here means falling back to what .NET said, which
                // is the behaviour this replaces rather than something worse.
                return null;
            }
        }

        // The directory itself.
        [ExcludeFromCodeCoverage]
        internal static string Path() => Under(Path0()).Current;

        // The folder pre-Orbweaver hooks write — see LegacyFolderName.
        [ExcludeFromCodeCoverage]
        internal static string LegacyPath() => Under(Path0()).Legacy;
    }
}
