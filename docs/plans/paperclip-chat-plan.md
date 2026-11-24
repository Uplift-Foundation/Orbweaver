# Paperclip chat: see what an agent is doing and message it (CB-443)

Architect: Felicity Marchetti. Lead: Jennifer Voss. Written against Orbweaver `origin/develop` at `02f79fe83` and Orbweaver-Mobile `origin/develop` at `1a872f4`. Extends `docs/plans/paperclip-orbs-plan.md`; the measurements it rests on are in `docs/paperclip-findings.md`.

**The scope fork is not answered.** The owner was asked what "chat" means for an agent that sleeps between runs and has not replied. This plan is built on the lead's recommendation: **a message is a comment on the agent's current task**, shown as a chat, with "waking…" and then the reply; when the agent has no task, the message opens one. The other branch is costed in §9 so the owner can still choose it. If the owner picks it, §2 and §3 stand and §4 is redone.

**Status words.** *Measured* means run against the real Paperclip (2026.1117.0, on the mini) by the lead or recorded in the findings file. *Read from source* means I read it in this repository or the mobile one and did not run it. *Assumed* means neither. §10 lists what the lead's batch of 2026-11-24 (about 03:50Z, loopback, a key from the app's own connect flow) measured, and what is still assumed. I read the returned bodies myself for the shapes in §2.4; the requests were the lead's.

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
| Asleep, never had a task | "No task yet." | "Message <name>. This opens a new task." |
| Working live | tool lines and speech appear as they happen | "<name> is working. Your message goes to this run." |
| Just messaged | the person's bubble | "Waking <name>…" until a run shows, then the Working words |
| Waiting on a sub-task | the thread; a muted line naming the task it waits on, when Paperclip says | the Asleep words |
| Needs you | the thread; the amber flyout keeps Approve and Decline exactly as today | "Message <name>. It wakes to read it." |
| Broke | the thread; the failed run's activity with its red lines; the orb's own one-sentence reason as a red line | the Asleep words |
| Paused | the thread | "<name> is paused in Paperclip. It reads this when resumed." |
| Key refused | what was last read, frozen | no box; "Reconnect to Paperclip", with the link |

### 1.3 What a message does

1. The person types and sends. The comment is posted to the task in Paperclip.
2. Their bubble appears only when Paperclip has accepted it. A refusal leaves the text in the box and says why in a red line.
3. The box says "Waking <name>…". Measured with the app's key: the agent's run started 2.0 s after the comment and finished 12.2 s after it. Earlier, from a browser session: within 10 s, three times.
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
4. the agent's most recently active task that is not cancelled, from `issues?assigneeAgentId=<agent>`, read **once when the panel opens and only when rungs 1 to 3 gave nothing**. Measured: 200, but 49 rows and 169 KB in 79 ms for one agent, so it is never polled. A `done` task counts: a comment on a done task reopened it and woke the agent (measured, `wakeReason: issue_reopened_via_comment`). Whether `limit` and a sort shrink that read is declared for the route and not tried;
5. none.

The task can change while the panel is open (a run starts on another task). The thread is then replaced whole and the panel told through `HistoryReplaced`, which means the session also implements `IRemoteChatBacklog` with `HasMore` false. One event, no paging.

### 2.3 Reads and cadence

Only while a panel is open, or a phone is watching (slice 2). A closed panel costs nothing.

| Read | When | Cost |
| --- | --- | --- |
| `GET /api/issues/{id}/comments` | on open, every 4 s while open, at once after a send, at once when the run ends | measured: 5 to 6 ms for three comments, **newest first** (the parser sorts). The route declares no limit and no cursor, so a long thread is re-read whole; its cost is **not measured** |
| `GET /api/heartbeat-runs/{runId}/events?afterSeq=<last seen>&limit=500` for the newest run on the task | every 2 s while that run is live; once when it has finished, then kept | measured: `afterSeq` returns only later rows, oldest first (`limit=5&afterSeq=20` gave 21 to 25). A full read of 87 events, 107 KB, took 10 ms on loopback; LAN **not measured** |
| `GET /api/issues/{id}`, for the task line | on open and when the task changes | declared in Paperclip's OpenAPI document, **not called**; falls back to the title the poll already holds |

