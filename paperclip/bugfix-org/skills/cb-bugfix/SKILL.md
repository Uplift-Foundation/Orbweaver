---
name: cb-bugfix
description: Rules for fixing a bug on the CB Jira board for Orbweaver without a person in the loop - which tickets to take, how to claim and release one, the branch, commit and PR rules, how QA reviews, and when to merge. Use on every fix run and for every task assigned during one.
---

# Fixing a CB bug

## Which tickets

Jira project `CB` on `uplifttech.atlassian.net`. The `cloudId` is
`db488eae-d6cb-4100-92a9-e7a4519fc176`; pass it to every call and do not call
`getAccessibleAtlassianResources`. Query:

```
project = CB AND issuetype = Bug AND status = Refinement ORDER BY priority DESC, created ASC
```

That query picks what to take. A second query, in **Bugs outside
Refinement**, finds the Bugs nobody is working and settles them. Every pass
runs both.

## Every ticket gets an outcome

**No ticket the query returns is skipped in silence.** A pass that read 18
bugs, took none and changed nothing, then said the same thing thirty minutes
later, is the failure this section exists to stop (CB-480). Every ticket the
query returns leaves the pass with exactly one of these outcomes, decided in
this order; the first that applies is the one:

- **Wait: another team's PR.** An open PR's **title** names the key, in
   either repository. Check with
   `gh pr list --repo Uplift-Foundation/Orbweaver --state open --search "<key> in:title" --json number,title`
   and the same with `--repo Uplift-Foundation/Orbweaver-Mobile`, and confirm the key is literally in a returned title (the search is fuzzy).
   A key named only in a PR body, a commit message or a branch is **not** a
   reason to wait: work named in passing is not somebody holding the ticket.
   If that PR is the org's own, it is settled by the lead's step 0, not here.
   **The wait lasts while that PR moves.** Read
   `gh pr view <n> --repo <repository> --json updatedAt,headRefOid`
   (`updatedAt` moves on a push, a review or a comment). When it has not
   moved in 72 hours it no longer holds the ticket: take it. The engineer
   branches its own `bugfix/pc-*` from `origin/develop` and may cherry-pick or
   build on that PR's commits, and the lead leaves one comment on that PR
   (`gh pr comment <n> --repo <repository> --body "..."`) saying the org has
   taken the ticket in its own branch and which commits it builds on.
- **Wait: triage.** It has no comment starting with `[triage]` yet. The triage
   org runs every four hours, so this wait is short and is counted, not fixed.
- **Wait: put back.** The newest comment starting `[bugfix-org]` is a
   `[bugfix-org] stopped:` put-back (**Putting a ticket back**). It stays a
   wait, with no Jira write, counted on the line its sentence names
   (`Mobile access not live yet`, `Needs a protected workflow line` or `Needs
   an input the org cannot make`), until a
   person comments after it or, for mobile access, the check in **Two
   repositories, one code base** passes; then it is decided afresh.
- **Wait: mobile access.** The fix is in the mobile repository and the check in
   **Two repositories, one code base** finds it unreachable: no Jira write,
   counted on `Mobile access not live yet`, and not a defect of the pass.
- **Close.** The ticket is no longer a bug on `develop`, and the evidence is
   in hand, not inferred:
   - **already fixed:** a PR merged to `develop` fixed it. Name the PR and its
     merge sha, and say why it covers what this ticket describes (read the
     PR's diff and the ticket's reproduction steps side by side; a PR that
     mentions the key is not evidence it fixed it);
   - **duplicate:** another ticket describes the same defect. Name it, and say
     which one carries the work;
   - **cannot reproduce:** a real attempt was made and the bug did not appear.
     Say what was run, on which machine or in which test, at which sha, and
     what was seen. Reading the code and concluding it looks fine is not an
     attempt. A first-time cannot-reproduce comes from a claimed
     reproduce-or-fix round (**Take it**), never from reading alone.
   Close by moving it to Done (transition `41`) and posting one comment that
   starts `[bugfix-org] closed:` with the reason and the evidence. Close only a
   ticket in Refinement or one the org itself claimed, never one a person has
   in Development or Testing (a person's ticket whose own PR has merged is
   settled by **Bugs outside Refinement**, not closed). A close is
   **not a release and needs no claim**. When the close ends a ticket the org
   had claimed, set the engineer's Paperclip issue to `done`, not `cancelled`:
   the ticket was settled.
- **Take it: fix or reproduce.** Everything else is eligible, in either
   repository, including a security bug, a fix to a workflow file, a bug that needs a real captured prompt (class 4 below) and a
   bug that needs a real machine (class 3). Among them take the highest Jira
   priority, then the lower class number, then the oldest, up to the run's
   ticket limit (**Limits**).

**Closes do not use the ticket limit; they have their own cap (**Limits**).**
That cap is what keeps one pass from sweeping the board
on thin evidence. A ticket that would have been closed past the cap is left for the next pass and named in the report as such.

**Commits that name the key are not a skip, but check whose they are.** When
`git fetch origin` then `git log --remotes --not origin/develop --grep "<key>"`
finds commits, look at each branch that holds them. If it is **this org's own
released work** it is continued rather than started over (next paragraph). If
it is anybody else's and no open PR's title names the key, the ticket is still
eligible: say in the claim comment which branch already mentions it.

**This org's own released work** is a branch with no one working on it. It is
the org's only if **both** hold:

1. its name starts `bugfix/pc-` and the author of every commit on it that is not
   on `develop` is the App's bot:
   `git log --format='%an <%ae>' origin/develop..origin/<branch> | sort -u`
   prints only `orbweaver-bugfix[bot]`;
2. no PR from it is open: `gh pr list --repo Uplift-Foundation/Orbweaver --state open --head <branch>`
   prints nothing.

Then the engineer continues from that branch instead of starting over: in the
worktree, `git fetch origin`, `git checkout -B <branch> origin/<branch>`,
rebase once onto `origin/develop`, and carry on (a closed PR can be reopened
with `gh pr reopen <number>`, or open a new one from the branch with the same
`gh pr create` as always). Keep the branch name; never rename a pushed branch.
The claim comment names that branch and says `continuing released work`. The
work is yours to check: read what the branch changed before building on it.

**The pass report ends with counts.** The last lines of the lead's summary are:

```
Fixed: <n> (<keys and PRs merged>)
Closed: <n> (<keys, one reason each>)
Waiting on triage: <n> (<keys>)
Taken, in progress: <n> (<keys and PRs open>)
Waiting on another team's PR: <n> (<keys and PR numbers>)
Left untouched: <n> (<keys, and why>)
Mobile access not live yet: <n> (<keys>)
Needs a protected workflow line: <n> (<keys, and the line each needs>)
Needs an input the org cannot make: <n> (<keys, and the input each needs>)
Open outside Refinement: <n> (<each key: moved to Done or Refinement, or left, and why>)
```

Every ticket either query returned is in exactly one line. **Left untouched** is
for a ticket that got no outcome: the ticket limit was full, the
close cap was reached, or something failed. Each one there is a
defect of the pass and says why; a pass whose `Left untouched` grows pass after
pass for the same reason is reporting a problem with these rules, not a quiet
board.

## Bugs outside Refinement

