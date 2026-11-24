# Paperclip chat: see what an agent is doing and message it (CB-443)

Architect: Felicity Marchetti. Lead: Jennifer Voss. Written against Orbweaver `origin/develop` at `02f79fe83` and Orbweaver-Mobile `origin/develop` at `1a872f4`. Extends `docs/plans/paperclip-orbs-plan.md`; the measurements it rests on are in `docs/paperclip-findings.md`.

**The scope fork is not answered.** The owner was asked what "chat" means for an agent that sleeps between runs and has not replied. This plan is built on the lead's recommendation: **a message is a comment on the agent's current task**, shown as a chat, with "waking…" and then the reply; when the agent has no task, the message opens one. The other branch is costed in §9 so the owner can still choose it. If the owner picks it, §2 and §3 stand and §4 is redone.

**Status words.** *Measured* means run against the real Paperclip (2026.1117.0, on the mini) by the lead or recorded in the findings file. *Read from source* means I read it in this repository or the mobile one and did not run it. *Assumed* means neither. §10 lists every assumption and the request that settles it; the batch is with the lead and none of it had come back when this was pushed.

## 1. Who it is for and what they see

Someone watching their Paperclip company as orbs. Today an orb says working, asleep, needs you or broke, and a click opens Paperclip's web page. They asked for two things without leaving Orbweaver: to read what the agent did, and to tell it something.

### 1.1 The panel

Clicking a Paperclip orb opens the chat panel, in the place a terminal session's chat opens. It shows **one task's thread**: the task the agent is on now, or the last one it worked on.

Top to bottom:

- **A task line**: "CB-12 Triage the login bug · in progress". One line, the first row of the thread.
- **The thread, oldest first.** A person's comment is a bubble on the right. The agent's comment is a bubble on the left. Another agent's comment is a muted line with that agent's name.
- **The newest run's activity, in place in the thread**: each tool call as a muted line ("Bash: git status"), red when the tool failed; what the agent said between tool calls as its bubbles; the run's answer as its last bubble.
- **"Open in Paperclip"** above the message box, always.
- **The message box.**

### 1.2 States

The state shows in two places only: the orb the person already reads, and the words in the empty message box. The thread never gets a "status" row, because a row that later vanishes cannot be sent to a phone.

| State | Thread | Message box says |
| --- | --- | --- |
| Asleep, has a task | the task's thread and the last run's activity | "Message <name>. It wakes to read it." |
| Asleep, no task | "No task yet." and the last run's result when there is one | "Message <name>. This opens a new task." (or no box, see §4.4) |
| Working live | tool lines and speech appear as they happen | "<name> is working. It reads a message when this run ends." |
| Just messaged | the person's bubble | "Waking <name>…" until a run shows, then the Working words |
| Waiting on a sub-task | the thread; a muted line naming the task it waits on, when Paperclip says | the Asleep words |
| Needs you | the thread; the amber flyout keeps Approve and Decline exactly as today | "Message <name>. It wakes to read it." |
| Broke | the thread; the failed run's activity with its red lines; the orb's own one-sentence reason as a red line | the Asleep words |
| Paused | the thread | "<name> is paused in Paperclip. It reads this when resumed." |
| Key refused | what was last read, frozen | no box; "Reconnect to Paperclip", with the link |

### 1.3 What a message does

1. The person types and sends. The comment is posted to the task in Paperclip.
2. Their bubble appears only when Paperclip has accepted it. A refusal leaves the text in the box and says why in a red line.
3. The box says "Waking <name>…". Measured: a comment by a board user woke the assigned agent within 10 s, three times.
4. The orb turns to working; tool lines and speech arrive in the thread.
5. The run ends; the agent's answer is the last bubble. It is a real comment in Paperclip, so Paperclip's own page shows the same exchange.

If no run shows within 30 s the box says "<name> hasn't woken yet. Wake it from the orb's menu." Nothing is retried and nothing else is sent.

### 1.4 The phone

Opening the agent shows the same thread, read from the desk, and a message box that does the same thing. The phone gets the same rows the desktop panel shows, because the desk builds them once. After a send the phone says "Sent. Waking <name>…" until the row leaves idle or 30 s pass. Approve and Decline stay where they are (the prompt card and the push).

### 1.5 Deliberately left out

