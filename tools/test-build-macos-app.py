#!/usr/bin/env python3
"""Runs the tail of tools/build-macos-app.sh's --install block as it ships.

That tail reports how many installed Buddies are running afterwards — none
("launch it with"), one ("Running: pid N"), or more (a warning). It used to
exit 1 instead, straight after a successful install, whenever no Buddy was
running: `running_installed` failed inside `$(...)` under `set -euo pipefail`
(pgrep exits 1 when it finds nothing), so the assignment ended the script
before the report. Found installing develop at 0e09981d; fixed under CB-245.

The text from `running_installed() {` to the `esac` that ends the report is
cut out of the script and run under the script's own shell options, with
stub `pgrep` and `ps` on PATH so the number of running copies is chosen by
the test rather than by whatever happens to be running on the machine — a
developer's Mac usually has a real Buddy up. Nothing is ever launched: `open`
is a stub too.

CB-256 renamed the executable, so three paths are in the wild (INSTALLED,
INTERIM and LEGACY below) under two process names. The stub `pgrep` therefore
answers `-x NAME` by basename, as the real one does: a stub that ignored its
argument would make a script asking for one name look the same as one asking
for both. Cases passing `with_stop` also run the step before the install
(WAS_RUNNING, the keep-alive unload and the stop-installed-buddy.sh call)
against a stub stop script, so "a phase-2 copy is stopped" is asserted on
what the script actually asked to stop.

Set BUILD_SCRIPT_UNDER_TEST to run it against another copy of the script.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.environ.get("BUILD_SCRIPT_UNDER_TEST", os.path.join(HERE, "build-macos-app.sh"))
# The three executable paths of CB-256 §2, C/B/A in the design's table.
INSTALLED = "/Applications/Orbweaver.app/Contents/MacOS/Orbweaver"  # C: this build
# B: the phase-2 Orbweaver.app, which still ran the pre-rename executable. It
# is the bundle --install replaces, so nothing about the folder says it is old.
INTERIM = "/Applications/Orbweaver.app/Contents/MacOS/ClaudeBuddy"
# A: the pre-CB-255 bundle. --install stops and removes it; a copy still
# running out of it afterwards is not the new binary and must not be reported
# as one.
LEGACY = "/Applications/Claude Buddy.app/Contents/MacOS/ClaudeBuddy"
# The pid a relaunch through the stub `open` shows up as.
RELAUNCHED = 5151


def function(text, name):
    """One of the script's helper functions, as written: a one-liner
    (`  name() { ...; }`) or a block closed by `  }` on its own line."""
    m = re.search(r"^  %s\(\) \{(?: [^\n]*\}$|\n.*?^  \}$)" % name, text, re.S | re.M)
    return None if m is None else m.group(0)


def stop_step(text):
    """The step before the install proper: WAS_RUNNING, the keep-alive unload
    and the stop-installed-buddy.sh call, up to the `fi` closing the
    "Stopped the running" report. Ends before the rm -rf of /Applications,
    which must never run here."""
    m = re.search(r"^  WAS_RUNNING=.*?^  STOPPED=.*?^  fi$", text, re.S | re.M)
    if m is None:
        raise AssertionError("build-macos-app.sh has no WAS_RUNNING/stop step")
    if "rm -rf" in m.group(0):
        raise AssertionError("the stop step extracted reaches the install's rm -rf")
    return m.group(0)


def tail(text):
    """The script's install tail, as it would run: the helper functions it
    calls, wherever the script defines them, then the text from the CB-206
    "one Buddy" comment to the esac closing the 0/1/many report."""
    m = re.search(r"^  # CB-206: one Buddy, running the new binary.*?^  esac$", text, re.S | re.M)
    if m is None:
        raise AssertionError("build-macos-app.sh has no CB-206 install tail")
    body = m.group(0)
    if function(text, "running_installed") is None:
        raise AssertionError("build-macos-app.sh has no running_installed")
    prelude = ""
    # running_from is what running_installed calls since CB-255; optional so
    # this still runs against a copy of the script from before it.
    for name in ("running_from", "running_installed", "keepalive_loaded"):
        f = function(text, name)
        if f is not None and f not in body:
            prelude += f + "\n"
    return prelude + body


def bash():
    if os.name == "nt":
        for candidate in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(candidate):
                return candidate
    found = shutil.which("bash")
    if found is None:
        raise unittest.SkipTest("no bash on this machine")
    return found


with open(SCRIPT, encoding="utf-8") as f:
    TEXT = f.read()
TAIL = tail(TEXT)
STOP = stop_step(TEXT)


class InstallTail(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="cb-install-tail-")
        self.bin = os.path.join(self.tmp, "bin")
        os.makedirs(self.bin)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def posix(self, path):
        return path.replace("\\", "/")

    def bash_path(self, path):
        """A directory as bash's PATH needs it. Git Bash reads `C:/x` fine as a
        file path but PATH is colon-separated, so `C:/x` there is the two
        entries `C` and `/x` and nothing on it is found — which is how the
        Windows leg first reported `pgrep: command not found` for every stub."""
        p = self.posix(path)
        if re.match(r"^[A-Za-z]:/", p):
            p = "/" + p[0].lower() + p[2:]
        return p

    def stub(self, name, body):
        """A stand-in command that first records that it ran. The record is
        what lets run_tail prove its stubs were the ones found: on the first
        Windows CI run none of them was on PATH, every call was `command not
        found`, and one case passed anyway because the script swallows a
        failed pgrep by design."""
        path = os.path.join(self.bin, name)
        calls = self.posix(os.path.join(self.tmp, "stub-calls"))
        with open(path, "w", newline="\n") as f:
            f.write('#!/bin/sh\necho %s >> "%s"\n' % (name, calls) + body)
        os.chmod(path, 0o755)

    def stub_calls(self):
        try:
            with open(os.path.join(self.tmp, "stub-calls")) as f:
                return f.read().split()
        except FileNotFoundError:
            return []

    # The stubs' shell, with @TMP@ and friends filled in per case. One process
    # table, "pid|exe|first call it is visible on" per line, shared by every
    # stub so that stopping a copy is visible to each scan after it.
    PGREP = r'''name=""
while [ $# -gt 0 ]; do [ "$1" = "-x" ] && name="$2"; shift; done
echo "$name" >> "@TMP@/pgrep-names"
f="@TMP@/pgrep-calls-$name"
n=$(( $(cat "$f" 2>/dev/null || echo 0) + 1 )); echo $n > "$f"
out=$(awk -F'|' -v want="$name" -v n="$n" '{ k = split($2, p, "/"); if (p[k] == want && n >= $3) print $1 }' "@TMP@/procs")
[ -n "$out" ] || exit 1
echo "$out"
'''
    PS = r'''pid=""
while [ $# -gt 0 ]; do [ "$1" = "-p" ] && pid="$2"; shift; done
out=$(awk -F'|' -v pid="$pid" '$1 == pid { print $2 }' "@TMP@/procs")
[ -n "$out" ] || exit 1
echo "$out"
'''
    # `unload` takes the keep-alive's own copy down with it, as launchd does.
    LAUNCHCTL = r'''if [ "$1" = "unload" ]; then
  awk -F'|' -v owned=" @OWNED@ " 'index(owned, " " $1 " ") == 0' "@TMP@/procs" > "@TMP@/procs.new"
  mv "@TMP@/procs.new" "@TMP@/procs"
  exit 0
fi
exit @LIST@
'''
    # Records what it was asked to stop, prints the pids running from those
    # paths, and takes them out of the table.
    STOPPER = r'''#!/bin/sh
printf '%s\n' "$@" > "@TMP@/stop-args"
STOP_EXES="$(printf '%s\n' "$@")" awk -F'|' -v rest="@TMP@/procs.new" '
  BEGIN { n = split(ENVIRON["STOP_EXES"], l, "\n"); for (i = 1; i <= n; i++) want[l[i]] = 1 }
  ($2 in want) { print $1; next }
  { print > rest }' "@TMP@/procs"
touch "@TMP@/procs.new"; mv "@TMP@/procs.new" "@TMP@/procs"
'''

    def fill(self, text, **values):
        values.setdefault("TMP", self.posix(self.tmp))
        for key, value in values.items():
            text = text.replace("@%s@" % key, str(value))
        return text

    def run_tail(self, processes, appear_on_call=1, stopped="", was_running="", keepalive=False,
                 with_stop=False, launchd_owned=(), relaunch_appears=False):
        """processes: {pid: executable path}, each visible to `pgrep -x <its
        basename>` only from that name's `appear_on_call`-th call onwards — how
        launchd's restart looks from the script, counted rather than timed so
        the case cannot pass or fail on how fast the machine is. pgrep behaves
        like the real one: prints nothing and exits 1 while there is nothing.

        `open` records a relaunch and, with `relaunch_appears`, puts RELAUNCHED
        at INSTALLED in the table; launchctl answers `list` per `keepalive`,
        and its `unload` removes the `launchd_owned` pids. `sleep` is a no-op,
        since every wait here is counted in calls.

        with_stop runs the stop step ahead of the tail and leaves STOPPED and
        WAS_RUNNING to it; otherwise they are the strings given."""
        with open(os.path.join(self.tmp, "procs"), "w", newline="\n") as f:
            for pid, exe in processes.items():
                f.write("%d|%s|%d\n" % (pid, exe, appear_on_call))
        self.stub("pgrep", self.fill(self.PGREP))
        self.stub("ps", self.fill(self.PS))
        self.stub("launchctl", self.fill(self.LAUNCHCTL, OWNED=" ".join(map(str, launchd_owned)),
                                         LIST=0 if keepalive else 1))
        self.opened = os.path.join(self.tmp, "opened")
        appear = self.fill('echo "@PID@|@EXE@|0" >> "@TMP@/procs"\n', PID=RELAUNCHED, EXE=INSTALLED)
        self.stub("open", 'echo "$*" >> "%s"\n%s' % (self.posix(self.opened), appear if relaunch_appears else ""))
        self.stub("sleep", "exit 0\n")
        # The stop script, at the relative path the step calls it by.
        os.makedirs(os.path.join(self.tmp, "tools"))
        stopper = os.path.join(self.tmp, "tools", "stop-installed-buddy.sh")
        with open(stopper, "w", newline="\n") as f:
            f.write(self.fill(self.STOPPER))
        os.chmod(stopper, 0o755)
        # Present only when the keep-alive was registered, as on a real Mac:
        # the step unloads it only if the file is there.
        plist = os.path.join(self.tmp, "keepalive.plist")
        if launchd_owned:
            open(plist, "w").close()
        if with_stop:
            # The stop step calls running_from, which the tail's prelude would
            # otherwise define only after it.
            before = "%s\n%s\n" % (function(TEXT, "running_from"), STOP)
        else:
            before = 'STOPPED="%s"\nWAS_RUNNING="%s"\n' % (stopped, was_running)
        script = ("set -euo pipefail\nPATH=%s:$PATH\ncd \"%s\"\n"
                  "APP_NAME=\"Orbweaver\"\nEXECUTABLE=\"Orbweaver\"\nBUNDLE_ID=\"io.github.wtvamp.claudebuddy\"\n"
                  "INSTALLED_APP=\"/Applications/Orbweaver.app\"\nKEEPALIVE_PLIST=\"%s\"\n"
                  "INSTALLED_EXE=\"%s\"\nINTERIM_EXE=\"%s\"\nLEGACY_EXE=\"%s\"\n%s%s\necho TAIL-COMPLETED\n") % (
                      self.bash_path(self.bin), self.posix(self.tmp), self.posix(plist),
                      INSTALLED, INTERIM, LEGACY, before, TAIL)
        path = os.path.join(self.tmp, "tail.sh")
        with open(path, "w", newline="\n") as f:
            f.write(script)
        r = subprocess.run([bash(), self.posix(path)], capture_output=True, text=True)
        # Every version of the tail asks pgrep at least once. If the stub was
        # not the pgrep that ran, nothing this case asserts means anything.
        self.assertIn("pgrep", self.stub_calls(),
                      "the stub pgrep was never called, so the stubs were not on PATH:\n" + r.stdout + r.stderr)
        return r.returncode, r.stdout, r.stderr

    def relaunched(self):
        return os.path.exists(self.opened)

    def stop_args(self):
        with open(os.path.join(self.tmp, "stop-args")) as f:
            return f.read().splitlines()

    def pgrep_names(self):
        with open(os.path.join(self.tmp, "pgrep-names")) as f:
            return set(f.read().split())

    def test_with_nothing_running_it_says_how_to_launch_rather_than_failing(self):
        rc, out, err = self.run_tail({})
        self.assertEqual(0, rc, "the install tail failed with no Buddy running:\n" + out + err)
        self.assertIn('Launch it with: open -a "Orbweaver"', out)
        self.assertIn("TAIL-COMPLETED", out)

    def test_a_copy_running_from_another_path_does_not_count_and_does_not_fail(self):
        # Also the arm that returned 1 on its own: the loop's last test was
        # false, so the function's status was the failed [[ ]].
        rc, out, err = self.run_tail({4242: "/Users/dev/Claude Buddy dev.app/Contents/MacOS/ClaudeBuddy"})
        self.assertEqual(0, rc, out + err)
        self.assertIn("Launch it with", out)

    def test_one_installed_copy_is_reported_by_pid(self):
        rc, out, err = self.run_tail({4242: INSTALLED}, stopped="4242", was_running="4242")
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 4242", out)

    def test_two_installed_copies_are_warned_about(self):
        rc, out, err = self.run_tail({4242: INSTALLED, 4343: INSTALLED})
        self.assertEqual(0, rc, out + err)
        self.assertIn("more than one Orbweaver is running: 4242 4343", err)

    # CB-255: the first install after the rename. A copy still running out of
    # the legacy Claude Buddy.app is the old binary, not the one just
    # installed, so it is neither "Running" nor a second copy — and with a
    # Buddy recorded as running before, the new one is relaunched.
    def test_a_copy_still_running_from_the_legacy_bundle_does_not_count(self):
        rc, out, err = self.run_tail({4242: LEGACY}, stopped="", was_running="4242")
        self.assertEqual(0, rc, out + err)
        self.assertNotIn("==> Running: pid 4242", out)
        self.assertNotIn("more than one", err)
        self.assertTrue(self.relaunched(), "the new Orbweaver was not launched in place of the legacy copy")
        with open(self.opened) as f:
            self.assertIn("/Applications/Orbweaver.app", f.read())

    # The control: the same pid at the new path is the install working.
    def test_the_same_copy_at_the_new_path_counts(self):
        rc, out, err = self.run_tail({4242: INSTALLED}, stopped="", was_running="4242")
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 4242", out)
        self.assertFalse(self.relaunched())

    # The lead's install, exactly: on a machine opted in to the keep-alive,
    # unloading it is what stopped the old Buddy, so stop-installed-buddy.sh
    # found nothing and STOPPED was empty. The tail must still wait, because a
    # Buddy was running before, and report the copy launchd starts a moment
    # later — not "launch it with" moments before it appears.
    def test_a_keepalive_install_waits_for_the_copy_launchd_starts(self):
        rc, out, err = self.run_tail({42282: INSTALLED}, appear_on_call=2, stopped="",
                                     was_running="40928", keepalive=True)
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 42282", out)
        self.assertFalse(self.relaunched(), "relaunched by hand although launchd was starting it")

    # Opted in to the keep-alive, but nothing running before: launchd starts
    # it on load anyway, so the tail waits for that too.
    def test_a_loaded_keepalive_is_waited_for_even_with_nothing_running_before(self):
        rc, out, err = self.run_tail({42282: INSTALLED}, appear_on_call=2, keepalive=True)
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 42282", out)
        self.assertFalse(self.relaunched())

    # A Buddy was running, no keep-alive to bring it back: relaunched by hand,
    # once, through open.
    def test_a_running_buddy_with_no_keepalive_is_relaunched(self):
        rc, out, err = self.run_tail({}, stopped="40928", was_running="40928")
        self.assertEqual(0, rc, out + err)
        self.assertTrue(self.relaunched(), "a Buddy that was running was not brought back")

    # The control for the case above: nothing was running and nothing is
    # loaded, so nothing is launched — a first install leaves that to the user.
    def test_nothing_is_launched_when_nothing_was_running(self):
        rc, out, err = self.run_tail({})
        self.assertEqual(0, rc, out + err)
        self.assertFalse(self.relaunched())
        self.assertIn("Launch it with", out)

    # Roxanne's surviving mutant B: with WAS_RUNNING dropped from the wait,
    # every case above still passed, because each one that needed the wait also
    # had STOPPED or a loaded keep-alive to trigger it. Here only WAS_RUNNING
    # says a Buddy was up: stopped by something else, no keep-alive, the copy
    # back a moment later.
    def test_a_buddy_that_was_running_is_waited_for_on_that_alone(self):
        rc, out, err = self.run_tail({42282: INSTALLED}, appear_on_call=2, stopped="",
                                     was_running="40928", keepalive=False)
        self.assertEqual(0, rc, out + err)
        self.assertIn("==> Running: pid 42282", out)
        self.assertFalse(self.relaunched())

    # Roxanne's surviving mutant C: with the relaunch widened to "whenever none
    # appears", every case still passed. A loaded keep-alive on a machine where
    # nothing was running gets waited for — and if launchd never starts it,
    # that is not this script's cue to launch one.
    def test_a_loaded_keepalive_that_starts_nothing_is_not_relaunched_by_hand(self):
        rc, out, err = self.run_tail({}, keepalive=True)
        self.assertEqual(0, rc, out + err)
        self.assertIn("launchctl", self.stub_calls())
        self.assertFalse(self.relaunched(), "launched a Buddy on a machine where none was running")
        self.assertIn("Launch it with", out)

    # ---- CB-256: three executable paths, two process names --------------

    # The case the executable rename exists to get right: a phase-2 copy
    # running out of Orbweaver.app/Contents/MacOS/ClaudeBuddy — the very
    # bundle being replaced — is stopped, and the new executable relaunched
    # in its place and reported as the one running.
    def test_a_phase2_copy_is_stopped_and_the_new_one_relaunched(self):
        rc, out, err = self.run_tail({4242: INTERIM}, with_stop=True, relaunch_appears=True)
        self.assertEqual(0, rc, out + err)
        self.assertIn(INTERIM, self.stop_args(), "the phase-2 executable was not handed to the stop script")
        self.assertIn("==> Stopped the running Orbweaver (4242", out)
        self.assertTrue(self.relaunched(), "the new Orbweaver was not launched in place of the phase-2 copy")
        self.assertIn("==> Running: pid %d" % RELAUNCHED, out)

    # Every path the stop step knows, in one call, and every copy stopped:
    # one of each generation running at once is the worst a machine can have.
    def test_the_stop_step_names_all_three_paths_and_stops_every_generation(self):
        rc, out, err = self.run_tail({4141: LEGACY, 4242: INTERIM, 4343: INSTALLED},
                                     with_stop=True, relaunch_appears=True)
        self.assertEqual(0, rc, out + err)
        self.assertEqual([INSTALLED, INTERIM, LEGACY], self.stop_args())
        self.assertIn("==> Stopped the running Orbweaver (4141 4242 4343", out)
        self.assertIn("==> Running: pid %d" % RELAUNCHED, out)
        self.assertNotIn("more than one", err)

    # Why running_from asks for both names. The keep-alive was running the
    # phase-2 copy, so unloading it is what stopped it and the stop script
    # finds nothing: STOPPED is empty, and only WAS_RUNNING — computed before
    # the unload, through `pgrep -x ClaudeBuddy` — says one was up. Nothing
    # reloads the keep-alive here, so if WAS_RUNNING missed it, nothing would
    # be relaunched and the machine would be left with no Orbweaver at all.
    def test_a_phase2_copy_the_keepalive_was_running_still_counts_as_running(self):
        rc, out, err = self.run_tail({4242: INTERIM}, with_stop=True, launchd_owned=(4242,),
                                     relaunch_appears=True)
        self.assertEqual(0, rc, out + err)
        self.assertEqual([], [line for line in out.splitlines() if "Stopped the running" in line])
        self.assertTrue(self.relaunched(), "a phase-2 copy that was running was not brought back as the new one")
        self.assertIn("==> Running: pid %d" % RELAUNCHED, out)

    # The other half: this build's own process is called Orbweaver, so
    # without `pgrep -x Orbweaver` the copy the install put in place would be
    # invisible to the report. Asserted on the names asked as well as on the
    # outcome, so a stub that answered regardless could not pass it.
    def test_both_process_names_are_asked_for(self):
        rc, out, err = self.run_tail({4242: INSTALLED}, stopped="", was_running="4242")
        self.assertEqual(0, rc, out + err)
        self.assertEqual({"Orbweaver", "ClaudeBuddy"}, self.pgrep_names())
        self.assertIn("==> Running: pid 4242", out)

    # The negative control for the phase-2 cases: a copy still at the
    # phase-2 path after the install is the old binary, not the new one, and
    # must not be reported as running — the same rule as for the legacy
    # bundle above.
    def test_a_copy_still_running_from_the_phase2_path_does_not_count(self):
        rc, out, err = self.run_tail({4242: INTERIM}, stopped="", was_running="4242")
        self.assertEqual(0, rc, out + err)
        self.assertNotIn("==> Running: pid 4242", out)
        self.assertTrue(self.relaunched())


class AgentDenylistParity(unittest.TestCase):
    """CB-413: --install's relaunch keeps a hand copy of AgentEnvironment's
    denylist as shell case patterns, because the script cannot call into the
    app. A name added to one and not the other is a session identity that
    leaks through exactly one of the two launch paths, so the two sets are
    compared here: every ExactNames entry as a literal pattern, every Prefixes
    entry as PREFIX*, plus TMUX/TMUX_PANE, which the script unsets outright."""

    @staticmethod
    def _csharp():
        src = open(os.path.join(HERE, "..", "AgentEnvironment.cs")).read()

        def array(name):
            m = re.search(r"\b" + name + r"\s*=\s*\[(.*?)\];", src, re.S)
            body = re.sub(r"//[^\n]*", "", m.group(1))
            return re.findall(r'"([A-Z_]+)"', body)

        exact = set(array("ExactNames"))
        return exact, {p + "*" for p in array("Prefixes")}

    @staticmethod
    def _script():
        text = open(SCRIPT).read()
        m = re.search(r'case "\$name" in\s*(.*?)\)\s*\n\s*UNSET\+=', text, re.S)
        patterns = {p.strip() for p in m.group(1).replace("\\\n", "").split("|") if p.strip()}
        unset = set(re.findall(r"-u ([A-Z_]+)", re.search(r"UNSET=\((.*?)\)", text).group(1)))
        return patterns, unset

    def test_the_install_relaunch_strips_exactly_the_apps_denylist(self):
        exact, prefixes = self._csharp()
        patterns, unset = self._script()
        self.assertTrue(exact and prefixes, "could not read AgentEnvironment's lists")
        self.assertEqual(exact - {"TMUX", "TMUX_PANE"}, {p for p in patterns if not p.endswith("*")})
        self.assertEqual(prefixes, {p for p in patterns if p.endswith("*")})
        self.assertTrue({"TMUX", "TMUX_PANE", "CLAUDE_CONFIG_DIR"} <= unset)


if __name__ == "__main__":
    unittest.main()
