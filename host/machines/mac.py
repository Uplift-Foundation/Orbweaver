#!/usr/bin/env python3
"""The two Macs behind the bug-fix org's machine runner (CB-470; seam in base.py).

host/machines/runner.py owns the contract: the spool, the hold, the lock, the
log, and the stamp checks before and after. This file is the seam for `mini`
(the runner runs on it, so it is local) and `macbook` (over ssh):

    MacMachine(name, cfg, sh=None)
    install(pr, sha, log)      fetch the PR's signed build, keep the installed
                               app aside, put the new one in /Applications
    restore(log)               put the kept app back; delete e2e/bugfix-pr-<n>
                               (a failed install deletes it too)
    run(command, timeout_s)    -> RunResult
    installed_commit()         the 40-hex commit stamped in Orbweaver.dll

Why a signed build and why this route. The Macs run the Developer ID signed
app, and an ad hoc build over it silently breaks click-to-focus (Automation
consent is tied to the code identity). The identity is not on the mini and the
MacBook's keychain is not usable over ssh, so the build is CI's: the PR head
sha is pushed as `e2e/bugfix-pr-<n>`, release.yml is dispatched on it, the
`release` environment deployment is approved (the host's `gh` login is a
required reviewer), and the `dmg-osx-arm64` artifact is downloaded. The
environment only deploys from `v*` tags and `e2e/*` branches, which is why the
branch carries that prefix, and release.yml's `publish` job runs on tags only,
so this route never publishes a release. Measured on the CB-470 probe (run
45689769046): dispatch to a downloaded, stapled DMG in about five minutes.

One build serves both Macs (both are arm64): it is cached by sha under the
runner's state_dir on the mini, so whichever Mac installs second, or a second
install in the same hold, reuses it. The PR and sha of that build are recorded
beside it, which is how restore(), which the seam gives no PR, knows which
e2e/ branch to delete.

How an install can go wrong, and what each step below is for (all of these
have happened by hand, see the lead's notes for CB-289/341/406):
  * the bundle vanished for a minute when an ssh session dropped between
    `rm -rf` and the copy: the new bundle is copied next to the old one while
    the old one still runs, and only then is the app stopped and the copy
    swapped in with two renames; the whole thing runs as one script on the
    Mac, so a dropped connection cannot stop it half way, and any failure
    after the stop relaunches whatever is in /Applications;
  * a half-copied bundle that codesign rejected: a failed copy now never
    reaches /Applications, and the DMG's app is verified (signature, Team ID,
    Gatekeeper's notarization verdict, stamped commit) before anything is
    touched, and the installed one again afterwards;
  * a check that compared the host stub, which is the same in every build: the
    commit is read from Contents/MacOS/Orbweaver.dll, where the build stamps it
    into the informational version (`1.0.13+<sha>`);
  * a launchd keep-alive restarting the *old* binary mid-swap: its plist is
    unloaded before the stop and loaded again after, as build-macos-app.sh does.

The previous app is kept aside once per hold: a retried or second install
keeps the first one's copy, because that is the build that was there before
the PR took the machine. restore() deletes it only after the put-back
verifies, so a failed restore can be retried.

Every external command goes through `sh` (base.default_sh's shape), so
tools/test-bugfix-machines-mac.py drives every rule with fakes: `gh` argv to a
scripted GitHub, and the install and restore scripts for real under bash
against a scratch /Applications.
"""
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import time

if __package__ in (None, ""):
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
from host.machines.base import SSH_KEEPALIVE, MachineError, RunResult, default_sh  # noqa: E402

