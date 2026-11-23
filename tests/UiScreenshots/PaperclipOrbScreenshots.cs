using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-421: captures for what a Paperclip orb shows. Hand-written one per scenario, like
// every capture here: tests/UiTests renders through the null renderer and this suite
// through real Skia, and only Skia can say whether the drawn paperclip reads as one at
// 22 px, whether the corner marks are legible on a dimmed orb, or whether a ring and a
// badge collide on a small orb.
//
// What the pictures are for, in the words a reviewer comparing rids needs: (1) the
// paperclip badge on every orb, the amber "?", the broke "!", the pause mark, and a
// ring in each colour band, side by side; (2) a lead carrying its "+N". Neither
// can show the hover, which is a native popup the headless renderer does not draw.
//
// No clicks, for the reason the other orb captures give: an orb's pointer handling
// ends in a browser or a terminal.
[Collection("Settings")]
public class PaperclipOrbScreenshots
{
    private static SessionStatus Agent(
        string name, string state = "idle", OrbPresence presence = OrbPresence.Present,
        PaperclipMark mark = PaperclipMark.None, int? budget = null, int folded = 0, string? lead = null) => new()
    {
        Source = SessionSource.Paperclip,
        State = state,
        Title = name,
        Cwd = "",
        Presence = presence,
        Mark = mark,
        BudgetPercent = budget,
        FoldedCount = folded,
        FoldedNames = folded > 0 ? "Fern, Gale, Hart" : null,
        Url = "http://127.0.0.1:3100/CB/agents/" + name.ToLowerInvariant(),
        Lead = lead ?? "",
    };

    private static OrbWindow Orb(SessionStatus status)
    {
        var orb = new OrbWindow("paperclip:" + status.Title);
        orb.UpdateFrom(status);
        // SideBySide reads each window's Bounds, which are empty until it is shown and laid out.
        orb.Show();
        return orb;
    }

    // A company with one orb in each thing the product plan promises: working (with a
    // calm ring), amber, broke, paused, and asleep with a ring in the danger band.
    [AvaloniaFact]
    public void ACompanyShowsWorkingAmberBrokePausedAndOverBudgetSideBySide()
    {
        var orbs = new[]
        {
            Orb(Agent("Atlas", "generating", budget: 22)),
            Orb(Agent("Beacon", "waiting", OrbPresence.NeedsInput, budget: 48, lead: "paperclip:atlas")),
            Orb(Agent("Cinder", "idle", mark: PaperclipMark.Broke, lead: "paperclip:atlas")),
            Orb(Agent("Dune", "idle", OrbPresence.Parked, PaperclipMark.Paused, lead: "paperclip:atlas")),
            Orb(Agent("Ember", "idle", OrbPresence.Parked, PaperclipMark.OverBudget, budget: 96, lead: "paperclip:atlas")),
        };

        // Asserted before the capture so a picture of the wrong state fails
        // rather than being reviewed as the right one.
        Assert.All(orbs, o => Assert.Equal("paperclip", o.CliMarkName));
        Assert.Equal(
            [null, null, "broke", "paused", "over budget"],
            orbs.Select(o => o.PresenceMarkName));
        Assert.Equal("?", orbs[1].FindControl<TextBlock>("PresenceGlyph")!.Text);
        Assert.Equal(
            [AccountOrbWindow.CalmHex, AccountOrbWindow.CalmHex, null, null, AccountOrbWindow.DangerHex],
            orbs.Select(o => o.ContextRingColour));

        ScreenshotHelper.Flush();
        ScreenshotHelper.SideBySide(orbs, "paperclip-orbs-company.png");
    }

    // A lead with sleeping reports folded into it, beside the same lead with none, so the
    // "+N" can be judged against an orb that has no pill.
    [AvaloniaFact]
    public void AFoldedLeadWearsItsCountBesideOneWithNone()
    {
        var folded = Orb(Agent("Atlas", "idle", OrbPresence.Present, folded: 3));
        var plain = Orb(Agent("Atlas2"));

        Assert.True(folded.FoldedPillVisible);
        Assert.Equal("+3", folded.FoldedPillLabel);
        Assert.False(plain.FoldedPillVisible);

        ScreenshotHelper.Flush();
        ScreenshotHelper.SideBySide([folded, plain], "paperclip-orbs-folded-lead.png");
    }
}
