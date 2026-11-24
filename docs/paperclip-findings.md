# Paperclip control plane — findings

What Orbweaver's Paperclip arm (CB-421) was built from, in the voice of
`docs/openclaw-findings.md`: **measured** means run against a live instance,
**read from source** means Paperclip's own code or its OpenAPI document, and
**assumed** means neither. The plan is `docs/plans/paperclip-orbs-plan.md`; this
file is the record of what its tables rest on and what the probe re-measured.

Instance: Paperclip `2026.1117.0`, `authenticated` deployment mode, on the Mac
mini, one small fixture company with four agents on the `paperclip_runner`
adapter. Probed from a MacBook on the same machine's loopback and from the LAN.
Every check below that touches a credential names the paired refusal that
would have failed had the setup been wrong.

The short version:

1. **Release 1 only reads.** Every call it makes is a `GET`, plus the two
   connect calls (`POST cli-auth/challenges`, `POST cli-auth/revoke-current`).
2. **A board key as `Authorization: Bearer` is accepted on every read the app
   makes, and the same read with no credential is refused (401).** The key is
   full board power and there is no read-only board key, which is why nothing
   in release 1 writes.
3. **The live socket refuses an app key**, so the app polls (2 s and 4 s tiers).
4. **Nothing is installed into the user's org.** A hook fires in some agent
   modes and never in the default one; the feature works from the API alone.

## Re-measured by `tools/paperclip-probe` (this ticket, read-only)

Run with a board key from `PAPERCLIP_BOARD_KEY`; every row also called with no
credential.

| call | with key | no credential | size | time |
| --- | --- | --- | --- | --- |
| `GET /api/health` | 200, `status: ok`, `deploymentMode: authenticated` | 200 (health is public) | — | — |
| `GET /api/cli-auth/me` | 200 | — | — | — |
| `GET /api/companies` | 200, one company | 401 | — | — |
| `…/agents` | 200 | 401 | 9.7 KB | 21 ms |
| `…/live-runs` | 200 `[]` | 401 | 2 B | 10 ms |
| `…/approvals?status=pending` | 200 `[]` | 401 | 2 B | 24 ms |
| `…/tools/action-requests` | 200 `{"actionRequests":[]}` | 401 | 21 B | 18 ms |
| `…/issues?status=blocked` | 200, one real blocked issue | 401 | — | — |
| `…/heartbeat-runs?limit=20` | 200 | 401 | 71 KB | 75 ms |

What the app made of that company: four agents, three reporting to one; the two
in `error` carry `Broke` marks with their one-sentence `errorReason`; one of
them is also the assignee of the blocked issue (`blockerAttention.state:
needs_attention`) and so is **waiting** and **broke** at once, as the plan says
it can be; the other two show their last run `succeeded`, but **no run has succeeded under
the native runner on this instance**: every `succeeded` row has a `processPid`
and predates the agents' move to the default runner at 03:39:16Z (the rows are
legacy `claude_local` runs), and the only runs after the move are the two
`failed` ones. The `succeeded` row sitting in `native/heartbeat-runs.json` is
one of those legacy runs, not a native success. An earlier revision of this
file said otherwise; that was wrong.
The `agents` body is 9.7 KB for four agents and carries `adapterConfig`; the app
never reads it and never logs a body.

## Connect