**A Bug nobody is working does not stay in Development or Testing.** On
2026-11-27 every pass from 10:00Z to 22:00Z reported an empty board while four
Bugs sat unworked in Development. One of them had been merged and never moved,
and one had been claimed by this org and lost when a pass was stopped (CB-493).
The Refinement query cannot see any of them. So every pass, after step 0 and
before taking anything, also runs:

```
project = CB AND issuetype = Bug AND status in (Development, Testing) ORDER BY updated ASC
```

Each ticket it returns gets exactly one of these outcomes, decided in this
order; the first that applies is the one. This step runs before the pass
claims anything (step 2), so a claim made in this pass is never read here.

1. **Settled today.** A comment starting `[bugfix-org] settled:` was posted on
   it in the last 24 hours. No Jira write; counted on `Open outside
   Refinement` as `settled already today`. No ticket is moved twice in a day.
2. **The org is working it.** It has a live Paperclip task, or an open org PR.
   A live task is one `search_tasks` finds with `query` `Ticket: <key>` and
   `statuses` `["backlog", "todo", "in_progress", "in_review", "blocked"]`,
   whose description's first line is exactly `Ticket: <key>` (the search is
   fuzzy and also returns pass tasks that only mention the key), **and** that
   had a run or an update in the last 3 hours (its `updatedAt`, or its newest
   run). A non-terminal task untouched for longer is left over from a stopped
   pass, not live: it never protects its ticket, and the ticket falls through
   to the outcomes below (outcome 6 settles that task). Without this bound,
   one leftover task would hold its ticket for ever. An open org
   PR is one the step-0 listing returned, in either repository, whose title
   names the key. No Jira write; counted on `Taken, in progress`. Step 0
   settles the PR.
3. **Merged.** A PR whose **title** names the key is merged into `develop`
   (`gh pr list --repo <repository> --state merged --base develop --search "<key> in:title" --json number,title,mergedAt,mergeCommit`,
   run for both `Uplift-Foundation/Orbweaver` and
   `Uplift-Foundation/Orbweaver-Mobile`, with the key literally in the
   returned title), it merged more than an hour ago, no open PR's title
   names the key in either repository, and nothing is newer than that merge
   but the org's own: no comment after `mergedAt` that does not start
   `[bugfix-org]`, and no status change after it
   (`key = <key> AND status CHANGED AFTER -<m>m` returns nothing, `<m>` the
   minutes since `mergedAt`). A person who moved the ticket back because the
   fix did not hold has done one of those, so the old PR never closes it again. Move it to Done (`41`) with one
   comment: `[bugfix-org] settled: PR #<n> merged to develop at <merge sha>;
   nothing else open names this ticket`. The hour leaves a person who just
   merged time to move their own ticket, and the open-PR check keeps a ticket
   with a second PR still in flight open. This is the one move that recent
   activity does not stop (outcome 4): a merged PR is the evidence.
4. **Somebody else is on it.** Anyone other than the org commented on it or
   moved it in the last 72 hours. Two checks, because the org's comments and
   moves are posted under a person's account, so the author cannot tell them
   apart:
   - a comment in the last 72 hours that does not start `[bugfix-org]`;
   - a status change in the last 72 hours that is not the org's own. Every
     org move has a `[bugfix-org]` comment beside it, so the change is the
     org's only when `key = <key> AND status CHANGED AFTER -<m>m` returns
     nothing, where `<m>` is the minutes since ten minutes after the org's
     newest `[bugfix-org]` comment. With no org comment in the 72 hours, any
     change in them (`key = <key> AND status CHANGED AFTER -72h` returns the
     ticket) is somebody else's.
   No Jira write; counted on `Open outside Refinement` with the date of that
   activity. Outcomes 5 to 7 move a ticket only after this check.
5. **Another team's PR.** An open PR's title names the key, in either
   repository, and it is not the org's, by the same check as **Wait: another
   team's PR** in **Every ticket gets an outcome**. Read its last movement
   with `gh pr view <n> --repo <repository> --json updatedAt,headRefOid`:
   `updatedAt` moves on a push, a review or a comment.
   - It moved in the last 72 hours: a wait. No Jira write; counted on
     `Waiting on another team's PR` with the PR number.
   - It has not: the PR no longer holds the ticket. Put it back in Refinement
     (`11`) with one comment:
     `[bugfix-org] settled: PR #<n> has not moved for 72 hours (head <sha>); back to Refinement so it is worked, and the org may build on that PR's commits`.
     The take-rule's wait has the same limit, so the next pass takes it.
6. **The org's lost claim.** The newest comment on the ticket is this org's
   `[bugfix-org] claimed`, it is more than an hour old, and outcome 2 found no
   live task and no open org PR. Count `<n>`, this loss's number: one more
   than the comments on the ticket containing `[bugfix-org] settled: lost
   claim`. Find the claim's engineer issue: the newest `search_tasks` result
   for `Ticket: <key>` in any status whose first line is exactly that.
   - `<n>` is 1 or 2: losing a claim to a pause or a stopped pass is not
     giving up. Put it back in Refinement (`11`) with one comment:
     `[bugfix-org] settled: lost claim <n>; the org claimed this and lost it, no live task, no open PR; back to Refinement, branch <name> kept`.
     When the engineer issue is not yet `done` or `cancelled`, PATCH it to
     `{"status": "done", "comment": "[bugfix-org] lost claim <n>"}`. When it
     is already settled, leave it. It is never set `cancelled` here.
   - `<n>` is 3: the org keeps dropping this ticket, and that is a release.
     Release it as **Claiming and releasing** says (the `[bugfix-org]
     released` comment naming `lost claim 3`, back to Refinement), and PATCH
     the engineer issue to `{"status": "cancelled"}`, whatever its status
     unless it is already `cancelled`, so the release brake counts it.
7. **Untouched for 72 hours.** It has not changed status and has not been
   updated in 72 hours. Check that with the query
   `key = <key> AND NOT status CHANGED AFTER -72h AND updated <= -72h`: an
   empty result means it moved. Put it back in Refinement (`11`) with one
   comment:
   `[bugfix-org] settled: in <status> since <date> with no open PR and no change for 72 hours; back to Refinement so it is worked`.
8. **Moved recently.** Everything else: only the org itself moved or
   commented on it in the last 72 hours. No Jira write; counted on `Open
   outside Refinement` with the date it last moved.

**The guards, and why each exists.** Read the issue type with `getJiraIssue`
just before every move, and refuse it unless it is `Bug` (**Transitions: Bug
only**). Never move a ticket with a live task or an open org PR (outcome 2
comes before any move). A person who moved a ticket to Development minutes
ago, or commented on it, is protected by outcome 4 for 72 hours, and an open
PR of theirs that moved in that time holds it after that (outcome 5). The
settle comments start `[bugfix-org] settled:`, never `[bugfix-org] stopped:`,
because a `stopped:` comment is a put-back that holds the ticket as a wait
(**Wait: put back**), and a settled ticket must be taken by the next pass. A
ticket moved to Refinement here is not taken in the same pass.

Moves here do not use the ticket limit or the close cap; they have their own
cap (**Limits**). A ticket past the cap is left for the next pass and named on
`Open outside Refinement` as such. **The pass never reports an empty board
while this query returns a ticket**: it names each one and its outcome.

## What this org fixes

The class comes from the summary and the triage comment, and you re-classify
when you read the code.

