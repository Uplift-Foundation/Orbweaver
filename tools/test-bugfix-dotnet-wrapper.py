#!/usr/bin/env python3
"""Tests for the bug-fix org's `dotnet` stand-in (CB-494).

    python3 tools/test-bugfix-dotnet-wrapper.py

Every test drives a fake dotnet through ORBWEAVER_REAL_DOTNET, never the real
SDK. The fake appends "start"/"end" lines with timestamps to a log, so overlap
is read off the log rather than guessed from wall clock. Each ordering claim
has a control that shows the same setup can see the opposite. Where
/usr/bin/lockf is absent (the Windows leg) it prints "SKIP: lockf absent" and
exits 0; on macOS a missing lockf is a failure, so the macOS leg can never go
green having tested nothing. The shim only ever runs on a Mac.
"""
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SHIM = os.path.realpath(os.path.join(HERE, "..", "paperclip", "bugfix-org", "host", "shims", "dotnet"))
LOCKF = "/usr/bin/lockf"

FAKE = r"""#!/bin/bash
echo "start $FAKE_TAG $(python3 -c 'import time;print(time.time())')" >> "$FAKE_LOG"
echo "held=${ORBWEAVER_HEAVY_LOCK_HELD-UNSET}"
echo "nice=$(ps -o nice= -p $$ | tr -d ' ')"
echo "argc=$#"
for a in "$@"; do echo "arg=$a"; done
echo "to-stderr" >&2
if [ -n "$FAKE_NEST" ]; then n="$FAKE_NEST"; FAKE_NEST= FAKE_TAG=inner "$n" test nested.csproj >/dev/null || exit 9; fi
sleep "${FAKE_SLEEP:-0}"
echo "end $FAKE_TAG $(python3 -c 'import time;print(time.time())')" >> "$FAKE_LOG"
exit ${FAKE_EXIT:-0}
"""


