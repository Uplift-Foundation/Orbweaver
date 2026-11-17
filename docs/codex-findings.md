# Codex CLI: what was measured

Everything below was read off **codex-cli 0.148.0** as installed on this
machine (`~/.codex/packages/standalone/releases/0.148.0-aarch64-apple-darwin`)
and off two real rollouts written by it on 19 Aug 2026. Anything not confirmed
that way is in **Still unknown** at the bottom, and stays there until someone
runs it.

The reason for the split is the same one `docs/claude-code-chat-findings.md`
gives: this reads a format nobody here controls, it fails quietly when it fails,
and a plausible guess written down as a fact is worse than an admitted gap.

## The transcript

`~/.codex/sessions/<yyyy>/<mm>/<dd>/rollout-<iso>-<session-id>.jsonl`. The
session id is in the filename, which is what makes the file findable from a
hook payload that may not name it.

Every row:

```json
{"timestamp":"2026-08-19T16:57:08.663Z","ordinal":9,"type":"event_msg","payload":{…}}
```

### The same conversation is written twice

Two values of `type` carry content, and choosing between them is the single
biggest decision in `CodexTranscript`:

| `type` | what it is |
| --- | --- |
| `response_item` | the model-facing wire log — raw request items, the developer preamble, the environment context, and the full stdout of every command |
| `event_msg` / `payload.type: "item_completed"` | the TUI's own record of what it drew |

Only the second describes the conversation as a person experienced it. Measured
across the two rollouts: **253 of 504 rows were `response_item`, and not one of
them contained the string `item_completed`** — so a substring pre-filter
separates them exactly, which is what `CodexTranscript.IsInteresting` relies on.

Other row types seen and ignored: `session_meta` (row 0, carries the whole
system prompt), `world_state`, `turn_context`, and `event_msg` payloads
`task_started`, `task_complete`, `token_count`, `thread_settings_applied`.

### Item types

`payload.item.type`, counted across both rollouts:

| item | n | median bytes | max bytes |
| --- | --- | --- | --- |
| `CommandExecution` | 71 | 4,567 | **1,046,104** |
| `Reasoning` | 109 | 395 | 395 |
| `AgentMessage` | 26 | 741 | 1,613 |
| `FileChange` | 15 | 3,768 | 10,339 |
| `Extension` | 15 | 976 | 7,320 |
| `UserMessage` | 4 | 510 | 557 |

