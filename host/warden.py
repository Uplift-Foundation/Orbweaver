#!/usr/bin/env python3
"""The warden: the bug-fix org's host-side keeper (CB-455, plan §2, §4, §6).

    python3 host/warden.py run-once [--config ~/.config/orbweaver-bugfix/warden.json]

A launchd job (host/launchd/) runs `run-once` every five minutes, outside the
Paperclip sandbox. Each tick it:

  1. reads requests the org left in the spool folder (fixed schema, closed list
     of verbs) and acts on the ones it recognises;
  2. audits bot merges into each configured repository's develop, and runs
     the workflow-file guard on every open org PR at each new head (CB-481);
  3. mints one-hour installation tokens and writes them into the fix company's
     secrets, if the last ones are older than 10 minutes and the company is not
     paused (a paused org stops being refreshed, so its access lapses by
     itself): GITHUB_TOKEN for every configured repository the installation
     covers, and GITHUB_TOKEN_WORKFLOWS, which adds `workflows: write`, for
     Orbweaver only (CB-481);
  4. pauses the company on any trigger in plan §6;
  5. unless the company is paused, runs the unstick sweep (CB-479): closes a
     pass task left blocked on settled children, wakes and then cancels a
     stalled child task, and resets the session of a task whose pool account
     ran out once another account has room.

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
# The release brake is a cost brake, not a safety brake (CB-484): a pause whose
# only reasons are these two always lifts itself after BRAKE_COOL_OFF. The org
# must not freeze on it, so there is no limit on how often.
SPOOL_BRAKE = f"{RELEASES_IN_A_ROW} tickets released in a row"
PAPERCLIP_BRAKE = f"{RELEASES_IN_A_ROW} releases in a row (Paperclip issue states)"
BRAKE_COOL_OFF = 60 * 60
RESTART_COMMENT = ("Warden: the company was paused and is running again; the pause is in "
                   "pauses.json and does not count against this ticket's limit. Carry on.")
AUDIT_FAILURES_BEFORE_PAUSE = 3
DEFAULT_DIR = os.path.expanduser("~/.config/orbweaver-bugfix")
ACCESS_FILE = "access.json"

# The repositories the org works in (CB-481), from `repos` in warden.json. With
# no `repos` the warden uses DEFAULT_REPOS, today's desktop values only, so a
# config written before CB-481 and every older test behave exactly as before.
# Desktop keeps its state at the top of state.json (`audited`, `audit_pending`,
# `reruns`); any other repository keeps the same keys under state["repos"][key].
DESKTOP = {
    "key": "desktop", "repo": REPO, "base": "develop",
    "post_merge_checks": list(REQUIRED_CHECKS), "qa_names": list(RIDS),
    "ci_workflows": ["ci.yml"], "push_ci": True, "stress": True, "workflow_edits": True,
    "protected_workflows": ["release.yml", "publish-screenshots.yml"],
}
DEFAULT_REPOS = [DESKTOP]
# What warden.json's `repos` carries for the mobile repository, read off its
# workflows and last PRs (plan comment 11762). Here for the README and tests;
# the warden reads it only from the config.
MOBILE = {
    "key": "mobile", "repo": "Uplift-Foundation/Orbweaver-Mobile", "base": "develop",
    "post_merge_checks": [], "qa_names": ["relay", "contracts", "tools"],
    "head_checks": {"always": ["relay", "contracts", "tools"],
                    "paths": {"android": ["android/", "contracts/"],
                              "snapshots": ["app/", "contracts/frames-v7-vectors.json",
                                            "contracts/frames-v11-vectors.json"]}},
    "ci_workflows": ["relay-tests.yml", "android-tests.yml", "ios-snapshots.yml"],
    "push_ci": False, "stress": False, "workflow_edits": False,
}
REPO_KEY_RE = re.compile(r"^[a-z][a-z0-9-]{0,31}$")

# The everyday token's permissions, asked for by name so it never inherits a
# permission the App gains later (CB-481). Workflows is the one it must never
# carry: that lives on the second token alone, for desktop alone, because a
# workflow pushed to mobile could run on the mini's own runner with mobile's
# Docker Hub secrets (plan comment 11762, "Tokens").
EVERYDAY_PERMISSIONS = {"actions": "read", "checks": "read", "contents": "write",
                        "metadata": "read", "pull_requests": "write", "statuses": "read"}
WORKFLOW_PERMISSIONS = dict(EVERYDAY_PERMISSIONS, workflows="write")
WORKFLOW_SECRET = "GITHUB_TOKEN_WORKFLOWS"


def repos_from(cfg):
    """The configured repository entries, DEFAULT_REPOS when none are given."""
    return list(cfg.get("repos") or DEFAULT_REPOS)


def repo_by_key(cfg, key):
    return next((r for r in repos_from(cfg) if r.get("key") == key), None)
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
        out = {"verb": verb, "pr": pr, "test_class": cls, "n": n, "suite": suite}
        return _with_repo(out, req)
    if verb == "rerun_failed":
        pr = req.get("pr")
        if not (isinstance(pr, int) and not isinstance(pr, bool) and pr > 0):
            return None, "pr must be a positive integer"
        return _with_repo({"verb": verb, "pr": pr}, req)
    if verb == "released":
        t = req.get("ticket")
        if not (isinstance(t, str) and re.fullmatch(r"CB-\d{1,6}", t)):
            return None, "ticket must look like CB-123"
        return {"verb": verb, "ticket": t}, None
    return {"verb": verb}, None


def _with_repo(out, req):
    """An optional `repo` key (CB-481): absent means desktop. Its value is
    checked against the config when the request is handled."""
    if "repo" not in req:
        return out, None
    key = req["repo"]
    if not (isinstance(key, str) and REPO_KEY_RE.match(key)):
        return None, "repo outside pattern"
    return dict(out, repo=key), None


def dispatch_problem(pr, r=None):
    """Why the warden must NOT dispatch stress.yml for this PR (None = go).

    `pr` is the GitHub PR object, `r` the repository entry (desktop when
    None). "The org's own PRs only": opened by the bot, from a branch in that
    repository (a fork PR is the one way to run someone else's code on the
    runners at our expense), under the org's prefix, and still open.
    """
    r = r or DESKTOP
    if pr is None:
        return "PR not found"
    if pr.get("state") != "open":
        return "PR is not open"
    if (pr.get("user") or {}).get("login") != BOT_LOGIN:
        return "PR was not opened by the bug-fix App"
    head = pr.get("head") or {}
    if ((head.get("repo") or {}).get("full_name")) != r["repo"]:
        return "PR head is not in this repository"
    if not str(head.get("ref", "")).startswith(BRANCH_PREFIX):
        return f"head branch lacks the {BRANCH_PREFIX} prefix"
    if ((pr.get("base") or {}).get("ref")) != r["base"]:
        return f"PR does not target {r['base']}"
    return None


# A rerun of a red CI run's failed jobs (CB-465). The org's App holds Actions
# read only, so it cannot rerun a job itself; a flake in a test its change does
# not touch then cost a whole ticket (CB-387). The warden reruns on request, at
# most once per head sha: a second red at the same sha is real, and a flake
# cannot be retried until it passes.
MAX_RERUNS_PER_SHA = 1
RED_CONCLUSIONS = ("failure", "cancelled", "timed_out")


def rerun_targets(pr, runs, reruns, r=None):
    """(runs to rerun, None), or ([], why the warden must NOT rerun).

    `runs` is the latest run of each of the repository's CI workflows at the
    PR's head (desktop has one, ci.yml; mobile three); `reruns` maps a head sha
    to the reruns already made there. Same lane as a stress dispatch. Every
    completed red run is rerun together, and that counts as the one rerun the
    sha gets."""
    why = dispatch_problem(pr, r)
    if why:
        return [], why
    sha = (pr.get("head") or {}).get("sha")
    if not sha:
        return [], "PR head has no sha"
    if reruns.get(sha, 0) >= MAX_RERUNS_PER_SHA:
        return [], f"already reran once at {sha[:9]}; a second red there is real"
    runs = [x for x in runs if x]
    if not runs:
        return [], "no CI run at the PR's head"
    at_head = [x for x in runs if x.get("head_sha") == sha]
    if not at_head:
        return [], "the latest CI run is not at the PR's head"
    red = [x for x in at_head if x.get("status") == "completed" and x.get("conclusion") in RED_CONCLUSIONS]
    if red:
        return red, None
    if any(x.get("status") != "completed" for x in at_head):
        return [], "CI is still running"
    return [], "CI is not red (" + ", ".join(str(x.get("conclusion")) for x in at_head) + ")"


def rerun_problem(pr, run, reruns, r=None):
    """rerun_targets for one run: the reason, or None to go."""
    return rerun_targets(pr, [run], reruns, r)[1]


def has_qa_comment(bodies, names=RIDS):
    """A QA comment names every one of `names`: both rids on desktop, the
    checks it read on mobile. `bodies` are comment and review texts posted
    before the merge (the PR body itself is not one)."""
    return any(all(n in b for n in names) for b in bodies)


def audit_merge(has_qa, ci, r=None):
    """Pause reasons for one bot merge. `ci` maps check name -> conclusion
    for the merge commit. On desktop only the required checks count; a
    repository with no `post_merge_checks` (mobile, whose develop runs only
    relay-image on a push) counts any check that ends red. A check that is
    absent or pending is not red: checks may still be queued."""
    r = r or DESKTOP
    reasons = []
    if not has_qa:
        names = list(r["qa_names"])
        said = "both rids" if names == list(RIDS) else " and ".join(names)
        reasons.append(f"bot PR merged with no QA comment naming {said}")
    names = r["post_merge_checks"] or sorted(ci)
    failed = [n for n in names if ci.get(n) in RED_CONCLUSIONS]
    if failed:
        reasons.append(f"{r['base']} CI red on a bot merge: " + ", ".join(failed))
    return reasons


# How long a merge in a repository with no required post-merge checks stays
# pending, so a push-triggered run (mobile's relay-image) is seen to its end.
MERGE_QUIET = 30 * 60


def ci_settled(ci, r=None, age=0):
    """True once a merge commit's CI is done. Desktop: every required check
    has a conclusion. Until then the merge stays pending: CI takes longer than
    a tick, so a merge seen with its checks queued has not been audited yet
    (CB-460). A repository with no required checks: MERGE_QUIET seconds have
    passed since the merge was first seen and no check on it is pending."""
    r = r or DESKTOP
    if r["post_merge_checks"]:
        return all(ci.get(n) is not None for n in r["post_merge_checks"])
    return age >= MERGE_QUIET and all(v is not None for v in ci.values())


def expected_head_checks(paths, r):
    """The checks a PR's head must carry, from the paths it changed: the
    always-run ones plus each path-filtered check whose paths it touched
    (mobile's android and snapshots run only then)."""
    hc = r.get("head_checks") or {}
    out = list(hc.get("always", []))
    for name, prefixes in (hc.get("paths") or {}).items():
        if any(p.startswith(x) for p in paths for x in prefixes):
            out.append(name)
    return out


def head_checks_problem(ci, paths, r):
    """Pause reasons for a merged PR's head checks (CB-481). A path-filtered
    check cannot be required by a ruleset, because one that never starts would
    block every merge, so the warden audits it after the merge instead: every
    check the changed paths call for must be `success` at the head."""
    bad = []
    for name in expected_head_checks(paths, r):
        c = ci[name] if name in ci else "missing"
        if c != "success":
            bad.append(f"{name} {c or 'pending'}")
    return [f"merged with head checks not green: {', '.join(bad)}"] if bad else []


# ---------------------------------------------------------------- workflow guard (CB-481)
#
# The org may now change workflow files on desktop, with the Orbweaver-only
# workflow token. A PR's own CI runs the PR's copy of a workflow as soon as it
# is pushed, so the guard reads every open org PR at each new head sha, not
# only at merge. Any of these pauses the org, naming the PR, the file and the
# rule. It is a text match: it can pause on a harmless line (a pause costs one
# resume), and it cannot see a script a workflow calls.

WORKFLOW_DIRS = (".github/workflows/", ".github/actions/")
RISKY_WORDS = ("environment", "secrets", "pull_request_target", "workflow_run", "id-token",
               "permissions", ": write", "runs-on", "screenshot")
_USES = re.compile(r"""\buses:\s*['"]?([^\s'"#]+)""")


def is_workflow_path(path):
    return any(str(path or "").startswith(d) for d in WORKFLOW_DIRS)


def risky_line(text):
    """Why one added or removed workflow line is risky, or None. A comment is
    never risky; a `uses:` is unless it names `actions/...` or a local `./`."""
    t = text.strip()
    if not t or t.startswith("#"):
        return None
    low = t.lower()
    for w in RISKY_WORDS:
        if w in low:
            return f"mentions '{w.strip()}'"
    m = _USES.search(t)
    if m and not (m.group(1).startswith("actions/") or m.group(1).startswith("./")):
        return f"uses {m.group(1)}"
    return None


def workflow_problems(files, r):
    """Pause reasons for one PR's changed files (GET /pulls/<n>/files rows),
    one per file and rule, in the repository `r`."""
    out = []
    protected = set(r.get("protected_workflows") or ())
    for f in files:
        names = [f.get("filename"), f.get("previous_filename")]
        if not any(is_workflow_path(n) for n in names):
            continue
        name = f.get("filename")
        if not r.get("workflow_edits"):
            out.append(f"{name}: workflow change in {r['repo']}, where the org may not edit workflows")
            continue
        if any(os.path.basename(str(n or "")) in protected for n in names if is_workflow_path(n)):
            out.append(f"{name}: change to a protected workflow")
            continue
        if f.get("status") != "modified":
            out.append(f"{name}: workflow file {f.get('status')}")
            continue
        patch = f.get("patch")
        if not isinstance(patch, str):
            out.append(f"{name}: GitHub returned no patch, so the change cannot be read")
            continue
        for line in patch.splitlines():
            if line[:1] not in "+-" or line.startswith(("+++", "---")):
                continue
            why = risky_line(line[1:])
            if why:
                out.append(f"{name}: changed line {why}: {line[:120]}")
                break
    return out


# ---------------------------------------------------------------- tokens (CB-481)

def gated_branches(r):
    """The branches a ruleset must protect before `r` joins the token: its
    default branch and its base (lead ruling: both main and develop)."""
    return list(dict.fromkeys(["main", r["base"]]))


def ruleset_gate(rulesets, branch_rules):
    """Why a repository must NOT go on the everyday token (None = go), from
    GET /repos/<repo>/rulesets and, per branch in `branch_rules`,
    GET /repos/<repo>/rules/branches/<branch>.

    The token can push, so a repository joins it only once rulesets keep the
    org off both its default branch and its base (lead rulings): each branch
    must carry a `pull_request` rule from a ruleset listed as active. Measured
    on Orbweaver 2026-11-26: the branch-rules read names each rule's `type` and
    `ruleset_id`; the rulesets listing gives each ruleset's `enforcement`. A
    deletion-only rule is not enough, and a rule from a ruleset not listed as
    active does not count."""
    if not rulesets:
        return "no rulesets"
    active = {r.get("id") for r in rulesets if r.get("enforcement") == "active"}
    if not active:
        return "no active ruleset"
    bare = [b for b, rules in branch_rules.items()
            if not any(x.get("type") == "pull_request" and x.get("ruleset_id") in active for x in rules or [])]
    if bare:
        return "no active pull_request rule on " + " and ".join(bare)
    return None

def token_plan(repos, access):
    """Which repositories each token names. `access` maps a repository key to
    the installation object GitHub returns for that repository, or None when
    the installation does not cover it.

    everyday   every configured repository the installation covers
    workflows  only those of them with `workflow_edits` (desktop), and only
               while the installation shows `workflows: write`; else []
    """
    covered = [r for r in repos if access.get(r["key"])]
    if not covered:
        return {"installation": None, "everyday": [], "workflows": []}
    inst = access[covered[0]["key"]]
    # One installation holds them all; a repository under another is left out.
    covered = [r for r in covered if access[r["key"]].get("id") == inst.get("id")]
    has_wf = (inst.get("permissions") or {}).get("workflows") == "write"
    names = lambda rs: [x["repo"].split("/")[1] for x in rs]
    return {"installation": inst.get("id"),
            "everyday": names(covered),
            "workflows": names([x for x in covered if x.get("workflow_edits")]) if has_wf else []}


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


def is_release_brake(reasons):
    """True when every pause reason is the release brake (and there is one)."""
    return bool(reasons) and all(r in (SPOOL_BRAKE, PAPERCLIP_BRAKE) for r in reasons)


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


# ---------------------------------------------------------------- unstick (CB-479)
#
# Every stall of 2026-11-25 was cleared by hand with the board login: a pass
# task left `blocked` after its children were cancelled on a release (Paperclip
# counts cancelled children as open blockers and refuses the lead's `done`), a
# child task sitting with no run, a task whose last run was cancelled because
# its pool account was exhausted. The sweep makes those same moves itself.
# `unstick_actions` decides; Warden.unstick only carries the answer out.

SETTLED = ("done", "cancelled")
STALLABLE = ("todo", "in_progress", "in_review")
LIVE_RUN = ("queued", "running")
PARENT_IDLE = 10 * 60        # a blocked parent untouched this long is looked at
PARENT_WAKE_GRACE = 15 * 60  # after waking it, closed if still blocked
CHILD_IDLE = 20 * 60         # a child with no run and no activity this long is stalled
CHILD_WAKES = 2              # wakes before the sweep cancels it
RESET_EVERY = 60 * 60        # one session reset per task per hour
USAGE_FRESH = 60 * 60        # a usage cancel older than this no longer explains a stall
WAKE_MARGIN = 60             # activity this long after a wake is the task's own, not our comment
ISSUE_PAGE = 1000            # the listing size asked for; a full page may be truncated
POOL_HEADROOM = 90           # an account under this on every limit can take work
POOL_USAGE = os.path.expanduser("~/.paperclip/instances/default/pool-usage.json")
USAGE_LIMIT_CODE = "ai_connection_pool_exhausted"


def ts(s):
    """Epoch seconds from Paperclip's ISO timestamps, None when absent."""
    if not s:
        return None
    try:
        return dt.datetime.fromisoformat(str(s).replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def activity_at(issue):
    """The newest of lastActivityAt and updatedAt; a comment, status change or
    run moves one of them."""
    t = [x for x in (ts(issue.get("lastActivityAt")), ts(issue.get("updatedAt"))) if x is not None]
    return max(t) if t else None


def live_issue_ids(issues, runs):
    """Issues with a run in flight: the row's own `activeRun` (measured: null
    when none), or a queued/running run naming the issue."""
    live = {i["id"] for i in issues if i.get("activeRun")}
    live |= {r.get("issueId") for r in runs if r.get("status") in LIVE_RUN and r.get("issueId")}
    return live


def usage_cancelled(run):
    """A run the pool router cancelled because every account was exhausted
    (CB-469). Measured on the live org: status cancelled, errorCode
    ai_connection_pool_exhausted, error "All eligible pool accounts are over
    their usage limit. ..."."""
    return run.get("status") == "cancelled" and (
        run.get("errorCode") == USAGE_LIMIT_CODE
        or "over their usage limit" in str(run.get("error") or ""))


def pool_has_headroom(pool, threshold=POOL_HEADROOM):
    """True when pool-usage.json names an account that is `ok` with no limit at
    or over `threshold` percent and none reached. A limit with no number is not
    counted against it; an account with no numbers at all is not counted for it."""
    for acct in ((pool or {}).get("accounts") or {}).values():
        if not isinstance(acct, dict) or acct.get("status") != "ok":
            continue
        limits = acct.get("limits") or []
        pcts = [l.get("usedPercent") for l in limits if isinstance(l.get("usedPercent"), (int, float))]
        if pcts and max(pcts) < threshold and not any(l.get("limitReached") for l in limits):
            return True
    return False


def unstick_actions(issues, runs, comments_at, state, now, pool_ok):
    """The sweep's decision for one company, as a list of action dicts
    {kind, id, identifier, ...}, at most one per issue.

    issues      every issue row of the company
    runs        recent heartbeat runs of the company
    comments_at {issue id: epoch of the assignee's newest comment}, for blocked parents
    state       the warden's `unstick` state (read only here)
    now         epoch seconds
    pool_ok     pool_has_headroom() of the current pool-usage.json

    Kinds: close_parent, wake_parent, reset_session, wake_child, cancel_child."""
    live = live_issue_ids(issues, runs)
    kids = {}
    for i in issues:
        if i.get("parentId"):
            kids.setdefault(i["parentId"], []).append(i)
    latest_run = {}
    for r in runs:
        iid = r.get("issueId")
        if iid and (iid not in latest_run or (r.get("createdAt") or "") > (latest_run[iid].get("createdAt") or "")):
            latest_run[iid] = r
    woken = state.get("parent_woken", {})
    wakes = state.get("child_wakes", {})
    resets = state.get("resets", {})
    out = []
    for i in issues:
        iid, status = i["id"], i.get("status")
        if status in SETTLED or iid in live or i.get("hiddenAt"):
            continue
        act = activity_at(i)
        idle = now - act if act is not None else None
        base = {"id": iid, "identifier": i.get("identifier") or iid[:8]}

        # 3. Tied to an exhausted account. Decided first, but only while the
        # usage cancel is under an hour old: then the task is waiting on usage,
        # not stalled, and rules 1 and 2 leave it alone. An older one explains
        # nothing any more, so a blocked pass whose children settled since is
        # closed by rule 1 instead of being reset every hour (QA, CB-479).
        run = latest_run.get(iid)
        made = ts(run.get("createdAt")) if run else None
        if run and usage_cancelled(run) and made is not None and now - made < USAGE_FRESH:
            last = resets.get(iid)
            if pool_ok and run.get("agentId") and (last is None or now - last >= RESET_EVERY):
                key = (run.get("contextSnapshot") or {}).get("taskKey") or iid
                out.append(dict(base, kind="reset_session", agent_id=run["agentId"], task_key=key))
            continue

        # 1. Parent blocked on settled children.
        if status == "blocked":
            children = kids.get(iid, [])
            if (not children or any(c.get("status") not in SETTLED for c in children)
                    or any(c["id"] in live for c in children)
                    or idle is None or idle < PARENT_IDLE):
                continue
            settled_at = max((activity_at(c) or 0) for c in children)
            said = comments_at.get(iid)
            if said is not None and said > settled_at:
                out.append(dict(base, kind="close_parent", why="assignee reported after its children settled"))
            elif iid in woken:
                if now - woken[iid] >= PARENT_WAKE_GRACE:
                    out.append(dict(base, kind="close_parent", why="still blocked after a wake"))
            else:
                out.append(dict(base, kind="wake_parent"))
            continue

        # 2. Stalled child.
        if (status in STALLABLE and i.get("parentId") and i.get("assigneeAgentId")
                and idle is not None and idle >= CHILD_IDLE):
            n, at = (wakes.get(iid) or [0, None])
            if at is not None and act > at + WAKE_MARGIN:
                # It worked after the last wake: a new stall starts the count again.
                n, at = 0, None
            if at is not None and now - at < CHILD_IDLE:
                continue
            if n < CHILD_WAKES:
                out.append(dict(base, kind="wake_child", wakes=n + 1))
            else:
                out.append(dict(base, kind="cancel_child", parent_id=i["parentId"]))
    return out


def prune_unstick_state(state, issues, now):
    """Forget what no longer applies: a parent that is no longer blocked, a
    child that has settled, a reset older than the hour it guards. A task that
    comes back later starts again from a wake."""
    by_id = {i["id"]: i.get("status") for i in issues}
    state["parent_woken"] = {k: v for k, v in state.get("parent_woken", {}).items()
                             if by_id.get(k) == "blocked"}
    state["child_wakes"] = {k: v for k, v in state.get("child_wakes", {}).items()
                            if by_id.get(k) not in SETTLED and k in by_id}
    state["resets"] = {k: v for k, v in state.get("resets", {}).items() if now - v < RESET_EVERY}
    return state


WAKE_PARENT = ("Warden: this task is blocked, every child is done or cancelled, and nothing "
               "has run for 10 minutes. If the pass is finished, post your report; if not, "
               "say what is still open. Still blocked in 15 minutes, the warden marks it done.")
WAKE_CHILD = ("Warden: no run and no activity on this task for 20 minutes. Carry on, or "
              "say where it stands (wake {n} of 2; after that the warden cancels it).")
WAKE_PARENT_AFTER_CANCEL = ("Warden: cancelled {child} after two unanswered wakes. It is not a "
                            "release. Decide whether to reassign it or finish the pass without it.")
WAKE_AFTER_RESET = ("Warden: the last run on this task was cancelled because its pool account "
                    "was over its usage limit, and another account now has room. The session "
                    "was reset; please carry on.")


# ---------------------------------------------------------------- the tick

class Warden:
    def __init__(self, cfg, github, minter, paperclip, state, now, log):
        """`github` is one seam for desktop, or a dict {repo key: seam}."""
        self.ghs = github if isinstance(github, dict) else {"desktop": github}
        self.gh = self.ghs.get("desktop")
        self.cfg, self.minter, self.pc = cfg, minter, paperclip
        self.state, self.now, self.log = state, now, log
        self.pause_reasons = []
        self.develop_why = ""

    def pause(self, reason):
        self.pause_reasons.append(reason)

    def repos(self):
        return repos_from(self.cfg)

    def gh_for(self, r):
        return self.ghs.get(r["key"])

    def repo_state(self, r):
        """Desktop's audit state sits at the top of state.json, where it always
        has; any other repository's under state["repos"][key]."""
        if r["key"] == "desktop":
            return self.state
        return self.state.setdefault("repos", {}).setdefault(r["key"], {})

    def request_repo(self, req):
        """(repo entry, gh) for a spool request, or (None, None) when it names
        a repository the config does not list."""
        r = repo_by_key(self.cfg, req.get("repo", "desktop"))
        return (r, self.gh_for(r)) if r and self.gh_for(r) else (None, None)

    def handle(self, req):
        v = req["verb"]
        if v == "dispatch_stress":
            r, gh = self.request_repo(req)
            if r is None:
                return self.log(f"refused dispatch for PR {req['pr']}: unknown repo {req.get('repo')!r}")
            if not r.get("stress"):
                return self.log(f"refused dispatch for PR {req['pr']}: {r['key']} has no stress workflow")
            pr = gh.get_pr(req["pr"])
            why = dispatch_problem(pr, r)
            if why:
                return self.log(f"refused dispatch for PR {req['pr']}: {why}")
            try:
                gh.dispatch_stress(pr["head"]["ref"], req["test_class"], req["n"], req["suite"])
            except (subprocess.CalledProcessError, OSError) as e:
                return self.log(f"dispatch for PR {req['pr']} failed: {type(e).__name__}")
            self.log(f"dispatched stress.yml for PR {req['pr']} {req['test_class']} x{req['n']}")
        elif v == "released":
            self.state["consecutive_releases"] = next_release_state(self.state, "released")
            self.log(f"{req['ticket']} released ({self.state['consecutive_releases']} in a row)")
            if self.state["consecutive_releases"] >= RELEASES_IN_A_ROW:
                self.pause(SPOOL_BRAKE)
        elif v == "push_refused":
            self.pause("GitHub refused a bot push on a ruleset")
        elif v == "rerun_failed":
            r, gh = self.request_repo(req)
            if r is None:
                return self.log(f"refused rerun for PR {req['pr']}: unknown repo {req.get('repo')!r}")
            self.rerun_failed(req["pr"], r)

    def rerun_develop(self, sha, n):
        """Rerun the failed jobs of develop's push CI at a bot merge while the
        red looks like runner flakes (CB-467). True keeps the merge pending: a
        rerun was started, or the run has not finished. False lets the red
        stand, with `self.develop_why` saying why."""
        self.develop_why = ""
        try:
            run = self.gh.latest_ci_run(sha, event="push")
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError):
            return False
        if not run:
            return False
        if run.get("status") != "completed":
            return True
        if run.get("conclusion") not in RED_CONCLUSIONS:
            return False
        attempt = int(run.get("run_attempt") or 1)
        cache = self.state.setdefault("develop_failures", {}).setdefault(sha, {})
        try:
            for a in range(1, attempt + 1):
                if str(a) not in cache:
                    cache[str(a)] = self.gh.failed_tests(run["id"], a)
        except (subprocess.CalledProcessError, OSError, ValueError):
            if attempt > 1:
                self.develop_why = " (could not read which tests failed)"
                return False
        attempts = [cache.get(str(a), []) for a in range(1, attempt + 1)]
        why = develop_rerun_problem(attempts, attempt - 1)
        if why:
            self.develop_why = f" ({why})"
            return False
        try:
            self.gh.rerun_failed(run["id"])
        except (subprocess.CalledProcessError, OSError):
            return False
        failed = ", ".join(attempts[-1]) or "no test named"
        self.log(f"develop CI red on bot merge PR {n} at {sha[:9]} ({failed}): "
                 f"reran failed jobs of run {run['id']}, rerun {attempt} of {DEVELOP_RERUNS}")
        return True

    def rerun_failed(self, n, r=None):
        """Rerun the failed jobs of PR n's red CI runs, once per head sha
        (CB-465), in repository `r` (desktop when None; CB-481). A refusal is
        logged and never pauses: asking is not a fault."""
        r = r or DESKTOP
        gh = self.gh_for(r)
        tag = f"PR {n}" if r["key"] == "desktop" else f"{r['key']} PR {n}"
        reruns = self.repo_state(r).setdefault("reruns", {})
        try:
            pr = gh.get_pr(n)
            sha = ((pr or {}).get("head") or {}).get("sha")
            runs = gh.latest_ci_runs(sha, r["ci_workflows"]) if sha and not dispatch_problem(pr, r) else []
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError) as e:
            return self.log(f"rerun for {tag} unreadable: {type(e).__name__}")
        targets, why = rerun_targets(pr, runs, reruns, r)
        if why:
            return self.log(f"refused rerun for {tag}: {why}")
        done = []
        for run in targets:
            try:
                gh.rerun_failed(run["id"])
            except (subprocess.CalledProcessError, OSError) as e:
                self.log(f"rerun for {tag} failed: {type(e).__name__}")
                continue
            done.append(str(run["id"]))
        if not done:
            return
        reruns[sha] = reruns.get(sha, 0) + 1
        while len(reruns) > 200:
            reruns.pop(next(iter(reruns)))
        self.log(f"reran failed jobs of CI run {', '.join(done)} for {tag} at {sha[:9]}")

    def stop_work(self):
        """A pause only defers new assignments: a run in flight keeps going
        (phase 0, measured). So while the company is paused, every tick cancels
        its queued and running runs and sets its in-progress issues to blocked.
        The warden resumes only a pause of its own whose sole reason is the
        release brake, after its cool-off (CB-484); any other pause is lifted
        by a person or the lead."""
        # One refusal must not end the tick (CB-467): Paperclip answered a
        # PATCH to blocked with 422, the exception escaped, and the warden died
        # after pausing but before logging why or saving its state, on every
        # tick from then on.
        for run_id in self.pc.active_run_ids():
            try:
                self.pc.cancel_run(run_id)
                self.log(f"cancelled run {run_id[:8]}")
            except (urllib.error.URLError, OSError, ValueError) as e:
                self.log(f"could not cancel run {run_id[:8]}: {type(e).__name__} {getattr(e, 'code', '')}".rstrip())
        # A refusal is retried every tick but logged once per issue per pause:
        # Paperclip refused one issue with 422 about 160 times in one 13-hour
        # pause (CB-484). The list is cleared when the company is seen active.
        refused = self.state.setdefault("block_refused", [])
        blocked = self.state.setdefault("stop_blocked", [])
        for issue_id in self.pc.in_progress_issue_ids():
            try:
                self.pc.block_issue(issue_id)
                if issue_id not in blocked:
                    blocked.append(issue_id)
                self.log(f"blocked issue {issue_id[:8]}")
            except (urllib.error.URLError, OSError, ValueError) as e:
                if issue_id not in refused:
                    refused.append(issue_id)
                    self.log(f"could not block issue {issue_id[:8]}: {type(e).__name__} {getattr(e, 'code', '')}".rstrip())

    def watch_releases(self):
        """Releases from Paperclip's own issue states. The spool's `released`
        stays as an extra signal; this one cannot be suppressed from inside
        the sandbox short of not releasing at all. Only issues that are newly
        cancelled are read in full, so a long cancelled history costs nothing."""
        rows = {r["id"]: r for r in self.pc.cancelled_issue_rows()}
        swept = set(self.state.get("unstick", {}).get("cancelled", []))
        for issue_id in new_releases(list(rows), self.state):
            if issue_id in swept:
                # The unstick sweep cancelled a stalled child (CB-479): that is
                # the warden's own repair, not the lead giving up on a ticket.
                self.log(f"issue {issue_id[:8]} cancelled by the unstick sweep, not a release")
                continue
            kind, key = classify_release(rows[issue_id], self.pc.get_issue)
            if kind == "unreadable":
                self.log(f"release signal unreadable for issue {issue_id[:8]}")
                self.pause(f"release signal unreadable (issue {issue_id[:8]} has no readable description)")
            elif kind == "release":
                # One release per ticket until the next merge, whatever pass its
                # engineer issues belong to. CB-471 matched on [ticket, parent
                # pass], but with the routine firing every 30 minutes a fix
                # round's issue is made by a later pass than the first, so
                # CB-346's two cancelled issues under two passes paused the org
                # for 13 hours on one release (CB-484). Every row that reaches
                # here has a key (classify_release); a row without one is
                # `other` or `unreadable`, and the latter still pauses, which
                # errs towards the brake. State from before this kept
                # `paperclip_release_passes`; it is simply dropped.
                self.state.pop("paperclip_release_passes", None)
                counted = self.state.setdefault("paperclip_released_keys", [])
                if key in counted:
                    self.log(f"{key} already counted as released since the last merge (issue {issue_id[:8]})")
                    continue
                counted.append(key)
                # Checked after the key is recorded, so a second engineer
                # issue of the same ticket seen later is still deduplicated.
                if paused_within(self.state.get("pauses", []), self.now):
                    self.log(f"{key} released, not counted: the company was paused during this "
                             f"ticket's window (issue {issue_id[:8]})")
                    continue
                n = self.state["paperclip_releases"] = self.state.get("paperclip_releases", 0) + 1
                self.log(f"{key} released (Paperclip issue cancelled; {n} in a row)")
                if n >= RELEASES_IN_A_ROW:
                    self.pause(PAPERCLIP_BRAKE)

    def brake_cooled_off(self):
        """The warden's own pause (`warden_pause`, recorded when it paused,
        with every reason seen while paused) has only release-brake reasons and
        is an hour old. A pause with no record, a person's or one made before
        CB-484, never qualifies."""
        rec = self.state.get("warden_pause")
        return bool(rec and is_release_brake(rec.get("reasons"))
                    and self.now - rec.get("at", self.now) >= BRAKE_COOL_OFF)

    def restart_stopped(self):
        """After any resume, set going what stop_work stopped (CB-484). The
        unstick sweep never touches a blocked issue with no children, so
        without this an engineer task blocked by a pause sat forever. An issue
        still `blocked` goes to `todo` with a board comment, which wakes its
        assignee; `in_progress` with no run would only sit until the sweep's
        stall timer. One stop_work could not block keeps its status and gets
        the comment. Anything else has moved on and is left alone. A failure
        keeps the issue listed for the next tick and never ends this one."""
        # Retries live apart from stop_work's lists, so an issue whose restart
        # failed does not silence its 422 line in the next pause.
        todo = ([[i, False] for i in self.state.pop("stop_blocked", [])]
                + [[i, True] for i in self.state.pop("block_refused", [])]
                + self.state.pop("restart_retry", []))
        retry = []
        for iid, was_refused in todo:
            try:
                status = (self.pc.get_issue(iid) or {}).get("status")
                if not was_refused and status == "blocked":
                    self.pc.set_issue_status(iid, "todo")
                elif not (was_refused and status == "in_progress"):
                    continue
                self.pc.comment(iid, RESTART_COMMENT)
                self.log(f"restarted issue {iid[:8]} after the pause"
                         + (" (comment only)" if was_refused else ""))
            except Exception as e:
                self.log(f"could not restart issue {iid[:8]}: {type(e).__name__} {getattr(e, 'code', '')}".rstrip())
                retry.append([iid, was_refused])
        if retry:
            self.state["restart_retry"] = retry

    def resume_brake(self):
        """Lift a release-brake pause after its cool-off; True when lifted."""
        try:
            self.pc.resume()
        except Exception as e:
            self.log(f"could not resume after the release brake: {type(e).__name__} {getattr(e, 'code', '')}".rstrip())
            return False
        self.state["consecutive_releases"] = 0
        self.state["paperclip_releases"] = 0
        self.state["paperclip_released_keys"] = []
        self.log("RESUMED: release brake cooled off")
        return True

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
        """Each repository's audit and workflow guard, apart (CB-481). A GitHub
        outage must not stop the tick (no pause, no token refresh, no run
        cancelling), but a brake that stays blind is no brake: three failed
        audits in a row of one repository pause the company."""
        for r in self.repos():
            st = self.repo_state(r)
            tag = "" if r["key"] == "desktop" else f" ({r['key']})"
            try:
                if self.gh_for(r) is None:
                    raise KeyError(r["key"])
                self.audit(r)
                self.guard_open_prs(r)
                st["audit_failures"] = 0
            except (subprocess.CalledProcessError, OSError, ValueError, KeyError) as e:
                n = st["audit_failures"] = st.get("audit_failures", 0) + 1
                self.log(f"merge audit unreadable{tag} ({type(e).__name__}); {n} in a row")
                if n >= AUDIT_FAILURES_BEFORE_PAUSE:
                    self.pause(f"merge audit unreadable{tag} {n} ticks in a row")

    def guard(self, r, n, sha, tag):
        """Run the workflow guard on PR n at head `sha` once (CB-481)."""
        st = self.repo_state(r)
        guarded = st.setdefault("guarded", [])
        if not sha or sha in guarded:
            return None
        files = self.gh_for(r).pr_files(n)
        for why in workflow_problems(files, r):
            self.pause(f"{tag}: workflow guard: {why}")
        guarded.append(sha)
        del guarded[:-200]
        return files

    def guard_open_prs(self, r):
        """The workflow guard on every open org PR at each new head sha. A
        PR's CI runs its own copy of a workflow as soon as it is pushed, so a
        guard at merge only would be too late."""
        for pr in self.gh_for(r).open_bot_prs():
            tag = f"PR {pr['number']}" if r["key"] == "desktop" else f"{r['key']} PR {pr['number']}"
            self.guard(r, pr["number"], (pr.get("head") or {}).get("sha"), tag)

    def audit(self, r=None):
        """Audit bot merges into one repository's base branch (desktop when
        None). The QA comment, the workflow guard for a head it never saw, the
        head checks (a repository with `head_checks`) and the release-counter
        reset happen once, when a merge is first seen. CI on the merge commit
        is re-read every tick until it has settled: the first tick after a
        merge almost always sees it queued, and marking the merge audited then
        meant a red develop never paused the org (CB-460)."""
        r = r or DESKTOP
        gh, st = self.gh_for(r), self.repo_state(r)
        label = lambda n: f"PR {n}" if r["key"] == "desktop" else f"{r['key']} PR {n}"
        seen = set(st.setdefault("audited", []))
        pending = dict(st.get("audit_pending", {}))
        for pr in gh.merged_bot_prs():
            sha = pr["merge_commit_sha"]
            if sha in seen or sha in pending:
                continue
            self.state["consecutive_releases"] = next_release_state(self.state, "merged")
            self.state["paperclip_releases"] = 0
            self.state["paperclip_released_keys"] = []
            tag = label(pr["number"])
            qa = has_qa_comment(gh.comment_bodies(pr["number"], before=pr["merged_at"]), r["qa_names"])
            for why in audit_merge(qa, {}, r):
                self.pause(f"{tag}: {why}")
            head = (pr.get("head") or {}).get("sha")
            files = self.guard(r, pr["number"], head, tag)
            if r.get("head_checks"):
                if files is None:
                    files = gh.pr_files(pr["number"])
                paths = [f.get("filename", "") for f in files]
                ci_head = gh.check_conclusions(head) if head else {}
                for why in head_checks_problem(ci_head, paths, r):
                    self.pause(f"{tag}: {why}")
            pending[sha] = {"pr": pr["number"], "since": self.now}
        for sha, entry in list(pending.items()):
            ci = gh.check_conclusions(sha)
            red = audit_merge(True, ci, r)
            # A red develop on a bot merge is rerun while each attempt fails on
            # a different test (CB-467): CB-426's flake and then TurnSoundsTests
            # paused the org for hours on a merge that touched neither. Only
            # desktop has push CI to rerun.
            if red and r.get("push_ci") and self.rerun_develop(sha, entry["pr"]):
                continue
            why_dev = self.develop_why if r.get("push_ci") else ""
            for why in red:
                self.pause(f"{label(entry['pr'])}: {why}{why_dev}")
            if red or ci_settled(ci, r, self.now - entry.get("since", self.now)):
                del pending[sha]
                seen.add(sha)
            elif self.now - entry.get("since", self.now) >= PENDING_CI_LIMIT:
                self.pause(f"{label(entry['pr'])}: {r['base']} CI never finished on a bot merge")
                del pending[sha]
                seen.add(sha)
        failures = st.get("develop_failures", {})
        for sha in [k for k in failures if k not in pending]:
            del failures[sha]
        st["audit_pending"] = pending
        st["audited"] = sorted(seen)[-200:]

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
        if paused and not self.pause_reasons and self.brake_cooled_off():
            paused = not self.resume_brake()
        self.note_pauses(paused or bool(self.pause_reasons))
        if self.pause_reasons and not paused:
            self.pc.pause("; ".join(self.pause_reasons))
            self.state["warden_pause"] = {"at": self.now, "reasons": list(self.pause_reasons)}
            paused = True
        elif self.pause_reasons and self.state.get("warden_pause"):
            # A new reason on a pause the warden made: it joins the record, so
            # anything but the release brake keeps the pause manual.
            rec = self.state["warden_pause"]
            rec["reasons"] += [r for r in self.pause_reasons if r not in rec["reasons"]]
        elif not paused:
            # Active again, whoever resumed it: the next pause starts fresh,
            # and the work the pause stopped is set going again.
            self.state.pop("warden_pause", None)
            self.restart_stopped()
        # The reason goes in the log before anything that can fail (CB-467).
        for r in self.pause_reasons:
            self.log("PAUSED: " + r)
        if paused:
            self.stop_work()
        if mint_due(self.state, self.now, paused):
            # A failed mint is logged and the tick goes on to the sweep; the
            # next tick tries again, since last_mint only moves on success.
            try:
                self.mint_tokens()
            except Exception as e:
                self.log(f"token not refreshed: {type(e).__name__} {getattr(e, 'code', '')}".rstrip())
        self.unstick(paused)
        return paused

    def rulesets_problem(self, r):
        """ruleset_gate for a repository, read with the host's gh. A failed
        read withholds the repository too: the gate cannot be shown to hold."""
        gh = self.gh_for(r)
        try:
            return ruleset_gate(gh.rulesets(), {b: gh.branch_rules(b) for b in gated_branches(r)})
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError, AttributeError) as e:
            return f"rulesets unreadable ({type(e).__name__})"

    def note_withheld(self, withheld):
        """One log line per change in which repositories are kept off the
        token and why, not one per tick."""
        before = self.state.get("withheld", {})
        for key in sorted(set(before) | set(withheld)):
            if before.get(key) != withheld.get(key):
                if key in withheld:
                    self.log(f"{key} kept off the token: {withheld[key]}")
                else:
                    self.log(f"{key} back on the token: its rulesets now protect it")
        self.state["withheld"] = dict(withheld)

    def mint_tokens(self):
        """Both tokens (CB-481). The everyday one names every configured
        repository the installation covers, with EVERYDAY_PERMISSIONS by name,
        so it never carries Workflows. The workflow one names only the
        repositories with `workflow_edits` (desktop), and is minted only while
        the installation shows `workflows: write`. Each repository's coverage
        is read on every mint and written to <spool>/access.json."""
        access, withheld = {}, {}
        for r in self.repos():
            # One repository's lookup failing must not stop the others' token
            # (QA, CB-481): a 5xx on mobile used to abort the whole mint, so
            # desktop's token lapsed within the hour.
            try:
                access[r["key"]] = self.minter.installation(r["repo"])
            except Exception as e:
                access[r["key"]] = None
                withheld[r["key"]] = f"installation unreadable ({type(e).__name__} {getattr(e, 'code', '')})".replace(" )", ")")
                continue
            if access[r["key"]] and r["key"] != "desktop":
                why = self.rulesets_problem(r)
                if why:
                    withheld[r["key"]] = why
                    access[r["key"]] = None
        self.note_withheld(withheld)
        plan = token_plan(self.repos(), access)
        workflows = bool(plan["workflows"])
        if self.cfg.get("spool"):
            try:
                write_access(self.cfg["spool"], {k: bool(v) for k, v in access.items()},
                             workflows, self.now, withheld)
            except OSError as e:
                self.log(f"{ACCESS_FILE} not written: {type(e).__name__}")
        if not plan["everyday"]:
            raise LookupError("the installation covers none of the configured repositories")
        self.pc.put_secret("GITHUB_TOKEN", self.minter.mint(
            plan["installation"], plan["everyday"], EVERYDAY_PERMISSIONS))
        self.state["last_mint"] = self.now
        self.log("token refreshed (" + ", ".join(plan["everyday"]) + ")")
        if workflows:
            self.pc.put_secret(WORKFLOW_SECRET, self.minter.mint(
                plan["installation"], plan["workflows"], WORKFLOW_PERMISSIONS))
            self.log("workflow token refreshed (" + ", ".join(plan["workflows"]) + ")")

    def unstick(self, paused):
        """The unstick sweep (CB-479) for each company in `unstick_company_ids`
        (default: the fix company). Never acts on a paused company: a pause
        means stop, and stop_work has just blocked its in-progress issues,
        which the sweep must not then undo. A read failure skips the company
        for this tick; a failed action is logged and retried next tick, since
        state is only recorded after the call succeeds."""
        st = self.state.setdefault("unstick", {})
        st["cancelled"] = st.get("cancelled", [])[-500:]
        main = self.cfg.get("company_id")
        for co in self.cfg.get("unstick_company_ids") or [main]:
            try:
                if paused if co == main else self.pc.is_paused(co):
                    continue
                issues = self.pc.all_issues(co)
                if len(issues) >= ISSUE_PAGE:
                    # A child missing from a cut-off page would make its
                    # parent look settled; better to do nothing this tick.
                    self.log(f"unstick skipped for company {str(co)[:8]}: {len(issues)} issues, "
                             f"listing may be truncated")
                    continue
                runs = self.pc.company_runs(co)
                comments_at = {}
                for i in issues:
                    if i.get("status") == "blocked" and any(c.get("parentId") == i["id"] for c in issues):
                        comments_at[i["id"]] = self.pc.newest_comment_at(i["id"], i.get("assigneeAgentId"))
            except Exception as e:
                self.log(f"unstick sweep unreadable for company {str(co)[:8]}: {type(e).__name__}")
                continue
            try:
                pool_ok = pool_has_headroom(self.pc.pool_usage(self.cfg.get("pool_usage", POOL_USAGE)))
            except (OSError, ValueError) as e:
                # No reading is no headroom: rule 3 waits, the others go on.
                self.log(f"pool usage unreadable: {type(e).__name__}")
                pool_ok = False
            # Wakes and resets are kept per company: pruning one company's
            # against another's issues would forget them every tick (QA).
            cst = st.setdefault("companies", {}).setdefault(str(co), {})
            try:
                prune_unstick_state(cst, issues, self.now)
                actions = unstick_actions(issues, runs, comments_at, cst, self.now, pool_ok)
            except Exception as e:
                # The sweep never raises into the tick: state.json is saved
                # only when tick() returns, and the brakes' state is in it.
                self.log(f"unstick sweep failed for company {str(co)[:8]}: {type(e).__name__}")
                continue
            for a in actions:
                try:
                    self.do_unstick(a, cst, st)
                except Exception as e:
                    self.log(f"unstick {a['kind']} {a['identifier']} failed: {type(e).__name__}")
                    continue
                self.log(f"UNSTUCK: {a['kind']} {a['identifier']}")

    def do_unstick(self, a, st, top):
        """Carry out one action. `st` is the company's own unstick state,
        `top` the shared one holding the sweep-cancel exclusion list."""
        k, iid = a["kind"], a["id"]
        if k == "close_parent":
            self.pc.set_issue_status(iid, "done")
        elif k == "wake_parent":
            self.pc.comment(iid, WAKE_PARENT)
            st.setdefault("parent_woken", {})[iid] = self.now
        elif k == "wake_child":
            self.pc.comment(iid, WAKE_CHILD.format(n=a["wakes"]))
            st.setdefault("child_wakes", {})[iid] = [a["wakes"], self.now]
        elif k == "cancel_child":
            # Recorded before the PATCH, so a tick that dies between the two
            # still never counts this cancel as a release.
            top.setdefault("cancelled", []).append(iid)
            try:
                self.pc.set_issue_status(iid, "cancelled")
            except Exception:
                # Not cancelled, so not excluded: a later real release of it
                # must still count. The retry adds it back.
                top["cancelled"].remove(iid)
                raise
            self.pc.comment(a["parent_id"], WAKE_PARENT_AFTER_CANCEL.format(child=a["identifier"]))
        elif k == "reset_session":
            self.pc.reset_session(a["agent_id"], a["task_key"])
            st.setdefault("resets", {})[iid] = self.now
            self.pc.comment(iid, WAKE_AFTER_RESET)


