using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-447 (frames-v18): a Paperclip row's persona and the two prefs that gate things the phone asks for.
// The persona is the agent's name, the two letters its orb wears and the picture's hash; it goes only to a
// phone whose stored prefs say paperclip_personas true, and a Paperclip agent is never in the personas frame.
public class RelayPaperclipPersonaTests : IDisposable
{
    private const string Agent = "3f2a9c4e-6b1d-4e87-a5c3-9d0e7b2f1a64";
    private static readonly RelayPersona Atlas = new("Atlas", "AT", null, "c1334a1a8c0660d2", new string('a', 64));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb447-persona-" + Guid.NewGuid());
    public RelayPaperclipPersonaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    // ---- the id and the persona ----------------------------------------------------------

    [Fact]
    public void The_id_is_the_contracts_worked_example_and_does_not_depend_on_the_name()
    {
        Assert.Equal("c1334a1a8c0660d2", PersonaFrames.PaperclipPersonaId(Agent));
        Assert.Equal(PersonaFrames.PaperclipPersonaId(Agent), RelayPersonaRows.ForPaperclip("paperclip:" + Agent, "Atlas", null, _ => null)!.Id);
        Assert.Equal(PersonaFrames.PaperclipPersonaId(Agent), RelayPersonaRows.ForPaperclip("paperclip:" + Agent, "Renamed", null, _ => null)!.Id);
        Assert.NotEqual(PersonaFrames.PaperclipPersonaId(Agent), PersonaFrames.PaperclipPersonaId("00000000-0000-4000-8000-000000000003"));
    }

    [Fact]
    public void A_paperclip_persona_is_name_initials_id_and_the_picture_hash_only_when_the_desk_can_offer_it()
    {
        var asked = new List<string>();
        string? Offer(string path) { asked.Add(path); return new string('b', 64); }

        var with = RelayPersonaRows.ForPaperclip("paperclip:" + Agent, "Atlas", "/cache/atlas.png", Offer)!;
        Assert.Equal(("Atlas", "AT", null, "c1334a1a8c0660d2", new string('b', 64)), (with.Name, with.Initials, with.Color, with.Id, with.ImageHash));
        Assert.Equal(["/cache/atlas.png"], asked);

        // No cached picture: initials, and the store is not asked. A picture the store cannot offer yet: the same.
        asked.Clear();
        Assert.Null(RelayPersonaRows.ForPaperclip("paperclip:" + Agent, "Atlas", null, Offer)!.ImageHash);
        Assert.Null(RelayPersonaRows.ForPaperclip("paperclip:" + Agent, "Atlas", "", Offer)!.ImageHash);
        Assert.Empty(asked);
        Assert.Null(RelayPersonaRows.ForPaperclip("paperclip:" + Agent, "Atlas", "/cache/atlas.png", _ => null)!.ImageHash);

        // An agent with no name has no persona (initials of nothing), and a bare id works as well as a prefixed one.
        Assert.Null(RelayPersonaRows.ForPaperclip("paperclip:" + Agent, " ", "/cache/atlas.png", Offer));
        Assert.Equal("c1334a1a8c0660d2", RelayPersonaRows.ForPaperclip(Agent, "Atlas", null, Offer)!.Id);
    }

    // ---- who gets it -----------------------------------------------------------------------

    private static RelaySession Claude() => new("c1", "claude", "proj", "/c", "idle", null, new RelayPersona("Juno", "Ju", null, "1111111111111111"));
    private static RelaySession Paperclip(RelayPersona? persona) => new("paperclip:" + Agent, "paperclip", "Atlas", "", "idle", null, persona);

