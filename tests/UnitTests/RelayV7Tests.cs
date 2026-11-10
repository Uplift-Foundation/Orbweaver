using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323: the desktop against frames-v7-vectors.json. The file regenerates
/// byte for byte from the shipped code; a computed value tampered in the input
/// comes back recomputed rather than copied; and the activity grammar, written
/// here from frames-v7.json's words, accepts the five accepted frames, refuses
/// each of the nineteen refused ones on exactly its named field, and accepts
/// everything the desktop's own writer produces from a few thousand random
/// books — which is what "the desktop cannot produce a refused frame" means
/// when it has to be shown rather than said.
/// </summary>
public class RelayV7Tests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay");
    private static readonly string Committed = Path.Combine(Dir, "frames-v7-vectors.json");
    internal static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Committed)).RootElement;

    private static string Build(string path)
    {
        var source = V.GetProperty("_source");
        return V7Vectors.Build(path, source.GetProperty("orbweaver_sha").GetString()!, source.GetProperty("generated_by").GetString());
    }

    [Fact]
    public void The_desktop_regenerates_the_committed_vectors_byte_for_byte()
    {
        var committed = File.ReadAllText(Committed);
        var ours = Build(Committed);
        if (ours != committed)
        {
            var at = 0;
            while (at < Math.Min(ours.Length, committed.Length) && ours[at] == committed[at]) at++;
            Assert.Fail($"first difference at char {at}: ours «{ours[Math.Max(0, at - 80)..Math.Min(ours.Length, at + 80)]}» committed «{committed[Math.Max(0, at - 80)..Math.Min(committed.Length, at + 80)]}»");
        }
    }

    [Fact]
    public void A_regeneration_through_a_real_file_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "cb323-v7-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, Build(Committed));
            Assert.Equal(File.ReadAllText(Committed), Build(path));
        }
        finally { File.Delete(path); }
    }

    // Each case puts a wrong value where the desktop computes one and expects
    // the computed value back: a copied field would keep the tampering.
    [Theory]
    [InlineData("cut_rule[2].out", "MMMM")]
    [InlineData("content_state_example.hash", "0000000000000000")]
    [InlineData("content_state_example.change_key", "{}")]
    [InlineData("content_state_example.canonical", "{}")]
    [InlineData("limits.max_command_utf16", 161)]
    [InlineData("limits.boot_hex", 15)]
    [InlineData("limits.max_session_id_utf16", 65)]
    public void A_tampered_computed_value_is_recomputed_not_copied(string path, object wrong)
    {
        var root = JsonNode.Parse(File.ReadAllText(Committed))!;
        var original = Find(root, path)!.ToJsonString();
        Assert.Equal(original, Find(Rebuilt(root, path, JsonValue.Create(wrong)), path)!.ToJsonString());
    }

    // A frame's fields are inputs — the sessions and prompt it describes — so
    // an out-of-grammar input comes back through the desktop's own rule: cut,
    // or clamped, never copied.
    [Theory]
    [InlineData("snapshot_grammar.accepted[0].oldest.title", 65, 'T', 64)]
    [InlineData("snapshot_grammar.accepted[3].machine", 36, 'M', 32)]
    [InlineData("timeline.events[4].frame.oldest.command", 170, 'c', 160)]
    [InlineData("timeline.events[4].frame.oldest.tool", 40, 't', 32)]
    public void An_uncut_input_comes_back_cut(string path, int length, char ch, int limit)
    {
        var root = JsonNode.Parse(File.ReadAllText(Committed))!;
        var rebuilt = Rebuilt(root, path, JsonValue.Create(new string(ch, length)));
        Assert.Equal(new string(ch, limit - 1) + "…", Find(rebuilt, path)!.GetValue<string>());
    }

    [Fact]
    public void A_count_over_the_maximum_comes_back_clamped()
    {
        var root = JsonNode.Parse(File.ReadAllText(Committed))!;
        var rebuilt = Rebuilt(root, "snapshot_grammar.accepted[3].working", JsonValue.Create(10_500));
        Assert.Equal(RelayActivity.CountsMax, Find(rebuilt, "snapshot_grammar.accepted[3].working")!.GetValue<int>());
    }

    private static JsonNode Rebuilt(JsonNode root, string path, JsonNode? wrong)
    {
        Set(root, path, wrong);
        var tampered = Path.Combine(Path.GetTempPath(), "cb323-v7-tampered-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tampered, root.ToJsonString());
            return JsonNode.Parse(Build(tampered))!;
        }
        finally { File.Delete(tampered); }
    }

    private static JsonNode? Find(JsonNode node, string path)
    {
        foreach (var part in Regex.Matches(path, @"[^.\[\]]+").Select(m => m.Value))
            node = int.TryParse(part, out var i) ? node[i]! : node[part]!;
        return node;
    }

    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var parent = Find(root, path[..path.LastIndexOfAny(['.', '['])])!;
        var last = Regex.Match(path, @"[^.\[\]]+$").Value;
        if (int.TryParse(last, out var i)) parent[i] = value; else parent[last] = value;
    }

    // ---- the grammar, from frames-v7.json's words ---------------------------------

    private static readonly Regex EnvelopeId = new("^[A-Za-z0-9_.:-]{1,64}$");
    private static readonly Regex BootHex = new("^[0-9a-f]{16}$");
    private static readonly string[] OldestKeys = ["session_id", "request_id", "title", "tool", "command", "since"];

    // The field the relay names when it refuses a frame, or null when the frame
    // is in the grammar.
    internal static string? Refusal(JsonElement f)
    {
        static bool Int(JsonElement e, long min, long max) =>
            e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n >= min && n <= max;
        static bool Str(JsonElement f, string name, out string value)
        {
            value = "";
            if (!f.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString()!;
            return true;
        }

        if (!Str(f, "ref", out var r) || !EnvelopeId.IsMatch(r)) return "ref";
        if (!Str(f, "boot", out var b) || !BootHex.IsMatch(b)) return "boot";
        if (!f.TryGetProperty("seq", out var seq) || !Int(seq, 1, 1L << 53)) return "seq";
        if (!Str(f, "machine", out var m) || m.Length is 0 or > RelayActivity.MaxMachine) return "machine";
        foreach (var name in new[] { "amber", "working", "idle" })
            if (!f.TryGetProperty(name, out var n) || !Int(n, 0, RelayActivity.CountsMax)) return name;
        var amber = f.GetProperty("amber").GetInt32();
        if (!f.TryGetProperty("oldest", out var o)) return "oldest";
        if (amber == 0) return o.ValueKind == JsonValueKind.Null ? null : "oldest";
        if (o.ValueKind != JsonValueKind.Object || !o.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(OldestKeys.Order())) return "oldest";
        if (!Str(o, "session_id", out var sid) || sid.Length is 0 or > RelayActivity.MaxSessionId) return "oldest.session_id";
        if (!Str(o, "request_id", out var rid) || (rid.Length > 0 && !EnvelopeId.IsMatch(rid))) return "oldest.request_id";
        if (!Str(o, "title", out var title) || title.Length > RelayActivity.MaxTitle) return "oldest.title";
        if (!Str(o, "tool", out var tool) || tool.Length > RelayActivity.MaxTool) return "oldest.tool";
        if (!Str(o, "command", out var command) || command.Length > RelayActivity.MaxCommand) return "oldest.command";
        if (!Int(o.GetProperty("since"), 0, long.MaxValue)) return "oldest.since";
        return null;
    }

    [Fact]
    public void The_grammar_accepts_every_accepted_frame_and_every_timeline_snapshot()
    {
        var accepted = V.GetProperty("snapshot_grammar").GetProperty("accepted").EnumerateArray().ToList();
        Assert.Equal(5, accepted.Count);
        Assert.All(accepted, f => Assert.Null(Refusal(f)));
        var snapshots = V.GetProperty("timeline").GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "snapshot").Select(e => e.GetProperty("frame")).ToList();
        Assert.Equal(13, snapshots.Count);
        Assert.All(snapshots, f => Assert.Null(Refusal(f)));
    }

    // The control: the same grammar refuses each refused frame, on its field.
    [Fact]
    public void The_grammar_refuses_each_refused_frame_on_exactly_its_field()
    {
        var refused = V.GetProperty("snapshot_grammar").GetProperty("refused").EnumerateArray().ToList();
        Assert.Equal(19, refused.Count);
        Assert.All(refused, r => Assert.Equal(r.GetProperty("field").GetString(), Refusal(r.GetProperty("frame"))));
    }

    [Fact]
    public void Nothing_the_desktop_writes_from_a_random_book_is_outside_the_grammar()
    {
        var rng = new Random(323);
        string[] states = [RelayRoster.Waiting, RelayRoster.Generating, RelayRoster.Idle];
        string Text(int max) => string.Concat(Enumerable.Range(0, rng.Next(max)).Select(_ => rng.Next(12) switch
        {
            0 => "😀", 1 => "\"", 2 => "\\", 3 => "\n", 4 => "é", _ => ((char)rng.Next('a', 'z' + 1)).ToString(),
        }));
        for (var round = 0; round < 3000; round++)
        {
            var rows = Enumerable.Range(0, rng.Next(12)).Select(i =>
                new RelaySession($"s{i}-{rng.Next(1000)}", "claude", Text(90), "/c", states[rng.Next(3)], null, null)
                { Since = DateTimeOffset.FromUnixTimeSeconds(1794111792 + rng.Next(5)) }).ToList();
            var prompts = rows.ToDictionary(r => r.SessionId, _ => rng.Next(3) == 0 ? null
                : new ActivityPrompt(RelayLink.Ulid(DateTimeOffset.UnixEpoch.AddDays(rng.Next(20000))), Text(50), Text(250)));
            var s = RelayActivity.Snapshot(rows, id => prompts.GetValueOrDefault(id), Text(50));
            var frame = RelayActivity.Frame(RelayActivity.Ref(RelayActivity.NewBoot(), round + 1), RelayActivity.NewBoot(), rng.NextInt64(1, 1L << 53), s);
            Assert.Null(Refusal(JsonDocument.Parse(frame).RootElement));
        }
    }
}
