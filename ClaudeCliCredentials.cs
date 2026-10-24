using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // How the cloud arm gets a credential, and the rules it handles one under.
    //
    // **This is the first credential this application has ever held.** Every
    // other account-scoped path in the app — UsagePoller above all — deliberately
    // never touches one: it spawns `claude -p` and writes a control request, so
    // the CLI owns the token and a compromise of Orbweaver is not a token
    // disclosure. That posture is not being relaxed. CB-164 established there is
    // no control-protocol subtype for cloud sessions, so the cloud roster cannot
    // be reached that way, and this file is the scoped exception rather than a
    // new general capability.
    //
    // The credential read here is the *Claude Code CLI's own*, which is option A
    // of the three CB-164 weighed. On macOS it is a login-Keychain
    // generic-password item under service "Claude Code-credentials"; elsewhere it
    // is `~/.claude/.credentials.json`. Reading the Keychain item from a
    // different application raises a macOS consent prompt, and that prompt is the
    // user grant — the same supported-OS-mechanism shape as the Automation and
    // Local Network grants this app already depends on, and subject to the same
    // code-signature sensitivity CLAUDE.md documents.
    //
    // Minting our own token under Claude Code's OAuth client_id (option B) was
    // rejected: the consent screen would name Claude Code rather than Claude
    // Buddy, so the one screen whose entire job is to say who is asking would say
    // the wrong thing, and Anthropic can restrict or rotate that client id at any
    // time, after which no version of our code works again.
    //
    // ## Token custody — the rules this file is written to honour
    //
    // * The access token is a **local** everywhere it appears. It is never a
    //   static, never a field, never cached, and never placed in anything that
    //   outlives the request it authorises. CredentialRead is the one record that
    //   carries it, it is constructed at the call site and dropped at the end of
    //   the call, and nothing stores one.
    // * ParseCredentials reads **only** `claudeAiOauth.accessToken` and
    //   `expiresAt`. The stored blob also carries a refresh token. The record
    //   below deliberately has **no field a refresh token could land in**, so a
    //   later well-meaning "also parse the rest" is a compile error rather than a
    //   quiet widening. We do not refresh — the CLI owns refresh, and doing it
    //   ourselves needs the refresh token plus a client id, which is option B by
    //   another road. A stale copy is re-read, not renewed.
    // * Nothing token-derived reaches Detail, Describe, a status string, a log, a
    //   settings file or the disk. Stamp() returns a change-detector — a file
    //   mtime or a Keychain modification date — and never secret bytes.
    // * **The token is not zeroed and cannot be.** A .NET string is immutable and
    //   its bytes live wherever the GC last copied them until the heap is reused.
    //   Claiming otherwise would be the sort of assertion-with-good-grammar
    //   CLAUDE.md warns about, so it is written down here as a known limit
    //   instead. Reducing it would mean SecureString or a byte[] carried through
    //   HttpClient, and neither survives the framework's own string copies.
    internal enum CredentialOutcome
    {
        // A usable, unexpired credential was read.
        Found,

        // Nothing is stored, or what is stored has expired. An ordinary state,
        // not an error: a user who has never run `claude` is here, and so is one
        // whose CLI has not refreshed lately.
        NotLoggedIn,

        // The OS refused us — on macOS, the user answered "Don't Allow" to the
        // Keychain prompt, or the prompt could not be shown. Not retryable
        // without the user changing their mind, so the caller stops rather than
        // asking again and producing a prompt storm.
        Denied,

        // The Keychain could not ask (errSecInteractionNotAllowed): no one is at
        // the screen, or this is a background context. **Not a refusal** — no
        // dialog was shown and nobody said anything, so it must not latch as a
        // decline and it is retried on the normal cadence; the read fails
        // instantly and retrying costs nothing. Split from Denied for exactly that
        // reason: lumping it in told the user "denied" for something nobody did,
        // and would have left every account files-only after they logged back in.
        CannotPrompt,

        // The store answered with something we could not turn into text at all:
        // an I/O error, a permission error on the file, an unmapped OSStatus.
        Unreadable,

        // We read it and it is not the shape we expect. Distinguished from
        // Unreadable on purpose — this one says the *format* moved, which is the
        // failure an undocumented dependency produces, and it is the one worth
        // surfacing differently from a transient read error.
        Malformed,

        // **The store never answered at all.** Not an error code — the absence of
        // one. Measured on a real Mac: in a context with no window server session,
        // the *data* query into Security.framework does not return, at 30 seconds
        // and at 60. The attributes-only query is unaffected and answers instantly,
        // every time, which is what makes this a property of the secret read rather
        // than of the Keychain being unreachable.
        //
        // **Named for the observation, not for a mechanism.** The obvious story is
        // that a CLI token refresh rewrote the item and reset its ACL, and the
        // timing fits — the stamp moved between a working read and a hanging one.
        // But so does "the earlier success rode a grant that has since lapsed", and
        // nobody has told the two apart. `Unavailable` was rejected as a name
        // because it reads as retryable and this must never be retried on a timer;
        // `NoPrompt` was rejected because it asserts the unproven cause.
        //
        // What is *not* in doubt is the consequence, and it is the worst failure
        // shape this repository has: before CB-164 put a budget around the read,
        // the supervisor thread simply parked, the status stayed on "checking…"
        // forever, and the user got no orbs and no error — indistinguishable from
        // having no cloud sessions.
        NoAnswer,
    }

    // One reading of the credential store.
    //
    // AccessToken is populated only for Found, and only for as long as the caller
    // holds this record. See the custody rules above: there is no refresh-token
    // field here and there must not be one.
    internal sealed record CredentialRead(
        CredentialOutcome Outcome,
        string? AccessToken,
        DateTimeOffset? ExpiresAt,
        string? Detail,
        // A more actionable status line than the outcome's own wording, when a
        // store further up the walk knew something the last one could not say
        // (the file answered "expired" before the Keychain failed to answer).
        string? Lead = null);

    // How long a credential read is given, and the clock that decides it has
    // run out (CB-269).
    //
    // **The clock is a value carried by the caller, not a static.** The budget
    // race used to be `Task.Delay(budget)` against a real thread-pool read, so a
    // test that handed it five seconds and an instant fake was racing a real
    // clock against a real scheduler: when another test class pinned the pool,
    // the delay won and a correct send reported NotSent after exactly the budget.
    // A process-wide hook would be the same shared-state trap one level up, so
    // each session, sender and arm carries its own.
    //
    // Production never builds one by hand: `Of` wraps Task.Delay, which is the
    // behaviour this replaced, and `Unmeasured` is the 45 s placeholder.
    internal sealed record CredentialBudget(
        TimeSpan Length, Func<TimeSpan, CancellationToken, Task> Expiry)
    {
        internal static CredentialBudget Of(TimeSpan length) => new(length, Task.Delay);

        internal static readonly CredentialBudget Unmeasured =
            Of(ClaudeCliCredentials.UnmeasuredReadBudget);
    }

    // Where a credential comes from, as an interface, for the same reason
    // IUsageSource and IRemoteChatSession exist: the real one is an OS prompt or
    // a file in someone's home directory, and a surface that cannot be faked
    // cannot be tested. Every test in this repository talks to a fake; nothing in
    // the suites touches the real Keychain.
    // A source that shares a read gate with others. See ReadWithinAsync.
    internal interface IGatedCredentialSource : ICloudCredentialSource
    {
        SemaphoreSlim Gate { get; }

        // What to read this time; called with the gate held.
        ICloudCredentialSource Choose();

        // What the read said; called with the gate still held.
        void Report(CredentialRead read);
    }

    internal interface ICloudCredentialSource
    {
        // A cheap value that changes when the stored credential changes, and
        // otherwise does not. It exists so a poll can notice a re-login without
        // reading the secret every tick — on macOS that matters twice over,
        // because the attributes-only Keychain query does not return data and so
        // is not the query the consent prompt guards.
        //
        // Never secret bytes. A file mtime, a Keychain modification date.
        string? Stamp();

        // Read the credential. On macOS this is the call that can prompt.
        CredentialRead Read();
    }

    // The pure half: paths, parsing and wording, with no store behind any of it.
    internal static class ClaudeCliCredentials
    {
        // The Keychain generic-password service the Claude Code CLI stores under
        // on macOS. Read off a real machine, not from documentation.
        internal const string KeychainService = "Claude Code-credentials";

        // The service name the CLI actually uses, which depends on where its
        // config directory is. Read out of the 2.1.284 binary:
        //
        //   e = CLAUDE_SECURESTORAGE_CONFIG_DIR
        //   unsuffixed = e !== undefined ? !e : !CLAUDE_CONFIG_DIR
        //   dir        = e !== undefined ? e.normalize("NFC") : be()
        //   suffix     = unsuffixed ? "" : "-" + sha256(dir).hex.substring(0, 8)
        //   be()       = (CLAUDE_CONFIG_DIR || ~/.claude).normalize("NFC")
        //
        // **The suffix appears whenever CLAUDE_CONFIG_DIR is set at all — even
        // when it names ~/.claude.** So "the default directory" is not a property
        // of a path but of how the CLI was launched, and this function therefore
        // does not try to recognise ~/.claude: null or empty means "launched with
        // no CLAUDE_CONFIG_DIR", anything else is hashed as given. Callers that
        // know a root can be reached both ways (see ServicesForRoot) ask twice.
        //
        // configDir is the CLAUDE_CONFIG_DIR value; secureStorageDir is
        // CLAUDE_SECURESTORAGE_CONFIG_DIR, null meaning unset — an *empty* value
        // is set, and forces the unsuffixed name, exactly as the CLI's `!e`.
        //
        // ASSUMED, not verified on a machine: that OAUTH_FILE_SUFFIX is empty in
        // production (the unsuffixed name the app already reads confirms it), and
        // that the hash input is the string as the shell handed it over — so a
        // trailing slash or an unexpanded ~ in CLAUDE_CONFIG_DIR would hash
        // differently from the path this app derives. The hash is over the UTF-8
        // bytes of the NFC form, first 8 lowercase hex digits.
        internal static string KeychainServiceFor(string? configDir, string? secureStorageDir = null)
        {
            var unsuffixed = secureStorageDir is not null
                ? secureStorageDir.Length == 0
                : string.IsNullOrEmpty(configDir);
            if (unsuffixed) return KeychainService;

            var dir = (secureStorageDir ?? configDir!).Normalize(System.Text.NormalizationForm.FormC);
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir));
            return KeychainService + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        // Every config root worth looking in, in the order to look.
        //
        // The Buddy app is a menu-bar app and normally has no CLAUDE_CONFIG_DIR of
        // its own, so the CLI's environment cannot be read from here; what the app
        // does know is ClaudeConfigRoots — the default ~/.claude plus each extra
        // the user listed in settings — and that is reused rather than listed a
        // fourth time. Extras only appear there if configured (Claude Code
        // profile directories in settings); an account run out of an unlisted
        // CLAUDE_CONFIG_DIR is not found.
        //
        // The process's own CLAUDE_CONFIG_DIR is added when set, for a `dotnet run`
        // from a shell that has it.
        internal static IReadOnlyList<string> CandidateRoots(string home, string? configDirEnv)
        {
            var roots = new List<string>(ClaudeConfigRoots.All(home));
            if (!string.IsNullOrEmpty(configDirEnv) && !roots.Contains(configDirEnv, StringComparer.Ordinal))
                roots.Add(configDirEnv);
            return roots;
        }

        // The folder's name, never an email. A directory can be named after the
        // account it holds ("~/.claude-me@example.com"), and the label is shown on
        // a screen someone else may be looking at, so anything from an "@" on is
        // dropped; a name that was nothing else becomes "account".
        internal static string SafeLabel(string root)
        {
            var label = UsageAccounts.FallbackLabel(root);
            var at = label.IndexOf('@');
            if (at < 0) return label;
            return at == 0 ? "account" : label[..at];
        }

        // A root spelt the way the CLI would have been given it: no trailing
        // separator. A settings entry of ".claude-board/" reaches here as
        // "<home>/.claude-board/", but a shell exports CLAUDE_CONFIG_DIR without
        // the slash, and the CLI hashes what it was given verbatim. A root that is
        // nothing but separators is left as it was.
        internal static string TrimRoot(string root)
        {
            var trimmed = root.TrimEnd('/', '\\');
            return trimmed.Length == 0 ? root : trimmed;
        }

        // The Keychain service names to try for ONE root, in order.
        //
        // The default root is asked twice: unsuffixed (CLI launched without
        // CLAUDE_CONFIG_DIR) and suffixed (launched with CLAUDE_CONFIG_DIR=~/.claude).
        // Every other root can only have been reached through CLAUDE_CONFIG_DIR, so
        // only suffixed.
        internal static IReadOnlyList<string> ServicesForRoot(string home, string root)
        {
            var trimmed = TrimRoot(root);
            var suffixed = KeychainServiceFor(trimmed);
            return string.Equals(trimmed, TrimRoot(Path.Combine(home, ".claude")), StringComparison.Ordinal)
                ? new[] { KeychainServiceFor(null), suffixed }
                : new[] { suffixed };
        }

        // Where the CLI keeps the same thing on Windows and Linux.
        //
        // Note this is *inside* the config root, unlike UsageAccounts'
        // `.claude.json`, which is a sibling of it. The two files are neighbours
        // with different rules and it is worth not reasoning from one to the
        // other — see the comment on UsageAccounts.AccountFilePath for what that
        // trap already cost once.
        internal static string CredentialsFilePath(string configRoot) =>
            Path.Combine(configRoot, ".credentials.json");

        // The organisation the account belongs to, out of `~/.claude.json`.
        //
        // **Not sent anywhere, and that correction is the point of this comment.**
        // It used to say `x-organization-uuid` was one of six headers the endpoint
        // refuses a request without. That was measured against claude.ai, which is
        // the wrong host — against api.anthropic.com the header is ignored, along
        // with the other three claude.ai-specific ones, and only Authorization and
        // anthropic-version are required. `CloudRequestContext` no longer has a
        // field to put this in.
        //
        // Kept because it is the only place in the app that can name the account's
        // organisation, and `tools/claude-cloud-probe` still prints whether one was
        // found — a useful thing to know when diagnosing whose credential is in the
        // store. It is diagnostic now rather than load-bearing.
        internal static string? OrganizationUuidFrom(string? claudeJson)
        {
            if (string.IsNullOrWhiteSpace(claudeJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(claudeJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("oauthAccount", out var account)) return null;
                if (account.ValueKind != JsonValueKind.Object) return null;
                if (!account.TryGetProperty("organizationUuid", out var uuid)) return null;
                if (uuid.ValueKind != JsonValueKind.String) return null;

                var value = uuid.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Turn a stored blob into a reading.
        //
        // `now` is a parameter rather than DateTimeOffset.UtcNow so the expiry
        // arm is testable without a clock, the same argument the transcript
        // parsers make for taking their input rather than reading it.
        //
        // Reads two fields and no others, on purpose. See the custody rules.
        internal static CredentialRead ParseCredentials(string? json, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                    "no credential stored");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return new CredentialRead(CredentialOutcome.Malformed, null, null,
                    "the stored credential is not JSON");
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                    || oauth.ValueKind != JsonValueKind.Object)
                {
                    return new CredentialRead(CredentialOutcome.Malformed, null, null,
                        "the stored credential has no claudeAiOauth object");
                }

                if (!oauth.TryGetProperty("accessToken", out var token)
                    || token.ValueKind != JsonValueKind.String)
                {
                    return new CredentialRead(CredentialOutcome.Malformed, null, null,
                        "the stored credential has no access token");
                }

                // The CLI blanks the entry (accessToken and refreshToken "",
                // expiresAt 0) when its refresh token dies. That is a well-formed
                // statement that nobody is logged in here, not a shape we fail to
                // understand — and reporting it Malformed sent a user to look for
                // a format change when the answer was "sign in again".
                if (string.IsNullOrEmpty(token.GetString()))
                {
                    return new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                        "the Claude Code CLI signed this login out");
                }

                var expiresAt = ExpiryFrom(oauth);

                // An expired copy is NotLoggedIn rather than an error, because
                // that is what it actually means to us: the CLI refreshes on its
                // own next use and we re-read afterwards. It is not Malformed —
                // nothing is wrong with it — and treating it as a failure would
                // invite exactly the retry loop this file is meant to avoid.
                if (expiresAt is { } when && when <= now)
                {
                    return new CredentialRead(CredentialOutcome.NotLoggedIn, null, when,
                        "the stored credential expired; the CLI refreshes it on its next use",
                        ExpiredLead);
                }

                return new CredentialRead(CredentialOutcome.Found, token.GetString(), expiresAt,
                    "a credential is present");
            }
        }

        // `expiresAt` is written by the CLI as Unix milliseconds. An ISO string is
        // accepted too rather than assumed away: this is an undocumented format we
        // do not own, and a shape change there should cost us an expiry check, not
        // a parse failure.
        private static DateTimeOffset? ExpiryFrom(JsonElement oauth)
        {
            if (!oauth.TryGetProperty("expiresAt", out var expires)) return null;

            if (expires.ValueKind == JsonValueKind.Number
                && expires.TryGetInt64(out var millis))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(millis);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            if (expires.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expires.GetString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        // What to say on screen about an outcome.
        //
        // Every string here is safe to render: none of them is derived from the
        // credential, and the negative-control test asserts that for a fake token
        // chosen to be findable if it ever leaked into one.
        // The stored login has a past expiry and the CLI has not refreshed it. The
        // most actionable thing any store can say, so it leads whatever a later
        // store failed with.
        internal const string ExpiredLead = "the stored login expired — run `claude` to refresh it";

        // What a reading says on the status line: a store's own lead if it has
        // one, else the outcome's wording.
        internal static string StatusFor(CredentialRead read) => read.Lead ?? Describe(read.Outcome);

        internal static string Describe(CredentialOutcome outcome) => outcome switch
        {
            CredentialOutcome.Found => "signed in to Claude Code",
            CredentialOutcome.NotLoggedIn => "no Claude Code login found — run `claude` and sign in",
            CredentialOutcome.Denied => "access to the Claude Code login was declined",
            CredentialOutcome.CannotPrompt =>
                "the Keychain could not ask for permission — no one is at the screen, or this is a background context",
            CredentialOutcome.Unreadable => "the Claude Code login could not be read",
            CredentialOutcome.Malformed => "the Claude Code login is not in a shape this version understands",
            CredentialOutcome.NoAnswer =>
                "the Keychain did not answer — this can happen when no one is logged in at the screen",
            _ => "the Claude Code login is in an unknown state",
        };

        // How long to wait for a read before giving up on it.
        //
        // **Generous rather than snappy, deliberately.** A read that can answer
        // answers in milliseconds; the only thing a longer budget waits for is a
        // human looking at a consent dialog, and cutting one of those off is the
        // expensive mistake here — the arm halts on a timeout and stays halted
        // until the stamp moves or the user asks again, so abandoning a prompt
        // somebody was about to approve costs them a round trip through the
        // settings window. Waiting 45 seconds in the broken case costs nobody
        // anything, because this is a background poll behind an ambient overlay
        // and it runs off the supervisor's thread.
        //
        // **The number itself is unmeasured.** Two things would settle it: how
        // long a person actually takes to answer a Keychain dialog they were not
        // expecting, and whether the hang has any upper bound at all (it was
        // killed at 30s and 60s, never observed to return). Named Unmeasured for
        // the reason CB-122 exists.
        internal static readonly TimeSpan UnmeasuredReadBudget = TimeSpan.FromSeconds(45);

        // Whether this process is forbidden from asking the OS for a credential.
        //
        // The same env-var seam shape as ORBWEAVER_SETTINGS_DIR,
        // ORBWEAVER_PROFILE_ROOT and ORBWEAVER_BUNDLE_ROOT, and for the same
        // reason all three exist: without it a test reaches something real that
        // belongs to the person running it. Those three protect a settings file, a
        // profile directory and an icon cache. This one protects their Keychain,
        // and unlike the others the failure is not a dirtied file — it is a consent
        // dialog nobody answers and a suite that never finishes.
        //
        // Read on every call rather than cached, so a test can set it in a
        // [ModuleInitializer] before any static constructor here has run and not
        // have to care about ordering. The read is a dictionary lookup against
        // process environment; it is not on a hot path.
        //
        // Not a bool parameter threaded through the call sites: the callers are a
        // poll loop and a chat path several layers up, and a flag that has to be
        // passed correctly from each of them is a flag that will eventually not be.
        internal static bool CredentialStoreDisabled =>
            BrandEnv.Get(BrandEnv.NoCredentialStore) is not null;

        // Read the credential, or give up.
        //
        // **The read happens on a borrowed thread and the caller's never blocks.**
        // That is the whole point: `ICloudCredentialSource.Read()` is a P/Invoke on
        // macOS and a blocked P/Invoke cannot be cancelled, so there is no way to
        // make the hung call stop. What there is a way to do is stop *waiting* for
        // it, which is what keeps the supervisor alive to say what happened.
        //
        // **The abandoned thread is leaked, and that is a real cost written down
        // rather than claimed away.** A pool thread parked inside Security.framework
        // never comes back. One is survivable. A poll that retried this every thirty
        // seconds would lose one thread per attempt for as long as the app ran, which
        // is the second independent reason `NoAnswer` stops the arm rather than
        // backing off — the first being that retrying a call which may be waiting on
        // a human is how a pile of consent prompts gets queued up.
        //
        // **A gated source is read one at a time, process-wide.** Several accounts
        // and their chat panels share one macOS consent surface, and two dialogs on
        // screen at once are unanswerable. The gate is taken here — before the
        // budget starts, so queueing is not charged to a read that has not begun —
        // and the source *chooses what to read after it has the gate*, so a prompt
        // declined while this one waited is honoured. The outcome is reported back
        // before the gate is released, which is what makes a NoAnswer latch by the
        // time the next reader gets in.
        internal static async Task<CredentialRead> ReadWithinAsync(
            ICloudCredentialSource source, CredentialBudget budget, CancellationToken ct)
        {
            if (source is not IGatedCredentialSource gated)
            {
                return await ReadBudgetedAsync(source, budget, ct).ConfigureAwait(false);
            }

            await gated.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var read = await ReadBudgetedAsync(gated.Choose(), budget, ct).ConfigureAwait(false);
                gated.Report(read);
                return read;
            }
            finally
            {
                gated.Gate.Release();
            }
        }

        // The wall-clock form, for callers that have no reason to inject one.
        internal static Task<CredentialRead> ReadWithinAsync(
            ICloudCredentialSource source, TimeSpan budget, CancellationToken ct) =>
            ReadWithinAsync(source, CredentialBudget.Of(budget), ct);

        private static async Task<CredentialRead> ReadBudgetedAsync(
            ICloudCredentialSource source, CredentialBudget budget, CancellationToken ct)
        {
            // Not cancelled by ct: cancelling the wait is the point, and handing ct
            // to the work as well would only mean the abandoned thread carried a
            // token nothing can act on.
            var read = Task.Run(source.Read, CancellationToken.None);

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var finished = await Task.WhenAny(read, budget.Expiry(budget.Length, timer.Token))
                .ConfigureAwait(false);

            if (!ReferenceEquals(finished, read))
            {
                ct.ThrowIfCancellationRequested();

                return new CredentialRead(CredentialOutcome.NoAnswer, null, null,
                    "the credential store did not answer");
            }

            // The delay is still pending whenever the read won the race. Cancelled
            // rather than left to fire, because this runs on a poll and an orphaned
            // timer per tick is a slow leak of exactly the kind nobody notices.
            timer.Cancel();

            try
            {
                return await read.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A source that throws is a bug in the source, not a state the arm
                // should crash on. The exception's own text is deliberately not
                // carried into the detail: this is the one call site in the app
                // holding a secret, and nothing that came out of it goes on screen.
                return new CredentialRead(CredentialOutcome.Unreadable, null, null,
                    "the credential store failed while being read");
            }
        }

        // Which store this platform keeps it in.
        //
        // Takes the platform as an argument rather than asking the runtime, so the
        // choice itself is testable on either machine — the same reason OrbGlyph
        // takes the two-letter setting instead of reading it.
        //
        // **One source per account, not one flat walk.** The first version put
        // every root's stores in a single MultiCredentialSource, whose first Found
        // wins — so on a Mac with two logged-in accounts one was read and the
        // other never looked at. An account is a config root; each gets its own
        // MultiCredentialSource over that root's candidates only.
        //
        // Within a root the file is tried first, then the Keychain. A live file
        // needs no prompt and a stale one falls through to the Keychain; one root
        // cannot hold two different live identities, so the order never changes
        // *which* account is found, only how many prompts it costs.
        //
        // Read out of the 2.1.284 binary: the CLI's store is "keychain with
        // plaintext fallback", and `<config dir>/.credentials.json` (unhashed) is
        // where it writes when the Keychain write fails. Confirmed on a real Mac,
        // where both ~/.claude and ~/.claude-board held live logins in files. The
        // location under a custom root on Windows/Linux is ASSUMED to be the same.
        internal static IReadOnlyList<CloudAccount> SourcesFor(
            bool isMacOS, string home, string? configDirEnv = null)
        {
            var defaultRoot = Path.Combine(home, ".claude");
            var accounts = new List<CloudAccount>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in CandidateRoots(home, configDirEnv))
            {
                var root = TrimRoot(raw);
                if (!seen.Add(root)) continue;

                var file = (Name: CredentialsFilePath(root),
                    Source: (ICloudCredentialSource)new FileCredentialSource(CredentialsFilePath(root)));
                var children = new List<(string Name, ICloudCredentialSource Source)> { file };
                if (isMacOS)
                {
                    foreach (var service in ServicesForRoot(home, root))
                        children.Add((service, new KeychainCredentialSource(service)));
                }

                var label = string.Equals(root, TrimRoot(defaultRoot), StringComparison.Ordinal)
                    ? "default" : SafeLabel(root);
                // Two folders can reduce to one label ("me@x.com", "me@y.com").
                // Told apart with a number rather than left to read as one account.
                var unique = label;
                for (var n = 2; accounts.Any(a => a.Label == unique); n++) unique = $"{label}-{n}";

                accounts.Add(new CloudAccount(
                    root,
                    unique,
                    new MultiCredentialSource(children),
                    new MultiCredentialSource(new[] { file })));
            }

            return accounts;
        }
    }

    // One Claude Code account: a config root, the name it goes by on screen, and
    // where its login lives.
    //
    // Label is the folder's name (UsageAccounts.FallbackLabel), never an email:
    // the settings line is shown to whoever is looking over a shoulder.
    // FileSource is the same account's file stores alone, used while another
    // account's declined Keychain prompt is latched — a file needs no prompt.
    internal sealed record CloudAccount(
        string Root, string Label, ICloudCredentialSource Source, ICloudCredentialSource FileSource);

    // Tries several stores in order and answers with the first login found.
    //
    // **Prompt discipline.** A Keychain item is guarded per item, so reading two
    // entries can mean two consent prompts. Before any data read each child's
    // Stamp() — attributes only, prompt-free — is asked, and a child with no
    // stamp does not exist and is skipped without a read. In the common cases
    // (one entry, or a blanked default plus a live suffixed one) that is zero or
    // one extra prompt; two prompts only happen when two entries genuinely exist
    // and the first one is not a live login. Denied stops the walk at once:
    // the user said no to that prompt, and asking about the next entry would just
    // be a second prompt after a refusal. NoAnswer stops it too, for the same
    // reason — an unanswered dialog is still on screen.
    //
    // If nothing is Found, the most informative failure wins: the first
    // non-NotLoggedIn outcome (Malformed, Unreadable) over a NotLoggedIn.
    //
    // No token is held here beyond the returned read; AnsweredBy and Attempts
    // carry store names and outcomes only.
    internal sealed class MultiCredentialSource : ICloudCredentialSource
    {
        private readonly IReadOnlyList<(string Name, ICloudCredentialSource Source)> _children;

        internal MultiCredentialSource(IReadOnlyList<(string Name, ICloudCredentialSource Source)> children) =>
            _children = children;

        internal IReadOnlyList<string> Names => _children.Select(c => c.Name).ToList();

        // What one Read did, returned with it and never shared between reads: a
        // parked read that finishes late must not scribble over a newer one's
        // answer.
        internal sealed record ReadTrace(
            string? AnsweredBy,
            IReadOnlyList<(string Name, CredentialOutcome Outcome, string Reason)> Attempts);

        private ReadTrace _last = new(null, Array.Empty<(string, CredentialOutcome, string)>());

        // The most recently *finished* read's trace, published whole. A diagnostic
        // convenience for single-threaded callers (the probe, the tests); anything
        // concurrent uses ReadTraced.
        private ReadTrace Last => Volatile.Read(ref _last);

        // Name of the store that produced the last Found read; null otherwise.
        internal string? AnsweredBy => Last.AnsweredBy;

        // Each store the last Read consulted, what it said and why. Names and
        // reason wording only, never values. A store that does not exist is
        // listed too (outcome NotLoggedIn, reason "no such item or file"), so a
        // diagnostic can tell "absent" from "present but blank".
        internal IReadOnlyList<(string Name, CredentialOutcome Outcome, string Reason)> Attempts =>
            Last.Attempts;

        // The stamps of every store that has one, keyed by name so a login moving
        // from one store to another still changes the value. Null when none exist.
        public string? Stamp()
        {
            var parts = new List<string>();
            foreach (var (name, source) in _children)
            {
                var stamp = source.Stamp();
                if (stamp is not null) parts.Add(name + "=" + stamp);
            }
            return parts.Count == 0 ? null : string.Join(";", parts);
        }

        public CredentialRead Read() => ReadTraced().Read;

        internal (CredentialRead Read, ReadTrace Trace) ReadTraced()
        {
            var attempts = new List<(string, CredentialOutcome, string)>();
            CredentialRead? best = null;
            string? expiredLead = null;

            foreach (var (name, source) in _children)
            {
                if (source.Stamp() is null)
                {
                    attempts.Add((name, CredentialOutcome.NotLoggedIn, "no such item or file"));
                    continue;
                }

                // This is not an unbudgeted read: the whole walk is itself the
                // `Read` that ReadWithinAsync runs on a pool thread under one
                // budget, so a child that parks in Security.framework is cut off
                // by the caller's timeout like any other. The method-group hop
                // exists only because CredentialBudgetTests forbids the literal
                // call shape, and rightly — it must stay forbidden everywhere else.
                Func<CredentialRead> readChild = source.Read;
                var read = readChild();
                var reason = read.Detail ?? ClaudeCliCredentials.Describe(read.Outcome);
                if (read.ExpiresAt is { } expiry) reason += $" (expiresAt {expiry:u})";
                attempts.Add((name, read.Outcome, reason));

                if (read.Outcome == CredentialOutcome.NotLoggedIn && read.Lead is { } lead)
                {
                    expiredLead ??= lead;
                }

                if (read.Outcome == CredentialOutcome.Found)
                {
                    return Done(read, name, attempts);
                }

                if (read.Outcome is CredentialOutcome.Denied or CredentialOutcome.NoAnswer)
                {
                    return Done(Lead(read, expiredLead), null, attempts);
                }

                if (best is null || (best.Outcome == CredentialOutcome.NotLoggedIn
                                     && read.Outcome != CredentialOutcome.NotLoggedIn))
                    best = read;
            }

            return Done(Lead(best ?? new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                "no credential stored"), expiredLead), null, attempts);
        }

        // The most actionable outcome wins the status line, the outcome itself
        // untouched: a Denied or NoAnswer after an expired file must still latch
        // and halt as one, but it should not be what the user is told.
        private static CredentialRead Lead(CredentialRead read, string? expiredLead) =>
            expiredLead is null ? read : read with { Lead = expiredLead };

        private (CredentialRead, ReadTrace) Done(CredentialRead read, string? answeredBy,
            List<(string, CredentialOutcome, string)> attempts)
        {
            var trace = new ReadTrace(answeredBy, attempts);
            Volatile.Write(ref _last, trace);
            return (read, trace);
        }
    }

    // Windows and Linux: the credential is a file.
    internal sealed class FileCredentialSource : ICloudCredentialSource
    {
        private readonly string _path;

        internal FileCredentialSource(string path) => _path = path;

        internal string Path => _path;

        // The file's last-write time in ticks. A change detector and nothing
        // else — no length, no hash of the contents, nothing derived from the
        // secret. Null when the file is absent, which is itself a state worth
        // distinguishing from an unchanged one.
        //
        // ArgumentException is caught alongside the I/O ones because FileInfo's
        // constructor throws it for a path the platform will not accept at all,
        // and this method is on a poll path: a stamp that cannot be taken must
        // read as "no credential" rather than as an exception out of a timer.
        public string? Stamp()
        {
            try
            {
                var info = new FileInfo(_path);
                return info.Exists ? info.LastWriteTimeUtc.Ticks.ToString() : null;
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        public CredentialRead Read()
        {
            string json;
            try
            {
                if (!File.Exists(_path))
                {
                    return new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                        "no credential file");
                }

                json = File.ReadAllText(_path);
            }
            catch (UnauthorizedAccessException)
            {
                // The file-store analogue of the Keychain's "Don't Allow": the OS
                // says no, and asking again will get the same answer.
                return new CredentialRead(CredentialOutcome.Denied, null, null,
                    "the credential file is not readable by this user");
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                return new CredentialRead(CredentialOutcome.Unreadable, null, null,
                    "the credential file could not be read");
            }

            return ClaudeCliCredentials.ParseCredentials(json, DateTimeOffset.UtcNow);
        }
    }

    // macOS: the credential is a Keychain item, and reading it prompts.
    //
    // Excluded from coverage for the same reason MacOSScreenLock is: every line
    // is a call into Security.framework about an item a CI runner does not have,
    // behind a consent dialog no runner can answer. The mapping it performs is in
    // MacOSKeychain and is equally untestable here; what *is* covered is
    // SourcesFor choosing this class, which is the decision this repository owns.
    [ExcludeFromCodeCoverage]
    internal sealed class KeychainCredentialSource : ICloudCredentialSource
    {
        private readonly string _service;

        // The last successful read and the stamp it was taken at. When the stamp
        // has not moved, the cached result is returned without calling
        // SecItemCopyMatching — which is the call that prompts. The CLI rewrites
        // the keychain item on every token refresh and that rewrite resets the
        // item's ACL, wiping any "Always Allow" grant for this app. Without a
        // cache every poll cycle would re-prompt, which is what CB-259 fixed.
        //
        // The cache is two-tier: in-memory (survives within a process) and on
        // disk (survives across restarts). The disk cache lives in Buddy's own
        // app-support directory, one file per keychain service, at 0600. On
        // launch the stamp is checked (prompt-free), and if it matches the disk
        // cache the credential is loaded from the file instead of the keychain —
        // so a restart after the CLI refreshed its token is the only case that
        // prompts, not every restart.
        private CredentialRead? _cached;
        private string? _cachedStamp;

        internal KeychainCredentialSource(string? service = null) =>
            _service = service ?? ClaudeCliCredentials.KeychainService;

        // The attributes-only query. It returns no data, so it is not the query
        // the consent prompt guards — which is the whole point of Stamp() being a
        // separate call from Read() rather than a field on it.
        public string? Stamp() =>
            ClaudeCliCredentials.CredentialStoreDisabled
                ? null
                : MacOSKeychain.ModificationStamp(_service);

        public CredentialRead Read()
        {
            // A test process never asks the OS for this.
            if (ClaudeCliCredentials.CredentialStoreDisabled)
            {
                return new CredentialRead(CredentialOutcome.NotLoggedIn, null, null,
                    "the credential store is disabled for this process");
            }

            var stamp = MacOSKeychain.ModificationStamp(_service);

            // Tier 1: in-memory cache — stamp unchanged.
            if (_cached is { Outcome: CredentialOutcome.Found } cached
                && stamp is not null
                && string.Equals(stamp, _cachedStamp, StringComparison.Ordinal))
            {
                return cached;
            }

            // Tier 1b: in-memory cache — stamp changed but token still valid.
            // The CLI rewrites the keychain item on every token refresh, which
            // resets the item's ACL and wipes any "Always Allow" grant for this
            // app. So a stamp change means a new prompt even if the user already
            // approved. But the OLD token is still valid until its expiresAt —
            // OAuth access tokens typically last an hour. Using it avoids the
            // prompt entirely; the new token is picked up on the next poll after
            // the cached one expires.
            if (_cached is { Outcome: CredentialOutcome.Found, ExpiresAt: { } expiry } stillGood
                && expiry > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return stillGood;
            }

            // Tier 2: disk cache — survives restarts, never prompts.
            // When the stamp has changed, the disk cache's stamp won't match,
            // but the credential it holds may still be unexpired.
            if (stamp is not null)
            {
                var diskRead = ReadDiskCache(stamp);
                if (diskRead is { Outcome: CredentialOutcome.Found })
                {
                    _cached = diskRead;
                    _cachedStamp = stamp;
                    return diskRead;
                }

                // Stamp changed: the disk cache's stamp doesn't match, but
                // its credential may still be valid.
                var staleRead = ReadDiskCacheIgnoringStamp();
                if (staleRead is { Outcome: CredentialOutcome.Found, ExpiresAt: { } diskExpiry }
                    && diskExpiry > DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    _cached = staleRead;
                    // Don't update _cachedStamp — the stamp has changed, so
                    // we'll re-check on the next poll. Once the token expires
                    // we fall through to tier 3 and prompt once.
                    return staleRead;
                }
            }

            // Tier 3: the real keychain read — this is the call that prompts.
            var (outcome, json, detail) = MacOSKeychain.ReadGenericPassword(_service);
            if (outcome != CredentialOutcome.Found)
            {
                return new CredentialRead(outcome, null, null,
                    detail ?? ClaudeCliCredentials.Describe(outcome));
            }

            var read = ClaudeCliCredentials.ParseCredentials(json, DateTimeOffset.UtcNow);
            if (read.Outcome == CredentialOutcome.Found)
            {
                _cached = read;
                _cachedStamp = stamp;
                WriteDiskCache(stamp, json);
            }

            return read;
        }

        private string CacheDir
        {
            get
            {
                var appSupport = Path.Combine(
                    BrandEnv.Get(BrandEnv.SettingsDir)
                        is { Length: > 0 } scratch
                        ? scratch
                        : Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            Brand.DataDirName),
                    "credential-cache");
                return appSupport;
            }
        }

        private string CacheFilePath =>
            Path.Combine(CacheDir, _service.Replace('/', '_').Replace('\\', '_') + ".json");

        private CredentialRead? ReadDiskCache(string currentStamp)
        {
            try
            {
                var path = CacheFilePath;
                if (!File.Exists(path)) return null;

                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                if (!root.TryGetProperty("stamp", out var stampEl)
                    || stampEl.GetString() is not { } savedStamp
                    || !string.Equals(savedStamp, currentStamp, StringComparison.Ordinal))
                {
                    return null;
                }

                if (!root.TryGetProperty("credential", out var credEl)
                    || credEl.GetRawText() is not { Length: > 0 } credJson)
                {
                    return null;
                }

                return ClaudeCliCredentials.ParseCredentials(credJson, DateTimeOffset.UtcNow);
            }
            catch
            {
                return null;
            }
        }

        // Read the disk cache without checking the stamp. Used when the stamp
        // has changed (CLI refreshed the token) but the cached credential may
        // still be unexpired — in which case using it avoids a prompt.
        private CredentialRead? ReadDiskCacheIgnoringStamp()
        {
            try
            {
                var path = CacheFilePath;
                if (!File.Exists(path)) return null;

                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                if (!root.TryGetProperty("credential", out var credEl)
                    || credEl.GetRawText() is not { Length: > 0 } credJson)
                {
                    return null;
                }

                return ClaudeCliCredentials.ParseCredentials(credJson, DateTimeOffset.UtcNow);
            }
            catch
            {
                return null;
            }
        }

        private void WriteDiskCache(string? stamp, string? credentialJson)
        {
            if (stamp is null || credentialJson is null) return;

            try
            {
                var dir = CacheDir;
                Directory.CreateDirectory(dir);

                var path = CacheFilePath;
                var json = $"{{\"stamp\":{JsonSerializer.Serialize(stamp)},\"credential\":{credentialJson}}}";
                File.WriteAllText(path, json);

                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // Best effort — failing to cache just means the next launch
                // prompts again, which is the pre-cache behaviour.
            }
        }
    }
}
