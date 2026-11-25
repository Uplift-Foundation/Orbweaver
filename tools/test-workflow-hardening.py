#!/usr/bin/env python3
"""Guard for the GitHub Actions hardening in .github/workflows (CB-317..320).

    python3 tools/test-workflow-hardening.py

Two rules, both of which fail silently in review because a missing line looks
exactly like a line nobody needed:

  1. Every workflow declares `permissions:` at the top level or on every job.
     Without one the token takes the repository default, so widening that
     default widens every run that never said what it needed.
  2. Any job that references `secrets.MACOS_*` declares `environment: release`.
     The environment is what holds the approval gate and the tag/branch
     restriction; a job that maps the signing secrets without it is reachable
     from any branch that edits the workflow.

Stdlib only, like test-merge-coverage.py: PyYAML is not on the runners, and the
workflows here are plain block-style YAML, so the structure is read off
indentation. That is deliberately narrow. A flow-style `jobs: {...}` or a tab
indent is reported as unparseable rather than guessed at, so the guard cannot
pass a file it did not understand.

A third: no job holding `environment: release` may have `contents: write`,
since that job is the one holding the signing secrets.

A negative control runs first: a synthetic workflow that breaks both rules must
fail both checks, so a parser that goes blind turns this red instead of green.
"""
import glob
import os
import re
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
WORKFLOWS = os.path.join(_HERE, "..", ".github", "workflows")


def _meaningful(text):
    """(indent, content) for every non-blank, non-comment line."""
    out = []
    for raw in text.splitlines():
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        if "\t" in raw[: len(raw) - len(raw.lstrip())]:
            raise ValueError("tab indentation")
        out.append((len(raw) - len(raw.lstrip(" ")), raw.strip(), raw))
    return out


def _strip_comment(value):
    """Drop a trailing ` # comment`. Values compared here are plain words."""
    return re.sub(r"\s+#.*$", "", value)


def parse(text):
    """Return (top_level_keys, jobs) where jobs maps name -> {keys, raw}.

    `keys` holds the keys at the job's own indent, `raw` its full text, so a
    secrets reference anywhere inside the job (env, steps, with) is seen.
    """
    lines = _meaningful(text)
    top = {c.split(":", 1)[0] for i, c, _ in lines if i == 0 and ":" in c}
    if "jobs" not in top:
        raise ValueError("no top-level jobs:")
    jobs = {}
    in_jobs = False
    job_indent = None
    current = None
    for indent, content, raw in lines:
        if indent == 0:
            in_jobs = content.split(":", 1)[0] == "jobs"
            current = None
            if in_jobs and content != "jobs:":
                raise ValueError("flow-style jobs: is not supported")
            continue
        if not in_jobs:
            continue
        if job_indent is None:
            job_indent = indent
        if indent == job_indent and content.endswith(":"):
            current = jobs.setdefault(content[:-1], {"keys": {}, "raw": []})
            continue
        if current is None:
            raise ValueError("unexpected content under jobs:")
        current["raw"].append(raw)
        if indent == job_indent + 2 and ":" in content:
            k, v = content.split(":", 1)
            current["keys"][k] = _strip_comment(v).strip()
    if not jobs:
        raise ValueError("jobs: has no jobs")
    return top, jobs


def permission_problems(name, text):
    top, jobs = parse(text)
    if "permissions" in top:
        return []
    return [f"{name}: job '{j}' has no permissions: and the workflow has no "
            f"top-level permissions:"
            for j, d in jobs.items() if "permissions" not in d["keys"]]


def environment_problems(name, text, required="release"):
    _, jobs = parse(text)
    return [f"{name}: job '{j}' references secrets.MACOS_* without "
            f"environment: {required}"
            for j, d in jobs.items()
            if re.search(r"secrets\.MACOS_", "\n".join(d["raw"]))
            and d["keys"].get("environment") != required]


def write_problems(name, text, required="release"):
    top, jobs = parse(text)
    top_block = _top_permissions(text)
    out = []
    for j, d in jobs.items():
        if d["keys"].get("environment") != required:
            continue
        own = "\n".join(d["raw"])
        has_own = "permissions" in d["keys"]
        scope = _permissions_block(d["raw"]) if has_own else top_block
        if re.search(r"contents:\s*write|write-all", scope) or (
                has_own and re.search(r"permissions:\s*write-all", own)):
            out.append(f"{name}: job '{j}' holds environment: {required} "
                       f"and has contents: write")
    return out