REPO = "Uplift-Foundation/Orbweaver"
TEAM_ID = "5AQ4ULRG3Z"
BUNDLE_ID = "io.github.wtvamp.claudebuddy"
ARTIFACT = "dmg-osx-arm64"
E2E_PREFIX = "e2e/bugfix-pr-"
# Where the previous app waits during a hold, on each Mac. Deliberately not
# ~/.config/orbweaver-bugfix, which belongs to the running warden.
ASIDE = "$HOME/Library/Application Support/Orbweaver-bugfix-machines"
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
# The stamp: <Version>+<commit>, e.g. 1.0.13+83e57796...; the version may carry
# a prerelease suffix (0.5.0-beta). Same shape the lead's notes grep for.
STAMP_RE = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.]+)?\+([0-9a-f]{40})")
STAMP_GREP = r"[0-9]\{1,\}\.[0-9]\{1,\}\.[0-9]\{1,\}[-0-9A-Za-z.]*+[0-9a-f]\{40\}"
# timed() ends a command that ran out of time with this exit code and this
# line on stderr; run() needs both before it calls it a timeout, so a command
# that exits 124 by itself is not reported as one.
TIMEOUT_EXIT = 124
TIMEOUT_MARK = "ow-machines: timed out"
# The probe took under five minutes end to end; this is the point at which a
# build is stuck rather than slow, well inside the 45-minute hold.
BUILD_TIMEOUT_S = 25 * 60
POLL_S = 15
INSTALL_TIMEOUT_S = 5 * 60
MACBOOK_ROUTES = [["mbp"], ["-p", "2222", "warrenthompson@localhost"]]


# ---------------------------------------------------------------- pure rules

def parse_stamp(text):
    """The single commit stamped in `text` (grep output), or None.

    More than one distinct commit means the grep matched something other than
    the version stamp, which is a reason to refuse rather than to guess.
    """
    found = set(STAMP_RE.findall(text or ""))
    if len(found) == 1:
        return found.pop()
    return None


def e2e_branch(pr):
    return f"{E2E_PREFIX}{int(pr)}"


def check_args(pr, sha):
    if not (isinstance(pr, int) and not isinstance(pr, bool) and pr > 0):
        raise MachineError("pr must be a positive integer")
    if not (isinstance(sha, str) and SHA_RE.match(sha)):
        raise MachineError("sha must be 40 lowercase hex characters")


def pick_run(runs, sha, not_before):
    """The workflow_dispatch run of `sha` created at or after `not_before`
    (ISO 8601 UTC, as GitHub writes it), newest first; None if not there yet.

    Matching the sha alone is not enough: a re-install of the same PR would
    find the previous dispatch's run, already finished, and download that.
    """
    ok = [r for r in runs or []
          if r.get("head_sha") == sha and r.get("event") == "workflow_dispatch"
          and (r.get("created_at") or "") >= not_before]
    ok.sort(key=lambda r: r.get("created_at") or "", reverse=True)
    return ok[0] if ok else None


def release_env_ids(pending):
    """Environment ids of pending `release` deployments this login may approve.

    Only `release`: approving whatever happens to be pending would let a change
    to release.yml widen what this runner signs off on.
    """
    return [p["environment"]["id"] for p in pending or []
            if (p.get("environment") or {}).get("name") == "release"
            and p.get("current_user_can_approve")]


def macos_job_problem(jobs):
    """Why the run's osx-arm64 build cannot be used (None = usable).

    The Windows job and the osx-x64 leg are not needed for either Mac, so their
    failure does not block an install.
    """
    for j in jobs or []:
        name = j.get("name") or ""
        if name.startswith("macos") and "osx-arm64" in name:
            if j.get("conclusion") == "success":
                return None
            return f"{name} ended {j.get('conclusion') or j.get('status')}"
    return "no macos osx-arm64 job in the run"


# ---------------------------------------------------------------- machine scripts
#
# Each is one bash script run in one go on the target Mac. They print
# `RESULT key=value` lines the Python side reads; anything else is diagnostics
# for the log. `set -u` and explicit checks rather than `set -e`, so every
# failure says which step it was.

