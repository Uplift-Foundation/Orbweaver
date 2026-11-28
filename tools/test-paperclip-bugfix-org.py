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
import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.join(HERE, "..", "paperclip", "bugfix-org")
POLICY = os.path.join(PKG, "host", "project-workspace-policy.json")
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


def pass_title_is_per_fire(task_md):
    """True when the routine task's `name:` renders differently on every fire."""
    m = re.search(r"^name:\s*(.+)$", task_md, re.M)
    return bool(m) and re.search(r"\{\{\s*timestamp\s*\}\}", m.group(1)) is not None


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


def brakes(cfg, policy):
    """Everything wrong with the brakes in a parsed config and the project
    workspace policy JSON, as strings."""
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
    strat = policy.get("workspaceStrategy", {})
    if policy.get("enabled") is not True or policy.get("defaultMode") != "isolated_workspace":
        bad.append("workspace policy is not an enabled isolated workspace")
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
        self.policy_text = read("host", "project-workspace-policy.json")
        self.policy = json.loads(self.policy_text)

    def test_every_brake_is_present(self):
        self.assertEqual(brakes(self.cfg, self.policy), [])

    def test_yaml_stays_clean_for_the_agent_safe_import_route(self):
        # The CLI sends every existing-company import through the agent-safe
        # route, which refuses a project executionWorkspacePolicy (measured in
        # a dry run), so the policy lives in host/ and the YAML must not have one.
        self.assertNotIn("executionWorkspacePolicy", self.text.replace("# ", "").split("projects:")[1].split("orbweaver-develop:")[1])
        for forbidden in ("setupCommand", "cleanupCommand", "executionWorkspaceSettings", "assigneeAdapterOverrides"):
            self.assertNotIn(forbidden + ":", self.text)
        # Control: the check does notice one.
        bad = self.text.replace("    leadAgentSlug: marisol-vandenberg\n",
                                "    leadAgentSlug: marisol-vandenberg\n    executionWorkspacePolicy:\n      enabled: true\n")
        self.assertIn("executionWorkspacePolicy:", bad.split("orbweaver-develop:")[1])
        self.assertEqual(self.text.count("kind: schedule"), 1)

    def test_each_brake_fails_when_removed_or_loosened(self):
        # Negative control per brake, on a copy of the real file it lives in.
        for old, new, label, in_policy in [
            ("timeoutSec: 3600", "timeoutSec: 7200", "timeout", False),
            ("maxConcurrentRuns: 1", "maxConcurrentRuns: 4", "concurrency", False),
            ("maxDailyRuns: 120", "maxDailyRuns: 0", "daily runs", False),
            ("AVALONIA_TELEMETRY_OPTOUT:", "AVALONIA_TELEMETRY_OPTOUT_X:", "avalonia opt-out", False),
            ("DOTNET_CLI_TELEMETRY_OPTOUT:", "DOTNET_CLI_TELEMETRY_OPTOUT_X:", "dotnet opt-out", False),
            ("concurrencyPolicy: coalesce_if_active", "concurrencyPolicy: always_enqueue", "routine", False),
            ("enabled: false", "enabled: true", "heartbeat timer", False),
            ('"git_worktree"', '"project_primary"', "workspace strategy", True),
            ("bugfix/pc-{{slug}}", "feature/{{slug}}", "branch template", True),
            ('"origin/develop"', '"origin/main"', "base ref", True),
        ]:
            with self.subTest(label):
                src = self.policy_text if in_policy else self.text
                self.assertIn(old, src)
                mutated = src.replace(old, new, 1)
                cfg, pol = (self.cfg, json.loads(mutated)) if in_policy else (parse(mutated), self.policy)
                self.assertTrue(brakes(cfg, pol), label)

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

    def test_each_pass_gets_its_own_worktree(self):
        # CB-482: the branch is bugfix/pc-<slug of the title>, so a fixed title
        # put every pass in one worktree, and Paperclip's reaper deleted it under
        # a running pass a day after an earlier pass closed. {{timestamp}} is the
        # only built-in that differs between two fires 30 minutes apart.
        task = read("tasks", "fix-cb-bugs", "TASK.md")
        self.assertTrue(pass_title_is_per_fire(task), "routine title is the same every fire")
        # Negative controls: develop's fixed title, and {{date}} (48 fires a day share it).
        self.assertFalse(pass_title_is_per_fire(task.replace(" {{timestamp}}", "")))
        self.assertFalse(pass_title_is_per_fire(task.replace("{{timestamp}}", "{{date}}")))
        # The README says why, so the next edit does not "tidy" the title back.
        self.assertIn("One worktree per pass (CB-482)", read("README.md"))


