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
# The sweep reads the triage company id from beside pool-usage.json (CB-469);
# point that at a folder with no such file, so no test reads this machine's.
warden.POOL_USAGE = os.path.join(tempfile.mkdtemp(prefix="warden-test-"), "pool-usage.json")
pool_usage = load("pool_usage", "host/pool_usage.py")
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
        # CB-489: a paused org is refreshed too, on the same interval.
        self.assertTrue(warden.mint_due({}, 1000, True))
        self.assertTrue(warden.mint_due({"last_mint": 1000}, 1000 + 10 * 60, True))
        self.assertFalse(warden.mint_due({"last_mint": 1000}, 1000 + 9 * 60, True))


# ---------------------------------------------------------------- warden tick

UNREADABLE = object()


class FakeGH:
    def __init__(self, prs=None, merged=(), comments=None, checks=None, runs=None, push_runs=None,
                 open_prs=(), files=None, rulesets=(), branch_rules=()):
        self.prs, self.merged = prs or {}, list(merged)
        self.ruleset_rows, self.develop_rules = rulesets, branch_rules
        self.open_prs, self.files, self.files_read = list(open_prs), files or {}, []
        self.comments, self.checks = comments or {}, checks or {}
        self.dispatched = []
        self.runs, self.reran = runs or {}, []
        self.push_runs = push_runs or {}
        self.failures = {}

    def latest_ci_run(self, sha, event="pull_request"):
        return (self.push_runs if event == "push" else self.runs).get(sha)

    def latest_ci_runs(self, sha, workflows, event="pull_request"):
        got = self.runs.get(sha)
        return list(got) if isinstance(got, list) else ([got] if got else [])

    def open_bot_prs(self):
        return self.open_prs

    def rulesets(self):
        if self.ruleset_rows is UNREADABLE:
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        return list(self.ruleset_rows)

    def branch_rules(self, branch):
        """`branch_rules` is a dict {branch: rules}, or one list for both branches."""
        rules = self.develop_rules
        if isinstance(rules, dict):
            rules = rules.get(branch, [])
        if rules is UNREADABLE:
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        return list(rules)

    def pr_files(self, n):
        self.files_read.append(n)
        got = self.files.get(n, [])
        if got is UNREADABLE:
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        return got

    def rerun_failed(self, run_id):
        self.reran.append(run_id)
        for run in self.push_runs.values():
            if run and run.get("id") == run_id:
                run["run_attempt"] = run.get("run_attempt", 1) + 1

    def failed_tests(self, run_id, attempt):
        got = self.failures.get((run_id, attempt), [])
        if got is UNREADABLE:
            raise subprocess.CalledProcessError(1, ["gh", "run", "view"])
        return list(got)

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
            def latest_ci_runs(self, sha, workflows):
                raise subprocess.CalledProcessError(1, "gh")

        _, paused, logs = tick(gh=Boom(prs={7: head_pr()}), requests=[self.REQ])
        self.assertFalse(paused)
        self.assertTrue(any("unreadable" in l for l in logs))


DESKTOP_INSTALL = {"id": 9, "permissions": dict(warden.EVERYDAY_PERMISSIONS)}
WF_INSTALL = {"id": 9, "permissions": dict(warden.WORKFLOW_PERMISSIONS)}
MOBILE_REPO = warden.MOBILE["repo"]


class FakeMinter:
    """installs: repo -> installation object, None (404), or UNREADABLE."""

    def __init__(self, installs=None):
        self.n, self.minted = 0, []
        self.installs = {warden.REPO: DESKTOP_INSTALL} if installs is None else installs

    def installation(self, repo):
        got = self.installs.get(repo)
        if got is UNREADABLE:
            raise OSError("api down")
        return got

    def mint(self, installation_id, repositories, permissions):
        self.n += 1
        self.minted.append((installation_id, list(repositories), dict(permissions)))
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
        self.threads = {}  # issue id -> comment rows (CB-487)
        # CB-469: per-company worlds (absent: every company sees `all`) and
        # runs read one at a time.
        self.all_by, self.runs_by, self.run_full, self.run_reads = {}, {}, {}, []

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
        return self.all_by.get(company, self.all)

    def company_runs(self, company):
        return self.runs_by.get(company, self.run_rows)

    def get_run(self, run_id):
        self.run_reads.append(run_id)
        return self.run_full[run_id]

    def newest_comment_at(self, issue_id, agent_id):
        return self.said.get(issue_id)

    def issue_comments(self, issue_id):
        return list(self.threads.get(issue_id, []))

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

    def resume(self):
        if self.fail_writes:
            raise urllib.error.HTTPError("u", 500, "Server Error", {}, None)
        self.paused = False
        self.resumes = getattr(self, "resumes", 0) + 1

    def tokens_today(self):
        return self.tokens


def merged_pr(n=7, sha="abc"):
    return {"number": n, "merge_commit_sha": sha, "merged_at": "2026-11-24T10:00:00Z"}


def tick(gh=None, pc=None, requests=(), state=None, cfg=None, now=10_000.0, minter=None):
    logs = []
    w = warden.Warden(cfg or {}, gh or FakeGH(), minter or FakeMinter(), pc or FakePC(),
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

    def test_paused_company_is_still_refreshed(self):
        # CB-489: QAF-190 started against a token 13 hours dead because the
        # warden stopped minting for a pause that a person then lifted.
        pc = FakePC(paused=True)
        w, _, _ = tick(pc=pc)
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertEqual(w.state["last_mint"], 10_000.0)

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
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")], "the token is minted on the tick that pauses (CB-489)")

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


class DevelopRuleTests(unittest.TestCase):
    """CB-467: which tests a CI log says failed, and when a red develop is rerun."""
    # Lines as `gh run view --log-failed` printed them for develop's run 45499459145.
    LOG = ("unit-package (osx-arm64)\tUnit tests (xUnit)\t2026-11-25T03:52:11.9809370Z [xUnit.net 00:01:05.46]"
           "     Orbweaver.Tests.TurnSoundsTests.APendingSoundAndALiveOneAreNotPlayedInsideOneGap [FAIL]\n"
           "unit-package (osx-arm64)\tUnit tests (xUnit)\t2026-11-25T03:52:11.9913230Z   Failed "
           "Orbweaver.Tests.TurnSoundsTests.APendingSoundAndALiveOneAreNotPlayedInsideOneGap [3 s]\n"
           "build (macos-latest, osx-arm64)\tEvery job must have succeeded\t2026-11-25T03:57:15Z "
           "##[error]a CI job was not green (failure, cancelled and skipped all count as red)\n")

    def test_vstest_and_xunit_lines_name_one_test_once(self):
        self.assertEqual(warden.failed_tests_in(self.LOG),
                         ["Orbweaver.Tests.TurnSoundsTests.APendingSoundAndALiveOneAreNotPlayedInsideOneGap"])

    def test_the_testing_platform_line_is_read_too(self):
        log = "ui (osx-arm64)\tUI tests\tT   failed Orbweaver.UiTests.ChatPanelTests.SendsOnEnter (812ms)\n"
        self.assertEqual(warden.failed_tests_in(log), ["Orbweaver.UiTests.ChatPanelTests.SendsOnEnter"])

    def test_a_log_with_no_failed_test_names_none(self):
        for log in ("", None, "error CS1002: ; expected\n##[error]Process completed with exit code 1.\n",
                    "Failed!  - Failed: 1, Passed: 4, Skipped: 0, Total: 5\n"):
            self.assertEqual(warden.failed_tests_in(log), [], log)

    def test_the_first_rerun_is_unconditional(self):
        for attempts in ([["A"]], [[]]):
            self.assertIsNone(warden.develop_rerun_problem(attempts, 0), attempts)

    def test_a_different_test_each_attempt_is_rerun_until_the_cap(self):
        self.assertIsNone(warden.develop_rerun_problem([["A"], ["B"]], 1))
        self.assertIn("after 2 reruns", warden.develop_rerun_problem([["A"], ["B"], ["C"]], 2))

    def test_a_test_failing_twice_is_real(self):
        self.assertEqual(warden.develop_rerun_problem([["A", "B"], ["B"]], 1), "failed twice: B")
        self.assertEqual(warden.develop_rerun_problem([["A"], ["B"], ["A"]], 1), "failed twice: A")

    def test_an_attempt_that_names_no_test_is_real(self):
        for attempts in ([[], ["B"]], [["A"], []]):
            self.assertIn("other than a test", warden.develop_rerun_problem(attempts, 1), attempts)


class DevelopRerunTests(unittest.TestCase):
    """CB-467: a red develop on a bot merge is rerun while the failures look like flakes."""
    RED = {warden.REQUIRED_CHECKS[0]: "failure", warden.REQUIRED_CHECKS[1]: "success"}
    GREEN = {n: "success" for n in warden.REQUIRED_CHECKS}

    def gh(self, *failures):
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": dict(self.RED)}, push_runs={"abc": ci_run("abc", run_id=55)})
        gh.failures = {(55, i + 1): f for i, f in enumerate(failures)}
        return gh

    def test_a_different_test_in_each_attempt_is_rerun_twice_then_pauses(self):
        gh, state = self.gh(["A"], ["B"], ["C"]), {}
        _, paused, logs = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertEqual(gh.reran, [55])
        self.assertIn("abc", state["audit_pending"], "still pending until the rerun concludes")
        self.assertTrue(any("(A): reran failed jobs of run 55, rerun 1 of 2" in l for l in logs), logs)
        _, paused, logs = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused, "B is not A: the runner, not the merge")
        self.assertEqual(gh.reran, [55, 55])
        self.assertTrue(any("rerun 2 of 2" in l for l in logs), logs)
        pc = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("(still red after 2 reruns)", pc.pauses[0])
        self.assertEqual(gh.reran, [55, 55])
        self.assertEqual(state["develop_failures"], {}, "retired merges leave no cache behind")

    def test_the_same_test_failing_again_pauses_and_names_it(self):
        gh, state = self.gh(["A"], ["A", "B"]), {}
        tick(gh=gh, pc=FakePC(), state=state)
        pc = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("CI red", pc.pauses[0])
        self.assertIn("(failed twice: A)", pc.pauses[0])
        self.assertEqual(gh.reran, [55])

    def test_a_build_failure_is_rerun_once_then_pauses(self):
        gh, state = self.gh([], []), {}
        _, paused, logs = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertTrue(any("(no test named)" in l for l in logs), logs)
        pc = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("other than a test", pc.pauses[0])

    def test_unreadable_logs_rerun_the_first_time_and_pause_after(self):
        gh, state = self.gh(UNREADABLE, UNREADABLE), {}
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertEqual(gh.reran, [55])
        pc = FakePC()
        _, paused, _ = tick(gh=gh, pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("(could not read which tests failed)", pc.pauses[0])

    def test_red_then_green_after_the_rerun_retires_the_merge_without_a_pause(self):
        gh, state = self.gh(["A"]), {}
        tick(gh=gh, pc=FakePC(), state=state)
        gh.checks["abc"] = dict(self.GREEN)
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertEqual(state["audit_pending"], {})
        self.assertIn("abc", state["audited"])
        self.assertEqual(state["develop_failures"], {})

    def test_a_run_still_going_keeps_the_merge_pending(self):
        gh, state = self.gh(), {}
        gh.push_runs = {"abc": ci_run("abc", status="in_progress", conclusion=None)}
        _, paused, _ = tick(gh=gh, pc=FakePC(), state=state)
        self.assertFalse(paused)
        self.assertIn("abc", state["audit_pending"])
        self.assertEqual(gh.reran, [])

    def test_no_red_run_to_rerun_pauses_at_once(self):
        for run in (None, ci_run("abc", conclusion="success")):
            gh = self.gh()
            gh.push_runs = {"abc": run} if run else {}
            pc = FakePC()
            _, paused, _ = tick(gh=gh, pc=pc)
            self.assertTrue(paused, run)
            self.assertEqual(gh.reran, [])

    def test_a_refused_rerun_pauses(self):
        class Refusing(FakeGH):
            def rerun_failed(self, run_id):
                raise subprocess.CalledProcessError(1, ["gh"])
        gh = self.gh(["A"])
        gh.__class__ = Refusing
        _, paused, _ = tick(gh=gh, pc=FakePC())
        self.assertTrue(paused)


class StopWorkTests(unittest.TestCase):
    def test_pause_cancels_active_runs_and_blocks_in_progress_issues(self):
        pc = FakePC(active=["run-aaaaaaaa", "run-bbbbbbbb"], issues=["iss-11111111"])
        tick(pc=pc, requests=[{"verb": "push_refused"}])
        self.assertEqual(pc.cancelled, ["run-aaaaaaaa", "run-bbbbbbbb"])
        self.assertEqual(pc.blocked, ["iss-11111111"])

    def test_a_refused_block_is_logged_and_the_tick_survives_with_the_reason_first(self):
        # CB-467: Paperclip answered 422 to a PATCH to blocked, the exception
        # escaped, and the warden died before logging why it had paused.
        class Refusing(FakePC):
            def block_issue(self, i):
                if i == "iss-bad00000":
                    raise urllib.error.HTTPError("u", 422, "Unprocessable Entity", {}, None)
                super().block_issue(i)

        pc = Refusing(active=["run-aaaaaaaa"], issues=["iss-bad00000", "iss-good0000"])
        _, paused, logs = tick(pc=pc, requests=[{"verb": "push_refused"}])
        self.assertTrue(paused)
        self.assertEqual(pc.cancelled, ["run-aaaaaaaa"])
        self.assertEqual(pc.blocked, ["iss-good0000"], "the next issue is still blocked")
        refused = [i for i, l in enumerate(logs) if "could not block issue iss-bad0" in l]
        reason = [i for i, l in enumerate(logs) if l.startswith("PAUSED:")]
        self.assertTrue(refused and reason)
        self.assertLess(reason[0], refused[0], "the reason is logged before anything that can fail")

    def test_still_paused_next_tick_keeps_cancelling_stragglers(self):
        pc = FakePC(paused=True, active=["run-cccccccc"])
        tick(pc=pc)
        self.assertEqual(pc.cancelled, ["run-cccccccc"])
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")], "minting goes on through a pause (CB-489)")

    def test_unpaused_company_keeps_its_runs(self):
        pc = FakePC(active=["run-dddddddd"], issues=["iss-22222222"])
        _, paused, _ = tick(pc=pc)
        self.assertFalse(paused)
        self.assertEqual((pc.cancelled, pc.blocked), ([], []))

    def test_warden_resumes_only_through_the_release_brake_cool_off(self):
        # CB-484 gave the warden one way to resume; the PATCH to active must
        # stay in that one place.
        self.assertFalse(hasattr(warden.Warden, "resume"))
        src = open(os.path.join(ROOT, "host/warden.py")).read()
        self.assertEqual(src.count('"status": "active"'), 1)
        self.assertEqual(src.count("self.pc.resume()"), 1)

    def test_a_refused_block_is_logged_once_per_issue_per_pause(self):
        # CB-484: one issue's 422 was logged on every tick of a 13-hour pause.
        class Refusing(FakePC):
            def block_issue(self, i):
                if i == "iss-bad00000":
                    raise urllib.error.HTTPError("u", 422, "Unprocessable Entity", {}, None)
                super().block_issue(i)

        state = {}
        pc = Refusing(issues=["iss-bad00000"])
        lines = []
        for t in range(3):
            pc.paused = t > 0
            lines += tick(pc=pc, state=state, now=1000 + t * 300,
                          requests=[{"verb": "push_refused"}] if t == 0 else [])[2]
        self.assertEqual(sum("could not block issue iss-bad0" in l for l in lines), 1)
        # Control: a new pause logs it again.
        pc.paused = False
        tick(pc=pc, state=state, now=5000)
        _, _, logs = tick(pc=pc, state=state, now=5300, requests=[{"verb": "push_refused"}])
        self.assertEqual(sum("could not block issue iss-bad0" in l for l in logs), 1)

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
        pc.cancelled_issues = [R("old"), R("x", 101)]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertFalse(paused, "one release is not a pause")
        pc.cancelled_issues = [R("old"), R("x", 101), R("y", 102)]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn("Paperclip issue states", pc.pauses[0])

    def test_a_merge_between_releases_resets_the_paperclip_streak(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("x", 101)]
        tick(pc=pc, state=state)
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64"]},
                    checks={"abc": {n: "success" for n in warden.REQUIRED_CHECKS}})
        tick(gh=gh, pc=pc, state=state)
        pc.cancelled_issues = [R("x", 101), R("y", 102)]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertFalse(paused)

    def test_suppressing_the_spool_does_not_hide_releases(self):
        # No spool requests at all in either tick; Paperclip alone pauses.
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("x", 101), R("y", 102)]
        self.assertTrue(tick(pc=pc, state=state)[1])

    def test_spool_release_and_paperclip_release_are_not_added_together(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("x", 101)]
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

    def test_one_ticket_cancelled_under_two_passes_is_one_release(self):
        # CB-484, the 2026-11-26 shape: CB-346's engineer tasks QAF-179 (07:30
        # pass) and QAF-185 (08:30 pass) were both cancelled on one release.
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("q179", 346, parentId="pass0730"), R("q185", 346, parentId="pass0830")]
        _, paused, logs = tick(pc=pc, state=state)
        self.assertFalse(paused, "one ticket released once is one release, whatever pass made its tasks")
        self.assertEqual(state["paperclip_releases"], 1)
        self.assertEqual(state["paperclip_released_keys"], ["CB-346"])
        self.assertTrue(any("CB-346 already counted" in l for l in logs))
        # Control: a second, different ticket still trips the brake.
        pc.cancelled_issues.append(R("q190", 347, parentId="pass0900"))
        _, paused, _ = tick(pc=pc, state=state)
        self.assertTrue(paused)
        self.assertIn(warden.PAPERCLIP_BRAKE, pc.pauses[0])

    def test_the_same_ticket_seen_on_a_later_tick_is_still_one_release(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415, parentId="pass1")]
        self.assertFalse(tick(pc=pc, state=state)[1])
        pc.cancelled_issues.append(R("r3", 415, parentId="pass2"))
        self.assertFalse(tick(pc=pc, state=state)[1])
        self.assertEqual(state["paperclip_releases"], 1)

    def test_old_state_with_release_passes_is_tolerated(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        state["paperclip_release_passes"] = [["CB-415", "pass1"]]
        state["paperclip_releases"] = 1
        pc.cancelled_issues = [R("r1", 415, parentId="pass1")]
        _, paused, _ = tick(pc=pc, state=state)
        self.assertNotIn("paperclip_release_passes", state)
        self.assertEqual(state["paperclip_released_keys"], ["CB-415"])
        self.assertTrue(paused, "the old counter still holds the earlier release; erring towards the brake")

    def test_a_merge_clears_the_released_ticket_set(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415)]
        tick(pc=pc, state=state)
        self.assertEqual(state["paperclip_released_keys"], ["CB-415"])
        gh = FakeGH(merged=[merged_pr()], comments={7: ["osx-arm64 win-x64 ok"]},
                    checks={"abc": {n: "success" for n in warden.REQUIRED_CHECKS}})
        tick(gh=gh, pc=pc, state=state)
        self.assertEqual(state["paperclip_released_keys"], [])
        self.assertEqual(state["paperclip_releases"], 0)
        # After the merge the same ticket released again counts afresh, once.
        pc.cancelled_issues.append(R("r2", 415))
        _, paused, logs = tick(pc=pc, state=state)
        self.assertFalse(paused)
        self.assertEqual(state["paperclip_releases"], 1)
        self.assertTrue(any("CB-415 released" in l for l in logs))

    def test_two_tickets_released_in_one_pass_are_two_releases(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415, parentId="pass1"), R("r2", 416, parentId="pass1")]
        self.assertTrue(tick(pc=pc, state=state)[1])

    def test_issues_with_no_parent_count_by_ticket(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state)
        pc.cancelled_issues = [R("r1", 415), R("r2", 415)]
        self.assertFalse(tick(pc=pc, state=state)[1], "the ticket key, not the parent, is what is counted")
        pc.cancelled_issues.append(R("r3", 416))
        self.assertTrue(tick(pc=pc, state=state)[1])

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


