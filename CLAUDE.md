# Working in this repository

Notes for Claude Code (and anyone else) working here. The README is the real documentation — how the app works, how the hooks work, how to build installers. This file only covers *how work gets landed*, which the code can't tell you.

`AGENTS.md` is the same content for Codex, which does not read this file. Two files because each CLI looks for its own name and neither reads the other's — **if you change one, change both.** A symlink would look like one file until someone checks the repo out on Windows.

@.claude/PERSONA.MD

## How a feature gets built

**Every feature is a ticket, a team, and a round trip through QA — in that order.** The backlog is Jira project **CB**, board 70:

<https://uplifttech.atlassian.net/jira/software/projects/CB/boards/70/backlog>

No feature work starts from a chat message alone. A request that arrives in a terminal, in an issue, or in conversation becomes a CB ticket first, and from then on the ticket is what moves — every agent in the chain leaves the board in a state that says where the work actually is, so someone reading the board and someone reading the terminal see the same thing.

**Prefer the autonomous path at every step.** The escalations below are for an agent that is genuinely not confident, not checkpoints to hit out of habit. An agent that can defend its plan, its tests and its screenshots should carry the feature through to done and say so; one that cannot should ask early, while the answer is still cheap, rather than after building the wrong thing.

### The team

Features are built by an **agent team**, not by one session doing everything. That is the same mechanism this app draws orbs for: Claude Code spawns each member as its own `claude` process carrying `--agent-name`, `--team-name` and `--parent-session-id`, and `AgentTeam.cs` reads the last of those straight off the process to link a member to its lead — deliberately not out of the transcript, so a team that has gone quiet still draws its arrows. Building Orbweaver with the thing Orbweaver visualises is deliberate — a team whose shape looks wrong on the board is a bug report about the app, and you get it for free.

The roles, and who hands to whom:

- **Product manager** — writes the requirement, owns the ticket's status, and makes the final call on done.
- **Architect** and **engineer** — take a refined ticket and build it. Spawn as many of each as the work genuinely splits into; one per independent surface beats one agent doing all of them in sequence.
- **QA** — tests what the engineers build and hands failures back. A loop, not a gate at the end: build a piece, test a piece, send it back, repeat until QA has nothing left to return.

**A team member is a process, not a subagent — and a hook enforces it (CB-273).** In Claude Code, a *named* `Agent` spawn with neither `isolation` nor `run_in_background` comes up as a teammate process carrying `--team-name` and `--parent-session-id`; add either flag and it becomes an in-process subagent, which joins no team and draws no linked orb. CB-272 was staffed that way — engineer and QA as background worktree subagents, no architect stage — by a session that had already written the lesson down. A paragraph is weighed against everything else in context; a refused tool call is not. So `tools/require-team-sdlc.sh`, registered in `.claude/settings.json`, denies a named spawn carrying either flag, an unnamed `cb-teammate`, a `gh pr create` whose body lacks a CB key, `## Verified`, `## Not verified` and an "Installed on" line, and a `gh pr review --approve` that does not name both rids. Teammates create their own worktrees. The hook fails open where `jq` is missing; `tools/test-require-team-sdlc.py` pins every refusal against a passing control. It cannot check stage *order* — architect before engineer — so that one is still on you.

### Product manager: requirement first, then plan

The PM agent creates the requirement in CB as a **`Feature`** and leaves it in **Refinement**, which is where CB's workflow puts a new issue anyway. CB's issue types are `Epic`, `Feature`, `Story`, `Task`, `Bug` and `Subtask` (one word, no hyphen); `Feature` sits at the same level as `Story` rather than above it, so an `Epic` is still what groups a multi-ticket effort.

**A follow-up that fixes an existing feature is a `Bug` against that feature's ticket, not a new `Feature`.** The test is whether a reasonable reading of the original ticket's acceptance criteria already covered the case. If it did, the feature is incomplete, and filing the gap as fresh scope quietly redefines the original as having succeeded: the board then shows a finished feature sitting next to unrelated new work, when what actually happened is that the feature does not do what it said. It picks the wrong branch prefix too — `bugfix/` is what says "wrong on `develop`, not yet released".

CB-133 paid for this rule. It shipped personas parsed out of markdown, and the same day `.claude/PERSONA.MD` in this repository — naming a name, a profile photo and a voice, in a file the resolver provably reads — resolved to nothing at all. Two follow-ups went up as `Feature`s and had to be refiled as `Bug`s, because the requirement had been "whenever an md file mentions a name, voice or profile picture, assign that persona to the orb", and a real file producing no persona is that requirement unmet rather than a new idea. Scope somebody asks for *afterwards* still belongs in a `Feature`, which is what CB-134 correctly is.

Changing a mis-filed ticket's type in place is usually better than deleting and re-keying it — the keys survive and so do the comments. What is not acceptable is leaving an enhancement-shaped summary on a defect: rewrite the summary and description to say what was written, what the app did, and what it should have done, with the real input as the reproduction steps.

**CB's board has four columns, and they are now confirmed** — read off CB-1, the first ticket filed, which is what this paragraph used to ask for. They are **Refinement → Development → Testing → Done**, with transition ids 11, 21, 31 and 41; every transition is global, so any status can be reached from any other. The name this file guessed was "Refining", which is a status on project FMN and not one CB has: a team-managed project owns its workflow separately, and the guess was wrong. Nothing else here needed correcting.

