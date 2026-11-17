# CB-330 plan: teammate and peer messages as a compact, collapsible row

Architect: Ines Carvalho. Read against `origin/develop` at `c670e8e8b`. Branch `feature/compact-teammate-messages`. Fixtures are in `tests/TranscriptTests/Captures/cb330/` with their own README.

## 1. What was measured

Scope of the scan: every `*.jsonl` under `~/.claude/projects` on one Mac that mentions `teammate-message` or `Another Claude session sent a message` (72 files, Claude Code 2.1.303 to 2.1.326, October and November 2026). The other config dirs on the machine were not scanned.

### Which rows carry the wrappers

| Row | Count | Shape of `message.content` |
| --- | --- | --- |
| Plain `user` row (no `isMeta`, no `origin`) in a **lead's** transcript | 1,840 | `Another Claude session sent a message:\n` + one or more `<teammate-message>` tags + `\n\nThis came from another Claude session…` |
| Plain `user` row in a **team member's** transcript (`agentName`, `teamName` set) | 1,117 | One or more `<teammate-message>` tags and nothing else; no peer wrapper |
| `isMeta: true` + `origin.kind == "peer"` | 15 | Peer wrapper around bare text (12) or a `<cross-session-message>` tag (3); never a teammate tag |
| `queue-operation` / `enqueue` | **0** of 493 with content | Never carried either wrapper |

Content is always a JSON string, never a block array, on all 2,972 rows. Nothing sits between or around the tags inside a wrapper: 2,957 of 2,957 rows are tags and whitespace only, and tags are separated by exactly `\n\n`. Rows carry 1 to 18 tags (397 rows carry more than one). No row was a sidechain.

### Payloads, per tag (3,896 tags)

| Side | Body | Count | Notes |
| --- | --- | --- | --- |
| lead | JSON `idle_notification` | 1,433 | `idleReason: "available"` with `result` on 1,431; `idleReason: "failed"` with `failureReason` and no `result` on 2. 623 also carry a `summary` key. |
| lead | plain text | 748 | Always has a `summary="…"` attribute |
| lead | JSON `shutdown_approved` | 16 | `requestId`, `from`, `timestamp`, `paneId`, `backendType` |
| lead | JSON `teammate_terminated` | 14 | `teammate_id="system"`, no `color`; `{"type","message"}` |
| member | plain text | 1,657 | From `team-lead` (no `color`) or another member (`color`) |
| member | one line of JSON `shutdown_request`, then a paragraph of prose | 28 | The body is not valid JSON as a whole |

Attributes seen: `teammate_id` (always), `color` (2,967 tags; values purple, blue, yellow, pink, orange, green, red, cyan), `summary` (2,347). Attribute values are XML-escaped (`&apos;` 530, `&quot;` 117, `&gt;` 23, `&lt;` 10, `&amp;` 9); bodies are not escaped at all. Every body starts and ends with one `\n` inside the tag. The provenance suffix was the same paragraph on all 1,855 wrapped rows.

### The leak path

