#!/usr/bin/env python3
"""The warden: the bug-fix org's host-side keeper (CB-455, plan §2, §4, §6).

    python3 host/warden.py run-once [--config ~/.config/orbweaver-bugfix/warden.json]

A launchd job (host/launchd/) runs `run-once` every five minutes, outside the
Paperclip sandbox. Each tick it:

  1. reads requests the org left in the spool folder (fixed schema, closed list
     of verbs) and acts on the ones it recognises;
  2. audits bot merges into develop;
  3. mints a one-hour installation token and writes it into the fix company's
     GITHUB_TOKEN secret, if the last one is older than 10 minutes and the
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
# A run reads GH_TOKEN once, when it starts, and an installation token lives one
# hour. Minting every ten minutes on a five-minute tick means a run starts with
# at least 45 minutes of token left; at 45 minutes it could start with ten, and
# an engineer's push at the end of a long run would be refused.
MINT_INTERVAL = 10 * 60
VERBS = ("dispatch_stress", "released", "push_refused", "rerun_failed")
CLASS_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]{0,199}$")
SUITES = ("unit", "integration", "ui")
MAX_N = 50
RELEASES_IN_A_ROW = 2
AUDIT_FAILURES_BEFORE_PAUSE = 3
DEFAULT_DIR = os.path.expanduser("~/.config/orbweaver-bugfix")
# A ticket's time limit in the org (skill). A release seen within this long of
# any pause may have been caused by the pause, not by the ticket (CB-472).
TICKET_WINDOW = 90 * 60
PAUSES_FILE = "pauses.json"
PAUSES_KEPT = 20


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
    if verb == "rerun_failed":
        pr = req.get("pr")
        if not (isinstance(pr, int) and not isinstance(pr, bool) and pr > 0):
            return None, "pr must be a positive integer"
        return {"verb": verb, "pr": pr}, None
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


# A rerun of a red CI run's failed jobs (CB-465). The org's App holds Actions
# read only, so it cannot rerun a job itself; a flake in a test its change does
# not touch then cost a whole ticket (CB-387). The warden reruns on request, at
# most once per head sha: a second red at the same sha is real, and a flake
# cannot be retried until it passes.
MAX_RERUNS_PER_SHA = 1
RED_CONCLUSIONS = ("failure", "cancelled", "timed_out")


def rerun_problem(pr, run, reruns):
    """Why the warden must NOT rerun this PR's CI (None = go). `run` is the
    latest CI workflow run at the PR's head, or None; `reruns` maps a head sha
    to the reruns already made there. Same lane as a stress dispatch."""
    why = dispatch_problem(pr)
    if why:
        return why
    sha = (pr.get("head") or {}).get("sha")
    if not sha:
        return "PR head has no sha"
    if reruns.get(sha, 0) >= MAX_RERUNS_PER_SHA:
        return f"already reran once at {sha[:9]}; a second red there is real"
    if run is None:
        return "no CI run at the PR's head"
    if run.get("head_sha") != sha:
        return "the latest CI run is not at the PR's head"
    if run.get("status") != "completed":
        return "CI is still running"
    if run.get("conclusion") not in RED_CONCLUSIONS:
        return f"CI is not red ({run.get('conclusion')})"
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


def ci_settled(ci):
    """True once every required check on a merge commit has a conclusion.
    Until then the merge stays pending: CI takes longer than a tick, so a
    merge seen with its checks queued has not been audited yet (CB-460)."""
    return all(ci.get(n) is not None for n in REQUIRED_CHECKS)


# A bot merge whose required checks have not concluded this long after the
# warden first saw it is itself a reason to pause: something stopped CI, and
# the org should not keep merging on top of an unknown develop.
PENDING_CI_LIMIT = 6 * 60 * 60


def next_release_state(state, event):
    """Counter of consecutive releases; a merge breaks the run."""
    n = state.get("consecutive_releases", 0)
    return n + 1 if event == "released" else 0


def cost_problem(tokens, ceiling):
    if ceiling is None:
        return None
    return f"daily tokens {tokens} passed ceiling {ceiling}" if tokens > ceiling else None


# git's own refusal output, not the bare code. The lead's instructions say
# "stop if a push is refused (GH013)", so the string GH013 is in nearly every
# engineer run's log; matching it alone paused the org on 2026-11-24 with no
# push refused at all (CB-462). These are the two forms git prints for a
# ruleset refusal: the remote's error line and the push summary.
REFUSAL_RE = re.compile(
    r"remote: error: GH013: Repository rule violations"
    r"|\(push declined due to repository rule violations\)")


def new_releases(cancelled_ids, state):
    """Ids newly cancelled since the last look: releases, as seen by Paperclip
    itself rather than reported from the sandbox. The first look only seeds the
    set (what is already cancelled is history, not a release)."""
    seen = state.get("cancelled_seen")
    ids = sorted(set(cancelled_ids))
    state["cancelled_seen"] = sorted(set(seen or []) | set(ids))[-500:]
    if seen is None:
        return []
    return [i for i in ids if i not in seen]


TICKET_RE = re.compile(r"\ATicket: (CB-\d+)(?:\n|\Z)")


def classify_release(row, fetch):
    """('release', key) | ('other', None) | ('unreadable', None) for one newly
    cancelled issue row.

    A row from the listing that carries a `description` key is decided from it.
    A row with no `description` key at all (the listing may omit it) is read in
    full through `fetch(id)`; skipping it would blind the brake and counting it
    blindly would pause on a QA child. If the full issue has no description key
    either, the signal cannot be read and the caller pauses."""
    if "description" not in row:
        try:
            row = fetch(row["id"]) or {}
        except Exception:
            return "unreadable", None
        if "description" not in row:
            return "unreadable", None
    m = TICKET_RE.match(row.get("description") or "")
    return ("release", m.group(1)) if m else ("other", None)


def run_log_text(resp):
    """The text of a run log from GET /api/heartbeat-runs/<id>/log. Measured
    shape: {runId, store, logRef, content}, `content` an NDJSON string of
    {ts, stream, chunk}. Lines that do not parse are kept as raw text, so an
    odd line cannot hide a refusal."""
    if not isinstance(resp, dict) or not isinstance(resp.get("content"), str):
        return ""
    out = []
    for line in resp["content"].splitlines():
        try:
            rec = json.loads(line)
        except ValueError:
            out.append(line)
            continue
        out.append(str(rec.get("chunk", "")) if isinstance(rec, dict) else line)
    return "".join(out)


def log_shows_refusal(text):
    return bool(REFUSAL_RE.search(text or ""))


def runs_problem(count, cap):
    """Daily run brake. Paperclip's heartbeat.maxDailyRuns does gate assignment
    wakes (measured in phase 0), but it defers them rather than dropping them:
    they all fire when the cap resets at UTC midnight. So the warden counts runs
    itself and pauses the company, which is what stops that backlog burst."""
    if cap is None:
        return None
    return f"{count} runs started today, cap {cap}" if count > cap else None


def note_pause_state(windows, paused, now):
    """Pause windows [{paused_at, resumed_at}] (epoch seconds) after a tick
    that saw the company `paused` or not. A window opens on the first tick that
    sees a pause, whoever made it, and closes on the first tick that sees the
    company active again, so both ends are within one tick of the truth."""
    windows = [dict(w) for w in windows]
    open_ = windows and windows[-1].get("resumed_at") is None
    if paused and not open_:
        windows.append({"paused_at": now, "resumed_at": None})
    elif not paused and open_:
        windows[-1]["resumed_at"] = now
    return windows[-PAUSES_KEPT:]


def paused_within(windows, now, span=TICKET_WINDOW):
    """True when any pause window overlaps the `span` seconds before now. A
    ninety-minute limit does not stop while the company is paused, so a
    release seen soon after a pause may be the pause's doing (CB-472)."""
    for w in windows:
        resumed = w.get("resumed_at")
        if w["paused_at"] <= now and (resumed is None or resumed >= now - span):
            return True
    return False


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
            try:
                self.gh.dispatch_stress(pr["head"]["ref"], req["test_class"], req["n"], req["suite"])
            except (subprocess.CalledProcessError, OSError) as e:
                return self.log(f"dispatch for PR {req['pr']} failed: {type(e).__name__}")
            self.log(f"dispatched stress.yml for PR {req['pr']} {req['test_class']} x{req['n']}")
        elif v == "released":
            self.state["consecutive_releases"] = next_release_state(self.state, "released")
            self.log(f"{req['ticket']} released ({self.state['consecutive_releases']} in a row)")
            if self.state["consecutive_releases"] >= RELEASES_IN_A_ROW:
                self.pause(f"{RELEASES_IN_A_ROW} tickets released in a row")
        elif v == "push_refused":
            self.pause("GitHub refused a bot push on a ruleset")
        elif v == "rerun_failed":
            self.rerun_failed(req["pr"])

    def rerun_failed(self, n):
        """Rerun the failed jobs of PR n's latest CI run, once per head sha
        (CB-465). A refusal is logged and never pauses: asking is not a fault."""
        reruns = self.state.setdefault("reruns", {})
        try:
            pr = self.gh.get_pr(n)
            sha = ((pr or {}).get("head") or {}).get("sha")
            run = self.gh.latest_ci_run(sha) if sha and not dispatch_problem(pr) else None
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError) as e:
            return self.log(f"rerun for PR {n} unreadable: {type(e).__name__}")
        why = rerun_problem(pr, run, reruns)
        if why:
            return self.log(f"refused rerun for PR {n}: {why}")
        try:
            self.gh.rerun_failed(run["id"])
        except (subprocess.CalledProcessError, OSError) as e:
            return self.log(f"rerun for PR {n} failed: {type(e).__name__}")
        reruns[sha] = reruns.get(sha, 0) + 1
        while len(reruns) > 200:
            reruns.pop(next(iter(reruns)))
        self.log(f"reran failed jobs of CI run {run['id']} for PR {n} at {sha[:9]}")

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
        the sandbox short of not releasing at all. Only issues that are newly
        cancelled are read in full, so a long cancelled history costs nothing."""
        rows = {r["id"]: r for r in self.pc.cancelled_issue_rows()}
        for issue_id in new_releases(list(rows), self.state):
            kind, key = classify_release(rows[issue_id], self.pc.get_issue)
            if kind == "unreadable":
                self.log(f"release signal unreadable for issue {issue_id[:8]}")
                self.pause(f"release signal unreadable (issue {issue_id[:8]} has no readable description)")
            elif kind == "release":
                # One release per ticket per fix pass. Releasing a ticket after
                # its second fix round cancels both engineer issues, and counting
                # each paused the org on a single release (CB-471). The pass is
                # the parent issue; a row with no parent cannot be matched to
                # one, so it is counted, which errs towards the brake.
                parent = rows[issue_id].get("parentId")
                counted = self.state.setdefault("paperclip_release_passes", [])
                if parent and [key, parent] in counted:
                    self.log(f"{key} already counted as released in this pass (issue {issue_id[:8]})")
                    continue
                if parent:
                    counted.append([key, parent])
                    del counted[:-50]
                # Checked after the pass is recorded, so a second engineer
                # issue of the same pass seen later is still deduplicated.
                if paused_within(self.state.get("pauses", []), self.now):
                    self.log(f"{key} released, not counted: the company was paused during this "
                             f"ticket's window (issue {issue_id[:8]})")
                    continue
                n = self.state["paperclip_releases"] = self.state.get("paperclip_releases", 0) + 1
                self.log(f"{key} released (Paperclip issue cancelled; {n} in a row)")
                if n >= RELEASES_IN_A_ROW:
                    self.pause(f"{RELEASES_IN_A_ROW} releases in a row (Paperclip issue states)")

    def note_pauses(self, paused):
        """Record this tick's pause state (CB-472), counting a pause the tick
        is about to make, and publish the windows to the org. Failing to write
        the file must never stop the tick before it pauses."""
        self.state["pauses"] = note_pause_state(self.state.get("pauses", []), paused, self.now)
        if self.cfg.get("spool"):
            try:
                write_pauses(self.cfg["spool"], self.state["pauses"])
            except OSError as e:
                self.log(f"{PAUSES_FILE} not written: {type(e).__name__}")

    def watch_push_refusals(self):
        """GitHub's ruleset refusal text in a run's log means an agent tried to
        leave its lane. Each run is read once. See host/README.md for what this
        does and does not see."""
        scanned = set(self.state.get("scanned_runs", []))
        for run_id in self.pc.recent_run_ids():
            if run_id in scanned:
                continue
            try:
                text = self.pc.run_log(run_id)
            except Exception as e:  # not seen, not paused: retried next tick
                self.log(f"run log for {run_id[:8]} unreadable: {type(e).__name__}")
                continue
            scanned.add(run_id)
            if log_shows_refusal(text):
                self.pause(f"run {run_id[:8]} log shows GitHub refusing a push (GH013)")
        self.state["scanned_runs"] = sorted(scanned)[-500:]

    def safe_audit(self):
        """A GitHub outage must not stop the tick (no pause, no token refresh,
        no run cancelling), but a brake that stays blind is no brake: three
        failed audits in a row pause the company."""
        try:
            self.audit()
            self.state["audit_failures"] = 0
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError) as e:
            n = self.state["audit_failures"] = self.state.get("audit_failures", 0) + 1
            self.log(f"merge audit unreadable ({type(e).__name__}); {n} in a row")
            if n >= AUDIT_FAILURES_BEFORE_PAUSE:
                self.pause(f"merge audit unreadable {n} ticks in a row")

    def audit(self):
        """Audit bot merges into develop. The QA comment and the release-counter
        reset happen once, when a merge is first seen. CI is re-read every tick
        until both required checks have concluded: the first tick after a merge
        almost always sees them queued, and marking the merge audited then
        meant a red develop never paused the org (CB-460)."""
        seen = set(self.state.setdefault("audited", []))
        pending = dict(self.state.get("audit_pending", {}))
        for pr in self.gh.merged_bot_prs():
            sha = pr["merge_commit_sha"]
            if sha in seen or sha in pending:
                continue
            self.state["consecutive_releases"] = next_release_state(self.state, "merged")
            self.state["paperclip_releases"] = 0
            qa = has_qa_comment(self.gh.comment_bodies(pr["number"], before=pr["merged_at"]))
            for r in audit_merge(qa, {}):
                self.pause(f"PR {pr['number']}: {r}")
            pending[sha] = {"pr": pr["number"], "since": self.now}
        for sha, entry in list(pending.items()):
            ci = self.gh.check_conclusions(sha)
            red = audit_merge(True, ci)
            for r in red:
                self.pause(f"PR {entry['pr']}: {r}")
            if red or ci_settled(ci):
                del pending[sha]
                seen.add(sha)
            elif self.now - entry.get("since", self.now) >= PENDING_CI_LIMIT:
                self.pause(f"PR {entry['pr']}: develop CI never finished on a bot merge")
                del pending[sha]
                seen.add(sha)
        self.state["audit_pending"] = pending
        self.state["audited"] = sorted(seen)[-200:]

    def tick(self, requests):
        for req in requests:
            self.handle(req)
        self.watch_releases()
        self.safe_audit()
        self.watch_push_refusals()
        why = cost_problem(self.pc.tokens_today(), self.cfg.get("token_ceiling"))
        if why:
            self.pause(why)
        why = runs_problem(self.pc.runs_today(), self.cfg.get("max_runs_per_day"))
        if why:
            self.pause(why)
        paused = self.pc.is_paused()
        self.note_pauses(paused or bool(self.pause_reasons))
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

    def api(self, path, method="GET"):
        out = _run(["gh", "api", "-X", method, path])
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

    def latest_ci_run(self, sha):
        """The newest run of ci.yml at this sha from a pull_request, or None."""
        data = self.api(f"repos/{REPO}/actions/runs?head_sha={sha}&event=pull_request&per_page=30") or {}
        runs = [r for r in data.get("workflow_runs", [])
                if str(r.get("path", "")).endswith("/ci.yml") or r.get("name") == "CI"]
        runs.sort(key=lambda r: r.get("created_at") or "", reverse=True)
        return runs[0] if runs else None

    def rerun_failed(self, run_id):
        _run(["gh", "api", "-X", "POST", f"repos/{REPO}/actions/runs/{int(run_id)}/rerun-failed-jobs"])

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
    def __init__(self, app_dir, now=time.time, api="https://api.github.com"):
        self.dir, self.now, self.api = app_dir, now, api

    def jwt(self, app_id):
        t = int(self.now())
        head = b64url(json.dumps({"alg": "RS256", "typ": "JWT"}).encode())
        body = b64url(json.dumps({"iat": t - 60, "exp": t + 540, "iss": str(app_id)}).encode())
        sig = subprocess.run(["openssl", "dgst", "-sha256", "-sign", os.path.join(self.dir, "app.pem")],
                             input=f"{head}.{body}".encode(), capture_output=True, check=True).stdout
        return f"{head}.{body}.{b64url(sig)}"

    def _call(self, jwt, path, method="GET", body=None):
        req = urllib.request.Request(
            self.api + path, method=method,
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

    def cancelled_issue_rows(self):
        rows = self.call("GET", f"/api/companies/{self.company}/issues?status=cancelled") or []
        rows = rows if isinstance(rows, list) else rows.get("issues", [])
        return [r for r in rows if r.get("status") == "cancelled"]

    def get_issue(self, issue_id):
        return self.call("GET", f"/api/issues/{issue_id}")

    def recent_run_ids(self):
        return [r["id"] for r in self._runs()][:30]

    def run_log(self, run_id):
        """Log text, or "" for a run that has none: a run that never started
        (cancelled by a cap) answers 404 "Heartbeat run not found"."""
        try:
            resp = self.call("GET", f"/api/heartbeat-runs/{run_id}/log?limitBytes=200000")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return ""
            raise
        return run_log_text(resp)

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
        if os.path.basename(path) == PAUSES_FILE:
            continue  # the warden's own answer to the org, not a request
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


def iso(t):
    return dt.datetime.fromtimestamp(t, dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_pauses(spool, windows):
    """<spool>/pauses.json for the sandboxed lead: the same windows as UTC
    ISO strings, oldest first. Written whole and renamed into place so a
    reader never sees half a file, 0644 because the org only reads it."""
    rows = [{"paused_at": iso(w["paused_at"]),
             "resumed_at": None if w.get("resumed_at") is None else iso(w["resumed_at"])}
            for w in windows]
    path = os.path.join(spool, PAUSES_FILE)
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644)
    with os.fdopen(fd, "w") as f:
        json.dump(rows, f, indent=1)
        f.write("\n")
    os.chmod(tmp, 0o644)
    os.replace(tmp, path)


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

    w = Warden(cfg, GhCli(), AppMinter(d, api=cfg.get("github_api", "https://api.github.com")),
               PaperclipHttp(cfg["paperclip_login"], cfg["company_id"]),
               state, time.time(), log)
    try:
        paused = w.tick(read_spool(cfg["spool"], log))
    finally:
        logf.close()
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
