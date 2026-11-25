#!/usr/bin/env python3
"""The machine runner: real machines for the bug-fix org (CB-470).

    python3 host/machines/runner.py run-once [--config ~/.config/orbweaver-bugfix/machines.json]

The org's agents run inside the Paperclip sandbox. They cannot hold an SSH key,
install a build or touch the Windows PC, the mini or the MacBook, and for most
bugs that is right. A few bugs can only be proved on a real machine, and before
this the org skipped them. A launchd job runs `run-once` on an interval,
outside the sandbox, as Warren's user, and does three things for the org on
request, through files only:

  machine_install  {id, pr, sha, machines}      take the install lock, keep
                   aside what is installed, install the PR's build at `sha`,
                   confirm each machine's stamped commit;
  machine_run      {id, pr, machine, command, timeout_s}
                   run one command on a machine the PR holds;
  machine_release  {id, pr}                     put back exactly what was
                   there, confirm it, release the lock.

Requests are JSON files in <company>/machine-spool/; each answer is a folder
<company>/machine-results/<id>/ holding result.json, stdout.txt and stderr.txt.
The folder is written under a temporary name and renamed into place, so an
agent that sees it sees all three files.

Every rule that decides anything is a pure function near the top. The world
sits behind four seams: GitHub (`gh`, the host user's own login), the lock (a
directory, the same /tmp/ow-install.lock every team on these machines takes by
hand), the clock, and the machines themselves (host/machines/base.py). So
tools/test-bugfix-machines.py drives every rule with fakes and no suite touches
a machine.

Accepted with the decision (CB-470, lead's plan): a command the org writes runs
as Warren's user on all three machines, and while a PR holds them they run that
PR's build. The rules below bound that rather than remove it: only the org's
own PRs, only at their current head, one PR at a time, at most 45 minutes, and
every command logged.
"""
import argparse
import datetime as dt
import errno
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import time

if __package__ in (None, ""):
    # Run as a script by launchd: make `host.machines` importable from the
    # checkout this file sits in.
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

from host.machines.base import MachineError, stamp_matches  # noqa: E402

DEFAULT_DIR = os.path.expanduser("~/.config/orbweaver-bugfix")
DEFAULTS = {
    "repo": "Uplift-Foundation/Orbweaver",
    # `gh pr view --json author` names a GitHub App's PRs `app/<slug>`. The
    # REST API calls the same author `orbweaver-bugfix[bot]`; this runner only
    # ever asks gh, so only the first spelling is ever compared.
    "allowed_authors": ["app/orbweaver-bugfix"],
    "hold_cap_s": 45 * 60,
    "lock_path": "/tmp/ow-install.lock",
    "log_path": os.path.join(DEFAULT_DIR, "machines.log"),
    "state_dir": os.path.join(DEFAULT_DIR, "machines"),
    # How long an install waits for someone else's /tmp/ow-install.lock
    # before refusing (lead's ruling, CB-470).
    "lock_wait_s": 10 * 60,
    # Per stream, for machine_run's stdout.txt and stderr.txt.
    "max_output_bytes": 1024 * 1024,
}
MACHINES = ("windows", "mini", "macbook")
ID_RE = re.compile(r"[a-z0-9-]{1,40}")
SHA_RE = re.compile(r"[0-9a-f]{40}")
MAX_COMMAND = 4000
MAX_TIMEOUT = 600
# A request carrying a 4000-character command can be six times that once JSON
# has escaped it; anything past this is not a request this runner would accept.
MAX_REQUEST_BYTES = 64 * 1024
FIELDS = {
    "machine_install": {"verb", "id", "pr", "sha", "machines"},
    "machine_run": {"verb", "id", "pr", "machine", "command", "timeout_s"},
    "machine_release": {"verb", "id", "pr"},
}


# ---------------------------------------------------------------- pure rules

def _pos_int(v):
    return isinstance(v, int) and not isinstance(v, bool) and v > 0