class BrakeCoolOffTests(unittest.TestCase):
    """CB-484: a pause whose only reason is the release brake lifts itself
    after an hour; anything else stays until a person lifts it."""

    def trip(self, state, pc, now, a=346, b=347):
        pc.cancelled_issues += [R(f"i{a}", a), R(f"i{b}", b)]
        self.assertTrue(tick(pc=pc, state=state, now=now)[1])

    def fresh(self):
        state, pc = {}, FakePC()
        tick(pc=pc, state=state, now=0)
        return state, pc

    def test_release_only_pause_resumes_after_sixty_minutes(self):
        state, pc = self.fresh()
        self.trip(state, pc, 10_000)
        self.assertEqual(state["warden_pause"]["reasons"], [warden.PAPERCLIP_BRAKE])
        _, paused, logs = tick(pc=pc, state=state, now=10_000 + warden.BRAKE_COOL_OFF - 1)
        self.assertTrue(paused, "not before the hour")
        self.assertFalse(any("RESUMED" in l for l in logs))
        _, paused, logs = tick(pc=pc, state=state, now=10_000 + warden.BRAKE_COOL_OFF)
        self.assertFalse(paused)
        self.assertFalse(pc.paused)
        self.assertIn("RESUMED: release brake cooled off", logs)
        self.assertEqual((state["paperclip_releases"], state["consecutive_releases"],
                          state["paperclip_released_keys"]), (0, 0, []))
        self.assertNotIn("warden_pause", state)
        self.assertEqual(state["pauses"][-1]["resumed_at"], 10_000 + warden.BRAKE_COOL_OFF,
                         "the pause window closes on the resuming tick")

    def test_spool_brake_also_cools_off(self):
        state, pc = self.fresh()
        rel = {"verb": "released", "ticket": "CB-1"}
        tick(pc=pc, state=state, now=100, requests=[rel])
        self.assertTrue(tick(pc=pc, state=state, now=200, requests=[rel])[1])
        _, paused, logs = tick(pc=pc, state=state, now=200 + warden.BRAKE_COOL_OFF)
        self.assertFalse(paused)
        self.assertIn("RESUMED: release brake cooled off", logs)

    def test_no_auto_resume_when_another_reason_is_present(self):
        state, pc = self.fresh()
        pc.cancelled_issues += [R("a", 1), R("b", 2)]
        tick(pc=pc, state=state, now=100, requests=[{"verb": "push_refused"}])
        self.assertTrue(pc.paused)
        _, paused, logs = tick(pc=pc, state=state, now=100 + 3 * warden.BRAKE_COOL_OFF)
        self.assertTrue(paused)
        self.assertFalse(any("RESUMED" in l for l in logs))

    def test_a_reason_added_while_paused_keeps_it_manual(self):
        state, pc = self.fresh()
        self.trip(state, pc, 100)
        tick(pc=pc, state=state, now=400, requests=[{"verb": "push_refused"}])
        self.assertEqual(len(state["warden_pause"]["reasons"]), 2)
        tick(pc=pc, state=state, now=700, requests=[{"verb": "push_refused"}])
        self.assertEqual(len(state["warden_pause"]["reasons"]), 2, "a repeated reason is not added twice")
        self.assertTrue(tick(pc=pc, state=state, now=100 + 2 * warden.BRAKE_COOL_OFF)[1])

    def test_a_pause_the_warden_did_not_make_never_auto_resumes(self):
        state, pc = {}, FakePC(paused=True)
        tick(pc=pc, state=state, now=0)
        self.assertTrue(tick(pc=pc, state=state, now=10 * warden.BRAKE_COOL_OFF)[1])
        self.assertNotIn("warden_pause", state)

    def test_the_brake_cools_off_every_time(self):
        # Lead's ruling: no limit on repeats; a release brake is only ever a
        # one-hour breather.
        state, pc = self.fresh()
        self.trip(state, pc, 100)
        resumed = 100 + warden.BRAKE_COOL_OFF
        self.assertFalse(tick(pc=pc, state=state, now=resumed)[1])
        again = resumed + warden.TICKET_WINDOW + 60
        pc.cancelled_issues += [R("c", 10), R("d", 11)]
        self.assertTrue(tick(pc=pc, state=state, now=again)[1], "the brake still trips after a resume")
        _, paused, logs = tick(pc=pc, state=state, now=again + warden.BRAKE_COOL_OFF)
        self.assertFalse(paused)
        self.assertIn("RESUMED: release brake cooled off", logs)

    def test_run_cap_seen_while_paused_keeps_it_manual(self):
        state, pc = self.fresh()
        self.trip(state, pc, 100)
        pc.runs = 11
        tick(pc=pc, state=state, now=400, cfg={"max_runs_per_day": 10})
        pc.runs = 0
        self.assertTrue(tick(pc=pc, state=state, now=100 + 2 * warden.BRAKE_COOL_OFF)[1])

    def test_the_live_state_shape_never_auto_resumes(self):
        # ~/.config/orbweaver-bugfix/state.json on 2026-11-26: an open window,
        # the old pass list, and no record of why.
        state = {"pauses": [{"paused_at": 0, "resumed_at": None}], "cancelled_seen": [],
                 "paperclip_release_passes": [["CB-346", "p1"], ["CB-346", "p2"]], "paperclip_releases": 2}
        pc = FakePC(paused=True)
        _, paused, logs = tick(pc=pc, state=state, now=13 * 3600)
        self.assertTrue(paused)
        self.assertFalse(any("RESUMED" in l for l in logs))

    def test_a_person_resuming_first_clears_the_record(self):
        state, pc = self.fresh()
        self.trip(state, pc, 100)
        pc.paused = False
        tick(pc=pc, state=state, now=400)
        self.assertNotIn("warden_pause", state)

    def test_a_failed_resume_stays_paused_and_says_so(self):
        state, pc = self.fresh()
        self.trip(state, pc, 100)
        pc.fail_writes = True
        _, paused, logs = tick(pc=pc, state=state, now=100 + warden.BRAKE_COOL_OFF)
        self.assertTrue(paused)
        self.assertTrue(any("could not resume after the release brake: HTTPError 500" in l for l in logs))
        self.assertIn("warden_pause", state, "kept, so the next tick tries again")

    def stopped(self, pc, statuses):
        pc.full = {i: {"id": i, "status": st} for i, st in statuses.items()}

    def test_any_resume_restarts_what_stop_work_stopped(self):
        class Refusing(FakePC):
            def block_issue(self, i):
                if i == "iss-bad00000":
                    raise urllib.error.HTTPError("u", 422, "Unprocessable Entity", {}, None)
                super().block_issue(i)

        state, pc = {}, Refusing(issues=["iss-aaaaaaaa", "iss-bbbbbbbb", "iss-bad00000"])
        tick(pc=pc, state=state, now=0, requests=[{"verb": "push_refused"}])
        self.assertEqual(state["stop_blocked"], ["iss-aaaaaaaa", "iss-bbbbbbbb"])
        tick(pc=pc, state=state, now=300)
        self.assertEqual(state["stop_blocked"], ["iss-aaaaaaaa", "iss-bbbbbbbb"], "listed once")
        self.assertEqual(pc.statuses, [])
        # A person resumes (push_refused is manual). One task was finished by hand meanwhile.
        pc.paused, pc.issues = False, []
        self.stopped(pc, {"iss-aaaaaaaa": "blocked", "iss-bbbbbbbb": "done", "iss-bad00000": "in_progress"})
        _, _, logs = tick(pc=pc, state=state, now=600)
        self.assertEqual(pc.statuses, [("iss-aaaaaaaa", "todo")])
        self.assertEqual(pc.comments, [("iss-aaaaaaaa", warden.RESTART_COMMENT),
                                       ("iss-bad00000", warden.RESTART_COMMENT)])
        self.assertTrue(any("restarted issue iss-bad0" in l and "comment only" in l for l in logs))
        self.assertNotIn("stop_blocked", state)
        tick(pc=pc, state=state, now=900)
        self.assertEqual(len(pc.comments), 2, "restarted once, not every tick")

    def test_auto_resume_restarts_on_the_same_tick(self):
        state, pc = self.fresh()
        pc.issues = ["iss-cccccccc"]
        self.trip(state, pc, 100)
        pc.issues = []
        self.stopped(pc, {"iss-cccccccc": "blocked"})
        _, paused, logs = tick(pc=pc, state=state, now=100 + warden.BRAKE_COOL_OFF)
        self.assertFalse(paused)
        self.assertEqual(pc.statuses, [("iss-cccccccc", "todo")])
        self.assertLess(logs.index("RESUMED: release brake cooled off"),
                        next(i for i, l in enumerate(logs) if "restarted issue iss-cccc" in l))

    def test_a_failed_restart_is_retried_and_does_not_end_the_tick(self):
        state, pc = {}, FakePC(issues=["iss-dddddddd", "iss-eeeeeeee"])
        tick(pc=pc, state=state, now=0, requests=[{"verb": "push_refused"}])
        pc.paused, pc.issues = False, []
        # dddd cannot be read (get_issue raises), eeee restarts.
        pc.full = {"iss-eeeeeeee": {"status": "blocked"}}
        _, paused, logs = tick(pc=pc, state=state, now=300)
        self.assertFalse(paused)
        self.assertEqual(pc.statuses, [("iss-eeeeeeee", "todo")])
        self.assertTrue(any("could not restart issue iss-dddd: OSError" in l for l in logs))
        self.assertEqual(state["restart_retry"], [["iss-dddddddd", "blocked"]])
        self.assertTrue(pc.secrets, "the tick went on to mint")
        pc.full["iss-dddddddd"] = {"status": "blocked"}
        tick(pc=pc, state=state, now=600)
        self.assertEqual(pc.statuses[-1], ("iss-dddddddd", "todo"))
        self.assertNotIn("restart_retry", state)

    def test_a_failed_comment_after_the_todo_patch_is_posted_next_tick(self):
        # QA round 2: the PATCH went through, the comment did not; the retry
        # must still post it, since the comment is what wakes the assignee.
        class CommentFailsOnce(FakePC):
            def comment(self, issue_id, body):
                if not getattr(self, "failed", False):
                    self.failed = True
                    raise urllib.error.HTTPError("u", 500, "Server Error", {}, None)
                super().comment(issue_id, body)

        state, pc = {}, CommentFailsOnce(issues=["iss-ffffffff"])
        tick(pc=pc, state=state, now=0, requests=[{"verb": "push_refused"}])
        pc.paused, pc.issues = False, []
        pc.full = {"iss-ffffffff": {"status": "blocked"}}
        _, _, logs = tick(pc=pc, state=state, now=300)
        self.assertEqual((pc.statuses, pc.comments), ([("iss-ffffffff", "todo")], []))
        self.assertEqual(state["restart_retry"], [["iss-ffffffff", "comment"]])
        pc.full["iss-ffffffff"]["status"] = "todo"
        tick(pc=pc, state=state, now=600)
        self.assertEqual(pc.statuses, [("iss-ffffffff", "todo")], "not PATCHed twice")
        self.assertEqual(pc.comments, [("iss-ffffffff", warden.RESTART_COMMENT)])
        self.assertNotIn("restart_retry", state)
        # Control: an owed comment on an issue that has since moved on is dropped.
        state["restart_retry"] = [["iss-ffffffff", "comment"]]
        pc.full["iss-ffffffff"]["status"] = "in_progress"
        tick(pc=pc, state=state, now=900)
        self.assertEqual(len(pc.comments), 1)
        self.assertNotIn("restart_retry", state)

    def test_paperclip_http_resume_patches_active(self):
        calls = []
        pc = warden.PaperclipHttp.__new__(warden.PaperclipHttp)
        pc.company = "co1"
        pc.call = lambda *a: calls.append(a)
        pc.resume()
        self.assertEqual(calls, [("PATCH", "/api/companies/co1", {"status": "active"})])


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
        pc.cancelled_issues = [R("x", 101), R("y", 102)]
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
        pc.cancelled_issues = [R("x", 101), R("y", 102)]
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


