using System;
using System.Globalization;

namespace Orbweaver
{
    // The words a file link says about itself (CB-363): the hover tooltip, the
    // "Fetching…" line and every refusal. Pure strings in, strings out, so each
    // sentence is a unit case rather than something only a synthesized click
    // can reach.
    //
    // Refusal codes arrive as bare tokens from two places: the serving peer
    // (forbidden_path, not_found, too_large, rate_limited, busy, unreadable)
    // and this client (update, failed, and the policy's own refusals). Anything
    // not recognised gets a generic sentence rather than the raw token, because
    // a code the next peer version adds is a string a person should never have
    // to read.
    internal static class ChatFileLinkText
    {
        // "the machine" is the fallback when the roster has not named it yet:
        // naming the wrong machine is worse than not naming one.
        internal static string MachineOr(string? name) =>
            string.IsNullOrWhiteSpace(name) ? "that machine" : name!;

        // Path first, then where it lives — the destination has to be
        // readable for what it is before anyone clicks it.
        internal static string Tooltip(string path, ChatFileHost host, string? machine) =>
            host == ChatFileHost.Peer
                ? $"{path}\non {MachineOr(machine)}"
                : $"{path}\non this computer";

        internal static string FileName(string path)
        {
            // Both separators on either OS: a path from a Windows peer is read
            // on a Mac and the other way round, and Path.GetFileName only
            // splits on the host's own.
            var cut = path.LastIndexOfAny(new[] { '/', '\\' });
            var name = cut >= 0 ? path[(cut + 1)..] : path;
            return name.Length == 0 ? path : name;
        }

        // "Fetching x.pdf from MacMini… 42%". No percentage until the total is
        // known: a bar that guessed would sit at 99% and look stuck.
        internal static string Fetching(string path, string? machine, long done, long total)
        {
            var line = $"Fetching {FileName(path)} from {MachineOr(machine)}…";
            if (total <= 0) return line;

            var pct = (int)Math.Clamp(done * 100 / total, 0, 100);
            return line + " " + pct.ToString(CultureInfo.InvariantCulture) + "%";
        }

        // The sentence for a refusal, chosen by where the file lives as well as
        // by the code: a file on this computer must never be blamed on "that
        // machine". A local session has no peer to be busy, rate limited or
        // out of date, so those codes fall to the generic line there.
        internal static string Refusal(string? code, string? machine, ChatFileHost host = ChatFileHost.Peer) =>
            host == ChatFileHost.ThisMachine ? LocalRefusal(code) : PeerRefusal(code, machine);

        private static string LocalRefusal(string? code) => code switch
        {
            "forbidden_path" => "That file is outside the folders Orbweaver opens from chat.",
            "not_found" or "missing" => "That file no longer exists.",
            "unreadable" => "That file couldn't be read.",
            "refused" => "That kind of link can't be opened.",
            _ => "Couldn't open that file."
        };

        private static string PeerRefusal(string? code, string? machine) => code switch
        {
            "update" => $"Update Orbweaver on {MachineOr(machine)} to open files from it.",
            "forbidden_path" => $"{MachineOr(machine)} doesn't share that folder.",
            "not_found" => $"That file no longer exists on {MachineOr(machine)}.",
            "too_large" => "That file is too large to open from here (50 MB limit).",
            "rate_limited" => "Too many file requests — try again in a moment.",
            "busy" => $"{MachineOr(machine)} is still sending another file — try again in a moment.",
            "unreadable" => $"{MachineOr(machine)} couldn't read that file.",
            "failed" => $"Couldn't fetch the file from {MachineOr(machine)}.",
            "refused" => "That kind of link can't be opened.",
            "missing" => "That file no longer exists.",
            _ => "Couldn't open that file."
        };

        // The note for a deny-listed or executable file that was revealed
        // instead of opened, so a click that shows a Finder window is not
        // mistaken for one that did nothing.
        internal static string Revealed(string path) =>
            $"{FileName(path)} isn't opened from chat — it's shown in your file manager instead.";
    }
}
