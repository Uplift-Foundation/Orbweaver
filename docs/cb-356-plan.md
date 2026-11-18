# CB-356 plan: a compaction as one compact, collapsible row

Architect: Ines Carvalho. Read against `origin/develop` at `99b427fd6`. Branch `feature/compaction-summary-row`. Fixtures are in `tests/TranscriptTests/Captures/cb356/` with their own README.

## 1. What was measured

Scope: every `*.jsonl` under `~/.claude*/projects` on the MacBook (six config dirs) and on the Mac mini (five), 2026-11-17. Claude Code 2.1.296 to 2.1.326. The Windows box was not scanned.

### Triggers

| `compactMetadata.trigger` | MacBook | mini |
| --- | --- | --- |
| `auto` | 77 | 114 |
| `manual` | 0 | 1 |
| `idle` | 0 | 0 |
| anything else | 0 | 0 |

**No idle compaction exists on either machine: 192 rows, every one `auto` or `manual`.** Whether Claude Code ever writes `trigger: "idle"` on a row is not settled. Read off the 2.1.327 binary (one version, minified): it has an idle path (a dialog shown to an idle session that offers to compact, and an internal precompute trigger with the values `idle`, `threshold` and `ptl`), but the place that builds `compactMetadata` takes its trigger from a variable, so the binary does not show which word a compaction started that way records. Two things it does show: a compaction that ran from the idle caller returns an `afterIdle` flag that is not written to the row, and the row can carry `precomputed: true`, described in its own schema text as "the summary was generated in the background at the autocompact threshold and swapped in when prompt-too-long fired". That is not "while idle", no real row carries the key (0 of 192), and this plan does not derive anything from it. So: `idle` is mapped to "while idle" in case it is written, covered by a synthetic fixture, and any other value reads as plain "Conversation compacted".

### The two rows

Measured on the MacBook's 77 pairs unless noted.

- **Boundary:** `type: "system"`, `subtype: "compact_boundary"`, `content: "Conversation compacted"`, `level: "info"`, `isSidechain: false` on all 77. `compactMetadata` always present with `trigger`, `preTokens`, `postTokens`, `durationMs` (all four on all 77), plus `cumulativeDroppedTokens`, `preservedSegment`, `preservedMessages` and a varying set of others (`preCompactDiscoveredTools`, `runningTurnSends`, `runningTurnPrompt`, `preCompactArtifactReadVersions`). `preTokens` 167,139 to 978,379; `postTokens` 11,294 to 38,041.
- **Summary:** `type: "user"`, `isCompactSummary: true`, `isVisibleInTranscriptOnly: true`, `message.content` a JSON **string** on all 77 (never a block array), no `isMeta`. 2,022 to 26,179 UTF-8 bytes, median 14,367; **76 of 77 are over 8,000 bytes**, which matters for the phone (section 5).
- **`isVisibleInTranscriptOnly` marks nothing else:** 77 of 77 rows carrying it are compact summaries on the MacBook, and 0 rows on the mini carry it without `isCompactSummary`. But the binary also writes a summary with `isCompactSummary: true`, a `summarizeMetadata` object and **no** `isVisibleInTranscriptOnly` (a partial summary; none found on disk). So the parser keys on `isCompactSummary` alone and never reads `isVisibleInTranscriptOnly`.
- **Pairing:** every boundary had a summary and every summary a boundary (0 orphans of 77). The summary directly follows the boundary in 40; in 37 exactly three `attachment` rows sit between them. `summary.parentUuid == boundary.uuid` only in the 40 adjacent cases, so the link is by position, not by uuid. Boundary and summary timestamps are under 1 s apart in 55 and 1 to 60 s apart in 22. The boundary carries `postTokens` and `durationMs`, which only exist once the compaction has finished, so both rows are built at the end; whether they reach the file in one write was not measured.
- **Summary text:** 66 open with "This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion of the conversation." and 11 open with `<artifact-content-authored-by-others/>` plus one sentence, then that same opening. 58 have one `<system-reminder>` block, 1 has two, 18 have none; every block is `<tools-used-before-summary>…</tools-used-before-summary>` and sits after the opening, never at the very start or end. All 77 end with a paragraph starting "If you need specific details from before compaction" that names the transcript's path and tells the model how to resume, sometimes followed by one more paragraph.
- **Other `system` subtypes** in the same files (`stop_hook_summary`, `turn_duration`, `away_summary`, `local_command` and eight more) are not touched. The binary also names a `microcompact_boundary` subtype; 0 rows found, out of scope.

