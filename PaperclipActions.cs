using System;
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
    }

    // The HTTP implementation, built on the same IPaperclipApi the poll uses and
    // bound to one company (a tool action request names it in the body).
    //
    // Measured (plan 13.1): approve / reject with a board key; approving an
    // approved item again is a 200 and so Done; rejecting an approved one is a 422
    // and the row does not change; pause, resume and wakeup. Declared in source
    // and never called live: the tool action approve / decline.
    internal sealed class PaperclipActions : IPaperclipActions
    {
        internal const string DecisionNote = "From Orbweaver";
        internal const string AlreadyDecidedWhy = "Already answered in Paperclip";
        internal const string ReconnectWhy = "Reconnect to Paperclip to answer";
        internal const string UnreachableWhy = "Can't reach Paperclip";
        internal const string RefusedWhy = "Paperclip refused that";
        internal const int WhyLimit = 140;

        private readonly IPaperclipApi _api;
        private readonly string _companyId;
        private readonly Action _pollNow;

        // pollNow is what runs after a settled call so the orb leaves amber at
        // once: the live one is PaperclipSessions.PollNow, a test passes a counter.
        internal PaperclipActions(IPaperclipApi api, string companyId, Action? pollNow = null)
        {
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
                _ => null,
            };
        }

        internal static bool Answerable(PaperclipNeed need) => PathFor(need, true) != null;

        internal static string BodyFor(PaperclipNeed need, string companyId) =>
            need.Kind == "action"
                ? JsonSerializer.Serialize(new { companyId })
                : JsonSerializer.Serialize(new { decisionNote = DecisionNote });

        internal static string AgentPath(string agentId, string verb) =>
            $"/api/agents/{Uri.EscapeDataString(agentId)}/{verb}";

        public async Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct)
        {
            var path = PathFor(need, approve);
            if (path == null) return new(PaperclipActionResult.Refused, "This one can only be answered in Paperclip.");
            var res = await _api.PostAsync(path, BodyFor(need, _companyId), ct).ConfigureAwait(false);
            var outcome = Classify(res);

            // A 4xx other than 401 to an approval is how Paperclip says "not
            // pending" (the measured 422), but the wording is its own, so the item
            // itself is asked: anything but pending means someone decided it.
            if (outcome.Result == PaperclipActionResult.Refused && need.Kind == "approval" && res.StatusCode != 401)
            {
                var now = await _api.GetAsync($"/api/approvals/{Uri.EscapeDataString(need.Id)}", ct).ConfigureAwait(false);
                if (now.IsOk && StatusOf(now.Body) is { } status && status != "pending")
                    outcome = new(PaperclipActionResult.AlreadyDecided, AlreadyDecidedWhy);
            }
            return Settle(outcome);
        }

        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) =>
            Agent(agentId, "wakeup", "{\"source\":\"on_demand\"}", ct);

        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) =>
            Agent(agentId, "pause", "{}", ct);

        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) =>
            Agent(agentId, "resume", "{}", ct);

        private async Task<PaperclipOutcome> Agent(string agentId, string verb, string body, CancellationToken ct) =>
            Settle(Classify(await _api.PostAsync(AgentPath(agentId, verb), body, ct).ConfigureAwait(false)));

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
