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
