using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// SymbolMarks.MuteLook / MuteTip are the pure half of the chat panel's mute
// button (CB-298): which glyph, and what the tooltip says. No window needed.
public class SymbolMarksMuteTests
{
    [Fact]
    public void MuteLook_WhenMuted_IsTheCrossedOutSpeaker() =>
        Assert.Equal("\U0001F507", SymbolMarks.MuteLook(true));

    [Fact]
    public void MuteLook_WhenNotMuted_IsTheLoudSpeaker() =>
        Assert.Equal("\U0001F50A", SymbolMarks.MuteLook(false));

    [Fact]
    public void MuteTip_NamesTheBoundChord()
    {
        var combo = new HotkeyCombo(KeyModifiers.Control | KeyModifiers.Alt, Key.M);
        Assert.Equal("Mute — Ctrl+Alt+M", SymbolMarks.MuteTip(false, combo));
        Assert.Equal("Unmute — Ctrl+Alt+M", SymbolMarks.MuteTip(true, combo));
    }

    [Fact]
    public void MuteTip_WithNothingBound_NamesNoChord()
    {
        Assert.Equal("Mute", SymbolMarks.MuteTip(false, null));
        Assert.Equal("Unmute", SymbolMarks.MuteTip(true, null));
    }
}
