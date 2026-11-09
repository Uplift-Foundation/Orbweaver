using System.Diagnostics;
using Orbweaver;

// CB-316: the shipped desktop relay code (RelayClient, PhoneService, PhoneLaunch and the
// real transcript parsers) as a desktop a real phone can pair with, over a real relay,
// with no terminal and no installed app involved.
//
//   dotnet run --project tools/phone-desk -- --relay http://127.0.0.1:18080 \
//       --qr-host <host:port the phone reaches the relay on> --transcript <a Claude Code .jsonl> [--dir <state dir>] [--name "PHONE DESK"]
//
// Prints the pairing text; allows the first request; keeps a roster of three sessions (one
// working, one waiting on a dialog, one idle), a usage reading that climbs, and a done push
// every time the working one goes idle. `input` is "typed" by appending a user line (and a
// short reply a moment later) to a scratch COPY of the transcript, so the live tail shows
// it; `launch` is verified for real (signature, expiry, replay, folder, model) and starts a
// pretend session that checks in two seconds later. Nothing here types into a terminal,
// and the transcript given is never written to (the copy is).

// --tmux-check: the real tmux arm on this machine. A fake "claude" that draws an idle prompt
// is launched through PhoneSeams.Start (a real detached tmux session), typed into through the
// real guarded route (TypingGate, settle read, tmux paste), and a second one that draws a
// permission dialog must be refused. Proves the Mac/Linux terminal code, not Windows' conhost.
// Runs on a PRIVATE tmux server (TMUX_TMPDIR in a scratch directory) and only ever kills the
// one session it made, so it can never touch the user's own tmux.
if (args.Contains("--tmux-check"))
{
    var work = Path.Combine(Path.GetTempPath(), "phone-desk-tmux-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(work);
    Environment.SetEnvironmentVariable("TMUX_TMPDIR", work);
    Environment.SetEnvironmentVariable("TMUX", null);
    var typedLog = Path.Combine(work, "typed.log");
    string Fake(string name, string screen)
    {
        var path = Path.Combine(work, name);
        File.WriteAllText(path, "#!/bin/sh\nprintf '\\033[2J\\033[H'\ncat <<'SCREEN'\n" + screen + "\nSCREEN\ncat -v >> '" + typedLog + "'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    var rule = new string('\u2500', 60);
    var idle = Fake("claude-idle.sh", rule + "\n\u276f\u00a0\n" + rule);
    var dialog = Fake("claude-dialog.sh", " Bash command\n\n   touch /tmp/x\n\n Do you want to proceed?\n \u276f 1. Yes\n   2. Yes, and don't ask again\n   3. No, and tell Claude what to do differently (esc)\n");

    var checks = new PhoneSeams();
    var ok = true;
    void Say(bool pass, string what) { ok &= pass; Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {what}"); }
    var tmux = TerminalLauncher.ResolveTmux() ?? throw new InvalidOperationException("no tmux");
    string Tmux(params string[] a)
    {
        var info = new ProcessStartInfo(tmux) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in a) info.ArgumentList.Add(x);
        using var p = Process.Start(info)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }
    foreach (var (label, script, expectTyped) in new[] { ("idle", idle, true), ("dialog", dialog, false) })
    {
        var plan = new LaunchPlan(NewChatCli.ClaudeCode, work, "", null, "");
        var (code, why) = checks.Start(plan, script);
        Say(code is null, $"{label}: Start launches a detached tmux session ({code} {why})");
        await Task.Delay(1500);
        var pane = Tmux("list-panes", "-a", "-F", "#{pane_id} #{session_name}").Split('\n').FirstOrDefault(l => l.EndsWith(" orbweaver"))?.Split(' ')[0];
        Say(pane is not null, $"{label}: the pane exists ({pane})");
        var status = new SessionStatus { State = "idle", Cwd = work, TmuxPane = pane ?? "", TmuxBin = tmux, SessionPid = 0, Source = SessionSource.ClaudeCode };
        var session = new PhoneService.Session("fake-" + label, status, "idle");
        Say(PhoneSeams.CanType(status), $"{label}: the route can be typed into");
        var (didType, refusal) = await PhoneSeams.TypeAsync(session, "hello from the tmux check");
        await Task.Delay(500);
        var log = File.Exists(typedLog) ? File.ReadAllText(typedLog) : "";
        if (expectTyped) Say(didType && refusal is null && log.Contains("hello from the tmux check"), $"{label}: typed={didType} refusal={refusal}; the pane received it ({log.Trim().Replace("\n", "|")})");
        else Say(!didType && refusal is not null, $"{label}: refused ({refusal})");
        Tmux("kill-session", "-t", "orbweaver");
    }
    Tmux("kill-server");   // the private server only: TMUX_TMPDIR above points it at the scratch directory
    Directory.Delete(work, true);
    return ok ? 0 : 1;
}

string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }

var relay = new Uri(Arg("--relay", "http://127.0.0.1:18080"));
var qrHost = Arg("--qr-host", "");
var source = Arg("--transcript", "");
var dir = Arg("--dir", Path.Combine(Path.GetTempPath(), "phone-desk"));
var name = Arg("--name", "PHONE DESK");
if (source.Length == 0 || !File.Exists(source)) { Console.Error.WriteLine("--transcript <file.jsonl> is required"); return 2; }
Directory.CreateDirectory(dir);
Environment.SetEnvironmentVariable(BrandEnv.Name(BrandEnv.SettingsDir), Path.Combine(dir, "settings"));

var scratch = Path.Combine(dir, "transcript-copy.jsonl");
File.Copy(source, scratch, overwrite: true);

var identity = new RelayIdentity(Path.Combine(dir, "identity"), PlainSecretStore.Instance);
var seams = new PhoneSeams();
var book = new List<(string Id, SessionStatus Status)>();
SessionStatus Status(string state, string title, string cwd, string? transcript = null) => new()
{
    State = state, Title = title, Cwd = cwd, Source = SessionSource.ClaudeCode, TranscriptPath = transcript ?? "",
};
book.Add(("sim-main", Status("generating", "real transcript", "/home/me/project", scratch)));
book.Add(("sim-wait", Status("waiting", "waiting session", "/home/me/other")));
book.Add(("sim-idle", Status("idle", "idle session", "/home/me/idle")));

void Publish(RelayClient c)
{
    seams.OnScan(book);
    c.OnScanned(RelayRoster.Project(book));
    _ = c.OnStatuses(book);
}

var typed = new List<string>();
Task<(bool, string?)> Type(PhoneService.Session session, string text)
{
    if (session.Id is not ("sim-main" or "sim-new")) return Task.FromResult<(bool, string?)>((false, "it is showing a dialog: Bash command"));
    lock (typed) typed.Add(text);
    // Claude Code writes a user's text as a string and an assistant's as blocks.
    string Line(string role, string body) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = role, ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"), ["uuid"] = Guid.NewGuid().ToString(),
        ["message"] = new Dictionary<string, object>
        {
            ["role"] = role,
            ["content"] = role == "user" ? body : new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = body } },
        },
    });
    File.AppendAllText(scratch, Line("user", text) + "\n");
    _ = Task.Run(async () =>
    {
        await Task.Delay(1500);
        File.AppendAllText(scratch, Line("assistant", "Typed through the shipped desktop code: " + text) + "\n");
    });
    return Task.FromResult<(bool, string?)>((true, null));
}

