#!/usr/bin/env python3
"""Tests for Orbweaver-Mobile's rulesets (CB-481): host/rulesets-mobile/*.json,
host/apply-rulesets.sh run against them, and host/check-rulesets.py.

    python3 tools/test-bugfix-rulesets-mobile.py

stdlib unittest, fakes only: a stub `gh` for the bash apply script and a fake
`sh` for the checker. Nothing here reaches GitHub. Each refusal sits beside a
passing control.

Why these files exist: the warden's everyday token is about to reach the
mobile repository, which had no rulesets and an unprotected `main`. The JSON
mirrors what Orbweaver's rulesets protect, adapted to mobile's CI. The check
names (`relay`, `contracts`, `tools`, all from GitHub Actions, app 15368) were
read off the check runs of mobile's merged PRs, not guessed; they are the only
checks that run on every PR to `develop`, so they are the only ones a ruleset
can require there (`android` and `snapshots` are path-filtered, and a required
check that never starts blocks the merge). Nothing runs on PRs to mobile
`main` (its workflows trigger on `develop` only), so `main` requires a PR and
no checks.
"""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
DIR = os.path.join(ROOT, "host/rulesets-mobile")
MOBILE = "Uplift-Foundation/Orbweaver-Mobile"

_spec = importlib.util.spec_from_file_location("check_rulesets", os.path.join(ROOT, "host/check-rulesets.py"))
check = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(check)

