using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Orbweaver.Tests;

// CB-308's update notice. Never presented through the real Present(): that calls
// Show() and MacOSActivation, which a headless runner must not do, so the
// constructor is built directly and Toggle is driven through its seams. The
// installer is a fake — nothing here downloads, stages or exits a process.
[Collection("Settings")]
public class UpdateNoticeWindowTests : IDisposable
{
    public void Dispose()
    {
        UpdateNoticeWindow.OpenForTests = null;
        UpdateNoticeWindow.PresentForTests = null;
        UpdateNoticeWindow.BringForwardForTests = null;
        UpdateNoticeWindow.ClearFailureMarkerForTests = null;
    }

    internal sealed class FakeInstaller : IUpdateInstaller
    {
        public int Calls;
        public Func<IProgress<UpdateProgress>, CancellationToken, Task<InstallOutcome>> Run =
            (_, _) => Task.FromResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));

        public Task<InstallOutcome> InstallAsync(
            UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Run(progress, cancellationToken);
        }
    }

    private static UpdateRelease Release(string version = "0.6.0", string body = "## Fixed\n- a thing") =>
        new("v" + version, version, false, body, Array.Empty<UpdateAsset>());

    private static (UpdateNoticeWindow Window, FakeInstaller Installer, List<string> Skipped, List<string> Urls) Build(
        UpdateRelease? release = null)
    {
        var installer = new FakeInstaller();
        var skipped = new List<string>();
        var urls = new List<string>();
        UpdateNoticeWindow.ClearFailureMarkerForTests = (_, _) => { };
        var window = new UpdateNoticeWindow(release ?? Release(), installer, r => skipped.Add(r.Version), urls.Add);
        return (window, installer, skipped, urls);
    }

    [AvaloniaFact]
    public void TheOfferShowsTheVersionNotesAndTheThreeChoices()
    {
        var (w, _, _, _) = Build();

        Assert.Equal(UpdateNoticeWindow.Phase.Offer, w.State);
        Assert.Contains("0.6.0", w.HeadlineText.Text);
        Assert.True(w.InstallButton.IsVisible);
        Assert.True(w.LaterButton.IsVisible);
        Assert.True(w.SkipButton.IsVisible);
        Assert.False(w.RetryButton.IsVisible);
        Assert.False(w.ManualButton.IsVisible);
        Assert.False(w.Bar.IsVisible);
        Assert.False(w.StatusText.IsVisible);
        Assert.NotEmpty(w.NotesPanel.Children);
        Assert.Equal("Install & restart", w.InstallButton.Content);
    }

    [AvaloniaFact]
    public async Task InstallHandsOffAndStaysUpWorkingWithoutOfferingAnythingElse()
    {
        var (w, installer, _, _) = Build();
        UpdateProgress? seen = null;
        installer.Run = (progress, _) =>
        {
            progress.Report(new UpdateProgress(UpdateStage.Downloading, 50, 200));
            seen = new UpdateProgress(UpdateStage.Downloading, 50, 200);
            return Task.FromResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));
        };

        await w.InstallAsync();

        Assert.Equal(1, installer.Calls);
        Assert.NotNull(seen);
        Assert.Equal(UpdateNoticeWindow.Phase.Working, w.State);
        Assert.True(w.Bar.IsVisible);
        Assert.False(w.InstallButton.IsVisible);
        Assert.False(w.SkipButton.IsVisible);
        Assert.False(w.RetryButton.IsVisible);
        Assert.Equal("Restarting…", w.StatusText.Text);
    }

    [AvaloniaFact]
    public async Task ProgressDrivesTheBarAndTheSentence()
    {
        var (w, installer, _, _) = Build();
        var gate = new TaskCompletionSource<InstallOutcome>();
        installer.Run = (progress, _) =>
        {
            progress.Report(new UpdateProgress(UpdateStage.Downloading, 50, 200));
            return gate.Task;
        };

        var install = w.InstallAsync();

        Assert.Equal(UpdateNoticeWindow.Phase.Working, w.State);
        Assert.False(w.Bar.IsIndeterminate);
        Assert.Equal(0.25, w.Bar.Value, 3);
        Assert.Equal("Downloading — 25%…", w.StatusText.Text);

        gate.SetResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));
        await install;
    }

    [AvaloniaFact]
    public void ProgressWithoutASizeIsIndeterminateRatherThanAFalseFraction()
    {
        var (w, _, _, _) = Build();

        w.OnProgress(new UpdateProgress(UpdateStage.Downloading, 10, 0));
        Assert.True(w.Bar.IsIndeterminate);

        w.OnProgress(new UpdateProgress(UpdateStage.Verifying, 200, 200));
        Assert.True(w.Bar.IsIndeterminate);

        w.OnProgress(new UpdateProgress(UpdateStage.Downloading, 50, 200));
        Assert.False(w.Bar.IsIndeterminate);
    }

    [AvaloniaTheory]
    [InlineData((int)UpdateStage.Downloading, 0, 0, null, "Downloading…")]
    [InlineData((int)UpdateStage.Downloading, 100, 100, null, "Downloading — 100%…")]
    [InlineData((int)UpdateStage.Downloading, 900, 100, null, "Downloading — 100%…")]
    [InlineData((int)UpdateStage.Verifying, 0, 0, null, "Verifying…")]
    [InlineData((int)UpdateStage.Staging, 0, 0, null, "Preparing…")]
    [InlineData((int)UpdateStage.Installing, 0, 0, null, "Installing…")]
    [InlineData((int)UpdateStage.Restarting, 0, 0, null, "Restarting…")]
    [InlineData((int)UpdateStage.Installing, 0, 0, "step 2 of 3", "Installing… step 2 of 3")]
    public void StageTextNamesEachStage(int stage, long done, long total, string? detail, string expected)
    {
        Assert.Equal(expected, UpdateNoticeWindow.StageText(new UpdateProgress((UpdateStage)stage, done, total, detail)));
    }

    [AvaloniaTheory]
    [InlineData((int)InstallStatus.Refused, "Move the app to Applications.")]
    [InlineData((int)InstallStatus.Failed, "Checksum mismatch.")]
    public async Task ARefusalOrFailureShowsInlineWithRetryAndManualDownload(int status, string message)
    {
        var (w, installer, _, _) = Build();
        installer.Run = (_, _) => Task.FromResult(new InstallOutcome((InstallStatus)status, message));

        await w.InstallAsync();

        Assert.Equal(UpdateNoticeWindow.Phase.Failed, w.State);
        Assert.Equal(message, w.StatusText.Text);
        Assert.True(w.StatusText.IsVisible);
        Assert.True(w.RetryButton.IsVisible);
        Assert.True(w.ManualButton.IsVisible);
        Assert.True(w.LaterButton.IsVisible);
        Assert.False(w.InstallButton.IsVisible);
        Assert.False(w.SkipButton.IsVisible);
        Assert.False(w.Bar.IsVisible);
    }

    [AvaloniaFact]
    public async Task AnInstallerThatThrowsBecomesTheSameInlineFailure()
    {
        var (w, installer, _, _) = Build();
        installer.Run = (_, _) => throw new InvalidOperationException("boom");

        await w.InstallAsync();

        Assert.Equal(UpdateNoticeWindow.Phase.Failed, w.State);
        Assert.Equal("boom", w.StatusText.Text);
    }

    [AvaloniaFact]
    public async Task RetryRunsTheInstallerAgainAndCanSucceed()
    {
        var (w, installer, _, _) = Build();
        var attempt = 0;
        installer.Run = (_, _) => Task.FromResult(++attempt == 1
            ? new InstallOutcome(InstallStatus.Failed, "network")
            : new InstallOutcome(InstallStatus.HandedOff, "ok"));

        await w.InstallAsync();
        Assert.Equal(UpdateNoticeWindow.Phase.Failed, w.State);

        await w.InstallAsync();

        Assert.Equal(2, installer.Calls);
        Assert.Equal(UpdateNoticeWindow.Phase.Working, w.State);
        Assert.False(w.RetryButton.IsVisible);
    }

    [AvaloniaFact]
    public async Task AnInstallAlreadyRunningIsNotStartedTwice()
    {
        var (w, installer, _, _) = Build();
        var gate = new TaskCompletionSource<InstallOutcome>();
        installer.Run = (_, _) => gate.Task;

        var first = w.InstallAsync();
        await w.InstallAsync();

        Assert.Equal(1, installer.Calls);
        gate.SetResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));
        await first;
    }

    [AvaloniaFact]
    public async Task ALaterWhileWorkingCancelsTheInstallAndClosesQuietly()
    {
        var (w, installer, _, _) = Build();
        w.Show();
        CancellationToken token = default;
        var started = new TaskCompletionSource();
        installer.Run = async (_, ct) =>
        {
            token = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new InstallOutcome(InstallStatus.HandedOff, "unreachable");
        };
        var closed = false;
        w.Closed += (_, _) => closed = true;

        var install = w.InstallAsync();
        await started.Task;
        w.Later();
        await install;

        Assert.True(token.IsCancellationRequested);
        Assert.True(closed);
        // Cancelled is not a failure: no inline error was drawn.
        Assert.NotEqual(UpdateNoticeWindow.Phase.Failed, w.State);
    }

    [AvaloniaFact]
    public void LaterClosesWithoutSkipping()
    {
        var (w, _, skipped, _) = Build();
        w.Show();
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.Later();

        Assert.True(closed);
        Assert.Empty(skipped);
    }

    [AvaloniaFact]
    public void SkipRecordsThisReleaseAndCloses()
    {
        var (w, _, skipped, _) = Build(Release("0.6.1"));
        w.Show();
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.Skip();

        Assert.Equal(new[] { "0.6.1" }, skipped);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task DownloadManuallyOpensTheReleasePage()
    {
        var (w, installer, _, urls) = Build(Release("0.6.0"));
        installer.Run = (_, _) => Task.FromResult(new InstallOutcome(InstallStatus.Failed, "x"));
        await w.InstallAsync();

        w.ManualButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { "https://github.com/Uplift-Foundation/Orbweaver/releases/tag/v0.6.0" }, urls);
    }

    [AvaloniaFact]
    public void ManualUrlEscapesTheTag()
    {
        var release = Release("0.6.0-beta") with { Tag = "v0.6.0-beta+1 x" };
        Assert.EndsWith("/tag/v0.6.0-beta%2B1%20x", UpdateNoticeWindow.ManualUrl(release));
    }

    [AvaloniaTheory]
    [InlineData(Key.Escape, true)]
    [InlineData(Key.Enter, false)]
    [InlineData(Key.A, false)]
    public void OnlyEscapeIsLater(Key key, bool closes)
    {
        Assert.Equal(closes, UpdateNoticeWindow.ShouldClose(key));
    }

    [AvaloniaFact]
    public void EscapeOnTheWindowActsAsLater()
    {
        var (w, _, skipped, _) = Build();
        w.Show();
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.OnWindowKeyDown(w, new KeyEventArgs { Key = Key.A });
        Assert.False(closed);

        w.OnWindowKeyDown(w, new KeyEventArgs { Key = Key.Escape });
        Assert.True(closed);
        Assert.Empty(skipped);
    }

    [AvaloniaFact]
    public async Task TheFailureMarkerIsClearedWhenTheFailureIsShownInline()
    {
        var installer = new FakeInstaller { Run = (_, _) => Task.FromResult(new InstallOutcome(InstallStatus.Failed, "x")) };
        var cleared = new List<string>();
        UpdateNoticeWindow.ClearFailureMarkerForTests = (_, v) => cleared.Add(v);
        var w = new UpdateNoticeWindow(Release("0.6.0"), installer, _ => { }, _ => { });

        await w.InstallAsync();

        Assert.Equal(new[] { "0.6.0" }, cleared);
    }

    // ---- the singleton -------------------------------------------------------

    [AvaloniaFact]
    public void ToggleOpensOneWindowAndPresentsIt()
    {
        var presented = new List<UpdateNoticeWindow>();
        UpdateNoticeWindow.PresentForTests = presented.Add;

        UpdateNoticeWindow.Toggle(Release(), new FakeInstaller());

        Assert.Single(presented);
        Assert.Same(presented[0], UpdateNoticeWindow.OpenForTests);
    }

    [AvaloniaFact]
    public void ASecondToggleBringsTheOpenWindowForwardAndRetargetsIt()
    {
        var presented = new List<UpdateNoticeWindow>();
        var forward = new List<UpdateNoticeWindow>();
        UpdateNoticeWindow.PresentForTests = presented.Add;
        UpdateNoticeWindow.BringForwardForTests = forward.Add;

        UpdateNoticeWindow.Toggle(Release("0.6.0"), new FakeInstaller());
        UpdateNoticeWindow.Toggle(Release("0.7.0"), new FakeInstaller());

        Assert.Single(presented);
        Assert.Single(forward);
        Assert.Contains("0.7.0", presented[0].HeadlineText.Text);
        Assert.Equal("0.7.0", presented[0].Release.Version);
    }

    [AvaloniaFact]
    public async Task ARunningInstallIsNotRetargetedUnderneathItself()
    {
        var presented = new List<UpdateNoticeWindow>();
        UpdateNoticeWindow.PresentForTests = presented.Add;
        UpdateNoticeWindow.BringForwardForTests = _ => { };
        var installer = new FakeInstaller();
        var gate = new TaskCompletionSource<InstallOutcome>();
        installer.Run = (_, _) => gate.Task;

        UpdateNoticeWindow.Toggle(Release("0.6.0"), installer);
        var install = presented[0].InstallAsync();
        UpdateNoticeWindow.Toggle(Release("0.7.0"), installer);

        Assert.Equal("0.6.0", presented[0].Release.Version);
        Assert.Equal(UpdateNoticeWindow.Phase.Working, presented[0].State);

        gate.SetResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));
        await install;
    }

    // ---- release notes -------------------------------------------------------

    [AvaloniaFact]
    public void GitHubReleaseMarkdownBecomesRealControlsNotLiteralMarkup()
    {
        const string body =
            "## What's new\n\n- **Faster** startup\n- Fixed `crash` on exit\n\n1. first\n2. second\n\n"
            + "> a quote\n\n```\ncode here\n```\n\nPlain *paragraph* text.";

        var controls = UpdateNotesView.Build(body);

        Assert.True(controls.Count >= 7);
        var text = string.Join("\n", Flatten(controls));
        Assert.DoesNotContain("##", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.Contains("Faster", text);
        Assert.Contains("code here", text);
        Assert.Contains("a quote", text);
    }

    [AvaloniaFact]
    public void AnEmptyBodySaysSoInsteadOfShowingNothing()
    {
        foreach (var body in new string?[] { null, "", "   \n " })
        {
            var controls = UpdateNotesView.Build(body);
            Assert.Single(controls);
            Assert.Contains("No release notes", Flatten(controls).Single());
        }
    }

    private static IEnumerable<string> Flatten(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            switch (control)
            {
                case TextBlock t:
                    yield return t.Inlines is { Count: > 0 }
                        ? string.Concat(t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text))
                        : t.Text ?? "";
                    break;
                case Panel p:
                    foreach (var s in Flatten(p.Children.OfType<Control>())) yield return s;
                    break;
                case Border b when b.Child is Control child:
                    foreach (var s in Flatten(new[] { child })) yield return s;
                    break;
            }
        }
    }
}
