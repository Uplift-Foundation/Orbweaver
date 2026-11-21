# Model dropdown and effort dial on every chat panel — plan (CB-404)

Architect's plan. Ticket key: to be filled in by the lead. Read against
Orbweaver `origin/develop` 0cd659c8 and Orbweaver-Mobile `origin/develop`
71f5e21 (the phone apps and the contracts live in that second repository, not
under `mobile/` in this one; the `mobile/` directory in Warren's clone is a
nested checkout of it).

Every claim about a CLI below is marked **measured** (run today, 2026-11-21, on
the machine and version named), **read** (from a binary's strings, a doc the CLI
ships, or this repo's findings docs) or **assumed** (nobody has run it).

## 1. What already exists, and the decision that follows

`develop` already has the feature for one CLI. CB-270 Stage 4 gave OpenClaw
native sessions:

- `IRemoteChatRunOptions` and `RemoteRunOptions` in `RemoteChat.cs:682-728`
  (model id, model choices, thinking id, thinking choices, fast, locked, chip
  text, `SetModelAsync`, `SetThinkingAsync`, `RunOptionsChanged`);
- a chip in the panel header strip (`RunOptionsChip`, `ChatPanel.axaml:599`)
  that names what the conversation is running on, and a flyout with a model
  `ComboBox` and a thinking `ComboBox` (`ChatPanel.axaml.cs:1109-1305`);
- the read path off `sessions.list` and the write path through
  `sessions.patch` (`OpenClawRunState.cs`, `OpenClawSessionPatch.cs`,
  `OpenClawSessions.cs:2565-2630`).

**Decision: generalise that interface to every session kind. Do not build a
second mechanism beside it.** The panel then has one code path, OpenClaw keeps
its extra controls (fast mode, time limit), and the phone gets one frame shape
for all five CLIs.

Three things the existing shape lacks and this ticket adds:

1. **Disabled with a reason.** Today `RunOptions` is null when the picker
   should not show, and the chip hides. The requirement is the opposite: shown
   and disabled, with the reason. `RemoteRunOptions` gains
   `ModelDisabledReason` and `EffortDisabledReason` (null = enabled), and
   `Locked` becomes one source of the first.
2. **A reading with provenance.** "What the session is actually running" comes
   from a different place per CLI and can be absent. The reading carries where
   it came from and when, so the chip can say "not known yet" instead of
   guessing.
3. **Optional extras.** Fast mode and the time limit become optional sections
   (`HasFast`, `HasTimeLimit`), shown only for OpenClaw.

## 2. Per-CLI matrix

### 2.1 Claude Code, local session with a terminal pane

| | How | Evidence |
| --- | --- | --- |
| Set model | Preferred: `set_model {model}` control request to the session's Remote Control bridge. Fallback: type `/model <alias>` into the pane. | Control: **measured** on a bridge at 2.1.317/318 (`docs/remote-control-findings.md`), and on stdio at 2.1.330 today, where the answer carries the resolved model: `{"model":"claude-sonnet-5-5"}`; an unknown name answers `error` "Model 'x' not found". Typed: **measured** from 12 real transcripts, e.g. `/model opus` → "Set model to `Opus 5.5` and saved as your default for new sessions". |
| Set effort | Preferred: `apply_flag_settings {settings:{effortLevel}}` to the bridge. Fallback: type `/effort <level>`. | Control on stdio: **measured** today at 2.1.330 (`medium → high → xhigh`, `null` returns to `medium`, each read back through `get_settings`). Control on a bridge: **read** only (`onApplyFlagSettings` is in the bridge handler's table) — **needs one measurement on a throwaway `claude --bg --remote-control` job** with `tools/claude-cloud-probe control`. Typed: **measured** from a transcript ("Set effort level to medium (saved as your default for new sessions)"). |
| Read model | The transcript's last `assistant` row, `message.model`. | **Measured**: 34 of 34 assistant rows in a 2.1.330 transcript. Already on disk and already tailed. |
| Read effort | The same row's top-level `effort` (and `perTurnEffort`). Second source: the pane footer `◐ medium · /effort`. | Transcript: **measured**, 34 of 34 rows at 2.1.330. Whether 2.1.319 (the Windows box) writes the field is **assumed**; where it is absent the footer is read, and where neither exists the dial says so. Footer: fixtures exist in `tests/TranscriptTests/PaneFixtures.cs`. |

Two findings the plan depends on:

- **A typed `/model` or `/effort` also saves the choice as the default for new
  sessions.** That is Claude Code's own behaviour, and it conflicts with "live
  session only". The control route is session-scoped (**measured** on stdio: a
  fresh process after the probe still reported `opus` / `medium`). So the
  control route is preferred whenever the session has a bridge id and "Run
  slash commands in background jobs and Desktop sessions" is on, and the typed
  route is the fallback. When the typed route is used, the flyout says so in
  one line. See fork F1.
- **A bad effort level is accepted silently.** `apply_flag_settings
  {effortLevel:"bogus"}` answered `success` and changed nothing (**measured**).
  So the app validates the level against the model's own list before sending,
  and never treats `success` as proof: the dial shows the new level as pending
  until a reading confirms it.

An interactive terminal session does have a bridge id: both registry rows read
today (`kind: "interactive"`, 2.1.330) carried `bridgeSessionId`.

### 2.2 Claude Code, local session with no pane (background job, Desktop chat)

Same as 2.1 without the typed fallback. With no bridge id the controls are
disabled with the existing sentence (`SlashRouting.PeerSocketReason` or
`DesktopNoBridgeReason`). Reads are unchanged: the transcript is local.

### 2.3 Claude cloud session

| | How | Evidence |
| --- | --- | --- |
| Set model | `set_model` through `ClaudeCloudSend.ControlRequestBody`. | **Measured** on a cloud worker, 2.1.317 (`docs/claude-cloud-findings.md`). |
| Set effort | `apply_flag_settings {settings:{effortLevel}}`. | **Assumed.** Not in the measured set for the cloud worker. The CLI's own text says max "can't be set on a remote session. Use low, medium, high, or xhigh instead" (**read**), so the cloud list drops `max`. **Needs one measurement on a throwaway `claude --cloud` session.** If the worker refuses it, the dial is disabled on cloud with that refusal as the reason. |
| Read model | The events rows' `model` (assistant rows; `system/init`). | **Read** (`docs/claude-cloud-findings.md:62`). |
| Read effort | **Unknown.** `get_settings` is not in the bridge handler's table. Whether the cloud event rows carry `effort` like the local transcript does is **assumed** and is part of the same measurement. | If nothing carries it, the dial shows the last level this app set, labelled as such, or "not reported by a cloud session" before any set. |

### 2.4 Codex

Spiked 2026-11-21 on codex-cli 0.167.0 (the Windows box's version, run on this
Mac in a throwaway private tmux pane, ChatGPT login). **The typed route works;
no picker driving is needed.**

| | How | Evidence |
| --- | --- | --- |
| Set model | Type `/model <id>`. | **Measured**: `/model gpt-6-luna` → "Model changed to gpt-6-luna medium for this session only". Control: `/model no-such-model-xyz` → "Model 'no-such-model-xyz' is unavailable. Use /model to see available models." The id is the lower-case id, not the display name (`/model GPT-6.1 Terra` → "Unexpected argument 'Terra'"). |
| Set effort | Type `/model --reasoning=<level>` (keeps the model), or both at once: `/model <id> --reasoning=<level>`. | **Measured**: `/model --reasoning=low` → "Model changed to gpt-6.2-sol low for this session only". Control: `--reasoning=bogus` → "Reasoning level 'bogus' is unavailable for gpt-6.2-sol. Available: low, medium, high, xhigh, max, ultra." and nothing changed. |
| Read model and effort | The rollout: an `event_msg/thread_settings_applied` row is written at once on a change, with no turn needed, and every turn's `turn_context` carries `model` and `effort`. | **Measured**: changed to `gpt-6-luna low`, sent one turn, `turn_context` read `gpt-6-luna low`; then changed with no turn and a `thread_settings_applied` row for `gpt-6.2-sol` appeared within the 3 s before it was read. |
| Read before the first turn | No rollout file exists until the first turn. The pane footer reads `<Model> <effort> · <cwd>`. | **Measured** (footer "GPT-6.2-Sol high"). Before the first turn the reading is the footer, or "not known yet". |

Codex 0.164.0 and later says "for this session only", so unlike Claude Code's
typed route it saves no default.

**Codex older than 0.164.0 has no argument form, and is set through its
picker.** Bisected on the npm releases: the `--reasoning=<level>` string is
absent in 0.154.0, 0.160.0, 0.161.0, 0.162.0 and 0.163.0, and present in
0.164.0, 0.167.0 and 0.171.0 (the newest). The version is in the rollout's
`session_meta.cli_version`, and before the first turn in the pane banner
("OpenAI Codex (v0.167.0)").

The picker was driven end to end on 0.154.0 (**measured**):

1. Type `/model`, Enter. The screen shows "Select Model and Effort" and
   numbered rows of model ids, the highlighted one marked `›`.
2. Press the row's digit. The screen becomes "Select Reasoning Level for
   <id>" with numbered levels (Low, Medium, High, Extra high, More
   reasoning…).
