#!/usr/bin/env python3
"""End-to-end tests for the bug-fix org's host side (CB-455), against loopback
stand-ins for the services it talks to.

    python3 tools/test-bugfix-host-e2e.py

tools/test-bugfix-host.py tests the decisions with fakes. This file runs the
real classes (warden.run_once and main, PaperclipHttp, GhCli, AppMinter,
check_host.main, create_app's helpers, stress-run's runner) against:

  * a loopback Paperclip that returns the response shapes QA measured on the
    live service (run rows with id/status/createdAt, issue rows with
    id/status/description, run log {runId,store,logRef,content}), refuses any
    write without an Origin header, and refuses unauthenticated requests;
  * a loopback GitHub App API that verifies the RS256 JWT it is sent;
  * a stub `gh` and `dotnet` on PATH, driven by a fixture file.

Every pause trigger in plan §6 has a control: the same world one step short of
the trigger leaves the company active and makes no PATCH. POSIX only (the stubs
are scripts, openssl is the signer); skipped on the Windows leg.

Still not covered, and why: the real `gh` login, the real launchctl, GitHub's
real App and ruleset behaviour, and the live Paperclip beyond what QA read.
"""
import base64
import contextlib
import datetime as dt
import http.server
import importlib.util
import io
import json
import os
import re
import shutil
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


warden = load("warden_e2e", "host/warden.py")
create_app = load("create_app_e2e", "host/create_app.py")
check_host = load("check_host_e2e", "host/check_host.py")
stress = load("stress_run_e2e", "tools/stress-run.py")

POSIX = sys.platform != "win32"
HAVE_OPENSSL = POSIX and shutil.which("openssl") is not None
TODAY = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")
REPO = warden.REPO
BOT = warden.BOT_LOGIN


# ---------------------------------------------------------------- loopback Paperclip

class FakePaperclip:
    def __init__(self):
        self.company = {"id": "c1", "status": "active"}
        self.secrets, self.runs, self.logs = [], [], {}
        self.issues = {}          # status -> [rows]
        self.full = {}            # id -> full issue
        self.costs = []
        self.experimental = {}
        self.comments = {}        # issue id -> [comment rows], newest first as measured
        self.resets = []          # (agent id, body) of reset-session posts
        self.requests = []        # (method, path, body, origin)
        self.fail = {}            # path prefix -> status
        outer = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _reply(self, code, obj):
                body = json.dumps(obj).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _handle(self, method):
                n = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(n) if n else b""
                body = json.loads(raw) if raw else None
                origin = self.headers.get("Origin")
                path = self.path
                outer.requests.append((method, path, body, origin))
                if method != "GET" and origin != outer.base:
                    return self._reply(403, {"error": "origin"})
                if path == "/api/auth/sign-in/email":
                    self.send_response(200)
                    self.send_header("Set-Cookie", "session=abc; Path=/")
                    self.send_header("Content-Length", "2")
                    self.end_headers()
                    return self.wfile.write(b"{}")
                if "session=abc" not in (self.headers.get("Cookie") or ""):
                    return self._reply(401, {"error": "auth"})
                for prefix, code in outer.fail.items():
                    if path.startswith(prefix):
                        return self._reply(code, {"error": "injected"})
                return self._route(method, path, body)

            def _route(self, method, path, body):
                c = "/api/companies/c1"
                if path == c and method == "GET":
                    return self._reply(200, outer.company)
                if path == c and method == "PATCH":
                    outer.company.update(body)
                    return self._reply(200, outer.company)
                if path == c + "/secrets" and method == "GET":
                    return self._reply(200, outer.secrets)
                if path == c + "/secrets" and method == "POST":
                    outer.secrets.append({"id": f"s{len(outer.secrets)+1}", "name": body["name"],
                                          "status": "active", "value": body["value"]})
                    return self._reply(201, {})
                m = re.fullmatch(r"/api/secrets/(\w+)/rotate", path)
                if m and method == "POST":
                    for s in outer.secrets:
                        if s["id"] == m.group(1):
                            s["value"] = body["value"]
                    return self._reply(200, {})
                if path.startswith(c + "/heartbeat-runs"):
                    return self._reply(200, outer.runs)
                m = re.fullmatch(r"/api/heartbeat-runs/(\w+)/cancel", path)
                if m and method == "POST":
                    for r in outer.runs:
                        if r["id"] == m.group(1):
                            r["status"] = "cancelled"
                    return self._reply(200, {})
                m = re.fullmatch(r"/api/heartbeat-runs/(\w+)/log.*", path)
                if m:
                    if m.group(1) not in outer.logs:
                        return self._reply(404, {"error": "Heartbeat run not found"})
                    return self._reply(200, outer.logs[m.group(1)])
                if path == c + "/issues?limit=1000":
                    return self._reply(200, [r for rows in outer.issues.values() for r in rows])
                m = re.fullmatch(r"/api/issues/(\w+)/comments", path)
                if m and method == "GET":
                    return self._reply(200, outer.comments.get(m.group(1), []))
                if m and method == "POST":
                    outer.comments.setdefault(m.group(1), []).insert(
                        0, {"id": "new", "body": body["body"], "authorUserId": "board",
                            "authorAgentId": None, "createdAt": "2099-01-01T00:00:00Z"})
                    return self._reply(201, {})
                m = re.fullmatch(r"/api/agents/(\w+)/runtime-state/reset-session", path)
                if m and method == "POST":
                    outer.resets.append((m.group(1), body))
                    return self._reply(200, {})
                m = re.fullmatch(re.escape(c) + r"/issues\?status=(\w+)", path)
                if m:
                    return self._reply(200, outer.issues.get(m.group(1), []))
                m = re.fullmatch(r"/api/issues/(\w+)", path)
                if m and method == "GET":
                    return self._reply(200, outer.full.get(m.group(1), {"id": m.group(1)}))
                if m and method == "PATCH":
                    for rows in outer.issues.values():
                        for r in rows:
                            if r["id"] == m.group(1):
                                r.update(body)
                    return self._reply(200, {})
                if path.startswith(c + "/costs/by-agent"):
                    return self._reply(200, outer.costs)
                if path == "/api/instance/settings/experimental":
                    return self._reply(200, outer.experimental)
                return self._reply(404, {"error": "no route " + path})

            def do_GET(self):
                self._handle("GET")

            def do_POST(self):
                self._handle("POST")

            def do_PATCH(self):
                self._handle("PATCH")

        self.srv = http.server.HTTPServer(("127.0.0.1", 0), H)
        self.base = f"http://127.0.0.1:{self.srv.server_address[1]}"
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def close(self):
        self.srv.shutdown()
        self.srv.server_close()

    def writes(self):
        return [r for r in self.requests if r[0] != "GET" and r[1] != "/api/auth/sign-in/email"]

    def company_patches(self):
        return [r for r in self.requests if r[0] == "PATCH" and r[1] == "/api/companies/c1"]