- A tool call's output. The line says what ran and whether it failed. Outputs are large and are one click away in Paperclip.
- More than one task per agent, and any task picker. The panel follows the current task.
- Activity of runs older than the newest one on the task. Their comments still show.
- Stopping a run from the panel, attachments, images, slash commands, model or effort controls.
- Wake, Pause and Resume in the panel. They stay on the orb's menu.
- Editing, deleting or reacting to comments; mentions; reassigning or closing the task.
- Any notification for a reply. Quiet by default: a finished run never buzzes.
- Streaming text letter by letter. Speech appears a sentence or a paragraph at a time, as Paperclip records it.

## 2. Design: the activity view

### 2.1 Shape

```
PaperclipSessions (existing poll)        snapshot: agents, live runs, task titles
        |
PaperclipChatFeed   (new, one per open agent; runs only while opened)
        |  reads: comments, the newest run's events, the task row
PaperclipChat       (new, pure)          JSON in, rows out; which task; what the box says
        |
PaperclipChatSession (new)               IRemoteChatSession over the rows
        |                                   \
ChatPanel (unchanged)                    RelayRemote (slice 2): window frames from the same rows
```

Three new files and no change to `ChatPanel`. The session implements `IRemoteChatSession`, `IRemoteChatComposer`, `IRemoteChatComposerChanges` and `IRemoteChatReadOnly` (all in `RemoteChat.cs`, read from source). `IRemoteChatReadOnly.ReplyUrl` is the "Open in Paperclip" link above the box. Nothing else optional is implemented, which is what leaves out stop, images and run options without any code.

`ClaudeCloudChatSession` is the model: a session object cached per orb in `SessionManager`, a pure layer under it, a seam for the dispatcher so it tests without an Avalonia app.

### 2.2 Which task (`PaperclipChat.CurrentTask`, pure)

First that exists:

1. the live run's `issueId` (measured: live-runs rows carry it);
2. the agent's blocked task that needs a person (already in the poll);
3. the newest finished run's `issueId` (already parsed, `RawRun.IssueId`);
4. the newest task assigned to the agent that is not done (**assumed**: needs an `assigneeAgentId` filter on the issues list, §10 E12);
5. none.

The task can change while the panel is open (a run starts on another task). The thread is then replaced whole and the panel told through `HistoryReplaced`, which means the session also implements `IRemoteChatBacklog` with `HasMore` false. One event, no paging.

### 2.3 Reads and cadence

Only while a panel is open, or a phone is watching (slice 2). A closed panel costs nothing.

| Read | When | Cost |
| --- | --- | --- |
| `GET /api/issues/{id}/comments` | on open, every 4 s while open, at once after a send, at once when the run ends | measured to exist and to carry `body`, `authorAgentId`, `authorUserId`, `authorType`, `createdByRunId`; size and time on a long thread **not measured** (C7) |
| `GET /api/heartbeat-runs/{runId}/events?limit=500` for the newest run on the task | every 2 s while that run is live; once when it has finished, then kept | measured 8 to 34 ms for about 100 events on loopback; LAN **not measured** (F14) |
| `GET /api/issues/{id}` or the list row, for the task line | on open and when the task changes | **assumed** to exist; falls back to the title the poll already holds |

Per open panel that is 15 reads a minute asleep and 45 a minute during a run, on top of the 105 a minute the connected app already makes (findings, Timing). The feed does not share the poll's own events read (CB-442 reads each running run every 4 s for permission prompts): two readers of one body would couple the prompt path to the panel, and the saving is one read in three.

If Paperclip lets events be read after a `seq` (D9), the feed asks for the new ones only. If it does not, it re-reads the 500. **A run with more than 500 events is the open risk**: which 500 come back is not measured. Until D9 answers, a run whose read returns exactly 500 rows shows a muted line "Earlier activity is in Paperclip." and the link.

A failed read keeps what is on screen and tries again at the next tick. A 401 or 403 is the existing reconnect state (`PaperclipLink.NeedsReconnect`); the feed stops and the box is replaced by the reconnect words.

### 2.4 Rows (`PaperclipChat`, pure)

