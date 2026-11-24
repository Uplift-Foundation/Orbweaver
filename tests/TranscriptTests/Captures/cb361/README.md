# CB-361 fixtures: past sessions on disk

Real transcripts from the Mac mini, captured 2026-11-23 and scrubbed. Each tree
mirrors the layout the CLI writes under its own home, so an enumeration test can
point a root at `claude/`, `codex/` or `grok/` and walk it.

| Path | Source | What it is |
| --- | --- | --- |
| `claude/projects/-Users-user-Source-project/0d249aed-….jsonl` | Claude Code 2.1.330, `entrypoint: cli` | A whole interactive session, 36 rows. Its first user row is a `<teammate-message>` (a team member's session), and it carries an `ai-title` row. `cwd` first appears on the first `user` row, not on the rows before it. |
| `claude/projects/…/a6b87b1e-….jsonl` plus `a6b87b1e-…/subagents/agent-….jsonl` and `.meta.json` | Claude Code 2.1.310, `entrypoint: sdk-cli` | A session with a sub-agent sibling directory. The sub-agent file sits two levels below `projects/<slug>/`, opens with a `fork-context-ref` row, and every later row has `isSidechain: true` and the PARENT's `sessionId`. |
| `codex/sessions/2026/11/22/rollout-….jsonl` | Codex 0.154.0, `originator: codex-tui` | A whole interactive rollout, 17 rows. `session_meta` is row 0. The first `role: user` rows are context blocks that open with a tag (`<environment_context>` and the like); the typed prompt comes after them. |
| `grok/sessions/%2FUsers%2Fuser%2FSource%2Fproject/0570de7d-…/` | Grok 1.0.13 | `updates.jsonl` (57 rows) and `summary.json`. The first three rows are hook rows; the first `user_message_chunk` is row 3. |

## What was changed

- Every free-text string is `scrubbed`. A user prompt is `Scrubbed user prompt`,
  a title is `Scrubbed session title`. Text that opened with a tag keeps the tag
  name only, as `<tag>scrubbed</tag>`, because readers branch on it.
- Home paths became `/Users/user/Source/project`; the Claude slug and the Grok
  directory name were rewritten to match.
- Every UUID was remapped, consistently across files, so a file name, the
  `sessionId` rows and a sub-agent's parent id still agree.
- Closed-vocabulary values (`type`, `role`, `entrypoint`, `originator`,
  `sessionUpdate`, `method`, versions) and all timestamps and numbers are real.
- The two files of the sub-agent session are trimmed: the parent keeps its first
  14 rows, its title rows and its last 4; the sub-agent keeps its first 6 and
  last 2. The other three transcripts have every row.

## What the fixtures do not show

Scrubbing shrinks rows, so sizes here are not the real ones. On the mini, row 0
of a Codex rollout (`session_meta`, which carries `base_instructions`) was larger
than 64 KB in 1206 of 1678 files, all of them written by OpenClaw; the 14
`codex-tui` files topped out near 22 KB. A test of the head read needs a
synthetic long row; these files will not exercise it.