The reported bubble came through the **plain non-meta `user` row** (`MapUser`'s `content.ValueKind == String` branch, which calls `AddText` with no unwrap). It did not come through the queued path. How I know:

- Every one of the 1,840 lead-side teammate rows is that shape, including rows from `nadia-okafor` with an `idle_notification` whose `result` opens "Understood", as in the screenshot. I did not find the screenshot's exact row (its tag says `color="green"`; the three candidates I looked at say `orange`), so this is the row *shape*, not the row.
- Zero `enqueue` rows carry a wrapper, in the same 72 files.
- Run, not reasoned: `dotnet run --project tests/TranscriptTests -- <file>` on develop over `lead-idle-result.jsonl` prints a `User` turn whose text begins `Another Claude session sent a message:`.

Two more leaks the same run showed:

- The synthetic queued row also comes out as a raw `User` turn. The code path leaks if Claude Code ever writes one; it has not been seen to.
- `peer-meta-cross-session-other-session.jsonl` (the isMeta peer path, with CB-266's unwrap applied) comes out as a `User` turn whose text is the raw `<cross-session-message …>` tag. So a message from another Claude session is drawn as the user's own bubble with a tag in it today.

### Codex and Grok

- **Codex:** 18 rollouts under `~/.codex/sessions`, 0 mention either wrapper or `cross-session-message`. Control: the same search finds 934 `"type"` rows and 40 user-role rows there.
- **Grok:** 1,140 `updates.jsonl` files under `~/.grok/sessions`; one mentions the strings, and that row is a `tool_call_update` (tool output quoting source), not a `user_message_chunk`. Control: 17,577 `sessionUpdate` rows.

Decision 7 therefore resolves to: **do not touch `CodexTranscript` or `GrokTranscript`.** Each gets one test pinning that a user message which is the wrapper stays an ordinary user turn there (so the choice is visible, not accidental).

## 2. Design

### One fork for the lead (default chosen, engineers are not blocked)

The ticket says a plain peer message "without a teammate tag" becomes a "From another session" row. On the isMeta peer path, real rows are of three kinds, and two of them are the user's own words:

| isMeta peer row | Real example | Plan |
| --- | --- | --- |
| Bare text, `origin.from: "unknown"` | `/help`, sent by this app over Remote Control (CB-266) | **Stays a user bubble, unchanged.** `ChatTranscriptPeerRowTests` and `LocalCliChatSessionDesktopTests` pin this, and `LocalCliChatSession.Reconcile` settles the optimistic bubble against it. |
| `<cross-session-message>` whose `from-name` starts with `Orbweaver on ` | A message typed into this app's panel on another machine (`SessionMessenger`) | **User bubble showing the body only.** Today it shows the tag. |
| `<cross-session-message>` from anything else | Another Claude session's `SendMessage` | **Compact row, "From another session"**, with `from-name` beside it. |

The alternative is the ticket's literal reading: all three become compact rows, which turns the user's own sent message into a dimmed system row on reload and breaks CB-266's tests. I recommend the table above.

### The contract between A and B

New file `RelayedMessage.cs`, namespace `Orbweaver`, pure (no window, no settings, no I/O). A owns it; B consumes it. B can start against these signatures immediately and build `ChatTurn`s by hand in tests.

```csharp
public enum RelayKind { Teammate, Peer }

public sealed record RelayedMessage(
    RelayKind Kind,
    string? Name,        // teammate_id, or a cross-session from-name. Null for teammate_id "system".
    string? Color,       // the payload colour name as written ("green"); null when absent
    string PayloadType,  // "idle_notification", "shutdown_approved", "teammate_terminated",
                         // "shutdown_request", "message" for plain text, or an unknown type verbatim
    string Verb,         // see the table below; never empty for Kind.Teammate with a Name
    string Preview,      // one line, whitespace collapsed, at most PreviewMax (140) chars, "…" when cut; may be ""
    string FullText)     // Markdown for the expanded view; never the provenance boilerplate
{
    public const int PreviewMax = 140;

    // "Teammate @hana-moriyama finished", "Team", "From another session".
    // B draws the pieces itself (bold name); this is the plain-text form.
    public string Label { get; }

    // Label, then ": " + Preview when there is one. What ChatTurn.Text holds.
    public string Line { get; }

    // What one transcript string is. Null: ordinary text, show it as before.
    public static Classified? Classify(string content);
}

// Relays in order (one per tag), or OwnText when the wrapper held the user's own words.
public sealed record Classified(IReadOnlyList<RelayedMessage> Relays, string? OwnText);
```

`ChatTurn` (in `RemoteChat.cs`) gains one member:

```csharp
public RelayedMessage? Relay { get; init; }
```

**A relayed turn is `Role = ChatRole.System`, `Text = Relay.Line`, `IsComplete = true`.** That choice is what makes everything that does not know about `Relay` degrade correctly instead of leaking: speech picks the last `Assistant` turn, `Reconcile` ignores non-user turns, the phone's `TranscriptWatch.RoleOf` sends `system`, and an older Orbweaver on the far end of a mirror link draws a dim one-line note. A row with N tags yields N turns sharing the row's uuid, in order, exactly as a multi-text-block row does today.

`RelayedMessage` is a record, so it has value equality. B keys per-message expand state on `(turn.At, turn.Relay)`, which survives the panel rebuilding its `TurnView`s and a session re-mapping its history.

### Verbs (A)

| Payload | Label | Preview | FullText |
| --- | --- | --- | --- |
| `idle_notification`, has `result` | Teammate **@name** finished | `result` | `result` |
| `idle_notification`, `idleReason: "failed"` | Teammate **@name** failed | `failureReason` | `failureReason` |
| `idle_notification`, neither | Teammate **@name** is idle | "" | "" |
| `shutdown_approved` | Teammate **@name** shut down | "" | "" |
| `shutdown_request` | Teammate **@name** asked to shut down | `reason` | `reason` |
| `teammate_terminated` (`teammate_id="system"`) | Team | `message` | `message` |
| plain text | Teammate **@name** sent a message | decoded `summary` attribute, else the first non-empty line | the body |
| unknown `type`, or a body starting `{` that does not parse | Teammate **@name** sent a message | first line of the raw body | the raw body in a fenced code block |
| peer (cross-session, not this app) | From another session | first line of the body | the body |

"finished" for an available idle notification is Claude Code's own word for the same event (the ticket's target screenshot). The JSON `summary` key on an idle notification is the member's last message to someone else ("[to nadia-okafor] …"); it is not used.

### Recognition rules (A)

1. If the content starts with `ChatTranscript.PeerPrefix` **and** contains `PeerSuffixMarker`, the inner text is what is classified. A prefix with no suffix is not the measured wrapper: return null (ordinary text) on the non-meta path, and stay dropped on the isMeta path, as today.
2. Teammate tags are read from the **start** of the (inner) text, anchored, one after another: `<teammate-message` + attributes + `>` … `</teammate-message>`, whitespace between. Text that merely contains a tag later on is not a relay (the negative control). Non-whitespace left over after the last tag becomes one more `Peer` relay rather than being dropped.
3. Attributes are matched individually (`key="value"`), as `BridgeProtocol` does, and decoded with `WebUtility.HtmlDecode`. The body is not decoded.
4. A body is JSON if it parses whole as an object with a string `type`, or if its **first line** does (the `shutdown_request` shape). Anything else starting with `{` is the malformed fallback. Never throw; never return an empty relay list for a recognised wrapper.
5. `<cross-session-message>` inside a peer wrapper is parsed with `BridgeProtocol.ParseInboundMessages`. `from-name` starting with `Orbweaver on ` gives `OwnText` = the body; otherwise a `Peer` relay with `Name` = `from-name`. Bare inner text gives `OwnText` on the isMeta path (CB-266 unchanged) and a `Peer` relay on the non-meta and queued paths, where no real row has ever been seen and the text is certainly not typed by the user.

## 3. Surface A: classifier and parser paths

Files: `RelayedMessage.cs` (new), `ChatTranscript.cs`, `RemoteChat.cs` (the one `ChatTurn` member), `MirrorProtocol.cs`, `RemoteControlChatSession.cs`.

- **`ChatTranscript.MapUser`**, string content, non-meta: call `RelayedMessage.Classify`; relays become system turns via a new private `AddRelays(classified, uuid, at, into)`; `OwnText` goes through `AddText` as a user turn; null falls through to today's `AddText`. This is the fix for the reported leak.
- **`MapUser`, isMeta peer branch:** replace the direct `UnwrapPeerMessage` call with `Classify` under rule 5. `UnwrapPeerMessage` stays `internal` with its tests; `Classify` uses it.
- **`MapUser`, block-array content:** classify each `text` block the same way. No real row has this shape; it costs one call and closes the path.
- **`MapRow`, `queue-operation`/`enqueue`:** same call before the user turn is added. In the PR body this is named as the CB-266 gap decision 2 refers to, with the honest note that no real queued row has carried a wrapper.
- **`MirrorProtocol.MirrorTurn`:** add an optional trailing `[property: JsonPropertyName("k")] MirrorRelay? Relay = null` (a small record mirroring `RelayedMessage`'s seven fields with short names). `TurnsFrom` fills it; `WhenWritingNull` keeps old frames byte-identical for non-relay turns, and `System.Text.Json` ignores an unknown property on an older receiver. **Check before relying on that:** the comment above `MirrorTurn` mentions a hash over the turns; confirm both ends hash the payload bytes and not a re-serialisation.
- **`RemoteControlChatSession`** (the two `MirrorProtocol.RoleOf(t.Role)` sites, lines 474 and 580 on develop): set `ChatTurn.Relay` from the frame. When the frame has none and the role is `u`, run `RelayedMessage.Classify(t.Text)` so a mirror from an **older** Orbweaver, which still sends the raw wrapper as a user turn, is compacted on this side. One private helper used by both sites.
- **`ClaudeCloudChat`** calls `ChatTranscript.Map` (lines 73 and 380) and needs no change; verify with one test that a cloud row carrying the wrapper yields a relay turn.
- `ChatTranscript.IsInteresting` needs no change: every carrier is a `user` or `queue-operation` row.
- **Not touched:** `CodexTranscript.cs`, `GrokTranscript.cs`, `LocalCliChatSession.Reconcile`, `TranscriptWatch.cs`.

### Tests for A

**Console suite / unit (`tests/TranscriptTests/TranscriptSuite.cs`, compiled into `tests/UnitTests`):** add `Cb330Captures.cs` beside `Cb327Captures.cs` (same `CallerFilePath` loader; add its `<Compile Include>` to `Orbweaver.UnitTests.csproj` and to the console project if that one lists files). One check per fixture file, through `ChatTranscript.Map`:

- `lead-idle-result`: one system turn; Relay Teammate, name `hana-moriyama`, colour `green`, type `idle_notification`, verb `finished`, preview and full text `Routine, no report.`; text contains neither `Another Claude session` nor `permission`.
- `lead-idle-summary-suffixed-name`: name `celeste-ngata-2`; preview is the `result`, not the `summary`.
- `lead-idle-failed`: verb `failed`, preview is the `failureReason`.
- `lead-idle-markdown-result`: preview is one line with no newline, at most 140 chars; full text keeps the list and the newlines.
- `lead-shutdown-approved`: verb `shut down`, empty preview.
- `lead-teammate-terminated`: name null, label `Team`, preview `nadia-okafor has shut down.`
- `lead-plain-text-summary-attr`: type `message`, preview is the summary, full text is the body.
- `lead-plain-text-entity-in-summary`: preview has a real apostrophe and real quotes, no `&apos;`.
- `lead-multi-terminated-and-approved`: four turns, same uuid, in file order.
- `member-lead-plain-summary`: name `team-lead`, colour null, no peer wrapper needed.
- `member-lead-shutdown-request`: type `shutdown_request`, verb `asked to shut down`, preview `CB-267 is merged and Done.`
- `member-peer-teammate-color-summary`, `member-multi-mixed-senders`: names, colours, two turns.
- `peer-meta-bare-slash`: one **user** turn, text `/help`, uuid kept (CB-266 unchanged).
- `peer-meta-cross-session-own-app`: one **user** turn, text is the body, no `<cross-session-message`.
- `peer-meta-cross-session-other-session`: one system turn; Relay Peer, name `cb-276`, label `From another session`.
- `synthetic-queued-enqueue-lead-idle`: same relay as `lead-idle-result` (pins the queued path).
- `synthetic-control-user-quotes-wrapper`: one user turn, Relay null, text unchanged (the negative control).
- `synthetic-unknown-payload-type`, `synthetic-malformed-json`: verb `sent a message`; full text contains the raw body; one turn, no throw.

Paired controls, each one change from a passing case: the `lead-idle-result` content with the suffix removed stays a user turn; with a leading sentence before the tag stays a user turn; an isMeta row with `origin.kind` other than `peer` carrying a teammate tag stays dropped.

**Unit (`tests/UnitTests/RelayedMessageTests.cs`, new):** `Classify` directly, one case per row of the verbs table and per recognition rule; preview cut at exactly 140 with `…`; whitespace collapse; empty body; attribute order reversed; unknown attribute ignored; `teammate_id` missing; a tag never closed (null, ordinary text); non-whitespace after the last tag; `Label` and `Line` for every kind; record equality for two identical relays. Extend `ChatTranscriptPeerRowTests` only by adding cases; every existing case must pass untouched. Add one case each to the Codex and Grok unit tests pinning that the wrapper is ordinary user text there. `MirrorProtocolTests`: a relay turn round-trips through `EncodeTurns`/`DecodeTurns`; a non-relay turn's JSON has no `k`; a frame with no `k` decodes.

**Integration (`tests/IntegrationTests`):** write the cb330 fixture rows to a temp `.jsonl` and read them through `TranscriptWatcher` (the file seam the panel really uses), asserting the same turns as the unit cases for one lead row, one member row and the control. Add to `MirrorRoundTripTests`: a transcript containing `lead-idle-result` arrives on the far side as a relay turn; and a frame built the old way (role `u`, raw wrapper text, no `k`) is compacted by the receiver.

Targeted runs: `dotnet run --project tests/TranscriptTests`, `dotnet test tests/UnitTests --filter "FullyQualifiedName~RelayedMessage|FullyQualifiedName~ChatTranscript|FullyQualifiedName~MirrorProtocol"`, and the two integration classes by `--filter`.

## 4. Surface B: the row, the setting, speech, the avatar

Files: `ChatPanel.axaml`, `ChatPanel.axaml.cs`, `OrbweaverSettings.cs`, `SettingsWindow.cs`, a new pure `RelayRow.cs`, `README.md`.

- **Pure decisions in `RelayRow.cs`** (unit-testable, no window): `enum RelayedMessageDisplay { Collapsed, Expanded, Hidden }`; `static bool IsExpanded(RelayedMessageDisplay setting, bool? userChoice)` (the user's click wins over the setting, in both directions); `static bool IsShown(RelayedMessageDisplay setting)`; `static Color? DotColor(string? payloadColor)` mapping the eight colour names through the table `OrbWindow.AgentColors` already holds (move or expose that table rather than copying it); `static string PersonaNameFor(string name)` via `PersonaPool.NamesFor` so `celeste-ngata-2` finds `celeste-ngata` (CB-288, merged as PR #252).
- **`TurnView`:** new bindables `IsRelay`, `RelayLabelParts` (prefix, bold `@name`, verb), `RelayPreview`, `RelayDot`, `RelayAvatar`, `HasRelayAvatar`, `IsRelayExpanded`, `RelayBody` (built with the existing `ChatMarkdown.Parse` path from `Relay.FullText`, lazily on first expand), `CanExpand` (false when `FullText` is empty, so a `shut down` row has no chevron). `IsUser`/`IsSystem` styling must not apply when `IsRelay`.
- **`ChatPanel.axaml`, the `Turns` `DataTemplate` (line 289):** one more branch bound to `IsRelay`: a full-width, left-aligned row, no bubble fill; a 8 px dot or 16 px avatar, the label, a dimmed single-line preview with `TextTrimming="CharacterEllipsis"`, and a chevron. `PointerPressed` on the row toggles; the handler lives on the panel's own `Border`, never an orb (the `ChatPanelPeerInboundScreenshots` note applies). Expanded text is selectable through the existing `Adopt` path.
- **Expand state:** `Dictionary<(DateTimeOffset, RelayedMessage), bool>` on the panel, passed to `TurnView` as a pair of delegates like `Adopt`. Cleared only when the panel closes. It must survive the rebuild paths at lines 3057 and 3066.
- **Setting:** `OrbweaverSettings.RelayedMessages` (`RelayedMessageDisplay`), JSON key `relayedMessages` holding `"collapsed"`, `"expanded"` or `"hidden"`; an unknown or missing value reads as `Collapsed`. Add the key to the list of keys `Save` writes (the comment at line 70 explains what happens if it is missed) and write it in `Save`. Follow `ChatTextScale` (lines 949, 1941, 2697, 3446).
- **`SettingsWindow.ChatRows()` (line 1121):** a second row, "Teammate and peer messages", a three-item combo, with a sentence saying what each does. On change, call a new `ChatPanel.ApplyRelayDisplay()` that walks `Panels` as `ApplyTextScale` does (line 2853): Collapsed and Expanded re-evaluate rows without a user choice; Hidden removes relay rows from `_turns`, and leaving Hidden rebuilds from `_session.History`.
- **Hidden** filters at the three `new TurnView(…)` sites (924, 3057, 3066, 3087) through one helper, so indices into `_turns` and `History` are no longer assumed equal. Line 3057 inserts by history index today; that is the place this breaks if missed.
- **Speech:** `SpeakLatest` (line 2965) already takes the last `Assistant` turn, and relay turns are `System`, so no code change is expected. Pin it with a test rather than trusting it. The orb's speak button (`OrbWindow` lines 2017 and 2042) reads the last assistant text from elsewhere; confirm with a test that a transcript ending in a relay row still speaks the assistant turn before it.
- **Scroll:** `OnTurnAdded` (line 3085) scrolls to the end for `Role == User`. Relay rows are `System`, so an idle notification no longer drags the view down. Intended; say so in the PR.
- **Avatar (decision 6):** resolve in this order and stop at the first hit: a live session whose `SessionStatus.Lead` is this panel's session and whose agent name equals `Relay.Name` (or its `PersonaPool.NamesFor` base) gives its persona portrait via `LocalPersonas.For(sessionId)`, first frame only as `SpeakerAvatar` does; else the payload colour dot; else a neutral dot. The lookup goes through an injected `Func<string, Bitmap?>` on `TurnView` so tests supply it. Two live members matching one name: refuse and use the dot, as `OpenClawSessions.AvatarForAgentName` does.
- **README:** one paragraph beside the text-size paragraph (around line 1691) describing the row and the setting.

### Tests for B

**Unit (`tests/UnitTests/RelayRowTests.cs`):** `IsExpanded` across all nine setting-by-choice combinations; `IsShown`; `DotColor` for the eight real names, an unknown name and null; `PersonaNameFor` for a plain name, `-2`, `_3` and `name-v2`.

**Integration (`tests/IntegrationTests`, the `OrbweaverSettings` round-trip class):** `relayedMessages` survives save and load for each value; a file with `"relayedMessages":"sideways"` loads as Collapsed and does not throw; an unknown key beside it still round-trips; a settings file with no such key loads as Collapsed.

**UI (`tests/UiTests/ChatPanelRelayRowTests.cs`, `[Collection("Settings")]`, `FakeChatSession`):**

- a relay turn renders as a relay row: collapsed, label and preview visible, no user-bubble alignment or fill;
- a synthesized click expands it and the Markdown body is present; a second click collapses it;
- expanded state survives a history reload in the same panel, and is gone in a new panel;
- a relay with empty `FullText` shows no chevron and a click does nothing;
- setting Expanded: rows open by default, and a click still collapses one;
- setting Hidden: no relay row in the list, the turns around it intact; switching back to Collapsed with the panel open brings it back;
- a live `OnTurnAdded` under Hidden adds nothing, and a later non-relay turn lands in the right place;
- `SpeakLatest` with history ending assistant, relay: speaks the assistant text, never `Relay.Line`;
- avatar resolver returns a bitmap: avatar shown, dot hidden; returns null: dot in the payload colour; null colour: neutral dot;
- a plain user turn and a plain system turn beside a relay row are styled exactly as before (control);
- `SettingsWindow.ChatRows()` has the new row, and choosing each item writes the setting.

Run with `dotnet test tests/UiTests -c Release -- --filter-class Orbweaver.Tests.ChatPanelRelayRowTests` and check the total; `--filter` is ignored by this runner.

**Screenshot (`tests/UiScreenshots/ChatPanelRelayRowScreenshots.cs`, modelled on `ChatPanelPeerInboundScreenshots.cs`):** one panel holding a user turn, an assistant turn, a collapsed `finished` row with a colour dot, an expanded row with a Markdown list (text from `lead-idle-markdown-result`), a `Team` row, a "From another session" row, and a row with a persona avatar. A second capture with the setting on Expanded. Both rids come from CI.

## 5. Order, ownership and merge points

- A and B start together. B builds against the contract in section 2 by constructing `ChatTurn { Role = System, Text = …, Relay = new RelayedMessage(…) }` directly; nothing in B's tests needs A's parser.
- A lands `RelayedMessage.cs` and the `ChatTurn.Relay` member **first, as its first commit**, signatures only plus `Label`/`Line`, and pushes it, so B rebases onto real types within the hour instead of carrying a stub. Everything else in A follows.
- Shared files: `RemoteChat.cs` (A adds one property; B does not edit it). No other file is touched by both.
- One PR for the ticket, against `develop`. QA runs the full suites; engineers run targeted ones only.

## 6. Install-path parity (Windows and macOS)

The change is managed code inside the app assembly plus one settings key. It adds no file, resource, hook, entitlement or native call, so nothing in `tools/build-macos-dmg.sh`, `tools/build-macos-app.sh`'s Resources copy, `tools/Orbweaver.iss` or `tools/install-hooks.sh`/`.ps1` should change. Verify rather than assume:

- `git diff --stat origin/develop...HEAD` shows no change under `tools/` and no new non-`.cs`/`.axaml`/`.md`/fixture file.
- The settings key round-trips on both rids (the integration test above runs on both CI legs).
- Path and newline handling: the classifier reads `\n` only. Transcripts on Windows are written by Claude Code with `\n` inside JSON strings, so this should hold; I did not read a Windows transcript. QA: on the Windows box, run `dotnet run --project tests/TranscriptTests -- <a real lead transcript there>` and confirm teammate rows come out as relay turns. Fixture files must be checked out with LF: add `tests/TranscriptTests/Captures/cb330/*.jsonl text eol=lf` to `.gitattributes` if the cb327 captures have an equivalent rule, since a trailing `\r` on the single row is harmless to `JsonDocument` but worth pinning.
- Fonts: the dot and chevron are drawn shapes, not glyphs, so the Fluent theme on Windows cannot substitute them. Check the Windows screenshot for the preview's ellipsis and the bold name.
- Install on the MacBook, the mini and the Windows PC before CI, and on each open a lead session's panel with a live team to see a real row.

## 7. Confirmed and assumed

Confirmed by running: the row counts and shapes in section 1 (one machine, one config dir); the three leaks, through develop's real `ChatTranscript.Map`; the Codex and Grok negatives, each with a control; `tools/check-brand.py` exits 0 with the fixtures in the tree.

Assumed, not run: that `WebUtility.HtmlDecode` handles `&apos;` (A's entity fixture will say); that an older mirror peer ignores the new `k` property and that the mirror's hash is over payload bytes; that Windows transcripts have the same shapes; that bare isMeta peer text is always this app's own send (true of all 12 real rows, which are slash-command sends; a message typed in the claude.ai Remote Control UI was not captured); that Claude Code never queues a teammate message as an `enqueue` row (never seen in 493; a version outside 2.1.303 to 2.1.326 might).
