using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-434: the desk's half of frames-v17 (listing and resuming past sessions),
/// replayed against the committed frames-v17-vectors.json, whose independent
/// check is Orbweaver-Mobile's check_v17_vectors.py. Every signed body is built
/// by the shipped LaunchGate, every argv by NewChatCommand.ResumeArgs, every
/// list_sessions by the shipped parser, every page by PastSessionsRemote and
/// SessionFrames over the vectors' index, every resume case by RelayRemote over
/// a model of the vectors' desk, and every sealed box is opened under the
/// epoch-0 key and written again by the desk. The phone's tables are the
/// phones' to prove and are not read.
/// </summary>
public class RelayV17VectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v17-vectors.json"))).RootElement;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static string Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly byte[] Key = Convert.FromHexString(Str(In, "session_key_epoch0"));
    private static readonly byte[] CkKey = Convert.FromHexString(Str(In, "ck_key"));
    private static readonly byte[] PhonePublic = Convert.FromHexString(Str(In, "phone_identity_public"));
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(In.GetProperty("now").GetInt64());

    // ---- limits ------------------------------------------------------------------------------

    [Fact]
    public void The_limits_table_is_the_constants_the_desk_uses()
    {
        var l = V.GetProperty("limits");
        int I(string name) => l.GetProperty(name).GetInt32();
        Assert.Equal(BrowseLimits.MaxDirPlaintextBytes, I("max_sessions_plaintext_bytes"));
        Assert.Equal(PastSessionsRemote.PageSize, I("max_rows_per_page"));
        Assert.Equal(PastSessionsRemote.MaxFolders, I("max_folders"));
        Assert.Equal(SessionFrames.MaxFoldersBytes, I("max_folders_bytes"));
        Assert.Equal(PastSessions.TitleLength, I("max_title_chars"));
        Assert.Equal(PastSessionsRemote.MaxText, I("max_query_chars"));
        Assert.Equal(PastSessionsRemote.MaxPath, I("max_path_bytes"));
        Assert.Equal(BrowseLimits.RateBurst, I("rate_burst"));
        Assert.Equal(BrowseLimits.RateIntervalMs, I("rate_interval_ms"));
        Assert.Equal(I("count_budget_ms"), (int)PastSessionsRemote.CountBudget.TotalMilliseconds);
        Assert.Equal(I("resume_window_days"), (int)PastSessions.RecentWindow.TotalDays);

        // session_id_pattern is IsSafeId's: both ends of the length, and a byte outside the set.
        Assert.Equal("^[A-Za-z0-9-]{1,128}$", Str(l, "session_id_pattern"));
        Assert.True(PastSessions.IsSafeId(new string('a', 128)));
        Assert.False(PastSessions.IsSafeId(new string('a', 129)));
        Assert.False(PastSessions.IsSafeId("a_b"));
    }

    // ---- the signed body ---------------------------------------------------------------------

    private static LaunchFields Fields(JsonElement f) => new(
        Str(f, "cwd"), Str(f, "cli"), Opt(f, "persona"), Opt(f, "model"), Opt(f, "parent_session_id"),
        Str(f, "first_message"), Str(f, "nonce"), f.GetProperty("expires_at").GetInt64(), Opt(f, "account"), Opt(f, "resume_session_id"));

    private static IEnumerable<JsonElement> Positives => V.GetProperty("signed_body").GetProperty("positives").EnumerateArray();

    [Fact]
    public void Every_positive_body_is_the_one_the_desk_rebuilds_verifies_and_parses()
    {
        var positives = Positives.ToList();
        Assert.Equal(6, positives.Count);
        foreach (var p in positives)
        {
            var name = Str(p, "name");
            var f = Fields(p.GetProperty("fields"));
            Assert.Equal(Str(p, "body"), LaunchGate.Body(f));
            Assert.Equal(Str(p, "message"), Convert.ToHexString(LaunchGate.Message(Str(p, "body"))).ToLowerInvariant());
            Assert.True(LaunchGate.Verify(PhonePublic, f, Str(p, "sig")), name);

            var parsed = Assert.IsType<LaunchRequest>(RelayRequests.Parse(JsonDocument.Parse(Str(p, "frame")).RootElement, "ref"));
            Assert.Equal(f, parsed.Fields);
            Assert.Equal(Str(p, "sig"), parsed.Sig);
        }
    }

    // A negative is a frame a desk would rebuild from the fields it carries: the
    // message is the positive's with the same nonce (its sha256 is in the body).
    [Fact]
    public void Every_negative_body_fails_against_the_body_the_desk_rebuilds()
    {
        var negatives = V.GetProperty("signed_body").GetProperty("negatives").EnumerateArray().ToList();
        Assert.Equal(6, negatives.Count);
        foreach (var n in negatives)
        {
            var name = Str(n, "name");
            var body = JsonDocument.Parse(Str(n, "body")).RootElement;
            var twin = Positives.First(p => Str(p.GetProperty("fields"), "nonce") == Str(body, "nonce"));
            var message = Str(twin.GetProperty("fields"), "first_message");
            Assert.Equal(Str(body, "first_message_sha256"),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(message))).ToLowerInvariant());

            var f = new LaunchFields(Str(body, "cwd"), Str(body, "cli"), Str(body, "persona"), Str(body, "model"),
                Str(body, "parent_session_id"), message, Str(body, "nonce"), body.GetProperty("expires_at").GetInt64(),
                Opt(body, "account"), Opt(body, "resume_session_id"));
            Assert.False(LaunchGate.Verify(PhonePublic, f, Str(n, "sig")), name);

            // The reordered ones are a good signature over the wrong bytes: the
            // desk's rebuild is what refuses them, not the signature itself.
            var sig = Base64UrlDecode(Str(n, "sig"));
            if (Str(n, "expect") == "verify_fails_against_rebuilt")
            {
                Assert.True(RelayCrypto.Verify(PhonePublic, LaunchGate.Message(Str(n, "body")), sig), name);
                Assert.NotEqual(Str(n, "body"), LaunchGate.Body(f));
            }
            else Assert.Equal("verify_fails", Str(n, "expect"));
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    // ---- argv --------------------------------------------------------------------------------

    [Fact]
    public void Every_argv_row_is_what_the_desk_starts()
    {
        var rows = V.GetProperty("argv").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(12, rows.Count);
        foreach (var r in rows)
            Assert.Equal(
                r.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!),
                NewChatCommand.ResumeArgs(LaunchGate.CliFor(Str(r, "cli")), Str(r, "id"), Str(r, "first_message"))!);
    }

    // ---- list_sessions -----------------------------------------------------------------------

    private static ListSessionsRequest ParseList(string plaintext) =>
        Assert.IsType<ListSessionsRequest>(RelayRequests.Parse(JsonDocument.Parse(plaintext).RootElement, "ref"));

    [Fact]
    public void Every_list_sessions_frame_parses_to_its_query()
    {
        var cases = V.GetProperty("list_sessions").EnumerateArray().ToDictionary(c => Str(c, "name"), c => Str(c, "plaintext"));
        Assert.Equal(new PastSessionQuery("claude", "", false, "", "", 0), ParseList(cases["minimal"]).Query);
        Assert.Equal(new PastSessionQuery("codex", "", true, "", "", 0), ParseList(cases["older"]).Query);
        Assert.Equal(new PastSessionQuery("claude", "work", true, "résumé \"feed\"", "/Users/dev/Source/project-03", 50), ParseList(cases["full"]).Query);
        Assert.Equal(new PastSessionQuery("grok", "", false, "", "", 0), ParseList(cases["from-zero-omitted"]).Query);
    }

    // bad_frame is the parser's (a null query, answered after the rate limit);
    // unknown_cli is the page's, so it parses and the desk refuses it there.
    [Fact]
    public void Every_list_sessions_refusal_is_refused_where_the_desk_refuses_it()
    {
        var refusals = V.GetProperty("list_sessions_refusals").EnumerateArray().ToList();
        Assert.Equal(9, refusals.Count);
        foreach (var c in refusals)
        {
            var q = ParseList(Str(c, "plaintext")).Query;
            switch (Str(c, "expect"))
            {
                case "bad_frame": Assert.True(q is null, Str(c, "name")); break;
                case "ok": Assert.NotNull(q); break;
                case "unknown_cli":
                    Assert.NotNull(q);
                    Assert.Equal(RelayReplies.UnknownCli, Remote().Page(q!).Code);
                    break;
                default: Assert.Fail(Str(c, "name") + ": " + Str(c, "expect")); break;
            }
        }
    }

    // ---- sessions pages ----------------------------------------------------------------------

    // The vectors' index as this desk's own: each row a session in its folder,
    // live and missing as marked, its count behind the transcript path.
    private static PastSessionsRemote Remote(IReadOnlyList<JsonElement>? index = null)
    {
        index ??= [];
        var sessions = index.Select(r => new PastSession(NewChatCli.ClaudeCode, Str(r, "id"), Str(r, "cwd"), Str(r, "title"),
            DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("at").GetInt64()), null, Str(r, "id"),
            r.TryGetProperty("missing", out var m) && m.GetBoolean())).ToList();
        var live = index.Where(r => r.TryGetProperty("live", out var l) && l.GetBoolean()).Select(r => Str(r, "id")).ToHashSet();
        var counts = index.ToDictionary(r => Str(r, "id"), r => r.TryGetProperty("n", out var n) ? n.GetInt32() : (int?)null);
        return new PastSessionsRemote(new PastSessionsRemote.Seams(
            new RelayRemote.AccountSeams(_ => [new("Default", null)], _ => null),
            (_, _, _, _) => sessions, () => live, (_, path) => counts[path], () => Now, () => () => TimeSpan.Zero));
    }

    [Fact]
    public void Every_page_is_the_frames_the_desk_writes_for_the_index()
    {
        var s = V.GetProperty("sessions");
        var index = s.GetProperty("index").EnumerateArray().ToList();
        var pages = s.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(5, pages.Count);
        foreach (var p in pages)
        {
            var name = Str(p, "name");
            var expected = p.GetProperty("frames").EnumerateArray().Select(f => f.GetString()!).ToList();
            var first = JsonDocument.Parse(expected[0]).RootElement;
            var cli = Str(first, "cli");
            var from = p.GetProperty("query_from").GetInt32();

            var page = Remote(name == "empty" ? [] : index).Page(new PastSessionQuery(cli, "", false, "", "", from));
            Assert.Equal(p.GetProperty("total").GetInt32(), page.Total);
            if (from == 0 && name != "empty")
                Assert.Equal(s.GetProperty("folders").EnumerateArray().Select(f => f.GetString()!), SessionFrames.FoldersThatFit(page.Folders));

            var frames = SessionFrames.Sessions(Str(first, "ref"), cli, page);
            Assert.True(expected.SequenceEqual(frames), name + ": " + string.Join("\n", frames.Select(f => f.Length > 300 ? f[..150] + "…" + f[^150..] : f)));
            Assert.Equal(p.GetProperty("frame_bytes").EnumerateArray().Select(b => b.GetInt32()), frames.Select(f => Encoding.UTF8.GetByteCount(f)));
        }
    }

    // ---- resume, in the contract's check order -----------------------------------------------

    private sealed class Sent : IRemoteSender
    {
        public readonly List<string> Replies = [];
        public void Reply(string phoneId, string? sessionId, string plaintext) { lock (this) Replies.Add(plaintext); }
        public void Window(string phoneId, string sessionId, string plaintext) { }
        public bool ImageWindow(string phoneId, string hash, string plaintext) => false;
        public void Run(string phoneId, string sessionId, string plaintext) { }
    }

    private static SessionSource SourceFor(string cli) => cli switch
    {
        "codex" => SessionSource.Codex,
        "grok" => SessionSource.Grok,
        _ => SessionSource.ClaudeCode,
    };

    private static readonly RelayCrypto.KeyPair TestPhone = GenerateKeyPair();

    // The vectors' model desk: its roots, accounts, live sessions, per-CLI index
    // and help probes, with every CLI installed and every folder present.
    private static List<string> Resume(JsonElement fields)
    {
        var desk = V.GetProperty("resume_rules").GetProperty("desk");
        var roots = desk.GetProperty("roots").EnumerateArray().Select(r => r.GetString()!).ToList();
        var live = desk.GetProperty("live").EnumerateArray()
            .Select(l => (Str(l, "id"), new SessionStatus { State = "idle", Source = SourceFor(Str(l, "cli")), Cwd = "/" })).ToList();
        var accounts = desk.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()!)
            .Select(a => new NewChatAccounts.Choice(a, a == RelayAccounts.Default ? null : a)).ToList();

        var fs = new FakeBrowseFileSystem("/Users/dev", false, ["/", "/Users"], ["/System"]);
        fs.Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Source").Add("/Users/dev/scratch").Add("/Users/dev/SourceX");
        foreach (var cwd in new[] { "/Users/dev/Source/Orbweaver", "/Users/dev/Source/demo-sdk", "/Users/dev/Source/elsewhere", "/Users/dev/SourceX/Orbweaver" })
            fs.Add(cwd);

        var resumeSeams = new LaunchRunner.ResumeSeams(
            (cli, _, id) =>
            {
                var name = cli switch { NewChatCli.Codex => "codex", NewChatCli.Grok => "grok", _ => "claude" };
                return desk.GetProperty("index").TryGetProperty(name, out var ids) && ids.TryGetProperty(id, out var cwd)
                    ? new PastSession(cli, id, cwd.GetString(), "", Now, null, "/t/" + id, false)
                    : null;
            },
            (cli, _) =>
            {
                var name = cli switch { NewChatCli.Codex => "codex", NewChatCli.Grok => "grok", _ => "claude" };
                var id = Str(fields, "resume_session_id");
                if (desk.GetProperty("unsupported").TryGetProperty(name + ":" + id, out var why)) return new ResumeCheck(false, why.GetString()!);
                if (desk.GetProperty("unchecked").TryGetProperty(name + ":" + id, out var unread)) return new ResumeCheck(false, unread.GetString()!, Checked: false);
                return new ResumeCheck(true, "");
            });

        var sent = new Sent();
        RelayRemote remote = null!;
        var now = Now;
        remote = new RelayRemote(new RelayRemote.Seams(
            () => now,
            d =>
            {
                now += d;
                // The resumed session checks in on the first wait after the start.
                var id = fields.GetProperty("resume_session_id").GetString()!;
                remote.OnStatuses([.. live, (id, new SessionStatus { State = "idle", Source = SourceFor(Str(fields, "cli")), Cwd = Str(fields, "cwd") })]);
                return Task.CompletedTask;
            },
            _ => new TranscriptWatcher.Seams(() => null, _ => null, _ => null),
            new RelayInput(new RelayInput.Seams(_ => TerminalTyping.Route.None, _ => Task.FromResult<string?>(null),
                (_, _) => Task.FromResult(false), _ => Task.CompletedTask)),
            new RelayRemote.LaunchSeams(false, cli => "/bin/" + cli, _ => true, () => "/bin/zsh",
                (_, _) => ("%1", null), (System.Diagnostics.ProcessStartInfo _) => null),
            _ => { })
        {
            Accounts = new RelayRemote.AccountSeams(_ => accounts, _ => null),
            Browse = new RelayRemote.BrowseSeams(fs, () => new BrowseSettings(roots, false), TimeSpan.FromSeconds(5)),
            Resume = resumeSeams,
        }, sent, new NonceWindow());
        // Every row has passed frames-v4's checks, so a parent it names is one of this desk's sessions.
        if (Opt(fields, "parent_session_id") is { Length: > 0 } parent)
            live.Add((parent, new SessionStatus { State = "idle", Source = SessionSource.ClaudeCode, Cwd = "/" }));
        remote.OnStatuses(live);

        // The frame as a phone writes it, through the shipped parser first: a
        // shape fault is answered there, before anything is signed or checked.
        var frame = new JsonObject
        {
            ["t"] = "launch", ["cwd"] = Str(fields, "cwd"), ["cli"] = Str(fields, "cli"), ["persona"] = "",
            ["model"] = Opt(fields, "model"), ["parent_session_id"] = Opt(fields, "parent_session_id"),
        };
        if (fields.TryGetProperty("account", out var account)) frame["account"] = account.GetString();
        frame["resume_session_id"] = JsonNode.Parse(fields.GetProperty("resume_session_id").GetRawText());
        frame["first_message"] = Opt(fields, "first_message");
        frame["nonce"] = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        frame["expires_at"] = Now.ToUnixTimeSeconds() + 120;
        frame["sig"] = "unsigned";

        var parsed = RelayRequests.Parse(JsonDocument.Parse(frame.ToJsonString()).RootElement, "R");
        if (parsed is BadFrameRequest) return [RelayReplies.BadFrame];
        var f = Assert.IsType<LaunchRequest>(parsed).Fields;
        remote.HandleAsync("ph", TestPhone.Public,
            new LaunchRequest("R", f, Base64Url(Sign(TestPhone, LaunchGate.Message(LaunchGate.Body(f)))))).GetAwaiter().GetResult();
        return sent.Replies.Select(r => JsonDocument.Parse(r).RootElement.GetProperty("code").GetString()!).ToList();
    }

    [Fact]
    public void Every_resume_case_is_the_first_answer_the_desk_gives()
    {
        var cases = V.GetProperty("resume_rules").GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(29, cases.Count);
        var wrong = new List<string>();
        foreach (var c in cases)
        {
            var name = Str(c, "name");
            var codes = Resume(c.GetProperty("fields"));
            var first = codes.FirstOrDefault() ?? "(none)";
            if (first != Str(c, "expect")) wrong.Add($"{name}: expected {Str(c, "expect")}, desk {string.Join(",", codes)}");
            // already_running is one reply: nothing is typed into a running session.
            if (first == RelayReplies.AlreadyRunning) Assert.Single(codes);
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // ---- the sealed boxes --------------------------------------------------------------------

    private static void AssertBox(JsonElement box, uint direction, string expectedPlaintext, string expectedCk)
    {
        Assert.Equal(expectedPlaintext, Str(box, "plaintext"));
        Assert.Equal(box.GetProperty("plaintext_bytes").GetInt32(), Encoding.UTF8.GetByteCount(expectedPlaintext));
        var aad = Convert.FromHexString(Str(box, "aad"));
        Assert.Equal(Aad(Str(box, "from"), Str(box, "to"), Str(box, "class")), aad);
        Assert.Equal(expectedPlaintext, Encoding.UTF8.GetString(Open(Key, Str(box, "ct"), aad, direction)!));
        Assert.Equal(Str(box, "ct"), Seal(Key, Convert.FromHexString(Str(box, "nonce")), Encoding.UTF8.GetBytes(expectedPlaintext), aad));
        Assert.Equal(expectedCk, Str(box, "ck"));
    }

    private static JsonElement Sealed => V.GetProperty("sealed");

    [Fact]
    public void The_caps_box_names_resume_on_the_caps_ck()
    {
        Assert.Equal(Str(In, "ck_caps"), Ck(CkKey, BrowseFrames.CapsCk));
        var box = Sealed.GetProperty("caps");
        AssertBox(box, DesktopToPhone, BrowseFrames.Caps, Str(In, "ck_caps"));
    }

    [Fact]
    public void The_list_request_opens_and_every_sessions_box_is_the_first_pages_frames()
    {
        var request = Sealed.GetProperty("list_sessions");
        AssertBox(request, PhoneToDesktop, Str(request, "plaintext"), "");
        Assert.NotNull(ParseList(Str(request, "plaintext")).Query);

        var firstPage = V.GetProperty("sessions").GetProperty("pages").EnumerateArray().First(p => Str(p, "name") == "first-page")
            .GetProperty("frames").EnumerateArray().Select(f => f.GetString()!).ToList();
        var boxes = Sealed.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(firstPage.Count, boxes.Count);
        for (var i = 0; i < boxes.Count; i++)
        {
            Assert.Equal(Str(request, "envelope_id"), Str(JsonDocument.Parse(firstPage[i]).RootElement, "ref"));
            AssertBox(boxes[i], DesktopToPhone, firstPage[i], "");
        }
    }

    [Fact]
    public void A_rate_limited_list_is_answered_with_the_desks_err()
    {
        var r = Sealed.GetProperty("rate_limited");
        AssertBox(r.GetProperty("request"), PhoneToDesktop, Str(r.GetProperty("request"), "plaintext"), "");
        AssertBox(r.GetProperty("reply"), DesktopToPhone, RelayReplies.Err(Str(r, "envelope_id"), RelayReplies.RateLimited), "");
    }

    // Each launch request is a signed positive's frame; each reply is the desk's
    // ok or err for that code, written by the desk's own builders.
    [Fact]
    public void Every_sealed_launch_is_a_signed_positive_and_its_replies_are_the_desks_bytes()
    {
        var launches = Sealed.GetProperty("launch").EnumerateObject().ToList();
        Assert.Equal(8, launches.Count);
        foreach (var (name, l) in launches.Select(p => (p.Name, p.Value)))
        {
            var body = Positives.First(p => Str(p, "name") == Str(l, "body"));
            AssertBox(l.GetProperty("request"), PhoneToDesktop, Str(body, "frame"), "");

            var reference = Str(l, "envelope_id");
            var sid = Str(body.GetProperty("fields"), "resume_session_id");
            foreach (var reply in l.GetProperty("replies").EnumerateArray())
            {
                var p = JsonDocument.Parse(Str(reply, "plaintext")).RootElement;
                var code = Str(p, "code");
                var expected = Str(p, "t") == "ok"
                    ? RelayReplies.Ok(reference, code, sid)
                    : RelayReplies.Err(reference, code, why: p.TryGetProperty("why", out var why) ? why.GetString() : null);
                AssertBox(reply, DesktopToPhone, expected, "");
                Assert.Contains(code, (string[])[RelayReplies.Launched, RelayReplies.Typed, RelayReplies.AlreadyRunning,
                    RelayReplies.UnknownResume, RelayReplies.ResumeUnsupported, RelayReplies.LaunchFailed]);
            }
            Assert.True(name.Length > 0);
        }
    }

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => [],
    };

    // frames-v11's blindness rule, the desk's half: every planted string is in a
    // sealed plaintext (the positive control), and none in a field the relay reads.
    [Fact]
    public void Every_planted_string_is_sealed_and_none_is_relay_visible()
    {
        var planted = V.GetProperty("blindness").GetProperty("planted").EnumerateArray().Select(p => p.GetString()!).ToList();
        var visible = V.GetProperty("blindness").GetProperty("relay_visible").EnumerateArray().Select(p => p.GetString()!).ToHashSet();
        var boxes = new List<JsonElement>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("ct", out _)) boxes.Add(e);
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(Sealed);
        Assert.True(boxes.Count >= 15);
        foreach (var s in planted)
        {
            Assert.Contains(boxes, b => Strings(JsonDocument.Parse(Str(b, "plaintext")).RootElement).Any(v => v.Contains(s)));
            foreach (var b in boxes)
                foreach (var f in b.EnumerateObject().Where(f => visible.Contains(f.Name) && f.Value.ValueKind == JsonValueKind.String))
                    Assert.DoesNotContain(s, f.Value.GetString()!);
        }
    }
}
