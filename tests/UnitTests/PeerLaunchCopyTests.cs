using Xunit;

namespace Orbweaver.Tests;

public class PeerLaunchCopyTests
{
    [Theory]
    [InlineData("peer_launch_off", "mini doesn't allow other desktops to start sessions. Turn that on in its Settings.")]
    [InlineData("no_answer", "mini didn't answer. It may be asleep, or running a version that can't start sessions for another desktop.")]
    [InlineData("link_lost", "The link to mini dropped before it answered.")]
    [InlineData("bad_signature", "mini didn't accept this machine's signature. Pair the two desktops again.")]
    [InlineData("expired", "That request took too long to reach mini. Check both clocks and try again.")]
    [InlineData("replayed", "mini has already seen that request. Try again.")]
    [InlineData("unknown_cli", "Codex isn't installed on mini.")]
    [InlineData("unknown_cwd", "mini doesn't have that folder, or won't start sessions in it.")]
    [InlineData("unknown_account", "mini doesn't have that account.")]
    [InlineData("no_terminal", "mini has no terminal to start Codex in. On a Mac, that means tmux isn't installed.")]
    [InlineData("launch_failed", "mini couldn't start Codex.")]
    [InlineData("no_session", "Codex started on mini but never reported in.")]
    [InlineData("bad_frame", "mini couldn't read the request.")]
    [InlineData("bad_input", "mini couldn't read the request.")]
    [InlineData("bad_model", "mini doesn't know that model.")]
    public void Each_code_reads_as_its_own_sentence(string code, string expected) =>
        Assert.Equal(expected, PeerLaunchCopy.Explain(code, null, "codex", "mini"));

    [Fact]
    public void An_unknown_code_falls_back_to_why_then_to_the_code()
    {
        Assert.Equal("mini refused: out of disk.", PeerLaunchCopy.Explain("weird", "out of disk", "claude", "mini"));
        Assert.Equal("mini refused: weird.", PeerLaunchCopy.Explain("weird", " ", "claude", "mini"));
        Assert.Equal("mini refused: weird.", PeerLaunchCopy.Explain("weird", null, "claude", "mini"));
    }

    [Theory]
    [InlineData("claude", "Claude Code")]
    [InlineData("codex", "Codex")]
    [InlineData("grok", "Grok")]
    [InlineData("other", "other")]
    public void Cli_names(string wire, string shown) => Assert.Equal(shown, PeerLaunchCopy.CliName(wire));

    [Fact]
    public void Progress_and_success_lines_name_the_machine()
    {
        Assert.Equal("Asking mini…", PeerLaunchCopy.Asking("mini"));
        Assert.Equal("Starting on mini…", PeerLaunchCopy.Starting("mini"));
        Assert.Equal("Claude Code is starting on mini. Its orb appears when mini next reports its sessions.",
            PeerLaunchCopy.Launched("claude", "mini"));
        Assert.Equal("mini has no terminal to start a session in. On a Mac, that means tmux isn't installed.", PeerLaunchCopy.NoTerminal("mini"));
        Assert.Equal("No supported CLI is installed on mini.", PeerLaunchCopy.NoCli("mini"));
        Assert.Equal("OpenClaw conversations start from this machine only.", PeerLaunchCopy.OpenClawLocalOnly);
    }
}
