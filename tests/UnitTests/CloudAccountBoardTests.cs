using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// Covers reading every Claude Code account at once: one source per account, one
// merged and deduplicated list, one owner per session, and the login that owns a
// session being the one its requests carry.
//
// The last is the test that matters. A cloud session belongs to the account that
// created it, and a send with another account's token is refused at best — so
// two accounts get two distinct canary tokens and every request is asserted
// against the token of the account it was made for.
[Collection("Settings")]
public class CloudAccountBoardTests : IDisposable
{
    private const string TokenA = "sk-ant-oat01-CANARY-A-0123456789abcdef";
    private const string TokenB = "sk-ant-oat01-CANARY-B-fedcba9876543210";
    private const string RootA = "/Users/x/.claude";
    private const string RootB = "/Users/x/.claude-board";

    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => CloudAccounts.SetForTests(null);

    // --- fakes ---------------------------------------------------------------

    private sealed class Creds : ICloudCredentialSource
    {
        internal string? StampValue { get; set; } = "s1";
        internal CredentialRead Reading { get; set; }
        internal int Reads;

        internal Creds(string token) =>
            Reading = new(CredentialOutcome.Found, token, null, "a credential is present");

        public string? Stamp() => StampValue;

        public CredentialRead Read()
        {
            Interlocked.Increment(ref Reads);
            return Reading;
        }
    }

    // Records the token each path was requested with.
    private sealed class Api : ICloudApi
    {
        private readonly Func<string, string, CloudApiResult> _answer;
        internal List<(string Token, string Path)> Calls { get; } = new();