`McpToolCall` is known to exist (it is in the binary's item enum) but did not
occur in either rollout, so it is **not** mapped — an MCP call currently shows
as nothing in the panel.

### The one that would cost every reply

**`UserMessage` content blocks are typed `"text"`. `AgentMessage` content blocks
are typed `"Text"`.** Confirmed on real rows, both directions. A parser that
assumes one casing covers both silently drops half the conversation. There is a
test named for this in `tests/TranscriptTests`.

### Why oversized rows are not parsed

The largest `item_completed` row measured is **1,046,104 bytes** — a
`CommandExecution`. In that row:

| field | byte offset |
| --- | --- |
| `command` | 311 |
| `parsed_cmd` | 445 |
| `aggregated_output` | 503,008 |

Everything the panel shows sits in the first kilobyte and the megabyte is
output. `CodexTranscript` therefore parses rows up to 2 MB (above the largest
measured, so every ordinary row takes the exact path) and reads anything larger
from a bounded scan of its head. A `cat` of something big has no upper bound at
all, which is the case that makes this a rule rather than an optimisation.

### Other measured shapes

- **`ordinal`** is dense and unique within a rollout (0..503 in the file
  checked) and is what a reader should dedupe on. Rows carry no `uuid`; item
  `id`s are absent on some items and shared between a call and its output on
  others.
- **`Reasoning.summary_text` was empty on all 109 items.** Codex writes a
  summary only when the model produces one. Both plausible element shapes
  (a bare string, an object with `text`) are accepted rather than guessed at.
- **`AgentMessage.phase`** is `commentary` (22) or `final_answer` (4). Both are
  drawn by the TUI, so both are shown.
- **`CommandExecution.parsed_cmd[].type`** is `unknown` (43), `search` (27),
  `read` (21) or `list_files` (5). Its `cmd` is preferred over `command`, which
  begins `/bin/zsh -lc` on every entry and would make every row look the same.
- **`FileChange.changes`** is keyed by absolute path, values `update` (35),
  `delete` (1), `add` (1).
- **`Extension.kind`** was `web.search` on all 15. `action.type` is `search`,
  `openPage`, `findInPage` or `other`; when it is `other` the top-level `query`
  is the empty string, so a bare `· web.search` is the honest reading rather
  than a parse failure.

### Names: not in the rollout, but Codex has them

An earlier version of this document said Codex names nothing. That was wrong,
and it was wrong in the way worth guarding against — true of the file being
read, and false about the product.

The rollout carries nothing corresponding to Claude Code's `custom-title`,
`ai-title` or `agent-color` rows. But `$CODEX_HOME/state_<n>.sqlite` has a
`threads` table, and it holds both halves of what Claude Code keeps in its
transcript:

| column | is |
| --- | --- |
| `name` | what `/rename` set. Null until someone renames the thread. |
| `title` | Codex's own, taken from the first thing you asked. Always set. |
| `preview` | the same first message, truncated. |
| `first_user_message` | likewise. |
| `agent_nickname` | null on every thread seen so far. |

So the precedence is exactly Claude Code's — a name set by hand outranks a
generated one — and the query is:

```sql
select coalesce(nullif(name,''), nullif(title,'')) from threads where id = ?
```

Measured against the live database while Codex was running: **7 ms**, read-only
so a hook can never take a write lock on a database Codex is using. A full Codex
hook invocation including this costs about 98 ms.

"While Codex was running" is load-bearing, and was found the hard way. The
database is in WAL mode, so a read-only connection needs the `-shm` file to
attach to — and when Codex is not running, that file and the `-wal` are
checkpointed away, at which point a read-only open fails outright with
`SQLITE_CANTOPEN`. The same query that returned `aiea` in one minute returned
nothing in the next, purely because Codex had exited in between.

That is benign in production, because a hook only ever fires while the CLI that
called it is running — and the fallback to the rollout's first message is exactly
what should happen anyway. It matters for anyone *testing* this: a title that
comes back as the first prompt rather than the `/rename` name is not necessarily
a bug in the precedence, it may just be that nothing has Codex open.

Two things to know before relying on it:

- **The filename carries a schema version** — `state_5.sqlite` today. Take the
  highest-numbered match rather than naming one, so a bump alone doesn't break
  the lookup.
- **A thread appears in the table a moment after it starts**, so the very first
  hook of a session can legitimately find nothing. The fallback reads the first
  `UserMessage` out of the rollout, which is the same message Codex builds its
  own `title` from — so the two agree rather than compete.

There is **no per-session colour**, but there is a colour. It lives on a
*section* — a named group of threads, managed with `/section` and
`/createsection` — as `thread_sections.appearance`, a JSON `{"icon":…,"color":…}`,
which a thread points at through `threads.thread_section_id`. Confirmed by
creating one over the app-server:

```
threadSection/create {"name":"cb-probe","appearance":{"icon":"circle","color":"blue"}}
  -> {"section":{"id":"01a02004-…","name":"cb-probe","appearance":{"icon":"circle","color":"blue"}}}
```

`threadSection/create`, `/update`, `/delete` and `/list` all exist as RPCs, as
does `thread/section`. So writing one is possible — and is deliberately not
done. Filing a session under a section to give it a colour would reorganise the
user's own thread list in Codex's sidebar. The hook reads the section's colour
when there is one and leaves the ring plain otherwise.

Note that a derived title is not unique. Two of the rollouts on this machine
began with the *same* first message in different directories — the same
instruction run against two repositories, which is a normal way to work. The
working directory therefore has to stay part of anything identifying a session,
the saved orb position included.

## The hooks

Codex ships a hook system that is deliberately Claude-Code-shaped — its own
embedded output schema even says *"Claude requires `reason` when `decision` is
`block`; we enforce that semantic rule during output parsing"*.

**Events**: `PreToolUse`, `PostToolUse`, `PermissionRequest`, `PreCompact`,
`PostCompact`, `SessionStart`, `SessionEnd`, `SubagentStart`, `SubagentStop`,
`UserPromptSubmit`, `Stop`. **There is no `Notification`** — `PermissionRequest`
is the analogue, and carries `tool_name` and `tool_input`, which Claude Code's
`Notification` does not.

**Input**, from the binary's own JSON schemas (`stop.command.input` and friends):
`session_id`, `cwd`, `hook_event_name`, `model`, `permission_mode`
(`default` | `acceptEdits` | `plan` | `dontAsk` | `bypassPermissions`), and a
Codex extension `turn_id`. `transcript_path` is **nullable**. `Stop` adds
`last_assistant_message` and `stop_hook_active`; `SessionStart` adds `source`
(`startup` | `resume` | `clear` | `compact`); `SessionEnd` adds `reason`.

**Output**: every property of `permission-request.command.output` has a default
and none is required, so **empty stdout is "no opinion"** and the normal prompt
is shown. The failure modes to design against are elsewhere:

- **exit code 2 is a deny** — `PermissionRequest hook exited with code 2 but did
  not write a denial reason to stderr`
- the output schema is `additionalProperties: false`, so even valid-but-unknown
  JSON on stdout is `hook returned invalid permission-request JSON output`

"Fails closed" in the schemas refers only to the reserved `interrupt`,
`updatedInput` and `updatedPermissions` fields, not to silence.

A handler may also declare `async: true`, which makes it structurally unable to
return a decision at all.

**`SubagentStart` / `SubagentStop` carry the parent's `session_id`** alongside
`agent_id`, so a subagent's hook writes would land on the parent's status file.

### Where hooks are configured, and what is accepted

Measured by writing a `hooks.json` into a scratch `CODEX_HOME` and asking a
running `codex app-server` for `hooks/list` over JSON-RPC on stdio — which is
also the cheapest way to check an installer's work without starting a session:

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"probe","title":"probe","version":"0.0.1"}}}
{"jsonrpc":"2.0","method":"initialized","params":{}}
{"jsonrpc":"2.0","id":2,"method":"hooks/list","params":{}}
```

- **`$CODEX_HOME/hooks.json` is discovered automatically.** Nothing needs adding
  to `config.toml`. Entries come back tagged `source: "user"`.
- **The config shape is Claude Code's**, exactly:
  `{"hooks": {"<Event>": [{"matcher": …, "hooks": [{"type": "command",
  "command": …}]}]}}`. `matcher` works. Event keys are PascalCase in the file
  and normalise to camelCase in the listing.
- **A handler accepts `async: true`**, confirmed on `PermissionRequest` and
  `PreToolUse`. It also accepts `timeout`, in seconds, defaulting to 600.
- **An event name Codex does not know is dropped silently** — `Notification`
  was accepted into the file, produced no entry, and reported nothing in either
  `warnings` or `errors`. So a wrong event name is indistinguishable from a hook
  that never fires, which is the single easiest way to waste an afternoon here.
  It also means a config carried over by Codex's own `/import` from a Claude
  Code setup can contain dead entries that nothing complains about.
- **A `hooks.json` written by anything other than Codex starts untrusted**:
  every entry came back `trustStatus: "untrusted"`, alongside a
  `currentHash: "sha256:…"`, which is what re-asks after an edit. Nothing fires
  until the user accepts it.

## The TUI, measured in a tmux pane

Captured with `tmux capture-pane -p` against a real Codex 0.148 session. Three
things were expected to differ from Claude Code and do not — which is worth
writing down, because "we checked and it is the same" and "nobody looked" are
indistinguishable otherwise.

**The approval prompt is a numbered 1..n list**, and
`ChatTranscript.ParseDialog` reads it correctly, keys and labels both. A real
escalation, verbatim:

```
  Would you like to run the following command?

  Environment: local

  Reason: Allow creating the requested empty file cb-approval-probe in your home directory?

  $ touch $HOME/cb-approval-probe

