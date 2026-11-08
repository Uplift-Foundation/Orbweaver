# Screen reading on every terminal: findings (CB-313)

The question: for each supported terminal, can Orbweaver read a live permission prompt off the screen well enough for the existing dialog parser (`ChatTranscript.ParseDialog`, `PaneScreen.Read`) to turn it into buttons? Windows Terminal through ConPTY was the open one.

**Answer: yes for every terminal, for Claude Code and Codex. Windows Terminal through ConPTY included.** Grok's prompt is read off the screen fine and then not understood: Grok draws a shape the parser has no arm for (below), on all five Mac terminals. Grok's prompt is also not parsed on Windows Terminal or conhost (captured once Warren had signed Grok in there).

Everything below is from real captures taken 7 Nov 2026, saved in `tools/screen-read-probe/fixtures/`, and replayed by `tests/UnitTests/ScreenReadFixtureTests.cs`. Re-derive any cell by hand with `dotnet run --project tests/TranscriptTests -- tools/screen-read-probe/fixtures/<fixture>.txt`.

## What "yes" means here

All of these hold on the real capture: the parser returns a dialog; the option count equals the options drawn; keys are 1..n; the title is the question line (Claude Code) or the command line (Codex, accepted per `docs/codex-findings.md`); and `PaneScreen.Read` says `Dialog`. **`yes*`** means all of that, except that a long option label was wrapped by the terminal and the parser kept only its first physical line, so the button text is cut (the key is still right). Read strictly, `yes*` is a "no" on label completeness; it is a width effect, not a terminal one, and it appears wherever the terminal was 80 columns wide. iTerm2 and WezTerm were captured a second time at 120 columns (`*-120` fixtures, same recipe, a separate session each, with their own idle `none-120` captures): nothing wraps and every label is complete, so the parse is right when nothing wraps. Both widths are real captures and both are kept.