def EXHAUSTED(issue, at=NOW - 10 * 60, **over):
    return RUN(issue, "cancelled", at=at, errorCode="ai_connection_pool_exhausted",
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

    def test_a_child_that_worked_after_its_wakes_starts_the_count_again(self):
        # QA, CB-479: woken twice yesterday, worked, idle 21 minutes today.
        st = {"child_wakes": {"c": [2, NOW - 86400]}}
        self.assertEqual(decide(self.kids(), state=st), [("wake_child", "c")])
        acts = warden.unstick_actions(self.kids(), [], {}, st, NOW, True)
        self.assertEqual(acts[0]["wakes"], 1)

    def test_our_own_wake_comment_does_not_reset_the_count(self):
        # The comment bumps updatedAt to the wake time; inside the margin.
        kids = [I("P", "in_progress", idle=60),
                I("c", "in_progress", "P", idle=21 * 60 - 30, assignee="eng")]
        st = {"child_wakes": {"c": [2, NOW - 21 * 60]}}
        self.assertEqual(decide(kids, state=st), [("cancel_child", "c")])

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

    def test_a_stale_usage_cancel_lets_a_settled_blocked_parent_be_closed(self):
        # QA, CB-479: CB-469's run history must not shadow CB-424's release.
        issues = [I("P", "blocked", idle=3600), I("e", "cancelled", "P", idle=3600, assignee="eng")]
        runs = [EXHAUSTED("P", at=NOW - 7200)]
        self.assertEqual(decide(issues, runs), [("wake_parent", "P")])
        self.assertEqual(decide(issues, runs, said={"P": NOW - 600}), [("close_parent", "P")])

    def test_a_fresh_usage_cancel_with_the_pool_full_does_nothing(self):
        issues = [I("P", "blocked", idle=3600), I("e", "cancelled", "P", idle=3600, assignee="eng"),
                  I("c", "in_progress", "P2", idle=3600)]
        runs = [EXHAUSTED("P", at=NOW - 59 * 60), EXHAUSTED("c", at=NOW - 30 * 60)]
        self.assertEqual(decide(issues, runs, said={"P": NOW - 600}, pool_ok=False), [])

    def test_a_usage_cancel_with_no_time_does_not_count_as_fresh(self):
        self.assertEqual(decide([I("c", "todo", "P")], [EXHAUSTED("c", createdAt=None)]),
                         [("wake_child", "c")])

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


# CB-469: a task routed through the pool is pinned to its account and a
# session reset does not move it, so rule 3 closes it for a new issue instead.
CONN_FULL, CONN_FREE = "conn-full", "conn-free"


def POOL_WITH(free_in, now=NOW):
    """pool-usage.json with CONN_FULL at its weekly limit, resetting in
    `free_in` seconds (None: no reset time), and CONN_FREE with room."""
    full = {"usedPercent": 100, "limitReached": True}
    if free_in is not None:
        full["resetsAt"] = warden.iso(now + free_in)
    checked = warden.iso(now - 4 * 60)
    return {"accounts": {CONN_FULL: {"status": "ok", "checkedAt": checked, "limits": [{"usedPercent": 40}, full]},
                         CONN_FREE: {"status": "ok", "checkedAt": checked, "limits": [{"usedPercent": 10}]}}}


def PIN(free_in=6 * 3600, room=True):
    return {"connection": CONN_FULL, "free_at": None if free_in is None else NOW + free_in, "room": room}


def LIMIT_FAILED(issue, at=NOW - 10 * 60, **over):
    return RUN(issue, "failed", at=at, errorCode="native_provider_usage_limit",
               error="ACP agent reported a terminal limit failure.", **over)


def ROUTED(issue, conn=CONN_FULL, at=NOW - 3 * 3600, status="succeeded"):
    r = RUN(issue, status, at=at)
    r["id"] = f"run-{issue}-routed"
    r["contextSnapshot"] = {"taskKey": issue, "aiRouterSelection": {"binding": {"connectionId": conn}}}
    return r


def PASS(i, status="blocked", routine="rt-fix", **over):
    return I(i, status, None, originKind="routine_execution", originId=routine, title="Fix open CB bugs", **over)


def kinds(acts):
    return [a["kind"] for a in acts]


def decide_pins(issues, runs, pins, state=None, pool_ok=True, root_only=False, now=NOW):
    return warden.unstick_actions(list(issues), list(runs), {}, state or {}, now, pool_ok, {}, pins, root_only)


class PinnedUsageTests(unittest.TestCase):
    def test_a_pinned_child_is_closed_for_the_lead_not_reset(self):
        child = I("c", "in_progress", "P", title="stop flaky orb test")
        acts = decide_pins([PASS("P"), child], [EXHAUSTED("c")], {"c": PIN()})
        self.assertEqual([(a["kind"], a["id"], a["parent_id"]) for a in acts], [("close_pinned", "c", "P")])
        self.assertEqual(acts[0]["key"], "child:P:stop flaky orb test")
        self.assertEqual(acts[0]["until"], warden.iso(NOW + 6 * 3600))
        # Control: the same task with no routed run is the unpinned case and
        # keeps the CB-479 reset, which does free it (QAF-50).
        self.assertEqual(decide([PASS("P"), child], [EXHAUSTED("c")]), [("reset_session", "c")])

    def test_a_pinned_routine_fire_is_closed_so_the_next_fire_is_not_coalesced(self):
        acts = decide_pins([PASS("P", "todo")], [EXHAUSTED("P")], {"P": PIN()})
        self.assertEqual([(a["kind"], a["parent_id"], a["key"]) for a in acts],
                         [("close_pinned", None, "routine:rt-fix")])
        # Control: a root task no routine made is left alone.
        manual = I("M", "todo", None, originKind="manual", originId="x", title="one-off")
        self.assertEqual(decide_pins([manual], [EXHAUSTED("M")], {"M": PIN()}), [])

    def test_a_short_wait_is_left_to_paperclips_own_retry(self):
        child = I("c", "todo", "P", title="t")
        self.assertEqual(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=30 * 60)}), [])
        self.assertEqual([a["kind"] for a in decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=31 * 60)})],
                         ["close_pinned"])
        # No reset time known: the wait cannot be shown short, so it acts.
        self.assertEqual([a["kind"] for a in decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=None)})],
                         ["close_pinned"])

    def test_nothing_while_no_account_has_room(self):
        child = I("c", "todo", "P", title="t")
        self.assertEqual(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(room=False)}), [])
        self.assertEqual(decide_pins([PASS("P", "todo")], [EXHAUSTED("P")], {"P": PIN(room=False)}), [])

    def test_unknown_reset_closes_but_no_fresh_room_does_nothing(self):
        # The lead's default (CB-469): a stale reading for the pinned account is
        # an unknown reset (pins_for gives free_at None), and an unknown reset
        # with fresh room elsewhere closes; with no fresh room, nothing.
        child = I("c", "todo", "P", title="t")
        self.assertEqual(kinds(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=None)})), ["close_pinned"])
        self.assertEqual(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=None, room=False)}), [])
        pool = POOL_WITH(6 * 3600)
        self.assertTrue(warden.reading_fresh(pool, CONN_FULL, NOW))
        self.assertTrue(warden.fresh_headroom(pool, NOW))
        old = NOW + warden.READING_FRESH + 5 * 60
        self.assertFalse(warden.reading_fresh(pool, CONN_FULL, old), "checked 34 minutes before")
        self.assertFalse(warden.fresh_headroom(pool, old), "room on a stale reading is no room")
        self.assertFalse(warden.reading_fresh(pool, "unknown", NOW))
        self.assertFalse(warden.reading_fresh({"accounts": {CONN_FULL: {"status": "ok"}}}, CONN_FULL, NOW),
                         "no checkedAt")
        self.assertFalse(warden.reading_fresh(pool, CONN_FULL, NOW - 3600), "checked in the future")

    def test_a_reset_time_already_past_is_unknown_never_soon(self):
        # QA must-fix 1: a past resetsAt used to read as "frees within 30
        # minutes" and waited for ever.
        child = I("c", "todo", "P", title="t")
        for past in (-600, -3 * 3600):
            act = decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=past)})
            self.assertEqual(kinds(act), ["close_pinned"], past)
            self.assertIsNone(act[0]["until"], "a passed reset is not reported as a time")
        # Control: a reset 10 minutes ahead on a router cancel is still waited on.
        self.assertEqual(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=600)}), [])

    def test_the_short_wait_is_only_for_the_routers_cancel(self):
        # QA must-fix 2: Paperclip retries a router cancel at the reset, but
        # schedules nothing for a run that failed part-way on the limit.
        child = I("c", "todo", "P", title="t")
        self.assertEqual(kinds(decide_pins([child], [LIMIT_FAILED("c")], {"c": PIN(free_in=600)})),
                         ["close_pinned"])
        self.assertEqual(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=600)}), [])
        # Once that cancel's reset has passed with the task still stopped, the
        # retry did not free it, and it closes.
        self.assertEqual(kinds(decide_pins([child], [EXHAUSTED("c")], {"c": PIN(free_in=-60)})), ["close_pinned"])

    def test_the_short_wait_and_a_passed_reset_in_the_triage_company(self):
        # QA: in a root_only company nothing falls through afterwards, so a
        # wait there would be for ever (ORB-164 on 2026-11-25).
        fire = PASS("T", "todo", routine="rt-tri")
        self.assertEqual(kinds(decide_pins([fire], [LIMIT_FAILED("T")], {"T": PIN(free_in=600)}, root_only=True)),
                         ["close_pinned"])
        self.assertEqual(kinds(decide_pins([fire], [EXHAUSTED("T")], {"T": PIN(free_in=-600)}, root_only=True)),
                         ["close_pinned"])
        # Control: a router cancel freeing in 10 minutes is waited on there too.
        self.assertEqual(decide_pins([fire], [EXHAUSTED("T")], {"T": PIN(free_in=600)}, root_only=True), [])

    def test_a_routine_fire_close_settles_its_idle_ticket_children(self):
        # The lead's ruling: open `Ticket:` children with no live run go done
        # with the pass; a running one and a helper without the line are left.
        kids = [I("e", "todo", "P", title="fix", description="Ticket: CB-1\nplan"),
                I("q", "in_progress", "P", title="qa", description="Ticket: CB-1\nreview", activeRun={"id": "r"}),
                I("h", "todo", "P", title="helper", description="look something up"),
                I("d", "done", "P", title="old", description="Ticket: CB-2\nx")]
        acts = decide_pins([PASS("P", "todo")] + kids, [EXHAUSTED("P")], {"P": PIN()})
        self.assertEqual([(a["id"], a["children"]) for a in acts if a["kind"] == "close_pinned"], [("P", ["e"])])
        # Control: in a root_only company the fire is closed and nothing else.
        acts = decide_pins([PASS("P", "todo")] + kids, [EXHAUSTED("P")], {"P": PIN()}, root_only=True)
        self.assertEqual([(a["id"], a.get("children")) for a in acts], [("P", None)])
        # Control: a child's close carries no children list.
        child = I("c", "todo", "P", title="t")
        self.assertNotIn("children", decide_pins([PASS("P"), child], [EXHAUSTED("c")], {"c": PIN()})[0])

    def test_a_pinned_stop_older_than_the_hour_still_closes_if_nothing_happened_since(self):
        # QA: the warden down for hours must not strand the task it exists to free.
        child = I("c", "todo", "P", title="t", idle=6 * 3600)
        self.assertEqual(kinds(decide_pins([child], [EXHAUSTED("c", at=NOW - 5 * 3600)], {"c": PIN()})),
                         ["close_pinned"])
        # Control: something happened on the task after the stop, so it is not
        # this rule's any more (and the unpinned reset keeps its hour).
        busy = I("c", "todo", "P", title="t", idle=3600)
        self.assertNotIn("close_pinned",
                         kinds(decide_pins([busy], [EXHAUSTED("c", at=NOW - 5 * 3600)], {"c": PIN()})))
        self.assertEqual(decide([I("c", "todo", "P", idle=6 * 3600)], [EXHAUSTED("c", at=NOW - 5 * 3600)]),
                         [("wake_child", "c")], "unpinned: no reset after the hour")

    def test_a_pinned_task_left_alone_still_gets_the_other_rules_once_its_stop_is_old(self):
        # A repeat (no close) on an old stop falls through to the stall rules.
        child = I("c2", "todo", "P", title="t", idle=6 * 3600)
        st = {"repinned": {"child:P:t": NOW - 3600}, "repin_logged": {"child:P:t": NOW - 60}}
        self.assertEqual(kinds(decide_pins([child], [EXHAUSTED("c2", at=NOW - 5 * 3600)], {"c2": PIN()},
                                           state=st)), ["wake_child"])
        # Control: on a fresh stop it waits on usage instead.
        self.assertEqual(decide_pins([I("c2", "todo", "P", title="t")], [EXHAUSTED("c2")], {"c2": PIN()},
                                     state=st), [])

    def test_the_parent_is_told_only_while_open_and_not_closing_itself(self):
        # QA item 4: a child whose parent is pinned to the same full account.
        parent, child = PASS("P", "blocked"), I("c", "todo", "P", title="t")
        acts = decide_pins([parent, child], [EXHAUSTED("P"), EXHAUSTED("c")], {"P": PIN(), "c": PIN()})
        self.assertEqual(sorted((a["id"], a.get("notify")) for a in acts), [("P", None), ("c", False)])
        # Control: a parent that is open and not stopped is told.
        acts = decide_pins([parent, child], [EXHAUSTED("c")], {"c": PIN()})
        self.assertEqual([(a["id"], a["notify"]) for a in acts], [("c", True)])
        # And a parent already done is never commented on (a comment reopens it).
        done = PASS("P", "done")
        self.assertEqual([(a["id"], a["notify"]) for a in decide_pins([done, child], [EXHAUSTED("c")], {"c": PIN()})],
                         [("c", False)])

    def test_a_run_that_hit_the_limit_part_way_counts_for_a_pinned_task_only(self):
        child = I("c", "todo", "P", title="t")
        self.assertEqual([a["kind"] for a in decide_pins([child], [LIMIT_FAILED("c")], {"c": PIN()})],
                         ["close_pinned"])
        # Control: unpinned, a failed run gets no reset (the reset answers only
        # the router's own cancel), and another failure is not this rule.
        self.assertNotIn(("reset_session", "c"), decide([child], [LIMIT_FAILED("c")]))
        other = RUN("c", "failed", errorCode="adapter_error", error="boom")
        self.assertNotIn("close_pinned", kinds(decide_pins([child], [other], {"c": PIN()})))

    def test_only_a_fresh_stop_with_no_live_run_on_an_open_task(self):
        child = I("c", "todo", "P", title="t")
        moved = I("c", "todo", "P", title="t", idle=30 * 60)  # activity after a stop an hour ago
        self.assertNotIn("close_pinned", kinds(decide_pins([moved], [EXHAUSTED("c", at=NOW - 3600)], {"c": PIN()})))
        live = I("c", "in_progress", "P", title="t", activeRun={"id": "r"})
        self.assertEqual(decide_pins([live], [EXHAUSTED("c")], {"c": PIN()}), [])
        done = I("c", "done", "P", title="t")
        self.assertEqual(decide_pins([done], [EXHAUSTED("c")], {"c": PIN()}), [])

    def test_at_most_one_close_per_key_then_logged_once(self):
        child = I("c2", "todo", "P", title="t")
        st = {"repinned": {"child:P:t": NOW - 3600}}
        acts = decide_pins([child], [EXHAUSTED("c2")], {"c2": PIN()}, state=st)
        self.assertEqual([(a["kind"], a["key"]) for a in acts], [("repin_repeat", "child:P:t")])
        st["repin_logged"] = {"child:P:t": NOW - 60}
        self.assertEqual(decide_pins([child], [EXHAUSTED("c2")], {"c2": PIN()}, state=st), [])
        # Control: after REPIN_EVERY a new close is allowed, and another
        # title under the same parent is its own key.
        old = {"repinned": {"child:P:t": NOW - warden.REPIN_EVERY}}
        self.assertEqual([a["kind"] for a in decide_pins([child], [EXHAUSTED("c2")], {"c2": PIN()}, state=old)],
                         ["close_pinned"])
        other = I("c3", "todo", "P", title="u")
        self.assertEqual([a["kind"] for a in decide_pins([other], [EXHAUSTED("c3")], {"c3": PIN()}, state=st)],
                         ["close_pinned"])

    def test_root_only_company_closes_routine_fires_and_nothing_else(self):
        child = I("c", "todo", "P", title="t")
        issues = [PASS("P", "todo"), child, I("s", "in_progress", "P2", idle=3600)]
        runs = [EXHAUSTED("P"), EXHAUSTED("c")]
        acts = decide_pins(issues, runs, {"P": PIN(), "c": PIN()}, root_only=True)
        self.assertEqual([(a["kind"], a["id"]) for a in acts], [("close_pinned", "P")])
        # Control: the same world as the fix company also closes the child and
        # wakes the stalled task; root_only is what turns those off.
        full = [(a["kind"], a["id"]) for a in decide_pins(issues, runs, {"P": PIN(), "c": PIN()})]
        self.assertIn(("close_pinned", "c"), full)
        self.assertIn(("wake_child", "s"), full)
        # And an unpinned stop in the triage company gets no reset.
        self.assertEqual(decide_pins([PASS("P", "todo")], [EXHAUSTED("P")], {}, root_only=True), [])

    def test_account_free_at_reads_only_reached_limits(self):
        self.assertEqual(warden.account_free_at(POOL_WITH(7200), CONN_FULL), NOW + 7200)
        self.assertIsNone(warden.account_free_at(POOL_WITH(None), CONN_FULL), "no reset time")
        self.assertIsNone(warden.account_free_at(POOL_WITH(7200), CONN_FREE), "nothing reached")
        self.assertIsNone(warden.account_free_at(POOL_WITH(7200), "unknown"))
        self.assertIsNone(warden.account_free_at(None, CONN_FULL))

    def test_routed_connection_and_repin_key(self):
        self.assertEqual(warden.routed_connection(ROUTED("c")), CONN_FULL)
        self.assertIsNone(warden.routed_connection(EXHAUSTED("c")))
        self.assertIsNone(warden.routed_connection(None))
        self.assertEqual(warden.repin_key(I("c", "todo", "P", title="t")), "child:P:t")
        self.assertEqual(warden.repin_key(PASS("P")), "routine:rt-fix")
        self.assertIsNone(warden.repin_key(I("M", "todo", None)))


