using System.Text.Json;
using Avalonia.Threading;

namespace Orbweaver
{
    // How an approval ended, as far as this app can tell, for a panel that did not
    // answer it itself. Drained by TakeEnded so each ending is written up once.
    //   resolved: another reviewer, or the gateway, decided it (Decision, Reason and
    //             ResolvedBy are what the event carried);
    //   expired:  the poll found it gone after its expiry, or its expiry passed;
    //   gone:     the poll found it gone before its expiry and nothing said why.
    internal sealed record OpenClawApprovalEnded(
        OpenClawApproval Approval, string Cause, string? Decision, string? Reason, string? ResolvedBy);

    internal enum OpenClawApprovalResult
    {
        // This call decided it.
        Answered,

        // The gateway answered applied:false: another reviewer decided first.
        DecidedElsewhere,

        // Not found: resolved by someone else, expired, or no longer visible. The
        // gateway deliberately does not say which, so neither does this.
        NoLongerPending,

        // The same id was already being answered or already answered.
        Duplicate,

        // Nothing was decided and the record is kept: not connected, a refusal,
        // a transport failure, or an answer outside the two offered.
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
    // setting changing. A test asserts the single site and another replays a
    // whole fixture set through every path and counts the resolve frames sent.
    internal static partial class OpenClawSessions
    {
        // Pending approvals by the session they belong to. Replaced per kind by the
        // poll and added to and removed from by the broadcasts. Guarded by Gate.
        private static readonly Dictionary<string, List<OpenClawApproval>> Pending =
            new(StringComparer.Ordinal);

        // Endings nobody has been told about yet, by session key.
        private static readonly Dictionary<string, List<OpenClawApprovalEnded>> Ended =
            new(StringComparer.Ordinal);

        // Ids with an answer in flight or already given. An id goes in the moment a
        // click lands, so a second click, or a click racing a broadcast, can never
        // send a second request for it; it comes out again only if the answer
        // failed and the record is still pending.
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

        // The sessions an approval event stamped as recently active. A session that
        // just had an approval raised is exactly one worth keeping on screen, which
        // is why the event moves LastSeen at all; remembered here so a test can undo
        // it, since LastSeen is a process-wide static that survives a reconnect on
        // purpose and nothing else forgets it.
        private static readonly HashSet<string> ApprovalSeen = new(StringComparer.Ordinal);

        private static void SeenByApproval(string key, DateTime now)
        {
            LastSeen[key] = now;
            ApprovalSeen.Add(key);
        }

        // A test seam: forgets every approval-related thing this class holds,
        // including the recent-activity stamps approval events left, so one test's
        // approvals cannot make another's session look recent. Restart forgets the
        // pending set but, deliberately, not LastSeen.
        internal static void ForgetApprovalsForTests()
        {
            lock (Gate)
            {
                Pending.Clear();
                Ended.Clear();
                Answered.Clear();
                SessionSourced.Clear();
                foreach (var key in ApprovalSeen) LastSeen.Remove(key);
                ApprovalSeen.Clear();
            }
        }

        // The pending approvals for one session, oldest first, expired ones left
        // out. Expiry is enforced here as well as by the gateway so a prompt cannot
        // outlive its record if every signal is missed.
        internal static IReadOnlyList<OpenClawApproval> PendingFor(string key, DateTimeOffset? now = null)
        {
            var at = now ?? DateTimeOffset.UtcNow;

            lock (Gate)
            {
                return Pending.TryGetValue(key, out var list)
                    ? list.Where(a => !OpenClawApprovals.IsExpired(a, at))
                        .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id, StringComparer.Ordinal).ToList()
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
                OnSessionApprovalEvent(payload, now, at);
                return;
            }

            if (OpenClawApprovals.ParseRequested(name, payload, at) is { } requested)
            {
                if (OpenClawApprovals.IsExpired(requested, at)) return;

                bool added;
                lock (Gate)
                {
                    added = AddLocked(requested);
                    if (added) SeenByApproval(requested.SessionKey, now);
                }

                if (added) NotifyApprovals(requested.SessionKey);
                return;
            }

