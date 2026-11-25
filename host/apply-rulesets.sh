#!/usr/bin/env bash
# Apply the bug-fix org's three repository rulesets (CB-455, plan §2).
#
#   host/apply-rulesets.sh            # dry run: prints what it would do
#   host/apply-rulesets.sh --apply    # creates or updates, by ruleset name
#
# Idempotent: a ruleset with the same name is PUT, otherwise POSTed. Needs a
# `gh` login with repository admin. The dry run still reads (the existing
# rulesets and the two bypass apps' ids) and writes nothing.
#
# The Dependabot bypass actor id in rulesets/other-branches-admins-only.json
# (29110) was written from memory, so the script checks it against GitHub
# (`gh api apps/<slug>`) before any write and refuses if it differs. GitHub
# Actions is not a bypass actor: GitHub refuses it on a repository ruleset
# ("Actor GitHub Actions integration must be part of the ruleset source or
# owner organization", HTTP 422, measured 2026-11-24), so the one branch
# Actions pushes, `screenshots`, is excluded from that ruleset instead. REPO and RULESETS_DIR can be
# overridden so the test suite can run this against a stub `gh`.
set -euo pipefail

REPO="${REPO:-Uplift-Foundation/Orbweaver}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIR="${RULESETS_DIR:-$HERE/rulesets}"
APPLY=0
[ "${1:-}" = "--apply" ] && APPLY=1

check_app() {  # slug id
  local got
  got="$(gh api "apps/$1" --jq .id)"
  if [ "$got" != "$2" ]; then
    echo "refusing: bypass app $1 is id $got on GitHub, the JSON says $2" >&2
    exit 1
  fi
}
check_app dependabot 29110

existing="$(gh api "repos/$REPO/rulesets" --jq '.[] | "\(.id)\t\(.name)"')"

for f in "$DIR"/*.json; do
  name="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["name"])' "$f")"
  id="$(printf '%s\n' "$existing" | awk -F'\t' -v n="$name" '$2==n {print $1; exit}')"
  if [ -n "$id" ]; then verb=PUT; path="repos/$REPO/rulesets/$id"; else verb=POST; path="repos/$REPO/rulesets"; fi
  echo "$verb $path  <- $(basename "$f")  ($name)"
  if [ "$APPLY" = 1 ]; then
    gh api -X "$verb" "$path" --input "$f" >/dev/null
  fi
done
[ "$APPLY" = 1 ] || echo "(dry run: nothing written; pass --apply)"
