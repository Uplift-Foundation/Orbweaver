using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-321: the desktop's v5 frames against Orbweaver-Mobile
/// contracts/frames-v5-vectors.json (checked there by check_v5_vectors.py, an
/// independent implementation). Every plaintext is rebuilt by the shipped
/// code and compared byte for byte, every box is opened, and the rule tables
/// are the desktop's own verdicts.
/// </summary>
public class RelayV5VectorTests
{
    internal static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v5-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static readonly byte[] CkKey = Convert.FromHexString(Str(V.GetProperty("inputs"), "ck_key"));

    private static (string Hash, int W, int H, List<PersonaFrames.Frame> Frames) Picture(string name)
    {
        var p = V.GetProperty("pictures").EnumerateArray().First(x => Str(x, "name") == name);
        return (Str(p, "image_hash"), p.GetProperty("w").GetInt32(), p.GetProperty("h").GetInt32(),
            p.GetProperty("frames").EnumerateArray()
                .Select(f => new PersonaFrames.Frame(f.GetProperty("ms").GetInt32(), Convert.FromHexString(Str(f, "png_hex")))).ToList());
    }

    [Fact]
    public void The_limits_are_the_desktop_s_constants()
    {
        var l = V.GetProperty("limits");
        Assert.Equal(
            new long[] { 48000, 32768, 120, 16, 256, 20, 10000, 16777216, 2097152, 10, 30 },
            new long[]
            {
                PersonaFrames.MaxWindowBytes, PersonaFrames.MaxFrameBytes, PersonaFrames.MaxFrames, PersonaFrames.MinSide,
                PersonaFrames.MaxSide, PersonaFrames.MinFrameMs, PersonaFrames.MaxFrameMs, PersonaFrames.MaxSourceBytes,
                PersonaFrames.MaxImageBytes, PersonaFrames.ImageWindowsPerSecond, PersonaFrames.FetchTimeoutSeconds,
            });
        Assert.Equal(new long[] { 48000, 32768, 120, 16, 256, 20, 10000, 16777216, 2097152, 10, 30 },
            l.EnumerateObject().Select(p => p.Value.GetInt64()).ToArray());
    }

    [Fact]
    public void Each_picture_s_hash_is_the_sha256_of_its_source()
    {
        foreach (var p in V.GetProperty("pictures").EnumerateArray())
            Assert.Equal(Str(p, "image_hash"), PersonaImageBundle.HashOf(Convert.FromHexString(Str(p, "source_hex"))));
    }

    [Theory]
    [InlineData("still")]
    [InlineData("animated_small")]
    [InlineData("animated_wide")]
    public void Each_picture_s_request_and_windows_are_what_the_desktop_writes(string name)
    {
        var image = V.GetProperty("persona_image").GetProperty(name);
        var (hash, w, h, frames) = Picture(name);
        Assert.Equal(Str(image, "ck_image"), Ck(CkKey, PersonaFrames.ImageCk(hash)));
        RelayV4VectorTests.AssertBox(image.GetProperty("request"), PersonaFrames.Request(hash));

        var ours = PersonaFrames.Chunk(hash, w, h, frames);
        var theirs = image.GetProperty("windows").EnumerateArray().ToList();
        Assert.Equal(theirs.Count, ours.Count);
        for (var i = 0; i < ours.Count; i++)
        {
            RelayV4VectorTests.AssertBox(theirs[i], ours[i].Json);
            Assert.Equal(Str(theirs[i], "ck"), Str(image, "ck_image"));
            Assert.Equal((theirs[i].GetProperty("from_frame").GetInt32(), theirs[i].GetProperty("frame_count").GetInt32()), (ours[i].From, ours[i].Count));
        }
    }