class PinnedUsageTickTests(unittest.TestCase):
    def pc(self, issues, runs, full=()):
        pc = FakePC()
        pc.all, pc.run_rows, pc.pool = issues, runs, POOL_WITH(6 * 3600)
        pc.asked = []
        orig = pc.all_issues

        def all_issues(co):
            pc.asked.append(co)
            return orig(co)
        pc.all_issues = all_issues
        pc.run_full = {r["id"]: r for r in full}
        return pc

    def test_a_pinned_child_is_closed_bare_and_the_parent_told(self):
        # The listing trims contextSnapshot: the routed account is read from
        # the run itself.
        routed = ROUTED("c")
        trimmed = dict(routed, contextSnapshot={"taskKey": "c"})
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), trimmed], [routed])
        state = {}
        _, _, logs = tick(pc=pc, state=state, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [("c", "done")])
        self.assertEqual(pc.resets, [], "a pinned task is never reset")
        self.assertEqual(len(pc.comments), 1)
        to, body = pc.comments[0]
        self.assertEqual(to, "P")
        for needle in ("QAF-c", "over its usage limit until " + warden.iso(NOW + 6 * 3600),
                       "with the same title and the same description", "not a release"):
            self.assertIn(needle, body)
        self.assertNotIn(("c", "cancelled"), pc.statuses, "never a release")
        self.assertIn("child:P:t", state["unstick"]["companies"]["co"]["repinned"])
        self.assertTrue(any("closed QAF-c: pinned to an exhausted account" in l for l in logs))

    def test_control_an_unpinned_stop_still_gets_the_reset(self):
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c")],
                     [EXHAUSTED("c")])
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [])
        self.assertEqual(len(pc.resets), 1)

    def test_lookups_are_bounded(self):
        runs = [EXHAUSTED("c")] + [dict(RUN("c", "succeeded", at=NOW - 3600 - k), id=f"r{k}") for k in range(9)]
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], runs, runs)
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(len(pc.run_reads), warden.PIN_LOOKUPS)

    def test_the_triage_company_is_read_for_routine_fires_only(self):
        pc = self.pc([], [])
        pc.all_by["tri"] = [PASS("T", "blocked", routine="rt-tri"), I("x", "todo", "T", title="helper")]
        routed_t, routed_x = ROUTED("T"), ROUTED("x")
        pc.runs_by["tri"] = [EXHAUSTED("T"), routed_t, EXHAUSTED("x"), routed_x]
        pc.run_full = {r["id"]: r for r in (routed_t, routed_x)}
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co", "triage_company_id": "tri"})
        self.assertEqual(pc.statuses, [("T", "done")])
        self.assertEqual(pc.comments, [])
        # Control: paused, nothing at all.
        pc2 = self.pc([], [])
        pc2.all_by, pc2.runs_by, pc2.run_full = pc.all_by, pc.runs_by, pc.run_full
        pc2.paused_cos = {"tri"}
        tick(pc=pc2, state={}, now=NOW, cfg={"company_id": "co", "triage_company_id": "tri"})
        self.assertEqual(pc2.statuses, [])

    def test_the_triage_id_falls_back_to_the_file_beside_pool_usage(self):
        d = tempfile.mkdtemp()
        with open(os.path.join(d, "triage-company-id.txt"), "w") as f:
            f.write("tri\n")
        w = warden.Warden.__new__(warden.Warden)
        w.cfg = {"company_id": "co", "pool_usage": os.path.join(d, "pool-usage.json")}
        self.assertEqual(w.triage_company(), "tri")
        w.cfg = {"company_id": "co", "pool_usage": os.path.join(d, "pool-usage.json"), "triage_company_id": "cfg"}
        self.assertEqual(w.triage_company(), "cfg")
        w.cfg = {"company_id": "co", "pool_usage": os.path.join(tempfile.mkdtemp(), "pool-usage.json")}
        self.assertIsNone(w.triage_company())

    def test_closing_a_routine_fire_settles_idle_ticket_children_done_never_cancelled(self):
        # Paperclip does not cascade a parent's close (ORB-220, ORB-196; ORB-241
        # and ORB-242), so the warden settles the idle ticket children itself,
        # `done`, and leaves the one with a live run. Nothing the brake counts.
        routed = ROUTED("P")
        kids = [I("e", "in_progress", "P", title="fix", description="Ticket: CB-1\nplan"),
                I("q", "blocked", "P", title="qa", description="Ticket: CB-1\nreview"),
                I("r", "in_progress", "P", title="run", description="Ticket: CB-1\nx", activeRun={"id": "x"})]
        pc = self.pc([PASS("P", "todo")] + kids, [EXHAUSTED("P"), routed], [routed])
        state = {}
        _, _, logs = tick(pc=pc, state=state, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [("P", "done"), ("e", "done"), ("q", "done")])
        self.assertTrue(any("settled 2 idle ticket child(ren) done" in l for l in logs))
        self.assertFalse([c for c in pc.comments if c[0] == "P"], "no comment on the closed fire")
        self.assertFalse([s for s in pc.statuses if s[1] == "cancelled"])
        self.assertEqual(state.get("consecutive_releases", 0), 0)

    def test_pause_and_repeat_keys_are_per_company(self):
        def world():
            pc = self.pc([PASS("P", "todo", routine="rt-fix")], [])
            rf, rt = ROUTED("P"), ROUTED("T")
            pc.runs_by = {"co": [EXHAUSTED("P"), rf], "tri": [EXHAUSTED("T"), rt]}
            pc.all_by = {"tri": [PASS("T", "todo", routine="rt-fix")]}
            pc.run_full = {rf["id"]: rf, rt["id"]: rt}
            return pc
        cfg = {"company_id": "co", "triage_company_id": "tri"}
        # The fix company paused: triage still acts.
        pc = world()
        pc.paused = True
        tick(pc=pc, state={}, now=NOW, cfg=cfg)
        self.assertIn(("T", "done"), pc.statuses)
        self.assertNotIn(("P", "done"), pc.statuses)
        # Triage paused: the fix company still acts.
        pc = world()
        pc.paused_cos = {"tri"}
        tick(pc=pc, state={}, now=NOW, cfg=cfg)
        self.assertEqual(pc.statuses, [("P", "done")])
        # The same routine key in both companies: one company's close does not
        # count as the other's repeat.
        pc = world()
        state = {}
        tick(pc=pc, state=state, now=NOW, cfg=cfg)
        self.assertEqual(sorted(pc.statuses), [("P", "done"), ("T", "done")])
        cos = state["unstick"]["companies"]
        self.assertIn("routine:rt-fix", cos["co"]["repinned"])
        self.assertIn("routine:rt-fix", cos["tri"]["repinned"])

    def test_no_triage_id_means_no_second_company(self):
        pc = self.pc([], [])
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.asked, ["co"])
        pc = self.pc([], [])
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co", "triage_company_id": "tri"})
        self.assertEqual(pc.asked, ["co", "tri"])

    def test_a_stale_pool_file_closes_nothing(self):
        routed = ROUTED("c")
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), routed], [routed])
        pc.pool = POOL_WITH(6 * 3600, now=NOW - 3600)  # every reading an hour old
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [])
        self.assertEqual(pc.resets, [])

    def test_a_stale_reading_of_the_pinned_account_is_an_unknown_reset(self):
        # Its reset reads 10 minutes ahead, but that reading is an hour old: not
        # trusted, so not a short wait, so the router cancel closes.
        routed = ROUTED("c")
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), routed], [routed])
        pc.pool = POOL_WITH(600)
        pc.pool["accounts"][CONN_FULL]["checkedAt"] = warden.iso(NOW - 3600)
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [("c", "done")])
        # Control: the same reading fresh is a short wait.
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), routed], [routed])
        pc.pool = POOL_WITH(600)
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [])

    def test_a_stale_file_is_logged_once(self):
        pc = self.pc([], [])
        pc.pool["writtenAt"] = warden.iso(NOW - 3600)
        state = {}
        _, _, logs = tick(pc=pc, state=state, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(sum("pool-usage.json is stale" in l for l in logs), 1)
        _, _, logs = tick(pc=pc, state=state, now=NOW + 300, cfg={"company_id": "co"})
        self.assertEqual(sum("pool-usage.json is stale" in l for l in logs), 0, "once per writing")
        # Control: a fresh file logs nothing.
        pc2 = self.pc([], [])
        pc2.pool["writtenAt"] = warden.iso(NOW - 120)
        _, _, logs = tick(pc=pc2, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertFalse([l for l in logs if "stale" in l])

    def test_room_only_on_a_stale_reading_closes_nothing(self):
        routed = ROUTED("c")
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), routed], [routed])
        pc.pool["accounts"][CONN_FREE]["checkedAt"] = warden.iso(NOW - 3600)
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [])
        # Control: the same world with that reading fresh closes the child.
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), routed], [routed])
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.statuses, [("c", "done")])

    def test_paused_fix_company_closes_nothing(self):
        routed = ROUTED("c")
        pc = self.pc([PASS("P"), I("c", "in_progress", "P", title="t")], [EXHAUSTED("c"), routed], [routed])
        pc.paused = True
        tick(pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertNotIn(("c", "done"), pc.statuses)


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
        self.assertEqual(state["unstick"]["companies"]["co"]["parent_woken"], {"P": NOW})
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
        state["unstick"]["companies"]["co"]["child_wakes"] = {"c": [2, NOW - 3600]}
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
        self.assertIn("c", state["unstick"]["companies"]["co"]["resets"])
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
        self.assertEqual(state["unstick"]["companies"]["co"].get("parent_woken", {}), {},
                         "retried next tick, not skipped")

    def test_two_companies_keep_each_others_wakes(self):
        # QA, CB-479: company B's prune must not forget company A's wakes.
        class TwoCos(FakePC):
            def all_issues(self, company):
                return self.by_co[company]
        pc = TwoCos()
        pc.pool = POOL_OK
        pc.by_co = {"A": released_pass(), "B": [I("x", "in_progress", idle=60)]}
        cfg = {"company_id": "A", "unstick_company_ids": ["A", "B"]}
        state = {}
        self.run_tick(pc, state, cfg=cfg)
        self.assertEqual(state["unstick"]["companies"]["A"]["parent_woken"], {"P": NOW})
        self.run_tick(pc, state, now=NOW + 5 * 60, cfg=cfg)
        self.assertEqual(len(pc.comments), 1, "not re-woken every tick")
        self.run_tick(pc, state, now=NOW + 15 * 60, cfg=cfg)
        self.assertEqual(pc.statuses, [("P", "done")])

    def test_a_malformed_row_is_logged_and_the_tick_still_finishes(self):
        pc = self.pc([{"status": "todo"}] + released_pass())
        state = {}
        w, paused, logs = self.run_tick(pc, state)
        self.assertFalse(paused)
        self.assertTrue(any("unstick sweep failed for company co: KeyError" in l for l in logs))
        self.assertEqual(state["last_mint"], NOW, "the rest of the tick ran and its state is kept")

    def test_a_full_listing_page_skips_the_company(self):
        pc = self.pc(released_pass() + [I(f"n{k}", "done", idle=3600) for k in range(996)])
        pc.said = {"P": NOW}
        _, _, logs = self.run_tick(pc, {})
        self.assertEqual(pc.statuses, [])
        self.assertTrue(any("listing may be truncated" in l for l in logs))
        pc.all = pc.all[:-1]
        self.run_tick(pc, {})
        self.assertEqual(pc.statuses, [("P", "done")], "control: one row under the page acts")

    def test_a_failed_cancel_is_not_left_on_the_exclusion_list(self):
        state = {"unstick": {"cancelled": ["older"],
                             "companies": {"co": {"child_wakes": {"c": [2, NOW - 3600]}}}}}
        pc = self.pc([I("P", "in_progress", idle=60), I("c", "in_progress", "P", idle=3600, assignee="eng")])
        pc.fail_writes = True
        _, _, logs = self.run_tick(pc, state)
        self.assertEqual(state["unstick"]["cancelled"], ["older"])
        self.assertTrue(any("unstick cancel_child QAF-c failed" in l for l in logs))
        pc.fail_writes = False
        self.run_tick(pc, state, now=NOW + 300)
        self.assertEqual(state["unstick"]["cancelled"], ["older", "c"], "the retry adds it back")

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


REAL_FILES = {
 397: [
  {
   "filename": ".github/workflows/ci.yml",
   "status": "modified",
   "patch": "@@ -156,6 +156,20 @@ jobs:\n         if: ${{ !cancelled() && matrix.checks }}\n         run: python tools/test-bugfix-host.py\n \n+      # CB-470: the machine runner (spool rules, hold, cap, restore) and its\n+      # Windows seam, against fake machines, a fake gh and a fake clock. No\n+      # ssh and no machine; the run_once file-level cases skip on Windows,\n+      # where the runner (which lives on the mini) never runs.\n+      - name: Unit tests (bug-fix org machine runner)\n+        if: ${{ !cancelled() && matrix.checks }}\n+        run: python tools/test-bugfix-machines.py\n+\n+      # CB-470: the Mac half of the machine runner (signed e2e build, install,\n+      # restore), same fakes-only rule.\n+      - name: Unit tests (bug-fix org machine runner, Macs)\n+        if: ${{ !cancelled() && matrix.checks }}\n+        run: python tools/test-bugfix-machines-mac.py\n+\n       # CB-455: the same host side against loopback stand-ins for Paperclip and the\n       # GitHub App API and stub gh/dotnet scripts, every pause trigger beside its\n       # control. POSIX only; the cases skip themselves on the Windows leg."
  }
 ],
 381: [
  {
   "filename": ".github/workflows/ci.yml",
   "status": "modified",
   "patch": "@@ -59,6 +59,13 @@ concurrency:\n   group: ci-${{ github.event.pull_request.number || github.run_id }}\n   cancel-in-progress: ${{ github.event_name == 'pull_request' }}\n \n+# CB-466: nothing a workflow launches may count as a real install. The suites\n+# never reach the ping (see InstallPingSingleSiteTests); this covers any step that\n+# starts a built app, and marks it ours if it somehow does.\n+env:\n+  ORBWEAVER_INSTALL_PING: \"0\"\n+  ORBWEAVER_INTERNAL: \"1\"\n+\n jobs:\n   # One job definition; which suites each instance runs is in the matrix below.\n   suite:"
  },
  {
   "filename": ".github/workflows/release.yml",
   "status": "modified",
   "patch": "@@ -18,6 +18,13 @@ on:\n permissions:\n   contents: read\n \n+# CB-466: nothing a workflow launches may count as a real install. The suites\n+# never reach the ping (see InstallPingSingleSiteTests); this covers any step that\n+# starts a built app, and marks it ours if it somehow does.\n+env:\n+  ORBWEAVER_INSTALL_PING: \"0\"\n+  ORBWEAVER_INTERNAL: \"1\"\n+\n jobs:\n   # Runs first and gates everything else. A tag whose number disagrees with the\n   # csproj would ship installers named one version while reporting another in"
  }
 ],
 370: [
  {
   "filename": ".github/workflows/stress.yml",
   "status": "added",
   "patch": "@@ -0,0 +1,79 @@\n+name: Stress\n+\n+# Runs ONE test class N times on both rids (CB-455, plan \u00a74). The bug-fix org's\n+# proof for a flaky-test fix is \"this class, 30 times, on GitHub's runners\",\n+# and the org itself cannot dispatch this (its App has Actions read only), so"
  }
 ]
}

# ---------------------------------------------------------------- CB-481: mobile repository and workflow files
#
# REAL_FILES above are GET /pulls/<n>/files rows of three merged desktop PRs,
# read 2026-11-26 (only the .github/ rows, and #370's added file cut to its
# first lines, since an added workflow pauses before its patch is read): #397
# added test steps to ci.yml (benign), #381 added an env block to ci.yml and
# release.yml, #370 added stress.yml.

TWO = {"repos": [warden.DESKTOP, warden.MOBILE]}


def wf(name, patch, status="modified", **over):
    row = {"filename": ".github/workflows/" + name, "status": status, "patch": patch}
    row.update(over)
    return row


def mobile_pr(sha="m1", **over):
    pr = {"state": "open", "user": {"login": BOT}, "number": 5,
          "head": {"ref": "bugfix/pc-relay-race", "repo": {"full_name": MOBILE_REPO}, "sha": sha},
          "base": {"ref": "develop"}}
    pr.update(over)
    return pr


class RepoConfigTests(unittest.TestCase):
    def test_no_repos_key_means_desktop_only_and_a_list_is_taken_as_given(self):
        self.assertEqual(warden.repos_from({}), [warden.DESKTOP])
        self.assertEqual([r["key"] for r in warden.repos_from(TWO)], ["desktop", "mobile"])
        self.assertIsNone(warden.repo_by_key({}, "mobile"), "mobile exists only once configured")

    def test_the_lane_is_the_repository_it_is_asked_about(self):
        self.assertIsNone(warden.dispatch_problem(mobile_pr(), warden.MOBILE))
        self.assertIn("not in this repository", warden.dispatch_problem(mobile_pr(), warden.DESKTOP))
        self.assertIn("not in this repository", warden.dispatch_problem(good_pr(), warden.MOBILE))
        self.assertIsNone(warden.dispatch_problem(good_pr()), "desktop is the default")
        self.assertIn("does not target develop",
                      warden.dispatch_problem(mobile_pr(base={"ref": "main"}), warden.MOBILE))

    def test_spool_repo_key_is_optional_and_patterned(self):
        ok, _ = warden.parse_request('{"verb":"rerun_failed","pr":7,"repo":"mobile"}')
        self.assertEqual(ok, {"verb": "rerun_failed", "pr": 7, "repo": "mobile"})
        self.assertNotIn("repo", warden.parse_request('{"verb":"rerun_failed","pr":7}')[0])
        for bad in ('"../x"', '7', '"Mobile"', '""', 'null'):
            req, why = warden.parse_request('{"verb":"rerun_failed","pr":7,"repo":%s}' % bad)
            self.assertIsNone(req, bad)
            self.assertIn("repo", why)


class MobileHeadCheckTests(unittest.TestCase):
    ALL3 = {"relay": "success", "contracts": "success", "tools": "success"}

    def test_expected_checks_follow_the_changed_paths(self):
        m = warden.MOBILE
        self.assertEqual(warden.expected_head_checks(["relay/Tests/X.cs"], m), ["relay", "contracts", "tools"])
        self.assertIn("android", warden.expected_head_checks(["android/app/Y.kt"], m))
        self.assertIn("android", warden.expected_head_checks(["contracts/check_frames.py"], m))
        self.assertIn("snapshots", warden.expected_head_checks(["app/Sources/Z.swift"], m))
        self.assertIn("snapshots", warden.expected_head_checks(["contracts/frames-v11-vectors.json"], m))
        self.assertNotIn("snapshots", warden.expected_head_checks(["contracts/frames-v9-vectors.json"], m))
        self.assertNotIn("android", warden.expected_head_checks(["relay/x"], m))

    def test_all_green_passes_and_each_failure_is_named(self):
        m = warden.MOBILE
        self.assertEqual(warden.head_checks_problem(self.ALL3, ["relay/x"], m), [])
        red = warden.head_checks_problem(dict(self.ALL3, tools="failure"), ["relay/x"], m)
        self.assertIn("tools failure", red[0])
        missing = warden.head_checks_problem(self.ALL3, ["android/a.kt"], m)
        self.assertIn("android missing", missing[0])
        pending = warden.head_checks_problem(dict(self.ALL3, android=None), ["android/a.kt"], m)
        self.assertIn("android pending", pending[0])
        self.assertEqual(warden.head_checks_problem(dict(self.ALL3, android="success"), ["android/a.kt"], m), [])

    def test_mobile_merge_commit_reds_count_and_settle_after_the_quiet_window(self):
        m = warden.MOBILE
        self.assertEqual(warden.audit_merge(True, {}, m), [])
        self.assertIn("relay-image", warden.audit_merge(True, {"relay-image": "failure"}, m)[0])
        self.assertIn("relay and contracts and tools", warden.audit_merge(False, {}, m)[0])
        self.assertFalse(warden.ci_settled({}, m, age=warden.MERGE_QUIET - 1))
        self.assertTrue(warden.ci_settled({}, m, age=warden.MERGE_QUIET))
        self.assertFalse(warden.ci_settled({"relay-image": None}, m, age=warden.MERGE_QUIET))
        # Control: desktop still waits on its two required checks, not on time.
        self.assertFalse(warden.ci_settled({}, warden.DESKTOP, age=10 ** 6))
        self.assertIn("both rids", warden.audit_merge(False, {})[0])


class WorkflowGuardTests(unittest.TestCase):
    D = warden.DESKTOP

    def test_real_benign_ci_step_change_is_left_alone(self):
        self.assertEqual(warden.workflow_problems(REAL_FILES[397], self.D), [])
        # The ci.yml half of #381 (an env block) is benign on its own too.
        self.assertEqual(warden.workflow_problems(REAL_FILES[381][:1], self.D), [])

    def test_real_release_yml_change_pauses(self):
        got = warden.workflow_problems(REAL_FILES[381], self.D)
        self.assertEqual(len(got), 1)
        self.assertIn("release.yml: change to a protected workflow", got[0])

    def test_real_added_workflow_pauses(self):
        got = warden.workflow_problems(REAL_FILES[370], self.D)
        self.assertEqual(got, [".github/workflows/stress.yml: workflow file added"])

    def test_publish_screenshots_is_protected_even_for_a_comment(self):
        got = warden.workflow_problems([wf("publish-screenshots.yml", "@@ -1 +1 @@\n-# a\n+# b")], self.D)
        self.assertIn("protected", got[0])

    def test_removed_and_renamed_files_pause(self):
        self.assertIn("removed", warden.workflow_problems([wf("ci.yml", None, "removed")], self.D)[0])
        got = warden.workflow_problems([wf("ci2.yml", "", "renamed", previous_filename=".github/workflows/ci.yml")], self.D)
        self.assertIn("renamed", got[0])
        # Renaming a protected workflow away is still a change to it.
        got = warden.workflow_problems([wf("r.yml", "", "renamed", previous_filename=".github/workflows/release.yml")], self.D)
        self.assertIn("protected", got[0])

    def test_each_risky_line_pauses_and_its_harmless_neighbour_does_not(self):
        risky = ["    environment: release", "      KEY: ${{ secrets.APPLE_KEY }}", "  pull_request_target:",
                 "  workflow_run:", "  id-token: write", "permissions:", "      contents: write",
                 "    runs-on: [self-hosted, orbweaver-ios]", "      - run: tools/publish-screenshot.sh",
                 "      - uses: someone/thing@v1"]
        for line in risky:
            for sign in "+-":
                got = warden.workflow_problems([wf("ci.yml", f"@@ -1 +1 @@\n{sign}{line}")], self.D)
                self.assertEqual(len(got), 1, (sign, line))
        harmless = ["    timeout-minutes: 45", "      - uses: actions/checkout@v4", "      - uses: ./.github/actions/setup",
                    "    # environment: release would go here", "        run: python tools/test-bugfix-host.py", ""]
        for line in harmless:
            self.assertEqual(warden.workflow_problems([wf("ci.yml", f"@@ -1 +1 @@\n+{line}")], self.D), [], line)

    def test_a_benign_timeout_edit_does_not_pause(self):
        patch = "@@ -40,7 +40,7 @@ jobs:\n   suite:\n-    timeout-minutes: 30\n+    timeout-minutes: 45\n     steps:"
        self.assertEqual(warden.workflow_problems([wf("ci.yml", patch)], self.D), [])

    def test_unchanged_context_lines_are_not_read(self):
        patch = "@@ -1,3 +1,3 @@\n     environment: release\n-    timeout-minutes: 30\n+    timeout-minutes: 45"
        self.assertEqual(warden.workflow_problems([wf("ci.yml", patch)], self.D), [])

    def test_a_workflow_file_with_no_patch_pauses(self):
        got = warden.workflow_problems([{"filename": ".github/workflows/ci.yml", "status": "modified"}], self.D)
        self.assertIn("no patch", got[0])

    def test_actions_dir_is_guarded_and_other_files_are_not(self):
        row = {"filename": ".github/actions/setup/action.yml", "status": "modified", "patch": "+  secrets: x"}
        self.assertEqual(len(warden.workflow_problems([row], self.D)), 1)
        other = {"filename": "host/warden.py", "status": "modified", "patch": "+ environment secrets runs-on"}
        self.assertEqual(warden.workflow_problems([other], self.D), [])

    def test_a_composite_action_is_guarded_like_a_workflow(self):
        act = ".github/actions/setup/action.yml"
        for status in ("added", "removed", "renamed"):
            got = warden.workflow_problems([{"filename": act, "status": status, "patch": "+x"}], self.D)
            self.assertIn(f"workflow file {status}", got[0], status)
        moved_in = {"filename": "tools/setup.yml", "status": "renamed", "previous_filename": act}
        self.assertEqual(len(warden.workflow_problems([moved_in], self.D)), 1, "moved out of .github/actions")
        risky = {"filename": act, "status": "modified", "patch": "@@ -1 +1 @@\n+      KEY: ${{ secrets.X }}"}
        self.assertIn("mentions 'secrets'", warden.workflow_problems([risky], self.D)[0])
        benign = {"filename": act, "status": "modified", "patch": "@@ -1 +1 @@\n-      shell: bash\n+      shell: pwsh"}
        self.assertEqual(warden.workflow_problems([benign], self.D), [], "control")
        self.assertIn("may not edit workflows", warden.workflow_problems([benign], warden.MOBILE)[0])

    def test_any_workflow_change_on_mobile_pauses_and_its_code_does_not(self):
        got = warden.workflow_problems(REAL_FILES[397], warden.MOBILE)
        self.assertIn("may not edit workflows", got[0])
        code = {"filename": "relay/Relay/Hub.cs", "status": "modified", "patch": "+ x"}
        self.assertEqual(warden.workflow_problems([code], warden.MOBILE), [])


class TokenPlanTests(unittest.TestCase):
    def test_both_repositories_on_the_everyday_token_and_desktop_alone_on_the_workflow_token(self):
        plan = warden.token_plan([warden.DESKTOP, warden.MOBILE],
                                 {"desktop": WF_INSTALL, "mobile": WF_INSTALL})
        self.assertEqual(plan, {"installation": 9, "everyday": ["Orbweaver", "Orbweaver-Mobile"],
                                "workflows": ["Orbweaver"]})

    def test_the_workflow_token_never_names_mobile(self):
        for inst in (DESKTOP_INSTALL, WF_INSTALL):
            for access in ({"desktop": inst, "mobile": inst}, {"desktop": None, "mobile": inst}):
                plan = warden.token_plan([warden.DESKTOP, warden.MOBILE], access)
                self.assertNotIn("Orbweaver-Mobile", plan["workflows"], (inst, access))
        # Control: the rule is not simply "never mint one".
        self.assertEqual(warden.token_plan([warden.DESKTOP], {"desktop": WF_INSTALL})["workflows"], ["Orbweaver"])

    def test_no_workflow_token_while_the_installation_lacks_the_permission(self):
        plan = warden.token_plan([warden.DESKTOP, warden.MOBILE], {"desktop": DESKTOP_INSTALL, "mobile": DESKTOP_INSTALL})
        self.assertEqual(plan["workflows"], [])
        self.assertEqual(plan["everyday"], ["Orbweaver", "Orbweaver-Mobile"])

    def test_uncovered_repositories_are_left_out(self):
        plan = warden.token_plan([warden.DESKTOP, warden.MOBILE], {"desktop": DESKTOP_INSTALL, "mobile": None})
        self.assertEqual(plan["everyday"], ["Orbweaver"])
        other = dict(DESKTOP_INSTALL, id=10)
        plan = warden.token_plan([warden.DESKTOP, warden.MOBILE], {"desktop": DESKTOP_INSTALL, "mobile": other})
        self.assertEqual(plan["everyday"], ["Orbweaver"], "a repository under another installation")
        self.assertEqual(warden.token_plan([warden.DESKTOP], {"desktop": None})["everyday"], [])

    def test_the_everyday_permissions_are_the_six_by_name_without_workflows(self):
        self.assertEqual(warden.EVERYDAY_PERMISSIONS, {
            "actions": "read", "checks": "read", "contents": "write", "metadata": "read",
            "pull_requests": "write", "statuses": "read"})
        self.assertEqual(warden.WORKFLOW_PERMISSIONS, dict(warden.EVERYDAY_PERMISSIONS, workflows="write"))


def read_json(path):
    with open(path) as f:
        return json.load(f)


# Shaped like Orbweaver's own reads of 2026-11-26 (rulesets listing; rules/branches/develop).
ACTIVE_RS = [{"id": 1, "name": "main and develop: PRs only", "target": "branch", "enforcement": "active"}]
DEVELOP_RULES = [{"type": "deletion", "ruleset_id": 1}, {"type": "pull_request", "ruleset_id": 1},
                 {"type": "required_status_checks", "ruleset_id": 1}]


def protected_mobile(**kw):
    return FakeGH(rulesets=ACTIVE_RS, branch_rules=DEVELOP_RULES, **kw)


def both(rules):
    return {"main": rules, "develop": rules}


class RulesetGateTests(unittest.TestCase):
    def test_both_branches_are_gated(self):
        self.assertEqual(warden.gated_branches(warden.MOBILE), ["main", "develop"])

    def test_an_active_pull_request_rule_on_main_and_develop_passes(self):
        self.assertIsNone(warden.ruleset_gate(ACTIVE_RS, both(DEVELOP_RULES)))

    def test_develop_only_or_main_only_is_not_enough(self):
        self.assertEqual(warden.ruleset_gate(ACTIVE_RS, {"main": [], "develop": DEVELOP_RULES}),
                         "no active pull_request rule on main")
        self.assertEqual(warden.ruleset_gate(ACTIVE_RS, {"main": DEVELOP_RULES, "develop": []}),
                         "no active pull_request rule on develop")
        self.assertEqual(warden.ruleset_gate(ACTIVE_RS, {"main": [], "develop": []}),
                         "no active pull_request rule on main and develop")

    def test_each_way_of_not_being_protected_is_named(self):
        disabled = [dict(ACTIVE_RS[0], enforcement="disabled")]
        evaluate = [dict(ACTIVE_RS[0], enforcement="evaluate")]
        cases = {
            "empty": ([], [], "no rulesets"),
            "disabled": (disabled, DEVELOP_RULES, "no active ruleset"),
            "evaluate only": (evaluate, DEVELOP_RULES, "no active ruleset"),
            "active but nothing on either branch": (ACTIVE_RS, [], "no active pull_request rule on main and develop"),
            "deletion only": (ACTIVE_RS, [{"type": "deletion", "ruleset_id": 1}], "pull_request"),
            "rule from another, inactive ruleset": (ACTIVE_RS + [dict(ACTIVE_RS[0], id=2, enforcement="disabled")],
                                                    [{"type": "pull_request", "ruleset_id": 2}], "pull_request"),
        }
        for label, (rs, rules, why) in cases.items():
            self.assertIn(why, warden.ruleset_gate(rs, both(rules)) or "", label)


class MintTickTests(unittest.TestCase):
    def setUp(self):
        self.spool = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(self.spool, True))

    def test_old_config_mints_one_desktop_token_with_the_six_permissions(self):
        pc, m = FakePC(), FakeMinter()
        tick(pc=pc, minter=m)
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertEqual(m.minted, [(9, ["Orbweaver"], warden.EVERYDAY_PERMISSIONS)])

    def test_two_repositories_and_the_permission_mint_both_tokens_each_scoped(self):
        pc, m = FakePC(), FakeMinter({warden.REPO: WF_INSTALL, MOBILE_REPO: WF_INSTALL})
        gh = {"desktop": FakeGH(), "mobile": protected_mobile()}
        _, _, logs = tick(gh=gh, pc=pc, minter=m, cfg=dict(TWO, spool=self.spool))
        self.assertEqual([n for n, _ in pc.secrets], ["GITHUB_TOKEN", "GITHUB_TOKEN_WORKFLOWS"])
        everyday, workflow = m.minted
        self.assertEqual(everyday, (9, ["Orbweaver", "Orbweaver-Mobile"], warden.EVERYDAY_PERMISSIONS))
        self.assertNotIn("workflows", everyday[2])
        self.assertEqual(workflow, (9, ["Orbweaver"], warden.WORKFLOW_PERMISSIONS))
        access = read_json(os.path.join(self.spool, "access.json"))
        self.assertEqual(access["repos"], {"desktop": True, "mobile": True})
        self.assertTrue(access["workflows"])
        self.assertTrue(any("workflow token refreshed (Orbweaver)" in l for l in logs))

    def test_workflow_token_is_withheld_while_the_permission_is_absent(self):
        pc, m = FakePC(), FakeMinter({warden.REPO: DESKTOP_INSTALL, MOBILE_REPO: DESKTOP_INSTALL})
        tick(gh={"desktop": FakeGH(), "mobile": protected_mobile()}, pc=pc, minter=m, cfg=dict(TWO, spool=self.spool))
        self.assertEqual([n for n, _ in pc.secrets], ["GITHUB_TOKEN"])
        self.assertEqual(len(m.minted), 1)
        self.assertFalse(read_json(os.path.join(self.spool, "access.json"))["workflows"])

    def test_uncovered_mobile_is_reported_and_left_off_the_token(self):
        pc, m = FakePC(), FakeMinter({warden.REPO: DESKTOP_INSTALL})
        tick(pc=pc, minter=m, cfg=dict(TWO, spool=self.spool))
        self.assertEqual(m.minted[0][1], ["Orbweaver"])
        self.assertEqual(read_json(os.path.join(self.spool, "access.json"))["repos"],
                         {"desktop": True, "mobile": False})

    def mint_with_mobile(self, mobile_gh, state=None):
        if state is not None:
            state.pop("last_mint", None)   # every call is a mint tick
        pc, m = FakePC(), FakeMinter({warden.REPO: DESKTOP_INSTALL, MOBILE_REPO: DESKTOP_INSTALL})
        _, _, logs = tick(gh={"desktop": FakeGH(), "mobile": mobile_gh}, pc=pc, minter=m,
                          cfg=dict(TWO, spool=self.spool), state=state if state is not None else {})
        return m.minted[0][1], read_json(os.path.join(self.spool, "access.json")), logs

    def test_mobile_rulesets_present_puts_both_repositories_on_the_token(self):
        repos, access, logs = self.mint_with_mobile(protected_mobile())
        self.assertEqual(repos, ["Orbweaver", "Orbweaver-Mobile"])
        self.assertEqual((access["repos"], access["withheld"]), ({"desktop": True, "mobile": True}, {}))
        self.assertFalse(any("kept off" in l for l in logs))

    def test_empty_failed_or_disabled_mobile_rulesets_mint_orbweaver_only(self):
        disabled = [dict(ACTIVE_RS[0], enforcement="disabled")]
        cases = {"empty": (FakeGH(), "no rulesets"),
                 "failed read": (FakeGH(rulesets=UNREADABLE), "rulesets unreadable (CalledProcessError)"),
                 "failed rules read": (FakeGH(rulesets=ACTIVE_RS, branch_rules=UNREADABLE), "rulesets unreadable"),
                 "disabled": (FakeGH(rulesets=disabled, branch_rules=DEVELOP_RULES), "no active ruleset")}
        for label, (gh, why) in cases.items():
            repos, access, logs = self.mint_with_mobile(gh)
            self.assertEqual(repos, ["Orbweaver"], label)
            self.assertEqual(access["repos"], {"desktop": True, "mobile": False}, label)
            self.assertIn(why, access["withheld"]["mobile"], label)
            self.assertTrue(any(l.startswith("mobile kept off the token: " + why) for l in logs), label)

    def test_mobile_needs_both_main_and_develop_protected(self):
        cases = {"develop only": ({"develop": DEVELOP_RULES}, "on main"),
                 "main only": ({"main": DEVELOP_RULES}, "on develop")}
        for label, (rules, why) in cases.items():
            repos, access, _ = self.mint_with_mobile(FakeGH(rulesets=ACTIVE_RS, branch_rules=rules))
            self.assertEqual(repos, ["Orbweaver"], label)
            self.assertIn(why, access["withheld"]["mobile"], label)
        repos, _, _ = self.mint_with_mobile(FakeGH(rulesets=ACTIVE_RS, branch_rules=both(DEVELOP_RULES)))
        self.assertEqual(repos, ["Orbweaver", "Orbweaver-Mobile"], "control: both protected")
        rules = {"main": UNREADABLE, "develop": DEVELOP_RULES}
        _, access, _ = self.mint_with_mobile(FakeGH(rulesets=ACTIVE_RS, branch_rules=rules))
        self.assertIn("rulesets unreadable", access["withheld"]["mobile"], "a failed main read fails closed")

    def test_a_failed_installation_lookup_on_mobile_still_refreshes_desktop(self):
        pc = FakePC()
        m = FakeMinter({warden.REPO: DESKTOP_INSTALL, MOBILE_REPO: UNREADABLE})
        _, paused, logs = tick(gh={"desktop": FakeGH(), "mobile": protected_mobile()}, pc=pc, minter=m,
                               cfg=dict(TWO, spool=self.spool))
        self.assertFalse(paused)
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertEqual(m.minted[0][1], ["Orbweaver"])
        access = read_json(os.path.join(self.spool, "access.json"))
        self.assertEqual(access["repos"], {"desktop": True, "mobile": False})
        self.assertEqual(access["withheld"]["mobile"], "installation unreadable (OSError)")
        self.assertIn("mobile kept off the token: installation unreadable (OSError)", logs)

    def test_the_withheld_line_is_logged_once_per_state_change(self):
        state = {}
        _, _, logs = self.mint_with_mobile(FakeGH(), state)
        self.assertEqual(sum("kept off" in l for l in logs), 1)
        _, _, logs = self.mint_with_mobile(FakeGH(), state)
        self.assertEqual(sum("kept off" in l for l in logs), 0, "same reason, no new line")
        _, _, logs = self.mint_with_mobile(FakeGH(rulesets=UNREADABLE), state)
        self.assertEqual(sum("kept off" in l for l in logs), 1, "a new reason is a new line")
        repos, _, logs = self.mint_with_mobile(protected_mobile(), state)
        self.assertIn("mobile back on the token: its rulesets now protect it", logs)
        self.assertIn("Orbweaver-Mobile", repos)

    def test_desktop_never_waits_on_a_rulesets_read(self):
        class NoRulesets(FakeGH):
            def rulesets(self):
                raise AssertionError("desktop's rulesets must not be read")
        pc, m = FakePC(), FakeMinter()
        tick(gh=NoRulesets(), pc=pc, minter=m)
        self.assertEqual(m.minted[0][1], ["Orbweaver"])

    def test_a_failed_mint_is_logged_and_the_tick_goes_on(self):
        pc = FakePC()
        pc.all = [{"id": "p1", "identifier": "FIX-1", "status": "blocked", "updatedAt": "2026-01-01T00:00:00Z"},
                  {"id": "c1", "identifier": "FIX-2", "status": "done", "parentId": "p1",
                   "updatedAt": "2026-01-01T00:00:00Z"}]
        state = {}
        w, paused, logs = tick(pc=pc, state=state, minter=FakeMinter({warden.REPO: UNREADABLE}),
                               cfg={"company_id": "co"}, now=warden.ts("2026-01-02T00:00:00Z"))
        self.assertFalse(paused, "a failed mint is not a pause")
        self.assertEqual(pc.secrets, [])
        self.assertNotIn("last_mint", state, "the next tick tries again")
        self.assertIn("desktop kept off the token: installation unreadable (OSError)", logs)
        self.assertTrue(any("token not refreshed: LookupError" in l for l in logs))
        self.assertTrue(any("UNSTUCK" in l for l in logs), "the sweep still ran after the failed mint")
        # Control: once the API answers, the next tick mints.
        tick(pc=pc, state=state, minter=FakeMinter())
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])

    def test_no_covered_repository_mints_nothing(self):
        pc = FakePC()
        _, _, logs = tick(pc=pc, minter=FakeMinter({}))
        self.assertEqual(pc.secrets, [])
        self.assertTrue(any("token not refreshed: LookupError" in l for l in logs))

    def test_access_json_is_not_read_as_a_request(self):
        warden.write_access(self.spool, {"desktop": True}, False, 0)
        logs = []
        self.assertEqual(warden.read_spool(self.spool, logs.append), [])
        self.assertTrue(os.path.exists(os.path.join(self.spool, "access.json")))
        self.assertEqual(logs, [])


