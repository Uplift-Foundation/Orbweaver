#!/usr/bin/env python3
"""Tests for the Paperclip bug-fix org package (paperclip/bugfix-org, CB-455).

    python3 tools/test-paperclip-bugfix-org.py

Plain stdlib unittest, like the other tools/test-*.py (no YAML library on the
runners). Paperclip reads .paperclip.yaml with its own small reader, and it has
three known failure modes (a YAML anchor, a comment after a value, an unquoted
string containing a colon), so this reads the file with an equally small reader
of its own and checks the three directly. It then pins the brakes the plan
(docs/plans/paperclip-bugfix-org-plan.md, section 6) relies on, the workspace
strategy, the Bug-only rule and the PR-body shape, and the sandbox profile's
deny for /tmp/ow-install.lock.

Every check is paired with a negative control: the same check run on a copy of
the input with the thing removed or broken must fail, so a check that accepted
anything would fail here instead of looking healthy. What this does NOT show is
that Paperclip imports the package or that the profile denies the real lock
path under the real service; the README labels those.
"""
import os
import re
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.join(HERE, "..", "paperclip", "bugfix-org")
PROFILE = os.path.join(HERE, "..", "paperclip", "triage-org", "host", "paperclip-service.sb.template")
AGENTS = {
    "marisol-vandenberg": "cto",
    "hana-moriyama": "engineer",
    "rafaela-quintero": "engineer",
    "tamsin-oyelaran": "qa",
}
TRIAGE_SLUGS = {"jennifer-voss", "nadia-okafor", "juno-abernathy", "ines-carvalho"}
LOCK = "/private/tmp/ow-install.lock"


def read(*parts):
    with open(os.path.join(PKG, *parts), encoding="utf-8") as f:
        return f.read()


# ---- a reader for the subset of YAML .paperclip.yaml uses -------------------
def unquoted(value):
    """The value with any quoted span blanked out, so the checks below only look
    at text YAML itself would interpret."""
    return re.sub(r'"[^"]*"|\'[^\']*\'', '""', value)


def scalar(raw):
    raw = raw.strip()
    if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in "\"'":
        return raw[1:-1]
    if raw in ("true", "false"):
        return raw == "true"
    if re.fullmatch(r"-?\d+", raw):
        return int(raw)
    return raw


def lint(text):
    """The three things Paperclip's reader got wrong. Returns a list of
    problems, empty when the text is safe for it."""
    problems = []
    for n, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        bare = unquoted(line)
        if re.search(r"(^|[\s:\[,])[&*][A-Za-z_]", bare) or "<<:" in bare:
            problems.append(f"line {n}: YAML anchor or alias")
        m = re.match(r"\s*(?:- )?[^\s:#][^:]*:\s*(.*)$", bare)
        if m is None:
            continue
        value = m.group(1)
        if re.search(r"\s#", " " + value) and value:
            problems.append(f"line {n}: comment after a value")
        if value and ": " in value:
            problems.append(f"line {n}: unquoted string containing a colon")
    return problems


def parse(text):
    """Indentation reader: nested maps, and lists whose items are maps or
    scalars. Enough for this file; raises on anything else."""
    root = {}
    stack = [(-1, root)]
    lines = [l for l in text.splitlines() if l.strip() and not l.strip().startswith("#")]
    i = 0
    while i < len(lines):
        line = lines[i]
        indent = len(line) - len(line.lstrip())
        body = line.strip()
        while stack[-1][0] >= indent:
            stack.pop()
        parent = stack[-1][1]
        if body.startswith("- "):
            item_body = body[2:]
            if not isinstance(parent, list):
                raise ValueError(f"list item outside a list: {line!r}")
            if ":" in item_body and not item_body.startswith(('"', "'")):
                k, _, v = item_body.partition(":")
                item = {}
                parent.append(item)
                if v.strip():
                    item[k.strip()] = scalar(v)
                stack.append((indent, item))
                # sibling keys of this item sit two columns deeper
                stack.append((indent + 1, item))
            else:
                parent.append(scalar(item_body))
            i += 1
            continue
        k, sep, v = body.partition(":")
        if not sep:
            raise ValueError(f"cannot read line {line!r}")
        k = k.strip()
        if v.strip():
            parent[k] = scalar(v)
        else:
            nxt = lines[i + 1] if i + 1 < len(lines) else ""
            child = [] if nxt.strip().startswith("- ") else {}
            parent[k] = child
            stack.append((indent, child))
        i += 1
    return root


