#!/usr/bin/env python3
"""CB-367: tests for tools/check-test-budget.py. stdlib unittest, runs on both CI legs.

Each case writes real TRX-shaped files and runs the checker as a subprocess, the
way CI does, so the exit code and the ::error:: lines are what is asserted.
"""
import os
import subprocess
import sys
import tempfile
import unittest
from xml.sax.saxutils import quoteattr

HERE = os.path.dirname(os.path.abspath(__file__))
CHECK = os.path.join(HERE, "check-test-budget.py")

TRX = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
{rows}
</Results></TestRun>"""
ROW = '<UnitTestResult testName={name} duration="{dur}" outcome="Passed" />'


def dur(seconds):
    return f"{int(seconds // 3600):02d}:{int(seconds % 3600 // 60):02d}:{seconds % 60:010.7f}"


class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def trx(self, tests, name="r.trx"):
        path = os.path.join(self.tmp.name, name)
        with open(path, "w", encoding="utf-8") as f:
            f.write(TRX.format(rows="\n".join(ROW.format(name=quoteattr(n), dur=dur(s)) for n, s in tests)))
        return path

    def allow(self, text):
        path = os.path.join(self.tmp.name, "allow.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        return path

    def run_check(self, *args):
        p = subprocess.run([sys.executable, "-I", CHECK, *args], capture_output=True, text=True)
        return p.returncode, p.stdout + p.stderr

    def test_everything_within_budget_passes(self):
        code, out = self.run_check(self.trx([("A.Fast", 0.2), ("A.Edge", 30.0)]))
        self.assertEqual(code, 0, out)
        self.assertIn("0 over budget", out)

    def test_over_budget_test_fails_and_is_named(self):
        code, out = self.run_check(self.trx([("A.Fast", 0.2), ("A.WaitsOutATimeout", 120.1)]))
        self.assertEqual(code, 1, out)
        self.assertIn("::error::A.WaitsOutATimeout took 120.1s", out)
        self.assertNotIn("A.Fast took", out)

    def test_allow_listed_slow_test_passes_and_prints_its_reason(self):
        allow = self.allow("A.WaitsOutATimeout | waits out a 120s timeout; CB-999\n")
        code, out = self.run_check("--allowlist", allow, self.trx([("A.WaitsOutATimeout", 120.1)]))
        self.assertEqual(code, 0, out)
        self.assertIn("waits out a 120s timeout; CB-999", out)

    def test_allow_list_only_covers_the_named_test(self):
        allow = self.allow("A.Known | reason; CB-1\n")
        code, out = self.run_check("--allowlist", allow, self.trx([("A.Known", 90), ("A.Other", 90)]))
        self.assertEqual(code, 1, out)
        self.assertIn("::error::A.Other took", out)
        self.assertNotIn("::error::A.Known", out)

    def test_parameterised_test_matches_its_base_name(self):
        allow = self.allow("A.Theory | slow case; CB-1\n")
        code, out = self.run_check("--allowlist", allow, self.trx([('A.Theory(kind: "x")', 60)]))
        self.assertEqual(code, 0, out)

    def test_entry_without_a_reason_is_refused_not_honoured(self):
        for bad in ("A.Slow\n", "A.Slow |\n", "A.Slow |   \n", "| reason\n"):
            with self.subTest(bad=bad):
                code, out = self.run_check("--allowlist", self.allow(bad), self.trx([("A.Slow", 60)]))
                self.assertEqual(code, 2, out)
                self.assertIn("needs '<test name> | <reason>'", out)

    def test_stale_entry_warns_but_does_not_fail(self):
        allow = self.allow("A.NowFast | was slow; CB-1\n")
        code, out = self.run_check("--allowlist", allow, self.trx([("A.NowFast", 0.1)]))
        self.assertEqual(code, 0, out)
        self.assertIn("::warning::allow-list entry matched nothing", out)

    def test_budget_is_configurable(self):
        report = self.trx([("A.Four", 4.0)])
        self.assertEqual(self.run_check(report)[0], 0)
        self.assertEqual(self.run_check("--budget", "3", report)[0], 1)

    def test_directory_argument_collects_every_trx_in_it(self):
        self.trx([("A.Fast", 1)], "unit.trx")
        self.trx([("B.Slow", 99)], "ui.trx")
        code, out = self.run_check(self.tmp.name)
        self.assertEqual(code, 1, out)
        self.assertIn("2 results in 2 report(s)", out)
        self.assertIn("B.Slow", out)

    def test_unreadable_input_fails_rather_than_passing(self):
        empty = tempfile.mkdtemp(dir=self.tmp.name)
        self.assertEqual(self.run_check(empty)[0], 2)
        broken = os.path.join(self.tmp.name, "broken.trx")
        with open(broken, "w") as f:
            f.write("<TestRun")
        self.assertEqual(self.run_check(broken)[0], 2)
        self.assertEqual(self.run_check(os.path.join(self.tmp.name, "missing.trx"))[0], 2)

    def test_a_report_with_no_executed_results_fails(self):
        self.assertEqual(self.run_check(self.trx([]))[0], 2)

    def test_an_executed_result_without_a_duration_fails(self):
        path = os.path.join(self.tmp.name, "nodur.trx")
        with open(path, "w") as f:
            f.write(TRX.format(rows='<UnitTestResult testName="A.NoDur" outcome="Passed" />'))
        code, out = self.run_check(path)
        self.assertEqual(code, 2, out)
        self.assertIn("has no duration", out)

    def test_skipped_results_are_ignored(self):
        path = os.path.join(self.tmp.name, "skip.trx")
        with open(path, "w") as f:
            f.write(TRX.format(rows='<UnitTestResult testName="A.Skipped" outcome="NotExecuted" />\n'
                               + ROW.format(name='"A.Ran"', dur=dur(1))))
        self.assertEqual(self.run_check(path)[0], 0)

    def test_the_committed_allow_list_parses_and_every_entry_has_a_ticket(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("budget", CHECK)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        entries = mod.load_allowlist(os.path.join(HERE, "test-budget-allowlist.txt"))
        self.assertTrue(entries)
        for name, reason in entries.items():
            self.assertRegex(reason, r"CB-\d+", f"{name}: reason must name a ticket")


if __name__ == "__main__":
    unittest.main()
