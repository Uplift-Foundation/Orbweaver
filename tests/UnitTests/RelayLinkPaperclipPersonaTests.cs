using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-447 (frames-v18), through the link: the roster one phone is sent carries a Paperclip row's persona only
// after that phone said paperclip_personas true, and flipping the key re-sends a whole roster in either direction.
public partial class RelayLinkTests
{
    private static readonly RelayPersona AtlasPersona = new("Atlas", "AT", null, "c1334a1a8c0660d2", new string('a', 64));

    private static RelaySession AtlasRow() =>
        new("paperclip:3f2a9c4e-6b1d-4e87-a5c3-9d0e7b2f1a64", "paperclip", "Atlas", "", "idle", null, AtlasPersona);

    private static JsonElement RowOf(JsonElement roster, string id) =>
        roster.GetProperty("sessions").EnumerateArray().Single(r => r.GetProperty("session_id").GetString() == id);

    private static JsonElement? RosterIn(FakePhone phone, IEnumerable<string> frames) =>
        frames.Select(f => J(f)).Where(e => e.GetProperty("class").GetString() == "state")
            .Select(e => phone.OpenFromDesktop(e.ToString())).Cast<JsonElement?>()
            .FirstOrDefault(p => p!.Value.GetProperty("t").GetString() == "roster");

    [Fact]
    public void A_phone_that_did_not_ask_is_sent_the_paperclip_row_without_a_persona_and_one_that_did_is_sent_it()
    {
        var (link, _, phone, _, _) = Paired();
        var first = RosterIn(phone, link.OnScan([Row("c1"), AtlasRow()]));
        Assert.NotNull(first);
        Assert.False(RowOf(first!.Value, AtlasRow().SessionId).TryGetProperty("persona", out _));

        // The key flips on: the reply is prefs.ok and a fresh whole roster, now with the persona.
        var onFrames = link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"all","amber_members":true,"paperclip_personas":true}"""));
        var withKey = RosterIn(phone, onFrames);
        Assert.NotNull(withKey);
        var persona = RowOf(withKey!.Value, AtlasRow().SessionId).GetProperty("persona");
        Assert.Equal(("Atlas", "AT", "c1334a1a8c0660d2", new string('a', 64)),
            (persona.GetProperty("name").GetString(), persona.GetProperty("initials").GetString(),
             persona.GetProperty("id").GetString(), persona.GetProperty("image_hash").GetString()));
        Assert.False(persona.TryGetProperty("color", out _));

        // A prefs frame that leaves the key as it was sends no roster.
        Assert.Null(RosterIn(phone, link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads","paperclip_personas":true}"""))));

        // The next scan keeps it for this phone; flipping it off takes it away again with a fresh roster.
        var offFrames = link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"all"}"""));
        var withoutKey = RosterIn(phone, offFrames);
        Assert.NotNull(withoutKey);
        Assert.False(RowOf(withoutKey!.Value, AtlasRow().SessionId).TryGetProperty("persona", out _));
    }

    [Fact]
    public void The_roster_of_a_phone_that_did_not_ask_is_byte_for_byte_the_roster_of_rows_with_no_persona()
    {
        var withPersona = RelayRoster.ForPhone([Row("c1"), AtlasRow()], paperclipPersonas: false);
        var never = new[] { Row("c1"), AtlasRow() with { Persona = null } };
        Assert.Equal(never, withPersona);
        var a = new RelayPhoneStream().Roster(withPersona);
        var b = new RelayPhoneStream().Roster(never);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(b), System.Text.Encoding.UTF8.GetString(a));
    }
}
