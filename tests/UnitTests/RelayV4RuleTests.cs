using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-316: every arm of the v4 rules the vectors do not already reach — the
/// canonical writer's edges, request shapes that are bad_frame, the watcher's
/// debounce and diff, the input gate's refusals, the launch gate's order and
/// the usage tracker's memory — each with the control that would fail if the
/// rule were missing.
/// </summary>
public class RelayV4RuleTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    // ---- canonical form and replies --------------------------------------------

    [Fact]
    public void Canonical_writes_flags_only_when_true_and_escapes_like_the_answer_gate()
    {
        var json = new CanonicalJson().Str("a", "q\"\\\n\u0001/é🙂").Flag("c", false).Flag("d", true)
            .Num("e", -3).Array("f", ["1", "\"x\""]).ToString();
        Assert.Equal("{\"a\":\"q\\\"\\\\\\u000a\\u0001/é🙂\",\"d\":true,\"e\":-3,\"f\":[1,\"x\"]}", json);
        Assert.Equal("{}", new CanonicalJson().ToString());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(json), new CanonicalJson().Str("a", "q\"\\\n\u0001/é🙂")
            .Flag("c", false).Flag("d", true).Num("e", -3).Array("f", ["1", "\"x\""]).Utf8());
    }

    [Fact]
    public void Err_carries_why_only_on_the_four_codes_that_have_one()
    {
        Assert.Equal("""{"t":"err","ref":"r","code":"untypeable","session_id":"s"}""",
            RelayReplies.Err("r", RelayReplies.Untypeable, "s", "ignored"));
        Assert.Equal("""{"t":"err","ref":"r","code":"no_terminal","why":"tmux is not installed"}""",
            RelayReplies.Err("r", RelayReplies.NoTerminal, null, "tmux is not installed"));
        Assert.Equal("""{"t":"err","ref":"r","code":"not_at_prompt","session_id":"s"}""",
            RelayReplies.Err("r", RelayReplies.NotAtPrompt, "s"));
        Assert.Equal(4, RelayReplies.OkCodes.Length);
        // 18 from v4, plus v5's unknown_image (CB-321).
        Assert.Equal(19, RelayReplies.ErrCodes.Length);
    }

    // ---- request shapes -----------------------------------------------------------

    public static TheoryData<string, string?> BadFrames() => new()
    {
        { """{"t":"watch"}""", null },
        { """{"t":"watch","session_id":"s","before":0}""", "s" },
        { """{"t":"watch","session_id":"s","before":-4}""", "s" },
        { """{"t":"watch","session_id":"s","before":"30"}""", "s" },
        { """{"t":"watch","session_id":"s","before":1.5}""", "s" },
        { """{"t":"unwatch"}""", null },
        { """{"t":"watch","session_id":5}""", null },
        { """{"t":"input","session_id":"s"}""", "s" },
        { """{"t":"input","text":"hi"}""", null },
        { """{"t":"launch","cwd":"/a","cli":"claude","persona":"","model":"","first_message":"","nonce":"n","sig":"s"}""", null },
        { """{"t":"launch","cwd":"/a","cli":"claude","persona":"","model":"","first_message":"","nonce":"n","expires_at":"1","sig":"s"}""", null },
        { """{"t":"launch","cwd":"/a","cli":"claude","persona":"","model":"","parent_session_id":7,"first_message":"","nonce":"n","expires_at":1,"sig":"s"}""", null },
        { """{"t":"launch","cli":"claude","persona":"","model":"","first_message":"","nonce":"n","expires_at":1,"sig":"s"}""", null },
    };

    [Theory]
    [MemberData(nameof(BadFrames))]
    public void A_v4_type_with_the_wrong_shape_is_bad_frame(string frame, string? sessionId) =>
        Assert.Equal(new BadFrameRequest("r", sessionId), RelayRequests.Parse(J(frame), "r"));

    [Fact]
    public void Other_types_are_not_v4_requests_and_good_shapes_parse()
    {
        Assert.Null(RelayRequests.Parse(J("""{"t":"answer"}"""), "r"));
        Assert.Null(RelayRequests.Parse(J("""{"session_id":"s"}"""), "r"));
        Assert.Equal(new WatchRequest("r", "s", 1), RelayRequests.Parse(J("""{"t":"watch","session_id":"s","before":1}"""), "r"));
        Assert.Equal(new UnwatchRequest("r", "s"), RelayRequests.Parse(J("""{"t":"unwatch","session_id":"s"}"""), "r"));
        Assert.Equal(new InputRequest("r", "s", "hi"), RelayRequests.Parse(J("""{"t":"input","session_id":"s","text":"hi"}"""), "r"));
        var launch = Assert.IsType<LaunchRequest>(RelayRequests.Parse(J(
            """{"t":"launch","cwd":"/a","cli":"codex","persona":"","model":"m","first_message":"go","nonce":"n","expires_at":9,"sig":"s"}"""), "r"));
        Assert.Equal(new LaunchFields("/a", "codex", "", "m", "", "go", "n", 9), launch.Fields);
        Assert.Equal("s", launch.Sig);
    }

    [Fact]
    public void Text_rule_refuses_del_and_cr_and_allows_empty_only_for_a_launch()
    {
        Assert.False(RelayText.Typeable("a\u007fb"));
        Assert.False(RelayText.Typeable("a\rb"));
        Assert.False(RelayText.Typeable(""));
        Assert.True(RelayText.Typeable("", allowEmpty: true));
        Assert.True(RelayText.Typeable("a\tb\nc"));
        // C1: U+009B is an 8-bit CSI; refused with the rest of the range,
        // while the first printable character after it is not.
        Assert.False(RelayText.Typeable("ls\u009b2J"));
        Assert.False(RelayText.Typeable("\u0080"));
        Assert.False(RelayText.Typeable("\u009f"));
        Assert.True(RelayText.Typeable("\u00a0é"));
    }

    // ---- TranscriptWatch ------------------------------------------------------------

    private static TranscriptWatch.WireLine L(string text, long at = 1) => TranscriptWatch.Line("user", text, at);

    [Fact]
    public void Roles_and_turns_map_to_the_wire()
    {
        Assert.Equal(("user", "assistant", "system"),
            (TranscriptWatch.RoleOf(ChatRole.User), TranscriptWatch.RoleOf(ChatRole.Assistant), TranscriptWatch.RoleOf(ChatRole.System)));
        var turn = new ChatTurn { Role = ChatRole.System, Text = "Bash: x", At = DateTimeOffset.FromUnixTimeSeconds(5) };
        turn.IsError = true;
        Assert.Equal(new TranscriptWatch.WireLine("system", "Bash: x", 5, true, false), TranscriptWatch.From(turn));
        Assert.Equal("""{"role":"system","text":"Bash: x","at":5,"error":true}""", TranscriptWatch.LineJson(TranscriptWatch.From(turn)));
    }

    [Fact]
    public void Cut_backs_up_to_a_scalar_boundary_and_keeps_text_at_the_cap()
    {
        Assert.Equal((new string('x', 8000), false), TranscriptWatch.CutText(new string('x', 8000)));
        var (cut, was) = TranscriptWatch.CutText(new string('€', 3000));   // 3-byte scalars, 9,000 bytes
        Assert.True(was);
        Assert.Equal(7995 / 3 * 3 + 3, System.Text.Encoding.UTF8.GetByteCount(cut));
        Assert.EndsWith("\u2026", cut);
    }

    // CB-316: the 8,000-byte cap is on text, the window budget on escaped JSON;
    // a line of control characters (six bytes each, escaped) is cut further so
    // it still fits an empty window, and an ordinary line is left alone.
    [Fact]
    public void A_line_that_escapes_past_a_window_is_cut_to_fit_one()
    {
        var heavy = TranscriptWatch.Line("system", new string('\u0001', 8000), 1);
        var window = TranscriptWatch.Backfill("s", [heavy]).Single();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(window) <= TranscriptWatch.MaxWindowBytes);
        var line = J(window).GetProperty("lines")[0];
        Assert.True(line.GetProperty("cut").GetBoolean());
        Assert.EndsWith("\u2026", line.GetProperty("text").GetString());

        var plain = L("short");
        Assert.Equal(plain, TranscriptWatch.FitAlone(plain, "s", 1, false, false));

        // A surrogate pair is never split by the extra cut.
        var astral = TranscriptWatch.Line("user", string.Concat(Enumerable.Repeat("\u0001🙂", 3000)), 1);
        var fitted = TranscriptWatch.FitAlone(astral, "s", 1, false, false);
        Assert.False(char.IsHighSurrogate(fitted.Text[^2]));
    }

    // An already-cut line (CutText kept 7,997 bytes and added the ellipsis)
    // whose text is control characters escapes to about 48 KB: FitAlone drops
    // the old ellipsis, cuts further and adds one back — never two.
    [Fact]
    public void An_already_cut_escape_heavy_line_is_cut_again_to_fit_with_one_ellipsis()
    {
        var cut = TranscriptWatch.Line("system", new string('\u0001', 9000), 1);
        Assert.True(cut.Cut);
        var fitted = TranscriptWatch.FitAlone(cut, "s", 1, false, false);
        Assert.True(fitted.Cut);
        Assert.True(fitted.Text.Length < cut.Text.Length);
        Assert.Single(fitted.Text, '\u2026');
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(TranscriptWatch.Window("s", 0, 1, false, false, [TranscriptWatch.LineJson(fitted)]))
                    <= TranscriptWatch.MaxWindowBytes);
    }

    // Emoji right at the cut point: whichever side of a surrogate pair the
    // search lands on, no lone surrogate survives. Swept over the length of
    // the control-character run so the cut falls both mid-pair and between
    // pairs.
    [Fact]
    public void A_cut_landing_inside_a_surrogate_pair_backs_off_to_whole_characters()
    {
        var backedOff = 0;
        for (var run = 7960; run < 8000; run++)
        {
            var text = new string('\u0001', run) + string.Concat(Enumerable.Repeat("🙂", 200));
            var fitted = TranscriptWatch.FitAlone(new TranscriptWatch.WireLine("user", text, 1, false, false), "s", 1, false, false);
            var kept = fitted.Text[..^1];
            for (var i = 0; i < kept.Length; i++)
            {
                if (char.IsHighSurrogate(kept[i])) Assert.True(i + 1 < kept.Length && char.IsLowSurrogate(kept[i + 1]), $"lone high surrogate at {i}, run {run}");
                if (char.IsLowSurrogate(kept[i])) Assert.True(i > 0 && char.IsHighSurrogate(kept[i - 1]), $"lone low surrogate at {i}, run {run}");
            }
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(TranscriptWatch.Window("s", 0, 1, false, false, [TranscriptWatch.LineJson(fitted)]))
                        <= TranscriptWatch.MaxWindowBytes);

            // The control that the sweep reached the arm at all: one unit more —
            // the high surrogate — would still have fitted (FitAlone measures
            // with the widest from_line), so the search stopped mid-pair and
            // the back-off is what removed it.
            if (kept.Length < text.Length && char.IsHighSurrogate(text[kept.Length]))
            {
                var withHigh = fitted with { Text = text[..(kept.Length + 1)] + "\u2026" };
                if (System.Text.Encoding.UTF8.GetByteCount(TranscriptWatch.Window("s", long.MaxValue, 1, false, false, [TranscriptWatch.LineJson(withHigh)]))
                    <= TranscriptWatch.MaxWindowBytes) backedOff++;
            }
        }
        Assert.True(backedOff > 0, "the sweep never cut mid-pair");
    }

    [Fact]
    public void Diff_covers_every_arm()
    {
        List<TranscriptWatch.WireLine> two = [L("a"), L("b")];
        Assert.Equal(TranscriptWatch.Change.None, TranscriptWatch.Diff(2, L("a"), two));
        Assert.Equal(TranscriptWatch.Change.Tail, TranscriptWatch.Diff(1, L("a"), two));
        Assert.Equal(TranscriptWatch.Change.Tail, TranscriptWatch.Diff(0, null, two));
        Assert.Equal(TranscriptWatch.Change.Reset, TranscriptWatch.Diff(3, L("a"), two));
        Assert.Equal(TranscriptWatch.Change.Reset, TranscriptWatch.Diff(1, L("z"), two));
    }

    // QA's finding: a parser stamps "now" on a row with no timestamp, so the
    // same row parsed twice differs only in `at` — which must not be a reset.
    [Fact]
    public void Rows_are_compared_by_what_the_phone_draws_not_by_time()
    {
        List<TranscriptWatch.WireLine> now = [L("a", at: 99), L("b", at: 99)];
        Assert.Equal(TranscriptWatch.Change.None, TranscriptWatch.Diff(2, L("a", at: 1), now, L("b", at: 1)));
        Assert.True(TranscriptWatch.Same(L("a", 1), L("a", 2)));
        Assert.False(TranscriptWatch.Same(L("a"), TranscriptWatch.Line("assistant", "a", 1)));
        Assert.True(TranscriptWatch.Same(L("a"), TranscriptWatch.Line("user", "a", 1, error: true)));   // the contract's rule: role and text only
        Assert.False(TranscriptWatch.Same(L("a"), L(new string('a', 9000))));
    }

    // A last row that grew in place (Grok coalescing streamed chunks) is
    // restated from its own index, with or without new rows after it — never
    // a reset, which stays for a shrink or a changed first row.
    [Fact]
    public void A_last_row_that_grew_in_place_is_restated_not_reset()
    {
        Assert.Equal(TranscriptWatch.Change.Restate, TranscriptWatch.Diff(2, L("a"), [L("a"), L("bc")], L("b")));
        Assert.Equal(TranscriptWatch.Change.Restate, TranscriptWatch.Diff(2, L("a"), [L("a"), L("bc"), L("d")], L("b")));
        Assert.Equal(TranscriptWatch.Change.Tail, TranscriptWatch.Diff(2, L("a"), [L("a"), L("b"), L("d")], L("b")));
        Assert.Equal(TranscriptWatch.Change.Reset, TranscriptWatch.Diff(2, L("a"), [L("z"), L("bc")], L("b")));
    }

    [Fact]
    public void The_watcher_restates_a_grown_last_row_at_its_own_index()
    {
        var file = new File();
        file.Append("a", "b");
        var w = new TranscriptWatcher("ph", "s", file.Seams);
        w.Start();

        file.Lines![1] = L("b grew");
        file.Length++;
        w.Poll(Ms(0));
        var restated = J(w.Poll(Ms(300)).Single());
        Assert.Equal((true, 1, 2, 1, "b grew"), (restated.GetProperty("live").GetBoolean(), restated.GetProperty("from_line").GetInt32(),
            restated.GetProperty("total").GetInt32(), restated.GetProperty("lines").GetArrayLength(),
            restated.GetProperty("lines")[0].GetProperty("text").GetString()));
        Assert.False(restated.TryGetProperty("reset", out _));

        // It grows again and a new row lands in the same quiet period: one
        // window from the grown row's index carrying both.
        file.Lines[1] = L("b grew more");
        file.Append("c");
        w.Poll(Ms(400));
        var both = J(w.Poll(Ms(700)).Single());
        Assert.Equal((1, 3, 2), (both.GetProperty("from_line").GetInt32(), both.GetProperty("total").GetInt32(),
            both.GetProperty("lines").GetArrayLength()));
    }

    [Fact]
    public void Page_clamps_before_to_total_and_an_empty_run_is_one_empty_window()
    {
        List<TranscriptWatch.WireLine> three = [L("a"), L("b"), L("c")];
        var page = J(TranscriptWatch.Page("s", three, 99).Single());
        Assert.Equal((0, 3, 3), (page.GetProperty("from_line").GetInt32(), page.GetProperty("total").GetInt32(), page.GetProperty("lines").GetArrayLength()));
        var empty = TranscriptWatch.Chunk("s", 0, 0, true, []);
        Assert.Equal("""{"t":"window","session_id":"s","from_line":0,"total":0,"live":true,"lines":[]}""", empty.Single());
    }

    // ---- TranscriptWatcher ------------------------------------------------------------

    private sealed class File
    {
        public string? Path = "/t.jsonl";
        public long Length;
        public List<TranscriptWatch.WireLine>? Lines = [];
        public int Parses;

        public TranscriptWatcher.Seams Seams => new(
            () => Path,
            p => Lines is null ? null : new TranscriptWatcher.Stamp(p, Length, DateTime.UnixEpoch),
            _ => { Parses++; return Lines; });

        public void Append(params string[] texts)
        {
            foreach (var t in texts) Lines!.Add(L(t));
            Length += texts.Length;
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Ms(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void Start_with_no_transcript_answers_zero_and_the_first_rows_arrive_as_a_backfill()
    {
        var file = new File { Path = null };
        var w = new TranscriptWatcher("ph", "s", file.Seams);
        Assert.Equal(("ph", "s"), (w.PhoneId, w.SessionId));
        Assert.Equal((0, 0), (w.Start().Total, w.Start().Windows.Count));
        Assert.Empty(w.Page(5));

        file.Path = "/t.jsonl";
        file.Append("a", "b");
        Assert.Empty(w.Poll(Ms(0)));                     // stamp changed: the quiet period starts
        Assert.Empty(w.Poll(Ms(100)));                   // still inside 250 ms
        var backfill = J(w.Poll(Ms(260)).Single());
        Assert.Equal((false, 0, 2), (backfill.GetProperty("live").GetBoolean(), backfill.GetProperty("from_line").GetInt32(),
            backfill.GetProperty("lines").GetArrayLength()));
        Assert.Equal(2, w.Total);
    }

    [Fact]
    public void Appends_inside_the_debounce_become_one_tail_and_a_still_file_sends_nothing()
    {
        var file = new File();
        file.Append("a");
        var w = new TranscriptWatcher("ph", "s", file.Seams);
        var (total, windows) = w.Start();
        Assert.Equal((1, 1), (total, windows.Count));

        file.Append("b");
        Assert.Empty(w.Poll(Ms(0)));
        file.Append("c");                                // inside 250 ms of the first append
        Assert.Empty(w.Poll(Ms(200)));                   // ...restarts the quiet period
        Assert.Empty(w.Poll(Ms(400)));
        var tail = J(w.Poll(Ms(460)).Single());
        Assert.Equal((true, 1, 3, 2), (tail.GetProperty("live").GetBoolean(), tail.GetProperty("from_line").GetInt32(),
            tail.GetProperty("total").GetInt32(), tail.GetProperty("lines").GetArrayLength()));
        var parses = file.Parses;
        Assert.Empty(w.Poll(Ms(2000)));                  // nothing changed: not even parsed
        Assert.Equal(parses, file.Parses);
    }

    [Fact]
    public void A_shrunk_or_replaced_transcript_is_a_reset_and_a_vanished_one_sends_nothing()
    {
        var file = new File();
        file.Append("a", "b", "c");
        var w = new TranscriptWatcher("ph", "s", file.Seams);
        w.Start();

        file.Lines = [L("x")];
        file.Length = 99;
        w.Poll(Ms(0));
        var reset = J(w.Poll(Ms(300)).Single());
        Assert.True(reset.GetProperty("reset").GetBoolean());
        Assert.Equal(1, reset.GetProperty("total").GetInt32());

        file.Lines = [L("y")];                           // same count, different first row: replaced
        file.Length = 100;
        w.Poll(Ms(400));
        Assert.True(J(w.Poll(Ms(700)).Single()).GetProperty("reset").GetBoolean());

        file.Length = 101;
        file.Lines = [];                                  // truncated to nothing: nothing to send yet
        w.Poll(Ms(800));
        Assert.Empty(w.Poll(Ms(1100)));

        file.Lines = null;                                // gone
        w.Poll(Ms(1200));
        Assert.Empty(w.Poll(Ms(1500)));
    }

    [Fact]
    public void A_page_of_an_empty_transcript_sends_no_window()
    {
        var file = new File();
        var w = new TranscriptWatcher("ph", "s", file.Seams);
        Assert.Empty(w.Page(5));
        file.Append("a");
        Assert.Single(w.Page(5));
    }

    [Fact]
    public void Same_stamp_but_no_new_rows_sends_nothing()
    {
        var file = new File();
        file.Append("a");
        var w = new TranscriptWatcher("ph", "s", file.Seams);
        w.Start();
        file.Length = 5;                                  // the file grew, the parser found no new row
        w.Poll(Ms(0));
        Assert.Empty(w.Poll(Ms(300)));
    }

    // ---- usage -----------------------------------------------------------------------

    [Fact]
    public void An_account_is_projected_with_its_email_and_without_an_unpaired_window()
    {
        var r = new AccountUsage("/c", "Work", true, "Pro",
            new UsageWindow(12.4, null), new UsageWindow(50, DateTimeOffset.FromUnixTimeSeconds(9)), null,
            DateTimeOffset.FromUnixTimeSeconds(1), AccountUsageSource.ClaudeCode, ObservedAt: DateTimeOffset.FromUnixTimeSeconds(2));
        var a = RelayUsage.From(r, "me@example.com");
        Assert.Equal(new RelayUsage.Account("me@example.com", "claude", "pro", null, null, 50, 9, 2, true), a);
        Assert.Equal("""{"name":"me@example.com","cli":"claude","plan":"pro","weekly_pct":50,"weekly_resets_at":9,"as_of":2}""",
            RelayUsage.AccountJson(a));
        Assert.Equal("Work", RelayUsage.From(r, " ").Name);
        Assert.Equal(("codex", "grok"), (RelayUsage.CliOf(AccountUsageSource.Codex), RelayUsage.CliOf(AccountUsageSource.Grok)));
    }

    // A percent with no reset time (or the reverse) is half a pair: omitted,
    // and to the limit rule it is no reading.
    [Fact]
    public void Half_a_window_pair_is_omitted_and_is_no_reading()
    {
        var half = new RelayUsage.Account("a", "claude", "", 90, null, 40, null, 1, true);
        Assert.Equal("""{"name":"a","cli":"claude","plan":"","as_of":1}""", RelayUsage.AccountJson(half));
        var t = new RelayUsageTracker();
        Assert.Null(t.Next([half]).Single().Crossing);
        Assert.NotNull(t.Next([half with { ResetsAt = 100 }]).Single().Crossing);   // the first real reading at 90 fires
    }

    // An account that drops out of the readings is forgotten, so its next
    // reading over 80 is a crossing again.
    [Fact]
    public void An_account_that_disappears_is_forgotten_and_re_arms()
    {
        var t = new RelayUsageTracker();
        Assert.NotNull(t.Next([A("a", 85)]).Single().Crossing);
        Assert.Null(t.Next([A("a", 85)]).SingleOrDefault()?.Crossing);
        t.Next([A("b", 10)]);
        Assert.NotNull(t.Next([A("a", 85), A("b", 10)]).Single().Crossing);
    }

    private static RelayUsage.Account A(string name, int? pct, long resets = 100, bool available = true) =>
        new(name, "claude", "", pct, pct is null ? null : resets, null, null, 1, available);

    [Fact]
    public void The_tracker_sends_on_change_only_and_remembers_the_latest()
    {
        var t = new RelayUsageTracker();
        Assert.Null(t.Latest);
        Assert.Single(t.Next([A("a", 10)]));
        Assert.NotNull(t.Latest);
        Assert.Empty(t.Next([A("a", 10) with { AsOf = 99 }]));    // as_of alone is not a change
        Assert.Single(t.Next([A("a", 11)]));
        Assert.Single(t.Next([A("a", 11, available: false)]));    // availability is
        Assert.Single(t.Next([A("a", 11, available: false), A("b", null)]));   // an account arriving is
    }

    [Fact]
    public void Each_crossing_account_gets_its_own_frame_and_an_unread_window_re_arms()
    {
        var t = new RelayUsageTracker();
        t.Next([A("a", 50), A("b", 50)]);
        var sends = t.Next([A("a", 85), A("b", 90)]);
        Assert.Equal(["a", "b"], sends.Select(s => s.Crossing!.Name));
        Assert.All(sends, s => Assert.Equal(sends[0].Plaintext, s.Plaintext));

        t.Next([A("a", null), A("b", 90)]);               // a's window unreported: forgotten
        Assert.Equal(["a"], t.Next([A("a", 85), A("b", 90)]).Select(s => s.Crossing!.Name));
        t.Next([A("b", 90)]);                              // a gone entirely: forgotten too
        Assert.Equal(["a"], t.Next([A("a", 85), A("b", 90)]).Select(s => s.Crossing!.Name));
    }

    // ---- launch gate ------------------------------------------------------------------

    private static readonly RelayCrypto.KeyPair Phone = RelayCrypto.GenerateKeyPair();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);

    private static LaunchRequest Signed(LaunchFields f, RelayCrypto.KeyPair? signer = null) =>
        new("r", f, RelayCrypto.Base64Url(RelayCrypto.Sign(signer ?? Phone, LaunchGate.Message(LaunchGate.Body(f)))));

    private static LaunchFields F(string cli = "claude", string cwd = "/w", string model = "", string persona = "",
        string parent = "", string message = "hi", string? nonce = null) =>
        new(cwd, cli, persona, model, parent, message, nonce ?? RelayCrypto.Base64Url(new byte[16]), Now.ToUnixTimeSeconds() + 120);

    private static LaunchGate.Environment Env(bool windows = false, bool installed = true, bool exists = true) =>
        new(windows, _ => installed, _ => exists, sid => sid == "lead");

    private static string? Decide(LaunchRequest r, LaunchGate.Environment? env = null, NonceWindow? nonces = null) =>
        LaunchGate.Decide(r, "ph", Phone.Public, Now, nonces ?? new NonceWindow(), env ?? Env());

    [Fact]
    public void A_signed_launch_passes_and_each_rule_refuses_in_order()
    {
        Assert.Null(Decide(Signed(F())));
        Assert.Equal(RelayReplies.BadSignature, Decide(Signed(F(), RelayCrypto.GenerateKeyPair())));
        Assert.Equal(RelayReplies.BadSignature, Decide(Signed(F(nonce: RelayCrypto.Base64Url(new byte[15])))));
        Assert.Equal(RelayReplies.BadSignature, Decide(Signed(F(nonce: "not base64url!"))));   // signed, but no nonce at all
        Assert.Equal(RelayReplies.UnknownCli, Decide(Signed(F()), Env(installed: false)));
        Assert.Equal(RelayReplies.UnknownCwd, Decide(Signed(F()), Env(exists: false)));
        Assert.Equal(RelayReplies.UnknownCwd, Decide(Signed(F()), Env(windows: true)));
        Assert.Null(Decide(Signed(F(cwd: "D:\\code")), Env(windows: true)));
        Assert.Equal(RelayReplies.UnknownParent, Decide(Signed(F(parent: "gone"))));
        Assert.Null(Decide(Signed(F(parent: "lead"))));
        Assert.Equal(RelayReplies.UnsupportedParent, Decide(Signed(F(cli: "codex", parent: "lead"))));
        Assert.Equal(RelayReplies.BadInput, Decide(Signed(F(message: "\u001b[A"))));
        Assert.Null(Decide(Signed(F(message: ""))));

        // The nonce is recorded once signature and expiry pass, even when a
        // later rule refuses — so a resend of that refused launch is a replay.
        var nonces = new NonceWindow();
        var refused = Signed(F(persona: "Pixel"));
        Assert.Equal(RelayReplies.UnsupportedPersona, Decide(refused, nonces: nonces));
        Assert.Equal(RelayReplies.Replayed, Decide(refused, nonces: nonces));
    }

    // CB-316 (PM): conhost expands %NAME% in a Windows Codex/Grok first
    // message, so the expandable shape is bad_input, defined or not; anything
    // else with a % passes. Claude (typed, not argv) and a Mac desk are not
    // subject to it.
    [Theory]
    [InlineData("%PATH%", "codex", true, "bad_input")]
    [InlineData("say %X% now", "grok", true, "bad_input")]
    [InlineData("%_a9%", "codex", true, "bad_input")]
    [InlineData("20%", "codex", true, null)]
    [InlineData("100% sure", "codex", true, null)]
    [InlineData("%", "grok", true, null)]
    [InlineData("% %", "grok", true, null)]
    [InlineData("50% done %", "codex", true, null)]
    [InlineData("%9X%", "codex", true, null)]
    [InlineData("%PATH%", "codex", false, null)]
    [InlineData("%PATH%", "claude", true, null)]
    public void Expandable_percent_names_are_refused_only_where_conhost_would_expand_them(string message, string cli, bool windows, string? expect)
    {
        var f = F(cli: cli, cwd: windows ? "C:\\w" : "/w", message: message);
        Assert.Equal(expect, LaunchGate.Rules(f, "ph", Now, new NonceWindow(), Env(windows: windows)));
    }

    [Fact]
    public void Model_cli_and_path_rules()
    {
        Assert.True(LaunchGate.ModelOk(""));
        Assert.True(LaunchGate.ModelOk("claude-opus-4.1:latest"));
        Assert.False(LaunchGate.ModelOk("-rf"));
        Assert.False(LaunchGate.ModelOk(new string('m', 65)));
        Assert.True(LaunchGate.IsAbsolute("/x", false));
        Assert.False(LaunchGate.IsAbsolute("C:\\x", false));
        Assert.False(LaunchGate.IsAbsolute("C:x", true));
        Assert.Equal((NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok),
            (LaunchGate.CliFor("claude"), LaunchGate.CliFor("codex"), LaunchGate.CliFor("grok")));
    }

    [Fact]
    public void Nonce_try_add_is_seen_then_add_in_one_step()
    {
        var n = new NonceWindow();
        Assert.True(n.TryAdd("ph", "a", Now));
        Assert.False(n.TryAdd("ph", "a", Now));
        Assert.True(n.TryAdd("ph2", "a", Now));
        Assert.True(n.TryAdd("ph", "a", Now + NonceWindow.Lifetime));   // aged out
        Assert.Equal(1, n.Count);                                           // the prune took both old entries
    }

    // ---- launch command lines --------------------------------------------------------

    [Fact]
    public void Launch_args_are_each_clis_own_spelling()
    {
        Assert.Empty(NewChatCommand.LaunchArgs(NewChatCli.ClaudeCode, "", ""));
        Assert.Equal(["--model", "opus", "--parent-session-id", "lead"], NewChatCommand.LaunchArgs(NewChatCli.ClaudeCode, "opus", "lead"));
        Assert.Equal(["-m", "gpt-5"], NewChatCommand.LaunchArgs(NewChatCli.Codex, "gpt-5", "lead"));
        Assert.Equal(["-m", "grok-4"], NewChatCommand.LaunchArgs(NewChatCli.Grok, "grok-4", ""));
    }

    // CB-316: Codex and Grok get the first message as their positional prompt,
    // behind `--` so a leading "-" or a subcommand name is never a flag; Claude
    // still has it typed.
    [Fact]
    public void Codex_and_grok_take_the_first_message_as_a_prompt_behind_a_double_dash()
    {
        Assert.Equal(["-m", "gpt-5", "--", "-n exec rm"], NewChatCommand.LaunchArgs(NewChatCli.Codex, "gpt-5", "", "-n exec rm"));
        Assert.Equal(["--", "hi"], NewChatCommand.LaunchArgs(NewChatCli.Grok, "", "", "hi"));
        Assert.Empty(NewChatCommand.LaunchArgs(NewChatCli.Codex, "", "", ""));
        Assert.Equal(["--model", "opus"], NewChatCommand.LaunchArgs(NewChatCli.ClaudeCode, "opus", "", "hi"));
        Assert.True(NewChatCommand.PromptAsArgument(NewChatCli.Grok));
        Assert.False(NewChatCommand.PromptAsArgument(NewChatCli.ClaudeCode));
    }

    // The message reaches the CLI's argv word for word, with no cmd.exe to
    // re-parse & | > " % (PM: pin it with exactly those).
    [Fact]
    public void A_windows_prompt_launch_is_conhost_and_the_cli_with_no_cmd()
    {
        const string message = "a & b | c > d \" e %PATH% ^ f";
        var start = NewChatCommand.ConsoleStartInfo("C:\\bin\\codex.exe", "C:\\w",
            NewChatCommand.LaunchArgs(NewChatCli.Codex, "", "", message), viaCmd: false);
        Assert.Equal("conhost.exe", start.FileName);
        Assert.Equal(["C:\\bin\\codex.exe", "--", message], start.ArgumentList);
        Assert.DoesNotContain("cmd.exe", start.ArgumentList);
        Assert.DoesNotContain("/k", start.ArgumentList);
    }

    [Fact]
    public void The_target_must_be_the_sessions_own_pane_and_codex_needs_a_process()
    {
        var codexNoPid = new SessionStatus { Source = SessionSource.Codex, SessionPid = 0 };
        var codex = new SessionStatus { Source = SessionSource.Codex, SessionPid = 7, TmuxPane = "%1" };
        var claudeNoPid = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 0 };
        Assert.False(RelayInput.TargetVerified(codexNoPid, TerminalTyping.Route.WindowsConsole, _ => true));
        Assert.True(RelayInput.TargetVerified(codex, TerminalTyping.Route.Tmux, _ => true));
        Assert.False(RelayInput.TargetVerified(codex, TerminalTyping.Route.Tmux, _ => false));
        Assert.False(RelayInput.TargetVerified(codex, TerminalTyping.Route.Tmux, _ => null));   // cannot tell: fail closed
        Assert.True(RelayInput.TargetVerified(codex, TerminalTyping.Route.Kitty, _ => false));  // the pane rule is tmux's
        Assert.True(RelayInput.TargetVerified(claudeNoPid, TerminalTyping.Route.WindowsConsole, _ => null));
    }

    [Fact]
    public void A_pane_holds_a_pid_only_in_its_own_process_tree()
    {
        List<ProcessCommand> table = [new(10, 1, "zsh"), new(11, 10, "codex"), new(20, 1, "zsh"), new(21, 20, "codex")];
        Assert.True(TmuxPaneOwnershipRules.Holds(table, 10, 11));
        Assert.False(TmuxPaneOwnershipRules.Holds(table, 10, 21));
        Assert.True(TmuxPaneOwnershipRules.Holds(table, 21, 21));
        Assert.Null(TmuxPaneOwnershipRules.Holds(null, 10, 11));
        Assert.Null(TmuxPaneOwnershipRules.Holds(table, 0, 11));
        Assert.Null(TmuxPaneOwnershipRules.Holds(table, 10, 0));
    }

    [Fact]
    public void Launch_line_runs_the_login_shell_with_every_word_quoted()
    {
        Assert.Equal("'/bin/zsh' -lc 'exec '\\''/opt/claude'\\'' '\\''--model'\\'' '\\''opus'\\'''",
            NewChatCommand.LaunchLine("/bin/zsh", "/opt/claude", ["--model", "opus"]));
    }

    [Fact]
    public void Detached_tmux_creates_the_orbweaver_session_once_then_adds_windows()
    {
        Assert.Equal(["new-session", "-d", "-s", "orbweaver", "-c", "/w", "-P", "-F", "#{pane_id}", "cd -- '/w' || exit 1; cmd"],
            TerminalScripts.TmuxDetachedArgs(false, "/w", "cmd"));
        Assert.Equal(["new-window", "-t", "orbweaver:", "-c", "/w", "-P", "-F", "#{pane_id}", "cd -- '/w' || exit 1; cmd"],
            TerminalScripts.TmuxDetachedArgs(true, "/w", "cmd"));
    }

    // conhost.exe named outright: a bare cmd.exe is handed to Windows Terminal
    // by the default-terminal setting and owns no console window, which the
    // hook reads as "no terminal" (see ConsoleStartInfo).
    [Fact]
    public void The_windows_launch_is_conhost_with_the_clis_arguments()
    {
        var start = NewChatCommand.ConsoleStartInfo("C:\\bin\\claude.exe", "C:\\w",
            NewChatCommand.LaunchArgs(NewChatCli.ClaudeCode, "opus", ""));
        Assert.Equal("conhost.exe", start.FileName);
        Assert.Equal(["cmd.exe", "/k", "C:\\bin\\claude.exe", "--model", "opus"], start.ArgumentList);
        Assert.Equal("C:\\w", start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
    }
}

/// <summary>
/// CB-316: Grok Build never turns bracketed paste on, so the markers were typed
/// into its message (QA, Windows: "[200~Reply with exactly…[201~"). Every
/// route that brackets leaves them off for Grok and only for Grok; Claude and
/// Codex keep CB-304's bracketing, which their Return depends on.
/// </summary>
public class GrokPasteMarkerTests
{
    private static SessionStatus S(SessionSource source) =>
        new() { Source = source, TermId = "7", Tty = "/dev/ttys001" };

    [Fact]
    public void Only_grok_is_typed_without_paste_markers()
    {
        Assert.False(TerminalTyping.Brackets(S(SessionSource.Grok)));
        Assert.True(TerminalTyping.Brackets(S(SessionSource.ClaudeCode)));
        Assert.True(TerminalTyping.Brackets(S(SessionSource.Codex)));
        Assert.True(TerminalTyping.Brackets(null));

        Assert.Equal("hi\r", TerminalFocuser.KittySend(S(SessionSource.Grok), "hi").Stdin);
        Assert.Equal("hi\r", TerminalFocuser.WezTermSend(S(SessionSource.Grok), "hi").Stdin);
        Assert.Equal(TerminalTyping.PasteStart + "hi" + TerminalTyping.PasteEnd + "\r", TerminalFocuser.KittySend(S(SessionSource.Codex), "hi").Stdin);
        Assert.DoesNotContain(TerminalFocuser.ITerm2Args(S(SessionSource.Grok), "hi"), a => a.Contains(TerminalTyping.PasteStart));
        Assert.Contains(TerminalFocuser.ITerm2Args(S(SessionSource.ClaudeCode), "hi"), a => a.Contains(TerminalTyping.PasteStart));
        Assert.DoesNotContain(TerminalFocuser.TerminalAppArgs(S(SessionSource.Grok), "hi"), a => a.Contains(TerminalTyping.PasteStart));
    }

    [Fact]
    public void The_windows_console_types_grok_bare_with_its_newline_as_lf_and_one_return()
    {
        var grok = TerminalFocuser.ConsoleRecords("a\nb", bracketed: false);
        Assert.Equal("a\nb\r", new string(grok.Where(r => r.KeyDown == 1).Select(r => r.Char).ToArray()));
        Assert.DoesNotContain(grok, r => r.Char == '\u001b');

        var claude = TerminalFocuser.ConsoleRecords("a");
        Assert.Equal(TerminalTyping.PasteStart + "a" + TerminalTyping.PasteEnd + "\r",
            new string(claude.Where(r => r.KeyDown == 1).Select(r => r.Char).ToArray()));
    }
}

/// <summary>
/// CB-316: the arms of PaneScreen's Grok and Codex shapes that the real
/// fixtures never land on — a box with no top or no bottom, a Windows-style
/// cancel footer with no spinner, a numbered Codex option, a mark at the very
/// bottom of the screen. Small synthetic screens, each beside the fixture
/// shape it differs from by one line.
/// </summary>
public class PaneScreenShapeTests
{
    private const string Top = "╭────────────────────╮";
    private const string Mark = "│ ❯                  │";
    private const string Bottom = "╰──────────── Grok ──╯";

    private static (PaneReading, string) R(params string[] lines) => PaneScreen.Read(string.Join("\n", lines) + "\n");

    [Fact]
    public void A_grok_box_reads_as_a_prompt_only_with_its_top_and_bottom()
    {
        Assert.Equal((PaneReading.Prompt, "grok-prompt"), R("ok", Top, Mark, Bottom));
        Assert.Equal((PaneReading.Prompt, "grok-prompt"), R("ok", Top, "", Mark, Bottom));    // a blank between: skipped
        Assert.NotEqual("grok-prompt", R(Mark, Bottom).Item2);                                // nothing above the mark
        Assert.NotEqual("grok-prompt", R("", "", "", Mark, Bottom).Item2);                    // only blanks above
        Assert.NotEqual("grok-prompt", R("text", Mark, Bottom).Item2);                        // above, but not the box top
        Assert.NotEqual("grok-prompt", R(Top, Mark).Item2);                                   // no bottom: the screen ends
        Assert.NotEqual("grok-prompt", R(Top, Mark, "a", "b", "c", Bottom).Item2);            // bottom too far below
    }

    [Fact]
    public void A_windows_cancel_footer_alone_is_working_and_the_mark_shapes_are_exact()
    {
        Assert.Equal((PaneReading.Unknown, "working"), R(Top, Mark, Bottom, "Shift+Tab:mode  │  Ctrl+c:cancel"));
        Assert.Equal((PaneReading.Prompt, "grok-prompt"), R(Top, "│ > draft text │", Bottom));
        Assert.NotEqual("grok-prompt", R(Top, "│ ❯x                 │", Bottom).Item2);
        Assert.NotEqual("grok-prompt", R(Top, "│ x                  │", Bottom).Item2);
        Assert.Equal((PaneReading.Unknown, "working"), R("- Thinking… 1.0s", Top, Mark, Bottom));
        Assert.Equal((PaneReading.Prompt, "grok-prompt"), R("- a bullet", Top, Mark, Bottom));   // no ellipsis: not a spinner
    }

    [Fact]
    public void A_codex_mark_needs_its_footer_and_is_never_a_numbered_option()
    {
        Assert.Equal((PaneReading.Prompt, "codex-prompt"), R("› Ask Codex to do anything", "", "  ? for shortcuts"));
        Assert.Equal((PaneReading.Prompt, "codex-prompt"), R("›", "? for shortcuts"));
        Assert.NotEqual("codex-prompt", R("› 1. Yes, proceed (y)", "? for shortcuts").Item2);
        Assert.NotEqual("codex-prompt", R("› Ask Codex to do anything").Item2);                 // the screen ends: no footer
        Assert.NotEqual("codex-prompt", R("› Ask", "a", "b", "c", "d", "? for shortcuts").Item2); // footer too far below
    }
}