    [Fact]
    public void Every_window_but_the_last_is_maximal_and_carries_the_full_header()
    {
        var (hash, w, h, frames) = Picture("animated_wide");
        var windows = PersonaFrames.Chunk(hash, w, h, frames);
        Assert.Equal(3, windows.Count);
        foreach (var window in windows)
        {
            Assert.True(Encoding.UTF8.GetByteCount(window.Json) <= PersonaFrames.MaxWindowBytes);
            var j = JsonDocument.Parse(window.Json).RootElement;
            Assert.Equal((hash, w, h, 6, window.From), (Str(j, "hash"), j.GetProperty("w").GetInt32(), j.GetProperty("h").GetInt32(),
                j.GetProperty("frames_total").GetInt32(), j.GetProperty("from_frame").GetInt32()));
        }
        foreach (var window in windows.SkipLast(1))
        {
            // One more frame would not fit.
            var more = PersonaFrames.Chunk(hash, w, h, frames.Skip(window.From).Take(window.Count + 1).ToList());
            var bigger = PersonaFrames.Window(hash, w, h, 6, window.From,
                JsonDocument.Parse(more[0].Json).RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetRawText())
                    .Concat(more.Count > 1 ? JsonDocument.Parse(more[1].Json).RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetRawText()) : []));
            Assert.True(Encoding.UTF8.GetByteCount(bigger) > PersonaFrames.MaxWindowBytes);
        }
    }

    [Fact]
    public void A_single_largest_frame_fits_an_empty_window()
    {
        var frame = new PersonaFrames.Frame(10000, new byte[PersonaFrames.MaxFrameBytes]);
        var windows = PersonaFrames.Chunk(new string('f', 64), 256, 256, [frame, frame]);
        Assert.Equal(2, windows.Count);
        Assert.All(windows, w => Assert.True(Encoding.UTF8.GetByteCount(w.Json) <= PersonaFrames.MaxWindowBytes));
    }

    [Fact]
    public void The_personas_frames_are_what_the_desktop_writes()
    {
        var p = V.GetProperty("personas");
        var rows = p.GetProperty("rows_input").EnumerateArray().Select(r => new PersonaFrames.Row(
            Str(r, "id"), Str(r, "name"), OrbGlyph.Initials(Str(r, "name")),
            r.TryGetProperty("default", out var d) && d.GetBoolean(),
            r.TryGetProperty("image_hash", out var h) ? h.GetString() : null)).ToList();
        RelayV4VectorTests.AssertBox(p.GetProperty("frame"), PersonaFrames.Personas(rows));
        RelayV4VectorTests.AssertBox(p.GetProperty("empty"), PersonaFrames.Personas([]));
        Assert.Equal(Str(V.GetProperty("inputs"), "ck_personas"), Ck(CkKey, PersonaFrames.PersonasCk));
        Assert.Equal(Str(p.GetProperty("frame"), "ck"), Ck(CkKey, "personas"));
    }

    [Fact]
    public void The_roster_with_v5_keys_is_what_RelayRoster_writes()
    {
        var r = V.GetProperty("roster");
        var sessions = r.GetProperty("sessions_input").EnumerateArray().Select(s => new RelaySession(
            Str(s, "session_id"), Str(s, "cli"), Str(s, "title"), Str(s, "cwd"), Str(s, "state"),
            s.TryGetProperty("parent_session_id", out var parent) ? parent.GetString() : null,
            s.TryGetProperty("persona", out var p)
                ? new RelayPersona(Str(p, "name"), OrbGlyph.Initials(Str(p, "name")),
                    p.TryGetProperty("color", out var c) ? c.GetString() : null, Str(p, "id"),
                    p.TryGetProperty("image_hash", out var h) ? h.GetString() : null)
                : null)
        { Since = DateTimeOffset.FromUnixTimeSeconds(s.GetProperty("since").GetInt64()) }).ToList();
        RelayV4VectorTests.AssertBox(r.GetProperty("frame"), Encoding.UTF8.GetString(RelayRoster.RosterJson(1, sessions)));
    }

    [Fact]
    public void The_refusals_are_what_RelayReplies_writes()
    {
        var r = V.GetProperty("persona_image").GetProperty("refusals");
        var unknown = JsonDocument.Parse(Str(r.GetProperty("unknown_request"), "plaintext")).RootElement;
        RelayV4VectorTests.AssertBox(r.GetProperty("unknown_request"), PersonaFrames.Request(Str(unknown, "hash")));
        RelayV4VectorTests.AssertBox(r.GetProperty("err_unknown_image"),
            RelayReplies.Err(Str(r.GetProperty("unknown_request"), "envelope_id"), RelayReplies.UnknownImage));
        RelayV4VectorTests.AssertBox(r.GetProperty("err_bad_frame"), RelayReplies.Err("01JCA0IMAGE000000000000AC1", RelayReplies.BadFrame));
        Assert.IsType<BadFrameRequest>(RelayRequests.Parse(r.GetProperty("_bad_frame_input"), "01JCA0IMAGE000000000000AC1"));
        Assert.Equal(new PersonaImageRequest("ref", Str(unknown, "hash")), RelayRequests.Parse(unknown, "ref"));
    }

    // ---- the rule tables ---------------------------------------------------------

    [Fact]
    public void Hash_and_persona_id_grammar()
    {
        foreach (var c in V.GetProperty("rules").GetProperty("hash_grammar").EnumerateArray())
            Assert.Equal(c.GetProperty("valid").GetBoolean(), PersonaFrames.IsHash(Str(c, "value")));
        foreach (var c in V.GetProperty("rules").GetProperty("persona_id_grammar").EnumerateArray())
            Assert.Equal(c.GetProperty("valid").GetBoolean(), PersonaFrames.IsPersonaId(Str(c, "value")));
        Assert.False(PersonaFrames.IsHash(null));
        Assert.False(PersonaFrames.IsPersonaId(null));
    }

    [Fact]
    public void Frame_cap_side_and_ms_rules()
    {
        var rules = V.GetProperty("rules");
        foreach (var c in rules.GetProperty("frame_cap").EnumerateArray())
            Assert.Equal(Str(c, "expect") == "accepted", PersonaFrames.FrameAllowed(c.GetProperty("frame_bytes").GetInt32()));
        foreach (var c in rules.GetProperty("side").EnumerateArray())
            Assert.Equal(Str(c, "expect") == "accepted", PersonaFrames.SideAllowed(c.GetProperty("w").GetInt32(), c.GetProperty("h").GetInt32()));
        Assert.False(PersonaFrames.SideAllowed(16, 257));
        Assert.False(PersonaFrames.SideAllowed(16, 15));
        foreach (var c in rules.GetProperty("ms_rule").GetProperty("cases").EnumerateArray())
            Assert.Equal(c.GetProperty("sent_ms").GetInt32(), PersonaFrames.SentMs(c.GetProperty("gif_ms").GetInt32(), c.GetProperty("frames").GetInt32()));
    }

    // ---- persona ids and avatar ids ------------------------------------------------

    [Fact]
    public void A_persona_id_is_16_hex_of_its_origin_file_and_stable()
    {
        var a = PersonaFrames.PersonaId("/Users/dev/.claude/CLAUDE.md", "Aurora");
        Assert.True(PersonaFrames.IsPersonaId(a));
        Assert.Equal(a, PersonaFrames.PersonaId("/Users/dev/.claude/CLAUDE.md", "Another name"));   // the file, not the name
        Assert.NotEqual(a, PersonaFrames.PersonaId("/Users/dev/Source/demo-app/CLAUDE.md", "Aurora")); // a nearer file shadows
        var byName = PersonaFrames.PersonaId(null, "Aurora");
        Assert.True(PersonaFrames.IsPersonaId(byName));
        Assert.Equal(byName, PersonaFrames.PersonaId("", "Aurora"));
        Assert.NotEqual(byName, PersonaFrames.PersonaId(null, "Nova"));
        Assert.DoesNotContain("Users", a);
    }

    [Fact]
    public void An_avatar_id_s_hash_half_is_the_image_hash()
    {
        var bytes = "a picture"u8.ToArray();
        Assert.Equal(PersonaImageBundle.HashOf(bytes), PersonaFrames.HashOf(MirrorProtocol.AvatarIdOf(bytes)));
        Assert.Equal(PersonaImageBundle.HashOf(bytes), PersonaFrames.HashOf(PersonaImageBundle.HashOf(bytes)));   // no colon
        Assert.Null(PersonaFrames.HashOf(null));
        Assert.Null(PersonaFrames.HashOf("XYZ:12"));
        Assert.Equal("0123456789ab", PersonaFrames.Short("0123456789abcdef"));
        Assert.Equal("abc", PersonaFrames.Short("abc"));
    }

    [Fact]
    public void A_persona_image_request_needs_a_well_formed_hash()
    {
        static RelayRequest? P(string json) => RelayRequests.Parse(JsonDocument.Parse(json).RootElement, "r");
        var hash = new string('0', 64);
        Assert.Equal(new PersonaImageRequest("r", hash), P(PersonaFrames.Request(hash)));
        Assert.Equal(new BadFrameRequest("r", null), P("""{"t":"persona_image"}"""));
        Assert.Equal(new BadFrameRequest("r", null), P("""{"t":"persona_image","hash":7}"""));
        Assert.Equal(new BadFrameRequest("r", null), P($$"""{"t":"persona_image","hash":"{{hash.ToUpperInvariant()}}A"}"""));
        Assert.Contains("persona_image", RelayRequests.Types);
        Assert.Contains(RelayReplies.UnknownImage, RelayReplies.ErrCodes);
        Assert.Equal("""{"t":"err","ref":"r","code":"unknown_image"}""", RelayReplies.Err("r", RelayReplies.UnknownImage, null, "no why"));
    }
}