def brakes(cfg):
    """Everything wrong with the brakes in a parsed config, as strings."""
    bad = []
    agents = cfg.get("agents", {})
    if set(agents) != set(AGENTS):
        bad.append(f"agents are {sorted(agents)}")
    for slug, a in agents.items():
        c = a.get("adapter", {}).get("config", {})
        h = a.get("runtime", {}).get("heartbeat", {})
        env = c.get("env", {})
        if c.get("timeoutSec") != 3600:
            bad.append(f"{slug}: timeoutSec is {c.get('timeoutSec')!r}")
        if h.get("maxConcurrentRuns") != 1:
            bad.append(f"{slug}: maxConcurrentRuns is {h.get('maxConcurrentRuns')!r}")
        daily = h.get("maxDailyRuns")
        if not isinstance(daily, int) or daily < 1:
            bad.append(f"{slug}: maxDailyRuns is {daily!r}")
        if h.get("enabled") is not False:
            bad.append(f"{slug}: heartbeat timer is on")
        for var in ("AVALONIA_TELEMETRY_OPTOUT", "DOTNET_CLI_TELEMETRY_OPTOUT"):
            if env.get(var, {}).get("value") != "1":
                bad.append(f"{slug}: {var} is not 1")
        if a.get("role") != AGENTS.get(slug):
            bad.append(f"{slug}: role is {a.get('role')!r}")
    proj = cfg.get("projects", {}).get("orbweaver-develop", {})
    pol = proj.get("executionWorkspacePolicy", {})
    strat = pol.get("workspaceStrategy", {})
    if strat.get("type") != "git_worktree":
        bad.append(f"workspace strategy is {strat.get('type')!r}")
    if not str(strat.get("branchTemplate", "")).startswith("bugfix/pc-"):
        bad.append(f"branch template is {strat.get('branchTemplate')!r}")
    if strat.get("baseRef") != "origin/develop":
        bad.append(f"baseRef is {strat.get('baseRef')!r}")
    routine = cfg.get("routines", {}).get("fix-cb-bugs", {})
    if routine.get("concurrencyPolicy") != "coalesce_if_active":
        bad.append("routine does not coalesce")
    return bad


def profile_denies_lock(text):
    """True when a rule denying writes to the lock comes after every allow of
    temp space, which is what makes it win (last matching rule wins in SBPL)."""
    lines = text.splitlines()
    deny = [n for n, l in enumerate(lines)
            if re.match(r"\s*\(deny\s+file-write\*", l) and LOCK in l and not l.lstrip().startswith(";")]
    allows = [n for n, l in enumerate(lines)
              if re.match(r"\s*\(allow\b", l) and ("/private/tmp" in l or "(allow default)" in l)]
    return bool(deny) and all(d > a for d in deny for a in allows)


class YamlSafetyTests(unittest.TestCase):
    def test_package_yaml_is_safe_for_paperclips_reader(self):
        self.assertEqual(lint(read(".paperclip.yaml")), [])

    def test_lint_catches_each_known_failure(self):
        # Negative controls: each of the three shapes is rejected.
        self.assertTrue(lint("a:\n  b: &anchor\n    c: 1\n"))
        self.assertTrue(lint("a:\n  b: *anchor\n"))
        self.assertTrue(lint("a:\n  enabled: false  # why\n"))
        self.assertTrue(lint("a:\n  cmd: run: now\n"))
        # ...and their safe spellings pass.
        self.assertEqual(lint('a:\n  # a comment on its own line\n  cmd: "run: now"\n  cron: "0 10 * * 1-5"\n'), [])

    def test_the_cron_star_is_not_an_alias(self):
        self.assertIn("cronExpression", read(".paperclip.yaml"))
        self.assertEqual(lint('x:\n  cronExpression: "0 10 * * 1-5"\n'), [])