3. Press the level's digit. The transcript says "Model changed to
   gpt-5.6-terra high" and the footer reads the same.
4. Control: opening the picker, choosing a model, then Esc twice changed
   nothing.

So the old-version route is: read the screen through `IScreenReader`, find the
wanted id's row number, press that digit through `TerminalPress`, read the
second screen, press the level's digit, and confirm from the "Model changed
to" line. Every key is sent only after the screen it expects has been read,
and Esc is the way out of any screen that is not the expected one. The parser
for both screens is pure and gets fixtures cut from these captures. More than
nine rows, and the "More reasoning…" sub-list, were not exercised; a choice
that is not on the first screen is reported as a failure, not guessed at.

**On 0.154.0 the picker saved the choice as the default.** It rewrote
`~/.codex/config.toml` (`model` and a new `model_reasoning_effort` line),
which the spike then put back. Per F1's ruling the flyout says so for an old
Codex, the same way it does for Claude Code's typed route.

A bare `/model` typed by mistake opens a full-screen picker, and text typed
into an open dialog goes to the dialog (the spike's first keystrokes landed in
a "Hooks need review" screen). The set therefore goes through the existing
gated typing (`TypingGate`, not mid-turn), never a blind send.

Not pursued, because the typed route works: 0.167 starts a managed app-server
daemon with a control socket, so `thread/settings/update` may be reachable.

