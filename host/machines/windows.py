"""The Windows PC behind the machine seam (CB-470; seam in base.py).

The build is the `unsigned-win-x64` artifact CI already uploads for every PR
head: the Inno Setup installer, unsigned, the same thing a reviewer would
download. Everything on the PC itself is done by windows.ps1, copied over and
run with `powershell -File`, because the ssh shell there is Git Bash and it
rewrites `/VERYSILENT` and every other Inno switch into a path.

Keeping aside is a copy of the install folder, and restoring is a mirror of
that copy back over it, checked by the runner against ProductVersion of
Orbweaver.exe (which carries the commit, "1.0.13+<40 hex>"). The old installer
is not kept because nothing on the PC has it: a build Warren installed by hand
weeks ago exists only as the folder it left behind. A mirror puts back every
file; what it does not put back is the registry's DisplayVersion and any task
the newer installer added, neither of which changes what runs.

`run` hands the command to the PC's ssh shell as one string, exactly as a
person typing `ssh windows '<command>'` would, so an agent writes Git Bash, and
reaches PowerShell with `powershell -Command ...` when it needs it. A command
that times out is cut off at our end; whatever it started on the PC may
outlive the ssh session, which the timeout cannot prevent.
"""
import json
import os
import re
import secrets
import shutil
import subprocess
import time

from host.machines.base import SSH_KEEPALIVE, MachineError, RunResult, default_sh

REMOTE_DIR = "orbweaver-machines"
SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "windows.ps1")
ARTIFACT = "unsigned-win-x64"
STAMP_RE = re.compile(r"^\d+\.\d+\.\d+[^+\s]*\+([0-9a-fA-F]{7,40})\s*$", re.M)
# CB-475: how long one remote action may hold the ssh call. QA measured a real
# install at 10-25 s and a restore at about 5 s; ssh to this PC can then stay
# open for many minutes after the script has finished (one install in two on
# 2026-11-25) while the runner holds /tmp/ow-install.lock. _ps therefore reads
# the script's own outcome record every DONE_POLL s while ssh is open and
# returns as soon as it appears; these caps only bound an action that never
# records one, so a stuck install costs at most INSTALL_TIMEOUT + DONE_GRACE.
INSTALL_TIMEOUT = 90
RESTORE_TIMEOUT = 60
STEP_TIMEOUT = 60
# After a cut-off, how long to keep looking for the outcome record of an
# action that may genuinely still be running, before calling it failed.
DONE_GRACE = 30
DONE_POLL = 5


def parse_stamp(text):
    """The commit out of ProductVersion, or None when nothing is installed."""
    m = STAMP_RE.search(text or "")
    return m.group(1).lower() if m else None


def pick_artifact_run(runs, artifacts_of):
    """The newest ci.yml run at the sha that still holds the Windows installer.
    `runs` is the API's workflow_runs list; `artifacts_of(run_id)` returns that
    run's artifacts. A rerun uploads afresh, so the newest wins."""
    ci = [r for r in runs if str(r.get("path", "")).endswith("/ci.yml")]
    ci.sort(key=lambda r: r.get("created_at") or "", reverse=True)
    for r in ci:
        for a in artifacts_of(r["id"]):
            if a.get("name") == ARTIFACT and not a.get("expired"):
                return r["id"]
    return None


def _tail(text, n=800):
    text = (text or "").strip()
    return text if len(text) <= n else "..." + text[-n:]


