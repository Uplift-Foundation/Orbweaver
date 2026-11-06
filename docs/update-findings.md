# macOS auto-updater: what was measured (CB-308)

This is the record behind the macOS half of the updater. Confirmed means observed on a real machine; assumed means reasoned or read from a script and not run. The Windows half is tracked separately.

## The design, in one paragraph

The running app downloads and verifies the `.dmg`, mounts it read-only, checks the bundle inside (`codesign --verify --deep --strict`, Team ID `5AQ4ULRG3Z`, `spctl --assess --type execute`, bundle id, version), copies it to `~/Library/Application Support/Orbweaver/updates/<version>/`, writes a `handoff.json`, and starts the staged **new** bundle with `open -n <staged.app> --args --finish-update <handoff.json>`. That finisher stops the old app, swaps the bundle by rename, reconciles the keep-alive LaunchAgent from the new bundle's own script, makes sure the new app is running, and writes a `finished` or `failed` marker. It is the new bundle rather than a bash helper because launchd kills whatever is left in an app's process group when the app exits, so a detached helper child would die with the app it was replacing. The keep-alive plist also gains `AbandonProcessGroup=true` for the same reason.

## Two cross-version rules

`handoff.json` is written by version N and read by version N+1's finisher, so it carries a `Schema` integer (currently 1). A finisher that sees any other value, including a missing one, stops before it unloads, signals or moves anything and writes a `failed` marker the old app reads. The installer also refuses, before downloading, any release older than `MacFirstFinisherVersion` (1.0.4, the first release after 1.0.3 and so the first with a finisher), because earlier builds ignore `--finish-update`, start as a second instance and exit on the single-instance mutex. The 60 second "helper did not start, nothing was changed" path remains as the backstop. Both are covered by tests; the v1.0.3 refusal itself has not been run against a real v1.0.3 download.

## Confirmed on the Mac mini (macOS 27.0.1, 2026-11-06)

The mini runs the real Orbweaver (pid 20079, `/Applications/Orbweaver.app`) as a LaunchServices job, with the console session locked (`CGSSessionScreenIsLocked true`, read with `ioreg`; nothing was locked or woken for this).

Two throwaway unsigned bundles, `B.app` with **the same bundle id as the running app** (`io.github.wtvamp.claudebuddy`) and `C.app` with a unique id, each containing a `/bin/bash` script that records its own `$0`, arguments, pid, pgid and ppid, then sleeps four seconds. Each was started with exactly the command the app will use, `/usr/bin/open -n <bundle> --args --finish-update <json>`, from an ssh shell.

| | B (duplicate bundle id) | C (control, unique id) |
| --- | --- | --- |
| `open` exit | 0 | 0 |
| executable that ran | `~/orb-probe-cb308/B.app/Contents/MacOS/probe`, the path given | the path given |
| arguments received | `--finish-update <json path>`, intact | the same |
| pid / pgid | 35130 / 35130 | 35218 / 35218 |
| ppid | 1 (launchd) | 1 |
| the real app's pgid, same moment | 20079 | 20079 |

So `open -n` honours the path it is given even when another process of the same bundle id is running, and the launched process is in its own process group, parented to launchd, with a pgid different from the real app's. Both rows matter: the control shows the duplicate bundle id changed nothing. The decision that follows is that `launchctl submit` is **not** needed and the plan's `open -n` stands.

The probes were unregistered from LaunchServices afterwards (`lsregister -u`), the folder deleted, and the real app was still pid 20079 at the end.

## Confirmed on the MacBook: a cross-volume swap (2026-11-06)

Run by hand, not in a suite. A 40 MB APFS disk image was attached at a scratch mountpoint (device 16777239) and the staged bundle put on it; a scratch "installed" bundle sat on the boot data volume (device 16777234). The real `FinishAsync` ran with its real `Directory.Move` and a real `ditto`; only the process-table, codesign and launch calls were stubbed, and the logged exception was captured by wrapping the move.

- **Directory.Move across volumes throws.** `IOException: Cross-device link` on staged -> installed. It does not silently copy.
- **The fallback lands.** `ditto` to `Orbweaver.app.new` beside the target, then a same-volume rename: the installed executable read `NEW`, the old bundle went to the trash folder, the finished marker was written.
- **A forced failure restores.** With `ditto` forced to exit 1 the finisher moved the old bundle back (`OLD` in place), wrote a `failed` marker reading "The update did not complete (copy into place failed (ditto exited 1)). The previous version was restored.", and left the staged copy untouched.

The first run also found a gap, now fixed and covered: after the copy fallback the staged bundle was left behind, a second full copy of the app, so a successful finish now deletes it. The volume was detached and the scratch folder deleted. In normal use the updates folder and /Applications are usually on the same volume, so this path is the exception; it is now measured rather than assumed.

## Not confirmed

- **The real signed finisher.** The probes were unsigned shell scripts. Whether LaunchServices treats a *signed, hardened-runtime* staged bundle the same, and whether the staged bundle's own start-up (it must reach `Startup.Run`'s `finishUpdate` step before any window or mutex) behaves under a locked console, is for QA's end-to-end run with a real build.
- **Whether a plain `Process.Start` child survives the app's exit under launchd.** The plan wanted this measured once. It was not: the finisher's ppid=1 and own pgid already show it is not in the app's group, which is what the design relies on, and `AbandonProcessGroup` covers the keep-alive case. The question as literally put (a `Process.Start` child of Orbweaver itself) was not run.
- **Moving the old bundle to `~/.Trash` from the finisher.** A failure is logged and the old bundle left beside the new one; success is assumed.
- **TCC grants.** A replaced bundle gets a new CDHash, so Local Network, Automation, Accessibility and Microphone grants reset. That is the premise of the post-update notice and is known from the install notes in CLAUDE.md; this branch did not re-measure it.

## What the tests do and do not prove

`UpdateInstallPlanMacOSTests` asserts every argv literally and runs the parsers over real `codesign -dv` shapes. `UpdateInstallMacOSTests` runs stage, launch, finish and the installer against scratch folders with every OS tool replaced by a recording stand-in, including a refused stage leaving an installed bundle byte-identical. None of it runs a real `hdiutil`, `codesign` against a real bundle, `spctl`, `open` or `launchctl`; the real-machine rows above and QA's end-to-end run are what cover that.