### 2.5 Grok

| | How | Evidence |
| --- | --- | --- |
| Set model | Type `/model <id>`. | **Read**: Grok's shipped user guide, `04-slash-commands.md`. **Not run** (see below). |
| Set effort | Type `/effort <level>`. | **Read**, same file. **Not run.** |
| Read model | Per turn: `chat_history.jsonl` assistant rows, `model_id`. Session-level: `summary.json` → `current_model_id`. | **Measured** on the Windows box, grok 1.0.77: 40 assistant rows carrying `model_id` (`grok-4.7-build`); `summary.json` read `grok-4.7`. |
| Read effort | The same rows' `reasoning_effort`; `summary.json` → `reasoning_effort`. | **Measured**, same files: `high`. |

**M3 could not be run from here.** The Windows box has no pty tooling
reachable over ssh (no tmux, no pywinpty), `grok` over ssh reports "You are
not authenticated", and this Mac's grok (1.0.13) is not logged in. So the
typed commands and how soon `summary.json` is rewritten are still unmeasured.
They need a logged-in interactive Grok: QA runs them on the Windows box
through the installed app's own typing path, with a bogus model as the paired
refusal. Until then the Grok set route is doc-read only.

### 2.6 OpenClaw

Already built and measured (CB-270 Stage 4, `docs/openclaw-findings.md`):
`sessions.patch {model}` / `{thinkingLevel}`, read back off `sessions.list`,
choices from `models.list` with each model's own `thinkingLevels`. Surface
(channel) chats stay without the picker on purpose: their model is the
channel's configuration. Under this ticket they show the controls disabled
with that reason instead of hiding them. A locked row keeps its model disabled
with `OpenClawRunText.LockedModelTip`.

## 3. What is offered, and where the list comes from