### What the app does today

Run, not reasoned: `dotnet run --project tests/TranscriptTests -- tests/TranscriptTests/Captures/cb356/auto-attachments-between.jsonl` on develop prints three turns: Assistant, **User** "This session is being continued…", Assistant. The boundary row produces nothing because `ChatTranscript.IsInteresting` does not pass `system` rows. Since every Claude Code path goes through `ChatTranscript.Map` (`CliChatFormat.ClaudeCode`, `TranscriptWatcher.ParseFile`, `MirrorProtocol.TurnsFrom`, `ClaudeCloudChat` at lines 73, 448, 1861, 1874), the same user bubble reaches the local panel, the mirror, the cloud panel and the phone.

### Not measured

- A cloud session's `/v2/ccr-sessions/{id}/events` rows for a compaction. `docs/claude-cloud-findings.md` records that a `system/compact_boundary` event and "a replayed user row" appear there; whether that user row carries `isCompactSummary`, and whether the metadata key is `compactMetadata` or the SDK's `compact_metadata`, was not captured. Engineer A captures one (section 4).
- Windows transcripts.

## 2. Design

### The model

New file `CompactionNote.cs`, namespace `Orbweaver`, pure (no window, no settings, no I/O). A owns it; B consumes it.

```csharp
public sealed record CompactionNote(
    string? Trigger,     // as written: "auto", "manual", or anything else; null when unknown
    long? PreTokens,
    long? PostTokens,
    long? DurationMs,
    string Summary,      // Markdown for the expanded view, scaffolding stripped; "" until a summary row is seen
    bool HasBoundary)    // true when built from, or merged with, a boundary row
{
    public bool HasSummary => Summary.Length > 0;
    public string Label { get; }       // "Conversation compacted automatically"
    public string Detail { get; }      // "246k → 21k tokens", or ""
    public string Line { get; }        // Label, then " · " + Detail when there is one. What ChatTurn.Text holds.

    public static CompactionNote FromBoundary(JsonElement row);
    public static CompactionNote FromSummary(string content);
    public static string Strip(string content);
    public static string Tokens(long n);
    public CompactionNote WithSummaryFrom(CompactionNote summary);
    public static bool TryAbsorb(ChatTurn open, ChatTurn incoming);
}
```

`ChatTurn` (in `RemoteChat.cs`) gains `public CompactionNote? Compaction { get; set; }`, raising `PropertyChanged` like `Text` does. **A compaction turn is `Role = ChatRole.System`, `Text = Compaction.Line`, `IsComplete = true`.** That is the same choice CB-330 made for relays and it is what makes every consumer that does not know about `Compaction` degrade correctly: speech picks the last `Assistant` turn, `Reconcile` ignores non-user turns, the scroll rule only follows `User`, an older mirror peer and an older phone draw a dim one-line "Conversation compacted automatically · 246k → 21k tokens".

### Words

| `trigger` | Label |
| --- | --- |
| `manual` | Conversation compacted manually |
| `auto` | Conversation compacted automatically |
| `idle` (never observed) | Conversation compacted while idle |
| anything else, missing, or no boundary row | Conversation compacted |

Compared with `OrdinalIgnoreCase`. An unknown value is kept in `Trigger` verbatim and never shown.

### Tokens

