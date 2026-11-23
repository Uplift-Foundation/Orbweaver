# Paperclip agents as orbs: implementation plan (CB-421 desktop, CB-422 contract)

Epic CB-419. Desktop branch `feature/paperclip-orbs`, cut from `develop` at `c9b5bc047`. Mobile read at `Orbweaver-Mobile` `origin/develop` `7155aa3`. Every line reference below is against those two shas. Everything in §1 was measured on 2026-11-22 against Paperclip `2026.1117.0` (authenticated mode) on the Mac mini, or read from its installed server source where it says so.

Scrubbed captures of everything measured are already committed in `tests/UnitTests/Fixtures/paperclip/` (§9). Engineers do not need the live instance to write the parsers.

## 0. What changed from the approved plan, and why

| Approved plan said | This plan says | Evidence |
| --- | --- | --- |
| WebSocket for `agent.status` / `heartbeat.run.status` when available | **REST polling only in v1.** No WebSocket. | A board API key cannot open the socket: 403 as `Authorization: Bearer` and as `?token=`, while the browser session cookie on the same URL gets 101 (§1.2). The upgrade path only looks bearer tokens up in the *agent* key table. |
| Poll every 5 s | **Poll every 2 s** | 13 recorded runs lasted 0.4 to 99 s; 2 of 13 were under 5 s, so a 5 s poll never shows them working (§1.4). |
| Dedupe on `PAPERCLIP_AGENT_ID` in the hook env | **Dedupe on `sdk-cli` + cwd, then pid / session id** | The hook does fire for `claude_local` runs, but the status file records no env. It does record `entrypoint`, `cwd` and `session_pid`, and the run row carries the same pid and session id (§1.5). |
| Next contract file is `frames-v14` | **`frames-v15.json`** | `contracts/frames-v14-login.json` (CB-412) landed on mobile `develop` after the plan was written. |
| `amber` push for a pending approval | **No push of any kind for a Paperclip row in v1** | An amber is a sealed `prompt` with a `request_id` the phone answers. Paperclip approvals cannot be answered through it, so an amber would be a notification with dead buttons. The row still shows `waiting`. |
| Reuse the Cron/Heartbeat cluster and badge for scheduled agents | **Dropped for v1** | Knowing an agent is "scheduled" needs the routines endpoint, whose shape was not measured. The org chart already groups the orbs. |
| `input` refused with `untypeable` | Kept, and `watch` answers an empty conversation (§6.2) | Today `watch` on a non-local id answers `unknown_session`, which both phones word as "That session is gone from the desk." |

## 1. Measured vs assumed

### 1.1 REST, measured

All with `Authorization: Bearer pcp_board_…` unless stated. No auth at all: `401 {"error":"Unauthorized"}` (the negative control for every 200 below).

| Call | Result |
| --- | --- |
| `GET /api/health` (no auth) | 200. `status`, `deploymentMode` (`authenticated` here), `bootstrapStatus`. |
| `GET /api/companies` | 200, array. Fields used: `id`, `name`, `status`, `issuePrefix`. |
| `GET /api/companies/{id}/agents` | 200, array of 4, 11.9 KB, 12 ms on loopback. Fields used: `id`, `name`, `role`, `title`, `status`, `reportsTo`, `adapterType`, `budgetMonthlyCents`, `spentMonthlyCents`, `pauseReason`, `errorReason`, `lastHeartbeatAt`, `urlKey`. **Also carries `adapterConfig` with local paths and tool lists: never log this payload.** |
| `GET /api/companies/{id}/live-runs` | 200. `[]` when idle (2 bytes). During a run: one row with `id`, `agentId`, `status:"running"`, `startedAt`, `issueId`, `invocationSource`, `currentStatusMessage`, `currentToolName`. **No pid and no session id.** |
| `GET /api/companies/{id}/heartbeat-runs` | 200, newest first. Query params that work: `limit`, `agentId`, `summary=true`. Full rows are ~5 KB each and carry `processPid`, `sessionIdBefore`, `sessionIdAfter`, `status`, `error`, `errorCode`, `exitCode`, `startedAt`, `finishedAt`, `issueId`. **`summary=true` nulls `processPid` and both session ids** (and still costs ~1.8 KB a row). |
| `GET /api/cli-auth/me` | 200, `source:"board_key"`. A one-call key check. |
| `POST /api/board-api-keys` (cookie session + `origin` header) | 201 with `token` (`pcp_board_` + 48 hex) and the `expiresAt` that was asked for. |
| Bad bearer | 401 `Agent token did not verify…` |
| Same key over the LAN address | 200. |

Statuses seen in recorded runs: `succeeded`, `failed` (`adapter_failed`, `provider_session_open_rejected`), `cancelled` (`issue_dependencies_blocked`). Agent statuses seen: `idle`, `running`.

### 1.2 WebSocket, measured

`ws://host:3100/api/companies/{id}/events/ws`, six attempts in one script:

| Credential | Result |
| --- | --- |
| none | 403 |
| `Authorization: Bearer <board key>` | 403 |
| `?token=<board key>` | 403 |
| browser session cookie | **101**, first frame `{"type":"live.capabilities",…}` |
| cookie + foreign `Origin` | 101 |
| cookie, company id that does not exist | 101 (the user is an instance admin) |

With the cookie, one wake produced, in order: `heartbeat.run.queued`, `heartbeat.run.status` running, `agent.status` running (0.2 s after the run status), `heartbeat.run.progress`, `heartbeat.run.status` succeeded (twice), `agent.status` idle. The scrubbed sequence is `ws-events-one-wake.jsonl`. It is kept as a record, not as a fixture any v1 code reads.

Polling at 0.5 s saw the same transitions 0.3 to 0.5 s after the socket did. The socket buys under a second and costs a credential a non-browser client cannot hold.

### 1.3 Auth, measured and read

- **Measured:** a board key works as a Bearer on every REST call above, from loopback and the LAN. It does not work on the WebSocket.
- **Read from source, not exercised:** keys default to a **30-day** expiry (`BOARD_API_KEY_TTL_MS`); `expiresAt: null` in the create body means no expiry. `local_trusted` deployments accept requests with no credential at all. A CLI approval flow exists (`POST /api/cli-auth/challenges`, approve in a browser, poll for a key).
- **Consequence:** a board key is full board power (it can wake agents and spend budget). There is no read-only board key. v1 only ever issues `GET`.

### 1.4 Timing, measured

- 13 runs with both timestamps: 0.4, 3.8, 9.0, 10.3, 15.2, 15.7, 27.1, 29.5, 30.8, 32.0, 35.7, 63.3, 99.2 s.
- One wake of an agent with no issue: `running` visible over REST from 0.5 s after the POST until 4.2 s after it.
- This is one small org on one evening. It is not a claim about run lengths in general, only that short runs exist.

### 1.5 The hook during a `claude_local` run, measured

The run's Claude process ran the ordinary hooks and wrote an ordinary status file, three times in 2.6 s, then removed it:

- `entrypoint: "sdk-cli"`, `cli: "claude"`, empty `tty`, `term_program`, `tmux_*`.
- `cwd`: `~/.paperclip/instances/default/workspaces/<agentId>`. **The agent id is the last path segment.**
- `session_pid` **equals** the run row's `processPid`. The status file's name (the Claude session id) **equals** the run row's `sessionIdAfter`, and sessions are reused across runs (`sessionIdBefore == sessionIdAfter` on the measured run; one session id served three earlier runs).
- Transcript directories under the Claude config root show two cwd shapes across 5 sessions: `…/instances/default/workspaces/<uuid>` (1) and `…/instances/default/isolated-workspaces/<companyId>/<uuid>` (4, issue-scoped runs).
- The status file holds nothing from the environment. `PAPERCLIP_AGENT_ID` is not in it.

Captured as `hook-status-during-run-{1,2,3}.json`; the matching run is the newest row of `heartbeat-runs.json`.

### 1.6 Assumed, and who settles it

| Assumption | Settled by |
| --- | --- |
| `processPid` is already set on the run row **while** it is running (only read after it finished) | Engineer A's probe, `runs --watch` during QA's wake |
| `live-runs` also lists `queued` runs | Parser treats `queued` and `running` the same, so either answer is right |
| Agents on `paperclip_runner` (ACP) fire no hook | Not needed: the source works with no hook by construction |
| A project workspace outside `~/.paperclip` gives a cwd the path rule misses | Covered by the pid / session rule; unmeasured |
| Agent page is `<base>/<issuePrefix>/agents/<agentId>` (read from server-generated links) | QA clicks one orb |
| `paused`, `error`, `pending_approval`, `terminated`, `active` look as the source enum says | Unit tests on hand-edited copies of the real `agents.json`, labelled as edited |
| `expiresAt: null` mints a key that never expires | Stated as "read from source" in the settings copy's doc, not promised in the UI |
| No-credential access in `local_trusted` mode | Empty key sends no `Authorization` header; untested |
| Windows and a LAN instance behave as loopback did | QA real-machine proof on the MacBook and the Windows box against `192.168.0.127:3100` |

## 2. Desktop design

### 2.1 Shape

Copy `ClaudeCloudSessions.cs` (poll loop, `Snapshot()`, `SetSnapshotForTests`, `Restart()`, settings gate inside `Snapshot()`), not `OpenClawSessions`.

```
PaperclipApi.cs          pure: JSON -> records, state mapping, dedupe rule, URL building. No sockets, no settings.
PaperclipSessions.cs     the loop: StepAsync(api, clock, state) -> StepResult; Snapshot(); StatusText; Restart().
PaperclipCredentials.cs  the board key, 0600, outside settings.json.
```

Interface the two engineers build against (names are binding, bodies are not):

