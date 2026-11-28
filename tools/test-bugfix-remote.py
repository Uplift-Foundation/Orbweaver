#!/usr/bin/env python3
"""Tests for the bug-fix org's remote test runner (CB-494):
host/machines/remote.py, its launchd plist and the CI wiring.

    python3 tools/test-bugfix-remote.py

stdlib unittest. Nothing here reaches a machine, GitHub or ssh: the runner is
driven with a fake mirror, fake targets and a fake clock, and the real Mirror
and Target with a fake `sh` that records every argv. Each refusal has a passing
control beside it, so a rule that goes blind turns red instead of passing for
the wrong reason.

One class runs the target's own shell scripts for real, on this machine, in a
scratch HOME with `bash -s` standing where `ssh <host> bash -s` would be: the
checkout of a dirty worktree, the detached start, the poll, and the kill of a
job's whole process tree are shell, and a fake cannot say whether shell works.
It needs bash, git and perl and skips where they are missing (Windows).

Not covered, and said plainly: ssh itself, the .NET SDK, and the Air. Those
are the real-machine proof in the PR.
"""
import io
import json
import os
import plistlib
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, ROOT)

from host.machines import base, remote, runner  # noqa: E402

SHA = "a" * 40
SHA2 = "b" * 40
BASE = "c" * 40
MINI = "Warrens-Mac-mini.local"


# ---------------------------------------------------------------- fakes

class Clock:
    def __init__(self, t=1_800_000_000.0):
        self.t = t

    def __call__(self):
        return self.t


class FakeMirror:
    def __init__(self, pushed=(SHA, SHA2), readable=True):
        self.pushed, self.readable, self.asked, self.pushes = set(pushed), readable, [], []
        self.push_error = None

    def on_branch(self, sha):
        self.asked.append(sha)
        return (sha in self.pushed) if self.readable else None

    def base_sha(self):
        return BASE

    def push(self, url, sha, rid):
        if self.push_error:
            raise base.MachineError(self.push_error)
        self.pushes.append((url, sha, rid))


class FakeTarget:
    """A target that records everything asked of it. `state` is what poll
    answers; `head` is what the worktree is at after a checkout (None = the
    commit asked for)."""

    def __init__(self, name="air", hostname="air.internal", reachable=True):
        self.name, self.hostname, self.reachable = name, hostname, reachable
        self.push_url = f"{name}:orbweaver-remote/repo.git"
        self.calls, self.state, self.code, self.head = [], "running", 0, None
        self.output, self.files, self.dropped = "ok\n", {}, []
        self.start_error = self.collect_error = None
        self.busy = None  # the id of a job the target itself still has running
        self.is_this_machine = False  # the start script found itself on a forbidden host
        self.tmux = True
        self.load = [1.5, 1.0, 0.5]

    def probe(self):
        self.calls.append(("probe",))
        return {"reachable": self.reachable, "load": self.load if self.reachable else None,
                "hostname": self.hostname if self.reachable else None,
                "tmux": self.tmux if self.reachable else None}

    def init(self):
        self.calls.append(("init",))

    def start(self, rid, sha, argv, cap_s, files=(), clean_all=False):
        self.calls.append(("start", rid, sha, tuple(argv), clean_all, cap_s, tuple(files)))
        if self.start_error:
            raise base.MachineError(self.start_error)
        if self.is_this_machine:
            raise remote.LocalTarget("the target reports hostname 'avatar.internal', which is this machine; "
                                     "not run on the mini")
        if self.busy:
            return {"sha": None, "hostname": None, "started": False, "busy": self.busy}
        got = self.head or sha
        return {"sha": got, "hostname": self.hostname, "started": got == sha, "busy": None}

    def poll(self, rid):
        self.calls.append(("poll", rid))
        if not self.reachable:
            return "unreachable", None, None
        return self.state, (self.code if self.state == "done" else None), self.load

    def kill(self, rid):
        self.calls.append(("kill", rid))

    def collect(self, rid, limit, allowed=(), png=False):
        self.calls.append(("collect", rid, tuple(allowed), png))
        if self.collect_error:
            raise base.MachineError(self.collect_error)
        return self.output, dict(self.files), list(self.dropped)

    def cleanup(self, rid):
        self.calls.append(("cleanup", rid))

    def did(self, what):
        return [c for c in self.calls if c[0] == what]


def cfg(**kw):
    c = dict(remote.DEFAULTS)
    c.update(kw)
    return c


class Harness:
    def __init__(self, mirror=None, targets=None, clock=None, state=None, **kw):
        self.mirror = mirror or FakeMirror()
        self.air = FakeTarget()
        self.targets = {"air": self.air} if targets is None else targets
        self.clock = clock or Clock()
        self.logs, self.results, self.saves = [], {}, []
        self.state = {} if state is None else state
        self.remote = remote.Remote(cfg(**kw), self.mirror, self.targets, self.state, self.clock,
                                    self.logs.append, lambda s: self.saves.append(json.dumps(s)),
                                    self.write, MINI)

    def write(self, rid, result, output, files):
        assert rid not in self.results, f"{rid} answered twice"
        self.results[rid] = (result, output, files)

    def ask(self, *reqs):
        self.remote.accept([(r, None) for r in reqs])

    def res(self, rid):
        return self.results[rid][0]


def req(i="t1", sha=SHA, run="ui", flt=None):
    r = {"verb": "remote_test", "id": i, "sha": sha, "run": run}
    if flt is not None:
        r["filter"] = flt
    return r


FIELDS = {"id", "verb", "sha", "run", "filter", "ok", "refused", "queued_at", "started_at", "finished_at",
          "target", "target_hostname", "checked_out_sha", "exit_code", "timed_out", "files", "error"}


# ---------------------------------------------------------------- pure rules

class ParseTests(unittest.TestCase):
    def parse(self, **kw):
        r = req()
        r.update(kw)
        r = {k: v for k, v in r.items() if v is not ...}
        return remote.parse_request(json.dumps(r))

    def test_every_run_parses_and_filters_only_where_allowed(self):
        for run, (_, style, _, _) in remote.RUNS.items():
            self.assertIsNone(self.parse(run=run)[1], run)
            got = self.parse(run=run, filter="Orbweaver.Tests.ChatPanelTests")[1]
            if style is None:
                self.assertEqual(got, f"run {run!r} takes no filter")
            else:
                self.assertIsNone(got, run)

    def test_a_run_outside_the_table_is_refused_control_inside_passes(self):
        for bad in ("dotnet test", "shell", "", "UI", "ui ", "coverage;ls", None, 3, ["ui"]):
            partial, why = self.parse(run=bad)
            self.assertIn("run must be one of", why, repr(bad))
            self.assertEqual(partial["id"], "t1")
        self.assertIsNone(self.parse(run="ui")[1])

    def test_there_is_no_command_field(self):
        self.assertEqual(self.parse(command="rm -rf ~")[1], "unknown field(s): command")
        self.assertEqual(self.parse(timeout_s=5, machine="mini")[1], "unknown field(s): machine, timeout_s")

    def test_shell_metacharacters_in_a_filter_are_refused_control_a_class_name_passes(self):
        for bad in ("A;rm -rf ~", "A B", "A$(id)", "A`id`", "A|B", "A&B", "A>B", "A<B", "A'B", 'A"B',
                    "A\\B", "A\nB", "A*", "A~B", "A/B", "-x", "--filter", "A=B", "A(B)", "", "é",
                    "A" * 201, 5, None, ["A"]):
            self.assertIn("filter must be a class name", self.parse(filter=bad)[1], repr(bad))
        for good in ("ChatPanelTests", "Orbweaver.Tests.ChatPanelTests", "A_b.C9", "A" * 200):
            self.assertIsNone(self.parse(filter=good)[1], good)

    def test_sha_must_be_full_lowercase_hex(self):
        for bad in ("abc1234", "A" * 40, "g" * 40, "a" * 39, "a" * 41, "HEAD", "develop", None, 7):
            self.assertEqual(self.parse(sha=bad)[1], "sha must be a full 40-character lowercase commit", repr(bad))
        self.assertIsNone(self.parse(sha=SHA)[1])

    def test_id_verb_and_shape(self):
        self.assertEqual(remote.parse_request("{")[1], "not JSON")
        self.assertEqual(remote.parse_request("[]"), (None, "not a JSON object"))
        self.assertEqual(self.parse(id="Bad Id"), (None, "id must match [a-z0-9-]{1,40}"))
        partial, why = self.parse(verb="machine_run")
        self.assertEqual((partial, why), ({"id": "t1", "verb": None}, "unknown verb 'machine_run'"))
        self.assertEqual(self.parse(sha=...)[1], "missing field(s): sha")
        self.assertEqual(self.parse(run=...)[1], "missing field(s): run")


class CommandTests(unittest.TestCase):
    def test_each_run_is_the_command_ci_and_the_skill_name(self):
        c = remote.command_for
        # Not the root, where `dotnet build` picks the app-only Orbweaver.sln
        # and a broken test project would still answer green (QA round 1).
        self.assertEqual(c("build"), (["dotnet", "build", "tests/Tests.sln", "-c", "Release"], 600))
        self.assertEqual(c("unit", "OrbGlyph")[0][-2:], ["--filter", "FullyQualifiedName~OrbGlyph"])
        self.assertEqual(c("integration", "X")[0][:3], ["dotnet", "test", "tests/IntegrationTests"])
        ui, cap = c("ui", "Orbweaver.Tests.ChatPanelTests")
        self.assertEqual(ui[:5], ["dotnet", "test", "tests/UiTests", "-c", "Release"])
        # The MTP suites do not read --filter at all (CB-270): the class goes
        # after `--`, as --filter-class.
        self.assertGreater(ui.index("--filter-class"), ui.index("--"))
        self.assertNotIn("--filter", ui)
        self.assertEqual(cap, 600)
        self.assertEqual(c("screenshots")[0][2], "tests/UiScreenshots")
        for name, proj in (("arrangement", "ArrangementTests"), ("glyph", "GlyphTests"),
                           ("transcript", "TranscriptTests")):
            self.assertEqual(c(name)[0], ["dotnet", "run", "--project", f"tests/{proj}", "-c", "Release"])
        self.assertEqual(c("coverage"), (["./tools/coverage.sh", "--base", "origin/develop"], 45 * 60))

    def test_a_full_suite_gets_the_longer_cap(self):
        self.assertEqual(remote.command_for("ui")[1], 30 * 60)
        self.assertEqual(remote.command_for("ui", "A")[1], 10 * 60)

    def test_a_filter_on_a_run_that_takes_none_cannot_be_built(self):
        with self.assertRaises(ValueError):
            remote.command_for("coverage", "A")

    def test_the_table_has_no_shell_and_no_other_program(self):
        for run, (argv, _, _, _) in remote.RUNS.items():
            self.assertIn(argv[0], ("dotnet", "./tools/coverage.sh"), run)