def mobile_merged(n=5, sha="mm1", head="mh1"):
    return {"number": n, "merge_commit_sha": sha, "merged_at": "2026-11-26T10:00:00Z",
            "head": {"sha": head}}


MOBILE_QA = "QA at mh1: relay, contracts and tools green."
GREEN3 = {"relay": "success", "contracts": "success", "tools": "success"}


class MobileAuditTickTests(unittest.TestCase):
    def world(self, merged=(), comments=None, checks=None, files=None, desktop=None):
        self.mob = FakeGH(merged=merged, comments=comments or {}, checks=checks or {}, files=files or {})
        return {"desktop": desktop or FakeGH(), "mobile": self.mob}

    def test_a_clean_mobile_merge_is_audited_and_retired_after_the_quiet_window(self):
        gh = self.world([mobile_merged()], {5: [MOBILE_QA]}, {"mh1": GREEN3},
                        {5: [{"filename": "relay/Tests/HubTests.cs", "status": "modified", "patch": "+x"}]})
        state = {}
        _, paused, _ = tick(gh=gh, state=state, cfg=TWO)
        self.assertFalse(paused)
        mob = state["repos"]["mobile"]
        self.assertIn("mm1", mob["audit_pending"], "kept pending for push-triggered runs")
        self.assertNotIn("mm1", state.get("audit_pending", {}), "desktop's state is apart")
        _, paused, _ = tick(gh=gh, state=state, cfg=TWO, now=10_000.0 + warden.MERGE_QUIET)
        self.assertFalse(paused)
        self.assertIn("mm1", mob["audited"])
        self.assertEqual(mob["audit_pending"], {})

    def test_no_qa_comment_pauses_naming_the_mobile_pr(self):
        gh = self.world([mobile_merged()], {5: ["looks fine"]}, {"mh1": GREEN3})
        _, paused, logs = tick(gh=gh, cfg=TWO)
        self.assertTrue(paused)
        self.assertIn("PAUSED: mobile PR 5: bot PR merged with no QA comment naming relay and contracts and tools", logs)

    def test_a_red_head_check_pauses_and_a_missing_path_check_too(self):
        gh = self.world([mobile_merged()], {5: [MOBILE_QA]}, {"mh1": dict(GREEN3, relay="failure")})
        _, paused, logs = tick(gh=gh, cfg=TWO)
        self.assertTrue(paused)
        self.assertTrue(any("relay failure" in l for l in logs))
        gh = self.world([mobile_merged()], {5: [MOBILE_QA]}, {"mh1": GREEN3},
                        {5: [{"filename": "android/app/src/A.kt", "status": "modified", "patch": "+x"}]})
        _, paused, logs = tick(gh=gh, cfg=TWO)
        self.assertTrue(paused)
        self.assertTrue(any("android missing" in l for l in logs))

    def test_a_red_check_on_the_merge_commit_pauses(self):
        gh = self.world([mobile_merged()], {5: [MOBILE_QA]}, {"mh1": GREEN3})
        state = {}
        tick(gh=gh, state=state, cfg=TWO)
        self.mob.checks["mm1"] = {"relay-image": "failure"}
        _, paused, logs = tick(gh=gh, state=state, cfg=TWO, now=10_300.0)
        self.assertTrue(paused)
        self.assertIn("PAUSED: mobile PR 5: develop CI red on a bot merge: relay-image", logs)
        self.assertEqual(self.mob.reran, [], "mobile has no push CI to rerun")

    def test_a_mobile_merge_breaks_the_release_streak(self):
        gh = self.world([mobile_merged()], {5: [MOBILE_QA]}, {"mh1": GREEN3})
        state = {"consecutive_releases": 1, "paperclip_releases": 1}
        tick(gh=gh, state=state, cfg=TWO)
        self.assertEqual((state["consecutive_releases"], state["paperclip_releases"]), (0, 0))

    def test_mobile_unreadable_three_ticks_pauses_on_its_own_counter(self):
        class Down(FakeGH):
            def merged_bot_prs(self):
                raise OSError("down")
        gh = {"desktop": FakeGH(), "mobile": Down()}
        state = {}
        for i in range(2):
            _, paused, _ = tick(gh=gh, state=state, cfg=TWO)
            self.assertFalse(paused)
        _, paused, logs = tick(gh=gh, state=state, cfg=TWO)
        self.assertTrue(paused)
        self.assertIn("PAUSED: merge audit unreadable (mobile) 3 ticks in a row", logs)
        self.assertEqual(state.get("audit_failures"), 0, "desktop's audit kept succeeding")

    def test_old_config_never_touches_a_mobile_seam(self):
        class Untouchable:
            def __getattr__(self, name):
                raise AssertionError("mobile seam used without mobile configured: " + name)
        _, paused, _ = tick(gh={"desktop": FakeGH(), "mobile": Untouchable()}, cfg={})
        self.assertFalse(paused)


