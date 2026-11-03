using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbweaver;

// CB-199's gate: can the Claude Code CLI's own token *write* into a cloud
// session from outside one?
//
// ## Why this is a different host and prefix from everything else here
//
// Program.cs reads `/v2/ccr-sessions`, and CB-164's findings record every write
// path under that prefix as 404. That was right about the prefix and says
// nothing about writing: read out of the CLI 2.1.284 binary, the CLI sends
// user turns and interrupts to `POST /v1/code/sessions/{id}/events`, and
// `/v2/ccr-sessions` turns out to be in-container ingress authenticated by a
// session JWT. So the write question has to be asked at `/v1/code`, and a
// working read on `/v2` is not evidence about it — CB-164 already measured this
// same token being *refused* on another `/v1` route
// (`/v1/organizations/me` → 403 "Authentication method not allowed").
//
// ## Why it now builds its requests through the app
//
// Program.cs refuses to reimplement the shipped request builder, and rightly:
// a probe that asks differently from the app can answer differently and be
// believed. This file first had to break that rule, because the shipped
// builder was GET-only and knew no `/v1/code` path, and what it measured is
// what decided the shape the builder was given. Now it has one, so every
// request here goes through `CloudRequest.Build`, every body through
// `ClaudeCloudSend`, and every id through `ClaudeCloudRoster.IsWellFormedId` —
// one copy of the header set, the bodies and the id rule, shared with the app.
//
// The ablation flags survive on top of that: `--beta`, `--org` and
// `--platform` add a header to the shipped build, and `--no-version` removes
// one. The output lists the headers read back off the built request, so what
// a run says it sent is what it sent.
//
// ## What it never prints, and what it never touches
//
// No token, ever — same rule as Program.cs. Response bodies are printed as
// shape plus a short allow-list of enum-valued fields (`worker_status`,
// `status`, `connection_status`, `type`, `message` on an error) whose values
// name a state rather than a person. Session titles and message text are never
// printed.
//
// `send` and `interrupt` write into a live session. They refuse to run without
// `--throwaway`, which is the caller asserting the session was created for this
// probe and holds nothing anybody wants. That is a speed bump, not a proof — it
// exists so that the command line of every write says what it was aimed at.
internal static class WriteProbe
{
    private static readonly HashSet<string> SafeScalars = new(StringComparer.Ordinal)
    {
        "worker_status", "status", "connection_status", "session_status", "status_bucket",
        "environment_kind", "type", "subtype", "duplicate", "sequence_num", "has_more",
        "message", "error", "code", "outcome", "cross_session_inbound",
        "event_type", "device_attestation_status", "session_agent_owned", "source",
    };

    internal static void Usage() =>
        Console.Error.WriteLine(
            "\n" +
            "CB-199 write gate (aim these only at a session created for the probe):\n" +
            "  v1-session <id>  [auth]            GET  /v1/code/sessions/<id>, shape + status fields\n" +
            "  v1-events  <id>  [auth]            GET  /v1/code/sessions/<id>/events, shape only\n" +
            "  v2-session <id>  [auth]            GET  /v2/ccr-sessions/<id>, shape + status fields (CB-225)\n" +
            "  v2-events  <id>                    GET  /v2/ccr-sessions/<id>/events via the app's own parser\n" +
            "  send <id> --throwaway [opts]       POST a user turn to .../events\n" +
            "  interrupt <id> --throwaway [opts]  POST a control_request interrupt to .../events\n" +
            "  control <id> --throwaway --request <json> [opts]  POST any control_request (CB-306)\n" +
            "  archive <id> --throwaway [opts]    POST {} to /v1/code/sessions/<id>/archive (CB-225)\n" +
            "  delete <id> --throwaway [opts]     DELETE /v1/code/sessions/<id> (CB-225)\n" +
            "  route <id> --throwaway [opts]      POST to a sub-route that does not exist (control)\n" +
            "         --save <path>            write the raw response body to <path>, 0600, never printed\n" +
            "         --absent-id              allow a lifecycle verb only on an id that GETs 404\n" +
            "\n" +
            "  auth:  --auth real|none|bogus   (default real; none/bogus are the negative controls)\n" +
            "  opts:  --text <s>               message text (default: a fixed probe sentence)\n" +
            "         --uuid <u>               reuse an event uuid (dedupe check)\n" +
            "         --expect-uuid <u>        report whether <u> comes back, and where (never the value)\n" +
            "         --empty                  POST {\"events\":[]} and write nothing\n" +
            "         --beta                   add anthropic-beta: ccr-byoc-2025-07-29\n" +
            "         --org                    add x-organization-uuid from ~/.claude.json\n" +
            "         --platform <v>           add anthropic-client-platform: <v>\n" +
            "         --no-version             omit anthropic-version (ablation)\n");