/// <summary>
/// CB-321: tools/relay-vectors' V5Vectors regenerates frames-v5-vectors.json
/// from the shipped code; with the committed _source stamped back it is the
/// committed file byte for byte. The control: a computed value tampered with
/// in a copy of the committed file is not carried over — the regeneration
/// differs from the tampered copy — so the test is not passing by copying.
/// </summary>
public class V5VectorsReproduceTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay");

    [Fact]
    public void The_desktop_regenerates_the_committed_vectors_byte_for_byte()
    {
        var committedPath = Path.Combine(Dir, "frames-v5-vectors.json");
        var committed = File.ReadAllText(committedPath);
        var source = RelayV5VectorTests.V.GetProperty("_source");
        var ours = V5Vectors.Build(Dir, committedPath, source.GetProperty("orbweaver_sha").GetString()!, source.GetProperty("generated_by").GetString());
        if (ours != committed)
        {
            var at = 0;
            while (at < Math.Min(ours.Length, committed.Length) && ours[at] == committed[at]) at++;
            Assert.Fail($"first difference at char {at}: ours «{ours[Math.Max(0, at - 80)..Math.Min(ours.Length, at + 80)]}» committed «{committed[Math.Max(0, at - 80)..Math.Min(committed.Length, at + 80)]}»");
        }
    }

    [Theory]
    [InlineData("personas", "frame", "ct")]
    [InlineData("roster", "frame", "plaintext")]
    [InlineData("persona_image", "refusals", "err_unknown_image")]
    public void A_tampered_computed_value_is_recomputed_not_copied(string a, string b, string c)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "frames-v5-vectors.json")))!;
        if (node[a]![b]![c] is JsonObject box) box["ct"] = "AAAA";
        else node[a]![b]![c] = "tampered";
        var tampered = Path.Combine(Path.GetTempPath(), "v5-tampered-" + Guid.NewGuid() + ".json");
        File.WriteAllText(tampered, PyJson.Pretty(node) + "\n");
        try
        {
            var ours = JsonNode.Parse(V5Vectors.Build(Dir, tampered, "x"))!;
            var good = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "frames-v5-vectors.json")))!;
            Assert.Equal(good[a]![b]!.ToJsonString(), ours[a]![b]!.ToJsonString());
        }
        finally { File.Delete(tampered); }
    }
}

