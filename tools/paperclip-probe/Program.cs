using System.Text.Json;
using Orbweaver;

// Asks a Paperclip what it says, using the app's own parsing and transport.
//
//   dotnet run --project tools/paperclip-probe -- discover
//   dotnet run --project tools/paperclip-probe -- check
//   dotnet run --project tools/paperclip-probe -- agents
//   dotnet run --project tools/paperclip-probe -- needs
//   dotnet run --project tools/paperclip-probe -- runs [--watch [seconds]]
//
// Read-only: every call it makes is a GET (the transport it shares with the app
// can POST, and nothing here calls that), so it cannot wake an agent, decide an
// approval, pause anything or create a key. The board key
// comes from PAPERCLIP_BOARD_KEY and is never printed. PAPERCLIP_URL
// (default: the first instance `discover` finds, else http://127.0.0.1:3100)
// and PAPERCLIP_COMPANY_ID (default: the only company) are optional.
//
// It never prints an agent's adapterConfig (local paths) or a run's `error`
// text (measured rows carry commands): it prints what PaperclipApi parses,
// and PaperclipApi does not read either.

var command = args.Length > 0 ? args[0] : "check";
var key = Environment.GetEnvironmentVariable("PAPERCLIP_BOARD_KEY");
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

var baseUrl = Environment.GetEnvironmentVariable("PAPERCLIP_URL");
if (string.IsNullOrWhiteSpace(baseUrl))
{
    var cands = PaperclipDiscovery.Candidates(
        root => Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(),
        f => File.Exists(f) ? File.ReadAllText(f) : null,
        PaperclipDiscovery.InstancesRoot(home));
    var found = await PaperclipDiscovery.FindAsync(cands, u => new PaperclipHttp(u, null), default);
    if (command == "discover")
    {
        if (found.Count == 0) Console.WriteLine("No Paperclip answered /api/health on: " + string.Join(", ", cands.Select(c => c.BaseUrl)));
        foreach (var f in found) Console.WriteLine($"{f.InstanceName}: {f.BaseUrl} (deploymentMode {f.DeploymentMode})");
        return found.Count == 0 ? 1 : 0;
    }
    baseUrl = found.Count > 0 ? found[0].BaseUrl : PaperclipDiscovery.DefaultAddress;
}
else if (command == "discover")
{
    Console.WriteLine("PAPERCLIP_URL is set; discovery skipped: " + baseUrl);
    return 0;
}

var api = new PaperclipHttp(baseUrl!, key);
Console.WriteLine($"Paperclip at {baseUrl} (key {(string.IsNullOrEmpty(key) ? "not set" : "set, " + key.Length + " characters")})");

if (command == "check") return await Check();

if (string.IsNullOrEmpty(key))
{
    Console.Error.WriteLine("Set PAPERCLIP_BOARD_KEY (a board key; the probe never creates one).");
    return 1;
}

var companies = await ListCompanies(api);
var companyId = Environment.GetEnvironmentVariable("PAPERCLIP_COMPANY_ID");
var company = companies.FirstOrDefault(c => c.Id == companyId) ?? (companies.Count == 1 ? companies[0] : null);
if (company == null)
{
    Console.Error.WriteLine(companies.Count == 0 ? "No companies came back (is the key valid?)." : "Several companies; set PAPERCLIP_COMPANY_ID.");
    foreach (var c in companies) Console.Error.WriteLine($"  {c.Id}  {c.Name}");
    return 1;
}
Console.WriteLine($"Company {company.Name} ({company.IssuePrefix})");

return command switch
{
    "agents" => await Agents(),
    "needs" => await Needs(),
    "runs" => await Runs(),
    _ => Usage(),
};

static async Task<IReadOnlyList<PaperclipApi.Company>> ListCompanies(IPaperclipApi a) =>
    PaperclipApi.ParseCompanies((await a.GetAsync("/api/companies", default)).Body);

static int Usage()
{
    Console.Error.WriteLine("Commands: discover, check, agents, needs, runs [--watch [seconds]]");
    return 2;
}

// Each read the app makes, with its status, size and time, and the paired
// negative: the same call with no credential must be refused, or a pass above
// it says nothing about the key.
async Task<int> Check()
{
    var health = await api.GetAsync("/api/health", default);
    var h = PaperclipApi.ParseHealth(health.Body);
    Console.WriteLine($"health: {health.StatusCode} status={h?.Status} deploymentMode={h?.DeploymentMode}");
    if (string.IsNullOrEmpty(key)) { Console.WriteLine("No key: stopping after health."); return h?.Status == "ok" ? 0 : 1; }

    var me = await api.GetAsync("/api/cli-auth/me", default);
    Console.WriteLine($"cli-auth/me: {me.StatusCode}");
    var cs = await ListCompanies(api);
    Console.WriteLine($"companies: {cs.Count}");
    var id = Environment.GetEnvironmentVariable("PAPERCLIP_COMPANY_ID") ?? cs.FirstOrDefault()?.Id;
    if (id == null) return 1;
    var anon = new PaperclipHttp(baseUrl!, null);
    foreach (var tail in new[] { "agents", "live-runs", "approvals?status=pending", "tools/action-requests", "issues?status=blocked", "heartbeat-runs?limit=20" })
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await api.GetAsync($"/api/companies/{id}/{tail}", default);
        var n = await anon.GetAsync($"/api/companies/{id}/{tail}", default);
        Console.WriteLine($"  {tail,-28} {r.StatusCode} {r.Body.Length,7} bytes {sw.ElapsedMilliseconds,4} ms   (no credential: {n.StatusCode})");
    }
    return 0;
}