1. **Flaky tests in this repository.** Proof: the test class run 30 times on
   both rids on GitHub's runners. You cannot dispatch that run; the warden
   does, when you ask through its spool. Once your PR is open, write one file,
   `$BUGFIX_SPOOL/stress-<PR number>.json`, holding exactly
   `{"verb": "dispatch_stress", "pr": <PR number>, "test_class": "<Namespace.Class>", "n": 30, "suite": "unit"}`
   (`suite` is `unit`, `integration` or `ui`, whichever holds the class). The
   warden reads the spool every five minutes, checks the PR is the org's own and
   open, and dispatches `stress.yml` on your branch. Find the run with
   `gh run list --repo Uplift-Foundation/Orbweaver --workflow stress.yml --branch <your branch>`
   and name it, with its result on both rids, in the PR body. A request the
   warden refuses never runs, so a run that has not appeared ten minutes later
   is a failed round, not a reason to wait.
2. **Logic bugs with a testable seam.** Proof: a test that fails before the fix
   and passes after, plus CI green on both rids.
3. **Bugs whose proof needs a real machine** (an install, an observation on the
   running app, a timing on a real Windows PC). These are yours now, through the
   machines runner: see **Real machines** below. Proof: the test or observation
   that shows the bug, then the same one on the fixed build installed on the
   machines it concerns, plus CI green on both rids.

4. **Bugs that need a real captured prompt** (a permission dialog, a pane, a
   transcript or rollout the parser has to read and no fixture holds). These
   are taken, not skipped: the org has real machines (**Real machines**), and
   the capture is made there. The order:
   - the engineer opens the PR first, as a draft
     (`gh pr create ... --draft`), with the test that will read the capture and
     a note in the body of exactly what has to be captured; the runner only
     serves an open org PR, so the PR has to exist before any capture;
   - the lead hands QA a **capture task** with the PR number, the ticket key
     and what to capture. QA sends `machine_install` for the machines the bug
     concerns, then `machine_run` commands that produce the prompt and print it
     (for example start the CLI in a detached `tmux` session, wait, then
     `tmux capture-pane -p`), reads `stdout.txt`, and always sends
     `machine_release`. QA's answer names the result folder holding the
     capture;
   - the engineer commits that capture as the fixture, unedited, shows the test
     failing on it before the fix and passing after, and the PR leaves draft.
   Proof: the fixture as captured, the test failing then passing, plus CI green
   on both rids. **If the capture cannot be made as first planned**, the ticket
   still gets an outcome, and "it needs a person" is never one. When the
   prompt seems to need a person (a sign-in, an open panel, somebody at a
   screen), get it the way **A proof that seems to need a person** says. When
   the attempts ran and the prompt did not appear, close the ticket as
   cannot-reproduce (**Close** in **Every ticket gets an outcome**). Either
   way the comment says what was run, on which machine, and what came back.

**Security bugs are taken like any other bug**, with one added rule: the PR
body, the commit messages and every Jira comment describe the class of flaw and
the fix, never a working exploit (no payload, no step-by-step to trigger it).
The test that proves the fix may exercise the flaw; the prose around it does
not explain how to use it.

## Two repositories, one code base

The org fixes bugs in two repositories and treats them as one code base, laid
out the way a person here has them: Orbweaver-Mobile is cloned at `mobile/`
inside the Orbweaver checkout. Paperclip gives each ticket its own Orbweaver
worktree; a ticket that needs the mobile repository adds the clone there. The
process (claim, branch, PR, QA, merge, limits) is the one this skill already
describes. Only the rows of this table differ:

| | Orbweaver | Orbweaver-Mobile (`mobile/`) |
| --- | --- | --- |
| Repository | `Uplift-Foundation/Orbweaver` | `Uplift-Foundation/Orbweaver-Mobile` |
| Branch from, PR target | `origin/develop`, `--base develop` | `origin/develop`, `--base develop` (`main` takes only `release/` PRs) |
| PR command | `gh pr create --repo Uplift-Foundation/Orbweaver ...` | inside `mobile/`: `gh pr create --repo Uplift-Foundation/Orbweaver-Mobile ...` |
| Merge gate at the head sha | `build (macos-latest, osx-arm64)` and `build (windows-latest, win-x64)` green | `relay`, `contracts` and `tools` green always; `android` green when the diff touches `android/` or `contracts/`; `snapshots` green when it touches `app/`, `contracts/frames-v7-vectors.json` or `contracts/frames-v11-vectors.json`; no check pending or red |
| QA comment names | both rids, `osx-arm64` and `win-x64` | each check it read, by name |
| Tests, targeted only | `ow-remote <run> <class>` on the MacBook Air, never on the mini (**Building and testing: the remote runner**) | **untried under the sandbox:** the contract checkers (`python3 -I contracts/check_*.py contracts/`), `app/Tools/test-*.py`, `./gradlew :crypto:test :relay:test --tests <class>`, `swift test --filter <class>` in `app/`; one that fails to run is named in `## Not verified` and is **not** a failed round, and CI is the gate. The relay suites (Docker), Android Robolectric and every simulator test are CI's |
| Flaky-test proof | `stress.yml`, 30 runs on both rids (class 1) | none exists: the fix removes the race by construction, CI is green, and the PR says the failure rate was not measured |
| Machines runner | yes (**Real machines**) | no: it serves Orbweaver PRs only |
| Workflow files | allowed, under **Workflow files** | never |
| Always in `## Not verified` | Windows behaviour not reproduced; any uncovered added line | no real phone or Watch; APNs delivery; Watch and UI simulator tests; no TestFlight build; the relay not deployed |
| `Installed on:` | the machines used, or none and why | `none (mobile repository)` |

**The clone.** In the engineer's worktree, with the usual prefix
(**GitHub access**):

```
git clone https://github.com/Uplift-Foundation/Orbweaver-Mobile.git mobile
git -C mobile checkout -b <the worktree's own branch name> origin/develop
```

The same branch name in both repositories, `bugfix/pc-<change>`, so the claim
comment names one branch. The clone is fresh per ticket; `git -C mobile fetch
origin` before a rebase is all the refreshing it needs. `/mobile/` is in
Orbweaver's `.gitignore`, so a commit in the worktree never picks up the nested
clone; commit mobile changes with `git -C mobile`. Every command in `mobile/`
uses the everyday token, never `$BUGFIX_GH_WORKFLOW_TOKEN`. QA and the lead clone the
same way when they need to read it.