class BrakeTests(unittest.TestCase):
    def setUp(self):
        self.text = read(".paperclip.yaml")
        self.cfg = parse(self.text)

    def test_every_brake_is_present(self):
        self.assertEqual(brakes(self.cfg), [])

    def test_each_brake_fails_when_removed_or_loosened(self):
        # Negative control per brake, on a copy of the real file.
        for old, new, label in [
            ("timeoutSec: 3600", "timeoutSec: 7200", "timeout"),
            ("maxConcurrentRuns: 1", "maxConcurrentRuns: 4", "concurrency"),
            ("maxDailyRuns: 3", "maxDailyRuns: 0", "daily runs"),
            ("AVALONIA_TELEMETRY_OPTOUT:", "AVALONIA_TELEMETRY_OPTOUT_X:", "avalonia opt-out"),
            ("DOTNET_CLI_TELEMETRY_OPTOUT:", "DOTNET_CLI_TELEMETRY_OPTOUT_X:", "dotnet opt-out"),
            ("type: git_worktree", "type: project_primary", "workspace strategy"),
            ("bugfix/pc-{{slug}}", "feature/{{slug}}", "branch template"),
            ("baseRef: origin/develop", "baseRef: origin/main", "base ref"),
            ("concurrencyPolicy: coalesce_if_active", "concurrencyPolicy: always_enqueue", "routine"),
            ("enabled: false", "enabled: true", "heartbeat timer"),
        ]:
            with self.subTest(label):
                self.assertIn(old, self.text)
                self.assertTrue(brakes(parse(self.text.replace(old, new, 1))), label)

    def test_agent_set_is_a_lead_two_engineers_and_qa(self):
        roles = sorted(a["role"] for a in self.cfg["agents"].values())
        self.assertEqual(roles, ["cto", "engineer", "engineer", "qa"])

    def test_personas_do_not_collide_with_the_triage_org(self):
        self.assertFalse(set(self.cfg["agents"]) & TRIAGE_SLUGS)
        # Control: swapping one slug for a triage persona is detected.
        swapped = parse(self.text.replace("hana-moriyama", "juno-abernathy"))
        self.assertTrue(set(swapped["agents"]) & TRIAGE_SLUGS)

    def test_agent_files_match_the_config(self):
        for slug, role in AGENTS.items():
            doc = read("agents", slug, "AGENTS.md")
            self.assertIn(f"slug: {slug}\n", doc)
            self.assertIn(f"role: {role}\n", doc)
            self.assertIn("  - cb-bugfix\n", doc)

    def test_routine_task_exists_and_names_the_project(self):
        task = read("tasks", "fix-cb-bugs", "TASK.md")
        self.assertIn("assignee: marisol-vandenberg", task)
        self.assertIn("project: orbweaver-develop", task)
        self.assertIn("orbweaver-develop", self.cfg["projects"])


