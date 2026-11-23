using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-434 (frames-v17): RelayRemote answering a phone's list_sessions and a
/// resume launch. The list is list_dir's order — rate, then shape, then the
/// page — kept to the phone's browse scope (amendment 2). A resume goes
/// through the v4 gate and LaunchRunner, with the phone's two rules on top:
/// Codex needs a first message, and already_running types nothing. Every
/// refusal sits next to its control.
/// </summary>
public class RelayResumeHandlerTests
{
    private const string Id = "0f5b1c2e-6a8d-4c11-9d3e-2b7a5f8e1c40";
    private const string Inside = "/Users/dev/Source/app";
    private const string Outside = "/Users/dev/Other";
    private static readonly RelayCrypto.KeyPair PhoneKey = RelayCrypto.GenerateKeyPair();

    private sealed class Sent : IRemoteSender
    {
        public readonly List<(string? Session, JsonElement Body)> Replies = [];
        public void Reply(string phoneId, string? sessionId, string plaintext) { lock (this) Replies.Add((sessionId, JsonDocument.Parse(plaintext).RootElement)); }
        public void Window(string phoneId, string sessionId, string plaintext) { }
        public bool ImageWindow(string phoneId, string hash, string plaintext) => false;
        public void Run(string phoneId, string sessionId, string plaintext) { }
    }

    private sealed class Rig
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
        public readonly Sent Sent = new();
        public readonly List<string> Log = [];
        public readonly List<string> Starts = [];
        public readonly FakeBrowseFileSystem Fs = new("/Users/dev", false, ["/", "/Users"], ["/System"]);
        public string Binary = "/bin/claude";
        public Action? OnDelay;
        public readonly List<string> Finds = [];
        public RelayRemote Remote;

        public List<PastSession> Sessions =
        [
            new(NewChatCli.ClaudeCode, Id, Inside, "Planted title", DateTimeOffset.FromUnixTimeSeconds(1794111000), null, "/t/a.jsonl", false),
            new(NewChatCli.ClaudeCode, "11111111-2222-3333-4444-555555555555", Outside, "Elsewhere", DateTimeOffset.FromUnixTimeSeconds(1794110000), null, "/t/b.jsonl", false),
        ];

        public Rig(bool browse = true, bool past = true, bool resume = true)
        {
            Fs.Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Source").Add(Inside).Add(Outside);
            var pastSeams = new PastSessionsRemote(new PastSessionsRemote.Seams(
                new RelayRemote.AccountSeams(_ => [new("Default", null)], _ => null),
                (_, _, _, _) => Sessions, () => new HashSet<string>(), (_, _) => null, () => Now, () => () => TimeSpan.Zero));
            var resumeSeams = new LaunchRunner.ResumeSeams(
                (cli, _, id) => { Finds.Add(id); return Sessions.FirstOrDefault(s => s.Id == id) is { } s ? s with { Cli = cli } : null; },
                (_, _) => new ResumeCheck(true, ""));

            Remote = new RelayRemote(new RelayRemote.Seams(
                () => Now,
                d => { Now += d; OnDelay?.Invoke(); return Task.CompletedTask; },
                _ => new TranscriptWatcher.Seams(() => null, _ => null, _ => null),
                new RelayInput(new RelayInput.Seams(_ => TerminalTyping.Route.None, _ => Task.FromResult<string?>(null),
                    (_, _) => Task.FromResult(false), _ => Task.CompletedTask)),
                new RelayRemote.LaunchSeams(false, _ => Binary, _ => true, () => "/bin/zsh",
                    (command, _) => { Starts.Add(command); return ("%1", null); }, (ProcessStartInfo _) => null),
                Log.Add)
            {
                Accounts = new RelayRemote.AccountSeams(_ => [new("Default", null)], _ => null),
                Browse = browse ? new RelayRemote.BrowseSeams(Fs, () => new BrowseSettings(["/Users/dev/Source"], false), TimeSpan.FromSeconds(5)) : null,
                Past = past ? pastSeams : null,
                Resume = resume ? resumeSeams : null,
            }, Sent, new NonceWindow());
        }

        public Task List(string reference, PastSessionQuery? q) =>
            Remote.HandleAsync("ph", [], new ListSessionsRequest(reference, q));

