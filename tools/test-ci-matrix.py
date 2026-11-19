#!/usr/bin/env python3
"""CB-375: every suite runs exactly once per rid in ci.yml's `suite` job.

ci.yml has one `suite` job; a matrix row names the suites an instance runs
(`checks: true`, `unit: true` ...) and each suite's steps carry
`if: ... matrix.<flag> ...`. A missing matrix key evaluates to null with no
error, so a typo on either side (`screenshot: true`, `matrix.packge`) skips a
suite while the job and the `build` gate stay green. This reads the file and
fails on that, so regrouping stays a one-line edit that cannot drop a suite.

stdlib only (no YAML library on the runners), reading the file's own shape:
matrix rows are single-line `- { key: value, ... }` flow mappings and steps
are `      - name:` / `      - uses:` blocks with their `if:` on the next line.
"""
import os
import re
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
CI = os.path.join(HERE, "..", ".github", "workflows", "ci.yml")
SUITES = {"checks", "unit", "ui", "screenshots", "package"}
RESERVED = {"os", "rid", "name"}
RIDS = {"osx-arm64", "win-x64"}
# What each suite's command must be gated on. A step under a real but wrong
# flag (UiTests under matrix.unit) would pass the structural checks above.
COMMANDS = {
    "dotnet run --project tests/ArrangementTests": "checks",
    "dotnet test tests/UnitTests": "unit",
    "dotnet test tests/IntegrationTests": "unit",
    "dotnet test tests/UiTests": "ui",
    "dotnet test tests/UiScreenshots": "screenshots",
    "dotnet publish Orbweaver.csproj": "package",
}


def suite_job(text):
    m = re.search(r"^  suite:\n(.*?)^  build:\n", text, re.S | re.M)
    if not m:
        raise ValueError("no `suite` job found in ci.yml")
    return m.group(1)


def matrix_rows(job):
    rows = []
    for m in re.finditer(r"^          - \{ (.*) \}\s*$", job, re.M):
        row = {}
        for pair in m.group(1).split(","):
            k, _, v = pair.strip().partition(":")
            row[k.strip()] = v.strip()
        rows.append(row)
    return rows


def suite_steps(job):
    """(step name, set of matrix.<x> flags in its if) for each step after setup."""
    out = []
    steps = re.split(r"^      - (?=(?:name|uses): )", job, flags=re.M)[1:]
    for s in steps:
        name = re.match(r"(?:name|uses): (.*)", s).group(1).strip()
        if name.startswith("actions/checkout") or name.startswith("actions/setup-"):
            continue
        if name == "Set up Python":
            continue
        im = re.search(r"^        if: (.*)$", s, re.M)
        flags = set(re.findall(r"matrix\.(\w+)", im.group(1))) - RESERVED if im else None
        out.append((name, flags, s))
    return out


def problems(text):
    job = suite_job(text)
    found = []
    rows = matrix_rows(job)
    for rid in sorted({r.get("rid") for r in rows} | RIDS):
        flags = [k for r in rows if r.get("rid") == rid for k, v in r.items()
                 if k not in RESERVED and v == "true"]
        for f in flags:
            if f not in SUITES:
                found.append(f"{rid}: matrix flag {f!r} is not one of {sorted(SUITES)}")
        for suite in sorted(SUITES):
            n = flags.count(suite)
            if n != 1:
                found.append(f"{rid}: suite {suite!r} runs {n} times, expected exactly 1")
    for name, flags, _ in suite_steps(job):
        if flags is None:
            found.append(f"step {name!r} has no `if:`")
            continue
        for f in flags - SUITES:
            found.append(f"step {name!r} tests matrix.{f}, which is not a suite flag")
        if not flags & SUITES:
            found.append(f"step {name!r} names no suite flag in its `if:`")
    # a real but wrong flag: each suite's command must sit under its own flag
    for name, flags, body in suite_steps(job):
        for needle, want in COMMANDS.items():
            if needle in body and flags and want not in flags:
                found.append(f"step {name!r} runs {needle!r} but is gated on {sorted(flags)}, not {want!r}")
    # every suite flag must be used by at least one step
    used = set().union(*[f for _, f, _ in suite_steps(job) if f])
    for suite in sorted(SUITES - used):
        found.append(f"no step is gated on matrix.{suite}")
    return found


class CiMatrixTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(CI, encoding="utf-8") as f:
            cls.text = f.read()

    def test_the_real_ci_yml_runs_every_suite_once_per_rid(self):
        self.assertEqual(problems(self.text), [])

    def test_a_misspelled_matrix_flag_is_caught(self):
        bad = self.text.replace("name: ui, ui: true }", "name: ui, ui_: true }", 1)
        self.assertNotEqual(bad, self.text)
        found = problems(bad)
        self.assertTrue(any("'ui_'" in p for p in found), found)
        self.assertTrue(any("suite 'ui' runs 0 times" in p for p in found), found)

    def test_a_misspelled_step_flag_is_caught(self):
        bad = self.text.replace("matrix.screenshots }}", "matrix.screenshot }}", 1)
        self.assertNotEqual(bad, self.text)
        self.assertTrue(any("matrix.screenshot, which" in p for p in problems(bad)), problems(bad))

    def test_a_rid_missing_a_suite_is_caught(self):
        bad = re.sub(r"^          - \{ os: windows-latest, rid: win-x64, name: package, package: true \}\n", "", self.text, flags=re.M)
        self.assertNotEqual(bad, self.text)
        self.assertIn("win-x64: suite 'package' runs 0 times, expected exactly 1", problems(bad))

    def test_a_suite_listed_twice_for_one_rid_is_caught(self):
        bad = self.text.replace("name: ui, ui: true }\n          # Windows", "name: ui, ui: true, unit: true }\n          # Windows", 1)
        self.assertNotEqual(bad, self.text)
        self.assertIn("osx-arm64: suite 'unit' runs 2 times, expected exactly 1", problems(bad))

    def test_a_suite_command_under_the_wrong_but_real_flag_is_caught(self):
        i = self.text.index("      - name: UI tests (xUnit, headless)\n        if: ${{ !cancelled() && matrix.ui }}")
        bad = self.text[:i] + self.text[i:].replace("matrix.ui }}", "matrix.unit }}", 1)
        self.assertNotEqual(bad, self.text)
        self.assertTrue(any("dotnet test tests/UiTests" in p and "not 'ui'" in p for p in problems(bad)), problems(bad))

    def test_a_step_with_no_if_is_caught(self):
        bad = self.text.replace("        if: ${{ !cancelled() && matrix.unit }}\n", "", 1)
        self.assertNotEqual(bad, self.text)
        self.assertTrue(any("has no `if:`" in p for p in problems(bad)), problems(bad))


if __name__ == "__main__":
    unittest.main()
