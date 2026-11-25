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

Skip a ticket when **any** of these holds:

- its status is not Refinement;
- an open PR names its key in the title or body
  (`gh pr list --repo Uplift-Foundation/Orbweaver --state open --search "<key> in:title,body"`);
- a pushed, unmerged commit names its key
  (`git fetch origin` in your worktree, then `git log --remotes --not origin/develop --grep "<key>"`),
  **unless that work is this org's own released work** (next paragraph);
- it has no comment starting with `[triage]` yet (the triage org's likely cause is your input);
- it is in a class this org leaves alone (below).

**This org's own released work is not a reason to skip.** When the rule above
finds commits, check each branch that holds them. It is the org's own, with no
one working on it, only if **both** hold:

1. its name starts `bugfix/pc-` and the author of every commit on it that is not
   on `develop` is the App's bot:
   `git log --format='%an <%ae>' origin/develop..origin/<branch> | sort -u`
   prints only `orbweaver-bugfix[bot]`;
2. no PR from it is open: `gh pr list --repo Uplift-Foundation/Orbweaver --state open --head <branch>`
   prints nothing.

Then the ticket is **eligible**, and the engineer continues from that branch
instead of starting over: in the worktree, `git fetch origin`,
`git checkout -B <branch> origin/<branch>`, rebase once onto `origin/develop`,
and carry on (a closed PR can be reopened with `gh pr reopen <number>`, or open a
new one from the branch with the same `gh pr create` as always). Keep the branch
name; never rename a pushed branch. The claim comment names that branch and says
`continuing released work`. If either test fails (a human's commit is on the
branch, or a PR is open) the ticket is somebody's and you skip it as before.
The work is yours to check: read what the branch changed before building on it.

Among what is left take the highest Jira priority, then class 1 before class 2,
then the oldest. At most **two tickets per run**.

## What this org fixes, and what it leaves

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

Leave alone, and say why if asked: a bug that needs a real captured prompt, a
timing on a real Windows install, or any observation on an installed app; a
security fix; anything in the mobile repository (`Orbweaver-Mobile`); a ticket a
human team holds. The class comes from the summary and the triage comment, and
you re-classify when you read the code: a ticket that turns out to need a real
machine is released.

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
the PR merged to `develop` and `cancelled` when the ticket was released. The
Jira comment is the human-readable record; the issue status is the machine one.

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
the token, never write it to a file, and never use any other credential. The
token lasts at least 45 minutes from the start of your run. If a push or `gh`
call fails with 401 late in a long run, finish the task, say so, and ask for
another round: the next run gets a fresh token. A push the rulesets refuse
(GH013) means you left your lane, which is `bugfix/pc-*` branches and PRs into
`develop`. Stop there, and say so in your answer.

## Long commands

A shell command gets ten minutes; a longer one is moved to the background, and
a run that ends its turn without reporting is recorded as failed ("no semantic
result"). This happened on 2026-11-24. So:

- Keep every command inside ten minutes. Run tests per class with the filter,
  not a whole suite. Split a long build-and-test into separate commands.
- Never end your turn while waiting on a background job. Either wait in the
  foreground, or report progress and finish the task, saying what is still
  running. The next wake picks it up.
- The build lock is yours only while your command runs. Take it and release it
  in the same command (`mkdir`, `trap 'rmdir …' EXIT`).

## Branch, commits, PR

- Branch `bugfix/pc-<change>`, from `origin/develop`. Named for the change, not
  the ticket key. Never rename a pushed branch.
- Commit messages are prose: a short imperative summary, paragraphs on why, the
  key in the message, ending with
  `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Iterate with targeted tests only. VSTest suites (`tests/UnitTests`,
  `tests/IntegrationTests`) take `--filter "FullyQualifiedName~<Class>"`; the
  MTP suites (`tests/UiTests`, `tests/UiScreenshots`) take
  `--filter-class <Namespace.Class>`, never `--filter`, and the total in the
  summary line shows whether the filter took. Add the matching console suite for
  a change to geometry, transcript parsing or orb initials.
- Tests land in the same branch as the fix and cover every line it adds.
- One `./tools/coverage.sh --base origin/develop` per PR; its first line must be
  `merged 4`, otherwise the number is not a measurement.
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
<each command you ran, the test classes, their totals, and the coverage figure
with its merged-count line>

## Not verified
<what an install on a real machine would have shown; Windows behaviour not
reproduced; any added line the tests cannot reach, named with the reason>

Installed on: none. This org cannot reach the three machines (MacBook, Mac mini, Windows PC).
```

The title carries the CB key. Say only what was run: a PR that claims more than
was tested costs more than one that admits a gap.

## QA and merge

QA reviews one sha, re-resolved just before reporting, and posts a **review
comment** (not an approval, GitHub refuses that on the account's own PR) whose
body starts `[bugfix-org] QA`, names both rids (`osx-arm64` and `win-x64`), says
which suites ran and what was confirmed against what is assumed, and ends
`Verdict: merge` or `Verdict: fix` with one list. The lead merges only when that
comment exists, both required checks are green at the sha it names, and the PR
head is still that sha.

## A red check your change did not cause

When a required check is red only on a test outside your change's footprint
(another area, a timing test you did not touch, a class with a flaky-test
ticket such as CB-461 or CB-463), ask the warden to rerun the failed jobs. Do
not push to retry. Write one file, `$BUGFIX_SPOOL/rerun-<PR number>.json`,
holding exactly `{"verb": "rerun_failed", "pr": <PR number>}`.

The warden reruns the failed jobs of the PR's latest CI run within five
minutes. It does this once per head sha, and only for a completed red run;
anything else it refuses, and logs why. Name the failing test in the PR body as
a suspected flake.

A rerun is not a fix round. If the rerun is red again at the same sha, the
failure is real for this PR: fix it in a round, or release the ticket. A new
push gets a fresh rerun. The lead does the same before releasing a ticket over
a red check.

## Limits

Two fix rounds per ticket, ninety minutes from the claim, one rebase onto
`develop`. Past any of them the ticket is released. Two tickets per run, two org
PRs open at once.

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

Install on any machine (so never take `/tmp/ow-install.lock`; the sandbox denies
it anyway). Push to `develop`, `main` or another team's branch. Create a tag or
a release. Edit `.github/workflows/`. Paste a secret, a token, a home path or a
hostname into a PR, comment or commit.
