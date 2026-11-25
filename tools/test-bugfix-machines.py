#!/usr/bin/env python3
"""Tests for the bug-fix org's machine runner (CB-470): host/machines/runner.py,
host/machines/windows.py and windows.ps1, the launchd plist and the CI wiring.
The Mac machines have their own file, tools/test-bugfix-machines-mac.py.

    python3 tools/test-bugfix-machines.py

stdlib unittest. Nothing here touches a machine, GitHub or ssh: the runner is
driven with fake machines, a fake `gh`, a fake lock and a fake clock, and the
Windows module with a fake `sh` that records every argv. Each refusal has a
passing control beside it, so a rule that goes blind turns red instead of
passing for the wrong reason.

Not covered, and said plainly: GhCli against real GitHub, and windows.ps1 on a
real PC (only its text is checked here). Those are QA's real-machine proof.
"""
import json
import os
import plistlib
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, ROOT)

from host.machines import base, runner, windows  # noqa: E402

BOT = "app/orbweaver-bugfix"
SHA = "a" * 40
SHA2 = "b" * 40
OLD = "c" * 40


# ---------------------------------------------------------------- fakes

class FakeGh:
    def __init__(self, **prs):
        self.prs = {int(k[1:]): v for k, v in prs.items()}

    def pr(self, n):
        return self.prs.get(n)


def pr(head=SHA, author=BOT, state="OPEN"):
    return {"state": state, "author": {"login": author}, "headRefOid": head}


class FakeLock:
    def __init__(self, held_by_other=False):
        self.held = held_by_other
        self.other = held_by_other
        self.released = []
        self.stuck = False  # ours, but rmdir fails

    def acquire(self, who):
        if self.held:
            return None
        self.held = True
        return "tok"

        self.stuck = False  # ours, but rmdir fails

    def ours(self, token):
        return self.held and not self.other and token == "tok"

    def release(self, token):
        self.released.append(token)
        if self.other or token != "tok" or self.stuck:
            return False
        self.held = False
        return True


class FakeMachine:
    def __init__(self, name, stamp=OLD):
        self.name, self.stamp, self.kept, self.calls = name, stamp, None, []
        self.install_error = self.restore_error = self.read_error = None
        self.stamp_after_install = None  # None = the sha it was asked for
        self.restore_to = "kept"         # or a literal stamp, for a bad restore
        self.result = base.RunResult(0, "out", "err", False)

    def installed_commit(self):
        self.calls.append(("installed_commit",))
        if self.read_error:
            raise base.MachineError(self.read_error)
        return self.stamp

    def install(self, pr_n, sha, log):
        self.calls.append(("install", pr_n, sha))
        log(f"fake install {sha}")
        if self.install_error:
            raise base.MachineError(self.install_error)
        if self.kept is None:
            self.kept = self.stamp
        self.stamp = self.stamp_after_install or sha

    def restore(self, log):
        self.calls.append(("restore",))
        if self.restore_error:
            raise base.MachineError(self.restore_error)
        if self.kept is not None:
            self.stamp = self.kept if self.restore_to == "kept" else self.restore_to
            self.kept = None

    def run(self, command, timeout_s):
        self.calls.append(("run", command, timeout_s))
        return self.result


class Clock:
    def __init__(self, t=1_000_000.0):
        self.t = t

    def __call__(self):
        return self.t


def cfg(**kw):
    c = dict(runner.DEFAULTS)
    c["company_dir"] = "/nonexistent"
    c.update(kw)
    return c


def make_runner(gh=None, lock=None, machines=None, state=None, clock=None, **kw):
    lines = []
    ms = machines if machines is not None else {n: FakeMachine(n) for n in ("windows", "mini", "macbook")}
    r = runner.Runner(cfg(**kw), gh or FakeGh(p7=pr()), lock or FakeLock(), ms,
                      state if state is not None else {}, clock or Clock(), lines.append, lambda s: None)
    r.lines = lines
    return r


def install(i="i1", n=7, sha=SHA, machines=("windows",)):
    return {"verb": "machine_install", "id": i, "pr": n, "sha": sha, "machines": list(machines)}


def run(i="r1", n=7, machine="windows", command="echo hi", timeout_s=30):
    return {"verb": "machine_run", "id": i, "pr": n, "machine": machine, "command": command, "timeout_s": timeout_s}


def release(i="x1", n=7):
    return {"verb": "machine_release", "id": i, "pr": n}


def one(r, req):
    """Answer one valid request; returns the result dict."""
    [(rid, result, out, err)] = r.tick([(req, None)])
    assert rid == req["id"]
    result["_stdout"], result["_stderr"] = out, err
    return result


# ---------------------------------------------------------------- parse

class ParseTests(unittest.TestCase):
    def ok(self, req):
        got, why = runner.parse_request(json.dumps(req))
        self.assertIsNone(why, why)
        self.assertEqual(got, req)

    def refused(self, req, fragment, has_id=True):
        got, why = runner.parse_request(json.dumps(req) if not isinstance(req, str) else req)
        self.assertIsNotNone(why)
        self.assertIn(fragment, why)
        if has_id:
            self.assertEqual(got["id"], req["id"])
        else:
            self.assertIsNone(got)

    def test_three_verbs_parse(self):
        self.ok(install(machines=("windows", "mini", "macbook")))
        self.ok(run())
        self.ok(release())

    def test_not_json_and_not_object(self):
        self.refused("{", "not JSON", has_id=False)
        self.refused("[]", "not a JSON object", has_id=False)

    def test_id_pattern(self):
        self.refused(dict(release(), id="Has-Caps"), "id must match", has_id=False)
        self.refused(dict(release(), id="a" * 41), "id must match", has_id=False)
        self.refused(dict(release(), id="../etc"), "id must match", has_id=False)
        self.ok(release(i="a" * 40))
        self.ok(release(i="cb-470-1"))

    def test_id_edge_cases_from_qa(self):
        for bad in ("", "a/b", "id\n", "UPPER", "a b", 7):
            self.refused(dict(release(), id=bad), "id must match", has_id=False)
        r = release()
        del r["id"]
        self.refused(r, "id must match", has_id=False)

    def test_type_edge_cases_from_qa(self):
        self.refused("", "not JSON", has_id=False)
        self.refused(run(timeout_s="5"), "timeout_s")
        self.refused(run(timeout_s=True), "timeout_s")
        self.refused(run(timeout_s=-1), "timeout_s")
        self.refused(dict(install(), machines="windows"), "non-empty list")
        self.refused(install(machines=("Windows",)), "unknown machine(s): 'Windows'")
        self.refused(run(command=7), "command must be")

    def test_unknown_verb_is_answered_with_its_id(self):
        self.refused(dict(release(), verb="machine_reboot"), "unknown verb")

    def test_unknown_and_missing_fields(self):
        self.refused(dict(release(), extra=1), "unknown field(s): extra")
        r = install()
        del r["sha"]
        self.refused(r, "missing field(s): sha")

    def test_pr_must_be_a_positive_int(self):
        self.refused(release(n=True), "pr must be")
        self.refused(release(n=0), "pr must be")
        self.refused(release(n="7"), "pr must be")
        self.ok(release(n=1))

    def test_sha_must_be_full_lowercase(self):
        self.refused(install(sha=SHA[:7]), "sha must be")
        self.refused(install(sha=SHA.upper()), "sha must be")
        self.ok(install(sha=SHA))

    def test_machines(self):
        self.refused(install(machines=()), "non-empty list")
        self.refused(install(machines=("windows", "linux")), "unknown machine(s): 'linux'")
        self.refused(install(machines=("mini", "mini")), "twice")
        self.refused(run(machine="linux"), "unknown machine")
        self.ok(run(machine="macbook"))

    def test_command_length(self):
        self.ok(run(command="x" * 4000))
        self.refused(run(command="x" * 4001), "command must be")
        self.refused(run(command="   "), "command must be")

    def test_timeout_bounds(self):
        self.ok(run(timeout_s=600))
        self.ok(run(timeout_s=1))
        self.refused(run(timeout_s=601), "timeout_s")
        self.refused(run(timeout_s=0), "timeout_s")
        self.refused(run(timeout_s=1.5), "timeout_s")