**Milestone-3 parser requirement: wrapped option labels have to be joined.** `ParseDialog` keeps only the first physical line of an option. A continuation line (indented deeper than the option's number, not itself numbered) must be appended to the label, or any user whose terminal is narrower than the longest label gets a truncated button.

## The table

Prompt captured per cell by asking the CLI to run a harmless `touch` (Windows: `New-Item`) outside its sandbox or approval rules, in a fresh scratch directory, default permission mode (`--permission-mode default` for Claude and Grok, `--ask-for-approval on-request` for Codex). Every prompt was then answered No and the file was checked not to exist.

| Terminal (reader) | Claude Code | Codex | Grok |
| --- | --- | --- | --- |
| tmux 3.7c (`capture-pane -p`) | **yes** `tmux-claude-prompt` | **yes** `tmux-codex-prompt` | **no** `tmux-grok-prompt` |
| Terminal.app 2.15 (`osascript`, `pcnt`) | **yes** `terminal-claude-prompt` | **yes** `terminal-codex-prompt` | **no** `terminal-grok-prompt` |
| iTerm2 3.7.7 (`osascript contents`) | **yes\*** at 80 columns `iterm2-claude-prompt`; **yes** at 120 `iterm2-claude-prompt-120` | **yes\*** at 80 `iterm2-codex-prompt`; **yes** at 120 `iterm2-codex-prompt-120` | **no** `iterm2-grok-prompt` |
| WezTerm 20240203 (`cli get-text`) | **yes\*** at 80 columns `wezterm-claude-prompt`; **yes** at 120 `wezterm-claude-prompt-120` | **yes\*** at 80 `wezterm-codex-prompt`; **yes** at 120 `wezterm-codex-prompt-120` | **no** `wezterm-grok-prompt` |
| kitty 0.49.2 (`kitten @ get-text`) | **yes** `kitty-claude-prompt` | **yes** `kitty-codex-prompt` | **no** `kitty-grok-prompt` |
| Windows Terminal 1.24 / ConPTY (`AttachConsole` + `ReadConsoleOutputCharacterW`) | **yes** `wt-claude-prompt` | **yes** `wt-codex-prompt` | **no** `wt-grok-prompt` (five-option "auto-review" variant, below) |
| conhost, the classic host (same reader) | **yes** `conhost-claude-prompt` | **yes** `conhost-codex-prompt` | **no** `conhost-grok-prompt` (same variant) |

Versions: Mac Claude Code 2.1.319, Codex 0.167.0, Grok 1.0.77 (it auto-updated from 1.0.70 during the session; every Grok capture is 1.0.77). Windows Claude Code 2.1.316, Grok 1.0.34, Codex 0.148.0 for the Windows Terminal captures and 0.167.0 for conhost (see "Unplanned change" below). The exact version of each capture is in `manifest.json`. Windows Grok 1.0.77 for every Grok capture that shows a prompt or an idle session (`wt-grok-*`, `conhost-grok-*` other than `signin`); the box's Grok updated itself from 1.0.34 to 1.0.77 at about 21:31, and the two `*-grok-signin` screens were captured around then without a version on screen, so their version is not known.

**Negative control, per cell.** Every cell has an idle capture (`<terminal>-<cli>-none`, plus `none-120` for the 120-column iTerm2 and WezTerm cells) of the same session before any prompt was raised: 25 in all, and none parses as a dialog (`Every_idle_capture_is_not_a_dialog` covers all 25, `none-120` included). The earlier `wt-grok-signin` and `conhost-grok-signin` screens (Grok not yet signed in) are kept as captures and also do not parse; they are not idle sessions. The adversarial control from the plan (an assistant message with a numbered 1..3 list and the input box drawn below it) is **not** captured; it is QA's.

## The ConPTY answer

Windows Terminal hosts the console in `OpenConsole.exe` (ConPTY); `tasklist` confirmed a new `OpenConsole` per `wt` window and a `conhost.exe` for the `conhost.exe cmd /k` windows. `AttachConsole(<claude pid>)` followed by `CONOUT$` and `ReadConsoleOutputCharacterW` over `srWindow` returns the TUI's text under both, with the box-drawing rules, `❯` and the option list intact. Under ConPTY the pseudo-console's buffer does hold what the TUI drew, and the parser reads it.

What it took, because none of it was obvious:

- The read has to run in the interactive session. Run over `ssh`, the process is not on the desktop; the probe ran as a one-off `schtasks /Create ... /SC ONCE /ST 23:59 /IT` task and `schtasks /Run`. `query user` showed the `warre` session as `Disc` (disconnected, 5 days idle) and `/IT` still worked and `wt` still created windows.
- The pid to attach to is the CLI's own console-attached process. For Grok on Windows that is the **parent** `grok.exe`; the child it spawns has its own hidden `conhost` and `AttachConsole` on it fails with error 87.
- Typing into those consoles used `WriteConsoleInput` with the bracketed-paste records the app uses (`TerminalFocuser.ConsoleRecords`); it worked in both hosts.

## Findings that are not in the table

1. **Grok's prompt shape is new and the parser reads none of it.** Identical on all five Mac terminals. On Windows (Windows Terminal and conhost) it is a five-option variant, headed "Auto-review blocked this action.", with `Always allow` and `Never allow` rows and `│` as the frame under conhost (`(•)` for the selected radio there, `(●)` in Windows Terminal); neither parses either:

   ```
    ┃  ›  Create the cb313-probe file in home
    ┃  │ touch $HOME/cb313-probe
    ┃
    ┃  1 (○) Yes, and don't ask again for anything (always-approve mode)
    ┃  2 (●) Yes, proceed
    ┃  3 (○) No, reject (type to add feedback)
    ┃
    1/3:select  │  Tab:next option  │  Ctrl+o:always-approve  │  Ctrl+c:cancel
   ```

   Options are `N (radio) label` with no `.` or `)`, the selected one is a filled `●`, the frame is `┃`, the title is the `›` line and the command the `│` line below it, and the hint line has no `Enter to` / `Esc to`. `ParseDialog` returns null and `PaneScreen` reads `Unknown/no-prompt-mark`. The spec plans one parser per CLI for v1, so this is the capture that parser needs (`docs/grok-findings.md` listed it as an open item). Pressing `3` answered No at once (no Enter needed).
2. **Grok's default mode is `always-approve`.** `grok` with no flags ran `touch $HOME/cb313-probe` with no prompt at all (the status bar says `always-approve`); that one file was removed. `--permission-mode default` is what raises the prompt above. A user running Grok defaults sees no dialog to read.
3. **80-column wrap truncates labels** (a milestone-3 parser requirement, above). iTerm2 (default 80x49) and WezTerm (default 80x24) wrapped Claude's long "Yes, and always allow access to /private/tmp/c3/... from this project" and Codex's "Yes, and don't ask again for commands that start with ..." option onto a second line and the parser kept only the first (`yes*`). Counts and keys were right. tmux with a long path did the same (`tmux-claude-prompt-wrapped`, a 120-column pane and a long scratch path), so this is width and path length, not terminal. Whether a truncated label matters depends on whether the panel shows the label; the key is what it sends.
4. **conhost draws the selection marker as `>`, not `❯`.** Claude Code's dialog parses fine (the parser accepts `>`), but the idle input line reads `> Try "how does <filepath> work?"`, so `PaneScreen.Read` says `Unknown/no-prompt-mark` where every other terminal says `Prompt` (`conhost-claude-none`). Dialog reading is unaffected; "is the session at an empty prompt" is not reliable under conhost.
5. **Terminal.app: `contents` by name returns the tab, not its text.** `contents of tab ...` in AppleScript is the language's own dereference and returns `tab 1 of window id N`; the sdef's property has to be addressed by its code, `«property pcnt»`, on the tab expression in place (a variable holding the tab still returns the tab). Terminal's `history` returns the whole scrollback and works by name. iTerm2's `contents` has no such clash.
6. **Terminal and iTerm2 pad rows with trailing spaces; tmux, WezTerm, kitty and the Windows reader do not.** The parser trims, so no effect.
7. **kitty and WezTerm need to be started for this.** kitty ignores `kitten @` unless launched with `allow_remote_control=yes` and `--listen-on`, and a kitty launched from a shell without the login PATH failed with `Failed to launch child` (launched through `zsh -lic` it worked). WezTerm started with `--always-new-process` gets its own socket and pane id 0 each time, so `WEZTERM_UNIX_SOCKET` has to be set per instance.
8. **First-run dialogs are real dialogs in their own right.** Captured and kept as fixtures (they are not part of the table): Claude Code's "Allow external CLAUDE.md file imports?" (unnumbered `❯` list, parses as null, `tmux-claude-importdialog`, `wt-claude-importdialog`), Grok's "Do you trust the contents of this directory?" (`tmux-grok-trustdialog`, null), Codex's "Do you trust the contents of this directory?" (`wt-codex-trustdialog`, parses as a 2-option dialog with the explanatory sentence as title), Codex's hooks review (`wt-codex-hooksdialog`, 3 options) and Codex's update prompt (`conhost-codex-updatedialog`, 3 options). A user launching one of these in a fresh directory sees a dialog the panel may or may not be meant to answer.
9. **Not observed:** wide-character (two-cell) padding from `ReadConsoleOutputCharacterW`. Nothing captured contained a wide glyph.

## Grok on Windows (captured after Warren signed in)

- **Default mode did not prompt for the first two commands.** With `--permission-mode default`, `New-Item C:/Users/xxxxx/cb313-probe -ItemType File` ran with no prompt and created the file (removed afterwards; `wt-grok-ranwithoutprompt`), and a read-only `Get-Content C:/Windows/win.ini` also ran unprompted. A `Remove-Item` of a path that does not exist (so the answer could be No or Yes with nothing to lose) did raise the dialog, several seconds in, after Grok's "auto-review" step. So there is no guarantee a Grok session ever shows a dialog for a given command; reading one is only half of the problem.
- **The prompt is five options, not three** (`wt-grok-prompt`, `conhost-grok-prompt`): `1 Yes, and don't ask again for anything (always-approve mode)`, `2 Always allow: <pattern>`, `3 Yes, proceed`, `4 No, reject`, `5 Never allow: <pattern>`. Answered with key `4`; nothing ran.
- **Bracketed paste is not honoured by Grok on Windows.** The text typed by `WriteConsoleInput` with the bracketed-paste markers appears in Grok's message literally as `[200~...[201~`. Codex and Claude Code did not show this. It did not stop the model acting on the request.
- Grok trusts the directory first (`*-grok-trustdialog`, an unnumbered Yes/No list that does not parse); `y` answered it.
- The Grok idle input line is `❯` in Windows Terminal and `>` in conhost, the same split as Claude Code.

## Setup notes

- Requests were typed through each terminal's own route; every prompt was answered No and the file checked absent.
- Grok was run with `--permission-mode default` (see finding 2). Windows Grok was signed in by Warren himself through the device-code flow; its auth was otherwise not touched.
- Account name in the captures is replaced with same-length `x` characters.

## Unplanned change on the Windows box

Codex 0.148.0 showed an "Update available" dialog in the conhost session, and I pressed Enter on it without reading it first; the default option was "Update now", which ran OpenAI's install script. The Windows box's Codex went from 0.148.0 to **0.167.0**. It is the same version as the Mac now. The Windows Terminal Codex captures were taken before this, at 0.148.0; the conhost ones after, at 0.167.0. Which captures were at which version: `wt-claude-*`, `wt-codex-*` and `wt-grok-*` were taken at Claude Code 2.1.316 / Codex 0.148.0 / Grok 1.0.34; `conhost-codex-none` and `conhost-codex-prompt` at Codex 0.167.0; `conhost-codex-updatedialog` is the 0.148.0 session that showed the update prompt. Nothing else on the box was changed: the probe and its fixtures live in `C:\Users\warre\cb313\`.

## What was not verified

- The adversarial numbered-list-above-the-input-box control (QA).
- Wide-character padding, narrow (80-column) Windows consoles, and Codex 0.167.0 under Windows Terminal (only conhost was captured after the update).
- Typing routes: requests were typed through each terminal's own scripting route (`send-keys`, `do script`, `write text`, `wezterm cli send-text`, `kitten @ send-text`, `WriteConsoleInput`). The point of the spike was reading, and the typing was only to raise prompts; whether those routes also work for answering was not the question.
- The probe's Windows reader is not covered by the unit suite (it needs a Windows console; `AttachConsole` has no seam). It ran on the Windows box through the `schtasks` recipe above.
- Whether the app's own `PaneScreen`/`ParseDialog` run on a Windows-captured screen inside the real app: the same code ran inside the probe (self-contained win-x64 build) on the Windows box and gave the same answers as the Mac replay.
