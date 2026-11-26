#!/usr/bin/env python3
"""Read a repository's rulesets back against the JSON that applies them, and
prove a direct push to its protected branches is refused (CB-481).

    REPO=Uplift-Foundation/Orbweaver-Mobile RULESETS_DIR=host/rulesets-mobile \\
        python3 host/check-rulesets.py                 # before and after --apply
    ... python3 host/check-rulesets.py --push-probe    # after: try the push

Three steps, each read-only except the last, and each printed:

1. **Read back.** For every JSON file, the ruleset of the same name on GitHub:
   `missing` (apply would POST it), `matches`, or `differs` with the fields
   that differ. Before `--apply` this is the diff of what applying creates.
2. **Effective rules.** `GET repos/<repo>/rules/branches/<branch>` for each
   branch in PROBE_BRANCHES (default `main develop`): what GitHub will
   actually enforce there, whichever ruleset it comes from. Each must carry
   `pull_request`, `deletion` and `non_fast_forward`; `develop` must also
   require every check listed in REQUIRED_CHECKS (default the mobile three).
3. **`--push-probe`, only when step 2 passed.** Per branch: make a commit
   object whose tree and parent are the branch head (a dangling object, it
   changes no ref), then ask GitHub to move the branch to it without force.
   Refused is the pass. It runs with whatever `gh` is logged in as, so run it
   once as an admin (whose bypass is "pull requests only", so they are refused
   too) and, for the org, with `GH_TOKEN` set to a token the warden minted.
   If GitHub ever accepts the move, the script puts the branch straight back
   with a forced update to the old head and exits non-zero; that can only
   happen when step 2's reading was wrong, which is why step 2 gates it.

Exit 0 only when every step that ran passed. Needs `gh`; nothing else.
"""
import json
import os
import subprocess
import sys

COMPARED = ("target", "enforcement", "conditions", "bypass_actors", "rules")
BRANCH_RULES = {"pull_request", "deletion", "non_fast_forward"}


def gh(args, sh=None):
    """(returncode, stdout, stderr) of `gh <args>`."""
    run = sh or (lambda a: subprocess.run(a, capture_output=True, text=True, timeout=60))
    r = run(["gh", *args])
    return r.returncode, r.stdout, r.stderr


def norm(value):
    """A ruleset field in a comparable shape: every list sorted, so order on
    either side is not a difference. (Keys GitHub adds on its own, such as
    the pull_request rule's allowed_merge_methods, are cut by trim_to.)"""
    if isinstance(value, dict):
        return {k: norm(v) for k, v in sorted(value.items())}
    if isinstance(value, list):
        items = [norm(v) for v in value]
        return sorted(items, key=lambda v: json.dumps(v, sort_keys=True))
    return value


def trim_to(want, got):
    """`got` with every dict key `want` does not have removed, recursively,
    so a field GitHub adds on its own is not a difference."""
    if isinstance(want, dict) and isinstance(got, dict):
        return {k: trim_to(want[k], got[k]) for k in want if k in got}
    if isinstance(want, list) and isinstance(got, list):
        # Pair items by what identifies them (a rule's type, an actor, a
        # check's context), not by position; an unpaired item stays whole.
        wanted = {identity(w): w for w in want if isinstance(w, dict)}
        return [trim_to(wanted[identity(g)], g) if isinstance(g, dict) and identity(g) in wanted else g
                for g in got]
    return got


def identity(d):
    return (d.get("type"), d.get("actor_type"), d.get("actor_id"), d.get("context"))


def ruleset_diff(want, got):
    """Field names whose values differ between the file and GitHub."""
    out = []
    for k in COMPARED:
        if norm(want.get(k)) != norm(trim_to(want.get(k), got.get(k))):
            out.append(k)
    return out


def rules_problem(branch, rules, required_checks):
    """Why GitHub's effective rules on `branch` are not enough (None = ok).
    `rules` is the list `GET rules/branches/<branch>` returns."""
    if rules is None:
        return f"{branch}: could not read the effective rules"
    types = {r.get("type") for r in rules}
    missing = sorted(BRANCH_RULES - types)
    if missing:
        return f"{branch}: no {', '.join(missing)} rule in force"
    if branch == "develop" and required_checks:
        have = set()
        for r in rules:
            if r.get("type") == "required_status_checks":
                have |= {c.get("context") for c in r.get("parameters", {}).get("required_status_checks", [])}
        absent = [c for c in required_checks if c not in have]
        if absent:
            return f"{branch}: required checks not in force: {', '.join(absent)}"
    return None