**In refinement the PM surfaces scope forks the requester may not have considered, not just the reading that was asked for.** CB-198 asked for resizable orbs, and the obvious reading was a global slider; whether each orb should also be able to override it was a fork the ticket never raised, and the answer changed the branch — a per-orb key, a right-click submenu, a title migration, mixed sizes in every arrangement. So the PM puts a fork like that to the requester as one question, with a recommendation and the real cost of each branch, rather than refining one branch in silence and discovering the other at review. Then the answer is final: asked once and settled, not reopened each round.

Then the PM writes a plan, and **plans on a stronger model than the one that will implement it.** Planning is where a wrong decision is cheapest to fix and most expensive to miss, so spend the capability there and let implementation run cheaper:

| Feature | Plan with | Implement with |
| --- | --- | --- |
| Complex — a new subsystem, a cross-platform surface, anything touching the hook or transcript contracts | `fable` | `opus` or `sonnet` |
| Simpler — a setting, a bounded UI change, a fix with an obvious shape | `opus` or `sonnet` | `sonnet` |

Those are the `model:` values the Agent and Workflow tools accept (`fable`, `opus`, `sonnet`, `haiku`). `effort:` is the other dial and moves the same way — high while planning, lower for the mechanical stages afterwards.

**If the PM is not confident the requirement and plan are enough to build from autonomously, it asks a human before spending a team on it.** Ask on GitHub, against the repo, with a link to the CB ticket — and because the Jira site needs its own login separate from GitHub's, put enough of the requirement in the comment that the question stands on its own for someone who cannot open the link. Repo admins are the escalation point (`wtvamp` and `lunarjuice` at the time of writing; `gh api repos/Uplift-Foundation/Orbweaver/collaborators` prints the current list).

If it *is* confident, it hands straight to the architects and engineers, and the feature proceeds without a human in the loop.

### Build and QA, in a loop

Engineers and architects build, QA agents test, and the work goes back and forth until it stops coming back. The substance of QA's half is **Every feature ships with its tests** below — unit, integration and UI, covering 100% of the lines the branch adds — so "QA passed" means those exist and are green, not that somebody had a look.

**A feature works on Windows and macOS, or it is not a feature.** CI enforces the shape of that already: the matrix is `macos-latest`/`osx-arm64` and `windows-latest`/`win-x64`, and every suite runs on both legs. Parity is more than a green build, though — a feature no install path wires up is equally unfinished. Before calling it done, check `tools/build-macos-dmg.sh`, `tools/build-macos-app.sh`'s Resources copy, `tools/Orbweaver.iss`, `tools/install-hooks.sh`/`.ps1` and the README's install section, and satisfy yourself that any platform-specific gate is real (WSL) rather than accidental.

### PR, screenshots, and the call on done

The feature goes up as a PR against `develop`, per **Pull requests** below.

