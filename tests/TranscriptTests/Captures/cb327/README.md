# CB-327 captures

Real `tmux capture-pane -p` output, byte for byte, taken 2026-11-14 on a MacBook from throwaway Claude Code 2.1.325 sessions in a scratch tmux 3.7c server. Nothing here was composed by hand. The session was started with `claude -n long-session-name` in a scratch directory under `/private/tmp`, so no home path or person's name is on any screen. The file name carries the pane width (`-w23` is 23 columns); each was taken by resizing the window and waiting for the redraw, except `idle-short-w23` and `idle-unnamed-w23`, whose sessions were started at 23 columns.

These are raw material for fixtures. Turn the ones a test needs into `PaneFixtures` constants (or load them as files) rather than retyping them.

## What `upstream/develop` at 07b86eb7 makes of each

Produced by `dotnet run --project tests/TranscriptTests -- <file>`, which prints the gate's reading, the form and the dialog for one capture.

| capture | pane reading (check) | form | should be |
| --- | --- | --- | --- |
| `form1-w100` | Dialog (dialog) | 1 tab(s), 5 option(s) | Dialog, form minted |
| `form1-w23` | Unknown (no-prompt-mark) | none | Dialog, form minted |
| `form1-w32` | Unknown (no-prompt-mark) | none | Dialog, form minted |
| `form1-w60` | Dialog (dialog) | 1 tab(s), 5 option(s) | Dialog, form minted |
| `form4-w100` | Dialog (dialog) | 5 tab(s), 6 option(s) | Dialog, form minted |
| `form4-w23` | Unknown (no-prompt-mark) | none | see below: taller than the pane |
| `form4-w32` | Unknown (no-prompt-mark) | none | Dialog, form minted |
| `form4-w60` | Dialog (dialog) | 5 tab(s), 6 option(s) | Dialog, form minted |
| `form4long-w100` | Dialog (dialog) | 5 tab(s), 6 option(s) | Dialog, form minted |
| `form4long-w40` | Unknown (no-prompt-mark) | none | Dialog, form minted |
| `form4long-w60` | Unknown (no-prompt-mark) | none | Dialog, form minted |
| `form4long-w80` | Unknown (no-prompt-mark) | none | Dialog, form minted |
| `idle-after-turns-w20` | Unknown (no-box-top) | none | Prompt |
| `idle-after-turns-w23` | Unknown (no-box-top) | none | Prompt |
| `idle-named-draft-w23` | Unknown (no-box-top) | none | Prompt |
| `idle-named-w100` | Prompt (prompt) | none | Prompt |
| `idle-named-w12` | Unknown (no-box-top) | none | Prompt |
| `idle-named-w15` | Unknown (no-box-top) | none | Prompt |
| `idle-named-w18` | Unknown (no-box-top) | none | Prompt |
| `idle-named-w20` | Unknown (no-box-top) | none | Prompt |
| `idle-named-w23` | Unknown (no-box-top) | none | Prompt |
| `idle-named-w32` | Prompt (prompt) | none | Prompt |
| `idle-named-w40` | Prompt (prompt) | none | Prompt |
| `idle-named-w60` | Prompt (prompt) | none | Prompt |
| `idle-named-w80` | Prompt (prompt) | none | Prompt |
| `idle-short-w23` | Prompt (prompt) | none | Prompt |
| `idle-unnamed-w23` | Prompt (prompt) | none | Prompt |
| `model-w100` | Dialog (dialog) | none | Dialog: develop offers the picker as buttons, and a fix does not take that away |
| `model-w60` | Dialog (dialog) | none | Dialog (same) |
| `model-w40` | Unknown (no-prompt-mark) | none | Dialog while fully on screen (same) |
| `model-w32-h60` | Unknown (no-prompt-mark) | none | Dialog while fully on screen (same) |
| `model-w23` | Unknown (no-prompt-mark) | none | not Prompt |
| `model-w32` | Unknown (no-prompt-mark) | none | not Prompt |
| `modelswitch-w20` | Dialog (dialog) | none | Dialog |
| `modelswitch-w23` | Dialog (dialog) | none | Dialog |
| `modelswitch-w60` | Dialog (dialog) | none | Dialog |
| `permissions-w23` | Unknown (no-prompt-mark) | none | not Prompt |
| `permissions-w32` | Unknown (no-prompt-mark) | none | not Prompt |
| `trust-w100` | Unknown (no-box-top) | none | not Prompt |
| `trust-w32` | Unknown (no-box-top) | none | not Prompt |

