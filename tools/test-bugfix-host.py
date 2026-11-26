#!/usr/bin/env python3
"""Tests for the bug-fix org's host side (CB-455): host/create_app.py,
host/warden.py, host/apply-rulesets.sh, host/rulesets/*.json,
tools/stress-run.py and .github/workflows/stress.yml's wiring.

    python3 tools/test-bugfix-host.py

stdlib unittest, like the other tools tests. Nothing here touches GitHub,
Paperclip or the network beyond 127.0.0.1: the manifest helper runs against a
fake GitHub endpoint, the warden against fake seams, the apply script against a
stub `gh` on PATH. Each rule has a passing control beside its refusal, so a
check that goes blind turns red.

Not covered, and said plainly: the real seams (GhCli, AppMinter, PaperclipHttp)
and GitHub's real manifest flow. They need live services and live credentials.
"""
import contextlib
import http.server
import importlib.util
import io
import json
import os
import plistlib
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")


def load(name, rel):
    spec = importlib.util.spec_from_file_location(name, os.path.join(ROOT, rel))
    m = importlib.util.module_from_spec(spec)
    sys.modules[name] = m
    spec.loader.exec_module(m)
    return m


create_app = load("create_app", "host/create_app.py")
warden = load("warden", "host/warden.py")
stress = load("stress_run", "tools/stress-run.py")
check_host = load("check_host", "host/check_host.py")


# ---------------------------------------------------------------- create_app

class FakeGitHub:
    """Answers POST /app-manifests/<code>/conversions."""

    def __init__(self, permissions=None, status=200):
        self.permissions = create_app.PERMISSIONS if permissions is None else permissions
        self.status, self.codes = status, []
        outer = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_POST(self):
                outer.codes.append(self.path)
                body = json.dumps({"id": 4242, "slug": "orbweaver-bugfix",
                                   "pem": "-----BEGIN KEY-----\nSECRETKEY\n-----END KEY-----\n",
                                   "webhook_secret": "WHSECRET", "client_secret": "CLSECRET",
                                   "permissions": outer.permissions}).encode()
                self.send_response(outer.status)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.srv = http.server.HTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.srv.server_address[1]}"
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def close(self):
        self.srv.shutdown()
        self.srv.server_close()


def run_helper(tmp, fake, drive, timeout=10):
    """Run create_app.serve in a thread; `drive(port, state)` plays the browser."""
    args = create_app.argparse.Namespace(
        public_host="127.0.0.1", bind="127.0.0.1", port=0, config_dir=os.path.join(tmp, "cfg"),
        github_web="https://github.example", github_api=fake.url, timeout=timeout)
    box, ready = {}, threading.Event()

    def on_ready(port, state):
        box["port"], box["state"] = port, state
        ready.set()

    out = io.StringIO()

    def target():
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            box["rc"] = create_app.serve(args, ready=on_ready)

    t = threading.Thread(target=target)
    t.start()
    assert ready.wait(5)
    drive(box["port"], box["state"])
    t.join(timeout + 5)
    return box["rc"], out.getvalue(), args.config_dir


def get(port, path):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=5) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


class CreateAppTests(unittest.TestCase):
    def test_happy_path_stores_id_and_key_at_0600_and_prints_no_secret(self):
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        with tempfile.TemporaryDirectory() as tmp:
            seen = {}

            def drive(port, state):
                seen["page"] = get(port, "/")
                seen["cb"] = get(port, f"/callback?code=ONETIMECODE&state={state}")

            rc, out, cfg = run_helper(tmp, fake, drive)
            self.assertEqual(rc, 0)
            self.assertEqual(seen["cb"][0], 200)
            if sys.platform != "win32":  # POSIX modes mean nothing on the Windows leg
                for name in ("app.pem", "app.json"):
                    mode = stat.S_IMODE(os.stat(os.path.join(cfg, name)).st_mode)
                    self.assertEqual(mode, 0o600, name)
                self.assertEqual(stat.S_IMODE(os.stat(cfg).st_mode), 0o700)
            self.assertEqual(json.load(open(os.path.join(cfg, "app.json"))),
                             {"app_id": 4242, "slug": "orbweaver-bugfix"})
            self.assertIn("SECRETKEY", open(os.path.join(cfg, "app.pem")).read())
            # Nothing else GitHub returned is kept.
            blob = "".join(open(os.path.join(cfg, n)).read() for n in os.listdir(cfg))
            self.assertNotIn("WHSECRET", blob)
            self.assertNotIn("CLSECRET", blob)
            # The URL is printed; no secret or code is.
            self.assertIn("Open this URL", out)
            for secret in ("SECRETKEY", "WHSECRET", "CLSECRET", "ONETIMECODE"):
                self.assertNotIn(secret, out + seen["page"][1] + seen["cb"][1])
            self.assertEqual(fake.codes, ["/app-manifests/ONETIMECODE/conversions"])

    def test_form_posts_exactly_the_planned_permissions_to_the_org(self):
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        with tempfile.TemporaryDirectory() as tmp:
            seen = {}

            def drive(port, state):
                seen["page"] = get(port, "/")[1]
                get(port, f"/callback?code=c&state={state}")

            run_helper(tmp, fake, drive)
            import html
            page = seen["page"]
            self.assertIn("https://github.example/organizations/Uplift-Foundation/settings/apps/new?state=", page)
            m = json.loads(html.unescape(page.split('name=manifest value="')[1].split('"><')[0]))
            self.assertEqual(m["default_permissions"], {
                "contents": "write", "pull_requests": "write", "checks": "read",
                "actions": "read", "statuses": "read", "metadata": "read"})
            self.assertEqual(m["default_events"], [])
            self.assertFalse(m["public"])
            self.assertNotIn("workflows", m["default_permissions"])
            self.assertTrue(m["redirect_url"].endswith("/callback"))

    def test_wrong_state_is_refused_and_does_not_end_the_run(self):
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        with tempfile.TemporaryDirectory() as tmp:
            seen = {}

            def drive(port, state):
                seen["bad"] = get(port, "/callback?code=c&state=forged")
                seen["nostate"] = get(port, "/callback?code=c")
                seen["nocode"] = get(port, f"/callback?state={state}")
                seen["good"] = get(port, f"/callback?code=c&state={state}")

            rc, _, cfg = run_helper(tmp, fake, drive)
            self.assertEqual([seen[k][0] for k in ("bad", "nostate", "nocode")], [400, 400, 400])
            self.assertEqual(seen["good"][0], 200)
            self.assertEqual(rc, 0)
            self.assertEqual(len(fake.codes), 1, "only the valid callback may reach GitHub")

    def test_broader_permissions_than_planned_are_refused_and_not_stored(self):
        for granted in (dict(create_app.PERMISSIONS, workflows="write"),
                        dict(create_app.PERMISSIONS, contents="read"),
                        {k: v for k, v in create_app.PERMISSIONS.items() if k != "checks"}):
            fake = FakeGitHub(permissions=granted)
            self.addCleanup(fake.close)
            with tempfile.TemporaryDirectory() as tmp:
                seen = {}
                rc, out, cfg = run_helper(
                    tmp, fake, lambda port, state: seen.update(cb=get(port, f"/callback?code=c&state={state}")))
                self.assertEqual(rc, 1, granted)
                self.assertEqual(seen["cb"][0], 409)
                self.assertFalse(os.path.exists(os.path.join(cfg, "app.pem")), granted)

    def test_conversion_failure_is_reported_without_storing(self):
        fake = FakeGitHub(status=500)
        self.addCleanup(fake.close)
        with tempfile.TemporaryDirectory() as tmp:
            seen = {}
            rc, _, cfg = run_helper(
                tmp, fake, lambda port, state: seen.update(cb=get(port, f"/callback?code=c&state={state}")))
            self.assertEqual(rc, 1)
            self.assertEqual(seen["cb"][0], 502)
            self.assertFalse(os.path.exists(os.path.join(cfg, "app.pem")))

    def test_second_callback_after_success_is_refused(self):
        # The server may already be down; either way the key must not be rewritten.
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        with tempfile.TemporaryDirectory() as tmp:
            def drive(port, state):
                get(port, f"/callback?code=c&state={state}")
                try:
                    get(port, f"/callback?code=d&state={state}")
                except OSError:
                    pass

            rc, _, _ = run_helper(tmp, fake, drive)
            self.assertEqual(rc, 0)
            self.assertEqual(len(fake.codes), 1)

    def test_times_out_when_nobody_calls_back(self):
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        with tempfile.TemporaryDirectory() as tmp:
            rc, out, cfg = run_helper(tmp, fake, lambda p, s: None, timeout=0.3)
            self.assertEqual(rc, 2)
            self.assertFalse(os.path.exists(os.path.join(cfg, "app.pem")))

    def test_permissions_problem_control(self):
        self.assertIsNone(create_app.permissions_problem(dict(create_app.PERMISSIONS)))
        self.assertIn("workflows", create_app.permissions_problem(
            dict(create_app.PERMISSIONS, workflows="write")))


# ---------------------------------------------------------------- warden rules

BOT = warden.BOT_LOGIN


def good_pr(**over):
    pr = {"state": "open", "user": {"login": BOT},
          "head": {"ref": "bugfix/pc-fix-flake", "repo": {"full_name": warden.REPO}},
          "base": {"ref": "develop"}}
    for k, v in over.items():
        pr[k] = v
    return pr


