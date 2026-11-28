#!/usr/bin/env python3
"""Tests for `ow-remote`, the bug-fix org's way to ask the remote runner (CB-494).

    python3 tools/test-bugfix-ow-remote.py

A scratch repository whose origin reads as Uplift-Foundation/Orbweaver but is
redirected (url.insteadOf) to a local bare repository, so "is HEAD pushed" is
answered by real git and nothing reaches GitHub. The test plays the runner:
it reads the request from the spool and writes the result folder by rename,
which is the contract the real runner keeps. Every refusal is paired with the
control that asks: the same setup, clean and pushed, does write a request.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.realpath(os.path.join(HERE, "..", "paperclip", "bugfix-org", "host", "shims", "ow-remote"))
GITHUB = "https://github.com/Uplift-Foundation/Orbweaver.git"


def git(cwd, *args):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


class OwRemote(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        bare = os.path.join(self.tmp, "origin.git")
        self.repo = os.path.join(self.tmp, "wt")
        subprocess.run(["git", "init", "-q", "--bare", bare], check=True)
        subprocess.run(["git", "init", "-q", self.repo], check=True)
        for k, v in (("user.name", "t"), ("user.email", "t@example.invalid"),
                     ("commit.gpgsign", "false"), (f"url.{bare}.insteadOf", GITHUB)):
            git(self.repo, "config", k, v)
        git(self.repo, "remote", "add", "origin", GITHUB)
        self.commit("a")
        git(self.repo, "push", "-q", "origin", "HEAD:refs/heads/bugfix/pc-x")
        company = os.path.join(self.tmp, "company")
        self.spool = os.path.join(company, "remote-spool")
        self.results = os.path.join(company, "remote-results")
        os.makedirs(self.spool)
        os.makedirs(self.results)
        self.env = dict(os.environ, BUGFIX_SPOOL=os.path.join(company, "bugfix-spool"),
                        OW_REMOTE_WAIT_S="20", OW_REMOTE_POLL_S="0.05",
                        GIT_CONFIG_NOSYSTEM="1", HOME=self.tmp)

    def commit(self, name):
        with open(os.path.join(self.repo, name), "w") as f:
            f.write(name)
        git(self.repo, "add", name)
        git(self.repo, "commit", "-q", "-m", name)

    def head(self):
        return git(self.repo, "rev-parse", "HEAD")

    def start(self, *args, **env):
        p = subprocess.Popen([sys.executable, TOOL, *args], cwd=self.repo, text=True,
                             env=dict(self.env, **env), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.addCleanup(lambda: p.poll() is None and p.kill())
        return p

    def run_tool(self, *args, **env):
        p = self.start(*args, **env)
        out, err = p.communicate(timeout=30)
        return p.returncode, out, err

    def requests(self):
        return sorted(os.listdir(self.spool))

    def take_request(self, timeout=10):
        deadline = time.time() + timeout
        while time.time() < deadline:
            done = [n for n in self.requests() if n.endswith(".json")]
            if done:
                # Consume it, as the runner does, so the next ask is the next file.
                src = os.path.join(self.spool, done[0])
                with open(src) as f:
                    req = json.load(f)
                os.rename(src, os.path.join(self.tmp, "taken-" + done[0]))
                return done[0], req
            time.sleep(0.05)
        self.fail("no request was written")

    def answer(self, req, tail="tail line\n", **fields):
        res = {"id": req["id"], "verb": "remote_test", "sha": req["sha"],
               "checked_out_sha": req["sha"], "run": req["run"], "filter": req.get("filter"),
               "target": "air", "target_hostname": "air.local", "ok": True, "refused": None,
               "exit_code": 0, "timed_out": False, "files": []}
        res.update(fields)
        tmp = os.path.join(self.results, "." + req["id"])
        os.mkdir(tmp)
        with open(os.path.join(tmp, "output-tail.txt"), "w") as f:
            f.write(tail)
        with open(os.path.join(tmp, "result.json"), "w") as f:
            json.dump(res, f)
        os.rename(tmp, os.path.join(self.results, req["id"]))

    # --- asking ---------------------------------------------------------

    def test_asks_for_the_pushed_head_with_the_closed_schema(self):
        p = self.start("ui", "Orbweaver.UiTests.ChatPanelTests")
        name, req = self.take_request()
        self.assertEqual(set(req), {"verb", "id", "sha", "run", "filter"})
        self.assertEqual((req["verb"], req["sha"], req["run"], req["filter"]),
                         ("remote_test", self.head(), "ui", "Orbweaver.UiTests.ChatPanelTests"))
        self.assertEqual(name, req["id"] + ".json")
        self.assertRegex(req["id"], r"^[a-z0-9-]{1,40}$")
        self.assertEqual(self.requests(), [])  # no .tmp left behind
        self.answer(req, tail="Passed: 12\n", exit_code=0)
        out, err = p.communicate(timeout=30)
        self.assertEqual(p.returncode, 0, err)
        self.assertEqual(out, "Passed: 12\n")
        self.assertIn("air.local", err)

    def test_no_filter_sends_no_filter_key(self):
        p = self.start("build")
        _, req = self.take_request()
        self.assertNotIn("filter", req)
        self.answer(req)
        p.communicate(timeout=30)

    def test_two_asks_get_different_ids(self):
        ids = []
        for _ in range(2):
            p = self.start("glyph")
            name, req = self.take_request()
            self.answer(req)
            p.communicate(timeout=30)
            ids.append(req["id"])
        self.assertNotEqual(*ids)

    # --- refusals before asking, each beside the control above ---------

    def assert_refused_without_asking(self, args, phrase, **env):
        code, out, err = self.run_tool(*args, **env)
        self.assertEqual(code, 64, err)
        self.assertIn(phrase, err)
        self.assertEqual(self.requests(), [])

    def test_refuses_an_uncommitted_change(self):
        with open(os.path.join(self.repo, "a"), "w") as f:
            f.write("changed")
        self.assert_refused_without_asking(["unit"], "commit and push first")

    def test_refuses_an_untracked_file(self):
        open(os.path.join(self.repo, "NewTests.cs"), "w").close()
        self.assert_refused_without_asking(["unit"], "commit and push first")

    def test_refuses_a_commit_that_is_not_pushed(self):
        self.commit("b")
        self.assert_refused_without_asking(["unit"], "is not the tip of any branch on origin")

    def test_refuses_another_repository(self):
        git(self.repo, "remote", "set-url", "origin", "https://github.com/Uplift-Foundation/Orbweaver-Mobile.git")
        self.assert_refused_without_asking(["unit"], "Orbweaver only")

    def test_refuses_bad_runs_and_filters(self):
        for args, phrase in ((["everything"], "unknown run"),
                             (["build", "X"], "takes no filter"),
                             (["coverage", "X"], "takes no filter"),
                             (["unit", "A B"], "a filter is a class name"),
                             (["unit", "x;rm"], "a filter is a class name"),
                             (["unit", "a" * 201], "a filter is a class name")):
            with self.subTest(args=args):
                self.assert_refused_without_asking(args, phrase)

    def test_refuses_without_a_spool(self):
        env = dict(self.env)
        env.pop("BUGFIX_SPOOL")
        p = subprocess.run([sys.executable, TOOL, "unit"], cwd=self.repo, env=env,
                           capture_output=True, text=True, timeout=30)
        self.assertEqual(p.returncode, 64)
        self.assertIn("BUGFIX_SPOOL is not set", p.stderr)
        self.assertEqual(self.requests(), [])

    # --- reading the answer --------------------------------------------

    def test_exit_status_is_the_remote_commands(self):
        p = self.start("unit", "Orbweaver.UnitTests.GlyphTests")
        _, req = self.take_request()
        self.answer(req, tail="Failed: 1\n", ok=False, exit_code=3)
        out, err = p.communicate(timeout=30)
        self.assertEqual((p.returncode, out), (3, "Failed: 1\n"))

    def test_a_refusal_exits_64_and_says_nothing_ran(self):
        p = self.start("build")
        _, req = self.take_request()
        self.answer(req, tail="", ok=False, exit_code=None,
                    refused="no target reachable; not run on the mini")
        _, err = p.communicate(timeout=30)
        self.assertEqual(p.returncode, 64)
        self.assertIn("refused, nothing ran: no target reachable; not run on the mini", err)

    def test_a_runner_error_is_printed(self):
        p = self.start("build")
        _, req = self.take_request()
        self.answer(req, ok=False, exit_code=None, error="ssh to air dropped mid-job")
        _, err = p.communicate(timeout=30)
        self.assertEqual(p.returncode, 1)
        self.assertIn("runner error: ssh to air dropped mid-job", err)

    def refused_with(self, run, sentence):
        p = self.start(run)
        _, req = self.take_request()
        self.answer(req, tail="", ok=False, exit_code=None, checked_out_sha=None, refused=sentence)
        _, err = p.communicate(timeout=30)
        return p.returncode, err

    def test_no_tmux_refusal_says_to_run_integration_and_coverage_on_the_mini(self):
        for run, cmd in (("integration", "dotnet test tests/IntegrationTests"),
                         ("coverage", "./tools/coverage.sh --base origin/develop")):
            with self.subTest(run=run):
                code, err = self.refused_with(run, "the target has no tmux, which IntegrationTests needs; not run")
                self.assertEqual(code, 64)
                self.assertIn("until the runner runs it on the Air, run " + run + " on the mini", err)
                self.assertIn(cmd, err)

    def test_control_every_other_refusal_never_points_at_the_mini(self):
        for run, sentence in (("ui", "the target has no tmux, which IntegrationTests needs; not run"),
                              ("coverage", "no target reachable; not run on the mini"),
                              ("integration", "queue is full")):
            with self.subTest(run=run, sentence=sentence):
                code, err = self.refused_with(run, sentence)
                self.assertEqual(code, 64)
                self.assertNotIn("run " + run + " on the mini", err)

    def test_a_timeout_without_an_exit_code_is_124(self):
        p = self.start("build")
        _, req = self.take_request()
        self.answer(req, ok=False, exit_code=None, timed_out=True)
        _, err = p.communicate(timeout=30)
        self.assertEqual(p.returncode, 124)
        self.assertIn("timed out", err)

    def test_still_running_exits_75_and_wait_picks_it_up(self):
        code, out, err = self.run_tool("ui", OW_REMOTE_WAIT_S="0.3")
        self.assertEqual(code, 75, err)
        m = re.fullmatch(r"still running: ow-remote wait ([a-z0-9-]+)\n", out)
        self.assertTrue(m, out)
        _, req = self.take_request()
        self.assertEqual(req["id"], m.group(1))
        code, _, _ = self.run_tool("wait", req["id"], OW_REMOTE_WAIT_S="0.3")
        self.assertEqual(code, 75)  # control: still nothing to read
        self.answer(req, exit_code=5)
        code, out, _ = self.run_tool("wait", req["id"])
        self.assertEqual((code, out), (5, "tail line\n"))

    def test_wait_refuses_an_id_outside_the_pattern(self):
        code, _, err = self.run_tool("wait", "../x")
        self.assertEqual(code, 64)
        self.assertIn("not a request id", err)

    def finish(self, run, **fields):
        p = self.start(run)
        _, req = self.take_request()
        self.answer(req, **fields)
        out, err = p.communicate(timeout=30)
        return p.returncode, out, err

    def test_coverage_short_of_four_is_void_even_at_exit_0(self):
        for merged in (3, None):
            with self.subTest(merged=merged):
                code, _, err = self.finish("coverage", merged_reports=merged, exit_code=0, base_sha="b" * 40)
                self.assertEqual(code, 65, err)
                self.assertIn("VOID", err)
                self.assertIn("not a measurement", err)
        code, _, err = self.finish("coverage", merged_reports=4, exit_code=0, base_sha="b" * 40)
        self.assertEqual(code, 0, err)  # control
        self.assertIn("merged 4 report(s)", err)
        self.assertNotIn("VOID", err)

    def test_another_commit_checked_out_is_void_even_at_exit_0(self):
        code, _, err = self.finish("build", checked_out_sha="c" * 40, exit_code=0)
        self.assertEqual(code, 65)
        self.assertIn("checked out cccccccccccc", err)
        code, _, err = self.finish("glyph", exit_code=0)  # control: same sha
        self.assertEqual(code, 0)
        self.assertNotIn("VOID", err)

    def test_an_answer_for_another_sha_than_asked_is_void(self):
        # The runner checked out what the answer names, but that is not what
        # this invocation asked for.
        other = "d" * 40
        code, _, err = self.finish("build", sha=other, checked_out_sha=other, exit_code=0)
        self.assertEqual(code, 65)
        self.assertIn("not " + self.head()[:12], err)

    def test_no_checked_out_sha_is_void(self):
        code, _, err = self.finish("build", checked_out_sha=None, exit_code=0)
        self.assertEqual(code, 65)
        self.assertIn("no checked-out commit", err)

    def test_ok_false_with_exit_0_is_void(self):
        code, _, err = self.finish("build", ok=False, exit_code=0)
        self.assertEqual(code, 65)
        code, _, _ = self.finish("build", ok=True, exit_code=0)  # control
        self.assertEqual(code, 0)

    def test_a_failed_ui_test_is_exit_2_and_never_reads_as_a_refusal(self):
        # MTP exits 2 for a failed test; a refusal must not share that code.
        code, _, err = self.finish("ui", ok=False, exit_code=2)
        self.assertEqual(code, 2)
        self.assertNotIn("refused", err)
        self.assertNotIn("VOID", err)
        p = self.start("ui")  # control: a refusal exits 64, never 2
        _, req = self.take_request()
        self.answer(req, ok=False, exit_code=None, checked_out_sha=None, refused="queue is full")
        p.communicate(timeout=30)
        self.assertEqual(p.returncode, 64)

    def test_zero_tests_run_passes_exit_8_through(self):
        code, _, _ = self.finish("ui", ok=False, exit_code=8)
        self.assertEqual(code, 8)

    def test_wait_without_the_asked_sha_still_voids_a_mismatch(self):
        code, out, err = self.run_tool("build", OW_REMOTE_WAIT_S="0.2")
        self.assertEqual(code, 75)
        _, req = self.take_request()
        self.answer(req, checked_out_sha="c" * 40, exit_code=0)
        code, _, err = self.run_tool("wait", req["id"])
        self.assertEqual(code, 65)

    def test_status_prints_the_runners_status(self):
        with open(os.path.join(self.results, "status.json"), "w") as f:
            f.write('{"target_reachable": true}\n')
        code, out, _ = self.run_tool("status")
        self.assertEqual((code, out), (0, '{"target_reachable": true}\n'))


if __name__ == "__main__":
    unittest.main(verbosity=2)