def _permissions_block(raw_lines):
    """Text of a job's own permissions: block (the key line and its children)."""
    out, base = [], None
    for raw in raw_lines:
        ind = len(raw) - len(raw.lstrip(" "))
        if base is None:
            if raw.strip().startswith("permissions:") and ind >= 4:
                base = ind
                out.append(_strip_comment(raw))
            continue
        if ind <= base:
            break
        out.append(_strip_comment(raw))
    return "\n".join(out)


def _top_permissions(text):
    out, on = [], False
    for raw in text.splitlines():
        if raw.startswith("permissions:"):
            on = True
            out.append(_strip_comment(raw))
        elif on and raw.startswith(" ") and raw.strip():
            out.append(_strip_comment(raw))
        elif on and raw.strip() and not raw.startswith("#"):
            break
    return "\n".join(out)


def workflow_files():
    return sorted(glob.glob(os.path.join(WORKFLOWS, "*.yml")) +
                  glob.glob(os.path.join(WORKFLOWS, "*.yaml")))


BAD = """name: Bad
on: push
jobs:
  sign:
    runs-on: macos-latest
    env:
      MACOS_CERT: ${{ secrets.MACOS_CERTIFICATE }}
    steps:
      - run: echo hi
"""

GOOD_TOP = """name: Good
on: push
permissions:
  contents: read
jobs:
  sign:
    runs-on: macos-latest
    environment: release
    env:
      MACOS_CERT: ${{ secrets.MACOS_CERTIFICATE }}
    steps:
      - run: echo hi
"""

GOOD_PER_JOB = """name: Good
on: push
jobs:
  a:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - run: echo hi
"""


class NegativeControl(unittest.TestCase):
    def test_bad_workflow_fails_both_rules(self):
        self.assertEqual(len(permission_problems("bad.yml", BAD)), 1)
        self.assertEqual(len(environment_problems("bad.yml", BAD)), 1)

    def test_good_workflows_pass(self):
        self.assertEqual(permission_problems("g.yml", GOOD_TOP), [])
        self.assertEqual(environment_problems("g.yml", GOOD_TOP), [])
        self.assertEqual(permission_problems("g.yml", GOOD_PER_JOB), [])

    def test_wrong_environment_name_fails(self):
        wrong = GOOD_TOP.replace("environment: release", "environment: other")
        self.assertEqual(len(environment_problems("w.yml", wrong)), 1)

    def test_one_job_missing_permissions_is_named(self):
        text = GOOD_PER_JOB + "  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: x\n"
        problems = permission_problems("p.yml", text)
        self.assertEqual(len(problems), 1)
        self.assertIn("'b'", problems[0])

    def test_inline_comment_after_environment_passes(self):
        commented = GOOD_TOP.replace("environment: release",
                                     "environment: release   # why")
        self.assertEqual(environment_problems("c.yml", commented), [])

    def test_release_job_with_write_fails(self):
        top_write = GOOD_TOP.replace("contents: read", "contents: write")
        self.assertEqual(len(write_problems("w.yml", top_write)), 1)
        job_write = GOOD_TOP.replace("    environment: release\n",
            "    environment: release\n    permissions:\n      contents: write  # x\n")
        self.assertEqual(len(write_problems("w.yml", job_write)), 1)
        self.assertEqual(write_problems("w.yml", GOOD_TOP), [])

    def test_job_read_overrides_top_level_write(self):
        text = GOOD_TOP.replace("contents: read", "contents: write").replace(
            "    environment: release\n",
            "    environment: release\n    permissions:\n      contents: read\n")
        self.assertEqual(write_problems("o.yml", text), [])

    def test_unparseable_input_raises_instead_of_passing(self):
        for text in ("name: x\n", "jobs: {a: {}}\n", "jobs:\n"):
            with self.assertRaises(ValueError):
                parse(text)
        with self.assertRaises(ValueError):
            parse("jobs:\n\ta:\n")


