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
        self.assertFalse(warden.mint_due({"last_mint": 1000}, 1000 + 44 * 60, False))
        self.assertTrue(warden.mint_due({"last_mint": 1000}, 1000 + 45 * 60, False))
        self.assertFalse(warden.mint_due({}, 1000, True), "a paused org is not refreshed")


# ---------------------------------------------------------------- warden tick

class FakeGH:
    def __init__(self, prs=None, merged=(), comments=None, checks=None):
        self.prs, self.merged = prs or {}, list(merged)
        self.comments, self.checks = comments or {}, checks or {}
        self.dispatched = []

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

    def is_paused(self):
        return self.paused

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
                         ["refs/heads/bugfix/pc-*", "refs/heads/develop", "refs/heads/main"])
        self.assertEqual({x["type"] for x in r["rules"]}, {"creation", "update", "deletion"})
        ids = {(a["actor_type"], a["actor_id"]) for a in r["bypass_actors"]}
        self.assertEqual(ids, {("RepositoryRole", 5), ("Integration", 15368), ("Integration", 29110)})

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
  "api apps/github-actions --jq .id") echo "${ACTIONS_ID:-15368}" ;;
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
        for env in ({"ACTIONS_ID": "1"}, {"DEPENDABOT_ID": "2"}):
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
