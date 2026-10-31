#!/usr/bin/env python3
"""Merge this repo's coverage reports into one honest number.

Why a script instead of a flag: `dotnet test tests/Tests.sln` runs three
projects on **two different test platforms**, and they emit coverage two
different ways (see tools/coverage.sh). That leaves three cobertura files
measuring the *same* Orbweaver assembly, and no single number in any of them
is the truth — a line exercised only by a UI test is reported as unhit by the
unit-test run.

So the merge is a union over hits: a line counts as covered if any suite covered
it, and a branch point takes the best taken-count any suite recorded. Summing
the reports instead would be wrong in both directions at once, double-counting
the denominator while undercounting the numerator.

It is *not* a union over what exists. coverlet's report is the authority on
which lines and which branch points are real, and the other engine may only
contribute hits against them — see merge_secondary().

Usage:
    tools/merge-coverage.py <report.xml> [more.xml ...] [--base <git-ref>] [--expect N]

Exits non-zero without printing a figure unless it finds exactly --expect
(default 4) reports, and -- when coverage.sh left a source-stamp beside them --
unless the checkout it is merging against is the one they were measured in.
--allow-source-mismatch overrides that, for a deliberate cross-checkout merge.

With --base, also reports coverage restricted to the lines added since that ref
(measured from the merge-base with HEAD to the working tree, so staged, unstaged and
untracked .cs changes are included and tagged, and a --base that is not a commit is
refused up front) — which is usually the number you actually want when reviewing a change, since
a file-level percentage is dominated by whatever was already there.

It also reports what has been held *out* of the number by
[ExcludeFromCodeCoverage]. That is not decoration. Both coverage engines honour
the attribute by omitting the code entirely, so an excluded file and a deleted
file look identical in a report, and a percentage can be walked to 100% by
excluding whatever refuses to be covered. The exclusions are read back out of
the sources and printed next to the number, so the number always ships with the
size of its own blind spot.
"""
import collections
import glob
import hashlib
import io
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


# coverage.sh runs four suites, each producing exactly one cobertura report.
# Any other count means the headline is measuring something other than the
# repository (CB-229: two reports, on Windows, and a whole-app figure printed
# anyway).
EXPECTED_REPORTS = 4

_MSYS_DRIVE = re.compile(r"^/([A-Za-z])(/.*)?$")


def native_path(pattern, windows=None):
    """Turn an MSYS "/c/Users/x" path into "C:/Users/x" on Windows.

    A native Windows Python reads "/c/Users/x" as a path on the current drive
    and matches nothing, silently. coverage.sh hands over cygpath -m paths, but
    a person running this by hand from Git Bash will type the MSYS spelling, so
    be robust to it rather than to the caller's discipline. Anywhere but Windows
    this is the identity (and "/c/foo" is a perfectly good POSIX path).
    """
    if windows is None:
        windows = os.name == "nt"
    if not windows:
        return pattern
    m = _MSYS_DRIVE.match(pattern)
    if not m:
        return pattern
    return f"{m.group(1).upper()}:{m.group(2) or '/'}"


def parse_args(argv):
    base = None
    expect = EXPECTED_REPORTS
    patterns = []
    i = 0
    while i < len(argv):
        if argv[i] == "--base":
            base = argv[i + 1]
            i += 2
            continue
        if argv[i] == "--expect":
            expect = int(argv[i + 1])
            i += 2
            continue
        patterns.append(argv[i])
        i += 1
    reports = []
    for pat in patterns:
        for p in glob.glob(native_path(pat), recursive=True):
            if p not in reports:
                reports.append(p)
    return base, reports, expect


def refuse_unless_expected(reports, expect):
    """Return an error message when the report count is wrong, else None."""
    if len(reports) == expect:
        return None
    return (f"REFUSING to print a coverage figure: merged {len(reports)} report(s), "
            f"expected exactly {expect}. A number computed from the wrong set of "
            f"reports is fiction (CLAUDE.md, Coverage). Reports found: "
            f"{', '.join(reports) if reports else 'none'}")


STAMP_NAME = "source-stamp"


