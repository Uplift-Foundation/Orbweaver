# Working in this repository

Notes for Codex (and anyone else) working here. The README is the real documentation — how the app works, how the hooks work, how to build installers. This file only covers *how work gets landed*, which the code can't tell you.

This is the same content as `CLAUDE.md`, which Claude Code reads. Two files because the two CLIs each look for their own name and neither reads the other's; **if you change one, change both.** They are short enough that keeping them in step by hand is cheaper than any mechanism for it, and a symlink would be a trap — it looks like one file until someone checks out the repo on Windows.

## How a feature gets built

**Every feature is a ticket, a team, and a round trip through QA — in that order.** The backlog is Jira project **CB**, board 70:

<https://uplifttech.atlassian.net/jira/software/projects/CB/boards/70/backlog>

No feature work starts from a chat message alone. A request becomes a CB ticket first, and from then on the ticket is what moves, so someone reading the board and someone reading the terminal see the same thing.

**Prefer the autonomous path at every step.** The escalations below are for an agent that is genuinely not confident, not checkpoints to hit out of habit. An agent that can defend its plan, its tests and its screenshots should carry the feature to done; one that cannot should ask early rather than after building the wrong thing.

### The team

Features are built by an **agent team**, not one session doing everything — the same mechanism this app draws orbs for, with each member spawned as its own `claude` process carrying `--agent-name`, `--team-name` and `--parent-session-id`, the last of which `AgentTeam.cs` reads off the process to link a member to its lead. Building Orbweaver with the thing Orbweaver visualises is deliberate: a team that looks wrong on the board is a free bug report.

- **Product manager** — writes the requirement, owns the ticket's status, calls done.
- **Architect** / **engineer** — build a refined ticket. As many of each as the work genuinely splits into, one per independent surface.
- **QA** — tests what they build and hands failures back. A loop, not a final gate: build a piece, test a piece, send it back, until nothing comes back.

**Claude Code sessions get this enforced by a hook (CB-273).** `tools/require-team-sdlc.sh`, registered in `.claude/settings.json`, refuses team members spawned as in-process subagents (a named spawn with `isolation` or `run_in_background`), PRs whose body lacks a CB key, `## Verified`, `## Not verified` and an "Installed on" line, and approvals that do not name both rids. Codex does not run Claude Code hooks, so the same rules are yours to keep by hand; `tools/test-require-team-sdlc.py` shows exactly what they are.

### Product manager: requirement first, then plan

The PM agent creates the requirement in CB as a **`Feature`** and leaves it in **Refinement**, which is where CB's workflow puts a new issue anyway. CB's issue types are `Epic`, `Feature`, `Story`, `Task`, `Bug`, `Subtask` (one word); `Feature` is level with `Story`, not above it, so an `Epic` still groups a multi-ticket effort.

**A follow-up that fixes an existing feature is a `Bug` against that feature's ticket, not a new `Feature`.** The test is whether a reasonable reading of the original ticket's acceptance criteria already covered the case. If it did, the feature is incomplete, and filing the gap as fresh scope quietly redefines the original as having succeeded — and picks the wrong branch prefix, since `bugfix/` is what says "wrong on `develop`, not yet released".

CB-133 paid for it: personas parsed out of markdown shipped, and the same day `.claude/PERSONA.MD` in this repository — naming a name, a profile photo and a voice, in a file the resolver provably reads — resolved to nothing. Two follow-ups went up as `Feature`s and had to be refiled as `Bug`s. Scope asked for *afterwards* stays a `Feature` (CB-134). Retyping a mis-filed ticket in place beats re-keying it, but rewrite the summary and description as a defect — what was written, what the app did, what it should have done, with the real input as reproduction steps.

**CB's board has four columns, and they are now confirmed** — read off CB-1, the first ticket filed, which is what this paragraph used to ask for. They are **Refinement → Development → Testing → Done**, with transition ids 11, 21, 31 and 41; every transition is global, so any status can be reached from any other. The name this file guessed was "Refining", which is a status on project FMN and not one CB has: a team-managed project owns its workflow separately, and the guess was wrong. Nothing else here needed correcting.

**In refinement the PM surfaces scope forks the requester may not have considered, not just the reading that was asked for.** CB-198 asked for resizable orbs, and the obvious reading was a global slider; whether each orb should also be able to override it was a fork the ticket never raised, and the answer changed the branch — a per-orb key, a right-click submenu, a title migration, mixed sizes in every arrangement. So the PM puts a fork like that to the requester as one question, with a recommendation and the real cost of each branch, rather than refining one branch in silence and discovering the other at review. Then the answer is final: asked once and settled, not reopened each round.

Then it writes a plan, and **plans on a stronger model than the one that implements**. Planning is where a wrong call is cheapest to fix and most expensive to miss:

| Feature | Plan with | Implement with |
| --- | --- | --- |
| Complex — new subsystem, cross-platform surface, anything touching the hook or transcript contracts | `fable` | `opus` or `sonnet` |
| Simpler — a setting, a bounded UI change, a fix with an obvious shape | `opus` or `sonnet` | `sonnet` |

Those are the `model:` values the Agent and Workflow tools accept; `effort:` is the other dial and moves the same way — high while planning, lower for mechanical stages.

**If the PM isn't confident the requirement and plan support autonomous implementation, it asks a human before spending a team.** Ask on GitHub with a link to the CB ticket, and — the Jira site needs its own login separate from GitHub's — enough of the requirement inline that the question stands alone for someone who can't open the link. Repo admins are the escalation point (`wtvamp`, `lunarjuice` at time of writing; `gh api repos/Uplift-Foundation/Orbweaver/collaborators` is the live list). If it *is* confident, it hands straight to the architects and engineers and no human is in the loop.

### Build and QA, in a loop

Engineers and architects build, QA tests, back and forth until it stops coming back. QA's half is **Every feature ships with its tests** below — unit, integration and UI covering 100% of the lines the branch adds — so "QA passed" means those are green, not that somebody had a look.

**A feature works on Windows and macOS or it is not a feature.** CI enforces the shape: `macos-latest`/`osx-arm64` and `windows-latest`/`win-x64`, every suite on both legs. Parity is more than a green build, though — a feature no install path wires up is equally unfinished, so check `tools/build-macos-dmg.sh`, `tools/build-macos-app.sh`'s Resources copy, `tools/Orbweaver.iss`, `tools/install-hooks.sh`/`.ps1` and the README install section, and confirm any platform gate is real (WSL) rather than accidental.

### PR, screenshots, and the call on done

The feature goes up as a PR against `develop`, per **Pull requests** below.