class RuleTests(unittest.TestCase):
    def test_sha_problem_is_three_way(self):
        self.assertIsNone(remote.sha_problem(True))
        self.assertIn("not on any branch", remote.sha_problem(False))
        self.assertIn("could not read", remote.sha_problem(None))

    def test_the_mini_is_never_a_target_however_it_is_spelled(self):
        ok = [{"name": "air", "ssh": "air"}]
        self.assertIsNone(remote.targets_problem(ok, MINI))
        for name, host in (("mini", "air"), ("air", "mini"), ("air", "macmini"), ("air", "localhost"),
                           ("air", "127.0.0.1"), ("air", "127.0.0.2"), ("air", "Warrens-Mac-mini"),
                           ("air", "warrens-mac-mini.local"), ("localhost", "air")):
            why = remote.targets_problem([{"name": name, "ssh": host}], MINI)
            self.assertIn("the mini is never a target", why or "", (name, host))

    def test_an_alias_that_resolves_to_this_machine_is_refused(self):
        t = [{"name": "air", "ssh": "air"}]
        for resolved in ("localhost", "127.0.0.1", "warrens-mac-mini.local", "::1"):
            self.assertIn("never a target", remote.targets_problem(t, MINI, lambda h: resolved), resolved)
        self.assertIsNone(remote.targets_problem(t, MINI, lambda h: "192.168.0.50"))

    def test_an_alias_pointed_at_one_of_this_machines_addresses_is_refused(self):
        t = [{"name": "air", "ssh": "air"}]
        mine = ("192.168.0.127", "fe80::1c2d:3e4f:5a6b:7c8d", "10.0.0.5")
        for resolved in mine + ("FE80::1C2D:3E4F:5A6B:7C8D",):
            why = remote.targets_problem(t, MINI, lambda h: resolved, mine)
            self.assertIn("never a target", why or "", resolved)
        # A name that resolves, in turn, to one of them.
        why = remote.targets_problem(t, MINI, lambda h: "sneaky.example", mine, lambda h: ("192.168.0.127",))
        self.assertIn("(192.168.0.127)", why)
        # Controls: another machine's address, by either route.
        self.assertIsNone(remote.targets_problem(t, MINI, lambda h: "192.168.0.50", mine))
        self.assertIsNone(remote.targets_problem(t, MINI, lambda h: "air.internal", mine, lambda h: ("192.168.0.50",)))

    def test_local_addresses_reads_the_interfaces_and_the_hostname(self):
        out = ("lo0: flags=8049\n\tinet 127.0.0.1 netmask 0xff000000\n\tinet6 ::1 prefixlen 128\n"
               "en0: flags=8863\n\tinet6 fe80::10ab:cdef:1234:5678%en0 prefixlen 64 scopeid 0xb\n"
               "\tinet 192.168.0.127 netmask 0xffffff00 broadcast 192.168.0.255\n")
        sh = lambda argv, timeout=None, input=None: subprocess.CompletedProcess(argv, 0, out, "")  # noqa: E731
        got = remote.local_addresses("no-such-host.invalid", sh)
        self.assertEqual(got, ("127.0.0.1", "192.168.0.127", "::1", "fe80::10ab:cdef:1234:5678"))

        def gone(argv, timeout=None, input=None):
            raise OSError("no ifconfig")
        self.assertEqual(remote.local_addresses("no-such-host.invalid", gone), ())
        self.assertEqual(remote.addresses_of("no-such-host.invalid"), ())
        self.assertTrue(remote.addresses_of("localhost"))

    def test_target_list_shape(self):
        p = remote.targets_problem
        self.assertEqual(p([], MINI), "targets must be a non-empty list")
        self.assertEqual(p("air", MINI), "targets must be a non-empty list")
        self.assertIn("each target is", p(["air"], MINI))
        self.assertIn("each target is", p([{"name": "air", "ssh": "air", "local": True}], MINI))
        self.assertIn("unique", p([{"name": "air", "ssh": "air"}, {"name": "air", "ssh": "b"}], MINI))
        for host in ("-oProxyCommand=id", "user@air", "air -p 22", "", "air;id"):
            self.assertIn("one host alias", p([{"name": "air", "ssh": host}], MINI), host)
        self.assertIsNone(p([{"name": "air", "ssh": "air"}, {"name": "macbook", "ssh": "mbp"}], MINI))

    def test_pick_target_is_the_first_that_answers(self):
        self.assertEqual(remote.pick_target(["air", "macbook"], {"air": True, "macbook": True}), "air")
        self.assertEqual(remote.pick_target(["air", "macbook"], {"air": False, "macbook": True}), "macbook")
        self.assertIsNone(remote.pick_target(["air"], {"air": False}))
        self.assertIsNone(remote.pick_target([], {}))

    def test_wait_problem_counts_only_time_spent_unreachable(self):
        w = remote.wait_problem
        self.assertIsNone(w(0, None, 10_000, 900))            # reachable: waits as long as it takes
        self.assertIsNone(w(0, 100, 999, 900))                # 899 s unreachable
        self.assertEqual(w(0, 100, 1000, 900), "no target reachable; not run on the mini")
        self.assertIsNone(w(500, 100, 1399, 900))             # arrived during the outage
        self.assertIsNotNone(w(500, 100, 1400, 900))

    def test_queue_start_and_deadline(self):
        self.assertIsNone(remote.queue_problem(9, 10))
        self.assertIn("already holds 10", remote.queue_problem(10, 10))
        self.assertTrue(remote.may_start(None, [1]))
        self.assertFalse(remote.may_start({"req": {}}, [1]))
        self.assertFalse(remote.may_start(None, []))
        self.assertFalse(remote.timed_out({"deadline": 100}, 99))
        self.assertTrue(remote.timed_out({"deadline": 100}, 100))

    def test_checkout_problem(self):
        c = remote.checkout_problem
        self.assertIsNone(c(SHA, SHA, "air.internal", MINI))
        self.assertIn(f"is at {SHA2}, not {SHA}", c(SHA, SHA2, "air.internal", MINI))
        self.assertIn("no commit", c(SHA, None, "air.internal", MINI))
        self.assertIn("not run on the mini", c(SHA, SHA, "Warrens-Mac-mini.local", MINI))
        self.assertIn("not run on the mini", c(SHA, SHA, "warrens-mac-mini", MINI))

    def test_coverage_summary(self):
        out = "build noise\nmerged 3 report(s)\nold\nmore noise\nmerged 4 report(s)\n\nlines 90%\n"
        summary, n = remote.coverage_summary(out)
        self.assertEqual(n, 4)
        self.assertTrue(summary.startswith("merged 4 report(s)\n"))
        self.assertIn("lines 90%", summary)
        self.assertEqual(remote.coverage_summary("REFUSING to print a coverage figure: merged 1 report(s), x"),
                         (None, None))
        self.assertEqual(remote.coverage_summary(""), (None, None))

    def test_job_ok(self):
        ok = remote.job_ok
        self.assertTrue(ok("ui", 0, False, None))
        self.assertFalse(ok("ui", 1, False, None))
        self.assertFalse(ok("ui", 0, True, None))
        self.assertFalse(ok("ui", None, False, None))
        self.assertTrue(ok("coverage", 0, False, 4))
        for merged in (1, 3, 5, 6, None):
            self.assertFalse(ok("coverage", 0, False, merged), merged)
        self.assertFalse(ok("coverage", 1, False, 4))

    def test_safe_members_keeps_only_the_runners_own_names(self):
        names = [("output.txt", 10), ("files/unit-tests.trx", 10), ("files/other.trx", 1),
                 ("files/../../etc/passwd", 1), ("/etc/passwd", 1), ("files/sub/unit-tests.trx", 1),
                 ("run.sh", 1), ("files/screenshots/chat-panel.png", 5), ("files/.hidden", 1)]
        kept, dropped = remote.safe_members(names, 50, ["unit-tests.trx"])
        self.assertEqual(kept, ["output.txt", "files/unit-tests.trx"])
        self.assertEqual(len(dropped), 7)
        # Control: the same archive with nothing allowed keeps only the output.
        self.assertEqual(remote.safe_members(names, 50)[0], ["output.txt"])

    def test_safe_members_takes_flat_png_names_only_for_a_screenshot_run(self):
        names = [("files/screenshots/chat-panel.png", 5), ("files/screenshots/../../x.png", 1),
                 ("files/screenshots/a/b.png", 1), ("files/screenshots/.x.png", 1),
                 ("files/screenshots/x.sh", 1), ("files/screenshots/a b.png", 1), ("files/x.png", 1)]
        self.assertEqual(remote.safe_members(names, 50, [], png=True)[0], ["files/screenshots/chat-panel.png"])
        self.assertEqual(remote.safe_members(names, 50, [], png=False)[0], [])

    def test_safe_members_stops_at_the_byte_limit(self):
        names = [("output.txt", 30), ("files/a.trx", 30), ("files/b.trx", 10)]
        kept, dropped = remote.safe_members(names, 50, ["a.trx", "b.trx"])
        self.assertEqual((kept, dropped), (["output.txt", "files/b.trx"], ["files/a.trx"]))

    def test_tool_problem_refuses_only_the_runs_that_need_tmux_and_only_without_it(self):
        sentence = "the target has no tmux, which IntegrationTests needs; not run"
        for run in remote.RUNS:
            want = sentence if run in ("integration", "coverage") else None
            self.assertEqual(remote.tool_problem(run, {"tmux": False}), want, run)
            self.assertEqual(remote.tool_problem(run, {"tmux": None}), want, run)
            self.assertEqual(remote.tool_problem(run, {}), want, run)
            self.assertIsNone(remote.tool_problem(run, {"tmux": True}), run)

    def test_busy_problem(self):
        self.assertIsNone(remote.busy_problem({"busy": None}))
        self.assertIsNone(remote.busy_problem({}))
        self.assertEqual(remote.busy_problem({"busy": "old1"}), "the target is still running job old1")

    def test_parse_load(self):
        self.assertEqual(remote.parse_load("air.internal\n{ 1.96 2.00 1.98 }\n"), [1.96, 2.0, 1.98])
        self.assertIsNone(remote.parse_load("nothing"))


