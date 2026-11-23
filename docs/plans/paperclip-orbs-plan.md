# Orbweaver for Paperclip: implementation plan (CB-421 desktop, CB-422 phone contract)

Epic CB-419. **The requirement is the product plan** ("Orbweaver for Paperclip: the product"): your Paperclip company, alive on your desktop and in your pocket, and the thing that needs you one tap away. This document turns its release 1 ("see it") into something two engineers and QA can build, and fixes the release 2 ("act on it") contract now so release 1's does not have to be reopened.

Revision 4. It adds §13, the buildable release 2 (CB-427 desktop, CB-428 phone and watch), with its own measurements, and replaces §7's outline and §4's "broke" outline where they differ; §13 says where. Sections 0 to 12 are revision 3, unchanged. Revision 1 (`f6e7700a0`) designed a pasted key, no pushes and a legacy-mode dedupe as the main path; revision 2 (`0c7d29c3c`) rebuilt it on the product plan. Revision 3 added the first run under Paperclip's own runner: a second, measured hook-firing mode, a real blocked task, and a real failed run.

Desktop branch `feature/paperclip-orbs`, cut from `develop` at `c9b5bc047`. Mobile read at `Orbweaver-Mobile` `origin/develop` `7155aa3`. Line references are against those shas. Paperclip is `2026.1117.0`, authenticated mode, on the Mac mini.

## 0. Rules this plan holds everywhere

1. **Nothing is installed into, or required of, the user's Paperclip org.** No hook, skill, MCP server or agent setting. Orbweaver reads Paperclip's control plane. The API-only path is the main path and must work when no hook ever fires.
2. **One agent, one orb.** Where an agent's run also shows up as a local session, the local row is folded away (§2.9).
3. **Amber means you can act.** Release 1: a click on an amber orb opens the exact Paperclip page that answers it. Release 2: answer in place.
4. **Quiet by default.** No push for a finished run, ever. A routine never starts a Live Activity.
5. **Release 1 is read-only.** It issues `GET`, plus the two connect calls (`POST cli-auth/challenges`, `POST cli-auth/revoke-current`). Approve, decline, wake, pause and resume are release 2.

## 1. Measured, read from source, assumed

"Measured" means run against the live instance. "Lead" marks measurements the team lead took on 2026-11-22 and sent me; I did not repeat the ones that create keys or decide approvals. "Declared" means Paperclip's own OpenAPI document, which it serves at `GET /api/openapi.json` (200 without a credential, 843 paths) and which names the credential kinds each route accepts.

### 1.1 Connect

| Fact | Status |
| --- | --- |
| Unauthenticated `POST /api/cli-auth/challenges` with `{"command":"Orbweaver","clientName":"Orbweaver","requestedAccess":"board"}` returns `id`, `token`, `boardApiToken`, `approvalUrl`, `pollPath`, `expiresAt`, `suggestedPollIntervalMs: 1000` | measured (lead) |
| `GET /api/cli-auth/challenges/{id}?token=…` reports `status` | measured (lead) |
| The `boardApiToken` is 401 before approval, 200 after, 401 after `POST /api/cli-auth/revoke-current` | measured (lead) |
| The key shows in Paperclip as "Orbweaver (board)" and expires in 30 days | measured (lead) |
| A board key as `Authorization: Bearer` is 200 on every GET in §1.2, from loopback and the LAN; no credential is 401; a wrong key is 401 | measured |
| `GET /api/cli-auth/me` with a board key: 200, `source: "board_key"` | measured |
| Challenge body also takes `requestedCompanyId`; `requestedAccess` is `board` or `instance_admin_required` | declared |
| Challenge lifetime is 10 minutes; statuses are pending, approved, cancelled, expired | read from source |
| `local_trusted` instances accept requests with no credential | read from source; **not measured** (this instance is `authenticated`) |
| Instance folder `~/.paperclip/instances/<name>/` holds `runtime-info.json` (`instanceId`, `pid`, `host`, `port`, `dashboardUrl`, `startedAt`) and `config.json` (`server.deploymentMode`, `server.port`, `server.host`) | measured on macOS |
| `GET /api/health` (no credential): `status`, `deploymentMode`, `bootstrapStatus` | measured |
| The same folder on Windows is `%USERPROFILE%\.paperclip` | **assumed** |

### 1.2 Reading the company

All measured with a board key unless marked.

| Call | Result |
| --- | --- |
| `GET /api/companies` | array; `id`, `name`, `status`, `issuePrefix` |
| `GET /api/companies/{id}/agents` | 9.6 KB for 4 agents, 11 ms. Used: `id`, `name`, `role`, `title`, `status`, `reportsTo`, `adapterType`, `budgetMonthlyCents`, `spentMonthlyCents`, `pauseReason`, `errorReason`, `lastHeartbeatAt`. **Carries `adapterConfig` with local paths: never log the body.** |
| An agent in `error` | measured live, twice: `status: "error"` with a one-sentence `errorReason` (one from a refused task type, one from a failed run) |
| `GET …/live-runs` | `[]` idle. Running, legacy runner (measured): `id`, `agentId`, `status`, `startedAt`, `issueId`, `invocationSource`, `currentStatusMessage` ("Receiving agent output"), `currentToolName`. Running, native runner (lead): `currentStatusMessage` was internal span text ("runner span: runner.process.launch …") and `currentToolName` was null. |
| `GET …/heartbeat-runs` | newest first; `limit`, `agentId`, `summary=true` work. **Rows vary.** One failed run came back with 13 keys and `redacted: true` (`status`, `startedAt`, `finishedAt`, `durationMs`, no `errorCode`); cause not established. A later failed native run came back full (53 keys) with `errorCode: native_provider_terminal_failed` and a one-sentence `error`, but `adapterType`, `runtimeMode`, `processPid` and both session ids null. The parser must accept all three shapes. |
| `GET /api/heartbeat-runs/{id}/events` | 64 events for that run, 74 KB. Includes `native.process_identity_recorded {processPid, processGroupId, hostname}` 0.6 s into the run, `hook.started` / `hook.completed` with `payload.event` (`SessionStart`, `UserPromptSubmit`), `turn.failed`. Not polled in release 1. |
| `GET …/approvals?status=pending` | `[]` now, 7 ms. Two real pending approvals listed in 6 ms (lead). Row, measured from the two now-decided ones: `id`, `type`, `status`, `payload{title, summary, agentId}`, `requestedByAgentId`, `requestedByUserId`, `decisionNote`, `decidedAt`, `createdAt` |
| `GET …/tools/action-requests` | 200 `{"actionRequests":[]}`, 2 ms. Defaults to `status=pending` (read from source). **A non-empty row has never been seen.** |
| `GET …/issues?status=blocked` | One real blocked issue, 3.5 KB: `id`, `identifier`, `title`, `status`, `assigneeAgentId`, `blockedTransitionAt`, and `blockerAttention{state: "needs_attention", reason: "attention_required", …}`. It was blocked by the failed run above, so the same agent is both `error` and the assignee of a blocked task. |
| `GET …/sidebar-badges` | `{inbox, approvals, failedRuns, joinRequests, unreadIssueIds}`, 28 ms |
| `GET …/dashboard` | `agents{active,running,paused,error}`, `tasks{…}`, `costs{monthSpendCents, monthBudgetCents, …}`, `pendingApprovals`, `budgets{…}` |
| Spend | every agent: `budgetMonthlyCents: 5000`, `spentMonthlyCents: 0` after real runs. Paperclip's docs say subscription usage does not count against a budget (lead). |

Enumerations, read from source: agent status `active, paused, idle, running, error, pending_approval, terminated`; run status `queued, scheduled_retry, running, succeeded, interrupted, failed, cancelled, timed_out`; approval type `hire_agent, approve_ceo_strategy, budget_override_required, request_board_approval`; pause reason includes `manual, budget, system, company_archived`; tool action request status `pending, approved, executing, rejected, expired, cancelled, executed, failed`.

Web pages, read from the UI bundle's route table, all under `<base>/<issuePrefix>/`: `agents/:agentId`, `approvals/:approvalId`, `inbox/requests`, `inbox/blocked`, `issues/:issueId`. **Not opened in a browser.**

### 1.3 Acting (release 2)

| Fact | Status |
| --- | --- |
| `POST /api/approvals/{id}/approve` and `/reject` with `{"decisionNote":…}` and the app key: 200, and the pending list emptied; no credential: 403 | measured (lead) |
| `POST /api/tool-gateway/action-requests/{id}/approve` and `/decline`, body `{"companyId"}`, accept a board key | declared; **never called** |
| `POST /api/agents/{id}/wakeup` (202), with a browser session | measured |
| `POST /api/agents/{id}/pause`, `/resume`, and `wakeup` accept a board key | declared; pause and resume **never called** |

### 1.4 The live feed

`ws://…/api/companies/{id}/events/ws`: no credential 403; board key as Bearer 403; board key as `?token=` 403; browser session cookie 101. Measured. The upgrade path only looks bearer tokens up in the agent-key table (read from source). So Orbweaver polls. Under the legacy runner, a 0.5 s poll saw each change 0.3 to 0.5 s after the socket did.

### 1.5 Timing

