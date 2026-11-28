#!/usr/bin/env python3
"""The remote test runner: builds, test suites and coverage for the bug-fix
org on another machine, never on the mini (CB-494).

    python3 host/machines/remote.py run-once [--config ~/.config/orbweaver-bugfix/remote.json]

The mini is the always-on host for the OpenClaw gateway, the Discord bots,
Paperclip and the mobile relay. On 2026-11-27 the org's builds, UI suites and
coverage merges held its load near 20 for over an hour and the gateway stalled
5 to 16 seconds at a time. So that work goes to the MacBook Air, and this is
how a sandboxed agent, which cannot ssh, asks for it:

  remote_test  {id, sha, run, filter?}   check out `sha` on the target, run
               the one command `run` names, and hand back the exit code, the
               output and the result files.

Requests are JSON files in <company>/remote-spool/; each answer is a folder
<company>/remote-results/<id>/ holding result.json, output.txt,
output-tail.txt and files/. The folder is renamed into place when the job has
ended, so an agent that sees it sees all of it. remote-results/status.json is
rewritten as things change: whether the target answers, its load average, what
is running and what is queued.

Why a second runner and not three more verbs on runner.py: that runner answers
each request inside one tick, and a Mac install holds a tick for 5 to 20
minutes. A coverage run in the same tick would eat a PR's 45-minute machine
hold, and an install would block every test. Here a job is started detached on
the target and looked at again every few seconds, so nothing waits on anything
but the one job that is running.

What bounds it:

  * The agent never sends a command. `run` is one word from RUNS and this file
    builds the argv; `filter` is a class name and nothing else.
  * `sha` must be reachable from a branch of the canonical repository, checked
    in this runner's own mirror before the target does any work. A commit that
    exists only in a fork, or only on an agent's disk, is refused: its result
    would describe code no reviewer can see.
  * The target holds no credential. The mirror on the mini reads GitHub and
    pushes the commit to the target over ssh.
  * One job at a time on a target; the rest wait in arrival order.
  * There is no local execution path in this file. With no target answering, a
    request waits `unreachable_wait_s` and is then refused. The mini is refused
    as a target by name, by ssh host, and by the hostname the target reports.

Every rule that decides anything is a pure function near the top; the world
sits behind two seams, Mirror and Target, so tools/test-bugfix-remote.py
drives every rule with fakes and no suite touches a machine.
"""
import argparse
import io
import json
import os
import re
import shlex
import shutil
import socket
import subprocess
import sys
import tarfile
import time

if __package__ in (None, ""):
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from host.machines.base import SSH_KEEPALIVE, MachineError, default_sh  # noqa: E402
from host.machines.runner import ID_RE, SHA_RE, iso, read_spool, save_state  # noqa: E402

DEFAULT_DIR = os.path.expanduser("~/.config/orbweaver-bugfix")
DEFAULTS = {
    "repo_url": "https://github.com/Uplift-Foundation/Orbweaver.git",
    "base_branch": "develop",
    "log_path": os.path.join(DEFAULT_DIR, "remote.log"),
    "state_dir": os.path.join(DEFAULT_DIR, "remote"),
    # In order of preference. A second entry is a fallback used only while the
    # first does not answer; none is configured (CB-494 leaves the MacBook Pro
    # out: it is a person's working machine and sleeps with its lid closed).
    "targets": [{"name": "air", "ssh": "air"}],
    "queue_cap": 10,
    "unreachable_wait_s": 15 * 60,
    # How long one run-once lives, and how often it looks. launchd starts the
    # next one each minute, so a 17-second test is answered in about 20
    # seconds rather than at the next minute.
    # How soon a start is tried again after the target said it was still
    # running a job this runner did not know about.
    "busy_retry_s": 15,
    "tick_s": 55,
    "poll_s": 2,
    "tail_bytes": 200 * 1024,
    "max_output_bytes": 5 * 1024 * 1024,
    "max_result_bytes": 50 * 1024 * 1024,
    "keep_results_days": 7,
}
FILTER_RE = re.compile(r"[A-Za-z0-9_.]{1,200}")
TARGET_NAME_RE = re.compile(r"[a-z0-9-]{1,20}")
FIELDS = {"verb", "id", "sha", "run", "filter"}
REQUIRED = {"verb", "id", "sha", "run"}
SHORT, FULL, COVERAGE = 10 * 60, 30 * 60, 45 * 60

# run -> (argv, how a filter is spelled or None when none is allowed, the cap
# with a filter, the cap without). Release throughout, as CI builds: one
# configuration on the target means one set of binaries to keep warm.
RUNS = {
    # tests/Tests.sln, not the root: `dotnet build` in the root picks
    # Orbweaver.sln, which is the app alone, and a green answer would say
    # nothing about a test change. Tests.sln builds the app through its
    # project references plus UnitTests, IntegrationTests and UiTests.
    # UiScreenshots and the console suites are compiled by their own runs.
    "build": (["dotnet", "build", "tests/Tests.sln", "-c", "Release"], None, SHORT, SHORT),
    "unit": (["dotnet", "test", "tests/UnitTests", "-c", "Release", "--logger", "trx;LogFileName=unit-tests.trx",
              "--results-directory", "TestResults"], "vstest", SHORT, FULL),
    "integration": (["dotnet", "test", "tests/IntegrationTests", "-c", "Release", "--logger",
                     "trx;LogFileName=integration-tests.trx", "--results-directory", "TestResults"],
                    "vstest", SHORT, FULL),
    "ui": (["dotnet", "test", "tests/UiTests", "-c", "Release", "--", "--report-xunit",
            "--report-xunit-filename", "ui-tests.xunit.xml"], "mtp", SHORT, FULL),
    "screenshots": (["dotnet", "test", "tests/UiScreenshots", "-c", "Release", "--", "--report-xunit",
                     "--report-xunit-filename", "ui-screenshots.xunit.xml"], "mtp", SHORT, FULL),
    "arrangement": (["dotnet", "run", "--project", "tests/ArrangementTests", "-c", "Release"], None, SHORT, SHORT),
    "glyph": (["dotnet", "run", "--project", "tests/GlyphTests", "-c", "Release"], None, SHORT, SHORT),
    "transcript": (["dotnet", "run", "--project", "tests/TranscriptTests", "-c", "Release"], None, SHORT, SHORT),
    "coverage": (["./tools/coverage.sh", "--base", "origin/develop"], None, COVERAGE, COVERAGE),
}


