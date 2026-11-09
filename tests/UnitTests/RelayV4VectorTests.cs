using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-316: the desktop's v4 frames against Orbweaver-Mobile
/// contracts/frames-v4-vectors.json (checked there by check_v4_vectors.py,
/// an independent implementation). Every plaintext the desktop writes is
/// rebuilt here by the shipped code and compared byte for byte, and every
/// box is opened with the epoch-0 key — so a canonical-form drift fails a
/// build instead of a phone.
/// </summary>
public class RelayV4VectorTests
{
    internal static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v4-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly byte[] Key = Convert.FromHexString(Str(In, "session_key_epoch0"));
    private static readonly byte[] CkKey = Convert.FromHexString(Str(In, "ck_key"));
    private static readonly string Sid = Str(In, "session_id");

    internal static List<TranscriptWatch.WireLine> Lines(JsonElement array) =>
        array.EnumerateArray().Select(l => TranscriptWatch.Line(
            Str(l, "role"), Str(l, "text"), l.GetProperty("at").GetInt64(),
            l.TryGetProperty("error", out var e) && e.GetBoolean())).ToList();

    private static readonly List<TranscriptWatch.WireLine> All = Lines(V.GetProperty("window").GetProperty("lines_input"));

    // A box opens under the epoch-0 key with its own aad, and its plaintext is
    // the vector's; then the expected plaintext is compared with ours.
    internal static void AssertBox(JsonElement box, string expectedPlaintext)
    {
        Assert.Equal(Str(box, "plaintext"), expectedPlaintext);
        Assert.Equal(box.GetProperty("plaintext_bytes").GetInt32(), Encoding.UTF8.GetByteCount(expectedPlaintext));
        var aad = Convert.FromHexString(Str(box, "aad"));
        Assert.Equal(Aad(Str(box, "from"), Str(box, "to"), Str(box, "class")), aad);
        var direction = Str(box, "from").StartsWith("dsk_", StringComparison.Ordinal) ? DesktopToPhone : PhoneToDesktop;
        var opened = Open(Key, Str(box, "ct"), aad, direction);
        Assert.NotNull(opened);
        Assert.Equal(expectedPlaintext, Encoding.UTF8.GetString(opened));
        // Sealing our bytes under the vector's nonce gives the vector's ct.
        Assert.Equal(Str(box, "ct"), Seal(Key, Convert.FromHexString(Str(box, "nonce")), Encoding.UTF8.GetBytes(expectedPlaintext), aad));
    }

    private static void AssertChunks(JsonElement boxes, List<string> ours)
    {
        Assert.Equal(boxes.GetArrayLength(), ours.Count);
        var i = 0;
        foreach (var box in boxes.EnumerateArray())
        {
            AssertBox(box, ours[i]);
            Assert.Equal(Ck(CkKey, Sid), Str(box, "ck"));
            Assert.True(Encoding.UTF8.GetByteCount(ours[i]) <= TranscriptWatch.MaxWindowBytes);
            i++;
        }
    }

    [Fact]
    public void Ck_values_are_the_shipped_Ck()
    {
        Assert.Equal(Str(In, "ck_session"), Ck(CkKey, Sid));
        Assert.Equal(Str(In, "ck_usage"), Ck(CkKey, "usage"));
    }

    [Fact]
    public void Backfill_chunks_byte_for_byte() =>
        AssertChunks(V.GetProperty("window").GetProperty("backfill"), TranscriptWatch.Backfill(Sid, All));

    [Fact]
    public void Page_before_30_byte_for_byte() =>
        AssertChunks(V.GetProperty("window").GetProperty("page_before_30"), TranscriptWatch.Page(Sid, All, 30));

    [Fact]
    public void Tail_byte_for_byte()
    {
        var grown = All.Concat(Lines(V.GetProperty("window").GetProperty("tail_appended_input"))).ToList();
        AssertChunks(V.GetProperty("window").GetProperty("tail"), TranscriptWatch.Tail(Sid, grown, All.Count));
    }

