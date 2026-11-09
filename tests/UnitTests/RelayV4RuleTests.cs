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
        var json = new CanonicalJson().Str("a", "q\"\\\n\u0001/é🙂").Bool("b", false).Flag("c", false).Flag("d", true)
            .Num("e", -3).Array("f", ["1", "\"x\""]).ToString();
        Assert.Equal("{\"a\":\"q\\\"\\\\\\u000a\\u0001/é🙂\",\"b\":false,\"d\":true,\"e\":-3,\"f\":[1,\"x\"]}", json);
        Assert.Equal("{}", new CanonicalJson().ToString());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(json), new CanonicalJson().Str("a", "q\"\\\n\u0001/é🙂").Bool("b", false)
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
        Assert.Equal(18, RelayReplies.ErrCodes.Length);
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

    [Fact]
    public void The_windows_launch_is_conhost_with_the_clis_arguments()
    {
        var start = NewChatCommand.GeneralWindowsStartInfo("C:\\bin\\claude.exe", "C:\\w",
            NewChatCommand.LaunchArgs(NewChatCli.ClaudeCode, "opus", ""), useWindowsTerminal: false)!;
        Assert.Equal("cmd.exe", start.FileName);
        Assert.Equal(["/k", "C:\\bin\\claude.exe", "--model", "opus"], start.ArgumentList);
        Assert.Equal("C:\\w", start.WorkingDirectory);
    }
}
