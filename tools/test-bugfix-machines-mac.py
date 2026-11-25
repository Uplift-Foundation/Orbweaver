#!/usr/bin/env python3
"""Tests for the bug-fix org's Mac machines (CB-470): host/machines/mac.py.

    python3 tools/test-bugfix-machines-mac.py

stdlib unittest, like tools/test-bugfix-machines.py. Nothing here touches
GitHub, ssh or the real /Applications: every command goes through the
machine's `sh` seam, where `gh` meets a scripted fake and ssh/scp a fake
runner, and the install and restore scripts run for real under bash against a
scratch root (ORBWEAVER_MACHINES_ROOT) with stub `hdiutil`, `codesign` and
`launchctl` on PATH. The "app" in each scratch bundle is an ad hoc signed copy
of /bin/sleep, so stopping and relaunching it are real processes found by real
`ps` and ended by real `kill`. Each refusal has a passing control beside it,
so a check that goes blind turns red.

Not covered, and said plainly: the seams' contact with the world -- `gh`
against GitHub, ssh/scp against a real sshd, hdiutil/codesign/stapler on a
real notarized DMG, `open -a` through LaunchServices, and an app that survives
SIGKILL. Those are QA's proof on the two Macs. The script and timeout cases
need macOS (ps, ditto, perl) and skip elsewhere.
"""
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, ROOT)

from host.machines import base, mac, runner  # noqa: E402

SHA_NEW = "a" * 40
SHA_OLD = "b" * 40
SHA_OTHER = "c" * 40
IS_MAC = sys.platform == "darwin"


def done(argv, code=0, out="", err=""):
    return subprocess.CompletedProcess(argv, code, out, err)


# ---------------------------------------------------------------- pure rules

class PureRules(unittest.TestCase):
    def test_parse_stamp(self):
        self.assertEqual(mac.parse_stamp(f"1.0.13+{SHA_NEW} "), SHA_NEW)
        self.assertEqual(mac.parse_stamp(f"0.5.0-beta+{SHA_NEW}"), SHA_NEW)
        # the same stamp twice is still one commit
        self.assertEqual(mac.parse_stamp(f"1.0.13+{SHA_NEW} 1.0.13+{SHA_NEW}"), SHA_NEW)
        # refusals: nothing, a bare sha with no version, two different commits
        self.assertIsNone(mac.parse_stamp(""))
        self.assertIsNone(mac.parse_stamp(SHA_NEW))
        self.assertIsNone(mac.parse_stamp(f"1.0.13+{SHA_NEW} 1.0.12+{SHA_OLD}"))

    def test_check_args(self):
        mac.check_args(12, SHA_NEW)  # control
        for pr, sha in ((0, SHA_NEW), (True, SHA_NEW), ("12", SHA_NEW),
                        (12, SHA_NEW.upper()), (12, SHA_NEW[:39]), (12, "x; rm -rf /")):
            with self.assertRaises(base.MachineError, msg=(pr, sha)):
                mac.check_args(pr, sha)

    def test_e2e_branch(self):
        self.assertEqual(mac.e2e_branch(317), "e2e/bugfix-pr-317")

    def test_pick_run_needs_sha_event_and_time(self):
        t0 = "2026-11-25T19:54:20Z"
        runs = [
            {"id": 1, "head_sha": SHA_NEW, "event": "workflow_dispatch", "created_at": "2026-11-25T19:00:00Z"},
            {"id": 2, "head_sha": SHA_NEW, "event": "push", "created_at": "2026-11-25T19:54:30Z"},
            {"id": 3, "head_sha": SHA_OTHER, "event": "workflow_dispatch", "created_at": "2026-11-25T19:54:30Z"},
        ]
        # an earlier dispatch of the same sha, a push run, another sha: none qualify
        self.assertIsNone(mac.pick_run(runs, SHA_NEW, t0))
        runs.append({"id": 4, "head_sha": SHA_NEW, "event": "workflow_dispatch", "created_at": "2026-11-25T19:54:28Z"})
        runs.append({"id": 5, "head_sha": SHA_NEW, "event": "workflow_dispatch", "created_at": "2026-11-25T19:54:40Z"})
        self.assertEqual(mac.pick_run(runs, SHA_NEW, t0)["id"], 5)
        self.assertIsNone(mac.pick_run(None, SHA_NEW, t0))

    def test_release_env_ids_only_release_and_only_approvable(self):
        pending = [
            {"environment": {"id": 7, "name": "release"}, "current_user_can_approve": True},
            {"environment": {"id": 8, "name": "production"}, "current_user_can_approve": True},
            {"environment": {"id": 9, "name": "release"}, "current_user_can_approve": False},
        ]
        self.assertEqual(mac.release_env_ids(pending), [7])
        self.assertEqual(mac.release_env_ids([]), [])
        self.assertEqual(mac.release_env_ids(None), [])

    def test_macos_job_problem(self):
        ok = [{"name": "macos (osx-arm64)", "conclusion": "success"},
              {"name": "macos (osx-x64)", "conclusion": "failure"},
              {"name": "windows", "conclusion": "failure"}]
        self.assertIsNone(mac.macos_job_problem(ok))  # other legs failing do not block
        bad = [{"name": "macos (osx-arm64)", "conclusion": "failure"}]
        self.assertIn("failure", mac.macos_job_problem(bad))
        self.assertIn("no macos", mac.macos_job_problem([{"name": "windows", "conclusion": "success"}]))

    def test_results(self):
        r = mac.results("noise\nRESULT pid=12 \nRESULT error=a=b\nRESULT pid=13\n")
        self.assertEqual(r, {"pid": "13", "error": "a=b"})


