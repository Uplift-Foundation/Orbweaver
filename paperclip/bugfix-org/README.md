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

## Status: live on the mini around the clock since 2026-11-24

**Every host step below is done on the mini, and each was read back.**

- **Rulesets:** the three in `host/rulesets` are `active`.
- **GitHub App:** `orbweaver-bugfix` is owned by Uplift-Foundation and installed on Orbweaver only, with exactly the six permissions in plan section 2. `host/create_app.py` refuses any other set.
- **Warden:** running under launchd every five minutes from a detached `develop` checkout outside `~/.paperclip`, where no agent can write. It writes the company's `GITHUB_TOKEN`.
- **Claude connection:** "Claude (W Thompson)", shared with all four agents. All four passed Paperclip's verification and are `ready`.
- **Jira:** six read actions for every agent. Comment and transition are for the lead only, behind two `block` policies created before the actions were enabled.
- **Build env:** set for both engineers.

**The pilot carried one ticket through on 2026-11-24.** CB-343: claimed 17:33Z, PR #365 opened by the App, QA's review with a broken-on-purpose control, merged by the App at 18:20Z as `9515045`, ticket moved to Done, warden audit clean. That took 47 minutes, one fix round and five agent runs.

**It runs around the clock, on Warren's word.** Since 18:25Z the routine fires every 30 minutes, every day. The run caps are 120 for the lead and 48 for each of the others, and the warden's `max_runs_per_day` is 300. The other warden brakes are unchanged, including the pause after two releases in a row. A paused org stays paused until a person, or the lead of a session working on it, resumes it.

**The App's lane, measured with its own installation token:**

- The token lists only `Uplift-Foundation/Orbweaver`.
- Creating and deleting `bugfix/pc-fixture-proof` were both accepted.
- Pushing `feature/pc-fixture-refused` and the tag `pc-fixture-refused` were both refused with GH013.

**Still unrun:** a stress dispatch from a spool request. The pilot's ticket was class 2. A worktree, `git`, `gh` and `dotnet` inside one, and the lead's merge and ticket moves were all measured by the pilot.

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
`timeoutSec: 3600` and `maxDailyRuns` (120 for the lead, 48 for each other
agent, since the org runs around the clock on Warren's word of 2026-11-24; they
bound a runaway, they are not a throttle. One ticket, CB-343, took three lead
runs, one engineer run and one QA run. The lead started at 3, which cannot
finish one ticket; it defers wakes rather than dropping them, see the table above) per agent; two fix rounds and ninety minutes per ticket,
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
   job. The org never takes that lock itself: the machines runner (`host/README.md`)
   does, on the org's request.
3. **AI connection** bound to each of the four agents, as for the triage org.
   **Bind each agent explicitly.** Starting the sign-in with `allAgents: true`
   did not bind agents that were still `verifying`. Verification then passed
   anyway, because its probe does not need a binding, and the first real run
   failed with "Not logged in · Please run /login" (measured 2026-11-24). Set
   `runtimeConfig.aiConnection` to `{connectionId, grantId, method: "subscription",
   mode: "shared", provider: "anthropic"}` on every agent, as the triage
   agents have it, keeping `runtimeConfig.heartbeat`.
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
   **On Atlassian's consent page, expand all three groups and leave exactly
   `read_jira`, `write_jira` and `search_jira` ticked.** Check the site says
   Uplifttech, switching account first if it offers another. Measured
   2026-11-24, three consents in a row:
   - all 11 permissions ticked: every connector call came back HTTP 400 from
     `mcp.atlassian.com`, even `atlassianUserInfo`;
   - only `read_jira` and `write_jira`: reads worked, but `searchJiraIssuesUsingJql`
     answered "tool not found", because the toolset sits under Search, not Read;
   - all three Jira toolsets: the grant matched the triage org's six OAuth scopes
     and search, read and the lead's write all answered, while a non-lead's
     transition was refused by the block policy.
   A click on an item inside a collapsed group does not register.
   The OAuth callback goes to `127.0.0.1:3100`, so a browser on another machine
   cannot complete it directly. Catch the callback path there, then replay it on
   the mini with a board session.