    internal static async Task<int> RunAsync(string verb, string[] args,
        Func<Task<CredentialRead>> readCredential, Func<string?> orgUuid)
    {
        // The app's id rule, so a typo in a probe run is refused locally
        // instead of becoming a request.
        if (args.Length == 0 || !ClaudeCloudRoster.IsWellFormedId(args[0]))
        {
            Console.Error.WriteLine("first argument must be a session id matching ^session_[A-Za-z0-9_-]+$");
            return 2;
        }

        var id = args[0];
        var opts = args.Skip(1).ToArray();
        // CB-225 adds the lifecycle verbs. `archive`, `delete` and `route` are
        // writes for the guard's purposes even though `route` aims at a path
        // nobody claims exists: a probe that guessed wrong about what a route
        // does must not be able to guess wrong about a real session.
        var isWrite = verb is "send" or "interrupt" or "control" or "archive" or "delete" or "route";

        if (isWrite && !opts.Contains("--throwaway"))
        {
            Console.Error.WriteLine(
                $"{verb} writes into a live session and needs --throwaway: the session must have\n" +
                "been created for this probe. There is no way to run it without saying so.");
            return 2;
        }

        var auth = Value(opts, "--auth") ?? "real";
        string? token = null;
        if (auth == "real")
        {
            var read = await readCredential();
            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is null)
            {
                Console.Error.WriteLine($"no usable credential: {ClaudeCliCredentials.Describe(read.Outcome)}");
                return 1;
            }
            token = read.AccessToken;
        }
        else if (auth == "bogus")
        {
            // Well-formed, shaped like the CLI's scheme, and certainly not valid.
            token = "sk-ant-oat01-PROBE-PLACEHOLDER-NOT-A-TOKEN";
        }
        else if (auth != "none")
        {
            Console.Error.WriteLine("--auth must be real, none or bogus");
            return 2;
        }

        if (verb == "v2-events")
        {
            return await V2EventsAsync(id, token, Value(opts, "--expect-uuid"));
        }

        // `--throwaway` is only the caller's word. Back it with the roster's:
        // refuse a write unless /v2 says this id is a genuine cloud session.
        // A `bridge` row is the user's own local session registered for remote
        // control, and nothing in this probe may type into one of those.
        if (isWrite)
        {
            if (token is null || auth != "real")
            {
                // Negative controls carry no usable token, so there is nothing
                // to check the kind with — and nothing they send can land.
            }
            else if (opts.Contains("--absent-id"))
            {
                // The negative control for a lifecycle route: the same call,
                // real token, aimed at an id that does not exist. Allowed only
                // once a GET on that id has just answered 404, so the write
                // provably has nothing to land on. What it measures is whether
                // the *route* exists — "Session ... not found" from the
                // handler against a router-level 404 from `route`.
                var (status, _) = await GetSessionAsync(id, token);
                Console.WriteLine($"guard    GET on the id answered {status}");
                if (status != 404)
                {
                    Console.Error.WriteLine("refusing: --absent-id needs an id the API says does not exist.");
                    return 2;
                }
            }
            else
            {
                // `--bridge` (CB-266) widens this to a `bridge` row, and only
                // that: a Remote Control session the caller started for the
                // probe, e.g. `claude --remote-control` in a scratch directory.
                // Still never a user's real session — that remains the caller's
                // word under --throwaway, backed now by the kind check alone.
                var kind = await KindOfAsync(id, token);
                Console.WriteLine($"guard    environment_kind {kind ?? "(unreadable)"}");
                var allowed = opts.Contains("--bridge") ? "bridge" : "anthropic_cloud";
                if (kind != allowed)
                {
                    Console.Error.WriteLine($"refusing to write: not an {allowed} session.");
                    return 2;
                }
            }
        }