| CLI | Models | Effort levels |
| --- | --- | --- |
| Claude (local and cloud) | **Discovered**: the `list_models` control request on a throwaway `claude -p --input-format stream-json` process (hooks disabled, no turn, so no tokens). **Measured** today: it returns `value`, `displayName`, `resolvedModel`, `supportsEffort` and `supportedEffortLevels` per model. Cached per config root, the way `SlashCatalogueCache` caches slash commands. Fallback when the probe fails: the aliases `default`, `opus`, `sonnet`, `haiku`. | The current model's own `supportedEffortLevels` (`low, medium, high, xhigh, max` on every model measured). Cloud drops `max`. A model with `supportsEffort` absent (Haiku 4.5) disables the dial with "this model has no effort setting". |
| Codex | **Discovered**: `model/list` on `codex app-server`, which this app already runs for rate limits. **Measured** at 0.167.0: 10 models, each with `id`, `displayName`, `hidden`, `isDefault`, `defaultReasoningEffort`. | Each model's own `supportedReasoningEfforts` (**measured**; they differ: Luna models lack `ultra`). |
| Grok | **Discovered**: `~/.grok/models_cache.json` (**measured** on 1.0.77: 4 models, each with `name`, `hidden`, `supports_reasoning_effort`). Not `grok models`, which printed a stale two-model list when it ran unauthenticated. | Each model's own `reasoning_efforts` with `id`, `label` and `default` (**measured**: low, medium, high, xhigh). A model with `supports_reasoning_effort` false disables the dial. |
| OpenClaw | `models.list` (existing). | The model's own `thinkingLevels` (existing). |

The model the session is running is always listed, even if the catalogue does
not contain it (the existing OpenClaw rule).

## 4. Design

### 4.1 Pure core (no window, no settings)

New file `RunControls.cs`:

- `RunReading(string? Model, string? Effort, DateTimeOffset? At, RunReadingSource Source)`.
- `RunCatalogue(IReadOnlyList<RunModel> Models)`, `RunModel(Id, Label, ResolvedId, EffortLevels)`.
- `RunControlsResolver.Resolve(cli, route, reading, catalogue, pending) → RemoteRunOptions`.
  One arm per outcome: enabled, disabled-with-reason (no route, locked, model
  has no effort, CLI cannot), value unknown, value pending.
- `RunPending`: a set the app made that no reading has confirmed yet. Cleared
  when a newer reading agrees; turned into a failure note when a newer reading
  disagrees or a timeout sized to the CLI's own cadence passes.
- `RunRoute`: Control, Typed, GatewayPatch or None, decided from
  `SlashChannel` plus whether a bridge exists. This is where "prefer control,
  fall back to typed" lives.
- `EffortDial.Steps(levels)` / `Nearest(level)`: maps the ordered level list
  onto dial positions.

Parsers, each pure and beside the parser that already owns the format:

- `ChatTranscript.LastRunReading(lines)` — last assistant row's
  `message.model` and `effort`, superseded by a later `/model` or `/effort`
  `local-command-stdout` row.
- `ChatTranscript.FooterEffort(pane)` — the `◐ <level> · /effort` footer.
- `CodexTranscript.LastTurnContext(lines)` — `turn_context.payload.model` /
  `.effort`.
- `GrokSummary.Parse(json)` — `current_model_id`, `reasoning_effort`.
- `ClaudeModelList.Parse(json)` — the `list_models` answer.

`SlashRouting.ControlRequest` gains `apply_flag_settings` with a validated
`effortLevel`. `/effort` joins `SlashRouting.ControlSubtypes`, so a typed
`/effort high` in a bridge panel also works; that is the same route, not extra
scope.

### 4.2 Sessions

**The read and the set live in a windowless host, not in a panel session.** A
phone's request never reaches an `IRemoteChatSession`: `RelayRemote` works
from the scan snapshot and types through `RelayInput`
(`RelayRemote.cs:366-377`). The linked-desktop server is in the same position,
which is why `MirrorSlashHost` exists. So:

- New `RunControlsHost` (the `MirrorSlashHost` pattern): `Read(status)` returns
  the resolved `RemoteRunOptions` for a local session, and
  `SetAsync(status, field, value)` runs the change by its route and returns
  null or the failure sentence. Its seams are the ones `MirrorSlashHost`
  already takes (gated typing, gated control post, registry lookup), so it is
  unit-testable with fakes.
- `LocalCliChatSession` (one class for Claude, Codex and Grok panes; the
  format comes from `CliChatFormat.For(status.Source)`) implements
  `IRemoteChatRunOptions` by calling the host.
