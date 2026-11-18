using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Turns a policy verdict into the process that carries it out, the way
    // CloudSessionLink.StartInfoFor does for a browser (CB-363).
    //
    // The path it is handed is a local, already-decoded path — either a file
    // the session named on this machine, or the cached copy of one fetched
    // from another. Everything is argv, never a shell string, so a name with
    // spaces or quotes in it is one argument and nothing is interpreted.
    internal static class FileLinkLauncher
    {
        // Null means nothing should be started.
        //
        // The policy is run again here because this is the last stop before a
        // process: a caller that decided Open for one path and passes another,
        // or a future caller that skips the decision, still cannot open a
        // program. Facts are not re-read — a launch that has just decided on
        // them is not made safer by a second stat that could disagree — so what
        // is re-checked is everything that needs only the path: its shape, the
        // platform it belongs to, and the extension allowlist. Open is downgraded
        // to Reveal for an extension off the list rather than refused, since the file
        // is real and showing it is exactly what the policy would have said.
        internal static ProcessStartInfo? StartInfoFor(FileLinkVerdict verdict, string path, bool isMacOs, bool isWindows)
        {
            if (verdict.Action == FileLinkAction.Refuse) return null;
            if (!isMacOs && !isWindows) return null;

            var link = FileLink.FromDecodedPath(path);
            if (link is null || link.Value.IsWindowsPath != isWindows) return null;

            var local = link.Value.Path;
            if (isWindows && FileLinkPolicy.HasTrailingDotOrSpace(local)) return null;

            var reveal = verdict.Action == FileLinkAction.Reveal || !FileLinkPolicy.IsAllowedExtension(local);

            if (isMacOs)
            {
                var info = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
                if (reveal) info.ArgumentList.Add("-R");

                // "--" ends option parsing (confirmed against /usr/bin/open on
                // macOS), so a name like "-a" can never be read as a flag. The
                // path is always rooted at "/" here, which already cannot start
                // with a dash; the terminator is the belt for that suspender.
                info.ArgumentList.Add("--");
                info.ArgumentList.Add(local);
                return info;
            }

            if (reveal)
            {
                var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
                info.ArgumentList.Add("/select," + local);
                return info;
            }

            // ShellExecute on the path itself: the default handler for the
            // file's type, exactly as a double-click would do.
            return new ProcessStartInfo { FileName = local, UseShellExecute = true };
        }

        // Decides and launches for a local path on this machine; the seam
        // behind ChatLinks.OpenFile. Returns true when something was started.
        internal static bool Plan(string path, FileFacts facts, bool isMacOs, bool isWindows, out ProcessStartInfo? info, out FileLinkVerdict verdict)
        {
            var link = FileLink.FromDecodedPath(path);
            verdict = link is null
                ? FileLinkVerdict.Refuse("That is not a path this computer can open.")
                : FileLinkPolicy.Decide(link.Value, facts, isWindows);

            info = StartInfoFor(verdict, path, isMacOs, isWindows);
            return info is not null;
        }

        // Excluded from coverage: what is left is Process.Start, which opens a
        // real application or a Finder window on whichever machine runs it,
        // plus the disk probe that Plan's tests feed by hand. Everything it
        // starts was decided by Plan, which is pure and covered on both
        // platforms' shapes.
        [ExcludeFromCodeCoverage]
        internal static bool OpenLocal(string path)
        {
            if (!Plan(path, FileFacts.Probe(path), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), out var info, out _))
                return false;

            try
            {
                using var process = Process.Start(info!);
                return process is not null;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
