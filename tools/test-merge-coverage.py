#!/usr/bin/env python3
"""Tests for tools/merge-coverage.py's merge rules.

    python3 tools/test-merge-coverage.py

Plain stdlib `unittest`, no pytest and no virtualenv, because this is the only
Python in the repository that decides anything and adding a dependency to test
one script would cost more than the script. CI runs it as its own step for the
same reason every other suite is in CI: a merge rule that is wrong produces a
number, not a failure, and this repo has already spent hours three times on a
coverage figure that was fiction.

The seam under test is merge_secondary(), which is where coverlet's report is
made the authority over the Microsoft.CodeCoverage ones. Everything above it —
finding reports, parsing cobertura, resolving paths — is glue this deliberately
does not re-test; the arithmetic is what has been wrong.
"""
import importlib.util
import os
import sys
import unittest
from collections import defaultdict

_HERE = os.path.dirname(os.path.abspath(__file__))
_SPEC = importlib.util.spec_from_file_location(
    "merge_coverage", os.path.join(_HERE, "merge-coverage.py"))
merge_coverage = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(merge_coverage)


def merged(coverlet_lines, coverlet_branches, mtp_lines, mtp_branches, keep):
    """Run the merge over plain dicts and hand back what it produced."""
    lines = defaultdict(dict, {p: dict(v) for p, v in coverlet_lines.items()})
    branches = defaultdict(dict, {p: dict(v) for p, v in coverlet_branches.items()})
    merge_coverage.merge_secondary(
        lines, branches,
        defaultdict(dict, mtp_lines), defaultdict(dict, mtp_branches),
        keep)
    return lines, branches


class PartialBranchReport(unittest.TestCase):
    """--base used to print only lines that never ran, so an added line that ran
    with some of its arms untaken (lines 100%, branches under 100%) could not be
    located. PARTIAL BRANCHES is that list."""

    def test_a_partly_taken_point_is_listed_and_a_fully_taken_one_is_not(self):
        branches = {"A.cs": {10: (1, 2), 11: (2, 2), 12: (0, 4)}}

        report = merge_coverage.partial_branch_report("A.cs", [10, 11, 12], branches)

        self.assertEqual("  PARTIAL BRANCHES A.cs: [10(1/2), 12(0/4)]", report)
        self.assertNotIn("11(", report)          # the control: fully taken

    def test_only_the_added_lines_it_is_given_are_considered(self):
        branches = {"A.cs": {10: (1, 2), 99: (0, 2)}}

        self.assertEqual(
            "  PARTIAL BRANCHES A.cs: [10(1/2)]",
            merge_coverage.partial_branch_report("A.cs", [10], branches))

    def test_nothing_partial_prints_nothing(self):
        self.assertIsNone(merge_coverage.partial_branch_report(
            "A.cs", [10, 11], {"A.cs": {10: (2, 2)}}))      # 11 has no branch point

    def test_a_file_with_no_branch_data_prints_nothing(self):
        self.assertIsNone(merge_coverage.partial_branch_report("B.cs", [1, 2], {}))


