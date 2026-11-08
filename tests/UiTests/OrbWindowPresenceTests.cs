using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// Presence on the orb: the third axis, beside identity and state. A parked
// background job or an orphaned teammate is dimmed and held still, and it wears
// the gear badge whether it is parked or working.
//
// The failure this guards against is the one the whole ticket is about, and it
// is a failure of *rendering* rather than of classification — the rules are
// covered per case in tests/UnitTests/SessionPresenceTests.cs, and none of that
// helps if UpdateFrom does not carry the answer onto the window. Fifteen orbs
// breathing at full opacity on an idle machine was exactly that gap: everything
// needed to tell them apart was parsed, and then discarded.
//
// [Collection("Settings")] because constructing an OrbWindow reads a colour
// setting in a field initializer, and OrbweaverSettings is a process-wide
// static — see tests/UiTests/SettingsCollection.cs.
[Collection("Settings")]
public class OrbWindowPresenceTests
{
    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static SessionStatus Status(
        OrbPresence presence = OrbPresence.Present,
        SessionKind kind = SessionKind.Background,
        string state = "idle",
        int pid = 4321,
        SessionSource source = SessionSource.ClaudeCode,
        // Overrides the shape the kind would imply. Needed because the two are
        // not the same axis — SessionKind is what a *gateway* conversation is,
        // LocalSessionShape is whose lifecycle a local session follows — and a
        // teammate has no kind at all, so it cannot be reached by picking one.
        LocalSessionShape? shape = null) =>
        new()
        {
            Source = source,
            State = state,
            Kind = kind,
            Presence = presence,
            SessionPid = pid,
            Cwd = "/Users/user/project",
            Lead = shape == LocalSessionShape.Teammate ? "lead-session-id" : "",
            Shape = shape ?? (kind == SessionKind.Background
                ? LocalSessionShape.Background
                : LocalSessionShape.Terminal),
        };