def refused_by_rules(stderr):
    s = stderr.lower()
    return "repository rule violations" in s or "rule violation" in s or "protected branch" in s


def load_files(directory):
    out = []
    for name in sorted(os.listdir(directory)):
        if name.endswith(".json"):
            with open(os.path.join(directory, name)) as f:
                out.append((name, json.load(f)))
    return out


def jget(args, sh):
    rc, out, _ = gh(["api", *args], sh)
    if rc != 0:
        return None
    try:
        return json.loads(out)
    except ValueError:
        return None


def read_back(repo, files, sh, say):
    existing = jget([f"repos/{repo}/rulesets"], sh)
    if existing is None:
        say(f"FAIL cannot list rulesets on {repo}")
        return False
    by_name = {r["name"]: r["id"] for r in existing}
    ok = True
    for fname, want in files:
        rid = by_name.get(want["name"])
        if rid is None:
            say(f"missing  {want['name']}  <- {fname}  (apply would POST it)")
            ok = False
            continue
        got = jget([f"repos/{repo}/rulesets/{rid}"], sh)
        diff = ["unreadable"] if got is None else ruleset_diff(want, got)
        if diff:
            say(f"differs  {want['name']}  <- {fname}  (id {rid}): {', '.join(diff)}")
            ok = False
        else:
            say(f"matches  {want['name']}  <- {fname}  (id {rid})")
    extra = sorted(set(by_name) - {w["name"] for _, w in files})
    for name in extra:
        say(f"note     {name} is on GitHub and in no file here (left alone)")
    return ok


def effective(repo, branches, checks, sh, say):
    ok = True
    for b in branches:
        why = rules_problem(b, jget([f"repos/{repo}/rules/branches/{b}"], sh), checks)
        say(f"FAIL {why}" if why else f"ok   {b}: PR-only, no deletion, no force push"
            + (f", requires {', '.join(checks)}" if b == "develop" and checks else ""))
        ok = ok and why is None
    return ok


def push_probe(repo, branches, sh, say):
    ok = True
    for b in branches:
        head = jget([f"repos/{repo}/commits/{b}"], sh)
        if not head:
            say(f"FAIL {b}: cannot read the head")
            ok = False
            continue
        old, tree = head["sha"], head["commit"]["tree"]["sha"]
        made = jget([f"repos/{repo}/git/commits", "-X", "POST", "-f",
                     "message=CB-481 ruleset probe: this update must be refused",
                     "-f", f"tree={tree}", "-f", f"parents[]={old}"], sh)
        if not made:
            say(f"FAIL {b}: could not make the probe commit object")
            ok = False
            continue
        rc, _, err = gh(["api", "-X", "PATCH", f"repos/{repo}/git/refs/heads/{b}",
                         "-f", f"sha={made['sha']}", "-F", "force=false"], sh)
        if rc != 0 and refused_by_rules(err):
            say(f"ok   {b}: direct update refused by the rules ({err.strip().splitlines()[0] if err.strip() else 'no text'})")
        elif rc != 0:
            say(f"FAIL {b}: refused, but not by a rule; GitHub said: {err.strip()}")
            ok = False
        else:
            ok = False
            rc2, _, err2 = gh(["api", "-X", "PATCH", f"repos/{repo}/git/refs/heads/{b}",
                               "-f", f"sha={old}", "-F", "force=true"], sh)
            say(f"FAIL {b}: GitHub ACCEPTED a direct update; "
                + (f"put back to {old}" if rc2 == 0 else f"COULD NOT put back to {old}: {err2.strip()}"))
    return ok


def main(argv=None, sh=None, say=print, env=None):
    argv = sys.argv[1:] if argv is None else argv
    env = os.environ if env is None else env
    here = os.path.dirname(os.path.abspath(__file__))
    repo = env.get("REPO", "Uplift-Foundation/Orbweaver-Mobile")
    directory = env.get("RULESETS_DIR", os.path.join(here, "rulesets-mobile"))
    branches = env.get("PROBE_BRANCHES", "main develop").split()
    checks = env.get("REQUIRED_CHECKS", "relay contracts tools").split()
    say(f"# {repo}, files in {directory}")
    a = read_back(repo, load_files(directory), sh, say)
    b = effective(repo, branches, checks, sh, say)
    c = True
    if "--push-probe" in argv:
        if b:
            c = push_probe(repo, branches, sh, say)
        else:
            say("skip push probe: the effective rules above are not in force")
            c = False
    return 0 if a and b and c else 1


if __name__ == "__main__":
    sys.exit(main())
