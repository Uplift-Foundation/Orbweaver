# CB-330 captures: teammate and peer messages as Claude Code writes them

One transcript row per file. Every file not named `synthetic-*` is a real row from a Claude Code transcript under `~/.claude/projects` on one Mac, Claude Code 2.1.303 to 2.1.326, taken 2026-11-16. The shortest row of each variant was chosen, and rows whose text named a person, a home path, a device or a credential were skipped rather than edited.

## What was changed in the real rows

Row-level identifiers only. `uuid`, `parentUuid`, `promptId` and `sessionId` are replaced with sequential placeholder ids, `cwd` is `/work/orbweaver`, `gitBranch` is `develop`, `slug` and `teamName` are placeholders, a cloud session id is `session_01FIXTURE`, a machine hostname is `example-macbook`, and `msg_id` is a placeholder. `message.content` is byte-for-byte what Claude Code wrote, apart from that hostname and session id. Key order and compact JSON spacing are preserved, because `ChatTranscript.IsInteresting` is a substring test on `"type":"user"`.

## Real rows

| File | Row | What it is |
| --- | --- | --- |
| `lead-idle-result` | plain `user`, no `isMeta`, no `origin` | Peer wrapper around one tag; `idle_notification`, `idleReason: available`, `result`. The shape in the ticket's screenshot. |
| `lead-idle-summary-suffixed-name` | same | `idle_notification` with a `summary` key as well, from a respawned member named `<name>-2` (CB-288). |
| `lead-idle-failed` | same | `idleReason: failed` with `failureReason` and no `result`. |
| `lead-idle-markdown-result` | same | A multi-line `result` with a bullet list, bold and code spans. |
| `lead-shutdown-approved` | same | `shutdown_approved`. |
| `lead-teammate-terminated` | same | `teammate_terminated`; `teammate_id="system"`, no `color`. |
| `lead-plain-text-summary-attr` | same | Plain text body; `summary="…"` attribute. |
| `lead-plain-text-entity-in-summary` | same | `summary` attribute carrying `&apos;` and `&quot;` and truncated with `…`; the body is not entity-escaped. |
| `lead-multi-terminated-and-approved` | same | Four tags in one row, separated by a blank line. |
| `member-lead-plain-summary` | plain `user` in a team member's transcript (`agentName`, `teamName`) | No peer wrapper at all: the row is the tag. From `team-lead`, no `color`. |
| `member-lead-shutdown-request` | same | A tag with no attributes but `teammate_id`; the body is one line of JSON (`shutdown_request`) followed by a paragraph of prose. |
| `member-peer-teammate-color-summary` | same | From another member, with `color` and `summary`. |
| `member-multi-mixed-senders` | same | Two tags, one from the lead and one from a member. |
| `peer-meta-bare-slash` | `isMeta: true`, `origin: {"kind":"peer","from":"unknown"}` | The CB-266 shape: bare text between the prefix and the provenance paragraph. |
| `peer-meta-cross-session-own-app` | `isMeta`, `origin.from` this app | A `<cross-session-message>` tag this app wrote (`SessionMessenger`). |
| `peer-meta-cross-session-other-session` | `isMeta`, `origin.from` `bridge:session_…`, with `name`, `fromMode`, `body` | A `<cross-session-message>` tag from another Claude session. |

## Synthetic rows, and why each is not real

| File | Why |
| --- | --- |
| `synthetic-queued-enqueue-lead-idle` | No real `queue-operation` row carried either wrapper: 0 of 493 `enqueue` rows with content, in the same files the 2,957 teammate rows came from. The keys are a real `enqueue` row's; the content is `lead-idle-result`'s. It exists because the ticket requires the queued path to be covered. |
| `synthetic-control-user-quotes-wrapper` | Negative control: a person quoting the prefix and a tag in the middle of a sentence. Must stay a user bubble. |
| `synthetic-unknown-payload-type` | A payload `type` nobody has seen, inside the real wrapper. |
| `synthetic-malformed-json` | A JSON body cut off mid-string, inside the real wrapper. |

## Not captured

A lead-to-member message with no `summary` attribute and a plain-text body exists (58 single-tag rows) but every one is a spawn brief of 1,700 characters or more naming a home path, so none is here. `member-lead-shutdown-request` covers the attribute-less tag.
