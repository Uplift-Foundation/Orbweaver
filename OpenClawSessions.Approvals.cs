using System.Text.Json;
using Avalonia.Threading;

namespace Orbweaver
{
    // How an approval ended, as far as this app can tell, for a panel that did not
    // answer it itself. Drained by TakeEnded so each ending is written up once.
    //   resolved: another reviewer, or the gateway, decided it (Decision, Reason,
    //             ResolvedBy and Status are what the event carried: Status is one of
    //             allowed, denied, expired or cancelled);
    //   expired:  the poll found it gone after its expiry, or its expiry passed;
    //   gone:     the poll found it gone before its expiry and nothing said why;
    //   evicted:  there were too many pending and this was the oldest, so it is no
    //             longer shown here (the gateway still holds it and it can be
    //             answered there).
    internal sealed record OpenClawApprovalEnded(
        OpenClawApproval Approval, string Cause, string? Decision, string? Reason, string? ResolvedBy,
        string? Status = null);

    internal enum OpenClawApprovalResult
    {
        // This call decided it.
        Answered,

        // The gateway answered applied:false: another reviewer decided first.
        DecidedElsewhere,

        // Not pending: either this app does not hold it (it ended, was evicted or
        // expired while the click was in flight, or was never shown), or the gateway
        // said it is not found. The gateway deliberately does not say which of
        // resolved, expired or not visible it is, so neither does this.
        NoLongerPending,

        // The same id was already being answered or already answered.
        Duplicate,

        // Nothing was decided and the record is kept: not connected, a refusal that
        // was not "not found", a transport failure, an answer outside the two
        // offered or ruled out for this request, or a request that can only be
        // denied here.
        Failed,
    }

    internal sealed record OpenClawApprovalOutcome(
        OpenClawApprovalResult Result, string? Decision, string? Status, string? Reason, string? Detail);

    // CB-270 Stage 6: the gateway side of approvals. Pending approvals per session
    // key, what turns an orb amber, how the list poll and the broadcasts feed it,
    // and the one method that answers one.
    //
    // **Nothing in this file can answer an approval except ResolveApprovalAsync,
    // and nothing calls that but a click.** The string "approval.resolve" appears
    // here once. Not the poll, not an event handler, not a reconnect, not a
    // setting changing. A test asserts the single site, another scans every request
    // this app sends for a method name that could be it however it is built, and
    // another replays a whole fixture set through every path and counts the resolve
    // frames sent.
    internal static partial class OpenClawSessions
    {
        // Pending approvals by the session they belong to, in the order they arrived.
        // Replaced per kind by the poll and added to and removed from by the
        // broadcasts. Guarded by Gate.
        private static readonly Dictionary<string, List<OpenClawApproval>> Pending =
            new(StringComparer.Ordinal);

        // id -> where it is and when it arrived, so a record is found, de-duplicated
        // and evicted without scanning every session under the lock.
        private static readonly Dictionary<string, (string Key, long Seq)> ById =
            new(StringComparer.Ordinal);

        private static long _arrival;

        // Endings nobody has been told about yet, by session key.
        private static readonly Dictionary<string, List<OpenClawApprovalEnded>> Ended =
            new(StringComparer.Ordinal);

        // Ids with an answer in flight or already given that the gateway took. An id
        // goes in the moment a click lands, so a second click, or a click racing a
        // broadcast, can never send a second request for it. It stays in once the
        // gateway applied the answer (or said another reviewer had), and a record
        // that comes back for it is not re-added: the prompt would be unanswerable.
        // It comes out again if the answer failed, or if the gateway said the id was
        // not found, so a real approval is never stranded behind a spent id.
        private static readonly HashSet<string> Answered = new(StringComparer.Ordinal);

        // Ids learned from the session-scoped path (a `session.approval` event or a
        // subscribe's replay) rather than from the lists. The list poll must not
        // drop these just because it cannot see them: the whole reason the session
        // path exists is that a device can be shown an approval through it that the
        // lists' visibility rules would not show. They live until a session event,
        // a replay, an expiry or a restart ends them, or until the panel that
        // subscribed closes, when they go back to being the list's to judge.
        private static readonly HashSet<string> SessionSourced = new(StringComparer.Ordinal);

        private const int MaxEndedPerSession = 20;
        private const int MaxAnswered = 1024;