def parse_request(raw):
    """(request, None) for a valid request, else (partial, why).

    The schema is closed: an unknown verb, an unknown field or a value outside
    its pattern is refused, never guessed at. `partial` carries the id when it
    was valid, so the refusal can still be answered where the agent will look
    for it; with no usable id there is nowhere to answer, and the file is only
    logged.
    """
    try:
        req = json.loads(raw)
    except (ValueError, TypeError):
        return None, "not JSON"
    if not isinstance(req, dict):
        return None, "not a JSON object"
    rid = req.get("id")
    partial = {"id": rid} if isinstance(rid, str) and ID_RE.fullmatch(rid) else None
    if partial is None:
        return None, "id must match [a-z0-9-]{1,40}"
    verb = req.get("verb")
    partial["verb"] = verb if verb in FIELDS else None
    if verb not in FIELDS:
        return partial, f"unknown verb {verb!r}"
    extra = sorted(set(req) - FIELDS[verb])
    if extra:
        return partial, "unknown field(s): " + ", ".join(extra)
    missing = sorted(FIELDS[verb] - set(req))
    if missing:
        return partial, "missing field(s): " + ", ".join(missing)
    if not _pos_int(req["pr"]):
        return partial, "pr must be a positive integer"
    partial["pr"] = req["pr"]
    if verb == "machine_install":
        if not (isinstance(req["sha"], str) and SHA_RE.fullmatch(req["sha"])):
            return partial, "sha must be a full 40-character lowercase commit"
        ms = req["machines"]
        if not (isinstance(ms, list) and ms and all(isinstance(m, str) for m in ms)):
            return partial, "machines must be a non-empty list of names"
        unknown = [m for m in ms if m not in MACHINES]
        if unknown:
            return partial, "unknown machine(s): " + ", ".join(map(repr, unknown))
        if len(set(ms)) != len(ms):
            return partial, "machines lists a machine twice"
    if verb == "machine_run":
        if req["machine"] not in MACHINES:
            return partial, f"unknown machine {req['machine']!r}"
        c = req["command"]
        if not (isinstance(c, str) and c.strip() and len(c) <= MAX_COMMAND):
            return partial, f"command must be a non-empty string of at most {MAX_COMMAND} characters"
        if not (_pos_int(req["timeout_s"]) and req["timeout_s"] <= MAX_TIMEOUT):
            return partial, f"timeout_s must be 1..{MAX_TIMEOUT}"
    return dict(req), None


def pr_problem(pr, allowed_authors, sha=None):
    """Why this PR may NOT use the machines (None = go). `pr` is the object
    `gh pr view --json state,author,headRefOid` returns, or None.

    `sha` is checked against the head when given: an install of anything but
    the current head would put a build on the machines that no reviewer will
    ever see, and its proof would describe code that is no longer the PR.
    """
    if pr is None:
        return "PR not found"
    if pr.get("state") != "OPEN":
        return "PR is not open"
    author = (pr.get("author") or {}).get("login")
    if author not in allowed_authors:
        return f"PR author {author!r} is not allowed to use the machines"
    if sha is not None and pr.get("headRefOid") != sha:
        return "sha is not the PR's current head"
    return None


def hold_expired(hold, now, cap_s):
    return hold is not None and now - hold["since"] >= cap_s


def run_problem(req, hold, pr):
    """Why a machine_run may NOT go ahead, given the hold and the PR now."""
    if hold is None or hold["pr"] != req["pr"]:
        return "PR does not hold the machines; send machine_install first"
    if req["machine"] not in hold["machines"]:
        return f"{req['machine']} is not one of the machines this PR installed on"
    if pr is not None and pr.get("headRefOid") != hold["sha"]:
        # Not a separate rule so much as rule 1 applied to the build actually
        # on the machine: after a push the installed build is no longer the PR.
        return "the PR's head moved since machine_install; install the new head first"
    return None


