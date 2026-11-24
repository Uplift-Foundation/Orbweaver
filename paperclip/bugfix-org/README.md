# Orbweaver Bug Fix - a Paperclip org (CB-455)

This directory is a [Paperclip](https://github.com/paperclipai/paperclip)
company package: four agents that take open `Bug` tickets from the CB board,
fix them in their own git worktrees, open PRs against `develop`, have QA review
them, merge them when both required CI checks are green, and move the ticket.
The plan and every reason behind a choice here is
`docs/plans/paperclip-bugfix-org-plan.md`; this file is the setup and the
honest state of what has been run.

| Agent | Role | Does |
| --- | --- | --- |
| Marisol Vandenberg | CTO, lead | selects and claims tickets, plans, merges, moves the ticket, releases it |
| Hana Moriyama | engineer | fixes one ticket in a worktree, opens the PR |
| Rafaela Quintero | engineer | the same, on the other ticket |
| Tamsin Oyelaran | QA | reviews one sha, posts the review comment naming both rids |

The personas are ones the triage org does not use, so no agent draws two orbs.
Their pictures live in `.profiles-assets/`, which is not checked in.

## Status: set up on the mini, 2026-11-24; first pilot run next

**Every host step below is done on the mini, and each was read back.**

- **Rulesets:** the three in `host/rulesets` are `active`.
- **GitHub App:** `orbweaver-bugfix` is owned by Uplift-Foundation and installed on Orbweaver only, with exactly the six permissions in plan section 2. `host/create_app.py` refuses any other set.
- **Warden:** running under launchd every five minutes from a detached `develop` checkout outside `~/.paperclip`, where no agent can write. It writes the company's `GITHUB_TOKEN`.
- **Claude connection:** "Claude (W Thompson)", shared with all four agents. All four passed Paperclip's verification and are `ready`.
- **Jira:** six read actions for every agent. Comment and transition are for the lead only, behind two `block` policies created before the actions were enabled.
- **Build env:** set for both engineers.

The routine and its trigger stay off until a pilot run has carried one ticket through.

**The App's lane, measured with its own installation token:**

- The token lists only `Uplift-Foundation/Orbweaver`.
- Creating and deleting `bugfix/pc-fixture-proof` were both accepted.
- Pushing `feature/pc-fixture-refused` and the tag `pc-fixture-refused` were both refused with GH013.

**Still unrun:** a worktree realised for a real run, `git`, `gh` and `dotnet` inside one, a stress dispatch from a spool request, and the lead's merge and ticket moves. The pilot run measures each of these.

**Deviation from the plan:** appendix A.4 lists `tools/check-pr-body.sh`, the
body rules lifted out of `tools/require-team-sdlc.sh` and called by both. It was
not built. `tools/test-paperclip-bugfix-org.py` feeds the skill's PR template to
the real hook instead, with a control that the hook denies it without the
"Installed on" line, so the org's body shape is checked by the hook's own code
and cannot drift from it, but an agent has no script to check its body with
before `gh pr create`; the hook is what refuses a bad one.

**Import dry run, measured 2026-11-24 on the Mac mini** against the live
instance (Paperclip 2026.1117.0), `paperclipai company import paperclip/bugfix-org
--target new --include company,agents,projects,issues,tasks,skills --dry-run --yes --json`:
exit 0, `errors: []`, `warnings: []`, nothing written. The preview plans one
company, four agents, one project and one routine, all `create`. Read back from
its manifest, every field survived: each agent's role, title, `reportsTo` (the
three non-leads to marisol-vandenberg), model, `approve-all`, `timeoutSec: 3600`,
the three `env` bindings, `maxConcurrentRuns: 1` and the per-agent
`maxDailyRuns`; the `cb-bugfix` skill on each; the project's `git_repo`
workspace on `develop` and, while the policy was still in the YAML, its
`git_worktree` policy with `baseRef` `origin/develop` and `branchTemplate`
`bugfix/pc-{{slug}}` (see the next paragraph for why it left); the routine as
`coalesce_if_active`, `skip_missed` and a `0 10 * * 1-5` Pacific trigger, imported
**enabled** (hence setup step 1). Nothing was dropped or misread, so no field
needed changing. This says the package parses; it does not say a real import,
a worktree or a run works.