/// <summary>CB-321: which persona record a roster row and the personas frame carry.</summary>
public class RelayPersonaRowsTests
{
    private static LocalPersona.Persona P(string? name, string? source = null, string? avatar = null, params string[] files) =>
        new(name, null, null, source, avatar, files);

    private static readonly Dictionary<string, string> Offered = new() { ["/p/aurora.gif"] = new string('a', 64) };
    private static string? Offer(string path) => Offered.GetValueOrDefault(path);

    [Fact]
    public void A_matching_registry_persona_lends_its_id_and_offered_picture()
    {
        var status = new SessionStatus { AgentColor = "purple" };
        var r = RelayPersonaRows.For("Aurora", status, P("Aurora", "/p/CLAUDE.md", "/p/aurora.gif", "/p/CLAUDE.md"), Offer)!;
        Assert.Equal(new RelayPersona("Aurora", "AU", "purple", PersonaFrames.PersonaId("/p/CLAUDE.md", "Aurora"), new string('a', 64)), r);
    }

    [Fact]
    public void Without_a_picture_source_the_first_file_names_the_persona()
    {
        var r = RelayPersonaRows.For("Vector", new SessionStatus(), P("Vector", null, null, "/v/AGENTS.md", "/v/CLAUDE.md"), Offer)!;
        Assert.Equal((PersonaFrames.PersonaId("/v/AGENTS.md", "Vector"), (string?)null), (r.Id, r.ImageHash));
    }

    [Fact]
    public void A_picture_not_yet_offered_has_no_hash()
    {
        var r = RelayPersonaRows.For("Nova", new SessionStatus(), P("Nova", "/n/CLAUDE.md", "/n/building.gif", "/n/CLAUDE.md"), Offer)!;
        Assert.Null(r.ImageHash);
        Assert.Equal(PersonaFrames.PersonaId("/n/CLAUDE.md", "Nova"), r.Id);
    }

    [Fact]
    public void A_registry_persona_naming_someone_else_lends_nothing()
    {
        var r = RelayPersonaRows.For("Aurora", new SessionStatus(), P("Nova", "/p/CLAUDE.md", "/p/aurora.gif"), Offer)!;
        Assert.Equal((PersonaFrames.PersonaId(null, "Aurora"), (string?)null), (r.Id, r.ImageHash));
        Assert.Equal(PersonaFrames.PersonaId(null, "Aurora"), RelayPersonaRows.For("Aurora", new SessionStatus(), null, Offer)!.Id);
        Assert.Null(RelayPersonaRows.For(null, new SessionStatus(), P("Nova"), Offer));
        Assert.Null(RelayPersonaRows.Origin(null));
        Assert.Null(RelayPersonaRows.Origin(P("x")));
    }

    private static RelaySession Row(string id, RelayPersona? p) => new(id, "claude", "t", "/c", "idle", null, p);

