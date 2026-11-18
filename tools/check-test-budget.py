#!/usr/bin/env python3
"""CB-367: fail the run when any single test exceeds a time budget.

CB-334 sat in the suite for weeks because a test that waits out a 120 s
production timeout *passes*: nothing in a green run says it took two minutes.
This turns that shape into a red build. It reads the TRX files every suite
already writes (VSTest and Microsoft.Testing.Platform produce the same
`UnitTestResult testName=... duration=hh:mm:ss.fffffff` rows), so it needs no
new instrumentation and runs the same on both runners.

    check-test-budget.py [--budget SECONDS] [--allowlist FILE] TRX [TRX ...]

A TRX argument may be a directory, searched for *.trx. Exit 0 when every test
is within budget or allow-listed; 1 otherwise; 2 for a malformed allow-list or
a report it cannot measure -- missing, unparsable, zero executed results, or an
executed result with no duration (a check that cannot read its input must not
pass). The three console exes write no report and are outside the budget.

The allow-list (tools/test-budget-allowlist.txt) is one entry per line:

    <Fully.Qualified.TestName> | <reason, ideally naming the ticket that retires it>

An entry without a reason is an error, not a free pass: the point of the list
is that every slow test has a stated cause and an owner. Parameterised tests
match on the name with the `(args)` suffix removed. An entry that nothing
exceeded is reported as stale (a warning, not a failure, since a test can be
slow on one runner only) so the list shrinks as the tickets land.
"""
import argparse
import glob
import os
import sys
import xml.etree.ElementTree as ET

DEFAULT_BUDGET = 30.0


def parse_duration(text):
    # hh:mm:ss.fffffff, hours possibly > 24 is not expected; days are not used.
    h, m, s = text.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def base_name(name):
    i = name.find("(")
    return name if i < 0 else name[:i]


def load_allowlist(path):
    """Returns {name: reason}. Raises ValueError naming the first bad line."""
    entries = {}
    with open(path, encoding="utf-8") as f:
        for n, raw in enumerate(f, 1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            name, sep, reason = (p.strip() for p in line.partition("|"))
            if not sep or not name or not reason:
                raise ValueError(f"{path}:{n}: entry needs '<test name> | <reason>': {line!r}")
            entries[name] = reason
    return entries


def read_results(paths):
    """Yields (report, test name, seconds) for every result in every TRX."""
    files = []
    for p in paths:
        if os.path.isdir(p):
            files += sorted(glob.glob(os.path.join(p, "**", "*.trx"), recursive=True))
        else:
            files.append(p)
    if not files:
        raise ValueError("no .trx reports found in: " + " ".join(paths))
    for f in files:
        root = ET.parse(f).getroot()
        n = 0
        for e in root.iter():
            if e.tag.rsplit("}", 1)[-1] != "UnitTestResult":
                continue
            if e.get("outcome") == "NotExecuted":
                continue  # skipped tests have no duration to measure
            if not e.get("duration"):
                raise ValueError(f"{f}: executed result {e.get('testName')!r} has no duration")
            n += 1
            yield f, e.get("testName"), parse_duration(e.get("duration"))
        if n == 0:
            raise ValueError(f"{f}: no executed test results to measure")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--budget", type=float, default=DEFAULT_BUDGET)
    ap.add_argument("--allowlist", default=None)
    ap.add_argument("reports", nargs="+")
    args = ap.parse_args(argv)

    try:
        allow = load_allowlist(args.allowlist) if args.allowlist else {}
        results = list(read_results(args.reports))
    except (ValueError, OSError, ET.ParseError) as ex:
        print(f"::error::test budget check could not run: {ex}")
        return 2

    over = [(f, n, s) for f, n, s in results if s > args.budget]
    failed, allowed, used = [], [], set()
    for f, n, s in over:
        key = n if n in allow else base_name(n)
        if key in allow:
            used.add(key)
            allowed.append((n, s, allow[key]))
        else:
            failed.append((f, n, s))

    print(f"Test budget {args.budget:g}s: {len(results)} results in {len(set(f for f, _, _ in results))} report(s), "
          f"{len(over)} over budget ({len(allowed)} allow-listed).")
    for n, s, why in allowed:
        print(f"  allowed  {s:7.1f}s  {n}  -- {why}")
    for name in sorted(set(allow) - used):
        print(f"::warning::allow-list entry matched nothing over budget (stale on this run?): {name}")
    for f, n, s in sorted(failed, key=lambda r: -r[2]):
        print(f"::error::{n} took {s:.1f}s, over the {args.budget:g}s budget "
              f"({os.path.basename(f)}). Make it faster, or allow-list it with a reason and a ticket.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
