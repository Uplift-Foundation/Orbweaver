using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// The un-seamed arms of UpdateServices/UpdateMenu: the real routes to the
// notice and message windows, with only their Present() halves replaced so no
// window is shown (CB-308).
[Collection("Settings")]
public class UpdateServicesWindowRoutingTests : IDisposable
{
    public void Dispose()
    {
        UpdateNoticeWindow.PresentForTests = null;
        UpdateNoticeWindow.OpenForTests = null;
        UpdateMessageWindow.PresentForTests = null;
        UpdateMessageWindow.OpenForTests = null;
        UpdateServices.Checker = null;
        UpdateMenu.PendingForTests = null;
    }

    private static UpdateRelease Release() =>
        new("v0.6.0", "0.6.0", false, "n", Array.Empty<UpdateAsset>());

    [AvaloniaFact]
    public void AnAvailableResultReachesTheRealNoticeWindow()
    {
        var presented = new List<UpdateNoticeWindow>();
        UpdateNoticeWindow.PresentForTests = presented.Add;

        UpdateServices.OnCompleted(
            new UpdateCheckResult(UpdateCheckOutcome.Available, Release(), null, true), "0.5.0");

        Assert.Single(presented);
        Assert.Equal("0.6.0", presented[0].Release.Version);
    }

    [AvaloniaFact]
    public void AManualUpToDateReachesTheRealMessageWindow()
    {
        var presented = new List<UpdateMessageWindow>();
        UpdateMessageWindow.PresentForTests = presented.Add;

        UpdateServices.OnCompleted(new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null, true), "0.5.0");

        Assert.Single(presented);
        Assert.Equal("You're up to date", presented[0].Heading.Text);
    }

    [AvaloniaFact]
    public void ClickingAPendingTrayItemWithNoSeamsOpensTheRealNotice()
    {
        var presented = new List<UpdateNoticeWindow>();
        UpdateNoticeWindow.PresentForTests = presented.Add;
        UpdateMenu.PendingForTests = () => Release();

        UpdateMenu.Activate();

        Assert.Single(presented);
    }

    [AvaloniaFact]
    public async Task WithARealCheckerPendingComesFromItNotTheSeam()
    {
        var settings = new Settings();
        var checker = new UpdateChecker(new Feed(Release()), settings, "0.5.0",
            () => DateTimeOffset.UtcNow, _ => { }, (_, _) => new Nothing());
        UpdateServices.Checker = checker;

        Assert.Null(UpdateMenu.PendingVersion());
        await checker.CheckNowAsync();
        Assert.Equal("0.6.0", UpdateMenu.PendingVersion());
    }

    private sealed class Nothing : IDisposable { public void Dispose() { } }

    private sealed class Settings : IUpdateSettings
    {
        public bool AutoCheck { get; set; }
        public bool IncludePrerelease { get; set; }
        public string? SkippedVersion { get; set; }
        public DateTimeOffset? LastCheck { get; set; }
    }

    private sealed class Feed : IReleaseFeed
    {
        private readonly UpdateRelease _r;
        public Feed(UpdateRelease r) => _r = r;
        public Task<IReadOnlyList<UpdateRelease>> FetchAsync(bool p, System.Threading.CancellationToken c) =>
            Task.FromResult<IReadOnlyList<UpdateRelease>>(new[] { _r });
    }
}