class GuardTickTests(unittest.TestCase):
    def open_pr(self, n=8, sha="h1"):
        return {"number": n, "head": {"sha": sha}}

    def test_an_open_pr_touching_release_yml_pauses_once_per_head(self):
        gh = FakeGH(open_prs=[self.open_pr()], files={8: REAL_FILES[381]})
        state = {}
        _, paused, logs = tick(gh=gh, state=state)
        self.assertTrue(paused)
        self.assertTrue(any(l.startswith("PAUSED: PR 8: workflow guard: .github/workflows/release.yml") for l in logs))
        pc = FakePC()
        _, paused, _ = tick(gh=gh, state=state, pc=pc)
        self.assertEqual(gh.files_read, [8], "the same head is read once")
        self.assertFalse(pc.pauses, "and not paused again after a person resumes")
        gh.open_prs = [self.open_pr(sha="h2")]
        tick(gh=gh, state=state)
        self.assertEqual(gh.files_read, [8, 8], "a new head is read again")

    def test_an_open_pr_with_a_benign_workflow_edit_is_left_alone(self):
        gh = FakeGH(open_prs=[self.open_pr()], files={8: REAL_FILES[397]})
        _, paused, _ = tick(gh=gh)
        self.assertFalse(paused)
        self.assertEqual(gh.files_read, [8])

    def test_a_merged_head_the_guard_never_saw_is_read_at_the_audit(self):
        pr = dict(merged_pr(), head={"sha": "h9"})
        gh = FakeGH(merged=[pr], comments={7: ["osx-arm64 win-x64"]}, files={7: REAL_FILES[370]})
        _, paused, logs = tick(gh=gh)
        self.assertTrue(paused)
        self.assertTrue(any("PR 7: workflow guard: .github/workflows/stress.yml: workflow file added" in l for l in logs))
        # Control: a head already guarded while open is not read again at merge.
        gh = FakeGH(merged=[pr], comments={7: ["osx-arm64 win-x64"]}, files={7: REAL_FILES[370]})
        _, paused, _ = tick(gh=gh, state={"guarded": ["h9"]})
        self.assertEqual(gh.files_read, [])
        self.assertFalse(paused)

    def test_a_mobile_open_pr_touching_any_workflow_pauses(self):
        mob = FakeGH(open_prs=[self.open_pr(3, "mh")], files={3: [wf("relay-tests.yml", "+# x")]})
        _, paused, logs = tick(gh={"desktop": FakeGH(), "mobile": mob}, cfg=TWO)
        self.assertTrue(paused)
        self.assertTrue(any("mobile PR 3: workflow guard" in l and "may not edit workflows" in l for l in logs))

    def test_unreadable_files_count_as_an_audit_failure_not_a_pass(self):
        gh = FakeGH(open_prs=[self.open_pr()], files={8: UNREADABLE})
        state = {}
        _, paused, logs = tick(gh=gh, state=state)
        self.assertFalse(paused)
        self.assertEqual(state["audit_failures"], 1)
        self.assertNotIn("h1", state.get("guarded", []), "retried next tick")


class RepoRerunTests(unittest.TestCase):
    def mob_run(self, wf_, conclusion, run_id, sha="m1", status="completed"):
        return {"id": run_id, "path": ".github/workflows/" + wf_, "head_sha": sha,
                "status": status, "conclusion": conclusion}

    def test_mobile_rerun_reruns_each_red_workflow_once_per_sha_in_its_own_state(self):
        mob = FakeGH(prs={5: mobile_pr()}, runs={"m1": [self.mob_run("relay-tests.yml", "failure", 1),
                                                        self.mob_run("android-tests.yml", "success", 2),
                                                        self.mob_run("ios-snapshots.yml", "timed_out", 3)]})
        gh, state = {"desktop": FakeGH(), "mobile": mob}, {}
        req = {"verb": "rerun_failed", "pr": 5, "repo": "mobile"}
        _, paused, logs = tick(gh=gh, state=state, cfg=TWO, requests=[req])
        self.assertFalse(paused)
        self.assertEqual(mob.reran, [1, 3])
        self.assertEqual(state["repos"]["mobile"]["reruns"], {"m1": 1})
        self.assertNotIn("reruns", state, "desktop's reruns are apart")
        tick(gh=gh, state=state, cfg=TWO, requests=[req])
        self.assertEqual(mob.reran, [1, 3], "once per sha")

    def test_desktop_pr_number_is_not_rerun_on_mobile(self):
        mob = FakeGH(prs={5: good_pr(head={"ref": "bugfix/pc-x", "repo": {"full_name": warden.REPO}, "sha": "m1"})},
                     runs={"m1": [self.mob_run("relay-tests.yml", "failure", 1)]})
        _, _, logs = tick(gh={"desktop": FakeGH(), "mobile": mob}, cfg=TWO,
                          requests=[{"verb": "rerun_failed", "pr": 5, "repo": "mobile"}])
        self.assertEqual(mob.reran, [])
        self.assertTrue(any("refused rerun for mobile PR 5: PR head is not in this repository" in l for l in logs))

    def test_unknown_or_unconfigured_repo_is_refused(self):
        req = {"verb": "rerun_failed", "pr": 5, "repo": "mobile"}
        _, paused, logs = tick(requests=[req])
        self.assertFalse(paused)
        self.assertTrue(any("unknown repo 'mobile'" in l for l in logs), "mobile is not configured here")

    def test_no_stress_dispatch_on_mobile(self):
        mob = FakeGH(prs={5: mobile_pr()})
        req = {"verb": "dispatch_stress", "pr": 5, "test_class": "A.B", "n": 3, "suite": "unit", "repo": "mobile"}
        _, _, logs = tick(gh={"desktop": FakeGH(), "mobile": mob}, cfg=TWO, requests=[req])
        self.assertEqual(mob.dispatched, [])
        self.assertTrue(any("mobile has no stress workflow" in l for l in logs))

    def test_rerun_targets_waits_for_a_running_run_and_refuses_all_green(self):
        runs = [self.mob_run("relay-tests.yml", "success", 1), self.mob_run("ios-snapshots.yml", None, 2, status="in_progress")]
        self.assertEqual(warden.rerun_targets(mobile_pr(), runs, {}, warden.MOBILE), ([], "CI is still running"))
        runs = [self.mob_run("relay-tests.yml", "success", 1)]
        self.assertIn("not red", warden.rerun_targets(mobile_pr(), runs, {}, warden.MOBILE)[1])


# ---------------------------------------------------------------- pool usage (CB-468)

FIXTURE = os.path.join(HERE, "fixtures", "claude-get-usage.jsonl")


def fixture_stream():
    with open(FIXTURE, encoding="utf-8") as f:
        return f.read()


class PoolUsageParseTests(unittest.TestCase):
    """host/pool_usage.py turns `claude -p` get_usage output into Paperclip-shaped limits."""

    def test_the_real_reply_gives_both_account_windows(self):
        # Captured from a real account on the mini; behaviors and session dropped.
        r = pool_usage.reading("noise\n{not json}\n" + fixture_stream(), "2026-11-25T04:25:00Z")
        self.assertEqual(r["status"], "ok")
        by = {l["id"]: l for l in r["limits"]}
        self.assertEqual(sorted(by), ["five_hour", "seven_day"], "null model windows are left out")
        self.assertEqual((by["five_hour"]["usedPercent"], by["seven_day"]["usedPercent"]), (24, 80))
        self.assertIsNone(by["seven_day"]["scope"])
        self.assertTrue(by["seven_day"]["resetsAt"].startswith("2026-11-26T04:00"))
        self.assertIsNone(by["five_hour"]["limitReached"])

    def payload(self, **rl):
        return json.dumps({"type": "control_response", "response": {"subtype": "success", "request_id": "cb-usage",
                           "response": {"rate_limits_available": True, "rate_limits": rl}}})

    def test_model_windows_and_locks(self):
        out = self.payload(seven_day_opus={"utilization": 97, "resets_at": "x", "locked_reason": "limit"},
                           five_hour={"utilization": True})
        by = {l["id"]: l for l in pool_usage.reading(out, "t")["limits"]}
        self.assertEqual(by["seven_day_opus"]["scope"], "seven_day_opus")
        self.assertTrue(by["seven_day_opus"]["limitReached"])
        self.assertIsNone(by["five_hour"]["usedPercent"], "a bool is not a percentage")

    def test_anything_unreadable_is_an_error_never_zero(self):
        bad = ["", None, "{}", '{"type":"control_response","response":{"subtype":"error","error":"x"}}',
               json.dumps({"type": "control_response", "response": {"subtype": "success",
                           "response": {"rate_limits_available": False, "rate_limits": {}}}}),
               self.payload()]
        for out in bad:
            with self.subTest(out=out):
                self.assertEqual(pool_usage.reading(out, "t"), {"status": "error", "checkedAt": "t", "limits": []})

    def test_the_default_login_removes_the_config_dir_variable(self):
        env = pool_usage.usage_env(None, {"CLAUDE_CONFIG_DIR": "/x", "PATH": "/p"})
        self.assertNotIn("CLAUDE_CONFIG_DIR", env)
        env = pool_usage.usage_env("~/.claude-board", {"PATH": "/p"})
        self.assertEqual(env["CLAUDE_CONFIG_DIR"], os.path.expanduser("~/.claude-board"))

    def test_summary_names_the_worst_window(self):
        doc = {"accounts": {"aaaaaaaa-1": pool_usage.reading(fixture_stream(), "t"),
                            "bbbbbbbb-2": {"status": "error", "limits": []}}}
        self.assertEqual(pool_usage.summary(doc), "aaaaaaaa 80%; bbbbbbbb unreadable")


class PoolUsagePublishTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.out = os.path.join(self.d, "inst", "pool-usage.json")

    def tearDown(self):
        import shutil
        shutil.rmtree(self.d, ignore_errors=True)

    def cfg(self, accounts):
        return {"output": self.out, "accounts": accounts}

    def test_each_account_is_published_and_a_failure_is_marked(self):
        seen = []

        def run(config_dir):
            seen.append(config_dir)
            return fixture_stream() if config_dir == "~/.claude-board" else ""

        doc = pool_usage.publish(self.cfg([{"connectionId": "c-board", "configDir": "~/.claude-board"},
                                           {"connectionId": "c-default", "configDir": None},
                                           {"configDir": "~/.claude-nobody"}]), run=run, clock=lambda: "T")
        self.assertEqual(seen, ["~/.claude-board", None], "an entry with no connection id is skipped")
        with open(self.out) as f:
            self.assertEqual(json.load(f), doc)
        self.assertEqual(doc["accounts"]["c-board"]["status"], "ok")
        self.assertEqual(doc["accounts"]["c-default"], {"status": "error", "checkedAt": "T", "limits": []})
        self.assertEqual(doc["version"], 1)
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(os.stat(self.out).st_mode), 0o600)
        self.assertEqual([n for n in os.listdir(os.path.dirname(self.out)) if n.startswith(".pool-usage.")], [],
                         "no temp file left behind")

    def test_a_failed_write_leaves_the_old_file_and_no_temp(self):
        os.makedirs(os.path.dirname(self.out))
        with open(self.out, "w") as f:
            f.write("old")

        def boom(*a, **k):
            raise OSError("disk full")

        real = pool_usage.json.dump
        pool_usage.json.dump = boom
        try:
            with self.assertRaises(OSError):
                pool_usage.publish(self.cfg([{"connectionId": "c", "configDir": None}]), run=lambda d: "")
        finally:
            pool_usage.json.dump = real
        with open(self.out) as f:
            self.assertEqual(f.read(), "old")
        self.assertEqual(sorted(os.listdir(os.path.dirname(self.out))), ["pool-usage.json"])

    def test_a_missing_claude_binary_reads_as_unreadable(self):
        old = os.environ.get("PATH")
        os.environ["PATH"] = self.d
        try:
            self.assertEqual(pool_usage.run_claude(None), "")
        finally:
            os.environ["PATH"] = old

    @unittest.skipIf(os.name == "nt", "a shebang stub needs a POSIX shell")
    def test_run_once_against_a_stub_claude(self):
        # The real contract with the CLI: argv, stdin and CLAUDE_CONFIG_DIR.
        bindir = os.path.join(self.d, "bin")
        os.makedirs(bindir)
        log = os.path.join(self.d, "claude.log")
        stub = os.path.join(bindir, "claude")
        with open(stub, "w") as f:
            f.write("#!/usr/bin/env python3\nimport json, os, sys\n"
                    f"open({log!r}, 'a').write(json.dumps([sys.argv[1:], os.environ.get('CLAUDE_CONFIG_DIR'), sys.stdin.read()]) + '\\n')\n"
                    f"sys.stdout.write(open({FIXTURE!r}).read())\n")
        os.chmod(stub, 0o755)
        cfg_path = os.path.join(self.d, "pool-accounts.json")
        with open(cfg_path, "w") as f:
            json.dump(self.cfg([{"connectionId": "c1", "configDir": "~/.claude-loginid"},
                                {"connectionId": "c2", "configDir": None}]), f)
        env = dict(os.environ, PATH=bindir + os.pathsep + os.environ.get("PATH", ""), CLAUDE_CONFIG_DIR="/leak")
        p = subprocess.run([sys.executable, os.path.join(ROOT, "host", "pool_usage.py"), "run-once", "--config", cfg_path],
                           capture_output=True, text=True, env=env, timeout=60)
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertRegex(p.stdout, r"c1 80%; c2 80%")
        calls = [json.loads(l) for l in open(log)]
        self.assertEqual(calls[0][0], ["-p", "--verbose", "--no-session-persistence", "--settings",
                                       '{"disableAllHooks":true}', "--input-format", "stream-json",
                                       "--output-format", "stream-json"])
        self.assertEqual(calls[0][1], os.path.expanduser("~/.claude-loginid"))
        self.assertIsNone(calls[1][1], "the default login must not inherit CLAUDE_CONFIG_DIR")
        self.assertIn('"subtype":"get_usage"', calls[0][2])
        with open(self.out) as f:
            self.assertEqual(sorted(json.load(f)["accounts"]), ["c1", "c2"])

    def test_the_launchd_job_runs_it_with_claude_on_path(self):
        with open(os.path.join(ROOT, "host", "launchd", "org.uplift.orbweaver-bugfix-pool-usage.plist"), "rb") as f:
            pl = plistlib.load(f)
        self.assertEqual(pl["ProgramArguments"][1:], ["__REPO__/host/pool_usage.py", "run-once"])
        self.assertIn("/opt/homebrew/bin", pl["EnvironmentVariables"]["PATH"].split(":"))
        self.assertEqual(pl["StartInterval"], 300)


# ---------------------------------------------------------------- answered leaves (CB-487)

def C(agent, body, at, **over):
    """A comment row as /api/issues/<id>/comments returns it (measured on QAF-194):
    an agent's carries authorAgentId; Paperclip's own notice and a board
    comment carry none."""
    row = {"authorAgentId": agent, "body": body, "createdAt": warden.iso(at)}
    row.update(over)
    return row


ANSWER_427 = ("ANSWER CB-478: I reviewed PR 427 at sha 70024baa and the verdict is merge. "
              "Both required checks pass at that sha.")
VALIDATION = ("Paperclip stopped before launching the local adapter because the issue workspace "
              "failed validation. Moving it to `blocked` so the workspace link, cwd, or git "
              "checkout can be repaired before resuming.")


def BOTPR(state="closed", merged_at="2026-11-27T00:10:00Z", **over):
    """PR 427 as GitHub returns it: the org's bot, a bugfix/pc- head."""
    row = {"state": state, "merged_at": merged_at, "user": {"login": warden.BOT_LOGIN},
           "head": {"ref": "bugfix/pc-make-bare-gh-calls-use-the-app-token"}}
    row.update(over)
    return row


def qaf194_thread():
    """QAF-194's order: blocked by validation at 22:58:28, ANSWER at 22:59:59,
    then the medic's board wake and Paperclip's re-block, neither by an agent."""
    return [C(None, VALIDATION, NOW - 4 * 3600 - 90),
            C("qa", ANSWER_427, NOW - 4 * 3600),
            C(None, "Medic: the org was stopped and has been restarted ...", NOW - 3600,
              authorUserId="board"),
            C(None, VALIDATION, NOW - 3500)]


class AnswerHelperTests(unittest.TestCase):
    def test_last_agent_answer_skips_paperclip_and_board_comments(self):
        self.assertEqual(warden.last_agent_answer(qaf194_thread(), "qa"), ANSWER_427)

    def test_a_newer_agent_comment_is_not_an_answer(self):
        rows = qaf194_thread() + [C("qa", "Still checking the Windows leg.", NOW - 60)]
        self.assertIsNone(warden.last_agent_answer(rows, "qa"))

    def test_another_agents_answer_is_not_the_assignees(self):
        self.assertIsNone(warden.last_agent_answer([C("eng", ANSWER_427, NOW)], "qa"))
        self.assertIsNone(warden.last_agent_answer([C("qa", ANSWER_427, NOW)], None))

    def test_no_answer_prefix_and_deleted_answers_do_not_count(self):
        self.assertIsNone(warden.last_agent_answer([C("qa", "Verdict: merge", NOW)], "qa"))
        rows = [C("qa", "working", NOW - 60), C("qa", ANSWER_427, NOW, deletedAt=warden.iso(NOW))]
        self.assertIsNone(warden.last_agent_answer(rows, "qa"))
        self.assertIsNone(warden.last_agent_answer([], "qa"))

    def test_answer_pr(self):
        self.assertEqual(warden.answer_pr(ANSWER_427), 427)
        self.assertEqual(warden.answer_pr("ANSWER: PR #428 merge"), 428)
        self.assertIsNone(warden.answer_pr("ANSWER: no change needed"))
        self.assertIsNone(warden.answer_pr(None))

    def test_pr_over(self):
        self.assertEqual(warden.pr_over(BOTPR()), "merged")
        self.assertEqual(warden.pr_over(BOTPR(merged_at=None, merged=True)), "merged")
        self.assertEqual(warden.pr_over(BOTPR(merged_at=None)), "closed")
        self.assertIsNone(warden.pr_over(BOTPR("open", None)))
        self.assertIsNone(warden.pr_over(None))

    def test_a_pr_the_org_did_not_open_is_no_proof(self):
        self.assertIsNone(warden.pr_over(BOTPR(user={"login": "wtvamp"})))
        self.assertIsNone(warden.pr_over(BOTPR(head={"ref": "feature/persist-orb-positions"})))
        self.assertIsNone(warden.pr_over(BOTPR(user=None, head=None)))

    def test_answer_pr_needs_exactly_one_desktop_pr(self):
        self.assertIsNone(warden.answer_pr("ANSWER: PR 427 merge; PR 428 still open"), "two PRs")
        self.assertEqual(warden.answer_pr("ANSWER: PR 427 merge, same as PR #427 above"), 427)
        self.assertIsNone(warden.answer_pr("ANSWER: Orbweaver-Mobile PR 12 merge"), "mobile")
        self.assertIsNone(warden.answer_pr("ANSWER: the mobile relay PR 12 is fine"), "mobile")

    def test_a_board_comment_starting_answer_does_not_count(self):
        rows = [C(None, "ANSWER: closing this by hand", NOW, authorUserId="board")]
        self.assertIsNone(warden.last_agent_answer(rows, "qa"))
        rows = [C("warden", "ANSWER CB-1: quoting the qa answer", NOW)]
        self.assertIsNone(warden.last_agent_answer(rows, "qa"))

    def test_a_tickets_engineer_issue_closes_only_on_a_merge(self):
        self.assertIsNone(warden.answered_and_over("ANSWER", "done", None, ticket=True), "parent proof")
        self.assertIsNone(warden.answered_and_over("ANSWER", "done", "closed", ticket=True), "a release")
        self.assertIn("merged", warden.answered_and_over("ANSWER", None, "merged", ticket=True))
        self.assertTrue(warden.is_ticket({"description": "Ticket: CB-478\nFix it"}))
        self.assertFalse(warden.is_ticket({"description": "Review PR 427"}))
        self.assertFalse(warden.is_ticket({"description": None}))
        self.assertTrue(warden.is_ticket({}), "no description key: treated as a ticket")

    def test_answered_and_over(self):
        self.assertIn("parent is done", warden.answered_and_over("ANSWER", "done", None))
        self.assertIn("parent is cancelled", warden.answered_and_over("ANSWER", "cancelled", None))
        self.assertIn("PR is merged", warden.answered_and_over("ANSWER", "blocked", "merged"))
        self.assertIn("PR is closed", warden.answered_and_over("ANSWER", None, "closed"))
        self.assertIsNone(warden.answered_and_over("ANSWER", "blocked", None), "work not over")
        self.assertIsNone(warden.answered_and_over(None, "done", "merged"), "no answer")