```csharp
internal sealed record PaperclipAgent(
    string Id, string Name, string Role, string Title,
    string Status,                 // raw agent.status
    string? ReportsTo,             // agent id or null
    string AdapterType,
    string State,                  // "idle" | "generating" | "waiting"  (PaperclipApi.StateOf)
    OrbPresence Presence,          // Present | Parked | NeedsInput
    string? StatusDetail,          // one line, see 2.3
    string? RecentAction,          // live run's currentStatusMessage / currentToolName, else null
    DateTime LastActivity,         // run startedAt while running, else lastHeartbeatAt, else createdAt
    string Url,                    // "<base>/<issuePrefix>/agents/<id>"
    int? RunPid, string? RunSessionId);   // of the live run, when the full row has been read

internal static class PaperclipSessions {
    public static IReadOnlyList<PaperclipAgent> Snapshot();   // empty unless paperclipEnabled
    internal static void SetSnapshotForTests(IReadOnlyList<PaperclipAgent> agents);
    internal static string StatusText { get; }                // the settings status line
    public static void Restart();
    internal static IReadOnlySet<int> KnownRunPids();         // for dedupe, 2.5
    internal static IReadOnlySet<string> KnownRunSessionIds();
}
```

### 2.2 Poll loop

One tick, every `PollInterval = 2 s` while healthy:

1. `GET agents` and `GET live-runs` (about 12 KB together).
2. `GET heartbeat-runs?limit=20` (full rows, about 70 KB) **only** when this tick's `live-runs` is non-empty, or any agent's `lastHeartbeatAt` differs from the previous tick, or it is the first successful tick. It supplies the live run's pid and session id, and each agent's most recent finished run.
3. Publish a whole new snapshot (replace, never mutate).

Company: `paperclipCompanyId` if set; otherwise `GET companies` once per connect and use it when there is exactly one. More than one and none chosen: no orbs, status line says to pick.

Failure: backoff 2, 4, 8, 16, 30 s. The last good snapshot is kept for `DisconnectGrace = 15 s` of consecutive failures, then emptied. A 401 or 403 empties it at once. `StepAsync` takes the API, the clock and the previous state as arguments, as `ClaudeCloudSessions.StepAsync` does, so every branch is a unit test.

`HttpClient` with a 5 s timeout. Never log a response body or the key; log status code, byte count and elapsed time.

### 2.3 Paperclip to `SessionStatus`

`PaperclipApi.StateOf(agentStatus, liveRunStatus?)`, first match wins:

| # | Input | `State` | `Presence` | `StatusDetail` |
| --- | --- | --- | --- | --- |
| 1 | agent `terminated` | not in the snapshot | | |
| 2 | live run `running` or `queued` (any agent status but 1) | `generating` | Present | issue id if `issueId` set, else `invocationSource` worded: "Working (on demand)" |
| 3 | agent `running`, no live row | `generating` | Present | "Working" |
| 4 | agent `pending_approval` | `waiting` | NeedsInput | "Waiting for board approval" |
| 5 | agent `paused` | `idle` | Parked | "Paused" + `: <pauseReason>` when set |
| 6 | agent `error` | `idle` | Present | "Error" + `: <errorReason>` when set |
| 7 | agent `idle` or `active` | `idle` | Present | last-run line below |
| 8 | any other string | `idle` | Present | the raw status |

Run statuses, as the live row (rule 2) or the agent's newest finished row (the last-run line for rules 6 to 8):

| Run status | As live row | As last-run line |
| --- | --- | --- |
| `queued` | generating | n/a |
| `running` | generating | n/a |
| `scheduled_retry` | not live: falls to agent status | "Retry scheduled" |
| `succeeded` | n/a | "Last run succeeded" |
| `failed` | n/a | "Last run failed: <errorCode>" |
| `timed_out` | n/a | "Last run timed out" |
| `cancelled` | n/a | "Last run cancelled: <errorCode>" when set |
| `interrupted` | n/a | "Last run interrupted" |
| anything else | not live | the raw status |

Only `errorCode` is shown, never `error`: the measured `error` strings carry commands and JSON. Spend is appended as " · $0.26 of $50.00" only when `budgetMonthlyCents > 0` and `spentMonthlyCents > 0` (it was 0 on this instance despite real runs, because billing was subscription-included).

Other `SessionStatus` fields, set in the merge block: `Source = Paperclip`, `Kind = Unknown` (no kind badge), `Title = Name`, `Cwd = ""`, `Color = OpenClawSessions.ColourForAgent(Id)`, `Url`, `StatusDetail`, `RecentAction`, `Presence`, `Lead` (2.4). `ScanEntry.Written = LastActivity`, never `now` (same reason as the cloud block, `SessionManager.cs:2760` on).

### 2.4 Keying, lead, removal

- **Key:** `"paperclip:" + agent.Id`. Agent ids are UUIDs, unique across companies.
- **Lead:** `status.Lead = "paperclip:" + ReportsTo` when `ReportsTo` names an agent in the same snapshot; otherwise empty. A pure function `PaperclipApi.LeadOf(agent, idsInSnapshot)`. The precedent for a lead with no process is the OpenClaw room at `SessionManager.cs:2582`. Chains (a report of a report) are passed through as they are; `ArrangementTests` already covers nested teams.
- **Removed when:** the agent is `terminated` or absent from a successful listing; the source is switched off (`Snapshot()` returns empty at once); the grace in 2.2 runs out; the key is rejected.
- **Never removed by the clock:** add `&& status.Source != SessionSource.Paperclip` beside the cloud exemption at `SessionManager.cs:1491`. An agent between runs is the normal state, not a husk.
- Hidden-orb and group rules need no special case beyond the labels in §3.

