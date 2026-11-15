using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-289: a table in a reply is drawn verbatim, unwrapped, in a horizontal
// scroller, and ordinary code is not. The bug was that a row wider than the
// bubble wrapped and every later column landed on the next line.
[Collection("Settings")]
public class ChatPanelTableTests : IDisposable
{
    private readonly List<string> _toClean = new();

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private ChatPanel Open(string text)
    {
        var id = "table-" + Guid.NewGuid();
        _toClean.Add(id);
        var fake = new FakeChatSession(new[] { new ChatTurn { Role = ChatRole.Assistant, Text = text } })
        {
            SessionId = id,
            DisplayName = "Table Session"
        };

        ChatPanel.OpenFor(NewOrb(), fake);
        FlushRender();
        return ChatPanelTestAccess.Instance!;
    }

    private static Control Row(ChatPanel panel) =>
        (Control)panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children[0];

    private static SelectableTextBlock BlockWith(Control row, string text) =>
        row.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => b.Text == text);

    [AvaloniaFact]
    public void ABoxTableIsDrawnNoWrapInMonospaceInsideAHorizontalScroller()
    {
        var table = ChatTableFixtures.Join(ChatTableFixtures.BoxUpdateStatus);
        var row = Row(Open("Status:\n\n" + table));

        var block = BlockWith(row, table);
        Assert.Equal(TextWrapping.NoWrap, block.TextWrapping);
        Assert.Equal(ChatPanel.MonoFamily, block.FontFamily);

        var scroller = Assert.IsType<ScrollViewer>(block.GetVisualAncestors().OfType<ScrollViewer>().First());
        Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);
    }

    [AvaloniaFact]
    public void AWideTableScrollsInsteadOfWideningTheBubble()
    {
        var table = ChatTableFixtures.Join(ChatTableFixtures.BoxUpdateStatus);
        var panel = Open(table);
        var row = Row(panel);

        var block = BlockWith(row, table);
        var scroller = block.GetVisualAncestors().OfType<ScrollViewer>().First();

        Assert.True(scroller.Extent.Width > scroller.Viewport.Width,
            $"extent {scroller.Extent.Width} should exceed viewport {scroller.Viewport.Width}");
        Assert.True(scroller.Bounds.Width <= panel.Bounds.Width,
            $"scroller {scroller.Bounds.Width} must fit the panel {panel.Bounds.Width}");
    }

    [AvaloniaFact]
    public void AnOrdinaryFencedCodeBlockStillWrapsAndHasNoScroller()
    {
        var code = "var a = SomethingVeryLongIndeed(withSeveralArguments, andMoreArguments, andEvenMoreArguments, forGoodMeasure);";
        var row = Row(Open("```csharp\n" + code + "\n```"));

        var block = BlockWith(row, code);

        Assert.Equal(TextWrapping.Wrap, block.TextWrapping);
        Assert.DoesNotContain(block.GetVisualAncestors().OfType<ScrollViewer>(), s => row.IsVisualAncestorOf(s));
    }

    [AvaloniaFact]
    public void ATableBlockIsSelectableLikeAnyOtherBlock()
    {
        var table = ChatTableFixtures.Join(ChatTableFixtures.AsciiSqlite);
        var row = Row(Open(table));

        var block = BlockWith(row, table);

        Assert.False(block.Focusable);
        Assert.Equal(table, block.Text);
    }

    // Release notes keep wrapping; they only gain the Table -> Code mapping, so
    // a box table there is the monospace border and not a proportional Line.
    [AvaloniaFact]
    public void ABoxTableInReleaseNotesRendersInTheMonospaceCodeBorder()
    {
        // A box table, because StripTables drops leading-pipe rows (the release
        // body's downloads table) and a heading section that holds one.
        var table = ChatTableFixtures.Join(ChatTableFixtures.BoxUpdateStatus);
        var controls = UpdateNotesView.Build(table);

        var border = controls.OfType<Border>().Single();
        var text = Assert.IsType<TextBlock>(border.Child);

        Assert.Equal(table, text.Text);
        Assert.Contains("Menlo", text.FontFamily.Name);
    }
}
