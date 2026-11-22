using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-339: RelayRemote answering a list_dir. The contract's order is rate, then shape, then the
/// path rules, and one reply per request: the listing's dir frames or exactly one err. Every
/// refusal has its control beside it.
/// </summary>
public class RelayBrowseHandlerTests
{
    private sealed class Sent : IRemoteSender
    {
        public readonly List<(string Phone, string? Session, JsonElement Body)> Replies = [];
        public void Reply(string phoneId, string? sessionId, string plaintext) { lock (this) Replies.Add((phoneId, sessionId, JsonDocument.Parse(plaintext).RootElement)); }
        public void Window(string phoneId, string sessionId, string plaintext) { }
        public bool ImageWindow(string phoneId, string hash, string plaintext) => false;
        public void Run(string phoneId, string sessionId, string plaintext) { }
    }

    private sealed class Harness
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
        public readonly Sent Sent = new();
        public readonly List<string> Log = [];
        public readonly FakeBrowseFileSystem Fs = new("/Users/dev", false, ["/", "/Users"], ["/System"]);
        public BrowseSettings Settings = new(["/Users/dev/Source"], false);
        public RelayRemote Remote;

        public Harness(bool browse = true, TimeSpan? timeout = null)
        {
            Fs.Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Source").Add("/Users/dev/Source/app")
                .Add("/Users/dev/Source/.hidden").Add("/Users/dev/Other");
            var seams = new RelayRemote.Seams(
                () => Now, _ => Task.CompletedTask,
                _ => new TranscriptWatcher.Seams(() => null, _ => null, _ => null),
                new RelayInput(new RelayInput.Seams(_ => TerminalTyping.Route.None, _ => Task.FromResult<string?>(null),
                    (_, _) => Task.FromResult(false), _ => Task.CompletedTask)),
                new RelayRemote.LaunchSeams(false, _ => null, _ => false, () => "/bin/zsh", (_, _) => (null, null), _ => null),
                Log.Add)
            {
                Browse = browse ? new RelayRemote.BrowseSeams(Fs, () => Settings, timeout ?? TimeSpan.FromSeconds(5)) : null,
            };
            Remote = new RelayRemote(seams, Sent, new NonceWindow());
        }

        public Task Ask(string reference, string? path, bool hidden = false, string phone = "ph") =>
            Remote.HandleAsync(phone, [], new ListDirRequest(reference, path, hidden));

