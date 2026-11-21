# CB-403 captures: a lead pane with an agent team

`tmux capture-pane -p` of one throwaway Claude Code 2.1.330 session on the Mac
mini, 2026-11-21, in a detached tmux window of 80x24 with `teammateMode: tmux`,
which leaves the lead a pane of 24 columns by 23 rows. The session spawned two
teammates (`scratch-one`, `scratch-two`) that only ran `sleep 900`. Nothing here
comes from anybody's working session.

"Typing" below means the app's own transport, `set-buffer` + `paste-buffer -p`
+ `send-keys Enter`, run back to back.

| capture | how it was reached | what typing does | should read |
| --- | --- | --- | --- |
| `team-idle-w24` | idle, footer at rest | message to the lead | prompt |
| `team-draft-w24` | a draft in the box | message to the lead | prompt |
| `team-after-send-w24` | after a turn | message to the lead | prompt |
| `team-sel-main-w24` | one Down arrow: `❯ ⏺ main` | pasted text lands in the box and clears the selection | prompt |
| `team-sel-teammate-w24` | two Down arrows: `❯ ◯ scratch-two` | a pasted lone `x` plus Enter became a turn for the lead | prompt |
| `team-sel-stopped-w80` | a stopped teammate's row selected, 80 columns | not typed into | prompt |
| `teammate-view-w24` | Enter on a teammate's row | the box is the teammate's (`Message @scratch-two…`) | unknown, `teammate-view` |
| `teammate-view-draft-w24` | a pasted draft in that box | landed in the teammate's box; not sent | unknown, `teammate-view` |
| `teammate-view-sel-teammate-w24` | just after Enter, `❯` still on the row | as above | unknown, `teammate-view` |
| `teammate-view-sel-main-w24` | Down arrow inside the teammate's view: `❯ ◯ main` | not typed into | unknown, `teammate-view` |
| `background-dialog-w24` | Left arrow at the prompt | a dialog | dialog |

Before CB-403 the three `team-sel-*` screens read unknown (`no-box-top`), which
is the refusal that was reported, and the first two `teammate-view-*` screens
read as the prompt.

Not safe, and measured: the same lone `x` sent with `send-keys -l` instead of a
bracketed paste, on a selected teammate row, stopped that teammate (the hint
row there reads `enter to view · x to stop`). The reading "prompt" for the
`team-sel-*` screens holds for bracketed paste only.

Not found: a screen that reads `no-prompt-mark`, which the same incident also
logged once.