**Don't attach screenshots by hand — they are already automatic.** `ci.yml` captures a PNG per `tests/UiScreenshots` scenario on both runners; `publish-screenshots.yml` then picks those artifacts up on a `workflow_run` trigger, pushes them to the `screenshots` branch and comments on the PR with real `github.com/.../raw/...` image URLs, labelled per rid. Those are `github.com` links rather than `raw.githubusercontent.com` ones on purpose (CB-118): the latter is a separate origin that never sees the viewer's `github.com` session, so it 404s in a browser the moment this repo is private, even for someone with full read access — the `github.com/<owner>/<repo>/raw/<branch>/<path>` form is same-origin with the PR comment page and 302s through a freshly signed `raw.githubusercontent.com` URL on every load. (The two-workflow split is not stylistic: a `pull_request` run from a fork gets a read-only token and every PR here is one. Both files' header comments have the full story.) A feature with a visible surface adds its capture to that suite, and the PR comment is what everyone reviews.

The PM agent reviews that comment — **both** rids, since a macOS-only implementation shows itself precisely there — and **if it can approve the feature as done autonomously, it should**: approving the PR itself, and moving the ticket accordingly.

**Approving is the agent's own call, not a checkpoint to hand back.** An agent that has read both rids, can point at the tests covering the change and can say what it confirmed on a real machine is holding everything a human reviewer would be handed — asking a person to look anyway is the "checkpoint out of habit" this file already warns against, and it costs a round trip to be told what the agent already knew. So approve it:

```bash
gh pr review <number> --repo Uplift-Foundation/Orbweaver --approve --body "..."
```

The body is the part that matters, and it is the same distinction the rest of this file keeps: name both rids, say which suites ran, and separate what was confirmed on a machine from what is assumed. "LGTM" from an agent is worth nothing to the next person; "routing verified end to end on a real Mac, Windows not reproduced and not fixed" is worth the whole review.

GitHub refuses an approving review on a PR the same account opened, which is the common case here since one person's token opens and reviews it. That is a mechanical limit, not a reason to escalate — post the identical body as a review comment instead. The written record is the point, not the green tick.

Approving is not the same as landing it. The ticket moves to Done when the change is actually on `develop`, so an approved-but-unmerged PR stays in Testing until the merge goes through.

Only where the agent genuinely cannot approve does it ask a human to pull the branch, install it and approve by hand:

- **If someone is driving the feature in Claude Code**, ask them in the terminal. They are already there and the round trip costs seconds.
- **If nobody is watching the terminal**, ask on GitHub, on the PR.

Either way, name the specific thing you could not confirm from the screenshots. "The Windows flyout renders, but I can't tell from the capture whether the mic button is enabled" is a request someone can settle in a minute. "Needs manual approval" is not.

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

**`bugfix/` and `hotfix/` are not a severity judgement — they say where the fix starts.** A bug that only exists on `develop`, or one that is shipped but can wait for the next release, is a `bugfix/` off `develop`: it rides the normal train and there is nothing to fix on `main`. A `hotfix/` branches off `main` because it has to reach released users without waiting for whatever else is sitting on `develop`, and it merges to both so the fix isn't lost at the next release. If you're unsure which, ask what a user on the latest release experiences today: if the answer is "nothing wrong yet", it's a `bugfix/`.

Docs and chores go on `feature/` unless they're correcting something wrong, in which case `bugfix/` says more.

Name the rest of the branch after the change, not the issue number: `feature/persist-orb-positions`, not `feature/pr-12`.

Releases before 0.1.2 used flat names (`release-0.1.1-beta`) and features went straight to `main`; that's history, not a pattern to copy.

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

**Don't rename a pushed branch to fix its name.** GitHub closes the open PR instead of retargeting it, and reopening is refused once the old head ref is gone; you have to open a fresh PR. Get the prefix right before the first push.

## Remotes

`Uplift-Foundation/Orbweaver` is the canonical repository; branches and PRs go there. **Which remote name points at it varies by clone** — run `git remote -v` rather than assuming:

- Clones made from the canonical repo call it **`origin`** and have no `upstream`, so `git fetch upstream` fails outright.
- Clones made from the **`wtvamp/Claude-Buddy`** fork call the fork `origin` and the canonical repo `upstream`. Older branches still track the fork; don't add to them.

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

End Claude-authored commits with the `Co-Authored-By:` and `Claude-Session:` trailers.

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

macOS ties the Automation (Apple Events) consent to the app's code identity, so an ad-hoc build silently breaks click-to-focus until the user re-approves it in System Settings — and that failure is invisible. Same reason the bundle id is never renamed casually; the comment at the top of `tools/build-macos-app.sh` has the full story.

**Local Network consent works the same way, and breaks on every upgrade.** Replacing `/Applications/Orbweaver.app` gives the bundle a new CDHash, macOS re-evaluates Local Network access against it, and the grant does not carry over — so anything on the LAN, OpenClaw's gateway most of all, starts failing with `EHOSTUNREACH`. Nothing prompts loudly enough to notice: this is a menu-bar app with no Dock icon and no window.

What makes it expensive is that **every obvious check agrees with the wrong answer.** `ping`, `nc`, `curl` and `ssh` are Apple-signed and exempt from the gate, so they all report the host perfectly reachable while the app cannot open a socket to it — and `nc -z` will additionally claim success against ports that refuse an honest connect. Reach for the app's own transport instead:

```bash
dotnet run --project tools/openclaw-probe -- sessions
```

Note the probe is itself subject to the gate, because `dotnet` is a third-party binary. **A failing probe alongside a working app is expected, not a contradiction** — it says the terminal lacks the grant, not that the app does. `OpenClawGateway.ExplainConnectFailure` now appends a hint naming the settings pane, so the app says this itself rather than only reporting the errno. CB-38 has the full diagnosis.

`<Version>` in `Orbweaver.csproj` is the single source of truth for the shipped version — the packaging scripts and the release workflow parse it out of there.

## Every feature ships with its tests

**A feature request is not finished when the feature works. It is finished when the feature works and the code that makes it work is covered.** Tests land in the same branch as the behaviour they cover — not a follow-up, not an issue filed against `develop` — and the target for the lines that branch adds or changes is **100%**: every new method, every arm of every new conditional, every error path you wrote a `catch` for.

Cover it at all three levels the suites already separate, rather than only the one that is easiest to reach from where the change happens to live:

- **Unit** (`tests/UnitTests`) — the decisions. If the change added a rule about which orb wins, which name is used, or where something is drawn, that rule belongs in a function with no window and no settings behind it, and that function gets a case per outcome. If new logic can't be reached without constructing a window, that is a seam to fix before writing the test, not a reason to skip it — the same argument that keeps `OrbArrangement`, `OrbGlyph` and both transcript parsers pure.
- **Integration** (`tests/IntegrationTests`) — the seams with what this process does not own: the hook scripts run as real subprocesses, files on disk, settings round-tripped through a real file. Anything touching a format someone else defines is covered here *as well as* by a unit test of the parsing, because the two fail differently — the parser gets a field wrong, the seam gets the whole exchange wrong.
- **UI** (`tests/UiTests`) — the headless Avalonia path. A new window, panel, control, binding or click handler gets driven with a synthesized click or a real `UpdateFrom`, and asserted on what a user would have seen. `ChatPanel`'s `FakeChatSession` is the pattern for anything that would otherwise need a live session behind it. A new *visible* surface also gets a capture in `tests/UiScreenshots`, which renders through real Skia rather than the null renderer; its cases are hand-written one per scenario, so adding a `UiTests` scenario does not add its screenshot for you.

A change to geometry, transcript parsing or orb initials extends the three console suites too — `dotnet test tests/Tests.sln` does not run them, and CI failing on `ArrangementTests` after a green `dotnet test` is a bad way to find that out. CI runs every suite on both runners, so a test that only passes on the machine you wrote it on blocks the build.

**100% means the diff, not the repository.** The app is nowhere near it — as of this writing `UnitTests` alone covers about 3% of the assembly, and whole areas of it (`TerminalFocuser`, the tray, the installers) make real `tmux`/`ps`/`osascript` calls that a headless runner must not execute. Holding new work to 100% is how that number climbs without a rewrite; holding the existing repository to it would make the rule something everybody quietly ignores, which is worse than not having one.

Where a line genuinely cannot be covered — an OS call with no seam, a `catch` for something only the other platform throws — **name it in the PR body and say why**, in the same voice this project already uses to separate "confirmed on a real machine" from "assumed to work". An uncovered line that is named is a known gap; an uncovered line that isn't is a claim about the suite that isn't true.

To see the number:

```bash
dotnet add tests/UnitTests/Orbweaver.UnitTests.csproj package coverlet.collector
dotnet test tests/UnitTests --collect:"XPlat Code Coverage"
# tests/UnitTests/TestResults/<guid>/coverage.cobertura.xml
```

**Nothing in the repository collects coverage today, and the flag fails silently without that package.** `--collect:"XPlat Code Coverage"` against a project with no `coverlet.collector` reference creates an empty `TestResults/` directory, prints no warning and exits 0 — a run that measured nothing, and reads exactly like one that measured everything. Add the package to whichever suite you are measuring and take it back out before you push, unless you are deliberately wiring collection into all three suites and CI, which is its own change and needs its own reasoning.

## Testing UI behavior

Orb *geometry* has a test suite — `dotnet run --project tests/ArrangementTests`. It walks every shape at every end of the spacing slider and orb size, mixed sizes included, across a range of orb counts and team shapes (nested teams, everything in one team, lead cycles, a lead that points at nothing) on three screen sizes, and asserts that nothing leaves the work area, nothing is drawn on top of anything else, and no team member ends up too far from its lead to be read as one. Run it after any change to `OrbArrangement`; it exits non-zero and prints the exact case that failed.

That exists because the arrangement was fixed by eye half a dozen times and each fix broke a case the previous one had fixed. Keep the geometry in `OrbArrangement` — pure, no windows, no settings — so it stays testable, with `SessionManager` only mapping orbs onto its inputs and its answers back.

Transcript and dialog parsing has one too — `dotnet run --project tests/TranscriptTests`. It covers `ChatTranscript`: turning Claude Code's JSONL into chat turns, and reading a permission dialog off a captured tmux pane. It also covers `CodexTranscript`, which does the first of those for Codex's rollout JSONL, and `GrokTranscript` for Grok Build's ACP `updates.jsonl` (see `docs/grok-findings.md` before changing that parser). Same rule as the geometry — the parsers are pure, and the chat session only decides which bytes to hand them.

Both parsers read formats nobody here controls, and both fail *quietly*: a mis-mapped transcript silently drops a message, and a mis-read dialog puts a button on screen that presses something other than what it says. Write fixtures from real output, not from memory — the dialog parser was first written against an invented fixture and failed on every real dialog. To capture one:

```bash
tmux capture-pane -p -t %<pane> > /tmp/pane.txt
dotnet run --project tests/TranscriptTests -- /tmp/pane.txt   # or a .jsonl
```

Both CLIs write `.jsonl`, so the harness decides which parser to use by looking at the file's first row rather than its extension — hand it a Codex rollout from `~/.codex/sessions/<yyyy>/<mm>/<dd>/` and it says so in its first line of output. If that line names the wrong CLI, nothing below it means anything.

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

Three more suites, all xUnit rather than the bespoke console-exe pattern above, live in `tests/UnitTests`, `tests/IntegrationTests` and `tests/UiTests`. One command runs all three: `dotnet test tests/Tests.sln`. They join the three suites above rather than replacing them — `Tests.sln` holds only the xUnit projects, so it can't accidentally try to `dotnet test` an exe with no test SDK reference, and `Orbweaver.sln` stays app-only. CI (`.github/workflows/ci.yml`) runs every one of them, plus `tests/UiScreenshots`, on both runners, before packaging — a failing test blocks the build the same way a failed `dotnet publish` already did.

**Run the UI suite in Release before pushing, because CI does and `dotnet test` does not.**

```bash
dotnet test tests/Tests.sln            # Debug — what everyone runs
dotnet test tests/UiTests -c Release   # what CI actually runs
```

**Run CB-119's guard locally too — `dotnet test`'s own exit code is not enough.** During CB-120's work it fired three times (twice for an implementer, once for a reviewer), each time a test count one short with zero failures and zero skips, clean on immediate rerun — a higher rate than the 2-in-28 originally measured, and none of the three was caught, because the guard only ran in CI:

```bash
dotnet test tests/UiTests -c Release -- --report-xunit --report-xunit-filename ui-tests.xunit.xml
pwsh tools/check-xunit-report.ps1 -ReportPath tests/UiTests/bin/Release/net10.0/<rid>/TestResults/ui-tests.xunit.xml -SuiteName UiTests -MinimumExpectedTests 1050 -Attempt 1 -MaxAttempts 1
```

(`<rid>` is whatever `dotnet test` printed for your machine. **1050 is the
value `ci.yml` actually sets today — read off `$minimumExpectedTests` in the
UiTests step, not remembered.** This paragraph said 1005 until CB-141 checked
it against the file, and a floor 45 too low is the worst kind of wrong number:
it reads as measured, it never fails, and it silently stops being the guard it
claims to be. If you quote it again, quote it from `ci.yml`. Bump the floor
itself only when the real total moves meaningfully, in both places at once, and
never to make a red run pass. The real total sits comfortably above this floor
and moves every time a branch lands — CB-141 watched it go 1090 to 1091 during
its own rebase — so measure it when you need it rather than reading a number
out of this paragraph.)

`AGENTS.md` carries this same advice and deliberately names **no** number,
pointing at `.github/workflows/ci.yml` instead. That is the better shape and it
is why it needed no correction here: a figure copied into prose goes stale
without anything failing, and the two files are in sync on substance even
though only one of them has a digit in it.

**To run one UI test class, filter with the runner's own flag, not `--filter`.** `tests/UiTests` and `tests/UiScreenshots` run on xunit v3 and the Microsoft Testing Platform, which does not read VSTest's `--filter` at all: `dotnet test tests/UiTests --filter ...` is accepted without complaint and then runs the whole suite, which is exactly the full run the targeted-test rule exists to avoid, and it reads as a normal pass or fail until you notice the total. Two engineers hit this on the same day (CB-270). The form that works is `dotnet run --project tests/UiTests -c Release -- --filter-class <Namespace.Class>` (or `--filter-method "*MethodName"`), and `dotnet test tests/UiTests -c Release -- --filter-class <Namespace.Class>` works too; the same applies to `tests/UiScreenshots`. `tests/UnitTests` and `tests/IntegrationTests` are on VSTest and keep `--filter`. Check the total in the summary line before trusting a targeted run.

`dotnet test` defaults to Debug; `ci.yml` builds Release. That gap is not theoretical and it is not about optimisation changing behaviour — Release simply runs faster, which reorders a parallel suite and closes the gaps between writes to a scratch directory. CB-3 landed six `SessionScanTests` that were green in Debug on three separate machines and red in Release on both CI legs, every attempt: no exception in the app, just a scan that found no sessions, because a timing assumption held at Debug speed and not at Release speed.

The other three suites have been clean in both so far. It is `tests/UiTests` that is worth the extra half-minute, being the one with a dispatcher, real timers and process-wide statics in it. If a test passes in one configuration and not the other, the answer is to make it independent of what else is running — never a sleep, never a widened tolerance. This branch has fixed four flakes of that shape and each commit says why.

**What a short run looks like, and where it came from (CB-183).** The fingerprint is one `<error type="test-case-cleanup">` in the xUnit report carrying `InvalidOperationException: The calling thread cannot access this object because a different thread owns it`, thrown from `Avalonia.Rendering.DefaultRenderLoop.Add` under `AvaloniaHeadlessPlatform.Initialize` under `HeadlessUnitTestSession.EnsureIsolatedApplication` — Avalonia.Headless's per-test session isolation, not any test of ours. The test name on the error is whichever test was due to run next, and it is different on nearly every short run, so never record a victim's name as the defect's location; `AccountOrbsTests` was recorded that way once and was simply first to be hit. The cause, read out of the Avalonia 12.1.1 assemblies and then forced deliberately: per-test isolation, the package default, nulls the process-wide `Dispatcher.s_uiThread` around every test, and the next thread to read `Dispatcher.UIThread` becomes its owner — so a pool thread left behind by an earlier test (a watcher, a timer, a fire-and-forget `Post`) could claim the UI thread out from under the next test's setup. A scratch thread doing nothing but read `Dispatcher.UIThread` turned about 1350 of 1412 tests into exactly this error on every run under per-test isolation, and none under `PerAssembly`. `tests/UiTests/TestAppBuilder.cs` now declares `[assembly: AvaloniaTestIsolation(PerAssembly)]` and `UiDispatcherIsolationTests` pins it. The cost is that the headless platform — the clipboard most visibly — is now shared between tests, so a test that needs something empty empties it rather than assuming it; that exposed one such test. Measured on one Mac, 30 Release runs each through CB-119's guard: 2 of 30 short before (errors=1, zero failures, exit 0 both times, the victims `AccountOrbsTests` and `CloudScanTests`), 0 of 30 after — too few events on their own to call the rate fixed, which is why the forced run above is the evidence that carries the causal claim. If a short run with this fingerprint ever comes back, the isolation level has been changed or something is creating its own session — look there first.

That advice is for a flake that *fails* — it has nothing to investigate when the flake instead silently drops the test count, because there is no failure to chase down. CB-119's guard above is what turns a count-flake back into an ordinary failure with an exit code, which is the only way this section's advice can reach it at all.

They reference `Orbweaver.csproj` directly with a `<ProjectReference>` rather than compiling individual files in with `<Compile Include>` the way the three suites above do. That convention holds for a file with a small dependency closure; `SessionManager` and `OrbweaverSettings` do not have one, and pulling either in that way would mean compiling most of the app a second time. A `<ProjectReference>` also needs `<InternalsVisibleTo>` to see anything not `public` — granted in `Orbweaver.csproj` to exactly the three new test assembly names, nothing else.

**`tests/UnitTests`** covers more of the same pure, no-window logic as the suites above — most valuably `SessionManager`'s `Superseded` and `InheritTerminalInfo`, the rules that decide which of several status files for one process is the live orb and which sibling donates terminal coordinates to one that has none. Both are `internal`, made reachable the same way.

**`tests/IntegrationTests`** drives `OrbweaverHook.sh`/`.ps1` as real subprocesses — payload on stdin, a scratch `TMPDIR`/`-TempDir`, asserting the one invariant everything else depends on: exit 0, empty stdout, empty stderr, always. Codex reads a hook's stdout as strict permission-request JSON and treats exit code 2 as a deny, so a hook that ever prints anything starts silently refusing the user's own approvals — this is what actually enforces the rule the hook scripts' own comments state. It also covers `TranscriptReader`'s tail-window and truncation rules against temp `.jsonl` files, and `OrbweaverSettings`' round-trip of unknown keys — the protection against exactly the downgrade that once silently erased three settings from a real file (see the comment on `_unknownKeys` in `OrbweaverSettings.cs`). The `OpenArgumentDeliveryTests` cases that launch app bundles through LaunchServices skip on a developer's Mac, because every launch leaves a tile in the logged-in user's Dock, and run only in CI (`CI=true`) or with `ORBWEAVER_LAUNCH_TESTS=1` (CB-246). Every `ORBWEAVER_*` variable in this repo used to be spelled `CLAUDE_BUDDY_*`; the old spelling still works as a fallback, and the new one wins when both are set.

**`tests/UiTests`** runs headless, via `Avalonia.Headless.XUnit` — no display, no window ever actually shown. It uses the real `App` class as its own test host rather than a parallel stand-in: under Avalonia's headless lifetime, `App.OnFrameworkInitializationCompleted`'s guard for `IClassicDesktopStyleApplicationLifetime` is never true, so its entire body — mutex, `SessionManager.Start()`, tray icon — never runs, with no test code needed to arrange that. It drives `OrbFlyout` with real synthesized clicks, `OrbWindow.UpdateFrom` against hand-built `SessionStatus` values, and `ChatPanel` through `FakeChatSession` — an in-memory `IRemoteChatSession`, which `RemoteChat.cs`'s own header comment says the interface exists to make possible, used here for the first time for exactly that. It does not synthesize a click on an orb or a chat panel's send-via-mouse path: an orb click reaches `TerminalFocuser`, which fires real `tmux`/`ps`/`osascript` processes off-thread with no OS guard at its own entry point, so a headless click on a CI runner would be a real, unpredictable side effect rather than a test.

All three new suites need settings.json out of the way, since even constructing an `OrbWindow` reads a color setting in a field initializer — see the next paragraph for why that file cannot otherwise be pointed elsewhere. Each project's `TestBootstrap.cs` sets `ORBWEAVER_SETTINGS_DIR` to a fresh temp directory via a `[ModuleInitializer]`, before any test can run and before any settings static constructor can fire.

## Coverage

```bash
./tools/coverage.sh                            # whole-app line and branch coverage
./tools/coverage.sh --base upstream/develop    # ...and just the lines you added
```

**Two collectors, not one, and it has to stay that way.** `tests/UnitTests` and `tests/IntegrationTests` run on VSTest and use `coverlet.collector`; `tests/UiTests` and `tests/UiScreenshots` run on the Microsoft Testing Platform (both moved to xUnit v3 for `Avalonia.Headless.XUnit` 12.x — see their csprojs) where VSTest data collectors do not apply at all, so they use `Microsoft.Testing.Extensions.CodeCoverage`'s own coverage switch instead. That package is **pinned to 17.14.2** in both, for the same reason everything else in those csprojs is pinned: 18.x depends on `Microsoft.Testing.Platform` 2.x while xunit.v3 3.2.2 brings the `mtp-v1` packages, and the mix throws `TypeLoadException` for `IDataConsumer` before one test runs. Bump xunit.v3 and you have to re-check both pins.

That leaves four cobertura files measuring the *same* assembly, which is what `tools/merge-coverage.py` is for: a line counts as covered if **any** suite covered it. Adding the reports up instead is wrong in both directions at once — it double-counts the denominator while undercounting the numerator, since a line only a UI test reaches is reported unhit by the other three.

`tests/UiScreenshots` counts as of CB-3 and did not before. CI has always run it, and it is the only suite that draws through **real Skia** rather than the null renderer — so a few things are reachable only there, a bitmap actually written to disk most obviously (`ClaudeDesktopBundles.WriteTinted`, whose pixel maths is tested there for exactly this reason). Leaving it out meant those lines were verified and counted nowhere, which is the same invisible-verification problem the console suites had.

**Before quoting a number, check the run actually produced one.** Two of the three times a coverage figure has been wrong here, it was not wrong about the code — the run had not measured what it claimed to, and said so on a line nobody read. Both checks below cost seconds and each has already cost hours by being skipped.

**`merged N report(s)` is the first line of the output, and it is a self-check.** There are exactly four cobertura files, for the reasons above, so **anything other than `merged 4` means the number printed underneath it is fiction.** Both directions have been seen live, within one afternoon on CB-6:

- `merged 1` — three reports missing, because another agent working in the same clone ran `rm -rf bin obj tests/*/bin tests/*/obj` while this one was measuring. The binaries the report maps against were deleted underneath it.
- `merged 6` — two stale extras, left in the MTP suites' own `TestResults` directories from an earlier run. `coverage.sh` fishes reports out of there and does not clear it first, so an old one is simply merged in alongside.

**"Quiet tree" means the process table, not the working tree.** `git status` being clean proves nothing about what is running; neither does an agent roster that looks idle, because an agent between tool calls is indistinguishable from one mid-`dotnet test`. Check `pgrep -fl "dotnet|coverage.sh|testhost"` for a test host, a `dotnet test` or a second `coverage.sh`, then rebuild clean. **A number taken while anything else was building does not leave the session that took it.**

**And that check has three traps in it, all of which fired in one evening on CB-172.** It lies in both directions and the false-clear is the dangerous one.

**Stopping a background job does not stop the test host.** The harness kills the shell; the `dotnet test` child survives, re-parented, and keeps running in the worktree. So an agent can stop its run, check the table, find it clear and still have a live test host in the tree — which is exactly how two runs end up in one worktree with nobody having ignored anything. It happened three times in one evening (one of them noticed only as an unexplained process "77 seconds old" that nobody could account for). After stopping a run, look for the child by worktree path and kill it explicitly; a job that reports itself stopped has not told you about its children.

**`pgrep -f` matches the shell wrapper, not just the work.** Every command here runs under a `/bin/zsh -c source .../shell-snapshots/...` wrapper that contains the whole command line, so `pgrep -f "dotnet test"` matches the wrapper — including the wrapper of the command doing the asking. That produces a false positive that reads as somebody else's run (one was reported as another agent's suite, and its worktree turned out not to have been written to in ninety minutes), and it makes `until ! pgrep ...; do sleep; done` a loop that can never exit, because the waiter matches itself. Filter the wrappers out, and never wait on absence.

