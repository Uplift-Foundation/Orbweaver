# Claude Code cloud sessions — credential and API findings

Everything here was measured during CB-164 unless it says otherwise, against the API as it stood on **19 September 2026**, from a real Mac running Claude Code **2.1.278**. Where something is assumed rather than observed, it says so, and the "Not established" section at the end is the part worth reading first.

**This describes an undocumented API.** Nothing about it is supported and all of it can change without notice. CB-164 accepted that cost deliberately; this document exists so the next person can tell a shape change from a bug.

## The correction this document exists because of

**An earlier version of this file described the wrong host throughout, and every measurement in it was real.** It documented `GET https://claude.ai/v1/code/sessions` with a six-header set the endpoint supposedly 400s without, an auth asymmetry established with three negative controls, and a load-bearing open question about whether the CLI's own token would be accepted. The requests were made, the responses were recorded, the controls were run. The host was wrong, and being wrong about the host made every conclusion above it wrong too.

What claude.ai actually does to a non-browser client is answer with a Cloudflare challenge. That challenge is **identical for a real token and a bogus one**, which is precisely why it was expensive: it reads as an authentication failure, so an afternoon went into the credential and none into the host. The six headers were claude.ai's web client's, reproduced faithfully and required by nothing. The "is the CLI token accepted at this audience" question was unanswerable there by construction, because no credential of any kind gets a different answer from a challenge page.

Against `api.anthropic.com` the same credential returns 200 with two headers. The lesson is the one CLAUDE.md already writes down twice: **rigour inside a wrong frame produces confident error, and it arrives with receipts.** Every control was sound and about the wrong thing. When a conclusion says something is impossible — "this token is not accepted here" — the question to ask is not whether the evidence is sound but *what else knows about this*.

The second-order lesson is in `tests/UnitTests/ClaudeCloudRequestTests.cs`, which pinned the six headers **twice**: once off a built request and once against the constants, deliberately, so a refactor changing one would fail the other. Two independent copies of a wrong measurement are still one wrong measurement, and the doubling made it look doubly confirmed.

## The short version

1. There is **no supported client-side route**. `claude agents --json` returns local sessions only, the CLI control protocol has no cloud-session subtype, and the shipped CLI binary contains no listing endpoint.
2. `GET https://api.anthropic.com/v2/ccr-sessions` lists them, with the Claude Code CLI's own OAuth token and **two** headers.
3. **The roster is overwhelmingly not cloud sessions.** 573 of 578 rows are the user's own local sessions, registered for remote control. The filter is the feature.
4. **There is no server-side filtering.** Five different query parameters were sent and all five were *silently ignored*.
5. Per-session reads and a per-session transcript exist, and the transcript is Claude Code's own format.
6. **Writing works, on a different prefix** (CB-199): `POST /v1/code/sessions/<id>/events` with the same token and the same two headers plus `Content-Type`. Reads stay on `/v2`.

## The endpoint

```
GET https://api.anthropic.com/v2/ccr-sessions
Authorization: Bearer <the Claude Code CLI's OAuth token>
anthropic-version: 2023-06-01
→ 200
```

**Those two headers, and nothing else.** `anthropic-beta`, `anthropic-client-feature`, `anthropic-client-platform` and `x-organization-uuid` are all unnecessary here; they were carried over from claude.ai. No cookie is ever sent, and no body — the empty `application/json` body the old builder attached was there to reproduce a browser's GET byte for byte against the wrong host.

Response envelope is `{ data, first_id, has_more, last_id }`, paged forwards with `?after_id=<last_id>`.

| checked | result |
| --- | --- |
| `limit=100` | 200 |
| `limit=200` | **400** — `must be greater than or equal to 0 and less than 101` |
| `environment_kind=anthropic_cloud` | 200, **unfiltered** — the parameter is ignored |
| `session_status=`, `status_bucket=`, `exclude_archived=`, `order=` | same: 200, ignored, no error |
| ordering | `created_at` DESC; **`updated_at` is unsorted** |
| one page | ≈0.6s |

**A silently-ignored query parameter is the worst kind of failure**, because it reads as working: the request succeeds, the page comes back, and only counting the rows tells you the filter did nothing. That is why `ClaudeCloudRoster.Keep` is client-side and why nothing in this arm puts a filter in a query string.