```csharp
internal static class PaperclipChat
{
    internal sealed record Comment(string Id, string Body, string? AuthorAgentId, string? AuthorUserId, string? RunId, DateTime? At);
    internal sealed record TaskRef(string Id, string Identifier, string Title, string Status);
    // Key is stable for the life of the thing it names: "task", "c:<commentId>", "r:<runId>:t:<executionId>", "r:<runId>:s:<itemId>", "r:<runId>:result".
    internal sealed record Row(string Key, ChatRole Role, string Text, DateTime At, bool Error = false);

    internal static IReadOnlyList<Comment> ParseComments(string? json);
    internal static IReadOnlyList<Row> ParseActivity(string runId, string? eventsJson);
    internal static TaskRef? CurrentTask(/* agent id, live runs, finished runs, blocked issues, assigned issues */);
    internal static IReadOnlyList<Row> Build(TaskRef? task, string agentId, IReadOnlyDictionary<string, string> agentNames,
        IReadOnlyList<Comment> comments, IReadOnlyList<Row> activity);
    internal static IReadOnlyList<Row> Merge(IReadOnlyList<Row> shown, IReadOnlyList<Row> built);   // §2.5
    internal static string Hint(/* agent state, status, link, awaiting-wake since, now */);
}
```

`ParseActivity`, from one run's events:

| Event | Row | Status |
| --- | --- | --- |
| `tool.execution.progressed` with a `subject` | System, "<name>: <subject>", key by `executionId`. The name is on the `started` event with the same `executionId`. | shape measured (CB-442 uses exactly this join) |
| `tool.execution.started` with no `progressed` yet | nothing yet; the line appears when the command is known, or at `completed` as "<name>" alone | decided, so a line's text never changes after it is shown |
| `tool.execution.completed` | sets `Error` on that tool's row when `status` says it failed | `status` values **not read** (D8) |
| `item.completed` | Assistant, the agent's spoken text, key by `itemId` | **shape not read** (D8). Until it is, this arm is written against the real body, not guessed |
| `item.delta` | ignored | decided: no streaming |
| `run.result.proposed` | Assistant, `payload.summary`, key `result`. Dropped when a comment carries this run's id in `createdByRunId` (measured: the answer is stored as a comment) | measured |
| anything else, and `"payload": null` (measured to occur) | skipped | |

`Build` puts the task row first, then comments and activity by time. A comment by this agent is Assistant; by a user is User; by another agent is System, "<that agent's name>: <body>". Every text is cut at 8,000 bytes, the phone contract's line limit, so one rule serves both.

The parsers never throw, as `PaperclipApi`'s do not. They belong in a new file, not in `PaperclipApi.cs`, which is 693 lines and has a different job.

**What is shown and what is never written down.** `PaperclipApi` refuses to read a run's `error` text because measured rows carry commands. This feature shows commands on purpose; that is the requirement. So: command text and comment bodies go to the panel, and to the phone sealed; they are never logged, never put in a roster row, a hover or a push. The transport already logs path, status, size and time only.

### 2.5 Keeping the list stable (`Merge`)

The panel and the phone both want a list that only grows at the end. So once a row is shown its place is fixed: `Merge` keeps the shown rows in order, updates one in place when its text or error changed, and appends rows it has not seen, in time order among themselves. Only a change of task replaces the list. At most the newest 200 rows are held, the phone's backfill size.

The session turns the difference into `TurnAdded` and `TurnUpdated` on the UI thread (requirements 1 and 2 on `IRemoteChatSession`).

## 3. Design: sending

Two calls added to `IPaperclipActions`, implemented in `PaperclipActions` beside approve and wake, so the phone path and the panel share one implementation and one fake:

```csharp
Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct);
Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct);
// PaperclipSent(PaperclipOutcome Outcome, string? CommentId, string? IssueId)
```

`CommentAsync` is `POST /api/issues/{id}/comments {"body": text}`. **Measured as a board user in a browser session; not measured with the app's board key (B1). That one call is the feature's only unproven write and is the first thing to settle.** Every other write the app makes with that key works (approve, reject, wake, pause, resume, resolve), which is a reason to expect it and not a measurement.

| Outcome | When | What the person sees | `SendAsync` returns |
| --- | --- | --- | --- |
| Sent | 2xx | their bubble; "Waking <name>…" | `Sent` |
| Sent, agent busy | 2xx while a run is live | their bubble; "<name> is working. It reads a message when this run ends." | `Sent` |
| Sent, agent paused | 2xx, agent `paused` | their bubble; the Paused words | `Sent` |
| No task | no current task | §4.4 | |
| Refused | 4xx other than 401/403 | red line with Paperclip's own sentence when the body has one, else "Paperclip refused that."; text stays in the box | `Failed` |
| Needs reconnect | 401, 403 | red line "Reconnect to Paperclip to message <name>."; text stays | `Failed` |
| Unreachable | timeout, refused, 5xx | red line "Can't reach Paperclip."; text stays | `Failed` |