_COMMON = r'''
set -uo pipefail
# OW_MACHINES_TEST_ROOT exists for tools/test-bugfix-machines-mac.py, which
# runs these scripts against a scratch /Applications; unset, it is the real one.
APP="${OW_MACHINES_TEST_ROOT:-}/Applications/Orbweaver.app"
EXE="$APP/Contents/MacOS/Orbweaver"
BUNDLE_ID=%(bundle)s
KEEP="$HOME/Library/LaunchAgents/$BUNDLE_ID.plist"
ASIDE=%(aside)s
# A failure after this script stopped the app relaunches whatever is in
# /Applications now (the old app, if the swap never happened), so a refused
# install never leaves Warren's machine without a running Orbweaver.
fail() {
  echo "RESULT error=$*"
  if [ -n "${STOPPED:-}" ] && [ -d "$APP" ] && [ -z "$(pids)" ]; then
    if [ -n "${KEEP_LOADED:-}" ] && [ -f "$KEEP" ]; then launchctl load "$KEEP" >/dev/null 2>&1
    else env -u CLAUDE_CONFIG_DIR open -a "$APP" >/dev/null 2>&1; fi
    echo "relaunched what is at $APP after the failure"
  fi
  exit 1
}
stamp() { LC_ALL=C grep -ao '%(grep)s' "$1/Contents/MacOS/Orbweaver.dll" 2>/dev/null | sed 's/.*+//' | sort -u; }
pids() { ps -axo pid=,uid=,comm= | awk -v u="$(id -u)" -v e="$EXE" '$2==u && substr($0, index($0,$3))==e {print $1}'; }
stop_app() {
  KEEP_LOADED=
  if launchctl list "$BUNDLE_ID" >/dev/null 2>&1; then KEEP_LOADED=1; fi
  [ -f "$KEEP" ] && launchctl unload "$KEEP" >/dev/null 2>&1
  P="$(pids)"
  WAS_RUNNING="$P"
  [ -n "$P" ] && kill -TERM $P 2>/dev/null
  for _ in $(seq 1 50); do [ -z "$(pids)" ] && break; sleep 0.2; done
  P="$(pids)"; [ -n "$P" ] && kill -KILL $P 2>/dev/null && sleep 1
  STOPPED=1
  [ -z "$(pids)" ] || fail "the running app (pid $(pids | tr '\n' ' ')) survived SIGKILL"
  echo "stopped: ${WAS_RUNNING:-nothing running}"
}
stage() {  # $1 = verified bundle; copied next to $APP while the app still runs
  rm -rf "$APP.incoming"
  ditto "$1" "$APP.incoming" || { rm -rf "$APP.incoming"; fail "ditto into /Applications failed; the installed app was not touched"; }
}
swap_in() {  # the staged copy becomes $APP: two renames, nothing copied
  rm -rf "$APP.outgoing"
  if [ -d "$APP" ]; then mv "$APP" "$APP.outgoing" || fail "could not move the installed app out of the way"; fi
  mv "$APP.incoming" "$APP" || { [ -d "$APP.outgoing" ] && mv "$APP.outgoing" "$APP"; fail "could not move the new app into place"; }
  rm -rf "$APP.outgoing"
}
start_app() {
  if [ -n "$KEEP_LOADED" ] && [ -f "$KEEP" ]; then
    launchctl load "$KEEP" >/dev/null 2>&1
    # launchd starts it; give that a moment before starting a second copy by hand
    for _ in $(seq 1 10); do [ -n "$(pids)" ] && break; sleep 0.5; done
  fi
  if [ -z "$(pids)" ]; then env -u CLAUDE_CONFIG_DIR open -a "$APP"; fi
  for _ in $(seq 1 30); do [ -n "$(pids)" ] && break; sleep 0.5; done
  [ -n "$(pids)" ] || fail "the app did not start after the swap"
  echo "RESULT pid=$(pids | tr '\n' ' ')"
}
verify_signed() {  # $1 = bundle
  codesign --verify --deep --strict "$1" 2>&1 || fail "codesign --verify refused $1"
  TEAM="$(codesign -dv "$1" 2>&1 | sed -n 's/^TeamIdentifier=//p')"
  [ "$TEAM" = %(team)s ] || fail "$1 is signed by team '${TEAM:-none}', not %(team)s"
}
verify_notarized() {  # $1 = bundle; Gatekeeper's own verdict, as a user's Mac would give it
  spctl -a -t exec -vv "$1" 2>&1 || fail "spctl refused $1: not a notarized Developer ID app"
}
'''