› 1. Yes, proceed (y)
  2. Yes, and don't ask again for commands that start with `touch $HOME/…` (p)
  3. No, and tell Codex what to do differently (esc)

  Press enter to confirm or esc to cancel
```

This contradicts what the binary's strings suggest. Codex ships `Allow` /
`Allow for this session` / `Allow and don't ask me again` wording, which would
have broken the contiguous-1..n rule — but the TUI does not use it for command
approval. A Codex-specific dialog parser was planned and turned out to be dead
code. The fixture is in `tests/TranscriptTests`.

**A digit answers immediately.** Sending `3` dismissed the prompt and acted on
it. The "Press enter to confirm" line describes the arrow-key route, not the
numeric one.

**Escape interrupts.** Codex's own working indicator says so — `• Working (0s •
esc to interrupt)`.

**Submitting works with the sequence the app already uses**: set a tmux paste
buffer, `paste-buffer -p -d`, then `send-keys Enter`. Replayed against a live
Codex pane and it submitted exactly as it does for Claude Code. Note that the
Enter has to be its own key — sending the text with a trailing `C-m` typed it
into the composer without submitting.

**The sandbox does not block the hook.** `codex exec` defaults to a read-only
sandbox, which looked like a likely cause when status files failed to appear;
it is not. `mkdir`, `>` redirection and `touch` all succeed from inside a hook,
and `TMPDIR` is passed through untouched. The real cause of the missing file was
`SessionEnd` firing at the end of a one-shot `exec` run and correctly deleting
it — the session really had ended.

### When a Codex session gets an orb, and why it isn't at startup

**Codex fires no hooks until the first message of a session.** Measured by
wiring every event to a logging probe and watching a real TUI:

| moment | events |
| --- | --- |
| TUI opens | *nothing* |
| first prompt submitted | `SessionStart`, `UserPromptSubmit`, `Stop` |
| `/quit` | `SessionEnd` |
| `codex resume` | *nothing* |
| first prompt after resume | `SessionStart`, `UserPromptSubmit`, `Stop` |

So a session's thread is created when you first speak to it, and `SessionStart`
means "a thread started", not "the program launched". `codex exec` looks
different only because it submits a prompt immediately.

The consequence is visible and worth stating plainly: **an open-but-untouched
Codex session has no orb, and neither does a resumed one until you send
something.** Claude Code fires `SessionStart` on launch, so the two feel
different for the same actions — exit and resume brings a Claude Code orb back
at once and a Codex orb back on the next message.

Nothing in the hook can improve this; the hook cannot run before Codex runs it.
The alternative would be scanning for `codex` processes and matching them to
threads, which this app deliberately does not do for orb discovery — the whole
design is that a session announces itself and nothing polls the CLI.

### Trust is a hash of hooks.json, so the wiring must stay still

Codex records a `currentHash` per hook entry, and a `trustStatus` beside it. If
`hooks.json` changes, entries come back **`modified`** and stop running until the
review is accepted again — with no error anywhere, which is the same
silently-does-nothing failure the trust prompt itself causes.

Measured both directions on the same machine. Adding an argument to the hook
command flipped all seven entries from `trusted` to `modified`; putting the file
back to its byte-identical earlier form flipped them back to `trusted` with no
prompt at all. So it really is content-addressed, and a rewrite that changes
nothing costs nothing.

That is why the auto-colour setting is a **marker file** the app writes beside
the status files rather than a flag baked into the hook command. A flag was the
first design and shipped broken for exactly this reason: toggling a colour
rewrote `hooks.json`, and every Codex orb quietly stopped appearing until the
user happened to re-approve the hooks. Anything that toggles at runtime has to
stay out of `hooks.json`.

## Credits / usage

Codex has no `codex usage` CLI. `/status` inside a live session shows the
same numbers, and the usage dashboard at chatgpt.com/codex/settings/usage
shows them too. Neither is a thing this app can ask without holding a
session or a login token.

What it *can* read, without either, is the `rate_limits` object Codex
already writes onto every `token_count` event in the rollout. Measured on
**codex-cli 0.151.0**, 31 Aug 2026, last snapshot of
`rollout-2026-08-30T22-20-49-01a05643-….jsonl`:

```json
{
  "limit_id": "codex",
  "primary": {
    "used_percent": 6.0,
    "window_minutes": 300,
    "resets_at": 1788239518
  },
  "secondary": {
    "used_percent": 3.0,
    "window_minutes": 10080,
    "resets_at": 1788807866
  },
  "credits": {
    "has_credits": false,
    "unlimited": false,
    "balance": null
  },
  "plan_type": "team"
}
```

`window_minutes` 300 is the five-hour window; 10080 is the week. Older
rollouts on this account (August 19–24) had only the week, as `primary`,
with `secondary` null — the five-hour window was off for a stretch in July
and those files predate it coming back. `used_percent` is 0–100, the same
scale Claude Code's `utilization` uses. `resets_at` is unix seconds.

`credits.has_credits` was false on every snapshot here. A remaining
`credits.balance` without a cap is not a percentage and is not drawn as a
ring.

### The window-less snapshot, measured 2 Sep 2026 (CB-83)

**Not every `token_count` carries a window, and the empty one is often the
newest thing on disk.** When the workspace runs out of credits Codex sends a
snapshot whose windows are both null, carrying only a reason:

```json
{
  "limit_id": "premium",
  "primary": null,
  "secondary": null,
  "credits": { "has_credits": false, "unlimited": false, "balance": null },
  "plan_type": null,
  "rate_limit_reached_type": "workspace_owner_credits_depleted"
}
```

It goes to **every live session at once**. On this machine, four rollouts
received it inside one second (18:57:26–18:57:45 UTC), and the line 0.3s before
it in the same file read `primary` 98%, `secondary` 38%. So a reader that keeps
the last `rate_limits` line, in the newest-modified file, gets null/null — and
reports an account with no subscription limits while the account is at 99% of
its five-hour window.

Two rules follow, and both are load-bearing:

- **Keep only snapshots that carry a window.** An empty one is legible but is
  not a reading.
- **Order by the snapshot's own `timestamp`, not by file mtime.** mtime says
  which rollout was written to last, which on a machine running several
  sessions is whichever one emitted any event last — not the one holding the
  newest usage. mtime is still sound as an *upper bound*, since no line can
  post-date its file's last write, and the scan uses it to stop early.

### Polling usage live, measured 2 Sep 2026 (CB-85)

**`codex app-server` answers a rate-limits request with no session, no model
call and no credential read.** This was the open question below for two
tickets; it is now measured, and the orb no longer has to wait for a session to
happen.

Spawn `codex app-server`, then write newline-delimited JSON-RPC on stdin:

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"claude-buddy","title":"Claude Buddy","version":"1"}}}
{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read","params":null}
```

