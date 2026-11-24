#!/usr/bin/env python3
"""The warden: the bug-fix org's host-side keeper (CB-455, plan §2, §4, §6).

    python3 host/warden.py run-once [--config ~/.config/orbweaver-bugfix/warden.json]

A launchd job (host/launchd/) runs `run-once` every five minutes, outside the
Paperclip sandbox. Each tick it:

  1. reads requests the org left in the spool folder (fixed schema, closed list
     of verbs) and acts on the ones it recognises;
  2. audits bot merges into develop;
  3. mints a one-hour installation token and writes it into the fix company's
     GITHUB_TOKEN secret, if the last one is older than 45 minutes and the
     company is not paused (a paused org stops being refreshed, so its access
     lapses by itself);
  4. pauses the company on any trigger in plan §6.

Everything that decides anything is a pure function at the top of the file;
everything that touches the world sits behind one of three seams (GitHub,
Minter, Paperclip), so tools/test-bugfix-host.py drives the rules with fakes
and no network. The real seams are at the bottom and are not exercised by the
suite: `gh` runs as the host user's own GitHub login, the minter signs a JWT
with `openssl`, and Paperclip is reached over HTTP with the board login in
~/.config/paperclip-admin/.

Credentials, to be explicit about which does what:
  * the App private key only ever mints installation tokens, which go only to
    Paperclip's secret store;
  * dispatching stress.yml and reading PRs/checks use the host's own `gh`
    login, because the App holds Actions *read* only (plan §2) and so cannot
    dispatch. That login is not reachable from the sandbox (measured in the plan).

The warden never runs a command an agent wrote, never runs git in an agent's
worktree, and reads spool files as data: a request that does not parse, names
an unknown verb, or carries a value outside its pattern is logged and dropped.
"""
import argparse
import base64
import datetime as dt
import glob
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

REPO = "Uplift-Foundation/Orbweaver"
BOT_LOGIN = "orbweaver-bugfix[bot]"
BRANCH_PREFIX = "bugfix/pc-"
RIDS = ("osx-arm64", "win-x64")
REQUIRED_CHECKS = ("build (macos-latest, osx-arm64)", "build (windows-latest, win-x64)")
MINT_INTERVAL = 45 * 60
VERBS = ("dispatch_stress", "released", "push_refused")
CLASS_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]{0,199}$")
SUITES = ("unit", "integration", "ui")
MAX_N = 50
RELEASES_IN_A_ROW = 2
DEFAULT_DIR = os.path.expanduser("~/.config/orbweaver-bugfix")


# ---------------------------------------------------------------- pure rules

def parse_request(raw):
    """(request, None) for a valid spool request, else (None, why)."""
    try:
        req = json.loads(raw)
    except (ValueError, TypeError):
        return None, "not JSON"
    if not isinstance(req, dict):
        return None, "not an object"
    verb = req.get("verb")
    if verb not in VERBS:
        return None, f"unknown verb {verb!r}"
    if verb == "dispatch_stress":
        pr, cls, n, suite = req.get("pr"), req.get("test_class"), req.get("n"), req.get("suite", "unit")
        if not (isinstance(pr, int) and not isinstance(pr, bool) and pr > 0):
            return None, "pr must be a positive integer"
        if not (isinstance(cls, str) and CLASS_RE.match(cls)):
            return None, "test_class outside pattern"
        if not (isinstance(n, int) and not isinstance(n, bool) and 1 <= n <= MAX_N):
            return None, f"n must be 1..{MAX_N}"
        if suite not in SUITES:
            return None, "suite must be one of " + "/".join(SUITES)
        return {"verb": verb, "pr": pr, "test_class": cls, "n": n, "suite": suite}, None
    if verb == "released":
        t = req.get("ticket")
        if not (isinstance(t, str) and re.fullmatch(r"CB-\d{1,6}", t)):
            return None, "ticket must look like CB-123"
        return {"verb": verb, "ticket": t}, None
    return {"verb": verb}, None