# The result files a run hands back, under names this file chose (they are
# the names its own argv gave the reports). A screenshot run also hands back
# its PNGs, whose names the tests chose: those are kept only when they are a
# dull flat name, and are written under files/screenshots/.
RESULT_FILES = {
    "unit": ["unit-tests.trx"],
    "integration": ["integration-tests.trx"],
    "ui": ["ui-tests.xunit.xml"],
    "screenshots": ["ui-screenshots.xunit.xml"],
}
PNG_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,150}\.png")


# ---------------------------------------------------------------- pure rules

def parse_request(raw):
    """(request, None) for a valid request, else (partial, why): the shape
    runner.parse_request has, so runner.read_spool can serve both."""
    try:
        req = json.loads(raw)
    except (ValueError, TypeError):
        return None, "not JSON"
    if not isinstance(req, dict):
        return None, "not a JSON object"
    rid = req.get("id")
    if not (isinstance(rid, str) and ID_RE.fullmatch(rid)):
        return None, "id must match [a-z0-9-]{1,40}"
    partial = {"id": rid, "verb": req.get("verb") if req.get("verb") == "remote_test" else None}
    if req.get("verb") != "remote_test":
        return partial, f"unknown verb {req.get('verb')!r}"
    extra = sorted(set(req) - FIELDS)
    if extra:
        return partial, "unknown field(s): " + ", ".join(extra)
    missing = sorted(REQUIRED - set(req))
    if missing:
        return partial, "missing field(s): " + ", ".join(missing)
    if not (isinstance(req["sha"], str) and SHA_RE.fullmatch(req["sha"])):
        return partial, "sha must be a full 40-character lowercase commit"
    partial["sha"] = req["sha"]
    run = req["run"]
    if not (isinstance(run, str) and run in RUNS):
        return partial, f"run must be one of: {', '.join(RUNS)}"
    partial["run"] = run
    if "filter" in req:
        f = req["filter"]
        if RUNS[run][1] is None:
            return partial, f"run {run!r} takes no filter"
        # The pattern is the whole rule: a class name has none of the
        # characters a shell reads, and the argv is quoted besides.
        if not (isinstance(f, str) and FILTER_RE.fullmatch(f)):
            return partial, "filter must be a class name matching [A-Za-z0-9_.]{1,200}"
    return dict(req), None


def command_for(run, flt=None):
    """(argv, cap in seconds) for a parsed request. The only place a command
    is made; nothing the agent wrote reaches it but a validated class name."""
    argv, style, cap_filtered, cap_full = RUNS[run]
    argv = list(argv)
    if flt is None:
        return argv, cap_full
    if style == "vstest":
        argv += ["--filter", f"FullyQualifiedName~{flt}"]
    elif style == "mtp":
        argv += ["--filter-class", flt]
    else:
        raise ValueError(f"run {run!r} takes no filter")
    return argv, cap_filtered


def sha_problem(on_branch):
    """Why a sha may not run (None = go). `on_branch` is Mirror.on_branch's
    answer: True, False, or None when GitHub could not be read. None fails
    closed, like every GitHub answer runner.py reads."""
    if on_branch is None:
        return "could not read the repository from GitHub to check the sha; try again later"
    if not on_branch:
        return ("sha is not on any branch of the canonical repository; commit, push it to your "
                "branch, and ask again")
    return None


def local_names(hostname):
    """Every name this machine answers to that a config or a target could
    spell, lowercased."""
    names = {"localhost", "mini", "macmini"}
    h = (hostname or "").strip().lower().rstrip(".")
    if h:
        names.update({h, h.split(".")[0], h.split(".")[0] + ".local"})
    return names


def is_local(host, local_hostname, local_addrs=()):
    """True when `host` (a name or an address) is this machine. `local_addrs`
    are this machine's own interface addresses: an alias pointed at the mini's
    LAN address is the mini as surely as one named for it."""
    h = (host or "").strip().lower().rstrip(".")
    return (h in local_names(local_hostname) or h in {a.lower() for a in local_addrs}
            or h.startswith("127.") or h in ("::1", "0.0.0.0", "::"))


def targets_problem(targets, local_hostname, resolve=lambda host: host, local_addrs=(),
                    addrs_of=lambda host: ()):
    """Why a configured target list may not be used (None = go). The whole
    reason for this runner is that the work does not happen on the mini, so
    the mini is refused however the config spells it: by name, by ssh host,
    by what ssh says that host resolves to (`resolve`, `ssh -G`), and by the
    addresses that resolves to in turn (`addrs_of`) against this machine's
    own (`local_addrs`)."""
    if not (isinstance(targets, list) and targets):
        return "targets must be a non-empty list"
    seen = set()
    for t in targets:
        if not (isinstance(t, dict) and set(t) == {"name", "ssh"}
                and isinstance(t["name"], str) and isinstance(t["ssh"], str)):
            return "each target is {\"name\", \"ssh\"}"
        name, host = t["name"], t["ssh"]
        if not TARGET_NAME_RE.fullmatch(name) or name in seen:
            return f"target name {name!r} is not a unique [a-z0-9-]{{1,20}} name"
        seen.add(name)
        # One plain host alias: no user@, no options, nothing ssh would read
        # as an argument.
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", host):
            return f"target {name!r}: ssh must be one host alias"
        resolved = resolve(host)
        for spelled in (name, host, resolved, *addrs_of(resolved)):
            if is_local(spelled, local_hostname, local_addrs):
                return f"target {name!r} is this machine ({spelled}); the mini is never a target"
    return None


def pick_target(names, reachable):
    """The first configured target that answers, or None. Order is preference:
    a later entry is used only while every earlier one is silent."""
    for n in names:
        if reachable.get(n):
            return n
    return None


def wait_problem(queued_at, unreachable_since, now, cap_s):
    """Why a queued request stops waiting (None = keep waiting). The clock
    runs from whichever is later, the request arriving or the targets going
    silent, so time spent queued behind a running job is not counted."""
    if unreachable_since is None:
        return None
    if now - max(queued_at, unreachable_since) >= cap_s:
        return "no target reachable; not run on the mini"
    return None


def queue_problem(queue_len, cap):
    if queue_len >= cap:
        return f"the queue already holds {queue_len} requests; ask again when some have finished"
    return None


def may_start(running, queue):
    """True when the head of the queue may start now: one job at a time."""
    return running is None and bool(queue)