**Same dry run against the existing company, measured 2026-11-24**
(`--target existing -C f6845723-4ffd-4fab-bedc-db07c47b5bba`, the fixture company
being renamed "Orbweaver Bug Fix"). The first attempt **failed**:
`errors: ["Safe import does not allow project orbweaver-develop executionWorkspacePolicy."]`.
The CLI sends every existing-company import through Paperclip's agent-safe
route, which refuses a project's `executionWorkspacePolicy` (and workspace
`setupCommand`/`cleanupCommand`, task `executionWorkspaceSettings` and
`assigneeAdapterOverrides`, and non-schedule routine triggers); a `--target new`
import goes through the board route and had accepted it. So the worktree policy
moved out of `.paperclip.yaml` into `host/project-workspace-policy.json`, applied
after import (setup step 8). With that change both targets dry-run clean:
`errors: []`, `warnings: []`, the four agents, project and routine all `create`,
the agents with the same fields as above; the existing-company run reports the
company action as `none`, as it should. The policy JSON itself matches the
`projectExecutionWorkspacePolicySchema` keys in Paperclip's source; applying it
has **not** been run.

## Measured, assumed, and read from source

| Claim | Label |
| --- | --- |
| The YAML has no anchors, no trailing comments and no unquoted colon strings, by a reader of this repo's own | **measured** (the test), not run through Paperclip's reader |
| `heartbeat.maxConcurrentRuns` and `maxDailyRuns` are fields Paperclip's agent policy reads; `adapter.config.env` takes `{type: plain, value}` bindings; a project's `executionWorkspacePolicy.workspaceStrategy` takes `git_worktree`, `baseRef`, `branchTemplate` | **read from source** (Paperclip 2026.1117.0), not run on an import |
| `maxDailyRuns` gates assignment wakes | **measured** by Ines on the mini's live Paperclip, 2026-11-24: with a cap of 16 and three assignments, one run happened; the control with no cap ran all three. An earlier reading of `heartbeat/timer-policy` that it caps only timer heartbeats was wrong. **Deferred wakes are not dropped:** they fire when the cap resets at UTC midnight or when it is raised, so a backlog of assignments can burst later. The cap delays a burst, it does not end it. What stops a backlog burst is the warden's pause of the company (plan section 6) |
| The lead's `claude-opus-5-5` model id is accepted by the connection | **assumed**; the id appears in Paperclip's source and the import accepted it, which says nothing about the connection accepting it; the first real run settles that |
| `defaultMode: isolated_workspace` with the strategy realises one worktree per issue under the sandbox profile | **assumed** (A.7 row 2) |
| `git`, `gh` and `dotnet` run unprompted under `approve-all` with the opt-out variables set | **assumed** for a real run; `dotnet build` and four test classes passed in a shell under the profile (plan A.2, **measured**) |
| The profile's new deny stops a process under the profile creating the lock | **measured** on the MacBook, 2026-11-24: a copy of the rule with the lock's name swapped for a scratch name refused `touch` (`Operation not permitted`) while `touch` of another name in `/private/tmp` succeeded. The live lock path was deliberately not touched, and the live service profile has not been re-rendered, so this says nothing about the running service |
| The PR body shape in the skill passes `tools/require-team-sdlc.sh` | **measured**: the real hook allowed it, and denied it with the `Installed on` line removed |

## How "released" and "merged" show up in Paperclip

