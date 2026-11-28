# Bug-fix org: host side (CB-455)

Everything here runs on the Mac mini, outside the Paperclip sandbox. Plan:
`docs/plans/paperclip-bugfix-org-plan.md` §2, §4, §6, A.5.

## Setup order

The lead runs each step; none of it is run by the tests.

1. `host/setup-git-config.sh` — creates the empty `~/.paperclip/instances/default/gitconfig`
   and makes sure the service plist sets `GIT_CONFIG_GLOBAL` to it, reloading the service
   (bootout, bootstrap) only if it had to change the plist. Without it the managed
   `git clone` fails under the sandbox profile; the fix is not a profile read of
   `~/.gitconfig`, which can carry credential helpers. Already applied on the mini by QA,
   so a run there changes nothing.
2. `host/apply-rulesets.sh`, then `--apply` — the three rulesets; a dry run first. Then the
   same for Orbweaver-Mobile, **before the warden mints a token that reaches it**: see
   [Orbweaver-Mobile rulesets](#orbweaver-mobile-rulesets-cb-481).
3. `python3 host/create_app.py --public-host <mini LAN address>` — send Warren the URL it prints.
4. Write `~/.config/orbweaver-bugfix/warden.json`: `company_id`, `paperclip_login`
   (`~/.config/paperclip-admin/board-login.txt`), `spool` (a folder inside the org's
   directory), and optionally `token_ceiling`, `max_runs_per_day` and `repos` (below,
   CB-481; without it the warden works in Orbweaver alone, as before).
5. `python3 host/check_host.py` — read-only: warden.json, the empty gitconfig, the plist,
   and Paperclip's instance flags (`enableIsolatedWorkspaces` and
   `enableWorktreeRunExecution` on, `enableIsolatedWorkspacesByDefault` off; `git_worktree`
   needs both). It prints one `PROBLEM:` line per fault. `{{slug}}` in `branchTemplate`
   comes from the issue title, so the lead titles issues with the change in plain words.
6. Install `host/launchd/org.uplift.orbweaver-bugfix-warden.plist` (replace `__REPO__`).

## What the warden does when it pauses

A Paperclip pause only defers new assignments; a run in flight keeps going (measured in
phase 0). So while the company is paused, every tick cancels its queued and running runs and
sets its in-progress issues to `blocked`. Parking them as `blocked` makes the paused org's
orbs go amber on the desktop; that is intended, because a warden pause needs a person. A
refusal to block an issue is retried every tick but logged once per issue per pause (CB-484).

The warden resumes the company itself in one case only (CB-484). The release brake is a cost
brake, not a safety brake, so a pause the warden made whose only reasons are "releases in a
row" (the Paperclip counter, the spool's, or both) is lifted 60 minutes after it was made:
`PATCH /api/companies/<id> {"status":"active"}`, both release counters and the set of released
tickets reset, and `RESUMED: release brake cooled off` in the log. It does this every time the brake trips; the
org must not freeze on a cost brake. Every other pause
(merge audit, workflow guard, refusal, cost ceiling, run cap, unreadable signal), a release
pause that gained any other reason while paused, and any pause the warden did not make itself,
is lifted by a person or the CB-455 lead. The reasons are kept in `state.json` as
`warden_pause`; a pause made before this change has none and stays manual.

On any resume, the warden's or a person's, the first tick that sees the company active sets
going what `stop_work` stopped: an issue it blocked that is still `blocked` is set to `todo`
and gets one board comment, which wakes its assignee; an issue it could not block (the 422
case) and that is still `in_progress` gets the comment only; anything that has moved on since
is left alone. The unstick sweep never touched a blocked issue with no children (it now
closes one that already answered, `close_answered` below, but not one blocked by a pause), so before
this an engineer task blocked by a pause sat forever. A failed restart is logged and retried
on the next tick (`restart_retry` in `state.json`). `max_runs_per_day` is the run brake because Paperclip's
`heartbeat.maxDailyRuns` is timer-only and does not bound assignment wakes.

## Where the warden's signals come from, and how far each can be trusted

The spool is written from inside the sandbox, so a misbehaving org can forge or suppress it.
Signals that matter are therefore read from Paperclip and GitHub, which the org does not
write to directly:

| Trigger | Source | Notes |
| --- | --- | --- |
| Two releases in a row | Newly `cancelled` engineer issues in the fix company (description starts `Ticket: CB-NNN`), read from Paperclip each tick. A ticket counts once until the next merge, whatever fix pass its engineer issues belong to: the warden keeps the set of ticket keys released since the last merge (`paperclip_released_keys`) and the brake trips on two distinct tickets. CB-471 matched on ticket and parent pass, but with the routine firing every 30 minutes a fix round's issue has a later parent, and one release of CB-346 paused the org for 13 hours (CB-484) | The lead sets the engineer's issue to `cancelled` on release and `done` on merge (its instructions; the PATCH has not been run). A cancelled issue without that first line (a QA or helper child task) is not a release; each release is logged with its CB key. Measured by QA: cancelled rows in the listing do carry `description`, so the next line is the rare path. A newly cancelled row that has no `description` field is read in full with `GET /api/issues/<id>`; if even that has none, the warden logs `release signal unreadable` and pauses, rather than skipping it or counting it blindly. The first tick only records what is already cancelled. A merge resets the count and clears the set. The spool's `released` is a second, separate counter; the two are not added. |
| Bot merge with no QA comment, red CI on a bot merge | GitHub | Known limit: the bot and the QA agent comment as the same GitHub identity, so "a QA comment" means any comment or review before the merge naming both rids. The warden cannot tell QA's comment from the lead's. |
| Run cap, token ceiling | Paperclip runs and costs APIs | Off until set in `warden.json`. |
| GitHub refused a push | git's ruleset-refusal output in a run's log, `remote: error: GH013: Repository rule violations` or `(push declined due to repository rule violations)`, each run read once. The bare code alone is not a refusal: the lead's instructions quote it (CB-462). The spool's `push_refused` is an extra signal. | **Not fully observable.** A refusal that never reaches a run log (a push the agent ran quietly, or the log truncated at 200 KB) is not seen. The idea of comparing a `bugfix/pc-*` branch's remote head against a "push attempt" in the issue was not built: Paperclip records no structured push attempt to compare against. |

Only the Paperclip and GitHub reads above were checked against Paperclip's source; the
instance-settings read was also run live (read-only) and matches `check_host.py`'s shape.
QA has since read the issues listing and the run-log shape live (read-only); the cancel route, the pause PATCH, `blocked` and the secret calls have not been run.

## Reruns (CB-465)