# ---------------------------------------------------------------- PR rules

class PrRuleTests(unittest.TestCase):
    def test_open_allowed_author_at_head_passes(self):
        self.assertIsNone(runner.pr_problem(pr(), [BOT], sha=SHA))

    def test_missing_closed_other_author_stale_sha(self):
        self.assertEqual(runner.pr_problem(None, [BOT]), "PR not found")
        self.assertEqual(runner.pr_problem(pr(state="MERGED"), [BOT]), "PR is not open")
        self.assertIn("not allowed", runner.pr_problem(pr(author="wtvamp"), [BOT]))
        self.assertEqual(runner.pr_problem(pr(head=SHA2), [BOT], sha=SHA), "sha is not the PR's current head")

    def test_allowed_authors_come_from_config(self):
        # QA's real proof names another author in a temp config.
        self.assertIsNone(runner.pr_problem(pr(author="wtvamp"), [BOT, "wtvamp"]))

    def test_default_allows_only_the_app(self):
        self.assertEqual(runner.DEFAULTS["allowed_authors"], [BOT])

    def test_stamp_matches(self):
        self.assertTrue(base.stamp_matches(SHA[:7], SHA))
        self.assertTrue(base.stamp_matches(SHA, SHA))
        self.assertFalse(base.stamp_matches(SHA[:6], SHA))
        self.assertFalse(base.stamp_matches(None, SHA))
        self.assertFalse(base.stamp_matches(SHA2, SHA))


# ---------------------------------------------------------------- runner

FIELDS = {"id", "verb", "pr", "ok", "refused", "started_at", "finished_at"}


def shape(test, res, *extra):
    """Every answer carries the lead's fixed fields and exactly its verb's
    extra ones, so the skill can document them without a 'sometimes'."""
    got = set(res) - {"_stdout", "_stderr"}
    test.assertEqual(got, FIELDS | set(extra), res)


def refused_untouched(test, r, res, fragment):
    test.assertFalse(res["ok"])
    test.assertIn(fragment, res["refused"] or "")
    for m in r.machines.values():
        test.assertFalse([c for c in m.calls if c[0] in ("install", "restore", "run")], m.calls)


