using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-314: what a paired phone is told about this machine's sessions — which
/// sessions, under what name and state, and when a scan becomes a roster
/// rather than a delta. Plan §2.4.
/// </summary>
public class RelayRosterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);

    private static (string, SessionStatus) S(string id, string state = "idle", string title = "", string cwd = "/Users/someone/Source/proj",
        SessionSource source = SessionSource.ClaudeCode, string lead = "", string agent = "") =>
        (id, new SessionStatus { State = state, Title = title, Cwd = cwd, Source = source, Lead = lead, Agent = agent });

    private static RelaySession Row(string id, string state = "idle", string title = "proj", string? parent = null, RelayPersona? persona = null, string cwd = "/c") =>
        new(id, "claude", title, cwd, state, parent, persona);

    [Theory]
    [InlineData("generating", "generating")]
    [InlineData("waiting", "waiting")]
    [InlineData("idle", "idle")]
    [InlineData("", "idle")]
    [InlineData(null, "idle")]
    [InlineData("blocked", "idle")]   // anything the orb itself would draw as idle
    public void State_maps_to_the_orbs_own_reading(string? state, string expected) =>
        Assert.Equal(expected, RelayRoster.MapState(state));

    [Fact]
    public void Project_offers_local_clis_only_ordered_by_id()
    {
        var rows = RelayRoster.Project([
            S("b", source: SessionSource.Codex),
            S("a"),
            S("c", source: SessionSource.Grok),
            S("x", source: SessionSource.OpenClaw),
            S("y", source: SessionSource.RemoteControl),
            S("z", source: SessionSource.ClaudeCloud),
        ]);
        Assert.Equal(["a", "b", "c"], rows.Select(r => r.SessionId));
        Assert.Equal(["claude", "codex", "grok"], rows.Select(r => r.Cli));
    }

    [Fact]
    public void Project_names_by_agent_then_title_then_folder()
    {
        var rows = RelayRoster.Project([
            S("1", title: "Chat", agent: "MenuUX"),
            S("2", title: "Chat"),
            S("3", cwd: "/Users/someone/Source/Orbweaver/"),
        ]);
        Assert.Equal(["MenuUX", "Chat", "Orbweaver"], rows.Select(r => r.Title));
    }

    [Fact]
    public void Project_carries_lead_for_claude_code_only_and_the_persona_seam()
    {
        var persona = new RelayPersona("Jennifer Voss", "JV", "purple");
        var rows = RelayRoster.Project([
            S("m", lead: "L"),
            S("n", lead: "L", source: SessionSource.Codex),
            S("o"),
        ], status => status.Lead == "L" ? persona : null);
        Assert.Equal("L", rows[0].ParentSessionId);
        Assert.Null(rows[1].ParentSessionId);
        Assert.Null(rows[2].ParentSessionId);
        Assert.Equal(persona, rows[0].Persona);
        Assert.Null(rows[2].Persona);
        Assert.Null(RelayRoster.Project([S("p")])[0].Persona);   // no seam at all
    }

    [Fact]
    public void Roster_json_has_exactly_the_plan_fields()
    {
        var full = Row("s1", "generating", parent: "lead", persona: new RelayPersona("Tamsin", "TO", "pink")) with { Since = T0 };
        var bare = Row("s2", persona: new RelayPersona("Nadia", "NO", null)) with { Since = T0.AddSeconds(5) };
        var json = JsonDocument.Parse(RelayRoster.RosterJson(1, [full, bare, Row("s3") with { Since = T0 }])).RootElement;

        Assert.Equal("roster", json.GetProperty("t").GetString());
        Assert.Equal(1, json.GetProperty("seq").GetInt64());
        var s = json.GetProperty("sessions");
        Assert.Equal("""{"session_id":"s1","cli":"claude","title":"proj","cwd":"/c","state":"generating","since":1794124800,"parent_session_id":"lead","persona":{"name":"Tamsin","initials":"TO","color":"pink"}}""",
            s[0].GetRawText());
        Assert.False(s[1].GetProperty("persona").TryGetProperty("color", out _));
        Assert.False(s[2].TryGetProperty("persona", out _));
        Assert.False(s[2].TryGetProperty("parent_session_id", out _));
    }

    [Fact]
    public void Delta_json_omits_what_did_not_change()
    {
        Assert.Equal("""{"t":"delta","seq":4,"session_id":"s","state":"waiting","title":"T","since":1794124800,"removed":true}""",
            Encoding.UTF8.GetString(RelayRoster.DeltaJson(4, new RelayDelta("s", "waiting", "T", T0, true))));
        Assert.Equal("""{"t":"delta","seq":2,"session_id":"s"}""",
            Encoding.UTF8.GetString(RelayRoster.DeltaJson(2, new RelayDelta("s", null, null, null, false))));
    }

    [Fact]
    public void Since_is_integer_unix_seconds_whatever_the_offset()
    {
        // Addendum 4: the same type and unit as ts and last_seen; milliseconds
        // are truncated, the offset is irrelevant.
        var local = new DateTimeOffset(2026, 11, 8, 0, 0, 0, 999, TimeSpan.FromHours(-8));
        Assert.Equal(1794124800L, RelayRoster.Timestamp(local));
        var json = JsonDocument.Parse(RelayRoster.RosterJson(1, [Row("s") with { Since = local }])).RootElement;
        Assert.Equal(JsonValueKind.Number, json.GetProperty("sessions")[0].GetProperty("since").ValueKind);
    }

    [Fact]
    public void Persona_colour_passes_only_names_in_the_published_set()
    {
        Assert.Equal("purple", RelayRoster.PersonaColor("purple"));
        Assert.Equal("purple", RelayRoster.PersonaColor(" Purple "));        // lowercased, trimmed
        Assert.Null(RelayRoster.PersonaColor("#875FD7"));                    // never hex
        Assert.Null(RelayRoster.PersonaColor("chartreuse"));                 // not a /color name
        Assert.Null(RelayRoster.PersonaColor(null));
        Assert.Null(RelayRoster.PersonaColor(""));
        Assert.Equal(14, RelayRoster.PersonaColors.Count);
        Assert.All(RelayRoster.PersonaColors, c => Assert.Equal(c.ToLowerInvariant(), c));
        // An agent colour outside the set falls through to the session's own.
        Assert.Equal(new RelayPersona("P", "P", "green"),
            RelayRoster.PersonaFrom("P", new SessionStatus { AgentColor = "#123456", Color = "Green" }));
        Assert.Equal(new RelayPersona("P", "P", null),
            RelayRoster.PersonaFrom("P", new SessionStatus { AgentColor = "chartreuse", Color = "#fff" }));
    }

    [Fact]
    public void Persona_takes_the_name_its_glyph_and_the_team_colour_first()
    {
        Assert.Null(RelayRoster.PersonaFrom(null, new SessionStatus()));
        Assert.Null(RelayRoster.PersonaFrom("  ", new SessionStatus()));
        Assert.Equal(new RelayPersona("Jennifer Voss", "JV", "purple"),
            RelayRoster.PersonaFrom("Jennifer Voss", new SessionStatus { AgentColor = "purple", Color = "green" }));
        Assert.Equal(new RelayPersona("Pixel", "PI", "green"),
            RelayRoster.PersonaFrom("Pixel", new SessionStatus { AgentColor = " ", Color = "green" }));
        Assert.Equal(new RelayPersona("Pixel", "PI", null),
            RelayRoster.PersonaFrom("Pixel", new SessionStatus { Color = "" }));
    }

    // ---- the book ---------------------------------------------------------

    [Fact]
    public void A_new_session_needs_a_roster_and_is_stamped_now()
    {
        var book = new RelaySessionBook();
        var u = book.Update([Row("a")], T0);
        Assert.True(u.NeedsRoster);
        Assert.Empty(u.Deltas);
        Assert.False(u.IsEmpty);
        Assert.Equal(T0, book.Snapshot().Single().Since);
    }

    [Fact]
    public void An_unchanged_scan_is_empty()
    {
        var book = new RelaySessionBook();
        book.Update([Row("a")], T0);
        Assert.True(book.Update([Row("a")], T0.AddSeconds(1)).IsEmpty);
        Assert.Equal(T0, book.Snapshot().Single().Since);
    }

    [Fact]
    public void A_state_flip_is_a_delta_with_a_new_since_and_title_alone_keeps_since()
    {
        var book = new RelaySessionBook();
        book.Update([Row("a")], T0);

        var flip = book.Update([Row("a", "generating")], T0.AddSeconds(3));
        Assert.False(flip.NeedsRoster);
        Assert.False(flip.IsEmpty);                              // deltas alone are not empty
        Assert.Equal(new RelayDelta("a", "generating", null, T0.AddSeconds(3), false, From: "idle"), flip.Deltas.Single());
        Assert.False(flip.Deltas.Single().Finished);

        var rename = book.Update([Row("a", "generating", title: "renamed")], T0.AddSeconds(9));
        Assert.Equal(new RelayDelta("a", null, "renamed", null, false), rename.Deltas.Single());
        Assert.Equal(T0.AddSeconds(3), book.Snapshot().Single().Since);
    }

    [Theory]
    [InlineData("cwd")]
    [InlineData("parent")]
    [InlineData("persona")]
    [InlineData("cli")]
    public void A_shape_change_needs_a_roster(string what)
    {
        var book = new RelaySessionBook();
        book.Update([Row("a")], T0);
        var changed = what switch
        {
            "cwd" => Row("a", cwd: "/elsewhere"),
            "parent" => Row("a", parent: "lead"),
            "persona" => Row("a", persona: new RelayPersona("P", "P", null)),
            _ => Row("a") with { Cli = "codex" },
        };
        Assert.True(book.Update([changed], T0.AddSeconds(1)).NeedsRoster);
    }

    [Fact]
    public void A_session_gone_from_the_scan_ends_once_and_leaves_the_book()
    {
        var book = new RelaySessionBook();
        book.Update([Row("a"), Row("b")], T0);
        var u = book.Update([Row("a")], T0.AddSeconds(2));
        Assert.False(u.NeedsRoster);
        Assert.Equal(new RelayDelta("b", "ended", null, T0.AddSeconds(2), true), u.Deltas.Single());
        Assert.Equal(["a"], book.Snapshot().Select(r => r.SessionId));
        Assert.True(book.Update([Row("a")], T0.AddSeconds(3)).IsEmpty);
    }

    // ---- per-phone numbering -------------------------------------------------

    [Fact]
    public void Seq_starts_at_one_on_every_roster_and_counts_through_deltas()
    {
        var stream = new RelayPhoneStream();
        Assert.Null(stream.Delta(new RelayDelta("a", "idle", null, null, false)));   // nothing before a roster
        Assert.Equal(0, stream.Seq);

        stream.Roster([]);
        Assert.Equal(1, stream.Seq);
        var d = JsonDocument.Parse(stream.Delta(new RelayDelta("a", "idle", null, null, false))!).RootElement;
        Assert.Equal(2, d.GetProperty("seq").GetInt64());
        stream.Delta(new RelayDelta("a", "waiting", null, null, false));
        Assert.Equal(3, stream.Seq);

        var again = JsonDocument.Parse(stream.Roster([])).RootElement;    // a resync
        Assert.Equal(1, again.GetProperty("seq").GetInt64());
    }
}