Measured under the legacy runner, one small org, one evening: 13 runs lasted 0.4 to 99 s, two under 5 s; a no-work wake showed `running` over REST for 3.7 s. Under the native runner one run has happened and it failed after 8 s. **No native run has succeeded yet** (the org's Claude connection is still being set up), so none of this is a claim about a healthy native run.

### 1.6 Agent modes and the hook

| Mode | Hook fires | Status |
| --- | --- | --- |
| Default runner (`paperclip_runner`, isolated home) | never | lead, read from code; no run of this mode has been observed. Nothing local to fold. |
| `paperclip_runner` with `homeMode: "preconfigured"` | **yes, measured** | Paperclip's own run events show `SessionStart` and two `UserPromptSubmit` hooks. Orbweaver's hook log, in the ordinary per-user status directory, has the same session going idle, generating, ended within 2 s, so the status file existed for about 2 s and was removed. The session's transcript rows say `entrypoint: sdk-ts` and cwd `~/.paperclip/instances/<name>/isolated-workspaces/<companyId>/<uuid>`. **The status file itself was not read** (it was gone); the hook copies `CLAUDE_CODE_ENTRYPOINT` into it, so `sdk-ts` is inferred, not observed. The Claude session id is **not** Paperclip's `normalizedSessionId`, and the run row has no pid or session id, so only the cwd identifies it. The transcript landed under the default Claude config root, not the one the agent's env named. |
| Legacy `claude_local` | yes, measured | `entrypoint: sdk-cli`; cwd `…/workspaces/<agentId>` or `…/isolated-workspaces/<companyId>/<uuid>`; `session_pid` equals the run row's `processPid`; the status file's session id equals `sessionIdAfter`. Nothing from the environment is recorded. |

### 1.7 Assumed, and who settles it

| Assumption | Settled by |
| --- | --- |
| A tool action request row names its agent (`agentId` or similar) and something to show (a tool name, a summary) | engineer A recaptures when the fixture org's "Ask first" action fires; until then the parser tries the likely keys and falls back to the company's top lead |
| A blocked issue needs a person exactly when its `blockerAttention.state` is `needs_attention`. One real case measured; the other values of that field have not been seen (an earlier run was cancelled because its issue was blocked on dependencies Paperclip resolves itself, which is the case this field should exclude). | engineer A, on the first blocked issue that is waiting on another task |
| What `live-runs` says during a healthy native run once past startup (a human sentence, a tool name, or more span text) | recapture (§10) |
| The preconfigured-mode status file says `sdk-ts` and the isolated-workspace cwd, as its transcript does | engineer A reads the file during a run that lasts long enough to catch it |
| `local_trusted` connect with no approval | QA, only if an instance in that mode exists; otherwise stated as unverified |
| Windows instance folder; LAN behaviour from Windows | QA real-machine proof |
| The five page URLs in §1.2 | QA clicks each kind once |
| `approvalUrl` uses the host the request came in on | engineer A logs the host of one real challenge |

## 2. Release 1, desktop design

### 2.1 Shape

```
PaperclipApi.cs          pure: JSON -> records; state, marks, needs-you, fold, ring, URLs, fold-away rule.
PaperclipDiscovery.cs    pure over a file-reading seam: instance folders -> candidates.
PaperclipConnect.cs      the approval flow as a step function over IPaperclipApi and a clock.
PaperclipSessions.cs     the poll loop (copy ClaudeCloudSessions: StepAsync, Snapshot, SetSnapshotForTests, Restart).
PaperclipCredentials.cs  key and expiry per base URL, 0600, outside settings.json.
```

Binding interface (names fixed so the packages in §11 can build in parallel; bodies free):

```csharp
internal enum PaperclipMark { None, Broke, Paused, OverBudget }

internal sealed record PaperclipNeed(
    string Kind,        // "approval" | "action" | "blocked"
    string Id,          // Paperclip's id for it
    string Title,       // one line: what is being asked
    string Url);        // the page that answers it

internal sealed record PaperclipAgent(
    string Id, string Name, string Role, string Title,
    string Status, string? ReportsTo, string AdapterType,
    string State,                       // "idle" | "generating" | "waiting"
    PaperclipMark Mark, string? MarkReason,
    IReadOnlyList<PaperclipNeed> Needs, // empty unless waiting
    string? Task, string? Activity,     // hover: task line; currentStatusMessage / currentToolName
    DateTime? RunStarted, string? LastResult,
    int? BudgetPercent,                 // null = draw no ring
    DateTime LastActivity, string Url,
    bool Sleeping,                      // idle, no mark, no need
    int? RunPid = null, string? RunSessionId = null,
    long BudgetCapCents = 0, long SpentCents = 0);   // as built in B1: the hover prints the amounts, the ring only the percent

internal static class PaperclipSessions {
    public static IReadOnlyList<PaperclipAgent> Snapshot();     // every agent, unfolded; empty unless enabled
    internal static void SetSnapshotForTests(IReadOnlyList<PaperclipAgent> agents);
    internal static PaperclipLink Link { get; }                 // Off | Connecting | Connected | NeedsReconnect | Unreachable
    internal static string StatusText { get; }
    public static void Restart();
}
```

New `SessionStatus` members, all `[JsonIgnore]` like `Kind`: `PaperclipMark Mark`, `string? MarkReason`, `int FoldedCount`, `string? FoldedNames`, `int? BudgetPercent`, `IReadOnlyList<string> HoverLines` (the hover's lines, already ordered, so the orb prints them and decides nothing).

### 2.2 Finding a Paperclip (`PaperclipDiscovery`)

- For each folder in `~/.paperclip/instances/*/`: read `runtime-info.json` for `port`, fall back to `config.json` `server.port`. Candidate address is `http://127.0.0.1:<port>` (the files say `0.0.0.0` and a LAN `dashboardUrl`; loopback is what this machine should use).
- No folder: one candidate, `http://127.0.0.1:3100`.
- A candidate counts only if `GET /api/health` answers `status: "ok"`. Its `deploymentMode` is kept.
- Runs when Settings opens, and once at launch if Paperclip is not enabled and the hint has not been shown. A folder read and one loopback GET; never on a timer.
- A found instance shows **"Paperclip found on this machine. Show your company?"** with a **Connect** button in the Settings group, and one tray-menu row until it is used or dismissed (`paperclipHintShown`).
- Nothing in that folder is ever written, and no other file in it is read (it also holds a secrets directory and a database).

### 2.3 Connecting (`PaperclipConnect`)

1. `GET /api/health`. If `deploymentMode` is `local_trusted`: `GET /api/companies` with no credential; 200 means connected with no key and no browser.
2. Otherwise `POST /api/cli-auth/challenges` with `command: "Orbweaver"`, `clientName: "Orbweaver"`, `requestedAccess: "board"`. Open `approvalUrl` in the browser (scheme must be `http` or `https`; anything else is refused and shown as an error). Settings shows "Approve Orbweaver in your browser…" with Cancel.
3. Poll `pollPath` every `suggestedPollIntervalMs` until `approved`, `cancelled`, `expired`, or the challenge's own `expiresAt`.
4. On `approved`: `GET /api/cli-auth/me` with the `boardApiToken` must be 200; only then is the token written to `PaperclipCredentials` with its expiry. Before that it lives in memory only.
5. Company: exactly one means it is chosen; more than one shows the picker.
6. **Disconnect** (toggle off, or a "Disconnect" button): `POST /api/cli-auth/revoke-current`, then delete the stored key. A failed revoke still deletes the local copy and says the key may still be listed in Paperclip.
7. **A remote Paperclip:** an address box, then steps 1 to 5 against it. When the scheme is `http` and the host is not loopback, a note: "The key is sent unencrypted on this network."

The key is full board power and there is no read-only board key. That is why release 1 never issues a write beyond the two connect calls.

### 2.4 Reconnect, and never a silent disappearance

| Condition | Orbs | Words |
| --- | --- | --- |
| 401 or 403 on a poll | **kept, dimmed** (`Presence = Parked`), states frozen, ring and marks hidden | Status line and each orb's tooltip: "Reconnect to Paperclip". Settings button and orb-menu row **Reconnect** run §2.3 again. |
| Stored expiry within 3 days | unchanged | Status line: "Connected. The key expires in N days." |
| Unreachable (timeout, refused, 5xx) | kept as they are for `DisconnectGrace = 15 s`, then dimmed the same way | "Can't reach <host>." On `EHOSTUNREACH` for a non-loopback host on macOS, append the Local Network hint `OpenClawGateway.ExplainConnectFailure` uses. |
| Switched off | removed at once | "Paperclip is switched off." |

Orbs are removed only by switching off, disconnecting, or the agent being terminated or gone from a successful listing.

### 2.5 The poll

| Every | Calls | Why |
| --- | --- | --- |
| 2 s | `agents`, `live-runs` | working and asleep; two of thirteen measured runs were under 5 s |
| 4 s | `approvals?status=pending`, `tools/action-requests`, `issues?status=blocked` | needs-you is a slower state; 6, 2 and 10 ms measured |
| on change | `heartbeat-runs?limit=20` when `live-runs` is non-empty, or any `lastHeartbeatAt` moved, or first tick | last result; legacy pid and session id |
| on demand | `issues?status=in_progress` when a live run names an `issueId` not yet known | the hover's task line (the `status` filter is measured for `blocked`, assumed for `in_progress`; no task line if it fails) |

`sidebar-badges` and `dashboard` are not polled: the three lists already say which agent needs you, which the summaries do not.

Backoff on failure 2, 4, 8, 16, 30 s. `StepAsync` takes the API, the clock and the previous state, so every branch is a unit test. 5 s request timeout. Log status code, byte count and elapsed time; never a body, never the key.

### 2.6 States, marks and needs

`PaperclipApi.Compose`, first match wins for `State`:

| # | Input | `State` | Notes |
| --- | --- | --- | --- |
| 1 | agent `terminated` | no orb | |
| 2 | the agent has at least one need (below) | `waiting` | `Presence = NeedsInput`. Wins over a live run: an agent blocked mid-run is waiting on you. |
| 3 | agent `pending_approval` with no need found | `waiting` | need synthesised: kind `approval`, URL `approvals/pending` |
| 4 | live run `running` or `queued` | `generating` | |
| 5 | agent `running`, no live row | `generating` | |
| 6 | everything else (`idle`, `active`, `paused`, `error`, unknown) | `idle` | dimmed when `Sleeping` |

Needs, one per item, attached to an agent:

| Source | Agent | `Title` | Click opens |
| --- | --- | --- | --- |
| pending approval | `payload.agentId`, else `requestedByAgentId`, else the top lead | `payload.title`, else the type worded ("Hire", "Budget override", "Strategy", "Approval") | `approvals/<id>` |
| pending tool action request | the row's agent, else the top lead | the row's tool or summary, else "Tool action" | `inbox/requests` |
| blocked issue whose `blockerAttention.state` is `needs_attention` (or that has no `blockerAttention` at all) | `assigneeAgentId`; an unassigned one is ignored | "Blocked: <identifier> <title>" | `issues/<id>` |

`Mark`, independent of `State`, first match wins:

| # | Input | `Mark` | `MarkReason` |
| --- | --- | --- | --- |
| 1 | agent `paused`, `pauseReason` `budget` | OverBudget | "Paused: over budget" |
| 2 | agent `paused`, any other reason | Paused | "Paused" + the reason worded when known |
| 3 | agent `error` | Broke | `errorReason`, cut to 140 characters |
| 4 | newest finished run `failed` or `timed_out` | Broke | "Last run failed" + `: <errorCode>` when the row has one / "Last run timed out" |
| 5 | otherwise | None | |

**`Activity` for the hover** is `currentToolName` when set; else `currentStatusMessage`, unless it looks internal, in which case it is dropped and the task line stands alone. Internal means it starts with `runner span:` or matches `^[\w-]+\.[\w-]` (as built: `PaperclipApi.LooksInternal`), i.e. it opens with a dotted identifier such as `runner.process.launch`. A sentence whose first word ends in a full stop followed by a space does not match. The measured legacy sentence "Receiving agent output" passes; the measured native span text does not.

An agent can be amber and broke at once (measured: a failed run blocked its task). Amber decides the state and the click; the mark's reason still shows in the hover.

Broke clears on the next `succeeded` run, because rule 4 reads the newest finished run. `cancelled` and `interrupted` are not broke. `LastResult` wording for the hover: "succeeded", "failed", "timed out", "cancelled", "interrupted", "retry scheduled", or the raw status. A run row's `error` text is never shown (measured ones carry commands); `errorCode` and the agent's `errorReason` are.

### 2.7 What the orb shows

| Thing | Rule | Reuses |
| --- | --- | --- |
| Paperclip badge | `CliMark.Paperclip` bottom-left on every Paperclip orb, working or not | `CliMark.cs` |
| Asleep | idle, dimmed like a parked session | `OrbPresence.Parked` |
| Amber | `waiting`, with the "?" | `OrbPresence.NeedsInput` |
| Broke / Paused / Over budget | a mark in the presence corner: "!" for Broke, a pause glyph for the other two. Amber's "?" wins the corner when both apply; the mark's reason still shows in the hover. Drawn marks go in `SymbolMarks`, not typed glyphs (CB-173). | new `ApplyMark` beside `ApplyPresence` |
| Budget ring | drawn only when `budgetMonthlyCents > 0` **and** `spentMonthlyCents > 0`; percent is spent over cap, capped at 100 for geometry. Hidden otherwise, so a subscription-billed company shows no empty ring. | The orb's **existing context ring** (`OrbWindow` `ContextRingLayer`, `UsageRingGeometry`, the colour bands account orbs use): No new arc. `PaperclipOrb.RingPercent(status)` is the gate: a Paperclip row draws `BudgetPercent` only, every other source `ContextPercent` only, and null draws no ring. So `BudgetPercent` must be null, never 0, when the cap or the spend is zero. (Lead's ruling; revision 2 had a new arc.) |
| Hover | lines in this order, each omitted when empty: need titles; mark reason; task; activity; "running for 3 m"; "last run succeeded 2 h ago"; "$0.26 of $50.00 this month" | tooltip at `OrbWindow.axaml.cs:371` |
| Click | amber: the first need's URL. Otherwise the agent's page. | `ClickRouting.OpensInABrowser` |
| Menu | **Open in Paperclip**; **Review in Paperclip** (amber only); **Reconnect** (§2.4 only). Wake, Pause, Resume are release 2. | |
| Folded count | a "+N" pill on the lead; its tooltip lists the names | new, small |

No chat panel. No kind badge.

**Dimming, as built (`PaperclipOrb.Dims`, B2 at `b6a81bf78`).** A Paperclip orb dims to 0.45 only when it is asleep (`Parked` with no Broke or OverBudget mark) or manually paused (mark Paused, unless it also needs you). Needs-you, broke, over-budget and working draw at full strength, and broke or over-budget win even when the row is `Parked`. The dim is applied to the orb's body, not the whole window, so the presence-corner mark ("?", "!", pause) stays at full strength on a dimmed orb. It is re-applied whenever the verdict changes, even if presence did not. Every other source keeps today's rule.

**The hover's "last run … ago", as built, and where it can be wrong.** `PaperclipScan` prints `last run <result> <ago>` from `PaperclipAgent.LastActivity`, which is the newest of the agent's `lastHeartbeatAt`, the newest finished run's start and finish, and any live run's start. It is a stand-in for the run's own finish time. The line is already left out while the agent is working, and when the mark's reason already says "Last run …".

- **Usually right.** In the one case where both were read, `lastHeartbeatAt` was 28 ms after the run's `finishedAt`.
- **Wrong, certain from the code:** an agent that is amber while a run is in flight (a need beats a live run for the state). The live run's start is in the maximum, so the line gives the *current* run's start as the time the *previous* run ended.
- **Wrong, possible:** whenever `lastHeartbeatAt` moves without the listed run being the one that just finished. The run listing is the newest 20 rows across the whole company, so on a busy company an agent's newest run can fall outside it and an older result gets a fresh time. Whether `lastHeartbeatAt` ever moves without a run finishing has not been established.

The stand-in is not needed: all three measured run-row shapes (full, redacted, summary) carry `finishedAt`. **Recommended fix:** print the time from the newest finished run's `finishedAt`, and print the result with no time when that is missing. One nullable field on the record and one line in `PaperclipScan`; `LastActivity` keeps its job as the scan entry's stamp.

A report under a lead draws at the existing team-member scale. **Open in Paperclip** and **Review in Paperclip** both open `status.Url`, which §3's merge block sets to the first need's page when the agent is waiting.

### 2.8 The fold rule (`PaperclipApi.Fold`)

Pure: `Fold(agents, mode, limit = 8, recentlyAwake)` returns the agents to draw and, per lead, the count and names folded into it.

- `show`: draw everyone.
- `fold` (default): with `limit` or fewer agents in the company, draw everyone. Above it, an agent is folded when it is `Sleeping`, has no reports of its own, and has a lead. It unfolds while generating, waiting or carrying any mark, and stays unfolded for `UnfoldHold = 60 s` after it last did, so a short run does not make the picture jump. A manually paused agent carries a mark, so it stays visible.
- `hide`: the same test, but folded agents are not counted on the lead.
- A lead is never folded. An agent with no lead is never folded.

Setting `paperclipSleeping`: `fold` | `show` | `hide`.

### 2.9 Keying, lead, and one orb per agent

- **Key** `"paperclip:" + agent.Id`. **Lead** `"paperclip:" + ReportsTo` when that agent is drawn; otherwise empty. The precedent for a lead with no process is the OpenClaw room, `SessionManager.cs:2582`.
- **Stale timer:** exempt, beside the cloud exemption at `SessionManager.cs:1491`. An agent between runs is the normal state.
- **Hiding a lead does not hide its reports.** `HiddenOrbs.PropagatingLead` returns "" for a Paperclip row: an org chart is not an agent team whose members only make sense with their lead on screen. A report whose lead is hidden draws with no lead.
- **Fold-away of a local row** (`PaperclipApi.IsRunProcess`), applied only while Paperclip is enabled and its snapshot is non-empty, at the `OpenClawCliSession.ShowHere` call site (`SessionManager.cs:3070`):
  - `entrypoint` starts with `sdk-` (measured: `sdk-cli` for legacy, `sdk-ts` for preconfigured) **and** any of: the cwd has the consecutive segments `.paperclip`, `instances`, `<any>`, then `workspaces` or `isolated-workspaces`; the row's pid is a known run pid; the row's session id is a known run session id.
  - **The cwd half is the one that covers both measured modes.** The pid and session halves only ever match under legacy `claude_local`; under the native runner the run row carries neither and Paperclip's session id is a different id. They stay because they catch a legacy agent whose project workspace lies outside `.paperclip`.
  - Both halves are required: `sdk-` alone is every headless Claude anyone runs, and a `.paperclip` cwd alone is a person's own terminal in a workspace.
  - Under the isolated default runner there is no local row and the rule never fires.
  - A preconfigured-mode agent whose workspace is outside `.paperclip` would be missed (no such agent exists to measure). The run's pid is available from the run's events (`native.process_identity_recorded`); reading events per live run is the fix if that case turns up, and is not built in release 1.
  - With Paperclip off, nothing changes from today.

## 3. Release 1, desktop: per-file change list

New: `PaperclipApi.cs`, `PaperclipDiscovery.cs`, `PaperclipConnect.cs`, `PaperclipSessions.cs`, `PaperclipCredentials.cs` (file `paperclip-credentials.json` in `OrbweaverSettings.Directory`, mode 0600 off Windows, copying `OpenClawIdentity.cs:107-186`), `tools/paperclip-probe/` (read-only: `discover`, `check`, `agents`, `needs`, `runs --watch`; key from `PAPERCLIP_BOARD_KEY`; never prints `adapterConfig`), `docs/paperclip-findings.md` (§1 in the voice of `docs/openclaw-findings.md`, updated by the recaptures).

| File (line at `c9b5bc047`) | Change |
| --- | --- |
| `SessionManager.cs:342` | `SessionSource.Paperclip`. Not in `IsLocalCli` (`:162`). New `SessionStatus` members (§2.1). |
| `SessionManager.cs:1491` | Stale exemption. |
| `SessionManager.cs:~2775` | Merge block after the cloud block: `Fold(PaperclipSessions.Snapshot(), …)`, one `ScanEntry` per drawn agent. `Source = Paperclip`, `Kind = Unknown`, `Title = Name`, `Cwd = ""`, `Color = OpenClawSessions.ColourForAgent(Id)`, `Url` = first need's URL when waiting else the agent page, `StatusDetail`/`RecentAction` from the hover fields, `Presence`, `Lead`, mark, ring, folded count. `Written = LastActivity`, never `now`. When `Link` is `NeedsReconnect` or unreachable past the grace: every row `Parked`, state idle, no mark, no ring. |
| `SessionManager.cs:3070` | Fold-away (§2.9). |
| `SessionManager.cs:3652` area | A Paperclip orb never builds a chat session. |
| `ClickRouting.cs:165` | `OpensInABrowser`: `ClaudeCloud or Paperclip`. Empty `Url` opens nothing. |
| `CliMark.cs:76` | `Paperclip`: a paperclip outline as one filled path in the 16×16 box, fill `#5B5BD6`. Judged in the screenshot on both rids. |
| `OrbWindow.axaml`, `.axaml.cs` | Budget through the existing context ring; mark in the presence corner; "+N" pill; hover lines (`:371`); menu rows; `resetIdleExplanation` arm (`:487`): "Paperclip controls this agent's state". `NewChatHereItem` stays hidden (`:472`). |
| `SymbolMarks.cs` | Pause and "!" marks as paths. |
| `HiddenOrbs.cs:117` | `"Paperclip agent"`. `PropagatingLead` gives "" for Paperclip. |
| `OrbAutoGroups.cs:46` | key `paperclip`, name `Paperclip`. |
| `AutoGroupAccounts.cs:75`, `LocalPersona.cs:193` | No account; no local persona. |
| `OrbweaverSettings.cs:90` | Keys `paperclipEnabled` (false), `paperclipUrl` (""), `paperclipCompanyId` (""), `paperclipSleeping` ("fold"), `paperclipHintShown` (false), in the `OpenClawEnabled` pattern (`:334`, `:1340`). |
| `SettingsWindow.cs:736`, `SettingsWindow.Paperclip.cs` | As built: `SettingsWindow` is `partial` and the group's rows live in `SettingsWindow.Paperclip.cs`. `Group("paperclip", "Paperclip", …)` after OpenClaw: found-instance row with **Connect**; address box for a remote one; approving state with Cancel; company picker (hidden with one company); "When agents are asleep" (fold / show / hide); **Disconnect**; **Reconnect** when needed; status line refreshed where `_claudeCloudStatus` is (`:2245`). No key is ever shown or typed. |
| `TrayController.cs` | The one-time "Paperclip found" row. |
| `Program.cs:166`, `ScreenLockWait.cs:73` | `PaperclipSessions.Restart()`; the comment's list. |
| `README.md` | A Paperclip section: what it shows; connect is one approval; the key lasts 30 days and Orbweaver asks to reconnect; nothing is installed in the org; release 1 is view-only. Two troubleshooting lines, measured on the mini and not Orbweaver's to fix: on a Homebrew Mac the Paperclip launchd service's `PATH` excludes Homebrew; Paperclip's Claude sign-in could not parse Claude Code 2.1.330's output (2.1.286 worked). |

No hook, installer or packaging file changes: the feature is all in the app binary. `tools/build-macos-app.sh`, `tools/Orbweaver.iss` and both `install-hooks` scripts were checked for that and need nothing.

## 4. The phone contract: `frames-v15.json` (Orbweaver-Mobile, lands first)

`frames-v14` is taken (`frames-v14-login.json`, CB-412). One file covers both releases; release 1 implements the `roster` half and sends no prompt.

```json
{
  "version": 15,
  "note": "CB-422: Paperclip agents. Additive over frames-v2's roster, frames-v3's prompt/answer/resolved and frames-v4's watch/input. No new frame, no new roster field, no new state, no new error code, no caps name, nothing for the relay. \"15\" is this file's name; nothing on the wire carries it. Section `roster` ships first; section `prompt` is sent only by a desktop that can carry the answer out.",
  "roster": {
    "cli": "may be \"paperclip\". A phone must treat a cli it does not know as a row it can show and cannot type to.",
    "paperclip_row": {
      "session_id": "\"paperclip:\" + the agent's id (a UUID). Stable across runs and restarts.",
      "title": "the agent's name",
      "cwd": "\"\" (present, empty)",
      "state": "generating: a run is queued or running. waiting: something of this agent's needs a person (an approval, a tool action, a blocked task). idle: everything else, including paused and error.",
      "since": "unix seconds of the last state change",
      "parent_session_id": "\"paperclip:\" + the id of the agent it reports to, only when that agent is a row in the same roster",
      "persona": "omitted"
    },
    "which_agents": "the agents the desktop is drawing. An agent the desktop has folded into its lead is not a row.",
    "removal": "the ordinary removal delta"
  },
  "desktop_answers": {
    "watch": "ok watching, total 0, then no window and no run frame",
    "unwatch": "ok unwatched",
    "input": "err untypeable, why \"Paperclip agents can't be typed to from a phone\"",
    "set_run": "err unavailable, why \"This desk can't change that session's model or effort.\" (frames-v13's code for a control that is off for the session)",
    "launch.parent_session_id naming a paperclip row": "err unsupported_parent, whatever the launch's own cli"
  },
  "prompt": {
    "when": "one per pending approval or pending tool action, and only for an agent that is already a row in the roster the phone holds. A blocked task mints none: it has no yes or no.",
    "frame": {
      "t": "prompt", "request_id": "<ULID>", "session_id": "paperclip:<agent id>",
      "kind": "permission", "cli": "paperclip",
      "tool": "Approval | Hire | Budget override | Strategy | Tool action",
      "preview": "<title>\\n<summary>, at most 4000 characters",
      "options": [
        { "key": "approve", "label": "Approve", "kind": "approve" },
        { "key": "decline", "label": "Decline", "kind": "deny" }
      ],
      "screen_hash": "base64url SHA-256 of UTF-8 \"orbweaver-paperclip-v1\\n\" + (\"approval\" | \"action\") + \"\\n\" + Paperclip's id for the item",
      "expires_at": "mint time + 86400. The item stays pending in Paperclip for as long as it takes; frames-v3's 1800 is a terminal dialog's lifetime."
    },
    "push": "amber, with e, exactly as any prompt",
    "answer": "frames-v3's answer unchanged. action is approve or deny; anything else is err forbidden_option. A watch-signed approve or deny passes rule 1w as it does today.",
    "on_a_valid_answer": "the desktop carries it out in Paperclip, then sends resolved by phone:<ph_id>",
    "resolved": "by desk when it was decided at the desk or in Paperclip; by gone when it is withdrawn or the agent is terminated",
    "refusals": "the six of frames-v3 and no others. Already decided in Paperclip: not_pending. Paperclip unreachable or refusing the call: stale_prompt, and the prompt stays pending."
  },
  "pushes": "amber and resolve for a prompt, as above. Never done: a paperclip row's generating -> idle is an ordinary delta with push none.",
  "activity": "frames-v7 counts: a paperclip row is never counted as working or idle. It is counted as amber only while a prompt for it is pending.",
  "phone": {
    "row": "title, state, machine, its place under its parent; no composer, no watch request, no run controls. Not offered as a parent for a new session or a team join.",
    "old_builds": "decode the roster and the prompt unchanged (cli is a string in both). The row opens to an empty conversation; typing is refused with the sentence; a prompt is answerable."
  }
}
```

Three things I had not read, confirmed by engineer 2 from the code at desktop `0c7d29c3c` and mobile `7155aa3` (read, not run on a device):

- `set_run` on a session with no run controls answers `unavailable` with `RelayRemote.NoRunControlsWhy`. Today a Paperclip id would answer `unknown_session` first, so the `Listed()` check goes before `Local()`.
- `RelayLaunch.Rules` (`RelayLaunch.cs:161-165`) tests the **request's** cli, not the parent's, so a `claude` launch naming a Paperclip row as its parent is **not refused today**. Release 1 adds one check: the parent is a Paperclip row, answer `unsupported_parent`. Scoped to Paperclip only; what a Codex or Grok parent does today is not changed here.
- Nothing rejects an `expires_at` a day out: `AnswerGate` rule 3 compares the answer's to the pending prompt's and to now; both phones use the prompt's own value. So 86400 stands. One cosmetic case: iOS `WatchContextBuild.swift:37` derives an orphan amber's `since` as `expires_at - 1800`, which would be wrong for a prompt whose session is not in the roster. The `when` rule above (mint only for a row already in the roster) keeps a Paperclip prompt off that path.

**Not in v15, and why:** the **"broke" push**. The relay refuses any `push` value outside `none, amber, resolve, done, limit` (`relay/Relay/Frames.cs:156`), and a new alert needs its own APNs wording and mute class. That is a relay change, so it is `frames-v16` with release 2's tickets. Outline: push value `broke`, class `state`, on the delta where a row's mark becomes Broke, `e` `{v:1, t:"broke", session_id, label, title}`, mutable like `done`.

Files in the contract PR: `contracts/frames-v15.json`; `contracts/roster-fixtures.json` gains a fourth roster `paperclip_roster` (one `claude` row, a Paperclip lead, two reports, all three states) and `paperclip_deltas_after_roster`, the three existing rosters keeping their bytes; `contracts/check_vectors.py` `rosters()` (`:281`) walks it with rules for a paperclip row (`session_id` matches `^paperclip:[0-9a-f-]{36}$`, `cwd == ""`, no persona, parent starts `paperclip:`), `ROSTER_FIELDS` unchanged; `contracts/test_check_vectors_paperclip.py` (the real file passes, six mutations each fail naming their rule); `contracts/README.md`; `.github/workflows/relay-tests.yml`. Prompt vectors for the `prompt` section are release 2's.

Phone pointers for CB-422. Verified at `7155aa3`: `cli` is a `String` in `app/Relay/RosterReducer.swift` (`Entry.cli`), `app/Relay/Prompts.swift:110` and `android/relay/.../Roster.kt` (`entry()`); the team-join filter at `app/Model/LiveMapping.swift:119` is already `cli == "claude"`; `app/Screens/SessionScreen.swift:201,225` print `s.cli` raw. Not re-read at that sha: `canSend`, `SessionScreen.kt`, `Shell.kt`, `NewSessionScreen.kt`. Fixture consumers: `app/Tests/ShellTests/RosterContractTests.swift`, `android/relay/src/test/.../FramesAndRosterTests.kt`, `app/LiveTests/Vectors/`.

## 5. Release 1, relay: desktop side

| File | Change |
| --- | --- |
| `MirrorProtocol.cs:834` | `CliPaperclip = "paperclip"` and its `CliFor` arm. One test that the peer roster never emits it. |
| `RelayRoster.cs:97-106` | `Project` admits `IsLocalCli || Source == Paperclip`. Paperclip row: `cwd ""`, no persona, `parent_session_id = Lead` only when that row is admitted. |
| `RelayRoster.cs:55` | `Finished` is false for a Paperclip row, so `RelayLink.cs:698` mints no `done` push. |
| `RelayRemote.cs:150` | Keep `Local()`. Add `Listed()` for a Paperclip row and the four answers in §4 ahead of the `Local()` checks at `:376`, `:430`, `:471`, `:482`. |
| `RelayLaunch.cs:161` | In `Rules`, after the `InRoster` check: a parent that is a Paperclip row answers `unsupported_parent`. A vector for it, beside a control with a Claude parent that still launches. |
| `RelayActivity.cs` | Paperclip rows out of `working` and `idle`; out of `amber` too in release 1, since no prompt exists yet. |
| `tools/relay-vectors`, `tests/UnitTests/Fixtures/relay/roster-fixtures.json` | The fourth roster, byte-identical to the mobile copy. |

## 6. What old phone builds do

Read from the code at `7155aa3`, not run on a device. The roster decodes; the row draws with the default look and nests under its lead; opening it shows an empty conversation (not `unknown_session`, which both phones word as "That session is gone from the desk."); typing is refused with the sentence; no push arrives in release 1. In release 2 an old build receives an ordinary answerable prompt.

## 7. Release 2, designed now, built under its own tickets

**Superseded by §13 (revision 4).** Kept as the record of what release 1 promised.

| Piece | Design |
| --- | --- |
| Approve / Decline on the desktop | The orb's flyout shows each need's title with **Approve**, **Decline** and "Review in Paperclip", in the shape of today's permission prompt. Calls: approvals `approve` / `reject` with `decisionNote: "From Orbweaver"`; tool actions `tool-gateway/action-requests/{id}/approve` / `decline` with `{companyId}`. Behind an `IPaperclipActions` seam; the UI suite drives a fake. A blocked task shows only "Review in Paperclip". |
| Wake, Pause, Resume | Orb menu rows calling `wakeup` (`source: "on_demand"`), `pause`, `resume`. Pause and resume are armed-then-confirmed rows, like the OpenClaw end-conversation row. |
| Answerable push | §4 `prompt`. Minted from `PaperclipAgent.Needs` by a small `PaperclipPrompts` beside `PromptPipeline`; the answer is verified by the existing `AnswerGate` rules and carried out through `IPaperclipActions`. |
| "Broke" push | `frames-v16`, relay change, outlined in §4. |
| Must be measured first | a real tool action request row and its approve/decline; pause and resume with the app key; what Paperclip answers when an item is approved twice. |

## 8. Release 1 test matrix

100% of added lines. Named exceptions for the PR body: real `HttpClient` construction, `Process.Start` for the browser, real `Environment` home lookup.

| Level | Class | Cases |
| --- | --- | --- |
| Unit | `PaperclipApiParseTests` | Every file in `native/` and `legacy-claude-local/` parses to the expected records: 4 agents with 3 reporting to 1; the `error` agent with its reason; a redacted run row and a full one in one listing; both decided approvals; empty lists. `[]`, `{}`, a row with no `id`, wrong types, truncated JSON: none throw. |
| Unit | `PaperclipStateTests` | One case per row of the three tables in §2.6, plus precedence: a need beats a live run; `terminated` beats everything; Broke clears after a newer `succeeded`; `cancelled` is not Broke; a run `error` string never reaches any output; the real agent that is both `error` and blocked is `waiting` with a Broke mark; `Activity` keeps "Receiving agent output", drops "runner span: runner.process.launch (124 ms)" and a bare dotted identifier, and prefers a tool name. Statuses never captured are built from edited copies of the real files, and the test says so. |
| Unit | `PaperclipNeedsTests` | Approval by `payload.agentId`, by `requestedByAgentId`, by neither (top lead); each approval type's wording; a tool action row under each candidate agent key and with none; the real blocked issue (`needs_attention`) is a need on its assignee; the same row with another `blockerAttention.state` is not; with no `blockerAttention` it is; unassigned and unknown-assignee rows are ignored; each need's URL. |
| Unit | `PaperclipFoldTests` | 8 agents: all drawn. 9: sleeping non-leads folded, count and names right. A folded agent unfolds on generating, waiting, each mark; stays for the hold; folds after. Leads and lead-less agents never fold. `show`, `hide`. Three-level chain. |
| Unit | `PaperclipRingTests` | No ring for cap 0, spent 0, both 0; percent for a normal pair; over 100 capped. |
| Unit | `PaperclipRunProcessTests` | The three real legacy hook files are run processes. A row with `sdk-ts` and the measured isolated-workspace cwd, no pid or session match, is one (the preconfigured case; built in the test from the measured values and labelled so, since the file itself was never captured). Must be false: `entrypoint: cli` in the same cwd; `sdk-cli` or `sdk-ts` in an ordinary repo with no pid or session match; a `.paperclip` path that is not a workspace. Windows separators. |
| Unit | `PaperclipDiscoveryTests` | The real `runtime-info.json` and config section give `127.0.0.1:3100`; a folder with only `config.json`; two instances; a malformed file is skipped; no folder gives the default candidate; a candidate whose health is not ok is dropped. |
| Unit | `PaperclipConnectTests` | Fake API and clock: `local_trusted` connects with no challenge; approved stores the key only after `me` is 200; cancelled, expired and timed out store nothing; a non-http `approvalUrl` is refused; Cancel stops polling; disconnect revokes then deletes, and deletes even when the revoke fails. |
| Unit | `PaperclipSessionsStepTests` | The cadences in §2.5 (what is called on which tick); backoff; 401 gives `NeedsReconnect` with the snapshot kept; unreachable keeps then dims after the grace; recovery clears both; several companies and none chosen; disabled makes no call. Every status-line string, including the expiry countdown. |
| Unit | `PaperclipCredentialsTests` | Round trip of key and expiry per base URL; delete; 0600 off Windows. |
| Unit | `RelayRosterTests`, `RosterFixtureTests`, `RelayRemotePaperclipTests` | Row admitted with the §4 shape; parent only when admitted; `Finished` false with a Claude control true; the four answers, each beside the unchanged answer for an unknown id and a local row; activity counts exclude it; the fourth roster re-serialises byte-identically. |
| Integration | `PaperclipHttpTests` | The real `IPaperclipApi` against `tests/Shared/LoopbackHttp.cs`: the paths and query strings are exactly §2.3's and §2.5's; `Authorization` sent with a key and absent without; 401, 500, a hung response, a dropped connection and non-JSON come back as typed failures; the challenge POST body is byte-exact. |
| Integration | settings round trip | The five keys survive save and load; an old file loads defaults; unknown keys survive beside them. |
| UI | `PaperclipScanTests` (model `CloudScanTests`, in `[Collection("Settings")]`) | Lead and two reports give three orbs with the right keys and leads; state changes in place; a terminated agent's orb goes; switching off removes all; an agent three hours asleep survives the stale timer; `NeedsReconnect` dims and keeps every orb; ten agents fold with the count on the lead; a matching `sdk-cli` row is not drawn while connected and **is** drawn with Paperclip off. |
| UI | `PaperclipOrbTests` | Badge present; no kind badge; each mark; "?" wins the corner over a mark; the context ring drawing budget for a Paperclip row and never a context percent, hidden when budget is null; each row of the dimming rule, with the corner mark at full strength on a dimmed orb and a non-Paperclip control on the old rule; "+N"; every hover line and its order; click target for amber and for not amber; menu rows per state; reset-idle disabled. |
| UI | `SettingsWindow` tests | Found-instance row and Connect; approving state and Cancel; picker shown only with several companies; Reconnect shown only when needed; the http-on-LAN note; no control ever holds the key. |
| Screenshot | `tests/UiScreenshots` | (1) a company: lead with reports, one working, one amber, one broke, one paused, one with a ring; (2) a folded lead with "+N"; (3) the Settings group, connected; (4) the dimmed "Reconnect" state. |
| Console | `ArrangementTests` | Run once; no new geometry expected. |
| Contract | `test_check_vectors_paperclip.py`, `relay-tests` | §4. |

## 9. Release 1 real-machine proof (QA; minutes each, paired)

Install owner: QA. Take `/tmp/ow-install.lock` on the mini first and hash-check each install. Do not decide approvals or wake agents on the fixture org without the lead's go-ahead.

1. **Connect, timed.** On the MacBook (remote address) and the Windows PC: from opening Settings to orbs on screen, nothing pasted. Target under 30 s. On the mini: the "Paperclip found" row appears with no address typed.
2. **Connect, negative.** Cancel in the browser: no key stored, no orbs.
3. **Company.** Four orbs linked to the lead on all three machines; positions read back, not judged by eye.
4. **Presence.** When an agent can run: its orb is working within about 2 s and asleep after. Size the window to about three times the run.
5. **Needs you.** With a real pending item: the orb is amber and a click opens the page that decides it. Paired negative: a finished run changes nothing on a paired phone.
6. **Broke.** The agent currently in `error` wears the mark and its reason.
7. **Expiry.** Revoke the app's key in Paperclip: orbs dim, "Reconnect" restores them with one approval. Control: an unreachable address dims after the grace and says so.
8. **One orb.** On the hook-firing fixture agent: one orb during a run. Control: Paperclip off, the transient local orb appears as today.
9. **Phone.** A current build shows the company under its lead with states; an old build, if one is installed anywhere, opens a row to an empty conversation.

## 10. Fixtures in the branch, and what must be recaptured

`tests/UnitTests/Fixtures/paperclip/`, scrubbed by one script: every UUID remapped consistently across both folders (the same agent has the same fake id in each), home paths and the company replaced, user ids nulled, approval titles, summaries and decision notes replaced, tool lists emptied, all free text over 200 characters and every excerpt and log reference removed. Audited for the home name, the org, LAN addresses, key and cookie prefixes, ticket keys and unmapped UUIDs: none left.

| Folder | Files | State |
| --- | --- | --- |
| `native/` | `agents.json` (4 on `paperclip_runner`, two in `error`, one with `homeMode: preconfigured`), `issues-blocked.json` (the real blocked task, assigned to one of the `error` agents), `heartbeat-runs.json` (a full failed native row, a redacted row, a legacy success), `run-events-failed-run.reduced.json` (all 64 events of the failed run by type and time, with payloads kept only for the process-identity, hook and turn-failed events; **reduced, not verbatim**), `approvals-decided.json` (one approved, one rejected), `dashboard.json`, `sidebar-badges.json`, `org.json`, `runtime-info.json`, `config-server-section.json`, and the empty lists | current. `dashboard.json` and `sidebar-badges.json` predate the failed run. |
| `legacy-claude-local/` | `agents.json`, `live-runs-running.json`, `heartbeat-runs.json`, `heartbeat-runs-summary.json`, `hook-status-during-run-{1,2,3}.json`, `ws-events-one-wake.jsonl` | **legacy mode only.** Kept because the fold-away rule is tested against them. Their `processPid`, session ids and `runtimeMode` say nothing about the native runner. |
| top level | `companies.json`, `health.json` | current |

**To recapture once an agent can run under the native runner** (engineer A, with the probe, read-only):

1. **A successful native run**: `live-runs` during it (what the status message says after startup), and its `heartbeat-runs` row. Waiting on the Claude connection, which may need one interactive sign-in.
2. **A non-empty tool action request** row ("Ask first"). Waiting on the same thing. This is the least-known shape in the plan.
3. A **pending** approval (not yet decided).
4. A blocked issue that is waiting on another task, for the other `blockerAttention` states.
5. An agent `paused` manually and one paused by budget, and a non-zero `spentMonthlyCents` if metered billing ever produces one.
6. The preconfigured-mode **status file** itself, during a run long enough to read it.
7. Why some run rows are redacted.

The real blocked task and the two real `error` agents are already captured; the failed run will be fixed later, so those files are the record of it.

Until each exists, its tests run on edited copies made inside the test and labelled as edited.

## 11. Work packages (release 1)

**Commit 0, engineer 1, first and at once:** `SessionSource.Paperclip`, the new `SessionStatus` members, `MirrorProtocol.CliPaperclip`, and the §2.1 records with an empty `PaperclipSessions` (`Snapshot`, `SetSnapshotForTests`, `Link`, `StatusText`). After it, nobody waits on anybody.

| Package | Owner | Files |
| --- | --- | --- |
| **A. Paperclip side** | engineer 1 | `PaperclipApi.cs`, `PaperclipDiscovery.cs`, `PaperclipConnect.cs`, `PaperclipSessions.cs`, `PaperclipCredentials.cs`, `tools/paperclip-probe/`, `docs/paperclip-findings.md`, their unit and integration tests, the recaptures in §10 |
| **C. Contract and relay** | engineer 2, first | Mobile contract PR (§4). Desktop §5 files and tests. |
| **B2. What the orb shows** | engineer 2, after C | `OrbWindow.axaml(.cs)`, `SymbolMarks.cs`, `CliMark.cs`, `HiddenOrbs.cs`, `OrbAutoGroups.cs`, `AutoGroupAccounts.cs`, `LocalPersona.cs`, `PaperclipOrbTests`, screenshots 1 and 2. Built from hand-made `SessionStatus` values, so it needs only commit 0. |
| **B1. Wiring and settings** | engineer 1, after A's `Compose` and `Fold` exist | `SessionManager.cs`, `ClickRouting.cs`, `OrbweaverSettings.cs`, `SettingsWindow.cs`, `TrayController.cs`, `Program.cs`, `README.md`, `PaperclipScanTests`, settings tests, screenshots 3 and 4 |

No file appears in two packages. QA starts at commit 0: writes the §9 script and checks which old phone builds exist, then measures added-line coverage once per PR.

One desktop PR against `develop` naming CB-421 and CB-422; one mobile contract PR against mobile `develop` naming CB-422, merged first. A desktop that sends `cli:"paperclip"` before any phone updates is safe by §6. The phone screens follow under CB-422 in the mobile repo.

Release 2 is staffed from §7 under its own tickets once its three measurements exist.

## 12. Not in release 1

- Approve, decline, wake, pause, resume; any push; the "broke" push (release 2, §7).
- The live-feed socket (§1.4).
- A chat panel or run transcript.
- Several instances or several companies side by side.
- Folded agents on the phone (the roster is what the desktop draws).
- Persona pictures from Paperclip's avatars.
- TLS pinning for an `https` instance.
- Merging a hook row's finer state into the Paperclip orb.

## 13. Release 2, "Act on it" (CB-427 desktop, CB-428 phone and watch)

Revision 4. Desktop read at `origin/develop` `8bfc5e7b9`, mobile at `origin/develop` `69b663e`. Shared desktop branch `feature/paperclip-act`. The requirement is the two tickets; this section is how they are built.

Where this section differs from §4 and §7: the "broke" push is muted through the phone's `prefs` frame, not the relay's `push_mute` (§13.3); its `e` carries a `why` code; a desktop sends one Paperclip prompt per agent at a time (§13.4); `expires_at` and the option list are §4's, unchanged.

### 13.1 Measured, read from source, assumed (release 2)

Measured on 2026-11-23 against Paperclip `2026.1117.0` on the Mac mini, with a board key obtained through the one-click challenge and revoked afterwards. Two test approvals were created for the purpose and both are decided; one agent was used and left `idle`, as found, though its newest run is now a failed one (rows 6 and 10).

| # | Fact | Status |
| --- | --- | --- |
| 1 | `POST /api/companies/{id}/approvals` with a board key, type `request_board_approval`, `payload{title, summary, agentId}`: 201, a `pending` row with `createdAt` | measured |
| 2 | Approve a pending approval: 200, the row with `status: "approved"`, `decidedAt`, `decisionNote` | measured (3.4 s the first time, 0.2 s for the reject) |
| 3 | **Approve an approved one again: 200, the same row, the same `decidedAt`.** Reject a rejected one again: the same. | measured |
| 4 | **Reject an approved one: 422** `{"error":"Only pending or revision requested approvals can be rejected"}`. Approve a rejected one: 422 with the matching sentence. The row does not change. | measured |
| 5 | Approve an id that does not exist: 404 `Approval not found`. No credential: 403 `Board access required`. A key that is not a key, or a revoked one: 401. | measured |
| 6 | **Approving wakes the agent that asked.** A run with `invocationSource: "automation"` started 1.7 s after the decision. On this org it failed after 7.8 s and the agent went to `error`. Whether a reject does the same was not separated out. | measured, once |
| 7 | `POST /api/agents/{id}/pause` with a board key: 200, the agent row with `status: "paused"`, `pauseReason: "manual"`, `pausedAt`. Again: 200, the same `pausedAt`. | measured |
| 8 | `POST /api/agents/{id}/resume`: 200. **The row it returns, and the agent list for up to 2 s after, still say `paused` with `pauseReason: null`; then `idle`.** A second resume is 200. | measured, twice |
| 9 | Pausing an agent in `error` and resuming it leaves it `idle`: the error status is gone. What resume alone does to an `error` agent was not measured. | measured, twice |
| 10 | `POST /api/agents/{id}/wakeup` with a board key and body `{"source":"on_demand"}`: 202 and the run row (`id`, `status: "queued"`, `triggerDetail: "manual"`). `forceFreshSession` is not needed, whatever the OpenAPI document says. | measured |
| 11 | **That wake, on a `paperclip_runner` agent with no task waiting, failed in 0.7 s** with `errorCode: adapter_failed` ("Paperclip Runner requires a standard, planning, ask, or skill test task") and put the agent in `error`. It never appeared in `live-runs` at a 1 to 2 s poll. Two `on_demand` runs of the same agent the night before succeeded, so this is not every wake; what differed was not established. | measured, once |
| 12 | A pending tool action request, minted without an agent run by a test call on the fixture's example tool (the lead's recipe: `POST /api/tool-connections/{id}/test-calls`, which answers `{"decision":"ask_first","invocationId","actionRequestId"}`). The list is `{"actionRequests":[row]}` and **the row is nested**: `request{id, status, previewMarkdown, canonicalArgumentsSummary{summary}, requestedByAgentId, requestedByUserId, createdAt, expiresAt, …}` beside `toolName`, `toolTitle`, `connectionName`, `applicationName`, `riskLevel`, `requestedByAgentId`. There is no `id` at the row's top level. | measured, 4 rows |
| 12a | On a test-call request `requestedByAgentId` is null at both levels and `requestedByUserId` is set, though the call named an agent. What a request raised by a real run carries has still not been seen. | measured (lead, and again here) |
| 12b | A request lives one hour (`expiresAt` is `createdAt` + 1 h). `previewMarkdown` is markdown ("create_item · \*\*Title:\*\* …"); `toolName` is the gateway's namespaced name. | measured |
| 12c | Approve with `{"companyId"}` and a board key: 200 and the request object itself (not wrapped). **The tool runs inside that call**, and the status in the answer is the run's: here `failed`, with `error` set to the tool's own message. So a 200 does not mean the tool worked. | measured, once |
| 12d | Approve again: 200, the same row. Decline again after a decline: 200, the same row. | measured |
| 12e | **Decline after approve: 409** `{"error":"This review has already been resolved"}`. **Approve after decline: 409** `{"error":"Tool action request is no longer pending","reasonCode":"action_not_pending"}`. | measured |
| 12f | Decline with `{"companyId","reason"}`: 200, `status: "rejected"`. No `companyId`: 400 `companyId is required`. Unknown id: 404 with `reasonCode: "action_request_not_found"`. No credential: 403. | measured |
| 12g | The default list and `?status=pending` return the same rows; a decided request leaves both at once. `?status=failed` and `?status=rejected` list the decided ones. | measured |
| 12h | **Release 1 on `develop` does not see a real request.** `PaperclipApi.ParseActionRequests` reads `id` from the row's top level and skips a row without one, and the real row keeps it under `request`. | measured by running the parser (identical to `origin/develop` `8bfc5e7b9`) on the captured pending list: 2 real rows in, 0 parsed; the same rows with `request` lifted to the top level, 2 parsed. A defect against CB-421's "Ask first" requirement, fixed in this branch. |
| 13 | Resume of an agent paused for budget | **not measured**; none exists |

