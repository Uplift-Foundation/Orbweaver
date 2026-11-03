using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-306: the panel's one new decision — a slash line on a session that runs
// slash commands itself goes to the runner, never to SendAsync — and the one
// new filter: a command the session marks Unavailable is not offered.
[Collection("Settings")]
public class ChatPanelSlashRunnerTests : IDisposable
{
    private static void Flush() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // One session id per test and its panel hidden afterwards, the way
    // ChatPanelTests does, so a panel left open here cannot be the transient
    // the next class finds.
    private readonly List<string> _opened = new();

    private FakeChatSession NewFake()
    {
        var id = Guid.NewGuid().ToString();
        _opened.Add(id);
        return new FakeChatSession { SessionId = id };
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
    public void ASlashLineGoesToTheRunnerAndNotToSend()
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
    public void ARefusedSlashLineStaysInTheBox()
    {
        var fake = NewFake();
        fake.SlashOutcome = ChatSendOutcome.Failed;
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var input = TypeAndEnter("/compact");

        Assert.Equal(new[] { "/compact" }, fake.RanSlash);
        Assert.Empty(fake.SentTexts);
        Assert.Equal("/compact", input.Text);
    }

    [AvaloniaFact]
    public void ALineThatIsNotACommandStillGoesToSend()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        TypeAndEnter("hello /world");

        Assert.Empty(fake.RanSlash);
        Assert.Equal(new[] { "hello /world" }, fake.SentTexts);
    }

    [AvaloniaFact]
    public void ABareSlashIsAMessageNotACommand()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        TypeAndEnter("/");

        Assert.Empty(fake.RanSlash);
        Assert.Equal(new[] { "/" }, fake.SentTexts);
    }

    [AvaloniaFact]
    public void AnUnavailableCommandIsNotOffered()
    {
        var fake = NewFake();
        fake.SlashCommands = new[]
        {
            new SlashCommand("/color", "Set the prompt bar color", SlashRouteKind.Control),
            new SlashCommand("/compact", "Free up context", SlashRouteKind.Unavailable),
            new SlashCommand("/clear", "Start over"),
        };
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

        Assert.Equal(new[] { "/color", "/clear" }, names);
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

    [Theory]
    [InlineData("/compact", true)]
    [InlineData("/color pink", true)]
    [InlineData("/", false)]
    [InlineData("hi /there", false)]
    [InlineData("", false)]
    public void IsSlashLineIsSplitsAnswer(string text, bool expected)
    {
        Assert.Equal(expected, ChatPanel.IsSlashLine(text));
    }
}