class RealWorkflows(unittest.TestCase):
    def test_found_the_workflows(self):
        # An empty glob would make the checks below vacuously green.
        self.assertGreaterEqual(len(workflow_files()), 4)

    def test_every_workflow_declares_permissions(self):
        problems = []
        for f in workflow_files():
            with open(f, encoding="utf-8") as fh:
                problems += permission_problems(os.path.basename(f), fh.read())
        self.assertEqual(problems, [])

    def test_release_environment_jobs_cannot_write(self):
        problems = []
        for f in workflow_files():
            with open(f, encoding="utf-8") as fh:
                problems += write_problems(os.path.basename(f), fh.read())
        self.assertEqual(problems, [])

    def test_macos_secret_jobs_use_release_environment(self):
        problems, seen = [], 0
        for f in workflow_files():
            with open(f, encoding="utf-8") as fh:
                text = fh.read()
            seen += text.count("secrets.MACOS_")
            problems += environment_problems(os.path.basename(f), text)
        self.assertGreater(seen, 0, "no MACOS_ secrets found; is the check blind?")
        self.assertEqual(problems, [])


def inert_problems(name, text):
    """Rules for a workflow that must stay inert (stress.yml, CB-455).

    It runs code from a branch the bug-fix org wrote, so it may hold no secret,
    no write permission, only be started by hand, never persist the checkout
    credential, and never put a dispatch input inside a `run:` line (the input
    is typed by whoever dispatches; reaching it through env keeps it out of the
    shell's parser).
    """
    out = []
    if re.search(r"secrets\.", text):
        out.append(f"{name}: references secrets.*")
    if re.search(r"(?m)^\s*(contents|pull-requests|actions|checks|statuses|id-token|packages):\s*write", text) \
            or "write-all" in text:
        out.append(f"{name}: holds a write permission")
    triggers = re.search(r"(?m)^on:\n((?:[ ]+.*\n|\n)+)", text)
    keys = set(re.findall(r"(?m)^  (\w+):", triggers.group(1))) if triggers else set()
    if keys != {"workflow_dispatch"}:
        out.append(f"{name}: triggers are {sorted(keys)}, not only workflow_dispatch")
    for m in re.finditer(r"(?ms)^\s+- uses: actions/checkout@\S+\n(.*?)(?=^\s+- |\Z)", text):
        if "persist-credentials: false" not in m.group(1):
            out.append(f"{name}: checkout persists credentials")
    if not re.search(r"actions/checkout@", text):
        out.append(f"{name}: no checkout found; is the check blind?")
    in_run = False
    for raw in text.splitlines():
        stripped = raw.strip()
        if re.match(r"^(- )?run:", stripped):
            in_run = True
            if "${{" in stripped and "inputs." in stripped:
                out.append(f"{name}: dispatch input interpolated into run:")
            if stripped.rstrip() in ("run: |", "run: >", "- run: |"):
                continue
            in_run = False
            continue
        if in_run:
            if raw.startswith(" " * 10) or not stripped:
                if "inputs." in stripped and "${{" in stripped:
                    out.append(f"{name}: dispatch input interpolated into run:")
            else:
                in_run = False
    return out


INERT_GOOD = """name: S
on:
  workflow_dispatch:
    inputs:
      x:
        type: string
permissions:
  contents: read
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false
      - name: go
        env:
          X: ${{ inputs.x }}
        run: echo "$X"
"""


class InertWorkflow(unittest.TestCase):
    def test_good_control_passes(self):
        self.assertEqual(inert_problems("g.yml", INERT_GOOD), [])

    def test_each_violation_is_caught(self):
        cases = {
            "secret": INERT_GOOD.replace("X: ${{ inputs.x }}", "X: ${{ secrets.T }}"),
            "write": INERT_GOOD.replace("contents: read", "contents: write"),
            "pr trigger": INERT_GOOD.replace("on:\n  workflow_dispatch:", "on:\n  pull_request:\n  workflow_dispatch:"),
            "persist": INERT_GOOD.replace("          persist-credentials: false\n", ""),
            "inline input": INERT_GOOD.replace('run: echo "$X"', 'run: echo ${{ inputs.x }}'),
            "block input": INERT_GOOD.replace('run: echo "$X"', 'run: |\n          echo ${{ inputs.x }}'),
        }
        for label, text in cases.items():
            self.assertTrue(inert_problems("b.yml", text), label)

    def test_stress_yml_is_inert(self):
        path = os.path.join(WORKFLOWS, "stress.yml")
        self.assertTrue(os.path.exists(path), "stress.yml missing")
        with open(path, encoding="utf-8") as fh:
            self.assertEqual(inert_problems("stress.yml", fh.read()), [])


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