    [Fact]
    public void Rows_list_each_worn_persona_once_and_mark_the_default()
    {
        var aurora = new RelayPersona("Aurora", "AU", "purple", PersonaFrames.PersonaId("/p/CLAUDE.md", "Aurora"), new string('a', 64));
        var vector = new RelayPersona("Vector", "VE", null, PersonaFrames.PersonaId(null, "Vector"));
        var rows = RelayPersonaRows.Rows(
            [Row("s1", aurora), Row("s2", aurora), Row("s3", vector), Row("s4", null), Row("s5", new RelayPersona("Old", "OL", null))],
            P("Aurora", "/p/CLAUDE.md", "/p/other.gif", "/p/CLAUDE.md"), Offer);
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows.OrderBy(r => r.Id, StringComparer.Ordinal), rows);
        var def = Assert.Single(rows, r => r.Default);
        Assert.Equal(("Aurora", new string('a', 64)), (def.Name, def.ImageHash));   // the worn persona's offered hash
        Assert.False(rows.Single(r => r.Name == "Vector").Default);
    }

    [Fact]
    public void A_default_nobody_wears_is_added_with_its_own_picture_and_a_nameless_one_adds_nothing()
    {
        var rows = RelayPersonaRows.Rows([], P("Aurora", "/p/CLAUDE.md", "/p/aurora.gif", "/p/CLAUDE.md"), Offer);
        Assert.Equal([new PersonaFrames.Row(PersonaFrames.PersonaId("/p/CLAUDE.md", "Aurora"), "Aurora", "AU", true, new string('a', 64))], rows);
        Assert.Null(RelayPersonaRows.Rows([], P("Nova", null, null, "/n/CLAUDE.md"), Offer).Single().ImageHash);
        Assert.Empty(RelayPersonaRows.Rows([], P(null, "/p/CLAUDE.md"), Offer));
        Assert.Empty(RelayPersonaRows.Rows([], P("  "), Offer));
        Assert.Empty(RelayPersonaRows.Rows([], null, Offer));
        Assert.Equal("""{"t":"personas","personas":[]}""", PersonaFrames.Personas(RelayPersonaRows.Rows([], null, Offer)));
    }

    [Fact]
    public void The_roster_writes_id_and_image_hash_after_color_and_omits_them_when_absent()
    {
        var with = new RelaySession("s", "claude", "t", "/c", "idle", null, new RelayPersona("A", "A", "red", "0123456789abcdef", new string('e', 64)));
        Assert.Contains("\"persona\":{\"name\":\"A\",\"initials\":\"A\",\"color\":\"red\",\"id\":\"0123456789abcdef\",\"image_hash\":\"" + new string('e', 64) + "\"}",
            Encoding.UTF8.GetString(RelayRoster.RosterJson(1, [with])));
        var without = with with { Persona = new RelayPersona("A", "A", null) };
        Assert.Contains("\"persona\":{\"name\":\"A\",\"initials\":\"A\"}", Encoding.UTF8.GetString(RelayRoster.RosterJson(1, [without])));
        // A changed hash is a changed shape: the whole roster goes again.
        Assert.False(with.SameShape(with with { Persona = with.Persona! with { ImageHash = new string('f', 64) } }));
        Assert.False(with.SameShape(with with { Persona = with.Persona! with { Id = "fedcba9876543210" } }));
    }

    [Fact]
    public void Project_hands_the_persona_seam_each_session_s_id()
    {
        var seen = new List<string>();
        var rows = RelayRoster.Project(
            [("b", new SessionStatus { Source = SessionSource.ClaudeCode }), ("a", new SessionStatus { Source = SessionSource.ClaudeCode })],
            (id, _) => { seen.Add(id); return null; });
        Assert.Equal(["a", "b"], seen);
        Assert.Equal(2, rows.Count);
    }
}

/// <summary>CB-321: a persona picture as bounded frames, through real Skia.</summary>
public class PersonaImageBundleTests
{
    private static byte[] Png(int w, int h, SKColor colour)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(colour);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void An_animated_gif_becomes_its_frames_at_their_delays_and_size()
    {
        var gif = TinyGif.Write(40, 30, [TinyGif.Solid(40, 30, 0), TinyGif.Solid(40, 30, 1), TinyGif.Solid(40, 30, 2)], [8, 0, 1200]);
        var b = PersonaImageBundle.Build(gif)!;
        Assert.Equal((PersonaImageBundle.HashOf(gif), 40, 30), (b.Hash, b.W, b.H));     // never enlarged
        Assert.Equal([80, 100, 10000], b.Frames.Select(f => f.Ms));                     // the delay rule, then the clamp
        Assert.All(b.Frames, f =>
        {
            Assert.True(PersonaFrames.FrameAllowed(f.Data.Length));
            using var decoded = SKBitmap.Decode(f.Data);
            Assert.Equal((40, 30), (decoded.Width, decoded.Height));
        });
        using var first = SKBitmap.Decode(b.Frames[0].Data);
        using var second = SKBitmap.Decode(b.Frames[1].Data);
        Assert.NotEqual(first.GetPixel(5, 5), second.GetPixel(5, 5));                   // the frames really differ
        Assert.Equal(b.Frames.Sum(f => (long)f.Data.Length), b.Bytes);
        Assert.Same(b.Windows, b.Windows);                                               // chunked once
        Assert.Equal(PersonaFrames.Chunk(b.Hash, b.W, b.H, b.Frames).Select(w => w.Json), b.Windows.Select(w => w.Json));
    }

    [Fact]
    public void A_delta_frame_is_composited_onto_the_frame_it_requires()
    {
        // Frame 1 repaints only a 4x4 corner; the rest must still be frame 0's.
        var gif = TinyGif.Write(20, 20, [TinyGif.Solid(20, 20, 0), TinyGif.Solid(20, 20, 2)], [10, 10], patch: 4);
        var b = PersonaImageBundle.Build(gif)!;
        Assert.Equal(2, b.Frames.Count);
        using var zero = SKBitmap.Decode(b.Frames[0].Data);
        using var one = SKBitmap.Decode(b.Frames[1].Data);
        // WebP is lossy, so "the same" is within a few levels per channel.
        static bool Near(SKColor a, SKColor b) =>
            Math.Abs(a.Red - b.Red) <= 8 && Math.Abs(a.Green - b.Green) <= 8 && Math.Abs(a.Blue - b.Blue) <= 8;
        Assert.True(Near(zero.GetPixel(15, 15), one.GetPixel(15, 15)));                  // kept from frame 0
        Assert.False(Near(zero.GetPixel(1, 1), one.GetPixel(1, 1)));                     // the patch
    }

    [Fact]
    public void A_picture_whose_pixels_never_arrive_is_not_offered()
    {
        var png = Png(20, 20, SKColors.Red);
        Assert.Null(PersonaImageBundle.Build(png[..33]));                                // a signature and a header: no codec
        // A GIF whose LZW stream is corrupt: Skia opens it and then answers
        // ErrorInInput for the first frame, so there are no frames at all.
        var gif = TinyGif.Write(20, 20, [TinyGif.Solid(20, 20, 0)], [10]);
        gif[^5] ^= 0xFF;
        Assert.Null(PersonaImageBundle.Build(gif));
    }

