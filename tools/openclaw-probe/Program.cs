using System.Text.Json;
using Orbweaver;

// Asks the gateway a question and prints what it says, using the app's own
// transport rather than a second one.
//
// It exists because several attempts at explaining the panel's behaviour were
// reasoning about a payload nobody here had read. Twice that produced a
// plausible fix that was wrong. The gateway is one LAN hop away and already
// paired with this machine; asking it is cheaper than another guess.
//
//   dotnet run --project tools/openclaw-probe -- sessions
//   dotnet run --project tools/openclaw-probe -- history <sessionKey> [limit] [offset]
//   dotnet run --project tools/openclaw-probe -- cron-runs <jobId> [limit] [offset]
//   dotnet run --project tools/openclaw-probe -- raw <method> [jsonParams]
//   dotnet run --project tools/openclaw-probe -- events [seconds] [sessionKey|-] [out.jsonl|-] [messagesKey]
//   dotnet run --project tools/openclaw-probe -- approvals list
//   dotnet run --project tools/openclaw-probe -- approvals watch [seconds] [sessionKey]
//   dotnet run --project tools/openclaw-probe -- approvals resolve <id> <kind> <deny|allow-once>
//
// Read-only by construction: it never calls chat.send, and it requests whatever
// scopes the app's settings already granted rather than asking for more.
//
// CB-270 Stage 6 adds two environment overrides, so a probe can pair with an
// explicit request instead of reading the settings it shares a device identity
// with (pairing the probe with a new scope set re-pairs the app, and the reverse,
// which is why the app is quiet while one is run):
//   ORBWEAVER_PROBE_SCOPES=operator.read,operator.write,operator.approvals
//   ORBWEAVER_PROBE_CAPS=approvals      (or "none" for an explicitly empty list)
// and three `approvals` modes. **None of them has been run against a gateway**:
// they were built while the device could not be paired with operator.approvals, and
// the first run is Stage 6's P1 to P7. `resolve` answers a real approval, so it
// refuses unless ORBWEAVER_PROBE_ALLOW_RESOLVE=1 is set, only sends deny or
// allow-once, and is meant for an approval the probe registered itself on a
// throwaway session, never a real one.

var host = OrbweaverSettings.OpenClawHost;
var port = OrbweaverSettings.OpenClawPort;

if (string.IsNullOrWhiteSpace(host))
{
    Console.Error.WriteLine("No gateway address in settings. Turn OpenClaw on in Orbweaver first.");
    return 1;
}

var command = args.Length > 0 ? args[0] : "sessions";

OpenClawGateway.ScopesOverride = OpenClawGateway.ParseOverride(
    BrandEnv.Get(BrandEnv.ProbeScopes));
OpenClawGateway.CapsOverride = OpenClawGateway.ParseOverride(
    BrandEnv.Get(BrandEnv.ProbeCaps));

// The token the app already stores for this gateway, beside the device key.
var token = OpenClawIdentity.GatewayTokenFor(host);
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine($"No gateway token stored for {host}.");
    return 1;
}

using var gateway = new OpenClawGateway(host, port, token!);

var pinned = OrbweaverSettings.OpenClawFingerprint;
var result = await gateway.ConnectAsync(string.IsNullOrEmpty(pinned) ? null : pinned, CancellationToken.None);

if (result.Outcome != OpenClawGateway.Outcome.Connected)
{
    Console.Error.WriteLine($"Not connected: {result.Outcome} {result.Detail}");
    return 1;
}

// Whole objects, indented. The point is to see the fields nobody documented,
// so nothing here selects or reshapes what came back.
var pretty = new JsonSerializerOptions { WriteIndented = true };