### 2.5 Dedupe with hook sessions

`PaperclipApi.IsRunProcess(entrypoint, cwd, sessionPid, sessionId, runPids, runSessionIds)`, true when `entrypoint` is `sdk-cli` (case-insensitive, reuse `OpenClawCliSession.SdkEntrypoint`) **and** any of:

1. `cwd` has the consecutive segments `.paperclip`, `instances`, `<any>`, then `workspaces` or `isolated-workspaces`. Measured for both run kinds. Needs no request, so it has no race with the poll.
2. `sessionPid` is in `runPids`.
3. `sessionId` is in `runSessionIds` (`sessionIdBefore` and `sessionIdAfter` of the rows read in 2.2 step 2).

`sdk-cli` alone is every `claude -p` anyone runs, and a `.paperclip` cwd alone is a person's own terminal in a workspace; both halves are required, as `OpenClawCliSession.Spawned` requires both of its own.

A matching local row is **not drawn** when `paperclipEnabled` is on **and** the source has a non-empty snapshot. Call site: beside `OpenClawCliSession.ShowHere` at `SessionManager.cs:3070`. With Paperclip off, or connected to nothing, behaviour is exactly today's (the transient Claude orb), so the feature is a strict no-op for anyone who has not enabled it. The hook row's state is not merged into the Paperclip orb in v1; Paperclip's own state is at most one poll behind it.

## 3. Desktop: per-file change list

New files:

| File | Content |
| --- | --- |
| `PaperclipApi.cs` | Records; `ParseAgents`, `ParseLiveRuns`, `ParseRuns`, `ParseCompanies` (tolerant: unknown fields ignored, a row missing `id` skipped, never throws on shape); `StateOf`; `DetailOf`; `LeadOf`; `IsRunProcess`; `AgentUrl`; `Compose(agents, liveRuns, runs, baseUrl, issuePrefix) -> IReadOnlyList<PaperclipAgent>`. |
| `PaperclipSessions.cs` | §2.2. `IPaperclipApi` seam with the three GETs; the real one over `HttpClient`. |
| `PaperclipCredentials.cs` | `KeyFor(baseUrl)`, `SetKeyFor(baseUrl, key)`; file `paperclip-credentials.json` in `OrbweaverSettings.Directory`, written then `File.SetUnixFileMode(… UserRead | UserWrite)` off Windows. Copy `OpenClawIdentity.cs:107-186`. Do not reuse `openclaw-devices.json`. |
| `tools/paperclip-probe/` | Read-only console app over `PaperclipApi` + the real `IPaperclipApi`, like `tools/openclaw-probe`. Verbs: `agents`, `runs`, `runs --watch` (prints pid and session id each tick), `check` (health + `cli-auth/me`). Takes `--url` and reads the key from `PAPERCLIP_BOARD_KEY`. Prints names, statuses, ids; never `adapterConfig`. |
| `docs/paperclip-findings.md` | §1 of this plan, in the voice of `docs/openclaw-findings.md`, updated with whatever the probe settles from §1.6. |

Existing files (lines at `c9b5bc047`):