def dispatch_problem(pr):
    """Why the warden must NOT dispatch stress.yml for this PR (None = go).

    `pr` is the GitHub PR object. "The org's own PRs only": opened by the bot,
    from a branch in this repository (a fork PR is the one way to run someone
    else's code on the runners at our expense), under the org's prefix, and
    still open.
    """
    if pr is None:
        return "PR not found"
    if pr.get("state") != "open":
        return "PR is not open"
    if (pr.get("user") or {}).get("login") != BOT_LOGIN:
        return "PR was not opened by the bug-fix App"
    head = pr.get("head") or {}
    if ((head.get("repo") or {}).get("full_name")) != REPO:
        return "PR head is not in this repository"
    if not str(head.get("ref", "")).startswith(BRANCH_PREFIX):
        return f"head branch lacks the {BRANCH_PREFIX} prefix"
    if ((pr.get("base") or {}).get("ref")) != "develop":
        return "PR does not target develop"
    return None


def has_qa_comment(bodies):
    """A QA comment names both rids. `bodies` are comment and review texts
    posted before the merge (the PR body itself is not one)."""
    return any(all(r in b for r in RIDS) for b in bodies)


def audit_merge(has_qa, ci):
    """Pause reasons for one bot merge. `ci` maps check name -> conclusion
    for the merge commit; a required check that is absent counts as not green
    only when some check has already failed, since checks may still be queued."""
    reasons = []
    if not has_qa:
        reasons.append("bot PR merged with no QA comment naming both rids")
    failed = [n for n in REQUIRED_CHECKS if ci.get(n) in ("failure", "cancelled", "timed_out")]
    if failed:
        reasons.append("develop CI red on a bot merge: " + ", ".join(failed))
    return reasons


def next_release_state(state, event):
    """Counter of consecutive releases; a merge breaks the run."""
    n = state.get("consecutive_releases", 0)
    return n + 1 if event == "released" else 0


def cost_problem(tokens, ceiling):
    if ceiling is None:
        return None
    return f"daily tokens {tokens} passed ceiling {ceiling}" if tokens > ceiling else None


REFUSAL_MARKER = "GH013"  # "remote: error: GH013: Repository rule violations found"


def new_releases(cancelled_ids, state):
    """Count issues newly cancelled in the fix company: a release, as seen by
    Paperclip itself rather than reported from the sandbox. The first look only
    seeds the set (everything already cancelled is history, not a release)."""
    seen = state.get("cancelled_seen")
    ids = sorted(set(cancelled_ids))
    state["cancelled_seen"] = sorted(set(seen or []) | set(ids))[-500:]
    if seen is None:
        return 0
    return len([i for i in ids if i not in seen])


def log_shows_refusal(text):
    return REFUSAL_MARKER in (text or "")


def runs_problem(count, cap):
    """Daily run brake. Paperclip's heartbeat.maxDailyRuns sits in the timer
    policy and does not bound assignment wakes (phase 0), so the warden counts."""
    if cap is None:
        return None
    return f"{count} runs started today, cap {cap}" if count > cap else None


def mint_due(state, now, paused):
    if paused:
        return False
    last = state.get("last_mint")
    return last is None or now - last >= MINT_INTERVAL


# ---------------------------------------------------------------- the tick

