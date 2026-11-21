using Xunit;

namespace Orbweaver.Tests;

public class PeerLaunchCopyTests
{
    [Theory]
    [InlineData("peer_launch_off", "mini doesn't allow other desktops to start sessions. Turn that on in its Settings.")]
    [InlineData("no_answer", "mini didn't answer. It may be asleep, or running a version that can't start sessions for another desktop.")]
    [InlineData("cannot_sign", "This machine couldn't sign the request, so nothing was sent.")]
    [InlineData("link_lost", "The link to mini dropped before it answered.")]
    [InlineData("bad_frame", "mini could not read that request.")]
    [InlineData("bad_input", "That message can't be typed.")]
    [InlineData("bad_signature", "mini could not verify this machine's signature. Nothing was started.")]
    [InlineData("expired", "That launch timed out. Nothing was started.")]
    [InlineData("replayed", "That launch was already sent once.")]
    [InlineData("unknown_cli", "Codex isn't installed on mini.")]
    [InlineData("unknown_cwd", "That folder doesn't exist on mini.")]
    [InlineData("unknown_account", "mini doesn't have that account.")]
    [InlineData("bad_model", "That model name can't be passed to the CLI.")]
    [InlineData("unsupported_persona", "Personas come from the folder in this version.")]
    [InlineData("unknown_parent", "That lead session is gone.")]
    [InlineData("unsupported_parent", "Only Claude Code sessions can join a team.")]
    [InlineData("no_terminal", "No terminal to start it in (no reason given).")]
    [InlineData("launch_failed", "mini could not start it (no reason given).")]
    [InlineData("no_session", "It started but never checked in. Open mini to see why.")]
    [InlineData("weird", "mini refused it (weird).")]
    public void Each_code_reads_as_its_own_sentence(string code, string expected) =>
        Assert.Equal(expected, PeerLaunchCopy.Explain(code, null, "codex", "mini"));

    [Theory]
    [InlineData("no_terminal", "No terminal to start it in (tmux isn't installed).")]
    [InlineData("launch_failed", "mini could not start it (tmux isn't installed).")]
    public void The_far_desks_own_sentence_is_carried_where_the_code_has_one(string code, string expected) =>
        Assert.Equal(expected, PeerLaunchCopy.Explain(code, "tmux isn't installed", "claude", "mini"));

    [Fact]
    public void A_blank_why_reads_as_no_reason_given() =>
        Assert.Equal("mini could not start it (no reason given).", PeerLaunchCopy.Explain("launch_failed", "  ", "claude", "mini"));

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
        Assert.Equal("mini reported no folder to start in.", PeerLaunchCopy.NoFolder("mini"));
        Assert.Equal("No supported CLI is installed on mini.", PeerLaunchCopy.NoCli("mini"));
        Assert.Equal("OpenClaw conversations start from this machine only.", PeerLaunchCopy.OpenClawLocalOnly);
    }
}

// CB-397: the words of the trust question, one case each.
public class PeerLaunchTrustCopyTests
{
    [Fact]
    public void The_question_reads_the_same_whichever_cli_asked()
    {
        Assert.Equal("Claude Code on mini is asking whether to trust this folder.", PeerLaunchCopy.TrustAsking("claude", "mini"));
        Assert.Equal("Trust this folder on mini?", PeerLaunchCopy.TrustTitle("mini"));
        Assert.Equal("Codex will be able to read, edit and run files there, and the folder's own hooks will run on mini.", PeerLaunchCopy.TrustWarning("codex", "mini"));
        Assert.Equal("Trusting it on mini…", PeerLaunchCopy.Trusting("mini"));
    }

    [Theory]
    [InlineData(true, "launched", null, true, "Claude Code is starting on mini. Its orb appears when mini next reports its sessions.")]
    [InlineData(false, "trust_gone", null, true, "Nobody answered in time, so mini closed the session. Nothing was trusted.")]
    [InlineData(false, "expired", null, false, "Nobody answered in time, so mini closed the session. Nothing was trusted.")]
    [InlineData(false, "trust_cancelled", null, false, "Cancelled. Nothing was trusted, and mini closed the session.")]
    [InlineData(false, "no_answer", null, false, "Cancelled here. mini didn't confirm, and closes the session itself within two minutes.")]
    [InlineData(false, "link_lost", null, false, "Cancelled here. mini didn't confirm, and closes the session itself within two minutes.")]
    [InlineData(false, "no_answer", null, true, "mini didn't answer. It may be asleep, or running a version that can't start sessions for another desktop.")]
    [InlineData(false, "launch_failed", "gone", true, "mini could not start it (gone).")]
    public void An_answer_reads_as_one_sentence(bool ok, string code, string? why, bool trusted, string expected)
    {
        Assert.Equal(expected, PeerLaunchCopy.AfterAnswer(new PeerLaunchResult(ok, code, null, why), trusted, "claude", "mini"));
    }
}