**Don't attach screenshots by hand — they're already automatic.** `ci.yml` captures a PNG per `tests/UiScreenshots` scenario on both runners, and `publish-screenshots.yml` picks them up on a `workflow_run` trigger, pushes them to the `screenshots` branch and comments on the PR with real `github.com/.../raw/...` URLs labelled per rid — `github.com`, not `raw.githubusercontent.com` (CB-118): the latter is a separate origin blind to the viewer's `github.com` session, so it 404s in a browser for a private repo even with full read access, while `github.com/<owner>/<repo>/raw/<branch>/<path>` is same-origin with the comment page and 302s through a freshly signed raw URL on every load. (Two workflows because a `pull_request` run from a fork gets a read-only token, and every PR here is one.) A feature with a visible surface adds its capture there; that comment is what gets reviewed.

The PM agent reviews it — **both** rids, since that is exactly where a macOS-only implementation shows itself — and **if it can approve the feature as done autonomously, it should**: approving the PR itself, and moving the ticket accordingly.

**Approving is the agent's own call, not a checkpoint to hand back.** An agent that has read both rids, can point at the tests covering the change and can say what it confirmed on a real machine is holding everything a human reviewer would be handed; asking a person anyway is a checkpoint out of habit. So approve it — `gh pr review <number> --repo Uplift-Foundation/Orbweaver --approve --body "..."` — and put the substance in the body: both rids by name, which suites ran, and what was confirmed on a machine kept separate from what is assumed. "LGTM" from an agent is worth nothing to the next person.

GitHub refuses an approving review on a PR the same account opened, which is the common case here. That is a mechanical limit, not a reason to escalate: post the identical body as a review comment instead. The written record is the point, not the green tick.

Approving is not the same as landing it — the ticket moves to Done when the change is actually on `develop`, so an approved-but-unmerged PR stays in Testing.

Only where it genuinely cannot approve does it ask a human to pull, install and approve by hand: **in the terminal** if someone is driving the feature in Claude Code, **on the PR** if nobody is watching. Either way name the specific thing the screenshots could not settle — "can't tell whether the mic button is enabled on Windows" is actionable in a minute; "needs manual approval" is not.

## Branching: gitflow

Two long-lived branches:

- **`main`** — released code. Tagged releases come from here. Nothing is committed to it directly.
- **`develop`** — the integration branch. Finished work lands here first and sits until it's part of a release.

Everything else is short-lived and **named for what it is**:

| Prefix | For | Branches from | Merges to |
| --- | --- | --- | --- |
| `feature/` | new or changed behaviour | `develop` | `develop` |
| `bugfix/` | fixing something that is wrong but not yet released | `develop` | `develop` |
| `release/` | preparing a version (bump, notes, packaging fixes) | `develop` | `main` **and** `develop` |
| `hotfix/` | a fix for released code that can't wait | `main` | `main` **and** `develop` |

**`bugfix/` and `hotfix/` are not a severity judgement — they say where the fix starts.** A bug that only exists on `develop`, or one that is shipped but can wait for the next release, is a `bugfix/` off `develop`. A `hotfix/` branches off `main` because it has to reach released users without waiting for whatever else is sitting on `develop`. If you're unsure which, ask what a user on the latest release experiences today: if the answer is "nothing wrong yet", it's a `bugfix/`.

Name the rest of the branch after the change, not the issue number: `feature/persist-orb-positions`, not `feature/pr-12`.

## Cutting a release: finish it

**A release is not cut until the tag is pushed.** Not when the branch exists, not when the PR is open, not when CI is green — a `release/` branch that stops at an approved PR has shipped nothing, and it leaves the repository in the one state that looks finished and is not.

That is not hypothetical. 0.4.3-beta was cut because seventeen merged PRs had been sitting on `develop` since 0.4.2-beta with no release behind them: users were still hitting orb-lifecycle bugs that had been fixed weeks earlier, and the heartbeat-and-cron-shapes feature had been reported as *lost* because the only build anyone could install did not contain it. Work that is merged but unshipped is indistinguishable, from outside, from work that was never done.

So the sequence below runs to the end, in one go, by whoever started it:

1. **Branch** `release/<version>` off `develop`.
2. **Bump `<Version>`** in `Orbweaver.csproj` — the single source of truth the packaging scripts and `release.yml`'s `check-version` gate both parse. **Patch only** (`0.5.0-beta` → `0.5.1-beta`) unless you are told to bump the minor or major.
3. **Write `.github/release-notes/v<version>.md`**, which the workflow publishes with the tag. Without it the release gets generated notes and a warning.
4. **Open the PR against `main`** and let CI go green on *both* rids.
5. **Merge it to `main`.**
6. **Push the tag** — `git tag -a v<version> <merge-sha> && git push upstream v<version>`. This is the step that actually builds, signs, notarizes and publishes the installers; `release.yml` is tag-driven and nothing before it ships anything.
7. **Merge `main` back into `develop`**, so the bump and notes are not lost at the next release.

**Do not stop between 4 and 7 to ask whether to proceed.** Cutting a release is already the decision to publish one; a second confirmation before the tag buys nothing and strands the release at exactly the point this section exists to prevent. The one thing worth pausing for is a *red* CI leg, which is a real answer to a real question. "Should I merge the release I was asked to cut" is not.

Two mechanical notes that look like problems and are not. GitHub refuses an approving review on a PR the same account opened, so the review body goes up as a comment instead — the written record is the point, not the green tick. And the PR against `main` will show every commit since the last release, because `main` is a whole train behind; that is the release branch doing its job, not the branch having changed all of it.

## Pull requests

Open every PR **against `develop`** — `feature/` and `bugfix/` both — unless it's a `release/` or `hotfix/` branch, which target `main`. The repository's default branch is still `main`, so GitHub will offer the wrong base — pass it explicitly:

```bash
gh pr create --repo Uplift-Foundation/Orbweaver --base develop \
  --head <branch> --title "..." --body "..."
```

Say in the PR body what was actually verified and what wasn't. This project keeps that distinction deliberately — see `docs/*-findings.md`, which separate "confirmed on a real machine" from "assumed to work". A PR that claims more than was tested costs more than one that admits a gap.

That includes the tests — see **Every feature ships with its tests** below. A PR that adds or changes behaviour without covering it isn't ready to review, however well the behaviour itself works, and a line the tests can't reach is named in the body rather than left for the next person to discover.

**Don't rename a pushed branch to fix its name.** GitHub closes the open PR instead of retargeting it. Get the prefix right before the first push.

## Remotes