class BranchAuthority(unittest.TestCase):
    """coverlet decides which branch points exist; MTP only fills in hits."""

    def test_mtp_cannot_invent_a_branch_point(self):
        # The real shape of CB-100: OpenClawSessions.cs 1628/1647 are lines
        # coverlet instrumented, hit, and recorded no branch on, while the ui
        # and shots reports — which never executed that parser — claim 0/6 and
        # 0/2 there. Eight arcs the code does not have, none of them takeable.
        keep = {"OpenClawSessions.cs": {1628, 1647}}
        _, branches = merged(
            {"OpenClawSessions.cs": {1628: True, 1647: True}},
            {},
            {"OpenClawSessions.cs": {1628: False, 1647: False}},
            {"OpenClawSessions.cs": {1628: (0, 6), 1647: (0, 2)}},
            keep)
        self.assertEqual({}, dict(branches["OpenClawSessions.cs"]))

    def test_mtp_still_contributes_hits_to_a_real_branch_point(self):
        # The whole reason the MTP reports are merged at all: a branch arm only
        # a UI test takes must still count.
        keep = {"OrbWindow.axaml.cs": {10}}
        _, branches = merged(
            {"OrbWindow.axaml.cs": {10: True}},
            {"OrbWindow.axaml.cs": {10: (1, 2)}},
            {"OrbWindow.axaml.cs": {10: True}},
            {"OrbWindow.axaml.cs": {10: (2, 2)}},
            keep)
        self.assertEqual({10: (2, 2)}, dict(branches["OrbWindow.axaml.cs"]))

    def _one(self, coverlet, mtp):
        keep = {"F.cs": {42}}
        _, branches = merged(
            {"F.cs": {42: True}}, {"F.cs": {42: coverlet}},
            {"F.cs": {42: True}}, {"F.cs": {42: mtp}}, keep)
        return branches["F.cs"][42]

    # CB-244. coverlet decides how many arcs a branch point has. The real shape:
    # ChatMarkdown.cs:179 was 6/6 in coverlet's unit run and 10/12 in the MTP
    # runs, and merged to 10/12, a denominator for arcs coverlet says the line
    # does not have (reports and sources both at 95e84ea5). The test below uses
    # the ticket's shape, 6/6 against 2/8, as a synthetic pair. Fails on the max-of-both-halves rule this replaced.
    def test_a_wider_mtp_total_does_not_widen_a_branch_point_coverlet_knows(self):
        self.assertEqual((6, 6), self._one((6, 6), (2, 8)))

    def test_widened_only_by_a_partial_reading_the_line_is_not_called_covered(self):
        # The worry the old max() answered, answered the other way now: a bare cap
        # would turn coverlet 0/2 + MTP 2/4 into 2/2 -- "fully covered" when
        # neither engine covered it fully. MTP's partial count cannot be paired
        # with coverlet's arcs, so it adds nothing.
        self.assertEqual((0, 2), self._one((0, 2), (2, 4)))

    def test_a_wider_mtp_that_covered_everything_it_counts_covers_the_point(self):
        # Every arc MTP counted was taken, so every arc coverlet counts was too,
        # capped at coverlet's own total. The control that MTP hits still count.
        self.assertEqual((2, 2), self._one((0, 2), (4, 4)))
        self.assertEqual((2, 2), self._one((1, 2), (10, 10)))

    def test_the_same_total_takes_the_larger_taken_count(self):
        self.assertEqual((2, 2), self._one((1, 2), (2, 2)))
        self.assertEqual((2, 2), self._one((2, 2), (1, 2)))

    def test_a_narrower_mtp_total_adds_nothing_and_never_shrinks_the_point(self):
        # Fewer arcs than coverlet counts: which of coverlet's arcs those were is
        # unknowable, even when MTP took all of its own.
        self.assertEqual((0, 4), self._one((0, 4), (2, 2)))
        self.assertEqual((3, 4), self._one((3, 4), (1, 2)))

    def test_an_mtp_only_branch_point_is_still_dropped_where_coverlet_is_the_authority(self):
        keep = {"F.cs": {42}}
        _, branches = merged(
            {"F.cs": {42: True}}, {},
            {"F.cs": {42: True}}, {"F.cs": {42: (2, 2)}}, keep)
        self.assertEqual({}, dict(branches["F.cs"]))

    def test_file_coverlet_never_reported_keeps_its_mtp_branches(self):
        # No authority to defer to, so the only engine that saw the file is
        # believed — the same per-file reasoning `keep` already uses for lines.
        keep = {"SessionManager.cs": {42}}
        _, branches = merged(
            {"SessionManager.cs": {42: True}},
            {"SessionManager.cs": {42: (2, 2)}},
            {"OnlyMtpSawThis.cs": {7: True}},
            {"OnlyMtpSawThis.cs": {7: (1, 2)}},
            keep)
        self.assertEqual({7: (1, 2)}, dict(branches["OnlyMtpSawThis.cs"]))

    def test_no_authority_at_all_keeps_everything(self):
        # keep is None when there was no coverlet report to be the authority.
        # Nothing may be dropped in that case.
        _, branches = merged(
            {}, {},
            {"Whatever.cs": {3: True}},
            {"Whatever.cs": {3: (1, 2)}},
            None)
        self.assertEqual({3: (1, 2)}, dict(branches["Whatever.cs"]))


class LineUnion(unittest.TestCase):
    """Lines stay a union of hits over whatever survived load()'s `keep`."""

    def test_mtp_hit_covers_a_line_coverlet_missed(self):
        lines, _ = merged(
            {"ClaudeDesktopBundles.cs": {90: False}}, {},
            {"ClaudeDesktopBundles.cs": {90: True}}, {},
            {"ClaudeDesktopBundles.cs": {90}})
        self.assertEqual({90: True}, dict(lines["ClaudeDesktopBundles.cs"]))

    def test_mtp_miss_does_not_uncover_a_line_coverlet_hit(self):
        lines, _ = merged(
            {"ClaudeDesktopBundles.cs": {90: True}}, {},
            {"ClaudeDesktopBundles.cs": {90: False}}, {},
            {"ClaudeDesktopBundles.cs": {90}})
        self.assertEqual({90: True}, dict(lines["ClaudeDesktopBundles.cs"]))


