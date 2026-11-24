# Paperclip orbs: QA test matrix (CB-421 desktop, CB-422 phone contract), release 1

Written from the plan (`paperclip-orbs-plan.md` §8-§10 at 0c7d29c3c) and the product requirement's "How we will know it is good". One row per behaviour: what proves it, at which level, and the paired negative control, meaning the check that would fail if the setup were wrong.

Levels: **U** unit, **I** integration, **UI** headless UiTests, **S** screenshot, **C** console suite, **K** contract (mobile repo), **RM** real machine.

Gate rules (CLAUDE.md "Speed" and "Test cadence"): CI green on both rids at the same sha is the suite gate and QA does not rerun full suites locally. QA reruns targeted classes for what a round touched, and checks coverage of the added lines once per PR with `tools/coverage.sh --base origin/develop`, reading `merged 4` first. UiTests and UiScreenshots take `--filter-class`, never `--filter`; the summary total confirms the filter took.

**Blocked** means the instance cannot produce the state yet: no successful default-runner run, and no "Ask first" tool request. Such rows run on edited copies of real fixtures, labelled as edited inside the test. The real-machine half waits for a recapture.

## A. Product acceptance (from the requirement)

| # | Behaviour | Proof | Level | Paired negative control | Status |
|---|---|---|---|---|---|
| P1 | Connect: "Paperclip found" to orbs on screen in under 30 s, nothing typed | Timed on the MacBook and the Windows PC; discovery row appears on the mini with no address typed | RM | Cancel in the browser: no key stored, no orbs (C2) | Ready once a build exists |
| P2 | Presence: wake an agent, orb working within about 2 s, asleep after the run | Orb state read back from status/roster at 1 s sampling over about 3x the run length (Juno's run is about 8 s, so about 25 s) | RM | Agent not woken: its orb stays asleep through an equal window | Ready (wake permitted). It proves working and asleep, but the run fails, so it also lands on Broke |
| P3 | Needs you: orb amber and click opens the deciding page | Unit and UI on edited fixtures; RM with a board approval created per the lead's recipe | U, UI, RM | A finished or failed run does not turn the orb amber and changes nothing on a paired phone | Approval path ready. The "Ask first" tool-action path is **blocked** |
| P4 | One orb per agent | `PaperclipRunProcessTests` on the three real legacy hook files; RM on the hook-firing agent | U, UI, RM | `entrypoint: cli` in the same cwd is not folded. Paperclip off gives today's behaviour | Unit ready. RM **blocked** until "preconfigured home" recapture §10.7 shows its cwd and entrypoint |
| P5 | Expiry: orbs dim, Reconnect restores them in one click | `StepTests` 401; RM revoking the key. QA may not revoke keys, so the lead does it or it is simulated with a fixture server | U, UI, RM | An unreachable address dims only after the 15 s grace and says "Can't reach" | RM key revoke is the lead's call |
| P6 | Quiet: no push for a finished run; release 1 has no pushes at all | `RelayRosterTests` (`Finished` false) and `RelayActivity` counts. RM: complete a run and watch a paired phone | U, RM | A Claude control row still gets its `done` push | Ready |
| P7 | Both CI legs green, added lines covered, screenshots reviewed on both rids, installs hash-checked | CI; `coverage.sh --base`; the screenshot PR comment for macOS and Windows | CI, RM | n/a | End of ticket |

## B. Pure logic (unit)

| # | Behaviour | Class | Cases and controls |
|---|---|---|---|
| U1 | Every real fixture parses | `PaperclipApiParseTests` | `native/` and `legacy-claude-local/` files give 4 agents with 3 reporting to 1, the `error` agent with its reason, a redacted and a full run row in one listing. Control: `[]`, `{}`, a row with no id, wrong types and truncated JSON all return empty without throwing |
| U2 | State precedence | `PaperclipStateTests` | One case per row of the §2.6 State table. A need beats a live run. `terminated` beats everything. Control: an agent with no need and a live run is `generating`, not `waiting` |
| U3 | Mark rules | `PaperclipStateTests` | Budget pause is OverBudget, other pause is Paused, `error` is Broke, newest failed or timed_out run is Broke. Broke clears after a newer `succeeded`. Controls: `cancelled` and `interrupted` are not Broke; a run's `error` text appears in no output (assert on every string field) |
| U4 | Needs | `PaperclipNeedsTests` | Approval by `payload.agentId`, `requestedByAgentId`, then top lead; type wording; tool action by row agent or top lead; blocked issue assigned (counts), unassigned or unknown assignee (ignored); each URL. Controls: an approval with a decided status is not a need; a blocked issue is a need only when `blockerAttention.state` is `needs_attention` or the field is absent, so the real `issues-blocked.json` row is a need on its assignee, the same row edited to another state is **not** (labelled edited), and one with no `blockerAttention` is |
| U5 | Fold | `PaperclipFoldTests` | 8 agents all drawn, 9 folds sleeping non-leads with right count and names; unfold on generating, waiting or any mark; the 60 s hold; `show` and `hide`; three-level chain. Controls: a lead and a lead-less agent never fold; a manually paused agent stays visible |
| U6 | Ring | `PaperclipRingTests` | No ring at cap 0, spent 0, or both 0; percent; over 100 capped at geometry. Control: a normal pair does draw |
| U7 | Fold-away rule (as revised at 3bd6ac80c: entrypoint starts with `sdk-`, plus cwd, pid or session match) | `PaperclipRunProcessTests` | Three real hook files are run processes; an `sdk-ts` row with the measured isolated-workspace cwd is one, built in the test and labelled as inferred because the status file was never captured. Controls: a `cli` entrypoint in the same cwd is not folded; `sdk-cli` or `sdk-ts` in an ordinary repo, ordinary repo with no match, `.paperclip` path that is not a workspace, Windows separators; empty pid and session sets change nothing |
| U8 | Discovery | `PaperclipDiscoveryTests` | Real runtime-info gives 127.0.0.1:3100; config-only folder; two instances; malformed skipped; no folder gives the default; health not ok is dropped. Control: nothing in the instance folder is written, and secrets and db paths are never read (assert on the file-reading seam's call log) |
| U9 | Connect flow | `PaperclipConnectTests` | `local_trusted` connects with no challenge. Approved stores the key only after `me` is 200. Cancelled, expired and timed-out store nothing. A non-http approvalUrl is refused. Disconnect revokes then deletes, and deletes even when revoke fails. Control: key never appears in any status string |
| U10 | Poll cadence | `PaperclipSessionsStepTests` | What is called on which tick (2 s, 4 s, on change, on demand); backoff 2/4/8/16/30. 401 gives NeedsReconnect with the snapshot kept. Unreachable keeps, then dims after 15 s; recovery clears. Disabled makes **no** call. Every status-line string, including the expiry countdown |
| U11 | Credentials | `PaperclipCredentialsTests` | Round trip per base URL, delete, 0600 off Windows. Control: nothing key-like lands in `settings.json` |
| U12 | Relay roster | `RelayRosterTests`, `RosterFixtureTests`, `RelayRemotePaperclipTests` | Row shape per §4, parent only when admitted, `Finished` false. The four answers (`watch`, `unwatch`, `input`, `set_run`, `launch` parent) each beside the unchanged answer for an unknown id and a local row. Activity counts exclude it. The fourth roster re-serialises byte-identically. Control: the peer roster never emits `paperclip` |
| U14 | Hover text | `PaperclipStateTests` | Activity is `currentToolName`, else `currentStatusMessage`. Internal-looking text (`runner span:` prefix, or a dotted identifier like `runner.process.launch`) is dropped and the task line stands alone. Control: "Receiving agent output" is kept |
| U15 | Amber and broke together | `PaperclipStateTests`, `PaperclipOrbTests` | An agent in `error` that is assignee of a blocked task is `waiting` with Mark Broke: state and click follow amber, the "?" owns the corner, the Broke reason shows in the hover. Expected, not a defect (live case: Juno). Control: the same agent without the blocked task shows the "!" |
| U13 | Click routing | `ClickRoutingTests` | Amber opens the first need's URL; otherwise the agent page; a non-http URL is refused. Control: a terminal session still focuses the terminal |

Fixtures at 3bd6ac80c: `native/agents.json` and `heartbeat-runs.json` replaced, `issues-blocked.json` added (`issues-blocked-empty.json` deleted), `run-events-failed-run.reduced.json` new and reduced, not verbatim. Tests must not claim it as a verbatim capture. I audit the staged fixtures by file for home paths, unmapped UUIDs, key prefixes, LAN addresses and ticket keys.

## C. Seams (integration)

| # | Behaviour | Class | Cases and controls |
|---|---|---|---|
| I1 | The real HTTP client | `PaperclipHttpTests` against `LoopbackHttp` | Paths and query strings exactly as §2.3 and §2.5; `Authorization` sent with a key and absent without; challenge POST body byte-exact. 401, 500, a hung response (5 s timeout), a dropped connection and non-JSON come back as typed failures. Control: the log line has status, bytes and elapsed and never a body or key |
| I2 | Settings round trip | settings tests | The five new keys survive save and load; an old file loads defaults; unknown keys survive beside them |
| I3 | Existing hook contract untouched | existing hook suite | Still exit 0 with empty stdout and stderr. Run only if `Orbweaver.csproj` or the hooks changed |

## D. UI (headless)

| # | Behaviour | Class | Cases and controls |
|---|---|---|---|
| UI1 | Company as a team | `PaperclipScanTests` (in `[Collection("Settings")]`) | Lead plus two reports give three orbs with the right keys and leads; state changes in place; a terminated agent's orb goes; switching off removes all. Control: Paperclip off draws none |
| UI2 | Persistence | `PaperclipScanTests` | An agent three hours asleep survives the stale timer. Control: a normal idle session still expires |
| UI3 | Reconnect and dim | `PaperclipScanTests` | NeedsReconnect dims and keeps every orb |
| UI4 | Fold | `PaperclipScanTests` | Ten agents fold with the count on the lead |
| UI5 | One orb | `PaperclipScanTests` | A matching `sdk-cli` row is not drawn while connected and **is** drawn with Paperclip off |
| UI6 | Orb visuals | `PaperclipOrbTests` | Paperclip badge present and no kind badge; each mark; "?" wins the corner over a mark; ring shown and hidden; "+N" pill; every hover line in order; click target amber or not; menu rows per state (Open, Review only if amber, Reconnect only if needed); reset-idle disabled. Control: a terminal-session orb is unchanged |
| UI7 | Settings group | `SettingsWindow` tests | Found-instance row and Connect; approving state with Cancel; picker only with several companies; Reconnect only when needed; the http-on-LAN note. Control: no control ever holds the key |

## E. Screenshots (`tests/UiScreenshots`, both rids, reviewed in the PR comment)

1. Company: lead with reports, one working, one amber, one broke, one paused, one with a ring.
2. Folded lead with "+N".
3. Settings group, connected.
4. The dimmed Reconnect state.

I check each on both macOS and Windows. A mark that cannot be told from the badge, or a ring clipped at the orb edge, fails the review.

## F. Console and contract

| # | Behaviour | Proof | Control |
|---|---|---|---|
| C1 | No new geometry | `dotnet run --project tests/ArrangementTests` once | Fails only if a mixed-size or team layout broke |
| K1 | frames-v15 vectors | `test_check_vectors_paperclip.py` and `relay-tests` in the mobile repo, merged first | Six mutations each fail naming their rule |
| K2 | Desktop fixture byte-identical to the mobile copy | `RosterFixtureTests` against `tests/UnitTests/Fixtures/relay/roster-fixtures.json` | A one-byte edit fails |
| K3 | Old phone builds | Read from code only (§6). Check whether any old build is installed anywhere | If none exists, the PR says "not run on a device" |

## G. Parity and install paths (CLAUDE.md "works on Windows and macOS")

- `Orbweaver.csproj`, installers and `tools/*` need no new file unless the branch adds one. I check `build-macos-dmg.sh`, `build-macos-app.sh` Resources, `Orbweaver.iss`, `install-hooks.*` and the README install section.
- `~/.paperclip` home lookup and the 0600 key file on Windows (ACL or best effort): confirm the PR names what it does. Browser launch of the approval URL on both OSes.
- The paperclip-probe tool builds on both rids, or is excluded from CI on purpose.

## H. Real-machine script (short and paired; I own the installs)

Install order and discipline: take `/tmp/ow-install.lock` on the mini, install signed on the MacBook and the mini where a signing identity exists, SHA-256 the installed binary against the built one before saying "installed", and re-check before reporting. Windows via `ssh windows` and Orbweaver.iss. The report names which of the three I installed on and which I did not.

| # | Step | Observation window | Negative control | Status |
|---|---|---|---|---|
| R1 | Connect timed (MacBook remote address, Windows PC, mini discovery row) | Stopwatch from Settings open to orbs; target under 30 s | Cancel stores no key | Ready with a build |
| R2 | Company: four orbs, three linked to Jennifer, on all three machines | Positions read from the window server (`CGWindowListCopyWindowInfo` by pid), re-resolved on every sample; not judged by eye | Paperclip off removes them | Ready |
| R3 | Presence: wake Juno | 1 s samples over about 25 s | No wake leaves it asleep | Ready (permitted) |
| R4 | Broke: Jennifer in `error` wears the mark with her reason; Juno after a failed run | Single read | A healthy agent has no mark. A fixture succeeded run clears it | Ready |
| R5 | Needs you: create a board approval, orb amber, click opens the approval page. Approve or reject it afterwards, and the orb returns | Poll cadence is 4 s, so a window of about 12 s | A failed run changes nothing | Approval ready. Ask-first is **blocked** |
| R6 | Pause then resume an agent (tests the mark only, not an Orbweaver action: release 1 sends no pause): Paused mark appears then clears | About 12 s each way | Resume restores | Ready (permitted). It uses my pause call, not Orbweaver's, since release 1 is read-only |
| R7 | Expiry | Automated proof: the engineers' integration tests against a refusing fixture server. Real machine: I tell the lead when a build is connected, and the lead revokes that build's own "Orbweaver (board)" key; I never revoke | Unreachable address dims after about 15 s and says so | Partly blocked |
| R8 | One orb on the hook-firing agent | One run. In preconfigured mode the hook's status file lived about 2 s and was removed on `ended`, so sample faster than 1 s, or read `hook.log` | Paperclip off shows the local orb | **Blocked** until a hook-firing agent exists |
| R9 | Phone roster | Needs a phone with a current mobile build | Old build opens an empty conversation | Depends on mobile |

Anything I create (approvals, wakes, pauses) is cleaned up and listed to the lead. I do not create or revoke keys, change agent config, touch connectors, or un-pause the routine.

## I. What QA checks on every review round

For each engineer push: (1) the tests named above exist for the lines added; (2) a targeted run of the classes the push touched is green and the summary total shows the filter took; (3) any claim about Paperclip's behaviour in a comment is either measured in `docs/paperclip-findings.md` or labelled assumed; (4) fixtures contain no home paths, real UUIDs, key prefixes, LAN addresses or ticket keys (grep the staged diff by file, naming the files); (5) no `GET`-only rule break: release 1 issues no write beyond `cli-auth/challenges` and `cli-auth/revoke-current` (grep the client for POST, PUT, PATCH and DELETE); (6) PR body names the uncovered lines (real `HttpClient` construction, browser `Process.Start`, real home lookup) and says which cadence step produced each figure.

---

# Release 2 (CB-427 desktop, CB-428 phone and watch, CB-425 bug): "Act on it"

Written from the three tickets and plan §7 before any release 2 code was pushed, so it names behaviours, not classes the engineers have not yet chosen: where a row says "class" it is a placeholder to be renamed to what lands. Same levels, gate rules and review checklist as release 1 (section I applies unchanged, with one change below).

**Change to review item (5).** Release 1 allowed no write beyond `cli-auth/challenges` and `cli-auth/revoke-current`. Release 2 allows exactly: approvals `approve` / `reject`, tool-gateway `action-requests/{id}/approve` / `decline`, `wakeup`, `pause`, `resume`. The check becomes: grep the client for every POST, PUT, PATCH and DELETE and confirm that set and no other, each behind `IPaperclipActions`, and that no call edits an agent, tool, budget or schedule (requirement 4).

**Measured before it is built (plan §7).** Three facts the tickets say to measure first. A test that encodes any of them without a captured fixture is labelled assumed: (a) a real "Ask first" tool action row and its approve and decline; (b) pause and resume with the app key (is the app key even allowed?); (c) what Paperclip answers on a second approve of the same item. Row A4 and A5 depend on (c).

## J. Product acceptance, release 2

| # | Behaviour | Proof | Level | Paired negative control | Status |
|---|---|---|---|---|---|
| Q1 | Approve from the flyout changes the item in Paperclip's own approvals list | Test approval created by QA; click Approve; read the approvals list from Paperclip itself, not from the orb | RM | Same click with the app's key revoked: list unchanged and the flyout says why (Q2) | Needs a build |
| Q2 | A failed answer says why and leaves the item answerable | UI with a fake `IPaperclipActions` failing 401, 500, timeout; RM with the revoked key | U, UI, RM | A succeeding fake clears the row | Ready |
| Q3 | Decline, same as Q1 | As Q1 with Decline; item shows rejected | RM | Item not touched stays pending | Needs a build |
| Q4 | Orb leaves amber on the next poll after an answer, without waiting | Time from click to orb not amber, read from the window server and status; compare to the cadence | RM | Local-orb control for the lag (Q9) | Needs a build |
| Q5 | An item already answered elsewhere is reported as such, not an error | Approve in Paperclip's UI or API first, then click Approve in the flyout; text differs from the failure text of Q2 | U, UI, RM | An item that really failed (revoked key) shows the failure text | Blocked on measurement (c) |
| Q6 | Blocked task shows only "Review in Paperclip" | UI | UI | An approval beside it shows Approve and Decline | Ready |
| Q7 | Wake now produces a working orb | Wake the paused-free fixture agent; orb working within a poll | RM | No wake: stays asleep through an equal window | Needs a build; the run may end Broke (as release 1 R3) |
| Q8 | Pause shows the mark, Resume clears it, armed-then-confirmed rows | UI: first click arms, second confirms, a click elsewhere or a timeout disarms, nothing sent before the second click. RM: mark appears and clears | U, UI, RM | Resume on an agent not paused is not offered | Blocked on measurement (b) |
| Q9 | CB-425: amber clears as fast as it appears | Local-orb control on the same machine measured first, then the Paperclip orb, same method, 20 reps each side if the cadence allows | RM | The local orb is the control, since "never taken" is the open item in the ticket | Ready for the control now |
| Q10 | Nothing in the user's org is edited | Diff Paperclip's agents, tools, budgets and routines list before and after a full session | RM | Before/after of a session with no clicks is identical | Ready |
| Q11 | Needs-you push: exactly one answerable push per need | Staging relay: create a need, count pushes; see section M | RM | A completed run: zero pushes | Needs the relay build |
| Q12 | Approve through the phone path changes the item | Section M | RM | Approve of an already-answered item is reported, not an error | Needs the relay build |
| Q13 | Broke push, once, not answerable, taps through to read-only detail | Section M | RM, UI | A completed run: zero pushes. A `cancelled` run does not produce one (release 1 mark rules) | Needs frames-v16 |
| Q14 | Quiet: no Live Activity for a scheduled routine, a need answered elsewhere resolves the notification | Contract vectors and relay tests; RM on the staging relay | K, RM | A user-started run does start one if it did before | Needs frames-v16 |
| Q15 | Paperclip rows stay out of session counts | `RelayActivity` counts unchanged with a Paperclip need present | U | A Claude control row still counts | Ready |
| Q16 | Windows and macOS parity, installers, README | Section G rules from release 1 plus: `tools/Orbweaver.iss`, `build-macos-*` need nothing new unless the branch adds a file | Review | n/a | End of ticket |

## K. Pure logic and seams, release 2

| # | Behaviour | Class (placeholder) | Cases and controls |
|---|---|---|---|
| V1 | The five calls build the right request | `PaperclipActionsHttpTests` against `LoopbackHttp` | Method, path, body byte-exact for approvals `approve` / `reject` with `decisionNote: "From Orbweaver"`, tool action `approve` / `decline` with `{companyId}`, `wakeup` with `source: "on_demand"`, `pause`, `resume`. Authorization present. Control: a body is never logged and the key never appears in a log line |
| V2 | Outcome mapping | same | 200 is Done; the already-answered status, whatever measurement (c) shows, is `AlreadyAnswered`; 401 is NeedsReconnect, not a failure of the item; 404, 409, 500, hung response and dropped connection each map to a typed failure with its own message. Control: a 2xx with an unexpected body is not Done unless the shape says so |
| V3 | Which need is which call | `PaperclipActionsTests` | Approval need to approvals route; tool action need to tool-gateway route; blocked issue has no action. Control: an approval id never goes to the tool-gateway route |
| V4 | Optimistic clear and rollback | scan tests | After a successful answer the need is dropped from the snapshot before the next poll; a failed answer leaves it; a poll that still lists the item after a success does not flicker the orb to idle and back for the already-dropped need beyond one poll. Control: an unrelated need on the same agent stays |
| V5 | Double click | UI | A second Approve while the first is in flight sends one call. Control: Decline after a failed Approve is allowed |
| V6 | Pause and resume rows | UI | Rows per state; Pause disabled or absent when the agent is terminated or already paused; arming timeout; the confirmed row sends once. Control: the OpenClaw end-conversation row's behaviour is unchanged |
| V7 | CB-425 | scan and orb tests | The update path from "need gone" to `UpdateFrom` has a measured bound; a test with a fake clock asserts no timer between them above the bound. Control: gaining a need keeps its current latency. Whatever the diagnosis, the PR names the cause, read from the log line, not assumed |
| V8 | Prompt minting | `PaperclipPromptsTests` | One prompt per need with a stable id across polls (no duplicate on a re-poll), the §4 `prompt` shape, options Approve and Decline only, answer verified by `AnswerGate`, carried out through `IPaperclipActions`, a wrong or unknown answer rejected. Control: a blocked task mints no prompt; a Claude permission prompt is unchanged |
| V9 | Prompt lifecycle | same | Need answered elsewhere resolves the prompt; need gone on restart does not leave a stale prompt; two needs on one agent are two prompts. Control: a need that persists across five polls is still one prompt |
| V10 | Broke detection edge | state tests | One broke event per transition into Broke, not per poll; a second failed run after Broke is a new event only if the rule says so (state the rule in the PR); `cancelled` is not Broke. Control: restart with an already-Broke agent sends no push |
| V11 | Relay frames-v16 | `RelayRosterTests`, `RosterFixtureTests`, relay tests | The new push value and wording; old phone builds still receive an ordinary answerable prompt; fixtures byte-identical to the merged mobile copy. Control: a one-byte edit fails (K2) |
| V12 | Settings round trip | settings tests | New keys, if any, round trip; old file loads defaults; unknown keys survive |
| V13 | Screenshots | `tests/UiScreenshots` | The flyout with Approve, Decline and the link; the flyout for a blocked task (link only); the flyout after a failed answer; the orb menu with Wake, Pause and Resume. Reviewed on both rids in the PR comment |
| V14 | Console | `ArrangementTests` | Once; the flyout should not change geometry |

## L. Mobile (CB-428), what QA checks and what it cannot

| # | Behaviour | Proof | Control |
|---|---|---|---|
| M1 | `frames-v16` contract and vectors green in Mobile CI, merged before the relay and phones | `test_check_vectors_paperclip.py` and relay tests; vectors include the prompt vectors CB-428 names | The checker's mutations each fail naming their rule |
| M2 | iOS, watchOS, Android handle both pushes | Read from code and the repo's own tests. I run targeted tests only. No simulator work by QA unless Hana asks: the mini has about two simulator slots and other teams use them | An old phone build is read from code only, as release 1 K3 |
| M3 | TestFlight build uploaded | Read the build number from App Store Connect with the API key | Build number is higher than the last |

## M. Real-machine proof, release 2

I own the installs. Before anything: take `/tmp/ow-install.lock` on the mini, copy each machine's `settings.json` aside, SHA-256 the installed binary against the built one, re-check before reporting, and restore every setting and delete every file I created, listing the directory to prove it. Never touch input on the MacBook or Windows PC. Sizing: a window is about three times the event's own cadence. The app polls at 2 s and 4 s, so a need appearing or clearing is observed over about 12 s. Test approvals only, each named `QA <date> <n>`, all rejected or deleted at the end. Every key I mint is revoked and I list them.

| # | Step | Window | Negative control | Status |
|---|---|---|---|---|
| X1 | Approve: create a test approval; orb amber; click Approve in the flyout | 12 s; read the item from Paperclip's own approvals list | Item not clicked stays pending over an equal window | Needs a build |
| X2 | Decline: same | 12 s | same | Needs a build |
| X3 | Revoked-key negative: revoke the app's own key from Paperclip, click Approve | 12 s | The item stays pending; the flyout says why; reconnect restores answers (one approval) | Needs a build and a key I minted myself (the lead decides on the app's own key) |
| X4 | Already answered: approve in Paperclip first, then in the flyout | 12 s | A really failing call (X3) shows different text | Blocked on measurement (c) |
| X5 | Wake: wake the fixture agent | 25 s at 1 s samples (the run is about 8 s) | No wake: asleep through an equal window | Needs a build |
| X6 | Pause, then Resume | 12 s each way | Resume on a never-paused agent is not offered | Blocked on measurement (b) |
| X7 | CB-425 control: a local session's orb leaves amber (an existing permission prompt answered by a test session I own). Sample the window server every 0.25 s from the answer to the orb losing amber. Then the Paperclip orb, same instrument, same machine | 10 s per rep, at least 5 reps each, both on the mini; the Windows PC if the lead lets me, since the ticket was measured there | A control that is itself slow (local orb about as slow) says the cause is not Paperclip-specific. A fast control says it is. Report both numbers with the poll time from `diagnostics.log`, never an unanchored guess | Ready for the control now |
| X8 | Staging relay: one need gives exactly one answerable push | Judged from `mirror-log.txt` (UTC, rotates; copy it aside first). Turns under 3 s never count on the mini, so only runs that last longer count | A completed run gives no push over the same window | Needs a relay build and a phone path |
| X9 | Approve through the phone path | 12 s after the answer, read the item from Paperclip | A second approve is reported, not an error | Needs the relay build |
| X10 | Broke push | One failing run | A completed run: none | Needs frames-v16 |
| X11 | Nothing else changed | Before and after listing of agents, tools, budgets and routines | A session with no clicks is identical | Ready |

**Connect challenges.** When a build needs a challenge approved I write its token to `~/.paperclip/instances/default/qa-approval-token.txt` at mode 0600 and message the lead the challenge id, never the token.

**Not checkable by me, and the report will say so:** an old phone build on a device, iOS and Android on real phones and the watch unless a build lands on one; macOS Local Network consent after an upgrade; the Windows PC if it is off.

## N. Updated to plan revision 4 (§13, origin/feature/paperclip-act c10094e3c)

The three measurements section J called blocked are now in plan §13.1. Where this section and J disagree, this one wins. Class names now follow §13.5.

| Row | What changed |
| --- | --- |
| Q5, X4 (already answered) | Measured. An approval approved twice answers 200 with the same row, so it maps to `Done`. The opposite decision answers 422, and the implementation reads the approval back and maps it to `AlreadyDecided` ("Already answered in Paperclip"). A tool action answers 409 on the opposite decision and 200 on the same one. The real-machine step is proof 2 of §13.6: approve in Paperclip's own page while the flyout is open, then press Approve. Control: a revoked key shows the reconnect sentence and **not** the already-answered one. |
| Q8, X6 (pause, resume) | Measured. Both work with a board key. After Resume, Paperclip still says `paused` for up to 2 s, so the mark clears on the poll after the immediate one. That is not a failure. The window is 3 x 2 s plus a poll, about 12 s, and an observation inside the first 2 s that still sees the mark is not a defect. Control: Resume is not offered on an agent that is not paused. An agent paused for budget is offered neither Pause nor Resume (resume of a budget pause is unmeasured): check it only on a fixture, labelled edited. |
| Q7, X5 (wake) | Wake on an agent with no task waiting fails in about 0.7 s and leaves the agent in `error`. So "a working orb" can only be proved on an agent with an assigned open task. Without one the proof is the opposite: Broke with Paperclip's reason, and **no push** to a paired phone within 30 s of the Wake pressed on that desktop (the lead's decision, §13.6 proof 3). Paired positive: a failure that was not a local Wake does push when "Agent broke" is on (proof 5). |
| Q1, Q3 (tool action) | A real "Ask first" request exists: `819406f3…` (pending, expires 19:54Z); more via the test-call recipe. The row is nested under `request`. A request names **no agent** (`requestedByAgentId` null), so its need lands on the company lead's orb. Approve answers 200 and the tool runs inside the call; on this fixture it fails, so the expected flyout text is "Approved, but the tool failed", not an error, and the item moves to `?status=failed`, not `executed`. Control: Decline another one, which leaves the list as `rejected`. |
| New R1 | `develop` draws nothing for a real request (parser reads the id from the wrong level, §13.1 row 12h). The proof is the captured `tool-actions/action-requests-pending.json` parsed by `PaperclipApiTests`, which must fail on `develop` and pass on the branch. Run it on `origin/develop` first to see it fail. There is no release 1 baseline to compare against on a real machine, and the report says so. |
| New R2 | After Approve, Paperclip starts a run on the agent that asked about 1.7 s later, so the orb goes amber to working, not to asleep. The Q4 timing reads "not amber", not "asleep". Control: Decline does not necessarily start a run (not separated out in §13.1 row 6), so a Decline observation does not assert either way. |
| New R3 | Several needs on one agent: one prompt at a time, the oldest first. Phone proof: two approvals on one agent give one push, the second after the first resolves. |
| New R4 | The desktop restart mints a fresh prompt and alerts again (named limit in §13.4). The report records it as a known behaviour, not a failure. Control: no restart gives no repeat. |
| V1, V2 | The outcome table is in §13.2: 2xx `Done`; 4xx then a read-back for approvals; 409 and 404 `AlreadyDecided` for tool actions; 401 `NeedsReconnect`; other 4xx `Refused` (text cut to 140 characters, no path-shaped string from a 5xx body); no connection, timeout or 5xx `Unreachable`. One case per row for each of the four calls. A `blocked` need and the synthetic `pending` need make no request. |
| V10 | `PaperclipBrokeTests` per §13.5. Add: a Wake pressed 5 s before sends nothing and 31 s before sends; a finished run with no mark sends push `none`. |

**The write allow-list for review item (5) is now exactly:** approvals `approve` / `reject` (body `{"decisionNote":"From Orbweaver"}`), tool-gateway `action-requests/{id}/approve` / `decline` (body `{"companyId"}`), `agents/{id}/wakeup` (body `{"source":"on_demand"}`), `agents/{id}/pause`, `agents/{id}/resume`, plus release 1's challenge and revoke. A POST to anything else fails the review. The one extra read is `GET /api/approvals/{id}` after a 4xx on an approval decide.

**CB-425.** §13.2 puts the fix in `PaperclipSessions.PollNow()`, which releases the poll loop's wait so the next step runs at once. My Windows log evidence (scan passes of 1.0 to 7.7 s on that PC) says the wait may not be where the time goes. Whichever it is, the proof is the same: the local-orb control and the Paperclip orb measured on the same machine with the same instrument before and after, with the poll time from `diagnostics.log`. The PR names the cause from that reading.