The `id: 2` response carries `result.rateLimits`, plus a `rateLimitsByLimitId`
map this app does not yet model:

```json
{"rateLimits": {"limitId": "codex", "planType": "team",
  "primary":   {"usedPercent": 100, "windowDurationMins": 300,   "resetsAt": 1788391232},
  "secondary": {"usedPercent": 38,  "windowDurationMins": 10080, "resetsAt": 1788807866},
  "credits": {"hasCredits": false, "unlimited": false, "balance": null},
  "spendControlReached": false, "rateLimitReachedType": "workspace_owner_credits_depleted"}}
```

Three things that are not obvious and cost time to find:

- **The field names are camelCase here and snake_case in the rollout.**
  `usedPercent` / `used_percent`, `windowDurationMins` / `window_minutes`,
  `resetsAt` / `resets_at`, `planType` / `plan_type`, `hasCredits` /
  `has_credits`. Same numbers, two transports, and nothing reconciles them —
  `CodexUsageParse` reads both spellings for every field.
- **Do not close stdin.** `UsagePoller` closes it so `claude` knows no more
  requests are coming and exits; `codex app-server` treats a closed stdin as
  shutdown and exits *before* answering. The first version of this printed
  nothing at all. Write the requests, read the response, kill the process.
- **`CODEX_HOME` selects the account**, the way `CLAUDE_CONFIG_DIR` does for
  Claude Code. It is the only reason a second Codex account can be polled.