class InstallTests(unittest.TestCase):
    def test_install_takes_the_lock_and_confirms_the_stamp(self):
        lock, clock = FakeLock(), Clock()
        r = make_runner(lock=lock, clock=clock)
        res = one(r, install(machines=("windows", "mini")))
        shape(self, res, "machines", "hold_expires_at")
        self.assertTrue(res["ok"], res)
        self.assertIsNone(res["refused"])
        self.assertTrue(lock.held)
        self.assertEqual(res["machines"]["windows"],
                         {"ok": True, "installed_commit": SHA, "previous_commit": OLD, "error": None})
        self.assertEqual(r.hold["before"], {"windows": OLD, "mini": OLD})
        self.assertEqual(r.hold["since"], clock.t)
        self.assertEqual(res["hold_expires_at"], runner.iso(clock.t + 45 * 60))
        self.assertEqual(res["started_at"], runner.iso(clock.t))

    def test_other_author_refused_control_allowed(self):
        r = make_runner(gh=FakeGh(p7=pr(author="someone")))
        res = one(r, install())
        shape(self, res)
        refused_untouched(self, r, res, "not allowed")
        self.assertIsNone(r.hold)
        self.assertTrue(one(make_runner(), install())["ok"])

    def test_empty_allowed_list_allows_nobody(self):
        res = one(make_runner(allowed_authors=[]), install())
        self.assertIn("not allowed", res["refused"])

    def test_stale_sha_refused_control_head_ok(self):
        r = make_runner(gh=FakeGh(p7=pr(head=SHA2)))
        res = one(r, install(sha=SHA))
        refused_untouched(self, r, res, "sha is not the PR's current head")
        self.assertFalse(r.lock.held)
        self.assertTrue(one(make_runner(gh=FakeGh(p7=pr(head=SHA2))), install(sha=SHA2))["ok"])

    def test_head_is_asked_afresh_for_every_install(self):
        gh = FakeGh(p7=pr())
        r = make_runner(gh=gh)
        self.assertTrue(one(r, install())["ok"])
        gh.prs[7] = pr(head=SHA2)  # a push since the first install
        self.assertIn("current head", one(r, install(i="i2", sha=SHA))["refused"])

    def test_closed_missing_and_unreadable_pr_refused(self):
        self.assertEqual(one(make_runner(gh=FakeGh(p7=pr(state="CLOSED"))), install())["refused"], "PR is not open")
        self.assertEqual(one(make_runner(gh=FakeGh(p7=pr(state="MERGED"))), install())["refused"], "PR is not open")
        # GhCli turns a gh error or timeout into None: the gate fails closed.
        self.assertEqual(one(make_runner(gh=FakeGh()), install())["refused"], "PR not found")

    def test_lock_held_elsewhere_waits_then_refuses(self):
        clock = Clock()
        slept = []

        def sleep(s):
            slept.append(s)
            clock.t += s
        r = make_runner(lock=FakeLock(held_by_other=True), clock=clock, lock_wait_s=60)
        r.sleep = sleep
        res = one(r, install())
        refused_untouched(self, r, res, "install lock is held by another session and was not free within 60 s")
        self.assertEqual(sum(slept), 60)
        self.assertIsNone(r.hold)

    def test_lock_freed_during_the_wait_is_taken(self):
        clock, lock = Clock(), FakeLock(held_by_other=True)

        def sleep(s):
            clock.t += s
            lock.held = lock.other = False  # the other team released it
        r = make_runner(lock=lock, clock=clock)
        r.sleep = sleep
        self.assertTrue(one(r, install())["ok"])

    def test_lock_wait_defaults_to_ten_minutes(self):
        self.assertEqual(runner.DEFAULTS["lock_wait_s"], 600)

    def test_an_unauthorized_pr_learns_nothing_about_the_holder(self):
        r = make_runner(gh=FakeGh(p7=pr(), p8=pr(author="someone")))
        one(r, install())
        res = one(r, install(i="i2", n=8))
        self.assertIn("not allowed", res["refused"])
        self.assertNotIn("#7", res["refused"])

    def test_one_pr_at_a_time(self):
        gh = FakeGh(p7=pr(), p8=pr(head=SHA2))
        r = make_runner(gh=gh)
        self.assertTrue(one(r, install())["ok"])
        before = list(r.machines["windows"].calls)
        res = one(r, install(i="i2", n=8, sha=SHA2))
        self.assertFalse(res["ok"])
        self.assertIn("held by PR #7", res["refused"])
        self.assertEqual(r.hold["pr"], 7)
        self.assertEqual(r.machines["windows"].calls, before)

    def test_same_pr_reinstalls_new_head_keeping_the_original_before_and_start(self):
        gh = FakeGh(p7=pr())
        clock = Clock()
        r = make_runner(gh=gh, clock=clock)
        first = one(r, install())
        gh.prs[7] = pr(head=SHA2)
        clock.t += 60
        res = one(r, install(i="i2", sha=SHA2, machines=("windows", "mini")))
        self.assertTrue(res["ok"], res)
        # windows' previous build is still the one from before the hold, not SHA.
        self.assertEqual(res["machines"]["windows"]["previous_commit"], OLD)
        self.assertEqual(r.hold["before"], {"windows": OLD, "mini": OLD})
        self.assertEqual(r.hold["sha"], SHA2)
        # A re-install does not extend the 45 minutes.
        self.assertEqual(res["hold_expires_at"], first["hold_expires_at"])

    def test_machine_not_configured_refused(self):
        ms = {"windows": FakeMachine("windows")}
        res = one(make_runner(machines=ms), install(machines=("mini",)))
        self.assertIn("not available on this runner: mini", res["refused"])
        self.assertTrue(one(make_runner(machines=ms), install())["ok"])

    def test_stamp_mismatch_fails_restores_and_releases(self):
        r = make_runner()
        r.machines["windows"].stamp_after_install = SHA2
        res = one(r, install(machines=("windows", "mini")))
        shape(self, res, "machines", "hold_expires_at")
        self.assertFalse(res["ok"])
        self.assertIsNone(res["refused"])
        self.assertIn("reports " + SHA2, res["machines"]["windows"]["error"])
        self.assertEqual(res["machines"]["windows"]["installed_commit"], OLD)  # what is there now
        self.assertEqual(r.machines["windows"].stamp, OLD)
        self.assertNotIn(("install", 7, SHA), r.machines["mini"].calls)
        self.assertIsNone(r.hold)
        self.assertFalse(r.lock.held)

    def test_failure_on_machine_two_of_three_restores_machine_one(self):
        r = make_runner()
        r.machines["mini"].install_error = "no artifact"
        res = one(r, install(machines=("windows", "mini", "macbook")))
        self.assertFalse(res["ok"])
        self.assertIsNone(res["refused"])
        self.assertEqual(res["machines"]["mini"]["error"], "install failed: no artifact")
        self.assertTrue(res["machines"]["windows"]["ok"])           # restored, and confirmed
        self.assertEqual(res["machines"]["windows"]["installed_commit"], OLD)
        self.assertEqual(r.machines["windows"].stamp, OLD)
        self.assertIn(("restore",), r.machines["windows"].calls)
        # macbook was only read: its restore is the seam's no-op, and the
        # report says its build is the one it had.
        self.assertFalse(any(c[0] == "install" for c in r.machines["macbook"].calls))
        self.assertEqual(res["machines"]["macbook"]["installed_commit"], OLD)
        self.assertIsNone(r.hold)
        self.assertFalse(r.lock.held)

    def test_failed_restore_after_failed_install_says_both(self):
        r = make_runner()
        r.machines["windows"].stamp_after_install = SHA2
        r.machines["windows"].restore_to = SHA2
        res = one(r, install())
        self.assertIn("then restored build reports", res["machines"]["windows"]["error"])

    def test_unreadable_machine_fails_before_any_install(self):
        r = make_runner()
        r.machines["mini"].read_error = "ssh: connect timed out"
        res = one(r, install(machines=("windows", "mini")))
        self.assertFalse(res["ok"])
        self.assertIn("could not read the installed build", res["machines"]["mini"]["error"])
        self.assertFalse(any(c[0] == "install" for c in r.machines["windows"].calls))
        self.assertFalse(r.lock.held)


class RunTests(unittest.TestCase):
    RUN_FIELDS = ("machine", "exit_code", "timed_out", "stdout_truncated", "stderr_truncated")

    def held(self, **kw):
        r = make_runner(**kw)
        self.assertTrue(one(r, install(machines=("windows",)))["ok"])
        return r

    def test_run_without_hold_refused_control_with_hold(self):
        r = make_runner()
        res = one(r, run())
        shape(self, res, *self.RUN_FIELDS)
        refused_untouched(self, r, res, "does not hold the machines")
        self.assertIsNone(res["exit_code"])
        r = self.held()
        res = one(r, run())
        shape(self, res, *self.RUN_FIELDS)
        self.assertEqual((res["ok"], res["exit_code"], res["timed_out"]), (True, 0, False))
        self.assertEqual((res["_stdout"], res["_stderr"]), ("out", "err"))
        self.assertIn(("run", "echo hi", 30), r.machines["windows"].calls)

    def test_nonzero_exit_is_not_ok_but_not_refused(self):
        r = self.held()
        r.machines["windows"].result = base.RunResult(3, "", "boom", False)
        res = one(r, run())
        self.assertEqual((res["ok"], res["refused"], res["exit_code"]), (False, None, 3))

    def test_run_by_another_pr_refused(self):
        r = self.held(gh=FakeGh(p7=pr(), p8=pr()))
        self.assertIn("does not hold", one(r, run(n=8))["refused"])

    def test_run_on_a_machine_not_installed_refused(self):
        r = self.held()
        self.assertIn("not one of the machines", one(r, run(machine="mini"))["refused"])
        self.assertEqual(r.machines["mini"].calls, [])

    def test_run_after_release_refused(self):
        r = self.held()
        one(r, release())
        self.assertIn("does not hold", one(r, run())["refused"])

    def test_run_after_head_moved_refused(self):
        r = self.held()
        r.gh.prs[7] = pr(head=SHA2)
        self.assertIn("head moved", one(r, run())["refused"])

    def test_run_on_closed_pr_refused(self):
        r = self.held()
        r.gh.prs[7] = pr(state="MERGED")
        self.assertEqual(one(r, run())["refused"], "PR is not open")

    def test_every_command_is_logged_with_machine_pr_and_exit(self):
        r = self.held()
        r.machines["windows"].result = base.RunResult(3, "", "boom", False)
        one(r, run(command='dir "C:\\x"'))
        line = [l for l in r.lines if "run id=r1" in l][0]
        self.assertIn("pr=7", line)
        self.assertIn("machine=windows", line)
        self.assertIn("exit=3", line)
        self.assertIn(json.dumps('dir "C:\\x"'), line)

    def test_refused_run_is_logged_too(self):
        r = make_runner()
        one(r, run())
        self.assertTrue(any("refused id=r1 verb=machine_run" in l and "machine=windows" in l for l in r.lines))

    def test_run_on_a_machine_dropped_from_config_refused(self):
        r = self.held()
        del r.machines["windows"]
        self.assertIn("not available on this runner", one(r, run())["refused"])

    def test_run_machine_error_is_a_failure_with_the_sentence_in_stderr(self):
        r = self.held()
        r.machines["windows"].run = lambda c, t: (_ for _ in ()).throw(base.MachineError("ssh: no route"))
        res = one(r, run())
        self.assertEqual((res["ok"], res["refused"], res["exit_code"]), (False, None, None))
        self.assertEqual(res["_stderr"], "ssh: no route\n")
        self.assertTrue(any("run id=r1 failed: ssh: no route" in l for l in r.lines))

    def test_timeout_is_reported(self):
        r = self.held()
        r.machines["windows"].result = base.RunResult(None, "partial", "", True)
        res = one(r, run())
        self.assertEqual((res["ok"], res["exit_code"], res["timed_out"]), (False, None, True))
        self.assertEqual(res["_stdout"], "partial")

    def test_output_is_bounded_and_flagged(self):
        r = self.held(max_output_bytes=10)
        r.machines["windows"].result = base.RunResult(0, "0123456789abc", "short", False)
        res = one(r, run())
        self.assertEqual((res["_stdout"], res["stdout_truncated"]), ("0123456789", True))
        self.assertEqual((res["_stderr"], res["stderr_truncated"]), ("short", False))

    def test_bound_never_splits_a_character(self):
        self.assertEqual(runner.bound("é" * 3, 3), ("é", True))
        self.assertEqual(runner.bound(None, 3), ("", False))


