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
            // A plain sleeper: no mark, no ring, so "asleep" can be judged on its own.
            Orb(Agent("Fern", "idle", OrbPresence.Parked, lead: "paperclip:atlas")),
        };

        // Asserted before the capture so a picture of the wrong state fails
        // rather than being reviewed as the right one.
        Assert.All(orbs, o => Assert.Equal("paperclip", o.CliMarkName));
        Assert.Equal(
            [null, null, "broke", "paused", "over budget", null],
            orbs.Select(o => o.PresenceMarkName));
        Assert.Equal("?", orbs[1].FindControl<TextBlock>("PresenceGlyph")!.Text);
        // Only the asleep and the paused are dimmed; amber, broke and over budget are not.
        Assert.Equal([1.0, 1.0, 1.0, 0.45, 1.0, 0.45], orbs.Select(o => Math.Round(o.BodyOpacity, 2)));
        Assert.Equal(
            [AccountOrbWindow.CalmHex, AccountOrbWindow.CalmHex, null, null, AccountOrbWindow.DangerHex, null],
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

    // The same two scenes at twice the orb size, so a reviewer can judge the marks and the
    // dimmed letters that are hard to read at actual size, and a parked local Claude Code
    // orb beside them as the reference for "asleep" as the app already draws it.
    [AvaloniaFact]
    public void TheCompanyAndTheFoldedLeadAtTwiceTheSizeBesideAParkedLocalOrb()
    {
        var before = OrbweaverSettings.OrbSize;
        try
        {
            OrbweaverSettings.OrbSize = 2.0;
            var company = new[]
            {
                Orb(Agent("Atlas", "generating", budget: 22)),
                Orb(Agent("Beacon", "waiting", OrbPresence.NeedsInput, budget: 48, lead: "paperclip:atlas")),
                Orb(Agent("Cinder", "idle", mark: PaperclipMark.Broke, lead: "paperclip:atlas")),
                Orb(Agent("Dune", "idle", OrbPresence.Parked, PaperclipMark.Paused, lead: "paperclip:atlas")),
                Orb(Agent("Ember", "idle", OrbPresence.Parked, PaperclipMark.OverBudget, budget: 96, lead: "paperclip:atlas")),
                Orb(Agent("Fern", "idle", OrbPresence.Parked)),
            };
            var parkedLocal = new OrbWindow("local-parked");
            parkedLocal.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, State = "idle", Title = "Fern", Cwd = "/w", Presence = OrbPresence.Parked,
            });
            parkedLocal.Show();
            var folded = Orb(Agent("Atlas", "idle", OrbPresence.Present, folded: 3));

            ScreenshotHelper.Flush();
            ScreenshotHelper.SideBySide(company.Append(parkedLocal).Append(folded).ToList(), "paperclip-orbs-2x.png");
        }
        finally { OrbweaverSettings.OrbSize = before; }
    }
}