**Check that mobile is reachable before relying on it.** The org's GitHub
access to the mobile repository may be newer than these instructions on a
given day. A clone proves nothing: it can succeed through a credential helper
the org's token has no part in. Before claiming a mobile ticket the lead runs, with the prefix,
`gh api /installation/repositories --jq '.repositories[].full_name'`, which
answers only for the org's own token and lists exactly the repositories it
reaches. If `Uplift-Foundation/Orbweaver-Mobile` is not in that list, the
ticket is not claimed and is counted on the report's `Mobile access not live
yet` line, with no Jira write. After a claim the signal is a **refused push**
to the mobile branch (not a failed clone): the ticket is put back (**Putting a
ticket back**) with the sentence `[bugfix-org] stopped: mobile access not live
yet; nothing merged; branch kept at <sha>`.

**A fix in both repositories is one ticket with two PRs**, one in each, on the
same branch name, each gated by its own row. QA reviews both and posts its
comment on each. The ticket moves to Testing when the first PR opens and to
Done only when both are merged; the Done comment names both merge shas. Merge
the one the other depends on first (usually mobile's contracts, then the
desktop reader); if the second cannot merge, the first stays merged and the
ticket is released with that said.

**Never on mobile:** edit `.github/workflows/`, bump
`CURRENT_PROJECT_VERSION`, upload to TestFlight, deploy the relay, or push to
`main` or `develop`.

## Workflow files

A bug whose fix is a change under `.github/workflows/` in **Orbweaver** is the
org's. On mobile it never is (table above).

- **The smallest diff, in one workflow file.** Never `release.yml` or
  `publish-screenshots.yml`, and never an added or removed line about an
  environment, a secret, permissions, a trigger (`on:`, `pull_request_target`,
  `workflow_run`) or a runner (`runs-on`). The warden reads every org PR's
  workflow diff and pauses the company on any of those, so a fix that needs
  one cannot land through the org.
- **A fix that needs one of those lines is put back** (**Putting a ticket
  back**) with the sentence `[bugfix-org] stopped: needs a protected workflow
  line (<the line it needs, e.g. "permissions: contents: write" in ci.yml>); nothing
  merged; branch kept at <sha>`, and counted on the report's `Needs a protected
  workflow line` line. It is not released and not closed: the bug is real and
  a person makes that change.
- **Push with the workflow token.** GitHub refuses a push that touches a
  workflow file unless the token carries the workflows permission, and only the
  engineers have one, `$BUGFIX_GH_WORKFLOW_TOKEN` (Orbweaver only). For that
  branch, begin the push command with `export GH_TOKEN="$BUGFIX_GH_WORKFLOW_TOKEN";`
  instead of the everyday prefix's first part. Everything else uses the everyday
  token, and the workflow token is **never used inside `mobile/` or for any
  mobile push**.
- **The proof is the PR's own CI run** showing the changed step doing what the
  fix says, named by run id in `## Verified`. A workflow PR never asks for a
  machine install (the runner refuses one). A path that runs only on a push to
  `develop` or `main`, or on a tag, cannot be seen before merge; name it in
  `## Not verified`.
- **QA adds one line** to its review comment on a workflow PR:
  `Workflow change: <files>; no environment, secret, permission, trigger or runner line`.
  The lead does not merge a PR that touches `.github/workflows/` without that
  line at the reviewed sha.

## Putting a ticket back

Some stops are not the org failing, so they are not releases. Putting a ticket
back means: close any open PR the ticket has (draft or not), move the ticket to
Refinement with the one comment the rule gives (it always starts
`[bugfix-org] stopped:`, which is how a later pass finds it, **Wait: put
back**), and set the engineer's
Paperclip issue to `done` with that same sentence as its `comment`, never
`cancelled` (`cancelled` is a release, and the warden counts releases toward a
pause). The branch is kept. The rules that put a ticket back are in **Two
repositories, one code base**, **Workflow files** and **A proof that seems to
need a person**.

## Real machines

Three real machines exist: `windows` (the Windows PC), `mini` (the Mac mini this
org runs on) and `macbook`. You cannot install on them or run anything on them.
A runner outside your sandbox does it for you when you ask by writing a file.
You never hold a key, never run `ssh`, and never touch `/tmp/ow-install.lock`;
the runner takes it.

**When to ask:** only when the proof needs it, after the PR is open and CI has
built that sha (the Windows build is CI's `unsigned-win-x64` artifact for the
PR's head sha; the Mac build is made and signed by the runner, which takes
longer). Ask for the machines the bug concerns, not all three by habit.

**One PR holds the machines at a time, for at most 45 minutes.** While you hold
them the three machines run your PR's build, not the one a person installed, so
be quick and always finish with `machine_release`, which puts back exactly what
was installed before. If you forget, the runner restores and releases at 45
minutes by itself, but a person was without their build for that long.

**How to ask.** The spool folder is `machine-spool` in the same company folder as
`$BUGFIX_SPOOL`; the results are in `machine-results` beside it:

```
SPOOL="$(dirname "$BUGFIX_SPOOL")/machine-spool"
RESULTS="$(dirname "$BUGFIX_SPOOL")/machine-results"
```

Write each request as one file named `<id>.json`, where `<id>` is the request's
own `id` and is yours to choose: 1 to 40 characters of `a-z`, `0-9` and `-`,
never reused (for example `pr412-install`, `pr412-run1`, `pr412-release`). Write it under another name and
rename it into place so the runner never reads half a file:
`printf '%s' '<json>' > "$SPOOL/<id>.json.tmp" && mv "$SPOOL/<id>.json.tmp" "$SPOOL/<id>.json"`.
The `pr` is the PR number, an integer. Fields other than the ones shown, an unknown
verb and an unknown machine are all refused.

1. Install the PR's build, on one or more of `windows`, `mini`, `macbook`.
   `sha` must be the PR's current head (`gh pr view <n> --json headRefOid`):

```
{"verb": "machine_install", "id": "pr412-install", "pr": 412, "sha": "<full 40-character head sha>", "machines": ["windows", "mini"]}
```

2. Run a command on one machine, only after step 1 succeeded for it. `command` is
   up to 4000 characters, `timeout_s` up to 600:

```
{"verb": "machine_run", "id": "pr412-run1", "pr": 412, "machine": "mini", "command": "<one shell command>", "timeout_s": 120}
```

3. Release, always, whether the proof worked or not:

```
{"verb": "machine_release", "id": "pr412-release", "pr": 412}
```

**Reading the result.** The runner looks at the spool about once a minute and
answers requests in the order they arrived. When it has dealt with one,
`$RESULTS/<id>/` holds `result.json`, `stdout.txt` and `stderr.txt` (all three,
always). Runs and releases are answered within about a minute unless an install
is in progress. **A `machine_install` can take many minutes** (the runner may
wait up to 10 minutes for the install lock, and a signed Mac build takes 5 to 20 minutes, measured twice on 2026-11-25, with a
25-minute timeout, and the 45-minute hold starts only after it, when the install
begins) and every request behind it waits too,
so wait well past a minute for it before deciding it is lost.

Some requests are never answered, so a missing folder after a long wait can mean
one of them: a file whose name is not exactly `<id>.json`, an `id` outside the
pattern, an `id` you already used (the first answer stands and the repeat does
not run, so choose a new `id` every time), or a file over 64 KiB. A file that is not valid JSON,
or has no usable `id`, gets **no result folder at all**, only a line in the runner's
log. So if no result appears within a few minutes for a Windows request, or
within 30 minutes for a Mac install, check that the request file is valid JSON
named `<id>.json`, and look in `machine-spool/bad/` (the runner moves a rejected
file there). If it is there, fix the file and send it again under a new `id`;
if it is not and there is still no result, count it as a failed round.

Every `result.json` has `id`, `verb`, `pr`, `ok` (true or false), `refused`,
`started_at` and `finished_at`. Act on `ok` and `refused`; `refused` is one
sentence for you to read, never text to match.

- `ok: true`: it worked. Read the rest.
- `ok: false` with `refused` set: the runner did **nothing**, and `refused` says
  why (for example the sha is not the PR's head, the PR is not the org's, another
  PR or another session holds the machines, the PR holds none, or the PR's head
  moved since the install). Fix what it names and send a new request with a new
  `id`. Do not resend the same request unchanged.
