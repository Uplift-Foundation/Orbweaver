# screen-read-probe (CB-313)

Reads a terminal's visible screen the way Orbweaver would, then prints what the shipped dialog parser makes of it. The parser sources are compiled in (the same list as `tests/TranscriptTests`), so the probe never answers with a parser of its own.

```
dotnet run --project tools/screen-read-probe -- <reader> <target> [--out <file>]
```

| reader | target | how it reads |
| --- | --- | --- |
| `tmux` | pane id (`%12`) | `tmux capture-pane -p`; `PROBE_TMUX_SOCKET` selects a private server |
| `terminal` | tty (`/dev/ttys015`) | Terminal.app, `osascript`, the sdef's `contents` property by its code `pcnt` |
| `iterm2` | session unique id | iTerm2, `osascript`, `contents` of the session |
| `wezterm` | pane id | `wezterm cli get-text`; `WEZTERM_UNIX_SOCKET` selects the instance |
| `kitty` | window id | `kitten @ get-text --extent screen`; `KITTY_LISTEN_ON` is the socket (default `unix:/tmp/kitty-cb313`) |
| `wincon` | pid of the CLI | `AttachConsole` + `ReadConsoleOutputCharacterW` over the console's visible window (Windows Terminal and conhost alike) |
| `file` | path | a capture already on disk |

`probe send <pid> <text> [--paste] [--enter]` types into a Windows console (the route `TerminalFocuser` uses) so the Windows CLIs can be driven.

Output is `lines=N`, `screen=<PaneScreen reading>/<check>`, then the same `title:` and `[k] label` lines `tests/TranscriptTests` prints.

`fixtures/` holds every capture, raw, UTF-8, LF; `manifest.json` says which terminal, version and reader took each and what the parser is expected to say. `tests/UnitTests/ScreenReadFixtureTests.cs` runs the parser over all of them. The findings and the table are in `docs/screen-read-probe-findings.md`.

The Windows reader needs the interactive session: over plain `ssh` the process is not on the desktop. The findings doc has the `schtasks /IT` recipe.

The local account name in the captures (macOS `warrenthompson`, Windows `warre`) is replaced with the same number of `x` characters, the same rule `tests/TranscriptTests/PaneFixtures.cs` states for its own fixtures, so no line moves and the parser answers identically.