class ParseRequestTests(unittest.TestCase):
    OK = {"verb": "dispatch_stress", "pr": 5, "test_class": "Orbweaver.Tests.FooTests", "n": 30}

    def test_valid_requests(self):
        req, why = warden.parse_request(json.dumps(self.OK))
        self.assertIsNone(why)
        self.assertEqual(req["suite"], "unit")
        self.assertEqual(warden.parse_request('{"verb":"released","ticket":"CB-12"}')[0]["ticket"], "CB-12")
        self.assertEqual(warden.parse_request('{"verb":"push_refused"}')[0], {"verb": "push_refused"})

    def test_every_malformed_request_is_dropped(self):
        bad = [
            "not json", "[]", '{"verb":"rm"}', '{"verb":"pause_nobody"}',
            json.dumps(dict(self.OK, test_class="Foo; rm -rf ~")),
            json.dumps(dict(self.OK, test_class="Foo Bar")),
            json.dumps(dict(self.OK, test_class="$(id)")),
            json.dumps(dict(self.OK, test_class="")),
            json.dumps(dict(self.OK, n=0)), json.dumps(dict(self.OK, n=51)),
            json.dumps(dict(self.OK, n="30")), json.dumps(dict(self.OK, n=True)),
            json.dumps(dict(self.OK, pr=0)), json.dumps(dict(self.OK, pr="5")),
            json.dumps(dict(self.OK, suite="shell")),
            '{"verb":"released","ticket":"CB-1; x"}', '{"verb":"released"}',
        ]
        for raw in bad:
            req, why = warden.parse_request(raw)
            self.assertIsNone(req, raw)
            self.assertTrue(why)

    def test_boundaries_accepted(self):
        for n in (1, warden.MAX_N):
            self.assertIsNotNone(warden.parse_request(json.dumps(dict(self.OK, n=n)))[0])


class DispatchRuleTests(unittest.TestCase):
    def test_org_pr_passes(self):
        self.assertIsNone(warden.dispatch_problem(good_pr()))

    def test_each_refusal(self):
        cases = {
            "missing": None,
            "closed": good_pr(state="closed"),
            "human author": good_pr(user={"login": "wtvamp"}),
            "fork": good_pr(head={"ref": "bugfix/pc-x", "repo": {"full_name": "someone/Orbweaver"}}),
            "deleted fork": good_pr(head={"ref": "bugfix/pc-x", "repo": None}),
            "wrong prefix": good_pr(head={"ref": "feature/x", "repo": {"full_name": warden.REPO}}),
            "base main": good_pr(base={"ref": "main"}),
        }
        for label, pr in cases.items():
            self.assertTrue(warden.dispatch_problem(pr), label)


class AuditRuleTests(unittest.TestCase):
    GREEN = {n: "success" for n in warden.REQUIRED_CHECKS}

    def test_clean_merge_has_no_reasons(self):
        self.assertEqual(warden.audit_merge(True, self.GREEN), [])

    def test_no_qa_comment_is_a_reason(self):
        self.assertEqual(len(warden.audit_merge(False, self.GREEN)), 1)

    def test_red_check_is_a_reason_per_failure_kind(self):
        for concl in ("failure", "cancelled", "timed_out"):
            ci = dict(self.GREEN, **{warden.REQUIRED_CHECKS[1]: concl})
            self.assertIn("win-x64", warden.audit_merge(True, ci)[0], concl)

    def test_pending_or_absent_checks_are_not_red(self):
        self.assertEqual(warden.audit_merge(True, {}), [])
        self.assertEqual(warden.audit_merge(True, {warden.REQUIRED_CHECKS[0]: None}), [])

    def test_ci_settles_only_when_every_required_check_has_concluded(self):
        self.assertFalse(warden.ci_settled({}))
        self.assertFalse(warden.ci_settled({warden.REQUIRED_CHECKS[0]: "success"}))
        self.assertFalse(warden.ci_settled({n: None for n in warden.REQUIRED_CHECKS}))
        self.assertTrue(warden.ci_settled({n: "success" for n in warden.REQUIRED_CHECKS}))
        self.assertTrue(warden.ci_settled({warden.REQUIRED_CHECKS[0]: "failure", warden.REQUIRED_CHECKS[1]: "success"}))

    def test_qa_comment_needs_both_rids(self):
        self.assertTrue(warden.has_qa_comment(["x", "osx-arm64 and win-x64 reviewed"]))
        self.assertFalse(warden.has_qa_comment(["osx-arm64 only"]))
        self.assertFalse(warden.has_qa_comment([]))

    def test_release_streak_resets_on_merge(self):
        s = {}
        s["consecutive_releases"] = warden.next_release_state(s, "released")
        self.assertEqual(s["consecutive_releases"], 1)
        self.assertEqual(warden.next_release_state(s, "merged"), 0)

    def test_cost_ceiling(self):
        self.assertIsNone(warden.cost_problem(10**9, None))
        self.assertIsNone(warden.cost_problem(100, 100))
        self.assertTrue(warden.cost_problem(101, 100))

    def test_mint_due(self):
        self.assertTrue(warden.mint_due({}, 1000, False))
        self.assertFalse(warden.mint_due({"last_mint": 1000}, 1000 + 9 * 60, False))
        self.assertTrue(warden.mint_due({"last_mint": 1000}, 1000 + 10 * 60, False))
        self.assertFalse(warden.mint_due({}, 1000, True), "a paused org is not refreshed")


# ---------------------------------------------------------------- warden tick

class FakeGH:
    def __init__(self, prs=None, merged=(), comments=None, checks=None, runs=None):
        self.prs, self.merged = prs or {}, list(merged)
        self.comments, self.checks = comments or {}, checks or {}
        self.dispatched = []
        self.runs, self.reran = runs or {}, []

    def latest_ci_run(self, sha):
        return self.runs.get(sha)

    def rerun_failed(self, run_id):
        self.reran.append(run_id)

    def get_pr(self, n):
        return self.prs.get(n)

    def dispatch_stress(self, *a):
        self.dispatched.append(a)

    def merged_bot_prs(self):
        return self.merged

    def comment_bodies(self, n, before):
        return self.comments.get(n, [])

    def check_conclusions(self, sha):
        return self.checks.get(sha, {})


# ---------------------------------------------------------------- reruns (CB-465)

def head_pr(sha="s1", **over):
    pr = good_pr(head={"ref": "bugfix/pc-fix-flake", "repo": {"full_name": warden.REPO}, "sha": sha})
    pr.update(over)
    return pr


def ci_run(sha="s1", status="completed", conclusion="failure", run_id=99):
    return {"id": run_id, "name": "CI", "head_sha": sha, "status": status, "conclusion": conclusion}


class RerunRuleTests(unittest.TestCase):
    def test_a_red_run_at_the_head_of_a_bot_pr_may_be_rerun(self):
        for concl in warden.RED_CONCLUSIONS:
            self.assertIsNone(warden.rerun_problem(head_pr(), ci_run(conclusion=concl), {}), concl)

    def test_refusals_name_their_reason(self):
        cases = {
            "not the org's": (head_pr(user={"login": "wtvamp"}), ci_run(), {}, "not opened by the bug-fix App"),
            "closed": (head_pr(state="closed"), ci_run(), {}, "not open"),
            "already reran": (head_pr(), ci_run(), {"s1": 1}, "already reran"),
            "no run": (head_pr(), None, {}, "no CI run"),
            "run at another sha": (head_pr(), ci_run(sha="old"), {}, "not at the PR's head"),
            "still running": (head_pr(), ci_run(status="in_progress", conclusion=None), {}, "still running"),
            "green": (head_pr(), ci_run(conclusion="success"), {}, "not red"),
            "no head sha": (good_pr(), ci_run(), {}, "no sha"),
        }
        for label, (pr, run, reruns, why) in cases.items():
            self.assertIn(why, warden.rerun_problem(pr, run, reruns) or "", label)

    def test_parse_accepts_a_pr_and_refuses_anything_else(self):
        self.assertEqual(warden.parse_request('{"verb":"rerun_failed","pr":7}')[0], {"verb": "rerun_failed", "pr": 7})
        for raw in ('{"verb":"rerun_failed"}', '{"verb":"rerun_failed","pr":0}',
                    '{"verb":"rerun_failed","pr":true}', '{"verb":"rerun_failed","pr":"7"}'):
            self.assertIsNone(warden.parse_request(raw)[0], raw)


class RerunTickTests(unittest.TestCase):
    REQ = {"verb": "rerun_failed", "pr": 7}

    def test_reruns_once_per_head_sha_and_a_new_sha_may_again(self):
        gh = FakeGH(prs={7: head_pr("s1")}, runs={"s1": ci_run("s1", run_id=99)})
        state = {}
        _, paused, logs = tick(gh=gh, state=state, requests=[self.REQ])
        self.assertFalse(paused)
        self.assertEqual(gh.reran, [99])
        self.assertEqual(state["reruns"], {"s1": 1})
        self.assertTrue(any("reran failed jobs of CI run 99 for PR 7" in l for l in logs))
        _, paused, logs = tick(gh=gh, state=state, requests=[self.REQ])
        self.assertEqual(gh.reran, [99], "a second red at the same sha is real")
        self.assertTrue(any("already reran" in l for l in logs))
        self.assertFalse(paused, "a refused rerun never pauses")
        gh.prs[7], gh.runs["s2"] = head_pr("s2"), ci_run("s2", run_id=100)
        tick(gh=gh, state=state, requests=[self.REQ])
        self.assertEqual(gh.reran, [99, 100])

    def test_a_refused_rerun_is_logged_and_runs_nothing(self):
        gh = FakeGH(prs={7: head_pr(user={"login": "human"})}, runs={"s1": ci_run()})
        _, paused, logs = tick(gh=gh, requests=[self.REQ])
        self.assertEqual(gh.reran, [])
        self.assertFalse(paused)
        self.assertTrue(any("refused rerun for PR 7" in l for l in logs))

    def test_a_failed_gh_call_is_logged_and_not_counted(self):
        class Boom(FakeGH):
            def rerun_failed(self, run_id):
                raise subprocess.CalledProcessError(1, "gh")

        gh = Boom(prs={7: head_pr()}, runs={"s1": ci_run()})
        state = {}
        _, _, logs = tick(gh=gh, state=state, requests=[self.REQ])
        self.assertTrue(any("rerun for PR 7 failed" in l for l in logs))
        self.assertEqual(state.get("reruns"), {})

    def test_an_unreadable_pr_or_run_is_logged_not_raised(self):
        class Boom(FakeGH):
            def latest_ci_run(self, sha):
                raise subprocess.CalledProcessError(1, "gh")

        _, paused, logs = tick(gh=Boom(prs={7: head_pr()}), requests=[self.REQ])
        self.assertFalse(paused)
        self.assertTrue(any("unreadable" in l for l in logs))