def qaf_night():
    """Parent pass P blocked on review leaf r, both untouched for hours."""
    return [I("P", "blocked", idle=4 * 3600), I("r", "blocked", "P", idle=3500, assignee="qa")]


class UnstickAnsweredTests(unittest.TestCase):
    def test_answered_leaf_with_merged_pr_is_closed(self):
        acts = decide_answers(qaf_night(), {"r": (ANSWER_427, "merged")})
        self.assertIn(("close_answered", "r"), acts)

    def test_control_before_cb487_the_leaf_fell_through(self):
        # The same world without the answer: no rule touches the blocked leaf,
        # and the parent is not closed because its child is not settled.
        self.assertEqual(decide(qaf_night()), [])

    def test_open_pr_and_open_parent_is_not_closed(self):
        # Not closed; since CB-492 its blocked parent is woken to read the answer instead.
        self.assertEqual(decide_answers(qaf_night(), {"r": (ANSWER_427, None)}),
                         [("wake_parent_for_answer", "P")])

    def test_settled_parent_is_proof_without_a_pr(self):
        issues = [I("P", "done"), I("r", "blocked", "P", assignee="qa", description="Review PR 427")]
        self.assertEqual(decide_answers(issues, {"r": ("ANSWER: nothing to change", None)}),
                         [("close_answered", "r")])

    def test_a_task_with_children_or_a_live_run_is_not_a_closable_leaf(self):
        issues = [I("P", "blocked"), I("c", "in_progress", "P", idle=60)]
        self.assertNotIn(("close_answered", "P"), decide_answers(issues, {"P": (ANSWER_427, "merged")}))
        live = [I("r", "blocked", "P", assignee="qa", activeRun={"id": "x"})]
        self.assertEqual(decide_answers(live, {"r": (ANSWER_427, "merged")}), [])

    def test_only_blocked_tasks(self):
        issues = [I("r", "in_progress", "P", idle=60, assignee="qa")]
        self.assertEqual(decide_answers(issues, {"r": (ANSWER_427, "merged")}), [])

    def test_answered_beats_a_fresh_usage_cancel(self):
        issues = [I("r", "blocked", "P", assignee="qa")]
        acts = decide_answers(issues, {"r": (ANSWER_427, "merged")}, runs=[EXHAUSTED("r", agent="qa")])
        self.assertEqual(acts, [("close_answered", "r")])

    def test_parent_closes_once_the_leaf_is_done(self):
        # The next ticks: the leaf done, the parent's own rule takes over.
        issues = [I("P", "blocked", idle=11 * 60), I("r", "done", "P", idle=11 * 60, assignee="qa")]
        self.assertEqual(decide(issues), [("wake_parent", "P")])
        self.assertEqual(decide(issues, state={"parent_woken": {"P": NOW - 15 * 60}}),
                         [("close_parent", "P")])


def decide_answers(issues, answers, runs=()):
    return [(a["kind"], a["id"]) for a in
            warden.unstick_actions(list(issues), list(runs), {}, {}, NOW, True, answers)]


class CountingGH(FakeGH):
    def __init__(self, **kw):
        super().__init__(**kw)
        self.asked = []

    def get_pr(self, n):
        self.asked.append(n)
        return super().get_pr(n)