def source_stamp(root):
    """What the working tree is, as coverage.sh stamps it beside its reports.

    HEAD plus a digest of everything uncommitted: tracked changes by content,
    untracked files by name, and untracked .cs files by content too (CB-251 --
    --base now reads added lines out of those files, so an edit to one while the
    suites run must change the stamp). The contents go through `git hash-object`
    on both sides rather than being re-read here, so git's own clean filters
    apply to both and there is no second implementation to drift. coverage.sh
    writes exactly this with the same git commands piped through
    shasum/sha1sum, so the two agree byte for byte. CB-244:
    reports produced at one sha and merged against another checkout's sources
    attribute their lines to the wrong code, and `merged 4` plus fresh timestamps
    cannot see it -- it happened to a real review.
    """
    def git(*args):
        return subprocess.run(["git", "-C", root, *args], capture_output=True, check=True).stdout

    head = git("rev-parse", "HEAD").decode().strip()
    untracked_cs = subprocess.run(
        ["git", "-C", root, "-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard", "--", "*.cs"],
        capture_output=True, check=True).stdout
    contents = subprocess.run(["git", "-C", root, "hash-object", "--stdin-paths"],
                              input=untracked_cs, capture_output=True, check=True).stdout
    digest = hashlib.sha1(git("diff", "HEAD") + git("ls-files", "--others", "--exclude-standard")
                          + contents).hexdigest()[:16]
    return f"{head}\n{digest}\n"


def find_stamp(reports):
    """The nearest source-stamp at or above any report (coverage.sh puts one at the top
    of its output directory), or None for reports that did not come from coverage.sh."""
    for report in reports:
        directory = os.path.dirname(os.path.abspath(report))
        for _ in range(5):
            candidate = os.path.join(directory, STAMP_NAME)
            if os.path.isfile(candidate):
                return candidate
            directory = os.path.dirname(directory)
    return None


def source_mismatch(reports, root):
    """An error message when the reports were measured in a different checkout than
    the one being merged against, else None. No stamp means a hand-assembled set of
    reports and nothing to compare."""
    stamp_path = find_stamp(reports)
    if stamp_path is None:
        return None
    with open(stamp_path) as f:
        measured = f.read()
    current = source_stamp(root)
    if measured == current:
        return None

    def parts(stamp):
        # A stamp with no digest line (an old or hand-made one) must refuse with the
        # message below, not crash building it.
        found = stamp.split()
        return (found + ["(missing)", "(missing)"])[:2]

    (measured_head, measured_digest), (current_head, current_digest) = parts(measured), parts(current)
    return ("REFUSING to print a coverage figure: these reports were measured in a different "
            f"checkout than the one being merged against.\n  measured: {measured_head} "
            f"(changes {measured_digest})\n  now:      {current_head} "
            f"(changes {current_digest})\n"
            "Line numbers in the reports belong to the sources they were built from; against "
            "any others every attribution below would be plausible and wrong. Re-run "
            "coverage.sh in this checkout, or pass --allow-source-mismatch if the merge is "
            "deliberately across checkouts.")


def repo_root():
    return subprocess.run(["git", "rev-parse", "--show-toplevel"],
                          capture_output=True, text=True, check=True).stdout.strip()


def instrumented_lines(reports, root):
    """file -> {line numbers} that these reports instrumented at all."""
    seen = defaultdict(set)

    for report in reports:
        tree = ET.parse(report).getroot()
        sources = [s.text for s in tree.findall("./sources/source") if s.text]
        for cls in tree.iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            path = resolve(filename, sources, root)
            for line in cls.iter("line"):
                seen[path].add(int(line.get("number")))

    return seen


def resolve(filename, sources, root):
    path = filename.replace("\\", "/")
    if not os.path.isabs(path):
        for src in sources:
            if os.path.exists(os.path.join(src, path)):
                path = os.path.join(src, path)
                break
    return os.path.relpath(os.path.abspath(path), root)