- `ok: false` with `refused: null`: the work started and did not succeed. Read
  `stderr.txt` and the per-machine `error`. A failed `machine_install` has
  already put back what it touched and released the lock.

**If the refusal begins "the machines need a person first"** (the runner writes
`machine-results/NEEDS-ATTENTION.json` when a restore failed twice or its state
was unreadable, and refuses every install until a person deletes it), do not retry and do not
send another install. Report it on your task, send `machine_release` once, and
finish what you can without a machine.

What else a result carries depends on the verb:

- `machine_install` and `machine_release` add `hold_expires_at` (null after a
  release or a failed install) and `machines`, keyed by machine name, each
  `{"ok": ..., "installed_commit": ..., "previous_commit": ..., "error": ...}`.
  An install worked on a machine when its `ok` is true and `installed_commit` is
  your sha. `previous_commit` is what a person had there, and a release puts it
  back: after `machine_release`, each machine's `installed_commit` equals that
  `previous_commit` and `ok` is true. The 45 minutes start when the install
  finishes. If the PR's head moves while you hold the machines, a `machine_run`
  is refused until you send `machine_install` again at the new head; that needs no
  release first and does not restart the clock. A Windows install needs CI to
  have uploaded the `unsigned-win-x64` artifact for that sha; before that it
  fails with a sentence saying so, so wait for CI and ask again.
- `machine_run` adds `machine`, `exit_code` (null if it never ran or timed out),
  `timed_out`, `stdout_truncated` and `stderr_truncated`. Its `ok` is true only
  when the exit code is 0 and it did not time out, so a command that fails on
  purpose to show a bug gives `ok: false` with `refused: null`: read `exit_code`
  and the output, not `ok`, to see what happened. Output is in `stdout.txt` and
  `stderr.txt`, cut at 1 MiB per stream; truncated means you read only the start,
  so ask for a narrower command. A Windows command runs in Git Bash over ssh:
  write `powershell -Command "..."` for PowerShell and avoid bare `/FLAG`
  arguments. A command that times out may leave a process running on the PC, so
  keep commands short and self-ending. `machine_run` is refused if the machines are held by a different PR.
- A release works even after the PR merged or closed. If the 45 minutes had
  already passed, the runner restored by itself, and the release answers
  `ok: true` and says so in `stdout.txt`.

**If a request is refused or fails and you cannot finish your proof**, send
`machine_release` anyway and say in the PR body what you could not confirm.
When the proof was the point of the ticket, do not release it for that: get the
proof the next way **A proof that seems to need a person** gives, and settle
the ticket as that section says. Never work around a refusal
(another path, another machine, an edited request): the refusals are the rules,
and every request and result is logged for the lead.

**A `machine_run` on `macbook` that would type or click is refused.** A person
works at the MacBook, so the runner refuses a command there that names a tool
for sending keyboard or mouse input (`cliclick`, `shortcuts run`, System Events
and the like). **Every `osascript` on `macbook` is refused**, read-only ones
too, and so is any command that merely contains one of those words (a `grep`
for "keystroke", say): read what you need another way (`pgrep`, `defaults
read`, a log file). `mini` is headless and has no such refusal. On `windows` a command
runs over ssh with no desktop; a program that needs one is started through a
scheduled task, never by sending keys.

## A proof that seems to need a person

**No ticket is released because its last proof needs a person at a machine.**
Nobody sits at the machines, so a ticket released for that reason comes back
unchanged on the next pass. On 2026-11-27 CB-373 was released twice and CB-359
three times this way. Each time the next claim found a way that needed nobody:
both ends of the peer exchange in one test, and a sign-in against a server the
org runs itself. Take that way first.

**Name the observation.** In the draft PR the engineer writes one sentence:
what has to be seen, and where, for the bug to count as shown or fixed. For
CB-373 that was "a peer that asks `PROMPT` for a session waiting on
AskUserQuestion gets the question back, not `no-prompt`". The proof is that
observation, not the steps a person would take to cause it. "Open the panel on
the MacBook" is a step. "The peer asks" is the observation, and a test can ask.

**Get the observation by the first of these that can give it:**

1. **The real CLI and the installed app on `mini`.** Anything a `machine_run`
   can start there is allowed: a detached `tmux` session, hooks that log their
   payloads, the mirror log, the status files, and tools under `tools/` run
   with their own `ORBWEAVER_STATUS_ROOT` and `ORBWEAVER_SETTINGS_DIR` so they
   never touch the installed app's files. (The app itself runs once per user;
   a second copy started by hand exits at once, so do not plan a proof around
   one.) **A service the bug needs is one the org starts itself.** A sign-in is
   a server answering a browser: run a throwaway server on `127.0.0.1` whose
   approve step answers by itself, fetch the sign-in link with `curl`, and
   hand the callback to the session with `tmux send-keys`. That uses no real
   account, no third party and no person. Bring the dialog up and **leave it
   pending**: an approval is captured, never answered.
   **The capture leaves nothing behind in a live account.** The session runs
   on a real Claude account, so: give it a scratch `CLAUDE_CONFIG_DIR` when it
   can run with one, and when it cannot (a scratch folder holds no login), the
   server reaches the session only through an `--mcp-config` file in the
   scratch folder with `--strict-mcp-config`, never `claude mcp add`; the
   server's token step always refuses, so no credential can be stored even if
   an approval were answered by mistake; the server binds `127.0.0.1` on a
   port the system picks (bind port 0 and print it), not a fixed one; and the
   last command of the capture stops the server and the `tmux` session and
   says so in its output. The session uses the machine's own default login,
   never a pool account's folder. **Check it, do not assume it:** give the
   server a name no other server has (`cb<ticket number>cap`), and end the
   last command with
   `f=$(grep -l "cb<ticket number>cap" ~/.claude*/*.json ~/.claude*/.*.json ~/.claude.json 2>/dev/null); echo "left behind: ${f:-none}"`.
   QA quotes that line in her answer. A file name there instead of `left
   behind: none` is a failed capture: report it to the lead and do not edit
   the file.
2. **Both ends in one test.** When the observation is an exchange (two
   machines, or the app and a panel), connect the real classes in one process
   (the host's mirror server and the peer's chat session; a panel over
   `FakeChatSession`) and give the host side the **unedited capture from
   step 1**. The test asserts the observation and has a negative control
   beside it (the same exchange with the dialog absent gives no prompt).

**Never, to get a proof:** keyboard or mouse input on `macbook`; a sign-in to
a real account; an answered approval; an edited capture (scrubbing a home
path, a host name, a user name or a secret is the only edit, and the PR says
which); a fixture written from memory.

**When the exact condition still cannot be made**, the ticket is settled on
what was run, by the first of these that fits. The exact condition is the one
input the org has no way to produce: a real third party's sign-in, an approval
card that exists only inside another app's window, a person's own screen.

- **There is a fix.** It merges when all of these hold: a test reads an
  unedited real capture, fails before the fix and passes after; a negative
  control sits beside it; the merge gate is green; and `## Not verified` names
  the condition not exercised in one sentence a reporter could act on. QA's
  comment carries the line
  `Proof without a person: <step 1 or 2, and what it showed>; not exercised: <the condition>`,
  and the lead does not merge without that line at the reviewed sha.