class ReleaseTests(unittest.TestCase):
    def test_release_restores_confirms_and_unlocks(self):
        r = make_runner()
        one(r, install(machines=("windows", "mini")))
        res = one(r, release())
        shape(self, res, "machines", "hold_expires_at")
        self.assertTrue(res["ok"], res)
        self.assertEqual(res["machines"]["windows"],
                         {"ok": True, "installed_commit": OLD, "previous_commit": OLD, "error": None})
        self.assertIsNone(res["hold_expires_at"])
        self.assertEqual(r.machines["windows"].stamp, OLD)
        self.assertIsNone(r.hold)
        self.assertFalse(r.lock.held)

    def test_release_by_non_holder_refused_and_touches_nothing(self):
        r = make_runner(gh=FakeGh(p7=pr(), p8=pr()))
        one(r, install())
        res = one(r, release(n=8))
        shape(self, res, "machines", "hold_expires_at")
        self.assertIn("does not hold", res["refused"])
        self.assertNotIn(("restore",), r.machines["windows"].calls)
        self.assertEqual(r.hold["pr"], 7)
        self.assertTrue(r.lock.held)
        self.assertEqual(r.lock.released, [])

    def test_release_with_no_hold_refused(self):
        self.assertIn("does not hold", one(make_runner(), release())["refused"])

    def test_release_works_after_the_pr_merged(self):
        r = make_runner()
        one(r, install())
        r.gh.prs[7] = pr(state="MERGED")
        self.assertTrue(one(r, release())["ok"])

    def test_restore_that_does_not_match_fails_but_still_unlocks(self):
        r = make_runner()
        one(r, install())
        r.machines["windows"].restore_to = SHA2
        res = one(r, release())
        self.assertFalse(res["ok"])
        self.assertIsNone(res["refused"])
        self.assertIn("expected " + OLD, res["machines"]["windows"]["error"])
        # Tried twice, then let go: holding the lock forever stops every team.
        self.assertEqual(r.machines["windows"].calls.count(("restore",)), 2)
        self.assertFalse(r.lock.held)
        self.assertEqual(r.state["unrestored"][0]["want"], OLD)
        self.assertTrue(any("NEEDS ATTENTION" in l for l in r.lines))

    def test_twice_failed_restore_raises_the_marker_and_blocks_installs(self):
        r = make_runner()
        one(r, install())
        r.machines["windows"].restore_to = SHA2
        one(r, release())
        rec = r.attention.read()
        self.assertEqual((rec["machine"], rec["should_have"], rec["has"]), ("windows", OLD, SHA2))
        calls = list(r.machines["windows"].calls)
        res = one(r, install(i="i2"))
        self.assertFalse(res["ok"])
        self.assertIn("NEEDS-ATTENTION.json", res["refused"])
        self.assertIn("windows was not restored after PR #7", res["refused"])
        self.assertEqual(r.machines["windows"].calls, calls)
        self.assertFalse(r.lock.held)
        # Control: once a person deletes the marker, installs work again.
        r.attention.record = None
        self.assertTrue(one(r, install(i="i3"))["ok"])

    def test_a_confirmed_restore_raises_no_marker(self):
        r = make_runner()
        one(r, install())
        one(r, release())
        self.assertIsNone(r.attention.read())

    def test_the_first_marker_is_kept(self):
        m = runner.MemoryMarker()
        m.write({"reason": "first"})
        m.write({"reason": "second"})
        self.assertEqual(m.read()["reason"], "first")

    def test_releasing_pr_must_be_the_holder(self):
        r = make_runner(gh=FakeGh(p7=pr(), p8=pr()))
        one(r, install())
        r.gh.prs[7] = pr(state="MERGED")  # holder's state does not matter
        self.assertIn("does not hold", one(r, release(n=8))["refused"])
        self.assertTrue(one(r, release())["ok"])

    def test_our_stranded_lock_is_remembered_and_retried(self):
        lock = FakeLock()
        r = make_runner(lock=lock)
        one(r, install())
        lock.stuck = True
        self.assertFalse(one(r, release())["ok"])
        self.assertEqual(r.state["lock_stranded"], "tok")
        r.tick([])
        self.assertIn("lock_stranded", r.state)  # still stuck: kept
        self.assertTrue(any("still cannot remove our install lock" in l for l in r.lines))
        lock.stuck = False
        r.tick([])
        self.assertNotIn("lock_stranded", r.state)
        self.assertFalse(lock.held)

    def test_a_lock_someone_else_replaced_is_not_remembered(self):
        lock = FakeLock()
        r = make_runner(lock=lock)
        one(r, install())
        lock.other = True
        one(r, release())
        self.assertNotIn("lock_stranded", r.state)

    def test_machine_dropped_from_config_is_reported_not_crashed(self):
        r = make_runner()
        one(r, install())
        del r.machines["windows"]
        res = one(r, release())
        self.assertEqual(res["machines"]["windows"]["error"], "machine no longer configured on this runner")
        self.assertFalse(r.lock.held)

    def test_restore_error_retried_once(self):
        r = make_runner()
        one(r, install())
        r.machines["windows"].restore_error = "ssh down"
        res = one(r, release())
        self.assertFalse(res["machines"]["windows"]["ok"])
        self.assertEqual(res["machines"]["windows"]["error"], "restore failed: ssh down")
        self.assertEqual(r.machines["windows"].calls.count(("restore",)), 2)

    def test_lock_left_behind_fails_the_release_loudly(self):
        lock = FakeLock()
        r = make_runner(lock=lock)
        one(r, install())
        lock.other = True  # rmdir failed, or someone replaced it by hand
        res = one(r, release())
        self.assertFalse(res["ok"])
        self.assertTrue(res["machines"]["windows"]["ok"])
        self.assertIn("was not removed", res["_stderr"])
        self.assertTrue(any("NEEDS ATTENTION: install lock" in l for l in r.lines))
        self.assertIsNone(r.hold)