    [Fact]
    public void Skia_s_failed_encode_is_null()
    {
        using var empty = new SKBitmap();
        Assert.Null(PersonaImageBundle.SkiaEncode(empty, SKEncodedImageFormat.Png, 100));
    }

    [Fact]
    public void A_large_still_is_scaled_to_216_on_its_longer_side_with_ms_zero()
    {
        var png = Png(400, 200, SKColors.Teal);
        var b = PersonaImageBundle.Build(png)!;
        Assert.Equal((216, 108), (b.W, b.H));
        Assert.Equal([0], b.Frames.Select(f => f.Ms));
    }

    [Fact]
    public void Nothing_is_offered_for_empty_oversized_or_unreadable_bytes()
    {
        Assert.Null(PersonaImageBundle.Build([]));
        Assert.Null(PersonaImageBundle.Build(new byte[PersonaFrames.MaxSourceBytes + 1]));
        Assert.Null(PersonaImageBundle.Build("not a picture at all"u8.ToArray()));
    }

    [Theory]
    [InlineData(1000, 1000, 216, 216, 216)]
    [InlineData(10, 10, 216, 16, 16)]
    [InlineData(300, 30, 216, 216, 22)]
    [InlineData(1000, 10, 216, 216, 16)]
    [InlineData(64, 64, 216, 64, 64)]
    [InlineData(500, 500, 512, 256, 256)]
    public void The_target_keeps_aspect_within_the_sides(int sw, int sh, int side, int w, int h) =>
        Assert.Equal((w, h), PersonaImageBundle.Target(sw, sh, side));

    [Fact]
    public void Decimation_keeps_every_other_frame_and_its_time()
    {
        PersonaFrames.Frame F(int ms) => new(ms, [1]);
        Assert.Equal([200, 80], PersonaImageBundle.Decimate([F(100), F(100), F(80)]).Select(f => f.Ms));
        Assert.Equal([0], PersonaImageBundle.Decimate([F(5000), F(6000)]).Select(f => f.Ms));   // one left: a still
        Assert.Equal([10000, 2], PersonaImageBundle.Decimate([F(6000), F(6000), F(1), F(1)]).Select(f => f.Ms));
    }

    private static PersonaImageBundle.Encoder Fake(Func<SKEncodedImageFormat, int, int, int?> size) =>
        (bitmap, format, quality) => size(format, quality, bitmap.Width) is { } n ? new byte[n] : null;

    [Fact]
    public void The_codec_ladder_takes_webp_then_png_then_jpeg()
    {
        using var bitmap = new SKBitmap(16, 16);
        var asked = new List<(SKEncodedImageFormat, int)>();
        PersonaImageBundle.Encoder Log(Func<SKEncodedImageFormat, int?> size) => (_, f, q) => { asked.Add((f, q)); return size(f) is { } n ? new byte[n] : null; };

        Assert.Equal(10, PersonaImageBundle.EncodeFrame(bitmap, 80, Log(f => f == SKEncodedImageFormat.Webp ? 10 : null))!.Length);
        Assert.Equal(20, PersonaImageBundle.EncodeFrame(bitmap, 80, Log(f => f == SKEncodedImageFormat.Webp ? null : 20))!.Length);
        Assert.Equal(30, PersonaImageBundle.EncodeFrame(bitmap, 80, Log(f => f == SKEncodedImageFormat.Jpeg ? 30 : 40_000))!.Length);
        Assert.Null(PersonaImageBundle.EncodeFrame(bitmap, 60, Log(f => f == SKEncodedImageFormat.Png ? 0 : 40_000)));    // empty is refused too
        Assert.Equal((SKEncodedImageFormat.Jpeg, 75), asked[^4]);                        // 75 at the top rung
        Assert.Equal((SKEncodedImageFormat.Jpeg, 60), asked[^1]);                        // the rung's own below it
        Assert.Contains((SKEncodedImageFormat.Png, 100), asked);
    }

    private static List<SKBitmap> Bitmaps(int count, int side) => Enumerable.Range(0, count).Select(_ => new SKBitmap(side, side)).ToList();

    [Fact]
    public void Over_budget_frames_are_decimated_before_quality_drops()
    {
        var bitmaps = Bitmaps(4, 216);
        var b = PersonaImageBundle.Fit("h", bitmaps, [100, 100, 100, 100], Fake((_, q, _) => q == 80 ? 30_000 : 1))!;
        Assert.Equal(4, b.Frames.Count);                                                 // 120 KB: fits as it is
        var big = PersonaImageBundle.Fit("h", Bitmaps(120, 216), Enumerable.Repeat(100, 120).ToList(), Fake((_, q, _) => q == 80 ? 30_000 : 1))!;
        Assert.Equal(60, big.Frames.Count);                                              // 3.6 MB -> 1.8 MB at quality 80
        Assert.All(big.Frames, f => Assert.Equal(200, f.Ms));
        Assert.Equal(30_000, big.Frames[0].Data.Length);
        bitmaps.ForEach(x => x.Dispose());
    }