var phoneSeams = new PhoneService.Seams(
    seams.Find, seams.Load, s => s.Source == SessionSource.ClaudeCode, Type,
    (phone, cls, ck, plain) => false, line => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} log {line}"));

RelayClient? client = null;
phoneSeams = phoneSeams with
{
    Send = (phone, cls, ck, plain) =>
    {
        var sent = client!.SendForTool(phone, cls, ck, plain);
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} -> {cls} {Brief(plain)}{(sent ? "" : " (not sent)")}");
        return sent;
    },
};

client = new RelayClient(identity, new RelayClient.Options(relay, "win", "phone-desk", name), new WebSocketRelayTransport(), phoneSeams: phoneSeams);
client.Logged += note => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} note {note}");

// launch, verified for real; the terminal is pretend.
client.Phone.Launch = new PhoneLaunch(new PhoneLaunch.Seams(
    () => DateTimeOffset.UtcNow, d => Task.Delay(d), Directory.Exists,
    cli => cli == NewChatCli.ClaudeCode ? "/bin/claude" : null,
    seams.Find,
    (plan, binary) =>
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} LAUNCH {LaunchCommand.CliName(plan.Cli)} in {plan.Cwd} model='{plan.Model}' parent='{plan.ParentSessionId}' message={plan.FirstMessage.Length} chars");
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            lock (book) book.Add(("sim-new", Status("idle", "launched " + Path.GetFileName(plan.Cwd.TrimEnd('/')), plan.Cwd)));
            Publish(client!);
        });
        return (null, null);
    },
    (cli, cwd, _) => seams.Find("sim-new"),
    Type, (phone, cls, ck, plain) => client!.SendForTool(phone, cls, ck, plain), () => false,
    line => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} log {line}")), client.Prompts.Nonces);

