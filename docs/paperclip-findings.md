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
it can be; the other two show their last run `succeeded`, so a native run has
now completed here, which the plan's earlier revision said had not happened.
The `agents` body is 9.7 KB for four agents and carries `adapterConfig`; the app
never reads it and never logs a body.

## Connect

| fact | status |
| --- | --- |
| `POST /api/cli-auth/challenges` with `{"command":"Orbweaver","clientName":"Orbweaver","requestedAccess":"board"}` returns `id`, `token`, `boardApiToken`, `approvalUrl`, `pollPath`, `expiresAt`, `suggestedPollIntervalMs: 1000` | measured by the lead |
| the `boardApiToken` is 401 before approval, 200 after, 401 after `POST cli-auth/revoke-current` | measured by the lead |
| the key shows in Paperclip as "Orbweaver (board)" and expires in 30 days | measured by the lead; **neither `me` nor the challenge reports the expiry**, so the app stores approval time plus 30 days and believes the 401 over its own date |
| `local_trusted` instances accept requests with no credential | read from source; **not measured** (this instance is `authenticated`) |
| the poll URL is `pollPath` with `?token=<challenge token>` | **assumed**: the connect code appends the token only when `pollPath` has no query already, and the loopback test pins both shapes. The probe does not POST, so no real `pollPath` was read for this file |
| `approvalUrl` uses the host the request came in on | **assumed**; settled by logging the host of one real challenge |
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
