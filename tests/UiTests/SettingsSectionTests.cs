using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// The seam the rest of CB-166 is built on: Group() now returns a
// SettingsSection registered under a stable id, and SettingsWindow.Sections
// hands that register out.
//
// House policy is inherited from SettingsWindowRowTests next door rather than
// restated: the window is built through its private constructor, never shown
// and never closed — closing a headless Window corrupts a process-wide Avalonia
// font cache and takes every later test in the assembly with it — and nothing
// here synthesizes a click, because there is no gesture in this commit to
// synthesize. These drive the builder and read the tree it produced.
[Collection("Settings")]
public class SettingsSectionTests
{
    // The ids, in the order Body() adds them. Written out rather than derived,
    // so a call site that silently loses or renames one fails here: the whole
    // point of an id is that it outlives a copy edit to the heading, which
    // means nothing in the window can be trusted to confirm it.
    private static readonly string[] ExpectedIds =
    {
        "orbs", "orb-click", "auto-organize", "group-outlines", "orb-colours", "chat-panel",
        "voice", "sounds", "claude-code", "codex", "grok", "openclaw", "claude-cloud",
        "peer-link", "claude-desktop"
    };

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    [AvaloniaFact]
    public void RegistersEverySectionUnderItsOwnId()
    {
        var window = NewWindow();

        Assert.Equal(ExpectedIds, window.Sections.Keys.ToArray());
        Assert.Equal(ExpectedIds.Length, window.Sections.Count);

        // A dictionary cannot hold a duplicate key, so a repeated id would not
        // show up as a duplicate — it would show up as a missing section, with
        // the second Group() call having overwritten the first. Asserting the
        // count as well as the ids is what catches that, and it is why the ids
        // above are a list rather than a set.
        Assert.Equal(ExpectedIds.Length, ExpectedIds.Distinct().Count());
    }

    // Every section's Title has to be a real TextBlock in that section's own
    // logical subtree, carrying the heading verbatim. Three screenshot
    // scenarios find their group by searching the window for a TextBlock whose
    // Text equals the heading, and six of them fall back to a zero-bounds
    // anchor when the search fails — which yields a 1x1 PNG and a green run.
    // So the thing those scenarios rely on is asserted here, where a failure is
    // a failure rather than a picture of nothing.
    [AvaloniaFact]
    public void EveryTitleIsARealTextBlockInThatSectionsSubtree()
    {
        var window = NewWindow();

        foreach (var (id, section) in window.Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Title), id);

            var headings = section.GetLogicalDescendants()
                .OfType<TextBlock>()
                .Where(t => t.Text == section.Title)
                .ToList();

            Assert.True(headings.Count >= 1,
                $"section '{id}' has no TextBlock reading \"{section.Title}\"");
        }
    }

    [AvaloniaFact]
    public void EverySectionKeepsHoldOfItsCards()
    {
        var window = NewWindow();

        foreach (var (id, section) in window.Sections)
        {
            Assert.NotEmpty(section.Cards);

            // The cards are the ones Group() was handed, not the wrapper the
            // params overload builds — so each has to be reachable from the
            // section itself rather than merely non-null.
            foreach (var card in section.Cards)
            {
                Assert.Contains(card, section.GetLogicalDescendants());
            }
        }
    }

    // Sections start open, and nothing in this commit writes that anywhere.
    [AvaloniaFact]
    public void SectionsStartOpenAndRestoringIsHarmless()
    {
        var window = NewWindow();

        Assert.All(window.Sections.Values, s => Assert.True(s.IsOpen));

        foreach (var section in window.Sections.Values)
        {
            section.RestoreOpenState();
        }

        Assert.All(window.Sections.Values, s => Assert.True(s.IsOpen));
    }

    // The probe the collapse half needs settled before it starts, measured
    // rather than reasoned about: does hiding a control with IsVisible = false
    // take its children out of the *logical* tree?
    //
    // It decides whether the existing whole-window tests survive collapsing at
    // all. SettingsUrlRoutingRowTests finds its controls by walking the window,
    // so if a folded section's rows left the logical tree those tests would go
    // red for every section a user happened to have folded — and a fold is
    // persisted, so it would be red on one machine and green on another.
    //
    // The visible half of this test is the negative control, and it is not
    // decoration: a query that found nothing at all would pass the hidden half
    // on its own and look like proof of the opposite answer.
    [AvaloniaFact]
    public void HidingASectionsBodyKeepsItsRowsInTheLogicalTree()
    {
        var window = NewWindow();

        var hidden = window.Sections["orbs"];
        var visible = window.Sections["voice"];

        var hiddenRows = hidden.GetLogicalDescendants().OfType<TextBlock>().ToList();
        var visibleRows = visible.GetLogicalDescendants().OfType<TextBlock>().ToList();

        Assert.NotEmpty(hiddenRows);
        Assert.NotEmpty(visibleRows);

        foreach (var card in hidden.Cards) card.IsVisible = false;

        var reachable = window.GetLogicalDescendants().ToHashSet();

        // The negative control: an untouched section must still be found by
        // exactly this query.
        Assert.All(visibleRows, row => Assert.Contains(row, reachable));

        // The measurement.
        Assert.All(hiddenRows, row => Assert.Contains(row, reachable));
    }
}