class CapTests(unittest.TestCase):
    def test_hold_past_cap_is_restored_and_released_while_idle_control_just_before(self):
        clock = Clock()
        r = make_runner(clock=clock, hold_cap_s=120)
        one(r, install())
        clock.t += 119
        self.assertEqual(r.tick([]), [])
        self.assertIsNotNone(r.hold)
        clock.t += 1
        self.assertEqual(r.tick([]), [])  # no request needed
        self.assertIsNone(r.hold)
        self.assertFalse(r.lock.held)
        self.assertEqual(r.machines["windows"].stamp, OLD)
        self.assertTrue(any("cap; restoring" in l for l in r.lines))
        self.assertTrue(any("released the machines" in l for l in r.lines))
        res = one(r, run())
        self.assertIn("reached its cap", res["refused"])
        res = one(r, release())
        self.assertTrue(res["ok"])
        self.assertIsNone(res["refused"])
        self.assertIn("already reached its cap", res["_stdout"])
        self.assertTrue(res["machines"]["windows"]["ok"])

    def test_cap_defaults_to_45_minutes(self):
        self.assertEqual(runner.DEFAULTS["hold_cap_s"], 2700)

    def test_a_hold_whose_install_never_finished_is_restored_next_tick(self):
        lock = FakeLock()
        lock.held = True
        m = FakeMachine("windows")
        m.kept, m.stamp = OLD, SHA  # died after installing, before since was set
        state = {"hold": {"pr": 7, "sha": SHA, "since": None, "machines": ["windows"],
                          "before": {"windows": OLD}, "lock_token": "tok"}}
        r = make_runner(lock=lock, machines={"windows": m}, state=state)
        r.tick([])
        self.assertIsNone(r.hold)
        self.assertEqual(m.stamp, OLD)
        self.assertFalse(lock.held)

    def test_a_new_install_clears_the_old_expiry_note(self):
        clock = Clock()
        r = make_runner(clock=clock, hold_cap_s=10)
        one(r, install())
        clock.t += 10
        r.tick([])
        self.assertIn("expired", r.state)
        one(r, install(i="i2"))
        self.assertNotIn("expired", r.state)


class RefusedParseTests(unittest.TestCase):
    def test_a_bad_request_with_an_id_is_answered(self):
        r = make_runner()
        req, why = runner.parse_request(json.dumps(dict(release(), extra=1)))
        [(rid, result, _, _)] = r.tick([(req, why)])
        self.assertEqual((rid, result["ok"]), ("x1", False))
        self.assertIn("unknown field", result["refused"])


# ---------------------------------------------------------------- files

class DirLockTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "ow-install.lock")

    def tearDown(self):
        self.tmp.cleanup()

    def test_acquire_is_exclusive_and_release_removes_file_and_dir(self):
        lock = runner.DirLock(self.path)
        tok = lock.acquire("runner")
        self.assertIsNotNone(tok)
        self.assertIsNone(lock.acquire("runner"))
        self.assertTrue(lock.release(tok))
        self.assertFalse(os.path.exists(self.path))

    def test_someone_elses_lock_is_never_removed(self):
        os.mkdir(self.path)  # taken by hand, the way people take it
        lock = runner.DirLock(self.path)
        self.assertIsNone(lock.acquire("runner"))
        self.assertFalse(lock.release("anything"))
        self.assertTrue(os.path.isdir(self.path))

    def test_unexpected_mkdir_error_is_raised_not_read_as_busy(self):
        lock = runner.DirLock(os.path.join(self.tmp.name, "missing", "ow-install.lock"))
        with self.assertRaises(OSError):
            lock.acquire("runner")

    def test_ours(self):
        lock = runner.DirLock(self.path)
        self.assertFalse(lock.ours("x"))
        tok = lock.acquire("runner")
        self.assertTrue(lock.ours(tok))
        self.assertFalse(lock.ours("x"))

    def test_file_marker_keeps_the_first_problem(self):
        m = runner.FileMarker(os.path.join(self.tmp.name, "NEEDS-ATTENTION.json"))
        self.assertIsNone(m.read())
        m.write({"reason": "first"})
        m.write({"reason": "second"})
        self.assertEqual(m.read()["reason"], "first")

    def test_a_lock_already_gone_counts_as_released(self):
        self.assertTrue(runner.DirLock(self.path).release("tok"))

    def test_write_result_never_overwrites(self):
        self.assertTrue(runner.write_result(self.tmp.name, "a1", {"ok": True}, "1", ""))
        self.assertFalse(runner.write_result(self.tmp.name, "a1", {"ok": False}, "2", ""))
        with open(os.path.join(self.tmp.name, "a1", "stdout.txt")) as f:
            self.assertEqual(f.read(), "1")

    def test_wrong_token_refused(self):
        lock = runner.DirLock(self.path)
        lock.acquire("runner")
        self.assertFalse(lock.release("not ours"))
        self.assertTrue(os.path.isdir(self.path))


