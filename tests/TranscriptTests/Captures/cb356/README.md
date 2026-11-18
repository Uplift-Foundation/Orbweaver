# CB-356 captures: a compaction as Claude Code writes it

Each file is a short run of transcript rows around one compaction: a normal row before, the `compact_boundary` row, whatever sat between, the `isCompactSummary` row, and a normal row after. Every file not named `synthetic-*` comes from a real Claude Code transcript (2.1.296 to 2.1.319), taken 2026-11-17 from two Macs.

## What was changed in the real rows

Scrubbed default-deny: every string value was replaced unless its key is structural (`type`, `subtype`, `role`, `level`, `userType`, `entrypoint`, `version`, `trigger`, `timestamp`, `renderedRole`, `stop_reason`, `sessionKind`, `turnOrigin`). Uuids are sequential placeholders (consistent within a file, so `parentUuid` and `preservedSegment` links still line up), `cwd` is `/Users/dev/project`, `gitBranch` is `main`. Keys, key order, value types, numbers, booleans and compact JSON spacing are exactly what Claude Code wrote. `compactMetadata`'s numbers are real.

The summary's text is rebuilt, not copied:

- Real, byte for byte: the opening sentence, the `<artifact-content-authored-by-others/>` prelude where the row had one, the number and position of `<system-reminder>` blocks, and the closing paragraphs from "If you need specific details from before compaction" to the end (with the transcript path replaced).
- Invented: the list of tool names inside `<tools-used-before-summary>` and the whole summary body between the scaffolding. The body is neutral Markdown with headings, a list, a code block and bold text so the expanded view has something to render.

Neighbouring rows keep their keys; their message text is one neutral sentence. `attachment` rows keep their keys and types with every string neutralised.

## Real rows

| File | Rows | What it is |
| --- | --- | --- |
| `auto-adjacent` | assistant, boundary, summary, assistant | `trigger: auto`. The summary directly follows the boundary and its `parentUuid` is the boundary's `uuid`. One `<system-reminder>` block. |
| `auto-attachments-between` | assistant, boundary, 3 attachment, summary, assistant | `trigger: auto`. Three `attachment` rows between boundary and summary; the summary's `parentUuid` is not the boundary. No `<system-reminder>` block. |
| `auto-artifact-prelude` | boundary, 3 attachment, summary, assistant | The summary opens with `<artifact-content-authored-by-others/>` and a sentence before the usual opening, so it does not start with "This session is being continued". |
| `auto-two-reminders` | assistant, boundary, 3 attachment, summary, assistant | Two `<system-reminder>` blocks in one summary (1 real row of 77 had two). |
| `manual` | user, boundary, summary | `trigger: manual`, from the Mac mini: the only manual compaction found on either machine. |

## Synthetic rows, and why each is not real

All are `auto-adjacent` with one change.

| File | Change | Why synthetic |
| --- | --- | --- |
| `synthetic-orphan-summary` | boundary row removed | 0 of 77 real summaries lacked a boundary; a tail read that starts between the two produces this. |
| `synthetic-orphan-boundary` | summary row removed | 0 of 77 real boundaries lacked a summary; a read that ends between the two produces this. |
| `synthetic-unknown-trigger` | `trigger: "scheduled"` | A value Claude Code does not write today. |
| `synthetic-idle-trigger` | `trigger: "idle"` | The ticket names it; no row on either machine carries it, and the 2.1.327 binary does not show whether a row ever does (see the plan, section 1). |
| `synthetic-no-metadata` | `compactMetadata` removed | All 77 real boundaries had it. |
| `synthetic-no-post-tokens` | `postTokens` removed | All real rows had it; the binary sets it after the row is built, so a row without it is possible. |
| `synthetic-summarize-metadata` | summary has `summarizeMetadata` and no `isVisibleInTranscriptOnly` | The binary writes this shape for a partial summary; none was found on disk. |
| `synthetic-snake-case-metadata` | `compact_metadata` with `pre_tokens`, `post_tokens`, `duration_ms` | The SDK/stream form of the row. The field names are read off the 2.1.327 binary's schema; no row or event carrying them was captured. |
| `synthetic-control-user-quotes-summary` | a plain `user` row whose text opens with the summary's first sentence, no `isCompactSummary` | Negative control: must stay a user bubble. |
| `synthetic-control-visible-only` | `isVisibleInTranscriptOnly: true` without `isCompactSummary` | Negative control: no real row has this (77 of 77 rows with the flag are summaries); the parser must key on `isCompactSummary` only. |
