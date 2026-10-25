using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Claude Code sessions running in Anthropic's cloud, listed by the account
    // API rather than by a hook.
    //
    // Shaped after OpenClawSessions deliberately, because the two arms answer
    // the same question: what is running somewhere this app cannot see, and
    // what state is it in. A static class with a volatile snapshot rather than
    // an interface, for the reason that file gives — a second implementation
    // that is permanently off is ceremony, and "off" here means Snapshot()
    // returns nothing.
    //
    // ## What this reads, and the one thing it is careful about
    //
    // GET https://api.anthropic.com/v2/ccr-sessions, with the Claude Code CLI's
    // own OAuth token. Two headers: Authorization and anthropic-version. That
    // host serves ordinary HTTP clients; claude.ai does not — it answers a
    // non-browser client with a Cloudflare challenge, identically for a real
    // token and an invalid one, which is how the first attempt at this wasted
    // an afternoon. See docs/claude-cloud-findings.md.
    //
    // **The roster is mostly not cloud sessions.** Measured on a real account:
    // 578 rows, of which 573 were `environment_kind: bridge` — the user's own
    // *local* sessions, registered for remote control, which Orbweaver
    // already draws orbs for from its hooks. Drawing those again would double
    // every local orb. Five rows were genuinely `anthropic_cloud`, one of them
    // not archived. So the filter is not a nicety; it is the difference between
    // this feature and a bug report.
    //
    // ## Where the decisions live
    //
    // Almost nowhere in this file. Reading the payload, filtering it, naming a
    // state and wording the status line are all ClaudeCloudRoster's, and
    // everything this arm decides per tick — which credential state stops it,
    // which cycle is due, what to publish when a fetch fails halfway — is in
    // StepAsync, which takes its API, its credential source and its clock as
    // arguments. What is left in RunAsync is a `while` and a `Task.Delay`.
    internal static class ClaudeCloudSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole, like OpenClawSessions' own: the
        // scan reads it on the UI thread and never locks anything else.
        private static volatile IReadOnlyList<Session> _snapshot = Array.Empty<Session>();

        private static string _state = "off";

        private static CancellationTokenSource? _cts;

        // One row the orb layer can draw without knowing anything about JSON.
        //
        // LastActivity is the session's own `updated_at`, never the time of the
        // read. SessionManager stamps ScanEntry.Written from it, and stamping
        // "now" instead would give every session the account has ever had a
        // permanent orb — the same trap OpenClaw's mapping documents.
        //
        // Url is carried rather than built at the click, because the payload's
        // own `session_url` is empty on every row measured and the id is what
        // the address is actually made of.
        //
        // ConnectionStatus is the payload's raw string, added by CB-199 as an
        // optional trailing field so the many places that build a Session
        // positionally did not all have to change. Busy is decided by
        // ClaudeCloudRoster.IsBusy from State and StatusBucket.
        internal sealed record Session(
            string Id,
            string Title,
            string State,
            DateTime LastActivity,
            string Url,
            string StatusBucket,
            bool NeedsAction,
            string? Model,
            int? ContextPercent,
            string? StatusDetail,
            string? RecentAction,
            string? ConnectionStatus = null,
            string? OwnerRoot = null);

        // The settings gate lives here rather than in SessionManager.EnabledFor,
        // following OpenClaw: off means the app asks the OS for no credential
        // and opens no socket, which is stronger than "draws no orb" and is the
        // promise the settings copy makes.
        public static IReadOnlyList<Session> Snapshot() =>
            OrbweaverSettings.ClaudeCloudEnabled
                ? WithoutTombstones(_snapshot, TombstonesNow(DateTime.UtcNow))
                : Array.Empty<Session>();

        // --- archived or deleted from here (CB-225) ------------------------------

        // Sessions this app has just archived or deleted, each with the moment
        // it stops being hidden.
        //
        // **Why the orb goes at once rather than at the next roster read.** The
        // roster already drops an archived row (Keep) and a deleted one (it is
        // simply absent), so the orb would go by itself — after up to a short
        // cycle, during which a person who has just watched "Archived" appear
        // on the row is looking at the orb they archived. That reads as the
        // archive having failed. So a success hides the id here, and the next
        // roster publish that no longer lists it clears it (Publish).
        //
        // **Bounded rather than permanent**, for the case where the roster
        // still lists it: then something disagrees with the 2xx, and the honest
        // answer after a while is to show the orb again rather than to hide a
        // session the account still has. Two minutes is the hold the Claude
        // Code CLI's own fleet view gives an archived row for the same reason
        // (read out of the 2.1.288 binary, not measured).
        private static readonly Dictionary<string, DateTime> _tombstones = new(StringComparer.Ordinal);

        internal static readonly TimeSpan TombstoneHold = TimeSpan.FromMinutes(2);

        internal static void Tombstone(string id, DateTime now)
        {
            lock (Gate) _tombstones[id] = now + TombstoneHold;
        }

        // The ids still hidden at `now`, dropping any whose hold has run out.
        internal static IReadOnlySet<string> TombstonesNow(DateTime now)
        {
            lock (Gate)
            {
                foreach (var expired in _tombstones.Where(t => t.Value <= now).Select(t => t.Key).ToList())
                    _tombstones.Remove(expired);

                return _tombstones.Count == 0
                    ? EmptyIds
                    : new HashSet<string>(_tombstones.Keys, StringComparer.Ordinal);
            }
        }

        private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>();

        internal static IReadOnlyList<Session> WithoutTombstones(IReadOnlyList<Session> sessions,
            IReadOnlySet<string> hidden) =>
            hidden.Count == 0 ? sessions : sessions.Where(s => !hidden.Contains(s.Id)).ToList();

        // A roster read landing: publish it, and let go of every tombstone the
        // roster now agrees with — the session is no longer listed, so there is
        // nothing left to hide.
        internal static void Publish(IReadOnlyList<Session> sessions)
        {
            _snapshot = sessions;

            lock (Gate)
            {
                if (_tombstones.Count == 0) return;
                var listed = new HashSet<string>(sessions.Select(s => s.Id), StringComparer.Ordinal);
                foreach (var settled in _tombstones.Keys.Where(id => !listed.Contains(id)).ToList())
                    _tombstones.Remove(settled);
            }
        }

        internal static void ClearTombstonesForTests()
        {
            lock (Gate) _tombstones.Clear();
        }

        internal static bool IsTombstoned(string id)
        {
            lock (Gate) return _tombstones.ContainsKey(id);
        }

        // Where the action goes, and as whom. Settable so a test can hand in a
        // fake endpoint and login instead of api.anthropic.com and the
        // Keychain; production builds the real client once, on first use.
        internal static ICloudApi LifecycleApi
        {
            get { lock (Gate) return _lifecycleApi ??= new HttpCloudApi(); }
            set { lock (Gate) _lifecycleApi = value; }
        }

        private static ICloudApi? _lifecycleApi;

        internal static Func<string?, ICloudCredentialSource> LifecycleCredentials { get; set; } =
            CloudAccounts.SourceFor;

        internal const string SwitchedOffDetail = "cloud sessions are switched off in Settings";
        internal const string NotListedDetail = "this session is no longer in the list";
        internal const string InFlightDetail = "a request for this session is already on its way";

        // Ids with an archive or delete out. The menu already ignores clicks
        // while its row is working; this is the same promise kept one layer
        // down, so two callers — two orbs' menus, a menu and anything later —
        // cannot send two requests for one session, the second of which would
        // answer 404 and print "not found" on a row that is going away.
        private static readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

        // Archive or delete the cloud session behind one orb.
        //
        // **As the account that owns it** (CB-221): the row's OwnerRoot picks
        // the login, never "whichever is first" — a session can only be
        // archived by its own account, and the default login would be refused.
        //
        // On success the orb is hidden at once (Tombstone) and an open chat
        // panel on it flips read-only on the next scan, which pushes it a
        // missing row — the Gone path CB-199 built for a vanished session.
        //
        // The row is looked up in the unfiltered snapshot, so a session that
        // has just been tombstoned can still be asked about — deleting one
        // that was archived a moment ago is a real request. What stops the
        // same request twice is the in-flight set, not the lookup.
        internal static async Task<CloudLifecycleResult> RunLifecycleAsync(CloudLifecycleAction action,
            string? orbKey, CancellationToken ct)
        {
            if (!OrbweaverSettings.ClaudeCloudEnabled)
                return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, SwitchedOffDetail);

            var id = CloudOrbActions.IdFromKey(orbKey);
            var row = id is null ? null : _snapshot.FirstOrDefault(s => s.Id == id);
            if (row is null)
                return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, NotListedDetail);

            lock (Gate)
            {
                if (!_inFlight.Add(row.Id))
                    return new CloudLifecycleResult(CloudLifecycleVerdict.Refused, InFlightDetail);
            }

            try
            {
                var result = await ClaudeCloudLifecycle
                    .RunAsync(LifecycleApi, LifecycleCredentials(row.OwnerRoot), action, row.Id, ct)
                    .ConfigureAwait(false);

                if (result.Succeeded) Tombstone(row.Id, DateTime.UtcNow);
                return result;
            }
            finally
            {
                lock (Gate) _inFlight.Remove(row.Id);
            }
        }

        // What the settings window shows on its status row.
        public static string StatusText
        {
            get { lock (Gate) return _board is { } board ? board.StatusText : _state; }
        }

        private static CloudAccountBoard? _board;

        // The seam every downstream test uses, matching
        // OpenClawSessions.SetSnapshotForTests: the poll loop is the only
        // production publisher and is excluded from coverage, so without this
        // every orb this feature draws would be unreachable from a test.
        internal static void SetSnapshotForTests(IReadOnlyList<Session> sessions)
        {
            _snapshot = sessions;
        }

        internal static void SetStateForTests(string state)
        {
            lock (Gate)
            {
                _board = null;
                _state = state;
            }
        }

        internal static void SetBoardForTests(CloudAccountBoard? board)
        {
            lock (Gate) _board = board;
        }

        // How long a halted arm waits before looking again.
        //
        // It is not a retry. A halted arm sends no request and reads no secret —
        // all it does is call Stamp(), which on macOS is an attributes-only
        // Keychain query that returns no data and therefore is **not** the query
        // the consent prompt guards, and on Windows and Linux is a file mtime.
        // So this cadence costs nothing a user can perceive, and it is what makes
        // "sign in again and the orbs come back on their own" true without ever
        // producing a second prompt.
        internal static readonly TimeSpan HaltedRecheckInterval = TimeSpan.FromSeconds(30);

        // --- the arm, as a value ----------------------------------------------

        // Everything one tick needs to remember from the last one.
        //
        // A record passed in and out rather than fields on this class, for the
        // reason ArrangementTests gives about OrbArrangement: a decision that
        // reads its inputs out of statics can only be tested by arranging the
        // statics, and a suite that arranges process-wide state is a suite whose
        // cases interfere with each other.
        //
        // `Halted` is the credential-and-refusal stop. It is not a long backoff:
        // CB-164 requires that a refused or absent login produces no cloud orbs,
        // a legible reason and **no repeated OS prompt**, and only a distinct
        // stop state can promise that. It clears when `Stamp()` moves, which is
        // the user having signed in again or re-approved us.
        internal sealed record ArmState(
            IReadOnlyList<Session> Sessions,
            string Status,
            string? CredentialStamp,
            bool Halted,
            DateTime LastWalkUtc,
            TimeSpan? Backoff)
        {
            internal static ArmState Initial { get; } = new(
                Array.Empty<Session>(), "checking…", null, false, default, null);
        }

        // What one tick decided.
        //
        // **Snapshot is nullable and null is the interesting value.** It means
        // "publish nothing; leave what is on screen alone", and it exists for one
        // case in particular: a walk that failed on page four must not replace a
        // good roster with an empty one. Orbs vanishing because a request timed
        // out would read as sessions having ended, which is a lie the app would be
        // telling on the strength of a network hiccup.
        //
        // An empty list, by contrast, is a real answer — "we have access and there
        // is nothing" or "we have no access at all" — and does clear the orbs.
        //
        // RateLimited exists for the accounts above this arm: one account's 429
        // backs all of them off.
        internal sealed record StepResult(
            ArmState Next,
            IReadOnlyList<Session>? Snapshot,
            string Status,
            TimeSpan Wait,
            bool RateLimited = false);

        // One tick. Every decision this arm makes is here.
        //
        // `readBudget` is how long the credential read is given before the arm
        // gives up on it — a parameter rather than a constant read inside so a
        // test can drive the give-up path in milliseconds instead of waiting
        // three quarters of a minute. Production passes nothing and gets
        // ClaudeCliCredentials.UnmeasuredReadBudget.
        internal static async Task<StepResult> StepAsync(
            ICloudApi api,
            ICloudCredentialSource credentials,
            ArmState state,
            DateTime now,
            CancellationToken ct,
            CredentialBudget? readBudget = null)
        {
            var stamp = credentials.Stamp();

            // Halted, and nothing about the stored credential has changed. Do not
            // read the secret, do not open a socket, do not prompt.
            if (state.Halted && string.Equals(stamp, state.CredentialStamp, StringComparison.Ordinal))
            {
                return new StepResult(state, null, state.Status, HaltedRecheckInterval);
            }

            // **Never `credentials.Read()` directly.** On macOS that is a P/Invoke
            // into Security.framework which, in a context with no window server
            // session, was measured not to return at all — at 30 seconds and at 60.
            // Called on this thread it parks the supervisor forever, leaving the
            // status on "checking…" and the user with no orbs and no error, which
            // is indistinguishable from having no cloud sessions. The budget is the
            // only thing standing between that measurement and a silent app.
            //
            // A gated source (one per account, see CloudAccountSource) is read one
            // at a time across the whole process, and chooses what to read only
            // once it holds the gate. That happens inside ReadWithinAsync.
            var read = await ClaudeCliCredentials.ReadWithinAsync(
                credentials, readBudget ?? CredentialBudget.Unmeasured, ct)
                .ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                var wait = Backoff.Next(read.Outcome, state.Backoff);
                return Stop(state, stamp, ClaudeCliCredentials.StatusFor(read), wait);
            }

            var plan = ClaudeCloudRoster.PlanFor(state.LastWalkUtc, now, state.Sessions);

            return plan.DeepWalk
                ? await WalkAsync(api, state, stamp, token, now, ct).ConfigureAwait(false)
                : await RefreshAsync(api, state, stamp, token, plan, ct).ConfigureAwait(false);
        }

        // The deep cycle: page the whole roster, capped.
        private static async Task<StepResult> WalkAsync(ICloudApi api, ArmState state,
            string? stamp, string token, DateTime now, CancellationToken ct)
        {
            var pages = new List<ClaudeCloudRoster.Page>();
            string? after = null;

            for (var i = 0; i < CloudRequest.MaxPagesPerWalk; i++)
            {
                var result = await api.SendAsync(
                    new CloudRequestContext(token, CloudRequest.ListPath(CloudRequest.MaxPageSize, after)),
                    ct).ConfigureAwait(false);

                if (result.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    return Failed(state, stamp, result.Outcome);
                }

                var page = ClaudeCloudRoster.ParsePage(result.Body);
                pages.Add(page);

                if (!page.HasMore || page.LastId is null)
                {
                    after = null;
                    break;
                }

                after = page.LastId;
            }

            // Still somewhere to go when the cap ran out. Kept, marked, and said
            // out loud by Describe — never silently fewer.
            var reduction = ClaudeCloudRoster.Reduce(pages, after is not null);
            var status = ClaudeCloudRoster.Describe(reduction);

            return new StepResult(
                state with
                {
                    Sessions = reduction.Sessions,
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = false,
                    Backoff = null,
                    LastWalkUtc = now,
                },
                reduction.Sessions,
                status,
                CloudRequest.UnmeasuredFirstPageInterval);
        }

        // The short cycle: page one, then one direct read per session already
        // known. See ClaudeCloudRoster.Plan for why the second half is not
        // redundant with the first.
        private static async Task<StepResult> RefreshAsync(ICloudApi api, ArmState state,
            string? stamp, string token, ClaudeCloudRoster.Plan plan, CancellationToken ct)
        {
            var first = await api.SendAsync(
                new CloudRequestContext(token, CloudRequest.ListPath(CloudRequest.MaxPageSize, null)),
                ct).ConfigureAwait(false);

            if (first.Outcome.Kind != CloudOutcomeKind.Ok)
            {
                return Failed(state, stamp, first.Outcome);
            }

            var page = ClaudeCloudRoster.ParsePage(first.Body);

            var resolved = new HashSet<string>(page.Rows.Select(r => r.Id), StringComparer.Ordinal);
            var fresh = new List<Session>();

            foreach (var row in page.Rows)
            {
                if (!ClaudeCloudRoster.Keep(row)) continue;
                if (ClaudeCloudRoster.ToSession(row) is { } session) fresh.Add(session);
            }

            foreach (var id in plan.RefreshIds)
            {
                if (resolved.Contains(id)) continue;

                var one = await api.SendAsync(
                    new CloudRequestContext(token, CloudRequest.SessionPath(id)), ct)
                    .ConfigureAwait(false);

                if (one.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    // A 404 on one session is about that session and nothing
                    // else, and it is a definite answer: the session has been
                    // deleted. So it is *resolved* — Merge drops its orb now,
                    // rather than at the next deep walk up to five minutes on —
                    // and the cycle carries on. It must not reach the check
                    // below: Backoff stops on SessionGone, and that would halt
                    // the whole arm, every cloud orb gone until the credential
                    // changed, because one session was deleted.
                    if (one.Outcome.Kind == CloudOutcomeKind.SessionGone)
                    {
                        resolved.Add(id);
                        continue;
                    }

                    // A refusal that would stop the arm stops it here too — there
                    // is no point walking the rest of the list to be refused eight
                    // more times. Anything retryable is treated as **no news about
                    // this session**: its orb survives on what the last deep walk
                    // saw, and the next walk is the authority that will drop it if
                    // it has really gone. A 404 is deliberately in that group; that
                    // it means "ended" rather than "briefly unreachable" is not
                    // something anybody has measured.
                    if (Backoff.Next(one.Outcome, state.Backoff) is null)
                    {
                        return Failed(state, stamp, one.Outcome);
                    }

                    continue;
                }

                if (ClaudeCloudRoster.ParseSession(one.Body) is not { } row) continue;

                resolved.Add(row.Id);
                if (!ClaudeCloudRoster.Keep(row)) continue;
                if (ClaudeCloudRoster.ToSession(row) is { } session) fresh.Add(session);
            }

            var merged = ClaudeCloudRoster.Merge(state.Sessions, fresh, resolved);

            // The reduction describes what *page one* saw — the inspected count
            // and the kind histogram are about the rows actually looked at — while
            // the sessions are the merged set, which includes ones this cycle never
            // asked about. Two different populations in one sentence, and that is
            // right: the counts are a claim about the fetch and the list is a claim
            // about what is running.
            var reduction = ClaudeCloudRoster.Reduce(new[] { page }, false) with
            {
                Sessions = merged,
            };

            var status = ClaudeCloudRoster.Describe(reduction);

            return new StepResult(
                state with
                {
                    Sessions = merged,
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = false,
                    Backoff = null,
                },
                merged,
                status,
                CloudRequest.UnmeasuredFirstPageInterval);
        }

        // A request that came back wrong.
        //
        // The split is the whole point. A verdict Backoff stops on — the token was
        // refused, we are blocked — is a definite statement that there is no access,
        // so the orbs go and the arm halts until the credential changes. Anything
        // retryable leaves the orbs exactly where they are and publishes nothing.
        private static StepResult Failed(ArmState state, string? stamp, CloudOutcome outcome)
        {
            outcome = RosterView(outcome);

            var wait = Backoff.Next(outcome, state.Backoff);
            var status = outcome.Detail ?? $"the endpoint answered {outcome.Status}";

            if (wait is null) return Stop(state, stamp, status, null);

            return new StepResult(
                state with
                {
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = false,
                    Backoff = wait,
                },
                null,
                status,
                wait.Value,
                RateLimited: outcome.Kind == CloudOutcomeKind.RateLimited);
        }

        // What a failure on a *roster* request means.
        //
        // Every request Failed sees is the listing, page one of it, or a single
        // session read whose refusal would stop the arm — and a 404 on the
        // listing is the collection having moved, not a session having gone.
        // OutcomeFor cannot see the path and calls every 404 SessionGone, which
        // Backoff stops on; left alone, that would halt the arm and put "this
        // cloud session no longer exists" on the status line for a problem with
        // the endpoint. So on this path a 404 is what it was before CB-199:
        // retryable, and described by its status.
        internal static CloudOutcome RosterView(CloudOutcome outcome) =>
            outcome.Kind == CloudOutcomeKind.SessionGone
                ? outcome with
                {
                    Kind = CloudOutcomeKind.Unavailable,
                    Detail = $"the endpoint answered {outcome.Status}",
                }
                : outcome;

        // A stop, or a retryable credential problem, in one shape.
        private static StepResult Stop(ArmState state, string? stamp, string status, TimeSpan? wait)
        {
            if (wait is { } retry)
            {
                return new StepResult(
                    state with { Status = status, CredentialStamp = stamp, Halted = false, Backoff = retry },
                    null,
                    status,
                    retry);
            }

            return new StepResult(
                state with
                {
                    Sessions = Array.Empty<Session>(),
                    Status = status,
                    CredentialStamp = stamp,
                    Halted = true,
                    Backoff = null,
                },
                Array.Empty<Session>(),
                status,
                HaltedRecheckInterval);
        }

        // --- the loop ---------------------------------------------------------

        // Excluded from coverage: a `while` around StepAsync and a `Task.Delay`.
        // Everything it would be worth testing is in StepAsync and
        // CloudAccountBoard, which is why this is as short as it is. One of these
        // runs per account, so one account's hung read or refused token never
        // stalls another's.
        [ExcludeFromCodeCoverage]
        private static async Task RunAccountAsync(ICloudApi api, CloudAccountBoard board,
            CloudAccount account, TimeSpan stagger, CancellationToken ct)
        {
            var state = ArmState.Initial;

            try { await Task.Delay(stagger, ct).ConfigureAwait(false); } catch { return; }

            while (!ct.IsCancellationRequested)
            {
                if (board.HoldRemaining(DateTime.UtcNow) is { } hold && hold > TimeSpan.Zero)
                {
                    try { await Task.Delay(hold, ct).ConfigureAwait(false); } catch { break; }
                }

                StepResult step;
                try
                {
                    step = await StepAsync(api, CloudAccounts.GatedFor(account),
                            state, DateTime.UtcNow, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Nothing below this is allowed to take the app down. The arm
                    // keeps its sessions and says what happened.
                    state = state with { Status = ex.Message, Backoff = Backoff.Cap };
                    board.ApplyError(account.Root, ex.Message);
                    try { await Task.Delay(Backoff.Cap, ct).ConfigureAwait(false); } catch { break; }
                    continue;
                }

                state = step.Next;
                Publish(board.Apply(account.Root, step, DateTime.UtcNow));

                try { await Task.Delay(step.Wait, ct).ConfigureAwait(false); } catch { break; }
            }
        }

        // Excluded from coverage: starts the poll loops that talk to the real
        // endpoint, and on macOS is the path that can raise a Keychain prompt.
        [ExcludeFromCodeCoverage]
        public static void Restart()
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _cts = null;
                _board = null;
                _snapshot = Array.Empty<Session>();
                _tombstones.Clear();

                if (!OrbweaverSettings.ClaudeCloudEnabled)
                {
                    _state = "off";
                    return;
                }

                _state = "checking…";

                var cts = new CancellationTokenSource();
                _cts = cts;

                var api = new HttpCloudApi();
                var accounts = CloudAccounts.Rebuild();
                var board = new CloudAccountBoard(accounts, CloudAccounts.Coordinator);
                _board = board;

                for (var i = 0; i < accounts.Count; i++)
                {
                    var account = accounts[i];
                    var stagger = TimeSpan.FromSeconds(3 * i);
                    _ = Task.Run(() => RunAccountAsync(api, board, account, stagger, cts.Token),
                        cts.Token);
                }
            }
        }
    }
}
