---
name: cb-triage
description: Rules for triaging a bug on the CB Jira board for Orbweaver - which tickets to pick up, how to judge Bug versus Feature, how to read the code, and the exact comment to leave. Use on every triage run and for every bug assigned during one.
---

# Triaging a CB bug

## Which tickets

Jira project `CB` on `uplifttech.atlassian.net`. Pick up:

```
project = CB AND issuetype = Bug AND status = Refinement ORDER BY created ASC
```

Skip any ticket that already has a comment starting with `[triage]`. That
marker is how a run knows a ticket is done, so every triage comment starts
with it. At most 10 tickets per run; the rest wait for the next run. If your
task names one ticket, triage only that one and do not look for others.

## The Jira tools

The Jira connector gives you these and no others. The `cloudId` for
`uplifttech.atlassian.net` is `db488eae-d6cb-4100-92a9-e7a4519fc176`; pass it
to every call and do not call `getAccessibleAtlassianResources` (it has failed
with HTTP 400).

- `searchJiraIssuesUsingJql` - run the query above.
- `getJiraIssue` - read one ticket with its description and comments.
- `addCommentToJiraIssue` - the lead (Jennifer Voss) only: add one new
  comment. Never edit or replace an existing comment, and never call this
  twice for one ticket.

Everything else (edit, transition, assign, link, worklog, create) is off.

## What you may do

Read tickets and their comments, add **one new comment** per ticket. Nothing else.
No transitions, no field edits, no assignment, no links, no new tickets, no
pull requests. If something needs more than a comment, say so in the comment.

## Bug or Feature

A follow-up that fixes an existing feature is a `Bug` against that feature's
ticket. The test: would a reasonable reading of the original ticket's
acceptance criteria already have covered this case? If yes, it is a bug and
the feature is incomplete. Scope somebody asks for afterwards is a `Feature`.
If a ticket filed as `Bug` is really new scope, say so and name the reason.

## Reading the code

The code is in the engineer's project workspace ("Orbweaver source"), a
read-only copy of `origin/develop` refreshed before each run. Read only with
the Read tool: Bash, git, Grep and Glob are not available and Bash needs a
human approval. To find code, Read `INDEX-files.txt` (every tracked path) and
`INDEX-symbols.txt` (declarations as `path:line: text`) in the workspace
root, in pieces with offset and limit, then Read the file it names around that
line. To name the commit, Read `.git/HEAD` (detached, so it holds
the sha) and quote it. If you cannot read the code, say "could not read the
code" instead of guessing.

Mobile, relay and contract code is in a separate repository
(`Orbweaver-Mobile`); if the bug is there and you cannot read it, say that
instead of guessing.

## Claims

Say what you read and what you are inferring, separately. "The code at
`path:line` does X" is a reading. "So the orb never clears" is an inference
unless you ran it, and triage does not run the app. A confident "cannot
reproduce" stops people looking, so write "not reproduced from reading the
code" and say what was not checked.

## The comment

```
[triage] <one line: what this is, in plain words>

Type: Bug | looks like a Feature (reason)
Bug against: CB-nnn | unknown
Duplicates: none found | CB-nnn (same defect) | CB-nnn (related, different)
Suggested priority: Highest | High | Medium | Low - <reason>
Repro steps: complete | missing <what>
Likely cause: <path:line at origin/develop <sha>> - <what the code does>
Not checked: <what triage could not confirm>

- Jennifer Voss, Orbweaver Bug Triage (automated)
```

Keep it under 200 words. Never paste a secret, a token, a home path or a
hostname from a ticket or a log into the comment.