Read from source, not run:

| Fact | Where |
| --- | --- |
| The relay refuses a `push` outside `none, amber, resolve, done, limit` with `bad_envelope`, and the envelope is dropped with it | mobile `relay/Relay/Frames.cs:156` |
| `done` and `limit` need class `state`, desktop to phone, and go through `StatePushAsync`: mute check, token lookup, 30 s window keyed `push|from|ck|to`, no reminder, no ack | `relay/Relay/Push.cs:44,84,158` |
| `hello.push_mute` naming anything but `done` or `limit` is `bad_hello` and the socket closes 1008 | `relay/Relay/Frames.cs:119-130` |
| A phone already sends its alert choices to each desktop in a sealed `prefs` frame (class `pair`); the desktop keeps them per phone, ignores keys it does not know, and decides the done push from them | desktop `RelayAlertRules.cs`, `RelayLink.cs:428,741`; iOS `app/Relay/AlertPrefs.swift`. **Android: not found by a search of `android/relay`; the phone engineer confirms.** |
| The iOS notification extension tells a push's kind by the APNs body's top-level `push`, then the plaintext's `t`; an unknown `push` falls through to the amber path, fails to parse and keeps the relay's text | `app/Relay/PushRewrite.swift:283-296`, `app/NSE/NotificationService.swift:54-98` |
| Roster deltas are numbered per phone; a phone that sees a gap resyncs | desktop `RelayRoster.cs:302-309` |
| The desktop prompt pipeline holds one live prompt per session id | `PromptPipeline.cs:120` |
| A prompt may be sent with no push (`PromptQuiet`) | `PromptPipeline.cs:36` |
| The wakeup body declares `forceFreshSession` as required; `source` is one of `timer, assignment, on_demand, automation` | Paperclip `GET /api/openapi.json` |
| Tool action approve and decline accept a board session or board key only (no agent key); body `{companyId}`; 404 is a declared answer | same |
| An approval can also be sent back (`request-revision`) and resubmitted | same; not used |