        public string Code(int i) => Sent.Replies[i].Body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_listing_is_answered_with_its_dir_frame_on_an_empty_ck_and_the_log_names_no_folder()
    {
        var h = new Harness();
        await h.Ask("R1", "/Users/dev/Source");

        var (phone, session, body) = Assert.Single(h.Sent.Replies);
        Assert.Equal(("ph", null), (phone, session));
        Assert.Equal(("dir", "R1", "/Users/dev/Source"), (body.GetProperty("t").GetString(), body.GetProperty("ref").GetString(), body.GetProperty("path").GetString()));
        Assert.Equal(["app"], body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.Equal("list_dir R1 1 entries", Assert.Single(h.Log));
        Assert.DoesNotContain(h.Log, l => l.Contains("Source") || l.Contains("app"));
    }

    [Fact]
    public async Task A_child_that_is_not_privacy_protected_keeps_its_badge_on_the_phone()
    {
        var h = new Harness();
        h.Fs.Add("/Users/dev/Source/app/.git");
        await h.Ask("R1", "/Users/dev/Source");

        var entry = h.Sent.Replies[0].Body.GetProperty("entries").EnumerateArray().Single();
        Assert.True(entry.GetProperty("git").GetBoolean());
    }

    [Fact]
    public async Task Hidden_folders_come_only_when_the_request_asked_for_them()
    {
        var h = new Harness();
        await h.Ask("R1", "/Users/dev/Source", hidden: true);
        Assert.Equal([".hidden", "app"], h.Sent.Replies[0].Body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_refusal_is_exactly_one_err_with_the_requests_ref_and_nothing_below_it_was_read()
    {
        var h = new Harness();
        await h.Ask("R1", "/Users/dev/Other");                 // outside the roots, and it exists
        await h.Ask("R2", "/Users/dev/nope");                  // outside the roots, and it does not
        await h.Ask("R3", "/Users/dev/Source/nope");           // inside, and gone

        Assert.Equal(["forbidden_path", "forbidden_path", "unknown_path"], new[] { h.Code(0), h.Code(1), h.Code(2) });
        Assert.All(h.Sent.Replies, r => Assert.Equal("err", r.Body.GetProperty("t").GetString()));
        Assert.Equal(["R1", "R2", "R3"], h.Sent.Replies.Select(r => r.Body.GetProperty("ref").GetString()));
        Assert.Empty(h.Fs.Enumerated);                          // no refusal ever read a folder
        await h.Ask("R4", "/Users/dev/Source");
        Assert.Equal("dir", h.Sent.Replies[3].Body.GetProperty("t").GetString());   // the control
    }

    [Fact]
    public async Task Rate_is_checked_before_shape_so_a_flood_of_malformed_frames_costs_one_counter()
    {
        var h = new Harness();
        for (var i = 0; i < BrowseLimits.RateBurst; i++) await h.Ask("R" + i, null);      // malformed, each accepted by the bucket
        Assert.All(h.Sent.Replies, r => Assert.Equal("bad_frame", r.Body.GetProperty("code").GetString()));

        await h.Ask("late", null);                                                        // the ninth: rate_limited, not bad_frame
        Assert.Equal("rate_limited", h.Code(BrowseLimits.RateBurst));
        await h.Ask("later", "/Users/dev/Source");                                        // and a well-formed one is refused the same
        Assert.Equal("rate_limited", h.Code(BrowseLimits.RateBurst + 1));
        Assert.Empty(h.Fs.Enumerated);

        h.Now += TimeSpan.FromMilliseconds(BrowseLimits.RateIntervalMs);
        await h.Ask("after", "/Users/dev/Source");                                        // one interval later it is served
        Assert.Equal("dir", h.Sent.Replies[^1].Body.GetProperty("t").GetString());
    }

    [Fact]
    public async Task Each_phone_has_its_own_bucket()
    {
        var h = new Harness();
        for (var i = 0; i < BrowseLimits.RateBurst; i++) await h.Ask("a" + i, "", phone: "A");
        await h.Ask("a-over", "", phone: "A");
        await h.Ask("b0", "", phone: "B");
        Assert.Equal(("A", "rate_limited"), (h.Sent.Replies[BrowseLimits.RateBurst].Phone, h.Code(BrowseLimits.RateBurst)));
        Assert.Equal(("B", "dir"), (h.Sent.Replies[^1].Phone, h.Sent.Replies[^1].Body.GetProperty("t").GetString()));
    }

    [Fact]
    public async Task A_desk_with_no_browse_seam_says_forbidden_path_as_a_desk_with_nothing_to_share_would()
    {
        var h = new Harness(browse: false);
        await h.Ask("R1", "/Users/dev/Source");
        Assert.Equal("forbidden_path", h.Code(0));
    }

    [Fact]
    public async Task A_read_that_throws_or_does_not_finish_in_time_is_unreadable_and_the_log_names_the_exception_not_the_folder()
    {
        var h = new Harness();
        h.Fs.OnRead = _ => new IOException("secret-folder-name");
        await h.Ask("R1", "/Users/dev/Source");
        Assert.Equal("unreadable", h.Code(0));
        Assert.Contains(h.Log, l => l == "list_dir R1 IOException");
        Assert.DoesNotContain(h.Log, l => l.Contains("secret"));

        var slow = new Harness(timeout: TimeSpan.FromMilliseconds(50));
        var gate = new ManualResetEventSlim();
        slow.Fs.OnRead = _ => { gate.Wait(TimeSpan.FromSeconds(5)); return null; };
        await slow.Ask("R2", "/Users/dev/Source");
        gate.Set();
        Assert.Equal("unreadable", slow.Code(0));
        Assert.Contains(slow.Log, l => l == "list_dir R2 TimeoutException");
    }

    [Fact]
    public async Task A_listing_longer_than_one_frame_is_sent_as_every_frame_in_order_under_one_ref()
    {
        var h = new Harness();
        for (var i = 0; i < 300; i++) h.Fs.Add($"/Users/dev/Source/app/{new string('n', 200)}{i:D3}");
        await h.Ask("R1", "/Users/dev/Source/app");

        Assert.True(h.Sent.Replies.Count >= 2);
        Assert.All(h.Sent.Replies, r => Assert.Equal("R1", r.Body.GetProperty("ref").GetString()));
        Assert.Equal(h.Sent.Replies.Select(r => r.Body.GetProperty("from_entry").GetInt32()).Order(), h.Sent.Replies.Select(r => r.Body.GetProperty("from_entry").GetInt32()));
        Assert.Equal(300, h.Sent.Replies.Sum(r => r.Body.GetProperty("entries").GetArrayLength()));
    }
}