# ---------------------------------------------------------------- run / timeout

@unittest.skipUnless(IS_MAC, "needs macOS (perl, bash)")
class RunCommand(unittest.TestCase):
    def setUp(self):
        self.m = mac.MacMachine("mini", {})

    def test_run_returns_code_and_output(self):
        r = self.m.run("echo hi; echo oops >&2; exit 3", 10)
        self.assertEqual(r, base.RunResult(3, "hi\n", "oops\n", False))

    def test_run_times_out_on_the_far_side(self):
        t = time.monotonic()
        r = self.m.run("echo started; sleep 20; echo never", 1)
        self.assertTrue(r.timed_out)
        self.assertIsNone(r.exit_code)
        self.assertEqual(r.stdout, "started\n")
        self.assertIn(mac.TIMEOUT_MARK, r.stderr)
        # The child is ended too: returning only when `sleep 20` finished by
        # itself was the bug this pins, measured on both Macs.
        self.assertLess(time.monotonic() - t, 8)

    def test_a_command_that_ignores_term_is_killed(self):
        t = time.monotonic()
        r = self.m.run("trap '' TERM; sleep 20", 1)
        self.assertTrue(r.timed_out)
        self.assertLess(time.monotonic() - t, 8)

    def test_a_real_exit_124_is_not_a_timeout(self):
        r = self.m.run("exit 124", 10)
        self.assertEqual((r.exit_code, r.timed_out), (124, False))
        self.assertEqual(self.m.run("exit 0", 10).exit_code, 0)  # control
        self.assertEqual(self.m.run("kill -9 $$", 10).exit_code, 137)

    def test_run_quotes_the_command_whole(self):
        r = self.m.run("printf '%s|' \"a b\" 'c'\"'\"'d'", 10)
        self.assertEqual((r.exit_code, r.stdout), (0, "a b|c'd|"))


class SshRoutes(unittest.TestCase):
    def machine(self, codes, cfg=None):
        calls = []

        def sh(argv, timeout=None, input=None):
            calls.append(argv)
            code = codes.pop(0)
            if isinstance(code, Exception):
                raise code
            return done(argv, code, "out", "err")
        return mac.MacMachine("macbook", cfg or {}, sh=sh), calls

    def test_falls_back_to_tunnel_only_when_ssh_cannot_connect(self):
        m, calls = self.machine([255, 0])
        self.assertEqual(m.run("true", 5).exit_code, 0)
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0][-2], "mbp")
        self.assertEqual(calls[1][-4:-1], ["-p", "2222", "warrenthompson@localhost"])

    def test_script_failure_is_not_retried(self):
        m, calls = self.machine([1])
        self.assertEqual(m.run("false", 5).exit_code, 1)
        self.assertEqual(len(calls), 1)  # control: a real answer stops at route one

    def test_both_routes_down(self):
        m, calls = self.machine([255, 255])
        self.assertEqual(m.run("true", 5).exit_code, 255)
        self.assertEqual(len(calls), 2)

    def test_routes_come_from_machines_json(self):
        m, calls = self.machine([0], {"machines": {"macbook": {"ssh": [["other-mac"]]}}})
        m.run("true", 5)
        self.assertEqual(calls[0][-2], "other-mac")

    def test_backstop_timeout_is_a_timeout(self):
        m, _ = self.machine([subprocess.TimeoutExpired(["ssh"], 1, output=b"part")])
        r = m.run("x", 1)
        self.assertEqual((r.exit_code, r.stdout, r.timed_out), (None, "part", True))

    def test_put_scps_over_the_route_that_answers(self):
        m, calls = self.machine([255, 0])
        lines = []
        remote = m._put("/x/Orbweaver-1.0.13-osx-arm64.dmg", lines.append)
        self.assertEqual(remote, "/tmp/ow-bugfix-Orbweaver-1.0.13-osx-arm64.dmg")
        self.assertEqual(calls[0][0], "scp")
        self.assertEqual(calls[0][-1], "mbp:" + remote)
        self.assertIn("-P", calls[1])  # scp spells the port -P
        self.assertNotIn("-p", calls[1])
        self.assertEqual(calls[1][-1], "warrenthompson@localhost:" + remote)

    def test_put_refuses_when_copy_fails(self):
        m, calls = self.machine([1])
        with self.assertRaisesRegex(base.MachineError, "copying the DMG failed"):
            m._put("/x/a.dmg", lambda _: None)
        self.assertEqual(len(calls), 1)

    def test_the_mini_is_local(self):
        calls = []
        m = mac.MacMachine("mini", {}, sh=lambda a, timeout=None, input=None: (calls.append(a), done(a))[1])
        self.assertEqual(m._put("/x/a.dmg", lambda _: None), "/x/a.dmg")
        m.run("true", 5)
        self.assertEqual(calls[0][:2], ["/bin/bash", "-c"])
        self.assertEqual(len(calls), 1)  # no scp for the mini