async Task<PaperclipSessions.PollState> OneStep(PaperclipSessions.PollState st)
{
    var cfg = new PaperclipSessions.PaperclipConfig(true, baseUrl!, company!.Id, key!, null, "fold");
    var step = await PaperclipSessions.StepAsync(api, cfg, st, DateTime.UtcNow, default);
    return step.Next;
}

async Task<int> Agents()
{
    var st = await OneStep(PaperclipSessions.PollState.Initial);
    Console.WriteLine($"link: {st.Link}  {st.Status}");
    foreach (var a in st.Published ?? Array.Empty<PaperclipAgent>())
    {
        Console.WriteLine($"{a.Name,-20} {a.Role,-10} status={a.Status,-10} state={a.State,-10} mark={a.Mark}"
            + (a.MarkReason == null ? "" : $" ({a.MarkReason})")
            + (a.BudgetPercent == null ? "" : $" budget={a.BudgetPercent}%")
            + (a.LastResult == null ? "" : $" last={a.LastResult}")
            + (a.Task == null ? "" : $" task=\"{a.Task}\"")
            + (a.Activity == null ? "" : $" activity=\"{a.Activity}\"")
            + $" reportsTo={(a.ReportsTo == null ? "-" : a.ReportsTo[..8])} adapter={a.AdapterType}");
        foreach (var n in a.Needs) Console.WriteLine($"    needs you: [{n.Kind}] {n.Title} -> {n.Url}");
    }
    return 0;
}

async Task<int> Needs()
{
    var id = company!.Id;
    var ap = PaperclipApi.ParseApprovals((await api.GetAsync($"/api/companies/{id}/approvals?status=pending", default)).Body);
    var ac = PaperclipApi.ParseActionRequests((await api.GetAsync($"/api/companies/{id}/tools/action-requests", default)).Body);
    var bl = PaperclipApi.ParseIssues((await api.GetAsync($"/api/companies/{id}/issues?status=blocked", default)).Body);
    Console.WriteLine($"pending approvals: {ap.Count}");
    foreach (var a in ap) Console.WriteLine($"  {a.Id[..8]} {a.Type} \"{a.Title}\" agent={a.PayloadAgentId ?? a.RequestedByAgentId ?? "-"}");
    Console.WriteLine($"pending tool action requests: {ac.Count}");
    foreach (var a in ac) Console.WriteLine($"  {a.Id[..Math.Min(8, a.Id.Length)]} agent={a.AgentId ?? "-"} \"{a.Summary}\"");
    Console.WriteLine($"blocked issues: {bl.Count}");
    foreach (var i in bl) Console.WriteLine($"  {i.Identifier} \"{i.Title}\" assignee={i.AssigneeAgentId ?? "-"} blockerAttention={i.BlockerState ?? "(none)"}");
    return 0;
}

async Task<int> Runs()
{
    var watch = Array.IndexOf(args, "--watch");
    if (watch < 0)
    {
        var rs = PaperclipApi.ParseRuns((await api.GetAsync($"/api/companies/{company!.Id}/heartbeat-runs?limit=20", default)).Body);
        foreach (var r in rs)
            Console.WriteLine($"{r.StartedAt:u} {r.Status,-10} {(r.Redacted ? "redacted" : "full    ")} code={r.ErrorCode ?? "-"} pid={r.ProcessPid?.ToString() ?? "-"}");
        return 0;
    }

    // Poll fast and print each change: the way to size an observation window
    // (about three times the event's own cadence) rather than guess one.
    var seconds = args.Length > watch + 1 && int.TryParse(args[watch + 1], out var s) ? s : 60;
    var until = DateTime.UtcNow.AddSeconds(seconds);
    var last = new Dictionary<string, string>();
    var st = PaperclipSessions.PollState.Initial;
    while (DateTime.UtcNow < until)
    {
        st = await OneStep(st);
        foreach (var a in st.Published ?? Array.Empty<PaperclipAgent>())
        {
            var sig = $"{a.State}/{a.Mark}/{a.Needs.Count}/{a.Activity}";
            if (!last.TryGetValue(a.Id, out var prev) || prev != sig)
            {
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {a.Name}: {sig}");
                last[a.Id] = sig;
            }
        }
        await Task.Delay(500);
    }
    return 0;
}