class FakeMinter:
    def __init__(self):
        self.n = 0

    def mint(self):
        self.n += 1
        return f"tok{self.n}"


class FakePC:
    def __init__(self, paused=False, tokens=0, runs=0, active=(), issues=(), cancelled=(), logs=None):
        self.cancelled_issues, self.logs, self.full = list(cancelled), logs or {}, {}
        self.paused, self.tokens, self.runs = paused, tokens, runs
        self.active, self.issues = list(active), list(issues)
        self.secrets, self.pauses, self.cancelled, self.blocked = [], [], [], []
        self.resumed = False
        # The unstick sweep's world (CB-479).
        self.all, self.run_rows, self.said, self.pool = [], [], {}, {}
        self.paused_cos, self.fail_reads, self.fail_writes = set(), False, False
        self.statuses, self.comments, self.resets = [], [], []

    def runs_today(self):
        return self.runs

    def cancelled_issue_rows(self):
        return self.cancelled_issues

    def get_issue(self, issue_id):
        if issue_id in self.full:
            return self.full[issue_id]
        raise OSError("no such issue")

    def recent_run_ids(self):
        return list(self.logs)

    def run_log(self, run_id):
        return self.logs[run_id]

    def active_run_ids(self):
        return self.active

    def cancel_run(self, i):
        self.cancelled.append(i)

    def in_progress_issue_ids(self):
        return self.issues

    def block_issue(self, i):
        self.blocked.append(i)

    def put_secret(self, name, value):
        self.secrets.append((name, value))

    def is_paused(self, company=None):
        return company in self.paused_cos if company else self.paused

    def all_issues(self, company):
        if self.fail_reads:
            raise OSError("listing failed")
        return self.all

    def company_runs(self, company):
        return self.run_rows

    def newest_comment_at(self, issue_id, agent_id):
        return self.said.get(issue_id)

    def pool_usage(self, path):
        if self.pool is None:
            raise OSError("no pool file")
        return self.pool

    def _write(self):
        if self.fail_writes:
            raise OSError("write refused")

    def set_issue_status(self, issue_id, status):
        self._write()
        self.statuses.append((issue_id, status))

    def comment(self, issue_id, body):
        self._write()
        self.comments.append((issue_id, body))

    def reset_session(self, agent_id, task_key):
        self._write()
        self.resets.append((agent_id, task_key))

    def pause(self, reason):
        self.paused = True
        self.pauses.append(reason)

    def tokens_today(self):
        return self.tokens


def merged_pr(n=7, sha="abc"):
    return {"number": n, "merge_commit_sha": sha, "merged_at": "2026-11-24T10:00:00Z"}


def tick(gh=None, pc=None, requests=(), state=None, cfg=None, now=10_000.0):
    logs = []
    w = warden.Warden(cfg or {}, gh or FakeGH(), FakeMinter(), pc or FakePC(),
                      state if state is not None else {}, now, logs.append)
    paused = w.tick(list(requests))
    return w, paused, logs


class TickTests(unittest.TestCase):
    def test_quiet_tick_mints_and_writes_the_secret_only(self):
        pc = FakePC()
        w, paused, _ = tick(pc=pc)
        self.assertFalse(paused)
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertEqual(pc.pauses, [])
        self.assertEqual(w.state["last_mint"], 10_000.0)

    def test_does_not_remint_inside_the_interval(self):
        pc = FakePC()
        tick(pc=pc, state={"last_mint": 10_000.0 - 60})
        self.assertEqual(pc.secrets, [])

    def test_paused_company_is_not_refreshed(self):
        pc = FakePC(paused=True)
        tick(pc=pc)
        self.assertEqual(pc.secrets, [])

    def test_dispatch_for_org_pr_and_refusal_for_others(self):
        gh = FakeGH(prs={5: good_pr(), 6: good_pr(head={"ref": "bugfix/pc-x", "repo": {"full_name": "f/o"}})})
        req = lambda pr: {"verb": "dispatch_stress", "pr": pr, "test_class": "A.B", "n": 3, "suite": "unit"}
        _, _, logs = tick(gh=gh, requests=[req(5), req(6), req(99)])
        self.assertEqual(gh.dispatched, [("bugfix/pc-fix-flake", "A.B", 3, "unit")])
        self.assertEqual(sum("refused dispatch" in l for l in logs), 2)

    def test_two_releases_in_a_row_pause_one_does_not(self):
        rel = {"verb": "released", "ticket": "CB-1"}
        pc = FakePC()
        tick(pc=pc, requests=[rel])
        self.assertEqual(pc.pauses, [])
        pc = FakePC()
        w, paused, _ = tick(pc=pc, requests=[rel, dict(rel, ticket="CB-2")])
        self.assertTrue(paused)
        self.assertIn("released in a row", pc.pauses[0])
        self.assertEqual(pc.secrets, [], "no token is minted on the tick that pauses")

    def test_release_streak_persists_across_ticks_and_a_merge_breaks_it(self):
        rel = {"verb": "released", "ticket": "CB-1"}
        state = {}
        pc = FakePC()
        tick(pc=pc, requests=[rel], state=state)
        _, paused, _ = tick(pc=pc, requests=[rel], state=state)
        self.assertTrue(paused)
        state2 = {}
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64 ok"]},
                    checks={"abc": {n: "success" for n in warden.REQUIRED_CHECKS}})
        pc2 = FakePC()
        tick(pc=pc2, requests=[rel], state=state2)
        tick(gh=gh, pc=pc2, state=state2)
        _, paused, _ = tick(pc=pc2, requests=[rel], state=state2)
        self.assertFalse(paused)

    def test_merge_without_qa_pauses(self):
        pc = FakePC()
        gh = FakeGH(merged=[merged_pr()], comments={7: ["looks fine"]})
        w, paused, _ = tick(gh=gh, pc=pc)
        self.assertTrue(paused)
        self.assertIn("no QA comment", pc.pauses[0])

    def test_merge_with_qa_and_green_ci_does_not_pause(self):
        pc = FakePC()
        gh = FakeGH(merged=[merged_pr()], comments={7: ["both osx-arm64 and win-x64"]},
                    checks={"abc": {n: "success" for n in warden.REQUIRED_CHECKS}})
        _, paused, _ = tick(gh=gh, pc=pc)
        self.assertFalse(paused)

    def test_red_develop_ci_on_a_bot_merge_pauses(self):
        pc = FakePC()
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": {warden.REQUIRED_CHECKS[0]: "failure"}})
        _, paused, _ = tick(gh=gh, pc=pc)
        self.assertTrue(paused)
        self.assertIn("CI red", pc.pauses[0])

    def test_a_merge_is_audited_once(self):
        gh = FakeGH(merged=[merged_pr()], comments={7: ["nope"]})
        state = {}
        tick(gh=gh, pc=FakePC(), state=state)
        pc2 = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc2, state=state)
        self.assertFalse(paused, "an already-audited merge must not re-pause after a human resumes")

    def test_merge_seen_with_ci_pending_stays_pending_then_green_retires_it(self):
        # CB-460: the first tick after a merge sees the checks queued.
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": {n: None for n in warden.REQUIRED_CHECKS}})
        state = {}
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertIn("abc", state["audit_pending"])
        self.assertNotIn("abc", state["audited"])
        gh.checks = {"abc": {n: "success" for n in warden.REQUIRED_CHECKS}}
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertEqual(state["audit_pending"], {})
        self.assertIn("abc", state["audited"])

    def test_merge_seen_with_ci_pending_then_red_pauses(self):
        # The case CB-460 found live: pending at the first tick, red later.
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": {n: None for n in warden.REQUIRED_CHECKS}})
        state = {}
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        gh.checks = {"abc": {warden.REQUIRED_CHECKS[0]: "success", warden.REQUIRED_CHECKS[1]: "failure"}}
        pc = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("CI red", pc.pauses[0])
        self.assertIn("abc", state["audited"])

    def test_red_merge_does_not_repause_after_a_human_resumes(self):
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": {warden.REQUIRED_CHECKS[0]: "failure"}})
        state = {}
        tick(gh=gh, pc=FakePC(), state=state)
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)

    def test_qa_comment_is_read_once_while_ci_is_pending(self):
        reads = []

        class CountingGH(FakeGH):
            def comment_bodies(self, n, before):
                reads.append(n)
                return ["nope"]

        gh = CountingGH(merged=[merged_pr()], checks={"abc": {}})
        state = {}
        pcs = [FakePC(), FakePC(), FakePC()]
        for pc in pcs:
            tick(gh=gh, pc=pc, state=state)
        self.assertEqual(reads, [7])
        self.assertEqual(len(pcs[0].pauses), 1, "the missing QA comment pauses once")
        self.assertEqual(pcs[1].pauses + pcs[2].pauses, [])

    def test_ci_that_never_settles_pauses_after_the_limit(self):
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]}, checks={"abc": {}})
        state = {}
        tick(gh=gh, pc=FakePC(), state=state, now=1000)
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state, now=1000 + warden.PENDING_CI_LIMIT - 1)
        self.assertFalse(paused)
        pc = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc, state=state, now=1000 + warden.PENDING_CI_LIMIT)
        self.assertTrue(paused)
        self.assertIn("never finished", pc.pauses[0])

    def test_cost_ceiling_pauses_and_unset_ceiling_does_not(self):
        pc = FakePC(tokens=500)
        _, paused, _ = tick(pc=pc, cfg={"token_ceiling": 100})
        self.assertTrue(paused)
        pc = FakePC(tokens=500)
        _, paused, _ = tick(pc=pc, cfg={})
        self.assertFalse(paused)

    def test_push_refused_pauses(self):
        pc = FakePC()
        _, paused, _ = tick(pc=pc, requests=[{"verb": "push_refused"}])
        self.assertTrue(paused)

    def test_already_paused_company_is_not_paused_twice(self):
        pc = FakePC(paused=True)
        tick(pc=pc, requests=[{"verb": "push_refused"}])
        self.assertEqual(pc.pauses, [])