def timed_out(job, now):
    return now >= job["deadline"]


def checkout_problem(want, got, target_hostname, local_hostname):
    """Why a prepared checkout may not be used (None = go). `got` is `git
    rev-parse HEAD` in the target's worktree after the checkout, never the
    request echoed back: a worktree left dirty at another commit must not
    lend its result to this one."""
    if is_local(target_hostname, local_hostname):
        return f"the target reports hostname {target_hostname!r}, which is this machine; not run on the mini"
    if got != want:
        return f"the target's worktree is at {got or 'no commit'}, not {want}, after the checkout"
    return None


MERGED_RE = re.compile(r"^merged (\d+) report\(s\)\s*$", re.MULTILINE)


def coverage_summary(output):
    """(summary, merged) from a coverage run's output: everything from
    merge-coverage.py's `merged N report(s)` line on, and N. (None, None)
    when the line is absent, which means no figure was produced at all."""
    m = None
    for m in MERGED_RE.finditer(output or ""):
        pass  # the last one: nothing before it is the summary
    if m is None:
        return None, None
    return output[m.start():], int(m.group(1))


def job_ok(run, exit_code, was_timed_out, merged):
    """A coverage run that exits 0 without `merged 4` measured nothing (four
    suites, four reports: CLAUDE.md, Coverage), so it is not ok."""
    if was_timed_out or exit_code != 0:
        return False
    return merged == 4 if run == "coverage" else True


def safe_members(names_sizes, limit, allowed=(), png=False):
    """Which members of the target's result archive are kept, in order, until
    `limit` bytes: output.txt, files/<name> for a name in `allowed` (the
    runner's own fixed names for this run), and with `png` a flat, dull
    files/screenshots/<name>.png. Nothing else: the archive comes from a
    machine that just ran the code under test, so a member that climbs out of
    the folder, or that this run was never going to produce, is dropped and
    named, not followed. Returns (kept, dropped)."""
    kept, dropped, total = [], [], 0
    for name, size in names_sizes:
        ok = name == "output.txt" or (name.startswith("files/") and name[6:] in allowed) or (
            png and name.startswith("files/screenshots/") and PNG_RE.fullmatch(name[18:]) is not None)
        if not ok or total + size > limit:
            dropped.append(name)
            continue
        total += size
        kept.append(name)
    return kept, dropped


# Runs whose suites drive a real tmux (tests/IntegrationTests'
# TmuxPlacementTests; coverage.sh runs that suite too).
NEEDS_TMUX = ("integration", "coverage")


def tool_problem(run, probe):
    """Why a run may not start on the target just probed (None = go). On a
    target with no tmux, the integration suite is red on seven tests whatever
    the commit is. Run and returned red, that teaches whoever reads it to
    wave a red through; so it is refused, and nothing runs."""
    if run in NEEDS_TMUX and not probe.get("tmux"):
        return "the target has no tmux, which IntegrationTests needs; not run"
    return None


def busy_problem(prep):
    """Why a prepared start did not start because the target itself is busy
    (None = it is not). `prep["busy"]` is the id of a job the target found
    still running in its job folder: one left behind when a connection
    dropped, which this runner's own state knows nothing about. It must not
    share the one worktree with a new job."""
    if prep.get("busy"):
        return f"the target is still running job {prep['busy']}"
    return None


# ---------------------------------------------------------------- the runner