# ---------------------------------------------------------------- install / restore scripts

STUB_HDIUTIL = r'''#!/bin/bash
# attach ... -mountpoint M DMG: the "DMG" is a folder named <x>.dmg.d next to it
if [ "$1" = attach ]; then
  M=""; prev=""; for a in "$@"; do [ "$prev" = -mountpoint ] && M="$a"; prev="$a"; done
  DMG="${@: -1}"
  cp -R "$DMG.d/." "$M/" || exit 1
fi
exit 0
'''
STUB_CODESIGN = r'''#!/bin/bash
B="${@: -1}"
case "$1" in
  --verify) [ -f "$B/BADSIG" ] && { echo "$B: invalid signature" >&2; exit 1; }; exit 0 ;;
  -dv) echo "TeamIdentifier=$(cat "$B/TEAM" 2>/dev/null || echo not set)" >&2; exit 0 ;;
esac
exit 0
'''
STUB_SPCTL = r'''#!/bin/bash
B="${@: -1}"
[ -f "$B/UNNOTARIZED" ] && { echo "$B: rejected" >&2; exit 3; }
echo "$B: accepted"; exit 0
'''
# Real ditto, except a source carrying DITTOFAIL copies part of itself and
# fails, as a full disk or a dropped volume would.
STUB_DITTO = r'''#!/bin/bash
if [ -f "$1/DITTOFAIL" ]; then mkdir -p "$2/Contents"; exit 1; fi
exec /usr/bin/ditto "$@"
'''
STUB_LAUNCHCTL = r'''#!/bin/bash
echo "$*" >> "$STUB_LOG"
if [ "$1" = list ]; then [ -f "$STUB_KEEPALIVE" ]; exit $?; fi
if [ "$1" = load ]; then
  nohup "$ORBWEAVER_MACHINES_ROOT/Applications/Orbweaver.app/Contents/MacOS/Orbweaver" 300 >/dev/null 2>&1 &
fi
exit 0
'''
STUB_OPEN = r'''#!/bin/bash
echo "open $* CLAUDE_CONFIG_DIR=${CLAUDE_CONFIG_DIR:-unset}" >> "$STUB_LOG"
nohup "$2/Contents/MacOS/Orbweaver" 300 >/dev/null 2>&1 &
exit 0
'''