The host decides a ticket was released from Paperclip's API, not from what an
agent wrote. At the end of a ticket the lead sets the **engineer's Paperclip
issue** (the one titled with the change words, in the project "Orbweaver
develop") to `done` when the PR merged to `develop`, or to `cancelled` when it
released the ticket, with `PATCH /api/issues/<id>` through `call_api`. The
Jira comment `[bugfix-org] released` accompanies it for people. No label is
used. Until a ticket ends, that issue is `todo`, `in_progress` or `in_review`.
The status values are Paperclip's own (`ISSUE_STATUSES`, read from source); the
lead actually making that PATCH has not been run.

## The brakes (plan section 6)

Two tickets per run; routine `coalesce_if_active`; `maxConcurrentRuns: 1`,
`timeoutSec: 3600` and `maxDailyRuns` (3 for the lead, 6 for each other agent,
values chosen, not measured; it defers wakes rather than dropping them, see the table above) per agent; two fix rounds and ninety minutes per ticket,
then release. The warden (host side, not in this package) pauses the company on
the conditions in plan section 6. `budgetMonthlyCents` is set but is not a
brake: on a subscription connection spend reads 0.

## Prerequisites the package cannot set

Measured by QA on the mini's live Paperclip on 2026-11-24 (not by me):

- **`GIT_CONFIG_GLOBAL`** must point at an empty file in the service's launchd
  environment, here `~/.paperclip/instances/default/gitconfig`. Without it any
  `git_repo` workspace checkout fails under the sandbox profile.
- **Two instance flags, `enableIsolatedWorkspaces` and
  `enableWorktreeRunExecution`, must be on.** `git_worktree` does nothing
  without them. Both are on now.
- **The import goes into the existing company "Orbweaver Bug Fix"**, not a new
  one, because a company's AI connection cannot be shared between companies and
  the sign-in is a browser step. Use `--target existing -C <company id>`. The
  package does not assume a new company; see the dry runs above.

## Setup on the host

Do the triage org's setup first (`paperclip/triage-org/README.md`): Paperclip
installed, the AI connection, the Jira connector, the sandbox. Then:

```bash
paperclipai company import paperclip/bugfix-org --target existing -C <company id> --dry-run
paperclipai company import paperclip/bugfix-org --target existing -C <company id>
```

Then, none of which a package can carry:

1. **Pause the routine and disable its trigger**, straight after import. It
   imports active with its schedule trigger `enabled: true`, and pausing the
   routine alone leaves that trigger enabled (measured by QA). Do both
   (`PATCH /routine-triggers/<id>` with `{"enabled": false}`, then read back
   `enabled: false` and the routine `paused`); re-enabling needs both, the
   routine back to `active` and the trigger `enabled: true`. Nothing below is in
   place yet.
2. **Sandbox profile.** Re-render `paperclip/triage-org/host/paperclip-service.sb.template`
   (the command is in the triage README) so the profile gains the deny for
   `/private/tmp/ow-install.lock`, then restart the service through its launchd
   job. The org never installs, so it never takes that lock.
3. **AI connection** bound to each of the four agents, as for the triage org.
   This comes before any run: imported agents arrive `paused` with
   `lifecycleState: verifying` (measured), and verification needs the
   connection. An assignment to a verifying agent queues a run that never
   starts, so cancel it rather than waiting.
   **Check each agent's harness after binding.** An import made while the
   company had no Claude connection gives every agent
   `adapterConfig.provider: codex`. Verification then fails with
   `harness_test_failed` even with the connection bound, and pauses itself after
   three tries (measured 2026-11-24). Set
   `PATCH /api/agents/<id>` `{"adapterConfig": {"provider": "acpx", "acpxAgent": "claude"}}`
   (a merge; it keeps the env bindings), then `POST /api/agents/<id>/lifecycle/retry`.
   All four reached `ready` within a minute.
4. **Jira, block first.** Add a tool policy of type `block` naming
   `addCommentToJiraIssue` and `transitionJiraIssue` and the three non-lead
   agents, at a priority ahead of the others. Only then finish the connector
   with the six read actions (`searchJiraIssuesUsingJql`, `getJiraIssue`,
   `getAccessibleAtlassianResources`, `atlassianUserInfo`,
   `getVisibleJiraProjects`, `getJiraIssueRemoteIssueLinks`) plus those two for
   the lead. Check each agent's access afterwards: eight for the lead, six for
   the others. In that order there is no moment at which a non-lead can comment
   or move a ticket. Paperclip cannot limit a transition to Bug tickets, so the
   lead's instructions carry that rule.
5. **GitHub identity.** The company secret `GITHUB_TOKEN`, written by the warden
   from the GitHub App described in plan section 2. Never a personal token.
6. **Build environment.** In each engineer's environment set
   `NUGET_PACKAGES` to a folder inside the org's own Paperclip directory (the
   restore cache was 2.2 GB), `BUGFIX_BUILD_LOCK` to a path in that folder
   for the one-build-at-a-time lock, and `BUGFIX_SPOOL` to the warden's
   `spool` folder from `warden.json`, which is where the skill tells an engineer
   to write a stress request. Without `BUGFIX_SPOOL` a flaky-test fix has no
   way to get its 30-run proof, because the warden reads requests only from
   the spool. Both are absolute paths, so the package
   cannot carry them. The package sets `AVALONIA_TELEMETRY_OPTOUT=1`,
   `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1` itself.
7. **Project workspace.** Import creates the project "Orbweaver develop" with a
   `git_repo` workspace on `develop`. Confirm it exists before the first run,
   and put the project's id in the lead's instructions if the `create_task`
   `projectId` is wanted explicitly.
8. **Worktree policy.** Apply it to that project (the import route refuses it):
   `paperclipai project update orbweaver-develop -C <company id> --execution-workspace-policy-json "$(cat paperclip/bugfix-org/host/project-workspace-policy.json)"`,
   then read the project back to confirm `workspaceStrategy` is `git_worktree`
   with `branchTemplate` `bugfix/pc-{{slug}}`. Run by QA on 2026-11-24 against
   the existing company and read back as expected. `defaultProjectWorkspaceId`
   did not need setting: the imported `git_repo` workspace was already primary.

## What the org does not do

Install on any of the three machines (so its PR bodies say `Installed on: none`
and list what an install would have shown under `## Not verified`); touch the
mobile repository; fix a bug that needs a real captured prompt, a real Windows
timing or any observation on an installed app; fix a security ticket; edit a
workflow file; tag or release; push to `develop` or `main`.