        internal Api(Func<string, string, CloudApiResult> answer) => _answer = answer;

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            lock (Calls) Calls.Add((context.AccessToken, context.Path));
            return Task.FromResult(_answer(context.AccessToken, context.Path));
        }
    }

    private static List<(string Token, string Path)> Snapshot(Api api)
    {
        lock (api.Calls) return api.Calls.ToList();
    }

    private static CloudApiResult Ok(string body) => new(CloudOutcomes.OutcomeFor(200, body), body);

    private static CloudApiResult Fail(int status) => new(CloudOutcomes.OutcomeFor(status, null), null);

    private static string Roster(params string[] ids) =>
        "{\"data\":[" + string.Join(",", ids.Select(id =>
            "{\"id\":\"" + id + "\",\"environment_kind\":\"anthropic_cloud\",\"session_status\":\"running\","
            + "\"status_bucket\":\"running\",\"title\":\"t-" + id + "\",\"updated_at\":\"2026-09-19T10:00:00Z\"}"))
        + "],\"has_more\":false,\"last_id\":null}";

    private static CloudAccount Account(string root, ICloudCredentialSource source) =>
        new(root, root == RootA ? "default" : ClaudeCliCredentials.SafeLabel(root), source, source);

    private static ClaudeCloudSessions.Session S(string id, string? owner, int minute = 0, string title = "t") =>
        new(id, title, "idle", new DateTime(2026, 9, 19, 10, minute, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null, OwnerRoot: owner);

    private static async Task<ClaudeCloudSessions.StepResult> Step(
        Api api, ICloudCredentialSource creds, DateTime? now = null, CredentialBudget? budget = null) =>
        await ClaudeCloudSessions.StepAsync(api, creds, ClaudeCloudSessions.ArmState.Initial,
            now ?? Now, CancellationToken.None, budget ?? TestBudgets.AlwaysAnswers);

    // --- dedup ---------------------------------------------------------------

    [Fact]
    public void OneIdSeenByTwoAccountsIsOneSessionOwnedByTheFirstRoot()
    {
        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_1", RootA) }, new[] { S("session_1", RootB) } },
            Array.Empty<ClaudeCloudSessions.Session>());

        var only = Assert.Single(merged);
        Assert.Equal(RootA, only.OwnerRoot);
    }

    [Fact]
    public void OwnershipStaysWithThePreviousOwnerWhileItStillSeesTheSession()
    {
        var previous = new[] { S("session_1", RootB) };

        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_1", RootA) }, new[] { S("session_1", RootB) } }, previous);

        Assert.Equal(RootB, Assert.Single(merged).OwnerRoot);
    }

    [Fact]
    public void OwnershipMovesOnWhenThePreviousOwnerNoLongerSeesIt()
    {
        var previous = new[] { S("session_1", RootB) };

        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_1", RootA) }, Array.Empty<ClaudeCloudSessions.Session>() }, previous);

        Assert.Equal(RootA, Assert.Single(merged).OwnerRoot);
    }

    [Fact]
    public void TheNewestCopyProvidesTheRowDataWhoeverOwnsIt()
    {
        var merged = ClaudeCloudRoster.MergeAccounts(
            new[]
            {
                new[] { S("session_1", RootA, minute: 1, title: "old") },
                new[] { S("session_1", RootB, minute: 9, title: "new") },
            },
            Array.Empty<ClaudeCloudSessions.Session>());

        var only = Assert.Single(merged);
        Assert.Equal("new", only.Title);
        Assert.Equal(RootA, only.OwnerRoot);
    }

    [Fact]
    public void DistinctSessionsFromBothAccountsAreAllKeptNewestFirst()
    {
        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_a", RootA, minute: 1) }, new[] { S("session_b", RootB, minute: 5) } },
            Array.Empty<ClaudeCloudSessions.Session>());

        Assert.Equal(new[] { "session_b", "session_a" }, merged.Select(m => m.Id));
    }

    // --- status text -----------------------------------------------------------

    [Fact]
    public void OneAccountReadsExactlyAsItAlwaysDid()
    {
        Assert.Equal("no cloud sessions (578 sessions inspected)",
            ClaudeCloudRoster.DescribeAccounts(new[] { ("default", "no cloud sessions (578 sessions inspected)") }, 0));
    }

    [Fact]
    public void SeveralAccountsGetAHeaderAndOneLineEach()
    {
        var text = ClaudeCloudRoster.DescribeAccounts(
            new[] { ("default", "1 cloud session (578 sessions inspected)"), ("board", "the Claude Code CLI signed this login out") },
            1);

        Assert.Equal("2 accounts, 1 cloud session\ndefault: 1 cloud session (578 sessions inspected)\n"
                     + "board: the Claude Code CLI signed this login out", text);
        Assert.StartsWith("3 accounts, 2 cloud sessions", ClaudeCloudRoster.DescribeAccounts(
            new[] { ("a", "x"), ("b", "y"), ("c", "z") }, 2));
    }

    [Fact]
    public void ARootNamedLikeAnEmailNeverPutsAnAtSignInTheText()
    {
        var accounts = ClaudeCliCredentials.SourcesFor(isMacOS: false, "/Users/x", "/Users/x/.claude-me@example.com");
        var board = new CloudAccountBoard(accounts);

        Assert.DoesNotContain("@", board.StatusText);
        Assert.Equal("me", accounts[1].Label);
        Assert.Equal("account", ClaudeCliCredentials.SafeLabel("/Users/x/@example.com"));
    }

    // --- the board ---------------------------------------------------------------

    private static (CloudAccountBoard Board, Creds A, Creds B) TwoAccounts()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        return (new CloudAccountBoard(new[] { Account(RootA, a), Account(RootB, b) }), a, b);
    }

    [Fact]
    public async Task AccountAGetting401IsHaltedWhileBsSessionsStillPublish()
    {
        var (board, a, b) = TwoAccounts();
        var api = new Api((token, _) => token == TokenA ? Fail(401) : Ok(Roster("session_b1")));

        var stepA = await Step(api, a);
        var stepB = await Step(api, b);
        board.Apply(RootA, stepA, Now);
        var merged = board.Apply(RootB, stepB, Now);

        Assert.True(stepA.Next.Halted);
        Assert.Equal("session_b1", Assert.Single(merged).Id);
        Assert.Equal(RootB, merged[0].OwnerRoot);
        var lines = board.StatusText.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("default: ", lines[1]);
        Assert.StartsWith("board: 1 cloud session", lines[2]);
    }

    [Fact]
    public async Task ANullSnapshotFromAKeepsAsSlotAndAHaltEmptiesOnlyItsOwn()
    {
        var (board, a, b) = TwoAccounts();
        var ok = new Api((_, _) => Ok(Roster("session_1")));
        board.Apply(RootA, await Step(ok, a), Now);
        board.Apply(RootB, await Step(new Api((t, _) => Ok(Roster("session_2"))), b), Now);

        // A retryable failure: no snapshot, so A's orbs stay.
        var failed = await Step(new Api((_, _) => Fail(503)), a);
        Assert.Null(failed.Snapshot);
        var kept = board.Apply(RootA, failed, Now);
        Assert.Equal(2, kept.Count);

        // A refusal: A's slot empties, B's does not.
        var refused = await Step(new Api((_, _) => Fail(401)), a);
        var after = board.Apply(RootA, refused, Now);
        Assert.Equal("session_2", Assert.Single(after).Id);
    }

    [Fact]
    public async Task BothAccountsSeeingOneIdIsOneOrbAndTheOwnerDoesNotFlap()
    {
        var (board, a, b) = TwoAccounts();
        var api = new Api((_, _) => Ok(Roster("session_shared")));

        board.Apply(RootB, await Step(api, b), Now);
        var first = board.Apply(RootA, await Step(api, a), Now);

        var only = Assert.Single(first);
        // B reported it first; A joining later must not take it over.
        Assert.Equal(RootB, only.OwnerRoot);
    }

    [Fact]
    public async Task ARateLimitOnOneAccountHoldsEveryAccountBack()
    {
        var (board, a, _) = TwoAccounts();
        var step = await Step(new Api((_, _) => Fail(429)), a);
        Assert.True(step.RateLimited);

        board.Apply(RootA, step, Now);

        Assert.True(board.HoldRemaining(Now) >= Backoff.RateLimitFloor);
        Assert.Equal(TimeSpan.Zero, board.HoldRemaining(Now + TimeSpan.FromMinutes(10)));
        Assert.False((await Step(new Api((_, _) => Fail(503)), a)).RateLimited);
    }

    [Fact]
    public void AnUnexpectedErrorIsSaidOnThatAccountsLineOnly()
    {
        var (board, _, _) = TwoAccounts();

        board.ApplyError(RootB, "boom");

        Assert.Contains("board: boom", board.StatusText);
        Assert.Contains("default: checking…", board.StatusText);
        Assert.Equal(2, board.Accounts.Count);
        Assert.Empty(board.Merged);
    }

    // --- the owner's login, end to end -------------------------------------------

    [Fact]
    public async Task EveryRequestForABOwnedSessionCarriesBsTokenAndNeverAs()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        CloudAccounts.SetForTests(new[] { Account(RootA, a), Account(RootB, b) });

        // The poll: each account lists with its own token.
        var pollApi = new Api((token, _) => Ok(Roster(token == TokenA ? "session_a1" : "session_b1")));
        var board = new CloudAccountBoard(CloudAccounts.Current);
        board.Apply(RootA, await Step(pollApi, a), Now);
        var merged = board.Apply(RootB, await Step(pollApi, b), Now);
        var bSession = merged.Single(s => s.Id == "session_b1");
        Assert.Equal(RootB, bSession.OwnerRoot);

        // The chat: resolved through the same registry, by the session's owner.
        // Reads, a send (CB-199) and a Stop all go out on it — the write path is
        // the one where the wrong account would do the most harm.
        // The live loop's status reads (CB-199) answer "working", so the loop runs
        // to its cap on B's login and Stop is still there to press afterwards.
        var ticks = 0;
        var chatApi = new Api((_, path) =>
            path == CloudRequest.CodeEventsPath("session_b1")
                ? Ok("{\"results\":[{\"duplicate\":false,\"sequence_num\":\"1\",\"event_id\":\"e\"}]}")
                : path == CloudRequest.CodeSessionPath("session_b1")
                    ? Ok("{\"status_bucket\":\"working\"}")
                    : Ok("{\"data\":[],\"has_more\":false,\"last_id\":null}"));
        var chat = new ClaudeCloudChatSession(
            bSession, chatApi, CloudAccounts.SourceFor(bSession.OwnerRoot), action => action())
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Enabled = () => true,

            // One live tick, then park until the panel closes: enough for one
            // status read on B's login, and short, since a loop left to run to
            // its cap made this test slow enough to be at the mercy of whatever
            // else shares the account registry in a parallel run.
            Delay = (_, ct) => Interlocked.Increment(ref ticks) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.Infinite, ct),
        };
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("hello from b"));
        var statusPath = CloudRequest.CodeSessionPath("session_b1");
        for (var i = 0; i < 500 && !Snapshot(chatApi).Any(c => c.Path == statusPath); i++) await Task.Delay(10);
        Assert.True(Snapshot(chatApi).Any(c => c.Path == statusPath),
            "B's live loop never made its status read.");

        Assert.True(chat.CanInterrupt);
        chat.Cancel();
        await chat.InterruptTask!;
        chat.PanelClosed();
        await chat.LiveTask!;

        var calls = Snapshot(chatApi);
        var writes = calls.Where(c => c.Path == CloudRequest.CodeEventsPath("session_b1")).ToList();
        Assert.Equal(2, writes.Count);
        Assert.Contains(calls, c => c.Path == statusPath);

        Assert.All(calls, c => Assert.Equal(TokenB, c.Token));
        Assert.DoesNotContain(calls, c => c.Token == TokenA);
        Assert.All(pollApi.Calls.Where(c => c.Token == TokenA), c => Assert.NotEqual(TokenB, c.Token));
    }

    [Fact]
    public async Task ReopeningAfterOwnershipMovesReadsWithTheNewOwnersLogin()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        CloudAccounts.SetForTests(new[] { Account(RootA, a), Account(RootB, b) });
        var api = new Api((_, _) => Ok("{\"data\":[],\"has_more\":false,\"last_id\":null}"));
        var chat = new ClaudeCloudChatSession(S("session_1", RootA), api, CloudAccounts.SourceFor(RootA), x => x())
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
        };

        await chat.LoadAsync(CancellationToken.None);
        chat.UseCredentials(CloudAccounts.SourceFor(RootB + "/"));
        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal(TokenA, api.Calls.First().Token);
        Assert.Equal(TokenB, api.Calls.Last().Token);
    }

    [Fact]
    public void AnUnknownOrMissingOwnerFallsBackToTheFirstAccount()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        CloudAccounts.SetForTests(new[] { Account(RootA, a), Account(RootB, b) });

        Assert.Same(CloudAccounts.GatedFor(CloudAccounts.Current[0]), CloudAccounts.SourceFor(null));
        Assert.Same(CloudAccounts.GatedFor(CloudAccounts.Current[0]), CloudAccounts.SourceFor("/somewhere/else"));
        Assert.Same(CloudAccounts.GatedFor(CloudAccounts.Current[1]), CloudAccounts.SourceFor(RootB));
    }

    // Both build the real stores without querying any of them; the registry is
    // the same list the poll and the chat panels share.
    [Fact]
    public void TheRegistryBuildsItselfOnFirstUseAndRebuildsOnRequest()
    {
        CloudAccounts.SetForTests(null);

        var first = CloudAccounts.Current;
        Assert.NotEmpty(first);
        Assert.Same(first, CloudAccounts.Current);
        Assert.NotSame(first, CloudAccounts.Rebuild());
    }

    // --- the gate and the latch ---------------------------------------------------

    // A source whose Read announces it has entered, then waits to be released.
    // Ordering and "who has entered" are observed through signals and a log,
    // never through how long anything took: a loaded runner stretches sleeps, and
    // a test that races them fails there and only there.
    private sealed class Blocking : ICloudCredentialSource, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly string _name;
        private readonly List<string> _log;
        private readonly CredentialRead _result;
        internal int Reads;
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Blocking(string name, List<string> log, CredentialRead result)
        {
            _name = name;
            _log = log;
            _result = result;
        }

        public string? Stamp() => "s";

        public CredentialRead Read()
        {
            Interlocked.Increment(ref Reads);
            lock (_log) _log.Add(_name + "-start");
            Entered.TrySetResult();
            _release.Wait();
            lock (_log) _log.Add(_name + "-end");
            return _result;
        }

        internal void Release() => _release.Set();

        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    private static (CloudReadCoordinator Coord, CloudAccountSource GA, CloudAccountSource GB,
        Creds BKeychain, Creds BFile) Gated(ICloudCredentialSource aKeychain, ICloudCredentialSource? aFile = null)
    {
        var coord = new CloudReadCoordinator();
        var bKeychain = new Creds(TokenB);
        var bFile = new Creds(TokenB);
        var accountA = new CloudAccount(RootA, "default", aKeychain, aFile ?? aKeychain);
        var accountB = new CloudAccount(RootB, "board", bKeychain, bFile);
        return (coord, new CloudAccountSource(accountA, coord), new CloudAccountSource(accountB, coord),
            bKeychain, bFile);
    }

    // The race QA found: B queues behind A's dialog having decided nothing, A
    // says no, and B must then read files only rather than raise a second dialog
    // right after the refusal. The choice is made once B holds the gate, so the
    // outcome does not depend on when B happened to start.
    [Theory]
    [InlineData("declined")]
    [InlineData("unanswered")]
    public async Task AQueuedAccountSkipsItsKeychainWhenTheOneAheadOfItDeclined(string how)
    {
        var log = new List<string>();
        var denied = new CredentialRead(CredentialOutcome.Denied, null, null, "declined");
        using var a = new Blocking("A", log, denied);
        var (coord, ga, gb, bKeychain, bFile) = Gated(a);
        var api = new Api((_, _) => Ok(Roster()));

        // "unanswered": A's own short budget expires while it is parked, which
        // only needs to happen eventually. "declined": A is released after B has
        // been started.
        //
        // The "unanswered" budget is expired by hand once B is queued behind A, so
        // "B waited for A" no longer depends on a clock beating a scheduler.
        var (manual, expire) = TestBudgets.Manual();
        var stepA = Step(api, ga, budget: how == "unanswered" ? manual : TestBudgets.AlwaysAnswers);
        await a.Entered.Task;
        var stepB = Step(api, gb);
        if (how == "declined") a.Release();
        else expire.SetResult();

        await stepA;
        var b = await stepB;

        Assert.Equal(0, bKeychain.Reads);
        Assert.Equal(1, bFile.Reads);
        Assert.NotNull(b.Snapshot);
        Assert.True(coord.KeychainSkippedFor());
    }

    [Fact]
    public async Task TheSecondReadIsNotEnteredUntilTheFirstHasFinished()
    {
        var log = new List<string>();
        var found = (string t) => new CredentialRead(CredentialOutcome.Found, t, null, "a credential is present");
        using var a = new Blocking("A", log, found(TokenA));
        using var b = new Blocking("B", log, found(TokenB));
        b.Release();
        var coord = new CloudReadCoordinator();
        var ga = new CloudAccountSource(new CloudAccount(RootA, "default", a, a), coord);
        var gb = new CloudAccountSource(new CloudAccount(RootB, "board", b, b), coord);
        var api = new Api((_, _) => Ok(Roster()));

        var t1 = Step(api, ga);
        await a.Entered.Task; // A is inside its read and holds the gate
        var t2 = Step(api, gb);

        // A negative that can only pass early, never fail late: however long B
        // takes to reach the gate, it cannot have entered its read while A is
        // parked. The log below is what proves the order.
        await Task.Delay(50);
        Assert.Equal(0, b.Reads);

        a.Release();
        await t1;
        await t2;

        Assert.Equal(new[] { "A-start", "A-end", "B-start", "B-end" }, log);
    }

    [Fact]
    public async Task ASkippedAccountThatFindsNothingSaysItWasNotAsked()
    {
        var aKeychain = new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "declined") };
        var (coord, ga, gb, bKeychain, bFile) = Gated(aKeychain);
        bFile.Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "no credential stored");
        var board = new CloudAccountBoard(new[] { ga.Account, gb.Account }, coord);
        var api = new Api((_, _) => Ok(Roster()));

        await Step(api, ga);
        var stepB = await Step(api, gb);
        board.Apply(RootB, stepB, Now);

        Assert.True(stepB.Next.Halted);
        Assert.Contains(CloudAccountBoard.NotAsked, board.StatusText);
        Assert.Equal(0, bKeychain.Reads);
    }

    [Fact]
    public async Task ASkippedAccountThatIsNotHaltedKeepsItsOwnStatus()
    {
        var aKeychain = new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "declined") };
        var (coord, ga, gb, _, _) = Gated(aKeychain);
        var board = new CloudAccountBoard(new[] { ga.Account, gb.Account }, coord);
        var api = new Api((_, _) => Ok(Roster("session_1")));

        await Step(api, ga);
        var stepB = await Step(api, gb);
        board.Apply(RootB, stepB, Now);

        Assert.False(stepB.Next.Halted);
        Assert.DoesNotContain(CloudAccountBoard.NotAsked, board.StatusText);
    }

    // The latch clears for the declining account when its own stamp moves, and
    // the parked account's stamp changes with it — which is what opens the arm's
    // Halted gate so "not asked" un-parks by itself.
    [Fact]
    public async Task TheLatchClearsWhenTheDecliningAccountsStampMovesAndTheStampSaysSo()
    {
        var aKeychain = new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "declined") };
        var (coord, ga, gb, _, _) = Gated(aKeychain);
        await Step(new Api((_, _) => Ok(Roster())), ga);

        var parked = gb.Stamp();
        Assert.EndsWith("|files-only", parked);

        aKeychain.StampValue = "s2"; // the user signed in again
        Assert.False(coord.KeychainSkippedFor());
        Assert.DoesNotContain("|files-only", gb.Stamp());
        Assert.NotEqual(parked, gb.Stamp());
    }

    [Fact]
    public void ADirectReadOfAGatedSourceChoosesAndReads()
    {
        var (_, ga, _, _, _) = Gated(new Creds(TokenA));

        Assert.Equal(CredentialOutcome.Found, ga.Read().Outcome);
    }

    // Chat panels read through the same gate: an open panel cannot stack a dialog
    // on one the poll is showing, and it honours the latch.
    [Fact]
    public async Task AChatReadWaitsForTheGateAndHonoursTheLatch()
    {
        var aKeychain = new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "declined") };
        var (coord, ga, gb, bKeychain, bFile) = Gated(aKeychain);
        CloudAccounts.SetForTests(new[] { ga.Account, gb.Account });
        var api = new Api((_, _) => Ok("{\"data\":[],\"has_more\":false,\"last_id\":null}"));
        var chat = new ClaudeCloudChatSession(S("session_1", RootB), api,
            CloudAccounts.SourceFor(RootB), a => a())
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
        };

        // Someone else holds the gate: the panel's read must wait, not race.
        var held = CloudAccounts.Coordinator.Gate;
        await held.WaitAsync();
        Task<bool> load;
        try
        {
            load = chat.LoadAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(load.IsCompleted);
        }
        finally
        {
            // Process-wide gate: a failure above must not leave every later gated
            // read in the process waiting for a release that never comes.
            held.Release();
        }
        Assert.True(await load);

        // And after A declined, B's panel reads its file only.
        var keychainReadsBefore = bKeychain.Reads;
        CloudAccounts.Coordinator.Record(RootA, false,
            new CredentialRead(CredentialOutcome.Denied, null, null, "declined"), aKeychain.Stamp);
        var reads = CloudAccounts.SourceFor(RootB);
        var read = await ClaudeCliCredentials.ReadWithinAsync(reads, TestBudgets.AlwaysAnswers, CancellationToken.None);
        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Equal(keychainReadsBefore, bKeychain.Reads);
        Assert.True(bFile.Reads >= 1);
    }

    // --- concurrent reads of one MultiCredentialSource ----------------------------

    [Fact]
    public void EachReadReturnsItsOwnTraceRatherThanSharingOne()
    {
        var child = new Creds(TokenA);
        var multi = new MultiCredentialSource(new (string, ICloudCredentialSource)[] { ("a", child) });

        var (_, first) = multi.ReadTraced();
        child.Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "signed out");
        var (_, second) = multi.ReadTraced();

        Assert.Equal("a", first.AnsweredBy);
        Assert.Null(second.AnsweredBy);
        Assert.Equal("Found", first.Attempts[0].Outcome.ToString());
        Assert.Equal("signed out", second.Attempts[0].Reason);
        Assert.Null(multi.AnsweredBy);
    }

    [Fact]
    public void CollidingLabelsAreToldApartWithANumber()
    {
        var accounts = ClaudeCliCredentials.SourcesFor(isMacOS: false, "/Users/x", "/Users/x/.claude-me@x.com");
        var more = ClaudeCliCredentials.SourcesFor(isMacOS: false, "/Users/x", "/Users/x/.claude-me@x.com");
        Assert.Equal("me", accounts[1].Label);
        Assert.Equal("me", more[1].Label);

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-me@y.com");
        try
        {
            var both = ClaudeCliCredentials.SourcesFor(isMacOS: false, "/Users/x", "/Users/x/.claude-me@x.com");
            Assert.Equal(new[] { "default", "me", "me-2" }, both.Select(a => a.Label).OrderBy(l => l == "default" ? "" : l));
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-me@y.com");
        }
    }

    // The declining account is held to its own refusal too: a chat panel for one
    // of its sessions must not put the dialog straight back, on that open or on
    // any later one, until its login changes.
    [Fact]
    public async Task ADecliningAccountsOwnChatReadsItsFileOnlyUntilItsStampMoves()
    {
        var aKeychain = new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "declined") };
        var aFile = new Creds(TokenA);
        var (_, ga, gb, _, _) = Gated(aKeychain, aFile);
        CloudAccounts.SetForTests(new[] { ga.Account, gb.Account });
        var coord = CloudAccounts.Coordinator; // the registry builds its own
        await Step(new Api((_, _) => Ok(Roster())), (ICloudCredentialSource)CloudAccounts.SourceFor(RootA));
        var keychainReads = aKeychain.Reads;

        for (var i = 0; i < 2; i++)
        {
            var read = await ClaudeCliCredentials.ReadWithinAsync(
                CloudAccounts.SourceFor(RootA), TestBudgets.AlwaysAnswers, CancellationToken.None);
            Assert.Equal(CredentialOutcome.Found, read.Outcome);
        }

        Assert.Equal(keychainReads, aKeychain.Reads);
        Assert.Equal(2, aFile.Reads);

        aKeychain.StampValue = "s2";
        Assert.False(coord.KeychainSkippedFor());
        await ClaudeCliCredentials.ReadWithinAsync(
            CloudAccounts.SourceFor(RootA), TestBudgets.AlwaysAnswers, CancellationToken.None);
        Assert.True(aKeychain.Reads > keychainReads);
    }

    // errSecInteractionNotAllowed is no refusal: no dialog was shown. It must not
    // latch, so the other account still reads its Keychain child, and the account
    // that could not ask is retried rather than halted.
    [Fact]
    public async Task ACannotPromptAnswerDoesNotLatchAndIsRetried()
    {
        var aKeychain = new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.CannotPrompt, null, null, "no one at the screen") };
        var (coord, ga, gb, bKeychain, _) = Gated(aKeychain);
        var api = new Api((_, _) => Ok(Roster()));

        var stepA = await Step(api, ga);
        var stepB = await Step(api, gb);

        Assert.False(coord.KeychainSkippedFor());
        Assert.False(stepA.Next.Halted);
        Assert.NotNull(stepA.Next.Backoff);
        Assert.Equal(1, bKeychain.Reads);
        Assert.NotNull(stepB.Snapshot);
        Assert.Contains("could not ask", stepA.Status);
    }

    // The real-Mac composition: an expired file, then a Keychain that could not
    // ask. The account says the login expired, not that anything was denied.
    [Fact]
    public async Task AnExpiredLoginLeadsTheAccountsLineOverAKeychainThatCouldNotAsk()
    {
        var multi = new MultiCredentialSource(new (string, ICloudCredentialSource)[]
        {
            ("file", new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, Now, "expired", ClaudeCliCredentials.ExpiredLead) }),
            ("svc", new Creds(TokenA) { Reading = new CredentialRead(CredentialOutcome.CannotPrompt, null, null, "no one at the screen") }),
        });

        var step = await Step(new Api((_, _) => Ok(Roster())), multi);

        Assert.Equal(ClaudeCliCredentials.ExpiredLead, step.Status);
    }
}
