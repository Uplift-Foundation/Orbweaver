using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // One approval the gateway is holding until somebody says yes or no, as this
    // app understands it (CB-270 Stage 6).
    //
    // Pure, for the same reason OpenClawSender and OpenClawRetention are: what it
    // decides is what a person is about to click on, and the only other way to
    // check it is to arrange for a real agent to ask for permission. Everything
    // here reads a shape nobody in this repository controls, so the rules degrade
    // toward "no record" rather than toward a record that says something the
    // gateway did not.
    //
    // The shapes are **declared, not measured**. They were read out of
    // openclaw 2026.10.5's own dist on the gateway host (the list row, the
    // requested event, the resolved event, the resolve result); nothing has yet
    // been called against a live approval, because that needs the device paired
    // with operator.approvals and a person on the gateway host to approve the
    // pairing. The fixtures that pin these parsers say "declared" in their
    // comments for the same reason. See docs/openclaw-findings.md, CB-270 Stage 6.
    //
    // Command is what the gateway says will run, drawn as plain text and never
    // markup. It is bounded and stripped of characters that could make one string
    // read as another (see Clean), but it is **not** shortened silently: a command
    // too long to show whole is marked Truncated, so the panel can refuse to offer
    // "Allow once" for a command nobody saw the end of.
    internal sealed record OpenClawApproval(
        string Id,
        string Kind,
        string SessionKey,
        string? AgentId,
        string Command,
        string? Cwd,
        string? Host,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        bool CommandTruncated = false,

        // The decisions the gateway says a reviewer may give this one, null when it
        // did not say (a legacy list row without the field). `deny` is always in it
        // when it is present; `allow-once` can be absent, and then the panel must
        // not offer it. Declared in the gateway's approval schemas, not measured.
        IReadOnlyList<string>? AllowedDecisions = null,

        // The gateway's own warning about this request, where it carries one.
        string? Warning = null)
    {
        // A record compares by value, and the decision list has to as well: the same
        // approval read from a list row and from an event is one record.
        public bool Equals(OpenClawApproval? other) =>
            other is not null
            && Id == other.Id && Kind == other.Kind && SessionKey == other.SessionKey
            && AgentId == other.AgentId && Command == other.Command && Cwd == other.Cwd
            && Host == other.Host && CreatedAt == other.CreatedAt && ExpiresAt == other.ExpiresAt
            && CommandTruncated == other.CommandTruncated && Warning == other.Warning
            && (AllowedDecisions is null
                ? other.AllowedDecisions is null
                : other.AllowedDecisions is not null && AllowedDecisions.SequenceEqual(other.AllowedDecisions));

        public override int GetHashCode() => HashCode.Combine(Id, Kind, SessionKey, Command, ExpiresAt);

        // Whether this is an answer the gateway has not ruled out. Nothing said
        // means nothing ruled out.
        internal bool Allows(string decision) =>
            AllowedDecisions is null || AllowedDecisions.Contains(decision);
    }

    // A requested approval that is no longer pending, whoever or whatever ended
    // it. The event carries the decision (null when nobody made one); the
    // gateway's reason (user, timeout, run-aborted, …) is on the snapshots the
    // resolve call returns rather than on the event, so it is carried when
    // present and null otherwise.
    internal sealed record OpenClawApprovalResolved(
        string Id, string Kind, string? Decision, string? Reason, string? ResolvedBy);

    // One `session.approval` event, or one entry of a subscribe's approvalReplay,
    // read: a pending transition carries the approval, a terminal one carries how
    // it ended. Declared in the gateway's schemas (since 2026.7), not measured.
    internal sealed record OpenClawSessionApproval(
        string Phase, OpenClawApproval? Pending, OpenClawApprovalResolved? Terminal);

    // The subscribe response's approvalReplay: the authoritative pending set for
    // the session, and whether the gateway cut it short.
    internal sealed record OpenClawApprovalReplay(IReadOnlyList<OpenClawApproval> Approvals, bool Truncated);

    // What the gateway answered when asked to resolve one: `applied` says whether
    // this call decided it, and a false carries what the other reviewer decided.
    internal sealed record OpenClawApprovalAnswer(bool Applied, string? Status, string? Reason, string? Decision);

    internal static class OpenClawApprovals
    {
        // The only two answers this app can send. allow-always exists on the
        // gateway and mints a standing grant this app has no surface to list or
        // revoke, so it is not an option, and a decision outside this set is
        // refused before anything is sent. See Stage 6's plan, section 3.3.
        internal const string Deny = "deny";
        internal const string AllowOnce = "allow-once";

        internal static bool IsAnswerableDecision(string? decision) =>
            decision is Deny or AllowOnce;

        // The kinds approval.resolve takes.
        internal const string ExecKind = "exec";
        internal const string PluginKind = "plugin";
        internal const string SystemAgentKind = "system-agent";

        // Per-session ceiling on what is held, so a flood from a hostile or buggy
        // gateway cannot wedge a panel or grow without bound. The oldest are
        // shown first and the rest queue behind them.
        internal const int MaxPerSession = 20;

        // Bounds on what is drawn. Generous for a command, deliberately: a script
        // is long. Past MaxCommand the text is cut and the record says so.
        internal const int MaxIdLength = 200;
        internal const int MaxKeyLength = 400;
        internal const int MaxCommand = 2000;
        internal const int MaxShort = 400;

        // The exec default on the gateway (`timeoutMs ?? 18e5`): what a record
        // that did not say when it expires is given, counted from when it was
        // created.
        internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

        // `<kind>.approval.<requested|resolved>`; the system agent's is
        // `openclaw.approval.*`. Anything else is not an approval event.
        internal static string? KindOfEvent(string name) => name switch
        {
            "exec.approval.requested" or "exec.approval.resolved" => ExecKind,
            "plugin.approval.requested" or "plugin.approval.resolved" => PluginKind,
            "openclaw.approval.requested" or "openclaw.approval.resolved" => SystemAgentKind,
            _ => null,
        };

        // The session-scoped event, delivered only to a connection that subscribed
        // with includeApprovals and may see the record. The one path that does not
        // depend on the requester fields the broadcasts and lists are filtered by.
        internal const string SessionEventName = "session.approval";

        internal static bool IsSessionEvent(string name) => name == SessionEventName;

        internal static bool IsRequestedEvent(string name) =>
            KindOfEvent(name) is not null && name.EndsWith(".requested", StringComparison.Ordinal);

        internal static bool IsResolvedEvent(string name) =>
            KindOfEvent(name) is not null && name.EndsWith(".resolved", StringComparison.Ordinal);

        // The list methods, one per kind. Each answers a bare array of rows in the
        // same shape the requested event carries.
        internal static string ListMethodFor(string kind) => kind switch
        {
            ExecKind => "exec.approval.list",
            PluginKind => "plugin.approval.list",
            _ => "openclaw.approval.list",
        };

        internal static readonly string[] Kinds = { ExecKind, PluginKind, SystemAgentKind };

        // One approval, from a list row or from a requested event (the gateway
        // sends the same record both ways). Null for anything that cannot be drawn
        // honestly: no id, no session to hang it on, or a control character in an
        // identifier. The session key may be run-scoped ("…:run:<runId>") and is
        // trimmed back to the session's own, the same way a run event's is.
        internal static OpenClawApproval? ParseRecord(JsonElement row, string kind, DateTimeOffset now)
        {
            if (row.ValueKind != JsonValueKind.Object) return null;

            var id = Str(row, "id");
            if (string.IsNullOrWhiteSpace(id) || id!.Length > MaxIdLength || OpenClawSocket.HasControlCharacter(id))
                return null;

            // The record's own approvalKind wins over the one the caller assumed:
            // a list read from one method can carry another kind's rows.
            var rowKind = Str(row, "approvalKind");
            var resolvedKind = rowKind is ExecKind or PluginKind or SystemAgentKind ? rowKind : kind;

            var request = row.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object
                ? req
                : default;

            var sessionKey = Str(request, "sessionKey") ?? Str(row, "sourceSessionKey");
            if (string.IsNullOrWhiteSpace(sessionKey)
                || sessionKey!.Length > MaxKeyLength
                || OpenClawSocket.HasControlCharacter(sessionKey))
                return null;

            var run = sessionKey.IndexOf(":run:", StringComparison.Ordinal);
            if (run > 0) sessionKey = sessionKey[..run];

            var agent = Str(request, "agentId");
            if (agent is not null && (agent.Length > MaxIdLength || OpenClawSocket.HasControlCharacter(agent)))
                agent = null;

            var (command, truncated) = CommandOf(request);

            var created = TimeOf(row, "createdAtMs") ?? now;
            var expires = TimeOf(row, "expiresAtMs") ?? created + DefaultLifetime;

            return new OpenClawApproval(
                id, resolvedKind!, sessionKey, agent, command,
                Short(Str(request, "cwd")), Short(Str(request, "host")),
                created, expires, truncated,
                DecisionsOf(request), Short(Str(request, "warningText")));
        }

        // The reviewer-safe snapshot the gateway's session surfaces carry: an id,
        // a status, and a `presentation` that says what is being asked, with the
        // runtime's cwd and environment deliberately left out. The session key is
        // the caller's: an event names it, and a subscribe's replay is read under
        // the key that was subscribed, which is the one this app holds a panel on.
        // Null for anything that is not a well-formed pending snapshot.
        internal static OpenClawApproval? ParseSnapshot(JsonElement snapshot, string sessionKey, DateTimeOffset now)
        {
            if (snapshot.ValueKind != JsonValueKind.Object) return null;

            var id = Str(snapshot, "id");
            if (string.IsNullOrWhiteSpace(id) || id!.Length > MaxIdLength || OpenClawSocket.HasControlCharacter(id))
                return null;

            if (string.IsNullOrWhiteSpace(sessionKey)
                || sessionKey.Length > MaxKeyLength
                || OpenClawSocket.HasControlCharacter(sessionKey))
                return null;

            var run = sessionKey.IndexOf(":run:", StringComparison.Ordinal);
            if (run > 0) sessionKey = sessionKey[..run];

            if (!snapshot.TryGetProperty("presentation", out var presentation)
                || presentation.ValueKind != JsonValueKind.Object)
                return null;

            var kind = Str(presentation, "kind");
            if (kind is not (ExecKind or PluginKind or SystemAgentKind)) return null;

            var text = kind == ExecKind
                ? Str(presentation, "commandText") ?? Str(presentation, "commandPreview")
                : Join(Str(presentation, "title"), Str(presentation, "description"))
                  ?? Str(presentation, "toolName");

            var (command, truncated) = BoundCommand(text);
            var agent = Str(presentation, "agentId");
            if (agent is not null && (agent.Length > MaxIdLength || OpenClawSocket.HasControlCharacter(agent)))
                agent = null;

            var created = TimeOf(snapshot, "createdAtMs") ?? now;
            var expires = TimeOf(snapshot, "expiresAtMs") ?? created + DefaultLifetime;

            return new OpenClawApproval(
                id, kind!, sessionKey, agent, command, null, Short(Str(presentation, "host")),
                created, expires, truncated,
                DecisionsOf(presentation), Short(Str(presentation, "warningText")));
        }

        // A `session.approval` event: phase pending carries a pending snapshot,
        // phase terminal carries how it ended. The audience key is `sessionKey`.
        internal static OpenClawSessionApproval? ParseSessionEvent(JsonElement payload, DateTimeOffset now)
        {
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("approval", out var approval)
                || approval.ValueKind != JsonValueKind.Object)
                return null;

            var key = Str(payload, "sessionKey");
            if (string.IsNullOrWhiteSpace(key)) return null;

            switch (Str(payload, "phase"))
            {
                case "pending":
                {
                    var pending = ParseSnapshot(approval, key!, now);
                    return pending is null ? null : new OpenClawSessionApproval("pending", pending, null);
                }

                case "terminal":
                {
                    var id = Str(approval, "id");
                    if (string.IsNullOrWhiteSpace(id) || id!.Length > MaxIdLength || OpenClawSocket.HasControlCharacter(id))
                        return null;

                    var presentation = approval.TryGetProperty("presentation", out var p) ? p : default;
                    var resolver = approval.TryGetProperty("resolver", out var r) ? r : default;

                    return new OpenClawSessionApproval("terminal", null, new OpenClawApprovalResolved(
                        id, Str(presentation, "kind") ?? ExecKind,
                        Short(Str(approval, "decision")),
                        Short(Str(approval, "reason")) ?? Short(Str(approval, "status")),
                        Short(Str(resolver, "id") ?? Str(resolver, "kind"))));
                }

                default:
                    return null;
            }
        }

        // The subscribe response: `{subscribed, key, agentId, approvalReplay}`, where
        // approvalReplay is `{sessionKey, updatedAtMs, approvals, truncated}`. Null
        // when the response carries none, which is also what a subscribe made
        // without includeApprovals answers.
        internal static OpenClawApprovalReplay? ParseReplay(JsonElement response, string key, DateTimeOffset now)
        {
            if (response.ValueKind != JsonValueKind.Object
                || !response.TryGetProperty("approvalReplay", out var replay)
                || replay.ValueKind != JsonValueKind.Object
                || !replay.TryGetProperty("approvals", out var approvals)
                || approvals.ValueKind != JsonValueKind.Array)
                return null;

            var parsed = new List<OpenClawApproval>();
            foreach (var row in approvals.EnumerateArray())
            {
                if (ParseSnapshot(row, key, now) is { } approval) parsed.Add(approval);
            }

            var truncated = replay.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
            return new OpenClawApprovalReplay(parsed, truncated);
        }

        // The decisions listed, only the well-formed strings, null when there are
        // none to read: absent, not an array, or empty all mean "did not say".
        private static IReadOnlyList<string>? DecisionsOf(JsonElement holder)
        {
            if (!holder.TryGetProperty("allowedDecisions", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;

            var decisions = list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(d => d.Length is > 0 and <= 40 && !OpenClawSocket.HasControlCharacter(d))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();

            return decisions.Count == 0 ? null : decisions;
        }

        private static string? Join(string? title, string? description) =>
            string.IsNullOrWhiteSpace(title) ? description
            : string.IsNullOrWhiteSpace(description) ? title
            : title + " — " + description;

        // A requested event: its payload is a record, and the name says the kind.
        internal static OpenClawApproval? ParseRequested(string name, JsonElement payload, DateTimeOffset now) =>
            IsRequestedEvent(name) ? ParseRecord(payload, KindOfEvent(name)!, now) : null;

        // A resolved event: `{id, decision, resolvedBy, ts, request}`. Only the id
        // is needed to take the record off; the rest is carried for the line the
        // panel writes about it.
        internal static OpenClawApprovalResolved? ParseResolved(string name, JsonElement payload)
        {
            if (!IsResolvedEvent(name) || payload.ValueKind != JsonValueKind.Object) return null;

            var id = Str(payload, "id");
            if (string.IsNullOrWhiteSpace(id) || id!.Length > MaxIdLength || OpenClawSocket.HasControlCharacter(id))
                return null;

            return new OpenClawApprovalResolved(
                id, KindOfEvent(name)!,
                Short(Str(payload, "decision")),
                Short(Str(payload, "reason")),
                Short(Str(payload, "resolvedBy")));
        }

        // A list reply: the declared shape is a bare array, and an object holding
        // one under a likely name is accepted too, since the system agent's list
        // was not among the shapes read. Rows that cannot be parsed are skipped,
        // not fatal: one malformed row must not hide the rest.
        internal static List<OpenClawApproval> ParseList(JsonElement result, string kind, DateTimeOffset now)
        {
            var parsed = new List<OpenClawApproval>();

            var rows = result;
            if (result.ValueKind == JsonValueKind.Object)
            {
                rows = default;
                foreach (var name in new[] { "approvals", "items", "pending", "rows" })
                {
                    if (result.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Array)
                    {
                        rows = found;
                        break;
                    }
                }
            }

            if (rows.ValueKind != JsonValueKind.Array) return parsed;

            foreach (var row in rows.EnumerateArray())
            {
                if (ParseRecord(row, kind, now) is { } approval) parsed.Add(approval);
            }

            return parsed;
        }

        // approval.resolve's result: `{applied, approval}`, where `approval` is the
        // terminal snapshot. applied false means another reviewer decided first and
        // the snapshot is what they decided. A reply that says neither is not
        // treated as applied: claiming an answer landed that the gateway did not
        // confirm would be a guess presented as a result.
        internal static OpenClawApprovalAnswer? ParseResolveResult(JsonElement result)
        {
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("applied", out var applied)
                || applied.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            var snapshot = result.TryGetProperty("approval", out var a) && a.ValueKind == JsonValueKind.Object
                ? a
                : default;

            return new OpenClawApprovalAnswer(
                applied.ValueKind == JsonValueKind.True,
                Short(Str(snapshot, "status")),
                Short(Str(snapshot, "reason")),
                Short(Str(snapshot, "decision")));
        }

        // Whether a resolve refusal means "this id is not pending (any more, or to
        // this device)". The gateway deliberately does not say which of those it
        // is, and its message is the only signal, so this is matched on it.
        internal static bool IsNotFound(Exception ex) =>
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase);

        internal static bool IsExpired(OpenClawApproval approval, DateTimeOffset now) =>
            approval.ExpiresAt <= now;

        // The command as it will be drawn, and whether it had to be cut. Falls
        // back through the fields a request carries so a plugin approval, which
        // has no shell command, still says what it is asking about.
        private static (string Text, bool Truncated) CommandOf(JsonElement request)
        {
            var text = Str(request, "command")
                       ?? ArgvOf(request)
                       ?? Str(request, "title")
                       ?? Str(request, "description")
                       ?? Str(request, "toolName");

            if (string.IsNullOrWhiteSpace(text)) return ("(the gateway did not say what this runs)", false);

            return BoundCommand(text);
        }

        // The text as it will be drawn, cut at the bound and marked when it was.
        private static (string Text, bool Truncated) BoundCommand(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ("(the gateway did not say what this runs)", false);

            var cleaned = Clean(text!.Trim());
            return cleaned.Length > MaxCommand
                ? (cleaned[..MaxCommand], true)
                : (cleaned, false);
        }

        private static string? ArgvOf(JsonElement request)
        {
            if (!request.TryGetProperty("commandArgv", out var argv) || argv.ValueKind != JsonValueKind.Array)
                return null;

            var parts = argv.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();

            return parts.Count == 0 ? null : string.Join(' ', parts);
        }

        private static string? Short(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var cleaned = Clean(text!.Trim());
            return cleaned.Length > MaxShort ? cleaned[..MaxShort] : cleaned;
        }

        // Display text from the gateway, made safe to draw. Newlines and tabs stay
        // (a script is several lines); every other control character, and the
        // bidirectional controls that can make one string read as another, become
        // U+FFFD, so a command cannot hide its own tail or reorder itself. Nothing
        // is refused: a legitimate multi-line command must still be answerable.
        internal static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var c in text)
            {
                var control = (c < ' ' && c != '\n' && c != '\t') || c == '\u007f';
                var bidi = c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏';
                sb.Append(control || bidi ? '�' : c);
            }

            return sb.ToString();
        }

        private static DateTimeOffset? TimeOf(JsonElement row, string name) =>
            row.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var ms)
            && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