class Warden:
    def __init__(self, cfg, github, minter, paperclip, state, now, log):
        self.cfg, self.gh, self.minter, self.pc = cfg, github, minter, paperclip
        self.state, self.now, self.log = state, now, log
        self.pause_reasons = []

    def pause(self, reason):
        self.pause_reasons.append(reason)

    def handle(self, req):
        v = req["verb"]
        if v == "dispatch_stress":
            pr = self.gh.get_pr(req["pr"])
            why = dispatch_problem(pr)
            if why:
                return self.log(f"refused dispatch for PR {req['pr']}: {why}")
            self.gh.dispatch_stress(pr["head"]["ref"], req["test_class"], req["n"], req["suite"])
            self.log(f"dispatched stress.yml for PR {req['pr']} {req['test_class']} x{req['n']}")
        elif v == "released":
            self.state["consecutive_releases"] = next_release_state(self.state, "released")
            self.log(f"{req['ticket']} released ({self.state['consecutive_releases']} in a row)")
            if self.state["consecutive_releases"] >= RELEASES_IN_A_ROW:
                self.pause(f"{RELEASES_IN_A_ROW} tickets released in a row")
        elif v == "push_refused":
            self.pause("GitHub refused a bot push on a ruleset")

    def stop_work(self):
        """A pause only defers new assignments: a run in flight keeps going
        (phase 0, measured). So while the company is paused, every tick cancels
        its queued and running runs and sets its in-progress issues to blocked.
        The warden never resumes; a person or the lead does."""
        for run_id in self.pc.active_run_ids():
            self.pc.cancel_run(run_id)
            self.log(f"cancelled run {run_id[:8]}")
        for issue_id in self.pc.in_progress_issue_ids():
            self.pc.block_issue(issue_id)
            self.log(f"blocked issue {issue_id[:8]}")

    def watch_releases(self):
        """Releases from Paperclip's own issue states. The spool's `released`
        stays as an extra signal; this one cannot be suppressed from inside
        the sandbox short of not releasing at all."""
        for _ in range(new_releases(self.pc.cancelled_issue_ids(), self.state)):
            n = self.state["paperclip_releases"] = self.state.get("paperclip_releases", 0) + 1
            self.log(f"a Paperclip issue was cancelled: release ({n} in a row)")
            if n >= RELEASES_IN_A_ROW:
                self.pause(f"{RELEASES_IN_A_ROW} releases in a row (Paperclip issue states)")

    def watch_push_refusals(self):
        """GitHub's ruleset refusal text in a run's log means an agent tried to
        leave its lane. Each run is read once. See host/README.md for what this
        does and does not see."""
        scanned = set(self.state.get("scanned_runs", []))
        for run_id in self.pc.recent_run_ids():
            if run_id in scanned:
                continue
            scanned.add(run_id)
            if log_shows_refusal(self.pc.run_log(run_id)):
                self.pause(f"run {run_id[:8]} log shows GitHub refusing a push (GH013)")
        self.state["scanned_runs"] = sorted(scanned)[-500:]

    def audit(self):
        seen = set(self.state.setdefault("audited", []))
        for pr in self.gh.merged_bot_prs():
            sha = pr["merge_commit_sha"]
            if sha in seen:
                continue
            seen.add(sha)
            self.state["consecutive_releases"] = next_release_state(self.state, "merged")
            self.state["paperclip_releases"] = 0
            qa = has_qa_comment(self.gh.comment_bodies(pr["number"], before=pr["merged_at"]))
            for r in audit_merge(qa, self.gh.check_conclusions(sha)):
                self.pause(f"PR {pr['number']}: {r}")
        self.state["audited"] = sorted(seen)[-200:]

    def tick(self, requests):
        for req in requests:
            self.handle(req)
        self.watch_releases()
        self.audit()
        self.watch_push_refusals()
        why = cost_problem(self.pc.tokens_today(), self.cfg.get("token_ceiling"))
        if why:
            self.pause(why)
        why = runs_problem(self.pc.runs_today(), self.cfg.get("max_runs_per_day"))
        if why:
            self.pause(why)
        paused = self.pc.is_paused()
        if self.pause_reasons and not paused:
            self.pc.pause("; ".join(self.pause_reasons))
            paused = True
        if paused:
            self.stop_work()
        for r in self.pause_reasons:
            self.log("PAUSED: " + r)
        if mint_due(self.state, self.now, paused):
            self.pc.put_secret("GITHUB_TOKEN", self.minter.mint())
            self.state["last_mint"] = self.now
            self.log("token refreshed")
        return paused


# ---------------------------------------------------------------- real seams

def _run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, check=True, **kw).stdout


