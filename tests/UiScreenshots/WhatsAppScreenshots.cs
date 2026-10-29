using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 S5: what WhatsApp looks like once nothing on screen is an identifier and
// nothing the machine wrote is drawn as a person.
//
// Two captures, both of scrubbed fixtures: the field structure is real
// (2026.10.5, read-only) and every number, group id, name and sentence is
// invented. Nothing here carries a phone number or a raw group id, and QA checks
// each PNG for a run of five or more digits before the pictures are published.
//
// The room orb is the group's name where it used to be its id, and the chat
// panel shows an exec notice and a reminder as dim notes beside one message the
// operator really typed, which is the pairing that makes the picture a
// comparison and not just a screenshot.
[Collection("Settings")]
public class WhatsAppScreenshots : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
        OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
    }

    // Never closed, for the reason ChatPanelScreenshots gives: closing a
    // headless Window here corrupts a process-wide FontManager cache.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // A two-agent WhatsApp group, registered the way a poll registers it so the
    // room orb has members to wear, and titled by the subject through the real
    // title rule.
    [AvaloniaFact]
    public void AWhatsAppGroupRoomOrbIsTitledWithItsName()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string Row(string agent) => $$"""
            {"key":"agent:{{agent}}:whatsapp:group:120000000000000001@g.us","classification":"group","kind":"group",
             "chatType":"group","peerKind":"group","subject":"Studio Crew",
             "origin":{"provider":"whatsapp","chatType":"group","label":"120000000000000001@g.us"},
             "lastActivityAt":{{now}}}
            """;

        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        var sessions = OpenClawSessions.Parse(
            JsonDocument.Parse("{\"sessions\":[" + Row("alpha") + "," + Row("beta") + "]}").RootElement,
            DateTime.UtcNow).Sessions;

        // What the room orb is called is what the scan derives from a member.
        var title = SessionManager.RoomTitle(sessions[0].Title);
        Assert.Equal("Studio Crew", title);

        var orb = new OrbWindow(SessionManager.RoomId("whatsapp:120000000000000001@g.us"));
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            IsRoom = true,
            State = "idle",
            Title = title,
            Kind = SessionKind.Channel,
            Color = "#5F87D7",
        });

        ScreenshotHelper.Capture(orb, "whatsapp-group-room-orb.png");
    }

    // An exec notice and a reminder as dim notes, then a message the operator
    // typed, in the panel of an agent's main session a WhatsApp conversation is
    // routed into. Parsed by the real history parser and shown by a real chat
    // session, so the demotion is what is being photographed.
    [AvaloniaFact]
    public void ExecNoticesAndRemindersAreNotesBesideARealTypedMessage()
    {
        const string page = """
            [{"role":"user","content":"Exec completed (calm-otter, code 0) :: build finished",
              "timestamp":1787000000000,
              "idempotencyKey":"3f1c8ad2-59b7-4e06-9c31-8ab7205de164:user",
              "provenance":{"kind":"internal_system","sourceTool":"exec"},
              "__openclaw":{"senderIsOwner":false}},
             {"role":"user","content":"A scheduled reminder has been triggered. The reminder content is: stand up and stretch",
              "timestamp":1787000001000},
             {"role":"user","content":"are the reminders going out?",
              "timestamp":1787000002000,
              "idempotencyKey":"7a4d19e0-0c52-4b8e-a6d3-91f0c4ba7d58:user"},
             {"role":"assistant","content":[{"type":"text","text":"Yes, the next one is due in an hour."}],
              "timestamp":1787000003000}]
            """;

        var chat = new OpenClawChatSession(
            "openclaw:agent:alpha:main", "agent:alpha:main", "Alpha") { HasMore = false };
        chat.SetHistory(OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(page).RootElement, null));
        _sessionIdsToClean.Add(chat.SessionId);

        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            State = "idle",
            Title = "alpha — main",
            Kind = SessionKind.Main,
            Color = "#5F87D7",
        });

        ChatPanel.OpenFor(orb, chat);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(ChatPanelTestAccess.Instance!, "whatsapp-machine-turns-as-notes.png");
    }
}