**`updated_at` being unsorted is the reason the fetch strategy has two halves.** A cloud session created last month that resumes today does not move to the front of a `created_at` DESC roster, so page one alone would show it frozen at whatever the last full walk saw.

## Per-session

The control is a nonsense subpath, which 404s — so the 200s below are real answers rather than a permissive router.

| path | result |
| --- | --- |
| `GET /v2/ccr-sessions/<id>` | 200 — a single session object, same row shape as a listing row |
| `GET /v2/ccr-sessions/<id>/events` | 200 — **Claude Code's own transcript format**, same `{data, has_more, last_id}` paging |
| `/messages`, `/history`, `/transcript`, `/turns`, `/conversation`, `/input`, `/state`, `/logs` | 404 |

The events rows carry `type` (`user`/`assistant`/`system`/`result`/`control_request`/…), a `message` with a `role` and content blocks of `text`/`thinking`/`tool_use`/`tool_result`, plus `usage`, `model`, `stop_reason`, `uuid` and `parent_tool_use_id`. That is what `ChatTranscript` already reads, so `ClaudeCloudEvents` parses the envelope and hands the rows to that parser rather than writing a second one — two parsers over one format is how a panel comes to show something a terminal does not.

**There is no input route on `/v2`, and CB-164 was wrong to conclude from that that there is none at all.** Every plausible write path under `/v2/ccr-sessions` 404s — that measurement stands. What it did not ask is where the CLI itself writes, and the answer is a different prefix: `POST /v1/code/sessions/<id>/events` on the same host. `/v2/ccr-sessions` is the in-container ingress; `/v1/code` is the client-facing API. See **Writing (CB-199)** below. It is the "what else knows about this" failure the correction at the top of this file warns about, one prefix over: every control was sound and the frame was one path too narrow.

## Writing (CB-199)

Measured 2026-09-28 from a real Mac against a throwaway session made with `claude --cloud` for the purpose, never a real one, with `tools/claude-cloud-probe`. The request shapes were read out of the Claude Code CLI 2.1.284 binary as strings; the binary was never executed for this.

### Measured

**The write.** `POST https://api.anthropic.com/v1/code/sessions/<id>/events` with a body of `{"events":[{"payload":{uuid, session_id, type:"user", parent_tool_use_id:null, message:{role:"user", content}}}]}`.

| request | result |
| --- | --- |
| real token, `Authorization: Bearer` + `anthropic-version` + `Content-Type` only | 200, `results[0]` = `{duplicate:false, sequence_num, event_id}`, with `event_id` equal to the uuid we sent. `sequence_num` is a string |
| the same event again, same uuid | 200, `duplicate:true` — so a retry with the same uuid is safe |
| no `anthropic-version` | 400 |
| no `Authorization` | 401, "Authentication failed" |
| bogus Bearer | 401, "OAuth access token is invalid." |
| `{"events":[]}` with a real token | 400 |
| `{"events":[]}` with a bogus token | 400 — identical, so an empty POST **cannot** serve as a pre-flight for "may this login write"; the first real send is what finds out |

No beta header and no `x-organization-uuid` are needed for either the send or the interrupt, so the app has no organisation-uuid reader to grow.

**Where the sent turn lands.** It appears in the `/v2/ccr-sessions/<id>/events` history as one `user` row carrying our uuid about 10 s after the send. At 3 s it was not there yet. So history stays on `/v2`, and the panel reconciles its own bubble against the echo by uuid rather than drawing a second one.

**Sending while a turn is running** → 200, queued; it lands after the current turn. So a busy session is sendable.

**The interrupt.** The same endpoint, event `{type:"control_request", request_id, request:{subtype:"interrupt", cancel_queued:true}, uuid}`. Mid-turn the session went running → idle within about 1 s, and the interrupted turn ended with result `error_during_execution`; the session's earlier turns ended `success`, which is the control that says the result is the interrupt's doing. Sent while idle it is harmless.

**Turn state, read two ways.**

| source | mid-turn | idle |
| --- | --- | --- |
| `/v2` row | `session_status` "running", `status_bucket` "working", no `worker_status` field | `session_status` "idle", `status_bucket` "blocked" |
| `/v1/code/sessions/<id>` | `status_bucket` "working", `worker_status` "running" **or** "WORKER_STATUS_UNSPECIFIED" | — |

So busy is `status_bucket == "working"`. `worker_status` is not a reliable busy signal even where it exists.

