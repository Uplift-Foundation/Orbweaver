using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// Covers one tick of the cloud arm: which credential state stops it, which cycle
// is due, what it asks for, and — the one that matters most — what it publishes
// when a fetch fails halfway through.
//
// Driven entirely through fakes. Nothing here opens a socket or reads a
// credential store, which is the whole reason StepAsync takes its API, its
// credential source and its clock as arguments rather than reaching for them.
public class ClaudeCloudStepTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    // --- fakes ---------------------------------------------------------------

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<string, CloudApiResult> _answer;

        internal FakeApi(Func<string, CloudApiResult> answer) => _answer = answer;

        // The whole context, not just the path, so a test can see the method
        // and body a call went out with as well as where it went.
        internal List<CloudRequestContext> Requests { get; } = new();

        internal List<string> Paths => Requests.Select(r => r.Path).ToList();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(_answer(context.Path));
        }
    }

    // A credential store that never answers.
    //
    // **This is the measured failure, not an invented one.** On a real Mac with no
    // window server session, the Keychain's *data* query does not return — killed
    // at 30 seconds and at 60 — while the attributes-only query answers instantly.
    // So the fake blocks in Read() and not in Stamp(), which is the asymmetry that
    // matters: an arm that could not get past Stamp() would have failed loudly
    // long before this.
    //
    // The gate is released in Dispose so the borrowed thread is not left parked
    // for the rest of the test run. Production has no such luxury, which is why
    // the leak is written down in ReadWithinAsync rather than claimed away.
    private sealed class HangingCredentials : ICloudCredentialSource, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        internal int Reads;
        internal int Stamps;

        public string? Stamp()
        {
            Stamps++;
            return "stamp-1";
        }

        // Completed once the abandoned read is actually running. With a budget that
        // has already expired the arm can return before the pool has started it,
        // so a test that counts reads waits for this rather than assuming.
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CredentialRead Read()
        {
            Interlocked.Increment(ref Reads);
            Entered.TrySetResult();
            _gate.Wait();
            return new CredentialRead(CredentialOutcome.Found, Token, null, "too late");
        }

        public void Dispose()
        {
            _gate.Set();
            _gate.Dispose();
        }
    }

    // A store whose Read throws rather than hangs. A source that throws is a bug
    // in the source; the arm still has to survive it.
    private sealed class ThrowingCredentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";

        public CredentialRead Read() =>
            throw new InvalidOperationException("SENSITIVE-EXCEPTION-TEXT");
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal string? StampValue { get; set; } = "stamp-1";
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        internal int Reads { get; private set; }
        internal int Stamps { get; private set; }

        public string? Stamp()
        {
            Stamps++;
            return StampValue;
        }

        public CredentialRead Read()
        {
            Reads++;
            return Reading;
        }
    }

    private static CloudApiResult Ok(string body) =>
        new(CloudOutcomes.OutcomeFor(200, body), body);

    private static CloudApiResult Fail(int status, string? body = null) =>
        new(CloudOutcomes.OutcomeFor(status, body), null);

    private static string Row(string id, string kind = "anthropic_cloud", string status = "running") =>
        "{\"id\":\"" + id + "\",\"environment_kind\":\"" + kind
        + "\",\"session_status\":\"" + status + "\",\"status_bucket\":\"" + status
        + "\",\"title\":\"a session\",\"updated_at\":\"2026-09-19T10:00:00Z\"}";

    private static string Envelope(IEnumerable<string> rows, bool hasMore = false, string? lastId = null)
    {
        var last = lastId is null ? "null" : "\"" + lastId + "\"";
        return "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":"
               + (hasMore ? "true" : "false") + ",\"last_id\":" + last + "}";
    }

    private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ClaudeCloudSessions.Session Session(string id, string state = "idle") =>
        new(id, id, state, new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // --- the walk ------------------------------------------------------------

    // Follows `last_id` until `has_more` is false, and never asks for more than the
    // measured ceiling — `limit=200` is a 400, so a walk that asked for it would
    // return nothing rather than fewer rows.
    [Fact]
    public async Task TheWalkFollowsTheCursorUntilThereIsNoMore()
    {
        var pages = new Queue<string>(new[]
        {
            Envelope(new[] { Row("session_a") }, hasMore: true, lastId: "session_a"),
            Envelope(new[] { Row("session_b") }, hasMore: true, lastId: "session_b"),
            Envelope(new[] { Row("session_c") }),
        });

        var api = new FakeApi(_ => Ok(pages.Dequeue()));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal(new[]
        {
            "/v2/ccr-sessions?limit=100",
            "/v2/ccr-sessions?limit=100&after_id=session_a",
            "/v2/ccr-sessions?limit=100&after_id=session_b",
        }, api.Paths);

        // CB-199 gave the context a method and a body; the roster must still
        // leave both unset, which Build reads as a bodiless GET.
        Assert.All(api.Requests, r =>
        {
            Assert.Null(r.Method);
            Assert.Null(r.Body);
        });

        Assert.NotNull(step.Snapshot);
        Assert.Equal(new[] { "session_a", "session_b", "session_c" },
            step.Snapshot!.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task NoRequestEverAsksForMoreThanAHundred()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.All(api.Paths, path => Assert.Contains("limit=100", path, StringComparison.Ordinal));
        Assert.DoesNotContain(api.Paths, path => path.Contains("limit=200", StringComparison.Ordinal));
    }

    // Eleven pages of roster, ten pages of cap. Keep what was found, mark it, and
    // **say so** — a cap that silently showed fewer sessions would be the same
    // class of bug as the filter getting it wrong, and much harder to notice.
    [Fact]
    public async Task ElevenPagesStopAtTheCapAndTheStatusNamesTheCutoff()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return Ok(Envelope(new[] { Row($"session_p{page:D2}") },
                hasMore: page < 11, lastId: $"session_p{page:D2}"));
        });

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal(CloudRequest.MaxPagesPerWalk, api.Paths.Count);
        Assert.Equal(CloudRequest.MaxPagesPerWalk, step.Snapshot!.Count);
        Assert.Contains($"stopped after {CloudRequest.MaxPagesPerWalk} pages", step.Status,
            StringComparison.Ordinal);
        Assert.Contains("there may be more", step.Status, StringComparison.Ordinal);
    }

    // A walk that ends exactly on the cap with nothing left to fetch is **not**
    // truncated, and must not say it is. The negative control for the case above.
    [Fact]
    public async Task ExactlyTenPagesWithNothingLeftIsNotCalledTruncated()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return Ok(Envelope(new[] { Row($"session_p{page:D2}") },
                hasMore: page < CloudRequest.MaxPagesPerWalk, lastId: $"session_p{page:D2}"));
        });

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal(CloudRequest.MaxPagesPerWalk, api.Paths.Count);
        Assert.DoesNotContain("stopped after", step.Status, StringComparison.Ordinal);
    }

    // A page claiming `has_more` with no cursor to follow ends the walk rather than
    // asking for page one forever.
    [Fact]
    public async Task HasMoreWithNoCursorEndsTheWalk()
    {
        var api = new FakeApi(_ => Ok(
            """{"data":[],"has_more":true,"last_id":null}"""));

        await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Single(api.Paths);
    }

    [Fact]
    public async Task ASuccessfulWalkRecordsWhenItHappenedAndClearsAnyBackoff()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Backoff = TimeSpan.FromSeconds(8) },
            Now, CancellationToken.None);

        Assert.Equal(Now, step.Next.LastWalkUtc);
        Assert.Null(step.Next.Backoff);
        Assert.False(step.Next.Halted);
        Assert.Equal(CloudRequest.UnmeasuredFirstPageInterval, step.Wait);
    }

    // --- a failure mid-walk --------------------------------------------------

    // **The requirement this whole nullable-snapshot design exists for.** A walk
    // that dies on page two must not replace a good roster with an empty one:
    // orbs vanishing because a request timed out would read as sessions having
    // ended, which is a lie told on the strength of a network hiccup.
    [Fact]
    public async Task AMidWalkFailureNeverPublishesAnEmptySnapshotOverAGoodOne()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return page == 1
                ? Ok(Envelope(new[] { Row("session_a") }, hasMore: true, lastId: "session_a"))
                : Fail(503);
        });

        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Sessions = good },
            Now, CancellationToken.None);

        Assert.Null(step.Snapshot);
        Assert.Equal(good, step.Next.Sessions);
        Assert.False(step.Next.Halted);
        Assert.Equal(Backoff.UnavailableFloor, step.Wait);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(400)]
    [InlineData(0)]
    public async Task EveryRetryableFailureLeavesTheOrbsWhereTheyAre(int status)
    {
        var api = new FakeApi(_ => Fail(status));
        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Sessions = good },
            Now, CancellationToken.None);

        Assert.Null(step.Snapshot);
        Assert.Equal(good, step.Next.Sessions);
    }

    // A definite refusal is the opposite case, and it *should* clear the orbs:
    // there is no access, so there is nothing to draw. It also halts, so nothing
    // asks again — and on macOS that is what keeps "never a repeated OS prompt"
    // true.
    [Theory]
    [InlineData(401, null)]
    [InlineData(401, """{"error":{"message":"OAuth access token is invalid."}}""")]
    [InlineData(403, null)]
    public async Task ADefiniteRefusalClearsTheOrbsAndHalts(int status, string? body)
    {
        var api = new FakeApi(_ => Fail(status, body));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Sessions = new[] { Session("session_known") } },
            Now, CancellationToken.None);

        Assert.NotNull(step.Snapshot);
        Assert.Empty(step.Snapshot!);
        Assert.True(step.Next.Halted);
        Assert.Empty(step.Next.Sessions);
        Assert.Equal(ClaudeCloudSessions.HaltedRecheckInterval, step.Wait);
    }

    // A retryable failure doubles, so a host that is down for an hour is not asked
    // once a second for an hour.
    [Fact]
    public async Task ARetryableFailureBacksOff()
    {
        var api = new FakeApi(_ => Fail(503));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { Backoff = TimeSpan.FromSeconds(8) },
            Now, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(16), step.Wait);
        Assert.Equal(TimeSpan.FromSeconds(16), step.Next.Backoff);
    }

    // --- the credential ------------------------------------------------------

    // **Halted means halted.** No read, no socket, no prompt — only Stamp(), which
    // on macOS is the attributes-only query the consent dialog does not guard.
    [Fact]
    public async Task AHaltedArmReadsNoSecretAndOpensNoSocket()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var credentials = new FakeCredentials { StampValue = "stamp-1" };

        var halted = ClaudeCloudSessions.ArmState.Initial with
        {
            Halted = true,
            CredentialStamp = "stamp-1",
            Status = "access to the Claude Code login was declined",
        };

        var step = await ClaudeCloudSessions.StepAsync(api, credentials, halted, Now,
            CancellationToken.None);

        Assert.Empty(api.Paths);
        Assert.Equal(0, credentials.Reads);
        Assert.Equal(1, credentials.Stamps);
        Assert.Null(step.Snapshot);
        Assert.Equal(halted, step.Next);
        Assert.Equal("access to the Claude Code login was declined", step.Status);
    }

    // And it comes back on its own once the stamp moves — the user having signed
    // in again, or re-approved us.
    [Fact]
    public async Task AMovedStampLetsAHaltedArmTryAgain()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));
        var credentials = new FakeCredentials { StampValue = "stamp-2" };

        var step = await ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial with { Halted = true, CredentialStamp = "stamp-1" },
            Now, CancellationToken.None);

        Assert.Equal(1, credentials.Reads);
        Assert.False(step.Next.Halted);
        Assert.Equal("stamp-2", step.Next.CredentialStamp);
        Assert.Single(step.Snapshot!);
    }

    // Two facts rather than a theory: CredentialOutcome is internal, and an xUnit
    // theory's parameters have to be as accessible as the method, which is public.
    [Fact]
    public Task ADeniedLoginHaltsWithoutAskingTheEndpointAnything() =>
        ARefusedOrAbsentLoginHalts(CredentialOutcome.Denied);

    [Fact]
    public Task AnAbsentLoginHaltsWithoutAskingTheEndpointAnything() =>
        ARefusedOrAbsentLoginHalts(CredentialOutcome.NotLoggedIn);

    private static async Task ARefusedOrAbsentLoginHalts(CredentialOutcome outcome)
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var credentials = new FakeCredentials
        {
            Reading = new CredentialRead(outcome, null, null, "…"),
        };

        var step = await ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial with { Sessions = new[] { Session("session_known") } },
            Now, CancellationToken.None);

        Assert.Empty(api.Paths);
        Assert.True(step.Next.Halted);
        Assert.Empty(step.Snapshot!);
        Assert.Equal(ClaudeCliCredentials.Describe(outcome), step.Status);
    }

    // Unreadable and Malformed are transient — the file was locked, the shape was
    // briefly odd — so they retry rather than halt, and leave the orbs alone.
    [Fact]
    public Task AnUnreadableCredentialRetriesAndKeepsTheOrbs() =>
        ATransientCredentialProblemRetries(CredentialOutcome.Unreadable);

    [Fact]
    public Task AMalformedCredentialRetriesAndKeepsTheOrbs() =>
        ATransientCredentialProblemRetries(CredentialOutcome.Malformed);

    private static async Task ATransientCredentialProblemRetries(CredentialOutcome outcome)
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api,
            new FakeCredentials { Reading = new CredentialRead(outcome, null, null, "…") },
            ClaudeCloudSessions.ArmState.Initial with { Sessions = good },
            Now, CancellationToken.None);

        Assert.Null(step.Snapshot);
        Assert.False(step.Next.Halted);
        Assert.Equal(good, step.Next.Sessions);
        Assert.Equal(Backoff.UnavailableFloor, step.Wait);
    }

    // A Found reading with no token in it is a contradiction, and it must stop
    // rather than send `Bearer `.
    [Fact]
    public async Task AFoundReadingWithNoTokenStillStops()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));

        var step = await ClaudeCloudSessions.StepAsync(api,
            new FakeCredentials { Reading = new CredentialRead(CredentialOutcome.Found, null, null, "…") },
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Empty(api.Paths);
        Assert.True(step.Next.Halted);
    }

    // --- the short cycle -----------------------------------------------------

    [Fact]
    public async Task BetweenWalksTheCycleIsPageOneThenOneReadPerKnownSession()
    {
        var api = new FakeApi(path => path.Contains("after_id", StringComparison.Ordinal)
            ? Fail(400)
            : path.EndsWith("session_b", StringComparison.Ordinal)
                ? Ok(Row("session_b"))
                : Ok(Envelope(new[] { Row("session_a") })));

        var state = ClaudeCloudSessions.ArmState.Initial with
        {
            LastWalkUtc = Now - TimeSpan.FromSeconds(1),
            Sessions = new[] { Session("session_a"), Session("session_b") },
        };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(), state, Now,
            CancellationToken.None);

        Assert.Equal(new[]
        {
            "/v2/ccr-sessions?limit=100",
            "/v2/ccr-sessions/session_b",
        }, api.Paths);

        Assert.Equal(new[] { "session_a", "session_b" },
            step.Snapshot!.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    // A session that appeared on page one is not asked about again. The cheapest
    // possible optimisation and the one that keeps a short cycle short.
    [Fact]
    public async Task ASessionAlreadyOnPageOneIsNotReadASecondTime()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_a") },
            },
            Now, CancellationToken.None);

        Assert.Single(api.Paths);
        Assert.Single(step.Snapshot!);
    }

    // The point of the per-session reads: a cloud session created last month that
    // resumes today does **not** move to the front of a `created_at` DESC roster,
    // so page one alone would show it frozen at whatever the last walk saw.
    [Fact]
    public async Task APerSessionReadIsWhatMovesASessionOffPageOneOutOfIdle()
    {
        var api = new FakeApi(path => path.EndsWith("session_old", StringComparison.Ordinal)
            ? Ok(Row("session_old", status: "running"))
            : Ok(Envelope(new[] { Row("session_new") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_old") },
            },
            Now, CancellationToken.None);

        Assert.Equal("generating", step.Snapshot!.Single(s => s.Id == "session_old").State);
    }

    // A session that has been archived since the last walk is definitively gone,
    // and its orb goes with it.
    [Fact]
    public async Task AnArchivedSessionLosesItsOrbOnTheShortCycle()
    {
        var api = new FakeApi(path => path.EndsWith("session_gone", StringComparison.Ordinal)
            ? Ok(Row("session_gone", status: "archived"))
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_gone") },
            },
            Now, CancellationToken.None);

        Assert.Empty(step.Snapshot!);
    }

    // A per-session read that merely failed is **no news**, not a death. The orb
    // survives on what the last deep walk saw, and the next walk is the authority
    // that will drop it if it has really gone.
    [Fact]
    public async Task ARetryablePerSessionFailureLeavesThatOrbAlone()
    {
        var api = new FakeApi(path => path.EndsWith("session_known", StringComparison.Ordinal)
            ? Fail(503)
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_known") },
            },
            Now, CancellationToken.None);

        Assert.Equal("session_known", Assert.Single(step.Snapshot!).Id);
        Assert.False(step.Next.Halted);
    }

    // A 404 is not that. **Measured** against a deleted cloud session, it is a
    // definite answer, so the orb goes on this short cycle rather than
    // lingering until the next deep walk — and only that orb: the arm is not
    // halted, and a session whose own read succeeded stays.
    [Fact]
    public async Task ADeletedSessionsOrbGoesAndNothingElseDoes()
    {
        var api = new FakeApi(path =>
            path.EndsWith("session_deleted", StringComparison.Ordinal) ? Fail(404)
            : path.EndsWith("session_alive", StringComparison.Ordinal) ? Ok(Row("session_alive"))
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_deleted"), Session("session_alive") },
            },
            Now, CancellationToken.None);

        Assert.False(step.Next.Halted);
        Assert.Equal("session_alive", Assert.Single(step.Snapshot!).Id);
        Assert.Equal("session_alive", Assert.Single(step.Next.Sessions).Id);

        // Page one, then both direct reads: the 404 did not end the cycle.
        Assert.Equal(3, api.Paths.Count);
    }

    // A 404 on the *listing* is the collection moving, not a session going, so
    // the roster reads it as it did before CB-199: retryable, no orbs dropped,
    // and a status line that does not claim a session was deleted.
    [Fact]
    public async Task AFourOhFourOnTheListingBacksOffInsteadOfHalting()
    {
        var api = new FakeApi(_ => Fail(404));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.False(step.Next.Halted);
        Assert.Null(step.Snapshot);
        Assert.Equal(Backoff.UnavailableFloor, step.Next.Backoff);
        Assert.Equal("the endpoint answered 404", step.Status);
        Assert.DoesNotContain("no longer exists", step.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRosterViewLeavesEveryOtherOutcomeAlone()
    {
        var refused = CloudOutcomes.OutcomeFor(401, null);

        Assert.Same(refused, ClaudeCloudSessions.RosterView(refused));
        Assert.Equal(CloudOutcomeKind.Unavailable,
            ClaudeCloudSessions.RosterView(CloudOutcomes.OutcomeFor(404, null)).Kind);
    }

    // A per-session read refused for a reason that would stop the arm stops it
    // here too, rather than being refused once per known session.
    [Fact]
    public async Task ADefinitePerSessionRefusalStopsTheWholeCycle()
    {
        var api = new FakeApi(path => path.Contains("/session_", StringComparison.Ordinal)
            ? Fail(401)
            : Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = new[] { Session("session_a"), Session("session_b") },
            },
            Now, CancellationToken.None);

        // Page one, then one refusal, and no attempt at the second session.
        Assert.Equal(2, api.Paths.Count);
        Assert.True(step.Next.Halted);
    }

    // Page one failing on a short cycle is the same rule as a walk failing
    // mid-way: the orbs stay, nothing is published, and the arm backs off. Worth
    // its own case because the short cycle reaches it by a different path and a
    // reader could reasonably assume the walk's test covered both.
    [Fact]
    public async Task PageOneFailingOnAShortCycleLeavesTheOrbsAlone()
    {
        var api = new FakeApi(_ => Fail(503));
        var good = new[] { Session("session_known") };

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with
            {
                LastWalkUtc = Now - TimeSpan.FromSeconds(1),
                Sessions = good,
            },
            Now, CancellationToken.None);

        Assert.Single(api.Paths);
        Assert.Null(step.Snapshot);
        Assert.Equal(good, step.Next.Sessions);
        Assert.Equal(Backoff.UnavailableFloor, step.Wait);
    }

    [Fact]
    public async Task AShortCycleDoesNotMoveTheWalkClock()
    {
        var api = new FakeApi(_ => Ok(Envelope(Array.Empty<string>())));
        var lastWalk = Now - TimeSpan.FromSeconds(1);

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial with { LastWalkUtc = lastWalk },
            Now, CancellationToken.None);

        Assert.Equal(lastWalk, step.Next.LastWalkUtc);
    }

    // --- the shape change ----------------------------------------------------

    // Rows came back and none carried a kind this version knows. Reported as the
    // filter having stopped matching, never as "no cloud sessions".
    [Fact]
    public async Task AllUnknownKindsReachTheStatusAsAShapeChange()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[]
        {
            Row("session_a", kind: "managed_runtime"),
            Row("session_b", kind: "managed_runtime"),
        })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Contains("filter may no longer match", step.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("no cloud sessions", step.Status, StringComparison.Ordinal);
    }

    // --- the canary ----------------------------------------------------------

    // **The token reaches no status string, ever.** Fed through every arm this
    // test can reach: a good walk, a shape change, each refusal, and the halted
    // no-op. The canary is chosen to be findable if it ever leaked.
    [Fact]
    public async Task TheTokenNeverReachesAStatusString()
    {
        var bodies = new[]
        {
            Envelope(new[] { Row("session_a") }),
            Envelope(new[] { Row("session_a").Replace("a session", Token, StringComparison.Ordinal) }),
            "<html>",
            "{\"error\":\"" + Token + "\"}",
        };

        foreach (var body in bodies)
        {
            foreach (var status in new[] { 200, 400, 401, 403, 429, 500 })
            {
                var api = new FakeApi(_ => status == 200
                    ? new CloudApiResult(CloudOutcomes.OutcomeFor(200, body), body)
                    : new CloudApiResult(CloudOutcomes.OutcomeFor(status, body), null));

                var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
                    ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

                Assert.DoesNotContain(Token, step.Status, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, step.Next.Status, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, step.Next.CredentialStamp ?? "", StringComparison.Ordinal);
            }
        }
    }

    // The stamp is a change detector and never secret bytes. Asserted here as well
    // as in the credential tests because this is where it is carried around.
    [Fact]
    public async Task TheStampCarriedThroughTheArmIsWhateverTheSourceGaveAndNotATokens()
    {
        var api = new FakeApi(_ => Ok(Envelope(Array.Empty<string>())));

        var step = await ClaudeCloudSessions.StepAsync(api,
            new FakeCredentials { StampValue = "638000000000000000" },
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None);

        Assert.Equal("638000000000000000", step.Next.CredentialStamp);
    }

    // --- the store that never answers ----------------------------------------

    // **The regression that matters.** Before the budget existed, this call did
    // not return: StepAsync blocked inside the credential read, RunAsync's thread
    // parked, StatusText stayed on "checking…" and the user got no orbs and no
    // error. Silent, and indistinguishable from having no cloud sessions — the
    // worst failure shape this repository has.
    [Fact]
    public async Task AStoreThatNeverAnswersDoesNotParkTheArm()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        using var credentials = new HangingCredentials();

        // **Raced against a wall clock, on a thread of its own.** Two deliberate
        // choices, and the second one was learned the hard way.
        //
        // Racing rather than awaiting, because the regression this test exists for
        // would otherwise show up as a CI leg that hangs until the runner's own
        // timeout kills it — no failure, no message, nothing naming the test. A
        // regression should fail in a second and say which assertion it was.
        //
        // **And Task.Run, because racing alone was not enough.** An async method
        // runs synchronously up to its first real await, and in the un-budgeted
        // version the credential read happens before any await — so
        // `StepAsync(...)` blocked on the *calling* thread and never got as far as
        // returning a Task to race. The control run proved it: with the budget
        // removed, this test hung rather than failing, which is precisely the
        // failure shape it was written to rule out. Verified afterwards by
        // removing the budget again and watching it fail in ten seconds.
        var (budget, expire) = TestBudgets.Manual();
        var step = Task.Run(() => ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None,
            readBudget: budget));

        // Expired by hand once the read is actually running: an expiry that beat
        // the pool to it would leave Reads at 0 for the assertion below (CB-269).
        await credentials.Entered.Task;
        expire.SetResult();

        var finished = await Task.WhenAny(step, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, step),
            "StepAsync did not return: the credential read parked the arm, which is the "
            + "exact failure the read budget exists to prevent.");

        await step;
        Assert.Empty(api.Paths);
        Assert.Equal(1, credentials.Reads);
    }

    // And it says what happened, in words that describe the observation rather
    // than assert a mechanism nobody has proven.
    [Fact]
    public async Task AStoreThatNeverAnswersSaysSoOnTheStatusRow()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        using var credentials = new HangingCredentials();

        var step = await ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None,
            readBudget: TestBudgets.ExpiresAtOnce);

        Assert.Equal(ClaudeCliCredentials.Describe(CredentialOutcome.NoAnswer), step.Status);
        Assert.Contains("did not answer", step.Status, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(step.Next.Status));
    }

    // **And it does not retry.** A call that may be blocked waiting on a human is
    // the definition of what must not be re-issued on a timer, and every attempt
    // abandons a thread inside a P/Invoke that cannot be cancelled. So it halts,
    // exactly as Denied does, and comes back only when the stamp moves or the user
    // asks again.
    [Fact]
    public async Task AStoreThatNeverAnswersHaltsRatherThanSchedulingARetry()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        using var credentials = new HangingCredentials();

        var step = await ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None,
            readBudget: TestBudgets.ExpiresAtOnce);

        Assert.True(step.Next.Halted);
        Assert.Null(step.Next.Backoff);

        // The stamp-recheck cadence, which reads no secret and opens no socket —
        // not a retry of the thing that hung.
        Assert.Equal(ClaudeCloudSessions.HaltedRecheckInterval, step.Wait);
        Assert.True(step.Wait >= ClaudeCloudSessions.HaltedRecheckInterval);
    }

    // The halt is a real halt: a second tick at the same stamp reads nothing at
    // all, so the hung call is not made twice.
    [Fact]
    public async Task AHaltedArmDoesNotReachTheStoreASecondTime()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        using var credentials = new HangingCredentials();

        var (budget, expire) = TestBudgets.Manual();
        var firstStep = ClaudeCloudSessions.StepAsync(api, credentials,
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None,
            readBudget: budget);

        // The budget runs out only once the read has started, so "one read" is a
        // count and not a race with the pool.
        await credentials.Entered.Task;
        expire.SetResult();
        var first = await firstStep;

        var second = await ClaudeCloudSessions.StepAsync(api, credentials, first.Next, Now,
            CancellationToken.None, readBudget: TestBudgets.ExpiresAtOnce);

        Assert.Equal(1, credentials.Reads);
        Assert.Equal(2, credentials.Stamps);
        Assert.Null(second.Snapshot);
        Assert.True(second.Next.Halted);
    }

    // Backoff agrees, which is where the "no timer" half is actually enforced.
    [Fact]
    public void NoAnswerIsAStopAndNotADelay()
    {
        Assert.Null(Backoff.Next(CredentialOutcome.NoAnswer, null));
        Assert.Null(Backoff.Next(CredentialOutcome.NoAnswer, TimeSpan.FromSeconds(2)));
    }

    // The budget is generous rather than snappy, because the only thing a longer
    // wait waits for is a person reading a consent dialog.
    [Fact]
    public void TheReadBudgetIsAPositiveUnmeasuredPlaceholder()
    {
        Assert.True(ClaudeCliCredentials.UnmeasuredReadBudget > TimeSpan.Zero);
        Assert.True(ClaudeCliCredentials.UnmeasuredReadBudget >= TimeSpan.FromSeconds(30));
    }

    // The arm races its read against the budget it was handed and no other.
    [Fact]
    public async Task TheStepReadsThroughTheBudgetItWasHanded()
    {
        var (budget, calls) = TestBudgets.Spy();
        var credentials = new FakeCredentials();
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        await ClaudeCloudSessions.StepAsync(api, credentials, ClaudeCloudSessions.ArmState.Initial,
            Now, CancellationToken.None, readBudget: budget);

        Assert.Equal(1, calls());
    }

    // A read that returns inside its budget is completely unaffected — the
    // negative control, without which every test above is equally consistent with
    // the budget having broken the ordinary path.
    [Fact]
    public async Task AStoreThatAnswersInTimeIsUntouchedByTheBudget()
    {
        var api = new FakeApi(_ => Ok(Envelope(new[] { Row("session_a") })));

        var step = await ClaudeCloudSessions.StepAsync(api, new FakeCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None,
            readBudget: TestBudgets.AlwaysAnswers);

        Assert.False(step.Next.Halted);
        Assert.Single(step.Snapshot!);
    }

    // A source that throws is a bug in the source, not a crash in the arm — and
    // the exception's own text does not reach the screen. This is the one call
    // site in the app holding a secret; nothing that came out of it is rendered.
    [Fact]
    public async Task AStoreThatThrowsIsSurvivedAndItsTextIsNotShown()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));

        var step = await ClaudeCloudSessions.StepAsync(api, new ThrowingCredentials(),
            ClaudeCloudSessions.ArmState.Initial, Now, CancellationToken.None,
            readBudget: TestBudgets.AlwaysAnswers);

        Assert.Empty(api.Paths);
        Assert.DoesNotContain("SENSITIVE-EXCEPTION-TEXT", step.Status, StringComparison.Ordinal);
        Assert.Equal(ClaudeCliCredentials.Describe(CredentialOutcome.Unreadable), step.Status);
    }

    // Cancellation is cancellation, not a timeout. A feature switched off while a
    // read is in flight must not publish "the Keychain did not answer".
    [Fact]
    public async Task CancellingWhileAReadIsInFlightThrowsRatherThanReportingNoAnswer()
    {
        using var credentials = new HangingCredentials();
        using var cts = new CancellationTokenSource();

        var pending = ClaudeCliCredentials.ReadWithinAsync(credentials,
            TimeSpan.FromMinutes(5), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // --- the initial state ---------------------------------------------------

    [Fact]
    public void TheInitialStateIsEmptyAndNotHalted()
    {
        var initial = ClaudeCloudSessions.ArmState.Initial;

        Assert.Empty(initial.Sessions);
        Assert.False(initial.Halted);
        Assert.Null(initial.Backoff);
        Assert.Null(initial.CredentialStamp);
        Assert.Equal(default, initial.LastWalkUtc);
    }
}