### `CODEX_HOME` and the default home (CB-203)

**Setting `CODEX_HOME` to `~/.codex` behaves the same as leaving it unset**, so CB-42's split does not exist for Codex. Measured 2 Oct 2026: on the MacBook (codex-cli 0.160.0) and the Windows box (0.148.0), `codex login status` reports `Logged in using ChatGPT` both ways against the real home, and an empty `CODEX_HOME` reports `Not logged in` as the negative control. Under a scratch `$HOME` via `env -i`, unset and `CODEX_HOME=$HOME/.codex` created identical trees apart from one randomly named `tmp/arg0/codex-arg0*` directory.

**A `CODEX_HOME` that does not exist is refused**, on both platforms: `Error loading configuration: CODEX_HOME points to "…", but that path does not exist`. Codex creates nothing in that case. Because the dialog launches `exec codex`, a Codex that exits at once can take its tmux window or terminal with it before the error is read, so the New chat dialog warns under the Account picker when the chosen home is missing (`NewChatAccountWarning`), and leaves Start enabled. Grok creates a missing home instead.

The New chat dialog still sets nothing for the default account — one rule for all three CLIs, and a `CODEX_HOME` Buddy itself was launched under reaches the chat unchanged.

Measured at ~800ms end to end, and it returned 100% of the five-hour window
while the newest snapshot on disk still read 99% from three hours earlier — so
it is genuinely live rather than a replay of the same file.

