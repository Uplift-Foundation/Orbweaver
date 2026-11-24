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

**Not running on a schedule, by decision, not by accident.** Every piece of a
triage run has worked on Paperclip's own runner in its default isolated mode
(Paperclip 2026.1117.0, Mac mini, 2026-11-23), but only with a person approving
tool calls as they came. The routine stays paused.

**What worked, measured**

- A Claude subscription connection made through Paperclip's own sign-in, bound
  to each agent. An agent with no binding fails with "Could not resolve
  authentication method": the connection existing on the company is not
  enough.
- The hand-off with no outside tools: the lead opened a task for each of the
  three, all four runs succeeded, no approval was asked for.
- Jira through Paperclip's Jira connector, granted at Atlassian's consent page
  as Jira read, write and search only, then limited in Paperclip to six
  read-only actions for all agents. The lead read CB-424 and searched with JQL.
- The engineer agent reading `origin/develop` from a dedicated local clone
  mounted as a project workspace, with the file tool only, naming the commit
  from `.git/HEAD`.
- One read-only triage of a real ticket, CB-424: three answers and a comment in
  the skill's format, written into the lead's task and **not posted**.

**What stops it running unattended**

- In `approve-paperclip` mode the runner auto-approves only Paperclip's own
  workflow tools. Every connector tool, read-only ones included, and several of
  Paperclip's own read tools (`load_skill`, `get_task`, `get_workspace`,
  `list_agents`) wait for a person. For a Claude agent the only choices are
  allow once, deny and cancel; nothing is remembered for the next run (read
  from the runner's code).
- `approve-all` answers every request with allow, with no tool filter, and the
  `paperclip-runner-workspace-only` sandbox profile belongs to Codex agents, not
  Claude ones (read from the code, not run). A triage agent reads ticket text
  anyone can write, so unfiltered shell as the logged-in user is not something
  to switch on without containing the service first.
- Until one of those changes, a run needs a person approving its tool calls.
- Since CB-442 (2026-11-24) that person no longer has to sit in Paperclip's own
  page: Orbweaver shows each waiting permission prompt on the agent's orb and
  on the phone, with the command and the task, and answers it with Allow once
  or Deny. Proven on the Mac mini against this org (an agent's `mkdir` allowed,
  a second one denied, a read-only `date` raising no prompt). That makes a
  supervised run practical. It does not make an unattended one: every Jira
  read is still one approval, on every run.

**Not done**

- Posting a comment: `addCommentToJiraIssue` is Off for every agent.
- The check that an agent's attempt to comment is refused.
- The lead reading the three answers inside a run: `get_task` returned
  truncated summaries, so the operator pasted them in for the CB-424 trial.
- The schedule firing, more than one ticket, a reboot of the host, backups.

**What was tried and withdrawn.** On 2026-11-22 the org ran once, on CB-416, by
moving every agent to Paperclip's legacy plain-`claude` adapter with the host's
Claude login and Jira MCP server lent to it. That was removed; the comment it
posted on CB-416 is real.

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
   ids).
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
5. **Pause the routine.** It imports active with its schedule enabled.
6. **Contain the service.** `host/paperclip-service.sb.template` is a macOS
   sandbox profile with the account's details left out. Render it once:
   `sed -e "s#__HOME__#$HOME#g" -e "s#__UID__#$(id -u)#g" -e "s#__USER__#$(id -un)#g" host/paperclip-service.sb.template > ~/.paperclip/instances/default/paperclip-service.sb`
   (on the mini that output is the running profile, byte for byte). The
   service's launchd job then runs
   `sandbox-exec -f <profile> paperclipai run --instance default`. Keep
   anything of your own (admin logins, session cookies) out of Paperclip's
   folder, because the agents can read that folder.

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