| File | Change |
| --- | --- |
| `SessionManager.cs:342` | `SessionSource.Paperclip`, with a comment in the enum's own style. **Not** added to `IsLocalCli` (`:162`). |
| `SessionManager.cs:1491` | Stale-timer exemption (2.4). |
| `SessionManager.cs:~2775` | New merge block after the cloud block in `ScanAndUpdateCore`: one `ScanEntry` per `PaperclipSessions.Snapshot()` row (2.3). |
| `SessionManager.cs:3070` | Dedupe (2.5). |
| `SessionManager.cs:3652` area | A click on a Paperclip orb must not build a chat session. Confirm what the default arm does for a non-local source with no chat and make it "no panel". |
| `ClickRouting.cs:165` | `OpensInABrowser`: `ClaudeCloud or Paperclip`. An empty `Url` opens nothing. |
| `CliMark.cs:76` | `Paperclip` style and its arm in `For(SessionSource)`. A paperclip outline as one filled path in the 16×16 box; fill `#5B5BD6` (indigo: none of terracotta, green, near-black, lobster red). The glyph is judged in the screenshot on both rids. |
| `HiddenOrbs.cs:117` | `SessionSource.Paperclip => "Paperclip agent"`. |
| `OrbAutoGroups.cs:46` | `ClassifyCli`: key `paperclip`, name `Paperclip`. `IsClaude` unchanged. |
| `AutoGroupAccounts.cs:75` | Paperclip has no account: confirm it returns null, add a case to the existing test. |
| `LocalPersona.cs:193` | Add `SessionSource.Paperclip` to the no-local-persona guard. |
| `CliChatFormat.cs:38` | No change. `For` is only reached for local CLIs; add a test that a Paperclip status never reaches it via the panel path. |
| `OrbWindow.axaml.cs:371` | Tooltip path line: show `Url` for Paperclip, as cloud does. |
| `OrbWindow.axaml.cs:472` | `NewChatHereItem` stays hidden (already false for this source; pin with a test). |
| `OrbWindow.axaml.cs:487` | `resetIdleExplanation` arm: "Paperclip controls this agent's state" / "This agent runs in Paperclip, so its state is changed there." |
| `OrbWindow.axaml.cs` menu | "Open in Paperclip" item, visible for this source only, same action as the click. |
| `OrbweaverSettings.cs:90` | Key list: `paperclipEnabled`, `paperclipUrl`, `paperclipCompanyId`. Model properties and static accessors in the `OpenClawEnabled` pattern (`:334`, `:1340`). Defaults: `false`, `http://127.0.0.1:3100`, `""`. |
| `SettingsWindow.cs:736` | `Group("paperclip", "Paperclip agents", Card(PaperclipRows()))` after OpenClaw. Rows: enable toggle; URL box; board key box (masked, write-only: shows "saved" not the value); company picker (filled from `companies` after connect, hidden when there is one); status `NoteRow`, refreshed where `_claudeCloudStatus` is (`:2245`). Each change calls `PaperclipSessions.Restart()`. A note under the URL when the scheme is `http` and the host is not loopback: "The key is sent unencrypted on this network." |
| `Program.cs:166` | `PaperclipSessions.Restart()` beside the cloud one. |
| `ScreenLockWait.cs:73` | Add the third supervisor to the comment's list. |
| `Orbweaver.csproj` | No new package. `InternalsVisibleTo` unchanged. |
| `README.md` | A "Paperclip" section: what it shows, how to mint a key (and that keys expire after 30 days unless minted with `expiresAt: null`), view-only. |
| `AGENTS.md` / `CLAUDE.md` | No change. |

Status line wording (`PaperclipSessions.StatusText`), each a test:

- off: "Paperclip agents are switched off."
- "Connecting to <host>…"
- "Connected: 4 agents in <company>."
- "Can't reach <host>: <reason>." On `EHOSTUNREACH` for a non-loopback host on macOS, append the Local Network hint `OpenClawGateway.ExplainConnectFailure` uses.
- "The board key was rejected. Keys expire after 30 days unless minted without an expiry."
- "This instance has N companies. Pick one."
- "No agents in <company>."

Install paths: no hook, script or installer file changes. `tools/build-macos-app.sh`, `tools/Orbweaver.iss` and both `install-hooks` scripts were considered and need nothing, because the feature is all in the app binary.

## 4. Relay contract: `frames-v15.json` (Orbweaver-Mobile, lands first)

### 4.1 Text

```json
{
  "version": 15,
  "note": "CB-422: Paperclip agents on the roster, view only. Additive over frames-v2's roster and frames-v4's watch/input. No new frame, no new field, no new state, no new error code, no caps name, nothing for the relay, and no version gate: \"15\" is this file's name in the series and nothing on the wire carries it.",
  "roster": {
    "cli": "may be \"paperclip\" beside \"claude\", \"codex\", \"grok\". A phone must treat any cli it does not know as a row it can show and cannot type to.",
    "paperclip_row": {
      "session_id": "\"paperclip:\" + the agent's id (a UUID). Stable across runs and desktop restarts.",
      "title": "the agent's name",
      "cwd": "\"\" (present, empty)",
      "state": "idle | generating | waiting, as for any row. generating: a run is queued or running. waiting: the agent is pending board approval. Everything else, including paused and error, is idle.",
      "since": "unix seconds of the last state change, as for any row",
      "parent_session_id": "\"paperclip:\" + the id of the agent this one reports to, only when that agent is a row in the same roster; otherwise omitted",
      "persona": "omitted in v1"
    },
    "order": "unchanged: rows sorted by session_id, ordinal",
    "removal": "an agent that is terminated, or whose Paperclip instance the desktop can no longer read, leaves by the ordinary removal delta"
  },
  "desktop_answers": {
    "watch": "ok watching with total 0, then no window and no run frame. The subscription holds nothing.",
    "unwatch": "ok unwatched",
    "input": "err untypeable, why \"Paperclip agents can't be typed to from a phone\"",
    "set_run": "err with the frames-v13 code for a session that has no run controls",
    "launch.parent_session_id naming a paperclip row": "err unsupported_parent"
  },
  "pushes": "none. A paperclip row's generating to idle is an ordinary delta with no push value, and a waiting row mints no prompt.",
  "activity": "paperclip rows are not counted in frames-v7 activity counts",
  "phone": {
    "new_builds": "no composer, no watch request, no run controls on a paperclip row; the row shows title, state, machine and its place under its parent. Not offered as a parent for a new session or a team join.",
    "old_builds": "decode the roster unchanged (cli is a string on both platforms). The row opens to an empty conversation; sending is refused with the untypeable sentence."
  }
}
```

Engineer C confirms two strings against the files before writing vectors: the exact frames-v13 refusal code for `set_run` on a session without controls (`frames-v13.json`, `set_run` replies), and that `unsupported_parent` is what `RelayLaunch` answers for a non-Claude parent today.

