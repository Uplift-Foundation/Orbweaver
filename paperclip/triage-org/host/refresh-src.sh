#!/bin/sh
# Keeps the triage agents' read-only copy of Orbweaver on origin/develop.
# The agents read this clone with file tools and never run git, so it is
# refreshed from outside, shortly before the weekday triage routine fires.
# The clone carries no credential; the fetch works while the repository is
# public and fails (leaving the last good copy) if it is made private.
set -eu
SRC="$HOME/.paperclip/instances/default/triage/orbweaver-src"
LOG="$HOME/.paperclip/instances/default/triage/refresh-src.log"
{
  date -u +"%Y-%m-%dT%H:%M:%SZ refresh start"
  cd "$SRC"
  if GIT_TERMINAL_PROMPT=0 git -c credential.helper= fetch --quiet --prune origin; then
    git checkout --quiet --force --detach origin/develop
    git clean --quiet -fd -e INDEX-files.txt -e INDEX-symbols.txt
    echo "now at $(git rev-parse --short=12 HEAD)"
    # The agents have no search tool, only Read. Two untracked index files
    # give them something to open: every tracked path, and every type and
    # member declaration in the C# sources as path:line: text.
    git ls-files > INDEX-files.txt
    git grep -nE '^[[:space:]]*(public|internal|private|protected)[^;=]*\b(class|struct|enum|interface|record)[[:space:]]+[A-Za-z_]|^[[:space:]]*(public|internal|private|protected)[^;=]*[A-Za-z_][A-Za-z0-9_<>,]*[[:space:]]*\(' -- '*.cs' > INDEX-symbols.txt || true
    echo "index: $(wc -l < INDEX-files.txt | tr -d ' ') files, $(wc -l < INDEX-symbols.txt | tr -d ' ') declarations"
  else
    echo "fetch failed; left at $(git rev-parse --short=12 HEAD)"
  fi
} >> "$LOG" 2>&1
tail -n 200 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
