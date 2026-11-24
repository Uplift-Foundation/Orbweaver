using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // What came of one call (CB-427, plan 13.2). AlreadyDecided is not a failure:
    // someone (Paperclip's own page, another desktop, a phone) got there first.
    // Refused, Unreachable and NeedsReconnect carry a sentence in Why that the
    // flyout shows, and leave the item answerable.
    internal enum PaperclipActionResult { Done, AlreadyDecided, Refused, Unreachable, NeedsReconnect }

    internal readonly record struct PaperclipOutcome(PaperclipActionResult Result, string? Why = null)
    {
        internal bool IsDone => Result == PaperclipActionResult.Done;
        // Done and AlreadyDecided both mean the item is no longer waiting on anyone.
        internal bool Settled => Result is PaperclipActionResult.Done or PaperclipActionResult.AlreadyDecided;
    }

    // The only thing that changes anything in Paperclip. The flyout, the orb menu
    // and the phone's answers all go through it; the UI suite hands them a fake.
    internal interface IPaperclipActions
    {
        Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct);
        Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct);
        Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct);
        Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct);
        // CB-443: a message to an agent is a comment on its task, or, when it has none, a new task.
        Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct);
        Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct);
    }

    // The outcome of a message plus what Paperclip named in its 2xx answer: the new comment's id,
    // or the new task's id. Both are null on a failure and when a 2xx body could not be read (a
    // send that landed still counts as sent; the next read adopts it).
    internal readonly record struct PaperclipSent(PaperclipOutcome Outcome, string? CommentId = null, string? IssueId = null);

    // CB-450: what PaperclipPrompts asks of the actions when an unconfirmed answer's item turns out decided: was it
    // decided the way that answer chose. Its own interface so IPaperclipActions, which other work is changing, stays as it is.
    internal interface IPaperclipConfirm
    {
        Task<bool> DecidedTheWayAsync(PaperclipNeed need, bool approve, CancellationToken ct);
    }

    // The HTTP implementation, built on the same IPaperclipApi the poll uses and
    // bound to one company (a tool action request names it in the body).
    //
    // Measured (plan 13.1): approve / reject with a board key; approving an
    // approved item again is a 200 and so Done; rejecting an approved one is a 422
    // and the row does not change; pause, resume and wakeup; the tool action
    // approve / decline (a 409 for the opposite decision, a 200 whose status can
    // read failed because the tool runs inside the call). No request raised by a
    // real agent run has been seen, only test-call ones.
    internal sealed class PaperclipActions : IPaperclipActions, IPaperclipConfirm
    {
        internal const string DecisionNote = "From Orbweaver";
        internal const string AlreadyDecidedWhy = "Already answered in Paperclip";
        internal const string RequestGoneWhy = "This request no longer exists in Paperclip";
        internal const string AgentGoneWhy = "That agent is no longer in Paperclip";
        internal const string ToolFailedWhy = "Approved, but the tool failed";
        internal const string ReconnectWhy = "Reconnect to Paperclip to answer";
        internal const string UnreachableWhy = "Can't reach Paperclip";
        internal const string RefusedWhy = "Paperclip refused that";
        internal const string TaskGoneWhy = "That task is no longer in Paperclip.";
        internal const string NothingToSendWhy = "Nothing to send.";
        internal const int WhyLimit = 140;
        internal const int TaskTitleLimit = 80;

        private readonly IPaperclipApi _api;
        private readonly string _companyId;
        private readonly Action _pollNow;
        private readonly TimeSpan _verifyBudget;

        // pollNow is what runs after a settled call so the orb leaves amber at
        // once: the live one is PaperclipSessions.PollNow, a test passes a counter.
        internal PaperclipActions(IPaperclipApi api, string companyId, Action? pollNow = null, TimeSpan? verifyBudget = null)
        {
            _verifyBudget = verifyBudget ?? VerifyBudget;
            _api = api;
            _companyId = companyId;
            _pollNow = pollNow ?? PaperclipSessions.PollNow;
        }

        // Null for a need that is not answerable here: a blocked task, the
        // placeholder "pending" row built from an agent status alone, any other kind.
        internal static string? PathFor(PaperclipNeed need, bool approve)
        {
            if (string.IsNullOrEmpty(need.Id)) return null;
            var id = Uri.EscapeDataString(need.Id);
            return need.Kind switch
            {
                "approval" when need.Id != "pending" => $"/api/approvals/{id}/" + (approve ? "approve" : "reject"),
                "action" => $"/api/tool-gateway/action-requests/{id}/" + (approve ? "approve" : "decline"),
                // CB-442: a runtime permission prompt is resolved on its run. One without a run or a turn
                // (a request kind this build cannot answer) is shown with the link only.
                "runtime" when !string.IsNullOrEmpty(need.RunId) && !string.IsNullOrEmpty(need.TurnId) =>
                    $"/api/heartbeat-runs/{Uri.EscapeDataString(need.RunId)}/runtime-requests/{id}/resolve",
                _ => null,
            };
        }

        internal static bool Answerable(PaperclipNeed need) => PathFor(need, true) != null;

        internal static string BodyFor(PaperclipNeed need, string companyId, bool approve = true) =>
            need.Kind == "runtime"
                ? JsonSerializer.Serialize(new
                {
                    resolution = new { action = approve ? "accept" : "decline" },
                    requestKind = "permission_approval",
                    turnId = need.TurnId,
                })
            : need.Kind == "action"
                ? JsonSerializer.Serialize(new { companyId })
                : JsonSerializer.Serialize(new { decisionNote = DecisionNote });

        internal static string AgentPath(string agentId, string verb) =>
            $"/api/agents/{Uri.EscapeDataString(agentId)}/{verb}";

        public async Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct)
        {
            var path = PathFor(need, approve);
            if (path == null) return new(PaperclipActionResult.Refused, "This one can only be answered in Paperclip.");
            var res = await _api.PostAsync(path, BodyFor(need, _companyId, approve), ct).ConfigureAwait(false);
            var outcome = Classify(res);

            // A tool action's approve runs the tool inside the same call, and the
            // status in the answer is the run's (measured: 200 with "failed"). The
            // decision was made and the item is gone, so it is Done; the person is
            // told the tool failed, and the tool's own error text is never shown.
            if (outcome.IsDone && need.Kind == "action" && approve && StatusOf(res.Body) == "failed")
                outcome = new(PaperclipActionResult.Done, ToolFailedWhy);
            // A 404 on a tool action is NOT "answered elsewhere" (the plan's table said
            // so for approvals): it is also what a Paperclip without that route, or a
            // wrong company id, answers, and telling the person someone else decided
            // would be false. Only the measured 409 and the same-decision 200 mean that.
            else if (need.Kind == "action" && res.StatusCode == 404)
                outcome = new(PaperclipActionResult.Refused, RequestGoneWhy);
            // 409 on a tool action is "no longer pending" / "already resolved".
            else if (need.Kind == "action" && res.StatusCode == 409)
                outcome = new(PaperclipActionResult.AlreadyDecided, AlreadyDecidedWhy);

            // Runtime: 409 is "stale or no longer pending" (measured), 404 the run or request is gone, 403 is
            // "Board access required", which for a key that was valid a minute ago means reconnect.
            if (need.Kind == "runtime")
            {
                if (res.StatusCode == 409) outcome = new(PaperclipActionResult.AlreadyDecided, AlreadyDecidedWhy);
                else if (res.StatusCode == 404) outcome = new(PaperclipActionResult.Refused, RequestGoneWhy);
                else if (res.StatusCode == 403) outcome = new(PaperclipActionResult.NeedsReconnect, ReconnectWhy);
            }

            // A 4xx other than 401 to an approval is how Paperclip says "not
            // pending" (the measured 422), but the wording is its own, so the item
            // itself is asked: anything but pending means someone decided it.
            if (outcome.Result == PaperclipActionResult.Refused && need.Kind == "approval" && res.StatusCode != 401)
            {
                var now = await _api.GetAsync($"/api/approvals/{Uri.EscapeDataString(need.Id)}", ct).ConfigureAwait(false);
                if (now.IsOk && StatusOf(now.Body) is { } status && status != "pending")
                    outcome = new(PaperclipActionResult.AlreadyDecided, AlreadyDecidedWhy);
            }

            // CB-450: a POST that timed out or never connected is not known to have failed, and under load Paperclip
            // has been measured applying one that the app gave up on. Ask once before calling it unreachable.
            if (outcome.Result == PaperclipActionResult.Unreachable && await VerifyAsync(need, approve, ct).ConfigureAwait(false) is { } known)
                outcome = known;
            return Settle(outcome);
        }

        // The whole confirming read: one request (one page of a run's events, which the request's own seq makes the
        // few rows after it), under one budget. The actions client carries the 20 s action timeout, so without this
        // a slow Paperclip could hold the phone's answer for another 20 s on top of the POST's own; with it an
        // answer holds its prompt gate at most the POST's 20 s plus this.
        internal static readonly TimeSpan VerifyBudget = TimeSpan.FromSeconds(5);

        // What Paperclip says of the item now, for an answer whose POST outcome is unknown: Done when it was
        // decided the way this answer decided it, AlreadyDecided when it was decided some other way or the
        // way cannot be told, null when it is still pending or this read failed too. Never throws for a
        // transport or parse failure; one read per item (a run's events: one page), no retry.
        private async Task<PaperclipOutcome?> VerifyAsync(PaperclipNeed need, bool approve, CancellationToken callerCt)
        {
            var ct = callerCt;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_verifyBudget);
            ct = budget.Token;
            try
            {
                PaperclipOutcome Decided(bool sameWay) => sameWay ? new(PaperclipActionResult.Done) : new(PaperclipActionResult.AlreadyDecided, AlreadyDecidedWhy);
                switch (need.Kind)
                {
                    case "approval":
                    {
                        var r = await _api.GetAsync($"/api/approvals/{Uri.EscapeDataString(need.Id)}", ct).ConfigureAwait(false);
                        if (!r.IsOk || StatusOf(r.Body) is not { } status || status == "pending") return null;
                        return Decided(status == (approve ? "approved" : "rejected"));
                    }
                    case "action":
                    {
                        // Only pending ones are listed, so an item that has left the list was decided, and which way is
                        // not on it.
                        var r = await _api.GetAsync($"/api/companies/{Uri.EscapeDataString(_companyId)}/tools/action-requests", ct).ConfigureAwait(false);
                        if (!r.IsOk) return null;
                        return PaperclipApi.ParseActionRequests(r.Body).Any(a => a.Id == need.Id) ? null : Decided(false);
                    }
                    default:   // "runtime": PathFor answers only a runtime need that has its run
                    {
                        var after = need.Seq is > 0 ? need.Seq.Value - 1 : 0;
                        var r = await _api.GetAsync($"/api/heartbeat-runs/{Uri.EscapeDataString(need.RunId)}/events?afterSeq={after}&limit=500", ct).ConfigureAwait(false);
                        if (!r.IsOk) return null;
                        return PaperclipApi.RuntimeResolution(r.Body, need.Id) is { } res
                            ? Decided(res.Type == "resolved" && res.Action == (approve ? "accept" : "decline")) : null;
                    }
                }
            }
            // Out of budget is "could not tell"; the caller's own cancellation is not swallowed.
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !callerCt.IsCancellationRequested) { return null; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        // The poll found an item gone that an earlier answer of ours timed out on: was it decided the way that answer
        // chose. False for anything else, including a read that could not tell.
        public async Task<bool> DecidedTheWayAsync(PaperclipNeed need, bool approve, CancellationToken ct) =>
            await VerifyAsync(need, approve, ct).ConfigureAwait(false) is { Result: PaperclipActionResult.Done };

        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct)
        {
            // Before the call: a Wake that fails at once must already be known as ours.
            PaperclipSessions.NoteWake(agentId, DateTime.UtcNow);
            return Agent(agentId, "wakeup", "{\"source\":\"on_demand\"}", ct);
        }

        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) =>
            Agent(agentId, "pause", "{}", ct);

        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) =>
            Agent(agentId, "resume", "{}", ct);

        // For an agent verb a 404 is not "answered elsewhere": nothing was waiting on
        // an answer, the agent is simply gone, and the menu must say so.
        private async Task<PaperclipOutcome> Agent(string agentId, string verb, string body, CancellationToken ct)
        {
            var res = await _api.PostAsync(AgentPath(agentId, verb), body, ct).ConfigureAwait(false);
            return Settle(res.StatusCode == 404 ? new(PaperclipActionResult.Refused, AgentGoneWhy) : Classify(res));
        }

        // ---- messages (CB-443) -------------------------------------------------
        // Measured (plan 3, 2026-11-24, the app's own board key): the comment POST is a 201 whose body is
        // the comment row; the create-task POST is a 201 whose body is the issue row and whose assignee
        // woke 2.0 s later. No credential on the comment route answered 404 (not 401), and an empty body
        // 400 with a validation sentence. Assumed, not run: a 403 for a key without board access, which
        // is read as reconnect, as it is for a runtime request.

        internal static string CommentPath(string issueId) => $"/api/issues/{Uri.EscapeDataString(issueId)}/comments";
        internal string OpenTaskPath() => $"/api/companies/{Uri.EscapeDataString(_companyId)}/issues";
        internal static string CommentBody(string text) => JsonSerializer.Serialize(new { body = text });

        // The title is the first line, cut to 80 characters; the description is the whole message.
        internal static string TitleFor(string text)
        {
            var first = text.Trim().Split('\n')[0].Trim();
            if (first.Length <= TaskTitleLimit) return first;
            // Never leave half a surrogate pair at the cut: an emoji at the limit goes whole or not at all.
            var cut = char.IsHighSurrogate(first[TaskTitleLimit - 1]) ? TaskTitleLimit - 1 : TaskTitleLimit;
            return first[..cut].TrimEnd();
        }

        internal static string OpenTaskBody(string agentId, string text) =>
            JsonSerializer.Serialize(new
            {
                title = TitleFor(text),
                description = text,
                assigneeAgentId = agentId,
                status = "todo",
            });

        public async Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) return new(new(PaperclipActionResult.Refused, NothingToSendWhy));
            var res = await _api.PostAsync(CommentPath(issueId), CommentBody(text), ct).ConfigureAwait(false);
            if (res.StatusCode == 404) return new(new(PaperclipActionResult.Refused, TaskGoneWhy));
            return Sent(res, commentId: true);
        }

        public async Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) return new(new(PaperclipActionResult.Refused, NothingToSendWhy));
            var res = await _api.PostAsync(OpenTaskPath(), OpenTaskBody(agentId, text), ct).ConfigureAwait(false);
            if (res.StatusCode == 404) return new(new(PaperclipActionResult.Refused, AgentGoneWhy));
            return Sent(res, commentId: false);
        }

        private PaperclipSent Sent(PaperclipResponse res, bool commentId)
        {
            var outcome = res.StatusCode == 403 ? new PaperclipOutcome(PaperclipActionResult.NeedsReconnect, ReconnectWhy) : Classify(res);
            if (!outcome.IsDone) return new(outcome);
            var id = Field(res.Body, "id");
            _pollNow();
            return commentId ? new(outcome, CommentId: id) : new(outcome, IssueId: id);
        }

        private PaperclipOutcome Settle(PaperclipOutcome o)
        {
            if (o.Settled) _pollNow();
            return o;
        }

        // ---- the verdict, pure ------------------------------------------------

        // 2xx Done; 404 (the item is gone) AlreadyDecided; 401 NeedsReconnect; no
        // connection, a timeout or a 5xx Unreachable; any other 4xx Refused with
        // Paperclip's own one-sentence `error` when it is short enough to read.
        internal static PaperclipOutcome Classify(PaperclipResponse r)
        {
            if (r.IsOk) return new(PaperclipActionResult.Done);
            if (r.StatusCode == 404) return new(PaperclipActionResult.AlreadyDecided, AlreadyDecidedWhy);
            if (r.StatusCode == 401) return new(PaperclipActionResult.NeedsReconnect, ReconnectWhy);
            if (r.Kind is PaperclipHttpKind.Timeout or PaperclipHttpKind.Unreachable or PaperclipHttpKind.ServerError)
                return new(PaperclipActionResult.Unreachable, UnreachableWhy);
            return new(PaperclipActionResult.Refused, ErrorSentence(r.Body) ?? RefusedWhy);
        }

        // Only Paperclip's `error` text, cut. Never the body, never anything else.
        internal static string? ErrorSentence(string? body)
        {
            var e = Field(body, "error");
            if (string.IsNullOrWhiteSpace(e)) return null;
            e = e.Trim();
            return e.Length <= WhyLimit ? e : e[..WhyLimit].TrimEnd();
        }

        internal static string? StatusOf(string? body) => Field(body, "status");

        private static string? Field(string? body, string name)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
    }
}