# ---------------------------------------------------------------- the runner

class FlowTests(unittest.TestCase):
    def test_a_request_runs_on_the_air_and_is_answered_in_full(self):
        h = Harness()
        h.ask(req("t1", flt="Orbweaver.Tests.ChatPanelTests"))
        self.assertEqual(h.results, {})
        st = h.remote.step()
        self.assertEqual(st["running"]["id"], "t1")
        self.assertEqual(st["targets"]["air"], {"reachable": True, "load": [1.5, 1.0, 0.5],
                                                "hostname": "air.internal", "tmux": True})
        self.assertEqual(h.mirror.pushes, [("air:orbweaver-remote/repo.git", SHA, "t1")])
        start = h.air.did("start")[0]
        self.assertEqual(start[1:3], ("t1", SHA))
        self.assertIn("--filter-class", start[3])
        self.assertFalse(start[4])
        h.remote.step()
        self.assertEqual(h.results, {})  # still running: no answer yet
        h.air.state, h.air.files, h.air.output = "done", {"ui-tests.xunit.xml": b"<x/>"}, "Passed!\n"
        h.clock.t += 20
        st = h.remote.step()
        r, out, files = h.results["t1"]
        self.assertEqual(set(r), FIELDS)
        self.assertEqual((r["ok"], r["refused"], r["exit_code"], r["timed_out"], r["error"]),
                         (True, None, 0, False, None))
        self.assertEqual((r["target"], r["target_hostname"], r["checked_out_sha"]), ("air", "air.internal", SHA))
        self.assertEqual(r["files"], ["ui-tests.xunit.xml"])
        self.assertEqual(out, "Passed!\n")
        self.assertIsNone(st["running"])
        self.assertEqual(h.air.did("cleanup"), [("cleanup", "t1")])

    def test_a_failing_run_is_answered_not_ok_with_its_exit_code(self):
        h = Harness()
        h.ask(req())
        h.remote.step()
        h.air.state, h.air.code = "done", 2
        h.remote.step()
        self.assertEqual((h.res("t1")["ok"], h.res("t1")["exit_code"], h.res("t1")["refused"]), (False, 2, None))

    def test_a_sha_that_is_not_pushed_is_refused_before_any_target_is_touched(self):
        h = Harness()
        h.ask(req("bad", sha="d" * 40), req("good", sha=SHA))
        self.assertIn("not on any branch", h.res("bad")["refused"])
        self.assertEqual(set(h.res("bad")), FIELDS)
        self.assertEqual(h.air.calls, [])  # refused at arrival: no probe, no push, no start
        self.assertEqual(h.mirror.pushes, [])
        h.remote.step()
        self.assertEqual([c[1] for c in h.air.did("start")], ["good"])  # the control ran

    def test_github_unreadable_fails_closed_control_readable_queues(self):
        h = Harness(mirror=FakeMirror(readable=False))
        h.ask(req())
        self.assertIn("could not read the repository", h.res("t1")["refused"])
        self.assertEqual(h.state["queue"], [])
        h2 = Harness()
        h2.ask(req())
        self.assertEqual(len(h2.state["queue"]), 1)

    def test_a_second_request_waits_for_the_first_and_starts_in_arrival_order(self):
        h = Harness()
        h.ask(req("one"), req("two", sha=SHA2), req("three"))
        st = h.remote.step()
        self.assertEqual([c[1] for c in h.air.did("start")], ["one"])
        self.assertEqual([q["id"] for q in st["queued"]], ["two", "three"])
        for _ in range(5):
            h.remote.step()
        self.assertEqual([c[1] for c in h.air.did("start")], ["one"])  # still one, however often it looks
        h.air.state = "done"
        h.remote.step()  # one finishes; two starts in the same look
        self.assertIn("one", h.results)
        self.assertEqual([c[1] for c in h.air.did("start")], ["one", "two"])
        self.assertNotIn("two", h.results)
        h.remote.step()
        h.remote.step()
        self.assertEqual([c[1] for c in h.air.did("start")], ["one", "two", "three"])
        self.assertEqual(sorted(h.results), ["one", "three", "two"])

    def test_a_full_queue_refuses_control_one_below_is_queued(self):
        h = Harness(queue_cap=2)
        h.ask(req("a"), req("b"), req("c"))
        self.assertEqual(len(h.state["queue"]), 2)
        self.assertIn("already holds 2", h.res("c")["refused"])
        self.assertEqual(h.mirror.asked, [SHA, SHA])  # the refused one cost no GitHub read either

    def test_a_repeated_id_that_is_waiting_or_running_is_dropped(self):
        h = Harness()
        h.ask(req("a"), req("a"))
        self.assertEqual(len(h.state["queue"]), 1)
        h.remote.step()
        h.ask(req("a"))
        self.assertEqual(h.state["queue"], [])
        self.assertEqual(h.results, {})

    def test_air_unreachable_waits_then_refuses_and_never_runs_anywhere(self):
        h = Harness()
        h.air.reachable = False
        h.ask(req())
        st = h.remote.step()
        self.assertEqual(st["targets"]["air"]["reachable"], False)
        self.assertIsNotNone(st["unreachable_since"])
        h.clock.t += 899
        h.remote.step()
        self.assertEqual(h.results, {})  # control: one second short, still waiting
        h.clock.t += 1
        h.remote.step()
        r = h.res("t1")
        self.assertEqual((r["ok"], r["refused"], r["target"], r["started_at"]),
                         (False, "no target reachable; not run on the mini", None, None))
        self.assertEqual(h.air.did("start") + h.air.did("init"), [])
        self.assertEqual(h.mirror.pushes, [])

    def test_air_back_within_the_wait_runs_the_request(self):
        h = Harness()
        h.air.reachable = False
        h.ask(req())
        h.remote.step()
        h.clock.t += 600
        h.air.reachable = True
        st = h.remote.step()
        self.assertEqual(st["running"]["id"], "t1")
        self.assertIsNone(st["unreachable_since"])

    def test_time_queued_behind_a_job_does_not_count_toward_the_wait(self):
        h = Harness()
        h.ask(req("one"), req("two"))
        h.remote.step()
        h.clock.t += 1200  # twenty minutes behind a running job
        h.air.state = "done"
        h.remote.step()    # one answered, two started
        self.assertEqual([c[1] for c in h.air.did("start")], ["one", "two"])
        self.assertNotIn("two", h.results)

    def test_no_targets_at_all_refuses_after_the_wait(self):
        h = Harness(targets={})
        h.ask(req())
        h.remote.step()
        h.clock.t += 900
        h.remote.step()
        self.assertEqual(h.res("t1")["refused"], "no target reachable; not run on the mini")

    def test_a_second_target_is_used_only_while_the_first_is_silent(self):
        air, mb = FakeTarget("air"), FakeTarget("macbook", "mbp.internal")
        h = Harness(targets={"air": air, "macbook": mb})
        h.ask(req("a"))
        h.remote.step()
        self.assertEqual((len(air.did("start")), len(mb.did("start"))), (1, 0))
        air.state = "done"
        h.remote.step()
        air.reachable = False
        h.ask(req("b"))
        h.remote.step()
        self.assertEqual(len(mb.did("start")), 1)
        mb.state = "done"
        h.remote.step()
        self.assertEqual((h.res("b")["target"], h.res("b")["target_hostname"]), ("macbook", "mbp.internal"))

    def test_a_job_at_its_cap_is_killed_and_answered_timed_out(self):
        h = Harness()
        h.ask(req(flt="A.B"))
        h.remote.step()
        h.clock.t += 599
        h.remote.step()
        self.assertEqual(h.air.did("kill"), [])  # control: one second short
        h.clock.t += 1
        h.air.output = "partial\n"
        h.remote.step()
        self.assertEqual(h.air.did("kill"), [("kill", "t1")])
        r = h.res("t1")
        self.assertEqual((r["ok"], r["timed_out"], r["exit_code"], r["refused"]), (False, True, None, None))
        self.assertIn("600 s cap", r["error"])
        self.assertEqual(h.results["t1"][1], "partial\n")
        self.assertIsNone(h.state["running"])

    def test_a_target_that_goes_silent_mid_job_is_waited_on_until_the_cap(self):
        h = Harness()
        h.ask(req(flt="A.B"))
        h.remote.step()
        h.air.reachable = False
        h.clock.t += 300
        h.remote.step()
        self.assertEqual(h.results, {})
        h.air.reachable = True
        h.air.state = "done"
        h.remote.step()
        self.assertTrue(h.res("t1")["ok"])  # it came back and the job had finished

    def test_a_job_that_vanished_is_answered_as_unknown(self):
        h = Harness()
        h.ask(req())
        h.remote.step()
        h.air.state = "lost"
        h.remote.step()
        self.assertEqual((h.res("t1")["ok"], h.res("t1")["exit_code"]), (False, None))
        self.assertIn("gone from the target", h.res("t1")["error"])

    def test_a_worktree_left_at_another_commit_is_not_run_and_the_result_names_what_was_there(self):
        h = Harness()
        h.air.head = SHA2
        h.ask(req(sha=SHA))
        h.remote.step()
        r = h.res("t1")
        self.assertEqual((r["ok"], r["sha"], r["checked_out_sha"], r["exit_code"]), (False, SHA, SHA2, None))
        self.assertIn(f"is at {SHA2}, not {SHA}", r["error"])
        self.assertIsNone(h.state["running"])
        self.assertEqual(h.air.did("cleanup"), [("cleanup", "t1")])
        # Control: the same request against a worktree that took the checkout.
        h2 = Harness()
        h2.ask(req(sha=SHA))
        h2.remote.step()
        self.assertEqual(h2.state["running"]["checked_out_sha"], SHA)

    def test_checked_out_sha_is_what_the_target_said_not_the_request(self):
        h = Harness()
        h.ask(req(sha=SHA))
        h.remote.step()
        h.state["running"]["checked_out_sha"] = "e" * 40  # what the target reported at the start
        h.air.state = "done"
        h.remote.step()
        self.assertEqual((h.res("t1")["sha"], h.res("t1")["checked_out_sha"]), (SHA, "e" * 40))

    def test_a_target_that_reports_this_machines_hostname_is_not_used(self):
        h = Harness()
        h.air.hostname = "Warrens-Mac-mini.local"
        h.ask(req())
        h.remote.step()
        self.assertIn("not run on the mini", h.res("t1")["error"])
        self.assertIsNone(h.state["running"])

    def test_a_target_that_finds_itself_on_this_machine_starts_nothing(self):
        h = Harness()
        h.air.is_this_machine = True
        h.ask(req())
        h.remote.step()
        r = h.res("t1")
        self.assertEqual((r["ok"], r["refused"], r["exit_code"]), (False, None, None))
        self.assertIn("which is this machine; not run on the mini", r["error"])
        self.assertNotIn("if the job did start there", r["error"])  # it did not: the script stopped first
        self.assertIsNone(h.state["running"])
        self.assertIn("NEEDS ATTENTION", "\n".join(h.logs))

    def test_a_refusal_after_the_start_script_ran_kills_before_it_cleans_up(self):
        for setup in (lambda a: setattr(a, "head", SHA2), lambda a: setattr(a, "hostname", "avatar.internal")):
            h = Harness()
            h.remote.local_hostname = "avatar.internal"
            setup(h.air)
            h.ask(req())
            h.remote.step()
            self.assertFalse(h.res("t1")["ok"])
            tail = [c[0] for c in h.air.calls][-2:]
            self.assertEqual(tail, ["kill", "cleanup"])

    def test_a_start_or_push_that_fails_is_answered_and_the_queue_moves_on(self):
        h = Harness()
        h.air.start_error = "could not check out the commit (exit 92)"
        h.ask(req("a"), req("b"))
        h.remote.step()
        self.assertIn("could not start on air: could not check out", h.res("a")["error"])
        self.assertIsNone(h.res("a")["refused"])
        h.air.start_error = None
        h.remote.step()
        self.assertEqual(h.state["running"]["req"]["id"], "b")
        h3 = Harness()
        h3.mirror.push_error = "pushing the commit to the target failed: x"
        h3.ask(req())
        h3.remote.step()
        self.assertIn("pushing the commit", h3.res("t1")["error"])
        self.assertEqual(h3.air.did("start"), [])

    def test_a_job_the_target_still_has_running_holds_the_next_one_back(self):
        h = Harness()
        h.air.busy = "old1"  # left behind by a dropped connection; not in this runner's state
        h.ask(req("a"), req("b"))
        st = h.remote.step()
        self.assertEqual(h.results, {})                       # not an answer
        self.assertIsNone(h.state["running"])
        self.assertEqual([q["id"] for q in st["queued"]], ["a", "b"])  # back at the head, order kept
        self.assertEqual(st["target_busy_with"], "old1")
        h.remote.step()
        self.assertEqual(len(h.air.did("start")), 1)          # not asked again every look
        h.clock.t += 15
        h.remote.step()
        self.assertEqual(len(h.air.did("start")), 2)
        h.air.busy = None
        h.clock.t += 15
        st = h.remote.step()
        self.assertEqual((st["running"]["id"], st["target_busy_with"]), ("a", None))
        self.assertEqual("\n".join(h.logs).count("waiting id=a"), 1)

    def test_a_connection_that_drops_during_the_start_is_answered_and_the_next_request_waits(self):
        h = Harness()
        h.air.start_error = "ssh failed (exit 255)"
        h.ask(req("a"), req("b"))
        h.remote.step()
        r = h.res("a")
        self.assertEqual((r["ok"], r["refused"], r["exit_code"]), (False, None, None))
        self.assertIn("could not start on air: ssh failed (exit 255)", r["error"])
        self.assertIn("if the job did start there", r["error"])
        # The job did start there: the target says so when b is tried.
        h.air.start_error, h.air.busy = None, "a"
        h.remote.step()
        self.assertEqual((h.state["running"], h.state["target_busy"]), (None, "a"))
        self.assertNotIn("b", h.results)
        h.air.busy = None
        h.clock.t += 15
        h.remote.step()
        self.assertEqual(h.state["running"]["req"]["id"], "b")

    def test_each_run_asks_for_its_own_fixed_file_names(self):
        for run, want, png in (("unit", ("unit-tests.trx",), False), ("integration", ("integration-tests.trx",), False),
                               ("ui", ("ui-tests.xunit.xml",), False),
                               ("screenshots", ("ui-screenshots.xunit.xml",), True),
                               ("build", (), False), ("coverage", (), False), ("glyph", (), False)):
            h = Harness()
            h.ask(req(run=run))
            h.remote.step()
            self.assertEqual(h.air.did("start")[0][6], want, run)
            self.assertEqual(h.air.did("start")[0][5], remote.command_for(run)[1], run)  # the cap, for the target's own clock
            h.air.state = "done"
            h.remote.step()
            self.assertEqual(h.air.did("collect")[0][2:], (want, png), run)
            # The name is the one the argv gave the report.
            for name in want:
                self.assertIn(name, " ".join(remote.command_for(run)[0]), run)

    def test_on_a_target_without_tmux_integration_and_coverage_are_refused_not_run_red(self):
        h = Harness()
        h.air.tmux = False
        h.ask(req("i1", run="integration"), req("c1", run="coverage"), req("u1", run="unit"), req("b1", run="build"))
        st = h.remote.step()
        self.assertEqual(st["targets"]["air"]["tmux"], False)
        h.remote.step()
        for rid in ("i1", "c1"):
            r = h.res(rid)
            self.assertEqual((r["ok"], r["refused"], r["exit_code"], r["started_at"], r["target"]),
                             (False, "the target has no tmux, which IntegrationTests needs; not run", None, None, None))
            self.assertEqual(set(r), FIELDS)
        # Nothing of theirs reached the target, and the runs that need no tmux go on.
        h.remote.step()
        self.assertEqual([c[1] for c in h.air.did("start")], ["u1"])
        self.assertEqual([p[2] for p in h.mirror.pushes], ["u1"])
        h.air.state = "done"
        h.remote.step()
        h.remote.step()
        self.assertEqual([c[1] for c in h.air.did("start")], ["u1", "b1"])
        self.assertTrue(h.res("u1")["ok"])

    def test_on_a_target_with_tmux_integration_and_coverage_run(self):
        for run in ("integration", "coverage"):
            h = Harness()
            h.ask(req(run=run))
            st = h.remote.step()
            self.assertEqual((st["running"]["run"], st["targets"]["air"]["tmux"]), (run, True))
            self.assertEqual(len(h.air.did("start")), 1)

    def test_coverage_cleans_everything_and_must_say_merged_4(self):
        h = Harness()
        h.ask(req(run="coverage"))
        h.remote.step()
        self.assertTrue(h.air.did("start")[0][4])  # clean_all
        self.assertEqual(h.state["running"]["cap_s"], 45 * 60)
        h.air.state = "done"
        h.air.output = "building...\nmerged 4 report(s)\n\nlines 9033/10067 (89.7%)\n"
        h.remote.step()
        r, _, files = h.results["t1"]
        self.assertEqual((r["ok"], r["merged_reports"], r["base_sha"], r["error"]), (True, 4, BASE, None))
        self.assertTrue(files["coverage-summary.txt"].startswith(b"merged 4 report(s)\n"))
        self.assertIn("coverage-summary.txt", r["files"])

    def test_coverage_that_merged_anything_but_four_is_not_ok_though_it_exited_zero(self):
        for out, n, frag in (("merged 1 report(s)\nx\n", 1, "merged 1 report(s), not 4"),
                             ("merged 6 report(s)\nx\n", 6, "merged 6 report(s), not 4"),
                             ("no figure\n", None, "no `merged N report(s)` line")):
            h = Harness()
            h.ask(req(run="coverage"))
            h.remote.step()
            h.air.state, h.air.output = "done", out
            h.remote.step()
            r = h.res("t1")
            self.assertEqual((r["ok"], r["exit_code"], r["merged_reports"]), (False, 0, n))
            self.assertIn(frag, r["error"])

    def test_other_runs_do_not_clean_everything_or_carry_coverage_fields(self):
        h = Harness()
        h.ask(req(run="build"))
        h.remote.step()
        self.assertFalse(h.air.did("start")[0][4])
        h.air.state = "done"
        h.remote.step()
        self.assertNotIn("merged_reports", h.res("t1"))

    def test_result_files_that_could_not_be_fetched_do_not_hide_the_exit_code(self):
        h = Harness()
        h.ask(req())
        h.remote.step()
        h.air.state, h.air.collect_error = "done", "ssh to air failed: timeout"
        h.remote.step()
        r = h.res("t1")
        self.assertEqual((r["ok"], r["exit_code"]), (True, 0))
        self.assertIn("could not fetch the result files", r["error"])

    def test_dropped_archive_members_are_named(self):
        h = Harness()
        h.ask(req())
        h.remote.step()
        h.air.state, h.air.dropped = "done", ["files/../x"]
        h.remote.step()
        self.assertEqual(h.res("t1")["files_dropped"], ["files/../x"])

    def test_a_running_job_survives_a_new_tick(self):
        h = Harness()
        h.ask(req("a"), req("b"))
        h.remote.step()
        state = json.loads(h.saves[-1])  # what the next run-once loads
        h2 = Harness(state=state)
        h2.air.state = "done"
        h2.remote.step()
        self.assertTrue(h2.res("a")["ok"])
        self.assertEqual([c[1] for c in h2.air.did("start")], ["b"])

    def test_every_decision_is_logged(self):
        h = Harness()
        h.ask(req("a"), req("bad", sha="d" * 40))
        h.remote.step()
        h.air.state = "done"
        h.remote.step()
        text = "\n".join(h.logs)
        for frag in ("queued id=a", "refused id=bad", "started id=a target=air host=air.internal",
                     '"dotnet", "test"', "finished id=a target=air exit=0"):
            self.assertIn(frag, text)

    def test_there_is_no_local_execution_path(self):
        """The file starts processes in two places only, both seams' `sh`:
        Mirror (git) and Target (ssh). Nothing in it can run dotnet here."""
        with open(os.path.join(ROOT, "host", "machines", "remote.py")) as f:
            src = f.read()
        self.assertEqual(src.count("subprocess.run("), 1)   # Target's bytes seam, argv starts "ssh"
        self.assertNotIn("os.system", src)
        self.assertNotIn("Popen", src)
        self.assertNotIn("shell=True", src)