@unittest.skipUnless(IS_MAC, "the install scripts are macOS bash (ps, ditto)")
class InstallScripts(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="ow-mac-test-")
        self.addCleanup(self.cleanup)
        self.root = os.path.join(self.tmp, "root")
        self.home = os.path.join(self.tmp, "home")
        self.bin = os.path.join(self.tmp, "bin")
        self.state = os.path.join(self.tmp, "state")
        for d in (self.root + "/Applications", self.home, self.bin, self.state):
            os.makedirs(d)
        for name, body in (("hdiutil", STUB_HDIUTIL), ("codesign", STUB_CODESIGN),
                           ("launchctl", STUB_LAUNCHCTL), ("open", STUB_OPEN),
                           ("spctl", STUB_SPCTL), ("ditto", STUB_DITTO)):
            p = os.path.join(self.bin, name)
            with open(p, "w") as f:
                f.write(body)
            os.chmod(p, 0o755)
        self.log_path = os.path.join(self.tmp, "stub.log")
        self.keepalive_flag = os.path.join(self.tmp, "keepalive-loaded")
        self.env = dict(os.environ, PATH=self.bin + ":" + os.environ["PATH"], HOME=self.home,
                        ORBWEAVER_MACHINES_ROOT=self.root, STUB_LOG=self.log_path,
                        STUB_KEEPALIVE=self.keepalive_flag, CLAUDE_CONFIG_DIR="/leaked/from/agent")
        self.app = os.path.join(self.root, "Applications", "Orbweaver.app")
        self.exe = os.path.join(self.app, "Contents", "MacOS", "Orbweaver")
        self.aside = os.path.join(self.home, "Library/Application Support/Orbweaver-bugfix-machines")
        self.gh_calls = []
        self.m = mac.MacMachine("mini", {"state_dir": self.state}, sh=self.sh)
        self.lines = []

    def cleanup(self):
        # Only processes this test started: the copies of sleep under our root.
        out = subprocess.run(["ps", "-axo", "pid=,comm="], capture_output=True, text=True).stdout
        for line in out.splitlines():
            pid, _, comm = line.strip().partition(" ")
            if self.tmp in comm:
                try:
                    os.kill(int(pid), signal.SIGKILL)
                except (ProcessLookupError, ValueError):
                    pass
        shutil.rmtree(self.tmp, ignore_errors=True)

    def sh(self, argv, timeout=None, input=None):
        if argv[0] == "gh":
            self.gh_calls.append(argv)
            return done(argv)
        return subprocess.run(argv, input=input, capture_output=True, text=True,
                              timeout=timeout, env=self.env)

    def log(self, line):
        self.lines.append(line)

    # fixtures

    def make_bundle(self, path, sha, team=mac.TEAM_ID, badsig=False, marks=()):
        macos = os.path.join(path, "Contents", "MacOS")
        os.makedirs(macos)
        exe = os.path.join(macos, "Orbweaver")
        shutil.copy("/bin/sleep", exe)
        # A copied platform binary is killed at exec; an ad hoc signature on
        # the copy lets it run (the real codesign, not the stub on PATH).
        subprocess.run(["/usr/bin/codesign", "-f", "-s", "-", exe], check=True, capture_output=True)
        with open(os.path.join(macos, "Orbweaver.dll"), "wb") as f:
            f.write(b"\x00MZjunk\x00" + (f"1.0.13+{sha}".encode() if sha else b"") + b"\x00tail")
        with open(os.path.join(path, "TEAM"), "w") as f:
            f.write(team)
        for mark in (("BADSIG",) if badsig else ()) + tuple(marks):
            open(os.path.join(path, mark), "w").close()

    def cache_build(self, sha, pr=12, stamp=None, **kw):
        """A downloaded build as signed_build() leaves one, so install() takes
        it from the cache and asks GitHub nothing."""
        d = os.path.join(self.state, "mac-build", sha)
        dmg = os.path.join(d, f"Orbweaver-{sha[:7]}.dmg")
        shutil.rmtree(os.path.join(self.state, "mac-build"), ignore_errors=True)
        self.make_bundle(dmg + ".d/Orbweaver.app", stamp or sha, **kw)
        open(dmg, "w").close()
        with open(os.path.join(self.state, "mac-build", "build.json"), "w") as f:
            json.dump({"pr": pr, "sha": sha, "run": 1, "dmg": dmg}, f)

    def install(self, sha, **kw):
        self.cache_build(sha, **kw)
        self.m.install(12, sha, self.log)

    def install_old(self, running=True):
        self.make_bundle(self.app, SHA_OLD)
        if running:
            proc = subprocess.Popen([self.exe, "300"], start_new_session=True)
            # Cleanups run last-registered first, so this one kills before it
            # reaps; a bare proc.wait would sit out the fixture's 300 s sleep.
            self.addCleanup(lambda: (proc.kill(), proc.wait()))
            for _ in range(50):
                if self.pids():
                    return
                time.sleep(0.1)
            self.fail("fixture app did not start")

    def pids(self):
        out = subprocess.run(["ps", "-axo", "pid=,stat=,comm="], capture_output=True, text=True).stdout
        found = []
        for line in out.splitlines():
            parts = line.split(None, 2)
            if len(parts) == 3 and parts[2] == self.exe and not parts[1].startswith("Z"):
                found.append(int(parts[0]))
        return found

    def stub_log(self):
        return open(self.log_path).read() if os.path.exists(self.log_path) else ""

    # cases

    def test_installed_commit_reads_the_dll_stamp(self):
        self.assertIsNone(self.m.installed_commit())  # no app at all
        self.install_old(running=False)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)

    def test_install_replaces_relaunches_and_keeps_previous_aside(self):
        self.install_old()
        old_pids = self.pids()
        self.install(SHA_NEW)
        self.assertEqual(self.m.installed_commit(), SHA_NEW)
        new_pids = self.pids()
        self.assertTrue(new_pids, "app was not relaunched")
        self.assertFalse(set(old_pids) & set(new_pids), "old process survived the install")
        self.assertEqual(open(os.path.join(self.aside, "previous.commit")).read().strip(), SHA_OLD)
        self.assertIn("CLAUDE_CONFIG_DIR=unset", self.stub_log())  # env -u reached open
        self.assertFalse(os.path.exists(self.app + ".incoming"))
        self.assertFalse(os.path.exists(self.app + ".outgoing"))
        self.assertEqual(self.gh_calls, [])  # cached build: GitHub not asked
        self.assertTrue(any("kept aside " + SHA_OLD in line for line in self.lines))

    def test_install_refuses_a_dmg_signed_by_another_team_and_leaves_the_app(self):
        self.install_old()
        with self.assertRaisesRegex(base.MachineError, "team"):
            self.install(SHA_NEW, team="ADHOC0000")
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertTrue(self.pids(), "the running app was stopped for a refused DMG")

    def test_install_refuses_an_app_gatekeeper_rejects(self):
        self.install_old()
        before = self.pids()
        with self.assertRaisesRegex(base.MachineError, "spctl refused"):
            self.install(SHA_NEW, marks=("UNNOTARIZED",))
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertEqual(self.pids(), before)  # not even stopped

    def test_a_failed_copy_leaves_the_running_app_alone(self):
        self.install_old()
        before = self.pids()
        with self.assertRaisesRegex(base.MachineError, "ditto into /Applications failed"):
            self.install(SHA_NEW, marks=("DITTOFAIL",))
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertEqual(self.pids(), before)  # same process: never stopped
        self.assertFalse(os.path.exists(self.app + ".incoming"))  # no half copy left behind

    def test_a_failure_after_the_stop_relaunches_what_is_installed(self):
        # The swap's first rename fails, after the copy is staged and the app
        # stopped: the one point where a failure could leave nothing running.
        self.install_old()
        self.cache_build(SHA_NEW)
        real = self.sh

        def sh(argv, timeout=None, input=None):
            if argv[:2] == ["/bin/bash", "-c"] and "hdiutil" in argv[2]:
                argv = ["/bin/bash", "-c", argv[2].replace(
                    'mv "$APP" "$APP.outgoing" ||', 'false ||')]
            return real(argv, timeout, input)
        m = mac.MacMachine("mini", {"state_dir": self.state}, sh=sh)
        with self.assertRaisesRegex(base.MachineError, "could not move the installed app"):
            m.install(12, SHA_NEW, self.log)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertEqual(len(self.pids()), 1, "the old app was left stopped")
        self.assertIn("open -a " + self.app, self.stub_log())

    def test_a_failed_install_deletes_the_e2e_branch(self):
        self.install_old()
        with self.assertRaises(base.MachineError):
            self.install(SHA_NEW, team="ADHOC0000")
        self.assertEqual(self.gh_calls[-1][-1],
                         "repos/Uplift-Foundation/Orbweaver/git/refs/heads/e2e/bugfix-pr-12")
        self.assertEqual(self.gh_calls[-1][1:4], ["api", "-X", "DELETE"])

    def test_install_refuses_a_bad_signature(self):
        self.install_old()
        with self.assertRaisesRegex(base.MachineError, "codesign"):
            self.install(SHA_NEW, badsig=True)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)

    def test_install_refuses_a_dmg_of_another_commit(self):
        self.install_old()
        with self.assertRaisesRegex(base.MachineError, "stamped"):
            self.install(SHA_NEW, stamp=SHA_OTHER)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)

    def test_second_install_in_a_hold_keeps_the_first_aside(self):
        self.install_old()
        self.install(SHA_NEW)
        self.install(SHA_OTHER)
        self.assertEqual(open(os.path.join(self.aside, "previous.commit")).read().strip(), SHA_OLD)
        self.m.restore(self.log)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)

    def test_restore_puts_back_the_previous_build_clears_aside_and_deletes_the_branch(self):
        self.install_old()
        self.install(SHA_NEW)
        self.m.restore(self.log)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertTrue(self.pids())
        self.assertFalse(os.path.exists(self.aside))
        self.assertEqual(self.gh_calls[-1][-1],
                         "repos/Uplift-Foundation/Orbweaver/git/refs/heads/e2e/bugfix-pr-12")
        # a second restore has nothing aside and changes nothing
        self.m.restore(self.log)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertIn("mini: nothing kept aside, nothing to restore", self.lines)

    def test_restore_puts_back_a_signed_but_unnotarized_build(self):
        # Measured 2026-11-25: the build on both Macs (1.0.12) is Developer ID
        # signed but spctl rejects it as unnotarized. Gatekeeper's verdict is
        # for what comes in, never for what goes back, or no restore of those
        # machines could succeed.
        self.make_bundle(self.app, SHA_OLD, marks=("UNNOTARIZED",))
        self.install(SHA_NEW)
        self.m.restore(self.log)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertEqual(len(self.pids()), 1)

    def test_restore_refuses_when_the_kept_bundle_is_gone(self):
        self.install_old()
        self.install(SHA_NEW)
        shutil.rmtree(os.path.join(self.aside, "Orbweaver.app"))
        with self.assertRaisesRegex(base.MachineError, "no bundle is kept aside"):
            self.m.restore(self.log)
        self.assertEqual(self.m.installed_commit(), SHA_NEW)  # left as it was, not half-restored
        self.assertTrue(os.path.exists(os.path.join(self.aside, "previous.commit")))  # retry still possible

    def test_restore_of_a_machine_that_had_no_app_removes_it(self):
        self.install(SHA_NEW)
        self.assertEqual(self.m.installed_commit(), SHA_NEW)
        self.m.restore(self.log)
        self.assertFalse(os.path.exists(self.app))
        self.assertFalse(self.pids())
        self.assertIsNone(self.m.installed_commit())

    def test_keepalive_is_unloaded_before_the_stop_and_loaded_after(self):
        self.install_old()
        os.makedirs(os.path.join(self.home, "Library/LaunchAgents"))
        open(os.path.join(self.home, "Library/LaunchAgents", mac.BUNDLE_ID + ".plist"), "w").close()
        open(self.keepalive_flag, "w").close()
        self.install(SHA_NEW)
        log = self.stub_log()
        self.assertLess(log.index("unload"), log.index("load " + self.home))
        self.assertNotIn("open ", log)  # launchd started it; no second copy by hand
        self.assertEqual(len(self.pids()), 1)

    def test_without_keepalive_it_relaunches_through_open(self):
        self.install_old()
        self.install(SHA_NEW)
        log = self.stub_log()
        self.assertIn("open -a " + self.app, log)
        self.assertNotIn("load ", log.replace("unload", ""))
        self.assertEqual(len(self.pids()), 1)  # exactly one instance

    def test_install_refuses_bad_args_before_touching_anything(self):
        self.install_old()
        with self.assertRaises(base.MachineError):
            self.m.install(12, "nope", self.log)
        self.assertEqual(self.m.installed_commit(), SHA_OLD)
        self.assertEqual(self.gh_calls, [])