`Uplift-Foundation/Orbweaver` is the canonical repository. **Which remote name points at it varies by clone** — run `git remote -v` rather than assuming. Clones from the canonical repo call it `origin`; clones from the `wtvamp/Claude-Buddy` fork call the fork `origin` and the canonical repo `upstream`.

## Reading the code: never from this clone

**The checkout you are probably standing in is not `develop`.** `/Users/user/Source/Claude-Buddy` is Owner's own working clone and is routinely parked on an in-progress branch, so a file read from it describes *that branch's* behaviour — and whoever reads it goes on to describe that as current. Read from the ref you mean:

```bash
git show upstream/develop:OpenClawSessions.cs    # a one-off check
git worktree add /tmp/wt upstream/develop        # several files
```

**Don't switch this clone's branch to make reading convenient.** It holds uncommitted work that isn't yours; an agent did that once and had it reverted within the hour.

CB-109 paid for this: it was planned for hours against `LocalMediaPathFrom` as read from this clone, which was sitting on CB-107's branch, so the plan described a parser arm `develop` does not have. Everything measured against the live gateway survived; every statement about *what the app does today* had to be withdrawn.

## Claims, and the checks that are worth their cost

The failure above has a general shape, and it appeared six times in one evening across CB-93/108/109/112/115/116, from three different agents: **a claim inheriting confidence from evidence about something adjacent.**

| the claim | what the evidence was actually about |
| --- | --- |
| "the same file" | a byte count from a different file in a sibling directory |
| "the gateway is gone, a restart won't fix it" | one hung request, plus another agent's stale process table |
| "this is what the app does today" | source read from a feature branch |
| "these turns name a picture" | a paraphrase of the parser's rule, not the rule |
| "harvesting recovers most of them" | files existing on disk, not the transcript saying where |
| "no client-side fix exists" | the transcript alone, never asking what else the gateway knew |

Each time the evidence was real and about the wrong thing. Four checks that cost seconds and each of which has already cost hours by being skipped:

**Don't reimplement the rule you're measuring — run it.** "Names a picture" got paraphrased as "contains an image-extension token" where the rule is "the *trailing* token is one" — wrong in both directions at once. The parsers here are pure and cheap to call, precisely so nobody has to paraphrase them. Related: "a message mentioning a `.png`" and "a picture turn" are different counts.

**Pair every positive result with a negative control that would have failed if your setup were wrong.** `cron.runs → data` alongside `tasks.flows → missing scope: operator.admin`, on one token, is what makes "this works at read scope" a measurement rather than a hope. Without the paired refusal you cannot tell it from *I happen to be over-privileged*, which nearly invalidated two tickets. **Prefer a declaration where the system publishes one** — the gateway states per-method scopes in `dist/method-scopes-*.js` — but the two are peers: the table can't prove your credential lacks admin, your probe can't prove the requirement survives the next release.

**A confident negative ends the inquiry, so it earns more suspicion than a confident error.** "We can't test that from here" and "I already scrubbed that" were both false and both stopped anyone looking again; a wrong positive gets contradicted by the next measurement. The sixth row above is the dangerous version of this: every claim was correctly sourced and correctly caveated, and the conclusion was still wrong because nobody asked whether the transcript was the whole world. **Rigour inside a wrong frame produces confident error, and it arrives with receipts.** When a conclusion says something is impossible, ask what *else* knows about this — not whether your evidence is sound.

**In `pwsh`, a bare property path can't tell an XML attribute from a same-named child element.** `$xml.assembly.errors` in CB-119's guard silently resolved to an object array, so the check read healthy against a report that said `errors="1"`. Use `GetAttribute()` / `SelectNodes()`.

**Naming a real value is necessary in a non-goal record and gratuitous in a report.** A non-goal nobody can check against is a shrug, not a decision — so "we are deliberately not chasing X, measured at N on ref R" has to name X. A body reporting a finding needs only the category — "a home path", "a four-digit id prefix" — because the value adds nothing a reader can act on.

**Everything above reads as a warning about optimism. It is not.** Of six wrong conclusions in one day, four were too strong and two were too weak — a freeze reported as real that was not, and a defect written off as a perception problem that was not either. The direction is incidental, and a rule tuned to catch only the confident half will keep letting the other half through. What all six share is that something was *concluded* where it could have been *gone and looked at*. Five more checks, in that light:

**A comment asserting a fact about an external system is not evidence — it is a hypothesis with good grammar.** `UsagePoller.MinimumInterval` said Claude Code caches the usage fetch behind a five-minute write guard, so polling faster "cannot produce a newer number". Nobody had ever checked. A fresher figure comes back twelve seconds later. Because the sentence read like a finding rather than an assumption, the cadence was never weighed against a real cost — it was deferred to — and an investigation into orbs a user reported as frozen spent hours downstream of it, since a comment saying fresher data does not exist rules out the cheapest explanation first. CB-122 is what that cost. The check was one command in a loop.

**A measurement taken during one regime is not a measurement of the system.** This happened twice in one afternoon, and the repeat is the point, because the second came *after* the first had been understood and written down. First an eight-second opacity trace was taken as evidence about indefinite behaviour. Then, having learned that, a ten-minute window in which usage did not move was nearly reported as the cache granularity — but the account was idle, so a flat series is equally consistent with a five-minute cache and with no cache at all. The instrument was perfect both times: the right command, the right environment, sixty-second intervals, thirteen clean rounds, zero dropped samples. **Care at the point of measurement cannot catch this.** Only one question can, and it has to be asked before generalising rather than after: *is this system stationary over the window I sampled?* If it is bursty, a quiet sample measures the quiet.

**A line-based search over wrapped prose can return a false zero.** Two greps for phrases that were present came back empty purely because the text had been reflowed across a line break, and both were a keystroke away from being reported as missing. Confirm a negative by reading, not by rerunning. This compounds with the confident-negative rule above: a false zero ends the inquiry, which is exactly why it deserves more suspicion than a false positive would.

**A review is a statement about a sha, and it expires when the branch moves.** A PR read, reasoned about and reported on can be merged, rebased or force-pushed between the reading and the report, and every identifier in that report then describes something that no longer exists. Re-resolve the head sha immediately before reporting, and say which one you checked. This is the reused-window-id trap in another costume: a stale identifier does not error, it answers about something else, plausibly.

**Auditing part of a claim and reporting the whole of it is that same failure in miniature.** Resolving a rebase conflict here deleted a paragraph the other branch had added. The audit that followed checked that every **measurement** from that branch survived, found that they had, and reported that **nothing** had. The check was structurally incapable of finding the missing thing, because the missing thing was not a measurement — a claim about the whole from evidence about one part, stated in the same sentence that claimed a careful audit. Report what you checked *for*, not merely that you checked.