Assumed, and who settles it:

| Assumption | Settled by |
| --- | --- |
| A tool action request raised by a real agent run names that agent in `requestedByAgentId` | the first native run that reaches an "Ask first" tool; until then every real row seen names no agent and lands on the company's lead (§13.2) |
| Both phones key a pending prompt by session and would overwrite a second one | phone engineer, by reading; the desktop rule in §13.4 is safe either way |
| The phone sends `prefs` on every connect, not only on a change | phone engineer; if only on change, a fresh install sends it once at pairing |

### 13.2 Desktop: acting (CB-427)

**The seam.** New file `PaperclipActions.cs`:

```csharp
internal enum PaperclipActionResult { Done, AlreadyDecided, Refused, Unreachable, NeedsReconnect }
internal readonly record struct PaperclipOutcome(PaperclipActionResult Result, string? Why);
internal interface IPaperclipActions
{
    Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct);
    Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct);
    Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct);
    Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct);
}
```

Reached as `PaperclipSessions.Actions`, a static settable property like `RosterStore`. The flyout, the orb menu and `PaperclipPrompts` all call it and nothing else calls Paperclip to change anything. The address, key and company id stay inside the implementation, which is built from the same `IPaperclipApi` the poll uses.

| Call | Request |
| --- | --- |
| Decide, `need.Kind == "approval"` | `POST /api/approvals/{id}/approve` or `/reject`, body `{"decisionNote":"From Orbweaver"}` |
| Decide, `need.Kind == "action"` | `POST /api/tool-gateway/action-requests/{id}/approve` or `/decline`, body `{"companyId":…}` (measured, rows 12c to 12f) |
| Decide, any other kind, or the synthetic need whose id is `pending` | no request; `Refused`. The UI never offers buttons for these. |
| Wake | `POST /api/agents/{id}/wakeup`, body `{"source":"on_demand"}` (measured, §13.1 row 10) |
| Pause, Resume | `POST /api/agents/{id}/pause`, `/resume`, no body |