`Tokens(n)`: below 1,000 the number itself; otherwise `round(n / 1000)` (half away from zero) + `k`; when that reaches 1,000, `n / 1,000,000` to one decimal + `M`. Invariant culture. `245582` → `246k`, `20628` → `21k`, `978379` → `978k`, `999600` → `1.0M`, `850` → `850`. `Detail` is `"{pre} → {post} tokens"` with both, `"{pre} tokens"` with only `preTokens`, and `""` otherwise. A non-number or negative value counts as missing. `DurationMs` is carried and not shown.

### Stripping (`Strip`)

Applied to the summary row's text, in this order. Each step is anchored and exact; a step whose text is not found does nothing, so an unfamiliar summary is shown whole rather than mangled.

1. Remove every `<system-reminder>` … `</system-reminder>` block, tags included (non-greedy, across newlines). An opening tag with no closing tag is left alone.
2. At the start only: remove `<artifact-content-authored-by-others/>` and the rest of that line's sentence (through the newline before "This session is being continued").
3. At the start only: remove the measured opening sentence pair ("This session is being continued … earlier portion of the conversation.").
4. Remove from the **last** occurrence of a paragraph starting "If you need specific details from before compaction" to the end. It is addressed to the model and names a file path.
5. At the start only: remove a leading `Summary:` line. Trim.

Steps 2 to 5 go beyond the ticket's "system-reminder scaffolding"; they remove text written to the model, not to a reader, and the row's own label already says what the row is. If the result is empty, `Summary` is the original text trimmed, so an expandable row never opens onto nothing.

### Pairing rules

**In one `Map` call** (the whole-file and window reads):

1. A `compact_boundary` row adds one turn keyed by the boundary's uuid, `Compaction = FromBoundary(row)`, and is remembered as *open*.
2. A row with `isCompactSummary: true` (any `type: "user"` row, string content, or the text blocks of an array joined with blank lines) **never** goes down the user-text path. If the open boundary's turn is still the last turn in the list, the summary is merged into it (`WithSummaryFrom`) and the turn keeps the boundary's uuid. Rows that produce no turn (the three `attachment` rows) do not break the pairing.
3. Otherwise the summary is an **orphan summary**: one turn keyed by its own uuid, `Trigger` null, plain "Conversation compacted", expandable.
4. A boundary never followed by a summary stays as rule 1 left it, an **orphan boundary**: label and detail, no chevron.
5. `isSidechain` rows are skipped first, as today. An `isCompactSummary` row is handled before the `isMeta` test.
6. A boundary whose `compactMetadata` is missing or not an object is still a row (plain label). The snake_case `compact_metadata` with `pre_tokens`, `post_tokens`, `duration_ms` is read as a fallback.

**Across calls** (`TryAbsorb`, pure): a live tail can deliver the boundary in one batch and the summary in the next, the cloud stream maps one event per call, and a page seam can fall between the two. `TryAbsorb(open, incoming)` returns true, and sets `open.Compaction` to the merged note and `open.Text` to its `Line`, when `open.Compaction` has a boundary and no summary and `incoming.Compaction` has a summary and no boundary. Each session calls it at the points listed in section 3 and raises `TurnUpdated(open)` instead of adding the incoming turn. Two summaries in a row, or two boundaries, never merge.

## 3. Consumers, one by one

