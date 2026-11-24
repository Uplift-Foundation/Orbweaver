# A Paperclip org that fixes CB bugs: plan (CB-455)

Epic CB-419. Requirement: CB-455, "a Paperclip company that fixes open CB bugs end to end, visible as orbs in Orbweaver". This is a plan only. Nothing here has been built, and nothing on the live Paperclip, the repository's settings or Jira was changed while writing it.

Read at `origin/develop` `53b1ce6d8`. Paperclip is `2026.1117.0` on the Mac mini. Measurements are dated 2026-11-24 unless marked. Each fact is labelled **measured** (I ran it), **read from source** (Paperclip's installed code, with the file named in the appendix), or **assumed** (nobody has run it; phase 0 settles it).

## 1. What Warren sees

**On the desktop and phone.** A second Paperclip company, "Orbweaver Bug Fix", beside the triage org: four orbs, one per agent, drawn by the Paperclip support that already exists (CB-421). A lead, two engineers and a QA agent. An orb is busy while its agent has a run, and quiet otherwise. In normal operation no orb goes amber, because the org never waits for a person. If the org stops itself (§6) the company shows as paused in Paperclip; how Orbweaver draws a paused company was not checked for this plan.

**On the CB board.** A bug the org takes moves by itself:

| Board | What happened | Comment the org leaves |
| --- | --- | --- |
| Refinement → Development | the org claimed it | `[bugfix-org] claimed`, with the branch name |
| Development → Testing | the PR is open | the PR link and what the fix is |
| Testing → Done | the PR is merged to `develop` | the merge sha, what was verified and what was not |
| back to Refinement | the org gave up | `[bugfix-org] released`, what it learned, why it stopped |

A ticket in Development with a `[bugfix-org] claimed` comment is the org's. Human-led teams skip it by the same rule they already use for each other.

**On GitHub.** PRs against `develop` from `orbweaver-bugfix[bot]`, on branches named `bugfix/pc-<change>`, with the CB key in the title and a body that carries `## Verified`, `## Not verified` and an "Installed on" line. The QA agent's review is a PR comment naming both rids. The bot merges only after both required checks are green.

**What it will and will not fix.** Of the 31 open bugs measured today (§3), the org can take about 19 with proof that needs no person. It leaves 12 alone: two a human team holds, two that live in the mobile repository, two security fixes, and six that need an observation on a real installed app.

**The one thing Warren does.** Open one link and confirm twice, to create the org's GitHub identity (§2). Everything else is built and switched on by the CB-455 team.

## 2. Identity: a GitHub App, not a person's token

**Choice: a GitHub App owned by `Uplift-Foundation`, installed on `Orbweaver` only.** Permissions: Contents read and write, Pull requests read and write, Checks read, Actions read, Commit statuses read, Metadata read. Nothing else: no Workflows, no Administration, no Environments, no Secrets.

Why this and not the alternatives:

| Option | Verdict |
| --- | --- |
| GitHub App installation token | **Chosen.** No new account. Tokens live one hour, so a stopped org loses its access by itself. It is not an admin and is not a bypass actor on either ruleset. Without the Workflows permission GitHub refuses any push that changes a workflow file. |
| Fine-grained PAT from a dedicated account | Works with Paperclip unchanged, but needs a new GitHub account, an invitation, a 2FA setup and a token that lives up to a year in Paperclip's store. More browser steps, and no dead-man's switch. |
| Deploy key plus something else | A deploy key cannot open or merge a PR, so it needs a second credential anyway. Rejected. |
| Paperclip's hosted GitHub connector | Its App's permissions are not in the install, and tokens would pass through Paperclip's cloud. Rejected unmeasured. |
| The `wtvamp` token | Never. Measured: under the sandbox profile the keychain entry, `~/.config/gh` and `~/.ssh` are all unreachable (0 bytes, three ways), so the org cannot pick it up by accident either. |

**Where the key lives.** The App's private key stays outside the sandbox, in `~/.config/orbweaver-bugfix/` at 0600. A small host-side job, the **warden** (§6), mints a one-hour installation token and writes it into the fix company's `GITHUB_TOKEN` secret, which is where Paperclip's own `git` and `gh` launchers look. The agents can read that token (anything Paperclip holds, an agent with a shell can read), so the token's scope and the rulesets are the control, exactly as the Atlassian grant is for the triage org.

**What the rulesets must gain.** Measured today: the ruleset on `main` and `develop` requires a PR and the two build checks with zero approvals, and only admins bypass it. That already binds the App on `develop`. Three gaps remain for any non-admin writer, so the CB-455 team adds, with the admin token and no browser:

1. `main`: restrict updates to admins. Today a non-admin could merge a green PR into `main`.
2. Every branch except `bugfix/pc-*`, `develop` and `main`: restrict creation, update and deletion to admins, with the Dependabot app as a bypass actor so `dependabot/*` keeps working, and `screenshots` excluded so the screenshot publisher keeps working (GitHub refuses GitHub Actions as a bypass actor on a repository ruleset: HTTP 422, measured when the rulesets were applied). This is what stops the App deleting a human team's branch or creating `e2e/*` or `release/*`.
3. All tags: restrict creation to admins (`v*` already is).

Every other writer on the repository is an admin (measured: three admins, the rest read), so these rules cost the human teams nothing. Each is proved with a refused push from the App's token next to an accepted one (§7, phase 0). The `bugfix/pc-` prefix names the owner, not an issue number; the rest of the name is the change, per the branching rule.

**What stays unenforced, plainly.** Pushing a branch and merging a PR are the same GitHub permission. A misbehaving agent holding the token could merge its own green PR without the QA agent's review. What bounds that is the ruleset (CI green on both rids), which is the same bound a human-led team has. The warden audits every bot merge and pauses the company on one with no QA comment. Enforcing the review would mean keeping the token out of the sandbox and having the warden push and merge; that is designed in the appendix (A.6) and not recommended for the first release.

**Warren's step, as one link.** The CB-455 team runs a one-shot helper on the mini and sends `http://192.168.0.127:<port>/`. The page posts the App manifest to GitHub. Warren clicks **Create GitHub App**, then on the next page picks **Only select repositories → Orbweaver** and clicks **Install**. The helper stores the key and exits. Assumed, not measured: GitHub's manifest flow has not been run on this org. The lead sends Warren the link; nobody drives his browser.

## 3. Which tickets, in what order

**Measured today:** 31 open `Bug` tickets (CB-455 counted 32 when filed). Two are in Development (CB-294, CB-413; CB-413 has open PRs #336 and #337).

**A ticket is skipped when any of these holds:**

- its status is not Refinement;
- an open PR names its key in the title or body;
- a pushed, unmerged commit names its key (`git log --remotes --not origin/develop --grep`), since branches here are named for the change and do not carry keys;
- it has no `[triage]` comment yet (the triage org's likely-cause note is the fix org's input);
- it is in a class the org leaves alone (below).

**Claiming.** The lead moves the ticket to Development and comments `[bugfix-org] claimed`, then reads the ticket back. If anyone else's comment or transition landed in between, it releases the ticket. A human who wants a ticket the org holds comments on it; the org stops and releases at its next run.

**Rank:** Jira priority first, then class in the order below, then oldest.

| Class | Tickets | Proof with no person |
| --- | --- | --- |
| 1. Flaky tests in this repo. First because each one costs every team reruns. | CB-336, 352, 362, 378, 382, 389, 390, 398, 410, 426, 451 | the class run 30 times on both rids on GitHub's runners (`stress.yml`, §4) |
| 2. Logic bugs with a testable seam | CB-343, 345, 347, 387, 408, 415, 416, 432 | a test that fails before and passes after, plus CI on both rids. CB-432's Windows quoting is exercised by a Windows-leg integration test. |
| 3. Left to human-led teams | CB-359, 373, 405, 414, 429 (need a real captured prompt), CB-424 (needs timing on a real Windows install), CB-346, 423 (security), CB-395, 441 (mobile repo), CB-294, 413 (held) | none the org can produce |

The class comes from the ticket summaries, not from reading each ticket's code. The lead re-classifies when it reads the triage comment, and a ticket that turns out to need a real machine is released.

## 4. Proof on real machines

The sandbox cannot `ssh` (measured: `ssh mini` fails under the profile). More to the point, any helper that installs or runs a PR's code on the three machines runs agent-written code as the logged-in user, outside the sandbox. That would undo the containment the ticket says to keep. So:

- **Before merge, proof runs on GitHub's runners only.** The CB-455 team adds a `stress.yml` workflow: a `workflow_dispatch` that runs one named test class N times on both rids. The org cannot dispatch it (no Actions write), so the warden does, for the org's own PRs only. Both rids, isolated machines, no secrets.
- **The org never installs.** Its PR body says "Installed on: none. This org cannot reach the three machines" and lists under `## Not verified` what an install would have shown. That is honest and passes the hook's rule.
- **After merge (phase 3, optional).** The warden installs the CI-built `develop` artifact on the mini only, under `/tmp/ow-install.lock`, and runs checked-in recipes. Merged code reaches those machines at the next human install anyway, so this adds no new exposure. A failed recipe reopens the ticket.
- **Bugs that need an observation before the fix can be trusted stay with the human-led teams** (class 3).

## 5. Living beside the human teams and other sessions

- **One ticket at a time in the pilot, two after.** At most two org PRs open at once. Each PR costs eight CI jobs, and macOS runners queue.
- **One build at a time on the mini.** The engineers share a lock inside the org's folder. The mini is also the simulator host and other teams' test machine.
- **One fix round per batch.** QA sends one list, the engineer pushes exactly that. Two rounds, then the ticket is released.
- **The install lock.** The org never installs, so it never takes `/tmp/ow-install.lock`. Measured gap: the profile lets an agent write in `/tmp`, so it could create or remove that file. The profile gains a deny for it.
- **Signals and sessions.** Already contained: the profile denies signals outside the sandbox and other sessions' sockets.
- **Simulators.** Never used; mobile tickets are out of scope.
- **Rate limits.** Measured: the agents run on a Claude subscription connection, the same pool human sessions draw on. The run caps in §6 are what keep the org from starving them.
- **`develop` moves under it.** The org rebases once on a conflict. A second conflict releases the ticket.

## 6. Cost, budget, and how it stops

**Measured: Paperclip's cent budgets do nothing here.** The triage org shows 0 cents spent against about 5.3 million input, 50 million cached and 460 thousand output tokens, because a subscription connection has no price. `budgetMonthlyCents` is still set (5000 per agent) in case the connection changes, but it is not the brake.

**The brakes:**

| Brake | Where | Status |
| --- | --- | --- |
| Two tickets per routine run; routine `coalesce_if_active` | lead's instructions, `.paperclip.yaml` | pattern measured on the triage org |
| `maxConcurrentRuns: 1` per agent, `timeoutSec: 3600` | agent runtime config | read from source |
| `heartbeat.maxDailyRuns` per agent | agent runtime config | read from source; whether it counts assignment wakes is **assumed** |
| Per ticket: two fix rounds, 90 minutes, then release | lead's instructions | new |
| The warden pauses the company | host, outside the sandbox | new |
| The token expires in an hour if the warden stops | GitHub | by design of App tokens |

**The warden pauses the company** (`PATCH /api/companies/<id>` with `status: "paused"`, read from source) when: two tickets in a row are released; a bot PR merges with no QA comment; `develop`'s CI goes red on a bot merge (the warden reverts nothing and holds no Jira credential; it pauses and logs); a day's token count from `/costs/by-agent` passes a ceiling set after the pilot; or GitHub refuses a bot push on a ruleset, which means an agent tried to leave its lane. A paused org stays paused until a person or the CB-455 lead resumes it.

## 7. Phases

| Phase | What | Exit |
| --- | --- | --- |
| 0. Prove the path | On a fixture company and a throwaway branch, with no Jira writes: every **assumed** row in A.7. Rulesets applied, each with a refused and an accepted push. | all rows measured |
| 1. Pilot | Class 1 only, one ticket at a time, three tickets. The CB-455 QA reads each merged PR afterwards. | three merged, none reverted |
| 2. Widen | Class 2, two tickets at a time, weekday schedule after triage. | steady |
| 3. Post-merge proof | Warden installs `develop` on the mini and runs recipes. Its own ticket. | optional |

## 8. Feasible, and not

**Feasible, measured:** `dotnet build` and targeted unit, integration, UI and arrangement runs under the live sandbox profile; anonymous fetch of the public repository; the admin token is out of reach.

**Feasible, read from source, not yet run:** a shell, `git` and `gh` for runner agents under `approve-all`; a git worktree per Paperclip issue on a templated branch; a company secret named `GITHUB_TOKEN` feeding Paperclip's `git` and `gh` launchers; review stages on an issue; pausing by API.

**Not feasible, or deliberately not done:** proof on the three machines before merge; fixes in the mobile repository; an enforced QA gate on merge while the token is in the sandbox; "transitions on Bug tickets only" as a Paperclip rule (the connector allows or blocks an action per agent, and I found no per-ticket-type selector, though I did not read the tool policy code in full). The lead alone gets the transition action, and its instructions carry the Bug-only rule.

---

# Appendix: mechanics

## A.1 Paperclip's path for code-writing agents (question 1)

All read from source under `~/.paperclip/cli/current/node_modules/@paperclipai/` unless marked.

- **Tools.** A `paperclip_runner` agent on a Claude model gets Claude's own tools from the bundled Claude agent SDK. `acpxPermissionMode` has four values (`approve-all`, `approve-paperclip`, `approve-reads`, `deny-all`; `server/dist/vendor/paperclip-runner/drivers/acpx/driver-profile.js`). `approve-all` writes no permission block and there is no per-tool filter for Claude's native tools (`drivers/acpx/runtime-sandbox.js`), which confirms the triage README. The triage team measured one shell command inside a real run on 2026-11-23. A code-writing agent needs `approve-all`; the sandbox is the containment.
- **Workspaces.** A project workspace can be `git_repo` with `repoUrl` and `repoRef` (`shared/dist/validators/project.js`). A project's `executionWorkspacePolicy.workspaceStrategy` of type `git_worktree` gives each issue its own worktree on a branch from `branchTemplate` (default `{{issue.identifier}}-{{slug}}`), off `baseRef`, under `<repo>/.paperclip/worktrees` (`server/dist/services/workspace-runtime.js`, checked at line 2645). The plan uses `branchTemplate: "bugfix/pc-{{slug}}"`, `baseRef: origin/develop`. The triage org uses `project_primary` on a `local_path` (measured from the API: 122 execution workspaces, all `shared_workspace`).
- **GitHub.** Credential order is a managed identity, then a company secret named `GITHUB_TOKEN`, `GH_TOKEN` or `PAPERCLIP_GITHUB_TOKEN`, then the server's environment (`server/dist/services/git-credentials.js`, checked). `git` and `gh` are wrapped by launchers that ask the server for the credential per network operation and put it in the child process's environment (`adapter-utils/dist/github-launcher.js`). Paperclip has no server-side merge; the agent runs `gh pr merge` and GitHub's rules decide.
- **Review.** Issues take `executionPolicy.stages` of type `review` with a named participant. The plan uses one for QA. Its enforcement code was not read.
- **Not the path.** Paperclip's own Coder template recommends the `claude_local` and `codex_local` adapters, which use the host's login. The triage org withdrew that mode and this plan does not use it.
- **Scanner.** Paperclip's prompt-injection scanner runs on connector results only (`tool-gateway.js` call sites), so it will still refuse some Jira reads and will not touch shell output or source files (inferred from absence of a call).

## A.2 Build and test inside the sandbox (question 2)

Measured in a throwaway clone at `/private/tmp/cb455-probe` (since removed), run as `sandbox-exec -f ~/.paperclip/instances/default/paperclip-service.sb` with a private `HOME`. The live service was not touched.

| Run | Result |
| --- | --- |
| Controls: read `~/.ssh/config`, list `~/.nuget`, list `~/.config/gh`, `ssh mini` | all refused |
| `git credential-osxkeychain get`, `security find-internet-password`, `security find-generic-password` for GitHub | 0 bytes each |
| `git ls-remote` of `develop`, no credential | works (the repository is public) |
| `dotnet build`, first try | **fails**: Avalonia's build telemetry task resolves the real `~/Library/Application Support`, which the profile denies |
| `dotnet build` with `AVALONIA_TELEMETRY_OPTOUT=1` | passes, 0 errors, 16 s warm |
| `dotnet test tests/UnitTests --filter FullyQualifiedName~OrbGlyphSuiteTests` | 67 of 67 |
| `dotnet test tests/IntegrationTests --filter FullyQualifiedName~TranscriptReader` | 25 of 25 |
| `dotnet run --project tests/UiTests -c Release -- --filter-class Orbweaver.Tests.OrbFlyoutTests` | 20 of 20 |
| `dotnet run --project tests/ArrangementTests` | exit 0, 101 s |

So the profile needs no new allowance to build. The agents need `AVALONIA_TELEMETRY_OPTOUT=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` in their environment, and a shared `NUGET_PACKAGES` folder inside the org's own directory (the restore cache was 2.2 GB; the disk has 107 GB free).

Not measured: the full suites, `tools/coverage.sh`, the hook-script integration tests, a cold restore's duration, and a build inside a real agent run rather than a shell under the profile. Each named test class passed; that is a statement about those classes.

## A.3 Selection queries (question 3)

- Jira: `project = CB AND issuetype = Bug AND status = Refinement ORDER BY priority DESC, created ASC`.
- GitHub: `gh pr list --state open --search "<key> in:title,body"`, and `git log --remotes --not origin/develop --grep "<key>"` in the org's clone after a fetch.
- Measured today for the two held tickets: CB-413 is caught by status and by PR; CB-294 by status only.

## A.4 The package (`paperclip/bugfix-org`)

A new company, separate from the triage org so it has its own pause switch, secrets and budgets. Personas from `.profiles-assets` that the triage org does not use, so no agent draws two orbs: a lead, two engineers, one QA. The lead plans on the stronger model; engineers and QA run on Sonnet.

- `COMPANY.md`, `.paperclip.yaml` (agents, `GITHUB_TOKEN` binding, the weekday routine an hour after triage), `agents/*/AGENTS.md`.
- `skills/cb-bugfix/SKILL.md`: selection, claim, the branch and commit rules, targeted tests while iterating, one `tools/coverage.sh --base origin/develop` per PR, the PR body, when to release.
- Jira: the six read actions for all four; `addCommentToJiraIssue` and `transitionJiraIssue` for the lead only, added in the block-first order the triage README gives.
- `tools/check-pr-body.sh`: the body rules lifted out of `tools/require-team-sdlc.sh`, called by both, so the org checks its body with the same code the hook uses.

## A.5 Host side (built by the CB-455 team, outside the sandbox)

- **Warden** (`host/warden.py`, a launchd job): mints the installation token every 45 minutes and writes the company secret; dispatches `stress.yml` on request for the org's own PRs; audits merges; pauses the company (§6). Requests reach it as JSON files in a spool folder inside the org's directory, with a fixed schema and a closed list of verbs. It never runs a command an agent wrote and never runs `git` inside an agent's worktree.
- **App helper** (`host/create-app.py`): the one-shot manifest page (§2).
- **Rulesets** (`host/rulesets/*.json`, `host/apply-rulesets.sh`).
- **Profile**: the deny for `/private/tmp/ow-install.lock`.
- **`stress.yml`**.
- Tests: the warden's decisions are pure functions with a case per outcome, in the style of `tools/test-require-team-sdlc.py`, run by CI.

## A.6 Hardened variant, not recommended first

No GitHub write token in the sandbox at all. The warden fetches the agent's branch into its own clean clone, checks it (prefix, base, no workflow files), pushes, opens the PR, and merges only when CI is green at the head sha and the QA stage is approved in Paperclip. This enforces the QA gate. It costs a larger host-side program that handles agent-controlled input outside the sandbox, and it leaves Paperclip's own GitHub path unused. Worth it only if the audit in §6 ever fires.

## A.7 Assumed today; phase 0 measures each

| Assumption | How it is settled |
| --- | --- |
| A runner agent under `approve-all` runs `git`, `gh` and `dotnet` unprompted, with the two environment variables set | a fixture run that builds and prints the variables |
| `git_worktree` realises a worktree from a `git_repo` workspace under the sandbox profile | a fixture issue; list the worktree |
| The `GITHUB_TOKEN` company secret reaches `git push` and `gh pr create` through the launchers | push to a throwaway `bugfix/pc-fixture` branch; control: the same push with the secret removed fails |
| The App cannot bypass a ruleset, push a workflow change, create a tag or touch another branch | one refused push per rule beside one accepted push |
| The new rulesets leave Dependabot and the screenshots publisher working | the next Dependabot update and the next PR's screenshot comment |
| `heartbeat.maxDailyRuns` counts assignment wakes | set it to 1 on a fixture agent and assign twice |
| A paused company cancels a run in flight | pause during a fixture run |
| GitHub's App manifest flow works for this org from a LAN redirect | run it |
| `tools/coverage.sh --base` reports `merged 4` under the profile | one run in a fixture worktree |