# ---------------------------------------------------------------- loopback GitHub App API

class FakeGitHubApp:
    def __init__(self, pubkey_path):
        self.pub, self.calls, self.jwts = pubkey_path, [], []
        outer = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _go(self):
                n = int(self.headers.get("Content-Length") or 0)
                body = json.loads(self.rfile.read(n)) if n else None
                auth = self.headers.get("Authorization", "")
                outer.calls.append((self.command, self.path, body))
                if not auth.startswith("Bearer "):
                    code, obj = 401, {"message": "no jwt"}
                else:
                    outer.jwts.append(auth[7:])
                    if self.path == f"/repos/{REPO}/installation":
                        code, obj = 200, {"id": 9}
                    elif self.path == "/app/installations/9/access_tokens":
                        code, obj = 201, {"token": "ghs_FAKETOKEN"}
                    else:
                        code, obj = 404, {}
                data = json.dumps(obj).encode()
                self.send_response(code)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_GET = do_POST = _go

        self.srv = http.server.HTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.srv.server_address[1]}"
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def close(self):
        self.srv.shutdown()
        self.srv.server_close()


def b64d(s):
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def verify_rs256(jwt, pub_path):
    head, body, sig = jwt.split(".")
    with tempfile.NamedTemporaryFile(delete=False) as f:
        f.write(b64d(sig))
    try:
        p = subprocess.run(["openssl", "dgst", "-sha256", "-verify", pub_path, "-signature", f.name],
                           input=f"{head}.{body}".encode(), capture_output=True)
    finally:
        os.unlink(f.name)
    return p.returncode == 0


GH_STUB = r'''#!/usr/bin/env python3
import json, os, sys
a = sys.argv[1:]
open(os.environ["GH_LOG"], "a").write(json.dumps(a) + "\n")
fx = json.load(open(os.environ["GH_FIXTURE"]))
if a[:2] == ["run", "view"]:
    key = "log:" + a[2] + ":" + a[a.index("--attempt") + 1]
    if "--log-failed" not in a or key not in fx:
        sys.stderr.write("gh: run log not found\n")
        sys.exit(1)
    sys.stdout.write(fx[key])
    sys.exit(0)
assert a[0] == "api"
method, path = a[2], a[3]
if method == "POST":
    sys.exit(0)
key = path.split("?")[0]
if key not in fx:
    sys.stderr.write("gh: Not Found (HTTP 404)\n")
    sys.exit(1)
print(json.dumps(fx[key]))
'''

DOTNET_STUB = r'''#!/usr/bin/env python3
import os, sys
n = int(open(os.environ["DN_COUNT"]).read() or 0) + 1 if os.path.exists(os.environ["DN_COUNT"]) else 1
open(os.environ["DN_COUNT"], "w").write(str(n))
if n == int(os.environ.get("DN_FAIL_ON", "0")):
    print("Failed!  - Failed: 1, Passed: 4, Skipped: 0, Total: 5")
    sys.exit(1)
print("Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5")
'''


def write_exe(path, text):
    with open(path, "w") as f:
        f.write(text)
    os.chmod(path, 0o755)


def pr_obj(n=7, **over):
    pr = {"number": n, "state": "open", "user": {"login": BOT},
          "head": {"ref": "bugfix/pc-fix-flake", "repo": {"full_name": REPO}},
          "base": {"ref": "develop"}}
    pr.update(over)
    return pr


KEYS = {}


def setUpModule():
    """One throwaway RSA key for the whole run (generating one per test dominates the time)."""
    if not HAVE_OPENSSL:
        return
    d = tempfile.mkdtemp()
    unittest.addModuleCleanup(shutil.rmtree, d, True)
    KEYS["pem"], KEYS["pub"] = os.path.join(d, "k.pem"), os.path.join(d, "k.pub")
    subprocess.run(["openssl", "genrsa", "-out", KEYS["pem"], "2048"], capture_output=True, check=True)
    subprocess.run(["openssl", "rsa", "-in", KEYS["pem"], "-pubout", "-out", KEYS["pub"]],
                   capture_output=True, check=True)


