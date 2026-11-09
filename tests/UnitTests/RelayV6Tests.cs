using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-321 addendum 2: the persona on push notifications. Every desktop-side
/// rule of frames-v6 — which persona is sent, how its name is cut, where it
/// sits in each plaintext and when it is dropped to keep e — pinned against
/// the committed frames-v6-vectors.json, whose independent check is
/// Orbweaver-Mobile's check_v6_vectors.py.
/// </summary>
public class RelayV6VectorTests
{
    internal static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v6-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly string Sid = Str(In, "session_id");
    private static readonly string Machine = Str(In, "machine");
    private static readonly KeyPair PhoneKa = new(
        Convert.FromHexString(Str(In.GetProperty("phone_agreement"), "private")),
        Convert.FromHexString(Str(In.GetProperty("phone_agreement"), "public")));

    private static PushPersona? Persona(JsonElement p) => p.ValueKind == JsonValueKind.Null ? null
        : PushPersona.From(Str(p, "id"), Str(p, "name"), p.TryGetProperty("image_hash", out var h) ? h.GetString() : null);

    private static PushPersona Aurora => Persona(In.GetProperty("personas").GetProperty("aurora"))!;

    // The vector's own e opens under the phone's key to its plaintext, and the
    // desktop's sealer with the vector's ephemeral key and nonce writes it again.
    private static void AssertBox(JsonElement box, string plaintext)
    {
        Assert.Equal(Str(box, "plaintext"), plaintext);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(PushSeal.Open(PhoneKa, Str(box, "e"))!));
        var eph = new KeyPair(Convert.FromHexString(Str(box.GetProperty("eph"), "private")), Convert.FromHexString(Str(box.GetProperty("eph"), "public")));
        Assert.Equal(Str(box, "e"), PushSeal.SealWith(eph, Convert.FromHexString(Str(box, "nonce")), PhoneKa.Public, Encoding.UTF8.GetBytes(plaintext)));
    }

    public static TheoryData<string> AmberCases => new() { "with_picture", "without_picture", "name_cut", "persona_dropped", "no_persona" };

    [Theory]
    [MemberData(nameof(AmberCases))]
    public void FitPush_writes_each_amber_vector_with_its_cuts(string name)
    {
        var c = V.GetProperty("amber").GetProperty(name);
        var i = c.GetProperty("input");
        var options = i.GetProperty("options").EnumerateArray().Select(o => new PromptOption(
            Str(o, "key"), Str(o, "label"), Str(o, "kind"), o.TryGetProperty("scope", out var s) ? s.GetString() : null)).ToList();
        var fit = PromptFrames.FitPush(Str(i, "request_id"), Str(i, "session_id"), Str(i, "label"), Str(i, "tool"), Str(i, "summary"),
            Str(i, "preview"), i.GetProperty("expires_at").GetInt64(), options, Str(i, "screen_hash"), Persona(i.GetProperty("persona")))!;

        AssertBox(c, fit.Plaintext);
        Assert.Equal((c.GetProperty("detail_cut").GetBoolean(), c.GetProperty("labels_cut").GetBoolean(), c.GetProperty("persona_dropped").GetBoolean()),
            (fit.DetailCut, fit.LabelsCut, fit.PersonaDropped));
        var plain = JsonNode.Parse(fit.Plaintext)!.AsObject();
        if (plain.ContainsKey("persona"))
        {
            Assert.Equal("persona", plain.Last().Key);                      // the last key, after screen_hash
            string[] order = Persona(i.GetProperty("persona"))!.ImageHash is null ? ["id", "name"] : ["id", "name", "image_hash"];
            Assert.Equal(order, plain["persona"]!.AsObject().Select(kv => kv.Key).ToArray());
        }
    }

    [Fact]
    public void The_no_persona_amber_is_v3_s_plaintext_byte_for_byte()
    {
        var push = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "push-vectors.json"))).RootElement;
        Assert.Equal(Str(push.GetProperty("positives")[0], "plaintext"), Str(V.GetProperty("amber").GetProperty("no_persona"), "plaintext"));
    }

    [Fact]
    public void An_amber_that_fits_nowhere_is_still_null_with_or_without_a_persona()
    {
        var options = Enumerable.Range(1, 40).Select(n => new PromptOption(n.ToString(), "Option " + new string('x', 200), "option", null)).ToList();
        Assert.Null(PromptFrames.FitPush("r", "s", "label", "Bash", "Needs an answer", "d", 1, options, "h", Aurora));
        Assert.Null(PromptFrames.FitPush("r", "s", "label", "Bash", "Needs an answer", "d", 1, options, "h"));
    }

    [Fact]
    public void DoneE_writes_the_done_vector_and_its_two_back_compat_plaintexts()
    {
        var done = V.GetProperty("done");
        AssertBox(done.GetProperty("e"), Encoding.UTF8.GetString(RelayLink.PushPlaintext(Aurora, p => RelayLink.DoneE(Sid, "orbweaver", Machine, p)).Bytes!));
        Assert.Equal(Str(done.GetProperty("no_persona"), "plaintext"), RelayLink.DoneE(Sid, "orbweaver", Machine));
        var v4 = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v4-vectors.json"))).RootElement;
        Assert.Equal(Str(v4.GetProperty("done").GetProperty("e"), "plaintext"), RelayLink.DoneE(Sid, "orbweaver", Machine));

        var title = Str(done.GetProperty("persona_dropped"), "title");
        var (bytes, withPersona) = RelayLink.PushPlaintext(Aurora, p => RelayLink.DoneE(Sid, title, Machine, p));
        Assert.False(withPersona);
        Assert.Equal(Str(done.GetProperty("persona_dropped"), "plaintext"), Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void LimitE_writes_the_limit_vector_with_the_default_persona()
    {
        var limit = V.GetProperty("limit");
        var np = JsonDocument.Parse(Str(limit.GetProperty("no_persona"), "plaintext")).RootElement;
        var a = new RelayUsage.Account(Str(np, "name"), Str(np, "cli"), "", np.GetProperty("window_5h_pct").GetInt32(), np.GetProperty("resets_at").GetInt64(), null, null, 0, true);
        AssertBox(limit.GetProperty("e"), RelayUsage.LimitE(a, Aurora));
        Assert.Equal(Str(limit.GetProperty("no_persona"), "plaintext"), RelayUsage.LimitE(a));
        Assert.Equal(Str(limit.GetProperty("no_persona"), "plaintext"), RelayUsage.LimitE(a, null));
    }

    [Fact]
    public void PushPlaintext_takes_the_persona_then_none_then_gives_up()
    {
        string Small(PushPersona? p) => RelayLink.DoneE("s", "t", "m", p);
        string Huge(PushPersona? p) => RelayLink.DoneE("s", new string('t', 3000), "m", p);

        var (with, used) = RelayLink.PushPlaintext(Aurora, Small);
        Assert.Equal((Small(Aurora), true), (Encoding.UTF8.GetString(with!), used));
        var (plain, none) = RelayLink.PushPlaintext(null, Small);
        Assert.Equal((Small(null), false), (Encoding.UTF8.GetString(plain!), none));
        Assert.Equal((null, false), RelayLink.PushPlaintext(Aurora, Huge));
        Assert.Equal((null, false), RelayLink.PushPlaintext(null, Huge));
    }

    [Fact]
    public void The_name_cut_table_is_the_desktop_s_cut()
    {
        foreach (var c in V.GetProperty("rules").GetProperty("name_cut").EnumerateArray())
        {
            Assert.Equal(Str(c, "sent"), PromptFrames.Cut(Str(c, "name"), PushPersona.MaxName));
            Assert.Equal(Str(c, "sent"), PushPersona.From("3f7a9c1e5b2d8046", Str(c, "name"), null)!.Name);
        }
    }

    // The phone must ignore these; the desktop must never write one. Each bad
    // persona either is refused outright or comes out as something else.
    [Fact]
    public void The_desktop_never_builds_a_negative_s_persona()
    {
        var negatives = V.GetProperty("negatives").EnumerateArray().Where(n => Str(n, "expect") == "persona_ignored").ToList();
        Assert.Equal(9, negatives.Count);
        foreach (var n in negatives)
        {
            var bad = JsonDocument.Parse(Str(n, "plaintext")).RootElement.GetProperty("persona");
            var built = bad.ValueKind != JsonValueKind.Object ? null : PushPersona.From(
                bad.TryGetProperty("id", out var id) ? id.GetString() : null,
                bad.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                bad.TryGetProperty("image_hash", out var h) ? h.GetString() : null);
            Assert.True(built is null || built.Canonical() != bad.GetRawText(), Str(n, "name"));
        }
    }
}

/// <summary>CB-321 addendum 2: the persona record itself — the sending rules, both forms, the log tag.</summary>
public class PushPersonaTests
{
    private const string Id = "3f7a9c1e5b2d8046";
    private const string Hash = "6a3f8c2d1e0b9a7f4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3";

    [Theory]
    [InlineData(null, "Aurora")]
    [InlineData("3F7A9C1E5B2D8046", "Aurora")]       // uppercase
    [InlineData("3f7a9c1e5b2d804", "Aurora")]        // 15 hex
    [InlineData(Id, null)]
    [InlineData(Id, "")]
    [InlineData(Id, "   ")]
    public void A_persona_with_a_bad_id_or_no_name_is_not_sent(string? id, string? name) =>
        Assert.Null(PushPersona.From(id, name, Hash));

    [Fact]
    public void The_name_is_cut_and_a_bad_hash_is_left_off_rather_than_costing_the_name()
    {
        Assert.Equal(new PushPersona(Id, new string('N', 63) + "…", Hash), PushPersona.From(Id, new string('N', 65), Hash));
        Assert.Equal(new PushPersona(Id, "Aurora", null), PushPersona.From(Id, "Aurora", Hash[..63]));
        Assert.Equal(new PushPersona(Id, "Aurora", null), PushPersona.From(Id, "Aurora", Hash.ToUpperInvariant()));
        Assert.Equal(new PushPersona(Id, "Aurora", null), PushPersona.From(Id, "Aurora", null));
    }

    [Fact]
    public void Of_reads_a_roster_persona_and_a_personas_row()
    {
        Assert.Null(PushPersona.Of((RelayPersona?)null));
        Assert.Null(PushPersona.Of((PersonaFrames.Row?)null));
        Assert.Null(PushPersona.Of(new RelayPersona("Old", "OL", "pink")));    // before v5 ids: not nameable
        Assert.Equal(new PushPersona(Id, "Aurora", Hash), PushPersona.Of(new RelayPersona("Aurora", "AU", "pink", Id, Hash)));
        Assert.Equal(new PushPersona(Id, "Aurora", null), PushPersona.Of(new PersonaFrames.Row(Id, "Aurora", "AU", true, null)));
    }

    [Fact]
    public void Both_forms_write_id_name_image_hash_in_that_order_and_escape_their_own_way()
    {
        var p = new PushPersona(Id, "Zoë \"Z\"\n😀", Hash);
        Assert.Equal($$"""{"id":"{{Id}}","name":"Zoë \"Z\"\u000a😀","image_hash":"{{Hash}}"}""", p.Canonical());    // the shared escaper
        Assert.Contains("\"name\":\"Zoë \\\"Z\\\"\\n\\uD83D\\uDE00\"", PromptFrames.PushPlaintext("r", "s", "l", "t", "u", "d", 1, [], "h", p));   // the amber's library form
        Assert.Equal(PushPersona.From(Id, "Zoë \"Z\"\n😀", Hash)!.Canonical(), p.Canonical());
        Assert.Equal(["id", "name", "image_hash"], p.Json().Select(kv => kv.Key).ToArray());
        Assert.Equal($$"""{"id":"{{Id}}","name":"Aurora"}""", new PushPersona(Id, "Aurora", null).Canonical());
        Assert.Equal(["id", "name"], new PushPersona(Id, "Aurora", null).Json().Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void The_log_tag_says_the_id_prefix_and_whether_a_picture_rode_along_never_the_name()
    {
        var with = new PushPersona(Id, "Aurora", Hash);
        Assert.Equal("persona=3f7a9c avatar=yes", with.LogTag);
        Assert.Equal("persona=3f7a9c avatar=no", (with with { ImageHash = null }).LogTag);
        Assert.Equal("persona=none", PushPersona.LogTagFor(null, false));
        Assert.Equal("persona=dropped", PushPersona.LogTagFor(with, false));
        Assert.Equal(with.LogTag, PushPersona.LogTagFor(with, true));
        Assert.DoesNotContain("Aurora", with.LogTag);
        Assert.DoesNotContain(Hash[..8], with.LogTag);
    }
}

/// <summary>
/// CB-321 addendum 2: tools/relay-vectors' V6Vectors regenerates
/// frames-v6-vectors.json from the shipped code, byte for byte below
/// `_source`, and recomputes rather than copies what the desktop sends.
/// </summary>
public class V6VectorsReproduceTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay");

    [Fact]
    public void The_desktop_regenerates_the_committed_vectors_byte_for_byte()
    {
        var committedPath = Path.Combine(Dir, "frames-v6-vectors.json");
        var committed = File.ReadAllText(committedPath);
        var source = RelayV6VectorTests.V.GetProperty("_source");
        var ours = V6Vectors.Build(Dir, committedPath, source.GetProperty("orbweaver_sha").GetString()!, source.GetProperty("generated_by").GetString());
        if (ours != committed)
        {
            var at = 0;
            while (at < Math.Min(ours.Length, committed.Length) && ours[at] == committed[at]) at++;
            Assert.Fail($"first difference at char {at}: ours «{ours[Math.Max(0, at - 80)..Math.Min(ours.Length, at + 80)]}» committed «{committed[Math.Max(0, at - 80)..Math.Min(committed.Length, at + 80)]}»");
        }
    }

    [Theory]
    [InlineData("amber", "persona_dropped", "plaintext")]
    [InlineData("amber", "with_picture", "e")]
    [InlineData("done", "e", "e")]
    [InlineData("limit", "e", "e")]
    [InlineData("done", "persona_dropped", "plaintext")]
    public void A_tampered_computed_value_is_recomputed_not_copied(string a, string b, string c)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "frames-v6-vectors.json")))!;
        node[a]![b]![c] = c == "plaintext" && a == "limit" ? """{"v":1,"t":"limit","name":"x","cli":"claude","window_5h_pct":81,"resets_at":1}""" : "tampered";
        var tampered = Path.Combine(Path.GetTempPath(), "v6-tampered-" + Guid.NewGuid() + ".json");
        File.WriteAllText(tampered, PyJson.Pretty(node, indent: 1) + "\n");
        try
        {
            var ours = JsonNode.Parse(V6Vectors.Build(Dir, tampered, "x"))!;
            var good = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "frames-v6-vectors.json")))!;
            Assert.Equal(good[a]![b]![c]!.ToJsonString(), ours[a]![b]![c]!.ToJsonString());
        }
        finally { File.Delete(tampered); }
    }

    [Fact]
    public void An_amber_input_that_fits_nowhere_is_refused_rather_than_written()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "frames-v6-vectors.json")))!;
        node["amber"]!["no_persona"]!["input"]!["label"] = new string('l', 4000);
        var tampered = Path.Combine(Path.GetTempPath(), "v6-unfit-" + Guid.NewGuid() + ".json");
        File.WriteAllText(tampered, PyJson.Pretty(node, indent: 1) + "\n");
        try { Assert.Contains("amber no_persona: no e", Assert.Throws<InvalidOperationException>(() => V6Vectors.Build(Dir, tampered, "x")).Message); }
        finally { File.Delete(tampered); }
    }

    [Fact]
    public void A_done_input_that_fits_nowhere_is_refused_rather_than_written()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "frames-v6-vectors.json")))!;
        node["done"]!["persona_dropped"]!["title"] = new string('t', 4000);
        var tampered = Path.Combine(Path.GetTempPath(), "v6-unfit-" + Guid.NewGuid() + ".json");
        File.WriteAllText(tampered, PyJson.Pretty(node, indent: 1) + "\n");
        try { Assert.Equal("no e", Assert.Throws<InvalidOperationException>(() => V6Vectors.Build(Dir, tampered, "x")).Message); }
        finally { File.Delete(tampered); }
    }
}