## Auditing a diff: range, scope, and refusals

**Name the range you audited, and whether it was per-commit or net.** A PR is audited against the base it merges into, not against your own commit's parent — and net-zero across a range is not the same as never present, because a scrub committed *on top of* an introduction cancels it in the net diff while both commits stay in history:

```bash
for c in $(git rev-list upstream/develop..HEAD); do
  tools/audit-diff.sh $c | grep "^+" | grep -i "<pattern>"  # code
  git log -1 --format=%B $c | grep -i "<pattern>"           # message
done
```

**Name the file you checked, not just the pattern, counter and ref — three of four looks exactly like four.** A check with the right pattern, the right counter (`grep -io`, not `grep -c`) and the right ref, run against the wrong file, returns a clean zero that reads exactly like a refutation. It happened twice while this section's own hostname counts were being confirmed: querying `CLAUDE.md`/`AGENTS.md` for them returns 0 in both, because the record lives in `docs/openclaw-findings.md`, not the convention files.

**Audit the commit message before you commit, not the PR body after you push.** Reading a body back is a step someone remembers to do; nobody re-reads a commit message once it exists. The reflex of quoting a scrubbed value while explaining that it was scrubbed has needed correcting in a PR body three times in this project, and landed uncorrected in a merged commit message once — where it can't be fixed without a history rewrite.

`tools/audit-diff.sh` is `git show` with one extra check: it refuses a commit with two or more parents instead of silently handing back the empty diff `git show <merge-sha>` returns, which examines nothing and looks exactly like a clean pass. That trap has bitten three times in this project in one evening, once against someone who had written this very rule down an hour earlier — use the wrapper rather than a bare `git show` for any per-commit audit. **And treat its refusal as a refusal, not a zero:** a wrapper that pipes the tool's error into a counter turns a correct refusal into `0 hits`, which is what happened the first time this wrapper caught an accidentally-amended merge commit — the guard worked and the harness discarded the signal. Fail loudly on a refusal; "no output" is not "no problem."

Better still, **prefer un-stacking over remembering you're stacked**: while a branch sits on someone else's unmerged commit, "my added lines" and "this PR's diff" are different ranges. Rebasing onto merged `develop` collapses them and the audit is right by construction. A structural fix beats a discipline.

## Commits

Messages here are prose, not changelog lines: a short summary in the imperative, then paragraphs explaining **why** the change is right and what was considered and rejected. Comments in the code follow the same habit — read a few (`OrbWindow.axaml.cs`, `SessionManager.cs`) before writing new ones, and match the density rather than stripping or padding it.

## Install on all three machines before you kick off CI

**Push the branch, install it on every machine, and *then* let CI run.** Not after CI goes green, and not only on the machine the work happened on. CI takes long enough that it is the natural window for a human to actually use the build — but only if the build is already sitting on their machines when that window opens. Install afterwards and the two waits happen back to back for no reason.

The three machines, and all three matter for a different reason:

| Machine | Where | Why it is not optional |
| --- | --- | --- |
| **This MacBook** — `Warrens-MacBook-Pro` | local | Where the work was written, so it is the one most likely to pass by accident. Install **signed** (see below) or click-to-focus breaks silently. |
| **Mac Mini** — `mini` / `macmini`, `192.168.0.127` | LAN, `~/.ssh/config` | The headless, unattended case. It runs Buddy under launchd with no one looking at it, which is where a change that depends on a window server session, a focused app or an answered prompt shows itself. |
| **Windows PC** — `windows`, `192.168.1.24` | LAN, `~/.ssh/config` | The only real Windows install. CI's `windows-latest` leg proves the suites pass; it proves nothing about the installer, the Fluent theme's own control templates, or how anything looks. |

That Windows box is also the ComfyUI host, so `curl http://192.168.1.24:8000/system_stats` is a quick liveness check for it.

**A green CI leg is not an install.** `windows-latest` runs tests against a checkout; it never runs `tools/Orbweaver.iss`, never puts an icon in a tray, and never shows anybody a window. The parity rule above — a feature no install path wires up is unfinished — is about exactly the gap between those two things, and the only way to close it is to install the thing.

**Say which machines you actually installed on, and which you did not.** Same rule as everywhere else here: "installed on all three" when one of them was asleep is worse than "installed on the MacBook and the mini, Windows box was off". If a machine is unreachable, say so and carry on rather than silently dropping it.

## Build and run

```bash
dotnet build                       # quick compile check
dotnet run                         # run the loose binary (no bundle)
./tools/build-macos-app.sh         # "Orbweaver.app" into dist/
./tools/build-macos-app.sh --install   # ...and copy to /Applications
```

When installing over an existing `/Applications/Orbweaver.app` (or the pre-CB-255 `/Applications/Claude Buddy.app`) on macOS, build **signed**:

```bash
MACOS_SIGNING_IDENTITY="Developer ID Application: UPLIFT FOUNDATION (5AQ4ULRG3Z)" \
  ./tools/build-macos-app.sh --install
```

