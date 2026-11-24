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
2. `host/apply-rulesets.sh`, then `--apply` — the three rulesets; a dry run first.
3. `python3 host/create_app.py --public-host <mini LAN address>` — send Warren the URL it prints.
4. Write `~/.config/orbweaver-bugfix/warden.json`: `company_id`, `paperclip_login`
   (`~/.config/paperclip-admin/board-login.txt`), `spool` (a folder inside the org's
   directory), and optionally `token_ceiling` and `max_runs_per_day`.
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
| Two releases in a row | Newly `cancelled` engineer issues in the fix company (description starts `Ticket: CB-NNN`), read from Paperclip each tick | The lead sets the engineer's issue to `cancelled` on release and `done` on merge (its instructions; the PATCH has not been run). A cancelled issue without that first line (a QA or helper child task) is not a release; each release is logged with its CB key. Measured by QA: cancelled rows in the listing do carry `description`, so the next line is the rare path. A newly cancelled row that has no `description` field is read in full with `GET /api/issues/<id>`; if even that has none, the warden logs `release signal unreadable` and pauses, rather than skipping it or counting it blindly. The first tick only records what is already cancelled. A merge resets the count. The spool's `released` is a second, separate counter; the two are not added. |
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

## Deletes

Paperclip refuses `DELETE` on issues, agents and projects with 409 once they are referenced
(measured by QA). The warden never deletes anything; it cancels runs, blocks issues and
pauses the company, and that is all it writes besides the `GITHUB_TOKEN` secret.

## Run logs

`GET /api/heartbeat-runs/<id>/log` returns `{runId, store, logRef, content}`, `content` being
NDJSON of `{ts, stream, chunk}`; the warden joins the chunks before searching for `GH013`. A
run that never started (cancelled by a cap) answers 404 and is read as "no log", never as a
fault. Any other error reading a log is logged and the run is retried next tick; it neither
pauses nor stops the tick.