### 4.2 Files in Orbweaver-Mobile (contract PR)

| File | Change |
| --- | --- |
| `contracts/frames-v15.json` | New, 4.1. |
| `contracts/roster-fixtures.json` | **Add** a fourth roster, `paperclip_roster` (one `claude` row, a Paperclip lead, two reports with `parent_session_id`, one in each of the three states) and `paperclip_deltas_after_roster` (generating to idle, then a removal). The three existing rosters and their bytes do not change, so no existing consumer moves. Regenerated by desktop `tools/relay-vectors` from the `feature/paperclip-orbs` build; `_source.orbweaver_sha` updated. |
| `contracts/check_vectors.py` | `rosters()` (`:281`) also walks `paperclip_roster`. New rules for a row whose `cli` is `paperclip`: `session_id` matches `^paperclip:[0-9a-f-]{36}$`, `cwd == ""`, no `persona`, a `parent_session_id` starts with `paperclip:`. `ROSTER_FIELDS` unchanged: that is the "no new field" promise, enforced. |
| `contracts/test_check_vectors_paperclip.py` | New, in the shape of `test_check_vectors_personas.py`: the real file passes; mutations each fail naming their rule (a non-empty cwd, a persona, a bad id, a parent outside the roster, a fourth state, a ninth field). |
| `contracts/README.md` | Rows for the new and changed files. |
| `.github/workflows/relay-tests.yml` | Run the new test where the other `test_check_*` run. |

### 4.3 Phone work that follows (pointers for the mobile engineers, CB-422)