class GhCli:
    """GitHub via the host's own `gh` login."""

    def api(self, path, method="GET", fields=None):
        cmd = ["gh", "api", "-X", method, path]
        for k, v in (fields or {}).items():
            cmd += ["-f", f"{k}={v}"]
        out = _run(cmd)
        return json.loads(out) if out.strip() else None

    def get_pr(self, n):
        try:
            return self.api(f"repos/{REPO}/pulls/{n}")
        except subprocess.CalledProcessError:
            return None

    def dispatch_stress(self, ref, test_class, n, suite):
        _run(["gh", "api", "-X", "POST", f"repos/{REPO}/actions/workflows/stress.yml/dispatches",
              "-f", f"ref={ref}", "-f", f"inputs[test_class]={test_class}",
              "-f", f"inputs[n]={n}", "-f", f"inputs[suite]={suite}"])

    def merged_bot_prs(self):
        prs = self.api(f"repos/{REPO}/pulls?state=closed&base=develop&sort=updated&direction=desc&per_page=30")
        return [p for p in prs if p.get("merged_at") and (p["user"] or {}).get("login") == BOT_LOGIN]

    def comment_bodies(self, n, before):
        out = []
        for path in (f"issues/{n}/comments", f"pulls/{n}/reviews", f"pulls/{n}/comments"):
            for c in self.api(f"repos/{REPO}/{path}?per_page=100") or []:
                when = c.get("created_at") or c.get("submitted_at") or ""
                if when <= before and c.get("body"):
                    out.append(c["body"])
        return out

    def check_conclusions(self, sha):
        data = self.api(f"repos/{REPO}/commits/{sha}/check-runs?per_page=100")
        return {c["name"]: c.get("conclusion") for c in data["check_runs"]}


def b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