def load(reports, root, keep=None):
    """file -> {line: covered}, file -> {line: (taken, total)}

    `keep`, when given, is the set of lines each file is allowed to contribute —
    see the comment in main() about the two engines disagreeing about
    [ExcludeFromCodeCoverage] on a *method*.
    """
    lines = defaultdict(dict)
    branches = defaultdict(dict)

    for report in reports:
        tree = ET.parse(report).getroot()
        sources = [s.text for s in tree.findall("./sources/source") if s.text]
        for cls in tree.iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            path = resolve(filename, sources, root)
            allowed = None if keep is None else keep.get(path)

            for line in cls.iter("line"):
                number = int(line.get("number"))

                # A line this engine instrumented but the authority did not is a
                # line the authority excluded. Skipping it here is what makes a
                # member-level exclusion mean the same thing in both reports.
                if allowed is not None and number not in allowed:
                    continue

                hits = int(line.get("hits", "0"))
                lines[path][number] = lines[path].get(number, False) or hits > 0

                # The attribute is "True"/"False" in these reports, not
                # "true"/"false". Comparing case-sensitively made an early
                # version of this script confidently print "0/0 branches".
                if (line.get("branch") or "").casefold() != "true":
                    continue
                condition = line.get("condition-coverage") or ""
                if "(" not in condition:
                    continue
                taken, total = condition.split("(")[1].rstrip(")").split("/")
                previous = branches[path].get(number, (0, int(total)))
                # Max on BOTH halves, not just the numerator. The two engines do
                # not always agree on how many arcs a line has — the same `if`
                # can be reported as 2 arcs by one and 4 by the other — and
                # keeping the last-seen total while maxing the taken count can
                # pair a taken from the wider reading with a total from the
                # narrower one and print a line as fully covered when neither
                # suite covered it fully. Taking the widest denominator anyone
                # reported is the conservative reading, and being wrong in the
                # pessimistic direction is the only acceptable direction here.
                branches[path][number] = (
                    max(previous[0], int(taken)), max(previous[1], int(total)))

    return lines, branches


def merge_secondary(lines, branches, more_lines, more_branches, keep):
    """Fold the secondary (MTP) reports into the authority's (coverlet's) view.

    `lines`/`branches` are coverlet's, and are mutated in place. `keep` is the
    per-file set of lines coverlet instrumented, or None when there was nothing
    to be the authority over (a run with coverlet reports only).

    Lines and branches obey the *same* authority rule, and the second half of
    that used not to be true. coverlet's report says which lines exist, because
    it is the engine that honours [ExcludeFromCodeCoverage] on a method — and by
    exactly the same argument its report says which *branch points* exist, since
    a line it instrumented and recorded no branch on has no branch on it. The
    MTP engines disagree about this constantly. Measured over one run of all
    four suites, 158 branch points in 42 files existed in the MTP reports and in
    neither coverlet report — 416 arcs, 328 of them claimed as taken. What the
    lines look like is the giveaway, and it is the signature CB-6 already wrote
    down: OpenClawSessions.cs:285 is `lock (Gate) return agentIds` reported
    (0/6), :328 is `List<string> ids;` reported (0/2), and OrbGlyph.cs:62 is the
    second half of a two-line *comment* reported (4/4). A declaration and a
    comment cannot hold a branch. The arcs belong to compiler-generated code
    that engine attributes to whichever statement line is nearest; coverlet,
    instrumenting the same assembly, puts them somewhere else or nowhere.

    It moves the number in BOTH directions, which is why it survived. Dropping
    them took the whole app from 9361/10483 (89.3%) to 9033/10067 (89.7%), and
    per file it went up for OpenClawSessions.cs (91.6% -> 92.4%) and down for
    OrbGlyph.cs (94.4% -> 93.8%), because a phantom point reported 4/4 flatters
    a file exactly as a phantom 0/6 punishes one. Neither direction was ever a
    statement about the tests.

    A file coverlet never reported at all is the one case where the secondary
    reports may still introduce branch points, for the same reason `keep` is
    consulted per file rather than globally: there is no authority to defer to,
    and dropping the file would be worse than trusting the only engine that saw
    it.
    """
    for path, hits in more_lines.items():
        for number, covered in hits.items():
            lines[path][number] = lines[path].get(number, False) or covered

    for path, arcs in more_branches.items():
        authoritative = keep is not None and path in keep
        for number, (taken, total) in arcs.items():
            previous = branches[path].get(number)
            if previous is None:
                if authoritative:
                    continue
                branches[path][number] = (taken, total)
                continue

            # A branch point coverlet knows: its total is the total (CB-244).
            # This used to take max() of both halves, on the worry below, and
            # that adopted whatever wider arc count the MTP engine reported for
            # the same line: ChatMarkdown.cs:179 was 6/6 in coverlet's
            # unit run and 10/12 in the MTP runs, and merged to 10/12, a
            # denominator for arcs coverlet says the line does not have
            # (reports and sources both at 95e84ea5).
            #
            # The worry max() answered was real and is still answered: pairing a
            # taken count from the wider engine with the narrower total can print
            # a line as fully covered when neither suite covered it. Measured on
            # one tree, a bare cap (min(taken, coverlet's total)) did exactly
            # that at 7 points, 13 arcs. The two engines' arcs cannot be paired
            # up, so an MTP count is only believed where it needs no pairing:
            #   - the same total: the same arcs, so the larger taken count wins;
            #   - a wider total that MTP covered completely: every arc it counts
            #     was taken, so every arc coverlet counts was too (capped at
            #     coverlet's total);
            #   - anything else (a wider total covered partly, or a narrower
            #     one): MTP cannot say which of coverlet's arcs it took, so it
            #     adds nothing. The pessimistic reading, and an honest one.
            coverlet_taken, coverlet_total = previous
            if total == coverlet_total:
                branches[path][number] = (max(coverlet_taken, taken), coverlet_total)
            elif total > coverlet_total and taken == total:
                branches[path][number] = (coverlet_total, coverlet_total)