class UnstickAnsweredTickTests(unittest.TestCase):
    def run_tick(self, issues, threads, prs=None, gh=None):
        pc = FakePC()
        pc.all, pc.pool, pc.threads = issues, POOL_OK, threads
        gh = gh or CountingGH(prs=prs or {})
        _, _, logs = tick(gh=gh, pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        return pc, gh, logs

    def test_qaf194_is_closed_with_no_comment_and_logged_with_its_proof(self):
        pc, gh, logs = self.run_tick(qaf_night(), {"r": qaf194_thread()},
                                     prs={427: BOTPR()})
        self.assertIn(("r", "done"), pc.statuses)
        self.assertNotIn("r", [c[0] for c in pc.comments], "a comment would wake it")
        self.assertIn("UNSTUCK: close_answered QAF-r (answered and its PR is merged)", logs)
        self.assertEqual(gh.asked, [427])

    def test_open_pr_leaves_it_alone(self):
        pc, _, logs = self.run_tick(qaf_night(), {"r": qaf194_thread()},
                                    prs={427: BOTPR("open", None)})
        self.assertEqual(pc.statuses, [])
        self.assertFalse(any("close_answered" in l for l in logs))

    def test_settled_parent_does_not_ask_github(self):
        issues = [I("P", "done"), I("r", "blocked", "P", assignee="qa", description="Review PR 427")]
        pc, gh, _ = self.run_tick(issues, {"r": qaf194_thread()})
        self.assertEqual(pc.statuses, [("r", "done")])
        self.assertEqual(gh.asked, [])

    def test_unreadable_pr_is_no_proof_and_the_sweep_goes_on(self):
        class Broken(CountingGH):
            def get_pr(self, n):
                raise OSError("gh down")
        issues = qaf_night() + [I("x", "blocked", idle=11 * 60), I("y", "done", "x", idle=12 * 60)]
        pc, _, logs = self.run_tick(issues, {"r": qaf194_thread()}, gh=Broken())
        self.assertNotIn(("r", "done"), pc.statuses)
        self.assertTrue(any("unstick: PR 427 unreadable for QAF-r: OSError" in l for l in logs))
        self.assertIn("UNSTUCK: wake_parent QAF-x", logs, "the rest of the sweep still ran")

    def test_engineer_issue_with_a_closed_unmerged_pr_is_not_set_done(self):
        issues = [I("P", "done"), I("e", "blocked", "P", assignee="qa", description="Ticket: CB-478\nx")]
        pc, gh, logs = self.run_tick(issues, {"e": [C("qa", ANSWER_427, NOW - 60)]},
                                     prs={427: BOTPR(merged_at=None)})
        self.assertEqual(pc.statuses, [])
        self.assertEqual(gh.asked, [427], "a ticket is read even under a settled parent")
        pc, _, _ = self.run_tick(issues, {"e": [C("qa", ANSWER_427, NOW - 60)]}, prs={427: BOTPR()})
        self.assertEqual(pc.statuses, [("e", "done")], "control: merged closes it")

    def test_a_parent_missing_from_the_listing_is_no_proof(self):
        issues = [I("r", "blocked", "gone", assignee="qa", description="Review PR 427")]
        pc, _, _ = self.run_tick(issues, {"r": [C("qa", "ANSWER: nothing to change", NOW - 60)]})
        self.assertEqual(pc.statuses, [])

    def test_a_gh_timeout_closes_nothing_and_the_tick_finishes(self):
        class Slow(CountingGH):
            def get_pr(self, n):
                raise subprocess.TimeoutExpired(["gh"], 30)
        pc, _, logs = self.run_tick(qaf_night(), {"r": qaf194_thread()}, gh=Slow())
        self.assertEqual(pc.statuses, [])
        self.assertTrue(any("PR 427 unreadable for QAF-r: TimeoutExpired" in l for l in logs))

    def test_comments_are_read_only_for_blocked_leaves(self):
        class Watching(FakePC):
            read = []
            def issue_comments(self, issue_id):
                self.read.append(issue_id)
                return []
        pc = Watching()
        pc.all, pc.pool = qaf_night() + [I("t", "todo", "P", assignee="qa"), I("d", "done", "P")], POOL_OK
        tick(gh=CountingGH(), pc=pc, state={}, now=NOW, cfg={"company_id": "co"})
        self.assertEqual(pc.read, ["r"])

@unittest.skipIf(os.name != "posix", "the token file needs O_NOFOLLOW, dir_fd and a uid; the host is a Mac")
class TokenNeverExpiresMidRunTests(unittest.TestCase):
    """CB-489: QAF-190 and QAF-209 started with dead tokens, because the
    warden did not mint while paused and, on a resume, restarted the stopped
    work before it minted. Each mint now also lands in bugfix-tokens/, beside
    the spool and never in it, for the
    gh and git shims to read at every call."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(self.root, True))
        self.spool = os.path.join(self.root, "bugfix-spool")
        self.tokens = os.path.join(self.root, "bugfix-tokens")
        os.mkdir(self.spool)

    def token_file(self, name):
        return os.path.join(self.tokens, name)

    def test_a_resume_mints_before_it_restarts_the_stopped_work(self):
        events = []

        class Ordered(FakePC):
            def put_secret(self, name, value):
                events.append(("secret", name))
                super().put_secret(name, value)

            def set_issue_status(self, issue_id, status):
                events.append(("restart", issue_id))
                super().set_issue_status(issue_id, status)

        state, pc = {}, Ordered(issues=["iss-aaaaaaaa"])
        tick(pc=pc, state=state, now=0, requests=[{"verb": "push_refused"}])
        pc.paused, pc.issues = False, []
        pc.full = {"iss-aaaaaaaa": {"id": "iss-aaaaaaaa", "status": "blocked"}}
        events.clear()
        tick(pc=pc, state=state, now=warden.MINT_INTERVAL)
        self.assertEqual(events, [("secret", "GITHUB_TOKEN"), ("restart", "iss-aaaaaaaa")])

    def test_the_everyday_token_is_written_to_a_file_only_the_owner_reads(self):
        pc, m = FakePC(), FakeMinter({warden.REPO: WF_INSTALL})
        tick(pc=pc, minter=m, cfg={"spool": self.spool})
        self.assertEqual([n for n, _ in pc.secrets], ["GITHUB_TOKEN", "GITHUB_TOKEN_WORKFLOWS"])
        self.assertEqual(open(self.token_file("gh-token")).read(), "tok1\n")
        self.assertEqual(stat.S_IMODE(os.stat(self.token_file("gh-token")).st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(os.stat(self.tokens).st_mode), 0o700)
        self.assertEqual(sorted(os.listdir(self.spool)), ["access.json", "pauses.json"],
                         "nothing token-shaped in the spool agents write to")

    def test_the_token_folder_never_holds_the_workflow_token(self):
        # Lead's ruling (CB-489): every run, the lead included, can read the
        # folder, and CB-486 keeps the workflow token from the lead.
        pc, m = FakePC(), FakeMinter({warden.REPO: WF_INSTALL})
        state = {}
        for now in (0, warden.MINT_INTERVAL):
            tick(pc=pc, minter=m, state=state, now=now, cfg={"spool": self.spool})
        workflow = [v for n, v in pc.secrets if n == "GITHUB_TOKEN_WORKFLOWS"]
        self.assertEqual(len(workflow), 2, "the workflow secret is still minted")
        self.assertEqual(os.listdir(self.tokens), ["gh-token"], "no .tmp, no workflow file")
        held = open(self.token_file("gh-token")).read()
        self.assertNotIn(held.strip(), workflow)

    def test_the_file_moves_on_every_mint_paused_or_not(self):
        state, pc, m = {}, FakePC(paused=True), FakeMinter()
        tick(pc=pc, state=state, now=0, cfg={"spool": self.spool}, minter=m)
        tick(pc=pc, state=state, now=warden.MINT_INTERVAL, cfg={"spool": self.spool}, minter=m)
        self.assertEqual(open(self.token_file("gh-token")).read(), "tok2\n")

    def test_the_file_is_written_before_the_secret(self):
        seen = []
        spool = self.spool

        class Peeking(FakePC):
            def put_secret(self, name, value):
                seen.append(open(os.path.join(spool, "..", "bugfix-tokens", "gh-token")).read())
                super().put_secret(name, value)

        tick(pc=Peeking(), cfg={"spool": spool})
        self.assertEqual(seen, ["tok1\n"])

    def test_a_planted_symlink_is_replaced_not_followed(self):
        os.makedirs(self.tokens)
        victim = os.path.join(self.root, "victim")
        open(victim, "w").write("keep")
        os.symlink(victim, self.token_file("gh-token"))
        os.symlink(victim, self.token_file("gh-token") + ".tmp")
        tick(cfg={"spool": self.spool})
        self.assertEqual(open(victim).read(), "keep")
        self.assertFalse(os.path.islink(self.token_file("gh-token")))
        self.assertEqual(open(self.token_file("gh-token")).read(), "tok1\n")

    def test_a_planted_folder_symlink_is_refused_and_nothing_moves(self):
        # QA, CB-489: the company folder is writable from the sandbox, so an
        # agent can put a link where bugfix-tokens should be.
        target = os.path.join(self.root, "elsewhere")
        os.mkdir(target, 0o755)
        os.symlink(target, self.tokens)
        pc = FakePC()
        _, _, logs = tick(pc=pc, cfg={"spool": self.spool})
        self.assertEqual(os.listdir(target), [], "no token written through the link")
        self.assertEqual(stat.S_IMODE(os.stat(target).st_mode), 0o755, "the target was not chmodded")
        self.assertTrue(any(l.startswith("bugfix-tokens/gh-token not written: NotADirectoryError") for l in logs), logs)
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")], "the secret still rotates")

    def test_negative_control_a_real_folder_in_the_same_place_is_written(self):
        os.mkdir(self.tokens, 0o755)
        tick(cfg={"spool": self.spool})
        self.assertEqual(open(self.token_file("gh-token")).read(), "tok1\n")
        self.assertEqual(stat.S_IMODE(os.stat(self.tokens).st_mode), 0o700, "tightened")

    def test_a_folder_someone_else_owns_is_refused(self):
        os.mkdir(self.tokens)
        real_getuid = os.getuid
        warden.os.getuid = lambda: real_getuid() + 1
        self.addCleanup(setattr, warden.os, "getuid", real_getuid)
        _, _, logs = tick(cfg={"spool": self.spool})
        self.assertEqual(os.listdir(self.tokens), [])
        self.assertTrue(any("not written: PermissionError" in l for l in logs), logs)

    def test_an_unwritable_file_is_logged_and_the_secret_still_goes(self):
        open(self.tokens, "w").write("not a folder")
        pc = FakePC()
        _, _, logs = tick(pc=pc, cfg={"spool": self.spool})
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertTrue(any(l.startswith("bugfix-tokens/gh-token not written: ") for l in logs), logs)
        self.assertFalse(any("tok1" in l for l in logs), "the token is never logged")

    def test_the_folder_is_beside_the_spool_unless_named(self):
        self.assertEqual(warden.token_dir({"spool": self.spool + "/"}), self.tokens)
        self.assertEqual(warden.token_dir({"spool": self.spool, "token_dir": "/x"}), "/x")
        self.assertIsNone(warden.token_dir({}))
        named = os.path.join(self.root, "named")
        tick(cfg={"spool": self.spool, "token_dir": named})
        self.assertEqual(open(os.path.join(named, "gh-token")).read(), "tok1\n")

    def test_off_posix_the_secret_still_rotates_and_no_file_is_tried(self):
        # CI's win-x64 leg: write_token's calls do not exist there, and an
        # AttributeError once escaped publish and stopped the secret rotating.
        self.addCleanup(setattr, warden.os, "name", warden.os.name)
        warden.os.name = "nt"
        pc = FakePC()
        _, _, logs = tick(pc=pc, cfg={"spool": self.spool})
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertFalse(os.path.exists(self.tokens))
        self.assertFalse(any("not written" in l for l in logs))

    def test_no_spool_writes_no_file(self):
        pc = FakePC()
        _, _, logs = tick(pc=pc)
        self.assertEqual(pc.secrets, [("GITHUB_TOKEN", "tok1")])
        self.assertFalse(any("not written" in l for l in logs))

# ---------------------------------------------------------------- workflow merge (CB-486)

WF_SHA = "a00bc0cd3bebfff73fd6f157d7188d2ac011eedd"
# Trimmed from the real QA review on #429 at a00bc0cd3 (2026-11-26): the lines
# the check reads, in the order she wrote them.
QA_429 = """[bugfix-org] QA

Reviewed sha: a00bc0cd3bebfff73fd6f157d7188d2ac011eedd (re-resolved just before posting; still the PR head).

Suites: CI only (no local runs). Rids read: osx-arm64 and win-x64.

Workflow change: .github/workflows/ci.yml; no environment, secret, permission, trigger or runner line

Verdict: merge
"""
WF_FILES = [{"filename": ".github/workflows/ci.yml", "status": "modified",
             "patch": "@@ -1,2 +1,3 @@\n   - name: Integration\n+        # watchdog\n+        timeout 480 dotnet test\n"}]
GREEN = {n: "success" for n in warden.DESKTOP["post_merge_checks"]}


def qa_row(body=QA_429, login=BOT, at="2026-11-26T23:56:23Z"):
    return {"login": login, "body": body, "at": at}


class WorkflowMergeRuleTests(unittest.TestCase):
    def ok(self, **over):
        a = dict(pr=head_pr(WF_SHA), sha=WF_SHA, files=WF_FILES, reviews=[qa_row()],
                 checks=dict(GREEN), r=warden.DESKTOP)
        a.update(over)
        return warden.workflow_merge_problem(**a)

    def test_the_real_429_state_clears(self):
        self.assertIsNone(self.ok())

    def test_each_refusal_names_its_reason(self):
        mobile = dict(warden.MOBILE)
        cases = {
            "mobile": (dict(r=mobile), "does not allow workflow edits"),
            "closed": (dict(pr=head_pr(WF_SHA, state="closed")), "not open"),
            "not the bot's": (dict(pr=head_pr(WF_SHA, user={"login": "wtvamp"})), "not opened by the bug-fix App"),
            "fork": (dict(pr=head_pr(WF_SHA, head={"ref": "bugfix/pc-x", "repo": {"full_name": "evil/Orbweaver"}, "sha": WF_SHA})), "not in this repository"),
            "branch": (dict(pr=head_pr(WF_SHA, head={"ref": "feature/x", "repo": {"full_name": warden.REPO}, "sha": WF_SHA})), "prefix"),
            "base": (dict(pr=head_pr(WF_SHA, base={"ref": "main"})), "does not target develop"),
            "moved head": (dict(pr=head_pr("b" * 40)), "not the requested"),
            "file cap": (dict(files=WF_FILES + [{"filename": f"f{i}.cs", "status": "modified", "patch": "+x"}
                                                 for i in range(warden.PR_FILES_CAP - 1)]), "3000-file cap"),
            "no workflow": (dict(files=[{"filename": "App.cs", "status": "modified", "patch": "+x"}]), "changes no workflow file"),
            "guard": (dict(files=[dict(WF_FILES[0], patch="+    runs-on: self-hosted\n")]), "workflow guard"),
            "protected": (dict(files=[{"filename": ".github/workflows/release.yml", "status": "modified", "patch": "+# x"}]), "protected workflow"),
            "red check": (dict(checks=dict(GREEN, **{warden.REQUIRED_CHECKS[1]: "failure"})), "not green"),
            "pending check": (dict(checks={warden.REQUIRED_CHECKS[0]: "success"}), "pending"),
            "no QA": (dict(reviews=[]), "no QA comment"),
            "QA by a person": (dict(reviews=[qa_row(login="wtvamp")]), "no QA comment"),
            "not a QA comment": (dict(reviews=[qa_row(body="Verdict: merge at a00bc0cd3")]), "no QA comment"),
            "fix verdict": (dict(reviews=[qa_row(QA_429.replace("Verdict: merge", "Verdict: fix\n- x"))]), "does not end `Verdict: merge`"),
            "later fix wins": (dict(reviews=[qa_row(), qa_row(QA_429.replace("Verdict: merge", "Verdict: fix"), at="2026-11-27T00:10:00Z")]), "does not end"),
            "other sha": (dict(reviews=[qa_row(QA_429.replace(WF_SHA, "c" * 40))]), "does not name a00bc0cd3"),
            "later QA at another sha": (dict(reviews=[qa_row(), qa_row(QA_429.replace(WF_SHA, "c" * 40), at="2026-11-27T01:00:00Z")]), "does not name a00bc0cd3"),
            "7-char sha": (dict(reviews=[qa_row(QA_429.replace(WF_SHA, "a00bc0c"))]), "does not name a00bc0cd3"),
            "skipped check": (dict(checks=dict(GREEN, **{warden.REQUIRED_CHECKS[0]: "skipped"})), "not green"),
            "neutral check": (dict(checks=dict(GREEN, **{warden.REQUIRED_CHECKS[1]: "neutral"})), "not green"),
            "one rid": (dict(reviews=[qa_row(QA_429.replace(" and win-x64", ""))]), "does not name win-x64"),
            "no workflow line": (dict(reviews=[qa_row(QA_429.replace("Workflow change:", "Workflow:"))]), "no `Workflow change:` line"),
        }
        for label, (over, why) in cases.items():
            self.assertIn(why, self.ok(**over) or "", label)

    def test_one_file_under_the_cap_still_clears(self):
        files = WF_FILES + [{"filename": f"f{i}.cs", "status": "modified", "patch": "+x"}
                            for i in range(warden.PR_FILES_CAP - 2)]
        self.assertIsNone(self.ok(files=files))

    def test_a_nine_character_sha_in_the_qa_comment_counts(self):
        body = QA_429.replace(WF_SHA, "a00bc0cd3")
        self.assertIsNone(self.ok(reviews=[qa_row(body)]))

    def test_a_later_merge_verdict_clears_an_earlier_fix(self):
        fix = qa_row(QA_429.replace("Verdict: merge", "Verdict: fix"), at="2026-11-26T20:00:00Z")
        self.assertIsNone(self.ok(reviews=[fix, qa_row()]))

    def test_parse_request(self):
        ok, _ = warden.parse_request(json.dumps({"verb": "merge_workflow_pr", "pr": 429, "sha": WF_SHA}))
        self.assertEqual(ok, {"verb": "merge_workflow_pr", "pr": 429, "sha": WF_SHA})
        for bad in ({"pr": 429}, {"pr": 0, "sha": WF_SHA}, {"pr": True, "sha": WF_SHA},
                    {"pr": 429, "sha": "a00bc0cd3"}, {"pr": 429, "sha": WF_SHA.upper()},
                    {"pr": 429, "sha": WF_SHA, "repo": "Bad Key"}):
            req, why = warden.parse_request(json.dumps(dict(bad, verb="merge_workflow_pr")))
            self.assertIsNone(req, bad)
            self.assertTrue(why, bad)


class MergingMinter(FakeMinter):
    def __init__(self, installs=None, fail=None):
        super().__init__({warden.REPO: WF_INSTALL} if installs is None else installs)
        self.calls, self.fail = [], fail

    def _call(self, token, path, method="GET", body=None):
        if self.fail:
            raise self.fail
        self.calls.append((token, path, method, body))
        return {"merged": True}


class WfGH(FakeGH):
    def __init__(self, reviews=None, **kw):
        super().__init__(**kw)
        self.reviews = reviews or {}

    def qa_reviews(self, n):
        got = self.reviews.get(n, [])
        if got is UNREADABLE:
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        return got


class WorkflowMergeTickTests(unittest.TestCase):
    REQ = {"verb": "merge_workflow_pr", "pr": 429, "sha": WF_SHA}

    def gh(self, **over):
        kw = dict(prs={429: head_pr(WF_SHA)}, files={429: WF_FILES},
                  checks={WF_SHA: dict(GREEN)}, reviews={429: [qa_row()]})
        kw.update(over)
        return WfGH(**kw)

    def run_tick(self, gh=None, minter=None, pc=None, req=None, state=None):
        m = minter or MergingMinter()
        _, paused, logs = tick(gh=gh or self.gh(), minter=m, pc=pc, requests=[req or self.REQ],
                               state=state if state is not None else {"last_mint": 10_000.0})
        return m, paused, logs

    def test_a_replayed_request_never_merges_twice(self):
        state = {"last_mint": 10_000.0}
        m = MergingMinter()
        gh = self.gh()
        self.run_tick(gh, minter=m, state=state)
        _, _, logs = self.run_tick(gh, minter=m, state=state)
        self.assertEqual(len(m.calls), 1)
        self.assertTrue(any("already merged at a00bc0cd3" in l for l in logs))
        self.assertEqual(state["workflow_merges"][WF_SHA], {"attempts": 1, "merged": True})

    def test_failed_attempts_stop_at_the_cap_and_never_count_as_merged(self):
        state = {"last_mint": 10_000.0}
        err = urllib.error.HTTPError("u", 502, "Bad Gateway", {}, None)
        for _ in range(warden.MAX_WORKFLOW_MERGES_PER_SHA):
            self.run_tick(minter=MergingMinter(fail=err), state=state)
        m, _, logs = self.run_tick(minter=MergingMinter(), state=state)
        self.assertEqual(m.calls, [])
        self.assertTrue(any("already tried 2 times" in l for l in logs))
        self.assertFalse(state["workflow_merges"][WF_SHA]["merged"])
        self.assertNotIn("consecutive_releases", state, "a failed merge is not a merge to the release brake")
        # Control: a new head sha gets fresh attempts.
        sha2 = "b" * 40
        gh = self.gh(prs={429: head_pr(sha2)}, checks={sha2: dict(GREEN)},
                     reviews={429: [qa_row(QA_429.replace(WF_SHA, sha2))]})
        m, _, _ = self.run_tick(gh, req=dict(self.REQ, sha=sha2), state=state)
        self.assertEqual(len(m.calls), 1)

    def test_a_merge_error_never_logs_the_token(self):
        class Leaky(Exception):
            code = 422
            def __str__(self):
                return "Authorization: Bearer tok1"
        _, _, logs = self.run_tick(minter=MergingMinter(fail=Leaky()))
        self.assertTrue(any("failed: Leaky 422" in l for l in logs))
        self.assertFalse([l for l in logs if "tok1" in l])

    def test_a_pause_reason_earlier_in_the_tick_refuses(self):
        logs = []
        w = warden.Warden({}, self.gh(), MergingMinter(), FakePC(), {}, 10_000.0, logs.append)
        w.pause("PR 8: workflow guard: x")
        w.handle(self.REQ)
        self.assertTrue(any("the company is paused" in l for l in logs))
        self.assertEqual(w.minter.calls, [])

    def test_the_next_tick_audits_the_warden_merge_clean(self):
        state = {"last_mint": 10_000.0}
        gh = self.gh()
        self.run_tick(gh, state=state)
        merged = dict(head_pr(WF_SHA), number=429, state="closed", merged_at="2026-11-27T02:00:00Z",
                      merge_commit_sha="m429")
        gh.merged = [merged]
        gh.comments = {429: [QA_429]}
        gh.checks["m429"] = dict(GREEN)
        _, paused, logs = tick(gh=gh, minter=MergingMinter(), state=state)
        self.assertFalse(paused, logs)
        self.assertIn("m429", state["audited"])
        # Control: the same merge with no QA comment pauses.
        gh.comments, state2 = {429: []}, {"last_mint": 10_000.0}
        _, paused, _ = tick(gh=gh, minter=MergingMinter(), state=state2)
        self.assertTrue(paused)

    def test_merges_with_a_workflow_token_pinned_to_the_sha(self):
        m, paused, logs = self.run_tick()
        self.assertFalse(paused)
        self.assertEqual(m.minted, [(9, ["Orbweaver"], dict(warden.WORKFLOW_PERMISSIONS))])
        self.assertEqual(m.calls, [("tok1", f"/repos/{warden.REPO}/pulls/429/merge", "PUT",
                                    {"merge_method": "squash", "sha": WF_SHA})])
        self.assertTrue(any("merged PR 429 at a00bc0cd3 with the workflow token" in l for l in logs))

    def test_a_refusal_mints_nothing_merges_nothing_and_never_pauses(self):
        m, paused, logs = self.run_tick(self.gh(checks={WF_SHA: {}}))
        self.assertEqual((m.minted, m.calls, paused), ([], [], False))
        self.assertTrue(any("refused workflow merge for PR 429: required checks not green" in l for l in logs))

    def test_a_paused_company_is_refused(self):
        m, _, logs = self.run_tick(pc=FakePC(paused=True))
        self.assertEqual(m.calls, [])
        self.assertTrue(any("refused workflow merge for PR 429: the company is paused" in l for l in logs))

    def test_a_missing_pr_or_unknown_repo_is_refused(self):
        m, _, logs = self.run_tick(self.gh(prs={}))
        self.assertTrue(any("PR not found" in l for l in logs))
        m, _, logs = self.run_tick(req=dict(self.REQ, repo="nope"))
        self.assertEqual(m.calls, [])
        self.assertTrue(any("unknown repo 'nope'" in l for l in logs))

    def test_an_installation_without_workflows_is_refused_before_minting(self):
        m, _, logs = self.run_tick(minter=MergingMinter({warden.REPO: DESKTOP_INSTALL}))
        self.assertEqual((m.minted, m.calls), ([], []))
        self.assertTrue(any("workflow merge for PR 429 failed: LookupError" in l for l in logs))

    def test_unreadable_github_is_logged_not_raised(self):
        m, paused, logs = self.run_tick(self.gh(reviews={429: UNREADABLE}))
        self.assertEqual((m.calls, paused), ([], False))
        self.assertTrue(any("workflow merge for PR 429 unreadable" in l for l in logs))

    def test_a_github_refusal_of_the_merge_is_logged_with_its_message(self):
        body = io.BytesIO(json.dumps({"message": "Pull Request is not mergeable",
                                      "documentation_url": "x"}).encode())
        err = urllib.error.HTTPError("u", 405, "Not Allowed", {}, body)
        m, paused, logs = self.run_tick(minter=MergingMinter(fail=err))
        self.assertFalse(paused)
        self.assertTrue(any(l.endswith("workflow merge for PR 429 failed: HTTPError 405: Pull Request is not mergeable")
                            for l in logs), logs)
        # Control: no body, or a body that is not JSON, logs the code alone.
        for fp in (None, io.BytesIO(b"<html>")):
            _, _, logs = self.run_tick(minter=MergingMinter(fail=urllib.error.HTTPError("u", 502, "x", {}, fp)))
            self.assertTrue(any(l.endswith("failed: HTTPError 502") for l in logs), logs)

    def test_the_attempt_record_keeps_the_newest_200_shas(self):
        old = {f"{i:040x}": {"attempts": 1, "merged": True} for i in range(200)}
        state = {"last_mint": 10_000.0, "workflow_merges": dict(old)}
        self.run_tick(state=state)
        tries = state["workflow_merges"]
        self.assertEqual(len(tries), 200)
        self.assertNotIn(f"{0:040x}", tries, "the oldest is dropped")
        self.assertEqual(tries[WF_SHA], {"attempts": 1, "merged": True})

    def test_the_lead_token_never_gains_workflows(self):
        self.assertNotIn("workflows", warden.EVERYDAY_PERMISSIONS)


# ---------------------------------------------------------------- CB-492: an answered leaf whose work is not over

# QAF-244's answer, 2026-11-27T07:08Z, trimmed: it could not do the capture and
# blocked itself. Its parent QAF-234 waited until a person woke it at 08:42Z.
COULD_NOT = ("ANSWER CB-359: could not answer - this run has no `machine_install`, `machine_run` "
             "or `machine_release` tools, and `gh` isn't signed in, so no capture was made on `mini`.")


def qaf244_night(parent="blocked"):
    return [I("P", parent, idle=3600), I("r", "blocked", "P", idle=1800, assignee="qa",
                                         blockedTransitionAt=warden.iso(NOW - 1800))]


def decide_full(issues, answers, state=None, runs=()):
    return warden.unstick_actions(list(issues), list(runs), {}, state or {}, NOW, True, answers)


class AnsweredOpenLeafTests(unittest.TestCase):
    def test_could_not_answer_wakes_the_blocked_parent_once_and_never_touches_the_leaf(self):
        acts = decide_full(qaf244_night(), {"r": (COULD_NOT, None)})
        self.assertEqual([(a["kind"], a["id"]) for a in acts], [("wake_parent_for_answer", "P")])
        self.assertEqual((acts[0]["leaf"], acts[0]["leaf_identifier"], acts[0]["stamp"]),
                         ("r", "QAF-r", warden.iso(NOW - 1800)))
        self.assertFalse([a for a in acts if a["id"] == "r"], "the leaf gets no action at all")

    def test_control_without_an_answer_nothing_wakes_the_parent(self):
        self.assertEqual(decide_full(qaf244_night(), {}), [])

    def test_once_per_block_and_again_after_a_re_block(self):
        state = {"answer_wakes": {"r": warden.iso(NOW - 1800)}}
        self.assertEqual(decide_full(qaf244_night(), {"r": (COULD_NOT, None)}, state), [])
        state = {"answer_wakes": {"r": warden.iso(NOW - 9000)}}  # an earlier block of the same leaf
        self.assertEqual([a["kind"] for a in decide_full(qaf244_night(), {"r": (COULD_NOT, None)}, state)],
                         ["wake_parent_for_answer"])

    def test_a_parent_that_is_not_blocked_or_is_running_is_left_to_read_it(self):
        for status in ("todo", "in_progress", "done"):
            self.assertEqual(decide_full(qaf244_night(status), {"r": (COULD_NOT, None)}), [], status)
        issues = qaf244_night()
        issues[0]["activeRun"] = {"id": "run1"}
        self.assertEqual(decide_full(issues, {"r": (COULD_NOT, None)}), [])

    def test_two_answered_leaves_wake_their_parent_once(self):
        issues = qaf244_night() + [I("s", "blocked", "P", idle=1800, assignee="qa")]
        acts = decide_full(issues, {"r": (COULD_NOT, None), "s": (COULD_NOT, None)})
        self.assertEqual([(a["kind"], a["id"]) for a in acts], [("wake_parent_for_answer", "P")])

    def test_control_an_answer_whose_work_is_over_is_still_closed_not_woken(self):
        acts = decide_full(qaf244_night(), {"r": (ANSWER_427, "merged")})
        self.assertEqual([(a["kind"], a["id"]) for a in acts], [("close_answered", "r")])

    def test_a_tickets_engineer_issue_is_never_closed_on_could_not_answer(self):
        issues = qaf244_night()
        issues[1]["description"] = "Ticket: CB-359\nCapture it"
        acts = decide_full(issues, {"r": (COULD_NOT, None)})
        self.assertEqual([(a["kind"], a["id"]) for a in acts], [("wake_parent_for_answer", "P")])

    def test_the_stamp_is_blocked_transition_only(self):
        issues = qaf244_night()
        state = {"answer_wakes": {"r": warden.iso(NOW - 1800)}}
        issues[1]["updatedAt"] = warden.iso(NOW - 5)  # a comment moved it, not a re-block
        self.assertEqual(decide_full(issues, {"r": (COULD_NOT, None)}, state), [])
        bare = qaf244_night()
        bare[1]["blockedTransitionAt"] = None
        acts = decide_full(bare, {"r": (COULD_NOT, None)})
        self.assertEqual(acts[0]["stamp"], "")
        self.assertEqual(decide_full(bare, {"r": (COULD_NOT, None)}, {"answer_wakes": {"r": ""}}), [],
                         "a leaf with no stamp is woken for once while blocked")

    def test_the_parent_gets_one_action_on_the_tick(self):
        acts = decide_full(qaf244_night(), {"r": (COULD_NOT, None)})
        self.assertEqual([a["kind"] for a in acts if a["id"] == "P"], ["wake_parent_for_answer"])
        self.assertFalse([a for a in acts if a["kind"] in ("wake_parent", "close_parent", "close_answered")])

    def test_the_record_is_kept_while_the_leaf_stays_blocked(self):
        st = {"answer_wakes": {"r": "s1", "gone": "s2", "moved": "s3"}}
        warden.prune_unstick_state(st, [I("r", "blocked"), I("moved", "todo")], NOW)
        self.assertEqual(st["answer_wakes"], {"r": "s1"})


class AnsweredOpenLeafTickTests(unittest.TestCase):
    def run_tick(self, state):
        pc = FakePC()
        pc.all, pc.pool = qaf244_night(), POOL_OK
        pc.threads = {"r": [C("qa", COULD_NOT, NOW - 1800)]}
        _, _, logs = tick(gh=CountingGH(), pc=pc, state=state, now=NOW, cfg={"company_id": "co"})
        return pc, logs

    def test_the_parent_gets_one_comment_and_no_status_moves(self):
        state = {}
        pc, logs = self.run_tick(state)
        self.assertEqual([c[0] for c in pc.comments], ["P"])
        self.assertIn("QAF-r", pc.comments[0][1])
        self.assertIn("its last comment is its ANSWER", pc.comments[0][1])
        self.assertNotIn("machine_install", pc.comments[0][1], "the answer itself is not quoted")
        self.assertEqual(pc.statuses, [], "neither the leaf nor the parent changes status")
        self.assertIn("UNSTUCK: wake_parent_for_answer QAF-P", logs)
        self.assertEqual(state["unstick"]["companies"]["co"]["answer_wakes"], {"r": warden.iso(NOW - 1800)})
        pc, _ = self.run_tick(state)
        self.assertEqual(pc.comments, [], "the same block wakes the parent once")


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
