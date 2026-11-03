using System.Collections.Generic;
using System.ComponentModel;
using Xunit;

namespace Orbweaver.Tests;

// ImageUrl and ImageBytes are settable rather than init-only, and raise
// PropertyChanged like Text already does — the mechanism a live turn needs to
// gain a picture after it was first drawn with none. See
// OpenClawChatSession.TryResolveLiveImage and ChatPanel's TurnView, which
// reacts to exactly this notification.
public class ChatTurnTests
{
    private static List<string?> Names(ChatTurn turn)
    {
        var names = new List<string?>();
        turn.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }

    [Fact]
    public void SettingImageUrlRaisesPropertyChanged()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "here it is" };
        var names = Names(turn);

        turn.ImageUrl = "https://x/pic.png";

        Assert.Equal("https://x/pic.png", turn.ImageUrl);
        Assert.Contains(nameof(ChatTurn.ImageUrl), names);
    }

    [Fact]
    public void SettingImageUrlToTheSameValueRaisesNothing()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, ImageUrl = "https://x/pic.png" };
        var names = Names(turn);

        turn.ImageUrl = "https://x/pic.png";

        Assert.Empty(names);
    }

    [Fact]
    public void SettingImageBytesRaisesPropertyChanged()
    {
        var turn = new ChatTurn { Role = ChatRole.User };
        var names = Names(turn);

        turn.ImageBytes = new byte[] { 1, 2, 3 };

        Assert.Equal(new byte[] { 1, 2, 3 }, turn.ImageBytes);
        Assert.Contains(nameof(ChatTurn.ImageBytes), names);
    }

    // ImageBytes compares by reference (it's a byte[]), unlike ImageUrl's
    // string equality — setting it back to the exact same array is the no-op
    // arm of that check.
    [Fact]
    public void SettingImageBytesToTheSameReferenceRaisesNothing()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var turn = new ChatTurn { Role = ChatRole.User, ImageBytes = bytes };
        var names = Names(turn);

        turn.ImageBytes = bytes;

        Assert.Empty(names);
    }

    // CB-270: a live tool line learns it failed only when the result arrives.
    [Fact]
    public void SettingIsErrorRaisesPropertyChanged()
    {
        var turn = new ChatTurn();
        var names = Names(turn);

        turn.IsError = true;

        Assert.True(turn.IsError);
        Assert.Equal(new[] { nameof(ChatTurn.IsError) }, names);
    }

    [Fact]
    public void SettingIsErrorToTheSameValueRaisesNothing()
    {
        var turn = new ChatTurn();
        var names = Names(turn);

        turn.IsError = false;

        Assert.Empty(names);
    }
}
