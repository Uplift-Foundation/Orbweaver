# Grok Build: what was measured

Everything below was read off **grok 1.0.13** as installed on this machine
(`~/.grok/bin/grok`) and off a real session written by it on 31 Aug 2026
(`~/.grok/sessions/%2FUsers%2F…%2FClaude-Buddy/01a058ec-…/`). Anything not
confirmed that way is in **Still unknown** at the bottom.

The reason for the split is the same one `docs/codex-findings.md` gives: this
reads a format nobody here controls, it fails quietly when it fails, and a
plausible guess written down as a fact is worse than an admitted gap.

## Why orbs appeared before this feature existed

Grok's Claude Code compatibility layer loads hooks from
`~/.claude/settings.json` by default (documented in
`~/.grok/docs/user-guide/10-hooks.md`). Claude Buddy's Claude Code installer
had already written its handlers there. Grok fired them with a compatible
payload (`session_id`, `cwd`, `transcript_path`) and labelled the status file
`"cli":"claude"`. Auto-color then appended a Claude Code record into Grok's
own `updates.jsonl`:

```json
{"type":"agent-color","agentColor":"cyan","sessionId":"01a058ec-618a-7800-8788-c362cdf238fa"}
```

Confirmed on that session, line 17. The hook now treats `GROK_SESSION_ID` /
`GROK_HOOK_EVENT` as the source of truth and never writes into a Grok
transcript.

## The transcript

`~/.grok/sessions/<urlencode(cwd)>/<session-id>/updates.jsonl`. Sibling files
include `summary.json` (title, `agent_name`, `parent_session_id`) and
`signals.json` (context tokens).

Each row is an ACP envelope:

```json
{"timestamp":1788198313,"method":"session/update","params":{"sessionId":"…","update":{…}}}
```

`sessionUpdate` values observed in one live session:

| value | n | what the panel should do |
| --- | --- | --- |
| `user_message_chunk` | | stitch consecutive chunks into one user turn |
| `agent_message_chunk` | | stitch into one assistant turn |
| `agent_thought_chunk` | | stitch into a system (thinking) turn |
| `tool_call` | | one system line, titled |
| `tool_call_update` | | ignore — same tool, later status |
| `hook_execution` | | ignore |
| `turn_completed` | | ignore (flush is implied by the next kind) |
| `current_mode_update` | | ignore |

Chunk `content` is `{"type":"text","text":"…"}`. `tool_call` carries
`toolCallId`, `title`, `kind`, `status`. Unix timestamps on this build were
seconds around 1.7e9.

## Names

`summary.json` holds `generated_title` and `session_summary`. Docs say
`title_is_manual` is true after `/rename` and that a manual title wins. The
live session this was written against had never been renamed, so the exact
JSON key `/rename` writes (a `title` field vs overwriting `generated_title`)
is **still unknown**. The hook prefers `title` when `title_is_manual` is
true, then `generated_title`, then `session_summary`.

Grok has `/rename` (alias `/title`) and `/rename --auto`. It has no `/color`.

## Colour

TUI-wide `/theme` only. Auto-color for Grok is cwd-derived into the status
file, never written into `updates.jsonl` — the Codex rule, for the same
reason.

## Credits / usage

Grok logs `billing: fetched credits config` to `~/.grok/logs/unified.jsonl`.
A SuperGrok unified-billing account on this machine reported:

```json
{
  "creditUsagePercent": 14.0,
  "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "…", "end": "…" },
  "onDemandCap": { "val": 0 },
  "onDemandUsed": { "val": 0 },
  "prepaidBalance": { "val": 0 },
  "isUnifiedBillingUser": true
}
```

`subscriptionTier` is beside that (`SuperGrok`) — **beside**, meaning a sibling
of `config` inside `ctx`, not a member of `config`. A parser that looks in
`config` and then at the envelope root misses it in both places and leaves the
plan line blank, which is exactly what shipped until CB-83.

The envelope's `ts` is the age of the number. Grok writes this line **once, at
startup**, and never again for the life of the process, so a machine that last
ran `grok` two days ago is holding a two-day-old percentage — measured here at
38 hours. Read the reading's age off `ts`, never off the moment the log file
was read.