# Develop's push CI after a bot merge (CB-467). Red there pauses the org, but
# the macOS unit leg flakes on timing tests the merge never touched: the first
# rerun of PR 377's merge failed on ClaudeCloudEventsTests, its rerun on
# TurnSoundsTests, and pausing on the second cost the org another hour. A
# different test failing in each attempt is the runner, not the merge; the same
# test failing twice, or a failure that names no test (a build error), is real.
DEVELOP_RERUNS = 2
_FAILED_TEST = re.compile(
    r"(?:\bFailed ([A-Za-z_][\w.`+<>,]*) \[|\bfailed ([A-Za-z_][\w.`+<>,]*) \(\d"
    r"|\s([A-Za-z_][\w.`+<>,]*) \[FAIL\])")


def failed_tests_in(log):
    """Test names a CI log reports as failed: VSTest's `Failed X [3 s]`, the
    testing platform's `failed X (12ms)` and xUnit's `X [FAIL]`."""
    names = set()
    for m in _FAILED_TEST.finditer(log or ""):
        names.add(next(g for g in m.groups() if g))
    return sorted(names)


def develop_rerun_problem(attempts, reruns):
    """Why a red develop run at a bot merge must NOT be rerun again (None = go).
    `attempts` lists the failed test names of every attempt so far, newest last;
    `reruns` is how many reruns the warden has already started there."""
    if reruns == 0:
        return None
    if reruns >= DEVELOP_RERUNS:
        return f"still red after {reruns} reruns"
    if any(not a for a in attempts):
        return "an attempt failed on something other than a test"
    seen = set()
    for a in attempts:
        twice = sorted(seen & set(a))
        if twice:
            return "failed twice: " + ", ".join(twice)
        seen |= set(a)
    return None