    // Mobile #31: a last row rewritten in place is restated from its own
    // index, alone or with a row after it, decided by the desk's Diff.
    [Theory]
    [InlineData("restated_last_row")]
    [InlineData("restated_and_appended")]
    public void Restated_tail_byte_for_byte(string name)
    {
        var v = V.GetProperty("window").GetProperty(name);
        var prev = All.Concat(Lines(V.GetProperty("window").GetProperty("tail_appended_input"))).ToList();
        var changed = Lines(JsonDocument.Parse("[" + v.GetProperty("changed_row").GetRawText() + "]").RootElement);
        var now = prev.Take(v.GetProperty("prev_count").GetInt32() - 1).Concat(changed).Concat(Lines(v.GetProperty("appended"))).ToList();
        Assert.Equal(v.GetProperty("new_count").GetInt32(), now.Count);

        var change = TranscriptWatch.Diff(prev.Count, prev[0], now, prev[^1]);
        Assert.Equal(TranscriptWatch.Change.Restate, change);
        AssertChunks(v.GetProperty("windows"), TranscriptWatch.Tail(Sid, now, TranscriptWatch.Start(change, prev.Count)!.Value));
    }

    public static TheoryData<string> DiffCases()
    {
        var d = new TheoryData<string>();
        foreach (var c in V.GetProperty("window").GetProperty("diff_rule").GetProperty("cases").EnumerateArray()) d.Add(Str(c, "name"));
        return d;
    }