@unittest.skipIf(os.name == "nt", "the runner runs on the mini; fcntl is POSIX")
class RunOnceTests(unittest.TestCase):
    """run_once end to end over real files: spool in, result folders out,
    state carried between ticks, machines and gh faked."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        t = self.tmp.name
        self.company = os.path.join(t, "company")
        self.conf = {"company_dir": self.company, "state_dir": os.path.join(t, "state"),
                     "log_path": os.path.join(t, "machines.log"), "lock_path": os.path.join(t, "ow-install.lock"),
                     "hold_cap_s": 300, "lock_wait_s": 30, "machines": {"windows": {}}}
        self.cpath = os.path.join(t, "machines.json")
        with open(self.cpath, "w") as f:
            json.dump(self.conf, f)
        self.spool = os.path.join(self.company, "machine-spool")
        self.results = os.path.join(self.company, "machine-results")
        self.clock = Clock()
        self.machines = {"windows": FakeMachine("windows")}
        self.gh = FakeGh(p7=pr())
        self.n = 0

    def tearDown(self):
        self.tmp.cleanup()

    def drop(self, req, raw=None, name=None):
        os.makedirs(self.spool, exist_ok=True)
        self.n += 1
        path = os.path.join(self.spool, name or f"{req['id']}.json")
        with open(path, "w") as f:
            f.write(raw if raw is not None else json.dumps(req))
        os.utime(path, (self.n, self.n))
        return path

    def sleep(self, s):
        self.clock.t += s

    def tick(self):
        return runner.run_once(self.cpath, clock=self.clock, gh=self.gh, machines=self.machines, sleep=self.sleep)

    def result(self, rid):
        d = os.path.join(self.results, rid)
        self.assertEqual(sorted(os.listdir(d)), ["result.json", "stderr.txt", "stdout.txt"])
        with open(os.path.join(d, "result.json")) as f:
            res = json.load(f)
        with open(os.path.join(d, "stdout.txt")) as f:
            res["_stdout"] = f.read()
        with open(os.path.join(d, "stderr.txt")) as f:
            res["_stderr"] = f.read()
        return res

    def log_text(self):
        with open(self.conf["log_path"]) as f:
            return f.read()

    def test_the_runner_creates_both_company_folders(self):
        self.tick()
        for d in (self.spool, self.results):
            self.assertTrue(os.path.isdir(d))
            self.assertEqual(os.stat(d).st_mode & 0o777, 0o755)

    def test_a_full_round_across_three_ticks(self):
        self.drop(install())
        self.assertEqual(self.tick(), 0)
        self.assertTrue(self.result("i1")["ok"])
        self.assertTrue(os.path.isdir(self.conf["lock_path"]))
        # The hold survives into the next tick through the state file.
        self.drop(run(command="echo hi"))
        self.tick()
        res = self.result("r1")
        self.assertEqual((res["ok"], res["_stdout"], res["_stderr"]), (True, "out", "err"))
        self.drop(release())
        self.tick()
        self.assertTrue(self.result("x1")["ok"])
        self.assertFalse(os.path.exists(self.conf["lock_path"]))
        self.assertEqual(sorted(os.listdir(os.path.join(self.spool, "done"))), ["i1.json", "r1.json", "x1.json"])
        self.assertIn('pr=7 machine=windows run id=r1 exit=0 timed_out=False command="echo hi"', self.log_text())

    def test_requests_in_one_tick_are_answered_in_arrival_order(self):
        self.drop(run())       # before the install: refused
        self.drop(install())
        self.drop(run(i="r2"))  # after it: runs
        self.tick()
        self.assertIn("does not hold", self.result("r1")["refused"])
        self.assertTrue(self.result("r2")["ok"])

    def test_tmp_files_are_left_for_the_writer_to_finish(self):
        self.drop(install(), name="i1.json.tmp")
        self.tick()
        self.assertEqual(sorted(os.listdir(self.spool)), ["bad", "done", "i1.json.tmp"])
        self.assertFalse(os.path.exists(os.path.join(self.results, "i1")))

    def test_bad_requests_go_to_bad_and_are_answered_when_they_have_an_id(self):
        self.drop(None, raw="not json", name="junk.json")
        self.drop(dict(release(), extra=1))
        self.tick()
        self.assertEqual(sorted(os.listdir(os.path.join(self.spool, "bad"))), ["junk.json", "x1.json"])
        self.assertIn("unknown field", self.result("x1")["refused"])
        self.assertIn("dropped junk.json: not JSON", self.log_text())

    def test_file_name_must_be_the_id(self):
        self.drop(install(), name="other.json")
        self.tick()
        self.assertFalse(os.path.exists(os.path.join(self.results, "i1")))
        self.assertFalse(os.path.exists(os.path.join(self.results, "other")))
        self.assertEqual(os.listdir(os.path.join(self.spool, "bad")), ["other.json"])
        self.assertIn("file name is not <id>.json", self.log_text())
        self.assertEqual(self.machines["windows"].calls, [])
        self.drop(install())  # control
        self.tick()
        self.assertTrue(self.result("i1")["ok"])

    def test_oversized_request_dropped(self):
        self.drop(None, raw=json.dumps(release()) + " " * (64 * 1024), name="x1.json")
        self.tick()
        self.assertFalse(os.path.exists(os.path.join(self.results, "x1")))
        self.assertIn("larger than 64 KiB", self.log_text())

    def test_a_reused_id_is_not_run_and_the_first_answer_stands(self):
        self.drop(install())
        self.tick()
        with open(os.path.join(self.results, "i1", "result.json"), "rb") as f:
            first = f.read()
        calls = list(self.machines["windows"].calls)
        self.drop(release(i="i1"))
        self.tick()
        with open(os.path.join(self.results, "i1", "result.json"), "rb") as f:
            self.assertEqual(f.read(), first)
        self.assertEqual(self.machines["windows"].calls, calls)  # the release never ran
        self.assertIn("already has a result", self.log_text())
        self.assertEqual(os.listdir(os.path.join(self.spool, "bad")), ["i1.json"])

    def test_short_cap_restores_between_ticks(self):
        self.drop(install())
        self.tick()
        self.clock.t += 300
        self.tick()
        self.assertFalse(os.path.exists(self.conf["lock_path"]))
        self.assertEqual(self.machines["windows"].stamp, OLD)
        with open(os.path.join(self.conf["state_dir"], "state.json")) as f:
            self.assertIsNone(json.load(f)["hold"])

    def test_a_hand_taken_lock_waits_then_refuses_and_is_never_removed(self):
        os.mkdir(self.conf["lock_path"])
        self.drop(install())
        t0 = self.clock.t
        self.tick()
        self.assertIn("install lock is held", self.result("i1")["refused"])
        self.assertEqual(self.clock.t - t0, 30)
        self.assertTrue(os.path.isdir(self.conf["lock_path"]))
        self.assertEqual(self.machines["windows"].calls, [])

    def test_a_second_runner_on_the_same_state_does_nothing(self):
        import fcntl
        os.makedirs(self.conf["state_dir"])
        with open(os.path.join(self.conf["state_dir"], "runner.lock"), "w") as g:
            fcntl.flock(g, fcntl.LOCK_EX)
            self.drop(install())
            self.tick()
            self.assertEqual(os.listdir(self.spool), ["i1.json"])
        self.tick()
        self.assertTrue(self.result("i1")["ok"])

    def test_an_unreadable_spool_entry_is_skipped(self):
        os.makedirs(os.path.join(self.spool, "odd.json"))
        self.drop(install())
        self.tick()
        self.assertTrue(self.result("i1")["ok"])
        self.assertTrue(os.path.isdir(os.path.join(self.spool, "odd.json")))

    def test_writes_nothing_outside_its_own_folders(self):
        bugfix = os.path.join(self.company, "bugfix-spool")
        os.makedirs(bugfix)
        self.drop(install())
        self.tick()
        self.assertEqual(os.listdir(bugfix), [])
        self.assertEqual(sorted(os.listdir(self.company)), ["bugfix-spool", "machine-results", "machine-spool"])

    def test_marker_file_after_a_failed_restore_blocks_installs_until_deleted(self):
        self.drop(install())
        self.tick()
        self.machines["windows"].restore_to = SHA2
        self.drop(release())
        self.tick()
        marker = os.path.join(self.results, "NEEDS-ATTENTION.json")
        with open(marker) as f:
            rec = json.load(f)
        self.assertEqual((rec["machine"], rec["should_have"], rec["has"]), ("windows", OLD, SHA2))
        self.drop(install(i="i2"))
        self.tick()
        self.assertIn("NEEDS-ATTENTION.json", self.result("i2")["refused"])
        os.remove(marker)
        self.machines["windows"].restore_to = "kept"
        self.drop(install(i="i3"))
        self.tick()
        self.assertTrue(self.result("i3")["ok"])

    def test_an_unreadable_state_file_is_kept_aside_and_blocks_installs(self):
        os.makedirs(self.conf["state_dir"])
        sp = os.path.join(self.conf["state_dir"], "state.json")
        with open(sp, "w") as f:
            f.write("{ half a hold")
        self.drop(install())
        self.tick()
        self.assertIn("NEEDS-ATTENTION.json", self.result("i1")["refused"])
        aside = [n for n in os.listdir(self.conf["state_dir"]) if n.startswith("state.json.unreadable-")]
        self.assertEqual(len(aside), 1)
        with open(os.path.join(self.conf["state_dir"], aside[0])) as f:
            self.assertEqual(f.read(), "{ half a hold")
        self.assertIn("NEEDS ATTENTION: state file unreadable", self.log_text())
        self.assertEqual(self.machines["windows"].calls, [])

    def test_a_non_object_state_file_counts_as_unreadable(self):
        os.makedirs(self.conf["state_dir"])
        with open(os.path.join(self.conf["state_dir"], "state.json"), "w") as f:
            f.write("[]")
        self.tick()
        self.assertTrue(os.path.exists(os.path.join(self.results, "NEEDS-ATTENTION.json")))

    def test_an_unreadable_marker_still_blocks(self):
        os.makedirs(self.results)
        with open(os.path.join(self.results, "NEEDS-ATTENTION.json"), "w") as f:
            f.write("garbage")
        self.drop(install())
        self.tick()
        self.assertIn("an unreadable marker", self.result("i1")["refused"])

    def test_state_file_is_private(self):
        self.drop(install())
        self.tick()
        mode = os.stat(os.path.join(self.conf["state_dir"], "state.json")).st_mode & 0o777
        self.assertEqual(mode, 0o600)


class ConfigTests(unittest.TestCase):
    def test_defaults_fill_in_and_company_dir_is_required(self):
        with tempfile.TemporaryDirectory() as t:
            p = os.path.join(t, "m.json")
            with open(p, "w") as f:
                json.dump({"company_dir": "~/x", "allowed_authors": [BOT, "wtvamp"]}, f)
            c = runner.load_config(p)
            self.assertEqual(c["hold_cap_s"], 2700)
            self.assertEqual(c["lock_path"], "/tmp/ow-install.lock")
            self.assertEqual(c["allowed_authors"], [BOT, "wtvamp"])
            self.assertEqual(c["company_dir"], os.path.expanduser("~/x"))
            with open(p, "w") as f:
                json.dump({}, f)
            with self.assertRaises(SystemExit):
                runner.load_config(p)


class MakeMachinesTests(unittest.TestCase):
    def test_builds_what_is_configured_and_ignores_unknown_names(self):
        ms = runner.make_machines({"state_dir": "/tmp", "machines": {"windows": {}, "toaster": {}}})
        self.assertEqual(list(ms), ["windows"])
        self.assertIsInstance(ms["windows"], windows.WindowsMachine)
        self.assertEqual(runner.make_machines({"machines": {}}), {})

    @unittest.skipUnless(os.path.exists(os.path.join(ROOT, "host/machines/mac.py")), "mac.py not on this branch yet")
    def test_builds_the_macs_from_mac_py(self):
        ms = runner.make_machines({"state_dir": "/tmp", "machines": {"mini": {}, "macbook": {"ssh": "mbp"}}})
        self.assertEqual(sorted(ms), ["macbook", "mini"])
        self.assertEqual(ms["mini"].name, "mini")


class DefaultShTests(unittest.TestCase):
    def test_captures_text_and_returns_nonzero_as_data(self):
        r = base.default_sh([sys.executable, "-c", "import sys; print('hi'); sys.exit(3)"], timeout=30)
        self.assertEqual((r.returncode, r.stdout.strip()), (3, "hi"))


class GhCliTests(unittest.TestCase):
    def test_asks_gh_for_the_three_fields_and_reads_failure_as_missing(self):
        seen = []

        def sh(argv):
            seen.append(argv)
            return subprocess.CompletedProcess(argv, 0, json.dumps(pr()), "")
        self.assertEqual(runner.GhCli("o/r", sh).pr(7), pr())
        self.assertEqual(seen[0], ["gh", "pr", "view", "7", "--repo", "o/r", "--json", "state,author,headRefOid"])
        self.assertIsNone(runner.GhCli("o/r", lambda a: subprocess.CompletedProcess(a, 1, "", "no")).pr(7))
        self.assertIsNone(runner.GhCli("o/r", lambda a: subprocess.CompletedProcess(a, 0, "{", "")).pr(7))

        def hang(argv):
            raise subprocess.TimeoutExpired(argv, 60)
        self.assertIsNone(runner.GhCli("o/r", hang).pr(7))


# ---------------------------------------------------------------- windows

class FakeSh:
    """Records argv; answers by the first matching rule."""

    def __init__(self, rules):
        self.rules, self.calls = rules, []

    def __call__(self, argv, timeout=None, input=None):
        self.calls.append(argv)
        for match, answer in self.rules:
            if match(argv):
                if isinstance(answer, BaseException):
                    raise answer
                if callable(answer):
                    answer = answer(argv)
                code, out = answer if isinstance(answer, tuple) else (0, answer)
                return subprocess.CompletedProcess(argv, code, out, "" if code == 0 else "remote said no")
        return subprocess.CompletedProcess(argv, 0, "", "")


def has(*parts):
    return lambda argv: all(p in argv for p in parts)


def gh_api(fragment):
    return lambda argv: argv[:2] == ["gh", "api"] and fragment in argv[2]


RUNS = {"workflow_runs": [
    {"id": 1, "path": ".github/workflows/ci.yml", "created_at": "2026-11-25T10:00:00Z"},
    {"id": 2, "path": ".github/workflows/ci.yml", "created_at": "2026-11-25T11:00:00Z"},
    {"id": 3, "path": ".github/workflows/codeql.yml", "created_at": "2026-11-25T12:00:00Z"},
]}


def fake_download(argv):
    d = argv[argv.index("-D") + 1]
    open(os.path.join(d, "Orbweaver-1.0.13-win-x64-setup.exe"), "w").close()
    return ""


class WindowsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.cfg = {"state_dir": self.tmp.name, "repo": "o/r", "machines": {"windows": {"ssh": "winbox"}}}

    def tearDown(self):
        self.tmp.cleanup()

    def machine(self, rules):
        sh = FakeSh(rules)
        return windows.WindowsMachine(self.cfg, sh=sh), sh

    def install_rules(self, **over):
        rules = [
            (gh_api("actions/runs?head_sha="), json.dumps(RUNS)),
            (gh_api("runs/2/artifacts"), json.dumps({"artifacts": [{"name": "unsigned-win-x64", "expired": False}]})),
            (gh_api("/artifacts"), json.dumps({"artifacts": []})),
            (has("gh", "run", "download"), fake_download),
            (has("-Action", "stamp"), "1.0.13+" + SHA + "\r\n"),
        ]
        return over.get("first", []) + rules

    def test_parse_stamp(self):
        self.assertEqual(windows.parse_stamp("1.0.13+" + SHA.upper() + "\r\n"), SHA)
        self.assertEqual(windows.parse_stamp("1.0.13-beta+abcdef1\n"), "abcdef1")
        self.assertIsNone(windows.parse_stamp(""))
        self.assertIsNone(windows.parse_stamp("1.0.13\n"))
        self.assertIsNone(windows.parse_stamp("1.0.13+abc\n"))

    def test_pick_artifact_run_newest_ci_with_live_artifact(self):
        arts = {1: [{"name": "unsigned-win-x64"}], 2: [{"name": "unsigned-win-x64", "expired": True}],
                3: [{"name": "unsigned-win-x64"}]}
        self.assertEqual(windows.pick_artifact_run(RUNS["workflow_runs"], arts.get), 1)
        arts[2] = [{"name": "unsigned-win-x64"}]
        self.assertEqual(windows.pick_artifact_run(RUNS["workflow_runs"], arts.get), 2)
        self.assertIsNone(windows.pick_artifact_run(RUNS["workflow_runs"], lambda i: []))

    def test_install_downloads_copies_keeps_then_installs(self):
        m, sh = self.machine(self.install_rules())
        lines = []
        m.install(7, SHA, lines.append)
        self.assertEqual(m.installed_commit(), SHA)
        dl = [a for a in sh.calls if a[:3] == ["gh", "run", "download"]][0]
        self.assertEqual(dl[3:8], ["2", "--repo", "o/r", "-n", "unsigned-win-x64"])
        scps = [a for a in sh.calls if a[0] == "scp"]
        self.assertTrue(scps[0][-2].endswith("windows.ps1"))
        self.assertEqual(scps[0][-1], "winbox:orbweaver-machines/ow-machine.ps1")
        self.assertTrue(scps[1][-2].endswith("Orbweaver-1.0.13-win-x64-setup.exe"))
        actions = [a[a.index("-Action") + 1] for a in sh.calls if "-Action" in a]
        self.assertEqual(actions[:2], ["keep", "install"])
        # The script is copied once per machine object, not once per step.
        self.assertEqual(sum(1 for a in scps if a[-2].endswith("windows.ps1")), 1)
        self.assertTrue(any("windows.ps1 install: exit 0" in l for l in lines))

    def test_no_inno_switch_ever_reaches_the_git_bash_shell(self):
        m, sh = self.machine(self.install_rules())
        m.install(7, SHA, lambda s: None)
        m.restore(lambda s: None)
        for argv in sh.calls:
            if argv[0] == "ssh":
                self.assertFalse(any(a.startswith("/") for a in argv), argv)
        # Control: the switches do exist, inside the script.
        with open(windows.SCRIPT) as f:
            ps1 = f.read()
        self.assertIn("'/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'", ps1)

    def test_no_artifact_yet_is_a_clear_error(self):
        m, _ = self.machine([(gh_api("actions/runs?head_sha="), json.dumps({"workflow_runs": []}))])
        with self.assertRaises(base.MachineError) as e:
            m.install(7, SHA, lambda s: None)
        self.assertIn("no unsigned-win-x64 artifact", str(e.exception))

    def test_failed_remote_step_raises_with_its_output(self):
        m, _ = self.machine(self.install_rules(first=[(has("-Action", "install"), (1, "setup exited 5"))]))
        with self.assertRaises(base.MachineError) as e:
            m.install(7, SHA, lambda s: None)
        self.assertIn("windows.ps1 install exited 1", str(e.exception))

    def test_install_step_timeout_is_a_machine_error(self):
        m, _ = self.machine(self.install_rules(first=[(has("-Action", "keep"),
                                                       subprocess.TimeoutExpired("ssh", 120))]))
        with self.assertRaises(base.MachineError) as e:
            m.install(7, SHA, lambda s: None)
        self.assertIn("timed out", str(e.exception))

    def test_more_than_one_installer_is_refused(self):
        def two(argv):
            d = argv[argv.index("-D") + 1]
            for v in ("1.0.12", "1.0.13"):
                open(os.path.join(d, f"Orbweaver-{v}-win-x64-setup.exe"), "w").close()
            return ""
        m, _ = self.machine([(has("gh", "run", "download"), two)] + self.install_rules())
        with self.assertRaises(base.MachineError) as e:
            m.install(7, SHA, lambda s: None)
        self.assertIn("holds 2 installers", str(e.exception))

    def test_stamp_timeout_without_a_log_is_still_an_error(self):
        m, _ = self.machine([(has("-Action", "stamp"), subprocess.TimeoutExpired("ssh", 120))])
        with self.assertRaises(base.MachineError) as e:
            m.installed_commit()
        self.assertIn("windows.ps1 stamp timed out", str(e.exception))

    def test_restore_and_unreachable_stamp(self):
        m, sh = self.machine([])
        m.restore(lambda s: None)
        self.assertIn("restore", sh.calls[-1])
        m, _ = self.machine([(has("ssh", "mkdir"), (255, ""))])
        with self.assertRaises(base.MachineError):
            m.installed_commit()

    def test_run_passes_the_command_as_one_remote_argument(self):
        m, sh = self.machine([(lambda a: a[0] == "ssh", (2, "out"))])
        r = m.run("ls /c && echo 'a b'", 30)
        self.assertEqual(sh.calls[-1][-2:], ["winbox", "ls /c && echo 'a b'"])
        self.assertEqual((r.exit_code, r.stdout, r.timed_out), (2, "out", False))

    def test_run_timeout(self):
        m, _ = self.machine([(lambda a: a[0] == "ssh",
                              subprocess.TimeoutExpired("ssh", 5, output=b"part", stderr=None))])
        r = m.run("sleep 100", 5)
        self.assertEqual((r.exit_code, r.stdout, r.stderr, r.timed_out), (None, "part", "", True))

    def test_script_rules_from_the_windows_notes(self):
        with open(windows.SCRIPT) as f:
            ps1 = f.read()
        code = "\n".join(l for l in ps1.splitlines() if not l.lstrip().startswith("#"))
        self.assertNotIn("CLOSEAPPLICATIONS", code)
        self.assertIn("-LogonType Interactive", code)
        self.assertIn("-UserId $env:USERNAME", code)
        self.assertIn("if (Test-Path $Keep) { 'already kept'; break }", code)
        self.assertIn("Rename-Item \"$Keep.tmp\" $Keep", code)
        self.assertIn("VersionInfo.ProductVersion", code)


# ---------------------------------------------------------------- wiring

class WiringTests(unittest.TestCase):
    def test_plist_runs_run_once_every_minute_with_gh_on_path(self):
        with open(os.path.join(ROOT, "host/launchd/org.uplift.orbweaver-bugfix-machines.plist"), "rb") as f:
            p = plistlib.load(f)
        self.assertEqual(p["Label"], "org.uplift.orbweaver-bugfix-machines")
        self.assertEqual(p["ProgramArguments"], ["/usr/bin/python3", "__REPO__/host/machines/runner.py", "run-once"])
        self.assertEqual(p["StartInterval"], 60)
        self.assertIn("/opt/homebrew/bin", p["EnvironmentVariables"]["PATH"])

    def test_ci_runs_this_file(self):
        with open(os.path.join(ROOT, ".github/workflows/ci.yml")) as f:
            self.assertIn("run: python tools/test-bugfix-machines.py", f.read())

    def test_runner_runs_as_a_script_with_the_system_python(self):
        # launchd runs /usr/bin/python3 on the file path, not -m; the import
        # shim at the top of runner.py is what makes that work.
        r = subprocess.run([sys.executable, os.path.join(ROOT, "host/machines/runner.py"), "--help"],
                           capture_output=True, text=True, cwd=tempfile.gettempdir())
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("run-once", r.stdout)


if __name__ == "__main__":
    unittest.main()