class StopWorkTests(unittest.TestCase):
    def test_pause_cancels_active_runs_and_blocks_in_progress_issues(self):
        pc = FakePC(active=["run-aaaaaaaa", "run-bbbbbbbb"], issues=["iss-11111111"])
        tick(pc=pc, requests=[{"verb": "push_refused"}])
        self.assertEqual(pc.cancelled, ["run-aaaaaaaa", "run-bbbbbbbb"])
        self.assertEqual(pc.blocked, ["iss-11111111"])

    def test_still_paused_next_tick_keeps_cancelling_stragglers(self):
        pc = FakePC(paused=True, active=["run-cccccccc"])
        tick(pc=pc)
        self.assertEqual(pc.cancelled, ["run-cccccccc"])
        self.assertEqual(pc.secrets, [])

    def test_unpaused_company_keeps_its_runs(self):
        pc = FakePC(active=["run-dddddddd"], issues=["iss-22222222"])
        _, paused, _ = tick(pc=pc)
        self.assertFalse(paused)
        self.assertEqual((pc.cancelled, pc.blocked), ([], []))

    def test_warden_never_resumes(self):
        self.assertFalse(hasattr(warden.Warden, "resume"))
        self.assertFalse(hasattr(warden.PaperclipHttp, "resume"))
        src = open(os.path.join(ROOT, "host/warden.py")).read()
        self.assertNotIn('"status": "active"', src)

    def test_daily_run_cap(self):
        self.assertIsNone(warden.runs_problem(999, None))
        self.assertIsNone(warden.runs_problem(10, 10))
        self.assertTrue(warden.runs_problem(11, 10))
        pc = FakePC(runs=11)
        _, paused, _ = tick(pc=pc, cfg={"max_runs_per_day": 10})
        self.assertTrue(paused)
        pc = FakePC(runs=11)
        self.assertFalse(tick(pc=pc, cfg={})[1])


def R(i, n=None, **over):
    """A cancelled engineer issue row, as the lead writes it."""
    row = {"id": i, "status": "cancelled", "description": f"Ticket: CB-{n or 100}\nfix"}
    row.update(over)
    return row


class PaperclipSignalTests(unittest.TestCase):
    def test_first_look_only_seeds_history(self):
        st = {}
        self.assertEqual(warden.new_releases(["a", "b"], st), [])
        self.assertEqual(warden.new_releases(["a", "b"], st), [])
        self.assertEqual(warden.new_releases(["a", "b", "c"], st), ["c"])
        self.assertEqual(warden.new_releases(["a", "b", "c"], st), [], "counted once")

    def test_two_newly_cancelled_issues_pause_without_any_spool_file(self):
        state, pc = {}, FakePC(cancelled=[R("old")])
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("old"), R("x")]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertFalse(paused, "one release is not a pause")
        pc.cancelled_issues = [R("old"), R("x"), R("y")]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("Paperclip issue states", pc.pauses[0])

    def test_a_merge_between_releases_resets_the_paperclip_streak(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("x")]
        tick(pc=pc, state=state)
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": {n: "success" for n in warden.REQUIRED_CHECKS}})
        tick(gh=gh, pc=pc, state=state)
        pc.cancelled_issues = [R("x"), R("y")]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertFalse(paused)

    def test_suppressing_the_spool_does_not_hide_releases(self):
        # No spool requests at all in either tick; Paperclip alone pauses.
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("x"), R("y")]
        self.assertTrue(tick(pc=pc, state=state)[1])

    def test_spool_release_and_paperclip_release_are_not_added_together(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("x")]
        _, paused, _ = tick(pc=pc, state=state, requests=[{"verb": "released", "ticket": "CB-1"}])
        self.assertFalse(paused, "one release reported twice is still one release")

    def test_refusal_text_in_a_run_log_pauses_once_per_run(self):
        state = {}
        pc = FakePC(logs={"run-11111111": "remote: error: GH013: Repository rule violations found"})
        _, paused, _ = tick(pc=pc, state=state)
        self.assertTrue(paused)
        pc2 = FakePC(logs={"run-11111111": "remote: error: GH013"})
        _, paused, _ = tick(pc=pc2, state=state)
        self.assertFalse(paused, "a run already read is not read again")

    def test_clean_logs_do_not_pause(self):
        pc = FakePC(logs={"run-22222222": "all good, pushed bugfix/pc-x"})
        self.assertFalse(tick(pc=pc)[1])
        self.assertFalse(warden.log_shows_refusal(None))


class ReleaseClassifyTests(unittest.TestCase):
    def no_fetch(self, i):
        raise AssertionError("must not fetch when the row carries a description")

    def test_listing_row_with_a_description_is_decided_from_it(self):
        c = warden.classify_release
        self.assertEqual(c(R("a", 12), self.no_fetch), ("release", "CB-12"))
        self.assertEqual(c(R("f", description="Ticket: CB-15"), self.no_fetch), ("release", "CB-15"))
        for d in ("Review PR 77 for CB-12", "Find the flaky test", "see Ticket: CB-14 below", "", None):
            self.assertEqual(c(R("q", description=d), self.no_fetch), ("other", None), d)

    def test_row_without_a_description_key_is_read_in_full(self):
        bare = {"id": "x", "status": "cancelled"}
        full = lambda i: {"id": i, "status": "cancelled", "description": "Ticket: CB-31\nx"}
        self.assertEqual(warden.classify_release(bare, full), ("release", "CB-31"))
        child = lambda i: {"id": i, "description": "QA round"}
        self.assertEqual(warden.classify_release(bare, child), ("other", None))

    def test_unreadable_when_the_full_issue_has_no_description_or_cannot_be_fetched(self):
        bare = {"id": "x", "status": "cancelled"}
        self.assertEqual(warden.classify_release(bare, lambda i: {"id": i}), ("unreadable", None))
        self.assertEqual(warden.classify_release(bare, lambda i: None), ("unreadable", None))

        def boom(i):
            raise OSError("500")
        self.assertEqual(warden.classify_release(bare, boom), ("unreadable", None))