def exclusions(root):
    """path -> number of [ExcludeFromCodeCoverage] sites in it.

    Read out of the sources rather than out of the reports, because a coverage
    report cannot tell you this: both engines honour the attribute by *omitting*
    the code entirely, so an excluded member and a member that was deleted look
    identical from here. Counting the attribute is the only way to say how much
    of the app the headline number has stopped being about.
    """
    found = {}
    for name in sorted(os.listdir(root)):
        if not name.endswith(".cs"):
            continue
        with io.open(os.path.join(root, name), encoding="utf-8", errors="replace") as f:
            body = f.read()
        # Matches whether the attribute stands alone or shares its brackets with
        # others, and tolerates the fully-qualified spelling.
        sites = len(re.findall(
            r"\[(?:[^\]]*[\s,\[])?(?:System\.Diagnostics\.CodeAnalysis\.)?"
            r"ExcludeFromCodeCoverage(?:Attribute)?\b", body))
        if sites:
            found[name] = sites
    return found


def source_lines(root, path):
    """Physical lines in a file, for saying how big an exclusion is."""
    try:
        with io.open(os.path.join(root, path), encoding="utf-8", errors="replace") as f:
            return sum(1 for _ in f)
    except OSError:
        return 0


def is_app_file(path):
    """The app's own sources only — not the suites, not generated code."""
    if path.startswith("tests/") or path.startswith("obj/") or "/obj/" in path:
        return False
    return path.endswith(".cs")


def rate(part, whole):
    return f"{100.0 * part / whole:.1f}%" if whole else "n/a"


def totals(paths, lines, branches):
    hit = total = taken = arcs = 0
    for path in paths:
        for covered in lines[path].values():
            total += 1
            hit += 1 if covered else 0
        for got, want in branches[path].values():
            taken += got
            arcs += want
    return hit, total, taken, arcs


AddedLines = collections.namedtuple("AddedLines", "lines merge_base head uncommitted untracked")


