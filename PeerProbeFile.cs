using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Orbweaver
{
    // The pure half of `peer-probe ... read_file` (CB-363): reading its
    // arguments and saying what a FILE reply was, in lines a person or a script
    // can compare. The probe itself only dials, calls
    // RemoteMirrorClient.FetchFileAsync — the shipped client, not a copy — and
    // writes the bytes, so everything with a decision in it lives here where a
    // unit case can reach it.
    //
    // It exists because the E2E needs the negative control as much as the
    // positive: an outcome code, size and checksum are what show that
    // forbidden_path came back for a path outside the roots, and that the bytes
    // that crossed are the bytes on the far disk.
    internal static class PeerProbeFile
    {
        internal readonly record struct Request(string Session, string Path, string? OutPath, string? Have);

        internal const string Usage =
            "read_file <session-name> <absolute-path> [--out <file>] [--have <mtimeMs-size>]\n" +
            "  fetches one file the session could have linked to, prints the outcome code,\n" +
            "  size and sha256, and writes the bytes to --out (default: a file in the temp\n" +
            "  directory named by the checksum). --have is a stamp from an earlier fetch;\n" +
            "  when the far file still matches, the answer is 'same' and no bytes cross.";

        // Arguments after "read_file". Null request means a usage error,
        // with the reason.
        internal static (Request? Request, string? Error) Parse(IReadOnlyList<string> args)
        {
            var positional = new List<string>();
            string? outPath = null;
            string? have = null;

            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] is "--out" or "--have")
                {
                    if (i + 1 >= args.Count) return (null, $"{args[i]} needs a value");

                    if (args[i] == "--out") outPath = args[++i];
                    else have = args[++i];
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            if (positional.Count != 2) return (null, "read_file takes a session name and a path");

            return (new Request(positional[0], positional[1], outPath, have), null);
        }

        // One line per fact, key=value, so a script can grep it and a person
        // can read it. The path is deliberately not echoed: the probe's output
        // is pasted into tickets, and a file name is somebody's content.
        internal static IReadOnlyList<string> Describe(PeerFileResult result) => result switch
        {
            PeerFileResult.Bytes b =>
            [
                "outcome=ok",
                $"size={b.Size}",
                $"mtime={b.MtimeMs}",
                $"exec={(b.Exec ? 1 : 0)}",
                $"stamp={b.MtimeMs}-{b.Size}",
                $"sha256={Sha256(b.Data)}"
            ],
            PeerFileResult.Same => ["outcome=same"],
            PeerFileResult.Error e => e.Reason is null
                ? [$"outcome=err", $"code={e.Code}"]
                : [$"outcome=err", $"code={e.Code}", $"reason={e.Reason}"],
            _ => ["outcome=unknown"]
        };

        // Zero for bytes or "same", one for a refusal — so `peer-probe ... ||`
        // reads the way the E2E wants: a forbidden_path negative control
        // expects non-zero.
        internal static int ExitCodeOf(PeerFileResult result) => result is PeerFileResult.Error ? 1 : 0;

        internal static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        // Where the bytes go when --out is not given.
        internal static string DefaultOutPath(string tempDirectory, byte[] data) =>
            System.IO.Path.Combine(tempDirectory, "peer-probe-" + Sha256(data)[..12]);
    }
}