        // The pending approvals for one session, in the order they arrived with the
        // oldest first, expired ones left out. Never ordered by the gateway's own
        // dates: a record carrying an old date must not be able to pin itself first.
        // Expiry is enforced here as well as by the gateway, against the clamped
        // local expiry, so a prompt cannot outlive its record if every signal is
        // missed.
        internal static IReadOnlyList<OpenClawApproval> PendingFor(string key, DateTimeOffset? now = null)
        {
            var at = now ?? DateTimeOffset.UtcNow;

            lock (Gate)
            {
                return Pending.TryGetValue(key, out var list)
                    ? list.Where(a => !OpenClawApprovals.IsExpired(a, at)).ToList()
                    : Array.Empty<OpenClawApproval>();
            }
        }

        // The orb's state reads this: a session with an unanswered approval is
        // waiting on a person, not working. Caller holds Gate or does not; the lock
        // is re-entrant.
        private static bool HasPending(string key, DateTimeOffset now)
        {
            lock (Gate)
            {
                return Pending.TryGetValue(key, out var list)
                       && list.Any(a => !OpenClawApprovals.IsExpired(a, now));
            }
        }

        // What ended since the panel last asked, for this key, oldest first.
        internal static IReadOnlyList<OpenClawApprovalEnded> TakeEnded(string key)
        {
            lock (Gate)
            {
                if (!Ended.Remove(key, out var list)) return Array.Empty<OpenClawApprovalEnded>();
                return list;
            }
        }

        // A test seam: forgets every approval-related thing this class holds.
        // Restart forgets the pending set but not the rest, and the test bootstrap
        // calls this around every test so one class's approvals cannot reach the
        // next, whichever class that is.
        internal static void ForgetApprovalsForTests()
        {
            lock (Gate)
            {
                Pending.Clear();
                ById.Clear();
                Ended.Clear();
                Answered.Clear();
                SessionSourced.Clear();
            }
        }

        // A requested or resolved broadcast. The fast path: it makes the amber
        // immediate and never replaces the poll, which is what survives a dropped
        // broadcast, a reconnect, and the app starting after the approval was
        // raised.
        //
        // Ignored entirely unless the user opted in. Without the cap this app does
        // not ask for, none arrive; one that does is not allowed to colour an orb
        // for someone who never asked to approve anything.
        internal static void OnApprovalEvent(string name, JsonElement payload, DateTime now)
        {
            if (!OrbweaverSettings.OpenClawApproveEnabled) return;

            var at = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));

            if (OpenClawApprovals.IsSessionEvent(name))
            {
                OnSessionApprovalEvent(payload, at);
                return;
            }

            if (OpenClawApprovals.ParseRequested(name, payload, at) is { } requested)
            {
                if (OpenClawApprovals.IsExpired(requested, at)) return;

                var touched = new HashSet<string>(StringComparer.Ordinal);
                lock (Gate) AddLocked(requested, false, touched);

                Notify(touched);
                return;
            }

