# CB-412 captures: Claude Code's `/login`, screen by screen

Real `tmux capture-pane -p` output from Claude Code 2.1.330 on macOS, taken
2026-11-22 against a throwaway `CLAUDE_CONFIG_DIR` so no real login was
touched. No sign-in was completed.

## What was changed in them

- The opaque value after `https://claude.ai/oauth/authorize?` (45 characters)
  is replaced by a placeholder of the same length, character for character,
  so every wrap is exactly as captured.
- The working directory in the session header became `~/scratch/login-fixture`.
- `login-rows.jsonl`: ids replaced, paths replaced, nothing else.

The code typed into the field was a made-up string, never a real code.

## The screens

`login-*` is `/login` run inside a session. `onboarding-*` is the same flow on
a first run, which draws no `Login` header and words its footers differently.

| File | Screen |
| --- | --- |
| `login-picker-w100` | The method picker |
| `login-opening-browser-w100` | The moment after a method is picked, before the URL is drawn |
| `login-url-w100`, `-w60` | The sign-in URL and the empty code field. At 60 columns the URL wraps |
| `login-code-typed-w100` | A code typed into the field: masked except its last six characters |
| `login-badcode-error-w100` | A refused code: `OAuth error` and `Press Enter to retry` |
| `login-picker-after-retry-w100` | Enter on the error screen goes back to the picker |
| `login-interrupted-w100` | Esc: `Login interrupted`, and the prompt is back |
| `login-console-picker-w100` | Method 2 leads to a second picker |
| `login-thirdparty-picker-w100` | Method 3 leads to a platform picker |
| `onboarding-url-w40` | The URL wrapped at 40 columns |

## Measured alongside them

- The URL starts at column 0 and is hard-wrapped at the pane width with no
  indent, so a wrapped URL is rejoined by concatenating rows with nothing
  between them. It is 79 characters, so it wraps in any pane narrower than 80.
- Claude Code's own session record reads `status: waiting`,
  `waitingFor: dialog open` on the picker, the URL screen and the error
  screen, and `idle` again after Esc.
- The transcript gets `<local-command-stdout>Login interrupted</local-command-stdout>`
  on Esc. The row a successful login writes was **not** captured.

## Not captured

- A successful login.
- The same screens read through iTerm2's `contents` or a Windows console.
  These are tmux reads; another reader pads rows with trailing spaces.
