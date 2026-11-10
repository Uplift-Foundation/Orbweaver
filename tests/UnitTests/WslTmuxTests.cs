using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// A WSL session's tmux, reached from the Windows app through wsl.exe: how the hook's record is read,
/// how the command is wrapped, and that the typing route is Tmux (and never the Windows console
/// route, which a Linux pid would otherwise point at an unrelated Windows process).
/// </summary>
public class WslTmuxTests
{
    private static SessionStatus Wsl(string pane = "%15", string distro = "Ubuntu", int wslPid = 4242, int sessionPid = 0) => new()
    {
        Source = SessionSource.ClaudeCode, State = "idle", Cwd = "/home/me/proj",
        TmuxPane = pane, TmuxSocket = "/tmp/tmux-1000/default", TmuxBin = "wsl:" + distro, WslPid = wslPid, SessionPid = sessionPid,
    };

    [Theory]
    [InlineData("wsl:Ubuntu", "Ubuntu")]
    [InlineData("wsl:Debian-12", "Debian-12")]
    [InlineData("wsl:", null)]
    [InlineData("/usr/bin/tmux", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_distro_is_what_follows_the_prefix_in_tmux_bin(string? tmuxBin, string? distro) =>
        Assert.Equal(distro, TerminalFocuser.WslDistro(tmuxBin));

    [Fact]
    public void A_wsl_sessions_tmux_command_runs_in_its_distro_through_exec_with_the_socket_pinned()
    {
        Assert.Equal(["-d", "Ubuntu", "-e", "tmux", "-S", "/tmp/tmux-1000/default", "send-keys", "-t", "%15", "-l", "it's \"quoted\" & done"],
            TerminalFocuser.TmuxArgs(Wsl(), "send-keys", "-t", "%15", "-l", "it's \"quoted\" & done"));

        var noSocket = Wsl(); noSocket.TmuxSocket = "";
        Assert.Equal(["-d", "Ubuntu", "-e", "tmux", "capture-pane", "-p", "-t", "%15"], TerminalFocuser.TmuxArgs(noSocket, "capture-pane", "-p", "-t", "%15"));
    }

    [Fact]
    public void A_tmux_on_this_machine_is_unwrapped_exactly_as_before()
    {
        var local = new SessionStatus { TmuxPane = "%3", TmuxSocket = "/tmp/s", TmuxBin = "/opt/homebrew/bin/tmux" };
        Assert.Equal(["-S", "/tmp/s", "list-panes"], TerminalFocuser.TmuxArgs(local, "list-panes"));
    }

    [Fact]
    public void Wsl_exe_is_found_only_on_windows_and_only_where_it_exists()
    {
        Assert.Equal(@"C:\Windows\System32\wsl.exe", TerminalFocuser.WslExe(true, @"C:\Windows\System32", _ => true)?.Replace('/', '\\'));
        Assert.Null(TerminalFocuser.WslExe(true, @"C:\Windows\System32", _ => false));
        Assert.Null(TerminalFocuser.WslExe(false, "/usr/lib", _ => true));
    }

    [Fact]
    public void Off_windows_a_wsl_record_resolves_to_no_tmux_rather_than_the_hosts()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(TerminalFocuser.ResolveTmuxBinary("wsl:Ubuntu"));
    }

    [Fact]
    public void A_wsl_session_in_a_pane_routes_to_tmux_and_a_linux_pid_never_reaches_the_console_route()
    {
        var status = Wsl();
        Assert.Equal(TerminalTyping.Route.Tmux, TerminalTyping.RouteFor(status, false, true, new TerminalTyping.Tools(Tmux: true, Kitty: false, WezTerm: false)));
        // With no wsl.exe the route is not available, and the Linux pid (kept out of session_pid) offers none.
        Assert.Equal(TerminalTyping.Route.None, TerminalTyping.RouteFor(status, false, true, new TerminalTyping.Tools(Tmux: false, Kitty: false, WezTerm: false)));
    }

    [Fact]
    public void The_status_file_carries_the_wsl_pid_and_the_hooks_tmux_record()
    {
        var s = JsonSerializer.Deserialize<SessionStatus>("""{"state":"idle","tmux_pane":"%15","tmux_socket":"/tmp/tmux-1000/default","tmux_bin":"wsl:Ubuntu","wsl_pid":4242,"session_pid":0}""")!;
        Assert.Equal(("%15", "wsl:Ubuntu", 4242, 0), (s.TmuxPane, s.TmuxBin, s.WslPid, s.SessionPid));
        Assert.Equal(0, JsonSerializer.Deserialize<SessionStatus>("""{"state":"idle"}""")!.WslPid);
    }

    [Fact]
    public void Input_into_a_wsl_pane_is_typed_only_when_the_pane_holds_the_sessions_wsl_process()
    {
        var status = Wsl();
        Assert.True(RelayInput.TargetVerified(status, TerminalTyping.Route.Tmux, _ => true));
        Assert.False(RelayInput.TargetVerified(status, TerminalTyping.Route.Tmux, _ => false));
        Assert.False(RelayInput.TargetVerified(status, TerminalTyping.Route.Tmux, _ => null));   // cannot be checked: not typed into
    }

    [Fact]
    public void A_pane_holds_a_wsl_process_that_is_a_descendant_of_the_pane_and_no_other()
    {
        var table = TmuxPaneOwnershipRules.ParseProcessListing("""
              100     1 -zsh
              200   100 claude
              300   200 node tool
              900     1 other
            """);
        Assert.True(TmuxPaneOwnershipRules.Holds(table, 100, 200));
        Assert.False(TmuxPaneOwnershipRules.Holds(table, 100, 900));
        Assert.Null(TmuxPaneOwnershipRules.Holds(table, 100, 0));        // no wsl pid recorded: nothing to judge
    }
}