            if (OpenClawApprovals.ParseResolved(name, payload) is { } resolved) EndResolved(resolved, now);
        }

        // A `session.approval` event, from a session this app subscribed to with
        // includeApprovals. Pending adds the record and marks it session-sourced;
        // terminal ends it exactly as a resolved broadcast does.
        private static void OnSessionApprovalEvent(JsonElement payload, DateTime now, DateTimeOffset at)
        {
            if (OpenClawApprovals.ParseSessionEvent(payload, at) is not { } change) return;

            if (change.Pending is { } pending)
            {
                if (OpenClawApprovals.IsExpired(pending, at)) return;

                bool added;
                lock (Gate)
                {
                    added = AddLocked(pending, sessionSourced: true);
                    if (added) SeenByApproval(pending.SessionKey, now);
                }

                if (added) NotifyApprovals(pending.SessionKey);
                return;
            }

            EndResolved(change.Terminal!, now);
        }

        // Takes a resolved approval off the set and records how it ended, for the
        // panel to write up once.
        private static void EndResolved(OpenClawApprovalResolved resolved, DateTime now)
        {
            OpenClawApproval? approval;
            lock (Gate)
            {
                RemoveLocked(resolved.Id, out approval);

                // An id we never listed is ignored: a broadcast cannot invent an
                // ending for something this app was not holding.
                if (approval is null) return;

                SeenByApproval(approval.SessionKey, now);
                RecordEndedLocked(new OpenClawApprovalEnded(
                    approval, "resolved", resolved.Decision, resolved.Reason, resolved.ResolvedBy));
            }

            NotifyApprovals(approval.SessionKey);
        }

        // A subscribe's approvalReplay: the gateway's authoritative pending set for
        // this session. What it lists is added (and marked session-sourced), and,
        // unless the gateway says it cut the list short, what it omits is gone.
        internal static void ApplyReplay(string key, OpenClawApprovalReplay replay, DateTimeOffset now)
        {
            var changed = false;

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
                        changed = true;
                        RecordEndedLocked(new OpenClawApprovalEnded(
                            gone, OpenClawApprovals.IsExpired(gone, now) ? "expired" : "gone", null, null, null));
                    }
                }