| Consumer | Where | What happens |
| --- | --- | --- |
| Local panel, initial window and older pages | `LocalCliChatSession` lines 405 and 462, via `CliChatFormat.ClaudeCode.Map` | Paired inside `Map`. On prepend, if the page's last turn and `_history`'s first turn satisfy `TryAbsorb`, absorb into the page's turn and drop the other before `InsertRange`. |
| Local panel, live tail | `LocalCliChatSession.AddRow` (line 2064) | Before `Reconcile`: `if (_history.Count > 0 && CompactionNote.TryAbsorb(_history[^1], turn)) { TurnUpdated; return; }` |
| "The user's last message" | `LocalCliChatSession.Reconcile` (1522) and `RemoteControlChatSession` (966) both return early for a non-user turn | No change; a test pins that a pending optimistic bubble is not settled by a compaction turn. |
| Speak latest | `ChatPanel.SpeakLatest` (2997) takes the last `Assistant` turn | No change; pinned by a UI test. |
| Orb speak button and turn-summary speech | `OrbWindow` 2017 to 2107, all fed assistant text | No change; one test with a transcript ending boundary + summary. |
| Scroll | `ChatPanel` line 3233 follows `User` turns only | A compaction no longer drags the view down. Intended. |
| Relay setting | `ChatPanel` 3083, 3140, 3267 test `turn.Relay` | A compaction row is **not** governed by `relayedMessages`: Hidden does not hide it and Expanded does not open it. It is always shown, collapsed by default. |
| Remote Control mirror, sender | `MirrorProtocol.SelectInterestingRows` uses `ChatTranscript.IsInteresting`; `TurnsFrom` (line ~960) builds `MirrorTurn`s | `IsInteresting` passes the boundary row, so it is relayed. `TurnsFrom` sets the new `c` field. |
| Remote Control mirror, receiver | `MirrorProtocol.AdoptTurns`; `RemoteControlChatSession` 468, 560 and its live add | `AdoptTurns` builds the compaction turn from `c`. The session applies `TryAbsorb` at the same three points as the local one. |
| Cloud panel, pages | `ClaudeCloudChat` 73, 1861, 1874 | Paired inside `Map`. |
| Cloud panel, live stream | the event switch at `ClaudeCloudChat` 1481 handles `user` and `assistant` only | Add a `system` case that folds `CloudStreamRows.RowsFrom` when the payload is a `compact_boundary`; `FoldDurable` (around 1905) applies `TryAbsorb` against the last history turn. `CloudStreamRows.IsTypedMessage` must return false for an `isCompactSummary` row so a compaction does not set the panel busy. |
| Phone | `TranscriptWatcher.ParseFile` → `TranscriptWatch.From` | Section 5. |
| Liveness and handoff | `SessionLiveness.IsATurn`, `TranscriptHandoff.UserMark` count any `user` row | Not changed. A compaction counts as activity, which it is. |
| Codex, Grok | `CodexTranscript`, `GrokTranscript` | Not touched. |

`ChatTranscript.IsInteresting` gains one arm: `line.Contains("\"subtype\":\"compact_boundary\"", Ordinal)`. The substring is independent of key order (the mini's manual row orders its keys differently from the MacBook's rows). The comment above it says the strings must be exactly the row types `MapRow` handles; update it and its test.

### Mirror wire

`MirrorTurn` gains an optional trailing `[property: JsonPropertyName("c")] MirrorCompaction? Compaction = null`, with `MirrorCompaction(g: string? Trigger, b: long? PreTokens, o: long? PostTokens, m: long? DurationMs, s: string Summary, h: bool HasBoundary)`. Written only when set (`WhenWritingNull` is already on), so every other frame is byte for byte what it was. It is a new key, not a new `k` kind, on purpose: `MirrorRelay.ToRelayed` reads an unknown kind as `Peer`, so an older receiver would label a compaction "From another session". With `c`, an older receiver ignores the key and draws `Text` as a system line.

An **older sender** still sends the summary as a `u` turn with the raw text and no flag. The receiver cannot tell that from a user quoting the sentence (the negative control), so it stays a bubble until the far machine is upgraded. Named gap, not fixed by guessing.

## 4. Surface A: parser, model, sessions, wires