5. **GitHub identity.** The company secret `GITHUB_TOKEN`, written by the warden
   from the GitHub App described in plan section 2. Never a personal token.
   **The secret alone does not reach an agent's shell, and nor does binding it
   as `GH_TOKEN`.** Paperclip uses the company secret only for its own
   server-side clone and fetch. It also blanks `GH_TOKEN` and `GITHUB_TOKEN` in
   every run, even when they are bound on the agent. Measured 2026-11-24: bound
   under those names, `gh` reported no login; bound as `BUGFIX_GH_TOKEN`, the
   same secret arrived as a `ghs_` token. So bind it into every agent's
   `adapterConfig.env` as
   `BUGFIX_GH_TOKEN: {"type": "secret_ref", "secretId": "<GITHUB_TOKEN's id>", "version": "latest"}`.
   The skill's "GitHub access" section has the one-line prefix that hands it to
   `gh` and `git`. From an agent run, that prefix pushed `bugfix/pc-probe-push`,
   was refused GH013 on `feature/pc-probe-refused` and deleted the probe branch.
   A deliberate GH013 like that is a warden pause trigger, so add the run's id to
   `scanned_runs` in the warden's `state.json` before its next tick. A run reads
   the token once, when it starts, which is why the warden mints every ten
   minutes.
6. **Build environment.** In each engineer's environment set
   `NUGET_PACKAGES` to a folder inside the org's own Paperclip directory (the
   restore cache was 2.2 GB), `BUGFIX_BUILD_LOCK` to a path in that folder
   for the one-build-at-a-time lock, and `BUGFIX_SPOOL` to the warden's
   `spool` folder from `warden.json`, which is where the skill tells an engineer
   to write a stress request. Give `BUGFIX_SPOOL` to the lead and QA as well: the
   pilot's lead read its absence in its own environment as "stress runs are
   unavailable" and skipped every flaky-test ticket for that reason
   (2026-11-24). Without `BUGFIX_SPOOL` a flaky-test fix has no
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
9. **Persona pictures.** The import leaves each agent with Paperclip's generated
   avatar, and Orbweaver draws whatever picture Paperclip holds, so the orbs
   show generic avatars until the personas' own pictures are uploaded. Use the
   safe-for-work `<slug>.png` from `.profiles-assets/<slug>/`, never the
   `-nsfw` one. Paperclip caps an avatar at 512 KB and the source files are
   1.2–1.5 MB, so scale each to 512 px first (`sips -Z 512 -s format png`), then
   `PUT /api/companies/<id>/agents/<agent id>/avatar` with
   `{"imageBase64": "<base64 of the png>"}`. Read the agent back: `avatarUrl`
   should be `/api/assets/<id>/content`, not `/api/agent-avatars/...`. Done for
   all four on 2026-11-24. The MacBook's Orbweaver fetched the new pictures on
   its next roster poll.

## What the org does not do

Install on any of the three machines itself (it asks the machines runner, which
holds the keys and the lock; a PR that needed no machine says `Installed on: none`
and why); touch the
mobile repository or edit a
workflow file until its GitHub access covers them (a pass reports those bugs as
waiting on org access and writes nothing to Jira); tag or release; push to
`develop` or `main`. Security bugs are fixed like any other, with write-ups
that describe the class of flaw and the fix, never an exploit. A bug that needs
a real captured prompt is taken: QA captures it on the real machines.

## Every bug gets an outcome (CB-480)

On 2026-11-26 two passes in a row read 18 open bugs, took none and changed
nothing, and the next pass would have said the same. Since CB-480 every ticket
the query returns leaves a pass with one outcome: fixed, closed (already fixed,
duplicate, or not reproduced after a real attempt, with the evidence in the
comment, transition `41`, no claim), waiting on triage, waiting on org access
(mobile and workflow-file bugs, until the org's GitHub access covers them), waiting on another team's PR (only when an open PR's title names the
key), or taken. Closes are capped at five per pass so a pass
cannot sweep the board on thin evidence. The report ends with the counts and
names anything left untouched as a defect of the pass. The rules are the
skill's **Every ticket gets an outcome**.

