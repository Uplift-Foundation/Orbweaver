---
schema: agentcompanies/v1
name: Orbweaver Bug Fix
slug: orbweaver-bug-fix
description: Takes open CB bugs from Refinement to a merged PR on develop on its own - claims the ticket, fixes it with tests, has QA review it, merges it once both CI legs are green, and moves the ticket. Bug tickets only.
version: 0.1.0
---

# Orbweaver Bug Fix

A four-agent org that picks up `Bug` tickets in Jira project **CB** that the
triage org has already commented on, fixes them in its own git worktree, opens
a PR against `develop`, has a QA agent review it, merges it when both required
checks are green, and moves the ticket along the board.

It is separate from the triage org (`paperclip/triage-org`) on purpose: its own
pause switch, its own secrets, its own budgets, and personas the triage org does
not use, so no agent draws two orbs in Orbweaver.

It never installs anything on the three machines and never works on a bug that
needs an observation on a real installed app. `skills/cb-bugfix/SKILL.md` is the
rulebook; `docs/plans/paperclip-bugfix-org-plan.md` is the plan and the reasons.

Import with `paperclipai company import paperclip/bugfix-org --target new`
(add `--dry-run` first). See `README.md` beside this file for the host setup.
