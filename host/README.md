# Bug-fix org: host side (CB-455)

Everything here runs on the Mac mini, outside the Paperclip sandbox. Plan:
`docs/plans/paperclip-bugfix-org-plan.md` §2, §4, §6, A.5.

Order of setup (the lead runs each; none of it is run by tests):

1. `host/setup-git-config.sh` — creates the empty global git config and checks the
   service plist sets `GIT_CONFIG_GLOBAL` to it (phase 0: keeps the org's git off
   the logged-in user's `~/.gitconfig`). Reload the service if it prints commands.
2. `host/apply-rulesets.sh`, then `--apply` — the three rulesets; prints a dry run first.
3. `python3 host/create_app.py --public-host <mini LAN address>` — send Warren the URL it prints.
4. Write `~/.config/orbweaver-bugfix/warden.json`: `company_id`, `paperclip_login`
   (`~/.config/paperclip-admin/board-login.txt`), `spool` (a folder inside the org's
   directory), and optionally `token_ceiling` and `max_runs_per_day`.
5. Install `host/launchd/org.uplift.orbweaver-bugfix-warden.plist` (replace `__REPO__`).

The warden pauses the company and then, every tick while it stays paused, cancels
queued and running runs and sets in-progress issues to blocked (a pause alone does
not stop a run in flight). It never resumes the company. `max_runs_per_day` is the
run brake, because Paperclip's `heartbeat.maxDailyRuns` is timer-only.
