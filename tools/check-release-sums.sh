#!/usr/bin/env bash
# CB-308. Fails unless SHA256SUMS.txt has a line for every installer about to be
# published (*.dmg and *-setup.exe in the given directory).
#
# The in-app updater downloads SHA256SUMS.txt first and refuses any installer it
# has no line for. An asset renamed or added outside the `sha256sum` globs in
# release.yml would therefore publish a release that every installed updater
# silently rejects, and nothing in CI would notice because CI never runs a
# release. This is that notice.
#
# Usage: tools/check-release-sums.sh <artifacts-dir>
set -euo pipefail

dir="${1:?usage: check-release-sums.sh <artifacts-dir>}"
sums="$dir/SHA256SUMS.txt"

if [ ! -f "$sums" ]; then
  echo "::error::$sums does not exist"
  exit 1
fi

seen=0
missing=0
for f in "$dir"/*.dmg "$dir"/*-setup.exe; do
  [ -e "$f" ] || continue
  seen=$((seen + 1))
  name="$(basename "$f")"
  # sha256sum format: "<hash>  <name>" (text) or "<hash> *<name>" (binary).
  if ! awk -v n="$name" '$2 == n || $2 == "*" n { found = 1 } END { exit !found }' "$sums"; then
    echo "::error::SHA256SUMS.txt has no line for $name"
    missing=1
  fi
done

if [ "$seen" -eq 0 ]; then
  echo "::error::no *.dmg or *-setup.exe found in $dir; refusing to publish a release with no installers"
  exit 1
fi

exit "$missing"