    [Theory]
    [MemberData(nameof(DiffCases))]
    public void Diff_rule_case_matches_the_vector(string name)
    {
        var c = V.GetProperty("window").GetProperty("diff_rule").GetProperty("cases").EnumerateArray().Single(x => Str(x, "name") == name);
        var prev = Lines(c.GetProperty("prev"));
        var now = Lines(c.GetProperty("new"));
        var change = TranscriptWatch.Diff(prev.Count, prev.Count > 0 ? prev[0] : null, now, prev.Count > 0 ? prev[^1] : null);
        var start = TranscriptWatch.Start(change, prev.Count);
        var expect = Str(c, "expect");
        Assert.Equal(expect, start is not null ? "tail" : change == TranscriptWatch.Change.Reset ? "reset" : "none");
        if (expect == "tail")
            Assert.Equal((c.GetProperty("from_line").GetInt32(), c.GetProperty("line_count").GetInt32()), (start!.Value, now.Count - start.Value));
        else
            Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (c.GetProperty("from_line").ValueKind, c.GetProperty("line_count").ValueKind));
    }

    [Fact]
    public void Reset_to_5_byte_for_byte() =>
        AssertChunks(V.GetProperty("window").GetProperty("reset_to_5"), TranscriptWatch.Backfill(Sid, All.Take(5).ToList(), reset: true));

    [Fact]
    public void Cut_lines_are_the_vectors()
    {
        var cut = All.Select((l, i) => (l, i)).Where(x => x.l.Cut).Select(x => x.i).ToList();
        Assert.Equal(V.GetProperty("window").GetProperty("cut_lines").EnumerateArray().Select(e => e.GetInt32()), cut);
    }

    // Every window but the last of a run is maximal: the checker's proof, again
    // here so the desktop's chunker is held to it without the checker.
    [Fact]
    public void Every_backfill_chunk_but_the_last_is_maximal()
    {
        var windows = TranscriptWatch.Backfill(Sid, All);
        Assert.True(windows.Count > 1, "the fixture is meant to need more than one chunk");
        var from = All.Count - TranscriptWatch.BackfillLines;
        for (var w = 0; w < windows.Count - 1; w++)
        {
            using var doc = JsonDocument.Parse(windows[w]);
            var count = doc.RootElement.GetProperty("lines").GetArrayLength();
            var grown = TranscriptWatch.Window(Sid, from, All.Count, false, false,
                All.Skip(from).Take(count + 1).Select(TranscriptWatch.LineJson));
            Assert.True(Encoding.UTF8.GetByteCount(grown) > TranscriptWatch.MaxWindowBytes);
            from += count;
        }
    }

    // ---- watch / unwatch -----------------------------------------------------

    private static JsonElement W => V.GetProperty("watch");

    private static RelayRequest? ParsePlain(JsonElement box) =>
        RelayRequests.Parse(JsonDocument.Parse(Str(box, "plaintext")).RootElement, box.TryGetProperty("envelope_id", out var id) ? id.GetString()! : "");

    [Fact]
    public void Watch_requests_parse_and_replies_are_byte_for_byte()
    {
        var sub = Assert.IsType<WatchRequest>(ParsePlain(W.GetProperty("subscribe")));
        Assert.Equal((Str(W.GetProperty("subscribe"), "envelope_id"), Sid, (long?)null), (sub.Ref, sub.SessionId, sub.Before));
        var page = Assert.IsType<WatchRequest>(ParsePlain(W.GetProperty("page")));
        Assert.Equal(30, page.Before);
        var un = Assert.IsType<UnwatchRequest>(ParsePlain(W.GetProperty("unsubscribe")));
        Assert.Equal(Sid, un.SessionId);

        AssertBox(W.GetProperty("ok_watching"), RelayReplies.Ok(sub.Ref, RelayReplies.Watching, Sid, All.Count));
        AssertBox(W.GetProperty("ok_unwatched"), RelayReplies.Ok(un.Ref, RelayReplies.Unwatched, Sid));
        AssertBox(W.GetProperty("err_unknown_session"), RelayReplies.Err("01JCA0WATCH0000000000000AE", RelayReplies.UnknownSession, "deadbeef"));
        AssertBox(W.GetProperty("err_bad_frame"), RelayReplies.Err("01JCA0WATCH0000000000000AF", RelayReplies.BadFrame, Sid));
        Assert.Equal("", Str(W.GetProperty("err_unknown_session"), "ck"));
        Assert.Equal(Ck(CkKey, Sid), Str(W.GetProperty("err_bad_frame"), "ck"));

        var bad = Assert.IsType<BadFrameRequest>(RelayRequests.Parse(JsonDocument.Parse(W.GetProperty("_bad_frame_input").GetRawText()).RootElement, "r"));
        Assert.Equal(Sid, bad.SessionId);
    }

    // ---- input -----------------------------------------------------------------

    private static JsonElement I => V.GetProperty("input");

    [Fact]
    public void Input_parses_and_its_replies_are_byte_for_byte()
    {
        var typed = Assert.IsType<InputRequest>(ParsePlain(I.GetProperty("typed")));
        Assert.Contains('\n', typed.Text);
        Assert.True(RelayText.Typeable(typed.Text));
        AssertBox(I.GetProperty("ok_typed"), RelayReplies.Ok(typed.Ref, RelayReplies.Typed, Sid));

        var expected = new[]
        {
            RelayReplies.Err(typed.Ref, RelayReplies.NotAtPrompt, Sid, TypingGate.WaitingWhy),
            RelayReplies.Err(typed.Ref, RelayReplies.NotAtPrompt, Sid, TypingGate.DialogWhy("Bash command")),
            RelayReplies.Err(typed.Ref, RelayReplies.NotAtPrompt, Sid, TypingGate.UnreadableWhy),
            RelayReplies.Err(typed.Ref, RelayReplies.Untypeable, Sid),
            RelayReplies.Err(typed.Ref, RelayReplies.TypingFailed, Sid, "kitty refused the send (remote control off)"),
            RelayReplies.Err(typed.Ref, RelayReplies.UnknownSession, "deadbeef"),
            RelayReplies.Err(typed.Ref, RelayReplies.BadInput, Sid),
        };
        var i = 0;
        foreach (var box in I.GetProperty("refusals").EnumerateArray()) AssertBox(box, expected[i++]);
        Assert.Equal(expected.Length, i);
    }

    public static TheoryData<string> BadInputCases()
    {
        var d = new TheoryData<string>();
        foreach (var c in I.GetProperty("bad_input_cases").EnumerateArray()) d.Add(Str(c, "name"));
        return d;
    }

    [Theory]
    [MemberData(nameof(BadInputCases))]
    public void Input_text_rule_matches_the_vector(string name)
    {
        var c = I.GetProperty("bad_input_cases").EnumerateArray().Single(x => Str(x, "name") == name);
        Assert.Equal(Str(c, "expect") == "ok", RelayText.Typeable(Str(c, "text")));
    }

    // ---- launch ------------------------------------------------------------------

    private static JsonElement L => V.GetProperty("launch");
    private static readonly byte[] PhoneIdPublic = Convert.FromHexString(Str(In.GetProperty("phone_identity"), "public"));
    private const long Mint = 1794111792;

    private static LaunchFields FieldsOf(JsonElement f) => new(
        Str(f, "cwd"), Str(f, "cli"), Str(f, "persona"), Str(f, "model"),
        f.TryGetProperty("parent_session_id", out var p) ? p.GetString()! : "",
        Str(f, "first_message"), Str(f, "nonce"), f.GetProperty("expires_at").GetInt64());

    private static LaunchGate.Environment Env(bool windows = false) =>
        new(windows, _ => true, _ => true, sid => sid == Sid);

    public static TheoryData<string> LaunchPositives()
    {
        var d = new TheoryData<string>();
        foreach (var p in L.GetProperty("positives").EnumerateArray()) d.Add(Str(p, "name"));
        return d;
    }

    [Theory]
    [MemberData(nameof(LaunchPositives))]
    public void Launch_positive_body_verifies_parses_and_passes(string name)
    {
        var p = L.GetProperty("positives").EnumerateArray().Single(x => Str(x, "name") == name);
        var fields = FieldsOf(p.GetProperty("fields"));
        Assert.Equal(Str(p, "body"), LaunchGate.Body(fields));
        Assert.Equal(Str(p, "message"), Hex(LaunchGate.Message(LaunchGate.Body(fields))));
        Assert.True(LaunchGate.Verify(PhoneIdPublic, fields, Str(p, "sig")));

        // The frame on the wire parses back to the same fields and passes the gate.
        var frame = p.GetProperty("launch_frame");
        Assert.True(Open(Key, Str(frame, "ct"), Convert.FromHexString(Str(frame, "aad")), PhoneToDesktop) is not null);
        var request = Assert.IsType<LaunchRequest>(ParsePlain(frame));
        Assert.Equal(fields, request.Fields);
        var windows = fields.Cwd.Contains(":\\");
        Assert.Null(LaunchGate.Decide(request, "ph", PhoneIdPublic, DateTimeOffset.FromUnixTimeSeconds(Mint), new NonceWindow(), Env(windows)));

        // Its replies: launched, then typed only when there was a message.
        var replies = p.GetProperty("replies").EnumerateArray().ToList();
        Assert.Equal(p.GetProperty("reply_count").GetInt32(), replies.Count);
        Assert.Equal(fields.FirstMessage.Length > 0 ? 2 : 1, replies.Count);
        var sid = JsonDocument.Parse(Str(replies[0], "plaintext")).RootElement.GetProperty("session_id").GetString()!;
        AssertBox(replies[0], RelayReplies.Ok(request.Ref, RelayReplies.Launched, sid));
        Assert.Equal(Ck(CkKey, sid), Str(replies[0], "ck"));
        if (replies.Count == 2) AssertBox(replies[1], RelayReplies.Ok(request.Ref, RelayReplies.Typed, sid));
    }

    public static TheoryData<string> SignatureNegatives()
    {
        var d = new TheoryData<string>();
        foreach (var n in L.GetProperty("negatives").EnumerateArray()) d.Add(Str(n, "name"));
        return d;
    }

    // Each negative fails where the desktop checks it: the signature does not
    // verify over the body the desktop rebuilds from the frame.
    [Theory]
    [MemberData(nameof(SignatureNegatives))]
    public void Launch_signature_negative_is_refused(string name)
    {
        var n = L.GetProperty("negatives").EnumerateArray().Single(x => Str(x, "name") == name);
        var basis = FieldsOf(L.GetProperty("positives")[1].GetProperty("fields"));
        var body = Str(n, "body");
        var sig = FromBase64Url(Str(n, "sig"));
        var verifiesOverItsBody = RelayCrypto.Verify(PhoneIdPublic, LaunchGate.Message(body), sig);
        if (verifiesOverItsBody)
        {
            // Signed over bytes the desktop never builds (a library's escaping,
            // or the right body with the wrong domain): the rebuilt body fails.
            Assert.NotEqual(LaunchGate.Body(basis), body);
            Assert.False(LaunchGate.Verify(PhoneIdPublic, basis, Str(n, "sig")));
        }
        else if (body == LaunchGate.Body(basis))
            Assert.False(LaunchGate.Verify(PhoneIdPublic, basis, Str(n, "sig")));
        // else: a body for other fields that the signature does not cover, which
        // the false above already proves.
    }

    public static TheoryData<string> RuleNegatives()
    {
        var d = new TheoryData<string>();
        foreach (var n in L.GetProperty("rule_negatives").EnumerateArray()) d.Add(Str(n, "name"));
        return d;
    }

    [Theory]
    [MemberData(nameof(RuleNegatives))]
    public void Launch_rule_negative_gives_its_code(string name)
    {
        var n = L.GetProperty("rule_negatives").EnumerateArray().Single(x => Str(x, "name") == name);
        var fields = FieldsOf(n.GetProperty("fields"));
        var now = DateTimeOffset.FromUnixTimeSeconds(n.GetProperty("now").GetInt64());
        var nonces = new NonceWindow();
        if (Str(n, "expect") == RelayReplies.Replayed) nonces.Add("ph", fields.Nonce, now);
        var code = LaunchGate.Rules(fields, "ph", now, nonces, Env());
        Assert.Equal(Str(n, "expect") == "ok" ? null : Str(n, "expect"), code);
    }

    [Fact]
    public void Launch_check_order_and_refusals_are_the_contracts()
    {
        Assert.Equal(L.GetProperty("check_order").EnumerateArray().Select(e => e.GetString()!),
            new[] { RelayReplies.BadFrame, RelayReplies.BadSignature, RelayReplies.Expired, RelayReplies.Replayed,
                RelayReplies.UnknownCli, RelayReplies.UnknownCwd, RelayReplies.BadModel, RelayReplies.UnsupportedPersona,
                RelayReplies.UnknownParent, RelayReplies.UnsupportedParent, RelayReplies.BadInput, RelayReplies.NoTerminal,
                RelayReplies.LaunchFailed, RelayReplies.NoSession });
        var reference = Str(L.GetProperty("positives")[1].GetProperty("launch_frame"), "envelope_id");
        foreach (var box in L.GetProperty("refusals").EnumerateArray())
        {
            var plain = JsonDocument.Parse(Str(box, "plaintext")).RootElement;
            var code = plain.GetProperty("code").GetString()!;
            var why = plain.TryGetProperty("why", out var w) ? w.GetString() : null;
            AssertBox(box, RelayReplies.Err(reference, code, null, why));
            Assert.Equal("", Str(box, "ck"));
        }
    }

    // ---- usage and limit -----------------------------------------------------------

    private static JsonElement U => V.GetProperty("usage");

    private static List<AccountUsage> Readings(int claudePct) =>
    [
        new("/c", "warren@uplifttech.org", true, "Max",
            new UsageWindow(claudePct, DateTimeOffset.FromUnixTimeSeconds(Mint + 5400)),
            new UsageWindow(31, DateTimeOffset.FromUnixTimeSeconds(Mint + 432000)), null,
            DateTimeOffset.FromUnixTimeSeconds(Mint), AccountUsageSource.ClaudeCode),
        new(null, "grok", true, null, null, null, null,
            DateTimeOffset.FromUnixTimeSeconds(Mint - 120), AccountUsageSource.Grok),
        new(null, "codex (default)", true, null,
            new UsageWindow(17, DateTimeOffset.FromUnixTimeSeconds(Mint + 9000)), null, null,
            DateTimeOffset.FromUnixTimeSeconds(Mint - 60), AccountUsageSource.Codex),
    ];

    [Fact]
    public void Usage_frame_is_byte_for_byte()
    {
        var accounts = RelayUsage.Order(Readings(62).Select(r => RelayUsage.From(r)));
        AssertBox(U.GetProperty("frame"), RelayUsage.Frame(accounts));
        Assert.Equal(Str(In, "ck_usage"), Str(U.GetProperty("frame"), "ck"));
    }

    [Fact]
    public void Rounding_is_the_vectors()
    {
        foreach (var r in U.GetProperty("_rounding").EnumerateArray())
            Assert.Equal(r.GetProperty("pct").GetInt32(), RelayUsage.Pct(r.GetProperty("percent").GetDouble()));
    }

    [Fact]
    public void Limit_frame_and_its_e_are_byte_for_byte()
    {
        var accounts = RelayUsage.Order(Readings(81).Select(r => RelayUsage.From(r)));
        var frame = U.GetProperty("limit_frame");
        AssertBox(frame, RelayUsage.Frame(accounts));
        Assert.Equal("limit", Str(frame, "push"));

        var e = U.GetProperty("limit_e");
        Assert.Equal(Str(e, "plaintext"), RelayUsage.LimitE(accounts[0]));
        var eph = new KeyPair(Convert.FromHexString(Str(e.GetProperty("eph"), "private")), Convert.FromHexString(Str(e.GetProperty("eph"), "public")));
        var phoneKa = Convert.FromHexString(Str(In.GetProperty("phone_agreement"), "public"));
        Assert.Equal(Str(e, "e"), PushSeal.SealWith(eph, Convert.FromHexString(Str(e, "nonce")), phoneKa, Encoding.UTF8.GetBytes(RelayUsage.LimitE(accounts[0]))));
        Assert.Equal(Str(e, "e"), Str(frame, "e"));
    }

    [Fact]
    public void Limit_rule_fires_exactly_where_the_vectors_say()
    {
        var tracker = new RelayUsageTracker();
        foreach (var r in U.GetProperty("limit_rule").GetProperty("readings").EnumerateArray())
        {
            var a = new RelayUsage.Account("a", "claude", "", r.GetProperty("pct").GetInt32(), r.GetProperty("resets_at").GetInt64(), null, null, 0, true);
            var sends = tracker.Next([a]);
            Assert.Equal(r.GetProperty("fires").GetBoolean(), sends.Any(x => x.Crossing is not null));
        }
        var fresh = new RelayUsageTracker();
        Assert.Equal(U.GetProperty("limit_rule").GetProperty("first_reading_at_82_fires").GetBoolean(),
            fresh.Next([new RelayUsage.Account("a", "claude", "", 82, 1, null, null, 0, true)]).Single().Crossing is not null);
    }

    // ---- done ------------------------------------------------------------------------

    private static JsonElement D => V.GetProperty("done");

    [Fact]
    public void Done_delta_and_e_are_byte_for_byte()
    {
        var frame = D.GetProperty("delta_frame");
        var delta = new RelayDelta(Sid, "idle", null, DateTimeOffset.FromUnixTimeSeconds(Mint + 30), false, From: "generating");
        Assert.True(delta.Finished);
        AssertBox(frame, Encoding.UTF8.GetString(RelayRoster.DeltaJson(7, delta)));
        Assert.Equal("done", Str(frame, "push"));

        var e = D.GetProperty("e");
        var plain = RelayLink.DoneE(Sid, "orbweaver", Str(In, "machine"));
        Assert.Equal(Str(e, "plaintext"), plain);
        var eph = new KeyPair(Convert.FromHexString(Str(e.GetProperty("eph"), "private")), Convert.FromHexString(Str(e.GetProperty("eph"), "public")));
        var phoneKa = Convert.FromHexString(Str(In.GetProperty("phone_agreement"), "public"));
        Assert.Equal(Str(e, "e"), PushSeal.SealWith(eph, Convert.FromHexString(Str(e, "nonce")), phoneKa, Encoding.UTF8.GetBytes(plain)));
    }

    [Fact]
    public void Done_fires_only_on_generating_to_idle()
    {
        foreach (var t in D.GetProperty("transitions").EnumerateArray())
        {
            var to = Str(t, "to");
            var d = new RelayDelta(Sid, to, null, null, to == "ended", From: Str(t, "from"));
            Assert.Equal(t.GetProperty("fires").GetBoolean(), d.Finished);
        }
    }
}