`account/rateLimits/updated` is a *server push* on the same connection. Nothing
uses it yet; a long-lived connection would let the orb update when Codex says so
rather than on a five-minute timer.

The usage orb tries this first and falls back to the newest windowed snapshot
across the rollout tree, carrying that snapshot's timestamp so the card can date
the number rather than the read. It does not call `/status`, does not hit
chatgpt.com, and does not read `auth.json`'s `OPENAI_API_KEY`, `access_token` or
`refresh_token`.

It does read two identifier strings out of `tokens` since CB-340 —
`tokens.account_id` and the `chatgpt_user_id` claim of `tokens.id_token`,
nested under `https://api.openai.com/auth` on the machine measured — so one
Codex account can be recognised across PCs. The old rule ("never `tokens`")
existed because a second reader that loses a compare-and-swap on a rotating
token logs the user out; that is an argument against refreshing or writing a
token, and reading an identifier does neither. Only an HMAC of the two ids
leaves the desktop. See `docs/account-identity-findings.md`.

## Still unknown

- On Windows: `install-codex-hooks.ps1`'s install path and the hook script's
  own `-Agent codex` branch have never run, because no runner or machine here
  has Codex installed. CI does cover the wrapper, the installer wiring and the
  skip-when-absent path.

Not measured. Do not write these down as facts until they are.

- Whether a hook declared `async: true` still receives its payload on stdin. It
  is accepted and stored — see above — but nothing here has watched one fire.
  This is the one thing the amber "waiting" state depends on.
- Whether `allowManagedHooksOnly` is set on any machine this ships to.
- What accepting the trust prompt actually requires of the user, and whether it
  can be done without an interactive TUI.
- What `PermissionRequest` stdin actually contains for a shell approval —
  specifically whether `tool_input` carries a `cwd`-like key, which would defeat
  a greedy `sed` extraction.
- Whether the hook's ancestor walk lands on the `codex` process, and what
  `$SHELL -lc` adds to the chain.
- Whether a side conversation shares a pid or a session id with its parent.
- Whether compaction rewrites a rollout in place — a reader that treats a
  shrinking file as "start over" depends on the answer.
- Whether `rateLimitsByLimitId` ever carries more than one bucket, and what a
  second `limit_id` would mean for an orb that draws two rings. Only `codex` has
  been seen.
- Whether `account/rateLimits/read` works against an app-server old enough not
  to know the method, and what it answers if not. The fallback covers it either
  way, but the shape of the failure has not been seen.
- What `credits.balance` is denominated in, and whether a cap ever arrives
  beside it. No snapshot here had `has_credits: true`.
- A second *logged-in* `CODEX_HOME` account. CB-203 measured the default-home rule on Windows and macOS with scratch homes, which shows where Codex reads from, not that a second subscription polls correctly.