Outcome, the same for every call:

| Paperclip answers | Outcome | Words (`Why`) |
| --- | --- | --- |
| 2xx | `Done` | none. One exception: a tool action approve whose answer says `status: "failed"` is still `Done` (the decision was made, and the item is gone), with `Why` "Approved, but the tool failed", shown at the desk in the ordinary colour. The tool's own error text is not shown and never leaves the desktop. |
| 422 on an approval decide, then `GET /api/approvals/{id}` says anything but `pending`; 409 on a tool action decide; 404 on either | `AlreadyDecided` | "Already answered in Paperclip" |
| 401 | `NeedsReconnect` | "Reconnect to Paperclip to answer" |
| any other 4xx | `Refused` | Paperclip's `error` text cut to 140 characters, else "Paperclip refused that" |
| no connection, timeout, 5xx | `Unreachable` | "Can't reach Paperclip" |

`AlreadyDecided` is decided without trusting how Paperclip words a double decision: on a 4xx other than 401 to an approval decide, the implementation reads `GET /api/approvals/{id}` once, and a status other than `pending` is `AlreadyDecided`. That is the measured 422 (§13.1 row 4). The same decision made twice is a 200 and so `Done`, which is true: the item is in the state the user asked for (row 3). A 404 on a decide is `AlreadyDecided` (the item is gone, which is what the user needs to know).