There is no five-hour
window. There is no public `grok usage` CLI. Asking Grok for this without
holding its refresh token is **still unknown**; the log line is a last-resort
read and is only as fresh as the last Grok process.

Do not call `/billing?format=credits` from this app. Do not read
`auth.json`'s refresh token. The email in that file is fair game as a **label**,
the way `~/.claude.json` `oauthAccount.emailAddress` is.

### Forcing a fresh reading, measured 4 Sep 2026 (CB-96)

**Nothing short of booting the real interactive TUI writes a fresh line — every
lighter subcommand was tried and left `unified.jsonl` untouched.** `grok
models`, `grok doctor`, `grok inspect`, `grok sessions` and `grok agent stdio`
all ran and produced other log output, but none of them contain or trigger
`billing: fetched credits config`.

**It needs a real pty, not just a subprocess.** A plain redirected child
(`ProcessStartInfo` with stdio piped, no terminal) fails immediately with
`Device not configured (os error 6)` and never reaches the credits fetch.
`script -q /dev/null grok` supplies one — BSD `script`, which ships with macOS,
so this costs no extra dependency the way reaching for `tmux` would have. The
credits line appears within 4-6 seconds of launch, consistently, across every
run measured.

**Three things worth knowing before killing it early:**

- **No trust-prompt hang, even from a directory Grok has never seen.** Launched
  from a brand-new scratch temp directory, credits still arrived in the same
  4-6 second window with nothing blocking on an interactive prompt. Safe to
  launch from an isolated cwd rather than a real project, so it never touches
  real session history.
- **No MCP servers start in that window.** `~/.grok/logs/mcp/{blender,
  playwright,nocodb,unity-mcp}.stderr.log` were untouched by any test launch
  here. Whatever starts those happens later in Grok's own boot sequence than
  the credits fetch — killing within ~8 seconds stays ahead of it. Not proven
  to stay ahead of it forever; a future Grok version that reorders its startup
  could change this, which is why the app always kills the whole process tree
  rather than trusting a graceful exit.
- **No residue in `~/.grok`.** Killed this quickly, a scratch-directory launch
  leaves no `trusted_folders.toml` entry and no session directory behind.

**Killing it clean matters as much as starting it.** `pkill -P <script-pid>`
then `kill <script-pid>` is what actually leaves nothing running — confirmed
against `ps -eo pid,ppid,etime,command`, not just an exit code, because `script`
is the parent and Grok itself is the child; killing only the parent would leave
Grok running unsupervised.

**Windows is unknown, not assumed working.** The pty requirement is not
macOS-specific, but the way to supply one is: Windows needs the ConPTY API
(`CreatePseudoConsole`), which nothing in this codebase wraps and which has not
been run against a real Grok install on Windows. The auto-refresh feature is a
deliberate no-op there rather than an unverified guess.

## Hooks

Grok events used here: SessionStart, UserPromptSubmit, PreToolUse,
Notification (`permission_prompt`), Stop, SessionEnd. Global
`~/.grok/hooks/*.json` is always trusted. Observe-hook default timeout is 5
seconds; installed handlers set `timeout` explicitly.

Payload docs use camelCase (`sessionId`); the live Claude-compat fire also
sent snake_case `session_id` and `transcript_path`. The hook reads both, and
falls back to `GROK_SESSION_ID` / `GROK_WORKSPACE_ROOT`.

PreToolUse is blocking; exit 2 is a deny. The hook still exits 0 and prints
nothing.

## `GROK_HOME` and the default home (CB-203)

**Setting `GROK_HOME` to `~/.grok` behaves the same as leaving it unset** — unlike Claude Code, where `CLAUDE_CONFIG_DIR=~/.claude` reads a different identity file from no variable at all (CB-42). Measured 2 Oct 2026 on grok 1.0.46 (MacBook) and 1.0.34 (Windows box), by running `grok du` under a scratch `$HOME` via `env -i`, once unset and once with `GROK_HOME=$HOME/.grok`, and diffing every file each run created: identical trees, and both print `Disk usage for ~/.grok`. The bundled user guide says the same ("when it is unset, Grok uses `~/.grok`"). The negative control was Claude Code under the same harness (`claude config list`), which does show CB-42's split — unset writes `$HOME/.claude.json`, set does not — so the method can see the difference when there is one.

