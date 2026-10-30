using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6: an approval belongs to one member's session and is answered in
// that member's own panel, but the room says who is waiting. One System line per
// request, from the same record, and no prompt of its own: a room never offers an
// answer, since which member's command it would be is the one thing it cannot say.
[Collection("Settings")]
public class OpenClawRoomApprovalLineTests : IDisposable
{
    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawRoomApprovalLineTests()
    {
        Reset();
        OrbweaverSettings.OpenClawApproveEnabled = true;
    }

    public void Dispose()
    {
        Reset();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
    }

    private static void Reset()
    {
        OrbweaverSettings.OpenClawEnabled = false;
        OpenClawSessions.Restart();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static void Request(string id, string key) =>
        OpenClawSessions.OnEvent("exec.approval.requested", Json(
            "{\"id\":\"" + id + "\",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + key + "\"},"
            + "\"createdAtMs\":" + new DateTimeOffset(Now).ToUnixTimeMilliseconds() + ","
            + "\"expiresAtMs\":" + new DateTimeOffset(Now.AddMinutes(30)).ToUnixTimeMilliseconds() + "}"), Now);

    private static (OpenClawRoomChatSession Room, OpenClawChatSession Quill, OpenClawChatSession Aster) NewRoom()
    {
        var quill = new OpenClawChatSession(
            "openclaw:agent:quill:discord:channel:900", "agent:quill:discord:channel:900", "Quill");
        var aster = new OpenClawChatSession(
            "openclaw:agent:aster:discord:channel:900", "agent:aster:discord:channel:900", "Aster");
        var room = new OpenClawRoomChatSession("openclaw:room:discord:900", "#lobby");
        room.SetMembers(new[] { (quill, "Quill", "#7f7"), (aster, "Aster", "#77f") });
        return (room, quill, aster);
    }

    private static string[] Lines(OpenClawRoomChatSession room) =>
        room.History.Where(t => t.Role == ChatRole.System).Select(t => t.Text).ToArray();

    [Fact]
    public void ARequestForAMemberSaysWhoIsWaiting()
    {
        var (room, quill, _) = NewRoom();

        Request("a1", quill.GatewayKey);
        quill.OnApprovalsChanged();

        Assert.Equal(new[] { "Quill is waiting for an approval" }, Lines(room));
    }

    // Said once per request, not once per notification.
    [Fact]
    public void TheSameRequestIsAnnouncedOnceAndANewOneAgain()
    {
        var (room, quill, aster) = NewRoom();
        Request("a1", quill.GatewayKey);
        quill.OnApprovalsChanged();
        quill.OnApprovalsChanged();

        Request("a2", aster.GatewayKey);
        aster.OnApprovalsChanged();

        Assert.Equal(
            new[] { "Quill is waiting for an approval", "Aster is waiting for an approval" }, Lines(room));
    }

    // A room opened while one is already pending says so straight away.
    [Fact]
    public void ARequestAlreadyPendingWhenTheRoomIsBuiltIsAnnounced()
    {
        var key = "agent:quill:discord:channel:900";
        Request("a1", key);

        var quill = new OpenClawChatSession("openclaw:" + key, key, "Quill");
        var room = new OpenClawRoomChatSession("openclaw:room:discord:900", "#lobby");
        room.SetMembers(new[] { (quill, "Quill", "#7f7") });

        Assert.Equal(new[] { "Quill is waiting for an approval" }, Lines(room));
    }

    [Fact]
    public void TheRoomOffersNoPromptOfItsOwn()
    {
        var (room, quill, _) = NewRoom();
        Request("a1", quill.GatewayKey);
        quill.OnApprovalsChanged();

        Assert.False(room is IRemoteChatPrompts);
    }

    // A member that has left the room is no longer listened to.
    [Fact]
    public void AMemberWhoLeftTheRoomIsNoLongerAnnounced()
    {
        var (room, quill, aster) = NewRoom();
        room.SetMembers(new[] { (aster, "Aster", "#77f") });

        Request("a1", quill.GatewayKey);
        quill.OnApprovalsChanged();

        Assert.Empty(Lines(room));
    }
}