**Three of the matches are furniture and will never go away.** `VBCSCompiler` and the `MSBuild.dll /nodemode:1 /nodeReuse:true` workers are persistent build servers that outlive every build by design. The condition worth waiting on is *no active build or test host* — nothing under a worktree path, nothing carrying `testhost` or `--coverage` — not an empty `pgrep`. Reading it as "returns nothing" leads either to waiting forever or to killing the daemons to make the check pass, which is damage done to satisfy a check.

**Check and launch in separate commands.** Putting `pgrep` and the run in one command prints the answer after the launch is already committed, which is not a check. Two runs collided in one worktree that way, twice.


The failure this catches is worth recognising by sight, because it does not look like a measurement error — it looks like a regression. On CB-6 a phantom `56/60 = 93.3%` branch figure was reported as a real drop, sent back to an engineer as work, and never reproduced: six later runs all gave `56/56 = 100%`. What gave it away was reading the lines it named as uncovered. Two were **closing braces** and one was a range-slice assignment — none of which can hold a branch in the source at all. Branch arms attributed to punctuation is the signature of a report mapped against a binary that is stale or no longer there, and it is quicker to spot than to re-run.

That is now three documented cases of an unreproducible number in this file, and only one of them was the code's fault. The next one will also read as a regression. Check `merged 4` and the process table first.