        public Task Resume(string cli = "claude", string cwd = Inside, string message = "", string id = Id)
        {
            var f = new LaunchFields(cwd, cli, "", "", "", message,
                RelayCrypto.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), Now.ToUnixTimeSeconds() + 120, "", id);
            return Remote.HandleAsync("ph", PhoneKey.Public,
                new LaunchRequest("L", f, RelayCrypto.Base64Url(RelayCrypto.Sign(PhoneKey, LaunchGate.Message(LaunchGate.Body(f))))));
        }

        public void ChecksIn(string id, SessionSource source = SessionSource.ClaudeCode) =>
            OnDelay = () => Remote.OnStatuses([(id, new SessionStatus { State = "idle", Source = source, Cwd = Inside })]);

        public string[] Codes => Sent.Replies.Select(r => r.Body.GetProperty("code").GetString()!).ToArray();
    }

    private static PastSessionQuery Q(string cli = "claude") => new(cli, "", false, "", "", 0);

    // ---- list_sessions -------------------------------------------------------------

    [Fact]
    public async Task A_list_is_answered_with_sessions_frames_kept_to_the_browse_scope_and_logs_no_title_or_folder()
    {
        var r = new Rig();
        await r.List("R1", Q());

        var (session, body) = Assert.Single(r.Sent.Replies);
        Assert.Null(session);
        Assert.Equal(("sessions", "R1", "claude"), (body.GetProperty("t").GetString(), body.GetProperty("ref").GetString(), body.GetProperty("cli").GetString()));
        Assert.Equal([Id], body.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal([Inside], body.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("list_sessions R1 1/1", Assert.Single(r.Log));
        Assert.DoesNotContain(r.Log, l => l.Contains("Planted") || l.Contains("/Users") || l.Contains(Id));
    }

    [Fact]
    public async Task With_no_browse_scope_nothing_is_in_scope()
    {
        var r = new Rig(browse: false);
        await r.List("R1", Q());

        Assert.Equal(0, r.Sent.Replies.Single().Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Rate_is_checked_before_shape()
    {
        var r = new Rig();
        for (var i = 0; i < BrowseLimits.RateBurst; i++) await r.List("R" + i, null);
        Assert.All(r.Codes, c => Assert.Equal(RelayReplies.BadFrame, c));

        await r.List("late", Q());
        Assert.Equal(RelayReplies.RateLimited, r.Codes[^1]);
    }

    [Fact]
    public async Task A_desk_with_no_index_says_unreadable_and_a_page_refusal_is_its_code()
    {
        var none = new Rig(past: false);
        await none.List("R1", Q());
        Assert.Equal([RelayReplies.Unreadable], none.Codes);

        var r = new Rig();
        await r.List("R1", Q("gemini"));
        Assert.Equal([RelayReplies.UnknownCli], r.Codes);
    }

    // ---- resume --------------------------------------------------------------------

    [Fact]
    public async Task A_claude_resume_inside_the_scope_starts_and_answers_launched_with_the_id()
    {
        var r = new Rig();
        r.ChecksIn(Id);
        await r.Resume();

        Assert.Equal([RelayReplies.Launched], r.Codes);
        Assert.Equal(Id, r.Sent.Replies[0].Session);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--resume", Id]), r.Starts.Single());
    }

    [Fact]
    public async Task A_resume_outside_the_scope_is_unknown_resume_and_the_index_is_never_read()
    {
        var r = new Rig();
        await r.Resume(cwd: Outside, id: "11111111-2222-3333-4444-555555555555");

        Assert.Equal([RelayReplies.UnknownResume], r.Codes);
        Assert.Empty(r.Finds);
        Assert.Empty(r.Starts);
    }

    [Fact]
    public async Task A_codex_resume_from_the_phone_needs_a_first_message()
    {
        var r = new Rig { Binary = "/bin/codex" };
        await r.Resume(cli: "codex");

        Assert.Equal([RelayReplies.BadInput], r.Codes);
        Assert.Empty(r.Starts);
    }

    [Fact]
    public async Task A_codex_resume_with_a_message_hands_it_to_the_cli_and_answers_launched_then_typed()
    {
        var r = new Rig { Binary = "/bin/codex" };
        r.ChecksIn(Id, SessionSource.Codex);
        await r.Resume(cli: "codex", message: "carry on");

        Assert.Equal([RelayReplies.Launched, RelayReplies.Typed], r.Codes);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/codex", ["resume", Id, "--", "carry on"]), r.Starts.Single());
    }

    // Nothing was started, so nothing is typed into somebody's live session.
    // Codex and Grok too: their message would have gone in argv, and with no
    // start there is no argv, so it reaches nothing.
    [Theory]
    [InlineData("claude", SessionSource.ClaudeCode)]
    [InlineData("codex", SessionSource.Codex)]
    [InlineData("grok", SessionSource.Grok)]
    public async Task Already_running_answers_once_and_types_nothing_even_with_a_message(string cli, SessionSource source)
    {
        var r = new Rig { Binary = "/bin/" + cli };
        r.Remote.OnStatuses([(Id, new SessionStatus { State = "idle", Source = source, Cwd = Inside })]);
        await r.Resume(cli: cli, message: "hello");

        Assert.Equal([RelayReplies.AlreadyRunning], r.Codes);
        Assert.Equal(Id, r.Sent.Replies[0].Body.GetProperty("session_id").GetString());
        Assert.Empty(r.Starts);
    }

    [Fact]
    public async Task A_desk_that_cannot_resume_says_so_with_its_sentence()
    {
        var r = new Rig(resume: false);
        await r.Resume();

        var body = r.Sent.Replies.Single().Body;
        Assert.Equal(RelayReplies.ResumeUnsupported, body.GetProperty("code").GetString());
        Assert.Equal("This machine can't resume sessions.", body.GetProperty("why").GetString());
        Assert.Empty(r.Starts);
    }

    // ---- the scope predicate -------------------------------------------------------

    [Fact]
    public void The_phone_may_see_a_folder_inside_a_root_and_a_gone_one_is_judged_by_its_path()
    {
        var fs = new FakeBrowseFileSystem("/Users/dev", false, ["/", "/Users"], ["/System"]);
        fs.Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Source").Add(Inside);
        var settings = new BrowseSettings(["/Users/dev/Source"], false);

        Assert.True(BrowseRules.PhoneMaySee(Inside, settings, fs));
        Assert.True(BrowseRules.PhoneMaySee("/Users/dev/Source/gone", settings, fs));
        Assert.False(BrowseRules.PhoneMaySee("/Users/dev/Source/../Other", settings, fs));
        Assert.False(BrowseRules.PhoneMaySee("relative/path", settings, fs));
        Assert.False(BrowseRules.PhoneMaySee(null, settings, fs));
        Assert.False(BrowseRules.PhoneMaySee("/System/Library", new BrowseSettings(["/"], false), fs));
    }
}