def _common():
    return _COMMON % {"bundle": BUNDLE_ID, "aside": f'"{ASIDE}"',
                      "grep": STAMP_GREP, "team": TEAM_ID}


def install_script(dmg, sha):
    return _common() + r'''
DMG=%(dmg)s
SHA=%(sha)s
# Under the test's scratch root when it sets one: there the stub hdiutil's
# "mount" is a plain copy that detach cannot empty, so the trap's rmdir fails
# and /tmp kept one folder per test install (394 on the mini in a day).
MNT="$(mktemp -d "${OW_MACHINES_TEST_ROOT:-}/tmp/ow-bugfix-dmg.XXXXXX")"
cleanup() {
  hdiutil detach -quiet "$MNT" >/dev/null 2>&1; rmdir "$MNT" 2>/dev/null
  case "$DMG" in /tmp/ow-bugfix-*) rm -f "$DMG" ;; esac  # the copy put() made
}
trap cleanup EXIT
hdiutil attach -nobrowse -readonly -noverify -quiet -mountpoint "$MNT" "$DMG" || fail "hdiutil could not mount the DMG"
NEW="$MNT/Orbweaver.app"
[ -d "$NEW" ] || fail "the DMG holds no Orbweaver.app"
verify_signed "$NEW"
verify_notarized "$NEW"
[ "$(stamp "$NEW")" = "$SHA" ] || fail "the DMG's app is stamped '$(stamp "$NEW" | tr '\n' ' ')', not $SHA"
mkdir -p "$ASIDE" || fail "could not create the aside folder"
if [ -f "$ASIDE/previous.commit" ]; then
  echo "keeping the copy already aside ($(cat "$ASIDE/previous.commit")) from earlier in this hold"
elif [ -d "$APP" ]; then
  rm -rf "$ASIDE/Orbweaver.app"
  ditto "$APP" "$ASIDE/Orbweaver.app" || fail "could not keep the installed app aside"
  C="$(stamp "$APP")"
  [ "$(stamp "$ASIDE/Orbweaver.app")" = "$C" ] || fail "the copy kept aside does not match the installed app"
  printf '%%s\n' "${C:-unstamped}" > "$ASIDE/previous.commit"
else
  printf 'none\n' > "$ASIDE/previous.commit"
fi
echo "RESULT previous=$(cat "$ASIDE/previous.commit")"
stage "$NEW"
stop_app
swap_in
verify_signed "$APP"
[ "$(stamp "$APP")" = "$SHA" ] || fail "installed app is stamped '$(stamp "$APP" | tr '\n' ' ')', not $SHA"
start_app
echo "RESULT commit=$(stamp "$APP")"
''' % {"dmg": shlex.quote(dmg), "sha": shlex.quote(sha)}


def restore_script():
    return _common() + r'''
if [ ! -f "$ASIDE/previous.commit" ]; then echo "RESULT restored=nothing-aside"; exit 0; fi
PREV="$(cat "$ASIDE/previous.commit")"
echo "RESULT previous=$PREV"
if [ "$PREV" = none ]; then
  stop_app
  rm -rf "$APP"
  rm -rf "$ASIDE"
  echo "RESULT restored=none"
  exit 0
fi
[ -d "$ASIDE/Orbweaver.app" ] || fail "previous.commit says $PREV but no bundle is kept aside"
stage "$ASIDE/Orbweaver.app"
stop_app
swap_in
codesign --verify --deep --strict "$APP" 2>&1 || fail "codesign --verify refused the restored app"
GOT="$(stamp "$APP")"
[ "${GOT:-unstamped}" = "$PREV" ] || fail "restored app is stamped '$GOT', expected $PREV"
start_app
rm -rf "$ASIDE"
echo "RESULT commit=${GOT:-unstamped}"
echo "RESULT restored=$PREV"
'''