| fact | status |
| --- | --- |
| `POST /api/cli-auth/challenges` with `{"command":"Orbweaver","clientName":"Orbweaver","requestedAccess":"board"}` returns `id`, `token`, `boardApiToken`, `approvalUrl`, `pollPath`, `expiresAt`, `suggestedPollIntervalMs: 1000` | measured by the lead |
| the `boardApiToken` is 401 before approval, 200 after, 401 after `POST cli-auth/revoke-current` | measured by the lead |
| the key shows in Paperclip as "Orbweaver (board)" and expires in 30 days | measured by the lead; **neither `me` nor the challenge reports the expiry**, so the app stores approval time plus 30 days and believes the 401 over its own date |
| `local_trusted` instances accept requests with no credential | read from source; **not measured** (this instance is `authenticated`) |
| `pollPath` is `/cli-auth/challenges/<id>` with **no `/api` prefix and no query**; `approvalPath` is `/cli-auth/<id>?token=<token>` (a UI route); `approvalUrl` is the absolute form of it. The poll that works is `GET <base>/api/cli-auth/challenges/<id>?token=<token>` | **measured by the lead** on the live instance (unauthenticated `POST`). The connect code resolves `pollPath` against the API root (prefixing `/api` when absent) and appends the token when there is no query; a poll that fails three times in a row ends the attempt with a reason instead of waiting out the 10-minute challenge |
| `POST /api/cli-auth/challenges/{id}/cancel` takes `{"token": <challenge token>}` and no credential | **measured by the lead** (2026-11-23 05:18Z): with the token, 200 `{"status":"cancelled","cancelled":true}`; without it, 400. Afterwards the poll returns `status: "cancelled"` with a `cancelledAt`, an approve by the signed-in user answers **200** (not an error) with `{"approved": false, "status": "cancelled", "keyId": null}`, and the challenge's `boardApiToken` stays refused (401). The author never called it. The app sends it best-effort, once, with its own 3 s timeout, whenever an attempt ends other than by approval or Paperclip's own cancelled/expired, so an abandoned challenge does not stay pending where a later Approve would issue a key no app holds. Its failure is ignored and never shown, and the token is never logged. A poll that returns `status: "cancelled"` (someone cancelled it in Paperclip's UI) ends the attempt as cancelled and sends no second cancel |
| `approvalUrl` is built from the address the server was called on, so a LAN-reached Paperclip may return a host the browser cannot use | **assumed, not measured**; when the host differs from the address the user connected to and the server gave an `approvalPath`, the connected address plus that path is opened instead. A non-http(s) URL is never rewritten; it is refused |
| the instance folder `~/.paperclip/instances/<name>/` holds `runtime-info.json` (`port`) and `config.json` (`server.port`) | measured on macOS; the Windows folder (`%USERPROFILE%\.paperclip`) is **assumed** |

Discovery reads exactly those two files and nothing else in the folder (it also
holds a secrets directory and a database), and writes nothing.

## What the parsers had to tolerate

- **Run rows vary.** One failed run came back with 13 keys and `redacted: true`
  (no `errorCode`, no pid); a later failed native run came back full (53 keys)
  with `errorCode` but `processPid` and both session ids null; a legacy run has
  the pid and session. The parser accepts all three and reads only `status`,
  times, `errorCode`, `issueId`, `processPid`, `sessionIdAfter` and `redacted`.
  A run's `error` text is never read: measured ones carry commands.
- **Optional lists.** Approvals, tool action requests and blocked tasks are
  optional: a company is "connected" when the company and agents reads succeed.
  A 404 on one of them means "none"; any other failure keeps the last answer and
  is named in the status line ("Couldn't read tool actions."), never a link
  failure. Only a refused key (401/403) asks for a reconnect. Not measured
  against a Paperclip that lacks one of the routes; decided from the product rule.
- **Nothing secret is logged.** The transport logs method, path (query
  stripped, since the poll URL carries the challenge token), status, size and
  time. Never a body or a key.
- **Two shapes were never seen:** a non-empty tool action request, and a blocked
  issue waiting on another task. The first is parsed by trying the likely keys
  (`agentId` / `requestedByAgentId` / `agent.id`; `summary` / `toolName` /
  `tool` / `title`) and the tests built on it say they are edited copies; the
  second's only measured `blockerAttention.state` is `needs_attention`, so any
  other value is treated as "Paperclip resolves this itself".
- **`live-runs` under the native runner** carried internal span text as its
  `currentStatusMessage` ("runner span: runner.process.launch …") and no tool
  name (measured by the lead). The hover drops text of that shape. The rule is
  the plan's literal reading, written as `^[\w-]+\.[\w-]` plus the
  `runner span:` prefix; a real sentence with a space after its first full stop
  passes. What `live-runs` says *during a healthy native run past startup* has
  not been measured.

## Where the board key is stored

`paperclip-credentials.json`, beside the settings file. The key is full board power, so it is created owner-only from its first byte and replaced by renaming a file that was itself created that way:

- **macOS and Linux:** mode 0600 on a temp file, renamed over the real one.
- **Windows:** an explicit ACL with inheritance off and a single rule, the current user with full control. **"The profile's ACL protects it" was an assumption and it was false on a real PC (measured):** a file created in the profile inherited access for `CodexSandboxUsers` (Modify), an unresolved SID (Modify), SYSTEM, Administrators and the user. The new code was run on that PC: the file as created, as stored, and as replaced over a deliberately world-readable file each had exactly one rule (the user, FullControl, not inherited), inheritance off and the user as owner. The unit tests read the same back on the Windows leg of CI.
- **An already-wide key file is not narrowed at launch, only when the key is next stored.** For Paperclip that is acceptable: the file is new in this release, so only test machines that connected before the owner-only change can have a wide one, and a reconnect or Disconnect/Connect rewrites it owner-only. It is one of CB-423's requirements for the older secret files, which do exist wide in the field.
- Other secrets in this app are written with the same assumption on Windows (the OpenClaw gateway token and device identity, the peer-link identity, the relay identity, the cached Claude CLI credential): the owner-only mode is set only off Windows and the file otherwise inherits its folder's ACL. Not changed here; reported.

## Remembering the roster across restarts

A key lasts 30 days, and a launch with an expired key (or an unreachable Paperclip) has no snapshot at the first poll, so without help the company would draw nothing: a silent disappearance, which the product plan forbids. The app therefore writes `paperclip-roster.json` beside the settings, only when the roster changed, holding ids, names, roles, titles, `reportsTo`, the issue prefix, the address and the company id, and nothing else (no key, task text, spend, activity, error reason or adapter setting; a test reads the file back and asserts it). It is created owner-only by the same private-create path as the key file. At start, when enabled, those rows are published as sleeping orbs, parked with the link's own words, until the first successful read replaces them; they are never used once a live snapshot exists, agents that no longer exist vanish at that first read, and the file is removed on Disconnect and when the address or company changes. A corrupt or unreadable file is ignored. Not seen on a real machine: written and tested, and the first run that restarts the app with a refused key is the real proof.

## Timing

- A run shorter than the poll interval never shows as working: a woken agent whose
  run failed in 0.18 s was measured (on Windows, against a LAN Paperclip) and the
  orb correctly showed the result, not the run.
- A connected app makes about 105 requests a minute: agents and live-runs every
  2 s (60), approvals, tool actions and blocked tasks every 4 s (45), plus recent
  runs when something moved. Server-side each read is 2 to 75 ms.
- A change reaches the screen after the poll that sees it, plus the scan: the
  poll's snapshot used to wait for the scan's next 2 s tick (measured: amber held
  3.7 s after the poll that saw the approval gone). A publish that changes what
  the orbs draw now asks for a scan through the status-file watcher's 150 ms
  debounce, so it is about one poll plus 150 ms. The first publish at launch
  counts as a change.

## Real-machine results (QA, 2026-11-23)

As reported by QA and the lead, on a real Windows PC against the LAN Paperclip (the Mac mini) and on the mini itself. Builds named where they matter.

**Worked on a real machine**

- **Connect from the Settings UI on Windows.** Connect opened the approval page in Edge on the connected host; about 3 s after approval the group read "Connected", with the key and company stored by the UI itself. The `/api` poll resolution, storing the key only after `me` answers, and the single-company auto-pick were all exercised there.
- **The company drew correctly** (39c9bcd9d): the lead with the broke mark, arrows to three reports, two asleep, one amber.
- **A refused key** dims all four orbs with their marks hidden, and Settings says "Reconnect to Paperclip". After a Reconnect from the UI the key file reads the user only, inheritance off (it had been group-readable before the owner-only change).
- **Cancel.** A real cancel from Settings answered 200 and the challenge then read `cancelled` (mini, 3bad4033c). The poll's failure-only log wrote 0 lines in 110 s while the app was polling.
- **An unapproved attempt** polled for 600 s and stored nothing; a local Cancel stored nothing.
- **Latency.** Amber appears about 0.2 s after the poll that sees the approval with the scan nudge (1.0 to 1.5 s without it). A real approval was seen on screen 0.8 to 1.3 s after it was created before the nudge.

**Measured, and not explained**

- **Amber clears about 3.3 to 3.5 s after the poll that sees the approval gone** (3.7 s before the nudge), then fades over about 0.8 s. The nudge helped the way in and not the way out. No hold on leaving amber was found in `SessionManager`, `OrbWindow` or the fold rule. The fade is the orb's own colour and dim fade. The delay is upstream of the apply; candidates are a scan pass already in flight on that PC, or a need still present after the rejection. Settled by the slow-phase log and a local-orb control, neither read yet.
- **Launch on Windows.** The four Paperclip orbs were on screen about 3.2 s after launch (first read at about +1 s). It is the local orbs that arrive late: one at about +6 s and the rest at about +15 s. The scan runs its disk read, subprocess probes and apply one after another, nothing draws until the slowest finishes, and a tick that lands mid-pass is skipped, so one slow source delays every orb. That is the existing scan, not Paperclip's; it is its own bug. The slow-phase diagnostic added to name the phase wrote to stderr, which the Windows build (a WinExe) does not have, so it wrote nothing at 98f659c84; it now writes to `diagnostics.log` beside the crash log.
- **A run shorter than the poll** (a 0.18 s failure was measured) never shows as working.

**Never exercised on a real machine**

- A **successful agent run under the native runner** (no run has succeeded there; the native runs that happened failed).
- An **"Ask first" tool action request**: its row has never been seen, so the parser's keys remain guesses.
- A **blocked task waiting on another task**, so the other `blockerAttention` states.
- The **one-orb fold on a real screen**: a hook-firing agent next to a local session, and a company of more than eight agents folding and unfolding.
- **Anything on a phone**: the roster rows, the old-build behaviour, and release 2's prompts.
- **`local_trusted` connect**, and **local discovery on Windows** (the Paperclip there is on another machine).
- A connect where the approval URL host differs from the address the user connected to (the LAN rewrite is written and unit-tested, not seen).
- The "Paperclip found" tray hint and the launch discovery on a machine with a Paperclip running locally.
- Windows: the tray Reconnect row, if the tray controller cannot be built headless on the CI runner (its tests skip there with a reason), and the other Windows-only checks beyond the key file ACL, which was run on the real PC.

## Not measured, and who settles it

| open | settled by |
| --- | --- |
| a non-empty tool action request row | recapture when an "Ask first" action fires |
| the `in_progress` filter on `issues` (only `blocked` is measured) | the task line simply does not appear if it fails |
| pending-approval and budget-pause shapes beyond the decided approvals captured | recapture |
| the status file of a preconfigured-home agent during a run | read during a run long enough to catch it |
| Windows instance folder, and LAN behaviour from Windows | QA's real-machine proof |

## Wire facts from the loopback tests (not Paperclip's, but the transport's)

- `HttpListener.Abort()` does **not** drop the socket on this runtime; it
  answered an empty 200 when tried. A dropped connection is therefore tested
  against a raw listener that accepts and closes.
- `HttpClient` retries an idempotent `GET` once on a connection closed before
  any response. A fixture that accepts only one connection turns that retry
  into a five-second timeout, which looks like a hung server and is not.

## Runtime permission prompts (CB-442)

Measured 2026-11-24 on the mini's Paperclip 2026.1117.0, one run of an agent in `approve-paperclip` mode, loopback. Everything under "Measured" was seen on that run; everything under "Assumed" was not.

**Measured**

- A board API key from the app's own connect flow may resolve a request: `POST /api/heartbeat-runs/{runId}/runtime-requests/{requestId}/resolve` answered 200 `{"accepted":true,"commandId":…}` in 68 ms. Paired controls on the same request: no auth, 403 "Board access required"; the same call again, 409 "This runtime request is stale or is no longer pending."; the run's events then carried `runtime_request.resolved`.
- Nothing marks a waiting run without reading its events: with a request pending, the heartbeat-runs row and the live-runs row both had `livenessState`, `nextAction` and `currentToolName` null.
- `GET /api/heartbeat-runs/{id}/events?limit=500` took 8 to 34 ms for 46 to 92 events.
- A created event: top-level `eventType`, `seq`, and `payload.prpEvent = { turnId, itemId, payload: { request: { requestId, requestKind: "permission_approval", type: "permission", prompt, choices: [accept "Allow once", decline "Deny", cancel "Cancel"], details: { toolCallId } } } }`. The tool's name is on the `tool.execution.started` event sharing `prpEvent.itemId` (`payload.name`); the command text is not on it.
- Resolve body: `{"resolution":{"action":"accept"|"decline"},"requestKind":"permission_approval","turnId":"<turnId>"}`.
- The events endpoint returns a bare JSON array (107 events on that run). The `resolved` event (seq 51, after the created at seq 49) carries `requestId`, `requestKind`, `turnId`, `itemId` and `action` ("decline") directly under `prpEvent.payload`. The tool's started event shared the request's `itemId`. Scrubbed excerpts are in `tests/UnitTests/Fixtures/paperclip/native/runtime/events-real-*.json`.
- Not every call prompts: a read-only shell command ran with no request; `mkdir` raised one.

**Assumed (not measured)**

- Whether `cancelled` and `expired` events carry their `requestId` where `resolved` does; the parser looks under `prpEvent.payload.request`, `prpEvent.payload`, then `payload`.
- Polling cost over a LAN or on a long run; 403 as the answer to a key that was valid a minute ago (mapped to "reconnect", not seen).

**What Orbweaver does:** on the 4 s tick it reads the events of each `running` live run, keeps requests whose latest event is `created`, and shows one per agent as a `runtime` need ("Bash: Create probe directory"), answerable with "Allow once" / "Deny" in the flyout and on the phone (same option keys, different labels). Only `permission_approval` with accept and decline choices is answerable; anything else links to Paperclip. A failed events read keeps that run's last answer for one tick.