`form4-w23` is a different case from the other failing forms: at 23 columns by 49 rows the form is taller than the pane, so its tab bar, question and option 1 are not on the screen at all (the pane is on the alternate screen, and `capture-pane -S -80` returned nothing above the visible rows). No screen parser can read what was not drawn.

The `modelswitch-*` captures hold the user's own echoed `❯ hi` and `❯ /model` lines in scrollback above a real "Switch model?" dialog. They are the narrow-pane version of the case CB-307's addendum was written against, and a loosened box-top rule must keep reading them as a dialog, never a prompt.

## Paired with transcript rows (items 4 and 5)

The `ask-*` files come from a second scratch session (same Claude Code 2.1.325, same scratch tmux 3.7c server, `claude -n long-session-name` in `/private/tmp`), taken by Juno Abernathy for plan items 4 and 5. Each screen is byte-for-byte `capture-pane -p`; each `.jsonl` is that session's own transcript, captured while the form was still on screen and filtered to its `user`, `assistant` and `custom-title` rows (the request, attachment and account rows were dropped, as they carry system prompt text and account ids, not the form). Thinking rows were dropped too.

| file | what it is |
| --- | --- |
| `ask-venue-pending.jsonl` | the four-question offsite call, pending (the tool_use row is the last row) |
| `ask-venue-answered.jsonl` | the same, after Submit: the tool_result naming the call, then the reply |
| `ask-mixed-pending.jsonl` | the venue call answered, then a pending call with Colour (single), Suites (multiSelect) and Ship |
| `ask-venue-tall-w23h30` | the venue question in a 23x30 pane: options 1 and 2, the question and the tab bar scrolled away |
| `ask-catering-tall-w23h30` | the next question, same pane, after `2` was pressed |
| `ask-mixed-colour-w32` | Colour at 32 columns: the tab names are cut to `Colo…` and `S…` |
| `ask-mixed-suites-w32` | Suites at 32 columns, after `3` was pressed on Colour |
| `ask-mixed-suites-tall-w23h22` | Suites in a 23x22 pane, taller than it |
| `ask-freetext-pending.jsonl` | a third session's pending two-question call (Venue, Food), rows up to and not including its tool_result |
| `ask-freetext-on-row-w23h30` | its Venue question in a 23x30 pane after `5` was pressed with the cursor scrolled away: the cursor is on `5. Type something.` and the footer offers ctrl+g |
| `ask-freetext-typed-w23h30` | the same after `a quiet barn` was typed: the row reads `❯ 5. a quiet barn` |

Measured on the venue form in the 23x30 pane: with options 1 and 2 off screen and the cursor on option 1, pressing `2` answered Venue with option 2, and on Catering (1 and 2 off screen again) pressing `1` answered option 1. The review screen and the tool_result both said so. A digit selects its option whether or not the option is drawn.

Measured on the free-text row in the same 23x30 shape: pressing the row's digit with the cursor scrolled away put the cursor on the row and opened the field without answering (no tool_result was written), the typed text showed in the row, and Enter answered Venue with it (the review read `→ a quiet barn`). The screen before the digit was seen but not kept; the test that needs it rebuilds it from `ask-freetext-on-row-w23h30` by clearing the cursor mark and the ctrl+g footer, which is the whole of the difference that was seen.