def commit_script():
    # The raw stamp, version and all, so parse_stamp() sees what grep saw.
    return _common() + r'''
[ -f "$APP/Contents/MacOS/Orbweaver.dll" ] || { echo "RESULT stamp="; exit 0; }
echo "RESULT stamp=$(LC_ALL=C grep -ao '%s' "$APP/Contents/MacOS/Orbweaver.dll" | sort -u | tr '\n' ' ')"
''' % STAMP_GREP


def results(stdout):
    """`RESULT k=v` lines into a dict (last wins)."""
    out = {}
    for line in (stdout or "").splitlines():
        if line.startswith("RESULT ") and "=" in line:
            k, v = line[7:].split("=", 1)
            out[k.strip()] = v.strip()
    return out


def timed(command, timeout_s):
    """`command` wrapped so the Mac it runs on ends it after timeout_s.

    Killing the local `ssh` on a timeout leaves the remote command running, so
    the limit has to live on the far side. macOS has no `timeout(1)`, so perl
    does it: the command runs in its own process group, and at the limit the
    whole group gets TERM, then KILL if any of it is left two seconds later,
    and perl prints TIMEOUT_MARK and exits 124.

    The group matters. Measured on both Macs: with a plain alarm that ended
    only the top bash, `sleep 30` under a 3 s limit still returned after 30 s,
    because the orphaned child held the output pipe open, and the runner
    would have waited out every runaway command in full.
    """
    perl = ('my $t = shift @ARGV; my $pid = fork; die "fork: $!" unless defined $pid;'
            ' if (!$pid) { setpgrp(0, 0); exec @ARGV or exit 127 }'
            ' $SIG{ALRM} = sub { kill "TERM", -$pid;'
            ' for (1 .. 20) { last unless kill 0, -$pid; select(undef, undef, undef, 0.1) }'
            f' kill "KILL", -$pid; print STDERR "\\n{TIMEOUT_MARK} after $t s\\n"; exit {TIMEOUT_EXIT} }};'
            ' alarm $t; waitpid($pid, 0);'
            ' exit($? & 127 ? 128 + ($? & 127) : $? >> 8)')
    return (f"perl -e {shlex.quote(perl)} {int(timeout_s)} "
            f"/bin/bash -c {shlex.quote(command)}")


def _tail(text, n=300):
    text = (text or "").strip()
    return text if len(text) <= n else "..." + text[-n:]


def _text(b):
    return b.decode("utf-8", "replace") if isinstance(b, bytes) else (b or "")


# ---------------------------------------------------------------- the machines