class Remote:
    """Decisions over a state dict that survives between ticks:

        {"queue": [{"req", "queued_at"}], "running": {...} | None,
         "unreachable_since": t | None}

    `step()` is one look at the world. Every answer has one shape: id, verb,
    sha, run, filter, ok, refused, queued_at, started_at, finished_at, target,
    target_hostname, checked_out_sha, exit_code, timed_out, files, error.
    `refused` is set only when nothing ran on any target.
    """

    def __init__(self, cfg, mirror, targets, state, clock, log, save, write, local_hostname):
        self.cfg, self.mirror, self.targets = cfg, mirror, targets
        self.state, self.clock, self.log, self.save, self.write = state, clock, log, save, write
        self.local_hostname = local_hostname
        self.state.setdefault("queue", [])
        self.state.setdefault("running", None)
        self.state.setdefault("unreachable_since", None)
        self.probes = {}

    # -- answers

    def result(self, req, ok, refused=None, **extra):
        r = {"id": req["id"], "verb": req.get("verb"), "sha": req.get("sha"), "run": req.get("run"),
             "filter": req.get("filter"), "ok": ok, "refused": refused, "queued_at": None,
             "started_at": None, "finished_at": iso(self.clock()), "target": None,
             "target_hostname": None, "checked_out_sha": None, "exit_code": None, "timed_out": False,
             "files": [], "error": None}
        r.update(extra)
        return r

    def refuse(self, req, why, **extra):
        self.log(f"refused id={req['id']} run={req.get('run')} sha={req.get('sha')}: {why}")
        self.write(req["id"], self.result(req, False, why, **extra), "", {})

    # -- requests in

    def accept(self, requests):
        """`requests` is runner.read_spool's (request, why) list. A request is
        refused here, before any target is asked to do anything, or queued."""
        for req, why in requests:
            job = self.state["running"]
            if any(i["req"]["id"] == req["id"] for i in self.state["queue"]) or (
                    job is not None and job["req"]["id"] == req["id"]):
                # The same rule as an id that already has an answer: the
                # first request stands and the repeat does nothing.
                self.log(f"dropped a repeat of id={req['id']}, which is already waiting or running")
                continue
            if why is None:
                why = queue_problem(len(self.state["queue"]), self.cfg["queue_cap"])
            if why is None:
                why = sha_problem(self.mirror.on_branch(req["sha"]))
            if why is not None:
                self.refuse(req, why)
                continue
            self.state["queue"].append({"req": req, "queued_at": self.clock()})
            self.log(f"queued id={req['id']} run={req['run']} sha={req['sha']} "
                     f"filter={req.get('filter')} behind={len(self.state['queue']) - 1 + bool(self.state['running'])}")
            self.save(self.state)

    # -- one look

    def step(self):
        job = self.state["running"]
        if job is not None:
            self.watch(job)
        if may_start(self.state["running"], self.state["queue"]) and \
                self.clock() >= self.state.get("retry_after", 0):
            self.start_next()
        return self.status()

    def probe_all(self):
        self.probes = {name: t.probe() for name, t in self.targets.items()}
        now = self.clock()
        if any(p["reachable"] for p in self.probes.values()):
            self.state["unreachable_since"] = None
        elif self.state["unreachable_since"] is None:
            self.state["unreachable_since"] = now
            self.log("no target answers; queued requests wait "
                     f"{self.cfg['unreachable_wait_s']} s and are then refused")

    def start_next(self):
        self.probe_all()
        now = self.clock()
        name = pick_target(list(self.targets), {n: p["reachable"] for n, p in self.probes.items()})
        if name is None:
            keep = []
            for item in self.state["queue"]:
                why = wait_problem(item["queued_at"], self.state["unreachable_since"], now,
                                   self.cfg["unreachable_wait_s"])
                if why is None:
                    keep.append(item)
                else:
                    self.refuse(item["req"], why, queued_at=iso(item["queued_at"]))
            self.state["queue"] = keep
            self.save(self.state)
            return
        item = self.state["queue"].pop(0)
        req = item["req"]
        why = tool_problem(req["run"], self.probes[name])
        if why is not None:
            self.refuse(req, why, queued_at=iso(item["queued_at"]))
            self.save(self.state)
            return
        argv, cap = command_for(req["run"], req.get("filter"))
        base = dict(queued_at=iso(item["queued_at"]), started_at=iso(now), target=name)
        try:
            base_sha = self.mirror.base_sha()
            self.targets[name].init()
            self.mirror.push(self.targets[name].push_url, req["sha"], req["id"])
            prep = self.targets[name].start(req["id"], req["sha"], argv, cap, RESULT_FILES.get(req["run"], []),
                                            clean_all=req["run"] == "coverage", png=req["run"] == "screenshots")
        except LocalTarget as e:
            self.log(f"NEEDS ATTENTION: not run id={req['id']} target={name}: {e}")
            self.write(req["id"], self.result(req, False, error=str(e), **base), "", {})
            self.save(self.state)
            return
        except MachineError as e:
            # A connection that dropped part way may have left the job
            # running on the target. Nothing can be said about it from here,
            # so this request is answered as failed, and the next start asks
            # the target itself and waits for whatever is there.
            self.log(f"start failed id={req['id']} target={name}: {e}")
            self.write(req["id"], self.result(
                req, False, error=f"could not start on {name}: {e}; if the job did start there, its "
                                  "result is lost and the next request waits for it to end", **base), "", {})
            self.save(self.state)
            return
        why = busy_problem(prep)
        if why is not None:
            # Not an answer: the request goes back to the head of the queue
            # and is tried again shortly. The target ends a left-behind job
            # itself once that job's own cap has passed, so this cannot wait
            # for ever.
            if self.state.get("target_busy") != prep["busy"]:
                self.log(f"waiting id={req['id']} target={name}: {why}")
            self.state["target_busy"] = prep["busy"]
            self.state["queue"].insert(0, item)
            self.state["retry_after"] = now + self.cfg["busy_retry_s"]
            self.save(self.state)
            return
        self.state["target_busy"] = None
        base.update(target_hostname=prep.get("hostname"), checked_out_sha=prep.get("sha"))
        why = checkout_problem(req["sha"], prep.get("sha"), prep.get("hostname"), self.local_hostname)
        if why is None and not prep.get("started"):
            why = "the target prepared the checkout but did not start the job"
        if why is not None:
            self.log(f"not run id={req['id']} target={name}: {why}")
            # The script does not start a job on a wrong commit or a
            # forbidden host, so there should be nothing to kill. Kill
            # anyway: an answer that says "not run" must be true even if
            # that script is one day wrong.
            self.targets[name].kill(req["id"])
            self.targets[name].cleanup(req["id"])
            self.write(req["id"], self.result(req, False, error=why, **base), "", {})
            self.save(self.state)
            return
        self.state["running"] = dict(base, req=req, deadline=now + cap, cap_s=cap, started=now,
                                     base_sha=base_sha, argv=argv)
        self.log(f"started id={req['id']} target={name} host={prep.get('hostname')} sha={prep.get('sha')} "
                 f"cap={cap}s argv={json.dumps(argv)}")
        self.save(self.state)

    def watch(self, job):
        t, rid = self.targets.get(job["target"]), job["req"]["id"]
        if t is None:
            return self.finish(job, None, False, error=f"target {job['target']} is no longer configured")
        state, code, load = t.poll(rid)
        if load is not None:
            self.probes[job["target"]] = dict(self.probes.get(job["target"]) or {}, reachable=True, load=load,
                                              hostname=job["target_hostname"])
        if state == "done":
            return self.finish(job, code, False)
        if state == "lost":
            return self.finish(job, None, False, error="the job is gone from the target (restarted?); "
                                                       "nothing can be said about its result")
        if timed_out(job, self.clock()):
            # Whether it is running or the target has gone silent: the cap is
            # the cap. The kill takes the job's whole process group.
            t.kill(rid)
            return self.finish(job, None, True, error=f"reached its {job['cap_s']} s cap and was killed"
                               + ("" if state == "running" else "; the target was not answering"))

    def finish(self, job, code, was_timed_out, error=None):
        req, t = job["req"], self.targets.get(job["target"])
        output, files, dropped = "", {}, []
        if t is not None:
            try:
                output, files, dropped = t.collect(req["id"], self.cfg["max_result_bytes"],
                                                   RESULT_FILES.get(req["run"], []), req["run"] == "screenshots")
            except MachineError as e:
                error = (error + "; " if error else "") + f"could not fetch the result files: {e}"
            t.cleanup(req["id"])
        extra = {}
        merged = None
        if req["run"] == "coverage":
            summary, merged = coverage_summary(output)
            if summary is not None:
                files["coverage-summary.txt"] = summary.encode("utf-8", "replace")
            extra = {"merged_reports": merged, "base_sha": job.get("base_sha")}
            if merged != 4 and error is None and not was_timed_out:
                error = ("no `merged N report(s)` line in the output" if merged is None else
                         f"merged {merged} report(s), not 4: the figure is not a measurement")
        if dropped:
            extra["files_dropped"] = dropped
        res = self.result(req, job_ok(req["run"], code, was_timed_out, merged), None,
                          queued_at=job["queued_at"], started_at=job["started_at"], target=job["target"],
                          target_hostname=job["target_hostname"], checked_out_sha=job["checked_out_sha"],
                          exit_code=code, timed_out=was_timed_out, files=sorted(files), error=error, **extra)
        self.log(f"finished id={req['id']} target={job['target']} exit={code} timed_out={was_timed_out} "
                 f"ok={res['ok']} files={len(files)}" + (f" error={error}" if error else ""))
        self.write(req["id"], res, output, files)
        self.state["running"] = None
        self.save(self.state)

    def status(self):
        job, now = self.state["running"], self.clock()
        return {
            "at": iso(now),
            "targets": {n: {"reachable": p["reachable"], "load": p.get("load"), "hostname": p.get("hostname"),
                            "tmux": p.get("tmux")}
                        for n, p in self.probes.items()},
            "unreachable_since": iso(self.state["unreachable_since"]) if self.state["unreachable_since"] else None,
            "target_busy_with": self.state.get("target_busy"),
            "running": None if job is None else {
                "id": job["req"]["id"], "run": job["req"]["run"], "filter": job["req"].get("filter"),
                "sha": job["req"]["sha"], "target": job["target"], "started_at": job["started_at"],
                "deadline": iso(job["deadline"])},
            "queued": [{"id": i["req"]["id"], "run": i["req"]["run"], "queued_at": iso(i["queued_at"])}
                       for i in self.state["queue"]],
        }