/// <summary>
/// CB-316: tools/relay-vectors' V4Vectors regenerates frames-v4-vectors.json
/// from the shipped code. With the committed signatures reused (ECDSA cannot be
/// reproduced) and the committed _source stamped back, the regeneration is the
/// committed file, byte for byte — the desktop's opinion of every vector agrees
/// with the architect's generator and the independent checker.
/// </summary>
public class V4VectorsReproduceTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay");

    [Fact]
    public void The_desktop_regenerates_the_committed_vectors_byte_for_byte()
    {
        var committedPath = Path.Combine(Dir, "frames-v4-vectors.json");
        var committed = File.ReadAllText(committedPath);
        var source = RelayV4VectorTests.V.GetProperty("_source");
        var ours = V4Vectors.Build(Dir, source.GetProperty("orbweaver_sha").GetString()!, committedPath,
            source.GetProperty("generated_by").GetString());
        if (ours != committed)
        {
            var at = 0;
            while (at < Math.Min(ours.Length, committed.Length) && ours[at] == committed[at]) at++;
            Assert.Fail($"first difference at char {at}: ours «{ours[Math.Max(0, at - 80)..Math.Min(ours.Length, at + 80)]}» committed «{committed[Math.Max(0, at - 80)..Math.Min(committed.Length, at + 80)]}»");
        }
    }

    [Fact]
    public void Without_the_committed_signatures_only_signature_bytes_differ()
    {
        var fresh = JsonDocument.Parse(V4Vectors.Build(Dir, "x")).RootElement;
        var phone = Convert.FromHexString(RelayV4VectorTests.V.GetProperty("inputs").GetProperty("phone_identity").GetProperty("public").GetString()!);
        foreach (var p in fresh.GetProperty("launch").GetProperty("positives").EnumerateArray())
            Assert.True(RelayCrypto.Verify(phone, Convert.FromHexString(p.GetProperty("message").GetString()!),
                RelayCrypto.FromBase64Url(p.GetProperty("sig").GetString())));
        Assert.Equal(RelayV4VectorTests.V.GetProperty("window").GetRawText(), fresh.GetProperty("window").GetRawText());
        Assert.Equal(RelayV4VectorTests.V.GetProperty("usage").GetRawText(), fresh.GetProperty("usage").GetRawText());
        Assert.Equal(RelayV4VectorTests.V.GetProperty("done").GetRawText(), fresh.GetProperty("done").GetRawText());
    }

    [Fact]
    public void Python_float_and_ascii_layouts()
    {
        Assert.Equal("[\n  100.0,\n  0.4,\n  -1.0,\n  {},\n  [],\n  null\n]",
            PyJson.Pretty(new System.Text.Json.Nodes.JsonArray(100.0, 0.4, -1.0, new System.Text.Json.Nodes.JsonObject(), new System.Text.Json.Nodes.JsonArray(), null)));
        Assert.Equal("{\"a\":\"\\u00e9\\u2014\\ud83d\\ude42\\u007f\\b\\f\\r\\u0001\"}".Replace("\\u007f", "\u007f"),
            PyJson.Compact(new System.Text.Json.Nodes.JsonObject { ["a"] = "é—🙂\u007f\b\f\r\u0001" }));
    }
}