# ---------------------------------------------------------------- real seams, fake sh

class Sh:
    def __init__(self, answers=None):
        self.calls, self.answers = [], answers or {}

    def __call__(self, argv, timeout=None, input=None):
        self.calls.append((list(argv), input))
        for key, (code, out) in self.answers.items():
            if key in argv or (isinstance(input, str) and key in input):
                return subprocess.CompletedProcess(argv, code, out, "boom")
        return subprocess.CompletedProcess(argv, 0, "", "")


class MirrorTests(unittest.TestCase):
    def mirror(self, sh):
        self.d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.d, True)
        return remote.Mirror(cfg(state_dir=self.d), sh)

    def test_only_branches_are_fetched_never_pull_refs(self):
        sh = Sh({"for-each-ref": (0, "x commit refs/heads/bugfix/pc-a\n")})
        self.assertTrue(self.mirror(sh).on_branch(SHA))
        fetch = [a for a, _ in sh.calls if "fetch" in a][0]
        self.assertEqual(fetch[-2:], [remote.DEFAULTS["repo_url"], "+refs/heads/*:refs/heads/*"])
        self.assertNotIn("pull", " ".join(" ".join(a) for a, _ in sh.calls))
        contains = [a for a, _ in sh.calls if "for-each-ref" in a][0]
        self.assertEqual(contains[-3:], ["--contains", SHA, "refs/heads"])

    def test_unknown_commit_or_on_no_branch_is_false_and_a_failed_fetch_is_none(self):
        self.assertFalse(self.mirror(Sh({"cat-file": (1, "")})).on_branch(SHA))
        self.assertFalse(self.mirror(Sh({"for-each-ref": (0, "")})).on_branch(SHA))
        self.assertIsNone(self.mirror(Sh({"fetch": (128, "")})).on_branch(SHA))

        def hung(argv, timeout=None, input=None):
            raise subprocess.TimeoutExpired(argv, timeout)
        self.assertIsNone(self.mirror(hung).on_branch(SHA))

    def test_push_sends_the_commit_and_develop_as_origin_develop(self):
        sh = Sh()
        self.mirror(sh).push("air:orbweaver-remote/repo.git", SHA, "t1")
        argv = sh.calls[-1][0]
        self.assertEqual(argv[-3:], ["air:orbweaver-remote/repo.git", f"{SHA}:refs/remote-runs/t1",
                                     "refs/heads/develop:refs/remotes/origin/develop"])
        with self.assertRaises(base.MachineError):
            self.mirror(Sh({"push": (1, "")})).push("air:x", SHA, "t1")

    def test_base_sha(self):
        self.assertEqual(self.mirror(Sh({"rev-parse": (0, BASE + "\n")})).base_sha(), BASE)
        with self.assertRaises(base.MachineError):
            self.mirror(Sh({"rev-parse": (1, "")})).base_sha()


