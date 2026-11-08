using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-315: the one-key press a phone's answer makes. Pure halves only — the
/// argv and console records each route would send — because the processes
/// themselves (kitty, wezterm, osascript, a console) are not run headless; the
/// live presses are recorded in PR #227 against panes and windows of our own.
/// </summary>
public class TerminalPressTests
{
    private static SessionStatus S(string id = "42", string program = "") =>
        new() { Source = SessionSource.ClaudeCode, TermId = id, TermProgram = program };

    [Theory]
    [InlineData("1", "1")]
    [InlineData("9", "9")]
    [InlineData("Enter", "\r")]
    [InlineData("10", null)]
    [InlineData("a", null)]
    [InlineData("", null)]
    [InlineData("y", null)]
    [InlineData("１", null)]   // a fullwidth digit is not an ASCII digit
    public void Only_a_digit_or_Enter_is_a_key(string key, string? bytes) => Assert.Equal(bytes, TerminalPress.Bytes(key));

    [Fact]
    public void Kitty_sends_exactly_the_key_on_stdin_with_no_paste_markers_or_enter()
    {
        var (stdin, args) = TerminalPress.Kitty(S("7"), "1");
        Assert.Equal("1", stdin);
        Assert.Equal(["@", "send-text", "--match", "id:7", "--stdin"], args);
    }

    [Fact]
    public void WezTerm_sends_exactly_the_key_with_no_paste()
    {
        var (stdin, args) = TerminalPress.WezTerm(S("3"), "\r");
        Assert.Equal("\r", stdin);
        Assert.Equal(["cli", "send-text", "--pane-id", "3", "--no-paste"], args);
    }

    [Fact]
    public void ITerm2_passes_the_session_and_key_through_argv_with_newline_off()
    {
        var args = TerminalPress.ITerm2Args(S("w0t0p0:ABC"), "2");
        Assert.Equal(["w0t0p0:ABC", "2"], args[^2..]);
        var script = string.Join("\n", TerminalPress.ITerm2Script);
        Assert.Contains("write text body newline NO", script);
        Assert.DoesNotContain("w0t0p0:ABC", script);   // never in the source
        Assert.Equal(TerminalPress.ITerm2Script.Length, args.Count(a => a == "-e"));
    }

    [Fact]
    public void A_console_digit_is_one_down_up_pair_and_nothing_after()
    {
        var r = TerminalPress.ConsoleRecords("3");
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal('3', x.Char));
        Assert.Equal([1, 0], r.Select(x => x.KeyDown));
        Assert.All(r, x => Assert.Equal(0, x.VirtualKeyCode));
    }

    [Fact]
    public void A_console_enter_is_vk_return_with_cr()
    {
        var r = TerminalPress.ConsoleRecords("\r");
        Assert.All(r, x => Assert.Equal((0x0D, '\r'), (x.VirtualKeyCode, x.Char)));
    }

    [Theory]
    [InlineData("Tmux", true)]
    [InlineData("Kitty", true)]
    [InlineData("WezTerm", true)]
    [InlineData("ITerm2", true)]
    [InlineData("WindowsConsole", true)]
    [InlineData("TerminalApp", false)]   // needs Accessibility and focus: Open on desk
    [InlineData("None", false)]
    public void Only_routes_that_write_without_focus_press(string route, bool presses) =>
        Assert.Equal(presses, TerminalPress.Silent(Enum.Parse<TerminalTyping.Route>(route)));

    [Theory]
    [InlineData("1")]
    [InlineData("Enter")]
    public async Task Nothing_is_pressed_without_a_route_a_session_or_a_valid_key(string key)
    {
        Assert.False(await TerminalFocuser.PressKey(null, key));
        Assert.False(await TerminalFocuser.PressKey(S(), key));                       // no terminal coordinates
        Assert.False(await TerminalFocuser.PressKey(S("1", TerminalTyping.TerminalAppProgram) , key));
        Assert.False(await TerminalFocuser.PressKey(S(), "x"));
        Assert.False(await TerminalFocuser.TypeQuietly(null, "note"));
        Assert.False(await TerminalFocuser.TypeQuietly(S(), ""));
        Assert.False(await TerminalFocuser.TypeQuietly(S(), "note"));
    }
}