def resolve_merge_base(base, root):
    """The commit --base is measured from, or exit with a reason.

    Refused up front, before anything is printed: a bad --base used to print the
    whole-app figure and then die with a traceback, which reads as a half-finished
    run rather than as a refusal.
    """
    def git(*args):
        return subprocess.run(["git", "-C", root, *args], capture_output=True, text=True)

    if git("rev-parse", "--verify", "--quiet", f"{base}^{{commit}}").returncode != 0:
        sys.exit(f"REFUSING to measure added lines: --base {base} is not a commit in this checkout")
    found = git("merge-base", base, "HEAD")
    if found.returncode != 0 or not found.stdout.strip():
        sys.exit(f"REFUSING to measure added lines: --base {base} has no merge-base with HEAD")
    return found.stdout.strip()


def parse_added(diff_text):
    """path -> {added line numbers} out of a `git diff -U0` (pure)."""
    added = defaultdict(set)
    current = None
    for row in diff_text.splitlines():
        if row.startswith("+++ /dev/null"):
            current = None        # a deleted file's hunks belong to nobody
        elif row.startswith("+++ b/"):
            current = row[6:].rstrip("\t")   # git appends a tab when the path has a space
        elif row.startswith("@@") and current:
            match = re.search(r"\+(\d+)(?:,(\d+))?", row)
            if match:
                start = int(match.group(1))
                for n in range(start, start + int(match.group(2) or "1")):
                    added[current].add(n)
    return added


def count_lines(data):
    """Lines in a file as git counts them: a last line with no newline still counts."""
    return data.count(b"\n") + (1 if data and not data.endswith(b"\n") else 0)


def added_lines(base, root):
    """Lines added between merge-base(base, HEAD) and the WORKING TREE.

    CB-251: this used to diff `base...HEAD`, commits only, so uncommitted work
    measured as 0/0 = n/a with no warning. The reports are built from the working
    tree (source_mismatch() refuses any other), so the working tree is what their
    line numbers belong to; on a clean tree it equals HEAD and the answer is the
    committed range exactly. Untracked .cs files count wholly, and respect
    .gitignore. The explicit prefixes keep diff.mnemonicPrefix / diff.noprefix from
    changing the `+++` lines, --ignore-cr-at-eol keeps a CRLF checkout of an LF blob
    from counting every line as added, and quotePath=false keeps non-ASCII names
    readable.
    """
    def git(*args):
        return subprocess.run(["git", "-C", root, "-c", "core.quotePath=false", *args],
                              capture_output=True, check=True).stdout.decode("utf-8", errors="replace")

    merge_base = resolve_merge_base(base, root)
    diff = git("diff", "-U0", "--no-color", "--no-ext-diff", "--ignore-cr-at-eol",
               "--src-prefix=a/", "--dst-prefix=b/", merge_base, "--", "*.cs")
    added = parse_added(diff)
    untracked = [p for p in git("ls-files", "--others", "--exclude-standard", "--", "*.cs").splitlines() if p]
    counted = []
    for path in untracked:
        try:
            with open(os.path.join(root, path), "rb") as f:
                size = count_lines(f.read())
        except OSError:
            continue
        counted.append(path)
        added[path] = set(range(1, size + 1))
    uncommitted = [p for p in git("diff", "--name-only", "HEAD", "--", "*.cs").splitlines() if p]
    head = git("rev-parse", "HEAD").strip()
    return AddedLines(added, merge_base, head, uncommitted, counted)