`--install` stops the Buddy already running from `/Applications` — out of `Orbweaver.app` or the legacy `Claude Buddy.app`, which it then removes so the two never share the one bundle id — before replacing it, and ends with exactly one running the new binary (launchd's keep-alive starts it if registered, otherwise the script relaunches it with `CLAUDE_CONFIG_DIR` stripped). **Do not relaunch by hand afterwards** — since CB-206 the single-instance mutex is one per user across sessions, so a manual launch just finds the new copy and exits. The script prints the running pid; a warning there is the thing to look at.

macOS ties the Automation (Apple Events) consent to the app's code identity, so an ad-hoc build silently breaks click-to-focus until the user re-approves it in System Settings — and that failure is invisible.

**Local Network consent works the same way, and breaks on every upgrade.** Replacing `/Applications/Orbweaver.app` gives the bundle a new CDHash, macOS re-evaluates Local Network access against it, and the grant does not carry over — so anything on the LAN, OpenClaw's gateway most of all, starts failing with `EHOSTUNREACH`. Nothing prompts loudly enough to notice: this is a menu-bar app with no Dock icon and no window.

What makes it expensive is that **every obvious check agrees with the wrong answer.** `ping`, `nc`, `curl` and `ssh` are Apple-signed and exempt from the gate, so they all report the host perfectly reachable while the app cannot open a socket to it — and `nc -z` will additionally claim success against ports that refuse an honest connect. Reach for the app's own transport instead:

```bash
dotnet run --project tools/openclaw-probe -- sessions
```

Note the probe is itself subject to the gate, because `dotnet` is a third-party binary. **A failing probe alongside a working app is expected, not a contradiction** — it says the terminal lacks the grant, not that the app does. `OpenClawGateway.ExplainConnectFailure` now appends a hint naming the settings pane, so the app says this itself rather than only reporting the errno. CB-38 has the full diagnosis.

`<Version>` in `Orbweaver.csproj` is the single source of truth for the shipped version.

## Every feature ships with its tests

**A feature request is not finished when the feature works. It is finished when the feature works and the code that makes it work is covered.** Tests land in the same branch as the behaviour, not a follow-up, and the target for the lines that branch adds or changes is **100%** — every new method, every arm of every new conditional, every error path you wrote a `catch` for.

Cover all three levels the suites already separate, not just the one nearest to where the change happens to live:

- **Unit** (`tests/UnitTests`) — the decisions. A new rule about which orb wins, which name is used, or where something is drawn belongs in a function with no window and no settings behind it, with a case per outcome. Logic that can't be reached without constructing a window is a seam to fix first, not a test to skip — the same argument that keeps `OrbArrangement`, `OrbGlyph` and both transcript parsers pure.
- **Integration** (`tests/IntegrationTests`) — the seams with what this process does not own: hook scripts as real subprocesses, files on disk, settings round-tripped through a real file. A format someone else defines is covered here *as well as* by a unit test of the parsing; the two fail differently.
- **UI** (`tests/UiTests`) — the headless Avalonia path. A new window, panel, control, binding or click handler is driven with a synthesized click or a real `UpdateFrom` and asserted on what a user would have seen. `FakeChatSession` is the pattern for anything needing a live session behind it. A new *visible* surface also gets a hand-written capture in `tests/UiScreenshots` — real Skia rather than the null renderer, and its cases don't follow `UiTests` automatically.

A change to geometry, transcript parsing or orb initials extends the three console suites too — `dotnet test tests/Tests.sln` does not run them, and CI failing on `ArrangementTests` after a green `dotnet test` is a bad way to find that out. CI runs every suite on both runners, so a test that only passes on the machine you wrote it on blocks the build.

Also run `dotnet test tests/UiTests -c Release` before pushing, and run CB-119's guard on the report it writes (`tools/check-xunit-report.ps1` — see `.github/workflows/ci.yml`) rather than trusting `dotnet test`'s own exit code: it fired three times unnoticed during CB-120's work alone, each time a test count one short with zero failures, because the guard only ran in CI. `dotnet test` defaults to Debug and CI builds Release, and Release is not just faster code — it reorders a parallel suite and tightens the gaps between writes to a scratch directory. CB-3 landed six SessionScanTests green in Debug and red in Release on both CI legs, every attempt. The other suites have been clean in both; it is the UI one, with a dispatcher and real timers and process-wide statics, that is worth the extra half-minute. A test that passes in one configuration and not the other gets made independent of what else is running — never a sleep, never a widened tolerance. That advice is only for a flake that *fails*; a flake that silently drops the test count has nothing to chase until CB-119's guard turns it back into an ordinary failure.

**To run one UI test class, filter with the runner's own flag, not `--filter`.** `tests/UiTests` and `tests/UiScreenshots` run on xunit v3 and the Microsoft Testing Platform, which does not read VSTest's `--filter` at all: `dotnet test tests/UiTests --filter ...` is accepted without complaint and then runs the whole suite, which is exactly the full run the targeted-test rule exists to avoid, and it reads as a normal pass or fail until you notice the total. Two engineers hit this on the same day (CB-270). The form that works is `dotnet run --project tests/UiTests -c Release -- --filter-class <Namespace.Class>` (or `--filter-method "*MethodName"`), and `dotnet test tests/UiTests -c Release -- --filter-class <Namespace.Class>` works too; the same applies to `tests/UiScreenshots`. `tests/UnitTests` and `tests/IntegrationTests` are on VSTest and keep `--filter`. Check the total in the summary line before trusting a targeted run.

**What a short run looks like, and where it came from (CB-183).** The fingerprint is one `<error type="test-case-cleanup">` in the xUnit report carrying `InvalidOperationException: The calling thread cannot access this object because a different thread owns it`, thrown from `Avalonia.Rendering.DefaultRenderLoop.Add` under `AvaloniaHeadlessPlatform.Initialize` under `HeadlessUnitTestSession.EnsureIsolatedApplication` — Avalonia.Headless's per-test session isolation, not any test of ours. The test name on the error is whichever test was due to run next, and it is different on nearly every short run, so never record a victim's name as the defect's location; `AccountOrbsTests` was recorded that way once and was simply first to be hit. The cause, read out of the Avalonia 12.1.1 assemblies and then forced deliberately: per-test isolation, the package default, nulls the process-wide `Dispatcher.s_uiThread` around every test, and the next thread to read `Dispatcher.UIThread` becomes its owner — so a pool thread left behind by an earlier test (a watcher, a timer, a fire-and-forget `Post`) could claim the UI thread out from under the next test's setup. A scratch thread doing nothing but read `Dispatcher.UIThread` turned about 1350 of 1412 tests into exactly this error on every run under per-test isolation, and none under `PerAssembly`. `tests/UiTests/TestAppBuilder.cs` now declares `[assembly: AvaloniaTestIsolation(PerAssembly)]` and `UiDispatcherIsolationTests` pins it. The cost is that the headless platform — the clipboard most visibly — is now shared between tests, so a test that needs something empty empties it rather than assuming it; that exposed one such test. Measured on one Mac, 30 Release runs each through CB-119's guard: 2 of 30 short before (errors=1, zero failures, exit 0 both times, the victims `AccountOrbsTests` and `CloudScanTests`), 0 of 30 after — too few events on their own to call the rate fixed, which is why the forced run above is the evidence that carries the causal claim. If a short run with this fingerprint ever comes back, the isolation level has been changed or something is creating its own session — look there first.

Remember the three console suites too — `dotnet test tests/Tests.sln` does not run them. CI runs every suite on both runners, so a test that only passes on your machine blocks the build.

**100% means the diff, not the repository.** The app is nowhere near it — as of this writing `UnitTests` alone covers about 3% of the assembly, and whole areas (`TerminalFocuser`, the tray, the installers) make real `tmux`/`ps`/`osascript` calls a headless runner must not execute. Holding new work to 100% is how that number climbs without a rewrite; holding the existing repository to it would make the rule something everybody quietly ignores.

Where a line genuinely cannot be covered — an OS call with no seam, a `catch` for something only the other platform throws — **name it in the PR body and say why**, in the same voice that separates "confirmed on a real machine" from "assumed to work". A named uncovered line is a known gap; an unnamed one is a claim about the suite that isn't true.

To see the number:

```bash
dotnet add tests/UnitTests/Orbweaver.UnitTests.csproj package coverlet.collector
dotnet test tests/UnitTests --collect:"XPlat Code Coverage"
# tests/UnitTests/TestResults/<guid>/coverage.cobertura.xml
```

**Nothing in the repository collects coverage today, and the flag fails silently without that package** — `--collect:"XPlat Code Coverage"` against a project with no `coverlet.collector` reference creates an empty `TestResults/`, prints no warning and exits 0, so a run that measured nothing reads exactly like one that measured everything. Add the package to the suite you are measuring and take it back out before you push, unless you are deliberately wiring collection into all three suites and CI, which is its own change.

## Testing

Orb *geometry*: `dotnet run --project tests/ArrangementTests`. It walks every shape at every end of the spacing slider and orb size, mixed sizes included, across a range of orb counts and team shapes on three screen sizes, and asserts that nothing leaves the work area, nothing is drawn on top of anything else, and no team member ends up too far from its lead to be read as one. Run it after any change to `OrbArrangement`.

Transcript and dialog parsing: `dotnet run --project tests/TranscriptTests`. It covers `ChatTranscript` (Claude Code's JSONL, and reading a permission dialog off a captured tmux pane) and `CodexTranscript` (Codex's rollout JSONL).

Both parsers read formats nobody here controls, and both fail *quietly*: a mis-mapped transcript silently drops a message, and a mis-read dialog puts a button on screen that presses something other than what it says. **Write fixtures from real output, not from memory** — the dialog parser was first written against an invented fixture and failed on every real dialog. To capture one:

```bash
tmux capture-pane -p -t %<pane> > /tmp/pane.txt
dotnet run --project tests/TranscriptTests -- /tmp/pane.txt   # or a .jsonl
```

Both CLIs write `.jsonl`, so the harness picks its parser from the file's first row rather than its extension and says which one it used. If that line names the wrong CLI, nothing below it means anything.

Orb *initials* have one too — `dotnet run --project tests/GlyphTests`. It covers `OrbGlyph`: the two letters an orb wears and the ones the chat panel's header wears beside it, across kebab and snake case, all three dashes, leading punctuation, emoji, and the single-letter setting. It also covers `ChatSpeaker`, which decides *whose* letters those are on a message bubble — the agent in the session key, or the panel's title for a terminal session, and never "nobody" once a name is known.

It exists because the initials were wrong for a year and nobody saw it. Every kebab-case name drew two letters off the front of its first word, so `claude-buddy` was "Cl" and not "Cb" — invisible partly because it only looks wrong when the halves start with different letters, and partly because reading the answer meant looking at the screen. Same rule as the geometry: `OrbGlyph` is pure and takes the two-letter *setting* as an argument rather than reading it, so the tests do not depend on the machine they run on.

### Measuring a running app from outside

Reading geometry and pixels back out of the window server is the right instrument when a change is about what actually reached the screen — a property can update while no frame is ever presented, and only the pixels can tell you which happened. It has three traps in it, and **all three hand back plausible data rather than an error**, which is what makes them worth writing down. All three were hit in one afternoon reading account orbs off a live app.

**A macOS window id is not stable across a process restart, and a freed id is reused rather than retired.** `screencapture -x -o -l<dead id>` does not fail; it returns a perfectly good PNG of whatever window inherited the number. That was caught only because a weekly ring appeared to move eighty points in a minute, which is not a rate any weekly window moves at — a two-point drift would have been reported as exactly the movement being looked for. Re-resolve the pid *and* the window ids on every sample, and log them beside the data, so a restart shows up in the series instead of hiding in it. Note `MacOSWindowList.ForPid` will not find orbs: it drops anything under 80x80 and anything off layer 0, and orbs are 72x72 and topmost, so this needs its own `CGWindowListCopyWindowInfo` query.

**An arc sweep read off pixels overshoots — about +2.5% at r=32 and +3.2% at r=25.** The stroke is 5 dip with a round cap, so the drawn arc runs 2.5 dip past each end, and the antialiased fringe counts as coloured on top of that. The error is proportionally worse on a short arc, where the caps are a large fraction of the whole. Subtract it before calling a drawn value wrong: the orbs matched their source exactly once it was accounted for, having read about eleven points high before. And mask the CLI badge — it sits 32.5 dip from the orb centre at roughly 225 degrees, which is *on* the weekly ring, so a naive "coloured pixel at r=32" scan reports about 6.7% weekly for an orb drawing none at all.

**A background job cannot draw to the screen, and that failure is silent — but not by the mechanism this paragraph used to name.** `MacOSScreenLock` reads a null session dictionary as locked, and the main thread then parks having printed nothing whatsoever, which is real. What was wrong was the claim that a background job *is* such a context. Measured 2026-09-22: a `Background`-domain agent shell and an SSH login both got a **non-null** dictionary out of `CGSessionCopyCurrentDictionary`, carrying the console session's own state including `kCGSSessionOnConsoleKey`. Apple's `CGSession.h` documents NULL for "the caller is not running within a Quartz GUI session or the window server is disabled" — a root LaunchDaemon before login, which needs root to exercise and nobody here has. So a null reading is a bound on genuine ignorance rather than an observed background-job behaviour, and it should be reasoned about that way; an ordinary agent shell or SSH login is not in it.

The symptoms either side of that sentence are real and separately corroborated, which is why the sentence had to be replaced rather than the paragraph deleted. Patching the lock check out moves the failure along to `Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661`, and `screencapture` refuses both `-l<id>` and `-R<rect>` in that context too. So anything that has to see the screen still goes to a session with a real GUI context, or to a human. It cannot be made to work from a background job by trying harder.

The wrong sentence survived because it read like a finding and was never checked — the same shape as the `UsagePoller.MinimumInterval` comment two paragraphs up, and it cost PR #84 an arm justified on a behaviour that does not happen. A claim about an external system that nobody has run is a hypothesis with good grammar however long it has been in the file.

**To exercise a locked-screen path, read it off a machine that is already locked instead of locking one.** The Mac mini is the standing fixture: `ssh <mini> 'ioreg -n Root -d1 -a | plutil -extract IOConsoleUsers xml1 -o - -'` returns the console session's full dictionary, and on a locked machine that is `CGSSessionScreenIsLocked true`, `CGSSessionScreenLockedTime`, and `kCGSSessionOnConsoleKey true` — thirteen keys against eleven when unlocked, the two extra being exactly the lock pair. Nothing on either machine is touched. That is how the locked reading was finally observed after three rounds of reasoning about it, and it is repeatable any time the mini is locked. `ioreg` enumerates *every* session rather than only the caller's, so a second locked session can be sampled from an unlocked one the same way. Never lock, sleep or fast-user-switch a machine somebody is sitting at to get a reading you can take remotely.

## The automated suite

Three xUnit suites — `tests/UnitTests`, `tests/IntegrationTests`, `tests/UiTests` — join the three above rather than replacing them: one command, `dotnet test tests/Tests.sln`, runs all three; `Orbweaver.sln` stays app-only and `Tests.sln` holds only these, so neither `dotnet build` nor `dotnet test` at the root can trip over the other's projects. CI runs every suite — including `tests/UiScreenshots`, which renders through real Skia rather than the null renderer — on both runners, before packaging.

They reference `Orbweaver.csproj` with a `<ProjectReference>` rather than `<Compile Include>`-ing individual files, because `SessionManager` and `OrbweaverSettings` have dependency closures too large for that — and `<InternalsVisibleTo>` in `Orbweaver.csproj` grants exactly these three assemblies visibility into anything `internal`.

`UnitTests` covers more pure logic the same way as `ArrangementTests`/ `GlyphTests`, most valuably `SessionManager`'s `Superseded` and `InheritTerminalInfo` — the rules deciding which status file is the live orb and which sibling donates terminal coordinates to one that has none. `IntegrationTests` runs `OrbweaverHook.sh`/`.ps1` as real subprocesses, asserting the invariant Codex depends on (exit 0, empty stdout/stderr — its hook stdout is parsed as strict JSON and exit 2 means deny), plus `TranscriptReader`'s tail/truncation rules and `OrbweaverSettings`' unknown-key round-trip. The `OpenArgumentDeliveryTests` cases that launch app bundles through LaunchServices skip on a developer's Mac, because every launch leaves a tile in the logged-in user's Dock, and run only in CI (`CI=true`) or with `ORBWEAVER_LAUNCH_TESTS=1` (CB-246). Every `ORBWEAVER_*` variable in this repo used to be spelled `CLAUDE_BUDDY_*`; the old spelling still works as a fallback, and the new one wins when both are set. `UiTests` runs headless via `Avalonia.Headless.XUnit`, using the real `App` class as its own host (its `OnFrameworkInitializationCompleted` guard is never true under a headless lifetime, so the mutex/`SessionManager.Start()`/tray body never runs) — covers `OrbFlyout`'s real clicks, `OrbWindow.UpdateFrom`, and `ChatPanel` driven by `FakeChatSession` (an in-memory `IRemoteChatSession`, the reason that interface exists per its own header comment). It never synthesizes a click on an orb itself: that reaches `TerminalFocuser`, which fires real `tmux`/`ps`/`osascript` off-thread with no OS guard at its own entry point.

All three point `ORBWEAVER_SETTINGS_DIR` at a fresh temp directory via a `[ModuleInitializer]` before anything else runs — even constructing an `OrbWindow` reads a colour setting in a field initializer. That env var is checked in `OrbweaverSettings.Directory` before `SpecialFolder.ApplicationData`, the same pattern as `ORBWEAVER_PROFILE_ROOT` in `ClaudeDesktopManager.cs` and `ORBWEAVER_BUNDLE_ROOT` in `ClaudeDesktopBundles.cs`, which redirects the cloned-bundle cache so a test never writes into the live one; without it a test reads and writes the real settings.json.

## Coverage

`./tools/coverage.sh` for whole-app line and branch coverage; `./tools/coverage.sh --base upstream/develop` adds coverage of just the lines you added, which is the figure that actually says whether new code is tested — a file-level percentage is dominated by whatever was already there.

Two collectors, deliberately: `UnitTests`/`IntegrationTests` are VSTest and use `coverlet.collector`, while `UiTests` and `UiScreenshots` run on the Microsoft Testing Platform (xUnit v3, forced by `Avalonia.Headless.XUnit` 12.x) where VSTest collectors do not apply, so they use `Microsoft.Testing.Extensions.CodeCoverage` — **pinned to 17.14.2** in both, because 18.x wants `Microsoft.Testing.Platform` 2.x while xunit.v3 3.2.2 brings `mtp-v1`, and the mix throws `TypeLoadException` for `IDataConsumer` before a test runs. `tools/merge-coverage.py` then unions the four cobertura files, which all measure the same assembly; summing them would double-count the denominator and undercount the numerator at the same time.

`UiScreenshots` counts as of CB-3 and did not before. CI always ran it, and it is the only suite drawing through **real Skia** rather than the null renderer, so a few things are reachable only there — a bitmap actually written to disk most obviously (`ClaudeDesktopBundles.WriteTinted`). Leaving it out meant those lines were verified and counted nowhere.

**Check the run produced a number before quoting one.** `merged N report(s)` is the first line `coverage.sh` prints and it is a self-check: there are exactly four cobertura files, so **anything but `merged 4` means the figure below it is fiction**. Both directions turned up within one afternoon on CB-6 — `merged 1`, because another agent in the same clone ran `rm -rf bin obj tests/*/bin tests/*/obj` mid-measurement and deleted the binaries the report maps against; and `merged 6`, from stale reports left in the MTP suites' own `TestResults`, which `coverage.sh` reads and does not clear.

"Quiet tree" therefore means the **process table**, not `git status` and not an agent roster that looks idle — `pgrep -fl "dotnet|coverage.sh|testhost"`, then a clean rebuild. A number taken while anything else was building does not leave the session that took it.

Three traps in that check, all seen on CB-172, and the false-clear is the dangerous one. **Stopping a background job does not stop the test host** — the harness kills the shell and the `dotnet test` child survives re-parented, so an agent can stop its run, find the table clear and still have a live test host in its worktree (three times in one evening). Kill the child by worktree path explicitly. **`pgrep -f` matches the zsh wrapper**, including the wrapper of the command doing the asking, so it reports work that is not running and makes `until ! pgrep ...` a loop that can never exit. **`VBCSCompiler` and the `MSBuild.dll /nodemode:1 /nodeReuse:true` workers are persistent daemons** — the condition is no active build or test host, not an empty `pgrep`, and reading it the other way leads to killing build servers to satisfy a check. Finally, run the check and the launch as **separate commands**: one command prints its verdict after the launch is already committed.


Learn the signature, because it reads as a regression rather than as an error. CB-6 produced a phantom `56/60 = 93.3%` that was reported as a real branch drop and sent back as work; six later runs all gave `56/56 = 100%`. The tell was that two of the lines it called uncovered were **closing braces** and one was a range-slice assignment — none can hold a branch at all. Branch arms attributed to punctuation means the report was mapped against a stale or deleted binary. Three unreproducible numbers are now documented here and only one was the code's fault.

The three console suites still contribute nothing *as suites* — `ArrangementTests`, `GlyphTests`, `TranscriptTests` are plain exes, not test-SDK projects. Their **cases** do count now: CB-3 moved each matrix into a class that `UnitTests` compiles in and runs (`ArrangementSweep`, `GlyphSuite`, `TranscriptSuite`), so `OrbArrangement` no longer reads 0% while being the most exhaustively verified file here. Run the exes for the grouped failure report.

Any new UI test class that reads or writes `OrbweaverSettings` goes in `[Collection("Settings")]`. The settings model is a process-wide static that almost everything visual reads while being constructed, so parallel classes race to a different set of *executed lines* rather than to a failure — three runs of an identical binary once reported 1914, 2024 and 1914 covered lines in SettingsWindow.cs. See `tests/UiTests/SettingsCollection.cs`.

And read the headline as coverage **of what remains**: an excluded file and a deleted one look identical in a report. `merge-coverage.py` reads the attributes back out of the sources and prints what was held out, what is excluded inside measured files, and what is absent for no stated reason.

The two engines also disagree about `[ExcludeFromCodeCoverage]` on a *method*. coverlet honours it; `Microsoft.CodeCoverage` (both MTP suites) instruments the body anyway and reports it unhit. Both honour it on a *class*, which is how the gap survived until CB-3 had 157 member-level exclusions for it to show up in. The merge is therefore **not** a union: coverlet decides which lines *and which branch points* exist, and the MTP reports only contribute hits for those. Undo that and an exclusion on a method silently stops meaning anything.

The branch half was missing until CB-100, and the MTP reports were inventing branch points wherever they liked. Measured over one run of all four suites: 158 branch points in 42 files existed in the MTP reports and in neither coverlet report — 416 arcs, 328 of them claimed as taken. Reading the lines gives CB-6's signature again, arcs attributed to code that cannot hold one: `OpenClawSessions.cs:285` is `lock (Gate) return agentIds` reported `(0/6)`, `:328` is a bare `List<string> ids;` reported `(0/2)`, `OrbGlyph.cs:62` is the second line of a comment reported `(4/4)`. They are compiler-generated arcs the MTP engine pins to the nearest statement line.

It moved the number in both directions, which is why nobody caught it: dropping them took the whole app from 9361/10483 (89.3%) to 9033/10067 (89.7%), with `OpenClawSessions.cs` going 91.6% → 92.4% and `OrbGlyph.cs` going 94.4% → 93.8%. A phantom `4/4` flatters a file exactly as a phantom `0/6` punishes one, so a fix tuned only to the understating half would have left the other in. `tools/test-merge-coverage.py` pins both halves now and CI runs it.

The total is coverlet's too (CB-244). `merge_secondary` used to take `max()` of the total for a point both engines knew, adopting the MTP engine's wider arc count: `ChatMarkdown.cs:179` was `6/6` in coverlet's unit run and `10/12` in the MTP runs and merged to `10/12` (reports and sources at the same sha, 95e84ea5). A bare cap would bring back what the `max()` guarded against (a taken count from the wider engine paired with the narrower total prints a line as fully covered when neither suite covered it: 7 points, 13 arcs on the tree measured), so an MTP count is believed only where it needs no pairing: the same total (larger taken wins), or a wider total MTP covered completely (capped at coverlet's total). Anything else adds nothing. On the same four reports that moved the whole app's branches from 12855/13994 (91.9%) to 12746/13816 (92.3%), lines unchanged, 33 of 172 files (23 up, 10 down). `tools/test-merge-coverage.py` pins it.