- `ClaudeCloudChatSession` implements it over its own control channel and
  events rows. `ClaudeCloudRoster` already parses `external_metadata.model`
  (`ClaudeCloudRoster.cs:69`), which nothing displays today; it is the
  reading before the first event row arrives.
- `RemoteControlChatSession` implements it over the mirror (4.6).
- `OpenClawChatSession` is adapted to the widened record and to "disabled
  with reason" for surface chats.
- `SessionStatus` and the hook status file gain nothing. The readings come
  from files the app already tails.

### 4.3 Desktop UI

Keep the chip on its own strip under the header and the flyout it opens. The
comment at `ChatPanel.axaml:593` records why three controls do not fit in the
header row. Changes:

- The chip is visible for every session that implements the interface, and
  reads "<model> · <effort>". Unknown parts read "model not known yet".
- The flyout's thinking `ComboBox` becomes the **effort dial**, a round
  control (4.7).
- When the route in use also changes the default for new sessions (Claude
  Code's typed route, Codex before 0.164.0), the flyout says so in a line
  under the control before anything is chosen.
- A disabled control stays visible, greyed, with the reason as its tooltip and
  as a line of text under it (a tooltip alone is invisible on touch and in a
  screenshot).
- A pending change shows the chosen value with "applying…" until a reading
  confirms it.

### 4.4 Relay contract (frames v13, Orbweaver-Mobile `contracts/`)

**Superseded by the contract itself.** `contracts/frames-v13.json` in
Orbweaver-Mobile (branch `feature/model-effort-controls`) is now the
authority, with `frames-v13-vectors.json` and `effort-dial-vectors.json`; where
the sketch below disagrees, the contract wins. What changed, and why:

- `pending` is the pending id, not `true`: the dial draws its pointer at the
  chosen level with the confirmed level still marked, which needs both.
- No `label` beside `id`: the label is the matching choice's.
- Two additions: a per-field `failed` sentence (how a phone learns that a
  change it was told `ok` for did not take) and `saves_default` (F1's warning).
- `bad_field` is `bad_frame`, and `failed` is `set_failed`. The codes are
  `bad_frame`, `unknown_session`, `unavailable`, `bad_value`, `set_failed`, in
  that check order.
- Ids have their own grammar (1 to 64 printable ASCII characters, no space,
  quote or backslash), not `launch.model`'s, which would refuse a Claude alias
  with brackets. The desk hands a route only a value found in its own choices.
- A change that only waits for a reading does not block the next one. A set
  is refused `unavailable` only while the host is running another change to
  that session. Claude Code and Grok confirm effort with the next turn, so a
  pending can last minutes, and the model list must not freeze that long.
- The phone accepts a `run` frame only between `ok watching` and the watch's
  end, which discards a stale frame the relay stored while it was away.
- The dial's drag keeps an unclamped running angle; that, not a clamp in
  `IndexFor`, is what stops a drag across the gap wrapping (section 4.7's
  `IndexFor(angle)` alone would flip highest to lowest at bottom centre).

The sketch as first planned:

Copy frames-v11's shape. Additive; `hello.proto` stays 2; the relay needs no
change because it forwards sealed frames blind.

**Scope the phone can reach.** The relay roster carries only local CLI
sessions (`IsLocalCli`: Claude, Codex, Grok; `RelayRoster.Project`). Cloud and
OpenClaw sessions are not on the phone at all today, so "every chat panel on
mobile" means those three. Putting cloud and OpenClaw sessions on the phone
is its own feature and is not in this plan.

On the desk, `set_run` is a new arm in `RelayRequests.Parse`
(`RelayFramesV4.cs:136`) and `RelayRemote.HandleAsync`, which calls
`RunControlsHost.SetAsync`. The `run` frame is sent from `RelayRemote.Tick`
for watched sessions, only when its bytes change, the way `SendCaps` does
(`RelayLink.cs:827`). An unknown `t` from a phone is already ignored without
a reply (`RelayLink.cs:433-440`), which is what makes the capability gate
necessary.

- **Capability**: `caps` gains `"run_options"`. A phone shows the controls only
  for a desk that advertises it. An old phone ignores the name.
- **State frame**, desktop → phone, for watched sessions only, its own
  coalescing key `Ck(ck_key, "run:" + session_id)` so the byte-pinned roster
  is untouched:

  ```json
  {"t":"run","session_id":"…",
   "model":{"id":"opus","label":"Opus 5.5","choices":[{"id":"…","label":"…"}],"off":"reason, present only when disabled","pending":true},
   "effort":{"id":"high","label":"high","choices":[…],"off":"…"}}
  ```

  `id` is absent when the value is not known. Choices are capped (count and
  bytes) with the limit in `limits`.
- **Action**, phone → desktop: `{"t":"set_run","session_id":"…","field":"model"|"effort","value":"<id>"}`.
  `value` uses `launch.model`'s grammar. Unsigned, like `input` and
  `list_dir`. See fork F3.
- **Replies** by `ref`: `ok set`, or `err` with `unknown_session`,
  `bad_field`, `bad_value`, `unavailable` (with `why`) or `failed` (with
  `why`).
- **Back-compat**: new phone + old desk: no cap, controls not shown. Old phone
  + new desk: unknown state `t` is logged and ignored on both platforms
  (frames-v8.md, frames-v11.json). An old desk leaves an unknown action alone
  and the phone's 10 s timeout answers.
- **Vectors**: `frames-v13.json`, `make_v13_vectors.py`,
  `check_v13_vectors.py`, `test_check_v13_vectors.py` with the blindness
  check, landed in Mobile first and then copied to
  `tests/UnitTests/Fixtures/relay/` with a `SOURCE.md` entry. Desktop side:
  `RelayV13VectorTests`; relay side: `RelayV13BlindnessTests`.

### 4.5 Phones

- **iOS** (`app/Screens/SessionScreen.swift`): a model `Menu` and an effort
  stepper in `SessionHeader`, replacing the "Stop" pill that has no action
  today, or as a chip row above the composer if the header is too tight at
  the smallest width. Decode in `RosterReducer.swift` / a new
  `RunFrames.swift`; send through `LiveClient.sealAndSend`, gated on the cap
  like `list_dir` (`LiveClient.swift:713`).
- **Android** (`SessionScreen.kt`): two chips in the composer's chip row
  beside "Hands-free"; decode in `Roster.kt`, send through `sendRequest`.
- The Watch target does not mirror chat. No Watch work.

### 4.7 The effort dial

A round dial on all three platforms, built once per platform as a reusable
control over one shared, pure mapping.

- **Pure mapping** (`EffortDialMath` on desktop, the same functions ported to
  Swift and Kotlin, all three checked against one table of vectors in
  `contracts/`): the levels sit at equal steps on a fixed arc with a gap at
  the bottom. `AngleFor(index, count)`, `IndexFor(angle, count)` (nearest
  detent, clamped at the arc's ends so a drag across the gap does not wrap
  from lowest to highest), `IndexFor(point, centre)`, and `Step(index, delta,
  count)`. One level, and zero levels, are cases with stated answers.
- **Desktop control** (`EffortDial.cs`, an Avalonia `TemplatedControl` or a
  drawn `Control`): an arc with a tick and a short label per level, a pointer,
  the current level's name in the middle. Drag or click a tick to choose; the
  change is committed on release, not during the drag, so one gesture is one
  request. Left/Down and Right/Up step, Home and End jump, and it is a single
  tab stop. Automation: a range-value peer whose value text is the level's
  name. Disabled draws greyed with the reason beneath. A pending value draws
  the pointer at the chosen level with the confirmed level still marked.
- **iOS** (`EffortDial.swift`, SwiftUI): the same drawing; a drag gesture;
  `accessibilityAdjustableAction` for increment and decrement and
  `accessibilityValue` as the level's name; Dynamic Type for the labels.
- **Android** (`EffortDial.kt`, Compose `Canvas`): the same; `pointerInput`
  drag; `semantics { progressBarRangeInfo; setProgress; stateDescription }`.
- **Tests**: the vectors on each platform; a control test per platform for
  drag, tick click, each key, disabled and pending; a screenshot per platform
  with the dial enabled, disabled-with-reason and pending.

On a phone the dial is large enough to drag, so it opens from the effort chip
as a sheet, with the model list beside it.

### 4.6 Linked desktops (a panel on machine A for a session on machine B)

The mirror protocol gates features by optional roster fields, not by version
(`PeerProtocol.Version` stays 1; an old server drops an unknown frame type
silently, `RemoteMirrorServer.cs:588-630`). Follow the slash precedent, where
the presence of `routes` is the capability:

- **State**: a trailing optional `run` field on `MirrorRosterEntry`
  (`MirrorProtocol.cs:561-679`), omitted when null so the CB-216 roster hash
  stays stable for sessions that have none. It carries the resolved options
  from the far side's `RunControlsHost.Read`. Its presence is the capability.
- **Set**: an `INPUT` frame with a `run=model|effort` flag beside the value,
  the way `slash=1` rides on `INPUT` today (`RemoteMirrorClient.cs:1168`).
  The far side routes it to `RunControlsHost.SetAsync` and answers `OK` with
  the existing `via` / `why` / `verdict` fields.
- **Old far side**: no `run` field, so the near panel shows the controls
  disabled with "the Orbweaver on <machine> is too old to change this", in
  the manner of `OldFarBuddyNote`.
- **Old near side**: ignores the unknown roster field.

Tests extend `PanelOverTheLinkTests`, `MirrorProtocolTests` and
`MirrorSlashHostTests`' pattern.

## 5. Forks for the PM (one question each, with a recommendation)

Warren answered all four on 2026-11-21. They are settled.

- **F1. Decided: type it and warn.** Typed fallback saves a new-session default. When a Claude terminal
  session has no usable bridge, the only route is typing `/model` or
  `/effort`, which Claude Code also saves as the default for new sessions.
  Options: (a) use it and say so in the flyout (**recommended**: the control
  works everywhere and the side effect is stated); (b) disable the controls on
  that route with the reason. Cost of (b): the feature is off for anyone who
  turned the bridge setting off.
- **F2. Decided: a literal round dial** on desktop, iOS and Android, not a
  slider. See 4.7.
- **F3. Decided: unsigned, no Face ID.** Phone's set action unsigned. Precedent: `input` and `list_dir` are
  unsigned; `launch` and `answer` need Face ID. A model change cannot approve
  anything or start a process, so **unsigned is recommended**. The cost it
  accepts: a paired phone can move a session to a more expensive model without
  a biometric check.
- **F4. Decided: Codex must be settable before this ships.** It is, on every
  version: typed arguments from 0.164.0, the picker before it (2.4). What is
  left to prove is Windows (section 7, S1).

## 6. Work split

After the spike and the measurements, six surfaces. E1 owns `RunControls.cs`,
`RunControlsHost.cs` and the parsers; E2 owns the session classes and
`ChatPanel`; E4 owns `Relay*.cs` and the mirror files. E2 and E4 both touch
`RemoteControlChatSession` (E2: the interface; E4: the wire), so E4 lands the
wire first and E2 rebases onto it:

| # | Surface | Repo | Owner | Depends on |
| --- | --- | --- | --- | --- |
| E1 | Pure core: `RunControls.cs`, the five parsers, `SlashRouting` effort arm, unit tests, console-suite cases | Orbweaver | engineer (sonnet/opus) | — |
| E2 | Sessions + desktop UI: the three session classes, OpenClaw adaptation, chip, flyout, the round dial control (4.7), UI tests, screenshots | Orbweaver | engineer | E1's record shapes (agreed up front, below) |
| E3 | Contract v13: frames file, make/check/test_check, relay blindness test | Mobile | architect | — |
| E4 | Desktop relay and mirror: `caps`, `run` frame, `set_run` handling, vector tests; the mirror roster field and `INPUT` flag (4.6) | Orbweaver | engineer | E3's file, E1's shapes |
| E5 | iOS | Mobile | engineer | E3 |
| E6 | Android | Mobile | engineer | E3 |

E1, E3 start at once. E2 and E4 start against the record shapes in 4.1 and
4.4, which are fixed by this plan so nobody waits on a merge. E5 and E6 start
when E3's JSON is pushed, not when it merges. QA writes its matrix while E1
builds. Mind the pane limit: about six teammates at once.

## 7. Before anyone builds (lead or QA, about 30 minutes total)

- **S0. Codex spike: done**, see 2.4.
- **S1. Codex on Windows: not measured.** Both routes were run on macOS
  through tmux. On Windows the keys go through `WriteConsoleInput` and the
  screen through `WindowsConsoleScreenReader`. QA runs both routes on the
  Windows box through the installed app (it has 0.167.0; the picker route
  needs a 0.163 or older `codex` on the side).
- **M1. Attempted, blocked.** A throwaway `claude --bg --remote-control` job
  (2.1.330, Haiku) came up with a `bridgeSessionId` and its first assistant
  row read `effort: medium`. `tools/claude-cloud-probe control` then could
  not post: "no usable credential: the Keychain could not ask for permission
  — no one is at the screen, or this is a background context". That guard is
  deliberate and was not worked around. M1 has to be run from a session with
  someone at the screen, or by QA through the installed app, which holds the
  grant. Steps: post `{"subtype":"m1_no_such_subtype"}` (expect the bridge's
  refusal), post `apply_flag_settings {settings:{effortLevel:"high"}}`, send
  one turn, read the next assistant row's `effort`.
- **M2.** The same on a throwaway cloud session, and whether its event rows
  carry `effort`.
- **M3. Not done**: Grok's typed `/model` and `/effort` and the timing of
  `summary.json`, on a logged-in interactive Grok (see 2.5).

## 8. Test plan (100% of added lines)

- **Unit** (`tests/UnitTests`): one case per resolver arm; every parser
  against fixtures cut from real files (the 2.1.330 transcript rows, the
  0.164.0 `turn_context`, the 1.0.77 `summary.json`, the `list_models`
  answer), each with a negative (row without the field, malformed JSON, a
  `/model` refusal such as "Model 'x' not found"); `ControlRequest` for
  `apply_flag_settings` including the rejected level; pending cleared,
  contradicted and timed out; `RelayV13VectorTests`.
- **Console suites**: `TranscriptTests` gains the reading and footer cases.
- **Integration** (`tests/IntegrationTests`): control payload bytes for
  `set_model` and `apply_flag_settings` beside the existing
  `LocalCliSlashControlPayloadTests` and `ClaudeCloudSendPayloadTests`;
  readings from real temp `.jsonl` and `summary.json` files through
  `TranscriptReader`'s tail window.
- **UI** (`tests/UiTests`, in `[Collection("Settings")]` where settings are
  read): `FakeChatSession` implements the interface; chip text per state;
  flyout enabled, disabled-with-reason (tooltip and text), pending, failure
  sentence restoring the old value; dial snapping; OpenClaw extras still
  present and absent for other CLIs.
- **Screenshots** (`tests/UiScreenshots`): chip + open flyout for enabled,
  disabled-with-reason and pending.
- **Mobile**: contract check and mutation tests in CI; iOS `ShellTests`,
  vector tests and `SessionScreenSnapshotTests`; Android `:relay:test` vector
  tests and Robolectric `ScreenTests`.
- **Lines that cannot be covered** are named in the PR body: the throwaway
  `claude -p` process launch for `list_models` sits behind a seam and the real
  launch is the uncovered line.

## 9. Install and parity

- No installer change is expected: no new files outside the app binary, no
  hook change. QA confirms by reading `tools/build-macos-app.sh`,
  `tools/Orbweaver.iss` and the hook installers against the diff.
- Real-machine proof, paired with its negative control, per CLI: change the
  model from the panel, send one turn, and read the next transcript row's
  model; the control is a session whose bridge setting is off showing the
  typed route or the disabled reason. MacBook for Claude, the mini for
  OpenClaw and the headless case, the Windows box for Codex and Grok.
- Phone: one paired iPhone and one Android build against a desk with and
  without the capability.

## 10. Not verified

- Effort over a Remote Control bridge and over a cloud worker (M1, M2).
- Codex was measured on macOS only (typed arguments at 0.167.0, the picker at
  0.154.0), not on Windows. A picker with more than nine rows and the "More
  reasoning…" sub-list were not exercised.
- Grok's typed commands were read from its guide, not run (M3).
- Whether Claude Code older than 2.1.330 writes `effort` on assistant rows.
- Whether `list_models` differs by account or by config root beyond what one
  probe on one account returned.
- That an old phone ignores an unknown state frame was read from the
  contracts and the phone code, not exercised against a built old phone.
- The mirror design in 4.6 is from reading the protocol code; nothing was run
  across two machines.
