using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-476: the console a Windows launch started, as a CB-397 pane. The process
/// table, the read, the press and the kill are fakes, so nothing here touches a
/// real console or process.
/// </summary>
public class WindowsConsolePaneTests
{
    private static WindowsConsolePane.Row R(int pid, int parent, string exe) => new(pid, parent, exe);

    [Theory]
    [InlineData("con:42", 42)]
    [InlineData("con:2147483647", 2147483647)]
    [InlineData("%1", null)]
    [InlineData("con:", null)]
    [InlineData("con:0", null)]
    [InlineData("con:-4", null)]
    [InlineData("con:+4", null)]
    [InlineData("con: 4", null)]
    [InlineData("con:4x", null)]
    [InlineData("CON:4", null)]
    [InlineData(null, null)]
    public void Host_reads_only_a_console_pane_with_a_positive_pid(string? pane, int? host)
    {
        Assert.Equal(host, WindowsConsolePane.Host(pane));
    }

    [Fact]
    public void For_round_trips_through_Host()
    {
        Assert.Equal("con:7", WindowsConsolePane.For(7));
        Assert.Equal(7, WindowsConsolePane.Host(WindowsConsolePane.For(7)));
    }

    [Fact]
    public void The_client_is_the_hosts_first_child_that_is_not_a_conhost()
    {
        var table = new[]
        {
            R(1, 0, "System"), R(10, 1, "conhost.exe"),
            R(11, 10, "ConHost.EXE"), R(10, 10, "cmd.exe"), R(0, 10, "Idle"),
            R(12, 10, "cmd.exe"), R(13, 10, "claude.exe"), R(14, 12, "claude.exe"),
        };
        Assert.Equal(12, WindowsConsolePane.Client(10, table));
        Assert.Null(WindowsConsolePane.Client(99, table));
        Assert.Null(WindowsConsolePane.Client(10, []));
    }

    [Fact]
    public void A_console_look_routes_to_the_windows_console_by_the_clients_pid()
    {
        var status = WindowsConsolePane.StatusFor(12);
        Assert.Equal(12, status.SessionPid);
        Assert.Equal(TerminalTyping.Route.WindowsConsole,
            TerminalTyping.RouteFor(status, onMacOS: false, onWindows: true, new TerminalTyping.Tools(false, false, false)));
    }

    private sealed class Fake
    {
        public WindowsConsolePane.Row[] Table = [R(10, 1, "conhost.exe"), R(12, 10, "cmd.exe")];
        public readonly List<int> Read = [];
        public readonly List<(int Pid, string Key)> Pressed = [];
        public readonly List<int> Killed = [];
        public string? Screen = "screen";

        public LaunchRunner.TrustSeams Seams() => WindowsConsolePane.Seams(
            () => Table,
            s => { Read.Add(s.SessionPid); return Task.FromResult(Screen); },
            (s, key, approved) =>
            {
                if (!approved(Screen)) return Task.FromResult((false, Screen));
                Pressed.Add((s.SessionPid, key));
                return Task.FromResult((true, Screen));
            },
            Killed.Add);
    }

    [Fact]
    public async Task ReadPane_reads_the_client_of_the_host_the_pane_names()
    {
        var f = new Fake();
        Assert.Equal("screen", await f.Seams().ReadPane("con:10"));
        Assert.Equal([12], f.Read);
    }

    [Theory]
    [InlineData("%1")]
    [InlineData("con:11")]
    public async Task ReadPane_is_null_without_touching_a_console_for_a_tmux_pane_or_a_host_with_no_client(string pane)
    {
        var f = new Fake();
        Assert.Null(await f.Seams().ReadPane(pane));
        Assert.Empty(f.Read);
    }

    [Fact]
    public async Task CaptureAndPress_presses_on_the_client_only_when_approved()
    {
        var f = new Fake();
        Assert.Equal((true, "screen"), await f.Seams().CaptureAndPress("con:10", "Down", s => s == "screen"));
        Assert.Equal((false, "screen"), await f.Seams().CaptureAndPress("con:10", "Enter", _ => false));
        Assert.Equal([(12, "Down")], f.Pressed);
    }

    [Fact]
    public async Task CaptureAndPress_with_no_client_presses_nothing()
    {
        var f = new Fake { Table = [] };
        Assert.Equal((false, (string?)null), await f.Seams().CaptureAndPress("con:10", "Enter", _ => true));
        Assert.Empty(f.Pressed);
    }

    [Fact]
    public async Task ClosePane_kills_the_host_and_never_a_tmux_pane()
    {
        var f = new Fake();
        await f.Seams().ClosePane("con:10");
        await f.Seams().ClosePane("%1");
        Assert.Equal([10], f.Killed);
    }

    // The real seams behind LaunchRunner's statics, on a pid no process can
    // have (Windows pids are multiples of 4): nothing is found, read, pressed
    // or killed, on either rid, and nothing throws.
    [Fact]
    public async Task The_default_console_seams_on_a_pid_nobody_has_find_nothing_and_throw_nothing()
    {
        const string nobody = "con:2147483647";
        Assert.Null(await LaunchRunner.ReadPane(nobody));
        Assert.Equal((false, (string?)null), await LaunchRunner.PressKey(nobody, "Enter", _ => true));
        await LaunchRunner.ClosePane(nobody);
    }
}
