---
schema: agentcompanies/v1
name: Orbweaver Bug Triage
slug: orbweaver-bug-triage
description: Triages new bugs on the CB Jira board every weekday morning and comments its findings. Comment only - it never moves, edits or fixes a ticket.
version: 0.1.0
---

# Orbweaver Bug Triage

A three-agent org, staffed by the same personas the CB feature teams use, that
reads new `Bug` tickets in Jira project **CB** on a schedule and leaves one
triage comment on each.

It exists so a bug filed overnight has a duplicate check, a type check, a
suggested priority and a likely cause on it before a person opens the board.

Import with `paperclipai company import paperclip/triage-org --target new`
(add `--dry-run` first). See `README.md` beside this file for the host setup.