class ReportCount(unittest.TestCase):
    """CB-229: never print a figure from other than the expected four reports."""

    def test_four_reports_are_accepted(self):
        self.assertIsNone(merge_coverage.refuse_unless_expected(list("abcd"), 4))

    def test_two_reports_are_refused_with_a_reason(self):
        msg = merge_coverage.refuse_unless_expected(["u.xml", "s.xml"], 4)
        self.assertIn("REFUSING", msg)
        self.assertIn("merged 2", msg)
        self.assertIn("u.xml", msg)

    def test_six_reports_are_refused_too(self):
        self.assertIsNotNone(merge_coverage.refuse_unless_expected(list("abcdef"), 4))

    def test_no_reports_are_refused(self):
        self.assertIn("none", merge_coverage.refuse_unless_expected([], 4))

    def test_default_expectation_is_four(self):
        self.assertEqual(4, merge_coverage.EXPECTED_REPORTS)

    def test_expect_flag_and_dedup_in_parse_args(self):
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            f = os.path.join(d, "a.xml")
            open(f, "w").close()
            base, reports, expect = merge_coverage.parse_args(
                [f, os.path.join(d, "*.xml"), "--base", "x", "--expect", "1"])
            self.assertEqual(("x", 1, [f]), (base, expect, reports))

    def test_main_exits_nonzero_with_wrong_count(self):
        import sys
        old = sys.argv
        sys.argv = ["merge-coverage.py", "/definitely/not/there.xml"]
        try:
            with self.assertRaises(SystemExit) as cm:
                merge_coverage.main()
        finally:
            sys.argv = old
        self.assertIn("REFUSING", str(cm.exception.code))


class MsysPaths(unittest.TestCase):
    """A native Windows Python cannot glob an MSYS /c/... path."""

    def test_drive_path_becomes_native_on_windows(self):
        self.assertEqual("C:/Users/x/*.xml",
                         merge_coverage.native_path("/c/Users/x/*.xml", windows=True))

    def test_bare_drive(self):
        self.assertEqual("D:/", merge_coverage.native_path("/d", windows=True))

    def test_native_and_other_paths_untouched_on_windows(self):
        for p in ("C:/a/b", "rel/x", "/tmp/x", "/cc/x"):
            self.assertEqual(p, merge_coverage.native_path(p, windows=True))

    def test_posix_is_identity(self):
        self.assertEqual("/c/foo", merge_coverage.native_path("/c/foo", windows=False))


class EmptyReportArrayUnderSetU(unittest.TestCase):
    """coverage.sh expands its report array under `set -u`; bash 3.2 (macOS)
    aborts on an empty one before merge-coverage.py can explain itself."""

    IDIOM = 'python3 tools/merge-coverage.py ${REPORTS[@]+"${REPORTS[@]}"} "$@"'

    def test_coverage_sh_uses_the_guarded_expansion(self):
        src = open(os.path.join(os.path.dirname(__file__), "coverage.sh")).read()
        self.assertIn(self.IDIOM, src)

    def test_zero_reports_reach_the_refusal_not_an_unbound_variable(self):
        import shutil, subprocess
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("no bash")
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        r = subprocess.run(
            [bash, "-c", "set -euo pipefail; REPORTS=(); " + self.IDIOM.replace(
                "python3", sys.executable.replace("\\", "/"))],
            cwd=root, capture_output=True, text=True)
        self.assertNotEqual(0, r.returncode)
        self.assertIn("REFUSING", r.stderr)
        self.assertNotIn("unbound variable", r.stderr)


class MissingReportNote(unittest.TestCase):
    """coverage.sh's note_missing_report: red AND no report says so on the RED line."""

    def _run(self, make_report):
        import re, shutil, subprocess, tempfile
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("no bash")
        src = open(os.path.join(os.path.dirname(__file__), "coverage.sh")).read()
        fn = re.search(r"^note_missing_report\(\) \{.*?^\}", src, re.S | re.M).group(0)
        with tempfile.TemporaryDirectory() as out:
            os.makedirs(os.path.join(out, "unit", "guid"))
            if make_report:
                open(os.path.join(out, "unit", "guid", "coverage.cobertura.xml"), "w").close()
            r = subprocess.run(
                [bash, "-c", f'set -euo pipefail; OUT="{out.replace(chr(92), "/")}"; RED=(); {fn}; '
                             'note_missing_report unit tests/UnitTests; '
                             'echo "RED=${RED[*]:-}"'],
                capture_output=True, text=True)
        return r

    def test_no_report_is_named_on_the_red_line(self):
        r = self._run(make_report=False)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("RED=tests/UnitTests (no report)", r.stdout)
        self.assertIn("produced no cobertura report", r.stderr)

    def test_a_report_leaves_the_red_line_alone(self):
        r = self._run(make_report=True)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("RED=\n", r.stdout + "\n")
        self.assertNotIn("no report", r.stdout)

    def test_coverage_sh_checks_both_vstest_suites(self):
        src = open(os.path.join(os.path.dirname(__file__), "coverage.sh")).read()
        self.assertIn("note_missing_report unit tests/UnitTests", src)
        self.assertIn("note_missing_report integration tests/IntegrationTests", src)