        // Well formed, checked above, so neither of these is null.
        var events = CloudRequest.CodeEventsPath(id)!;
        HttpMethod method;
        string path;
        string? body = null;

        switch (verb)
        {
            case "v1-session":
                method = HttpMethod.Get;
                path = $"{CloudRequest.CodeSessionsPath}/{Uri.EscapeDataString(id)}";
                break;
            case "v2-session":
                // CB-225: the /v2 row itself, which is what the roster's Keep
                // filter reads `session_status` off.
                method = HttpMethod.Get;
                path = CloudRequest.SessionPath(id);
                break;
            case "v1-events":
                method = HttpMethod.Get;
                path = events + "?limit=20&sort_order=desc";
                break;
            case "send":
                method = HttpMethod.Post;
                path = events;
                body = opts.Contains("--empty")
                    ? "{\"events\":[]}"
                    : ClaudeCloudSend.UserMessageBody(opts.Contains("--blank-session-id") ? "" : id,
                        Value(opts, "--text") ?? "CB-199 probe: please reply with the single word ok.",
                        Value(opts, "--uuid") ?? Guid.NewGuid().ToString());
                break;
            case "interrupt":
                method = HttpMethod.Post;
                path = events;
                body = ClaudeCloudSend.InterruptBody(Guid.NewGuid().ToString(),
                    Value(opts, "--uuid") ?? Guid.NewGuid().ToString());
                break;
            case "control":
            {
                // CB-306: any control_request, its `request` object given verbatim
                // as JSON (`--request '{"subtype":"set_color","color":"pink"}'`), so
                // the subtypes the binary's bridge handler lists can be measured
                // one at a time without the probe knowing their fields.
                var raw = Value(opts, "--request");
                JsonObject? controlRequest = null;
                try { controlRequest = raw is null ? null : JsonNode.Parse(raw) as JsonObject; }
                catch (JsonException) { }
                if (controlRequest is null || controlRequest["subtype"] is null)
                {
                    Console.Error.WriteLine("control needs --request '<json object with a subtype>'");
                    return 2;
                }
                method = HttpMethod.Post;
                path = events;
                body = ClaudeCloudSend.ControlRequestBody(Guid.NewGuid().ToString(),
                    Value(opts, "--uuid") ?? Guid.NewGuid().ToString(), controlRequest);
                break;
            }
            case "archive":
                // Read out of the CLI 2.1.288 binary: `tR` posts `{}` here with
                // the same header set as a send, and counts 200 *and* 409 as
                // archived. Measured below, not taken from the binary.
                method = HttpMethod.Post;
                path = $"{CloudRequest.CodeSessionsPath}/{Uri.EscapeDataString(id)}/archive";
                body = "{}";
                break;
            case "delete":
                // Nothing in the CLI binary sends this. It is the obvious REST
                // shape, asked so that "no delete exists" is a measurement.
                method = HttpMethod.Delete;
                path = $"{CloudRequest.CodeSessionsPath}/{Uri.EscapeDataString(id)}";
                break;
            case "route":
                // A sub-route that certainly does not exist: the router's own
                // 404, to tell apart from a handler's "Session not found".
                method = HttpMethod.Post;
                path = $"{CloudRequest.CodeSessionsPath}/{Uri.EscapeDataString(id)}/cb225-no-such-route";
                body = "{}";
                break;
            default:
                return 2;
        }

        // The shipped build, then the ablation on top of it. A negative control
        // with no token builds with an empty one and has the Authorization
        // header taken off again, so it differs from a real request in exactly
        // that header and nothing else.
        using var request = CloudRequest.Build(token ?? "", path, method, body);
        if (token is null) request.Headers.Authorization = null;