class RemoteTimeout(unittest.TestCase):
    """The MacBook is Warren's own machine: a command past its limit must die
    there, not only lose its ssh session (lead's ruling). The kill itself is
    proved for real in RunCommand; this pins that the ssh route sends the
    command inside that same wrapper rather than bare."""

    def test_macbook_command_goes_out_inside_the_far_side_timeout(self):
        sent = []
        m = mac.MacMachine("macbook", {}, sh=lambda a, timeout=None, input=None: (sent.append((a, timeout)), done(a))[1])
        m.run("make test", 42)
        argv, backstop = sent[0]
        remote = argv[-1]
        self.assertTrue(remote.startswith("/bin/bash -c "))
        self.assertIn("setpgrp", remote)
        self.assertIn('kill "KILL", -$pid', remote)
        self.assertIn(" 42 /bin/bash -c ", remote)
        self.assertIn("make test", remote)
        self.assertGreater(backstop, 42)  # ours fires only if the Mac never answers
        self.assertEqual(argv[0], "ssh")


# ---------------------------------------------------------------- signed build

class FakeGitHub:
    """A machine `sh` that answers `gh` argv like GitHub would, and xcrun
    stapler by file name. Records every call."""

    def __init__(self, sha=SHA_NEW, ref_exists=False, jobs=None, pending_after=1,
                 complete_after=3, run_created="2099-01-01T00:00:00Z", dmgs=1, delete=(0, "")):
        self.calls = []
        self.sha, self.ref_exists, self.dmgs, self.delete = sha, ref_exists, dmgs, delete
        self.jobs = jobs if jobs is not None else [{"name": "macos (osx-arm64)", "conclusion": "success"},
                                                   {"name": "windows", "conclusion": "failure"}]
        self.pending_after, self.complete_after = pending_after, complete_after
        self.run_created = run_created
        self.pending_reads = self.state_reads = 0
        self.approved = None
        self.dmg_name = "Orbweaver-1.0.13-osx-arm64.dmg"

    def __call__(self, argv, timeout=None, input=None):
        self.calls.append(list(argv))
        if argv[:3] == ["xcrun", "stapler", "validate"]:
            return done(argv, 1, "", "does not have a ticket stapled") if "BAD" in argv[3] else done(argv, 0, "ok")
        assert argv[0] == "gh", argv
        args = argv[1:]
        a = " ".join(args)
        if a.startswith("api -X POST repos/Uplift-Foundation/Orbweaver/git/refs"):
            return done(argv, 1, "", "gh: Reference already exists (HTTP 422)") if self.ref_exists else done(argv, 0, "{}")
        if a.startswith("api -X PATCH"):
            return done(argv, 0, "{}")
        if a.startswith("workflow run release.yml"):
            return done(argv)
        if "/actions/workflows/release.yml/runs" in a:
            return done(argv, 0, json.dumps({"workflow_runs": [
                {"id": 99, "head_sha": self.sha, "event": "workflow_dispatch", "created_at": self.run_created}]}))
        if a.startswith("api -X POST") and "pending_deployments" in a:
            self.approved = args
            return done(argv, 0, "[]")
        if "pending_deployments" in a:
            self.pending_reads += 1
            if self.pending_reads >= self.pending_after and self.approved is None:
                return done(argv, 0, json.dumps([
                    {"environment": {"id": 555, "name": "release"}, "current_user_can_approve": True},
                    {"environment": {"id": 666, "name": "other"}, "current_user_can_approve": True}]))
            return done(argv, 0, "[]")
        if a.endswith("/jobs"):
            return done(argv, 0, json.dumps({"jobs": self.jobs}))
        if a.startswith("api repos/Uplift-Foundation/Orbweaver/actions/runs/99"):
            self.state_reads += 1
            status = "completed" if self.state_reads >= self.complete_after else "in_progress"
            return done(argv, 0, json.dumps({"status": status}))
        if a.startswith("run download 99"):
            d = args[args.index("-D") + 1]
            for i in range(self.dmgs):
                open(os.path.join(d, self.dmg_name if i == 0 else f"extra{i}.dmg"), "w").close()
            open(os.path.join(d, "OrbweaverSpeech-1.0.13-osx-arm64.zip"), "w").close()
            return done(argv)
        if a.startswith("api -X DELETE"):
            return done(argv, self.delete[0], "", self.delete[1])
        return done(argv, 1, "", f"unexpected gh call: {a}")

    def gh(self):
        return [c[1:] for c in self.calls if c[0] == "gh"]