After every `Done` and `AlreadyDecided` the implementation calls `PaperclipSessions.PollNow()`, new: it releases the poll loop's wait so the next step runs at once. That is requirement 3 and the CB-425 fix in one place, since the three seconds were the loop waiting out its interval.

**Reading a tool action request (corrects release 1).** `ParseActionRequests` reads the nested row: id `request.id`, status `request.status`, agent `request.requestedByAgentId` then the row's `requestedByAgentId`, created `request.createdAt`, expiry `request.expiresAt`. The need's title is `toolTitle`, else the part of `toolName` after its last colon, else "Tool action"; its summary is `previewMarkdown` with `**` removed, cut to 4000. The flat shape release 1 guessed stays accepted, so the old fixtures still parse. **A need that names no agent, approval or tool action, belongs to the company's lead** (the lead's decision; it is what `Compose.Resolve` already does), because it still needs the human. A tool action prompt's `expires_at` is the request's own expiry when that is sooner than a day.

**Wake, decided by the lead.** A Wake that fails at once must not raise its own alarm. The poll's issue queries are `status=blocked` and `status=in_progress`, so they cannot say whether an agent has an assigned task not yet started (the fixture has none in any open state to check against), and Wake therefore cannot be gated on a task at no extra query. So: Wake is offered as the ticket says and shows Paperclip's reason when the run fails; and a Broke transition for an agent within 30 s of a Wake pressed on this desktop mints no "broke" push. The orb still shows Broke. `PaperclipSessions.NoteWake(agentId, now)` records the press; `BrokeTransitions` takes those times and the clock.