- **There is no defect to fix.** Every stage the reported symptom passes
  through was run on real output and behaved, **and the condition not
  exercised would take the same code**. That second half is shown, not
  assumed: QA's line and the close comment name the code that would handle
  the condition, as `file:line`, and say why it is the path the capture
  already ran (the same hook matcher, the same parser arm, the same request).
  The tests and captures merge as
  a tests-only PR (the same gate, the same QA line), so the stages stay pinned.
  Then the ticket is closed as cannot-reproduce. The close comment says what
  was run, on which machine, at which sha; names the condition not exercised;
  and lists what a reporter should attach to reopen it (the pane capture, the
  hook payloads, the status file).
- **The report came from a host the org cannot produce** (for example a
  session hosted in another app, with no pane to read). It is closed the same
  way, and the close comment **quotes the ticket's own words that show the
  host**, exactly as written, so the reporter can dispute the reading. A
  guess about the host with no words from the ticket under it is not a reason
  to close.

- **Some code is reached only by the condition not exercised** (a hook
  matcher, a parser arm or a request type that nothing the org ran went
  through). Then nobody has seen that code work, and the ticket is **not
  closed**: "no defect found" would be a claim about code that never ran. The
  tests and captures still merge as a tests-only PR. Then the ticket is put
  back (**Putting a ticket back**) with the sentence
  `[bugfix-org] stopped: needs an input the org cannot make (<the input, and
  the file:line only it reaches>); tests merged at <merge sha>; branch kept at
  <sha>`, and counted
  on the report's `Needs an input the org cannot make` line. That is not a
  release and not a close.

**Neither close is a release.** The engineer's Paperclip issue ends `done`,
never `cancelled` (**Claiming and releasing**). The host counts only
`cancelled` issues toward a pause, and a merged tests-only PR counts as the
merge it is.

**Why this is enough.** A release leaves the same gap and an open ticket the
next pass repeats. This leaves the gap named where a person will read it, the
stages around it held by tests on real output, and one clear way back in. It is
not "merge unverified": nothing merges without a test that failed before the
change and a real capture under it.

## Claiming and releasing

A ticket in Development with a `[bugfix-org] claimed` comment is the org's, and
human-led teams skip it by the same rule. Claim in this order: transition to
Development, comment `[bugfix-org] claimed` with the branch name, read the
ticket back. If another comment or transition landed in between, release it.

