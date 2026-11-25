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
    """

    def __init__(self, cfg, gh, lock, machines, state, clock, log, save):
        self.cfg, self.gh, self.lock, self.machines = cfg, gh, lock, machines
        self.state, self.clock, self.log_line, self.save = state, clock, log, save

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
        self.expire()
        out = []
        for req, why in requests:
            if why is not None:
                self.log(f"refused id={req['id']} verb={req.get('verb')}: {why}", pr=req.get("pr"))
                out.append(self.answer(req, "refused", why))
                continue
            out.append(getattr(self, req["verb"])(req))
            # A request is answered against the state the one before it left.
            self.save(self.state)
        return out

    def answer(self, req, status, reason=None, stdout="", stderr="", **extra):
        result = {"id": req["id"], "verb": req.get("verb"), "pr": req.get("pr"),
                  "status": status, "reason": reason, "finished_at": iso(self.clock())}
        if self.hold is not None and self.hold["pr"] == req.get("pr"):
            result["hold_expires_at"] = iso(self.hold["since"] + self.cfg["hold_cap_s"])
        result.update(extra)
        return req["id"], result, stdout, stderr

    def expire(self):
        """A hold past its cap is restored and released by the runner itself:
        an agent that crashed, or forgot, must not leave its build on three
        machines and every other team locked out."""
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
        self.state["expired"] = {"pr": h["pr"], "at": self.clock(), "restore": report}
        self.save(self.state)

    # -- verbs

    def machine_install(self, req):
        pr_n = req["pr"]
        why = install_problem(req, self.hold, self.machines)
        if why is None:
            why = pr_problem(self.gh.pr(pr_n), self.cfg["allowed_authors"], sha=req["sha"])
        if why is None and self.hold is None:
            token = self.lock.acquire(f"orbweaver-bugfix machine runner, PR #{pr_n}")
            if token is None:
                why = "the install lock is held by another session; try again later"
            else:
                self.state["hold"] = {"pr": pr_n, "sha": req["sha"], "since": None,
                                      "machines": [], "before": {}, "lock_token": token}
                self.state.pop("expired", None)
        if why is not None:
            self.log(f"refused id={req['id']} machine_install: {why}", pr=pr_n)
            return self.answer(req, "refused", why)

        h = self.hold
        h["sha"] = req["sha"]
        report, failed = {}, None
        for name in req["machines"]:
            if name not in h["machines"]:
                # The stamp before anything is touched. Recorded once per hold:
                # a second install for a new head must not record the first
                # install's build as the one to put back.
                try:
                    h["before"][name] = self.machines[name].installed_commit()
                except MachineError as e:
                    report[name] = {"status": "failed", "error": f"could not read the installed build: {e}"}
                    failed = f"could not read the installed build on {name}: {e}"
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
                report[name] = {"before": h["before"].get(name), "status": "failed", "error": str(e)}
                failed = f"install on {name} failed: {e}"
                break
            ok = stamp_matches(got, req["sha"])
            report[name] = {"before": h["before"].get(name), "installed": got,
                            "status": "ok" if ok else "failed"}
            log(f"installed stamp {got}: {'ok' if ok else 'does not match ' + req['sha']}")
            if not ok:
                failed = f"{name} reports {got!r} after installing {req['sha']}"
                break

        if failed:
            # Half an install is the worst state to leave three machines in:
            # put everything this hold touched back and let go of the lock.
            self.log(f"install failed, restoring: {failed}", pr=pr_n)
            restore = self.restore_all()
            self.release_lock()
            return self.answer(req, "failed", failed, machines=report, restore=restore)
        if h["since"] is None:
            # The clock starts when the machines are ready, not when the
            # request arrived: a signed Mac build alone can take a third of it.
            h["since"] = self.clock()
        self.save(self.state)
        return self.answer(req, "ok", machines=report)

    def machine_run(self, req):
        pr = self.gh.pr(req["pr"])
        why = pr_problem(pr, self.cfg["allowed_authors"]) or run_problem(req, self.hold, pr)
        if why is None and req["machine"] not in self.machines:
            why = f"{req['machine']} is not available on this runner"
        if why is not None:
            exp = self.state.get("expired")
            if exp and exp["pr"] == req["pr"] and self.hold is None:
                why += f" (this PR's hold reached its cap at {iso(exp['at'])} and was restored)"
            self.log(f"refused id={req['id']} machine_run on {req['machine']}: {why}", pr=req["pr"])
            return self.answer(req, "refused", why)
        name = req["machine"]
        try:
            r = self.machines[name].run(req["command"], req["timeout_s"])
        except MachineError as e:
            self.log(f"run id={req['id']} failed: {e} command={json.dumps(req['command'])}",
                     pr=req["pr"], machine=name)
            return self.answer(req, "failed", str(e), machine=name)
        self.log(f"run id={req['id']} exit={r.exit_code} timed_out={r.timed_out} "
                 f"command={json.dumps(req['command'])}", pr=req["pr"], machine=name)
        return self.answer(req, "ok", stdout=r.stdout or "", stderr=r.stderr or "", machine=name,
                           exit_code=r.exit_code, timed_out=bool(r.timed_out))

    def machine_release(self, req):
        # No PR check here on purpose: a PR that merged or closed while it held
        # the machines must still be able to give them back.
        h = self.hold
        if h is None or h["pr"] != req["pr"]:
            exp = self.state.get("expired")
            if exp and exp["pr"] == req["pr"] and h is None:
                return self.answer(req, "ok", "the hold had already reached its cap and been restored",
                                   restore=exp.get("restore"))
            why = "PR does not hold the machines"
            self.log(f"refused id={req['id']} machine_release: {why}", pr=req["pr"])
            return self.answer(req, "refused", why)
        report = self.restore_all()
        self.release_lock()
        bad = sorted(n for n, r in report.items() if r["status"] != "ok")
        if bad:
            return self.answer(req, "failed", "restore not confirmed on " + ", ".join(bad), restore=report)
        return self.answer(req, "ok", restore=report)

    # -- restore and lock

    def restore_all(self):
        """Put back every machine the hold touched and check each against the
        stamp recorded before the install. One retry each: a restore that fails
        twice needs a person, and holding the lock forever would only stop
        every other team too, so it is reported and logged, not retried."""
        h, report = self.hold, {}
        for name in h["machines"]:
            m, log, want = self.machines.get(name), self.machine_log(h["pr"], name), h["before"].get(name)
            if m is None:
                report[name] = {"status": "failed", "error": "machine no longer configured", "want": want}
                continue
            entry = {"want": want}
            for attempt in (1, 2):
                try:
                    m.restore(log)
                    got = m.installed_commit()
                except MachineError as e:
                    entry.update(status="failed", error=str(e))
                    log(f"restore attempt {attempt} failed: {e}")
                    continue
                entry["got"] = got
                # Nothing was installed before: there is no stamp to compare
                # with, so trust the module's restore and say so.
                ok = got == want if want is None else stamp_matches(got, want) or stamp_matches(want, got)
                entry["status"] = "ok" if ok else "failed"
                entry.pop("error", None)
                log(f"restore attempt {attempt}: stamp {got}, want {want}: {entry['status']}")
                if ok:
                    break
            if entry["status"] != "ok":
                self.log(f"NEEDS ATTENTION: {name} not restored to {want}", pr=h["pr"], machine=name)
                self.state.setdefault("unrestored", []).append(
                    {"machine": name, "want": want, "pr": h["pr"], "at": self.clock()})
            report[name] = entry
        return report

    def release_lock(self):
        h = self.hold
        if not self.lock.release(h["lock_token"]):
            self.log("install lock was not ours to release (gone or replaced)", pr=h["pr"])
        self.log("released the machines", pr=h["pr"])
        self.state["hold"] = None
        self.save(self.state)


# ---------------------------------------------------------------- real seams

class GhCli:
    def __init__(self, repo, sh=None):
        self.repo = repo
        self.sh = sh or (lambda argv: subprocess.run(argv, capture_output=True, text=True, timeout=60))

    def pr(self, n):
        r = self.sh(["gh", "pr", "view", str(int(n)), "--repo", self.repo,
                     "--json", "state,author,headRefOid"])
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

    def release(self, token):
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

def read_spool(spool, log):
    """(request, why) pairs from spool/*.json in arrival order; every file is
    moved to done/ or bad/ so it is read once. A file with no usable id is
    moved to bad/ and only logged: there is no result folder to answer it in."""
    out = []
    for sub in ("done", "bad"):
        os.makedirs(os.path.join(spool, sub), exist_ok=True)
    paths = glob.glob(os.path.join(spool, "*.json"))
    paths.sort(key=lambda p: (os.path.getmtime(p), p))
    for path in paths:
        try:
            with open(path, "rb") as f:
                raw = f.read(MAX_REQUEST_BYTES + 1)
        except OSError:
            continue
        if len(raw) > MAX_REQUEST_BYTES:
            req, why = None, "request is larger than 64 KiB"
        else:
            req, why = parse_request(raw.decode("utf-8", "replace"))
        dest = "done" if why is None else "bad"
        if req is None:
            log(f"dropped {os.path.basename(path)}: {why}")
        else:
            out.append((req, why))
        os.replace(path, os.path.join(spool, dest, os.path.basename(path)))
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


def run_once(config_path, clock=time.time, gh=None, machines=None, lock=None):
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
    try:
        with open(state_path) as f:
            state = json.load(f)
    except (OSError, ValueError):
        state = {}
    os.makedirs(os.path.dirname(cfg["log_path"]), exist_ok=True)
    logf = open(cfg["log_path"], "a")

    def log(msg):
        logf.write(f"{iso(clock())} {msg}\n")
        logf.flush()

    company = cfg["company_dir"]
    spool, results = os.path.join(company, "machine-spool"), os.path.join(company, "machine-results")
    os.makedirs(spool, exist_ok=True)
    os.makedirs(results, exist_ok=True)
    runner = Runner(cfg, gh or GhCli(cfg["repo"]), lock or DirLock(cfg["lock_path"], clock),
                    machines if machines is not None else make_machines(cfg),
                    state, clock, log, lambda s: save_state(state_path, s))
    try:
        for rid, result, out, err in runner.tick(read_spool(spool, log)):
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