def install_problem(req, hold, configured):
    absent = [m for m in req["machines"] if m not in configured]
    if absent:
        return "machine(s) not available on this runner: " + ", ".join(absent)
    if hold is not None and hold["pr"] != req["pr"]:
        return f"machines are held by PR #{hold['pr']}; try again after it releases"
    return None


def iso(t):
    return dt.datetime.fromtimestamp(t, dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------- the runner

class Runner:
    """One tick's worth of decisions over a state dict that survives between
    ticks. `state["hold"]` is the one hold there can be:

        {"pr", "sha", "since", "machines": [...], "before": {machine: stamp},
         "lock_token"}

    `before` is written to disk before the first install starts, so a tick
    killed half way through an install still knows what to put back.

    Every answer has the lead's fixed shape (CB-470): id, verb, pr, ok,
    refused, started_at, finished_at, plus per-verb fields. `refused` is set
    only when nothing was touched; a failure after work began is ok=false with
    refused=null and the sentence in the per-machine `error` (or, for a run,
    in stderr.txt).
    """

    def __init__(self, cfg, gh, lock, machines, state, clock, log, save, sleep=time.sleep, attention=None):
        self.cfg, self.gh, self.lock, self.machines = cfg, gh, lock, machines
        self.state, self.clock, self.log_line, self.save, self.sleep = state, clock, log, save, sleep
        self.attention = attention if attention is not None else MemoryMarker()

    @property
    def hold(self):
        return self.state.get("hold")

    def log(self, msg, pr=None, machine=None):
        tags = []
        if pr is not None:
            tags.append(f"pr={pr}")
        if machine is not None:
            tags.append(f"machine={machine}")
        self.log_line(" ".join(tags + [msg]))

    def machine_log(self, pr, name):
        return lambda msg: self.log(msg, pr=pr, machine=name)

    # -- the tick

    def tick(self, requests):
        """`requests` is a list of (request, why) from the spool in arrival
        order. Returns a list of (id, result, stdout, stderr) to write."""
        self.retry_stranded_lock()
        self.expire()
        out = []
        for req, why in requests:
            self.started = self.clock()
            if why is not None:
                out.append(self.refuse(req, why))
                continue
            out.append(getattr(self, req["verb"])(req))
            # A request is answered against the state the one before it left.
            self.save(self.state)
        return out

    def answer(self, req, ok, refused=None, stdout="", stderr="", **extra):
        result = {"id": req["id"], "verb": req.get("verb"), "pr": req.get("pr"), "ok": ok,
                  "refused": refused, "started_at": iso(self.started), "finished_at": iso(self.clock())}
        result.update(extra)
        return req["id"], result, stdout, stderr

    def refuse(self, req, why, **extra):
        self.log(f"refused id={req['id']} verb={req.get('verb')}: {why}", pr=req.get("pr"),
                 machine=req.get("machine"))
        return self.answer(req, False, why, **extra)

    def expires_at(self):
        h = self.hold
        return iso(h["since"] + self.cfg["hold_cap_s"]) if h and h.get("since") is not None else None

    def expire(self):
        """A hold past its cap is restored and released by the runner itself:
        an agent that crashed, or forgot, must not leave its build on three
        machines and every other team locked out. This runs at the top of
        every tick, so it happens with no request needed."""
        h = self.hold
        if h is None:
            return
        if h.get("since") is None:
            # An install runs inside one tick and ticks never overlap, so a
            # hold with no start time at the top of a tick is one whose tick
            # died mid-install. Its machines are in an unknown state; put them
            # back now rather than let it sit for 45 minutes.
            self.log("found a hold whose install never finished; restoring", pr=h["pr"])
        elif hold_expired(h, self.clock(), self.cfg["hold_cap_s"]):
            self.log(f"hold reached its {self.cfg['hold_cap_s']} s cap; restoring", pr=h["pr"])
        else:
            return
        report = self.restore_all()
        self.release_lock()
        self.state["expired"] = {"pr": h["pr"], "at": self.clock(), "machines": report}
        self.save(self.state)

    def take_lock(self, pr_n):
        """The shared install lock, waiting up to lock_wait_s for whoever has
        it (lead's ruling): installs by people and other teams are minutes
        long, and refusing at once would make the org retry in a loop."""
        deadline = self.clock() + self.cfg["lock_wait_s"]
        while True:
            token = self.lock.acquire(f"orbweaver-bugfix machine runner, PR #{pr_n}")
            if token is not None or self.clock() >= deadline:
                return token
            self.sleep(min(15, max(0.0, deadline - self.clock())))

    # -- verbs

    def machine_install(self, req):
        pr_n = req["pr"]
        # The PR gate first: a PR that may not use the machines learns
        # nothing about who holds them (QA round 1).
        why = pr_problem(self.gh.pr(pr_n), self.cfg["allowed_authors"], sha=req["sha"])
        if why is None:
            marker = self.attention.read()
            if marker is not None:
                # Lead's ruling: after a restore nobody could confirm, no more
                # installs on machines in an unknown state until a person has
                # looked and deleted the marker.
                why = (f"the machines need a person first: {self.attention.path} says "
                       f"{marker.get('reason', 'see the file')}; installs are refused until it is deleted")
        if why is None:
            why = install_problem(req, self.hold, self.machines)
        if why is None and self.hold is None:
            token = self.take_lock(pr_n)
            if token is None:
                why = (f"the install lock is held by another session and was not free within "
                       f"{self.cfg['lock_wait_s']} s; try again later")
            else:
                self.state["hold"] = {"pr": pr_n, "sha": req["sha"], "since": None,
                                      "machines": [], "before": {}, "lock_token": token}
                self.state.pop("expired", None)
        if why is not None:
            return self.refuse(req, why)

        h = self.hold
        h["sha"] = req["sha"]
        report, failed = {}, False

        def entry(name, ok, installed, error=None):
            report[name] = {"ok": ok, "installed_commit": installed,
                            "previous_commit": h["before"].get(name), "error": error}

        for name in req["machines"]:
            if name not in h["machines"]:
                # The stamp before anything is touched. Recorded once per hold
                # (lead's ruling): a second install for a new head must not
                # record the first install's build as the one to put back.
                try:
                    h["before"][name] = self.machines[name].installed_commit()
                except MachineError as e:
                    entry(name, False, None, f"could not read the installed build: {e}")
                    failed = True
                    break
                h["machines"].append(name)
        self.save(self.state)

        for name in ([] if failed else req["machines"]):
            m, log = self.machines[name], self.machine_log(pr_n, name)
            log(f"install {req['sha']} (was {h['before'].get(name)})")
            try:
                m.install(pr_n, req["sha"], log)
                got = m.installed_commit()
            except MachineError as e:
                entry(name, False, None, f"install failed: {e}")
                failed = True
                break
            ok = stamp_matches(got, req["sha"])
            entry(name, ok, got, None if ok else f"the installed build reports {got} after installing {req['sha']}")
            log(f"installed stamp {got}: {'ok' if ok else 'does not match ' + req['sha']}")
            if not ok:
                failed = True
                break

        if failed:
            # Half an install is the worst state to leave three machines in:
            # put back every machine this hold changed and let go of the lock.
            self.log("install failed; restoring every machine the hold changed", pr=pr_n)
            restored = self.restore_all()
            self.release_lock()
            for name, r in restored.items():
                if name not in report:
                    report[name] = r
                else:
                    # Keep the install error; say what is on the machine now.
                    report[name]["installed_commit"] = r["installed_commit"]
                    if not r["ok"]:
                        report[name]["error"] += "; then " + r["error"]
            return self.answer(req, False, machines=report, hold_expires_at=None)
        if h["since"] is None:
            # The clock starts when the machines are ready, not when the
            # request arrived: a signed Mac build alone can take a third of
            # it. A re-install keeps the original start (lead's ruling).
            h["since"] = self.clock()
        self.save(self.state)
        return self.answer(req, True, machines=report, hold_expires_at=self.expires_at())

    def machine_run(self, req):
        name = req["machine"]
        run_fields = dict(machine=name, exit_code=None, timed_out=False,
                          stdout_truncated=False, stderr_truncated=False)
        pr = self.gh.pr(req["pr"])
        why = pr_problem(pr, self.cfg["allowed_authors"]) or run_problem(req, self.hold, pr)
        if why is None and name not in self.machines:
            why = f"{name} is not available on this runner"
        if why is not None:
            exp = self.state.get("expired")
            if exp and exp["pr"] == req["pr"] and self.hold is None:
                why += f" (this PR's hold reached its cap at {iso(exp['at'])} and was restored)"
            return self.refuse(req, why, **run_fields)
        try:
            r = self.machines[name].run(req["command"], req["timeout_s"])
        except MachineError as e:
            self.log(f"run id={req['id']} failed: {e} command={json.dumps(req['command'])}",
                     pr=req["pr"], machine=name)
            return self.answer(req, False, stderr=f"{e}\n", **run_fields)
        out, run_fields["stdout_truncated"] = bound(r.stdout, self.cfg["max_output_bytes"])
        err, run_fields["stderr_truncated"] = bound(r.stderr, self.cfg["max_output_bytes"])
        run_fields.update(exit_code=r.exit_code, timed_out=bool(r.timed_out))
        self.log(f"run id={req['id']} exit={r.exit_code} timed_out={bool(r.timed_out)} "
                 f"command={json.dumps(req['command'])}", pr=req["pr"], machine=name)
        return self.answer(req, not r.timed_out and r.exit_code == 0, stdout=out, stderr=err, **run_fields)

    def machine_release(self, req):
        # No PR check here on purpose: a PR that merged or closed while it held
        # the machines must still be able to give them back.
        h = self.hold
        if h is None or h["pr"] != req["pr"]:
            exp = self.state.get("expired")
            if exp and exp["pr"] == req["pr"] and h is None:
                report = exp.get("machines") or {}
                return self.answer(req, all(r["ok"] for r in report.values()), machines=report,
                                   hold_expires_at=None,
                                   stdout=f"the hold had already reached its cap at {iso(exp['at'])} "
                                          "and the runner restored the machines then\n")
            return self.refuse(req, "PR does not hold the machines", machines={}, hold_expires_at=None)
        report = self.restore_all()
        lock_ok = self.release_lock()
        err = "" if lock_ok else f"the install lock at {self.cfg['lock_path']} was not removed\n"
        return self.answer(req, lock_ok and all(r["ok"] for r in report.values()), machines=report,
                           hold_expires_at=None, stderr=err)

    # -- restore and lock

    def restore_all(self):
        """Put back every machine the hold touched and check each against the
        stamp recorded before the install. One retry each: a restore that fails
        twice needs a person, and holding the lock forever would only stop
        every other team too, so it is reported and logged, not retried."""
        h, report = self.hold, {}
        for name in h["machines"]:
            m, log, want = self.machines.get(name), self.machine_log(h["pr"], name), h["before"].get(name)
            entry = {"ok": False, "installed_commit": None, "previous_commit": want, "error": None}
            if m is None:
                entry["error"] = "machine no longer configured on this runner"
            for attempt in ((1, 2) if m is not None else ()):
                try:
                    m.restore(log)
                    got = m.installed_commit()
                except MachineError as e:
                    entry["error"] = f"restore failed: {e}"
                    log(f"restore attempt {attempt} failed: {e}")
                    continue
                entry["installed_commit"] = got
                # Nothing was installed before: there is no stamp to compare
                # with, so trust the module's restore and say so.
                ok = got == want if want is None else stamp_matches(got, want) or stamp_matches(want, got)
                entry["ok"] = ok
                entry["error"] = None if ok else f"restored build reports {got}, expected {want}"
                log(f"restore attempt {attempt}: stamp {got}, want {want}: {'ok' if ok else 'MISMATCH'}")
                if ok:
                    break
            if not entry["ok"]:
                self.log(f"NEEDS ATTENTION: {name} not restored to {want}", pr=h["pr"], machine=name)
                self.raise_attention(f"{name} was not restored after PR #{h['pr']}",
                                     machine=name, should_have=want, has=entry["installed_commit"],
                                     error=entry["error"])
                self.state.setdefault("unrestored", []).append(
                    {"machine": name, "want": want, "pr": h["pr"], "at": self.clock()})
            report[name] = entry
        return report

    def release_lock(self):
        """Release and check: True only when the lock directory is gone. A
        directory still there is logged as NEEDS ATTENTION; if it is still
        ours (rmdir failed: CB-427's shape), the token is kept in the state and
        the release retried every tick, so nothing forgets a stranded lock.
        The hold ends either way."""
        h = self.hold
        ok = self.lock.release(h["lock_token"])
        if ok:
            self.log("released the machines and the install lock", pr=h["pr"])
        else:
            self.log(f"NEEDS ATTENTION: install lock at {self.cfg['lock_path']} was not removed "
                     "(replaced by someone else, or rmdir failed)", pr=h["pr"])
            if self.lock.ours(h["lock_token"]):
                self.state["lock_stranded"] = h["lock_token"]
        self.state["hold"] = None
        self.save(self.state)
        return ok

    def retry_stranded_lock(self):
        token = self.state.get("lock_stranded")
        if token is None:
            return
        if self.lock.release(token) or not self.lock.ours(token):
            self.log("stranded install lock is gone now")
            self.state.pop("lock_stranded")
            self.save(self.state)
        else:
            self.log(f"NEEDS ATTENTION: still cannot remove our install lock at {self.cfg['lock_path']}")

    def raise_attention(self, reason, **detail):
        record = {"reason": reason, "at": iso(self.clock())}
        record.update(detail)
        self.attention.write(record)


class MemoryMarker:
    """The NEEDS-ATTENTION marker for tests and for a runner with nowhere to
    write one. FileMarker is the real one."""
    path = "NEEDS-ATTENTION.json"

    def __init__(self):
        self.record = None

    def read(self):
        return self.record

    def write(self, record):
        # The first problem is the one a person must see; a second does not
        # overwrite it.
        if self.record is None:
            self.record = record


class FileMarker:
    """machine-results/NEEDS-ATTENTION.json, where the org and a person both
    look. Present means installs are refused; a person deletes it after
    putting the machines right."""

    def __init__(self, path):
        self.path = path

    def read(self):
        try:
            with open(self.path) as f:
                rec = json.load(f)
            return rec if isinstance(rec, dict) else {"reason": "an unreadable marker"}
        except FileNotFoundError:
            return None
        except (OSError, ValueError):
            return {"reason": "an unreadable marker"}

    def write(self, record):
        if os.path.exists(self.path):
            return
        tmp = self.path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(record, f, indent=2)
            f.write("\n")
        os.replace(tmp, self.path)


def bound(text, limit):
    """Output capped at `limit` UTF-8 bytes, and whether it was cut. A command
    that prints a disk image must not fill the company folder."""
    text = text or ""
    raw = text.encode("utf-8", "replace")
    if len(raw) <= limit:
        return text, False
    return raw[:limit].decode("utf-8", "ignore"), True


# ---------------------------------------------------------------- real seams

class GhCli:
    def __init__(self, repo, sh=None):
        self.repo = repo
        self.sh = sh or (lambda argv: subprocess.run(argv, capture_output=True, text=True,
                                                     errors="replace", timeout=60))

    def pr(self, n):
        """The PR, or None for anything that is not a clean answer. Every
        rule reads None as a refusal, so a GitHub outage or a hung gh fails
        closed: nothing is installed or run on a PR the runner could not see.
        The head is asked afresh for every request, never cached."""
        try:
            r = self.sh(["gh", "pr", "view", str(int(n)), "--repo", self.repo,
                         "--json", "state,author,headRefOid"])
        except (subprocess.TimeoutExpired, OSError):
            return None
        if r.returncode != 0:
            return None
        try:
            return json.loads(r.stdout)
        except ValueError:
            return None


class DirLock:
    """The install lock every team on these machines takes by hand: a
    directory, made with mkdir because mkdir is atomic.

    An owner file inside says whose it is, and release removes the file and
    the directory together: CB-427 lost thirty minutes to a release that
    deleted the owner file, then failed `rmdir` on a non-empty directory and
    never retried. Release also refuses a lock whose owner file is not ours, so
    a hold that expired while a person took the lock by hand cannot delete
    theirs.
    """

    def __init__(self, path, clock=time.time):
        self.path, self.clock = path, clock

    def acquire(self, who):
        try:
            os.mkdir(self.path)
        except OSError as e:
            if e.errno == errno.EEXIST:
                return None
            raise
        token = f"{who} pid={os.getpid()} at={iso(self.clock())}"
        with open(os.path.join(self.path, "owner"), "w") as f:
            f.write(token + "\n")
        return token

    def ours(self, token):
        try:
            with open(os.path.join(self.path, "owner")) as f:
                return f.read().strip() == token
        except OSError:
            return False

    def release(self, token):
        """True when the lock directory is gone afterwards."""
        if not os.path.exists(self.path):
            return True
        try:
            with open(os.path.join(self.path, "owner")) as f:
                if f.read().strip() != token:
                    return False
        except OSError:
            return False
        shutil.rmtree(self.path, ignore_errors=True)
        return not os.path.exists(self.path)


def make_machines(cfg):
    """The configured machines. Imported lazily so a runner configured for
    Windows only never imports the Mac module, and the other way round."""
    out = {}
    for name in cfg.get("machines", {}):
        if name == "windows":
            from host.machines.windows import WindowsMachine
            out[name] = WindowsMachine(cfg)
        elif name in ("mini", "macbook"):
            from host.machines.mac import MacMachine
            out[name] = MacMachine(name, cfg)
    return out


# ---------------------------------------------------------------- spool, main

def read_spool(spool, results, log):
    """(request, why) pairs from spool/<id>.json in arrival order; every file
    is moved to done/ or bad/ so it is read once. Agents write <id>.json.tmp
    and rename, so only names ending .json are read.

    Three kinds of file are moved to bad/ and only logged, never answered:
    one with no usable id (there is no folder to answer in), one whose name is
    not its own id, and one whose id already has a result folder (lead's
    ruling: the first answer stands, untouched, and the repeat does nothing).
    """
    out = []
    for sub in ("done", "bad"):
        os.makedirs(os.path.join(spool, sub), exist_ok=True)
    paths = glob.glob(os.path.join(spool, "*.json"))
    paths.sort(key=lambda p: (os.path.getmtime(p), p))
    for path in paths:
        base = os.path.basename(path)
        try:
            with open(path, "rb") as f:
                raw = f.read(MAX_REQUEST_BYTES + 1)
        except OSError:
            continue
        if len(raw) > MAX_REQUEST_BYTES:
            req, why = None, "request is larger than 64 KiB"
        else:
            req, why = parse_request(raw.decode("utf-8", "replace"))
        if req is not None and base != req["id"] + ".json":
            req, why = None, f"file name is not <id>.json for id {req['id']!r}"
        if req is not None and os.path.exists(os.path.join(results, req["id"])):
            req, why = None, f"id {req['id']!r} already has a result; the repeat was not run"
        if req is None:
            log(f"dropped {base}: {why}")
        else:
            out.append((req, why))
        os.replace(path, os.path.join(spool, "done" if why is None else "bad", base))
    return out


def write_result(results_dir, rid, result, stdout, stderr):
    """Write the three files under a temporary name and rename the folder into
    place. An id that already has a result is not overwritten: the first
    answer stands, and the duplicate is reported to the caller."""
    final = os.path.join(results_dir, rid)
    if os.path.exists(final):
        return False
    tmp = os.path.join(results_dir, f".{rid}.tmp")
    shutil.rmtree(tmp, ignore_errors=True)
    os.makedirs(tmp)
    for name, text in (("stdout.txt", stdout), ("stderr.txt", stderr)):
        with open(os.path.join(tmp, name), "w") as f:
            f.write(text)
    with open(os.path.join(tmp, "result.json"), "w") as f:
        json.dump(result, f, indent=2)
        f.write("\n")
    os.replace(tmp, final)
    return True


def load_config(path):
    with open(os.path.expanduser(path)) as f:
        cfg = json.load(f)
    merged = dict(DEFAULTS)
    merged.update(cfg)
    for k in ("log_path", "state_dir", "lock_path", "company_dir"):
        if k in merged:
            merged[k] = os.path.expanduser(merged[k])
    if "company_dir" not in merged:
        raise SystemExit("machines.json needs company_dir (the folder holding bugfix-spool)")
    return merged


def save_state(path, state):
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump(state, f)
    os.replace(tmp, path)


def run_once(config_path, clock=time.time, gh=None, machines=None, lock=None, sleep=time.sleep):
    import fcntl

    cfg = load_config(config_path)
    os.makedirs(cfg["state_dir"], exist_ok=True)
    # launchd never overlaps two runs of one job, but a person running
    # run-once by hand beside it would: two runners on one state file could
    # both see no hold and both install.
    guard = open(os.path.join(cfg["state_dir"], "runner.lock"), "w")
    try:
        fcntl.flock(guard, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        guard.close()
        return 0
    state_path = os.path.join(cfg["state_dir"], "state.json")
    os.makedirs(os.path.dirname(cfg["log_path"]), exist_ok=True)
    logf = open(cfg["log_path"], "a")

    def log(msg):
        logf.write(f"{iso(clock())} {msg}\n")
        logf.flush()

    company = cfg["company_dir"]
    spool, results = os.path.join(company, "machine-spool"), os.path.join(company, "machine-results")
    # The runner makes both folders (lead's ruling), 755 like bugfix-spool:
    # the org writes requests, only the runner writes results.
    for d in (spool, results):
        os.makedirs(d, mode=0o755, exist_ok=True)
    attention = FileMarker(os.path.join(results, "NEEDS-ATTENTION.json"))
    state = {}
    try:
        with open(state_path) as f:
            state = json.load(f)
        if not isinstance(state, dict):
            raise ValueError("not an object")
    except FileNotFoundError:
        state = {}
    except (OSError, ValueError) as e:
        # A state file that cannot be read may have held a live hold: a build
        # on the machines and our lock in /tmp. Forgetting it silently would
        # strand both (QA round 1), so set it aside and stop installing until
        # a person has looked.
        aside = f"{state_path}.unreadable-{int(clock())}"
        os.replace(state_path, aside)
        log(f"NEEDS ATTENTION: state file unreadable ({e}); kept at {aside}")
        attention.write({"reason": "the runner's state file was unreadable and may have held a live hold",
                         "at": iso(clock()), "kept_at": aside})
        state = {}
    runner = Runner(cfg, gh or GhCli(cfg["repo"]), lock or DirLock(cfg["lock_path"], clock),
                    machines if machines is not None else make_machines(cfg),
                    state, clock, log, lambda s: save_state(state_path, s), sleep, attention)
    try:
        for rid, result, out, err in runner.tick(read_spool(spool, results, log)):
            if not write_result(results, rid, result, out, err):
                log(f"id={rid} already has a result; this answer was not written: {json.dumps(result)}")
    finally:
        save_state(state_path, runner.state)
        logf.close()
        guard.close()
    return 0


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("command", choices=["run-once"])
    p.add_argument("--config", default=os.path.join(DEFAULT_DIR, "machines.json"))
    return run_once(p.parse_args(argv).config)


if __name__ == "__main__":
    sys.exit(main())
