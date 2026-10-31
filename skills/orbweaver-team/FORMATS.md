# File formats

Everything is Markdown. A file may start with a front matter block, `key: value` lines between two `---` lines; the rest of the file is free text. Nothing in front matter is nested, so there is no indentation to get wrong. A `# comment` line and blank lines are ignored, a space followed by `#` ends an unquoted value (`model: sonnet   # optional`), and a quoted value is what is inside the quotes, `#` and all.

## Where files are looked up

Nearest first. A nearer file of the same name **replaces** a farther one completely; fields are never merged between them.

| Level | Where |
|---|---|
| project | `.orbweaver/` in the nearest ancestor directory of the working directory that has one (and `profiles/<name>/` in any ancestor, the layout profile-gen writes) |
| account | `$CLAUDE_CONFIG_DIR`, else `~/.claude` |
| pool | `$ORBWEAVER_PERSONAS`, else the `personaPoolDir` the Orbweaver app has saved in its `settings.json`, else `~/.orbweaver`; under WSL with none of those, each Windows user's `.orbweaver` that exists. A relative path is refused. |
| built-in | the `roles/` folder next to the script (roles only) |

Under each level: `profiles/<name>/<name>.md` (a persona), `roles/<role>.md` (a role), `teams/<team>.md` (a team). profile-gen's fixed `persona/persona.md` is also accepted in the account and the pool, as the persona named by its own `slug:`.

Names (personas, roles, teams, slots) are letters, digits, `_`, `-` and `.`, and do not start with `.` or `-`.

## Persona

Whatever profile-gen writes: front matter with `name:`, `slug:`, `image:`, `voice:`, and a body describing the personality. Orbweaver reads the name, picture and voice for the orb; the personality is what a teammate is told it is.

The resolver only counts a persona file whose front matter has a `name:`, and goes on to the next place for one that does not (the app skips a nearer file that gives it nothing in the same way). Within one directory the persona in `profiles/<name>/` is looked for before the one in `.orbweaver/`, which is the order the app uses, so the persona a teammate is told it is and the one its orb wears are the same file.

## Role

```
---
role: qa
title: QA Engineer
model: sonnet        # optional: the model this role runs on unless a team says otherwise
disabled: false      # optional: true removes the role from use at this level
---
Instructions for whoever fills this role, in the second person.
```

Four ship with Orbweaver: `pm`, `architect`, `engineer`, `qa`. Add a role by writing a file (`roles/writer.md`); change one by writing a file with the same name at a nearer level; remove one by writing it with `disabled: true`.

## Team

```
---
team: alpha
title: Alpha Team
extends: base          # optional: start from another team
pm: atlas
architect: vector
engineer: vector, echo # several people in one role
qa: sentry
engineer.mode: collaborate   # optional: lead | collaborate (the default for a list)
engineer.lead: vector        # naming a lead makes the slot `lead`
qa.model: haiku              # optional: overrides the role's model for this slot
---
Free text about what this team is good at.
```

Every key without a dot is a slot, named for the role it fills. A slot may be one persona or a comma list. `extends` takes another team and then replaces whole slots: a slot the team names drops the base's slot, mode, lead and model together.

## Specs

What you give `orbweaver-team.sh resolve`, as words:

| Spec | Meaning |
|---|---|
| `alpha` | a saved team |
| `alpha+bravo` | both teams. A slot both fill with different people needs its mode said: `pm.mode=lead pm.lead=atlas` or `pm.mode=collaborate` |
| `alpha qa=bravo-qa` | a saved team with one slot replaced |
| `pm=atlas engineer=vector,echo` | no team at all |
| `qa.model=haiku` | one setting of one slot |

## Instance names

A persona that would run twice at once, whether in two slots of one team or on two teams running together, gets `_2`, `_3` and so on after its name (`vector`, `vector_2`). "Running" is read from the teams Claude Code records for the account (`<config dir>/teams/*/config.json`, where a member is listed while it lives) and from the process table; a stale entry only costs an unnecessary `_2`. The orb still wears the persona's face, because lookup tries the full name and then the persona it is an instance of.