try
{
    switch (command)
    {
        case "sessions":
        {
            var res = await gateway.RequestAsync("sessions.list", new Dictionary<string, object>(), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        case "history":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("history needs a sessionKey");
                return 1;
            }

            var parameters = new Dictionary<string, object>
            {
                ["sessionKey"] = args[1],
                ["limit"] = args.Length > 2 ? int.Parse(args[2]) : 40,
                ["offset"] = args.Length > 3 ? int.Parse(args[3]) : 0
            };

            var res = await gateway.RequestAsync("chat.history", parameters, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        // CB-115: cron.runs, which recovers a cron-delivered picture's real
        // path once OpenClaw's delivery route has stripped it out of the
        // transcript. `raw` already reaches this method, but a named
        // subcommand is what makes a capture reproducible without retyping
        // the jsonParams shape — the same reasoning `history` above exists
        // for. Limit defaults higher than history's, on purpose: an earlier
        // capture of this ticket's real job at limit:5 silently held only 4
        // of its 35 real MEDIA: paths, and 60 is what actually captured all
        // of them in one page (see OpenClawCronRecovery's header).
        case "cron-runs":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("cron-runs needs a jobId");
                return 1;
            }

            var parameters = new Dictionary<string, object>
            {
                ["jobId"] = args[1],
                ["limit"] = args.Length > 2 ? int.Parse(args[2]) : 60,
                ["offset"] = args.Length > 3 ? int.Parse(args[3]) : 0
            };

            var res = await gateway.RequestAsync("cron.runs", parameters, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        // CB-169: what the event stream actually carries during a turn. The
        // orb's running state is decided entirely by which events arrive and
        // in what order, and the only capture of that sequence so far was a
        // cron run — nobody had watched a DM reply. This subscribes exactly as
        // the app does (sessions.subscribe and nothing more), so what it sees
        // is what the app sees, and prints one line per event with the fields
        // the classifier reads. The raw payloads go to the .jsonl, one per
        // line with the arrival time, so a test can replay the real sequence
        // rather than one written from memory. "-" skips the sessionKey
        // filter; the filter matches the key with any ":run:" suffix trimmed.
        // A fifth argument also calls sessions.messages.subscribe for that
        // key, which the app does not do — it exists to answer whether that
        // subscription carries a run's agent/chat stream when
        // sessions.subscribe alone does not.
        case "events":
        {
            var seconds = args.Length > 1 ? int.Parse(args[1]) : 180;
            var filter = args.Length > 2 && args[2] != "-" ? args[2] : null;
            using var raw = args.Length > 3 && args[3] != "-" ? new StreamWriter(args[3]) { AutoFlush = true } : null;
            var started = DateTime.UtcNow;
            var gate = new object();

            gateway.EventReceived += (name, payload) =>
            {
                var at = DateTime.UtcNow;
                var key = Field(payload, "sessionKey");
                var trimmed = key;
                var run = trimmed?.IndexOf(":run:", StringComparison.Ordinal) ?? -1;
                if (run > 0) trimmed = trimmed![..run];
                if (filter is not null && trimmed != filter) return;

                JsonElement data = default, task = default;
                var hasData = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
                var hasTask = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("task", out task) && task.ValueKind == JsonValueKind.Object;

                var line = string.Join(" ", new[]
                {
                    $"+{(at - started).TotalSeconds,7:F2}s",
                    name,
                    $"key={trimmed ?? "-"}",
                    run > 0 ? $"keyRun={key![(run + 5)..]}" : null,
                    Opt("runId", Field(payload, "runId")),
                    Opt("action", Field(payload, "action")),
                    Opt("stream", Field(payload, "stream")),
                    Opt("phase", hasData ? Field(data, "phase") : null),
                    Opt("state", Field(payload, "state")),
                    Opt("task.status", hasTask ? Field(task, "status") : null),
                    Opt("task.runId", hasTask ? Field(task, "runId") : null),
                    Opt("task.sessionKey", hasTask ? Field(task, "sessionKey") : null),
                }.Where(s => s is not null));

                lock (gate)
                {
                    Console.WriteLine(line);
                    raw?.WriteLine(JsonSerializer.Serialize(new { at = at.ToString("O"), name, payload }));
                }
            };

            await gateway.RequestAsync("sessions.subscribe", new Dictionary<string, object>(), CancellationToken.None);
            if (args.Length > 4)
            {
                await gateway.RequestAsync("sessions.messages.subscribe",
                    new Dictionary<string, object> { ["key"] = args[4] }, CancellationToken.None);
                Console.Error.WriteLine($"also subscribed to messages on {args[4]}");
            }
            Console.Error.WriteLine($"listening for {seconds}s{(filter is null ? "" : $" on {filter}")}");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            break;
        }

        // Stage 6. What the gateway is holding for a person's say-so, as raw as
        // every other mode here: the point is to see the fields nobody documented.
        case "approvals":
        {
            var mode = args.Length > 1 ? args[1] : "list";
            Console.Error.WriteLine($"granted scopes: {string.Join(", ", gateway.GrantedScopes)}");

            switch (mode)
            {
                case "list":
                    foreach (var method in new[] { "exec.approval.list", "plugin.approval.list", "openclaw.approval.list" })
                    {
                        Console.WriteLine($"== {method}");
                        try
                        {
                            var res = await gateway.RequestAsync(method, new Dictionary<string, object>(), CancellationToken.None);
                            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    break;

                // Every *.approval.* event, whole. Optionally also subscribes to one
                // session's messages with includeApprovals, to see the replay.
                case "watch":
                {
                    var seconds = args.Length > 2 ? int.Parse(args[2]) : 60;
                    var started = DateTime.UtcNow;

                    gateway.EventReceived += (name, payload) =>
                    {
                        if (!name.Contains(".approval.", StringComparison.Ordinal) && name != "session.approval") return;
                        Console.WriteLine($"+{(DateTime.UtcNow - started).TotalSeconds,7:F2}s {name}");
                        Console.WriteLine(JsonSerializer.Serialize(payload, pretty));
                    };

                    await gateway.RequestAsync("sessions.subscribe", new Dictionary<string, object>(), CancellationToken.None);
                    if (args.Length > 3)
                    {
                        await gateway.RequestAsync("sessions.messages.subscribe",
                            new Dictionary<string, object> { ["key"] = args[3], ["includeApprovals"] = true },
                            CancellationToken.None);
                        Console.Error.WriteLine($"also subscribed to {args[3]} with includeApprovals");
                    }

                    Console.Error.WriteLine($"watching for {seconds}s");
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    break;
                }

                // Answers one. Refused unless explicitly allowed, and only with the
                // two answers the app itself can send.
                case "resolve":
                {
                    if (args.Length < 5)
                    {
                        Console.Error.WriteLine("approvals resolve needs <id> <kind> <deny|allow-once>");
                        return 1;
                    }

                    if (BrandEnv.Get(BrandEnv.ProbeAllowResolve) != "1")
                    {
                        Console.Error.WriteLine(
                            "Refusing: this answers a real approval. Set ORBWEAVER_PROBE_ALLOW_RESOLVE=1 "
                            + "for one the probe registered itself on a throwaway session.");
                        return 1;
                    }

                    if (args[4] is not ("deny" or "allow-once"))
                    {
                        Console.Error.WriteLine("Only deny and allow-once are sent, as in the app.");
                        return 1;
                    }

                    var res = await gateway.RequestAsync("approval.resolve", new Dictionary<string, object>
                    {
                        ["id"] = args[2],
                        ["kind"] = args[3],
                        ["decision"] = args[4]
                    }, CancellationToken.None);
                    Console.WriteLine(JsonSerializer.Serialize(res, pretty));
                    break;
                }

                default:
                    Console.Error.WriteLine($"Unknown approvals mode '{mode}'.");
                    return 1;
            }

            break;
        }

        // For the methods this app has never called. The gateway's own error is
        // more informative than a guess about whether something exists.
        case "raw":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("raw needs a method name");
                return 1;
            }

            var parameters = args.Length > 2
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(args[2]) ?? new()
                : new Dictionary<string, object>();

            var res = await gateway.RequestAsync(args[1], parameters, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    if (ex is OpenClawRequestException rex) Console.Error.WriteLine($"  code={rex.Code} detail={rex.DetailCode}");
    return 1;
}

return 0;

static string? Field(JsonElement e, string name) =>
    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        : null;

static string? Opt(string label, string? value) => value is null ? null : $"{label}={value}";