class MacMachine:
    """One Mac. `sh` is base.default_sh's shape: (argv, timeout=, input=) ->
    CompletedProcess with text output; every command here goes through it.

    machines.json may give, under machines.<name>:
      macbook: "ssh": a list of routes, each the ssh arguments before the
               command, tried in order while ssh itself cannot connect
               (exit 255). Default: `mbp`, then the reverse tunnel Warren runs
               from the MacBook to the mini's localhost:2222 when he is away.
    """

    def __init__(self, name, cfg, sh=None, clock=time.time, sleep=time.sleep):
        if name not in ("mini", "macbook"):
            raise ValueError(f"not a Mac: {name}")
        mc = (cfg.get("machines") or {}).get(name) or {}
        self.name = name
        self.local = name == "mini"
        self.routes = [list(r) for r in mc.get("ssh", MACBOOK_ROUTES)]
        self.repo = cfg.get("repo", REPO)
        self.build_dir = os.path.join(cfg.get("state_dir", "/tmp"), "mac-build")
        self.sh = sh or default_sh
        self.clock, self.sleep = clock, sleep

    # -- plumbing

    def _sh(self, argv, timeout, input=None):
        try:
            return self.sh(argv, timeout=timeout, input=input)
        except subprocess.TimeoutExpired as e:
            return subprocess.CompletedProcess(argv, None, _text(e.stdout), _text(e.stderr))

    def _on_mac(self, script, timeout):
        """Run a bash script on this Mac: CompletedProcess (returncode None on
        our own timeout). On the MacBook, try each route while ssh itself
        cannot connect; any other answer is the script's and stops there."""
        if self.local:
            return self._sh(["/bin/bash", "-c", script], timeout)
        r = None
        for route in self.routes:
            r = self._sh(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", *SSH_KEEPALIVE]
                         + route + ["/bin/bash -c " + shlex.quote(script)], timeout)
            if r.returncode != 255:
                return r
        return r

    def _script(self, script, log, what, timeout=INSTALL_TIMEOUT_S):
        r = self._on_mac(script, timeout)
        res = results(r.stdout)
        for line in (r.stdout or "").splitlines():
            if line and not line.startswith("RESULT "):
                log(f"{what}: {line}")
        log(f"{what}: exit {r.returncode}")
        if r.returncode != 0 or "error" in res:
            why = res.get("error") or _tail(r.stderr or r.stdout) or (
                "timed out" if r.returncode is None else f"exit {r.returncode}")
            raise MachineError(f"{self.name}: {what} failed: {why}")
        return res

    def _gh(self, args, log, what, timeout=120):
        r = self._sh(["gh"] + list(args), timeout)
        log(f"gh {what}: exit {r.returncode}")
        return r

    def _gh_json(self, args, log, what):
        r = self._gh(args, log, what)
        if r.returncode != 0:
            raise MachineError(f"{what}: {_tail(r.stderr or r.stdout)}")
        try:
            return json.loads(r.stdout) if (r.stdout or "").strip() else None
        except ValueError:
            raise MachineError(f"{what}: gh did not return JSON")

    # Per route. QA's MacBook proof on b758f80 lost the first two copies to a
    # transient "No route to host / lost connection", which scp reports as
    # exit 1, not ssh's 255, so one retry per route, and every route is tried.
    PUT_ATTEMPTS = 2

    def _put(self, path, log):
        """The DMG where this Mac can mount it: the mini reads it in place.

        A failed copy is retried once on the same route, then the next route is
        tried, whatever scp's exit code. After each failure the partial file is
        removed on the MacBook over that route (best effort), so a lost
        connection does not leave 70 MB in its /tmp.
        """
        if self.local:
            return path
        remote = "/tmp/ow-bugfix-" + os.path.basename(path)
        last = None
        for route in self.routes:
            # scp spells ssh's -p as -P; a route is ssh arguments then a host.
            args = ["-P" if a == "-p" else a for a in route[:-1]]
            for attempt in range(1, self.PUT_ATTEMPTS + 1):
                last = self._sh(["scp", "-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", *SSH_KEEPALIVE]
                                + args + [path, f"{route[-1]}:{remote}"], 600)
                log(f"scp DMG via {' '.join(route)} (attempt {attempt}): exit {last.returncode}")
                if last.returncode == 0:
                    return remote
                rm = self._sh(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", *SSH_KEEPALIVE]
                              + route + ["rm -f " + shlex.quote(remote)], 30)
                log(f"rm partial DMG via {' '.join(route)}: exit {rm.returncode}")
                if last.returncode == 255:
                    break  # ssh cannot connect on this route: go to the next one
        raise MachineError(f"{self.name}: copying the DMG failed: {_tail(last.stderr) or 'timed out'}")

    # -- the seam

    def installed_commit(self):
        r = self._on_mac(commit_script(), 60)
        if r.returncode != 0:
            raise MachineError(f"{self.name}: could not read the installed commit: "
                               f"{_tail(r.stderr or r.stdout) or 'timed out'}")
        return parse_stamp(results(r.stdout).get("stamp", ""))

    def install(self, pr, sha, log):
        check_args(pr, sha)
        try:
            dmg = self.signed_build(pr, sha, log)
            res = self._script(install_script(self._put(dmg, log), sha), log, "install")
        except MachineError:
            # Lead's ruling: the e2e/ branch goes on a failed install too, not
            # only on release. Directly by PR, since a build that failed has
            # no record for release_build() to read.
            self._delete_branch(pr, log)
            raise
        log(f"{self.name}: installed {res.get('commit')}, pid {res.get('pid', '?').strip()}, "
            f"kept aside {res.get('previous')}")

    def restore(self, log):
        res = self._script(restore_script(), log, "restore")
        restored = res.get("restored")
        log(f"{self.name}: nothing kept aside, nothing to restore" if restored == "nothing-aside"
            else f"{self.name}: restored {restored}")
        self.release_build(log)

    def run(self, command, timeout_s):
        r = self._on_mac(timed(command, timeout_s), timeout_s + 30)
        err = r.stderr or ""
        if r.returncode is None:  # our own backstop: the Mac never answered
            return RunResult(None, r.stdout or "", err, True)
        if r.returncode == TIMEOUT_EXIT and TIMEOUT_MARK in err:
            return RunResult(None, r.stdout or "", err, True)
        return RunResult(r.returncode, r.stdout or "", err, False)

    # -- the signed build both Macs share

    def _record(self):
        try:
            with open(os.path.join(self.build_dir, "build.json")) as f:
                return json.load(f)
        except (OSError, ValueError):
            return None

    def signed_build(self, pr, sha, log):
        """A notarized DMG of `sha`, built by release.yml on e2e/bugfix-pr-<pr>;
        downloaded once and reused by the other Mac."""
        check_args(pr, sha)
        rec = self._record()
        if rec and rec.get("sha") == sha and rec.get("dmg") and os.path.isfile(rec["dmg"]):
            log(f"signed build of {sha} already here: {rec['dmg']}")
            return rec["dmg"]
        branch = e2e_branch(pr)
        start = self.clock()
        r = self._gh(["api", "-X", "POST", f"repos/{self.repo}/git/refs",
                      "-f", f"ref=refs/heads/{branch}", "-f", f"sha={sha}"], log, f"create {branch}")
        if r.returncode != 0:
            if "already exists" not in (r.stderr or "") + (r.stdout or ""):
                raise MachineError(f"could not push {branch}: {_tail(r.stderr or r.stdout)}")
            self._gh_json(["api", "-X", "PATCH", f"repos/{self.repo}/git/refs/heads/{branch}",
                           "-f", f"sha={sha}", "-F", "force=true"], log, f"move {branch}")
        log(f"pushed {sha} as {branch}")
        not_before = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(start - 5))
        r = self._gh(["workflow", "run", "release.yml", "--repo", self.repo, "--ref", branch],
                     log, "dispatch release.yml")
        if r.returncode != 0:
            raise MachineError(f"could not dispatch release.yml on {branch}: {_tail(r.stderr or r.stdout)}")

        run, approved = None, False
        while True:
            if self.clock() - start > BUILD_TIMEOUT_S:
                raise MachineError(f"the signed build did not finish in {BUILD_TIMEOUT_S // 60} minutes"
                                   + (f" (run {run['id']})" if run else " (no run found)"))
            if run is None:
                data = self._gh_json(["api", f"repos/{self.repo}/actions/workflows/release.yml/runs"
                                      f"?branch={branch}&event=workflow_dispatch&per_page=10"],
                                     log, "list release.yml runs")
                run = pick_run((data or {}).get("workflow_runs"), sha, not_before)
                if run:
                    log(f"release.yml run {run['id']} on {branch}")
            if run is not None:
                if not approved:
                    pending = self._gh_json(["api", f"repos/{self.repo}/actions/runs/{run['id']}"
                                             "/pending_deployments"], log, "read pending deployments")
                    ids = release_env_ids(pending)
                    if ids:
                        args = ["api", "-X", "POST",
                                f"repos/{self.repo}/actions/runs/{run['id']}/pending_deployments"]
                        for i in ids:
                            args += ["-F", f"environment_ids[]={i}"]
                        args += ["-f", "state=approved", "-f",
                                 f"comment=CB-470 machine runner: signed build of PR #{pr} at {sha}"]
                        self._gh_json(args, log, "approve the release deployment")
                        approved = True
                        log(f"approved the release deployment of run {run['id']}")
                state = self._gh_json(["api", f"repos/{self.repo}/actions/runs/{run['id']}"],
                                      log, "read the run") or {}
                if state.get("status") == "completed":
                    jobs = self._gh_json(["api", f"repos/{self.repo}/actions/runs/{run['id']}/jobs"],
                                         log, "read the run's jobs")
                    why = macos_job_problem((jobs or {}).get("jobs"))
                    if why:
                        raise MachineError(f"release.yml run {run['id']}: {why}")
                    break
            self.sleep(POLL_S)

        # One build at a time is kept: about 210 MB each, and the record is
        # what restore() reads to find the branch.
        shutil.rmtree(self.build_dir, ignore_errors=True)
        dest = os.path.join(self.build_dir, sha)
        os.makedirs(dest)
        r = self._gh(["run", "download", str(run["id"]), "--repo", self.repo, "-n", ARTIFACT, "-D", dest],
                     log, f"download {ARTIFACT}", timeout=600)
        if r.returncode != 0:
            raise MachineError(f"could not download {ARTIFACT} from run {run['id']}: {_tail(r.stderr)}")
        dmgs = sorted(f for f in os.listdir(dest) if f.endswith(".dmg"))
        if len(dmgs) != 1:
            raise MachineError(f"{ARTIFACT} from run {run['id']} holds {len(dmgs)} DMGs, expected 1")
        dmg = os.path.join(dest, dmgs[0])
        # stapler runs on the mini, where the file is, whichever Mac asked.
        r = self._sh(["xcrun", "stapler", "validate", dmg], 120)
        if r.returncode != 0:
            raise MachineError(f"the DMG is not notarized (stapler validate): {_tail(r.stderr or r.stdout)}")
        with open(os.path.join(self.build_dir, "build.json"), "w") as f:
            json.dump({"pr": pr, "sha": sha, "run": run["id"], "dmg": dmg}, f)
        log(f"signed build ready in {int(self.clock() - start)} s: run {run['id']}, {dmg}")
        return dmg

    def release_build(self, log):
        """Delete the e2e/ branch the recorded build came from. Called by every
        Mac's restore, so the second finds it gone, which is fine. The DMG
        stays until the next build replaces it. Never raises."""
        rec = self._record()
        if rec and rec.get("pr"):
            self._delete_branch(rec["pr"], log)

    def _delete_branch(self, pr, log):
        """Delete e2e/bugfix-pr-<pr>; already gone is fine. Never raises."""
        branch = e2e_branch(pr)
        r = self._gh(["api", "-X", "DELETE", f"repos/{self.repo}/git/refs/heads/{branch}"],
                     log, f"delete {branch}")
        gone = "Reference does not exist" in (r.stderr or "") + (r.stdout or "") \
            or "Not Found" in (r.stderr or "") + (r.stdout or "")
        if r.returncode == 0:
            log(f"deleted {branch}")
        elif gone:
            log(f"{branch} was already gone")
        else:
            # Logged, not raised: the app is back, and a restore reported as
            # failed would have the runner retry it and call for a person
            # over a leftover branch. The next restore tries again.
            log(f"could not delete {branch}: {_tail(r.stderr or r.stdout)}")