    // The shared pulse roster, which is what "held still" actually means — a
    // parked orb has to leave it, or the ticker keeps breathing it 20 times a
    // second. Private static, read the way TrayRemoteItemTests reaches
    // TrayController's own private menu rather than widened for a test.
    private static bool IsOnThePulseRoster(OrbWindow orb)
    {
        var field = typeof(OrbWindow).GetField(
            "Pulsing", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var roster = (IList)field!.GetValue(null)!;
        for (var i = 0; i < roster.Count; i++)
        {
            if (ReferenceEquals(roster[i], orb)) return true;
        }

        return false;
    }

    // The first line of the hover tooltip, read off the bubble the window
    // builds — the word a user actually sees, rather than the label behind it.
    private static string TipTitle(OrbWindow orb)
    {
        var tip = ToolTip.GetTip(orb.FindControl<Grid>("Root")!);
        var bubble = Assert.IsAssignableFrom<Control>(tip);
        return bubble.GetLogicalDescendants().OfType<TextBlock>().First().Text ?? "";
    }

    private static Border Badge(OrbWindow orb, string name) =>
        orb.FindControl<Border>(name)!;

    private static string Glyph(OrbWindow orb, string name) =>
        orb.FindControl<TextBlock>(name)!.Text ?? "";

    private static double Opacity(OrbWindow orb)
    {
        var root = orb.FindControl<Grid>("Root");
        Assert.NotNull(root);
        return root!.Opacity;
    }

    // --- dimming ------------------------------------------------------------

    [AvaloniaFact]
    public void AParkedSessionIsDimmedAndHeldStill()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        // Working first, so this is a transition rather than an initial value —
        // the same order the scan produces when a job's turn ends.
        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));
        Assert.Equal(OrbPresence.Present, orb.Presence);

        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        Assert.NotEqual(OrbPresence.Present, orb.Presence);
        Assert.True(Opacity(orb) < 1.0);
        Assert.False(IsOnThePulseRoster(orb));
    }

    [AvaloniaFact]
    public void AnUnparkedSessionIsDrawnAtFullOpacity()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Present));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));
    }

    [AvaloniaFact]
    public void ResumingWorkRestoresTheOpacity()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.True(Opacity(orb) < 1.0);

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));
    }

    // The un-park arm's other half, which only runs on a window that is
    // genuinely Loaded — a job resuming has to start breathing again, and every
    // branch of ApplyState ends in a StartPulse, so being back on the roster is
    // what proves the state was re-applied rather than merely un-dimmed.
    //
    // Shown, never closed, per every sibling in this suite: closing a headless
    // window corrupts a process-wide font cache.
    [AvaloniaFact]
    public void AResumedJobIsPutBackOnThePulseRosterOnceItIsLoaded()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();
        Assert.True(orb.IsLoaded);

        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.False(IsOnThePulseRoster(orb));

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));

        Assert.True(IsOnThePulseRoster(orb));
        Assert.Equal(1.0, Opacity(orb));
    }

    // Applied last in UpdateFrom, after the state block — which is the thing
    // that starts motion. In the other order an orb that parked and changed
    // state in one update would be left dim and breathing, which is the one
    // combination that reads as a bug rather than as a state.
    [AvaloniaFact]
    public void ParkingAndAStateChangeInOneUpdateStillEndsStill()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));
        orb.UpdateFrom(Status(OrbPresence.NeedsInput, state: "idle"));

        Assert.NotEqual(OrbPresence.Present, orb.Presence);
        Assert.False(IsOnThePulseRoster(orb));
        Assert.True(Opacity(orb) < 1.0);
    }

    // The force arm, whose only caller in the app is StopRecording — handing the
    // orb's motion back after dictation, which puts it on the pulse roster
    // whether or not it is parked. Driven directly here because StopRecording
    // itself needs a live VoiceRecorder and a Whisper model load, and is
    // excluded from coverage for exactly that reason.
    [AvaloniaFact]
    public void PresenceCanBeReAssertedAfterSomethingElseTookOverTheMotion()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.False(IsOnThePulseRoster(orb));

        // What the mic does: motion, on a parked orb, from outside the presence
        // axis. The un-forced call then declines, since nothing about the
        // presence changed — which is the whole reason force exists.
        orb.ApplyState("generating");
        Assert.True(IsOnThePulseRoster(orb));

        orb.ApplyPresence(OrbPresence.NeedsInput);
        Assert.True(IsOnThePulseRoster(orb));

        orb.ApplyPresence(OrbPresence.NeedsInput, force: true);

        Assert.NotEqual(OrbPresence.Present, orb.Presence);
        Assert.False(IsOnThePulseRoster(orb));
        Assert.True(Opacity(orb) < 1.0);
    }

    // A session that has never reported a state at all — an orb built from a
    // status file whose state field was empty. Un-parking has to fall back to
    // "idle" rather than handing ApplyState an empty string, which is the same
    // fallback the Loaded handler and ReapplyStateColors already make.
    [AvaloniaFact]
    public void UnparkingASessionThatNeverReportedAStateFallsBackToIdle()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status(OrbPresence.NeedsInput, state: ""));
        Assert.NotEqual(OrbPresence.Present, orb.Presence);

        orb.UpdateFrom(Status(OrbPresence.Present, state: ""));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));

        // Idle's slow breath, which is what ApplyState's default arm starts.
        Assert.True(IsOnThePulseRoster(orb));
    }

    // --- the two presence marks ---------------------------------------------

    // The daemon's own taxonomy calls a blocked job "Needs input", and several of
    // the ones this was written for are literally holding a question — so plain
    // dimming undersold them. Dim *and* marked: dim because it is not competing
    // with live work, marked because "there is something here for you" is the
    // opposite of what dimming alone says.
    [AvaloniaFact]
    public void ASessionNeedingInputIsDimmedAndMarked()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        Assert.True(Opacity(orb) < 1.0);
        Assert.Equal("needs input", orb.PresenceLabel);

        var badge = Badge(orb, "PresenceBadge");
        Assert.True(badge.IsVisible);
        Assert.Equal("?", Glyph(orb, "PresenceGlyph"));
    }

    // A finished job: dimmed the same, marked differently. The two are opposite
    // instructions — one wants you, one wants nothing ever again — and being
    // unmistakable from across a screen is the whole reason they are two states
    // rather than one dim one.
    [AvaloniaFact]
    public void AFinishedSessionIsDimmedAndMarkedDifferently()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Finished));

        Assert.True(Opacity(orb) < 1.0);
        Assert.Equal("finished", orb.PresenceLabel);
        Assert.True(Badge(orb, "PresenceBadge").IsVisible);
        Assert.Equal("✓", Glyph(orb, "PresenceGlyph"));
    }

    // A parked session — an orphaned team member, or since CB-307 any live
    // session nobody has spoken to for a while: dimmed, and deliberately
    // unbadged. Nothing is waiting on the user and nothing has finished, so the
    // badge channel ("there is something here for you") stays empty. It does get
    // the word "parked" in the tooltip, so a dim orb can say why it is dim.
    [AvaloniaFact]
    public void AnOrphanedTeammateIsDimmedWithAWordAndNoBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Parked, kind: SessionKind.Unknown));

        Assert.True(Opacity(orb) < 1.0);
        Assert.Equal("parked", orb.PresenceLabel);
        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
    }

    // CB-307: a quiet Terminal-shaped Claude Code session, the case the ticket
    // is about. Dimmed, off the pulse roster, wording in the tooltip, no badge —
    // and the very next UpdateFrom carrying Present (a new turn arrived) puts it
    // back at full brightness, on the roster, with the word gone. Loaded, so the
    // un-park arm that restores motion is the one that runs.
    [AvaloniaFact]
    public void AQuietTerminalSessionIsParkedAndClearsTheInstantANewTurnArrives()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();
        Assert.True(orb.IsLoaded);

        orb.UpdateFrom(Status(OrbPresence.Parked, kind: SessionKind.Unknown, shape: LocalSessionShape.Terminal));

        Assert.Equal(OrbPresence.Parked, orb.Presence);
        Assert.Equal(0.45, Opacity(orb));
        Assert.False(IsOnThePulseRoster(orb));
        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
        Assert.Equal("parked", orb.PresenceLabel);
        Assert.EndsWith(" · parked", TipTitle(orb));

        orb.UpdateFrom(Status(OrbPresence.Present, kind: SessionKind.Unknown,
            state: "generating", shape: LocalSessionShape.Terminal));

        Assert.Equal(OrbPresence.Present, orb.Presence);
        Assert.Equal(1.0, Opacity(orb));
        Assert.True(IsOnThePulseRoster(orb));
        Assert.Null(orb.PresenceLabel);
        Assert.DoesNotContain("parked", TipTitle(orb));
    }

    // The remote path: a peer's roster said "parked", so the orb for it is drawn
    // dimmed by the same code, whatever the source.
    [AvaloniaFact]
    public void AParkedRemoteOrbIsDimmedLikeALocalOne()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Parked, kind: SessionKind.Remote,
            source: SessionSource.RemoteControl));

        Assert.Equal(OrbPresence.Parked, orb.Presence);
        Assert.Equal(0.45, Opacity(orb));
        Assert.Equal("parked", orb.PresenceLabel);
        Assert.False(Badge(orb, "PresenceBadge").IsVisible);

        orb.UpdateFrom(Status(OrbPresence.Present, kind: SessionKind.Remote,
            source: SessionSource.RemoteControl));
        Assert.Equal(1.0, Opacity(orb));
        Assert.Null(orb.PresenceLabel);
    }

    // The mark table itself, so the glyph-less arm is pinned apart from any
    // window: parked is a word with no glyph, the other two keep theirs.
    [Fact]
    public void ParkedIsAWordWithNoGlyphWhileTheDaemonsTwoKeepTheirs()
    {
        Assert.Equal((null, "parked"), OrbWindow.PresenceMarkFor(OrbPresence.Parked));
        Assert.Equal("?", OrbWindow.PresenceMarkFor(OrbPresence.NeedsInput)!.Value.Glyph);
        Assert.Equal("\u2713", OrbWindow.PresenceMarkFor(OrbPresence.Finished)!.Value.Glyph);
        Assert.Null(OrbWindow.PresenceMarkFor(OrbPresence.Present));
    }

    // A present session carries no mark at all, which is most orbs most of the
    // time — the same argument the kind badges are held to.
    [AvaloniaFact]
    public void APresentSessionCarriesNoPresenceMark()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.Null(orb.PresenceLabel);
        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
    }

    // The mark goes away again when the session comes back, which is the case a
    // one-way "apply the badge" would quietly get wrong: an attach un-dims a
    // parked job, and a stale "?" on a session somebody is sitting in says the
    // opposite of what is true.
    [AvaloniaFact]
    public void ComingBackToLifeTakesTheMarkAwayWithTheDimming()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Assert.True(Badge(orb, "PresenceBadge").IsVisible);

        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));

        Assert.False(Badge(orb, "PresenceBadge").IsVisible);
        Assert.Equal(1.0, Opacity(orb));
    }

    // Both marks live in their own corner. The kind badge is bottom-right and the
    // heart is top-right, and a session can want two of the three said at once —
    // a background job (gear) holding a question (mark) — so a collision would be
    // one mark hidden under another rather than a layout nitpick.
    [AvaloniaFact]
    public void ThePresenceMarkAndTheKindBadgeOccupyDifferentCorners()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        var presence = Badge(orb, "PresenceBadge");
        var kind = Badge(orb, "KindBadge");

        Assert.True(presence.IsVisible);
        Assert.True(kind.IsVisible);

        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, presence.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, presence.VerticalAlignment);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, kind.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, kind.VerticalAlignment);
    }

    // A team member's orb is drawn smaller, and every badge has to move with the
    // circle or it floats off the rim. The presence mark joins the same sum the
    // other two use rather than carrying its own copy of it.
    [AvaloniaFact]
    public void ThePresenceMarkShrinksOntoATeamMembersSmallerRim()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));

        var full = Badge(orb, "PresenceBadge").Margin;

        var status = Status(OrbPresence.NeedsInput);
        status.Lead = "lead-session-id";
        orb.UpdateFrom(status);

        var member = Badge(orb, "PresenceBadge").Margin;

        Assert.True(member.Left > full.Left);
        Assert.Equal(member.Left, member.Top);
        Assert.Equal(Badge(orb, "KindBadge").Margin.Right, member.Left);
    }

    // --- the gear badge -----------------------------------------------------

    // The badge says what a session *is*, which does not change while it runs.
    // Whether anything is happening in it rides the opacity instead — so a
    // working job and a parked one wear the same mark, and only one of them is
    // dim. Badging only the parked ones would smuggle a state into the kind
    // channel.
    [AvaloniaTheory]
    [InlineData(OrbPresence.NeedsInput)]
    [InlineData(OrbPresence.Present)]
    [InlineData(OrbPresence.Finished)]
    public void ABackgroundJobWearsTheGearWhateverItsPresence(OrbPresence presence)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(presence));

        Assert.Equal("background job", orb.KindLabel);
        Assert.Equal("⚙", orb.KindGlyphText);

        var badge = orb.FindControl<Border>("KindBadge");
        Assert.NotNull(badge);
        Assert.True(badge!.IsVisible);
    }

    [AvaloniaFact]
    public void AnOrdinaryTerminalSessionStillWearsNoBadgeAtAll()
    {
        // The regression this ticket could most easily have caused: a mark on
        // almost every orb distinguishes nothing, and every local session was
        // unbadged before the gear existed.
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(kind: SessionKind.Unknown));

        Assert.Null(orb.KindLabel);
        Assert.False(orb.FindControl<Border>("KindBadge")!.IsVisible);
    }

    // --- the two menu items -------------------------------------------------

    [AvaloniaFact]
    public void BothLifecycleItemsAreOfferedForALocalSessionWithAPid()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.True(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.True(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // CB-265. Claude Desktop owns the process, so ending it from here would
    // kill a session inside someone else's app; Dismiss still clears the file.
    [AvaloniaFact]
    public void AClaudeDesktopSessionIsOfferedDismissButNotEnd()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = Status();
        status.Entrypoint = SessionStatus.DesktopEntrypoint;
        orb.UpdateFrom(status);

        Assert.True(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.False(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);

        // Control: the same status under "cli" offers both.
        var twin = new OrbWindow(Guid.NewGuid().ToString());
        var cli = Status();
        cli.Entrypoint = "cli";
        twin.UpdateFrom(cli);

        Assert.True(twin.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.True(twin.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // A hook older than the session_pid field. There is nothing to signal, so
    // End is not offered — but the file is still there to be dismissed.
    [AvaloniaFact]
    public void ASessionWithNoRecordedPidIsOfferedDismissButNotEnd()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(pid: 0));

        Assert.True(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.False(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // A gateway or bridged session's orb comes from a socket: there is no file
    // to delete and no local process to end, so neither item appears. Hidden
    // rather than greyed out — the answer to "why not" is "this conversation
    // lives on another machine", which is not something a menu can say.
    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void NeitherItemIsOfferedForASessionThatLivesSomewhereElse(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(kind: SessionKind.Remote, source: source));

        Assert.False(orb.FindControl<MenuItem>("DismissItem")!.IsVisible);
        Assert.False(orb.FindControl<MenuItem>("EndSessionItem")!.IsVisible);
    }

    // CB-26. The row is offered, and it says what it will do, until something
    // turns out to be running underneath this session's pid.
    [AvaloniaFact]
    public void TheEndRowSaysWhatItDoesForASessionWithNothingUnderneathIt()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var item = orb.FindControl<MenuItem>("EndSessionItem")!;
        Assert.True(item.IsEnabled);
        Assert.Equal("End this session", item.Header);
        Assert.Equal(
            "Stops the process behind this session. This cannot be undone.",
            ToolTip.GetTip(item));
    }

    // Disabled with the count in the header, rather than hidden — the opposite
    // call from the one CanEndSession makes for a gateway session. Hiding is
    // right when the answer to "why not" is "this conversation lives somewhere
    // else"; here the answer is "this orb looks finished and is actually your
    // window onto three running jobs", which is the thing the user does not
    // know and the reason they were about to click.
    [AvaloniaFact]
    public void TheEndRowRefusesAndNamesTheJobsWhenTheDaemonIsUnderneath()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());
        orb.ApplyEndSessionGuard(new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 3));

        var item = orb.FindControl<MenuItem>("EndSessionItem")!;
        Assert.True(item.IsVisible);
        Assert.False(item.IsEnabled);
        Assert.Equal("Can't end this: it is your view of 3 background jobs", item.Header);
        Assert.Contains("on Windows would stop the background daemon", (string)ToolTip.GetTip(item)!);
    }

    // The stale-sentence failure a menu written at open-time can still have: a
    // husk whose job has since finished has no daemon under it any more, and
    // the row it left behind would otherwise go on refusing an action that is
    // now ordinary. UpdateFrom puts the plain wording back on every scan, and
    // the Opening handler puts the real answer there a moment before anyone can
    // read it.
    [AvaloniaFact]
    public void ARefusalDoesNotOutliveTheThingItWasAbout()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());
        orb.ApplyEndSessionGuard(new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 1));

        orb.UpdateFrom(Status());

        var item = orb.FindControl<MenuItem>("EndSessionItem")!;
        Assert.True(item.IsEnabled);
        Assert.Equal("End this session", item.Header);
    }

    // --- CB-228: the End row is read off the UI thread ----------------------
    //
    // Driven through RefreshEndSessionGuardAsync rather than a real right-click,
    // for the reason the two tests above drive ApplyEndSessionGuard directly: a
    // ContextMenu's Opening needs a shown window and a working popup. The read is
    // a TaskCompletionSource, so each case decides exactly when — and in what
    // order — the answers arrive, which is what a real WMI read never lets a
    // test decide.

    private static MenuItem EndRow(OrbWindow orb) => orb.FindControl<MenuItem>("EndSessionItem")!;

    // The defect itself. The menu used to wait for the read before it could
    // appear; now the row is drawn at once, refused and saying it is checking,
    // while the read is still outstanding — which is only possible because
    // nothing on this thread is waiting for it.
    [AvaloniaFact]
    public async Task TheRowSaysItIsCheckingUntilTheReadAnswers()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var answer = new TaskCompletionSource<SessionDependents.Verdict>();
        var asked = new List<string>();
        var refresh = orb.RefreshEndSessionGuardAsync(id =>
        {
            asked.Add(id);
            return answer.Task;
        });

        Assert.False(refresh.IsCompleted);
        Assert.Equal(new[] { orb.SessionId }, asked);
        Assert.False(EndRow(orb).IsEnabled);
        Assert.Equal(SessionDependents.CheckingHeader, EndRow(orb).Header);
        Assert.Equal(SessionDependents.CheckingTip, ToolTip.GetTip(EndRow(orb)));

        answer.SetResult(SessionDependents.Nothing);
        await refresh;

        Assert.True(EndRow(orb).IsEnabled);
        Assert.Equal("End this session", EndRow(orb).Header);
    }

    // And when the answer is the husk, the row turns into the refusal with its
    // count — the CB-26 sentence, arriving late rather than not at all.
    [AvaloniaFact]
    public async Task ALateRefusalStillReachesTheRow()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var answer = new TaskCompletionSource<SessionDependents.Verdict>();
        var refresh = orb.RefreshEndSessionGuardAsync(_ => answer.Task);

        answer.SetResult(new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 3));
        await refresh;

        Assert.False(EndRow(orb).IsEnabled);
        Assert.Equal("Can't end this: it is your view of 3 background jobs", EndRow(orb).Header);
    }

    // Two opens, the second answered first: the row shows the newest open's
    // answer, and the older read finishing afterwards changes nothing.
    [AvaloniaFact]
    public async Task AnOlderReadFinishingLastDoesNotOverwriteTheNewerAnswer()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var older = new TaskCompletionSource<SessionDependents.Verdict>();
        var newer = new TaskCompletionSource<SessionDependents.Verdict>();
        var first = orb.RefreshEndSessionGuardAsync(_ => older.Task);
        var second = orb.RefreshEndSessionGuardAsync(_ => newer.Task);

        newer.SetResult(SessionDependents.Nothing);
        await second;
        older.SetResult(new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 1));
        await first;

        Assert.True(EndRow(orb).IsEnabled);
        Assert.Equal("End this session", EndRow(orb).Header);
    }

    // A read that finishes after the menu has closed writes nothing, and the
    // next scan is free to put the plain wording back.
    [AvaloniaFact]
    public async Task AReadFinishingAfterTheMenuClosedWritesNothing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var answer = new TaskCompletionSource<SessionDependents.Verdict>();
        var refresh = orb.RefreshEndSessionGuardAsync(_ => answer.Task);

        orb.SessionMenu_Closed(null, new Avalonia.Interactivity.RoutedEventArgs());
        answer.SetResult(new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 2));
        await refresh;

        Assert.Equal(SessionDependents.CheckingHeader, EndRow(orb).Header);

        orb.UpdateFrom(Status());

        Assert.True(EndRow(orb).IsEnabled);
        Assert.Equal("End this session", EndRow(orb).Header);
    }

    // The same-kind defect on this branch: a scan landing while the menu is open
    // used to reset a husk's row to an enabled "End this session" under the
    // cursor. While the menu is open the answer is held; once it closes, the
    // reset applies again — ARefusalDoesNotOutliveTheThingItWasAbout's rule.
    [AvaloniaFact]
    public async Task AScanWhileTheMenuIsOpenDoesNotOverwriteTheRefusal()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        await orb.RefreshEndSessionGuardAsync(
            _ => Task.FromResult(new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 1)));

        orb.UpdateFrom(Status());

        Assert.False(EndRow(orb).IsEnabled);
        Assert.Equal("Can't end this: it is your view of 1 background job", EndRow(orb).Header);

        orb.SessionMenu_Closed(null, new Avalonia.Interactivity.RoutedEventArgs());
        orb.UpdateFrom(Status());

        Assert.True(EndRow(orb).IsEnabled);
        Assert.Equal("End this session", EndRow(orb).Header);
    }

    // A read that fails leaves the row disabled and saying it could not check
    // — never enabled, because the read exists to find the one shape where the
    // row must refuse, and not knowing is not permission.
    [AvaloniaFact]
    public async Task AReadThatFailsSaysSoAndStaysDisabled()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        await orb.RefreshEndSessionGuardAsync(
            _ => Task.FromException<SessionDependents.Verdict>(new InvalidOperationException("no WMI")));

        Assert.False(EndRow(orb).IsEnabled);
        Assert.Equal(SessionDependents.UnknownHeader, EndRow(orb).Header);
        Assert.Equal(SessionDependents.UnknownTip, ToolTip.GetTip(EndRow(orb)));
    }

    // And one that never answers is treated the same once the timeout passes,
    // rather than leaving the row on "Checking" for as long as the menu is open.
    [AvaloniaFact]
    public async Task AReadThatHangsSaysSoAfterTheTimeout()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var never = new TaskCompletionSource<SessionDependents.Verdict>();
        await orb.RefreshEndSessionGuardAsync(_ => never.Task, TimeSpan.FromMilliseconds(50));

        Assert.False(EndRow(orb).IsEnabled);
        Assert.Equal(SessionDependents.UnknownHeader, EndRow(orb).Header);
    }

    // A failure from an older open is as stale as an answer from one: it must
    // not overwrite the newer open's answer.
    [AvaloniaFact]
    public async Task AnOlderReadFailingLastDoesNotOverwriteTheNewerAnswer()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var older = new TaskCompletionSource<SessionDependents.Verdict>();
        var first = orb.RefreshEndSessionGuardAsync(_ => older.Task);
        await orb.RefreshEndSessionGuardAsync(_ => Task.FromResult(SessionDependents.Nothing));

        older.SetException(new InvalidOperationException("late"));
        await first;

        Assert.True(EndRow(orb).IsEnabled);
        Assert.Equal("End this session", EndRow(orb).Header);
    }

    // An orb whose row is not offered is not asked about at all: a gateway
    // conversation, or a cloud session (CB-225's Archive/Delete orb), has no
    // local pid, and the read would only answer Nothing.
    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.ClaudeCloud)]
    public async Task ARowThatIsNotOfferedIsNotChecked(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(source: source, pid: 0));
        Assert.False(EndRow(orb).IsVisible);

        var asked = 0;
        await orb.RefreshEndSessionGuardAsync(_ =>
        {
            asked++;
            return Task.FromResult(SessionDependents.Nothing);
        });

        Assert.Equal(0, asked);
        Assert.NotEqual(SessionDependents.CheckingHeader, EndRow(orb).Header);
    }

    [AvaloniaFact]
    public void ALocalSessionCanResetOnlyItsDisplayedStateToIdle()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        var item = orb.FindControl<MenuItem>("ResetIdleItem")!;
        Assert.True(item.IsEnabled);
        Assert.Equal("Reset this session to idle", item.Header);
        Assert.Equal(
            "Changes this orb's displayed state only; the next hook event may update it again.",
            ToolTip.GetTip(item));
    }

    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw,
        "OpenClaw controls this session's state",
        $"This session is managed by OpenClaw, so {Brand.DisplayName} cannot reset its state.")]
    [InlineData(SessionSource.RemoteControl,
        "This session's state is controlled on its other machine",
        "This session is managed on its other machine, so it must be reset there.")]
    public void ASessionOwnedElsewhereExplainsWhyItsStateCannotBeResetHere(
        SessionSource source, string explanation, string tooltip)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(kind: SessionKind.Remote, source: source));

        var item = orb.FindControl<MenuItem>("ResetIdleItem")!;
        Assert.False(item.IsEnabled);
        Assert.Equal(explanation, item.Header);
        Assert.Equal(tooltip, ToolTip.GetTip(item));
    }

    // A parked orb that was *already* parked when its window first appeared.
    //
    // The order the scan produces every time — construct, UpdateFrom, Show — and
    // the one this suite never exercised, because its tests assert before Loaded
    // has fired. ApplyState calls StartPulse in every arm and knows nothing about
    // presence, so the Loaded handler used to set a dimmed orb breathing again a
    // moment after ApplyPresence had held it still. Dim and breathing is the shape
    // this whole ticket started from.
    [AvaloniaFact]
    public void AnOrbThatIsAlreadyParkedWhenItLoadsDoesNotStartBreathing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        orb.Show();
        Flush();

        Assert.False(IsOnThePulseRoster(orb));
        Assert.True(Opacity(orb) < 1.0);
        Assert.Equal(1.0, ((Avalonia.Media.ScaleTransform)
            orb.FindControl<Ellipse>("Orb")!.RenderTransform!).ScaleX);
    }

    // --- the click acknowledgment -------------------------------------------

    private static Ellipse Halo(OrbWindow orb) => orb.FindControl<Ellipse>("Glow")!;

    private static double HaloScale(OrbWindow orb) =>
        ((Avalonia.Media.ScaleTransform)Halo(orb).RenderTransform!).ScaleX;

    // Round eight in one assertion. The click that correctly does nothing —
    // because the session is already on screen — was indistinguishable from the
    // broken clicks the whole ticket is about: "still not working", said of a
    // gesture that had worked perfectly. So the orb answers.
    [AvaloniaFact]
    public void AnAcknowledgedClickStartsTheHalo()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.False(orb.IsAcknowledging);

        orb.Acknowledge();

        Assert.True(orb.IsAcknowledging);
    }

    // The motion itself: out and away, growing and fading the whole time. Driven
    // a tick at a time rather than waited for, the way every other animation in
    // this suite is — TickAcknowledgement is reached from the shared ticker in
    // production and directly here.
    [AvaloniaFact]
    public void TheHaloGrowsAndFadesRatherThanSwelling()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        orb.Acknowledge();
        orb.TickAcknowledgement();
        Flush();

        var firstScale = HaloScale(orb);
        var firstOpacity = Halo(orb).Opacity;

        Assert.True(firstScale >= 1.0);
        Assert.True(firstOpacity <= 1.0);

        // A later tick is further out and fainter, never back. A swell-and-return
        // would be the breath's shape at a different speed, which is the confusion
        // this channel exists to avoid.
        orb.TickAcknowledgement();
        Flush();

        Assert.True(HaloScale(orb) >= firstScale);
        Assert.True(Halo(orb).Opacity <= firstOpacity);
    }

    // And it ends, restoring the halo exactly. An acknowledgment that left the
    // glow enlarged or faded would be a permanent mark for a momentary answer.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task TheHaloReturnsToRestAndTheOrbStopsAcknowledging()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        orb.Acknowledge();

        // AckMs is 280; pump real time until it has elapsed, the pattern
        // OrbWindowClickResolutionTests uses for its own timed behaviour.
        for (var attempt = 0; attempt < 80 && orb.IsAcknowledging; attempt++)
        {
            orb.TickAcknowledgement();
            Flush();
            await System.Threading.Tasks.Task.Delay(10);
        }

        Assert.False(orb.IsAcknowledging);
        Assert.Equal(1.0, HaloScale(orb));
        Assert.Equal(1.0, Halo(orb).Opacity);
    }

    // A parked orb is deliberately held still and off the shared roster, and it
    // still has to be able to answer a click — a click on a parked orb is the
    // whole reason any of this exists. What it must *not* do is start breathing:
    // scale is the state channel, and an orb that swelled for a quarter of a
    // second would be claiming to have come back to life.
    [AvaloniaFact]
    public void AParkedOrbAcknowledgesWithoutStartingToBreathe()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Flush();

        Assert.False(IsOnThePulseRoster(orb));

        orb.Acknowledge();
        orb.TickPulse();
        Flush();

        Assert.True(orb.IsAcknowledging);

        // On the roster for the duration, because that is what drives it...
        Assert.True(IsOnThePulseRoster(orb));

        // ...and still not breathing: the orb's own scale is untouched.
        Assert.Equal(1.0, ((Avalonia.Media.ScaleTransform)
            orb.FindControl<Ellipse>("Orb")!.RenderTransform!).ScaleX);

        // Still dim, too. Acknowledging a click says nothing about presence.
        Assert.True(Opacity(orb) < 1.0);
    }

    // ...and it gives the roster back. Left on it, a parked orb would be ticked
    // forever for an animation that finished — the exact waste the "Show orbs"
    // toggle and ApplyPresence's own Pulsing.Remove exist to avoid.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task AParkedOrbLeavesTheRosterWhenTheHaloEnds()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.NeedsInput));
        Flush();

        orb.Acknowledge();
        Assert.True(IsOnThePulseRoster(orb));

        for (var attempt = 0; attempt < 80 && orb.IsAcknowledging; attempt++)
        {
            orb.TickAcknowledgement();
            Flush();
            await System.Threading.Tasks.Task.Delay(10);
        }

        Assert.False(IsOnThePulseRoster(orb));
    }

    // A breathing orb keeps breathing through an acknowledgment and stays on the
    // roster afterwards: the halo borrowed nothing from it, so it has nothing to
    // give back.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task AWorkingOrbKeepsItsBreathAfterAcknowledging()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(OrbPresence.Present, state: "generating"));
        Flush();

        Assert.True(IsOnThePulseRoster(orb));

        orb.Acknowledge();

        for (var attempt = 0; attempt < 80 && orb.IsAcknowledging; attempt++)
        {
            orb.TickAcknowledgement();
            Flush();
            await System.Threading.Tasks.Task.Delay(10);
        }

        Assert.True(IsOnThePulseRoster(orb));
    }

    // The callback GoToSession actually hands the focuser. Its whole content is
    // the hop onto the UI thread, and the click path that invokes it in
    // production is the one thing this suite must not drive — so it is driven
    // here instead of being an uncovered line that reads as an oversight.
    [AvaloniaFact]
    public void TheCallbackTheFocuserIsGivenAcknowledgesOnTheUiThread()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        orb.AcknowledgeFromAnyThread();

        // Posted rather than run inline, so nothing has happened yet.
        Assert.False(orb.IsAcknowledging);

        Dispatcher.UIThread.RunJobs();

        Assert.True(orb.IsAcknowledging);
    }

    // While dictation is running the mic owns the orb's colour and motion, and
    // StopRecording is what hands it back. An acknowledgment that fought that
    // would leave the orb mid-halo when dictation ended, so it declines — the
    // same guard ApplyPresence carries, set the same way the flyout suite sets it.
    [AvaloniaFact]
    public void AnOrbBusyRecordingDoesNotAcknowledge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        typeof(OrbWindow)
            .GetField("_recording", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(orb, true);

        orb.Acknowledge();

        Assert.False(orb.IsAcknowledging);
        Assert.False(IsOnThePulseRoster(orb));
    }

    // --- the agents-view item -----------------------------------------------

    // The `claude agents` roster, offered on the orbs the roster lists. It was
    // briefly what a double-click did, and live use reversed that within the hour
    // — "cd is taking me to the wrong window" — so the menu is where it lives now.
    // See ClickRouting.AgentsView.
    [AvaloniaFact]
    public void ABackgroundOrbOffersTheAgentsView()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(shape: LocalSessionShape.Background));

        Assert.True(orb.FindControl<MenuItem>("AgentsViewItem")!.IsVisible);
    }

    // Every other orb: the roster is jobs, not terminals, so on a terminal
    // session or a teammate it would open a window with the clicked session
    // nowhere in it.
    [AvaloniaTheory]
    [InlineData(LocalSessionShape.Terminal)]
    [InlineData(LocalSessionShape.Teammate)]
    public void NoOtherShapeOffersTheAgentsView(LocalSessionShape shape)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(shape: shape));

        Assert.False(orb.FindControl<MenuItem>("AgentsViewItem")!.IsVisible);
    }

    // It reappears and disappears with the shape, rather than latching on the
    // first status that qualified: a session the scan reclassifies must not keep
    // a menu item that no longer applies to it.
    [AvaloniaFact]
    public void TheAgentsViewItemFollowsAChangeOfShape()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.UpdateFrom(Status(shape: LocalSessionShape.Background));
        Assert.True(orb.FindControl<MenuItem>("AgentsViewItem")!.IsVisible);

        orb.UpdateFrom(Status(shape: LocalSessionShape.Terminal));
        Assert.False(orb.FindControl<MenuItem>("AgentsViewItem")!.IsVisible);
    }

    // The handler, on an orb that has never had a status. TerminalFocuser's own
    // entry point returns on null, which is what makes this safe to call at all
    // in a headless suite — with a status it would run tmux and osascript for
    // real, which is why no test here gives it one. Named as the gap it is.
    [AvaloniaFact]
    public void TheAgentsViewHandlerIsSafeBeforeAnyStatusArrives()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.AgentsView_Click(null, null!);
    }

    // Both handlers with no SessionManager current, which is this suite's
    // situation: making one current would start the status-directory watcher,
    // the scan timer and a tray icon. The null-conditional is the whole body, so
    // what is asserted is that clicking either does nothing at all rather than
    // throwing — and, more to the point, that neither can reach a real session's
    // file or pid from a test process.
    [AvaloniaFact]
    public void NeitherHandlerDoesAnythingWithNoSessionManagerCurrent()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status());

        Assert.Null(SessionManager.Instance);

        orb.Dismiss_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        orb.EndSession_Click(null, new Avalonia.Interactivity.RoutedEventArgs());

        // The click's own task, awaited: with no manager it ends at once.
        Assert.True(orb.EndSessionRecordingFailureAsync().IsCompletedSuccessfully);
    }
}