client.Start();
while (client.State != RelayClient.Status.Connected) await Task.Delay(100);
Console.WriteLine($"desk {identity.DeviceId} connected to {relay}");

var phoneIds = identity.Phones.Select(p => p.PhoneId).ToList();
if (phoneIds.Count == 0)
{
    var offer = await client.BeginPairingAsync();
    var qr = offer.QrText;
    if (qrHost.Length > 0) qr = qr.Replace(relay.IsDefaultPort ? relay.Host : $"{relay.Host}:{relay.Port}", qrHost);
    File.WriteAllText(Path.Combine(dir, "pair-link.txt"), qr);
    Console.WriteLine($"QR {qr}");
    var asked = new TaskCompletionSource<RelayLink.PairRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PairRequested += r => asked.TrySetResult(r);
    var request = await asked.Task;
    Console.WriteLine($"request from {request.PhoneId} \"{request.Name}\" check code {request.CheckCodeDisplay}: {(client.Allow(request) ? "allowed" : "allow not sent")}");
}
else Console.WriteLine($"already paired with {string.Join(", ", phoneIds)}");

Publish(client);

// A working session that finishes now and then (a done push), and usage that climbs through 80%.
_ = Task.Run(async () =>
{
    var pct = 52.0;
    var windowEnd = DateTimeOffset.UtcNow.AddHours(3);
    var round = 0;
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        pct = pct >= 96 ? 52 : pct + 3;
        var now = DateTimeOffset.UtcNow;
        client.OnPolledForTool([
            new AccountUsage(null, "desk@example.org", true, "Max", new UsageWindow(pct, windowEnd), new UsageWindow(31, now.AddDays(3)), null, now),
            new AccountUsage(null, "codex (default)", true, null, new UsageWindow(17, windowEnd), null, null, now, AccountUsageSource.Codex),
        ]);
        if (++round % 4 == 0)
        {
            lock (book) book[0] = (book[0].Id, book[0].Status.State == "generating" ? Status("idle", "real transcript", "/home/me/project", scratch) : Status("generating", "real transcript", "/home/me/project", scratch));
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} sim-main -> {book[0].Status.State}");
            Publish(client);
        }
    }
});

Console.WriteLine("running; Ctrl-C to stop");
await Task.Delay(Timeout.Infinite);
return 0;

static string Brief(string plain) => plain.Length <= 140 ? plain : plain[..140] + $"… ({plain.Length} chars)";