A second reading, by a different method, agrees: `grok -p "Reply with exactly: OK"` against the real home printed `OK` both unset and with `GROK_HOME=$HOME/.grok`, and printed a not-logged-in prompt naming `grok login --device-code` with `GROK_HOME` pointed at an empty directory (the negative control). Same machine, grok 1.0.46, 2 Oct 2026.

Reading `~/.grok` itself: the shell's `ls` here is aliased to a tool that, given several paths at once, listed `~/.grok` as holding only `bin/`. `/bin/ls -la ~/.grok` shows the whole tree — `auth.json`, `sessions/`, `logs/` and the rest. Confirm a directory listing that looks empty with `/bin/ls` before concluding anything from it.

**A `GROK_HOME` that does not exist is created, not refused.** `grok du` against a scratch `GROK_HOME=…/.grok-work` left `docs/` and `logs/` behind, and a first interactive or `-p` run scaffolds a full home (`config.toml`, `agent_id`, `sessions/`, `logs/` and more), logged out. So a mistyped extra home silently becomes a fresh, logged-out account rather than an error. Codex differs on exactly this point (see `codex-findings.md`). The New chat dialog states this under the Account picker when the chosen home is missing (`NewChatAccountWarning`).

The New chat dialog still sets nothing for the default account (`NewChatAccountHome`, `ClaudeProfile.ConfigDirFor`): one rule for all three CLIs, and a `GROK_HOME` Buddy itself was launched under reaches the chat unchanged.

## Resuming a session by id (CB-361)

**`grok --resume <id>` exists and keeps the id.** It retires the old "no resume
flag is known" reading of this file: New chat's Resume mode starts
`grok --resume <id>` in the session's own folder rather than scraping the home
menu. What was observed, by machine:

- **Mac mini, Grok 1.0.13:** `--help` lists `--resume`, and a disposable session
  resumed with `grok --resume <id>` reused the same session directory, appended
  to the same `updates.jsonl`, kept the same id, and fired `session_start`
  within 2 s of launch (CB-361 architect, 23 Nov 2026) — so a resumed Grok orb
  appears without a prompt, unlike Codex.
- **MacBook, Grok 1.0.92, and Windows, Grok 1.0.77:** `--help` lists `--resume`
  on both. **Only the help text was read there; a resume was not run**, so
  "keeps the id" is measured on 1.0.13 only.

The same-id result is why a resumed orb is found again by its session id. It
does not by itself carry an orb's saved position, size or group, which are keyed
by folder and title (`SessionManager.PositionKeyFor`), not by id; hidden state
*is* keyed by id and is pruned two minutes after the session leaves the status
folder, so a session resumed after that comes back visible, while one resumed
inside the two minutes still carries its hide (CB-361 architect's reading of
`HiddenOrbs.DuePrunes`, not run).

The resume token is what `ResumeSupport` looks for in `<binary> --help`, so an
older Grok without it gets a plain refusal instead of a launch that errors in a
terminal nobody is watching.

## Still unknown

- The JSON field `/rename` writes when `title_is_manual` is true.
- A real Grok permission-prompt pane (`tmux capture-pane`) for dialog buttons.
- Whether `spawn_subagent` is a new `grok` pid (team arrows) or in-process
  (must not be hidden by the superseded-by-pid rule).
- How to poll credits without a token; until that is measured, usage orbs
  wait on a findings update rather than pretending a five-minute poll.
- Whether Grok fires `permission_prompt` before auto-approve (Codex's
  PermissionRequest problem). PostToolUse is not wired until that is known.
- Windows Grok beyond `grok du`/`--version`, WSL Grok, and a second *logged-in* `GROK_HOME` account, none of which have been run here — CB-203's measurement used a scratch home, which proves where Grok reads from, not that a second subscription works.
