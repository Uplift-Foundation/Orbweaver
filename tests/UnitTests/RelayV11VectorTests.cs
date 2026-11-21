using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-339: the desktop's half of frames-v11 (the folder picker), pinned against the committed
/// frames-v11-vectors.json, whose independent check is Orbweaver-Mobile's check_v11_vectors.py.
/// Every one of the 66 listing cases is replayed through the shipped BrowseRules over the vectors'
/// own model desks; the shape, rate, automatic-root and sort tables through the shipped parse,
/// limiter, DefaultRoots and Compare; and every sealed box is opened under the epoch-0 key and
/// written again by the desktop. The phone's tables (offer, join, crumbs, assemble) are the
/// phones' to prove and are not read here.
/// </summary>
public class RelayV11VectorTests
{
    internal static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v11-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly byte[] Key = Convert.FromHexString(Str(In, "session_key_epoch0"));
    private static readonly byte[] CkKey = Convert.FromHexString(Str(In, "ck_key"));

    private static FakeBrowseFileSystem Desk(string name) => FakeBrowseFileSystem.FromVectors(V.GetProperty("desks").GetProperty(name));

    private static BrowseSettings Settings(string desk, string name)
    {
        var s = V.GetProperty("desks").GetProperty(desk).GetProperty("settings").GetProperty(name);
        return new BrowseSettings(s.GetProperty("roots").EnumerateArray().Select(r => r.GetString()!).ToList(), s.GetProperty("home").GetBoolean());
    }

    private static JsonElement Case(string name) =>
        V.GetProperty("listing").GetProperty("cases").EnumerateArray().First(c => Str(c, "name") == name);