"Agent busy" is worded as a fact about delivery that is **assumed** (B3): what Paperclip does with a comment that arrives mid-run has not been seen. If it turns out the running run reads it, only the sentence changes.

The person's bubble is raised by `SendAsync` on a 2xx (requirement 3), keyed by the comment id from the response, so the next comments read finds the same key and adds nothing. If the response carries no id (**assumed** that it does), the next read adopts the newest unkeyed user bubble with the same text.

The 20 s `ActionTimeout` and the `PollNow` nudge after a settled call are reused, so the orb reacts at once.

### 3.1 No current task

`OpenTaskAsync` creates a task assigned to the agent: title is the message's first line cut to 80 characters, description is the whole message. **Nothing about this call is measured** (E13): the route, whether a board key may call it, whether assignment wakes the agent. It ships in slice 1 only if E13 comes back clean. If it does not, an agent with no task shows no message box and the words "<name> has no task. Give it one in Paperclip.", with the link. That is honest on its own and loses only the least common case.

## 4. Design: wiring on the desktop

| File (line at `02f79fe83`) | Change |
| --- | --- |
| `SessionManager.cs:3888` | `RemoteChatFor` returns a cached `PaperclipChatSession` for a Paperclip orb instead of `null`. Cached per session id like `_cloudChats`; dropped when the orb goes. |
| `ClickRouting.cs:165` | `OpensInABrowser` is `ClaudeCloud` only. |
| `OrbWindow.axaml.cs:4209` area | A click on a Paperclip orb opens the panel. The amber flyout with Approve and Decline is unchanged. "Open in Paperclip" stays on the menu. |
| `ChatPanel.axaml.cs:784`, `:933` | The panel tells a cloud session it opened and closed by type test. Lift those two calls to a small `IRemoteChatPanelLifetime { PanelOpened(); PanelClosed(); }` that both sessions implement, so the feed starts and stops with the panel. The only `ChatPanel` edit. |
| `PaperclipSessions.cs` | Expose what the session needs from the poll's state: the agent's row, its live run, its newest finished run, its blocked task. Read-only. |
| `README.md` | The Paperclip section: click opens the panel; a message is a comment on the task. |

Settings: none. No switch for this.

## 5. Design: the phone

### 5.1 Can the existing frames carry it?

**Yes for the shapes, no for the rules.** No new frame type and no relay change; one new contract file because three rules change and one refusal is added.

Read from source at Orbweaver-Mobile `1a872f4`:

- `window` (frames-v4) already carries `lines` of `{role, text, at, error, cut}` with role `user`, `assistant` or `system`. That is exactly a `PaperclipChat.Row`.
- Both phones store a window by index (`TranscriptStore.swift:46`, `ClientFrames.kt:223`), so a window that restates several earlier rows is applied correctly. Not run.
- `input` is `{t, session_id, text}` with `ok typed`. Nothing to add.
- The relay routes on the envelope and never opens `ct` (frames-v4 note, frames-v17 `layers`). A new refusal code inside `ct` needs no relay deploy.
- Today a Paperclip row answers `watch` with `total 0` and no window, and `input` with `err untypeable` (`RelayRemote.cs:502`, `:566`). Both phones hide the composer for `cli == "paperclip"` (`ClientFrames.swift:374`, `Shell.kt:126`), and iOS refuses to send while a row is `waiting` (`SessionScreen.swift:374`).

### 5.2 `frames-v18.json` (Orbweaver-Mobile, lands first)

Additive over frames-v4, v11 (caps) and v15. Nothing on the wire carries 18. If another branch has taken the number, use the next.