class Clock:
    def __init__(self):
        self.t = 1_800_000_000.0

    def __call__(self):
        return self.t

    def sleep(self, s):
        self.t += s


class SignedBuild(unittest.TestCase):
    def setUp(self):
        self.state = tempfile.mkdtemp(prefix="ow-mac-build-")
        self.addCleanup(shutil.rmtree, self.state, True)
        self.lines = []
        self.clock = Clock()

    def machine(self, fake, name="mini"):
        return mac.MacMachine(name, {"state_dir": self.state}, sh=fake, clock=self.clock, sleep=self.clock.sleep)

    def build(self, fake, name="mini"):
        return self.machine(fake, name).signed_build(12, SHA_NEW, self.lines.append)

    def test_happy_path_pushes_dispatches_approves_release_only_and_downloads(self):
        fake = FakeGitHub()
        dmg = self.build(fake)
        self.assertEqual(dmg, os.path.join(self.state, "mac-build", SHA_NEW, fake.dmg_name))
        gh = fake.gh()
        self.assertIn("ref=refs/heads/e2e/bugfix-pr-12", gh[0])
        self.assertIn(f"sha={SHA_NEW}", gh[0])
        self.assertEqual(gh[1][-2:], ["--ref", "e2e/bugfix-pr-12"])
        self.assertIn("environment_ids[]=555", fake.approved)
        self.assertNotIn("environment_ids[]=666", fake.approved)
        self.assertTrue(any(c[:2] == ["run", "download"] and "dmg-osx-arm64" in c for c in gh))
        self.assertIn(["xcrun", "stapler", "validate", dmg], fake.calls)
        rec = json.load(open(os.path.join(self.state, "mac-build", "build.json")))
        self.assertEqual((rec["pr"], rec["sha"], rec["run"]), (12, SHA_NEW, 99))

    def test_one_build_serves_both_macs(self):
        self.build(FakeGitHub(), "mini")
        fake = FakeGitHub()
        self.build(fake, "macbook")
        self.assertEqual(fake.calls, [])

    def test_a_build_of_another_sha_is_not_reused_and_is_replaced(self):
        m = self.machine(FakeGitHub(sha=SHA_OTHER))
        m.signed_build(12, SHA_OTHER, self.lines.append)
        fake = FakeGitHub()
        self.build(fake)
        self.assertTrue(fake.gh())
        self.assertEqual(os.listdir(os.path.join(self.state, "mac-build")).count(SHA_OTHER), 0)

    def test_existing_branch_is_moved_not_refused(self):
        fake = FakeGitHub(ref_exists=True)
        self.build(fake)
        patch = [c for c in fake.gh() if c[:3] == ["api", "-X", "PATCH"]]
        self.assertEqual(len(patch), 1)
        self.assertIn("refs/heads/e2e/bugfix-pr-12", patch[0][3])
        self.assertIn(f"sha={SHA_NEW}", patch[0])

    def test_an_old_run_of_the_same_sha_is_not_taken(self):
        fake = FakeGitHub(run_created="2001-01-01T00:00:00Z")
        with self.assertRaisesRegex(base.MachineError, r"did not finish .*no run found"):
            self.build(fake)
        self.assertFalse(any(c[:2] == ["run", "download"] for c in fake.gh()))

    def test_failed_mac_leg_refuses(self):
        with self.assertRaisesRegex(base.MachineError, "failure"):
            self.build(FakeGitHub(jobs=[{"name": "macos (osx-arm64)", "conclusion": "failure"}]))

    def test_timeout_names_the_run(self):
        with self.assertRaisesRegex(base.MachineError, "run 99"):
            self.build(FakeGitHub(complete_after=10_000))

    def test_download_must_hold_exactly_one_dmg(self):
        with self.assertRaisesRegex(base.MachineError, "holds 0 DMGs"):
            self.build(FakeGitHub(dmgs=0))
        with self.assertRaisesRegex(base.MachineError, "holds 2 DMGs"):
            self.build(FakeGitHub(dmgs=2))

    def test_unstapled_dmg_refuses_and_is_not_cached(self):
        fake = FakeGitHub()
        fake.dmg_name = "BAD.dmg"
        with self.assertRaisesRegex(base.MachineError, "not notarized"):
            self.build(fake)
        self.assertFalse(os.path.exists(os.path.join(self.state, "mac-build", "build.json")))

    def test_push_refused_for_another_reason_stops(self):
        def sh(argv, timeout=None, input=None):
            return done(argv, 1, "", "HTTP 403: Resource not accessible")
        with self.assertRaisesRegex(base.MachineError, "could not push"):
            self.build(sh)

    def test_bad_args_refused_before_any_gh_call(self):
        fake = FakeGitHub()
        with self.assertRaises(base.MachineError):
            self.machine(fake).signed_build(12, "HEAD", self.lines.append)
        self.assertEqual(fake.calls, [])


