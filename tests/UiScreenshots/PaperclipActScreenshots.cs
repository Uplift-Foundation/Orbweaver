using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-427: captures for answering a waiting Paperclip agent in place. Hand-written
// one per scenario; only real Skia can say whether the amber card, its two
// buttons and the link read as a permission prompt at the size a person sees.
// Nothing here clicks: the panel is built from hand-made needs and never
// reaches a Paperclip.
[Collection("Settings")]
public class PaperclipActScreenshots : IDisposable
{
    public void Dispose() => PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");

    private static SessionStatus Status(string state, OrbPresence presence, string agentStatus, params PaperclipNeed[] needs) => new()
    {
        Source = SessionSource.Paperclip, State = state, Title = "Atlas", Cwd = "", Presence = presence,
        PaperclipAgentId = "a1", PaperclipAgentStatus = agentStatus, Needs = needs,
        Url = "http://127.0.0.1:3100/CB/agents/atlas",
    };

    private static readonly PaperclipNeed Hire = new("approval", "ap1", "Hire: QA engineer", "http://127.0.0.1:3100/CB/approvals/ap1");
    private static readonly PaperclipNeed Tool = new("action", "ac1", "Run the deploy tool on staging", "http://127.0.0.1:3100/CB/inbox/requests");
    private static readonly PaperclipNeed Stuck = new("blocked", "i1", "Blocked: CB-4 Ship the release", "http://127.0.0.1:3100/CB/issues/i1");

    private static PaperclipNeedsPanel Panel(SessionStatus s)
    {
        var p = new PaperclipNeedsPanel(() => null, _ => { });
        p.Present(s);
        return p;
    }

    [AvaloniaFact]
    public void TheAmberFlyoutListsEachNeedWithApproveDeclineAndReview()
    {
        var p = Panel(Status("waiting", OrbPresence.NeedsInput, "idle", Hire, Tool));
        Assert.Equal(2, p.NeedRows.Count);
        Assert.All(p.NeedRows, r => Assert.NotNull(r.ApproveButton));
        ScreenshotHelper.Capture(p, "paperclip-act-flyout-needs.png");
    }

    [AvaloniaFact]
    public void ABlockedTaskShowsOnlyTheLink()
    {
        var p = Panel(Status("waiting", OrbPresence.NeedsInput, "idle", Stuck));
        Assert.Null(p.RowFor("i1")!.ApproveButton);
        ScreenshotHelper.Capture(p, "paperclip-act-flyout-blocked.png");
    }

    [AvaloniaFact]
    public void AFailedAnswerSaysWhyAndStaysAnswerable()
    {
        var fake = new Refusing();
        var p = new PaperclipNeedsPanel(() => fake, _ => { });
        p.Present(Status("waiting", OrbPresence.NeedsInput, "idle", Hire));
        p.AnswerAsync(p.RowFor("ap1")!, true).GetAwaiter().GetResult();
        Assert.True(p.RowFor("ap1")!.Status.IsVisible);
        ScreenshotHelper.Capture(p, "paperclip-act-flyout-failed.png");
    }

    private static void CaptureMenu(OrbWindow orb, string fileName)
    {
        orb.Show();
        orb.Width = 460;
        orb.Height = 560;
        ScreenshotHelper.Flush();
        var root = orb.FindControl<Grid>("Root")!;
        var menu = root.ContextMenu!;
        menu.Open(root);
        ScreenshotHelper.Flush();
        try { ScreenshotHelper.CaptureControl(menu, fileName); }
        finally { menu.Close(); }
    }

    [AvaloniaFact]
    public void TheMenuOffersWakeAndPauseOnAnIdleAgentAndResumeOnAPausedOne()
    {
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "t");
        var idle = new OrbWindow("paperclip:atlas");
        idle.UpdateFrom(Status("idle", OrbPresence.Present, "idle"));
        Assert.True(idle.FindControl<MenuItem>("WakePaperclipItem")!.IsVisible);
        Assert.True(idle.FindControl<MenuItem>("PausePaperclipItem")!.IsVisible);
        CaptureMenu(idle, "paperclip-act-menu-idle.png");

        var paused = new OrbWindow("paperclip:atlas-paused");
        paused.UpdateFrom(Status("idle", OrbPresence.Parked, "paused"));
        Assert.True(paused.FindControl<MenuItem>("ResumePaperclipItem")!.IsVisible);
        Assert.False(paused.FindControl<MenuItem>("WakePaperclipItem")!.IsVisible);
        CaptureMenu(paused, "paperclip-act-menu-paused.png");
    }

    // Every answer refused, as a revoked key would be.
    private sealed class Refusing : IPaperclipActions
    {
        private static readonly Task<PaperclipOutcome> No =
            Task.FromResult(new PaperclipOutcome(PaperclipActionResult.NeedsReconnect, PaperclipActions.ReconnectWhy));
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed n, bool approve, CancellationToken ct) => No;
        public Task<PaperclipOutcome> WakeAsync(string a, CancellationToken ct) => No;
        public Task<PaperclipOutcome> PauseAsync(string a, CancellationToken ct) => No;
        public Task<PaperclipOutcome> ResumeAsync(string a, CancellationToken ct) => No;
    }
}
