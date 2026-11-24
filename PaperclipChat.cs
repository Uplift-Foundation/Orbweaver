using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // CB-443 (plan 2.4): a Paperclip agent's task thread as chat rows. Pure: JSON in, rows out, no
    // window, no settings, no clock (callers pass `now`). Like PaperclipApi's parsers, nothing here
    // throws on a body it does not understand; a bad body yields what was already known.
    //
    // Measured on 2026-11-24 (Paperclip 2026.1117.0): the comments list is newest first; a run's events
    // read oldest first with afterSeq; speech is `item.completed` with payload.prpEvent.payload.kind
    // "agentMessage"; the same answer arrives as the final agentMessage, the run.result.proposed summary
    // and a comment whose createdByRunId is the run. ASSUMED, not seen: that a failed tool carries a
    // status other than "completed" or a non-zero numeric exitCode (only success has been captured).
    internal static class PaperclipChat
    {
        // The phone contract's line limit; one rule serves the panel and the phone.
        internal const int TextLimitBytes = 8000;
        internal const int RowCap = 200;
        internal static readonly TimeSpan WakeGrace = TimeSpan.FromSeconds(30);

        internal const string TaskKey = "task";
        internal static string CommentKey(string commentId) => "c:" + commentId;
        internal static string ToolKey(string runId, string executionId) => $"r:{runId}:t:{executionId}";
        internal static string SpeechKey(string runId, string itemId) => $"r:{runId}:s:{itemId}";
        internal static string AnswerKey(string runId) => $"r:{runId}:answer";
        // A bubble the session raised from a send whose answer carried no readable id. Merge adopts it.
        internal const string LocalPrefix = "u:";

        internal sealed record Comment(string Id, string Body, string? AuthorAgentId, string? AuthorUserId, string? RunId, DateTime? At);
        internal sealed record TaskRef(string Id, string Identifier, string Title, string Status);
        // Replaces names a shown row this one takes the place of (a comment that carries a run's answer
        // replaces that run's answer row, so the bubble never doubles and never jumps).
        internal sealed record Row(string Key, ChatRole Role, string Text, DateTime At, bool Error = false, string? Replaces = null);
        internal sealed record AssignedIssue(TaskRef Task, DateTime? UpdatedAt, bool Cancelled);

        // ---- comments ----------------------------------------------------------

        // Oldest first (the route answers newest first). A deleted comment, and one with no id or no body, is skipped.
        internal static IReadOnlyList<Comment> ParseComments(string? json)
        {
            var list = new List<Comment>();
            foreach (var e in Elements(json))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id"); var body = Str(e, "body");
                if (string.IsNullOrEmpty(id) || body == null) continue;
                if (Str(e, "deletedAt") != null) continue;
                list.Add(new Comment(id, body, NonEmpty(Str(e, "authorAgentId")), NonEmpty(Str(e, "authorUserId")),
                    NonEmpty(Str(e, "createdByRunId")), Time(Str(e, "createdAt"))));
            }
            // Stable: equal or missing times keep the order the server gave, reversed to oldest first.
            list.Reverse();
            return list.OrderBy(c => c.At ?? DateTime.MinValue).ToList();
        }

        // ---- the task ----------------------------------------------------------

        // The rows of `issues?assigneeAgentId=<agent>`, for rung 4 of CurrentTask.
        internal static IReadOnlyList<AssignedIssue> ParseAssignedIssues(string? json)
        {
            var list = new List<AssignedIssue>();
            foreach (var e in Elements(json))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var status = Str(e, "status") ?? "";
                list.Add(new AssignedIssue(
                    new TaskRef(id, Str(e, "identifier") ?? "", Str(e, "title") ?? "", status),
                    Time(Str(e, "lastActivityAt")) ?? Time(Str(e, "updatedAt")),
                    status == "cancelled" || Str(e, "cancelledAt") != null));
            }
            return list;
        }

        // The first that exists (plan 2.2): the live run's task; the agent's blocked task that needs a person;
        // the newest finished run's task; the most recently active assigned task that is not cancelled
        // (a done one counts: a comment on it reopened it and woke the agent, measured); none.
        // `known` is every issue the poll already holds, for the identifier and title of rungs 1 to 3; an id
        // it does not hold gives a TaskRef with the id alone and the caller falls back to the poll's title.
        internal static TaskRef? CurrentTask(string agentId,
            IReadOnlyList<PaperclipApi.RawLiveRun> live, IReadOnlyList<PaperclipApi.RawIssue> blocked,
            IReadOnlyList<PaperclipApi.RawRun> finished, IReadOnlyList<AssignedIssue>? assigned,
            IReadOnlyList<PaperclipApi.RawIssue> known)
        {
            TaskRef Resolve(string id)
            {
                var k = known.FirstOrDefault(i => i.Id == id) ?? blocked.FirstOrDefault(i => i.Id == id);
                return k != null ? new TaskRef(k.Id, k.Identifier, k.Title, k.Status)
                    : assigned?.FirstOrDefault(a => a.Task.Id == id)?.Task ?? new TaskRef(id, "", "", "");
            }

            var liveRun = live.Where(r => r.AgentId == agentId && !string.IsNullOrEmpty(r.IssueId))
                .OrderByDescending(r => r.StartedAt ?? DateTime.MinValue).FirstOrDefault();
            if (liveRun != null) return Resolve(liveRun.IssueId!);

            var stuck = blocked.FirstOrDefault(i => i.AssigneeAgentId == agentId);
            if (stuck != null) return Resolve(stuck.Id);

            var last = finished.Where(r => r.AgentId == agentId && !string.IsNullOrEmpty(r.IssueId))
                .OrderByDescending(r => r.FinishedAt ?? r.StartedAt ?? DateTime.MinValue).FirstOrDefault();
            if (last != null) return Resolve(last.IssueId!);

            return assigned?.Where(a => !a.Cancelled)
                .OrderByDescending(a => a.UpdatedAt ?? DateTime.MinValue).FirstOrDefault()?.Task;
        }

        // ---- a run's events ----------------------------------------------------

        internal sealed record ToolState(string Name, string? Subject, bool Error);

        // Folded across incremental reads (afterSeq): the rows so far and the highest seq seen. The rest is
        // the join state, so a later slice of events can finish a row an earlier slice started.
        internal sealed record Activity(string RunId, long LastSeq, IReadOnlyList<Row> Rows)
        {
            internal IReadOnlyDictionary<string, ToolState> Tools { get; init; } = new Dictionary<string, ToolState>();
            internal IReadOnlySet<string> Hidden { get; init; } = new HashSet<string>();
            internal string? FinalText { get; init; }
            internal string? Summary { get; init; }

            internal static Activity Empty(string runId) => new(runId, 0, Array.Empty<Row>());
        }

        // Never throws; a body that is not an event list returns soFar. Events at or below LastSeq are
        // ignored, so feeding the same slice twice changes nothing.
        internal static Activity Fold(Activity soFar, string? eventsJson)
        {
            var events = Elements(eventsJson).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            if (events.Count == 0) return soFar;

            var rows = soFar.Rows.ToList();
            var tools = soFar.Tools.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
            var hidden = new HashSet<string>(soFar.Hidden, StringComparer.Ordinal);
            var finalText = soFar.FinalText; var summary = soFar.Summary;
            var runId = soFar.RunId; var last = soFar.LastSeq;
            var answerAt = rows.FirstOrDefault(r => r.Key == AnswerKey(runId))?.At;

            void Put(Row row)
            {
                var i = rows.FindIndex(r => r.Key == row.Key);
                if (i >= 0) rows[i] = row with { At = rows[i].At }; else rows.Add(row);
            }

            foreach (var e in events.Select(e => (E: e, Seq: Num(e, "seq"))).Where(x => x.Seq > soFar.LastSeq).OrderBy(x => x.Seq))
            {
                last = Math.Max(last, e.Seq);
                var type = Str(e.E, "eventType");
                var at = Time(Str(e.E, "createdAt")) ?? DateTime.MinValue;
                if (!e.E.TryGetProperty("payload", out var pl) || pl.ValueKind != JsonValueKind.Object
                    || !pl.TryGetProperty("prpEvent", out var prp) || prp.ValueKind != JsonValueKind.Object) continue;
                var p = prp.TryGetProperty("payload", out var ip) && ip.ValueKind == JsonValueKind.Object ? ip : default;

                switch (type)
                {
                    case "tool.execution.started":
                    case "tool.execution.progressed":
                    case "tool.execution.completed":
                    {
                        var exec = NonEmpty(Str(p, "executionId"));
                        var name = NonEmpty(Str(p, "name"));
                        if (exec == null || hidden.Contains(exec)) break;
                        // The runner's own bookkeeping (paperclip_finish), not something done for the person.
                        if (Str(p, "namespace") == "paperclip") { hidden.Add(exec); rows.RemoveAll(r => r.Key == ToolKey(runId, exec)); break; }
                        tools.TryGetValue(exec, out var prev);
                        name ??= prev?.Name;
                        if (name == null) break;
                        var subject = type == "tool.execution.progressed" ? NonEmpty(Str(p, "subject")) ?? prev?.Subject : prev?.Subject;
                        var error = prev?.Error ?? false;
                        if (type == "tool.execution.completed") error = Failed(p);
                        tools[exec] = new ToolState(name, subject, error);
                        Put(new Row(ToolKey(runId, exec), ChatRole.System, subject == null ? name : $"{name}: {subject}", at, error));
                        break;
                    }
                    case "item.completed" when Str(p, "kind") == "agentMessage":
                    {
                        var text = Str(p, "text");
                        if (string.IsNullOrEmpty(text)) break;
                        var item = NonEmpty(Str(prp, "itemId")) ?? seqKey(e.Seq);
                        if (Str(p, "channel") == "final")
                        {
                            finalText = text;
                            Put(new Row(AnswerKey(runId), ChatRole.Assistant, text, answerAt ??= at));
                        }
                        else Put(new Row(SpeechKey(runId, item), ChatRole.Assistant, text, at));
                        break;
                    }
                    case "run.result.proposed":
                    {
                        var s = Str(p, "summary");
                        if (string.IsNullOrWhiteSpace(s)) break;
                        summary = s;
                        // The summary is the answer only when no final message came.
                        if (finalText == null) Put(new Row(AnswerKey(runId), ChatRole.Assistant, s, answerAt ??= at));
                        break;
                    }
                }
            }
            return soFar with { LastSeq = last, Rows = rows, Tools = tools, Hidden = hidden, FinalText = finalText, Summary = summary };

            static string seqKey(long seq) => "seq" + seq.ToString(CultureInfo.InvariantCulture);
        }

        private static bool Failed(JsonElement p)
        {
            var status = Str(p, "status");
            if (status != null && status != "completed") return true;
            return p.ValueKind == JsonValueKind.Object && p.TryGetProperty("exitCode", out var c)
                && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) && n != 0;
        }

        internal static int CountEvents(string? eventsJson) => Elements(eventsJson).Count();

        // ---- the thread --------------------------------------------------------

        // The task row first, then comments and the newest run's activity by time. Every text is cut at
        // TextLimitBytes. A comment by this agent is Assistant, by a person User, by another agent System
        // with that agent's name. A comment that carries the activity's run id takes the answer row's place.
        internal static IReadOnlyList<Row> Build(TaskRef? task, string agentId, IReadOnlyDictionary<string, string> agentNames,
            IReadOnlyList<Comment> comments, Activity? activity)
        {
            var body = new List<Row>();
            var answerKey = activity == null ? null : AnswerKey(activity.RunId);
            var answerTaken = false;
            foreach (var c in comments)
            {
                ChatRole role; string text = c.Body;
                if (c.AuthorAgentId == agentId) role = ChatRole.Assistant;
                else if (c.AuthorAgentId == null && c.AuthorUserId != null) role = ChatRole.User;
                else if (c.AuthorAgentId != null)
                {
                    role = ChatRole.System;
                    text = (agentNames.TryGetValue(c.AuthorAgentId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "Another agent") + ": " + c.Body;
                }
                else role = ChatRole.System;
                var takes = answerKey != null && c.RunId == activity!.RunId && role == ChatRole.Assistant
                    && activity.Rows.Any(r => r.Key == answerKey);
                answerTaken |= takes;
                body.Add(new Row(CommentKey(c.Id), role, Cap(text), c.At ?? DateTime.MinValue, false, takes ? answerKey : null));
            }
            if (activity != null)
                foreach (var r in activity.Rows)
                    if (!(answerTaken && r.Key == answerKey))
                        body.Add(r with { Text = Cap(r.Text) });

            var ordered = body.Select((r, i) => (r, i)).OrderBy(x => x.r.At).ThenBy(x => x.i).Select(x => x.r);
            var taskRow = new Row(TaskKey, ChatRole.System, task == null ? "No task yet." : Cap(TaskLine(task)), DateTime.MinValue);
            return new[] { taskRow }.Concat(ordered).ToList();
        }

        internal static string TaskLine(TaskRef t)
        {
            var head = string.Join(" ", new[] { t.Identifier, t.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (head.Length == 0) head = "Task";
            return string.IsNullOrWhiteSpace(t.Status) ? head : head + " · " + t.Status.Replace('_', ' ');
        }

        // ---- keeping the list stable (plan 2.5) --------------------------------

        // Once shown a row keeps its place. A built row with a shown key updates that row's text and error in
        // place; one that Replaces a shown key takes its place under its own key; a local unkeyed user bubble
        // ("u:...") is adopted by a built user row with the same text; the rest append in time order.
        // At most the newest RowCap rows are held (the task row always stays). A change of task is the
        // caller's to handle by replacing the list.
        internal static IReadOnlyList<Row> Merge(IReadOnlyList<Row> shown, IReadOnlyList<Row> built)
        {
            var result = shown.ToList();
            var fresh = new List<(Row Row, int Order)>();
            var n = 0;
            foreach (var b in built)
            {
                n++;
                var i = result.FindIndex(r => r.Key == b.Key);
                if (i < 0 && b.Replaces != null) i = result.FindIndex(r => r.Key == b.Replaces);
                if (i < 0 && b.Role == ChatRole.User)
                    i = result.FindIndex(r => r.Key.StartsWith(LocalPrefix, StringComparison.Ordinal) && r.Role == ChatRole.User && r.Text == b.Text);
                if (i >= 0) result[i] = b with { At = result[i].At, Replaces = null };
                else fresh.Add((b, n));
            }
            result.AddRange(fresh.OrderBy(f => f.Row.At).ThenBy(f => f.Order).Select(f => f.Row with { Replaces = null }));
            if (result.Count > RowCap)
            {
                var keepTask = result.Count > 0 && result[0].Key == TaskKey;
                var tail = result.Skip(result.Count - (keepTask ? RowCap - 1 : RowCap));
                result = (keepTask ? new[] { result[0] } : Array.Empty<Row>()).Concat(tail).ToList();
            }
            return result;
        }

        // ---- the box -----------------------------------------------------------

        // The words in the empty message box (plan 1.2). `agentStatus` is Paperclip's own ("paused", ...);
        // `wakingSince` is when the person last sent and no run has shown since (null otherwise).
        internal static string Hint(string agentName, string? agentStatus, bool hasTask, bool runLive,
            PaperclipLink link, DateTime? wakingSince, DateTime now)
        {
            if (link == PaperclipLink.NeedsReconnect) return "Reconnect to Paperclip";
            if (agentStatus == "paused") return $"{agentName} is paused in Paperclip. It reads this when resumed.";
            if (runLive) return $"{agentName} is working. It reads a message when this run ends.";
            if (wakingSince is { } since)
                return now - since >= WakeGrace
                    ? $"{agentName} hasn't woken yet. Wake it from the orb's menu."
                    : $"Waking {agentName}…";
            return hasTask ? $"Message {agentName}. It wakes to read it." : $"Message {agentName}. This opens a new task.";
        }

        // ---- small helpers -----------------------------------------------------

        internal static string Cap(string s)
        {
            if (Encoding.UTF8.GetByteCount(s) <= TextLimitBytes) return s;
            var budget = TextLimitBytes - Encoding.UTF8.GetByteCount("…");
            var sb = new StringBuilder(); var used = 0;
            foreach (var r in s.EnumerateRunes())
            {
                var w = r.Utf8SequenceLength;
                if (used + w > budget) break;
                sb.Append(r.ToString()); used += w;
            }
            return sb.Append('…').ToString();
        }

        // An array body, or an object that wraps one under a common key.
        private static IEnumerable<JsonElement> Elements(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<JsonElement>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var k in new[] { "events", "comments", "items", "data" })
                        if (root.TryGetProperty(k, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }
                }
                return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().Select(x => x.Clone()).ToList() : Array.Empty<JsonElement>();
            }
            catch (JsonException) { return Array.Empty<JsonElement>(); }
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static DateTime? Time(string? s) =>
            s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.UtcDateTime : null;
    }
}