@unittest.skipUnless(POSIX, "stub gh/dotnet are scripts; the host is a Mac")
class World(unittest.TestCase):
    """One warden world: loopback Paperclip, loopback App API, stub gh, key."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.pc = FakePaperclip()
        self.addCleanup(self.pc.close)
        self.d = os.path.join(self.tmp, "cfg")
        self.spool = os.path.join(self.tmp, "spool")
        os.makedirs(self.d)
        os.makedirs(self.spool)
        bin_ = os.path.join(self.tmp, "bin")
        os.makedirs(bin_)
        write_exe(os.path.join(bin_, "gh"), GH_STUB)
        self.gh_log = os.path.join(self.tmp, "gh.log")
        self.fixture = os.path.join(self.tmp, "fx.json")
        self.fx = {f"repos/{REPO}/pulls": []}
        self.write_fx()
        self.old_env = dict(os.environ)
        os.environ.update(PATH=bin_ + os.pathsep + os.environ["PATH"],
                          GH_LOG=self.gh_log, GH_FIXTURE=self.fixture)
        self.addCleanup(lambda: (os.environ.clear(), os.environ.update(self.old_env)))
        self.pub = None
        if HAVE_OPENSSL:
            pem = os.path.join(self.d, "app.pem")
            shutil.copy(KEYS["pem"], pem)
            self.pub = os.path.join(self.tmp, "pub.pem")
            shutil.copy(KEYS["pub"], self.pub)
            with open(os.path.join(self.d, "app.json"), "w") as f:
                json.dump({"app_id": 77, "slug": "orbweaver-bugfix"}, f)
            self.app = FakeGitHubApp(self.pub)
            self.addCleanup(self.app.close)
        self.login = os.path.join(self.tmp, "login.txt")
        with open(self.login, "w") as f:
            f.write(f"url: {self.pc.base}\nemail: board@example.test\npassword: pw\n")
        # Never this machine's real pool-usage.json.
        self.pool = os.path.join(self.tmp, "pool-usage.json")
        self.cfg = {"dir": self.d, "company_id": "c1", "paperclip_login": self.login, "spool": self.spool,
                    "pool_usage": self.pool,
                    "github_api": self.app.url if HAVE_OPENSSL else "http://127.0.0.1:1"}
        self.state = {"last_mint": 10 ** 12, "cancelled_seen": [], "audited": [], "scanned_runs": []}

    def write_fx(self):
        with open(self.fixture, "w") as f:
            json.dump(self.fx, f)

    def spool_file(self, name, req):
        with open(os.path.join(self.spool, name), "w") as f:
            json.dump(req, f)

    def run_warden(self, mint=False):
        with open(os.path.join(self.d, "warden.json"), "w") as f:
            json.dump(self.cfg, f)
        st = dict(self.state)
        if mint:
            st.pop("last_mint")
        with open(os.path.join(self.d, "state.json"), "w") as f:
            json.dump(st, f)
        self.write_fx()
        return warden.main(["run-once", "--config", os.path.join(self.d, "warden.json")])

    def assert_paused_and_stopped(self, rc, run_ids=(), issue_ids=()):
        self.assertEqual(rc, 3)
        self.assertEqual(self.pc.company["status"], "paused")
        self.assertEqual(len(self.pc.company_patches()), 1)
        self.assertEqual(self.pc.company_patches()[0][2], {"status": "paused"})
        self.assertFalse([w for w in self.pc.secrets], "no token minted on the tick that pauses")
        cancels = {p.split("/")[3] for m, p, b, o in self.pc.requests if p.endswith("/cancel")}
        self.assertEqual(cancels, set(run_ids))
        blocked = [(p, b) for m, p, b, o in self.pc.requests if m == "PATCH" and p.startswith("/api/issues/")]
        self.assertEqual(sorted(p.rsplit("/", 1)[1] for p, b in blocked), sorted(issue_ids))
        self.assertTrue(all(b == {"status": "blocked"} for p, b in blocked))

    def assert_untouched(self, rc):
        self.assertEqual(rc, 0)
        self.assertEqual(self.pc.company["status"], "active")
        self.assertEqual(self.pc.company_patches(), [])

    # ---- the §6 triggers, each beside its control ----

    def test_two_releases_in_a_row_from_the_spool(self):
        self.pc.runs = [{"id": "run1", "status": "running", "createdAt": "2000-01-01"}]
        self.pc.issues["in_progress"] = [{"id": "iss1", "status": "in_progress"}]
        self.spool_file("1.json", {"verb": "released", "ticket": "CB-1"})
        self.assert_untouched(self.run_warden())
        self.pc.requests.clear()
        self.spool_file("2.json", {"verb": "released", "ticket": "CB-2"})
        self.spool_file("3.json", {"verb": "released", "ticket": "CB-3"})
        self.assert_paused_and_stopped(self.run_warden(), ["run1"], ["iss1"])

    def test_two_releases_in_a_row_from_paperclip_issue_states(self):
        eng = lambda i, n: {"id": i, "status": "cancelled", "description": f"Ticket: CB-{n}\nx"}
        self.pc.issues["cancelled"] = [eng("e1", 1)]
        self.assert_untouched(self.run_warden())
        self.pc.requests.clear()
        self.pc.issues["cancelled"] = [eng("e1", 1), eng("e2", 2), eng("e3", 3)]
        self.state["cancelled_seen"] = ["e1"]
        self.assert_paused_and_stopped(self.run_warden())
        self.assertIn("CB-2 released", open(os.path.join(self.d, "warden.log")).read())

    def merged_world(self, comments, check):
        self.fx[f"repos/{REPO}/pulls"] = [pr_obj(7, merged_at="2026-11-24T10:00:00Z", merge_commit_sha="abc")]
        self.fx[f"repos/{REPO}/issues/7/comments"] = [{"body": b, "created_at": "2026-11-24T09:00:00Z"} for b in comments]
        self.fx[f"repos/{REPO}/pulls/7/reviews"] = []
        self.fx[f"repos/{REPO}/pulls/7/comments"] = []
        self.fx[f"repos/{REPO}/commits/abc/check-runs"] = {"check_runs": [
            {"name": n, "conclusion": check} for n in warden.REQUIRED_CHECKS]}

    def test_bot_merge_without_a_qa_comment(self):
        self.merged_world(["osx-arm64 and win-x64 reviewed"], "success")
        self.assert_untouched(self.run_warden())
        self.state["audited"] = []
        self.merged_world(["looks fine"], "success")
        self.assert_paused_and_stopped(self.run_warden())

    def test_red_develop_ci_on_a_bot_merge(self):
        self.merged_world(["osx-arm64 win-x64"], "success")
        self.assert_untouched(self.run_warden())
        self.merged_world(["osx-arm64 win-x64"], "failure")
        self.assert_paused_and_stopped(self.run_warden())

    def test_token_ceiling(self):
        self.pc.costs = [{"inputTokens": 100, "cachedInputTokens": 300, "outputTokens": 100}]
        self.cfg["token_ceiling"] = 500
        self.assert_untouched(self.run_warden())
        self.cfg["token_ceiling"] = 499
        self.assert_paused_and_stopped(self.run_warden())

    def test_run_cap(self):
        self.pc.runs = [{"id": f"r{i}", "status": "completed", "createdAt": TODAY} for i in range(10)]
        self.pc.runs.append({"id": "old", "status": "completed", "createdAt": "2020-01-01T00:00:00Z"})
        self.cfg["max_runs_per_day"] = 10
        self.assert_untouched(self.run_warden())
        self.cfg["max_runs_per_day"] = 9
        self.assert_paused_and_stopped(self.run_warden())

    def test_push_refused_from_the_spool(self):
        self.assert_untouched(self.run_warden())
        self.spool_file("p.json", {"verb": "push_refused"})
        self.assert_paused_and_stopped(self.run_warden())

    def test_push_refused_seen_in_a_run_log(self):
        line = lambda t: json.dumps({"ts": "t", "stream": "stderr", "chunk": t})
        self.pc.runs = [{"id": "ok1", "status": "completed", "createdAt": "2020-01-01"},
                        {"id": "never", "status": "cancelled", "createdAt": "2020-01-01"}]
        self.pc.logs = {"ok1": {"runId": "ok1", "store": "s", "logRef": "x", "content": line("pushed fine")}}
        self.assert_untouched(self.run_warden())
        self.pc.requests.clear()
        self.state["scanned_runs"] = []
        self.pc.logs["ok1"]["content"] = line("remote: error: GH013: Repository rule violations found")
        self.assert_paused_and_stopped(self.run_warden())

    def test_unreadable_release_signal_pauses(self):
        self.pc.issues["cancelled"] = [{"id": "zz112233", "status": "cancelled"}]
        self.pc.full = {"zz112233": {"id": "zz112233"}}
        self.assert_paused_and_stopped(self.run_warden())

    # ---- ordinary ticks ----

    def test_quiet_tick_mints_a_token_into_the_secret_and_never_logs_it(self):
        if not HAVE_OPENSSL:
            self.skipTest("openssl")
        rc = self.run_warden(mint=True)
        self.assert_untouched(rc)
        self.assertEqual([(s["name"], s["value"]) for s in self.pc.secrets], [("GITHUB_TOKEN", "ghs_FAKETOKEN")])
        self.assertNotIn("ghs_FAKETOKEN", open(os.path.join(self.d, "warden.log")).read())
        self.assertGreater(json.load(open(os.path.join(self.d, "state.json")))["last_mint"], 0)
        # Second token refresh rotates the existing secret rather than adding another.
        self.assertEqual(self.run_warden(mint=True), 0)
        self.assertEqual(len(self.pc.secrets), 1)
        self.assertEqual(sum(1 for m, p, b, o in self.pc.requests if p.endswith("/rotate")), 1)

    def test_no_refresh_inside_the_interval(self):
        self.state["last_mint"] = warden.time.time() - 9 * 60
        self.assert_untouched(self.run_warden())
        self.assertEqual(self.pc.secrets, [])
        self.state["last_mint"] = warden.time.time() - 11 * 60
        if HAVE_OPENSSL:
            self.assert_untouched(self.run_warden())
            self.assertEqual(len(self.pc.secrets), 1, "after ten minutes the token is renewed")

    def test_a_paused_company_is_not_refreshed_and_its_stragglers_are_cancelled(self):
        self.pc.company["status"] = "paused"
        self.pc.runs = [{"id": "late", "status": "queued", "createdAt": "2000-01-01"}]
        rc = self.run_warden(mint=True)
        self.assertEqual(rc, 3)
        self.assertEqual(self.pc.secrets, [])
        self.assertEqual(self.pc.company_patches(), [])
        self.assertTrue(any(p.endswith("/cancel") for m, p, b, o in self.pc.requests))

    def dispatches(self):
        return [json.loads(l) for l in open(self.gh_log).read().splitlines()
                if "dispatches" in l] if os.path.exists(self.gh_log) else []

    def test_dispatch_only_for_the_orgs_own_pr(self):
        self.fx[f"repos/{REPO}/pulls/7"] = pr_obj(7)
        self.fx[f"repos/{REPO}/pulls/8"] = pr_obj(8, head={"ref": "bugfix/pc-x", "repo": {"full_name": "fork/Orbweaver"}})
        req = lambda pr: {"verb": "dispatch_stress", "pr": pr, "test_class": "A.B", "n": 5, "suite": "ui"}
        self.spool_file("a.json", req(7))
        self.spool_file("b.json", req(8))
        self.spool_file("c.json", req(99))
        self.assert_untouched(self.run_warden())
        d = self.dispatches()
        self.assertEqual(len(d), 1)
        self.assertIn(f"repos/{REPO}/actions/workflows/stress.yml/dispatches", d[0])
        self.assertIn("ref=bugfix/pc-fix-flake", d[0])
        self.assertIn("inputs[suite]=ui", d[0])
        self.assertIn("inputs[n]=5", d[0])

    def test_state_file_is_private_and_bad_spool_files_are_set_aside(self):
        self.spool_file("x.json", {"verb": "rm -rf"})
        self.run_warden()
        self.assertEqual(stat.S_IMODE(os.stat(os.path.join(self.d, "state.json")).st_mode), 0o600)
        self.assertTrue(os.path.exists(os.path.join(self.spool, "bad", "x.json")))

    def test_a_github_outage_does_not_stop_the_tick_until_it_has_lasted(self):
        del self.fx[f"repos/{REPO}/pulls"]        # the stub gh now fails the audit read
        self.pc.runs = [{"id": "r1", "status": "running", "createdAt": "2000-01-01"}]
        self.assert_untouched(self.run_warden())
        self.assertEqual(self.state.get("audit_failures"), None)
        st = json.load(open(os.path.join(self.d, "state.json")))
        self.assertEqual(st["audit_failures"], 1)
        self.state["audit_failures"] = 2
        self.pc.requests.clear()
        self.assertEqual(self.run_warden(), 3)
        self.assertEqual(self.pc.company["status"], "paused")
        self.assertIn("audit unreadable", open(os.path.join(self.d, "warden.log")).read())

    def test_a_failed_dispatch_is_logged_and_the_tick_continues(self):
        self.fx[f"repos/{REPO}/pulls/7"] = pr_obj(7)
        self.spool_file("a.json", {"verb": "dispatch_stress", "pr": 7, "test_class": "A.B", "n": 5, "suite": "unit"})
        bin_ = os.path.join(self.tmp, "bin")
        write_exe(os.path.join(bin_, "gh"), GH_STUB.replace('if method == "POST":\n    sys.exit(0)', 'if method == "POST":\n    sys.exit(1)'))
        self.assert_untouched(self.run_warden())
        self.assertIn("dispatch for PR 7 failed", open(os.path.join(self.d, "warden.log")).read())

    def test_missing_or_corrupt_state_starts_fresh_and_an_unreadable_spool_entry_is_skipped(self):
        os.makedirs(os.path.join(self.spool, "dir.json"))      # open() on a directory raises OSError
        with open(os.path.join(self.d, "warden.json"), "w") as f:
            json.dump(self.cfg, f)
        with open(os.path.join(self.d, "state.json"), "w") as f:
            f.write("{not json")
        self.write_fx()
        rc = warden.main(["run-once", "--config", os.path.join(self.d, "warden.json")])
        self.assertEqual(rc, 0)
        st = json.load(open(os.path.join(self.d, "state.json")))
        self.assertIn("cancelled_seen", st, "a fresh state was written over the corrupt one")

    # ---- PaperclipHttp ----

    def client(self):
        return warden.PaperclipHttp(self.login, "c1")

    def test_every_write_carries_origin_and_a_request_without_one_is_refused(self):
        c = self.client()
        c.pause("r")
        c.cancel_run("r1")
        c.block_issue("i1")
        c.put_secret("GITHUB_TOKEN", "v")
        writes = self.pc.writes()
        self.assertGreaterEqual(len(writes), 4)
        self.assertTrue(all(o == self.pc.base for m, p, b, o in writes))
        # Control: the stub really refuses a write that lacks Origin.
        req = urllib.request.Request(self.pc.base + "/api/companies/c1", method="PATCH", data=b"{}",
                                     headers={"Cookie": "session=abc", "Content-Type": "application/json"})
        with self.assertRaises(urllib.error.HTTPError) as cm:
            urllib.request.urlopen(req)
        self.assertEqual(cm.exception.code, 403)

    def test_client_methods_against_the_measured_shapes(self):
        self.pc.runs = [{"id": "a", "status": "running", "createdAt": TODAY},
                        {"id": "b", "status": "completed", "createdAt": "2020-01-01"},
                        {"id": "q", "status": "queued", "createdAt": TODAY}]
        self.pc.issues["in_progress"] = [{"id": "i1", "status": "in_progress"}, {"id": "i2", "status": "todo"}]
        self.pc.issues["cancelled"] = [{"id": "c1", "status": "cancelled", "description": "Ticket: CB-5\n"}]
        self.pc.costs = [{"inputTokens": 1, "cachedInputTokens": 2, "outputTokens": 3},
                         {"inputTokens": 10, "cachedInputTokens": 0, "outputTokens": 0}]
        c = self.client()
        self.assertFalse(c.is_paused())
        self.assertEqual(c.runs_today(), 2)
        self.assertEqual(c.active_run_ids(), ["a", "q"])
        self.assertEqual(c.in_progress_issue_ids(), ["i1"])
        self.assertEqual([r["id"] for r in c.cancelled_issue_rows()], ["c1"])
        self.assertEqual(c.get_issue("c1"), {"id": "c1"})
        self.assertEqual(c.tokens_today(), 16)
        self.assertEqual(c.recent_run_ids(), ["a", "b", "q"])
        c.pause("why")
        self.assertTrue(c.is_paused())

    def test_issue_listing_may_be_wrapped_in_an_object(self):
        c = self.client()
        c.call = lambda method, path, body=None: {"issues": [{"id": "i1", "status": "in_progress"}]}
        self.assertEqual(c.in_progress_issue_ids(), ["i1"])
        self.assertEqual([r["id"] for r in c.cancelled_issue_rows()], [])

    def test_put_secret_creates_then_rotates_and_skips_deleted(self):
        c = self.client()
        c.put_secret("GITHUB_TOKEN", "one")
        c.put_secret("GITHUB_TOKEN", "two")
        self.assertEqual([(s["name"], s["value"]) for s in self.pc.secrets], [("GITHUB_TOKEN", "two")])
        self.pc.secrets[0]["status"] = "deleted"
        c.put_secret("GITHUB_TOKEN", "three")
        self.assertEqual(len(self.pc.secrets), 2)

    def test_run_log_404_is_no_log_and_other_errors_raise(self):
        c = self.client()
        self.assertEqual(c.run_log("missing"), "")
        self.pc.logs["r1"] = {"runId": "r1", "store": "s", "logRef": "x",
                              "content": json.dumps({"ts": "t", "stream": "stdout", "chunk": "hello"})}
        self.assertEqual(c.run_log("r1"), "hello")
        self.pc.fail["/api/heartbeat-runs/r1/log"] = 500
        with self.assertRaises(urllib.error.HTTPError):
            c.run_log("r1")

    def test_http_errors_are_raised_not_swallowed(self):
        c = self.client()
        for prefix, call in (("/api/companies/c1/secrets", lambda: c.put_secret("N", "v")),
                             ("/api/companies/c1", lambda: c.pause("r")),
                             ("/api/heartbeat-runs/r1/cancel", lambda: c.cancel_run("r1")),
                             ("/api/issues/i1", lambda: c.block_issue("i1"))):
            self.pc.fail[prefix] = 500
            with self.assertRaises(urllib.error.HTTPError, msg=prefix):
                call()
            del self.pc.fail[prefix]

    # ---- the unstick sweep (CB-479), through run_once and the real client ----

    OLD = "2000-01-01T00:00:00.000Z"

    def released_pass(self):
        """CB-424's release: pass blocked, engineer child cancelled, the lead's
        report after it."""
        self.pc.issues["blocked"] = [{"id": "P", "identifier": "QAF-126", "status": "blocked",
                                      "parentId": None, "assigneeAgentId": "lead", "activeRun": None,
                                      "lastActivityAt": self.OLD, "updatedAt": self.OLD}]
        self.pc.issues["cancelled"] = [{"id": "e1", "identifier": "QAF-127", "status": "cancelled",
                                        "parentId": "P", "assigneeAgentId": "eng", "activeRun": None,
                                        "lastActivityAt": self.OLD, "updatedAt": self.OLD,
                                        "description": "Ticket: CB-424\n"}]
        self.pc.comments["P"] = [{"id": "c2", "authorAgentId": "lead", "createdAt": "2000-01-01T00:05:00Z"},
                                 {"id": "c1", "authorAgentId": "lead", "createdAt": "1999-12-31T00:00:00Z"}]
        self.state["cancelled_seen"] = ["e1"]

    def test_unstick_closes_a_released_pass_with_origin(self):
        self.released_pass()
        self.assert_untouched(self.run_warden())
        done = [(p, b, o) for m, p, b, o in self.pc.requests if m == "PATCH" and p == "/api/issues/P"]
        self.assertEqual(done, [("/api/issues/P", {"status": "done"}, self.pc.base)])
        log = open(os.path.join(self.d, "warden.log")).read()
        self.assertIn("UNSTUCK: close_parent QAF-126", log)

    def test_unstick_control_a_paused_company_is_not_touched(self):
        self.released_pass()
        self.pc.company["status"] = "paused"
        self.run_warden()
        self.assertEqual([r for r in self.pc.requests if r[1].startswith("/api/issues/P")], [])
        self.assertNotIn("UNSTUCK", open(os.path.join(self.d, "warden.log")).read())

    def test_unstick_resets_an_exhausted_task_when_the_pool_has_room(self):
        self.pc.issues["in_progress"] = [{"id": "c", "identifier": "QAF-9", "status": "in_progress",
                                          "parentId": "P", "assigneeAgentId": "eng", "activeRun": None,
                                          "lastActivityAt": self.OLD, "updatedAt": self.OLD}]
        self.pc.runs = [{"id": "r9", "issueId": "c", "agentId": "eng", "status": "cancelled",
                         "createdAt": TODAY, "errorCode": "ai_connection_pool_exhausted",
                         "error": "All eligible pool accounts are over their usage limit.",
                         "contextSnapshot": {"taskKey": "c"}}]
        with open(self.pool, "w") as f:
            json.dump({"accounts": {"b": {"status": "ok", "limits": [{"usedPercent": 12}]}}}, f)
        self.assert_untouched(self.run_warden())
        self.assertEqual(self.pc.resets, [("eng", {"taskKey": "c"})])
        self.assertEqual(len(self.pc.comments["c"]), 1, "and the task is woken")
        self.assertTrue(all(o == self.pc.base for m, p, b, o in self.pc.writes()))

    def test_unstick_client_methods(self):
        self.pc.comments["i1"] = [{"authorAgentId": "a", "createdAt": "2026-01-01T00:00:10Z"},
                                  {"authorAgentId": "a", "createdAt": "2026-01-01T00:00:20Z",
                                   "deletedAt": "2026-01-01T00:01:00Z"},
                                  {"authorAgentId": "b", "createdAt": "2026-01-01T00:00:30Z"},
                                  {"authorAgentId": "a", "createdAt": None}]
        c = self.client()
        self.assertEqual(c.newest_comment_at("i1", "a"), warden.ts("2026-01-01T00:00:10Z"),
                         "deleted comments and other authors do not count")
        self.assertIsNone(c.newest_comment_at("i1", None))
        self.assertIsNone(c.newest_comment_at("none", "a"))
        self.assertFalse(c.is_paused("c1"))
        c.call = lambda method, path, body=None: {"comments": [], "issues": [{"id": "x"}]}
        self.assertEqual(c.all_issues("c1"), [{"id": "x"}])
        self.assertIsNone(c.newest_comment_at("i1", "a"))
        with open(self.pool, "w") as f:
            f.write("{}")
        self.assertEqual(c.pool_usage(self.pool), {})

    def test_unauthenticated_requests_are_refused_by_the_stub(self):
        with self.assertRaises(urllib.error.HTTPError) as cm:
            urllib.request.urlopen(self.pc.base + "/api/companies/c1")
        self.assertEqual(cm.exception.code, 401)

    # ---- GhCli (real class, stub gh) ----

    def test_ghcli_methods(self):
        gh = warden.GhCli()
        self.fx[f"repos/{REPO}/pulls/7"] = pr_obj(7)
        self.fx[f"repos/{REPO}/pulls"] = [
            pr_obj(1, merged_at="t", merge_commit_sha="m1"),
            pr_obj(2, merged_at=None),
            pr_obj(3, merged_at="t", user={"login": "human"})]
        self.fx[f"repos/{REPO}/issues/1/comments"] = [
            {"body": "early", "created_at": "2026-01-01T00:00:00Z"},
            {"body": "late", "created_at": "2027-01-01T00:00:00Z"}]
        self.fx[f"repos/{REPO}/pulls/1/reviews"] = [{"body": "rev", "submitted_at": "2026-01-02T00:00:00Z"}, {"body": ""}]
        self.fx[f"repos/{REPO}/pulls/1/comments"] = []
        self.fx[f"repos/{REPO}/commits/m1/check-runs"] = {"check_runs": [{"name": "a", "conclusion": "success"}, {"name": "b"}]}
        self.write_fx()
        self.assertEqual(gh.get_pr(7)["number"], 7)
        self.assertIsNone(gh.get_pr(404), "a missing PR is None, not a crash")
        self.assertEqual([p["number"] for p in gh.merged_bot_prs()], [1])
        self.assertEqual(gh.comment_bodies(1, before="2026-06-01T00:00:00Z"), ["early", "rev"])
        self.assertEqual(gh.check_conclusions("m1"), {"a": "success", "b": None})
        gh.dispatch_stress("bugfix/pc-x", "A.B", 3, "unit")
        args = json.loads([l for l in open(self.gh_log) if "dispatches" in l][0])
        self.assertEqual(args[1:4], ["-X", "POST", f"repos/{REPO}/actions/workflows/stress.yml/dispatches"])
        # CB-465: the newest ci.yml run at a sha, never another workflow's, and the rerun POST.
        self.fx[f"repos/{REPO}/actions/runs"] = {"workflow_runs": [
            {"id": 5, "name": "CI", "path": ".github/workflows/ci.yml", "head_sha": "m1", "created_at": "2026-01-01T00:00:00Z"},
            {"id": 6, "name": "CI", "path": ".github/workflows/ci.yml", "head_sha": "m1", "created_at": "2026-01-02T00:00:00Z"},
            {"id": 7, "name": "Stress", "path": ".github/workflows/stress.yml", "head_sha": "m1", "created_at": "2026-01-03T00:00:00Z"}]}
        self.write_fx()
        self.assertEqual(gh.latest_ci_run("m1")["id"], 6)
        gh.rerun_failed(6)
        args = json.loads([l for l in open(self.gh_log) if "rerun-failed-jobs" in l][0])
        self.assertEqual(args[1:4], ["-X", "POST", f"repos/{REPO}/actions/runs/6/rerun-failed-jobs"])
        self.fx[f"repos/{REPO}/actions/runs"] = {"workflow_runs": []}
        self.write_fx()
        self.assertIsNone(gh.latest_ci_run("m1"))
        # CB-467: the failed tests of one attempt, read off its failed jobs' log.
        self.fx["log:6:2"] = ("unit-package (osx-arm64)\tUnit tests (xUnit)\t2026-11-25T03:52:11.9809370Z "
                              "[xUnit.net 00:01:05.46]     Orbweaver.Tests.TurnSoundsTests.APendingSound [FAIL]\n"
                              "unit-package (osx-arm64)\tUnit tests (xUnit)\t2026-11-25T03:52:11.9913230Z "
                              "  Failed Orbweaver.Tests.TurnSoundsTests.APendingSound [3 s]\n")
        self.write_fx()
        self.assertEqual(gh.failed_tests(6, 2), ["Orbweaver.Tests.TurnSoundsTests.APendingSound"])
        args = json.loads([l for l in open(self.gh_log) if "--log-failed" in l][0])
        self.assertEqual(args, ["run", "view", "6", "--repo", REPO, "--attempt", "2", "--log-failed"])
        with self.assertRaises(subprocess.CalledProcessError):
            gh.failed_tests(6, 3)

    # ---- AppMinter ----

    @unittest.skipUnless(HAVE_OPENSSL, "needs openssl")
    def test_jwt_is_a_valid_rs256_token_with_a_short_life(self):
        m = warden.AppMinter(self.d, now=lambda: 1_700_000_000)
        jwt = m.jwt(77)
        self.assertTrue(verify_rs256(jwt, self.pub))
        head, body, _ = jwt.split(".")
        self.assertEqual(json.loads(b64d(head)), {"alg": "RS256", "typ": "JWT"})
        claims = json.loads(b64d(body))
        self.assertEqual(claims["iss"], "77")
        self.assertLessEqual(claims["exp"] - claims["iat"], 600)
        self.assertEqual(claims["iat"], 1_700_000_000 - 60)
        # Control: one flipped payload byte must fail verification.
        tampered = json.loads(b64d(body))
        tampered["iss"] = "78"
        bad = ".".join([head, warden.b64url(json.dumps(tampered).encode()), jwt.split(".")[2]])
        self.assertFalse(verify_rs256(bad, self.pub))

    @unittest.skipUnless(HAVE_OPENSSL, "needs openssl")
    def test_jwt_uses_the_clock_it_is_given(self):
        a = warden.AppMinter(self.d, now=lambda: 1000).jwt(1)
        b = warden.AppMinter(self.d, now=lambda: 1000 + 46 * 60).jwt(1)
        ia = json.loads(b64d(a.split(".")[1]))["iat"]
        ib = json.loads(b64d(b.split(".")[1]))["iat"]
        self.assertEqual(ib - ia, 46 * 60)

    @unittest.skipUnless(HAVE_OPENSSL, "needs openssl")
    def test_mint_returns_a_repo_scoped_installation_token_signed_with_the_key(self):
        token = warden.AppMinter(self.d, api=self.app.url).mint()
        self.assertEqual(token, "ghs_FAKETOKEN")
        self.assertEqual([(m, p) for m, p, b in self.app.calls],
                         [("GET", f"/repos/{REPO}/installation"), ("POST", "/app/installations/9/access_tokens")])
        self.assertEqual(self.app.calls[1][2], {"repositories": ["Orbweaver"]})
        self.assertTrue(all(verify_rs256(j, self.pub) for j in self.app.jwts))

    @unittest.skipUnless(HAVE_OPENSSL, "needs openssl")
    def test_mint_fails_loudly_when_the_api_refuses(self):
        self.app.close()
        with self.assertRaises(OSError):
            warden.AppMinter(self.d, api=self.app.url).mint()

    # ---- check_host.main ----

    def host_world(self, **drop):
        gitconfig = os.path.join(self.tmp, "gitconfig")
        plist = os.path.join(self.tmp, "svc.plist")
        open(gitconfig, "w").close()
        with open(plist, "w") as f:
            f.write(f"<dict><key>GIT_CONFIG_GLOBAL</key><string>{gitconfig}</string></dict>")
        self.pc.experimental = {"enableIsolatedWorkspaces": True, "enableWorktreeRunExecution": True,
                                "enableIsolatedWorkspacesByDefault": False}
        cfg = dict(self.cfg)
        if drop.get("config"):
            cfg.pop("spool")
        if drop.get("gitconfig"):
            os.unlink(gitconfig)
        if drop.get("plist"):
            with open(plist, "w") as f:
                f.write("<dict/>")
        self.pc.experimental.update(drop.get("flags", {}))
        path = os.path.join(self.tmp, "warden.json")
        with open(path, "w") as f:
            json.dump(cfg, f)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = check_host.main(["--config", path, "--plist", plist, "--gitconfig", gitconfig])
        return rc, out.getvalue()

    def test_check_host_all_present_exits_zero(self):
        rc, out = self.host_world()
        self.assertEqual((rc, out.strip()), (0, "host ok"))

    def test_check_host_each_missing_prerequisite_fails_and_is_named(self):
        cases = [
            ("config", {"config": True}, "spool"),
            ("gitconfig", {"gitconfig": True}, "gitconfig"),
            ("plist", {"plist": True}, "GIT_CONFIG_GLOBAL"),
            ("isolated off", {"flags": {"enableIsolatedWorkspaces": False}}, "enableIsolatedWorkspaces"),
            ("worktree off", {"flags": {"enableWorktreeRunExecution": False}}, "enableWorktreeRunExecution"),
            ("by-default on", {"flags": {"enableIsolatedWorkspacesByDefault": True}}, "enableIsolatedWorkspacesByDefault"),
        ]
        for label, drop, needle in cases:
            rc, out = self.host_world(**drop)
            self.assertEqual(rc, 1, label)
            self.assertIn("PROBLEM:", out, label)
            self.assertIn(needle, out, label)

    def test_check_host_reports_an_unreachable_paperclip_and_an_unreadable_config(self):
        self.pc.close()
        rc, out = self.host_world()
        self.assertEqual(rc, 1)
        self.assertIn("could not read Paperclip", out)
        out2 = io.StringIO()
        with contextlib.redirect_stdout(out2):
            rc = check_host.main(["--config", "/nonexistent/w.json", "--plist", "/nonexistent/p", "--gitconfig", "/nonexistent/g"])
        self.assertEqual(rc, 1)
        self.assertIn("cannot read /nonexistent/w.json", out2.getvalue())
        self.assertIn("cannot read /nonexistent/p", out2.getvalue())


# ---------------------------------------------------------------- create_app extras

class CreateAppExtras(unittest.TestCase):
    @unittest.skipUnless(POSIX, "POSIX modes")
    def test_write_private_replaces_a_world_readable_file_without_a_readable_window(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "app.pem")
            with open(path, "w") as f:
                f.write("OLD")
            os.chmod(path, 0o644)
            real = os.chmod
            os.chmod = lambda *a, **k: None   # control: the mode must be right without the chmod
            try:
                create_app.write_private(path, "NEWKEY")
            finally:
                os.chmod = real
            self.assertEqual(stat.S_IMODE(os.stat(path).st_mode), 0o600)
            self.assertEqual(open(path).read(), "NEWKEY")
            create_app.write_private(path, "AGAIN")
            self.assertEqual(open(path).read(), "AGAIN")

    def test_unknown_path_is_404_and_a_finished_helper_answers_410(self):
        result = create_app.Result()
        with tempfile.TemporaryDirectory() as d:
            handler = create_app.make_handler("S", "https://g.example", "http://127.0.0.1:1", d,
                                              "http://127.0.0.1/callback", result)
            srv = http.server.HTTPServer(("127.0.0.1", 0), handler)
            threading.Thread(target=srv.serve_forever, daemon=True).start()
            self.addCleanup(srv.server_close)
            self.addCleanup(srv.shutdown)
            base = f"http://127.0.0.1:{srv.server_address[1]}"

            def code(path):
                try:
                    return urllib.request.urlopen(base + path).status
                except urllib.error.HTTPError as e:
                    return e.code
            self.assertEqual(code("/nope"), 404)
            result.done.set()
            self.assertEqual(code("/callback?code=c&state=S"), 410)
            self.assertEqual(os.listdir(d), [])

    def test_main_times_out_when_nobody_calls_back(self):
        with tempfile.TemporaryDirectory() as d, contextlib.redirect_stdout(io.StringIO()), \
                contextlib.redirect_stderr(io.StringIO()):
            rc = create_app.main(["--public-host", "127.0.0.1", "--bind", "127.0.0.1",
                                  "--timeout", "0.2", "--config-dir", d])
        self.assertEqual(rc, 2)


# ---------------------------------------------------------------- stress-run extras

@unittest.skipUnless(POSIX, "stub dotnet is a script")
class StressRunExtras(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        write_exe(os.path.join(self.tmp, "dotnet"), DOTNET_STUB)
        self.count = os.path.join(self.tmp, "count")
        self.old = dict(os.environ)
        os.environ.update(PATH=self.tmp + os.pathsep + os.environ["PATH"], DN_COUNT=self.count)
        self.addCleanup(lambda: (os.environ.clear(), os.environ.update(self.old)))

    def invocations(self):
        return int(open(self.count).read()) if os.path.exists(self.count) else 0

    def run_main(self, argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = stress.main(argv)
        return rc, out.getvalue()

    def test_default_runner_returns_exit_code_and_both_streams(self):
        rc, text = stress.default_runner([sys.executable, "-c",
                                          "import sys; print('out'); print('err', file=sys.stderr); sys.exit(3)"])
        self.assertEqual(rc, 3)
        self.assertIn("out", text)
        self.assertIn("err", text)

    def test_main_runs_n_times_all_green(self):
        rc, out = self.run_main(["--suite", "unit", "--test-class", "A.B", "--n", "4"])
        self.assertEqual(rc, 0)
        self.assertEqual(self.invocations(), 4)
        self.assertEqual(len(re.findall(r"^run \d+/4: green", out, re.M)), 4)

    def test_failure_on_iteration_k_is_red_and_every_run_still_happens(self):
        os.environ["DN_FAIL_ON"] = "2"
        rc, out = self.run_main(["--suite", "ui", "--test-class", "A.B", "--n", "5"])
        self.assertEqual(rc, 1)
        self.assertEqual(self.invocations(), 5)
        self.assertEqual(len(re.findall(r"^run \d+/5: ", out, re.M)), 5)
        self.assertIn("run 2/5: red", out)
        self.assertIn("4 green, 1 red", out)

    def test_bad_class_name_fails_without_running_anything(self):
        for cls in ("A;rm -rf ~", "$(id)", "a b"):
            rc, out = self.run_main(["--suite", "unit", "--test-class", cls, "--n", "3"])
            self.assertEqual(rc, 2, cls)
            self.assertIn("refusing", out)
        self.assertEqual(self.invocations(), 0)


class WiringTests(unittest.TestCase):
    def test_ci_runs_the_e2e_suite(self):
        with open(os.path.join(ROOT, ".github/workflows/ci.yml")) as f:
            self.assertIn("python tools/test-bugfix-host-e2e.py", f.read())


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