**A deleted session.** `GET /v1/code/sessions/<id>` answers 404 `not_found_error`, and `GET /v2/ccr-sessions/<id>/events` answers 404 too.

**Rate.** Ten back-to-back `/v2` reads drew no 429. That is ten reads on one afternoon, not a statement about a day's polling.

### Not measured

- ~~**409 `session_inactive` for an archived session.**~~ **Measured by CB-225** (below): 409, and the error type is `session_not_active` — the binary's `session_inactive` was its own name for the case, not the wire string.
- **413** (too large). Also from the binary only.
- **A device-bound 403.** The CLI re-sends with `device_attestation`, which Buddy cannot do. No session that demands it was available, so the 403 split stays two-way (edge block / account refusal).
- **Sending while the session is waiting on a permission prompt** (`requires_action`).
- **Sending to a deleted session.** The probe's own guard refused to send to anything but its throwaway session, and the throwaway was not deleted before the send was tried.

### Which login a send uses (CB-221)

A cloud session is listed under the account whose login found it, and a send goes out as that account — never as whichever login happens to be current. Every Claude Code account directory the user has listed is read (PR #121).

On the Mac this was measured on, those logins turned out to live in the plaintext `<config dir>/.credentials.json` rather than in the Keychain. The hypothesis is that the CLI's own Keychain writes fail when it runs over SSH and it falls back to the file; that is consistent with the Keychain entries' last-written dates, which predate the file's by weeks, and it is not demonstrated. Either way the reader has to look in both places, which it does.

## Archive and delete (CB-225)

Measured 2026-10-02 on the MacBook against three throwaway sessions made with `claude --cloud` (interactive only — `--cloud` refuses `--print`) on the default account, every one deleted afterwards, with `tools/claude-cloud-probe`'s `archive`, `delete`, `route` and `v2-session` verbs. `lifecycle.sh` is the sequence. No real session was archived or deleted. The routes were read out of the Claude Code CLI 2.1.288 binary as strings; the binary was not run against the API.

### What the CLI does, read from the binary

The fleet view's `archiveRemote` calls `archiveRemoteSession`, which posts `{}` to `/v1/code/sessions/<id>/archive` with the same header set as a send (User-Agent, Bearer, `anthropic-version`, and `X-Trusted-Device-Token` only when device identity is on), and counts **200 or 409** as archived. **The CLI has no delete.** Every `/v1/code/sessions/<id>/…` path in the binary was enumerated and none is sent with DELETE or PATCH; its only DELETE anywhere is `/v1/environments/bridge/<id>`. `PUT /v1/code/sessions/<id>` with `{title}` exists and is a rename.

### Measured

| request | result |
| --- | --- |
| `POST …/archive`, body `{}`, real token | **200**, `{"session":{… "status":"archived" …}}` — the whole session object |
| the same archive again | **200** again, not 409. Idempotent in practice; the CLI's tolerance of 409 was never exercised |
| archive with no `anthropic-version` | 400, "anthropic-version: header is required" |
| archive with a bogus Bearer | 401, "OAuth access token is invalid." |
| archive with no `Authorization` | 401, "Authentication failed" |
| after archive, `GET /v1/code/sessions/<id>` | 200, `status` "archived", `connection_status` "disconnected" |
| after archive, the `/v2/ccr-sessions/<id>` row | 200, `session_status` **"archived"**, `connection_status` "disconnected" |
| after archive, the roster | the session is gone from the app's own roster count (3 → 2 on that account): `ClaudeCloudRoster.Keep`'s archived filter drops it with no change |
| after archive, `/v2/…/events` | 200 — the history stays readable |
| after archive, `POST …/events` (a send) | **409**, error type `session_not_active`, "Session <id> is not active" |
| `DELETE /v1/code/sessions/<id>` on an archived session | **200**, body `{}` |
| `DELETE` on an active, never-archived session | **200** — no archive needed first |
| `DELETE` with no `anthropic-version` / bogus Bearer / no `Authorization` | 400 / 401 OAuth / 401 "Authentication failed", as for archive |
| after delete, `GET /v1/code/sessions/<id>` | 404 JSON, `not_found_error`, "Session <id> not found" |
| after delete, `/v2/…/events` | 404 → the shipped `SessionGone` |
| the confirming `GET` made the instant `DELETE` returned, through the app's own `RunAsync` and `HttpCloudApi` (`lifecycle-run`) | 404 `not_found_error`, both times it was run (+347 ms and +259 ms from the start of the exchange, no delay between the two). So no read-after-delete lag was seen — a two-sample observation, not a property, and the app does not depend on it: the 2xx DELETE is the success, and a read that still finds the session only changes the row to "Deleted — not yet confirmed" |

**The controls that make the two routes real.** A nonsense sub-route (`…/<id>/cb225-no-such-route`) on the same live id with the same token answers **404 `text/plain` "404 page not found"** — the router. Archive and DELETE aimed at an id whose GET has just answered 404 answer **404 JSON `not_found_error`** — a handler. So both routes exist as handlers, and a 404 from either comes in two kinds that mean different things: a JSON `not_found_error` is "no such session", and a plain-text 404 is "no such route". `CloudOutcomes.OutcomeFor` cannot tell those apart and reads both as `SessionGone`, which is why the lifecycle code (`ClaudeCloudLifecycle`) does not use a 404 as evidence of success.

**What the app is handed is not what the endpoint sent.** HttpCloudApi returns a body only on a 2xx (`HttpCloudApi.ResultFor`), so the handler-or-router question has to be answered where the body is still in hand — `OutcomeFor` records it as `CloudOutcome.SessionNotFound` — and carried on the outcome. The first version read the body in the lifecycle code, every 404 arrived there as null, and every successful delete said "couldn't confirm"; `lifecycle-run`, which drives the shipped code through the shipped client, reproduced that before the fix and showed "Deleted" after it.

An invented id is not a usable control: the API validates tagged ids before looking them up and answers **400** "invalid session ID: must be a cse_… or session_… tagged ID". The probe's `--absent-id` therefore accepts only an id whose GET answered 404 — a real id that has been deleted.

### Not measured

- **Another account's token on this id.** The board account's Keychain entry did not answer from the shell the probe ran in, so the cross-account refusal (403 or 404?) is not known.
- **A device-bound 403.** The throwaways succeeded with no `X-Trusted-Device-Token`; a session that demands one was not available.
- **Archive or delete while a turn is running.**
- **How long a deleted session's id keeps answering 404**, or whether a deleted id can ever be reused.
- **That DELETE stays.** It is the one route here the CLI does not itself call, so it is the likeliest to move. A route that moves answers the router's plain-text 404, which the app shows as a refusal rather than a success.

## The roster, measured across 578 rows

| `environment_kind` | rows | what they are |
| --- | --- | --- |
| `bridge` | 573 | the user's **own local sessions**, registered for remote control |
| `anthropic_cloud` | 5 | genuinely running in Anthropic's cloud; 4 archived, 1 live |

**Claude Buddy already draws an orb for every one of those 573 from its hooks.** Drawing them again would double every local orb on the screen, and the doubling would read as a layout bug rather than as a filter bug. So the filter is `environment_kind == "anthropic_cloud" && session_status != "archived"`, and `tests/IntegrationTests/ClaudeCloudRosterPayloadTests` walks a fixture in exactly those proportions to one orb, with the bridge rows as the negative control.

**Two fields are empty on all 578 rows:** `session_context.cwd` and `session_url`. So the link is built — `https://claude.ai/code/<id>` — which puts a payload-supplied string into a URL that on macOS reaches `open`. `ClaudeCloudRoster.UrlFor` therefore checks the id against an allow-list of the characters an id is made of, plus the `session_` prefix, before it can get that far. An allow-list rather than a deny-list, because a deny-list is a promise about every character somebody might one day put in a payload.

Fields worth reading: `id`, `title`, `session_status`, `status_bucket`, `updated_at`, `external_metadata.context_usage {max_tokens, used_tokens}`, `external_metadata.model`, and `external_metadata.post_turn_summary {status_detail, recent_action, needs_action, …}`.

**`post_turn_summary` is a dict on cloud rows and `null` on bridge rows.** That asymmetry is ordinary rather than a shape change, and it is the field most likely to trip a parser that assumes a shape from one example.

`LastActivity` on the orb row is the session's own `updated_at`, never the time of the read, and an unparseable one becomes `MinValue` rather than now — stamping now would give every session the account has ever had a permanent orb, because the recency window would be measuring a time this app invented.

## The auth measurement

| sent | status | body |
| --- | --- | --- |
| no `Authorization` header | 401 | `Authentication failed` |
| well-formed but bogus `Bearer` | 401 | `OAuth access token is invalid.` |
| **the real CLI token** | **200** | the roster |
| the real CLI token, at `/v1/organizations/me` | 403 | — |

The first two are the negative controls; the fourth is the one that makes the third mean something, since a token that is refused *somewhere* is demonstrably a real token being scoped rather than a string that happens to be accepted everywhere.

`CloudOutcomes.OutcomeFor` turns the 401 asymmetry into the difference between `TokenRefused` — our token was read and rejected, stop and tell the user to sign in — and `AuthFailed`, which means we presented nothing recognisable as a credential and is our bug rather than theirs.

### The 403, and the detail that used to be a misdiagnosis

`OutcomeFor`'s 403 arm used to say **"this account may not list cloud sessions"** for every 403. That is a confident claim about an account's permissions, and what had actually produced it was the Cloudflare challenge in front of claude.ai — which says nothing whatsoever about the account. A wrong explanation that reads as a finding is worse than no explanation, because it ends the inquiry.

`CloudOutcomes.LooksLikeEdgeBlock` now separates the two. Three signals, any one of which is enough: a `cf-mitigated` response header, a body that is not a JSON object, or a JSON error carrying no `request_id`. It is deliberately biased towards "edge", because the failure being guarded against is asserting something about the account on evidence that is about the network path, and the reverse mistake misleads nobody into a wrong fix.

## The fetch strategy

Two cycles, and both intervals are placeholders that say so.

- **The deep walk** pages the whole roster, following `last_id` until `has_more` is false, capped at `CloudRequest.MaxPagesPerWalk` (10 — the measured roster is 6 pages, so this is headroom). When the cap is hit, what was found is kept, the reduction is marked truncated, and `Describe` says so in the status text. **A cap that silently showed fewer sessions would be the same class of bug as the filter getting it wrong, and much harder to notice.**
- **The short cycle** reads page one plus one `GET /v2/ccr-sessions/<id>` per cloud session already known. This is what stops an older cloud session resuming from being invisible until the next walk, and it exists because `updated_at` is unsorted.

A session the short cycle never asked about survives untouched; one it got a definitive answer about and which is no longer kept has genuinely ended and loses its orb. A per-session read that merely *failed* is treated as **no news** rather than as a death — including a 404, since "ended" rather than "briefly unreachable" is not something anybody has measured — and the next deep walk is the authority that will drop it.

**A mid-walk failure never publishes an empty snapshot over a good one.** `StepResult.Snapshot` is nullable and null means "leave what is on screen alone": orbs vanishing because a request timed out would read as sessions having ended, which is a lie the app would be telling on the strength of a network hiccup. A *definite* refusal — the token was rejected, we are blocked — does clear them, because that is a real statement that there is no access.

## Where the credential lives

| platform | store |
| --- | --- |
| macOS | login Keychain, generic password, service `Claude Code-credentials` |
| Windows, Linux | `~/.claude/.credentials.json` |

Note the file is **inside** the config root, where `UsageAccounts.AccountFilePath`'s `.claude.json` is a **sibling** of it. Two neighbouring files with different rules; reasoning from one to the other is a trap that has already cost this repository once.

The stored blob's shape, as parsed: `claudeAiOauth.accessToken` and `claudeAiOauth.expiresAt` (Unix milliseconds, with an ISO string accepted as a fallback). It also carries a refresh token, which this code deliberately does not read — `CredentialRead` has no field one could land in.

`ClaudeCliCredentials.OrganizationUuidFrom` survives, but **nothing sends what it returns**. It was there because `x-organization-uuid` was believed mandatory; it is diagnostic now, and `tools/claude-cloud-probe` prints only whether one was found.

### The attributes-only query is prompt-free and the data query is not, and the difference is load-bearing

**Measured on a real Mac, and this is the finding that changed the code rather than only the documentation.** In a context with no window server session, `SecItemCopyMatching` with `kSecReturnData` — the query that reads the secret — **does not return**. Killed at 30 seconds and at 60, on two separately built binaries, so it is not code identity. The attributes-only query (`kSecReturnAttributes`, what `Stamp()` uses) answers instantly in the same context, every time.

Earlier the same day the data query had succeeded from the same kind of context, returning in about a second. Between that success and the hang, the item's modification stamp moved — 811497348 → 811525914 — which is the CLI having refreshed its token and rewritten the item.

**The mechanism is not established.** A rewrite resetting the item's ACL fits the timing. So does "the earlier success rode a grant that has since lapsed". Nobody has told the two apart, and nothing in the code is written as though the question were settled. What *is* established: it blocks, it is reproducible, and it is the secret read that it happens to.

The consequence is what mattered, and it was the same under either story. `MacOSKeychain.OutcomeForStatus` maps `errSecInteractionNotAllowed` (−25308) to `Denied`, which is the right mapping for "wanted to prompt, could not" — **when it arrives**. Here it never arrives, so no OSStatus is produced and no branch runs. Called on the supervisor's own thread, that parked `RunAsync` forever: the status stayed on "checking…", there were no orbs and no error, and the whole thing was indistinguishable from an account with no cloud sessions. Silent failure that looks like a working empty state is the worst shape in this repository.

`ClaudeCliCredentials.ReadWithinAsync` is the fix. It runs the read on a borrowed thread, waits `UnmeasuredReadBudget` (45s), and reports a new `CredentialOutcome.NoAnswer` when the call does not come back. Four things about it are deliberate:

- **`NoAnswer` is named for the observation, not a mechanism.** `Unavailable` was rejected because it reads as retryable; `NoPrompt` because it asserts the unproven cause.
- **It stops the arm rather than backing off**, exactly as `Denied` does, and comes back only when `Stamp()` moves or the user asks again. Two independent reasons: a call that may be waiting on a human is the definition of what must not be re-issued on a timer, and each attempt abandons a thread that cannot be recovered.
- **The abandoned thread is leaked and that is written down rather than claimed away.** A blocked P/Invoke cannot be cancelled. There is no way to make the hung call stop; there is only a way to stop waiting for it.
- **The budget is generous rather than snappy.** The only thing a longer wait waits for is a person reading a consent dialog, and cutting one of those off costs them a round trip through the settings window. 45 seconds is unmeasured; what would settle it is how long people actually take to answer an unexpected Keychain dialog, and whether the hang has any upper bound at all.

There is a plausible fix one level lower — `kSecUseAuthenticationUI` set to `kSecUseAuthenticationUIFail` forces the call to return `errSecInteractionNotAllowed` instead of waiting for a UI it cannot show — and it is **deliberately not applied**, because it would suppress the consent prompt in the windowed case too, and that prompt is the entire user grant. Applying it only when there is no window server session would need the mechanism proven first.

**One product consequence:** a CLI token refresh may re-prompt, so the settings copy cannot promise "Always Allow once and you are done".

**The Keychain is read by P/Invoke into Security.framework, never by shelling out to `security find-generic-password`.** The macOS consent dialog names the calling binary. Going through the command-line tool would put `security` on that screen, and an "Always Allow" answered there would grant `/usr/bin/security` — every script on the machine — instead of Claude Buddy. CB-164 rejected minting our own OAuth token precisely because the consent screen would have named the wrong application; a helper binary is the same mistake with a broader blast radius.

## Token custody, and one honest limit

The rules the code is written to, stated so a later change can be checked against them:

- The access token is a local everywhere it appears — never a static, never a field, never cached, never in anything that outlives the request it authorises.
- `ParseCredentials` reads two fields and no others.
- Nothing token-derived reaches `Detail`, `Describe`, a status string, a log or the disk. Negative-control tests feed a distinctive canary token through every outcome, every status string the arm produces, and the largest payload fixture in the repository, and assert it appears nowhere.
- `Stamp()` returns a change detector — a file mtime in ticks, or a Keychain modification date — and never secret bytes. On macOS it is an attributes-only query, which returns no data and so is not the query the consent prompt guards; that is why it is a separate call from `Read()`.
- **The credential is re-read only when `Stamp()` moves**, or when the user acts. `Denied`, `NotLoggedIn` and `TokenRefused` halt the arm until then, and a halted arm sends no request and reads no secret — it calls `Stamp()` and nothing else. That is what keeps "never a repeated OS prompt" true, and it is why the halt is a distinct state rather than a very long backoff: a backoff that merely got long would still eventually prompt again.
- We never refresh. The CLI owns refresh; our copy goes stale and is re-read. Refreshing ourselves would need the refresh token and a client id, which is the option CB-164 rejected.

**The token is not zeroed and cannot be.** A .NET string is immutable and its bytes live wherever the GC last copied them until the heap is reused. This is recorded as a known limit rather than claimed away. Reducing it would mean carrying a `byte[]` or a `SecureString` through `HttpClient`, and neither survives the framework's own string copies.

## The probe

```
dotnet run --project tools/claude-cloud-probe -- stamp
dotnet run --project tools/claude-cloud-probe -- read --keys-only
dotnet run --project tools/claude-cloud-probe -- list --shape
dotnet run --project tools/claude-cloud-probe -- roster
tools/claude-cloud-probe/lifecycle.sh <throwaway id>   # CB-225: archives and DELETES it
dotnet run --project tools/claude-cloud-probe -- lifecycle-run <throwaway id> archive|delete --throwaway
```

It references the app rather than building its own request, so its answer is the app's answer rather than a second opinion. `stamp` prompts for nothing. `read` prints a length and a four-character prefix and has no mode that prints a token. `list --shape` prints field names and JSON types with every value stripped, so a fixture can be designed without a single session title. `roster` prints the `environment_kind` histogram and the status sentence — **the cheapest available check that the filter still matches something**, and much cheaper than diagnosing it from a screenshot of missing orbs.

**Every secret read in the probe is budgeted, at 10 seconds rather than the app's 45.** That is deliberate and the reason is who is waiting: the app may be waiting on a person reading a consent dialog, while a person running a diagnostic is watching a cursor blink — most likely *because* the credential read is already misbehaving. A diagnostic that hangs on the condition it diagnoses is worse than no diagnostic. This was not free: the app was fixed first and the probe was left calling the source directly at three call sites, so `roster` still hung for a hundred seconds against a Keychain the app itself handled in 45. `tests/IntegrationTests/CredentialBudgetTests` is a source-text guard against that recurring — it is the one class of bug here that no behavioural test can reach, because the probe has no behaviour a suite can drive.

`stamp` is deliberately **not** budgeted and not wrapped in anything. It is the attributes-only query, it has never been observed to hang, and it is the one thing that still answers when the data read does not — which makes it the first thing to run.

On macOS `read`, `list` and `roster` raise a Keychain consent prompt naming *this binary*, which is a separate unsigned executable — expected, and not the shipped app's grant. It is run by a human on purpose.

## Not established

**The two cadences.** `CloudRequest.UnmeasuredWalkInterval` and `UnmeasuredFirstPageInterval` are placeholders and their comments say so. CB-122 is the cautionary tale for exactly this kind of constant: a cadence defended by a comment asserting a fact about an external system that nobody had ever checked, which then ruled out the cheapest explanation for a real bug for hours. Three questions would settle them, and none has been asked:

- **Does `updated_at` advance during a running turn**, or only when a turn ends? If only at the end, a fast cadence buys nothing; if it advances continuously, the cadence is what decides whether a generating orb looks alive.
- **Does this endpoint 429 anybody at this rate over a whole day?** One page is ≈0.6s and the roster is six pages, so a walk is a handful of seconds — but rate limits are not something to infer from one afternoon's probing.
- **How often is a cloud session first seen only on a deep page?** That is the entire argument for the walk existing beside the first-page poll, and nobody has counted it.

**The page caps.** `MaxPagesPerWalk = 10` and `ClaudeCloudChatSession.MaxEventPages = 10` are round numbers with headroom over the one roster anybody has seen. Neither is measured. What *is* deliberate is that hitting the roster cap is visible in the status text rather than silent.

**The history cap.** `ClaudeCloudChatSession.HistoryTurns = 200`. Nobody has counted how long a cloud session's transcript runs.

**Rate limits or terms on calling this endpoint from a third-party desktop client.** Not investigated. `Backoff` is written to be a well-behaved guest — a 60-second floor on any 429 whatever `Retry-After` says — but that is caution, not knowledge.

**Whether every 404 on a per-session read means the session is gone.** A deleted session was measured to answer 404 on both prefixes (CB-199), and a per-session 404 is now read as gone (`CloudOutcome.SessionGone`); the roster listing, where a 404 would mean the collection moved, undoes that itself. That a 404 is never transient — an edge hiccup, a session briefly unrouted — is assumed, not shown.

**Why the Keychain data query blocks with no window server session.** Reproducible and fixed around; the cause is open. See the section above for the two stories that fit the evidence equally well, and for the lower-level fix that is not being applied until one of them is ruled out.

**Whether `kSecAttrAccount` needs constraining.** The query matches on service alone. The CLI appears to write one item under this service and the account name it uses was not measured, so pinning it would be an assumption that fails silently with `ItemNotFound` the day it is wrong.

**Whether `/v2/ccr-sessions` has a watch or stream form.** claude.ai had one; this host was not probed for an equivalent. If one exists it would make polling the wrong shape entirely, and it is the first thing to look for before tuning either interval above.

## What this file does not cover

The `SessionManager` mapping and the orb itself — the UI half of CB-164. The privacy constraint from the ticket applies there and not here: an account's cloud sessions are not all work, and a title on an always-on-top overlay is a different act from a listing in a web UI somebody opened deliberately.

## Slash commands and control requests from outside (CB-306)

Measured 2026-11-03 on this Mac against Claude Code 2.1.317 and a throwaway cloud session made with `claude --cloud` in a non-git scratch folder (so it started empty), through `tools/claude-cloud-probe`'s `send`, the new `control` and `stream` verbs, and `v1-events --save`. Never a real session. The same measurements were taken on a throwaway `claude --bg` job's Remote Control bridge; those are in `docs/remote-control-findings.md`, and the two agree except where noted below.

**A user event we POST is a peer message, not the user's typing.** `/compact`, `/help`, `/color pink` and `/rename …` sent as `ClaudeCloudSend.UserMessageBody` all answered 200 and landed, and every one was answered by the model as "The peer session sent `/x`. That's a built-in CLI command, so I can't run it from here." The `result` rows carry `origin.kind: "peer"`; the session's own creation turn carries `"human"`. Blanking `session_id` in the payload changes nothing. The CLI's own `--teleport` client does not post a slash command at all: `/color pink` typed in the teleported TUI printed "Session color set to: pink" locally and no event reached the session. So **PassThrough does not exist for a cloud session from this app**, and the plan's assumption that the bridge slash dispatcher (`resolveBridgeSlashOverride`) would see our text was wrong: that code runs only for input the CLI classifies as its Remote Control client's, and what classifies it is not in the payload. Our stored rows carry only the keys we sent plus two the server adds (`server_received_wall_ms`, `trace_context`); the envelope carries `source: client`, `sent_by_account_id`, `device_attestation_status` (`DEVICE_ATTESTATION_STATUS_UNSPECIFIED` on every row measured, ours and the worker's) and `session_agent_owned` (null), and the consumer of those fields is in a bytecode-only chunk the binary's strings cannot reach. Not established: what claude.ai's own web client sends into a session — the headless debug browser is refused by Cloudflare's interstitial, and restarting it headed would disturb the other agents sharing it.

