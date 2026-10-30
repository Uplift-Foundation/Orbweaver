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
        string? Warning = null,

        // A displayed field contained a character that could make one string read
        // as another: a control, format or line/paragraph-separator code point, or
        // a lone surrogate. Such text is not shown at all and never rewritten into
        // clean-looking text: Command, Cwd, Host and Warning are blank, the record
        // is kept so the orb still goes amber, and the panel offers Deny only.
        bool Unsafe = false,

        // The command's length as the gateway sent it, before any cut, so the panel
        // can say how much it did not show.
        int CommandLength = 0)
    {
        // A record compares by value, and the decision list has to as well: the same
        // approval read from a list row and from an event is one record.
        public bool Equals(OpenClawApproval? other) =>
            other is not null
            && Id == other.Id && Kind == other.Kind && SessionKey == other.SessionKey
            && AgentId == other.AgentId && Command == other.Command && Cwd == other.Cwd
            && Host == other.Host && CreatedAt == other.CreatedAt && ExpiresAt == other.ExpiresAt
            && CommandTruncated == other.CommandTruncated && Warning == other.Warning
            && Unsafe == other.Unsafe && CommandLength == other.CommandLength
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
        string Id, string Kind, string? Decision, string? Reason, string? ResolvedBy, string? Status = null);

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
        // The status of an ending nothing explained: a row a list stopped showing with
        // no event, or one pushed out by the cap.
        internal const string UnknownStatus = "unknown";

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
        internal const int MaxPerSession = 8;

        // And overall. On overflow the oldest by arrival is evicted and the panel
        // open on its session is told, so a flood from a hostile gateway can neither
        // grow this without bound nor push a real request out of sight behind
        // stale ones: the newest are kept.
        internal const int MaxTotal = 64;

        // Rows read from one list or replay; the rest are ignored.
        internal const int MaxListRows = 500;

        // Bounds on what is drawn. Generous for a command, deliberately: a script
        // is long. Past MaxCommand the text is cut and the record says so, and a cut
        // command is Deny only.
        //
        // An id is the gateway's own: non-empty, at most 512 characters, no unpaired
        // surrogate (its isWellFormedApprovalId admits anything else), so the pattern
        // is not tightened here, or a real approval would become unanswerable. It is
        // data: it only ever goes into a JSON frame.
        internal const int MaxIdLength = 512;
        internal const int MaxKeyLength = 400;
        internal const int MaxCommand = 4000;
        internal const int MaxShort = 1024;

        // The exec default on the gateway (`timeoutMs ?? 18e5`): what a record
        // that did not say when it expires is given, counted from now.
        internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

        // The gateway's expiry is never trusted for control. Local expiry is its
        // value clamped to between a second and a day from arrival: a date years
        // ahead cannot pin a prompt, and one in the past is shown for a moment
        // rather than dropped unseen. The poll is the authority either way.
        internal static readonly TimeSpan MinLifetime = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

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

        // --- what may be drawn -------------------------------------------------
        //
        // HasControlCharacter stops only C0 and DEL. These stop every code point
        // that can make one string read as another.

        // Whether text is safe to draw as it is: no control (Cc, which includes C1
        // and NEL), format (Cf: bidi overrides and isolates, zero-width characters,
        // the BOM), line or paragraph separator (Zl, Zp), and no unpaired surrogate.
        // `multiLine` allows the two a real script uses, newline and tab, and only
        // for the command: everywhere else even those are refused.
        internal static bool IsDisplaySafe(string? text, bool multiLine = false)
        {
            if (text is null) return true;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    i++;
                    continue;
                }

                if (char.IsLowSurrogate(c)) return false;

                if (multiLine && c is '\n' or '\t') continue;

                if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control
                    or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator)
                    return false;
            }

            return true;
        }

        // An identifier is the gateway's own: non-empty, within the bound, not "."
        // or "..", and safe to show. Never stored otherwise.
        internal static bool IsWellFormedId(string? id) =>
            !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength
            && id is not ("." or "..") && IsDisplaySafe(id);

        // One approval, from a list row or from a requested event (the gateway
        // sends the same record both ways). Null for anything that cannot be kept:
        // a malformed id, no session to hang it on, an unknown kind. Nothing here
        // can throw whatever the row holds: every read is checked for its type
        // first, and a time is only ever compared, never added to.
        //
        // The session key may be run-scoped ("...:run:<runId>") and is trimmed back
        // to the session's own, the same way a run event's is.
        internal static OpenClawApproval? ParseRecord(JsonElement row, string kind, DateTimeOffset now)
        {
            if (row.ValueKind != JsonValueKind.Object) return null;

            var id = Str(row, "id");
            if (!IsWellFormedId(id)) return null;

            // A kind outside the three is refused, whatever the caller assumed: it
            // is sent back on a resolve, so a hostile one is not stored.
            var rowKind = Str(row, "approvalKind");
            if (rowKind is not null && rowKind is not (ExecKind or PluginKind or SystemAgentKind)) return null;
            var resolvedKind = rowKind ?? kind;

            var request = row.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object
                ? req
                : default;

            var sessionKey = SessionKeyOf(Str(request, "sessionKey") ?? Str(row, "sourceSessionKey"));
            if (sessionKey is null) return null;

            var text = Str(request, "command")
                       ?? ArgvOf(request)
                       ?? Str(request, "title")
                       ?? Str(request, "description")
                       ?? Str(request, "toolName");

            return Build(
                id!, resolvedKind, sessionKey, text,
                Str(request, "agentId"), Str(request, "cwd"), Str(request, "host"),
                Str(request, "warningText"), DecisionsOf(request),
                TimeOf(row, "createdAtMs"), TimeOf(row, "expiresAtMs"), now);
        }

        // The reviewer-safe snapshot the gateway's session surfaces carry: an id,
        // a status, and a `presentation` that says what is being asked, with the
        // runtime's cwd and environment deliberately left out. The session key is
        // the caller's: an event names it, and a subscribe's replay is read under
        // the key that was subscribed, which is the one this app holds a panel on.
        internal static OpenClawApproval? ParseSnapshot(JsonElement snapshot, string sessionKey, DateTimeOffset now)
        {
            if (snapshot.ValueKind != JsonValueKind.Object) return null;

            var id = Str(snapshot, "id");
            if (!IsWellFormedId(id)) return null;

            var key = SessionKeyOf(sessionKey);
            if (key is null) return null;

            if (!snapshot.TryGetProperty("presentation", out var presentation)
                || presentation.ValueKind != JsonValueKind.Object)
                return null;

            var kind = Str(presentation, "kind");
            if (kind is not (ExecKind or PluginKind or SystemAgentKind)) return null;

            var text = kind == ExecKind
                ? Str(presentation, "commandText") ?? Str(presentation, "commandPreview")
                : Join(Str(presentation, "title"), Str(presentation, "description"))
                  ?? Str(presentation, "toolName");

            return Build(
                id!, kind, key, text,
                Str(presentation, "agentId"), null, Str(presentation, "host"),
                Str(presentation, "warningText"), DecisionsOf(presentation),
                TimeOf(snapshot, "createdAtMs"), TimeOf(snapshot, "expiresAtMs"), now);
        }

        // The one place a record is made, so every feed is held to the same rules.
        private static OpenClawApproval Build(
            string id, string kind, string sessionKey, string? commandText, string? agent,
            string? cwd, string? host, string? warning, IReadOnlyList<string>? decisions,
            DateTimeOffset? created, DateTimeOffset? expires, DateTimeOffset now)
        {
            // \r\n is one line break; a lone \r is a control character and stays one.
            var command = (string.IsNullOrWhiteSpace(commandText)
                ? "(the gateway did not say what this runs)"
                : commandText!.Trim()).Replace("\r\n", "\n", StringComparison.Ordinal);

            var length = command.Length;
            var unsafeText = !IsDisplaySafe(command, multiLine: true)
                             || !IsDisplaySafe(cwd) || !IsDisplaySafe(host)
                             || !IsDisplaySafe(warning) || !IsDisplaySafe(agent)
                             || (agent is not null && agent.Length > MaxIdLength);

            var truncated = length > MaxCommand;
            if (truncated)
            {
                var cut = MaxCommand;
                // Never through the middle of a surrogate pair.
                if (char.IsHighSurrogate(command[cut - 1])) cut--;
                command = command[..cut];
            }

            // Local expiry: the gateway's value clamped to [now + 1 s, now + 24 h],
            // or now + 30 min when it said nothing usable. Compared, never added to.
            var earliest = now + MinLifetime;
            var latest = now + MaxLifetime;
            var expiry = expires is null ? now + DefaultLifetime
                : expires.Value < earliest ? earliest
                : expires.Value > latest ? latest
                : expires.Value;

            if (unsafeText)
            {
                // Not shown, not rewritten: the fields are blank, the flag is set and
                // the record is kept so the orb is still amber.
                return new OpenClawApproval(
                    id, kind, sessionKey, null, "", null, null, created ?? now, expiry,
                    truncated, decisions, null, Unsafe: true, CommandLength: length);
            }

            return new OpenClawApproval(
                id, kind, sessionKey, agent, command, Bound(cwd), Bound(host), created ?? now, expiry,
                truncated, decisions, Bound(warning), Unsafe: false, CommandLength: length);
        }

        // A session key we can attach an approval to, trimmed of a run suffix, or
        // null: blank, too long, or not safe to compare as data.
        private static string? SessionKeyOf(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || !IsDisplaySafe(key)) return null;

            var run = key.IndexOf(":run:", StringComparison.Ordinal);
            return run > 0 ? key[..run] : key;
        }

        // A requested event: its payload is a record, and the name says the kind.
        internal static OpenClawApproval? ParseRequested(string name, JsonElement payload, DateTimeOffset now) =>
            IsRequestedEvent(name) ? ParseRecord(payload, KindOfEvent(name)!, now) : null;

        // A resolved event. **No schema is declared for the broadcast**: the gateway
        // builds it inline, and the only declared vocabulary for a resolved record is
        // the terminal snapshot: `status` (allowed, denied, expired or cancelled),
        // `reason`, `decision` (allowed and denied only), `resolvedAtMs` and a
        // `resolver` attribution object. So those are read, and `resolvedBy` (a
        // string) is accepted as an assumed alias, as the first read of the dist had
        // it. A live capture confirms which the broadcast really sends.
        internal static OpenClawApprovalResolved? ParseResolved(string name, JsonElement payload)
        {
            if (!IsResolvedEvent(name) || payload.ValueKind != JsonValueKind.Object) return null;

            var id = Str(payload, "id");
            if (!IsWellFormedId(id)) return null;

            var resolver = payload.TryGetProperty("resolver", out var r) ? r : default;

            return new OpenClawApprovalResolved(
                id!, KindOfEvent(name)!,
                Short(Str(payload, "decision")),
                Short(Str(payload, "reason")),
                Short(Str(resolver, "id") ?? Str(resolver, "kind") ?? Str(payload, "resolvedBy")),
                Short(Str(payload, "status")));
        }

        // A list reply: the declared shape is a bare array, and an object holding
        // one under a likely name is accepted too, since the system agent's list
        // was not among the shapes read. Rows that cannot be parsed are skipped,
        // not fatal: one malformed row must not hide the rest, and no row can make
        // this throw. At most MaxListRows are read.
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

            foreach (var row in rows.EnumerateArray().Take(MaxListRows))
            {
                if (ParseRecord(row, kind, now) is { } approval) parsed.Add(approval);
            }

            return parsed;
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
                    if (!IsWellFormedId(id)) return null;

                    var presentation = approval.TryGetProperty("presentation", out var p) ? p : default;
                    var resolver = approval.TryGetProperty("resolver", out var r) ? r : default;
                    var status = Short(Str(approval, "status"));

                    return new OpenClawSessionApproval("terminal", null, new OpenClawApprovalResolved(
                        id!, Str(presentation, "kind") is ExecKind or PluginKind or SystemAgentKind
                            ? Str(presentation, "kind")! : ExecKind,
                        Short(Str(approval, "decision")),
                        Short(Str(approval, "reason")) ?? status,
                        Short(Str(resolver, "id") ?? Str(resolver, "kind")),
                        status));
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
            foreach (var row in approvals.EnumerateArray().Take(MaxListRows))
            {
                if (ParseSnapshot(row, key, now) is { } approval) parsed.Add(approval);
            }

            var truncated = replay.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
            return new OpenClawApprovalReplay(parsed, truncated);
        }

        // approval.resolve's result: `{applied, approval}`, where `approval` is the
        // terminal snapshot (status allowed, denied, expired or cancelled). applied
        // false means another reviewer decided first and the snapshot is what they
        // decided. A reply that says neither is not treated as applied: claiming an
        // answer landed that the gateway did not confirm would be a guess presented
        // as a result.
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

        // Whether a resolve refusal is the gateway's own "this id is not pending (any
        // more, or to this device)". Exactly that: an INVALID_REQUEST carrying one of
        // the gateway's literal sentences for it, and nothing looser. A refusal that
        // merely contains the word "unknown" or "expired" is a failure like any other,
        // because treating it as not-found would take a real pending approval out of
        // sight and strand it. The gateway's detail for these is `details.reason`,
        // which this client does not read, so the message is the signal.
        internal static bool IsNotFound(Exception ex) =>
            ex is OpenClawRequestException { Code: "INVALID_REQUEST" }
            && ex.Message is "unknown or expired approval id"
                or "approval expired or not found"
                or "approval already resolved";

        internal static bool IsExpired(OpenClawApproval approval, DateTimeOffset now) =>
            approval.ExpiresAt <= now;

        private static string? ArgvOf(JsonElement request)
        {
            if (!request.TryGetProperty("commandArgv", out var argv) || argv.ValueKind != JsonValueKind.Array)
                return null;

            var parts = argv.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(SafeString)
                .ToList();

            return parts.Count == 0 ? null : string.Join(' ', parts);
        }

        private static string? Join(string? title, string? description) =>
            string.IsNullOrWhiteSpace(title) ? description
            : string.IsNullOrWhiteSpace(description) ? title
            : title + " — " + description;

        // The decisions listed, only the well-formed strings, null when there are
        // none to read: absent, not an array, or empty all mean "did not say".
        private static IReadOnlyList<string>? DecisionsOf(JsonElement holder)
        {
            if (!holder.TryGetProperty("allowedDecisions", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;

            var decisions = list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(SafeString)
                .Where(d => d.Length is > 0 and <= 40 && IsDisplaySafe(d))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();

            return decisions.Count == 0 ? null : decisions;
        }

        // Display text that is not decision-relevant (cwd, host, warning), cut at its
        // bound. Safety was checked before this is reached.
        private static string? Bound(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null
            : text.Trim().Length > MaxShort ? text.Trim()[..MaxShort] : text.Trim();

        // Short bookkeeping strings from an ending (a reason, a status), refused
        // rather than cleaned if unsafe, since they are only ever drawn after being
        // looked up against known words, and an unknown one is shown as unknown.
        private static string? Short(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var trimmed = text.Trim();
            return trimmed.Length <= 120 && IsDisplaySafe(trimmed) ? trimmed : null;
        }

        // A time from the record, or null for anything that is not a whole number of
        // milliseconds inside the range a DateTimeOffset can hold. Out of range is
        // ignored, not thrown: a row with a year-9999 date must not take the poll
        // down.
        private static DateTimeOffset? TimeOf(JsonElement row, string name) =>
            row.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var ms)
            && ms > 0
            && ms <= MaxMilliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;

        // 9999-12-31T23:59:59.999Z, the last instant a DateTimeOffset holds, and
        // comfortably clear of overflow when a day is added to now.
        private const long MaxMilliseconds = 253402300799999;

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? SafeString(v)
                : null;

        // A string value, or, for one holding an unpaired surrogate escape, a string
        // that is unsafe to show. JsonElement.GetString throws on such a value, and a
        // row built to make it throw would otherwise take the whole poll down: this
        // keeps "a row can cost only itself" true. The stand-in contains a lone
        // surrogate, so every check that refuses unsafe text refuses it.
        private static string SafeString(JsonElement value)
        {
            try
            {
                return value.GetString()!;
            }
            catch (InvalidOperationException)
            {
                return ((char)0xD800).ToString();
            }
        }
    }
}