ADMIN = {"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"}
ORG_ADMIN = {"actor_id": None, "actor_type": "OrganizationAdmin", "bypass_mode": "always"}
ADMIN_PR = {"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "pull_request"}
ORG_ADMIN_PR = {"actor_id": None, "actor_type": "OrganizationAdmin", "bypass_mode": "pull_request"}
ACTIONS = 15368


def load(name):
    with open(os.path.join(DIR, name)) as f:
        return json.load(f)


def files():
    return sorted(f for f in os.listdir(DIR) if f.endswith(".json"))


# ---------------------------------------------------------------- JSON

class MobileRulesetJsonTests(unittest.TestCase):
    def test_the_five_files(self):
        self.assertEqual(files(), ["develop-required-checks.json", "main-admins-only.json",
                                   "main-and-develop-prs-only.json", "other-branches-admins-only.json",
                                   "tags-admins-only.json"])

    def test_all_active_distinct_names_and_the_app_never_bypasses(self):
        names = set()
        for f in files():
            r = load(f)
            names.add(r["name"])
            self.assertEqual(r["enforcement"], "active", f)
            for a in r["bypass_actors"]:
                # Only repository admins, org admins and Dependabot; never the
                # org's App, which is what these rules are for.
                self.assertIn((a["actor_type"], a["actor_id"]),
                              {("RepositoryRole", 5), ("OrganizationAdmin", None), ("Integration", 29110)}, f)
        self.assertEqual(len(names), 5)

    def test_main_updates_are_admins_only(self):
        r = load("main-admins-only.json")
        self.assertEqual(r["conditions"]["ref_name"]["include"], ["refs/heads/main"])
        self.assertEqual({x["type"] for x in r["rules"]}, {"update", "deletion", "non_fast_forward"})
        self.assertEqual(r["bypass_actors"], [ADMIN])

    def test_main_and_develop_take_prs_only_and_admins_bypass_only_through_a_pr(self):
        r = load("main-and-develop-prs-only.json")
        self.assertEqual(sorted(r["conditions"]["ref_name"]["include"]), ["refs/heads/develop", "refs/heads/main"])
        self.assertEqual({x["type"] for x in r["rules"]}, {"deletion", "non_fast_forward", "pull_request"})
        # "pull_request" mode, as on Orbweaver: an admin can merge a PR past a
        # rule, never push straight to the branch. That is what makes the
        # direct-push probe refuse an admin too.
        self.assertEqual(sorted(r["bypass_actors"], key=json.dumps), sorted([ADMIN_PR, ORG_ADMIN_PR], key=json.dumps))
        pr = next(x for x in r["rules"] if x["type"] == "pull_request")["parameters"]
        self.assertEqual(pr["required_approving_review_count"], 0)

    def test_develop_requires_the_three_always_run_checks_from_actions(self):
        r = load("develop-required-checks.json")
        self.assertEqual(r["conditions"]["ref_name"]["include"], ["refs/heads/develop"])
        [rule] = r["rules"]
        self.assertEqual(rule["type"], "required_status_checks")
        got = rule["parameters"]["required_status_checks"]
        self.assertEqual(sorted(c["context"] for c in got), ["contracts", "relay", "tools"])
        self.assertTrue(all(c["integration_id"] == ACTIONS for c in got))
        # Control: the path-filtered checks are not required, or a PR that
        # never starts them could never merge.
        self.assertFalse({"android", "snapshots"} & {c["context"] for c in got})
        # And main requires none: no mobile workflow runs on a PR to main.
        for f in files():
            rr = load(f)
            if "refs/heads/main" in rr["conditions"]["ref_name"]["include"]:
                self.assertNotIn("required_status_checks", {x["type"] for x in rr["rules"]}, f)

    def test_other_branches_leave_the_orgs_lane_and_the_two_long_lived_branches(self):
        r = load("other-branches-admins-only.json")
        self.assertEqual(r["conditions"]["ref_name"]["include"], ["~ALL"])
        # No `screenshots` exclusion: mobile has no workflow that pushes a
        # branch (its one push-triggered workflow pushes a Docker image).
        self.assertEqual(sorted(r["conditions"]["ref_name"]["exclude"]),
                         ["refs/heads/bugfix/pc-*", "refs/heads/develop", "refs/heads/main"])
        self.assertEqual({x["type"] for x in r["rules"]}, {"creation", "update", "deletion"})

    def test_tags_are_admins_only_for_every_change(self):
        r = load("tags-admins-only.json")
        self.assertEqual(r["target"], "tag")
        self.assertEqual(r["conditions"]["ref_name"]["include"], ["~ALL"])
        self.assertEqual({x["type"] for x in r["rules"]}, {"creation", "update", "deletion", "non_fast_forward"})
        self.assertEqual(sorted(r["bypass_actors"], key=json.dumps), sorted([ADMIN, ORG_ADMIN], key=json.dumps))

    def test_the_bugfix_org_three_keep_orbweavers_shape(self):
        # Same protection as Orbweaver's host/rulesets, apart from the two
        # stated differences (no screenshots branch; tags cover v*'s rules).
        def rules(d, f):
            with open(os.path.join(d, f)) as fh:
                r = json.load(fh)
            return r["target"], {x["type"] for x in r["rules"]}
        desk = os.path.join(ROOT, "host/rulesets")
        self.assertEqual(rules(DIR, "main-admins-only.json"), rules(desk, "main-admins-only.json"))
        self.assertEqual(rules(DIR, "other-branches-admins-only.json"), rules(desk, "other-branches-admins-only.json"))


# ---------------------------------------------------------------- apply script

STUB_GH = r"""#!/usr/bin/env bash
echo "$@" >> "$GH_LOG"
case "$*" in
  "api apps/dependabot --jq .id") echo "${DEPENDABOT_ID:-29110}" ;;
  "api repos/Uplift-Foundation/Orbweaver-Mobile/rulesets --jq"*) printf '%b' "${EXISTING:-}" ;;
esac
"""


@unittest.skipIf(sys.platform == "win32", "the apply script and its stub gh are bash; the host is a Mac")
class ApplyToMobileTests(unittest.TestCase):
    def run_script(self, *args, **env):
        with tempfile.TemporaryDirectory() as d:
            gh = os.path.join(d, "gh")
            with open(gh, "w") as f:
                f.write(STUB_GH)
            os.chmod(gh, 0o755)
            log = os.path.join(d, "log")
            e = dict(os.environ, PATH=d + os.pathsep + os.environ["PATH"], GH_LOG=log,
                     REPO=MOBILE, RULESETS_DIR=DIR, **env)
            p = subprocess.run(["bash", os.path.join(ROOT, "host/apply-rulesets.sh"), *args],
                               capture_output=True, text=True, env=e)
            calls = []
            if os.path.exists(log):
                with open(log) as f:
                    calls = f.read().splitlines()
            return p, calls

    @staticmethod
    def writes(calls):
        return [c for c in calls if "-X POST" in c or "-X PUT" in c]

    def test_dry_run_names_five_posts_to_mobile_and_writes_nothing(self):
        p, calls = self.run_script()
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(self.writes(calls), [])
        self.assertEqual(p.stdout.count(f"POST repos/{MOBILE}/rulesets"), 5)
        self.assertNotIn("Uplift-Foundation/Orbweaver/", p.stdout)
        self.assertIn("dry run", p.stdout)

    def test_apply_posts_each_file_to_mobile(self):
        p, calls = self.run_script("--apply")
        self.assertEqual(p.returncode, 0, p.stderr)
        w = self.writes(calls)
        self.assertEqual(len(w), 5)
        self.assertTrue(all(f"repos/{MOBILE}/rulesets" in c and c.endswith(".json") for c in w))
        self.assertEqual(sorted(os.path.basename(c.split("--input ")[1]) for c in w), files())

    def test_rerun_puts_by_name_instead_of_duplicating(self):
        name = load("develop-required-checks.json")["name"]
        p, calls = self.run_script("--apply", EXISTING=f"77\\t{name}\\n")
        self.assertEqual(p.returncode, 0, p.stderr)
        w = self.writes(calls)
        self.assertEqual(sum(f"-X PUT repos/{MOBILE}/rulesets/77" in c for c in w), 1)
        self.assertEqual(sum("-X POST" in c for c in w), 4)

    def test_wrong_dependabot_id_refuses_before_any_write(self):
        p, calls = self.run_script("--apply", DEPENDABOT_ID="2")
        self.assertNotEqual(p.returncode, 0)
        self.assertIn("refusing", p.stderr)
        self.assertEqual(self.writes(calls), [])


# ---------------------------------------------------------------- checker

class FakeGitHub:
    """Answers the checker's `gh api` calls from a dict of rulesets and
    effective rules; records every argv."""

    def __init__(self, rulesets=None, effective=None, accept_push=False, refusal="GH013 Repository rule violations found"):
        self.rulesets = rulesets or {}          # id -> body (with name)
        self.effective = effective or {}        # branch -> rules list
        self.accept_push, self.refusal = accept_push, refusal
        self.calls = []

    def __call__(self, argv):
        self.calls.append(argv)
        a = argv[2:]  # after "gh api"
        path = a[0] if a[0] != "-X" else a[2]

        def ok(obj):
            return subprocess.CompletedProcess(argv, 0, json.dumps(obj), "")
        if path == f"repos/{MOBILE}/rulesets":
            return ok([{"id": i, "name": r["name"]} for i, r in self.rulesets.items()])
        if path.startswith(f"repos/{MOBILE}/rulesets/"):
            return ok(self.rulesets[int(path.rsplit("/", 1)[1])])
        if path.startswith(f"repos/{MOBILE}/rules/branches/"):
            b = path.rsplit("/", 1)[1]
            return ok(self.effective.get(b, []))
        if path.startswith(f"repos/{MOBILE}/commits/"):
            return ok({"sha": "1" * 40, "commit": {"tree": {"sha": "2" * 40}}})
        if path == f"repos/{MOBILE}/git/commits":
            return ok({"sha": "3" * 40})
        if path.startswith(f"repos/{MOBILE}/git/refs/heads/"):
            if "sha=" + "1" * 40 in argv:  # the put-back
                return ok({})
            if self.accept_push:
                return ok({})
            return subprocess.CompletedProcess(argv, 1, "", self.refusal + " (HTTP 422)")
        return subprocess.CompletedProcess(argv, 1, "", "unexpected " + path)


def applied():
    """Every file as GitHub would echo it back: with ids and the defaults it
    adds (allowed_merge_methods, source fields), so a match proves the
    comparison ignores what the file does not say."""
    out = {}
    for i, f in enumerate(files(), start=1):
        r = load(f)
        r = json.loads(json.dumps(r))
        r.update(id=i, source="Uplift-Foundation/Orbweaver-Mobile", source_type="Repository")
        for rule in r["rules"]:
            if rule["type"] == "pull_request":
                rule["parameters"]["allowed_merge_methods"] = ["merge", "squash", "rebase"]
        r["rules"].reverse()
        out[i] = r
    return out


ENFORCED = {
    "main": [{"type": "deletion"}, {"type": "non_fast_forward"}, {"type": "pull_request"}, {"type": "update"}],
    "develop": [{"type": "deletion"}, {"type": "non_fast_forward"}, {"type": "pull_request"},
                {"type": "required_status_checks", "parameters": {"required_status_checks": [
                    {"context": "relay"}, {"context": "contracts"}, {"context": "tools"}]}}],
}


def run_check(fake, *argv):
    lines = []
    env = {"REPO": MOBILE, "RULESETS_DIR": DIR}
    rc = check.main(list(argv), sh=fake, say=lines.append, env=env)
    return rc, "\n".join(lines)


def patches(fake):
    return [c for c in fake.calls if "PATCH" in c]


class CheckerTests(unittest.TestCase):
    def test_before_apply_every_file_is_missing_and_the_check_fails(self):
        # Mobile today: no rulesets (measured 2026-11-26).
        rc, out = run_check(FakeGitHub())
        self.assertEqual(rc, 1)
        self.assertEqual(out.count("missing"), 5)
        self.assertIn("apply would POST it", out)
        self.assertIn("FAIL main: no deletion, non_fast_forward, pull_request rule in force", out)

    def test_after_apply_all_match_and_the_rules_are_in_force(self):
        rc, out = run_check(FakeGitHub(applied(), ENFORCED))
        self.assertEqual(rc, 0, out)
        self.assertEqual(out.count("matches"), 5)
        self.assertIn("ok   develop: PR-only, no deletion, no force push, requires relay, contracts, tools", out)

    def test_a_changed_ruleset_on_github_is_named_with_the_field(self):
        rs = applied()
        i = next(k for k, v in rs.items() if v["name"].startswith("other branches"))
        rs[i]["conditions"]["ref_name"]["exclude"].append("refs/heads/feature/*")
        rc, out = run_check(FakeGitHub(rs, ENFORCED))
        self.assertEqual(rc, 1)
        self.assertIn("differs  other branches: restricted to admins (bugfix org)", out)
        self.assertIn(": conditions", out)

    def test_a_missing_required_check_fails_develop(self):
        eff = json.loads(json.dumps(ENFORCED))
        eff["develop"][3]["parameters"]["required_status_checks"].pop()
        rc, out = run_check(FakeGitHub(applied(), eff))
        self.assertEqual(rc, 1)
        self.assertIn("FAIL develop: required checks not in force: tools", out)

    def test_push_probe_refused_on_both_branches_is_the_pass(self):
        fake = FakeGitHub(applied(), ENFORCED)
        rc, out = run_check(fake, "--push-probe")
        self.assertEqual(rc, 0, out)
        self.assertIn("ok   main: direct update refused by the rules", out)
        self.assertIn("ok   develop: direct update refused by the rules", out)
        self.assertTrue(all("force=false" in c for c in patches(fake)))
        self.assertEqual(len(patches(fake)), 2)

    def test_push_probe_accepted_fails_and_puts_the_branch_back(self):
        fake = FakeGitHub(applied(), ENFORCED, accept_push=True)
        rc, out = run_check(fake, "--push-probe")
        self.assertEqual(rc, 1)
        self.assertIn("GitHub ACCEPTED a direct update; put back to " + "1" * 40, out)
        self.assertTrue(any("force=true" in c and "sha=" + "1" * 40 in c for c in patches(fake)))

    def test_a_refusal_that_is_not_a_rule_is_not_a_pass(self):
        fake = FakeGitHub(applied(), ENFORCED, refusal="Bad credentials")
        rc, out = run_check(fake, "--push-probe")
        self.assertEqual(rc, 1)
        self.assertIn("refused, but not by a rule", out)

    def test_push_probe_never_runs_while_the_rules_are_not_in_force(self):
        fake = FakeGitHub()
        rc, out = run_check(fake, "--push-probe")
        self.assertEqual(rc, 1)
        self.assertIn("skip push probe", out)
        self.assertEqual(patches(fake), [])
        self.assertFalse([c for c in fake.calls if "git/commits" in " ".join(c)])

    def test_rules_problem(self):
        self.assertIn("could not read", check.rules_problem("main", None, []))
        self.assertIsNone(check.rules_problem("main", ENFORCED["main"], ["relay"]))  # checks only on develop
        self.assertIsNone(check.rules_problem("develop", ENFORCED["develop"], []))


class CiWiringTests(unittest.TestCase):
    def test_ci_runs_this_file(self):
        with open(os.path.join(ROOT, ".github/workflows/ci.yml")) as f:
            self.assertIn("run: python tools/test-bugfix-rulesets-mobile.py", f.read())


if __name__ == "__main__":
    unittest.main()