# ---------------------------------------------------------------- real seams

class LocalTarget(MachineError):
    """The target turned out to be this machine. Raised from a start that
    stopped before it touched anything."""


class Mirror:
    """This runner's own bare copy of the canonical repository, on the mini.
    It answers "is this commit on a branch there" and carries commits to a
    target, so the target never talks to GitHub and holds no credential.
    Only refs/heads is fetched: a pull request's refs/pull/N/head is a fork's
    code, and is not a branch of this repository."""

    def __init__(self, cfg, sh=default_sh):
        self.dir = os.path.join(cfg["state_dir"], "mirror.git")
        self.url, self.base, self.sh = cfg["repo_url"], cfg["base_branch"], sh

    def git(self, *args, timeout=300):
        return self.sh(["git", "--git-dir", self.dir, *args], timeout=timeout)

    def fetch(self):
        try:
            if not os.path.isdir(self.dir):
                if self.sh(["git", "init", "-q", "--bare", self.dir], timeout=60).returncode != 0:
                    return False
            return self.git("fetch", "-q", "--prune", "--no-tags", self.url,
                            "+refs/heads/*:refs/heads/*").returncode == 0
        except (subprocess.TimeoutExpired, OSError):
            return False

    def on_branch(self, sha):
        """True, False, or None when GitHub could not be read."""
        if not self.fetch():
            return None
        try:
            if self.git("cat-file", "-e", sha + "^{commit}", timeout=60).returncode != 0:
                return False
            r = self.git("for-each-ref", "--count=1", "--contains", sha, "refs/heads", timeout=120)
        except (subprocess.TimeoutExpired, OSError):
            return None
        return r.returncode == 0 and bool(r.stdout.strip())

    def base_sha(self):
        try:
            r = self.git("rev-parse", "--verify", "-q", f"refs/heads/{self.base}", timeout=60)
        except (subprocess.TimeoutExpired, OSError) as e:
            raise MachineError(f"could not read {self.base} in the mirror: {e}")
        if r.returncode != 0:
            raise MachineError(f"the mirror has no {self.base} branch")
        return r.stdout.strip()

    def push(self, push_url, sha, rid):
        """The commit, and the base branch as the target's origin/develop
        (what `coverage.sh --base origin/develop` reads)."""
        try:
            r = self.git("push", "-q", "--force", push_url, f"{sha}:refs/remote-runs/{rid}",
                         f"refs/heads/{self.base}:refs/remotes/origin/develop", timeout=600)
        except (subprocess.TimeoutExpired, OSError) as e:
            raise MachineError(f"pushing the commit to the target failed: {e}")
        if r.returncode != 0:
            raise MachineError("pushing the commit to the target failed: " + (r.stderr or "").strip()[-300:])


# What every script on the target starts with. $HOME/.dotnet is the SDK
# dotnet-install.sh puts there with no sudo (host/README.md, target setup).
# No build server outlives a job: MSBuild node reuse, the MSBuild server and
# the shared compiler are all off (MSBuild reads UseSharedCompilation from the
# environment as a property, so coverage.sh's own builds get it too), and
# run.sh ends with `dotnet build-server shutdown`. They would make a warm run
# faster, and they would pile up on a 16 GB machine nobody is watching.
PREAMBLE = """set -u
ROOT="$HOME/{root}"
J="$ROOT/jobs/{rid}"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false
"""