Release means: close the PR if there is one, make sure everything worth keeping
is **pushed** on the engineer's branch (never delete the branch), comment
`[bugfix-org] released` with what you learned, why you stopped and **the branch
name with its last commit sha** (that is what makes "a later pass can pick this
up" true: the next pass finds the branch by the rule in **Which tickets**), and
move the ticket back to Refinement.
The engineer's Paperclip issue is titled with the change words, never the CB
key (its slug becomes the branch name), and its description starts with the line
`Ticket: CB-NNN`, which is how the host matches the issue to the ticket.

Settling a ticket also leaves one fact in Paperclip, which the host reads in
place of anything an agent says: the engineer's Paperclip issue ends `done` when
the ticket left the org with an outcome (the PR merged to `develop`, the
ticket was closed after a claim, or an outcome in **Every ticket gets an
outcome** says so), and `cancelled` only when the ticket was released. The
Jira comment is the human-readable record; the issue status is the machine one.
The warden also cancels a child task that sat with no run through two of its
wake comments, and says so on your task; that is not a release, so treat it as
a failed round. And it closes a child `done` that was pinned to a Claude
account over its usage limit, saying so on your task; the lead re-creates it
with the same title and description (CB-469), which is not a failed round. It closes your task itself when you have reported and only
settled children are left blocking it.

## Transitions: Bug only

Only the lead transitions or comments on Jira, and **only on a ticket whose
issue type is `Bug`**, read from the ticket just before the transition. The
connector cannot check this; the rule is the control.

| Moment | Move | Comment |
| --- | --- | --- |
| claimed | Refinement to Development | `[bugfix-org] claimed`, the branch name |
| PR open | Development to Testing | the PR link and what the fix is |
| merged to `develop` | Testing to Done | the merge sha, what was verified and what was not |
| gave up | back to Refinement | `[bugfix-org] released`, what it learned, why it stopped |
| closed | Refinement, or a ticket the org claimed, to Done (`41`); no claim needed | `[bugfix-org] closed:`, the reason and the evidence (**Every ticket gets an outcome**, **Close**) |
| settled | Development or Testing to Done (`41`) or Refinement (`11`); no claim needed | `[bugfix-org] settled:`, the sentence its outcome gives (**Bugs outside Refinement**) |

## GitHub access

Your GitHub identity is the `orbweaver-bugfix` App, and its token is in
`$BUGFIX_GH_TOKEN`. Paperclip blanks `GH_TOKEN` and `GITHUB_TOKEN` in your
environment, and every shell command starts fresh. So begin **every** command
that runs `gh` or `git`, including `git commit`, with:

```
export GH_TOKEN="$BUGFIX_GH_TOKEN"; export GIT_CONFIG_PARAMETERS="'credential.helper=' 'credential.https://github.com.helper=!gh auth git-credential' 'user.name=orbweaver-bugfix[bot]' 'user.email=360784342+orbweaver-bugfix[bot]@users.noreply.github.com'"; <your command>
```

That makes `gh` and `git` use the App and author commits as its bot. Without
it on a `git commit`, the commit is authored as the Mac mini's own user, which
credits the org's work to a person. Never print
the token, never write it to a file, and never use any other credential. It
reaches both repositories, Orbweaver and Orbweaver-Mobile, and never carries
the workflows permission; the engineers' second token, `$BUGFIX_GH_WORKFLOW_TOKEN`,
is used only as **Workflow files** says.

`$BUGFIX_GH_TOKEN` is a copy taken when your run started, and a token lives
one hour, so it can be dead by the time you use it. The `gh` and `git` on
your `PATH` handle that: at every call they swap that copy (or an empty
`GH_TOKEN`) for the token the warden minted last, and retry a 401 once with a
newer one. `$BUGFIX_GH_WORKFLOW_TOKEN` gets no such refresh: it lasts at least
45 minutes from the start of your run, and a workflow push that gets a 401
is not retried. Finish the task, say so, and ask for another round. With the everyday token, **on a 401, run the same command once more** before you decide
anything. Only a second 401 means access is really broken: then read
`checked_at` in `$BUGFIX_SPOOL/access.json`. More than 15 minutes old means the
warden has stopped minting; say that, with the time, rather than that the
token is wrong, and mark the task blocked. A push the rulesets refuse
(GH013) means you left your lane, which is `bugfix/pc-*` branches and PRs into
`develop`, in either repository. Stop there, and say so in your answer.

## Long commands

A shell command gets ten minutes; a longer one is moved to the background, and
a run that ends its turn without reporting is recorded as failed ("no semantic
result"). This happened on 2026-11-24. So:

- Keep every command inside ten minutes. Run tests per class with the filter,
  not a whole suite. Split a long build-and-test into separate commands.
- Never end your turn while waiting on a background job. Either wait in the
  foreground, or report progress and finish the task, saying what is still
  running. The next wake picks it up.
- Builds and tests go through `ow-remote` (**Building and testing: the remote
  runner**); one that is still running past nine minutes exits 75, and
  `ow-remote wait <id>` picks it up in the next command.

## Building and testing: the remote runner

**Builds and tests run on the MacBook Air, never on the mini**, with one
temporary exception below (**Integration and coverage**). The mini is the
always-on gateway host, and passes building and testing side by side on it
starved everything else it serves (CB-494). A runner on the mini takes your
request and runs it on the Air at the sha you name; nothing runs on the mini,
and when the Air cannot be reached the request is refused, not run locally.

The runner builds the commit you pushed, not your files, so the loop is:
commit, push, ask. `ow-remote` asks for `HEAD` and refuses, with a sentence,
when the tree has uncommitted or untracked changes or `HEAD` is not the tip of a
branch on `origin`:

```
ow-remote <run> [filter]
```

| `<run>` | runs | filter |
| --- | --- | --- |
| `build` | `tests/Tests.sln` in Release: the app, `UnitTests`, `IntegrationTests` and `UiTests`, no tests run (`screenshots` and the console suites compile their own) | none |
| `unit` | `tests/UnitTests` | a class: `FullyQualifiedName~<filter>` |
| `integration` | refused on the Air today; run it on the mini (**Integration and coverage**) | |
| `ui`, `screenshots` | `tests/UiTests`, `tests/UiScreenshots`, in Release | a `Namespace.Class`: `--filter-class` |
| `arrangement`, `glyph`, `transcript` | the console suites, for a change to geometry, transcript parsing or orb initials | none |
| `coverage` | refused on the Air today; run it on the mini (**Integration and coverage**) | |

Iterate with a filter: a filtered run answers in under a minute. No filter on
`unit`, `integration`, `ui` or `screenshots` runs the whole suite, which CI
runs anyway, so do not; a full `ui` run is not passed through CB-119's count
guard there, so it proves less than CI does. A filter is a class, letters,
digits, `_` and `.` only; there is no single-method filter, and a filter on
any other run is refused.

**Reading the answer.** `ow-remote` waits up to nine minutes, prints the last
200 KB of the output, and exits with the remote command's exit status. Its last
stderr line names the request id, the sha, the machine and the folder,
`remote-results/<id>/` beside `$BUGFIX_SPOOL`, which holds `result.json`, the
whole `output.txt`, and `files/`: `unit-tests.trx`, `integration-tests.trx`,
`ui-tests.xunit.xml`, `ui-screenshots.xunit.xml`, `coverage-summary.txt`, and a
screenshot run's PNGs under `files/screenshots/` (a line names any file the
runner dropped). Quote the request id in the PR body.

- Exit `0`: it passed, on your commit.
- Exit `1` (or any code not listed here): the build or a test failed, **or no
  test ran**. Read the summary line: `Total: 0` means the filter matched no
  class, so check the `Namespace.Class` name; it is not a pass. Measured on
  the Air (CB-494): a filter naming no class exits 1, not the 8 the UI suite
  returns on its own, because `dotnet test` wraps it. Never a refusal.
- Exit `64`, `refused, nothing ran: <sentence>`: read the sentence, fix what it
  names, ask again. "no target reachable; not run on the mini" means the Air
  was away for fifteen minutes. **Say so and wait**: report it on your task,
  check `ow-remote status` later and ask again once the Air is reachable. That
  wait is **not** a failed round. **Never run the build or test on the mini
  instead**: a refusal or an unreachable Air is never the exception below. The
  one refusal that does send you to the mini is "the target has no tmux, which
  IntegrationTests needs; not run", for `integration` or `coverage` only.
- Exit `75`, `still running: ow-remote wait <id>`: run exactly that; it waits
  another nine minutes. Each command stays inside the ten-minute limit
  (**Long commands**).
- Exit `124`: the run hit its cap (10 minutes filtered, 30 for a suite, 45 for
  coverage).
- Exit `65`, `VOID, not a pass or a fail: <sentence>`: it ran, but the result
  says nothing about your commit (another commit was checked out, or none was
  reported), or the runner said it did not succeed, or coverage did not merge
  4 reports. Push if needed and ask again; never count it either way.
- `ow-remote status` shows whether the Air is reachable and what is queued.
  One job runs at a time and the rest wait their turn; `target_busy_with` there
  names a job the Air is still finishing, which yours waits behind, and is not
  an error.

**Integration and coverage run on the mini, until the runner runs them on the
Air.** The runner does not run `integration` or `coverage` on the Air yet: it
refuses them with "the target has no tmux, which IntegrationTests needs; not
run", which is the runner's own check, not a fact about the Air. Until it
runs them, run those two on the mini, through `~/.paperclip/tools/bin/dotnet` (the first `dotnet` on your
`PATH`), which queues every heavy command one at a time, and never two at once:

```
dotnet test tests/IntegrationTests --filter "FullyQualifiedName~<Class>"
./tools/coverage.sh --base origin/develop
```

Coverage once per PR. Its first line is `merged <N> report(s)`; the figure is a
measurement only when N is 4. Quote that line with the figure, and say in
`## Verified` that coverage was taken on the mini. When the runner runs them
on the Air, the same `ow-remote integration` and `ow-remote coverage` requests
simply stop being refused, and this paragraph goes.

**Nothing else runs on the mini.** Any other local `dotnet test` or build there
is the exception, for something the runner cannot do, and `## Verified` says
which command ran locally and why.

## Branch, commits, PR

- Branch `bugfix/pc-<change>`, from `origin/develop`. Named for the change, not
  the ticket key. Never rename a pushed branch.
- Commit messages are prose: a short imperative summary, paragraphs on why, the
  key in the message, ending with
  `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Iterate with targeted tests only, through `ow-remote <run> <class>`
  (**Building and testing: the remote runner**); the total in the output's
  summary line shows whether the filter took. Add the matching console suite
  (`ow-remote arrangement`, `glyph` or `transcript`) for a change to geometry,
  transcript parsing or orb initials.
- Tests land in the same branch as the fix and cover every line it adds.
- One coverage run per PR, on the mini until the runner runs it on the Air
  (**Integration and coverage**); its `merged <N> report(s)` line must say 4, otherwise the
  number is not a measurement.
- Open the PR against `develop` at the first working push so CI runs while QA
  reviews: `gh pr create --repo Uplift-Foundation/Orbweaver --base develop --head <branch>`.
- Nobody reruns the full suites locally; CI green on both rids at one sha is
  the suite gate.

### The PR body

`tools/require-team-sdlc.sh` refuses a body that lacks any of these. Use exactly
this shape:

```
CB-<number>: <what was wrong, in one line>

## Summary
<what the fix is and why it is right>

## Verified
<each ow-remote request id with its run, filter and totals, and the coverage
figure with its merged-count line; a command run locally on the mini, and why>

## Not verified
<what an install on a real machine would have shown; Windows behaviour not
reproduced; any added line the tests cannot reach, named with the reason>

Installed on: <each of windows, mini, macbook you used, with the commit the
machine reported and what you saw there; or "none" and why this fix needed no
machine>
```

The `Installed on:` line says what the runner reported, not what you expect:
`Installed on: mini and windows at <sha>; macbook not used (the bug is
Windows-only). Ran <command> on windows, exit 0, saw <line>.` A machine you did
not use is named as not used, with the reason. The title carries the CB key. Say
only what was run: a PR that claims more than
was tested costs more than one that admits a gap.

## QA and merge

**Never move your own worktree off its branch.** To build or test the PR at
its sha, use a second worktree and remove it when you are done:
`git fetch origin <branch> && git worktree add --detach "$TMPDIR/qa-<sha>" <sha>`, then
`git worktree remove --force "$TMPDIR/qa-<sha>"`. A `git checkout <sha>`,
`git checkout --detach ...` or `git checkout FETCH_HEAD` in your own worktree
leaves it on a detached HEAD, and Paperclip refuses to start your next run there
(`workspace_validation_failed`): that blocked QAF-194 and QAF-198 three times
each on 2026-11-27 (CB-492). Checking out a single file from another ref
(`git checkout <ref> -- <path>`) does not detach; restore it before you finish.

QA reviews one sha, re-resolved just before reporting, and posts a **review
comment** (not an approval, GitHub refuses that on the account's own PR) whose
body starts `[bugfix-org] QA`, names what its repository's row in **Two
repositories, one code base** says (both rids, `osx-arm64` and `win-x64`, on
Orbweaver; each check read, on mobile), says which suites ran and what was
confirmed against what is assumed, carries the `Workflow change:` line on a
workflow PR, carries the `Proof without a person:` line when the PR was proved
as **A proof that seems to need a person** says, and ends `Verdict: merge` or
`Verdict: fix` with one list. The
lead merges only when that comment exists, the repository's merge gate holds at
the sha it names (on Orbweaver: both required checks are green), and the PR
head is still that sha:
`gh pr merge <number> --repo <the PR's repository> --squash`.

**A PR that changes a workflow file is merged by the warden, not by you.**
GitHub works out a PR's merge state for whoever asks: on a PR touching
`.github/workflows/` or `.github/actions/`, a token without `workflows: write`
is told BLOCKED, and `gh pr merge` refuses with "the base branch policy
prohibits the merge" (CB-486, #429). The lead's token never carries that
permission. So, when the same conditions hold, write one file,
`$BUGFIX_SPOOL/merge-<PR number>.json`, holding exactly
`{"verb": "merge_workflow_pr", "pr": <PR number>, "sha": "<the full 40-hex head sha QA reviewed>"}`.
Orbweaver only: mobile PRs may not change workflows at all. Within five minutes
the warden checks everything again itself (the bot's latest `[bugfix-org] QA`
comment ends `Verdict: merge`, names that sha, both rids and has the
`Workflow change:` line; the head is still that sha; both required checks are
green; the workflow guard is clean; the PR is the org's own `bugfix/pc-*`
branch into `develop`; the company is not paused), then squash-merges it at
that sha. Anything else it refuses and logs. Confirm the merge with
`gh pr view <number> --repo Uplift-Foundation/Orbweaver --json state` on your
next pass before you move the ticket to Done. Never close a workflow PR because
`gh pr merge` refused it.

## A red check your change did not cause

When a required check is red only on a test outside your change's footprint
(another area, a timing test you did not touch, a class with a flaky-test
ticket such as CB-461 or CB-463), ask the warden to rerun the failed jobs. Do
not push to retry. Write one file, `$BUGFIX_SPOOL/rerun-<PR number>.json`,
holding exactly `{"verb": "rerun_failed", "pr": <PR number>}` for an
Orbweaver PR. **A mobile PR's rerun** goes to a different file,
`$BUGFIX_SPOOL/rerun-mobile-<PR number>.json`, holding
`{"verb": "rerun_failed", "pr": <PR number>, "repo": "mobile"}`, and only when
`$BUGFIX_SPOOL/access.json` lists `"mobile": true` under `repos` (an older
warden ignores `repo` and would rerun the Orbweaver PR with that number). Until
then a red mobile check you did not cause is a failed round, named in the PR
body as a suspected flake.

The warden reruns the failed jobs of the PR's latest CI run within five
minutes. It does this once per head sha, and only for a completed red run;
anything else it refuses, and logs why. Name the failing test in the PR body as
a suspected flake.

A rerun is not a fix round. If the rerun is red again at the same sha, the
failure is real for this PR: fix it in a round, or release the ticket. A new
push gets a fresh rerun. The lead does the same before releasing a ticket over
a red check.

## Limits

**The limits are stated here and nowhere else;** every other file points
here.

- **Per run:** at most **two tickets taken** (to fix or reproduce), at most
  **five closes** (which do not use the two), at most **five settles** in
  **Bugs outside Refinement** (which use neither), and at most **two tickets with
  org PRs open** at once (a ticket with a PR in each repository counts once).
- **Per ticket:** **two fix rounds**, **ninety minutes** from the claim (less
  paused time, below), and **one rebase** onto `develop`.

Past a per-ticket limit the ticket is released, or closed when the attempt
itself is the evidence (**Every ticket gets an outcome**).

**A round that pushed no fix is not a fix round.** A round whose work was a
capture, or a step of **A proof that seems to need a person**, and which
changed no product code, does not use one. The ninety minutes still bound the
ticket. When they run out on a ticket that section covers, the lead settles it
as that section says with what was run; it is released only if no capture and
no test exist yet, and the release says that, never that it needs a person.

**Time the company was paused does not count toward the ninety minutes.** The
warden publishes every pause in `$BUGFIX_SPOOL/pauses.json`, a list of the last
20 windows, each `{"paused_at": "<UTC time>", "resumed_at": "<UTC time>" or null}`
(null means the company is paused now). The times are UTC and good to about
five minutes either way, because the warden notices a pause or a resume on its
five-minute tick; do not argue over a few minutes. A missing file means no pause
has been recorded. The lead works out a ticket's limit like this, and does it before
releasing any ticket for time:

1. `claimed` is the time of the `[bugfix-org] claimed` comment; `now` is the
   current UTC time.
2. For each window, take `max(paused_at, claimed)` as its start and
   `min(resumed_at, now)` as its end, using `now` when `resumed_at` is null. If
   the end is not after the start, the window does not overlap; skip it.
3. Add up the overlapping lengths. That is `paused_minutes`.
4. The ticket is out of time when `now - claimed - paused_minutes` is more than
   90 minutes.

Example: claimed 18:36, now 20:09, one window paused 18:39 and resumed 19:19.
It overlaps for 40 minutes. Elapsed is 93 minutes, minus 40 is 53, which is
inside the limit, so the ticket is not released for time. Without the pause the
same ticket would have been released.

Fix rounds and the rebase limit are not paused by this; only the ninety minutes.
A ticket continued from released work (see **Which tickets**) is a new claim: its
ninety minutes start at its new `[bugfix-org] claimed` comment.

## Never

Install on any machine yourself, or run `ssh` or anything else that reaches
one: only the machines runner does that, when you write it a request (**Real
machines**). So never take `/tmp/ow-install.lock` (the sandbox denies it
anyway). Send keyboard or mouse input to `macbook`, sign in to a real
account, or answer an approval to get a proof. Release a ticket because a
proof needs a person (**A proof that seems to need a person**). Push to `develop`, `main` or another team's branch. Create a tag or
a release. Edit `.github/workflows/` other than as **Workflow files** says, or
on mobile at all. Write a working exploit into a PR, a
commit or a comment. Paste a secret, a token, a home path or a
hostname into a PR, comment or commit.
