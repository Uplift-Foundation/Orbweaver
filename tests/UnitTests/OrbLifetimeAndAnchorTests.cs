using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// The three places SessionManager reads a setting and turns it into a rule:
// how long an orb outlives its session, whether a CLI is tracked at all, and
// where an arrangement gets drawn.
//
// Every one of these setters writes settings.json, and TestBootstrap points the
// whole assembly at one temp directory — so a value left behind here is the
// value every later test in every other class reads. The save/restore is not
// tidiness; TrayRemoteItemTests records what it cost to learn that, where a
// leaked RemoteControlEnabled made an unrelated test pass or fail on nothing
// but which class the runner reached first.
[Collection("Settings")]
public class OrbLifetimeAndAnchorTests
{
    // --- StaleAfter ----------------------------------------------------------

    [Fact]
    public void ForeverIsNullRatherThanAVeryLongTimeSpan()
    {
        // Null is what JudgeLiveness checks for, and the distinction is real:
        // "forever" has to skip the comparison entirely rather than compare
        // against some large number, because the state a user reaches for it in
        // is precisely the one where a session has been quiet for days.
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = OrbweaverSettings.OrbLifetimeForever;
            Assert.Null(SessionManager.StaleAfter);

            OrbweaverSettings.OrbLifetimeMinutes = 30;
            Assert.Equal(30, SessionManager.StaleAfter?.TotalMinutes);
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = before;
        }
    }

    // --- EnabledFor ----------------------------------------------------------

    [Fact]
    public void ACliSwitchedOffIsIgnoredWhileEverythingWithNoSwitchIsKept()
    {
        // OpenClaw and RemoteControl have no display switch here on purpose:
        // OpenClaw's own toggle means something stronger and is consulted where
        // the gateway is asked, and there is nothing to gate a remote session
        // on beyond the bridge already being running.
        var claude = OrbweaverSettings.ClaudeCodeEnabled;
        var codex = OrbweaverSettings.CodexEnabled;
        var grok = OrbweaverSettings.GrokEnabled;
        try
        {
            OrbweaverSettings.ClaudeCodeEnabled = false;
            OrbweaverSettings.CodexEnabled = false;
            OrbweaverSettings.GrokEnabled = false;

            Assert.False(SessionManager.EnabledFor(SessionSource.ClaudeCode));
            Assert.False(SessionManager.EnabledFor(SessionSource.Codex));
            Assert.False(SessionManager.EnabledFor(SessionSource.Grok));
            Assert.True(SessionManager.EnabledFor(SessionSource.OpenClaw));
            Assert.True(SessionManager.EnabledFor(SessionSource.RemoteControl));

            OrbweaverSettings.ClaudeCodeEnabled = true;
            OrbweaverSettings.CodexEnabled = true;
            OrbweaverSettings.GrokEnabled = true;

            Assert.True(SessionManager.EnabledFor(SessionSource.ClaudeCode));
            Assert.True(SessionManager.EnabledFor(SessionSource.Codex));
            Assert.True(SessionManager.EnabledFor(SessionSource.Grok));
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeEnabled = claude;
            OrbweaverSettings.CodexEnabled = codex;
            OrbweaverSettings.GrokEnabled = grok;
        }
    }

    // --- ShiftArrangementAnchor -----------------------------------------------

    // TheFirstArrangementEverIsCentredAndThenRemembered lived here and tested
    // SessionManager.ArrangementAnchor, which seeded main's anchor at the
    // screen's middle before laying out. CB-268 removed it: with no anchors
    // ComputeGroups draws today's bands, and every group's landed anchor is
    // saved from that. The remembering it protected is now
    // SessionManagerGroupsTests.TheFirstArrangeSavesEveryGroupsAnchorAndTheNextReproducesIt.

    [Fact]
    public void DraggingTheWholeShapeMovesItsSavedCentreByTheSameDelta()
    {
        // A whole-shape drag moves every arranged orb by one delta; without the
        // anchor getting the same nudge, the next session to start or end would
        // snap the shape back to where it was before the drag.
        var before = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = new OrbweaverSettings.OrbPlacement(500, 400);

            new SessionManager().ShiftArrangementAnchor(-120, 60);

            Assert.Equal(new OrbweaverSettings.OrbPlacement(380, 460), OrbweaverSettings.ArrangeAnchor);
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = before;
        }
    }

    [Fact]
    public void AZeroDeltaAndAnUnarrangedShapeBothLeaveTheAnchorAlone()
    {
        var before = OrbweaverSettings.ArrangeAnchor;
        try
        {
            var manager = new SessionManager();

            // Nothing arranged yet, so there is no anchor to move — and a save
            // here would invent one, which would then be honoured as "where the
            // shape already is" the first time somebody did arrange.
            OrbweaverSettings.ArrangeAnchor = null;
            manager.ShiftArrangementAnchor(10, 10);
            Assert.Null(OrbweaverSettings.ArrangeAnchor);

            OrbweaverSettings.ArrangeAnchor = new OrbweaverSettings.OrbPlacement(500, 400);
            manager.ShiftArrangementAnchor(0, 0);
            Assert.Equal(new OrbweaverSettings.OrbPlacement(500, 400), OrbweaverSettings.ArrangeAnchor);
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = before;
        }
    }
}