# One worktree, reused, so a build is incremental (17 s cold against 3 s warm
# for one UI class, measured). It is forced to the commit and swept of
# untracked files every time, and every TestResults folder under it is
# removed: a report left by an earlier run is how a coverage merge comes to
# say `merged 6`. A coverage run also removes everything ignored (bin, obj),
# because a stale binary is what makes a coverage figure fiction.
START = PREAMBLE + """SHA={sha}
# First, before anything is made, checked out or started: is this machine one
# the work must never run on? The runner checks the name it is told too, but
# only a check made here happens before the job exists.
H="$(hostname | tr '[:upper:]' '[:lower:]')"
echo "hostname=$H"
for N in {forbidden}; do
  if [ "$H" = "$N" ] || [ "${{H%%.*}}" = "$N" ]; then exit 94; fi
done
cd "$ROOT" || exit 90
# Ask this machine, not the mini, whether a job is still running here: one
# left behind by a dropped connection is in nobody's state file. A live one
# inside its own cap is left alone and reported; one past its cap is killed.
NOW="$(date +%s)"
for P in jobs/*/pid; do
  [ -f "$P" ] || continue
  D="$(dirname "$P")"
  [ -f "$D/exit" ] && continue
  OLD="$(cat "$P")"
  kill -0 "$OLD" 2>/dev/null || continue
  if [ "$NOW" -ge "$(cat "$D/deadline" 2>/dev/null || echo 0)" ]; then
    kill -TERM -- "-$OLD" 2>/dev/null; sleep 2; kill -KILL -- "-$OLD" 2>/dev/null
  else
    echo "busy=$(basename "$D")"; exit 0
  fi
done
# Sweep what earlier jobs left. A finished job's folder is kept for ten
# minutes: its result may not have been fetched yet (by this runner after a
# restart, or by a second runner pointed at the same target).
for D in jobs/*/; do
  [ -d "$D" ] || continue
  if [ -f "$D/exit" ] && [ -z "$(find "$D/exit" -mmin +10 2>/dev/null)" ]; then continue; fi
  rm -rf "$D"
done
rm -rf "$J" && mkdir -p "$J/files/screenshots" || exit 90
echo $((NOW + {cap})) > "$J/deadline"
if ! git -C work rev-parse --git-dir >/dev/null 2>&1; then
  rm -rf work; git --git-dir=repo.git worktree prune
  git --git-dir=repo.git worktree add -f --detach work "$SHA" >/dev/null 2>&1 || exit 91
fi
cd work || exit 91
git checkout -q -f --detach "$SHA" || exit 92
git clean -q -f -d {clean_x} || exit 92
find . -name TestResults -type d -prune -exec rm -rf {{}} + 2>/dev/null
GOT="$(git rev-parse HEAD)"
echo "sha=$GOT"
[ "$GOT" = "$SHA" ] || exit 0
cat > "$J/run.sh" <<'REMOTE_JOB_EOF'
echo $$ > "$J/pid"
cd "$ROOT/work"
{command} > "$J/output.txt" 2>&1
code=$?
for N in {files}; do
  F="$(find . -name "$N" -path '*TestResults*' -print -quit 2>/dev/null)"
  [ -n "$F" ] && cp "$F" "$J/files/$N"
done
{copy_png}
dotnet build-server shutdown >/dev/null 2>&1
echo $code > "$J/exit.tmp" && mv "$J/exit.tmp" "$J/exit"
# The job is over and its answer is on disk. Whatever it left running in its
# process group goes with it: Avalonia's build helper, for one, outlives the
# build by minutes (measured on the Air) and no build-server switch stops it.
trap '' TERM
kill -TERM -- "-$$" 2>/dev/null; sleep 1; kill -KILL -- "-$$" 2>/dev/null
REMOTE_JOB_EOF
export ROOT J
# Its own process group, so a kill at the cap takes the build, the test host
# and everything they started, and nothing else.
nohup perl -e 'setpgrp(0,0); exec @ARGV' bash "$J/run.sh" >/dev/null 2>&1 </dev/null &
# Not started until its pid is on disk, where the check above will find it.
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do [ -f "$J/pid" ] && break; sleep 0.25; done
[ -f "$J/pid" ] && echo "started=1"
"""

POLL = PREAMBLE + """sysctl -n vm.loadavg
if [ -f "$J/exit" ]; then echo "done $(cat "$J/exit")"
elif [ -f "$J/pid" ] && kill -0 "$(cat "$J/pid")" 2>/dev/null; then echo running
elif [ -d "$J" ] && [ ! -f "$J/pid" ]; then echo starting
else echo lost; fi
"""

KILL = PREAMBLE + """P="$(cat "$J/pid" 2>/dev/null)"
[ -n "$P" ] || exit 0
kill -TERM -- "-$P" 2>/dev/null; sleep 2; kill -KILL -- "-$P" 2>/dev/null
exit 0
"""

COLLECT = PREAMBLE + """cd "$J" || exit 90
if [ -f output.txt ]; then tail -c {max_output} output.txt > output.capped && mv output.capped output.txt; fi
rmdir files/screenshots 2>/dev/null
# Without this macOS tar adds a ._name member for every file carrying an
# extended attribute, which is all of them.
COPYFILE_DISABLE=1 tar -cf - output.txt files 2>/dev/null | head -c {max_stream}
"""

CLEANUP = PREAMBLE + """rm -rf "$J"
git --git-dir="$ROOT/repo.git" update-ref -d refs/remote-runs/{rid} 2>/dev/null
exit 0
"""

# tmux is looked for on the PATH a job gets, because that is where the
# integration tests will look for it.
PROBE = ('export PATH="$HOME/.dotnet:$PATH"\nhostname; sysctl -n vm.loadavg\n'
         "command -v tmux >/dev/null 2>&1 && echo tmux=yes || echo tmux=no\n")
INIT = PREAMBLE + 'mkdir -p "$ROOT/jobs" || exit 90\n[ -d "$ROOT/repo.git" ] || git init -q --bare "$ROOT/repo.git"\n'
ROOT_DIR = "orbweaver-remote"
# Only a `screenshots` run hands its PNGs back. Any run that happens to draw
# them (coverage runs UiScreenshots) would otherwise copy some 330 files that
# the collect then drops, one name each in files_dropped.
COPY_PNG = '[ -d TestResults/screenshots ] && cp TestResults/screenshots/*.png "$J/files/screenshots/" 2>/dev/null'
ARCHIVE_SLACK = 1024 * 1024


def parse_load(text):
    """[1, 5, 15]-minute load from `sysctl -n vm.loadavg` ("{ 1.96 2.00 1.98 }"), or None."""
    m = re.search(r"\{\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\}", text or "")
    return [float(x) for x in m.groups()] if m else None


