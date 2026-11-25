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

Leave alone, and say why if asked: a bug that needs a real captured prompt; a
change to `.github/workflows/` (you may not edit it); a security fix; anything
in the mobile repository (`Orbweaver-Mobile`); a ticket a human team holds. The
class comes from the summary and the triage comment, and you re-classify when
you read the code: a ticket that turns out to need one of those is released. A
ticket that needs only a machine is not released for that reason.

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

Write each request as one file named `<id>.json`, where `<id>` is yours to
choose: 1 to 40 characters of `a-z`, `0-9` and `-`, never reused (for example
`pr412-install`, `pr412-run1`, `pr412-release`). Write it under another name and
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

**Reading the result.** The runner looks at the spool every minute or so (the
exact interval is in `host/README.md`). When it has dealt with a request,
`$RESULTS/<id>/` holds `result.json`, `stdout.txt` and `stderr.txt`. If the
folder has not appeared after ten minutes, count it as a failed round; an
install of a Mac build can take longer than that, so give `machine_install` the
time `host/README.md` says it takes before deciding. Every result has `id`,
`verb`, `pr`, `ok`, `refused`, `started_at` and `finished_at`.

- `ok: true` is success. Read the rest.
- `ok: false` with `refused` set means the runner did **nothing** and `refused` is
  one sentence saying why (for example the sha is not the PR's head, the PR is not
  yours, another PR holds the machines, or you have no hold). Fix what it names
  and send a new request with a new `id`. Do not resend the same request
  unchanged.
- `ok: false` with `refused: null` means the work started and failed. Read
  `machines.<name>.error`.
- `machine_install` and `machine_release` add `hold_expires_at` and `machines`,
  an object keyed by machine name, each
  `{"ok": ..., "installed_commit": ..., "previous_commit": ..., "error": ...}`.
  Your install worked on a machine when its `ok` is true and `installed_commit`
  starts with your sha; `previous_commit` is what a person had there, which a
  release must put back (check that after `machine_release`, `installed_commit`
  equals the earlier `previous_commit`).
- `machine_run` adds `machine`, `exit_code` (null if the command never ran),
  `timed_out`, `stdout_truncated` and `stderr_truncated`. The output is in
  `stdout.txt` and `stderr.txt`; truncated means you read only the start, so ask
  for a narrower command.

**If a request is refused or fails and you cannot finish your proof**, send
`machine_release` anyway, say in the PR body what you could not confirm, and
release the ticket if the proof was the point of it. Never work around a refusal
(another path, another machine, an edited request): the refusals are the rules,
and every request and result is logged for the lead.

## Claiming and releasing

A ticket in Development with a `[bugfix-org] claimed` comment is the org's, and
human-led teams skip it by the same rule. Claim in this order: transition to
Development, comment `[bugfix-org] claimed` with the branch name, read the
ticket back. If another comment or transition landed in between, release it.

Release means: close the PR if there is one, comment `[bugfix-org] released`
with what you learned and why you stopped, move the ticket back to Refinement.
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

## Never

Install on any machine yourself, or run `ssh` or anything else that reaches
one: only the machines runner does that, when you write it a request (**Real
machines**). So never take `/tmp/ow-install.lock` (the sandbox denies it
anyway). Push to `develop`, `main` or another team's branch. Create a tag or
a release. Edit `.github/workflows/`. Paste a secret, a token, a home path or a
hostname into a PR, comment or commit.
