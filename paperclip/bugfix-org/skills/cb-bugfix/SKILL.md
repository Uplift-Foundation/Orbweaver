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
  (`git fetch origin` in your worktree, then `git log --remotes --not origin/develop --grep "<key>"`);
- it has no comment starting with `[triage]` yet (the triage org's likely cause is your input);
- it is in a class this org leaves alone (below).

Among what is left take the highest Jira priority, then class 1 before class 2,
then the oldest. At most **two tickets per run**.

## What this org fixes, and what it leaves

1. **Flaky tests in this repository.** Proof: the test class run 30 times on
   both rids on GitHub's runners, by the warden (you ask for it in the PR; you
   cannot dispatch it).
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

Release means: close the PR if there is one, comment `[bugfix-org] released`
with what you learned and why you stopped, move the ticket back to Refinement.
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

## Limits

Two fix rounds per ticket, ninety minutes from the claim, one rebase onto
`develop`. Past any of them the ticket is released. Two tickets per run, two org
PRs open at once.

## Never

Install on any machine (so never take `/tmp/ow-install.lock`; the sandbox denies
it anyway). Push to `develop`, `main` or another team's branch. Create a tag or
a release. Edit `.github/workflows/`. Paste a secret, a token, a home path or a
hostname into a PR, comment or commit.
