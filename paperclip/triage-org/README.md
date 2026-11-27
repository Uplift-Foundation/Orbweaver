# Orbweaver Bug Triage - a Paperclip org (CB-420)

This directory is a [Paperclip](https://github.com/paperclipai/paperclip)
company package: an org of four agents that triages new `Bug` tickets on the
CB Jira board every weekday at 09:00 Pacific and comments what it found.

| Agent | Role | Does |
| --- | --- | --- |
| Jennifer Voss | CTO, lead | lists untriaged bugs, hands them out, writes the one Jira comment |
| Nadia Okafor | product manager | Bug-or-Feature, which ticket it is a bug against, priority |
| Juno Abernathy | engineer | reads `origin/develop`, names the likely cause |
| Ines Carvalho | QA | duplicate check, repro-steps check |

Jennifer is the project's own persona (`.claude/persona/persona.md`) and the
other three are the personas the CB feature teams already use, reporting to
her here as they do there. Their pictures live in `.profiles-assets/`, which
is not checked in, so this package carries names and instructions only.

## What it may do

Comment on a ticket. Nothing else: no transitions, no field edits, no PRs.
`skills/cb-triage/SKILL.md` is the rulebook and the comment format.

## Status

**Running unattended since 2026-11-24, on the owner's decision.** The routine
fires on weekdays at 09:00 Pacific on the Mac mini (Paperclip 2026.1117.0,
Paperclip's own runner, default isolated mode, agents in `approve-all`, the
service inside the sandbox described below). Only the lead may comment on
Jira; the other three have six read-only Jira actions and nothing else.

**What ran, measured on 2026-11-24**

- A non-lead agent asked to comment on Jira: the comment tool did not exist
  for it and nothing was sent.
- One ticket, start to finish, with no approval: three answers and one comment
  posted (CB-424, Jira comment 11569), in about two minutes.
- The schedule: on resume the routine's missed trigger fired by itself and
  opened the run's task.
- The first full run: ten tickets, ten comments (CB-336 to CB-378, Jira
  comments 11570 to 11579), one per ticket, read back from Jira afterwards.
  Eighteen more waited for the next runs, because a run stops at ten.

**What that first run needed fixing, and why the instructions say what they say**

It stalled three times before it posted anything, each time on how the org was
set up, not on Paperclip being down:

- **The lead cannot read a helper's answer with `get_task`.** Paperclip stores
  the answer as a comment on the helper's task and `get_task` returns the task
  without its comments. The lead now reads `GET /api/issues/<id>/comments`
  through `call_api`, for that one read only, and helpers begin every answer
  with a short `ANSWER <key>:` block.
- **A helper that could not answer marked its task blocked**, and a blocked
  child never wakes the lead. Helpers now always finish; "could not answer" is
  an answer.
- **Thirty tasks at once.** One helper run failed with the runner's
  "Accounting spool changed during recovery; retry required", which Paperclip
  will not retry, and the lead was holding all ten comments for it. The lead now
  works three tickets at a time, posts each comment when its three answers are
  in, and writes "not obtained" for a field whose task failed.

**Known limits**

- Paperclip scans tool results and refused four Jira reads in that run with
  `prompt_injection_blocked` (finding `secret_exfiltration`); the tickets
  involved discuss secrets. That filter is left on. The agent says which ticket
  it could not read under "Not checked".
- Jira search results are often larger than an agent can take in one piece;
  the agents narrow the query or page it.
- Comments are posted under the Atlassian account that granted the connector.
- `approve-all` has no tool filter for a Claude agent. What contains a run is
  the sandbox around the service, the six-plus-one Jira actions, and the
  Atlassian grant, which is Jira read, write and search. An agent that goes
  wrong can still do whatever that grant and Paperclip's own folder allow.
- While the lead waits for its helpers, Paperclip shows its task as blocked.
  That is a wait, not a request for a person.
- Not tried: a reboot of the host, backups, a week of runs, a run with nothing
  to triage.

**What was tried and withdrawn.** On 2026-11-22 the org ran once, on CB-416, by
moving every agent to Paperclip's legacy plain-`claude` adapter with the host's
Claude login and Jira MCP server lent to it. That was removed; the comment it
posted on CB-416 is real. On 2026-11-23 it ran in `approve-paperclip`, where
every connector call waits for a person; CB-442 made those prompts answerable
from Orbweaver, and the owner then chose unattended runs instead.

## Setup on the host

```bash
npx paperclipai@latest onboard --bind lan --install-service
paperclipai company import paperclip/triage-org --target new --dry-run
paperclipai company import paperclip/triage-org --target new
```

Then, none of which a package can carry:

1. **Service address.** With the server bound to the LAN, set
   `PAPERCLIP_API_URL=http://127.0.0.1:<port>` in the service's environment.
   The runner refuses an MCP endpoint that is neither HTTPS nor loopback.
2. **AI connection.** Connectors, Anthropic, Connect, in a browser. Then bind
   it to each agent (`runtimeConfig.aiConnection` with the connection and grant
   ids). **One connection is not enough on its own:** when that subscription
   hit its weekly limit on 2026-11-25, every triage run failed until a person
   stepped in (CB-469). The running company now draws on a pool of all four
   subscriptions, as `paperclip/bugfix-org/README.md` step 3a describes under
   "The triage company has the same four accounts", and a new company is set
   up by the four steps there.
3. **Jira connector.** Connect, choosing the right Atlassian account and site,
   and untick everything but the three Jira toolsets at Atlassian's consent
   page. **Straight after the consent, Paperclip allows every discovered action
   for every agent.** Call the connector's finish step at once with only the
   read actions: `searchJiraIssuesUsingJql`, `getJiraIssue`,
   `getAccessibleAtlassianResources`, `atlassianUserInfo`,
   `getVisibleJiraProjects`, `getJiraIssueRemoteIssueLinks`.
4. **Source.** A dedicated clone of the repository, never a working one, as a
   project named "Orbweaver source" with a `local_path` workspace; put that
   project's id in the lead's instructions. `host/refresh-src.sh` keeps it on
   `origin/develop` and writes `INDEX-files.txt` and `INDEX-symbols.txt`, which
   the agents need because the runner gives them a file tool and no search
   tool. Run it before each scheduled run (on the mini a launchd job does, at
   08:50 on weekdays).
5. **Pause the routine** until the steps below are done. It imports active
   with its schedule enabled.
6. **Contain the service.** `host/paperclip-service.sb.template` is a macOS
   sandbox profile with the account's details left out. Render it once:
   `sed -e "s#__HOME__#$HOME#g" -e "s#__UID__#$(id -u)#g" -e "s#__USER__#$(id -un)#g" host/paperclip-service.sb.template > ~/.paperclip/instances/default/paperclip-service.sb`
   (on the mini that output is the running profile, byte for byte). The
   service's launchd job then runs
   `sandbox-exec -f <profile> paperclipai run --instance default`. Keep
   anything of your own (admin logins, session cookies) out of Paperclip's
   folder, because the agents can read that folder.

   Then build the `ps` stand-in and put it first on the job's `PATH`:
   `clang -O2 -o ~/.paperclip/tools/bin/ps host/ps-lstart.c`, and prepend
   `~/.paperclip/tools/bin` to `EnvironmentVariables.PATH` in the launchd
   plist (a plist change needs `launchctl bootout` then `bootstrap`, not a
   kickstart). Paperclip decides that an old run has stopped by signalling its
   recorded pid and, when something answers, comparing that process's start
   time from `ps -o lstart= -p <pid>`. Under any sandbox macOS refuses to run
   the setuid `/bin/ps` at all, and this profile's signal rule makes every
   process outside the sandbox answer, so once the kernel hands an old run's
   pid to an unrelated process, pausing that agent never finishes. That is
   how the bug-fix org's lead sat in "pausing" for hours (CB-467). The
   stand-in answers that one question from `sysctl`, needs no privilege, and
   hands every other `ps` invocation to `/bin/ps` unchanged.

7. **Let the lead comment, and nobody else.** First add a tool policy of type
   `block` whose selectors name the `addCommentToJiraIssue` catalog entry and
   the three non-lead agents (`agentIds`), at a priority ahead of the others.
   Only then call the connector's finish step again with the six read actions
   plus `addCommentToJiraIssue`. Check each agent's access afterwards: seven
   allowed for the lead, six for the others. In that order there is no moment
   at which everyone can comment.
8. **Run mode.** The package sets `acpxPermissionMode: approve-all`, which is
   what lets a run finish with nobody watching. Do not resume the routine in
   that mode before step 6 is in place.

## What the sandbox does and does not contain

Measured on the Mac mini, 2026-11-23, from a shell under the profile and from
one approved shell command inside a real agent run:

| Blocked | Still reachable |
| --- | --- |
| The rest of the user's home: ssh keys, shell and tool config, the login keychain file, other clones | Paperclip's own folder, including its secret store (`secrets/master.key`), its database, and every agent's private home |
| Other sessions' terminal and message sockets, the ssh agent, other sessions' Claude scratch space | The network, outbound, unrestricted |
| Writing anywhere in home outside Paperclip's folder; changing the profile itself | The names (not contents) of entries in the home folder; the host's `~/.claude/settings.json`, which the runner insists on inspecting |
| Starting launchd jobs, opening apps through LaunchServices, signalling processes outside the sandbox | Temp space, system toolchains |

So the profile turns "an agent with a shell is the logged-in user" into "an
agent with a shell is Paperclip": it can still read what Paperclip itself
holds, which here is the Claude connection and the Jira grant, and it can
reach the network. The per-action Jira limits are enforced by Paperclip's
gateway, not against a process that reads Paperclip's own store. Narrowing the
Atlassian grant at consent is the control that survives that.

Three things the runner needed that a tighter first draft broke, each found by
a failed run: the host Claude settings file (setup failed with EPERM), the
home directory entry itself (EPERM again), and the agents' own Claude scratch
folders under `/tmp/claude-<uid>` (startup failed with EEXIST on the parent).
Denying LaunchServices lookups outright made Node abort at start; `deny
lsopen` blocks opening apps without that.
