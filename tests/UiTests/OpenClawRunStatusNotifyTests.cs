using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 Stage 4: the poll tells every open chat how its conversation's last run
// ended, and a chat that drew a run which timed out says so. Here because the
// notification is posted to the UI thread, which only this project has.
[Collection("Settings")]
public class OpenClawRunStatusNotifyTests
{
    [AvaloniaFact]
    public void ThePollTellsAnOpenChatThatItsRunTimedOut()
    {
        var key = "agent:main:dashboard:" + Guid.NewGuid();
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;

        var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + key, "main")!;
        chat.OnAgentEvent("agent", JsonDocument.Parse(
            """{"runId":"r","stream":"assistant","data":{"text":"working on it"}}""").RootElement);

        var now = DateTime.UtcNow;
        var at = new DateTimeOffset(now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        OpenClawSessions.Parse(JsonDocument.Parse(
            "{\"sessions\":[{\"key\":\"" + key + "\",\"classification\":\"dashboard\",\"lastActivityAt\":" + at
            + ",\"status\":\"timeout\"}]}").RootElement, now);

        OpenClawSessions.NotifyRunStatuses(OpenClawSessions.OpenChats().Where(c => c.GatewayKey == key));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(chat.History, t => t.Text.Contains("time limit"));
    }
}