| Rule | Text |
| --- | --- |
| caps | A desk that can show and message Paperclip agents adds the name `paperclip_chat` (sorted in byte order with the others). |
| `watch` on a paperclip row, desk with the cap | `ok watching` with `total` = the row count, then `window` frames as frames-v4: backfill of up to 200 lines, `live:false`, then tails. Line numbering is the thread's row index. `reset:true` when the agent's task changed. |
| tail rule for a paperclip row | The desk sends from the **first index whose role, text or error changed**, through the end. So a tail may restate more than the last row. (frames-v4 compares role and text only and restates at most one row; a tool line turning red would never reach the phone under that rule.) |
| `input` on a paperclip row, desk with the cap | Posts the text as a comment on the agent's current task. `ok typed` on a 2xx; the line then arrives in a tail window as a `user` line. Text limits and `bad_input` as frames-v4. |
| new refusal `send_failed {why}` | The comment was not posted. `why` is the desk's sentence: reconnect, can't reach, refused, or no task. Phone copy: the `why` itself, then "Nothing was sent." |
| phone | For a paperclip row on a desk whose latest caps names `paperclip_chat`: send `watch` when the screen is in front; show the composer; **allow sending while the row is `waiting`** (a blocked agent is the one a person most wants to tell something). After `ok typed`, show "Sent. Waking <title>…" until the row's state is not idle or 30 s pass. Without the cap: exactly today's read-only row. |
| old builds | An old phone never sends `watch` or `input` to a paperclip row and sees no change. A new phone never sends either to a desk without the cap. An old desk answers as today. |
| pushes, activity counts | unchanged from v15 and v16. A reply never pushes. |