Files: `CompactionNote.cs` (new), `RemoteChat.cs` (the one `ChatTurn` member), `ChatTranscript.cs`, `LocalCliChatSession.cs`, `RemoteControlChatSession.cs`, `MirrorProtocol.cs`, `ClaudeCloudChat.cs`, `TranscriptWatch.cs`, `tests/TranscriptTests/Program.cs` (print a compaction turn's fields, as `c38d31712` did for relays), and the two projects that compile `ChatTranscript.cs` and `RemoteChat.cs` in by file: `tests/TranscriptTests/TranscriptTests.csproj` (line 24) and `tools/screen-read-probe/ScreenReadProbe.csproj` (line 40). Add `CompactionNote.cs` beside `RelayedMessage.cs` in both or they stop building; CB-330 hit exactly this, and also had to fix `tools/openclaw-probe`, so build all three.

First commit, pushed before anything else: `CompactionNote.cs` with real `Label`/`Detail`/`Line`/`Tokens` and the `ChatTurn.Compaction` member, so B builds against real types.

**Cloud capture, before writing the cloud case:** on a throwaway cloud session, produce one compaction (`claude --cloud <id> -p "/compact"`, the route `docs/claude-cloud-findings.md` already used) and save the events with `tools/claude-cloud-probe`. Record in `docs/claude-cloud-findings.md` what the boundary and summary events look like, scrub one pair into `Captures/cb356/cloud-*.jsonl`, and make the parser read what is really there. If the cloud's summary row has no `isCompactSummary`, say so on the ticket: the cloud panel then collapses only what it can identify, and the plan's negative control still holds.

### Tests for A

**Console suite / unit (`tests/TranscriptTests/Cb356Captures.cs`, new, beside `Cb330Captures.cs`; add `Cb356Captures.Run(Check)` to `TranscriptSuite.cs` and a `<Compile Include>` to `Orbweaver.UnitTests.csproj` next to line 64).** One check per fixture through `ChatTranscript.Map`, expectations read off the rows:

- `auto-adjacent`: three turns (Assistant, System, Assistant). The middle one: `Compaction` set, trigger `auto`, label "Conversation compacted automatically", detail `246k → 21k tokens`, `Text == Line`, uuid is the boundary's, `HasSummary`, summary contains `## 1. Primary Request and Intent` and none of `<system-reminder>`, `tools-used-before-summary`, `This session is being continued`, `If you need specific details`. No turn has `Role == User`.
- `auto-attachments-between`: same shape with three attachments between; one compaction turn, not two.
- `auto-artifact-prelude`: summary does not contain `artifact-content-authored-by-others`.
- `auto-two-reminders`: no `<system-reminder>` left; the body on both sides of the second block is kept.
- `manual`: label "Conversation compacted manually", detail `117k → 23k tokens`; the user row before it is still a user turn.
- `synthetic-orphan-summary`: one compaction turn, plain label, empty detail, expandable, keyed by the summary's uuid.
- `synthetic-orphan-boundary`: one compaction turn with label and detail, `HasSummary` false.
- `synthetic-unknown-trigger`: plain "Conversation compacted", detail still present, `Trigger == "scheduled"`, one turn, no throw.
- `synthetic-idle-trigger`: "Conversation compacted while idle".
- `synthetic-no-metadata`: plain label, no detail, summary still attached.
- `synthetic-no-post-tokens`: detail `246k tokens`.
- `synthetic-summarize-metadata`: collapses exactly like `auto-adjacent` (proves `isVisibleInTranscriptOnly` is not read).
- `synthetic-snake-case-metadata`: same label and detail as `auto-adjacent`.
- `synthetic-control-user-quotes-summary`: the middle turn is a **User** turn, `Compaction` null, text unchanged.
- `synthetic-control-visible-only`: a **User** turn, `Compaction` null.

Paired controls built in the test from `auto-adjacent`, each one change from a passing case: `isCompactSummary: false` gives a user turn; the boundary with `subtype: "informational"` gives no compaction turn and the summary becomes an orphan; an assistant row inserted between boundary and summary gives two compaction turns (orphan boundary, orphan summary); the boundary with `isSidechain: true` is skipped.

**Unit (`tests/UnitTests/CompactionNoteTests.cs`, new):** `Tokens` at 0, 850, 999, 1000, 1499, 1500, 20628, 245582, 978379, 999499, 999500, 1234567; `Label` for manual, auto, idle, `AUTO`, unknown, null; `Detail` for both, pre only, post only, neither, negative, a string where a number should be; `Strip` per step, with each step's text absent, with an unclosed `<system-reminder>`, with the tail phrase appearing twice (only the last is cut), with a result that would be empty; `FromBoundary` for camelCase, snake_case, no metadata, metadata that is an array; `WithSummaryFrom`; `TryAbsorb` for every pairing of (boundary only, summary only, merged, relay turn, plain system turn, user turn) on each side, asserting the mutation and the false cases; record equality. `ChatTranscriptTests`: `IsInteresting` passes a boundary row with keys in either order and still rejects a `system`/`turn_duration` row; a boundary and summary handed to `Map` in two calls then joined by `TryAbsorb` equal the one-call result. `MirrorProtocolTests`: a compaction turn round-trips `TurnsFrom` → `EncodeTurns` → `DecodeTurns` → `AdoptTurns`; a non-compaction turn's JSON has no `c`; a frame with no `c` decodes; a `u` turn carrying the raw summary text stays a user turn. `ClaudeCloudEventsTests`: a page holding the pair yields one compaction turn; `IsTypedMessage` is false for the summary. `TranscriptWatch` tests: section 5.

**Integration (`tests/IntegrationTests`):** write `auto-attachments-between` to a temp `.jsonl` and read it through `TranscriptWatcher.ParseFile`; append the boundary and the summary in two separate writes to a file a `LocalCliChatSession` is tailing and assert one history turn that ends merged, with one `TurnAdded` and one `TurnUpdated`; add to `MirrorRoundTripTests` a transcript holding the pair arriving as one compaction turn on the far side.

**UI-level session tests (`tests/UiTests`, the existing `LocalCliChatSession*`, `RemoteControlChatSessionTurnTests`, `RemoteMirrorChatSessionTests` classes):** the page-seam absorb on prepend for both sessions; a pending optimistic user bubble is not settled by a compaction turn; the cloud stream's `system` case with a `FakeChatSession`-style feed.

Targeted runs: `dotnet run --project tests/TranscriptTests`; `dotnet test tests/UnitTests --filter "FullyQualifiedName~Compaction|FullyQualifiedName~ChatTranscript|FullyQualifiedName~MirrorProtocol|FullyQualifiedName~TranscriptWatch|FullyQualifiedName~ClaudeCloudEvents"`; integration classes by `--filter`; UI classes with `-- --filter-class`.

## 5. The phone: a typed line in `window` frames

Decision from the PM: the desktop sends a typed compaction line. Design:

A `window` line gains three optional trailing keys, present only on a compaction line, after the existing five (`role`, `text`, `at`, `error`, `cut`):

```json
{"role":"system","text":"Conversation compacted automatically · 246k → 21k tokens","at":1794124800,"kind":"compaction","body":"## 1. Primary Request…","body_cut":true}
```

- `kind: "compaction"` marks the line. `text` is `Compaction.Line`, so a phone that ignores `kind` shows a muted aside that reads correctly.
- `body` is `Compaction.Summary`, cut by the existing `CutText` rule (longest prefix of at most 7,997 bytes on a scalar boundary, plus `…`), with `body_cut: true` when cut. Absent when there is no summary. 76 of 77 real summaries are over the cap, so the phone will nearly always get the first 8,000 bytes; the full text is on the desktop. The alternative, spreading the summary over several lines, was rejected: it breaks "one line is one row of the desktop's parser output", which paging and the diff both lean on.
- `TranscriptWatch.WireLine` gains `Kind` and `Body`/`BodyCut`; `LineJson` appends them via `CanonicalJson` only when set, so **every existing line and every pinned vector is byte-identical**. `FitAlone` cuts `body` before `text` when a line cannot fit an empty window (a body of control characters escapes to six bytes each). `Same` compares `Role`, `Text`, `Kind` and `Body`: a boundary line whose summary arrives later has the same `text`, and without `Body` in the comparison the phone would never be sent the summary.

Confirmed by reading `Orbweaver-Mobile` at `origin/main` `cac3267e`: iOS `ClientFrames.Line` decodes with fixed `CodingKeys` and Android `Window.decode` reads named keys from a `JsonObject`, so both ignore unknown line keys. Not run against a phone.

Tests (`tests/UnitTests`, the existing `TranscriptWatch` and `RelayV4VectorTests` classes): the committed `frames-v4-vectors.json` still replays byte for byte; a compaction line's JSON has the keys in the stated order; a non-compaction line has none of them; a 20,000-byte body is cut to 8,000 with `body_cut`; `FitAlone` on a body of control characters; `Same` false when only `Body` differs; `Diff` restates the line when the summary arrives.

**Mobile gap, to note on CB-356 and not built here:** `Uplift-Foundation/Orbweaver-Mobile` needs (1) the contract addendum and vectors under `contracts/` (the next free number; v10 is the latest on `origin/main`), with the changed diff rule, and (2) iOS and Android to read `kind`/`body` and draw a collapsed row that expands. Until then both phones show the one-line aside, which is already correct and no longer a giant user bubble.

## 6. Surface B: the row

Files: `ChatPanel.axaml`, `ChatPanel.axaml.cs`, `README.md`, UI tests and screenshots.

- **Reuse the CB-330 row.** The `RelayRow` border in the `Turns` `DataTemplate` (`ChatPanel.axaml` line 507) becomes the row for both: bind its `IsVisible` to a new `TurnView.IsCompactRow` (`IsRelay || IsCompaction`). `IsRelay` keeps its meaning, so the relay tests and the Hidden filter are untouched. `IsBubble` becomes `!IsCompactRow`; the italic `Style` rule excludes compaction as it excludes relays.
- **`TurnView` additions:** `IsCompaction` (`_turn.Compaction is not null`); the header line branch in `BuildRelayLine` (label in the normal weight, then the dimmed detail, both from `CompactionNote`; no name, no avatar); `CanExpand` true when `Compaction.HasSummary`; the body built by the existing `BuildBodyOf(Compaction.Summary)`, lazily, selectable through `Adopt` like a relay body. `TurnView` already listens to its turn; when `Compaction` changes (the live merge), re-raise the row's properties and drop the cached line and body.
- **Icon:** in the dot's slot, a drawn `Path` (two short chevrons pointing at each other, the usual "compress" mark) at the dot's size, `#66FFFFFF`. A drawn shape, not a glyph, so the Fluent theme on Windows cannot substitute it. `HasRelayDot` is false for a compaction row.
- **Expand state:** `RelayHost` gains `Dictionary<(DateTimeOffset, CompactionNote), bool> CompactionChoices`. Default collapsed, regardless of `relayedMessages`. The click handler is the row's existing `Relay_PointerPressed` (on the panel's own `Border`, never an orb). State survives the rebuild paths and is gone with the panel, as for relays.
- **README:** two sentences after the relay paragraph (line 1708): what the row shows, that it opens to the summary, that speech skips it, and that it is not affected by **Teammate and peer messages**.
- No setting, no `SettingsWindow` change.

