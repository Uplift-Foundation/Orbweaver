using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Talking to the account API's cloud-sessions endpoint.
    //
    // **Everything in this file is about an undocumented API.** It is
    // `GET https://api.anthropic.com/v2/ccr-sessions`, measured by hand during
    // CB-164 rather than read from documentation, and the product decision on
    // that ticket accepted the maintenance cost of depending on it with eyes
    // open. The consequence for the code is that every shape here is treated as
    // something that can move without notice: a request that comes back wrong
    // produces a legible outcome and no cloud orbs, never a crash and never a
    // retry storm.
    //
    // **The host is api.anthropic.com and that is the whole first lesson of this
    // ticket.** The first attempt aimed at claude.ai, because claude.ai/code is
    // where a person reads this roster — and claude.ai answers a non-browser
    // client with a Cloudflare challenge, *identically* for a real token and a
    // bogus one. That identical pair is what makes it expensive: it looks exactly
    // like a credential being refused, so an afternoon went into the credential
    // and none into the host. The header set that grew out of it — six headers
    // the endpoint supposedly 400s without — was the browser's, not the API's.
    // Against api.anthropic.com **two** headers are required and the other four
    // are ignored. See docs/claude-cloud-findings.md for the measurement table.
    //
    // The transport is a plain HttpClient, as NeuralSpeech uses. Emphatically
    // *not* OpenClawSocket's BouncyCastle TLS stack — that exists because a
    // self-signed LAN gateway speaks TLS 1.3 that .NET on macOS cannot, and
    // api.anthropic.com is an ordinary public HTTPS host with a real certificate.
    // Copying it would mean hand-rolling TLS against a host that does not need it.
    internal enum CloudOutcomeKind
    {
        // 200, with a body to parse.
        Ok,

        // 401, and the endpoint told us it parsed an OAuth bearer token and did
        // not like it. CB-164 measured this as a distinct message; see OutcomeFor.
        TokenRefused,

        // 401 of the generic kind — no credential recognised at all.
        AuthFailed,

        // 400. Measured once deliberately — `limit=200` is refused with "must be
        // greater than or equal to 0 and less than 101" — so in practice this
        // means *our* request shape is wrong, which after shipping means the
        // contract moved under us. Named for what it tells the user rather than
        // for the number.
        ShapeChanged,

        // 403. The account may not do this, or something in front of the endpoint
        // declined us. Not retryable by waiting. The two read very differently to
        // a person and OutcomeFor tells them apart — see BlockedDetail.
        Blocked,

        // 429, with whatever Retry-After came with it.
        RateLimited,

        // 5xx, a timeout, a DNS failure, no network. Retryable, and the only kind
        // that is.
        Unavailable,

        // 409, on a write. **Measured by CB-225**: a send to a session archived
        // moments earlier answered 409 with error type `session_not_active`,
        // "Session <id> is not active". CB-199 had only the CLI binary's name
        // for it, `session_inactive`, which turned out to be the CLI's own
        // label rather than the wire string — so nothing here matches on the
        // type, only on the status.
        SessionInactive,

        // 413. Plain HTTP: the body was bigger than the endpoint takes. The CLI
        // handles it on the same route; the limit itself is not known here.
        TooLarge,

        // 404 on a session: it no longer exists. **Measured** against a deleted
        // cloud session: `GET /v1/code/sessions/{id}` answered 404
        // `not_found_error` "Session <id> not found", and
        // `GET /v2/ccr-sessions/{id}/events` answered 404 as well. Distinct from
        // SessionInactive, which is an ended session and is not measured. A
        // POST to a deleted session is not measured either; it is assumed to be
        // 404 like the reads, and lands here if so.
        //
        // OutcomeFor cannot see the path, so a 404 anywhere reads as this. The
        // one caller where that would be wrong — the roster listing, where a
        // 404 means the collection moved rather than a session went — undoes it
        // itself; see ClaudeCloudSessions.RosterView.
        SessionGone,
    }

    // One attempt's verdict. Status is the HTTP status where there was one and 0
    // where the request never got an answer.
    //
    // SessionNotFound (CB-225) is true only for a 404 whose body is the
    // handler's JSON `not_found_error`. A 404 comes in two kinds that mean
    // opposite things — that, and the router's plain-text "404 page not
    // found" for a route that does not exist — and Kind reads both as
    // SessionGone. The distinction is made here, where the body is in hand,
    // so it can travel without the body: HttpCloudApi hands a caller the body
    // only on a 2xx (ResultFor), and archive and delete need the distinction
    // on a 404.
    internal sealed record CloudOutcome(
        CloudOutcomeKind Kind,
        int Status,
        TimeSpan? RetryAfter = null,
        string? Detail = null,
        bool SessionNotFound = false);

    // A verdict plus the body, for Ok. The body is deliberately a string rather
    // than a parsed roster: parsing belongs to ClaudeCloudRoster, and keeping the
    // two apart is what lets a fixture be captured from the probe without this
    // file knowing anything about session titles.
    internal sealed record CloudApiResult(CloudOutcome Outcome, string? Body);

    // What one call needs to know.
    //
    // **A readonly record struct constructed at the call site and never stored.**
    // It carries the access token, and the custody rules in ClaudeCliCredentials
    // apply to it in full: nothing holds one of these across a request, and
    // nothing puts one in a field.
    //
    // No organisation field. `OrganizationUuid` was here because
    // `x-organization-uuid` was believed mandatory; it is claude.ai's header and
    // api.anthropic.com ignores it. Removing the field rather than leaving it
    // unused is deliberate — an unused credential-adjacent field is an invitation
    // to start sending it again, and a compile error is a better argument than a
    // comment. CB-199's gate re-measured it for writes and it is still not
    // needed, for a send or for an interrupt.
    //
    // Method and Body arrived with CB-199's writes, as optional trailing fields
    // so every read in the app still constructs this with two arguments and
    // still means a bodiless GET. The body is a payload the user typed, never a
    // credential: the token travels in AccessToken and only ever reaches the
    // Authorization header.
    internal readonly record struct CloudRequestContext(
        string AccessToken,
        string Path,
        HttpMethod? Method = null,
        string? Body = null);

    // Where the cloud arm asks, and the exact shape it must ask in.
    internal static class CloudRequest
    {
        internal const string Host = "https://api.anthropic.com";

        // The collection every path below hangs off.
        internal const string SessionsPath = "/v2/ccr-sessions";

        // **Where a write goes, and it is not under SessionsPath.** Read out of
        // the Claude Code CLI binary and then measured by CB-199's gate: user
        // turns and interrupts are `POST /v1/code/sessions/{id}/events`, while
        // every write tried under `/v2/ccr-sessions` 404s — that prefix turns out
        // to be in-container ingress authenticated by a session JWT, which is why
        // CB-164 found no input route there. Same host, same two headers: the
        // gate measured that `anthropic-beta` and `x-organization-uuid` change
        // nothing on this route either, for a send or for an interrupt.
        internal const string CodeSessionsPath = "/v1/code/sessions";

        // **Measured: 101 is refused.** `limit=200` comes back 400 with "must be
        // greater than or equal to 0 and less than 101", so this is the real
        // ceiling rather than a self-imposed politeness, and a walk that asked
        // for more would get nothing at all rather than fewer rows.
        internal const int MaxPageSize = 100;

        // **Two headers, and they are the measured set.** Authorization plus
        // anthropic-version returns 200. `anthropic-beta`,
        // `anthropic-client-feature`, `anthropic-client-platform` and
        // `x-organization-uuid` were all carried here from the claude.ai attempt
        // and all four are unnecessary: they are what claude.ai's web client
        // sends, and claude.ai is the wrong host.
        //
        // They are constants so a test can assert each one exactly. A header set
        // copied into prose goes stale; one a test reads off a built request does
        // not.
        internal const string VersionHeader = "anthropic-version";
        internal const string VersionValue = "2023-06-01";

        // How often the cloud arm walks the whole roster.
        //
        // **This number is not measured and nothing justifies it.** CB-122 is the
        // cautionary tale for exactly this constant: a cadence defended by a
        // comment asserting a fact about an external system that nobody had ever
        // checked, which then ruled out the cheapest explanation for a real bug
        // for hours. So this one says plainly that it is a placeholder, and names
        // what would settle it:
        //
        //  * **Does `updated_at` advance during a running turn**, or only when a
        //    turn ends? If only at the end, a fast cadence buys nothing at all and
        //    this should be minutes; if it advances continuously, the cadence is
        //    what decides whether a generating orb looks alive.
        //  * **Does this endpoint 429 anybody at this rate over a whole day?** One
        //    page is about 0.6s and the roster measured six pages, so a walk is a
        //    handful of seconds — but rate limits are not something to infer from
        //    a single afternoon's probing.
        //  * **How often is a cloud session first seen only on a deep page?** That
        //    is the entire argument for the walk existing beside the first-page
        //    poll, and nobody has counted it.
        //
        // Until somebody answers those three, these are placeholders with a
        // plausible shape and no defence, and they are named so that reading the
        // call site says so.
        internal static readonly TimeSpan UnmeasuredWalkInterval = TimeSpan.FromMinutes(5);

        // The short cycle: page one, plus one direct read per cloud session we
        // already know about. The per-session reads are what stop an older cloud
        // session resuming from being invisible until the next deep walk — the
        // roster is `created_at` DESC and **`updated_at` is unsorted**, so a
        // session waking up does not move to the front.
        internal static readonly TimeSpan UnmeasuredFirstPageInterval = TimeSpan.FromSeconds(30);

        // How many pages one walk will ask for before it stops and says so.
        //
        // The roster measured 578 rows, six pages at the maximum size, so ten is
        // headroom rather than a limit anybody has hit. What matters is not the
        // number but that hitting it is **visible**: Reduce marks the reduction
        // truncated and Describe puts it in the status text. A cap that silently
        // showed fewer sessions would be the same class of bug as the filter
        // getting it wrong, and harder to notice.
        internal const int MaxPagesPerWalk = 10;

        // The listing call. `limit` and `after_id` are the only two query
        // parameters that do anything: `environment_kind`, `session_status`,
        // `status_bucket`, `exclude_archived` and `order` were all sent and all
        // **silently ignored** — no error, just an unfiltered page. That is why
        // the filter lives in ClaudeCloudRoster.Keep and not in a query string.
        internal static string ListPath(int limit, string? afterId)
        {
            var capped = limit < 1 ? 1 : limit > MaxPageSize ? MaxPageSize : limit;
            var path = SessionsPath + "?limit=" + capped.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(afterId)
                ? path
                : path + "&after_id=" + Uri.EscapeDataString(afterId);
        }

        // One session, read directly. The id is escaped rather than trusted: it
        // arrives from a payload we do not own, and a path is the one place an
        // unescaped external string turns into a different request.
        internal static string SessionPath(string id) =>
            SessionsPath + "/" + Uri.EscapeDataString(id);

        // A session's transcript. **This is Claude Code's own transcript format**
        // — rows with `type`, a `message` with `role` and content blocks — which
        // is why the chat session built on it reuses ChatTranscript rather than
        // parsing a second time. Same `{data, has_more, last_id}` paging as the
        // listing.
        internal static string EventsPath(string id, int limit, string? afterId)
        {
            var capped = limit < 1 ? 1 : limit > MaxPageSize ? MaxPageSize : limit;
            var path = SessionPath(id) + "/events?limit=" + capped.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(afterId)
                ? path
                : path + "&after_id=" + Uri.EscapeDataString(afterId);
        }

        // A session's write endpoint: one POST per batch of events.
        //
        // **Null for an id that is not well formed, and that is a refusal rather
        // than an escape.** The read paths above escape an id and send it anyway,
        // because a malformed id there costs a 404. Here the request *writes*, so
        // an id that is not shaped like one is refused before it can become a
        // request at all — the CLI applies the same rule to the same route. The
        // escape stays too, as the second of two locks; for a well-formed id it
        // changes nothing.
        internal static string? CodeEventsPath(string? id) =>
            ClaudeCloudRoster.IsWellFormedId(id)
                ? CodeSessionsPath + "/" + Uri.EscapeDataString(id!) + "/events"
                : null;

        // A page of a session's events from the write host, newest first, and the
        // page before it when given the last page's `next_cursor`. Measured
        // (2026-11-17): newest first with or without `sort_order=desc`, sent
        // anyway so the order is asked for rather than relied on. Null for a
        // malformed id, for CodeEventsPath's reason.
        internal static string? NewestEventsPath(string? id, int limit, string? cursor)
        {
            if (CodeEventsPath(id) is not { } events) return null;

            var capped = limit < 1 ? 1 : limit > MaxPageSize ? MaxPageSize : limit;
            var path = events + "?limit=" + capped.ToString(CultureInfo.InvariantCulture) + "&sort_order=desc";
            return string.IsNullOrWhiteSpace(cursor) ? path : path + "&cursor=" + Uri.EscapeDataString(cursor);
        }

        // A session's own record on the write host, which is where its live turn
        // state is read while a panel is watching a reply (CB-199). **Measured**:
        // 200 with the same two headers as a read, and `status_bucket` "working"
        // mid-turn. Refused locally for a malformed id, for the reason
        // CodeEventsPath gives, even though this one is a read.
        internal static string? CodeSessionPath(string? id) =>
            ClaudeCloudRoster.IsWellFormedId(id)
                ? CodeSessionsPath + "/" + Uri.EscapeDataString(id!)
                : null;

        // A session's archive endpoint (CB-225). **Measured**: `POST` with `{}`
        // answers 200 and the session back as archived. Refused locally for a
        // malformed id, for the reason CodeEventsPath gives — this is a write.
        internal static string? ArchivePath(string? id) =>
            ClaudeCloudRoster.IsWellFormedId(id)
                ? CodeSessionsPath + "/" + Uri.EscapeDataString(id!) + "/archive"
                : null;

        // The media type a body goes out as. The only one the gate sent.
        internal const string JsonMediaType = "application/json";

        // Build the request. Pure, so the header set is testable without a socket.
        //
        // Method defaults to GET and body to none, so a read built through here
        // is exactly the request it was before writes existed — the read tests
        // pin that rather than trusting it.
        internal static HttpRequestMessage Build(string token, string path,
            HttpMethod? method = null, string? body = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, Host + path);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation(VersionHeader, VersionValue);

            // No cookie, ever. claude.ai's observation of this roster rode a
            // session cookie; ours rides the CLI's OAuth token and nothing else.
            // Sending both would make it impossible to tell which one was
            // accepted, which is precisely the question this arm exists to answer.
            //
            // No body either, and no content-type. The empty ByteArrayContent
            // that used to be attached was there to reproduce the browser's GET
            // exactly; api.anthropic.com neither needs nor notices it, and a GET
            // with a body is the sort of thing an intermediary is entitled to
            // object to.
            //
            // A write carries JSON, and Content-Type rides with the body and only
            // with it. **It is not optional there:** the gate's POSTs all carried
            // it, and nothing was measured without it, so a write that dropped it
            // would be a request nobody has seen succeed.
            if (body is not null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, JsonMediaType);
            }

            return request;
        }
    }

    // Turning an answer into a verdict, and a verdict into a wait.
    internal static class CloudOutcomes
    {
        // **The 401 asymmetry here is measured, and it is the useful part.**
        // A request with no Authorization header at all gets "Authentication
        // failed"; a well-formed but bogus Bearer gets "OAuth access token is
        // invalid."; the real CLI token gets 200. Those three together are what
        // make it a measurement rather than a single result — and the same token
        // answering 403 on `/v1/organizations/me` is the fourth control, showing
        // the token is real and merely scoped away from that endpoint.
        //
        // For us it is the difference between "our token was rejected" — stop,
        // re-read the credential, tell the user to sign in — and "we did not
        // present a credential the endpoint understood at all", which is a bug on
        // our side. Both stop; they say different things.
        internal const string TokenRefusedMarker = "OAuth access token is invalid.";

        // What a 403 says when it came from the API itself, and what it says when
        // something in front of the API never let the request through.
        //
        // **The distinction exists because getting it wrong cost this ticket an
        // afternoon.** The detail here used to read "this account may not list
        // cloud sessions", which is a confident statement about an account's
        // permissions — and what had actually happened was a Cloudflare challenge
        // in front of claude.ai, refusing a non-browser client and saying nothing
        // about the account at all. A wrong explanation that reads as a finding is
        // worse than no explanation, because it ends the inquiry.
        internal const string EdgeBlockedDetail =
            "something in front of the endpoint refused the request before it reached the API";

        internal const string AccountBlockedDetail =
            "the API refused this request for this account";

        // The two write refusals, worded for what is actually known.
        //
        // The 409 sentence used to name the CLI as the source of its meaning,
        // because a reading was all there was. CB-225 measured it — the API's
        // own words are "is not active", on a session that had been archived —
        // so it now says what the endpoint said.
        internal const string SessionInactiveDetail =
            "the endpoint answered 409: this session is no longer active";

        internal const string SessionGoneDetail = "this cloud session no longer exists";

        internal const string TooLargeDetail =
            "the endpoint answered 413: the message was larger than it accepts";

        // Was this 403 an edge block rather than an API decision?
        //
        // Three signals, any of which is enough, and the argument for each:
        //
        //  * **A `cf-mitigated` response header.** Cloudflare's own marker. The
        //    caller passes it because it is a header, not a body.
        //  * **A body that is not JSON**, or is JSON that is not an object. The
        //    API answers every error with a JSON error object; a challenge page is
        //    HTML, and an empty body is nobody's error format.
        //  * **A JSON error with no `request_id`.** Every real refusal measured on
        //    this API carried one. A body shaped like an error but with no id
        //    never reached anything that assigns ids.
        //
        // Deliberately biased towards "edge": the failure being guarded against is
        // asserting something about the *account* on evidence that is about the
        // network path, and the reverse mistake — saying "something in front of
        // the endpoint" about a genuine permission error — misleads nobody into a
        // wrong fix.
        internal static bool LooksLikeEdgeBlock(string? body, bool cfMitigated = false)
        {
            if (cfMitigated) return true;
            if (string.IsNullOrWhiteSpace(body)) return true;

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;

                return !(doc.RootElement.TryGetProperty("request_id", out var id)
                         && id.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(id.GetString()));
            }
            catch (JsonException)
            {
                return true;
            }
        }

        // The handler's own word for "no such session". Matched on the parsed
        // error type, never on the message, which names the session id.
        internal const string NotFoundErrorType = "not_found_error";

        // Whether a body is the handler's "no such session". Anything that is
        // not a JSON error object of exactly that type — the router's plain
        // text, an empty body, some other error — is not.
        internal static bool IsSessionNotFoundError(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;

            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty("error", out var error)
                       && error.ValueKind == JsonValueKind.Object
                       && error.TryGetProperty("type", out var type)
                       && type.ValueKind == JsonValueKind.String
                       && type.GetString() == NotFoundErrorType;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static CloudOutcome OutcomeFor(int status, string? body,
            TimeSpan? retryAfter = null, bool cfMitigated = false)
        {
            if (status is >= 200 and < 300)
            {
                return new CloudOutcome(CloudOutcomeKind.Ok, status);
            }

            switch (status)
            {
                case 400:
                    return new CloudOutcome(CloudOutcomeKind.ShapeChanged, status, null,
                        "the endpoint refused the request shape");

                case 401:
                    return body is not null
                           && body.Contains(TokenRefusedMarker, StringComparison.Ordinal)
                        ? new CloudOutcome(CloudOutcomeKind.TokenRefused, status, null,
                            "the stored Claude Code login was not accepted")
                        : new CloudOutcome(CloudOutcomeKind.AuthFailed, status, null,
                            "no credential the endpoint recognised");

                case 403:
                    return new CloudOutcome(CloudOutcomeKind.Blocked, status, null,
                        LooksLikeEdgeBlock(body, cfMitigated)
                            ? EdgeBlockedDetail
                            : AccountBlockedDetail);

                case 404:
                    return new CloudOutcome(CloudOutcomeKind.SessionGone, status, null,
                        SessionGoneDetail, IsSessionNotFoundError(body));

                case 409:
                    return new CloudOutcome(CloudOutcomeKind.SessionInactive, status, null,
                        SessionInactiveDetail);

                case 413:
                    return new CloudOutcome(CloudOutcomeKind.TooLarge, status, null,
                        TooLargeDetail);

                case 429:
                    return new CloudOutcome(CloudOutcomeKind.RateLimited, status, retryAfter,
                        "the endpoint is rate limiting us");

                default:
                    return new CloudOutcome(CloudOutcomeKind.Unavailable, status, retryAfter,
                        $"the endpoint answered {status}");
            }
        }
    }

    // How long to wait before asking again, or whether to stop asking.
    //
    // Null means **stop**, and it is a real answer rather than an absence. CB-164
    // requires that a refused or absent credential produces no cloud orbs, a
    // legible reason and no repeated OS prompt; a backoff that merely got very
    // long would still eventually prompt again, so the stop has to be a distinct
    // value the caller cannot mistake for a long sleep.
    internal static class Backoff
    {
        // What a 429 waits at minimum, whatever Retry-After says. A server that
        // asks us back in one second while rate limiting us is asking for a loop,
        // and this endpoint is one we are a guest on.
        internal static readonly TimeSpan RateLimitFloor = TimeSpan.FromSeconds(60);

        // Where a retryable failure starts, and where it stops growing.
        internal static readonly TimeSpan UnavailableFloor = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

        internal static TimeSpan? Next(CloudOutcome outcome, TimeSpan? previous)
        {
            switch (outcome.Kind)
            {
                // Nothing to wait for.
                case CloudOutcomeKind.Ok:
                    return null;

                // A credential problem, or a decision about this account. Waiting
                // longer changes none of them.
                case CloudOutcomeKind.TokenRefused:
                case CloudOutcomeKind.AuthFailed:
                case CloudOutcomeKind.Blocked:
                    return null;

                // A session that has been deleted is not coming back, and
                // asking again on a schedule would be a retry loop against a
                // measured answer. Before CB-199 a 404 fell to Unavailable and
                // was retried with exponential backoff, which was wrong for this.
                case CloudOutcomeKind.SessionGone:
                    return null;

                case CloudOutcomeKind.RateLimited:
                    var asked = outcome.RetryAfter ?? RateLimitFloor;
                    return asked < RateLimitFloor ? RateLimitFloor : asked;

                // SessionInactive and TooLarge are about one request, not about
                // the arm. The roster's direct session reads treat any outcome
                // this returns null for as a reason to halt the whole arm (see
                // ClaudeCloudSessions.RefreshAsync), and before CB-199 a 409 or a
                // 413 on a read fell through to Unavailable — so they stay in the
                // retryable group, and a read keeps behaving exactly as it did.
                // A write does not come through here at all: its single retry is
                // decided by the send path, on Unavailable only.
                case CloudOutcomeKind.SessionInactive:
                case CloudOutcomeKind.TooLarge:

                // ShapeChanged joins Unavailable rather than stopping: a 400 after
                // shipping most likely means the contract moved, and a deploy in
                // progress looks exactly the same for a few minutes. Backing off
                // to the cap and staying there is quiet enough, and it recovers on
                // its own where a stop would need a restart.
                case CloudOutcomeKind.ShapeChanged:
                case CloudOutcomeKind.Unavailable:
                default:
                    if (previous is not { } last || last < UnavailableFloor)
                    {
                        return UnavailableFloor;
                    }

                    var doubled = last + last;
                    return doubled > Cap ? Cap : doubled;
            }
        }

        // The credential-side half of the same rule, so a caller that never gets
        // as far as an HTTP request still has one place to ask.
        internal static TimeSpan? Next(CredentialOutcome outcome, TimeSpan? previous)
        {
            switch (outcome)
            {
                case CredentialOutcome.Found:
                    return null;

                // Denied is the user saying no, and NotLoggedIn is there being
                // nothing to read. Retrying either means prompting again, which
                // CB-164 rules out in as many words.
                //
                // NoAnswer joins them, and it is the one that would look most like
                // a candidate for a retry: the store did not say no, it said
                // nothing. It stops anyway, for two independent reasons. A call
                // that may be blocked waiting on a human is the definition of what
                // must not be re-issued on a timer — that is a queue of consent
                // prompts. And each attempt abandons a thread inside a P/Invoke
                // that cannot be cancelled, so a backoff loop leaks one per tick
                // for as long as the app runs.
                case CredentialOutcome.Denied:
                case CredentialOutcome.NotLoggedIn:
                case CredentialOutcome.NoAnswer:
                    return null;

                // CannotPrompt is a failure with no dialog behind it: it fails
                // instantly, costs nothing to retry, and clears the moment someone
                // is at the screen — so it backs off like any transient failure.
                case CredentialOutcome.CannotPrompt:
                case CredentialOutcome.Unreadable:
                case CredentialOutcome.Malformed:
                default:
                    if (previous is not { } last || last < UnavailableFloor)
                    {
                        return UnavailableFloor;
                    }

                    var doubled = last + last;
                    return doubled > Cap ? Cap : doubled;
            }
        }
    }

    // The call, as an interface, so everything above it can be driven without a
    // network. Same argument as IUsageSource and ICloudCredentialSource.
    //
    // **One method, not one per endpoint.** There are four paths now — the
    // listing, a single session, a session's events, and the write endpoint —
    // and they differ only in what the context carries. A method each would be
    // four identical bodies and five fakes to keep in step. It was `GetAsync`
    // until CB-199 gave the context a method and a body; the rename is so a
    // POST does not go out through a method whose name says it cannot.
    internal interface ICloudApi
    {
        Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token);
    }

    // The real one.
    //
    // Excluded from coverage: it is an HttpClient talking to api.anthropic.com,
    // and the repository's rule is that tests never touch the real network.
    // Everything it decides — the header set, the status mapping, the wait — is in
    // the pure classes above and is covered there. What is left here is the
    // socket, and a test of that would be a test of HttpClient.
    [ExcludeFromCodeCoverage]
    internal sealed class HttpCloudApi : ICloudApi, IDisposable
    {
        // One client, reused, as .NET wants. 30 seconds because this is a roster
        // fetch behind an ambient overlay: nobody is waiting on it, and a request
        // still outstanding after half a minute is not going to help the user.
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        // What a caller is handed: the body on a 2xx, and on anything else
        // only the verdict. An error body is unvetted text — it can echo ids,
        // and nothing here has an allow-list for it — so it stops at the
        // wrapper, and whatever a caller needs from it is decided in OutcomeFor
        // and carried on the outcome (SessionNotFound is the case CB-225 found).
        //
        // Pulled out of SendAsync, which is excluded from coverage, so the rule
        // is testable, and so a test's fake can apply the same rule instead of
        // a friendlier one: CB-225's first fakes handed every body back, and
        // the bug that hid — every delete reading as unconfirmed — was only
        // found by reading this line.
        internal static CloudApiResult ResultFor(CloudOutcome outcome, string? body) =>
            new(outcome, outcome.Kind == CloudOutcomeKind.Ok ? body : null);

        public async Task<CloudApiResult> SendAsync(CloudRequestContext context,
            CancellationToken token)
        {
            try
            {
                using var request = CloudRequest.Build(context.AccessToken, context.Path,
                    context.Method, context.Body);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var retryAfter = response.Headers.RetryAfter?.Delta;

                // Read off the response rather than inferred from the body, which
                // is the whole reason LooksLikeEdgeBlock takes it as an argument.
                var mitigated = response.Headers.Contains("cf-mitigated");

                var outcome = CloudOutcomes.OutcomeFor(
                    (int)response.StatusCode, body, retryAfter, mitigated);

                return ResultFor(outcome, body);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new CloudApiResult(
                    new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, "the request timed out"),
                    null);
            }
            catch (HttpRequestException ex)
            {
                return new CloudApiResult(
                    new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, ex.Message),
                    null);
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