Verified at `7155aa3`: `cli` is `String` in `app/Relay/RosterReducer.swift` (`Entry.cli`) and in `android/relay/.../Roster.kt` (`entry()`); the team-join lead filter at `app/Model/LiveMapping.swift:119` is already `cli == "claude"`, so Paperclip leads are excluded with no change; the detail rows at `app/Screens/SessionScreen.swift:201,225` print `s.cli` raw. Not re-read at `7155aa3` (from the lead's reading at `6602ebf`): `SessionScreen.swift` `canSend`, `SessionScreen.kt:255`, `Shell.kt`, `NewSessionScreen.kt`. Consumers of the fixture file to extend: `app/Tests/ShellTests/RosterContractTests.swift`, `android/relay/src/test/.../FramesAndRosterTests.kt`, `app/LiveTests/Vectors/` (vendored copy).

## 5. Relay: desktop side (same desktop branch)

| File | Change |
| --- | --- |
| `MirrorProtocol.cs:834` | `CliPaperclip = "paperclip"` and its arm in `CliFor`. Every other caller of `CliFor` (`RemoteMirrorServer`, `PromptPipeline`, `LaunchRunner`, `MirrorRun`) only ever passes a local CLI; add one test that says so for the peer roster, since that is the claim this change leans on. |
| `RelayRoster.cs:97-106` | `Project` admits `IsLocalCli || Source == Paperclip`. For a Paperclip row: `cwd ""`, `persona null`, `parent_session_id = Lead` only when `Lead` is the id of another admitted row. The Claude rule on `:105` is untouched. |
| `RelayRoster.cs:55` | `RelayDelta.Finished` must be false for a Paperclip row, so `RelayLink.cs:698` never mints a `done` push for one. Carry the row's cli (or a `Pushable` flag) to the delta where the book stamps it. |
| `RelayRemote.cs:150` | Keep `Local()`. Add `Listed(sessionId)` returning a Paperclip status. `Watch` (`:430`): Paperclip gives `Ok(…, Watching, sid, 0)` and returns. `Unwatch` (`:471`): ok. `InputAsync` (`:482`): `Err(untypeable, why)`. `SetRunAsync` (`:376`): the frames-v13 refusal. All four before the `Local()` check. |
| `RelayActivity.cs` | Exclude Paperclip rows from the counts. Find where the counts are taken from the snapshot; if they are taken from the projected rows, filter on `cli`. |
| `RelayService.cs:310` | No change expected: persona lookup returns null for an empty cwd. Pin with a test. |
| `tools/relay-vectors` | Emit `paperclip_roster` and its deltas through `RelayRoster`'s own serializer. |
| `tests/UnitTests/Fixtures/relay/roster-fixtures.json` | The regenerated file, byte-identical to the mobile copy. |

## 6. What old phone builds do

Read from the code at `7155aa3`, not run on a device:

- The roster decodes: `cli` is a free string on both platforms, no state is new, no field is new.
- The row draws with the Claude-default look and nests under its lead through the existing team forest.
- Opening it sends `watch`; the desktop answers an empty conversation (not `unknown_session`, whose phone wording is "That session is gone from the desk.").
- Typing sends `input`; the refusal's `why` is shown.
- No push ever arrives for the row, and Live Activity and Watch counts ignore it.

QA settles this on one real old build (§8). If no old build is installed anywhere, it is stated as unverified in the PR, not assumed.

## 7. Test matrix

Coverage target is 100% of added lines, per `CLAUDE.md`. Lines that cannot be covered: the real `HttpClient` construction in `PaperclipSessions` and `Process.Start` for the browser; name them in the PR body.

| Level | Suite / class | Cases |
| --- | --- | --- |
| Unit | `tests/UnitTests/PaperclipApiParseTests` | Each real fixture parses to the expected records (`agents.json`: 4 agents, 3 reporting to 1; `live-runs-running.json`: 1 run for the right agent; `heartbeat-runs.json`: the wake run's pid and session id; `heartbeat-runs-summary.json`: pid null). Tolerance: `[]`, `{}`, a row with no `id`, a number where a string is expected, truncated JSON: none throw. |
| Unit | `PaperclipStateTests` | One case per row of both tables in 2.3 (8 + 9), plus precedence: `paused` with a live run is generating; `terminated` with a live run is absent. Hand-edited statuses are edited copies of the real `agents.json`, and the test says so. |
| Unit | `PaperclipDetailTests` | Spend shown and hidden (budget 0, spent 0, both set); `error` never appears in any output; `errorCode` does. |
| Unit | `PaperclipLeadTests` | Lead present; lead terminated (empty); lead missing; self-reference (empty); three-level chain. |
| Unit | `PaperclipDedupeTests` | The three real hook files are run processes. Controls that must be **false**: `entrypoint: cli` in the same cwd (a person's terminal); `sdk-cli` in an ordinary repo with no pid or session match; `.paperclip/instances/x/triage` (not a workspace). Pid-only and session-only matches with an outside cwd are true. Windows path separators. |
| Unit | `PaperclipSessionsStepTests` | `StepAsync` with a fake API and clock: first tick fetches runs; a quiet tick does not; a `lastHeartbeatAt` change does; a live run does; backoff sequence; grace keeps then empties; 401 empties at once; more than one company and none chosen; disabled publishes nothing and makes no call. Every status-line string. |
| Unit | `PaperclipCredentialsTests` | Round trip per base URL; empty key removes; file mode is 0600 off Windows (skip the mode assert on Windows, say so). |
| Unit | `RelayRosterTests` (extend) | Paperclip row admitted with `cli:"paperclip"`, `cwd:""`, no persona; parent only when the lead is admitted; a hidden lead gives no parent; `Finished` false for Paperclip with a Claude control that is true; OpenClaw, cloud and Remote Control still excluded. |
| Unit | `RosterFixtureTests` (extend) | `paperclip_roster` and its deltas re-serialise byte-identically. |
| Unit | `RelayRemotePaperclipTests` | The four answers in 5, each paired with the unchanged answer for an unknown id (`unknown_session`) and for a local row. No `done` push on generating to idle; a Claude control still gets one. Activity counts exclude it. |
| Integration | `tests/IntegrationTests/PaperclipHttpTests` | The real `IPaperclipApi` against `tests/Shared/LoopbackHttp.cs` serving the fixtures: the `Authorization` header is sent when a key is set and absent when it is empty; 401, 500, a hung response (timeout), a dropped connection and non-JSON each come back as a typed failure; the URL paths requested are exactly the three in 2.2. |
| Integration | `OrbweaverSettings` round trip (extend) | The three new keys survive a save and load; an old file without them loads defaults; unknown keys still survive beside them. |
| UI | `tests/UiTests/PaperclipScanTests` (model: `CloudScanTests`) | `SetSnapshotForTests` with a lead and two reports gives three orbs keyed `paperclip:<id>` with the right leads; a state change updates in place; removing an agent removes its orb; disabling the setting removes all; an agent 3 hours quiet survives a scan with "Keep orbs for" at its minimum; a hook row matching 2.5 is not drawn while the snapshot is non-empty and **is** drawn when Paperclip is off (the control). In `[Collection("Settings")]`. |
| UI | `PaperclipOrbTests` | `UpdateFrom` a Paperclip status: CLI mark present, no kind badge, reset-idle disabled with the new wording, New Chat Here hidden, "Open in Paperclip" visible; `ClickRouting.OpensInABrowser` true; an empty `Url` opens nothing. |
| UI | `SettingsWindow` tests (extend) | The group exists; toggling calls `Restart`; the key box never shows a stored key; the http-on-LAN note shows and hides; the status line follows `StatusText`. |
| Screenshot | `tests/UiScreenshots` | One capture: a lead with two reports, one generating, one waiting. One capture: the settings group, connected. |
| Console | `ArrangementTests` | No new geometry. Run it once to confirm a three-orb team through the new source arranges as any team does; no new cases unless it fails. |
| Contract | `contracts/test_check_vectors_paperclip.py`, `relay-tests` | 4.2. |

## 8. Real-machine proof (QA, minutes, paired)

Install owner: QA. Take `/tmp/ow-install.lock` on the mini first, and hash-check each install.

1. **Probe, positive and negative:** `paperclip-probe check` with the real key is 200 with `source: board_key`; the same call with the last character of the key changed is 401.
2. **Orbs:** on the mini (loopback), the MacBook and the Windows box (LAN URL), four orbs draw linked to the lead. Read window positions back rather than judging by eye.
3. **Wake:** one `POST /api/agents/{id}/wakeup` on one agent with no issue. Its orb goes generating and back. The run lasts about 4 s, so a window of about 15 s proves it; `paperclip-probe runs --watch` during the same wake settles the pid assumption in §1.6.
4. **Dedupe:** during that wake on the mini, exactly one orb exists for the agent. Control: switch Paperclip off, wake again, and the transient Claude orb appears as it does today.
5. **Stopped instance:** not on the production instance. Point `paperclipUrl` at a closed port instead: orbs clear after the grace and the status line names the host.
6. **Phone:** a current build shows the rows nested under the lead and the state change from step 3; an old build, if one is installed, opens a row to an empty conversation.

Do not write to the instance beyond the one wake per step. It runs a production triage org on a schedule.

## 9. Fixtures already in the branch

`tests/UnitTests/Fixtures/paperclip/`, all from the live instance on 2026-11-22, scrubbed by one script: every UUID remapped to `00000000-0000-4000-8000-…` consistently across files (so the hook file's pid and session id still match their run row), home paths to `/Users/someone`, the company renamed, user ids nulled, `extraArgs` and skill lists replaced, every free-text field over 200 characters and every excerpt, result, context snapshot and log reference removed. Agent names are the persona names already published in `.profiles-assets`. Audited for the home name, the org, LAN addresses, key and cookie prefixes, ticket keys and unmapped UUIDs: none left.

| File | What it is |
| --- | --- |
| `agents.json` | 4 agents, all `idle`, one lead with three reports |
| `companies.json`, `health.json` | one company; authenticated mode |
| `live-runs-idle.json`, `live-runs-running.json` | `[]`, and one running run |
| `heartbeat-runs.json` | 14 full rows: succeeded, 2 failed, 1 cancelled; newest is the wake |
| `heartbeat-runs-summary.json` | the same listing with `summary=true` |
| `hook-status-during-run-{1,2,3}.json` | the three status-file writes of the wake |
| `ws-events-one-wake.jsonl` | the socket's event order for the wake (record only) |

Statuses never observed (`paused`, `error`, `pending_approval`, `terminated`, `active`, `timed_out`, `interrupted`, `scheduled_retry`, `queued`) are tested from edited copies made inside the tests, not from new files presented as captures.

## 10. Work packages

Three packages with no shared files except the first commit.

**Commit 0 (engineer 1, first, minutes):** `SessionSource.Paperclip` in the enum and `MirrorProtocol.CliPaperclip`, plus empty `PaperclipAgent` / `PaperclipSessions.Snapshot()` / `SetSnapshotForTests` with the signatures in 2.1. Pushed at once so nobody waits on anybody.

| Package | Owner | Files | Depends on |
| --- | --- | --- | --- |
| **A. Source** | engineer 1 | `PaperclipApi.cs`, `PaperclipSessions.cs`, `PaperclipCredentials.cs`, `tools/paperclip-probe/`, `docs/paperclip-findings.md`, their unit and integration tests | commit 0 |
| **B. Orbs and settings** | engineer 1 after A's parser; settings rows can start at once | `SessionManager.cs`, `ClickRouting.cs`, `CliMark.cs`, `HiddenOrbs.cs`, `OrbAutoGroups.cs`, `AutoGroupAccounts.cs`, `LocalPersona.cs`, `OrbWindow.axaml(.cs)`, `OrbweaverSettings.cs`, `SettingsWindow.cs`, `Program.cs`, `README.md`, UI tests, screenshots | commit 0 (uses `SetSnapshotForTests`, not the live loop) |
| **C. Contract and relay** | engineer 2 | Mobile: 4.2. Desktop: §5 files and their tests | commit 0. The mobile contract PR opens first; the desktop relay half follows on the desktop branch. |

If engineer 2 finishes C while B is open, the settings group (`OrbweaverSettings.cs`, `SettingsWindow.cs`, `PaperclipCredentials.cs` wiring and their tests) is the piece of B to hand over: it touches no file A or the rest of B touches.

QA starts with commit 0: writes the §8 script and the old-phone check while the engineers build, then reviews coverage of added lines once per PR.

One desktop PR against `develop` naming CB-421 and CB-422; one mobile contract PR against mobile `develop` naming CB-422. The mobile PR merges first. A desktop that sends `cli:"paperclip"` before any phone updates is safe by §6.

## 11. Non-goals for v1

- The WebSocket (§0). Revisit if Paperclip accepts board keys on the upgrade, or for `local_trusted` instances.
- Wake, pause, approve from Orbweaver or the phone. View only.
- A chat panel or run transcript for an agent.
- More than one Paperclip instance, or more than one company at a time.
- Pending approvals from `GET approvals` (returned `[]` here; shape unmeasured). `pending_approval` on the agent is the only waiting signal.
- Routine or schedule badges.
- Persona pictures, by name match or from Paperclip's `avatarUrl`.
- The browser approval flow for minting a key (`cli-auth/challenges`); v1 is paste-a-key.
- Merging the hook row's finer-grained state into the Paperclip orb.
- TLS pinning for an `https` instance.
