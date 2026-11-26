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
orbs go amber on the desktop; that is intended, because a warden pause needs a person. The warden never resumes the company; a person or
the CB-455 lead does. `max_runs_per_day` is the run brake because Paperclip's
`heartbeat.maxDailyRuns` is timer-only and does not bound assignment wakes.

## Where the warden's signals come from, and how far each can be trusted

The spool is written from inside the sandbox, so a misbehaving org can forge or suppress it.
Signals that matter are therefore read from Paperclip and GitHub, which the org does not
write to directly:

| Trigger | Source | Notes |
| --- | --- | --- |
| Two releases in a row | Newly `cancelled` engineer issues in the fix company (description starts `Ticket: CB-NNN`), read from Paperclip each tick. Issues of one ticket under one parent issue (one fix pass) count once, so a ticket released after its second fix round is one release (CB-471) | The lead sets the engineer's issue to `cancelled` on release and `done` on merge (its instructions; the PATCH has not been run). A cancelled issue without that first line (a QA or helper child task) is not a release; each release is logged with its CB key. Measured by QA: cancelled rows in the listing do carry `description`, so the next line is the rare path. A newly cancelled row that has no `description` field is read in full with `GET /api/issues/<id>`; if even that has none, the warden logs `release signal unreadable` and pauses, rather than skipping it or counting it blindly. The first tick only records what is already cancelled. A merge resets the count. The spool's `released` is a second, separate counter; the two are not added. |
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
(default: `company_id`, so triage can be added later). It never acts on a paused company,
including on the tick that pauses it. `unstick_actions` decides from the issue listing,
the recent runs, the assignee's newest comment on each blocked parent, `state.json` and
`pool-usage.json`; `Warden.unstick` only carries the answer out. Every action is logged as
`UNSTUCK: <kind> <identifier>`, and its state is recorded only after the call succeeds, so
a refused write is retried next tick.

| Kind | When | What it does |
| --- | --- | --- |
| `close_parent` | Task `blocked`, at least one child and every child `done` or `cancelled`, no live run on it or any child, untouched 10 min. The assignee's newest comment is newer than every child's last activity, or the parent was woken 15+ min ago and is still blocked. | `PATCH /api/issues/<id> {"status":"done"}`. Paperclip counts cancelled children as open blockers and refuses the lead's own `done` (CB-424's release); the board login is allowed. |
| `wake_parent` | Same, with no newer report and no wake yet. | One board comment on the parent; a board comment on an open issue wakes its assignee. |
| `wake_child` | Child task (has `parentId` and an assignee agent) `todo`, `in_progress` or `in_review`, no live run, no activity for 20 min, and 20 min since the last wake. At most twice; activity of its own more than a minute after the last wake (beyond our comment's bump) starts the count again. | Board comment on the child. |
| `cancel_child` | The same child, still stalled 20 min after its second wake. | Recorded in `state["unstick"]["cancelled"]` first, then `PATCH {"status":"cancelled"}`, then a board comment on the parent. That list keeps it out of the release brake: a sweep cancel is the warden's repair, not the lead giving up, and is logged as `not a release`. |
| `reset_session` | Not settled, no live run, its latest run cancelled under an hour ago by the pool router (`errorCode` `ai_connection_pool_exhausted`, error "...over their usage limit..."), and `pool-usage.json` shows an `ok` account with every limit under 90% and none reached. Once per task per hour. | `POST /api/agents/<run's agent>/runtime-state/reset-session {"taskKey"}` (board-only route), then a wake comment. While that cancel is under an hour old the task counts as waiting on usage and the other rules leave it alone; once it is older it explains nothing, and a blocked pass whose children settled is closed by the parent rule. An unreadable pool file means no headroom. |

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

When one holds, it writes `medic-snapshot.txt` and starts `claude -p` on
`host/medic-runbook.md` under a pool account's config dir (`medic_config_dir`
in `warden.json`, else the first pool account with one). One at a time
(`medic.lock`), at most one launch per 20 minutes, 15-minute timeout, all of it
in `medic.log`. The model's only command is `medic.py act <verb>`
(`--allowedTools`, `--permission-mode dontAsk`, `--permission-prompts none`,
`--safe-mode`); everything else is refused, which was proved with a real
launch before this was merged. Each verb re-checks its own preconditions:
`resume` never lifts a merge-audit or workflow-guard pause, lifts a develop-red
pause only when it reads develop green at head itself, and a push-refusal pause
only when it re-reads the run log and finds no git refusal line.

`escalate` posts one pending `request_board_approval` to the company per
incident, which the app shows on the lead's orb as a waiting need titled
"Bug-fix org stopped: ...". After three launches in one incident with no
recovery, `medic.py` escalates on its own. `file-bug` writes to
`~/.config/orbweaver-bugfix/medic-bugs/`, because the host has no Jira
credential: the org's Jira goes through Paperclip's connector, comment and
transition only.

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
a hold, on an unlisted machine, or after the PR's head moved since the install; any unknown
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
