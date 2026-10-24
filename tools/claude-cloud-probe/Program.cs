using System.Text;
using System.Text.Json;
using Orbweaver;

// Settles CB-164's last open question from a terminal, with a human present.
//
// ## What the question is
//
// CB-164 settled the load-bearing one — the Claude Code CLI's own stored token
// is accepted at `GET https://api.anthropic.com/v2/ccr-sessions`, returning 200
// with two headers and no cookie. What is left for this tool is everything that
// needs a real credential and a real roster: confirming the store still holds
// what we think it holds on a given machine, and capturing the *shape* of a
// payload without capturing anybody's session titles.
//
// It runs the shipped code — the same credential source the app uses, the same
// request builder, the same status mapping — so its answer is the app's answer
// rather than a second opinion.
//
// **The host matters and is the trap this ticket paid for.** Aimed at claude.ai,
// the identical request is answered by a Cloudflare challenge that looks exactly
// like an auth failure — the same 403 for a real token and a bogus one. If this
// tool ever reports something that reads as "the account is not allowed", check
// the host before checking the account.
//
// ## Why it is a separate binary rather than something the app does
//
// Two reasons, and both are about consent.
//
// On macOS, reading the credential raises a Keychain prompt naming the calling
// binary. Running this is a deliberate act by a person at a keyboard who can see
// that prompt and answer it. The app must not take that step on anybody's behalf
// before the feature is opted into, and nothing built on top of this file may
// ship reading a credential by default.
//
// The prompt this raises names *this binary*, not Orbweaver — it is a
// separate unsigned executable. That is expected and is not the rule the app
// follows being bent: the P/Invoke-rather-than-`security` argument in
// MacOSKeychain is about what the shipped, signed app puts on that screen. A
// grant given to the probe is a grant to the probe, and it does not carry over.
//
// And an agent must not run this. CB-164's own investigation had two attempts to
// reach the credential refused by a safety classifier, and neither was worked
// around; that refusal stands. The code below reads a credential when a *human*
// runs it.
//
// ## What it never prints
//
// No subcommand prints a token. `read` prints its length and first four
// characters at most, which is enough to tell "we read something plausible" from
// "we read an empty string" and not enough to be a credential. `list --shape`
// prints field names and JSON types with every value stripped, so a fixture for
// the roster parser can be designed without a single session title — several of
// which, per this ticket, are personal.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var flags = args.Skip(1).ToArray();

        return args[0] switch
        {
            "stamp" => Stamp(),
            "read" => await ReadAsync(flags),
            "list" => await ListAsync(flags),
            "roster" => await RosterAsync(),
            "lifecycle-run" => await LifecycleRun.RunAsync(flags, ReadFirstFoundAsync),
            "v1-session" or "v1-events" or "v2-session" or "v2-events" or "send" or "interrupt"
                or "archive" or "delete" or "route" =>
                await WriteProbe.RunAsync(args[0], flags, ReadFirstFoundAsync, OrganizationUuid),
            _ => UnknownCommand(args[0]),
        };
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            "usage: claude-cloud-probe <command>\n" +
            "\n" +
            "  stamp              is a credential present, and what is its change stamp\n" +
            "  read --keys-only   which fields were found, and the outcome (no token value)\n" +
            "  list --raw         make the real call; print status and body\n" +
            "  list --shape       make the real call; print field names and types only\n" +
            "  roster             make the real call; print the environment-kind histogram\n" +
            "\n" +
            "On macOS, `read` and `list` raise a Keychain consent prompt naming this\n" +
            "binary. That prompt is the point: answer it yourself.");
        WriteProbe.Usage();
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        Usage();
        return 2;
    }

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // One instance, because it remembers which store answered and the probe
    // reports that afterwards. CLAUDE_CONFIG_DIR is honoured when the probe is run
    // from a shell that has it; the app itself cannot see the CLI's environment.
    //
    // One source per account (config root), because the app now reads every
    // account with a live login. `list`, `roster` and the write verbs use the
    // first account that reads Found; `read` and `stamp` report every account.
    // The write verbs target one throwaway session, so "the first login that
    // works" is the account the write verbs aim with.
    private static readonly IReadOnlyList<CloudAccount> Accounts = ClaudeCliCredentials.SourcesFor(
        OperatingSystem.IsMacOS(), Home, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    // How long this tool waits for the credential store before giving up.
    //
    // **Deliberately much shorter than the app's 45 seconds, and the reason is
    // who is waiting.** ClaudeCliCredentials.UnmeasuredReadBudget is generous
    // because the arm is a background poll that may be waiting on a person
    // reading a consent dialog, and cutting one of those off costs them a round
    // trip through the settings window. Nobody is reading a dialog here: they are
    // watching a cursor blink in a terminal, having run a *diagnostic*, most
    // likely because the credential read is already misbehaving.
    //
    // **A diagnostic that hangs when the thing it diagnoses is broken is the
    // worst possible shape for one** — it turns a legible answer into an
    // indefinite wait at exactly the moment somebody needs the answer. That is
    // what this constant exists to prevent, and it is why the probe does not
    // simply reuse the app's number.
    //
    // Unmeasured, like the app's. Ten seconds is "longer than a working read has
    // ever taken by three orders of magnitude, and shorter than a person's
    // patience".
    private static readonly TimeSpan UnmeasuredProbeReadBudget = TimeSpan.FromSeconds(10);

    // Every secret read in this file goes through here.
    //
    // **There is no unbudgeted path left, and that is the point rather than the
    // tidiness.** The app was fixed first and this file was left calling the
    // source's read method directly at three call sites, so `roster` still hung
    // for a hundred seconds against a Keychain the app itself handled correctly in
    // 45. A diagnostic that hangs on the condition it diagnoses is worse than no
    // diagnostic, because it consumes the attention of whoever is trying to find
    // out what is wrong.
    //
    // CredentialBudgetTests guards it: nothing in the app or this tool may reach a
    // credential source's read except through ClaudeCliCredentials.
    private static async Task<CredentialRead> ReadFirstFoundAsync()
    {
        CredentialRead? first = null;
        foreach (var account in Accounts)
        {
            var read = await ReadCredentialAsync(account);
            if (read.Outcome == CredentialOutcome.Found) return read;
            first ??= read;
        }

        return first!;
    }

    private static async Task<CredentialRead> ReadCredentialAsync(CloudAccount account)
    {
        var read = await ClaudeCliCredentials.ReadWithinAsync(
            account.Source, UnmeasuredProbeReadBudget, CancellationToken.None);

        if (read.Outcome == CredentialOutcome.NoAnswer)
        {
            // The extra sentence the app cannot usefully show on a one-line status
            // row, and which is the whole reason somebody ran this.
            Console.Error.WriteLine(
                $"the credential store did not answer within {UnmeasuredProbeReadBudget.TotalSeconds:0}s.");
            Console.Error.WriteLine(
                "The read may be waiting on a Keychain prompt that cannot be displayed here —");
            Console.Error.WriteLine(
                "this has been measured in contexts with no window server session (no one logged");
            Console.Error.WriteLine(
                "in at the screen, or a background job). Try `stamp`, which asks only for");
            Console.Error.WriteLine(
                "attributes, never prompts, and has kept answering when this call does not.");
        }

        return read;
    }

    // Attributes only on macOS, an mtime elsewhere. Neither returns secret
    // material and neither prompts, so this is the subcommand to run first: it
    // says whether there is anything to read at all before anyone is asked to
    // approve reading it.
    //
    // **Deliberately not routed through ReadCredentialAsync, and deliberately not
    // wrapped in anything at all.** This is the attributes-only query. It has
    // never been observed to hang — including in the contexts where the data read
    // does not return at all — which makes it the one thing that still answers
    // when the other one will not, and therefore the first thing to run when
    // diagnosing this. Putting a budget or a thread hop around it could only make
    // that less true.
    private static int Stamp()
    {
        var any = false;
        foreach (var account in Accounts)
        {
            var stamp = account.Source.Stamp();
            any |= stamp is not null;
            Console.WriteLine(stamp is null
                ? $"account {account.Label} ({account.Root}): no credential found"
                : $"account {account.Label} ({account.Root}): credential present; stamp {stamp}");
        }

        return any ? 0 : 1;
    }

    private static async Task<int> ReadAsync(string[] flags)
    {
        if (!flags.Contains("--keys-only"))
        {
            Console.Error.WriteLine(
                "read requires --keys-only. There is no mode that prints the token; the flag is\n" +
                "mandatory so that is obvious from the command line rather than from the output.");
            return 2;
        }

        var exit = 1;
        foreach (var account in Accounts)
        {
            var read = await ReadCredentialAsync(account);
            var multi = (MultiCredentialSource)account.Source;

            Console.WriteLine($"account  {account.Label} ({account.Root})");
            Console.WriteLine($"outcome  {read.Outcome}");
            // Store names and outcomes only — never a value.
            Console.WriteLine($"answered {multi.AnsweredBy ?? "(none)"}");
            foreach (var (name, outcome, reason) in multi.Attempts)
                Console.WriteLine($"tried    {name} -> {outcome}: {reason}");
            Console.WriteLine($"meaning  {ClaudeCliCredentials.StatusFor(read)}");
            if (read.Detail is { } detail) Console.WriteLine($"detail   {detail}");
            Console.WriteLine($"expires  {read.ExpiresAt?.ToString("u") ?? "(not stated)"}");
            Console.WriteLine(read.AccessToken is null ? "token    none" : "token    present");
            Console.WriteLine();

            if (read.Outcome == CredentialOutcome.Found) exit = 0;
        }

        // Printed as a presence, never as a value, and nothing sends it — see
        // ClaudeCliCredentials.OrganizationUuidFrom. It is here because knowing
        // *whether* the config names an organisation is useful when working out
        // whose credential is in the store.
        var orgUuid = OrganizationUuid();
        Console.WriteLine($"org      {(orgUuid is null ? "(not found in ~/.claude.json)" : "found")}");

        return exit;
    }

    private static string? OrganizationUuid()
    {
        var path = UsageAccounts.AccountFilePath(Home, null);
        try
        {
            return ClaudeCliCredentials.OrganizationUuidFrom(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // **This is the measurement the ticket is waiting on.**
    //
    // A 200 says the CLI's token is accepted at this audience and the feature is
    // buildable. A 401 carrying "OAuth access token is invalid." says it is a
    // structurally valid token for a different audience, and the feature is not
    // buildable on this footing — which is a real answer and closes the ticket as
    // firmly as a 200 opens it.
    private static async Task<int> ListAsync(string[] flags)
    {
        var raw = flags.Contains("--raw");
        var shape = flags.Contains("--shape");
        if (raw == shape)
        {
            Console.Error.WriteLine("list needs exactly one of --raw or --shape.");
            return 2;
        }

        var read = await ReadFirstFoundAsync();
        if (read.Outcome != CredentialOutcome.Found || read.AccessToken is null)
        {
            Console.Error.WriteLine(
                $"no usable credential: {ClaudeCliCredentials.Describe(read.Outcome)}");
            if (read.Detail is { } detail) Console.Error.WriteLine($"  {detail}");
            return 1;
        }

        // **No organisation gate any more.** This used to refuse to make the call
        // at all without an `organizationUuid` out of `~/.claude.json`, because
        // `x-organization-uuid` was believed mandatory. It is claude.ai's header;
        // api.anthropic.com ignores it. A probe that refuses to run for want of a
        // value nothing sends is a diagnostic that reports its own assumption as
        // the machine's problem.
        using var api = new HttpCloudApi();
        var result = await api.SendAsync(
            new CloudRequestContext(read.AccessToken,
                CloudRequest.ListPath(CloudRequest.MaxPageSize, null)),
            CancellationToken.None);

        Console.WriteLine($"outcome  {result.Outcome.Kind}");
        Console.WriteLine($"status   {result.Outcome.Status}");
        if (result.Outcome.Detail is { } why) Console.WriteLine($"detail   {why}");

        if (result.Body is null)
        {
            Console.WriteLine("body     (none)");
            return result.Outcome.Kind == CloudOutcomeKind.Ok ? 0 : 1;
        }

        if (raw)
        {
            Console.WriteLine("body:");
            Console.WriteLine(result.Body);
            return 0;
        }

        Console.WriteLine("shape:");
        Console.WriteLine(Shape(result.Body));
        return 0;
    }

    // The whole roster, reduced to counts.
    //
    // **This is the subcommand that answers "is the filter still right".** It
    // walks every page the way the app does, runs the shipped
    // ClaudeCloudRoster.Reduce over the result and prints the environment-kind
    // histogram plus the status sentence the settings window would show. No
    // titles, no ids, no timestamps — a count per kind is a fact about the
    // account's shape and names nobody.
    //
    // A run showing 573 bridge and 5 anthropic_cloud is the measurement CB-164
    // was built on. A run showing a kind this version does not know is the
    // earliest warning that the filter has stopped matching, and it is much
    // cheaper to read here than to diagnose from a screenshot of missing orbs.
    // Wraps the real API to count what each account's tick asked for, so the
    // probe can report pages and kinds without a second implementation of the
    // walk. Only shapes are recorded — never a body, id or title.
    private sealed class CountingApi : ICloudApi
    {
        private readonly ICloudApi _inner;
        internal int Pages;
        internal int Inspected;
        internal Dictionary<string, int> Kinds { get; } = new(StringComparer.Ordinal);

        internal CountingApi(ICloudApi inner) => _inner = inner;

        public async Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            var result = await _inner.SendAsync(context, token);
            if (result.Outcome.Kind == CloudOutcomeKind.Ok
                && context.Path.StartsWith(CloudRequest.ListPath(CloudRequest.MaxPageSize, null).Split('?')[0],
                    StringComparison.Ordinal)
                && !context.Path.Contains("/session_", StringComparison.Ordinal))
            {
                var page = ClaudeCloudRoster.ParsePage(result.Body);
                Pages++;
                Inspected += page.Inspected;
                foreach (var row in page.Rows)
                    Kinds[row.Kind] = Kinds.TryGetValue(row.Kind, out var n) ? n + 1 : 1;
            }

            return result;
        }
    }

    // **Runs the shipped per-account path, not a re-implementation of it.** Each
    // account goes through ClaudeCloudSessions.StepAsync (the same call the app's
    // poll loop makes), then CloudAccountBoard.Apply folds the results exactly as
    // the app does, and the status text is DescribeAccounts' output — what
    // Settings would show. Output is labels, counts and kinds only: no ids, no
    // titles, no tokens.
    private static async Task<int> RosterAsync()
    {
        using var http = new HttpCloudApi();
        var coordinator = new CloudReadCoordinator();
        var board = new CloudAccountBoard(Accounts, coordinator);
        var exit = 0;

        foreach (var account in Accounts)
        {
            var api = new CountingApi(http);
            var step = await ClaudeCloudSessions.StepAsync(
                api, new CloudAccountSource(account, coordinator), ClaudeCloudSessions.ArmState.Initial,
                DateTime.UtcNow, CancellationToken.None, CredentialBudget.Of(UnmeasuredProbeReadBudget));
            board.Apply(account.Root, step, DateTime.UtcNow);

            Console.WriteLine($"account   {account.Label}");
            Console.WriteLine($"pages     {api.Pages}");
            Console.WriteLine($"inspected {api.Inspected}");
            Console.WriteLine("kinds:");
            foreach (var kind in api.Kinds.OrderByDescending(k => k.Value))
            {
                var known = ClaudeCloudRoster.IsKnownKind(kind.Key) ? "" : "   <- unknown to this version";
                Console.WriteLine($"  {kind.Key,-20} {kind.Value}{known}");
            }

            Console.WriteLine($"orbs      {step.Snapshot?.Count ?? 0}");
            Console.WriteLine($"status    {step.Status}");
            Console.WriteLine();
            if (step.Snapshot is null || step.Next.Halted) exit = 1;
        }

        var merged = board.Merged;
        Console.WriteLine($"merged orbs after dedup  {merged.Count}");
        foreach (var owner in merged.GroupBy(m => Accounts.First(a => a.Root == m.OwnerRoot).Label))
            Console.WriteLine($"  owned by {owner.Key}: {owner.Count()}");
        Console.WriteLine();
        Console.WriteLine("settings status text:");
        Console.WriteLine(board.StatusText);
        return exit;
    }

    // Field names and JSON types, no values anywhere.
    //
    // An array collapses to its first element's shape rather than being printed
    // per row: the roster's rows are homogeneous, and printing fifty of them
    // would say nothing extra while making it likelier that something slipped
    // through. A scalar prints only its type — this is the whole privacy
    // property, so it is the one thing in this file worth reading twice.
    private static string Shape(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            Describe(doc.RootElement, "", builder);
            return builder.ToString().TrimEnd();
        }
        catch (JsonException ex)
        {
            return $"(not JSON: {ex.Message})";
        }
    }

    private static void Describe(JsonElement element, string indent, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    builder.Append(indent).Append(property.Name).Append(": ")
                        .Append(TypeName(property.Value)).AppendLine();
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Describe(property.Value, indent + "  ", builder);
                    }
                }
                break;

            case JsonValueKind.Array:
                var first = element.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined)
                {
                    builder.Append(indent).Append("[0]: ").Append(TypeName(first)).AppendLine();
                    if (first.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Describe(first, indent + "  ", builder);
                    }
                }
                break;
        }
    }

    private static string TypeName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => $"array[{element.GetArrayLength()}]",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Null => "null",
        _ => "?",
    };
}
