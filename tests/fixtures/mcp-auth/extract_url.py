#!/usr/bin/env python3
"""CB-359: read the authorize URL out of a tmux pane that wrapped it.

  extract_url.py PORT < pane.txt

Prints the URL, or nothing and exit 1 when the pane holds none. Claude Code
hard-wraps a long URL onto several indented lines, so `capture-pane -J` alone
does not rejoin it. The URL starts at `http://127.0.0.1:PORT/authorize`, takes
the first space-free token on that line, and continues over each following line
whose stripped text is one space-free token; the first line with a space ends
it (its first token is kept: the URL's last piece, before any prose).
A candidate missing client_id, redirect_uri, state or code_challenge is
rejected: it was cut short, and sending it would give a callback with no state.
"""
import re, sys

port = sys.argv[1]
lines = sys.stdin.read().splitlines()
start = re.compile(r"http://127\.0\.0\.1:%s/authorize" % re.escape(port))
need = ("client_id=", "redirect_uri=", "state=", "code_challenge=")
best = None
for i, line in enumerate(lines):
    m = start.search(line)
    if not m:
        continue
    toks = line[m.start():].split()
    url = toks[0]
    if len(toks) == 1:
        for nxt in lines[i + 1:]:
            s = nxt.strip()
            if not s:
                break
            parts = s.split()
            url += parts[0]
            if len(parts) > 1:
                break
    if all(n in url for n in need):
        best = url  # the last complete one wins (the pane may scroll)
if not best:
    sys.exit(1)
print(best)