**What the orb shows.** `SessionStatus` gains `Needs` (`IReadOnlyList<PaperclipNeed>`, `[JsonIgnore]`, empty for every other source), filled where `HoverLines` is.

- **Flyout, amber orb.** One block per need, in `Needs` order: the title, **Approve**, **Decline**, and a "Review in Paperclip" link that opens `need.Url`. A `blocked` need and the `pending` synthetic need show the title and the link only. While a call is out, that block's two buttons are disabled. `Done` and `AlreadyDecided` remove the block at once, without waiting for the poll (`AlreadyDecided` leaves its sentence in place of the block for the life of the flyout, in the ordinary text colour). `Refused`, `Unreachable` and `NeedsReconnect` put `Why` under the block in the warning colour and leave it answerable. A click on the amber orb opens this flyout instead of the browser; an orb whose only needs are link-only keeps release 1's click.
- **Orb menu.** `PaperclipOrb.MenuOffer` gains `Wake`, `Pause`, `Resume`. All three need `link == Connected`. `Wake` is offered when the agent is not running, not paused and not terminated. `Pause` when the mark is not `Paused` or `OverBudget`. `Resume` when the mark is `Paused`. An agent paused for budget is offered neither (lifting a budget pause is Paperclip's decision, and that resume is unmeasured). Pause and Resume are armed-then-confirmed rows like the OpenClaw end-conversation row; Wake acts at once. A failure shows `Why` as the orb's transient note, the way a failed focus does.

Three measured behaviours the UI has to live with. After **Resume**, Paperclip goes on saying `paused` for up to 2 s (row 8), so the pause mark clears on the poll after the immediate one; that is not a failure and nothing is shown for it. After **Approve**, Paperclip starts a run on the agent that asked (row 6), so the orb goes from amber to working, not to asleep. And a **Wake** is a real run that can fail at once (row 11): the orb then wears the Broke mark with Paperclip's reason, and a phone with "Agent broke" on gets that push. Nothing hides this; it is what happened. Whether Wake should be offered at all for an agent with nothing waiting is put to the lead as a product question, and the menu rule above stands until it is answered.

**Per-file change list (desktop).**

| File | Change |
| --- | --- |
| `PaperclipActions.cs` (new) | The seam, the outcome mapping (pure, a function from status code and body to `PaperclipOutcome`), the implementation. |
| `PaperclipApi.cs` | `IPaperclipApi` gains `PostAsync(path, body)` returning status and body, and `GetApprovalAsync(id)`. `RawApproval` gains `Summary` (`payload.summary`) and `CreatedAt`; `PaperclipNeed` gains `Summary` and `CreatedAt` (both optional, defaulted, so release 1's call sites compile). |
| `PaperclipHttp.cs` | The POST, with the same typed failures as GET. |
| `PaperclipSessions.cs` | `Actions`; `PollNow()`; the mark-transition list the relay reads (§13.3). |
| `PaperclipOrb.cs` | `MenuOffer` with the three rows; `NeedOffer(need)` (buttons or link only); the click rule. |
| `SessionManager.cs` | `SessionStatus.Needs`; filled in the Paperclip merge. |
| `ClickRouting.cs` | An amber Paperclip orb with an answerable need opens the flyout. |
| `OrbFlyout.axaml(.cs)` or a new `PaperclipNeedsPanel` hosted by it | The blocks. The engineer picks whichever keeps `OrbFlyout`'s arc untouched; the panel is a control that takes `Needs` and an `IPaperclipActions`, so the UI suite drives it with a fake. |
| `OrbWindow.axaml(.cs)` | Three menu rows and their handlers. |
| `PaperclipPrompts.cs` (new) | §13.4. |
| `RelayLink.cs`, `RelayAlertRules.cs`, `RelayActivity.cs`, `RelayRoster.cs` | §13.3, §13.4. |
| `README.md` | The Paperclip section: answering in place, the three menu rows, the two phone alerts and where to mute "broke". |
| `tests/UnitTests/Fixtures/relay/` | The v16 and prompt vectors, byte-identical to the mobile copies. |

No hook, installer or packaging file changes, as in release 1; the five install paths were checked for release 1 and nothing here adds a file outside the app binary.

### 13.3 The "broke" push: `frames-v16.json` (Orbweaver-Mobile, contract first)

```json
{
  "version": 16,
  "note": "CB-428: one new push value, broke, for a Paperclip agent that stopped working. Additive over frames-v4's state pushes and frames-v15. No new sealed frame, roster field, state, class, error code or caps name; hello and push_mute are byte-identical. Also records one narrowing of frames-v15's prompt rule.",
  "envelope": {
    "push_values": ["none", "amber", "resolve", "done", "limit", "broke"],
    "rules": "broke is accepted only with class state, desktop to phone, like done and limit. e is optional with the frames-v3 grammar.",
    "frame": { "id": "<ULID>", "class": "state", "push": "broke", "ck": "Ck(ck_key, session_id)", "ct": "an ordinary numbered frames-v2 delta for the agent's row", "e": "<sealed>" }
  },
  "relay": {
    "route": "the done and limit route: token lookup, the 30 s window per (push, from, ck, to), no reminder, no /v1/ack, no WasPushed entry",
    "replies": "ok pushed, ok push_throttled, ok push_no_token, err push_failed. Never push_muted: broke is not a relay mute class.",
    "apns": { "title": "Orbweaver", "body": "An agent stopped working", "interruption-level": "active", "mutable-content": 1, "category": "none", "sound": "none", "content-available": "absent", "priority": 10 },
    "push_mute": "unchanged: distinct names from done and limit. broke there is bad_hello, as any unknown name is."
  },
  "e": {
    "plaintext": { "v": 1, "t": "broke", "session_id": "paperclip:<agent id>", "label": "<agent name> on <machine>", "title": "<agent name>", "why": "run_failed | timed_out | error" },
    "rules": "No persona. No text from Paperclip: an agent's error reason can hold a path. A why the phone does not know is shown as the fallback line."
  },
  "prefs": {
    "frame": { "t": "prefs", "done_alerts": "all", "amber_members": true, "broke_alerts": true },
    "rules": "broke_alerts is an optional boolean beside CB-358's two keys. The desktop sends a broke push to a phone only when that phone's stored prefs say true. Absent is false. A desktop that does not know the key ignores it."
  },
  "desktop": {
    "when": "the poll in which a Paperclip agent's mark becomes Broke, when the previous successful poll in this process showed it not Broke and the agent is a row in the roster. The first poll after a start or a reconnect sets the baseline and sends nothing. An agent that stays Broke sends nothing more; it is armed again when its mark leaves Broke.",
    "ct": "the delta this scan already has for the row, else a delta restating the row's state and since",
    "own_wake": "no broke push for an agent whose Wake was pressed on this desktop in the last 30 s",
    "never": "for a finished run. A paperclip row's generating to idle with no Broke mark is a delta with push none.",
    "old_relay": "err bad_envelope for the envelope's id: the desktop sends no more broke pushes until its next welcome. The phone recovers the lost delta by its ordinary gap resync."
  },
  "phone": {
    "rewrite": "title \"Orbweaver · <label>\"; body by why: run_failed \"Last run failed\", timed_out \"Last run timed out\", error \"Is in an error state\", anything else \"Needs attention\". push and t must agree or the relay's text stands.",
    "actions": "none. A tap opens the agent's row. Nothing resolves it and no resolve push follows.",
    "setting": "\"Agent broke\", on by default, sent as broke_alerts.",
    "watch": "the mirrored notification, no action",
    "old_builds": "never sent one, because they never send broke_alerts. One that reached an old iOS build would show the relay's text with no buttons."
  },
  "activity": "unchanged from frames-v15: a broke agent is not amber and starts no Live Activity.",
  "prompt_narrowing": "frames-v15 said one prompt per pending approval or tool action. A desktop sends at most one pending prompt per session_id at a time, the oldest need first, and the next when that one resolves."
}
```

Deploy order: the contract PR, the relay, then desktop and phones in either order. Each pair of old and new is safe: an old relay refuses the envelope and the desktop stops (above); an old phone is never sent one; an old desktop never sends one and ignores the prefs key.

Vectors (`contracts/frames-v16-vectors.json`, `make_v16_vectors.py`, `check_v16_vectors.py`, `test_check_v16_vectors.py`): the envelope, the APNs body bytes, the sealed `e` for each `why`, the prefs frame with and without the key; mutations that must fail: `broke` with class `action`, phone to desktop, `push_mute: ["broke"]`, an `e` whose `t` is `done`.

Per-file (mobile): `relay/Relay/Frames.cs` (the value and its class rule), `relay/Relay/Push.cs` (the route), `relay/Relay/Apns.cs` (`ApnsPayload.Broke`), `relay/tests/RelayTests/` (a `V16PushTests` beside `V4PushTests`); iOS `app/Relay/PushRewrite.swift` (`StatusRewrite.Kind.broke`, `BrokePlaintext`), `app/Relay/AlertPrefs.swift`, the settings screen, `app/NSE/NotificationService.swift` only if the kind switch needs an arm, the watch's mirrored text; Android `Prompts.kt` (`BrokePlaintext`), `Notifier.kt` (a channel), `ShellPrefs.kt` and the settings row, and wherever Android sends prefs.

Per-file (desktop): `RelayAlertRules.cs` (`AlertPrefs.BrokeAlerts`, parsed, stored, default false; `PushBroke(prefs)`), `RelayLink.cs` (`OnScan` mints it; `BrokeE`; the `bad_envelope` latch in `OnControl`), `PaperclipSessions.cs` (a pure `BrokeTransitions(previous, current)` returning the agent ids and `why`, with the baseline rule).

### 13.4 The answerable prompt (frames-v15 `prompt`, sent from release 2)

`PaperclipPrompts.cs`, beside `PromptPipeline`, pure over its inputs (the agents' needs, the roster's session ids, the clock) with the send and the action behind interfaces.

| Rule | Decision |
| --- | --- |
| Which needs | `approval` and `action` with a real Paperclip id. Never `blocked`, never the `pending` synthetic need. Only for an agent that is a roster row. |
| Identity of a need | `kind + "\n" + id`. `screen_hash` as §4. |
| `request_id` | A ULID minted the first time the need is seen, kept in memory by identity, so every later poll reuses it. A restart mints again and alerts again, as a terminal prompt does; named as a limit, not fixed here. |
| Several needs on one agent | One prompt at a time, the oldest by `createdAt` then id. The next is minted when the first resolves. |
| `tool`, `preview` | `PaperclipApi.ApprovalWording(type)` or "Tool action"; `title + "\n" + summary` cut to 4000. |
| Push | amber, unless the phone's prefs withhold ambers for members (`RelayAlertRules.PushAmber`, where a member is an agent whose lead is a row). |
| The need leaves the pending list | `resolved` `by: "desk"` when it was answered from the flyout or Paperclip shows it decided; `by: "gone"` when the agent is terminated or its row leaves the roster. |
| A valid answer | `DecideAsync(need, action == "approve")`. `Done`: `resolved by phone:<id>`. `AlreadyDecided`: `err not_pending`, then `resolved by desk`. `Refused`, `Unreachable`, `NeedsReconnect`: `err stale_prompt`, the prompt stays pending. |
| An answer for a prompt already resolved | the gate's existing `not_pending`; nothing is called. |
| Any other action | `err forbidden_option`. |
| `expires_at` passes with the need still pending | `resolved by gone`, then a fresh prompt sent with no push. |
| Activity counts | a Paperclip row is amber only while it has a pending prompt (`RelayActivity.cs`, which excluded it entirely in release 1). |

Prompt vectors (mobile `contracts/`, copied to the desktop): a board approval, a hire and a tool action prompt; an approve and a deny answer for each; the three `resolved` forms; `not_pending`, `stale_prompt`, `forbidden_option`; a watch-signed approve.

Phones (CB-428): the prompt is an ordinary frames-v3 prompt, so nothing new is parsed. The work is that a Paperclip row's view, read-only in release 1, shows the pending prompt with Approve and Decline, the notification and the watch offer both, and `stale_prompt` leaves it answerable with the desk's sentence.

### 13.5 Test matrix (release 2)

100% of added lines. Named exception for the PR body: the real `HttpClient` POST construction. Fixtures for the tool action routes are in `tests/UnitTests/Fixtures/paperclip/native/tool-actions/`, captured 2026-11-23 and scrubbed (ids remapped, user ids replaced, `signedArguments` removed).

| Level | Class | Cases |
| --- | --- | --- |
| Unit | `PaperclipApiTests` (extend) | The captured `tool-actions/action-requests-pending.json` parses to its rows with id, title, summary, created and expiry (this fails on `develop`, row 12h); a row naming no agent lands on the lead; the flat release 1 shape still parses; markdown stripped. |
| Unit | `PaperclipOutcomeTests` | One case per row of the outcome table for each of the four calls; the 4xx-then-read path for both read answers; each captured tool action answer in `tool-actions/` (200 failed, 200 again, both 409s, 400, 404) maps to its outcome; a `blocked` and a `pending` need make no request; `Why` is cut at 140 and never carries a path-shaped string from a 5xx body. |
| Unit | `PaperclipOrbDecisionTests` (extend) | `MenuOffer` for each of running, idle, paused, over budget, broke, terminated, and each link state; `NeedOffer` per kind; the click rule for answerable, link-only and mixed needs. |
| Unit | `PaperclipPromptsTests` | Mint once across three polls; nothing for blocked, pending or a non-roster agent; two needs give one prompt then the second; each resolve cause; each outcome's reply; expiry; the member-amber pref; restart mints again. |
| Unit | `PaperclipBrokeTests` | Baseline sends nothing; None to Broke sends once with each `why`; Broke through three more failed runs sends nothing; Broke, None, Broke sends twice; prefs absent, false and true; not a roster row; a Wake pressed 5 s before sends nothing and one pressed 31 s before sends; the `bad_envelope` latch and its release on welcome; a finished run with no mark sends push none (the quiet control). |
| Unit | relay vector tests | Every v16 and prompt vector re-serialises byte-identically. |
| Integration | `PaperclipHttpTests` (extend) | Each POST's path and body byte-exact against the loopback server; `Authorization` present; 401, 409, 422, 500, hung and dropped map to the typed failures. |
| UI | `PaperclipNeedsPanelTests` | With a fake `IPaperclipActions`: a block per need; link-only kinds have no buttons; a click calls `DecideAsync` once with the right need and flag; buttons disabled while the call is out; each outcome's result on screen; the panel follows a new `UpdateFrom`. |
| UI | `PaperclipOrbTests` (extend) | The three menu rows' visibility; arm then confirm for Pause and Resume; Wake calls once; a failure's note. |
| Screenshot | `PaperclipOrbScreenshots` | (1) the flyout with an approval and a tool action; (2) the same after a refused answer, with its sentence; (3) the orb menu on a running and on a paused agent. |
| Contract | `test_check_v16_vectors.py`, prompt vector checks, `relay-tests` | §13.3, §13.4. |
| Relay | `V16PushTests` | Accepted with class state; refused with class action and from a phone; APNs body bytes; throttle; `push_mute` with `broke` is `bad_hello`; done and limit unchanged. |
| Phone | iOS and Android unit suites | The broke rewrite per `why` and the disagreeing-`t` case; the prefs frame with the key; the Paperclip row with a pending prompt; the watch context. |

### 13.6 Real-machine proof (QA; minutes each, paired)

Install owner: QA. Test approvals are created for the purpose (`POST /api/companies/{id}/approvals`, type `request_board_approval`); nothing real is decided, and every agent is left in the state it was found.

0. **Ask first.** Mint a tool action request with the test call (§13.1 row 12); the lead's orb goes amber with the tool's name; Approve; the request leaves Paperclip's pending list. Decline a second one. They expire in an hour, so mint them when needed.
1. **Approve in place.** Create a test approval; the lead's orb goes amber; Approve in the flyout; `GET /api/approvals/{id}` says `approved` and the orb leaves amber within one poll. About a minute. **Paired negative:** revoke the app's key, create another, press Approve: the block stays, with the reconnect sentence, and Paperclip still says `pending`.
2. **Decline, and answered elsewhere.** Decline one from the flyout. Approve another in Paperclip's own page while the flyout is open, then press Approve: "Already answered in Paperclip", no error styling.
3. **Wake, Pause, Resume.** Wake an idle agent that has a task: a working orb within about 2 s. Wake one with nothing waiting: the Broke mark with Paperclip's reason, and no push on a paired phone with "Agent broke" on (the paired positive is proof 5). Pause: the pause mark, and Paperclip's page says paused. Resume: the mark clears. Window sized to three times the run, not to a round number.
4. **Phone, needs you.** On the staging relay with a current build: a test approval produces exactly one push; Approve from the notification; Paperclip says `approved`; the notification clears. Repeat from the watch. **Paired negative:** a run that finishes cleanly produces no push (judge from `mirror-log.txt`, turns under 3 s do not count).
5. **Phone, broke.** With "Agent broke" on: an agent's failed run produces one push with the agent's name and no buttons; a second failed run on the same agent produces none. **Paired negative:** the switch off, the same failure, no push.
6. **Old phone build**, if one is installed anywhere: the answerable prompt arrives and works; no broke push arrives.
7. **Windows and the MacBook:** proofs 1 and 3 repeated on each, since the flyout and menu are the platform-specific surfaces.

### 13.7 Work packages (release 2)

| Package | Owner | Files |
| --- | --- | --- |
| **R. Contract, relay, desktop relay side** | Wren Asare | Mobile: `contracts/frames-v16*`, the prompt vectors, `relay/`. Desktop: `PaperclipPrompts.cs`, `RelayLink.cs`, `RelayAlertRules.cs`, `RelayActivity.cs`, `RelayRoster.cs`, the relay fixtures and their tests. |
| **D. Desktop actions** | Juno Abernathy | `PaperclipActions.cs`, `PaperclipApi.cs`, `PaperclipHttp.cs`, `PaperclipSessions.cs` (`Actions`, `PollNow`), `PaperclipOrb.cs`, `SessionManager.cs`, `ClickRouting.cs`, the flyout panel, `OrbWindow.axaml(.cs)`, `README.md`, their tests and screenshots. |
| **P. Phones and watch** | Hana Moriyama | iOS, watchOS and Android as §13.3 and §13.4; the TestFlight build. |

Two shared files: `PaperclipActions.cs` is created by whoever lands first, with exactly the interface above, and `PaperclipSessions.cs` takes `BrokeTransitions` from R and `Actions`/`PollNow` from D in separate regions. Everything else has one owner.

### 13.8 Not in release 2

Wake, pause or resume from the phone; commenting on a task; sending an approval back for revision; a trust rule from a tool action; keeping a prompt's id across a desktop restart; several companies side by side.
