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