# ---------------------------------------------------------------- real seams

def _run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, check=True, **kw).stdout


class GhCli:
    """GitHub via the host's own `gh` login, for one repository."""

    def __init__(self, repo=REPO, base="develop"):
        self.repo, self.base = repo, base

    def api(self, path, method="GET"):
        out = _run(["gh", "api", "-X", method, path])
        return json.loads(out) if out.strip() else None

    def get_pr(self, n):
        try:
            return self.api(f"repos/{self.repo}/pulls/{n}")
        except subprocess.CalledProcessError:
            return None

    def dispatch_stress(self, ref, test_class, n, suite):
        _run(["gh", "api", "-X", "POST", f"repos/{self.repo}/actions/workflows/stress.yml/dispatches",
              "-f", f"ref={ref}", "-f", f"inputs[test_class]={test_class}",
              "-f", f"inputs[n]={n}", "-f", f"inputs[suite]={suite}"])

    def latest_ci_run(self, sha, event="pull_request"):
        """The newest run of ci.yml at this sha from `event`, or None."""
        data = self.api(f"repos/{self.repo}/actions/runs?head_sha={sha}&event={event}&per_page=30") or {}
        runs = [r for r in data.get("workflow_runs", [])
                if str(r.get("path", "")).endswith("/ci.yml") or r.get("name") == "CI"]
        runs.sort(key=lambda r: r.get("created_at") or "", reverse=True)
        return runs[0] if runs else None

    def latest_ci_runs(self, sha, workflows, event="pull_request"):
        """The newest run of each of `workflows` (file names) at this sha."""
        data = self.api(f"repos/{self.repo}/actions/runs?head_sha={sha}&event={event}&per_page=30") or {}
        newest = {}
        for r in data.get("workflow_runs", []):
            wf = os.path.basename(str(r.get("path", "")))
            if wf in workflows and (r.get("created_at") or "") >= (newest.get(wf, {}).get("created_at") or ""):
                newest[wf] = r
        return [newest[w] for w in workflows if w in newest]

    def rerun_failed(self, run_id):
        _run(["gh", "api", "-X", "POST", f"repos/{self.repo}/actions/runs/{int(run_id)}/rerun-failed-jobs"])

    def failed_tests(self, run_id, attempt):
        """Test names that failed in one attempt of a run, from its failed jobs' logs."""
        return failed_tests_in(_run(["gh", "run", "view", str(int(run_id)), "--repo", self.repo,
                                     "--attempt", str(int(attempt)), "--log-failed"]))

    def merged_bot_prs(self):
        prs = self.api(f"repos/{self.repo}/pulls?state=closed&base={self.base}&sort=updated&direction=desc&per_page=30")
        return [p for p in prs if p.get("merged_at") and (p["user"] or {}).get("login") == BOT_LOGIN]

    def rulesets(self):
        return self.api(f"repos/{self.repo}/rulesets") or []

    def branch_rules(self, branch):
        """The active rules GitHub applies to `branch`, each naming its ruleset."""
        return self.api(f"repos/{self.repo}/rules/branches/{branch}") or []

    def open_bot_prs(self):
        prs = self.api(f"repos/{self.repo}/pulls?state=open&base={self.base}&per_page=100") or []
        return [p for p in prs if (p["user"] or {}).get("login") == BOT_LOGIN
                and p.get("state") == "open" and not p.get("merged_at")]

    def pr_files(self, n):
        """Every changed file of PR n: GitHub pages /files at 100 (and stops
        at 3000). `--slurp` returns the pages as one array of arrays."""
        out = _run(["gh", "api", "--paginate", "--slurp", f"repos/{self.repo}/pulls/{int(n)}/files?per_page=100"])
        pages = json.loads(out) if out.strip() else []
        return [f for page in pages for f in (page or [])]

    def comment_bodies(self, n, before):
        out = []
        for path in (f"issues/{n}/comments", f"pulls/{n}/reviews", f"pulls/{n}/comments"):
            for c in self.api(f"repos/{self.repo}/{path}?per_page=100") or []:
                when = c.get("created_at") or c.get("submitted_at") or ""
                if when <= before and c.get("body"):
                    out.append(c["body"])
        return out

    def check_conclusions(self, sha):
        # filter=latest: after a rerun, the rerun's result replaces the red one.
        data = self.api(f"repos/{self.repo}/commits/{sha}/check-runs?filter=latest&per_page=100")
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

    def _jwt(self):
        app = json.load(open(os.path.join(self.dir, "app.json")))
        return self.jwt(app["app_id"])

    def installation(self, repo):
        """The App's installation object for `repo` (it carries `id` and the
        `permissions` the installation accepted), or None when the installation
        does not cover the repository (GitHub answers 404)."""
        try:
            return self._call(self._jwt(), f"/repos/{repo}/installation")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise

    def mint(self, installation_id, repositories, permissions):
        """A token for `repositories` (names, not owner/name) with exactly
        `permissions`. GitHub refuses a permission the installation lacks."""
        return self._call(self._jwt(), f"/app/installations/{int(installation_id)}/access_tokens", "POST",
                          {"repositories": list(repositories), "permissions": dict(permissions)})["token"]


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

    def is_paused(self, company=None):
        return self.call("GET", f"/api/companies/{company or self.company}").get("status") == "paused"

    # The unstick sweep's reads and writes (CB-479). Shapes measured read-only
    # against the live instance before this was written.

    def all_issues(self, company):
        rows = self.call("GET", f"/api/companies/{company}/issues?limit={ISSUE_PAGE}") or []
        return rows if isinstance(rows, list) else rows.get("issues", [])

    def company_runs(self, company):
        return self.call("GET", f"/api/companies/{company}/heartbeat-runs?limit=200") or []

    def newest_comment_at(self, issue_id, agent_id):
        """Epoch of the assignee agent's newest comment, or None."""
        rows = self.call("GET", f"/api/issues/{issue_id}/comments") or []
        rows = rows if isinstance(rows, list) else rows.get("comments", [])
        times = [ts(c.get("createdAt")) for c in rows
                 if agent_id and c.get("authorAgentId") == agent_id and not c.get("deletedAt")]
        times = [t for t in times if t is not None]
        return max(times) if times else None

    def pool_usage(self, path):
        with open(path) as f:
            return json.load(f)

    def set_issue_status(self, issue_id, status):
        self.call("PATCH", f"/api/issues/{issue_id}", {"status": status})

    def comment(self, issue_id, body):
        # A board comment on an open issue wakes its assignee (issue_commented).
        self.call("POST", f"/api/issues/{issue_id}/comments", {"body": body})

    def reset_session(self, agent_id, task_key):
        self.call("POST", f"/api/agents/{agent_id}/runtime-state/reset-session", {"taskKey": task_key})

    def pause(self, reason):
        self.call("PATCH", f"/api/companies/{self.company}", {"status": "paused"})

    def resume(self):
        self.call("PATCH", f"/api/companies/{self.company}", {"status": "active"})

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
        if os.path.basename(path) in (PAUSES_FILE, ACCESS_FILE):
            continue  # the warden's own answers to the org, not requests
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


def write_access(spool, repos, workflows, now, withheld=None):
    """<spool>/access.json (CB-481): which configured repositories are on the
    everyday token (covered by the installation and, beyond desktop, past the
    ruleset gate), why any covered one was withheld, and whether the workflow
    token was minted, as of the last mint. Written like pauses.json; its very
    existence tells the org a per-repository warden is running."""
    path = os.path.join(spool, ACCESS_FILE)
    tmp = path + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o644)
    with os.fdopen(fd, "w") as f:
        json.dump({"checked_at": iso(now), "repos": repos, "workflows": workflows,
                   "withheld": dict(withheld or {})}, f, indent=1)
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

    ghs = {r["key"]: GhCli(r["repo"], r["base"]) for r in repos_from(cfg)}
    w = Warden(cfg, ghs, AppMinter(d, api=cfg.get("github_api", "https://api.github.com")),
               PaperclipHttp(cfg["paperclip_login"], cfg["company_id"]),
               state, time.time(), log)
    try:
        paused = w.tick(read_spool(cfg["spool"], log))
    finally:
        logf.close()
        # Saved even when the tick raises (CB-467), so a crash cannot make the
        # next tick redo work it already did.
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