@unittest.skipIf(os.name == "nt" or shutil.which("git") is None, "needs git")
class MirrorRealGitTests(unittest.TestCase):
    """Mirror against a real repository on disk standing where GitHub is."""

    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.d, True)
        self.env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_SYSTEM=os.devnull,
                        GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
                        GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid")
        self.origin, self.work = os.path.join(self.d, "origin.git"), os.path.join(self.d, "work")
        self.git("init", "-q", "--bare", self.origin)
        self.git("init", "-q", self.work)
        self.develop = self.commit("base")
        self.git("-C", self.work, "push", "-q", self.origin, "HEAD:refs/heads/develop")

        def sh(argv, timeout=None, input=None):
            return subprocess.run(argv, capture_output=True, text=True, timeout=timeout, env=self.env)
        self.mirror = remote.Mirror(cfg(state_dir=os.path.join(self.d, "state"), repo_url=self.origin), sh)

    def git(self, *args):
        r = subprocess.run(["git", *args], capture_output=True, text=True, env=self.env)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout.strip()

    def commit(self, text):
        with open(os.path.join(self.work, "f.txt"), "w") as f:
            f.write(text)
        self.git("-C", self.work, "add", "f.txt")
        self.git("-C", self.work, "commit", "-q", "-m", text)
        return self.git("-C", self.work, "rev-parse", "HEAD")

    def test_a_sha_pushed_seconds_ago_passes_because_every_check_fetches_first(self):
        self.assertTrue(self.mirror.on_branch(self.develop))
        new = self.commit("fix")
        self.assertFalse(self.mirror.on_branch(new))      # committed, not pushed
        self.git("-C", self.work, "push", "-q", self.origin, "HEAD:refs/heads/bugfix/pc-fix")
        self.assertTrue(self.mirror.on_branch(new))       # the same object, no restart, straight after the push
        self.assertEqual(self.mirror.base_sha(), self.develop)

    def test_any_branch_counts_and_an_ancestor_of_a_tip_counts(self):
        first = self.commit("one")
        second = self.commit("two")
        self.git("-C", self.work, "push", "-q", self.origin, "HEAD:refs/heads/feature/anything")
        self.assertTrue(self.mirror.on_branch(first))
        self.assertTrue(self.mirror.on_branch(second))

    def test_a_pull_request_ref_or_a_tag_is_not_a_branch(self):
        fork = self.commit("from a fork")
        self.git("-C", self.work, "push", "-q", self.origin, "HEAD:refs/pull/7/head")
        self.git("-C", self.work, "push", "-q", self.origin, "HEAD:refs/tags/v9")
        self.assertFalse(self.mirror.on_branch(fork))
        self.assertTrue(self.mirror.on_branch(self.develop))  # control: the fetch itself worked

    def test_a_branch_deleted_upstream_stops_counting(self):
        gone = self.commit("abandoned")
        self.git("-C", self.work, "push", "-q", self.origin, "HEAD:refs/heads/bugfix/pc-gone")
        self.assertTrue(self.mirror.on_branch(gone))
        self.git("-C", self.work, "push", "-q", self.origin, ":refs/heads/bugfix/pc-gone")
        self.assertFalse(self.mirror.on_branch(gone))

    def test_github_unreachable_is_none(self):
        self.mirror.url = os.path.join(self.d, "nowhere.git")
        self.assertIsNone(self.mirror.on_branch(self.develop))


