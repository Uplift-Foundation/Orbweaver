using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-313: the terminal x CLI table from docs/screen-read-spike-findings.md, in
// executable form. Every fixture under tools/screen-read-probe/fixtures is a
// real capture of a live terminal (the reader that took it is in manifest.json),
// and this runs the shipped parser over each one and pins what it says.
//
// The manifest records what the parser says TODAY, which is not always what we
// want: the Grok rows pin `unknown` because Grok draws "1 (o) Yes" with a radio
// glyph and no "1." and the parser reads none of it. That is a known gap with
// the capture attached (CB-313 findings), pinned so that teaching the parser
// about Grok flips these rows on purpose rather than by accident.
public class ScreenReadFixtureTests
{
    public sealed record Row(
        string Fixture, string Terminal, string Cli, string Kind,
        string Expected, string ExpectedCheck, int ExpectedOptions,
        string? ExpectedTitle, bool LabelsComplete);

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<object[]> Rows() =>
        JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(Path.Combine(Dir, "manifest.json")), Json)!
            .Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(Rows))]
    public void Parser_says_what_the_manifest_records(Row row)
    {
        var screen = File.ReadAllText(Path.Combine(Dir, row.Fixture));

        // The fixtures are LF whatever the checkout (.gitattributes); a CR here
        // would be the PR #219 failure again, not a property of the screen.
        Assert.DoesNotContain('\r', screen);

        var (reading, check) = PaneScreen.Read(screen);
        Assert.Equal(row.Expected, reading.ToString().ToLowerInvariant());
        Assert.Equal(row.ExpectedCheck, check);

        var dialog = ChatTranscript.ParseDialog(screen);
        if (row.Expected != "dialog")
        {
            Assert.Null(dialog);
            return;
        }

        Assert.NotNull(dialog);
        Assert.Equal(row.ExpectedOptions, dialog!.Options.Count);
        Assert.Equal(row.ExpectedTitle, dialog.Title);

        // Keys 1..n, and every label is text that is on the screen. A label the
        // terminal wrapped is held to "a prefix of what is drawn" rather than
        // the whole of it, because that truncation is the recorded finding.
        for (var i = 0; i < dialog.Options.Count; i++)
        {
            var o = dialog.Options[i];
            Assert.Equal((i + 1).ToString(), o.Key);
            Assert.Contains(o.Label, screen);
            if (row.LabelsComplete)
                Assert.Contains(screen.Split('\n'), l => l.TrimEnd().EndsWith(o.Label, StringComparison.Ordinal));
        }
    }

    // The per-cell negative control: a screen with no prompt on it never parses as a dialog.
    [Fact]
    public void Every_idle_capture_is_not_a_dialog()
    {
        var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(Path.Combine(Dir, "manifest.json")), Json)!;
        var idle = rows.Where(r => r.Kind.StartsWith("none", StringComparison.Ordinal)).ToList();
        Assert.True(idle.Count >= 25, "expected an idle capture per cell");
        foreach (var r in idle)
            Assert.Null(ChatTranscript.ParseDialog(File.ReadAllText(Path.Combine(Dir, r.Fixture))));
    }
}
