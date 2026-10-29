using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// CB-270's Rename window. Never presented through the real Present(): that calls
// Show() and MacOSActivation, which a headless runner must not do, so the
// constructor is built directly and Toggle is driven through its seams. The
// request is a seam too, answered here without a gateway.
[Collection("Settings")]
public class OpenClawRenameWindowTests : IDisposable
{
    private const string Orb = "openclaw:agent:main:dashboard:abc";

    public void Dispose()
    {
        OpenClawRenameWindow.OpenForTests = null;
        OpenClawRenameWindow.PresentForTests = null;
        OpenClawRenameWindow.BringForwardForTests = null;
    }

    private static (OpenClawRenameWindow Window, List<string> Calls) Build(
        string? label = "Scratch pad",
        Func<Task<(OpenClawActionOutcome, string?)>>? answer = null)
    {
        var calls = new List<string>();
        var window = new OpenClawRenameWindow(Orb, label, (id, name, _) =>
        {
            calls.Add($"{id}|{name}");
            return answer?.Invoke() ?? Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
        });
        return (window, calls);
    }

    [AvaloniaFact]
    public void TheBoxStartsWithTheCurrentLabel()
    {
        Assert.Equal("Scratch pad", Build("Scratch pad").Window.NameBox.Text);
        Assert.Equal("", Build(null).Window.NameBox.Text);
        Assert.False(Build().Window.StatusText.IsVisible);
    }

    [AvaloniaFact]
    public async Task SaveTrimsAndCallsTheSeamOnceThenCloses()
    {
        var (window, calls) = Build();
        window.Show();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.NameBox.Text = "  Planning \t";
        await window.SaveAsync();

        Assert.Equal(new[] { $"{Orb}|Planning" }, calls);
        Assert.True(closed);
    }