class TargetTests(unittest.TestCase):
    def test_every_call_is_ssh_to_the_host_with_the_script_on_stdin(self):
        sh = Sh({"vm.loadavg": (0, "air.internal\n{ 1.0 2.0 3.0 }\ntmux=no\n")})
        t = remote.Target("air", "air", cfg(), sh)
        self.assertEqual(t.probe(), {"reachable": True, "load": [1.0, 2.0, 3.0], "hostname": "air.internal",
                                     "tmux": False})
        has = remote.Target("air", "air", cfg(), Sh({"vm.loadavg": (0, "air.internal\n{ 1 2 3 }\ntmux=yes\n")}))
        self.assertEqual(has.probe()["tmux"], True)
        self.assertIn("command -v tmux", remote.PROBE)
        argv, script = sh.calls[0]
        self.assertEqual(argv[0], "ssh")
        self.assertEqual(argv[-3:], ["air", "bash", "-s"])
        self.assertIn("BatchMode=yes", argv)
        self.assertEqual(script, remote.PROBE)

    def test_probe_of_a_silent_host_is_unreachable_not_an_error(self):
        t = remote.Target("air", "air", cfg(), Sh({"hostname": (255, "")}))
        self.assertEqual(t.probe()["reachable"], False)

        def hung(argv, timeout=None, input=None):
            raise subprocess.TimeoutExpired(argv, timeout)
        self.assertEqual(remote.Target("air", "air", cfg(), hung).probe()["reachable"], False)
        self.assertEqual(remote.Target("air", "air", cfg(), hung).poll("t1"), ("unreachable", None, None))

    def test_start_quotes_the_command_and_cleans_ignored_files_only_for_coverage(self):
        sh = Sh({"SHA=": (0, f"sha={SHA}\nhostname=air.internal\nstarted=1\n")})
        t = remote.Target("air", "air", cfg(), sh)
        got = t.start("t1", SHA, ["dotnet", "test", "--logger", "trx;LogFileName=a.trx"], 600, ["a.trx"])
        self.assertEqual(got, {"sha": SHA, "hostname": "air.internal", "started": True, "busy": None})
        script = sh.calls[-1][1]
        self.assertIn("dotnet test --logger 'trx;LogFileName=a.trx' >", script)
        self.assertIn("git checkout -q -f --detach \"$SHA\"", script)
        self.assertIn("git clean -q -f -d  ||", script)
        self.assertIn("-name TestResults", script)
        self.assertIn("for N in a.trx; do", script)
        self.assertIn("echo $((NOW + 600)) >", script)
        t.start("t2", SHA, ["./tools/coverage.sh"], 2700, clean_all=True)
        self.assertIn("git clean -q -f -d -x ||", sh.calls[-1][1])
        self.assertIn("for N in ''; do", sh.calls[-1][1])

    def test_no_build_server_outlives_a_job(self):
        for script in (remote.START, remote.POLL):
            for frag in ("MSBUILDDISABLENODEREUSE=1", "DOTNET_CLI_USE_MSBUILD_SERVER=0", "UseSharedCompilation=false"):
                self.assertIn(frag, script)
        self.assertIn("dotnet build-server shutdown", remote.START)
        # After the command, before the exit file that says the job is over.
        self.assertLess(remote.START.index("dotnet build-server shutdown"), remote.START.index('mv "$J/exit.tmp"'))

    def test_start_reports_a_target_that_is_busy(self):
        sh = Sh({"SHA=": (0, "busy=old1\n")})
        got = remote.Target("air", "air", cfg(), sh).start("t1", SHA, ["dotnet", "build"], 600)
        self.assertEqual((got["busy"], got["started"], got["sha"]), ("old1", False, None))

    def test_start_reports_a_worktree_at_another_commit_as_not_started(self):
        sh = Sh({"SHA=": (0, f"sha={SHA2}\nhostname=air.internal\n")})
        got = remote.Target("air", "air", cfg(), sh).start("t1", SHA, ["dotnet", "build"], 600)
        self.assertEqual(got, {"sha": SHA2, "hostname": "air.internal", "started": False, "busy": None})

    def test_start_failures_are_machine_errors(self):
        for code, frag in ((90, "job folder"), (91, "worktree"), (92, "check out"), (255, "ssh failed")):
            with self.assertRaises(base.MachineError) as cm:
                remote.Target("air", "air", cfg(), Sh({"SHA=": (code, "")})).start("t1", SHA, ["dotnet"], 600)
            self.assertIn(frag, str(cm.exception))

    def test_poll_answers(self):
        def poll(out, code=0):
            return remote.Target("air", "air", cfg(), Sh({"vm.loadavg": (code, out)})).poll("t1")
        self.assertEqual(poll("{ 1 2 3 }\nrunning\n"), ("running", None, [1.0, 2.0, 3.0]))
        self.assertEqual(poll("{ 1 2 3 }\nstarting\n")[0], "running")
        self.assertEqual(poll("{ 1 2 3 }\ndone 0\n")[:2], ("done", 0))
        self.assertEqual(poll("{ 1 2 3 }\ndone 134\n")[:2], ("done", 134))
        self.assertEqual(poll("{ 1 2 3 }\nlost\n")[0], "lost")
        self.assertEqual(poll("", 255), ("unreachable", None, None))

    def test_collect_reads_the_archive_through_the_safe_list(self):
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w") as tf:
            for name, data in (("output.txt", b"Passed!\n"), ("files/a.trx", b"<t/>"), ("files/b.trx", b"<u/>"),
                               ("files/screenshots/s.png", b"png"), ("files/../../evil", b"x"), ("/abs", b"y")):
                info = tarfile.TarInfo(name)
                info.size = len(data)
                tf.addfile(info, io.BytesIO(data))
            link = tarfile.TarInfo("files/link")
            link.type, link.linkname = tarfile.SYMTYPE, "/etc/passwd"
            tf.addfile(link)
        raw = buf.getvalue()
        t = remote.Target("air", "air", cfg(), Sh(),
                          sh_bytes=lambda argv, timeout=None, input=None:
                          subprocess.CompletedProcess(argv, 0, raw, b""))
        out, files, dropped = t.collect("t1", 10_000, ["a.trx"], True)
        self.assertEqual((out, files), ("Passed!\n", {"a.trx": b"<t/>", "screenshots/s.png": b"png"}))
        self.assertEqual(sorted(dropped), ["/abs", "files/../../evil", "files/b.trx", "files/link"])
        # The same archive for a run that hands back no files: only the output.
        self.assertEqual(t.collect("t1", 10_000)[1], {})

    def test_the_archive_is_cut_on_the_target_and_an_over_long_one_is_refused(self):
        sent = []

        def sh_bytes(argv, timeout=None, input=None):
            sent.append(input.decode())
            return subprocess.CompletedProcess(argv, 0, self.raw, b"")
        t = remote.Target("air", "air", cfg(), Sh(), sh_bytes=sh_bytes)
        most = 1000 + remote.ARCHIVE_SLACK
        self.raw = b"x" * (most + 1)
        with self.assertRaises(base.MachineError) as cm:
            t.collect("t1", 1000)
        self.assertIn("larger than", str(cm.exception))
        self.assertIn(f"| head -c {most + 1}", sent[-1])   # the cut happens on the target, in the pipeline
        # Control: a real archive inside the limit is read.
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w") as tf:
            info = tarfile.TarInfo("output.txt")
            info.size = 3
            tf.addfile(info, io.BytesIO(b"ok\n"))
        self.raw = buf.getvalue()
        self.assertLessEqual(len(self.raw), most)
        self.assertEqual(t.collect("t1", 1000)[0], "ok\n")

    def test_start_passes_this_machines_names_and_reads_exit_94_as_local(self):
        sh = Sh({"SHA=": (94, "hostname=avatar.internal\n")})
        t = remote.Target("air", "air", cfg(), sh, forbidden=remote.local_names("Avatar.internal") | {"bad name;id"})
        with self.assertRaises(remote.LocalTarget) as cm:
            t.start("t1", SHA, ["dotnet", "build"], 600)
        self.assertIn("'avatar.internal', which is this machine; not run on the mini", str(cm.exception))
        script = sh.calls[-1][1]
        self.assertIn("for N in avatar avatar.internal avatar.local localhost macmini mini; do", script)
        self.assertNotIn("bad name", script)
        # The check is before anything is made or started.
        self.assertLess(script.index("exit 94"), script.index("cd \"$ROOT\""))
        self.assertLess(script.index("exit 94"), script.index("nohup"))
        with self.assertRaises(base.MachineError):
            remote.unpack(b"not a tar", 10)

    def test_every_script_is_valid_bash(self):
        if shutil.which("bash") is None:
            self.skipTest("no bash")
        kw = dict(root="orbweaver-remote", rid="t1", sha=SHA, clean_x="-x", command="dotnet build", max_output=5,
                  cap=600, files="a.trx", forbidden="avatar avatar.internal", max_stream=99)
        for name in ("START", "POLL", "KILL", "COLLECT", "CLEANUP", "INIT"):
            r = subprocess.run(["bash", "-n"], input=getattr(remote, name).format(**kw),
                               capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, f"{name}: {r.stderr}")

    def test_ssh_hostname_reads_ssh_dash_g(self):
        sh = Sh({"-G": (0, "user x\nhostname 192.168.0.50\nport 22\n")})
        self.assertEqual(remote.ssh_hostname("air", sh), "192.168.0.50")
        self.assertEqual(remote.ssh_hostname("air", Sh({"-G": (0, "")})), "air")


# ---------------------------------------------------------------- the target's shell, for real

@unittest.skipIf(os.name == "nt" or not all(shutil.which(x) for x in ("bash", "git", "perl")),
                 "needs bash, git and perl")