## Extra accounts

Both CLIs support a second account through an environment variable — `CLAUDE_CONFIG_DIR` and `CODEX_HOME` — and each is a separate config file that the default wiring does not touch. The app keeps a list per CLI (`claudeCodeProfileDirs`, `codexHomes`), the Settings window edits them on both platforms, and the installers read them so a repair or uninstall covers every account rather than just the default one.

This was Windows-only until it wasn't, on the reasoning that "neither concept exists on macOS". Only the WSL half of that was ever true, and the gap was invisible: macOS already *consumed* the list in `TranscriptReader` and offered no way to fill it in, so a second account got wired once by hand if at all and was never maintained afterwards.

## Grok Build specifically

`docs/grok-findings.md` is the record for Grok Build — the ACP `updates.jsonl` stream, `summary.json` titles, hook payload spellings, and the credits-config shape. Read it before changing `GrokTranscript.cs`, `OrbweaverHook.sh` or `tools/install-grok-hooks.sh`, and add to it rather than to a commit message when you measure something new.

Grok also fires Orbweaver's Claude Code hooks via its compatibility layer. The hook itself treats `GROK_SESSION_ID` as the source of truth so those sessions are labelled `cli:grok` rather than `cli:claude`, and it never writes an `agent-color` record into Grok's transcript.

## Codex specifically

`docs/codex-findings.md` is the reference for everything measured about the Codex CLI — the rollout format, the hook events and their payloads, the TUI's approval prompt, and a clearly separated list of what has *not* been verified. Read it before changing `CodexTranscript.cs`, `OrbweaverHook.sh` or `tools/install-codex-hooks.sh`, and add to it rather than to a commit message when you measure something new.

Two things about Codex support are worth knowing before you are surprised by them:

- **A Codex orb's name comes from Codex, not from the rollout.** Its transcript carries no title record, but `$CODEX_HOME/state_<n>.sqlite` has a `threads` table holding both `name` (what `/rename` set) and `title` (Codex's own, from your first message). The hook prefers the first, exactly as the Claude Code path prefers `/rename` over an auto-title, and falls back to reading the first message out of the rollout for the brief window before a thread is written to the database. There is no `/color` equivalent, so a Codex orb keeps the plain ring. Two sessions started with the same first message in different directories get the same title, which is why the working directory is part of the position key and not just the title.
- **Codex will not run a hook it has not been told to trust.** A `hooks.json` written by anything other than Codex itself starts out untrusted, and editing one later changes its hash and asks again. Until it is trusted no hook fires, no orb appears, and nothing anywhere reports why.