    [Fact]
    public void A_frame_no_codec_fits_lowers_quality_then_halves_the_side()
    {
        var quality = PersonaImageBundle.Fit("h", Bitmaps(2, 216), [100, 100], Fake((_, q, _) => q == 40 ? 100 : null))!;
        Assert.Equal((216, 2), (quality.W, quality.Frames.Count));
        var halved = PersonaImageBundle.Fit("h", Bitmaps(2, 216), [100, 100], Fake((_, _, w) => w <= 54 ? 100 : null))!;
        Assert.Equal((54, 54), (halved.W, halved.H));
        Assert.Null(PersonaImageBundle.Fit("h", Bitmaps(1, 216), [0], Fake((_, _, _) => null)));       // never fits: not offered
    }

    [Fact]
    public void Skia_encodes_a_frame_that_decodes_again()
    {
        using var bitmap = new SKBitmap(24, 24);
        bitmap.Erase(SKColors.Purple);
        var bytes = PersonaImageBundle.EncodeFrame(bitmap, 80, PersonaImageBundle.SkiaEncode)!;
        using var back = SKBitmap.Decode(bytes);
        Assert.Equal(24, back.Width);
    }
}

/// <summary>CB-321: the bounded store of finished bundles, with its worker held by the test.</summary>
public class PersonaImageStoreTests
{
    private sealed class World
    {
        public readonly Dictionary<string, (long, DateTime)> Stats = new();
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<Action> Workers = [];
        public readonly List<string> Log = [];
        public Func<byte[], PersonaImageBundle?> Build = b => Bundle(b);
        public readonly PersonaImageStore Store;

        public World() => Store = new PersonaImageStore(
            p => Stats.TryGetValue(p, out var s) ? s : null, p => Files.GetValueOrDefault(p), b => Build(b), Workers.Add, Log.Add);

        public void Put(string path, string content, int at = 0)
        {
            Files[path] = Encoding.UTF8.GetBytes(content);
            Stats[path] = (content.Length, DateTime.UnixEpoch.AddSeconds(at));
        }

        public void RunAll()
        {
            var w = Workers.ToList();
            Workers.Clear();
            w.ForEach(a => a());
        }
    }

    private static PersonaImageBundle Bundle(byte[] source, int bytes = 10) =>
        new(PersonaImageBundle.HashOf(source), 24, 24, [new PersonaFrames.Frame(0, new byte[bytes])]);

    [Fact]
    public void A_picture_is_offered_only_once_its_bundle_is_built()
    {
        var w = new World();
        w.Put("/p/a.gif", "A");
        Assert.Null(w.Store.Offer("/p/a.gif"));
        Assert.Null(w.Store.Offer("/p/a.gif"));                                          // still building: no second build
        Assert.Single(w.Workers);
        w.RunAll();
        var hash = PersonaImageBundle.HashOf("A"u8.ToArray());
        Assert.Equal(hash, w.Store.Offer("/p/a.gif"));
        Assert.Empty(w.Workers);
        Assert.Equal(hash, w.Store.ById(hash)!.Hash);
        Assert.Null(w.Store.ById(new string('0', 64)));
        Assert.Contains(w.Log, l => l.StartsWith($"persona_image {hash[..12]} ready frames 1 24x24", StringComparison.Ordinal));
        Assert.DoesNotContain(w.Log, l => l.Contains("/p/"));
    }

    [Fact]
    public void No_path_or_no_file_is_no_picture()
    {
        var w = new World();
        Assert.Null(w.Store.Offer(null));
        Assert.Null(w.Store.Offer(""));
        Assert.Null(w.Store.Offer("/missing.gif"));
        Assert.Empty(w.Workers);
    }

    [Fact]
    public void A_changed_file_is_rebuilt_and_its_old_hash_is_still_answered_until_evicted()
    {
        var w = new World();
        w.Put("/p/a.gif", "A");
        w.Store.Offer("/p/a.gif");
        w.RunAll();
        w.Put("/p/a.gif", "B", at: 5);
        Assert.Null(w.Store.Offer("/p/a.gif"));
        w.RunAll();
        Assert.Equal(PersonaImageBundle.HashOf("B"u8.ToArray()), w.Store.Offer("/p/a.gif"));
        Assert.NotNull(w.Store.ById(PersonaImageBundle.HashOf("A"u8.ToArray())));
    }

    [Fact]
    public void A_picture_that_cannot_be_offered_is_not_retried_until_it_changes()
    {
        var w = new World { Build = _ => null };
        w.Put("/p/a.gif", "A");
        w.Store.Offer("/p/a.gif");
        w.RunAll();
        Assert.Null(w.Store.Offer("/p/a.gif"));
        Assert.Empty(w.Workers);
        Assert.Contains("persona_image not offered: unreadable, over the cap, or no frames fit", w.Log);

        w.Put("/p/b.gif", "B");                                                          // unreadable
        w.Files.Remove("/p/b.gif");
        w.Store.Offer("/p/b.gif");
        w.RunAll();
        Assert.Null(w.Store.Offer("/p/b.gif"));
        Assert.Empty(w.Workers);
    }