Per open panel that is 15 reads a minute asleep and 45 a minute during a run, on top of the 105 a minute the connected app already makes (findings, Timing). The feed does not share the poll's own events read (CB-442 reads each running run every 4 s for permission prompts): two readers of one body would couple the prompt path to the panel, and the saving is one read in three.

The feed keeps the highest `seq` it has seen for the run and asks only for what came after, so a tick during a quiet moment returns `[]`. A read that returns a full 500 is followed at once by the next page, at most four pages a tick. That removes the long-run risk the first draft of this plan carried: `limit` returns the oldest rows, so a plain `limit=500` would have frozen on a long run's first 500 events. Because reads are incremental, the feed holds a small folded state per run (`PaperclipChat.Activity`, §2.4), not the bodies.

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
    // Folded across incremental reads: the rows so far, the tool names by executionId, and the highest seq.
    internal sealed record Activity(string RunId, long LastSeq, IReadOnlyList<Row> Rows /* + private join state */);
    internal static Activity Fold(Activity soFar, string? eventsJson);   // never throws; a bad body returns soFar
    internal static TaskRef? CurrentTask(/* agent id, live runs, finished runs, blocked issues, assigned issues */);
    internal static IReadOnlyList<Row> Build(TaskRef? task, string agentId, IReadOnlyDictionary<string, string> agentNames,
        IReadOnlyList<Comment> comments, Activity? activity);
    internal static IReadOnlyList<Row> Merge(IReadOnlyList<Row> shown, IReadOnlyList<Row> built);   // §2.5
    internal static string Hint(/* agent state, status, link, awaiting-wake since, now */);
}
```

`Fold`, from one run's events. Every shape below was read out of the lead's real run body (87 events: one tool call, two deltas, one spoken message, one result); everything sits under `payload.prpEvent.payload`.

| Event | Row | Status |
| --- | --- | --- |
| `tool.execution.started` | System, "<name>", key by `executionId` | measured: `name`, `executionId`, `namespace`, `status: "running"` |
| `tool.execution.progressed` with a `subject` | the same row becomes "<name>: <subject>" | `subject` measured on a shell command (CB-442); the tool in this body, an MCP one, carried none, so the row stays "<name>" |
| `tool.execution.completed` | sets `Error` on that row when `status` is not `"completed"` or `exitCode` is a non-zero number | measured for success only (`status: "completed"`, `exitCode: null`). **A failed tool has not been seen**; the rule is the cautious reading |
| any tool event whose `namespace` is `"paperclip"` | no row | decided: the one measured is `paperclip_finish`, the runner's own bookkeeping, not something the agent did for the person |
| `item.completed` with `kind: "agentMessage"` | Assistant, `text`, key by `itemId` | measured. The other kinds in the body (`thread_lineage`, `model`, `usage`) are skipped: seven of the nine `item.completed` rows are not speech |
| `item.delta` | ignored | measured: `kind: "agentMessage"`, `text` is the fragment ("17 times 3 is ", then "51."). Decided: no streaming in this slice; the whole message arrives about a second later |
| `run.result.proposed` | no row of its own; its `summary` is the run's answer when no `agentMessage` with `channel: "final"` came | measured: the summary, the final message and the stored comment were the same sentence three times |
| anything else, and `"payload": null` (one in this body) | skipped | measured |

**One answer, shown once.** A run's answer reaches the app up to three ways. The row for it has the key `r:<runId>:answer`: the final `agentMessage` if there is one, else the result's summary. When a comment arrives whose `createdByRunId` is that run (measured: the reply is a new comment by the agent carrying it), `Merge` gives that comment the answer row's place and takes the comment's text, so the bubble never doubles and never jumps.

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

`CommentAsync` is `POST /api/issues/{id}/comments {"body": text}`. **Measured with the app's own board key: 201 in 25 ms**, the comment stored as `authorType: "user"` with the key's user. Paired controls: no credential, 404 `{"error":"Issue not found"}` (not a 401); an empty body, 400 with a validation error. The response is the comment row, `id` included. The run it woke names the comment (`contextSnapshot.wakeCommentId`), which nothing here needs yet.

| Outcome | When | What the person sees | `SendAsync` returns |
| --- | --- | --- | --- |
| Sent | 2xx | their bubble; "Waking <name>…" | `Sent` |
| Sent, agent busy | 2xx while a run is live | their bubble; "<name> is working. Your message goes to this run." | `Sent` |
| Sent, agent paused | 2xx, agent `paused` | their bubble; the Paused words | `Sent` |
| No task | no current task | §3.1: the message opens one | `Sent` or as below |
| Task gone | 404 | red line "That task is no longer in Paperclip."; text stays; the feed re-picks the task | `Failed` |
| Refused | any other 4xx | red line with Paperclip's own `error` sentence when the body has one, else "Paperclip refused that."; text stays in the box | `Failed` |
| Needs reconnect | 401, 403 | red line "Reconnect to Paperclip to message <name>."; text stays | `Failed` |
| Unreachable | timeout, refused, 5xx | red line "Can't reach Paperclip."; text stays | `Failed` |

"Agent busy" is **measured once** (2026-11-24, board session, Paperclip 2026.1117.0): a comment posted while the agent was already running on that task was a 201, started no second run, and the same run took it in and answered both the task and the message in its one final comment. That is why the words are "Your message goes to this run." and not a promise about timing: one reading cannot show what a message landing in a run's last second does. A paused agent was also measured: the comment is accepted (201), the task goes from done back to todo, nothing runs until the agent is resumed, and it answered 18 s after resume. Interrupting or steering a run from the panel is not in this plan.

The person's bubble is raised by `SendAsync` on a 2xx (requirement 3), keyed by the comment id from the response (measured to be there), so the next comments read finds the same key and adds nothing. A 2xx whose body cannot be read still counts as sent; the next read then adopts the newest unkeyed user bubble with the same text.

The 20 s `ActionTimeout` and the `PollNow` nudge after a settled call are reused, so the orb reacts at once.

### 3.1 No current task

`OpenTaskAsync` creates a task assigned to the agent, `POST /api/companies/{id}/issues` with `title`, `description`, `assigneeAgentId` and `status: "todo"`: title is the message's first line cut to 80 characters, description is the whole message. **Measured with the app's key: 201 in 26 ms; the assignee's run started 2.0 s later (`invocationSource: "assignment"`), finished 12.2 s after the create, and the task went to done.** No credential: 401. So it ships in slice 1. The new task becomes the panel's task, and the agent's answer arrives as a comment on it like any other.

Because a done task still takes a message (§2.2 rung 4), the no-task case is only an agent that has never been given anything.

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

### 5.5 Agent pictures on the phone (CB-445's phone half, in the same contract file)

The desktop half of CB-445 fetches each agent's picture with the board key and keeps it as an owner-only PNG file that `PersonaImageStore.Offer(path)` can take. This section is how that picture reaches a phone, so a Paperclip row gets one contract change and not two.

**What old phones do with a Paperclip row that carries a persona** (read from Orbweaver-Mobile `origin/develop` at `1a872f4`; none of it run):

| Phone | What happens | Verdict |
| --- | --- | --- |
| iOS | The roster decoder takes `id` and `image_hash` on any row, whatever its `cli` (`RosterReducer.swift:146`), and offers every roster hash for fetching (`LiveClient.swift:1042`). So the picture is fetched and drawn on the orb card, the session header and the bubbles. | mostly right by accident |
| iOS | New Session takes "this folder's persona" from the first row on that machine with the same `cwd` and a persona (`LiveMapping.swift:89`). A Paperclip row's `cwd` is `""`, so an empty folder field would show an agent's name there. | wrong, small |
| iOS | The sheet's subtitle is machine, cli and persona name (`SheetScreens.swift:84`): the agent's name twice. | cosmetic |
| Android | The roster decoder keeps a persona's `name`, `initials` and `color` only (`Roster.kt:101`); `id` and `image_hash` are dropped. **Android draws no persona picture on any session today**, only the initial disc; its picture store is fed by account icons alone (`LiveClient.kt:112`). | no picture, no harm |
| Android | The Personas screen is every distinct persona name across the roster (`Shell.kt:639`). Each Paperclip agent would be listed there as a persona, with a read-aloud setting. | wrong |

Neither phone drops the roster or crashes on such a row. But two of those outcomes put an agent where a persona belongs, on builds nobody can fix after the fact. So the desk does not send it to a phone that has not asked.

**The rule.**

| Rule | Text |
| --- | --- |
| who asks | The phone, not the desk. A desk's `caps` cannot protect an old phone from a field on a row, so this is **not** part of `paperclip_chat`. It is one optional boolean on the phone's `prefs` frame, `paperclip_personas`, exactly as frames-v16 added `broke_alerts`: absent is false, anything that is not JSON `true` is false, a phone that never sent prefs is false, a desk that does not know the key ignores it (read from source: `AlertPrefs.Parse` reads only the keys it knows). |
| the row | For a phone whose stored prefs say true, a `paperclip:` row may carry `persona`: `name` (the agent's name, equal to `title`), `initials` (the two letters the desktop orb wears), `id`, and `image_hash` when the desk can offer the picture now. No `color`. `id` is 16 lowercase hex: the first 16 of SHA-256 over UTF-8 `"orbweaver-paperclip-persona-v1\n"` + the agent's id, so it is stable across restarts and no path or URL crosses the wire. `image_hash` is frames-v5's: SHA-256 of the cached file's bytes. |
| every other phone | Gets today's row, byte for byte: no `persona` key. The desk builds each phone's roster separately already (`RelayLink.cs:1015`), so this is one filter before `Stream.Roster`. |
| a prefs change | A phone whose `paperclip_personas` changes is sent a fresh whole roster. |
| a picture change | A new or changed picture changes `image_hash`, which re-sends the roster (frames-v5: a changed persona record is a changed row). A picture the desk cannot offer (not fetched yet, too large, refused) leaves `image_hash` out and the phone draws the initials. |
| the picture itself | frames-v5's `persona_image` request and windows, unchanged. The desk offers the cached file's path to `PersonaImageStore` and answers by hash. |
| the `personas` frame | **A Paperclip agent is never a row in it.** That frame is the list of personas a person can start a session as (the Personas screen, the New Session row). The roster row alone carries the agent's face; frames-v5 already has a phone fetch a hash named by a roster row. |
| prompts and pushes | Unchanged: a Paperclip prompt carries no persona, so no notification changes its sender or picture. Not in this slice. |
| new phones | Send `paperclip_personas: true` only from a build that (a) draws a row's picture, (b) keeps a `paperclip` row out of its Personas list and out of "this folder's persona", and (c) does not repeat the name in the sheet subtitle. |

**What it costs each phone.** iOS already draws a roster row's picture, so it needs the prefs key and the three exclusions: small. **Android has no persona pictures for sessions at all**, so to send the key honestly it first needs what CB-321 gave iOS: decode `id` and `image_hash`, offer roster hashes to its picture store, and draw the picture on the orb card and the session header. That is the larger part of this section and is Android's own piece of work; until it lands Android does not send the key and keeps today's row. It is the lead's call whether that rides in slice 2 or follows it.

Vectors, added to `frames-v18-vectors.json`: a prefs frame with the key true, false and absent; a roster holding a Paperclip lead with a picture, a report with a persona and no picture, and a Claude row, as sent to a phone with the key and to one without (the second byte-identical to frames-v15's `paperclip_roster` for those rows); the persona id for a known agent id. `roster-fixtures.json` gains the with-key roster, byte-identical on both repos.

Desk changes, on top of §5.3: `AlertPrefs` gains the flag (parsed, stored, logged as a boolean); `RelayRoster.Project` gives a Paperclip row its persona from a seam that returns the cached picture's path for an agent id (CB-445's cache; this plan does not fetch pictures); a pure `RelayRoster.ForPhone(sessions, paperclipPersonas)` that strips it; the roster re-send on a prefs change.

## 6. What ships when

| Slice | Contents | PRs |
| --- | --- | --- |
| **1. Desktop** (CB-443) | §2, §3, §4: the panel with the thread and live activity, the message box, every state in §1.2, the no-task send included. | one PR to Orbweaver `develop`, branch `feature/paperclip-chat-desktop` |
| **2. Phone** (its own ticket under CB-419, to be filed by the lead) | §5: the thread and the message box (§5.2 to §5.4), and agent pictures (§5.5, CB-445's phone half) | one contract PR to Orbweaver-Mobile first, holding both; then the desk PR and the two phone PRs in parallel against the merged contract |

Slice 1 is honest alone: requirement 4 (the phone) is the only part missing, the phone keeps today's read-only row, and nothing on the desktop promises otherwise. Each slice is sized to an afternoon.

Cut to fit that: tool output, multiple tasks, older runs' activity, streaming, the panel's own wake/pause buttons, sharing the poll's events read, a status row in the thread, and any reply notification (§1.5).

## 7. Staffing

Known shape, no new subsystem; this plan is the architect stage. The lead spawns engineers and QA together.

**Slice 1: two engineers and QA.** The seam between the engineers is §2.4's signatures and §3's two calls, fixed here so neither waits.

- **Engineer A, the pure half and the wire.** `PaperclipChat.cs` (parsers, `CurrentTask`, `Build`, `Merge`, `Hint`), `CommentAsync` and `OpenTaskAsync` in `PaperclipActions`, `PaperclipChatFeed.cs` (the timed reads over `IPaperclipApi`), fixtures cut from the lead's real bodies, unit and integration tests, and two rows in `tools/paperclip-probe` (comments, events).
- **Engineer B, the session and the panel.** `PaperclipChatSession.cs` against a fake feed and the fake `IPaperclipActions`, the §4 wiring, UI tests, the screenshot captures, the README.
- **QA.** Writes the matrix from §1.2 and §3 while they build; coverage of added lines; installs (one install owner: QA); the real-machine proof in §8.4.

**Slice 2: one contract author, then three engineers and QA.** Contract and vectors (one engineer, about an hour, merged first). Then desk relay side, iOS, Android in parallel. QA proves it on a real phone against a real desk. Pictures (§5.5) add little to the desk and iOS and a lot to Android, which has no session pictures today; if Android's share does not fit the afternoon, Android ships the thread and the message box and leaves the prefs key unsent, which the contract makes safe.

## 8. Tests

Target is 100% of added lines at the three levels. Fixtures come from the lead's captured bodies, scrubbed, under `tests/UnitTests/Fixtures/paperclip/native/chat/`; a fixture that was edited by hand says so in its name, as the tool-action ones do.

### 8.1 Unit (`tests/UnitTests`)

- `ParseComments`: the real list; a user comment, this agent's, another agent's; missing fields; not an array; malformed JSON.
- `Fold`: the real run body end to end (expected: no tool row, since its one tool is the runner's own, and one answer row); the same body fed in three `afterSeq` slices gives the same rows as fed whole; a shell tool with a `subject` (from the CB-442 fixtures); a tool that only starts; a failed tool by `status` and by `exitCode` (edited copies, named so); the `paperclip` namespace hidden beside a control that is shown; each non-speech `item.completed` kind skipped; `item.delta` ignored; the answer from the final message, and from the summary when there is none; `"payload": null`; an unknown event; malformed JSON returns the state unchanged.
- `CurrentTask`: one case per rung of §2.2, and none.
- `Build`: order by time; each author kind; the 8,000-byte cut; no task.
- `Merge`: append; update in place; a late row with an early time still appends; the comment that carries a run's id takes the answer row's place; the 200-row cap; an empty side.
- `Hint`: one case per row of §1.2, and the 30 s "hasn't woken" edge on both sides.
- `PaperclipActions`: `CommentAsync` and `OpenTaskAsync` against a scripted `IPaperclipApi`, one case per row of §3's table (201, 404, 400 with Paperclip's sentence, 401, timeout), with the id read from the real response body and from an unreadable one.
- `PaperclipChatSession` over a fake feed and fake actions with an inline dispatcher: history on open; added and updated events; replaced on task change; `SendAsync` raises one user turn on a 2xx and none on a failure; the later read does not double it; read-only on a refused key; the hint changing raises its event.
- Slice 2: `PaperclipWindowDiff` per §5.2 (nothing changed, appended, one mid row changed, error flipped, shrank, task changed); the v18 vectors replayed; `RelayRemote` watch, unwatch and input for a `Listed` row with and without Paperclip connected, beside a control that a local CLI row behaves as before. For §5.5: `AlertPrefs.Parse` with the key true, false, absent and not a boolean; the persona id from an agent id; `Project` with and without a cached picture; `ForPhone` strips a Paperclip row's persona and leaves a Claude row's alone; the without-key roster equals today's bytes; a prefs change re-sends the roster; a Paperclip agent never appears in the `personas` frame.

### 8.2 Integration (`tests/IntegrationTests`)

Against the loopback HTTP listener the Paperclip tests already use: `PaperclipChatFeed` and `CommentAsync` through the real `PaperclipHttp`. The comment `POST` carries the bearer key and the exact JSON body, with a body containing a quote, a newline and a non-ASCII character; a 401 maps to reconnect; a closed connection maps to unreachable (raw listener, per the findings' wire facts); the events read carries `afterSeq` and follows a full page with the next; the feed makes **no** request before it is opened and none after it is closed (the listener's request count is the assertion).

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

**Measured by the lead on 2026-11-24, Paperclip 2026.1117.0, loopback, with a key from the app's own connect flow.** The bodies are the fixtures' source; engineer A gets them from the lead and scrubs ids before committing any.

| Fact | Result |
| --- | --- |
| Comment with the app's key | 201, 25 ms; `authorType: "user"`; response is the comment row with `id`. No credential 404; empty body 400 |
| The comment wakes the assignee | run started 2.0 s later, finished at 12.2 s; `invocationSource: "automation"`; `contextSnapshot` names `issueId` and `wakeCommentId` |
| A comment on a done task | reopens it and wakes the agent; the task returned to done |
| The reply | a new comment, `authorType: "agent"`, `createdByRunId` = the woken run; the list comes newest first, 5 to 6 ms |
| Events after a seq | `afterSeq` works, oldest first; `after`, `sinceSeq` and `order=desc` are ignored |
| Speech in the events | `item.completed` with `kind: "agentMessage"`; seven of nine `item.completed` rows were other kinds |
| Create a task with the app's key | 201, 26 ms; assignee woke in 2.0 s; no credential 401 |
| Issues by assignee | works; 49 rows, 169 KB, 79 ms. `&status=in_progress` works. A comma list of statuses returned nothing with nothing open, which proves nothing |
| From a browser session, earlier | a comment on a blocked and on an in-progress task woke the assignee within 10 s; a task in `execution_reconciliation_required` cannot be woken by anything; setting a task back to `todo` does not wake its agent |

Relied on from before (findings file): the tool event shapes and the `subject` on a shell command; the feed refusing an app key; the 2 s and 4 s ticks.

Read from source and not run: everything in §5.1; that `ChatPanel` needs no change beyond the lifetime calls; that `IRemoteChatReadOnly.ReplyUrl` shows above a live composer. Declared in Paperclip's OpenAPI document and not called: `GET /api/issues/{id}`; the queued-comments routes.

**Still assumed.**

| # | Assumption | Settled by | If it fails |
| --- | --- | --- | --- |
| 1 | A comment during a run is queued and read when the run ends | the lead, on request, once the slice is building | only the "working" sentence changes |
| 2 | A comment for a paused agent is accepted and waits | the lead, on request | a refusal already shows as refused |
| 3 | A comment on a `todo` or `backlog` task wakes the agent | not run; blocked, in-progress and done are measured | the "hasn't woken yet" words after 30 s already cover it |
| 4 | A failed tool says so in `status` or `exitCode` | the first real failed tool call; QA looks for one in the proof | no red tool lines |
| 5 | A finished run's events stay readable and do not change | not run (a day-old run was not read) | re-read on open instead of kept |
| 6 | LAN cost of the events read is close to loopback's 10 ms | QA's Windows install, which reaches Paperclip over the LAN | the live tick goes to 4 s on a non-loopback address |
| 7 | A long comment thread is cheap to re-read whole every 4 s | not measured; three comments took 5 ms | read comments on open, after a send and when a run ends only |
| 8 | A task stuck in `execution_reconciliation_required` is rare enough to leave to the 30 s "hasn't woken yet" words | measured that it cannot be woken; how often it happens is not known | name the state in the box |
| 9 | A multi-row restating tail applies correctly on both phones | read from source; run by the slice 2 phone engineers before the desk sends one | the desk sends a `reset` backfill instead |
| 11 | Old phones behave as §5.5's table says when a Paperclip row carries a persona | read from source at `1a872f4`, not run. The gate means no old phone is ever sent one, so the table is the reason for the gate, not something the feature depends on | nothing: the gate holds either way |
| 12 | CB-445's cached picture file is accepted by `PersonaImageStore.Offer` and encodes under frames-v5's caps (a custom one is 406 KB, measured) | the desk engineer, with a real cached file | no `image_hash`; the phone draws initials |
| 10 | `frames-v18` is free | I read v17 on Orbweaver-Mobile `origin/develop` at `1a872f4`; the lead reports v16 as the newest deployed. The contract author checks open branches | take the next number |

Not looked at: the watch app beyond confirming it shows no thread; Paperclip versions other than 2026.1117.0; a `local_trusted` instance.

## 11. Sharing the panel with CB-445 (agent pictures)

CB-445 puts a Paperclip agent's picture on its orb, and the same picture belongs in this panel's header. The two tickets meet in four files and need not wait on each other.

- **The phone half of CB-445 is §5.5**, in the same contract file as the thread, so a Paperclip row changes once.
- **The header picture is CB-445's, and this plan adds nothing for it.** `ChatPanel.ApplyAvatar(sessionId)` (`ChatPanel.axaml.cs:1870`) already asks for a face by session id, and this panel opens under the orb's own id, `paperclip:<agent id>`. So when CB-445 teaches that lookup about a Paperclip agent, the header shows the picture with no change here; until then the header shows the orb's letters, as any session without a picture does. CB-443 writes no avatar code and does not read `avatarUrl`.
- **The name and the line under it are CB-443's**: `DisplayName` is the agent's name. Its role or title goes in the header's meta line only if `ChatHeaderMeta` takes it without a new seam; otherwise it is left out.
- **One rule CB-445 must hold, stated here because this panel is where it would show:** a custom picture needs the key (`/api/assets/{id}/content`, measured 406 KB), so it is fetched through `IPaperclipApi`, cached once per agent and per URL, and never fetched by the panel or per open. The generated one (`/api/agent-avatars/…`, 26 KB) needs no credential. The picture is not sent to the phone by this plan's frames.
- **Where the edits touch.** `ChatPanel.axaml.cs`: this plan changes two lines near `:784` and `:933`, CB-445 the face lookup near `:1870`. `PaperclipApi.cs`: CB-445 adds `avatarUrl` to the agent row; this plan adds nothing to that file. `PaperclipSessions.cs`: both add read-only accessors. `SessionManager.cs`: CB-445 touches the Paperclip merge block, this plan `RemoteChatFor`. None overlap; whichever lands second rebases.
- **Screenshots.** This plan's three captures draw the letters. If CB-445 lands first, engineer B gives the fake agent a picture in one of them so the header is seen once with it.