def main():
    argv = [a for a in sys.argv[1:] if a != "--allow-source-mismatch"]
    allow_mismatch = len(argv) != len(sys.argv) - 1
    base, reports, expect = parse_args(argv)
    problem = refuse_unless_expected(reports, expect)
    if problem:
        sys.exit(problem)

    root = repo_root()
    if not allow_mismatch:
        problem = source_mismatch(reports, root)
        if problem:
            sys.exit(problem)
    if base:
        resolve_merge_base(base, root)

    # The two engines do not agree about [ExcludeFromCodeCoverage] on a *method*,
    # and the disagreement is silent. coverlet honours it — an excluded method's
    # body is not instrumented at all — while Microsoft.CodeCoverage, which the
    # two MTP suites use, instruments it anyway and reports every line unhit.
    # Both honour it on a *class*, which is why this went unnoticed until enough
    # member-level exclusions existed to matter: 157 sites, every one of which the
    # MTP reports were quietly putting back into the denominator.
    #
    # So coverlet's view of *which lines exist* is the authority — and, since
    # CB-100, of which *branch points* exist too; merge_secondary() has why. The
    # MTP reports contribute hits for those only. Not the other way round, and
    # not a union: a union means the attribute does nothing wherever it is on a
    # method, which is exactly the kind of claim-that-is-not-true this script was
    # extended to stop making.
    #
    # Only applied to files coverlet reported. It instruments the assembly at
    # build time rather than on execution, so it reports every app file whether or
    # not a unit test touched it — but a file it genuinely never saw must not be
    # silently dropped from the MTP reports, so `keep` is consulted per file.
    coverlet = [r for r in reports if os.path.basename(r) == "coverage.cobertura.xml"]
    others = [r for r in reports if r not in coverlet]

    keep = instrumented_lines(coverlet, root) if coverlet and others else None

    lines, branches = load(coverlet, root)
    if others:
        more_lines, more_branches = load(others, root, keep)
        merge_secondary(lines, branches, more_lines, more_branches, keep)

    app = sorted(p for p in lines if is_app_file(p))

    print(f"merged {len(reports)} report(s)\n")

    hit, total, taken, arcs = totals(app, lines, branches)
    print(f"WHOLE APP   lines {hit}/{total} = {rate(hit, total)}"
          f"   branches {taken}/{arcs} = {rate(taken, arcs)}")
    print(f"            {len(app)} source files instrumented")

    # A file absent from every report is not 0% — it is missing from the
    # denominator, which inflates every number above it. But there are two very
    # different reasons a file can be absent, and lumping them together is how a
    # 100% headline gets to hide an assembly's worth of untested code:
    #
    #   * [ExcludeFromCodeCoverage] — a decision somebody made and can defend.
    #   * anything else — a suite that never loaded the type, a project missing
    #     from the run, a rename. A bug in the measurement, not a decision.
    #
    # Both leave the denominator, so the percentage cannot tell them apart. This
    # is the only place that can, and it reads the attribute out of the sources
    # to do it.
    on_disk = {p for p in os.listdir(root) if p.endswith(".cs")}
    excluded = exclusions(root)
    absent = on_disk - set(app)

    fully_excluded = sorted(p for p in absent if p in excluded)
    unexplained = sorted(p for p in absent if p not in excluded)
    partly_excluded = sorted(p for p in excluded if p in set(app))

    if fully_excluded:
        cost = sum(source_lines(root, p) for p in fully_excluded)
        print(f"            EXCLUDED: {len(fully_excluded)} file(s) held out of the "
              f"number above by [ExcludeFromCodeCoverage] ({cost} source lines)")
        for path in fully_excluded:
            print(f"                      {source_lines(root, path):5d} lines  {path}")

    if partly_excluded:
        sites = sum(excluded[p] for p in partly_excluded)
        print(f"            EXCLUDED: {sites} more [ExcludeFromCodeCoverage] site(s) "
              f"inside {len(partly_excluded)} measured file(s)")
        for path in partly_excluded:
            print(f"                      {excluded[path]:5d} site(s)  {path}")

    if unexplained:
        print(f"            WARNING: {len(unexplained)} app file(s) in no report and "
              f"not excluded: {', '.join(unexplained)}")

    if fully_excluded or partly_excluded:
        print("            Read the percentage as coverage OF WHAT REMAINS. An "
              "exclusion is a claim")
        print("            that a headless runner cannot execute the code, and it "
              "is only as good as")
        print("            the reviewer who checked it.")
    print()

    print("BY FILE (most-covered first)")
    for path in sorted(app, key=lambda p: -sum(1 for c in lines[p].values() if c)):
        hit, total, taken, arcs = totals([path], lines, branches)
        print(f"  {rate(hit, total):>6}  lines {hit:5d}/{total:<5d}"
              f"  branches {rate(taken, arcs):>6}  {path}")

    if not base:
        return

    result = added_lines(base, root)
    added = result.lines
    dirty = bool(result.uncommitted or result.untracked)
    print(f"\nRESTRICTED TO LINES ADDED SINCE {base}" + (", INCLUDING UNCOMMITTED CHANGES" if dirty else ""))
    if dirty:
        print(f"  measured: merge-base {result.merge_base[:12]} to the working tree "
              f"(HEAD {result.head[:12]} plus {len(result.uncommitted)} file(s) with uncommitted .cs changes, "
              f"{len(result.untracked)} untracked .cs file(s))")
    new_hit = new_total = new_taken = new_arcs = 0
    uncovered = []
    partials = []
    for path in sorted(added):
        if not is_app_file(path):
            continue
        instrumented = sorted(n for n in added[path] if n in lines.get(path, {}))
        if not instrumented:
            continue
        covered = [n for n in instrumented if lines[path][n]]
        missed = [n for n in instrumented if not lines[path][n]]
        new_hit += len(covered)
        new_total += len(instrumented)
        for number in instrumented:
            if number in branches.get(path, {}):
                got, want = branches[path][number]
                new_taken += got
                new_arcs += want
        tag = "  [untracked]" if path in result.untracked else "  [uncommitted]" if path in result.uncommitted else ""
        print(f"  {path}: {len(added[path])} added, {len(instrumented)} instrumented,"
              f" {len(covered)} covered ({rate(len(covered), len(instrumented))}){tag}")
        if missed:
            uncovered.append((path, missed))
        partial = partial_branch_report(path, instrumented, branches)
        if partial:
            partials.append(partial)

    print(f"\n  NEW CODE  lines {new_hit}/{new_total} = {rate(new_hit, new_total)}"
          f"   branches {new_taken}/{new_arcs} = {rate(new_taken, new_arcs)}")
    if not new_total:
        print("  " + why_not_applicable(added, lines, result.merge_base, base))
    for path, missed in uncovered:
        print(f"  UNCOVERED {path}: {missed}")
    for line in partials:
        print(line)