    [Fact]
    public void A_build_that_throws_is_a_picture_not_offered()
    {
        var w = new World { Build = _ => throw new InvalidOperationException("skia") };
        w.Put("/p/a.gif", "A");
        w.Store.Offer("/p/a.gif");
        w.RunAll();
        Assert.Null(w.Store.Offer("/p/a.gif"));
        Assert.Contains("persona_image build threw InvalidOperationException", w.Log);
    }

    [Fact]
    public void A_build_superseded_by_a_newer_file_is_discarded()
    {
        var w = new World();
        w.Put("/p/a.gif", "A");
        w.Store.Offer("/p/a.gif");
        var stale = w.Workers.Single();
        w.Workers.Clear();
        w.Put("/p/a.gif", "B", at: 9);
        w.Store.Offer("/p/a.gif");
        stale();                                                                          // A finishes after B was asked for
        Assert.Null(w.Store.Offer("/p/a.gif"));
        Assert.Equal(0, w.Store.Count);
        var stale2 = w.Workers.Single();
        w.Workers.Clear();
        w.Put("/p/a.gif", "BB", at: 9);                                                   // same time, different length
        w.Store.Offer("/p/a.gif");
        stale2();
        Assert.Equal(0, w.Store.Count);
        w.RunAll();
        Assert.Equal(PersonaImageBundle.HashOf("BB"u8.ToArray()), w.Store.Offer("/p/a.gif"));
    }

    [Fact]
    public void The_store_keeps_eight_bundles_oldest_out_and_rebuilds_a_forgotten_one()
    {
        var w = new World();
        for (var i = 0; i < 9; i++)
        {
            w.Put($"/p/{i}.gif", "picture " + i);
            w.Store.Offer($"/p/{i}.gif");
        }
        w.RunAll();
        Assert.Equal(PersonaImageStore.Capacity, w.Store.Count);
        Assert.Null(w.Store.ById(PersonaImageBundle.HashOf("picture 0"u8.ToArray())));
        Assert.Null(w.Store.Offer("/p/0.gif"));                                          // forgotten: built again
        Assert.Single(w.Workers);
        Assert.NotNull(w.Store.Offer("/p/8.gif"));
    }

    [Fact]
    public void The_store_keeps_under_32_mb_but_always_the_newest()
    {
        var w = new World { Build = b => Bundle(b, 20 * 1024 * 1024) };
        w.Put("/p/a.gif", "A");
        w.Put("/p/b.gif", "B");
        w.Store.Offer("/p/a.gif");
        w.Store.Offer("/p/b.gif");
        w.RunAll();
        Assert.Equal(1, w.Store.Count);
        Assert.Equal(20L * 1024 * 1024, w.Store.TotalBytes);
        Assert.NotNull(w.Store.ById(PersonaImageBundle.HashOf("B"u8.ToArray())));
    }

    [Fact]
    public void Two_paths_to_the_same_picture_share_one_bundle()
    {
        var w = new World();
        w.Put("/p/a.gif", "same");
        w.Put("/q/a.gif", "same");
        w.Store.Offer("/p/a.gif");
        w.Store.Offer("/q/a.gif");
        w.RunAll();
        Assert.Equal(1, w.Store.Count);
        Assert.Equal(w.Store.Offer("/p/a.gif"), w.Store.Offer("/q/a.gif"));
    }
}

/// <summary>
/// CB-321: RelayService.Project through its seams, in the unit suite as well
/// as the UI suite (RelaySettingsAndServiceTests), because only coverlet counts
/// the method's own lines. Nothing else in this suite touches RelayService's
/// statics, and every seam is put back.
/// </summary>
public class RelayServiceProjectTests
{
    [Fact]
    public void A_scan_s_rows_and_personas_frame_come_from_the_seams()
    {
        try
        {
            RelayService.UserConfigDirs = () => [];
            RelayService.PersonaName = (_, _) => "Nova";
            RelayService.LocalPersonaFor = _ => null;
            RelayService.OfferImage = _ => null;
            RelayService.DefaultPersona = _ => null;
            var (rows, personas, defaultPersona) = RelayService.Project([("s1", new SessionStatus { Source = SessionSource.Codex, Cwd = "/w" })]);
            Assert.Null(defaultPersona);                                     // CB-321 addendum 2: no default row, no limit persona
            var id = PersonaFrames.PersonaId(null, "Nova");
            Assert.Equal(new RelayPersona("Nova", "NO", null, id), Assert.Single(rows).Persona);
            Assert.Equal(PersonaFrames.Personas([new PersonaFrames.Row(id, "Nova", "NO", false, null)]), personas);
        }
        finally
        {
            RelayService.UserConfigDirs = RelayService.DefaultUserConfigDirs;
            RelayService.PersonaName = RelayService.CachedPersonaName;
            RelayService.LocalPersonaFor = LocalPersonas.For;
            RelayService.OfferImage = RelayService.DefaultOfferImage;
            RelayService.DefaultPersona = RelayService.CachedDefaultPersona;
        }
    }
}