class SourceStamp(unittest.TestCase):
    """CB-244: reports measured in one checkout must not be merged against another's."""

    def setUp(self):
        import subprocess, tempfile
        self.tmp = tempfile.mkdtemp(prefix="cb-stamp-")
        self.repo = os.path.join(self.tmp, "repo")
        os.makedirs(self.repo)
        env = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@t",
                   GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@t")
        self._env = env

        def git(*a):
            subprocess.run(["git", "-C", self.repo, *a], check=True, capture_output=True, env=env)
        self.git = git
        git("init", "-q")
        with open(os.path.join(self.repo, "a.cs"), "w") as f:
            f.write("class A {}\n")
        git("add", "-A")
        git("commit", "-q", "-m", "one")

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _stamp_file(self, content, nested=True):
        out = os.path.join(self.tmp, "out")
        report_dir = os.path.join(out, "unit", "guid") if nested else out
        os.makedirs(report_dir, exist_ok=True)
        with open(os.path.join(out, merge_coverage.STAMP_NAME), "w") as f:
            f.write(content)
        report = os.path.join(report_dir, "coverage.cobertura.xml")
        open(report, "w").close()
        return report

    def test_the_stamp_changes_with_the_commit_with_edits_and_with_new_files(self):
        first = merge_coverage.source_stamp(self.repo)
        self.assertEqual(first, merge_coverage.source_stamp(self.repo))
        with open(os.path.join(self.repo, "a.cs"), "w") as f:
            f.write("class A { int x; }\n")
        edited = merge_coverage.source_stamp(self.repo)
        self.assertNotEqual(first, edited)
        with open(os.path.join(self.repo, "new.cs"), "w") as f:
            f.write("class B {}\n")
        self.assertNotEqual(edited, merge_coverage.source_stamp(self.repo))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "two")
        self.assertNotEqual(first.split()[0], merge_coverage.source_stamp(self.repo).split()[0])

    def test_reports_from_the_same_checkout_are_accepted(self):
        report = self._stamp_file(merge_coverage.source_stamp(self.repo))
        self.assertIsNone(merge_coverage.source_mismatch([report], self.repo))

    def test_reports_measured_at_another_sha_are_refused_with_both_shas(self):
        measured = merge_coverage.source_stamp(self.repo)
        report = self._stamp_file(measured)
        with open(os.path.join(self.repo, "a.cs"), "w") as f:
            f.write("class A { int changed; }\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "two")
        msg = merge_coverage.source_mismatch([report], self.repo)
        self.assertIn("REFUSING", msg)
        self.assertIn(measured.split()[0], msg)
        self.assertIn(merge_coverage.source_stamp(self.repo).split()[0], msg)
        self.assertIn("--allow-source-mismatch", msg)

    def test_a_stamp_with_no_digest_line_refuses_cleanly_instead_of_crashing(self):
        # A one-line stamp (an old or hand-made one; coverage.sh itself can no longer write
        # one) differs from the current stamp, so it must refuse, with the message.
        head = merge_coverage.source_stamp(self.repo).split()[0]
        report = self._stamp_file(head + "\n")
        msg = merge_coverage.source_mismatch([report], self.repo)
        self.assertIn("REFUSING", msg)
        self.assertIn("(missing)", msg)

    def test_an_edit_made_after_the_stamp_is_caught_too(self):
        # The case that bit this ticket's own measurements: same sha, tree edited while the
        # suites ran.
        report = self._stamp_file(merge_coverage.source_stamp(self.repo))
        with open(os.path.join(self.repo, "a.cs"), "w") as f:
            f.write("class A { int edited; }\n")
        self.assertIsNotNone(merge_coverage.source_mismatch([report], self.repo))

    def test_reports_with_no_stamp_are_not_second_guessed(self):
        report = os.path.join(self.tmp, "loose.cobertura.xml")
        open(report, "w").close()
        self.assertIsNone(merge_coverage.source_mismatch([report], self.repo))

    def test_main_refuses_and_the_override_goes_through_to_the_next_check(self):
        import io, contextlib
        report = self._stamp_file("0" * 40 + "\n" + "0" * 16 + "\n")
        old_argv, old_cwd = sys.argv, os.getcwd()
        os.chdir(self.repo)
        try:
            sys.argv = ["merge-coverage.py", report]            # one report, stamp wrong
            with self.assertRaises(SystemExit) as refused:
                merge_coverage.main()
            self.assertIn("expected exactly 4", str(refused.exception.code))   # count check comes first

            reports = []
            for i in range(4):
                d = os.path.join(self.tmp, "out", f"r{i}")
                os.makedirs(d, exist_ok=True)
                reports.append(os.path.join(d, "coverage.cobertura.xml"))
                open(reports[-1], "w").close()
            sys.argv = ["merge-coverage.py", *reports]
            with self.assertRaises(SystemExit) as refused:
                merge_coverage.main()
            self.assertIn("different checkout", str(refused.exception.code))
            self.assertIn("REFUSING", str(refused.exception.code))

            sys.argv = ["merge-coverage.py", *reports, "--allow-source-mismatch"]
            try:
                merge_coverage.main()
            except SystemExit as e:                              # empty reports fail later, not on the stamp
                self.assertNotIn("different checkout", str(e.code))
            except Exception:
                pass
        finally:
            sys.argv = old_argv
            os.chdir(old_cwd)

    def test_the_shell_stamp_coverage_sh_writes_is_the_one_python_computes(self):
        import re, shutil, subprocess
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("no bash")
        with open(os.path.join(os.path.dirname(__file__), "coverage.sh")) as f:
            src = f.read()
        block = re.search(r"^# --- source-stamp begin\n(.*?)^# --- source-stamp end$", src, re.M | re.S).group(1)
        # The hash tool is chosen by coverage.sh's own line, run by the same bash that runs
        # the stamp. Looking for it on Python's PATH instead picked a `shasum` bash could not
        # see on a Windows runner, and the digest line came out empty.
        pick = re.search(r"^if command -v shasum .*; fi$", src, re.M).group(0)
        os.makedirs(os.path.join(self.tmp, "o"))
        with open(os.path.join(self.repo, "a.cs"), "w") as f:       # a dirty tree and an untracked file
            f.write("class A { int dirty; }\n")
        with open(os.path.join(self.repo, "u.cs"), "w") as f:
            f.write("class U {}\n")
        out_dir = os.path.join(self.tmp, "o").replace("\\", "/")
        repo = self.repo.replace("\\", "/")
        subprocess.run([bash, "-c", f'set -euo pipefail; cd "{repo}"; OUT="{out_dir}"; {pick}\n{block}'],
                       check=True, capture_output=True)
        with open(os.path.join(self.tmp, "o", "source-stamp")) as f:
            self.assertEqual(merge_coverage.source_stamp(self.repo), f.read())

    def test_the_shell_stamp_fails_loudly_when_the_digest_comes_out_empty(self):
        # The case pipefail cannot catch: a hash tool that exists, succeeds, and prints
        # nothing. A one-line stamp would silently disagree with merge-coverage.py's and
        # refuse a legitimate run, so the script must stop instead.
        import re, shutil, subprocess
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("no bash")
        with open(os.path.join(os.path.dirname(__file__), "coverage.sh")) as f:
            src = f.read()
        block = re.search(r"^# --- source-stamp begin\n(.*?)^# --- source-stamp end$", src, re.M | re.S).group(1)
        os.makedirs(os.path.join(self.tmp, "o"))
        out_dir = os.path.join(self.tmp, "o").replace("\\", "/")
        repo = self.repo.replace("\\", "/")
        r = subprocess.run(
            [bash, "-c", f'set -euo pipefail; cd "{repo}"; OUT="{out_dir}"; '
                         f'silent_hash() {{ cat >/dev/null; }}; HASHER=silent_hash\n{block}'],
            capture_output=True, text=True)
        self.assertNotEqual(0, r.returncode)
        self.assertIn("could not compute the source stamp", r.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "o", "source-stamp")))

    def test_coverage_sh_stamps_before_any_suite_runs(self):
        src = open(os.path.join(os.path.dirname(__file__), "coverage.sh")).read()
        self.assertLess(src.index('> "$OUT/source-stamp"'), src.index('echo "==> tests/UnitTests"'))


