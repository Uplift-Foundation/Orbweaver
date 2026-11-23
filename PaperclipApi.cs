using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Orbweaver
{
    // Paperclip's control plane, reduced to what an orb can show (CB-421).
    //
    // Everything here is pure: JSON text in, records out, no clock of its own,
    // no network, no settings. The decisions an orb depends on - which state an
    // agent is in, what needs a person, what the mark says, which agents fold
    // into their lead, which local session is really a Paperclip run - are
    // functions of their arguments, so each is one unit test per outcome.
    //
    // The parsers never throw. Paperclip's payloads are not ours: a run row
    // came back redacted with 13 keys beside others with 53, and the shapes
    // for a tool action request or a blocked issue have never been seen, so a
    // row that does not look right is skipped rather than allowed to take the
    // whole poll down. See docs/paperclip-findings.md for what was measured.
    //
    // Two things never reach any output: an agent's adapterConfig (it carries
    // local paths) and a run's `error` text (measured rows carry commands).
    // The parsers do not read either, so no later caller can leak them.
    internal static class PaperclipApi
    {
        // ---- raw rows ------------------------------------------------------

        internal sealed record Company(string Id, string Name, string IssuePrefix);

        internal sealed record RawAgent(
            string Id, string Name, string Role, string Title, string Status,
            string? ReportsTo, string AdapterType,
            long BudgetCents, long SpentCents,
            string? PauseReason, string? ErrorReason, DateTime? LastHeartbeatAt);

        internal sealed record RawLiveRun(
            string Id, string AgentId, string Status, DateTime? StartedAt,
            string? IssueId, string? StatusMessage, string? ToolName);

        internal sealed record RawRun(
            string Id, string AgentId, string Status, DateTime? StartedAt,
            DateTime? FinishedAt, string? ErrorCode, string? IssueId,
            int? ProcessPid, string? SessionIdAfter, bool Redacted);

        internal sealed record RawApproval(
            string Id, string Type, string Status, string? Title,
            string? PayloadAgentId, string? RequestedByAgentId);

        internal sealed record RawActionRequest(
            string Id, string? AgentId, string? Summary);

        internal sealed record RawIssue(
            string Id, string Identifier, string Title, string Status,
            string? AssigneeAgentId, string? BlockerState = null);

        internal sealed record Health(string Status, string DeploymentMode);

        internal sealed record Challenge(
            string Id, string Token, string BoardApiToken, string ApprovalUrl,
            string PollPath, DateTime? ExpiresAt, int PollIntervalMs, string? ApprovalPath = null);

        internal sealed record RuntimeInfo(int Port);

        // ---- JSON helpers --------------------------------------------------

        private static IReadOnlyList<JsonElement> Rows(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<JsonElement>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
                // Cloned: elements die with the document.
                return doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList();
            }
            catch (JsonException) { return Array.Empty<JsonElement>(); }
        }

        private static JsonElement? Root(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch (JsonException) { return null; }
        }

        private static string? Str(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (!o.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static long Num(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return 0;
            if (!o.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
            return 0;
        }

        private static int? IntOrNull(JsonElement o, string name)
        {
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (!o.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        }

        private static bool Flag(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        internal static DateTime? Time(string? s) =>
            s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var t) ? t.UtcDateTime : null;

        private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        // ---- parsers -------------------------------------------------------

        internal static IReadOnlyList<Company> ParseCompanies(string? json) =>
            Rows(json).Select(r => (Id: Str(r, "id"), Name: Str(r, "name"), P: Str(r, "issuePrefix")))
                .Where(c => !string.IsNullOrEmpty(c.Id))
                .Select(c => new Company(c.Id!, c.Name ?? "", c.P ?? ""))
                .ToList();

        internal static IReadOnlyList<RawAgent> ParseAgents(string? json) =>
            Rows(json).Select(r =>
            {
                var id = Str(r, "id");
                if (string.IsNullOrEmpty(id)) return null;
                return new RawAgent(
                    id, Str(r, "name") ?? "", Str(r, "role") ?? "", Str(r, "title") ?? "",
                    Str(r, "status") ?? "", NonEmpty(Str(r, "reportsTo")), Str(r, "adapterType") ?? "",
                    Num(r, "budgetMonthlyCents"), Num(r, "spentMonthlyCents"),
                    NonEmpty(Str(r, "pauseReason")), NonEmpty(Str(r, "errorReason")),
                    Time(Str(r, "lastHeartbeatAt")));
            }).Where(a => a != null).Select(a => a!).ToList();

        internal static IReadOnlyList<RawLiveRun> ParseLiveRuns(string? json) =>
            Rows(json).Select(r =>
            {
                var id = Str(r, "id");
                var agent = Str(r, "agentId");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(agent)) return null;
                return new RawLiveRun(id, agent, Str(r, "status") ?? "", Time(Str(r, "startedAt")),
                    NonEmpty(Str(r, "issueId")), NonEmpty(Str(r, "currentStatusMessage")),
                    NonEmpty(Str(r, "currentToolName")));
            }).Where(a => a != null).Select(a => a!).ToList();

        // Newest first, as Paperclip lists them. A redacted row has no
        // errorCode, pid or session id and the parser simply leaves those null.
        internal static IReadOnlyList<RawRun> ParseRuns(string? json) =>
            Rows(json).Select(r =>
            {
                var id = Str(r, "id");
                var agent = Str(r, "agentId");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(agent)) return null;
                return new RawRun(id, agent, Str(r, "status") ?? "", Time(Str(r, "startedAt")),
                    Time(Str(r, "finishedAt")), NonEmpty(Str(r, "errorCode")), NonEmpty(Str(r, "issueId")),
                    IntOrNull(r, "processPid"), NonEmpty(Str(r, "sessionIdAfter")), Flag(r, "redacted"));
            }).Where(a => a != null).Select(a => a!).ToList();

        internal static IReadOnlyList<RawApproval> ParseApprovals(string? json) =>
            Rows(json).Select(r =>
            {
                var id = Str(r, "id");
                if (string.IsNullOrEmpty(id)) return null;
                string? title = null, payloadAgent = null;
                if (r.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    title = NonEmpty(Str(p, "title"));
                    payloadAgent = NonEmpty(Str(p, "agentId"));
                }
                return new RawApproval(id, Str(r, "type") ?? "", Str(r, "status") ?? "", title,
                    payloadAgent, NonEmpty(Str(r, "requestedByAgentId")));
            }).Where(a => a != null).Select(a => a!).ToList();

        // The shape of a non-empty row has never been captured (plan 1.7), so
        // this tries the likely keys: an agent under agentId / requestedByAgentId
        // / agent.id, and something to show under summary / toolName / title.
        // The list itself comes wrapped as {"actionRequests":[...]}.
        internal static IReadOnlyList<RawActionRequest> ParseActionRequests(string? json)
        {
            var root = Root(json);
            if (root == null || !root.Value.TryGetProperty("actionRequests", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<RawActionRequest>();
            var list = new List<RawActionRequest>();
            foreach (var r in arr.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var id = Str(r, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var status = Str(r, "status");
                if (status != null && status != "pending") continue;
                string? agent = NonEmpty(Str(r, "agentId")) ?? NonEmpty(Str(r, "requestedByAgentId"));
                if (agent == null && r.TryGetProperty("agent", out var a) && a.ValueKind == JsonValueKind.Object)
                    agent = NonEmpty(Str(a, "id"));
                var summary = NonEmpty(Str(r, "summary")) ?? NonEmpty(Str(r, "toolName"))
                    ?? NonEmpty(Str(r, "tool")) ?? NonEmpty(Str(r, "title"));
                list.Add(new RawActionRequest(id, agent, summary));
            }
            return list;
        }

        internal static IReadOnlyList<RawIssue> ParseIssues(string? json) =>
            Rows(json).Select(r =>
            {
                var id = Str(r, "id");
                if (string.IsNullOrEmpty(id)) return null;
                string? blocker = null;
                if (r.TryGetProperty("blockerAttention", out var b) && b.ValueKind == JsonValueKind.Object)
                    blocker = NonEmpty(Str(b, "state"));
                return new RawIssue(id, Str(r, "identifier") ?? "", Str(r, "title") ?? "",
                    Str(r, "status") ?? "", NonEmpty(Str(r, "assigneeAgentId")), blocker);
            }).Where(a => a != null).Select(a => a!).ToList();

        internal static Health? ParseHealth(string? json)
        {
            var r = Root(json);
            if (r == null) return null;
            var status = Str(r.Value, "status");
            return status == null ? null : new Health(status, Str(r.Value, "deploymentMode") ?? "");
        }

        internal static Challenge? ParseChallenge(string? json)
        {
            var r = Root(json);
            if (r == null) return null;
            var o = r.Value;
            var id = Str(o, "id"); var token = Str(o, "token");
            var board = Str(o, "boardApiToken"); var url = Str(o, "approvalUrl");
            var poll = Str(o, "pollPath");
            if (id == null || token == null || board == null || url == null || poll == null) return null;
            var ms = IntOrNull(o, "suggestedPollIntervalMs") ?? 1000;
            return new Challenge(id, token, board, url, poll, Time(Str(o, "expiresAt")), Math.Clamp(ms, 250, 10_000), NonEmpty(Str(o, "approvalPath")));
        }

        internal static string? ParseChallengeStatus(string? json)
        {
            var r = Root(json);
            return r == null ? null : Str(r.Value, "status");
        }

        // The port, from runtime-info.json, else from config.json's server
        // section. Neither file is ever written and no other file in the
        // instance folder is read (it also holds secrets and a database).
        internal static RuntimeInfo? ParseRuntimeInfo(string? json)
        {
            var r = Root(json);
            if (r == null) return null;
            var port = IntOrNull(r.Value, "port");
            return port is > 0 and < 65536 ? new RuntimeInfo(port.Value) : null;
        }

        internal static RuntimeInfo? ParseConfigPort(string? json)
        {
            var r = Root(json);
            if (r == null || !r.Value.TryGetProperty("server", out var s) || s.ValueKind != JsonValueKind.Object)
                return null;
            var port = IntOrNull(s, "port");
            return port is > 0 and < 65536 ? new RuntimeInfo(port.Value) : null;
        }

        // ---- urls ----------------------------------------------------------

        internal static string AgentUrl(string baseUrl, string prefix, string agentId) =>
            Page(baseUrl, prefix, "agents/" + Uri.EscapeDataString(agentId));

        internal static string ApprovalUrl(string baseUrl, string prefix, string id) =>
            Page(baseUrl, prefix, "approvals/" + Uri.EscapeDataString(id));

        internal static string PendingApprovalsUrl(string baseUrl, string prefix) =>
            Page(baseUrl, prefix, "approvals/pending");

        internal static string RequestsUrl(string baseUrl, string prefix) =>
            Page(baseUrl, prefix, "inbox/requests");

        internal static string IssueUrl(string baseUrl, string prefix, string id) =>
            Page(baseUrl, prefix, "issues/" + Uri.EscapeDataString(id));

        private static string Page(string baseUrl, string prefix, string tail) =>
            baseUrl.TrimEnd('/') + "/" + (string.IsNullOrEmpty(prefix) ? "" : Uri.EscapeDataString(prefix) + "/") + tail;

        // ---- state, marks and needs ----------------------------------------

        internal sealed record ComposeInput(
            IReadOnlyList<RawAgent> Agents,
            IReadOnlyList<RawLiveRun> LiveRuns,
            IReadOnlyList<RawRun> Runs,
            IReadOnlyList<RawApproval> Approvals,        // pending only
            IReadOnlyList<RawActionRequest> Actions,     // pending only
            IReadOnlyList<RawIssue> BlockedIssues,
            IReadOnlyDictionary<string, string> TaskTitles, // issueId -> title
            string BaseUrl, string IssuePrefix, DateTime Now);

        internal static IReadOnlyList<PaperclipAgent> Compose(ComposeInput c)
        {
            var known = c.Agents.Select(a => a.Id).ToHashSet();
            var lead = c.Agents.FirstOrDefault(a => a.ReportsTo == null || !known.Contains(a.ReportsTo))?.Id;

            string? Resolve(string? first, string? second = null)
            {
                if (first != null && known.Contains(first)) return first;
                if (second != null && known.Contains(second)) return second;
                return lead;
            }

            var needs = new Dictionary<string, List<PaperclipNeed>>();
            void Add(string? agent, PaperclipNeed n)
            {
                if (agent == null) return;
                if (!needs.TryGetValue(agent, out var l)) needs[agent] = l = new List<PaperclipNeed>();
                l.Add(n);
            }

            foreach (var a in c.Approvals)
                Add(Resolve(a.PayloadAgentId, a.RequestedByAgentId),
                    new PaperclipNeed("approval", a.Id, a.Title ?? ApprovalWording(a.Type),
                        ApprovalUrl(c.BaseUrl, c.IssuePrefix, a.Id)));
            foreach (var a in c.Actions)
                Add(Resolve(a.AgentId),
                    new PaperclipNeed("action", a.Id, a.Summary ?? "Tool action",
                        RequestsUrl(c.BaseUrl, c.IssuePrefix)));
            foreach (var i in c.BlockedIssues)
            {
                // An unassigned blocked issue is nobody's orb to light. Paperclip's
                // own verdict decides whether a person is needed: only
                // `needs_attention` (or no verdict at all) is. A task blocked on
                // another task, which Paperclip resolves itself, is not.
                if (i.AssigneeAgentId == null || !known.Contains(i.AssigneeAgentId)) continue;
                if (i.BlockerState != null && i.BlockerState != "needs_attention") continue;
                var label = string.Join(" ", new[] { i.Identifier, i.Title }.Where(s => s.Length > 0));
                Add(i.AssigneeAgentId, new PaperclipNeed("blocked", i.Id, "Blocked: " + label,
                    IssueUrl(c.BaseUrl, c.IssuePrefix, i.Id)));
            }

            var live = c.LiveRuns
                .Where(r => r.Status is "running" or "queued")
                .GroupBy(r => r.AgentId)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.StartedAt ?? DateTime.MaxValue).First());

            var newestFinished = new Dictionary<string, RawRun>();
            foreach (var r in c.Runs)
            {
                if (r.Status is "queued" or "running" or "scheduled_retry" or "") continue;
                newestFinished.TryAdd(r.AgentId, r);
            }

            var result = new List<PaperclipAgent>();
            foreach (var a in c.Agents)
            {
                if (a.Status == "terminated") continue;

                needs.TryGetValue(a.Id, out var agentNeeds);
                var list = (IReadOnlyList<PaperclipNeed>)(agentNeeds ?? new List<PaperclipNeed>());
                live.TryGetValue(a.Id, out var run);

                string state;
                if (list.Count > 0) state = "waiting";
                else if (a.Status == "pending_approval")
                {
                    state = "waiting";
                    list = new[] { new PaperclipNeed("approval", "pending", "Waiting for approval",
                        PendingApprovalsUrl(c.BaseUrl, c.IssuePrefix)) };
                }
                else if (run != null || a.Status == "running") state = "generating";
                else state = "idle";

                newestFinished.TryGetValue(a.Id, out var last);
                var (mark, reason) = MarkFor(a, last);

                string? task = null;
                if (run?.IssueId != null && c.TaskTitles.TryGetValue(run.IssueId, out var t)) task = t;

                var started = run?.StartedAt;
                var activity = run?.ToolName ?? (LooksInternal(run?.StatusMessage) ? null : run?.StatusMessage);

                var lastActivity = new[] { a.LastHeartbeatAt, last?.FinishedAt, last?.StartedAt, started }
                    .Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty(c.Now).Max();

                var sleeping = state == "idle" && mark == PaperclipMark.None;

                result.Add(new PaperclipAgent(
                    a.Id, a.Name, a.Role, a.Title, a.Status, a.ReportsTo, a.AdapterType,
                    state, mark, reason, list, task, activity, started,
                    last == null ? null : ResultWording(last.Status),
                    RingPercent(a.BudgetCents, a.SpentCents), lastActivity,
                    AgentUrl(c.BaseUrl, c.IssuePrefix, a.Id), sleeping,
                    last?.ProcessPid, last?.SessionIdAfter, a.BudgetCents, a.SpentCents));
            }
            return result;
        }

        // Under the native runner the live run's status message was internal span
        // text ("runner span: runner.process.launch ..."), not a sentence. Those
        // are dropped so the hover falls back to the task line.
        internal static bool LooksInternal(string? message) =>
            message != null && (message.StartsWith("runner span:", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(message, @"^[\w-]+\.[\w-]"));

        internal static string ApprovalWording(string type) => type switch
        {
            "hire_agent" => "Hire",
            "budget_override_required" => "Budget override",
            "approve_ceo_strategy" => "Strategy",
            _ => "Approval",
        };

        internal static (PaperclipMark, string?) MarkFor(RawAgent a, RawRun? newestFinished)
        {
            if (a.Status == "paused")
            {
                if (a.PauseReason == "budget") return (PaperclipMark.OverBudget, "Paused: over budget");
                return (PaperclipMark.Paused, a.PauseReason == null ? "Paused" : "Paused: " + PauseWording(a.PauseReason));
            }
            if (a.Status == "error")
                return (PaperclipMark.Broke, Cut(a.ErrorReason ?? "Error", 140));
            if (newestFinished is { Status: "failed" })
                return (PaperclipMark.Broke, "Last run failed" + (newestFinished.ErrorCode == null ? "" : ": " + newestFinished.ErrorCode));
            if (newestFinished is { Status: "timed_out" })
                return (PaperclipMark.Broke, "Last run timed out");
            return (PaperclipMark.None, null);
        }

        private static string PauseWording(string reason) => reason switch
        {
            "manual" => "paused by hand",
            "system" => "paused by Paperclip",
            "company_archived" => "company archived",
            _ => reason.Replace('_', ' '),
        };

        private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        internal static string ResultWording(string status) => status switch
        {
            "succeeded" => "succeeded",
            "failed" => "failed",
            "timed_out" => "timed out",
            "cancelled" => "cancelled",
            "interrupted" => "interrupted",
            "scheduled_retry" => "retry scheduled",
            _ => status,
        };

        // Drawn only when there is both a cap and spend, so a company on a
        // subscription (spend always 0) shows no empty ring. Capped at 100.
        internal static int? RingPercent(long capCents, long spentCents)
        {
            if (capCents <= 0 || spentCents <= 0) return null;
            return (int)Math.Min(100, Math.Round(spentCents * 100.0 / capCents, MidpointRounding.AwayFromZero));
        }

        // ---- folding sleeping agents into their lead -----------------------

        internal sealed record FoldResult(
            IReadOnlyList<PaperclipAgent> Drawn,
            IReadOnlyDictionary<string, (int Count, IReadOnlyList<string> Names)> FoldedInto);

        internal const int DefaultFoldLimit = 8;
        internal static readonly TimeSpan UnfoldHold = TimeSpan.FromSeconds(60);

        // mode is "show", "fold" or "hide" (anything else reads as fold).
        // recentlyAwake holds the ids that were doing something within
        // UnfoldHold, so a short run does not make the picture jump.
        internal static FoldResult Fold(IReadOnlyList<PaperclipAgent> agents, string mode,
            int limit, IReadOnlySet<string> recentlyAwake)
        {
            var none = new Dictionary<string, (int, IReadOnlyList<string>)>();
            if (mode == "show" || agents.Count <= limit)
                return new FoldResult(agents, none);

            var ids = agents.Select(a => a.Id).ToHashSet();
            var hasReports = agents.Where(a => a.ReportsTo != null).Select(a => a.ReportsTo!).ToHashSet();

            bool Folds(PaperclipAgent a) =>
                a.Sleeping
                && !hasReports.Contains(a.Id)
                && a.ReportsTo != null && ids.Contains(a.ReportsTo)
                && !recentlyAwake.Contains(a.Id);

            var drawn = new List<PaperclipAgent>();
            var names = new Dictionary<string, List<string>>();
            foreach (var a in agents)
            {
                if (!Folds(a)) { drawn.Add(a); continue; }
                if (mode == "hide") continue;
                if (!names.TryGetValue(a.ReportsTo!, out var l)) names[a.ReportsTo!] = l = new List<string>();
                l.Add(a.Name);
            }
            var folded = names.ToDictionary(k => k.Key, k => (k.Value.Count, (IReadOnlyList<string>)k.Value));
            return new FoldResult(drawn, folded);
        }

        // Carries the "awake within the hold" memory from one poll to the next.
        // An agent that is not sleeping now is stamped now; one stamped within
        // the hold stays in the returned set. Pure: the caller keeps the map.
        internal static (IReadOnlyDictionary<string, DateTime> Stamps, IReadOnlySet<string> Awake) TrackAwake(
            IReadOnlyDictionary<string, DateTime> previous, IReadOnlyList<PaperclipAgent> agents, DateTime now)
        {
            var next = new Dictionary<string, DateTime>();
            foreach (var a in agents)
            {
                if (!a.Sleeping) next[a.Id] = now;
                else if (previous.TryGetValue(a.Id, out var t)) next[a.Id] = t;
            }
            var awake = next.Where(k => now - k.Value < UnfoldHold).Select(k => k.Key).ToHashSet();
            return (next, awake);
        }

        // ---- one orb per agent ---------------------------------------------

        // True when a local hook row is really a Paperclip run, so the orb for
        // the agent is the only one. Hook-firing modes only: measured for the
        // legacy claude_local runner, not for "preconfigured home". The default
        // runner never writes a local row, so the known sets are empty there
        // and nothing changes.
        internal static bool IsRunProcess(string? entrypoint, string? cwd, int? pid, string? sessionId,
            IReadOnlySet<int> knownPids, IReadOnlySet<string> knownSessions)
        {
            // sdk-cli is legacy claude_local, sdk-ts the preconfigured native
            // mode. Both halves are required: sdk- alone is every headless
            // Claude anyone runs, a .paperclip cwd alone a person's terminal.
            if (entrypoint == null || !entrypoint.StartsWith("sdk-", StringComparison.Ordinal)) return false;
            if (pid is int p && knownPids.Contains(p)) return true;
            if (!string.IsNullOrEmpty(sessionId) && knownSessions.Contains(sessionId)) return true;
            return InWorkspace(cwd);
        }

        private static bool InWorkspace(string? cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return false;
            var seg = cwd.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 3 < seg.Length; i++)
            {
                if (seg[i] == ".paperclip" && seg[i + 1] == "instances"
                    && (seg[i + 3] == "workspaces" || seg[i + 3] == "isolated-workspaces"))
                    return true;
            }
            return false;
        }
    }
}