The org's App holds Actions read only, so it cannot rerun a CI job. A flake in
a test its change does not touch therefore cost CB-387 its ticket. The spool
verb `{"verb": "rerun_failed", "pr": N}` asks the warden to do it with the
host's `gh` login: `POST /actions/runs/<id>/rerun-failed-jobs` on the newest
`ci.yml` run at the PR's head. The lane is the stress dispatch's (the org's own
open `bugfix/pc-*` PR from this repository, into develop). The run must be
completed red, at most once per head sha (`state["reruns"]`). A refusal is
logged and never pauses.

## Workflow PR merges (CB-486)

The lead's everyday token cannot merge a PR that changes a workflow file. GitHub
works out a PR's merge state for whoever asks, and on such a PR a token without
`workflows: write` is told BLOCKED, so `gh pr merge` refuses on the client with
"the base branch policy prohibits the merge" and GitHub's merge endpoint is never
called. Measured 2026-11-27 with both App tokens, read-only through GraphQL
`mergeStateStatus`: #429 (`ci.yml` only) and #305 (two workflow files) read
BLOCKED to the everyday token and CLEAN to the workflow token. #436 and #390,
which change no workflow file, read the same to both. The ruleset's
`require_extra_approval_for_unattributed_changes`, which the lead blamed on #429,
cannot explain a state that changes with the token: it is a property of the PR,
and #431 merged under the same ruleset as the same App. That rule is unchanged.

So the lead asks through the spool and the warden merges:
`{"verb": "merge_workflow_pr", "pr": N, "sha": "<40 hex>"}`. The warden checks
everything again itself (`workflow_merge_problem`) and refuses, logging
`refused workflow merge for PR N: <why>`, unless all of these hold:

* the repository allows workflow edits (desktop; mobile never);
* the company is not paused, and this tick has no pause reason of its own;
* the PR is the org's own: open, opened by the App, head in this repository on a
  `bugfix/pc-*` branch, into `develop` (`dispatch_problem`);
* its head is exactly the requested sha;
* GitHub's file list for it is under its 3000-file cap (`PR_FILES_CAP`), so nothing below
  reads a list that may be cut short;
* it changes at least one workflow file (anything else the lead merges itself);
* the workflow guard finds nothing at that head (read fresh, not from `guarded`);
* both required `build (...)` checks are `success` at that sha;
* the bot's latest comment or review starting `[bugfix-org] QA` (every page read) ends
  `Verdict: merge`, names the sha (full, or a prefix of at least 9 as the skill writes it), both rids, and has
  a `Workflow change:` line. A later `Verdict: fix`, or a later QA comment at another sha,
  overrides an earlier merge;
* this sha has not already been merged, and has had fewer than
  `MAX_WORKFLOW_MERGES_PER_SHA` (2) attempts (`workflow_merges` in state), so a replayed
  spool file cannot merge twice or loop. A failed attempt is never counted as a merge.

Then `WorkflowMerger` mints a token for Orbweaver alone with
`WORKFLOW_PERMISSIONS`, used for that one call and never stored or written to a
secret, and calls `PUT /pulls/N/merge` with `merge_method: squash` and the sha,
so a push after the checks makes GitHub refuse it (409). The token goes in a request
header from inside the warden, never on a command line, and a failure logs only the
exception's type, status code and GitHub's JSON `message` (for example "Pull Request is not
mergeable"). An installation without
`workflows: write`, an unreadable read, or GitHub's refusal is logged. None of
them pauses: asking is not a fault. The merge then goes through the ordinary
merge audit like any bot merge.

Not run live: the `PUT` itself with the workflow token. A real-sha merge attempt
was not made while diagnosing. The token-dependent `mergeStateStatus` is the
evidence for the cause, and the first real request is the live proof.

Develop's own push CI after a bot merge is rerun by the warden unasked
(CB-467), because red there pauses the whole org. The first rerun is
unconditional. After that it reruns again only while every attempt failed on
tests and no test failed twice, at most `DEVELOP_RERUNS` (2) times; the names
come from the failed jobs' logs (`gh run view --attempt N --log-failed`) and
are cached per attempt in `state["develop_failures"]` until the merge is
retired. The macOS unit leg is why: PR 377's merge failed on
`ClaudeCloudEventsTests`, its rerun on `TurnSoundsTests`, and the old
rerun-once rule paused the org on the second. The same test failing twice, an
attempt whose log names no test (a build error), or a third red all pause, and
the pause reason says which.

## Two repositories and workflow files (CB-481)

**Config.** `repos` in `warden.json` lists the repositories the org works in. Absent, the
warden uses `warden.DEFAULT_REPOS`, Orbweaver alone, so an older config behaves exactly as
before. To add the mobile repository, paste `warden.DESKTOP` and `warden.MOBILE` from
`host/warden.py` as JSON. The order (rulesets before a token that can push there) is
enforced by the code, not left to the deploy: see the ruleset gate below.

| key | desktop | mobile |
| --- | --- | --- |
| `repo`, `base` | `Uplift-Foundation/Orbweaver`, `develop` | `Uplift-Foundation/Orbweaver-Mobile`, `develop` |
| `post_merge_checks` | the two `build (...)` checks | none: develop runs only `relay-image` on a push, so a merge stays pending 30 min (`MERGE_QUIET`) and any check on the merge commit that ends red pauses |
| `qa_names` | `osx-arm64`, `win-x64` | `relay`, `contracts`, `tools` |
| `head_checks` | none | `relay`, `contracts`, `tools` always; `android` when `android/` or `contracts/` changed; `snapshots` when `app/` or the v7/v11 vectors changed. Audited at the merged PR's head, every one must be `success`, because a path-filtered check cannot be required by a ruleset |
| `ci_workflows` (reruns) | `ci.yml` | `relay-tests.yml`, `android-tests.yml`, `ios-snapshots.yml` |
| `push_ci` (develop reruns, CB-467) | yes | no |
| `stress` | yes | no: `dispatch_stress` with `"repo": "mobile"` is refused |
| `workflow_edits` | yes, under the guard | no: any workflow change pauses |

Desktop's state stays where it was in `state.json` (`audited`, `audit_pending`, `reruns`,
`audit_failures`); any other repository keeps the same keys under `state["repos"][key]`, so
nothing migrates. A merge in either repository resets the release count. Each repository
has its own unreadable-audit counter; three in a row pause, naming it. The spool verbs
`rerun_failed` and `dispatch_stress` take an optional `"repo"` key (default desktop); a key
the config does not list is refused and logged. Releases and push refusals need no change:
both are read from Paperclip.

**Two tokens.** Every mint reads `GET /repos/<repo>/installation` with the App's JWT for
each configured repository (404 means not covered) and then mints:

* **Ruleset gate.** A repository other than desktop joins the everyday token only while
  `GET /repos/<repo>/rules/branches/<branch>` shows, for **both** `main` and its base
  (`develop`), a `pull_request` rule from a ruleset that `GET /repos/<repo>/rulesets`
  lists as `active`. One protected branch is not enough. Both are read with the host's `gh`.
  An empty listing, a failed read, a disabled or evaluate-only ruleset, or active rules
  with no `pull_request` among them (a deletion rule alone does not keep the org off
  develop) each keep it off the token and the mint goes ahead for the rest. One line is
  logged per change (`mobile kept off the token: <why>`, `mobile back on the token: ...`),
  not one per tick. Desktop is never gated, so its token never waits on that read.
  Measured 2026-11-26: Orbweaver's develop shows four active rules, `pull_request` among
  them; Orbweaver-Mobile shows no rulesets.
* `GITHUB_TOKEN`: every configured repository the installation covers and the gate lets
  through, with the six
  permissions asked for by name (`warden.EVERYDAY_PERMISSIONS`). It never carries
  Workflows, so a permission the App gains later does not reach it by default.
* `GITHUB_TOKEN_WORKFLOWS`: the repositories with `workflow_edits` only, which is
  Orbweaver alone, with the same six plus `workflows: write`. Minted only while the
  installation shows `workflows: write`. Never mobile: a workflow pushed there could run on
  the mini's own `orbweaver-ios` runner with mobile's Docker Hub secrets. Binding it to the
  engineers is the org definition's job (task 2).

The read is written to `<spool>/access.json`: `{"checked_at", "repos": {"desktop": true,
"mobile": false}, "workflows": true, "withheld": {"mobile": "no rulesets"}}`. `repos.<key>`
means "on the everyday token as of the last mint"; `withheld` names why a covered repository
is not. The org reads it: a warden older than CB-481 never writes the file, so its
existence with `"mobile": true` is what tells an engineer a mobile `rerun_failed` will be
read as mobile, not as a desktop PR of the same number. A failed installation lookup for one repository (anything but 404, e.g. a 5xx) keeps
that repository off the token for the tick, with `installation unreadable (...)` under
`withheld`, and the others are still minted: one bad lookup must not let desktop's
token lapse. A failed mint is logged
(`token not refreshed: ...`) and the tick goes on to the unstick sweep; `last_mint` moves
only on success, so the next tick tries again. Before CB-481 a failed mint ended the tick.

**Paused or not, and to a file too (CB-489).** Both tokens are minted every ten minutes
whether or not the company is paused, and on the tick that resumes it the mint comes first,
before `restart_stopped` wakes anything: a pause stops runs, it never revoked access, and a
run a resume woke used to copy a token that had expired during the pause (QAF-190, QAF-209).
The everyday token is also written to `bugfix-tokens/gh-token` beside the
spool, never inside it, since agents write requests there (`token_dir` in `warden.json`
overrides it; one line, 0600, folder 0700, renamed into place, written before the secret
rotates). The workflow token gets
no file, since any run, the lead included, could read one. The org's `gh`
and `git` stand-ins read the file at every call (`paperclip/bugfix-org/README.md`, setup step
5), since a run's `BUGFIX_GH_TOKEN` is only a copy taken at its start. A file that cannot
be written is logged as `bugfix-tokens/<name> not written: <error>` and the secret still rotates.