### Tests for B

**UI (`tests/UiTests/ChatPanelCompactionRowTests.cs`, new, `[Collection("Settings")]`, `FakeChatSession`, turns built by hand as `new ChatTurn { Role = System, Text = note.Line, Compaction = note }`):**

- renders as a compact row, collapsed: label and detail visible, body host not visible, no bubble, not right-aligned, not italic;
- a synthesized click expands it and the Markdown body is present (a heading and a list item from the note); a second click collapses it;
- an orphan-boundary note (no summary) has no chevron and a click does nothing;
- an orphan-summary note shows plain "Conversation compacted" and expands;
- expanded state survives `HistoryReplaced` in the same panel and is gone in a new panel;
- `relayedMessages` Hidden: the compaction row is still there, a relay row beside it is not; Expanded: the compaction row is still collapsed;
- a live merge: add a boundary-only turn, then set its `Compaction` to the merged note and raise `TurnUpdated`; the same row gains a chevron and expands;
- `SpeakLatest` with history ending assistant, compaction: speaks the assistant text; with only a compaction: says nothing;
- adding a compaction turn while scrolled up does not move the view;
- a user turn, a system turn and a relay row beside it are styled exactly as before (control);
- the text-scale setting scales the icon and line as it does the relay row.

Run with `dotnet test tests/UiTests -c Release -- --filter-class Orbweaver.Tests.ChatPanelCompactionRowTests` and check the total.

