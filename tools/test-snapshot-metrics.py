#!/usr/bin/env python3
"""Tests for tools/snapshot-metrics.py (CB-296).

    python3 tools/test-snapshot-metrics.py
"""
import csv
import importlib.util
import os
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "snapshot_metrics", os.path.join(_HERE, "snapshot-metrics.py"))
sm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(sm)

REPO = {"stargazers_count": 1, "forks_count": 0, "open_issues_count": 2}
RELEASES = [{"tag_name": "v1.0.0", "assets": [
    {"name": "a.dmg", "download_count": 5}, {"name": "b.exe", "download_count": 1}]}]


def views(*pairs):
    return {"views": [{"timestamp": d + "T00:00:00Z", "count": c, "uniques": u}
                      for d, c, u in pairs]}


def snapshot(date, v, repo=REPO):
    return sm.build(date, repo, v, {"clones": []},
                    [{"referrer": "github.com", "count": 3, "uniques": 1}],
                    [{"path": "/x", "count": 5, "uniques": 2}], RELEASES)


def rows(d, name):
    with open(os.path.join(d, name), newline="") as f:
        return list(csv.DictReader(f))


def apply(d, snap):
    for name, (header, rs, keys) in snap.items():
        sm.upsert(os.path.join(d, name), header, rs, keys)


class SnapshotTests(unittest.TestCase):
    def test_rerun_same_day_does_not_duplicate(self):
        with tempfile.TemporaryDirectory() as d:
            s = snapshot("2026-10-28", views(("2026-10-27", 4, 2)))
            apply(d, s)
            apply(d, s)
            self.assertEqual(len(rows(d, "views.csv")), 1)
            self.assertEqual(len(rows(d, "downloads.csv")), 2)
            self.assertEqual(len(rows(d, "referrers.csv")), 1)

    def test_overlapping_windows_upsert_by_day_and_keep_history(self):
        with tempfile.TemporaryDirectory() as d:
            apply(d, snapshot("2026-10-28", views(("2026-10-26", 1, 1), ("2026-10-27", 4, 2))))
            apply(d, snapshot("2026-10-29", views(("2026-10-27", 6, 3), ("2026-10-28", 2, 1))))
            got = {r["day"]: r["count"] for r in rows(d, "views.csv")}
            self.assertEqual(got, {"2026-10-26": "1", "2026-10-27": "6", "2026-10-28": "2"})

    def test_download_counts_are_kept_per_snapshot_date(self):
        with tempfile.TemporaryDirectory() as d:
            apply(d, snapshot("2026-10-28", views()))
            later = [{"tag_name": "v1.0.0", "assets": [{"name": "a.dmg", "download_count": 9}]}]
            s = sm.build("2026-10-29", REPO, views(), {"clones": []}, [], [], later)
            apply(d, s)
            dmg = [r for r in rows(d, "downloads.csv") if r["asset"] == "a.dmg"]
            self.assertEqual([r["download_count"] for r in dmg], ["5", "9"])

    def test_release_without_assets_is_skipped(self):
        s = sm.build("2026-10-28", REPO, views(), {"clones": []}, [], [],
                     [{"tag_name": "v0", "assets": []}, {"tag_name": "v1"}])
        self.assertEqual(s["downloads.csv"][1], [])

    def test_main_reads_json_files(self):
        import json
        with tempfile.TemporaryDirectory() as d:
            files = {"repo": REPO, "views": views(("2026-10-27", 4, 2)), "clones": {"clones": []},
                     "referrers": [], "paths": [], "releases": RELEASES}
            argv = ["--out", os.path.join(d, "o"), "--date", "2026-10-28"]
            for n, v in files.items():
                p = os.path.join(d, n + ".json")
                with open(p, "w") as f:
                    json.dump(v, f)
                argv += ["--" + n, p]
            import sys
            old = sys.argv
            sys.argv = ["snapshot-metrics.py"] + argv
            try:
                sm.main()
            finally:
                sys.argv = old
            self.assertEqual(rows(os.path.join(d, "o"), "repo.csv")[0]["stars"], "1")


if __name__ == "__main__":
    unittest.main()