**The workflow guard.** Every tick, for every open org PR in every repository, at each new
head sha (`guarded` in that repository's state), the warden reads `pulls/<n>/files` and
pauses, naming the PR, the file and the rule, on:

1. any change to `release.yml` or `publish-screenshots.yml` (`protected_workflows`),
   including a rename away from one;
2. a file under `.github/workflows/` or `.github/actions/` added, removed, renamed or
   anything else but `modified`;
3. in any other such file, an added or removed line that is not a comment and mentions
   `environment`, `secrets`, `pull_request_target`, `workflow_run`, `id-token`,
   `permissions`, `: write`, `runs-on` or `screenshot`, or a `uses:` outside `actions/` and
   `./`;
4. such a file whose patch GitHub did not return;
5. any workflow change in a repository with `workflow_edits: false` (mobile).

It runs on open PRs, not only at merge, because a PR's CI runs the PR's copy of a workflow
as soon as it is pushed. A merged PR whose head the guard never saw is read at the merge
audit. A head is read once; an unreadable file list counts as a failed audit and is retried.
Limits: it is a text match and can pause on a harmless line (a pause costs one resume), and
it cannot see a script a workflow calls, which is ordinary code the org can already change.
Checked against three real desktop PRs (fixtures in `tools/test-bugfix-host.py`): #397's
added test steps in `ci.yml` pass, #381's `release.yml` change pauses, #370's new
`stress.yml` pauses.

Not run live from this branch: a token minted with the narrowed permissions (that GitHub
honours a subset as asked, and the status code it gives for a permission the installation
lacks), the guard against a live PR, and a mobile merge audit. QA's live proof covers them.

## Deletes

Paperclip refuses `DELETE` on issues, agents and projects with 409 once they are referenced
(measured by QA). The warden never deletes anything; it cancels runs, blocks issues and
pauses the company, and besides the `GITHUB_TOKEN` and `GITHUB_TOKEN_WORKFLOWS` secrets the only other writes are the
unstick sweep's below.

## Unstick sweep (CB-479)

Every stall of 2026-11-25 was cleared by hand with the board login. The warden now makes
those moves itself, on each tick, for each company in `unstick_company_ids` in `warden.json`
(default: `company_id`). **It also reads a second company, the triage org, for one rule
only** (CB-469): `close_pinned` on a stuck routine fire, nothing else. Its id is
`triage_company_id` in `warden.json`, else `triage-company-id.txt` beside `pool-usage.json`
in the Paperclip instance folder (`triage_company_file` overrides that path), and the sweep
skips it when neither says. It never acts on a paused company,
including on the tick that pauses it. `unstick_actions` decides from the issue listing,
the recent runs, the assignee's newest comment on each blocked parent, `state.json` and
`pool-usage.json`; `Warden.unstick` only carries the answer out. Every action is logged as
`UNSTUCK: <kind> <identifier>`, and its state is recorded only after the call succeeds, so
a refused write is retried next tick.

| Kind | When | What it does |
| --- | --- | --- |
| `close_answered` | Task `blocked`, no children, no live run, an assignee agent, and its newest agent-written comment is that assignee's `ANSWER ...`; and either its parent is `done` or `cancelled`, or the one PR the answer names (`PR 427`; two different numbers, or any mention of mobile, is no proof) is the org's own (bot author, `bugfix/pc-` head, read with `gh`) and merged or closed (CB-487). A task whose cancel the release brake would count (description opens `Ticket: CB-n`, or no description in the row) closes only on a merged PR, never on a closed one or a settled parent, so a ticket given up on still reaches the brake. Checked ahead of the usage rule. | `PATCH {"status":"done"}`, with no comment, since a board comment wakes the assignee. QAF-194 and QAF-198 were blocked by Paperclip's workspace validation a minute before posting their merge verdicts and sat blocked for four hours; their parent passes then never settled. Comments are read only for blocked leaves, GitHub only when the parent has not settled, and an unreadable PR is no proof. The parent is then closed by `close_parent` on later ticks. |
| `wake_parent_for_answer` | A blocked leaf whose newest agent comment is its assignee's `ANSWER` but whose work is not over (no settled parent, no merged or closed PR: "could not answer", QAF-244), and its parent is `blocked` with no live run. Once per block of the leaf (`answer_wakes`, kept while the leaf stays blocked); two such leaves under one parent wake it once (CB-492). | One board comment on the **parent** naming the leaf, which wakes the lead to read the answer and decide. The leaf is never touched, so an engineer's task is never marked done. Before this, wake refused an answered task, `close_answered` refused an open one and `wake_parent` waited for every child to settle, so QAF-234 waited from 07:08Z until a person woke it at 08:42Z. The medic counts such a leaf as swept. |
| `close_parent` | Task `blocked`, at least one child and every child `done` or `cancelled`, no live run on it or any child, untouched 10 min. The assignee's newest comment is newer than every child's last activity, or the parent was woken 15+ min ago and is still blocked. | `PATCH /api/issues/<id> {"status":"done"}`. Paperclip counts cancelled children as open blockers and refuses the lead's own `done` (CB-424's release); the board login is allowed. |
| `wake_parent` | Same, with no newer report and no wake yet. | One board comment on the parent; a board comment on an open issue wakes its assignee. |
| `wake_child` | Child task (has `parentId` and an assignee agent) `todo`, `in_progress` or `in_review`, no live run, no activity for 20 min, and 20 min since the last wake. At most twice; activity of its own more than a minute after the last wake (beyond our comment's bump) starts the count again. | Board comment on the child. |
| `cancel_child` | The same child, still stalled 20 min after its second wake. | Recorded in `state["unstick"]["cancelled"]` first, then `PATCH {"status":"cancelled"}`, then a board comment on the parent. That list keeps it out of the release brake: a sweep cancel is the warden's repair, not the lead giving up, and is logged as `not a release`. |
| `reset_session` | Not settled, no live run, its latest run cancelled under an hour ago by the pool router (`errorCode` `ai_connection_pool_exhausted`, error "...over their usage limit..."), and `pool-usage.json` shows an `ok` account with every limit under 90% and none reached. Once per task per hour. **Only for a task that was never routed through the pool** (no run of it carries `aiRouterSelection`): a reset does not free a pinned task (below). | `POST /api/agents/<run's agent>/runtime-state/reset-session {"taskKey"}` (board-only route), then a wake comment. While that cancel is under an hour old the task counts as waiting on usage and the other rules leave it alone; once it is older it explains nothing, and a blocked pass whose children settled is closed by the parent rule. An unreadable pool file means no headroom. |

| `close_pinned` | Not settled, no live run, its latest run stopped on usage (the router's cancel above, or `failed` with `errorCode` `native_provider_usage_limit`, a run that hit the limit part-way) and nothing has happened on the task since (no activity more than 10 minutes after that stop). **There is no one-hour window here**, unlike the reset: a pinned task stays stuck however long the warden was down, so a stop from before an outage is still acted on. A task with activity after its stop is left to the other rules. One of its last five runs was routed to a pool account (`contextSnapshot.aiRouterSelection.binding.connectionId`, read from `GET /api/heartbeat-runs/<id>`, since the listing trims it). Some account with a **fresh** reading (`checkedAt` under 30 minutes old) has headroom; with none, nothing is done, and a `pool-usage.json` whose `writtenAt` is over 10 minutes old is logged once per writing as stale. The pinned account's reset time is read only from a fresh reading of it; a stale reading, no reset time, or a reset already past all count as **unknown**, and an unknown reset closes. **The 30-minute wait applies only to the router's cancel**, for which Paperclip schedules a retry at the reset; a run that `failed` part-way on the limit gets no retry, so it is never waited on, and a cancel whose reset has passed with the task still stopped closes too. The task is a child (`child:<parent>:<title>`), or a root task a routine made (`originKind` `routine_execution`, `routine:<originId>`); another root task is left. In the triage company, routine fires only. At most once per key per six hours (`repinned`); a second stop under the same key is logged once as `repin_repeat` and left alone, because it means the new task's pin also landed on a full account. | A bare `PATCH {"status":"done"}`, never `cancelled`, so the release brake does not count it. For a child, one board comment on the parent names it, says it was pinned to an exhausted account and until when, and tells the lead to re-create it with the same title and description, but only while the parent is open and is not itself being closed this tick. A comment on a done parent would reopen it, and a parent pinned to the same full account would only be woken into another stop. That parent's own close (if it is a routine fire) settles it, and the next pass finds the ticket with no live task (CB-493). For a routine fire, a log line, and its open children that carry a `Ticket:` line and have no run in flight are settled with the same bare `PATCH {"status":"done"}`, never `cancelled` (the lead's ruling): left open, a todo engineer issue would sit under a closed pass, and settled, its ticket comes back through CB-493's lost-claim outcome. A child with a live run is left; its PR brings the ticket back. In the triage company only the fire itself is closed. The PATCH carries no comment, and no comment follows it on the task itself: Paperclip moves a `blocked` or `done` task with an assignee back to `todo` when a board user comments, which would undo the close. Closing the routine fire matters because every routine here is `coalesce_if_active`, and Paperclip coalesces a fire into any open issue of that routine, `blocked` included (`OPEN_ISSUE_STATUSES` in its routines service), so one pinned pass would hold the org until the account's weekly reset. **Closing a parent leaves its children alone**, measured on the live instance on 2026-11-27: an unassigned scratch parent (ORB-241) with a `blocked` child carrying a `Ticket: CB-469` line (ORB-242) was closed by the board login with a bare `PATCH {"status":"done"}`. The child stayed `blocked` with its `updatedAt` unchanged, through a server restart too. Two earlier triage parents (ORB-220, ORB-196) were also closed `done` over a `blocked` child. So a close cancels no child, and the children the warden settles itself go `done`: nothing for the release brake to count. |

**Why a reset is not enough (CB-469, read from Paperclip 2026.1117.0's source, 2026-11-27).**
The first time a task is routed through a pool, Paperclip writes an `ai_connection_task_pins`
row keyed (company, pool, agent, task key) and logs `ai_connection.pool_selected`. The live
activity log held 21 of those for 21 distinct task keys, and later runs of the same task log
"Using task-pinned pool account". From then on the router is offered only the pinned member,
and Paperclip rejects any other answer ("The router returned an unauthorized member"), so our
plugin cannot move a task. When that account is over its limit, `choose.js` answers
`exhausted` with `retryAt` at the account's reset. Paperclip cancels the run
(`ai_connection_pool_exhausted`) and schedules a bounded retry at that time: up to days on a
weekly limit. **Nothing in Paperclip deletes a pin**, apart from cascades when the pool, the
agent or the company is deleted. `POST /api/agents/<id>/runtime-state/reset-session` clears
the saved task session (`agentTaskSessions`) and not the pin. So the CB-479 reset frees only a
task that was never routed: a session saved before the pool existed, which Paperclip passes to
the router as one member through `requireExisting` with no pin. That was QAF-50 on 2026-11-25,
where the reset by hand worked. A task routed since keeps its account until it is replaced by
a new issue, whose new key gets a new pin, and the router places that pin on an account with
room. That is why `close_pinned` closes the task rather than resetting it. The warden never
wrote to Paperclip's database to remove a pin: that is not an API, and a CLI upgrade could
change the table. None of this has been seen live yet. Since 2026-11-24 each company has had
one usage stop, both on 2026-11-25, and `state.json` has never recorded a reset.

Wakes and resets are kept per company in `state["unstick"]["companies"]`; the sweep-cancel
list is shared. A company whose listing comes back a full page (1000 rows) is skipped for the
tick, since a child cut off the page would make its parent look settled. Any error deciding
for a company is logged as `unstick sweep failed` and never reaches the rest of the tick,
whose state is saved only when the tick returns.

A blocked task with no children is not touched: that is what `stop_work` leaves behind on a
pause, and it must not be closed once the company resumes. Measured read-only against the
live instance before writing this: a live run shows as the row's `activeRun` (null when
none) or a `queued`/`running` run naming the issue; comments list newest first; the issue
listing returns up to 500 rows by default (the sweep asks for 1000); the reset route and the
comment wake were read from the installed server's source. None of the sweep's writes has
been run against the live org from this branch; the lead's deploy proof is a fixture task
the deployed warden clears.

## The medic (CB-485)

The warden stops the org; until now nothing noticed that it had stopped. Five
of its six pauses between 2026-11-24 and 11-26 were false alarms, and each
lasted until a person looked, the last for 13 hours. `host/medic.py check`
runs every 10 minutes from `launchd/org.uplift.orbweaver-bugfix-medic.plist`
(installed like the warden's, from the same checkout, reading the same
`warden.json`) and looks at the outcome instead of the causes. The org is
stopped when the company has been paused over 10 minutes, no run has started
in 45 with the routine active, a task has been blocked with nothing moving
under it (or an approval has waited) for 30 minutes unswept by the warden, or
the warden has not ticked in 15. While the company is paused only the first
and last are read: the other two are the pause's own effects.

When one holds, it asks a headless `claude -p` for a plan, with
`host/medic-runbook.md` and a snapshot of what it read as the prompt (also kept
in `medic-snapshot.txt`), under the pool accounts in turn
(`medic_config_dirs` in `warden.json`, else every distinct `configDir` in
`pool-accounts.json` in pool order, with the default `~/.claude` account last),
moving to the next only on an auth or usage failure.
One at a time (`medic.lock`), at most one launch per 20 minutes, 15-minute
timeout, all of it in `medic.log`. **The model has no tools**: `--tools ""`,
`--setting-sources ""`, `--strict-mcp-config`, an empty working directory. It
answers `{diagnosis, actions}` under `--json-schema`, and `medic.py` validates
every action against a closed list of verbs and arguments (issue references
must be open tasks in the snapshot) before running any; one invalid action
means none run and Warren is told once. An allow-listed Bash prefix was tried
first and dropped: QA measured `; id` and `| cat` running after an allowed
call. Each verb then re-checks its own preconditions: `resume` never lifts a
merge-audit or workflow-guard pause, lifts a develop-red pause only when it
reads develop green at head itself, a push-refusal pause only when it re-reads
the run log and finds no git refusal line, and never a pause that came back
within six hours of a medic resume. `close-task` (CB-487) closes a task with no
children only on the same proof as the sweep's `close_answered` (`warden.answered_and_over`):
its newest agent comment is its assignee's `ANSWER`, and the one org PR that answer names
is merged or closed (read with `gh` by `medic.py`, not taken from the model) or its parent
is settled; a ticket's task only on a merge. `wake` refuses
a task whose last agent comment is its `ANSWER` and one Paperclip blocked on a
failed workspace validation that is still awaiting repair. Each wake of
QAF-194 re-blocked it, and the wake that moved it to `todo` recovered the
incident, so every cycle opened a new one. So a refused action is remembered
against the task's `blockedTransitionAt` in `medic-state.json` across
incidents, and when only `stuck` holds and every stuck task was refused at its
current block, `medic.py` logs `NOT LAUNCHED` and escalates once instead of
starting the model, once in total for the same tasks at the same blocks
(`refused_escalated`), not once per incident. Records are dropped when a task is
no longer blocked, and a row with no block time is never recorded.

A board hand close has to be a bare `PATCH {"status":"done"}`. QAF-194's activity log
shows the 04:07 close set `done` and the board's closing comment, 49 ms later,
moved it back to `todo` (`source: comment`); the run that started failed validation and
re-blocked it at 04:19, which is the block the 04:55 medic wake then acted on. The
sweep's and the medic's closes post no comment for this reason, and `wake` refuses a
task that is `done` or `cancelled`.

An incident is the set of checks that hold plus the stuck tasks it was opened
over, each as its id at its `blockedTransitionAt` (never `updatedAt`, which every
comment moves), and each waiting approval. Only growth opens a new incident: a
newly stuck task or a re-block does, a task recovering does not (CB-492). Keyed by check names
alone, one escalation on `stuck` at 05:35Z on 2026-11-27 silenced the medic
until a person stepped in at 08:40Z while QAF-228 and then QAF-244 stood stuck.
An escalation refused because an earlier medic one is still pending is not
recorded as made, so it no longer mutes the incident either: it is logged once
per incident and retried on later launches, and the CB-487 once-in-total record
for refused tasks is made only when its escalation is actually sent.

`escalate` posts one pending `request_board_approval` to the company per
incident, which the app shows on the lead's orb as a waiting need titled
"Bug-fix org stopped: ...". After three launches in one incident with no
recovery, `medic.py` escalates on its own. `file-bug` writes the bug to
`~/.config/orbweaver-bugfix/medic-bugs/` and logs one `ACT file-bug` line naming
the file; nothing else (CB-492, lead's ruling). It used to hand the lead a
Paperclip task, "File a CB bug: false org stop", which the lead's rules forbid it
to do: QAF-228 sat blocked and held an incident open. A board approval was ruled
out because it reads as "the org needs you", and a bug is not a stop. The stuck
check ignores any task with that title left from before.
The host has no Jira credential of its own. Secrets (GitHub and Anthropic
tokens, `x-access-token:` URLs, `Authorization:` values) are redacted from the
snapshot, the prompt, `medic.log` (0600) and the bug before any is written.

## Run logs

`GET /api/heartbeat-runs/<id>/log` returns `{runId, store, logRef, content}`, `content` being
NDJSON of `{ts, stream, chunk}`; the warden joins the chunks before searching for `GH013`. A
run that never started (cancelled by a cap) answers 404 and is read as "no log", never as a
fault. Any other error reading a log is logged and the run is retried next tick; it neither
pauses nor stops the tick.

## Orbweaver-Mobile rulesets (CB-481)

`host/rulesets-mobile/` holds five rulesets for `Uplift-Foundation/Orbweaver-Mobile`, which had
none (and an unprotected `main`) when the warden's everyday token was widened to reach it. They
mirror what Orbweaver's rulesets protect, adapted to mobile:

| file | protects |
| --- | --- |
| `main-and-develop-prs-only.json` | `main` and `develop`: PR only, no deletion, no force push. Admins bypass only through a PR, as on Orbweaver, so nobody pushes straight to either |
| `develop-required-checks.json` | `develop`: `relay`, `contracts` and `tools` (GitHub Actions) must pass. They are the checks that run on every PR to `develop`, read off the check runs of mobile's merged PRs. `android` and `snapshots` are path-filtered and so cannot be required; nothing runs on a PR to `main`, so `main` requires no check |
| `main-admins-only.json` | `main`: updates, deletion and force push by admins only (the org's App never) |
| `other-branches-admins-only.json` | every branch but `bugfix/pc-*`, `develop` and `main`: admins only. No Dependabot bypass: GitHub refuses it on this repository (HTTP 422 "Actor Dependabot integration must be part of the ruleset source or owner organization", measured 2026-11-26), since Dependabot is not set up on mobile. No `screenshots` exclusion, because no mobile workflow pushes a branch |
| `tags-admins-only.json` | every tag: creation, update, deletion and force push by admins only (Orbweaver's two tag rulesets in one) |

Before applying, read the collaborator list: a non-admin who pushes branches would be locked out
of everything but `bugfix/pc-*`. On 2026-11-26 it was three admins and only `read` otherwise.

```bash
export REPO=Uplift-Foundation/Orbweaver-Mobile RULESETS_DIR=host/rulesets-mobile
python3 host/check-rulesets.py                 # before: five "missing", the diff apply would make
host/apply-rulesets.sh                         # dry run: five POSTs
host/apply-rulesets.sh --apply
python3 host/check-rulesets.py --push-probe    # after: five "matches", rules in force, pushes refused
GH_TOKEN=<a token the warden minted> python3 host/check-rulesets.py --push-probe   # the org's own view
```

`check-rulesets.py` reads each ruleset back against its file (`missing`, `matches`, or `differs`
naming the field), then GitHub's *effective* rules on `main` and `develop` (`GET
rules/branches/<b>`), and with `--push-probe`, only when those are in force, asks GitHub to move
each branch to a fresh commit object without force. Refused by a rule is the pass; refused for any
other reason is a fail. If GitHub ever accepted it, the script puts the branch back with a forced
update to the old head and exits non-zero. It works for Orbweaver too (`REPO`, `RULESETS_DIR`,
`REQUIRED_CHECKS`), where its read-back matches the three live bug-fix-org rulesets.

## The machines runner (CB-470)

The org's agents cannot hold an ssh key, install a build or touch the Windows PC, the
mini or the MacBook, and for most bugs that is right. For a bug whose proof needs a real
machine, `host/machines/runner.py` does it from outside the sandbox. A launchd job runs
`run-once` every 60 seconds as the logged-in user. The org asks with files; nothing else
reaches the machines. The agent-facing rules are the **Real machines** section of
`paperclip/bugfix-org/skills/cb-bugfix/SKILL.md`.

**The three verbs.** `machine_install` takes `/tmp/ow-install.lock` on the mini, keeps aside
what is installed on each named machine, installs the PR's build at `sha` and confirms the
stamped commit. `machine_run` runs one command on one machine the PR holds.
`machine_release` puts back exactly what was there, confirms it and drops the lock.
Requests are `<id>.json` files in `<company>/machine-spool/`; each is answered by a folder
`<company>/machine-results/<id>/` holding `result.json`, `stdout.txt` and `stderr.txt`,
and the file's name must equal its `id`. A valid request moves to `machine-spool/done/`, an
invalid one to `bad/`. Those that land in `bad/` **unanswered** (logged only) have no usable
`id`, a name that is not `<id>.json`, an `id` that already has a result folder (the first
answer stands, the repeat never runs) or a size over 64 KiB. An invalid request with a usable
`id` is answered as a refusal.
The runner creates the spool and results folders (mode 755) on every tick if they are missing. The sandbox profile
already lets agents write the first and read the second, because it allows all of
`~/.paperclip`, so nothing in it changes.

**What it refuses**, each written to `result.json` as `ok: false` with a one-line
`refused` sentence and nothing touched: a PR that is not open, not written by an allowed author or
whose head is not `sha`; a PR that changes any file under `.github/workflows/` or
`.github/actions/` (CB-481: a Mac install runs the PR's own `release.yml` on an `e2e/` branch with
the signing secrets, and the runner approves that deployment itself, so a workflow or composite
action edit would meet the secrets before review; the PR's changed files are read with
`gh api --paginate repos/<repo>/pulls/<n>/files`, a rename's old path included, the head is read
again afterwards and must still be `sha`, and an unreadable list or one at GitHub's 3000-file cap
is a refusal too); a second PR while one holds the machines; a `machine_run` without
a hold, on an unlisted machine, or after the PR's head moved since the install; a `machine_run`
on `macbook` whose command names a tool that sends keyboard or mouse input (`osascript`,
`cliclick`, `shortcuts run`, System Events, CGEvent, `pyautogui`, Quartz: CB-488, a person works at that machine;
a word match that catches the honest mistake, not a wall, and the mini is not covered because
it is headless); any unknown
verb, field or machine, and an `id` outside `[a-z0-9-]{1,40}`. Every answer has `id`, `verb`,
`pr`, `ok`, `refused`, `started_at`, `finished_at`; a run is `ok` only on exit 0 without a
timeout, and a failure after work started is `ok: false` with `refused: null`. A hold ends on
`machine_release` or after 45 minutes, when the runner restores and releases by itself.

The workflow refusal does not cover the scripts `release.yml` calls (`tools/build-macos-dmg.sh` and
the like): those are ordinary code the org can already change, and an install still runs them
with the signing secrets.

**When a restore fails twice**, or the runner's `state.json` cannot be read (it is kept aside
as `state.json.unreadable-<epoch>`), the runner writes `machine-results/NEEDS-ATTENTION.json`
holding `reason`, `at`, `machine`, `should_have`, `has` and `error`. Every `machine_install`
is then refused with "the machines need a person first: <path> says <reason>; installs are
refused until it is deleted". A person puts the machine right and deletes the file. An agent
that meets the refusal reports it on its task and does not retry.

**A known limit:** a Windows `machine_run` that times out is cut off on the mini, and a
process it started on the PC may keep running. The cap is 600 s.

**Config:** `~/.config/orbweaver-bugfix/machines.json`. Only `company_dir` is required.

| key | default | meaning |
| --- | --- | --- |
| `company_dir` | none | the folder that holds `bugfix-spool` |
| `allowed_authors` | `["app/orbweaver-bugfix"]` | PR authors as `gh pr view` spells them |
| `hold_cap_s` | `2700` | the hold cap, 45 minutes; set it low to test the cap |
| `lock_wait_s` | `600` | how long an install waits for the lock before refusing |
| `max_output_bytes` | `1048576` | output kept per stream of a `machine_run` |
| `lock_path` | `/tmp/ow-install.lock` | the install lock every team on the mini takes |
| `log_path` | `~/.config/orbweaver-bugfix/machines.log` | the audit log |
| `state_dir` | `~/.config/orbweaver-bugfix/machines` | the hold and what to restore |
| `repo` | `Uplift-Foundation/Orbweaver` | where PRs are read |
| `machines` | windows (ssh `windows`), mini, macbook (ssh `mbp`) | only listed machines are available |

**The log.** Every request, its machine, its PR, every command and its exit code, and every
refusal is appended to `machines.log`. The launchd job's own output is in
`/tmp/orbweaver-bugfix-machines.out` and `.err`.

**Deploy.** Run each step by hand; none is run by a test.

1. Write `machines.json` with at least `company_dir`.
2. Copy `host/launchd/org.uplift.orbweaver-bugfix-machines.plist` to `~/Library/LaunchAgents/`
   with `__REPO__` replaced by a checkout of `develop` outside `~/.paperclip`.
3. `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/org.uplift.orbweaver-bugfix-machines.plist`.
4. Check by hand, no agent: `/usr/bin/python3 <repo>/host/machines/runner.py run-once`
   exits 0 with an empty spool.
5. Update the live skill and the engineers' and lead's instructions in Paperclip (the skill
   file, then each agent's instructions bundle) so agents learn the verbs.

**Risks the decision accepted (CB-470).** A command the org writes runs as the host user on
the Windows PC, the mini and the MacBook. The org's unreviewed code is signed and
notarized under the Developer ID, so a Mac can install it before merge. While a PR holds
the machines they run that PR's build, not the one a person installed; the runner puts the
previous build back on release, and by itself at the cap. The bounds are the rules above:
the org's own PRs only, at their current head, one at a time, at most 45 minutes, every
command logged.

**Windows commands** run in the PC's ssh shell, which is Git Bash: use
`powershell -Command ...` for PowerShell and avoid bare `/FLAG` arguments. A Windows install
needs CI to have uploaded the `unsigned-win-x64` artifact for that sha first.

**The Mac build, measured twice on 2026-11-25: 5 to 20 minutes.** A Mac install waits for a
signed build from `release.yml` on `e2e/bugfix-pr-<n>`. One run (`release.yml` run
45689769046 on `e2e/cb-470-probe`) took 282 s from request to a stapled DMG on the mini;
another the same afternoon took 1238 s (20.6 minutes), because macOS CI jobs queue. On the
fast run the `release` deployment was pending 26 s after dispatch and the macOS leg took
3.5 minutes including signing and notarization; the runner waits for the whole run, so the Windows and osx-x64 legs count
too; the artifact download took 17 s (about 208 MB). One build, cached by sha, serves
both Macs, so the MacBook adds only a copy and an install. Each Mac's own install (stop,
swap, relaunch, verify) was not part of that measurement. The runner gives the build 25
minutes before it fails naming the run. The 45-minute hold starts only after the build,
when the install begins, so a slow build does not eat it. The `e2e/bugfix-pr-<n>` branch is deleted when the
Macs are restored, and `publish` never runs because it is tag-only. An agent should expect
a `machine_install` that includes a Mac to take 5 to 20 minutes plus any wait for the
install lock.

## The remote test runner (CB-494)

The mini is the always-on host for the OpenClaw gateway, the Discord bots, Paperclip and the
mobile relay, and the org's builds, UI suites and coverage merges once held its load near 20
for over an hour. So that work runs on the MacBook Air. Agents cannot ssh, so
`host/machines/remote.py` does it from outside the sandbox, as a second launchd job beside the
machines runner. It is separate because the machines runner answers a request inside one tick
and a Mac install holds that tick for up to twenty minutes; here a job is started detached on
the target and looked at every two seconds. The agent-facing rules are in the skill.

**The one verb.** A request is `<id>.json` in `<company>/remote-spool/`:

```
{"verb": "remote_test", "id": "pc412-ui3", "sha": "<full 40-character commit>", "run": "ui", "filter": "Orbweaver.Tests.ChatPanelTests"}
```

The agent never sends a command. `run` is one word and the runner builds the argv:

| `run` | what runs on the target | `filter` | cap |
| --- | --- | --- | --- |
| `build` | `dotnet build tests/Tests.sln -c Release`: the app plus UnitTests, IntegrationTests and UiTests. Not the root, whose solution is the app alone. UiScreenshots and the console suites are compiled by their own runs | none | 10 min |
| `unit`, `integration` | `dotnet test tests/<Suite> -c Release`, trx logged | optional class, sent as `--filter "FullyQualifiedName~X"` | 10 min filtered, 30 full |
| `ui`, `screenshots` | `dotnet test tests/<Suite> -c Release -- --report-xunit` | optional `Namespace.Class`, sent as `--filter-class X` | 10 min filtered, 30 full |
| `arrangement`, `glyph`, `transcript` | `dotnet run --project tests/<Suite> -c Release` | none | 10 min |
| `coverage` | `./tools/coverage.sh --base origin/develop` | none | 45 min |

`filter` must match `[A-Za-z0-9_.]{1,200}`. A full `ui` run is not passed through CB-119's
count guard (the target has no `pwsh`); the xunit report comes back for whoever wants to check
it, and CI remains the suite gate.

**The answer** is a folder `<company>/remote-results/<id>/`, renamed into place when the job
has ended or the request was refused: `result.json`, `output.txt` (capped at 5 MB),
`output-tail.txt` (the last 200 KB) and `files/`. The files have names the runner chose,
never a path the target chose: `unit-tests.trx`, `integration-tests.trx`, `ui-tests.xunit.xml`,
`ui-screenshots.xunit.xml`, and for coverage `coverage-summary.txt`, which opens with the
`merged N report(s)` line. A screenshot run also returns its PNGs under `files/screenshots/`,
kept only when the name is flat and plain. Anything else the target offers is dropped and
listed in `files_dropped`; the whole answer is capped at 50 MB. `result.json` has `id`, `verb`, `sha`, `run`, `filter`, `ok`, `refused`, `queued_at`,
`started_at`, `finished_at`, `target`, `target_hostname`, `checked_out_sha`, `exit_code`,
`timed_out`, `files` and `error`; a coverage run adds `merged_reports` and `base_sha` (the
develop commit `--base` compared against). `checked_out_sha` is `git rev-parse HEAD` in the
target's worktree after the checkout, not the request echoed back. `refused` is set only when
nothing ran on any target. A coverage run that exits 0 without `merged 4` is `ok: false`.
`remote-results/status.json` says whether each target answers, its load average, what is
running, what is queued, and `target_busy_with` (below). Answers older than seven days are removed.

**How the code gets there.** The runner keeps a bare mirror of the canonical repository in its
state folder and fetches its branches (`refs/heads` only, so a fork's pull-request commit is
not one). A `sha` that is not reachable from a branch there is refused before the target is
touched, so the agent commits and pushes to its own branch first. The mirror then pushes the
commit to a bare repository on the target over ssh, with develop's tip as `origin/develop`.
The target never talks to GitHub and holds no credential. On the target one worktree is
reused, forced to the commit and swept of untracked files and every `TestResults` folder on
each run, so builds are incremental; a coverage run also removes everything ignored.

**What it refuses or will not do.** A `run` outside the table; a `filter` outside the pattern
or on a run that takes none; any other field (there is no `command`); a sha not on a canonical
branch, or GitHub unreadable (fails closed); an eleventh request while ten wait. One job runs
at a time and the rest wait in arrival order. If no target answers, a request waits 15 minutes
and is then refused with "no target reachable; not run on the mini". There is no local
execution path in the file, and the mini is refused as a target twice over. When the config
is loaded: by name, by ssh host, by what `ssh -G` resolves the host to, and by address (the
mini's own interface addresses against the alias and whatever it resolves to). And on the
target, first thing in the script that starts a job and before anything is made or checked
out: a machine whose hostname is one of the mini's stops there. A job at its cap has its
whole process group killed. The result archive is cut on the target at the 50 MB cap, and
an over-long one is refused whole.

**A job the runner lost track of.** If the connection drops while a job starts or runs, the job
carries on. So every start first asks the target itself, in the same ssh call, whether a job
is still running there. If one is, nothing is touched: the request goes back to the head of
the queue, is tried again every 15 seconds, and `status.json` names the job in
`target_busy_with`. Each job records its own deadline on the target, and a left-behind job
past it is killed by the next start, so this wait is bounded by that job's cap. A start whose
connection dropped is answered `ok: false` with "could not start on air: ...".

**No build server outlives a job.** MSBuild node reuse, the MSBuild server and the shared
compiler are off in every job's environment, and each job ends with
`dotnet build-server shutdown`, then clears whatever is left in its own process group once its
answer is on disk (Avalonia's build helper outlives a build by minutes and none of those
switches stops it). Warm runs are slower for it; a 16 GB machine nobody watches
does not collect idle servers.

**Config:** `~/.config/orbweaver-bugfix/remote.json`. Only `company_dir` is required.

| Key | Default | Meaning |
| --- | --- | --- |
| `company_dir` | none | the folder that holds `bugfix-spool` |
| `targets` | `[{"name": "air", "ssh": "air"}]` | in order of preference; a later one is used only while every earlier one is silent. Only the Air is set up |
| `queue_cap` | `10` | requests waiting before the next is refused |
| `unreachable_wait_s` | `900` | how long a request waits with no target answering |
| `repo_url` | the canonical repository over HTTPS | what the mirror fetches |
| `log_path` | `~/.config/orbweaver-bugfix/remote.log` | every request, refusal, start (with its argv) and finish |
| `state_dir` | `~/.config/orbweaver-bugfix/remote` | the queue, the running job and the mirror |

**Setting up a target** (done on the Air on 2026-11-27, no sudo, no Homebrew): install the
.NET SDK at user level with the official script,
`curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet`.
The runner makes `~/orbweaver-remote` (a bare `repo.git`, the `work` worktree and `jobs/`) by
itself. The mini needs key-based `ssh <host>` to it.

**Install on the mini:**

1. Write `remote.json` with at least `company_dir`.
2. Copy `host/launchd/org.uplift.orbweaver-bugfix-remote.plist` to `~/Library/LaunchAgents/`
   with `__REPO__` replaced by a checkout of develop.
3. `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/org.uplift.orbweaver-bugfix-remote.plist`.
4. Check by hand: `remote-results/status.json` appears within a minute and says the Air is
   reachable, with its load.

**What the target needs beyond the SDK.** `git`, `bash`, `perl`, `tar` and `python3` (for
`coverage.sh`), all of which macOS has. `tests/IntegrationTests` also drives a real `tmux`
(`TmuxPlacementTests`, 7 tests): on a target without one, `integration` and `coverage` are red
on exactly those. The Air had none when this was written.

**Known limits.** A commit on any branch of the canonical repository runs as the host user on
the target, bounded by the command table. If the runner's state file is lost while a job runs,
that request is never answered (the log says so) and the job ends on the target by itself.
The launchd job's own output is in `/tmp/orbweaver-bugfix-remote.out` and `.err`.
