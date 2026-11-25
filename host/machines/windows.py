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

from host.machines.base import MachineError, RunResult, default_sh

REMOTE_DIR = "orbweaver-machines"
SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "windows.ps1")
ARTIFACT = "unsigned-win-x64"
STAMP_RE = re.compile(r"^\d+\.\d+\.\d+[^+\s]*\+([0-9a-fA-F]{7,40})\s*$", re.M)
# CB-475: how long one remote install or restore may hold the ssh call. The
# work itself takes about a minute; ssh to this PC can then stay open for many
# more after the script has finished (seen one install in two on 2026-11-25),
# and while it does the runner holds /tmp/ow-install.lock. So the call is cut
# off here and the script's own record of the outcome is read instead.
INSTALL_TIMEOUT = 4 * 60
RESTORE_TIMEOUT = 3 * 60
# After a cut-off, how long to keep looking for the outcome record of an
# action that may genuinely still be running, before calling it failed.
DONE_GRACE = 60
DONE_POLL = 10


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

    def __init__(self, cfg, sh=None):
        mc = (cfg.get("machines") or {}).get("windows") or {}
        self.host = mc.get("ssh", "windows")
        self.repo = cfg.get("repo", "Uplift-Foundation/Orbweaver")
        self.local_dir = os.path.join(cfg.get("state_dir", "/tmp"), "windows")
        self.sh = sh or default_sh
        self._script_pushed = False
        self.new_nonce = lambda: secrets.token_hex(8)
        self.sleep = time.sleep

    # -- plumbing

    def _ssh_argv(self, *remote):
        return ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", self.host] + list(remote)

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
        self._call(["scp", "-q", "-o", "BatchMode=yes", SCRIPT, f"{self.host}:{REMOTE_DIR}/ow-machine.ps1"],
                   log, what="scp windows.ps1")
        self._script_pushed = True

    def _ps(self, action, *args, log=None, timeout=120):
        self._push_script(log)
        nonce = self.new_nonce()
        what = f"windows.ps1 {action}"
        argv = self._ssh_argv("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                              "-File", f"{REMOTE_DIR}/ow-machine.ps1", "-Action", action, *args, "-Nonce", nonce)
        try:
            r = self.sh(argv, timeout=timeout)
        except subprocess.TimeoutExpired:
            # The local ssh has been killed by now. Whether the action finished
            # is what the script wrote, not what the channel did (CB-475).
            if log:
                log(f"{what}: ssh still open after {timeout} s; reading the script's own outcome")
            return self._outcome(action, nonce, what, log)
        if log:
            log(f"{what}: exit {r.returncode}")
        if r.returncode != 0:
            raise MachineError(f"{what} exited {r.returncode}: {_tail(r.stderr or r.stdout)}")
        return r.stdout

    def _outcome(self, action, nonce, what, log):
        """The result done-<action>.txt records for this call's nonce. Polled
        for DONE_GRACE seconds, since an action cut off while genuinely slow
        may still finish; a stale record from an earlier call never counts."""
        waited = 0
        while True:
            try:
                r = self.sh(self._ssh_argv("cat", f"{REMOTE_DIR}/done-{action}.txt"), timeout=30)
                text = r.stdout if r.returncode == 0 else ""
            except subprocess.TimeoutExpired:
                text = ""
            head, _, rest = (text or "").partition("\n")
            if head.strip() == f"{nonce} ok":
                if log:
                    log(f"{what}: finished (recorded by the script)")
                return rest
            if head.strip() == f"{nonce} failed":
                raise MachineError(f"{what} failed: {_tail(rest)}")
            if waited >= DONE_GRACE:
                raise MachineError(f"{what} did not finish: ssh was cut off after its timeout "
                                   f"and the script recorded no outcome within {DONE_GRACE} s more")
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
        self._call(["scp", "-q", "-o", "BatchMode=yes", setup, f"{self.host}:{REMOTE_DIR}/setup.exe"],
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