Vectors (`frames-v18-vectors.json`, with `make_v18_vectors.py`, `check_v18_vectors.py`, `test_check_v18_vectors.py`, in the shape of v17's): the caps frame; a backfill window of a task row, a user line, two tool lines and an assistant line; a tail that restates two rows with one turned `error:true` and appends one; a `reset` window; `input`, `ok typed`, and `err send_failed` with each of the four sentences.

### 5.3 Desk side (Orbweaver)

| File | Change |
| --- | --- |
| `RelayRemote.cs:497` (`Watch`) | For a `Listed` row: open that agent's `PaperclipChatSession` through a seam, answer `total`, send the backfill, and subscribe to its changes; send tails under §5.2's rule. `Unwatch`, a phone going offline, and the row leaving the roster close it. A phone watching counts as "open" for the feed. |
| `RelayRemote.cs:562` (`InputAsync`) | For a `Listed` row: `SendAsync` on that session; `Sent` is `ok typed`, `Failed` is `err send_failed` with the session's sentence. |
| the caps frame's builder | Add `paperclip_chat` when Paperclip is connected. |
| a new small pure class `PaperclipWindowDiff` | §5.2's tail rule: previous rows and new rows in, `(from_line, lines, reset)` out. Chunking reuses the existing window chunker. |
| `tools/relay-vectors` and the desk's copy of the vectors | Byte-identical to the mobile copy, as every earlier version. |

### 5.4 Phones

iOS and Android each: gate the composer and `watch` on the cap instead of on `cli`; lift the waiting guard for a paperclip row; the `send_failed` copy; the "Sent. Waking…" line; snapshot or UI test of an agent screen with a thread and a composer, and of the same row on a desk without the cap. The watch app shows no thread and is unchanged.

## 6. What ships when

| Slice | Contents | PRs |
| --- | --- | --- |
| **1. Desktop** (CB-443) | §2, §3, §4: the panel with the thread and live activity, the message box, every state in §1.2. The no-task send only if E13 is clean. | one PR to Orbweaver `develop`, branch `feature/paperclip-chat-desktop` |
| **2. Phone** (its own ticket under CB-419, to be filed by the lead) | §5 | contract PR to Orbweaver-Mobile first; then the desk PR and the two phone PRs in parallel against the merged contract |

Slice 1 is honest alone: requirement 4 (the phone) is the only part missing, the phone keeps today's read-only row, and nothing on the desktop promises otherwise. Each slice is sized to an afternoon.

Cut to fit that: tool output, multiple tasks, older runs' activity, streaming, the panel's own wake/pause buttons, sharing the poll's events read, a status row in the thread, and any reply notification (§1.5).

## 7. Staffing

Known shape, no new subsystem; this plan is the architect stage. The lead spawns engineers and QA together.

**Slice 1: two engineers and QA.** The seam between the engineers is §2.4's signatures and §3's two calls, fixed here so neither waits.

- **Engineer A, the pure half and the wire.** `PaperclipChat.cs` (parsers, `CurrentTask`, `Build`, `Merge`, `Hint`), `CommentAsync` and `OpenTaskAsync` in `PaperclipActions`, `PaperclipChatFeed.cs` (the timed reads over `IPaperclipApi`), fixtures cut from the lead's real bodies, unit and integration tests, and two rows in `tools/paperclip-probe` (comments, events).
- **Engineer B, the session and the panel.** `PaperclipChatSession.cs` against a fake feed and the fake `IPaperclipActions`, the §4 wiring, UI tests, the screenshot captures, the README.
- **QA.** Writes the matrix from §1.2 and §3 while they build; coverage of added lines; installs (one install owner: QA); the real-machine proof in §8.4.

**Slice 2: one contract author, then three engineers and QA.** Contract and vectors (one engineer, about an hour, merged first). Then desk relay side, iOS, Android in parallel. QA proves it on a real phone against a real desk.

## 8. Tests

Target is 100% of added lines at the three levels. Fixtures come from the lead's captured bodies, scrubbed, under `tests/UnitTests/Fixtures/paperclip/native/chat/`; a fixture that was edited by hand says so in its name, as the tool-action ones do.

### 8.1 Unit (`tests/UnitTests`)

- `ParseComments`: the real list; a user comment, this agent's, another agent's; missing fields; not an array; malformed JSON.
- `ParseActivity`: the real run body end to end (the expected rows written out by hand from reading it); a tool with `progressed`; one with only `started`; one that only completes; a failed tool; `item.completed`; `item.delta` ignored; the result dropped when a comment has the run's id, kept when none has; `"payload": null`; an unknown event; malformed JSON.
- `CurrentTask`: one case per rung of §2.2, and none.
- `Build`: order by time; each author kind; the 8,000-byte cut; no task.
- `Merge`: append; update in place; a late row with an early time still appends; the 200-row cap; an empty side.
- `Hint`: one case per row of §1.2, and the 30 s "hasn't woken" edge on both sides.
- `PaperclipActions`: `CommentAsync` and `OpenTaskAsync` against a scripted `IPaperclipApi`, one case per row of §3's table, with the id read from the response and the id missing.
- `PaperclipChatSession` over a fake feed and fake actions with an inline dispatcher: history on open; added and updated events; replaced on task change; `SendAsync` raises one user turn on a 2xx and none on a failure; the later read does not double it; read-only on a refused key; the hint changing raises its event.
- Slice 2: `PaperclipWindowDiff` per §5.2 (nothing changed, appended, one mid row changed, error flipped, shrank, task changed); the v18 vectors replayed; `RelayRemote` watch, unwatch and input for a `Listed` row with and without Paperclip connected, beside a control that a local CLI row behaves as before.

### 8.2 Integration (`tests/IntegrationTests`)

Against the loopback HTTP listener the Paperclip tests already use: `PaperclipChatFeed` and `CommentAsync` through the real `PaperclipHttp`. The comment `POST` carries the bearer key and the exact JSON body, with a body containing a quote, a newline and a non-ASCII character; a 401 maps to reconnect; a closed connection maps to unreachable (raw listener, per the findings' wire facts); the feed makes **no** request before it is opened and none after it is closed (the listener's request count is the assertion).

### 8.3 UI (`tests/UiTests`, class in `[Collection("Settings")]`) and screenshots

- `RemoteChatFor` returns a session for a Paperclip status and still a cloud session for a cloud one.
- `ChatPanel` opened on a fake `PaperclipChatSession`: the task line, a user bubble, tool lines, a failed tool line, the agent's bubble; the watermark for asleep, working and paused; a send puts the bubble up and clears the box; a failed send keeps the text and shows the red line; read-only hides the box and shows the reconnect words and link; "Open in Paperclip" is present.
- The two lifetime calls reach both a cloud session and a Paperclip one.
- `tests/UiScreenshots`: three captures: asleep with a thread; working with live tool lines; reconnect.
- Slice 2: the phones' own suites, §5.4.

Targeted runs while iterating (`--filter-class` for the MTP suites); CI on both rids is the suite gate; `tools/coverage.sh --base origin/develop` once for the PR body.

### 8.4 Real-machine proof (slice 1), with its control

On the MacBook, signed build, connected to the mini's Paperclip. One agent with an in-progress or blocked task.

1. **Positive.** Open the agent's panel. Send "Reply with the single word pineapple." Expect: the bubble; "Waking…" in the box; the orb working within about 10 s (window 30 s, three times the measured wake); at least one tool line or speech row while it runs; the agent's reply as a bubble when the run ends. Then read the same task in Paperclip's own page: the same two comments, the first by the board user. That page is the instrument; the panel agreeing with itself proves nothing.
2. **Negative control.** Revoke the app's key in Paperclip, send again. Expect: no new comment on Paperclip's page, the text still in the box, the reconnect words. This is what would have failed had the first send been going through a browser session or anywhere other than the app's key.
3. **Closed costs nothing.** Close the panel with the agent asleep; over 12 s (three slow ticks) `diagnostics.log` shows no `/comments` request. Open it: they start.

Windows: install and repeat step 1 only. Mini: install; headless, so the proof there is the log line pattern, not the panel. Slice 2's proof is the same exchange from a real phone, with the control being a desk without the cap showing today's read-only row.

## 9. The other branch: a free-standing conversation

A chat with no task behind it has no home in Paperclip. To build it Orbweaver would have to invent one, and there are two ways, both measured by nobody:

- **A hidden task per agent** ("Conversation with <name>") that every message comments on. It works through the same calls as this plan, so the code cost is small (one more create call and a rule to find it again), but it puts an Orbweaver-made task on the user's board that never closes, shows up in the agent's own task list and in every count Paperclip keeps, and pulls the agent off its real task each time. It breaks "Paperclip stays the source of truth" in spirit and needs the owner's say-so for that reason alone.
- **A wake with the message as its prompt**, if `wakeup` accepts one. Nothing stores the exchange, so Orbweaver would keep the conversation itself: a second record of what was said, on one desktop, invisible to Paperclip and to the phone unless relayed. That is new storage, a new sync problem and about twice slice 1.

Either way the reply still arrives when a run ends, seconds to minutes later. The free-standing branch does not buy a live turn; it only removes the task from view. If the owner wants it, the first option is an extra half-day on top of this plan and one more question to them about the board clutter.

## 10. Measured, assumed, and what settles each

Relied on as measured (lead, 2026.1117.0): a board user's comment `POST` wakes the assignee within 10 s; the events route and its tool, result and null-payload shapes; the answer stored as a comment with `createdByRunId`; live-runs fields; the feed refusing an app key; the 2 s and 4 s ticks.

Read from source and not run: everything in §5.1; that `ChatPanel` needs no change beyond the lifetime calls; that `IRemoteChatReadOnly.ReplyUrl` shows above a live composer.

Assumed, with the request in my batch to the lead that settles it:

| # | Assumption | Request | If it fails |
| --- | --- | --- | --- |
| 1 | The app's board key may post a comment | B1 | **Slice 1 has no message box.** It ships as the read-only activity panel and the send waits on a different credential. Settle before spawning engineers. |
| 2 | The woken run names its task, and the reply is a comment with that run's id | B2, C6 | the result row is shown instead of the comment; ordering still holds |
| 3 | A comment during a run is read when the run ends | B3 | only the "working" sentence changes |
| 4 | A comment on a `todo` or `done` task wakes the agent | B4 | the box says "It reads this at its next run." for those statuses |
| 5 | A comment for a paused agent is accepted and waits | B5 | a refusal is shown as refused, which already works |
| 6 | `item.completed` carries the spoken text | D8 | speech rows are dropped from slice 1; tool lines and the answer remain |
| 7 | `tool.execution.completed` says failure in a `status` | D8 | no red tool lines |
| 8 | Events can be read after a `seq`, or `limit=500` returns the newest 500 | D9 | the "Earlier activity is in Paperclip" line (§2.3) |
| 9 | A finished run's events stay readable and do not change | D10 | re-read on open instead of kept |
| 10 | Issues can be listed by assignee | E12 | rung 4 of §2.2 is dropped: an agent that has never run shows "No task yet." |
| 11 | A task can be created with the board key and assignment wakes the agent | E13 | §3.1's fallback: no box without a task |
| 12 | The comment response carries the new comment's id | B1 | match by text (§3) |
| 13 | LAN cost of the events read is close to loopback | F14 | the live tick goes to 4 s on a non-loopback address |
| 14 | A multi-row restating tail applies correctly on both phones | read from source | run by the slice 2 phone engineers before the desk sends one |

Not looked at: the watch app beyond confirming it shows no thread; Paperclip versions other than 2026.1117.0; a `local_trusted` instance.