class AppMinter:
    def __init__(self, app_dir, now=time.time):
        self.dir, self.now = app_dir, now

    def jwt(self, app_id):
        t = int(self.now())
        head = b64url(json.dumps({"alg": "RS256", "typ": "JWT"}).encode())
        body = b64url(json.dumps({"iat": t - 60, "exp": t + 540, "iss": str(app_id)}).encode())
        sig = subprocess.run(["openssl", "dgst", "-sha256", "-sign", os.path.join(self.dir, "app.pem")],
                             input=f"{head}.{body}".encode(), capture_output=True, check=True).stdout
        return f"{head}.{body}.{b64url(sig)}"

    def _call(self, jwt, path, method="GET", body=None):
        req = urllib.request.Request(
            "https://api.github.com" + path, method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Authorization": f"Bearer {jwt}", "Accept": "application/vnd.github+json"})
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.load(r)

    def mint(self):
        app = json.load(open(os.path.join(self.dir, "app.json")))
        jwt = self.jwt(app["app_id"])
        inst = self._call(jwt, f"/repos/{REPO}/installation")["id"]
        # Scoped to this one repository, on top of the App's own install scope.
        return self._call(jwt, f"/app/installations/{inst}/access_tokens", "POST",
                          {"repositories": [REPO.split("/")[1]]})["token"]


class PaperclipHttp:
    def __init__(self, login_file, company_id):
        kv = dict(l.split(": ", 1) for l in open(login_file).read().splitlines() if ": " in l)
        self.base, self.company = kv["url"].rstrip("/"), company_id
        self.creds = {"email": kv["email"], "password": kv["password"]}
        import http.cookiejar
        self.jar = http.cookiejar.CookieJar()
        self.op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.call("POST", "/api/auth/sign-in/email", self.creds)

    def call(self, method, path, body=None):
        # Every POST/PATCH needs an Origin header (plan, lead's brief).
        req = urllib.request.Request(
            self.base + path, method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Content-Type": "application/json", "Origin": self.base})
        with self.op.open(req, timeout=30) as r:
            raw = r.read()
        return json.loads(raw) if raw else None

    def put_secret(self, name, value):
        for s in self.call("GET", f"/api/companies/{self.company}/secrets"):
            if s.get("name") == name and s.get("status") != "deleted":
                self.call("POST", f"/api/secrets/{s['id']}/rotate", {"value": value})
                return
        self.call("POST", f"/api/companies/{self.company}/secrets", {"name": name, "value": value})

    def is_paused(self):
        return self.call("GET", f"/api/companies/{self.company}").get("status") == "paused"

    def pause(self, reason):
        self.call("PATCH", f"/api/companies/{self.company}", {"status": "paused"})

    def _runs(self):
        return self.call("GET", f"/api/companies/{self.company}/heartbeat-runs?limit=200") or []

    def runs_today(self):
        day = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d")
        return sum(1 for r in self._runs() if str(r.get("createdAt", "")) >= day)

    def active_run_ids(self):
        return [r["id"] for r in self._runs() if r.get("status") in ("queued", "running")]

    def cancel_run(self, run_id):
        self.call("POST", f"/api/heartbeat-runs/{run_id}/cancel", {})

    def in_progress_issue_ids(self):
        rows = self.call("GET", f"/api/companies/{self.company}/issues?status=in_progress") or []
        rows = rows if isinstance(rows, list) else rows.get("issues", [])
        return [i["id"] for i in rows if i.get("status") == "in_progress"]

    def block_issue(self, issue_id):
        self.call("PATCH", f"/api/issues/{issue_id}", {"status": "blocked"})

    def cancelled_issue_ids(self):
        rows = self.call("GET", f"/api/companies/{self.company}/issues?status=cancelled") or []
        rows = rows if isinstance(rows, list) else rows.get("issues", [])
        return [i["id"] for i in rows if i.get("status") == "cancelled"]

    def recent_run_ids(self):
        return [r["id"] for r in self._runs()][:30]

    def run_log(self, run_id):
        return json.dumps(self.call("GET", f"/api/heartbeat-runs/{run_id}/log?limitBytes=200000"))

    def tokens_today(self):
        day = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT00:00:00Z")
        rows = self.call("GET", f"/api/companies/{self.company}/costs/by-agent?from={day}") or []
        return sum(r.get("inputTokens", 0) + r.get("cachedInputTokens", 0) + r.get("outputTokens", 0)
                   for r in rows)


# ---------------------------------------------------------------- spool, main

def read_spool(spool, log):
    """Valid requests from spool/*.json; each file is moved to done/ or bad/."""
    out = []
    for sub in ("done", "bad"):
        os.makedirs(os.path.join(spool, sub), exist_ok=True)
    for path in sorted(glob.glob(os.path.join(spool, "*.json"))):
        try:
            with open(path) as f:
                raw = f.read(4096)
        except OSError:
            continue
        req, why = parse_request(raw)
        dest = "done" if req else "bad"
        if req:
            out.append(req)
        else:
            log(f"dropped {os.path.basename(path)}: {why}")
        os.replace(path, os.path.join(spool, dest, os.path.basename(path)))
    return out


def run_once(config_path):
    cfg = json.load(open(config_path))
    d = cfg.get("dir", DEFAULT_DIR)
    state_path = os.path.join(d, "state.json")
    try:
        state = json.load(open(state_path))
    except (OSError, ValueError):
        state = {}
    logf = open(os.path.join(d, "warden.log"), "a")

    def log(msg):
        logf.write(f"{dt.datetime.now(dt.timezone.utc).isoformat()} {msg}\n")
        logf.flush()

    w = Warden(cfg, GhCli(), AppMinter(d), PaperclipHttp(cfg["paperclip_login"], cfg["company_id"]),
               state, time.time(), log)
    paused = w.tick(read_spool(cfg["spool"], log))
    tmp = state_path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump(w.state, f)
    os.replace(tmp, state_path)
    return 3 if paused else 0


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("command", choices=["run-once"])
    p.add_argument("--config", default=os.path.join(DEFAULT_DIR, "warden.json"))
    return run_once(p.parse_args(argv).config)


if __name__ == "__main__":
    sys.exit(main())