            if (OpenClawApprovals.ParseResolved(name, payload) is { } resolved) EndResolved(resolved);
        }

        // A `session.approval` event, from a session this app subscribed to with
        // includeApprovals. Pending adds the record and marks it session-sourced;
        // terminal ends it exactly as a resolved broadcast does.
        private static void OnSessionApprovalEvent(JsonElement payload, DateTimeOffset at)
        {
            if (OpenClawApprovals.ParseSessionEvent(payload, at) is not { } change) return;

            if (change.Pending is { } pending)
            {
                if (OpenClawApprovals.IsExpired(pending, at)) return;

                var touched = new HashSet<string>(StringComparer.Ordinal);
                lock (Gate) AddLocked(pending, true, touched);

                Notify(touched);
                return;
            }

            EndResolved(change.Terminal!);
        }

        // Takes a resolved approval off the set and records how it ended, for the
        // panel to write up once.
        private static void EndResolved(OpenClawApprovalResolved resolved)
        {
            OpenClawApproval? approval;
            lock (Gate)
            {
                RemoveLocked(resolved.Id, out approval);

                // An id we never listed is ignored: a broadcast cannot invent an
                // ending for something this app was not holding.
                if (approval is null) return;

                RecordEndedLocked(new OpenClawApprovalEnded(
                    approval, "resolved", resolved.Decision, resolved.Reason, resolved.ResolvedBy,
                    resolved.Status ?? OpenClawApprovals.UnknownStatus));
            }

            NotifyApprovals(approval.SessionKey);
        }

        // A subscribe's approvalReplay: the gateway's authoritative pending set for
        // this session. What it lists is added (and marked session-sourced), and,
        // unless the gateway says it was cut short, what it omits is gone. A cut
        // list is merged, never replaced: replacing from it would drop pending
        // records the gateway simply did not fit.
        internal static void ApplyReplay(string key, OpenClawApprovalReplay replay, DateTimeOffset now)
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);

            lock (Gate)
            {
                var wanted = replay.Approvals.Where(a => !OpenClawApprovals.IsExpired(a, now))
                    .GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                var wantedIds = wanted.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

                if (!replay.Truncated && Pending.TryGetValue(key, out var held))
                {
                    foreach (var gone in held.Where(a => !wantedIds.Contains(a.Id)).ToList())
                    {
                        RemoveLocked(gone.Id, out _);
                        touched.Add(key);
                        RecordEndedLocked(DroppedEnding(gone, now));
                    }
                }

                foreach (var approval in wanted) AddLocked(approval, true, touched);
            }

            Notify(touched);
        }

        // The poll's reconciliation: the lists are the gateway's own account of
        // what is pending right now. For each kind that was read, what the list
        // omits is gone, whatever the broadcasts did or did not say, except what the
        // session path taught, which the list cannot be expected to show. A kind
        // that could not be read is left exactly as it was.
        internal static void ReplacePending(string kind, IReadOnlyList<OpenClawApproval> listed, DateTimeOffset now)
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);

            lock (Gate)
            {
                var wanted = listed.Where(a => !OpenClawApprovals.IsExpired(a, now))
                    .GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                var wantedIds = wanted.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

                foreach (var (key, list) in Pending.ToList())
                {
                    // A session-sourced record is not the list's to remove: the list
                    // not showing it is exactly the case the session path exists for.
                    foreach (var gone in list.Where(
                                 a => a.Kind == kind && !wantedIds.Contains(a.Id) && !SessionSourced.Contains(a.Id)).ToList())
                    {
                        RemoveLocked(gone.Id, out _);
                        touched.Add(key);
                        RecordEndedLocked(DroppedEnding(gone, now));
                    }
                }

                foreach (var approval in wanted) AddLocked(approval, false, touched);
            }

            Notify(touched);
        }

        // After each poll: a session the gateway no longer lists cannot have a
        // panel worth prompting and must not hold memory. Keys are what Parse
        // recorded for every row the gateway listed, filters or not, so a quiet
        // session with a real approval pending is kept and only a vanished or
        // invented one goes. Their endings go too: nobody will drain them.
        internal static void PurgeUnlistedApprovals()
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);

            lock (Gate)
            {
                foreach (var key in Pending.Keys.Where(k => !_deliveries.ContainsKey(k)).ToList())
                {
                    foreach (var approval in Pending[key].ToList()) RemoveLocked(approval.Id, out _);
                    touched.Add(key);
                }

                foreach (var key in Ended.Keys.Where(k => !_deliveries.ContainsKey(k)).ToList()) Ended.Remove(key);
            }

            Notify(touched);
        }

        // The panel that subscribed has closed: what it taught this app goes back
        // to being the list's to judge, so an approval resolved while nobody was
        // listening does not stay amber until it expires.
        internal static void ReleaseSessionSourced(string key)
        {
            lock (Gate)
            {
                if (!Pending.TryGetValue(key, out var held)) return;

                foreach (var approval in held) SessionSourced.Remove(approval.Id);
            }
        }

        // Called from Restart, which holds Gate; the lock here is re-entrant.
        // Everything pending goes when the connection does: a record the gateway
        // resolved while the socket was down must not outlive the reconnect.
        private static void ClearPendingLocked()
        {
            List<string> keys;

            lock (Gate)
            {
                keys = Pending.Keys.ToList();
                Pending.Clear();
                ById.Clear();
                SessionSourced.Clear();
                Ended.Clear();
                Answered.Clear();
            }

            foreach (var key in keys) NotifyApprovals(key);
        }

        // Adds a record, in arrival order, or declines. Declines an id already held
        // (by any feed, which makes the broadcast and the poll one record) and an id
        // already answered (a record that comes back after its answer applied would
        // be a prompt that cannot be answered). Keeps the newest within the caps:
        // when a session already holds MaxPerSession, or the whole set MaxTotal, the
        // oldest arrival is evicted and recorded so the panel can say so.
        // `touched` collects the sessions whose set changed, to be told afterwards.
        private static bool AddLocked(OpenClawApproval approval, bool sessionSourced, ISet<string> touched)
        {
            if (ById.ContainsKey(approval.Id))
            {
                // A session path that meets a record the lists already knew still
                // claims it, so the poll leaves it.
                if (sessionSourced) SessionSourced.Add(approval.Id);
                return false;
            }

            if (Answered.Contains(approval.Id)) return false;

            if (Pending.TryGetValue(approval.SessionKey, out var existing))
            {
                while (existing.Count >= OpenClawApprovals.MaxPerSession) EvictLocked(existing[0], touched);
            }

            while (ById.Count >= OpenClawApprovals.MaxTotal)
            {
                var oldest = ById.MinBy(pair => pair.Value.Seq);
                EvictLocked(Pending[oldest.Value.Key].First(a => a.Id == oldest.Key), touched);
            }

            // Eviction may have removed this session's now-empty list.
            if (!Pending.TryGetValue(approval.SessionKey, out var list))
                Pending[approval.SessionKey] = list = new List<OpenClawApproval>();

            list.Add(approval);
            ById[approval.Id] = (approval.SessionKey, ++_arrival);
            if (sessionSourced) SessionSourced.Add(approval.Id);
            touched.Add(approval.SessionKey);
            return true;
        }

        // An approval a feed stopped listing with no event saying why. Past its own
        // expiry it is "expired" (this app's clock is the evidence, said as such by
        // the "timeout" reason); before it, nothing says what happened, so the
        // status is "unknown" rather than a guess.
        private static OpenClawApprovalEnded DroppedEnding(OpenClawApproval gone, DateTimeOffset now) =>
            OpenClawApprovals.IsExpired(gone, now)
                ? new OpenClawApprovalEnded(gone, "expired", null, "timeout", null, "expired")
                : new OpenClawApprovalEnded(gone, "gone", null, null, null, OpenClawApprovals.UnknownStatus);

        private static void EvictLocked(OpenClawApproval approval, ISet<string> touched)
        {
            RemoveLocked(approval.Id, out _);
            RecordEndedLocked(new OpenClawApprovalEnded(approval, "evicted", null, null, null, OpenClawApprovals.UnknownStatus));
            touched.Add(approval.SessionKey);
        }

        private static bool RemoveLocked(string id, out OpenClawApproval? removed)
        {
            removed = null;

            if (!ById.Remove(id, out var where)) return false;

            SessionSourced.Remove(id);

            var list = Pending[where.Key];
            removed = list.First(a => a.Id == id);
            list.Remove(removed);
            if (list.Count == 0) Pending.Remove(where.Key);
            return true;
        }

        private static void RecordEndedLocked(OpenClawApprovalEnded ended)
        {
            var key = ended.Approval.SessionKey;
            if (!Ended.TryGetValue(key, out var list)) Ended[key] = list = new List<OpenClawApprovalEnded>();

            list.Add(ended);
            if (list.Count > MaxEndedPerSession) list.RemoveAt(0);
        }

        private static void Notify(IEnumerable<string> keys)
        {
            foreach (var key in keys) NotifyApprovals(key);
        }

        // Tells the panel open on this session, on the UI thread where it redraws.
        // Only a session someone has opened has a chat; the orb is told by the
        // snapshot like any other state.
        private static void NotifyApprovals(string key)
        {
            OpenClawChatSession? chat;
            lock (Gate) Chats.TryGetValue(key, out chat);

            if (chat is not null) Dispatcher.UIThread.Post(chat.OnApprovalsChanged);
        }

        // Asks the gateway for each kind's list and reconciles. Only for a device
        // that chose to approve *and* was granted the scope: asking without it is
        // a refusal per kind every five seconds. A kind that fails is skipped and
        // leaves its records as they were; one refused list must not wipe the
        // others, nor take the whole poll down. Nothing a row holds can make this
        // throw (see OpenClawApprovals), so a poisoned row costs only itself.
        internal static async Task PollApprovalsOnceAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            if (!OrbweaverSettings.OpenClawApproveEnabled
                || !gateway.GrantedScopes.Contains(ApprovalsScope))
                return;

            foreach (var kind in OpenClawApprovals.Kinds)
            {
                JsonElement result;

                try
                {
                    result = await gateway.RequestAsync(
                        OpenClawApprovals.ListMethodFor(kind), new Dictionary<string, object>(), ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                ReplacePending(kind, OpenClawApprovals.ParseList(result, kind, now), now);
            }
        }

        internal const string ApprovalsScope = "operator.approvals";

        // The one place this app answers an approval. Reached from exactly one
        // handler, a click on a button the person can see, through
        // OpenClawChatSession.AnswerAsync. No `reviewer` is sent: an answer from
        // here is attributed to this Mac's paired device, which is the thing the
        // person approved, and a reviewer this app is not would be impersonation.
        //
        // It takes the id the click was drawn for, and sends only if that id is
        // still pending on this conversation. A click drawn for an approval that has
        // since ended, been evicted, expired or been replaced sends nothing and says
        // so. It never sends anything for a decision outside {deny, allow-once},
        // one the gateway lists as unavailable for the request, an allow for a
        // request that cannot be shown whole or safely, or a request that belongs to
        // another conversation.
        internal static async Task<OpenClawApprovalOutcome> ResolveApprovalAsync(
            OpenClawChatSession chat, string approvalId, string decision, CancellationToken ct = default)
        {
            if (!OpenClawApprovals.IsAnswerableDecision(decision))
                return Fail("that answer is not offered");

            OpenClawApproval? approval = null;
            string? heldBy = null;
            OpenClawGateway? gateway;

            lock (Gate)
            {
                gateway = _gateway;

                if (ById.TryGetValue(approvalId, out var where))
                {
                    heldBy = where.Key;
                    approval = Pending[where.Key].First(a => a.Id == approvalId);
                }
            }

            if (approval is null || OpenClawApprovals.IsExpired(approval, DateTimeOffset.UtcNow))
                return new OpenClawApprovalOutcome(OpenClawApprovalResult.NoLongerPending, null, null, null, null);

            if (!string.Equals(heldBy, chat.GatewayKey, StringComparison.Ordinal))
                return Fail("this request belongs to another conversation");

            // Never an answer the gateway lists as unavailable for this request, and
            // never an allow for one that was cut, or held text that could not be
            // shown safely: nobody saw what they would be allowing.
            if (!approval.Allows(decision))
                return Fail("the gateway doesn't allow that answer for this request");

            if ((approval.Unsafe || approval.CommandTruncated) && decision != OpenClawApprovals.Deny)
                return Fail("this request can only be denied here");

            if (gateway is null) return Fail("not connected to the gateway");

            bool first;
            lock (Gate) first = Answered.Add(approvalId);

            if (!first)
                return new OpenClawApprovalOutcome(OpenClawApprovalResult.Duplicate, null, null, null, null);

            try
            {
                var res = await gateway.RequestAsync("approval.resolve", new Dictionary<string, object>
                {
                    ["id"] = approvalId,
                    ["kind"] = approval.Kind,
                    ["decision"] = decision
                }, ct);

                var answer = OpenClawApprovals.ParseResolveResult(res);
                if (answer is null)
                {
                    Release(approvalId);
                    return Fail("the gateway didn't confirm the answer");
                }

                // Taken off now, and the id stays spent: a record that comes back
                // for an answered id is not re-added.
                Settle(approval);

                return answer.Applied
                    ? new OpenClawApprovalOutcome(OpenClawApprovalResult.Answered, decision, answer.Status, answer.Reason, null)
                    : new OpenClawApprovalOutcome(
                        OpenClawApprovalResult.DecidedElsewhere, answer.Decision, answer.Status, answer.Reason, null);
            }
            catch (Exception ex) when (OpenClawApprovals.IsNotFound(ex))
            {
                // The gateway says it is not pending. Taken off, and the id released:
                // if a poll lists it again the gateway disagrees with itself, and the
                // person must not be left with a prompt that can never be answered.
                Settle(approval);
                Release(approvalId);
                return new OpenClawApprovalOutcome(OpenClawApprovalResult.NoLongerPending, null, null, null, null);
            }
            catch (Exception ex)
            {
                // Anything else is a failure: the record is kept, the id is free, and
                // the person can click again.
                Release(approvalId);
                return Fail(ex.Message);
            }

            static OpenClawApprovalOutcome Fail(string detail) =>
                new(OpenClawApprovalResult.Failed, null, null, null, detail);
        }

        // The panel's own call, which already holds the record it drew: the same
        // answer, by the id the click was bound to.
        internal static Task<OpenClawApprovalOutcome> ResolveApprovalAsync(
            OpenClawChatSession chat, OpenClawApproval approval, string decision, CancellationToken ct = default) =>
            ResolveApprovalAsync(chat, approval.Id, decision, ct);

        // The record is over whichever way it ended: off the pending set now,
        // rather than at the next poll.
        private static void Settle(OpenClawApproval approval)
        {
            bool removed;
            lock (Gate)
            {
                removed = RemoveLocked(approval.Id, out _);
                if (Answered.Count > MaxAnswered) Answered.Clear();
            }

            if (removed) NotifyApprovals(approval.SessionKey);
        }

        // An answer that did not land: the record is still pending, or the gateway
        // says it is not, and the id may be tried again.
        private static void Release(string id)
        {
            lock (Gate) Answered.Remove(id);
        }
    }
}
