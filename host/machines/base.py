"""The one seam every test machine sits behind (CB-470).

runner.py decides; a machine module only does. Each machine is a plain object,
built afresh on every tick (so anything that has to outlive a tick, such as the
copy of the build that was installed before, lives on disk), with:

    name                        "windows" | "mini" | "macbook"
    installed_commit()          the hex commit stamped in the installed build,
                                lowercased, full or abbreviated (>= 7), or None
    install(pr, sha, log)       keep the current build aside, then install the
                                build of `sha`; a retried install must not keep
                                aside the PR's own build over the original
    restore(log)                put the kept-aside build back and drop the
                                copy; a no-op when nothing is kept aside
    run(command, timeout_s)     -> RunResult

The runner, not the machine, checks the stamp before and after: a module that
verified itself would be marking its own homework, and the stamp is the one
fact both a restore and an install have to agree on.

`log` is a callable taking one string; the runner prefixes the time, machine
and PR and appends it to machines.log. Modules log every external command they
run (argv, never a secret) and its exit code.

Constructors take the whole parsed machines.json and an optional `sh=` seam
(callable(argv, timeout=None, input=None) -> subprocess.CompletedProcess) so
the suites never reach a real machine.
"""
import collections
import subprocess


class MachineError(Exception):
    """An install, restore or run that failed. The message is written into
    result.json for the org to read, so it must never carry a secret."""


RunResult = collections.namedtuple("RunResult", "exit_code stdout stderr timed_out")


def default_sh(argv, timeout=None, input=None):
    """subprocess.run with captured text output and no exception on a nonzero
    exit: every caller wants the exit code as data. A timeout still raises
    subprocess.TimeoutExpired, which callers turn into a MachineError or a
    timed-out RunResult."""
    return subprocess.run(argv, capture_output=True, text=True, timeout=timeout, input=input)


def stamp_matches(stamp, sha):
    """True when an installed build's stamp names `sha`. A stamp shorter than
    seven characters names nothing: one hex digit would match a sixteenth of
    all commits."""
    if not stamp or len(stamp) < 7 or not sha:
        return False
    return sha.lower().startswith(stamp.lower())
