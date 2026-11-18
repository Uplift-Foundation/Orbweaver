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

        internal static string Starting(string machine) => "Starting on " + machine + "…";

        internal static string Launched(string cli, string machine) =>
            CliName(cli) + " is starting on " + machine + ". Its orb appears when " + machine + " next reports its sessions.";

        // The reason Start is off after the machine's launch-info came back
        // without a usable answer. Null when the info is fine.
        internal static string Explain(string code, string? why, string cli, string machine)
        {
            var name = CliName(cli);
            return code switch
            {
                PeerLaunchCodes.PeerLaunchOff => machine + " doesn't allow other desktops to start sessions. Turn that on in its Settings.",
                PeerLaunchCodes.NoAnswer => machine + " didn't answer. It may be asleep, or running a version that can't start sessions for another desktop.",
                PeerLaunchCodes.LinkLost => "The link to " + machine + " dropped before it answered.",
                RelayReplies.BadSignature => machine + " didn't accept this machine's signature. Pair the two desktops again.",
                RelayReplies.Expired => "That request took too long to reach " + machine + ". Check both clocks and try again.",
                RelayReplies.Replayed => machine + " has already seen that request. Try again.",
                RelayReplies.UnknownCli => name + " isn't installed on " + machine + ".",
                RelayReplies.UnknownCwd => machine + " doesn't have that folder, or won't start sessions in it.",
                RelayReplies.UnknownAccount => machine + " doesn't have that account.",
                RelayReplies.NoTerminal => machine + " has no terminal to start " + name + " in. On a Mac, that means tmux isn't installed.",
                RelayReplies.LaunchFailed => machine + " couldn't start " + name + ".",
                RelayReplies.NoSession => name + " started on " + machine + " but never reported in.",
                RelayReplies.BadFrame or RelayReplies.BadInput => machine + " couldn't read the request.",
                RelayReplies.BadModel => machine + " doesn't know that model.",
                _ => machine + " refused: " + (string.IsNullOrWhiteSpace(why) ? code : why) + ".",
            };
        }
    }
}