class WindowsMachine:
    name = "windows"

    def __init__(self, cfg, sh=None, spawn=None):
        mc = (cfg.get("machines") or {}).get("windows") or {}
        self.host = mc.get("ssh", "windows")
        self.repo = cfg.get("repo", "Uplift-Foundation/Orbweaver")
        self.local_dir = os.path.join(cfg.get("state_dir", "/tmp"), "windows")
        self.sh = sh or default_sh
        self._script_pushed = False
        self.new_nonce = lambda: secrets.token_hex(8)
        self.sleep = time.sleep
        self.clock = time.monotonic
        self.spawn = spawn or (lambda argv: subprocess.Popen(
            argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, errors="replace"))

    # -- plumbing

    def _ssh_argv(self, *remote):
        return ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", *SSH_KEEPALIVE, self.host] + list(remote)

    def _call(self, argv, log=None, timeout=120, what=None):
        try:
            r = self.sh(argv, timeout=timeout)
        except subprocess.TimeoutExpired:
            if log:
                log(f"{what or argv[0]}: timed out after {timeout} s")
            raise MachineError(f"{what or argv[0]} timed out after {timeout} s")
        if log:
            log(f"{what or ' '.join(argv)}: exit {r.returncode}")
        if r.returncode != 0:
            raise MachineError(f"{what or argv[0]} exited {r.returncode}: {_tail(r.stderr or r.stdout)}")
        return r.stdout

    def _push_script(self, log=None):
        if self._script_pushed:
            return
        self._call(self._ssh_argv("mkdir", "-p", REMOTE_DIR), log, what="ssh mkdir")
        self._call(["scp", "-q", "-o", "BatchMode=yes", *SSH_KEEPALIVE, SCRIPT, f"{self.host}:{REMOTE_DIR}/ow-machine.ps1"],
                   log, what="scp windows.ps1")
        self._script_pushed = True

    def _ps(self, action, *args, log=None, timeout=STEP_TIMEOUT):
        """Run one windows.ps1 action and return its output.

        CB-475: ssh to this PC can stay open long after the script is done,
        holding the install lock. So the call is not simply waited on: every
        DONE_POLL seconds while ssh is still open, done-<action>.txt is read
        over a second, short ssh, and the moment it holds this call's nonce the
        outcome is taken from it and the hung ssh is killed. `timeout` caps an
        action that never records anything; after it, the record is looked
        for DONE_GRACE seconds more before the action is called unfinished.
        """
        self._push_script(log)
        nonce = self.new_nonce()
        what = f"windows.ps1 {action}"
        argv = self._ssh_argv("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                              "-File", f"{REMOTE_DIR}/ow-machine.ps1", "-Action", action, *args, "-Nonce", nonce)
        proc = self.spawn(argv)
        started = self.clock()
        while True:
            try:
                out, err = proc.communicate(timeout=DONE_POLL)
                break
            except subprocess.TimeoutExpired:
                pass
            recorded = self._record(action, nonce)
            if recorded is not None:
                self._kill(proc)
                if log:
                    log(f"{what}: finished after {self.clock() - started:.0f} s (recorded by the script); "
                        "its ssh was still open and has been closed")
                return self._recorded(what, recorded)
            if self.clock() - started >= timeout:
                self._kill(proc)
                if log:
                    log(f"{what}: timed out after {timeout} s with no outcome recorded; "
                        f"looking for one {DONE_GRACE} s more")
                return self._outcome(action, nonce, what, log, timeout)
        if log:
            log(f"{what}: exit {proc.returncode} after {self.clock() - started:.0f} s")
        if proc.returncode != 0:
            raise MachineError(f"{what} exited {proc.returncode}: {_tail(err or out)}")
        return out

    @staticmethod
    def _kill(proc):
        proc.kill()
        try:
            proc.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            pass

    def _record(self, action, nonce):
        """(state, text) from done-<action>.txt when it is this call's, else None."""
        try:
            r = self.sh(self._ssh_argv("cat", f"{REMOTE_DIR}/done-{action}.txt"), timeout=15)
            text = r.stdout if r.returncode == 0 else ""
        except subprocess.TimeoutExpired:
            text = ""
        head, _, rest = (text or "").partition("\n")
        word = head.strip().split(" ")
        if len(word) == 2 and word[0] == nonce and word[1] in ("ok", "failed"):
            return word[1], rest
        return None

    @staticmethod
    def _recorded(what, recorded):
        state, text = recorded
        if state == "failed":
            raise MachineError(f"{what} failed: {_tail(text)}")
        return text

    def _outcome(self, action, nonce, what, log, timeout):
        """After the cut-off: the record, if it appears within DONE_GRACE."""
        waited = 0
        while True:
            recorded = self._record(action, nonce)
            if recorded is not None:
                if log:
                    log(f"{what}: finished (recorded by the script) after the cut-off")
                return self._recorded(what, recorded)
            if waited >= DONE_GRACE:
                raise MachineError(f"{what} timed out after {timeout} s and recorded no outcome "
                                   f"within {DONE_GRACE} s more")
            self.sleep(DONE_POLL)
            waited += DONE_POLL

    def _gh_json(self, path, log):
        return json.loads(self._call(["gh", "api", path], log, what=f"gh api {path}") or "null")

    def download(self, sha, log):
        """The installer CI built for `sha`, downloaded locally; its path."""
        runs = (self._gh_json(f"repos/{self.repo}/actions/runs?head_sha={sha}&per_page=50", log)
                or {}).get("workflow_runs", [])
        run_id = pick_artifact_run(
            runs, lambda rid: (self._gh_json(f"repos/{self.repo}/actions/runs/{rid}/artifacts", log)
                               or {}).get("artifacts", []))
        if run_id is None:
            raise MachineError(f"no {ARTIFACT} artifact for {sha} yet; CI may still be packaging it")
        dest = os.path.join(self.local_dir, "download")
        shutil.rmtree(dest, ignore_errors=True)
        os.makedirs(dest)
        self._call(["gh", "run", "download", str(run_id), "--repo", self.repo, "-n", ARTIFACT, "-D", dest],
                   log, timeout=600, what=f"gh run download {run_id} -n {ARTIFACT}")
        exes = sorted(f for f in os.listdir(dest) if f.lower().endswith("-win-x64-setup.exe"))
        if len(exes) != 1:
            raise MachineError(f"{ARTIFACT} for {sha} holds {len(exes)} installers, expected 1")
        return os.path.join(dest, exes[0])

    # -- the seam

    def installed_commit(self):
        return parse_stamp(self._ps("stamp"))

    def install(self, pr, sha, log):
        setup = self.download(sha, log)
        self._push_script(log)
        self._call(["scp", "-q", "-o", "BatchMode=yes", *SSH_KEEPALIVE, setup, f"{self.host}:{REMOTE_DIR}/setup.exe"],
                   log, timeout=600, what="scp setup.exe")
        log(self._ps("keep", log=log).strip())
        log(self._ps("install", "-Setup", f"{REMOTE_DIR}/setup.exe", log=log, timeout=INSTALL_TIMEOUT).strip())

    def restore(self, log):
        log(self._ps("restore", log=log, timeout=RESTORE_TIMEOUT).strip())

    def run(self, command, timeout_s):
        try:
            r = self.sh(self._ssh_argv(command), timeout=timeout_s)
        except subprocess.TimeoutExpired as e:
            def text(b):
                return b.decode("utf-8", "replace") if isinstance(b, bytes) else (b or "")
            return RunResult(None, text(e.stdout), text(e.stderr), True)
        return RunResult(r.returncode, r.stdout, r.stderr, False)