    [Fact]
    public void ForPhone_strips_only_paperclip_personas_and_only_for_a_phone_that_did_not_ask()
    {
        var rows = new[] { Claude(), Paperclip(Atlas) };
        var stripped = RelayRoster.ForPhone(rows, paperclipPersonas: false);
        Assert.Null(stripped[1].Persona);
        Assert.Equal(rows[0], stripped[0]);                                    // a Claude row's persona is untouched
        Assert.Equal(rows[1] with { Persona = null }, stripped[1]);            // and nothing else on the row moved

        Assert.Same(rows, RelayRoster.ForPhone(rows, paperclipPersonas: true));                             // asked: the very list
        var plain = new[] { Claude(), Paperclip(null) };
        Assert.Same(plain, RelayRoster.ForPhone(plain, paperclipPersonas: false));                          // nothing to strip: the very list
    }

    [Fact]
    public void A_paperclip_agent_is_never_a_row_of_the_personas_frame()
    {
        var rows = RelayPersonaRows.Rows(new[] { Claude(), Paperclip(Atlas) }, defaultPersona: null, _ => null);
        Assert.Equal(["1111111111111111"], rows.Select(r => r.Id));
    }

    // ---- the prefs -------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"done_alerts":"all","paperclip_personas":true}""", true, false)]
    [InlineData("""{"done_alerts":"all","paperclip_unreachable":true}""", false, true)]
    [InlineData("""{"done_alerts":"all","paperclip_personas":true,"paperclip_unreachable":true}""", true, true)]
    [InlineData("""{"done_alerts":"all","paperclip_personas":false,"paperclip_unreachable":false}""", false, false)]
    [InlineData("""{"done_alerts":"all"}""", false, false)]
    [InlineData("""{"done_alerts":"all","paperclip_personas":"true","paperclip_unreachable":1}""", false, false)]
    [InlineData("""{"done_alerts":"all","paperclip_personas":null,"paperclip_unreachable":"yes"}""", false, false)]
    public void Only_the_json_true_turns_either_key_on_and_the_rest_of_the_frame_still_parses(string frame, bool personas, bool unreachable)
    {
        var prefs = AlertPrefs.Parse(J(frame));
        Assert.NotNull(prefs);
        Assert.Equal((personas, unreachable), (prefs!.Value.PaperclipPersonas, prefs.Value.PaperclipUnreachable));
        Assert.Equal(DoneAlerts.All, prefs.Value.DoneAlerts);
    }

    [Fact]
    public void A_phone_that_never_sent_prefs_has_neither_key_and_a_bad_done_alerts_still_refuses_the_frame()
    {
        Assert.False(AlertPrefs.Default.PaperclipPersonas);
        Assert.False(AlertPrefs.Default.PaperclipUnreachable);
        Assert.Null(AlertPrefs.Parse(J("""{"done_alerts":"sometimes","paperclip_personas":true}""")));
        Assert.True(AlertPrefs.FromStored("all", true, false, true, true)!.Value.PaperclipPersonas);
        Assert.True(AlertPrefs.FromStored("all", true, false, true, true)!.Value.PaperclipUnreachable);
        Assert.False(AlertPrefs.FromStored("all", true, false, null, null)!.Value.PaperclipPersonas);          // a file from before the keys
        Assert.Null(AlertPrefs.FromStored(null, null, null, true, true));
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    [Fact]
    public void Both_keys_survive_a_restart_and_a_later_prefs_frame_replaces_them()
    {
        var store = new MemoryStore();
        var id = new RelayIdentity(_dir, store);
        id.Keys();
        id.AddPhone("ph_00000000000000e2", "Test iPhone", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
        Assert.True(id.SaveAlerts("ph_00000000000000e2", new AlertPrefs(DoneAlerts.All, true, false, true, true)));

        var got = new RelayIdentity(_dir, store).Phones.Single().Alerts!.Value;
        Assert.Equal((true, true), (got.PaperclipPersonas, got.PaperclipUnreachable));

        Assert.True(id.SaveAlerts("ph_00000000000000e2", new AlertPrefs(DoneAlerts.All, true, false, false, true)));
        got = new RelayIdentity(_dir, store).Phones.Single().Alerts!.Value;
        Assert.Equal((false, true), (got.PaperclipPersonas, got.PaperclipUnreachable));
    }
}