def partial_branch_report(path, instrumented, branches):
    """The `PARTIAL BRANCHES` line for one file, or None when every added branch
    point was fully taken.

    UNCOVERED lists lines that never ran, so a line that ran with only some of
    its arms taken, which is exactly what keeps the added-branches figure under
    100% while the lines figure sits at 100%, was invisible: the tool reported a
    gap it could not locate. Each entry is `line(got/want)`, restricted to the
    added, instrumented lines it is handed, and a fully-taken point is not listed.
    """
    partial = [
        f"{n}({got}/{want})"
        for n in instrumented
        for got, want in [branches.get(path, {}).get(n, (0, 0))]
        if got < want
    ]
    return f"  PARTIAL BRANCHES {path}: [{', '.join(partial)}]" if partial else None


def why_not_applicable(added, lines, merge_base, base):
    """The one line that says why a lines total of 0 is not an unmeasured run."""
    everything = {p: n for p, n in added.items() if n}
    if not everything:
        return (f"WHY n/a: no .cs line was added between {base} (merge-base {merge_base[:12]}) and the "
                "working tree, committed, staged, unstaged or untracked. Nothing to measure; "
                "this is not an unmeasured run.")
    count = sum(len(n) for n in everything.values())
    in_app = {p: n for p, n in everything.items() if is_app_file(p)}
    if not in_app:
        shown = ", ".join(sorted(everything)[:5])
        return (f"WHY n/a: {count} .cs line(s) were added, none in app sources "
                f"(tests/ and obj/ are not part of this number): {shown}")
    count = sum(len(n) for n in in_app.values())
    reason = (f"WHY n/a: {count} line(s) were added to app sources and the reports instrument none of them. "
              "Comments, blank lines, usings and declarations carry no sequence point, and "
              "[ExcludeFromCodeCoverage] removes code from the reports.")
    unreported = sorted(p for p in in_app if p not in lines)
    if unreported:
        reason += (f" {len(unreported)} file(s) appear in no report at all (see WARNING above): "
                   f"{', '.join(unreported)}")
    return reason


if __name__ == "__main__":
    main()