class InstructionTests(unittest.TestCase):
    def test_ticket_key_goes_on_the_first_description_line_never_the_title(self):
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("first line of the issue's description exactly `Ticket: CB-NNN`", lead)
        self.assertIn("never in the title", lead)
        self.assertIn("starts with the line\n`Ticket: CB-NNN`", skill)
        # Control: the form the warden parses is a real match for a key, and a
        # title carrying a key would put it in the branch name.
        self.assertRegex("Ticket: CB-455\nplan", r"\ATicket: (CB-\d+)\n")
        self.assertIsNone(re.match(r"\ATicket: (CB-\d+)\n", "plan\nTicket: CB-455"))
        self.assertNotIn("Ticket: CB-NNN", read("agents", "hana-moriyama", "AGENTS.md"))

    def test_release_and_merge_leave_a_status_the_host_can_read(self):
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn('{"status": "cancelled"}', lead)
        self.assertIn('{"status": "done"}', lead)
        self.assertIn("`cancelled` only when the ticket was released", skill)
        # Control: the engineers are never told to settle a ticket.
        for slug in ("hana-moriyama", "rafaela-quintero"):
            self.assertNotIn("cancelled", read("agents", slug, "AGENTS.md"))

    def test_lead_points_at_the_limits_and_carries_the_bug_only_rule(self):
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        for needle in ("the run limits in the skill's **Limits**", "past a per-ticket limit in the skill's\n   **Limits**",
                       "past the skill's fix-round limit, release",
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

    def test_released_work_is_retakable_and_pauses_do_not_count(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        for needle in ("**This org's own released work** is a branch with no one working on it",
                       "orbweaver-bugfix[bot]", "--state open --head <branch>",
                       "continuing released work", "the branch\nname with its last commit sha",
                       "$BUGFIX_SPOOL/pauses.json", "`paused_minutes`"):
            with self.subTest(needle):
                self.assertIn(needle, skill)
        self.assertIn("the skill's **Limits** says (paused time does not count)",
                      read("agents", "marisol-vandenberg", "AGENTS.md"))

    # CB-480: two passes read 18 bugs, took none and changed nothing. Each
    # rule below is one of the ticket's five outcomes, pinned in the text the
    # lead actually reads, with a control that the old skip is gone.
    def test_query_is_the_plain_refinement_query(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("project = CB AND issuetype = Bug AND status = Refinement ORDER BY", skill)
        # Control: item 3 was changed to no hand-offs, so no label filter.
        self.assertNotIn("handoff-team", skill)

    def test_every_ticket_gets_one_of_the_outcomes(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        for needle in ("## Every ticket gets an outcome",
                       "**No ticket the query returns is skipped in silence.**",
                       "- **Wait: another team's PR.**", "- **Wait: triage.**",
                       "- **Wait: mobile access.**", "- **Close.**", "- **Take it: fix or reproduce.**"):
            with self.subTest(needle):
                self.assertIn(needle, skill)
        # Control: the old skip list, and the old leave-alone paragraph, are gone.
        self.assertNotIn("Skip a ticket when **any** of these holds", skill)
        self.assertNotIn("Leave alone, and say why if asked", skill)

    def test_lead_may_close_with_evidence_and_no_claim(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        for needle in ("**already fixed:**", "**duplicate:**", "**cannot reproduce:**",
                       "transition `41`", "`[bugfix-org] closed:`",
                       "A close is\n   **not a release and needs no claim**",
                       "Reading the code and concluding it looks fine is not an\n     attempt"):
            with self.subTest(needle):
                self.assertIn(needle, skill)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("Done when the PR is merged to `develop` or\n   when you close the ticket", lead)

    def test_limits_are_stated_once_in_the_skill(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        limits = skill.split("## Limits", 1)[1].split("\n## ", 1)[0]
        for needle in ("**The limits are stated here and nowhere else;**", "**two tickets taken**",
                       "**five closes** (which do not use the two)", "**two tickets with\n  org PRs open**",
                       "**two fix rounds**", "**ninety minutes**", "**one rebase**"):
            with self.subTest(needle):
                self.assertIn(needle, limits)
        self.assertIn("**Closes do not use the ticket limit; they have their own cap (**Limits**).**", skill)
        # Outside Limits, the numbers are not restated anywhere an agent reads them.
        rest = skill.replace(limits, "")
        for pattern in (r"\bninety\b", r"\*\*two\*\*", r"\bfive closes\b", r"two tickets per", r"two fix rounds",
                        r"two rounds\b", r"at most \*\*five\*\*"):
            with self.subTest(pattern):
                self.assertIsNone(re.search(pattern, rest), pattern)
                for doc in [read("tasks", "fix-cb-bugs", "TASK.md")] + [read("agents", a, "AGENTS.md") for a in AGENTS]:
                    self.assertIsNone(re.search(pattern, doc), pattern)

    def test_close_never_takes_a_persons_ticket(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("Close only a\n   ticket in Refinement or one the org itself claimed", skill)
        self.assertIn("| closed | Refinement, or a ticket the org claimed, to Done (`41`)", skill)
        # Control: the "any status" wording that could close a person's ticket is gone.
        self.assertNotIn("any status to Done", skill)

    def test_a_captured_prompt_is_taken_and_captured_by_qa(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        for needle in ("4. **Bugs that need a real captured prompt**",
                       "`gh pr create ... --draft`", "capture task",
                       "**If the capture cannot be made as first planned**",
                       'and "it needs a person" is never one'):
            with self.subTest(needle):
                self.assertIn(needle, skill)
        qa = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("**A capture task**", qa)
        self.assertIn("always send\n`machine_release`", qa)
        for slug in ("hana-moriyama", "rafaela-quintero"):
            self.assertIn("open the PR as\n   a draft first", read("agents", slug, "AGENTS.md"))
        # Control: the lead no longer releases a ticket for needing a capture.
        self.assertNotIn("need the mobile repository or a real captured prompt",
                         read("agents", "marisol-vandenberg", "AGENTS.md"))

    # CB-488: CB-373 and CB-359 were released five times between them because
    # the last proof "needed a person". Each rule that replaces that release
    # is pinned in the text the agent reads, and the old release wording is
    # checked gone from every file, so it cannot come back beside the new rule.
    def test_a_proof_that_needs_a_person_is_never_a_release(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## A proof that seems to need a person", 1)[1].split("\n## ", 1)[0]
        for needle in ("**No ticket is released because its last proof needs a person at a machine.**",
                       "**Name the observation.**",
                       "1. **The real CLI and the installed app on `mini`.**",
                       "**A service the bug needs is one the org starts itself.**",
                       "**leave it\n   pending**",
                       "2. **Both ends in one test.**",
                       "**unedited capture from\n   step 1**",
                       "negative control"):
            with self.subTest(needle):
                self.assertIn(needle, section)
        # Control: the two clauses that told the org to release are gone.
        for gone in ("such as a person signing in",
                     "release it when the capture needs something",
                     "release the ticket if the proof was the point of it"):
            with self.subTest(gone):
                self.assertNotIn(gone, skill)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("**Never release a ticket because a proof needs\n   a person at a machine**", lead)
        self.assertIn("a proof that\nneeds a person is not such a case", lead)
        for slug in ("hana-moriyama", "rafaela-quintero"):
            self.assertIn('Never\n   report "needs a person" as the reason you stopped',
                          read("agents", slug, "AGENTS.md"))

    def test_what_is_enough_without_the_exact_condition_is_stated(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## A proof that seems to need a person", 1)[1].split("\n## ", 1)[0]
        for needle in ("- **There is a fix.**", "fails before the fix and passes after",
                       "`## Not verified` names\n  the condition not exercised",
                       "- **There is no defect to fix.**", "tests-only PR",
                       "closed as cannot-reproduce",
                       "lists what a reporter should attach to reopen it",
                       'It is\nnot "merge unverified"'):
            with self.subTest(needle):
                self.assertIn(needle, section)
        line = "`Proof without a person: <step 1 or 2, and what it showed>; not exercised: <the condition>`"
        self.assertIn(line, section)
        self.assertIn("the lead does not merge without that line at the reviewed sha", section)
        self.assertIn(line, read("agents", "tamsin-oyelaran", "AGENTS.md"))
        self.assertIn("carries the `Proof without a person:` line", skill.split("## QA and merge", 1)[1])
        self.assertIn("do\n   not merge such a PR without Tamsin's `Proof without a person:` line",
                      read("agents", "marisol-vandenberg", "AGENTS.md"))
        # The bar is not lowered to a merge with no failing test under it: QA
        # is told the same condition the skill states.
        qa = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("fails before the change and passes after", qa)

    def test_a_close_without_the_exact_condition_is_not_a_release(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## A proof that seems to need a person", 1)[1].split("\n## ", 1)[0]
        self.assertIn("**Neither close is a release.** The engineer's Paperclip issue ends `done`,\n"
                      "never `cancelled`", section)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("close made that way ends the engineer's issue `done`, never `cancelled`", lead)
        # The wrong-host close has to be disputable from the ticket itself.
        self.assertIn("**quotes the ticket's own words that show the\n  host**", section)
        self.assertIn("A\n  guess about the host with no words from the ticket under it is not a reason\n  to close.",
                      section)
        self.assertIn("your comment quotes the ticket's own words that show the host", lead)
        # Control, on the warden itself: the release brake reads only issues
        # Paperclip lists as cancelled, so an issue that ends `done` never
        # reaches it, while the same ticket's issue cancelled does count; and a
        # merge resets the run of releases.
        import sys
        sys.path.insert(0, os.path.join(HERE, "..", "host"))
        import warden
        state = {"cancelled_seen": []}
        self.assertEqual(warden.new_releases([], state), [])            # ended `done`: not listed
        self.assertEqual(warden.new_releases(["i1"], state), ["i1"])    # ended `cancelled`
        row = {"id": "i1", "description": "Ticket: CB-359\nplan"}
        self.assertEqual(warden.classify_release(row, None), ("release", "CB-359"))
        self.assertEqual(warden.next_release_state({"consecutive_releases": 1}, "merged"), 0)
        self.assertEqual(warden.next_release_state({"consecutive_releases": 1}, "released"), 2)

    # QA's finding on CB-488: "every stage behaved" says nothing about code
    # only the unexercised condition reaches, which on CB-359 is the code the
    # ticket is about. So a close has to name that code and show the capture
    # ran it, and where it did not, the ticket is put back, never closed.
    def test_code_only_the_missing_condition_reaches_is_put_back_not_closed(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## A proof that seems to need a person", 1)[1].split("\n## ", 1)[0]
        no_defect = section.split("- **There is no defect to fix.**", 1)[1].split("\n- **", 1)[0]
        self.assertIn("**and the condition not\n  exercised would take the same code**", no_defect)
        self.assertIn("as `file:line`", no_defect)
        only = section.split("- **Some code is reached only by the condition not exercised**", 1)[1].split("\n\n", 1)[0]
        for needle in ("the ticket is **not\n  closed**", "(**Putting a ticket back**)",
                       "`[bugfix-org] stopped: needs an input the org cannot make (",
                       "`Needs an input the org cannot make` line", "not a\n  release and not a close"):
            with self.subTest(needle):
                self.assertIn(needle, only)
        # It is a real put-back: found again as a wait, counted on its own
        # line, and named among the rules that put a ticket back.
        wait = skill.split("- **Wait: put back.**", 1)[1].split("\n- **", 1)[0]
        self.assertIn("`Needs\n   an input the org cannot make`", wait)
        back = skill.split("## Putting a ticket back", 1)[1].split("\n## ", 1)[0]
        self.assertIn("**A proof that seems to\nneed a person**", back)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("if some code is reached only by that condition, do not close", lead)
        self.assertIn("Needs an input the org cannot make, Open outside\n   Refinement), every ticket either query returned", lead)
        qa = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("names the code that would handle\nthe condition not exercised, as `file:line`", qa)
        self.assertIn("the lead then puts the ticket back and does not close it", qa)
        # The lead's two conditions on this outcome. It never feeds the
        # release brake: a put-back ends the engineer's issue `done`, and the
        # warden counts only `cancelled`. And it is not claimed again until a
        # person comments: a pass finds the `stopped:` comment and waits.
        self.assertIn("Paperclip issue to `done` with that same sentence as its `comment`, never\n`cancelled`", back)
        self.assertIn("It stays a\n   wait, with no Jira write", wait)
        self.assertIn("until a\n   person comments after it", wait)
        first = skill.split("## Every ticket gets an outcome", 1)[1]
        self.assertLess(first.index("- **Wait: put back.**"), first.index("- **Take it: fix or reproduce.**"))
        # Control: the close bullet alone no longer covers that case, since
        # the put-back wording is not inside it.
        self.assertNotIn("stopped:", no_defect)

    def test_a_capture_leaves_nothing_in_a_live_account(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## A proof that seems to need a person", 1)[1].split("\n## ", 1)[0]
        for needle in ("**The capture leaves nothing behind in a live account.**",
                       "scratch `CLAUDE_CONFIG_DIR`", "`--strict-mcp-config`, never `claude mcp add`",
                       "token step always refuses", "bind port 0", "stops the server and the `tmux` session",
                       "never a pool account's folder", "**Check it, do not assume it:**",
                       '~/.claude*/*.json ~/.claude*/.*.json ~/.claude.json 2>/dev/null); echo "left behind: ${f:-none}"',
                       "is a failed capture"):
            with self.subTest(needle):
                self.assertIn(needle, section)
        self.assertIn("**Every `osascript` on `macbook` is refused**", skill)

    def test_a_capture_round_is_not_a_fix_round(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        limits = skill.split("## Limits", 1)[1].split("\n## ", 1)[0]
        self.assertIn("**A round that pushed no fix is not a fix round.**", limits)
        self.assertIn("The ninety minutes still bound the\nticket.", limits)
        self.assertIn("never that it needs a person", limits)

    def test_nobody_types_or_clicks_on_the_macbook(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("**A `machine_run` on `macbook` that would type or click is refused.**", skill)
        self.assertIn("**Never, to get a proof:** keyboard or mouse input on `macbook`", skill)
        never = skill.split("## Never", 1)[1]
        self.assertIn("Send keyboard or mouse input to `macbook`", never)
        self.assertIn("never send keyboard or mouse input to `macbook`",
                      read("agents", "tamsin-oyelaran", "AGENTS.md"))
        # Control: the rule the skill states is the one the runner enforces,
        # for the same machine, and the mini is left free.
        import sys
        sys.path.insert(0, os.path.join(HERE, ".."))
        from host.machines import runner
        self.assertEqual(runner.NO_INPUT_MACHINES, ("macbook",))
        req = {"machine": "macbook", "command": "osascript -e 1"}
        self.assertIsNotNone(runner.input_problem(req))
        self.assertIsNone(runner.input_problem(dict(req, machine="mini")))
        # The skill never offers a second copy of the app as a proof: the
        # single-instance claim has one fixed name, so that copy exits.
        self.assertIn("a second copy started by hand exits at once", skill)
        with open(os.path.join(HERE, "..", "Brand.cs"), encoding="utf-8") as f:
            self.assertIn('SingleInstanceMutexName = "Orbweaver_SingleInstance_Mutex"', f.read())

    def test_security_bugs_are_taken_and_never_described_as_an_exploit(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("**Security bugs are taken like any other bug**", skill)
        self.assertIn("describe the class of flaw and\nthe fix, never a working exploit", skill)
        self.assertIn("Write a working exploit into a PR, a\ncommit or a comment.", skill)
        self.assertIn("Security bugs are taken like any other.", read("agents", "marisol-vandenberg", "AGENTS.md"))
        # Control: no list of things the org leaves alone names security any more.
        for doc in (skill, read("README.md"), read("agents", "marisol-vandenberg", "AGENTS.md")):
            self.assertNotIn("a security fix", doc)
            self.assertNotIn("fix a security", doc)
        # Control: no agent is told to edit a Jira field, so no label path exists.
        for slug in AGENTS:
            self.assertNotIn("editJiraIssue", read("agents", slug, "AGENTS.md"))

    # CB-481: the org works Orbweaver-Mobile and workflow files. The CB-480
    # "Wait: org access" rule and every line tagged to it are gone.
    def test_org_access_wait_is_gone(self):
        docs = [read("skills", "cb-bugfix", "SKILL.md"), read("README.md"),
                read("tasks", "fix-cb-bugs", "TASK.md")] + [read("agents", a, "AGENTS.md") for a in AGENTS]
        for doc in docs:
            self.assertNotIn("org access", doc)
            self.assertNotIn("Wait: org access", doc)

    def test_mobile_is_cloned_inside_the_worktree_and_ignored(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        for needle in ("## Two repositories, one code base",
                       "git clone https://github.com/Uplift-Foundation/Orbweaver-Mobile.git mobile",
                       "git -C mobile checkout -b <the worktree's own branch name> origin/develop",
                       "The same branch name in both repositories"):
            with self.subTest(needle):
                self.assertIn(needle, skill)
        with open(os.path.join(HERE, "..", ".gitignore"), encoding="utf-8") as f:
            self.assertIn("/mobile/", f.read().splitlines())

    def test_each_repository_has_its_own_gate(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        table = skill.split("## Two repositories, one code base", 1)[1].split("**The clone.**", 1)[0]
        for needle in ("`build (macos-latest, osx-arm64)` and `build (windows-latest, win-x64)`",
                       "`relay`, `contracts` and `tools` green always",
                       "`android` green when the diff touches `android/` or `contracts/`",
                       "`snapshots` green when it touches `app/`",
                       "no real phone or Watch", "`none (mobile repository)`",
                       "| Machines runner | yes (**Real machines**) | no:"):
            with self.subTest(needle):
                self.assertIn(needle, table)
        qa = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("on a mobile PR, every check the skill's **Two\n  repositories, one code base** table expects", qa)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("`gh pr merge <number> --repo <the PR's repository> --squash`", lead)
        self.assertIn("and the same with `--repo Uplift-Foundation/Orbweaver-Mobile`", lead)
        # Control: the lead no longer merges into Orbweaver only.
        self.assertNotIn("gh pr merge <number> --repo Uplift-Foundation/Orbweaver --squash", lead)

    def test_mobile_unreachable_is_counted_not_written(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        # The check needs the org's own token: the repository is readable
        # anonymously, so a clone or ls-remote succeeding proves nothing.
        self.assertIn("`gh api /installation/repositories --jq '.repositories[].full_name'`", skill)
        self.assertIn("A clone proves nothing", skill)
        self.assertNotIn("git ls-remote", skill)
        self.assertNotIn("git ls-remote", read("README.md"))
        self.assertIn("After a claim the signal is a **refused push**", skill)
        self.assertIn("- **Wait: mobile access.**", skill)
        self.assertIn("counted on `Mobile access not live yet`, and not a defect of the pass", skill)
        self.assertIn("`[bugfix-org] stopped: mobile access not live\nyet; nothing merged; branch kept at <sha>`", skill)

    def test_a_put_back_ticket_stays_a_wait_until_something_changes(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        rule = skill.split("- **Wait: put back.**", 1)[1].split("\n- **", 1)[0]
        for needle in ("`[bugfix-org] stopped:` put-back", "with no Jira write",
                       "until a\n   person comments after it", "the check in **Two\n   repositories, one code base** passes"):
            with self.subTest(needle):
                self.assertIn(needle, rule)
        self.assertIn("(it always starts\n`[bugfix-org] stopped:`", skill)
        # Every put-back sentence carries the prefix a later pass searches for.
        for m in re.finditer(r"`([^`]*)stopped: ", skill):
            self.assertEqual(m.group(1), "[bugfix-org] ", m.group(0))

    def test_the_workflow_token_never_touches_mobile(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("the workflow token is **never used inside `mobile/` or for any\n  mobile push**", skill)
        self.assertIn("Every command in `mobile/`\nuses the everyday token, never `$BUGFIX_GH_WORKFLOW_TOKEN`.", skill)

    def test_mobile_local_tests_are_marked_untried(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        row = [l for l in skill.splitlines() if l.startswith("| Tests, targeted only |")][0]
        self.assertIn("**untried under the sandbox:**", row)
        self.assertIn("is **not** a failed round", row)

    def test_a_fix_in_both_repositories_is_one_ticket_with_two_prs(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("**A fix in both repositories is one ticket with two PRs**", skill)
        self.assertIn("to\nDone only when both are merged", skill)
        self.assertIn("**two tickets with\n  org PRs open** at once (a ticket with a PR in each repository counts once)", skill)
        # Control: the per-PR wording is gone.
        self.assertNotIn("**two org PRs open**", skill)

    def test_workflow_files_are_orbweavers_only_and_guarded(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## Workflow files", 1)[1].split("\n## ", 1)[0]
        for needle in ("Never `release.yml` or\n  `publish-screenshots.yml`",
                       "an\n  environment, a secret, permissions, a trigger",
                       "`$BUGFIX_GH_WORKFLOW_TOKEN` (Orbweaver only)",
                       "**The proof is the PR's own CI run**",
                       "`Workflow change: <files>; no environment, secret, permission, trigger or runner line`",
                       "`Needs a protected\n  workflow line`", "`[bugfix-org] stopped: needs a protected workflow\n  line"):
            with self.subTest(needle):
                self.assertIn(needle, section)
        self.assertIn("| Workflow files | allowed, under **Workflow files** | never |", skill)
        self.assertIn("**Never on mobile:** edit `.github/workflows/`", skill)
        # Control: the blanket ban is gone from the skill's Never and the engineers.
        self.assertNotIn("a release. Edit `.github/workflows/`. Write", skill)
        for slug in ("hana-moriyama", "rafaela-quintero"):
            doc = read("agents", slug, "AGENTS.md")
            self.assertIn("except as the skill's\n**Workflow files** allows (never on mobile)", doc)
            self.assertNotIn("never edit a file\nunder `.github/workflows/`. You", doc)

    def test_only_the_engineers_hold_the_workflow_token(self):
        for slug in AGENTS:
            has = "BUGFIX_GH_WORKFLOW_TOKEN" in read("agents", slug, "AGENTS.md")
            self.assertEqual(has, slug in ("hana-moriyama", "rafaela-quintero"), slug)

    def test_lead_needs_qas_workflow_line_before_merging(self):
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("A PR that touches `.github/workflows/` has her `Workflow change:` line.", lead)
        self.assertIn("`Workflow change:` line on a workflow PR", lead)
        qa = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("`Workflow change: <files>; no environment, secret, permission, trigger or runner line`", qa)

    def test_putting_back_is_done_never_cancelled(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        section = skill.split("## Putting a ticket back", 1)[1].split("\n## ", 1)[0]
        self.assertIn("set the engineer's\nPaperclip issue to `done` with that same sentence as its `comment`, never\n`cancelled`", section)
        self.assertIn("`[bugfix-org] stopped: needs a protected workflow\n  line (<the line it needs", skill)

    def test_the_lead_asks_the_warden_to_merge_a_workflow_pr(self):
        # CB-486: the lead's token is told BLOCKED on a workflow PR, so it asks
        # through the spool. The request the skill spells out must be one the
        # warden actually parses, and a refused `gh pr merge` must not close it.
        import importlib.util
        spec = importlib.util.spec_from_file_location("warden", os.path.join(HERE, "..", "host", "warden.py"))
        warden = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(warden)
        skill = read("skills", "cb-bugfix", "SKILL.md")
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        form = '{"verb": "merge_workflow_pr", "pr": <PR number>, "sha": "<the full 40-hex head sha QA reviewed>"}'
        self.assertIn(f"`{form}`", skill)
        self.assertIn("`$BUGFIX_SPOOL/merge-<PR number>.json`", skill)
        self.assertIn('{"verb": "merge_workflow_pr", "pr": <number>, "sha": "<full head sha>"}', lead)
        filled = form.replace("<PR number>", "429").replace(
            "<the full 40-hex head sha QA reviewed>", "a00bc0cd3bebfff73fd6f157d7188d2ac011eedd")
        self.assertEqual(warden.parse_request(filled)[0]["verb"], "merge_workflow_pr")
        # Control: the placeholder itself, or a short sha, is not a request.
        self.assertIsNone(warden.parse_request(form.replace("<PR number>", "429"))[0])
        self.assertIsNone(warden.parse_request(filled.replace("a00bc0cd3bebfff73fd6f157d7188d2ac011eedd", "a00bc0cd3"))[0])
        flat = lambda t: " ".join(t.split())
        self.assertIn("never close a workflow PR over a refused `gh pr merge`", flat(lead))
        self.assertIn("Never close a workflow PR because `gh pr merge` refused it.", flat(skill))
        self.assertNotIn("BUGFIX_GH_WORKFLOW_TOKEN", lead, "the lead never holds the workflow token")

    def test_qa_tests_a_sha_in_a_second_worktree_and_never_detaches_its_own(self):
        # CB-492: QA's own `git checkout <sha>` left its Paperclip worktree on a
        # detached HEAD, and every later run there failed workspace validation.
        for text in (read("skills", "cb-bugfix", "SKILL.md"), read("agents", "tamsin-oyelaran", "AGENTS.md")):
            flat = " ".join(text.split())
            self.assertIn("Never move your own worktree off its branch.", flat)
            self.assertIn('git worktree add --detach "$TMPDIR/qa-<sha>" <sha>', flat)
            self.assertIn('git worktree remove --force "$TMPDIR/qa-<sha>"', flat)
            self.assertIn("workspace_validation_failed", flat)
        # Control: no instruction anywhere tells an agent to check out a bare sha or FETCH_HEAD.
        bare = re.compile(r"git checkout (-q )?(--detach )?(<sha>|FETCH_HEAD|[0-9a-f]{7,40})(?! --)")
        for slug in AGENTS:
            for m in bare.finditer(read("agents", slug, "AGENTS.md")):
                self.assertIn("leaves it on a detached HEAD", read("agents", slug, "AGENTS.md")[m.start():m.start() + 400], slug)

    def test_the_lead_settles_an_answered_blocked_child_the_warden_names(self):
        # CB-492: the warden wakes the parent; without this step the wake brings
        # the lead back to a task still blocked by that child.
        import importlib.util
        spec = importlib.util.spec_from_file_location("warden", os.path.join(HERE, "..", "host", "warden.py"))
        warden = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(warden)
        lead = " ".join(read("agents", "marisol-vandenberg", "AGENTS.md").split())
        self.assertIn("**A child that answered and is blocked.**", lead)
        self.assertIn("settle the blocked child with the same `PATCH /api/issues/<the child's id>`", lead)
        self.assertIn("only on a child the warden named", lead)
        self.assertIn('`{"status": "done", "comment": "Settled by the lead: it could not finish; see its answer."}`', lead)
        self.assertIn("a QA, review or capture child, and the ticket's engineer issue when the ticket is settled "
                      "(its PR merged or closed): `{\"status\": \"done\"", lead)
        self.assertIn("the ticket's engineer issue when you release that ticket: `{\"status\": \"cancelled\"}`", lead)
        self.assertIn("Only the engineer issue, and only on a release.", lead)
        self.assertIn("as your instructions say", warden.WAKE_PARENT_FOR_ANSWER)

    def test_a_mobile_rerun_waits_for_the_per_repo_warden(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn('`{"verb": "rerun_failed", "pr": <PR number>, "repo": "mobile"}`', skill)
        self.assertIn("only when\n`$BUGFIX_SPOOL/access.json` lists `\"mobile\": true` under `repos`", skill)

    def test_report_ends_with_counts_and_names_what_was_left(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        lines = ("Fixed: <n>", "Closed: <n>", "Waiting on triage: <n>", "Taken, in progress: <n>",
                 "Waiting on another team's PR: <n>", "Left untouched: <n>",
                 "Mobile access not live yet: <n>", "Needs a protected workflow line: <n>",
                 "Needs an input the org cannot make: <n>", "Open outside Refinement: <n>")
        block = re.search(r"```\n(Fixed: <n>.*?)```", skill, re.S)
        self.assertIsNotNone(block)
        got = [l.split(" (")[0] for l in block.group(1).strip().splitlines()]
        self.assertEqual(got, list(lines))
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("**End it with the counts block from the skill**", lead)
        self.assertIn("ending with the counts the skill asks for", read("tasks", "fix-cb-bugs", "TASK.md"))

    # CB-493: twelve hours of passes reported an empty board while four Bugs
    # sat unworked in Development, because the pass only queried Refinement.
    # Each rule below is one of the section's outcomes or guards, pinned in the
    # text the lead reads, each beside a control that would fail without it.
    def outside(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("\n## Bugs outside Refinement\n", skill)
        return skill.split("\n## Bugs outside Refinement\n", 1)[1].split("\n## ", 1)[0]

    def outcome(self, n):
        sec = self.outside()
        m = re.search(r"^%d\. (.*?)(?=^\d\. |^\*\*The guards)" % n, sec, re.S | re.M)
        self.assertIsNotNone(m, n)
        # Wrapped prose: compare on single spaces, so a reflow is not a miss.
        return " ".join(m.group(1).split())

    def test_every_pass_also_queries_development_and_testing(self):
        sec = self.outside()
        self.assertIn("project = CB AND issuetype = Bug AND status in (Development, Testing)", sec)
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("Every pass\nruns both.", skill)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("**Then settle the Bugs outside Refinement, before claiming anything.**", lead)
        self.assertIn("and the query in **Bugs outside Refinement** returned\n   nothing either, do you say so and stop", lead)
        self.assertIn("If neither query returns anything,\nsay so and stop.", read("tasks", "fix-cb-bugs", "TASK.md"))
        # Control: the old stop on an empty Refinement query alone is gone, and
        # the second query is not the take query (a non-Bug never enters it).
        self.assertNotIn("returns nothing at all do you say so and stop", lead)
        self.assertNotIn("If the query returns nothing, say so", read("tasks", "fix-cb-bugs", "TASK.md"))
        self.assertNotIn("issuetype in", sec)

    def test_settling_runs_before_any_claim_in_the_pass(self):
        sec = " ".join(self.outside().split())
        self.assertIn("This step runs before the pass claims anything (step 2), so a claim made in this pass is never read here.", sec)
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        # Control: in the lead's numbered steps the settle sits in step 0,
        # ahead of step 2's claim.
        self.assertLess(lead.index("**Then settle the Bugs outside Refinement"), lead.index("2. **Claim.**"))

    def test_outcomes_are_ordered_so_no_move_precedes_the_live_work_check(self):
        sec = self.outside()
        heads = re.findall(r"^\d\. \*\*([^*]+)\*\*", sec, re.M)
        self.assertEqual(heads, ["Settled today.", "The org is working it.", "Merged.",
                                 "Somebody else is on it.", "Another team's PR.", "The org's lost claim.",
                                 "Untouched for 72 hours.", "Moved recently."])
        moves = [i for i in range(1, 9) if re.search(r"Move it to Done|Put it back in Refinement", self.outcome(i))]
        self.assertEqual(moves, [3, 5, 6, 7])
        # Control: the no-write outcomes that protect live work come first, and
        # the activity check sits ahead of every move except merged.
        for n in (1, 2, 4):
            self.assertIn("No Jira write", self.outcome(n))
        self.assertTrue(all(m > 4 for m in moves if m != 3))

    def test_never_moved_twice_in_a_day(self):
        o = self.outcome(1)
        self.assertIn("`[bugfix-org] settled:`", o)
        self.assertIn("in the last 24 hours", o)
        self.assertIn("No ticket is moved twice in a day", o)
        # Control: every settle sentence carries the prefix this guard searches for.
        sec = self.outside()
        sentences = re.findall(r"`(\[bugfix-org\][^`]*)`", sec)
        moves = [x for x in sentences if "back to Refinement" in x or "merged to develop" in x]
        self.assertEqual(len(moves), 4, moves)
        for x in moves:
            self.assertTrue(x.startswith("[bugfix-org] settled: "), x)

    def test_a_live_task_or_open_org_pr_is_never_moved(self):
        o = self.outcome(2)
        for needle in ("`query` `Ticket: <key>`", '["backlog", "todo", "in_progress", "in_review", "blocked"]',
                       "first line is exactly `Ticket: <key>`", "open org PR", "in either repository",
                       "counted on `Taken, in progress`"):
            with self.subTest(needle):
                self.assertIn(needle, o)
        for needle in ("**and** that had a run or an update in the last 3 hours",
                       "A non-terminal task untouched for longer is left over from a stopped pass, not live",
                       "the ticket falls through to the outcomes below (outcome 6 settles that task)"):
            with self.subTest(needle):
                self.assertIn(needle, o)
        # Control: a recently updated task still protects (the bound is on
        # staleness, not on status), and done and cancelled tasks are not live,
        # so a lost claim whose engineer issue ended is still settled.
        self.assertIn("No Jira write; counted on `Taken, in progress`", o)
        self.assertNotIn('"done"', o)
        self.assertNotIn('"cancelled"', o)
        self.assertIn("Never move a ticket with a live task or an open org PR", self.outside())
        self.assertIn("`search_tasks` for `Ticket: <key>`", read("agents", "marisol-vandenberg", "AGENTS.md"))

    def test_a_merged_pr_moves_it_to_done_only_when_nothing_else_is_open(self):
        o = self.outcome(3)
        for needle in ("**title** names the key", "--state merged --base develop",
                       "`Uplift-Foundation/Orbweaver` and `Uplift-Foundation/Orbweaver-Mobile`",
                       "merged more than an hour ago", "no open PR's title names the key in either repository",
                       "Move it to Done (`41`)", "PR #<n> merged to develop at <merge sha>",
                       "the one move that recent activity does not stop"):
            with self.subTest(needle):
                self.assertIn(needle, o)

    def test_a_ticket_reopened_after_its_merge_is_never_closed_on_the_old_pr(self):
        o = self.outcome(3)
        for needle in ("nothing is newer than that merge but the org's own",
                       "no comment after `mergedAt` that does not start `[bugfix-org]`",
                       "`key = <key> AND status CHANGED AFTER -<m>m` returns nothing, `<m>` the minutes since `mergedAt`",
                       "the old PR never closes it again"):
            with self.subTest(needle):
                self.assertIn(needle, o)
        # Control: the move itself is still there, so the guard narrows the
        # merged outcome rather than removing it.
        self.assertIn("Move it to Done (`41`)", o)
        # Control: a ticket only mentioned in a commit or body is not "merged".
        self.assertNotIn('--search "<key>" ', o)
        self.assertNotIn("in:body", o)

    def test_anyone_elses_activity_in_72_hours_protects_the_ticket(self):
        o = self.outcome(4)
        for needle in ("Anyone other than the org commented on it or moved it in the last 72 hours",
                       "a comment in the last 72 hours that does not start `[bugfix-org]`",
                       "`key = <key> AND status CHANGED AFTER -<m>m` returns nothing",
                       "ten minutes after the org's newest `[bugfix-org]` comment",
                       "`key = <key> AND status CHANGED AFTER -72h` returns the ticket",
                       "No Jira write", "Outcomes 5 to 7 move a ticket only after this check"):
            with self.subTest(needle):
                self.assertIn(needle, o)
        # Control: the author is not the test, because the org posts under a
        # person's account; a rule keyed on the author would never protect.
        self.assertIn("the author cannot tell them apart", o)
        self.assertNotIn("author is", o)

    def test_another_teams_pr_holds_it_only_while_it_moves(self):
        o = self.outcome(5)
        for needle in ("in either repository", "same check as **Wait: another team's PR**",
                       "--json updatedAt,headRefOid", "moves on a push, a review or a comment",
                       "It moved in the last 72 hours: a wait. No Jira write",
                       "`Waiting on another team's PR`", "It has not: the PR no longer holds the ticket.",
                       "Put it back in Refinement (`11`)",
                       "has not moved for 72 hours (head <sha>)", "may build on that PR's commits",
                       "The take-rule's wait has the same limit"):
            with self.subTest(needle):
                self.assertIn(needle, o)
        # Control: the old "whatever its age" hold is gone.
        self.assertNotIn("An idle PR still holds the ticket", o)
        self.assertNotIn("whatever", o)

    def test_the_take_rules_wait_on_another_teams_pr_has_the_same_limit(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        rule = " ".join(skill.split("- **Wait: another team's PR.**", 1)[1].split("\n- **", 1)[0].split())
        for needle in ("**The wait lasts while that PR moves.**", "--json updatedAt,headRefOid",
                       "When it has not moved in 72 hours it no longer holds the ticket: take it.",
                       "branches its own `bugfix/pc-*` from `origin/develop`",
                       "cherry-pick or build on that PR's commits", "`gh pr comment <n> --repo <repository>"):
            with self.subTest(needle):
                self.assertIn(needle, rule)
        # Control: a PR named only in a body still is not a reason to wait.
        self.assertIn("A key named only in a PR body", rule)

    def test_a_lost_claim_is_done_twice_then_a_release(self):
        o = self.outcome(6)
        for needle in ("The newest comment on the ticket is this org's `[bugfix-org] claimed`",
                       "it is more than an hour old", "one more than the comments on the ticket containing `[bugfix-org] settled: lost claim`",
                       "`<n>` is 1 or 2", "`[bugfix-org] settled: lost claim <n>;",
                       '{"status": "done", "comment": "[bugfix-org] lost claim <n>"}',
                       "It is never set `cancelled` here.", "`<n>` is 3", "that is a release",
                       '{"status": "cancelled"}', "so the release brake counts it"):
            with self.subTest(needle):
                self.assertIn(needle, o)
        # Control: the put-back must not be a `stopped:` comment, which the
        # take-rule would hold as a wait forever (**Wait: put back**), and the
        # first two losses never cancel.
        self.assertNotIn("stopped:", o)
        first_two = o.split("`<n>` is 3", 1)[0]
        self.assertNotIn('"cancelled"', first_two)
        self.assertIn("never `[bugfix-org] stopped:`", self.outside())
        lead = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("`done` with `[bugfix-org] lost claim <n>` the first two times, `cancelled` the third", lead)
        self.assertIn("A third lost claim on the same ticket is a release (step 7).", lead)

    def test_untouched_means_no_status_change_and_no_update_for_72_hours(self):
        o = self.outcome(7)
        self.assertIn("`key = <key> AND NOT status CHANGED AFTER -72h AND updated <= -72h`", o)
        self.assertIn("Put it back in Refinement (`11`)", o)
        # Control: a ticket that only the org moved inside the window is left.
        r = self.outcome(8)
        self.assertIn("No Jira write", r)
        self.assertIn("in the last 72 hours", r)
        self.assertNotIn("CHANGED AFTER -24h", o)

    def test_a_person_who_just_moved_a_ticket_is_protected(self):
        guards = " ".join(self.outside().split("**The guards, and why each exists.**", 1)[1].split())
        self.assertIn("A person who moved a ticket to Development minutes ago, or commented on it, is protected by outcome 4 for 72 hours", guards)
        self.assertIn("refuse it unless it is `Bug`", guards)
        # Control: the 72-hour figure is the one outcome 4 actually queries.
        self.assertIn("-72h", self.outcome(4))

    def test_settles_have_their_own_cap_and_a_report_line(self):
        skill = read("skills", "cb-bugfix", "SKILL.md")
        limits = skill.split("## Limits", 1)[1].split("\n## ", 1)[0]
        self.assertIn("**five settles** in\n  **Bugs outside Refinement** (which use neither)", limits)
        self.assertIn("they have their own\ncap (**Limits**)", self.outside())
        self.assertIn("**The pass never reports an empty board\nwhile this query returns a ticket**", self.outside())
        self.assertIn("| settled | Development or Testing to Done (`41`) or Refinement (`11`)", skill)
        # Control: the Close rule still never closes a person's ticket; the
        # merged case is a settle, named as such.
        self.assertIn("Close only a\n   ticket in Refinement or one the org itself claimed", skill)
        self.assertIn("settled by **Bugs outside Refinement**, not closed", skill)

    # CB-469: a pinned task never leaves its account, so the warden closes a
    # child `done` and the lead re-creates it. The lead's instruction and the
    # warden's comment have to ask for the same thing.
    def test_the_lead_recreates_a_child_the_warden_closed_as_pinned(self):
        lead = " ".join(read("agents", "marisol-vandenberg", "AGENTS.md").split())
        for needle in ("**A child the warden closed as pinned to an exhausted account.**",
                       "Re-create it at once with `create_task`, with the same title and the description copied exactly",
                       "It is not a release and not a failed round",
                       "a release then sets that engineer issue `cancelled` as always"):
            with self.subTest(needle):
                self.assertIn(needle, lead)
        skill = " ".join(read("skills", "cb-bugfix", "SKILL.md").split())
        self.assertIn("the lead re-creates it with the same title and description (CB-469), which is not a failed round", skill)
        import importlib.util
        spec = importlib.util.spec_from_file_location("warden", os.path.join(HERE, "..", "host", "warden.py"))
        warden = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(warden)
        body = warden.PINNED_CHILD_CLOSED
        for needle in ("create_task", "same title", "same description", "not a release"):
            with self.subTest(needle):
                self.assertIn(needle, body)
        # Control: the warden's comment never asks for a cancel, which the
        # release brake would count.
        self.assertNotIn("cancel", body.replace("cannot", ""))

    def test_nobody_is_told_to_install_or_take_the_lock(self):
        for slug in AGENTS:
            doc = read("agents", slug, "AGENTS.md")
            self.assertNotIn("ow-install.lock", doc)
        skill = read("skills", "cb-bugfix", "SKILL.md")
        self.assertIn("Install on any machine yourself", skill)
        self.assertIn("never take `/tmp/ow-install.lock`", skill)
        for slug in ("hana-moriyama", "rafaela-quintero"):
            self.assertIn("never install anything on a machine yourself", read("agents", slug, "AGENTS.md"))


class RemoteRunnerInstructionTests(unittest.TestCase):
    """CB-494: builds, tests and coverage go to the MacBook Air through
    ow-remote; the mini runs none of them. Each phrase is checked present in
    the files that must carry it, and the old local instructions absent."""
    ENGINEERS = ("hana-moriyama", "rafaela-quintero")
    RUNS = ("build", "unit", "integration", "ui", "screenshots", "arrangement",
            "glyph", "transcript", "coverage")

    def skill(self):
        return read("skills", "cb-bugfix", "SKILL.md")

    def section(self):
        s = self.skill()
        start = s.index("## Building and testing: the remote runner")
        return s[start:s.index("\n## ", start + 1)]

    def test_skill_has_the_section_and_every_run_word(self):
        sec = self.section()
        self.assertIn("never on the mini", sec)
        self.assertIn("ow-remote <run> [filter]", sec)
        for run in self.RUNS:
            self.assertIn(f"`{run}`", sec, run)

    def test_the_run_words_match_the_helper(self):
        tool = read("host", "shims", "ow-remote")
        m = re.search(r"^RUNS = \(([^)]*)\)", tool, re.M)
        self.assertEqual(tuple(re.findall(r'"([a-z]+)"', m.group(1))), self.RUNS)

    def test_skill_says_how_to_ask_and_read_the_answer(self):
        sec = self.section()
        for phrase in ("commit, push, ask", "ow-remote wait <id>", "Exit `75`",
                       "Exit `64`", "Exit `65`", "`Total: 0`", "Never a refusal",
                       "refused, nothing ran", "remote-results/<id>/",
                       "result.json", "ow-remote status", "Quote the request id"):
            self.assertIn(phrase, sec, phrase)

    def test_the_exit_codes_match_the_helper(self):
        # A refusal must not share a code a suite returns for a failed test
        # (MTP: 2 failed, 8 none ran), and the skill must name the helper's.
        tool = read("host", "shims", "ow-remote")
        codes = {k: int(v) for k, v in re.findall(r"^(REFUSED|VOID|STILL_RUNNING|TIMED_OUT) = (\d+)$", tool, re.M)}
        self.assertEqual(set(codes), {"REFUSED", "VOID", "STILL_RUNNING", "TIMED_OUT"})
        self.assertFalse({codes["REFUSED"], codes["VOID"]} & {0, 1, 2, 8})
        for code in codes.values():
            self.assertIn(f"Exit `{code}`", self.section(), code)
        self.assertNotIn("Exit `2`, `refused", self.section())

    def test_result_files_and_busy_target_are_named(self):
        sec = self.section()
        for name in ("unit-tests.trx", "integration-tests.trx", "ui-tests.xunit.xml",
                     "ui-screenshots.xunit.xml", "coverage-summary.txt", "files/screenshots/",
                     "target_busy_with", "no single-method filter", "count\nguard",
                     "`tests/Tests.sln` in Release"):
            self.assertIn(name, sec, name)

    def test_integration_and_coverage_run_on_the_mini_until_the_air_has_tmux(self):
        sec = self.section()
        self.assertIn("**Integration and coverage run on the mini, until the Air has tmux.**", sec)
        self.assertIn("through `~/.paperclip/tools/bin/dotnet`", sec)
        self.assertIn("never two at once", sec)
        self.assertIn("say in\n`## Verified` that coverage was taken on the mini", sec)
        self.assertIn("only when N is 4", sec)
        self.assertIn('"the target has no tmux, which\n  IntegrationTests needs; not run", for `integration` or `coverage` only', sec)
        self.assertNotIn("Never run `./tools/coverage.sh` on the mini.", sec)
        self.assertIn("**Nothing else runs on the mini.**", sec)

    def test_control_any_other_refusal_still_means_wait(self):
        # The exception is for the tmux refusal of two runs only; a refused ui
        # run or an unreachable Air still means report it and wait.
        sec = self.section()
        self.assertIn("**Say so and wait**", sec)
        self.assertIn("**Never run the build or test on the mini\n  instead**", sec)
        self.assertIn("a refusal or an unreachable Air is never the exception below", sec)
        for row in [l for l in sec.splitlines() if l.startswith("| `ui`") or l.startswith("| `build`")
                    or l.startswith("| `unit`") or l.startswith("| `arrangement`")]:
            self.assertNotIn("on the mini", row, row)

    def test_a_local_dotnet_test_is_the_exception_and_says_why(self):
        self.assertIn("**Nothing else runs on the mini.** Any other local `dotnet test`", self.section())
        self.assertIn("which command ran locally and why", self.section())
        self.assertIn("a command run locally on the mini, and why", self.skill())

    def test_unreachable_air_means_say_so_and_wait_never_the_mini(self):
        sec = self.section()
        self.assertIn("not run on the mini", sec)
        self.assertIn("**Say so and wait**", sec)
        self.assertIn("**not** a failed round", sec)
        self.assertIn("**Never run the build or test on the mini\n  instead**", sec)
        self.assertNotIn("let CI be\n  the gate", sec)

    def test_the_old_build_lock_and_local_dotnet_test_are_gone(self):
        self.assertNotIn("The build lock is yours", self.skill())
        self.assertNotIn("`dotnet test` per class", self.skill())
        for slug in self.ENGINEERS:
            doc = read("agents", slug, "AGENTS.md")
            self.assertNotIn("BUGFIX_BUILD_LOCK", doc, slug)
            self.assertNotIn("quote `merged 4` from its first line", doc, slug)

    def test_engineers_build_test_and_cover_through_ow_remote(self):
        for slug in self.ENGINEERS:
            doc = read("agents", slug, "AGENTS.md")
            for phrase in ("never on the mini", "`ow-remote <run> <class>`",
                           "on the mini through\n   the `dotnet` wrapper, until the Air has tmux",
                           "merged 4 report(s)", "the exception, and the PR\n   says why"):
                self.assertIn(phrase, doc, (slug, phrase))

    def test_qa_runs_a_class_remotely_and_flags_a_local_run_without_a_reason(self):
        doc = read("agents", "tamsin-oyelaran", "AGENTS.md")
        self.assertIn("`ow-remote <run> <class>` from your second worktree", doc)
        self.assertIn("never\n  `dotnet test` on the mini", doc)
        self.assertIn("without saying why is a `fix` finding, except `tests/IntegrationTests`", doc)
        self.assertIn("without `merged 4`, or without saying it was taken on the mini", doc)

    def test_lead_never_builds_on_the_mini(self):
        doc = read("agents", "marisol-vandenberg", "AGENTS.md")
        self.assertIn("You never build or test on the mini yourself", doc)
        self.assertIn("through `ow-remote`", doc)



def find_bash():
    # Git Bash on Windows, not WSL's System32 bash; same lookup as
    # tools/test-require-team-sdlc.py.
    if os.name == "nt":
        for candidate in (r"C:\Program Files\Git\bin\bash.exe",
                          r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(candidate):
                return candidate
        return None
    return shutil.which("bash")


BASH = find_bash()
HOOK = os.path.join(HERE, "require-team-sdlc.sh")


def hook_decision(body, repo="Uplift-Foundation/Orbweaver"):
    """Run the real PreToolUse hook on a `gh pr create` carrying this body in a
    --body-file, and return 'deny' or 'allow'. The body goes in a file because
    that is the form the hook reads from disk."""
    fd, path = tempfile.mkstemp(suffix=".md")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(body)
        # Forward slashes: Git Bash cannot read a backslashed Windows path.
        cmd = ("gh pr create --repo " + repo + " --base develop --head h "
               "--title t --body-file " + path.replace("\\", "/"))
        payload = json.dumps({"tool_name": "Bash", "tool_input": {"command": cmd}})
        out = subprocess.run([BASH, HOOK], input=payload, capture_output=True,
                             text=True, timeout=60).stdout
        return "deny" if '"permissionDecision":"deny"' in out.replace(" ", "") else "allow"
    finally:
        os.unlink(path)


@unittest.skipIf(BASH is None or shutil.which("jq") is None,
                 "bash and jq are required (the hook fails open without jq)")
class PrBodyShapeTests(unittest.TestCase):
    def shape(self):
        m = re.search(r"```\n(CB-<number>.*?)```", read("skills", "cb-bugfix", "SKILL.md"), re.S)
        self.assertIsNotNone(m, "PR body template not found in the skill")
        return m.group(1).replace("CB-<number>", "CB-123")

    def test_template_in_the_skill_is_allowed_by_the_real_hook(self):
        self.assertEqual(hook_decision(self.shape()), "allow")

    def test_mobile_body_is_allowed_by_the_real_hook(self):
        body = re.sub(r"(?m)^Installed on:.*(\n.+)*", "Installed on: none (mobile repository)", self.shape())
        self.assertIn("Installed on: none (mobile repository)", body)
        self.assertEqual(hook_decision(body, "Uplift-Foundation/Orbweaver-Mobile"), "allow")
        # Control: the same body without the line is refused for mobile too.
        self.assertEqual(hook_decision(body.replace("Installed on:", "Where:"),
                                       "Uplift-Foundation/Orbweaver-Mobile"), "deny")

    def test_template_asks_for_machines_used_or_none_with_a_reason(self):
        self.assertRegex(self.shape(), r"(?m)^Installed on: <each of windows, mini, macbook")
        self.assertIn('or "none" and why', self.shape())
        skill = read("skills", "cb-bugfix", "SKILL.md")
        for verb in ("machine_install", "machine_run", "machine_release"):
            self.assertIn('{"verb": "%s"' % verb, skill)
        self.assertIn("at most 45 minutes", skill)

    def test_hook_denies_the_template_with_each_part_removed(self):
        # Negative controls against the same hook, so a hook that allowed
        # everything (or the jq-less fail-open path) cannot pass the test above.
        body = self.shape()
        for label, cut in [("key", ("CB-123", "")), ("verified", ("## Verified", "## V")),
                           ("not verified", ("## Not verified", "## N")),
                           ("installed on", ("Installed on:", "Where:"))]:
            with self.subTest(label):
                self.assertNotEqual(body.replace(*cut), body)
                self.assertEqual(hook_decision(body.replace(*cut)), "deny")


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


@unittest.skipUnless(shutil.which("node"), "needs node")
class PoolRouterTests(unittest.TestCase):
    """CB-468: the connection-pool router's own suite, run with node's test runner."""

    def test_the_router_suite_passes(self):
        p = subprocess.run(["node", "--test", os.path.join(HERE, "test-pool-router.mjs")],
                           capture_output=True, text=True, timeout=120)
        self.assertEqual(p.returncode, 0, p.stdout[-3000:] + p.stderr[-2000:])
        self.assertRegex(p.stdout, r"(?m)^\S* ?pass [1-9]")
        self.assertRegex(p.stdout, r"(?m)^\S* ?fail 0$")


PS_LSTART = os.path.join(HERE, "..", "paperclip", "triage-org", "host", "ps-lstart.c")
CAN_BUILD_PS = (os.uname().sysname == "Darwin" if hasattr(os, "uname") else False) \
    and shutil.which("clang") is not None and os.path.exists("/usr/bin/sandbox-exec")


@unittest.skipUnless(CAN_BUILD_PS, "macOS with clang and sandbox-exec")
class PsLstartTests(unittest.TestCase):
    """CB-467: the service's `ps -o lstart= -p <pid>` stand-in. Paperclip uses that
    answer to tell a reused pid from an old run's process; inside the sandbox the
    real /bin/ps cannot run, and a lead stayed in "pausing" for hours."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.ps = os.path.join(cls.tmp, "ps")
        subprocess.run(["clang", "-O2", "-Wall", "-Wextra", "-Werror", "-o", cls.ps, PS_LSTART], check=True)
        with open(PROFILE, encoding="utf-8") as f:
            text = f.read()
        home = os.path.expanduser("~")
        text = (text.replace("__HOME__", home).replace("__UID__", str(os.getuid()))
                .replace("__USER__", os.path.basename(home)))
        cls.profile = os.path.join(cls.tmp, "service.sb")
        with open(cls.profile, "w", encoding="utf-8") as f:
            f.write(text)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def run_(self, *argv):
        return subprocess.run(list(argv), capture_output=True, text=True)

    def test_it_answers_exactly_what_ps_answers(self):
        pid = str(os.getpid())
        want = self.run_("/bin/ps", "-o", "lstart=", "-p", pid).stdout.strip()
        got = self.run_(self.ps, "-o", "lstart=", "-p", pid)
        self.assertEqual(got.returncode, 0)
        self.assertTrue(want)
        self.assertEqual(got.stdout.strip(), want)

    def test_a_missing_pid_prints_nothing_and_fails_like_ps(self):
        for pid in ("99999999", "0", "-1", "12x", ""):
            with self.subTest(pid=pid):
                got = self.run_(self.ps, "-o", "lstart=", "-p", pid)
                self.assertEqual((got.returncode, got.stdout), (1, ""))

    def test_any_other_invocation_is_the_real_ps(self):
        got = self.run_(self.ps, "-p", str(os.getpid()), "-o", "pid=")
        self.assertEqual(got.stdout.strip(), str(os.getpid()))

    def test_inside_the_service_sandbox_only_the_stand_in_can_answer(self):
        pid = str(os.getpid())
        real = self.run_("/usr/bin/sandbox-exec", "-f", self.profile, "/bin/ps", "-o", "lstart=", "-p", pid)
        self.assertNotEqual(real.returncode, 0, "negative control: setuid ps is refused in a sandbox")
        self.assertEqual(real.stdout, "")
        got = self.run_("/usr/bin/sandbox-exec", "-f", self.profile, self.ps, "-o", "lstart=", "-p", pid)
        self.assertEqual(got.returncode, 0, got.stderr)
        self.assertEqual(got.stdout.strip(), self.run_("/bin/ps", "-o", "lstart=", "-p", pid).stdout.strip())


if __name__ == "__main__":
    unittest.main()
