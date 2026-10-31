# Personas, roles and teams: findings and decisions (CB-291)

What was measured while building the pool, the roles, the teams and the
`orbweaver-team` skill, kept apart from what is merely believed, the way the other
`docs/*-findings.md` are. Dates are 2026-10-28. Everything here was run from a
Claude Code session in a WSL2 distro, with Orbweaver running on the Windows host,
unless it says otherwise.

## Spawning a team member

Four named spawns, foreground, no `isolation` and no `run_in_background`, each asked
to report its own process line.

| Question | Result |
|---|---|
| Is a named spawn a team member? | **Yes.** Each was a separate `claude` process carrying `--agent-id <name>@session-<id>`, `--agent-name`, `--team-name` and `--parent-session-id` (the lead's), which are the flags `AgentTeam.cs` reads. No custom agent type is needed. |
| Does a role given in the spawn prompt take? | **Yes.** A teammate given a name and the QA role in its prompt reported exactly that role and voice. |
| Does a teammate know its own name? | **No, not at the start.** One said its prompt did not state a name or role and guessed it was the other probe. It learned its name only from a `SendMessage` result showing its sender. The spawn prompt must say who it is. |
| Does `subagent_type` naming a persona work? | **Not from a session that cannot see the persona's agent file, and it fails silently.** The spawn succeeded, the process got `--agent-type sentry`, and the member had no personality at all. Nothing errored. The skill therefore never relies on an agent type. |
| Does a teammate inherit the project's persona? | **Yes.** It mentioned the repo's persona file, so it picks up whatever `CLAUDE.md` describes for the main session unless told otherwise. profile-gen's generated agent files carry a paragraph for exactly this. |
| Does `model` pass through? | **Yes.** `model: haiku` came up as `--model haiku`; without it a teammate inherits the lead's. |
| Does `effort` pass through? | **Not observably.** A spawn with `effort: low` came up with no `--effort` flag although the CLI lists one. The team config records no per-member model or effort. Left out of the first version. |

## What the app did before the pool

Four teammate orbs, all with the same portrait. The persona lookup for a team member
searched only `profiles/<name>/<name>.md` in the working directory and its
ancestors, so a pool in an account's config directory or in a shared folder was never
consulted, and each member fell through to the main session's persona. (The missing
arrows in the same screenshot were a separate defect, CB-290.)

## Why the resolver is a bash script with no external commands

A lead has to resolve `alpha+bravo qa=echo` the same way every time, which a model
reading the files does not. Of the places such a script could live, bash is the one
Claude Code already requires everywhere (Git Bash on Windows). Measured on the
Windows machine used here:

| Version | One four-slot resolve | Whole 36-case suite |
|---|---|---|
| awk, sed and tr freely | 14 s | 295 s |
| the same, after the `//` fix below | 0.6 s | not run |
| builtins only, trailing slashes stripped | 0.2 s | 8.9 s |

Two things made the difference, and both are the kind that read as nothing in the
code. A process costs about 0.1 s under Git Bash, so everything on the hot path is a
builtin and values come back in variables rather than through `$(...)`, each of which
forks. And the walk up from the working directory reaches `/`; joining `/` to
`/profiles/x` gives `//profiles/x`, which Git Bash reads as a UNC path to a host
called `profiles`, and each `[ -f ]` on one waited about 2.7 s for it to answer.
Every join now strips a trailing slash first.

macOS ships bash 3.2, so the script uses no associative arrays, no `mapfile` and no
`${var,,}`. That is reasoned from the language's history and from reading the script,
not run: the first run on a Mac is CI's `osascript`-capable leg.

## Putting it together, on real personas