    public static TheoryData<string> ListingCases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var c in V.GetProperty("listing").GetProperty("cases").EnumerateArray()) data.Add(Str(c, "name"));
            return data;
        }
    }

    // What the desktop sends for a case, as plaintext strings, from the request as the phone wrote it.
    private static List<string> Reply(JsonElement c)
    {
        var request = c.GetProperty("request");
        var reference = Str(c, "envelope_id");
        var hidden = request.TryGetProperty("hidden", out var h) && h.GetBoolean();
        var outcome = BrowseRules.Resolve(Str(request, "path"), hidden, Settings(Str(c, "desk"), Str(c, "settings")), Desk(Str(c, "desk")));
        return outcome.Err is { } code
            ? [RelayReplies.Err(reference, code)]
            : BrowseFrames.Dir(reference, outcome.Listing!);
    }

    [Fact]
    public void The_vectors_carry_the_66_listing_cases_this_replays() =>
        Assert.Equal(66, V.GetProperty("listing").GetProperty("cases").GetArrayLength());

    [Theory]
    [MemberData(nameof(ListingCases))]
    public void Each_listing_case_is_answered_as_the_vector_says_byte_for_byte(string name)
    {
        var c = Case(name);
        var request = c.GetProperty("request");
        var hidden = request.TryGetProperty("hidden", out var h) && h.GetBoolean();
        var outcome = BrowseRules.Resolve(Str(request, "path"), hidden, Settings(Str(c, "desk"), Str(c, "settings")), Desk(Str(c, "desk")));

        // The canonical path the desk arrived at, where the case says (a request refused at step 5
        // never gets one).
        if (c.GetProperty("canonical").ValueKind == JsonValueKind.String)
            Assert.Equal(Str(c, "canonical"), outcome.Canonical);
        else
            Assert.Null(outcome.Canonical);

        var sent = Reply(c);
        if (c.TryGetProperty("err", out var err))
        {
            Assert.Equal(Str(err, "code"), outcome.Err);
            Assert.Equal([Str(c, "envelope_id")], new[] { Str(err, "ref") });
            Assert.Equal(Canonical(err), Assert.Single(sent));
            return;
        }

        Assert.Null(outcome.Err);
        var frames = c.GetProperty("frames").EnumerateArray().ToList();
        Assert.Equal(frames.Count, sent.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(Canonical(frames[i]), sent[i]);
            Assert.Equal(c.GetProperty("frame_bytes")[i].GetInt32(), Encoding.UTF8.GetByteCount(sent[i]));
        }
    }

    // The vector's frame as canonical text: keys in the file's order, no whitespace, the shared
    // escaper. The file stores frames as JSON objects whose key order IS the contract's.
    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Select(p => CanonicalJson.Quote(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => CanonicalJson.Quote(e.GetString()!),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    [Fact]
    public void The_limits_are_the_shipped_constants()
    {
        var l = V.GetProperty("limits");
        Assert.Equal(l.GetProperty("max_dir_plaintext_bytes").GetInt32(), BrowseLimits.MaxDirPlaintextBytes);
        Assert.Equal(l.GetProperty("max_entries").GetInt32(), BrowseLimits.MaxEntries);
        Assert.Equal(l.GetProperty("max_path_bytes").GetInt32(), BrowseLimits.MaxPathBytes);
        Assert.Equal(l.GetProperty("max_roots").GetInt32(), BrowseLimits.MaxRoots);
        Assert.Equal(l.GetProperty("rate_burst").GetInt32(), BrowseLimits.RateBurst);
        Assert.Equal(l.GetProperty("rate_interval_ms").GetInt32(), BrowseLimits.RateIntervalMs);
    }

    [Fact]
    public void The_system_folders_the_model_desks_deny_are_what_the_shipped_list_builds()
    {
        foreach (var name in new[] { "mac", "win" })
        {
            var d = V.GetProperty("desks").GetProperty(name);
            var windows = name == "win";
            var (exact, trees) = BrowseRules.SystemFolders(windows, Str(d, "home"), @"C:\Windows", [@"C:\", @"D:\"]);
            Assert.Equal(d.GetProperty("denied_exact").EnumerateArray().Select(e => e.GetString()), exact);
            Assert.Equal(d.GetProperty("denied_trees").EnumerateArray().Select(e => e.GetString()), trees);
        }
    }

    // ---- shape, rate, automatic roots, sort -------------------------------------------------

    [Fact]
    public void Every_shape_case_is_bad_frame_exactly_where_the_vector_says()
    {
        var cases = V.GetProperty("shape").GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var parsed = RelayRequests.Parse(JsonDocument.Parse(Str(c, "plaintext")).RootElement, "ref");
            var d = Assert.IsType<ListDirRequest>(parsed);
            var expectBad = c.GetProperty("err").ValueKind == JsonValueKind.String;
            Assert.True(expectBad == (d.Path is null), Str(c, "_note"));
        }
    }

    [Fact]
    public void The_rate_table_is_replayed_per_phone_to_the_millisecond()
    {
        var limiter = new BrowseRateLimiter();
        var events = V.GetProperty("rate").GetProperty("events").EnumerateArray().ToList();
        Assert.True(events.Count > 30);
        foreach (var e in events)
            Assert.Equal(e.GetProperty("accepted").GetBoolean(), limiter.TryAccept(Str(e, "phone"), e.GetProperty("at_ms").GetInt64()));
    }

    [Fact]
    public void The_automatic_roots_table_is_what_DefaultRoots_derives()
    {
        var cases = V.GetProperty("default_roots").GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(13, cases.Count);
        foreach (var c in cases)
            Assert.True(c.GetProperty("roots").EnumerateArray().Select(r => r.GetString()).SequenceEqual(
                BrowseRules.DefaultRoots(c.GetProperty("recent").EnumerateArray().Select(r => r.GetString()!), Desk(Str(c, "desk")))),
                Str(c, "_note"));
    }

    [Fact]
    public void The_sort_table_is_the_shipped_comparison_in_utf8_bytes()
    {
        var s = V.GetProperty("sort");
        var input = s.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
        input.Sort(BrowseRules.Compare);
        Assert.Equal(s.GetProperty("sorted").EnumerateArray().Select(e => e.GetString()), input);
    }

    // ---- the sealed boxes -------------------------------------------------------------------

    private static void AssertBox(JsonElement box, uint direction, string expectedPlaintext, string? expectedCk = null)
    {
        Assert.Equal(expectedPlaintext, Str(box, "plaintext"));
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedPlaintext), box.GetProperty("plaintext_bytes").GetInt32());
        var aad = Convert.FromHexString(Str(box, "aad"));
        Assert.Equal(Aad(Str(box, "from"), Str(box, "to"), Str(box, "class")), aad);
        Assert.Equal(expectedPlaintext, Encoding.UTF8.GetString(Open(Key, Str(box, "ct"), aad, direction)!));
        Assert.Equal(Str(box, "ct"), Seal(Key, Convert.FromHexString(Str(box, "nonce")), Encoding.UTF8.GetBytes(expectedPlaintext), aad));
        if (expectedCk is not null) Assert.Equal(expectedCk, Str(box, "ck"));
    }

    [Fact]
    public void The_caps_frame_is_the_shipped_bytes_on_its_own_ck()
    {
        Assert.Equal(Str(In, "ck_caps"), Ck(CkKey, BrowseFrames.CapsCk));
        // v11's desk advertised list_dir alone; frames-v13 widens the shipped caps (RelayV13VectorTests).
        AssertBox(V.GetProperty("sealed").GetProperty("caps"), DesktopToPhone, BrowseFrames.CapsFrame("list_dir"), Str(In, "ck_caps"));
    }

    [Fact]
    public void Every_sealed_request_opens_and_every_sealed_reply_is_what_the_desktop_writes()
    {
        foreach (var (name, box) in V.GetProperty("sealed").EnumerateObject().Where(p => p.Name != "caps").Select(p => (p.Name, p.Value)))
        {
            var reference = Str(box, "envelope_id");
            List<string> expected;
            if (name == "rate_limited") expected = [RelayReplies.Err(reference, RelayReplies.RateLimited)];
            else if (name == "bad_frame") expected = [RelayReplies.Err(reference, RelayReplies.BadFrame)];
            else expected = Reply(Case(name));

            if (box.TryGetProperty("request", out var request))
                AssertBox(request, PhoneToDesktop, Str(request, "plaintext"));
            var replies = box.GetProperty("reply").ValueKind == JsonValueKind.Array
                ? box.GetProperty("reply").EnumerateArray().ToList() : [box.GetProperty("reply")];
            Assert.Equal(expected.Count, replies.Count);
            for (var i = 0; i < replies.Count; i++) AssertBox(replies[i], DesktopToPhone, expected[i], "");
        }
    }

    [Fact]
    public void A_sealed_list_dir_request_parses_to_the_path_the_phone_wrote()
    {
        var request = V.GetProperty("sealed").GetProperty("source_hidden").GetProperty("request");
        var parsed = Assert.IsType<ListDirRequest>(RelayRequests.Parse(JsonDocument.Parse(Str(request, "plaintext")).RootElement, "r"));
        Assert.Equal(("/Users/dev/Source", true), (parsed.Path, parsed.Hidden));
    }

    [Fact]
    public void Planted_strings_are_in_the_sealed_traffic_and_in_nothing_the_relay_reads()
    {
        var b = V.GetProperty("blindness");
        var plaintexts = new List<string>();
        var visible = new List<string>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("plaintext", out var p) && e.TryGetProperty("ct", out _))
                {
                    plaintexts.Add(p.GetString()!);
                    foreach (var f in b.GetProperty("relay_visible").EnumerateArray()) if (e.TryGetProperty(f.GetString()!, out var v)) visible.Add(v.ToString());
                }
                foreach (var prop in e.EnumerateObject()) Walk(prop.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(V.GetProperty("sealed"));
        Assert.NotEmpty(visible);
        foreach (var planted in b.GetProperty("planted").EnumerateArray().Select(e => e.GetString()!))
        {
            var escaped = CanonicalJson.Quote(planted)[1..^1];
            Assert.Contains(plaintexts, t => t.Contains(escaped));                 // the positive control
            Assert.DoesNotContain(visible, v => v.Contains(planted) || v.Contains(escaped));
        }
    }
}
