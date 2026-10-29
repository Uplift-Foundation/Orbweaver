#!/usr/bin/env python3
"""Folds one day's GitHub traffic and release numbers into CSV history.

    python3 tools/snapshot-metrics.py --out DIR --date YYYY-MM-DD \
        --repo repo.json --views views.json --clones clones.json \
        --referrers referrers.json --paths paths.json --releases releases.json

Each input is the raw JSON `gh api` returned (CB-296). GitHub keeps traffic for
only 14 days and a release asset's download_count is a running total with no
history, so the only way to see a trend is to write the numbers down daily.

Re-running the same day, or on overlapping 14-day windows, must not duplicate
rows: views.csv and clones.csv are upserted by their own day, and the
snapshot-style files (repo, referrers, paths, downloads) are upserted by
(snapshot date, key). Pure stdlib so the workflow needs no setup step.
"""
import argparse
import csv
import json
import os


def read_rows(path, header):
    if not os.path.exists(path):
        return []
    with open(path, newline="") as f:
        return list(csv.DictReader(f))


def upsert(path, header, new_rows, key_cols):
    """Merges new_rows into the CSV at path, replacing rows with an equal key."""
    rows = {tuple(r[c] for c in key_cols): r for r in read_rows(path, header)}
    for r in new_rows:
        rows[tuple(str(r[c]) for c in key_cols)] = {c: str(r[c]) for c in header}
    with open(path, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=header, lineterminator="\n")
        w.writeheader()
        for k in sorted(rows):
            w.writerow(rows[k])


def day(ts):
    return ts[:10]


def build(date, repo, views, clones, referrers, paths, releases):
    """Returns {filename: (header, rows, key_cols)} for one snapshot."""
    out = {}
    out["repo.csv"] = (
        ["date", "stars", "forks", "open_issues"],
        [{"date": date, "stars": repo["stargazers_count"],
          "forks": repo["forks_count"], "open_issues": repo["open_issues_count"]}],
        ["date"],
    )
    for name, data in (("views.csv", views), ("clones.csv", clones)):
        out[name] = (
            ["day", "count", "uniques"],
            [{"day": day(v["timestamp"]), "count": v["count"], "uniques": v["uniques"]}
             for v in data.get(name.split(".")[0], [])],
            ["day"],
        )
    out["referrers.csv"] = (
        ["date", "referrer", "count", "uniques"],
        [{"date": date, **r} for r in referrers],
        ["date", "referrer"],
    )
    out["paths.csv"] = (
        ["date", "path", "count", "uniques"],
        [{"date": date, "path": p["path"], "count": p["count"], "uniques": p["uniques"]}
         for p in paths],
        ["date", "path"],
    )
    out["downloads.csv"] = (
        ["date", "tag", "asset", "download_count"],
        [{"date": date, "tag": rel["tag_name"], "asset": a["name"],
          "download_count": a["download_count"]}
         for rel in releases for a in rel.get("assets", [])],
        ["date", "tag", "asset"],
    )
    return out


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--date", required=True)
    for n in ("repo", "views", "clones", "referrers", "paths", "releases"):
        p.add_argument("--" + n, required=True)
    a = p.parse_args()

    def load(path):
        with open(path) as f:
            return json.load(f)

    snap = build(a.date, load(a.repo), load(a.views), load(a.clones),
                 load(a.referrers), load(a.paths), load(a.releases))
    os.makedirs(a.out, exist_ok=True)
    for name, (header, rows, keys) in snap.items():
        upsert(os.path.join(a.out, name), header, rows, keys)


if __name__ == "__main__":
    main()