                foreach (var approval in wanted)
                {
                    if (AddLocked(approval, sessionSourced: true)) changed = true;
                }
            }

            if (changed) NotifyApprovals(key);
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

        // The poll's reconciliation: the lists are the gateway's own account of
        // what is pending right now. For each kind that was read, what the list
        // omits is gone, whatever the broadcasts did or did not say. A kind that
        // could not be read is left exactly as it was.
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
                        list.Remove(gone);
                        touched.Add(key);
                        RecordEndedLocked(new OpenClawApprovalEnded(
                            gone, OpenClawApprovals.IsExpired(gone, now) ? "expired" : "gone", null, null, null));
                    }

                    if (list.Count == 0) Pending.Remove(key);
                }

                foreach (var approval in wanted)
                {
                    if (AddLocked(approval)) touched.Add(approval.SessionKey);
                }
            }

            foreach (var key in touched) NotifyApprovals(key);
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
                SessionSourced.Clear();
                Ended.Clear();
                Answered.Clear();
            }

            foreach (var key in keys) NotifyApprovals(key);
        }

        private static bool AddLocked(OpenClawApproval approval, bool sessionSourced = false)
        {
            if (!Pending.TryGetValue(approval.SessionKey, out var list))
                Pending[approval.SessionKey] = list = new List<OpenClawApproval>();

            // De-duplicated by id across every session, so the same record arriving
            // by broadcast and by poll is one record. A session path that meets a
            // record the lists already knew still claims it, so the poll leaves it.
            if (Pending.Values.Any(l => l.Any(a => a.Id == approval.Id)))
            {
                if (sessionSourced) SessionSourced.Add(approval.Id);
                if (list.Count == 0) Pending.Remove(approval.SessionKey);
                return false;
            }

            // A flood from a buggy or hostile gateway is capped: the oldest stay
            // and the rest are not held.
            if (list.Count >= OpenClawApprovals.MaxPerSession) return false;

            list.Add(approval);
            if (sessionSourced) SessionSourced.Add(approval.Id);
            return true;
        }

        private static bool RemoveLocked(string id, out OpenClawApproval? removed)
        {
            removed = null;

            foreach (var (key, list) in Pending)
            {
                var found = list.FirstOrDefault(a => a.Id == id);
                if (found is null) continue;

                list.Remove(found);
                SessionSourced.Remove(id);
                removed = found;
                if (list.Count == 0) Pending.Remove(key);
                return true;
            }

            return false;
        }

        private static void RecordEndedLocked(OpenClawApprovalEnded ended)
        {
            var key = ended.Approval.SessionKey;
            if (!Ended.TryGetValue(key, out var list)) Ended[key] = list = new List<OpenClawApprovalEnded>();

            list.Add(ended);
            if (list.Count > MaxEndedPerSession) list.RemoveAt(0);
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
        // others, nor take the whole poll down.
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
        // Never sends anything for a decision outside {deny, allow-once}, or for an
        // approval that does not belong to the panel it was answered from.
        internal static async Task<OpenClawApprovalOutcome> ResolveApprovalAsync(
            OpenClawChatSession chat, OpenClawApproval approval, string decision,
            CancellationToken ct = default)
        {
            if (!OpenClawApprovals.IsAnswerableDecision(decision))
                return Fail("that answer is not offered");

            if (!string.Equals(approval.SessionKey, chat.GatewayKey, StringComparison.Ordinal))
                return Fail("this request belongs to another conversation");

            // Never an answer the gateway lists as unavailable for this request.
            if (!approval.Allows(decision))
                return Fail("the gateway doesn't allow that answer for this request");

            OpenClawGateway? gateway;
            bool first;
            lock (Gate)
            {
                gateway = _gateway;
                first = gateway is not null && Answered.Add(approval.Id);
            }

            if (gateway is null) return Fail("not connected to the gateway");

            if (!first)
                return new OpenClawApprovalOutcome(OpenClawApprovalResult.Duplicate, null, null, null, null);

            try
            {
                var res = await gateway.RequestAsync("approval.resolve", new Dictionary<string, object>
                {
                    ["id"] = approval.Id,
                    ["kind"] = approval.Kind,
                    ["decision"] = decision
                }, ct);

                var answer = OpenClawApprovals.ParseResolveResult(res);
                if (answer is null)
                {
                    Release(approval.Id);
                    return Fail("the gateway didn't confirm the answer");
                }

                Settle(approval);

                return answer.Applied
                    ? new OpenClawApprovalOutcome(OpenClawApprovalResult.Answered, decision, answer.Status, answer.Reason, null)
                    : new OpenClawApprovalOutcome(
                        OpenClawApprovalResult.DecidedElsewhere, answer.Decision, answer.Status, answer.Reason, null);
            }
            catch (Exception ex) when (OpenClawApprovals.IsNotFound(ex))
            {
                Settle(approval);
                return new OpenClawApprovalOutcome(OpenClawApprovalResult.NoLongerPending, null, null, null, null);
            }
            catch (Exception ex)
            {
                Release(approval.Id);
                return Fail(ex.Message);
            }

            static OpenClawApprovalOutcome Fail(string detail) =>
                new(OpenClawApprovalResult.Failed, null, null, null, detail);
        }

        // The record is over whichever way it ended: off the pending set now,
        // rather than at the next poll. The id stays in Answered, so a later click
        // on a stale prompt is still a no-op.
        private static void Settle(OpenClawApproval approval)
        {
            bool removed;
            lock (Gate) removed = RemoveLocked(approval.Id, out _);

            if (removed) NotifyApprovals(approval.SessionKey);
        }

        // An answer that did not land: the record is still pending and may be
        // answered again.
        private static void Release(string id)
        {
            lock (Gate) Answered.Remove(id);
        }
    }
}