/// <summary>
/// CB-314: roster-fixtures.json in Orbweaver-Mobile contracts/ is what the
/// phone's reducer and screens are built against. Each roster and delta in it
/// is read back into RelaySession/RelayDelta and re-serialised by RelayRoster;
/// the bytes must come out identical, so the fixtures can never describe a
/// field name, an omission or a timestamp format the desktop does not send.
/// </summary>
public class RosterFixtureTests
{
    private static readonly JsonElement F = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "roster-fixtures.json"))).RootElement;

    private static string? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    [Theory]
    [InlineData("macbook_roster")]
    [InlineData("windows_roster")]
    [InlineData("empty_roster")]
    public void Each_roster_is_exactly_what_RelayRoster_writes(string name)
    {
        var r = F.GetProperty(name);
        var sessions = r.GetProperty("sessions").EnumerateArray().Select(s => new RelaySession(
            s.GetProperty("session_id").GetString()!, s.GetProperty("cli").GetString()!, s.GetProperty("title").GetString()!,
            s.GetProperty("cwd").GetString()!, s.GetProperty("state").GetString()!, Opt(s, "parent_session_id"),
            s.TryGetProperty("persona", out var p) ? new RelayPersona(p.GetProperty("name").GetString()!, p.GetProperty("initials").GetString()!, Opt(p, "color")) : null)
        {
            Since = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("since").GetInt64()),
        }).ToList();
        Assert.Equal(Compact(r), Encoding.UTF8.GetString(RelayRoster.RosterJson(r.GetProperty("seq").GetInt64(), sessions)));
    }

    [Fact]
    public void Each_delta_is_exactly_what_RelayRoster_writes()
    {
        var all = F.GetProperty("macbook_deltas_after_roster").EnumerateArray().Append(F.GetProperty("gap_example").GetProperty("delta"));
        foreach (var d in all)
        {
            var delta = new RelayDelta(d.GetProperty("session_id").GetString()!, Opt(d, "state"), Opt(d, "title"),
                d.TryGetProperty("since", out var since) ? DateTimeOffset.FromUnixTimeSeconds(since.GetInt64()) : null,
                d.TryGetProperty("removed", out var r) && r.GetBoolean());
            Assert.Equal(Compact(d), Encoding.UTF8.GetString(RelayRoster.DeltaJson(d.GetProperty("seq").GetInt64(), delta)));
        }
    }

    private static string Compact(JsonElement e) =>
        JsonSerializer.Serialize(e, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default });
}