    // A blank name is "no request to make": the window closes and the gateway is
    // never asked.
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankNameClosesWithoutARequest(string text)
    {
        var (window, calls) = Build();
        window.Show();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.NameBox.Text = text;
        await window.SaveAsync();

        Assert.Empty(calls);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task ARefusalShowsItsMessageStaysOpenAndReEnablesTheControls()
    {
        var (window, _) = Build(answer: () =>
            Task.FromResult((OpenClawActionOutcome.Refused, (string?)"label too long")));
        window.Show();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.NameBox.Text = "x";
        await window.SaveAsync();

        Assert.False(closed);
        Assert.True(window.StatusText.IsVisible);
        Assert.Equal("Couldn't rename: label too long", window.StatusText.Text);
        Assert.True(window.NameBox.IsEnabled);
        Assert.True(window.SaveButton.IsEnabled);
        Assert.True(window.CancelButton.IsEnabled);
    }

    // While the request is out the controls are disabled, so a second Save
    // cannot send a second request.
    [AvaloniaFact]
    public async Task TheControlsAreDisabledWhileTheRequestIsOut()
    {
        var gate = new TaskCompletionSource<(OpenClawActionOutcome, string?)>();
        var (window, calls) = Build(answer: () => gate.Task);
        window.Show();

        var pending = window.SaveAsync();

        Assert.False(window.NameBox.IsEnabled);
        Assert.False(window.SaveButton.IsEnabled);
        Assert.False(window.CancelButton.IsEnabled);

        gate.SetResult((OpenClawActionOutcome.Done, null));
        await pending;

        Assert.Single(calls);
    }

    // A second attempt clears the first one's error.
    [AvaloniaFact]
    public async Task ARetryHidesTheOldError()
    {
        var results = new Queue<(OpenClawActionOutcome, string?)>(new[]
        {
            (OpenClawActionOutcome.Refused, (string?)"nope"),
            (OpenClawActionOutcome.Refused, (string?)"still no"),
        });
        var (window, _) = Build(answer: () => Task.FromResult(results.Dequeue()));
        window.Show();

        window.NameBox.Text = "x";
        await window.SaveAsync();
        Assert.Equal("Couldn't rename: nope", window.StatusText.Text);

        await window.SaveAsync();
        Assert.Equal("Couldn't rename: still no", window.StatusText.Text);
    }

    [AvaloniaFact]
    public void EscapeClosesAndOtherKeysDoNot()
    {
        Assert.True(OpenClawRenameWindow.ShouldClose(Key.Escape));
        Assert.False(OpenClawRenameWindow.ShouldClose(Key.A));

        var (window, _) = Build();
        window.Show();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.OnWindowKeyDown(null, new KeyEventArgs { Key = Key.A });
        Assert.False(closed);

        window.OnWindowKeyDown(null, new KeyEventArgs { Key = Key.Escape });
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void TheButtonsDoWhatTheirLabelsSay()
    {
        var (window, calls) = Build();
        window.Show();

        window.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Single(calls);

        var (cancelWindow, cancelCalls) = Build();
        cancelWindow.Show();
        var cancelled = false;
        cancelWindow.Closed += (_, _) => cancelled = true;
        cancelWindow.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert.True(cancelled);
        Assert.Empty(cancelCalls);
    }

    // One window at a time: a second Toggle retargets it rather than opening
    // another, and a bring-forward replaces Present.
    [AvaloniaFact]
    public void ToggleOpensOneWindowAndRetargetsItForAnotherConversation()
    {
        var presented = new List<OpenClawRenameWindow>();
        var forward = new List<OpenClawRenameWindow>();
        OpenClawRenameWindow.PresentForTests = presented.Add;
        OpenClawRenameWindow.BringForwardForTests = forward.Add;

        var calls = new List<string>();
        OpenClawRenameWindow.RenameFunc first = (id, name, _) =>
        {
            calls.Add($"first {id}");
            return Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
        };
        OpenClawRenameWindow.RenameFunc second = (id, name, _) =>
        {
            calls.Add($"second {id}");
            return Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
        };

        OpenClawRenameWindow.Toggle("openclaw:one", "One", first);
        OpenClawRenameWindow.Toggle("openclaw:two", "Two", second);

        Assert.Single(presented);
        Assert.Same(presented[0], Assert.Single(forward));
        Assert.Same(presented[0], OpenClawRenameWindow.OpenForTests);
        Assert.Equal("openclaw:two", presented[0].OrbSessionId);
        Assert.Equal("Two", presented[0].NameBox.Text);

        // The retarget replaced the request too: Save asks the second seam.
        presented[0].Show();
        presented[0].SaveAsync().GetAwaiter().GetResult();
        Assert.Equal(new[] { "second openclaw:two" }, calls);
    }

    // Retargeting clears an old error and re-enables the controls.
    [AvaloniaFact]
    public async Task RetargetingClearsAnOldErrorAndReEnables()
    {
        var presented = new List<OpenClawRenameWindow>();
        OpenClawRenameWindow.PresentForTests = presented.Add;
        OpenClawRenameWindow.BringForwardForTests = _ => { };

        OpenClawRenameWindow.Toggle("openclaw:one", "One", (_, _, _) =>
            Task.FromResult((OpenClawActionOutcome.Refused, (string?)"nope")));
        var window = presented[0];
        window.NameBox.Text = "x";
        await window.SaveAsync();
        Assert.True(window.StatusText.IsVisible);

        OpenClawRenameWindow.Toggle("openclaw:two", "Two", (_, _, _) =>
            Task.FromResult((OpenClawActionOutcome.Done, (string?)null)));

        Assert.False(window.StatusText.IsVisible);
        Assert.True(window.SaveButton.IsEnabled);
    }

    // A window retargeted at a conversation with no name starts with an empty box,
    // and a box whose text is null is a blank name: no request, window closes.
    [AvaloniaFact]
    public async Task RetargetingAtAnUnnamedConversationEmptiesTheBoxAndANullTextIsBlank()
    {
        var presented = new List<OpenClawRenameWindow>();
        OpenClawRenameWindow.PresentForTests = presented.Add;
        OpenClawRenameWindow.BringForwardForTests = _ => { };
        var calls = new List<string>();
        OpenClawRenameWindow.RenameFunc rename = (id, name, _) =>
        {
            calls.Add(name);
            return Task.FromResult((OpenClawActionOutcome.Done, (string?)null));
        };

        OpenClawRenameWindow.Toggle("openclaw:one", "One", rename);
        OpenClawRenameWindow.Toggle("openclaw:two", null, rename);
        var window = presented[0];
        Assert.Equal("", window.NameBox.Text);

        window.Show();
        window.NameBox.Text = null;
        await window.SaveAsync();

        Assert.Empty(calls);
    }
}
