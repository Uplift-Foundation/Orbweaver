namespace Orbweaver
{
    // CB-357: the words the New Chat window says about a launch on another
    // machine. Pure, so every refusal code has a case in a unit test and no
    // window has to be built to read one.
    //
    // The wording is the phone's own explanation of a refusal, with "the desk"
    // replaced by the machine's name and "this phone's" by "this machine's":
    // the same failure should read the same whichever device asked. The three
    // codes the phone never sees (peer_launch_off, no_answer, link_lost) are
    // PeerLaunchCodes, and are phrased in the same voice.
    internal static class PeerLaunchCopy
    {
        // Shown under the CLI list for OpenClaw when a remote machine is
        // chosen. OpenClaw conversations are the gateway's, which only this
        // machine talks to.
        internal const string OpenClawLocalOnly = "OpenClaw conversations start from this machine only.";

        // The wire name of a CLI as a person reads it. Unknown names pass
        // through rather than vanish, so a CLI added later still reads sensibly.
        internal static string CliName(string cli) => cli switch
        {
            "claude" => "Claude Code",
            "codex" => "Codex",
            "grok" => "Grok",
            _ => cli,
        };

        internal static string Asking(string machine) => "Asking " + machine + "…";

        internal static string NoFolder(string machine) => machine + " reported no folder to start in.";

        internal static string NoCli(string machine) => "No supported CLI is installed on " + machine + ".";

        internal static string Starting(string machine) => "Starting on " + machine + "…";

        internal static string Launched(string cli, string machine) =>
            CliName(cli) + " is starting on " + machine + ". Its orb appears when " + machine + " next reports its sessions.";

        // CB-397: the trust question the far machine put to this desktop.
        internal static string TrustAsking(string cli, string machine) =>
            CliName(cli) + " on " + machine + " is asking whether to trust this folder.";

        internal static string TrustTitle(string machine) => "Trust this folder on " + machine + "?";

        internal static string TrustWarning(string cli, string machine) =>
            CliName(cli) + " will be able to read, edit and run files there, and the folder's own hooks will run on " + machine + ".";

        internal static string Trusting(string machine) => "Trusting it on " + machine + "…";

        internal static string TrustCancelled(string machine) =>
            "Cancelled. Nothing was trusted, and " + machine + " closed the session.";

        internal static string TrustCancelledUnconfirmed(string machine) =>
            "Cancelled here. " + machine + " didn't confirm, and closes the session itself within two minutes.";

        internal static string TrustExpired(string machine) =>
            "Nobody answered in time, so " + machine + " closed the session. Nothing was trusted.";

        // What an answer to the question came back as. An answer that finds
        // no question (gone or timed out) reads as expired; a Cancel nobody
        // confirmed says so; everything else is a launch's own sentence.
        internal static string AfterAnswer(PeerLaunchResult result, bool trusted, string cli, string machine)
        {
            if (result.Ok) return Launched(cli, machine);
            if (result.Code is PeerLaunchCodes.TrustGone or RelayReplies.Expired) return TrustExpired(machine);
            if (!trusted)
            {
                if (result.Code == PeerLaunchCodes.TrustCancelled) return TrustCancelled(machine);
                if (result.Code is PeerLaunchCodes.NoAnswer or PeerLaunchCodes.LinkLost) return TrustCancelledUnconfirmed(machine);
            }
            return Explain(result.Code, result.Why, cli, machine);
        }

        // CB-357: why a far machine's folder listing was refused, in the same
        // voice as Explain. The browse codes are the far desk's own (the phone's
        // list_dir codes); the peer ones fall through to Explain.
        internal static string ExplainBrowse(string code, string machine) => code switch
        {
            RelayReplies.ForbiddenPath => machine + " doesn't share that folder.",
            RelayReplies.UnknownPath => "That folder doesn't exist on " + machine + ".",
            RelayReplies.Unreadable => machine + " could not read that folder.",
            RelayReplies.RateLimited => machine + " is busy. Try again in a moment.",
            _ => Explain(code, null, "claude", machine),
        };

        // One sentence per refusal code. The phone's own explanations (its
        // ClientFrames.explain), with "the desk" replaced by the machine's name,
        // so the same failure reads the same on whichever device asked. `why` is
        // the far desk's own sentence where it sends one (no_terminal and
        // launch_failed), and it is the useful part. unknown_account is the one
        // the phone has no copy for; the last three are peer-only.
        internal static string Explain(string code, string? why, string cli, string machine)
        {
            var w = string.IsNullOrWhiteSpace(why) ? "no reason given" : why;
            return code switch
            {
                PeerLaunchCodes.PeerLaunchOff => machine + " doesn't allow other desktops to start sessions. Turn that on in its Settings.",
                PeerLaunchCodes.NoAnswer => machine + " didn't answer. It may be asleep, or running a version that can't start sessions for another desktop.",
                // This desktop's own failure: nothing was sent, so it must not read
                // as the far machine refusing.
                PeerLaunchCodes.CannotSign => "This machine couldn't sign the request, so nothing was sent.",
                PeerLaunchCodes.LinkLost => "The link to " + machine + " dropped before it answered.",
                RelayReplies.BadFrame => machine + " could not read that request.",
                RelayReplies.BadInput => "That message can't be typed.",
                RelayReplies.BadSignature => machine + " could not verify this machine's signature. Nothing was started.",
                RelayReplies.Expired => "That launch timed out. Nothing was started.",
                RelayReplies.Replayed => "That launch was already sent once.",
                RelayReplies.UnknownCli => CliName(cli) + " isn't installed on " + machine + ".",
                RelayReplies.UnknownCwd => "That folder doesn't exist on " + machine + ".",
                RelayReplies.UnknownAccount => machine + " doesn't have that account.",
                RelayReplies.BadModel => "That model name can't be passed to the CLI.",
                RelayReplies.UnsupportedPersona => "Personas come from the folder in this version.",
                RelayReplies.UnknownParent => "That lead session is gone.",
                RelayReplies.UnsupportedParent => "Only Claude Code sessions can join a team.",
                RelayReplies.NoTerminal => "No terminal to start it in (" + w + ").",
                RelayReplies.LaunchFailed => machine + " could not start it (" + w + ").",
                RelayReplies.NoSession => "It started but never checked in. Open " + machine + " to see why.",
                _ => machine + " refused it (" + code + ").",
            };
        }
    }
}
