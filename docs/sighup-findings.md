# SIGHUP on macOS after a tree kill (CB-306)

Orbweaver on macOS was killed by SIGHUP twice in one day, each time right after a `claude agents --json` five-second timeout. This records what was measured and what was not. Confirmed means observed on a real machine; assumed means inferred.

## Mechanism

Orbweaver runs in launchd's session as the leader of its own process group. Its parent, launchd, is in a different group of the same session, so the group is not orphaned (its job-control count is 1). `Process.Kill(entireProcessTree: true)` on Unix SIGSTOPs each process, scans for children, then SIGKILLs it, parents before children. The grandchildren are reparented to launchd while still stopped. When one exits, XNU recounts the group, finds it orphaned with a stopped member, and sends SIGHUP+SIGCONT to every member, Orbweaver included. SIGHUP's default action terminates it.

## Confirmed

Harness: Roxanne Achterberg's `trials.c` (fork N times, each child in a fresh process group, count SIGHUP deaths) and the .NET `Program.cs`, run as a launchd job so the session id is 1. Her results, 28 of 30 reproduced and the controls 0 of 30, are in her scratchpad logs and are not repeated here.

The run for this fix, 4 Nov 2026, macOS on this MacBook, both as launchd jobs (`sid=1`, own group per trial), 30 trials each, each trial one `/bin/sh` that backgrounds twenty `sleep 30` and is killed 150 to 400 ms later:

| kill | trials | died of SIGHUP | survived |
| --- | --- | --- | --- |
| `Process.Kill(true)` (control) | 30 | 30 | 0 |
| `ProcessTreeKill.Kill` (this fix) | 30 | 0 | 30 |

All 30 fix trials logged a timeout and a kill, so the zero is not trials that never killed anything. No `sleep 30` was left running afterwards. The jobs were removed with `launchctl bootout` and their plists were never placed in `~/Library/LaunchAgents`.

The unit tests prove the kill order and the guard against duplicates and cycles. The integration test kills a real bash tree with a grandchild and sees every pid gone within a second.

## Assumed or not confirmed

- The real-CLI death was not captured. The link from `claude agents --json` to the death rests on two of two correlations plus the mechanism above.
- The one to four second gap between the logged kill and the death is unexplained. `BackgroundJobs.ReadOne`'s unlogged kill is the candidate.
- "Immune after the first crossing" is inferred from source, not measured.
- A child forked between the descendants snapshot and the kills survives as an orphan. It was never stopped, so it cannot trigger the SIGHUP.
- The session-1 SIGHUP itself cannot be reproduced in CI. The runners have no launchd session to run it in.
- `HangupGuard`'s handler is tested by constructing the signal context directly. It was not tested against a live kernel-delivered SIGHUP.
