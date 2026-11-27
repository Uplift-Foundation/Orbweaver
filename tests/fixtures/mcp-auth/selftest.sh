#!/bin/bash
# CB-359: check extract_url.py against saved wrapped panes. Not an end-to-end run.
d=$(cd "$(dirname "$0")" && pwd); fail=0
want='http://127.0.0.1:18765/authorize?response_type=code&client_id=dummy-client-a1b2c3&code_challenge=XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX&code_challenge_method=S256&redirect_uri=http%3A%2F%2Flocalhost%3A54321%2Fcallback&state=SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSS&resource=http%3A%2F%2F127.0.0.1%3A18765%2Fmcp'
for f in wrapped-pane wrapped-pane-hardwrap; do
  got=$(python3 "$d/extract_url.py" 18765 < "$d/samples/$f.txt")
  if [ "$got" = "$want" ]; then echo "ok   $f"; else echo "FAIL $f: $got"; fail=1; fi
done
# a URL cut short (no state) and a pane with no URL must be rejected
head -c 150 "$d/samples/wrapped-pane.txt" | python3 "$d/extract_url.py" 18765 >/dev/null && { echo "FAIL empty accepted"; fail=1; } || echo "ok   no-url rejected"
echo 'http://127.0.0.1:18765/authorize?client_id=x&redirect_uri=y' | python3 "$d/extract_url.py" 18765 >/dev/null && { echo "FAIL truncated accepted"; fail=1; } || echo "ok   truncated rejected"
exit $fail