class ReleaseTickTests(unittest.TestCase):
    def test_cancelled_qa_children_do_not_trip_two_in_a_row(self):
        kids = [{"id": f"k{i}", "status": "cancelled", "description": "QA round"} for i in range(3)]
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("e1", 20)] + kids
        _, paused, logs = tick(pc=pc, state=state)
        self.assertFalse(paused, "one engineer release plus three cancelled children is one release")
        self.assertTrue(any("CB-20 released" in l for l in logs))
        pc.cancelled_issues = [R("e1", 20), R("e2", 21)] + kids
        _, paused, logs = tick(pc=pc, state=state)
        self.assertTrue(paused)
        self.assertTrue(any("CB-21 released" in l for l in logs))

    def test_two_engineer_issues_of_one_ticket_in_one_pass_are_one_release(self):
        # CB-471: CB-415 was released once after its second fix round, both of
        # its engineer issues were cancelled, and the org paused on "2 in a row".
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415, parentId="pass1"), R("r2", 415, parentId="pass1")]
        _, paused, logs = tick(pc=pc, state=state)
        self.assertFalse(paused, "one ticket released once, with two fix rounds, is one release")
        self.assertEqual(state["paperclip_releases"], 1)
        self.assertTrue(any("CB-415 already counted" in l for l in logs))

    def test_the_same_ticket_released_in_a_second_pass_counts_again(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415, parentId="pass1")]
        self.assertFalse(tick(pc=pc, state=state)[1])
        pc.cancelled_issues = [R("r1", 415, parentId="pass1"), R("r3", 415, parentId="pass2")]
        self.assertTrue(tick(pc=pc, state=state)[1], "a second pass that also gives up is a second release")

    def test_two_tickets_released_in_one_pass_are_two_releases(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415, parentId="pass1"), R("r2", 416, parentId="pass1")]
        self.assertTrue(tick(pc=pc, state=state)[1])

    def test_issues_with_no_parent_are_each_counted(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415), R("r2", 415)]
        self.assertTrue(tick(pc=pc, state=state)[1], "with no pass to match on, the brake counts both")

    def test_row_missing_its_description_is_fetched_and_counted_when_it_is_an_engineer_issue(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [{"id": "m1", "status": "cancelled"}, {"id": "m2", "status": "cancelled"}]
        pc.full = {"m1": {"id": "m1", "description": "Ticket: CB-40\n"},
                   "m2": {"id": "m2", "description": "Ticket: CB-41\n"}}
        _, paused, logs = tick(pc=pc, state=state)
        self.assertTrue(paused, "two engineer issues, read through the full GET, are two releases")
        self.assertTrue(any("CB-40 released" in l for l in logs))

    def test_row_missing_its_description_is_not_counted_when_it_is_a_qa_child(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [{"id": f"m{i}", "status": "cancelled"} for i in range(3)]
        pc.full = {f"m{i}": {"id": f"m{i}", "description": "QA round"} for i in range(3)}
        self.assertFalse(tick(pc=pc, state=state)[1])

    def test_unreadable_signal_pauses_and_says_so(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [{"id": "zz112233", "status": "cancelled"}]
        pc.full = {"zz112233": {"id": "zz112233"}}
        _, paused, logs = tick(pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("release signal unreadable", pc.pauses[0])
        self.assertTrue(any("release signal unreadable" in l for l in logs))

    def test_history_is_not_fetched(self):
        pc = FakePC(cancelled=[{"id": "old", "status": "cancelled"}])
        self.assertFalse(tick(pc=pc, state={})[1], "the first look seeds history without reading it")


REAL_LOG = {"runId": "r1", "store": "local_file", "logRef": "x/r1.ndjson",
            "content": '{"ts":"2026-11-24T10:00:00Z","stream":"stdout","chunk":"pushing\\n"}\n'
                       '{"ts":"2026-11-24T10:00:01Z","stream":"stderr","chunk":"remote: error: GH0"}\n'
                       '{"ts":"2026-11-24T10:00:01Z","stream":"stderr","chunk":"13: Repository rule violations found\\n"}\n'}


class PauseWindowTests(unittest.TestCase):
    """CB-472: a pause does not stop a ticket's ninety minutes, so a release
    soon after a pause is not counted toward two in a row."""

    def test_windows_open_on_the_first_paused_tick_and_close_on_the_first_active_one(self):
        w = warden.note_pause_state([], False, 100)
        self.assertEqual(w, [])
        w = warden.note_pause_state(w, True, 200)
        w = warden.note_pause_state(w, True, 500)  # still paused: same window
        self.assertEqual(w, [{"paused_at": 200, "resumed_at": None}])
        w = warden.note_pause_state(w, False, 800)
        w = warden.note_pause_state(w, False, 1100)
        self.assertEqual(w, [{"paused_at": 200, "resumed_at": 800}])
        w = warden.note_pause_state(w, True, 1400)
        self.assertEqual(len(w), 2)

    def test_only_the_last_twenty_are_kept(self):
        w = []
        for i in range(30):
            w = warden.note_pause_state(w, True, i * 10)
            w = warden.note_pause_state(w, False, i * 10 + 5)
        self.assertEqual(len(w), 20)
        self.assertEqual(w[0]["paused_at"], 100)

    def test_paused_within_the_ticket_window(self):
        T = warden.TICKET_WINDOW
        now = 100_000
        self.assertTrue(warden.paused_within([{"paused_at": now - 10, "resumed_at": None}], now))
        self.assertTrue(warden.paused_within([{"paused_at": now - T - 600, "resumed_at": now - T}], now))
        self.assertFalse(warden.paused_within([{"paused_at": now - T - 600, "resumed_at": now - T - 1}], now))
        self.assertFalse(warden.paused_within([], now))
        self.assertFalse(warden.paused_within([{"paused_at": now + 5, "resumed_at": None}], now))

    def test_the_warden_records_its_own_pause_and_the_resume(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state, now=1000)
        pc.cancelled_issues = [R("x"), R("y")]
        self.assertTrue(tick(pc=pc, state=state, now=1300)[1])
        self.assertEqual(state["pauses"], [{"paused_at": 1300, "resumed_at": None}])
        pc.paused = False  # a person resumes
        tick(pc=pc, state=state, now=1600)
        self.assertEqual(state["pauses"], [{"paused_at": 1300, "resumed_at": 1600}])

    def test_a_pause_by_someone_else_is_recorded_when_seen(self):
        state = {}
        tick(pc=FakePC(paused=True), state=state, now=5000)
        self.assertEqual(state["pauses"], [{"paused_at": 5000, "resumed_at": None}])

    def test_releases_after_a_pause_are_not_counted_control_without_one_still_pauses(self):
        # The 2026-11-25 shape: a pause, a resume, then two releases forced by
        # the clocks that kept running through it.
        state, pc = {}, FakePC()
        tick(pc=pc, state=state, now=0)
        pc.paused = True
        tick(pc=pc, state=state, now=300)
        pc.paused = False
        tick(pc=pc, state=state, now=2700)
        pc.cancelled_issues = [R("a", 461), R("b", 432)]
        _, paused, logs = tick(pc=pc, state=state, now=3000)
        self.assertFalse(paused)
        self.assertEqual(state.get("paperclip_releases", 0), 0)
        self.assertEqual(sum("not counted: the company was paused during this ticket's window" in l
                             for l in logs), 2)
        # Control: once the window has passed, two releases pause as before.
        later = 2700 + warden.TICKET_WINDOW + 1
        pc.cancelled_issues += [R("c", 500), R("d", 501)]
        _, paused, logs = tick(pc=pc, state=state, now=later)
        self.assertTrue(paused)
        self.assertTrue(any("CB-501 released" in l and "2 in a row" in l for l in logs))

    def test_one_per_pass_still_holds_across_an_uncounted_release(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state, now=0)
        state["pauses"] = [{"paused_at": 100, "resumed_at": 200}]
        pc.cancelled_issues = [R("r1", 415, parentId="pass1")]
        tick(pc=pc, state=state, now=300)
        self.assertEqual(state.get("paperclip_releases", 0), 0)
        # The pass's second engineer issue, seen after the window: still one release, already handled.
        pc.cancelled_issues.append(R("r2", 415, parentId="pass1"))
        _, _, logs = tick(pc=pc, state=state, now=300 + warden.TICKET_WINDOW + 200)
        self.assertEqual(state.get("paperclip_releases", 0), 0)
        self.assertTrue(any("CB-415 already counted" in l for l in logs))

    def test_pauses_file_is_written_as_iso_and_skipped_by_the_spool_reader(self):
        with tempfile.TemporaryDirectory() as d:
            warden.write_pauses(d, [{"paused_at": 0, "resumed_at": 60}, {"paused_at": 120, "resumed_at": None}])
            path = os.path.join(d, "pauses.json")
            with open(path) as f:
                self.assertEqual(json.load(f), [
                    {"paused_at": "1970-01-01T00:00:00Z", "resumed_at": "1970-01-01T00:01:00Z"},
                    {"paused_at": "1970-01-01T00:02:00Z", "resumed_at": None}])
            if os.name != "nt":
                self.assertEqual(os.stat(path).st_mode & 0o777, 0o644)
            logs = []
            self.assertEqual(warden.read_spool(d, logs.append), [])
            self.assertTrue(os.path.exists(path), "pauses.json is not a request and stays put")
            self.assertEqual(logs, [])
            warden.write_pauses(d, [])
            with open(path) as f:
                self.assertEqual(json.load(f), [])

    def test_the_tick_publishes_the_file_and_a_write_failure_does_not_stop_the_pause(self):
        with tempfile.TemporaryDirectory() as d:
            state, pc = {}, FakePC(paused=True)
            tick(pc=pc, state=state, cfg={"spool": d}, now=0)
            with open(os.path.join(d, "pauses.json")) as f:
                self.assertEqual(json.load(f), [{"paused_at": "1970-01-01T00:00:00Z", "resumed_at": None}])
        state, pc = {}, FakePC()
        tick(pc=pc, state=state, now=0)
        pc.cancelled_issues = [R("x"), R("y")]
        _, paused, logs = tick(pc=pc, state=state, cfg={"spool": "/nonexistent/spool"}, now=300)
        self.assertTrue(paused)
        self.assertEqual(pc.pauses and len(pc.pauses), 1)
        self.assertTrue(any("pauses.json not written" in l for l in logs))


class RunLogTests(unittest.TestCase):
    def test_parses_the_measured_shape_and_joins_chunks(self):
        text = warden.run_log_text(REAL_LOG)
        self.assertIn("pushing", text)
        self.assertTrue(warden.log_shows_refusal(text), "GH013 split across chunks must still be found")

    def test_clean_log_has_no_refusal(self):
        clean = dict(REAL_LOG, content='{"ts":"t","stream":"stdout","chunk":"all good"}')
        self.assertFalse(warden.log_shows_refusal(warden.run_log_text(clean)))

    def test_odd_lines_are_kept_not_dropped(self):
        odd = dict(REAL_LOG, content='not json remote: error: GH013: Repository rule violations found\n{"chunk": 5}\n[1]')
        self.assertTrue(warden.log_shows_refusal(warden.run_log_text(odd)))

    def test_the_push_summary_form_is_a_refusal(self):
        self.assertTrue(warden.log_shows_refusal(
            " ! [remote rejected] HEAD -> feature/x (push declined due to repository rule violations)"))

    def test_the_bare_code_in_instructions_or_quoted_text_is_not_a_refusal(self):
        # The two texts that paused the org on 2026-11-24 with no push refused (CB-462).
        lead_instructions = ("## Stop and report instead if\nThe fix would need a real machine, the "
                             "mobile repo, or a change under `.github/workflows/`; or a push is refused (GH013).")
        probe_description = 'git push "$R" x 2>&1 | grep -o "GH013[^.]*" | head -1'
        for text in (lead_instructions, probe_description, "GH013", "see GH013 in the skill"):
            self.assertFalse(warden.log_shows_refusal(text), text)

    def test_wrong_shapes_read_as_no_log(self):
        for resp in (None, [], {}, {"content": None}, {"content": 5}):
            self.assertEqual(warden.run_log_text(resp), "")

    def _pc(self, error):
        pc = warden.PaperclipHttp.__new__(warden.PaperclipHttp)

        def call(method, path, body=None):
            if error:
                raise error
            return REAL_LOG
        pc.call = call
        return pc

    def test_404_for_a_run_that_never_started_is_no_log(self):
        err = urllib.error.HTTPError("u", 404, "Heartbeat run not found", {}, None)
        self.assertEqual(self._pc(err).run_log("r"), "")

    def test_other_http_errors_propagate_and_200_parses(self):
        with self.assertRaises(urllib.error.HTTPError):
            self._pc(urllib.error.HTTPError("u", 500, "boom", {}, None)).run_log("r")
        self.assertIn("GH013", self._pc(None).run_log("r"))

    def test_tick_survives_an_unreadable_log_and_retries_it(self):
        class Boom(FakePC):
            def run_log(self, run_id):
                raise OSError("500")
        state = {}
        pc = Boom(logs={"run-33333333": ""})
        _, paused, logs = tick(pc=pc, state=state)
        self.assertFalse(paused)
        self.assertTrue(any("unreadable" in l for l in logs))
        self.assertNotIn("run-33333333", state.get("scanned_runs", []), "retried next tick")

    def test_a_run_with_no_log_is_scanned_once_without_pausing(self):
        state = {}
        pc = FakePC(logs={"run-44444444": ""})
        self.assertFalse(tick(pc=pc, state=state)[1])
        self.assertIn("run-44444444", state["scanned_runs"])


# ---------------------------------------------------------------- unstick (CB-479)

NOW = 1_800_000_000.0


def I(i, status, parent=None, idle=3600, assignee="lead", **over):
    """An issue row as the company listing returns it (measured fields)."""
    row = {"id": i, "identifier": f"QAF-{i}", "status": status, "parentId": parent,
           "assigneeAgentId": assignee, "activeRun": None,
           "lastActivityAt": warden.iso(NOW - idle), "updatedAt": warden.iso(NOW - idle)}
    row.update(over)
    return row


def RUN(issue, status="succeeded", agent="eng", at=NOW - 3600, **over):
    row = {"id": f"run-{issue}-{status}", "issueId": issue, "agentId": agent, "status": status,
           "createdAt": warden.iso(at), "contextSnapshot": {"taskKey": issue}}
    row.update(over)
    return row


def EXHAUSTED(issue, **over):
    return RUN(issue, "cancelled", errorCode="ai_connection_pool_exhausted",
               error="All eligible pool accounts are over their usage limit. Waiting for usage to reset.",
               **over)


POOL_OK = {"accounts": {"a": {"status": "ok", "limits": [{"usedPercent": 100}]},
                        "b": {"status": "ok", "limits": [{"usedPercent": 7}, {"usedPercent": 88},
                                                         {"usedPercent": None}]}}}
POOL_FULL = {"accounts": {"a": {"status": "ok", "limits": [{"usedPercent": 100}]},
                          "b": {"status": "ok", "limits": [{"usedPercent": 95}]}}}


def decide(issues, runs=(), said=None, state=None, now=NOW, pool_ok=True):
    return [(a["kind"], a["id"]) for a in
            warden.unstick_actions(list(issues), list(runs), said or {}, state or {}, now, pool_ok)]


# The CB-424 release of 2026-11-26: pass P blocked, its two engineer issues
# cancelled on the release, QA child done, the lead's report after all three.
def released_pass():
    return [I("P", "blocked", idle=11 * 60),
            I("e1", "cancelled", "P", idle=12 * 60, assignee="eng"),
            I("e2", "cancelled", "P", idle=12 * 60, assignee="eng"),
            I("q1", "done", "P", idle=20 * 60, assignee="qa")]


class UnstickParentTests(unittest.TestCase):
    def test_parent_is_closed_when_the_assignee_reported_after_its_children_settled(self):
        self.assertEqual(decide(released_pass(), said={"P": NOW - 11 * 60}), [("close_parent", "P")])

    def test_parent_with_no_newer_report_is_woken_then_closed_after_the_grace(self):
        old_report = {"P": NOW - 3 * 3600}
        self.assertEqual(decide(released_pass(), said=old_report), [("wake_parent", "P")])
        st = {"parent_woken": {"P": NOW}}
        self.assertEqual(decide(released_pass(), said=old_report, state=st, now=NOW + 14 * 60), [],
                         "inside the 15-minute grace nothing happens")
        self.assertEqual(decide(released_pass(), said=old_report, state=st, now=NOW + 15 * 60),
                         [("close_parent", "P")])

    def test_control_a_child_with_a_live_run_leaves_the_parent_alone(self):
        issues = released_pass()
        self.assertEqual(decide(issues, runs=[RUN("q1", "running")], said={"P": NOW}), [])
        issues[3]["activeRun"] = {"id": "r"}
        self.assertEqual(decide(issues, said={"P": NOW}), [])

    def test_control_parent_with_its_own_live_run_is_left_alone(self):
        self.assertEqual(decide(released_pass(), runs=[RUN("P", "queued")], said={"P": NOW}), [])

    def test_control_an_open_child_leaves_the_parent_blocked(self):
        issues = released_pass() + [I("e3", "in_review", "P", idle=5 * 60, assignee="eng")]
        self.assertEqual(decide(issues, said={"P": NOW}), [])

    def test_control_a_recently_touched_parent_is_not_yet_looked_at(self):
        issues = released_pass()
        issues[0] = I("P", "blocked", idle=9 * 60)
        self.assertEqual(decide(issues, said={"P": NOW}), [])

    def test_control_blocked_with_no_children_is_not_this_rule(self):
        # stop_work blocks in-progress issues on a pause; those have no
        # settled children and must not be closed once the company resumes.
        self.assertEqual(decide([I("P", "blocked")]), [])


class UnstickChildTests(unittest.TestCase):
    def kids(self, idle=21 * 60, **over):
        return [I("P", "in_progress", idle=60), I("c", "in_progress", "P", idle=idle, assignee="eng", **over)]

    def test_stalled_child_is_woken_twice_then_cancelled(self):
        self.assertEqual(decide(self.kids()), [("wake_child", "c")])
        st = {"child_wakes": {"c": [1, NOW - 21 * 60]}}
        self.assertEqual(decide(self.kids(), state=st), [("wake_child", "c")])
        st = {"child_wakes": {"c": [2, NOW - 21 * 60]}}
        self.assertEqual(decide(self.kids(), state=st), [("cancel_child", "c")])

    def test_no_second_wake_inside_twenty_minutes_of_the_first(self):
        self.assertEqual(decide(self.kids(), state={"child_wakes": {"c": [1, NOW - 19 * 60]}}), [])

    def test_controls_recent_activity_live_run_no_assignee_or_no_parent(self):
        self.assertEqual(decide(self.kids(idle=19 * 60)), [], "active inside 20 minutes")
        self.assertEqual(decide(self.kids(), runs=[RUN("c", "running")]), [], "has a live run")
        self.assertEqual(decide(self.kids(assigneeAgentId=None)), [], "nobody to wake")
        self.assertEqual(decide([I("c", "todo", idle=3600)]), [], "a root task is not a child")
        for s in ("done", "cancelled", "blocked", "backlog"):
            self.assertEqual(decide([I("P", "in_progress", idle=60), I("c", s, "P", idle=3600)]), [], s)

    def test_each_stallable_status_counts(self):
        for s in ("todo", "in_progress", "in_review"):
            self.assertEqual(decide([I("c", s, "P", idle=3600)]), [("wake_child", "c")], s)


class UnstickUsageTests(unittest.TestCase):
    def test_exhausted_task_gets_a_session_reset_when_another_account_has_room(self):
        acts = warden.unstick_actions([I("c", "in_progress", "P")], [EXHAUSTED("c", agent="eng")],
                                      {}, {}, NOW, True)
        self.assertEqual([(a["kind"], a["agent_id"], a["task_key"]) for a in acts],
                         [("reset_session", "eng", "c")])

    def test_not_while_the_pool_is_full_and_not_woken_or_cancelled_either(self):
        st = {"child_wakes": {"c": [2, NOW - 3600]}}
        self.assertEqual(decide([I("c", "in_progress", "P")], [EXHAUSTED("c")], state=st, pool_ok=False), [])

    def test_once_per_task_per_hour(self):
        runs = [EXHAUSTED("c")]
        self.assertEqual(decide([I("c", "todo", "P")], runs, state={"resets": {"c": NOW - 59 * 60}}), [])
        self.assertEqual(decide([I("c", "todo", "P")], runs, state={"resets": {"c": NOW - 3600}}),
                         [("reset_session", "c")])

    def test_only_the_latest_run_decides(self):
        runs = [EXHAUSTED("c", at=NOW - 7200), RUN("c", "succeeded", at=NOW - 3600)]
        self.assertEqual(decide([I("c", "todo", "P")], runs), [("wake_child", "c")])
        other = RUN("c", "cancelled", at=NOW - 60, error="cancelled by the board")
        self.assertEqual(decide([I("c", "todo", "P")], [EXHAUSTED("c", at=NOW - 7200), other]),
                         [("wake_child", "c")], "a cancel for another reason is not this rule")

    def test_pool_headroom(self):
        self.assertTrue(warden.pool_has_headroom(POOL_OK))
        self.assertFalse(warden.pool_has_headroom(POOL_FULL))
        self.assertFalse(warden.pool_has_headroom({}))
        self.assertFalse(warden.pool_has_headroom(None))
        self.assertFalse(warden.pool_has_headroom(
            {"accounts": {"b": {"status": "error", "limits": [{"usedPercent": 1}]}}}), "not ok")
        self.assertFalse(warden.pool_has_headroom(
            {"accounts": {"b": {"status": "ok", "limits": [{"usedPercent": 1, "limitReached": True}]}}}))
        self.assertFalse(warden.pool_has_headroom(
            {"accounts": {"b": {"status": "ok", "limits": [{"usedPercent": None}]}}}), "no numbers")
        self.assertFalse(warden.pool_has_headroom(
            {"accounts": {"b": {"status": "ok", "limits": [{"usedPercent": 90}]}}}), "90 is not under 90")


class UnstickTickTests(unittest.TestCase):
    def pc(self, issues, **over):
        pc = FakePC(**over)
        pc.all, pc.pool = issues, POOL_OK
        return pc

    def run_tick(self, pc, state, now=NOW, cfg=None):
        return tick(pc=pc, state=state, now=now, cfg=cfg or {"company_id": "co"})

    def test_released_pass_is_closed_and_logged(self):
        pc = self.pc(released_pass())
        pc.said = {"P": NOW - 11 * 60}
        _, paused, logs = self.run_tick(pc, {})
        self.assertFalse(paused)
        self.assertEqual(pc.statuses, [("P", "done")])
        self.assertIn("UNSTUCK: close_parent QAF-P", logs)

    def test_wake_then_close_across_ticks(self):
        state, pc = {}, self.pc(released_pass())
        self.run_tick(pc, state)
        self.assertEqual([c[0] for c in pc.comments], ["P"])
        self.assertEqual(state["unstick"]["parent_woken"], {"P": NOW})
        self.run_tick(pc, state, now=NOW + 5 * 60)
        self.assertEqual(pc.statuses, [], "no close inside the grace, and no second wake")
        self.assertEqual(len(pc.comments), 1)
        self.run_tick(pc, state, now=NOW + 15 * 60)
        self.assertEqual(pc.statuses, [("P", "done")])

    def test_paused_company_gets_no_action(self):
        pc = self.pc(released_pass(), paused=True)
        pc.said = {"P": NOW}
        _, paused, logs = self.run_tick(pc, {})
        self.assertTrue(paused)
        self.assertEqual((pc.statuses, pc.comments, pc.resets), ([], [], []))
        self.assertFalse(any("UNSTUCK" in l for l in logs))

    def test_a_tick_that_pauses_the_company_takes_no_action(self):
        pc = self.pc(released_pass())
        pc.said, pc.tokens = {"P": NOW}, 10
        _, paused, _ = self.run_tick(pc, {}, cfg={"company_id": "co", "token_ceiling": 1})
        self.assertTrue(paused)
        self.assertEqual((pc.statuses, pc.comments), ([], []))

    def test_companies_come_from_config_and_each_pause_is_honoured(self):
        pc = self.pc(released_pass())
        pc.said = {"P": NOW}
        pc.paused_cos = {"triage"}
        self.run_tick(pc, {}, cfg={"company_id": "co", "unstick_company_ids": ["triage"]})
        self.assertEqual(pc.statuses, [], "the only configured company is paused")
        pc.paused_cos = set()
        self.run_tick(pc, {}, cfg={"company_id": "co", "unstick_company_ids": ["triage"]})
        self.assertEqual(pc.statuses, [("P", "done")])

    def test_sweep_cancel_wakes_the_parent_and_is_not_a_release(self):
        state = {}
        kid = I("c", "in_progress", "P", idle=3600, assignee="eng",
                description="Ticket: CB-500\nfix")
        pc = self.pc([I("P", "in_progress", idle=60), kid])
        self.run_tick(pc, state)                              # seeds the release history
        state["unstick"]["child_wakes"] = {"c": [2, NOW - 3600]}
        _, _, logs = self.run_tick(pc, state, now=NOW + 1)
        self.assertEqual(pc.statuses, [("c", "cancelled")])
        self.assertEqual(pc.comments[-1][0], "P", "the parent is told")
        self.assertIn("UNSTUCK: cancel_child QAF-c", logs)
        # Paperclip now lists it cancelled, beside one real release.
        pc.all = [I("P", "in_progress", idle=60), dict(kid, status="cancelled")]
        pc.cancelled_issues = [R("c", 500, parentId="P"), R("real", 501, parentId="P2")]
        _, paused, logs = self.run_tick(pc, state, now=NOW + 300)
        self.assertFalse(paused, "a sweep cancel plus one release is one release")
        self.assertEqual(state["paperclip_releases"], 1)
        self.assertTrue(any("cancelled by the unstick sweep, not a release" in l for l in logs))

    def test_control_a_cancel_by_anyone_else_still_counts(self):
        state, pc = {}, self.pc([])
        self.run_tick(pc, state)
        pc.cancelled_issues = [R("c", 500, parentId="P"), R("real", 501, parentId="P2")]
        self.assertTrue(self.run_tick(pc, state, now=NOW + 300)[1])

    def test_usage_reset_through_the_tick_and_unreadable_pool_waits(self):
        pc = self.pc([I("c", "in_progress", "P")], )
        pc.run_rows = [EXHAUSTED("c", agent="eng")]
        state = {}
        self.run_tick(pc, state)
        self.assertEqual(pc.resets, [("eng", "c")])
        self.assertEqual(pc.comments[0][0], "c")
        self.assertIn("c", state["unstick"]["resets"])
        pc2 = self.pc([I("c", "in_progress", "P")])
        pc2.run_rows, pc2.pool = [EXHAUSTED("c")], None
        _, _, logs = self.run_tick(pc2, {})
        self.assertEqual(pc2.resets, [])
        self.assertTrue(any("pool usage unreadable" in l for l in logs))

    def test_unreadable_listing_is_logged_and_does_nothing(self):
        pc = self.pc(released_pass())
        pc.fail_reads = True
        _, paused, logs = self.run_tick(pc, {})
        self.assertFalse(paused)
        self.assertTrue(any("unstick sweep unreadable" in l for l in logs))

    def test_a_failed_write_is_logged_and_not_recorded(self):
        state, pc = {}, self.pc(released_pass())
        pc.fail_writes = True
        _, _, logs = self.run_tick(pc, state)
        self.assertTrue(any("unstick wake_parent QAF-P failed: OSError" in l for l in logs))
        self.assertEqual(state["unstick"].get("parent_woken", {}), {}, "retried next tick, not skipped")

    def test_state_forgets_what_no_longer_applies(self):
        st = {"parent_woken": {"P": NOW, "gone": NOW}, "child_wakes": {"c": [1, NOW], "d": [2, NOW]},
              "resets": {"x": NOW - 3600, "y": NOW - 60}}
        warden.prune_unstick_state(st, [I("P", "in_progress"), I("c", "todo"), I("d", "done")], NOW)
        self.assertEqual(st["parent_woken"], {})
        self.assertEqual(st["child_wakes"], {"c": [1, NOW]})
        self.assertEqual(st["resets"], {"y": NOW - 60})

    def test_timestamps(self):
        self.assertEqual(warden.ts("1970-01-01T00:01:00.000Z"), 60.0)
        self.assertIsNone(warden.ts(None))
        self.assertIsNone(warden.ts("not a time"))
        self.assertIsNone(warden.activity_at({}))


class CheckHostTests(unittest.TestCase):
    GOOD = {"enableIsolatedWorkspaces": True, "enableWorktreeRunExecution": True,
            "enableIsolatedWorkspacesByDefault": False}

    def test_flags(self):
        self.assertEqual(check_host.flag_problems(self.GOOD), [])
        for k, v in (("enableIsolatedWorkspaces", False), ("enableWorktreeRunExecution", None),
                     ("enableIsolatedWorkspacesByDefault", True)):
            self.assertEqual(len(check_host.flag_problems(dict(self.GOOD, **{k: v}))), 1, k)
        self.assertEqual(len(check_host.flag_problems({})), 2)

    def test_plist(self):
        ok = "<key>GIT_CONFIG_GLOBAL</key>\n<string>/g/gitconfig</string>"
        self.assertEqual(check_host.plist_problems(ok, "/g/gitconfig"), [])
        self.assertEqual(len(check_host.plist_problems("<dict/>", "/g/gitconfig")), 1)
        self.assertEqual(len(check_host.plist_problems(ok, "/other")), 1)

    def test_gitconfig_must_exist_and_be_empty(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "gitconfig")
            self.assertEqual(len(check_host.gitconfig_problems(p)), 1)
            open(p, "w").close()
            self.assertEqual(check_host.gitconfig_problems(p), [])
            with open(p, "w") as f:
                f.write("[credential]\n")
            self.assertEqual(len(check_host.gitconfig_problems(p)), 1)

    def test_config(self):
        good = {"company_id": "c", "paperclip_login": "l", "spool": "s", "max_runs_per_day": 8}
        self.assertEqual(check_host.config_problems(good), [])
        self.assertEqual(len(check_host.config_problems({})), 3)
        self.assertEqual(len(check_host.config_problems(dict(good, max_runs_per_day=0))), 1)
        self.assertEqual(len(check_host.config_problems(dict(good, token_ceiling="9"))), 1)


@unittest.skipUnless(sys.platform == "darwin", "PlistBuddy and the Mac mini's launchd")
class GitConfigSetupTests(unittest.TestCase):
    PLIST = ('<?xml version="1.0" encoding="UTF-8"?>\n<plist version="1.0"><dict>'
             '<key>Label</key><string>x</string>%s</dict></plist>\n')

    def run_script(self, d, plist_body):
        plist = os.path.join(d, "svc.plist")
        with open(plist, "w") as f:
            f.write(self.PLIST % plist_body)
        env = dict(os.environ, PAPERCLIP_DIR=d, SERVICE_PLIST=plist, NO_RELOAD="1")
        p = subprocess.run(["bash", os.path.join(ROOT, "host/setup-git-config.sh")],
                           capture_output=True, text=True, env=env)
        with open(plist) as f:
            return p, f.read()

    def test_adds_the_variable_and_the_empty_file_when_missing(self):
        with tempfile.TemporaryDirectory() as d:
            p, text = self.run_script(d, "")
            self.assertEqual(p.returncode, 0, p.stderr)
            self.assertEqual(check_host.plist_problems(text, os.path.join(d, "gitconfig")), [])
            self.assertEqual(os.path.getsize(os.path.join(d, "gitconfig")), 0)
            self.assertIn("set GIT_CONFIG_GLOBAL", p.stdout)

    def test_already_correct_plist_is_left_alone(self):
        with tempfile.TemporaryDirectory() as d:
            body = ("<key>EnvironmentVariables</key><dict><key>GIT_CONFIG_GLOBAL</key><string>%s</string></dict>"
                    % os.path.join(d, "gitconfig"))
            p, text = self.run_script(d, body)
            self.assertEqual(p.returncode, 0, p.stderr)
            self.assertIn("nothing to do", p.stdout)
            self.assertEqual(text, self.PLIST % body)

    def test_wrong_value_is_corrected(self):
        with tempfile.TemporaryDirectory() as d:
            body = ("<key>EnvironmentVariables</key><dict><key>GIT_CONFIG_GLOBAL</key><string>/etc/gitconfig</string></dict>")
            p, text = self.run_script(d, body)
            self.assertEqual(p.returncode, 0, p.stderr)
            self.assertEqual(check_host.plist_problems(text, os.path.join(d, "gitconfig")), [])

    def test_refuses_a_non_empty_existing_file(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "gitconfig"), "w") as f:
                f.write("[credential]\n")
            p, _ = self.run_script(d, "")
            self.assertNotEqual(p.returncode, 0)
            self.assertIn("not empty", p.stderr)


class SpoolTests(unittest.TestCase):
    def test_valid_and_bad_files_are_sorted_into_done_and_bad(self):
        with tempfile.TemporaryDirectory() as d:
            open(os.path.join(d, "1.json"), "w").write('{"verb":"push_refused"}')
            open(os.path.join(d, "2.json"), "w").write("{nope")
            open(os.path.join(d, "3.json"), "w").write('{"verb":"exec","cmd":"id"}')
            open(os.path.join(d, "ignored.txt"), "w").write("x")
            logs = []
            reqs = warden.read_spool(d, logs.append)
            self.assertEqual(reqs, [{"verb": "push_refused"}])
            self.assertEqual(sorted(os.listdir(os.path.join(d, "done"))), ["1.json"])
            self.assertEqual(sorted(os.listdir(os.path.join(d, "bad"))), ["2.json", "3.json"])
            self.assertTrue(os.path.exists(os.path.join(d, "ignored.txt")))
            self.assertEqual(len(logs), 2)
            self.assertEqual(warden.read_spool(d, logs.append), [], "a file is consumed once")


class PlistTests(unittest.TestCase):
    def test_launchd_plist_runs_the_warden_every_five_minutes(self):
        with open(os.path.join(ROOT, "host/launchd/org.uplift.orbweaver-bugfix-warden.plist"), "rb") as f:
            p = plistlib.load(f)
        self.assertEqual(p["StartInterval"], 300)
        self.assertTrue(p["ProgramArguments"][1].endswith("host/warden.py"))
        self.assertEqual(p["ProgramArguments"][2], "run-once")
        self.assertLess(p["StartInterval"], warden.MINT_INTERVAL, "ticks must be finer than the mint interval")


# ---------------------------------------------------------------- rulesets

def _jload(path):
    with open(path) as f:
        return json.load(f)


def ruleset(name):
    return _jload(os.path.join(ROOT, "host/rulesets", name))


ADMIN_ROLE = {"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"}


class RulesetJsonTests(unittest.TestCase):
    def test_main_is_admins_only(self):
        r = ruleset("main-admins-only.json")
        self.assertEqual(r["target"], "branch")
        self.assertEqual(r["conditions"]["ref_name"]["include"], ["refs/heads/main"])
        self.assertIn({"type": "update"}, r["rules"])
        self.assertEqual(r["bypass_actors"], [ADMIN_ROLE])

    def test_other_branches_exclude_the_orgs_lane_and_keep_bots_working(self):
        r = ruleset("other-branches-admins-only.json")
        self.assertEqual(r["conditions"]["ref_name"]["include"], ["~ALL"])
        self.assertEqual(sorted(r["conditions"]["ref_name"]["exclude"]),
                         ["refs/heads/bugfix/pc-*", "refs/heads/develop", "refs/heads/main",
                          "refs/heads/screenshots"])
        self.assertEqual({x["type"] for x in r["rules"]}, {"creation", "update", "deletion"})
        ids = {(a["actor_type"], a["actor_id"]) for a in r["bypass_actors"]}
        # GitHub Actions cannot be a bypass actor on a repository ruleset (GitHub
        # answers 422), so `screenshots`, the only branch Actions pushes, is
        # excluded above instead.
        self.assertEqual(ids, {("RepositoryRole", 5), ("Integration", 29110)})

    def test_tags_restrict_creation_to_admins(self):
        r = ruleset("tags-admins-only.json")
        self.assertEqual(r["target"], "tag")
        self.assertEqual(r["rules"], [{"type": "creation"}])
        self.assertEqual(r["bypass_actors"], [ADMIN_ROLE])

    def test_no_ruleset_lets_the_app_bypass_and_all_are_active(self):
        names = set()
        for f in sorted(os.listdir(os.path.join(ROOT, "host/rulesets"))):
            r = ruleset(f)
            names.add(r["name"])
            self.assertEqual(r["enforcement"], "active", f)
            for a in r["bypass_actors"]:
                self.assertNotIn("bugfix", json.dumps(a), f)
        self.assertEqual(len(names), 3)
        # Control: names are distinct from the two rulesets already on the repo.
        self.assertFalse(names & {"main and develop: PRs only", "v* tags: admins only"})


STUB_GH = r"""#!/usr/bin/env bash
echo "$@" >> "$GH_LOG"
case "$*" in
  "api apps/dependabot --jq .id") echo "${DEPENDABOT_ID:-29110}" ;;
  "api repos/Uplift-Foundation/Orbweaver/rulesets --jq"*) printf '%b' "${EXISTING:-}" ;;
esac
"""


@unittest.skipIf(sys.platform == "win32", "the apply script and its stub gh are bash; the host is a Mac")
class ApplyScriptTests(unittest.TestCase):
    def run_script(self, *args, **env):
        with tempfile.TemporaryDirectory() as d:
            gh = os.path.join(d, "gh")
            open(gh, "w").write(STUB_GH)
            os.chmod(gh, 0o755)
            log = os.path.join(d, "log")
            e = dict(os.environ, PATH=d + os.pathsep + os.environ["PATH"], GH_LOG=log, **env)
            p = subprocess.run(["bash", os.path.join(ROOT, "host/apply-rulesets.sh"), *args],
                               capture_output=True, text=True, env=e)
            calls = open(log).read().splitlines() if os.path.exists(log) else []
            return p, calls

    def writes(self, calls):
        return [c for c in calls if "-X POST" in c or "-X PUT" in c]

    def test_dry_run_writes_nothing_and_names_three_posts(self):
        p, calls = self.run_script()
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(self.writes(calls), [])
        self.assertEqual(p.stdout.count("POST repos/Uplift-Foundation/Orbweaver/rulesets"), 3)
        self.assertIn("dry run", p.stdout)

    def test_apply_posts_each_ruleset_with_its_file(self):
        p, calls = self.run_script("--apply")
        self.assertEqual(p.returncode, 0, p.stderr)
        w = self.writes(calls)
        self.assertEqual(len(w), 3)
        self.assertTrue(all("--input" in c and c.endswith(".json") for c in w))

    def test_existing_ruleset_of_the_same_name_is_put_not_duplicated(self):
        name = ruleset("tags-admins-only.json")["name"]
        p, calls = self.run_script("--apply", EXISTING=f"99\\t{name}\\n")
        self.assertEqual(p.returncode, 0, p.stderr)
        w = self.writes(calls)
        self.assertEqual(sum("-X PUT repos/Uplift-Foundation/Orbweaver/rulesets/99" in c for c in w), 1)
        self.assertEqual(sum("-X POST" in c for c in w), 2)

    def test_wrong_bypass_app_id_refuses_before_any_write(self):
        for env in ({"DEPENDABOT_ID": "2"},):
            p, calls = self.run_script("--apply", **env)
            self.assertNotEqual(p.returncode, 0)
            self.assertIn("refusing", p.stderr)
            self.assertEqual(self.writes(calls), [])


# ---------------------------------------------------------------- stress-run

VSTEST_OK = "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s"
VSTEST_RED = "Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 1 s"
MTP_OK = "Test run summary: Passed!\n  total: 20\n  failed: 0\n  succeeded: 20\n  skipped: 0"
VSTEST_NONE = "No test matches the given testcase filter"


class StressRunTests(unittest.TestCase):
    def run_with(self, outputs, suite="unit", cls="A.B", n=None):
        it = iter(outputs)
        lines = []
        rc = stress.stress(suite, cls, n or len(outputs), runner=lambda cmd: next(it), out=lines.append)
        return rc, lines

    def test_judge(self):
        self.assertEqual(stress.judge(0, VSTEST_OK), "green")
        self.assertEqual(stress.judge(0, MTP_OK), "green")
        self.assertEqual(stress.judge(1, VSTEST_RED), "red")
        self.assertEqual(stress.judge(1, VSTEST_OK), "red", "a non-zero exit is red even with a green line")
        self.assertEqual(stress.judge(0, VSTEST_NONE), "empty")
        self.assertEqual(stress.judge(0, "Total: 0, Failed: 0"), "empty")

    def test_all_green(self):
        rc, lines = self.run_with([(0, VSTEST_OK)] * 3)
        self.assertEqual(rc, 0)
        self.assertIn("3 green, 0 red, 0 ran no tests", lines[-1])

    def test_a_red_run_fails_but_the_rest_still_run(self):
        rc, lines = self.run_with([(0, VSTEST_OK), (1, VSTEST_RED), (0, VSTEST_OK)])
        self.assertEqual(rc, 1)
        self.assertIn("2 green, 1 red", lines[-1])

    def test_a_filter_that_matches_nothing_is_not_a_pass(self):
        rc, lines = self.run_with([(0, VSTEST_NONE)] * 2)
        self.assertEqual(rc, 2)
        self.assertIn("ran no tests", lines[-1])

    def test_bad_inputs_are_refused_without_running(self):
        called = []
        for suite, cls, n in (("shell", "A", 3), ("unit", "A;id", 3), ("unit", "A", 0), ("unit", "A", 51)):
            rc = stress.stress(suite, cls, n, runner=lambda c: called.append(c), out=lambda s: None)
            self.assertEqual(rc, 2, (suite, cls, n))
        self.assertEqual(called, [])

    def test_commands_use_the_filter_each_runner_reads(self):
        self.assertIn("--filter", stress.command("unit", "A.B"))
        self.assertIn("FullyQualifiedName~A.B", stress.command("integration", "A.B"))
        ui = stress.command("ui", "A.B")
        self.assertNotIn("--filter", ui)
        self.assertEqual(ui[-2:], ["--filter-class", "A.B"])
        self.assertTrue(all("--no-build" in stress.command(s, "A") for s in stress.PROJECTS))


class WorkflowWiringTests(unittest.TestCase):
    def test_stress_yml_matches_the_scripts_and_the_warden(self):
        text = open(os.path.join(ROOT, ".github/workflows/stress.yml")).read()
        self.assertIn("workflow_dispatch", text)
        for rid in ("osx-arm64", "win-x64"):
            self.assertIn(rid, text)
        # The inputs the warden dispatches are the inputs the workflow declares.
        for name in ("test_class", "n", "suite"):
            self.assertIn(f"      {name}:", text)
        for s in warden.SUITES:
            self.assertIn(s, text)
        self.assertEqual(set(warden.SUITES), set(stress.PROJECTS))
        self.assertIn("tools/stress-run.py", text)
        self.assertEqual(warden.MAX_N, stress.MAX_N)
        self.assertEqual(warden.CLASS_RE.pattern, stress.CLASS_RE.pattern)

    def test_ci_runs_this_suite(self):
        ci = open(os.path.join(ROOT, ".github/workflows/ci.yml")).read()
        self.assertIn("python tools/test-bugfix-host.py", ci)


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