class ReleaseBuild(unittest.TestCase):
    def setUp(self):
        self.state = tempfile.mkdtemp(prefix="ow-mac-rel-")
        self.addCleanup(shutil.rmtree, self.state, True)
        os.makedirs(os.path.join(self.state, "mac-build"))
        with open(os.path.join(self.state, "mac-build", "build.json"), "w") as f:
            json.dump({"pr": 7, "sha": SHA_NEW, "run": 1, "dmg": "/x"}, f)
        self.lines = []

    def release(self, delete):
        fake = FakeGitHub(delete=delete)
        mac.MacMachine("mini", {"state_dir": self.state}, sh=fake).release_build(self.lines.append)
        return fake

    def test_deletes_only_the_recorded_e2e_ref(self):
        fake = self.release((0, ""))
        self.assertEqual(fake.gh()[0][-1], "repos/Uplift-Foundation/Orbweaver/git/refs/heads/e2e/bugfix-pr-7")
        self.assertIn("deleted e2e/bugfix-pr-7", self.lines)

    def test_already_gone_is_fine(self):
        self.release((1, "gh: Reference does not exist (HTTP 422)"))
        self.assertIn("e2e/bugfix-pr-7 was already gone", self.lines)

    def test_other_failures_are_logged_not_raised(self):
        self.release((1, "HTTP 403"))
        self.assertTrue(any(line.startswith("could not delete e2e/bugfix-pr-7") for line in self.lines))

    def test_no_record_no_call(self):
        os.unlink(os.path.join(self.state, "mac-build", "build.json"))
        self.assertEqual(self.release((0, "")).calls, [])