**Screenshots (`tests/UiScreenshots/ChatPanelCompactionRowScreenshots.cs`, modelled on `ChatPanelRelayRowScreenshots.cs`):** two captures of one panel holding a user turn, an assistant turn, an automatic compaction row and an assistant turn after it: `compaction-collapsed` and `compaction-expanded` (the summary from `auto-adjacent`: headings, list, code block). A third, small: a manual row, an orphan "Conversation compacted" row and a relay row together, to show they read as one family. Both rids come from CI.

## 7. Split, order, merge points

**Two engineers.**

- **A:** sections 2 to 5 (model, parser, sessions, mirror, cloud, phone line) and their tests.
- **B:** section 6 and its tests.

They start together. B needs nothing from A but the first commit (the record and the `ChatTurn` member). Shared file: `RemoteChat.cs`, A only. `ChatPanel.axaml.cs` is B only; A does not touch the panel. One PR for the ticket against `develop`. QA runs the full suites and CB-119's guard; engineers run targeted classes.

## 8. Install-path parity

Managed code inside the app assembly, plus one new source file that two tools compile in. No new file to ship, resource, hook, entitlement, setting or native call, so `tools/build-macos-dmg.sh`, `tools/build-macos-app.sh`'s Resources copy, `tools/Orbweaver.iss` and `tools/install-hooks.sh`/`.ps1` should not change. Verify rather than assume:

