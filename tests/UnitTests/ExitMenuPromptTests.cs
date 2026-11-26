using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// CB-414: the "Background work is running" menu /exit opens while teammates run.
public class ExitMenuPromptTests
{
    // The real captured pane has one `shell · …` row. The ticket's sighting had three
    // teammate rows; this is that capture with its body row swapped for those.
    private static readonly string ThreeTeammates = PaneFixtures.MiniExitDialog.Replace(
        "   shell · cd /Users/someoneexample/Source/example-local/Log…",
        "   teammate · Check agent team constraints\n   teammate · Review the diff\n   teammate · Write the notes");

    [Fact]
    public void TheRealCaptureTitlesTheMenuByItsHeading()
    {
        var prompt = ChatTranscript.ParseDialog(PaneFixtures.MiniExitDialog)!;

        Assert.Equal("Background work is running", prompt.Title);
        Assert.Equal(new[] { "1", "2", "3" }, prompt.Options.Select(o => o.Key));
    }

    [Fact]
    public void AMenuWithSeveralBodyRowsTakesTheHeadingNotABodyRow()
    {
        var prompt = ChatTranscript.ParseDialog(ThreeTeammates)!;

        Assert.Equal("Background work is running", prompt.Title);
        Assert.Equal(3, prompt.Options.Count);
    }

    [Fact]
    public void AnIdlePromptPaneIsNotAMenu()
    {
        Assert.Null(ChatTranscript.ParseDialog(PaneFixtures.MacPromptAfterEsc));
    }
}
