# CB-404 QA test matrix (draft 1)

Legend: D=desktop panel, P=phone (iOS/Android). Every set has a paired negative.
Rows = CLI x route. Each cell: set model / set effort / read both.

| CLI / route | Set model (+neg) | Set effort (+neg) | Read model+effort (+neg) |
|---|---|---|---|
| Claude local, pane, bridge on | set_model -> next assistant row model; neg: unknown name -> "not found", panel restores old | apply_flag_settings -> next row effort; neg: "bogus" validated app-side, never sent; success != proof, dial shows pending then confirms | transcript row model/effort; neg: row w/o effort -> footer; neither -> dial says so |
| Claude local, pane, bridge OFF | typed /model; flyout shows "saves as default" line | typed /effort, same line | same as above |
| Claude local, no pane (bg job/Desktop) | controls disabled w/ reason when no bridge id | same | reads still work |
| Claude cloud | set_model | effort (M2); max absent from list; refusal -> dial disabled w/ reason | event rows model; effort maybe "last set, labelled" |
| Codex >=0.164 | typed /model <id>; neg bogus id -> unavailable, unchanged | /model --reasoning=<lvl>; neg bogus -> unchanged | rollout thread_settings_applied / turn_context; pre-turn: footer or "not known yet" |
| Codex <0.164 | picker via screen reader+digits; Esc exit; >9 rows -> reported failure | picker level screen; "More reasoning..." -> failure, not guess | footer + "Model changed to" |
| Codex Windows (S1) | WriteConsoleInput route | same | WindowsConsoleScreenReader footer |
| Grok | typed /model (M3); neg bogus | typed /effort (M3); model w/o reasoning -> dial disabled | chat_history model_id/reasoning_effort; summary.json timing |
| OpenClaw | sessions.patch; locked row disabled w/ tip | thinkingLevel | sessions.list; surface (channel) chats: disabled w/ reason |
| Linked desktop (4.6) | panel on A for session on B | same | mirrored |

Phone (iOS + Android), each of the above: chip text per state (enabled / disabled-with-reason / pending), flyout, failure sentence restoring old value, dial snapping, frames v13 vectors, old phone ignores unknown state frame, desk without capability hides/disables.

Gates per push: added-line coverage 100% (tools/coverage.sh --base, merged 4), CI green both rids at sha, screenshots both rids (chip + flyout x enabled/disabled/pending), installs hash-checked on 3 machines under /tmp/ow-install.lock, installer files unchanged (build-macos-dmg/app, Orbweaver.iss, install-hooks).

## Phone contract behaviours a machine must show (frames-v13, Orbweaver-Mobile 01d45c3)

Source: contracts/frames-v13.json (`phone_rules`, `desktop_rules`, `back_compat`, `not_verified`), frames-v13-vectors.json, effort-dial-vectors.json. Each has a paired negative.

1. Disabled control shown with its reason as text, never hidden. Neg: desk without `run_options` cap -> no controls at all.
2. `ok set` is not confirmation: control shows applying until a reading confirms; the desk's `failed` sentence shows if it does not. Neg: desk accepts, never confirms -> pick dropped with sentence.
3. (Changed, Mobile a407b9a) A pending change does NOT disable anything: a control awaiting a reading shows applying and can be changed again (same-field change replaces the pending one); the other control stays usable. Refused `unavailable` only if a change arrives while the desk is still running an earlier one for that session (seconds). Phone disables both controls only from tap until the desk's reply. Real-machine case: on a Claude session set effort, send NO turn, change the model: it must go through with effort still showing applying. Neg: a second set_run during the desk's in-flight run -> `unavailable`.
4. Typed route (bridge setting off) and Codex < 0.164 show "Changing this also changes the default for new sessions on <machine>." Neg: bridge route shows no such line.
5. Dial drag across the bottom gap stays at the end it left, never jumps lowest to highest.
6. New phone + old desk: no controls. Old phone + new desk: nothing changes (code-read only; exercise against a built old phone).
7. A stale `run` frame the relay stored while the phone was away is not drawn; only frames after `ok watching` count.
Also: contract checker + 126 mutations run in relay-tests.yml; confirm it ran green at the sha.

## Added check (S1, Codex version)

Before the first turn, Codex controls must leave "checking Codex version..." within a few seconds (Windows box and the mini). The version is read from the session process image and must resolve to a binary named codex. Neg: if it stays on "checking...", npm node launcher is probably the recorded pid; capture what the pid resolves to and report it.

## Added row (linked desktop panel, found by Warren, not by QA)

A panel on machine A for a session on machine B (MacBook viewing a mini session; the session id is rc:...). The chip, flyout, set and read must work on this panel exactly as on a local one: it is RemoteControlChatSession, not LocalCliChatSession, and must implement IRemoteChatRunOptions. Check for EVERY session class the app can open a panel on: local CLI, cloud, OpenClaw, remote-control (rc:), phone-relay. Neg: a peer too old to change this shows "the Orbweaver on <machine> is too old to change this" (disabled with reason, not hidden). The first matrix row read only "panel on A for session on B | same | mirrored" and was never run; that gap let the hidden chip through.

## Added check (Codex 0.154 picker timing, from juno)

After a picker set on Codex 0.154 the host marks the change pending and waits for the rollout (thread_settings_applied). The picker already read "Model changed to <id> <level>" off the screen. If 0.154 writes thread_settings_applied slowly, the 15 s timeout would say "may not have applied" for a change that did. UNMEASURED: set via the picker on a 0.154 pane (not %242 without Warren), time the rollout row against the screen line. Neg: a picker set Esc-cancelled changes nothing and no pending shows.

## Results recorded (real machines)

- Model and effort set confirmed on build 9de638248 (mini and MacBook): Warren's set from the panel, `mirror-log.txt` `via=set run=model` not refused, and the next transcript row read `claude-sonnet-5-5` at `low`. The typed route also saved a default in `~/.claude/settings.json` (expected, F1).
- Codex 0.154 picker timing: the screen line "Model changed to <id> <level>" and the rollout `thread_settings_applied` rows arrived together, within ~0.1 s (one run, one pane). Esc-cancel changed nothing. The picker rewrote `~/.codex/config.toml`, restored after.
- Not measured by QA: M1, M2, M3, S1.