- `git diff --stat origin/develop...HEAD` shows nothing under `tools/` except the probe csproj line.
- `dotnet build tools/openclaw-probe`, `dotnet build tools/screen-read-probe` and `dotnet run --project tests/TranscriptTests` succeed.
- `.gitattributes` line 24 already pins `tests/TranscriptTests/Captures/**` to LF.
- On the Windows box, QA runs `dotnet run --project tests/TranscriptTests -- <a real transcript there that contains a compaction>` and confirms one compaction turn; no Windows transcript was read for this plan.
- Install on the MacBook, the mini and the Windows PC before CI; on each, open a session that has compacted (the mini has the one manual example) and expand the row.

## 9. Confirmed and assumed

**Confirmed by running or reading the thing itself:** the counts and shapes in section 1 on two Macs; develop's `ChatTranscript.Map` turning a real-shaped summary into a User turn and the boundary into nothing; the fixtures contain no home path, name, email, host or real id (every remaining string listed and read; `tools/check-brand.py` exits 0); both phone decoders ignore unknown line keys (source read at `cac3267e`); the `summarizeMetadata` variant and the snake_case field names `compact_metadata`, `pre_tokens`, `post_tokens` (read off the 2.1.327 binary, which is a declaration about one version).

**Assumed, not run:** that boundary and summary reach the file in one write (the design does not depend on it); the cloud events' shape for a compaction, including whether they use the snake_case names; what trigger word an idle-started compaction records; that an older mirror peer ignores `c` (it ignores `k` the same way, which shipped in CB-330; not re-run); that Windows transcripts have the same shapes; that `precomputed` never means idle in a way a user would recognise; that no other `system` subtype should become a row.
