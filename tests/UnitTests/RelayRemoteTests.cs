using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-316: RelayInput and RelayRemote against a fake desk — screens, routes,
/// typers, a fake clock, tmux and conhost stand-ins. Every refusal proves
/// nothing was typed or started; every positive has the negative beside it.
/// </summary>
public class RelayRemoteTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private static readonly string Prompt = PaneFixtures.MacPromptAfterEsc;
    private static readonly string Dialog = PaneFixtures.MiniExitDialog;

    private static SessionStatus Status(string state = "idle", SessionSource source = SessionSource.ClaudeCode, string cwd = "/w") =>
        new() { State = state, Source = source, Cwd = cwd };

    // ---- RelayInput ------------------------------------------------------------

    private sealed class Desk
    {
        public TerminalTyping.Route Route = TerminalTyping.Route.Tmux;
        public Queue<string?> Screens = new();
        public string? Screen = Prompt;
        public bool TypeResult = true;
        public readonly List<string> Typed = [];
        public readonly List<TimeSpan> Delays = [];
        public Func<TimeSpan, Task>? OnDelay;

        public RelayInput Input() => new(new RelayInput.Seams(
            _ => Route,
            _ => Task.FromResult(Screens.Count > 0 ? Screens.Dequeue() : Screen),
            (_, text) => { if (TypeResult) Typed.Add(text); return Task.FromResult(TypeResult); },
            d => { Delays.Add(d); return OnDelay?.Invoke(d) ?? Task.CompletedTask; }));
    }

    [Fact]
    public async Task A_settled_prompt_is_typed_after_two_identical_reads_150ms_apart()
    {
        var desk = new Desk();
        var outcome = await desk.Input().TypeAsync(Status(), "rerun\nthe tests");
        Assert.True(outcome.Typed);
        Assert.Equal(["rerun\nthe tests"], desk.Typed);
        Assert.Equal([PromptPipeline.SettleCheck], desk.Delays);
    }

    [Theory]
    [InlineData("TerminalApp")]
    [InlineData("None")]
    public async Task Terminal_app_and_no_route_are_untypeable_without_reading_the_screen(string route)
    {
        var desk = new Desk { Route = Enum.Parse<TerminalTyping.Route>(route), Screen = null };
        Assert.Equal(RelayReplies.Untypeable, (await desk.Input().TypeAsync(Status(), "hi")).Code);
        Assert.Empty(desk.Typed);
        Assert.Empty(desk.Delays);
    }

    [Fact]
    public async Task Each_not_at_prompt_names_why_and_types_nothing()
    {
        async Task<RelayInput.Outcome> Run(Desk d, string state = "idle") => await d.Input().TypeAsync(Status(state), "hi");

        var waiting = new Desk();
        Assert.Equal((RelayReplies.NotAtPrompt, TypingGate.WaitingWhy), ((await Run(waiting, "waiting")).Code, (await Run(waiting, "waiting")).Why));

        var dialog = new Desk { Screen = Dialog };
        var o = await Run(dialog);
        Assert.Equal(RelayReplies.NotAtPrompt, o.Code);
        Assert.StartsWith("it is showing a dialog: ", o.Why);

        var unread = new Desk { Screen = null };
        Assert.Equal(TypingGate.UnreadableWhy, (await Run(unread)).Why);

        var changing = new Desk { Screens = new([Prompt, Prompt + " "]) };
        Assert.Equal(RelayInput.ChangingWhy, (await Run(changing)).Why);

        var vanished = new Desk { Screens = new([Prompt, null]) };
        Assert.Equal(RelayInput.ChangingWhy, (await Run(vanished)).Why);

        Assert.All(new[] { waiting, dialog, unread, changing, vanished }, d => Assert.Empty(d.Typed));
    }

    [Fact]
    public async Task Bad_input_is_refused_first_an_empty_launch_message_types_nothing_and_a_refusing_typer_is_typing_failed()
    {
        var desk = new Desk();
        Assert.Equal(RelayReplies.BadInput, (await desk.Input().TypeAsync(Status(), "\u001b")).Code);
        Assert.True((await desk.Input().TypeAsync(Status(), "", allowEmpty: true)).Typed);
        Assert.Empty(desk.Typed);

        var refusing = new Desk { TypeResult = false };
        var outcome = await refusing.Input().TypeAsync(Status(), "hi");
        Assert.Equal((RelayReplies.TypingFailed, RelayInput.RefusedWhy), (outcome.Code, outcome.Why));
    }

    [Fact]
    public async Task Two_typings_never_interleave()
    {
        var gate = new TaskCompletionSource();
        var desk = new Desk { OnDelay = _ => gate.Task };
        var input = desk.Input();
        var first = input.TypeAsync(Status(), "one");
        var second = input.TypeAsync(Status(), "two");
        await Task.Delay(50);
        Assert.Single(desk.Delays);                       // the second is waiting for the first, not reading
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(["one", "two"], desk.Typed);
    }

    // ---- RelayRemote -----------------------------------------------------------

    private sealed class Sent : IRemoteSender
    {
        public readonly List<(string Phone, string? Session, JsonElement Body)> Replies = [];
        public readonly List<(string Phone, string Session, JsonElement Body)> Windows = [];
        public void Reply(string phoneId, string? sessionId, string plaintext) { lock (this) Replies.Add((phoneId, sessionId, J(plaintext))); }
        public void Window(string phoneId, string sessionId, string plaintext) { lock (this) Windows.Add((phoneId, sessionId, J(plaintext))); }
    }

    private sealed class World
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
        public readonly Desk Desk = new();
        public readonly Sent Sent = new();
        public readonly List<string> Log = [];
        public readonly Dictionary<string, List<TranscriptWatch.WireLine>?> Files = new();
        public bool Windows;
        public string? Binary = "/bin/claude";
        public bool DirExists = true;
        public (string? Pane, string? Why) MacPlace = ("%1", null);
        public int? WindowsPid = 42;
        public readonly List<(string Command, string Cwd)> MacStarts = [];
        public readonly List<ProcessStartInfo> WindowsStarts = [];
        public Action? OnLaunchDelay;
        public readonly NonceWindow Nonces = new();
        public RelayRemote Remote = null!;

        public World(bool windows = false, string? binary = "/bin/claude", (string?, string?)? place = null, int? pid = 42)
        {
            Windows = windows;
            Binary = binary;
            MacPlace = place ?? ("%1", null);
            WindowsPid = pid;
            Remote = new RelayRemote(new RelayRemote.Seams(
                () => Now,
                d => { Now += d; OnLaunchDelay?.Invoke(); return Task.CompletedTask; },
                s => new TranscriptWatcher.Seams(
                    () => s.TranscriptPath.Length == 0 ? null : s.TranscriptPath,
                    p => Files.TryGetValue(p, out var l) && l is not null ? new TranscriptWatcher.Stamp(p, l.Count, DateTime.UnixEpoch) : null,
                    p => Files.TryGetValue(p, out var l) ? l : null),
                Desk.Input(),
                new RelayRemote.LaunchSeams(
                    Windows,
                    _ => Binary,
                    _ => DirExists,
                    () => "/bin/zsh",
                    (command, cwd) => { MacStarts.Add((command, cwd)); return MacPlace; },
                    psi => { WindowsStarts.Add(psi); return WindowsPid; }),
                Log.Add), Sent, Nonces);
        }

        public void Sessions(params (string Id, SessionStatus Status)[] rows) => Remote.OnStatuses(rows);
    }

    private static SessionStatus Watched(string path) => new() { Source = SessionSource.ClaudeCode, TranscriptPath = path, Cwd = "/w" };

    private static List<TranscriptWatch.WireLine> Lines(int n) =>
        Enumerable.Range(0, n).Select(i => TranscriptWatch.Line("user", "line " + i, i)).ToList();

    [Fact]
    public async Task Watch_replies_watching_then_backfills_and_unwatch_stops_the_stream()
    {
        var w = new World();
        w.Files["/t"] = Lines(3);
        w.Sessions(("s1", Watched("/t")));

        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", "s1", null));
        var (phone, session, ok) = w.Sent.Replies.Single();
        Assert.Equal(("ph", "s1", "watching", 3), (phone, session, ok.GetProperty("code").GetString(), ok.GetProperty("total").GetInt32()));
        Assert.Equal(3, w.Sent.Windows.Single().Body.GetProperty("lines").GetArrayLength());
        Assert.True(w.Remote.IsWatching("ph", "s1"));

        w.Files["/t"] = Lines(5);
        w.Remote.Tick(w.Now);
        w.Remote.Tick(w.Now.AddMilliseconds(300));
        var tail = w.Sent.Windows.Last().Body;
        Assert.Equal((true, 3), (tail.GetProperty("live").GetBoolean(), tail.GetProperty("from_line").GetInt32()));

        await w.Remote.HandleAsync("ph", [], new UnwatchRequest("r2", "s1"));
        Assert.Equal("unwatched", w.Sent.Replies.Last().Body.GetProperty("code").GetString());
        Assert.Equal(0, w.Remote.WatchCount);
        w.Files["/t"] = Lines(9);
        w.Remote.Tick(w.Now.AddSeconds(1));
        w.Remote.Tick(w.Now.AddSeconds(2));
        Assert.Equal(2, w.Sent.Windows.Count);              // nothing after unwatch
    }

    [Fact]
    public async Task A_page_sends_windows_and_changes_no_subscription()
    {
        var w = new World();
        w.Files["/t"] = Lines(10);
        w.Sessions(("s1", Watched("/t")));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r", "s1", 4));
        Assert.Empty(w.Sent.Replies);
        Assert.Equal((0, 4), (w.Sent.Windows.Single().Body.GetProperty("from_line").GetInt32(), w.Sent.Windows.Single().Body.GetProperty("lines").GetArrayLength()));
        Assert.Equal(0, w.Remote.WatchCount);
    }

    [Fact]
    public async Task Unknown_and_non_local_sessions_are_unknown_session_with_an_empty_ck()
    {
        var w = new World();
        w.Sessions(("oc", new SessionStatus { Source = SessionSource.OpenClaw }));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r", "oc", null));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r", "nope", 3));
        await w.Remote.HandleAsync("ph", [], new UnwatchRequest("r", "nope"));
        await w.Remote.HandleAsync("ph", [], new InputRequest("r", "nope", "hi"));
        Assert.Equal(4, w.Sent.Replies.Count);
        Assert.All(w.Sent.Replies, r => Assert.Equal(("unknown_session", (string?)null), (r.Body.GetProperty("code").GetString(), r.Session)));
        Assert.Empty(w.Desk.Typed);
    }

    [Fact]
    public async Task A_bad_frame_names_a_known_session_in_its_ck_only()
    {
        var w = new World();
        w.Sessions(("s1", Watched("/t")));
        await w.Remote.HandleAsync("ph", [], new BadFrameRequest("r", "s1"));
        await w.Remote.HandleAsync("ph", [], new BadFrameRequest("r", null));
        Assert.Equal(["s1", null], w.Sent.Replies.Select(r => r.Session));
        Assert.All(w.Sent.Replies, r => Assert.Equal("bad_frame", r.Body.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Watches_die_with_the_phone_the_socket_and_the_session()
    {
        var w = new World();
        w.Files["/t"] = Lines(1);
        w.Sessions(("s1", Watched("/t")), ("s2", Watched("/t")));
        await w.Remote.HandleAsync("a", [], new WatchRequest("r", "s1", null));
        await w.Remote.HandleAsync("b", [], new WatchRequest("r", "s1", null));
        await w.Remote.HandleAsync("b", [], new WatchRequest("r", "s2", null));
        Assert.Equal(3, w.Remote.WatchCount);

        w.Remote.PhoneOffline("a");
        Assert.Equal(2, w.Remote.WatchCount);
        w.Sessions(("s2", Watched("/t")));                 // s1 left the roster
        Assert.Equal(1, w.Remote.WatchCount);
        Assert.True(w.Remote.IsWatching("b", "s2"));
        w.Remote.Disconnected();
        Assert.Equal(0, w.Remote.WatchCount);
    }

    [Fact]
    public async Task A_path_the_hook_names_later_is_picked_up_without_re_watching()
    {
        var w = new World();
        w.Sessions(("s1", Watched("")));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r", "s1", null));
        Assert.Equal(0, w.Sent.Replies.Single().Body.GetProperty("total").GetInt32());
        Assert.Empty(w.Sent.Windows);

        w.Files["/later"] = Lines(2);
        w.Sessions(("s1", Watched("/later")));
        w.Remote.Tick(w.Now);
        w.Remote.Tick(w.Now.AddMilliseconds(300));
        Assert.False(w.Sent.Windows.Single().Body.GetProperty("live").GetBoolean());   // a backfill, not a tail
    }

    [Fact]
    public async Task A_watcher_that_throws_is_logged_and_the_others_carry_on()
    {
        var w = new World();
        var fail = false;
        w.Files["/t"] = Lines(1);
        var flaky = new RelayRemote(new RelayRemote.Seams(() => w.Now, _ => Task.CompletedTask,
            s => s.TranscriptPath == "/x"
                ? new TranscriptWatcher.Seams(() => "/x", p => fail ? throw new IOException("gone") : new TranscriptWatcher.Stamp(p, 0, DateTime.UnixEpoch), _ => [])
                : new TranscriptWatcher.Seams(() => "/t", p => new TranscriptWatcher.Stamp(p, w.Files["/t"]!.Count, DateTime.UnixEpoch), _ => w.Files["/t"]),
            w.Desk.Input(), new RelayRemote.LaunchSeams(false, _ => null, _ => true, () => "", (_, _) => (null, null), _ => null),
            w.Log.Add), w.Sent, w.Nonces);
        flaky.OnStatuses([("bad", Watched("/x")), ("good", Watched("/t"))]);
        await flaky.HandleAsync("ph", [], new WatchRequest("r", "bad", null));
        await flaky.HandleAsync("ph", [], new WatchRequest("r", "good", null));
        var before = w.Sent.Windows.Count;

        fail = true;
        w.Files["/t"] = Lines(2);
        flaky.Tick(w.Now);
        flaky.Tick(w.Now.AddMilliseconds(300));
        Assert.Contains(w.Log, l => l == "watch bad: IOException");
        Assert.Equal(before + 1, w.Sent.Windows.Count);         // the good watch still sent its tail
    }

    [Fact]
    public async Task The_poll_loop_ticks_until_cancelled()
    {
        var ticks = 0;
        using var stop = new CancellationTokenSource();
        var remote = new RelayRemote(new RelayRemote.Seams(
            () => { if (++ticks == 3) stop.Cancel(); return DateTimeOffset.UnixEpoch; },
            _ => Task.Delay(1),
            _ => throw new InvalidOperationException(),
            new Desk().Input(),
            new RelayRemote.LaunchSeams(false, _ => null, _ => true, () => "", (_, _) => (null, null), _ => null),
            _ => { }), new Sent(), new NonceWindow());
        await remote.RunAsync(stop.Token);
        Assert.Equal(3, ticks);

        var never = new RelayRemote(new RelayRemote.Seams(() => DateTimeOffset.UnixEpoch, _ => Task.Delay(Timeout.Infinite),
            _ => throw new InvalidOperationException(), new Desk().Input(),
            new RelayRemote.LaunchSeams(false, _ => null, _ => true, () => "", (_, _) => (null, null), _ => null), _ => { }),
            new Sent(), new NonceWindow());
        using var soon = new CancellationTokenSource(50);
        await never.RunAsync(soon.Token);                   // a cancelled wait returns, it does not throw
    }

    [Fact]
    public async Task Input_types_through_the_gate_and_refusals_carry_their_why()
    {
        var w = new World();
        w.Sessions(("s1", Status()), ("s2", Status("waiting")));
        await w.Remote.HandleAsync("ph", [], new InputRequest("r1", "s1", "go"));
        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", "s2", "go"));
        Assert.Equal(["go"], w.Desk.Typed);
        Assert.Equal("typed", w.Sent.Replies[0].Body.GetProperty("code").GetString());
        Assert.Equal(("not_at_prompt", TypingGate.WaitingWhy),
            (w.Sent.Replies[1].Body.GetProperty("code").GetString(), w.Sent.Replies[1].Body.GetProperty("why").GetString()));
        Assert.Equal("s2", w.Sent.Replies[1].Session);
    }

    // ---- launch -----------------------------------------------------------------

    private static readonly RelayCrypto.KeyPair PhoneKey = RelayCrypto.GenerateKeyPair();

    private static LaunchRequest Launch(World w, string cli = "claude", string message = "first", string cwd = "/w",
        string model = "", string parent = "", string persona = "")
    {
        var f = new LaunchFields(cwd, cli, persona, model, parent, message,
            RelayCrypto.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), w.Now.ToUnixTimeSeconds() + 120);
        return new LaunchRequest("L", f, RelayCrypto.Base64Url(RelayCrypto.Sign(PhoneKey, LaunchGate.Message(LaunchGate.Body(f)))));
    }

    private static string[] Codes(World w) => w.Sent.Replies.Select(r => r.Body.GetProperty("code").GetString()!).ToArray();

    // The new session checks in after the first poll.
    private static void ChecksIn(World w, string id, SessionStatus status, int afterPolls = 1)
    {
        var polls = 0;
        w.OnLaunchDelay = () => { if (++polls == afterPolls) w.Sessions(("old", Status()), (id, status)); };
    }

    [Fact]
    public async Task A_mac_launch_starts_in_tmux_checks_in_and_types_its_first_message()
    {
        var w = new World();
        w.Sessions(("old", Status()), ("lead", Status()));
        ChecksIn(w, "new", Status(cwd: "/w/"));
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w, model: "opus", parent: "lead"));

        Assert.Equal(["launched", "typed"], Codes(w));
        Assert.All(w.Sent.Replies, r => Assert.Equal(("new", "L"), (r.Session, r.Body.GetProperty("ref").GetString())));
        var (command, cwd) = w.MacStarts.Single();
        Assert.Equal("/w", cwd);
        Assert.Equal(NewChatCommand.LaunchLine("/bin/zsh", "/bin/claude", ["--model", "opus", "--parent-session-id", "lead"]), command);
        Assert.Equal(["first"], w.Desk.Typed);
        Assert.Empty(w.WindowsStarts);
    }

    [Fact]
    public async Task A_launch_with_no_message_gets_exactly_one_reply()
    {
        var w = new World();
        ChecksIn(w, "new", Status());
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w, message: ""));
        Assert.Equal(["launched"], Codes(w));
        Assert.Empty(w.Desk.Typed);
    }

    [Fact]
    public async Task A_windows_launch_is_conhost_and_matches_the_folder_without_case()
    {
        var w = new World(windows: true);
        ChecksIn(w, "new", Status(source: SessionSource.Codex, cwd: "c:\\Work"));
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w, cli: "codex", cwd: "C:\\Work", model: "gpt-5", message: ""));
        Assert.Equal(["launched"], Codes(w));
        var start = w.WindowsStarts.Single();
        Assert.Equal("conhost.exe", start.FileName);
        Assert.Equal(["cmd.exe", "/k", "/bin/claude", "-m", "gpt-5"], start.ArgumentList);
        Assert.Empty(w.MacStarts);
    }

    [Fact]
    public async Task A_refused_launch_starts_nothing()
    {
        var w = new World();
        var request = Launch(w, persona: "Pixel");
        await w.Remote.HandleAsync("ph", PhoneKey.Public, request);
        await w.Remote.HandleAsync("ph", PhoneKey.Public, request);
        Assert.Equal(["unsupported_persona", "replayed"], Codes(w));
        Assert.All(w.Sent.Replies, r => Assert.Null(r.Session));
        Assert.Empty(w.MacStarts);

        var noCli = new World(binary: null);
        await noCli.Remote.HandleAsync("ph", PhoneKey.Public, Launch(noCli));
        Assert.Equal(["unknown_cli"], Codes(noCli));
    }

    public static TheoryData<string?, string, string> MacFailures() => new()
    {
        { TerminalScripts.NoTmuxWhy, "no_terminal", TerminalScripts.NoTmuxWhy },
        { "tmux could not start a session", "launch_failed", "tmux could not start a session" },
        { null, "launch_failed", "Couldn't open a terminal for Claude Code." },
    };

    [Theory]
    [MemberData(nameof(MacFailures))]
    public async Task A_mac_launch_that_cannot_place_says_why(string? why, string code, string expectedWhy)
    {
        var w = new World(place: (null, why));
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w));
        var reply = w.Sent.Replies.Single().Body;
        Assert.Equal((code, expectedWhy), (reply.GetProperty("code").GetString(), reply.GetProperty("why").GetString()));
    }

    [Fact]
    public async Task A_windows_console_that_will_not_start_is_launch_failed()
    {
        var w = new World(windows: true, pid: null);
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w, cwd: "C:\\w"));
        Assert.Equal("Couldn't open a console for Claude Code.", w.Sent.Replies.Single().Body.GetProperty("why").GetString());
    }

    [Fact]
    public async Task A_session_that_never_checks_in_is_no_session_after_30_seconds()
    {
        var w = new World();
        // The wrong CLI, and the right CLI in another folder, do not count.
        w.Sessions(("old", Status()));
        w.OnLaunchDelay = () => w.Sessions(("old", Status()), ("codex", Status(source: SessionSource.Codex)), ("elsewhere", Status(cwd: "/other")));
        var start = w.Now;
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w));
        Assert.Equal(["no_session"], Codes(w));
        Assert.Equal(RelayRemote.LaunchCheckIn, w.Now - start);
    }

    [Fact]
    public async Task A_first_message_held_by_a_dialog_is_typed_once_it_clears()
    {
        var w = new World();
        ChecksIn(w, "new", Status());
        w.Desk.Screens = new([Dialog, Dialog, Prompt, Prompt]);
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w));
        Assert.Equal(["launched", "typed"], Codes(w));
    }

    [Fact]
    public async Task A_first_message_still_held_after_60_seconds_is_not_at_prompt()
    {
        var w = new World();
        ChecksIn(w, "new", Status());
        w.Desk.Screen = Dialog;
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w));
        Assert.Equal(["launched", "not_at_prompt"], Codes(w));
        Assert.StartsWith("it is showing a dialog: ", w.Sent.Replies[1].Body.GetProperty("why").GetString());
        Assert.Empty(w.Desk.Typed);
    }

    [Fact]
    public async Task A_first_message_into_something_untypeable_or_refusing_stops_at_once()
    {
        var untypeable = new World();
        ChecksIn(untypeable, "new", Status());
        untypeable.Desk.Route = TerminalTyping.Route.TerminalApp;
        await untypeable.Remote.HandleAsync("ph", PhoneKey.Public, Launch(untypeable));
        Assert.Equal(RelayRemote.UntypeableWhy, untypeable.Sent.Replies[1].Body.GetProperty("why").GetString());

        var refusing = new World();
        ChecksIn(refusing, "new", Status());
        refusing.Desk.TypeResult = false;
        await refusing.Remote.HandleAsync("ph", PhoneKey.Public, Launch(refusing));
        Assert.Equal(("not_at_prompt", RelayInput.RefusedWhy),
            (refusing.Sent.Replies[1].Body.GetProperty("code").GetString(), refusing.Sent.Replies[1].Body.GetProperty("why").GetString()));
    }

    [Fact]
    public async Task A_new_session_that_ends_before_its_message_is_not_at_prompt()
    {
        var w = new World();
        var polls = 0;
        w.OnLaunchDelay = () =>
        {
            if (++polls == 1) w.Sessions(("new", Status()));
            else w.Sessions();
        };
        w.Desk.Screen = Dialog;
        await w.Remote.HandleAsync("ph", PhoneKey.Public, Launch(w));
        Assert.Equal(["launched", "not_at_prompt"], Codes(w));
        Assert.Equal("the session ended", w.Sent.Replies[1].Body.GetProperty("why").GetString());
    }

    [Fact]
    public void Same_folder_rule()
    {
        Assert.True(RelayRemote.SameFolder("/w/", "/w", false));
        Assert.False(RelayRemote.SameFolder("/W", "/w", false));
        Assert.True(RelayRemote.SameFolder("C:\\W\\", "c:\\w", true));
        Assert.True(RelayRemote.SameFolder("/", "/", false));
        Assert.False(RelayRemote.SameFolder(null, "/w", false));
        Assert.False(RelayRemote.SameFolder("", "/w", false));
    }
}
