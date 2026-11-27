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

# submit logic from capture.sh against a fake prompt in tmux: a prompt that
# submits at once, one that swallows the first Enter, and one that never submits
if command -v tmux >/dev/null; then
  S=cap359selftest; W=$(mktemp -d); export TMUX_TMPDIR=$W; SUBMIT_WAIT=0.3; SUBMIT_SETTLE=1
  pane() { tmux capture-pane -t $S -p -J -S -200; }
  die() { echo "DIED: $1"; return 1; }
  eval "$(sed -n '/^# --- submit logic/,/^# --- end submit logic/p' "$d/capture.sh")"
  cat > $W/tui.sh <<'EOT'
#!/bin/bash
# fake prompt: swallow the first $1 Enters (they stay in the box), then submit
skip=$1; buf=
while true; do
  printf '❯ %s' "$buf"
  IFS= read -r line
  if [ "$skip" -gt 0 ]; then skip=$((skip-1)); buf="$buf$line"; printf '\r'; continue; fi
  buf=; printf '\n⏺ tool call for: %s\n' "$line"
done
EOT
  run() { tmux kill-session -t $S 2>/dev/null; tmux new-session -d -s $S -x 120 -y 40 "bash $W/tui.sh $1"; sleep 1; }
  text='Call the authenticate tool of the MCP server you have and show me the authorization URL.'
  run 0; out=$(submit "$text" 2>&1); echo "$out" | grep -q 'tool-call lines in pane: 1' && ! echo "$out" | grep -q 'one more Enter' && echo "ok   submit first Enter" || { echo "FAIL submit first Enter: $out"; fail=1; }
  run 1; out=$(submit "$text" 2>&1); echo "$out" | grep -q 'one more Enter' && echo "$out" | grep -q 'prompt left the input box' && echo "ok   submit retries one Enter" || { echo "FAIL submit retry: $out"; fail=1; }
  run 9; out=$(submit "$text" 2>&1); echo "$out" | grep -q 'DIED: prompt not submitted' && echo "ok   submit fails loudly" || { echo "FAIL submit never-submits: $out"; fail=1; }
  tmux kill-session -t $S 2>/dev/null; rm -rf $W
else echo "skip submit tests (no tmux)"; fi
exit $fail
