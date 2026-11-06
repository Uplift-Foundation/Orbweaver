using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// The small window behind the post-update notice, the manual check's plain
// answers and a failed update's next-launch report (CB-308). Never presented
// through the real Present(): it calls Show() and MacOSActivation.
[Collection("Settings")]
public class UpdateMessageWindowTests : IDisposable
{
    public void Dispose()
    {
        UpdateMessageWindow.OpenForTests = null;
        UpdateMessageWindow.PresentForTests = null;
        UpdateMessageWindow.BringForwardForTests = null;
    }

    [AvaloniaFact]
    public void ItShowsTheTitleAndTheMessage()
    {
        var w = new UpdateMessageWindow("Hello", "World", false, () => { });

        Assert.Equal("Hello", w.Title);
        Assert.Equal("Hello", w.Heading.Text);
        Assert.Equal("World", w.Message.Text);
    }

    [AvaloniaFact]
    public void TheLocalNetworkButtonIsThereOnlyWhenOfferedAndOpensThePane()
    {
        var opened = 0;
        var without = new UpdateMessageWindow("t", "m", false, () => opened++);
        var with = new UpdateMessageWindow("t", "m", true, () => opened++);

        Assert.False(without.OpenButton.IsVisible);
        Assert.True(with.OpenButton.IsVisible);

        with.OpenButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(1, opened);
    }

    [AvaloniaFact]
    public void OkAndEscapeBothClose()
    {
        foreach (var close in new Action<UpdateMessageWindow>[]
        {
            w => w.OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)),
            w => w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }),
        })
        {
            var w = new UpdateMessageWindow("t", "m", false, () => { });
            w.Show();
            var closed = false;
            w.Closed += (_, _) => closed = true;

            close(w);

            Assert.True(closed);
        }
    }

    [AvaloniaFact]
    public void AnotherKeyDoesNotClose()
    {
        var w = new UpdateMessageWindow("t", "m", false, () => { });
        w.Show();
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });

        Assert.False(closed);
        w.Close();
    }

    [AvaloniaFact]
    public void ToggleOpensOneWindowAndASecondRetargetsIt()
    {
        var presented = new List<UpdateMessageWindow>();
        var forward = new List<UpdateMessageWindow>();
        UpdateMessageWindow.PresentForTests = presented.Add;
        UpdateMessageWindow.BringForwardForTests = forward.Add;

        UpdateMessageWindow.Toggle("One", "first", false);
        UpdateMessageWindow.Toggle("Two", "second", true);

        Assert.Single(presented);
        Assert.Single(forward);
        Assert.Equal("Two", presented[0].Heading.Text);
        Assert.Equal("second", presented[0].Message.Text);
        Assert.True(presented[0].OpenButton.IsVisible);
        Assert.Equal("Two", presented[0].Title);
    }

    [AvaloniaFact]
    public void TheOpenLogButtonIsThereOnlyWithAPathAndOpensThatPath()
    {
        var opened = new List<string>();
        var without = new UpdateMessageWindow("t", "m", false, () => { });
        var with = new UpdateMessageWindow("t", "m", false, () => { }, "/tmp/setup.log", opened.Add);

        Assert.False(without.LogButton.IsVisible);
        Assert.True(with.LogButton.IsVisible);

        with.LogButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        without.LogButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.Equal(new[] { "/tmp/setup.log" }, opened);
    }

    [AvaloniaFact]
    public void RetargetingCarriesTheLogPathAcross()
    {
        UpdateMessageWindow.PresentForTests = _ => { };
        UpdateMessageWindow.BringForwardForTests = _ => { };

        UpdateMessageWindow.Toggle("One", "a", false);
        Assert.False(UpdateMessageWindow.OpenForTests!.LogButton.IsVisible);

        UpdateMessageWindow.Toggle("Two", "b", false, "/tmp/setup.log");
        Assert.True(UpdateMessageWindow.OpenForTests!.LogButton.IsVisible);
    }
}