**Control requests run.** Same endpoint, `ClaudeCloudSend.ControlRequestBody`. Measured on the cloud session, each answered by a `control_response` row carrying the request's `request_id`:

| request | response |
| --- | --- |
| `rename_session {title}` | `success`, followed by a `system/session_title_changed` row |
| `set_model {model:"default"}` | `success` |
| `set_permission_mode {mode:"default"}` | `success` |
| `set_max_thinking_tokens {max_thinking_tokens:null}` | `success` |
| `set_color {color:"pink"}` | **`error` "Unsupported control request subtype: set_color"** — the cloud worker has no colour; the bridge does |

The full subtype set the CLI's bridge handler accepts, read from the 2.1.317 binary's `[bridge:repl]` switch: `set_model, set_permission_mode, interrupt, stop_task, background_tasks, cancel_async_message, set_max_thinking_tokens, rename_session, set_color, mcp_authenticate, mcp_oauth_callback_url, mcp_reconnect, apply_flag_settings, side_question, reload_plugins`. Only the five above plus `interrupt` (CB-199) are measured.

**`system/init` is durable and replays.** `GET …/events` lists it, and `…/events/stream?from_sequence_num=0` replays every durable row from 1, init included; the worker re-publishes an init after every turn, so a late subscriber always finds one. The cloud init carried `slash_commands[101]` (the container's bundled plugins' skills plus the built-ins) and `terminal_slash_commands` = `doctor, color, focus, reload-plugins`, with `skills[68]` and `agents[6]`. That `color` is a terminal command there and `set_color` is unsupported agree with each other.

**What this settles for CB-306.** On a cloud session the only commands that can run from this app are the ones with a control-request equivalent: `/rename`, `/model`, `/permissions` (mode) and `/think` (budget) — not `/color`, not `/compact`, not a custom or skill command. Everything else is Unavailable, and the panel must say so rather than send text the model will politely refuse as a peer.