class Wiring(unittest.TestCase):
    def test_runner_builds_both_macs_with_the_contract_names(self):
        ms = runner.make_machines({"machines": {"mini": {}, "macbook": {}}})
        self.assertEqual({n: (type(m).__name__, m.name) for n, m in ms.items()},
                         {"mini": ("MacMachine", "mini"), "macbook": ("MacMachine", "macbook")})
        self.assertTrue(ms["mini"].local)
        self.assertFalse(ms["macbook"].local)

    def test_only_macs(self):
        with self.assertRaises(ValueError):
            mac.MacMachine("windows", {})

    def test_release_yml_still_publishes_on_tags_only(self):
        # The route dispatches release.yml on an e2e/ branch and relies on
        # this gate to publish nothing; if it moves, this route must change.
        with open(os.path.join(ROOT, ".github/workflows/release.yml")) as f:
            text = f.read()
        pub = text[text.index("\n  publish:"):]
        self.assertIn("if: startsWith(github.ref, 'refs/tags/v')", pub[:800])
        self.assertEqual(mac.ARTIFACT, "dmg-osx-arm64")
        self.assertIn("name: dmg-${{ matrix.rid }}", text)
        self.assertIn("rid: [osx-arm64, osx-x64]", text)


if __name__ == "__main__":
    unittest.main()