class AddedLinesWorkingTree(unittest.TestCase):
    """CB-251: --base measures the working tree, not just commits.

    Every case has a control that would fail if the fixture were wrong. Each repo
    ignores the developer's and the runner's git config (windows-latest sets
    autocrlf=true system-wide), and files are written as bytes so Windows adds no CRs.
    """

    def setUp(self):
        import subprocess, tempfile
        from unittest import mock
        self.tmp = tempfile.mkdtemp(prefix="cb251-")
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)
        patcher = mock.patch.dict(os.environ, {
            "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
            "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.repo = os.path.join(self.tmp, "repo")
        os.makedirs(self.repo)
        self.g("init", "-q")
        self.g("symbolic-ref", "HEAD", "refs/heads/main")
        self.g("config", "core.autocrlf", "false")
        self.w("A.cs", "a1\na2\na3\n")
        self.g("add", "-A")
        self.g("commit", "-q", "-m", "one")
        self.g("branch", "base")

    def g(self, *a, check=True):
        import subprocess
        return subprocess.run(["git", "-C", self.repo, *a], check=check, capture_output=True).stdout.decode()

    def w(self, name, text):
        path = os.path.join(self.repo, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(text if isinstance(text, bytes) else text.encode("utf-8"))

    def commit_all(self):
        self.g("add", "-A")
        self.g("commit", "-q", "-m", "more")

    def measured(self):
        return merge_coverage.added_lines("base", self.repo)

    def as_dict(self, result):
        return {p: set(n) for p, n in result.lines.items() if n}

    # 1
    def test_an_unstaged_edit_is_measured_and_labelled_uncommitted(self):
        self.w("A.cs", "a1\nNEW\na2\na3\n")
        got = self.measured()
        self.assertEqual({"A.cs": {2}}, self.as_dict(got))
        self.assertEqual(["A.cs"], got.uncommitted)
        self.commit_all()                                   # control: committed, same lines, no label
        again = self.measured()
        self.assertEqual({"A.cs": {2}}, self.as_dict(again))
        self.assertEqual([], again.uncommitted)

    # 2
    def test_a_staged_edit_is_measured(self):
        self.w("A.cs", "a1\na2\na3\nNEW\n")
        self.g("add", "A.cs")
        staged = self.as_dict(self.measured())
        self.assertEqual({"A.cs": {4}}, staged)
        self.commit_all()
        self.assertEqual(staged, self.as_dict(self.measured()))

    # 3
    def test_staged_and_unstaged_edits_to_one_file_combine(self):
        self.w("A.cs", "a1\na2\na3\nS\n")
        self.g("add", "A.cs")
        self.w("A.cs", "U\na1\na2\na3\nS\n")
        combined = self.as_dict(self.measured())
        self.assertEqual({"A.cs": {1, 5}}, combined)
        self.commit_all()
        self.assertEqual(combined, self.as_dict(self.measured()))

    # 4
    def test_an_untracked_file_counts_wholly_with_or_without_a_final_newline(self):
        self.w("U.cs", "x\ny\nz")
        got = self.measured()
        self.assertEqual({1, 2, 3}, got.lines["U.cs"])
        self.assertEqual(["U.cs"], got.untracked)
        self.w("U.cs", "x\ny\nz\n")
        self.assertEqual({1, 2, 3}, self.measured().lines["U.cs"])      # not 4
        self.commit_all()                                               # control: committed, same set
        self.assertEqual({1, 2, 3}, self.measured().lines["U.cs"])
        self.assertEqual([], self.measured().untracked)

    def test_count_lines_is_gits_rule(self):
        self.assertEqual(0, merge_coverage.count_lines(b""))
        self.assertEqual(3, merge_coverage.count_lines(b"a\nb\nc"))
        self.assertEqual(3, merge_coverage.count_lines(b"a\nb\nc\n"))

    # 5
    def test_an_ignored_untracked_file_is_not_counted(self):
        self.w(".gitignore", "obj/\n")
        self.g("add", ".gitignore")
        self.g("commit", "-q", "-m", "ignore")
        self.w("obj/G.cs", "g\n")
        self.assertNotIn("obj/G.cs", self.measured().lines)
        self.w("other/G.cs", "g\n")                         # control: same file, not ignored
        self.assertEqual({1}, self.measured().lines["other/G.cs"])

    # 6
    def test_an_intent_to_add_file_is_counted_once_and_is_not_untracked(self):
        self.w("N.cs", "n1\nn2\n")
        self.g("add", "-N", "N.cs")
        got = self.measured()
        self.assertEqual({1, 2}, got.lines["N.cs"])
        self.assertEqual([], got.untracked)
        self.assertEqual(["N.cs"], got.uncommitted)

    # 7
    def test_a_clean_tree_is_todays_committed_range(self):
        import subprocess
        self.w("A.cs", "a1\nNEW\na2\na3\n")
        self.w("B.cs", "b\n")
        self.commit_all()
        today = subprocess.run(["git", "-C", self.repo, "diff", "-U0", "base...HEAD", "--", "*.cs"],
                               capture_output=True, text=True, check=True).stdout
        self.assertEqual({p: set(n) for p, n in merge_coverage.parse_added(today).items()},
                         self.as_dict(self.measured()))
        self.w("A.cs", "a1\nNEW\nMORE\na2\na3\n")           # control: one uncommitted edit makes them differ
        self.assertNotEqual({p: set(n) for p, n in merge_coverage.parse_added(today).items()},
                            self.as_dict(self.measured()))

    # 8
    def test_it_measures_from_the_merge_base_not_the_base_tip(self):
        self.w("A.cs", "a1\na2\n")                          # base drops a3; we keep it
        self.g("checkout", "-q", "base")
        self.g("add", "-A")
        self.g("commit", "-q", "-m", "base drops a line")
        self.g("checkout", "-q", "main")
        self.assertEqual({}, self.as_dict(self.measured()))
        raw = merge_coverage.parse_added(self.g("diff", "-U0", "base", "--", "*.cs"))
        self.assertEqual({"A.cs": {3}}, {p: set(n) for p, n in raw.items()})   # control: two-dot sees it

    # 9
    def test_diff_prefix_config_does_not_change_the_answer(self):
        self.w("A.cs", "a1\nNEW\na2\na3\n")
        plain = self.as_dict(self.measured())
        merge_base = self.g("merge-base", "base", "HEAD").strip()
        self.g("config", "diff.mnemonicPrefix", "true")
        self.assertIn("+++ w/A.cs", self.g("diff", "-U0", merge_base))            # control: config is live
        self.assertEqual(plain, self.as_dict(self.measured()))
        self.g("config", "--unset", "diff.mnemonicPrefix")
        self.g("config", "diff.noprefix", "true")
        self.assertIn("+++ A.cs", self.g("diff", "-U0", merge_base))
        self.assertEqual(plain, self.as_dict(self.measured()))

    # 10
    def test_a_crlf_working_copy_of_an_lf_blob_counts_only_the_new_line(self):
        self.w("A.cs", b"a1\r\nNEW\r\na2\r\na3\r\n")
        self.assertEqual({"A.cs": {2}}, self.as_dict(self.measured()))
        raw = merge_coverage.parse_added(self.g("diff", "-U0", "base", "--", "*.cs"))
        self.assertEqual({1, 2, 3, 4}, set(raw["A.cs"]))   # control: without the flag every line is "added"

    # 11
    def test_parse_added_pure_cases(self):
        spaced = "+++ b/Sp ace.cs\t\n@@ -1,0 +2 @@\n+x\n"
        self.assertTrue(spaced.splitlines()[0].endswith("\t"))                    # control: the tab is real
        self.assertEqual({"Sp ace.cs": {2}}, dict(merge_coverage.parse_added(spaced)))
        deleted = "+++ b/Keep.cs\n@@ -0,0 +1 @@\n+k\n--- a/Gone.cs\n+++ /dev/null\n@@ -1,2 +0,0 @@\n"
        self.assertEqual({"Keep.cs": {1}}, {p: set(n) for p, n in merge_coverage.parse_added(deleted).items()})
        misattributed = "+++ b/Keep.cs\n@@ -0,0 +1 @@\n+k\n+++ b/Keep.cs\n@@ -1,2 +9,0 @@\n"
        self.assertEqual({1}, set(merge_coverage.parse_added(misattributed)["Keep.cs"]))
        self.assertEqual({}, {p: n for p, n in merge_coverage.parse_added("+++ b/F.cs\n@@ -3,2 +2,0 @@\n").items() if n})
        self.assertEqual({5}, set(merge_coverage.parse_added("+++ b/F.cs\n@@ -0,0 +5 @@\n")["F.cs"]))

    def test_a_non_ascii_path_round_trips(self):
        self.w("é.cs", "e\n")
        self.assertEqual({1}, self.measured().lines["é.cs"])
        self.commit_all()
        self.assertEqual({1}, self.measured().lines["é.cs"])

    def test_a_deleted_file_adds_nothing_and_never_leaks_into_its_neighbour(self):
        self.w("B.cs", "b\n")
        self.commit_all()
        self.g("branch", "-f", "base", "HEAD~1")
        self.g("rm", "-q", "A.cs")
        self.assertEqual({"B.cs": {1}}, self.as_dict(self.measured()))

    # 12
    def test_a_bad_base_is_refused_before_anything_is_printed(self):
        import contextlib, io
        out = io.StringIO()
        with contextlib.redirect_stdout(out), self.assertRaises(SystemExit) as raised:
            merge_coverage.resolve_merge_base("nosuchref", self.repo)
        self.assertIn("nosuchref", str(raised.exception.code))
        self.assertEqual("", out.getvalue())
        self.assertEqual(self.g("rev-parse", "base").strip(), merge_coverage.resolve_merge_base("base", self.repo))

    def test_an_orphan_base_with_no_merge_base_is_refused(self):
        self.g("checkout", "-q", "--orphan", "island")
        self.g("rm", "-rfq", ".")
        self.w("Z.cs", "z\n")
        self.g("add", "-A")
        self.g("commit", "-q", "-m", "island")
        self.g("checkout", "-q", "main")
        with self.assertRaises(SystemExit) as raised:
            merge_coverage.resolve_merge_base("island", self.repo)
        self.assertIn("no merge-base", str(raised.exception.code))

    def test_main_refuses_a_bad_base_with_empty_stdout(self):
        import contextlib, io
        report = self.report({"A.cs": {1: 1}})
        out = io.StringIO()
        with contextlib.redirect_stdout(out), self.assertRaises(SystemExit):
            self.run_main("nosuchref", report)
        self.assertEqual("", out.getvalue())
        self.assertIn("WHOLE APP", self.run_main("base", report))      # control: a good base gets through

    # 13
    def report(self, lines_by_file, name="report"):
        """A one-file cobertura report for this repo: {file: {line: hits}}."""
        rows = "".join(
            f'<class filename="{f}"><lines>' + "".join(f'<line number="{n}" hits="{h}"/>' for n, h in ls.items())
            + "</lines></class>" for f, ls in lines_by_file.items())
        d = os.path.join(self.tmp, name)
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, "coverage.cobertura.xml")
        with open(path, "w") as f:
            f.write(f'<coverage><sources><source>{os.path.realpath(self.repo)}</source></sources><packages><package><classes>'
                    f'{rows}</classes></package></packages></coverage>')
        return path

    def run_main(self, base, report):
        import contextlib, io
        out, old_argv, old_cwd = io.StringIO(), sys.argv, os.getcwd()
        os.chdir(self.repo)
        sys.argv = ["merge-coverage.py", "--base", base, "--expect", "1", report]
        try:
            with contextlib.redirect_stdout(out):
                merge_coverage.main()
        finally:
            sys.argv = old_argv
            os.chdir(old_cwd)
        return out.getvalue()

    def test_main_end_to_end_on_a_dirty_tree_then_the_same_work_committed(self):
        self.w("A.cs", "a1\nNEW\na2\na3\n")                 # uncommitted: line 2 added, covered
        self.w("U.cs", "u1\nu2\n")                          # untracked: line 1 covered, line 2 missed
        report = self.report({"A.cs": {1: 1, 2: 1, 3: 1, 4: 1}, "U.cs": {1: 1, 2: 0}})
        dirty = self.run_main("base", report)
        self.assertIn("RESTRICTED TO LINES ADDED SINCE base, INCLUDING UNCOMMITTED CHANGES\n", dirty)
        self.assertRegex(dirty, r"measured: merge-base [0-9a-f]{12} to the working tree \(HEAD [0-9a-f]{12} "
                                r"plus 1 file\(s\) with uncommitted \.cs changes, 1 untracked \.cs file\(s\)\)")
        self.assertIn("A.cs: 1 added, 1 instrumented, 1 covered (100.0%)  [uncommitted]", dirty)
        self.assertIn("U.cs: 2 added, 2 instrumented, 1 covered (50.0%)  [untracked]", dirty)
        self.assertIn("NEW CODE  lines 2/3 = 66.7%", dirty)
        self.commit_all()
        clean = self.run_main("base", report)
        self.assertIn("\nRESTRICTED TO LINES ADDED SINCE base\n", clean)
        self.assertNotIn("measured:", clean)
        self.assertNotIn("[uncommitted]", clean)
        self.assertNotIn("[untracked]", clean)
        self.assertEqual([l for l in dirty.splitlines() if "NEW CODE" in l],
                         [l for l in clean.splitlines() if "NEW CODE" in l])

    def test_each_why_na_reason_appears_and_goes_away_when_a_line_is_instrumented(self):
        # (a) nothing added at all
        report = self.report({"A.cs": {1: 1}})
        out = self.run_main("base", report)
        self.assertIn("lines 0/0 = n/a", out)
        self.assertIn("WHY n/a: no .cs line was added between base", out)
        # (b) added, but outside the app
        self.w("tests/T.cs", "t\n")
        out = self.run_main("base", report)
        self.assertIn("WHY n/a: 1 .cs line(s) were added, none in app sources", out)
        self.assertIn("tests/T.cs", out)
        # (c) added to the app, nothing instrumented, and one file in no report
        self.w("A.cs", "a1\na2\na3\n// comment\n")
        self.w("Q.cs", "q\n")
        out = self.run_main("base", report)
        self.assertIn("WHY n/a: 2 line(s) were added to app sources and the reports instrument none of them", out)
        self.assertIn("1 file(s) appear in no report at all (see WARNING above): Q.cs", out)
        # controls: an instrumented added line removes the WHY and the n/a
        out = self.run_main("base", self.report({"A.cs": {1: 1, 4: 1}}, name="r2"))
        self.assertNotIn("WHY n/a", out)
        self.assertIn("NEW CODE  lines 1/1 = 100.0%", out)

    def test_a_bare_zero_over_zero_lines_figure_never_prints_without_a_why(self):
        report = self.report({"A.cs": {1: 1}})
        outputs = [self.run_main("base", report)]
        self.w("tests/T.cs", "t\n")
        outputs.append(self.run_main("base", report))
        self.w("A.cs", "a1\na2\na3\n// c\n")
        outputs.append(self.run_main("base", report))
        for out in outputs:
            rows = out.splitlines()
            at = [i for i, r in enumerate(rows) if "NEW CODE  lines 0/0 = n/a" in r]
            self.assertEqual(1, len(at))
            self.assertTrue(rows[at[0] + 1].lstrip().startswith("WHY n/a:"), out)
        self.assertEqual(0, sum("WHY n/a" in r for r in self.run_main("base", self.report({"A.cs": {1: 1, 4: 1}}, name="r3")).splitlines()))

    # 14
    def test_editing_an_untracked_cs_file_changes_the_stamp(self):
        self.w("U.cs", "u\n")
        before = merge_coverage.source_stamp(self.repo)
        self.w("U.cs", "u\n")                               # control: same bytes, same stamp
        self.assertEqual(before, merge_coverage.source_stamp(self.repo))
        self.w("U.cs", "edited\n")
        self.assertNotEqual(before, merge_coverage.source_stamp(self.repo))

    def test_the_shell_stamp_agrees_with_python_when_an_untracked_file_was_edited(self):
        import re, shutil, subprocess
        bash = shutil.which("bash")
        if not bash:
            self.skipTest("no bash")
        with open(os.path.join(_HERE, "coverage.sh")) as f:
            src = f.read()
        block = re.search(r"^# --- source-stamp begin\n(.*?)^# --- source-stamp end$", src, re.M | re.S).group(1)
        pick = re.search(r"^if command -v shasum .*; fi$", src, re.M).group(0)
        os.makedirs(os.path.join(self.tmp, "o"))
        out_dir = os.path.join(self.tmp, "o").replace("\\", "/")
        repo = self.repo.replace("\\", "/")
        digests = []
        for content in ("u1\n", "u2 edited\n"):
            self.w("U.cs", content)
            subprocess.run([bash, "-c", f'set -euo pipefail; cd "{repo}"; OUT="{out_dir}"; {pick}\n{block}'],
                           check=True, capture_output=True)
            with open(os.path.join(self.tmp, "o", "source-stamp")) as f:
                shell = f.read()
            self.assertEqual(merge_coverage.source_stamp(self.repo), shell)
            digests.append(shell)
        self.assertNotEqual(digests[0], digests[1])


if __name__ == "__main__":
    unittest.main()