class Target:
    """One machine the work runs on, over ssh. Scripts go to `bash -s` on
    stdin, so nothing is quoted through the login shell, and every value put
    into one is a validated id, a 40-hex sha or shlex-quoted argv."""

    def __init__(self, name, host, cfg, sh=default_sh, sh_bytes=None, forbidden=()):
        self.name, self.host, self.cfg, self.sh = name, host, cfg, sh
        # Hostnames the start script refuses to run on: this machine's.
        self.forbidden = [n for n in sorted(forbidden) if re.fullmatch(r"[a-z0-9._-]+", n)]
        # The result archive is bytes; everything else is text.
        self.sh_bytes = sh_bytes or (lambda argv, timeout=None, input=None: subprocess.run(
            argv, input=input, capture_output=True, timeout=timeout))
        self.push_url = f"{host}:{ROOT_DIR}/repo.git"

    def ssh(self, script, timeout=60, text=True):
        argv = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", *SSH_KEEPALIVE, self.host, "bash", "-s"]
        if text:
            return self.sh(argv, timeout=timeout, input=script)
        return self.sh_bytes(argv, timeout=timeout, input=script.encode())

    def probe(self):
        try:
            r = self.ssh(PROBE, timeout=20)
        except (subprocess.TimeoutExpired, OSError):
            return {"reachable": False, "load": None, "hostname": None, "tmux": None}
        if r.returncode != 0:
            return {"reachable": False, "load": None, "hostname": None, "tmux": None}
        lines = r.stdout.splitlines()
        return {"reachable": True, "load": parse_load(r.stdout), "hostname": lines[0].strip() if lines else None,
                "tmux": "tmux=yes" in lines}

    def init(self):
        try:
            r = self.ssh(INIT.format(root=ROOT_DIR, rid="none"))
        except (subprocess.TimeoutExpired, OSError) as e:
            raise MachineError(f"ssh to {self.name} failed: {e}")
        if r.returncode != 0:
            raise MachineError(f"could not prepare {ROOT_DIR} on {self.name}: exit {r.returncode}")

    def start(self, rid, sha, argv, cap_s, files=(), clean_all=False, png=False):
        script = START.format(root=ROOT_DIR, rid=rid, sha=sha, clean_x="-x" if clean_all else "",
                              copy_png=COPY_PNG if png else ":", cap=int(cap_s), files=" ".join(shlex.quote(f) for f in files) or "''",
                              forbidden=" ".join(shlex.quote(n) for n in self.forbidden) or "''",
                              command=" ".join(shlex.quote(a) for a in argv))
        try:
            r = self.ssh(script, timeout=300)
        except (subprocess.TimeoutExpired, OSError) as e:
            raise MachineError(f"ssh to {self.name} failed: {e}")
        if r.returncode == 94:
            host = dict(line.split("=", 1) for line in r.stdout.splitlines() if "=" in line).get("hostname")
            raise LocalTarget(f"the target reports hostname {host!r}, which is this machine; not run on the mini")
        if r.returncode != 0:
            raise MachineError({90: "could not make the job folder", 91: "could not make the worktree",
                                92: "could not check out the commit"}.get(r.returncode, "ssh failed")
                               + f" (exit {r.returncode})")
        out = dict(line.split("=", 1) for line in r.stdout.splitlines() if "=" in line)
        return {"sha": out.get("sha"), "hostname": out.get("hostname"), "started": out.get("started") == "1",
                "busy": out.get("busy")}

    def poll(self, rid):
        """(state, exit code, load): state is running, done, lost, or
        unreachable when ssh itself did not answer."""
        try:
            r = self.ssh(POLL.format(root=ROOT_DIR, rid=rid), timeout=30)
        except (subprocess.TimeoutExpired, OSError):
            return "unreachable", None, None
        lines = r.stdout.strip().splitlines()
        if r.returncode != 0 or not lines:
            return "unreachable", None, None
        load, last = parse_load(r.stdout), lines[-1].split()
        if last[0] == "done":
            return "done", int(last[1]) if len(last) > 1 and last[1].lstrip("-").isdigit() else None, load
        # The moment between the job folder existing and run.sh writing its
        # pid is not a lost job.
        return ("running" if last[0] in ("running", "starting") else "lost"), None, load

    def kill(self, rid):
        try:
            self.ssh(KILL.format(root=ROOT_DIR, rid=rid), timeout=30)
        except (subprocess.TimeoutExpired, OSError):
            pass

    def collect(self, rid, limit, allowed=(), png=False):
        """(output text, {file name: bytes}, dropped names)."""
        try:
            # The stream is cut on the target, one byte past what an honest
            # result can be (the limit, plus tar's own headers and padding),
            # so a runaway result folder never crosses the network or sits
            # in this process's memory.
            most = limit + ARCHIVE_SLACK
            r = self.ssh(COLLECT.format(root=ROOT_DIR, rid=rid, max_output=self.cfg["max_output_bytes"],
                                        max_stream=most + 1), timeout=300, text=False)
        except (subprocess.TimeoutExpired, OSError) as e:
            raise MachineError(f"ssh to {self.name} failed: {e}")
        if len(r.stdout) > most:
            raise MachineError(f"the result archive is larger than {most} bytes; nothing was taken from it")
        return unpack(r.stdout, limit, allowed, png)

    def cleanup(self, rid):
        try:
            self.ssh(CLEANUP.format(root=ROOT_DIR, rid=rid), timeout=30)
        except (subprocess.TimeoutExpired, OSError):
            pass


def unpack(raw, limit, allowed=(), png=False):
    """The target's tar stream as (output, files, dropped), through
    safe_members: nothing is extracted to disk by tarfile itself."""
    try:
        tf = tarfile.open(fileobj=io.BytesIO(raw), mode="r:")
        members = [m for m in tf.getmembers()]
    except (tarfile.TarError, EOFError) as e:
        raise MachineError(f"the result archive could not be read: {e}")
    plain = [(m.name, m.size) for m in members if m.isfile()]
    kept, dropped = safe_members(plain, limit, allowed, png)
    dropped += [m.name for m in members if not m.isfile()
                and m.name.rstrip("/") not in ("files", "files/screenshots")]
    output, files = "", {}
    for m in members:
        if m.isfile() and m.name in kept:
            data = tf.extractfile(m).read()
            if m.name == "output.txt":
                output = data.decode("utf-8", "replace")
            else:
                files[m.name[len("files/"):]] = data
    return output, files, dropped


def ssh_hostname(host, sh=default_sh):
    """What ssh would really connect to for `host` (`ssh -G`), lowercased;
    the alias itself when ssh cannot say."""
    try:
        r = sh(["ssh", "-G", host], timeout=20)
    except (subprocess.TimeoutExpired, OSError):
        return host
    for line in r.stdout.splitlines():
        if line.startswith("hostname "):
            return line.split(None, 1)[1].strip().lower()
    return host


def addresses_of(host):
    """The addresses a name resolves to, or () when it does not."""
    try:
        return tuple(sorted({info[4][0].split("%")[0].lower() for info in socket.getaddrinfo(host, None)}))
    except (OSError, UnicodeError):
        return ()


def local_addresses(hostname, sh=default_sh):
    """This machine's own addresses: what its hostname resolves to, and every
    address on an interface (`ifconfig -a`)."""
    found = set(addresses_of(hostname))
    try:
        out = sh(["/sbin/ifconfig", "-a"], timeout=20).stdout
    except (subprocess.TimeoutExpired, OSError):
        out = ""
    for m in re.finditer(r"^\s*inet6?\s+([0-9A-Fa-f:.]+)", out or "", re.MULTILINE):
        found.add(m.group(1).lower())
    return tuple(sorted(found))


# ---------------------------------------------------------------- files, main