Four things the number does not say, worth remembering before quoting it:

- **The number is only reproducible because the settings-touching UI classes are serialised — keep them that way.** `OrbweaverSettings` is a process-wide static and nearly every visual class reads it while being constructed, so two test classes running in parallel with one of them flipping a setting do not race to a failure, they race to a *different set of executed lines*. Before CB-3 serialised them, three consecutive runs of `tests/UiTests` over an identical binary reported 1914, 2024 and 1914 covered lines in `SettingsWindow.cs`. That swing is bigger than most real changes, so it reads as one — and it cost this ticket an hour of chasing a 145-line "regression" that was scheduling. Anything new that reads or writes settings joins `[Collection("Settings")]` in `tests/UiTests/SettingsCollection.cs`, whose comment has the rest of the story.

- **`--base` is the number that matters when reviewing a change.** A file-level percentage is dominated by whatever was already in the file; the added-lines figure is the one that says whether the new code is tested.
- **The three console suites still contribute nothing to it** as suites — `ArrangementTests`, `GlyphTests` and `TranscriptTests` are plain exes, not test-SDK projects. Their *cases* do count now, because CB-3 moved each one's matrix into a class that `tests/UnitTests` compiles in and runs (see `ArrangementSweep`, `GlyphSuite`, `TranscriptSuite`), so `OrbArrangement` no longer reads 0% while being the most exhaustively verified file in the repo. Running the exes is still the way to get the grouped failure report.
- **What has been excluded is printed next to the number.** An excluded file and a deleted one look identical in a report, and a percentage can be walked to 100% by excluding whatever refuses to be covered. `merge-coverage.py` therefore reads the attributes back out of the sources and reports files held out entirely, further sites inside measured files, and files absent for no stated reason at all. Read the headline as coverage **of what remains**.
- **The two engines disagree about `[ExcludeFromCodeCoverage]` on a *method*, and the merge compensates.** coverlet honours it — an excluded method's body is not instrumented at all — while `Microsoft.CodeCoverage`, which both MTP suites use, instruments it anyway and reports every line unhit. Both honour it on a *class*, which is how the gap went unnoticed until CB-3 had 157 member-level exclusions for it to show up in. So the merge is not a plain union: **coverlet's view of which lines *and which branch points* exist is the authority**, and the MTP reports contribute hits for those only. If you ever change that, an exclusion on a method silently stops meaning anything.

  The branch half of that rule was missing until CB-100, and the MTP reports were inventing branch points wherever they liked. Measured over one run of all four suites: **158 branch points in 42 files existed in the MTP reports and in neither coverlet report — 416 arcs, 328 of them claimed as taken.** Read the lines and it is CB-6's signature again, arcs attributed to code that cannot hold one: `OpenClawSessions.cs:285` is `lock (Gate) return agentIds` reported `(0/6)`, `:328` is a bare `List<string> ids;` reported `(0/2)`, and `OrbGlyph.cs:62` is the second line of a *comment* reported `(4/4)`. They are compiler-generated arcs the MTP engine pins to the nearest statement line and coverlet puts elsewhere or nowhere.

  **It moved the number in both directions, which is why nobody caught it.** Dropping them took the whole app from 9361/10483 (89.3%) to 9033/10067 (89.7%); per file, `OpenClawSessions.cs` went 91.6% → 92.4% and `OrbGlyph.cs` went 94.4% → 93.8%, since a phantom `4/4` flatters a file exactly as a phantom `0/6` punishes one. CB-100 was filed as "understates branch coverage", and the understating half is real — but a fix tuned only to that would have left the flattering half in place, which is the same lesson as the four-too-strong-two-too-weak paragraph above. `tools/test-merge-coverage.py` pins both halves of the rule now, and CI runs it.

  **And the total, not only whether the point exists (CB-244).** The rule said coverlet decides which branch points exist, but `merge_secondary` still took `max()` of the *total* for a point both engines knew, so it adopted the MTP engine's wider arc count: `ChatMarkdown.cs:179` was `6/6` in coverlet's unit run and `10/12` in the MTP runs, and merged to `10/12` — a denominator for arcs coverlet says the line does not have (reports and sources at the same sha, 95e84ea5). Coverlet's total is the total. The `max()` had an honest reason, and a bare cap would have brought it back: pairing a taken count from the wider engine with the narrower total can print a line as fully covered when neither suite covered it — **7 points, 13 arcs on the tree measured.** The two engines' arcs cannot be paired, so an MTP count is now believed only where it needs no pairing: the same total (the larger taken count wins), or a wider total that MTP covered *completely* (every arc coverlet counts, capped at coverlet's total). A wider total covered partly, or a narrower one, adds nothing. On the same four reports that moved the whole app's branches from 12855/13994 (91.9%) to 12746/13816 (92.3%), lines unchanged: 33 of 172 files moved, 23 up and 10 down — in both directions again. `tools/test-merge-coverage.py` pins it, including the 6/6 + 2/8 shape and a control that an MTP hit on a known arc still counts.

Everything else about orb behavior is still verified by running the app. Two things make that survivable:

- The status directory comes from the temp path, so `TMPDIR=<dir>` plus hand-written `<session-id>.txt` files gives a second instance its own fake sessions without touching real ones.
- The cloned-bundle cache honours `ORBWEAVER_BUNDLE_ROOT`, added by CB-3 for the same reason: without it, the only place a test of `ClaudeDesktopBundles` could write is the real `~/Library/Application Support/Orbweaver/bundles` — the live cache, holding real cloned `.app` bundles whose icons a user is looking at. That the seam did not exist is the whole reason nothing in that file was covered.
- Settings now honour `ORBWEAVER_SETTINGS_DIR`, an env-var override checked before `SpecialFolder.ApplicationData` — the same pattern as `ORBWEAVER_PROFILE_ROOT` in `ClaudeDesktopManager.cs`. Without it a test instance reads and writes the real `~/Library/Application Support/Orbweaver/settings.json`; a manual run that skips setting it should still back that file up first.

Read window geometry back out of the window server (`CGWindowListCopyWindowInfo` by owner pid) rather than eyeballing a screenshot when a change is about *where* an orb sits — and don't synthesize mouse events on a machine someone is using, because their real input interleaves with yours and the result is nonsense.

<!-- profile-gen:start slug=persona -->
@.claude/persona/persona.md
<!-- profile-gen:end slug=persona -->