class InstructionTests(unittest.TestCase):
    def test_lead_carries_the_run_limits_and_the_bug_only_rule(self):
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        for needle in ("at most\n   **two** tickets", "**two** rounds", "ninety minutes",
                       "`Bug`", "bugfix/pc-<slug>", "Verdict: merge", "both required checks"):
            with self.subTest(needle):
                self.assertIn(needle, lead)
        self.assertNotIn("Verdict: merge", read("agents", "hana-moriyama", "AGENTS.md"))

    def test_only_the_lead_is_told_to_transition_tickets(self):
        for slug in AGENTS:
            doc = read("agents", slug, "AGENTS.md")
            has = "transitionJiraIssue" in doc
            self.assertEqual(has, slug == "marisol-vandenberg", slug)

    def test_qa_review_names_both_rids(self):
        qa = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("osx-arm64", qa)
        self.assertIn("win-x64", qa)
        # Control: the engineers' instructions are not where the rids live.
        self.assertNotIn("win-x64", read("agents", "hana-moriyama", "AGENTS.md"))

    def test_nobody_is_told_to_install_or_take_the_lock(self):
        for slug in AGENTS:
            doc = read("agents", slug, "AGENTS.md")
            self.assertNotIn("ow-install.lock", doc)
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("Install on any machine (so never take `/tmp/ow-install.lock`", skill)
        for slug in ("hana-moriyama", "rafaela-quintero"):
            self.assertIn("never install anything on a machine", read("agents", slug, "AGENTS.md"))


def pr_body_problems(body):
    """The same four rules tools/require-team-sdlc.sh applies to a body."""
    missing = []
    if not re.search(r"CB-[0-9]+", body):
        missing.append("key")
    if not re.search(r"^#+ *verified", body, re.I | re.M):
        missing.append("verified")
    if not re.search(r"^#+ *not verified", body, re.I | re.M):
        missing.append("not verified")
    if not re.search(r"installed on", body, re.I):
        missing.append("installed on")
    return missing


class PrBodyShapeTests(unittest.TestCase):
    def shape(self):
        m = re.search(r"```\n(CB-<number>.*?)```", read("skills", "cb-bugfix", "SKILL.md"), re.S)
        self.assertIsNotNone(m, "PR body template not found in the skill")
        return m.group(1).replace("CB-<number>", "CB-123")

    def test_template_in_the_skill_passes_the_hooks_rules(self):
        self.assertEqual(pr_body_problems(self.shape()), [])

    def test_template_says_installed_on_none(self):
        self.assertRegex(self.shape(), r"(?m)^Installed on: none\.")

    def test_each_rule_fails_when_its_part_is_removed(self):
        body = self.shape()
        for label, cut in [("key", ("CB-123", "")), ("verified", ("## Verified", "## V")),
                           ("not verified", ("## Not verified", "## N")),
                           ("installed on", ("Installed on:", "Where:"))]:
            with self.subTest(label):
                self.assertIn(label, pr_body_problems(body.replace(*cut)))

    def test_hook_itself_still_has_the_same_four_rules(self):
        with open(os.path.join(HERE, "require-team-sdlc.sh"), encoding="utf-8") as f:
            hook = f.read()
        for needle in ("CB-[0-9]+", "verified", "not verified", "installed on"):
            self.assertIn(needle, hook)


class SandboxProfileTests(unittest.TestCase):
    def test_profile_denies_writes_to_the_install_lock(self):
        with open(PROFILE, encoding="utf-8") as f:
            self.assertTrue(profile_denies_lock(f.read()))

    def test_check_fails_without_the_deny_or_with_it_in_the_wrong_place(self):
        with open(PROFILE, encoding="utf-8") as f:
            text = f.read()
        without = "\n".join(l for l in text.splitlines() if LOCK not in l or l.lstrip().startswith(";"))
        self.assertFalse(profile_denies_lock(without))
        # A commented-out deny does not count.
        self.assertFalse(profile_denies_lock(without + "\n; (deny file-write* (subpath \"%s\"))\n" % LOCK))
        # A deny placed before the temp-space allow would lose to it.
        rule = '(deny file-write* (subpath "%s"))' % LOCK
        early = rule + "\n" + without
        self.assertFalse(profile_denies_lock(early))

    def test_deny_is_write_only_and_the_profile_still_allows_by_default(self):
        with open(PROFILE, encoding="utf-8") as f:
            text = f.read()
        self.assertRegex(text, r"(?m)^\(allow default\)")
        self.assertNotRegex(text, r"deny file-read\*[^\n]*ow-install\.lock")


if __name__ == "__main__":
    unittest.main()