class TargetShellTests(unittest.TestCase):
    """Target's scripts run on this machine in a scratch HOME: `bash -s` in
    place of `ssh <host> bash -s`. Nothing here is dotnet; the job is a shell
    command passed as argv, which only a test can do."""

    def setUp(self):
        self.home = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.home, True)
        env = dict(os.environ, HOME=self.home, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_SYSTEM=os.devnull,
                   GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
                   GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid")
        self.env = env

        def sh(argv, timeout=None, input=None):
            assert argv[0] == "ssh" and argv[-2:] == ["bash", "-s"], argv
            return subprocess.run(["bash", "-s"], input=input, capture_output=True, text=True,
                                  timeout=timeout, env=env)

        def sh_bytes(argv, timeout=None, input=None):
            return subprocess.run(["bash", "-s"], input=input, capture_output=True, timeout=timeout, env=env)

        # A stand-in dotnet where the scripts look first ($HOME/.dotnet), so
        # run.sh's `dotnet build-server shutdown` never reaches a real SDK:
        # on a machine other builds share, that would stop their servers.
        os.makedirs(os.path.join(self.home, ".dotnet"))
        self.dotnet_log = os.path.join(self.home, "dotnet-calls")
        stub = os.path.join(self.home, ".dotnet", "dotnet")
        with open(stub, "w") as f:
            f.write(f'#!/bin/sh\necho "$@" >> "{self.dotnet_log}"\n')
        os.chmod(stub, 0o755)
        self.sh, self.sh_bytes = sh, sh_bytes
        self.t = remote.Target("air", "air", cfg(), sh, sh_bytes)
        self.t.init()
        # A source repository with two commits, pushed into the target's bare one.
        src = os.path.join(self.home, "src")
        self.git("init", "-q", src)
        self.shas = []
        for n in ("one", "two"):
            with open(os.path.join(src, "f.txt"), "w") as f:
                f.write(n)
            self.git("-C", src, "add", "f.txt")
            self.git("-C", src, "commit", "-q", "-m", n)
            self.shas.append(self.git("-C", src, "rev-parse", "HEAD").strip())
        bare = os.path.join(self.home, remote.ROOT_DIR, "repo.git")
        self.git("-C", src, "push", "-q", bare, f"{self.shas[0]}:refs/remote-runs/a", f"{self.shas[1]}:refs/remote-runs/b")
        self.work = os.path.join(self.home, remote.ROOT_DIR, "work")

    def git(self, *args):
        r = subprocess.run(["git", *args], capture_output=True, text=True, env=self.env)
        self.assertEqual(r.returncode, 0, r.stderr)
        return r.stdout

    def wait(self, rid, want, secs=20):
        end = time.time() + secs
        while time.time() < end:
            state, code, _ = self.t.poll(rid)
            if state == want:
                return code
            time.sleep(0.1)
        self.fail(f"{rid} never reached {want}: {self.t.poll(rid)}")

    def test_a_job_runs_and_its_output_and_files_come_back(self):
        got = self.t.start("a", self.shas[0], ["bash", "-c",
                           "cat f.txt; mkdir -p TestResults/screenshots tests/U/bin/TestResults; "
                           "echo x > tests/U/bin/TestResults/r.trx; echo y > TestResults/other.trx; "
                           "echo p > TestResults/screenshots/s.png; exit 3"], 600, ["r.trx"])
        self.assertEqual((got["sha"], got["started"]), (self.shas[0], True))
        self.assertTrue(got["hostname"])
        self.assertEqual(self.wait("a", "done"), 3)
        out, files, dropped = self.t.collect("a", 10_000, ["r.trx"], True)
        self.assertEqual(out, "one")
        self.assertEqual(files, {"r.trx": b"x\n", "screenshots/s.png": b"p\n"})  # other.trx was never copied
        self.assertEqual(dropped, [])
        self.assertEqual(read(self.dotnet_log), "build-server shutdown\n")  # the stand-in, once, at the end
        self.t.cleanup("a")
        self.assertEqual(self.t.poll("a")[0], "lost")
        self.assertNotIn("remote-runs/a", self.git("--git-dir", os.path.join(self.home, remote.ROOT_DIR, "repo.git"),
                                                   "for-each-ref"))

    def test_a_dirty_worktree_at_another_commit_is_forced_to_the_one_asked_for(self):
        self.t.start("a", self.shas[0], ["true"], 600)
        self.wait("a", "done")
        # Left dirty: a tracked file edited, an untracked file, a stale report.
        with open(os.path.join(self.work, "f.txt"), "w") as f:
            f.write("edited")
        os.makedirs(os.path.join(self.work, "tests", "X", "TestResults"))
        for p in ("stray.txt", os.path.join("tests", "X", "TestResults", "old.trx")):
            with open(os.path.join(self.work, p), "w") as f:
                f.write("stale")
        got = self.t.start("b", self.shas[1], ["bash", "-c", "cat f.txt; ls"], 600, ["old.trx"])
        self.assertEqual(got["sha"], self.shas[1])
        self.assertEqual(got["sha"], self.git("-C", self.work, "rev-parse", "HEAD").strip())
        self.wait("b", "done")
        out, files, _ = self.t.collect("b", 10_000, ["old.trx"])
        self.assertTrue(out.startswith("two"))
        self.assertNotIn("stray.txt", out)
        self.assertEqual(files, {})  # the stale report was removed, not handed back as this run's

    def test_ignored_files_survive_a_normal_run_and_not_a_coverage_run(self):
        self.t.start("a", self.shas[0], ["true"], 600)
        self.wait("a", "done")
        with open(os.path.join(self.work, ".git", "info", "exclude") if os.path.isdir(os.path.join(self.work, ".git"))
                  else os.path.join(self.home, remote.ROOT_DIR, "repo.git", "info", "exclude"), "a") as f:
            f.write("bin/\n")
        os.makedirs(os.path.join(self.work, "bin"))
        with open(os.path.join(self.work, "bin", "app.dll"), "w") as f:
            f.write("x")
        self.t.start("b", self.shas[1], ["true"], 600)
        self.wait("b", "done")
        self.assertTrue(os.path.exists(os.path.join(self.work, "bin", "app.dll")))  # incremental build kept
        self.t.start("a", self.shas[0], ["true"], 600, clean_all=True)
        self.wait("a", "done")
        self.assertFalse(os.path.exists(os.path.join(self.work, "bin")))

    def test_an_unknown_commit_is_a_machine_error(self):
        with self.assertRaises(base.MachineError):
            self.t.start("a", "d" * 40, ["true"], 600)

    def test_on_a_forbidden_host_nothing_is_made_checked_out_or_run(self):
        # Here the "target" really is this machine (QA round 1): with its
        # names forbidden, the command must not run; without, it does.
        import socket
        marker = os.path.join(self.home, "ran")
        jobs = os.path.join(self.home, remote.ROOT_DIR, "jobs")
        guarded = remote.Target("air", "air", cfg(), self.sh, self.sh_bytes,
                                forbidden=remote.local_names(socket.gethostname()))
        with self.assertRaises(remote.LocalTarget) as cm:
            guarded.start("q1", self.shas[0], ["touch", marker], 600)
        self.assertIn("not run on the mini", str(cm.exception))
        time.sleep(1)
        self.assertFalse(os.path.exists(marker))
        self.assertEqual(os.listdir(jobs), [])            # no job folder, so no run.sh and no pid
        self.assertFalse(os.path.exists(self.work))       # no checkout either
        # Control: the same start with nothing forbidden runs the command.
        self.t.start("q1", self.shas[0], ["touch", marker], 600)
        self.wait("q1", "done")
        self.assertTrue(os.path.exists(marker))

    def test_a_job_left_running_holds_the_worktree_until_it_ends_or_passes_its_cap(self):
        marker = os.path.join(self.home, "pid")
        got = self.t.start("a", self.shas[0], ["bash", "-c", f"sleep 300 & echo $! > {marker}; wait"], 600)
        self.assertTrue(got["started"])
        # A second start, as after a dropped connection: the mini knows
        # nothing, the target does.
        got = self.t.start("b", self.shas[1], ["true"], 600)
        self.assertEqual((got["busy"], got["started"]), ("a", False))
        self.assertEqual(self.git("-C", self.work, "rev-parse", "HEAD").strip(), self.shas[0])  # worktree untouched
        self.assertEqual(self.t.poll("a")[0], "running")
        self.assertTrue(_alive(int(read(marker))))
        # Past its own cap, the left-behind job is killed and the new one starts.
        with open(os.path.join(self.home, remote.ROOT_DIR, "jobs", "a", "deadline"), "w") as f:
            f.write("1\n")
        got = self.t.start("b", self.shas[1], ["true"], 600)
        self.assertEqual((got["busy"], got["started"], got["sha"]), (None, True, self.shas[1]))
        end = time.time() + 10
        while time.time() < end and _alive(int(read(marker))):
            time.sleep(0.1)
        self.assertFalse(_alive(int(read(marker))))
        self.assertEqual(self.wait("b", "done"), 0)

    def test_a_finished_job_takes_what_it_left_running_with_it(self):
        marker = os.path.join(self.home, "pid")
        # A helper the job starts and does not wait for, as a build server is.
        self.t.start("a", self.shas[0], ["bash", "-c", f"sleep 300 & echo $! > {marker}; echo built; exit 5"], 600)
        self.assertEqual(self.wait("a", "done"), 5)       # the answer is written first, with the job's own code
        pid = int(read(marker))
        end = time.time() + 10
        while time.time() < end and _alive(pid):
            time.sleep(0.1)
        self.assertFalse(_alive(pid))
        self.assertEqual(self.t.collect("a", 10_000)[0], "built\n")

    def test_a_finished_job_does_not_hold_the_next_one_back(self):
        self.t.start("a", self.shas[0], ["true"], 600)
        self.wait("a", "done")
        got = self.t.start("b", self.shas[1], ["true"], 600)  # a was never collected or cleaned up
        self.assertEqual((got["busy"], got["started"]), (None, True))
        # Its result is still there to fetch: finished a moment ago.
        self.assertEqual(self.t.poll("a")[:2], ("done", 0))
        self.wait("b", "done")
        old = time.time() - 11 * 60
        os.utime(os.path.join(self.home, remote.ROOT_DIR, "jobs", "a", "exit"), (old, old))
        self.t.start("c", self.shas[0], ["true"], 600)
        self.assertEqual(self.t.poll("a")[0], "lost")         # swept once it is ten minutes old
        self.assertEqual(self.t.poll("b")[0], "done")         # control: the recent one is kept

    def test_kill_takes_the_whole_process_tree_and_nothing_else(self):
        marker = os.path.join(self.home, "pids")
        bystander = subprocess.Popen(["sleep", "60"])
        self.addCleanup(lambda: (bystander.kill(), bystander.wait()))
        self.t.start("a", self.shas[0], ["bash", "-c",
                     f"sleep 300 & echo $! >> {marker}; (sleep 300 & echo $! >> {marker}; wait) & wait"], 600)
        self.wait("a", "running")
        end = time.time() + 10
        while time.time() < end and (not os.path.exists(marker) or len(read(marker).split()) < 2):
            time.sleep(0.1)
        pids = [int(p) for p in read(marker).split()]
        self.assertEqual(len(pids), 2)
        for p in pids:
            os.kill(p, 0)  # control: both grandchildren are alive before the kill
        self.t.kill("a")
        end = time.time() + 10
        alive = pids
        while time.time() < end and alive:
            alive = [p for p in pids if _alive(p)]
            time.sleep(0.1)
        self.assertEqual(alive, [])
        self.assertIsNone(bystander.poll())  # a process outside the job's group is untouched
        self.assertEqual(self.t.poll("a")[0], "lost")  # no exit file was written: it did not finish


def read(path, mode="r"):
    with open(path, mode) as f:
        return f.read()


def _alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


# ---------------------------------------------------------------- files, run_once, wiring

class FileTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.d, True)

    def test_write_result_is_whole_or_absent_and_never_overwrites(self):
        big = "x" * 1000 + "END\n"
        self.assertTrue(remote.write_result(self.d, "t1", {"id": "t1"}, big, {
            "a.trx": b"<t/>", "screenshots/s.png": b"png", "../climb": b"x", "screenshots/../c": b"x",
            "deep/er/x": b"x"}, 100))
        folder = os.path.join(self.d, "t1")
        self.assertEqual(sorted(os.listdir(folder)), ["files", "output-tail.txt", "output.txt", "result.json"])
        self.assertEqual(read(os.path.join(folder, "output.txt")), big)
        tail = read(os.path.join(folder, "output-tail.txt"))
        self.assertEqual((len(tail), tail[-4:]), (100, "END\n"))
        self.assertEqual(read(os.path.join(folder, "files", "a.trx"), "rb"), b"<t/>")
        self.assertEqual(read(os.path.join(folder, "files", "screenshots", "s.png"), "rb"), b"png")
        self.assertEqual(sorted(os.listdir(os.path.join(folder, "files"))), ["a.trx", "screenshots"])
        self.assertEqual(os.listdir(os.path.join(folder, "files", "screenshots")), ["s.png"])
        self.assertEqual(sorted(os.listdir(self.d)), ["t1"])  # nothing climbed out
        self.assertFalse(remote.write_result(self.d, "t1", {"id": "other"}, "", {}, 100))
        self.assertEqual(json.loads(read(os.path.join(folder, "result.json"))), {"id": "t1"})
        self.assertEqual([n for n in os.listdir(self.d) if n.startswith(".")], [])

    def test_prune_removes_only_old_answers(self):
        results, spool = os.path.join(self.d, "r"), os.path.join(self.d, "s")
        for p in (os.path.join(results, "old"), os.path.join(results, "new"), os.path.join(spool, "done")):
            os.makedirs(p)
        for p in (os.path.join(results, "status.json"), os.path.join(spool, "done", "old.json"),
                  os.path.join(spool, "done", "new.json")):
            open(p, "w").close()
        now = time.time()
        for p in ("r/old", "r/status.json", "s/done/old.json"):
            os.utime(os.path.join(self.d, p), (now - 8 * 86400,) * 2)
        remote.prune(results, spool, now, 7)
        self.assertEqual(sorted(os.listdir(results)), ["new", "status.json"])
        self.assertEqual(os.listdir(os.path.join(spool, "done")), ["new.json"])


