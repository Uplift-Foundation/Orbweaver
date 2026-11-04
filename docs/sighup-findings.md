# SIGHUP on macOS after a tree kill (CB-306)

Orbweaver on macOS was killed by SIGHUP twice in one day, each time right after a `claude agents --json` five-second timeout. This records what was measured and what was not. Confirmed means observed on a real machine; assumed means inferred.

## Mechanism

Orbweaver runs in launchd's session as the leader of its own process group. Its parent, launchd, is in a different group of the same session, so the group is not orphaned (its job-control count, jobc, is 1).

`Process.Kill(entireProcessTree: true)` on Unix SIGSTOPs each process, scans for its children, then SIGKILLs it, parents before children. The children are reparented to launchd when their parent is SIGKILLed, and they are not stopped at that point. Each child is then SIGSTOPped for its own child scan. If an orphaned sibling exits in that window, XNU's `fixjobc` drops the group's jobc from 1 to 0, `orphanpg` finds the stopped member, and SIGHUP+SIGCONT goes to every member of the group, Orbweaver included. SIGHUP's default action terminates it.

## Confirmed

All trials were on this MacBook, 4 Nov 2026, as launchd jobs (session 1), with a fresh process group per trial.

### Architect's trials (Roxanne Achterberg)

Each arm is 30 trials, bash with 30 short sleeps, killed by the .NET harness:

| Arm | Result |
| --- | --- |
| `Kill(true)` | 28/30 SIGHUP deaths |
| `Kill(false)` | 0/30 |
| `Kill(true)`, harness in its own session | 0/30 |
| `Kill(true)` plus a SIGHUP trap | 0/30 deaths, 29 SIGHUPs caught |
| Stop-free (SIGKILL, never SIGSTOP) | 0/30 deaths, 0/100 long sleeps leaked |
| `Kill(true)` on that same tree, repeated | 5/5 deaths |

- **Deterministic C test (`tools/sighup-repro/mech.c`):** a stopped member plus an orphan exit delivers SIGHUP (`si_pid` is the exiting orphan, `si_code` 0). With no stopped member, nothing is delivered. In its own session, nothing is delivered.
- **Timing:** `Kill(true)` median 418 ms, stop-free median 13 ms.
- **Real CLI:** 23 fresh launchd processes at about 3 kills each, plus one process at 53 kills, gave 0 deaths. The real-CLI death was never reproduced this way (see below).

### This fix (`ProcessTreeKill.cs` compiled into the same harness)

30 trials each, a `/bin/sh` with twenty `sleep 30` children killed 150 to 400 ms later:

| Kill | Trials | Died of SIGHUP | Survived |
| --- | --- | --- | --- |
| `Process.Kill(true)` (control) | 30 | 30 | 0 |
| `ProcessTreeKill.Kill` | 30 | 0 | 30 |

All 30 fix trials logged a timeout and a kill, so the zero is not trials that never killed anything. No `sleep 30` was left running afterwards. The jobs were removed with `launchctl bootout`.

The unit tests prove the kill order and the duplicate and cycle guards. The integration test kills a real bash tree with a grandchild and sees every pid gone within a second.

### Harness

`tools/sighup-repro/trials.c` (fork N children, each in a fresh process group or session, count SIGHUP deaths) and `mech.c` (the deterministic mechanism test) are kept. The .NET harness is not: it is the old `Program.cs` plus a `ProcessTreeKill.Kill` mode and is a few lines on top of the code in this change.

## Assumed or not confirmed

- The real-CLI death was not captured. The link from `claude agents --json` to the death rests on two of two correlations plus the mechanism.
- The one to four second gap between the logged kill and the death is unexplained. `BackgroundJobs.ReadOne`'s unlogged kill is the candidate.
- "Immune after the first crossing" is inferred from source, not measured.
- A child forked between the descendants snapshot and the kills survives as an orphan. It was never stopped, so it cannot trigger the SIGHUP.
- The session-1 SIGHUP itself is not attempted in CI; it would need `launchctl submit` on the runner, which this change deliberately does not do.
- `HangupGuard`'s handler is tested by constructing the signal context directly. It was not tested against a live kernel-delivered SIGHUP.