class DotnetShim(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.fake = os.path.join(self.tmp, "fake-dotnet")
        with open(self.fake, "w") as f:
            f.write(FAKE)
        os.chmod(self.fake, 0o755)
        self.log = os.path.join(self.tmp, "log")
        open(self.log, "w").close()
        self.env = {"PATH": "/usr/bin:/bin", "HOME": self.tmp,
                    "ORBWEAVER_REAL_DOTNET": self.fake,
                    "ORBWEAVER_HEAVY_LOCK": os.path.join(self.tmp, "heavy.lock"),
                    "FAKE_LOG": self.log}

    def env_with(self, **kw):
        env = dict(self.env)
        for k, v in kw.items():
            if v is None:
                env.pop(k, None)
            else:
                env[k] = v
        return env

    def start(self, args, **kw):
        p = subprocess.Popen([SHIM, *args], env=self.env_with(**kw), text=True,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             stdin=subprocess.DEVNULL, start_new_session=True)
        self.addCleanup(self.kill, p)
        return p

    def kill(self, p):
        if p.poll() is None:
            try:
                os.killpg(p.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            p.wait()
        p.stdout.close()
        p.stderr.close()

    def run_shim(self, args, **kw):
        p = self.start(args, **kw)
        out, err = p.communicate(timeout=20)
        return p.returncode, out, err

    def seen(self, out, key):
        return [l.split("=", 1)[1] for l in out.splitlines() if l.startswith(key + "=")]

    def events(self):
        ev = {}
        with open(self.log) as f:
            for line in f:
                kind, tag, t = line.split()
                ev.setdefault(tag, {})[kind] = float(t)
        return ev

    def wait_started(self, tag, timeout=10):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if tag in self.events():
                return
            time.sleep(0.05)
        self.fail(f"{tag} never started")

    def overlapped(self, a, b):
        ev = self.events()
        return ev[a]["start"] < ev[b]["end"] and ev[b]["start"] < ev[a]["end"]

    # --- one at a time --------------------------------------------------

    def test_two_concurrent_heavy_calls_never_overlap(self):
        p1 = self.start(["build"], FAKE_TAG="a", FAKE_SLEEP="1.5")
        self.wait_started("a")
        p2 = self.start(["test"], FAKE_TAG="b", FAKE_SLEEP="0.2")
        p1.communicate(timeout=20)
        p2.communicate(timeout=20)
        self.assertFalse(self.overlapped("a", "b"))
        ev = self.events()
        self.assertGreaterEqual(ev["b"]["start"], ev["a"]["end"])

    def test_control_same_pair_overlaps_without_the_lock(self):
        # Proves the log can see an overlap: the held variable skips the lock.
        p1 = self.start(["build"], FAKE_TAG="a", FAKE_SLEEP="1.5", ORBWEAVER_HEAVY_LOCK_HELD="1")
        self.wait_started("a")
        p2 = self.start(["test"], FAKE_TAG="b", FAKE_SLEEP="0.2", ORBWEAVER_HEAVY_LOCK_HELD="1")
        p1.communicate(timeout=20)
        p2.communicate(timeout=20)
        self.assertTrue(self.overlapped("a", "b"))

    def installed(self):
        """A copy at <root>/tools/bin/dotnet, as deployed, with the lock
        folder it defaults to; no ORBWEAVER_HEAVY_LOCK, so the default is
        what is tested."""
        root = os.path.join(self.tmp, "paperclip")
        bindir = os.path.join(root, "tools", "bin")
        os.makedirs(bindir)
        os.makedirs(os.path.join(root, "instances", "default"))
        shutil.copy(SHIM, os.path.join(bindir, "dotnet"))
        return os.path.join(bindir, "dotnet"), os.path.join(root, "instances", "default", "heavy-dotnet.lock")

    def start_at(self, exe, **kw):
        p = subprocess.Popen([exe, "build"], env=self.env_with(**kw), text=True,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             stdin=subprocess.DEVNULL, start_new_session=True)
        self.addCleanup(self.kill, p)
        return p

    def homes(self):
        out = []
        for n in ("run-a", "run-b"):
            h = os.path.join(self.tmp, n, "home")
            os.makedirs(os.path.join(h, ".paperclip", "instances", "default"))
            out.append(h)
        return out

    def test_two_runs_with_different_homes_share_the_installed_lock(self):
        exe, lock = self.installed()
        ha, hb = self.homes()
        p1 = self.start_at(exe, HOME=ha, ORBWEAVER_HEAVY_LOCK=None, FAKE_TAG="a", FAKE_SLEEP="1.5")
        self.wait_started("a")
        p2 = self.start_at(exe, HOME=hb, ORBWEAVER_HEAVY_LOCK=None, FAKE_TAG="b", FAKE_SLEEP="0.2")
        p1.communicate(timeout=20)
        p2.communicate(timeout=20)
        self.assertFalse(self.overlapped("a", "b"))
        self.assertTrue(os.path.exists(lock))
        for h in (ha, hb):
            self.assertEqual(os.listdir(os.path.join(h, ".paperclip", "instances", "default")), [])

    def test_control_a_home_based_lock_lets_different_homes_overlap(self):
        # The pre-fix default, $HOME/.paperclip/instances/default/..., given
        # explicitly per run: each run gets a private lock and they overlap.
        exe, _ = self.installed()
        ha, hb = self.homes()
        old = lambda h: os.path.join(h, ".paperclip", "instances", "default", "heavy-dotnet.lock")
        p1 = self.start_at(exe, HOME=ha, ORBWEAVER_HEAVY_LOCK=old(ha), FAKE_TAG="a", FAKE_SLEEP="1.5")
        self.wait_started("a")
        p2 = self.start_at(exe, HOME=hb, ORBWEAVER_HEAVY_LOCK=old(hb), FAKE_TAG="b", FAKE_SLEEP="0.2")
        p1.communicate(timeout=20)
        p2.communicate(timeout=20)
        self.assertTrue(self.overlapped("a", "b"))

    def test_missing_lock_folder_says_so_and_runs_unlocked(self):
        code, out, err = self.run_shim(["build"], FAKE_TAG="v", FAKE_EXIT="4",
                                       ORBWEAVER_HEAVY_LOCK=os.path.join(self.tmp, "no", "such", "x.lock"))
        self.assertEqual(code, 4)
        self.assertEqual(self.seen(out, "held"), ["UNSET"])
        self.assertEqual(err.splitlines(), [err.splitlines()[0], "to-stderr"])
        self.assertIn("lock folder", err.splitlines()[0])

    def test_light_call_does_not_wait_behind_a_heavy_one(self):
        p1 = self.start(["build"], FAKE_TAG="heavy", FAKE_SLEEP="3")
        self.wait_started("heavy")
        t = time.time()
        code, out, _ = self.run_shim(["--version"], FAKE_TAG="light")
        self.assertEqual(code, 0)
        self.assertLess(time.time() - t, 2)
        self.assertIsNone(p1.poll(), "heavy finished first, so this proved nothing")
        p1.communicate(timeout=20)
        self.assertTrue(self.overlapped("heavy", "light"))

    def test_control_heavy_call_does_wait_behind_a_heavy_one(self):
        p1 = self.start(["build"], FAKE_TAG="heavy", FAKE_SLEEP="2")
        self.wait_started("heavy")
        self.run_shim(["test"], FAKE_TAG="second")
        self.assertIsNotNone(p1.poll())
        self.assertFalse(self.overlapped("heavy", "second"))

    def test_nested_heavy_call_does_not_deadlock(self):
        code, out, err = self.run_shim(["build"], FAKE_TAG="outer", FAKE_NEST=SHIM)
        self.assertEqual(code, 0, err)
        self.assertIn("outer", self.events())

    def test_control_nested_call_without_the_variable_deadlocks(self):
        # A nested call that lost the variable waits on its own parent forever.
        wrapper = os.path.join(self.tmp, "strip-held")
        with open(wrapper, "w") as f:
            f.write(f'#!/bin/bash\nunset ORBWEAVER_HEAVY_LOCK_HELD\nexec "{SHIM}" "$@"\n')
        os.chmod(wrapper, 0o755)
        p = self.start(["build"], FAKE_TAG="outer", FAKE_NEST=wrapper)
        with self.assertRaises(subprocess.TimeoutExpired):
            p.communicate(timeout=3)

    # --- which commands lock --------------------------------------------

    def held(self, args):
        code, out, err = self.run_shim(args, FAKE_TAG="v")
        self.assertEqual(code, 0, err)
        return self.seen(out, "held")[0] == "1"

    def test_heavy_verbs_lock(self):
        for args in (["test"], ["build"], ["run"], ["publish"], ["pack"], ["msbuild"],
                     ["restore"], ["vstest"], ["clean"], ["format"], ["watch"],
                     ["app.cs"], ["App.csproj"], ["All.sln"], ["All.slnx"],
                     ["-d", "build"], ["--roll-forward", "Major", "test"],
                     ["--fx-version", "10.0.0", "build", "x"], ["--diagnostics", "test", "-c", "Release"]):
            with self.subTest(args=args):
                self.assertTrue(self.held(args))

    def test_light_commands_pass_through_untouched(self):
        for args in (["--version"], ["--info"], ["--list-sdks"], ["nuget", "locals", "all", "--list"],
                     ["tool", "list"], [], ["--roll-forward", "test"]):
            with self.subTest(args=args):
                code, out, err = self.run_shim(args, FAKE_TAG="v")
                self.assertEqual(self.seen(out, "held"), ["UNSET"])
                self.assertEqual(self.seen(out, "arg"), args)
                self.assertEqual(err, "to-stderr\n")

    def test_heavy_call_runs_at_lower_priority(self):
        base = os.getpriority(os.PRIO_PROCESS, 0)
        _, out, _ = self.run_shim(["build"], FAKE_TAG="v")
        self.assertEqual(int(self.seen(out, "nice")[0]), min(base + 10, 20))
        _, out, _ = self.run_shim(["--info"], FAKE_TAG="w")
        self.assertEqual(int(self.seen(out, "nice")[0]), base)

    # --- the real command's output --------------------------------------

    def test_exit_code_args_stdout_and_stderr_preserved(self):
        for verb in ("build", "--info"):
            with self.subTest(verb=verb):
                code, out, err = self.run_shim([verb, "a b", "", "-c"], FAKE_TAG="v", FAKE_EXIT="7")
                self.assertEqual(code, 7)
                self.assertEqual(self.seen(out, "argc"), ["4"])
                self.assertEqual(self.seen(out, "arg"), [verb, "a b", "", "-c"])
                self.assertEqual(err, "to-stderr\n")

    def test_output_streams_before_the_command_exits(self):
        p = self.start(["build"], FAKE_TAG="v", FAKE_SLEEP="5")
        line = p.stdout.readline()
        self.assertTrue(line.startswith("held="))
        self.assertIsNone(p.poll(), "the line arrived only at exit")

    def test_missing_lockf_says_so_once_and_runs_unlocked(self):
        code, out, err = self.run_shim(["build"], FAKE_TAG="v", FAKE_EXIT="3",
                                       ORBWEAVER_LOCKF=os.path.join(self.tmp, "no-lockf"))
        self.assertEqual(code, 3)
        self.assertEqual(self.seen(out, "held"), ["UNSET"])
        lines = err.splitlines()
        self.assertEqual(len(lines), 2, err)
        self.assertIn("running unlocked", lines[0])
        self.assertEqual(lines[1], "to-stderr")

    def test_missing_real_dotnet_fails_loudly(self):
        for args in (["build"], ["--version"]):
            for real in (os.path.join(self.tmp, "nope"), self.log, self.tmp):
                with self.subTest(args=args, real=real):
                    code, out, err = self.run_shim(args, ORBWEAVER_REAL_DOTNET=real)
                    self.assertEqual(code, 127)
                    self.assertEqual(out, "")
                    self.assertIn("real dotnet not found", err)

    def test_add_package_and_new_stay_light(self):
        for args in (["add", "x.csproj", "package", "Foo"], ["new", "console"]):
            with self.subTest(args=args):
                _, out, _ = self.run_shim(args, FAKE_TAG="v")
                self.assertEqual(self.seen(out, "held"), ["UNSET"])

    def test_control_light_call_with_missing_lockf_says_nothing(self):
        _, _, err = self.run_shim(["--version"], FAKE_TAG="v",
                                  ORBWEAVER_LOCKF=os.path.join(self.tmp, "no-lockf"))
        self.assertEqual(err, "to-stderr\n")


if __name__ == "__main__":
    if not os.access(LOCKF, os.X_OK):
        if sys.platform == "darwin":
            sys.exit(f"FAIL: {LOCKF} absent on macOS; nothing would be tested")
        print("SKIP: lockf absent")
        sys.exit(0)
    unittest.main(verbosity=2)
