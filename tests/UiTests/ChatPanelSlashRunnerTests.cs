using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-306: the panel's one new decision — a line that NAMES a command this
// session knows goes to the runner; anything else (a path, an unknown word,
// ordinary prose) sends as before (the lead's ruling on Nadia's finding 1).
// Plus the autocomplete filter: an Unavailable command is not offered.
[Collection("Settings")]
public class ChatPanelSlashRunnerTests : IDisposable
{
    private static void Flush() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 40; i++) { Flush(); await Task.Delay(10); }
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // The catalogue a runner publishes — the full list, routes and all, so the
    // panel's known-check sees the Unavailable ones too. /compact is deliberately
    // Unavailable here to prove a known-but-unrunnable command is still routed to
    // the runner (which refuses it) rather than sent to the model as prose.
    private static SlashCommand[] Catalogue() => new[]
    {
        new SlashCommand("/color", "Set the prompt bar color", SlashRouteKind.Control),
        new SlashCommand("/rename", "Rename the session", SlashRouteKind.Control),
        new SlashCommand("/compact", "Free up context", SlashRouteKind.Unavailable),
        new SlashCommand("/clear", "Start over"),
    };

    private readonly List<string> _opened = new();

    private FakeChatSession NewFake(bool withCatalogue = true)
    {
        var id = Guid.NewGuid().ToString();
        _opened.Add(id);
        var fake = new FakeChatSession { SessionId = id };
        if (withCatalogue) fake.SlashCommands = Catalogue();
        return fake;
    }

    public void Dispose()
    {
        foreach (var id in _opened) ChatPanel.HideFor(id);
    }

    private static TextBox TypeAndEnter(string text)
    {
        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        input.Focus();
        Flush();

        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        Flush();
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();
        return input;
    }

    [AvaloniaFact]
    public void AKnownCommandGoesToTheRunnerAndNotToSend()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var input = TypeAndEnter("/rename a better name");

        Assert.Equal(new[] { "/rename a better name" }, fake.RanSlash);
        Assert.Empty(fake.SentTexts);
        Assert.Equal("", input.Text);
    }

    [AvaloniaFact]
    public void AKnownButUnavailableCommandIsRoutedNotSentAsProse()
    {
        // /compact is Unavailable in the catalogue — it must still reach the
        // runner (which writes the refusal), never SendAsync, or the command
        // text would go to the model as an ordinary message.
        var fake = NewFake();
        fake.SlashOutcome = ChatSendOutcome.Failed;
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var input = TypeAndEnter("/compact");

        Assert.Equal(new[] { "/compact" }, fake.RanSlash);
        Assert.Empty(fake.SentTexts);
        Assert.Equal("/compact", input.Text);   // refused → kept
    }

    [AvaloniaTheory]
    [InlineData("/tmp/build.log has the error")]
    [InlineData("/Users/x/foo.cs fails")]
    [InlineData("/frobnicate the widget")]   // an unknown /word
    [InlineData("hello /color")]             // slash not at the start
    public void AnythingThatIsNotAKnownCommandIsSentAsAMessage(string text)
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        TypeAndEnter(text);

        Assert.Empty(fake.RanSlash);
        Assert.Equal(new[] { text }, fake.SentTexts);
    }

    [AvaloniaFact]
    public void WithNoCatalogueEverythingIsAMessage()
    {
        // A runner that has not published a catalogue (e.g. a cloud session
        // before its init replays) knows no commands, so a typed /rename is a
        // message rather than a command refused into the void.
        var fake = NewFake(withCatalogue: false);
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        TypeAndEnter("/rename x");

        Assert.Empty(fake.RanSlash);
        Assert.Equal(new[] { "/rename x" }, fake.SentTexts);
    }

    private static async Task PasteABitmap(TextBox input, ItemsControl attachments)
    {
        var panel = ChatPanelTestAccess.Instance!;
        var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96));
        await panel.Clipboard!.SetBitmapAsync(bitmap);
        var gesture = TextBox.PasteGesture!;
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers
        });
        await FlushAsync();
        Assert.True(attachments.IsVisible);
    }

    [AvaloniaFact]
    public async Task AKnownCommandWithAPictureAttachedIsRefusedAndNothingIsSent()
    {
        // A control request carries no image and a command must never reach the
        // model as prose, so a known command with an attachment is refused with
        // a note, not run and not sent (Nadia's finding 4, the lead's ruling).
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        await PasteABitmap(input, panel.FindControl<ItemsControl>("Attachments")!);

        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "/rename x" });
        Flush();
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();

        Assert.Empty(fake.RanSlash);
        Assert.Empty(fake.SentWithImages);
        Assert.Empty(fake.SentTexts);
        Assert.Equal("/rename x", input.Text);   // kept for a retry without the picture

        var turns = (System.Collections.IEnumerable)panel.FindControl<ItemsControl>("Turns")!.ItemsSource!;
        var last = turns.Cast<object>().Last();
        var noteText = last.GetType().GetProperty("Text")!.GetValue(last) as string;
        Assert.Equal(ChatPanel.SlashCommandAndPictureNote, noteText);
    }

    [AvaloniaFact]
    public async Task AnUnknownWordWithAPictureStillSendsAsAMessage()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        await PasteABitmap(input, panel.FindControl<ItemsControl>("Attachments")!);

        input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "/frobnicate" });
        Flush();
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Flush();

        Assert.Empty(fake.RanSlash);
        var sent = Assert.Single(fake.SentWithImages);
        Assert.Equal("/frobnicate", sent.Text);
        var path = Assert.Single(sent.ImagePaths);
        if (File.Exists(path)) File.Delete(path);
    }

    [AvaloniaFact]
    public void AnAliasIsAKnownCommand()
    {
        // "/t" is an alias of "/think" — it must be recognised as a command and
        // routed, not sent as a message (alias-awareness after wren's change).
        var fake = NewFake(withCatalogue: false);
        fake.SlashCommands = new[]
        {
            new SlashCommand("/think", "Set the thinking budget", SlashRouteKind.Control,
                new[] { "/t", "/thinking" }),
        };
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        TypeAndEnter("/t 5000");

        Assert.Equal(new[] { "/t 5000" }, fake.RanSlash);
        Assert.Empty(fake.SentTexts);
    }

    [AvaloniaFact]
    public void AnUnavailableCommandIsNotOffered()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;
        input.Focus();
        Flush();
        input.Text = "/c";
        input.CaretIndex = 2;
        Flush();

        var list = panel.FindControl<ItemsControl>("SlashList")!;
        var names = ((System.Collections.IEnumerable)list.ItemsSource!).Cast<object>()
            .Select(v => v.GetType().GetProperty("Name")!.GetValue(v) as string)
            .ToList();

        // /color and /clear start with "/c"; /compact is Unavailable, so hidden.
        Assert.Equal(new[] { "/clear", "/color" }, names.OrderBy(n => n).ToList());
    }

    [Fact]
    public void OfferedCommandsDropsOnlyTheUnavailable()
    {
        Assert.Empty(ChatPanel.OfferedCommands(null));
        Assert.Empty(ChatPanel.OfferedCommands(System.Array.Empty<SlashCommand>()));

        var offered = ChatPanel.OfferedCommands(new[]
        {
            new SlashCommand("/a", "", SlashRouteKind.Typed),
            new SlashCommand("/b", "", SlashRouteKind.Unavailable),
            new SlashCommand("/c", "", SlashRouteKind.PassThrough),
        });

        Assert.Equal(new[] { "/a", "/c" }, offered.Select(c => c.Name));
    }
}
