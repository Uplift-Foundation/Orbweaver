#!/usr/bin/env python3
"""Run one test class N times and fail on any red run (CB-455, stress.yml).

    python tools/stress-run.py --suite unit --test-class Orbweaver.Tests.FooTests --n 30

Why a script and not a shell loop in the workflow: a filter that matches
nothing exits 0 and prints a green summary, so "30 passes" of a mistyped class
would read as proof. This counts the tests each run reports and fails when a
run executed none (the same trap CB-270 recorded for `--filter` on the MTP
suites), and it keeps going after a failure so the report says how many of the
N were red, which is the number a flake fix is judged by.

The class name reaches here as an argument, never interpolated into shell by
the workflow, and is checked against a strict pattern again here.
"""
import argparse
import re
import subprocess
import sys

CLASS_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]{0,199}$")
PROJECTS = {"unit": "tests/UnitTests", "integration": "tests/IntegrationTests", "ui": "tests/UiTests"}
MAX_N = 50


def command(suite, test_class):
    proj = PROJECTS[suite]
    base = ["dotnet", "test", proj, "-c", "Release", "--no-build"]
    if suite == "ui":  # Microsoft Testing Platform: --filter is silently ignored
        return base + ["--", "--filter-class", test_class]
    return base + ["--filter", f"FullyQualifiedName~{test_class}"]


def counts(output):
    """(total, failed) read off a VSTest or MTP summary line, else None."""
    total = re.findall(r"[Tt]otal:\s*(\d+)", output)
    failed = re.findall(r"[Ff]ailed:\s*(\d+)", output)
    if not total:
        return None
    return int(total[-1]), int(failed[-1]) if failed else 0


def judge(returncode, output):
    """'green', 'red' or 'empty' for one run."""
    c = counts(output)
    if c is None or c[0] == 0:
        return "empty"
    if returncode != 0 or c[1] > 0:
        return "red"
    return "green"


def default_runner(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    return p.returncode, p.stdout + p.stderr


def stress(suite, test_class, n, runner=default_runner, out=print):
    """Exit code: 0 all green, 1 any red, 2 bad input or any run executed nothing."""
    if suite not in PROJECTS or not CLASS_RE.match(test_class) or not 1 <= n <= MAX_N:
        out("refusing: bad suite, test class or n")
        return 2
    verdicts = []
    for i in range(1, n + 1):
        rc, text = runner(command(suite, test_class))
        v = judge(rc, text)
        verdicts.append(v)
        out(f"run {i}/{n}: {v}")
        if v == "red":
            out(text[-3000:])
    red, empty = verdicts.count("red"), verdicts.count("empty")
    out(f"{test_class} x{n}: {verdicts.count('green')} green, {red} red, {empty} ran no tests")
    if empty:
        return 2
    return 1 if red else 0


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--suite", required=True)
    p.add_argument("--test-class", required=True)
    p.add_argument("--n", type=int, required=True)
    a = p.parse_args(argv)
    return stress(a.suite, a.test_class, a.n)


if __name__ == "__main__":
    sys.exit(main())
