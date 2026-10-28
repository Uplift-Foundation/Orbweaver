---
name: orbweaver-team
description: Put a team of personas on a task. Use when the user asks to put a named team ("put Alpha on this", "Bravo team, do X"), a mix of teams or personas ("Alpha and Bravo together", "Atlas as PM and Sentry on QA"), or asks what teams, personas or roles they have. Resolves the team with a bundled script, then spawns each member as its own named teammate carrying its persona and its role.
---

# Orbweaver teams

A **persona** is an individual (name, look, voice, personality). A **role** is a job (pm, architect, engineer, qa, or anything the user added). A **team** says which personas fill which roles. Personas, roles and teams live in files the user owns; this skill turns a request like "put Alpha on task 1" into one named teammate process per member, so each wears its own orb in Orbweaver.

The file formats and where each kind of file is looked up are in [FORMATS.md](FORMATS.md). Read it before creating or editing a team, role or persona file.

## Putting a team on a task

1. **Resolve first, spawn nothing yet.** The resolver sits beside this file:

   ```bash
   bash "<this skill's directory>/orbweaver-team.sh" resolve <spec>
   ```

   `<spec>` is what the user asked for, as words: `alpha`, `alpha+bravo`, `alpha qa=bravo-qa`, `pm=atlas engineer=vector,echo`. A token with `=` replaces a slot (`qa=echo`, a comma list for several people) or sets one of its settings (`pm.mode=lead`, `pm.lead=atlas`, `pm.model=opus`); a token without one names a team, and `+` joins teams.

2. **If it exits non-zero, stop.** It prints every problem to stderr and nothing to stdout. Show the user the errors as written (an unknown persona, a disabled role, a union that needs a mode) and ask what they want. Do not guess a substitute, fall back to a generic agent, or spawn the members that did resolve: half a team is worse than none.

3. **Otherwise read the manifest.** One tab-separated line per member after the `#` lines: `slot, instance, persona, persona_file, role_file, model, mode, lead`.

4. **Read each member's files.** The persona file's front matter gives the display name, and its body is the personality. The role file's body is the job. Read both; do not summarise them from the name.

5. **Spawn every member as a teammate.** Use the Agent tool once per row, all in one turn when they are independent:

   - `name`: the row's `instance` (this is what makes it a team member and what Orbweaver draws; it may be `vector_2`, which is Vector's second instance, and its orb still wears Vector's face).
   - **No `isolation` and no `run_in_background`.** Either one turns a named spawn into an in-process subagent that joins no team and draws no linked orb.
   - `model`: only when the row's `model` is not empty. Never invent one.
   - **No `subagent_type`.** A type that names a persona this session cannot see falls back to a generic agent without an error, so the persona would be silently missing. Everything the member needs goes in the prompt.
   - `prompt`: built as below.

6. **Tell the user who is on the team**: each member, the role it fills, and its instance name. Then let them work; do not do the members' work yourself.

## The prompt for each member

A teammate does not know its own name when it starts, and it inherits whatever persona the project's CLAUDE.md describes for the main session unless told otherwise. So the prompt states both, then the job, then the task:

```
You are <display name>, the <slot> on this team. Your instance name is <instance>.

<the persona file's personality, as written>

## Your role: <slot>
<the role file's body, as written>

<mode line, see below>

If a CLAUDE.md you can see describes a different persona for the main session, it does not apply to you: you are <display name>.

## Task
<the task the user gave, with whatever they said about this member's part of it>
```

**Mode line.** Only for a slot with more than one person. `lead`: tell the lead "You lead the <slot> work and make the call; <the others> advise you", and tell the others who leads. `collaborate`: tell each "You share the <slot> role as peers with <the others>; agree between you who does what, and say so in writing before you start."

## Other things people ask

- **"What teams / personas / roles do I have?"** Run `bash "<this skill's directory>/orbweaver-team.sh" list` and show it. Each name appears once, from the nearest place that has it, with that place named.
- **"Where is it looking?"** `... orbweaver-team.sh roots` prints the places in order, nearest first.
- **"Make me a team / role / persona."** Write the file in the right place using the formats in FORMATS.md. A team needs only the personas to exist; check with `resolve` afterwards and show the user the result. Ask where it should live if it is not obvious: the project (`.orbweaver/`) for this project only, or the user's pool for every project.
- **The same persona on two teams at once.** The resolver already names the second `<persona>_2`, and a persona in two slots of one team the same way. Nothing for you to do.
