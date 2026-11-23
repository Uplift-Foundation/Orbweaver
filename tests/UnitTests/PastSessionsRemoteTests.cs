using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-434: the far desk's past-sessions page, shared by the peer link and the
/// phone — the order of its refusals, the far desk's own search and paging,
/// what never crosses (the transcript path), the count budget, and the phone's
/// browse-scope filter. Then the peer codec and server, and the phone's
/// list_sessions parser and chunked `sessions` frames.
/// </summary>
public class PastSessionsRemoteTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);

    private static PastSession S(string id, string? cwd = "/w", string title = "t", int daysAgo = 1, bool missing = false) =>
        new(NewChatCli.ClaudeCode, id, cwd, title, Now.AddDays(-daysAgo), null, "/secret/" + id + ".jsonl", missing);

    private sealed class Desk
    {
        public List<PastSession> Sessions = [S("a1"), S("b2", "/x", "Fix the feed", 2), S("c3", null, "no folder", 3)];
        public HashSet<string> Live = new(StringComparer.Ordinal) { "b2" };
        public Exception? LoadThrows;
        public readonly List<(NewChatCli Cli, string? Profile, bool Older)> Loads = [];
        public readonly List<string> Counted = [];
        public TimeSpan Elapsed;
        public List<NewChatAccounts.Choice> Choices = [new("Default", null), new("~/.claude-b", ".claude-b")];
        public string? Designated;

        public PastSessionsRemote Remote() => new(new PastSessionsRemote.Seams(
            new RelayRemote.AccountSeams(_ => Choices, _ => Designated),
            (cli, profile, older, now) =>
            {
                Loads.Add((cli, profile, older));
                if (LoadThrows is { } e) throw e;
                return Sessions;
            },
            () => Live,
            (cli, path) => { Counted.Add(path); return path.Length; },
            () => Now,
            () => () => Elapsed));
    }

    private static PastSessionQuery Q(string cli = "claude", string account = "default", bool older = false, string text = "",
        string folder = "", int from = 0, int limit = 50) => new(cli, account, older, text, folder, from, limit);

    // ---- the page ------------------------------------------------------------------

    [Fact]
    public void A_page_is_the_far_desks_own_list_newest_first_with_its_live_and_missing_marks()
    {
        var d = new Desk();
        var page = d.Remote().Page(Q());

        Assert.True(page.Ok);
        Assert.Equal(PastSessionsRemote.Listed, page.Code);
        Assert.Equal(3, page.Total);
        Assert.Equal(["a1", "b2", "c3"], page.Rows.Select(r => r.Id));
        Assert.True(page.Rows[1].Live);
        Assert.False(page.Rows[0].Live);
        // No folder cannot be resumed anywhere, so it reads as missing.
        Assert.True(page.Rows[2].Missing);
        Assert.Null(page.Rows[2].Cwd);
        Assert.Equal(Now.AddDays(-1).ToUnixTimeSeconds(), page.Rows[0].At);
        Assert.Equal(["/w", "/x"], page.Folders);
    }

    [Fact]
    public void The_transcript_path_never_crosses()
    {
        var json = JsonSerializer.Serialize(new Desk().Remote().Page(Q()).Rows, PeerProtocol.Json);
        Assert.DoesNotContain("/secret/", json);
    }

    [Fact]
    public void Search_folder_and_older_are_the_far_desks()
    {
        var d = new Desk { Sessions = [S("a1"), S("b2", "/x", "Fix the feed", 2), S("old", daysAgo: 90)] };
        var r = d.Remote();

        Assert.Equal(["b2"], r.Page(Q(text: "FEED")).Rows.Select(x => x.Id));
        Assert.Equal(["a1"], r.Page(Q(folder: "/w")).Rows.Select(x => x.Id));
        Assert.Equal(["a1", "b2", "old"], r.Page(Q(older: true)).Rows.Select(x => x.Id));
        Assert.Equal(true, d.Loads[^1].Older);
    }

    [Fact]
    public void Paging_is_by_offset_with_folders_on_the_first_page_only_and_past_the_end_is_empty()
    {
        var d = new Desk { Sessions = Enumerable.Range(0, 120).Select(i => S("s" + i.ToString("D3"), daysAgo: i)).ToList() };
        var r = d.Remote();

        var first = r.Page(Q(older: true));
        var second = r.Page(Q(older: true, from: 50));
        var beyond = r.Page(Q(older: true, from: 500));

        Assert.Equal(50, first.Rows.Count);
        Assert.Equal(120, first.Total);
        Assert.NotEmpty(first.Folders);
        Assert.Equal("s050", second.Rows[0].Id);
        Assert.Equal(50, second.From);
        Assert.Empty(second.Folders);
        Assert.True(beyond.Ok);
        Assert.Empty(beyond.Rows);
        Assert.Equal(120, beyond.Total);
    }

    [Fact]
    public void A_window_may_ask_for_2000_rows_and_gets_no_counts()
    {
        var d = new Desk { Sessions = Enumerable.Range(0, 2100).Select(i => S("s" + i, daysAgo: 0)).ToList() };

        var page = d.Remote().Page(Q(limit: 2000));

        Assert.Equal(2000, page.Rows.Count);
        Assert.Equal(2100, page.Total);
        Assert.All(page.Rows, row => Assert.Null(row.Count));
        Assert.Empty(d.Counted);
    }

    [Fact]
    public void Counts_fill_a_page_of_50_until_the_budget_is_spent()
    {
        var d = new Desk();
        var page = d.Remote().Page(Q());
        Assert.Equal("/secret/a1.jsonl".Length, page.Rows[0].Count);
        Assert.Equal(3, d.Counted.Count);

        var late = new Desk { Elapsed = PastSessionsRemote.CountBudget };
        Assert.All(late.Remote().Page(Q()).Rows, row => Assert.Null(row.Count));
        Assert.Empty(late.Counted);
    }

    [Fact]
    public void An_id_that_could_never_be_resumed_is_not_offered()
    {
        var d = new Desk { Sessions = [S("ok-1"), S("not ok")] };
        var page = d.Remote().Page(Q());
        Assert.Equal(["ok-1"], page.Rows.Select(r => r.Id));
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public void A_title_is_cleaned_and_a_folder_that_cannot_cross_is_sent_absent_and_missing()
    {
        var d = new Desk { Sessions = [S("a1", "/w\u0007", "bell\u0007 here"), S("b2", "/" + new string('é', 2100))] };

        var rows = d.Remote().Page(Q()).Rows.ToDictionary(r => r.Id);

        Assert.Equal("bell here", rows["a1"].Title);
        Assert.Null(rows["a1"].Cwd);
        Assert.True(rows["a1"].Missing);
        // 2,101 characters but 4,201 UTF-8 bytes: over the byte cap.
        Assert.Null(rows["b2"].Cwd);
        Assert.True(rows["b2"].Missing);
    }

    [Theory]
    [InlineData("gemini", "default", RelayReplies.UnknownCli)]
    [InlineData("claude", "nobody", RelayReplies.UnknownAccount)]
    public void Refusals_come_before_anything_is_read(string cli, string account, string code)
    {
        var d = new Desk();
        var page = d.Remote().Page(Q(cli, account));

        Assert.Equal(PeerPastSessionsResult.Fail(code), page with { Rows = [], Folders = [] });
        Assert.Empty(d.Loads);
    }

    [Fact]
    public void The_account_resolves_as_a_launch_does_and_empty_is_the_designated_one()
    {
        var d = new Desk { Designated = ".claude-b" };
        d.Remote().Page(Q(account: ""));
        d.Remote().Page(Q(account: "default"));

        Assert.Equal([".claude-b", null], d.Loads.Select(l => l.Profile));
    }

    [Fact]
    public void A_read_that_throws_is_unreadable()
    {
        var page = new Desk { LoadThrows = new IOException() }.Remote().Page(Q());
        Assert.Equal(RelayReplies.Unreadable, page.Code);
        Assert.False(page.Ok);
    }

    [Fact]
    public void The_phone_scope_is_applied_before_anything_is_counted_and_asked_once_per_folder()
    {
        var d = new Desk { Sessions = [S("a1"), S("a2", title: "u"), S("b2", "/x"), S("c3", null)] };
        var asked = new List<string?>();

        var page = d.Remote().Page(Q(), cwd => { asked.Add(cwd); return cwd == "/w"; });

        Assert.Equal(["a1", "a2"], page.Rows.Select(r => r.Id));
        Assert.Equal(2, page.Total);
        Assert.Equal(["/w"], page.Folders);
        Assert.Equal(["/w", "/x", null], asked.Distinct());
        Assert.Equal(1, asked.Count(a => a == "/w"));
    }

    // ---- the peer codec ------------------------------------------------------------

    [Fact]
    public void A_query_round_trips_and_defaults_its_limit_to_50()
    {
        var q = Q(text: "feed", folder: "/w", from: 50, limit: 2000);
        Assert.Equal(q, PastSessionsWire.ParseQuery(PastSessionsWire.EncodeQuery(q)));

        var bare = JsonDocument.Parse("{\"cli\":\"claude\",\"account\":\"default\"}").RootElement;
        Assert.Equal(Q(), PastSessionsWire.ParseQuery(bare));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"cli\":1,\"account\":\"default\"}")]
    [InlineData("{\"account\":\"default\"}")]
    [InlineData("{\"cli\":\"claude\"}")]
    [InlineData("{\"cli\":\"claude\",\"account\":\"default\",\"from\":-1}")]
    [InlineData("{\"cli\":\"claude\",\"account\":\"default\",\"limit\":0}")]
    [InlineData("{\"cli\":\"claude\",\"account\":\"default\",\"limit\":2001}")]
    [InlineData("{\"cli\":\"claude\",\"account\":\"default\",\"q\":\"a\\u0007\"}")]
    public void A_query_of_any_other_shape_is_null(string json) =>
        Assert.Null(PastSessionsWire.ParseQuery(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void A_query_at_the_limits_edges_parses()
    {
        Assert.Equal(1, PastSessionsWire.ParseQuery(JsonDocument.Parse("{\"cli\":\"claude\",\"account\":\"d\",\"limit\":1}").RootElement)!.Limit);
        Assert.Equal(2000, PastSessionsWire.ParseQuery(JsonDocument.Parse("{\"cli\":\"claude\",\"account\":\"d\",\"limit\":2000}").RootElement)!.Limit);
    }

    [Fact]
    public void A_page_round_trips_naming_its_cli_once_with_flags_only_when_true()
    {
        var page = new Desk().Remote().Page(Q());
        var body = PastSessionsWire.EncodePage(page, "claude");
        var json = body.GetRawText();

        Assert.Contains("\"cli\":\"claude\"", json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"live\""));
        Assert.DoesNotContain("\"missing\":false", json);

        var back = PastSessionsWire.ParsePage(body)!;
        Assert.Equal(page.Rows, back.Rows);
        Assert.Equal(page.Folders, back.Folders);
        Assert.Equal((page.Total, page.From), (back.Total, back.From));
    }

    [Fact]
    public void A_refusal_carries_its_code_and_nothing_else()
    {
        var body = PastSessionsWire.EncodePage(PeerPastSessionsResult.Fail(PeerLaunchCodes.PeerLaunchOff), "");
        Assert.DoesNotContain("rows", body.GetRawText());
        Assert.Equal(PeerPastSessionsResult.Fail(PeerLaunchCodes.PeerLaunchOff), PastSessionsWire.ParsePage(body) with { Rows = [], Folders = [] });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"total\":-1}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"from\":-1}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"rows\":[{\"id\":\"a b\",\"title\":\"t\",\"at\":1}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"rows\":[{\"id\":\"a\",\"at\":1}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"rows\":[{\"id\":\"a\",\"title\":\"t\",\"at\":-1}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"rows\":[{\"id\":\"a\",\"title\":\"t\",\"at\":1,\"n\":-1}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"rows\":[{\"id\":\"a\",\"title\":\"t\",\"at\":1,\"cwd\":\"/w\\u0007\"}]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"folders\":[\"\"]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"rows\":[null]}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"total\":\"x\"}")]
    public void A_page_of_any_other_shape_is_null(string json) =>
        Assert.Null(PastSessionsWire.ParsePage(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void A_page_over_2000_rows_or_64_folders_is_null()
    {
        string Rows(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => "{\"id\":\"a" + i + "\",\"title\":\"t\",\"at\":1}"));
        Assert.NotNull(PastSessionsWire.ParsePage(JsonDocument.Parse("{\"ok\":true,\"code\":\"ok\",\"rows\":[" + Rows(2000) + "]}").RootElement));
        Assert.Null(PastSessionsWire.ParsePage(JsonDocument.Parse("{\"ok\":true,\"code\":\"ok\",\"rows\":[" + Rows(2001) + "]}").RootElement));
        var folders = string.Join(",", Enumerable.Range(0, 65).Select(i => "\"/f" + i + "\""));
        Assert.Null(PastSessionsWire.ParsePage(JsonDocument.Parse("{\"ok\":true,\"code\":\"ok\",\"folders\":[" + folders + "]}").RootElement));
    }

    // ---- the peer server -----------------------------------------------------------

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_switch_is_checked_first_and_nothing_is_parsed_or_read_when_it_is_off()
    {
        var reads = 0;
        var server = new PeerPastSessionsServer(() => false, _ => { reads++; return PeerPastSessionsResult.Fail("x"); });

        server.Handle(Body("[]"), out var result);

        Assert.Equal(PeerLaunchCodes.PeerLaunchOff, result.Code);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void A_malformed_query_is_bad_frame_and_a_good_one_is_answered_naming_its_cli()
    {
        var d = new Desk();
        var server = new PeerPastSessionsServer(() => true, q => d.Remote().Page(q));

        server.Handle(Body("{}"), out var bad);
        Assert.Equal(RelayReplies.BadFrame, bad.Code);

        var body = server.Handle(Body("{\"cli\":\"claude\",\"account\":\"default\"}"), out var good);
        Assert.True(good.Ok);
        Assert.Equal("claude", body.GetProperty("cli").GetString());
        Assert.Equal(3, PastSessionsWire.ParsePage(body)!.Rows.Count);
    }

    // ---- the phone's list_sessions -------------------------------------------------

    private static RelayRequest? Phone(string json) => RelayRequests.Parse(JsonDocument.Parse(json).RootElement, "r");

    [Fact]
    public void The_phone_parser_reads_every_key_and_defaults_the_absent_ones()
    {
        var full = (ListSessionsRequest)Phone(
            "{\"t\":\"list_sessions\",\"cli\":\"codex\",\"account\":\".codex-b\",\"older\":true,\"q\":\"feed\",\"folder\":\"/w\",\"from\":50}")!;
        Assert.Equal(new PastSessionQuery("codex", ".codex-b", true, "feed", "/w", 50), full.Query);

        var bare = (ListSessionsRequest)Phone("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"older\":false}")!;
        Assert.Equal(new PastSessionQuery("claude", "", false, "", "", 0), bare.Query);
        Assert.Equal("r", bare.Ref);
    }

    [Theory]
    [InlineData("{\"t\":\"list_sessions\"}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"\"}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"account\":1}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"older\":\"yes\"}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"q\":2}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"q\":\"a\\u0007\"}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"folder\":3}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"folder\":\"/w\\u0001\"}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"from\":-1}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"from\":\"1\"}")]
    [InlineData("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"from\":1.5}")]
    public void A_malformed_list_sessions_reaches_the_handler_with_no_query(string json) =>
        Assert.Null(((ListSessionsRequest)Phone(json)!).Query);

    [Fact]
    public void A_q_of_257_characters_is_malformed_and_256_is_not()
    {
        Assert.NotNull(((ListSessionsRequest)Phone("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"q\":\"" + new string('a', 256) + "\"}")!).Query);
        Assert.Null(((ListSessionsRequest)Phone("{\"t\":\"list_sessions\",\"cli\":\"claude\",\"q\":\"" + new string('a', 257) + "\"}")!).Query);
    }

    private static PeerPastSession Row(int i, int titleLength = 100) =>
        new("id-" + i, "/Users/dev/Source/p" + i, new string('t', titleLength), 1794000000 + i, i % 2 == 0 ? i : null, i % 3 == 0, i % 5 == 0);

    [Fact]
    public void An_entry_is_its_keys_in_contract_order_with_flags_only_when_true()
    {
        Assert.Equal("{\"id\":\"a\",\"cwd\":\"/w\",\"title\":\"t\",\"at\":5,\"n\":3,\"missing\":true,\"live\":true}",
            SessionFrames.Entry(new PeerPastSession("a", "/w", "t", 5, 3, true, true)));
        Assert.Equal("{\"id\":\"a\",\"title\":\"t\",\"at\":5}",
            SessionFrames.Entry(new PeerPastSession("a", null, "t", 5)));
    }

    [Fact]
    public void An_empty_page_is_one_frame_with_no_entries()
    {
        var frame = SessionFrames.Sessions("r", "claude", new PeerPastSessionsResult(true, "ok", 0, 0, [], []));

        Assert.Equal(["{\"t\":\"sessions\",\"ref\":\"r\",\"cli\":\"claude\",\"total\":0,\"from\":0,\"entries_total\":0,\"from_entry\":0,\"entries\":[]}"], frame);
    }

    [Fact]
    public void A_page_too_big_for_one_frame_is_split_greedily_under_40000_bytes_with_folders_on_the_first_only()
    {
        var rows = Enumerable.Range(0, 50).Select(i => Row(i, 1200)).ToList();
        var page = new PeerPastSessionsResult(true, "ok", 137, 0, rows, ["/a", "/b"]);

        var frames = SessionFrames.Sessions("r", "claude", page);

        Assert.True(frames.Count > 1);
        var docs = frames.Select(f => JsonDocument.Parse(f).RootElement).ToList();
        Assert.All(frames, f => Assert.True(Encoding.UTF8.GetByteCount(f) <= BrowseLimits.MaxDirPlaintextBytes));
        Assert.All(docs, d => Assert.Equal((137, 0, 50), (d.GetProperty("total").GetInt32(), d.GetProperty("from").GetInt32(), d.GetProperty("entries_total").GetInt32())));
        Assert.True(docs[0].TryGetProperty("folders", out _));
        Assert.All(docs.Skip(1), d => Assert.False(d.TryGetProperty("folders", out _)));
        // from_entry is continuous and every row arrives once, in order.
        var next = 0;
        var ids = new List<string>();
        foreach (var d in docs)
        {
            Assert.Equal(next, d.GetProperty("from_entry").GetInt32());
            var entries = d.GetProperty("entries").EnumerateArray().ToList();
            next += entries.Count;
            ids.AddRange(entries.Select(e => e.GetProperty("id").GetString()!));
        }
        Assert.Equal(rows.Select(r => r.Id), ids);
    }

    [Fact]
    public void A_later_page_carries_no_folders()
    {
        var frames = SessionFrames.Sessions("r", "claude", new PeerPastSessionsResult(true, "ok", 60, 50, [Row(1)], ["/a"]));
        Assert.DoesNotContain("folders", frames.Single());
    }

    [Fact]
    public void Folders_stop_at_the_first_that_would_take_the_canonical_array_past_16000_bytes()
    {
        // Each quoted folder is 1,002 bytes; 15 of them with brackets and
        // commas are 2 + 15*1002 + 14 = 15,046, a 16th would be 16,049.
        var folders = Enumerable.Range(0, 64).Select(i => "/" + i.ToString("D2") + new string('f', 997)).ToList();

        var kept = SessionFrames.FoldersThatFit(folders);

        Assert.Equal(folders.Take(15), kept);
        var array = "[" + string.Join(",", kept.Select(CanonicalJson.Quote)) + "]";
        Assert.True(Encoding.UTF8.GetByteCount(array) <= SessionFrames.MaxFoldersBytes);
        Assert.True(Encoding.UTF8.GetByteCount(array) + 1 + 1002 > SessionFrames.MaxFoldersBytes);

        var frame = SessionFrames.Sessions("r", "claude", new PeerPastSessionsResult(true, "ok", 0, 0, [], folders)).Single();
        Assert.Equal(kept, JsonDocument.Parse(frame).RootElement.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public void Folders_stop_at_64_entries_and_an_exact_fit_is_kept()
    {
        Assert.Equal(64, SessionFrames.FoldersThatFit(Enumerable.Range(0, 80).Select(i => "/" + i).ToList()).Count);

        // One folder whose array is exactly 16,000 bytes: "[" + quoted + "]".
        var exact = "/" + new string('f', SessionFrames.MaxFoldersBytes - 5);
        Assert.Equal([exact], SessionFrames.FoldersThatFit([exact]));
        Assert.Empty(SessionFrames.FoldersThatFit([exact + "f"]));
    }
}