The resolver was run over a real pool of four personas (`pm=atlas architect=vector
engineer=nova qa=sentry`), each persona's `## Personality` and the matching built-in
role were put into a prompt of the shape SKILL.md gives, and four named teammates
were spawned from them. Each answered in character with its own name, the role it was
given and a habit that is in its persona file (the PM ended on "who owns what and what
done means", the QA assumed everything was broken until run), and none answered as the
main session's persona although the project's `CLAUDE.md` describes one.

The app's side was checked against the same pool: `LocalPersona.Resolve` for each
agent name, in a build of `develop` before this change and in a build with it.

| Agent name | Before | After |
|---|---|---|
| atlas, vector, nova, sentry | the main session's persona and portrait, all four | each its own name, voice and portrait |
| atlas_2 | the main session's | Atlas's |
| a name matching no persona | the main session's | the main session's (unchanged) |

What was *not* done is the visual check of those orbs in the running app, which needs
the installed app stopped to run a second copy.

## What an independent review found, and what was done

A second Claude session reviewed the finished change without having written it. It
reproduced all of these by running something, and each now has a test that fails
without its fix.

- **The orb and the resolver could disagree about which file is the persona.** The
  app folds a run of consecutive member-profile candidates as one persona, so a
  directory's `profiles/` and its `.orbweaver/` folder were blended (a name from one
  file speaking in the other's voice), and so were an instance and its persona.
  Candidates now carry a `Place`; a run folds only within one place, and only the two
  layouts of one name share one. The resolver now looks in the same order, skips a
  file that names nobody as the app does, and matches a slug without regard to case.
- **The documented examples broke the parser.** `model: sonnet   # optional` put the
  comment in the model, and `disabled: true   # optional` was not disabled. A space
  followed by `#` now ends an unquoted value.
- **The resolver ignored `personaPoolDir`** that the README said it read. It now reads
  the app's `settings.json` (and refuses a relative path, as the app does).
- **`install-skill.sh` could delete the shipped source** from a relative `--settings`,
  and reported success after a failed copy. Paths are made absolute first, an overlap
  with the source is refused, and every step is checked.
- **Live names could not be seen on Windows.** `ps` cannot list a native `claude.exe`
  under Git Bash, so the teams Claude Code records for the account are read as well.
- **PowerShell 5.1's `Remove-Item -Recurse` follows a junction** and deletes its target.
  Reproduced with a junction on Windows: the installer now removes a link as a link.

Not changed: the two WSL call sites in `install-windows-hooks.ps1` have no automated
test (they need a distro), and a persona file with no front matter at all is not
usable by the resolver although the orb can still read one written as prose.

## Decisions

1. **No Orbweaver launcher.** Claude Code starts teams when told to; the skill tells
   the lead which personas, roles and models to use.
2. **One shared pool, `~/.orbweaver` or `ORBWEAVER_PERSONAS`**, plus a `personaPoolDir`
   setting for the tray app, which never sees a shell's environment. The variable wins.
3. **Nearest file wins entirely, never merged.** A persona's fields come from one
   place. This is why each account's pool is offered immediately before that
   account's own `CLAUDE.md`: the `CLAUDE.md` ends the run of member candidates that
   `ResolveFrom` folds together as one persona.
4. **The same persona twice is `<persona>_2`.** The resolver picks the first name no
   live session and no earlier member of the manifest has. Lookup tries the full name,
   then the persona it is an instance of.
5. **A slot is one persona or several.** Several are `collaborate` by default, or
   `lead` with a named lead. A union of two teams that fill a slot with different
   people refuses until the mode is said.
6. **profile-gen's `persona/persona.md` is not moved.** It is recognised in the
   account and the pool by its own `slug:`, and offered only to the agent that slug
   names, because the same path holds whoever's persona it is.
7. **Roles are generic job descriptions.** Orbweaver's own ticket process stays in
   its own `AGENTS.md`; the four built-in roles say what a PM, architect, engineer
   and QA do for anyone.

## Not verified

- The resolver on macOS (bash 3.2) and the macOS installer
  path; both are first exercised by CI's Mac leg.
- `effort`, as above.
- Anything on a distro whose default user is not the one running Claude Code.
- The roster served to a paired machine does not carry teams from a WSL session
  (`SessionManager.ApplyTeams` is still keyed by pid; CB-290 follow-up).