@unittest.skipIf(os.name == "nt", "the runner lives on the mini")
class RunOnceTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.d, True)
        self.company = os.path.join(self.d, "company")
        self.spool = os.path.join(self.company, "remote-spool")
        self.results = os.path.join(self.company, "remote-results")
        os.makedirs(self.spool)
        self.config = os.path.join(self.d, "remote.json")
        self.write_config()
        self.mirror, self.air = FakeMirror(), FakeTarget()

    def write_config(self, **kw):
        c = {"company_dir": self.company, "state_dir": os.path.join(self.d, "state"),
             "log_path": os.path.join(self.d, "remote.log"), "tick_s": 0, "poll_s": 0}
        c.update(kw)
        with open(self.config, "w") as f:
            json.dump(c, f)

    def drop(self, r, name=None):
        with open(os.path.join(self.spool, (name or r["id"]) + ".json"), "w") as f:
            json.dump(r, f)

    def tick(self, **kw):
        kw.setdefault("targets", {"air": self.air})
        kw.setdefault("local_addrs", ("192.168.0.127",))
        return remote.run_once(self.config, mirror=self.mirror, local_hostname=MINI,
                               resolve=lambda h: h, addrs_of=lambda h: (), sleep=lambda s: None, **kw)

    def result(self, rid):
        with open(os.path.join(self.results, rid, "result.json")) as f:
            return json.load(f)

    def status(self):
        with open(os.path.join(self.results, "status.json")) as f:
            return json.load(f)

    def test_spool_to_answer_across_ticks(self):
        self.drop(req("t1", run="unit", flt="OrbGlyph"))
        self.tick()
        self.assertFalse(os.path.exists(os.path.join(self.results, "t1")))
        st = self.status()
        self.assertEqual(st["running"]["id"], "t1")
        self.assertEqual(st["targets"]["air"]["load"], [1.5, 1.0, 0.5])
        self.assertTrue(os.path.exists(os.path.join(self.spool, "done", "t1.json")))
        self.air.state, self.air.files = "done", {"unit-tests.trx": b"<t/>"}
        self.tick()
        r = self.result("t1")
        self.assertEqual((r["ok"], r["target"], r["files"]), (True, "air", ["unit-tests.trx"]))
        self.assertTrue(os.path.exists(os.path.join(self.results, "t1", "files", "unit-tests.trx")))
        self.assertIsNone(self.status()["running"])

    def test_an_idle_tick_still_reports_the_targets_load(self):
        self.tick()
        st = self.status()
        self.assertEqual(st["targets"]["air"], {"reachable": True, "load": [1.5, 1.0, 0.5],
                                                "hostname": "air.internal", "tmux": True})
        self.assertEqual((st["running"], st["queued"]), (None, []))

    def test_bad_requests_are_answered_or_dropped_like_the_machine_spool(self):
        self.drop(req("r1", run="shell"))
        self.drop({"verb": "remote_test", "id": "r2", "sha": SHA, "run": "ui", "command": "id"})
        self.drop(req("r3"), name="other-name")
        with open(os.path.join(self.spool, "junk.json"), "w") as f:
            f.write("{")
        self.tick()
        self.assertIn("run must be one of", self.result("r1")["refused"])
        self.assertEqual(self.result("r2")["refused"], "unknown field(s): command")
        self.assertFalse(os.path.exists(os.path.join(self.results, "r3")))
        self.assertEqual(sorted(os.listdir(os.path.join(self.spool, "bad"))),
                         ["junk.json", "other-name.json", "r1.json", "r2.json"])
        self.assertEqual(self.air.did("start"), [])

    def test_a_request_still_being_written_is_left_alone_until_it_is_renamed(self):
        # The agent side writes <id>.json.tmp and renames it; only *.json is read.
        tmp = os.path.join(self.spool, "t1.json.tmp")
        with open(tmp, "w") as f:
            f.write('{"verb": "remote_test", "id": "t1", "sha": "')  # half a request
        self.tick()
        self.assertTrue(os.path.exists(tmp))
        self.assertEqual(os.listdir(os.path.join(self.spool, "bad")), [])
        self.assertFalse(os.path.exists(os.path.join(self.results, "t1")))
        with open(tmp, "w") as f:
            json.dump(req("t1"), f)
        os.replace(tmp, os.path.join(self.spool, "t1.json"))
        self.tick()
        self.assertEqual(self.status()["running"]["id"], "t1")  # control: once renamed it is read

    def test_an_answered_id_is_never_run_again(self):
        self.drop(req("t1"))
        self.tick()
        self.air.state = "done"
        self.tick()
        self.drop(req("t1"))
        self.tick()
        self.assertEqual(len(self.air.did("start")), 1)

    def test_a_config_that_names_this_machine_runs_nothing_and_says_so(self):
        for targets in ([{"name": "mini", "ssh": "mini"}], [{"name": "air", "ssh": "localhost"}],
                        [{"name": "air", "ssh": "192.168.0.127"}]):
            self.write_config(targets=targets)
            self.drop(req("t1"))
            made = []
            orig = remote.Target
            remote.Target = lambda *a, **k: made.append(a) or orig(*a, **k)
            try:
                remote.run_once(self.config, mirror=self.mirror, local_hostname=MINI, resolve=lambda h: h,
                                local_addrs=("192.168.0.127",), addrs_of=lambda h: (), sleep=lambda s: None)
            finally:
                remote.Target = orig
            self.assertEqual(made, [])  # no Target was even built
            self.assertEqual(self.status()["targets"], {})
            self.assertIn("NEEDS ATTENTION: targets in remote.json refused", read(os.path.join(self.d, "remote.log")))
            shutil.rmtree(os.path.join(self.d, "state"))

    def test_a_second_runner_beside_a_live_one_does_nothing(self):
        import fcntl
        os.makedirs(os.path.join(self.d, "state"))
        with open(os.path.join(self.d, "state", "runner.lock"), "w") as held:
            fcntl.flock(held, fcntl.LOCK_EX)
            self.drop(req("t1"))
            self.assertEqual(self.tick(), 0)
            self.assertTrue(os.path.exists(os.path.join(self.spool, "t1.json")))  # left for the live one

    def test_an_unreadable_state_file_is_set_aside(self):
        os.makedirs(os.path.join(self.d, "state"))
        with open(os.path.join(self.d, "state", "state.json"), "w") as f:
            f.write("{")
        self.tick()
        self.assertEqual(len([n for n in os.listdir(os.path.join(self.d, "state")) if "unreadable" in n]), 1)

    def test_config_needs_company_dir(self):
        with open(self.config, "w") as f:
            json.dump({}, f)
        with self.assertRaises(SystemExit):
            remote.load_config(self.config)


class WiringTests(unittest.TestCase):
    def test_plist_runs_remote_py_every_minute_outside_the_sandbox(self):
        with open(os.path.join(ROOT, "host", "launchd", "org.uplift.orbweaver-bugfix-remote.plist"), "rb") as f:
            p = plistlib.load(f)
        self.assertEqual(p["Label"], "org.uplift.orbweaver-bugfix-remote")
        self.assertEqual(p["ProgramArguments"], ["/usr/bin/python3", "__REPO__/host/machines/remote.py", "run-once"])
        self.assertEqual(p["StartInterval"], 60)
        self.assertNotIn("sandbox-exec", json.dumps(p["ProgramArguments"]))

    def test_ci_runs_this_file(self):
        with open(os.path.join(ROOT, ".github", "workflows", "ci.yml")) as f:
            self.assertIn("python tools/test-bugfix-remote.py", f.read())

    def test_read_spool_still_defaults_to_the_machine_schema(self):
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, True)
        spool, results = os.path.join(d, "s"), os.path.join(d, "r")
        os.makedirs(spool)
        os.makedirs(results)
        with open(os.path.join(spool, "x1.json"), "w") as f:
            json.dump({"verb": "machine_release", "id": "x1", "pr": 7}, f)
        got = runner.read_spool(spool, results, lambda m: None)
        self.assertEqual(got, [({"verb": "machine_release", "id": "x1", "pr": 7}, None)])


if __name__ == "__main__":
    unittest.main()