        if (opts.Contains("--no-version"))
        {
            request.Headers.Remove(CloudRequest.VersionHeader);
        }
        if (opts.Contains("--beta"))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", "ccr-byoc-2025-07-29");
        }
        if (opts.Contains("--org"))
        {
            var org = orgUuid();
            if (org is null)
            {
                Console.Error.WriteLine("--org given but no organizationUuid found in ~/.claude.json");
                return 1;
            }
            request.Headers.TryAddWithoutValidation("x-organization-uuid", org);
        }
        if (Value(opts, "--platform") is { } platform)
        {
            request.Headers.TryAddWithoutValidation("anthropic-client-platform", platform);
        }

        Console.WriteLine($"request  {method} {CloudRequest.Host}{path}");
        foreach (var h in SentHeaders(request, auth)) Console.WriteLine($"header   {h}");
        if (body is not null) Console.WriteLine($"body     {RedactText(body)}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"transport {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"status   {(int)response.StatusCode}");
            // CB-225: the raw body, to a file only the user can read, for
            // scrubbing into a fixture. Never to the console: the body holds
            // a title and ids, which the allow-list below exists to keep out.
            if (Value(opts, "--save") is { } save)
            {
                // Created 0600, not chmodded after: a write then a chmod
                // leaves the body readable at umask permissions in between.
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                File.Delete(save);
                using (var file = new StreamWriter(save, System.Text.Encoding.UTF8, options))
                    file.Write(text);
                Console.WriteLine($"saved    {text.Length} chars");
            }
            Console.WriteLine($"ctype    {response.Content.Headers.ContentType?.MediaType ?? "(none)"}");
            Console.WriteLine($"cf       {(response.Headers.Contains("cf-mitigated") ? "cf-mitigated present" : "no cf-mitigated")}");
            Console.WriteLine("response:");
            Console.WriteLine(Summarise(text, response.Content.Headers.ContentType?.MediaType));
            if (Value(opts, "--expect-uuid") is { } expect) Console.WriteLine(Echo(text, expect));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
    }

    // The app's history source, read through the shipped client exactly as
    // ClaudeCloudChatSession reads it, so "the sent turn is visible" is
    // measured on the path the panel will actually render from.
    private static async Task<int> V2EventsAsync(string id, string? token, string? expectUuid)
    {
        if (token is null) { Console.Error.WriteLine("v2-events needs --auth real"); return 2; }
        using var api = new HttpCloudApi();
        var result = await api.SendAsync(
            new CloudRequestContext(token, CloudRequest.EventsPath(id, CloudRequest.MaxPageSize, null)),
            CancellationToken.None);
        Console.WriteLine($"request  GET {CloudRequest.Host}{CloudRequest.EventsPath(id, CloudRequest.MaxPageSize, null)}");
        Console.WriteLine($"outcome  {result.Outcome.Kind} {result.Outcome.Status}");
        if (result.Body is null) return 1;

        var page = ClaudeCloudEvents.ParsePage(result.Body);
        Console.WriteLine(page.Parsed
            ? $"turns    {page.Rows.Count} mapped by ChatTranscript (has_more {page.HasMore})"
            : "turns    (page did not parse)");
        if (page.Parsed && expectUuid is not null)
        {
            // Rows is the first page only, oldest-first within it; a fresh
            // session fits in one page, which is why the probe uses one.
            var mapped = page.Rows.Where(r => r.Uuid == expectUuid).ToList();
            Console.WriteLine(mapped.Count == 0
                ? "mapped   no mapped row carries the uuid"
                : $"mapped   {mapped.Count} row(s) carry the uuid, role {mapped[0].Turn.Role}");
        }
        if (expectUuid is not null) Console.WriteLine(Echo(result.Body, expectUuid));
        return 0;
    }

    private static async Task<(int Status, string? Body)> GetSessionAsync(string id, string token)
    {
        using var api = new HttpCloudApi();
        var result = await api.SendAsync(new CloudRequestContext(token, CloudRequest.SessionPath(id)),
            CancellationToken.None);
        return (result.Outcome.Status, result.Body);
    }

    internal static async Task<string?> KindOfAsync(string id, string token)
    {
        using var api = new HttpCloudApi();
        var result = await api.SendAsync(new CloudRequestContext(token, CloudRequest.SessionPath(id)),
            CancellationToken.None);
        if (result.Body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.TryGetProperty("environment_kind", out var k) ? k.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Whether the uuid we sent comes back anywhere in a body, and under which
    // property path. A boolean and a path of property names — never a value —
    // which is enough to decide "reconcile by uuid" against "match by pending".
    private static string Echo(string body, string uuid)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var hits = new List<string>();
            Find(doc.RootElement, "$", uuid, hits);
            return hits.Count == 0
                ? "echo     uuid not found anywhere in the body"
                : "echo     uuid found at " + string.Join(", ", hits.Distinct().Take(5));
        }
        catch (JsonException) { return "echo     (body not JSON)"; }
    }

    private static void Find(JsonElement e, string path, string uuid, List<string> hits)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Find(p.Value, path + "." + p.Name, uuid, hits);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Find(item, path + "[]", uuid, hits);
                break;
            case JsonValueKind.String when e.GetString() == uuid:
                hits.Add(path);
                break;
        }
    }

    // Every header the built request will carry, read back off it rather than
    // listed from the flags, with the token's value never printed — only which
    // kind of token it was (real, bogus). Values that name a person are
    // described rather than shown.
    private static IEnumerable<string> SentHeaders(HttpRequestMessage request, string auth)
    {
        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"Authorization: Bearer <{auth}>";
            }
            else if (string.Equals(header.Key, "x-organization-uuid", StringComparison.OrdinalIgnoreCase))
            {
                yield return "x-organization-uuid: <from ~/.claude.json>";
            }
            else
            {
                yield return $"{header.Key}: {string.Join(",", header.Value)}";
            }
        }

        if (request.Content?.Headers.ContentType is { } type)
        {
            yield return $"Content-Type: {type}";
        }
    }

    private static string? Value(string[] opts, string flag)
    {
        var i = Array.IndexOf(opts, flag);
        return i >= 0 && i + 1 < opts.Length ? opts[i + 1] : null;
    }

    // Print the body we sent with the message text replaced by its length, so a
    // run's log shows the shape of the write without repeating what was written.
    private static string RedactText(string body)
    {
        var node = JsonNode.Parse(body);
        if (node?["events"] is JsonArray events)
        {
            foreach (var e in events)
            {
                if (e?["payload"]?["message"] is JsonObject message &&
                    message["content"] is JsonValue content &&
                    content.TryGetValue<string>(out var s))
                {
                    message["content"] = $"<{s.Length} chars>";
                }
            }
        }
        return node?.ToJsonString() ?? body;
    }

    // Shape of the whole response, plus the values of allow-listed scalars.
    private static string Summarise(string text, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(text)) return "  (empty)";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var builder = new StringBuilder();
            Walk(doc.RootElement, "  ", builder, 0);
            return builder.ToString().TrimEnd();
        }
        catch (JsonException)
        {
            // Length and type only, never the text. A body this probe cannot
            // parse is one nobody has vetted — an edge page, a proxy error, or a
            // server echoing something back — and the allow-list that makes the
            // JSON summary safe to paste has nothing to apply to here.
            return $"  (not JSON) {text.Length} chars, {contentType ?? "no content type"}";
        }
    }

    private static void Walk(JsonElement element, string indent, StringBuilder builder, int depth)
    {
        if (depth > 6) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    builder.Append(indent).Append(p.Name).Append(": ");
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        builder.Append(p.Value.ValueKind == JsonValueKind.Array
                            ? $"array[{p.Value.GetArrayLength()}]" : "object").AppendLine();
                        Walk(p.Value, indent + "  ", builder, depth + 1);
                    }
                    else if (SafeScalars.Contains(p.Name))
                    {
                        builder.Append(p.Value.GetRawText()).AppendLine();
                    }
                    else
                    {
                        builder.Append(p.Value.ValueKind.ToString().ToLowerInvariant()).AppendLine();
                    }
                }
                break;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 0) Walk(element[0], indent, builder, depth + 1);
                break;
        }
    }
}