def write_result(results_dir, rid, result, output, files, tail_bytes):
    """The answer folder, built under a temporary name and renamed into
    place. An id that already has a result is not overwritten."""
    final = os.path.join(results_dir, rid)
    if os.path.exists(final):
        return False
    tmp = os.path.join(results_dir, f".{rid}.tmp")
    shutil.rmtree(tmp, ignore_errors=True)
    os.makedirs(os.path.join(tmp, "files"))
    raw = (output or "").encode("utf-8", "replace")
    with open(os.path.join(tmp, "output.txt"), "wb") as f:
        f.write(raw)
    with open(os.path.join(tmp, "output-tail.txt"), "wb") as f:
        f.write(raw[-tail_bytes:].decode("utf-8", "ignore").encode("utf-8"))
    for name, data in files.items():
        # Names are the runner's own, or screenshots/<a flat PNG name>
        # (safe_members); anything else here is a bug, and is not written.
        sub, _, leaf = name.rpartition("/")
        if sub not in ("", "screenshots") or leaf in ("", ".", "..") or os.sep in leaf:
            continue
        os.makedirs(os.path.join(tmp, "files", sub), exist_ok=True)
        with open(os.path.join(tmp, "files", sub, leaf), "wb") as f:
            f.write(data)
    with open(os.path.join(tmp, "result.json"), "w") as f:
        json.dump(result, f, indent=2)
        f.write("\n")
    os.replace(tmp, final)
    return True


def write_status(results_dir, status):
    tmp = os.path.join(results_dir, ".status.json.tmp")
    with open(tmp, "w") as f:
        json.dump(status, f, indent=2)
        f.write("\n")
    os.replace(tmp, os.path.join(results_dir, "status.json"))


def prune(results_dir, spool_dir, now, days):
    """Drop answers and read requests older than `days`: a screenshot run's
    answer is megabytes, and nobody reads last week's."""
    cutoff = now - days * 86400
    for d, dirs in ((results_dir, True), (os.path.join(spool_dir, "done"), False),
                    (os.path.join(spool_dir, "bad"), False)):
        try:
            names = os.listdir(d)
        except OSError:
            continue
        for n in names:
            p = os.path.join(d, n)
            try:
                if os.path.getmtime(p) >= cutoff or n.startswith("status.json"):
                    continue
                if os.path.isdir(p) and dirs:
                    shutil.rmtree(p, ignore_errors=True)
                elif os.path.isfile(p) and not dirs:
                    os.remove(p)
            except OSError:
                continue


def load_config(path):
    with open(os.path.expanduser(path)) as f:
        cfg = json.load(f)
    merged = dict(DEFAULTS)
    merged.update(cfg)
    for k in ("log_path", "state_dir", "company_dir"):
        if k in merged:
            merged[k] = os.path.expanduser(merged[k])
    if "company_dir" not in merged:
        raise SystemExit("remote.json needs company_dir (the folder holding bugfix-spool)")
    return merged


def run_once(config_path, clock=time.time, sleep=time.sleep, mirror=None, targets=None,
             local_hostname=None, resolve=None, local_addrs=None, addrs_of=None):
    import fcntl

    cfg = load_config(config_path)
    os.makedirs(cfg["state_dir"], exist_ok=True)
    guard = open(os.path.join(cfg["state_dir"], "runner.lock"), "w")
    try:
        fcntl.flock(guard, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        guard.close()
        return 0
    os.makedirs(os.path.dirname(cfg["log_path"]), exist_ok=True)
    logf = open(cfg["log_path"], "a")

    def log(msg):
        logf.write(f"{iso(clock())} {msg}\n")
        logf.flush()

    spool = os.path.join(cfg["company_dir"], "remote-spool")
    results = os.path.join(cfg["company_dir"], "remote-results")
    for d in (spool, results):
        os.makedirs(d, mode=0o755, exist_ok=True)
    state_path = os.path.join(cfg["state_dir"], "state.json")
    try:
        with open(state_path) as f:
            state = json.load(f)
        if not isinstance(state, dict):
            raise ValueError("not an object")
    except FileNotFoundError:
        state = {}
    except (OSError, ValueError) as e:
        # Unlike runner.py's hold, nothing here is worth stopping for: a job
        # that was running is abandoned on the target and dies at its own
        # end, and its requester gets no answer, which the log says.
        aside = f"{state_path}.unreadable-{int(clock())}"
        os.replace(state_path, aside)
        log(f"state file unreadable ({e}); kept at {aside}; any running or queued request is forgotten")
        state = {}

    local_hostname = local_hostname or socket.gethostname()
    try:
        why = targets_problem(cfg["targets"], local_hostname, resolve or ssh_hostname,
                              local_addresses(local_hostname) if local_addrs is None else local_addrs,
                              addrs_of or addresses_of)
        if why is not None:
            # No target, so every request waits and is refused: never a
            # reason to run anything here.
            log(f"NEEDS ATTENTION: targets in remote.json refused: {why}")
            targets = {}
        elif targets is None:
            targets = {t["name"]: Target(t["name"], t["ssh"], cfg, forbidden=local_names(local_hostname))
                       for t in cfg["targets"]}

        def write(rid, result, output, files):
            if not write_result(results, rid, result, output, files, cfg["tail_bytes"]):
                log(f"id={rid} already has a result; this answer was not written: {json.dumps(result)}")

        remote = Remote(cfg, mirror or Mirror(cfg), targets, state, clock, log,
                        lambda s: save_state(state_path, s), write, local_hostname)
        prune(results, spool, clock(), cfg["keep_results_days"])
        end, last_status, idle_probe = clock() + cfg["tick_s"], None, True
        while True:
            remote.accept(read_spool(spool, results, log, parse=parse_request))
            if idle_probe and not state["queue"] and state["running"] is None:
                # Once a tick while idle, so status.json always carries the
                # target's load and whether it answers.
                remote.probe_all()
            idle_probe = False
            status = remote.step()
            comparable = dict(status, at=None)
            if comparable != last_status:
                write_status(results, status)
                last_status = comparable
            if clock() + cfg["poll_s"] >= end:
                break
            sleep(cfg["poll_s"])
    finally:
        save_state(state_path, state)
        logf.close()
        guard.close()
    return 0


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("command", choices=["run-once"])
    p.add_argument("--config", default=os.path.join(DEFAULT_DIR, "remote.json"))
    return run_once(p.parse_args(argv).config)


if __name__ == "__main__":
    sys.exit(main())
