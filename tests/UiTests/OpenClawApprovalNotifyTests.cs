using System;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 Stage 6: a panel open on a session is told when that session's pending
// approvals change, on the UI thread. Here because the notification is posted to
// the dispatcher, which only this project has. The record shape is declared, not
// measured; see OpenClawApprovalsTests.
[Collection("Settings")]
public class OpenClawApprovalNotifyTests : IDisposable
{
    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;

    // ChatFor answers null unless OpenClaw is on.
    public OpenClawApprovalNotifyTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OrbweaverSettings.OpenClawEnabled = true;
    }

    public void Dispose()
    {
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OrbweaverSettings.OpenClawEnabled = _enabled;
    }

    private static JsonElement Row(string id, string key) => JsonDocument.Parse(
        "{\"id\":\"" + id + "\",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + key + "\"}}").RootElement;

    [AvaloniaFact]
    public void AnOpenPanelIsToldWhenAnApprovalArrivesAndWhenItIsResolved()
    {
        var key = "agent:alpha:dashboard:" + Guid.NewGuid();
        var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + key, "Alpha")!;
        var told = 0;
        chat.ApprovalsChanged += () => told++;

        OpenClawSessions.OnEvent("exec.approval.requested", Row("a1", key), DateTime.UtcNow);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, told);
        Assert.Equal("a1", Assert.Single(OpenClawSessions.PendingFor(key)).Id);

        OpenClawSessions.OnEvent("exec.approval.resolved", JsonDocument.Parse(
            "{\"id\":\"a1\",\"decision\":\"deny\"}").RootElement, DateTime.UtcNow);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, told);
        Assert.Empty(OpenClawSessions.PendingFor(key));
    }

    // A session nobody has opened has no panel to tell; the orb reads the state.
    [AvaloniaFact]
    public void ASessionWithNoPanelIsNotNotifiedButStillGoesWaiting()
    {
        var key = "agent:alpha:dashboard:" + Guid.NewGuid();

        OpenClawSessions.OnEvent("exec.approval.requested", Row("a2", key), DateTime.UtcNow);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("waiting", OpenClawSessions.StateFor(key));
    }

    // An approval the panel is not open for does not notify an unrelated panel.
    [AvaloniaFact]
    public void AnApprovalForAnotherSessionDoesNotNotifyThisPanel()
    {
        var key = "agent:alpha:dashboard:" + Guid.NewGuid();
        var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + key, "Alpha")!;
        var told = 0;
        chat.ApprovalsChanged += () => told++;

        OpenClawSessions.OnEvent(
            "exec.approval.requested", Row("a3", "agent:beta:dashboard:" + Guid.NewGuid()), DateTime.UtcNow);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, told);
    }
}
